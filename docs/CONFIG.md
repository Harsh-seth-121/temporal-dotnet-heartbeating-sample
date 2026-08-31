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
| `activity.heartbeatTimeout` | `5s` | required for cancellation; drives the throttle. **Validated, not applied**, see below |
| `activity.startToCloseTimeout` | `10m` | per attempt. **Validated, not applied** |
| `activity.scheduleToCloseTimeout` | `1h` | all attempts. **Read by nothing today** |
| `activity.retry.*` | `1s` / `2.0` / `10s` / `5` | initial, coefficient, max interval, max attempts. **Read by nothing today** |
| `worker.gracefulShutdownTimeout` | `30s` | SDK default is `0s`; see the fault table in [HEARTBEATING.md](HEARTBEATING.md). `demo-down.sh` reads this field and drains for it plus 15s before SIGKILL |
| `worker.maxHeartbeatThrottleInterval` | `60s` | upper bound on the throttle |
| `worker.defaultHeartbeatThrottleInterval` | `30s` | used when the timeout is unset |
| `worker.maxCachedWorkflows` | `0` (SDK default 10000) | set to `1` to force evictions and replay storms |
| `worker.maxConcurrentActivities` / `maxConcurrentWorkflowTasks` | `0` (SDK default 100) | slot counts; `0` leaves the SDK default. Applied by the worker **and** the loadgen. The loadgen used to drop both, so :8078 ran at 100/100 whatever the file said |
| `loadgen.rate` / `concurrency` / `steps` | `5s` / `8` / `20` | traffic shape |
| `fault.failureRate` | `0`, shipped as `0.15` | fraction of activity attempts that fail, one roll per attempt, so P(workflow fails) is this to the fifth |
| `fault.latency` | `0`, shipped as `150ms` | latency added per step |
| `fault.stallPastHeartbeatTimeout` | `false` | overrun the heartbeat timeout on attempt 1 |
| `fault.stopHeartbeating` | `false` | keep working, stop heartbeating |
| `fault.ignoreCancellation` | `false` | swallow cancellation and wedge shutdown |

## The four `activity.*` rows are not yet the source of truth

They are the one place this file lies. The workflow builds its `ActivityOptions` from
`JobInput.Activity`, on purpose: options that arrive in the input are recorded in the
history, so a replay reproduces them byte for byte, while a file that can be edited
between the original execution and the replay cannot promise that.

`ActivityOptionsInput.From(config.Activity)` exists to project the `activity:` block
onto that input. The starter and loadgen just do not call it yet, so `JobInput.Activity`
is null and the workflow falls back to `ActivityOptionsInput`'s own defaults. Those
defaults are exactly the values shipped in `config.yaml`, which is why nothing looks
wrong until you edit one.

`heartbeatTimeout` and `startToCloseTimeout` are still read at startup by
`ConfigLoader.Validate` (`> 0`, and start-to-close must exceed heartbeat), so editing
them changes what the process refuses to start on and nothing else.

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
