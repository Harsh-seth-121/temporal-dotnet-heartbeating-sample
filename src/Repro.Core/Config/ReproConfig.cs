namespace Repro.Core.Config;

/// <summary>
/// Every knob for the sandbox. A repro is described by config.yaml plus the
/// workflow and activity, and nothing else.
/// </summary>
/// <remarks>
/// Mirrors the Go original's single-Config design. Any key may be omitted; the
/// property initializers here are the defaults, so a stripped-down config.yaml
/// still works.
/// </remarks>
public sealed class ReproConfig
{
    public string Address { get; set; } = "localhost:7233";

    public string Namespace { get; set; } = "default";

    /// <summary>
    /// Keep the hyphen. Core does NOT sanitize label values, so this appears as
    /// <c>task_queue="repro-task-queue"</c> on :8077 while the Temporal server's
    /// own tally metrics sanitize it to <c>taskqueue="repro_task_queue"</c> on
    /// :8000. One queue, two spellings, and you cannot join them. That asymmetry
    /// is new in the .NET port and is the single most surprising thing here.
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

    /// <remarks>
    /// Spelled Loadgen, not LoadGen. CamelCaseNamingConvention lowers only the FIRST
    /// character, so "LoadGen" would map to the YAML key "loadGen" while the file
    /// says "loadgen" — and an unmatched key is a hard error here, by design.
    /// </remarks>
    public LoadgenConfig Loadgen { get; set; } = new();

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

/// <summary>Where SDK metrics go.</summary>
/// <remarks>
/// The listen addresses must be a full IP:port that is NOT loopback. See
/// <see cref="BindAddress"/> for why both halves of that matter.
/// </remarks>
public sealed class MetricsConfig
{
    public string ListenAddress { get; set; } = "0.0.0.0:8077";

    public string LoadgenAddress { get; set; } = "0.0.0.0:8078";

    /// <summary>Must include the <c>/metrics</c> path: prometheus-net does not add it.</summary>
    public string PushgatewayUrl { get; set; } = "http://localhost:9091/metrics";

    public string PushJob { get; set; } = "temporal_starter";

    /// <summary>Keep STABLE. A run id or timestamp here leaks Pushgateway groups forever.</summary>
    public string PushInstance { get; set; } = "local";

    /// <summary>
    /// How long the starter waits after closing its client before the final push.
    /// </summary>
    /// <remarks>
    /// The one genuinely fragile part of this port. Core buffers metric updates and
    /// delivers them to the custom meter on its own threads, and it exposes no
    /// flush API. Too short and the starter's last temporal_request samples are
    /// missing from the Pushgateway; there is no error, the group just has fewer
    /// series than it should. Tune it down until samples go missing to find the
    /// real floor on your machine, then back off.
    /// </remarks>
    public TimeSpan PushSettle { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>Shape of the seed job. Steps x StepDuration is roughly the activity's runtime.</summary>
public sealed class JobConfig
{
    public int Steps { get; set; } = 60;

    public TimeSpan StepDuration { get; set; } = TimeSpan.FromSeconds(1);
}

public sealed class ActivityConfig
{
    /// <summary>
    /// REQUIRED for the activity to receive cancellation at all, and the input to
    /// the throttle formula min(HeartbeatTimeout * 0.8, MaxHeartbeatThrottleInterval).
    /// At 5s the SDK sends at most one heartbeat every 4s no matter how often the
    /// activity calls Heartbeat().
    /// </summary>
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

    /// <summary>0 means unlimited, matching Temporalio.Common.RetryPolicy.</summary>
    public int MaximumAttempts { get; set; } = 5;
}

public sealed class WorkerConfig
{
    /// <summary>
    /// The SDK default is TimeSpan.Zero. Zero grace plus a minute-long heartbeating
    /// activity is the hang this repo demonstrates on purpose; leaving it at the
    /// default by accident is how you suffer it instead.
    /// </summary>
    public TimeSpan GracefulShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxHeartbeatThrottleInterval { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan DefaultHeartbeatThrottleInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 0 leaves the SDK default (10000). Set it LOW (1-2) to force sticky-cache
    /// evictions on demand.
    /// </summary>
    /// <remarks>
    /// This is the only way to make the sticky-cache and replay-pressure panels move
    /// on a laptop. At the default of 10000 nothing is ever evicted, no workflow is
    /// ever replayed from scratch, and temporal_sticky_cache_total_forced_eviction is
    /// never even CREATED — Core registers a counter on first increment, so a metric
    /// that has never fired is absent from /metrics entirely rather than reading 0.
    /// </remarks>
    public int MaxCachedWorkflows { get; set; }

    /// <summary>0 leaves the SDK default (100). Mutually exclusive with a Tuner, which this repo does not set.</summary>
    public int MaxConcurrentActivities { get; set; }

    public int MaxConcurrentWorkflowTasks { get; set; }
}

public sealed class LoadgenConfig
{
    /// <summary>
    /// Go used 500ms because its seed activity returned instantly. This one runs
    /// for job.steps x job.stepDuration, so 500ms would skip almost every tick and
    /// the flag would be a lie about what the process is doing.
    /// </summary>
    public TimeSpan Rate { get; set; } = TimeSpan.FromSeconds(5);

    public int Concurrency { get; set; } = 8;

    /// <summary>Loadgen runs shorter jobs than the starter, so a board fills in under a minute.</summary>
    public int Steps { get; set; } = 20;
}

/// <summary>
/// Makes the seed workflow produce interesting signal. All zero/false means the
/// activity always succeeds and every failure, retry and heartbeat panel sits at
/// zero.
/// </summary>
/// <remarks>
/// READ BY ACTIVITIES ONLY. It reaches the activity through
/// <c>HeartbeatActivities</c>'s constructor, which is deliberate: with constructor
/// injection there is no ambient global for workflow code to reach, so the Go
/// original's "never read faultConfig from workflow code" rule is enforced by the
/// type system instead of by a comment. Reading a mutable process global from
/// workflow code is a determinism violation.
/// </remarks>
public sealed class FaultConfig
{
    /// <summary>Fraction of activity ATTEMPTS that throw a retryable failure. 0-1.</summary>
    public double FailureRate { get; set; }

    /// <summary>Added to every step.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>
    /// Sleep past heartbeatTimeout without heartbeating, on attempt 1 only. Two
    /// things happen in order and both are the point: the server times the ATTEMPT
    /// out, and we keep running, because the only channel the server has to tell us
    /// is the response to a heartbeat RPC and we are not sending any.
    /// </summary>
    public bool StallPastHeartbeatTimeout { get; set; }

    /// <summary>
    /// Keep doing work but never call Heartbeat(). Proves an activity that stops
    /// heartbeating can never be cancelled.
    /// </summary>
    public bool StopHeartbeating { get; set; }

    /// <summary>
    /// Swallow OperationCanceledException and finish the batch anyway. Proves
    /// TemporalWorker.ExecuteAsync does not return until every executing activity
    /// returns. THIS WEDGES YOUR TERMINAL FOR THE REST OF THE BATCH. That is the demo.
    /// </summary>
    public bool IgnoreCancellation { get; set; }
}
