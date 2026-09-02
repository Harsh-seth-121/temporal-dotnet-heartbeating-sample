#!/bin/bash
#
# Bring up the whole end-to-end local demo and do not return until it is real.
#
#   ./scripts/demo-up.sh [--config PATH] [--no-loadgen]
#
# Replaces `dotnet build`, `docker compose up -d` and three `dotnet run` terminals, and
# waits until Prometheus has actually scraped the worker. See docs/DEMO.md, "What up does".
#
# `#!/bin/bash` is pinned and `set -eu` runs without pipefail. scripts/demo-lib.sh
# has both rules and the bash 3.2 constraints.
set -eu

REPO_ROOT=$(cd "$(dirname "$0")/.." && pwd -P)
# shellcheck source=scripts/demo-lib.sh
. "$REPO_ROOT/scripts/demo-lib.sh"

# ConfigLoader.Resolve searches upward, so everything below runs from the repo root.
# --config is also passed to every binary, so nothing depends on this cd surviving.
cd "$REPO_ROOT"

PHASES=8
CONFIG=""
WITH_LOADGEN=1
GATE_TIMEOUT="${DEMO_GATE_TIMEOUT:-90}"
STARTER_TIMEOUT="${DEMO_STARTER_TIMEOUT:-420}"

usage() {
    cat <<'EOF'
usage: ./scripts/demo-up.sh [--config PATH] [--no-loadgen] [-h]

  --config PATH   config file for all three processes. Defaults to $REPRO_CONFIG,
                  then config.yaml. Use config.local.yaml for secrets and overrides.
  --no-loadgen    do not start Repro.LoadGen, leaving :8078 free for the two-worker
                  recipe in docs/HEARTBEATING.md.

env vars:
  DEMO_DIR=.demo            pid and log files
  DEMO_SKIP_BUILD=1         skip dotnet build
  DEMO_GATE_TIMEOUT=90      per-gate budget, seconds
  DEMO_STARTER_TIMEOUT=420  seed-workflow watchdog, seconds

Stop everything with ./scripts/demo-down.sh. Full reference: docs/DEMO.md
EOF
}

while [ "$#" -gt 0 ]; do
    case "${1:-}" in
        --config)
            shift
            [ "$#" -gt 0 ] || demo_die 2 "--config requires a path"
            CONFIG="$1"
            ;;
        --config=*)  CONFIG="${1#--config=}" ;;
        --no-loadgen) WITH_LOADGEN=0 ;;
        -h|--help)   usage; exit 0 ;;
        *)           usage >&2; demo_die 2 "unknown flag \"$1\"" ;;
    esac
    shift
done

CONFIG="${CONFIG:-${REPRO_CONFIG:-config.yaml}}"
case "$CONFIG" in /*) ;; *) CONFIG="$REPO_ROOT/$CONFIG" ;; esac
# --config wins inside ConfigLoader.Resolve anyway; exporting also covers a process
# added later without the flag.
export REPRO_CONFIG="$CONFIG"

mkdir -p "$DEMO_DIR"

# Ctrl-C leaves everything running on purpose: rolling back would kill workers
# mid-drain and leave them holding :8077 for another 30 seconds.
demo_on_interrupt() {
    printf '\n'
    demo_info "interrupted. The stack and any process this run started are LEFT RUNNING."
    demo_info "Stop everything with:  ./scripts/demo-down.sh"
    exit 130
}
trap demo_on_interrupt INT

WORKFLOW_ID=$(grep -E '^workflowId:' "$CONFIG" 2>/dev/null \
    | head -1 | sed 's/^workflowId:[[:space:]]*//; s/[[:space:]]*#.*$//' | tr -d '"' || true)
WORKFLOW_ID="${WORKFLOW_ID:-repro-workflow}"

# 1. Preflight. Collects every failure and exits once, so a missing Docker and a
# missing SDK take one round trip. Every later phase fails fast.

PF_FAILED=0
pf_ok()   { printf '  ok    %s\n' "$*"; }
pf_warn() { printf '  warn  %s\n' "$*"; }
pf_bad()  { printf '  FAIL  %s\n' "$*"; PF_FAILED=$((PF_FAILED + 1)); }

# port_state NAME -> "free" | "ours PID" | "foreign PIDS"
port_state() {
    local name="$1" port holders mypid
    port=$(demo_field "$name" port)
    holders=$(demo_port_holders "$port" | sed 's/^ *//; s/ *$//')
    if [ -z "$holders" ]; then
        printf 'free\n'
        return 0
    fi
    mypid=$(demo_live_pid "$name")
    if [ -n "$mypid" ] && printf ' %s ' "$holders" | grep -q " $mypid "; then
        printf 'ours %s\n' "$mypid"
        return 0
    fi
    printf 'foreign %s\n' "$holders"
}

