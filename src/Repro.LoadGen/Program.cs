using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Repro.Core;
using Repro.Core.Activities;
using Repro.Core.Cli;
using Repro.Core.Config;
using Repro.Core.Telemetry;
using Repro.Core.Temporal;
using Repro.Core.Workflows;
using Repro.LoadGen;
using Temporalio.Client;
using Temporalio.Runtime;
using Temporalio.Worker;

// loadgen keeps workflows flowing so the histogram panels have data. It runs BOTH a
// worker and a starter loop in one process, so this one process is enough to make
// every dashboard move. scripts/demo-up.sh starts it alongside Repro.Worker;
// `--no-loadgen` there leaves :8078 free for the two-worker recipes.

var flags = Flags.Parse(args);
var config = ConfigLoader.Load(ConfigLoader.Resolve(flags.Str("--config")));

var rate = flags.Duration("--rate") ?? config.Loadgen.Rate;
var concurrency = flags.Number("--concurrency") ?? config.Loadgen.Concurrency;
var steps = flags.Number("--steps") ?? config.Loadgen.Steps;
var stepDuration = flags.Duration("--step-duration") ?? config.Job.StepDuration;

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));
var log = loggerFactory.CreateLogger("loadgen");

var bind = flags.Str("--metrics") ?? config.Metrics.LoadgenAddress;

// Same deal as the worker: `--metrics off` means no exporter, not no runtime.
// ClientFactory needs one, and a client that connects without it binds to
// TemporalRuntime.Default and loses its metrics with no error anywhere.
var metricsOff = BindAddress.IsOff(bind);
var runtime = metricsOff
    ? ReproRuntime.Adopt(new TemporalRuntime(new TemporalRuntimeOptions()))
    : ReproRuntime.CreateScrape(bind);
if (metricsOff)
{
    log.LogInformation("metrics: OFF; this loadgen exports nothing and binds no port");
}
else
{
    log.LogInformation("metrics: serving http://{Bind}/metrics", bind);
}

var client = await ClientFactory.ConnectAsync(config, runtime, "loadgen", loggerFactory);

var options = new TemporalWorkerOptions(config.TaskQueue)
    .AddWorkflow<HeartbeatWorkflow>()
    .AddWorkflow<SimpleNoActivity>()
    .AddWorkflow<WorkflowSimpleActivity>()
    .AddAllActivities(new HeartbeatActivities(config.Fault, config.Worker))
    // A SECOND call, not a second argument: AddAllActivities takes exactly ONE instance,
    // so a new activity CLASS needs its own. The two classes must not declare an activity
    // of the same name. A duplicate throws at registration, before the worker polls.
    .AddAllActivities(new WeatherActivities(config.SimpleActivity));
options.GracefulShutdownTimeout = config.Worker.GracefulShutdownTimeout;
if (config.Worker.MaxCachedWorkflows > 0)
{
    options.MaxCachedWorkflows = config.Worker.MaxCachedWorkflows;
}

options.MaxHeartbeatThrottleInterval = config.Worker.MaxHeartbeatThrottleInterval;
options.DefaultHeartbeatThrottleInterval = config.Worker.DefaultHeartbeatThrottleInterval;

// All six worker: knobs, same as Repro.Worker. These two were missing, so the :8078
// worker kept the SDK defaults (100 / 100) whatever config.yaml said and the
// slot-saturation panels could only ever be driven from the :8077 worker.
if (config.Worker.MaxConcurrentActivities > 0)
{
    options.MaxConcurrentActivities = config.Worker.MaxConcurrentActivities;
}

if (config.Worker.MaxConcurrentWorkflowTasks > 0)
{
    options.MaxConcurrentWorkflowTasks = config.Worker.MaxConcurrentWorkflowTasks;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    shutdown.Cancel();
});

using var worker = new TemporalWorker(client, options);

// ExecuteAsync never succeeds, only fails, so it is a background task we observe
// rather than await.
var workerTask = worker.ExecuteAsync(shutdown.Token);

// Semaphore plus SKIP-the-tick at capacity, exactly like the Go original's
// `select { case sem <- struct{}{}: default: continue }`. Queueing instead would
// build an unbounded backlog and the panel you are watching would stop reflecting
// the rate you configured.
using var slots = new SemaphoreSlim(concurrency, concurrency);
using var ticker = new PeriodicTimer(rate);

