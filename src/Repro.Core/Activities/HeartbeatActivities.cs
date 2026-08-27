using Microsoft.Extensions.Logging;
using Repro.Core.Config;
using Repro.Core.Telemetry;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Repro.Core.Activities;

/// <summary>
/// The seed activity: a long, resumable, heartbeating batch, plus the fault
/// injector that gives the dashboards something to show.
/// </summary>
/// <remarks>
/// Registered as an INSTANCE with
/// <c>.AddAllActivities(new HeartbeatActivities(cfg.Fault, cfg.Worker))</c>.
/// That is the .NET replacement for the Go original's package-level
/// <c>faultConfig</c> + <c>SetFaultConfig</c>: with constructor injection there is
/// no ambient global for workflow code to reach, so "workflows must never read the
/// fault config" is enforced by the type system rather than by a comment.
/// <para>
/// The WORKER config arrives the same way, for a duller reason: the throttle gauge
/// below has to report the intervals this worker was actually built with. Hard-coded
/// constants agree with them right up until someone edits config.yaml, and then the
/// gauge lies on a panel whose entire job is to explain the throttle.
/// </para>
/// <para>
/// Randomness and wall-clock are fine in here. This is activity code, not workflow code.
/// </para>
/// </remarks>
public sealed class HeartbeatActivities(FaultConfig fault, WorkerConfig? worker = null)
{
    // Optional so a caller that has not been taught to pass it still lands on the
    // SDK's own throttle defaults (60s/30s) rather than a null deref. Any worker
    // built from a config.yaml whose worker.*HeartbeatThrottleInterval differ MUST
    // pass its WorkerConfig here, or repro_heartbeat_throttle_ms reports a number
    // that nothing in the process is using.
    private readonly WorkerConfig workerConfig = worker ?? new WorkerConfig();

