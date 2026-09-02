namespace Repro.Core.Telemetry;

/// <summary>The 35 custom metric names, their tag keys, and the outcome values.</summary>
/// <remarks>Constants so a typo is a compile error: Core accepts any name matching [a-z_][a-z0-9_]*, so
/// "repro_hearbeat_sent" creates a series the dashboard never queries. Core replaces '-' with '_' and panics on
/// other invalid characters. MetricPrefix does not apply to custom names, so "repro_" is literal. Each workflow
/// gets its own completed/latency pair, not a workflow_type tag: the "Custom: repro workflow outcomes /s" panel
/// queries repro_workflow_completed with sum by (outcome), no type selector, and stacks it. Names must not
/// contain each other either: Core matches HistogramBuckets override keys with metric_name.Contains(key) in
/// nondeterministic order. TelemetryTests checks every pair.</remarks>
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

    /// <summary>SimpleNoActivity's outcome counter, latency, and message counter.</summary>
    public const string SimpleCompleted = "repro_simple_completed";
    public const string SimpleLatency = "repro_simple_latency";
    public const string SimpleMessage = "repro_simple_message";

    /// <summary>WorkflowSimpleActivity's outcome counter and end-to-end latency.</summary>
    /// <remarks>The latency carries no <see cref="Tags.Source"/>: <c>sum by (source)</c> on the counter answers
    /// that, and a second split would double a 13-boundary histogram's series count. The sleep dominates it;
    /// Open-Meteo's own latency is WeatherReading.HttpElapsedMs.</remarks>
    public const string SimpleActivityCompleted = "repro_simple_activity_completed";
    public const string SimpleActivityLatency = "repro_simple_activity_latency";

    /// <summary>WorkflowLocalActivity's outcome counter and end-to-end latency.</summary>
    /// <remarks>Does not account for every run: two-thirds at the shipped config end at
    /// WorkflowOptions.RunTimeout, and the server closes a run-timed-out workflow without scheduling a workflow
    /// task, so nothing tags an outcome. Prefer <see cref="PiAttemptStarted"/>.</remarks>
    public const string LocalActivityCompleted = "repro_local_activity_completed";
    public const string LocalActivityLatency = "repro_local_activity_latency";

    /// <summary>One increment per real execution of the Pi burn, re-executions included.</summary>
    /// <remarks>Emitted from activity code because Workflow.MetricMeter is replay-suppressed, and a local
    /// activity re-executed after a workflow task timeout is a real second burn of CPU. Over
    /// <see cref="LocalActivityCompleted"/> it gives wasted executions per result, about 13.</remarks>
    public const string PiAttemptStarted = "repro_pi_attempt_started";

    /// <summary>WorkflowFileScan's outcome counter and end-to-end latency.</summary>
    public const string FileScanCompleted = "repro_file_scan_completed";
    public const string FileScanLatency = "repro_file_scan_latency";

    /// <summary>Rows this attempt has physically read. The cost meter: never rewound.</summary>
    /// <remarks>Carries <see cref="Tags.Attempt"/>, the one exception to the no-extra-tags rule in
    /// <see cref="Tags"/>, bounded at 10 per process by fileScan.retry.maximumAttempts. Not rewound on resume,
    /// unlike <see cref="FileScanRowCursor"/>: re-read rows cost real I/O. Redone work is
    /// sum(max_over_time(repro_file_scan_rows_read[$__range])) minus
    /// max(max_over_time(repro_file_scan_rows_expected[$__range])); max_over_time, never increase(), which
    /// extrapolates across the gap where the killed target is down. Exact for a drain, cancel or failure, up to
    /// 4.2% low for kill -9.</remarks>
    public const string FileScanRowsRead = "repro_file_scan_rows_read";

    /// <summary>Bytes this attempt has read. Carries <see cref="Tags.Attempt"/>, same reasoning.</summary>
    /// <remarks>Denominator of the allocation-amplification panel, bytes_allocated / clamp_min(bytes_read, 1e-9),
    /// which reads fault.decodeRowsToStrings as "2.4x" rather than a bytes/s figure.</remarks>
    public const string FileScanBytesRead = "repro_file_scan_bytes_read";

    /// <summary>The row the scan has arithmetically completed. The sawtooth.</summary>
    /// <remarks>Must stay untagged. With <see cref="Tags.Attempt"/> a killed attempt's series does not vanish,
    /// Prometheus serves its last sample for the 5-minute staleness window, and max() holds the dead peak flat
    /// while attempt 2 climbs underneath, so the drop never renders. Untagged it is one last-writer-wins series,
    /// as <see cref="ActivityProgress"/> is: above fileScan.concurrency 1, read a single execution.</remarks>
    public const string FileScanRowCursor = "repro_file_scan_row_cursor";

    /// <summary>The row a resumed attempt restarted from: each tooth's floor.</summary>
    /// <remarks>Written once per attempt and only after a checkpoint is found, so the series is absent rather
    /// than 0 until the first resume. The floor target needs <c>or vector(0)</c>; the drop drawn is cursor minus
    /// floor.</remarks>
    public const string FileScanResumedFromRow = "repro_file_scan_resumed_from_row";

    /// <summary>Rows this scan intends to read. The denominator.</summary>
    /// <remarks>So no panel hard-codes 1,724,588: the corpus is generated at four sizes, and a literal renders as
    /// a progress percentage stuck at 20% or one over 100%.</remarks>
    public const string FileScanRowsExpected = "repro_file_scan_rows_expected";

    /// <summary>The idempotency verdict: see <see cref="Results"/>.</summary>
    /// <remarks>The activity compares its accumulated indexSum against rows x (rows + 1) / 2 and increments
    /// exactly one value before returning, so a completed scan reads 1 here, never 0. A mismatch throws
    /// non-retryable, so the outcome and this counter agree.</remarks>
    public const string FileScanVerified = "repro_file_scan_verified";

    /// <summary>How stale the checkpoint was on resume. Separate from repro_heartbeat_staleness.</summary>
    /// <remarks>repro_heartbeat_staleness sits at 0.8 x a 5s timeout with a bucket row stopping at 30_000, while
    /// this case's 30s timeout clusters near 24s and runs out to about 64s, so merging them would move the
    /// heartbeat board's p95 by an order of magnitude.</remarks>
    public const string FileScanStaleness = "repro_file_scan_staleness";

    /// <summary>One increment per activity attempt, tagged <c>retried</c> and <c>resumed</c>.</summary>
    /// <remarks>Both tag values are lowercased by hand, as HeartbeatActivities.Bool does: bool.ToString() returns
    /// "True", dashboard selectors match retried="true", and a capitalized value leaves the panel empty
    /// forever.</remarks>
    public const string FileScanStarted = "repro_file_scan_started";

    /// <summary>Process-wide bytes allocated, via a <see cref="ProcessPressure"/> watermark.</summary>
    /// <remarks>A counter over a process-wide cumulative value, so ProcessPressure advances it with a
    /// compare-exchange watermark. A near-zero rate is the default path working: a 4m47s scan of the 100 MB
    /// corpus reports about 415 KB total, 1.4 KB/s against 348 KB/s read, dominated by the 117 B per-batch
    /// heartbeat. fault.decodeRowsToStrings reports 2.41x bytes read.</remarks>
    public const string FileScanBytesAllocated = "repro_file_scan_bytes_allocated";

    /// <summary>GC collections, tagged <see cref="Tags.Gen"/>. One watermark per generation.</summary>
    /// <remarks>Read <see cref="Gens"/> first: the three values nest rather than partition. gen="2" is absent
    /// rather than zero at the shipped config; it needs fault.retainScannedRows or fault.slurpWholeFile. Never
    /// add <c>or vector(0)</c> to a bare <c>sum by (gen)</c>: it returns a series with no gen label.</remarks>
    public const string FileScanGcCollected = "repro_file_scan_gc_collected";

    /// <summary>Live managed heap: GC.GetTotalMemory(false), never true.</summary>
    /// <remarks>A sawtooth means garbage, a staircase with no falling edge means retention, which is what
    /// fault.decodeRowsToStrings and fault.retainScannedRows draw. The panel uses max(), never sum(): the value
    /// is process-wide.</remarks>
    public const string FileScanManagedHeapBytes = "repro_file_scan_managed_heap_bytes";

    /// <summary>Large object heap, GenerationInfo[3]. A last-GC snapshot, not a live reading.</summary>
    /// <remarks>Four names rather than a region label: these gauges nest by containment, so
    /// <c>sum by (region)</c> would double-count. GCMemoryInfo describes the last GC, not now: a 100 MB
    /// File.ReadAllBytes left this at 100.0 MiB until a forced blocking gen2 collect, after which it read
    /// 200.0 MiB, and every GenerationInfo entry, TotalCommittedBytes included, reads 0 before the first GC. Flat at zero is the default path
    /// working: byte[n] hits the 85,000-byte LOH threshold at n of 84,976 or more, and fileScan.bufferBytes ships
    /// at 65,536.</remarks>
    public const string FileScanLohBytes = "repro_file_scan_loh_bytes";

    /// <summary>Environment.WorkingSet: what the OS thinks the process costs, resident.</summary>
    /// <remarks>Measured on macOS 26.6.2 arm64, since Environment.WorkingSet has historically returned 0 on some
    /// Unix targets: it returns 37.2 MiB at process start, costs 1.3us per call and allocates nothing. It stays
    /// flat through a 500 MB streaming scan because the data lives in the kernel page cache: a live 100 MiB array
    /// left it at 139.4 MiB, while File.ReadAllBytes of the same size stepped it to 239.8 MiB.</remarks>
    public const string FileScanWorkingSetBytes = "repro_file_scan_working_set_bytes";

    /// <summary>GCMemoryInfo.PauseTimePercentage: why rows/s decayed.</summary>
    /// <remarks>Not the percentage of the last logInterval spent paused: the runtime computes it at the end of a
    /// GC and leaves it. It read 0.73 right after a forced gen2 collect and still 0.73 after 500ms of idle, so a
    /// scan that triggers no collection reports the worker's startup GCs forever. Believe movement only when
    /// <see cref="FileScanGcCollected"/> moves.</remarks>
    public const string FileScanGcPausePercent = "repro_file_scan_gc_pause_percent";

    /// <remarks>Do not add namespace/task_queue/workflow_type/activity_type: Workflow.MetricMeter and
    /// ActivityExecutionContext.MetricMeter arrive pre-tagged. <see cref="Attempt"/> is the one exception; see
    /// <see cref="FileScanRowsRead"/>.</remarks>
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

        /// <summary>ActivityInfo.Attempt, on <see cref="FileScanRowsRead"/> and <see cref="FileScanBytesRead"/>
        /// only. Formatted with CultureInfo.InvariantCulture, since CA1305 is a build error here.</summary>
        public const string Attempt = "attempt";

        /// <summary>The idempotency verdict: see <see cref="Results"/>.</summary>
        public const string Result = "result";

        /// <summary>Which GC generation was collected: see <see cref="Gens"/>.</summary>
        public const string Gen = "gen";
    }

    /// <summary>The values of the <c>kind</c> tag on <see cref="SimpleMessage"/>.</summary>
    /// <remarks>No value for a rejected update: the validator refuses it before the handler runs and writes
    /// nothing to history, and a validator must be side-effect free, so the loadgen counts rejections
    /// client-side.</remarks>
    public static class Kinds
    {
        public const string Poke = "poke";
        public const string Add = "add";
    }

    /// <summary>The values of the <c>source</c> tag on <see cref="SimpleActivityCompleted"/>.</summary>
    /// <remarks>The hyphen in "open-meteo" is safe: Core sanitizes metric names but not label values, the same
    /// asymmetry <c>ReproConfig.TaskQueue</c> documents. <see cref="None"/> exists so <c>sum by (source)</c>
    /// accounts for 100% of runs.</remarks>
    public static class Sources
    {
        /// <summary>A real reading, fetched over the network.</summary>
        public const string OpenMeteo = "open-meteo";

        /// <summary>The transport failed and the activity returned a stand-in. Watch this one.</summary>
        public const string Synthetic = "synthetic";

        /// <summary>No reading: the run failed, timed out or was cancelled.</summary>
        public const string None = "none";
    }

    /// <summary>The values of the <c>result</c> tag on <see cref="FileScanVerified"/>.</summary>
    /// <remarks>A scan that reaches the end produces exactly one of these and one that does not produces neither,
    /// so <c>sum by (result)</c> accounts for 100% of completed scans.</remarks>
    public static class Results
    {
        /// <summary>indexSum equals rows x (rows + 1) / 2. Resume was idempotent.</summary>
        public const string Match = "match";

        /// <summary>It did not. Unreachable in a healthy run: the activity throws non-retryable.</summary>
        public const string Mismatch = "mismatch";
    }

    /// <summary>The values of the <c>gen</c> tag on <see cref="FileScanGcCollected"/>.</summary>
    /// <remarks>These values nest, they do not partition. GC.CollectionCount(g) counts collections of generation
    /// g or higher: from 0/0/0, two forced gen0 collects then one gen1 then one gen2 read 4/2/1, not 2/1/1.
    /// <c>sum by (gen)</c> groups rather than adds, so adding the lines for a total triple-counts every
    /// gen2.</remarks>
    public static class Gens
    {
        public const string Gen0 = "0";
        public const string Gen1 = "1";

        /// <summary>Absent rather than zero at the shipped config.</summary>
        public const string Gen2 = "2";
    }

    /// <summary>Values of <c>PiEstimate.EndedBy</c>. A payload field, not a tag.</summary>
    /// <remarks>A per-run diagnostic for <c>temporal workflow show</c>: did this burn finish, or did something
    /// cut it short.</remarks>
    public static class Endings
    {
        /// <summary>The burn ran its full requested duration.</summary>
        public const string Completed = "completed";

        /// <summary>A worker drain cut it short: <c>WorkerShutdownToken</c> fired.</summary>
        public const string Shutdown = "shutdown";

        /// <summary>Cancelled mid-burn: <c>ActivityExecutionContext.CancellationToken</c> fired, which here
        /// overwhelmingly means the workflow task timed out.</summary>
        /// <remarks>Across 17 cut-short burns in one demo run, every one ended between 64.0s and 64.2s against a
        /// 1m history.workflowTaskHeartbeatTimeout, never at the drawn duration and never at a drain: a local
        /// activity is told when its workflow task times out, about four seconds after the server's timeout
        /// fires. A doomed run burns about 64s per execution. Distinct from <see cref="Shutdown"/>.</remarks>
        public const string Canceled = "canceled";
    }

    /// <summary>Values of the <c>outcome</c> tag. Which of them a given counter can carry differs.</summary>
    /// <remarks>repro_workflow_completed and repro_simple_activity_completed use completed / failed / canceled /
    /// timed_out, where timed_out is TimeoutType.StartToClose and never Heartbeat, since neither sets a heartbeat
    /// timeout. repro_simple_completed uses stopped / expired / canceled. repro_local_activity_completed reaches
    /// only three: timed_out needs localActivity.scheduleToCloseTimeout below the workflow task heartbeat
    /// timeout. Worse, the two-thirds of runs whose burn outlives that 1m timeout end at RunTimeout and tag
    /// nothing, so anything dividing by this counter uses the wrong denominator; count those with the server's
    /// workflow_timeout in the repro-local-activity namespace, or with <see cref="PiAttemptStarted"/>.</remarks>
    public static class Outcomes
    {
        public const string Completed = "completed";
        public const string Failed = "failed";

        /// <remarks>One L. US spelling, matching ActivityCancelReason and the dashboards.</remarks>
        public const string Canceled = "canceled";

        public const string TimedOut = "timed_out";

        /// <summary>SimpleNoActivity only: the Stop signal arrived. Server status is Completed.</summary>
        public const string Stopped = "stopped";

        /// <summary>SimpleNoActivity only: MaxDurationMs elapsed with no Stop. Server status Completed.</summary>
        public const string Expired = "expired";
    }
}
