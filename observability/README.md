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
because the server is still a Go binary emitting tally metrics in seconds. The one
panel that plots both multiplies the server series by 1000.

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
the seed activity deliberately runs longer. `HistogramBuckets.cs` overrides seven
metrics for this reason. Two traps in that mechanism: Core matches override keys
by **substring** against the already-prefixed name and iterates them in
nondeterministic order, so keys must be disjoint and prefixed; and changing
`MetricPrefix` breaks Core's own default-bucket lookup, which strips a hard-coded
`"temporal_"`.

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
rate.** The proxies are `temporal_request{operation="RecordActivityTaskHeartbeat"}`
and the server's `heartbeat_timeout{operation="TimerActiveTaskActivityTimeout"}`.
But Core throttles heartbeats to `min(HeartbeatTimeout × 0.8,
MaxHeartbeatThrottleInterval)`, so the observed RPC rate is the throttle, not the
rate at which your code calls `Heartbeat()`. That is why this repo emits
`repro_heartbeat_sent` at the call site: the gap between the two lines on the
Heartbeating board *is* the throttle.

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
(`service_name="worker"`), not by any Core-based SDK. Measured: 10 occurrences on
:8000, zero on :8077. A panel built on it will look like it should work and never
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
directly or `kill $(lsof -ti tcp:8077)`.

## What each process contributes

| Process | Transport | Port | Emits |
|---|---|---|---|
| Temporal server (container) | scraped | 8000 | ~145 server metric families, plus its own Go SDK worker metrics |
| `Repro.Worker` (host) | scraped | 8077 | SDK metrics + `repro_*` custom metrics |
| `Repro.LoadGen` (host) | scraped | 8078 | same, under continuous traffic |
| `Repro.Starter` (host) | pushes on exit | 9091 | SDK **client** metrics only |
| `Repro.Replay` (host) | opt-in `--metrics` | 8079 | **nothing** — 200 with an empty body |

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

`grafana/probe-dashboards.py` runs every target on every authored board twice: as
the panel ships it, and again with `or vector(0)` stripped. A single check is
useless in both directions — an expression ending in `or vector(0)` always returns
a row, so it proves nothing about whether the metric exists.

```bash
python3 grafana/probe-dashboards.py            # all boards
python3 grafana/probe-dashboards.py heartbeat  # one board
```

States: `OK` (both modes return data), `FALLBACK` (renders via `or vector(0)`
because the series has not been created yet — expected for anything that only
appears when something goes wrong), `NODATA` (neither, and no fallback — either
the wrong stack state or a wrong metric name), `ERROR` (always a bug).

Measured against a live stack with the worker and loadgen running and
`failureRate: 0.15`:

```
68 OK  +  14 FALLBACK  =  82/82 targets render
0 NODATA, 0 ERROR
```

To move the `FALLBACK` targets into `OK`, put the stack in the state each needs:

| Targets | State needed |
|---|---|
| heartbeat timeouts, `timed_out` outcomes | `fault.stallPastHeartbeatTimeout: true` |
| cancellation reasons | `temporal workflow cancel -w repro-workflow`, or Ctrl-C the starter |
| sticky cache miss / forced eviction / replay pressure | `worker.maxCachedWorkflows: 1` |
| non-determinism (SDK and server) | break the workflow and replay, or edit it while a run is in flight |
| RPC and poll failures | stop the server mid-run, or throttle it |

## Known-empty official panels

The four dashboards under `temporal-server/` and `temporal-sdk/` are imported from
[temporalio/dashboards](https://github.com/temporalio/dashboards) at commit
`4994df2` as-is, so they cover more ground than this sandbox exercises. Measured
against a running stack with worker and loadgen and faults on:

| Dashboard | With data | Empty | Non-query |
|---|---|---|---|
| `temporal-sdk/core-sdks-otel` | 23 | 3 | 2 |
| `temporal-server/server-general` | 29 | 6 | 1 |
| `temporal-server/frontend-service` | 14 | 2 | 0 |
| `temporal-server/history-service` | 34 | 17 | 0 |

The SDK board scores far better than the Go original's equivalent did (19 of 34)
for one reason: it is written against exactly the metric names a Core-based SDK
emits, which is why all three `PrometheusOptions` suffix flags stay at their
defaults.

Most remaining empties are correct, not broken:

- Error panels are empty because nothing is failing. Raise `fault.failureRate`.
- Local activity panels. This sandbox uses no local activities.
- Timer and transfer task panels on the history board. The seed workflow schedules
  no timers. Add a `Workflow.DelayAsync` and they fill.
- `GC Counter` and other Go-runtime panels are empty permanently. The server's
  Prometheus registry is private, so no `go_*` / `process_*` collectors exist.
- `Shard Rebalancing` is empty permanently. Single-node deployment.
- Panels keyed on `kubernetes_pod_name` are empty permanently. No Kubernetes here.

The `$Service` and `$Client` variables on `server-general` are decorative; they
appear only in panel titles, never in any expression.

The authored boards in `grafana/dashboards/sandbox/` exist because of this gap.
Regenerate them with `python3 grafana/build-dashboards.py`, from any directory.
Grafana picks the change up within 10s.
