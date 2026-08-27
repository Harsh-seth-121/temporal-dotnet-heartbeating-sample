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

// DISPOSAL ORDER IS LOad-BEARING. `await using` declarations dispose in REVERSE
// declaration order, so declaring the push guard FIRST makes it dispose LAST --
// after the client is gone and Core has drained its buffered metric updates. This
// is the C# analogue of the Go original registering `defer flush()` before
// `defer c.Close()` so that LIFO ran the flush last. Do not move this line below
// the ConnectAsync.
await using var push = PushMetrics.Start(config.Metrics, m => log.LogInformation("{Message}", m));

var client = await ClientFactory.ConnectAsync(config, push.Runtime, "starter", loggerFactory);

var input = new JobInput(steps, (int)stepDuration.TotalMilliseconds);
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

// `push` disposes here: settle -> stop the adapter -> guaranteed final PUT.
