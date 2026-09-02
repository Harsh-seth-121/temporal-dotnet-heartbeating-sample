# shellcheck shell=bash
#
# Shared helpers for scripts/demo-up.sh and scripts/demo-down.sh. Sourced, never
# executed: no shebang, not +x.
#
# WHY THIS IS BASH AND NOT /bin/sh, unlike observability/scripts/*.sh: those two run
# inside alpine/debian containers where /bin/sh may be BusyBox ash. These run on one
# known host and need $SECONDS, `local`, and process substitution.
#
# TARGET IS BASH 3.2.57, the /bin/bash that ships with macOS. `#!/usr/bin/env bash`
# would pick up Homebrew's bash 5 when it is on PATH, so both scripts pin
# `#!/bin/bash` instead and everything here stays 3.2-clean:
#
#   - no `declare -A`, `mapfile`, `wait -n`, `${v,,}`, `shopt -s globstar`
#   - no `$EPOCHSECONDS` (it expands to the empty string); use $SECONDS
#   - arithmetic is `i=$((i+1))`, never `(( i++ ))`. Bare `(( i++ ))` returns 1 when
#     i is 0, which aborts under `set -e` on bash 5 and does NOT on 3.2. That
#     divergence is the easiest bug to introduce in this file.
#
# The callers run `set -eu` and deliberately NOT `set -o pipefail`: pipefail turns a
# normal early-closed pipe into status 141 and kills the script mid-gate.
#
# This host has no `timeout`, `gtimeout`, `setsid`, `flock` or `wget`, so every
# bounded wait below is a hand-rolled $SECONDS loop and every HTTP probe is curl.

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------

# REPO_ROOT is set by the caller before sourcing. Both scripts cd there first:
# ConfigLoader.Resolve searches UPWARD from the working directory, so running
# scripts/demo-up.sh from $HOME would otherwise throw "config file not found".
: "${REPO_ROOT:?REPO_ROOT must be set before sourcing demo-lib.sh}"

# pid and log files, flat on purpose. A .demo/log/ subdirectory would be swallowed
# incidentally by the unanchored [Ll]og/ pattern near the top of .gitignore; the
# explicit /.demo/ entry covers both kinds of file and says so.
DEMO_DIR="${DEMO_DIR:-$REPO_ROOT/.demo}"

# ---------------------------------------------------------------------------
# Tables
# ---------------------------------------------------------------------------

# Container names are pinned in observability/compose.yml precisely so they are
# addressable. `docker inspect` against these beats `docker compose ps`: default ps
# output HIDES stopped containers, and this stack has two that exit 0 on purpose.
DEMO_CONTAINERS='dotnet-temporal-postgresql
dotnet-temporal-schema-setup
dotnet-temporal
dotnet-temporal-create-namespace
dotnet-temporal-ui
dotnet-sandbox-prometheus
dotnet-sandbox-pushgateway
dotnet-sandbox-grafana'

# name|port|binary|ready log line|stop signal
#
# The binaries, not `dotnet run`. docs/GOTCHAS.md: `dotnet run` launches the app as a
# CHILD process, so killing the parent orphans a child still holding :8077 and the
# next start fails with `Address already in use (os error 48)`. Running the built
# binary makes the pid in the pid file the pid holding the port. Do not "simplify"
# this back toward `dotnet run`.
#
# starter's signal is INT, not TERM, and that asymmetry is load-bearing. Worker and
# LoadGen both register PosixSignalRegistration for SIGTERM; src/Repro.Starter has
# only Console.CancelKeyPress. SIGTERM to the starter takes .NET's default path,
# abandons `await using var push`, loses the final Pushgateway push, and leaves
# repro-workflow running.
DEMO_PROCS='worker|8077|src/Repro.Worker/bin/Debug/net10.0/worker|worker polling|TERM
loadgen|8078|src/Repro.LoadGen/bin/Debug/net10.0/loadgen|loadgen: 1 workflow every|TERM
starter|-|src/Repro.Starter/bin/Debug/net10.0/starter|-|INT'

# ---------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------

demo_info()  { printf '%s\n' "$*"; }
demo_warn()  { printf 'WARNING: %s\n' "$*" >&2; }
demo_note()  { printf '  note: %s\n' "$*"; }

