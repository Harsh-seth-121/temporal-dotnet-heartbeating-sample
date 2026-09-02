# Heartbeating

This is what the repo is for. Start with the **Repro / Heartbeating** dashboard.

## The throttle

Core sends at most one heartbeat RPC every
`min(HeartbeatTimeout × 0.8, MaxHeartbeatThrottleInterval)`, no matter how often your
code calls `Heartbeat()`. At the shipped `heartbeatTimeout: 5s` that is one every
4 seconds. The activity calls it once per step, so every **1.15 s**
(`job.stepDuration: 1s` plus `fault.latency: 150ms`).

The activity publishes its own cadence as `repro_heartbeat_call_interval_ms`, latency
included. The board plots both rates against `repro_heartbeat_throttle_ms`, so the gap
is on screen.

There is no heartbeat metric in any Core SDK. Every panel on the Heartbeating board is
a proxy, a server-side consequence, or something this repo emits itself. The two
proxies are `temporal_request{operation="RecordActivityTaskHeartbeat"}` on the SDK side
and `activity_task_timeout{timeout_type="Heartbeat"}` on the server side. That second
one is not a `heartbeat_timeout` metric; no such metric exists in server 1.31.2. See
[GOTCHAS.md](GOTCHAS.md).

An activity that does not heartbeat can never be cancelled. The server only
communicates cancellation in the **response** to a `RecordActivityTaskHeartbeat` RPC.
No `HeartbeatTimeout` plus no `Heartbeat()` calls means nothing except worker shutdown
can stop the activity.

## Stale checkpoints

The checkpoint the server holds lags the work, so a resumed attempt redoes steps. The
activity stamps every heartbeat and records `repro_heartbeat_staleness` on resume,
which makes the cost measurable.

Observed on this stack: **5.8 s, 6.3 s, 12.7 s**. Those exceed the 4 s throttle bound
because staleness is throttle lag plus retry backoff. The larger values are third and
fourth attempts.

Resume must therefore be idempotent. Watch it happen:

```
resuming at step 2 of 12; checkpoint was 6265ms old (attempt 3)
```

## Kill the worker mid-activity

1. Free :8078 first with `./scripts/demo-up.sh --no-loadgen`, or start the second worker
with `--metrics off`. A `demo-up.sh` with no flags owns both :8077 and :8078.

Run the **built binary**, not `dotnet run`. `dotnet run` launches the app as a child
   process, so killing the parent leaves the child running and holding :8077.

   ```bash
   ./src/Repro.Worker/bin/Debug/net10.0/worker
   ```

2. Second terminal: `dotnet run --project src/Repro.Starter`

3. Read the progress gauge and call the number M. The activity logs once at the start
   and once per resume, never per step, so there is no "Progress: N" line to wait for.

   ```bash
   curl -sS localhost:8077/metrics | grep '^repro_activity_progress'
   ```

4. `kill -9`, not a plain kill. SIGTERM starts a graceful drain: the activity
   checkpoints once, then keeps working and keeps heartbeating for the whole
   `worker.gracefulShutdownTimeout` (30 s shipped), so it runs past M and holds :8077
   that entire time. `-9` is what "the worker died" actually looks like.

   ```bash
   kill -9 $(lsof -ti tcp:8077)
   ```

5. Restart the binary. It prints the resume line within a second of starting to poll.

   ```
   resuming at step 26 of 60; checkpoint was 10167ms old (attempt 4)
   ```

Three cycles on this stack, M read immediately before each `kill -9`:

| M | resume step | staleness printed |
|---|---|---|
| 9 | 8 | 7154 ms |
| 18 | 19 | 7561 ms |
| 28 | 26 | 10167 ms |

Read that carefully, because the obvious summary is wrong. The resume step lands at or
below `M + 1`. The shortfall is throttle lag, at most 4 s, which is three steps at the
shipped 1.15 s cadence, and sometimes it is **zero**, because the kill landed just
after Core flushed a heartbeat. Do not expect to lose work every time.

