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
    /// says "loadgen", and an unmatched key is a hard error here, by design.
    /// </remarks>
    public LoadgenConfig Loadgen { get; set; } = new();

    /// <summary>Everything about SimpleNoActivity: the run bound plus the chaos driver.</summary>
    public SimpleConfig Simple { get; set; } = new();

    /// <summary>Everything about WorkflowSimpleActivity: one activity, no heartbeats.</summary>
    /// <remarks>
    /// SimpleActivity maps to the YAML key <c>simpleActivity</c>. Safe under
    /// CamelCaseNamingConvention, which lowers only the FIRST character. See the
    /// <see cref="Loadgen"/> remark above for the shape that is not.
    /// </remarks>
    public SimpleActivityConfig SimpleActivity { get; set; } = new();

    /// <summary>Everything about WorkflowLocalActivity: one CPU-bound LOCAL activity.</summary>
    /// <remarks>
    /// The only block that carries its OWN <see cref="LocalActivityConfig.Namespace"/> and
    /// <see cref="LocalActivityConfig.TaskQueue"/>, because this workflow runs somewhere else
    /// entirely. That is not tidiness: <c>history.workflowTaskHeartbeatTimeout</c> is a
    /// namespace-scoped dynamic config setting, so a dedicated namespace is the ONLY way to
    /// lower it for this workflow without changing it for the other three.
    /// </remarks>
    public LocalActivityConfig LocalActivity { get; set; } = new();

    /// <summary>Everything about WorkflowFileScan: the corpus, the pace, and its own queue.</summary>
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
/// LIVE, and reached through the workflow INPUT rather than read from the file.
/// HeartbeatWorkflow builds its ActivityOptions from JobInput.Activity, deliberately:
/// options carried in the input are recorded in the history, so a replay reproduces them
/// byte for byte, while a file that can be edited between the original execution and the
/// replay cannot promise that.
/// <para>
/// ActivityOptionsInput.From projects this class onto that input, and both clients call it:
/// Repro.Starter/Program.cs and Repro.LoadGen/Program.cs. So every field below really does
/// change what the next run schedules.
/// </para>
/// <para>
/// The fallback is the part to know: JobInput.Activity is optional with a null default so a
/// pre-existing history still deserializes, and a null falls back to ActivityOptionsInput's
/// positional defaults, which are exactly the values below. Change config.yaml, not those
/// defaults.
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
    /// never even CREATED. Core registers a counter on first increment, so a metric
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
    /// that exercises a rejected update, and a rejected update writes NOTHING to history,
    /// which is why validators exist.
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
/// WorkflowSimpleActivity's job shape, its activity's timeouts, and every knob of the
/// loadgen's THIRD driver loop.
/// </summary>
/// <remarks>
/// The ordinary case, and the one the other two blocks do not cover: a single activity
/// with a plain start-to-close timeout and a retry policy. Know the consequence of having
/// no heartbeat timeout before you try to cancel one. WorkflowSimpleActivity's
/// BuildActivityOptions spells it out.
/// <para>
/// Flat and single-hump, and the names are boring on purpose, for the reason
/// <see cref="SimpleConfig"/> records: CamelCaseNamingConvention lowers only the FIRST
/// character, and an unmatched YAML key is a hard error here.
/// </para>
/// <para>
/// Reaches its workflow the same way <see cref="ActivityConfig"/> does: the loadgen driver
/// calls <c>SimpleActivityInput.From</c> on it, so the values travel into the workflow input
/// and are recorded in the history. There is no asymmetry between the two blocks to draw.
/// Both are live, and both are live by the same route.
/// </para>
/// </remarks>
public sealed class SimpleActivityConfig
{
    /// <summary>Turn the third driver loop off without editing the loadgen. <c>--no-simple-activity</c> does the same.</summary>
    /// <remarks>
    /// NOT <c>--no-simple</c>, which is the SimpleNoActivity loop. Both flags exist and
    /// they are matched exactly, never by prefix.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>How long the activity sleeps BEFORE it fetches anything.</summary>
    /// <remarks>
    /// In the ACTIVITY, not a workflow timer. That is what makes this case worth having:
    /// the sleep occupies an activity slot, produces a real
    /// temporal_activity_execution_latency, and gives
    /// <see cref="StartToCloseTimeout"/> something that can actually fire.
    /// Workflow.DelayAsync would write a TimerStarted/TimerFired pair, occupy nothing, and
    /// leave an activity that returns in one HTTP round trip.
    /// <para>
    /// It also FLOORS repro_simple_activity_latency, which is why HistogramBuckets.cs has
    /// a row with boundaries just above 5000ms. Change this and that row is wrong.
    /// </para>
    /// </remarks>
    public TimeSpan SleepDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Per attempt. Must exceed <see cref="SleepDuration"/> + <see cref="HttpTimeout"/> + 2s.</summary>
    /// <remarks>
    /// With no heartbeat timeout, start-to-close is the ONLY activity timeout this
    /// workflow can produce, which is also why its Classify matches
    /// TimeoutType.StartToClose and not TimeoutType.Heartbeat. ConfigLoader.Validate
    /// enforces the headroom: less than that and every attempt dies of start-to-close
    /// against a perfectly healthy network, and the retry policy burns through
    /// <see cref="Retry"/>.MaximumAttempts proving it.
    /// </remarks>
    public TimeSpan StartToCloseTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Hard bound on the Open-Meteo call, enforced by the activity itself.</summary>
    /// <remarks>
    /// A downed interface fails fast; a BLACKHOLED route does not, and without this the
    /// run outlives demo-down.sh's drain window with nothing in the log to say why. The
    /// activity aborts its own request so you get a WARNING naming the elapsed time,
    /// rather than an opaque server-side TimeoutFailure that cannot tell you whether the
    /// sleep, DNS, TLS or the response ran long.
    /// </remarks>
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>The activity's retry policy.</summary>
    /// <remarks>
    /// MaximumAttempts is validated to be greater than zero here, which is STRICTER than
    /// activity.retry.maximumAttempts. Zero means UNLIMITED in
    /// Temporalio.Common.RetryPolicy. Write 1 for "do not retry". Unlimited retries
    /// against a THIRD-PARTY endpoint is the one place in this repo where a stuck run is
    /// also someone else's problem: a sustained Open-Meteo 5xx would hold an activity slot
    /// and keep requesting forever.
    /// </remarks>
    public RetryConfig Retry { get; set; } = new();

