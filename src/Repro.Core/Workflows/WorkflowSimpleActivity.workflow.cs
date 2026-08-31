using Microsoft.Extensions.Logging;
using Repro.Core.Activities;
using Repro.Core.Telemetry;
using Temporalio.Api.Enums.V1;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Repro.Core.Workflows;

/// <summary>
/// ONE activity -> a result in the history.
/// </summary>
/// <remarks>
/// Read this against <see cref="HeartbeatWorkflow"/>, not instead of it. That one is the
/// heartbeating case; <c>SimpleNoActivity</c> has no activities at all. This is the shape
/// almost every real activity has, a plain start-to-close timeout and a retry policy, and the
/// repo had no example of it.
/// <para>
/// The activity sleeps, then fetches the current weather, and the reading is RETURNED, so
/// it lands in the WorkflowExecutionCompleted event and
/// <c>temporal workflow show -w &lt;id&gt;</c> prints the temperature in the payload.
/// </para>
/// <para>
/// WHY THE SLEEP IS IN THE ACTIVITY and not a workflow timer, in descending order of
/// importance. It is what makes this a long ACTIVITY: it occupies an activity slot,
/// produces a real temporal_activity_execution_latency, and gives
/// <c>startToCloseTimeout</c> something that can actually fire, where
/// Workflow.DelayAsync would write a TimerStarted/TimerFired pair, occupy nothing, and
/// leave an activity that returns in one HTTP round trip. Task.Delay in WORKFLOW code is
/// a determinism violation anyway, because its timer is a real system timer rather than
/// the workflow's. Forwarding the activity's own token into that sleep is what lets a worker
/// drain avoid waiting it out. And moving it later breaks every captured fixture, because the
/// command sequence changes shape.
/// </para>
/// <para>
/// Two determinism rules this obeys, same as the other two workflows: use
/// <c>Workflow.UtcNow</c>, never <c>DateTime.UtcNow</c>; and read nothing from config here.
/// The timeouts, the sleep and the coordinates all arrive in the input, and the endpoint is
/// reachable only through the activity object's constructor.
/// </para>
/// </remarks>
[Workflow]
public class WorkflowSimpleActivity
{
    /// <summary>Start-to-close plus a retry policy. That is the whole options object.</summary>
    /// <remarks>
    /// Know this before you try to cancel one of these. EVERYTHING
    /// <see cref="HeartbeatWorkflow"/> SETS AND THIS DOES NOT is the point of the file.
    /// <para>
    /// <c>HeartbeatTimeout</c> is unset, so the server has NO CHANNEL to tell a running
    /// activity it was cancelled: the only route is the response to a heartbeat RPC, and
    /// there are no heartbeats. <c>CancellationType</c> is therefore left at the SDK
    /// default, TryCancel: a client CancelAsync makes THIS WORKFLOW's await throw
    /// immediately, the run records CANCELED, and the activity keeps going with its result
    /// discarded. MEASURED: the workflow closed CANCELED at T+1s and the activity finished
    /// 5s later with a real reading nobody used. Setting WaitCancellationCompleted here, the
    /// value HeartbeatWorkflow deliberately pins, would turn a cancel into "wait out the rest
    /// of the activity", because the thing it waits for can never be delivered.
    /// </para>
    /// <para>
    /// "Keeps going" is bounded, not indefinite, and the bound is start-to-close rather than
    /// anything cancellation-related: the run ends timed_out, so a cancelled run's activity is
    /// discarded within one attempt's worth of start-to-close at the latest. The measurement
    /// is on <see cref="WeatherActivities.FetchWeatherAsync"/>.
    /// </para>
    /// <para>
    /// The activity DOES still observe worker shutdown, because
    /// <c>ctx.CancellationToken</c> is driven from the worker side rather than by the server:
    /// GracefulShutdownTimeout at minimum. That is why WeatherActivities forwards its token
    /// into Task.Delay.
    /// </para>
    /// <para>
    /// <c>ScheduleToCloseTimeout</c> is also unset on purpose. With a bounded
    /// maximumAttempts the retry policy already bounds the total, and adding a second
    /// ceiling would give two different timeouts that could fire, which is the opposite of
    /// what a minimal example should show.
    /// </para>
    /// <para>
    /// Options arrive in the input rather than from config.yaml, for the reason
    /// <see cref="SimpleActivityOptionsInput"/> records.
    /// </para>
    /// </remarks>
    internal static ActivityOptions BuildActivityOptions(SimpleActivityOptionsInput? activity)
    {
        var a = activity ?? new SimpleActivityOptionsInput();

        return new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromMilliseconds(a.StartToCloseTimeoutMs),

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
    public async Task<WeatherReading> RunAsync(SimpleActivityInput input)
    {
        var start = Workflow.UtcNow;

        // Replay-suppressed with no opt-out, same as the other two workflows: counts here
        // are "things that happened", not "things that were replayed".
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

            // No reading exists on this path, so the source tag is `none` rather than
            // absent. See MetricNames.Sources.
            Record(meter, outcome, MetricNames.Sources.None, start);
            throw;
        }

        // Empty only on a history captured before Source existed. See MetricNames.Sources.
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
    /// <remarks>
    /// Order matters, and IsCanceledException comes first for the reason
    /// <see cref="HeartbeatWorkflow"/> records: cancellation surfaces three different ways
    /// depending on where you await, and that helper is the only reliable way to recognise
    /// all of them.
    /// <para>
    /// STARTTOCLOSE, NOT HEARTBEAT. This is the mistake to expect here. Copying
    /// HeartbeatWorkflow.Classify verbatim would match TimeoutType.Heartbeat, which this
    /// workflow can NEVER produce because it sets no heartbeat timeout. Every exhausted
    /// start-to-close would then silently land in `failed`, and timed_out would never appear
    /// on the panel. Any other TimeoutType falling through to `failed` is honest: it would
    /// mean BuildActivityOptions grew a timeout this comment does not know about.
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
                InnerException: TimeoutFailureException { TimeoutType: TimeoutType.StartToClose },
            })
        {
            return MetricNames.Outcomes.TimedOut;
        }

        return MetricNames.Outcomes.Failed;
    }

    /// <summary>One counter split by outcome AND source, one histogram split by outcome only.</summary>
    /// <remarks>
    /// The source split is on the COUNTER because it answers a real question, namely whether
    /// this demo is reaching the internet, and because Prometheus aggregates away unselected
    /// labels, so `sum by (outcome) (rate(...))` still works unchanged.
    /// <para>
    /// It is NOT on the histogram, for the reason
    /// <see cref="MetricNames.SimpleActivityLatency"/> records at length.
    /// </para>
    /// <para>
    /// The source is derived from the ACTIVITY RESULT, meaning from history, not from a
    /// process global. Workflow.MetricMeter is replay-suppressed so that is already covered,
    /// but a value read from ambient state would let two workers disagree about the same run.
    /// </para>
    /// </remarks>
    private static void Record(MetricMeter meter, string outcome, string source, DateTime start)
    {
        // namespace / task_queue / workflow_type are already root tags on
        // Workflow.MetricMeter. Re-adding them would duplicate labels.
        var outcomeOnly = meter.WithTags(new Dictionary<string, object>
        {
            [MetricNames.Tags.Outcome] = outcome,
        });

        outcomeOnly.WithTags(new Dictionary<string, object>
        {
            [MetricNames.Tags.Source] = source,
        }).CreateCounter<long>(MetricNames.SimpleActivityCompleted).Add(1);

        // CreateHistogram<TimeSpan> maps to Core's HistogramDuration kind, so the value
        // follows UseSecondsForDuration automatically.
        outcomeOnly.CreateHistogram<TimeSpan>(MetricNames.SimpleActivityLatency)
            .Record(Workflow.UtcNow - start);
    }
}