The printed staleness is much larger than the 4 s throttle bound for a different
reason: the server does not know the worker is gone until the 5 s `heartbeatTimeout`
expires, and then waits out the retry backoff before attempt N+1 starts.

## Kill the worker mid-SCAN, which is the better version of that test

Same mechanism, same two traps, a far better subject. The seed activity's checkpoint is
over synthetic steps, so nothing is genuinely reprocessed and there is no way for a resume
to be *wrong*. `WorkflowFileScan`'s activity reads a real file, restores an accumulator
from the same heartbeat as its byte cursor, and closes with a closed-form check that says
out loud whether the resume was idempotent. Run this one instead when you want to see
resume work rather than see heartbeats arrive.

Start **one** scan, from the starter rather than the loadgen, so exactly one execution is
writing the untagged cursor gauge:

```bash
./scripts/demo-up.sh --no-loadgen      # frees :8078, and starts no scan loop
dotnet run --project src/Repro.Starter -- --file-scan
```

It needs the corpus. `sample_files/` is gitignored, so generate it first with
`scripts/gen-samples/gen-samples.sh` or the whole board reads NODATA and the driver logs
`file-scan: OFF (corpus not found at <path>)`.

The activity prints a progress line every `fileScan.logInterval`, about 29 of them over the
4m47s scan. Read the row off the last one and call it M. Then, mid-scan:

```bash
kill -9 $(lsof -ti tcp:8077)
./src/Repro.Worker/bin/Debug/net10.0/worker
```

Both traps from the recipe above apply unchanged, and the second one matters MORE here:

- Run the **built binary**, not `dotnet run`, which launches the app as a child process, so
  killing the parent leaves the child holding :8077 and still reading the corpus.
- Use **`-9`, not SIGTERM**. A SIGTERM drains gracefully, and this activity checkpoints on
  the `WorkerShutdownToken` **edge** and then keeps reading until `ctx.CancellationToken`
  fires `worker.gracefulShutdownTimeout` later. So a drain loses almost nothing, which is
  the opposite of what you are trying to see. It is worth watching once on its own: total
  drain reaction is **30.1s** (the grace window plus one 100 ms batch period) against
  `demo-down.sh`'s 45s budget, so a 4m47s activity is not meant to finish inside the drain
  window and does not need to.

What to expect, and where each number comes from:

| Reading | Expect | Why |
|---|---|---|
| `RESUMING at row N` in the console | N roughly **144,000** below M | `min(0.8 x 30s, 60s)` = 24s of throttle x 6000 rows/s |
| the drop on **Row cursor vs resume floor vs corpus ceiling** | **8.35%** of the corpus, drawn to scale | 144,000 of 1,724,588 rows |
| **Rows redone this range** | about **144,000**, up to ~4.2% low | `kill -9` also loses the record of up to one 1s scrape, 6,000 rows |
| printed staleness | above 24s, up to roughly **64s** | throttle, plus the server noticing (`heartbeatTimeout`), plus `retry.maximumInterval` |
| `scan COMPLETE` | `indexSum 1487102747166 == expected`, `wordByteSum 65508200 == expected`, end offset **99999968** | closed forms over `sample-100mb.txt`, independent of how many times it resumed |
| `repro_file_scan_verified` | `{result="match"}` | **A `mismatch` here is the one failure that must never pass.** It means rows were double-counted or skipped across the resume |

Those three totals came out **identical from a full scan and from three separate resume
points**, which is the measurement this case exists to produce.

That table is the row-cursor equivalent of the seed recipe's measured-run table above, but
read the difference: its Expect column is **derived** from the throttle arithmetic rather
than transcribed off a run. The one number this case pins by measurement is the aggregate,
which is the useful one, because it does not depend on where any kill landed. Run the three
cycles and read your own M, resume row and staleness off the console if you want those
recorded here too.

Do not expect a drop every time. The shortfall is throttle lag and it is sometimes **zero**,
because the kill landed just after Core flushed a heartbeat. That is the same caveat the
seed recipe carries, and it is why the resume row lands at or below `M + 1` rather than at a
fixed distance below it.

