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
    /// <summary>Activity options, built from the values the input carried in.</summary>
    /// <remarks>
    /// GOTCHA, and this is the one people get backwards. Config is not banned from
    /// workflow code because it is "mutable process state". It is banned because a
    /// REPLAY of an old history has to emit byte-identical commands, and a file that
    /// can be edited between the original execution and the replay cannot promise
    /// that. Activity options are captured into the history as a
    /// ScheduleActivityTask command at the moment the activity is scheduled, so they
    /// only have to be stable FOR ONE EXECUTION — and options that arrive in the
    /// INPUT are stable by construction, because replay reads back the same bytes it
    /// wrote. Threading them through the input is the Temporal idiom for a
    /// configurable timeout; it removes the determinism objection instead of
    /// arguing with it, and it is what makes <c>activity:</c> in config.yaml live.
    /// <para>
    /// A null <c>activity</c> means an input that predates the field. The fallback
    /// defaults on <see cref="ActivityOptionsInput"/> are the literals this method
    /// used to hard-code, so those older histories still replay clean.
    /// </para>
    /// </remarks>
    internal static ActivityOptions BuildActivityOptions(ActivityOptionsInput? activity)
    {
        var a = activity ?? new ActivityOptionsInput();

        return new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromMilliseconds(a.StartToCloseTimeoutMs),
            ScheduleToCloseTimeout = TimeSpan.FromMilliseconds(a.ScheduleToCloseTimeoutMs),

            // REQUIRED for the activity to receive cancellation at all. The server only
            // communicates cancellation in the RESPONSE to a heartbeat RPC, so an
            // activity with no heartbeat timeout and no Heartbeat() calls can never be
            // cancelled by anything except worker shutdown. ConfigLoader.Validate
            // rejects a zero or missing activity.heartbeatTimeout for that reason.
            HeartbeatTimeout = TimeSpan.FromMilliseconds(a.HeartbeatTimeoutMs),

            RetryPolicy = new RetryPolicy
            {
                InitialInterval = TimeSpan.FromMilliseconds(a.RetryInitialIntervalMs),

                // float, not double — Temporalio.Common.RetryPolicy takes a float and
                // config.yaml's 2.0 is parsed as a double.
                BackoffCoefficient = (float)a.RetryBackoffCoefficient,
                MaximumInterval = TimeSpan.FromMilliseconds(a.RetryMaximumIntervalMs),
                MaximumAttempts = a.RetryMaximumAttempts,
            },

            // Without this the workflow reports cancelled the instant it asks, before
            // the activity has observed anything, and the whole demo is hollow: you
            // never see the activity honour the request. WaitCancellationCompleted makes
            // the workflow wait for the activity to actually finish unwinding.
            //
            // Deliberately NOT configurable: every cancellation panel and every
            // README recipe assumes it, and the two other values turn those into
            // silently empty panels rather than a different demo.
            CancellationType = ActivityCancellationType.WaitCancellationCompleted,
        };
    }

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
        //
        // Reaching here at all takes an attempt that heartbeat-times-out and keeps
        // doing so until retries are EXHAUSTED, which in practice means
        // fault.stopHeartbeating. fault.stallPastHeartbeatTimeout only stalls attempt
        // 1, so attempt 2 completes and the outcome is `completed` — see the fault
        // comments in HeartbeatActivities.
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
