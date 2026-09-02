# config.yaml

Everything lives in `config.yaml`. All fields are optional and fall back to the defaults
in `src/Repro.Core/Config/ReproConfig.cs`. Durations are Go-style strings (`150ms`,
`10s`, `1m30s`, `0`).

**Unknown keys are a hard error.** A misspelled `failurRate` that quietly means `0.0` is
an afternoon spent staring at a flat panel.

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
| `activity.heartbeatTimeout` | `5s` | required for cancellation; drives the throttle. Applied, see below |
| `activity.startToCloseTimeout` | `10m` | per attempt. Applied |
| `activity.scheduleToCloseTimeout` | `1h` | all attempts. Applied |
| `activity.retry.*` | `1s` / `2.0` / `10s` / `5` | initial, coefficient, max interval, max attempts. Applied |
| `worker.gracefulShutdownTimeout` | `30s` | SDK default is `0s`; see the fault table in [HEARTBEATING.md](HEARTBEATING.md). `demo-down.sh` reads this field and drains for it plus 15s before SIGKILL |
| `worker.maxHeartbeatThrottleInterval` | `60s` | upper bound on the throttle |
| `worker.defaultHeartbeatThrottleInterval` | `30s` | used when the timeout is unset |
| `worker.maxCachedWorkflows` | `0` (SDK default 10000) | set to `1` to force evictions and replay storms |
| `worker.maxConcurrentActivities` / `maxConcurrentWorkflowTasks` | `0` (SDK default 100) | slot counts; `0` leaves the SDK default. Applied by the worker **and** the loadgen. The loadgen used to drop both, so :8078 ran at 100/100 whatever the file said |
| `loadgen.rate` / `concurrency` / `steps` | `5s` / `8` / `20` | traffic shape |
| `simple.enabled` | `true` | run the loadgen's second loop at all; `--no-simple` does the same |
| `simple.maxDuration` | `30s` | how long a `SimpleNoActivity` run waits before ending itself `expired`. Keep it UNDER `demo-down.sh`'s drain budget (`worker.gracefulShutdownTimeout` + 15 = 45s), or teardown SIGKILLs the loadgen mid-run |
| `simple.rate` | `3s` | mean interval between simple starts, before jitter |
| `simple.jitter` | `0.5` | interval is `rate x [1-jitter, 1+jitter]`. `0` is a metronome. Must be under `1`: at `1` the low end is zero and the driver loop busy-spins |
| `simple.concurrency` | `8` | simple runs in flight; at capacity a tick is SKIPPED, never queued |
| `simple.minMessages` / `maxMessages` | `0` / `5` | messages per run, coin-flipped between the `Poke` signal and the `Add` update |
| `simple.messageGap` | `250ms` | upper bound on the random gap between two messages in one run |
| `simple.overflowRate` | `0.05` | fraction of `Add` updates given operands that overflow an `int`. The workflow's update VALIDATOR rejects them, and a rejected update writes nothing at all to history |
| `simple.raceRate` | `0.10` | fraction of runs sent one more message AFTER they close. Expected result is `RpcException`/`NotFound`, counted rather than crashed on |
| `simple.stopWeight` / `cancelWeight` / `expireWeight` | `5` / `3` / `2` | weighted dice for how a run ends: `Stop` signal (Completed), a real client `CancelAsync` (CANCELED, the only path to that status), or nothing so `maxDuration` ends it. Only the ratio matters; the sum must be positive |
| `simpleActivity.enabled` | `true` | run the loadgen's THIRD loop at all; `--no-simple-activity` does the same. NOT `--no-simple`, which is the second loop |
| `simpleActivity.sleepDuration` | `5s` | how long the activity sleeps before fetching the weather. It FLOORS `repro_simple_activity_latency` for every run that reaches the fetch, so a p95 under 5s on the `completed` or `failed` series means the sleep is not happening or the buckets are wrong. The `canceled` series is the exception and legitimately sits below 5s, because a cancel is recorded the instant it lands, mid-sleep |
| `simpleActivity.startToCloseTimeout` | `30s` | per attempt. Must be at least `sleepDuration` + `httpTimeout` + 2s, or every attempt dies of start-to-close before the activity can return and the retry policy burns against a healthy network. With no heartbeat timeout this is the ONLY activity timeout this workflow can produce |
| `simpleActivity.httpTimeout` | `3s` | hard bound on the Open-Meteo call, enforced by the activity itself so the failure is logged rather than opaque. A downed interface fails fast; a BLACKHOLED route does not, so without this the request runs until start-to-close kills the attempt, and the retry chain then outlives `demo-down.sh`'s drain window |
| `simpleActivity.retry.*` | `1s` / `2.0` / `10s` / `3` | initial interval, coefficient, max interval, max attempts. `maximumAttempts: 0` is **rejected** here: `Temporalio.Common.RetryPolicy` reads `0` as *unlimited*, and unlimited retries against a third-party endpoint park the loadgen past the 45s drain budget. Write `1` for "do not retry" |
| `simpleActivity.latitude` / `longitude` | `47.6062` / `-122.3321` | Seattle. Validated to `[-90, 90]` / `[-180, 180]`: Open-Meteo answers HTTP 400 outside that and the activity refuses to retry it, so a typo fails on attempt 1 rather than looking like an outage |
| `simpleActivity.baseUrl` | `https://api.open-meteo.com/v1/forecast` | point it at `http://127.0.0.1:1/forecast` to exercise the synthetic fallback without touching your network |
| `simpleActivity.requireLiveWeather` | `false` | when `true`, an UNREACHABLE endpoint throws instead of falling back to a synthetic reading. It governs that case only: with the flag off, a server that *answered* still fails the run, because the fallback covers transport failure alone. A non-retryable status, a changed schema, or 429/5xx exhausting `maximumAttempts` all give `outcome="failed"` at the shipped setting |
| `simpleActivity.rate` / `jitter` / `concurrency` | `15s` / `0.5` / `4` | third-loop traffic shape. Slower than `simple.rate` because this is the only loop that calls a third party: `15s x 4` is ~4 requests/minute, ~5,760/day, inside Open-Meteo's free tier. Same jitter contract as `simple.jitter` |
| `fault.failureRate` | `0`, shipped as `0.15` | fraction of activity attempts that fail, one roll per attempt, so P(workflow fails) is this to the fifth |
| `fault.latency` | `0`, shipped as `150ms` | latency added per step |
| `fault.stallPastHeartbeatTimeout` | `false` | overrun the heartbeat timeout on attempt 1 |
| `fault.stopHeartbeating` | `false` | keep working, stop heartbeating |
| `fault.ignoreCancellation` | `false` | swallow cancellation and wedge shutdown |
| `fault.decodeRowsToStrings` | `false` | FILE SCAN: decode every scanned row and throw it away. Proves ALLOCATION IS NOT GROWTH, measured **2.41x** bytes read against a **0.01x** baseline, arriving with a flat live-heap floor. Read against the `retainScannedRows` row: 2.41 and 2.54 are nearly the same rate, and the shape of the heap is what differs |
| `fault.retainScannedRows` | `false` | FILE SCAN: the same decode, every string retained for the life of the attempt. Proves RETENTION grows the heap, measured **2.54x**. **Refused together with `fileScan.concurrency > 1`**, with the arithmetic in the message: one retained scan of the largest corpus is about 1.3 GB of live promoted heap, so eight is about 10 GB in one workstation-GC heap and the failure is an OOM-killed worker, not the empty panel you were expecting |
| `fault.slurpWholeFile` | `false` | FILE SCAN: `File.ReadAllBytes` the whole corpus before the loop. Proves a big read is ONE LOH object and the LOH is not compacted, measured **8.63x**. It does **not** produce a heartbeat timeout, since 500 MB off page cache is well under a second; for that you want `stallPastHeartbeatTimeout`. It is also SYNCHRONOUS, so it holds an activity-task thread for its whole duration |
| `fileScan.enabled` | `true` | run the loadgen's FIFTH loop at all; `--no-file-scan` does the same. Off leaves that process with no scan client, no scan worker and no dependency on the corpus existing at all |
| `fileScan.path` | `sample_files/sample-100mb.txt` | the corpus. Resolved to an absolute path against **this file's directory**, never the working directory: [HEARTBEATING.md](HEARTBEATING.md)'s kill recipe runs the built binary from the repo root while `demo-up.sh` runs from elsewhere, so a cwd-relative value silently means two DIFFERENT files across a resume and only the checkpoint's corpus-identity check would ever notice. Validated for shape and **never stat'd**; see below |
| `fileScan.taskQueue` | `repro-scan-queue` | its own queue, in the same namespace. Must be prefix-DISJOINT from `taskQueue` and `localActivity.taskQueue`, not merely different from them. Share a name with `taskQueue` and a second heartbeating activity type lands on the queue whose slot panel sums `temporal_worker_task_slots_used` unfiltered, and that metric carries no `activity_type` label to separate them again |
| `fileScan.targetRowsPerSecond` | `6000` | rows per second; `0` is the UNTHROTTLED sentinel. THE KNOB THAT MAKES THIS A LONG-RUNNING ACTIVITY: unthrottled, a raw-byte scan of the 500 MB corpus finishes in single-digit seconds, which is shorter than one heartbeat throttle interval, so the case emits one heartbeat and demonstrates neither resume nor pressure. Negative is rejected: the pacer's absolute due time would run backwards, every batch would be overdue, and every rows/s panel would keep reporting the configured rate |
| `fileScan.batchRows` | `600` | rows between one pace, cancel, drain, heartbeat and log check and the next, so it IS the loop's reaction time. `batchRows / targetRowsPerSecond` is validated into `[10ms, 2s]`. Above the cap a long batch is not slow, it is **deaf**: `batchRows: 1000000` is a 167-second batch inside which the activity can observe neither a drain nor a cancel nor emit one heartbeat. Below the floor `Task.Delay` rounds a sub-tick sleep UP, so the process runs slower than the rate every panel reports |
| `fileScan.bufferBytes` | `65536` | the single read buffer; range `[4096, 16777216]`. Below the longest row (76 bytes in the shipped corpora) a perfectly legal file fails as "no LF in a full buffer", which names the wrong cause entirely. The range deliberately SPANS the 85,000-byte LOH threshold (a `byte[]` reaches it at 84,976), because crossing it is the cheapest one-line demonstration of that threshold in the repo; above the ceiling the buffer is a slurp with extra steps and neither it nor `fault.slurpWholeFile` can attribute the LOH step to itself |
| `fileScan.maxRows` | `0` | stop after this many rows; `0` is the whole file. A checkpoint written under a LARGER value is a DIFFERENT JOB and the activity refuses to resume from it rather than reporting a total for a question nobody asked. Negative is rejected and is not "unlimited": it would make the completion aggregate negative, so a correct scan would report `repro_file_scan_verified{result="mismatch"}` and throw |
| `fileScan.logInterval` | `10s` | wall clock between progress lines and pressure samples, ONE interval feeding both sinks so Grafana and the console cannot disagree by a tick. At `0` every batch takes a `GC.GetGCMemoryInfo()` sample and prints a line, so the sampler comes to dominate the allocation counter it publishes and the memory panels measure the measurement |
| `fileScan.heartbeatTimeout` | `30s` | chosen for the STALENESS it produces, not for liveness. Floor is `max(5s, 10 x batchPeriod)`: any lower and one GC pause or page-cache miss times the ATTEMPT out on a healthy worker, which reads as "resume is broken" and is the worst way for this case to fail. Ceiling is `worker.maxHeartbeatThrottleInterval`, so raising it past 75s stops increasing the throttle and redone work plateaus at `60s x rate` |
| `fileScan.startToCloseTimeout` | `30m` | bounds ONE attempt, which for attempt 1 is the whole file. Must exceed `heartbeatTimeout`, or every attempt dies of start-to-close before a heartbeat timeout can be observed and the resume path is never taken. Floor is `worstScan + 2m`: below it attempt 1 dies part-way through the corpus on a healthy worker, and every retry resumes and dies at the same place until `maximumAttempts` is gone |
| `fileScan.scheduleToCloseTimeout` | `1h` | total across every attempt, including the cost of every resume. Floor is `worstScan + 9 x (heartbeatTimeout + retry.maximumInterval + throttle) + 2m`; below it the WORKFLOW fails schedule-to-close mid-scan with attempts still on the clock, which also reads as "resume is broken". The derivation is below, because "attempts x startToClose" is the wrong model |
| `fileScan.retry.*` | `1s` / `2.0` / `10s` / `10` | initial interval, coefficient, max interval, max attempts. **10, not the repo's usual 5**: each `kill -9` spends one attempt and [HEARTBEATING.md](HEARTBEATING.md)'s recipe does three cycles, so at 5 one careless extra kill fails the workflow terminally. `0` is **rejected**: `Temporalio.Common.RetryPolicy` reads it as UNLIMITED, and an unbounded chain of half-hour scans holds an activity slot on the scan queue forever. Write `1` for no retry, which also removes the resume this case exists to show |
| `fileScan.rate` / `jitter` / `concurrency` | `6m` / `0.2` / `1` | fifth-loop traffic shape, same jittered-interval and skip-at-capacity contract as `simple.jitter`. `6m` is just over one 4m47s scan, so a scan is in flight essentially always without a second one ever starting. `concurrency` is a pure multiplier on every byte, allocation and buffer in the case, all sharing ONE heap and ONE thread pool, which is why it is 1 and why `fault.retainScannedRows` refuses it above 1 |

