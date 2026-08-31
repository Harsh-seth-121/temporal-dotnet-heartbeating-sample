namespace Repro.Core.Telemetry;

/// <summary>The 14 custom metric names, their tag keys, and the outcome values.</summary>
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

    /// <summary>SimpleNoActivity's outcome counter.</summary>
    /// <remarks>
    /// SEPARATE NAMES, not a second workflow_type on repro_workflow_completed, and this
    /// is load-bearing. The Bug Signals board queries that metric as
    /// `sum by (outcome) (rate(repro_workflow_completed{...}))` with NO workflow_type
    /// selector (signals.json:704, generated at build-dashboards.py:611) and STACKS the
    /// result. A second workflow type sharing the name would be summed into the
    /// heartbeat lines and would falsify the outcome-split claim documented at
    /// config.yaml:118-122.
    /// </remarks>
    public const string SimpleCompleted = "repro_simple_completed";
    public const string SimpleLatency = "repro_simple_latency";
    public const string SimpleMessage = "repro_simple_message";

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

        /// <summary>Which message arrived: see <see cref="Kinds"/>.</summary>
        public const string Kind = "kind";
    }

    /// <summary>The values of the <c>kind</c> tag on <see cref="SimpleMessage"/>.</summary>
    /// <remarks>
    /// No value for a REJECTED update. An update the validator refuses never reaches the
    /// handler and writes nothing to history, and a validator must be side-effect free --
    /// so there is nowhere honest to count it from inside the workflow. The loadgen
    /// counts rejections client-side instead.
    /// </remarks>
    public static class Kinds
    {
        public const string Poke = "poke";
        public const string Add = "add";
    }

    /// <summary>Values of the <c>outcome</c> tag: four on the workflow metrics, three on the simple ones.</summary>
    /// <remarks>
    /// repro_workflow_completed uses completed / failed / canceled / timed_out.
    /// repro_simple_completed uses stopped / expired / canceled.
    /// </remarks>
    public static class Outcomes
    {
        public const string Completed = "completed";
        public const string Failed = "failed";

        /// <remarks>One L. US spelling, matching ActivityCancelReason and the dashboards.</remarks>
        public const string Canceled = "canceled";

        public const string TimedOut = "timed_out";

        /// <summary>SimpleNoActivity only: the Stop signal arrived. Server status is Completed.</summary>
        public const string Stopped = "stopped";

        /// <summary>SimpleNoActivity only: MaxDurationMs elapsed with no Stop. Server status is Completed.</summary>
        public const string Expired = "expired";
    }
}
