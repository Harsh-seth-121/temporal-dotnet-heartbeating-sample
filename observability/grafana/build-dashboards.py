#!/usr/bin/env python3
"""Generate the hand-authored sandbox dashboards.

Run from anywhere; the output path is resolved against this file, not the cwd.

The output JSON is committed, so you do not need to run this to use the stack.
Re-run it after editing a panel spec below, then let Grafana's provisioner pick
the change up (it rescans every 10s).

Every PromQL expression here was probed against a live stack before being
included -- a panel that cannot return data does not belong on a dashboard,
because next time you cannot tell "no data" from "nothing is wrong".
The probe script and the stack states each board needs are in
docs/DASHBOARDS.md under "Proving the boards".

--------------------------------------------------------------------------
WHAT CHANGED FROM THE GO ORIGINAL, AND WHY
--------------------------------------------------------------------------
The .NET SDK is Rust-Core-based and ships its own Prometheus exporter, so the
metric vocabulary is not Go's tally vocabulary. Left at Core defaults
(PrometheusOptions.HasCounterTotalSuffix / HasUnitSuffix / UseSecondsForDuration
all false), which is what temporalio/dashboards' Core board is written against
and what every other .NET user sees:

* Counters have NO `_total` suffix.
      temporal_request_total          -> temporal_request
      temporal_sticky_cache_hit_total -> temporal_sticky_cache_hit

* Histograms have NO `_seconds` infix and record INTEGER MILLISECONDS.
      temporal_request_latency_seconds_bucket -> temporal_request_latency_bucket
  Every SDK latency panel below therefore carries unit "ms", not "s".
  SERVER panels keep unit "s": the server is still the same Go binary emitting
  tally metrics, and tally still reports seconds. TWO panels put both sides on
  one axis, and both multiply the server series by 1000:
      Signals    "Schedule-to-start: SDK view vs server view"
      Heartbeat  "Activity schedule-to-start p95, SDK vs server"
  Getting that conversion wrong yields a 1000x gap that looks exactly like the
  dispatch loss those panels exist to detect. Both also pin the server side to
  ONE task_type: task_schedule_to_start_latency is a single histogram covering
  task_type="Workflow" and task_type="Activity", so summing over it compares a
  workflow-task SDK series against a workflow+activity server series and calls
  the difference divergence.
  Corollary you cannot escape: integer ms means sub-millisecond durations round
  to 0, and no bucket override recovers them. Fixing that needs
  UseSecondsForDuration=true, which blanks every panel on the imported
  core-sdk-otel board. You get one or the other.

* Label VALUES are not sanitized by Core. `heartbeat-task-queue` stays
  `task_queue="heartbeat-task-queue"`. The SERVER still sanitizes (tally's
  ValueCharacters: alphanumeric + underscore), so the same queue is
  `taskqueue="heartbeat_task_queue"` on server metrics. One queue, two
  spellings, no way to join them. Metric names and label KEYS are still
  sanitized on both sides.

* `temporal_request_attempt` exists in NO Core SDK. The Go board's "gRPC
  attempts per logical call" panel has no equivalent, so it was REPLACED with
  poll outcomes, not faked. See the worker board.

* `temporal_sticky_cache_miss` DOES exist in Core (it does not in Go+tally), so
  the Signals board computes a REAL hit ratio instead of the "hits per workflow
  task" approximation the Go original had to ship with an apology attached.
  This is the one place the port is strictly better than the original.

* Core attaches `service_name="temporal-core-sdk"` to every metric
  (TelemetryOptions.attach_service_name, default true). This matters far more
  than it looks. With no `_total` suffix, the worker's series names are now
  IDENTICAL to the ones the server's own embedded Go SDK workers emit on :8000
  -- a collision the Go original was structurally incapable of having. `sdk()`
  below pins service_name on every SDK selector so a board cannot silently pick
  up the server's internal system workers.

* Core creates a metric on FIRST INCREMENT. A counter that has never fired is
  absent from /metrics entirely rather than reading 0 -- including counters that
  exist only under a label value nothing has produced yet, e.g.
  repro_activity_started{resumed="true"} with fault.failureRate at 0. PromQL
  turns absence into silence, not into zero: `A + B` and `A / B` with either
  operand empty yield EMPTY. So every operand of every arithmetic expression
  below that can be absent carries `or vector(0)`, and the probe script runs
  each target twice (with and without the fallbacks) so a panel cannot pass on
  its fallback alone. Standalone targets do NOT get the fallback: `sum by (x)
  (...) or vector(0)` returns a series with no x, which renders as a blank
  legend and cannot join anything.

* Core's default histogram buckets are coarse for a laptop sandbox. Five of the
  latency panels here require PrometheusOptions.HistogramBucketOverrides in the
  worker/loadgen runtime config; without them they do not read "no data", they
  read a plausible CONSTANT, which is worse. Each affected panel says so in its
  description. The exact override block is explained in docs/GOTCHAS.md.
"""

import json
import pathlib

DS = {"type": "prometheus", "uid": "sandbox-prometheus"}
# Anchored to this file: docs/DASHBOARDS.md invokes it as grafana/build-dashboards.py
# from observability/, and the repo root is now the default cwd, so a cwd-relative
# path would scatter the JSON into whichever directory you happened to be in.
OUT = pathlib.Path(__file__).resolve().parent / "dashboards/sandbox"

# ---------------------------------------------------------------------------
# APPLICATION INPUTS. These four strings must match src/ exactly. A typo here
# produces a silently empty panel, never an error -- which is why they are
# centralized instead of inlined into 80 expressions.
# ---------------------------------------------------------------------------
CUSTOM = "repro_"              # prefix on this repo's own metrics. NOT applied by
                               # MetricPrefix -- Core does not prefix custom names,
                               # so this is a literal part of the metric name.
TASK_QUEUE = "repro-task-queue"   # for docs/legends only; panels group by label.
                                      # The dash is deliberate: see the sanitization
                                      # note in the docstring.
WORKFLOW_TYPE = "HeartbeatWorkflow"
ACTIVITY_TYPE = "ProcessBatch"        # [Activity] on ProcessBatchAsync TRIMS "Async"

# Units: s=seconds, ms=milliseconds, percent=0-100, percentunit=0-1,
# short=plain number, reqps=rate.
SEC, MS, PCT, RATIO, NUM, RPS = "s", "ms", "percent", "percentunit", "short", "reqps"


def target(expr, legend=None, ref="A"):
    t = {"refId": ref, "expr": expr, "datasource": DS}
    if legend:
        t["legendFormat"] = legend
    return t


def panel(pid, title, exprs, unit=NUM, kind="timeseries", w=12, h=8, x=0, y=0,
          desc="", stack=False, minval=None):
    """exprs: list of (expr, legendFormat)."""
    targets = [target(e, l, ref=chr(ord("A") + i)) for i, (e, l) in enumerate(exprs)]
    defaults = {"unit": unit, "custom": {}}
    if minval is not None:
        defaults["min"] = minval
    if kind == "timeseries":
        defaults["custom"] = {
            "drawStyle": "line",
            "lineWidth": 1,
            "fillOpacity": 10 if not stack else 40,
            "showPoints": "never",
            "stacking": {"mode": "normal" if stack else "none", "group": "A"},
        }
    p = {
        "id": pid,
        "type": kind,
        "title": title,
        "description": desc,
        "datasource": DS,
        "gridPos": {"h": h, "w": w, "x": x, "y": y},
        "targets": targets,
        "fieldConfig": {"defaults": defaults, "overrides": []},
    }
    if kind == "timeseries":
        p["options"] = {
            "legend": {"displayMode": "table", "placement": "bottom",
                       "calcs": ["lastNotNull", "max"], "showLegend": True},
            "tooltip": {"mode": "multi", "sort": "desc"},
        }
    else:  # stat
        p["options"] = {
            "reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": False},
            "textMode": "auto",
            "colorMode": "value",
            "graphMode": "area",
        }
    return p


