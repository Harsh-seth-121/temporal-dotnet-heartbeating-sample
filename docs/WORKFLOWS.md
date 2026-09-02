# The five workflows

The first three are registered on every worker, on one task queue, in the `default`
namespace. The other two are not, and they are split differently.
`WorkflowLocalActivity` has a namespace, a task queue and a worker of its own.
`WorkflowFileScan` stays in `default` and takes only a task queue and a worker, which is
the cheaper split: a namespace is a client property, a task queue is not. Both sections
below give the reason. Every process still registers all five types, so the replayer can
meet any of them.

They exist as a comparison. `HeartbeatWorkflow` is the case the repo was built for, and the
other four are shapes it does not cover: no activity at all, an ordinary activity with no
heartbeats, a LOCAL activity, which is a different execution model rather than a different
set of options, and a long heartbeating activity over a real file, which is the same
execution model the seed uses with something real to be right about. The interesting
reading is what changes between the columns.

| | `HeartbeatWorkflow` | `SimpleNoActivity` | `WorkflowSimpleActivity` | `WorkflowLocalActivity` | `WorkflowFileScan` |
|---|---|---|---|---|---|
| Activities | one, long, heartbeating | none | one, long, no heartbeats | one, long, **local** | one, long, heartbeating, **resumable** |
| Activity | `ProcessBatchAsync` | | `FetchWeatherAsync` | `EstimatePi` | `ScanFileAsync`, on the wire **`ScanFile`** |
| History event | `ActivityTaskScheduled` | none | `ActivityTaskScheduled` | **`MarkerRecorded`**, name `core_local_activity` | `ActivityTaskScheduled` |
| Slot type | activity | none | activity | **local activity** | activity |
| Namespace | `default` | `default` | `default` | **`repro-local-activity`** | `default` |
| Task queue | `repro-task-queue` | `repro-task-queue` | `repro-task-queue` | **`repro-la-queue`** | **`repro-scan-queue`** |
| Messages | none | `Poke` and `Stop` signals, `GetStatus` query, `Add` update | none | none | none |
| Input | `JobInput` | `SimpleInput` | `SimpleActivityInput` | `LocalActivityInput` | `FileScanInput` |
| Result | `int`, steps done | `SimpleResult` | `WeatherReading` | `PiEstimate` | `FileScanResult` |
| Ends after | its activity's `steps x stepDuration` | `simple.maxDuration`, or a `Stop` | `simpleActivity.sleepDuration` plus one HTTP call | its drawn burn, **or `runTimeout` for ~2/3 of runs** | the whole corpus: **4m47s** for `sample-100mb.txt` at 6000 rows/s |
| Config block | `job`, `activity`, `fault` | `simple` | `simpleActivity` | `localActivity` | `fileScan`, `fault` |
| Loadgen loop | first | second | third | fourth | fifth |
| Turn the loop off | no switch of its own | `simple.enabled: false`, or `--no-simple` | `simpleActivity.enabled: false`, or `--no-simple-activity` | `localActivity.enabled: false`, or `--no-local-activity` | `fileScan.enabled: false`, or `--no-file-scan` |
| Outcome counter | `repro_workflow_completed` | `repro_simple_completed` | `repro_simple_activity_completed` | `repro_local_activity_completed` | `repro_file_scan_completed` |
| Outcome values | `completed` `failed` `canceled` `timed_out` | `stopped` `expired` `canceled` | `completed` `failed` `canceled` `timed_out` | `completed` `failed` `canceled` `timed_out`, but see below | `completed` `failed` `canceled` `timed_out`, all four reachable |
| Accounts for every run | yes | yes | yes | **no** | yes |
| Latency histogram | `repro_workflow_latency` | `repro_simple_latency` | `repro_simple_activity_latency` | `repro_local_activity_latency` | `repro_file_scan_latency` |
| Fixture | `history/heartbeat-job.json` | `history/simple-no-activity.json` | `history/workflow-simple-activity.json` | `history/workflow-local-activity.json` and `-wft-timeout.json` | **none committed**; capture one, [REPLAY.md](REPLAY.md) |

The first loop has no off switch of its own. `demo-up.sh --no-loadgen` stops all five at
once by not starting the loadgen at all, which is also how you free :8078 for a second
worker.

