# The demo scripts

```bash
./scripts/demo-up.sh      # build, start everything, wait until the demo is real
./scripts/demo-down.sh    # drain the host processes, then remove the containers
```

About 90 seconds warm. Several minutes on a cold first boot, which pulls seven images
and initialises two Postgres schemas.

`docker compose down` alone is not a teardown for this repo. Three of the four .NET
processes run on the host and compose has never heard of them, so they survive it and
keep holding :8077 and :8078.

## What up does

| Phase | Does | Fails with |
|---|---|---|
| 1 | Preflight. Collects every failure and exits once | 3 |
| 2 | `dotnet build`, then checks all three binaries exist | 4 |
| 3 | `docker compose up -d`, not time-bounded | 5 |
| 4 | Waits for the namespace, the frontend on :7233, the pushgateway, Prometheus, Grafana | 5 |
| 5 | Starts the worker on :8077 and the loadgen on :8078, detached, and proves each one connected and is exporting | 6 |
| 6 | Waits until Prometheus has actually scraped both SDK targets | 5 |
| 7 | Prints the URL table and the log paths | |
| 8 | Runs one 60-step seed workflow and waits for the result | 6 |

Phase 7 comes before phase 8 on purpose. The seed run blocks for over a minute and can
fail, and the table is the output you are waiting for.

The phase 8 seed run is one `HeartbeatWorkflow`. The loadgen started in phase 5 is what
produces continuous traffic, and it drives all three workflow types on three independent
start loops. [WORKFLOWS.md](WORKFLOWS.md) compares them.

| Loop | Workflow | Shape |
|---|---|---|
| first | `HeartbeatWorkflow` | `loadgen.rate` on a metronome, `loadgen.concurrency` in flight |
| second | `SimpleNoActivity` | `simple.rate` jittered, random signals and updates, injected overflow and post-close races |
| third | `WorkflowSimpleActivity` | `simpleActivity.rate` jittered, one Open-Meteo call per run |

`demo-up.sh` has no pass-through flag for the second and third loops, so turn them off in
the config the demo is running: `simple.enabled: false` and
`simpleActivity.enabled: false`. The binary switches `--no-simple` and
`--no-simple-activity` do the same thing on the manual path, and they are distinct and
matched exactly, so `--no-simple` does **not** touch the simple-activity loop.

The third loop is the only thing in the repo that calls a third party. At the shipped
`15s x 4` that is roughly 4 requests a minute, so a stack left up overnight stays inside
Open-Meteo's free tier. With no egress it falls back to a synthetic reading and stays
green rather than failing the run, which is why `demo-up.sh` passes on a plane. See
[CONFIG.md](CONFIG.md).

## What down does

| Phase | Does |
|---|---|
| 1 | Prints the state of all three pid files and both ports. It never acts silently |
| 2 | SIGINT to the starter, so it cancels its workflow and still pushes its metrics |
| 3 | SIGTERM to the loadgen and the worker together, then one shared drain wait |
| 4 | SIGKILL anything left, then confirms 8077 and 8078 are actually free |
| 5 | `docker compose down`, keeping the three named volumes |
| 6 | Removes pid files for processes confirmed gone. Keeps the logs |

## Flags

| Flag | Script | Does |
|---|---|---|
| `--config PATH` | up | Config for all three processes. Defaults to `$REPRO_CONFIG`, then `config.yaml`. This is how you drive the demo from the gitignored `config.local.yaml` |
| `--no-loadgen` | up | Leaves :8078 free for the two-worker recipe in [HEARTBEATING.md](HEARTBEATING.md), and quiets the stack when you want the seed run alone in the panels |
| `--keep-stack` | down | Stops the host processes, leaves all eight containers running. The fast loop is `down --keep-stack`, edit a fault knob, `up` |
| `--volumes` | down | Also deletes the three named volumes. Required when you change `NUM_HISTORY_SHARDS`. No `-v` short form, because `-v` conventionally means verbose and this one is destructive |
| `--force` | down | Skips the drain and SIGKILLs immediately, which is what [HEARTBEATING.md](HEARTBEATING.md) recommends when the drain is not the thing under test |

`--keep-stack` and `--volumes` together is an error: a volume cannot be removed while
a running container is using it.

## Env vars

| Var | Default | Purpose |
|---|---|---|
| `DEMO_DIR` | `.demo` | pid and log files |
| `DEMO_SKIP_BUILD` | unset | Skip `dotnet build` |
| `DEMO_GATE_TIMEOUT` | `90` | Per-gate budget for Grafana, seconds |
| `DEMO_STARTER_TIMEOUT` | `420` | Seed-workflow watchdog, seconds |
| `DEMO_DRAIN_TIMEOUT` | `gracefulShutdownTimeout` + 15 | SIGTERM to SIGKILL window |

`DEMO_DRAIN_TIMEOUT` is derived, not guessed. `demo-down.sh` reads
`worker.gracefulShutdownTimeout` out of the active config and adds 15 seconds of
unwind, so editing that field in `config.yaml` moves the drain budget with it.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success |
| 2 | usage: unknown flag, or a flag missing its value |
| 3 | preflight |
| 4 | `dotnet build`, or a binary missing after it |
| 5 | compose, or a readiness gate |
| 6 | a host process failed to start, or the seed run failed |
| 7 | down: a port is still held after SIGKILL, so the state is not clean |
| 130 | interrupted |

