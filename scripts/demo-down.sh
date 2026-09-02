#!/bin/bash
#
# Put the machine back after ./scripts/demo-up.sh.
#
#   ./scripts/demo-down.sh [--keep-stack] [--volumes] [--force]
#
# `docker compose down` alone is not a teardown here: the worker, loadgen and starter run
# on the host, so left behind they hold :8077 and :8078 and the next demo-up.sh fails
# preflight. Standing rule: this signals only what demo-up.sh started (pid file plus
# identity check) or what holds 8077/8078 and looks like ours. Everything else it reports.
# `#!/bin/bash` is pinned and `set -eu` runs without pipefail; see scripts/demo-lib.sh.
set -eu

REPO_ROOT=$(cd "$(dirname "$0")/.." && pwd -P)
# shellcheck source=scripts/demo-lib.sh
. "$REPO_ROOT/scripts/demo-lib.sh"
cd "$REPO_ROOT"

PHASES=6
KEEP_STACK=0
WIPE_VOLUMES=0
FORCE=0

usage() {
    cat <<'EOF'
usage: ./scripts/demo-down.sh [--keep-stack] [--volumes] [--force] [-h]

  --keep-stack   stop the host processes but leave all eight containers running.
                 The fast iteration loop is: down --keep-stack, edit a fault knob, up.
  --volumes      also delete the three named volumes. REQUIRED when you change
                 NUM_HISTORY_SHARDS. Destroys all workflow history and all metrics.
  --force        skip the graceful drain and SIGKILL immediately. Use when the drain
                 is not the thing you are testing.

env vars:
  DEMO_DIR=.demo         pid and log files
  DEMO_DRAIN_TIMEOUT     SIGTERM to SIGKILL window. Defaults to
                         worker.gracefulShutdownTimeout from the config, plus 15s.

Full reference: docs/DEMO.md
EOF
}

while [ "$#" -gt 0 ]; do
    case "${1:-}" in
        --keep-stack) KEEP_STACK=1 ;;
        --volumes)    WIPE_VOLUMES=1 ;;
        --force)      FORCE=1 ;;
        -h|--help)    usage; exit 0 ;;
        # No -v short form: -v conventionally means verbose, and this is destructive.
        *)            usage >&2; demo_die 2 "unknown flag \"$1\"" ;;
    esac
    shift
done

if [ "$KEEP_STACK" -eq 1 ] && [ "$WIPE_VOLUMES" -eq 1 ]; then
    demo_die 2 "--keep-stack and --volumes contradict each other: volumes cannot be removed while the containers using them are running."
fi

CONFIG="${REPRO_CONFIG:-$REPO_ROOT/config.yaml}"
case "$CONFIG" in /*) ;; *) CONFIG="$REPO_ROOT/$CONFIG" ;; esac

# "1m30s" -> 90. Empty on anything this does not handle, so the caller falls back.
go_seconds() {
    local d="$1" mins=0 secs=0
    case "$d" in
        ''|*[!0-9ms]*) printf ''; return 0 ;;
        # "500ms" would take the minutes branch below and come out as 500m.
        *ms*) printf ''; return 0 ;;
    esac
    case "$d" in
        *m*) mins=${d%%m*}; secs=${d#*m}; secs=${secs%s} ;;
        *s)  secs=${d%s} ;;
        *)   secs=$d ;;
    esac
    [ -n "$secs" ] || secs=0
    [ -n "$mins" ] || mins=0
    case "$mins$secs" in *[!0-9]*) printf ''; return 0 ;; esac
    printf '%s\n' "$((mins * 60 + secs))"
}

config_value() {
    grep -E "^[[:space:]]*$1:" "$CONFIG" 2>/dev/null \
        | head -1 \
        | sed "s/^[[:space:]]*$1:[[:space:]]*//; s/[[:space:]]*#.*\$//" \
        | tr -d '"' || true
}

# Derived, not guessed: the worker holds :8077 for the whole grace window plus however
# long the activity then takes to unwind. See docs/GOTCHAS.md.
GRACE=$(go_seconds "$(config_value gracefulShutdownTimeout)")
GRACE="${GRACE:-30}"
DRAIN_TIMEOUT="${DEMO_DRAIN_TIMEOUT:-$((GRACE + 15))}"

