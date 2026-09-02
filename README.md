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

Two namespaces, three task queues. `default` holds four of the five workflows, with
`WorkflowFileScan` on `repro-scan-queue` inside it rather than in a namespace of its own.
`repro-local-activity` holds `WorkflowLocalActivity` alone, because the server setting that
case depends on, `history.workflowTaskHeartbeatTimeout`, can only be scoped per namespace.
Both namespaces are created by `demo-up.sh` and both are gated on before it declares
readiness.

[docs/DEMO.md](docs/DEMO.md) has every flag, the exit codes, and the four-terminal
manual sequence, which still works and is still the right tool for a debugger or a
second worker.

| URL | What |
|---|---|
| <http://localhost:3000> | Grafana, 10 dashboards |
| <http://localhost:8080> | Temporal Web UI |
| <http://localhost:9090/targets> | Prometheus target health, all 6 should be UP |
| <http://localhost:9091> | Pushgateway |
| <http://localhost:8000/metrics> | Temporal server metrics |
| <http://localhost:8077/metrics> | worker SDK metrics |

## Dashboards

The `sandbox` folder holds 6 boards written for this topology, 80 panels, 118 targets.

| Dashboard | Source | What it answers |
|---|---|---|
| Repro / Worker Health | SDK | Are slots exhausted? Are pollers alive? How long did tasks wait? |
| Repro / Server and Persistence | server | Frontend RPS and latency, persistence latency, backlog, sync-match ratio |
| Repro / Bug Signals | both | Non-determinism, workflow task retries, sticky cache, replay pressure, injected faults, simple-activity outcomes and weather source |
| **Repro / Heartbeating** | both | Heartbeat RPC rate vs call rate, the throttle, checkpoint staleness, cancellation reasons, timeouts |
| **Repro / Local Activity** | both | Executions vs completions (the gap is wasted CPU), workflow task heartbeat timeouts, local-activity slots. Opens on the `repro-local-activity` namespace and a 3h window |
| **Repro / File Scan** | both | Row cursor vs resume floor vs corpus ceiling (every drop is redone work), the idempotency verdict, checkpoint staleness, then what the scan costs the worker: heap, LOH, RSS, GC collections and pause time, allocation amplification. Empty until you generate a corpus |

