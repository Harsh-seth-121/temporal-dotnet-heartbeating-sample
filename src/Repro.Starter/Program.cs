using Microsoft.Extensions.Logging;
using Repro.Core;
using Repro.Core.Cli;
using Repro.Core.Config;
using Repro.Core.Temporal;
using Repro.Core.Workflows;
using Repro.Starter.Telemetry;
using Temporalio.Client;
using Temporalio.Exceptions;

var flags = Flags.Parse(args);
var config = ConfigLoader.Load(ConfigLoader.Resolve(flags.Str("--config")));

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));
var log = loggerFactory.CreateLogger("starter");

if (flags.Switch("--delete-push-group"))
{
    var deleted = await PushMetrics.DeleteGroupAsync(config.Metrics);
    log.LogInformation(
        "pushgateway group job={Job} instance={Instance}: {Result}",
        config.Metrics.PushJob, config.Metrics.PushInstance, deleted ? "deleted" : "delete failed");
    return deleted ? 0 : 1;
}

var steps = flags.Number("--steps") ?? config.Job.Steps;
var stepDuration = flags.Duration("--step-duration") ?? config.Job.StepDuration;

if (steps <= 0)
    throw new ArgumentException(
        $"--steps must be > 0 (got {steps}). It overrides job.steps, which has the same rule.");

// Zero step still works; it just spins the activity loop with no sleep in it.
// Negative reaches Task.Delay and throws from inside the activity, five attempts deep.
if (stepDuration <= TimeSpan.Zero)
    throw new ArgumentException(
        $"--step-duration must be > 0 (got {GoDuration.ToGoString(stepDuration)}).");

// --file, --rows-per-second and --max-rows land after Validate and cannot re-derive its timeout
// ladder, so the rule below is monotonicity: an override may only make the worst-case scan
// shorter. See docs/CONFIG.md, "`--rows-per-second` can only make the approved worst case
// shorter".
var scanRate = (long?)flags.Number("--rows-per-second") ?? config.FileScan.TargetRowsPerSecond;
var scanMaxRows = (long?)flags.Number("--max-rows") ?? config.FileScan.MaxRows;

// Resolved against the working directory, unlike fileScan.path, which ConfigLoader resolves
// against the config file's directory. Absolute either way, because the path travels in the
// payload and the corpus-identity check compares it across a resume.
var fileOverride = flags.Str("--file");
if (fileOverride is not null && string.IsNullOrWhiteSpace(fileOverride))
{
    throw new ArgumentException(
        "--file must name a corpus. Generate the corpora with scripts/gen-samples/gen-samples.sh, " +
        "or omit the flag to scan fileScan.path.");
}

var scanPath = fileOverride is null ? config.FileScan.Path : Path.GetFullPath(fileOverride);

if (scanRate != 0 &&
    (config.FileScan.TargetRowsPerSecond == 0 || scanRate < config.FileScan.TargetRowsPerSecond))
{
    throw new ArgumentException(
        $"--rows-per-second must be 0 (the unthrottled sentinel) or at least " +
        $"fileScan.targetRowsPerSecond ({config.FileScan.TargetRowsPerSecond}), got {scanRate}. " +
        "A lower rate makes the scan longer than the worst case ConfigLoader checked " +
        "startToClose and scheduleToClose against, and this flag cannot re-derive those rungs; a " +
        "configured rate of 0 derived no ladder at all. Slow the scan down in config.yaml, where " +
        "every rung is re-derived, or shorten it with --max-rows.");
}

if (scanRate > 0 && (double)config.FileScan.BatchRows / scanRate < 0.010)
{
    throw new ArgumentException(
        $"fileScan.batchRows ({config.FileScan.BatchRows}) over --rows-per-second ({scanRate}) " +
        "is a batch period below the 10ms floor ConfigLoader enforces. Task.Delay cannot express " +
        "a sub-tick sleep and rounds up to the platform timer, so the scan runs slower than the " +
        "rate every panel and the console line report. batchRows is not overridable from here, " +
        "so raise fileScan.batchRows in config.yaml alongside the rate.");
}

if (scanMaxRows < 0)
{
    throw new ArgumentException(
        $"--max-rows must be >= 0 (got {scanMaxRows}). 0 is the documented sentinel for the " +
        "whole file; a negative bound is not \"unlimited\". It also makes the completion " +
        "aggregate rowsToScan x (rowsToScan + 1) / 2 negative, so a correct scan reports " +
        "repro_file_scan_verified{result=\"mismatch\"} and throws non-retryably.");
}

// 0 means the whole file, the longest scan there is, so it cannot unbound a scan whose ladder
// was checked as bounded.
if (config.FileScan.MaxRows > 0 && (scanMaxRows == 0 || scanMaxRows > config.FileScan.MaxRows))
{
    throw new ArgumentException(
        $"--max-rows must be in [1, {config.FileScan.MaxRows}] while fileScan.maxRows bounds " +
        $"the scan at {config.FileScan.MaxRows} rows (got {scanMaxRows}, where 0 is the " +
        "sentinel for the whole file). Either value lengthens the scan past the worst case the " +
        "timeout ladder was checked against; raise fileScan.maxRows in config.yaml, where the " +
        "ladder is re-derived against it.");
}

// PushMetrics's settle delay guarantees the final push, not disposal order: ITemporalClient is
// not IDisposable in Temporalio 1.18.0, so there is no LIFO ordering to arrange. `await using`
// runs the settle-and-push on every exit path below, the WorkflowFailedException one included.
await using var push = PushMetrics.Start(config.Metrics, m => log.LogInformation("{Message}", m));

var client = await ClientFactory.ConnectAsync(config, push.Runtime, "starter", loggerFactory);