    /// <summary>Degrees north. Seattle by default.</summary>
    /// <remarks>
    /// Validated to [-90, 90]. Outside that Open-Meteo answers HTTP 400, which the
    /// activity throws NON-retryably, so a typo fails every run on attempt 1 instead of
    /// quietly producing a synthetic reading. A config bug is not an outage.
    /// </remarks>
    public double Latitude { get; set; } = 47.6062;

    /// <summary>Degrees east. Validated to [-180, 180], same reasoning as <see cref="Latitude"/>.</summary>
    public double Longitude { get; set; } = -122.3321;

    /// <summary>Where to fetch the weather from. Open-Meteo needs no API key and no account.</summary>
    /// <remarks>
    /// A knob rather than a constant so the synthetic-fallback path is reachable by config
    /// edit instead of by unplugging a laptop: point it at
    /// <c>http://127.0.0.1:1/forecast</c> for an instant connection-refused.
    /// <para>
    /// Reaches the activity through its CONSTRUCTOR, not the workflow input, because it is
    /// infrastructure rather than job shape. Same channel FaultConfig uses.
    /// </para>
    /// </remarks>
    public string BaseUrl { get; set; } = "https://api.open-meteo.com/v1/forecast";

    /// <summary>When true an UNREACHABLE Open-Meteo throws instead of falling back to a synthetic reading.</summary>
    /// <remarks>
    /// Shipped OFF because the demo scripts have to stay green with no egress.
    /// <para>
    /// This flag governs the UNREACHABLE case only, and it is not the only route to
    /// outcome="failed". The synthetic fallback is gated on transport failure, so with the
    /// flag off a server that ANSWERED still fails the run: a non-retryable status, a changed
    /// response schema, or a 429/5xx exhausting <see cref="Retry"/>.MaximumAttempts all reach
    /// the workflow as an activity failure and record outcome="failed" source="none".
    /// </para>
    /// <para>
    /// No initializer: CA1805 forbids a redundant <c>= false</c> and it is a build error
    /// here.
    /// </para>
    /// </remarks>
    public bool RequireLiveWeather { get; set; }