log.LogInformation(
    "loadgen: 1 workflow every {Rate}, up to {Concurrency} in flight, {Steps} steps of {Step} each",
    GoDuration.ToGoString(rate), concurrency, steps, GoDuration.ToGoString(stepDuration));

// SECOND LOOP, started AFTER the banner above. scripts/demo-lib.sh:70 gates loadgen
// readiness on the literal substring "loadgen: 1 workflow every" with a 45s budget, so
// anything that could throw before that line is logged turns a working start into a
// demo-up.sh timeout.
var simpleOn = config.Simple.Enabled && !flags.Switch("--no-simple");
var simpleTask = Task.CompletedTask;
if (simpleOn)
{
    var simpleDriver = new SimpleDriver(
        client, config.Simple, config.TaskQueue, loggerFactory.CreateLogger("simple"));

    // Not awaited here: RunAsync yields at its first Task.Delay and then runs alongside
    // the heartbeat loop below, on the same client, worker and shutdown token.
    simpleTask = simpleDriver.RunAsync(shutdown.Token);
}
else
{
    log.LogInformation("simple: OFF (simple.enabled is false, or --no-simple was passed)");
}

// THIRD LOOP, and like the second it MUST be constructed after the banner above, for the
// same demo-lib.sh:70 readiness reason.
//
// The process now runs three loops at concurrency 8 + 8 + 4, so up to 20 workflows in
// flight against the SDK's default 100 workflow-task and 100 activity slots. Nothing to
// change; it is the number you want when a slot-saturation panel moves.
var weatherOn = config.SimpleActivity.Enabled && !flags.Switch("--no-simple-activity");
var weatherTask = Task.CompletedTask;
if (weatherOn)
{
    var weatherDriver = new SimpleActivityDriver(
        client, config.SimpleActivity, config.TaskQueue,
        loggerFactory.CreateLogger("simple-activity"));

    weatherTask = weatherDriver.RunAsync(shutdown.Token);
}
else
{
    log.LogInformation(
        "simple-activity: OFF (simpleActivity.enabled is false, or --no-simple-activity was passed)");
}

// FOURTH LOOP, and the only one that needs a client, a worker and a namespace of its own.
//
// Everything about it is constructed HERE, after the readiness banner, and that placement is
// not stylistic. demo-lib.sh gates loadgen readiness on the literal substring
// "loadgen: 1 workflow every" with a 45s budget, so anything that can THROW before that line
// is logged turns a working start into a demo-up.sh timeout. This block can throw for a very
// ordinary reason: connecting to repro-local-activity fails outright if
// create-namespace.sh has not created it, which is the state of every stack that predates
// this feature. The rule the other loops follow as "construct the driver after the banner" is
// therefore "construct the CLIENT and the WORKER after the banner" here.
//
// The whole block is also gated on the loop being enabled, so --no-local-activity leaves this
// process with no dependency on the second namespace existing at all.
var localOn = config.LocalActivity.Enabled && !flags.Switch("--no-local-activity");
TemporalWorker? laWorker = null;
var laWorkerTask = Task.CompletedTask;
var localTask = Task.CompletedTask;

if (localOn)
{
    // Role "loadgen-la", not "loadgen". Identity is role@machine:pid, so two clients in one
    // process sharing a role are indistinguishable in `temporal workflow describe`.
    var laClient = await ClientFactory.ConnectAsync(
        config, runtime, "loadgen-la", loggerFactory, config.LocalActivity.Namespace);

    var laOptions = new TemporalWorkerOptions(config.LocalActivity.TaskQueue)
        .AddWorkflow<WorkflowLocalActivity>()
        // A local activity resolves against the registry of the worker running the WORKFLOW,
        // so PiActivities belongs on THIS worker and not the one above.
        .AddAllActivities(new PiActivities());

    laOptions.LocalActivityWorkerOnly = true;
    laOptions.GracefulShutdownTimeout = config.Worker.GracefulShutdownTimeout;
    laOptions.MaxConcurrentLocalActivities = config.LocalActivity.MaxConcurrentLocalActivities;

    if (config.Worker.MaxCachedWorkflows > 0)
    {
        laOptions.MaxCachedWorkflows = config.Worker.MaxCachedWorkflows;
    }

    if (config.Worker.MaxConcurrentWorkflowTasks > 0)
    {
        laOptions.MaxConcurrentWorkflowTasks = config.Worker.MaxConcurrentWorkflowTasks;
    }

    laWorker = new TemporalWorker(laClient, laOptions);
    laWorkerTask = laWorker.ExecuteAsync(shutdown.Token);

    var localDriver = new LocalActivityDriver(
        laClient, config.LocalActivity, loggerFactory.CreateLogger("local-activity"));

    localTask = localDriver.RunAsync(shutdown.Token);
}
else
{
    log.LogInformation(
        "local-activity: OFF (localActivity.enabled is false, or --no-local-activity was passed); "
        + "this process makes no connection to the local-activity namespace");
}

