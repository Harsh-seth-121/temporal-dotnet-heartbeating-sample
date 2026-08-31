# The three workflows

All three are registered on every worker, on one task queue, in one process. The worker,
the loadgen and the replayer each register the same set, so any of them can meet any of
the three.

They exist as a comparison. `HeartbeatWorkflow` is the case the repo was built for, and
the other two are the shapes it does not cover: no activity at all, and an ordinary
activity with no heartbeats. The interesting reading is what changes between the columns.

| | `HeartbeatWorkflow` | `SimpleNoActivity` | `WorkflowSimpleActivity` |
|---|---|---|---|
| Activities | one, long, heartbeating | none | one, long, no heartbeats |
| Activity | `ProcessBatchAsync` | | `FetchWeatherAsync` |
| Messages | none | `Poke` and `Stop` signals, `GetStatus` query, `Add` update | none |
| Input | `JobInput` | `SimpleInput` | `SimpleActivityInput` |
| Result | `int`, steps done | `SimpleResult` | `WeatherReading` |
| Ends after | its activity's `steps x stepDuration` | `simple.maxDuration`, or a `Stop` | `simpleActivity.sleepDuration` plus one HTTP call |
| Config block | `job`, `activity`, `fault` | `simple` | `simpleActivity` |
| Loadgen loop | first | second | third |
| Turn the loop off | no switch of its own | `simple.enabled: false`, or `--no-simple` | `simpleActivity.enabled: false`, or `--no-simple-activity` |
| Outcome counter | `repro_workflow_completed` | `repro_simple_completed` | `repro_simple_activity_completed` |
| Outcome values | `completed` `failed` `canceled` `timed_out` | `stopped` `expired` `canceled` | `completed` `failed` `canceled` `timed_out` |
| Latency histogram | `repro_workflow_latency` | `repro_simple_latency` | `repro_simple_activity_latency` |
| Fixture | `history/heartbeat-job.json` | `history/simple-no-activity.json` | `history/workflow-simple-activity.json` |

The first loop has no off switch of its own. `demo-up.sh --no-loadgen` stops all three at
once by not starting the loadgen at all, which is also how you free :8078 for a second
worker.

Three separate outcome counters rather than one counter with a `workflow_type` selector.
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

## The 16 custom metrics

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

`repro_simple_activity_latency` carries `outcome` only, deliberately, and not because the
source is invisible in the numbers. It is plainly visible: a refused endpoint lands near
5.02s and a live fetch near 5.77s. The reason is that `sum by (source)` on
`repro_simple_activity_completed` already answers "is this demo reaching the internet"
exactly once, and answering it twice would double a 13-boundary histogram's series count
for nothing.

All four of these histograms get bucket overrides in `HistogramBuckets.cs`, alongside six
SDK ones. Two of the four are mandatory rather than tuning: without a row,
`repro_simple_latency` and `repro_simple_activity_latency` fall to Core's catch-all, which
tops out at 10s, and p95 pins flat at ~9.9s forever.

A counter that has never incremented does not appear on `/metrics` at all, so
`repro_activity_cancel` and `repro_activity_failed` are absent from a healthy worker
rather than reading `0`. That is the first entry in [GOTCHAS.md](GOTCHAS.md), and it is
why `or vector(0)` is load-bearing on every board here.

## Replaying them

All three fixtures live in `history/` and all three replay from one command:

```bash
dotnet run --project src/Repro.Replay -- --history history/
```

`Repro.Replay` registers all three types. It fails loudly on a history whose type it was
not registered with, and that failure is not a nondeterminism error. See
[REPLAY.md](REPLAY.md) for both messages side by side.
