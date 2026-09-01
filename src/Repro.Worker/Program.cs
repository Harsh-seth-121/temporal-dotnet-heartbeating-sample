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

// FIRST, before any TemporalClient exists. A client that connects first binds to
// TemporalRuntime.Default and its metrics are lost with no error anywhere.
var bind = flags.Str("--metrics") ?? config.Metrics.ListenAddress;

// `--metrics off` is how you run a SECOND worker on this host without fighting the
// first one for :8077. The "kill the worker mid-activity" recipe needs two. Off
// means NO EXPORTER, not no runtime: ClientFactory requires a runtime, and a client
// that connects without one binds to TemporalRuntime.Default, which is the silent
// metrics loss ReproRuntime exists to prevent. Adopt a telemetry-free runtime so the
// single-shot guard still owns this process.
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
    // Instance registration is the SetFaultConfig replacement: the fault config is
    // reachable only from this object, so no workflow can read it.
    .AddAllActivities(new HeartbeatActivities(config.Fault, config.Worker))
    // A SECOND call, not a second argument: AddAllActivities takes exactly ONE instance,
    // so a new activity CLASS needs its own. The two classes must not declare an activity
    // of the same name. A duplicate throws at registration, before the worker polls.
    .AddAllActivities(new WeatherActivities(config.SimpleActivity));

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
// and most process supervisors send SIGTERM, which it never sees, so a worker
// wired only to CancelKeyPress hangs until it is SIGKILLed and never drains.
// scripts/demo-down.sh relies on the SIGTERM registration below: it is the only
// reason a scripted teardown drains this worker instead of killing it.
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

// SECOND CLIENT, SECOND WORKER, SECOND NAMESPACE, for WorkflowLocalActivity alone.
//
// A namespace is a CLIENT property and a worker binds one client, so this is the price of
// putting that workflow somewhere else -- and it has to be somewhere else, because
// history.workflowTaskHeartbeatTimeout is declared server-side as NewNamespaceDurationSetting
// and filters by namespace and nothing finer. Dropping it to 1m in its own namespace is the
// only way to make that case reproducible without changing behaviour for the other three.
//
// Same runtime, deliberately. ReproRuntime's guard counts runtime CONSTRUCTIONS, not client
// bindings, so N clients on one runtime is legal by design and is what keeps both namespaces'
// series on the one :8077 exporter. Building a second runtime here would bind nothing and
// serve an empty registry.
//
// Role is "worker-la", not "worker". Identity is role@machine:pid, so two clients in one
// process sharing a role produce a byte-identical identity and `temporal workflow describe`
// stops being able to tell you which one holds a run.
var laClient = await ClientFactory.ConnectAsync(
    config, runtime, "worker-la", loggerFactory, config.LocalActivity.Namespace);

// Options come from Repro.Core so this process and Repro.LoadGen cannot drift apart; see
// LocalActivityWorkerOptions for the copied-knobs failure that motivated pulling them out.
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

try
{
    // Task.WhenAll, NOT two sequential awaits, and the difference is a SIGKILL.
    //
    // Neither call succeeds; both only fail or are cancelled. On shutdown each waits for its
    // own executing activities to return. Awaited one after the other, the second worker would
    // not even begin draining until the first had finished, serialising two
    // gracefulShutdownTimeout windows into 60s against demo-down.sh's budget of
    // gracefulShutdownTimeout + 15 = 45s. Run concurrently the two windows overlap and the
    // teardown drains instead of being killed.
    await Task.WhenAll(
        worker.ExecuteAsync(shutdown.Token),
        laWorker.ExecuteAsync(shutdown.Token));
}
catch (OperationCanceledException)
{
    log.LogInformation("workers stopped");
}

return 0;
