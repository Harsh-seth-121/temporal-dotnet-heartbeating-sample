using System.Diagnostics.Metrics;
using Prometheus;
using Repro.Core.Config;
using Repro.Core.Telemetry;
using Temporalio.Extensions.DiagnosticSource;
using Temporalio.Runtime;

namespace Repro.Starter.Telemetry;

/// <summary>
/// The one-shot starter's metrics path: Core -> .NET Meter -> prometheus-net -> Pushgateway.
/// </summary>
/// <remarks>
/// This exists because Core's Prometheus exporter is SCRAPE-ONLY, and because
/// PrometheusOptions and CustomMetricMeter are MUTUALLY EXCLUSIVE — setting both
/// throws from `new TemporalRuntime(...)`. A process that lives for eight seconds
/// cannot be scraped, so it has to push, and pushing means leaving Core's exporter
/// behind entirely.
/// <para>
/// The chain is:
/// Core -> CustomMetricMeter -> System.Diagnostics.Metrics.Meter("temporal")
///      -> prometheus-net MeterAdapter -> CollectorRegistry -> MetricPusher -> Pushgateway.
/// </para>
/// <para>
/// It has three warts, all documented rather than hidden, because documenting them
/// is the point of this repo:
/// </para>
/// <para>
/// 1. MeterAdapter PREPENDS the Meter's name to every metric. Naming the meter
/// "temporal" and setting MetricPrefix = "" makes those cancel out to canonical
/// temporal_* names. Any other combination gives you temporal_temporal_request or
/// my_meter_temporal_request.
/// </para>
/// <para>
/// 2. MeterAdapter renders EVERY counter as a Prometheus gauge, deliberately: a
/// .NET Meter can be re-created at runtime and decrement, which would break a real
/// counter. rate() and increase() still compute correctly; only the `# TYPE` line
/// is wrong. It is also wrong across a worker restart, but a one-shot starter never
/// restarts within a group.
/// </para>
/// <para>
/// 3. MetricsOptions.GlobalTags is silently DROPPED on this path — only the
/// Prometheus and OpenTelemetry exporters honour it. Static labels have to come
/// from CollectorRegistry.SetStaticLabels instead.
/// </para>
/// </remarks>
public sealed class PushMetrics : IAsyncDisposable
{
    private readonly MetricsConfig config;
    private readonly Meter meter;
    private readonly IDisposable adapter;
    private readonly MetricPusher pusher;
    private readonly Action<string> log;

    private PushMetrics(
        MetricsConfig config, Meter meter, IDisposable adapter, MetricPusher pusher,
        TemporalRuntime runtime, Action<string> log)
    {
        this.config = config;
        this.meter = meter;
        this.adapter = adapter;
        this.pusher = pusher;
        this.log = log;
        Runtime = runtime;
    }

    /// <summary>The runtime to hand to <c>ClientFactory.ConnectAsync</c>.</summary>
    public TemporalRuntime Runtime { get; }

