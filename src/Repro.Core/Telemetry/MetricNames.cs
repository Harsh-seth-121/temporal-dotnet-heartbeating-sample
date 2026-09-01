namespace Repro.Core.Telemetry;

/// <summary>The 16 custom metric names, their tag keys, and the outcome values.</summary>
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
    /// selector and STACKS the result. A second workflow type sharing the name would be
    /// summed into the heartbeat lines and would falsify the outcome-split claim that
    /// config.yaml's fault.failureRate comment makes.
    /// <para>
    /// Anchors rather than line numbers, deliberately: the panel is the one titled
    /// "Custom: repro workflow outcomes /s" in build-dashboards.py, and its generated
    /// target is the only `sum by (outcome)` over repro_workflow_completed in
    /// dashboards/sandbox/signals.json. This comment previously cited
    /// build-dashboards.py:611 and was silently made wrong by an insertion four lines
    /// above it; grep for the title instead.
    /// </para>
    /// </remarks>
    public const string SimpleCompleted = "repro_simple_completed";
    public const string SimpleLatency = "repro_simple_latency";
    public const string SimpleMessage = "repro_simple_message";

    /// <summary>WorkflowSimpleActivity's outcome counter and end-to-end latency.</summary>
    /// <remarks>
    /// The THIRD separate pair, for the same reason <see cref="SimpleCompleted"/> is the
    /// second: the Bug Signals board queries repro_workflow_completed as
    /// `sum by (outcome) (rate(...))` with NO workflow_type selector and STACKS the result
    /// (the panel titled "Custom: repro workflow outcomes /s"), so a third workflow type
    /// sharing that name would be summed into the heartbeat lines.
    /// <para>
    /// SUBSTRING-COLLISION CHECK, because HistogramBuckets's header warns that Core matches
    /// bucket-override keys with metric_name.Contains(key) in nondeterministic order and a
    /// reader will immediately wonder about this pair.
    /// "repro_simple_activity_latency".Contains("repro_simple_latency") is FALSE: they
    /// diverge at index 13, "_l" against "_a". The reverse is false because it is shorter. The two rows are independent. TelemetryTests turns that hand-check into a
    /// test, because the next name inserted here may not be so lucky.
    /// </para>
    /// <para>
    /// <see cref="SimpleActivityLatency"/> deliberately carries ONLY the outcome tag, not
    /// <see cref="Tags.Source"/>, and NOT because the source is invisible in the numbers.
    /// It is plainly visible: measured, a refused endpoint lands near 5.02s and a live fetch
    /// near 5.77s, three of HistogramBuckets' boundaries apart. The reason is that the
    /// question "is this demo reaching the internet" is already answered exactly once, by
    /// <c>sum by (source)</c> on <see cref="SimpleActivityCompleted"/>, and answering it a
    /// second time would double a 13-boundary histogram's series count for no new
    /// information. For "how slow is Open-Meteo" specifically, the precise answer is
    /// WeatherReading.HttpElapsedMs in the result payload, not a latency histogram dominated
    /// by the sleep.
    /// </para>
    /// </remarks>
    public const string SimpleActivityCompleted = "repro_simple_activity_completed";
    public const string SimpleActivityLatency = "repro_simple_activity_latency";

    /// <summary>WorkflowLocalActivity's outcome counter and end-to-end latency.</summary>
    /// <remarks>
    /// The FOURTH separate pair, for the same reason <see cref="SimpleCompleted"/> is the
    /// second and <see cref="SimpleActivityCompleted"/> the third: the panel titled
    /// "Custom: repro workflow outcomes /s" queries repro_workflow_completed with NO
    /// workflow_type selector and STACKS it.
    /// <para>
    /// READ THIS BEFORE BUILDING A PANEL ON IT. Unlike the other three, this counter does not
    /// account for every run. Two-thirds of runs at the shipped config end by
    /// WorkflowOptions.RunTimeout, and the server closes a run-timed-out workflow by calling
    /// TimeoutWorkflow directly, WITHOUT scheduling a workflow task. Workflow code therefore
    /// never runs again and cannot record anything -- not `timed_out`, not any other value.
    /// Those runs are simply absent here.
    /// </para>
    /// <para>
    /// That is why <see cref="PiAttemptStarted"/> exists and why it, not this, is the primary
    /// signal for the local-activity case. The timeout COUNT comes from the server's own
    /// workflow_timeout in that namespace.
    /// </para>
    /// <para>
    /// SUBSTRING-COLLISION CHECK, because HistogramBuckets matches override keys with
    /// metric_name.Contains(key) in nondeterministic order.
    /// "repro_local_activity_latency" and "repro_simple_activity_latency" diverge at index 6,
    /// "l" against "s", so neither contains the other. The SDK key added alongside them,
    /// "temporal_local_activity_execution_latency", does NOT contain
    /// "temporal_activity_execution_latency" either: the byte before "activity_execution" is
    /// the "l" of "local_", not the "_" of "temporal_". TelemetryTests turns all of that into
    /// a test rather than leaving it as this paragraph.
    /// </para>
    /// </remarks>
    public const string LocalActivityCompleted = "repro_local_activity_completed";
    public const string LocalActivityLatency = "repro_local_activity_latency";

    /// <summary>One increment per REAL execution of the Pi burn, re-executions included.</summary>
    /// <remarks>
    /// THE POINT OF THE WHOLE CASE, and the only metric in this repo emitted from activity
    /// code for a reason other than convenience.
    /// <para>
    /// Workflow.MetricMeter is replay-suppressed, which is exactly wrong here: a local activity
    /// re-executed after a workflow task timeout is not a replay, it is a second real burn of
    /// CPU, and a replay-suppressed counter would hide precisely the waste this case exists to
    /// show. Activity code does not replay, so counting here counts executions.
    /// </para>
    /// <para>
    /// Divided by the completion rate of <see cref="LocalActivityCompleted"/> it gives wasted
    /// executions per useful result. At the shipped draw the expected steady state is about 13
    /// attempts per completed run: per three runs, one completer contributing a single attempt
    /// and two doomed runs contributing six each before runTimeout closes them.
    /// </para>
    /// </remarks>
    public const string PiAttemptStarted = "repro_pi_attempt_started";

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

        /// <summary>Where a weather reading came from: see <see cref="Sources"/>.</summary>
        public const string Source = "source";
    }

    /// <summary>The values of the <c>kind</c> tag on <see cref="SimpleMessage"/>.</summary>
    /// <remarks>
    /// No value for a REJECTED update. An update the validator refuses never reaches the
    /// handler and writes nothing to history, and a validator must be side-effect free, so
    /// there is nowhere honest to count it from inside the workflow. The loadgen
    /// counts rejections client-side instead.
    /// </remarks>
    public static class Kinds
    {
        public const string Poke = "poke";
        public const string Add = "add";
    }

    /// <summary>The values of the <c>source</c> tag on <see cref="SimpleActivityCompleted"/>.</summary>
    /// <remarks>
    /// THE HYPHEN IN "open-meteo" IS SAFE. Core replaces '-' with '_' in metric NAMES and
    /// panics on any other invalid character there, but it does not sanitize label VALUES
    /// at all. That is the same asymmetry <c>ReproConfig.TaskQueue</c> documents for
    /// task_queue="repro-task-queue".
    /// <para>
    /// <see cref="None"/> exists so `sum by (source)` accounts for 100% of runs. A failed
    /// or cancelled run has no reading at all, and omitting the tag would put series with
    /// an absent label next to series that have one. That is readable in Prometheus, confusing
    /// in a legend, and it makes source="" look like a third kind of run rather than a
    /// missing one.
    /// </para>
    /// </remarks>
    public static class Sources
    {
        /// <summary>A real reading, fetched over the network.</summary>
        public const string OpenMeteo = "open-meteo";

        /// <summary>The transport failed and the activity returned a stand-in. Watch this one.</summary>
        public const string Synthetic = "synthetic";

        /// <summary>No reading: the run failed, timed out or was cancelled.</summary>
        public const string None = "none";
    }

    /// <summary>Values of <c>PiEstimate.EndedBy</c>. A PAYLOAD field, not a tag.</summary>
    /// <remarks>
    /// Not a metric dimension on purpose. It answers "did this burn finish or was it cut
    /// short by a worker drain", which is a per-run diagnostic worth having in
    /// `temporal workflow show`, and which would add a second low-cardinality split to a
    /// counter that already carries one for no question anybody asks of the board.
    /// </remarks>
    public static class Endings
    {
        /// <summary>The burn ran its full requested duration.</summary>
        public const string Completed = "completed";

        /// <summary>A worker drain cut it short. See PiActivities on which token fires.</summary>
        public const string Shutdown = "shutdown";
    }

    /// <summary>
    /// Values of the <c>outcome</c> tag: four on repro_workflow_completed and
    /// repro_simple_activity_completed, three on repro_simple_completed.
    /// </summary>
    /// <remarks>
    /// repro_workflow_completed uses completed / failed / canceled / timed_out.
    /// repro_simple_completed uses stopped / expired / canceled.
    /// repro_simple_activity_completed uses completed / failed / canceled / timed_out, and
    /// timed_out THERE is TimeoutType.StartToClose, never Heartbeat, because that
    /// workflow sets no heartbeat timeout at all.
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
