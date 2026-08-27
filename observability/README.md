# observability/

Everything the metrics stack needs, in one directory. Nothing here is installed
globally. You need Docker Desktop, plus python3 if you regenerate or probe the
dashboards.

```
compose.yml                     eight services + volumes + network
.env                            image pins and Postgres credentials
dynamicconfig/                  MANDATORY server config (see below)
scripts/                        idempotent schema init + namespace creation
prometheus/prometheus.yml       six scrape jobs
grafana/provisioning/           datasource + dashboard provider
grafana/dashboards/             dashboard JSON, one Grafana folder per directory
grafana/build-dashboards.py     generator for the four authored boards
grafana/probe-dashboards.py     proves every panel returns data
```

## Bring up / tear down

```bash
docker compose up -d
docker compose down       # keep all data
docker compose down -v    # full reset (REQUIRED if you change NUM_HISTORY_SHARDS)
```

The same three commands work unchanged from the repo root. `../compose.yml`
`include`s this file and pins the project name to `temporal-dotnet-sandbox`, so
both directories drive one stack.

That project name is deliberately **not** `temporal-sandbox`, which is what the Go
project this repo was ported from uses. Sharing it means `docker compose up` here
silently adopts that stack's containers and volumes, and `down -v` from either
repo wipes the other's data. The `container_name` and network `name` values are
prefixed for the same reason: those are global to the Docker daemon, not scoped to
the compose project.

| URL | What |
|---|---|
| <http://localhost:8080> | Temporal Web UI |
| <http://localhost:3000> | Grafana (anonymous Admin, no login) |
| <http://localhost:9090/targets> | Prometheus target health |
| <http://localhost:9091> | Pushgateway |
| <http://localhost:8000/metrics> | Temporal server metrics, not SDK |

## Things that will bite you

Ordered by how much of your afternoon each one costs.

**A counter that has never incremented does not exist.** Core registers a metric
on its FIRST increment, so a counter that has not yet fired is absent from
`/metrics` entirely rather than reading `0`. `temporal_sticky_cache_miss`,
`temporal_sticky_cache_total_forced_eviction`, `repro_activity_cancel` and every
`*_failure` family are all missing from a healthy worker. This is why `or
vector(0)` is load-bearing on this stack in a way it was not in the Go original,
where tally registered metrics immediately. It also means PromQL arithmetic
silently collapses: `sum(rate(hit)) + sum(rate(miss))` is EMPTY, not `hit`, when
`miss` has never fired. Guard every term you add, not just the outer expression.
(The Go original's opposite gotcha — "fresh series read 0 for up to 1 second"
because tally flushed on a timer — does **not** apply here. Core's exporter reads
its registry at scrape time; there is no flush window.)

**Histograms are integer MILLISECONDS, and counters carry no `_total`.** With all
three `PrometheusOptions` suffix flags left at their defaults you get
`temporal_request`, not `temporal_request_total`, and
`temporal_workflow_task_execution_latency_bucket` with `le="500"` meaning 500
**milliseconds** — where the same bucket in the Go SDK meant 500 **seconds**.
Every SDK latency panel here uses Grafana unit `ms`. Server panels keep `s`,
because the server is still a Go binary emitting tally metrics in seconds. TWO panels
plot both on one axis and both multiply the server series by 1000 — Bug Signals
"Schedule-to-start: SDK view vs server view" and Heartbeating "Activity
schedule-to-start p95, SDK vs server". Both also pin ONE `task_type`:
`task_schedule_to_start_latency` is a single server histogram covering
`task_type="Workflow"` and `task_type="Activity"`, so summing over it compares a
one-sided SDK series against a two-sided server series and calls the difference
divergence.

**"Fixing" the missing `_total` blanks the imported SDK board.** The obvious
reaction to bare counter names is to set `HasCounterTotalSuffix = true`. Do not.
`temporalio/dashboards`' `sdk/temporal-core-sdks-otel.json` — the board this repo
vendors — is written against exactly the default names, and flipping any one of
the three flags empties all of it at once. The honest trade-off: integer
milliseconds mean sub-millisecond durations round to **zero**, and no bucket
override recovers them. `UseSecondsForDuration = true` fixes that and blanks the
board. You get one or the other.

