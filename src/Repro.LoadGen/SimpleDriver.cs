using Microsoft.Extensions.Logging;
using Repro.Core;
using Repro.Core.Config;
using Repro.Core.Telemetry;
using Repro.Core.Workflows;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Repro.LoadGen;

/// <summary>
/// The second loadgen loop: starts SimpleNoActivity runs on a JITTERED interval and
/// throws chaos at each one: a random mix of signals and updates, a weighted random
/// ending, deliberately overflowing operands, and a message sent after the run has
/// already closed.
/// </summary>
/// <remarks>
/// Everything in here is CLIENT code, so Random.Shared and wall-clock are fine, the same
/// licence HeartbeatActivities has. Nothing in this file may leak into workflow code.
/// <para>
/// NO SemaphoreSlim, unlike the heartbeat loop in Program.cs, and deliberately so.
/// That loop's <c>using var slots</c> is disposed when the method returns while
/// fire-and-forget run bodies are still calling slots.Release() in a finally. That is a latent
/// ObjectDisposedException masked only by the process exiting immediately afterwards. An
/// Interlocked counter has no disposal semantics at all and expresses "skip the tick at
/// capacity" just as directly.
/// </para>
/// </remarks>
internal sealed class SimpleDriver(
    ITemporalClient client,
    SimpleConfig simple,
    string taskQueue,
    ILogger log)
{
    /// <summary>
    /// Bounds every unary RPC. ExecuteUpdateAsync against a task queue with NO polling
    /// worker retries indefinitely, so an unbounded call would hold a slot forever and
    /// park this process past demo-down.sh's drain budget.
    /// </summary>
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(10);

    private int inFlight;
    private int started;
    private int skipped;
    private int stopped;
    private int canceled;
    private int expired;
    private int updates;
    private int rejected;
    private int raced;
    private int interrupted;
    private int failed;

    private enum Ending
    {
        Stop,
        Cancel,
        Expire,
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var maxDurationMs = (int)simple.MaxDuration.TotalMilliseconds;

        log.LogInformation(
            "simple: 1 workflow every {Rate} +/-{JitterPercent}%, up to {Concurrency} in flight, " +
            "{MinMessages}-{MaxMessages} messages each, ending stop/cancel/expire " +
            "{StopWeight}/{CancelWeight}/{ExpireWeight}, max duration {MaxDuration}",
            GoDuration.ToGoString(simple.Rate), (int)(simple.Jitter * 100), simple.Concurrency,
            simple.MinMessages, simple.MaxMessages, simple.StopWeight, simple.CancelWeight,
            simple.ExpireWeight, GoDuration.ToGoString(simple.MaxDuration));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Task.Delay, not PeriodicTimer: a PeriodicTimer has one fixed period and
                // the period has to vary here. The token is forwarded
                // because CA2016 is an error in this repo, and because without it a
                // shutdown waits out a full interval. See Jitter for why the formula lives
                // in one place and which validation rule keeps it safe.
                await Task.Delay(
                    Jitter.NextInterval(simple.Rate, simple.Jitter),
                    cancellationToken).ConfigureAwait(false);

                // SKIP at capacity, never queue. Same contract as the heartbeat loop.
                // Queueing would build an unbounded backlog and `rate` would stop
                // describing what the process is doing.
                if (Interlocked.Increment(ref inFlight) > simple.Concurrency)
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
                            await OneRunAsync(maxDurationMs, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            // A TOTAL catch, or an unobserved TaskException tears down the
                            // process on finalization.
                            //
                            // Shutdown is counted SEPARATELY from failure. A run whose RPCs
                            // were cancelled because the process is going down did not fail,
                            // and folding the two together makes every clean Ctrl-C look
                            // like it broke something, which is exactly the kind of
                            // misleading signal this repo exists to avoid.
                            if (cancellationToken.IsCancellationRequested)
                            {
                                Interlocked.Increment(ref interrupted);
                            }
                            else
                            {
                                Interlocked.Increment(ref failed);
                                log.LogWarning("simple run failed: {Message}", e.Message);
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

    private Ending PickEnding()
    {
        // Validate guarantees the sum is positive, so Next(total) cannot throw.
        var total = simple.StopWeight + simple.CancelWeight + simple.ExpireWeight;
        var roll = Random.Shared.Next(total);

        if (roll < simple.StopWeight)
        {
            return Ending.Stop;
        }

        return roll < simple.StopWeight + simple.CancelWeight ? Ending.Cancel : Ending.Expire;
    }

    private async Task OneRunAsync(int maxDurationMs, CancellationToken cancellationToken)
    {
        var rpc = new RpcOptions { CancellationToken = cancellationToken, Timeout = RpcTimeout };

        var handle = await client.StartWorkflowAsync(
            (SimpleNoActivity wf) => wf.RunAsync(new SimpleInput(maxDurationMs)),
            new WorkflowOptions(id: $"repro-simple-{Guid.NewGuid():N}", taskQueue: taskQueue)
            {
                // The start call needs the same bound as every other unary RPC below. An
                // unresponsive frontend here would hold a capacity slot forever and park
                // the process past demo-down.sh's 45s drain budget.
                Rpc = rpc,
            }).ConfigureAwait(false);

        var ending = PickEnding();
        await SendMessagesAsync(handle, rpc, cancellationToken).ConfigureAwait(false);

        switch (ending)
        {
            case Ending.Stop:
                await handle.SignalAsync(
                    wf => wf.StopAsync(),
                    new WorkflowSignalOptions { Rpc = rpc }).ConfigureAwait(false);
                break;

            case Ending.Cancel:
                // The ONLY path that produces a server status of Canceled. The workflow
                // cannot do this to itself.
                await handle.CancelAsync(
                    new WorkflowCancelOptions { Rpc = rpc }).ConfigureAwait(false);
                break;

            default:
                break;   // Ending.Expire: send nothing, let MaxDurationMs end it
        }

        try
        {
            // NO Timeout on this one: GetResultAsync long-polls for the whole run, and an
            // RpcTimeout here would abort a perfectly healthy wait. The cancellation token
            // still releases it at shutdown.
            var result = await handle.GetResultAsync(
                rpcOptions: new RpcOptions { CancellationToken = cancellationToken })
                .ConfigureAwait(false);

            if (result.EndedBy == MetricNames.Outcomes.Stopped)
            {
                Interlocked.Increment(ref stopped);
            }
            else
            {
                Interlocked.Increment(ref expired);
            }
        }
        catch (WorkflowFailedException e) when (e.InnerException is CanceledFailureException)
        {
            // EXPECTED for Ending.Cancel, and the exact shape matters twice over.
            //
            // MEASURED, and the opposite of what you would reach for: a cancelled run
            // arrives at a CLIENT as WorkflowFailedException{InnerException:
            // CanceledFailureException}, and TemporalException.IsCanceledException returns
            // FALSE for it. That helper covers .NET cancellation plus a cancellation nested
            // in an ACTIVITY or CHILD WORKFLOW failure, which is why it is right inside
            // SimpleNoActivity and wrong here. Using it at this call site classified every
            // deliberately cancelled run as a failure, with the counter reading
            // "0 canceled ... 2 failed" and no other symptom.
            //
            // Matching the shape rather than catching broadly also keeps SHUTDOWN out of
            // this bucket: when our own token cancels GetResultAsync we get an
            // OperationCanceledException, which is not a cancelled workflow and must not be
            // counted as one.
            Interlocked.Increment(ref canceled);
        }

        await MaybeRaceAsync(handle, rpc).ConfigureAwait(false);
    }

    private async Task SendMessagesAsync(
        WorkflowHandle<SimpleNoActivity, SimpleResult> handle,
        RpcOptions rpc,
        CancellationToken cancellationToken)
    {
        var count = Random.Shared.Next(simple.MinMessages, simple.MaxMessages + 1);
        var gapCeilingMs = (int)simple.MessageGap.TotalMilliseconds;

        for (var i = 0; i < count; i++)
        {
            await Task.Delay(Random.Shared.Next(gapCeilingMs + 1), cancellationToken)
                .ConfigureAwait(false);

            if (Random.Shared.NextDouble() < 0.5)
            {
                await handle.SignalAsync(
                    wf => wf.PokeAsync(new PokeInput($"poke {i}")),
                    new WorkflowSignalOptions { Rpc = rpc }).ConfigureAwait(false);
                continue;
            }

            await SendAddAsync(handle, rpc, i).ConfigureAwait(false);
        }
    }

    private async Task SendAddAsync(
        WorkflowHandle<SimpleNoActivity, SimpleResult> handle, RpcOptions rpc, int index)
    {
        // CHAOS: with probability overflowRate, hand the update a pair whose sum does not
        // fit in an int. The workflow's validator refuses it, and a refused update writes
        // NOTHING to the event history.
        var overflow = Random.Shared.NextDouble() < simple.OverflowRate;
        var a = overflow ? int.MaxValue : Random.Shared.Next(-1_000_000, 1_000_000);
        var b = overflow ? int.MaxValue : Random.Shared.Next(-1_000_000, 1_000_000);

        try
        {
            var sum = await handle.ExecuteUpdateAsync(
                wf => wf.AddAsync(new AddInput(a, b)),
                new WorkflowUpdateOptions { Rpc = rpc }).ConfigureAwait(false);

            Interlocked.Increment(ref updates);

            // A cheap end-to-end assertion on the update round trip: payload converter in,
            // handler, payload converter out. `(long)a + b` because a plain `a + b` in the
            // comparison would wrap in exactly the case worth catching.
            if (sum != (long)a + b)
            {
                log.LogWarning("update {Index} returned {Sum} for {A} + {B}", index, sum, a, b);
            }
        }
        catch (WorkflowUpdateFailedException) when (overflow)
        {
            Interlocked.Increment(ref rejected);
        }
    }

    private async Task MaybeRaceAsync(
        WorkflowHandle<SimpleNoActivity, SimpleResult> handle, RpcOptions rpc)
    {
        if (Random.Shared.NextDouble() >= simple.RaceRate)
        {
            return;
        }

        // CHAOS: the run is definitely CLOSED here, because GetResultAsync already returned or
        // threw. Signalling a closed workflow is an RpcException with StatusCode.NotFound,
        // not a crash.
        try
        {
            await handle.SignalAsync(
                wf => wf.PokeAsync(new PokeInput("after close")),
                new WorkflowSignalOptions { Rpc = rpc }).ConfigureAwait(false);

            log.LogWarning("post-close signal unexpectedly SUCCEEDED for {WorkflowId}", handle.Id);
        }
        catch (RpcException e) when (e.Code == RpcException.StatusCode.NotFound)
        {
            Interlocked.Increment(ref raced);
        }
    }

    /// <remarks>
    /// The template is a concatenation of string LITERALS, which is a compile-time constant.
    /// CA2254 rejects an interpolated template and CA1727 rejects lowercase placeholders, and
    /// both are errors in this repo.
    /// </remarks>
    private void LogSummary() =>
        log.LogInformation(
            "simple: {Started} started, {Skipped} skipped at capacity | {Stopped} stopped, " +
            "{Canceled} canceled, {Expired} expired | {Updates} updates, {Rejected} rejected, " +
            "{Raced} post-close NotFound, {Interrupted} interrupted by shutdown, {Failed} failed",
            started, skipped, stopped, canceled, expired, updates, rejected, raced, interrupted,
            failed);
}