Five separate outcome counters rather than one counter with a `workflow_type` selector.
The Bug Signals board queries `repro_workflow_completed` as
`sum by (outcome) (rate(...))` with no `workflow_type` selector and stacks the result, so
any other workflow type sharing that name would be summed into the heartbeat lines.
`MetricNames.cs` says the same thing at the definition.

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

## `WorkflowFileScan`

One long, heartbeating activity that streams a generated corpus out of `sample_files/`,
checkpointing an EXACT byte offset and a rewound accumulator into its heartbeat details,
and closing with a closed-form check that says out loud whether the resume was idempotent.

Read it against `HeartbeatWorkflow` rather than instead of it. That case is the heartbeat
mechanism on a `Task.Delay` step loop, where nothing is genuinely reprocessed because a
synthetic step is idempotent by construction, and where a sleeping task allocates nothing
so every memory panel reads the same whether the activity is working or asleep. This case
gives the same mechanism something real to be wrong about, in both halves.

The workflow itself is thin, and that is the design rather than a shortcoming. Everything
interesting is I/O, wall clock, memory and a checkpoint, none of which may live in
workflow code. What is left in `WorkflowFileScan.workflow.cs` is the timeout ladder, the
cancellation type, the outcome classification and one metric pair.

The result lands in the `WorkflowExecutionCompleted` event, so `temporal workflow show -w
repro-file-scan` prints the verdict:

```json
{"Rows":1724588,"Bytes":99999968,"IndexSum":1487102747166,
 "WordByteSum":65508200,"Verified":true}
```

Those are the measured values for `sample-100mb.txt`, and they came out **identical from a
full scan and from three separate resume points**.

### A task queue of its own, but not a namespace

`repro-scan-queue`, in `default`. Not for isolation of the interesting kind: GC, the thread
pool and RSS are process-wide and stay shared, which is the point of the pressure half.

It exists because `temporal_worker_task_slots_used` carries `worker_type` but **no
`activity_type` label**, and the Heartbeating board's headline stat sums it unfiltered
while its description claims this repo has exactly one heartbeating activity type. A second
heartbeating activity on `repro-task-queue` would corrupt that panel with nothing to filter
it back out. A separate queue lets the panel pin `task_queue` and excludes the scan exactly.

That split is a queue, not a namespace, so it costs one extra `TemporalWorker` on the
client the process already has. `Repro.Worker` runs three workers now: the main one, the
scan worker on the same client, and the local-activity worker on a second client. Compare
`WorkflowLocalActivity`, which needs the second client because a namespace is a client
property and `history.workflowTaskHeartbeatTimeout` can only be scoped per namespace.

### The corpus contract

Line 1 is the row count `N`. Each of the next `N` lines is `%010d [w w w w w w w]\n`:
ASCII, LF, a 10-digit zero-padded index, then seven 3-to-8-letter words in brackets. Rows
are **41 to 76 bytes including the LF, not fixed width**, of which 20 bytes are the fixed
overhead.

For `sample-100mb.txt`: `N` = **1,724,588**, **99,999,968** bytes, an **8**-byte header.
Three identities the design leans on, all checked against the file on disk:

```
ByteOffset  == headerLen + 20 x Rows + WordByteSum   # holds at every checkpoint
IndexSum    == Rows x (Rows + 1) / 2                 # 1,487,102,747,166 at a full scan
WordByteSum == FileBytes - headerLen - 20 x N        # 65,508,200 at a full scan
```

`gen_samples.py` keys its word seed on TARGET SIZE, so row 5 holds different words in every
corpus and each of the four is an independent stream. That is what makes the
corpus-identity check strong and what makes a regenerated corpus of the same size
byte-identical.

`sample_files/` is gitignored and generated, so a fresh clone has no corpus and nothing
fails: `ConfigLoader` validates the shape of `fileScan.path` and never stats it, the
loadgen driver checks once and skips its loop with a named banner, and the scan worker
registers the activity anyway so a corpus generated later needs no restart. Absent-corpus
behaviour in full is in [CONFIG.md](CONFIG.md).

### The scan reads raw bytes because a `StreamReader` cannot give an exact offset

