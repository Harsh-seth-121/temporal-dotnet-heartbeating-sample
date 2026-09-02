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
/// A process that lives for eight seconds cannot be scraped, so it pushes, and PrometheusOptions
/// and CustomMetricMeter are mutually exclusive: setting both throws from new TemporalRuntime(...).
/// Three warts follow, all in docs/GOTCHAS.md: "The push path cannot use Core's exporter, and
/// double-prefixes", "prometheus-net renders every counter as a gauge", and
/// "MetricsOptions.GlobalTags is silently dropped on the push path".
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

    /// <summary>Wires the chain up and starts pushing on a 5s interval.</summary>
    public static PushMetrics Start(MetricsConfig config, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Named "temporal" so the segment MeterAdapter prepends is a predictable "temporal_".
        // It does not cancel Core's own prefix out; see MetricPrefix below.
        var meter = new Meter("temporal");

        var registry = Metrics.NewCustomRegistry();

        // Process identity, since GlobalTags does not survive this path. Never collide with
        // namespace, task_queue, workflow_type or activity_type: the adapter drops any meter tag
        // whose name matches a static label, silently erasing the real dimension.
        registry.SetStaticLabels(new Dictionary<string, string> { ["role"] = "starter" });

        var factory = Metrics.WithCustomRegistry(registry);

        var adapter = MeterAdapter.StartListening(new MeterAdapterOptions
        {
            Registry = registry,

            // Required despite Registry being set above. MetricFactory defaults to the
            // non-null Metrics.DefaultFactory and wins over Registry, so setting only
            // Registry writes every metric into the default registry and the group is empty.
            MetricFactory = factory,

            // Without this the adapter listens to every Meter in the process.
            InstrumentFilterPredicate = i => i.Meter.Name == "temporal",

            // prometheus-net's default ExponentialBuckets(0.01, 2, 25) is seconds-shaped and Core
            // emits integer milliseconds, so every latency would land in the far tail. The key is
            // Instrument.Name, Core's name with its "temporal_" prefix.
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
                        // Required whenever prometheus-net is listening and workflow code
                        // might touch Workflow.MetricMeter: its managed lease handles are
                        // non-deterministic, and the workflow tracing event listener fails
                        // the task with InvalidWorkflowSchedulerException.
                        disableWorkflowTracingEventListener: true),

                    // Integer milliseconds so the push and scrape paths report the same units.
                    // FloatSeconds here alone would put the starter's temporal_request_latency
                    // 1000x off the worker's on the same dashboard.
                    CustomMetricMeterOptions = new()
                    {
                        HistogramDurationFormat =
                            CustomMetricMeterOptions.DurationFormat.IntegerMilliseconds,
                    },

                    // MetricPrefix = "" would cancel MeterAdapter's prepending out, but measured,
                    // string.Empty reads as unset and falls back to "temporal_". The option works
                    // otherwise ("zz_" yields temporal_zz_request); only "" is unexpressible.
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

            // HTTP PUT rather than POST, so the entire {job, instance} group is replaced. POST
            // replaces only same-named metrics, stranding earlier runs' series forever.
            ReplaceOnPush = true,

            // MetricPusher never throws, so a failed push is reported only here. Without it a
            // 404 from a missing /metrics path is silent.
            OnError = e => log($"pushgateway push failed: {e.Message}"),

            IntervalMilliseconds = 5000,
        });
        pusher.Start();

        return new PushMetrics(config, meter, adapter, pusher, runtime, log);
    }

    /// <summary>Settle, push, then stop listening. The order matters.</summary>
    /// <remarks>Core buffers metric updates, delivers them to the custom meter on its own threads,
    /// and exposes no flush API, so pushing too early drops the starter's final samples with no
    /// error; hence the settle delay. adapter.Dispose() runs after the push because disposing it
    /// removes MeterAdapter's before-collect callback, which drives
    /// MeterListener.RecordObservableInstruments and so makes every ObservableGauge produce a value
    /// at collect time.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (config.PushSettle > TimeSpan.Zero)
        {
            await Task.Delay(config.PushSettle).ConfigureAwait(false);
        }

        // StopAsync cancels the pusher's loop and awaits it; the loop performs exactly one more
        // full push after cancellation. That is prometheus-net's only "push now" guarantee.
        await pusher.StopAsync().ConfigureAwait(false);
        log($"pushgateway: pushed job={config.PushJob} instance={config.PushInstance}");

        adapter.Dispose();
        meter.Dispose();
    }

    /// <summary>Remove the group, so a stale run does not linger on the dashboards.</summary>
    /// <remarks>prometheus-net's MetricPusher has no delete, so this is a raw HTTP DELETE:
    /// <c>curl -X DELETE localhost:9091/metrics/job/temporal_starter/instance/local</c>.</remarks>
    public static async Task<bool> DeleteGroupAsync(MetricsConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        using var http = new HttpClient();
        var url = $"{config.PushgatewayUrl.TrimEnd('/')}/job/{config.PushJob}/instance/{config.PushInstance}";
        var response = await http.DeleteAsync(new Uri(url)).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }
}
