# temporal-dotnet-heartbeating-sample

Sandbox for reproducing Temporal .NET SDK behavior locally, built around a
long-running **heartbeating** activity. Server metrics, worker SDK metrics,
one-shot client metrics, and custom in-workflow metrics all flow into Grafana.

One repro case lives at HEAD; each new case gets its own branch.

## Prerequisites

Docker Desktop (running), the .NET 10 SDK, and the `temporal` CLI. `global.json`
pins the SDK band; `observability/.env` pins the server and UI images. Nothing
else is installed globally. Prometheus, Grafana, Pushgateway, the Temporal server,
and PostgreSQL all run as containers configured entirely from files in
`observability/`.

The four .NET processes run on the **host**, not in containers. Prometheus reaches
them over `host.docker.internal`.

## Loop

```bash
# 0. once, so step 2 is not a silent 30-second NuGet restore
dotnet build

# 1. the whole stack: Temporal server + Postgres + Web UI + Prometheus + Grafana + Pushgateway
docker compose up -d

# 2. worker (SDK metrics on :8077)
dotnet run --project src/Repro.Worker

# 3. continuous traffic, so dashboards have data (SDK metrics on :8078)
dotnet run --project src/Repro.LoadGen

# 4. one-shot run; pushes its client metrics to the Pushgateway on exit
dotnet run --project src/Repro.Starter
```

Then open <http://localhost:3000>, which needs no login, and look at the
`sandbox` folder.

| URL | What |
|---|---|
| <http://localhost:3000> | Grafana, 8 dashboards |
| <http://localhost:8080> | Temporal Web UI |
| <http://localhost:9090/targets> | Prometheus target health, all 6 should be UP |
| <http://localhost:9091> | Pushgateway |
| <http://localhost:8000/metrics> | Temporal server metrics |
| <http://localhost:8077/metrics> | worker SDK metrics |

## Dashboards

