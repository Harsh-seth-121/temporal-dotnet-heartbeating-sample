#!/bin/bash
#
# Generate the large sample corpora the long-running activity demo chews through.
#
#   ./scripts/gen-samples/gen-samples.sh [--out DIR] [--sizes LIST] [--selftest] ...
#
# Writes sample_files/sample-{100,200,350,500}mb.txt, about 1.15 GB. The row format and
# every flag live in gen_samples.py; this file only picks the interpreter, so --help
# goes straight to argparse rather than being restated here and drifting.
#
# uv is preferred for interpreter selection, not packages: the tool is stdlib-only, and
# uv's tuned CPython is roughly 25% faster on this workload than the 3.14 a Homebrew or
# python.org installer leaves on PATH. GEN_NO_UV=1 forces the python3 fallback.
#
# `#!/bin/bash` is pinned and `set -eu` runs without pipefail; scripts/demo-lib.sh has
# both rules and the bash 3.2 constraints this file also keeps to.
set -eu

# Inline rather than demo-lib.sh's demo_die: this directory is standalone and must work
# when copied elsewhere. Always exit 3, the only way this wrapper itself can fail; codes
# 2 and 4-7 belong to gen_samples.py and reach the caller through exec.
die() {
  printf 'gen-samples: %s\n' "$*" >&2
  exit 3
}

# Resolved from $0, not $PWD: gen_samples.py and words-1024.txt are found beside this
# file, whatever directory the caller ran it from.
SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd -P)

# exec in every branch: no wrapper process left on a 44-second run, and the python exit
# code becomes ours. "$@" is quoted so `--sizes '100MB, 200MB'` stays one argument.
if [ -z "${GEN_NO_UV:-}" ] && command -v uv >/dev/null 2>&1; then
  # The PEP 723 header pins requires-python; uv fetches a matching CPython and caches it.
  exec uv run --script "$SCRIPT_DIR/gen_samples.py" "$@"
elif command -v python3 >/dev/null 2>&1; then
  # gen_samples.py enforces its own 3.11 floor, so /usr/bin/python3 3.9.6 exits 3 there.
  exec python3 "$SCRIPT_DIR/gen_samples.py" "$@"
fi

die "no usable interpreter on PATH. Either install uv (https://docs.astral.sh/uv/), which is preferred because it fetches its own CPython in the tuned range, or put a python3 >= 3.11 on PATH and re-run."
