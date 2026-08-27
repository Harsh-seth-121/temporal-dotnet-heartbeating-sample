using Microsoft.Extensions.Logging;
using Repro.Core.Activities;
using Repro.Core.Telemetry;
using Temporalio.Api.Enums.V1;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Repro.Core.Workflows;

/// <summary>
/// The seed case: run one long heartbeating activity and classify how it ended.
/// </summary>
/// <remarks>
/// Replace the body with whatever you are reproducing. Two determinism rules this
/// obeys: use <c>Workflow.UtcNow</c>, never <c>DateTime.UtcNow</c>; and never read
/// the fault config from workflow code — it is reachable only through the activity
/// object's constructor, which is why there is no ambient global to reach for.
/// </remarks>
[Workflow]
public class HeartbeatWorkflow
{
    /// <summary>Activity options, built here so the workflow owns its own timeouts.</summary>
    /// <remarks>
    /// These are literals rather than config reads ON PURPOSE. Reading mutable
    /// process state from workflow code is a determinism violation: change the file,
    /// replay an old history, and the commands no longer match. The Go original
    /// carried the same warning.
    /// </remarks>
    internal static ActivityOptions BuildActivityOptions() => new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(10),
        ScheduleToCloseTimeout = TimeSpan.FromHours(1),

        // REQUIRED for the activity to receive cancellation at all. The server only
        // communicates cancellation in the RESPONSE to a heartbeat RPC, so an
        // activity with no heartbeat timeout and no Heartbeat() calls can never be
        // cancelled by anything except worker shutdown.
        HeartbeatTimeout = TimeSpan.FromSeconds(5),

        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            BackoffCoefficient = 2.0F,
            MaximumInterval = TimeSpan.FromSeconds(10),
            MaximumAttempts = 5,
        },

        // Without this the workflow reports cancelled the instant it asks, before
        // the activity has observed anything, and the whole demo is hollow: you
        // never see the activity honour the request. WaitCancellationCompleted makes
        // the workflow wait for the activity to actually finish unwinding.
        CancellationType = ActivityCancellationType.WaitCancellationCompleted,
    };

    [WorkflowRun]
    public async Task<int> RunAsync(JobInput input)
    {
        var start = Workflow.UtcNow;

        // Replay-suppressed with no opt-out: ReplaySafeMetricMeter is internal and
        // Workflow.MetricMeter is the only route to it. Counts here are therefore
        // "things that happened", not "things that were replayed".
        var meter = Workflow.MetricMeter;

        var outcome = MetricNames.Outcomes.Completed;
        var completed = 0;

        try
        {
            completed = await Workflow.ExecuteActivityAsync(
                (HeartbeatActivities a) => a.ProcessBatchAsync(input),
                BuildActivityOptions()).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            outcome = Classify(e);
            Workflow.Logger.LogWarning("workflow ending as {Outcome}: {Message}", outcome, e.Message);
            Record(meter, outcome, start);
            throw;
        }

        Record(meter, outcome, start);
        return completed;
    }

    /// <summary>Map an activity failure onto exactly one of the four outcome values.</summary>
    /// <remarks>
    /// Order matters. IsCanceledException comes FIRST because cancellation surfaces
    /// as OperationCanceledException, CanceledFailureException, or nested inside an
    /// ActivityFailureException depending on where you await — and that helper is
    /// the only reliable way to recognise all three.
    /// </remarks>
    private static string Classify(Exception e)
    {
        if (TemporalException.IsCanceledException(e))
        {
            return MetricNames.Outcomes.Canceled;
        }

        // A heartbeat timeout is NOT a generic failure and lumping it in with one
        // hides the most interesting thing this repo can show you. The chain is
        // ActivityFailureException -> TimeoutFailureException{TimeoutType.Heartbeat}.
        if (e is ActivityFailureException
            {
                InnerException: TimeoutFailureException { TimeoutType: TimeoutType.Heartbeat },
            })
        {
            return MetricNames.Outcomes.TimedOut;
        }

        return MetricNames.Outcomes.Failed;
    }

    private static void Record(MetricMeter meter, string outcome, DateTime start)
    {
        var tagged = meter.WithTags(new Dictionary<string, object>
        {
            // namespace / task_queue / workflow_type are already root tags on
            // Workflow.MetricMeter. Re-adding them would duplicate labels.
            [MetricNames.Tags.Outcome] = outcome,
        });

        tagged.CreateCounter<long>(MetricNames.WorkflowCompleted).Add(1);

        // CreateHistogram<TimeSpan> maps to Core's HistogramDuration kind, so the
        // value follows UseSecondsForDuration automatically. Recording a long of
        // milliseconds by hand would hard-code the unit and silently disagree with
        // every built-in latency metric if that flag ever changed.
        tagged.CreateHistogram<TimeSpan>(MetricNames.WorkflowLatency)
            .Record(Workflow.UtcNow - start);
    }
}
