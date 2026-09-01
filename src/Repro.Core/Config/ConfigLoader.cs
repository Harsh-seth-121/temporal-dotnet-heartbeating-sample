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
        Validate(config);
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
    private static void Validate(ReproConfig config)
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
                "localActivity.namespace must not be empty. It is the namespace this workflow's "
                + "client binds to, and an empty one is not a fallback to `default`.");
        }

        if (string.Equals(la.Namespace, config.Namespace, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"localActivity.namespace must differ from namespace (both are \"{la.Namespace}\"). "
                + "The whole point of the second namespace is that "
                + "history.workflowTaskHeartbeatTimeout is namespace-scoped: sharing one silently "
                + "applies this case's 1m override to the other three workflows too, and they have "
                + "no local activities, so it would look like heartbeat behaviour appearing from "
                + "nowhere.");
        }

        if (string.IsNullOrWhiteSpace(la.TaskQueue))
        {
            throw new ArgumentException("localActivity.taskQueue must not be empty.");
        }

        if (la.MinDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"localActivity.minDuration must be > 0 (got {la.MinDuration}). At zero the burn "
                + "loop exits before its first clock check and every run reports an estimate of Pi "
                + "computed from no samples.");
        }

        if (la.MaxDuration < la.MinDuration)
        {
            throw new ArgumentException(
                $"localActivity.maxDuration ({la.MaxDuration}) must be >= minDuration "
                + $"({la.MinDuration}). The driver draws uniformly on the closed interval, which "
                + "throws on an inverted one.");
        }

        // The burn is wall-clock capped at maxDuration, so start-to-close must clear it with
        // room for scheduling and payload conversion. This does NOT make start-to-close
        // reachable -- the workflow task dies at the heartbeat timeout long before it -- it
        // only stops a config where the unreachable rung would have been the binding one.
        var floor = la.MaxDuration + TimeSpan.FromSeconds(30);
        if (la.StartToCloseTimeout < floor)
        {
            throw new ArgumentException(
                $"localActivity.startToCloseTimeout must be >= maxDuration + 30s = {floor} (got "
                + $"{la.StartToCloseTimeout}). Below that the longest draws die of start-to-close "
                + "on a healthy worker, which is a different failure from the one this case "
                + "demonstrates and is indistinguishable from it on the board.");
        }

        if (la.ScheduleToCloseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"localActivity.scheduleToCloseTimeout must be > 0 (got {la.ScheduleToCloseTimeout}). "
                + "It is deliberately NOT ordered against startToCloseTimeout: setting it below the "
                + "workflow task heartbeat timeout is the documented fix for this failure mode, and "
                + "a rule forbidding that would make the fix unconfigurable.");
        }

        if (la.RunTimeout <= la.MaxDuration)
        {
            throw new ArgumentException(
                $"localActivity.runTimeout ({la.RunTimeout}) must be > maxDuration "
                + $"({la.MaxDuration}). It is the ONLY bound that actually ends a run whose local "
                + "activity keeps being re-executed, so at or below the burn length every run is "
                + "killed by it, including the ones that would have completed.");
        }

        if (la.Retry.MaximumAttempts <= 0)
        {
            throw new ArgumentException(
                $"localActivity.retry.maximumAttempts must be > 0 (got {la.Retry.MaximumAttempts}). "
                + "0 means UNLIMITED in Temporalio.Common.RetryPolicy, and an unset RetryPolicy on a "
                + "LOCAL activity means retry forever, so both routes to \"no policy\" give an "
                + "unbounded chain of multi-minute CPU burns. Write 1 for \"do not retry\".");
        }

        if (la.Rate <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"localActivity.rate must be > 0 (got {la.Rate}). At zero the driver loop is a busy "
                + "spin that starts a CPU-bound workflow every iteration.");
        }

        if (la.Jitter is < 0 or >= 1)
        {
            throw new ArgumentException(
                $"localActivity.jitter must be in [0, 1) (got {la.Jitter}). The interval is "
                + "rate x [1-jitter, 1+jitter], so at 1 the low end is zero and the loop spins.");
        }

        if (la.Concurrency <= 0)
        {
            throw new ArgumentException(
                $"localActivity.concurrency must be > 0 (got {la.Concurrency}), otherwise every tick "
                + "is skipped at capacity and the driver starts nothing at all.");
        }

        if (la.MaxConcurrentLocalActivities <= 0)
        {
            throw new ArgumentException(
                "localActivity.maxConcurrentLocalActivities must be > 0 (got "
                + $"{la.MaxConcurrentLocalActivities}). It is applied verbatim to "
                + "TemporalWorkerOptions, which rejects a non-positive value, and 0 is not a "
                + "\"leave the SDK default\" sentinel here the way worker.maxConcurrentActivities "
                + "treats it.");
        }
    }
}
