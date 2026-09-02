using System.Diagnostics;
using Repro.Core;
using Repro.Core.Activities;
using Repro.Core.Telemetry;
using Temporalio.Testing;
using Xunit;

namespace Repro.Tests;

/// <summary>
/// The Monte Carlo burn, and the drain behaviour the whole case's teardown depends on.
/// </summary>
/// <remarks>
/// Every test runs inside <see cref="ActivityEnvironment"/>. <c>EstimatePi</c> reads
/// <c>ActivityExecutionContext.Current</c>, an AsyncLocal that throws outside an activity, so a
/// direct call fails before it does anything. Two traps in the harness silently weaken a test:
/// its MetricMeter is a no-op unless assigned, so no test may assert on
/// <c>repro_pi_attempt_started</c>, and its <c>DefaultInfo</c> has <c>IsLocal = false</c>.
/// Durations here are hundreds of milliseconds where the shipped config draws 30s to 2m.
/// </remarks>
public class PiActivitiesTests
{
    /// <summary>Long enough for the estimator to converge, short enough for a test suite.</summary>
    private const int ShortBurnMs = 250;

    /// <summary>Run one burn inside <paramref name="env"/> and hand back its estimate.</summary>
    /// <remarks>The one construction site for LocalActivityInput here. The environment is a
    /// parameter because two tests fire a token on it before the burn starts, and which token
    /// fires is the point of those tests.</remarks>
    private static Task<PiEstimate> BurnAsync(ActivityEnvironment env, int durationMs, int seed) =>
        env.RunAsync(() => new PiActivities().EstimatePi(
            new LocalActivityInput(DurationMs: durationMs, Seed: seed)));

    [Fact]
    public async Task EstimatesPiWithinToleranceOverAShortBurn()
    {
        var result = await BurnAsync(new ActivityEnvironment(), durationMs: ShortBurnMs, seed: 42);

        // Monte Carlo error falls as 1/sqrt(n), so a quarter second is good to three decimals.
        // The tolerance is far looser so a loaded CI box does not fail it.
        Assert.InRange(result.Pi, 3.0, 3.3);
        Assert.True(result.Iterations > 0, "a 250ms burn recorded no samples at all");
    }

    [Fact]
    public async Task ReportsIsLocalFromTheActivityContext()
    {
        // ActivityEnvironment.DefaultInfo has IsLocal = false, so asserting it on a plain burn
        // would assert the harness default.
        var env = new ActivityEnvironment
        {
            Info = ActivityEnvironment.DefaultInfo with { IsLocal = true },
        };

        var local = await BurnAsync(env, durationMs: ShortBurnMs, seed: 3);
        Assert.True(local.IsLocal, "PiEstimate.IsLocal did not follow ActivityInfo.IsLocal");

        // The negative half: without it a hardcoded true passes, which is plausible given the
        // activity is only ever registered as a local one.
        var plain = await BurnAsync(new ActivityEnvironment(), durationMs: ShortBurnMs, seed: 3);
        Assert.False(plain.IsLocal, "PiEstimate.IsLocal is not reading ActivityInfo.IsLocal at all");
    }

    [Fact]
    public async Task PiIsExactlyDerivableFromInsideAndIterations()
    {
        // Iterations and Inside are adjacent longs, so a positional swap compiles clean. The
        // identity does not hold under a swap, because Inside is always the smaller.
        var result = await BurnAsync(new ActivityEnvironment(), durationMs: ShortBurnMs, seed: 7);

        Assert.True(result.Inside <= result.Iterations, "more points landed inside than were sampled");
        Assert.Equal(4.0 * result.Inside / result.Iterations, result.Pi, 12);
    }

    [Fact]
    public async Task ReportsTheRequestedAndMeasuredDurationsSeparately()
    {
        var result = await BurnAsync(new ActivityEnvironment(), durationMs: ShortBurnMs, seed: 1);

        // RequestedMs and ElapsedMs are adjacent ints, so a positional swap would report the
        // duration asked for as the one measured, which is the number this case exists to show.
        Assert.Equal(ShortBurnMs, result.RequestedMs);

        // The loop checks its clock only on a batch boundary, so it always overshoots: it breaks
        // once GetElapsedTime >= budget and reads ElapsedMs from a strictly later timestamp, and
        // truncation to int can only discard the overshoot.
        Assert.True(
            result.ElapsedMs >= ShortBurnMs,
            $"burn ended at {result.ElapsedMs}ms, before the {ShortBurnMs}ms it was asked for");

        Assert.Equal(MetricNames.Endings.Completed, result.EndedBy);
    }

