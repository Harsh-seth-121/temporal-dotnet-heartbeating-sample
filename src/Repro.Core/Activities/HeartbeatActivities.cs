using Microsoft.Extensions.Logging;
using Repro.Core.Config;
using Repro.Core.Telemetry;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Repro.Core.Activities;

/// <summary>
/// The seed activity: a long, resumable, heartbeating batch, plus the fault injector that
/// gives the dashboards something to show.
/// </summary>
/// <remarks>
/// Registered as an instance with
/// <c>.AddAllActivities(new HeartbeatActivities(cfg.Fault, cfg.Worker))</c>. Constructor
/// injection leaves no ambient global for workflow code to reach, so "workflows must never
/// read the fault config" is a type-system rule. Canonical site for that argument. The worker
/// config arrives the same way so <see cref="ThrottleMs"/>'s gauge reports the intervals this
/// worker was built with. Randomness and wall clock are fine here: this is activity code.
/// </remarks>
public sealed class HeartbeatActivities(FaultConfig fault, WorkerConfig? worker = null)
{
    // Optional so a caller that omits it lands on the SDK's throttle defaults (60s/30s). A
    // worker whose config.yaml sets different worker.*HeartbeatThrottleInterval values must
    // pass its WorkerConfig, or repro_heartbeat_throttle_ms reports an unused number.
    private readonly WorkerConfig workerConfig = worker ?? new WorkerConfig();