def dashboard(uid, title, desc, panels, tags, variables=("namespace",)):
    tmpl = []
    if "namespace" in variables:
        tmpl.append({
            "name": "namespace",
            "label": "SDK namespace",
            "type": "query",
            "datasource": DS,
            # temporal_request, not temporal_request_total: Core does not suffix
            # counters. This query is also the cheapest way to prove the worker's
            # exporter is being scraped at all -- if the dropdown is empty, the
            # Prometheus target is down, not the dashboard.
            # The service_name pin is the same pin sdk() applies, and for the same
            # reason -- this selector just cannot call sdk(), which would pin
            # namespace="$namespace" to the variable being defined. Unpinned, this
            # dropdown lists the SERVER's internal namespaces too (_unknown_,
            # system, temporal_system), because the server's own embedded Go SDK
            # workers emit a metric with the byte-identical name on :8000. Pick one
            # of those and every panel on the board goes blank at once.
            "query": 'label_values(temporal_request{service_name="temporal-core-sdk"}, namespace)',
            "refresh": 1,
            "includeAll": False,
            "multi": False,
            "current": {"text": "default", "value": "default", "selected": True},
            # Core does NOT sanitize label values (Go+tally did). A namespace or
            # task queue with a dash keeps its dash here. The SERVER still
            # sanitizes, so the same name can be spelled two ways in one TSDB.
            "description": "Values are NOT sanitized by the .NET SDK; dashes survive.",
        })
    if "task_queue" in variables:
        tmpl.append({
            "name": "task_queue",
            "label": "Task queue",
            "type": "query",
            "datasource": DS,
            "query": "label_values(temporal_worker_task_slots_available, task_queue)",
            "refresh": 1,
            "includeAll": True,
            "multi": True,
            "current": {"text": "All", "value": "$__all", "selected": True},
        })
    return {
        "uid": uid,
        "title": title,
        "description": desc,
        "tags": list(tags),
        "schemaVersion": 39,
        "version": 1,
        "editable": True,
        "graphTooltip": 1,  # shared crosshair
        "time": {"from": "now-30m", "to": "now"},
        "refresh": "10s",
        "timezone": "browser",
        "templating": {"list": tmpl},
        "panels": panels,
    }


def grid(specs):
    """Lay panels out left to right, wrapping at 24 columns, assigning ids and
    positions.

    The Go original placed panels by index parity (x = 0 if i is even else 12),
    which is only correct when every row is exactly two w=12 panels. It silently
    misplaced the three w=8 stat panels at the top of the worker board -- Grafana's
    layout engine floated them back into place, so it looked fine and was not.
    The heartbeat board has two w=8 triplets and two w=24 panels, so pack the row
    properly instead.
    """
    out, pid, x, y, rowh = [], 1, 0, 0, 0
    for s in specs:
        w = s.pop("w", 12)
        h = s.get("h", 8)
        if x + w > 24:
            y += rowh
            x, rowh = 0, 0
        out.append(panel(pid, x=x, y=y, w=w, **s))
        x += w
        rowh = max(rowh, h)
        pid += 1
    return out


def sdk(*extra):
    """Label selector for SDK (Core) metrics.

    service_name is NOT decoration. With Core's default naming the worker's series
    names are byte-identical to the ones the server's own embedded Go SDK workers
    emit on :8000. namespace alone usually separates them (system workers live in
    `temporal-system`), but not always, and "usually" is not a property you want in
    a debugging tool. Core sets service_name="temporal-core-sdk" on EVERY metric it
    emits, including custom ones, via TelemetryOptions.attach_service_name.
    """
    return "{" + ",".join(('namespace="$namespace"',
                           'service_name="temporal-core-sdk"') + extra) + "}"


def srv(*extra):
    """Label selector for server metrics.

    The namespace is pinned literally, not to $namespace, for two reasons: the
    $namespace variable is populated from SDK series and server label VALUES are
    sanitized while SDK ones are not, so the two vocabularies are not
    interchangeable; and this sandbox only ever runs one namespace.
    """
    return "{" + ",".join(('namespace="default"',) + extra) + "}"


SDK = sdk()          # {namespace="$namespace",service_name="temporal-core-sdk"}
SRV = srv()          # {namespace="default"}
FE = '{service_name="frontend"}'
HI = '{service_name="history"}'
# task_schedule_to_start_latency is ONE histogram split by task_type, so a panel
# comparing it against an SDK series has to pin the half that SDK series measures.
# Summing both halves and calling the difference "divergence" compares apples to
# apples plus oranges.
HI_WFT = '{service_name="history",task_type="Workflow"}'
HI_ACT = '{service_name="history",task_type="Activity"}'
HB = '{operation="RecordActivityTaskHeartbeat"}'   # documentation only; see sdk(...)


