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

// SECOND WORKER, SAME CLIENT, for WorkflowFileScan and its ScanFile activity alone.
//
// NO second client here, unlike the local-activity worker below, and the reason is the whole
// difference between the two cases: a namespace is a CLIENT property, but fileScan.taskQueue is
// a QUEUE in the namespace this client is already bound to. What the separate queue buys is a
// FILTERABLE panel -- temporal_worker_task_slots_used carries no activity_type label, and the
// heartbeat board's headline stat sums it while claiming this repo has exactly one heartbeating
// activity type, so a second heartbeating activity on repro-task-queue would corrupt that panel
// with no way to filter it back out. See FileScanConfig.TaskQueue.
var scanOptions = new TemporalWorkerOptions(config.FileScan.TaskQueue)
    .AddWorkflow<WorkflowFileScan>()
    // Its OWN AddAllActivities call, for the reason the two above record: the method takes
    // exactly ONE instance. config.Worker is not decoration -- the activity's drain line
    // reports how long it has before ctx.CancellationToken fires, which is
    // worker.gracefulShutdownTimeout, and a FileScanActivities built without it names the SDK
    // default instead of the window this process is actually using.
    .AddAllActivities(new FileScanActivities(config.Fault, config.Worker));

// The same knobs as the main worker, set here BY HAND rather than shared through Repro.Core the
// way LocalActivityWorkerOptions is: that file exists because Repro.Worker and Repro.LoadGen
// build the same options for the same namespace, and the two copies had already drifted. This
// pair cannot be pulled out the same way while the two processes' scan workers differ in the
// CLIENT they bind (this one reuses the process client, the loadgen builds a "loadgen-scan" one
// for its identity), so if a third copy ever appears, extract it then.
//
// MaxHeartbeatThrottleInterval is the one knob that is load-bearing rather than tidy: it is the
// ceiling in min(0.8 x heartbeatTimeout, this), which IS the heartbeat throttle, which is
// exactly how many rows a kill -9 destroys the record of. Leave it on the SDK default and every
// number in this case's docs is wrong.
scanOptions.GracefulShutdownTimeout = config.Worker.GracefulShutdownTimeout;
scanOptions.MaxHeartbeatThrottleInterval = config.Worker.MaxHeartbeatThrottleInterval;
scanOptions.DefaultHeartbeatThrottleInterval = config.Worker.DefaultHeartbeatThrottleInterval;
if (config.Worker.MaxCachedWorkflows > 0)
{
    scanOptions.MaxCachedWorkflows = config.Worker.MaxCachedWorkflows;
}

if (config.Worker.MaxConcurrentActivities > 0)
{
    scanOptions.MaxConcurrentActivities = config.Worker.MaxConcurrentActivities;
}

if (config.Worker.MaxConcurrentWorkflowTasks > 0)
{
    scanOptions.MaxConcurrentWorkflowTasks = config.Worker.MaxConcurrentWorkflowTasks;
}

// NOT gated on fileScan.enabled and NOT on the corpus existing. `enabled` and --no-file-scan
// turn off the loadgen's driver LOOP, not this process's ability to run a scan somebody starts
// by hand; and registering the activity while the corpus is absent is what lets a corpus
// generated later be scanned with no worker restart. A scan invoked before then fails
// non-retryably on attempt 1, which is the right outcome for a config bug -- see the warning
// below the banners.
using var scanWorker = new TemporalWorker(client, scanOptions);

// SECOND CLIENT, THIRD WORKER, SECOND NAMESPACE, for WorkflowLocalActivity alone.
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
log.LogInformation(
    "worker polling {TaskQueue} on {Address}/{Namespace} for the file scan " +
    "(corpus {Path}, target {RowsPerSecond} rows/s in batches of {BatchRows}, " +
    "heartbeat timeout {HeartbeatTimeout})",
    config.FileScan.TaskQueue, config.Address, config.Namespace, config.FileScan.Path,
    config.FileScan.TargetRowsPerSecond, config.FileScan.BatchRows,
    GoDuration.ToGoString(config.FileScan.HeartbeatTimeout));

// CHECKED ONCE, WARNED ABOUT, AND OTHERWISE IGNORED. sample_files/ is gitignored and
// generated, so on a fresh clone this file is absent while fileScan.enabled is still true --
// and ConfigLoader.ValidateFileScan deliberately never stats it, because ConfigTests loads
// this same config.yaml and a stat would fail `dotnet test` on every fresh clone.
//
// The worker still polls and still registers ScanFile, so generating the corpus later needs no
// restart. Not a fatal, and not a retry loop either: a missing corpus is a CONFIG bug, so the
// activity throws non-retryably on attempt 1 rather than burning ten attempts and burying the
// cause under an ActivityFailure chain.
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
    // Task.WhenAll, NOT three sequential awaits, and the difference is a SIGKILL.
    //
    // No call succeeds; each only fails or is cancelled. On shutdown each waits for its own
    // executing activities to return. Awaited one after another, a worker would not even begin
    // draining until the one before it had finished, serialising THREE
    // gracefulShutdownTimeout windows into 90s against demo-down.sh's budget of
    // gracefulShutdownTimeout + 15 = 45s. Run concurrently the three windows overlap and the
    // teardown drains instead of being killed.
    //
    // The scan worker is the one that makes this bite. Its activity does not finish inside the
    // grace window and is not meant to: it checkpoints on the WorkerShutdownToken EDGE, keeps
    // reading, and unwinds when ctx.CancellationToken fires at the end of that window. So it
    // spends the full gracefulShutdownTimeout every single teardown, where the other two
    // usually return early.
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
