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

// All five types: the replayer fails on a history whose type it was not given, while every
// other fixture still reports "replay OK". See docs/GOTCHAS.md, "An unregistered workflow type
// in the replayer is not a nondeterminism error". No activities: replay executes none.
var options = new WorkflowReplayerOptions()
    .AddWorkflow<HeartbeatWorkflow>()
    .AddWorkflow<SimpleNoActivity>()
    .AddWorkflow<WorkflowSimpleActivity>()
    // Its histories look nothing like the others: a local activity leaves a MarkerRecorded
    // (marker name core_local_activity) rather than an ActivityTaskScheduled/Started/Completed
    // triple. The namespace is irrelevant: the replayer reads JSON off disk and never connects.
    .AddWorkflow<WorkflowLocalActivity>()
    // A green replay here proves the workflow's determinism and nothing about resume
    // idempotence, which lives in the activity's heartbeat details. That is what
    // repro_file_scan_verified covers.
    .AddWorkflow<WorkflowFileScan>();
options.LoggerFactory = loggerFactory;

// Measured: the .NET replayer emits nothing through this runtime. Core still starts a real HTTP
// listener and /metrics answers 200 with a zero-byte body, so a Prometheus job would report the
// target up forever with every panel blank. See docs/REPLAY.md.
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
    // FromJson consumes `temporal workflow show --output json` directly, including its update
    // enum shorthands. The top-level JSON must be an object ({"events":[...]}), not a bare
    // array. The id only labels the run in replay errors, so the file name will do.
    var history = WorkflowHistory.FromJson(
        Path.GetFileNameWithoutExtension(file), await File.ReadAllTextAsync(file));

    // throwOnReplayFailure: false so a directory reports every failure, not just the first. SDK
    // defaults differ by overload: ReplayWorkflowAsync true, ReplayWorkflowsAsync false.
    var result = await replayer.ReplayWorkflowAsync(history, throwOnReplayFailure: false);

    if (result.ReplayFailure is { } failure)
    {
        // Nondeterminism arrives as WorkflowNondeterminismException, a subclass of
        // InvalidWorkflowOperationException. Its message carries [TMPRL1100], built by the Rust
        // Core and so not greppable in sdk-dotnet. Match on the type; Core may reword it.
        log.LogError("replay FAILED: {File}\n{Failure}", file, failure);
        failed++;
    }
    else
    {
        log.LogInformation("replay OK: {File}", file);
    }
}

// A short replay takes milliseconds, so the exporter would be gone before a scrape reached it.
if (!string.IsNullOrEmpty(metricsBind) && !BindAddress.IsOff(metricsBind))
{
    log.LogInformation(
        "holding http://{Bind}/metrics open for 30s. Curl it now and grep for " +
        "temporal_workflow_task_replay_latency", metricsBind);
    await Task.Delay(TimeSpan.FromSeconds(30));
}

return failed == 0 ? 0 : 1;
