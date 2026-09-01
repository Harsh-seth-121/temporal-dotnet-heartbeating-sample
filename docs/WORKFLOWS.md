# The four workflows

The first three are registered on every worker, on one task queue, in the `default`
namespace. `WorkflowLocalActivity` is not: it has a namespace, a task queue and a worker of
its own, for a reason given in its own section below. Every process still registers all four
types, so the replayer can meet any of them.

They exist as a comparison. `HeartbeatWorkflow` is the case the repo was built for, and the
other three are shapes it does not cover: no activity at all, an ordinary activity with no
heartbeats, and a LOCAL activity, which is a different execution model rather than a
different set of options. The interesting reading is what changes between the columns.

| | `HeartbeatWorkflow` | `SimpleNoActivity` | `WorkflowSimpleActivity` | `WorkflowLocalActivity` |
|---|---|---|---|---|
| Activities | one, long, heartbeating | none | one, long, no heartbeats | one, long, **local** |
| Activity | `ProcessBatchAsync` | | `FetchWeatherAsync` | `EstimatePi` |
| History event | `ActivityTaskScheduled` | none | `ActivityTaskScheduled` | **`MarkerRecorded`**, name `core_local_activity` |
| Slot type | activity | none | activity | **local activity** |
| Namespace | `default` | `default` | `default` | **`repro-local-activity`** |
| Task queue | `repro-task-queue` | `repro-task-queue` | `repro-task-queue` | **`repro-la-queue`** |
| Messages | none | `Poke` and `Stop` signals, `GetStatus` query, `Add` update | none | none |
| Input | `JobInput` | `SimpleInput` | `SimpleActivityInput` | `LocalActivityInput` |
| Result | `int`, steps done | `SimpleResult` | `WeatherReading` | `PiEstimate` |
| Ends after | its activity's `steps x stepDuration` | `simple.maxDuration`, or a `Stop` | `simpleActivity.sleepDuration` plus one HTTP call | its drawn burn, **or `runTimeout` for ~2/3 of runs** |
| Config block | `job`, `activity`, `fault` | `simple` | `simpleActivity` | `localActivity` |
| Loadgen loop | first | second | third | fourth |
| Turn the loop off | no switch of its own | `simple.enabled: false`, or `--no-simple` | `simpleActivity.enabled: false`, or `--no-simple-activity` | `localActivity.enabled: false`, or `--no-local-activity` |
| Outcome counter | `repro_workflow_completed` | `repro_simple_completed` | `repro_simple_activity_completed` | `repro_local_activity_completed` |
| Outcome values | `completed` `failed` `canceled` `timed_out` | `stopped` `expired` `canceled` | `completed` `failed` `canceled` `timed_out` | `completed` `failed` `canceled` `timed_out`, but see below |
| Accounts for every run | yes | yes | yes | **no** |
| Latency histogram | `repro_workflow_latency` | `repro_simple_latency` | `repro_simple_activity_latency` | `repro_local_activity_latency` |
| Fixture | `history/heartbeat-job.json` | `history/simple-no-activity.json` | `history/workflow-simple-activity.json` | `history/workflow-local-activity.json` and `-wft-timeout.json` |

The first loop has no off switch of its own. `demo-up.sh --no-loadgen` stops all three at
once by not starting the loadgen at all, which is also how you free :8078 for a second
worker.

Four separate outcome counters rather than one counter with a `workflow_type` selector.
The Bug Signals board queries `repro_workflow_completed` as
`sum by (outcome) (rate(...))` with no `workflow_type` selector and stacks the result, so
a second or third workflow type sharing that name would be summed into the heartbeat
lines. `MetricNames.cs` says the same thing at the definition.

## `HeartbeatWorkflow`

The seed case, and the one to edit per repro. One activity that runs
`steps x stepDuration`, calls `Heartbeat()` once per step, checkpoints its progress in the
heartbeat details, and resumes from that checkpoint on a retry. The seed starter takes
that shape from `job.*`; the loadgen overrides it with `loadgen.steps` so a board fills in
under a minute.

