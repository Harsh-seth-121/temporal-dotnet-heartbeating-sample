namespace Repro.Core.Config;

/// <summary>
/// Every knob for the sandbox; docs/CONFIG.md holds the field table and the derivations. Any key may be omitted and these
/// initializers are the defaults, but an unknown key is a hard error.
/// </summary>
public sealed class ReproConfig
{
    public string Address { get; set; } = "localhost:7233";

    public string Namespace { get; set; } = "default";

    /// <summary>
    /// Task queue for every binary. Keep the hyphen: Core does not sanitize label values, so this reads
    /// <c>task_queue="repro-task-queue"</c> on :8077 while the server's own tally metrics sanitize it to
    /// <c>taskqueue="repro_task_queue"</c> on :8000, and you cannot join the two.
    /// </summary>
    public string TaskQueue { get; set; } = "repro-task-queue";

    /// <summary>Fixed ID the starter uses, so `temporal workflow describe -w repro-workflow` always works.</summary>
    public string WorkflowId { get; set; } = "repro-workflow";

    /// <summary>Temporal Cloud API key. Setting this turns TLS on.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public TlsConfig Tls { get; set; } = new();

    public MetricsConfig Metrics { get; set; } = new();

    public JobConfig Job { get; set; } = new();

    public ActivityConfig Activity { get; set; } = new();

    public WorkerConfig Worker { get; set; } = new();

    /// <summary>
    /// The loadgen's first driver loop, which runs the seed job. Spelled Loadgen, not LoadGen: CamelCaseNamingConvention lowers
    /// only the first character, so LoadGen would map to <c>loadGen</c> while the file says <c>loadgen</c>, and an unmatched
    /// key is a hard error. <see cref="SimpleActivity"/> is safe: it maps to <c>simpleActivity</c>. Keep names single-humped.
    /// </summary>
    public LoadgenConfig Loadgen { get; set; } = new();

    /// <summary>SimpleNoActivity: the run bound plus the chaos driver.</summary>
    public SimpleConfig Simple { get; set; } = new();

    /// <summary>WorkflowSimpleActivity: one activity, no heartbeats.</summary>
    public SimpleActivityConfig SimpleActivity { get; set; } = new();

    /// <summary>WorkflowLocalActivity: one CPU-bound local activity.</summary>
    public LocalActivityConfig LocalActivity { get; set; } = new();

    /// <summary>WorkflowFileScan: the corpus, the pace, and its own queue.</summary>
    public FileScanConfig FileScan { get; set; } = new();

    public FaultConfig Fault { get; set; } = new();
}

public sealed class TlsConfig
{
    public string CertPath { get; set; } = string.Empty;

    public string KeyPath { get; set; } = string.Empty;

    /// <summary>Maps to <c>TlsOptions.Domain</c>: override when the cert CN differs from the address.</summary>
    public string ServerName { get; set; } = string.Empty;

    public string ServerCaPath { get; set; } = string.Empty;
}

/// <summary>Where SDK metrics go. Both listen addresses go through <see cref="BindAddress"/>.</summary>
public sealed class MetricsConfig
{
    public string ListenAddress { get; set; } = "0.0.0.0:8077";

    public string LoadgenAddress { get; set; } = "0.0.0.0:8078";

    /// <summary>Must include the <c>/metrics</c> path: prometheus-net does not add it.</summary>
    public string PushgatewayUrl { get; set; } = "http://localhost:9091/metrics";

    public string PushJob { get; set; } = "temporal_starter";

    /// <summary>Keep stable. A run id or timestamp here leaks Pushgateway groups forever.</summary>
    public string PushInstance { get; set; } = "local";