## The `activity.*` rows reach the workflow through its input, not through the file

The workflow does not read `config.yaml`. It builds its `ActivityOptions` from
`JobInput.Activity`, and that indirection is the point: options that arrive in the input
are recorded in the history, so a replay reproduces them byte for byte, while a file that
can be edited between the original execution and the replay cannot promise that.

`ActivityOptionsInput.From(config.Activity)` is what closes the gap, and both clients call
it -- `src/Repro.Starter/Program.cs` and `src/Repro.LoadGen/Program.cs`. So the block is
live: edit `startToCloseTimeout` and the next run really does get a different timeout,
captured in its own history. `simpleActivity.*` works the same way via
`SimpleActivityInput.From`.

The one thing to know is the fallback. `JobInput.Activity` is optional with a `null`
default so a history captured before the field existed still deserializes, and a `null`
falls back to `ActivityOptionsInput`'s own positional defaults. Those defaults are exactly
the values shipped in `config.yaml`, which is why they must not be "tidied" independently:
change the file, not the record.

## `simpleActivity` and the synthetic fallback

`WorkflowSimpleActivity` builds its `ActivityOptions` from values its input carried in,
and the loadgen driver calls `SimpleActivityInput.From(config.SimpleActivity)` to put them
there. So this block is live: edit `startToCloseTimeout` and the next run really does get
a different timeout, recorded in its history.

