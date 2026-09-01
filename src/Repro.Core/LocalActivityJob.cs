using Repro.Core.Config;

namespace Repro.Core;

/// <summary>Input to <c>WorkflowLocalActivity</c> and its single LOCAL activity.</summary>
/// <param name="DurationMs">
/// How long the activity burns CPU, in milliseconds. Drawn PER RUN by the loadgen from
/// <see cref="LocalActivityConfig.MinDuration"/>..<see cref="LocalActivityConfig.MaxDuration"/>
/// and then fixed for the life of the run.
/// <para>
/// THE FIXEDNESS IS THE POINT, and it is what makes this a repro rather than a flake. When
/// the workflow task heartbeat timeout fires, the local activity re-executes from the
/// beginning -- and it reads this same number out of the same input, so it takes just as
/// long and times out again. A duration re-drawn per attempt would converge to a short one
/// eventually and the case would heal itself.
/// </para>
/// </param>
/// <param name="Seed">
/// Seeds the activity's <c>System.Random</c>, so a captured history reproduces its own Pi
/// estimate exactly. Drawn client-side, like the duration.
/// <para>
/// CA5394 (insecure randomness) is NOT enabled in this repo -- verified against
/// AnalysisMode=Recommended -- so this stays a plain seeded Random. Do not "fix" it into
/// RandomNumberGenerator: that has no seed and would destroy the reproducibility this
/// field exists for.
/// </para>
/// </param>
/// <param name="Activity">
/// The timeouts and retry policy the workflow schedules the LOCAL activity with. Optional
/// with a null default so a history captured before this field existed still deserializes.
/// </param>
/// <remarks>
/// Job shape travels in the INPUT, the same rule <see cref="SimpleActivityInput"/> records:
/// the values are then in the history and a replay reads back the bytes it wrote. Nothing
/// here may be read from config.yaml inside workflow code.
/// </remarks>
public record LocalActivityInput(
    int DurationMs = 30_000,
    int Seed = 0,
    LocalActivityOptionsInput? Activity = null)
{
    /// <summary>Project config plus this run's draws onto the wire shape.</summary>
    /// <remarks>
    /// Takes the duration and seed as ARGUMENTS rather than reading them off the config,
    /// which is where this differs from <see cref="SimpleActivityInput.From"/>. Both vary
    /// per run, the config only bounds them, and the draw belongs to the driver, which is
    /// client code and may use Random.Shared.
    /// </remarks>
    public static LocalActivityInput From(LocalActivityConfig localActivity, int durationMs, int seed)
    {
        ArgumentNullException.ThrowIfNull(localActivity);

        return new LocalActivityInput(
            DurationMs: durationMs,
            Seed: seed,
            Activity: LocalActivityOptionsInput.From(localActivity));
    }
}