demo_phase 1 $PHASES "preflight"

if demo_docker_up; then
    pf_ok "docker daemon ($(docker version --format '{{.Server.Version}}' 2>/dev/null))"
else
    pf_bad "the docker daemon is not reachable. Start Docker Desktop."
fi

COMPOSE_VER=$(docker compose version --short 2>/dev/null || true)
case "$COMPOSE_VER" in
    '') pf_bad "docker compose v2 is not installed (\`docker compose version\` failed)" ;;
    *)
        cv_major=${COMPOSE_VER%%.*}
        cv_rest=${COMPOSE_VER#*.}
        cv_minor=${cv_rest%%.*}
        case "$cv_major$cv_minor" in
            *[!0-9]*) pf_warn "could not parse compose version \"$COMPOSE_VER\"; \`include:\` needs 2.20+" ;;
            *)
                if [ "$cv_major" -gt 2 ] || { [ "$cv_major" -eq 2 ] && [ "$cv_minor" -ge 20 ]; }; then
                    pf_ok "docker compose $COMPOSE_VER"
                else
                    pf_bad "docker compose $COMPOSE_VER is too old; compose.yml uses \`include:\`, which needs 2.20+"
                fi
                ;;
        esac
        ;;
esac

if [ -f "$REPO_ROOT/observability/.env" ]; then
    pf_ok "observability/.env"
else
    pf_bad "observability/.env is missing. Without it every \${...} in the compose file interpolates to a blank string and you get \`image: postgres:\`."
fi

# Compose reads the project-directory .env in preference to observability/.env, and it
# overrides the `name:` key, so a root .env splits the stack in two.
if [ -e "$REPO_ROOT/.env" ]; then
    pf_bad "a root .env exists. Compose reads it in preference to observability/.env and it overrides \`name: temporal-dotnet-sandbox\`, which splits the stack in two. Remove or rename it."
else
    pf_ok "no root .env"
fi

if DOTNET_VER=$(dotnet --version 2>/dev/null); then
    pf_ok ".NET SDK $DOTNET_VER (satisfies the global.json band)"
else
    pf_bad "\`dotnet --version\` failed. Either the SDK is missing or global.json pins a band this machine cannot satisfy."
fi

if [ -f "$CONFIG" ]; then
    pf_ok "config ${CONFIG#$REPO_ROOT/}"
else
    pf_bad "config file not found: $CONFIG"
fi

# ConfigLoader.ApplyEnvironmentOverrides lets these win over config.yaml, so a Cloud
# export gives a demo talking to Cloud while every gate below checks the local stack.
case "${TEMPORAL_ADDRESS:-}" in
    ''|localhost:7233|127.0.0.1:7233) pf_ok "TEMPORAL_ADDRESS is unset or local" ;;
    *) pf_bad "TEMPORAL_ADDRESS=${TEMPORAL_ADDRESS} overrides config.yaml, so the processes would connect there and not to this stack. Retry with: env -u TEMPORAL_ADDRESS ./scripts/demo-up.sh" ;;
esac
if [ -n "${TEMPORAL_API_KEY:-}" ] || [ -n "${TEMPORAL_TLS_CLIENT_CERT_PATH:-}" ] \
   || [ -n "${TEMPORAL_TLS_CLIENT_KEY_PATH:-}" ]; then
    pf_bad "TEMPORAL_API_KEY or TEMPORAL_TLS_CLIENT_* is set. Those override config.yaml and turn TLS on against a local server that speaks plaintext."
fi
case "${TEMPORAL_NAMESPACE:-}" in
    ''|default) ;;
    *) pf_warn "TEMPORAL_NAMESPACE=${TEMPORAL_NAMESPACE} overrides config.yaml; this stack creates \"default\" and \"repro-local-activity\" and nothing else" ;;
esac

for tool in curl lsof nc; do
    if command -v "$tool" >/dev/null 2>&1; then
        pf_ok "$tool"
    else
        pf_bad "$tool is required and not on PATH"
    fi
done

