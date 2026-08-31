namespace Repro.Core;

/// <summary>
/// Wire records for <see cref="Workflows.SimpleNoActivity"/>. Milliseconds as ints, not
/// TimeSpan, for the same reason as <see cref="JobInput"/>: `temporal workflow show`
/// prints them as numbers you can read instead of ISO-8601 duration strings.
/// </summary>
/// <remarks>
/// MaxDurationMs travels in the INPUT rather than being read from config.yaml inside the
/// workflow, and that is not a style choice. A replay of an old history has to emit
/// byte-identical commands; a file that can be edited between the original execution and
/// the replay cannot promise that, but a value that arrives in the input is stable by
/// construction because replay reads back the same bytes it wrote. Same argument as
/// <see cref="ActivityOptionsInput"/> -- see the remarks on
/// HeartbeatWorkflow.BuildActivityOptions.
/// </remarks>
public record SimpleInput(int MaxDurationMs = 30_000);

/// <summary>Payload of the simple signal. The note is only there to be visible in history.</summary>
public record PokeInput(string Note = "");

/// <summary>Payload of the update that adds two integers and returns the sum.</summary>
public record AddInput(int A, int B);

/// <summary>
/// What a run returns. <c>EndedBy</c> is one of MetricNames.Outcomes.Stopped or
/// .Expired -- deliberately the SAME strings as the metric's outcome tag, so a Grafana
/// legend and `temporal workflow show` can never disagree about what happened.
/// </summary>
/// <remarks>
/// There is no `canceled` value here: a cancelled run throws instead of returning, which
/// is the only way the server records a status of Canceled.
/// </remarks>
public record SimpleResult(string EndedBy, int Pokes, int Adds, int LastSum, int RanMs);

/// <summary>What the query hands back. Read-only view of the counters.</summary>
public record SimpleStatus(int Pokes, int Adds, int LastSum, bool StopRequested);