Four more boards are imported from
[temporalio/dashboards](https://github.com/temporalio/dashboards) as-is, pinned to
commit `4994df2`. See [docs/DASHBOARDS.md](docs/DASHBOARDS.md) for how every panel was
probed and which imported ones are known-empty.

## Make the panels move

`config.yaml` ships `fault.failureRate: 0.15` and `fault.latency: 150ms` on, so the
failure and latency panels move from the first run. Zero both for a clean baseline.

Six faults ship off. Three are for the seed heartbeating activity:
`stallPastHeartbeatTimeout`, `stopHeartbeating` and `ignoreCancellation`. Three are a ladder
for the file scan: `decodeRowsToStrings`, `retainScannedRows` and `slurpWholeFile`, which
move allocation from a measured 0.01x of bytes read to 2.41x, 2.54x and 8.63x respectively,
and each moves a different named panel. Each of the six proves one specific claim, so turn
on exactly one at a time, then `./scripts/demo-down.sh --keep-stack &&
./scripts/demo-up.sh` to restart the host processes without rebooting Temporal. See
[docs/HEARTBEATING.md](docs/HEARTBEATING.md).

`SimpleNoActivity` and `WorkflowSimpleActivity` have their own levers.
`simple.overflowRate` and `simple.raceRate` ship on, so rejected updates and post-close
message races are already in the numbers. Point `simpleActivity.baseUrl` at `http://127.0.0.1:1/forecast` to make
`source="synthetic"` appear on Bug Signals, and add `simpleActivity.requireLiveWeather:
true` to turn that into `outcome="failed"` instead. Full list in
[docs/DASHBOARDS.md](docs/DASHBOARDS.md).

## Start a new repro

```bash
git checkout main && git checkout -b repro/<short-name>
```

Then edit `HeartbeatWorkflow.workflow.cs` and `HeartbeatActivities.cs`. Adjust
`config.yaml` for the task queue, workflow ID, job shape and faults. Commit the history
JSON that demonstrates the bug. It is the artifact worth keeping.

`HeartbeatWorkflow` is the seed case and the one to edit, but it is not always the right
starting point. Four others ship. `SimpleNoActivity` if the bug is about signals, queries,
updates or cancellation, and `WorkflowSimpleActivity` if it is about a plain
non-heartbeating activity, its retry policy or its start-to-close timeout — both on the same
worker and task queue. `WorkflowLocalActivity` is somewhere else entirely: its own
namespace, its own task queue, its own worker, because the server setting it depends on can
only be scoped per namespace. Reach for it if the bug is about local activities, markers, or
a workflow task that will not stay alive. `WorkflowFileScan` is the fifth, on
`repro-scan-queue` in `default` and on a worker of its own: reach for it if the bug is
about a genuinely long activity, checkpoint-and-resume correctness, or what the activity
costs the worker in memory and GC, which is the one thing the other four cannot show at all.
[docs/WORKFLOWS.md](docs/WORKFLOWS.md) puts all five next to each other.

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
| [docs/WORKFLOWS.md](docs/WORKFLOWS.md) | The five workflows side by side, their message handlers and outcome vocabularies, and all 35 `repro_*` metrics with their tags |
| [docs/DEMO.md](docs/DEMO.md) | The two scripts: every phase, flag and exit code, and the six things they do differently from the manual path |
| [docs/HEARTBEATING.md](docs/HEARTBEATING.md) | The throttle, stale checkpoints, both `kill -9` resume tests, the six fault knobs, the `temporal activity` verbs |
| [docs/GOTCHAS.md](docs/GOTCHAS.md) | 44 .NET and Core behaviors that look exactly like bugs, worst first. Read before you conclude a panel is broken |
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
                    constants, histogram bucket overrides in milliseconds, and
                    ProcessPressure.cs: one coherent read of heap/LOH/RSS/GC per sample,
                    with a compare-exchange watermark over the cumulative ones
  Workflows/HeartbeatWorkflow.workflow.cs   seed workflow    <- edit per repro
  Workflows/SimpleNoActivity.workflow.cs    NO activities: signal, query, update, cancel
  Workflows/WorkflowSimpleActivity.workflow.cs  ONE activity, NO heartbeats: plain
                                            start-to-close + retry, result in history
  Workflows/WorkflowLocalActivity.workflow.cs   ONE LOCAL activity, CPU-bound: a marker
                                            instead of an activity task, in its OWN namespace
  Workflows/WorkflowFileScan.workflow.cs    ONE long heartbeating activity over a real
                                            file: exact byte cursor, idempotent resume,
                                            closed-form verdict. Its OWN task queue
  Activities/HeartbeatActivities.cs         seed activity    <- edit per repro
  Activities/WeatherActivities.cs           Open-Meteo fetch + synthetic offline fallback
  Activities/PiActivities.cs                Monte Carlo Pi burn; the repo's only SYNC activity
  Activities/FileScanActivities.cs          raw-byte corpus scan, wire name ScanFile; the
                                            resume path plus the three pressure fault knobs
  HeartbeatJob.cs      JobInput, ActivityOptionsInput, Checkpoint
  SimpleJob.cs         SimpleInput, PokeInput, AddInput, SimpleResult, SimpleStatus
  SimpleActivityJob.cs SimpleActivityInput, SimpleActivityOptionsInput, WeatherReading
  LocalActivityJob.cs  LocalActivityInput, LocalActivityOptionsInput, PiEstimate
  FileScanJob.cs       FileScanInput, FileScanOptionsInput, FileScanCheckpoint,
                       FileScanResult
src/Repro.Worker    THREE workers: the main queue, the scan queue on the same client, and
                    the local-activity queue on a second client. Serves :8077
src/Repro.LoadGen   THREE workers (one per task queue, across two namespaces) + FIVE start
                    loops (heartbeat; simple with chaos; simple-activity; local-activity
                    with a per-run duration draw; file scan, which skips itself with a
                    banner if the corpus is absent), serves :8078
src/Repro.Starter   one run, prints result, pushes metrics on exit. `--file-scan` starts
                    one scan instead, which is how the resume test is run
                    (owns Telemetry/PushMetrics.cs, the Pushgateway bridge)
src/Repro.Replay    replays a history JSON or a directory, exits 1 on mismatch
tests/Repro.Tests   config, duration, bind-address and flag parsing, histogram bucket
                    key collisions, dashboard metric names, and the file-scan row parser,
                    checkpoint identity and closed forms against the real corpus numbers
scripts/            demo-up.sh, demo-down.sh, the bash 3.2 library they share, and
                    gen-samples/ which generates the four corpora
sample_files/       generated corpora, GITIGNORED. A fresh clone has none, and nothing
                    fails: the loadgen skips its scan loop with a named banner
docs/               the seven topic files above
history/            captured histories (committed)
compose.yml         root entry point; includes observability/compose.yml
observability/      compose stack, Prometheus, Grafana, dashboards
```
