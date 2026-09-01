using Repro.Core.Activities;
using Repro.Core.Config;
using Repro.Core.Workflows;
using Temporalio.Worker;

namespace Repro.Core.Temporal;

/// <summary>
/// The worker options for the local-activity namespace, built in ONE place and used by both
/// <c>Repro.Worker</c> and <c>Repro.LoadGen</c>.
/// </summary>
/// <remarks>
/// Extracted rather than copied, and the reason is on the record in this repo rather than
/// theoretical. <c>Repro.LoadGen</c>'s first worker already carries a scar comment about
/// exactly this failure: MaxConcurrentActivities and MaxConcurrentWorkflowTasks were set on
/// the :8077 worker and silently missing on the :8078 one, so that worker kept the SDK
/// defaults whatever config.yaml said, and the slot-saturation panels could only ever be
/// driven from one of the two processes. Nothing failed; the numbers were just quietly wrong.
/// <para>
/// Adding a SECOND pair of workers with the same knobs copied by hand is how that happens
/// again, and the copies had already started to diverge in their comments before this was
/// pulled out. One home for the options is also one home for the prose explaining them, which
/// in this repo is the part worth protecting.
/// </para>
/// <para>
/// Options only. The CLIENT is still built per process, because the two processes use
/// different <c>role</c> strings and identity is <c>role@machine:pid</c>.
/// </para>
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
            // PiActivities goes on the worker that runs the WORKFLOW, and only there. A local
            // activity is resolved against that worker's registry, so registering it on the
            // other worker instead throws at schedule time INSIDE the workflow task, with
            // "is not registered on this worker". Registering it in both places is not a
            // workaround; the other worker would never be asked.
            .AddAllActivities(new PiActivities());

        // This worker hosts workflows and local activities and nothing else, so it should not
        // poll for regular activity tasks that can never arrive on this queue.
        options.LocalActivityWorkerOnly = true;
        options.GracefulShutdownTimeout = config.Worker.GracefulShutdownTimeout;

        // Local activities have their OWN slot type, so this does not come out of
        // MaxConcurrentActivities. Pinning it low is not politeness: the SDK default is 100,
        // workflow activations run on the same thread pool these CPU burns occupy, and a
        // workflow task that does not yield within 2 seconds is failed as a deadlock. A
        // starved pool therefore produces evicted runs and retried workflow tasks that look
        // exactly like this case's real failure and are not it.
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