**Default histogram buckets produce a plausible constant, not "no data".** Core's
default first bucket for `request_latency` is `le=50` while loopback gRPC is
0-5 ms, so every observation lands in one bucket and `histogram_quantile`
interpolates p95 to a flat ~47 ms forever. Same for schedule-to-start (flat
~99 ms) and for activity execution latency, whose top default bucket is 60 s while
the seed activity deliberately runs longer. `HistogramBuckets.cs` overrides eight
metrics for this reason — six SDK ones plus the two `repro_*` histograms. Two traps
in that mechanism: Core matches override keys by **substring** against the
already-prefixed name and iterates them in nondeterministic order, so keys must be
disjoint and prefixed; and changing `MetricPrefix` breaks Core's own default-bucket
lookup, which strips a hard-coded `"temporal_"`.

**`TemporalRuntime` must be built once, first, and shared.**
`TemporalRuntime.Default` is materialized on first touch, and
`TemporalClient.ConnectAsync` touches it when `Runtime` is unset. A client bound to
`Default` never writes into a runtime you construct afterwards: the exporter on
:8077 answers 200 with an empty registry, Prometheus reports the target UP, and
every SDK panel is blank — with no exception and no log line. `ReproRuntime`
enforces single-construction with an `Interlocked` guard for this reason.

**`BindAddress` has no default and must not be loopback.** Core parses it with
Rust's `SocketAddr::from_str`, which rejects Go's idiomatic `":8077"`; the .NET
layer only null-checks the string, so a malformed value surfaces as an opaque
native error. And `127.0.0.1:8077` is unreachable from the Prometheus container
over `host.docker.internal` while `curl localhost:8077` on the host still
succeeds, which makes it a genuinely nasty debug. `BindAddress.Normalize` accepts
both spellings and rejects loopback at startup.

**Label VALUES are not sanitized. The server's are.** Core passes label values
through verbatim, so the SDK reports `task_queue="repro-task-queue"` while the
Temporal server's tally metrics report `taskqueue="repro_task_queue"` — one queue,
two spellings, on two different label KEYS, and you cannot join them. The Go
original had the opposite problem: tally sanitized values, so its README warned
that selectors must use the underscore form. That advice is now backwards.

**An activity that does not heartbeat can never be cancelled.** The server only
communicates cancellation in the **response** to a `RecordActivityTaskHeartbeat`
RPC. No `HeartbeatTimeout` plus no `Heartbeat()` calls means nothing except worker
shutdown can ever stop the activity. `fault.stopHeartbeating` demonstrates it.

**There is no heartbeat metric in any Core SDK, and the RPC rate is not your call
rate.** The two proxies are `temporal_request{operation="RecordActivityTaskHeartbeat"}`
on the SDK side and, on the server side,
`activity_task_timeout{timeout_type="Heartbeat"}` — *not* a `heartbeat_timeout`
metric, which does not exist in server 1.31.2; see the entry further down. But Core
throttles heartbeats to `min(HeartbeatTimeout × 0.8, MaxHeartbeatThrottleInterval)`,
so the observed RPC rate is the throttle, not the rate at which your code calls
`Heartbeat()`. That is why this repo emits `repro_heartbeat_sent` at the call site,
and `repro_heartbeat_call_interval_ms` next to it: the gap between the two lines on
the Heartbeating board *is* the throttle.

**`GracefulShutdownTimeout` defaults to `TimeSpan.Zero`, and `ExecuteAsync` waits
for every activity.** A long heartbeating activity therefore gets no grace at all
on Ctrl-C by default, and an activity that swallows `OperationCanceledException`
makes `ExecuteAsync` never return — the timeout controls *when*
`ctx.CancellationToken` fires, not how long the worker will wait.
`fault.ignoreCancellation` demonstrates it.

**The push path cannot use Core's exporter, and double-prefixes.**
`PrometheusOptions` and `CustomMetricMeter` are mutually exclusive and throw at
runtime construction, so a process cannot both scrape and push. The starter goes
through `DiagnosticSource → prometheus-net`, which **prepends the .NET `Meter`
name to every metric** — so a meter named `temporal` carrying Core's already
prefixed `temporal_request` arrives as `temporal_temporal_request`. The obvious
fix, `MetricPrefix = ""`, does **not** work: the empty string is treated as unset
and falls back to `"temporal_"`. (The option itself works — `"zz_"` really does
produce `temporal_zz_request`. Only `""` is unexpressible.) It is stripped at
scrape time by a `metric_relabel_configs` rule on the pushgateway job.

