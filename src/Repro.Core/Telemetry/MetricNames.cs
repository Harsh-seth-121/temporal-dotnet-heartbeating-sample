namespace Repro.Core.Telemetry;

/// <summary>The 11 custom metric names, their tag keys, and the outcome values.</summary>
/// <remarks>
/// These exist so a typo is a compile error instead of an empty Grafana panel.
/// Nothing at runtime would catch "repro_hearbeat_sent": Core accepts any name
/// matching [a-z_][a-z0-9_]*, creates the series, and the dashboard queries the
/// other spelling forever.
/// <para>
/// Custom metric names are NOT prefixed by MetricsOptions.MetricPrefix, so the
/// "repro_" here is literal and stays literal even if someone changes the prefix.
/// Core replaces '-' with '_' and PANICS on any other invalid character.
/// </para>
/// </remarks>
public static class MetricNames
{
    public const string WorkflowCompleted = "repro_workflow_completed";
    public const string WorkflowLatency = "repro_workflow_latency";

    public const string ActivityStarted = "repro_activity_started";
    public const string ActivityFailed = "repro_activity_failed";
    public const string ActivityCancel = "repro_activity_cancel";
    public const string ActivityProgress = "repro_activity_progress";

    public const string HeartbeatSent = "repro_heartbeat_sent";
    public const string HeartbeatStaleness = "repro_heartbeat_staleness";
    public const string HeartbeatCallIntervalMs = "repro_heartbeat_call_interval_ms";
    public const string HeartbeatThrottleMs = "repro_heartbeat_throttle_ms";
    public const string HeartbeatTimeoutMs = "repro_heartbeat_timeout_ms";

    /// <remarks>
    /// Do NOT add namespace/task_queue/workflow_type/activity_type here. Both
    /// Workflow.MetricMeter and ActivityExecutionContext.MetricMeter arrive
    /// pre-tagged with them.
    /// </remarks>
    public static class Tags
    {
        public const string Outcome = "outcome";
        public const string Retried = "retried";
        public const string Resumed = "resumed";
        public const string Reason = "reason";
    }

    /// <summary>The exactly-four values of the <c>outcome</c> tag.</summary>
    public static class Outcomes
    {
        public const string Completed = "completed";
        public const string Failed = "failed";

        /// <remarks>One L. US spelling, matching ActivityCancelReason and the dashboards.</remarks>
        public const string Canceled = "canceled";

        public const string TimedOut = "timed_out";
    }
}