    [Fact]
    public async Task StopsEarlyOnWorkerShutdown()
    {
        // The test this file exists for. A local activity does not observe worker shutdown
        // through ActivityExecutionContext.CancellationToken: sdk-core applies the graceful
        // shutdown period only to server-scheduled activities, and local_activities.rs has no
        // cancel path on shutdown. WorkerShutdownToken is the one that fires. A regression shows
        // up as demo-down.sh SIGKILLing the worker, not as a red test.
        var env = new ActivityEnvironment();
        env.WorkerShutdownTokenSource.CancelAfter(TimeSpan.FromMilliseconds(150));

        var startedAt = Stopwatch.GetTimestamp();
        var result = await BurnAsync(env, durationMs: 60_000, seed: 3);
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        Assert.Equal(MetricNames.Endings.Shutdown, result.EndedBy);

        // The only place the RequestedMs/ElapsedMs swap is catchable. The loop overshoots by one
        // batch, under a millisecond at ~93M iterations/s, so the two are equal on any healthy
        // completing run and a swap passes every assertion in
        // ReportsTheRequestedAndMeasuredDurationsSeparately.
        Assert.Equal(60_000, result.RequestedMs);
        Assert.True(
            result.ElapsedMs < result.RequestedMs / 2,
            $"ElapsedMs {result.ElapsedMs} is not meaningfully below the requested " +
            $"{result.RequestedMs}; if these two are equal on a burn that was cut short, they " +
            "are probably the same value and PiEstimate was constructed positionally");

        // Generous by two orders of magnitude: the claim is "it noticed and stopped", and the
        // loop only checks on a batch boundary.
        Assert.True(
            elapsed < TimeSpan.FromSeconds(10),
            $"a 60s burn took {elapsed.TotalSeconds:F1}s to notice worker shutdown");

        // Still a usable estimate: the result is returned rather than thrown.
        Assert.True(result.Iterations > 0, "a burn cut short by a drain recorded no samples at all");
        Assert.InRange(result.Pi, 3.0, 3.3);
    }

    [Fact]
    public async Task StopsEarlyOnActivityCancellation()
    {
        // The other token, and the busy one in production: every burn cut short in a demo run
        // was this firing at ~64s against a 1m workflow task heartbeat timeout, the workflow task
        // timing out underneath the activity. It must report canceled and not shutdown; folding
        // the two into one check made the activity blame a drain that never happened.
        var env = new ActivityEnvironment();
        env.CancellationTokenSource.CancelAfter(TimeSpan.FromMilliseconds(150));

        var result = await BurnAsync(env, durationMs: 60_000, seed: 4);

        Assert.Equal(MetricNames.Endings.Canceled, result.EndedBy);
        Assert.NotEqual(MetricNames.Endings.Shutdown, result.EndedBy);

        // The same generous bound as the shutdown test.
        Assert.True(
            result.ElapsedMs < 10_000,
            $"a 60s burn ran {result.ElapsedMs}ms before it noticed activity cancellation");
    }

    [Fact]
    public async Task ThroughputIsConsistentWithIterationsAndElapsed()
    {
        var result = await BurnAsync(new ActivityEnvironment(), durationMs: ShortBurnMs, seed: 5);

        // IterationsPerSecond is the third adjacent long, and any of the three looks plausible
        // alone, so this is the only thing that catches a construction from the wrong one.
        var expected = (long)(result.Iterations / TimeSpan.FromMilliseconds(result.ElapsedMs).TotalSeconds);

        // Within 1%, not exact: ElapsedMs is the truncated copy of the TimeSpan the activity
        // divided by, so the two disagree by up to a millisecond of rounding.
        Assert.InRange(result.IterationsPerSecond, (long)(expected * 0.99), (long)(expected * 1.01));
    }
}
