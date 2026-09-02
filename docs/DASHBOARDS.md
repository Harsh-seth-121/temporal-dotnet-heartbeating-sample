# Dashboards

Grafana on <http://localhost:3000>, no login. Three folders, ten dashboards:

- `sandbox/` holds the boards written for this topology: 6 dashboards, 80 panels, 118
  targets. The probe result below covers 84 of those targets and is **stale**: the
  file-scan board's 19 have not been probed against a live stack at all, because that
  needs a generated corpus. See the note on it below.
- `temporal-server/` and `temporal-sdk/` hold boards imported from
  [temporalio/dashboards](https://github.com/temporalio/dashboards) as-is, for breadth,
  pinned to commit `4994df2` in `grafana/dashboards/UPSTREAM_SHA`.

Start with **Repro / Heartbeating**. It has no upstream equivalent and it is the reason
to build rather than only import. There is no heartbeat metric in any Core-based SDK, so
every panel on it is either a proxy, a server-side consequence, or something this repo
emits itself.

Regenerate the authored boards with `python3 observability/grafana/build-dashboards.py`,
from any directory. Grafana picks the change up within 10s.

## Proving the boards

`grafana/probe-dashboards.py` runs every target on every board it is pointed at twice:
as the panel ships it, and again with `or vector(0)` stripped. A single check is useless
in both directions, because an expression ending in `or vector(0)` always returns a row
and so proves nothing about whether the metric exists.

```bash
python3 grafana/probe-dashboards.py             # the six authored boards
python3 grafana/probe-dashboards.py heartbeat   # one board, by file stem
python3 grafana/probe-dashboards.py filescan    # the newest one
python3 grafana/probe-dashboards.py --vendored  # the four imported boards
python3 grafana/probe-dashboards.py --all       # both
```

States: `OK` (both modes return data), `FALLBACK` (renders via `or vector(0)` because
the series has not been created yet, expected for anything that only appears when
something goes wrong), `NODATA` (neither, and no fallback, so either the wrong stack
state or a wrong metric name), `ERROR` (always a bug).

Only the authored boards gate the exit code. The vendored ones are not ours to fix, and
upstream writes them against a fully loaded cluster, so plenty of their panels are
legitimately empty here. Probing them is still worth doing: it is how "leave all three
`PrometheusOptions` suffix flags false, because that is what the vendored Core SDK board
is written against" stops being an assertion and becomes a measurement.

Measured against a live stack brought up by `./scripts/demo-up.sh` with the worker and
loadgen running, shipped `config.yaml` (`failureRate: 0.15`), on a Prometheus started fresh
for this run:

**STALE. This block reports 84 targets and 57 panels, against the 118 targets and 80 panels
that ship today, so it was captured before the newest boards existed and the `sandbox-filescan`
board is not in it at all. Re-run `python3 grafana/probe-dashboards.py` against a live stack
with the corpus generated, and paste what it prints.** Nothing in it has been adjusted by
hand to match the new totals, per the rule at the end of this section.

```
authored: 66 OK  +  17 FALLBACK  =  83/84 targets render
authored: 1 NODATA, 0 ERROR
authored: 57/57 panels have at least one series
```

Both `simple-activity` targets scored `OK`, and the p95 read 5926 ms, between the 5500 and
6000 boundaries, which is a live fetch sitting on top of the 5s sleep exactly where
`HistogramBuckets.cs` says it should.

**The 1 NODATA is `Checkpoint staleness on resume p95`**, and it is honest rather than
broken. `repro_heartbeat_staleness` is recorded only when an activity RESUMES from a
heartbeat checkpoint, so nothing emits it until a worker is killed mid-activity. That target
carries no `or vector(0)`, so it cannot fall back to a zero line the way most
only-on-failure targets do. It reads `NODATA` until you run the `kill -9` resume test in
[HEARTBEATING.md](HEARTBEATING.md). Its row is in the table below.

Read the numbers this way: the OK/FALLBACK split moves with stack state and with how long
the stack has been up, so do not treat 66/17 as a target. `0 ERROR`, and every target and
panel accounted for, are the parts that must not move. `NODATA` is only acceptable with a
named reason, which is why the one above is named.

Do not hand-edit this block or do arithmetic on it. Re-run
`python3 grafana/probe-dashboards.py` against a live stack and paste what it prints. Note
that its exit code is `1` only on an authored `ERROR` -- a `NODATA` exits `0`, so read the
summary lines rather than trusting `$?`.

To move the `FALLBACK` targets into `OK`, put the stack in the state each needs:

| Targets | State needed |
|---|---|
| heartbeat timeouts | `fault.stallPastHeartbeatTimeout: true` |
| `timed_out` outcomes | `fault.stopHeartbeating: true`. **Not** `stallPastHeartbeatTimeout`, which only stalls attempt 1, so attempt 2 completes and the outcome stays `completed` |
| cancellation reasons | `temporal workflow cancel -w repro-workflow`, or Ctrl-C the starter |
| sticky cache miss / forced eviction / replay pressure | `worker.maxCachedWorkflows: 1` |
| checkpoint staleness on resume (**NODATA**, not FALLBACK, because it has no `or vector(0)`) | `kill -9` the worker mid-activity so the next attempt resumes from a heartbeat checkpoint; see [HEARTBEATING.md](HEARTBEATING.md) |
| non-determinism (SDK and server) | break the workflow and replay, or edit it while a run is in flight |
| RPC and poll failures | stop the server mid-run, or throttle it |
| simple-activity `outcome="failed"` | `simpleActivity.requireLiveWeather: true` with an unreachable `simpleActivity.baseUrl` |
| simple-activity `source="synthetic"` | an unreachable `simpleActivity.baseUrl`, e.g. `http://127.0.0.1:1/forecast`, or no egress |
| simple-activity `outcome="canceled"` | `temporal workflow cancel -w repro-weather-<hex>` mid-sleep. The third loadgen loop sends no cancels, by design, so this one is hand-only |
| the whole file-scan board (**NODATA**, not FALLBACK) | generate the corpus: `scripts/gen-samples/gen-samples.sh`. Four series on that board stay empty even with it, each for a named reason; see the file-scan section below |

## Known-empty official panels

Panel counts from `probe-dashboards.py --vendored` against a running stack with worker
and loadgen and faults on. Reproduce them rather than trust them, they move with stack
state:

| Dashboard | With data | Empty | Non-query |
|---|---|---|---|
| `temporal-sdk/core-sdks-otel` | 24 | 2 | 2 |
| `temporal-server/server-general` | 31 | 4 | 1 |
| `temporal-server/frontend-service` | 14 | 1 | 1 |
| `temporal-server/history-service` | 33 | 17 | 1 |

102 of 126 panels, 124 of 182 targets, and **0 ERROR**. No imported expression is
rejected by this Prometheus. "Non-query" is a text panel or a panel whose only target
ships a blank `expr` (the probe skips both). Grafana `row` headers are not counted at
all.

The SDK board scores far better than the Go original's equivalent did (19 of 34) for one
reason: it is written against exactly the metric names a Core-based SDK emits, which is
why all three `PrometheusOptions` suffix flags stay at their defaults. That premise is
checked panel by panel, not asserted. Its only two empty panels are
`temporal_workflow_task_execution_failed` and `temporal_sticky_cache_miss`, both
**absent counters** on a worker where nothing has failed and nothing has missed the
sticky cache, not name-shape mismatches. Every bare-name, millisecond-histogram selector
on that board resolves.

### The empties are correct, not broken

Four groups. Each claim was checked against `curl localhost:8000/metrics`, not inferred
from the panel title.

- **A family that has never been emitted here.** Most of the empty targets are
  server-side error counters: `service_errors_entity_not_found`,
  `service_errors_resource_exhausted`, `persistence_errors`, `cache_errors`,
  `acquire_lock_failed`, `task_errors_discarded`, `stale_mutable_state`,
  `failed_workflow_tasks`, `multiple_completion_commands`. No `# TYPE` line at all, so
  absent rather than zero, exactly like Core. Raising `fault.failureRate` will **not**
  fill them: an activity failure is a normal completion path as far as the server is
  concerned. Stop the server mid-run or throttle persistence instead.
- **A family that exists, under label values the panel does not select.** Bare
  `service_errors` is here but only with `service_name="matching"`, while the panels
  select `frontend` and `history`. `cache_requests` and `cache_latency` are here under
  `HistoryCacheGetOrCreate`, `HistoryCacheGetOrCreateCurrent`, `EventsCacheGetEvent` and
  `EventsCachePutEvent`, while "Mutable State Load Counts" hard-codes
  `HistoryCacheGetCurrentExecution`. `task_attempt` is here under
  `TransferActiveTaskActivity` and `TimerActiveTaskActivityTimeout`, while the two "Task
  Attempt Stats" panels hard-code `TransferActiveTaskCommand` and
  `TimerActiveTaskCommand`. Upstream is not wrong; it is written against a different
  server build than the one pinned in `.env`.
- **A grouping label that does not exist on this endpoint.** `GC Counter` is the
  instructive one: `memory_num_gc` really is exported and really has data, tagged
  `service_name`, but the panel groups by `kubernetes_pod_name`, of which there are
  **zero** occurrences in the whole scrape. Every `kubernetes_pod_name` panel is empty
  for that reason and no amount of load will change it. Separately, there genuinely are
  no `go_*` / `process_*` series: the server's Prometheus registry is private.
- **Permanently empty because of this topology.** `Shard Rebalancing`, `Shards Closed`
  and `State Transition` all read `shard_closed_count` / `sharditem_removed_count`,
  which a single-node deployment never emits.

Three empties are one command away from filling. `Workflow Completion Overview` and
`Workflow Timedout / Cancelled By Namespace` read `workflow_cancel` and
`workflow_timeout` at `operation="CompletionStats"`, so
`temporal workflow cancel -w repro-workflow` or `fault.stopHeartbeating: true` fills
them.

The `$Service` and `$Client` variables on `server-general` are decorative. They appear
only in panel titles, never in any expression.

## Repro / Local Activity

Its own board, and the reason is mechanical rather than editorial: the `$namespace`
variable is **single-select** and this case runs in `repro-local-activity` while everything
else runs in `default`. Panels for both cannot coexist on one board — pointing the variable
at one namespace blanks every panel selecting the other. So this board ships with its
variable defaulting to `repro-local-activity` and a **3h** default window instead of 30m.

The window matters. A doomed run holds its concurrency slot for the whole 6m `runTimeout`,
so events arrive roughly every three or four minutes. On a 30m window Grafana resolves
`$__rate_interval` to about 5m, and measured, `rate(...[5m])` returned **0** over the same
data where `rate(...[15m])` returned 0.0045. The two histogram panels therefore use
`[$__range]` rather than `$__rate_interval`; a sparse rate window renders `NaN`, not a flat
line.

Start at **Executions /s vs completions /s**. The gap between the two lines is CPU burnt on
local activities that were thrown away. Measured at the shipped config: about **14
executions per completed run**.

### Two panels that need reading carefully

**Custom: repro local-activity outcomes /s** does *not* account for every run, unlike the
equivalent panel for the other four workflows. A run killed by `runTimeout` is closed
without a workflow task, so workflow code never resumes and the counter never increments —
not even as `timed_out`. The **Workflow outcomes, server view** panel beside it is the only
place those runs appear.

**Core's local activity execution latency** times one execution of the burn; the workflow
latency panel beside it times the whole workflow. On a re-executed run the SDK records
several of the former and none of the latter, so one keeps moving while the other goes
quiet.

### Server metrics here need the other spelling

The server sanitizes label values and the SDK does not, so this one namespace is spelled two
ways in one TSDB, on two different label keys:

```
:8077   namespace="repro-local-activity"   task_queue="repro-la-queue"
:8000   namespace="repro_local_activity"   taskqueue="repro_la_queue"
```

`srv()` hard-pins `namespace="default"`, so a server panel for this case built with it
matches nothing, forever, with no error. `srv_ns()` exists for that and takes the sanitized
spelling.

### A bucket-override bug this board caught

Worth recording because the tests could not see it and the panel looked fine.

The row for `temporal_local_activity_execution_latency` was written with the `temporal_`
prefix already in the name, while `Custom=false` makes `HistogramBuckets` prepend it — so
the key became `temporal_temporal_local_activity_execution_latency` and matched nothing. The
build stayed green, the substring-collision test passed (a double-prefixed key collides with
nothing), and the reachability test passed (it only inspects `repro_` keys). The only symptom
was a live scrape returning `le=[50,100,500,1000,2500,10000,+Inf]` — Core's catch-all, a
populated panel, and a p95 capped under 10s on a metric that runs for a minute.

`TelemetryTests` now asserts no scrape key starts with `temporal_temporal_`.

One consequence outlives the fix: Prometheus keeps the old `le` layout for its full 2d
retention, so `sum by (le)` merges two incompatible bucket sets and reports **negative**
per-bucket counts until the old series age out. If a quantile on this metric reads a flat
value pinned to a boundary, check for that before believing it.

## Repro / File Scan

12 panels, 19 targets. Its own board, and unlike the local-activity board the reason is not
mechanical: `WorkflowFileScan` stays in the `default` namespace, so this board **opens on
`default`** and keeps the 30m default window like every other board here. What earned it a
board is that half its panels are about the worker's memory and GC, which no other case in
this repo emits any signal on at all.

Two blocks, in the order you ask the questions. Panels 1 to 6 answer "is the scan working,
and what did a resume redo". Panels 7 to 12 answer "what is this costing the worker".

Start at **Row cursor vs resume floor vs corpus ceiling**. Three untagged gauges, and every
drop from the cursor to the floor is work the next attempt has to redo, drawn to scale
against the whole corpus. At the shipped config a `kill -9` drops it about **144,000 rows,
8.35% of `sample-100mb.txt`**.

**The ceiling is a metric, not a literal.** `repro_file_scan_rows_expected` is the corpus's
own first line, so nothing on this board hard-codes 1,724,588 and swapping corpora moves the
line instead of lying about it. A literal would be wrong for three of the four shipped
corpora, and wrong in a way that RENDERS: a progress percentage stuck at 20%, or one over
100%.

### Named NODATA and near-zero reasons

`NODATA` is only acceptable with a named reason. Here are all six on this board. Only the
first one asks you to do something; the other five are the shipped configuration working
correctly:

| Series | State | Reason |
|---|---|---|
| the whole board | **NODATA** | The corpus is gitignored and generated. Run `scripts/gen-samples/gen-samples.sh` |
| `loh_bytes` | flat at ~0 | The raw-byte read path is LOH-clean by design: one buffer at 65,536 bytes, below the 85,000-byte threshold a `byte[]` reaches at 84,976. It moves with `fault.slurpWholeFile`, or with `fileScan.bufferBytes >= 84976`. **Note when it moves: at the NEXT GC, not in the sample that allocated the array.** `GCMemoryInfo` describes the last collection, so measured, a 100 MB `File.ReadAllBytes` left this gauge reporting the previous collection's value until a forced blocking gen2 collect |
| `gc_collected{gen="2"}` | **absent, not zero** | Core creates a series on first increment, and no gen2 collection happens in a shipped-config scan. Needs `fault.retainScannedRows` (promotes the retained rows) or `fault.slurpWholeFile` (one LOH object, and the LOH is collected with gen2). The panel carries no `or vector(0)` on purpose: a standalone `sum by (gen)` with one returns a series with NO `gen` label, a blank legend that joins nothing and reads as a real generation |
| `bytes_allocated` rate | near 0 | The default path allocates nothing per row. Needs `fault.decodeRowsToStrings`. **This is not a broken counter**, and the dominant contributor is not the sampler either: it is the per-batch heartbeat at 117 B, so a 4m47s scan reports about 415 KB in total, which is 1.4 KB/s against 348 KB/s of reading |
| `staleness` p50/p95 | **NODATA** | Recorded only when an attempt RESUMES from a checkpoint. A clean scan records it never, and the target carries no `or vector(0)`, so it reads NODATA until you run the `kill -9` recipe in [HEARTBEATING.md](HEARTBEATING.md). Same shape as the seed board's staleness target |
| `verified{result=...}` | empty until a scan finishes | Both values are absent until one completes inside the range. No `or vector(0)`: the fallback would print a confident **0** in the place where the idempotency verdict goes |

### Panels that need reading carefully

**Rows redone this range** uses `max_over_time` per attempt-series, **not** `increase()`,
and the difference is not cosmetic. `rows_read` is tagged with the attempt, so each
`(attempt, instance)` series is monotone and never resets within itself: its last sample IS
its total. `increase()` would extrapolate to both range edges *and* have to cross the gap
where a killed worker's target is down, which is to say it would approximate in exactly the
region this panel measures.

It is honest about its accuracy. Exact for an attempt that drains, cancels or fails, because
that attempt survives to the next scrape. Low **only** for `kill -9`, and then only by the
rows read since the last scrape: one 1s scrape at 6000 rows/s is 6,000 rows against a
144,000-row signal, so at most **~4.2% low**. That contrast is the punchline, because
`kill -9` loses the work AND the record of having done it.

**A negative reading means the scan is still in flight**, not that something is broken: the
ceiling is the whole corpus and the attempts have not reached it yet. Read it once
"Idempotency verdict" says `match`, and keep the range around ONE run, because two completed
scans in range sum two corpora of reads against one corpus of ceiling.

**Memory: managed heap, LOH, working set** uses `max()`, never `sum()`. These are properties
of a PROCESS, and two workers scrape into this board, so `sum()` would add two unrelated
heaps and report a number no process has. Last-writer-wins inside one process is not a
defect here and is why they carry no tags: eight concurrent scans read one heap and write the
same number, so the only artifact is a higher update rate.

**They nest, they do not partition.** The LOH is inside the managed heap, which is inside the
working set. That is why these are separate metric NAMES instead of one metric with a
`region` label. `sum by (region)` is this repo's reflex idiom, and over nested quantities it
would count the managed heap twice and the LOH three times, producing a total larger than
the process.

**The working set staying flat through a 500 MB scan is the proof, not a broken gauge.** The
read path streams one 64 KiB buffer and the file's bytes live in the kernel page cache,
never entering this process's address space. A reader who expects RSS to climb with bytes
read will conclude the gauge is wrong; the flat line is the cleanest evidence available that
the scan is really streaming.

**GC collections /s by generation** shows three lines that **nest rather than partition**.
`GC.CollectionCount(g)` counts collections of generation g or higher, so one gen2 collection
increments all three counters and `gen="0"` is always at least `gen="1"`, which is always at
least `gen="2"`. `sum by (gen)` groups rather than adds, so the three lines are each correct;
what is wrong is adding them together to get "total collections", which triple-counts every
gen2. They are published raw because raw is what `dotnet-counters` and every other .NET
exporter reports.

**GC pause time** is a LEVEL, not a rate. `GCMemoryInfo.PauseTimePercentage` is computed by
the runtime at the end of a collection over its own window, not over `$__rate_interval`, and
it does not move between collections. Measured: it read 0.73 immediately after a forced gen2
collect and still read 0.73 after 500 ms of idle. So on a scan that triggers no collection it
reports the WORKER'S STARTUP GCs forever. Only believe movement in it when the collections
panel is moving too.

### What this board changed on the Heartbeating board

The two activity-slot expressions on the Heartbeating board now pin
`task_queue="repro-task-queue"`. Their descriptions claimed this repo has exactly one
activity type and it always heartbeats, which `FetchWeather` already falsified and
`ScanFile` would have made worse: `temporal_worker_task_slots_used` carries `worker_type`
but no `activity_type`, so a second heartbeating activity type on the shared queue would
have corrupted that panel with nothing to filter it back out. That is a pre-existing defect
this case was the trigger to fix, and it is the reason `WorkflowFileScan` has a queue of
its own at all. **Activity slot saturation on repro-scan-queue** is the other half of the
same pin.
