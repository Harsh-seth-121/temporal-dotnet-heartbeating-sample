using Temporalio.Runtime;

namespace Repro.Core.Telemetry;

/// <summary>
/// Builds the ONE TemporalRuntime this process gets. Call it as the FIRST statement
/// of Main, before any TemporalClient, TemporalWorker or WorkflowReplayer exists.
/// </summary>
/// <remarks>
/// Why "first" is not advice: TemporalRuntime.Default is created on first touch,
/// and TemporalClient.ConnectAsync touches it when Runtime is unset. A client bound
/// to Default never writes into a runtime you build later. The exporter on :8077
/// then answers 200 with an empty registry, Prometheus reports the target UP, and
/// every SDK panel is blank — with no exception, no log, and nothing to distinguish
/// it from an idle worker.
/// <para>
/// This is an explicit factory with a single-shot guard rather than a static Lazy
/// precisely because a Lazy hides WHEN construction happens, and construction order
/// is the entire failure mode.
/// </para>
/// </remarks>
public static class ReproRuntime
{
    private static int created;
    private static TemporalRuntime? instance;

    /// <summary>The runtime built by <see cref="CreateScrape"/> or adopted by the starter.</summary>
    public static TemporalRuntime Current => instance ?? throw new InvalidOperationException(
        "no TemporalRuntime has been built. Call ReproRuntime.CreateScrape(...) as the FIRST line of Main, " +
        "before any TemporalClient exists — a client that connects first binds to TemporalRuntime.Default " +
        "and its metrics are permanently lost.");

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

        // Normalize here as well as in ConfigLoader.Validate, because --metrics
        // bypasses the config file entirely and that override is exactly the one
        // people get wrong ("--metrics 127.0.0.1:8079").
        var bind = Config.BindAddress.Normalize(bindAddress, "--metrics / metrics.listenAddress");

        var runtime = new TemporalRuntime(new TemporalRuntimeOptions
        {
            Telemetry = new()
            {
                Metrics = new()
                {
                    Prometheus = new(bind)
                    {
                        // All three suffix flags stay FALSE.
                        //
                        // Not a style choice. temporalio/dashboards'
                        // sdk/temporal-core-sdks-otel.json — the board this repo
                        // vendors as the .NET equivalent of go-sdk-tally.json — is
                        // written with no _total and no _seconds. Flip ANY ONE of
                        // these and every one of its panels goes blank at once.
                        //
                        // Consequences you are accepting:
                        //   * counters are bare: temporal_workflow_completed
                        //   * histograms are integer MILLISECONDS, so every Grafana
                        //     latency panel uses unit `ms`, not `s`
                        //   * sub-millisecond durations round to ZERO and no bucket
                        //     set recovers them. UseSecondsForDuration = true would
                        //     fix that and blank the imported board. You cannot have
                        //     both; this repo chose the board.
                        HasCounterTotalSuffix = false,
                        HasUnitSuffix = false,
                        UseSecondsForDuration = false,
                        HistogramBucketOverrides = HistogramBuckets.ScrapeOverrides,
                    },

                    // Left at the default "temporal_". DO NOT CHANGE IT. Beyond
                    // breaking every vendored dashboard, Core's bucket lookup falls
                    // back to default_buckets_for(strip_prefix("temporal_")) — a
                    // hard-coded literal. A different prefix means that strip fails,
                    // every metric lands in the catch-all bucket arm, and the failure
                    // is invisible because the numbers still look like numbers.
                    MetricPrefix = null,

                    // Default, and load-bearing. Core attaches
                    // service_name="temporal-core-sdk" to every metric, and it is the
                    // ONLY discriminator between this worker's temporal_workflow_completed
                    // on :8077 and the SAME SERIES NAME emitted by the Temporal server's
                    // own embedded Go SDK workers on :8000. In the Go original there was
                    // no collision because tally appended _total; with Core defaults the
                    // names are identical. Every SDK selector on every dashboard pins it.
                    AttachServiceName = true,

                    // Left null on purpose. GlobalTags is honoured on THIS path but
                    // silently DROPPED on the CustomMetricMeter path the starter uses,
                    // so setting it here would make the two processes disagree about
                    // which labels exist. Static labels belong in prometheus.yml's
                    // static_configs, where both paths get them.
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
                "a TemporalRuntime has already been built in this process. There must be exactly one: it owns " +
                "the Prometheus registry and the exporter's TCP listener, so a second one either fails to bind " +
                "or serves an empty registry that nothing writes into.");
        }
    }
}