### Prove idempotence a second way, with no kill at all

This is the cleanest proof available, and `temporal activity reset` is the verb for it.
Take one in-flight scan and run the two forms back to back:

```bash
# KEEPS the heartbeat details: the retried attempt resumes from the same checkpoint,
# re-reads the rows the throttle lost, and counts them once.
temporal activity reset -w repro-file-scan --activity-id <id>

# DISCARDS them: the next attempt starts over at row 1 and offset 8.
temporal activity reset -w repro-file-scan --activity-id <id> --reset-heartbeats
```

**The aggregate comes out right either way.** That is the point: one path exercises the
resume arithmetic and the other bypasses it entirely, and `indexSum` lands on
1,487,102,747,166 in both. This section of the doc has claimed that flag's behaviour for a
while without a case worth demonstrating it on; this is the case.

Get the activity id from `temporal workflow describe -w repro-file-scan`. A reset also
unpauses a paused activity unless you add `--keep-paused`, and a mid-flight activity only
observes the reset on its next heartbeat, so on this case that is within one 100 ms batch.

## Fault knobs

`config.yaml` ships the first two faults on, so the failure and latency panels move
from the first run:

```yaml
fault:
  failureRate: 0.15     # ONE roll per activity ATTEMPT; a hit is a retryable failure
  latency: 150ms        # added to every step
```

Zero both for a clean baseline. **Six** faults ship **off**, three for the seed heartbeating
activity and three for the file scan, and each one proves a specific claim. Turn on exactly
one at a time, then restart the worker or loadgen. A knob that moves two panels at once
cannot attribute a symptom to a cause, which is also why `fault.readAllLinesFirst` is
deliberately not offered: it would be the union of two of the rows below.

| Knob | What it proves | Watch |
|---|---|---|
| `stallPastHeartbeatTimeout` | The server can only tell an activity to stop via the **response to a heartbeat RPC**. Stop heartbeating and the attempt is timed out server-side while your code keeps running, oblivious. | Heartbeating board, "Heartbeat timeouts", and **nothing else**. It is gated to attempt 1, so attempt 2 runs normally and the workflow still ends `completed`. |
| `stopHeartbeating` | An activity that stops heartbeating can **never be cancelled**. This one is not gated to attempt 1, so it starves all five attempts. It is the knob that produces `outcome=timed_out`. | Heartbeat RPC rate falls to zero while `repro_activity_progress` climbs, then Bug Signals shifts to `timed_out`. Watch the gauge against ONE execution: the starter, or loadgen `--concurrency 1`. It is a single series per worker process and the last writer wins. |
| `ignoreCancellation` | `TemporalWorker.ExecuteAsync` does not return until every executing activity returns, and `gracefulShutdownTimeout` does **not** bound that. It only controls *when* `ctx.CancellationToken` fires. | Ctrl-C the worker and watch it refuse to exit. |
| `decodeRowsToStrings` | **Allocation is not growth.** A `string` per scanned row is about 140 B of gen0 garbage, so allocated lands at **2.41x** bytes read (measured, against a **0.01x** baseline) while the LIVE heap floor does not move at all. | File Scan board: "Allocation amplification" goes to ~2.4, `gen="0"` climbs, and "Memory" sawtooths on a **flat floor**. The flat floor is the claim. |
| `retainScannedRows` | **Retention grows the heap.** The identical garbage, kept in a list, becomes promoted live gen2. Allocation barely moves (**2.54x**), which is why the two knobs have to be read together: the *rate* is nearly the same and the *shape* is what differs. | "Memory" becomes a staircase with **no falling edge**, `gen="1"` and `gen="2"` appear, "GC pause time" reaches single digits, and "Rows/s achieved vs target" dips below 6000. **Refused** with `fileScan.concurrency > 1`: that is an OOM, not a panel. |
| `slurpWholeFile` | **A big read is one LOH object, and the LOH is not compacted**, so committed bytes step up and do not come back. Allocation reads **8.63x**. | `loh_bytes` and `working_set_bytes` both step and stay. **They step at the NEXT GC, not in the sample that allocated the array**: `GCMemoryInfo` describes the last collection, not now. It does **not** produce a heartbeat timeout, since 500 MB off page cache is well under a second. |