# ---------------------------------------------------------------- worker board
worker = grid([
    dict(title="Workflow completions /s", unit=RPS, h=6, w=8,
         desc="SDK view. Should track the server's workflow_success rate on the "
              "Server board. Counter names lost their _total suffix in the port: "
              "Core's exporter defaults HasCounterTotalSuffix to false.",
         exprs=[('sum by (workflow_type) (rate(temporal_workflow_completed%s[$__rate_interval]))' % SDK, "{{workflow_type}} completed"),
                ('sum by (workflow_type) (rate(temporal_workflow_failed%s[$__rate_interval])) or vector(0)' % SDK, "{{workflow_type}} failed")]),
    dict(title="Free workflow task slots", unit=NUM, h=6, w=8, kind="stat",
         desc="Hits 0 when the worker is saturated -- the first thing to check "
              "when schedule-to-start latency climbs. worker_type is PascalCase in "
              "Core: WorkflowWorker, ActivityWorker, LocalActivityWorker, NexusWorker.",
         exprs=[('min by (worker_type) (temporal_worker_task_slots_available%s)' % SDK, "{{worker_type}}")]),
    dict(title="Live pollers", unit=NUM, h=6, w=8, kind="stat",
         desc="Zero here with a queued workflow means no worker is polling. "
              "poller_type values in Core are workflow_task, sticky_workflow_task, "
              "activity_task, nexus_task. Note 'sticky_workflow_task' -- Go spells "
              "it 'workflow_sticky_task' and docs.temporal.io documents the Go "
              "spelling, which is wrong for every Core SDK including .NET.",
         exprs=[('sum by (poller_type) (temporal_num_pollers%s)' % SDK, "{{poller_type}}")]),

    dict(title="Workflow task schedule-to-start p99", unit=MS, minval=0,
         desc="Time a workflow task waited for a free worker. The single most "
              "useful saturation signal.\n\n"
              "REQUIRES a HistogramBucketOverrides entry. Core's default buckets "
              "for this metric are [100, 500, 1000, 5000, 10000, 100000, 1000000] "
              "ms; a healthy sandbox sits at 1-15 ms, so every observation lands "
              "in the first bucket and histogram_quantile interpolates to a flat "
              "~99 ms forever. That reads as a real number, not as no-data, which "
              "is the worst kind of broken panel.",
         exprs=[('histogram_quantile(0.99, sum by (le, task_queue) (rate(temporal_workflow_task_schedule_to_start_latency_bucket%s[$__rate_interval])))' % SDK, "{{task_queue}}")]),
    dict(title="Activity schedule-to-start p99", unit=MS, minval=0,
         desc="Same metric family and the same default-bucket problem as the "
              "panel to its left. Needs the same override.",
         exprs=[('histogram_quantile(0.99, sum by (le, task_queue) (rate(temporal_activity_schedule_to_start_latency_bucket%s[$__rate_interval])))' % SDK, "{{task_queue}}")]),

    dict(title="Workflow task execution latency", unit=MS, minval=0,
         desc="How long the worker spent running workflow code. One of the few "
              "latency panels that needs NO bucket override: Core's defaults here "
              "are [1, 10, 20, 50, 100, 200, 500, 1000] ms, which resolves a "
              "laptop workload properly.",
         exprs=[('histogram_quantile(0.50, sum by (le) (rate(temporal_workflow_task_execution_latency_bucket%s[$__rate_interval])))' % SDK, "p50"),
                ('histogram_quantile(0.95, sum by (le) (rate(temporal_workflow_task_execution_latency_bucket%s[$__rate_interval])))' % SDK, "p95"),
                ('histogram_quantile(0.99, sum by (le) (rate(temporal_workflow_task_execution_latency_bucket%s[$__rate_interval])))' % SDK, "p99")]),
    dict(title="Activity execution latency p95", unit=MS, minval=0,
         desc="Raise fault.latency in config.yaml and watch this move.\n\n"
              "REQUIRES a HistogramBucketOverrides entry. Core's default top "
              "bucket for this metric is 60 s, and the seed heartbeating activity "
              "is designed to run longer than that -- without an override every "
              "long attempt falls into +Inf and p95 is unresolvable.",
         exprs=[('histogram_quantile(0.95, sum by (le, activity_type) (rate(temporal_activity_execution_latency_bucket%s[$__rate_interval])))' % SDK, "{{activity_type}}")]),

    dict(title="Workflow end-to-end latency p95", unit=MS, minval=0,
         desc="No override needed: Core's defaults for this metric span 100 ms to "
              "24 hours, with points at 30 s, 60 s and 120 s that bracket the seed "
              "workflow nicely.",
         exprs=[('histogram_quantile(0.95, sum by (le, workflow_type) (rate(temporal_workflow_endtoend_latency_bucket%s[$__rate_interval])))' % SDK, "{{workflow_type}}")]),
    dict(title="Task slot saturation", unit=PCT, minval=0,
         desc="used / (used + available). Sustained 100% means add slots or workers.",
         # No `or vector(0)` on either operand, deliberately, and this is the one
         # ratio on any of these boards that does not need one: the slot gauges are
         # registered when the worker starts, not on first use, so every worker_type
         # is present reading 0 from the first scrape (LocalActivityWorker proves it
         # -- this repo never runs one). A fallback would also be actively wrong
         # here: `sum by (worker_type) (...) or vector(0)` yields a series with NO
         # worker_type, which cannot match the other side of the division.
         exprs=[('100 * sum by (worker_type) (temporal_worker_task_slots_used%s) / clamp_min(sum by (worker_type) (temporal_worker_task_slots_used%s) + sum by (worker_type) (temporal_worker_task_slots_available%s), 1)' % (SDK, SDK, SDK), "{{worker_type}}")]),

    dict(title="Client RPC rate by operation", unit=RPS,
         desc="Client-side gRPC calls. Emitted by worker, loadgen, and (via "
              "Pushgateway) the one-shot starter.\n\n"
              "Two families, not one. Core splits long-poll RPCs "
              "(PollWorkflowTaskQueue, PollActivityTaskQueue, and the blocking "
              "GetWorkflowExecutionHistory the starter uses to await a result) "
              "into temporal_long_request. Go+tally counted them all in "
              "temporal_request_total, so a Go reader saw polls on this panel and "
              "a .NET reader would not. Both are plotted.",
         exprs=[('sum by (operation) (rate(temporal_request%s[$__rate_interval]))' % SDK, "{{operation}}"),
                ('sum by (operation) (rate(temporal_long_request%s[$__rate_interval])) or vector(0)' % SDK, "{{operation}} (long)")]),
    dict(title="Client RPC latency p95", unit=MS, minval=0,
         desc="Idle operations read NaN -- no observations in the rate window. "
              "That is normal, not a fault.\n\n"
              "REQUIRES a HistogramBucketOverrides entry. Core's default first "
              "bucket is 50 ms and loopback gRPC is 0-5 ms, so p95 pins at a flat "
              "~47 ms. Also note Core records INTEGER milliseconds: a 400us call "
              "is recorded as 0. No bucket set fixes that; only "
              "UseSecondsForDuration=true does, and that blanks the imported "
              "core-sdk-otel board.",
         exprs=[('histogram_quantile(0.95, sum by (le, operation) (rate(temporal_request_latency_bucket%s[$__rate_interval])))' % SDK, "{{operation}}")]),

    dict(title="Client RPC failures", unit=RPS, stack=True,
         desc="Empty when healthy. Populates on server errors or throttling.\n\n"
              "status_code is SCREAMING_SNAKE in Core -- NOT_FOUND, "
              "DEADLINE_EXCEEDED, RESOURCE_EXHAUSTED, UNAVAILABLE -- plus one "
              "synthetic value, TRANSPORT_ERROR, which is not a gRPC status at "
              "all: Core emits it when the connection itself failed before a "
              "status came back. If you copied a selector from a Go dashboard or "
              "from docs that say status_code=\"not_found\", it will match nothing.",
         exprs=[('sum by (operation, status_code) (rate(temporal_request_failure%s[$__rate_interval])) or vector(0)' % SDK, "{{operation}} {{status_code}}"),
                ('sum by (operation, status_code) (rate(temporal_long_request_failure%s[$__rate_interval])) or vector(0)' % SDK, "{{operation}} {{status_code}} (long)")]),
    dict(title="Poll outcomes", unit=RPS,
         desc="REPLACES the Go board's \"gRPC attempts per logical call\". That "
              "panel read temporal_request_attempt_total, which exists in NO Core "
              "SDK -- not .NET, not Python, not TypeScript, not Rust. Rather than "
              "ship a permanently blank panel, this answers the same question "
              "(is the transport layer doing useful work or spinning) from metrics "
              "Core actually has.\n\n"
              "succeed vs empty is poll efficiency. All-empty with a queued "
              "workflow means a task queue name mismatch. temporal_activity_task_"
              "received also carries Core's `eager` label, which Go has no "
              "equivalent for -- eager=\"true\" means the activity was handed back "
              "on the workflow task response and never touched matching.",
         exprs=[('sum by (task_queue) (rate(temporal_workflow_task_queue_poll_succeed%s[$__rate_interval])) or vector(0)' % SDK, "{{task_queue}} WFT poll succeed"),
                ('sum by (task_queue) (rate(temporal_workflow_task_queue_poll_empty%s[$__rate_interval])) or vector(0)' % SDK, "{{task_queue}} WFT poll empty"),
                ('sum by (task_queue, eager) (rate(temporal_activity_task_received%s[$__rate_interval])) or vector(0)' % SDK, "{{task_queue}} activity received eager={{eager}}"),
                ('sum by (task_queue) (rate(temporal_activity_poll_no_task%s[$__rate_interval])) or vector(0)' % SDK, "{{task_queue}} activity poll empty")]),
])