/// <summary>The local activity's timeouts and retry policy, carried in the workflow INPUT.</summary>
/// <remarks>
/// A SEPARATE record from <see cref="SimpleActivityOptionsInput"/>, and the reason is the
/// whole design. <c>LocalActivityOptions</c> has NO HeartbeatTimeout at all -- not "unset",
/// absent from the type -- which is the structural reason heartbeating does not apply here.
/// It does have <see cref="ScheduleToCloseTimeoutMs"/>, which the regular options object in
/// this repo deliberately leaves unset.
/// <para>
/// READ THE LADDER BEFORE CHANGING A NUMBER. Only one of these rungs can actually fire at
/// the shipped config, and it is not the one most readers expect:
/// </para>
/// <para>
/// <see cref="StartToCloseTimeoutMs"/> is DELIBERATELY UNREACHABLE. The activity is wall-clock
/// capped at maxDuration (2m shipped) and the server kills the workflow task at 1m, so a
/// single attempt can never reach 2m30s. It is set because the SDK requires one of the two
/// timeouts, and it is documented as unreachable rather than pretended to be a guard.
/// </para>
/// <para>
/// <see cref="ScheduleToCloseTimeoutMs"/> DOES NOT BOUND THE RE-EXECUTION LOOP, which is the
/// single most counter-intuitive thing in this file. Its clock RESTARTS on every workflow-task
/// re-dispatch. In sdk-core the schedule command does
/// <c>original_schedule_time.get_or_insert(SystemTime::now())</c> on every fresh schedule,
/// and the only durable write of that value is inside the MARKER, guarded by
/// <c>if record_marker</c>. A local activity killed by a workflow task timeout never
/// resolved, so no marker exists, nothing is persisted, and the next dispatch starts a new
/// clock. Eviction then sends <c>InvalidateRun</c> and <c>Drop for TimeoutBag</c> aborts the
/// schedule-to-close handle outright. The proto's <c>original_schedule_time</c> is carried
/// only through <c>DoBackoff</c>, i.e. timer-based retry backoff, which is a different path
/// and the only one sdk-core's own test covers.
/// </para>
/// <para>
/// So the ONLY thing that ends a run whose duration exceeds the heartbeat timeout is
/// <c>WorkflowOptions.RunTimeout</c>, enforced server-side on the timer queue. See
/// <c>LocalActivityConfig.RunTimeout</c>.
/// </para>
/// <para>
/// <see cref="RetryMaximumAttempts"/> does not bound it either. A workflow-task-timeout
/// re-execution is not a retry: it arrives as attempt 1 again
/// (<c>explicit_attempt_num_or_1 = n.schedule_cmd.attempt.max(1)</c>), outside the retry
/// policy entirely. It must still be non-zero, because an unset RetryPolicy on a local
/// activity means retry FOREVER, which is a stronger default than the regular activity path.
/// </para>
/// </remarks>
public record LocalActivityOptionsInput(
    int StartToCloseTimeoutMs = 150_000,
    int ScheduleToCloseTimeoutMs = 300_000,
    int RetryInitialIntervalMs = 1_000,
    double RetryBackoffCoefficient = 2.0,
    int RetryMaximumIntervalMs = 10_000,
    int RetryMaximumAttempts = 1)
{
    /// <inheritdoc cref="LocalActivityInput.From"/>
    public static LocalActivityOptionsInput From(LocalActivityConfig localActivity)
    {
        ArgumentNullException.ThrowIfNull(localActivity);

        // NAMED, for the reason SimpleActivityOptionsInput.From records:
        // RetryMaximumIntervalMs and RetryMaximumAttempts are ADJACENT ints, and swapping
        // them positionally compiles clean and gives a 1ms maximum interval with 10,000
        // attempts. Here that would be 10,000 CPU burns rather than 10,000 HTTP requests.
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
/// <param name="IterationsPerSecond">
/// Derived, and the reason it is a FIELD rather than a metric. This case has no throughput
/// histogram: HistogramBuckets declares itself to be in milliseconds and an iterations/second
/// row would make that header false. The number is genuinely useful for spotting CPU
/// contention, so it lives in the payload, where `temporal workflow show` prints it.
/// </param>
/// <param name="Attempt">
/// <c>ActivityInfo.Attempt</c> as observed. EXPECTED to read 1 even on a re-execution after a
/// workflow task timeout, because that is a fresh execution rather than a retry. This field
/// exists so the history proves it rather than a comment claiming it.
/// </param>
/// <param name="IsLocal">
/// <c>ActivityInfo.IsLocal</c>. Puts "this really did run as a local activity" in the payload,
/// which matters because a local activity leaves no ActivityTaskScheduled event to check
/// against -- only a MarkerRecorded whose marker name is <c>core_local_activity</c>.
/// </param>
/// <param name="EndedBy">
/// <c>completed</c> or <c>shutdown</c>. See <c>MetricNames.Endings</c>.
/// </param>
/// <remarks>
/// AN ACTIVITY'S RETURN RECORD IS A REPLAY-VISIBLE SCHEMA and the contract is about NAMES,
/// not positions. <see cref="WeatherReading"/>'s remarks carry the measurements for that;
/// they are not repeated here. The short version is that RENAMING a parameter binds nothing
/// and yields <c>default(T)</c> with every fixture still reporting "replay OK".
/// <para>
/// The hazard specific to THIS record is the three adjacent <c>long</c>s
/// (<paramref name="Iterations"/>, <paramref name="Inside"/>, then later
/// <paramref name="IterationsPerSecond"/>) and the two adjacent <c>int</c>s
/// (<paramref name="RequestedMs"/>, <paramref name="ElapsedMs"/>). Positional construction
/// compiles clean and silently reports the requested duration as the measured one. Every
/// construction site uses NAMED arguments.
/// </para>
/// </remarks>
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