    /// <summary>Mean interval between starts, before jitter.</summary>
    /// <remarks>
    /// SLOWER than simple.rate on purpose, and not because of your laptop: this is the
    /// only loop in the repo that calls a third-party API. 15s with concurrency 4 is about
    /// 4 requests a minute, ~5,760 a day, comfortably inside Open-Meteo's free tier, so a
    /// demo left running overnight still does not get you blocked. simple.rate's 3s would
    /// be ~28,800 a day and would.
    /// </remarks>
    public TimeSpan Rate { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Fractional spread on <see cref="Rate"/>: the interval is
    /// <c>rate x [1-jitter, 1+jitter]</c>. 0 is a metronome.
    /// </summary>
    /// <remarks>
    /// Validated to be under 1, same contract as <see cref="SimpleConfig.Jitter"/> and
    /// computed by the same helper. At exactly 1 the low end of the range is zero and the
    /// driver loop becomes a busy spin, here against api.open-meteo.com, which will
    /// rate-limit you for it.
    /// </remarks>
    public double Jitter { get; set; } = 0.5;

    /// <summary>Max runs in flight. At capacity a tick is SKIPPED, never queued.</summary>
    public int Concurrency { get; set; } = 4;
}

/// <summary>
/// WorkflowLocalActivity's job shape, its local activity's timeouts, its dedicated
/// namespace, and every knob of the loadgen's FOURTH driver loop.
/// </summary>
/// <remarks>
/// The local-activity case, and the one the other three blocks cannot express. A local
/// activity runs INSIDE the workflow task rather than as a separately scheduled activity
/// task, so it writes a MarkerRecorded event instead of ActivityTaskScheduled, holds a
/// LocalActivityWorker slot rather than an activity slot, and cannot heartbeat at all.
/// <para>
/// Flat and single-hump, names boring on purpose, for the reason <see cref="SimpleConfig"/>
/// records: CamelCaseNamingConvention lowers only the FIRST character and an unmatched YAML
/// key is a hard error here.
/// </para>
/// </remarks>
public sealed class LocalActivityConfig
{
    /// <summary>Turn the fourth driver loop off without editing the loadgen. <c>--no-local-activity</c> does the same.</summary>
    /// <remarks>
    /// NOT <c>--no-simple-activity</c>, which is the WorkflowSimpleActivity loop. Three
    /// `--no-*` flags now exist and they are matched EXACTLY, never by prefix.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>The namespace this workflow runs in. Everything else uses <see cref="ReproConfig.Namespace"/>.</summary>
    /// <remarks>
    /// LOAD-BEARING, and the reason the second namespace exists at all.
    /// <c>history.workflowTaskHeartbeatTimeout</c> is declared in the server as
    /// <c>NewNamespaceDurationSetting</c>, so it can be filtered by NAMESPACE and by nothing
    /// finer -- not by task queue, not by workflow type. Dropping it from 30m to 1m in a
    /// dedicated namespace is therefore the only way to make this workflow's re-execution
    /// loop reachable in a demo while leaving the other three workflows on the stock default.
    /// <para>
    /// A namespace is a CLIENT property and a worker binds one client, so this costs a second
    /// TemporalClient and a second TemporalWorker in both Repro.Worker and Repro.LoadGen. They
    /// share the one TemporalRuntime; ReproRuntime's guard counts runtime constructions, not
    /// client bindings.
    /// </para>
    /// </remarks>
    public string Namespace { get; set; } = "repro-local-activity";