The `sandbox` folder holds boards written for this topology: 4 dashboards, 55
panels, 82 targets. Every one of the 82 was probed against a live stack before it
shipped — see [Proving the boards](observability/README.md#proving-the-boards).

| Dashboard | Source | What it answers |
|---|---|---|
| Repro / Worker Health | SDK | Are slots exhausted? Are pollers alive? How long did tasks wait? |
| Repro / Server and Persistence | server | Frontend RPS and latency, persistence latency, backlog, sync-match ratio |
| Repro / Bug Signals | both | Non-determinism, workflow task retries, sticky cache, replay pressure, injected faults |
| **Repro / Heartbeating** | both | Heartbeat RPC rate vs call rate, the throttle, checkpoint staleness, cancellation reasons, timeouts |

The `temporal-server` and `temporal-sdk` folders hold boards imported from
[temporalio/dashboards](https://github.com/temporalio/dashboards) as-is, for
breadth, pinned to commit `4994df2`.

Start with **Heartbeating**. It has no upstream equivalent and it is the reason to
build rather than only import — there is no heartbeat metric in any Core-based
SDK, so every panel on it is either a proxy, a server-side consequence, or
something this repo emits itself.

## Making the dashboards move

`config.yaml` ships with the first two faults already on, so the failure and
latency panels move from the first run:

```yaml
fault:
  failureRate: 0.15     # fraction of activity ATTEMPTS that fail (retryable)
  latency: 150ms        # added to every step
```

Zero both for a clean baseline. The three heartbeat faults ship **off**, and each
one proves a specific claim. Turn on exactly one at a time:

| Knob | What it proves | Watch |
|---|---|---|
| `stallPastHeartbeatTimeout` | The server can only tell an activity to stop via the **response to a heartbeat RPC**. Stop heartbeating and the attempt is timed out server-side while your code keeps running, oblivious. | Heartbeating board, "Heartbeat timeouts"; Bug Signals outcome split shifting to `timed_out` |
| `stopHeartbeating` | An activity that stops heartbeating can **never be cancelled**. | Heartbeating board: heartbeat RPC rate falls to zero while `repro_activity_progress` keeps climbing |
| `ignoreCancellation` | `TemporalWorker.ExecuteAsync` does not return until every executing activity returns, and `gracefulShutdownTimeout` does **not** bound that — it only controls *when* `ctx.CancellationToken` fires. | Ctrl-C the worker and watch it refuse to exit |

Restart the worker or loadgen after editing, and watch. Measured on this stack
with `failureRate: 0.15`, `latency: 150ms`, loadgen at `--rate 2s --concurrency 8
--steps 12 --step-duration 400ms`: injected failures ~1.0/s, retried attempts
~1.0/s, activity execution p95 ~7.2 s, workflow end-to-end p95 ~28.5 s, and the
outcome split a mix of `completed` and `failed`.

## The heartbeat story

This is what the repo is for.

**Heartbeats are throttled.** Core sends at most one heartbeat RPC every
`min(HeartbeatTimeout × 0.8, MaxHeartbeatThrottleInterval)`, no matter how often
your code calls `Heartbeat()`. At the shipped `heartbeatTimeout: 5s` that is one
every 4 seconds while the activity calls it every 400 ms. The Heartbeating board
plots both rates against `repro_heartbeat_throttle_ms` so you can see the gap.

**So the checkpoint the server holds is stale**, and a resumed attempt redoes
work. The activity puts a timestamp in every heartbeat and records
`repro_heartbeat_staleness` on resume, which makes the cost measurable rather than
theoretical. Observed on this stack: **5.8 s, 6.3 s, 12.7 s**. Note those exceed
the 4 s throttle bound — staleness is throttle lag **plus retry backoff**, and the
larger values are third and fourth attempts.

**Resume must therefore be idempotent.** Watch it happen:

```
resuming at step 2 of 12; checkpoint was 6265ms old (attempt 3)
```

Kill the worker mid-activity and restart it to see the same thing:

```bash
# Run the BUILT BINARY, not `dotnet run` -- `dotnet run` launches the app as a
# CHILD process, so killing the parent leaves the child running and holding :8077.
./src/Repro.Worker/bin/Debug/net10.0/worker

# from another terminal, once you see "Progress: 30" -- note that number, call it M
kill $(lsof -ti tcp:8077)
./src/Repro.Worker/bin/Debug/net10.0/worker
# the resume line reports a step BELOW M. The difference is the staleness.
```

## Capture and replay a history

```bash
temporal workflow show --workflow-id repro-workflow --output json > history/heartbeat-job.json
dotnet run --project src/Repro.Replay -- --history history/heartbeat-job.json
```

There is **no `--fields` flag on `workflow show`** — that belongs to
`workflow list` and `workflow describe`. Plain `--output json` is what the
replayer consumes; `WorkflowHistory.FromJson` handles the CLI's enum shorthands
itself.

Change `HeartbeatWorkflow.workflow.cs`, replay the old history, and a replay error
tells you the change is not backward compatible. Measured, after inserting a
`Workflow.DelayAsync` before the activity call:

```
Temporalio.Exceptions.WorkflowNondeterminismException: Nondeterminism:
  [TMPRL1100] Nondeterminism error: Timer machine does not handle this event:
  HistoryEvent(id: 5, ActivityTaskScheduled)
```

Exit code is 1. Match on the **type** (`WorkflowNondeterminismException`, a
subclass of `InvalidWorkflowOperationException`), not on the message. The
`TMPRL1100` code does appear, but it comes from the Rust Core, not the managed
SDK — you will not find that string anywhere in `sdk-dotnet`.

`--history` also accepts a directory, and replays every `*.json` in it.

History JSON format is tied to server version. Recapture rather than reuse a
history across a server upgrade.

**The replayer emits no metrics — and that is worse in .NET than in Go.** Go's
replayer hard-codes a no-op metrics handler, so you cannot even try.
`WorkflowReplayerOptions` *does* accept a real `TemporalRuntime`, which looks like
an improvement and is a trap: Core starts a real HTTP listener and `/metrics`
answers **200 with a zero-byte body**. Point a Prometheus job at it and the target
reads UP forever while every panel stays blank.

Measured, and reproducible:

```bash
# :8079 is not scraped by Prometheus, so nothing else can contaminate the result.
# --metrics holds the endpoint open for 30s, since a replay itself takes ms.
dotnet run --project src/Repro.Replay -- --history history/ --metrics 0.0.0.0:8079
curl -sS -o /dev/null -w '%{http_code} %{size_download}\n' localhost:8079/metrics
# 200 0
```

Do not spend time instrumenting replay.

## Starting a new repro

```bash
git checkout main && git checkout -b repro/<short-name>
```

Then edit `HeartbeatWorkflow.workflow.cs` and `HeartbeatActivities.cs`. Adjust
`config.yaml` for the task queue, workflow ID, job shape, and faults. Commit the
history JSON that demonstrates the bug. It is the artifact worth keeping.

## Poking a live execution

The worker stays running, so the `temporal` CLI reaches in-flight runs:

```bash
temporal workflow describe -w repro-workflow
temporal workflow cancel   -w repro-workflow
temporal workflow signal   -w repro-workflow --name <signal> --input '"payload"'
temporal workflow query    -w repro-workflow --type <query>
```

For a heartbeating activity the interesting verbs are the activity ones:

```bash
temporal activity pause   -w repro-workflow --activity-id <id>
temporal activity unpause -w repro-workflow --activity-id <id>
# restart the attempt from scratch AND discard the resume point:
temporal activity reset   -w repro-workflow --activity-id <id>
```

`temporal workflow cancel` is the one to try first. Because the activity is
scheduled with `CancellationType.WaitCancellationCompleted`, the workflow does not
report cancelled until the activity has actually observed the request on its next
heartbeat response and unwound. Watch `repro_activity_cancel{reason=...}` on the
Heartbeating board — the reason is Core's own `ActivityCancelReason`.

`temporal workflow describe` prints no `Status` line for open runs, only closed
ones. Use `HistoryLength` to tell a queued run from a finished one.

## Reset

```bash
docker compose down       # keep all data
docker compose down -v    # full reset; REQUIRED if you change NUM_HISTORY_SHARDS

# clear a stale one-shot starter push (Pushgateway retains groups forever)
dotnet run --project src/Repro.Starter -- --delete-push-group
# or:  curl -X DELETE localhost:9091/metrics/job/temporal_starter/instance/local

# .NET build state
dotnet clean && rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj
```

Grafana state is disposable. The provisioner rewrites dashboards and the
datasource from files on every boot, so
`docker volume rm temporal-dotnet-sandbox_grafana-data` loses nothing but UI-side
edits.

## Config

Everything lives in `config.yaml`; all fields are optional and fall back to the
defaults in `src/Repro.Core/Config/ReproConfig.cs`. Durations are Go-style strings
(`150ms`, `10s`, `1m30s`, `0`).

**Unknown keys are a hard error.** A misspelled `failurRate` that quietly means
`0.0` is an afternoon spent staring at a flat panel.

| Field | Default | Purpose |
|---|---|---|
| `address` | `localhost:7233` | gRPC endpoint |
| `namespace` | `default` | namespace |
| `taskQueue` | `repro-task-queue` | task queue for all binaries |
| `workflowId` | `repro-workflow` | ID the starter uses |
| `apiKey` | *(empty)* | Temporal Cloud API key; enables TLS |
| `tls.certPath` / `tls.keyPath` | *(empty)* | mTLS pair; enables TLS |
| `tls.serverName` | *(empty)* | maps to `TlsOptions.Domain` |
| `tls.serverCaPath` | *(empty)* | server root CA for self-hosted TLS |
| `metrics.listenAddress` | `0.0.0.0:8077` | worker SDK metrics endpoint |
| `metrics.loadgenAddress` | `0.0.0.0:8078` | loadgen SDK metrics endpoint |
| `metrics.pushgatewayUrl` | `http://localhost:9091/metrics` | where the starter pushes; **must end in `/metrics`** |
| `metrics.pushJob` / `pushInstance` | `temporal_starter` / `local` | push grouping key; keep stable |
| `metrics.pushSettle` | `2s` | wait before the starter's final push |
| `job.steps` / `job.stepDuration` | `60` / `1s` | shape of the seed job |
| `activity.heartbeatTimeout` | `5s` | required for cancellation; drives the throttle |
| `activity.startToCloseTimeout` | `10m` | per attempt |
| `activity.scheduleToCloseTimeout` | `1h` | all attempts |
| `activity.retry.*` | `1s` / `2.0` / `10s` / `5` | initial, coefficient, max interval, max attempts |
| `worker.gracefulShutdownTimeout` | `30s` | SDK default is `0s`; see the fault table |
| `worker.maxHeartbeatThrottleInterval` | `60s` | upper bound on the throttle |
| `worker.defaultHeartbeatThrottleInterval` | `30s` | used when the timeout is unset |
| `worker.maxCachedWorkflows` | `0` (SDK default 10000) | set to `1` to force evictions and replay storms |
| `worker.maxConcurrentActivities` / `maxConcurrentWorkflowTasks` | `0` (SDK default 100) | slot counts |
| `loadgen.rate` / `concurrency` / `steps` | `5s` / `8` / `20` | traffic shape |
| `fault.failureRate` | `0`, shipped as `0.15` | fraction of activity attempts that fail |
| `fault.latency` | `0`, shipped as `150ms` | latency added per step |
| `fault.stallPastHeartbeatTimeout` | `false` | overrun the heartbeat timeout on attempt 1 |
| `fault.stopHeartbeating` | `false` | keep working, stop heartbeating |
| `fault.ignoreCancellation` | `false` | swallow cancellation and wedge shutdown |

Metrics listen addresses must be a full `IP:port` that is **not** loopback. Go's
`":8077"` is accepted and normalized, but Core parses these with Rust's
`SocketAddr`, which rejects a bare `:port`; and `127.0.0.1` is unreachable from
the Prometheus container while `curl localhost:8077` on the host still works.
Both are rejected at startup with an explanation rather than left to fail later.

Keep secrets out of the committed file. Put them in `config.local.yaml`
(gitignored) and pass `--config config.local.yaml` to any binary.

## Layout

```
src/Repro.Core/                 the library everything else references
  Config/ReproConfig.cs         Config POCOs + defaults
  Config/ConfigLoader.cs        YAML load, env overrides, startup validation
  Config/GoDuration.cs          "150ms" / "1m30s" parsing
  Config/BindAddress.cs         normalize + reject loopback binds
  Cli/Flags.cs                  hand-rolled arg parser; unknown flags are errors
  Temporal/ClientFactory.cs     ConnectAsync, API key and mTLS paths
  Telemetry/ReproRuntime.cs     the ONE TemporalRuntime; Core Prometheus exporter
  Telemetry/MetricNames.cs      custom metric names as constants
  Telemetry/HistogramBuckets.cs bucket overrides, in milliseconds
  Workflows/HeartbeatWorkflow.workflow.cs   seed workflow    <- edit per repro
  Activities/HeartbeatActivities.cs         seed activity    <- edit per repro
  HeartbeatJob.cs               JobInput, Checkpoint
src/Repro.Worker    polls until interrupted, serves :8077
src/Repro.LoadGen   worker + continuous start loop, serves :8078
src/Repro.Starter   one run, prints result, pushes metrics on exit
                    (owns Telemetry/PushMetrics.cs, the Pushgateway bridge)
src/Repro.Replay    replays a history JSON or a directory of them, exits 1 on mismatch
tests/Repro.Tests   config, duration and bind-address parsing
history/            captured histories (committed)
compose.yml         root entry point; includes observability/compose.yml
observability/      compose stack, Prometheus, Grafana, dashboards
```

[`observability/README.md`](observability/README.md) documents the gotchas worth
knowing before you debug the stack itself. Read it before you conclude a panel is
broken — several .NET-specific behaviors look exactly like bugs.