# demo_die CODE MESSAGE...
demo_die() {
    local code="$1"; shift
    printf 'ERROR: %s\n' "$*" >&2
    exit "$code"
}

# demo_phase N TOTAL TITLE
demo_phase() {
    printf '\n[%s/%s] %s\n' "$1" "$2" "$3"
}

demo_url_table() {
    cat <<'EOF'
  http://localhost:3000            Grafana, 10 dashboards, anonymous Admin
  http://localhost:8080            Temporal Web UI
  http://localhost:9090/targets    Prometheus target health
  http://localhost:9091            Pushgateway
  http://localhost:8000/metrics    Temporal server metrics
  http://localhost:8077/metrics    worker SDK metrics
  http://localhost:8078/metrics    loadgen SDK metrics
EOF
}

# ---------------------------------------------------------------------------
# Process table lookup
# ---------------------------------------------------------------------------

# demo_field NAME INDEX  ->  one field of the DEMO_PROCS row, or empty.
#
# Iterates with a heredoc redirect, NOT `printf ... | while read`. In bash the pipe
# form runs the loop body in a subshell, so anything it assigns is silently lost.
demo_field() {
    local want="$1" idx="$2" f_name f_port f_bin f_ready f_sig
    while IFS='|' read -r f_name f_port f_bin f_ready f_sig; do
        [ "$f_name" = "$want" ] || continue
        case "$idx" in
            name)   printf '%s\n' "$f_name" ;;
            port)   printf '%s\n' "$f_port" ;;
            bin)    printf '%s\n' "$f_bin" ;;
            ready)  printf '%s\n' "$f_ready" ;;
            signal) printf '%s\n' "$f_sig" ;;
        esac
        return 0
    done <<EOF
$DEMO_PROCS
EOF
    return 0
}

demo_log_path() { printf '%s\n' "$DEMO_DIR/$1.log"; }
demo_pid_path() { printf '%s\n' "$DEMO_DIR/$1.pid"; }

# ---------------------------------------------------------------------------
# Ports
# ---------------------------------------------------------------------------

# demo_port_holders PORT -> space-separated pids LISTENING on the port, or empty.
#
# -sTCP:LISTEN matters. The `lsof -ti tcp:8077` form docs/GOTCHAS.md ships for
# interactive use also matches CONNECTED sockets, so it can name Prometheus's inbound
# connection or a curl instead of the listener. sort -u because -t repeats a pid once
# per matching socket, and a second `kill` of a duplicate pid exits 1.
#
# No pipefail in the callers, so lsof exiting 1 on "nothing found" is absorbed by tr.
demo_port_holders() {
    lsof -nP -iTCP:"$1" -sTCP:LISTEN -t 2>/dev/null | sort -u | tr '\n' ' '
}

demo_port_free() {
    [ -z "$(demo_port_holders "$1" | tr -d ' ')" ]
}

# demo_describe_pid PID -> the command line, for telling our own process apart from
# a stranger before anything gets signalled.
demo_describe_pid() {
    ps -o command= -p "$1" 2>/dev/null | head -1 || true
}

# ---------------------------------------------------------------------------
# Pid files
# ---------------------------------------------------------------------------

# demo_live_pid NAME -> pid if the pid file names a LIVE process that is really ours,
# else empty. Always returns 0, so `pid=$(demo_live_pid worker)` is safe under set -e.
#
# Four checks, and all four are needed:
#   1. file exists
#   2. contents are a positive integer. An empty pid file makes [ "$pid" -gt 0 ]
#      print "integer expression expected" and then quietly take the else branch.
#   3. kill -0 succeeds
#   4. IDENTITY. Pids recycle, and kill -0 cannot tell a dead pid from a foreign one:
#      "No such process" and "Operation not permitted" both exit 1. Without this
#      check, demo-down.sh eventually SIGKILLs an unrelated process.
demo_live_pid() {
    local name="$1" file pid bin
    file=$(demo_pid_path "$name")
    bin=$(demo_field "$name" bin)
    [ -f "$file" ] || return 0
    pid=$(cat "$file" 2>/dev/null || true)
    case "$pid" in ''|*[!0-9]*) return 0 ;; esac
    kill -0 "$pid" 2>/dev/null || return 0
    demo_describe_pid "$pid" | grep -qF -- "$bin" || return 0
    printf '%s\n' "$pid"
}