`StreamReader` buffers ahead, so `BaseStream.Position` is somewhere past the last returned
line and there is no public API for the logical one, while `line.Length + 1` counts chars
rather than bytes and drifts one byte per row on CRLF. An inexact byte cursor destroys the
resume half of this case, so the loop reads into one `byte[]` and finds its own line
breaks. [GOTCHAS.md](GOTCHAS.md) has it as an entry.

Two things follow. The cursor is a byte count and cannot drift. And the loop allocates
**nothing** per row, no `string` and no `char[]`, which is what makes the fault ladder
below legible against a provable zero rather than against a floor you have to read a step
change off.

### What makes the resume idempotent

The checkpoint carries the accumulator as well as the cursor, and on resume **both** are
restored from the same heartbeat. Rows between that checkpoint and the crash are therefore
physically re-read and arithmetically counted exactly once.

The rule that generalises is not "carry the sum in the checkpoint". It is **the
accumulator's origin and the read cursor's origin must be the same checkpoint**. Restore
the cursor from the heartbeat and the accumulator from zero and every re-read row is
counted twice; restore the accumulator and leave the cursor at zero and they are counted
twice the other way. Neither throws, and only the closed form at the end reports it.

An XOR or hash fold is the aggregate a reader reaches for first, and it would delete the
lesson: under a self-inverse operation double-counting CANCELS, so a naive resume produces
the *right* answer, the verdict counter reads `match`, and the case demonstrates nothing. A
sum does not forgive, and `rows x (rows + 1) / 2` says by how much it was wrong.

Before it reads one resumed byte the activity runs its checks in order, cheapest and most
decisive first, so a checkpoint that cannot be resumed from costs no I/O at all:

1. The checkpoint against **both** its own closed forms. Arithmetic only, no file opened.
   This is the schema-drift tripwire: heartbeat details bind by NAME through the data
   converter, so a renamed record parameter binds nothing and yields `default(T)`, and a
   zeroed `IndexSum` beside a correct `ByteOffset` is a silent wrong answer that would
   otherwise surface five minutes later as an aggregate mismatch.
2. **Corpus identity**, `(fileRows, fileBytes)`, both O(1). Without it a resume can seek
   into a different corpus, land on a valid line boundary, run to completion, report
   `outcome="completed"` and return a sum over a mixture of two files.
3. Bounds, then a **line-boundary proof**: the byte at `ByteOffset - 1` must be an LF.
   Uniform with no special case, because at `Rows == 0` that byte is line 1's own LF.
4. A **row-identity proof**: the leading 10 digits at the cursor must equal `Rows + 1`.

Every failure above is **non-retryable**, and so is an aggregate mismatch at the end. The
discriminator is that *structural* disagreement between the checkpoint and the file is
terminal while *transport* failure against a file that still matches is retryable, which
leaves `IOException` and `UnauthorizedAccessException` mid-read to the retry policy.

The per-row parser deliberately does **not** check each row's index against `rows + 1`.
That check would not catch the bug this case exists to show: a resume that rewinds the
cursor but not the accumulator restores `rows` from the checkpoint too, so every row it
reads satisfies `index == rows + 1` and the per-row check passes while the total comes out
short.

### What a `kill -9` costs, exactly

`fileScan.heartbeatTimeout` is 30s and is chosen for the **staleness it produces**, not for
liveness: the maximum gap between two `Heartbeat()` calls is one batch period, 100 ms, so
30s is 300x margin. What it actually sets is Core's throttle,
`min(0.8 x 30s, worker.maxHeartbeatThrottleInterval)` = **24s**, and therefore how far
behind the work the checkpoint the server holds can be.

At `targetRowsPerSecond: 6000` that is `24 x 6000` = **144,000 rows, 8.35% of the 100 MB
corpus**: an unmissable drop on the cursor panel. At the seed case's 5s heartbeat timeout
the throttle is 4s and the same drop is 1.4%, which is visible on a panel and invisible in
a demo. The knob saturates: past 75s the 60s throttle ceiling binds and redone work
plateaus at `60s x rate`.

Scan durations at the shipped 6000 rows/s, for the four generated corpora:

