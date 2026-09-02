# shellcheck shell=bash
#
# Shared helpers for scripts/demo-up.sh and scripts/demo-down.sh. Sourced, never
# executed: no shebang, not +x.
#
# Target is bash 3.2.57, the /bin/bash macOS ships, so both callers pin `#!/bin/bash`,
# not `#!/usr/bin/env bash`, which picks up Homebrew's bash 5. No `declare -A`,
# `mapfile`, `wait -n`, `${v,,}`, `shopt -s globstar` or `$EPOCHSECONDS`; use $SECONDS,
# and write `i=$((i+1))`: bare `(( i++ ))` returns 1 at i=0 and aborts under `set -e`.
#
# The callers run `set -eu` and deliberately not `set -o pipefail`: pipefail turns a
# normal early-closed pipe into status 141 and kills the script mid-gate.
#
# This host has no `timeout`, `setsid`, `flock` or `wget`, so every bounded wait is a
# hand-rolled $SECONDS loop and every HTTP probe is curl.

# REPO_ROOT is set by the caller before sourcing, and both scripts cd there:
# ConfigLoader.Resolve searches upward, so from $HOME it finds no config file.
: "${REPO_ROOT:?REPO_ROOT must be set before sourcing demo-lib.sh}"

# Flat, no .demo/log/ subdirectory: .gitignore's unanchored [Ll]og/ would swallow it.
DEMO_DIR="${DEMO_DIR:-$REPO_ROOT/.demo}"

# Pinned in observability/compose.yml so `docker inspect` can address them. Default
# `docker compose ps` hides stopped containers, and two here exit 0 on purpose.
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
# The built binaries, not `dotnet run`, whose child would hold the port the pid file
# does not name. See docs/GOTCHAS.md, "`dotnet run` launches your app as a CHILD
# process". starter's signal is INT: it registers only Console.CancelKeyPress, so
# SIGTERM loses the final Pushgateway push.
DEMO_PROCS='worker|8077|src/Repro.Worker/bin/Debug/net10.0/worker|worker polling|TERM
loadgen|8078|src/Repro.LoadGen/bin/Debug/net10.0/loadgen|loadgen: 1 workflow every|TERM
starter|-|src/Repro.Starter/bin/Debug/net10.0/starter|-|INT'

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

# demo_field NAME INDEX  ->  one field of the DEMO_PROCS row, or empty. Heredoc, not
# `printf | while read`: the pipe form runs the body in a subshell and loses assignments.
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

# demo_port_holders PORT -> space-separated pids listening on the port, or empty.
# -sTCP:LISTEN matters: plain `lsof -ti tcp:8077` also matches connected sockets. sort -u
# because -t repeats a pid per socket and a second kill of a duplicate exits 1.
demo_port_holders() {
    lsof -nP -iTCP:"$1" -sTCP:LISTEN -t 2>/dev/null | sort -u | tr '\n' ' '
}

demo_port_free() {
    [ -z "$(demo_port_holders "$1" | tr -d ' ')" ]
}

# demo_describe_pid PID -> the command line, for telling ours from a stranger.
demo_describe_pid() {
    ps -o command= -p "$1" 2>/dev/null | head -1 || true
}

# demo_live_pid NAME -> pid if the pid file names a live process that is really ours,
# else empty. Always returns 0, so `pid=$(demo_live_pid worker)` is safe under set -e.
# All four checks are needed: an empty pid file makes [ "$pid" -gt 0 ] take the else
# branch, and pids recycle, so without identity this eventually SIGKILLs a stranger.
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

DEMO_LAUNCHED_PID=""

# demo_launch NAME BINARY LOG [ARGS...]  ->  sets DEMO_LAUNCHED_PID
#
# `trap '' INT TSTP` before `exec` is not optional. macOS has no setsid and a
# non-interactive bash script leaves background children in its own process group, so
# Ctrl-C would reach worker and loadgen and start a 30s drain while they hold their
# ports. nohup does not fix it, measured; SIG_IGN does, and exec preserves it.
demo_launch() {
    local name="$1" bin="$2" log="$3"
    shift 3
    ( trap '' INT TSTP; exec "$bin" "$@" >"$log" 2>&1 ) &
    DEMO_LAUNCHED_PID=$!
    printf '%s\n' "$DEMO_LAUNCHED_PID" > "$(demo_pid_path "$name")"
}

# demo_launch_attached NAME BINARY LOG [ARGS...]  ->  sets DEMO_LAUNCHED_PID
#
# Same without the SIGINT shield, for the seed starter only, so Ctrl-C reaches it and
# cancels repro-workflow (docs/DASHBOARDS.md). Backgrounded so the caller can watchdog it.
demo_launch_attached() {
    local name="$1" bin="$2" log="$3"
    shift 3
    "$bin" "$@" >"$log" 2>&1 &
    DEMO_LAUNCHED_PID=$!
    printf '%s\n' "$DEMO_LAUNCHED_PID" > "$(demo_pid_path "$name")"
}

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
# healthcheck, and the naive template errors with `map has no entry for key "Health"`.
demo_container_health() {
    docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' \
        "$1" 2>/dev/null || printf 'absent\n'
}

# --fail is not optional: without it curl exits 0 on a 500, and -s hides the body,
# so the probe would pass against a broken Grafana.
demo_http_ok() {
    curl -fs -o /dev/null --max-time 3 "$1"
}

demo_tcp_ok() {
    nc -z -w 2 "$1" "$2" >/dev/null 2>&1
}

# demo_metrics_live PORT -> the exporter serves real SDK families, not just a 200. A
# client built before the runtime binds to TemporalRuntime.Default and answers 200 with
# an empty registry: target UP, every SDK panel blank. See docs/GOTCHAS.md.
demo_metrics_live() {
    curl -fs --max-time 3 "http://127.0.0.1:$1/metrics" 2>/dev/null | grep -q '^temporal_'
}

# demo_pool_up JOB -> true when Prometheus reports that scrape pool healthy. The
# ?scrapePool= filter keeps this jq-free. Nothing counts matches: Prometheus emits
# compact single-line JSON, so `grep -c` returns 1 however many targets matched.
demo_pool_up() {
    curl -fs --max-time 5 \
        "http://127.0.0.1:9090/api/v1/targets?state=active&scrapePool=$1" 2>/dev/null \
        | grep -q '"health":"up"'
}

# demo_gate LABEL BUDGET_SECONDS GUARD_CONTAINER COMMAND...
#
# Polls COMMAND once a second. GUARD_CONTAINER is checked every iteration, "-" disables
# it, so a crash-looping container fails in two seconds, not the whole budget.
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

# demo_wait_gone BUDGET PID...  -> 0 when every pid is gone, 1 on timeout. `wait` is
# unusable: these pids are not children of demo-down.sh, and `wait` on a non-child
# exits 127, which under set -e ends the script before `docker compose down` runs.
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