The scan knobs are also how you get the File Scan board's deliberately-empty series to
appear. `gen="2"` is ABSENT rather than zero on a shipped-config scan, and
`bytes_allocated` reads near zero because the default path really does allocate nothing per
row. [DASHBOARDS.md](DASHBOARDS.md) names each one.

## What a measured run looks like

Measured over 9.5 minutes with `failureRate: 0.15`, `latency: 150ms` and loadgen at
`--rate 2s --concurrency 8 --steps 12 --step-duration 400ms`: 280 workflows at 0.49/s,
322 activity attempts, 40 injected failures and 39 retried attempts. **0.124 of
attempts failed, converging on the configured 0.15**, so roughly 0.07/s of each.

(The 400 ms there is `--step-duration 400ms`, which makes
`repro_heartbeat_call_interval_ms` read 550. It is not the shipped value.)

The outcome split is **all `completed`**, and that is correct rather than broken.
`failureRate` is one roll per ATTEMPT, so a workflow only fails when all five attempts
roll a failure: `0.15^5`, about one in thirteen thousand. Push `failureRate` to `0.8`
if you want `failed` on the Bug Signals board, since `0.8^5` is one workflow in three.

An earlier build rolled once per STEP, which made P(attempt fails)
`1 - (1 - r)^steps`: 86% at this loadgen shape and 99.99% at the shipped
`job.steps: 60`. Any number you remember from before that fix is wrong by more than an
order of magnitude.

Do not read much into the two latency p95s at this shape. Twelve steps of 550 ms is a
6.6 s activity, which lands whole inside the single `[5 s, 10 s)` override bucket, so
`histogram_quantile` interpolates ~9.7 s from one bucket's worth of information. That
is the failure mode `HistogramBuckets.cs` exists to prevent, relocated: bucket edges
have to straddle the durations you actually run.

## Poke a live execution

The worker stays running, so the `temporal` CLI reaches in-flight runs:

```bash
temporal workflow describe -w repro-workflow
temporal workflow cancel   -w repro-workflow
```

`HeartbeatWorkflow` has no signals, queries or updates, so `workflow signal` and
`workflow query` have nothing to reach on `repro-workflow`. `SimpleNoActivity` is the one
that answers messages, and the loadgen's second loop keeps a handful of them open. Grab an
ID from `temporal workflow list --query 'WorkflowType="SimpleNoActivity"'`, then:

```bash
temporal workflow signal -w <id> --name Poke   --input '{"Note":"hi"}'
temporal workflow signal -w <id> --name Stop
temporal workflow query  -w <id> --type GetStatus
temporal workflow update execute -w <id> --name Add --input '{"A":1,"B":2}'
```

Payload property names are PascalCase; the .NET converter does not camel-case them. Wire
names drop the `Async` suffix on signals and updates but not on queries. Handler list and
the rest of the shape in [WORKFLOWS.md](WORKFLOWS.md).

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

A reset also unpauses a paused activity unless you add `--keep-paused`. An activity
that is mid-flight only observes the reset on its next heartbeat, failure or timeout,
and it arrives as a `Canceled` failure, so clean-up code has to re-throw it.

`temporal workflow cancel` is the one to try first. The activity is scheduled with
`CancellationType.WaitCancellationCompleted`, so the workflow does not report cancelled
until the activity has observed the request on its next heartbeat response and unwound.
Watch `repro_activity_cancel{reason=...}` on the Heartbeating board. The reason is
Core's own `ActivityCancelReason`.

`temporal workflow describe` prints no `Status` line for open runs, only closed ones.
Use `HistoryLength` to tell a queued run from a finished one.