The workflow pins `CancellationType = ActivityCancellationType.WaitCancellationCompleted`,
so a cancel does not close the run until the activity has observed it on a heartbeat
response and unwound. `WorkflowSimpleActivity` deliberately leaves that at the default,
and the comparison between the two is the point of both files.

Everything about the throttle, the stale checkpoints, the `kill -9` resume test and the
three fault knobs is in [HEARTBEATING.md](HEARTBEATING.md).

## `SimpleNoActivity`

No activities. The whole run is message passing, which is the surface `HeartbeatWorkflow`
never touches.

| Handler | Wire name | Shape |
|---|---|---|
| `PokeAsync` | `Poke` | signal, increments a counter |
| `StopAsync` | `Stop` | signal, ends the run `stopped` |
| `GetStatus` | `GetStatus` | query, returns `SimpleStatus` |
| `AddAsync` | `Add` | update, returns the sum, guarded by `ValidateAdd` |

The wire names drop the `Async` suffix for signals and updates. A query does not: name
one `GetStatusAsync` and the wire name is literally `GetStatusAsync`. Handlers here are
non-`async` on purpose, because `async Task X() => field = v;` is CS1998 and
`Directory.Build.props` sets `TreatWarningsAsErrors`. Both traps are written up in
[GOTCHAS.md](GOTCHAS.md).

Three endings, and the vocabulary is shared between `SimpleResult.EndedBy` and the
`outcome` tag:

- `stopped`, the `Stop` signal arrived. Server status `Completed`.
- `expired`, `simple.maxDuration` elapsed with no `Stop`. Server status `Completed`.
- `canceled`, a client called `handle.CancelAsync()`. Server status `CANCELED`.

That third one is the only path to `CANCELED`, and the reason is worth knowing before you
try to build it any other way: a workflow cannot cancel itself into that status. Throwing
`CanceledFailureException` unprompted records `FAILED`, swallowing a real cancel records
`COMPLETED`, and the server refuses a signal to self. [GOTCHAS.md](GOTCHAS.md) has the
measurements.

`ValidateAdd` rejects operands whose sum overflows an `int`. A rejected update writes
nothing at all to the event history, and `simple.overflowRate` is what exercises it. There
is no `kind` value for a rejection: a validator must be side-effect free, so the loadgen
counts those client-side instead.

## `WorkflowSimpleActivity`

One activity that sleeps `simpleActivity.sleepDuration`, then fetches the current weather
from Open-Meteo, and returns the reading. The reading lands in the
`WorkflowExecutionCompleted` event, so `temporal workflow show -w <id>` prints the
temperature in the payload.

The options object is a start-to-close timeout plus a retry policy, which is what almost
every real activity should be. What matters is what it does **not** set. No
`HeartbeatTimeout` means the server has no channel to tell a running activity it was
cancelled, so `CancellationType` stays at the SDK default `TryCancel` and a client
`CancelAsync` closes the workflow `CANCELED` while the activity runs to completion and its
result is discarded. Measured: workflow closed at `T+1s`, activity finished at `T+6s` with
a real reading nobody used. Worker shutdown does still reach it, because that token is
local.

The sleep is inside the activity, not a `Workflow.DelayAsync`. That is what makes it a
long activity: it holds an activity slot, produces a real
`temporal_activity_execution_latency`, and gives `startToCloseTimeout` something that can
fire. A workflow timer would write a `TimerStarted`/`TimerFired` pair and occupy nothing.

If the transport fails, the activity logs a warning and returns a synthetic reading tagged
`source="synthetic"` instead of failing, which is how `demo-up.sh` stays green with no
egress. A server that answered is never smoothed over. The trade-off, and
`requireLiveWeather`, are in [CONFIG.md](CONFIG.md).

## `WorkflowLocalActivity`

One CPU-bound LOCAL activity that estimates Pi by Monte Carlo for a duration the loadgen
draws per run, uniformly on `[localActivity.minDuration, localActivity.maxDuration]`
(30s..2m shipped). The estimate and its timing metadata are returned, so they land in the
`WorkflowExecutionCompleted` event:

