using Microsoft.Extensions.Logging;
using Repro.Core.Activities;
using Repro.Core.Telemetry;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Repro.Core.Workflows;

/// <summary>One long, heartbeating, resumable activity over a real file, and a verified
/// aggregate in the history.</summary>
/// <remarks>
/// Read this against <see cref="HeartbeatWorkflow"/>, the same mechanism on a synthetic step
/// loop where nothing is genuinely reprocessed. The workflow is thin because everything
/// interesting is I/O, wall clock, memory and a checkpoint, and lives in
/// <see cref="FileScanActivities"/>. Its own task queue in the shared namespace costs a second
/// <c>TemporalWorker</c> and no second client; the reason is on
/// <see cref="Repro.Core.Config.FileScanConfig.TaskQueue"/>. See docs/WORKFLOWS.md.
/// <para>
/// Beyond the determinism rules <see cref="HeartbeatWorkflow"/> states, use
/// <c>ConfigureAwait(true)</c> on every await: CA2007 is not enabled here, so
/// <c>ConfigureAwait(false)</c> compiles clean and drops the continuation off the SDK's
/// deterministic scheduler.
/// </para>
/// </remarks>
[Workflow]
public class WorkflowFileScan
{
    /// <summary>The full ladder: three timeouts, a retry policy, and a cancellation type.</summary>
    /// <remarks>
    /// Every rung is derived, in <see cref="Repro.Core.Config.FileScanConfig"/> and
    /// <see cref="FileScanOptionsInput"/>. Do not re-derive them here, and do not tidy the
    /// fallback defaults, which are the contract with the histories in <c>history/</c>.
    /// <c>HeartbeatTimeout</c> is required twice over: it is the server's only channel for a
    /// cancellation, and it sets Core's throttle,
    /// <c>min(0.8 x this, worker.maxHeartbeatThrottleInterval)</c>, so it decides how stale the
    /// server's checkpoint can be. 24s x 6000 rows/s = 144,000 rows lost to a <c>kill -9</c>.
    /// <c>ScheduleToCloseTimeout</c> is set where <see cref="WorkflowSimpleActivity"/> leaves it
    /// unset, because an attempt here can be a half-hour scan and ten are allowed.
    /// <c>WaitCancellationCompleted</c> works here, unlike in
    /// <see cref="WorkflowSimpleActivity.BuildActivityOptions"/>, because this activity
    /// heartbeats every batch and can therefore be told.
    /// </remarks>
    internal static ActivityOptions BuildActivityOptions(FileScanOptionsInput? activity)
    {
        var a = activity ?? new FileScanOptionsInput();

        return new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromMilliseconds(a.StartToCloseTimeoutMs),
            ScheduleToCloseTimeout = TimeSpan.FromMilliseconds(a.ScheduleToCloseTimeoutMs),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(a.HeartbeatTimeoutMs),

            // RetryMaximumAttempts is 10 here rather than the repo's usual 5, and never 0:
            // zero means unlimited in Temporalio.Common.RetryPolicy. Each kill -9 spends one.
            RetryPolicy = a.ToRetryPolicy(),

            CancellationType = ActivityCancellationType.WaitCancellationCompleted,
        };
    }

    [WorkflowRun]
    public async Task<FileScanResult> RunAsync(FileScanInput input)
    {
        var start = Workflow.UtcNow;

        // Replay-suppressed, as in HeartbeatWorkflow.RunAsync, which is why the activity owns
        // most of this case's metrics: rows read is per-attempt and only countable there.
        var meter = Workflow.MetricMeter;

        FileScanResult result;
        try
        {
            result = await Workflow.ExecuteActivityAsync(
                (FileScanActivities a) => a.ScanFileAsync(input),
                BuildActivityOptions(input.Activity)).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            var outcome = Classify(e);
            Workflow.Logger.LogWarning("workflow ending as {Outcome}: {Message}", outcome, e.Message);
            Record(meter, outcome, start);
            throw;
        }

        // Verified is always true here: on a mismatch the activity throws non-retryable, so it
        // arrives in the catch above as `failed`. Returned anyway so the payload says so.
        Workflow.Logger.LogInformation(
            "scan verified={Verified}: {Rows} rows, {Bytes} bytes, indexSum {IndexSum}, "
            + "wordByteSum {WordByteSum}",
            result.Verified, result.Rows, result.Bytes, result.IndexSum, result.WordByteSum);

        Record(meter, MetricNames.Outcomes.Completed, start);
        return result;
    }

    /// <summary>Map an activity failure onto exactly one of the four outcome values.</summary>
    /// <remarks>IsCanceledException first, for the reason
    /// <see cref="HeartbeatWorkflow.Classify"/> records. Then any TimeoutType, unlike the other
    /// four: this workflow sets all three, so matching one would file the other two under
    /// `failed`. Everything else is `failed`, because an idempotency failure reported as
    /// `completed` is the one outcome this case exists to rule out.</remarks>
    private static string Classify(Exception e)
    {
        if (TemporalException.IsCanceledException(e))
        {
            return MetricNames.Outcomes.Canceled;
        }

        if (e is ActivityFailureException { InnerException: TimeoutFailureException })
        {
            return MetricNames.Outcomes.TimedOut;
        }

        return MetricNames.Outcomes.Failed;
    }

    /// <summary>One counter and one histogram, both split by outcome only.</summary>
    /// <remarks>Its own name pair rather than a fifth <c>workflow_type</c>, for the reason
    /// <see cref="MetricNames.SimpleCompleted"/> gives. No verdict tag: the activity owns
    /// <c>repro_file_scan_verified{result}</c>.</remarks>
    private static void Record(MetricMeter meter, string outcome, DateTime start)
    {
        // See HeartbeatWorkflow.Record for the root tags already present.
        var tagged = meter.WithTags(new Dictionary<string, object>
        {
            [MetricNames.Tags.Outcome] = outcome,
        });

        tagged.CreateCounter<long>(MetricNames.FileScanCompleted).Add(1);

        // TimeSpan histogram, so the unit follows UseSecondsForDuration.
        tagged.CreateHistogram<TimeSpan>(MetricNames.FileScanLatency)
            .Record(Workflow.UtcNow - start);
    }
}
