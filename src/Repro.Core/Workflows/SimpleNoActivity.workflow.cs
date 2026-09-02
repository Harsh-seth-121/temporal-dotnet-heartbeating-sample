using Microsoft.Extensions.Logging;
using Repro.Core.Telemetry;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Repro.Core.Workflows;

/// <summary>The message-passing case: no activities. It ends on a Stop signal, on its own
/// MaxDurationMs, or on a client cancellation request.</summary>
/// <remarks>
/// There is no "cancel myself" path: a workflow cannot put itself into CANCELED, because the
/// server records that status only when a cancellation request exists. So Stop ends the run
/// Completed with EndedBy="stopped", and a real Canceled comes from a client's
/// handle.CancelAsync(). See docs/GOTCHAS.md and docs/WORKFLOWS.md.
/// </remarks>
[Workflow]
public class SimpleNoActivity
{
    // No initializers. CA1805 is an error under this repo's TreatWarningsAsErrors, so
    // `= false` or `= 0` will not compile.
    private int pokes;
    private int adds;
    private int lastSum;
    private bool stopRequested;

    [WorkflowRun]
    public async Task<SimpleResult> RunAsync(SimpleInput input)
    {
        var start = Workflow.UtcNow;
        var meter = Workflow.MetricMeter;

        bool stopped;
        try
        {
            // The cancellationToken argument is unset on purpose: it defaults to
            // Workflow.CancellationToken, which is the entire cancel path.
            stopped = await Workflow.WaitConditionAsync(
                () => stopRequested,
                TimeSpan.FromMilliseconds(input.MaxDurationMs)).ConfigureAwait(true);
        }
        catch (Exception e) when (TemporalException.IsCanceledException(e))
        {
            // IsCanceledException, not `catch (OperationCanceledException)`, for the reason
            // HeartbeatWorkflow.Classify records.
            Workflow.Logger.LogInformation(
                "cancel requested after {Pokes} pokes and {Adds} adds", pokes, adds);
            Record(meter, MetricNames.Outcomes.Canceled, start);

            // Rethrow. Swallowing this returns a value and the server records Completed for
            // a run the operator cancelled. Only the rethrow produces a real Canceled.
            throw;
        }

        var endedBy = stopped ? MetricNames.Outcomes.Stopped : MetricNames.Outcomes.Expired;
        Workflow.Logger.LogInformation(
            "ending as {EndedBy} after {Pokes} pokes and {Adds} adds", endedBy, pokes, adds);
        Record(meter, endedBy, start);

        return new SimpleResult(
            endedBy, pokes, adds, lastSum, (int)(Workflow.UtcNow - start).TotalMilliseconds);
    }

    /// <summary>The simple signal. Wire name: <c>Poke</c> (the SDK trims the Async suffix).</summary>
    /// <remarks>Non-async on purpose: `async Task PokeAsync(...) => pokes++;` raises CS1998, an
    /// error here. The SDK validates only a signal handler's return type.</remarks>
    [WorkflowSignal]
    public Task PokeAsync(PokeInput input)
    {
        pokes++;
        Workflow.Logger.LogDebug("poke {Pokes}: {Note}", pokes, input.Note);
        CountMessage(MetricNames.Kinds.Poke);
        return Task.CompletedTask;
    }

    /// <summary>Ask the run to end. Wire name: <c>Stop</c>. Produces Completed, not
    /// Canceled; see the class remarks.</summary>
    [WorkflowSignal]
    public Task StopAsync()
    {
        stopRequested = true;
        return Task.CompletedTask;
    }

    /// <summary>The simple handler: read state, change nothing. Wire name: <c>GetStatus</c>.</summary>
    /// <remarks>Queries keep an Async suffix where signals and updates lose it, and may not
    /// return a Task: GetStatusAsync would be the wire name literally.</remarks>
    [WorkflowQuery]
    public SimpleStatus GetStatus() => new(pokes, adds, lastSum, stopRequested);

    /// <summary>Rejects an Add whose sum does not fit in an int, before it reaches history.</summary>
    /// <remarks>A validator is the only way to refuse a message without writing to history.
    /// It must be void, non-static, take exactly the handler's parameters, and be side-effect
    /// free, so nothing is counted here.</remarks>
    [WorkflowUpdateValidator(nameof(AddAsync))]
    public void ValidateAdd(AddInput input)
    {
        // Cast first. `(long)(input.A + input.B)` adds two ints unchecked, wraps, then widens
        // the already-wrong answer, so the guard would never fire.
        var sum = (long)input.A + input.B;
        if (sum is > int.MaxValue or < int.MinValue)
        {
            throw new ApplicationFailureException(
                $"{input.A} + {input.B} = {sum}, which does not fit in an int", "AddOverflow");
        }
    }

    /// <summary>Adds two integers and returns the sum. Wire name: <c>Add</c>.</summary>
    /// <remarks>Non-async for the same CS1998 reason as <see cref="PokeAsync"/>.</remarks>
    [WorkflowUpdate]
    public Task<int> AddAsync(AddInput input)
    {
        adds++;
        lastSum = input.A + input.B;
        CountMessage(MetricNames.Kinds.Add);
        return Task.FromResult(lastSum);
    }

    /// <summary>One counter per message that reached a handler.</summary>
    private static void CountMessage(string kind) =>
        Workflow.MetricMeter.WithTags(new Dictionary<string, object>
        {
            // Kind only. See HeartbeatWorkflow.Record for the root tags already present.
            [MetricNames.Tags.Kind] = kind,
        }).CreateCounter<long>(MetricNames.SimpleMessage).Add(1);

    /// <summary>Same shape as HeartbeatWorkflow.Record, against the repro_simple_* names.</summary>
    private static void Record(MetricMeter meter, string outcome, DateTime start)
    {
        var tagged = meter.WithTags(new Dictionary<string, object>
        {
            [MetricNames.Tags.Outcome] = outcome,
        });

        tagged.CreateCounter<long>(MetricNames.SimpleCompleted).Add(1);

        // TimeSpan histogram, so the unit follows UseSecondsForDuration.
        tagged.CreateHistogram<TimeSpan>(MetricNames.SimpleLatency)
            .Record(Workflow.UtcNow - start);
    }
}