```json
{"Pi":3.141616673575971,"Iterations":3743744000,"Inside":2940352143,
 "RequestedMs":40124,"ElapsedMs":40124,"IterationsPerSecond":93304024,
 "Attempt":1,"IsLocal":true,"EndedBy":"completed"}
```

That is a real payload from a real run, and so is every number below it.

### What a local activity changes

It executes **inside the workflow task** rather than as a separately scheduled activity
task. Measured consequences, all from the committed fixtures:

- It writes a `MarkerRecorded` event, marker name **`core_local_activity`**. There is no
  `ActivityTaskScheduled`/`Started`/`Completed` triple anywhere in the history.
- It takes a **local activity** slot, not an activity slot. Core reports it as
  `worker_type="LocalActivityWorker"` on `temporal_worker_task_slots_available`, so it does
  not come out of `worker.maxConcurrentActivities`.
- The server has no idea it happened. It sees an undifferentiated `RecordMarker`, the same
  as a side effect or a patch, so **there is no server-side local-activity metric at all**.
- **Heartbeating does not apply.** `LocalActivityOptions` has no `HeartbeatTimeout` — not
  unset, absent from the type. Every mechanism in [HEARTBEATING.md](HEARTBEATING.md) is
  inapplicable here, checkpoints and resume included.

### The failure it exists to demonstrate

Because the activity runs inside the workflow task, that task stays open for the whole
burn, and the SDK keeps it alive with workflow task heartbeats. The server allows that only
up to `history.workflowTaskHeartbeatTimeout`, which this stack drops from its **30m** server
default to **1m** — in this workflow's own namespace, which is the whole reason the second
namespace exists. Past it the task is timed out and rescheduled, and because a local
activity's result is not written to history until it completes, **the burn starts again
from zero**.

The duration lives in the workflow input, so it is identical on every re-execution. A run
that draws more than a minute is therefore doomed to repeat until something else stops it.
At the shipped draw that is exactly `(120-60)/(120-30)` = **two-thirds of runs**.

Measured, on one six-minute doomed run:

| | |
|---|---|
| total events | 134 |
| `WorkflowTaskTimedOut` | 8 |
| `WorkflowTaskScheduled` / `Started` | 44 each |
| `WorkflowTaskCompleted` | 35 (these are the heartbeats) |
| **`MarkerRecorded`** | **0** |
| ends with | `WorkflowExecutionTimedOut` |

Zero markers in six minutes is the mechanism stated as a fact rather than quoted from a
doc: the activity ran eight times and never once persisted anything.

Across that whole demo run: **14 local-activity executions for 1 completed workflow.**

### What stops it, and what does not

Read this before changing a timeout, because three of the four rungs do not do what their
names suggest.

| Rung | Shipped | What it actually does |
|---|---|---|
| `startToCloseTimeout` | 2m30s | **Never fires.** The SDK requires one of the two timeouts to be set; this is the one that is set. The burn is capped at `maxDuration` and the workflow task dies at 1m first. |
| `scheduleToCloseTimeout` | 5m | **Never fires either**, at this config. Its clock *restarts* on every re-dispatch. |
| `retry.maximumAttempts` | 1 | Irrelevant to the loop. A re-execution is not a retry; it arrives as **attempt 1 again**. Must still be non-zero, because an unset `RetryPolicy` on a local activity means retry **forever**. |
| `runTimeout` | 6m | **The only rung that ends the run.** Server-enforced on the timer queue. |

The schedule-to-close claim is the counter-intuitive one and the one most likely to be
"corrected" by a future reader, so here is the chain. sdk-core re-stamps
`original_schedule_time` on every fresh schedule with `get_or_insert(SystemTime::now())`,
and persists it only inside the **marker**, guarded by `if record_marker`. A local activity
killed by a workflow task timeout never resolved, so no marker was written and nothing was
persisted. Eviction then sends `InvalidateRun`, and `Drop for TimeoutBag` aborts the
schedule-to-close handle outright. The proto field that carries a previous clock forward
travels only through `DoBackoff`, which is timer-based retry backoff — a different path.