demo_clear_pid() { rm -f "$(demo_pid_path "$1")"; }

# ---------------------------------------------------------------------------
# Launching
# ---------------------------------------------------------------------------

DEMO_LAUNCHED_PID=""

# demo_launch NAME BINARY LOG [ARGS...]  ->  sets DEMO_LAUNCHED_PID
#
# `trap '' INT TSTP` before `exec` is the whole trick, and it is not optional.
# macOS has no setsid, and a non-interactive bash script leaves background children
# in its OWN process group, so a Ctrl-C in the terminal reaches the worker and the
# loadgen too. Both handle SIGINT, so both would start a 30s graceful drain while
# still holding their ports, and the next demo-up.sh would fail preflight on
# "port in use". nohup does not fix this: a nohup'd child was measured staying in the
# same process group. Setting SIGINT to SIG_IGN does fix it, and exec preserves
# SIG_IGN across the replacement.
#
# nohup is also unnecessary: both streams are redirected, so nohup would only add an
# untracked nohup.out in the repo root.
demo_launch() {
    local name="$1" bin="$2" log="$3"
    shift 3
    ( trap '' INT TSTP; exec "$bin" "$@" >"$log" 2>&1 ) &
    DEMO_LAUNCHED_PID=$!
    printf '%s\n' "$DEMO_LAUNCHED_PID" > "$(demo_pid_path "$name")"
}

# demo_launch_attached NAME BINARY LOG [ARGS...]  ->  sets DEMO_LAUNCHED_PID
#
# Same, WITHOUT the SIGINT shield, for the seed starter only. Backgrounded so the
# caller can watchdog it (this host has no `timeout`), but left in the script's
# process group on purpose: Ctrl-C during the seed run should reach the starter and
# cancel repro-workflow, which is the documented behaviour of that process and the
# recipe docs/DASHBOARDS.md uses to make the cancellation panels move.
demo_launch_attached() {
    local name="$1" bin="$2" log="$3"
    shift 3
    "$bin" "$@" >"$log" 2>&1 &
    DEMO_LAUNCHED_PID=$!
    printf '%s\n' "$DEMO_LAUNCHED_PID" > "$(demo_pid_path "$name")"
}

# ---------------------------------------------------------------------------
# Docker
# ---------------------------------------------------------------------------

demo_docker_up() {
    docker version --format '{{.Server.Version}}' >/dev/null 2>&1
}

# status/exitcode, or absent/- when the container was never created.
demo_container_state() {
    docker inspect -f '{{.State.Status}}/{{.State.ExitCode}}' "$1" 2>/dev/null \
        || printf 'absent/-\n'
}

demo_container_running() {
    [ "$(docker inspect -f '{{.State.Status}}' "$1" 2>/dev/null || true)" = running ]
}

demo_container_exited_ok() {
    [ "$(demo_container_state "$1")" = "exited/0" ]
}

# The {{if .State.Health}} guard is required: temporal-ui and grafana have no
# healthcheck block, and the naive template errors with
# `map has no entry for key "Health"`.
demo_container_health() {
    docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' \
        "$1" 2>/dev/null || printf 'absent\n'
}

# ---------------------------------------------------------------------------
# Probes
# ---------------------------------------------------------------------------

# --fail is not optional: without it curl exits 0 on a 500, and -s hides the body,
# so the probe would pass against a broken Grafana.
demo_http_ok() {
    curl -fs -o /dev/null --max-time 3 "$1"
}

demo_tcp_ok() {
    nc -z -w 2 "$1" "$2" >/dev/null 2>&1
}

# demo_metrics_live PORT -> the exporter is serving REAL SDK families.
#
# Not just a 200. docs/GOTCHAS.md: a client that connects before the runtime is built
# binds to TemporalRuntime.Default, and the exporter then answers 200 with an EMPTY
# registry. Prometheus reports the target UP, every SDK panel stays blank, and nothing
# logs an error. Grepping for a real temporal_* family is the only check that catches
# it, and a polling worker has made gRPC calls within a second or two.
demo_metrics_live() {
    curl -fs --max-time 3 "http://127.0.0.1:$1/metrics" 2>/dev/null | grep -q '^temporal_'
}

