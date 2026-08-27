using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Repro.Core.Activities;
using Repro.Core.Cli;
using Repro.Core.Config;
using Repro.Core.Telemetry;
using Repro.Core.Temporal;
using Repro.Core.Workflows;
using Temporalio.Worker;

var flags = Flags.Parse(args);
var config = ConfigLoader.Load(ConfigLoader.Resolve(flags.Str("--config")));

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));
var log = loggerFactory.CreateLogger("worker");

// FIRST, before any TemporalClient exists. A client that connects first binds to
// TemporalRuntime.Default and its metrics are lost with no error anywhere.
var bind = flags.Str("--metrics") ?? config.Metrics.ListenAddress;
var runtime = ReproRuntime.CreateScrape(bind);
log.LogInformation("metrics: serving http://{Bind}/metrics", bind);

var client = await ClientFactory.ConnectAsync(config, runtime, "worker", loggerFactory);

var options = new TemporalWorkerOptions(config.TaskQueue)
    .AddWorkflow<HeartbeatWorkflow>()
    // Instance registration is the SetFaultConfig replacement: the fault config is
    // reachable only from this object, so no workflow can read it.
    .AddAllActivities(new HeartbeatActivities(config.Fault));

// The SDK default is TimeSpan.Zero. Zero grace plus a minute-long heartbeating
// activity is the hang this repo demonstrates on purpose (fault.ignoreCancellation);
// leaving it at the default by accident is how you suffer it instead.
options.GracefulShutdownTimeout = config.Worker.GracefulShutdownTimeout;
if (config.Worker.MaxCachedWorkflows > 0)
{
    options.MaxCachedWorkflows = config.Worker.MaxCachedWorkflows;
}

options.MaxHeartbeatThrottleInterval = config.Worker.MaxHeartbeatThrottleInterval;
options.DefaultHeartbeatThrottleInterval = config.Worker.DefaultHeartbeatThrottleInterval;
if (config.Worker.MaxConcurrentActivities > 0)
{
    options.MaxConcurrentActivities = config.Worker.MaxConcurrentActivities;
}

if (config.Worker.MaxConcurrentWorkflowTasks > 0)
{
    options.MaxConcurrentWorkflowTasks = config.Worker.MaxConcurrentWorkflowTasks;
}

using var shutdown = new CancellationTokenSource();

// Console.CancelKeyPress catches SIGINT only. `docker compose down`, `docker stop`
// and most process supervisors send SIGTERM, which it never sees — so a worker
// wired only to CancelKeyPress hangs until it is SIGKILLed and never drains.
// Both are registered even though this process is meant to run on the host,
// because the failure mode is silent and the fix is three lines.
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
log.LogInformation(
    "worker polling {TaskQueue} on {Address}/{Namespace} (graceful shutdown {Grace})",
    config.TaskQueue, config.Address, config.Namespace,
    GoDuration.ToGoString(config.Worker.GracefulShutdownTimeout));

try
{
    // Never succeeds, only fails or is cancelled. On shutdown it waits for EVERY
    // executing activity to return — an activity that swallows cancellation makes
    // this never come back, which is exactly what fault.ignoreCancellation shows.
    await worker.ExecuteAsync(shutdown.Token);
}
catch (OperationCanceledException)
{
    log.LogInformation("worker stopped");
}

return 0;