# ---------------------------------------------------------------- server board
# Every expression on this board ports from the Go original BYTE FOR BYTE. The
# server is the same Go binary emitting the same tally metrics: no _total
# suffixes, timers in SECONDS, label values sanitized. All 18 metric names were
# re-verified against temporalio/temporal v1.31.2 common/metrics/metric_defs.go.
# Only the descriptions changed, where they now need a .NET contrast.
server = grid([
    dict(title="Frontend availability", unit=PCT, h=6, w=8, kind="stat",
         desc="'or vector(0)' matters: without it this panel goes blank when "
              "there are no errors, which reads as broken rather than healthy.",
         exprs=[('100 * (1 - (sum(rate(service_errors%s[$__rate_interval])) or vector(0)) / clamp_min(sum(rate(service_requests%s[$__rate_interval])), 1e-9))' % (FE, FE), "availability")]),
    dict(title="State transitions /s", unit=RPS, h=6, w=8, kind="stat",
         desc="Mutable-state writes. Uses _count, never _sum: the metric records "
              "each workflow's cumulative value on close, so _sum is meaningless.",
         exprs=[('sum(rate(state_transition_count_count[$__rate_interval]))', "transitions/s")]),
    dict(title="Sync-match ratio", unit=RATIO, h=6, w=8, kind="stat",
         desc="1.0 means tasks handed straight to a waiting poller and never "
              "touched the database. Drops when workers fall behind.",
         # Numerator guarded for the same reason the denominator is clamped: a
         # server that has only ever matched through the database emits no
         # poll_success_sync at all, and empty / anything is EMPTY, not 0.
         exprs=[('(sum(rate(poll_success_sync[$__rate_interval])) or vector(0)) / clamp_min(sum(rate(poll_success[$__rate_interval])), 1e-9)', "sync-match")]),

    dict(title="Frontend RPS by operation", unit=RPS,
         exprs=[('sum by (operation) (rate(service_requests%s[$__rate_interval]))' % FE, "{{operation}}")]),
    dict(title="Frontend latency p95 by operation", unit=SEC, minval=0,
         desc="Server histograms are in SECONDS. The SDK's are in milliseconds -- "
              "do not put these two on one axis without converting.\n\n"
              "Server counters carry no _total suffix because tally emits classic "
              "text format, not OpenMetrics. The SDK's counters also carry no "
              "_total suffix, but for a completely unrelated reason: Core's "
              "HasCounterTotalSuffix defaults to false. Same shape, two causes; "
              "flipping the SDK flag would not change anything on this board.",
         exprs=[('histogram_quantile(0.95, sum by (operation, le) (rate(service_latency_bucket%s[$__rate_interval])))' % FE, "{{operation}}")]),

    dict(title="Persistence latency p95 by operation", unit=SEC, minval=0,
         desc="PostgreSQL here. Do not extrapolate these numbers to Cassandra.",
         exprs=[('histogram_quantile(0.95, sum by (operation, le) (rate(persistence_latency_bucket%s[$__rate_interval])))' % HI, "{{operation}}")]),
    dict(title="Persistence requests /s", unit=RPS,
         exprs=[('sum by (operation) (rate(persistence_requests%s[$__rate_interval]))' % HI, "{{operation}}")]),

    dict(title="Server-side schedule-to-start p95", unit=SEC, minval=0,
         desc="History service's own view, in seconds. Broken out by task_type "
              "because this is ONE histogram covering both Workflow and Activity "
              "tasks -- the SDK has a separate metric per type. Compare with the "
              "SDK's view on the Signals board, which converts this series to "
              "milliseconds AND pins task_type=\"Workflow\" so the two sides "
              "measure the same thing. Sustained divergence means clock skew or "
              "dispatch loss.",
         exprs=[('histogram_quantile(0.95, sum by (task_type, le) (rate(task_schedule_to_start_latency_bucket%s[$__rate_interval])))' % HI, "{{task_type}}")]),
    dict(title="Workflow outcomes, server view", unit=RPS,
         desc="Server label keys differ from SDK ones: workflowType (camelCase) "
              "here, workflow_type there. That gap widened in the .NET port -- the "
              "SDK side is now unambiguously snake_case because Core does no "
              "name mangling at all.",
         exprs=[('sum by (workflowType) (rate(workflow_success%s[$__rate_interval]))' % SRV, "{{workflowType}} success"),
                ('sum by (workflowType) (rate(workflow_failed%s[$__rate_interval])) or vector(0)' % SRV, "{{workflowType}} failed"),
                ('sum by (workflowType) (rate(workflow_timeout%s[$__rate_interval])) or vector(0)' % SRV, "{{workflowType}} timeout")]),

    dict(title="Task queue backlog", unit=NUM, minval=0,
         desc="Backlog count and age. Two label traps in one panel.\n\n"
              "KEY: it is 'taskqueue' on the server, without the underscore the "
              "SDK uses.\n"
              "VALUE: the server SANITIZES tag values (tally replaces anything "
              "non-alphanumeric with an underscore) and Core does NOT. A queue "
              "named heartbeat-task-queue is taskqueue=\"heartbeat_task_queue\" "
              "here and task_queue=\"heartbeat-task-queue\" on every SDK board. "
              "Same queue, two spellings, no join.",
         exprs=[('sum by (taskqueue, task_type) (approximate_backlog_count)', "{{taskqueue}} {{task_type}} count"),
                ('max by (taskqueue, task_type) (approximate_backlog_age_seconds)', "{{taskqueue}} {{task_type}} age (s)")]),
    dict(title="Tasks arriving with no poller /s", unit=RPS,
         desc="Non-zero means tasks landed with nobody polling -- a stopped "
              "worker, or a task queue name mismatch.",
         exprs=[('sum by (taskqueue) (rate(no_poller_tasks[$__rate_interval])) or vector(0)', "{{taskqueue}}")]),

    dict(title="Errors by type, all roles", unit=RPS, stack=True,
         desc="Empty when healthy. All server roles share one process on this "
              "deployment, so service_name distinguishes them: frontend, history, "
              "matching, worker.\n\n"
              "Careful: service_name is no longer server-only. Core attaches "
              "service_name=\"temporal-core-sdk\" to every SDK metric. Different "
              "metric families, so nothing collides here, but do not read "
              "service_name as 'server role' the way you could in the Go repo.",
         exprs=[('sum by (service_name, error_type) (rate(service_error_with_type[$__rate_interval])) or vector(0)', "{{service_name}} {{error_type}}")]),
    dict(title="History cache lock contention p99", unit=SEC, minval=0,
         desc="HistoryCacheGetOrCreate latency. Climbs when many workers "
              "contend on the same workflow.",
         exprs=[('histogram_quantile(0.99, sum by (le) (rate(cache_latency_bucket{operation="HistoryCacheGetOrCreate"}[$__rate_interval])))', "p99")]),
])