    /// <summary>This workflow's task queue, inside <see cref="Namespace"/>.</summary>
    /// <remarks>
    /// Task queues are namespace-scoped, so reusing <c>repro-task-queue</c> would be legal.
    /// It is not reused because this repo has already been bitten by ambiguous names: one
    /// spelling in two namespaces makes every `grep` and every visibility query ambiguous for
    /// a human, whatever the server thinks. <c>ConfigLoader.ValidateLocalActivity</c> rejects
    /// any pair where either name is a string prefix of the other.
    /// </remarks>
    public string TaskQueue { get; set; } = "repro-la-queue";

    /// <summary>Lower bound of the per-run duration draw.</summary>
    public TimeSpan MinDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound of the per-run duration draw.</summary>
    /// <remarks>
    /// The draw is uniform on [<see cref="MinDuration"/>, <see cref="MaxDuration"/>], so with
    /// the shipped 30s..2m against a 1m heartbeat timeout, exactly (120-60)/(120-30) = 2/3 of
    /// runs outlive the timeout and re-execute. Change either bound and that fraction moves;
    /// docs/WORKFLOWS.md quotes it.
    /// <para>
    /// THIS IS THE FIRST THING IN THE REPO TO BREAK THE 45s DRAIN DOCTRINE that
    /// <see cref="SimpleConfig.MaxDuration"/> states. demo-down.sh allows
    /// worker.gracefulShutdownTimeout + 15 = 45s before SIGKILL, and this runs for up to 2m.
    /// It survives only because the burn loop polls its worker-shutdown token; see
    /// PiActivities. Raise DEMO_DRAIN_TIMEOUT if you raise this.
    /// </para>
    /// </remarks>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Per attempt. DELIBERATELY UNREACHABLE at the shipped config.</summary>
    /// <remarks>
    /// The SDK requires StartToClose or ScheduleToClose to be set, so one of them has to
    /// exist. This one can never fire: the burn is wall-clock capped at
    /// <see cref="MaxDuration"/> and the server kills the workflow task at the heartbeat
    /// timeout well before 2m30s. Documented as unreachable rather than described as a guard,
    /// which is the standard WorkflowSimpleActivity's BuildActivityOptions sets for a rung
    /// that cannot fire.
    /// </remarks>
    public TimeSpan StartToCloseTimeout { get; set; } = TimeSpan.FromSeconds(150);

    /// <summary>Total across attempts -- but NOT across workflow-task re-executions.</summary>
    /// <remarks>
    /// Shipped ABOVE the 1m heartbeat timeout, which means it never fires, which is the
    /// repro. Its clock restarts on every re-dispatch: sdk-core re-stamps
    /// <c>original_schedule_time</c> on each fresh schedule and only persists it inside a
    /// marker, and a local activity killed by a workflow task timeout never wrote one.
    /// LocalActivityOptionsInput's remarks carry the full chain.
    /// <para>
    /// SET IT BELOW THE HEARTBEAT TIMEOUT to switch this case from the failure to the
    /// documented FIX: the local activity then fails with a timeout the workflow can catch,
    /// and the workflow task is never re-executed. That is the one regime in which this field
    /// does anything, and ConfigLoader deliberately does not forbid it.
    /// </para>
    /// </remarks>
    public TimeSpan ScheduleToCloseTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Server-enforced bound on the whole run. THE ONLY RUNG THAT ACTUALLY STOPS THE LOOP.</summary>
    /// <remarks>
    /// Passed as <c>WorkflowOptions.RunTimeout</c> by the driver, not carried in the workflow
    /// input, because it is enforced by the SERVER's timer queue rather than by anything the
    /// workflow does.
    /// <para>
    /// KNOW WHAT IT COSTS YOU. The server closes a run-timed-out workflow by calling
    /// TimeoutWorkflow directly, WITHOUT scheduling a workflow task. So workflow code never
    /// runs again and cannot record an outcome: repro_local_activity_completed does not
    /// increment at all for these runs, not even as timed_out. That is why this workflow's
    /// outcome vocabulary has three values rather than four, and why repro_pi_attempt_started
    /// -- emitted from ACTIVITY code, which does not replay -- is the primary signal for this
    /// case rather than a supporting one.
    /// </para>
    /// </remarks>
    public TimeSpan RunTimeout { get; set; } = TimeSpan.FromMinutes(6);