    [Activity]
    public async Task<int> ProcessBatchAsync(JobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Capture ONCE. ActivityExecutionContext.Current is an AsyncLocal lookup and
        // it throws outside an activity — which matters the moment you move a
        // heartbeat into a background timer or a Task.Run loop.
        var ctx = ActivityExecutionContext.Current;
        var meter = ctx.MetricMeter;
        var log = ctx.Logger;

        var heartbeatTimeout = ctx.Info.HeartbeatTimeout ?? TimeSpan.Zero;
        var stepDuration = TimeSpan.FromMilliseconds(input.StepDurationMs);

        // The REAL gap between two Heartbeat() calls, which is stepDuration PLUS the
        // injected latency: DoStepAsync awaits both before the loop reaches the
        // heartbeat. Publishing stepDuration alone under-reports the cadence by
        // exactly fault.latency (400 vs a measured 550 at the shipped 400ms/150ms),
        // which is a fine way to conclude the throttle is doing something it is not.
        var heartbeatCallInterval = stepDuration + fault.Latency;

        // These three gauges are what make the throttle legible on the heartbeat
        // board. They come from the ACTIVITY meter, never TemporalRuntime.MetricMeter:
        // the runtime meter carries no root tags, so those series would arrive with
        // no namespace/task_queue and every {namespace="$namespace"} selector on
        // every panel would silently drop them.
        meter.CreateGauge<long>(MetricNames.HeartbeatTimeoutMs, "ms")
            .Set((long)heartbeatTimeout.TotalMilliseconds);
        meter.CreateGauge<long>(MetricNames.HeartbeatCallIntervalMs, "ms")
            .Set((long)heartbeatCallInterval.TotalMilliseconds);
        meter.CreateGauge<long>(MetricNames.HeartbeatThrottleMs, "ms")
            .Set(ThrottleMs(heartbeatTimeout));

        // Resume. There is no HasHeartbeatDetails helper in .NET (unlike Go), and
        // HeartbeatDetailAtAsync uses ElementAt, which throws if the index is absent
        // — so the Count check is required, not defensive.
        Checkpoint? checkpoint = null;
        if (ctx.Info.HeartbeatDetails.Count > 0)
        {
            checkpoint = await ctx.Info.HeartbeatDetailAtAsync<Checkpoint>(0).ConfigureAwait(false);
        }

        var start = checkpoint is null ? 1 : checkpoint.Progress + 1;

        // LOWERCASE, via Bool(). .NET's bool.ToString() returns "True"/"False" with a
        // capital letter, while Go's fmt.Sprintf("%t") returns "true"/"false" — and
        // every dashboard selector, ported from the Go boards, matches retried="true".
        // Capitalized values do not error; the panel is just permanently empty.
        meter.WithTags(new Dictionary<string, object>
        {
            [MetricNames.Tags.Retried] = Bool(ctx.Info.Attempt > 1),
            [MetricNames.Tags.Resumed] = Bool(checkpoint is not null),
        }).CreateCounter<long>(MetricNames.ActivityStarted).Add(1);

        if (checkpoint is not null)
        {
            // THE number this repo exists to show. Core throttles heartbeats to
            // min(HeartbeatTimeout x 0.8, MaxHeartbeatThrottleInterval), so the
            // details the server holds lag what the activity actually did. This
            // measures that lag directly, and it is why resume must be idempotent:
            // some work WILL be redone.
            var staleness = DateTimeOffset.UtcNow - checkpoint.RecordedAtUtc;
            meter.CreateHistogram<TimeSpan>(MetricNames.HeartbeatStaleness).Record(staleness);
            log.LogInformation(
                "resuming at step {Start} of {Steps}; checkpoint was {StalenessMs}ms old (attempt {Attempt})",
                start, input.Steps, (long)staleness.TotalMilliseconds, ctx.Info.Attempt);
        }
        else
        {
            log.LogInformation("starting at step {Start} of {Steps} (attempt {Attempt})",
                start, input.Steps, ctx.Info.Attempt);
        }

        // LAST-WRITER-WINS, and left that way on purpose. The activity meter's root
        // tags are namespace/task_queue/activity_type, so this is ONE series per
        // process: at loadgen.concurrency 8 all eight in-flight activities write it
        // and the panel shows whichever wrote most recently. The only tags that would
        // separate them — workflow_id, run_id, activity_id — are unbounded, and a
        // gauge that ticks once per step is the last place to introduce unbounded
        // cardinality into the Prometheus you are debugging with. So read it as "some
        // activity on this worker reached step N", and watch it against a single
        // execution (the starter, or `--concurrency 1`) when you want the monotone
        // climb the README's stopHeartbeating recipe describes.
        var progressGauge = meter.CreateGauge<long>(MetricNames.ActivityProgress);
        var heartbeatCounter = meter.CreateCounter<long>(MetricNames.HeartbeatSent);

        // FAULT: fail this ATTEMPT, retryably, with probability fault.failureRate.
        //
        // ONE roll per attempt, outside the loop, exactly like the Go original
        // (activity.go: a single rand.Float64() with no loop to hide in). Rolling
        // per STEP instead makes P(attempt fails) = 1-(1-r)^steps: at the shipped
        // r=0.15 and steps=60 that is 99.99%, every attempt dies, all five retries
        // are consumed and the workflow FAILS terminally — the exact opposite of the
        // "retried but recovered" signal this knob is documented to produce.
        //
        // WHICH step it fails at is a second, independent draw, and it does not
        // affect that probability. It exists so the failure lands mid-batch, leaving
        // a checkpoint behind for the next attempt to resume from — that resume is
        // the only thing under the shipped config that makes repro_heartbeat_staleness
        // and resumed="true" fire at all.
        var failAtStep = fault.FailureRate > 0 && Random.Shared.NextDouble() < fault.FailureRate
            ? Random.Shared.Next(start, input.Steps + 1)
            : 0;

        // Declared out here so the ignoreCancellation recovery loop in the catch can
        // see how far we actually got. Scoped to the for-loop it was invisible down
        // there, so recovery restarted from `start`, redid finished work and walked
        // repro_activity_progress BACKWARDS on a gauge that is supposed to climb.
        var progress = start;

        // The drain checkpoint is an EDGE, not a level; see the loop.
        var checkpointedForDrain = false;

        try
        {
            // FAULT: stall past the heartbeat timeout, attempt 1 only.
            //
            // Every attempt would be an infinite retry loop that reads as a hang.
            // Two things happen in order and both are the point: the server's
            // activity-timeout timer fires and times this ATTEMPT out, and we keep
            // running regardless, because the only channel the server has to tell us
            // is the response to a heartbeat RPC and we are not sending any.
            //
            // It moves the heartbeat board's per-ATTEMPT panel —
            // activity_task_timeout{timeout_type="Heartbeat"} — and NOTHING ELSE.
            // The workflow outcome stays `completed`: attempt 2 is not gated, runs
            // normally, and succeeds well inside MaximumAttempts=5. For an outcome of
            // timed_out you want stopHeartbeating, below, which starves every attempt.
            if (fault.StallPastHeartbeatTimeout && ctx.Info.Attempt == 1)
            {
                var stall = heartbeatTimeout + TimeSpan.FromSeconds(2);
                log.LogWarning("FAULT stallPastHeartbeatTimeout: sleeping {StallMs}ms without heartbeating",
                    (long)stall.TotalMilliseconds);
                await Task.Delay(stall, ctx.CancellationToken).ConfigureAwait(false);
            }

            for (; progress <= input.Steps; progress++)
            {
                // WorkerShutdownToken fires FIRST, at shutdown start; CancellationToken
                // follows GracefulShutdownTimeout later. That gap is the only chance to
                // checkpoint, and taking it is what lets the restarted worker resume
                // near where this one stopped instead of at the last throttled heartbeat.
                //
                // ONCE, on the edge. The token stays signalled for the whole graceful
                // window, so an ungated branch re-fires every step and adds a bogus
                // heartbeat to repro_heartbeat_sent per step for up to
                // gracefulShutdownTimeout. There is also nothing to throw here: the
                // real CancellationToken has not fired yet, which made the
                // ThrowIfCancellationRequested that used to sit here a no-op for the
                // entire window. When it does fire, DoStepAsync is awaiting on it and
                // raises it from there.
                if (!checkpointedForDrain && ctx.WorkerShutdownToken.IsCancellationRequested)
                {
                    checkpointedForDrain = true;
                    log.LogInformation("worker draining; checkpointing at step {Progress}", progress - 1);
                    ctx.Heartbeat(new Checkpoint(progress - 1, DateTimeOffset.UtcNow));
                    heartbeatCounter.Add(1);
                }

                await DoStepAsync(stepDuration, ctx.CancellationToken).ConfigureAwait(false);

                if (progress == failAtStep)
                {
                    meter.CreateCounter<long>(MetricNames.ActivityFailed).Add(1);
                    throw new ApplicationFailureException(
                        $"injected failure at step {progress} on attempt {ctx.Info.Attempt}", "InjectedFault");
                }

                progressGauge.Set(progress);

                // FAULT: keep working, stop heartbeating. Proves an activity that
                // stops heartbeating can never be cancelled — and that its progress
                // gauge keeps climbing while the heartbeat RPC rate falls to zero.
                //
                // Unlike stallPastHeartbeatTimeout this is NOT gated to attempt 1, so
                // all five attempts heartbeat-time-out, the retry policy is exhausted,
                // and the terminal failure really is
                // ActivityFailure -> TimeoutFailure{Heartbeat}. THIS is the knob that
                // moves the signals board's outcome split to timed_out.
                if (!fault.StopHeartbeating)
                {
                    // Safe to call every iteration: Core throttles internally, so this
                    // does not hammer the server. The cost is staleness, measured above.
                    ctx.Heartbeat(new Checkpoint(progress, DateTimeOffset.UtcNow));

                    // Counted at the CALL SITE, before the throttle. Comparing this to
                    // rate(temporal_request{operation="RecordActivityTaskHeartbeat"})
                    // is what makes the throttle visible on the heartbeat board.
                    heartbeatCounter.Add(1);
                }
            }

            log.LogInformation("batch complete: {Steps} steps", input.Steps);
            return input.Steps;
        }
        catch (OperationCanceledException)
        {
            meter.WithTags(new Dictionary<string, object>
            {
                [MetricNames.Tags.Reason] = ctx.CancelReason.ToString(),
            }).CreateCounter<long>(MetricNames.ActivityCancel).Add(1);

            // FAULT: swallow the cancellation and finish anyway.
            //
            // The activity is only "cancelled" if this exception escapes. Swallowing
            // it means TemporalWorker.ExecuteAsync will not return until the batch
            // finishes on its own — and gracefulShutdownTimeout does NOT bound that,
            // it only controls when ctx.CancellationToken fires. This wedges your
            // terminal for the rest of the batch. That is the demo.
            if (fault.IgnoreCancellation)
            {
                log.LogWarning(
                    "FAULT ignoreCancellation: swallowing cancellation ({Reason}) and finishing the batch " +
                    "from step {Progress}. The worker will not exit until this returns.",
                    ctx.CancelReason, progress);

                // Resumes from `progress`, not from `start`. The step the cancellation
                // interrupted is redone (its await never completed); everything before
                // it is not.
                for (; progress <= input.Steps; progress++)
                {
                    await DoStepAsync(stepDuration, CancellationToken.None).ConfigureAwait(false);
                    progressGauge.Set(progress);
                }

                return input.Steps;
            }

            log.LogInformation("cancelled ({Reason})", ctx.CancelReason);
            throw;
        }
    }

