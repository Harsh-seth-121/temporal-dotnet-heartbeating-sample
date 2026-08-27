using Repro.Core.Config;

namespace Repro.Core;

/// <summary>Input to the seed workflow and its activity.</summary>
/// <param name="Steps">Number of units of work. Steps x StepDuration is roughly the activity's runtime.</param>
/// <param name="StepDurationMs">
/// Milliseconds per step. An int rather than a TimeSpan because this crosses the
/// payload converter and shows up in `temporal workflow show` output, where
/// "stepDurationMs": 1000 reads better than a serialized TimeSpan.
/// </param>
/// <param name="Activity">
/// The timeouts and retry policy the workflow schedules the activity with. Optional
/// with a null default so a history captured before this field existed still
/// deserializes — see <see cref="ActivityOptionsInput"/> for why the fallback values
/// are not allowed to drift.
/// </param>
public record JobInput(int Steps, int StepDurationMs, ActivityOptionsInput? Activity = null);

/// <summary>The activity's timeouts and retry policy, carried in the workflow INPUT.</summary>
/// <remarks>
/// This record is the entire reason the <c>activity:</c> block in config.yaml is a
/// live knob instead of a description of a literal.
/// <para>
/// THE RULE PEOPLE INVERT: the determinism hazard is not "workflow code touched
/// config". It is "a replay of an old history must emit byte-identical commands".
/// Activity options are baked into the ScheduleActivityTask command at the instant
/// the activity is scheduled, so they only have to be stable FOR ONE EXECUTION.
/// Values that arrive in the input are in the history, so a replay reads back the
/// same bytes it wrote and is deterministic by construction. Values read from a
/// process global at replay time are not: edit config.yaml, restart the worker,
/// replay, and the commands stop matching. Passing them through the input is the
/// Temporal idiom, and it removes the objection rather than arguing with it.
/// </para>
/// <para>
/// Milliseconds as ints, matching <see cref="JobInput.StepDurationMs"/>, so
/// `temporal workflow show` prints "HeartbeatTimeoutMs": 5000 rather than a
/// serialized TimeSpan. (The default converter emits the CLR property names
/// verbatim — the input payload really is PascalCase on the wire.)
/// </para>
/// <para>
/// The defaults below are exactly the literals HeartbeatWorkflow used to hard-code.
/// They are what a null Activity falls back to, so histories in history/ that
/// predate this field still replay clean. Do not "tidy" them — change config.yaml.
/// </para>
/// </remarks>
public record ActivityOptionsInput(
    int HeartbeatTimeoutMs = 5_000,
    int StartToCloseTimeoutMs = 600_000,
    int ScheduleToCloseTimeoutMs = 3_600_000,
    int RetryInitialIntervalMs = 1_000,
    double RetryBackoffCoefficient = 2.0,
    int RetryMaximumIntervalMs = 10_000,
    int RetryMaximumAttempts = 5)
{
    /// <summary>Project config.yaml's <c>activity:</c> block onto the wire shape.</summary>
    /// <remarks>
    /// Call this in the STARTER, not in the workflow. The whole point is that the
    /// config read happens once, in client code, before the workflow exists.
    /// </remarks>
    public static ActivityOptionsInput From(ActivityConfig activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        return new ActivityOptionsInput(
            (int)activity.HeartbeatTimeout.TotalMilliseconds,
            (int)activity.StartToCloseTimeout.TotalMilliseconds,
            (int)activity.ScheduleToCloseTimeout.TotalMilliseconds,
            (int)activity.Retry.InitialInterval.TotalMilliseconds,
            activity.Retry.BackoffCoefficient,
            (int)activity.Retry.MaximumInterval.TotalMilliseconds,
            activity.Retry.MaximumAttempts);
    }
}

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
