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
/// EVERY TEST GOES THROUGH <see cref="ActivityEnvironment"/>, and it is not optional.
/// <c>EstimatePi</c> reads <c>ActivityExecutionContext.Current</c>, which is an AsyncLocal
/// that throws <c>InvalidOperationException</c> outside an activity, so calling the method
/// directly from a test fails before it does anything. This is the first test in this repo to
/// need that harness: WeatherActivitiesTests sidesteps it by only exercising the pure static
/// <c>IsTransportFailure</c>.
/// <para>
/// Two traps in the harness itself, both of which silently weaken a test rather than failing
/// it. Its MetricMeter is a NO-OP unless assigned, so <c>repro_pi_attempt_started</c> records
/// nothing here and no test may assert on it. And its <c>DefaultInfo</c> has
/// <c>IsLocal = false</c>, so a test that wants the local-activity shape has to say so.
/// </para>
/// <para>
/// Durations are in the low hundreds of milliseconds. The shipped config draws 30s-2m; a test
/// that used those numbers would be a five-minute suite proving arithmetic that a quarter of a
/// second proves just as well.
/// </para>
/// </remarks>
public class PiActivitiesTests
{
    /// <summary>Long enough for the estimator to converge, short enough for a test suite.</summary>
    private const int ShortBurnMs = 250;

    [Fact]
    public async Task EstimatesPiWithinToleranceOverAShortBurn()
    {
        var result = await new ActivityEnvironment().RunAsync(
            () => new PiActivities().EstimatePi(new LocalActivityInput(DurationMs: ShortBurnMs, Seed: 42)));

        // Monte Carlo error falls as 1/sqrt(n), and a quarter second is millions of samples,
        // so the true error is around three decimal places. The tolerance is deliberately far
        // looser: this asserts "the estimator is not broken", not "the machine is fast". A
        // tight bound here would fail on a loaded CI box for reasons that have nothing to do
        // with the code.
        Assert.InRange(result.Pi, 3.0, 3.3);
        Assert.True(result.Iterations > 0, "a 250ms burn recorded no samples at all");
    }

    [Fact]
    public async Task PiIsExactlyDerivableFromInsideAndIterations()
    {
        // The one invariant that catches a swapped NAMED argument in the PiEstimate
        // construction, which is the failure that record's remarks warn about. Iterations and
        // Inside are adjacent longs, so swapping them positionally compiles clean; if they
        // were swapped this identity would not hold, because Inside is always the smaller.
        var result = await new ActivityEnvironment().RunAsync(
            () => new PiActivities().EstimatePi(new LocalActivityInput(DurationMs: ShortBurnMs, Seed: 7)));

        Assert.True(result.Inside <= result.Iterations, "more points landed inside than were sampled");
        Assert.Equal(4.0 * result.Inside / result.Iterations, result.Pi, 12);
    }

    [Fact]
    public async Task ReportsTheRequestedAndMeasuredDurationsSeparately()
    {
        var result = await new ActivityEnvironment().RunAsync(
            () => new PiActivities().EstimatePi(new LocalActivityInput(DurationMs: ShortBurnMs, Seed: 1)));

        // RequestedMs and ElapsedMs are ADJACENT ints in PiEstimate, so a positional swap
        // would report the duration that was asked for as the one that was measured. That is
        // the exact number this whole case exists to show, and it would compile clean.
        Assert.Equal(ShortBurnMs, result.RequestedMs);

        // The loop only checks its clock on a batch boundary, so it always overshoots slightly
        // and can never undershoot. Asserting >= rather than a window keeps this from being a
        // machine-speed test.
        Assert.True(
            result.ElapsedMs >= ShortBurnMs - 1,
            $"burn ended at {result.ElapsedMs}ms, before the {ShortBurnMs}ms it was asked for");

        Assert.Equal(MetricNames.Endings.Completed, result.EndedBy);
    }