The one thing to understand before reading a green board: if the activity cannot **reach**
Open-Meteo, it logs a warning and returns a stand-in reading tagged `source="synthetic"`
rather than failing. That keeps `demo-up.sh` green with no egress, and it is a deliberate
exception to this repo's rule that a broken thing must never look like a working one. Four
things pay for it:

- `Source` is a field in the returned payload, so `temporal workflow show` shows which
  path ran.
- `Source` is a label on `repro_simple_activity_completed`, so the Bug Signals board shows
  it.
- The fallback logs at WARNING.
- It covers **transport** failure only. A server that answered is never smoothed over: 429
  and 5xx stay retryable, any other 4xx and a changed response schema fail non-retryably.

`requireLiveWeather: true` turns it off entirely.

Cancellation is worth knowing too, and it is the reason this case exists next to
`HeartbeatWorkflow`. No heartbeat timeout means the server has no channel to tell a
running activity it was cancelled, so a client `CancelAsync` records the **workflow** as
`CANCELED` while the activity runs to completion on its own schedule and its result is
discarded. Measured: workflow closed at `T+1s`, activity finished at `T+6s` with a real
reading nobody used. Worker shutdown does still reach it, because that token is local.

## `localActivity`, and the second namespace

The only block that carries a `namespace` and a `taskQueue` of its own, and that is not
organisation. `history.workflowTaskHeartbeatTimeout` is declared server-side as
`NewNamespaceDurationSetting`: it filters by **namespace and nothing finer** — not task
queue, not workflow type. Dropping it from its 30m default to 1m in a namespace of its own is
the only way to make `WorkflowLocalActivity`'s re-execution loop reachable in a demo while the
other four workflows keep stock behaviour.

