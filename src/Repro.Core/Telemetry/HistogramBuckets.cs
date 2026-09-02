using System.Collections.ObjectModel;

namespace Repro.Core.Telemetry;

/// <summary>Histogram bucket overrides, in milliseconds.</summary>
/// <remarks>
/// Why these exist at all: docs/GOTCHAS.md, "Default histogram buckets produce a plausible
/// constant, not "no data"". One table, one key shape (the prefixed name), two match semantics.
/// Scrape (PrometheusOptions.HistogramBucketOverrides) matches metric_name.Contains(key) against
/// the already-prefixed name in nondeterministic order, so keys must carry "temporal_": bare,
/// request_latency collides with long_request_latency, and activity_execution_latency with
/// local_activity_execution_latency. Push (MeterAdapterOptions.ResolveHistogramBuckets) matches
/// Instrument.Name exactly, which Core prefixes too, because MetricPrefix = "" reads as unset (see
/// PushMetrics). Both lookups derive from one dictionary; built separately they drifted. Custom
/// metrics skip prefixing on both paths. Never use a bare "repro_" scrape key.
/// </remarks>
public static class HistogramBuckets
{
    /// <summary>Core's default prefix. ReproRuntime documents why it is never changed.</summary>
    private const string CorePrefix = "temporal_";

    /// <summary>Core's catch-all default, for anything not in the table.</summary>
    public static ReadOnlyCollection<double> DefaultMs { get; } =
        new([50, 100, 500, 1000, 2500, 10_000]);