| Corpus | Scan time |
|---|---|
| 100 MB | 4m47s |
| 200 MB | 9m35s |
| 350 MB | 16m46s |
| 500 MB | 23m57s |

The `kill -9` recipe, the `temporal activity reset` proof and what to expect from each
reading are in [HEARTBEATING.md](HEARTBEATING.md).

Redone work is a **derived panel**, not a metric, and the reason is a proof. A cumulative
`RowsRead` field on the checkpoint would have to be `A_k = A_(k-1) + (C_k - C_(k-1))`,
which telescopes to `A_k = C_k` identically, so it equals `Rows` and carries no
information. The reads that get lost are exactly the reads that were never checkpointed, so
the checkpoint is structurally incapable of measuring redone work. The board reconstructs
it from `repro_file_scan_rows_read` against `repro_file_scan_rows_expected` instead. See
[DASHBOARDS.md](DASHBOARDS.md).

### The pressure half, and three readings to get wrong

`ProcessPressure.Sample()` reads every value once per `fileScan.logInterval` and feeds both
the console line and the gauges from that one read, so Grafana and the console cannot
disagree by a tick. Cumulative values go through a compare-exchange watermark that refuses
to move backwards, seeded lazily at the first sample, so the worker's startup GCs are
excluded by construction and concurrent scans cannot count the same bytes twice.

Three faults form a ladder, each proving one claim. Turn on exactly one at a time. Measured
allocated bytes over bytes read, on 200,000 rows of the 100 MB corpus:

| Knob | The single claim | Amplification |
|---|---|---|
| *(none, shipped)* | The raw-byte path allocates nothing per row | **0.01x** |
| `fault.decodeRowsToStrings` | **Allocation is not growth.** About 140 B of gen0 garbage per row, and it arrives with a FLAT live-heap floor | **2.41x** |
| `fault.retainScannedRows` | **Retention grows the heap.** The identical garbage becomes promoted live gen2, the heap becomes a staircase with no falling edge, and rows/s dips below target | **2.54x** |
| `fault.slurpWholeFile` | **A big read is one LOH object, and the LOH is not compacted**, so committed bytes step up and do not come back | **8.63x** |

Note how little separates the second and third rungs, 2.41x against 2.54x: the allocation
rate is nearly the same and the *shape of the heap* is what differs. That is the whole
point of reading the two panels together.

Three readings a newcomer gets backwards, all measured on this stack:

- **A near-zero `repro_file_scan_bytes_allocated` rate is the default path working, not a
  broken counter.** The dominant contributor is the per-batch heartbeat at 117 B per batch,
  not the sampler: a 4m47s scan reports about **415 KB in total**, roughly 336 KB of
  checkpoints, 71 KB of fixed read-buffer and `FileStream` cost, and 8.4 KB of pressure
  samples (29 x 288 B). That is 1.4 KB/s against 348 KB/s of reading, or 0.4%.
- **`repro_file_scan_loh_bytes` and `repro_file_scan_gc_pause_percent` are last-GC
  snapshots, not live readings.** `GCMemoryInfo` describes the last collection, so
  `fault.slurpWholeFile` steps the LOH gauge **at the next GC**, not in the sample where the
  array was allocated. And `PauseTimePercentage` is not a rolling window: on a scan that
  triggers no collection it reports the worker's startup GCs forever. Believe movement in it
  only when `repro_file_scan_gc_collected` is moving too.
- **`repro_file_scan_working_set_bytes` staying flat through a 500 MB scan is the proof, not
  a broken gauge.** The file's bytes live in the kernel page cache and never enter this
  process's address space. `Environment.WorkingSet` itself works fine here: measured 37.2
  MiB, 1.3 us per call, zero allocation on macOS arm64, so there is no fallback path. Note
  that `TotalCommittedBytes` and every `GenerationInfo` entry read **0 before the process's
  first GC**, which is why the obvious fallback would have been the wrong one.

`GC.CollectionCount(g)` counts generation g **or higher**, so the three `gen` series NEST
and their sum is not "total collections". [GOTCHAS.md](GOTCHAS.md) has the measurement.

This is **workstation GC**: `ServerGarbageCollection` is unset in
`Directory.Build.props`. `DOTNET_gcServer=1` raises the gen0 budget dramatically and
invalidates every magnitude above. `DOTNET_GCgen0size` is how to pin the budget for a
reproducible collection count.

