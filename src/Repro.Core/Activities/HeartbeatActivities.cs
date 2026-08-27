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
/// Registered as an INSTANCE with <c>.AddAllActivities(new HeartbeatActivities(cfg.Fault))</c>.
/// That is the .NET replacement for the Go original's package-level
/// <c>faultConfig</c> + <c>SetFaultConfig</c>: with constructor injection there is
/// no ambient global for workflow code to reach, so "workflows must never read the
/// fault config" is enforced by the type system rather than by a comment.
/// <para>
/// Randomness and wall-clock are fine in here. This is activity code, not workflow code.
/// </para>
/// </remarks>
public sealed class HeartbeatActivities(FaultConfig fault)
{
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

        // These three gauges are what make the throttle legible on the heartbeat
        // board. They come from the ACTIVITY meter, never TemporalRuntime.MetricMeter:
        // the runtime meter carries no root tags, so those series would arrive with
        // no namespace/task_queue and every {namespace="$namespace"} selector on
        // every panel would silently drop them.
        meter.CreateGauge<long>(MetricNames.HeartbeatTimeoutMs, "ms")
            .Set((long)heartbeatTimeout.TotalMilliseconds);
        meter.CreateGauge<long>(MetricNames.HeartbeatCallIntervalMs, "ms")
            .Set((long)stepDuration.TotalMilliseconds);
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

        var progressGauge = meter.CreateGauge<long>(MetricNames.ActivityProgress);
        var heartbeatCounter = meter.CreateCounter<long>(MetricNames.HeartbeatSent);

        try
        {
            // FAULT: stall past the heartbeat timeout, attempt 1 only.
            //
            // Every attempt would be an infinite retry loop that reads as a hang.
            // Two things happen in order and both are the point: the server's
            // activity-timeout timer fires and times this ATTEMPT out, and we keep
            // running regardless, because the only channel the server has to tell us
            // is the response to a heartbeat RPC and we are not sending any.
            // Panels: heartbeat board "Heartbeat timeouts", and the signals board's
            // outcome split shifting toward timed_out.
            if (fault.StallPastHeartbeatTimeout && ctx.Info.Attempt == 1)
            {
                var stall = heartbeatTimeout + TimeSpan.FromSeconds(2);
                log.LogWarning("FAULT stallPastHeartbeatTimeout: sleeping {StallMs}ms without heartbeating",
                    (long)stall.TotalMilliseconds);
                await Task.Delay(stall, ctx.CancellationToken).ConfigureAwait(false);
            }

            for (var progress = start; progress <= input.Steps; progress++)
            {
                // WorkerShutdownToken fires FIRST, at shutdown start; CancellationToken
                // follows GracefulShutdownTimeout later. That gap is the only chance to
                // checkpoint, and taking it is what lets the restarted worker resume
                // near where this one stopped instead of at the last throttled heartbeat.
                if (ctx.WorkerShutdownToken.IsCancellationRequested)
                {
                    log.LogInformation("worker draining; checkpointing at step {Progress}", progress - 1);
                    ctx.Heartbeat(new Checkpoint(progress - 1, DateTimeOffset.UtcNow));
                    heartbeatCounter.Add(1);
                    ctx.CancellationToken.ThrowIfCancellationRequested();
                }

                await DoStepAsync(stepDuration, ctx.CancellationToken).ConfigureAwait(false);

                // FAULT: fail a fraction of attempts, retryably. Drives the retry and
                // failure panels without producing a terminal failure.
                if (fault.FailureRate > 0 && Random.Shared.NextDouble() < fault.FailureRate)
                {
                    meter.CreateCounter<long>(MetricNames.ActivityFailed).Add(1);
                    throw new ApplicationFailureException(
                        $"injected failure at step {progress} on attempt {ctx.Info.Attempt}", "InjectedFault");
                }

                progressGauge.Set(progress);

                // FAULT: keep working, stop heartbeating. Proves an activity that
                // stops heartbeating can never be cancelled — and that its progress
                // gauge keeps climbing while the heartbeat RPC rate falls to zero.
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
                    "FAULT ignoreCancellation: swallowing cancellation ({Reason}) and finishing the batch. " +
                    "The worker will not exit until this returns.", ctx.CancelReason);

                for (var progress = start; progress <= input.Steps; progress++)
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
    /// If the worker's throttle options are changed away from their defaults, update
    /// the constants below or this gauge quietly lies.
    /// </remarks>
    private static long ThrottleMs(TimeSpan heartbeatTimeout)
    {
        const double maxThrottleMs = 60_000;      // TemporalWorkerOptions.MaxHeartbeatThrottleInterval
        const double defaultThrottleMs = 30_000;  // TemporalWorkerOptions.DefaultHeartbeatThrottleInterval

        // There is a server bug that turns an unset heartbeat timeout into 0, which
        // is why Core treats 0 and unset identically instead of throttling at zero
        // and hammering the server.
        if (heartbeatTimeout <= TimeSpan.Zero)
        {
            return (long)defaultThrottleMs;
        }

        return (long)Math.Min(heartbeatTimeout.TotalMilliseconds * 0.8, maxThrottleMs);
    }
}
