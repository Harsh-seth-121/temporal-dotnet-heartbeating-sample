using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Repro.Core;
using Repro.Core.Activities;
using Repro.Core.Cli;
using Repro.Core.Config;
using Repro.Core.Telemetry;
using Repro.Core.Temporal;
using Repro.Core.Workflows;
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
    .AddAllActivities(new HeartbeatActivities(config.Fault, config.Worker));
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

try
{
    await workerTask;
}
catch (OperationCanceledException)
{
    // Expected: the worker was cancelled by the same token.
}

return 0;
