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
    }
}