    /// <summary>The local activity's retry policy.</summary>
    /// <remarks>
    /// MaximumAttempts is validated to be greater than zero, and the reason is STRONGER here
    /// than it is for simpleActivity. An unset RetryPolicy on a LOCAL activity means retry
    /// FOREVER -- "Gets or sets the retry policy. If unset, defaults to retrying forever" --
    /// and 0 means unlimited in Temporalio.Common.RetryPolicy, so both routes to "no policy"
    /// end in an unbounded chain of two-minute CPU burns. Write 1 for "do not retry".
    /// <para>
    /// It does NOT bound the re-execution loop. A workflow-task-timeout re-execution arrives
    /// as attempt 1 again, outside the retry policy entirely.
    /// </para>
    /// </remarks>
    public RetryConfig Retry { get; set; } = new() { MaximumAttempts = 1 };

    /// <summary>Mean interval between starts, before jitter.</summary>
    public TimeSpan Rate { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Fractional spread on <see cref="Rate"/>: the interval is
    /// <c>rate x [1-jitter, 1+jitter]</c>. 0 is a metronome.
    /// </summary>
    /// <remarks>Same contract and same helper as the other two jittered loops.</remarks>
    public double Jitter { get; set; } = 0.5;

    /// <summary>Max runs in flight. At capacity a tick is SKIPPED, never queued.</summary>
    /// <remarks>
    /// 3, and the arithmetic is worth keeping because a sparse panel here looks exactly like
    /// a broken target. Expected slot occupancy is (1/3)(~45s) + (2/3)(runTimeout) which is
    /// about 255s per run at the shipped values, so one slot is busy essentially always. At
    /// concurrency 1 that is roughly 14 runs an hour with ~88% of ticks skipped, which is one
    /// or two samples inside a 30-minute dashboard window. Three slots make the board legible
    /// without pegging a laptop.
    /// </remarks>
    public int Concurrency { get; set; } = 3;

    /// <summary>Worker-side cap on concurrent LOCAL activities. Its own slot type.</summary>
    /// <remarks>
    /// The SDK default is 100, and leaving it there is a real hazard for THIS activity rather
    /// than a theoretical one: 100 concurrent CPU burns saturate the thread pool, and workflow
    /// activations run on that same pool. The SDK's deadlock detector fails a workflow task
    /// that does not yield within 2 seconds, so a starved pool produces evicted runs and
    /// retried workflow tasks that look exactly like the heartbeat-timeout repro and are not
    /// it. A board that cannot tell you which failure you are looking at is worse than no
    /// board.
    /// <para>
    /// Local activities have their OWN slot type, so this does not come out of
    /// <see cref="WorkerConfig.MaxConcurrentActivities"/>: Core reports them as
    /// <c>worker_type="LocalActivityWorker"</c> on temporal_worker_task_slots_available.
    /// </para>
    /// </remarks>
    public int MaxConcurrentLocalActivities { get; set; } = 4;
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
    /// number. That makes P(the WORKFLOW fails) FailureRate ^ maximumAttempts, or
    /// 0.15^5, roughly one in thirteen thousand, which is why the shipped 0.15
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
    /// <summary>
    /// Decode every scanned row to a string and throw it away. Proves ALLOCATION IS
    /// NOT GROWTH: about 140 bytes of Gen0 garbage per row, so allocated lands near
    /// 2.4x bytes read, the gen0 rate climbs, and the live heap floor stays flat.
    /// </summary>
    /// <remarks>
    /// The default scan path reads raw bytes and allocates essentially nothing, which
    /// is what makes this knob legible: the baseline is provably zero rather than a
    /// floor you have to read the step change against.
    /// </remarks>
    public bool DecodeRowsToStrings { get; set; }

    /// <summary>
    /// Same decode, but every string is retained in a List for the life of the attempt.
    /// Proves RETENTION grows the heap: the same garbage becomes promoted live Gen2, the
    /// managed-heap gauge becomes a staircase with no falling edge, and rows/s decays as
    /// GC time rises.
    /// </summary>
    /// <remarks>
    /// The list is PRE-SIZED from the corpus header. An un-sized List grown to 8.6M
    /// elements doubles into a 128 MiB backing array while the previous 64 MiB one is
    /// still garbage, which moves the LOH panel for a second, unrelated reason and
    /// destroys the attribution this knob exists for.
    /// <para>
    /// ConfigLoader REFUSES this together with fileScan.concurrency > 1: eight
    /// concurrent retained scans of the 500 MB corpus is about 10 GB, and the failure
    /// is an OOM-killed worker rather than an empty panel.
    /// </para>
    /// </remarks>
    public bool RetainScannedRows { get; set; }

    /// <summary>
    /// File.ReadAllBytes the whole corpus before scanning. Proves a large read is ONE
    /// LOH OBJECT, and the LOH is not compacted by default, so committed bytes and RSS
    /// step up in a single sample and do not come back when the array dies.
    /// </summary>
    /// <remarks>
    /// It does NOT produce a heartbeat timeout: 500 MB off page cache or NVMe is well
    /// under a second. For that you want <see cref="StallPastHeartbeatTimeout"/>.
    /// ReadAllBytes is also SYNCHRONOUS, so it holds an activity-task thread for its
    /// whole duration, which is what moves the thread-pool panel.
    /// </remarks>
    public bool SlurpWholeFile { get; set; }
}

/// <summary>
/// WorkflowFileScan: a long, resumable scan of one generated corpus in sample_files/.
/// </summary>
/// <remarks>
/// Single-humped property names throughout. CamelCaseNamingConvention lowers only the
/// FIRST character, so a name like MaxRowCount would map to the YAML key maxRowCount
/// while a reader would write max_row_count or maxRowcount, and an unmatched key is a
/// hard error here by design.
/// <para>
/// NOTE this is WORKSTATION GC: ServerGarbageCollection is unset in Directory.Build.props.
/// DOTNET_gcServer=1 raises the gen0 budget dramatically and invalidates every magnitude
/// quoted in docs/WORKFLOWS.md for this case; DOTNET_GCgen0size is how to pin the budget
/// for a reproducible gen0 count.
/// </para>
/// </remarks>
public sealed class FileScanConfig
{
    /// <remarks>Paired with --no-file-scan, which turns the loadgen loop off without editing this file.</remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>The corpus to scan. Resolved to an absolute path against the CONFIG FILE's directory.</summary>
    /// <remarks>
    /// Not against the working directory, and the difference is a silent wrong answer.
    /// docs/HEARTBEATING.md's kill-the-worker recipe runs the built binary from the repo
    /// root while demo-up.sh runs from elsewhere; a cwd-relative path would mean two
    /// different files across a resume, and the checkpoint's corpus-identity check is the
    /// only thing that would notice.
    /// <para>
    /// sample_files/ is GITIGNORED and generated, so on a fresh clone this file is absent.
    /// ConfigLoader validates the SHAPE of this value and never stats it -- ConfigTests
    /// loads the committed config.yaml, so a stat would break dotnet test on every fresh
    /// clone. The loadgen driver checks existence once and skips its loop with a named
    /// banner; the activity throws NON-RETRYABLE if invoked with the corpus missing.
    /// </para>
    /// </remarks>
    public string Path { get; set; } = "sample_files/sample-100mb.txt";

