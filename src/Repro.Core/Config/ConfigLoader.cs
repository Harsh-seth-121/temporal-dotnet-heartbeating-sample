using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Repro.Core.Config;

/// <summary>Loads config.yaml over the POCO defaults, then applies env-var overrides.</summary>
public static class ConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        // CamelCaseNamingConvention maps TaskQueue -> taskQueue, which is the key
        // style the Go config.yaml used and the READMEs quote. Note it only lowers
        // the FIRST character; LowerCaseNamingConvention would give "taskqueue" and
        // every key would fail to match.
        //
        // Use .Instance, not `new`: the public constructors are [Obsolete], and
        // this repo builds with TreatWarningsAsErrors.
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new GoDurationYamlConverter())
        // NO .IgnoreUnmatchedProperties(). A misspelled key should stop the process,
        // not silently run with a default. `failurRate: 0.4` that quietly means 0.0
        // is a wasted afternoon staring at a flat panel.
        .Build();

    /// <summary>Resolve the config path: explicit flag, else env, else search upward for config.yaml.</summary>
    /// <remarks>
    /// The upward search is what makes `dotnet run --project src/Repro.Worker` work
    /// from the repo root with no flag, matching `go run ./cmd/worker`. The csproj
    /// also copies config.yaml next to the binary, which covers running the built
    /// executable directly.
    /// </remarks>
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
    /// <param name="config">Deserialized config, MUTATED here: see the normalizing calls below.</param>
    /// <param name="configPath">
    /// The path <see cref="Load"/> actually read, which is the only directory a relative path
    /// inside the file may be resolved against. See <see cref="ValidateFileScan"/>.
    /// </param>
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
                "both run on this host and each needs its own exporter port; prometheus.yml scrapes " +
                "8077 and 8078 as separate jobs.");
        }

        // prometheus-net builds the push URL as {Endpoint}/job/{Job}/instance/{Instance}
        // and does NOT append /metrics for you. Without it every push 404s, and
        // MetricPusher reports that through OnError rather than throwing.
        if (!config.Metrics.PushgatewayUrl.TrimEnd('/').EndsWith("/metrics", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"metrics.pushgatewayUrl must end in /metrics (got \"{config.Metrics.PushgatewayUrl}\"). " +
                "prometheus-net's MetricPusher appends /job/<job>/instance/<instance> to it verbatim, " +
                "so without the path every push 404s and is reported only through OnError.");
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

        if (config.Simple.Jitter is < 0 or >= 1)
        {
            throw new ArgumentException(
                $"simple.jitter must be in [0, 1) (got {config.Simple.Jitter}). The interval is " +
                "rate x [1-jitter, 1+jitter], so at 1 the low end is zero and the loop spins.");
        }

        if (config.Simple.Concurrency <= 0)
        {
            throw new ArgumentException(
                $"simple.concurrency must be > 0 (got {config.Simple.Concurrency}), otherwise " +
                "every tick is skipped at capacity and the driver starts nothing at all.");
        }

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
                "calls Random.Shared.Next(gapMs + 1) to pick each gap, which throws on a " +
                "negative bound. It throws inside a fire-and-forget run body, so the " +
                "only symptom is the failure counter climbing.");
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

    /// <summary>The <c>simpleActivity:</c> block. Split out only to keep Validate readable.</summary>
    /// <remarks>
    /// Every message names the key, gives the value, and says what breaks.
    /// <para>
    /// ONE rule here is stricter than its <c>activity:</c> counterpart:
    /// retry.maximumAttempts may not be 0, because 0 means UNLIMITED and this activity talks
    /// to a third party. The other retry rules have no counterpart at all, because
    /// <c>config.Activity.Retry</c> is never validated. And rate/jitter/concurrency are not
    /// stricter than anything: they repeat the <c>simple.*</c> rules above with the same
    /// bounds, since there is no <c>activity.rate</c> to compare against.
    /// </para>
    /// </remarks>
    private static void ValidateSimpleActivity(ReproConfig config)
    {
        var sa = config.SimpleActivity;

        if (sa.SleepDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"simpleActivity.sleepDuration must be > 0 (got {sa.SleepDuration}). A negative " +
                "value throws inside the activity, where the real cause is buried under " +
                "retry.maximumAttempts retries and an ActivityFailure chain. Zero makes both the " +
                "workflow's name and repro_simple_activity_latency's 5000ms boundaries a lie.");
        }

        if (sa.HttpTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"simpleActivity.httpTimeout must be > 0 (got {sa.HttpTimeout}). A downed " +
                "interface fails fast, but a BLACKHOLED route does not, so without this the " +
                "request runs until start-to-close kills the whole attempt. The RETRY " +
                "CHAIN then outlives demo-down.sh's drain window, with nothing in the log to " +
                "say which of sleep, DNS, TLS or response ran long.");
        }

        // The activity derives its request deadline as
        // min(httpTimeout, startToClose - sleep - 2s), so this is the rule that keeps that
        // subtraction positive. 2s of headroom covers activity-task scheduling and payload
        // conversion, and the derived value must still leave room for DNS + TLS + request.
        var floor = sa.SleepDuration + sa.HttpTimeout + TimeSpan.FromSeconds(2);
        if (sa.StartToCloseTimeout < floor)
        {
            throw new ArgumentException(
                $"simpleActivity.startToCloseTimeout must be >= sleepDuration + httpTimeout + 2s " +
                $"= {floor} (got {sa.StartToCloseTimeout}). The activity sleeps first and then makes " +
                "one HTTP round trip in the SAME attempt, so with less headroom every attempt dies " +
                "of start-to-close against a perfectly healthy network and the retry policy burns " +
                "through every attempt proving it. With no heartbeat timeout, start-to-close is the " +
                "only activity timeout this workflow can produce.");
        }

        if (sa.Retry.InitialInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"simpleActivity.retry.initialInterval must be > 0 (got {sa.Retry.InitialInterval}). " +
                "An invalid retry policy is rejected when the ScheduleActivityTask command is " +
                "validated, which fails the WORKFLOW TASK rather than the workflow, so the symptom " +
                "is a run that sits in RUNNING and never schedules its activity.");
        }

        if (sa.Retry.BackoffCoefficient < 1)
        {
            throw new ArgumentException(
                $"simpleActivity.retry.backoffCoefficient must be >= 1 (got {sa.Retry.BackoffCoefficient}). " +
                "Below 1 the interval SHRINKS on every retry, which is a retry storm wearing a retry " +
                "policy's clothes, against the frontend and against api.open-meteo.com at once.");
        }

        if (sa.Retry.MaximumInterval < sa.Retry.InitialInterval)
        {
            throw new ArgumentException(
                $"simpleActivity.retry.maximumInterval ({sa.Retry.MaximumInterval}) must be >= " +
                $"initialInterval ({sa.Retry.InitialInterval}). The maximum CLAMPS the computed " +
                "interval, so a maximum below the initial makes backoffCoefficient do nothing and " +
                "fires every retry at maximumInterval.");
        }

        if (sa.Retry.MaximumAttempts <= 0)
        {
            throw new ArgumentException(
                $"simpleActivity.retry.maximumAttempts must be > 0 (got {sa.Retry.MaximumAttempts}). " +
                "0 means UNLIMITED in Temporalio.Common.RetryPolicy, not \"do not retry\". Write 1 " +
                "for that. Unlimited retries against a THIRD-PARTY endpoint is the one place in this " +
                "repo where a stuck run is also someone else's problem, and it parks the loadgen past " +
                "demo-down.sh's drain budget. activity.retry.maximumAttempts may be 0 because that " +
                "activity only talks to itself.");
        }

        if (sa.Latitude is < -90 or > 90)
        {
            throw new ArgumentException(
                $"simpleActivity.latitude must be in [-90, 90] (got {sa.Latitude}). Open-Meteo answers " +
                "HTTP 400 outside that, which the activity throws NON-retryably, so a typo fails every " +
                "run on attempt 1 instead of quietly producing a synthetic reading. A config bug is " +
                "not an outage.");
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
                "exercise the synthetic fallback on purpose.");
        }

        if (sa.Rate <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"simpleActivity.rate must be > 0 (got {sa.Rate}). At zero the driver loop is a busy " +
                "spin. Unlike simple.rate this one spins against api.open-meteo.com, which " +
                "rate-limits you with a 429, which IS retryable, which then spends every attempt of " +
                "every run on it.");
        }

        if (sa.Jitter is < 0 or >= 1)
        {
            throw new ArgumentException(
                $"simpleActivity.jitter must be in [0, 1) (got {sa.Jitter}). The interval is " +
                "rate x [1-jitter, 1+jitter], so at 1 the low end is zero and the loop spins.");
        }

        if (sa.Concurrency <= 0)
        {
            throw new ArgumentException(
                $"simpleActivity.concurrency must be > 0 (got {sa.Concurrency}), otherwise " +
                "every tick is skipped at capacity and the driver starts nothing at all.");
        }
    }

    /// <summary>The <c>localActivity:</c> block. Split out to keep Validate readable.</summary>
    /// <remarks>
    /// Two rules here have no counterpart in any other block, and both guard the thing that
    /// makes this case work at all rather than a typo.
    /// <para>
    /// The NAMESPACE must differ from <see cref="ReproConfig.Namespace"/>. Sharing one would
    /// not fail anything loudly -- the workflow would run fine -- it would silently apply this
    /// case's 1m <c>history.workflowTaskHeartbeatTimeout</c> override to the other three
    /// workflows as well, because that setting is namespace-scoped and nothing finer. The
    /// symptom would be heartbeat-timeout behaviour appearing in a workflow that has no local
    /// activities, which is exactly the class of misattribution this repo exists to prevent.
    /// </para>
    /// <para>
    /// <c>scheduleToCloseTimeout</c> is deliberately NOT constrained against
    /// <c>startToCloseTimeout</c> or against the heartbeat timeout. Setting it BELOW the
    /// heartbeat timeout is the documented mitigation for this whole failure mode, so a rule
    /// ordering it after start-to-close would make the fix unconfigurable while looking like
    /// ordinary hygiene.
    /// </para>
    /// </remarks>
    private static void ValidateLocalActivity(ReproConfig config)
    {
        var la = config.LocalActivity;

        if (string.IsNullOrWhiteSpace(la.Namespace))
        {
            throw new ArgumentException(
                "localActivity.namespace must not be empty. It is the namespace this workflow's " +
                "client binds to, and an empty one is not a fallback to `default`.");
        }

        if (string.Equals(la.Namespace, config.Namespace, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"localActivity.namespace must differ from namespace (both are \"{la.Namespace}\"). " +
                "The whole point of the second namespace is that " +
                "history.workflowTaskHeartbeatTimeout is namespace-scoped: sharing one silently " +
                "applies this case's 1m override to the other three workflows too, and they have " +
                "no local activities, so it would look like heartbeat behaviour appearing from " +
                "nowhere.");
        }

        if (string.IsNullOrWhiteSpace(la.TaskQueue))
        {
            throw new ArgumentException("localActivity.taskQueue must not be empty.");
        }

        // PREFIX-disjoint, not merely unequal. Task queues are namespace-scoped, so the server
        // would happily accept the same name in both namespaces; the cost is paid entirely by
        // humans, and a shared PREFIX costs almost as much as a shared name. This repo tells its
        // two workers apart by queue name first, in `temporal task-queue describe`, in the
        // worker logs and in every dashboard selector -- none of which carry a namespace unless
        // you remember to add one.
        if (la.TaskQueue.StartsWith(config.TaskQueue, StringComparison.Ordinal) ||
            config.TaskQueue.StartsWith(la.TaskQueue, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"localActivity.taskQueue (\"{la.TaskQueue}\") and taskQueue " +
                $"(\"{config.TaskQueue}\") must not be a prefix of one another, in either " +
                "direction. They name queues in DIFFERENT namespaces, so the server permits it " +
                "and nothing fails at startup; what breaks is every human-facing lookup that " +
                "matches on queue name without a namespace.");
        }

        if (la.MinDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"localActivity.minDuration must be > 0 (got {la.MinDuration}). At zero the burn " +
                "loop exits before its first clock check and every run reports an estimate of Pi " +
                "computed from no samples.");
        }

        if (la.MaxDuration < la.MinDuration)
        {
            throw new ArgumentException(
                $"localActivity.maxDuration ({la.MaxDuration}) must be >= minDuration " +
                $"({la.MinDuration}). The driver draws uniformly on the closed interval, which " +
                "throws on an inverted one.");
        }

        // The burn is wall-clock capped at maxDuration, so start-to-close must clear it with
        // room for scheduling and payload conversion. This does NOT make start-to-close
        // reachable -- the workflow task dies at the heartbeat timeout long before it -- it
        // only stops a config where the unreachable rung would have been the binding one.
        var floor = la.MaxDuration + TimeSpan.FromSeconds(30);
        if (la.StartToCloseTimeout < floor)
        {
            throw new ArgumentException(
                $"localActivity.startToCloseTimeout must be >= maxDuration + 30s = {floor} (got " +
                $"{la.StartToCloseTimeout}). Below that the longest draws die of start-to-close " +
                "on a healthy worker, which is a different failure from the one this case " +
                "demonstrates and is indistinguishable from it on the board.");
        }

        if (la.ScheduleToCloseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"localActivity.scheduleToCloseTimeout must be > 0 (got {la.ScheduleToCloseTimeout}). " +
                "It is deliberately NOT ordered against startToCloseTimeout: setting it below the " +
                "workflow task heartbeat timeout is the documented fix for this failure mode, and " +
                "a rule forbidding that would make the fix unconfigurable.");
        }

        if (la.RunTimeout <= la.MaxDuration)
        {
            throw new ArgumentException(
                $"localActivity.runTimeout ({la.RunTimeout}) must be > maxDuration " +
                $"({la.MaxDuration}). It is the ONLY bound that actually ends a run whose local " +
                "activity keeps being re-executed, so at or below the burn length every run is " +
                "killed by it, including the ones that would have completed.");
        }

        if (la.Retry.MaximumAttempts <= 0)
        {
            throw new ArgumentException(
                $"localActivity.retry.maximumAttempts must be > 0 (got {la.Retry.MaximumAttempts}). " +
                "0 means UNLIMITED in Temporalio.Common.RetryPolicy, and an unset RetryPolicy on a " +
                "LOCAL activity means retry forever, so both routes to \"no policy\" give an " +
                "unbounded chain of multi-minute CPU burns. Write 1 for \"do not retry\".");
        }

        if (la.Rate <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"localActivity.rate must be > 0 (got {la.Rate}). At zero the driver loop is a busy " +
                "spin that starts a CPU-bound workflow every iteration.");
        }

        if (la.Jitter is < 0 or >= 1)
        {
            throw new ArgumentException(
                $"localActivity.jitter must be in [0, 1) (got {la.Jitter}). The interval is " +
                "rate x [1-jitter, 1+jitter], so at 1 the low end is zero and the loop spins.");
        }

        if (la.Concurrency <= 0)
        {
            throw new ArgumentException(
                $"localActivity.concurrency must be > 0 (got {la.Concurrency}), otherwise every tick " +
                "is skipped at capacity and the driver starts nothing at all.");
        }

        if (la.MaxConcurrentLocalActivities <= 0)
        {
            throw new ArgumentException(
                "localActivity.maxConcurrentLocalActivities must be > 0 (got " +
                $"{la.MaxConcurrentLocalActivities}). It is applied verbatim to " +
                "TemporalWorkerOptions, which rejects a non-positive value, and 0 is not a " +
                "\"leave the SDK default\" sentinel here the way worker.maxConcurrentActivities " +
                "treats it.");
        }
    }

    /// <summary>Rows in <c>sample-500mb.txt</c>, the largest corpus scripts/gen-samples produces.</summary>
    /// <remarks>
    /// A CONSTANT, and never <c>new FileInfo(fileScan.path).Length</c>, for the reason
    /// <see cref="ValidateFileScan"/> states: this method may not touch the filesystem. Kept in
    /// step with scripts/gen-samples/MANIFEST.txt BY HAND. Generate something larger and the
    /// timeout ladder is checked against the wrong worst case, and the symptom is an attempt
    /// that dies of start-to-close on a perfectly healthy worker part-way through the corpus.
    /// </remarks>
    private const long LargestShippedCorpusRows = 8_622_570;

    /// <summary>Bytes of LIVE heap one retained row costs, for <c>fault.retainScannedRows</c>' arithmetic.</summary>
    /// <remarks>
    /// A ~58-byte average row decoded to a UTF-16 string is 22 bytes of object header and length
    /// plus 2 bytes per char, rounded up, plus the 8-byte List slot holding the reference.
    /// Order-of-magnitude by intent: the message it feeds says "about".
    /// </remarks>
    private const int RetainedBytesPerRow = 150;

    /// <summary>
    /// Bounds on <c>fileScan.bufferBytes</c>. The range deliberately SPANS the 85,000-byte
    /// LOH threshold (a <c>byte[]</c> reaches it at 84,976), because crossing it on purpose is
    /// the cheapest one-line demonstration this repo has of that threshold.
    /// </summary>
    private const int MinBufferBytes = 4096;

    /// <inheritdoc cref="MinBufferBytes"/>
    private const int MaxBufferBytes = 16 * 1024 * 1024;

    /// <summary>Resumes the schedule-to-close ladder covers: <c>maximumAttempts - 1</c> at the shipped 10.</summary>
    /// <remarks>
    /// FIXED rather than derived from the configured <c>retry.maximumAttempts</c>. The ladder is
    /// provisioned for the documented worst case -- docs/HEARTBEATING.md's recipe does three
    /// kill cycles and a careless extra kill must not fail the workflow terminally -- and
    /// maximumAttempts is the field most likely to be edited on a whim. Deriving it would let
    /// lowering maximumAttempts quietly lower the floor as well, so the two numbers would never
    /// disagree and the check would stop being a check.
    /// </remarks>
    private const int LadderResumes = 9;

    /// <summary>Floor on the batch period: below one platform tick the configured rate is a lie.</summary>
    private static readonly TimeSpan MinBatchPeriod = TimeSpan.FromMilliseconds(10);

    /// <summary>Cap on the batch period: the loop's reaction time to a cancel, a drain or a heartbeat.</summary>
    private static readonly TimeSpan MaxBatchPeriod = TimeSpan.FromSeconds(2);

    /// <summary>Absolute floor on <c>fileScan.heartbeatTimeout</c>, independent of the batch period.</summary>
    private static readonly TimeSpan MinHeartbeatTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Slack both timeout rungs carry over their derived worst case.</summary>
    /// <remarks>
    /// Covers activity-task scheduling, payload conversion, opening the file and the header
    /// read, plus the ordinary variance of a multi-minute paced scan on a laptop.
    /// </remarks>
    private static readonly TimeSpan LadderHeadroom = TimeSpan.FromMinutes(2);

    /// <summary>The <c>fileScan:</c> block and its whole timeout ladder. Split out to keep Validate readable.</summary>
    /// <remarks>
    /// TWO things here are unlike every other block in this file, and both are the point.
    /// <para>
    /// IT NEVER STATS THE CORPUS. <c>sample_files/</c> is gitignored and generated, and
    /// ConfigTests calls <see cref="Load"/> against the COMMITTED config.yaml, so a
    /// <c>File.Exists</c> or a <c>FileInfo.Length</c> anywhere in this method would break
    /// <c>dotnet test</c> on every fresh clone. That is why rule 6's derived floor comes from
    /// <c>maxRows</c> when it is set and from <see cref="LargestShippedCorpusRows"/> otherwise,
    /// never from the file on disk. DO NOT "fix" this by reading the file: existence is checked
    /// where it can be handled instead, once in FileScanDriver's constructor, which skips its
    /// loop with a named banner, and again in the activity, which throws NON-retryably because
    /// a missing corpus is a config bug and burning ten retries proves nothing.
    /// </para>
    /// <para>
    /// IT MUTATES THE CONFIG, resolving <c>path</c> to an absolute path against the directory
    /// holding the resolved config file -- NOT the working directory. Same
    /// mutate-during-validate precedent <c>BindAddress.Normalize</c> sets at the top of
    /// <see cref="Validate"/>. docs/HEARTBEATING.md's kill-the-worker recipe runs the built
    /// binary from the repo root while demo-up.sh runs from elsewhere, so a cwd-relative path
    /// would silently mean two DIFFERENT files across a resume, and the checkpoint's
    /// corpus-identity check is the only thing that would ever notice.
    /// </para>
    /// <para>
    /// Every message names the key, quotes the value it got, and says what breaks.
    /// </para>
    /// </remarks>
    private static void ValidateFileScan(ReproConfig config, string configPath)
    {
        var fs = config.FileScan;

        // 1. path: non-empty, then resolved to absolute against the CONFIG FILE's directory.
        if (string.IsNullOrWhiteSpace(fs.Path))
        {
            throw new ArgumentException(
                $"fileScan.path must not be empty (got \"{fs.Path}\"). It names the corpus to scan " +
                "and there is no default that could be right, since the file is generated into a " +
                "gitignored directory by scripts/gen-samples/gen-samples.sh. An empty path is not " +
                "caught here as a missing FILE -- nothing in this method stats anything -- it " +
                "reaches the activity, which throws non-retryably, so every scan dies on attempt 1 " +
                "with the cause buried under an ActivityFailure chain.");
        }

        // Path.Combine returns its second argument unchanged when that is already absolute, so an
        // absolute path in the file survives verbatim. GetFullPath then normalizes ".." and ".",
        // which is what makes the RESUMING console line's absolute path worth printing.
        var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath));
        fs.Path = Path.GetFullPath(Path.Combine(
            string.IsNullOrEmpty(configDir) ? Directory.GetCurrentDirectory() : configDir,
            fs.Path));

        // 2. The plain scalar bounds.
        if (fs.TargetRowsPerSecond < 0)
        {
            throw new ArgumentException(
                $"fileScan.targetRowsPerSecond must be >= 0 (got {fs.TargetRowsPerSecond}). 0 is " +
                "the documented sentinel for UNTHROTTLED. A negative rate makes the pacer's " +
                "absolute due time run backwards, so every batch is already overdue, the scan runs " +
                "flat out, and every rows/s panel and the console line keep reporting the " +
                "configured rate.");
        }

        if (fs.BatchRows <= 0)
        {
            throw new ArgumentException(
                $"fileScan.batchRows must be > 0 (got {fs.BatchRows}). It is the number of rows " +
                "between one pace, cancel, drain, heartbeat and log check and the next, so at zero " +
                "the loop completes no rows between checks: the cursor never advances while the " +
                "activity keeps heartbeating an unchanged checkpoint, which on the board is " +
                "indistinguishable from a stalled disk.");
        }

        if (fs.MaxRows < 0)
        {
            throw new ArgumentException(
                $"fileScan.maxRows must be >= 0 (got {fs.MaxRows}). 0 is the documented sentinel " +
                "for the whole file; a negative bound is not \"unlimited\". It would also make the " +
                "completion aggregate rowsToScan x (rowsToScan + 1) / 2 negative, so a correct " +
                "scan reports repro_file_scan_verified{result=\"mismatch\"} and throws " +
                "non-retryably -- the one failure this case must never produce spuriously.");
        }

        if (fs.LogInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"fileScan.logInterval must be > 0 (got {fs.LogInterval}). One wall-clock interval " +
                "gates both the console line and the pressure sampler, and at zero every batch " +
                "takes a GC.GetGCMemoryInfo() sample (~400 B a call) and prints a line, so the " +
                "sampler comes to dominate the allocation counter it publishes and the memory " +
                "panels measure the measurement.");
        }

        if (fs.BufferBytes is < MinBufferBytes or > MaxBufferBytes)
        {
            throw new ArgumentException(
                $"fileScan.bufferBytes must be in [{MinBufferBytes}, {MaxBufferBytes}] (got " +
                $"{fs.BufferBytes}). The scan finds line breaks itself and treats a FULL buffer " +
                "with no LF as terminal, so a buffer below the longest row -- 76 bytes in the " +
                "shipped corpora -- fails a perfectly legal file, and below one page the read " +
                "syscall rate is the scan. Above the ceiling the buffer is a slurp with extra " +
                "steps: one Large Object Heap allocation held for the whole attempt, stepping " +
                "loh_bytes and working_set_bytes exactly the way fault.slurpWholeFile is supposed " +
                "to, so neither knob can attribute the step to itself any more. Crossing the " +
                "85,000-byte LOH threshold (a byte[] reaches it at 84,976) is INSIDE the range on " +
                "purpose: that is a demonstration, not a mistake.");
        }

        // 3 and 4 bound the BATCH PERIOD, which is the loop's reaction time. Both are vacuous
        // when unthrottled: with no target rate a batch is however long the raw read takes,
        // microseconds, and there is no configured period to bound.
        var batchPeriod = fs.TargetRowsPerSecond > 0
            ? TimeSpan.FromSeconds((double)fs.BatchRows / fs.TargetRowsPerSecond)
            : TimeSpan.Zero;

        // 3. The batch must reach a token check at least every 2s.
        if (batchPeriod > MaxBatchPeriod)
        {
            throw new ArgumentException(
                $"fileScan.batchRows ({fs.BatchRows}) over targetRowsPerSecond " +
                $"({fs.TargetRowsPerSecond}) is a batch period of {batchPeriod}, above the " +
                $"{MaxBatchPeriod} cap. The batch boundary is the ONLY place the loop observes " +
                "ctx.CancellationToken, polls ctx.WorkerShutdownToken or calls Heartbeat(), so a " +
                "long batch is not slow, it is deaf: batchRows 1000000 at 6000 rows/s is a " +
                "167-second batch, inside which the activity can observe neither a drain nor a " +
                "cancel nor emit one heartbeat.");
        }

        // 4. And not so short that Task.Delay cannot express the sleep.
        if (fs.TargetRowsPerSecond > 0 && batchPeriod < MinBatchPeriod)
        {
            throw new ArgumentException(
                $"fileScan.batchRows ({fs.BatchRows}) over targetRowsPerSecond " +
                $"({fs.TargetRowsPerSecond}) is a batch period of {batchPeriod}, below the " +
                $"{MinBatchPeriod} floor. Task.Delay cannot express a sub-tick sleep and rounds UP " +
                "to the platform timer, so the process runs SLOWER than the configured rate while " +
                "repro_file_scan_rows_expected, the console line and every rows/s panel report the " +
                "configured one. That is the same lie a per-row sleep would tell, which is why the " +
                "loop batches at all.");
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
                "ATTEMPT out on a healthy worker, which reads as \"resume is broken\" and is the " +
                "worst way for this case to fail. The 5s floor is separate: the throttle is " +
                "min(0.8 x this, worker.maxHeartbeatThrottleInterval), and below 5s a kill -9 " +
                "redoes under 4s of rows, which is visible on a panel and invisible in a demo.");
        }

        if (fs.StartToCloseTimeout <= fs.HeartbeatTimeout)
        {
            throw new ArgumentException(
                $"fileScan.startToCloseTimeout ({fs.StartToCloseTimeout}) must exceed " +
                $"fileScan.heartbeatTimeout ({fs.HeartbeatTimeout}). Otherwise every attempt dies " +
                "of start-to-close before a heartbeat timeout can be observed, so the server never " +
                "reschedules from the checkpoint and the resume path this case exists to " +
                "demonstrate is never taken.");
        }

        // 6. The DERIVED floor for both long rungs. rowsToScan comes from maxRows when set and
        // from LargestShippedCorpusRows otherwise -- NEVER from new FileInfo(fs.Path).Length,
        // which would break dotnet test on a fresh clone where the corpus does not exist. See
        // the remarks on this method before changing that.
        //
        // Vacuous when unthrottled: with no target rate there is no derivable duration, only
        // whatever the machine's read ceiling turns out to be.
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
                    "this floor attempt 1 dies of start-to-close part-way through the corpus on a " +
                    "healthy worker, and every retry then resumes from the last checkpoint and " +
                    "dies at the same place until maximumAttempts is gone.");
            }

            // The real SDK formula, so the number in the message is the number the throttle
            // actually takes: min(0.8 x heartbeatTimeout, worker.maxHeartbeatThrottleInterval).
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
                    $"{scheduleFloor} (got {fs.ScheduleToCloseTimeout}). \"maximumAttempts x " +
                    "startToClose\" is the WRONG model and gives an absurd number: useful work is " +
                    $"ONE worst-case scan ({worstScan}, from {derivedFrom}) however many attempts " +
                    $"it takes, and each RESUME adds heartbeatTimeout ({fs.HeartbeatTimeout}, the " +
                    $"server noticing) + retry.maximumInterval ({fs.Retry.MaximumInterval}, " +
                    $"backoff) + throttle ({throttle}, the reading that is redone) = {perResume}. " +
                    "Below this floor the WORKFLOW fails schedule-to-close mid-scan with attempts " +
                    "still on the clock, which also reads as \"resume is broken\".");
            }
        }

        // 7. The retry policy, and the queue name against both queues that already exist.
        if (fs.Retry.MaximumAttempts <= 0)
        {
            throw new ArgumentException(
                $"fileScan.retry.maximumAttempts must be > 0 (got {fs.Retry.MaximumAttempts}). 0 " +
                "means UNLIMITED in Temporalio.Common.RetryPolicy, not \"do not retry\", and an " +
                "unbounded chain of half-hour scans holds an activity slot on the scan queue " +
                "forever. Write 1 for no retry -- though 1 also removes the resume this case " +
                "exists to show, because each kill -9 spends one attempt and " +
                "docs/HEARTBEATING.md's recipe does three cycles. That is why the shipped value " +
                "is 10 rather than the usual 5.");
        }

        if (string.IsNullOrWhiteSpace(fs.TaskQueue))
        {
            throw new ArgumentException(
                $"fileScan.taskQueue must not be empty (got \"{fs.TaskQueue}\"). It is not a " +
                "fallback to taskQueue: an empty queue name is rejected by the server when the " +
                "worker polls, so the worker starts, logs nothing useful and takes no scan task.");
        }

        // PREFIX-disjoint against BOTH existing queues, not merely unequal, in the shape of
        // ValidateLocalActivity above -- but one step stronger there, because THIS queue lives in
        // the SAME namespace as config.TaskQueue. A shared name is not only ambiguous to a human,
        // it puts a second heartbeating activity type on the queue whose slot panel sums
        // temporal_worker_task_slots_used unfiltered while claiming this repo has exactly one.
        // That metric carries no activity_type label, so there is no way to filter it back out.
        if (fs.TaskQueue.StartsWith(config.TaskQueue, StringComparison.Ordinal) ||
            config.TaskQueue.StartsWith(fs.TaskQueue, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"fileScan.taskQueue (\"{fs.TaskQueue}\") and taskQueue (\"{config.TaskQueue}\") " +
                "must not be a prefix of one another, in either direction. They are in the SAME " +
                "namespace, so an outright collision silently merges a multi-minute heartbeating " +
                "scan into the seed case's queue, and temporal_worker_task_slots_used carries no " +
                "activity_type label to separate them again. A shared PREFIX costs almost as much, " +
                "because every dashboard selector and every `temporal task-queue describe` in this " +
                "repo is read by matching on queue name.");
        }

        if (fs.TaskQueue.StartsWith(config.LocalActivity.TaskQueue, StringComparison.Ordinal) ||
            config.LocalActivity.TaskQueue.StartsWith(fs.TaskQueue, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"fileScan.taskQueue (\"{fs.TaskQueue}\") and localActivity.taskQueue " +
                $"(\"{config.LocalActivity.TaskQueue}\") must not be a prefix of one another, in " +
                "either direction. These two ARE in different namespaces, so the server permits it " +
                "and nothing fails at startup; what breaks is every human-facing lookup that " +
                "matches on queue name without a namespace, which is all of them.");
        }

        // The loadgen's FOURTH jittered loop, and the fourth copy of the contract Jitter.cs
        // names: that file's formula is safe only because rate > 0 and jitter in [0, 1) are
        // enforced here.
        if (fs.Rate <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"fileScan.rate must be > 0 (got {fs.Rate}). At zero the driver loop is a busy " +
                "spin, and each iteration it does not skip starts a multi-minute scan that holds " +
                "an activity slot, so capacity is reached immediately and stays reached.");
        }

        if (fs.Jitter is < 0 or >= 1)
        {
            throw new ArgumentException(
                $"fileScan.jitter must be in [0, 1) (got {fs.Jitter}). The interval is " +
                "rate x [1-jitter, 1+jitter], so at 1 the low end is zero and the loop spins.");
        }

        if (fs.Concurrency <= 0)
        {
            throw new ArgumentException(
                $"fileScan.concurrency must be > 0 (got {fs.Concurrency}), otherwise every tick is " +
                "skipped at capacity and the driver starts nothing at all.");
        }

        // 8. The one cross-block refusal: retention x concurrency is an OOM, not a panel.
        if (config.Fault.RetainScannedRows && fs.Concurrency > 1)
        {
            var retainedGb = LargestShippedCorpusRows * RetainedBytesPerRow * fs.Concurrency
                / 1_000_000_000;
            throw new ArgumentException(
                $"fault.retainScannedRows is on together with fileScan.concurrency " +
                $"{fs.Concurrency}: refused. One retained scan of the largest shipped corpus is " +
                $"{LargestShippedCorpusRows} rows x about {RetainedBytesPerRow} bytes per retained " +
                $"string, so about 1.3 GB of LIVE promoted heap, and {fs.Concurrency} concurrent " +
                $"scans is about {retainedGb} GB in one process sharing one workstation-GC heap. " +
                "The failure is an OOM-killed worker, which takes the whole demo's signal down " +
                "with it, not the empty panel you were expecting. Turn the knob on at " +
                "concurrency 1: one retained scan already moves every panel it is meant to move.");
        }
    }
}
