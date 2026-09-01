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
never fired. Guard every term you add, the outer expression included.

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

`HistogramBuckets.cs` overrides ten metrics for this reason, six SDK ones plus the
four `repro_*` histograms. Two traps in that mechanism: Core matches override keys by
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

## A local activity's `scheduleToCloseTimeout` does not bound a re-execution loop

The most expensive wrong assumption in this repo, because it is the one the API name
actively encourages and the one a plan will reach for as its safety net.

A local activity runs inside the workflow task. If it outlives
`history.workflowTaskHeartbeatTimeout` the server times the task out and reschedules it, and
because a local activity's result is not written to history until it completes, the whole
thing **runs again from the beginning**. Schedule-to-close does not accumulate across those
re-dispatches. Its clock restarts every time.

The chain, in sdk-core: `original_schedule_time` is re-stamped on every fresh schedule with
`get_or_insert(SystemTime::now())`, and persisted only inside the **marker**, guarded by
`if record_marker`. A local activity killed by a workflow task timeout never resolved, so
there is no marker and nothing was persisted. Eviction then sends `InvalidateRun` and
`Drop for TimeoutBag` aborts the schedule-to-close handle. The field that carries a previous
clock forward is used only by `DoBackoff`, i.e. timer-based retry backoff — a different path,
and the only one sdk-core's own tests cover.

`startToCloseTimeout` does not save you either, and `retry.maximumAttempts` is not even in
the conversation: a re-execution is a **fresh execution, attempt 1 again**, outside the retry
policy. Measured: `ActivityInfo.Attempt` reads 1 on every re-execution.

The only thing that ends such a run is **`WorkflowOptions.RunTimeout`**, enforced by the
server's timer queue with no worker involvement. Set it, or the run is bounded by nothing.

The documented fix is the other direction: put `scheduleToCloseTimeout` **below** the
heartbeat timeout, and the activity fails with a timeout the workflow can catch before the
task is ever re-executed.

## A run killed by `RunTimeout` records no outcome, because workflow code never runs again

The server closes a run-timed-out workflow by calling `TimeoutWorkflow` directly, **without
scheduling a workflow task**. So a `catch` around your activity await does not run, and any
counter you increment there does not increment.

For `WorkflowLocalActivity` that is two-thirds of runs at the shipped config, and it is why
`repro_local_activity_completed` is documented as not accounting for every run and why
`repro_pi_attempt_started` — emitted from *activity* code, which does not replay — is that
case's primary signal. Combined with the "a counter that has never incremented does not
exist" entry above, a panel built only on the workflow-side counter reads a confident flat
line for the exact behaviour you are trying to watch.

## Heartbeating has no effect on a local activity

Not "is discouraged". `LocalActivityOptions` has no `HeartbeatTimeout` at all — the property
is absent from the type, not left unset — and the SDK README says the rest out loud:
"Heartbeating has no effect on local activities." Everything in
[HEARTBEATING.md](HEARTBEATING.md) — the throttle, the checkpoint details, the `kill -9`
resume test — is inapplicable. A worker killed mid-local-activity loses all of it and the
next attempt starts from zero.

## But the local activity IS told when its workflow task times out

Which nothing in the documentation predicted, so it is measured here rather than cited.

Across 17 cut-short burns in one demo run, **every one ended between 64.0s and 64.2s**
against a 1m `history.workflowTaskHeartbeatTimeout` — never at its requested duration and
never at a drain. `ActivityExecutionContext.Current.CancellationToken` fires roughly four
seconds after the server's timeout.

It changes no outcome, since the workflow task is already gone and the result is discarded.
It changes the CPU arithmetic: a doomed run burns ~64s per execution rather than its full
drawn length.

Do not fold that token together with `WorkerShutdownToken`. They are the same shape and
completely different events, and their signatures differ in a way worth knowing:

- a **drain** cuts every in-flight burn at the same **wall-clock** instant with unrelated
  elapsed values (measured: 49848ms, 2887ms, 30422ms, all at `10:25:30`)
- a **workflow task timeout** cuts each burn at the same **elapsed** value at unrelated
  wall-clock times (measured: 64076, 64079, 64080, 64097ms)

The first version of `PiActivities` checked them with a single `||` and logged "worker drain
cut the burn short" seventeen times during a demo in which nothing had drained.

## An unset `RetryPolicy` on a local activity means retry FOREVER

Stronger than the regular activity path, where you would usually have set one anyway.
`LocalActivityOptions.RetryPolicy` is documented as "If unset, defaults to retrying forever",
and `Temporalio.Common.RetryPolicy.MaximumAttempts` of `0` also means unlimited. Both routes
to "no policy" give you an unbounded chain. Write `1` for "do not retry".

## `history.workflowTaskHeartbeatTimeout` is namespace-scoped and nothing finer

It is declared in `temporalio/temporal` as `NewNamespaceDurationSetting`, so it filters by
namespace — not by task queue, not by workflow type. Server default is **30m**
(`common/dynamicconfig/constants.go`, v1.31.2).

That is the entire reason `WorkflowLocalActivity` has a namespace of its own. Lowering the
setting namespace-wide would have been worse than useless here: the other three workflows use
no local activities, so they would never visibly react, and the override would sit in the
config file looking scoped when it was not.

Temporal's own integration suite overrides it to 5s, so lowering it is supported rather than
a hack.

## `.editorconfig` does NOT scope anything to `.workflow.cs`

The `.workflow.cs` suffix is a naming convention and nothing more. `CA1848`, `CA1873` and
`CA1822` are suppressed **repo-wide** under `[*.cs]`. `CA1822` in particular is what lets a
`[WorkflowRun]` method and an instance `[Activity]` method stay non-static, and the
non-static local-activity lambda overload requires it.

