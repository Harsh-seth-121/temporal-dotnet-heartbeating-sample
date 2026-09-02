using Microsoft.Extensions.Logging;
using Repro.Core.Cli;
using Repro.Core.Config;
using Repro.Core.Telemetry;
using Repro.Core.Workflows;
using Temporalio.Common;
using Temporalio.Worker;

var flags = Flags.Parse(args);
var historyPath = flags.Str("--history");
if (string.IsNullOrEmpty(historyPath))
{
    Console.Error.WriteLine(
        "--history is required. Capture one with:\n" +
        "  temporal workflow show --workflow-id repro-workflow --output json > history/heartbeat-job.json\n" +
        "Note there is NO --fields flag on `workflow show`; --fields belongs to `workflow list`.");
    return 2;
}

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));
var log = loggerFactory.CreateLogger("replay");

// All FIVE types, because the replayer FAILS on a history whose workflow type it was not
// given. No activity registration: the replayer does not execute them.
//
// The failure is LOUD, which is worth knowing because the previous wording here claimed the
// opposite. MEASURED with WorkflowSimpleActivity unregistered, back when three types existed
// in total -- so the available-workflows list quoted below is that run's and not what you
// would see today: the two registered fixtures still report "replay OK" and only the third
// fails, with
// InvalidWorkflowOperationException carrying "Workflow type WorkflowSimpleActivity is not
// registered on this worker, available workflows: HeartbeatWorkflow, SimpleNoActivity" and
// ApplicationFailureInfo type NotFoundError. No WorkflowNondeterminismException, no
// TMPRL1100, so it cannot be mistaken for real nondeterminism.
var options = new WorkflowReplayerOptions()
    .AddWorkflow<HeartbeatWorkflow>()
    .AddWorkflow<SimpleNoActivity>()
    .AddWorkflow<WorkflowSimpleActivity>()
    // WorkflowLocalActivity replays like the others even though its histories look nothing
    // like them: a local activity leaves a MarkerRecorded (marker name core_local_activity)
    // rather than the ActivityTaskScheduled/Started/Completed triple, and a history captured
    // from a run whose workflow task timed out carries WorkflowTaskTimedOut events and SEVERAL
    // executions of the same local activity. Replay is exactly where a claim about that shape
    // stops being a comment and becomes checkable.
    //
    // Note the namespace is irrelevant here. The replayer never connects; it reads a history
    // JSON off disk, so a fixture from repro-local-activity replays with no second client.
    .AddWorkflow<WorkflowLocalActivity>()
    // WorkflowFileScan, whose omission is the easiest one in this list to miss. It fails the way
    // the paragraph above describes -- InvalidWorkflowOperationException, ApplicationFailureInfo
    // type NotFoundError, no TMPRL1100 -- while every other fixture in the same directory still
    // reports "replay OK", so a run of the whole history/ directory reads as four passes and one
    // puzzling failure rather than as a missing registration.
    //
    // Its histories are the plainest in the set: one ScheduleActivityTask and one long-running
    // attempt, possibly several of them after a kill -9. The interesting part of this case lives
    // in the ACTIVITY's heartbeat details, which a replay never executes and therefore never
    // checks -- so a green replay here proves the workflow's own determinism and nothing at all
    // about resume idempotence. That is what repro_file_scan_verified is for.
    .AddWorkflow<WorkflowFileScan>();
options.LoggerFactory = loggerFactory;

// MEASURED: the .NET replayer emits NOTHING through this runtime.
//
// Unlike the Go replayer, which hard-codes a no-op metrics handler so you cannot even
// try, WorkflowReplayerOptions accepts a real TemporalRuntime. It is worse
// than useless: Core starts a real HTTP listener, /metrics answers 200 with a
// ZERO-BYTE body, and a Prometheus job pointed at it would report the target UP
// forever while every panel stayed blank.
//
// The flag is kept so the claim is reproducible rather than folklore. See
// docs/REPLAY.md.
var metricsBind = flags.Str("--metrics");
if (!string.IsNullOrEmpty(metricsBind) && !BindAddress.IsOff(metricsBind))
{
    options.Runtime = ReproRuntime.CreateScrape(metricsBind);
    log.LogWarning(
        "a TemporalRuntime is attached to the replayer, but replay emits NO metrics: " +
        "http://{Bind}/metrics will answer 200 with an empty body. This flag exists only " +
        "so you can confirm that yourself. See docs/REPLAY.md.", metricsBind);
}

var replayer = new WorkflowReplayer(options);

var files = Directory.Exists(historyPath)
    ? Directory.EnumerateFiles(historyPath, "*.json").Order(StringComparer.Ordinal).ToList()
    : [historyPath];

if (files.Count == 0)
{
    log.LogError("no *.json files found in {Path}", historyPath);
    return 2;
}

var failed = 0;
foreach (var file in files)
{
    // FromJson consumes `temporal workflow show --output json` directly: it runs
    // HistoryJsonFixer over the CLI's enum shorthands and parses with
    // IgnoreUnknownFields. The top-level JSON must be an OBJECT ({"events":[...]}),
    // not a bare array. MEASURED: the fixer handles the WORKFLOW_EXECUTION_UPDATE_*
    // shorthands too, so a history containing accepted updates replays as-is.
    //
    // The id is bookkeeping only. It labels the run in replay errors and does not have
    // to match the original execution. Taking it from the FILE NAME beats a hard-coded
    // "repro-workflow": with two fixtures in history/ that literal made every failure
    // report name the wrong one.
    var history = WorkflowHistory.FromJson(
        Path.GetFileNameWithoutExtension(file), await File.ReadAllTextAsync(file));

    // throwOnReplayFailure: false so a directory of fixtures reports ALL failures
    // rather than stopping at the first. Note the SDK's defaults differ by overload:
    // ReplayWorkflowAsync defaults to TRUE, ReplayWorkflowsAsync to FALSE.
    var result = await replayer.ReplayWorkflowAsync(history, throwOnReplayFailure: false);

    if (result.ReplayFailure is { } failure)
    {
        // Non-determinism arrives as WorkflowNondeterminismException, a subclass of
        // InvalidWorkflowOperationException. MEASURED: the message DOES carry
        // [TMPRL1100]. It is not a Go-only convention. The string is built by the
        // Rust Core, not by the managed SDK, so you will not find it anywhere in
        // sdk-dotnet and cannot grep your way to that conclusion. Match on the TYPE
        // anyway: the code travels with a message Core is free to reword.
        log.LogError("replay FAILED: {File}\n{Failure}", file, failure);
        failed++;
    }
    else
    {
        log.LogInformation("replay OK: {File}", file);
    }
}

// A replay of a short history takes milliseconds, so the exporter would be gone
// before anything could scrape it. Hold the endpoint open so `curl` has a chance;
// this is the only reason --metrics is usable at all on a one-shot process.
if (!string.IsNullOrEmpty(metricsBind) && !BindAddress.IsOff(metricsBind))
{
    log.LogInformation(
        "holding http://{Bind}/metrics open for 30s. Curl it now and grep for " +
        "temporal_workflow_task_replay_latency", metricsBind);
    await Task.Delay(TimeSpan.FromSeconds(30));
}

return failed == 0 ? 0 : 1;
