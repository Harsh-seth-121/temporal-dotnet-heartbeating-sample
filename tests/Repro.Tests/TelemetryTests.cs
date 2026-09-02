using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Repro.Core.Config;
using Repro.Core.Telemetry;
using Xunit;

namespace Repro.Tests;

/// <summary>
/// The two silent failures HistogramBuckets.cs and build-dashboards.py warn about, turned into
/// test failures.
/// </summary>
/// <remarks>Neither is catchable at runtime. A colliding bucket key is resolved by a coin flip at
/// process start, and a mistyped metric name in a dashboard expression produces an empty panel
/// rather than an error.</remarks>
public class HistogramBucketsTests
{
    /// <summary>The workflow task heartbeat timeout this stack ships, in milliseconds.</summary>
    /// <remarks>Duplicated by hand from <c>history.workflowTaskHeartbeatTimeout</c> in
    /// observability/dynamicconfig/development-sql.yaml, which overrides the server's 30m
    /// default. That file is read by the server, not by this process, so a test that parsed it
    /// would be asserting its own parser. The YAML names this constant back.</remarks>
    private const double HeartbeatTimeoutMs = 60_000;

    [Fact]
    public void NoScrapeKeyIsASubstringOfAnother()
    {
        // Core matches PrometheusOptions.HistogramBucketOverrides with metric_name.Contains(key)
        // and iterates the map in nondeterministic order, so one key being a substring of another
        // is resolved by a coin flip at process start. repro_simple_latency against
        // repro_simple_activity_latency is the pair this guards.
        var keys = HistogramBuckets.ScrapeOverrides.Keys.ToList();

        foreach (var key in keys)
        {
            foreach (var other in keys.Where(k => !string.Equals(k, key, StringComparison.Ordinal)))
            {
                Assert.False(
                    other.Contains(key, StringComparison.Ordinal),
                    $"bucket key \"{key}\" is a substring of \"{other}\"; Core's Contains() match " +
                    "would resolve the pair by a coin flip");
            }
        }
    }

    [Theory]
    [InlineData(MetricNames.WorkflowLatency)]
    [InlineData(MetricNames.SimpleLatency)]
    [InlineData(MetricNames.SimpleActivityLatency)]
    [InlineData(MetricNames.HeartbeatStaleness)]
    [InlineData(MetricNames.LocalActivityLatency)]
    // The file-scan pair is furthest from the catch-all: a 100 MB scan runs 4m47s and a 500 MB
    // scan 23m57s, both landing in DefaultMs' +Inf bucket.
    [InlineData(MetricNames.FileScanLatency)]
    [InlineData(MetricNames.FileScanStaleness)]
    public void CustomHistogramsHaveTheirOwnRow(string name) =>
        // A missing row reads as a plausible constant out of Core's catch-all, which tops out
        // at 10s while every one of these runs longer.
        Assert.NotEqual(HistogramBuckets.DefaultMs, HistogramBuckets.ForInstrument(name));