    /// <summary>Its OWN task queue, in the SAME namespace as everything else.</summary>
    /// <remarks>
    /// Not for isolation of the interesting kind -- GC, the thread pool and RSS are
    /// process-wide and stay shared, which is the point. It exists because
    /// temporal_worker_task_slots_used carries NO activity_type label, and the heartbeat
    /// board's headline stat sums it unfiltered while its description claims this repo has
    /// exactly one heartbeating activity type. A second heartbeating activity on
    /// repro-task-queue would corrupt that panel with no way to filter it back out. A
    /// separate queue lets the panel pin task_queue and excludes the scan exactly.
    /// <para>
    /// Same namespace means no second CLIENT, unlike localActivity -- only a second
    /// TemporalWorker. ConfigLoader requires this to be prefix-DISJOINT from the other two
    /// queues, not merely different.
    /// </para>
    /// </remarks>
    public string TaskQueue { get; set; } = "repro-scan-queue";

    /// <summary>Rows per second. 0 means unthrottled: read at the machine's ceiling.</summary>
    /// <remarks>
    /// THE KNOB THAT MAKES THIS A LONG-RUNNING ACTIVITY. An unthrottled raw-byte scan of
    /// the 500 MB corpus finishes in single-digit seconds, which is shorter than one
    /// heartbeat throttle interval: the case would emit one heartbeat and demonstrate
    /// nothing about resume or about pressure.
    /// <para>
    /// The shipped 6000 puts the 100 MB corpus at 4m47s, and at heartbeatTimeout 30s the
    /// throttle is 24s, so a kill -9 redoes 24 x 6000 = 144,000 rows -- 8.35% of the
    /// corpus, an unmissable drop on the cursor panel. At the seed case's 5s timeout the
    /// throttle is 4s and the same drop is 1.4%: visible on a panel, invisible in a demo.
    /// </para>
    /// <para>
    /// Pacing is also what makes GC pressure legible. Unthrottled, rows/s is a noisy
    /// function of the page cache; pinned below the ceiling the line is flat and any dip
    /// is unambiguously pressure.
    /// </para>
    /// </remarks>
    public long TargetRowsPerSecond { get; set; } = 6000;

