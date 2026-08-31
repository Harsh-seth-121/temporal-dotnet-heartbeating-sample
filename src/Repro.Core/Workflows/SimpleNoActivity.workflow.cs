using Microsoft.Extensions.Logging;
using Repro.Core.Telemetry;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Repro.Core.Workflows;

/// <summary>
/// The message-passing case: no activities at all. It starts, waits, and ends one of
/// three ways -- a Stop signal, its own MaxDurationMs, or a real cancellation request
/// from a client.
/// </summary>
/// <remarks>
/// WHY THERE IS NO "cancel myself" PATH. A workflow cannot put itself into CANCELED
/// status. The server only records that status when a cancellation REQUEST exists;
/// throwing CanceledFailureException unprompted records Failed, and signalling yourself
/// is refused by the server outright. So the Stop signal ends the run as Completed with
/// EndedBy="stopped", and a genuine Canceled comes from the client calling
/// handle.CancelAsync() -- which lands here as Workflow.CancellationToken firing inside
/// WaitConditionAsync. See docs/GOTCHAS.md.
/// </remarks>
[Workflow]
public class SimpleNoActivity
{
    // No initializers. CA1805 ("do not initialize unnecessarily") is an ERROR under this
    // repo's TreatWarningsAsErrors, so `= false` / `= 0` will not compile.
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
            // The cancellationToken argument is UNSET on purpose: it defaults to
            // Workflow.CancellationToken, so a client's CancelAsync() raises out of this
            // await with no extra plumbing. That default is the entire cancel path.
            stopped = await Workflow.WaitConditionAsync(
                () => stopRequested,
                TimeSpan.FromMilliseconds(input.MaxDurationMs)).ConfigureAwait(true);
        }
        catch (Exception e) when (TemporalException.IsCanceledException(e))
        {
            // IsCanceledException, not `catch (OperationCanceledException)`: cancellation
            // reaches workflow code as OperationCanceledException OR CanceledFailureException
            // depending on where you await, and this helper is the only reliable test.
            Workflow.Logger.LogInformation(
                "cancel requested after {Pokes} pokes and {Adds} adds", pokes, adds);
            Record(meter, MetricNames.Outcomes.Canceled, start);

            // RETHROW. Swallowing this returns a value, and the server then records
            // Completed for a run the operator explicitly cancelled. The rethrow is the
            // only thing that produces a real Canceled status.
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
    /// <remarks>
    /// NOT async, and that is deliberate. `async Task PokeAsync(...) => pokes++;` raises
    /// CS1998 ("async method lacks await"), which TreatWarningsAsErrors turns into a build
    /// failure. The SDK validates only the RETURN TYPE of a signal handler, so returning
    /// Task.CompletedTask from a plain method is fully supported. Do not "fix" this back.
    /// </remarks>
    [WorkflowSignal]
    public Task PokeAsync(PokeInput input)
    {
        pokes++;
        Workflow.Logger.LogDebug("poke {Pokes}: {Note}", pokes, input.Note);
        CountMessage(MetricNames.Kinds.Poke);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Ask the run to end. Wire name: <c>Stop</c>. See the class remarks for why this
    /// produces Completed rather than Canceled.
    /// </summary>
    [WorkflowSignal]
    public Task StopAsync()
    {
        stopRequested = true;
        return Task.CompletedTask;
    }

    /// <summary>The simple handler: read state, change nothing. Wire name: <c>GetStatus</c>.</summary>
    /// <remarks>
    /// Queries are NOT trimmed of an Async suffix the way signals and updates are, and a
    /// query handler may not return a Task. Naming it GetStatusAsync would give you a
    /// query literally called "GetStatusAsync" on the wire.
    /// </remarks>
    [WorkflowQuery]
    public SimpleStatus GetStatus() => new(pokes, adds, lastSum, stopRequested);

    /// <summary>Rejects an Add whose sum does not fit in an int, before it reaches history.</summary>
    /// <remarks>
    /// A validator is the only way to refuse a message without writing anything to the
    /// event history: throw here and no WorkflowExecutionUpdateAccepted event is ever
    /// recorded. It must be void, non-static, and take exactly the handler's parameters.
    /// It must also be side-effect free, which is why nothing is counted here.
    /// </remarks>
    [WorkflowUpdateValidator(nameof(AddAsync))]
    public void ValidateAdd(AddInput input)
    {
        // CAST FIRST. `(long)(input.A + input.B)` adds two ints in an unchecked context,
        // wraps, and then widens the already-wrong answer -- the guard would never fire.
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
            // namespace / task_queue / workflow_type are already root tags on
            // Workflow.MetricMeter. Re-adding them would duplicate labels.
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

        // CreateHistogram<TimeSpan> maps to Core's HistogramDuration kind, so the value
        // follows UseSecondsForDuration automatically.
        tagged.CreateHistogram<TimeSpan>(MetricNames.SimpleLatency)
            .Record(Workflow.UtcNow - start);
    }
}
