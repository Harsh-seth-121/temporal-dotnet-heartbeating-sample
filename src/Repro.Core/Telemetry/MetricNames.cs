namespace Repro.Core.Telemetry;

/// <summary>The 35 custom metric names, their tag keys, and the outcome values.</summary>
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

    /// <summary>WorkflowFileScan's outcome counter and end-to-end latency.</summary>
    /// <remarks>
    /// The FIFTH separate pair, for the reason <see cref="SimpleCompleted"/> gives for the
    /// second: the panel titled "Custom: repro workflow outcomes /s" queries
    /// repro_workflow_completed as `sum by (outcome) (rate(...))` with NO workflow_type
    /// selector and STACKS the result, so a fifth workflow type sharing that name would be
    /// summed into the heartbeat lines.
    /// <para>
    /// SUBSTRING-COLLISION CHECK, because Core matches bucket-override keys with
    /// metric_name.Contains(key) in nondeterministic order and HistogramBuckets carries a row
    /// for both file-scan histograms. "repro_file_scan_latency" and
    /// "repro_file_scan_staleness" diverge from each other at index 16 ("l" against "s") and
    /// from every other key in that table at index 6 ("f"). Neither contains the other, in
    /// either direction. TelemetryTests turns that hand-check into a test, because the next
    /// name inserted here may not be so lucky.
    /// </para>
    /// </remarks>
    public const string FileScanCompleted = "repro_file_scan_completed";
    public const string FileScanLatency = "repro_file_scan_latency";

    /// <summary>Rows this ATTEMPT has physically read. The cost meter: never rewound.</summary>
    /// <remarks>
    /// THE ONE METRIC IN THIS FILE THAT CARRIES <see cref="Tags.Attempt"/>, and the only
    /// deliberate exception to the no-extra-tags rule in <see cref="Tags"/>. The exception is
    /// safe for one reason, and it is not "attempt is a small number": it is that attempt is
    /// BOUNDED BY CONFIGURATION. fileScan.retry.maximumAttempts is 10, so the series count is
    /// 10 per process no matter what fileScan.concurrency is set to. The other tags that
    /// would separate concurrent scans -- workflow_id, run_id, activity_id -- are unbounded,
    /// and a counter that ticks once per batch is the last place to introduce unbounded
    /// cardinality into the Prometheus you are debugging with.
    /// <para>
    /// NOT REWOUND ON RESUME, which is the exact opposite of what
    /// <see cref="FileScanRowCursor"/> does, and the two must not be conflated. A resumed
    /// attempt physically re-reads every row between the checkpoint and the crash; this
    /// counts them, because they cost real time and real I/O. The cursor does not count them,
    /// because they are arithmetically counted once. Two different questions, and one series
    /// cannot answer both.
    /// </para>
    /// <para>
    /// THERE IS DELIBERATELY NO repro_file_scan_redone_rows, and the reason is a proof rather
    /// than a preference. A process-local "redone" accumulator would have to be
    /// A_k = A_(k-1) + (C_k - C_(k-1)), where C_k is the checkpoint row attempt k resumed
    /// from. That telescopes to A_k = C_k identically, so the value equals
    /// FileScanCheckpoint.Rows and carries no information whatsoever. The reads that get lost
    /// are exactly the reads that were never checkpointed, so the checkpoint is structurally
    /// incapable of measuring redone work. FileScanJob.cs carries the same proof from the
    /// record's side, and this metric plus <see cref="FileScanRowsExpected"/> is the way out.
    /// </para>
    /// <para>
    /// Redone work is therefore a DERIVED panel:
    /// sum(max_over_time(repro_file_scan_rows_read[$__range])) minus
    /// max(max_over_time(repro_file_scan_rows_expected[$__range])). max_over_time per
    /// attempt-series, never increase(): each (attempt, instance) series is monotone and
    /// never resets within itself, whereas increase() extrapolates to the range edges AND has
    /// to cross the gap where the killed target is down -- which is to say it approximates in
    /// exactly the region being measured. Exact for an attempt that drains, cancels or fails,
    /// because that attempt survives to the next scrape; up to 4.2% low for kill -9, where
    /// the error is one 1s scrape (6,000 rows) against a 144,000-row signal. That contrast is
    /// the punchline: kill -9 loses the work AND the record of having done it.
    /// </para>
    /// </remarks>
    public const string FileScanRowsRead = "repro_file_scan_rows_read";

    /// <summary>Bytes this ATTEMPT has read. Carries <see cref="Tags.Attempt"/>, same reasoning.</summary>
    /// <remarks>
    /// The denominator of the allocation-amplification panel,
    /// bytes_allocated / clamp_min(bytes_read, 1e-9). That ratio is how
    /// fault.decodeRowsToStrings reads as "2.4x" instead of as a bare bytes/s that means
    /// nothing without the corpus geometry in front of you.
    /// </remarks>
    public const string FileScanBytesRead = "repro_file_scan_bytes_read";

    /// <summary>The row the scan has ARITHMETICALLY completed. The sawtooth.</summary>
    /// <remarks>
    /// UNTAGGED, and it has to stay untagged. Adding <see cref="Tags.Attempt"/> here is the
    /// obvious symmetry with <see cref="FileScanRowsRead"/> and it destroys the panel: when
    /// attempt 1 is killed its tagged series does not vanish, Prometheus keeps serving that
    /// series' LAST SAMPLE for the 5-minute staleness window, so max() across attempts holds
    /// the dead peak flat while attempt 2 climbs from the resume floor underneath it. The
    /// drop -- the entire point of this case -- never renders, on a board whose first panel
    /// exists to draw it. Untagged, this is one last-writer-wins series and the drop is
    /// immediate.
    /// <para>
    /// LAST-WRITER-WINS is the accepted cost, exactly as it is for
    /// <see cref="ActivityProgress"/>: at fileScan.concurrency above 1 every in-flight scan
    /// writes this one series and the panel shows whichever wrote most recently. Read it
    /// against a single execution (the starter, or concurrency 1) when you want the monotone
    /// climb and one clean tooth.
    /// </para>
    /// </remarks>
    public const string FileScanRowCursor = "repro_file_scan_row_cursor";

    /// <summary>The row a resumed attempt restarted from: each tooth's FLOOR.</summary>
    /// <remarks>
    /// Written once per attempt, and only when a checkpoint was found. Attempt 1 never writes
    /// it, so the series is ABSENT rather than 0 until the first resume. That is why the
    /// cursor panel's floor target needs `or vector(0)`, and why the drop it draws is
    /// cursor-minus-floor rather than cursor-minus-zero.
    /// </remarks>
    public const string FileScanResumedFromRow = "repro_file_scan_resumed_from_row";

    /// <summary>Rows this scan intends to read. The DENOMINATOR.</summary>
    /// <remarks>
    /// It exists so that no panel hard-codes 1,724,588. The corpus is gitignored and generated
    /// at four sizes, so a literal in a dashboard expression is wrong for three of them, and
    /// wrong in a way that RENDERS -- a progress percentage stuck at 20%, or one over 100% --
    /// rather than leaving an empty panel behind.
    /// </remarks>
    public const string FileScanRowsExpected = "repro_file_scan_rows_expected";

    /// <summary>The IDEMPOTENCY VERDICT: see <see cref="Results"/>.</summary>
    /// <remarks>
    /// The activity compares its accumulated indexSum against the closed form
    /// rows x (rows + 1) / 2 and increments exactly one of the two values before returning,
    /// so on any completed scan this counter reads 1 and never 0.
    /// <para>
    /// A MISMATCH IS NEVER RETURNED AS A SUCCESS. The activity logs at Error and throws
    /// non-retryable, so the outcome and this counter agree. A wrong aggregate arriving as
    /// outcome="completed" would be the plausible-constant failure HistogramBuckets's header
    /// calls the worst one in this repo, landing in the single place this whole case exists to
    /// rule out.
    /// </para>
    /// </remarks>
    public const string FileScanVerified = "repro_file_scan_verified";

    /// <summary>How stale the checkpoint was on resume. A SEPARATE name from repro_heartbeat_staleness.</summary>
    /// <remarks>
    /// Not a reuse, and the test is the one <see cref="ActivityCancel"/> passes and this one
    /// fails: does the existing panel's description make a claim that a second contributor
    /// falsifies? repro_heartbeat_staleness is read as "0.8 x a 5s heartbeat timeout, so
    /// samples top out near 4s" and its bucket row stops at 30_000. This case's timeout is
    /// 30s, its samples cluster near 24s and run out to roughly 64s, so merging the two would
    /// move the heartbeat board's p95 by an order of magnitude while both boards go on
    /// claiming to measure their own case.
    /// <para>
    /// The same test rules out reusing repro_heartbeat_sent, <see cref="ActivityStarted"/>,
    /// <see cref="ActivityProgress"/> and the three repro_heartbeat_*_ms gauges: every one of
    /// those panels is an unfiltered max() or sum() across activity types.
    /// <see cref="ActivityCancel"/> IS reused, because a per-reason breakdown makes no claim
    /// that a second activity type falsifies.
    /// </para>
    /// </remarks>
    public const string FileScanStaleness = "repro_file_scan_staleness";

    /// <summary>One increment per activity attempt, tagged <c>retried</c> and <c>resumed</c>.</summary>
    /// <remarks>
    /// Mirrors <see cref="ActivityStarted"/> deliberately rather than reusing it, per the test
    /// in <see cref="FileScanStaleness"/>. Both tag values are LOWERCASED BY HAND:
    /// bool.ToString() returns "True", every dashboard selector matches retried="true", and
    /// the capitalized value does not error -- the panel is simply empty forever.
    /// HeartbeatActivities.Bool is the helper to copy.
    /// </remarks>
    public const string FileScanStarted = "repro_file_scan_started";

    /// <summary>Process-wide bytes allocated, advanced by a WATERMARK. See <see cref="ProcessPressure"/>.</summary>
    /// <remarks>
    /// A COUNTER over a process-wide CUMULATIVE value, which is the case that needs
    /// ProcessPressure's compare-exchange watermark: several concurrent scans each read
    /// GC.GetTotalAllocatedBytes, and each must add only the difference IT won, or the same
    /// bytes are counted once per in-flight scan.
    /// <para>
    /// MEASURED, and it is the reading a newcomer gets backwards. The raw-byte read path
    /// allocates nothing per row -- no string, no char[], one 65,536-byte buffer for the whole
    /// attempt -- so what this counter reports is fixed overhead, and the PER-BATCH HEARTBEAT
    /// dominates it rather than the sampler: one FileScanCheckpoint plus the params object[]
    /// that carries it into Heartbeat() is 117 B per batch. A 4m47s scan of the 100 MB corpus
    /// therefore reports about 415 KB in total -- roughly 336 KB of checkpoints, 71 KB of fixed
    /// read-buffer and FileStream cost, and 8.4 KB of pressure samples (29 x 288 B) -- which is
    /// 1.4 KB/s against 348 KB/s of reading, or 0.4%. A near-zero rate here is THE DEFAULT PATH
    /// WORKING, not a broken counter. Turn on fault.decodeRowsToStrings and the same counter
    /// reports 2.41x bytes read (measured), at which point every fixed cost above is noise.
    /// </para>
    /// </remarks>
    public const string FileScanBytesAllocated = "repro_file_scan_bytes_allocated";

    /// <summary>GC collections, tagged <see cref="Tags.Gen"/>. One watermark per generation.</summary>
    /// <remarks>
    /// READ <see cref="Gens"/> BEFORE BUILDING A PANEL ON THIS. The three values nest rather
    /// than partition, because GC.CollectionCount(g) counts generation g or higher.
    /// <para>
    /// gen="2" is ABSENT rather than zero on a shipped-config scan, and that is correct: Core
    /// creates a series on first increment, and nothing in the default read path promotes
    /// anything far enough to trigger a gen2 collection. It needs fault.retainScannedRows or
    /// fault.slurpWholeFile. The panel therefore takes no `or vector(0)` -- a standalone
    /// `sum by (gen)` with one would return a series carrying no gen label at all, which is a
    /// blank legend entry that joins nothing.
    /// </para>
    /// </remarks>
    public const string FileScanGcCollected = "repro_file_scan_gc_collected";

    /// <summary>Live managed heap: GC.GetTotalMemory(false), never true.</summary>
    /// <remarks>
    /// SAWTOOTH means garbage, STAIRCASE with no falling edge means retention. Drawing that
    /// contrast is the whole job of fault.decodeRowsToStrings against
    /// fault.retainScannedRows, and it is only legible because the default path's floor is
    /// flat.
    /// <para>
    /// The panel uses max(), never sum(): the value is process-wide, and sum() across two
    /// worker processes adds two unrelated heaps into a number that describes neither.
    /// </para>
    /// </remarks>
    public const string FileScanManagedHeapBytes = "repro_file_scan_managed_heap_bytes";

    /// <summary>Large object heap, GenerationInfo[3]. A LAST-GC SNAPSHOT, not a live reading.</summary>
    /// <remarks>
    /// NESTED, NOT PARTITIONED, and that is why the four byte gauges are four NAMES instead of
    /// one metric with a region label: the LOH is inside the managed heap, which is inside the
    /// working set. `sum by (region)` is this repo's reflex idiom and here it would double- and
    /// triple-count. A label when the values partition, separate names when they nest -- the
    /// same rule <see cref="Gens"/> sits on the other side of.
    /// <para>
    /// MEASURED, and it corrects the obvious reading of fault.slurpWholeFile. GCMemoryInfo
    /// describes the LAST GC rather than now, so this gauge does not step when the array is
    /// allocated; it steps at the next collection after that. Probed directly: a 100 MB
    /// File.ReadAllBytes left this reading 100.0 MiB -- the PREVIOUS GC's value -- until a
    /// forced blocking gen2 collect, after which it read 200.0 MiB. Before the process's very
    /// first GC every GenerationInfo entry, and TotalCommittedBytes with them, reads 0.
    /// </para>
    /// <para>
    /// Flat at zero is again THE DEFAULT PATH WORKING: a byte[n] reaches the 85,000-byte LOH
    /// threshold at n of 84,976 or more, and fileScan.bufferBytes ships at 65,536.
    /// </para>
    /// </remarks>
    public const string FileScanLohBytes = "repro_file_scan_loh_bytes";

    /// <summary>Environment.WorkingSet: what the OS thinks the process costs, resident.</summary>
    /// <remarks>
    /// MEASURED on macOS 26.6.2 arm64, because Environment.WorkingSet has historically
    /// returned 0 on some Unix targets: it returns a real value (37.2 MiB at process start),
    /// costs 1.3us per call and allocates nothing, so no fallback is needed. The obvious
    /// fallback would also have been WRONG -- GCMemoryInfo.TotalCommittedBytes reads 0 until
    /// the first GC, per <see cref="FileScanLohBytes"/>.
    /// <para>
    /// THE READING A NEWCOMER GETS WRONG, and the cleanest proof this case produces: the line
    /// stays FLAT through a 500 MB streaming scan, because the file data lives in the kernel
    /// page cache rather than in this process's address space. Probed in both directions -- an
    /// untouched live 100 MiB array left the working set unmoved at 139.4 MiB, since its pages
    /// were never faulted in, while File.ReadAllBytes of the same size stepped it to 239.8 MiB,
    /// since that path touches every byte. Flat here is streaming working; a step is
    /// fault.slurpWholeFile.
    /// </para>
    /// </remarks>
    public const string FileScanWorkingSetBytes = "repro_file_scan_working_set_bytes";

    /// <summary>GCMemoryInfo.PauseTimePercentage: why rows/s decayed.</summary>
    /// <remarks>
    /// MEASURED SEMANTICS, and they are not the ones the name suggests. This is NOT the
    /// percentage of the last logInterval spent paused. The runtime computes it at the END OF
    /// A GC and then leaves it alone: probed, it read 0.73 immediately after a forced gen2
    /// collect and still read 0.73 after 500ms of idle with no GC in between. So on a scan
    /// that triggers no collection this gauge reports the last collection's number forever,
    /// which under the shipped config means it reports the WORKER'S STARTUP GCs. Read it as
    /// "the runtime's most recent pause figure", and only believe movement in it when
    /// <see cref="FileScanGcCollected"/> is moving too.
    /// </remarks>
    public const string FileScanGcPausePercent = "repro_file_scan_gc_pause_percent";

    /// <remarks>
    /// Do NOT add namespace/task_queue/workflow_type/activity_type here. Both
    /// Workflow.MetricMeter and ActivityExecutionContext.MetricMeter arrive
    /// pre-tagged with them.
    /// <para>
    /// <see cref="Attempt"/> is the ONE deliberate exception to that rule, on the two
    /// file-scan cost counters only. It is safe because retry.maximumAttempts bounds it;
    /// <see cref="FileScanRowsRead"/> carries the argument, including why the same tag on a
    /// gauge would blank out the panel it was added to help.
    /// </para>
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

        /// <summary>
        /// ActivityInfo.Attempt, on <see cref="FileScanRowsRead"/> and
        /// <see cref="FileScanBytesRead"/> ONLY. Formatted with
        /// CultureInfo.InvariantCulture, because CA1305 is a build error here.
        /// </summary>
        public const string Attempt = "attempt";

        /// <summary>The idempotency verdict: see <see cref="Results"/>.</summary>
        public const string Result = "result";

        /// <summary>Which GC generation was collected: see <see cref="Gens"/>.</summary>
        public const string Gen = "gen";
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

    /// <summary>The values of the <c>result</c> tag on <see cref="FileScanVerified"/>.</summary>
    /// <remarks>
    /// TWO values, and unlike <see cref="Sources"/> there is no third for "not applicable".
    /// The check is a closed form over numbers the activity already holds, so every scan that
    /// reaches the end produces exactly one of these and a scan that does not reach the end
    /// produces neither. `sum by (result)` therefore accounts for 100% of COMPLETED scans, and
    /// the count of scans is a different question that <see cref="FileScanCompleted"/> answers.
    /// <para>
    /// <see cref="Mismatch"/> is the one value in this file that must be unreachable in a
    /// healthy run and must still exist as a name. An idempotency bug that had nowhere to be
    /// counted would present as a slightly wrong number in a log line nobody re-reads.
    /// </para>
    /// </remarks>
    public static class Results
    {
        /// <summary>indexSum equals rows x (rows + 1) / 2. Resume was idempotent.</summary>
        public const string Match = "match";

        /// <summary>
        /// It did not. Rows were double-counted or skipped, and the activity throws
        /// non-retryable rather than returning the number.
        /// </summary>
        public const string Mismatch = "mismatch";
    }

    /// <summary>The values of the <c>gen</c> tag on <see cref="FileScanGcCollected"/>.</summary>
    /// <remarks>
    /// A LABEL rather than three metric names -- and the usual justification for that, "the
    /// values partition the quantity", is FALSE here. MEASURED: GC.CollectionCount(g) counts
    /// collections of generation g OR HIGHER. From 0/0/0, two forced gen0 collects then one
    /// gen1 then one gen2 read 4/2/1, not 2/1/1. A single gen2 collection increments all three
    /// counters.
    /// <para>
    /// So gen="0" is always at least gen="1", which is always at least gen="2", the three
    /// lines NEST, and each one reads "collections of this generation or higher". `sum by
    /// (gen)` groups rather than adds, so the panel still renders three correct lines; what
    /// breaks is adding those three lines together to get "total collections", which
    /// triple-counts every gen2.
    /// </para>
    /// <para>
    /// Published raw anyway, deliberately. Raw is what GC.CollectionCount means, what
    /// dotnet-counters prints and what every other .NET exporter publishes; differencing them
    /// here into an exclusive partition would produce a gen0 line that agrees with no other
    /// tool on the machine.
    /// </para>
    /// <para>
    /// The label still earns its place: these are the runtime's own generation numbers, there
    /// are exactly three of them forever, and no collection is missing from any line. Contrast
    /// the four byte gauges, which nest in the CONTAINMENT sense and therefore get separate
    /// names -- see <see cref="FileScanLohBytes"/>.
    /// </para>
    /// </remarks>
    public static class Gens
    {
        public const string Gen0 = "0";
        public const string Gen1 = "1";

        /// <summary>ABSENT rather than zero at the shipped config. See <see cref="FileScanGcCollected"/>.</summary>
        public const string Gen2 = "2";
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

        /// <summary>A worker drain cut it short: <c>WorkerShutdownToken</c> fired.</summary>
        public const string Shutdown = "shutdown";

        /// <summary>
        /// The activity was cancelled mid-burn: <c>ActivityExecutionContext.CancellationToken</c>
        /// fired. In this case that overwhelmingly means the WORKFLOW TASK TIMED OUT.
        /// </summary>
        /// <remarks>
        /// MEASURED, and it is the finding this case turned up that no doc predicted. Across 17
        /// cut-short burns in one run of the demo, EVERY ONE ended between 64.0s and 64.2s
        /// against a 1m history.workflowTaskHeartbeatTimeout -- never at the requested duration
        /// and never at a drain. So a local activity IS told when the workflow task it lives
        /// inside times out, roughly four seconds after the server's timeout fires, rather than
        /// running on obliviously to its full length.
        /// <para>
        /// It changes nothing about the outcome. The workflow task is already gone, so the
        /// estimate this activity returns is discarded and the next dispatch starts the burn
        /// again from zero. What it does change is the CPU arithmetic: a doomed run burns about
        /// 64s per execution rather than its full drawn duration.
        /// </para>
        /// <para>
        /// Distinguished from <see cref="Shutdown"/> because the two are the same shape and
        /// wildly different events, and folding them together is how the first version of this
        /// activity came to log "worker drain cut the burn short" seventeen times during a demo
        /// in which nothing had drained.
        /// </para>
        /// </remarks>
        public const string Canceled = "canceled";
    }

    /// <summary>
    /// Values of the <c>outcome</c> tag: four on repro_workflow_completed and
    /// repro_simple_activity_completed, three on repro_simple_completed, and three of a
    /// possible four on repro_local_activity_completed.
    /// </summary>
    /// <remarks>
    /// repro_workflow_completed uses completed / failed / canceled / timed_out.
    /// repro_simple_completed uses stopped / expired / canceled.
    /// repro_simple_activity_completed uses completed / failed / canceled / timed_out, and
    /// timed_out THERE is TimeoutType.StartToClose, never Heartbeat, because that
    /// workflow sets no heartbeat timeout at all.
    /// <para>
    /// repro_local_activity_completed is the one that does not fit the pattern, and reading it
    /// as if it did is the mistake this paragraph exists to prevent. WorkflowLocalActivity's
    /// Classify can return all four, but at the SHIPPED config only three are reachable:
    /// timed_out requires localActivity.scheduleToCloseTimeout to sit BELOW the workflow task
    /// heartbeat timeout, which is the documented mitigation rather than the default.
    /// </para>
    /// <para>
    /// Worse, the majority case records NOTHING. The two-thirds of runs whose burn outlives the
    /// 1m heartbeat timeout end at RunTimeout, and the server closes a run on RunTimeout WITHOUT
    /// scheduling a workflow task -- so the workflow's catch never executes and no outcome is
    /// ever tagged. This counter therefore undercounts runs by design, and a dashboard that
    /// divides by it is measuring the wrong denominator. Count the missing ones with the
    /// server's workflow_timeout in the repro-local-activity namespace, or with
    /// <see cref="PiAttemptStarted"/>, which the ACTIVITY emits and which survives.
    /// </para>
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