**prometheus-net renders every counter as a gauge.** Deliberate on their side: a
.NET `Meter` can be re-created at runtime and decrement. `rate()` and `increase()`
still compute correctly; only the `# TYPE` line is wrong. Also note prometheus-net
8.2.1 (2024-01-03) is the newest release and the project has been dormant since —
it works, but no fixes are coming.

**`MetricsOptions.GlobalTags` is silently dropped on the push path.** Only the
Prometheus and OpenTelemetry exporters honour it. The starter uses
`CollectorRegistry.SetStaticLabels` instead — and must not name a static label
`namespace`, `task_queue`, `workflow_type` or `activity_type`, because
prometheus-net filters out any meter tag colliding with a static label, silently
erasing the real dimension.

**Booleans in tag values are capitalized unless you stop them.** .NET's
`bool.ToString()` returns `"True"`, while Go's `fmt.Sprintf("%t")` returns
`"true"` — and every selector ported from the Go boards matches `retried="true"`.
A capitalized value does not error; the panel is just permanently empty.

**`temporal_request_attempt` exists in the TSDB but never for your worker.** It is
emitted by the Temporal server's own embedded **Go** SDK workers on :8000
(`service_name="worker"`), not by any Core-based SDK. Measured: present and growing
on :8000, and **exactly zero** occurrences on :8077. A panel built on it will look like it should work and never
populate for your code. That is why the Go original's "gRPC attempts per logical
call" panel was deleted rather than ported.

**There is no `heartbeat_timeout` server metric.** Activity timeouts in server
1.31.2 are ONE counter, `activity_task_timeout`, split by a `timeout_type` label
with values `Heartbeat`, `StartToClose`, `ScheduleToStart`, `ScheduleToClose` —
all tagged `operation="TimerActiveTaskActivityTimeout"`. Guides that name four
separate metrics are wrong for this version. Measured:

```
activity_task_timeout{timeout_type="Heartbeat",activityType="ProcessBatch",
  operation="TimerActiveTaskActivityTimeout",service_name="history",
  taskqueue="stall_queue"} 1
```

Note `taskqueue="stall_queue"` in that sample: the server sanitized the hyphen out
of `stall-queue`, while the SDK on :8077 reports the same queue with its hyphen
intact. That is the label-value asymmetry above, caught in the wild.

**Core's `poller_type` spelling differs from Go's, and the docs document Go's.**
Core emits `sticky_workflow_task`; the Go SDK emits `workflow_sticky_task`;
<https://docs.temporal.io/references/sdk-metrics> lists the Go spelling. The
source is authoritative — verify against your own scrape before writing alerts.

**`service_name` is the only thing separating your worker from the server's.**
Core attaches `service_name="temporal-core-sdk"`; the server's embedded workers
report `service_name="worker"`. With Core defaults there is no `_total` suffix, so
your `temporal_workflow_completed` and the server's have **identical names**. In
the Go original tally's suffix kept them apart by accident. Every SDK selector on
every authored board pins `service_name`.

**The dynamicconfig mount is mandatory.** `temporalio/server:1.31.x` ships no
default dynamicconfig file, but its embedded config template always sets
`dynamicConfigClient.filepath`, and the file-based client hard-fails at startup if
the path cannot be stat'd. Remove the mount and the server exits on boot.

**`NUM_HISTORY_SHARDS` is immutable** after the first schema init. Changing it
requires `docker compose down -v`.

**`temporalio/auto-setup` is deprecated** and has no 1.31.x tag (newest 1.29.7).
That is why schema init and namespace registration are separate one-shot
`admin-tools` containers rather than one all-in-one image.

**`dotnet run` launches your app as a CHILD process.** Killing the parent leaves
the child running and holding :8077, and the next worker start fails with
`Address already in use (os error 48)`. For any kill test, run the built binary
directly and kill it by port: `kill -9 $(lsof -ti tcp:8077)`.

**And use `-9`, unless the drain is what you are testing.** A plain SIGTERM starts a
graceful shutdown, and the worker keeps :8077 for as long as that takes. The SDK
says so out loud — measured on a SIGTERM with one activity in flight:

```
Beginning activity worker shutdown, will wait 00:00:30 before cancelling 1 activity instance(s)
worker draining; checkpointing at step 3
```

That is `worker.gracefulShutdownTimeout` before `ctx.CancellationToken` even fires,
and then however long the activity takes to unwind. Restarting into that window is
the `Address already in use` above, arriving from the direction you were not
watching.

