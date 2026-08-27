#!/usr/bin/env python3
"""Probe every target on every authored board against a live Prometheus.

Every target is run TWICE, because `or vector(0)` makes a single check useless in
both directions:

  shipped  the expression exactly as the panel runs it. An expression ending in
           `or vector(0)` ALWAYS returns a row, so this proves only that the panel
           does not ERROR -- never that the metric exists.
  strict   the same expression with the `or vector(0)` fallbacks removed. This is
           what proves the underlying series is real.

Each target lands in one of four states:

  OK        both modes return data. The series exists and the panel renders it.
  FALLBACK  shipped returns data, strict does not. The panel renders (usually 0)
            off its `or vector(0)`, because the series genuinely has not been
            created yet. This is EXPECTED for anything that only appears once
            something goes wrong -- failures, timeouts, cancellations,
            non-determinism -- and it is the correct design: a blank panel reads
            as "broken", a zero reads as "healthy".
  NODATA    neither mode returns data and there is no fallback. Either the stack
            is not in the state this panel needs, or the metric name is WRONG.
            Check it against observability/README.md, "Proving the boards".
  ERROR     Prometheus rejected the query. Always a bug in the expression.

Note that FALLBACK is not a weaker OK. Core creates a metric on FIRST INCREMENT,
so a counter that has never fired is absent from /metrics entirely rather than
reading 0 -- which is exactly why those fallbacks are load-bearing here in a way
they were not in the Go original.

Usage:
  python3 probe-dashboards.py [board ...]
"""

import json
import pathlib
import re
import sys
import urllib.parse
import urllib.request

PROM = "http://localhost:9090"
HERE = pathlib.Path(__file__).resolve().parent
BOARDS = HERE / "dashboards/sandbox"

# Grafana-only macros Prometheus does not understand. $__rate_interval is what
# Grafana computes from the datasource's timeInterval; 1m is a safe stand-in for a
# probe. Template variables are pinned to the values the sandbox actually uses.
SUBS = [
    (r"\$__rate_interval", "1m"),
    (r"\$__interval", "1m"),
    (r"\$__range", "15m"),
    (r"\$namespace", "default"),
    (r"\$task_queue", "repro-task-queue"),
]


def query(expr):
    url = f"{PROM}/api/v1/query?" + urllib.parse.urlencode({"query": expr})
    with urllib.request.urlopen(url, timeout=15) as r:   # noqa: S310
        return json.load(r)


def resolve(expr):
    for pattern, value in SUBS:
        expr = re.sub(pattern, value, expr)
    return expr


def strip_or_vector(expr):
    """Remove trailing `or vector(0)` so a panel cannot pass on its own fallback."""
    return re.sub(r"\s+or\s+vector\(0\)", "", expr)


def targets(board):
    doc = json.loads(board.read_text())
    for panel in doc.get("panels", []):
        for target in panel.get("targets", []):
            yield panel["title"], target["expr"]


def classify(raw):
    """Return (state, detail) for one target."""
    expr = resolve(raw)
    bare = strip_or_vector(expr)

    def usable(result):
        rows = result["data"]["result"]
        return [r for r in rows
                if r.get("value") and r["value"][1] not in ("NaN", "+Inf", "-Inf")]

    try:
        shipped = query(expr)
    except Exception as exc:                              # noqa: BLE001
        return "ERROR", str(exc)
    if shipped.get("status") != "success":
        return "ERROR", shipped.get("error", "")

    shipped_rows = usable(shipped)

    if bare == expr:
        return ("OK", f"{len(shipped_rows)} series") if shipped_rows else ("NODATA", "no series")

    try:
        strict = query(bare)
    except Exception as exc:                              # noqa: BLE001
        return "ERROR", str(exc)
    if strict.get("status") != "success":
        return "ERROR", strict.get("error", "")

    if usable(strict):
        return "OK", f"{len(usable(strict))} series"
    if shipped_rows:
        return "FALLBACK", "renders via `or vector(0)`; series not created yet"
    return "NODATA", "no series and no fallback"


def main():
    wanted = [a for a in sys.argv[1:] if not a.startswith("-")]

    files = sorted(BOARDS.glob("*.json"))
    if wanted:
        files = [f for f in files if f.stem in wanted]

    tally = {"OK": 0, "FALLBACK": 0, "NODATA": 0, "ERROR": 0}
    for board in files:
        print(f"\n=== {board.stem} ===")
        for title, raw in targets(board):
            state, detail = classify(raw)
            tally[state] += 1
            if state == "OK":
                print(f"  OK        {title}  ({detail})")
            else:
                print(f"  {state:9s} {title}  [{detail}]")
                if state in ("NODATA", "ERROR"):
                    print(f"            {resolve(raw)[:150]}")

    total = sum(tally.values())
    print(f"\n{tally['OK']} OK  +  {tally['FALLBACK']} FALLBACK  "
          f"=  {tally['OK'] + tally['FALLBACK']}/{total} targets render")
    print(f"{tally['NODATA']} NODATA, {tally['ERROR']} ERROR")
    return 1 if tally["ERROR"] else 0


if __name__ == "__main__":
    sys.exit(main())
