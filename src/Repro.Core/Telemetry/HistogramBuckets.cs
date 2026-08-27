using System.Collections.ObjectModel;

namespace Repro.Core.Telemetry;

/// <summary>Histogram bucket overrides, in MILLISECONDS.</summary>
/// <remarks>
/// Without these, five latency panels do not read "no data" — they read a
/// plausible CONSTANT, which is the worst failure mode this repo has. Example:
/// loopback gRPC is 0-5ms and Core's default first bucket for request_latency is
/// le=50, so every observation lands in one bucket and histogram_quantile
/// interpolates p95 to a flat ~47ms forever.
/// <para>
/// ONE table, TWO lookup shapes, because the scrape path and the push path key
/// differently.
/// </para>
/// <para>
/// SCRAPE (PrometheusOptions.HistogramBucketOverrides). Core matches with
/// metric_name.Contains(key) against the ALREADY PREFIXED name, and iterates the
/// map in NONDETERMINISTIC order. Keys therefore carry the "temporal_" prefix:
/// "temporal_request_latency" is not a substring of "temporal_long_request_latency",
/// and "temporal_activity_execution_latency" is not a substring of
/// "temporal_local_activity_execution_latency". Drop the prefix and BOTH become
/// collisions, resolved by a coin flip at process start.
/// </para>
/// <para>
/// PUSH (prometheus-net MeterAdapterOptions.ResolveHistogramBuckets). The starter
/// sets MetricPrefix = "" so that MeterAdapter's meter-name prepending produces
/// canonical temporal_* names. The Instrument.Name the resolver sees is therefore
/// the UNPREFIXED Core name. Same logical metric, different lookup key on the two
/// paths. This is not a typo.
/// </para>
/// <para>
/// Custom metrics bypass prefixing on both paths and otherwise fall into Core's
/// catch-all. Never use a bare "repro_" as a scrape key — substring matching would
/// capture every custom histogram at once.
/// </para>
/// </remarks>
public static class HistogramBuckets
{
    /// <summary>Core's catch-all default, for anything not in the table.</summary>
    public static ReadOnlyCollection<double> DefaultMs { get; } =
        new([50, 100, 500, 1000, 2500, 10_000]);

    /// <remarks><c>Custom</c> means "already unprefixed on both paths".</remarks>
    private static readonly (string Name, bool Custom, double[] Buckets)[] Table =
    [
        // Core default [50,100,500,1000,2500,10000]. Loopback gRPC is 0-5ms.
        ("request_latency", false, [1, 2, 5, 10, 25, 50, 100, 250, 500, 1000, 5000]),

        // Long-polls sit at the poll timeout (~60s) by design; the interesting
        // signal is the shoulder just below it.
        ("long_request_latency", false, [1, 10, 100, 1000, 10_000, 30_000, 60_000, 70_000]),

        // Core default [100,500,1000,5000,10000,100000,1000000]. A healthy sandbox
        // is 1-15ms: every sample lands in le=100 and p99 pins at a flat ~99ms.
        ("workflow_task_schedule_to_start_latency", false,
            [1, 2, 5, 10, 25, 50, 100, 250, 500, 1000, 5000, 30_000]),
        ("activity_schedule_to_start_latency", false,
            [1, 2, 5, 10, 25, 50, 100, 250, 500, 1000, 5000, 30_000]),

        // Core default tops out at 60s. The seed activity is configured
        // (job.steps x job.stepDuration) to run LONGER than that on purpose.
        ("activity_execution_latency", false,
            [10, 50, 100, 250, 500, 1000, 5000, 10_000, 30_000, 60_000, 120_000, 300_000, 600_000]),

        // Unmatched by Core's own latency-bucket table, so it gets the catch-all.
        ("activity_succeed_endtoend_latency", false,
            [10, 100, 1000, 10_000, 30_000, 60_000, 120_000, 300_000, 600_000]),

        ("repro_workflow_latency", true,
            [100, 500, 1000, 5000, 10_000, 30_000, 60_000, 120_000, 300_000, 600_000]),

        // Bounded above by 0.8 x heartbeatTimeout plus retry backoff. The 4s and 8s
        // boundaries are there so the throttle bound shows up as a visible shoulder.
        ("repro_heartbeat_staleness", true,
            [10, 50, 100, 250, 500, 1000, 2000, 4000, 8000, 16_000, 30_000]),

        // LEFT AT CORE DEFAULTS ON PURPOSE, do not add:
        //   workflow_task_execution_latency, workflow_task_replay_latency
        //     -> [1,10,20,50,100,200,500,1000]: 1ms floor, good spread already
        //   workflow_endtoend_latency
        //     -> already spans 100ms..24h
    ];

    private static readonly Dictionary<string, double[]> Lookup =
        Table.ToDictionary(e => e.Name, e => e.Buckets, StringComparer.Ordinal);

    /// <summary>For <c>PrometheusOptions.HistogramBucketOverrides</c>. Prefixed keys.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<double>> ScrapeOverrides { get; } =
        Table.ToDictionary(
            e => e.Custom ? e.Name : "temporal_" + e.Name,
            e => (IReadOnlyCollection<double>)e.Buckets,
            StringComparer.Ordinal);

    /// <summary>For prometheus-net's <c>ResolveHistogramBuckets</c>. Unprefixed instrument names.</summary>
    public static double[] ForInstrument(string instrumentName) =>
        Lookup.TryGetValue(instrumentName, out var buckets) ? buckets : [.. DefaultMs];
}