# Only 8077 and 8078. A busy 7233 means the stack is already up, which is the state we
# want, and compose reports its own error if a binding really collides.
WORKER_STATE=$(port_state worker)
LOADGEN_STATE=$(port_state loadgen)
report_port() {
    local name="$1" state="$2" port
    port=$(demo_field "$name" port)
    case "$state" in
        free)      pf_ok ":$port free" ;;
        ours\ *)   pf_ok ":$port already held by our own $name (pid ${state#ours }), so it will be left alone" ;;
        foreign\ *)
            pf_bad ":$port is held by pid(s) ${state#foreign }, and none of them is a $name we started"
            local pid
            for pid in ${state#foreign }; do
                printf '          %s\n' "$(demo_describe_pid "$pid")"
            done
            printf '        That is usually the \`dotnet run\` child described in docs/GOTCHAS.md. Free it with:\n'
            printf '          kill -9 $(lsof -nP -iTCP:%s -sTCP:LISTEN -t)\n' "$port"
            ;;
    esac
}
report_port worker "$WORKER_STATE"
if [ "$WITH_LOADGEN" -eq 1 ]; then
    report_port loadgen "$LOADGEN_STATE"
else
    pf_ok ":8078 not checked (--no-loadgen)"
fi

if [ "$PF_FAILED" -gt 0 ]; then
    demo_die 3 "$PF_FAILED preflight check(s) failed. Nothing was started."
fi

# 2. Build

demo_phase 2 $PHASES "build"

SKIP_BUILD_REASON=""
if [ -n "${DEMO_SKIP_BUILD:-}" ]; then
    SKIP_BUILD_REASON="DEMO_SKIP_BUILD is set"
else
    # macOS returns ETXTBSY when writing a file that is executing, and AssemblyName
    # makes bin/Debug/net10.0/worker that file. MSBuild fails the apphost copy only when
    # a source file changed, so it looks intermittent.
    case "$WORKER_STATE" in ours\ *) SKIP_BUILD_REASON="the worker binary is running (writing it would fail with ETXTBSY)" ;; esac
    case "$LOADGEN_STATE" in ours\ *) SKIP_BUILD_REASON="the loadgen binary is running (writing it would fail with ETXTBSY)" ;; esac
fi

if [ -n "$SKIP_BUILD_REASON" ]; then
    demo_info "  skipped: $SKIP_BUILD_REASON"
    [ -n "${DEMO_SKIP_BUILD:-}" ] || demo_note "run ./scripts/demo-down.sh first if you need a rebuild"
else
    BUILD_LOG="$DEMO_DIR/build.log"
    BUILD_T0=$SECONDS
    if dotnet build --nologo > "$BUILD_LOG" 2>&1; then
        demo_info "  build ok ($((SECONDS - BUILD_T0))s)"
    else
        tail -40 "$BUILD_LOG" >&2
        demo_die 4 "dotnet build failed. Full log: $BUILD_LOG. Directory.Build.props sets TreatWarningsAsErrors, so a single warning fails this build."
    fi
fi

for name in worker loadgen starter; do
    bin=$(demo_field "$name" bin)
    [ -x "$REPO_ROOT/$bin" ] \
        || demo_die 4 "expected binary $bin is missing or not executable. Check AssemblyName in src/Repro.${name}/*.csproj, or drop DEMO_SKIP_BUILD."
done
demo_info "  three binaries present"

# 3. Containers

demo_phase 3 $PHASES "docker compose up -d"
demo_note "first boot pulls seven images and initialises two Postgres schemas, so it can take several minutes. This step is deliberately not time-bounded."
if ! docker compose up -d; then
    printf '\n' >&2
    docker compose logs --tail 50 temporal temporal-schema-setup >&2 || true
    demo_die 5 "docker compose up -d failed. Its own \"dependency failed to start\" line says nothing useful, so the tail of the two relevant containers is above."
fi

# 4. Readiness gates
#
# `up -d` honours depends_on, so postgres, schema-setup, temporal and prometheus are ready
# when it returns. temporal-create-namespace, pushgateway, grafana and temporal-ui are not:
# nothing depends_on the first two and the last two have no healthcheck. The gates below
# are sufficient even if `up -d` had waited for nothing.

demo_phase 4 $PHASES "readiness gates"

if ! demo_gate "namespaces registered" 180 dotnet-temporal \
        demo_container_exited_ok dotnet-temporal-create-namespace; then
    docker logs --tail 30 dotnet-temporal-create-namespace >&2 || true
    demo_die 5 "the namespaces were never registered. create-namespace.sh retries for 150s of its own, so this budget is 180s."
fi

demo_gate "frontend on :7233" 30 dotnet-temporal demo_tcp_ok 127.0.0.1 7233 \
    || demo_die 5 "the server's healthcheck passes inside the container but 127.0.0.1:7233 is not reachable from this host."

