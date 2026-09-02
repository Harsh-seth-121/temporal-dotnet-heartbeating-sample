using Microsoft.Extensions.Logging;
using Repro.Core;
using Repro.Core.Config;
using Repro.Core.Telemetry;
using Repro.Core.Workflows;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Repro.LoadGen;

/// <summary>
/// The second loadgen loop: starts SimpleNoActivity runs on a jittered interval and throws chaos
/// at each one, being a random mix of signals and updates, a weighted random ending,
/// deliberately overflowing operands, and a message sent after the run has closed. Pacing and
/// the shared counters come from <see cref="DriverLoop{TRun}"/>.
/// </summary>
internal sealed class SimpleDriver(
    ITemporalClient client,
    SimpleConfig simple,
    string taskQueue,
    ILogger log)
{
    /// <summary>
    /// Bounds every unary RPC. ExecuteUpdateAsync against a task queue with no polling worker
    /// retries indefinitely, so an unbounded call holds a slot past demo-down.sh's drain budget.
    /// </summary>
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The shared tick loop and its started/skipped/interrupted/failed counters.</summary>
    private readonly DriverLoop<int> loop = new(simple.Rate, simple.Jitter, simple.Concurrency);

    private int stopped;
    private int canceled;
    private int expired;
    private int updates;
    private int rejected;
    private int raced;

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

        // Every run shares the same configured maximum, so the draw is a constant. The ending
        // is drawn inside OneRunAsync, where the counters it feeds live.
        await loop.RunAsync(
            () => maxDurationMs,
            OneRunAsync,

            // Reached only by a failure with no per-outcome catch; the expected endings are
            // counted in OneRunAsync.
            (_, e) => log.LogWarning("simple run failed: {Message}", e.Message),
            LogSummary,
            cancellationToken).ConfigureAwait(false);
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
                // The only path that produces a server status of Canceled. See docs/GOTCHAS.md,
                // "A workflow cannot cancel ITSELF into `CANCELED` status".
                await handle.CancelAsync(
                    new WorkflowCancelOptions { Rpc = rpc }).ConfigureAwait(false);
                break;

            default:
                break;   // Ending.Expire: send nothing, let MaxDurationMs end it
        }

        try
        {
            // No Timeout on this one: GetResultAsync long-polls for the whole run. The
            // cancellation token still releases it at shutdown.
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
            // Expected for Ending.Cancel. A cancelled run reaches a client as
            // WorkflowFailedException{InnerException: CanceledFailureException}; see
            // docs/GOTCHAS.md, "`IsCanceledException` does NOT recognise a cancelled workflow at
            // the client". Matching the shape also keeps shutdown, an
            // OperationCanceledException, out of this bucket.
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
        // Chaos: with probability overflowRate, hand the update a pair whose sum does not fit in
        // an int. The validator refuses it, and a refused update writes nothing to history.
        var overflow = Random.Shared.NextDouble() < simple.OverflowRate;
        var a = overflow ? int.MaxValue : Random.Shared.Next(-1_000_000, 1_000_000);
        var b = overflow ? int.MaxValue : Random.Shared.Next(-1_000_000, 1_000_000);

        try
        {
            var sum = await handle.ExecuteUpdateAsync(
                wf => wf.AddAsync(new AddInput(a, b)),
                new WorkflowUpdateOptions { Rpc = rpc }).ConfigureAwait(false);

            Interlocked.Increment(ref updates);

            // A cheap assertion on the update round trip. `(long)a + b` because a plain `a + b`
            // would wrap in the case worth catching.
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

        // Chaos: the run is closed here, since GetResultAsync already returned or threw, and
        // signalling a closed workflow is an RpcException with StatusCode.NotFound.
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

    /// <summary>One line, every ten starts and once at shutdown.</summary>
    private void LogSummary() =>
        log.LogInformation(
            "simple: {Started} started, {Skipped} skipped at capacity | {Stopped} stopped, " +
            "{Canceled} canceled, {Expired} expired | {Updates} updates, {Rejected} rejected, " +
            "{Raced} post-close NotFound, {Interrupted} interrupted by shutdown, {Failed} failed",
            loop.Started, loop.Skipped, stopped, canceled, expired, updates, rejected, raced,
            loop.Interrupted, loop.Failed);
}