    /// <summary>Rows between one pace / cancel / drain / heartbeat / log check and the next.</summary>
    /// <remarks>
    /// Batched, never per row: Task.Delay has a floor near 1ms and cannot express the
    /// 167us per row the shipped rate implies, so a per-row sleep would run at about
    /// 1000 rows/s regardless of configuration and TargetRowsPerSecond would be a lie.
    /// Same reasoning as PiActivities.CheckEvery.
    /// <para>
    /// 600 rows at 6000 rows/s is a 100ms batch period, which is also the loop's reaction
    /// time to a drain or a cancel. ConfigLoader caps the batch period at 2s for exactly
    /// that reason: batchRows 1000000 would be a 167-second batch, and the activity could
    /// then observe neither a drain nor a cancel nor emit a heartbeat inside any window.
    /// </para>
    /// </remarks>
    public int BatchRows { get; set; } = 600;

    /// <summary>Read buffer, bytes. One byte[] -- there is no second buffer.</summary>
    /// <remarks>
    /// The scan reads raw bytes and finds line breaks itself, so unlike a StreamReader
    /// path there is no char[] alongside this one. A byte[n] reaches the 85,000-byte LOH
    /// threshold at n >= 84,976, so the shipped 65536 is SOH and the LOH gauge sits at a
    /// true zero. Raising this past ~83 KiB moves the buffer to the LOH and is the
    /// cheapest possible one-line demonstration of that threshold.
    /// </remarks>
    public int BufferBytes { get; set; } = 65_536;

