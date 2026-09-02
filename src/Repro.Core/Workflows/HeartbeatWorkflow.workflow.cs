using Microsoft.Extensions.Logging;
using Repro.Core.Activities;
using Repro.Core.Telemetry;
using Temporalio.Api.Enums.V1;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Repro.Core.Workflows;

/// <summary>The seed case: run one long heartbeating activity and classify how it ended.</summary>
/// <remarks>
/// Replace the body with whatever you are reproducing. Two determinism rules every workflow
/// here obeys: use <c>Workflow.UtcNow</c>, never <c>DateTime.UtcNow</c>; and read nothing from
/// config.yaml in workflow code, which is why the fault knobs reach the activity only through
/// its constructor. See docs/WORKFLOWS.md.
/// </remarks>
[Workflow]
public class HeartbeatWorkflow
{
    /// <summary>Activity options, built from the values the input carried in.</summary>
    /// <remarks>
    /// Activity options are captured into history as a ScheduleActivityTask command, so they
    /// only have to be stable for one execution, and values carried in the input are stable by
    /// construction where a value read from config.yaml is not. A null <c>activity</c> is an
    /// input predating the field; <see cref="ActivityOptionsInput"/>'s defaults are the literals
    /// this method used to hard-code, so those histories still replay clean.
    /// </remarks>
    internal static ActivityOptions BuildActivityOptions(ActivityOptionsInput? activity)
    {
        var a = activity ?? new ActivityOptionsInput();

        return new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromMilliseconds(a.StartToCloseTimeoutMs),
            ScheduleToCloseTimeout = TimeSpan.FromMilliseconds(a.ScheduleToCloseTimeoutMs),

            // Required, or cancellation never reaches the activity: the server delivers it only
            // in a heartbeat RPC response. ConfigLoader.Validate rejects zero or missing.
            HeartbeatTimeout = TimeSpan.FromMilliseconds(a.HeartbeatTimeoutMs),

            RetryPolicy = a.ToRetryPolicy(),

            // Makes the workflow wait for the activity to finish unwinding. Not configurable:
            // every cancellation panel and docs/HEARTBEATING.md recipe assumes it.
            CancellationType = ActivityCancellationType.WaitCancellationCompleted,
        };
    }

    [WorkflowRun]
    public async Task<int> RunAsync(JobInput input)
    {
        var start = Workflow.UtcNow;

        // Replay-suppressed with no opt-out, so counts here are things that happened rather
        // than things that were replayed. Every workflow in this repo relies on that.
        var meter = Workflow.MetricMeter;

        var outcome = MetricNames.Outcomes.Completed;
        var completed = 0;

        try
        {
            completed = await Workflow.ExecuteActivityAsync(
                (HeartbeatActivities a) => a.ProcessBatchAsync(input),
                BuildActivityOptions(input.Activity)).ConfigureAwait(true);
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
    /// <remarks>Order matters. IsCanceledException comes first because cancellation surfaces as
    /// OperationCanceledException, CanceledFailureException, or nested inside an
    /// ActivityFailureException depending on where you await, and only that helper recognises
    /// all three. The other four workflows classify the same way.</remarks>
    private static string Classify(Exception e)
    {
        if (TemporalException.IsCanceledException(e))
        {
            return MetricNames.Outcomes.Canceled;
        }

        // A heartbeat timeout gets its own outcome rather than landing in failed. Reaching here
        // takes attempts that heartbeat-time-out until retries are exhausted, in practice
        // fault.stopHeartbeating; fault.stallPastHeartbeatTimeout stalls only attempt 1.
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
            // namespace, task_queue and workflow_type are already root tags on
            // Workflow.MetricMeter, so re-adding them here would duplicate labels.
            [MetricNames.Tags.Outcome] = outcome,
        });

        tagged.CreateCounter<long>(MetricNames.WorkflowCompleted).Add(1);

        // CreateHistogram<TimeSpan> maps to Core's HistogramDuration kind, so the unit follows
        // UseSecondsForDuration. A long of milliseconds would hard-code it.
        tagged.CreateHistogram<TimeSpan>(MetricNames.WorkflowLatency)
            .Record(Workflow.UtcNow - start);
    }
}
