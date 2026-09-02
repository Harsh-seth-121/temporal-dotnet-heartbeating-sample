using Microsoft.Extensions.Logging;
using Repro.Core.Activities;
using Repro.Core.Telemetry;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Repro.Core.Workflows;

/// <summary>
/// ONE long, heartbeating, resumable activity over a real file -> a verified aggregate in the
/// history.
/// </summary>
/// <remarks>
/// The FIFTH case, and read it against <see cref="HeartbeatWorkflow"/> rather than instead of
/// it. That one is the heartbeat MECHANISM on a synthetic step loop, where nothing is
/// genuinely reprocessed because a <c>Task.Delay</c> step is idempotent by construction. This
/// one gives the mechanism something real to be right about: an exact byte cursor over a
/// generated corpus, and a closed-form aggregate that says out loud whether the resume was
/// idempotent.
/// <para>
/// The WORKFLOW is deliberately thin, and that is the point of the case rather than a
/// shortcoming. Everything interesting is in <see cref="FileScanActivities"/>, because
/// everything interesting is I/O, wall clock, memory and a checkpoint -- none of which may
/// live in workflow code. What is left here is the ladder, the cancellation type, the outcome
/// classification and one metric pair, which is exactly the surface a reader has to get right
/// to reproduce a real long-activity problem.
/// </para>
/// <para>
/// It runs on its OWN task queue, <c>fileScan.taskQueue</c>, in the same namespace as
/// everything else. <see cref="Repro.Core.Config.FileScanConfig.TaskQueue"/> carries the
/// reason: <c>temporal_worker_task_slots_used</c> has no <c>activity_type</c> label, and the
/// heartbeat board's headline stat sums it unfiltered while claiming this repo has exactly one
/// heartbeating activity type. Same namespace means no second client, only a second
/// <c>TemporalWorker</c>.
/// </para>
/// <para>
/// Three determinism rules this obeys, same as the other four workflows. Use
/// <c>Workflow.UtcNow</c>, never <c>DateTime.UtcNow</c>. Read nothing from config.yaml here --
/// the corpus path, the pace and the whole timeout ladder arrive in
/// <see cref="FileScanInput"/>, and the three fault knobs are reachable only through the
/// activity object's constructor, so there is no ambient global to reach for. And
/// <c>ConfigureAwait(TRUE)</c> on every await: CA2007 is NOT enabled in this repo, so
/// <c>ConfigureAwait(false)</c> compiles clean and silently drops the continuation off the
/// SDK's deterministic scheduler (<c>WorkflowLocalActivity.workflow.cs:147-149</c>).
/// </para>
/// </remarks>
[Workflow]
public class WorkflowFileScan
{
    /// <summary>The full ladder: three timeouts, a retry policy, and a cancellation type.</summary>
    /// <remarks>
    /// THE RICHEST OPTIONS OBJECT IN THE REPO, and every rung is derived rather than picked.
    /// The derivations live in <see cref="Repro.Core.Config.FileScanConfig"/> and
    /// <see cref="FileScanOptionsInput"/>; do not re-derive them here and do not "tidy" the
    /// fallback defaults, which are the contract with the histories in <c>history/</c>.
    /// <para>
    /// <c>HeartbeatTimeout</c> is REQUIRED for two independent reasons. It is the only channel
    /// the server has to deliver a cancellation -- the response to a heartbeat RPC -- so
    /// without it the <c>CancellationType</c> below would be unsatisfiable. And it is what
    /// sets Core's throttle, <c>min(0.8 x this, worker.maxHeartbeatThrottleInterval)</c>, and
    /// therefore how much work a <c>kill -9</c> destroys the RECORD of: 24s x 6000 rows/s =
    /// 144,000 rows, which is the drop the cursor panel draws.
    /// </para>
    /// <para>
    /// <c>ScheduleToCloseTimeout</c> is set here where
    /// <see cref="WorkflowSimpleActivity"/> deliberately leaves it unset, and the difference is
    /// resume. There a bounded <c>maximumAttempts</c> already bounds the total, so a second
    /// ceiling would only add a second timeout that could fire. Here each attempt can be a
    /// half-hour scan and ten of them are allowed, so the retry policy bounds nothing useful
    /// and this is the rung that keeps a wedged scan off the queue.
    /// </para>
    /// <para>
    /// <c>WaitCancellationCompleted</c>, the value <see cref="HeartbeatWorkflow"/> also pins.
    /// Without it the workflow reports cancelled the instant it asks, before the activity has
    /// observed anything, and the demo is hollow: the interesting thing IS the unwind -- the
    /// scan checkpointing on the drain edge, reading on, and stopping when
    /// <c>ctx.CancellationToken</c> fires. Unlike
    /// <see cref="WorkflowSimpleActivity.BuildActivityOptions"/>, where the same value would
    /// mean "wait out the rest of the activity" because the cancellation can never be
    /// delivered, here it can be, because this activity heartbeats every batch.
    /// </para>
    /// </remarks>
    internal static ActivityOptions BuildActivityOptions(FileScanOptionsInput? activity)
    {
        var a = activity ?? new FileScanOptionsInput();

        return new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromMilliseconds(a.StartToCloseTimeoutMs),
            ScheduleToCloseTimeout = TimeSpan.FromMilliseconds(a.ScheduleToCloseTimeoutMs),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(a.HeartbeatTimeoutMs),

            RetryPolicy = new RetryPolicy
            {
                InitialInterval = TimeSpan.FromMilliseconds(a.RetryInitialIntervalMs),

                // float, not double. Temporalio.Common.RetryPolicy takes a float and
                // config.yaml's 2.0 is parsed as a double.
                BackoffCoefficient = (float)a.RetryBackoffCoefficient,
                MaximumInterval = TimeSpan.FromMilliseconds(a.RetryMaximumIntervalMs),

                // 10 rather than the repo's usual 5, and never 0 -- zero means UNLIMITED in
                // Temporalio.Common.RetryPolicy. Each kill -9 spends one attempt and
                // docs/HEARTBEATING.md's recipe does three cycles.
                MaximumAttempts = a.RetryMaximumAttempts,
            },

            CancellationType = ActivityCancellationType.WaitCancellationCompleted,
        };
    }

    [WorkflowRun]
    public async Task<FileScanResult> RunAsync(FileScanInput input)
    {
        var start = Workflow.UtcNow;

        // Replay-suppressed with no opt-out, same as the other four workflows: counts here are
        // "things that happened", not "things that were replayed". Note the asymmetry that
        // suppression creates for this case, which is why the activity owns most of the
        // metrics: "how many scans finished" belongs here, while "how many rows were
        // physically read" is per-attempt and can only be counted from activity code.
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

        // Verified is always true on this path by construction: the activity increments
        // repro_file_scan_verified{result="mismatch"}, logs at Error and throws NON-RETRYABLE
        // rather than returning a wrong aggregate, so a mismatch arrives in the catch above as
        // `failed` and never as `completed`. It is logged and returned anyway so that
        // `temporal workflow show` STATES the verdict rather than implying it.
        Workflow.Logger.LogInformation(
            "scan verified={Verified}: {Rows} rows, {Bytes} bytes, indexSum {IndexSum}, "
            + "wordByteSum {WordByteSum}",
            result.Verified, result.Rows, result.Bytes, result.IndexSum, result.WordByteSum);

        Record(meter, MetricNames.Outcomes.Completed, start);
        return result;
    }

    /// <summary>Map an activity failure onto exactly one of the four outcome values.</summary>
    /// <remarks>
    /// Order matters, and IsCanceledException comes FIRST for the reason
    /// <see cref="HeartbeatWorkflow"/> records: cancellation surfaces as
    /// OperationCanceledException, CanceledFailureException, or nested inside an
    /// ActivityFailureException depending on where you await, and that helper is the only
    /// reliable way to recognise all three.
    /// <para>
    /// ANY TimeoutType, which is the one place this file departs from BOTH precedents and it
    /// departs from each for the opposite reason.
    /// <see cref="HeartbeatWorkflow.Classify"/> matches only <c>Heartbeat</c> and
    /// <see cref="WorkflowSimpleActivity.Classify"/> only <c>StartToClose</c>, because each of
    /// those workflows can produce exactly one. This one sets all THREE timeouts, so all three
    /// are reachable: <c>Heartbeat</c> from a read stalled on a hung mount, <c>StartToClose</c>
    /// from one attempt outliving the 30m rung, <c>ScheduleToClose</c> from the resumes
    /// outliving the 1h rung. Matching one of them would silently file the other two under
    /// `failed`, on a board whose whole job is to separate "the scan broke" from "the ladder
    /// fired".
    /// </para>
    /// <para>
    /// EVERYTHING ELSE IS `failed`, AND THAT IS LOAD-BEARING HERE. The activity's terminal
    /// throws -- a checkpoint that disagrees with itself, a corpus that changed, and above all
    /// an aggregate that does not match its closed form -- are non-retryable
    /// ApplicationFailureExceptions, and they must land in `failed`. An idempotency failure
    /// reported as `completed` is the single outcome this whole case exists to rule out.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// The FIFTH separate name pair rather than a fifth <c>workflow_type</c> on
    /// <c>repro_workflow_completed</c>, for the reason <see cref="MetricNames.SimpleCompleted"/>
    /// gives for the second: the panel titled "Custom: repro workflow outcomes /s" queries that
    /// metric as <c>sum by (outcome) (rate(...))</c> with NO workflow_type selector and STACKS
    /// the result, so a fifth type sharing the name would be summed into the heartbeat lines.
    /// <para>
    /// NO extra tag. The obvious candidate would be the verdict, and it does not belong here:
    /// the activity already owns <c>repro_file_scan_verified{result}</c>, which counts one per
    /// completed scan, and a mismatch never reaches this method's success path anyway.
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

        tagged.CreateCounter<long>(MetricNames.FileScanCompleted).Add(1);

        // CreateHistogram<TimeSpan> maps to Core's HistogramDuration kind, so the value follows
        // UseSecondsForDuration automatically. Recording a long of milliseconds by hand would
        // hard-code the unit and silently disagree with every built-in latency metric.
        tagged.CreateHistogram<TimeSpan>(MetricNames.FileScanLatency)
            .Record(Workflow.UtcNow - start);
    }
}
