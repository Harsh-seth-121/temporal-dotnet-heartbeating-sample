using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Repro.Core;
using Repro.Core.Activities;
using Repro.Core.Cli;
using Repro.Core.Config;
using Repro.Core.Telemetry;
using Repro.Core.Temporal;
using Repro.Core.Workflows;
using Temporalio.Runtime;
using Temporalio.Worker;

var flags = Flags.Parse(args);
var config = ConfigLoader.Load(ConfigLoader.Resolve(flags.Str("--config")));

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));
var log = loggerFactory.CreateLogger("worker");

// Resolved before any TemporalClient exists. See docs/GOTCHAS.md, "TemporalRuntime must be
// built once, first, and shared".
var bind = flags.Str("--metrics") ?? config.Metrics.ListenAddress;

// `--metrics off` lets a second worker share this host without fighting for :8077, which the
// "kill the worker mid-activity" recipe needs. No exporter, but still a runtime.
var metricsOff = BindAddress.IsOff(bind);
var runtime = metricsOff
    ? ReproRuntime.Adopt(new TemporalRuntime(new TemporalRuntimeOptions()))
    : ReproRuntime.CreateScrape(bind);
if (metricsOff)
{
    log.LogInformation("metrics: OFF; this worker exports nothing and binds no port");
}
else
{
    log.LogInformation("metrics: serving http://{Bind}/metrics", bind);
}

var client = await ClientFactory.ConnectAsync(config, runtime, "worker", loggerFactory);

var options = new TemporalWorkerOptions(config.TaskQueue)
    .AddWorkflow<HeartbeatWorkflow>()
    .AddWorkflow<SimpleNoActivity>()
    .AddWorkflow<WorkflowSimpleActivity>()
    // Instance registration keeps the fault config reachable only here, never from a workflow.
    .AddAllActivities(new HeartbeatActivities(config.Fault, config.Worker))
    // A second call, not a second argument: AddAllActivities takes one instance, and two
    // classes declaring the same activity name throw at registration.
    .AddAllActivities(new WeatherActivities(config.SimpleActivity));

// The six worker: knobs, and why they live in one place. See WorkerKnobs.
WorkerKnobs.Apply(options, config.Worker);

using var shutdown = new CancellationTokenSource();

// Console.CancelKeyPress catches SIGINT only, so a worker wired to it alone is SIGKILLed
// without draining when scripts/demo-down.sh sends SIGTERM.
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

// Second worker, same client: fileScan.taskQueue is a queue inside the namespace this client
// already holds. The separate queue keeps the heartbeat board's slot panel filterable; see
// FileScanConfig.TaskQueue.
var scanOptions = new TemporalWorkerOptions(config.FileScan.TaskQueue)
    .AddWorkflow<WorkflowFileScan>()
    // config.Worker is not decoration: the activity's drain line reports its
    // gracefulShutdownTimeout, and without it names the SDK default.
    .AddAllActivities(new FileScanActivities(config.Fault, config.Worker));

WorkerKnobs.Apply(scanOptions, config.Worker);

// Not gated on fileScan.enabled or on the corpus existing: `enabled` turns off the loadgen's
// driver loop, and registering now means a corpus generated later needs no restart.
using var scanWorker = new TemporalWorker(client, scanOptions);

// Second client, third worker, second namespace, for WorkflowLocalActivity alone; see
// docs/GOTCHAS.md, "history.workflowTaskHeartbeatTimeout is namespace-scoped and nothing finer".
// Same runtime, so both namespaces keep exporting on the one :8077. Role "worker-la".
var laClient = await ClientFactory.ConnectAsync(
    config, runtime, "worker-la", loggerFactory, config.LocalActivity.Namespace);

// Options from Repro.Core, so this process and Repro.LoadGen cannot drift.
using var laWorker = new TemporalWorker(laClient, LocalActivityWorkerOptions.For(config));

log.LogInformation(
    "worker polling {TaskQueue} on {Address}/{Namespace} (graceful shutdown {Grace})",
    config.TaskQueue, config.Address, config.Namespace,
    GoDuration.ToGoString(config.Worker.GracefulShutdownTimeout));
log.LogInformation(
    "worker polling {TaskQueue} on {Address}/{Namespace} for local activities " +
    "(up to {MaxLocal} concurrent)",
    config.LocalActivity.TaskQueue, config.Address, config.LocalActivity.Namespace,
    config.LocalActivity.MaxConcurrentLocalActivities);
log.LogInformation(
    "worker polling {TaskQueue} on {Address}/{Namespace} for the file scan " +
    "(corpus {Path}, target {RowsPerSecond} rows/s in batches of {BatchRows}, " +
    "heartbeat timeout {HeartbeatTimeout})",
    config.FileScan.TaskQueue, config.Address, config.Namespace, config.FileScan.Path,
    config.FileScan.TargetRowsPerSecond, config.FileScan.BatchRows,
    GoDuration.ToGoString(config.FileScan.HeartbeatTimeout));

// Checked once and warned about, never fatal and never retried: a missing corpus is a config
// bug. See docs/CONFIG.md, "Absent corpus, and why `dotnet test` still passes without one".
if (!File.Exists(config.FileScan.Path))
{
    log.LogWarning(
        "fileScan: no corpus at {Path}. This worker polls {TaskQueue} and registers ScanFile " +
        "anyway, so a corpus generated later needs no restart -- but any scan started before " +
        "then fails NON-RETRYABLY on attempt 1. Generate the corpora with " +
        "scripts/gen-samples/gen-samples.sh.",
        config.FileScan.Path, config.FileScan.TaskQueue);
}

try
{
    // Task.WhenAll, not three sequential awaits: each worker drains its own activities, so in
    // sequence the three gracefulShutdownTimeout windows serialise into 90s against
    // demo-down.sh's budget of gracefulShutdownTimeout + 15 = 45s. The scan worker always
    // spends its full window, so this is what keeps a teardown inside the budget.
    await Task.WhenAll(
        worker.ExecuteAsync(shutdown.Token),
        laWorker.ExecuteAsync(shutdown.Token),
        scanWorker.ExecuteAsync(shutdown.Token));
}
catch (OperationCanceledException)
{
    log.LogInformation("workers stopped");
}

return 0;