The override itself is not in this file. It lives in
`observability/dynamicconfig/development-sql.yaml`, constrained to that namespace, and the
namespace is created by `observability/scripts/create-namespace.sh` from an env var in
`observability/compose.yml`. **Four files have to agree and nothing cross-checks them.** A
mismatch is silent in both directions: a wrong namespace in `config.yaml` fails at worker
connect time with an opaque not-found, and a wrong one in the dynamicconfig constraint leaves
the override applying to nothing at all while looking correct.

Two names are checked against the rest of the file at load. `localActivity.namespace` must
differ from `namespace`, because sharing one applies the 1m heartbeat override to the other
four workflows, which have no local activities, so heartbeat behaviour appears in workflows
that cannot cause it. `localActivity.taskQueue` and `taskQueue` must not be a **prefix** of one
another in either direction — not merely unequal. Task queues are namespace-scoped, so the
server accepts a collision and nothing fails at startup; what breaks is every lookup that
matches on queue name without a namespace, which is most of them: `temporal task-queue
describe`, a log grep, a dashboard selector.

`ConfigLoader` refuses `localActivity.namespace` equal to the top-level `namespace`, which is
the one part of that agreement it *can* check.

### The timeout ladder is mostly decorative, on purpose

Three of the four rungs cannot fire at the shipped config. They are documented as unreachable
rather than dressed up as guards, because a rung that looks like a bound and is not is worse
than no rung:

