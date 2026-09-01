using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Repro.Core.Telemetry;
using Temporalio.Activities;

namespace Repro.Core.Activities;

/// <summary>
/// One LOCAL activity: burn CPU estimating Pi by Monte Carlo for a requested duration.
/// </summary>
/// <remarks>
/// Register this as its OWN instance with a third
/// <c>.AddAllActivities(new PiActivities())</c> call, and register it on the LOCAL-ACTIVITY
/// worker only. AddAllActivities takes exactly one instance, and a local activity is resolved
/// against the registry of the worker running the WORKFLOW: put this on the wrong worker and
/// it throws at schedule time, inside the workflow task, with "is not registered on this
/// worker".
/// <para>
/// NOTHING IS INJECTED, which departs from both <c>HeartbeatActivities(FaultConfig, ...)</c>
/// and <c>WeatherActivities(SimpleActivityConfig)</c>. Those take a constructor argument
/// because they have infrastructure or policy that must not be reachable from workflow code.
/// This activity has neither: the duration and the RNG seed are job shape and travel in the
/// workflow input, and there is no endpoint, no client and no fault knob. A constructor
/// parameter added here for symmetry would be a lie about what the activity can reach.
/// </para>
/// <para>
/// THE WIRE NAME IS <c>EstimatePi</c>, and this is the repo's FIRST synchronous activity. The
/// SDK trims an <c>Async</c> suffix, which is why the other two are ProcessBatch and
/// FetchWeather rather than ...Async; there is nothing to trim here. It is synchronous
/// because there is nothing to await: an <c>async</c> method with no <c>await</c> is CS1998,
/// and TreatWarningsAsErrors makes that a build failure. The SDK validates the return type,
/// not the async-ness, and
/// <c>ExecuteLocalActivityAsync&lt;TActivityInstance, TResult&gt;(Expression&lt;Func&lt;T0, T1&gt;&gt;, ...)</c>
/// is the overload that binds a non-static, non-async, result-returning method.
/// </para>
/// <para>
/// It occupies a thread for its whole duration, on the default
/// <c>TemporalWorkerOptions.ActivityTaskFactory</c>, which is <c>Task.Factory</c>. That is
/// what <c>localActivity.maxConcurrentLocalActivities</c> exists to bound, and the bound is
/// not about politeness: workflow activations run on the same thread pool, and the SDK fails
/// any workflow task that does not yield within 2 seconds. A saturated pool produces evicted
/// runs and retried workflow tasks that look exactly like this case's real failure and are
/// not it.
/// </para>
/// </remarks>
public sealed class PiActivities
{
    /// <summary>Iterations between one clock-and-cancellation check and the next.</summary>
    /// <remarks>
    /// A power of two so the check is a mask rather than a division, and sized so the check
    /// itself is not the workload. At a few tens of millions of iterations a second, 65,536
    /// iterations is single-digit milliseconds, which is both a negligible fraction of the
    /// loop and a fine granularity for noticing a drain.
    /// <para>
    /// DO NOT CHECK EVERY ITERATION. Two token reads and a Stopwatch call per sample would
    /// dominate the arithmetic being measured, and <c>PiEstimate.IterationsPerSecond</c> would
    /// then report the cost of checking rather than the cost of computing.
    /// </para>
    /// </remarks>
    private const int CheckEvery = 1 << 16;