IGNORE_CANCEL=0
if grep -qE '^[[:space:]]*ignoreCancellation:[[:space:]]*true' "$CONFIG" 2>/dev/null; then
    IGNORE_CANCEL=1
fi

# 1. Report. down never acts silently: everything it is about to signal, and
# everything it decided not to, is printed first.

demo_phase 1 $PHASES "state"

WORKER_PID=$(demo_live_pid worker)
LOADGEN_PID=$(demo_live_pid loadgen)
STARTER_PID=$(demo_live_pid starter)

report_one() {
    local name="$1" pid="$2" port state holders
    port=$(demo_field "$name" port)
    if [ -n "$pid" ]; then
        state="pid $pid live"
    elif [ -f "$(demo_pid_path "$name")" ]; then
        state="pid file stale"
    else
        state="not running"
    fi
    if [ "$port" = "-" ]; then
        printf '  %-8s %-18s\n' "$name" "$state"
    else
        holders=$(demo_port_holders "$port" | sed 's/ *$//')
        if [ -n "$holders" ]; then
            printf '  %-8s %-18s :%s held by %s\n' "$name" "$state" "$port" "$holders"
        else
            printf '  %-8s %-18s :%s free\n' "$name" "$state" "$port"
        fi
    fi
}
report_one starter "$STARTER_PID"
report_one loadgen "$LOADGEN_PID"
report_one worker  "$WORKER_PID"

# A starter someone ran by hand has no pid file. Report it, never signal it.
STRAY_STARTERS=$(pgrep -f 'bin/Debug/net10.0/starter' 2>/dev/null | tr '\n' ' ' || true)
if [ -n "$(printf '%s' "$STRAY_STARTERS" | tr -d ' ')" ] && [ -z "$STARTER_PID" ]; then
    demo_note "starter pid(s) $STRAY_STARTERS are running but were not started by demo-up.sh, so they are left alone."
fi

# 2. Starter, with SIGINT. src/Repro.Starter/Program.cs registers only
# Console.CancelKeyPress, which is signal-driven rather than tty-driven, so `kill -INT` on
# a detached process fires it; SIGTERM would lose the final Pushgateway push. The starter
# goes first because its cancellation cannot complete once the worker is gone.

demo_phase 2 $PHASES "starter"

if [ -z "$STARTER_PID" ]; then
    demo_info "  nothing to stop"
else
    demo_info "  SIGINT to $STARTER_PID; it cancels the workflow, settles for pushSettle, then pushes"
    demo_signal INT "$STARTER_PID"
    if demo_wait_gone 30 "$STARTER_PID"; then
        printf '\n  stopped\n'
    else
        printf '\n'
        demo_warn "the starter ignored SIGINT for 30s; escalating to SIGTERM then SIGKILL"
        demo_signal TERM "$STARTER_PID"
        demo_wait_gone 5 "$STARTER_PID" || demo_signal KILL "$STARTER_PID"
    fi
    demo_note "a cancelled starter exits 1, because the workflow ends in WorkflowFailedException. That is the expected result here, not a failure."
fi

# 3. Loadgen and worker. SIGTERM to both, loadgen first because it is the only source of
# new workflows, then one shared wait: their grace windows overlap, so waiting in parallel
# costs one drain, not two.

demo_phase 3 $PHASES "loadgen and worker"

DRAIN_PIDS=""
[ -n "$LOADGEN_PID" ] && DRAIN_PIDS="$DRAIN_PIDS $LOADGEN_PID"
[ -n "$WORKER_PID" ] && DRAIN_PIDS="$DRAIN_PIDS $WORKER_PID"

if [ -z "$(printf '%s' "$DRAIN_PIDS" | tr -d ' ')" ]; then
    demo_info "  nothing to stop"
elif [ "$FORCE" -eq 1 ]; then
    demo_info "  --force: SIGKILL$DRAIN_PIDS, no drain"
    demo_signal KILL $DRAIN_PIDS
    demo_wait_gone 5 $DRAIN_PIDS || true
    printf '\n'
