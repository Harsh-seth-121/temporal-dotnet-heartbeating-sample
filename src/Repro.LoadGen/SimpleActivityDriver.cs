using Microsoft.Extensions.Logging;
using Repro.Core;
using Repro.Core.Config;
using Repro.Core.Telemetry;
using Repro.Core.Workflows;
using Temporalio.Client;

namespace Repro.LoadGen;

/// <summary>
/// The third loadgen loop: starts WorkflowSimpleActivity runs on a jittered interval and then
/// does nothing to them.
/// </summary>
/// <remarks>
/// Pacing and the shared counters come from <see cref="DriverLoop{TRun}"/>. What this case lacks
/// is why it exists: no heartbeats, signals, updates, cancellation or injected chaos, just a
/// StartToCloseTimeout and a retry policy. Retries are not counted, because a client cannot
/// observe an activity attempt; per-attempt failures are already in
/// temporal_activity_execution_failed{activity_type="FetchWeather"}.
/// </remarks>
internal sealed class SimpleActivityDriver(
    ITemporalClient client,
    SimpleActivityConfig simpleActivity,
    string taskQueue,
    ILogger log)
{
    /// <summary>Bounds the StartWorkflowAsync RPC, so a wedged frontend cannot park the loop.</summary>
    /// <remarks>Not applied to GetResultAsync; see <see cref="OneRunAsync"/>.</remarks>
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The shared tick loop and its started/skipped/interrupted/failed counters.</summary>
    private readonly DriverLoop<SimpleActivityInput> loop =
        new(simpleActivity.Rate, simpleActivity.Jitter, simpleActivity.Concurrency);

    private int completed;
    private int live;
    private int synthetic;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Built once: every run is identical, and projecting the config here rather than in the
        // workflow is the point of SimpleActivityInput.From.
        var input = SimpleActivityInput.From(simpleActivity);

        log.LogInformation(
            "simple-activity: 1 workflow every {Rate} +/-{JitterPercent}%, up to {Concurrency} in " +
            "flight, activity sleeps {Sleep} then fetches {Latitude},{Longitude} within " +
            "{HttpTimeout}, startToClose {StartToClose}, up to {MaxAttempts} attempts, " +
            "unreachable-endpoint fallback {Fallback}",
            GoDuration.ToGoString(simpleActivity.Rate), (int)(simpleActivity.Jitter * 100),
            simpleActivity.Concurrency, GoDuration.ToGoString(simpleActivity.SleepDuration),
            simpleActivity.Latitude, simpleActivity.Longitude,
            GoDuration.ToGoString(simpleActivity.HttpTimeout),
            GoDuration.ToGoString(simpleActivity.StartToCloseTimeout),
            simpleActivity.Retry.MaximumAttempts,
            simpleActivity.RequireLiveWeather ? "OFF (requireLiveWeather)" : "synthetic");

        // Every run takes the same prebuilt input, so the draw is a constant.
        await loop.RunAsync(
            () => input,
            OneRunAsync,

            // No per-outcome catch above this, unlike SimpleDriver: a failed run here is
            // genuine, either exhausted retries or a non-retryable Open-Meteo response.
            (_, e) => log.LogWarning("simple-activity run failed: {Message}", e.Message),
            LogSummary,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Start one run, wait for it, and record which source produced the reading.</summary>
    private async Task OneRunAsync(SimpleActivityInput input, CancellationToken cancellationToken)
    {
        var handle = await client.StartWorkflowAsync(
            (WorkflowSimpleActivity wf) => wf.RunAsync(input),

            // Not "repro-simple-activity-": "repro-simple-" is a string prefix of it, so a
            // `WorkflowId STARTS_WITH` query or a grep would merge the two cases and report a
            // count quietly too high. Every id prefix this repo generates is checked
            // prefix-disjoint against the others for that reason.
            new WorkflowOptions(id: $"repro-weather-{Guid.NewGuid():N}", taskQueue: taskQueue)
            {
                Rpc = new RpcOptions { CancellationToken = cancellationToken, Timeout = RpcTimeout },
            }).ConfigureAwait(false);

        // No Timeout, unlike the start call: GetResultAsync long-polls for the whole run.
        var reading = await handle.GetResultAsync(
            rpcOptions: new RpcOptions { CancellationToken = cancellationToken }).ConfigureAwait(false);

        if (string.Equals(reading.Source, MetricNames.Sources.Synthetic, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref synthetic);
        }
        else
        {
            Interlocked.Increment(ref live);
        }

        // First completed run only. Gating on the counter avoids needing an atomic for a double.
        if (Interlocked.Increment(ref completed) == 1)
        {
            log.LogInformation(
                "simple-activity: first run returned {Temperature}{TemperatureUnit} from {Source} " +
                "for {Latitude},{Longitude} in {ElapsedMs}ms",
                reading.TemperatureCelsius, reading.TemperatureUnit, reading.Source,
                simpleActivity.Latitude, simpleActivity.Longitude, reading.HttpElapsedMs);
        }
    }

    /// <summary>One line, every ten starts and once at shutdown.</summary>
    /// <remarks>`synthetic` climbing while `failed` stays at zero is not a healthy board, it is
    /// a board with no network. That distinction is why both counters exist.</remarks>
    private void LogSummary() =>
        log.LogInformation(
            "simple-activity: {Started} started, {Skipped} skipped at capacity | {Completed} " +
            "completed ({Live} live weather, {Synthetic} synthetic fallback) | {Interrupted} " +
            "interrupted by shutdown, {Failed} failed",
            loop.Started, loop.Skipped, completed, live, synthetic, loop.Interrupted, loop.Failed);
}