    /// <summary>Burn CPU for <c>input.DurationMs</c>, then return the estimate. Wire name <c>EstimatePi</c>.</summary>
    [Activity]
    public PiEstimate EstimatePi(LocalActivityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Capture ONCE. ActivityExecutionContext.Current is an AsyncLocal lookup that throws
        // outside an activity, and it does NOT flow into a Task.Run or a Parallel.For this
        // method might one day spawn. Hoisting it, and both tokens below, is what keeps that
        // future edit from throwing on a worker thread.
        var ctx = ActivityExecutionContext.Current;
        var log = ctx.Logger;

        // BEFORE any work, so a re-execution is counted even if it is killed part way. That
        // is the entire point of the counter: a run whose workflow task times out produces
        // several of these and no completion at all.
        //
        // Emitted from ACTIVITY code deliberately. Workflow.MetricMeter is replay-suppressed,
        // and a local activity re-executed after a workflow task timeout is not a replay, it
        // is a second real burn. Counting it on the workflow side would hide the waste.
        ctx.MetricMeter.CreateCounter<long>(MetricNames.PiAttemptStarted).Add(1);

        // Logged rather than asserted, because these four are the answer to questions this
        // repo could not settle from the SDK's documentation alone: whether a LOCAL activity
        // gets a context at all, what its attempt number reads after a re-execution, and which
        // task queue it reports. IsLocal also reaches the returned payload, so the history
        // carries the answer too.
        log.LogInformation(
            "estimating pi for {DurationMs}ms with seed {Seed} (isLocal {IsLocal}, attempt "
            + "{Attempt}, taskQueue {TaskQueue})",
            input.DurationMs, input.Seed, ctx.Info.IsLocal, ctx.Info.Attempt, ctx.Info.TaskQueue);

        // WHICH TOKEN FIRES IS THE WHOLE QUESTION, so both are watched.
        //
        // ctx.CancellationToken is the one every Temporal sample polls, and for a REGULAR
        // activity it is correct. For a local activity on WORKER SHUTDOWN it is the wrong one:
        // in sdk-core the graceful shutdown period is applied in activities.rs against
        // WorkerActivityTasks, i.e. server-scheduled activities only, and local_activities.rs
        // has no cancel path on shutdown at all -- it only waits, via
        // wait_all_outstanding_tasks_finished(), which core awaits BEFORE shutting anything
        // else down. Core never sends the Cancel variant for a local activity, so nothing
        // cancels that token.
        //
        // ctx.WorkerShutdownToken is documented as "cancelled when the worker is shutdown" and
        // is fired by ActivityWorker.NotifyShutdown() with no local/non-local distinction, so
        // it is the one expected to actually fire here.
        //
        // Both are polled anyway. Watching only the token this comment predicts would make the
        // prediction unfalsifiable, and a cancel requested through the workflow still arrives
        // on the first one.
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
                // The unit square, counting the quarter disc. x*x + y*y rather than any
                // sqrt: the comparison against 1 is equivalent and a square root here would
                // be most of the per-sample cost.
                var x = rng.NextDouble();
                var y = rng.NextDouble();
                if ((x * x) + (y * y) <= 1.0)
                {
                    inside++;
                }
            }

            iterations += CheckEvery;

            if (workerShutdown.IsCancellationRequested || cancellation.IsCancellationRequested)
            {
                endedBy = MetricNames.Endings.Shutdown;
                break;
            }

            if (Stopwatch.GetElapsedTime(startedAt) >= budget)
            {
                break;
            }
        }

        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        var elapsedMs = (int)elapsed.TotalMilliseconds;

        // Guarded because a burn cut short in its first batch can round to zero milliseconds,
        // and a division by it would end the activity with a DivideByZeroException whose real
        // cause is buried under an ActivityFailure chain.
        var perSecond = elapsed > TimeSpan.Zero
            ? (long)(iterations / elapsed.TotalSeconds)
            : 0;

        var pi = iterations > 0 ? 4.0 * inside / iterations : 0.0;

        if (endedBy == MetricNames.Endings.Shutdown)
        {
            // WARNING, not Information. This estimate is about to be reported as a normal
            // result on a run that was cut short, and the only thing distinguishing it in the
            // history is PiEstimate.EndedBy.
            log.LogWarning(
                "worker drain cut the burn short at {ElapsedMs}ms of {RequestedMs}ms; returning a "
                + "SHORT estimate from {Iterations} samples",
                elapsedMs, input.DurationMs, iterations);
        }
        else
        {
            log.LogInformation(
                "pi ~ {Pi} from {Iterations} samples in {ElapsedMs}ms ({PerSecond} iterations/s)",
                pi, iterations, elapsedMs, perSecond);
        }

        // NAMED arguments, not positional, and here the hazard is worse than usual: three
        // adjacent longs (Iterations, Inside, then IterationsPerSecond) and two adjacent ints
        // (RequestedMs, ElapsedMs). Positionally, swapping the ints reports the duration that
        // was ASKED FOR as the one that was MEASURED, which is exactly the number this case
        // exists to show, and it compiles clean.
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