**Set `scheduleToCloseTimeout` below 1m** and this case becomes its own documented fix: the
local activity then fails with a timeout the workflow catches, records `timed_out`, and the
workflow task is never re-executed. That is the only regime in which the rung fires. It is
why `Classify` matches `ScheduleToClose` as well as `StartToClose`, and why `ConfigLoader`
deliberately does not order that field against `startToCloseTimeout`.

### Why its outcome counter does not account for every run

`runTimeout` closes a workflow by calling `TimeoutWorkflow` directly, **without scheduling a
workflow task**. Workflow code never resumes, so `RunAsync`'s `catch` never runs and
`repro_local_activity_completed` does not increment at all for those runs — not even as
`timed_out`. Two-thirds of runs are simply absent from it.

That is why **`repro_pi_attempt_started` is the primary signal for this case** rather than a
supporting one. It is emitted from *activity* code, which does not replay, so it counts real
burns including every re-execution. The count of timed-out runs comes from the server's own
`workflow_timeout` in that namespace.

`temporal_local_activity_total` measures very nearly the same thing and was measured to
agree exactly (8 and 8, 6 and 6 across the two workers). The custom counter is kept because
the two are incremented at different moments — Core's at schedule time, this one on entry to
the burn — so they diverge exactly when local activity slots are saturated, which is the
signal worth having. Note the tag sets differ: `repro_pi_attempt_started` carries
`activity_type`, `namespace` and `task_queue` but **not** `workflow_type`, while
`temporal_local_activity_total` carries all four.

Both workers poll `repro-la-queue`, so both export these counters and a query must sum
across `:8077` and `:8078`.

### The activity is told when its workflow task dies

Not documented anywhere I could find, and measured here. Across 17 cut-short burns in one
demo run, **every one ended between 64.0s and 64.2s** against the 1m timeout — never at the
requested duration, never at a drain. So a local activity does receive cancellation when the
workflow task it lives inside times out, roughly four seconds after the server's timeout.

It changes no outcome: the workflow task is already gone and the estimate is discarded. It
changes the arithmetic, because a doomed run burns about 64s per execution rather than its
full drawn duration.

The two ways a burn is cut short have different signatures, which is worth knowing before
reading a log at 2am:

- a **drain** cuts every in-flight burn at the same **wall-clock** instant with unrelated
  elapsed values — measured 49848ms, 2887ms and 30422ms, all at `10:25:30`
- a **workflow task timeout** cuts each burn at the same **elapsed** value at unrelated
  wall-clock times — measured 64076, 64079, 64080, 64097ms

`PiEstimate.EndedBy` records which: `completed`, `shutdown`, or `canceled`.

### Expect most ticks to skip

A doomed run holds a concurrency slot for the whole of `runTimeout`, so mean occupancy is
roughly `(1/3)(a completing run) + (2/3)(6m)`. Measured over one demo run at concurrency 3:
**6 started, 11 skipped at capacity, 1 completed, 3 ended at `runTimeout`.** A sparse panel
here is the design, not a broken target.

## The 19 custom metrics

`src/Repro.Core/Telemetry/MetricNames.cs` holds every name as a constant, so a typo is a
compile error rather than an empty panel. Custom names are not touched by
`MetricsOptions.MetricPrefix`, so the `repro_` is literal and stays literal.

Root tags come from the meter and are never re-added here. `Workflow.MetricMeter` arrives
tagged with `namespace`, `task_queue` and `workflow_type`;
`ActivityExecutionContext.MetricMeter` with `namespace`, `task_queue` and `activity_type`.
The "Extra tags" column below is what this repo adds on top.