    [Fact]
    public async Task StopsEarlyOnWorkerShutdown()
    {
        // THE TEST THIS FILE EXISTS FOR. A local activity does not observe worker shutdown
        // through ActivityExecutionContext.CancellationToken: in sdk-core the graceful
        // shutdown period is applied only to server-scheduled activities, and
        // local_activities.rs has no cancel path on shutdown at all. WorkerShutdownToken is
        // the one that fires.
        //
        // If this ever regresses, the symptom is not a red test in CI. It is demo-down.sh
        // SIGKILLing the worker, because its budget is gracefulShutdownTimeout + 15 = 45s and
        // this activity is configured to run for up to two minutes.
        var env = new ActivityEnvironment();
        env.WorkerShutdownTokenSource.CancelAfter(TimeSpan.FromMilliseconds(150));

        var startedAt = Stopwatch.GetTimestamp();
        var result = await env.RunAsync(
            () => new PiActivities().EstimatePi(new LocalActivityInput(DurationMs: 60_000, Seed: 3)));
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        Assert.Equal(MetricNames.Endings.Shutdown, result.EndedBy);

        // THE ONLY PLACE the RequestedMs/ElapsedMs swap is actually catchable, which is why
        // this assertion lives in the shutdown test rather than next to the other one.
        //
        // MEASURED against a real run: a 40,124ms burn reported ElapsedMs 40124 exactly. The
        // loop only overshoots by one batch, and at the ~93M iterations/s this machine manages
        // that batch is under a millisecond, so the overshoot rounds away entirely. The two
        // adjacent ints are therefore EQUAL on any healthy completing run, and a positional
        // swap of them would pass every assertion in
        // ReportsTheRequestedAndMeasuredDurationsSeparately.
        //
        // A cut-short burn is the one case where they genuinely differ, by orders of
        // magnitude.
        Assert.Equal(60_000, result.RequestedMs);
        Assert.True(
            result.ElapsedMs < result.RequestedMs / 2,
            $"ElapsedMs {result.ElapsedMs} is not meaningfully below the requested "
            + $"{result.RequestedMs}; if these two are equal on a burn that was cut short, they "
            + "are probably the same value and PiEstimate was constructed positionally");

        // Generous by two orders of magnitude against the 60s it was asked for. The claim is
        // "it noticed and stopped", not "it stopped in exactly 150ms" -- the loop only checks
        // on a batch boundary and a loaded machine makes batches longer.
        Assert.True(
            elapsed < TimeSpan.FromSeconds(10),
            $"a 60s burn took {elapsed.TotalSeconds:F1}s to notice worker shutdown");

        // Still a usable estimate. A drain must not turn the result into garbage, because it
        // is returned rather than thrown and it lands in the history like any other.
        Assert.True(result.Iterations > 0);
        Assert.InRange(result.Pi, 3.0, 3.3);
    }

    [Fact]
    public async Task StopsEarlyOnActivityCancellation()
    {
        // The other token, and in production it is the BUSY one. Measured against the live
        // stack: every burn cut short in a demo run was this token firing at ~64s against a 1m
        // workflow task heartbeat timeout, i.e. the workflow task timing out underneath the
        // activity -- not a drain, and not a user cancel.
        //
        // It must report `canceled` and not `shutdown`. The two were folded into one check
        // originally, and the result was an activity that logged "worker drain cut the burn
        // short" seventeen times during a demo in which nothing had drained.
        var env = new ActivityEnvironment();
        env.CancellationTokenSource.CancelAfter(TimeSpan.FromMilliseconds(150));

        var result = await env.RunAsync(
            () => new PiActivities().EstimatePi(new LocalActivityInput(DurationMs: 60_000, Seed: 4)));

        Assert.Equal(MetricNames.Endings.Canceled, result.EndedBy);
        Assert.NotEqual(MetricNames.Endings.Shutdown, result.EndedBy);
        Assert.True(result.ElapsedMs < 10_000);
    }

    [Fact]
    public async Task ThroughputIsConsistentWithIterationsAndElapsed()
    {
        var result = await new ActivityEnvironment().RunAsync(
            () => new PiActivities().EstimatePi(new LocalActivityInput(DurationMs: ShortBurnMs, Seed: 5)));

        // IterationsPerSecond is the third adjacent long in PiEstimate. This is the only thing
        // that would catch it being constructed from the wrong one, since any of the three is
        // a plausible-looking number on its own.
        var expected = (long)(result.Iterations / TimeSpan.FromMilliseconds(result.ElapsedMs).TotalSeconds);

        // Within 1%, not exact: ElapsedMs is the truncated-to-milliseconds copy of the
        // TimeSpan the activity divided by, so the two disagree by up to one millisecond of
        // rounding.
        Assert.InRange(result.IterationsPerSecond, (long)(expected * 0.99), (long)(expected * 1.01));
    }
}
