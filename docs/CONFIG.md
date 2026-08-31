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
