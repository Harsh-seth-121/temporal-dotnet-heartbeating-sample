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

// loadgen keeps workflows flowing so the histogram panels have data, running a worker and five
// starter loops in one process. demo-up.sh's `--no-loadgen` frees :8078 for two-worker recipes.

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

// `--metrics off` means no exporter, not no runtime. See Repro.Worker/Program.cs.
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
    // A second call, not a second argument; see Repro.Worker/Program.cs.
    .AddAllActivities(new WeatherActivities(config.SimpleActivity));
// This worker is the one whose missing slot knobs made sharing them worthwhile; see WorkerKnobs.
WorkerKnobs.Apply(options, config.Worker);

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

// ExecuteAsync never succeeds, only fails, so it is observed rather than awaited.
var workerTask = worker.ExecuteAsync(shutdown.Token);

// Skip the tick at capacity rather than queue, for the reason DriverLoop records. This is the
// one loop still using a SemaphoreSlim; the four drivers use Interlocked counters.
using var slots = new SemaphoreSlim(concurrency, concurrency);
using var ticker = new PeriodicTimer(rate);

log.LogInformation(
    "loadgen: 1 workflow every {Rate}, up to {Concurrency} in flight, {Steps} steps of {Step} each",
    GoDuration.ToGoString(rate), concurrency, steps, GoDuration.ToGoString(stepDuration));

// The readiness rule for every loop below: scripts/demo-lib.sh gates loadgen readiness on the
// literal "loadgen: 1 workflow every" with a 45s budget, so anything throwing before that line
// is logged turns a working start into a timeout. Build clients, workers and drivers after it.
var simpleOn = config.Simple.Enabled && !flags.Switch("--no-simple");
var simpleTask = Task.CompletedTask;
if (simpleOn)
{
    var simpleDriver = new SimpleDriver(
        client, config.Simple, config.TaskQueue, loggerFactory.CreateLogger("simple"));

    simpleTask = simpleDriver.RunAsync(shutdown.Token);
}
else
{
    log.LogInformation("simple: OFF (simple.enabled is false, or --no-simple was passed)");
}

// Third loop. Three loops at concurrency 8 + 8 + 4 is up to 20 workflows in flight against the
// SDK's default 100 slots of each type.
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

// Fourth loop, and the only one needing a namespace of its own. Its client, worker and driver
// are built after the readiness banner, because ConnectAsync throws outright if
// create-namespace.sh has not created repro-local-activity. Gated on the loop being enabled.
var localOn = config.LocalActivity.Enabled && !flags.Switch("--no-local-activity");
TemporalWorker? laWorker = null;
var laWorkerTask = Task.CompletedTask;
var localTask = Task.CompletedTask;

if (localOn)
{
    // Role "loadgen-la", not "loadgen"; see ClientFactory's role parameter.
    var laClient = await ClientFactory.ConnectAsync(
        config, runtime, "loadgen-la", loggerFactory, config.LocalActivity.Namespace);

    // The same options Repro.Worker builds, so the two processes cannot drift.
    laWorker = new TemporalWorker(laClient, LocalActivityWorkerOptions.For(config));
    laWorkerTask = laWorker.ExecuteAsync(shutdown.Token);

    var localDriver = new LocalActivityDriver(
        laClient, config.LocalActivity, loggerFactory.CreateLogger("local-activity"));

    localTask = localDriver.RunAsync(shutdown.Token);
}
else
{
    log.LogInformation(
        "local-activity: OFF (localActivity.enabled is false, or --no-local-activity was passed); " +
        "this process makes no connection to the local-activity namespace");
}

// Fifth loop, with a client and worker of its own for a weaker reason than the fourth's:
// fileScan.taskQueue is a queue in the namespace this process already holds, so its second
// client buys only a distinct identity. Hence role "loadgen-scan" and no namespace argument.
var fileScanOn = config.FileScan.Enabled && !flags.Switch("--no-file-scan");
TemporalWorker? scanWorker = null;
var scanWorkerTask = Task.CompletedTask;
var fileScanTask = Task.CompletedTask;

if (fileScanOn)
{
    var scanClient = await ClientFactory.ConnectAsync(
        config, runtime, "loadgen-scan", loggerFactory);

    var scanOptions = new TemporalWorkerOptions(config.FileScan.TaskQueue)
        .AddWorkflow<WorkflowFileScan>()
        // config.Worker is not decoration: the activity's drain line reports its
        // gracefulShutdownTimeout, not the SDK default.
        .AddAllActivities(new FileScanActivities(config.Fault, config.Worker));

    WorkerKnobs.Apply(scanOptions, config.Worker);

    scanWorker = new TemporalWorker(scanClient, scanOptions);
    scanWorkerTask = scanWorker.ExecuteAsync(shutdown.Token);

    // The corpus check lives in the driver. The worker starts either way, so a corpus generated
    // later needs no restart.
    var fileScanDriver = new FileScanDriver(
        scanClient, config.FileScan, loggerFactory.CreateLogger("file-scan"));

    fileScanTask = fileScanDriver.RunAsync(shutdown.Token);
}
else
{
    log.LogInformation(
        "file-scan: OFF (fileScan.enabled is false, or --no-file-scan was passed); " +
        "this process neither polls the scan queue nor touches the corpus");
}

var started = 0;
var input = new JobInput(
    steps,
    (int)stepDuration.TotalMilliseconds,
    // Carries activity.* into the workflow input; see docs/CONFIG.md, "The `activity.*` rows
    // reach the workflow through its input, not through the file".
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
                    // .NET's WorkflowOptions requires an Id. Guid.NewGuid is fine: client code.
                    var handle = await client.StartWorkflowAsync(
                        (HeartbeatWorkflow wf) => wf.RunAsync(input),
                        new WorkflowOptions(id: $"repro-loadgen-{Guid.NewGuid():N}", taskQueue: config.TaskQueue));

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

// Drivers before the worker, so each summary lands while the worker is still polling. The
// catches are total rather than OperationCanceledException-only: these are top-level statements,
// so any fault escaping one await skips every await below it, the worker drain included.
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
    await fileScanTask;
}
catch (Exception e)
{
    log.LogWarning("file-scan driver ended in error: {Message}", e.Message);
}

try
{
    // Task.WhenAll, not one await then the next; see Repro.Worker/Program.cs. laWorkerTask and
    // scanWorkerTask are Task.CompletedTask when their loops are off.
    await Task.WhenAll(workerTask, laWorkerTask, scanWorkerTask);
}
catch (OperationCanceledException)
{
    // Expected, and kept narrow: a worker fault should reach the runtime, not be swallowed.
}
finally
{
    laWorker?.Dispose();
    scanWorker?.Dispose();
}

return 0;
