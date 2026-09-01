using Microsoft.Extensions.Logging;
using Repro.Core.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;
using Repro.Core.Telemetry;

namespace Repro.Core.Workflows;

/// <summary>
/// ONE LOCAL activity -> a marker in the history, and usually a workflow task timeout.
/// </summary>
/// <remarks>
/// Read this against <see cref="WorkflowSimpleActivity"/>. That one is the ordinary activity;
/// this is the same idea moved onto the local-activity path, and almost nothing survives the
/// move.
/// <para>
/// WHAT A LOCAL ACTIVITY ACTUALLY IS: it executes INSIDE the workflow task rather than as a
/// separately scheduled activity task. It writes a <c>MarkerRecorded</c> event (marker name
/// <c>core_local_activity</c>) instead of ScheduleActivityTask/Started/Completed, it holds a
/// LocalActivityWorker slot rather than an activity slot, and it is invisible to every
/// server-side activity metric because the server sees only an undifferentiated RecordMarker.
/// </para>
/// <para>
/// THE FAILURE THIS FILE EXISTS FOR. Because the local activity runs inside the workflow task,
/// that task stays open for the whole burn, and the SDK keeps it alive by sending workflow task
/// heartbeats. The server allows that only up to <c>history.workflowTaskHeartbeatTimeout</c>,
/// which this stack drops from its 30m default to 1m in this workflow's own namespace. Past
/// that the server times the task out and reschedules it -- and since a local activity's result
/// is not written to history until it completes, THE BURN STARTS AGAIN FROM ZERO. The duration
/// lives in the workflow input, so the retry takes just as long and times out again.
/// </para>
/// <para>
/// NOTHING IN THE OPTIONS OBJECT STOPS THAT, which is the part worth reading
/// <see cref="BuildLocalActivityOptions"/> for. The loop ends at
/// <c>WorkflowOptions.RunTimeout</c>, set by the driver, enforced by the server's timer queue.
/// </para>
/// <para>
/// AND THE RUN THAT ENDS THAT WAY RECORDS NOTHING HERE. The server closes a run-timed-out
/// workflow by calling TimeoutWorkflow directly, without scheduling a workflow task, so
/// <see cref="RunAsync"/> never resumes and neither <see cref="Record"/> call happens. At the
/// shipped draw that is two-thirds of runs. It is why <c>repro_pi_attempt_started</c>, emitted
/// from activity code, is the primary signal for this case and this workflow's counter is the
/// supporting one.
/// </para>
/// <para>
/// Two determinism rules this obeys, same as the other three: <c>Workflow.UtcNow</c>, never
/// <c>DateTime.UtcNow</c>; and nothing is read from config here. The duration, the seed and the
/// timeouts all arrive in the input.
/// </para>
/// </remarks>
[Workflow]
public class WorkflowLocalActivity
{
    /// <summary>Two timeouts and a retry policy, none of which bound the re-execution loop.</summary>
    /// <remarks>
    /// EVERY RUNG HERE IS EITHER UNREACHABLE OR DOES NOT DO WHAT ITS NAME SUGGESTS at the
    /// shipped config, and that is the lesson rather than an accident of tuning.
    /// <para>
    /// There is no <c>HeartbeatTimeout</c> to set. <c>LocalActivityOptions</c> does not have
    /// one -- not unset, absent from the type -- which is the structural reason this repo's
    /// whole heartbeat apparatus does not apply here. The SDK says it plainly: "Heartbeating
    /// has no effect on local activities."
    /// </para>
    /// <para>
    /// <c>StartToCloseTimeout</c> is DELIBERATELY UNREACHABLE. One of the two timeouts must be
    /// set or the call throws, and this is the one that is set. It cannot fire: the burn is
    /// capped at <c>localActivity.maxDuration</c> and the server kills the workflow task at the
    /// heartbeat timeout first. It is documented as unreachable rather than described as a
    /// guard, which is the standard <see cref="WorkflowSimpleActivity"/> sets for a rung that
    /// cannot fire.
    /// </para>
    /// <para>
    /// <c>ScheduleToCloseTimeout</c> DOES NOT ACCUMULATE ACROSS RE-EXECUTIONS, which is the
    /// single most counter-intuitive fact in this case and the one most likely to be
    /// "corrected" by a future reader. Its clock restarts on every workflow-task re-dispatch:
    /// sdk-core does <c>original_schedule_time.get_or_insert(SystemTime::now())</c> on each
    /// fresh schedule and persists that value only inside the MARKER, guarded by
    /// <c>if record_marker</c> -- and a local activity killed by a workflow task timeout never
    /// resolved, so no marker was written. Eviction then sends <c>InvalidateRun</c> and
    /// <c>Drop for TimeoutBag</c> aborts the schedule-to-close handle. The proto field that
    /// carries a previous clock forward, <c>original_schedule_time</c>, travels only through
    /// <c>DoBackoff</c>, i.e. timer-based retry backoff, which is a different path.
    /// </para>
    /// <para>
    /// SET IT BELOW THE HEARTBEAT TIMEOUT and this case becomes its own documented fix: the
    /// local activity fails with a TimeoutFailure the workflow catches, the run records
    /// <c>timed_out</c>, and the workflow task is never re-executed at all. That is the only
    /// regime in which the rung fires, it is why <see cref="Classify"/> matches
    /// ScheduleToClose, and it is why ConfigLoader does not order this field against
    /// startToCloseTimeout.
    /// </para>
    /// <para>
    /// <c>RetryPolicy</c> must be set at all, and that is a stronger requirement than on the
    /// regular path: unset means retry FOREVER here. It still does not bound the loop, because
    /// a workflow-task-timeout re-execution is not a retry -- it arrives as attempt 1 again.
    /// </para>
    /// <para>
    /// <c>CancellationType</c> is left at the SDK default, <c>TryCancel</c>. Worth knowing that
    /// the .NET default disagrees with sdk-core's own guidance: the shipped
    /// <c>ScheduleLocalActivity.CancellationType</c> comment says "Lang should default this to
    /// WAIT_CANCELLATION_COMPLETED". Left alone so this case shows the default a reader would
    /// actually get.
    /// </para>
    /// </remarks>
    internal static LocalActivityOptions BuildLocalActivityOptions(LocalActivityOptionsInput? activity)
    {
        var a = activity ?? new LocalActivityOptionsInput();

        return new LocalActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromMilliseconds(a.StartToCloseTimeoutMs),
            ScheduleToCloseTimeout = TimeSpan.FromMilliseconds(a.ScheduleToCloseTimeoutMs),

            RetryPolicy = new RetryPolicy
            {
                InitialInterval = TimeSpan.FromMilliseconds(a.RetryInitialIntervalMs),

                // float, not double. Temporalio.Common.RetryPolicy takes a float and
                // config.yaml's 2.0 is parsed as a double.
                BackoffCoefficient = (float)a.RetryBackoffCoefficient,
                MaximumInterval = TimeSpan.FromMilliseconds(a.RetryMaximumIntervalMs),
                MaximumAttempts = a.RetryMaximumAttempts,
            },
        };
    }

    [WorkflowRun]
    public async Task<PiEstimate> RunAsync(LocalActivityInput input)
    {
        var start = Workflow.UtcNow;

        // Replay-suppressed with no opt-out, same as the other three workflows. Note the
        // asymmetry this case introduces: that suppression is right for "how many runs
        // finished" and WRONG for "how many times did the burn actually execute", which is why
        // the latter is counted from activity code instead.
        var meter = Workflow.MetricMeter;

        PiEstimate estimate;
        try
        {
            // ExecuteLocalActivityAsync, not ExecuteActivityAsync. The difference is the entire
            // file: no activity task is scheduled, no activity slot is taken, and the result
            // lands in a marker rather than in an ActivityTaskCompleted event.
            //
            // ConfigureAwait(TRUE), like every await in every workflow here. CA2007 is NOT
            // enabled in this repo, so ConfigureAwait(false) on this line would compile clean
            // and silently drop the continuation off the SDK's deterministic scheduler.
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
            "pi ~ {Pi} from {Iterations} samples in {ElapsedMs}ms of a requested {RequestedMs}ms "
            + "(attempt {Attempt}, isLocal {IsLocal}, endedBy {EndedBy})",
            estimate.Pi, estimate.Iterations, estimate.ElapsedMs, estimate.RequestedMs,
            estimate.Attempt, estimate.IsLocal, estimate.EndedBy);

        Record(meter, MetricNames.Outcomes.Completed, start);
        return estimate;
    }

    /// <summary>Map a local-activity failure onto exactly one outcome value.</summary>
    /// <remarks>
    /// IsCanceledException first, for the reason <see cref="HeartbeatWorkflow"/> records:
    /// cancellation surfaces three different ways depending on where you await, and that helper
    /// is the only reliable way to recognise all of them. It is reachable here by a hand
    /// <c>temporal workflow cancel</c>: LocalActivityOptions.CancellationToken defaults to
    /// Workflow.CancellationToken, and CancellationType defaults to TryCancel, so the await
    /// throws immediately while the burn keeps running with its result discarded.
    /// <para>
    /// SCHEDULETOCLOSE, NOT STARTTOCLOSE, is the one to expect, which is the opposite of
    /// <see cref="WorkflowSimpleActivity.Classify"/> and the reason both are matched here.
    /// Start-to-close cannot fire in this case at all (see
    /// <see cref="BuildLocalActivityOptions"/>), while schedule-to-close fires whenever
    /// <c>localActivity.scheduleToCloseTimeout</c> is set below the workflow task heartbeat
    /// timeout -- the documented mitigation. Matching only one of them would put the
    /// mitigation's runs in `failed` and make the fix look like a bug.
    /// </para>
    /// <para>
    /// TIMED_OUT IS UNREACHABLE AT THE SHIPPED CONFIG, and not because of this method. With
    /// scheduleToCloseTimeout above the heartbeat timeout, a long run is ended by
    /// WorkflowOptions.RunTimeout, which the server applies WITHOUT scheduling a workflow task,
    /// so nothing here runs and no outcome is recorded at all. This arm is live code for the
    /// mitigation config and dead for the repro config; the count of repro-config timeouts
    /// comes from the server's own workflow_timeout instead.
    /// </para>
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
    /// <remarks>
    /// No <c>source</c>-style second dimension, unlike
    /// <see cref="WorkflowSimpleActivity"/>: there is no second question to ask of a run that
    /// completed. The interesting per-run numbers -- iterations, elapsed against requested,
    /// iterations per second -- are in the returned payload, where `temporal workflow show`
    /// prints them, rather than in a metric.
    /// <para>
    /// NEITHER OF THESE ACCOUNTS FOR EVERY RUN, which is the difference a reader has to carry
    /// away from this class. See the class remarks.
    /// </para>
    /// </remarks>
    private static void Record(MetricMeter meter, string outcome, DateTime start)
    {
        // namespace / task_queue / workflow_type are already root tags on
        // Workflow.MetricMeter. Re-adding them would duplicate labels.
        var tagged = meter.WithTags(new Dictionary<string, object>
        {
            [MetricNames.Tags.Outcome] = outcome,
        });

        tagged.CreateCounter<long>(MetricNames.LocalActivityCompleted).Add(1);

        // CreateHistogram<TimeSpan> maps to Core's HistogramDuration kind, so the value
        // follows UseSecondsForDuration automatically.
        tagged.CreateHistogram<TimeSpan>(MetricNames.LocalActivityLatency)
            .Record(Workflow.UtcNow - start);
    }
}
