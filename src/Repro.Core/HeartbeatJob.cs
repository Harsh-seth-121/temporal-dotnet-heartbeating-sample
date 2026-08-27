namespace Repro.Core;

/// <summary>Input to the seed workflow and its activity.</summary>
/// <param name="Steps">Number of units of work. Steps x StepDuration is roughly the activity's runtime.</param>
/// <param name="StepDurationMs">
/// Milliseconds per step. An int rather than a TimeSpan because this crosses the
/// payload converter and shows up in `temporal workflow show` output, where
/// "stepDurationMs": 1000 reads better than a serialized TimeSpan.
/// </param>
public record JobInput(int Steps, int StepDurationMs);

/// <summary>What the activity puts in each heartbeat.</summary>
/// <param name="Progress">Index of the last COMPLETED step. Resume starts at Progress + 1.</param>
/// <param name="RecordedAtUtc">
/// When the activity called Heartbeat(), not when the server received it.
/// </param>
/// <remarks>
/// The timestamp is load-bearing and is the reason this is a record rather than a
/// bare int. Core throttles heartbeats to min(HeartbeatTimeout x 0.8,
/// MaxHeartbeatThrottleInterval), so the details the server actually holds can be
/// up to that far behind what the activity has done. Comparing RecordedAtUtc to
/// now, on resume, is the only way to measure that staleness — and it is the single
/// most useful number this repo produces.
/// <para>
/// Heartbeat details must round-trip through the data converter. Anonymous types,
/// delegates and closures fail silently: the conversion error cancels the activity
/// with ActivityCancelReason.HeartbeatRecordFailure and then fails it. Records and
/// POCOs are safe.
/// </para>
/// </remarks>
public record Checkpoint(int Progress, DateTimeOffset RecordedAtUtc);