# --------------------------------------------------------------- signals board
signals = grid([
    dict(title="Non-determinism failures (dashboard range)", unit=NUM, h=6, w=8, kind="stat",
         desc="The number to look at first when a repro involves changed "
              "workflow code. Uses increase() over the range, not rate(), so "
              "short runs still register. failure_reason=\"NonDeterminismError\" "
              "is the identical string in Core and Go -- verified in sdk-core's "
              "FailureReason Display impl, not assumed.",
         exprs=[('sum(increase(temporal_workflow_task_execution_failed%s[$__range])) or vector(0)' % sdk('failure_reason="NonDeterminismError"'), "non-determinism")]),
    dict(title="Server-side non-determinism /s", unit=RPS, h=6, w=8, kind="stat",
         desc="The server's independent count. Should move with the SDK's.",
         exprs=[('sum(rate(service_errors_nondeterministic[$__rate_interval])) or vector(0)', "server view")]),
    dict(title="Workflow task attempt p99", unit=NUM, h=6, w=8, kind="stat",
         desc="Above 1 means workflow tasks are being retried -- a stuck "
              "workflow rather than a slow one.",
         exprs=[('histogram_quantile(0.99, sum by (le) (rate(workflow_task_attempt_bucket[$__rate_interval])))', "p99 attempt")]),

    dict(title="Schedule-to-start: SDK view vs server view", unit=MS, minval=0, w=24,
         desc="Two independent measurements of the SAME quantity: how long a "
              "WORKFLOW task sat between being scheduled and being started. They "
              "should track each other closely; sustained divergence means clock "
              "skew or dispatch loss.\n\n"
              "THE * 1000 IS NOT DECORATION. Core records milliseconds; the "
              "server's tally timers record seconds. Plotting them raw puts a "
              "1000x gap on the panel that looks exactly like the dispatch loss "
              "this panel exists to detect. The Go original needed no conversion "
              "because both sides were seconds.\n\n"
              "THE task_type PIN IS NOT DECORATION EITHER. The SDK metric is "
              "workflow-task-only by construction (activities have their own "
              "temporal_activity_schedule_to_start_latency), but the server's "
              "task_schedule_to_start_latency_bucket is one histogram carrying "
              "both task_type=\"Workflow\" and task_type=\"Activity\". Sum over "
              "it and the two lines are not the same quantity, so every gap "
              "between them reads as divergence when it is really just the "
              "activity half of a different metric.",
         exprs=[('histogram_quantile(0.95, sum by (le) (rate(temporal_workflow_task_schedule_to_start_latency_bucket%s[$__rate_interval])))' % SDK, "SDK (worker), ms"),
                ('1000 * histogram_quantile(0.95, sum by (le) (rate(task_schedule_to_start_latency_bucket%s[$__rate_interval])))' % HI_WFT, "SERVER (history), s -> ms")]),

    dict(title="Workflow task failures by reason", unit=RPS, stack=True,
         desc="Core's failure_reason vocabulary, read from the source rather than "
              "the docs: NonDeterminismError, WorkflowError, ActivityError, "
              "timeout, GrpcMessageTooLarge, PayloadsTooLarge, "
              "ExternalStorageError, RequestTooLarge, plus operation_<name> and "
              "handler_error_<name> for Nexus.",
         exprs=[('sum by (failure_reason) (rate(temporal_workflow_task_execution_failed%s[$__rate_interval])) or vector(0)' % SDK, "{{failure_reason}}")]),
    dict(title="Replay latency p95", unit=MS, minval=0,
         desc="Time spent replaying history to rebuild workflow state. Grows "
              "with history length when the sticky cache misses. Shares Core's "
              "[1..1000] ms bucket set with workflow task execution latency, so "
              "no override is needed.",
         exprs=[('histogram_quantile(0.95, sum by (le) (rate(temporal_workflow_task_replay_latency_bucket%s[$__rate_interval])))' % SDK, "p95")]),

    dict(title="Sticky cache hit ratio", unit=RATIO, minval=0,
         desc="A REAL hit ratio, and the clearest win in the whole Go -> .NET "
              "port. Go+tally emits no sticky_cache_miss metric, so the original "
              "board could only ship an approximation (hits divided by workflow "
              "task executions) with a paragraph apologising for it. Core emits "
              "temporal_sticky_cache_miss, so this is hit / (hit + miss) -- the "
              "actual quantity.\n\n"
              "Falling toward 0 means workers keep rebuilding state from history "
              "instead of resuming from cache. Look at forced evictions and "
              "MaxCachedWorkflows next.",
         # Every `or vector(0)` here is load-bearing, not decorative. Core creates
         # a metric on first increment, so on a worker that has never had a cache
         # miss temporal_sticky_cache_miss does not exist at all -- and in PromQL
         # `A + B` where B is empty yields EMPTY, not A. Without the fallback the
         # whole ratio silently vanishes on exactly the healthy worker you would
         # expect to read 1.0. The HIT term needs it just as much and in both
         # positions: a worker whose cache has only ever missed (cold start, or
         # MaxCachedWorkflows=0) has no hit series, and an empty NUMERATOR empties
         # the division too -- blanking the panel on the one worker whose 0.0 you
         # most wanted to see.
         exprs=[('(sum(rate(temporal_sticky_cache_hit%s[$__rate_interval])) or vector(0)) / clamp_min((sum(rate(temporal_sticky_cache_hit%s[$__rate_interval])) or vector(0)) + (sum(rate(temporal_sticky_cache_miss%s[$__rate_interval])) or vector(0)), 1e-9)' % (SDK, SDK, SDK), "hit ratio")]),
    dict(title="Sticky cache hits and misses /s", unit=RPS,
         desc="The two series behind the ratio. A miss is a workflow task that "
              "arrived for a workflow this worker no longer had cached -- it had "
              "to fetch and replay history to answer it.",
         exprs=[('sum(rate(temporal_sticky_cache_hit%s[$__rate_interval])) or vector(0)' % SDK, "hits/s"),
                ('sum(rate(temporal_sticky_cache_miss%s[$__rate_interval])) or vector(0)' % SDK, "misses/s")]),

    dict(title="Cached workflows and forced evictions", unit=NUM, minval=0,
         desc="Forced evictions are the leading indicator of replay storms. "
              "Note temporal_sticky_cache_total_forced_eviction keeps its "
              "'total' -- that is part of the metric NAME in Core, not the "
              "_total counter suffix, so it survives the suffix change.",
         exprs=[('sum(temporal_sticky_cache_size%s)' % SDK, "cached workflows"),
                ('sum(rate(temporal_sticky_cache_total_forced_eviction%s[$__rate_interval])) or vector(0)' % SDK, "forced evictions/s")]),
    dict(title="Replay pressure ratio", unit=RATIO, minval=0,
         desc="Replayed workflow tasks over executed workflow tasks. Directional "
              "proxy, not an exact quantity: toward 1.0 means most tasks are "
              "rebuilding state instead of resuming from cache. Now that a real "
              "hit ratio exists two panels up, treat this as corroboration.",
         # Replay latency does not exist until the first replay. On a worker whose
         # sticky cache has never missed, the unguarded numerator empties the whole
         # ratio and the panel reads "No data" instead of the 0.0 that is the
         # actual, and good, answer.
         exprs=[('(sum(rate(temporal_workflow_task_replay_latency_count%s[$__rate_interval])) or vector(0)) / clamp_min(sum(rate(temporal_workflow_task_execution_latency_count%s[$__rate_interval])), 1e-9)' % (SDK, SDK), "replay/exec")]),

    dict(title="Activity retried but recovered /s", unit=RPS,
         desc="activity_task_fail counts every failed attempt; activity_fail "
              "counts only terminal failures. The difference is work the retry "
              "policy rescued. Raise fault.failureRate to make this move.\n\n"
              "The third series is the SDK's own per-attempt failure count. Go's "
              "equivalent (temporal_activity_task_error) does not exist in Core; "
              "temporal_activity_execution_failed does, and it should track "
              "activity_task_fail one-for-one. If it does not, the worker is "
              "failing activities the server never hears about.",
         exprs=[('(sum(rate(activity_task_fail%s[$__rate_interval])) or vector(0)) - (sum(rate(activity_fail%s[$__rate_interval])) or vector(0))' % (SRV, SRV), "recovered/s (server)"),
                ('sum by (activity_type) (rate(temporal_activity_execution_failed%s[$__rate_interval])) or vector(0)' % SDK, "{{activity_type}} failed attempts/s (SDK)")]),
    dict(title="Custom: repro workflow outcomes /s", unit=RPS, stack=True,
         desc="Emitted from Workflow.MetricMeter. Replay-suppressed with NO "
              "opt-out -- ReplaySafeMetricMeter is internal in the .NET SDK, "
              "exactly as in Go. Custom metric names are NOT prefixed with "
              "MetricPrefix, so this is a literal `repro_` and there is no "
              "`temporal_` in front of it.",
         exprs=[('sum by (outcome) (rate(%sworkflow_completed%s[$__rate_interval])) or vector(0)' % (CUSTOM, SDK), "{{outcome}}")]),

    dict(title="Custom: repro workflow latency p95 by outcome", unit=MS, minval=0,
         desc="Recorded with CreateHistogram<TimeSpan>, which follows "
              "UseSecondsForDuration automatically -- so milliseconds, and no "
              "unit suffix on the name. Custom histograms fall into Core's "
              "catch-all bucket set [50, 100, 500, 1000, 2500, 10000] ms, which "
              "tops out at 10 s. The seed heartbeating workflow runs longer than "
              "that, so this one REQUIRES a HistogramBucketOverrides entry too.",
         exprs=[('histogram_quantile(0.95, sum by (le, outcome) (rate(%sworkflow_latency_bucket%s[$__rate_interval])))' % (CUSTOM, SDK), "{{outcome}}")]),
    dict(title="Custom: injected activity faults", unit=RPS,
         desc="Direct view of the fault injector. Both series stay at 0 until "
              "fault.failureRate is raised in config.yaml.",
         exprs=[('sum(rate(%sactivity_failed%s[$__rate_interval])) or vector(0)' % (CUSTOM, SDK), "injected failures/s"),
                ('sum(rate(%sactivity_started%s[$__rate_interval])) or vector(0)' % (CUSTOM, sdk('retried="true"')), "retried attempts/s")]),
])

