# Things that will bite you

Ordered by how much of your afternoon each one costs. Several of these look exactly
like bugs. Read this before you conclude a panel is broken.

## A counter that has never incremented does not exist

Core registers a metric on its FIRST increment, so a counter that has not yet fired is
absent from `/metrics` entirely rather than reading `0`.
`temporal_sticky_cache_miss`, `temporal_sticky_cache_total_forced_eviction`,
`repro_activity_cancel` and every `*_failure` family are all missing from a healthy
worker.

That is why `or vector(0)` is load-bearing on this stack in a way it was not in the Go
original, where tally registered metrics immediately. PromQL arithmetic collapses
silently too: `sum(rate(hit)) + sum(rate(miss))` is EMPTY, not `hit`, when `miss` has
never fired. Guard every term you add, not just the outer expression.

The Go original's opposite gotcha, "fresh series read 0 for up to 1 second" because
tally flushed on a timer, does not apply. Core's exporter reads its registry at scrape
time. There is no flush window.

## Histograms are integer milliseconds, and counters carry no `_total`

With all three `PrometheusOptions` suffix flags left at their defaults you get
`temporal_request`, not `temporal_request_total`, and
`temporal_workflow_task_execution_latency_bucket` with `le="500"` meaning 500
**milliseconds**, where the same bucket in the Go SDK meant 500 **seconds**.

Every SDK latency panel here uses Grafana unit `ms`. Server panels keep `s`, because
the server is still a Go binary emitting tally metrics in seconds. Two panels plot both
on one axis and both multiply the server series by 1000: Bug Signals
"Schedule-to-start: SDK view vs server view" and Heartbeating "Activity
schedule-to-start p95, SDK vs server". Both also pin ONE `task_type`.
`task_schedule_to_start_latency` is a single server histogram covering
`task_type="Workflow"` and `task_type="Activity"`, so summing over it compares a
one-sided SDK series against a two-sided server series and calls the difference
divergence.

## "Fixing" the missing `_total` blanks the imported SDK board

The obvious reaction to bare counter names is to set `HasCounterTotalSuffix = true`.
Do not. `temporalio/dashboards`' `sdk/temporal-core-sdks-otel.json`, the board this
repo vendors, is written against exactly the default names, and flipping any one of the
three flags empties all of it at once.

The honest trade-off: integer milliseconds mean sub-millisecond durations round to
**zero**, and no bucket override recovers them. `UseSecondsForDuration = true` fixes
that and blanks the board. You get one or the other.

## Default histogram buckets produce a plausible constant, not "no data"

Core's default first bucket for `request_latency` is `le=50` while loopback gRPC is
0-5 ms, so every observation lands in one bucket and `histogram_quantile` interpolates
p95 to a flat ~47 ms forever. Same for schedule-to-start (flat ~99 ms) and for activity
execution latency, whose top default bucket is 60 s while the seed activity
deliberately runs longer.

`HistogramBuckets.cs` overrides eight metrics for this reason, six SDK ones plus the
two `repro_*` histograms. Two traps in that mechanism: Core matches override keys by
**substring** against the already-prefixed name and iterates them in nondeterministic
order, so keys must be disjoint and prefixed. And changing `MetricPrefix` breaks Core's
own default-bucket lookup, which strips a hard-coded `"temporal_"`.

## `TemporalRuntime` must be built once, first, and shared

`TemporalRuntime.Default` is materialized on first touch, and
`TemporalClient.ConnectAsync` touches it when `Runtime` is unset. A client bound to
`Default` never writes into a runtime you construct afterwards: the exporter on :8077
answers 200 with an empty registry, Prometheus reports the target UP, and every SDK
panel is blank, with no exception and no log line. `ReproRuntime` enforces
single-construction with an `Interlocked` guard for this reason.

## `BindAddress` has no default and must not be loopback

Core parses it with Rust's `SocketAddr::from_str`, which rejects Go's idiomatic
`":8077"`. The .NET layer only null-checks the string, so a malformed value surfaces as
an opaque native error.