# demo_pool_up JOB -> true when Prometheus reports that scrape pool healthy.
#
# The ?scrapePool= filter keeps this jq-free and names its own offender. Counting is
# deliberately avoided: Prometheus emits compact single-line JSON, so `grep -c` would
# return 1 no matter how many targets matched.
demo_pool_up() {
    curl -fs --max-time 5 \
        "http://127.0.0.1:9090/api/v1/targets?state=active&scrapePool=$1" 2>/dev/null \
        | grep -q '"health":"up"'
}

# ---------------------------------------------------------------------------
# Bounded waits
# ---------------------------------------------------------------------------

# demo_gate LABEL BUDGET_SECONDS GUARD_CONTAINER COMMAND...
#
# Polls COMMAND once per second until it succeeds. GUARD_CONTAINER is checked on
# every iteration and "-" disables it: a crash-looping container then fails the gate
# in two seconds instead of burning the whole budget. One dot per 3s keeps a 180s
# gate on one line.
demo_gate() {
    local label="$1" budget="$2" guard="$3"
    shift 3
    local t0=$SECONDS elapsed=0
    printf '  %-30s' "$label"
    while :; do
        if "$@" >/dev/null 2>&1; then
            printf ' ok (%ss)\n' "$((SECONDS - t0))"
            return 0
        fi
        if [ "$guard" != "-" ] && ! demo_container_running "$guard"; then
            printf ' FAILED\n'
            printf '    %s is %s, not running\n' "$guard" "$(demo_container_state "$guard")"
            return 1
        fi
        elapsed=$((SECONDS - t0))
        if [ "$elapsed" -ge "$budget" ]; then
            printf ' TIMEOUT (%ss)\n' "$elapsed"
            return 1
        fi
        if [ $((elapsed % 3)) -eq 0 ]; then
            printf '.'
        fi
        sleep 1
    done
}

# demo_signal SIGNAME PID...  Never fatal: a process that exited between the liveness
# check and here is the normal case, not an error.
demo_signal() {
    local sig="$1"
    shift
    local pid
    for pid in "$@"; do
        [ -n "$pid" ] || continue
        kill -"$sig" "$pid" 2>/dev/null || true
    done
}

# demo_wait_gone BUDGET PID...  -> 0 when every pid is gone, 1 on timeout.
#
# `wait` cannot be used for this. These pids are not children of demo-down.sh, and
# `wait` on a non-child exits 127, which under set -e ends the script before
# `docker compose down` ever runs.
demo_wait_gone() {
    local budget="$1"
    shift
    local t0=$SECONDS elapsed=0 alive pid
    while :; do
        alive=""
        for pid in "$@"; do
            [ -n "$pid" ] || continue
            if kill -0 "$pid" 2>/dev/null; then
                alive="$alive $pid"
            fi
        done
        [ -n "$alive" ] || return 0
        elapsed=$((SECONDS - t0))
        [ "$elapsed" -lt "$budget" ] || return 1
        if [ $((elapsed % 10)) -eq 0 ] && [ "$elapsed" -gt 0 ]; then
            printf '\n    still draining after %ss:%s (SIGKILL at %ss)' \
                "$elapsed" "$alive" "$budget"
        elif [ $((elapsed % 2)) -eq 0 ]; then
            printf '.'
        fi
        sleep 1
    done
}

# demo_wait_ports_free BUDGET PORT...  A freed port is the real success criterion:
# a held one is the `Address already in use (os error 48)` that breaks the next up.
demo_wait_ports_free() {
    local budget="$1"
    shift
    local t0=$SECONDS port held
    while :; do
        held=""
        for port in "$@"; do
            [ "$port" = "-" ] && continue
            demo_port_free "$port" || held="$held $port"
        done
        [ -n "$held" ] || return 0
        [ $((SECONDS - t0)) -lt "$budget" ] || return 1
        sleep 1
    done
}
