namespace Repro.Core;

/// <summary>
/// Wire records for <see cref="Workflows.SimpleNoActivity"/>. Milliseconds as ints, not TimeSpan,
/// for the reason <see cref="JobInput"/> gives.
/// </summary>
/// <remarks>MaxDurationMs travels in the input rather than being read from config.yaml inside the
/// workflow, per <see cref="ActivityOptionsInput"/>.</remarks>
public record SimpleInput(int MaxDurationMs = 30_000);

/// <summary>Payload of the simple signal. The note is only there to be visible in history.</summary>
public record PokeInput(string Note = "");

/// <summary>Payload of the update that adds two integers and returns the sum.</summary>
public record AddInput(int A, int B);

/// <summary>
/// What a run returns. <c>EndedBy</c> is MetricNames.Outcomes.Stopped or .Expired, the same
/// strings as the metric's outcome tag, so a Grafana legend and `temporal workflow show` cannot
/// disagree.
/// </summary>
/// <remarks>No `canceled` value: a cancelled run throws instead of returning, which is the only
/// way the server records a status of Canceled.</remarks>
public record SimpleResult(string EndedBy, int Pokes, int Adds, int LastSum, int RanMs);

/// <summary>What the query hands back. Read-only view of the counters.</summary>
public record SimpleStatus(int Pokes, int Adds, int LastSum, bool StopRequested);