    /// <summary>
    /// How long the starter waits after closing its client before the final push. Core buffers metric updates, delivers them
    /// on its own threads, and exposes no flush API. Too short and the starter's last temporal_request samples never reach the
    /// Pushgateway, with no error: the group just has fewer series. Tune it down until samples go missing, then back off.
    /// </summary>
    public TimeSpan PushSettle { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>Shape of the seed job. Steps x StepDuration is roughly the activity's runtime.</summary>
public sealed class JobConfig
{
    public int Steps { get; set; } = 60;

    public TimeSpan StepDuration { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// The activity's timeouts and retry policy, reaching the workflow through its input rather than the file, so
/// <c>JobInput.Activity</c>'s defaults must match these. See docs/CONFIG.md, "The activity.* rows reach the
/// workflow through its input, not through the file".
/// </summary>
public sealed class ActivityConfig
{
    /// <summary>Required for cancellation to reach the activity, and the input to min(0.8 x this, MaxHeartbeatThrottleInterval).</summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan StartToCloseTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan ScheduleToCloseTimeout { get; set; } = TimeSpan.FromHours(1);

    public RetryConfig Retry { get; set; } = new();
}

public sealed class RetryConfig
{
    public TimeSpan InitialInterval { get; set; } = TimeSpan.FromSeconds(1);

    public double BackoffCoefficient { get; set; } = 2.0;

    public TimeSpan MaximumInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>0 means unlimited, matching Temporalio.Common.RetryPolicy. Write 1 for "do not retry".</summary>
    public int MaximumAttempts { get; set; } = 5;
}

public sealed class WorkerConfig
{
    /// <summary>The SDK default is zero, and zero grace plus a long heartbeating activity is the hang this repo demonstrates.</summary>
    public TimeSpan GracefulShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxHeartbeatThrottleInterval { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan DefaultHeartbeatThrottleInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 0 leaves the SDK default (10000). Set it to 1 or 2 to force sticky-cache evictions; until one fires,
    /// temporal_sticky_cache_total_forced_eviction is absent from /metrics rather than 0.
    /// </summary>
    public int MaxCachedWorkflows { get; set; }

    /// <summary>0 leaves the SDK default (100). Applied by Repro.Worker and Repro.LoadGen alike.</summary>
    public int MaxConcurrentActivities { get; set; }

    public int MaxConcurrentWorkflowTasks { get; set; }
}

public sealed class LoadgenConfig
{
    /// <summary>Mean interval between seed jobs. Each runs for job.steps x job.stepDuration, so a short rate skips most ticks.</summary>
    public TimeSpan Rate { get; set; } = TimeSpan.FromSeconds(5);

    public int Concurrency { get; set; } = 8;

    /// <summary>Loadgen runs shorter jobs than the starter, so a board fills in under a minute.</summary>
    public int Steps { get; set; } = 20;
}

/// <summary>SimpleNoActivity's run bound, plus every knob of the loadgen's second driver loop.</summary>
public sealed class SimpleConfig
{
    /// <summary>Turn the second driver loop off. <c>--no-simple</c> does the same.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>WaitConditionAsync timeout, so an unsignalled run still ends. Keep it under demo-down.sh's 45s drain budget.</summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Mean interval between starts, before jitter.</summary>
    public TimeSpan Rate { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Interval is <c>rate x [1-jitter, 1+jitter]</c>. Validated under 1: at 1 the low end is zero and the loop spins.</summary>
    public double Jitter { get; set; } = 0.5;

    /// <summary>Max runs in flight. At capacity a tick is skipped, never queued.</summary>
    public int Concurrency { get; set; } = 8;

    public int MinMessages { get; set; }

    public int MaxMessages { get; set; } = 5;

    /// <summary>Upper bound on the random gap between two messages within one run.</summary>
    public TimeSpan MessageGap { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Fraction of Add updates given overflowing operands. The validator rejects them, writing nothing to history.</summary>
    public double OverflowRate { get; set; } = 0.05;

    /// <summary>Fraction of runs sent a message after they close. Expect RpcException/NotFound, which the driver counts.</summary>
    public double RaceRate { get; set; } = 0.10;

    /// <summary>Weighted dice for how each run ends. Any non-negative ints; only the ratio matters.</summary>
    public int StopWeight { get; set; } = 5;

    /// <summary>Real client-side CancelAsync. The only path that produces a Canceled status.</summary>
    public int CancelWeight { get; set; } = 3;

    /// <summary>Send nothing and let <see cref="MaxDuration"/> end it.</summary>
    public int ExpireWeight { get; set; } = 2;
}

/// <summary>
/// WorkflowSimpleActivity's job shape, timeouts, and the loadgen's third driver loop: one activity with a start-to-close
/// timeout and a retry policy, no heartbeat timeout. See docs/CONFIG.md, "simpleActivity and the synthetic fallback".
/// </summary>
public sealed class SimpleActivityConfig
{
    /// <summary>Turn the third driver loop off. <c>--no-simple-activity</c>, not <c>--no-simple</c>, does the same.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Sleeps in the activity, not a workflow timer, so it holds a slot and floors repro_simple_activity_latency, whose
    /// HistogramBuckets row has boundaries just above 5000ms. Change this and that row is wrong.
    /// </summary>
    public TimeSpan SleepDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Per attempt, validated above sleep + http + 2s. With no heartbeat timeout it is the only one that can fire.</summary>
    public TimeSpan StartToCloseTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Bound on the Open-Meteo call, enforced by the activity so a blackholed route logs its elapsed time.</summary>
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>The activity's retry policy. MaximumAttempts may not be 0 here: this activity calls a third party.</summary>
    public RetryConfig Retry { get; set; } = new();

    /// <summary>Degrees north, validated to [-90, 90]. Out of range is an HTTP 400 the activity refuses to retry.</summary>
    public double Latitude { get; set; } = 47.6062;

    /// <summary>Degrees east, validated to [-180, 180]. Same reasoning as <see cref="Latitude"/>.</summary>
    public double Longitude { get; set; } = -122.3321;

    /// <summary>Point it at <c>http://127.0.0.1:1/forecast</c> to exercise the synthetic fallback. Injected via the constructor.</summary>
    public string BaseUrl { get; set; } = "https://api.open-meteo.com/v1/forecast";

    /// <summary>When true an unreachable Open-Meteo throws instead of a synthetic reading. No initializer: CA1805 forbids <c>= false</c>.</summary>
    public bool RequireLiveWeather { get; set; }

    /// <summary>Mean interval between starts. Slower than simple.rate to stay inside Open-Meteo's free tier.</summary>
    public TimeSpan Rate { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Fractional spread on <see cref="Rate"/>. Same contract as <see cref="SimpleConfig.Jitter"/>.</summary>
    public double Jitter { get; set; } = 0.5;

    /// <summary>Max runs in flight. At capacity a tick is skipped, never queued.</summary>
    public int Concurrency { get; set; } = 4;
}

/// <summary>
/// WorkflowLocalActivity's job shape, timeouts, namespace, and the loadgen's fourth driver loop. A local activity runs
/// inside the workflow task: MarkerRecorded rather than ActivityTaskScheduled, its own slot type, and no heartbeat.
/// See docs/CONFIG.md, "localActivity, and the second namespace".
/// </summary>
public sealed class LocalActivityConfig
{
    /// <summary>Turn the fourth driver loop off. <c>--no-local-activity</c> does the same.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary><c>history.workflowTaskHeartbeatTimeout</c> is namespace-scoped, so this case needs its own namespace, client and worker.</summary>
    public string Namespace { get; set; } = "repro-local-activity";

    /// <summary>Its own queue. ConfigLoader rejects a pair where this or <see cref="ReproConfig.TaskQueue"/> prefixes the other.</summary>
    public string TaskQueue { get; set; } = "repro-la-queue";

    /// <summary>Lower bound of the per-run duration draw.</summary>
    public TimeSpan MinDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound of the uniform draw. 30s..2m against a 1m heartbeat timeout re-executes exactly 2/3 of runs.</summary>
    /// <remarks>The only duration here that outlasts demo-down.sh's drain budget. Safe only because the
    /// burn loop polls ctx.WorkerShutdownToken; raise DEMO_DRAIN_TIMEOUT if you raise this.</remarks>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Per attempt. Unreachable at the shipped config; the SDK requires one of the two rungs.</summary>
    public TimeSpan StartToCloseTimeout { get; set; } = TimeSpan.FromSeconds(150);

    /// <summary>
    /// Its clock restarts on every workflow-task re-dispatch, so above the 1m heartbeat timeout it never fires, which is
    /// the repro. Set it below that timeout for the documented fix; ConfigLoader deliberately allows that.
    /// </summary>
    public TimeSpan ScheduleToCloseTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The only rung that stops the re-execution loop. The server closes the run without scheduling a workflow task, so
    /// repro_local_activity_completed never increments and repro_pi_attempt_started is this case's primary signal.
    /// </summary>
    public TimeSpan RunTimeout { get; set; } = TimeSpan.FromMinutes(6);

    /// <summary>May not be 0: unset means retry forever for a local activity and 0 means unlimited. It does not bound re-execution.</summary>
    public RetryConfig Retry { get; set; } = new() { MaximumAttempts = 1 };

    /// <summary>Mean interval between starts, before jitter.</summary>
    public TimeSpan Rate { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Fractional spread on <see cref="Rate"/>. Same contract as <see cref="SimpleConfig.Jitter"/>.</summary>
    public double Jitter { get; set; } = 0.5;

    /// <summary>Max runs in flight. Occupancy is ~255s per run, so at concurrency 1 a 30-minute window is nearly empty.</summary>
    public int Concurrency { get; set; } = 3;

    /// <summary>
    /// Worker-side cap on concurrent local activities. The SDK default of 100 is a real hazard here: workflow activations run
    /// on the same thread pool these CPU burns occupy, and the SDK fails a workflow task that does not yield within 2 seconds,
    /// so a starved pool produces evicted runs and retried workflow tasks that look exactly like the heartbeat-timeout repro.
    /// Local activities have their own slot type, <c>worker_type="LocalActivityWorker"</c>, not
    /// <see cref="WorkerConfig.MaxConcurrentActivities"/>.
    /// </summary>
    public int MaxConcurrentLocalActivities { get; set; } = 4;
}

/// <summary>
/// Makes the seed workflow produce interesting signal; all zero or false and every failure, retry and heartbeat panel
/// sits at zero. Reached through <c>HeartbeatActivities</c>'s constructor, so workflow code has no global to read.
/// </summary>
public sealed class FaultConfig
{
    /// <summary>
    /// Fraction of activity attempts that throw a retryable failure. One roll per attempt, outside the step loop, so
    /// P(the workflow fails) is FailureRate ^ maximumAttempts, 0.15^5 as shipped.
    /// </summary>
    public double FailureRate { get; set; }

    /// <summary>Added to every step.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>
    /// Sleep past heartbeatTimeout without heartbeating, on attempt 1 only. The server times the attempt out and the activity
    /// keeps running, since the server's only channel is the heartbeat response.
    /// </summary>
    public bool StallPastHeartbeatTimeout { get; set; }

    /// <summary>
    /// Keep working but never call Heartbeat(). Not gated to attempt 1, so the retry policy is exhausted and the terminal
    /// failure is ActivityFailure -> TimeoutFailure{Heartbeat}: outcome timed_out.
    /// </summary>
    public bool StopHeartbeating { get; set; }

    /// <summary>Swallow cancellation and finish the batch. TemporalWorker.ExecuteAsync then blocks until the batch ends.</summary>
    public bool IgnoreCancellation { get; set; }
    /// <summary>
    /// File scan: decode every row and drop it. Allocation is not growth. About 140 bytes of Gen0 garbage per row puts
    /// allocated near 2.4x bytes read with a flat live-heap floor, against a default path that allocates nothing.
    /// </summary>
    public bool DecodeRowsToStrings { get; set; }

    /// <summary>
    /// File scan: the same decode, retained for the attempt. Retention grows the heap. The list is pre-sized from the corpus
    /// header, or its doubling would move the LOH panel for an unrelated reason. Refused with fileScan.concurrency > 1.
    /// </summary>
    public bool RetainScannedRows { get; set; }

    /// <summary>
    /// File scan: File.ReadAllBytes the corpus first. One LOH object, and the LOH is not compacted. It causes no heartbeat
    /// timeout, and being synchronous it holds an activity-task thread throughout.
    /// </summary>
    public bool SlurpWholeFile { get; set; }
}

/// <summary>
/// WorkflowFileScan: a long, resumable scan of one generated corpus in sample_files/. See docs/CONFIG.md, "fileScan,
/// and the corpus it does not check for", for the corpus contract and every timeout derivation below. Magnitudes
/// quoted here assume workstation GC.
/// </summary>
public sealed class FileScanConfig
{
    /// <summary>Turn the fifth driver loop off. <c>--no-file-scan</c> does the same.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The corpus, resolved absolute against the config file's directory, not the cwd: a cwd-relative path would mean two
    /// different files across a resume. ConfigLoader never stats it.
    /// </summary>
    public string Path { get; set; } = "sample_files/sample-100mb.txt";

    /// <summary>
    /// Its own queue, same namespace: temporal_worker_task_slots_used carries no activity_type label and the heartbeat
    /// board sums it unfiltered, so a separate queue lets that panel pin task_queue and exclude the scan.
    /// </summary>
    public string TaskQueue { get; set; } = "repro-scan-queue";

    /// <summary>
    /// Rows per second; 0 is the unthrottled sentinel. The knob that makes the scan long-running: unthrottled it finishes the
    /// 500 MB corpus in single-digit seconds, under one throttle interval, so the case would show neither resume nor
    /// pressure. At 6000 with a 30s heartbeat timeout a kill -9 redoes 144,000 rows, 8.35% of the 100 MB corpus.
    /// </summary>
    public long TargetRowsPerSecond { get; set; } = 6000;

    /// <summary>
    /// Rows between one pace, cancel, drain, heartbeat and log check and the next, so it is the loop's reaction time. Batched
    /// because Task.Delay's ~1ms floor cannot express 167us per row; the period is bounded to [10ms, 2s].
    /// </summary>
    public int BatchRows { get; set; } = 600;

    /// <summary>
    /// Read buffer, bytes, and the only buffer. A byte[n] reaches the 85,000-byte LOH threshold at n >= 84,976, so the shipped
    /// 65536 is SOH and the LOH gauge reads a true zero. Raising it past ~83 KiB crosses that threshold.
    /// </summary>
    public int BufferBytes { get; set; } = 65_536;

    /// <summary>Stop after this many rows; 0 is the whole file. A checkpoint from a larger value is a different job.</summary>
    public long MaxRows { get; set; }

    /// <summary>Wall clock between progress lines and pressure samples, one interval feeding both sinks.</summary>
    public TimeSpan LogInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Chosen for the staleness it produces, not for liveness: the gap between two Heartbeat() calls is at most one batch
    /// period. What it sets is the throttle, min(0.8 x this, worker.maxHeartbeatThrottleInterval) = 24s, and so how much work a
    /// kill -9 loses the record of. Past 75s the second term binds and it saturates.
    /// </summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Bounds one attempt, so it covers the shipped corpora; ValidateFileScan raises the floor if you slow the scan.</summary>
    public TimeSpan StartToCloseTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Total across every attempt. "attempts x startToClose" is the wrong model: useful work is one worst-case scan, and
    /// each resume adds heartbeatTimeout + retry.maximumInterval + throttle, 64s as shipped.
    /// </summary>
    public TimeSpan ScheduleToCloseTimeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// 10 attempts, not the repo's usual 5: each kill -9 spends one and docs/HEARTBEATING.md's recipe does three cycles, so at
    /// 5 one careless extra kill fails the workflow terminally.
    /// </summary>
    public RetryConfig Retry { get; set; } = new() { MaximumAttempts = 10 };

    /// <summary>One scan started every rate, plus or minus jitter.</summary>
    public TimeSpan Rate { get; set; } = TimeSpan.FromMinutes(6);

    /// <summary>Fraction of Rate to jitter by, 0 to 1.</summary>
    public double Jitter { get; set; } = 0.2;

    /// <summary>
    /// In-flight scans the loadgen will allow; over capacity it skips. A pure multiplier on every byte, allocation and buffer,
    /// all sharing one heap and one thread pool. ConfigLoader refuses it above 1 when fault.retainScannedRows is on.
    /// </summary>
    public int Concurrency { get; set; } = 1;
}
