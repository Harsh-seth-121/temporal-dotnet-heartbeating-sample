using System.Collections.ObjectModel;

namespace Repro.Core.Telemetry;

/// <summary>Histogram bucket overrides, in MILLISECONDS.</summary>
/// <remarks>
/// Without these, six latency panels do not read "no data". They read a
/// plausible CONSTANT, which is the worst failure mode this repo has. Example:
/// loopback gRPC is 0-5ms and Core's default first bucket for request_latency is
/// le=50, so every observation lands in one bucket and histogram_quantile
/// interpolates p95 to a flat ~47ms forever.
/// <para>
/// ONE table, ONE key shape, the PREFIXED name, but TWO match semantics, and a
/// key has to satisfy both at once.
/// </para>
/// <para>
/// SCRAPE (PrometheusOptions.HistogramBucketOverrides). Core matches with
/// metric_name.Contains(key) against the ALREADY PREFIXED name, and iterates the
/// map in NONDETERMINISTIC order. Keys therefore have to carry "temporal_" to stay
/// unambiguous: "temporal_request_latency" is not a substring of
/// "temporal_long_request_latency", and "temporal_activity_execution_latency" is
/// not a substring of "temporal_local_activity_execution_latency". Drop the prefix
/// and BOTH become collisions, resolved by a coin flip at process start.
/// </para>
/// <para>
/// PUSH (prometheus-net MeterAdapterOptions.ResolveHistogramBuckets). Matching is
/// EXACT, on Instrument.Name, which is the name CORE handed the custom meter, so
/// it carries "temporal_" as well. The design here once assumed otherwise: set
/// MetricPrefix = "" and the names would arrive bare. Measured, that is
/// unexpressible. string.Empty reads as UNSET and falls back to Core's default
/// "temporal_" (see PushMetrics). So both paths present the same key, and both
/// lookups below are derived from ONE dictionary on purpose: while they were built
/// separately they silently disagreed, every non-custom push-path entry missed, and
/// the starter's latencies fell through to the catch-all with nothing to show for it.
/// </para>
/// <para>
/// Custom metrics bypass prefixing on both paths and otherwise fall into Core's
/// catch-all. Never use a bare "repro_" as a scrape key. Substring matching would
/// capture every custom histogram at once.
/// </para>
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

        // MANDATORY, not tuning. Without a row here this falls to Core's catch-all
        // [50,100,500,1000,2500,10000] ms, which tops out at 10s while simple.maxDuration
        // ships at 30s. Every `expired` run lands in the +Inf bucket and p95 reads a flat
        // ~9.9s forever. That is the exact failure this file's header describes.
        // The 30_000 boundary is there so `expired` shows up as a visible shoulder.
        ("repro_simple_latency", true,
            [100, 250, 500, 1000, 2500, 5000, 10_000, 20_000, 30_000, 45_000, 60_000, 90_000]),

        // MANDATORY, not tuning, and the boundaries turn on ONE fact: the activity sleeps
        // simpleActivity.sleepDuration (5s shipped) BEFORE it does anything, so 5000ms is
        // a FLOOR, not a middle. Core's catch-all [50,100,500,1000,2500,10000] puts every
        // healthy run in le=10000 and pins p95 at a flat ~9.9s. repro_simple_latency's set
        // above is no better here: its next boundary past 5000 is 10000, and the entire
        // interesting signal, the Open-Meteo round trip sitting on TOP of the sleep, lives
        // between 5000 and 6000.
        //
        // Every boundary below is backed by a MEASURED run at the shipped config. There are
        // four distinct modes, not two, and the middle two are why this row is 13 boundaries
        // rather than 6.
        //
        // 5100/5250/5500/6000 separate the two FAST modes, and they land in different
        // buckets, which is why they are there.
        //   connection REFUSED -> synthetic. Measured 21ms of HTTP, so ~5.02s -> le=5100.
        //   LIVE fetch         -> real reading. Measured 696ms of HTTP, so ~5.77s -> le=6000.
        // Three boundaries apart. Do not describe the source difference as "inside one
        // bucket": the committed fixture alone (HttpElapsedMs 600) already crosses 5100,
        // 5250 and 5500.
        //
        // 7500/10_000 are the BLACKHOLED mode, and they are the boundaries most easily
        // mistaken for dead weight. A route that neither answers nor refuses consumes the
        // whole httpTimeout (3s shipped) before the linked CTS fires, so the run completes
        // synthetic at ~8s in ONE attempt with zero retries. That is exactly the case
        // simpleActivity.httpTimeout exists for, and the one documented at
        // docs/DASHBOARDS.md's stub-baseUrl row. A reader at p95 ~8s who has been told the
        // only thing past 6s is retries will go looking at a flat-zero retry panel.
        //
        // 15_000/30_000/60_000 are for RETRIES, which need a server that ANSWERED (or
        // requireLiveWeather: true). An unreachable endpoint returns synthetic and never
        // retries. Measured: a server answering 200 then stalling its body failed after 3
        // attempts at 27.1s -> le=30_000.
        //
        // 1000/2500/4000 are NOT dead weight either. A cancelled run records the instant the
        // cancel lands, well before the sleep finishes, so outcome="canceled" is the only
        // thing below 5000 and without these it all piles into le=5000. Reachable by hand
        // (`temporal workflow cancel`), not by the loadgen, which sends no cancels.
        ("repro_simple_activity_latency", true,
            [1000, 2500, 4000, 5000, 5100, 5250, 5500, 6000, 7500, 10_000, 15_000, 30_000, 60_000]),

        // LEFT AT CORE DEFAULTS ON PURPOSE, do not add:
        //   workflow_task_execution_latency, workflow_task_replay_latency
        //     -> [1,10,20,50,100,200,500,1000]: 1ms floor, good spread already
        //   workflow_endtoend_latency
        //     -> already spans 100ms..24h
    ];

    /// <summary>Keyed the way BOTH paths present the name: prefixed unless custom.</summary>
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
