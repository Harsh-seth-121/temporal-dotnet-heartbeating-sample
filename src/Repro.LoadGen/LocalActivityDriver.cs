using Microsoft.Extensions.Logging;
using Repro.Core;
using Repro.Core.Config;
using Repro.Core.Workflows;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Repro.LoadGen;

/// <summary>
/// The fourth loadgen loop: starts <c>WorkflowLocalActivity</c> runs on a jittered interval,
/// each with its own randomly drawn burn duration, in the local-activity namespace.
/// </summary>
/// <remarks>
/// Pacing and the shared counters come from <see cref="DriverLoop{TRun}"/>. Two things differ.
/// It runs against its own client, bound to <c>localActivity.namespace</c>; see docs/GOTCHAS.md,
/// "history.workflowTaskHeartbeatTimeout is namespace-scoped and nothing finer". And it draws
/// the burn duration per run, uniform on [minDuration, maxDuration], into the workflow input, so
/// at the shipped 30s..2m against a 1m heartbeat timeout two-thirds of runs are expected to
/// outlive the timeout and re-execute their local activity from zero. The draw lives in the
/// input rather than being re-rolled per attempt, so a doomed run stays doomed, and expect most
/// ticks to skip while a timed-out run holds its slot for all of <c>localActivity.runTimeout</c>.
/// </remarks>
internal sealed class LocalActivityDriver(
    ITemporalClient client,
    LocalActivityConfig localActivity,
    ILogger log)
{
    /// <summary>Bounds the StartWorkflowAsync RPC, so a wedged frontend cannot park the loop.</summary>
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The shared tick loop. Its <c>TRun</c> is the per-run draw, hence the tuple.</summary>
    private readonly DriverLoop<(int DurationMs, int Seed)> loop =
        new(localActivity.Rate, localActivity.Jitter, localActivity.Concurrency);

    private int completed;
    private int timedOut;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var minMs = (int)localActivity.MinDuration.TotalMilliseconds;
        var maxMs = (int)localActivity.MaxDuration.TotalMilliseconds;

        log.LogInformation(
            "local-activity: 1 workflow every {Rate} +/-{JitterPercent}%, up to {Concurrency} in " +
            "flight, burn drawn from {Min}..{Max}, namespace {Namespace}, queue {TaskQueue}, " +
            "runTimeout {RunTimeout}",
            GoDuration.ToGoString(localActivity.Rate), (int)(localActivity.Jitter * 100),
            localActivity.Concurrency, GoDuration.ToGoString(localActivity.MinDuration),
            GoDuration.ToGoString(localActivity.MaxDuration), localActivity.Namespace,
            localActivity.TaskQueue, GoDuration.ToGoString(localActivity.RunTimeout));

        // Skip-at-capacity fires far more often here than in the other loops; see the remarks.
        await loop.RunAsync(

            // Next(min, max + 1) for a closed interval: the upper bound is exclusive, and
            // maxDuration is the value the case is tuned around.
            () => (Random.Shared.Next(minMs, maxMs + 1), Random.Shared.Next()),
            (run, token) => OneRunAsync(run.DurationMs, run.Seed, token),

            // The failure line names the duration asked for: every re-execution reads it.
            (run, e) => log.LogWarning(
                "local-activity run failed after a {DurationMs}ms burn: {Message}",
                run.DurationMs, e.Message),
            LogSummary,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Start one run with its drawn duration, then wait for it.</summary>
    private async Task OneRunAsync(int durationMs, int seed, CancellationToken cancellationToken)
    {
        var input = LocalActivityInput.From(localActivity, durationMs, seed);

        var handle = await client.StartWorkflowAsync(
            (WorkflowLocalActivity wf) => wf.RunAsync(input),

            // "repro-pi-", prefix-disjoint from every other id this repo generates; see
            // SimpleActivityDriver.
            new WorkflowOptions(id: $"repro-pi-{Guid.NewGuid():N}", taskQueue: localActivity.TaskQueue)
            {
                // The only bound that ends a re-executing run: no activity timeout or retry
                // policy survives a workflow task timeout, and the server enforces this one on
                // its own timer queue. It is also why `timedOut` is counted client-side; see
                // docs/GOTCHAS.md, "A run killed by `RunTimeout` records no outcome, because
                // workflow code never runs again".
                RunTimeout = localActivity.RunTimeout,

                Rpc = new RpcOptions { CancellationToken = cancellationToken, Timeout = RpcTimeout },
            }).ConfigureAwait(false);

        PiEstimate estimate;
        try
        {
            // No Timeout, unlike the start call: GetResultAsync long-polls for the whole run,
            // which is minutes by design.
            estimate = await handle.GetResultAsync(
                rpcOptions: new RpcOptions { CancellationToken = cancellationToken }).ConfigureAwait(false);
        }
        catch (WorkflowFailedException e) when (e.InnerException is TimeoutFailureException)
        {
            // Match the exception shape, not a helper or a broad catch; see docs/GOTCHAS.md,
            // "`IsCanceledException` does NOT recognise a cancelled workflow at the client".
            // The expected ending for two-thirds of runs, so counted apart from `failed`.
            Interlocked.Increment(ref timedOut);
            log.LogInformation(
                "local-activity run hit runTimeout after a {DurationMs}ms burn was asked for; its " +
                "local activity was re-executed from zero on every workflow task timeout",
                durationMs);
            return;
        }

        if (Interlocked.Increment(ref completed) == 1)
        {
            // First run only, carrying the two numbers that say how fast this machine is.
            log.LogInformation(
                "local-activity: first run returned pi ~ {Pi} from {Iterations} samples in " +
                "{ElapsedMs}ms ({PerSecond} iterations/s, attempt {Attempt}, isLocal {IsLocal})",
                estimate.Pi, estimate.Iterations, estimate.ElapsedMs, estimate.IterationsPerSecond,
                estimate.Attempt, estimate.IsLocal);
        }
    }

    /// <summary>One line, every ten starts and once at shutdown.</summary>
    /// <remarks>`timedOut` climbing while `failed` stays at zero is the healthy board here,
    /// the opposite of what that shape means in the other three drivers.</remarks>
    private void LogSummary() =>
        log.LogInformation(
            "local-activity: {Started} started, {Skipped} skipped at capacity | {Completed} " +
            "completed, {TimedOut} ended at runTimeout (expected, ~2/3) | {Interrupted} " +
            "interrupted by shutdown, {Failed} failed",
            loop.Started, loop.Skipped, completed, timedOut, loop.Interrupted, loop.Failed);
}