# Both namespaces, by existence rather than by the container's exit code:
# create-namespace.sh skips one that already exists and exits 0, so the gate above passes
# whether it made two, one or none. On a stack predating the local-activity case the first
# symptom is the loadgen failing its 45s gate two phases later with an opaque
# namespace-not-found. Skipped, not failed, when the host `temporal` CLI is absent.
if command -v temporal >/dev/null 2>&1; then
    for ns in default repro-local-activity; do
        if ! demo_gate "namespace ${ns}" 30 dotnet-temporal \
                temporal operator namespace describe -n "${ns}" --address 127.0.0.1:7233; then
            docker logs --tail 30 dotnet-temporal-create-namespace >&2 || true
            demo_die 5 "namespace \"${ns}\" does not exist. create-namespace.sh creates both and skips any that is already there; if only one is missing, that container ran against an older copy of the script. \`docker compose up --force-recreate temporal-create-namespace\` re-runs it."
        fi
    done
else
    pf_warn "temporal CLI not on PATH; skipping the namespace existence check (the create-namespace container still exited 0)"
fi

demo_gate "pushgateway" 60 dotnet-sandbox-pushgateway \
    demo_http_ok "http://127.0.0.1:9091/-/ready" \
    || demo_die 5 "the pushgateway is not ready, and the seed run's final client-metrics push lands there."

# /-/ready, not the /-/healthy the compose healthcheck uses: /-/healthy only means the
# process is alive, /-/ready means the TSDB can answer the phase 6 target gate.
demo_gate "prometheus" 60 dotnet-sandbox-prometheus \
    demo_http_ok "http://127.0.0.1:9090/-/ready" \
    || demo_die 5 "prometheus is not ready"

demo_gate "grafana" "$GATE_TIMEOUT" dotnet-sandbox-grafana \
    demo_http_ok "http://127.0.0.1:3000/api/health" \
    || demo_die 5 "grafana is not answering on :3000"

BOARDS=$(curl -fs --max-time 5 'http://127.0.0.1:3000/api/search?type=dash-db' 2>/dev/null \
    | grep -o '"uid"' | wc -l | tr -d ' ')
BOARDS="${BOARDS:-0}"
if [ "$BOARDS" -ge 10 ]; then
    printf '  %-30s ok (%s)\n' "dashboards provisioned" "$BOARDS"
else
    printf '  %-30s %s of 10\n' "dashboards provisioned" "$BOARDS"
    demo_warn "only $BOARDS dashboards are provisioned. The provisioner rewrites them from files on every boot, so the files in observability/grafana/dashboards/ are the source of truth, not the volume."
fi

if ! demo_gate "web UI on :8080" 60 dotnet-temporal-ui demo_http_ok "http://127.0.0.1:8080/"; then
    demo_warn "the Temporal Web UI is not answering on :8080. Everything else in this demo works without it."
fi

# 5. Host processes

demo_phase 5 $PHASES "worker and loadgen"

start_proc() {
    local name="$1" state="$2"
    local bin port ready log pid rel
    bin=$(demo_field "$name" bin)
    port=$(demo_field "$name" port)
    ready=$(demo_field "$name" ready)
    log=$(demo_log_path "$name")
    rel=${log#$REPO_ROOT/}

    case "$state" in
        ours\ *)
            demo_info "  $name already running (pid ${state#ours }) on :$port; left alone"
            return 0
            ;;
    esac

    demo_info "  starting $name on :$port  ->  $rel"
    demo_launch "$name" "$REPO_ROOT/$bin" "$log" --config "$CONFIG"
    pid=$DEMO_LAUNCHED_PID

    # A bind failure surfaces only in the redirected log, so check the process is
    # still there before waiting 45s for a line it will never print.
    sleep 1
    if ! kill -0 "$pid" 2>/dev/null; then
        printf '\n'
        tail -20 "$log" >&2
        demo_clear_pid "$name"
        demo_die 6 "$name exited immediately. Last 20 lines above; full log: $rel"
    fi

    if ! demo_gate "  $name connected" 45 - grep -qF -- "$ready" "$log"; then
        tail -20 "$log" >&2
        demo_die 6 "$name never logged \"$ready\", so it is not polling. Full log: $rel"
    fi
    if ! demo_gate "  $name exporting" 30 - demo_metrics_live "$port"; then
        demo_die 6 "$name answers on :$port but exports no temporal_* families, which is the TemporalRuntime.Default trap in docs/GOTCHAS.md. Full log: $rel"
    fi
    return 0
}

start_proc worker "$WORKER_STATE"
if [ "$WITH_LOADGEN" -eq 1 ]; then
    start_proc loadgen "$LOADGEN_STATE"
else
    demo_info "  loadgen skipped (--no-loadgen); :8078 is free"
fi

# 6. Prometheus targets

demo_phase 6 $PHASES "prometheus targets"