// --file-scan: the same starter, a different workflow. IdConflictPolicy, --restart and Ctrl-C
// work as the seed path below describes. It must come first: every branch of that path returns,
// and CS0162 is an error here.
if (flags.Switch("--file-scan"))
{
    // Prefix-disjoint from every other id this repo generates. Fixed rather than a Guid, which
    // is what makes --restart and attach mean anything.
    const string scanWorkflowId = "repro-file-scan";

    // `with`, not positional construction: FileScanInput has adjacent same-typed fields, so a
    // swapped pair compiles clean. Naming every override is the protection.
    var scanInput = FileScanInput.From(config.FileScan) with
    {
        Path = scanPath,
        TargetRowsPerSecond = scanRate,
        MaxRows = scanMaxRows,
    };

    // fileScan.taskQueue is where the scan worker polls; config.TaskQueue leaves it unclaimed.
    var scanOptions = new WorkflowOptions(
        id: scanWorkflowId, taskQueue: config.FileScan.TaskQueue)
    {
        IdConflictPolicy = flags.Switch("--restart")
            ? Temporalio.Api.Enums.V1.WorkflowIdConflictPolicy.TerminateExisting
            : Temporalio.Api.Enums.V1.WorkflowIdConflictPolicy.Fail,
    };

    WorkflowHandle<WorkflowFileScan, FileScanResult> scanHandle;
    try
    {
        scanHandle = await client.StartWorkflowAsync(
            (WorkflowFileScan wf) => wf.RunAsync(scanInput), scanOptions);
        log.LogInformation(
            "started workflowId={Id} runId={RunId} on {TaskQueue}: scanning {Path} at " +
            "{RowsPerSecond} rows/s, maxRows {MaxRows}",
            scanHandle.Id, scanHandle.ResultRunId, config.FileScan.TaskQueue, scanPath,
            scanRate, scanMaxRows);
    }
    catch (WorkflowAlreadyStartedException)
    {
        scanHandle = client.GetWorkflowHandle<WorkflowFileScan, FileScanResult>(scanWorkflowId);
        log.LogInformation("workflowId={Id} already running; attaching", scanHandle.Id);
    }

    // Ctrl-C cancels the workflow, as on the seed path, so the scan checkpoints and unwinds.
    using var scanInterrupt = new CancellationTokenSource();
    if (!flags.Switch("--no-cancel-on-interrupt"))
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            log.LogInformation(
                "canceling workflow {Id} (pass --no-cancel-on-interrupt to detach instead)",
                scanHandle.Id);
            _ = scanHandle.CancelAsync();
            scanInterrupt.Cancel();
        };
    }

    try
    {
        // No RPC timeout: this long-polls for the whole scan, about 4m47s at the shipped config.
        var scanResult = await scanHandle.GetResultAsync();
        log.LogInformation(
            "result: {Rows} rows, {Bytes} bytes, indexSum {IndexSum}, wordByteSum " +
            "{WordByteSum}, verified={Verified}",
            scanResult.Rows, scanResult.Bytes, scanResult.IndexSum, scanResult.WordByteSum,
            scanResult.Verified);
        return 0;
    }
    catch (WorkflowFailedException e)
    {
        // Everything terminal arrives here, and the activity's message names which.
        log.LogError("workflow failed: {Message}", e.InnerException?.Message ?? e.Message);
        return 1;
    }
}

var input = new JobInput(
    steps,
    (int)stepDuration.TotalMilliseconds,
    // See docs/CONFIG.md, "The `activity.*` rows reach the workflow through its input, not
    // through the file".
    ActivityOptionsInput.From(config.Activity));
var options = new WorkflowOptions(id: config.WorkflowId, taskQueue: config.TaskQueue)
{
    // Explicit, so "already running" is a message. A minute-long job makes it the common case.
    IdConflictPolicy = flags.Switch("--restart")
        ? Temporalio.Api.Enums.V1.WorkflowIdConflictPolicy.TerminateExisting
        : Temporalio.Api.Enums.V1.WorkflowIdConflictPolicy.Fail,
};

WorkflowHandle<HeartbeatWorkflow, int> handle;
try
{
    handle = await client.StartWorkflowAsync((HeartbeatWorkflow wf) => wf.RunAsync(input), options);

    // ResultRunId, not RunId: RunId is populated only when getting a handle, so after
    // StartWorkflowAsync it is null and logs as an empty string.
    log.LogInformation("started workflowId={Id} runId={RunId}", handle.Id, handle.ResultRunId);
}
catch (WorkflowAlreadyStartedException)
{
    // Attaching is the useful behaviour. Pass --restart to terminate and start fresh.
    handle = client.GetWorkflowHandle<HeartbeatWorkflow, int>(config.WorkflowId);
    log.LogInformation("workflowId={Id} already running; attaching", config.WorkflowId);
}

// Ctrl-C cancels the workflow, not just this process. The activity's CancellationType is
// WaitCancellationCompleted, so it observes the cancel on its next heartbeat response and
// unwinds before the workflow reports canceled. Watch repro_activity_cancel on the board.
using var interrupt = new CancellationTokenSource();
if (!flags.Switch("--no-cancel-on-interrupt"))
{
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        log.LogInformation("canceling workflow {Id} (pass --no-cancel-on-interrupt to detach instead)", handle.Id);
        _ = handle.CancelAsync();
        interrupt.Cancel();
    };
}

try
{
    var completed = await handle.GetResultAsync();
    log.LogInformation("result: completed {Steps} steps", completed);
    return 0;
}
catch (WorkflowFailedException e)
{
    log.LogError("workflow failed: {Message}", e.InnerException?.Message ?? e.Message);
    return 1;
}

// `push` disposes here: settle, guaranteed final PUT, stop the adapter.
