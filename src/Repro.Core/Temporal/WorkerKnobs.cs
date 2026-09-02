using Repro.Core.Config;
using Temporalio.Worker;

namespace Repro.Core.Temporal;

/// <summary>The six <c>worker:</c> knobs, applied to a worker's options in one place.</summary>
/// <remarks>
/// Four hand-copied copies existed first and drifted: the two slot knobs were set on the :8077
/// worker and missing on the :8078 one, which kept the SDK defaults (100 / 100) whatever
/// config.yaml said, and the slot-saturation panels were quietly wrong. Options only, so the
/// client, queue and registrations stay at each call site. Not called by
/// <see cref="LocalActivityWorkerOptions.For"/>, which sets <c>LocalActivityWorkerOnly</c> and
/// so omits MaxConcurrentActivities and both heartbeat throttles.
/// </remarks>
public static class WorkerKnobs
{
    /// <summary>Applies every <c>worker:</c> knob that is not namespace- or queue-specific.</summary>
    /// <param name="options">The options being built. The caller still owns the queue and registrations.</param>
    /// <param name="worker">The loaded <c>worker:</c> block.</param>
    public static void Apply(TemporalWorkerOptions options, WorkerConfig worker)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(worker);

        // The SDK default is TimeSpan.Zero, and zero grace plus a minute-long heartbeating
        // activity is the hang fault.ignoreCancellation demonstrates on purpose.
        options.GracefulShutdownTimeout = worker.GracefulShutdownTimeout;

        // MaxHeartbeatThrottleInterval is the ceiling in min(0.8 x heartbeatTimeout, this),
        // which is the heartbeat throttle and therefore how many rows a kill -9 destroys the
        // record of. On the SDK default every number in the file-scan docs is wrong.
        options.MaxHeartbeatThrottleInterval = worker.MaxHeartbeatThrottleInterval;
        options.DefaultHeartbeatThrottleInterval = worker.DefaultHeartbeatThrottleInterval;

        // Guarded, not unconditional: for all three int knobs 0 means "leave the SDK default"
        // (10000 cached workflows, 100 of each slot type), the contract WorkerConfig documents.
        if (worker.MaxCachedWorkflows > 0)
        {
            options.MaxCachedWorkflows = worker.MaxCachedWorkflows;
        }

        if (worker.MaxConcurrentActivities > 0)
        {
            options.MaxConcurrentActivities = worker.MaxConcurrentActivities;
        }

        if (worker.MaxConcurrentWorkflowTasks > 0)
        {
            options.MaxConcurrentWorkflowTasks = worker.MaxConcurrentWorkflowTasks;
        }
    }
}