var started = 0;
var input = new JobInput(
    steps,
    (int)stepDuration.TotalMilliseconds,
    // Carries activity.* from config.yaml INTO the workflow input, so the values are
    // captured in history. Without this the `activity:` block is dead config: the
    // workflow falls back to ActivityOptionsInput's defaults and changing
    // heartbeatTimeout does nothing.
    ActivityOptionsInput.From(config.Activity));

try
{
    while (await ticker.WaitForNextTickAsync(shutdown.Token))
    {
        if (!slots.Wait(0))
        {
            continue;   // at capacity; skip this tick rather than queue unboundedly
        }

        started++;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    // Go passed StartWorkflowOptions{TaskQueue} with no ID and let the
                    // SDK generate one. .NET's WorkflowOptions REQUIRES an Id, so it is
                    // generated here. Guid.NewGuid is fine: this is client code.
                    var handle = await client.StartWorkflowAsync(
                        (HeartbeatWorkflow wf) => wf.RunAsync(input),
                        new WorkflowOptions(id: $"repro-loadgen-{Guid.NewGuid():N}", taskQueue: config.TaskQueue));

                    // Drain the result so failures are OBSERVED rather than ignored.
                    await handle.GetResultAsync();
                }
                catch (Exception e) when (!shutdown.Token.IsCancellationRequested)
                {
                    log.LogWarning("run failed: {Message}", e.Message);
                }
                finally
                {
                    slots.Release();
                }
            },
            CancellationToken.None);
    }
}
catch (OperationCanceledException)
{
    log.LogInformation("loadgen: shutting down after starting {Count} workflows", started);
}

// Drivers before the worker, so each driver's final summary lands while the worker is still
// polling and its in-flight runs can still complete.
//
// TOTAL catches, deliberately rather than out of laziness. Both drivers already swallow
// OperationCanceledException internally and return normally, so an OCE-only handler here is
// dead code. And because these are bare top-level statements, ANY other fault escaping the
// first await would skip every await below it, including the worker drain. That is how a
// driver bug silently turns into a worker that was never drained, which is the opposite of
// what this ordering exists to guarantee. Logging and continuing costs a process-killing
// stack trace and buys a guaranteed drain; at shutdown, the drain is worth more.
//
// Not `await Task.WhenAll(simpleTask, weatherTask)`: it would surface only the first fault
// and would drop the guarantee that each driver's summary lands before the worker's.
try
{
    await simpleTask;
}
catch (Exception e)
{
    log.LogWarning("simple driver ended in error: {Message}", e.Message);
}

try
{
    await weatherTask;
}
catch (Exception e)
{
    log.LogWarning("simple-activity driver ended in error: {Message}", e.Message);
}

try
{
    await localTask;
}
catch (Exception e)
{
    log.LogWarning("local-activity driver ended in error: {Message}", e.Message);
}

try
{
    // Task.WhenAll, NOT one await then the other, and the difference is a SIGKILL. Each worker
    // waits for its own executing activities on shutdown; awaited in sequence the second would
    // not start draining until the first had finished, serialising two gracefulShutdownTimeout
    // windows into 60s against demo-down.sh's budget of gracefulShutdownTimeout + 15 = 45s.
    // Concurrently the two windows overlap. laWorkerTask is Task.CompletedTask when the
    // local-activity loop is off, so this is unchanged for --no-local-activity.
    await Task.WhenAll(workerTask, laWorkerTask);
}
catch (OperationCanceledException)
{
    // Expected, and kept narrow on purpose: this one really is the shutdown token, and a
    // worker fault SHOULD reach the runtime rather than be logged and swallowed.
}
finally
{
    laWorker?.Dispose();
}

return 0;
