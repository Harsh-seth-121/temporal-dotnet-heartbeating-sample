using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Repro.Core.Config;

/// <summary>Loads config.yaml over the POCO defaults, then applies env-var overrides.</summary>
public static class ConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        // Lowers the first character only: TaskQueue -> taskQueue. .Instance, not `new`: the public
        // constructors are [Obsolete] and this repo builds with TreatWarningsAsErrors.
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new GoDurationYamlConverter())
        // No .IgnoreUnmatchedProperties(): a misspelled key stops the process instead of defaulting.
        .Build();

    /// <summary>Resolve the config path: explicit flag, else env, else search upward for config.yaml.</summary>
    /// <remarks>The upward search makes `dotnet run --project src/Repro.Worker` work from the repo root with no flag.</remarks>
    public static string Resolve(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return explicitPath;
        }

        if (Environment.GetEnvironmentVariable("REPRO_CONFIG") is { Length: > 0 } fromEnv)
        {
            return fromEnv;
        }

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "config.yaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return "config.yaml";
    }

    public static ReproConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"config file not found: {path}. Pass --config <path>, or run from the repo root.", path);
        }

        var config = Deserializer.Deserialize<ReproConfig>(File.ReadAllText(path)) ?? new ReproConfig();
        ApplyEnvironmentOverrides(config);
        Validate(config, path);
        return config;
    }

    /// <summary>Env wins over the file, matching Temporal's own precedence.</summary>
    private static void ApplyEnvironmentOverrides(ReproConfig config)
    {
        if (Environment.GetEnvironmentVariable("TEMPORAL_ADDRESS") is { Length: > 0 } address)
        {
            config.Address = address;
        }

        if (Environment.GetEnvironmentVariable("TEMPORAL_NAMESPACE") is { Length: > 0 } ns)
        {
            config.Namespace = ns;
        }

        if (Environment.GetEnvironmentVariable("TEMPORAL_API_KEY") is { Length: > 0 } apiKey)
        {
            config.ApiKey = apiKey;
        }

        if (Environment.GetEnvironmentVariable("TEMPORAL_TLS_CLIENT_CERT_PATH") is { Length: > 0 } cert)
        {
            config.Tls.CertPath = cert;
        }

        if (Environment.GetEnvironmentVariable("TEMPORAL_TLS_CLIENT_KEY_PATH") is { Length: > 0 } key)
        {
            config.Tls.KeyPath = key;
        }
    }

    /// <summary>Fail at startup rather than in native code or on a blank dashboard.</summary>
    /// <param name="config">Deserialized config, mutated here by the normalizing calls below.</param>
    /// <param name="configPath">The path <see cref="Load"/> read, and the only directory a relative path may resolve against.</param>
    private static void Validate(ReproConfig config, string configPath)
    {
        config.Metrics.ListenAddress =
            BindAddress.Normalize(config.Metrics.ListenAddress, "metrics.listenAddress");
        config.Metrics.LoadgenAddress =
            BindAddress.Normalize(config.Metrics.LoadgenAddress, "metrics.loadgenAddress");

        if (config.Metrics.ListenAddress == config.Metrics.LoadgenAddress)
        {
            throw new ArgumentException(
                "metrics.listenAddress and metrics.loadgenAddress are the same. The worker and loadgen " +
                "each need their own exporter port; prometheus.yml scrapes 8077 and 8078 as separate jobs.");
        }

        if (!config.Metrics.PushgatewayUrl.TrimEnd('/').EndsWith("/metrics", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"metrics.pushgatewayUrl must end in /metrics (got \"{config.Metrics.PushgatewayUrl}\"). " +
                "prometheus-net's MetricPusher appends /job/<job>/instance/<instance> verbatim, so " +
                "without the path every push 404s and is reported only through OnError.");
        }

        if (config.Activity.HeartbeatTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "activity.heartbeatTimeout must be > 0. With no heartbeat timeout the activity can never " +
                "be cancelled: the server only communicates cancellation in the response to a heartbeat RPC.");
        }

        if (config.Activity.StartToCloseTimeout <= config.Activity.HeartbeatTimeout)
        {
            throw new ArgumentException(
                "activity.startToCloseTimeout must exceed activity.heartbeatTimeout, otherwise the attempt " +
                "always dies of start-to-close first and no heartbeat timeout is ever observed.");
        }

        if (config.Fault.FailureRate is < 0 or > 1)
        {
            throw new ArgumentException($"fault.failureRate must be between 0 and 1 (got {config.Fault.FailureRate}).");
        }

        if (config.Job.Steps <= 0)
        {
            throw new ArgumentException($"job.steps must be > 0 (got {config.Job.Steps}).");
        }

        if (config.Simple.MaxDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"simple.maxDuration must be > 0 (got {config.Simple.MaxDuration}). It is the " +
                "WaitConditionAsync timeout, so at zero every run ends `expired` instantly.");
        }

        if (config.Simple.Rate <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"simple.rate must be > 0 (got {config.Simple.Rate}). At zero the driver loop " +
                "is a busy spin against the frontend.");
        }

        ValidateJitter(config.Simple.Jitter, "simple");

        ValidateConcurrency(config.Simple.Concurrency, "simple");

        if (config.Simple.MinMessages < 0 || config.Simple.MaxMessages < config.Simple.MinMessages)
        {
            throw new ArgumentException(
                "simple.minMessages must be >= 0 and simple.maxMessages >= simple.minMessages " +
                $"(got {config.Simple.MinMessages}..{config.Simple.MaxMessages}). The driver " +
                "calls Random.Shared.Next(min, max + 1), which throws when max < min.");
        }

        if (config.Simple.MessageGap < TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"simple.messageGap must be >= 0 (got {config.Simple.MessageGap}). The driver " +
                "calls Random.Shared.Next(gapMs + 1) to pick each gap, which throws on a negative " +
                "bound inside a fire-and-forget run body, so the only symptom is the failure " +
                "counter climbing.");
        }

        if (config.Simple.OverflowRate is < 0 or > 1)
        {
            throw new ArgumentException(
                $"simple.overflowRate must be between 0 and 1 (got {config.Simple.OverflowRate}).");
        }

        if (config.Simple.RaceRate is < 0 or > 1)
        {
            throw new ArgumentException(
                $"simple.raceRate must be between 0 and 1 (got {config.Simple.RaceRate}).");
        }

        if (config.Simple.StopWeight < 0 || config.Simple.CancelWeight < 0
            || config.Simple.ExpireWeight < 0
            || config.Simple.StopWeight + config.Simple.CancelWeight
                + config.Simple.ExpireWeight <= 0)
        {
            throw new ArgumentException(
                "simple.stopWeight, cancelWeight and expireWeight must be >= 0 with a positive " +
                "sum. All-zero divides by zero in the driver's ending picker.");
        }

        ValidateSimpleActivity(config);
        ValidateLocalActivity(config);
        ValidateFileScan(config, configPath);
    }

    /// <summary>The <c>simpleActivity:</c> block. Split out to keep Validate readable.</summary>
    /// <remarks>
    /// One rule is stricter than its <c>activity:</c> counterpart: retry.maximumAttempts may not be 0,
    /// because 0 means unlimited and this activity talks to a third party.
    /// </remarks>
    private static void ValidateSimpleActivity(ReproConfig config)
    {
        var sa = config.SimpleActivity;

        if (sa.SleepDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"simpleActivity.sleepDuration must be > 0 (got {sa.SleepDuration}). A negative " +
                "value throws inside the activity, buried under the retry chain and an " +
                "ActivityFailure. Zero contradicts repro_simple_activity_latency's 5000ms boundaries.");
        }

        if (sa.HttpTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"simpleActivity.httpTimeout must be > 0 (got {sa.HttpTimeout}). A blackholed route " +
                "never fails fast, so without this the request runs until start-to-close kills the " +
                "attempt and the retry chain outlives demo-down.sh's drain window.");
        }

        // The activity derives its deadline as min(httpTimeout, startToClose - sleep - 2s); this rule
        // keeps that subtraction positive.
        var floor = sa.SleepDuration + sa.HttpTimeout + TimeSpan.FromSeconds(2);
        if (sa.StartToCloseTimeout < floor)
        {
            throw new ArgumentException(
                $"simpleActivity.startToCloseTimeout must be >= sleepDuration + httpTimeout + 2s " +
                $"= {floor} (got {sa.StartToCloseTimeout}). The activity sleeps and then makes one " +
                "HTTP round trip in the same attempt, so with less headroom every attempt dies of " +
                "start-to-close against a healthy network. With no heartbeat timeout it is the only " +
                "activity timeout this workflow can produce.");
        }

        if (sa.Retry.InitialInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"simpleActivity.retry.initialInterval must be > 0 (got {sa.Retry.InitialInterval}). " +
                "An invalid retry policy is rejected when the ScheduleActivityTask command is " +
                "validated, which fails the workflow task rather than the workflow, so the run sits " +
                "in RUNNING and never schedules its activity.");
        }

        if (sa.Retry.BackoffCoefficient < 1)
        {
            throw new ArgumentException(
                $"simpleActivity.retry.backoffCoefficient must be >= 1 (got {sa.Retry.BackoffCoefficient}). " +
                "Below 1 the interval shrinks on every retry, a retry storm against the frontend " +
                "and api.open-meteo.com at once.");
        }

        if (sa.Retry.MaximumInterval < sa.Retry.InitialInterval)
        {
            throw new ArgumentException(
                $"simpleActivity.retry.maximumInterval ({sa.Retry.MaximumInterval}) must be >= " +
                $"initialInterval ({sa.Retry.InitialInterval}). The maximum clamps the computed " +
                "interval, so a maximum below the initial makes backoffCoefficient do nothing and " +
                "fires every retry at maximumInterval.");
        }

        if (sa.Retry.MaximumAttempts <= 0)
        {
            throw new ArgumentException(
                $"simpleActivity.retry.maximumAttempts must be > 0 (got {sa.Retry.MaximumAttempts}). " +
                "0 means unlimited in Temporalio.Common.RetryPolicy, not \"do not retry\". Write 1 " +
                "for that. Unlimited retries against a third-party endpoint park the loadgen past " +
                "demo-down.sh's drain budget; activity.retry.maximumAttempts may be 0 because that " +
                "activity only talks to itself.");
        }

        if (sa.Latitude is < -90 or > 90)
        {
            throw new ArgumentException(
                $"simpleActivity.latitude must be in [-90, 90] (got {sa.Latitude}). Open-Meteo answers " +
                "HTTP 400 outside that, which the activity throws non-retryably, so a typo fails every " +
                "run on attempt 1 instead of quietly producing a synthetic reading.");
        }

        if (sa.Longitude is < -180 or > 180)
        {
            throw new ArgumentException(
                $"simpleActivity.longitude must be in [-180, 180] (got {sa.Longitude}). Same reason as " +
                "latitude: out of range is an HTTP 400 the activity refuses to retry.");
        }

        if (!Uri.TryCreate(sa.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"simpleActivity.baseUrl must be an absolute http or https URL (got \"{sa.BaseUrl}\"). " +
                "An unusable URL fails inside a fire-and-forget run body, so the only symptom is the " +
                "driver's failure counter climbing. Point it at http://127.0.0.1:1/forecast to " +
                "exercise the synthetic fallback deliberately.");
        }

        if (sa.Rate <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"simpleActivity.rate must be > 0 (got {sa.Rate}). At zero the driver loop spins " +
                "against api.open-meteo.com, which rate-limits with a 429, and a 429 is retryable, " +
                "so every attempt of every run is spent on it.");
        }

        ValidateJitter(sa.Jitter, "simpleActivity");

        ValidateConcurrency(sa.Concurrency, "simpleActivity");
    }

    /// <summary>The <c>localActivity:</c> block. Split out to keep Validate readable.</summary>
    /// <remarks>
    /// Two rules have no counterpart elsewhere. The namespace must differ from
    /// <see cref="ReproConfig.Namespace"/>, because sharing one applies this case's 1m
    /// <c>history.workflowTaskHeartbeatTimeout</c> override to the other workflows. And
    /// <c>scheduleToCloseTimeout</c> is deliberately not ordered against <c>startToCloseTimeout</c>:
    /// setting it below the heartbeat timeout is the documented fix.
    /// </remarks>
    private static void ValidateLocalActivity(ReproConfig config)
    {
        var la = config.LocalActivity;

        if (string.IsNullOrWhiteSpace(la.Namespace))
        {
            throw new ArgumentException(
                "localActivity.namespace must not be empty. It is the namespace this workflow's " +
                "client binds to; empty is not a fallback to `default`.");
        }

        if (string.Equals(la.Namespace, config.Namespace, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"localActivity.namespace must differ from namespace (both are \"{la.Namespace}\"). " +
                "history.workflowTaskHeartbeatTimeout is namespace-scoped, so sharing one applies " +
                "this case's 1m override to the other three workflows, which have no local " +
                "activities.");
        }

        if (string.IsNullOrWhiteSpace(la.TaskQueue))
        {
            throw new ArgumentException("localActivity.taskQueue must not be empty.");
        }

        // Prefix-disjoint, not merely unequal: task queues are namespace-scoped, so the server accepts
        // the same name in both and the cost falls on every lookup that matches on queue name alone.
        if (la.TaskQueue.StartsWith(config.TaskQueue, StringComparison.Ordinal) ||
            config.TaskQueue.StartsWith(la.TaskQueue, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"localActivity.taskQueue (\"{la.TaskQueue}\") and taskQueue " +
                $"(\"{config.TaskQueue}\") must not be a prefix of one another, in either " +
                "direction. They name queues in different namespaces, so the server permits it and " +
                "nothing fails at startup; what breaks is every human-facing lookup that matches on " +
                "queue name without a namespace.");
        }

        if (la.MinDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"localActivity.minDuration must be > 0 (got {la.MinDuration}). At zero the burn " +
                "loop exits before its first clock check and every run estimates Pi from no samples.");
        }

        if (la.MaxDuration < la.MinDuration)
        {
            throw new ArgumentException(
                $"localActivity.maxDuration ({la.MaxDuration}) must be >= minDuration " +
                $"({la.MinDuration}). The driver draws uniformly on the closed interval, which " +
                "throws on an inverted one.");
        }

        // The burn is wall-clock capped at maxDuration, so start-to-close must clear it. It stays
        // unreachable; this only stops a config where it would have been the binding rung.
        var floor = la.MaxDuration + TimeSpan.FromSeconds(30);
        if (la.StartToCloseTimeout < floor)
        {
            throw new ArgumentException(
                $"localActivity.startToCloseTimeout must be >= maxDuration + 30s = {floor} (got " +
                $"{la.StartToCloseTimeout}). Below that the longest draws die of start-to-close on " +
                "a healthy worker, a different failure from the one this case demonstrates and " +
                "indistinguishable from it on the board.");
        }

        if (la.ScheduleToCloseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"localActivity.scheduleToCloseTimeout must be > 0 (got {la.ScheduleToCloseTimeout}). " +
                "It is deliberately not ordered against startToCloseTimeout: setting it below the " +
                "workflow task heartbeat timeout is the documented fix for this failure mode.");
        }

        if (la.RunTimeout <= la.MaxDuration)
        {
            throw new ArgumentException(
                $"localActivity.runTimeout ({la.RunTimeout}) must be > maxDuration " +
                $"({la.MaxDuration}). It is the only bound that ends a run whose local activity " +
                "keeps being re-executed, so at or below the burn length it kills every run, " +
                "including the ones that would have completed.");
        }

        if (la.Retry.MaximumAttempts <= 0)
        {
            throw new ArgumentException(
                $"localActivity.retry.maximumAttempts must be > 0 (got {la.Retry.MaximumAttempts}). " +
                "0 means unlimited in Temporalio.Common.RetryPolicy, and an unset RetryPolicy on a " +
                "local activity also means retry forever, so either route gives an unbounded chain " +
                "of multi-minute CPU burns. Write 1 for \"do not retry\".");
        }

        if (la.Rate <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"localActivity.rate must be > 0 (got {la.Rate}). At zero the driver loop is a busy " +
                "spin that starts a CPU-bound workflow every iteration.");
        }

        ValidateJitter(la.Jitter, "localActivity");

        ValidateConcurrency(la.Concurrency, "localActivity");

        if (la.MaxConcurrentLocalActivities <= 0)
        {
            throw new ArgumentException(
                "localActivity.maxConcurrentLocalActivities must be > 0 (got " +
                $"{la.MaxConcurrentLocalActivities}). It is applied verbatim to " +
                "TemporalWorkerOptions, which rejects a non-positive value, and 0 is not a " +
                "\"leave the SDK default\" sentinel here, unlike worker.maxConcurrentActivities.");
        }
    }

    /// <summary>
    /// Rows in <c>sample-500mb.txt</c>, the largest corpus scripts/gen-samples produces. A constant, never
    /// <c>new FileInfo(fileScan.path).Length</c>: <see cref="ValidateFileScan"/> may not touch the
    /// filesystem. Kept in step with scripts/gen-samples/MANIFEST.txt by hand.
    /// </summary>
    private const long LargestShippedCorpusRows = 8_622_570;

    /// <summary>
    /// Bytes of live heap one retained row costs. A ~58-byte row decoded to UTF-16 is 22 bytes of header
    /// and length plus 2 bytes per char, rounded up, plus the 8-byte List slot. Order-of-magnitude.
    /// </summary>
    private const int RetainedBytesPerRow = 150;

    /// <summary>
    /// Bounds on <c>fileScan.bufferBytes</c>. The range deliberately spans the 85,000-byte LOH threshold
    /// (a <c>byte[]</c> reaches it at 84,976), so crossing it is a one-line demonstration.
    /// </summary>
    private const int MinBufferBytes = 4096;

    /// <inheritdoc cref="MinBufferBytes"/>
    private const int MaxBufferBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Resumes the schedule-to-close ladder covers. Fixed, not derived from <c>retry.maximumAttempts</c>:
    /// deriving it would let lowering that field lower the floor with it, so the check could never fail.
    /// </summary>
    private const int LadderResumes = 9;

    /// <summary>Floor on the batch period: below one platform tick the configured rate is a lie.</summary>
    private static readonly TimeSpan MinBatchPeriod = TimeSpan.FromMilliseconds(10);

    /// <summary>Cap on the batch period: the loop's reaction time to a cancel, a drain or a heartbeat.</summary>
    private static readonly TimeSpan MaxBatchPeriod = TimeSpan.FromSeconds(2);

    /// <summary>Absolute floor on <c>fileScan.heartbeatTimeout</c>, independent of the batch period.</summary>
    private static readonly TimeSpan MinHeartbeatTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Slack both timeout rungs carry over their derived worst case: scheduling, payload conversion, the header read, and variance.</summary>
    private static readonly TimeSpan LadderHeadroom = TimeSpan.FromMinutes(2);

    /// <summary>The <c>fileScan:</c> block and its whole timeout ladder. Split out to keep Validate readable.</summary>
    /// <remarks>
    /// It never stats the corpus: <c>sample_files/</c> is gitignored and ConfigTests calls
    /// <see cref="Load"/> against the committed config.yaml, so a <c>File.Exists</c> here would break
    /// <c>dotnet test</c> on a fresh clone. Existence is checked in FileScanDriver's constructor and in
    /// the activity instead. It also mutates the config, resolving <c>path</c> against the config file's
    /// directory rather than the cwd, as <c>BindAddress.Normalize</c> does at the top of Validate.
    /// </remarks>
    private static void ValidateFileScan(ReproConfig config, string configPath)
    {
        var fs = config.FileScan;

        // 1. path: non-empty, then resolved to absolute against the config file's directory.
        if (string.IsNullOrWhiteSpace(fs.Path))
        {
            throw new ArgumentException(
                $"fileScan.path must not be empty (got \"{fs.Path}\"). It names the corpus to scan, " +
                "generated into a gitignored directory by scripts/gen-samples/gen-samples.sh. " +
                "Nothing here stats the filesystem, so an empty path reaches the activity and " +
                "throws non-retryably on attempt 1, buried under an ActivityFailure chain.");
        }

        // Path.Combine returns an already-absolute second argument unchanged, so an absolute path here
        // survives verbatim; GetFullPath then normalizes ".." and ".".
        var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath));
        fs.Path = Path.GetFullPath(Path.Combine(
            string.IsNullOrEmpty(configDir) ? Directory.GetCurrentDirectory() : configDir,
            fs.Path));

        // 2. The plain scalar bounds.
        if (fs.TargetRowsPerSecond < 0)
        {
            throw new ArgumentException(
                $"fileScan.targetRowsPerSecond must be >= 0 (got {fs.TargetRowsPerSecond}). 0 is " +
                "the documented sentinel for unthrottled. A negative rate makes the pacer's " +
                "absolute due time run backwards, so the scan runs flat out while every rows/s " +
                "panel and the console line report the configured rate.");
        }

        if (fs.BatchRows <= 0)
        {
            throw new ArgumentException(
                $"fileScan.batchRows must be > 0 (got {fs.BatchRows}). It is the number of rows " +
                "between one pace, cancel, drain, heartbeat and log check and the next, so at zero " +
                "the cursor never advances while the activity keeps heartbeating an unchanged " +
                "checkpoint, which on the board looks like a stalled disk.");
        }

        if (fs.MaxRows < 0)
        {
            throw new ArgumentException(
                $"fileScan.maxRows must be >= 0 (got {fs.MaxRows}). 0 is the documented sentinel " +
                "for the whole file; a negative bound is not \"unlimited\". It would also make the " +
                "completion aggregate rowsToScan x (rowsToScan + 1) / 2 negative, so a correct " +
                "scan reports repro_file_scan_verified{result=\"mismatch\"} and throws non-retryably.");
        }

        if (fs.LogInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"fileScan.logInterval must be > 0 (got {fs.LogInterval}). One wall-clock interval " +
                "gates both the console line and the pressure sampler, so at zero every batch takes " +
                "a GC.GetGCMemoryInfo() sample (~400 B a call) and the sampler dominates the " +
                "allocation counter it publishes.");
        }

        if (fs.BufferBytes is < MinBufferBytes or > MaxBufferBytes)
        {
            throw new ArgumentException(
                $"fileScan.bufferBytes must be in [{MinBufferBytes}, {MaxBufferBytes}] (got " +
                $"{fs.BufferBytes}). The scan finds line breaks itself and treats a full buffer " +
                "with no LF as terminal, so a buffer below the longest row (76 bytes in the " +
                "shipped corpora) fails a legal file, and below one page the read syscall rate is " +
                "the scan. Above the ceiling the buffer is one Large Object Heap allocation held " +
                "for the whole attempt, stepping loh_bytes and working_set_bytes the way " +
                "fault.slurpWholeFile does, so neither knob can attribute the step to itself. The " +
                "85,000-byte LOH threshold (a byte[] reaches it at 84,976) is inside the range on " +
                "purpose.");
        }

        // 3 and 4 bound the batch period, the loop's reaction time. Vacuous when unthrottled.
        var batchPeriod = fs.TargetRowsPerSecond > 0
            ? TimeSpan.FromSeconds((double)fs.BatchRows / fs.TargetRowsPerSecond)
            : TimeSpan.Zero;

        // 3. The batch must reach a token check at least every 2s.
        if (batchPeriod > MaxBatchPeriod)
        {
            throw new ArgumentException(
                $"fileScan.batchRows ({fs.BatchRows}) over targetRowsPerSecond " +
                $"({fs.TargetRowsPerSecond}) is a batch period of {batchPeriod}, above the " +
                $"{MaxBatchPeriod} cap. The batch boundary is the only place the loop observes " +
                "ctx.CancellationToken, polls ctx.WorkerShutdownToken or calls Heartbeat(), so " +
                "inside a long batch the activity sees neither a drain nor a cancel and emits no " +
                "heartbeat.");
        }

        // 4. And not so short that Task.Delay cannot express the sleep.
        if (fs.TargetRowsPerSecond > 0 && batchPeriod < MinBatchPeriod)
        {
            throw new ArgumentException(
                $"fileScan.batchRows ({fs.BatchRows}) over targetRowsPerSecond " +
                $"({fs.TargetRowsPerSecond}) is a batch period of {batchPeriod}, below the " +
                $"{MinBatchPeriod} floor. Task.Delay cannot express a sub-tick sleep and rounds up " +
                "to the platform timer, so the process runs slower than the configured rate while " +
                "repro_file_scan_rows_expected, the console line and every rows/s panel report the " +
                "configured one.");
        }

        // 5. The heartbeat rung, against the batch period rather than against liveness.
        var heartbeatFloor = TimeSpan.FromTicks(
            Math.Max(MinHeartbeatTimeout.Ticks, batchPeriod.Ticks * 10));
        if (fs.HeartbeatTimeout < heartbeatFloor)
        {
            throw new ArgumentException(
                $"fileScan.heartbeatTimeout must be >= max({MinHeartbeatTimeout}, 10 x batchPeriod " +
                $"{batchPeriod}) = {heartbeatFloor} (got {fs.HeartbeatTimeout}). Ten batch periods " +
                "is the margin that keeps one GC pause or one page-cache miss from timing the " +
                "attempt out on a healthy worker, which reads as \"resume is broken\". The 5s floor " +
                "is separate: the throttle is min(0.8 x this, " +
                "worker.maxHeartbeatThrottleInterval), and below 5s a kill -9 redoes under 4s of " +
                "rows, too little to see in a demo.");
        }

        if (fs.StartToCloseTimeout <= fs.HeartbeatTimeout)
        {
            throw new ArgumentException(
                $"fileScan.startToCloseTimeout ({fs.StartToCloseTimeout}) must exceed " +
                $"fileScan.heartbeatTimeout ({fs.HeartbeatTimeout}). Otherwise every attempt dies " +
                "of start-to-close before a heartbeat timeout can be observed, so the server never " +
                "reschedules from the checkpoint and the resume path is never taken.");
        }

        // 6. The derived floor for both long rungs, from maxRows or LargestShippedCorpusRows and never
        // from the file on disk. Vacuous when unthrottled.
        if (fs.TargetRowsPerSecond > 0)
        {
            var rowsToScan = fs.MaxRows > 0 ? fs.MaxRows : LargestShippedCorpusRows;
            var derivedFrom = fs.MaxRows > 0
                ? "fileScan.maxRows"
                : $"the largest shipped corpus, sample-500mb.txt at {LargestShippedCorpusRows} rows";
            var worstScan = TimeSpan.FromSeconds((double)rowsToScan / fs.TargetRowsPerSecond);

            var startFloor = worstScan + LadderHeadroom;
            if (fs.StartToCloseTimeout < startFloor)
            {
                throw new ArgumentException(
                    $"fileScan.startToCloseTimeout must be >= worstScan + {LadderHeadroom} = " +
                    $"{startFloor} (got {fs.StartToCloseTimeout}). worstScan is {rowsToScan} rows " +
                    $"at {fs.TargetRowsPerSecond} rows/s = {worstScan}, taken from {derivedFrom} " +
                    "and never from the file on disk, which is gitignored and may be absent. Below " +
                    "this floor attempt 1 dies part-way through the corpus on a healthy worker, and " +
                    "every retry resumes from the last checkpoint and dies at the same place until " +
                    "maximumAttempts is gone.");
            }

            // The real SDK formula, so the message quotes the throttle the SDK actually takes.
            var throttle = TimeSpan.FromTicks(Math.Min(
                (long)(fs.HeartbeatTimeout.Ticks * 0.8),
                config.Worker.MaxHeartbeatThrottleInterval.Ticks));
            var perResume = fs.HeartbeatTimeout + fs.Retry.MaximumInterval + throttle;
            var scheduleFloor = worstScan + (perResume * LadderResumes) + LadderHeadroom;
            if (fs.ScheduleToCloseTimeout < scheduleFloor)
            {
                throw new ArgumentException(
                    $"fileScan.scheduleToCloseTimeout must be >= worstScan + {LadderResumes} x " +
                    $"(heartbeatTimeout + retry.maximumInterval + throttle) + {LadderHeadroom} = " +
                    $"{scheduleFloor} (got {fs.ScheduleToCloseTimeout}). Useful work is one " +
                    $"worst-case scan ({worstScan}, from {derivedFrom}) however many attempts it " +
                    $"takes, and each resume adds heartbeatTimeout ({fs.HeartbeatTimeout}, the " +
                    $"server noticing) + retry.maximumInterval ({fs.Retry.MaximumInterval}, " +
                    $"backoff) + throttle ({throttle}, the reading redone) = {perResume}. Below this " +
                    "floor the workflow fails schedule-to-close mid-scan with attempts still on the " +
                    "clock, which reads as \"resume is broken\".");
            }
        }

        // 7. The retry policy, and the queue name against both queues that already exist.
        if (fs.Retry.MaximumAttempts <= 0)
        {
            throw new ArgumentException(
                $"fileScan.retry.maximumAttempts must be > 0 (got {fs.Retry.MaximumAttempts}). 0 " +
                "means unlimited in Temporalio.Common.RetryPolicy, not \"do not retry\", and an " +
                "unbounded chain of half-hour scans holds an activity slot on the scan queue " +
                "forever. Write 1 for no retry, though 1 also removes the resume this case exists " +
                "to show: each kill -9 spends one attempt and docs/HEARTBEATING.md's recipe does " +
                "three cycles, which is why the shipped value is 10.");
        }

        if (string.IsNullOrWhiteSpace(fs.TaskQueue))
        {
            throw new ArgumentException(
                $"fileScan.taskQueue must not be empty (got \"{fs.TaskQueue}\"). It is not a " +
                "fallback to taskQueue: the server rejects an empty queue name when the worker " +
                "polls, so the worker starts and takes no scan task.");
        }

        // Prefix-disjoint, as in ValidateLocalActivity, and stronger here because this queue shares
        // config.TaskQueue's namespace: a collision puts a second heartbeating activity type on the queue
        // whose slot panel sums temporal_worker_task_slots_used, which carries no activity_type label.
        if (fs.TaskQueue.StartsWith(config.TaskQueue, StringComparison.Ordinal) ||
            config.TaskQueue.StartsWith(fs.TaskQueue, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"fileScan.taskQueue (\"{fs.TaskQueue}\") and taskQueue (\"{config.TaskQueue}\") " +
                "must not be a prefix of one another, in either direction. They are in the same " +
                "namespace, so a collision merges a multi-minute heartbeating scan into the seed " +
                "case's queue, and temporal_worker_task_slots_used carries no activity_type label " +
                "to separate them. A shared prefix costs almost as much: every dashboard selector " +
                "and every `temporal task-queue describe` here matches on queue name.");
        }

        if (fs.TaskQueue.StartsWith(config.LocalActivity.TaskQueue, StringComparison.Ordinal) ||
            config.LocalActivity.TaskQueue.StartsWith(fs.TaskQueue, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"fileScan.taskQueue (\"{fs.TaskQueue}\") and localActivity.taskQueue " +
                $"(\"{config.LocalActivity.TaskQueue}\") must not be a prefix of one another, in " +
                "either direction. These two are in different namespaces, so the server permits it " +
                "and nothing fails at startup; what breaks is every human-facing lookup that " +
                "matches on queue name without a namespace.");
        }

        // Jitter.cs's formula is safe only because rate > 0 and jitter in [0, 1) are enforced here.
        if (fs.Rate <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"fileScan.rate must be > 0 (got {fs.Rate}). At zero the driver loop spins, and " +
                "each iteration it does not skip starts a multi-minute scan that holds an activity " +
                "slot, so capacity is reached immediately and stays reached.");
        }

        ValidateJitter(fs.Jitter, "fileScan");

        ValidateConcurrency(fs.Concurrency, "fileScan");

        // 8. The one cross-block refusal: retention x concurrency is an OOM, not a panel.
        if (config.Fault.RetainScannedRows && fs.Concurrency > 1)
        {
            var retainedGb = LargestShippedCorpusRows * RetainedBytesPerRow * fs.Concurrency
                / 1_000_000_000;
            throw new ArgumentException(
                $"fault.retainScannedRows is on together with fileScan.concurrency " +
                $"{fs.Concurrency}: refused. One retained scan of the largest shipped corpus is " +
                $"{LargestShippedCorpusRows} rows x about {RetainedBytesPerRow} bytes per retained " +
                $"string, so about 1.3 GB of live promoted heap, and {fs.Concurrency} concurrent " +
                $"scans is about {retainedGb} GB in one process sharing one workstation-GC heap. " +
                "That OOM-kills the worker and takes the demo's whole signal with it. Turn the " +
                "knob on at concurrency 1: one retained scan already moves every panel it should.");
        }
    }

    /// <summary>The jitter bound, identical for all four driver loops.</summary>
    /// <remarks>Shared because the reason is a property of Jitter.NextInterval, not of any one loop.</remarks>
    /// <param name="jitter">The configured fraction.</param>
    /// <param name="block">The config block name, used as the message prefix.</param>
    private static void ValidateJitter(double jitter, string block)
    {
        if (jitter is < 0 or >= 1)
        {
            throw new ArgumentException(
                $"{block}.jitter must be in [0, 1) (got {jitter}). The interval is " +
                "rate x [1-jitter, 1+jitter], so at 1 the low end is zero and the loop spins.");
        }
    }

    /// <summary>The concurrency floor, identical for all four driver loops.</summary>
    /// <remarks>Shared because it is a property of <c>Repro.LoadGen.DriverLoop</c>, not of any one loop.</remarks>
    /// <param name="concurrency">The configured in-flight ceiling.</param>
    /// <param name="block">The config block name, used as the message prefix.</param>
    private static void ValidateConcurrency(int concurrency, string block)
    {
        if (concurrency <= 0)
        {
            throw new ArgumentException(
                $"{block}.concurrency must be > 0 (got {concurrency}), otherwise every tick is " +
                "skipped at capacity and the driver starts nothing at all.");
        }
    }
}