    /// <summary>Stop after this many rows. 0 means the whole file.</summary>
    /// <remarks>
    /// A checkpoint written under a LARGER maxRows is a different job and the activity
    /// refuses to resume from it, rather than silently answering a question nobody asked.
    /// </remarks>
    public long MaxRows { get; set; }

    /// <summary>How often the activity prints a progress line and samples the pressure gauges.</summary>
    /// <remarks>
    /// WALL CLOCK, not a row count, and one interval feeds both sinks. A row-count cadence
    /// goes sparse exactly when the system slows down, which is when you need it; and two
    /// independent samples would make the console and Grafana disagree by one tick, sending
    /// a reader after a discrepancy that does not exist.
    /// </remarks>
    public TimeSpan LogInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Chosen for the STALENESS it produces, not for liveness.</summary>
    /// <remarks>
    /// The maximum gap between two Heartbeat() calls is one batch period, 100ms, so 30s is
    /// 300x margin. What it actually sets is the throttle, min(0.8 x this,
    /// worker.maxHeartbeatThrottleInterval) = 24s, and therefore how much work a kill -9
    /// destroys the record of.
    /// <para>
    /// CEILING: worker.maxHeartbeatThrottleInterval is 60s, so raising this past 75s stops
    /// increasing the throttle and the redone work plateaus at 60s x rate. The knob for
    /// "make the lesson louder" saturates.
    /// </para>
    /// </remarks>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Bounds ONE attempt, which for attempt 1 is the whole file.</summary>
    /// <remarks>
    /// Covers the shipped corpora rather than guarding anything: the 500 MB corpus at 6000
    /// rows/s is 23m57s. It becomes a live guard the moment you drop the rate or point at
    /// something bigger, and then ValidateFileScan fails at startup naming the value it needs.
    /// Its honest role is catching an attempt that keeps heartbeating and never finishes.
    /// </remarks>
    public TimeSpan StartToCloseTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Total across every attempt, including the cost of every resume.</summary>
    /// <remarks>
    /// "attempts x startToClose" is the WRONG model and gives an absurd number. Useful work
    /// is one worst-case scan regardless of how many attempts it takes; each RESUME adds
    /// heartbeatTimeout (the server noticing) + retry.maximumInterval (backoff) + throttle
    /// (the reading that is redone) = 64s at the shipped values. Nine resumes on the 500 MB
    /// corpus is 23m57s + 9 x 64s + 2m = 35m33s, so 1h leaves about 1.7x headroom.
    /// </remarks>
    public TimeSpan ScheduleToCloseTimeout { get; set; } = TimeSpan.FromHours(1);

    /// <remarks>
    /// maximumAttempts 10, not the usual 5, and NOT 0 -- zero means UNLIMITED in
    /// Temporalio.Common.RetryPolicy, and an unbounded chain of half-hour scans holds an
    /// activity slot forever. Each kill -9 consumes one attempt and the HEARTBEATING.md
    /// recipe does three cycles, so 5 leaves little room: one careless extra kill fails the
    /// workflow terminally, which READS AS "resume is broken" and is the worst way for this
    /// case to fail.
    /// </remarks>
    public RetryConfig Retry { get; set; } = new() { MaximumAttempts = 10 };

    /// <summary>One scan started every rate, plus or minus jitter.</summary>
    public TimeSpan Rate { get; set; } = TimeSpan.FromMinutes(6);

    /// <summary>Fraction of Rate to jitter by, 0 to 1.</summary>
    public double Jitter { get; set; } = 0.2;

    /// <summary>In-flight scans the loadgen will allow. Over capacity it SKIPS, never queues.</summary>
    /// <remarks>
    /// A pure multiplier on every byte, allocation and buffer in the case, sharing ONE heap
    /// and ONE thread pool. See fault.retainScannedRows, which ConfigLoader refuses above 1.
    /// </remarks>
    public int Concurrency { get; set; } = 1;
}
