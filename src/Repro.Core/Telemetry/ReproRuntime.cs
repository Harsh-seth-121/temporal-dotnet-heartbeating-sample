using Temporalio.Runtime;

namespace Repro.Core.Telemetry;

/// <summary>
/// Builds the one TemporalRuntime this process gets. Call it as the first statement of Main,
/// before any TemporalClient, TemporalWorker or WorkflowReplayer exists.
/// </summary>
/// <remarks>
/// TemporalRuntime.Default is created on first touch, and TemporalClient.ConnectAsync touches it
/// when Runtime is unset, so a client that connects first binds to Default and its metrics are
/// lost. See docs/GOTCHAS.md, "TemporalRuntime must be built once, first, and shared". An explicit
/// factory with a single-shot guard rather than a static Lazy, because a Lazy hides when
/// construction happens.
/// </remarks>
public static class ReproRuntime
{
    private static int created;
    private static TemporalRuntime? instance;

    /// <summary>The runtime built by <see cref="CreateScrape"/> or adopted by the starter.</summary>
    public static TemporalRuntime Current => instance ?? throw new InvalidOperationException(
        "no TemporalRuntime has been built. Call ReproRuntime.CreateScrape(...) as the first line of Main, " +
        "before any TemporalClient exists. A client that connects first binds to TemporalRuntime.Default " +
        "and loses its metrics.");

    /// <summary>True when a runtime exists, so callers can stay quiet with <c>--metrics off</c>.</summary>
    public static bool IsConfigured => instance is not null;

    /// <summary>Registers an externally-built runtime. Used by the starter's push path.</summary>
    public static TemporalRuntime Adopt(TemporalRuntime runtime)
    {
        Claim();
        instance = runtime;
        return runtime;
    }

    /// <summary>Scrape path: Core's built-in Prometheus exporter. Worker, loadgen, replay.</summary>
    public static TemporalRuntime CreateScrape(string bindAddress)
    {
        Claim();

        // Normalize here as well as in ConfigLoader.Validate, because --metrics bypasses
        // the config file entirely.
        var bind = Config.BindAddress.Normalize(bindAddress, "--metrics / metrics.listenAddress");

        var runtime = new TemporalRuntime(new TemporalRuntimeOptions
        {
            Telemetry = new()
            {
                Metrics = new()
                {
                    Prometheus = new(bind)
                    {
                        // All three stay false: the vendored board
                        // sdk/temporal-core-sdks-otel.json is written with no _total
                        // and no _seconds. See docs/GOTCHAS.md, "Fixing the missing
                        // _total blanks the imported SDK board" and "Histograms are
                        // integer milliseconds, and counters carry no _total".
                        HasCounterTotalSuffix = false,
                        HasUnitSuffix = false,
                        UseSecondsForDuration = false,
                        HistogramBucketOverrides = HistogramBuckets.ScrapeOverrides,
                    },

                    // Left at the default "temporal_", and never changed. Core's bucket
                    // lookup falls back to default_buckets_for(strip_prefix("temporal_")),
                    // a hard-coded literal, so a different prefix makes the strip fail and
                    // every metric land in the catch-all bucket arm, invisibly.
                    MetricPrefix = null,

                    // See docs/GOTCHAS.md, "service_name is the only thing separating
                    // your worker from the server's".
                    AttachServiceName = true,

                    // See docs/GOTCHAS.md, "MetricsOptions.GlobalTags is silently dropped
                    // on the push path": setting it here would make the two processes
                    // disagree about which labels exist. Static labels belong in
                    // prometheus.yml's static_configs, where both paths get them.
                    GlobalTags = null,
                },
            },
        });

        instance = runtime;
        return runtime;
    }

    private static void Claim()
    {
        if (Interlocked.Exchange(ref created, 1) == 1)
        {
            throw new InvalidOperationException(
                "a TemporalRuntime has already been built in this process. There must be exactly one: " +
                "it owns the Prometheus registry and the exporter's TCP listener.");
        }
    }
}
