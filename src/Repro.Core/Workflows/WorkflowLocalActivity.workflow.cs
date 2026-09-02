using Microsoft.Extensions.Logging;
using Repro.Core.Activities;
using Repro.Core.Telemetry;
using Temporalio.Api.Enums.V1;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Repro.Core.Workflows;

/// <summary>One local activity, so a marker in the history and usually a workflow task
/// timeout.</summary>
/// <remarks>
/// Read this against <see cref="WorkflowSimpleActivity"/>, the ordinary activity, whose
/// determinism rules it shares. A local activity executes inside the workflow task, writes a
/// <c>MarkerRecorded</c> event named <c>core_local_activity</c>, holds a LocalActivityWorker
/// slot, and is invisible to every server-side activity metric.
/// <para>
/// The failure this file exists for: the workflow task stays open for the whole burn, the
/// server times it out at <c>history.workflowTaskHeartbeatTimeout</c> (1m in this workflow's
/// own namespace, down from 30m), and the burn restarts from zero because a local activity's
/// result is not in history until it completes. <c>WorkflowOptions.RunTimeout</c> ends the
/// loop, closing the run without scheduling a workflow task, so <see cref="RunAsync"/> never
/// resumes and records nothing. That is why <c>repro_pi_attempt_started</c>, emitted from
/// activity code, is the primary signal. See docs/WORKFLOWS.md.
/// </para>
/// </remarks>
[Workflow]
public class WorkflowLocalActivity
{
    /// <summary>Two timeouts and a retry policy, none of which bound the re-execution loop.</summary>
    /// <remarks>
    /// Every rung is either unreachable or does not do what its name suggests at the shipped
    /// config, which is the lesson. docs/WORKFLOWS.md, "What stops it, and what does not", has
    /// all four and the sdk-core chain behind the schedule-to-close claim.
    /// <para>
    /// <c>LocalActivityOptions</c> has no <c>HeartbeatTimeout</c> member at all, the structural
    /// reason this repo's heartbeat apparatus does not apply here. <c>StartToCloseTimeout</c> is
    /// set only because the SDK requires one of the two, and cannot fire. The clock on
    /// <c>ScheduleToCloseTimeout</c> restarts on every re-dispatch, so it does not accumulate;
    /// setting it below the heartbeat timeout is the documented fix and the only regime in which
    /// the rung fires, which is why <see cref="Classify"/> matches ScheduleToClose and why
    /// ConfigLoader does not order it against startToCloseTimeout. <c>RetryPolicy</c> must be
    /// set, because unset means retry forever here, and still bounds nothing: a re-execution
    /// arrives as attempt 1 again. <c>CancellationType</c> is left at the .NET default, which
    /// disagrees with sdk-core: <c>ScheduleLocalActivity.CancellationType</c> upstream says lang
    /// should default it to WAIT_CANCELLATION_COMPLETED.
    /// </para>
    /// </remarks>
    internal static LocalActivityOptions BuildLocalActivityOptions(LocalActivityOptionsInput? activity)
    {
        var a = activity ?? new LocalActivityOptionsInput();

        return new LocalActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromMilliseconds(a.StartToCloseTimeoutMs),
            ScheduleToCloseTimeout = TimeSpan.FromMilliseconds(a.ScheduleToCloseTimeoutMs),

            RetryPolicy = a.ToRetryPolicy(),
        };
    }

    [WorkflowRun]
    public async Task<PiEstimate> RunAsync(LocalActivityInput input)
    {
        var start = Workflow.UtcNow;

        // Replay-suppressed, as in HeartbeatWorkflow.RunAsync. Right for "how many runs
        // finished", wrong for "how often did the burn execute", counted from activity code.
        var meter = Workflow.MetricMeter;

        PiEstimate estimate;
        try
        {
            // ExecuteLocalActivityAsync, not ExecuteActivityAsync: no activity task, no activity
            // slot, result in a marker. ConfigureAwait(true) per WorkflowFileScan's remarks.
            estimate = await Workflow.ExecuteLocalActivityAsync(
                (PiActivities a) => a.EstimatePi(input),
                BuildLocalActivityOptions(input.Activity)).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            var outcome = Classify(e);
            Workflow.Logger.LogWarning("workflow ending as {Outcome}: {Message}", outcome, e.Message);
            Record(meter, outcome, start);
            throw;
        }

        Workflow.Logger.LogInformation(
            "pi ~ {Pi} from {Iterations} samples in {ElapsedMs}ms of a requested {RequestedMs}ms " +
            "(attempt {Attempt}, isLocal {IsLocal}, endedBy {EndedBy})",
            estimate.Pi, estimate.Iterations, estimate.ElapsedMs, estimate.RequestedMs,
            estimate.Attempt, estimate.IsLocal, estimate.EndedBy);

        Record(meter, MetricNames.Outcomes.Completed, start);
        return estimate;
    }

    /// <summary>Map a local-activity failure onto exactly one outcome value.</summary>
    /// <remarks>
    /// IsCanceledException first, for the reason <see cref="HeartbeatWorkflow.Classify"/>
    /// records; a hand <c>temporal workflow cancel</c> reaches it. Both timeout types are matched
    /// even though StartToClose cannot fire and ScheduleToClose fires only under the documented
    /// mitigation, because matching one would put the mitigation's runs in `failed` and make the
    /// fix look like a bug. So `timed_out` is unreachable at the shipped config, where the
    /// server's own workflow_timeout counts those runs.
    /// </remarks>
    private static string Classify(Exception e)
    {
        if (TemporalException.IsCanceledException(e))
        {
            return MetricNames.Outcomes.Canceled;
        }

        if (e is ActivityFailureException
            {
                InnerException: TimeoutFailureException
                {
                    TimeoutType: TimeoutType.ScheduleToClose or TimeoutType.StartToClose,
                },
            })
        {
            return MetricNames.Outcomes.TimedOut;
        }

        return MetricNames.Outcomes.Failed;
    }

    /// <summary>One counter and one histogram, both split by outcome only.</summary>
    /// <remarks>No second dimension, unlike <see cref="WorkflowSimpleActivity"/>: the per-run
    /// numbers ride the returned payload. Neither metric accounts for every run.</remarks>
    private static void Record(MetricMeter meter, string outcome, DateTime start)
    {
        // See HeartbeatWorkflow.Record for the root tags already present.
        var tagged = meter.WithTags(new Dictionary<string, object>
        {
            [MetricNames.Tags.Outcome] = outcome,
        });

        tagged.CreateCounter<long>(MetricNames.LocalActivityCompleted).Add(1);

        // TimeSpan histogram, so the unit follows UseSecondsForDuration.
        tagged.CreateHistogram<TimeSpan>(MetricNames.LocalActivityLatency)
            .Record(Workflow.UtcNow - start);
    }
}
