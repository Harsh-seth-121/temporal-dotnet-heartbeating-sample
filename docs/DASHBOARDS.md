# Dashboards

Grafana on <http://localhost:3000>, no login. Three folders, eight dashboards:

- `sandbox/` holds the boards written for this topology: 4 dashboards, 57 panels, 84
  targets. Every one of the 84 has been probed against a live stack, including the two
  `simple-activity` panels; see the measurement below.
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
python3 grafana/probe-dashboards.py             # the four authored boards
python3 grafana/probe-dashboards.py heartbeat   # one board, by file stem
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