| Field | Shipped | Fires? |
|---|---|---|
| `startToCloseTimeout` | 2m30s | No. Required by the SDK; the workflow task dies at 1m first. |
| `scheduleToCloseTimeout` | 5m | No. Its clock restarts on every workflow-task re-dispatch. |
| `retry.maximumAttempts` | 1 | Not applicable. A re-execution is a fresh attempt-1 execution, outside the retry policy. Must still be non-zero: unset means retry **forever** for a local activity. |
| `runTimeout` | 6m | **Yes. The only one.** Server-enforced. |

Set `scheduleToCloseTimeout` **below** 1m to flip this case from the failure to its
documented fix. Validation deliberately does not forbid that, which is why there is no
`scheduleToCloseTimeout > startToCloseTimeout` rule — such a rule would have made the fix
unconfigurable while looking like ordinary hygiene.

### The knobs that actually change what you see

- `minDuration` / `maxDuration` — the per-run draw, made client-side and then **fixed in the
  workflow input**, which is what keeps a doomed run doomed. At 30s..2m against a 1m timeout,
  exactly `(120-60)/(120-30)` = two-thirds of runs re-execute.
- `concurrency` — 3. A doomed run holds its slot for the whole `runTimeout`, so expect most
  ticks to skip; measured 6 started against 11 skipped in one demo run.
