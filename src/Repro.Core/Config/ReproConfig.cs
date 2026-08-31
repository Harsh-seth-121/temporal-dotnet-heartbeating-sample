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

    /// <summary>Everything about SimpleNoActivity: the run bound plus the chaos driver.</summary>
    public SimpleConfig Simple { get; set; } = new();

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

/// <summary>The timeouts and retry policy the workflow schedules the activity with.</summary>
/// <remarks>
/// NOT WIRED YET, and the only block in <see cref="ReproConfig"/> that is not the
/// source of truth for what it names. HeartbeatWorkflow builds its ActivityOptions
/// from JobInput.Activity rather than from here, deliberately: options carried in the
/// input are recorded in the history, so a replay reproduces them byte for byte,
/// while a file that can be edited between the original execution and the replay
/// cannot promise that. ActivityOptionsInput.From projects this class onto that
/// input, but no call site does so yet, so the workflow falls back to
/// ActivityOptionsInput's defaults — which are exactly the values below.
/// <para>
/// The consequence to know before you debug: today
/// <see cref="HeartbeatTimeout"/> and <see cref="StartToCloseTimeout"/> only affect
/// what ConfigLoader.Validate refuses to start on, and the other two affect nothing.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// Applied by Repro.Worker AND Repro.LoadGen. The loadgen used to drop both slot
    /// counts on the floor, so :8078 ran at 100/100 whatever this file said and the
    /// slot-saturation panels could only ever be driven from the :8077 worker.
    /// </remarks>
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
/// SimpleNoActivity's run bound, plus every knob of the loadgen's second driver loop.
/// </summary>
/// <remarks>
/// Flat and single-hump on purpose. CamelCaseNamingConvention lowers only the FIRST
/// character, so a property named MaxMessages maps to the YAML key <c>maxMessages</c> --
/// but a property named MinMsgIDs would map to <c>minMsgIDs</c>, and an unmatched key is
/// a hard error here. Keep new names boring.
/// </remarks>
public sealed class SimpleConfig
{
    /// <summary>Turn the second driver loop off without editing the loadgen. <c>--no-simple</c> does the same.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Carried into SimpleInput and used as the WaitConditionAsync timeout, so a run that
    /// is never signalled still ends.
    /// </summary>
    /// <remarks>
    /// KEEP THIS UNDER demo-down.sh's drain budget, which is
    /// worker.gracefulShutdownTimeout + 15 = 45s with the shipped config. A longer bound
    /// means a teardown that arrives mid-run gets SIGKILLed instead of draining.
    /// </remarks>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Mean interval between starts, before jitter.</summary>
    public TimeSpan Rate { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Fractional spread on <see cref="Rate"/>: the interval is
    /// <c>rate x [1-jitter, 1+jitter]</c>. 0 is a metronome.
    /// </summary>
    /// <remarks>
    /// Validated to be under 1. At exactly 1 the low end of the range is zero and the
    /// driver loop becomes a busy spin against the frontend.
    /// </remarks>
    public double Jitter { get; set; } = 0.5;

    /// <summary>Max runs in flight. At capacity a tick is SKIPPED, never queued.</summary>
    public int Concurrency { get; set; } = 8;

    public int MinMessages { get; set; }

    public int MaxMessages { get; set; } = 5;

    /// <summary>Upper bound on the random gap between two messages within one run.</summary>
    public TimeSpan MessageGap { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Fraction of Add updates handed operands whose sum overflows an int. 0-1.</summary>
    /// <remarks>
    /// The workflow's update validator rejects these. It is the only thing in the repo
    /// that exercises a rejected update, and a rejected update writes NOTHING to history
    /// -- which is the whole reason validators exist.
    /// </remarks>
    public double OverflowRate { get; set; } = 0.05;

    /// <summary>Fraction of runs sent one more message AFTER they have closed. 0-1.</summary>
    /// <remarks>
    /// Signalling a closed workflow is an RpcException with StatusCode.NotFound, not a
    /// crash. A client that does not expect that is one bad deploy from a restart loop,
    /// so this drives the path on purpose.
    /// </remarks>
    public double RaceRate { get; set; } = 0.10;

    /// <summary>Weighted dice for how each run ends. Any non-negative ints; only the ratio matters.</summary>
    public int StopWeight { get; set; } = 5;

    /// <summary>Real client-side CancelAsync. The ONLY path that produces a Canceled status.</summary>
    public int CancelWeight { get; set; } = 3;

    /// <summary>Send nothing and let <see cref="MaxDuration"/> end it.</summary>
    public int ExpireWeight { get; set; } = 2;
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
    /// <remarks>
    /// ONE roll per attempt, outside the step loop, so P(this attempt fails) IS this
    /// number. That makes P(the WORKFLOW fails) FailureRate ^ maximumAttempts —
    /// 0.15^5, roughly one in thirteen thousand — which is why the shipped 0.15
    /// produces an outcome split that is entirely `completed`. Rolling per step
    /// instead would give 1 - (1 - r)^steps, i.e. 99.99% at the shipped steps: 60,
    /// and every workflow would die terminally.
    /// </remarks>
    public double FailureRate { get; set; }

    /// <summary>Added to every step.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>
    /// Sleep past heartbeatTimeout without heartbeating, on attempt 1 only. Two
    /// things happen in order and both are the point: the server times the ATTEMPT
    /// out, and we keep running, because the only channel the server has to tell us
    /// is the response to a heartbeat RPC and we are not sending any.
    /// </summary>
    /// <remarks>
    /// It moves activity_task_timeout{timeout_type="Heartbeat"} and NOTHING ELSE.
    /// Attempt 2 is not gated, runs normally and succeeds, so the workflow outcome
    /// stays `completed`. For an outcome of timed_out you want
    /// <see cref="StopHeartbeating"/>, which starves every attempt.
    /// </remarks>
    public bool StallPastHeartbeatTimeout { get; set; }

    /// <summary>
    /// Keep doing work but never call Heartbeat(). Proves an activity that stops
    /// heartbeating can never be cancelled.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="StallPastHeartbeatTimeout"/> this is not gated to attempt 1,
    /// so all of activity.retry.maximumAttempts heartbeat-time-out, the retry policy
    /// is exhausted, and the terminal failure really is
    /// ActivityFailure -> TimeoutFailure{Heartbeat}. This is the knob that moves the
    /// outcome split to timed_out.
    /// </remarks>
    public bool StopHeartbeating { get; set; }

    /// <summary>
    /// Swallow OperationCanceledException and finish the batch anyway. Proves
    /// TemporalWorker.ExecuteAsync does not return until every executing activity
    /// returns. THIS WEDGES YOUR TERMINAL FOR THE REST OF THE BATCH. That is the demo.
    /// </summary>
    public bool IgnoreCancellation { get; set; }
}
