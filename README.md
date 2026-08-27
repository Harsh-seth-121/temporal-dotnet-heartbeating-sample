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
  failureRate: 0.15     # ONE roll per activity ATTEMPT; a hit is a retryable failure
  latency: 150ms        # added to every step
```

Zero both for a clean baseline. The three heartbeat faults ship **off**, and each
one proves a specific claim. Turn on exactly one at a time:

| Knob | What it proves | Watch |
|---|---|---|
| `stallPastHeartbeatTimeout` | The server can only tell an activity to stop via the **response to a heartbeat RPC**. Stop heartbeating and the attempt is timed out server-side while your code keeps running, oblivious. | Heartbeating board, "Heartbeat timeouts", and **nothing else**. It is gated to attempt 1, so attempt 2 runs normally and the workflow still ends `completed` |
| `stopHeartbeating` | An activity that stops heartbeating can **never be cancelled** — and because this one is not gated to attempt 1, it starves all five attempts. It is the knob that produces `outcome=timed_out`. | Heartbeating board: heartbeat RPC rate falls to zero while `repro_activity_progress` climbs, then Bug Signals shifts to `timed_out`. Watch the gauge against ONE execution — the starter, or loadgen `--concurrency 1`. It is a single series per worker process and the last writer wins |
| `ignoreCancellation` | `TemporalWorker.ExecuteAsync` does not return until every executing activity returns, and `gracefulShutdownTimeout` does **not** bound that — it only controls *when* `ctx.CancellationToken` fires. | Ctrl-C the worker and watch it refuse to exit |

Restart the worker or loadgen after editing, and watch. Measured over 9.5 minutes
with `failureRate: 0.15`, `latency: 150ms` and loadgen at `--rate 2s --concurrency 8
--steps 12 --step-duration 400ms`: 280 workflows at 0.49/s, 322 activity attempts, 40
injected failures and 39 retried attempts — **0.124 of attempts failed, converging on
the configured 0.15** — so roughly 0.07/s of each.

The outcome split is **all `completed`**, and that is correct rather than broken.
`failureRate` is one roll per ATTEMPT, so a workflow only fails when all five
attempts roll a failure: `0.15^5`, about one in thirteen thousand. Push
`failureRate` to `0.8` if you want `failed` on the Bug Signals board — `0.8^5` is
one workflow in three. (An earlier build rolled once per STEP, which made
P(attempt fails) `1 - (1 - r)^steps`: 86% at this loadgen shape and 99.99% at the
shipped `job.steps: 60`. Any number you remember from before that fix is wrong by
more than an order of magnitude.)

Do not read much into the two latency p95s at this shape. Twelve steps of 550 ms is
a 6.6 s activity, which lands whole inside the single `[5 s, 10 s)` override bucket,
so `histogram_quantile` interpolates ~9.7 s from one bucket's worth of information.
That is the failure mode `HistogramBuckets.cs` exists to prevent, relocated: bucket
edges have to straddle the durations you actually run.

## The heartbeat story

This is what the repo is for.

**Heartbeats are throttled.** Core sends at most one heartbeat RPC every
`min(HeartbeatTimeout × 0.8, MaxHeartbeatThrottleInterval)`, no matter how often
your code calls `Heartbeat()`. At the shipped `heartbeatTimeout: 5s` that is one
every 4 seconds, while the activity calls it once per step — `job.stepDuration: 1s`
plus `fault.latency: 150ms`, so every **1.15 s**. The activity publishes that cadence
itself as `repro_heartbeat_call_interval_ms`, latency included, and the Heartbeating
board plots both rates against `repro_heartbeat_throttle_ms` so you can see the gap.
(The 400 ms in the loadgen example above is `--step-duration 400ms`, which makes the
gauge read 550; it is not the shipped value.)

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
dotnet run --project src/Repro.Starter          # second terminal

# The activity logs ONCE at the start and once per resume, never per step, so there
# is no "Progress: N" line to wait for. The per-step signal is the gauge. Read it,
# and call the number M:
curl -sS localhost:8077/metrics | grep '^repro_activity_progress'

# SIGKILL, not a plain kill. SIGTERM starts a graceful drain: the activity
# checkpoints once, then keeps working and keeps heartbeating for the whole
# worker.gracefulShutdownTimeout (30s shipped), so it runs PAST M and holds :8077
# that entire time. -9 is what "the worker died" actually looks like.
kill -9 $(lsof -ti tcp:8077)
./src/Repro.Worker/bin/Debug/net10.0/worker
```