### Where the console tells you what the board cannot

Progress lines are bounded by **wall time**, `fileScan.logInterval`, checked once per
batch. A row-count cadence goes sparse exactly when the system slows down, which is when
the line is worth having. About 29 lines for the shipped corpus, plus off-interval lines at
start, resume, the drain edge, cancel, completion and every validation failure.

Four of the shapes, so you know what to grep for:

```
scanning <abs>/sample-100mb.txt: <N> rows, <bytes> bytes, from row <R> at offset <O> (attempt <A>, target 6000 rows/s, ~287s)
row <R>/1724588 (<P>%) offset <O>/99999968 at <rows>/s (<KB>/s); heap <M> MiB, alloc <X> MiB/s, gc <g0>/<g1>/<g2>
RESUMING at row <R> of 1724588, offset <O>; checkpoint was <ms>ms old, so about <E> rows will be re-read -- that figure is staleness x target rate, an ESTIMATE (attempt <A>)
scan COMPLETE: 1724588 rows, 99999968 bytes, ended at offset 99999968; indexSum 1487102747166 == expected, wordByteSum 65508200 == expected
```

The numbers on the `COMPLETE` line are fixed properties of `sample-100mb.txt` and do not
depend on how the run went, which is the whole point of it. Everything on the `RESUMING`
line depends on where the kill landed.

The `RESUMING` line prints the **absolute resolved path** on every attempt, so a
working-directory change is visible even where the corpus-identity check would miss it: two
corpora of the same target size are byte-identical, so identity alone cannot tell you which
directory you are in. It also labels its own redone figure an ESTIMATE, because that number
is staleness times target rate. The exact figure is the difference between this line's row
and the last periodic line above it in the scrollback.

## The 35 custom metrics

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
| `repro_activity_cancel` | counter | `reason` | `ProcessBatchAsync`, `ScanFile` |
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
| `repro_file_scan_completed` | counter | `outcome` | `WorkflowFileScan` |
| `repro_file_scan_latency` | histogram | `outcome` | `WorkflowFileScan` |
| `repro_file_scan_started` | counter | `retried`, `resumed` | `ScanFile` |
| `repro_file_scan_rows_read` | counter | **`attempt`** | `ScanFile` |
| `repro_file_scan_bytes_read` | counter | **`attempt`** | `ScanFile` |
| `repro_file_scan_row_cursor` | gauge | | `ScanFile` |
| `repro_file_scan_resumed_from_row` | gauge | | `ScanFile` |
| `repro_file_scan_rows_expected` | gauge | | `ScanFile` |
| `repro_file_scan_verified` | counter | `result` | `ScanFile` |
| `repro_file_scan_staleness` | histogram | | `ScanFile` |
| `repro_file_scan_bytes_allocated` | counter | | `ScanFile` |
| `repro_file_scan_gc_collected` | counter | `gen` | `ScanFile` |
| `repro_file_scan_managed_heap_bytes` | gauge | | `ScanFile` |
| `repro_file_scan_loh_bytes` | gauge | | `ScanFile` |
| `repro_file_scan_working_set_bytes` | gauge | | `ScanFile` |
| `repro_file_scan_gc_pause_percent` | gauge, `double` | | `ScanFile` |

`repro_activity_cancel` is the only name reused across cases, and the test for reuse is
whether the existing panel makes a claim a second contributor falsifies. A per-reason
breakdown of cancellations does not. By that same test `repro_heartbeat_sent`,
`repro_activity_started`, `repro_activity_progress` and the three `repro_heartbeat_*_ms`
gauges must **not** be reused: every one of those panels is an unfiltered `max()` or `sum()`
across activity types, so a second series with a different value silently changes what they
mean. That is why the file-scan case has its own `_started` counter and its own
`_staleness` histogram rather than joining those two.

Tag values:

- `outcome`: `completed` `failed` `canceled` `timed_out` on the workflow, simple-activity
  and file-scan counters, `stopped` `expired` `canceled` on `repro_simple_completed`.
  One L in `canceled`, matching `ActivityCancelReason` and the boards.
