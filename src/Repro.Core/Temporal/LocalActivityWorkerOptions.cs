using Repro.Core.Activities;
using Repro.Core.Config;
using Repro.Core.Workflows;
using Temporalio.Worker;

namespace Repro.Core.Temporal;

/// <summary>Worker options for the local-activity namespace, shared by both host processes.</summary>
/// <remarks>
/// Extracted for the reason <see cref="WorkerKnobs"/> records: hand-copied knob blocks drift
/// silently. Options only; the client is still built per process, because the two use different
/// <c>role</c> strings.
/// </remarks>
public static class LocalActivityWorkerOptions
{
    /// <summary>Builds the options for a worker bound to the local-activity namespace.</summary>
    /// <param name="config">The loaded config; both the worker and localActivity blocks are read.</param>
    public static TemporalWorkerOptions For(ReproConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var options = new TemporalWorkerOptions(config.LocalActivity.TaskQueue)
            .AddWorkflow<WorkflowLocalActivity>()
            // PiActivities goes on the worker that runs the workflow, and only there: a local
            // activity resolves against that worker's registry, so registering it elsewhere
            // throws inside the workflow task with "is not registered on this worker".
            .AddAllActivities(new PiActivities());

        // Workflows and local activities only, so it should not poll for regular activity tasks.
        options.LocalActivityWorkerOnly = true;
        options.GracefulShutdownTimeout = config.Worker.GracefulShutdownTimeout;

        // Local activities have their own slot type, so this is not part of
        // MaxConcurrentActivities. Pinned low because the SDK default is 100, workflow
        // activations share the thread pool these CPU burns occupy, and a task that does not
        // yield within 2 seconds is failed as a deadlock. A starved pool produces evicted runs
        // that look exactly like this case's real failure and are not it.
        options.MaxConcurrentLocalActivities = config.LocalActivity.MaxConcurrentLocalActivities;

        if (config.Worker.MaxCachedWorkflows > 0)
        {
            options.MaxCachedWorkflows = config.Worker.MaxCachedWorkflows;
        }

        if (config.Worker.MaxConcurrentWorkflowTasks > 0)
        {
            options.MaxConcurrentWorkflowTasks = config.Worker.MaxConcurrentWorkflowTasks;
        }

        return options;
    }
}