And `127.0.0.1:8077` is unreachable from the Prometheus container over
`host.docker.internal` while `curl localhost:8077` on the host still succeeds, which
makes it a genuinely nasty debug. `BindAddress.Normalize` accepts both spellings and
rejects loopback at startup.

## Label VALUES are not sanitized. The server's are.

Core passes label values through verbatim, so the SDK reports
`task_queue="repro-task-queue"` while the Temporal server's tally metrics report
`taskqueue="repro_task_queue"`. One queue, two spellings, on two different label KEYS,
and you cannot join them.

The Go original had the opposite problem: tally sanitized values, so its README warned
that selectors must use the underscore form. That advice is now backwards.

## An activity that does not heartbeat can never be cancelled

The server only communicates cancellation in the **response** to a
`RecordActivityTaskHeartbeat` RPC. `fault.stopHeartbeating` demonstrates it. Details in
[HEARTBEATING.md](HEARTBEATING.md).

## There is no heartbeat metric in any Core SDK, and the RPC rate is not your call rate

Core throttles heartbeats, so the observed RPC rate is the throttle, not the rate at
which your code calls `Heartbeat()`. That is why this repo emits
`repro_heartbeat_sent` at the call site. Proxies and throttle math in
[HEARTBEATING.md](HEARTBEATING.md).

## `GracefulShutdownTimeout` defaults to `TimeSpan.Zero`, and `ExecuteAsync` waits for every activity

A long heartbeating activity gets no grace at all on Ctrl-C by default, and an activity
that swallows `OperationCanceledException` makes `ExecuteAsync` never return. The
timeout controls *when* `ctx.CancellationToken` fires, not how long the worker will
wait. `fault.ignoreCancellation` demonstrates it.

## The push path cannot use Core's exporter, and double-prefixes

`PrometheusOptions` and `CustomMetricMeter` are mutually exclusive and throw at runtime
construction, so a process cannot both scrape and push. The starter goes through
`DiagnosticSource` to `prometheus-net`, which **prepends the .NET `Meter` name to every
metric**, so a meter named `temporal` carrying Core's already prefixed
`temporal_request` arrives as `temporal_temporal_request`.

The obvious fix, `MetricPrefix = ""`, does not work: the empty string is treated as
unset and falls back to `"temporal_"`. The option itself works, and `"zz_"` really does
produce `temporal_zz_request`. Only `""` is unexpressible. The prefix is stripped at
scrape time by a `metric_relabel_configs` rule on the pushgateway job.

## prometheus-net renders every counter as a gauge

Deliberate on their side: a .NET `Meter` can be re-created at runtime and decrement.
`rate()` and `increase()` still compute correctly, only the `# TYPE` line is wrong.
Note prometheus-net 8.2.1 (2024-01-03) is the newest release and the project has been
dormant since. It works, but no fixes are coming.

## `MetricsOptions.GlobalTags` is silently dropped on the push path

Only the Prometheus and OpenTelemetry exporters honour it. The starter uses
`CollectorRegistry.SetStaticLabels` instead, and must not name a static label
`namespace`, `task_queue`, `workflow_type` or `activity_type`, because prometheus-net
filters out any meter tag colliding with a static label, silently erasing the real
dimension.

## Booleans in tag values are capitalized unless you stop them

.NET's `bool.ToString()` returns `"True"`, while Go's `fmt.Sprintf("%t")` returns
`"true"`, and every selector ported from the Go boards matches `retried="true"`. A
capitalized value does not error. The panel is just permanently empty.

## `temporal_request_attempt` exists in the TSDB but never for your worker

It is emitted by the Temporal server's own embedded **Go** SDK workers on :8000
(`service_name="worker"`), not by any Core-based SDK. Measured: present and growing on
:8000, and **exactly zero** occurrences on :8077. A panel built on it looks like it
should work and never populates for your code. That is why the Go original's "gRPC
attempts per logical call" panel was deleted rather than ported.

## There is no `heartbeat_timeout` server metric

Activity timeouts in server 1.31.2 are ONE counter, `activity_task_timeout`, split by a
`timeout_type` label with values `Heartbeat`, `StartToClose`, `ScheduleToStart`,
`ScheduleToClose`, all tagged `operation="TimerActiveTaskActivityTimeout"`. Guides that
name four separate metrics are wrong for this version. Measured:

```
activity_task_timeout{timeout_type="Heartbeat",activityType="ProcessBatch",
  operation="TimerActiveTaskActivityTimeout",service_name="history",
  taskqueue="stall_queue"} 1
```

Note `taskqueue="stall_queue"` in that sample: the server sanitized the hyphen out of
`stall-queue`, while the SDK on :8077 reports the same queue with its hyphen intact.
That is the label-value asymmetry above, caught in the wild.

## Core's `poller_type` spelling differs from Go's, and the docs document Go's

Core emits `sticky_workflow_task`, the Go SDK emits `workflow_sticky_task`, and
<https://docs.temporal.io/references/sdk-metrics> lists the Go spelling. The source is
authoritative. Verify against your own scrape before writing alerts.

## `service_name` is the only thing separating your worker from the server's

Core attaches `service_name="temporal-core-sdk"`, the server's embedded workers report
`service_name="worker"`. With Core defaults there is no `_total` suffix, so your
`temporal_workflow_completed` and the server's have **identical names**. In the Go
original tally's suffix kept them apart by accident. Every SDK selector on every
authored board pins `service_name`.

## The Pushgateway does NOT persist, and that is deliberate

The Go original ran it with `--persistence.file`, so a pushed group outlived
`docker compose down` and the gateway kept serving it. Prometheus scraped a fresh
sample every 5s for a starter that was not running.

Worse here specifically: a group pushed before a `HistogramBucketOverrides` change
keeps its old `le` layout, and `sum by (le, operation)` silently merges two
incompatible bucket sets. Persistence is not needed to see an old run, because
Prometheus already scraped those samples and keeps them for its own 7d retention.
Clear a group without restarting:
`dotnet run --project src/Repro.Starter -- --delete-push-group`.

## Namespace retention is 7d, and changing it does not affect an existing namespace

Retention caps how long a CLOSED workflow's history survives, and the capture-and-replay
recipe depends on `temporal workflow show` still finding it. At the Go original's `1d`,
a history captured on Friday is gone on Monday and replay fails with a not-found error
that reads like a replayer bug.

`create-namespace.sh` exits early when the namespace exists, so editing
`DEFAULT_NAMESPACE_RETENTION` and re-running `docker compose up` does nothing. On a
live stack: `temporal operator namespace update -n default --retention 7d`.

## The dynamicconfig mount is mandatory

`temporalio/server:1.31.x` ships no default dynamicconfig file, but its embedded config
template always sets `dynamicConfigClient.filepath`, and the file-based client
hard-fails at startup if the path cannot be stat'd. Remove the mount and the server
exits on boot.

## `NUM_HISTORY_SHARDS` is immutable

After the first schema init. Changing it requires `docker compose down -v`.

## `temporalio/auto-setup` is deprecated

It has no 1.31.x tag (newest 1.29.7). That is why schema init and namespace
registration are separate one-shot `admin-tools` containers rather than one all-in-one
image.

## `dotnet run` launches your app as a CHILD process

Killing the parent leaves the child running and holding :8077, and the next worker
start fails with `Address already in use (os error 48)`. For any kill test, run the
built binary directly and kill it by port: `kill -9 $(lsof -ti tcp:8077)`.

## And use `-9`, unless the drain is what you are testing

A plain SIGTERM starts a graceful shutdown, and the worker keeps :8077 for as long as
that takes. The SDK says so out loud. Measured on a SIGTERM with one activity in
flight:

```
Beginning activity worker shutdown, will wait 00:00:30 before cancelling 1 activity instance(s)
worker draining; checkpointing at step 3
```

That is `worker.gracefulShutdownTimeout` before `ctx.CancellationToken` even fires, and
then however long the activity takes to unwind. Restarting into that window is the
`Address already in use` above, arriving from the direction you were not watching.

## Grafana state is disposable, so a blank board is never a lost volume

The provisioner rewrites dashboards and the datasource from files on every boot, so
`docker volume rm temporal-dotnet-sandbox_grafana-data` loses nothing but UI-side edits.
If a board looks wrong, the file is the source of truth, not the volume.