    public static PushMetrics Start(MetricsConfig config, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Named "temporal" so that MeterAdapter's prepending, combined with
        // MetricPrefix = "" below, produces exactly temporal_request rather than
        // temporal_temporal_request.
        var meter = new Meter("temporal");

        var registry = Metrics.NewCustomRegistry();

        // GlobalTags does not survive the custom-meter path, so process identity is
        // attached here instead. Never collide with namespace / task_queue /
        // workflow_type / activity_type: the adapter FILTERS OUT any meter tag whose
        // name matches a static label, so a collision silently erases the real
        // dimension.
        registry.SetStaticLabels(new Dictionary<string, string> { ["role"] = "starter" });

        var factory = Metrics.WithCustomRegistry(registry);

        var adapter = MeterAdapter.StartListening(new MeterAdapterOptions
        {
            Registry = registry,

            // NOT OPTIONAL, despite Registry being set above. MetricFactory defaults
            // to the non-null Metrics.DefaultFactory and WINS over Registry, so
            // setting only Registry silently writes every metric into the DEFAULT
            // registry and the pushed group comes back empty.
            MetricFactory = factory,

            // Without this the adapter listens to every Meter in the process,
            // including System.Runtime and System.Net.Http.
            InstrumentFilterPredicate = i => i.Meter.Name == "temporal",

            // prometheus-net's default is ExponentialBuckets(0.01, 2, 25) — seconds-
            // shaped. Core emits integer MILLISECONDS, so every latency would land
            // in the far tail. Note the lookup key here is the UNPREFIXED instrument
            // name, because MetricPrefix is "" on this path; the scrape path keys the
            // same table by the prefixed name.
            ResolveHistogramBuckets = i => HistogramBuckets.ForInstrument(i.Name),
        });

        var runtime = ReproRuntime.Adopt(new TemporalRuntime(new TemporalRuntimeOptions
        {
            Telemetry = new()
            {
                Metrics = new()
                {
                    CustomMetricMeter = new CustomMetricMeter(
                        meter,
                        // REQUIRED whenever prometheus-net is listening and workflow
                        // code might touch Workflow.MetricMeter. prometheus-net's
                        // "managed lease handles" do non-deterministic things, and the
                        // workflow tracing event listener fails the workflow task with
                        // InvalidWorkflowSchedulerException when it sees them. The
                        // starter runs no workflow code, but this wiring gets copied.
                        disableWorkflowTracingEventListener: true),

                    // Leave durations as integer milliseconds so the push path and the
                    // scrape path report the same units. Switching this to FloatSeconds
                    // here alone would make the starter's temporal_request_latency
                    // disagree with the worker's by 1000x on the same dashboard.
                    CustomMetricMeterOptions = new()
                    {
                        HistogramDurationFormat =
                            CustomMetricMeterOptions.DurationFormat.IntegerMilliseconds,
                    },

                    // MEASURED, and it does not work the way you would hope.
                    //
                    // The plan here was MetricPrefix = "" plus Meter("temporal"), so
                    // that MeterAdapter's meter-name prepending would cancel out the
                    // missing Core prefix and produce canonical temporal_request.
                    // It does not: string.Empty is treated as UNSET and falls back to
                    // Core's default "temporal_", so you get temporal_temporal_request.
                    //
                    // The prefix option itself works fine — MetricPrefix = "zz_"
                    // demonstrably yields temporal_zz_request — it is specifically the
                    // empty string that cannot be expressed.
                    //
                    // So the double prefix is unavoidable on this path, and it is
                    // stripped at SCRAPE time instead: see the metric_relabel_configs
                    // on the pushgateway job in observability/prometheus/prometheus.yml.
                    // Leaving it null documents that we are not fighting it here.
                    MetricPrefix = null,

                    AttachServiceName = true,
                },
            },
        }));

        var pusher = new MetricPusher(new MetricPusherOptions
        {
            Endpoint = config.PushgatewayUrl,
            Job = config.PushJob,
            Instance = config.PushInstance,
            Registry = registry,

            // HTTP PUT rather than POST: replaces the ENTIRE {job, instance} group.
            // POST would only replace same-named metrics, stranding series from
            // earlier runs forever. This matches Go's push.PushContext().
            ReplaceOnPush = true,

            // MetricPusher never throws; a failed push is reported only here. Without
            // this, a 404 from a missing /metrics path is completely silent.
            OnError = e => log($"pushgateway push failed: {e.Message}"),

            IntervalMilliseconds = 5000,
        });
        pusher.Start();

        return new PushMetrics(config, meter, adapter, pusher, runtime, log);
    }

    /// <summary>Settle, stop listening, then perform the guaranteed final push.</summary>
    /// <remarks>
    /// Ordering is the whole trick, and it is the C# analogue of the Go original's
    /// `defer flush()` registered BEFORE `defer c.Close()` so that LIFO runs it last.
    /// Here, declaring this object as the FIRST `await using` in Program.cs makes it
    /// dispose LAST — after the Temporal client is gone.
    /// <para>
    /// The settle delay is the genuinely fragile part. Core buffers metric updates
    /// and delivers them to the custom meter on its own threads, and it exposes no
    /// flush API. Push too early and the starter's final temporal_request samples are
    /// simply absent from the group, with no error to tell you.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (config.PushSettle > TimeSpan.Zero)
        {
            await Task.Delay(config.PushSettle).ConfigureAwait(false);
        }

        adapter.Dispose();

        // StopAsync cancels the pusher's loop and awaits it; the loop is written to
        // perform exactly one more full push after cancellation before exiting. That
        // is the only "push now" guarantee prometheus-net offers — there is no
        // one-shot API.
        await pusher.StopAsync().ConfigureAwait(false);
        log($"pushgateway: pushed job={config.PushJob} instance={config.PushInstance}");

        meter.Dispose();
    }

    /// <summary>Remove the group, so a stale run does not linger on the dashboards.</summary>
    /// <remarks>
    /// prometheus-net's MetricPusher has no delete, so this is a raw HTTP DELETE.
    /// Equivalent to:
    /// <c>curl -X DELETE localhost:9091/metrics/job/temporal_starter/instance/local</c>
    /// </remarks>
    public static async Task<bool> DeleteGroupAsync(MetricsConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        using var http = new HttpClient();
        var url = $"{config.PushgatewayUrl.TrimEnd('/')}/job/{config.PushJob}/instance/{config.PushInstance}";
        var response = await http.DeleteAsync(new Uri(url)).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }
}