- `maxConcurrentLocalActivities` — 4, against an SDK default of 100. Not politeness: workflow
  activations run on the same thread pool these CPU burns occupy, and the SDK fails a workflow
  task that does not yield within 2 seconds. A saturated pool produces evicted runs and
  retried workflow tasks that look exactly like this case's real failure and are not it.

`--no-local-activity` turns the loop off, and with it every dependency this process has on the
second namespace existing — useful against a stack created before this feature, where
`create-namespace.sh` has not run in its two-namespace form.

## `fileScan`, and the corpus it does not check for

`WorkflowFileScan` gets a task queue of its own and stays in `default`, so unlike
`localActivity` there is no second client and no second namespace to keep in agreement.
What it does have is a real file on disk, and this block is where the two things that
follow from that are settled: what the file has to look like, and what happens when it is
not there.

### The corpus contract

Line 1 is the row count `N`, authoritative and cross-checked by
`scripts/gen-samples/gen-samples.sh --verify <path>`. Each of the next `N` lines is
`%010d [w w w w w w w]\n`: ASCII, LF, a 10-digit zero-padded index, one space, seven
3-to-8-letter words in brackets. Rows are **41 to 76 bytes including the LF, not fixed
width**, of which exactly 20 bytes are fixed overhead.

For the shipped `sample-100mb.txt`: `N` = **1,724,588**, **99,999,968** bytes, header
length **8** (`digits(N) + 1`). The activity checks `headerLen == digits(N) + 1` on open,
because every byte identity in the case derives the header length from the row count alone.

**A CRLF corpus fails on row 1, loudly, and that is the design.** The read loop splits on
LF, so on CRLF input the last byte of every row is `\r` rather than `]` and the row parser
reports a malformed corpus immediately. Tolerating it would mean one byte of cursor drift
per row, no exception, and a resume that lands mid-row several hundred thousand rows later.

Generate the four corpora with `scripts/gen-samples/gen-samples.sh`. They total about
1.15 GB, which is why nothing generates them for you: that is not a demo script's decision,
and `demo-up.sh` never makes it.

### Absent corpus, and why `dotnet test` still passes without one

`sample_files/` is gitignored, so a fresh clone has no corpus and `fileScan.enabled` still
ships `true`. Four rules make that safe, and they are worth knowing before "fixing" any one
of them:

1. **Validation checks shape and never stats the file.** `ConfigTests` calls
   `ConfigLoader.Load` against the committed `config.yaml`, so a `File.Exists` or a
   `FileInfo.Length` anywhere in `ValidateFileScan` would break `dotnet test` on every
   fresh clone. This is also why the timeout ladder's derived floor comes from
   `fileScan.maxRows` when it is set and from a hand-maintained largest-corpus constant
   (8,622,570 rows) otherwise, never from the file on disk.
2. **`path` resolves against the resolved `config.yaml`'s directory**, not the cwd, and is
   rewritten in place during validation. That is what makes the absolute path on the
   `RESUMING` console line worth printing.
3. **The loadgen driver checks once, at construction, and skips its whole loop** with a
   named banner: `file-scan: OFF (corpus not found at <path>; run
   scripts/gen-samples/gen-samples.sh)`. It is logged *after* the readiness banner
   `scripts/demo-lib.sh` greps with a 45s budget, so a missing corpus cannot turn a working
   start into a `demo-up.sh` timeout.
4. **The scan worker registers the activity anyway.** `enabled` and `--no-file-scan` turn
   off the loadgen's driver loop, not this process's ability to run a scan you start by
   hand, so a corpus generated later needs no worker restart. Invoke a scan with the corpus
   still missing and the activity throws **non-retryable** on attempt 1: a missing file is a
   config bug, not a transient fault, and burning ten attempts on it buries the cause under
   an `ActivityFailure` chain.