# ------------------------------------------------------------- heartbeat board
# The board the Go original had no reason to exist. Heartbeating is this repo's
# seed case, and there is NO dedicated heartbeat metric in any Core SDK -- so
# every panel here is either a proxy (the RecordActivityTaskHeartbeat RPC), a
# server-side consequence (activity_task_timeout), or something this repo emits
# itself. Say which, in every description.
heartbeat = grid([
    dict(title="Heartbeat RPCs /s", unit=RPS, h=6, w=8, kind="stat",
         desc="The only built-in heartbeat signal in any Core SDK. Counts "
              "RecordActivityTaskHeartbeat calls that actually reached the "
              "server, i.e. AFTER Core's throttle. It is a normal request, not a "
              "long poll -- Core only marks the poll RPCs as long -- so it lands "
              "in temporal_request, never temporal_long_request. On Temporal "
              "Cloud each of these is a billable Action.",
         exprs=[('sum(rate(temporal_request%s[$__rate_interval])) or vector(0)' % sdk('operation="RecordActivityTaskHeartbeat"'), "heartbeat RPCs/s")]),
    dict(title="Heartbeat timeouts (dashboard range)", unit=NUM, h=6, w=8, kind="stat",
         desc="The authoritative signal that an activity stopped heartbeating in "
              "time, from the server's timer queue. Self-hosted only: this metric "
              "is not exposed in Temporal Cloud. Present in server 1.31.2 as "
              "activity_task_timeout{timeout_type=\"Heartbeat\"}, tagged "
              "operation=\"TimerActiveTaskActivityTimeout\". NOTE: there is no metric "
              "called heartbeat_timeout in server 1.31.2 -- activity timeouts are one "
              "counter split by a timeout_type label. "
              "increase() over the range, not rate(), so a single fault still "
              "registers.",
         exprs=[('sum(increase(activity_task_timeout%s[$__range])) or vector(0)' % srv('operation="TimerActiveTaskActivityTimeout",timeout_type="Heartbeat"'), "heartbeat timeouts")]),
    dict(title="Running heartbeating activities", unit=NUM, h=6, w=8, kind="stat",
         desc="Used activity slots. This repo has exactly one activity type and "
              "it always heartbeats, so this is the divisor that turns a "
              "heartbeat RATE into a per-activity INTERVAL on the panel below.",
         exprs=[('sum(temporal_worker_task_slots_used%s) or vector(0)' % sdk('worker_type="ActivityWorker"'), "running activities")]),

    dict(title="Heartbeat interval: asked for vs throttled vs observed", unit=MS, minval=0, w=24,
         desc="The most educational panel in this repo. It makes an invisible "
              "algorithm visible.\n\n"
              "Core throttles heartbeats to "
              "min(HeartbeatTimeout * 0.8, MaxHeartbeatThrottleInterval), falling "
              "back to DefaultHeartbeatThrottleInterval when the timeout is 0 or "
              "unset. It sends immediately, then suppresses until the interval "
              "elapses, then sends the LATEST buffered details. So calling "
              "Heartbeat() every 100 ms does not send 10 RPCs a second, and the "
              "SDK docs say only 'users do not have to be concerned with "
              "burdening the server', which tells you nothing about what you will "
              "actually see.\n\n"
              "Four series:\n"
              "  A  what the activity code asks for (config)\n"
              "  B  what Core enforces (the app computes and echoes the formula)\n"
              "  C  HeartbeatTimeout -- the cliff\n"
              "  D  the mean gap between the heartbeat RPCs that actually reach "
              "the server, per running activity:\n"
              "       1000 * running activities / heartbeat RPCs per second\n\n"
              "WHAT D IS, EXACTLY -- because it is NOT a measurement of B and it "
              "does not sit on B. An attempt sends one heartbeat immediately and "
              "then at most one per throttle interval, so over an attempt "
              "lifetime L it emits about 1 + L/B RPCs; running activities is L "
              "times the attempt start rate (Little's law), and the two rates "
              "cancel to D = B * (L/B) / (1 + L/B). That is always BELOW B, and "
              "it only approaches B as attempts get long: L = B reads B/2, L = 9B "
              "reads 0.9B. The shipped config runs attempts for many multiples "
              "of the throttle interval, so D lands just under B -- but shorten "
              "the activity and D drops with nothing wrong. Read D as a LOWER "
              "BOUND on the throttle, not as the throttle. Its numerator is an "
              "instantaneous "
              "gauge and its denominator a rate over the whole rate interval, so "
              "a few percent of sampling jitter is normal and D can cross B for a "
              "scrape or two.\n\n"
              "Read it: D up near B and far above A is the throttle doing its job "
              "-- the activity asks every A ms and the server hears one every "
              "~B ms. D climbing toward C means heartbeats are arriving too "
              "slowly and a heartbeat timeout is coming. D VANISHING means no "
              "heartbeat RPC reached the server in the window at all, which is "
              "what fault.stopHeartbeating looks like: the `and rate > 0` guard "
              "drops the series on purpose rather than dividing by ~0 and "
              "spiking to ~1e12 ms, which would autoscale A, B and C into one "
              "flat line at the bottom exactly when you need to read them.\n\n"
              "A, B and C are static config echoed as gauges from the ACTIVITY "
              "meter, not the runtime meter -- the runtime meter has no root "
              "tags, so those series would carry no namespace and the "
              "$namespace selector would drop them silently.",
         exprs=[('max(%sheartbeat_call_interval_ms%s) or vector(0)' % (CUSTOM, SDK), "A: configured call interval"),
                ('max(%sheartbeat_throttle_ms%s) or vector(0)' % (CUSTOM, SDK), "B: Core throttle = min(HeartbeatTimeout*0.8, MaxHeartbeatThrottleInterval)"),
                ('max(%sheartbeat_timeout_ms%s) or vector(0)' % (CUSTOM, SDK), "C: HeartbeatTimeout (the cliff)"),
                # No clamp_min here on purpose. clamp_min(rate, 1e-9) never
                # divides by zero, but that is the bug, not the fix: when
                # heartbeats stop the guard yields ~1e12 ms and Grafana's
                # autoscale flattens A, B and C. `and rate > 0` yields NO SERIES
                # instead -- the honest answer to "what is the mean gap between
                # events that did not happen".
                ('(1000 * sum(temporal_worker_task_slots_used%s) / sum(rate(temporal_request%s[$__rate_interval]))) and (sum(rate(temporal_request%s[$__rate_interval])) > 0)' % (sdk('worker_type="ActivityWorker"'), sdk('operation="RecordActivityTaskHeartbeat"'), sdk('operation="RecordActivityTaskHeartbeat"')), "D: observed gap per activity (lower bound on B)")]),

    dict(title="Heartbeat calls vs heartbeat RPCs /s", unit=RPS,
         desc="The throttle, seen from the other side. repro_heartbeat_sent is "
              "incremented at every ctx.Heartbeat() call site, before Core sees "
              "it. temporal_request{operation=RecordActivityTaskHeartbeat} counts "
              "what survived. The gap IS the throttle, and it is the gap between "
              "your Cloud bill and your intuition.",
         exprs=[('sum(rate(%sheartbeat_sent%s[$__rate_interval])) or vector(0)' % (CUSTOM, SDK), "ctx.Heartbeat() calls/s"),
                ('sum(rate(temporal_request%s[$__rate_interval])) or vector(0)' % sdk('operation="RecordActivityTaskHeartbeat"'), "RPCs reaching the server/s")]),
    dict(title="Heartbeat RPC failures by status code", unit=RPS, stack=True,
         desc="Empty when healthy, and it is the failures that carry the "
              "information.\n\n"
              "NOT_FOUND is the interesting one: the server no longer knows about "
              "this activity -- it timed out, was cancelled, or its workflow "
              "closed. Core turns that response into a cancellation of "
              "ActivityExecutionContext.CancellationToken with "
              "CancelReason=GoneFromServer. This RPC's RESPONSE is the ONLY "
              "channel by which cancellation ever reaches an activity: no "
              "HeartbeatTimeout plus no Heartbeat() calls means the activity can "
              "never be cancelled at all.\n\n"
              "TRANSPORT_ERROR is Core's synthetic value for 'the connection "
              "broke before a gRPC status came back'. It is not a gRPC code and "
              "you will not find it in any status enum. Codes are SCREAMING_SNAKE.",
         exprs=[('sum by (status_code) (rate(temporal_request_failure%s[$__rate_interval])) or vector(0)' % sdk('operation="RecordActivityTaskHeartbeat"'), "{{status_code}}")]),

    dict(title="Heartbeat RPC latency p95", unit=MS, minval=0,
         desc="How long the heartbeat call itself takes. Matters because the "
              "throttle interval is a floor on the SEND rate, not a budget for "
              "the round trip -- a slow heartbeat RPC eats into the 20% margin "
              "between the throttle and the timeout. Shares the "
              "temporal_request_latency bucket override.",
         exprs=[('histogram_quantile(0.95, sum by (le) (rate(temporal_request_latency_bucket%s[$__rate_interval])))' % sdk('operation="RecordActivityTaskHeartbeat"'), "p95")]),
    dict(title="Activity timeouts by type, server view", unit=RPS, stack=True,
         desc="All four counters come off the same emit site in the history "
              "service's timer queue, so they are directly comparable. This is "
              "how you tell a heartbeat timeout from a start-to-close timeout "
              "without opening the Web UI -- the workflow-side exception looks "
              "similar in both cases (ActivityFailureException wrapping "
              "TimeoutFailureException) and only TimeoutType tells them apart.",
         # ONE counter, split by the timeout_type label -- not four separate metrics.
         exprs=[('sum by (timeout_type) (rate(activity_task_timeout%s[$__rate_interval])) or vector(0)' % srv('operation="TimerActiveTaskActivityTimeout"'), "{{timeout_type}}")]),

    dict(title="Activity attempt outcomes, server view", unit=RPS, stack=True,
         desc="activity_task_fail counts every failed ATTEMPT; activity_fail "
              "counts only terminal failures. Same for activity_task_timeout vs "
              "activity_timeout. The gap in each pair is what the retry policy "
              "rescued -- and for a heartbeating activity, rescuing means "
              "resuming from a checkpoint, which is what the bottom two panels "
              "measure.",
         exprs=[('sum(rate(activity_success%s[$__rate_interval])) or vector(0)' % SRV, "success"),
                ('sum(rate(activity_task_fail%s[$__rate_interval])) or vector(0)' % SRV, "failed attempts"),
                ('sum(rate(activity_fail%s[$__rate_interval])) or vector(0)' % SRV, "terminal failures"),
                ('sum(rate(activity_task_timeout%s[$__rate_interval])) or vector(0)' % SRV, "timed-out attempts"),
                ('sum(rate(activity_cancel%s[$__rate_interval])) or vector(0)' % SRV, "cancelled")]),
    dict(title="Cancellation reasons, SDK view", unit=RPS, stack=True,
         desc="Emitted from the activity's catch block as "
              "ctx.CancelReason.ToString(). Nothing built in reports this -- the "
              "server sees 'cancelled', not why.\n\n"
              "The .NET values: CancelRequested (workflow or client cancelled), "
              "GoneFromServer (the heartbeat RPC came back NOT_FOUND -- the "
              "activity already timed out or its workflow closed), WorkerShutdown "
              "(fired GracefulShutdownTimeout after WorkerShutdownToken), Timeout, "
              "HeartbeatRecordFailure (your heartbeat details failed to serialize "
              "-- anonymous types and delegates do this silently), Paused, Reset, "
              "None.",
         exprs=[('sum by (reason) (rate(%sactivity_cancel%s[$__rate_interval])) or vector(0)' % (CUSTOM, SDK), "{{reason}}")]),

    dict(title="Activity execution latency", unit=MS, minval=0,
         desc="Per-attempt wall time from the worker's point of view. For a "
              "heartbeating activity this is the thing HeartbeatTimeout is NOT "
              "measuring -- the timeout is about the gap between heartbeats, not "
              "the total run.\n\n"
              "REQUIRES a HistogramBucketOverrides entry: Core's default top "
              "bucket for this metric is 60 s.",
         exprs=[('histogram_quantile(0.50, sum by (le, activity_type) (rate(temporal_activity_execution_latency_bucket%s[$__rate_interval])))' % SDK, "{{activity_type}} p50"),
                ('histogram_quantile(0.95, sum by (le, activity_type) (rate(temporal_activity_execution_latency_bucket%s[$__rate_interval])))' % SDK, "{{activity_type}} p95")]),
    dict(title="Activity schedule-to-start p95, SDK vs server", unit=MS, minval=0,
         desc="Two independent views of how long an ACTIVITY task sat in the "
              "queue. The server series is multiplied by 1000: server timers are "
              "seconds, Core histograms are milliseconds. Both need the "
              "schedule-to-start bucket override on the SDK side; the server's "
              "buckets are tally's and are fine.\n\n"
              "The server histogram is shared by both task types, so it is pinned "
              "to task_type=\"Activity\" here -- unpinned it also plots workflow "
              "tasks, which the SDK series opposite it does not measure.",
         exprs=[('histogram_quantile(0.95, sum by (le) (rate(temporal_activity_schedule_to_start_latency_bucket%s[$__rate_interval])))' % SDK, "SDK (worker), ms"),
                ('1000 * histogram_quantile(0.95, sum by (le) (rate(task_schedule_to_start_latency_bucket%s[$__rate_interval])))' % HI_ACT, "SERVER (history), s -> ms")]),

    dict(title="Attempts resuming from a checkpoint", unit=RATIO, minval=0,
         desc="Fraction of activity attempts that started from a heartbeat detail "
              "rather than from scratch. Reads 0 when nothing is failing; raise "
              "fault.failureRate or fault.stallPastHeartbeatTimeout and watch it "
              "climb.\n\n"
              "In .NET there is no HasHeartbeatDetails helper -- the app checks "
              "ctx.Info.HeartbeatDetails.Count > 0 and tags this counter with the "
              "answer. HeartbeatDetailAtAsync<T>(0) throws if you skip the check.",
         # Guarding only the denominator was not enough. resumed="true" is a LABEL
         # VALUE, and Core registers a series on first increment -- so with
         # fault.failureRate 0 nothing ever increments it and the numerator series
         # does not exist. Empty / anything is EMPTY in PromQL, so the panel read
         # "No data" in precisely the healthy state whose 0 the description above
         # promises.
         exprs=[('(sum(rate(%sactivity_started%s[$__rate_interval])) or vector(0)) / clamp_min(sum(rate(%sactivity_started%s[$__rate_interval])), 1e-9)' % (CUSTOM, sdk('resumed="true"'), CUSTOM, SDK), "resumed fraction")]),
    dict(title="Checkpoint staleness on resume p95", unit=MS, minval=0,
         desc="How much work gets redone. Measured in the activity as "
              "now - checkpoint.RecordedAtUtc at the moment of resume, which "
              "requires the heartbeat detail to be a record carrying a timestamp "
              "-- a plain int progress counter cannot answer this.\n\n"
              "The overlaid throttle line is the theoretical upper bound: Core "
              "coalesces heartbeats, so the details the server holds can be up to "
              "one full throttle interval old. If p95 staleness sits near that "
              "line, your resume logic had better be idempotent, because it is "
              "reprocessing that much work on every retry.\n\n"
              "Custom histograms land in Core's catch-all bucket set, which tops "
              "out at 10 s -- needs a HistogramBucketOverrides entry.",
         exprs=[('histogram_quantile(0.95, sum by (le) (rate(%sheartbeat_staleness_bucket%s[$__rate_interval])))' % (CUSTOM, SDK), "p95 staleness"),
                ('max(%sheartbeat_throttle_ms%s) or vector(0)' % (CUSTOM, SDK), "throttle interval (upper bound)")]),

    dict(title="Activity progress", unit=NUM, minval=0, w=24,
         desc="A gauge the activity sets each iteration. The sawtooth is the "
              "point: every drop is an attempt that died and a new one that "
              "picked up from the last checkpoint, and the height of the drop is "
              "the staleness measured two panels up. With no fault injected this "
              "is a clean ramp.",
         exprs=[('max by (activity_type) (%sactivity_progress%s) or vector(0)' % (CUSTOM, SDK), "{{activity_type}}")]),
])


