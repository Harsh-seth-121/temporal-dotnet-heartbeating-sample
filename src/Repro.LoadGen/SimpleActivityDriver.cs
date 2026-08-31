using Microsoft.Extensions.Logging;
using Repro.Core;
using Repro.Core.Config;
using Repro.Core.Telemetry;
using Repro.Core.Workflows;
using Temporalio.Client;

namespace Repro.LoadGen;

/// <summary>
/// The third loadgen loop: starts WorkflowSimpleActivity runs on a JITTERED interval and
/// then does nothing to them.
/// </summary>
/// <remarks>
/// WHAT THIS CASE IS NOT is why it sits next to two busier drivers. No heartbeats, no
/// signals, no updates, no cancellation, no injected chaos. Just a plain StartToCloseTimeout
/// and a retry policy, which is what almost every real activity is and what this repo had no
/// example of. The only thing the driver watches is which SOURCE the weather reading came
/// from.
/// <para>
/// Everything in here is CLIENT code, so Random.Shared and wall-clock are fine. Nothing in
/// this file may leak into workflow code.
/// </para>
/// <para>
/// NO SemaphoreSlim, for the reason <see cref="SimpleDriver"/> records at length: the
/// heartbeat loop's <c>using var slots</c> is disposed while fire-and-forget run bodies are
/// still calling Release() in a finally. Interlocked counters have no disposal semantics at
/// all.
/// </para>
/// <para>
/// RETRIES ARE DELIBERATELY NOT COUNTED. A client cannot observe an activity attempt, and a
/// counter that looks like it can is worse than no counter. Per-attempt failures are
/// already in temporal_activity_execution_failed{activity_type="FetchWeather"}.
/// </para>
/// </remarks>
internal sealed class SimpleActivityDriver(
    ITemporalClient client,
    SimpleActivityConfig simpleActivity,
    string taskQueue,
    ILogger log)
{
    /// <summary>Bounds the StartWorkflowAsync RPC, so a wedged frontend cannot park the loop.</summary>
    /// <remarks>
    /// Not applied to GetResultAsync, which long-polls for the whole run by design. See
    /// <see cref="OneRunAsync"/>.
    /// </remarks>
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(10);

    private int inFlight;
    private int started;
    private int skipped;
    private int completed;
    private int live;
    private int synthetic;
    private int interrupted;
    private int failed;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Built ONCE, outside the loop: every run is identical, and projecting the config
        // here rather than inside the workflow is the point of SimpleActivityInput.From.
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

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Task.Delay, not PeriodicTimer: the period varies, which is the point. The
                // token is forwarded because CA2016 is an error in this repo, and because
                // without it a shutdown waits out a full interval.
                await Task.Delay(
                    Jitter.NextInterval(simpleActivity.Rate, simpleActivity.Jitter),
                    cancellationToken).ConfigureAwait(false);

                // SKIP at capacity, never queue. Same contract as the other two loops.
                if (Interlocked.Increment(ref inFlight) > simpleActivity.Concurrency)
                {
                    Interlocked.Decrement(ref inFlight);
                    Interlocked.Increment(ref skipped);
                    continue;
                }

                var n = Interlocked.Increment(ref started);

                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await OneRunAsync(input, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            // A TOTAL catch, or an unobserved TaskException tears down the
                            // process on finalization.
                            //
                            // Shutdown is counted SEPARATELY from failure, for the reason
                            // SimpleDriver records: a run whose RPCs were cancelled because
                            // the process is going down did not fail, and folding the two
                            // together makes every clean Ctrl-C look like breakage.
                            //
                            // Unlike SimpleDriver there is no per-outcome catch above this:
                            // a failed run here is a GENUINE failure, either exhausted
                            // retries or a non-retryable Open-Meteo response, and belongs in
                            // this bucket rather than being reclassified as an expected
                            // ending.
                            if (cancellationToken.IsCancellationRequested)
                            {
                                Interlocked.Increment(ref interrupted);
                            }
                            else
                            {
                                Interlocked.Increment(ref failed);
                                log.LogWarning("simple-activity run failed: {Message}", e.Message);
                            }
                        }
                        finally
                        {
                            Interlocked.Decrement(ref inFlight);
                        }
                    },
                    CancellationToken.None);

                if (n % 10 == 0)
                {
                    LogSummary();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the shutdown token cancelled Task.Delay.
        }

        LogSummary();
    }

    /// <summary>Start one run, wait for it, and record which source produced the reading.</summary>
    private async Task OneRunAsync(SimpleActivityInput input, CancellationToken cancellationToken)
    {
        var handle = await client.StartWorkflowAsync(
            (WorkflowSimpleActivity wf) => wf.RunAsync(input),

            // Guid.NewGuid is fine: client code. The prefix is deliberately NOT
            // "repro-simple-activity-", because "repro-simple-" is a string PREFIX of that,
            // so the first `WorkflowId STARTS_WITH "repro-simple-"` visibility query or
            // `grep repro-simple- .demo/loadgen.log` would silently merge the two cases and
            // report a count that is quietly too high. "repro-weather-" also names the
            // payload, which is what you want reading `temporal workflow list`.
            new WorkflowOptions(id: $"repro-weather-{Guid.NewGuid():N}", taskQueue: taskQueue)
            {
                Rpc = new RpcOptions { CancellationToken = cancellationToken, Timeout = RpcTimeout },
            }).ConfigureAwait(false);

        // NO Timeout here, unlike the start call: GetResultAsync long-polls for the whole
        // run, which is at least sleepDuration and can be several attempts of it. The token
        // still releases it at shutdown.
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

        // First completed run only. This is the line that proves a weather value made it
        // all the way back to the client, so it earns a log entry. Only once, though, and
        // gating on the counter avoids needing an atomic for a double.
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
    /// <remarks>
    /// The template is CONCATENATED STRING LITERALS, not interpolation: CA2254 requires a
    /// compile-time constant message and CA1727 requires PascalCase placeholders, and both
    /// are build errors here.
    /// <para>
    /// `synthetic` climbing while `failed` stays at zero is not a healthy board. It is a
    /// board with no network. That distinction is why both counters exist.
    /// </para>
    /// </remarks>
    private void LogSummary() =>
        log.LogInformation(
            "simple-activity: {Started} started, {Skipped} skipped at capacity | {Completed} " +
            "completed ({Live} live weather, {Synthetic} synthetic fallback) | {Interrupted} " +
            "interrupted by shutdown, {Failed} failed",
            started, skipped, completed, live, synthetic, interrupted, failed);
}