    [Fact]
    public void EveryCustomHistogramRowIsReachableUnderItsOwnName()
    {
        // The reverse of CustomHistogramsHaveTheirOwnRow: a custom metric name is never prefixed,
        // so a repro_ key that falls through to the catch-all is unreachable.
        var reproKeys = HistogramBuckets.ScrapeOverrides.Keys
            .Where(k => k.StartsWith("repro_", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(reproKeys);

        foreach (var key in reproKeys)
        {
            Assert.NotEqual(HistogramBuckets.DefaultMs, HistogramBuckets.ForInstrument(key));
        }

        // No repro_ row may hide behind the temporal_ prefix, which a flipped Custom flag makes.
        Assert.DoesNotContain(
            HistogramBuckets.ScrapeOverrides.Keys,
            k => k.StartsWith("temporal_repro_", StringComparison.Ordinal));

        // And the mirror image, which no other assertion here can see: a non-custom row whose
        // Name already carries "temporal_" is prefixed twice, and temporal_temporal_* matches
        // nothing on either path.
        Assert.DoesNotContain(
            HistogramBuckets.ScrapeOverrides.Keys,
            k => k.StartsWith("temporal_temporal_", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalActivityBucketsResolveBelowTheDurationFloorAndAtTheHeartbeatTimeout()
    {
        // Below the floor: CancellationType defaults to TryCancel so a hand cancel records at
        // ~T+1s, and maximumAttempts is 1 so a throwing activity ends immediately. With no
        // boundary under minDuration those pile into the floor bucket.
        var config = ConfigLoader.Load(ConfigLoader.Resolve(null));
        var floorMs = config.LocalActivity.MinDuration.TotalMilliseconds;

        var buckets = HistogramBuckets.ForInstrument(MetricNames.LocalActivityLatency);

        Assert.Contains(floorMs, buckets);
        Assert.True(
            buckets.Count(b => b < floorMs) >= 3,
            $"repro_local_activity_latency needs boundaries BELOW the {floorMs}ms duration floor: " +
            "a cancelled or immediately-failed run records well under it, and without them " +
            "every such run lands in the floor bucket and p95 pins just below the floor " +
            "forever");

        // At the timeout: it separates runs that complete from runs that are re-executed.
        Assert.Contains(HeartbeatTimeoutMs, buckets);
    }

    [Fact]
    public void FileScanStalenessBucketsResolveTheThrottleShoulderAndTheFastFailures()
    {
        // At the throttle: the 24_000 boundary is Core's heartbeat throttle,
        // min(0.8 x fileScan.heartbeatTimeout, worker.maxHeartbeatThrottleInterval), which
        // staleness cannot beat, so it is where the distribution has an edge. Read from the live
        // config, and the only boundary value any test in this file checks.
        var config = ConfigLoader.Load(ConfigLoader.Resolve(null));
        var throttleMs = Math.Min(
            config.FileScan.HeartbeatTimeout.TotalMilliseconds * 0.8,
            config.Worker.MaxHeartbeatThrottleInterval.TotalMilliseconds);

        var buckets = HistogramBuckets.ForInstrument(MetricNames.FileScanStaleness);

        Assert.Contains(throttleMs, buckets);

        // The throttle is an upper bound on staleness, so with boundaries only at and above it
        // every sample shares one bucket and the panel is a single step.
        Assert.True(
            buckets.Count(b => b < throttleMs) >= 4,
            $"repro_file_scan_staleness needs boundaries BELOW the {throttleMs}ms throttle or " +
            $"every sample shares one bucket; got {string.Join(", ", buckets)}");

        // Above the throttle: samples run to roughly 64s (throttle + the server noticing the
        // heartbeat timeout + retry backoff), so a row stopping at the throttle reads constant.
        Assert.Contains(buckets, b => b >= 64_000);

        // And the millisecond end: a corpus-identity or schema-drift refusal happens before a
        // byte is read, so without sub-second boundaries it shares a bucket with a 24s one.
        Assert.True(
            buckets.Count(b => b < 1000) >= 2,
            $"repro_file_scan_staleness needs sub-second boundaries so a resume that is refused " +
            $"in milliseconds is distinguishable from one at the throttle; got " +
            $"{string.Join(", ", buckets)}");
    }

    [Fact]
    public void SimpleActivityBucketsStraddleTheSleepFloor()
    {
        // simpleActivity.sleepDuration floors every observation, so the signal is the Open-Meteo
        // round trip just above it. A jump straight from 5000 to 10000 cannot resolve that.
        var config = ConfigLoader.Load(ConfigLoader.Resolve(null));
        var floorMs = config.SimpleActivity.SleepDuration.TotalMilliseconds;

        var buckets = HistogramBuckets.ForInstrument(MetricNames.SimpleActivityLatency);

        Assert.Contains(floorMs, buckets);

        // The window is the measured fetch cost, not a multiple of the floor: a
        // (floor, floor x 1.5] window is 2500ms wide and collapses the refused mode (~5.02s) and
        // the live mode (~5.77s) into one bucket.
        Assert.True(
            buckets.Count(b => b > floorMs && b <= floorMs + 1000) >= 3,
            $"need 3+ boundaries in ({floorMs}, {floorMs + 1000}]ms to resolve the fetch cost " +
            $"sitting on top of the sleep; got {string.Join(", ", buckets)}");

        // A refused run lands tens of ms above the floor and a live one 100-700ms above it, so a
        // distant first boundary hides the difference however many boundaries follow.
        var firstAboveFloor = buckets.Where(b => b > floorMs).DefaultIfEmpty(double.MaxValue).Min();
        Assert.True(
            firstAboveFloor <= floorMs + 200,
            $"the first boundary above the {floorMs}ms floor must be within 200ms of it or a " +
            $"refused run shares a bucket with a live one; got {firstAboveFloor}");
    }
}

/// <summary>Every <c>repro_</c> metric a dashboard queries must be a MetricNames constant.</summary>
/// <remarks>The only thing in the repo that turns a dashboard typo into an error. The direction
/// is deliberate: dashboard to constants catches the typo, while the reverse would fail on
/// repro_simple_completed, repro_simple_latency and repro_simple_message, which are emitted on
/// purpose with no panels.</remarks>
public class DashboardMetricNameTests
{
    /// <summary>Prometheus's histogram series suffixes. One copy, because the strip below and
    /// the queried-bare check must use the same list.</summary>
    private static readonly string[] HistogramSuffixes = ["_bucket", "_count", "_sum"];

    [Fact]
    public void EveryReproMetricInADashboardIsAMetricNamesConstant()
    {
        // Reuses ConfigLoader's upward search rather than a second way to find the repo root.
        var root = Path.GetDirectoryName(Path.GetFullPath(ConfigLoader.Resolve(null)))!;
        var boards = Path.Combine(root, "observability", "grafana", "dashboards", "sandbox");

        Assert.True(Directory.Exists(boards), $"dashboard directory not found at {boards}");

        // Reflection over the consts, not a regex over the source: MetricNames' doc comment
        // contains a deliberately misspelled "repro_hearbeat_sent" that a regex would accept.
        var known = typeof(MetricNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string?)f.GetRawConstantValue())
            .Where(v => v is not null && v.StartsWith("repro_", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(known);

        var referenced = new SortedSet<string>(StringComparer.Ordinal);
        var bareHistograms = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(boards, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("panels", out var panels))
            {
                continue;
            }

            foreach (var panel in panels.EnumerateArray())
            {
                if (!panel.TryGetProperty("targets", out var targets))
                {
                    continue;
                }

                foreach (var t in targets.EnumerateArray())
                {
                    if (t.TryGetProperty("expr", out var expr) && expr.GetString() is { } rawExpr)
                    {
                        // Strip quoted label values first: in PromQL a label value is always
                        // double-quoted and a metric name never is. The server sanitizes label
                        // values where the SDK does not, so server-side panels carry
                        // namespace="repro_local_activity", which the pattern below cannot tell
                        // apart from a metric name.
                        var text = Regex.Replace(rawExpr, "\"[^\"]*\"", "\"\"");

                        foreach (Match m in Regex.Matches(text, "repro_[a-z0-9_]+"))
                        {
                            // Prometheus appends _bucket / _count / _sum to a histogram's
                            // series, so the constant is the bare name, but only if the base
                            // really is a histogram. An unconditional strip lets _bucket on a
                            // counter name pass while the panel renders a flat zero line, so the
                            // strip is gated on HistogramBuckets, the repo's register of which
                            // custom metrics are histograms.
                            var raw = m.Value;
                            var name = raw;

                            foreach (var suffix in HistogramSuffixes)
                            {
                                if (!raw.EndsWith(suffix, StringComparison.Ordinal))
                                {
                                    continue;
                                }

                                var bare = raw[..^suffix.Length];

                                // A histogram suffix on a non-histogram: keep the raw token so
                                // the assertion names what it saw.
                                name = HistogramBuckets.ScrapeOverrides.ContainsKey(bare) ? bare : raw;
                                break;
                            }

                            // The mirror image: rate() over a histogram's bare base name selects
                            // nothing, the same flat zero line from the other direction.
                            // string.Equals, not ReferenceEquals, which is equivalent only while
                            // `bare` is strictly shorter than `raw`.
                            if (string.Equals(name, raw, StringComparison.Ordinal)
                                && HistogramBuckets.ScrapeOverrides.ContainsKey(raw)
                                && !HistogramSuffixes.Any(
                                    s => text.Contains(raw + s, StringComparison.Ordinal)))
                            {
                                bareHistograms.Add(raw);
                            }

                            referenced.Add(name);
                        }
                    }
                }
            }
        }

        Assert.NotEmpty(referenced);

        Assert.True(
            bareHistograms.Count == 0,
            "dashboard expressions select a histogram by its bare name, which matches no " +
            $"series and renders a flat zero line: {string.Join(", ", bareHistograms)}");

        var orphans = referenced.Where(n => !known.Contains(n)).ToList();
        Assert.True(
            orphans.Count == 0,
            "dashboard expressions reference repro_ metrics that are not MetricNames constants, " +
            $"so they can only ever render empty: {string.Join(", ", orphans)}");
    }
}