The restarted worker prints the resume line within a second of starting to poll:

```
resuming at step 26 of 60; checkpoint was 10167ms old (attempt 4)
```

Three cycles on this stack, M read immediately before each `kill -9`:

| M | resume step | staleness printed |
|---|---|---|
| 9 | 8 | 7154 ms |
| 18 | 19 | 7561 ms |
| 28 | 26 | 10167 ms |

Read that carefully, because the obvious summary is wrong. The resume step lands at
or below `M + 1`; the shortfall is the throttle lag, at most 4 s, which is three
steps at the shipped 1.15 s cadence — and sometimes it is **zero**, because the kill
landed just after Core flushed a heartbeat. Do not expect to lose work every time.
The printed staleness is much larger than the 4 s throttle bound for a different
reason: the server does not know the worker is gone until the 5 s `heartbeatTimeout`
expires, and then waits out the retry backoff before attempt N+1 starts.

## Capture and replay a history

```bash
temporal workflow show --workflow-id repro-workflow --output json > history/heartbeat-job.json
dotnet run --project src/Repro.Replay -- --history history/heartbeat-job.json
```

There is **no `--fields` flag anywhere** in CLI 1.8.1 — not on `show`, not on
`list`, not on `describe`. All three answer `Error: unknown flag: --fields`, so a
guide that tells you to reach for it is written against a different CLI. Plain
`--output json` is what the replayer consumes; `WorkflowHistory.FromJson` handles the
CLI's enum shorthands itself.

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

# Resets the attempt count and the activity timeout, and KEEPS the heartbeat
# details: the retried attempt resumes from the same checkpoint it would have.
temporal activity reset   -w repro-workflow --activity-id <id>