| Metric | Kind | Extra tags | Emitted by |
|---|---|---|---|
| `repro_workflow_completed` | counter | `outcome` | `HeartbeatWorkflow` |
| `repro_workflow_latency` | histogram | `outcome` | `HeartbeatWorkflow` |
| `repro_activity_started` | counter | `retried`, `resumed` | `ProcessBatchAsync` |
| `repro_activity_failed` | counter | | `ProcessBatchAsync` |
| `repro_activity_cancel` | counter | `reason` | `ProcessBatchAsync` |
| `repro_activity_progress` | gauge | | `ProcessBatchAsync` |
| `repro_heartbeat_sent` | counter | | `ProcessBatchAsync` |
| `repro_heartbeat_staleness` | histogram | | `ProcessBatchAsync` |
| `repro_heartbeat_call_interval_ms` | gauge, `ms` | | `ProcessBatchAsync` |
| `repro_heartbeat_throttle_ms` | gauge, `ms` | | `ProcessBatchAsync` |
| `repro_heartbeat_timeout_ms` | gauge, `ms` | | `ProcessBatchAsync` |
| `repro_simple_completed` | counter | `outcome` | `SimpleNoActivity` |
| `repro_simple_latency` | histogram | `outcome` | `SimpleNoActivity` |
| `repro_simple_message` | counter | `kind` | `SimpleNoActivity` |
| `repro_simple_activity_completed` | counter | `outcome`, `source` | `WorkflowSimpleActivity` |
| `repro_simple_activity_latency` | histogram | `outcome` | `WorkflowSimpleActivity` |
| `repro_local_activity_completed` | counter | `outcome` | `WorkflowLocalActivity` |
| `repro_local_activity_latency` | histogram | `outcome` | `WorkflowLocalActivity` |
| `repro_pi_attempt_started` | counter | | `EstimatePi` |

Tag values:

- `outcome`: `completed` `failed` `canceled` `timed_out` on the workflow and
  simple-activity counters, `stopped` `expired` `canceled` on `repro_simple_completed`.
  One L in `canceled`, matching `ActivityCancelReason` and the boards.
- `retried` and `resumed`: `true` or `false`, lowercase. .NET's `bool.ToString()` returns
  `True`, which no ported selector matches and which does not error. See
  [GOTCHAS.md](GOTCHAS.md).
- `reason`: Core's own `ActivityCancelReason`.
- `kind`: `poke` or `add`. No value for a rejected update.
- `source`: `open-meteo`, `synthetic`, or `none` when the run produced no reading at all.
  `none` exists so `sum by (source)` accounts for every run.
- `PiEstimate.EndedBy` is a PAYLOAD field, not a tag: `completed`, `shutdown` (a worker
  drain) or `canceled` (in practice, the workflow task timed out under the activity). It is
  not a metric dimension because nothing on a board asks that question; it is a per-run
  diagnostic for `temporal workflow show`.

`repro_simple_activity_latency` carries `outcome` only, deliberately, and not because the
source is invisible in the numbers. It is plainly visible: a refused endpoint lands near
5.02s and a live fetch near 5.77s. The reason is that `sum by (source)` on
`repro_simple_activity_completed` already answers "is this demo reaching the internet"
exactly once, and answering it twice would double a 13-boundary histogram's series count
for nothing.

All five of these histograms get bucket overrides in `HistogramBuckets.cs`, alongside seven
SDK ones (`temporal_local_activity_execution_latency` is the seventh). Two of the four are mandatory rather than tuning: without a row,
`repro_simple_latency` and `repro_simple_activity_latency` fall to Core's catch-all, which
tops out at 10s, and p95 pins flat at ~9.9s forever.

A counter that has never incremented does not appear on `/metrics` at all, so
`repro_activity_cancel` and `repro_activity_failed` are absent from a healthy worker
rather than reading `0`. That is the first entry in [GOTCHAS.md](GOTCHAS.md), and it is
why `or vector(0)` is load-bearing on every board here.

## Replaying them

All five fixtures live in `history/` and all five replay from one command:

```bash
dotnet run --project src/Repro.Replay -- --history history/
```

`Repro.Replay` registers all four types. It fails loudly on a history whose type it was
not registered with, and that failure is not a nondeterminism error. See
[REPLAY.md](REPLAY.md) for both messages side by side.