else
    demo_info "  SIGTERM$DRAIN_PIDS"
    printf '  worker.gracefulShutdownTimeout is %ss, so the SDK holds its port that long\n' "$GRACE"
    printf '  before it even cancels the activity, then waits for it to unwind.\n'
    if [ "$IGNORE_CANCEL" -eq 1 ]; then
        demo_note "fault.ignoreCancellation is true in ${CONFIG#$REPO_ROOT/}. TemporalWorker.ExecuteAsync will NOT return, so the SIGKILL below is the expected outcome rather than a failure."
    fi
    printf '  waiting up to %ss' "$DRAIN_TIMEOUT"
    demo_signal TERM $DRAIN_PIDS
    if demo_wait_gone "$DRAIN_TIMEOUT" $DRAIN_PIDS; then
        printf '\n  both drained\n'
    else
        printf '\n'
        demo_warn "still running after ${DRAIN_TIMEOUT}s; SIGKILL"
        demo_signal KILL $DRAIN_PIDS
        demo_wait_gone 5 $DRAIN_PIDS || true
        printf '\n'
    fi
fi

# 4. Ports. A freed port is the real success criterion: a held one is the
# `Address already in use (os error 48)` that breaks the next up.

demo_phase 4 $PHASES "ports"

SWEEP_FAILED=0
if demo_wait_ports_free 5 8077 8078; then
    demo_info "  8077 and 8078 free"
else
    for name in worker loadgen; do
        port=$(demo_field "$name" port)
        bin=$(demo_field "$name" bin)
        holders=$(demo_port_holders "$port" | sed 's/ *$//')
        [ -n "$holders" ] || continue
        for pid in $holders; do
            cmd=$(demo_describe_pid "$pid")
            # Ours, or the `dotnet run` child docs/GOTCHAS.md warns about, whose
            # parent we never had a pid for. Nothing else is ours to clean up.
            if printf '%s' "$cmd" | grep -qE "bin/Debug/net10\.0/(worker|loadgen)|Repro\.(Worker|LoadGen)"; then
                demo_warn ":$port still held by pid $pid; SIGKILL"
                printf '    %s\n' "$cmd"
                demo_signal KILL "$pid"
            else
                demo_warn ":$port is held by pid $pid, which is not one of ours. NOT killing it."
                printf '    %s\n' "$cmd"
                SWEEP_FAILED=1
            fi
        done
    done
    if demo_wait_ports_free 5 8077 8078; then
        demo_info "  8077 and 8078 free"
    else
        SWEEP_FAILED=1
    fi
fi

# 5. Containers

demo_phase 5 $PHASES "containers"

if ! demo_docker_up; then
    # Warn, do not fail: the host processes are fixed and the containers are already down.
    demo_warn "the docker daemon is not reachable, so the container side is already down."
elif [ "$KEEP_STACK" -eq 1 ]; then
    demo_info "  --keep-stack: all eight containers left running"
    demo_url_table
    demo_note "the starter's Pushgateway group survives until the container does. Clear it with: $(demo_field starter bin) --delete-push-group"
elif [ "$WIPE_VOLUMES" -eq 1 ]; then
    cat <<'EOF'
  --volumes will DELETE:
    temporal-dotnet-sandbox_temporal-pgdata    every workflow history on this stack
    temporal-dotnet-sandbox_prometheus-data    every metric sample scraped so far
    temporal-dotnet-sandbox_grafana-data       UI-side dashboard edits only; the
                                               provisioner rewrites the boards from
                                               observability/grafana/dashboards/ on
                                               every boot
  history/*.json on disk is NOT affected.
EOF
    docker compose down -v
else
    docker compose down
    demo_info "  volumes kept: temporal-pgdata, prometheus-data, grafana-data"
fi

# 6. Cleanup. Pid files go only for processes confirmed gone. Logs are never deleted here:
# up truncates them at launch, so the session you just stopped stays readable.

demo_phase 6 $PHASES "cleanup"

for name in worker loadgen starter; do
    if [ -n "$(demo_live_pid "$name")" ]; then
        demo_warn "$name is still alive; keeping ${DEMO_DIR#$REPO_ROOT/}/$name.pid"
    else
        demo_clear_pid "$name"
    fi
done
demo_info "  pid files cleared; logs kept in ${DEMO_DIR#$REPO_ROOT/}/"

if [ "$SWEEP_FAILED" -eq 1 ]; then
    demo_die 7 "a port is still held, so the next ./scripts/demo-up.sh will fail preflight. See the pids named above."
fi

printf '\n'
demo_info "Down."
exit 0