## Logs and pid files

```
.demo/worker.log     .demo/worker.pid
.demo/loadgen.log    .demo/loadgen.pid
.demo/starter.log    .demo/starter.pid
.demo/build.log
```

`up` truncates each log at launch. `down` never deletes one, so the session you just
stopped stays readable until the next `up`. The whole directory is gitignored.

## Six things the scripts do differently from the manual path

**They run the built binaries, never `dotnet run`.** `dotnet run` launches your app as
a child process, so the pid you can see is not the pid holding the port. See
[GOTCHAS.md](GOTCHAS.md). Running `src/Repro.Worker/bin/Debug/net10.0/worker` directly
makes the pid file mean what it says.

**They shield the worker and the loadgen from your Ctrl-C.** macOS has no `setsid`, and
a bash script leaves background children in its own process group, so a Ctrl-C in the
terminal would otherwise reach both and start a 30-second drain in each while they keep
holding their ports. `demo-lib.sh` sets SIGINT to ignore before `exec`, which survives
the exec. The seed starter is deliberately left unshielded: Ctrl-C there cancels the
workflow, which is that process's documented behaviour and the recipe
[DASHBOARDS.md](DASHBOARDS.md) uses to move the cancellation panels.

**They stop the starter with SIGINT, not SIGTERM.** `src/Repro.Starter/Program.cs`
registers only `Console.CancelKeyPress`. The worker and the loadgen also register
`PosixSignalRegistration` for SIGTERM; the starter does not, so a SIGTERM there takes
.NET's default path, abandons `await using var push`, loses the final Pushgateway push
and leaves `repro-workflow` running.

**They require a real metric family from the exporter.** A 200 is not enough. A client that
connects before the runtime is built binds to `TemporalRuntime.Default`, and the
exporter then answers 200 with an empty registry while Prometheus reports the target
UP and every SDK panel stays blank. Phase 5 greps for a `temporal_` line, which is the
only check that catches it.

**They gate on two Prometheus targets, not six.** `temporal-server` serves a few
hundred metric families against a 900ms `scrape_timeout` and flaps under first-boot
load, and the `prometheus` and `grafana` jobs scrape every 15s so they read `unknown`
for up to 15 seconds after boot. Gating on all six turns a healthy stack into a
timeout. The other four get reported.

**They preflight only 8077 and 8078.** The eight compose-published ports are compose's
business: a busy 7233 means the stack is already up, which is the state you want.

## The manual sequence, still supported

Nothing was removed. Four terminals:

```bash
dotnet build
docker compose up -d
dotnet run --project src/Repro.Worker      # terminal 2, SDK metrics on :8077
dotnet run --project src/Repro.LoadGen     # terminal 3, SDK metrics on :8078
dotnet run --project src/Repro.Starter     # terminal 4, one run, pushes on exit
docker compose down
```

Use it when you want a process in the foreground, when you are attaching a debugger, or
for any recipe in [HEARTBEATING.md](HEARTBEATING.md) that needs a second worker with
`--metrics off`.

## What Temporal documents about all this

Two things worth recording, both checked against the docs rather than assumed.

**The shutdown semantics this repo relies on are documented and correct.** The
sdk-dotnet README states that `ActivityExecutionContext.WorkerShutdownToken` is
cancelled first, that the worker then waits `GracefulShutdownTimeout` before issuing
actual cancellation, and that "if a long-running activity does not respect
cancellation, the shutdown may never complete". So `GracefulShutdownTimeout` is a
delay-before-cancel, not a shutdown deadline, which is why `demo-down.sh` treats
SIGKILL as an expected outcome under `fault.ignoreCancellation` rather than a failure.
The Temporal encyclopedia spells out the same Core-versus-Go difference: in Go the
shutdown completes and the activity keeps running, in Core the shutdown does not
complete while the activity does.

SIGTERM handling for the .NET SDK is not documented anywhere. The
`PosixSignalRegistration` calls in `Program.cs` are this repo's own work, and they are
the only reason `demo-down.sh` can drain a worker with SIGTERM at all.

**`temporal server start-dev` is the supported alternative, and it does not fit.** It
would replace five of the eight containers: the server, the schema init, the namespace
creation, the Web UI (`--ui-port`, default :8233) and Postgres. It does support
Prometheus metrics through `--metrics-port`, and the monitoring guide's worked example
uses the same port layout this repo scrapes. Two things rule it out here. Its only
persistence is SQLite, with no Postgres or MySQL flag, and the SQLite path is
documented as single-writer, single-shard, so `NUM_HISTORY_SHARDS=4` has nowhere to go.
And `--metrics-port` defaults to a random free port, so a static `prometheus.yml`
target needs it pinned. Prometheus, the Pushgateway and Grafana stay containers either
way. `temporalio/docker-compose` is the documented Postgres-backed local option, which
is the category this repo already occupies.
