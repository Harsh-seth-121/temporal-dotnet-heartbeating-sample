using Microsoft.Extensions.Logging;
using Repro.Core.Activities;
using Repro.Core.Telemetry;
using Temporalio.Api.Enums.V1;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Repro.Core.Workflows;

/// <summary>One ordinary activity, and its result in the history.</summary>
/// <remarks>
/// Read this against <see cref="HeartbeatWorkflow"/>, the heartbeating case, whose determinism
/// rules it obeys. This is the shape almost every real activity has: a start-to-close timeout
/// and a retry policy. The sleep lives in the activity rather than a <c>Workflow.DelayAsync</c>
/// timer, which is what makes this a long activity: it holds an activity slot, produces a real
/// temporal_activity_execution_latency, and gives <c>startToCloseTimeout</c> something that can
/// fire. Moving it also breaks every captured fixture. See docs/WORKFLOWS.md.
/// </remarks>
[Workflow]
public class WorkflowSimpleActivity
{
    /// <summary>Start-to-close plus a retry policy. That is the whole options object.</summary>
    /// <remarks>
    /// What <see cref="HeartbeatWorkflow"/> sets and this does not is the point of the file.
    /// <c>HeartbeatTimeout</c> is unset, so the server has no channel to tell a running activity
    /// it was cancelled and <c>CancellationType</c> stays at the SDK default, TryCancel: a client
    /// CancelAsync throws out of this workflow's await, the run records CANCELED, and the
    /// activity runs on with its result discarded. Measured: workflow closed at T+1s, activity
    /// finished at T+6s. WaitCancellationCompleted here would mean "wait out the activity".
    /// Worker shutdown still reaches it, through <c>ctx.CancellationToken</c>.
    /// <c>ScheduleToCloseTimeout</c> is unset because maximumAttempts already bounds the total.
    /// </remarks>
    internal static ActivityOptions BuildActivityOptions(SimpleActivityOptionsInput? activity)
    {
        var a = activity ?? new SimpleActivityOptionsInput();

        return new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromMilliseconds(a.StartToCloseTimeoutMs),

            RetryPolicy = a.ToRetryPolicy(),
        };
    }

    [WorkflowRun]
    public async Task<WeatherReading> RunAsync(SimpleActivityInput input)
    {
        var start = Workflow.UtcNow;

        // Replay-suppressed, as in HeartbeatWorkflow.RunAsync.
        var meter = Workflow.MetricMeter;

        WeatherReading reading;
        try
        {
            reading = await Workflow.ExecuteActivityAsync(
                (WeatherActivities a) => a.FetchWeatherAsync(input),
                BuildActivityOptions(input.Activity)).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            var outcome = Classify(e);
            Workflow.Logger.LogWarning("workflow ending as {Outcome}: {Message}", outcome, e.Message);

            // No reading on this path, so source is `none` rather than absent.
            Record(meter, outcome, MetricNames.Sources.None, start);
            throw;
        }

        // Empty only on a history captured before Source existed.
        var source = string.IsNullOrEmpty(reading.Source) ? MetricNames.Sources.None : reading.Source;

        Workflow.Logger.LogInformation(
            "weather from {Source}: {Temperature}{TemperatureUnit}, wind {WindSpeed}{WindSpeedUnit} " +
            "at {ObservedAt}",
            source, reading.TemperatureCelsius, reading.TemperatureUnit,
            reading.WindSpeedKmh, reading.WindSpeedUnit, reading.ObservedAt);

        Record(meter, MetricNames.Outcomes.Completed, source, start);
        return reading;
    }

    /// <summary>Map an activity failure onto exactly one of the four outcome values.</summary>
    /// <remarks>IsCanceledException first, for the reason
    /// <see cref="HeartbeatWorkflow.Classify"/> records. Then StartToClose, not Heartbeat: this
    /// workflow sets no heartbeat timeout, so copying HeartbeatWorkflow.Classify verbatim would
    /// file every exhausted start-to-close under `failed`.</remarks>
    private static string Classify(Exception e)
    {
        if (TemporalException.IsCanceledException(e))
        {
            return MetricNames.Outcomes.Canceled;
        }

        if (e is ActivityFailureException
            {
                InnerException: TimeoutFailureException { TimeoutType: TimeoutType.StartToClose },
            })
        {
            return MetricNames.Outcomes.TimedOut;
        }

        return MetricNames.Outcomes.Failed;
    }

    /// <summary>One counter split by outcome and source, one histogram split by outcome.</summary>
    /// <remarks>The source split rides the counter, not the histogram: Prometheus aggregates
    /// away unselected labels, so `sum by (outcome) (rate(...))` still works, and the histogram
    /// is left alone for the reason <see cref="MetricNames.SimpleActivityLatency"/> gives.
    /// Source comes from the activity result, so from history rather than process state.
    /// </remarks>
    private static void Record(MetricMeter meter, string outcome, string source, DateTime start)
    {
        // See HeartbeatWorkflow.Record for the root tags already present.
        var outcomeOnly = meter.WithTags(new Dictionary<string, object>
        {
            [MetricNames.Tags.Outcome] = outcome,
        });

        outcomeOnly.WithTags(new Dictionary<string, object>
        {
            [MetricNames.Tags.Source] = source,
        }).CreateCounter<long>(MetricNames.SimpleActivityCompleted).Add(1);

        // TimeSpan histogram, so the unit follows UseSecondsForDuration.
        outcomeOnly.CreateHistogram<TimeSpan>(MetricNames.SimpleActivityLatency)
            .Record(Workflow.UtcNow - start);
    }
}
