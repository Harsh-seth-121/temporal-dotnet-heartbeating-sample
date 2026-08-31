# temporal-dotnet-heartbeating-sample

Sandbox for reproducing Temporal .NET SDK behavior locally, built around a long-running
heartbeating activity. Server metrics, worker SDK metrics, one-shot client metrics and
custom in-workflow metrics all flow into Grafana.

One repro case lives at HEAD. Each new case gets its own branch.

## Prerequisites

Docker Desktop (running), the .NET 10 SDK, and the `temporal` CLI. `global.json` pins
the SDK band, `observability/.env` pins the server and UI images. Nothing else is
installed globally.

The four .NET processes run on the **host**, not in containers. Prometheus reaches them
over `host.docker.internal`. `demo-up.sh` starts three of them; `Repro.Replay` stays
manual.

## Run it

```bash
./scripts/demo-up.sh      # everything, and it waits until the demo is real
./scripts/demo-down.sh    # drain the host processes, then remove the containers
```

`demo-up.sh` builds, starts the eight containers, waits for the namespace and for
Grafana, starts the worker on :8077 and the loadgen on :8078 as detached processes with
logs in `.demo/`, waits until Prometheus has actually scraped both, then runs one
60-step seed workflow. About 90s warm, several minutes on a cold first boot.

Then open <http://localhost:3000>, which needs no login, and look at the `sandbox`
folder.

[docs/DEMO.md](docs/DEMO.md) has every flag, the exit codes, and the four-terminal
manual sequence, which still works and is still the right tool for a debugger or a
second worker.

| URL | What |
|---|---|
| <http://localhost:3000> | Grafana, 8 dashboards |
| <http://localhost:8080> | Temporal Web UI |
| <http://localhost:9090/targets> | Prometheus target health, all 6 should be UP |
| <http://localhost:9091> | Pushgateway |
| <http://localhost:8000/metrics> | Temporal server metrics |
| <http://localhost:8077/metrics> | worker SDK metrics |

## Dashboards

The `sandbox` folder holds 4 boards written for this topology, 57 panels, 84 targets.

| Dashboard | Source | What it answers |
|---|---|---|
| Repro / Worker Health | SDK | Are slots exhausted? Are pollers alive? How long did tasks wait? |
| Repro / Server and Persistence | server | Frontend RPS and latency, persistence latency, backlog, sync-match ratio |
| Repro / Bug Signals | both | Non-determinism, workflow task retries, sticky cache, replay pressure, injected faults, simple-activity outcomes and weather source |
| **Repro / Heartbeating** | both | Heartbeat RPC rate vs call rate, the throttle, checkpoint staleness, cancellation reasons, timeouts |

Four more boards are imported from
[temporalio/dashboards](https://github.com/temporalio/dashboards) as-is, pinned to
commit `4994df2`. See [docs/DASHBOARDS.md](docs/DASHBOARDS.md) for how every panel was
probed and which imported ones are known-empty.

## Make the panels move

`config.yaml` ships `fault.failureRate: 0.15` and `fault.latency: 150ms` on, so the
failure and latency panels move from the first run. Zero both for a clean baseline.

Three heartbeat faults ship off: `stallPastHeartbeatTimeout`, `stopHeartbeating` and
`ignoreCancellation`. Each proves one specific claim. Turn on exactly one at a time,
then `./scripts/demo-down.sh --keep-stack && ./scripts/demo-up.sh` to restart the host
processes without rebooting Temporal. See
[docs/HEARTBEATING.md](docs/HEARTBEATING.md).

## Start a new repro

```bash
git checkout main && git checkout -b repro/<short-name>
```

Then edit `HeartbeatWorkflow.workflow.cs` and `HeartbeatActivities.cs`. Adjust
`config.yaml` for the task queue, workflow ID, job shape and faults. Commit the history
JSON that demonstrates the bug. It is the artifact worth keeping.

## Reset

```bash
./scripts/demo-down.sh              # keep all data
./scripts/demo-down.sh --volumes    # full reset; REQUIRED if you change NUM_HISTORY_SHARDS

# clear a stale one-shot starter push. Only needed after --keep-stack: a plain down
# removes the Pushgateway container and the group with it.
dotnet run --project src/Repro.Starter -- --delete-push-group
# or:  curl -X DELETE localhost:9091/metrics/job/temporal_starter/instance/local

# .NET build state
dotnet clean && rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj
```

## Docs

| File | What is in it |
|---|---|
| [docs/DEMO.md](docs/DEMO.md) | The two scripts: every phase, flag and exit code, and the six things they do differently from the manual path |
| [docs/HEARTBEATING.md](docs/HEARTBEATING.md) | The throttle, stale checkpoints, the `kill -9` resume test, the three fault knobs, the `temporal activity` verbs |
| [docs/GOTCHAS.md](docs/GOTCHAS.md) | 29 .NET and Core behaviors that look exactly like bugs, worst first. Read before you conclude a panel is broken |
| [docs/CONFIG.md](docs/CONFIG.md) | Every `config.yaml` field, and why activity options travel through the workflow input rather than being read from the file |
| [docs/DASHBOARDS.md](docs/DASHBOARDS.md) | Probing every panel, the every-target-renders result, known-empty imported panels |
| [docs/REPLAY.md](docs/REPLAY.md) | Capture a history, catch a nondeterminism error, and why the replayer emits no metrics |
| [observability/README.md](observability/README.md) | The compose stack itself: bring it up and down, what each process emits on which port |

## Layout

```
src/Repro.Core/     the library everything else references
  Config/           POCOs + defaults, YAML load and startup validation, "1m30s" parsing,
                    bind-address normalize + loopback reject
  Cli/Flags.cs      hand-rolled arg parser; unknown flags are errors, and so is
                    `--switch=value` (Go's `-restart=false`)
  Temporal/         ConnectAsync, API key and mTLS paths
  Telemetry/        the ONE TemporalRuntime + Core Prometheus exporter, metric name
                    constants, histogram bucket overrides in milliseconds
  Workflows/HeartbeatWorkflow.workflow.cs   seed workflow    <- edit per repro
  Workflows/SimpleNoActivity.workflow.cs    NO activities: signal, query, update, cancel
  Workflows/WorkflowSimpleActivity.workflow.cs  ONE activity, NO heartbeats: plain
                                            start-to-close + retry, result in history
  Activities/HeartbeatActivities.cs         seed activity    <- edit per repro
  Activities/WeatherActivities.cs           Open-Meteo fetch + synthetic offline fallback
  HeartbeatJob.cs      JobInput, ActivityOptionsInput, Checkpoint
  SimpleJob.cs         SimpleInput, PokeInput, AddInput, SimpleResult, SimpleStatus
  SimpleActivityJob.cs SimpleActivityInput, SimpleActivityOptionsInput, WeatherReading
src/Repro.Worker    polls until interrupted, serves :8077
src/Repro.LoadGen   worker + THREE start loops (heartbeat; simple with jitter and
                    injected chaos; simple-activity with jitter), serves :8078
src/Repro.Starter   one run, prints result, pushes metrics on exit
                    (owns Telemetry/PushMetrics.cs, the Pushgateway bridge)
src/Repro.Replay    replays a history JSON or a directory, exits 1 on mismatch
tests/Repro.Tests   config, duration, bind-address and flag parsing, histogram bucket
                    key collisions, dashboard metric names
scripts/            demo-up.sh, demo-down.sh, and the bash 3.2 library they share
docs/               the six topic files above
history/            captured histories (committed)
compose.yml         root entry point; includes observability/compose.yml
observability/      compose stack, Prometheus, Grafana, dashboards
```