    /// <remarks><c>Custom</c> means MetricPrefix never applies, so the name is already final.</remarks>
    private static readonly (string Name, bool Custom, double[] Buckets)[] Table =
    [
        // Core default [50,100,500,1000,2500,10000]. Loopback gRPC is 0-5ms.
        ("request_latency", false, [1, 2, 5, 10, 25, 50, 100, 250, 500, 1000, 5000]),

        // Long-polls sit at the poll timeout (~60s) by design; the signal is the shoulder below it.
        ("long_request_latency", false, [1, 10, 100, 1000, 10_000, 30_000, 60_000, 70_000]),

        // Core default [100,500,1000,5000,10000,100000,1000000]; a healthy sandbox is 1-15ms.
        ("workflow_task_schedule_to_start_latency", false,
            [1, 2, 5, 10, 25, 50, 100, 250, 500, 1000, 5000, 30_000]),
        ("activity_schedule_to_start_latency", false,
            [1, 2, 5, 10, 25, 50, 100, 250, 500, 1000, 5000, 30_000]),

        // Core's default tops out at 60s; the seed activity runs longer on purpose. Extended past
        // 600_000 because at fileScan.targetRowsPerSecond 6000 the 350 and 500 MB corpora scan for
        // 16m46s and 23m57s.
        ("activity_execution_latency", false,
            [10, 50, 100, 250, 500, 1000, 5000, 10_000, 30_000, 60_000, 120_000, 300_000,
             600_000, 1_200_000, 1_800_000]),

        // Unmatched by Core's own latency-bucket table, so it gets the catch-all.
        ("activity_succeed_endtoend_latency", false,
            [10, 100, 1000, 10_000, 30_000, 60_000, 120_000, 300_000, 600_000]),

        ("repro_workflow_latency", true,
            [100, 500, 1000, 5000, 10_000, 30_000, 60_000, 120_000, 300_000, 600_000]),

        // Bounded above by 0.8 x heartbeatTimeout plus retry backoff; 4000 and 8000 make the
        // throttle bound a visible shoulder.
        ("repro_heartbeat_staleness", true,
            [10, 50, 100, 250, 500, 1000, 2000, 4000, 8000, 16_000, 30_000]),

        // Required. Core's catch-all tops out at 10s while simple.maxDuration ships at 30s, so
        // every `expired` run would land in +Inf; 30_000 makes `expired` a visible shoulder.
        ("repro_simple_latency", true,
            [100, 250, 500, 1000, 2500, 5000, 10_000, 20_000, 30_000, 45_000, 60_000, 90_000]),

        // Required. The activity sleeps simpleActivity.sleepDuration (5s shipped) first, so 5000
        // is a floor and the signal lives between 5000 and 6000. Four measured modes at the shipped
        // config: a refused connection is synthetic at ~5.02s; a live fetch is ~5.77s; a blackholed
        // route burns the whole httpTimeout (3s) before the linked CTS fires and completes
        // synthetic at ~8s with zero retries (docs/DASHBOARDS.md, the stub-baseUrl row); and
        // retries need a server that answered, one of which failed after 3 attempts at 27.1s.
        // Below 5000 is outcome="canceled", which only a hand cancel produces.
        ("repro_simple_activity_latency", true,
            [1000, 2500, 4000, 5000, 5100, 5250, 5500, 6000, 7500, 10_000, 15_000, 30_000, 60_000]),

        // Required, asymmetric at both ends. localActivity.minDuration is 30s, so 30_000 is a
        // floor and the boundaries under it catch runs that end early (TryCancel, maximumAttempts
        // 1). Above 60_000 matters because this times the workflow, not the burn, and a workflow
        // also waits for one of localActivity.maxConcurrentLocalActivities slots, each held up to
        // a minute. Measured over one demo run: <=5s, (30s, 40s], (45s, 50s], (55s, 60s] and
        // (60s, 90s]. The tail runs to runTimeout, not the heartbeat timeout.
        ("repro_local_activity_latency", true,
            [1000, 5000, 10_000, 20_000, 30_000, 40_000, 45_000, 50_000, 55_000, 60_000,
             90_000, 120_000, 180_000, 300_000]),

        // Times one execution of the burn, where repro_local_activity_latency times the whole
        // workflow; on a re-executed run the SDK records several of these and the workflow none.
        // Unprefixed name, because Custom=false prepends CorePrefix. The prefixed spelling
        // produced temporal_temporal_local_activity_execution_latency, which matched nothing on
        // either path and silently served DefaultMs.
        ("local_activity_execution_latency", false,
            [1000, 5000, 10_000, 30_000, 60_000, 90_000, 120_000, 180_000, 300_000]),

        // Left at Core defaults on purpose, do not add:
        //   workflow_task_execution_latency, workflow_task_replay_latency
        //     -> [1,10,20,50,100,200,500,1000]: 1ms floor, good spread already
        //   workflow_endtoend_latency -> already spans 100ms to 24h

        // The sub-60s boundaries catch a corpus-identity mismatch, which fails in milliseconds.
        // 300_000 straddles the shipped 100 MB scan; 900_000 and 1_800_000 cover 350 and 500 MB.
        ("repro_file_scan_latency", true,
            [1000, 5000, 10_000, 30_000, 60_000, 120_000, 300_000, 600_000, 900_000,
             1_800_000, 3_600_000]),

        // 24_000 is 0.8 x fileScan.heartbeatTimeout, Core's throttle, so the bound a checkpoint's
        // staleness cannot beat is a visible shoulder. Samples run past it to roughly 64s:
        // throttle plus the server noticing plus retry backoff.
        ("repro_file_scan_staleness", true,
            [100, 500, 1000, 5000, 10_000, 16_000, 20_000, 24_000, 30_000, 45_000, 60_000,
             90_000]),

    ];

    /// <summary>Keyed the way both paths present the name: prefixed unless custom.</summary>
    private static readonly Dictionary<string, double[]> Lookup =
        Table.ToDictionary(
            e => e.Custom ? e.Name : CorePrefix + e.Name,
            e => e.Buckets,
            StringComparer.Ordinal);

    /// <summary>For <c>PrometheusOptions.HistogramBucketOverrides</c>. Substring-matched by Core.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<double>> ScrapeOverrides { get; } =
        Lookup.ToDictionary(
            e => e.Key,
            e => (IReadOnlyCollection<double>)e.Value,
            StringComparer.Ordinal);

    /// <summary>
    /// For prometheus-net's <c>ResolveHistogramBuckets</c>. Exact match against
    /// <c>Instrument.Name</c>, which arrives temporal_-prefixed on the push path.
    /// </summary>
    public static double[] ForInstrument(string instrumentName) =>
        Lookup.TryGetValue(instrumentName, out var buckets) ? buckets : [.. DefaultMs];
}