- `retried` and `resumed`: `true` or `false`, lowercase. .NET's `bool.ToString()` returns
  `True`, which no ported selector matches and which does not error. See
  [GOTCHAS.md](GOTCHAS.md).
- `reason`: Core's own `ActivityCancelReason`.
- `kind`: `poke` or `add`. No value for a rejected update.
- `source`: `open-meteo`, `synthetic`, or `none` when the run produced no reading at all.
  `none` exists so `sum by (source)` accounts for every run.
- `result`: `match` or `mismatch`, on `repro_file_scan_verified` only. Every scan that
  reaches the end produces exactly one of the two, so `sum by (result)` accounts for 100%
  of COMPLETED scans. `mismatch` must be unreachable in a healthy run and must still exist
  as a name: an idempotency bug with nowhere to be counted would present as a slightly
  wrong number in a log line nobody re-reads.
- `attempt`: `ActivityInfo.Attempt` as an invariant decimal string, on
  `repro_file_scan_rows_read` and `repro_file_scan_bytes_read` **only**. It is the one
  deliberate exception to the no-extra-tags rule, and the reason is not "attempt is a small
  number", it is that `fileScan.retry.maximumAttempts` **bounds** it at 10 per process
  whatever `fileScan.concurrency` is. The same tag on a gauge would blank out the panel it
  was added to help; [GOTCHAS.md](GOTCHAS.md) has the measurement.
- `gen`: `0`, `1` or `2`, on `repro_file_scan_gc_collected`. The three values do **not**
  partition the quantity: `GC.CollectionCount(g)` counts generation g or higher, so the
  lines nest and their sum is not "total collections". `gen="2"` is ABSENT rather than zero
  on a shipped-config scan, because Core creates a series on first increment and a
  streaming scan promotes nothing.
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

All **seven** custom histograms get bucket overrides in `HistogramBuckets.cs`, alongside
**seven** SDK ones, for **14** rows in that table. Three of the seven are mandatory rather
than tuning: without a row, `repro_simple_latency`, `repro_simple_activity_latency` and
`repro_local_activity_latency` fall to Core's catch-all, which tops out at 10s, and p95
pins flat at ~9.9s forever.

Two of those rows belong to the file-scan case, and one boundary in each is the point.
`repro_file_scan_staleness` carries **24_000**, which is `0.8 x fileScan.heartbeatTimeout`,
so Core's throttle bound reads as a visible shoulder instead of interpolation inside one
wide bucket. `repro_file_scan_latency` runs to **3_600_000** and keeps sub-60s boundaries
that are not padding: a corpus-identity mismatch fails in MILLISECONDS and would otherwise
pile into the same bucket as a real 4m47s scan.

The file-scan case also **extended an existing SDK row**. `activity_execution_latency`
topped out at 600,000 ms, so the 350 and 500 MB corpora (16m46s and 23m57s at 6000 rows/s)
put every attempt in `+Inf` and pinned p95 at a plausible constant forever. It now runs to
1,800,000 ms. The change is purely additive: boundaries above the old top change no
existing case's resolution.

A counter that has never incremented does not appear on `/metrics` at all, so
`repro_activity_cancel` and `repro_activity_failed` are absent from a healthy worker
rather than reading `0`. That is the first entry in [GOTCHAS.md](GOTCHAS.md), and it is
why `or vector(0)` is load-bearing on every board here.

## Replaying them

Five fixtures live in `history/` and all five replay from one command:

```bash
dotnet run --project src/Repro.Replay -- --history history/
```

`Repro.Replay` registers all **five** workflow types, one more than there are fixtures:
nothing has been captured for `WorkflowFileScan` yet, and [REPLAY.md](REPLAY.md) has the
command that captures one. It fails loudly on a history whose type it was not registered
with, and that failure is not a nondeterminism error. See [REPLAY.md](REPLAY.md) for both
messages side by side.

Note what a green file-scan replay would and would not prove. Its histories are the
plainest in the set, one `ScheduleActivityTask` and one long attempt per resume, and
everything interesting in the case lives in the ACTIVITY's heartbeat details, which a
replay never executes. So replay checks the workflow's determinism and says nothing at all
about resume idempotence. `repro_file_scan_verified` is what checks that.
