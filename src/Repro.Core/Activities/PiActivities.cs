using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Repro.Core.Telemetry;
using Temporalio.Activities;

namespace Repro.Core.Activities;

/// <summary>
/// One local activity: burn CPU estimating Pi by Monte Carlo for a requested duration.
/// </summary>
/// <remarks>
/// Register on the local-activity worker only: a local activity resolves against the registry
/// of the worker running the workflow, so on the wrong worker it throws at schedule time
/// inside the workflow task. Wire name <c>EstimatePi</c>; the SDK trims an <c>Async</c> suffix
/// and there is none here. Synchronous because an <c>async</c> method with no <c>await</c> is
/// CS1998, an error at this repo's settings; the SDK binds on return type, not async-ness.
/// <para>
/// It holds a thread for its whole duration, which is what
/// <c>localActivity.maxConcurrentLocalActivities</c> bounds. Workflow activations share that
/// pool and the SDK fails any workflow task that does not yield within 2 seconds, so a
/// saturated pool produces evictions that mimic this case's real failure.
/// </para>
/// </remarks>
public sealed class PiActivities
{
    /// <summary>Iterations between one clock-and-cancellation check and the next.</summary>
    /// <remarks>
    /// A power of two so the check is a mask, and sized so the check is not the workload. At a
    /// few tens of millions of iterations a second this is single-digit milliseconds. Checking
    /// every iteration would make <c>PiEstimate.IterationsPerSecond</c> report the check.
    /// </remarks>
    private const int CheckEvery = 1 << 16;

    /// <summary>Burn CPU for <c>input.DurationMs</c>, then return the estimate. Wire name <c>EstimatePi</c>.</summary>
    [Activity]
    public PiEstimate EstimatePi(LocalActivityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Capture once; see HeartbeatActivities.ProcessBatchAsync.
        var ctx = ActivityExecutionContext.Current;
        var log = ctx.Logger;

        // Before any work, so a re-execution killed part way is still counted: a run whose
        // workflow task times out produces several of these and no completion.
        //
        // From activity code, not workflow code. Workflow.MetricMeter is replay-suppressed, and
        // a local activity re-executed after a workflow task timeout is a second real burn.
        ctx.MetricMeter.CreateCounter<long>(MetricNames.PiAttemptStarted).Add(1);

        // IsLocal, Attempt and TaskQueue for a local activity are hard to get from the SDK
        // docs. IsLocal also reaches the returned payload, so the history carries it.
        log.LogInformation(
            "estimating pi for {DurationMs}ms with seed {Seed} (isLocal {IsLocal}, attempt " +
            "{Attempt}, taskQueue {TaskQueue})",
            input.DurationMs, input.Seed, ctx.Info.IsLocal, ctx.Info.Attempt, ctx.Info.TaskQueue);

        // Both tokens, watched separately, because they mean different things here.
        // ctx.CancellationToken never fires on worker shutdown for a local activity: sdk-core
        // applies the graceful shutdown period to server-scheduled activities only, and
        // local_activities.rs has no cancel path, it only waits. ctx.WorkerShutdownToken is
        // fired by ActivityWorker.NotifyShutdown() with no local/non-local distinction, so it
        // is the drain signal. Measured: in one demo run 17 burns were cut short, all by
        // ctx.CancellationToken at ~64s against a 1m workflow task heartbeat timeout. A single
        // `||` misreported all seventeen as worker drains.
        var cancellation = ctx.CancellationToken;
        var workerShutdown = ctx.WorkerShutdownToken;

        var budget = TimeSpan.FromMilliseconds(input.DurationMs);
        var startedAt = Stopwatch.GetTimestamp();

        // Seeded, so a captured history reproduces its own estimate. Client-drawn seed; see
        // LocalActivityInput.Seed for why this is not RandomNumberGenerator.
        var rng = new Random(input.Seed);

        long iterations = 0;
        long inside = 0;
        var endedBy = MetricNames.Endings.Completed;

        while (true)
        {
            for (var i = 0; i < CheckEvery; i++)
            {
                // The unit square, counting the quarter disc. No sqrt: the comparison against
                // 1 is equivalent and a square root would be most of the per-sample cost.
                var x = rng.NextDouble();
                var y = rng.NextDouble();
                if ((x * x) + (y * y) <= 1.0)
                {
                    inside++;
                }
            }

            iterations += CheckEvery;

            // Separate branches, not a single ||. See the token capture above.
            if (workerShutdown.IsCancellationRequested)
            {
                endedBy = MetricNames.Endings.Shutdown;
                break;
            }

            if (cancellation.IsCancellationRequested)
            {
                endedBy = MetricNames.Endings.Canceled;
                break;
            }

            if (Stopwatch.GetElapsedTime(startedAt) >= budget)
            {
                break;
            }
        }

        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        var elapsedMs = (int)elapsed.TotalMilliseconds;

        // Double division, so a zero elapsed yields +Infinity rather than throwing, and the
        // cast to long puts a meaningless number into PiEstimate and the history. A burn cut
        // short in its first batch can measure that little.
        var perSecond = elapsed > TimeSpan.Zero
            ? (long)(iterations / elapsed.TotalSeconds)
            : 0;

        var pi = iterations > 0 ? 4.0 * inside / iterations : 0.0;

        if (endedBy == MetricNames.Endings.Shutdown)
        {
            // Warning, not Information: a cut-short run reports a normal result and only
            // PiEstimate.EndedBy distinguishes it in the history.
            log.LogWarning(
                "worker drain cut the burn short at {ElapsedMs}ms of {RequestedMs}ms; returning a " +
                "SHORT estimate from {Iterations} samples",
                elapsedMs, input.DurationMs, iterations);
        }
        else if (endedBy == MetricNames.Endings.Canceled)
        {
            // The ordinary ending here: at the shipped config two-thirds of runs draw a
            // duration longer than the workflow task heartbeat timeout, measured at ~64s
            // against 1m. The elapsed time identifies which cancellation this was.
            log.LogWarning(
                "burn CANCELLED at {ElapsedMs}ms of {RequestedMs}ms after {Iterations} samples; if " +
                "this is near the workflow task heartbeat timeout the task timed out and this " +
                "entire burn is about to be repeated from zero",
                elapsedMs, input.DurationMs, iterations);
        }
        else
        {
            log.LogInformation(
                "pi ~ {Pi} from {Iterations} samples in {ElapsedMs}ms ({PerSecond} iterations/s)",
                pi, iterations, elapsedMs, perSecond);
        }

        // Named arguments: swapping the two adjacent ints positionally compiles clean and
        // reports the duration asked for as the one measured.
        return new PiEstimate(
            Pi: pi,
            Iterations: iterations,
            Inside: inside,
            RequestedMs: input.DurationMs,
            ElapsedMs: elapsedMs,
            IterationsPerSecond: perSecond,
            Attempt: ctx.Info.Attempt,
            IsLocal: ctx.Info.IsLocal,
            EndedBy: endedBy);
    }
}