### The timeout ladder, derived

Three rungs, and every one is derived rather than picked. `worstScan` below is
`rowsToScan / targetRowsPerSecond`, which at the shipped 6000 rows/s is 4m47s for the
100 MB corpus and **23m57s** for the 500 MB one.

| Rung | Shipped | Floor `ConfigLoader` enforces |
|---|---|---|
| `heartbeatTimeout` | 30s | `max(5s, 10 x batchPeriod)`, and it must be under `startToCloseTimeout` |
| `startToCloseTimeout` | 30m | `worstScan + 2m` |
| `scheduleToCloseTimeout` | 1h | `worstScan + 9 x (heartbeatTimeout + retry.maximumInterval + throttle) + 2m` |

`heartbeatTimeout` is the rung to understand first, and it is **not** set for liveness. The
maximum gap between two `Heartbeat()` calls is one batch period, 100 ms, so 30s is 300x
margin. What it sets is Core's throttle,
`min(0.8 x heartbeatTimeout, worker.maxHeartbeatThrottleInterval)` = **24s**, and therefore
how much work a `kill -9` destroys the RECORD of: `24 x 6000` = **144,000 rows, 8.35% of
the 100 MB corpus**. It saturates, because the second term binds past 75s.

`startToCloseTimeout: 30m` **covers** the shipped corpora rather than guarding anything;
its honest role is catching an attempt that keeps heartbeating and never finishes. It
becomes a live guard the moment you drop the rate or point at something bigger, and then
validation fails at startup naming the value it needs.

`scheduleToCloseTimeout` is the one where the obvious model is wrong. "attempts x
startToClose" gives an absurd number, because useful work is **one** worst-case scan
however many attempts it takes. Each *resume* adds `heartbeatTimeout` (the server noticing
the missing heartbeat) plus `retry.maximumInterval` (backoff) plus the throttle (the reading
that is redone) = **64s** at the shipped values. Nine resumes on the 500 MB corpus is
`23m57s + 9 x 64s + 2m` = **35m33s**, so 1h leaves about 1.7x headroom.

Nine, not `maximumAttempts - 1`: the number is fixed rather than derived, so that lowering
`maximumAttempts` on a whim cannot quietly lower the floor with it and leave a check that
can never disagree.

### `--rows-per-second` can only make the approved worst case shorter

The starter's `--file`, `--rows-per-second` and `--max-rows` land **after** validation, so
they have to repeat the rules the ladder was derived under rather than trust them.
`--rows-per-second` must therefore be `0` or **at least** `fileScan.targetRowsPerSecond`,
and `--max-rows` must be inside `[1, fileScan.maxRows]` when that is bounded. A faster rate
or a smaller row bound both shorten the scan and are allowed; slowing a scan down is a
`config.yaml` edit, because that lengthens the worst case the ladder was checked against.

## Metrics addresses

Listen addresses must be a full `IP:port` that is **not** loopback. Go's `":8077"` is
accepted and normalized, but Core parses these with Rust's `SocketAddr`, which rejects a
bare `:port`. And `127.0.0.1` is unreachable from the Prometheus container while
`curl localhost:8077` on the host still works. Both are rejected at startup with an
explanation rather than left to fail later.

`--metrics <ip:port>` overrides the configured address on the worker, the loadgen and
the replayer. **`--metrics off`** starts no exporter and binds no port at all, which is
how you run a second worker on this host without fighting the first one for :8077.

`off` is a flag value only: `metrics.listenAddress: off` in the file is still rejected,
and the error says so. It means no *exporter*, not no *runtime*. The process still
adopts a telemetry-free `TemporalRuntime`, because a client that connects without one
binds to `TemporalRuntime.Default` and loses its metrics silently.

## Secrets

Keep them out of the committed file. Put them in `config.local.yaml` (gitignored) and
pass `--config config.local.yaml` to any binary.