# Discarding the resume point is a separate, explicit flag. THIS is the one that
# makes the next attempt start over at step 1.
temporal activity reset   -w repro-workflow --activity-id <id> --reset-heartbeats
```

A reset also unpauses a paused activity unless you add `--keep-paused`, and an
activity that is mid-flight only observes the reset on its next heartbeat, failure
or timeout — it arrives as a `Canceled` failure, so clean-up code has to re-throw it.

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
| `activity.heartbeatTimeout` | `5s` | required for cancellation; drives the throttle. **Validated, not applied** — see below |
| `activity.startToCloseTimeout` | `10m` | per attempt. **Validated, not applied** |
| `activity.scheduleToCloseTimeout` | `1h` | all attempts. **Read by nothing today** |
| `activity.retry.*` | `1s` / `2.0` / `10s` / `5` | initial, coefficient, max interval, max attempts. **Read by nothing today** |
| `worker.gracefulShutdownTimeout` | `30s` | SDK default is `0s`; see the fault table |
| `worker.maxHeartbeatThrottleInterval` | `60s` | upper bound on the throttle |
| `worker.defaultHeartbeatThrottleInterval` | `30s` | used when the timeout is unset |
| `worker.maxCachedWorkflows` | `0` (SDK default 10000) | set to `1` to force evictions and replay storms |
| `worker.maxConcurrentActivities` / `maxConcurrentWorkflowTasks` | `0` (SDK default 100) | slot counts; `0` leaves the SDK default. Applied by the worker **and** the loadgen — the loadgen used to drop both, so :8078 ran at 100/100 whatever the file said |
| `loadgen.rate` / `concurrency` / `steps` | `5s` / `8` / `20` | traffic shape |
| `fault.failureRate` | `0`, shipped as `0.15` | fraction of activity attempts that fail — one roll per attempt, so P(workflow fails) is this to the fifth |
| `fault.latency` | `0`, shipped as `150ms` | latency added per step |
| `fault.stallPastHeartbeatTimeout` | `false` | overrun the heartbeat timeout on attempt 1 |
| `fault.stopHeartbeating` | `false` | keep working, stop heartbeating |
| `fault.ignoreCancellation` | `false` | swallow cancellation and wedge shutdown |

**The four `activity.*` rows are the one place this file is not yet the source of
truth.** The workflow builds its `ActivityOptions` from `JobInput.Activity`, on
purpose: options that arrive in the input are recorded in the history, so a replay
reproduces them byte for byte, while a file that can be edited between the original
execution and the replay cannot promise that. `ActivityOptionsInput.From(config.Activity)`
exists to project the `activity:` block onto that input — the starter and loadgen
just do not call it yet, so `JobInput.Activity` is null and the workflow falls back
to `ActivityOptionsInput`'s own defaults. Those defaults are exactly the values
shipped in `config.yaml`, which is why nothing looks wrong until you edit one.
`heartbeatTimeout` and `startToCloseTimeout` are still read at startup by
`ConfigLoader.Validate` (`> 0`, and start-to-close must exceed heartbeat), so editing
them changes what the process refuses to start on and nothing else.

Metrics listen addresses must be a full `IP:port` that is **not** loopback. Go's
`":8077"` is accepted and normalized, but Core parses these with Rust's
`SocketAddr`, which rejects a bare `:port`; and `127.0.0.1` is unreachable from
the Prometheus container while `curl localhost:8077` on the host still works.
Both are rejected at startup with an explanation rather than left to fail later.

`--metrics <ip:port>` overrides the configured address on the worker, the loadgen and
the replayer, and **`--metrics off`** starts no exporter and binds no port at all —
that is how you run a second worker on this host without fighting the first one for
:8077. `off` is a flag value only: `metrics.listenAddress: off` in the file is still
rejected, and the error says so. It means no *exporter*, not no *runtime*; the
process still adopts a telemetry-free `TemporalRuntime`, because a client that
connects without one binds to `TemporalRuntime.Default` and loses its metrics
silently.

Keep secrets out of the committed file. Put them in `config.local.yaml`
(gitignored) and pass `--config config.local.yaml` to any binary.

## Layout

```
src/Repro.Core/                 the library everything else references
  Config/ReproConfig.cs         Config POCOs + defaults
  Config/ConfigLoader.cs        YAML load, env overrides, startup validation
  Config/GoDuration.cs          "150ms" / "1m30s" parsing
  Config/BindAddress.cs         normalize + reject loopback binds
  Cli/Flags.cs                  hand-rolled arg parser; unknown flags are errors,
                                and so is `--switch=value` (Go's `-restart=false`)
  Temporal/ClientFactory.cs     ConnectAsync, API key and mTLS paths
  Telemetry/ReproRuntime.cs     the ONE TemporalRuntime; Core Prometheus exporter
  Telemetry/MetricNames.cs      custom metric names as constants
  Telemetry/HistogramBuckets.cs bucket overrides, in milliseconds
  Workflows/HeartbeatWorkflow.workflow.cs   seed workflow    <- edit per repro
  Activities/HeartbeatActivities.cs         seed activity    <- edit per repro
  HeartbeatJob.cs               JobInput, ActivityOptionsInput, Checkpoint
src/Repro.Worker    polls until interrupted, serves :8077
src/Repro.LoadGen   worker + continuous start loop, serves :8078
src/Repro.Starter   one run, prints result, pushes metrics on exit
                    (owns Telemetry/PushMetrics.cs, the Pushgateway bridge)
src/Repro.Replay    replays a history JSON or a directory of them, exits 1 on mismatch
tests/Repro.Tests   config, duration, bind-address and flag parsing
history/            captured histories (committed)
compose.yml         root entry point; includes observability/compose.yml
observability/      compose stack, Prometheus, Grafana, dashboards
```

[`observability/README.md`](observability/README.md) documents the gotchas worth
knowing before you debug the stack itself. Read it before you conclude a panel is
broken — several .NET-specific behaviors look exactly like bugs.