demo_gate "temporal-sdk-worker" 60 dotnet-sandbox-prometheus demo_pool_up temporal-sdk-worker \
    || demo_die 5 "prometheus never scraped the worker. The listen address must not be loopback: 127.0.0.1:8077 is unreachable from the container over host.docker.internal, and the only symptom is a DOWN target while curl on this host still works."

if [ "$WITH_LOADGEN" -eq 1 ]; then
    demo_gate "temporal-sdk-loadgen" 60 dotnet-sandbox-prometheus demo_pool_up temporal-sdk-loadgen \
        || demo_die 5 "prometheus never scraped the loadgen on :8078"
fi

# Reported, never gated. temporal-server serves hundreds of families against a 900ms
# scrape_timeout and flaps under first-boot load; prometheus and grafana scrape every 15s.
for job in prometheus temporal-server pushgateway grafana; do
    if demo_pool_up "$job"; then
        printf '  %-30s up\n' "$job"
    else
        printf '  %-30s not up yet\n' "$job"
    fi
done
demo_note "those four are reported, not gated. http://localhost:9090/targets has the live answer."

# 7. Summary, printed before the seed run: that blocks for over a minute and can fail,
# and this table is the output the user is waiting for.

demo_phase 7 $PHASES "the demo is live"
demo_url_table
printf '\n'
printf '  logs   %s\n' "${DEMO_DIR#$REPO_ROOT/}/{worker,loadgen,starter}.log"
printf '  stop   ./scripts/demo-down.sh\n'
printf '  boards Grafana needs no login. Open the "sandbox" folder.\n'

# 8. Seed workflow

demo_phase 8 $PHASES "seed workflow $WORKFLOW_ID"
STARTER_BIN=$(demo_field starter bin)
STARTER_LOG=$(demo_log_path starter)
demo_note "60 steps of 1s plus 150ms injected latency, so about 70s at best. fault.failureRate 0.15 with 5 attempts can add retry backoff."

# --restart is mandatory and must be written bare: without it a second demo-up.sh attaches
# to the previous repro-workflow. It is in Flags.Switches, so `--restart=true` is an error.
demo_launch_attached starter "$REPO_ROOT/$STARTER_BIN" "$STARTER_LOG" \
    --restart --config "$CONFIG"
SEED_PID=$DEMO_LAUNCHED_PID

seed_progress() {
    command -v temporal >/dev/null 2>&1 || return 0
    local line
    line=$(temporal workflow describe -w "$WORKFLOW_ID" 2>/dev/null \
        | grep -i 'HistoryLength' | head -1 | tr -s ' ' || true)
    [ -n "$line" ] && printf ', %s' "$line"
    return 0
}

SEED_T0=$SECONDS
SEED_TIMED_OUT=0
while kill -0 "$SEED_PID" 2>/dev/null; do
    seed_elapsed=$((SECONDS - SEED_T0))
    if [ "$seed_elapsed" -ge "$STARTER_TIMEOUT" ]; then
        SEED_TIMED_OUT=1
        demo_warn "the seed workflow has not finished after ${STARTER_TIMEOUT}s. Sending SIGINT, which is the starter's graceful path: it cancels the workflow, settles, and still pushes its client metrics."
        demo_signal INT "$SEED_PID"
        demo_wait_gone 15 "$SEED_PID" || demo_signal KILL "$SEED_PID"
        break
    fi
    if [ "$seed_elapsed" -gt 0 ] && [ $((seed_elapsed % 15)) -eq 0 ]; then
        printf '  %ss elapsed%s\n' "$seed_elapsed" "$(seed_progress)"
    fi
    sleep 1
done

SEED_RC=0
wait "$SEED_PID" 2>/dev/null || SEED_RC=$?
demo_clear_pid starter

if [ "$SEED_RC" -eq 0 ]; then
    grep -E 'started workflowId|result:' "$STARTER_LOG" 2>/dev/null | sed 's/^/  /' || true
    printf '\n'
    demo_info "Demo is up and one full run has completed. Open http://localhost:3000"
    exit 0
fi

tail -10 "$STARTER_LOG" >&2 || true
if [ "$SEED_TIMED_OUT" -eq 1 ]; then
    demo_die 6 "the seed workflow did not finish inside ${STARTER_TIMEOUT}s. The stack and both processes are still up; ./scripts/demo-down.sh stops them."
fi
demo_die 6 "the seed workflow exited $SEED_RC. At fault.failureRate 0.15 with activity.retry.maximumAttempts 5 a genuinely failed workflow is about 1 in 13,000, so this is real rather than flake. The stack is still up; full log: ${STARTER_LOG#$REPO_ROOT/}"