    /// <summary>One unit of work, plus the injected latency.</summary>
    private async Task DoStepAsync(TimeSpan stepDuration, CancellationToken cancellationToken)
    {
        // Passing the token into every await is the mechanism by which cancellation
        // is observed at all. A synchronous CPU-bound loop here would heartbeat fine
        // and still be uncancellable.
        await Task.Delay(stepDuration, cancellationToken).ConfigureAwait(false);

        if (fault.Latency > TimeSpan.Zero)
        {
            await Task.Delay(fault.Latency, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Go-style lowercase booleans, because that is what the dashboards match on.</summary>
    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>
    /// Core's throttle: <c>min(HeartbeatTimeout x 0.8, MaxHeartbeatThrottleInterval)</c>,
    /// falling back to <c>DefaultHeartbeatThrottleInterval</c> when the timeout is 0 or unset.
    /// </summary>
    /// <remarks>
    /// Recomputed here rather than read from the SDK because Core does not expose it.
    /// The two intervals come from the SAME WorkerConfig the host used to set
    /// TemporalWorkerOptions, so the gauge cannot drift from the worker the way a
    /// pair of literals did.
    /// </remarks>
    private long ThrottleMs(TimeSpan heartbeatTimeout)
    {
        // There is a server bug that turns an unset heartbeat timeout into 0, which
        // is why Core treats 0 and unset identically instead of throttling at zero
        // and hammering the server.
        if (heartbeatTimeout <= TimeSpan.Zero)
        {
            return (long)workerConfig.DefaultHeartbeatThrottleInterval.TotalMilliseconds;
        }

        return (long)Math.Min(
            heartbeatTimeout.TotalMilliseconds * 0.8,
            workerConfig.MaxHeartbeatThrottleInterval.TotalMilliseconds);
    }
}
