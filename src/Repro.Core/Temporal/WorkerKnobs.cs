using Repro.Core.Config;
using Temporalio.Worker;

namespace Repro.Core.Temporal;

/// <summary>
/// The six <c>worker:</c> knobs, applied to a worker's options in ONE place.
/// </summary>
/// <remarks>
/// FOUR HAND-COPIED COPIES OF THIS BLOCK EXISTED before it was pulled out: the main and scan
/// workers of both <c>Repro.Worker</c> and <c>Repro.LoadGen</c>. Both scan copies carried a
/// comment saying they could not be shared because the two processes bind a different CLIENT --
/// true, and beside the point: the client is passed to the TemporalWorker constructor, not set
/// on the options, so the knobs share fine even when the clients cannot. Both copies also said
/// "if a third copy ever appears, extract it then". There were four.
/// <para>
/// The drift is not hypothetical. <c>Repro.LoadGen</c>'s first worker carries the scar:
/// MaxConcurrentActivities and MaxConcurrentWorkflowTasks were set on the :8077 worker and
/// silently missing on the :8078 one, so that worker kept the SDK defaults (100 / 100) whatever
/// config.yaml said, and the slot-saturation panels could only ever be driven from one of the
/// two processes. Nothing failed; the numbers were just quietly wrong. That is the same argument
/// <see cref="LocalActivityWorkerOptions"/> makes, and one home for the knobs is one home for
/// the prose explaining them, which in this repo is the part worth protecting.
/// </para>
/// <para>
/// Options only, and nothing namespace- or queue-specific. The CLIENT, the task queue, and the
/// workflow and activity registrations stay at each call site, because those are what actually
/// differ between the four workers.
/// </para>
/// <para>
/// NOT called by <see cref="LocalActivityWorkerOptions.For"/>. That worker sets
/// <c>LocalActivityWorkerOnly</c> and therefore never polls for regular activity tasks, so it
/// deliberately omits MaxConcurrentActivities and both heartbeat throttles. Routing it through
/// here would silently add three assignments it has always gone without.
/// </para>
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

        // The SDK default is TimeSpan.Zero. Zero grace plus a minute-long heartbeating activity
        // is the hang this repo demonstrates on purpose (fault.ignoreCancellation); leaving it at
        // the default by accident is how you suffer it instead.
        options.GracefulShutdownTimeout = worker.GracefulShutdownTimeout;

        // MaxHeartbeatThrottleInterval is the one knob here that is load-bearing rather than
        // tidy: it is the ceiling in min(0.8 x heartbeatTimeout, this), which IS the heartbeat
        // throttle, which is exactly how many rows a kill -9 destroys the record of. Leave it on
        // the SDK default and every number in the file-scan case's docs is wrong.
        options.MaxHeartbeatThrottleInterval = worker.MaxHeartbeatThrottleInterval;
        options.DefaultHeartbeatThrottleInterval = worker.DefaultHeartbeatThrottleInterval;

        // Guarded, not assigned unconditionally: for all three int knobs 0 means "leave the SDK
        // default" (10000 cached workflows, 100 of each slot type), which is the contract
        // WorkerConfig documents and which an unconditional assignment would break.
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