    [Activity]
    public async Task<int> ProcessBatchAsync(JobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Capture once. Canonical note for all four activities: ActivityExecutionContext.Current
        // is an AsyncLocal lookup that throws outside an activity and does not flow into a
        // Task.Run, a Parallel.For, a continuation or a background timer.
        var ctx = ActivityExecutionContext.Current;
        var meter = ctx.MetricMeter;
        var log = ctx.Logger;

        var heartbeatTimeout = ctx.Info.HeartbeatTimeout ?? TimeSpan.Zero;
        var stepDuration = TimeSpan.FromMilliseconds(input.StepDurationMs);

        // The real gap between Heartbeat() calls: DoStepAsync awaits step plus injected
        // latency. stepDuration alone under-reports it by fault.latency, 400 against a
        // measured 550 at the shipped 400ms/150ms.
        var heartbeatCallInterval = stepDuration + fault.Latency;

        // Activity meter, never TemporalRuntime.MetricMeter: the runtime meter carries no
        // root tags, so every {namespace="$namespace"} panel selector would drop these.
        meter.CreateGauge<long>(MetricNames.HeartbeatTimeoutMs, "ms")
            .Set((long)heartbeatTimeout.TotalMilliseconds);
        meter.CreateGauge<long>(MetricNames.HeartbeatCallIntervalMs, "ms")
            .Set((long)heartbeatCallInterval.TotalMilliseconds);
        meter.CreateGauge<long>(MetricNames.HeartbeatThrottleMs, "ms")
            .Set(ThrottleMs(heartbeatTimeout));

        // The Count check is required, not defensive: .NET has no HasHeartbeatDetails helper
        // and HeartbeatDetailAtAsync uses ElementAt, which throws when the index is absent.
        Checkpoint? checkpoint = null;
        if (ctx.Info.HeartbeatDetails.Count > 0)
        {
            checkpoint = await ctx.Info.HeartbeatDetailAtAsync<Checkpoint>(0).ConfigureAwait(false);
        }

        var start = checkpoint is null ? 1 : checkpoint.Progress + 1;

        // Lowercased by hand via Bool(). bool.ToString() returns "True", dashboard selectors
        // match retried="true", and a capitalized value leaves the panel empty without error.
        meter.WithTags(new Dictionary<string, object>
        {
            [MetricNames.Tags.Retried] = Bool(ctx.Info.Attempt > 1),
            [MetricNames.Tags.Resumed] = Bool(checkpoint is not null),
        }).CreateCounter<long>(MetricNames.ActivityStarted).Add(1);

        if (checkpoint is not null)
        {
            // The number this repo exists to show. Core throttles heartbeats (see
            // ThrottleMs), so the server's details lag the activity and some work is redone.
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

        // Last-writer-wins, deliberately: one series per process, so at loadgen.concurrency 8
        // all eight in-flight activities write it and the only separating tags (workflow_id,
        // run_id, activity_id) are unbounded. Watch a single execution for the monotone climb.
        var progressGauge = meter.CreateGauge<long>(MetricNames.ActivityProgress);
        var heartbeatCounter = meter.CreateCounter<long>(MetricNames.HeartbeatSent);

        // Fault: fail this attempt, retryably, with probability fault.failureRate. One roll
        // per attempt, outside the loop, because rolling per step makes
        // P(attempt fails) = 1-(1-r)^steps: at the shipped r=0.15 and steps=60 that is 99.99%,
        // every attempt dies and the workflow fails terminally, the opposite of the "retried
        // but recovered" signal. Which step it fails at is an independent draw that leaves a
        // checkpoint mid-batch, and that resume is the only thing under the shipped config
        // that makes staleness and resumed="true" fire.
        var failAtStep = fault.FailureRate > 0 && Random.Shared.NextDouble() < fault.FailureRate
            ? Random.Shared.Next(start, input.Steps + 1)
            : 0;

        // Declared out here so the ignoreCancellation recovery loop can see how far we got.
        // Scoped to the for-loop, recovery restarted from `start` and walked
        // repro_activity_progress backwards on a gauge that is supposed to climb.
        var progress = start;

        // The drain checkpoint is an edge, not a level; see the loop.
        var checkpointedForDrain = false;

        try
        {
            // Fault: stall past the heartbeat timeout, attempt 1 only; on every attempt it
            // would be an infinite retry loop that reads as a hang. The server times this
            // attempt out while we keep running, because its only channel to tell us is the
            // response to a heartbeat RPC and we send none. That moves
            // activity_task_timeout{timeout_type="Heartbeat"} and nothing else: the outcome
            // stays completed because attempt 2 is not gated. For timed_out use
            // stopHeartbeating, below.
            if (fault.StallPastHeartbeatTimeout && ctx.Info.Attempt == 1)
            {
                var stall = heartbeatTimeout + TimeSpan.FromSeconds(2);
                log.LogWarning("FAULT stallPastHeartbeatTimeout: sleeping {StallMs}ms without heartbeating",
                    (long)stall.TotalMilliseconds);
                await Task.Delay(stall, ctx.CancellationToken).ConfigureAwait(false);
            }

            for (; progress <= input.Steps; progress++)
            {
                // WorkerShutdownToken fires at shutdown start, CancellationToken follows
                // GracefulShutdownTimeout later. That gap is the only chance to checkpoint,
                // and taking it lets the restarted worker resume near where this one stopped.
                // Gated on the edge because the token stays signalled for the whole window, so
                // an ungated branch adds a bogus repro_heartbeat_sent tick every step. Nothing
                // to throw here: the real CancellationToken has not fired yet.
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

                // Fault: keep working, stop heartbeating. See docs/GOTCHAS.md, "An activity
                // that does not heartbeat can never be cancelled". Not gated to attempt 1, so
                // all five attempts heartbeat-time-out and the terminal failure is
                // ActivityFailure -> TimeoutFailure{Heartbeat}, which is what moves the
                // outcome split to timed_out.
                if (!fault.StopHeartbeating)
                {
                    // Safe every iteration: Core throttles internally. The cost is the
                    // staleness measured above.
                    ctx.Heartbeat(new Checkpoint(progress, DateTimeOffset.UtcNow));

                    // Counted at the call site, before the throttle. Compared against
                    // rate(temporal_request{operation="RecordActivityTaskHeartbeat"}) it is
                    // what makes the throttle visible.
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

            // Fault: swallow the cancellation and finish anyway. The activity is cancelled
            // only if this exception escapes, so TemporalWorker.ExecuteAsync will not return
            // until the batch finishes; gracefulShutdownTimeout only controls when
            // ctx.CancellationToken fires.
            if (fault.IgnoreCancellation)
            {
                log.LogWarning(
                    "FAULT ignoreCancellation: swallowing cancellation ({Reason}) and finishing the batch " +
                    "from step {Progress}. The worker will not exit until this returns.",
                    ctx.CancelReason, progress);

                // Resumes from `progress`, not `start`. The interrupted step is redone
                // because its await never completed; everything before it is not.
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
        // The token in every await is how cancellation is observed. A synchronous CPU-bound
        // loop here would heartbeat fine and still be uncancellable.
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
    /// <remarks>Recomputed because Core does not expose it. Both intervals come from the
    /// same WorkerConfig the host used to set TemporalWorkerOptions, so it cannot drift.</remarks>
    private long ThrottleMs(TimeSpan heartbeatTimeout)
    {
        // A server bug turns an unset heartbeat timeout into 0, so Core treats 0 and unset
        // identically rather than throttling at zero and hammering the server.
        if (heartbeatTimeout <= TimeSpan.Zero)
        {
            return (long)workerConfig.DefaultHeartbeatThrottleInterval.TotalMilliseconds;
        }

        return (long)Math.Min(
            heartbeatTimeout.TotalMilliseconds * 0.8,
            workerConfig.MaxHeartbeatThrottleInterval.TotalMilliseconds);
    }
}