## What each process contributes

| Process | Transport | Port | Emits |
|---|---|---|---|
| Temporal server (container) | scraped | 8000 | 233 metric families, 27 of them `temporal_*` from its own embedded Go SDK workers |
| `Repro.Worker` (host) | scraped | 8077 | SDK metrics + `repro_*` custom metrics |
| `Repro.LoadGen` (host) | scraped | 8078 | same, under continuous traffic |
| `Repro.Starter` (host) | pushes on exit | 9091 | SDK **client** metrics only |
| `Repro.Replay` (host) | opt-in `--metrics` | 8079 | **nothing** — 200 with an empty body |

`--metrics <addr>` overrides the configured port on all three of worker, loadgen and
replay, and `--metrics off` starts no exporter and binds no port at all — that is how
you run a SECOND worker on this host without fighting the first one for :8077. `off`
is a FLAG value only: `metrics.listenAddress: off` in config.yaml still fails
validation, and the error says so. Note it means no *exporter*, not no *runtime* —
the process still adopts a telemetry-free `TemporalRuntime`, because a client that
connects without one binds to `TemporalRuntime.Default` and that is the silent
metrics loss above.

`Repro.Starter` pushes client metrics only. `repro_workflow_*` and
`repro_activity_*` come from the worker, because workflow and activity code does
not execute in the starter.

**The replayer cannot appear on a dashboard, and .NET hides that better than Go
does.** Go's replayer hard-codes a no-op metrics handler, so the attempt fails
loudly. .NET's `WorkflowReplayerOptions` accepts a real `TemporalRuntime`, Core
starts a real HTTP listener, and `/metrics` answers **200 with a zero-byte body** —
so a Prometheus job pointed at it reports the target UP while every panel stays
empty. Verify with `--metrics` rather than trusting either README:

```
curl -sS -o /dev/null -w '%{http_code} %{size_download}\n' localhost:8079/metrics
200 0
```

## Proving the boards

`grafana/probe-dashboards.py` runs every target on every board it is pointed at
twice: as the panel ships it, and again with `or vector(0)` stripped. A single check is
useless in both directions — an expression ending in `or vector(0)` always returns
a row, so it proves nothing about whether the metric exists.

```bash
python3 grafana/probe-dashboards.py             # the four authored boards
python3 grafana/probe-dashboards.py heartbeat   # one board, by file stem
python3 grafana/probe-dashboards.py --vendored  # the four imported boards
python3 grafana/probe-dashboards.py --all       # both
```

Only the authored boards gate the exit code. The vendored ones are pinned at
`dashboards/UPSTREAM_SHA` and are not ours to fix; upstream writes them against a
fully loaded cluster, so plenty of their panels are legitimately empty here. Probing
them is still worth doing: it is how "leave all three `PrometheusOptions` suffix
flags false, because that is what the vendored Core SDK board is written against"
stops being an assertion and becomes a measurement.

States: `OK` (both modes return data), `FALLBACK` (renders via `or vector(0)`
because the series has not been created yet — expected for anything that only
appears when something goes wrong), `NODATA` (neither, and no fallback — either
the wrong stack state or a wrong metric name), `ERROR` (always a bug).

Measured against a live stack with the worker and loadgen running and
`failureRate: 0.15`:

```
authored: 70 OK  +  12 FALLBACK  =  82/82 targets render
authored: 0 NODATA, 0 ERROR
authored: 55/55 panels have at least one series
```

The OK/FALLBACK split moves with stack state; `82/82`, `0 NODATA, 0 ERROR` and
`55/55` are the parts that must not.

To move the `FALLBACK` targets into `OK`, put the stack in the state each needs:

| Targets | State needed |
|---|---|
| heartbeat timeouts | `fault.stallPastHeartbeatTimeout: true` |
| `timed_out` outcomes | `fault.stopHeartbeating: true` — **not** `stallPastHeartbeatTimeout`, which only stalls attempt 1, so attempt 2 completes and the outcome stays `completed` |
| cancellation reasons | `temporal workflow cancel -w repro-workflow`, or Ctrl-C the starter |
| sticky cache miss / forced eviction / replay pressure | `worker.maxCachedWorkflows: 1` |
| non-determinism (SDK and server) | break the workflow and replay, or edit it while a run is in flight |
| RPC and poll failures | stop the server mid-run, or throttle it |

## Known-empty official panels