BOARDS = [
    ("sandbox-worker", "Repro / Worker Health", worker,
     "SDK-sourced worker health: slots, pollers, schedule-to-start, execution "
     "latency, and client RPCs. Every panel comes from the .NET SDK's Core "
     "Prometheus exporter on :8077/:8078. Latency units are MILLISECONDS.",
     ["sandbox", "sdk"]),
    ("sandbox-server", "Repro / Server and Persistence", server,
     "Server-sourced view from the containerized Temporal server on :8000: "
     "frontend RPS and latency, persistence, matching, and task queue backlog. "
     "Unchanged by the Go -> .NET port: same server binary, same tally metrics, "
     "latency units are SECONDS.",
     ["sandbox", "server"]),
    ("sandbox-signals", "Repro / Bug Signals", signals,
     "The bug-hunting board. Mixes SDK and server metrics to surface "
     "non-determinism, workflow task retries, sticky cache behaviour, replay "
     "pressure, and injected faults. This is the one with no upstream equivalent.",
     ["sandbox", "signals"]),
    ("sandbox-heartbeat", "Repro / Heartbeating", heartbeat,
     "The seed case's own board. No Core SDK has a heartbeat metric, so every "
     "panel here is a proxy (the RecordActivityTaskHeartbeat RPC), a server-side "
     "consequence (activity_task_timeout), or something this repo emits itself. "
     "Start at 'Heartbeat interval: asked for vs throttled vs observed'.",
     ["sandbox", "heartbeat"]),
]

OUT.mkdir(parents=True, exist_ok=True)
total_panels = total_targets = 0
for uid, title, panels, desc, tags in BOARDS:
    d = dashboard(uid, title, desc, panels, tags,
                  variables=("namespace",) if uid != "sandbox-server" else ())
    path = OUT / f"{uid.replace('sandbox-', '')}.json"
    path.write_text(json.dumps(d, indent=2) + "\n")
    n = sum(len(p["targets"]) for p in panels)
    total_panels += len(panels)
    total_targets += n
    print(f"  wrote {path}  panels={len(panels)}  targets={n}")
# The target count is the number docs/DASHBOARDS.md claims was probed against a
# live stack. If you add a panel, this number changes and that claim goes stale.
# Printing it here is the reminder.
print(f"  TOTAL panels={total_panels} targets={total_targets}")