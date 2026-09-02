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

// These two overrides land AFTER ConfigLoader.Validate, so they inherit none of its
// checks and have to repeat them. `--steps 0` otherwise walks straight past the
// "job.steps must be > 0" startup fatal and starts a workflow that completes zero
// steps and reports SUCCESS: a green run that exercised nothing.
if (steps <= 0)
{
    throw new ArgumentException(
        $"--steps must be > 0 (got {steps}). It overrides job.steps, which has the same rule.");
}

// Zero is the interesting one, not negative: a zero step still "works", it just
// spins the activity loop with no sleep in it. Negative reaches Task.Delay and
// throws from inside the activity, five attempts deep.
if (stepDuration <= TimeSpan.Zero)
{
    throw new ArgumentException(
        $"--step-duration must be > 0 (got {GoDuration.ToGoString(stepDuration)}).");
}

// --file, --rows-per-second and --max-rows land after Validate for the same reason, and they
// need more than a "> 0" because ConfigLoader.ValidateFileScan's floors are DERIVED rather
// than absolute. It checked the whole timeout ladder against fileScan's own pace: batchRows
// over targetRowsPerSecond has to stay inside [10ms, 2s] so the read loop keeps reaching a
// cancel, a drain and a heartbeat, and startToClose and scheduleToClose have to cover the
// worst-case scan AT THAT PACE.
//
// This block cannot re-derive that ladder, and deliberately does not try: the row count it was
// checked against is the largest shipped corpus, a constant that belongs to ConfigLoader and
// would be a second copy to drift here. The rule is MONOTONICITY instead -- an override may
// only make the worst case SHORTER than the one the ladder was approved for. A higher rate and
// a lower --max-rows both shorten it and are allowed; a lower rate, or dropping a bounded
// --max-rows back to "whole file", lengthen it and are refused with a message pointing at
// config.yaml, where changing the rate re-derives every rung.
//
// Without this, --rows-per-second 1 walks past all of it at once: a 600-row batch becomes a
// 10-minute window in which the activity can observe neither a cancel nor a drain nor emit one
// heartbeat, and the shipped corpus becomes a 20-DAY scan that dies of startToClose part-way
// through attempt 1 and then dies at the same place on all nine retries -- while every panel
// and the console line report exactly the rate that was asked for.
//
// Flags.Number is int, deliberately not widened: 2.1 billion rows is a ~120 GB corpus and 2.1
// billion rows/s is not a disk, so neither ceiling is reachable by the generator.
var scanRate = (long?)flags.Number("--rows-per-second") ?? config.FileScan.TargetRowsPerSecond;
var scanMaxRows = (long?)flags.Number("--max-rows") ?? config.FileScan.MaxRows;

// Resolved against the WORKING DIRECTORY, unlike fileScan.path, which ConfigLoader resolves
// against the config FILE's directory. The difference is deliberate: a path typed at a shell
// means what the shell means by it. Absolute either way, because an absolute path is what
// travels in the payload and what the corpus-identity check compares across a resume.
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
        "startToClose and scheduleToClose against, and this flag cannot re-derive those rungs. " +
        "A configured rate of 0 means no ladder was derived at all, so there is nothing here to " +
        "check a throttle against. Slow the scan down in config.yaml, where every rung is " +
        "re-derived, or shorten it with --max-rows.");
}

if (scanRate > 0 && (double)config.FileScan.BatchRows / scanRate < 0.010)
{
    throw new ArgumentException(
        $"fileScan.batchRows ({config.FileScan.BatchRows}) over --rows-per-second ({scanRate}) " +
        "is a batch period below the 10ms floor ConfigLoader enforces. Task.Delay cannot express " +
        "a sub-tick sleep and rounds up to the platform timer, so the scan runs SLOWER than the " +
        "rate every panel and the console line report. batchRows is not overridable from here, " +
        "so raise fileScan.batchRows in config.yaml alongside the rate.");
}

if (scanMaxRows < 0)
{
    throw new ArgumentException(
        $"--max-rows must be >= 0 (got {scanMaxRows}). 0 is the documented sentinel for the " +
        "whole file; a negative bound is not \"unlimited\". It also makes the completion " +
        "aggregate rowsToScan x (rowsToScan + 1) / 2 negative, so a CORRECT scan reports " +
        "repro_file_scan_verified{result=\"mismatch\"} and throws non-retryably -- the one " +
        "failure this case must never produce spuriously.");
}

// 0 means the WHOLE FILE, which is the longest scan there is, so it cannot be used to unbound a
// scan whose ladder was checked as bounded. Vacuous at the shipped fileScan.maxRows of 0, where
// the ladder was already checked against the whole file.
if (config.FileScan.MaxRows > 0 && (scanMaxRows == 0 || scanMaxRows > config.FileScan.MaxRows))
{
    throw new ArgumentException(
        $"--max-rows must be in [1, {config.FileScan.MaxRows}] while fileScan.maxRows bounds " +
        $"the scan at {config.FileScan.MaxRows} rows (got {scanMaxRows}, where 0 is the " +
        "sentinel for the whole file). Either value lengthens the scan past the worst case " +
        "ConfigLoader checked the timeout ladder against; raise fileScan.maxRows in config.yaml, " +
        "where the ladder is re-derived against it.");
}

