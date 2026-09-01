#!/bin/bash
#
# Generate the large sample corpora the long-running activity demo chews through.
#
#   ./scripts/gen-samples/gen-samples.sh [--out DIR] [--sizes LIST] [--selftest] ...
#
# Writes sample_files/sample-{100,200,350,500}mb.txt, ~1.15 GB total: line 1 is the row
# count, each row after it is a 10-digit index plus seven bracketed words, and no two
# rows ever share a word tuple. All of that lives in gen_samples.py. This file only
# picks the interpreter, so --help and every other flag is handed straight to argparse
# instead of being restated here, where it would drift.
#
# WHY uv IS PREFERRED: interpreter selection, not packages. The tool is stdlib-only and
# has nothing to install. What uv buys is a tuned CPython, roughly 25% faster on this
# workload than the 3.14 a current Homebrew or python.org installer leaves on PATH.
# GEN_NO_UV=1 forces the python3 fallback, which is also how you test that path on a
# machine that has uv.
#
# `#!/bin/bash` is pinned, NOT `#!/usr/bin/env bash`: Homebrew's bash 5.3 is first on
# PATH here while /bin/bash is 3.2.57, and the two disagree under `set -e`. Everything
# below stays 3.2-clean -- no `declare -A`, `mapfile`, `${v,,}`, `shopt -s globstar`,
# `$EPOCHSECONDS`, or bare `(( i++ ))`.
#
# `set -eu` without pipefail, matching scripts/demo-up.sh: pipefail turns a normally
# early-closed pipe into status 141, and buys nothing in a script this short.
set -eu

# Reimplemented inline rather than reusing demo-lib.sh's demo_die, and the divergence is
# deliberate: this directory is standalone. It has no REPO_ROOT, assumes no git repo,
# and must still work when copied somewhere else entirely, so sourcing a sibling library
# for two lines would be the only thing tying it to this checkout.
#
# Always exit 3, because "no usable interpreter" is the only way this wrapper itself can
# fail; codes 2 and 4-7 belong to gen_samples.py and reach the caller through exec.
die() {
  printf 'gen-samples: %s\n' "$*" >&2
  exit 3
}

# Resolved from $0, not from $PWD and not from `git rev-parse`: gen_samples.py and
# words-1024.txt are found beside THIS FILE, whatever directory the caller ran it from.
SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd -P)

# exec in every branch: no wrapper process left sitting on a 44-second run, and the
# python exit code becomes ours untouched. "$@" is quoted so `--sizes '100MB, 200MB'`
# survives as one argument.
if [ -z "${GEN_NO_UV:-}" ] && command -v uv >/dev/null 2>&1; then
  # The PEP 723 header in gen_samples.py pins requires-python; uv fetches a matching
  # CPython on first run and caches it. Nothing is installed into the project.
  exec uv run --script "$SCRIPT_DIR/gen_samples.py" "$@"
elif command -v python3 >/dev/null 2>&1; then
  # Whatever python3 happens to be on PATH. gen_samples.py enforces its own 3.11 floor,
  # so macOS's /usr/bin/python3 3.9.6 is rejected there with the same exit 3 as here.
  exec python3 "$SCRIPT_DIR/gen_samples.py" "$@"
fi

die "no usable interpreter on PATH. Either install uv (https://docs.astral.sh/uv/), which is preferred because it fetches its own CPython in the tuned range, or put a python3 >= 3.11 on PATH and re-run."
