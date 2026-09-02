using Repro.Core.Config;

namespace Repro.Core;

/// <summary>Input to <c>WorkflowLocalActivity</c> and its single local activity.</summary>
/// <param name="DurationMs">How long the activity burns CPU. Drawn per run by the loadgen from
/// <see cref="LocalActivityConfig.MinDuration"/>..<see cref="LocalActivityConfig.MaxDuration"/>,
/// then fixed for the life of the run. That fixedness is what makes this a repro rather than a
/// flake: a re-execution reads the same number and times out again.</param>
/// <param name="Seed">Seeds the activity's <c>System.Random</c>, so a captured history reproduces
/// its own Pi estimate exactly. Drawn client-side, like the duration. CA5394 is not enabled here;
/// RandomNumberGenerator has no seed and would destroy that reproducibility.</param>
/// <param name="Activity">The local activity's timeouts and retry policy. Null default so a history captured before this field existed still deserializes.</param>
/// <remarks>Job shape travels in the input, the rule <see cref="SimpleActivityInput"/> records, so
/// a replay reads back the bytes it wrote. Nothing here may be read from config.yaml inside
/// workflow code.</remarks>
public record LocalActivityInput(
    int DurationMs = 30_000,
    int Seed = 0,
    LocalActivityOptionsInput? Activity = null)
{
    /// <summary>Project config plus this run's draws onto the wire shape.</summary>
    /// <remarks>Takes the duration and seed as arguments rather than off the config, unlike
    /// <see cref="SimpleActivityInput.From"/>: both vary per run, the config only bounds them, and
    /// the draw belongs to the driver, which is client code and may use Random.Shared.</remarks>
    public static LocalActivityInput From(LocalActivityConfig localActivity, int durationMs, int seed)
    {
        ArgumentNullException.ThrowIfNull(localActivity);

        return new LocalActivityInput(
            DurationMs: durationMs,
            Seed: seed,
            Activity: LocalActivityOptionsInput.From(localActivity));
    }
}

/// <summary>The local activity's timeouts and retry policy, carried in the workflow input.</summary>
/// <remarks>
/// A separate record from <see cref="SimpleActivityOptionsInput"/> because
/// <c>LocalActivityOptions</c> has no HeartbeatTimeout at all, not unset but absent from the type.
/// The ladder is mostly decorative: <see cref="StartToCloseTimeoutMs"/> is unreachable, since the
/// activity is wall-clock capped at maxDuration (2m shipped) and the server kills the workflow task
/// at 1m, and it is set only because the SDK requires one of the two timeouts. Neither
/// <see cref="ScheduleToCloseTimeoutMs"/> nor <see cref="RetryMaximumAttempts"/> bounds the
/// re-execution loop, so only <c>WorkflowOptions.RunTimeout</c> ends an over-long run. See
/// docs/GOTCHAS.md, "Heartbeating has no effect on a local activity", "A local activity's
/// scheduleToCloseTimeout does not bound a re-execution loop" and "An unset RetryPolicy on a local
/// activity means retry FOREVER", plus docs/CONFIG.md, "The timeout ladder is mostly decorative, on
/// purpose".
/// </remarks>
public record LocalActivityOptionsInput(
    int StartToCloseTimeoutMs = 150_000,
    int ScheduleToCloseTimeoutMs = 300_000,
    int RetryInitialIntervalMs = 1_000,
    double RetryBackoffCoefficient = 2.0,
    int RetryMaximumIntervalMs = 10_000,
    int RetryMaximumAttempts = 1) : IRetryInput
{
    /// <summary>Projects the config block's timeouts and retry policy onto this record.</summary>
    /// <remarks>Call it in client code. The result travels in the workflow input and is recorded
    /// in history, so a replay uses the numbers the run started with, not today's
    /// config.yaml.</remarks>
    public static LocalActivityOptionsInput From(LocalActivityConfig localActivity)
    {
        ArgumentNullException.ThrowIfNull(localActivity);

        // Named, for the reason SimpleActivityOptionsInput.From records. Swapping the adjacent
        // RetryMaximumIntervalMs and RetryMaximumAttempts gives 10,000 CPU burns.
        return new LocalActivityOptionsInput(
            StartToCloseTimeoutMs: (int)localActivity.StartToCloseTimeout.TotalMilliseconds,
            ScheduleToCloseTimeoutMs: (int)localActivity.ScheduleToCloseTimeout.TotalMilliseconds,
            RetryInitialIntervalMs: (int)localActivity.Retry.InitialInterval.TotalMilliseconds,
            RetryBackoffCoefficient: localActivity.Retry.BackoffCoefficient,
            RetryMaximumIntervalMs: (int)localActivity.Retry.MaximumInterval.TotalMilliseconds,
            RetryMaximumAttempts: localActivity.Retry.MaximumAttempts);
    }
}

/// <summary>What the local activity returns, and therefore what lands in the marker.</summary>
/// <param name="Pi">The estimate. 4 x (points inside the unit quarter-circle / total points).</param>
/// <param name="Iterations">Points sampled. Varies with machine speed, because the loop is time-bounded.</param>
/// <param name="Inside">Points that fell inside. Kept so <paramref name="Pi"/> is checkable by hand.</param>
/// <param name="RequestedMs">What the input asked for. Present so the payload is self-describing.</param>
/// <param name="ElapsedMs">What it actually took, by Stopwatch.</param>
/// <param name="IterationsPerSecond">Derived, and a payload field rather than a metric because HistogramBuckets is in milliseconds.</param>
/// <param name="Attempt"><c>ActivityInfo.Attempt</c> as observed. Reads 1 even on a re-execution after a workflow task timeout, because that is a fresh execution rather than a retry.</param>
/// <param name="IsLocal"><c>ActivityInfo.IsLocal</c>. A local activity leaves no ActivityTaskScheduled event to check, only a MarkerRecorded named <c>core_local_activity</c>.</param>
/// <param name="EndedBy"><c>completed</c> or <c>shutdown</c>. See <c>MetricNames.Endings</c>.</param>
/// <remarks>A replay-visible schema keyed on names, not positions; see
/// <see cref="WeatherReading"/>. Three adjacent <c>long</c>s and two adjacent <c>int</c>s here, so
/// positional construction would report the requested duration as the measured one. Every
/// construction site uses named arguments.</remarks>
public record PiEstimate(
    double Pi = 0,
    long Iterations = 0,
    long Inside = 0,
    int RequestedMs = 0,
    int ElapsedMs = 0,
    long IterationsPerSecond = 0,
    int Attempt = 0,
    bool IsLocal = false,
    string EndedBy = "");