// The final push is guaranteed by PushMetrics's SETTLE DELAY, not by disposal
// order: Temporalio 1.18.0's client is a plain `var`, is never disposed, and
// ITemporalClient is not IDisposable, so there is no LIFO interaction to arrange
// here. (The Go original genuinely did rely on ordering -- `defer flush()`
// registered before `defer c.Close()` -- which is why this looks like it should.)
// `await using` earns its keep for a duller reason: it runs the settle-and-push on
// every exit path below, including the WorkflowFailedException one.
await using var push = PushMetrics.Start(config.Metrics, m => log.LogInformation("{Message}", m));

var client = await ClientFactory.ConnectAsync(config, push.Runtime, "starter", loggerFactory);

// --file-scan: THE SAME STARTER, A DIFFERENT WORKFLOW, and deliberately not a dispatcher.
// Two cases is not a registry, and the start / attach / cancel / await shape repeated from the
// seed path below is cheaper to read than an indirection that would have to be generic over
// both the workflow type and its result type. The prose on IdConflictPolicy, on --restart and
// on Ctrl-C is under the seed path; it applies identically here and is not repeated.
//
// It must come BEFORE the seed path, not after: every branch of that path returns, so anything
// following it is unreachable and CS0162 is an error here.
if (flags.Switch("--file-scan"))
{
    // Its OWN id, and prefix-disjoint from every id this repo generates -- INCLUDING the
    // loadgen's "repro-scan-" runs, of which it is not a prefix and which are not a prefix of
    // it, so `WorkflowId STARTS_WITH "repro-scan-"` finds the loop's scans and not this one. A
    // FIXED id rather than a Guid, like the seed case, because a fixed id is what makes
    // --restart and the attach-instead-of-fail behaviour below mean anything.
    const string scanWorkflowId = "repro-file-scan";

    // `with`, not positional construction. FileScanInput carries two longs with an int between
    // them and three adjacent ints, and FileScanJob.cs's remarks give the swap that compiles
    // clean: batchRows for bufferBytes is a 65,536-row batch, a 10.9-second batch period, and
    // it breaks nothing except the drain reaction time ConfigLoader validated. Naming every
    // override is the whole protection.
    var scanInput = FileScanInput.From(config.FileScan) with
    {
        Path = scanPath,
        TargetRowsPerSecond = scanRate,
        MaxRows = scanMaxRows,
    };

    // Its OWN queue, which is the one thing the seed path cannot lend it: fileScan.taskQueue is
    // where the scan worker polls, and starting this on config.TaskQueue would leave the run
    // sitting unclaimed with nothing to say why.
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

    // The same Ctrl-C-cancels-the-WORKFLOW behaviour, and it is worth more here than on the
    // seed case: the activity's CancellationType is WaitCancellationCompleted, so a cancel is
    // delivered in the response to a heartbeat and the scan then checkpoints, unwinds and
    // reports canceled rather than being abandoned mid-file.
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
        // No RPC timeout: this long-polls for the whole scan, which is about 4m47s at the
        // shipped config and 23m57s on the largest shipped corpus.
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
        // Everything terminal in this case arrives here: a checkpoint that disagrees with
        // itself, a corpus that changed under a resume, a missing corpus, and above all an
        // aggregate that does not match its closed form. The activity's message names which.
        log.LogError("workflow failed: {Message}", e.InnerException?.Message ?? e.Message);
        return 1;
    }
}

var input = new JobInput(
    steps,
    (int)stepDuration.TotalMilliseconds,
    // Carries activity.* from config.yaml INTO the workflow input, so the values are
    // captured in history. Without this the `activity:` block is dead config: the
    // workflow falls back to ActivityOptionsInput's defaults and changing
    // heartbeatTimeout does nothing.
    ActivityOptionsInput.From(config.Activity));
var options = new WorkflowOptions(id: config.WorkflowId, taskQueue: config.TaskQueue)
{
    // Explicit, so "already running" is a message rather than a surprise. The Go
    // starter never hit this because its seed workflow finished in milliseconds; a
    // minute-long heartbeating job makes it the common case.
    IdConflictPolicy = flags.Switch("--restart")
        ? Temporalio.Api.Enums.V1.WorkflowIdConflictPolicy.TerminateExisting
        : Temporalio.Api.Enums.V1.WorkflowIdConflictPolicy.Fail,
};

WorkflowHandle<HeartbeatWorkflow, int> handle;
try
{
    handle = await client.StartWorkflowAsync((HeartbeatWorkflow wf) => wf.RunAsync(input), options);

    // ResultRunId, NOT RunId. RunId is only populated when GETTING a handle; after
    // StartWorkflowAsync it is null, and logging it prints an empty string with no
    // hint that anything is wrong.
    log.LogInformation("started workflowId={Id} runId={RunId}", handle.Id, handle.ResultRunId);
}
catch (WorkflowAlreadyStartedException)
{
    // Attaching is the useful behaviour: open a second terminal and watch the same
    // run. Pass --restart to terminate the old one and start fresh.
    handle = client.GetWorkflowHandle<HeartbeatWorkflow, int>(config.WorkflowId);
    log.LogInformation("workflowId={Id} already running; attaching", config.WorkflowId);
}

// Ctrl-C cancels the WORKFLOW, not just this process. With the activity's
// CancellationType = WaitCancellationCompleted, that is a first-class demo: the
// workflow requests cancellation, the activity observes it on its next heartbeat
// RESPONSE, unwinds, and only then does the workflow report canceled. Watch
// repro_activity_cancel{reason=...} on the heartbeat board.
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

// `push` disposes here: settle -> guaranteed final PUT -> stop the adapter.