`CA2007` (ConfigureAwait) is **not enabled**, so `.ConfigureAwait(false)` in workflow code
compiles clean and silently drops the continuation off the SDK's deterministic scheduler. The
convention is hand-enforced: `(true)` in workflow files, `(false)` elsewhere in `Repro.Core`.
`CA5394` is not enabled either, which is why the seeded `System.Random` in `PiActivities` is
allowed to stay seeded.

## A workflow cannot cancel ITSELF into `CANCELED` status

The server records that status only when a cancellation **request** exists. From inside
workflow code there is no way to create one:

- Throwing `CanceledFailureException` with no request outstanding records **`FAILED`**.
- Swallowing a real cancellation and returning records **`COMPLETED`**.
- `Workflow.GetExternalWorkflowHandle` pointed at your own workflow ID cannot signal
  you: the server refuses a signal-to-self outright, because the command handler holds a
  lock on the source execution while writing to the target and self-targeting deadlocks
  it ([temporalio/temporal#682](https://github.com/temporalio/temporal/issues/682)).

So a "stop me" signal and a cancellation are different things and end differently.
`SimpleNoActivity` ships both on purpose: the `Stop` signal ends the run `COMPLETED`
with `EndedBy="stopped"`, and a real `CANCELED` comes only from a client calling
`handle.CancelAsync()`. The workflow sees that because
`Workflow.WaitConditionAsync`'s `cancellationToken` argument **defaults to
`Workflow.CancellationToken`** when you leave it unset, so the cancel raises straight
out of the wait, and the `catch` rethrows, because swallowing it would report
`COMPLETED` for a run somebody explicitly cancelled.

## `IsCanceledException` does NOT recognise a cancelled workflow at the client

`TemporalException.IsCanceledException` is the right test **inside** workflow or
activity code, where a cancel arrives as `OperationCanceledException` or
`CanceledFailureException` or nested inside an `ActivityFailureException`. Reach for it
at a **client** call site and it quietly gives you the wrong answer.

MEASURED, on a workflow whose server status really is `CANCELED`:

```
handle.GetResultAsync()
  -> WorkflowFailedException
       InnerException: CanceledFailureException
     TemporalException.IsCanceledException(e) == False
```

The helper covers .NET cancellation plus a cancellation nested in an **activity** or
**child workflow** failure. A client-side workflow-failed wrapper is neither. The only
symptom is a counter: every deliberately cancelled run lands in your failure bucket and
nothing logs an error. Match the shape instead:

```csharp
catch (WorkflowFailedException e) when (e.InnerException is CanceledFailureException)
```

Matching the shape rather than catching broadly also keeps **shutdown** out of that
bucket. When your own token cancels `GetResultAsync` you get an
`OperationCanceledException`, which is not a cancelled workflow.

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

## The Pushgateway does NOT persist, deliberately

The Go original ran it with `--persistence.file`, so a pushed group outlived
`docker compose down` and the gateway kept serving it. Prometheus scraped a fresh
sample every 5s for a starter that was not running.

Worse here specifically: a group pushed before a `HistogramBucketOverrides` change
keeps its old `le` layout, and `sum by (le, operation)` silently merges two
incompatible bucket sets. Persistence is not needed to see an old run, because
Prometheus already scraped those samples and keeps them for its own 2d retention
(`--storage.tsdb.retention.time=2d` in observability/compose.yml; the 7d in the
namespace-retention section below is a different, unrelated setting).
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

## `async` message handlers do not compile in this repo

Every Temporal doc sample writes signal and update handlers as `async`:

```csharp
[WorkflowSignal]
public async Task StopAsync() => stopRequested = true;   // CS1998 -> build FAILS here
```

With no `await` in the body that is **CS1998**, "async method lacks 'await' operators",
and `Directory.Build.props` sets `TreatWarningsAsErrors`. So the sample you copied does
not build, and the fix is not a pragma.

The SDK validates only the handler's **return type**, not whether it is `async`. A plain
method is fully supported and is what `SimpleNoActivity` uses:

```csharp
[WorkflowSignal]
public Task StopAsync() { stopRequested = true; return Task.CompletedTask; }

[WorkflowUpdate]
public Task<int> AddAsync(AddInput input) => Task.FromResult(input.A + input.B);
```

Queries are different again: a `[WorkflowQuery]` must be non-`async` and must **not**
return a `Task`, and its wire name is **not** trimmed of an `Async` suffix the way
signal and update names are. Naming one `GetStatusAsync` gives you a query literally
called `GetStatusAsync`.

## `dotnet run` launches your app as a CHILD process

Killing the parent leaves the child running and holding :8077, and the next worker
start fails with `Address already in use (os error 48)`. For any kill test, run the
built binary directly and kill it by port: `kill -9 $(lsof -ti tcp:8077)`.

This is why `scripts/demo-up.sh` launches the built binaries and never `dotnet run`:
the pid in the pid file has to be the pid holding the port. `demo-down.sh` also sweeps
both ports afterwards, so it cleans up orphans left by the manual path.

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

`scripts/demo-down.sh` derives its SIGTERM-to-SIGKILL budget from that field plus 15s
of unwind, and does not return until both ports are actually free. `--force` skips the
drain when it is not what you are testing.

## Grafana state is disposable, so a blank board is never a lost volume

The provisioner rewrites dashboards and the datasource from files on every boot, so
`docker volume rm temporal-dotnet-sandbox_grafana-data` loses nothing but UI-side edits.
If a board looks wrong, the file is the source of truth, not the volume.