The four dashboards under `temporal-server/` and `temporal-sdk/` are imported from
[temporalio/dashboards](https://github.com/temporalio/dashboards) as-is, pinned at
the SHA in `grafana/dashboards/UPSTREAM_SHA` (`4994df2`), so they cover more ground
than this sandbox exercises. These are panel counts, from
`probe-dashboards.py --vendored` against a running stack with worker and loadgen and
faults on — reproduce them rather than trust them, they move with stack state:

| Dashboard | With data | Empty | Non-query |
|---|---|---|---|
| `temporal-sdk/core-sdks-otel` | 24 | 2 | 2 |
| `temporal-server/server-general` | 31 | 4 | 1 |
| `temporal-server/frontend-service` | 14 | 1 | 1 |
| `temporal-server/history-service` | 33 | 17 | 1 |

102 of 126 panels, 124 of 182 targets, and **0 ERROR** — no imported expression is
rejected by this Prometheus. "Non-query" is a text panel or a panel whose only
target ships a blank `expr` (the probe skips both); Grafana `row` headers are not
counted at all.

The SDK board scores far better than the Go original's equivalent did (19 of 34)
for one reason: it is written against exactly the metric names a Core-based SDK
emits, which is why all three `PrometheusOptions` suffix flags stay at their
defaults. That premise is checked panel by panel, not asserted: its only two empty
panels are `temporal_workflow_task_execution_failed` and `temporal_sticky_cache_miss`,
both **absent counters** on a worker where nothing has failed and nothing has missed
the sticky cache — not name-shape mismatches. Every bare-name, millisecond-histogram
selector on that board resolves.

The empties are correct, not broken, and they fall into four groups. Each claim
below was checked against `curl localhost:8000/metrics`, not inferred from the panel
title:

- **A family that has never been emitted here.** Most of the empty targets are
  server-side error counters: `service_errors_entity_not_found`,
  `service_errors_resource_exhausted`, `persistence_errors`, `cache_errors`,
  `acquire_lock_failed`, `task_errors_discarded`, `stale_mutable_state`,
  `failed_workflow_tasks`, `multiple_completion_commands` — no `# TYPE` line at all,
  so absent rather than zero, exactly like Core. Raising `fault.failureRate` will
  **not** fill them: an activity failure is a normal completion path as far as the
  server is concerned. Stop the server mid-run or throttle persistence instead.
- **A family that exists, under label values the panel does not select.** Bare
  `service_errors` is here but only with `service_name="matching"`, while the panels
  select `frontend` and `history`. `cache_requests` and `cache_latency` are here
  under `HistoryCacheGetOrCreate`, `HistoryCacheGetOrCreateCurrent`,
  `EventsCacheGetEvent` and `EventsCachePutEvent`, while "Mutable State Load Counts"
  hard-codes `HistoryCacheGetCurrentExecution`. `task_attempt` is here under
  `TransferActiveTaskActivity` and `TimerActiveTaskActivityTimeout`, while the two
  "Task Attempt Stats" panels hard-code `TransferActiveTaskCommand` and
  `TimerActiveTaskCommand`. Upstream is not wrong; it is written against a different
  server build than the one pinned in `.env`.
- **A grouping label that does not exist on this endpoint.** `GC Counter` is the
  instructive one: `memory_num_gc` really is exported and really has data, tagged
  `service_name` — the panel groups by `kubernetes_pod_name`, of which there are
  **zero** occurrences in the whole scrape. Every `kubernetes_pod_name` panel is
  empty for that reason and no amount of load will change it. (Separately, there
  genuinely are no `go_*` / `process_*` series: the server's Prometheus registry is
  private.)
- **Permanently empty because of this topology.** `Shard Rebalancing`, `Shards
  Closed` and `State Transition` all read `shard_closed_count` /
  `sharditem_removed_count`, which a single-node deployment never emits.

Three empties are one command away from filling: `Workflow Completion Overview` and
`Workflow Timedout / Cancelled By Namespace` read `workflow_cancel` and
`workflow_timeout` at `operation="CompletionStats"`, so `temporal workflow cancel -w
repro-workflow` or `fault.stopHeartbeating: true` fills them.

The `$Service` and `$Client` variables on `server-general` are decorative; they
appear only in panel titles, never in any expression.

The authored boards in `grafana/dashboards/sandbox/` exist because of this gap.
Regenerate them with `python3 grafana/build-dashboards.py`, from any directory.
Grafana picks the change up within 10s.
