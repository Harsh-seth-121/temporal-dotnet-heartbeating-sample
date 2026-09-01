using Microsoft.Extensions.Logging;
using Repro.Core;
using Repro.Core.Config;
using Repro.Core.Workflows;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Repro.LoadGen;

/// <summary>
/// The fourth loadgen loop: starts <c>WorkflowLocalActivity</c> runs on a jittered interval,
/// each with its own randomly drawn burn duration, in the LOCAL-ACTIVITY namespace.
/// </summary>
/// <remarks>
/// TWO THINGS MAKE THIS DIFFERENT from the other three drivers, and both are load-bearing.
/// <para>
/// It runs against a DIFFERENT CLIENT, bound to <c>localActivity.namespace</c>. The others
/// share the process's main client. That is not organisation: the setting this whole case
/// depends on, <c>history.workflowTaskHeartbeatTimeout</c>, is namespace-scoped server-side,
/// so a separate namespace is the only way to lower it here without lowering it everywhere.
/// </para>
/// <para>
/// It DRAWS THE DURATION PER RUN and puts it in the workflow input, where the other three
/// project a fixed config value. Uniform on [minDuration, maxDuration], so with the shipped
/// 30s..2m against a 1m heartbeat timeout, exactly two-thirds of runs are expected to outlive
/// the timeout and re-execute their local activity from zero. Because the draw lands in the
/// INPUT rather than being re-rolled per attempt, a doomed run stays doomed: every
/// re-execution reads the same duration and times out again.
/// </para>
/// <para>
/// EXPECT MOST TICKS TO SKIP, and do not read that as breakage. A timed-out run holds its slot
/// for the whole of <c>localActivity.runTimeout</c>, so mean occupancy is roughly
/// (1/3)(a completing run) + (2/3)(runTimeout), which at shipped values is minutes rather than
/// seconds. The summary line prints skipped alongside started for exactly this reason.
/// </para>
/// <para>
/// Everything here is CLIENT code, so Random.Shared and wall-clock are fine. Nothing in this
/// file may leak into workflow code.
/// </para>
/// <para>
/// NO SemaphoreSlim, for the reason <see cref="SimpleDriver"/> records at length. Interlocked
/// counters have no disposal semantics at all.
/// </para>
/// </remarks>
internal sealed class LocalActivityDriver(
    ITemporalClient client,
    LocalActivityConfig localActivity,
    ILogger log)
{
    /// <summary>Bounds the StartWorkflowAsync RPC, so a wedged frontend cannot park the loop.</summary>
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(10);

    private int inFlight;
    private int started;
    private int skipped;
    private int completed;
    private int timedOut;
    private int interrupted;
    private int failed;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var minMs = (int)localActivity.MinDuration.TotalMilliseconds;
        var maxMs = (int)localActivity.MaxDuration.TotalMilliseconds;

        log.LogInformation(
            "local-activity: 1 workflow every {Rate} +/-{JitterPercent}%, up to {Concurrency} in "
            + "flight, burn drawn from {Min}..{Max}, namespace {Namespace}, queue {TaskQueue}, "
            + "runTimeout {RunTimeout}",
            GoDuration.ToGoString(localActivity.Rate), (int)(localActivity.Jitter * 100),
            localActivity.Concurrency, GoDuration.ToGoString(localActivity.MinDuration),
            GoDuration.ToGoString(localActivity.MaxDuration), localActivity.Namespace,
            localActivity.TaskQueue, GoDuration.ToGoString(localActivity.RunTimeout));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(
                    Jitter.NextInterval(localActivity.Rate, localActivity.Jitter),
                    cancellationToken).ConfigureAwait(false);

                // SKIP at capacity, never queue. Same contract as the other three loops, and
                // it fires far more often here; see the class remarks.
                if (Interlocked.Increment(ref inFlight) > localActivity.Concurrency)
                {
                    Interlocked.Decrement(ref inFlight);
                    Interlocked.Increment(ref skipped);
                    continue;
                }

                var n = Interlocked.Increment(ref started);

                // Next(min, max + 1) for a CLOSED interval. Next's upper bound is exclusive, so
                // without the +1 the configured maxDuration is the one value that can never be
                // drawn -- and it is the value the whole case is tuned around.
                var durationMs = Random.Shared.Next(minMs, maxMs + 1);
                var seed = Random.Shared.Next();

                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await OneRunAsync(durationMs, seed, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            // A TOTAL catch, or an unobserved TaskException tears down the
                            // process on finalization.
                            if (cancellationToken.IsCancellationRequested)
                            {
                                Interlocked.Increment(ref interrupted);
                            }
                            else
                            {
                                Interlocked.Increment(ref failed);
                                log.LogWarning(
                                    "local-activity run failed after a {DurationMs}ms burn: {Message}",
                                    durationMs, e.Message);
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

    /// <summary>Start one run with its drawn duration, then wait for it.</summary>
    private async Task OneRunAsync(int durationMs, int seed, CancellationToken cancellationToken)
    {
        var input = LocalActivityInput.From(localActivity, durationMs, seed);

        var handle = await client.StartWorkflowAsync(
            (WorkflowLocalActivity wf) => wf.RunAsync(input),

            // "repro-pi-", checked disjoint as a string PREFIX against every other id this
            // repo generates: repro-loadgen-, repro-simple-, repro-weather-, repro-workflow.
            // The reason is the one SimpleActivityDriver records: a prefix collision makes
            // `WorkflowId STARTS_WITH` visibility queries and `grep` silently merge two cases
            // and report a count that is quietly too high.
            new WorkflowOptions(id: $"repro-pi-{Guid.NewGuid():N}", taskQueue: localActivity.TaskQueue)
            {
                // THE ONLY BOUND THAT ACTUALLY ENDS A RE-EXECUTING RUN, which is why it is set
                // here rather than left to the workflow's activity options. Neither
                // scheduleToClose nor startToClose nor the retry policy survives a workflow
                // task timeout; this does, because the server enforces it on its own timer
                // queue with no worker involvement.
                //
                // It is also why this driver counts `timedOut` client-side. A run ended this
                // way is closed WITHOUT a workflow task, so workflow code never resumes and
                // repro_local_activity_completed never increments for it. The client handle is
                // the only place in this process that observes those runs at all.
                RunTimeout = localActivity.RunTimeout,

                Rpc = new RpcOptions { CancellationToken = cancellationToken, Timeout = RpcTimeout },
            }).ConfigureAwait(false);

        PiEstimate estimate;
        try
        {
            // NO Timeout here, unlike the start call: GetResultAsync long-polls for the whole
            // run, which is minutes by design. The token still releases it at shutdown.
            estimate = await handle.GetResultAsync(
                rpcOptions: new RpcOptions { CancellationToken = cancellationToken }).ConfigureAwait(false);
        }
        catch (WorkflowFailedException e) when (e.InnerException is TimeoutFailureException)
        {
            // MATCH THE SHAPE, do not reach for TemporalException.IsCanceledException or a
            // broad catch. docs/GOTCHAS.md has the measurement: at a CLIENT call site those
            // helpers quietly give the wrong answer, and a broad catch would also swallow
            // shutdown, which is not a failure.
            //
            // This is the EXPECTED ending for about two-thirds of runs at the shipped config,
            // so it is counted separately rather than folded into `failed`. A board where the
            // designed behaviour reads as breakage is worse than no board.
            Interlocked.Increment(ref timedOut);
            log.LogInformation(
                "local-activity run hit runTimeout after a {DurationMs}ms burn was asked for; its "
                + "local activity was re-executed from zero on every workflow task timeout",
                durationMs);
            return;
        }

        if (Interlocked.Increment(ref completed) == 1)
        {
            // First completed run only. This is the line that proves an estimate made it all
            // the way back to the client, and it carries the two numbers that answer "how fast
            // is this machine" without opening Grafana.
            log.LogInformation(
                "local-activity: first run returned pi ~ {Pi} from {Iterations} samples in "
                + "{ElapsedMs}ms ({PerSecond} iterations/s, attempt {Attempt}, isLocal {IsLocal})",
                estimate.Pi, estimate.Iterations, estimate.ElapsedMs, estimate.IterationsPerSecond,
                estimate.Attempt, estimate.IsLocal);
        }
    }

    /// <summary>One line, every ten starts and once at shutdown.</summary>
    /// <remarks>
    /// CONCATENATED STRING LITERALS, not interpolation: CA2254 requires a compile-time constant
    /// message and CA1727 requires PascalCase placeholders, both build errors here.
    /// <para>
    /// `timedOut` climbing while `failed` stays at zero IS the healthy board for this case, and
    /// it is the opposite of what the same shape means in the other three drivers. Two-thirds
    /// of runs are designed to end that way.
    /// </para>
    /// </remarks>
    private void LogSummary() =>
        log.LogInformation(
            "local-activity: {Started} started, {Skipped} skipped at capacity | {Completed} "
            + "completed, {TimedOut} ended at runTimeout (expected, ~2/3) | {Interrupted} "
            + "interrupted by shutdown, {Failed} failed",
            started, skipped, completed, timedOut, interrupted, failed);
}
