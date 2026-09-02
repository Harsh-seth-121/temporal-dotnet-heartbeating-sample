using Repro.Core.Config;

namespace Repro.Core;

/// <summary>Input to the seed workflow and its activity.</summary>
/// <param name="Steps">Number of units of work. Steps x StepDuration is roughly the activity's runtime.</param>
/// <param name="StepDurationMs">Milliseconds per step. An int rather than a TimeSpan because this
/// crosses the payload converter and reads better in `temporal workflow show`. Every *Ms field in
/// this repo follows this.</param>
/// <param name="Activity">The activity's timeouts and retry policy. Null default so a history
/// captured before this field existed still deserializes; see
/// <see cref="ActivityOptionsInput"/>.</param>
public record JobInput(int Steps, int StepDurationMs, ActivityOptionsInput? Activity = null);

/// <summary>The activity's timeouts and retry policy, carried in the workflow input.</summary>
/// <remarks>
/// The canonical statement of why activity options travel in the workflow input; the other option
/// records point here. The determinism hazard is not "workflow code touched config", it is "a
/// replay of an old history must emit byte-identical commands". Activity options are baked into the
/// ScheduleActivityTask command when the activity is scheduled, so a value arriving in the input is
/// in the history and a replay reads back the bytes it wrote. See docs/CONFIG.md, "The activity.*
/// rows reach the workflow through its input, not through the file". The defaults below are what a
/// null Activity falls back to, so histories in history/ that predate this field still replay
/// clean; change config.yaml, not these. The converter emits CLR property names verbatim, so the
/// input payload is PascalCase on the wire.
/// </remarks>
public record ActivityOptionsInput(
    int HeartbeatTimeoutMs = 5_000,
    int StartToCloseTimeoutMs = 600_000,
    int ScheduleToCloseTimeoutMs = 3_600_000,
    int RetryInitialIntervalMs = 1_000,
    double RetryBackoffCoefficient = 2.0,
    int RetryMaximumIntervalMs = 10_000,
    int RetryMaximumAttempts = 5) : IRetryInput
{
    /// <summary>Project config.yaml's <c>activity:</c> block onto the wire shape.</summary>
    /// <remarks>Call this in the starter, not in the workflow: the config read happens once, in
    /// client code, before the workflow exists.</remarks>
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
/// <param name="Progress">Index of the last completed step. Resume starts at Progress + 1.</param>
/// <param name="RecordedAtUtc">When the activity called Heartbeat(), not when the server received
/// it.</param>
/// <remarks>
/// The timestamp is why this is a record rather than a bare int: Core throttles heartbeats, so the
/// details the server holds lag the activity, and comparing RecordedAtUtc to now on resume is the
/// only way to measure that. See docs/HEARTBEATING.md, "The throttle" and "Stale checkpoints".
/// Heartbeat details must round-trip through the data converter: anonymous types, delegates and
/// closures fail silently, cancelling the activity with
/// ActivityCancelReason.HeartbeatRecordFailure and then failing it.
/// </remarks>
public record Checkpoint(int Progress, DateTimeOffset RecordedAtUtc);
