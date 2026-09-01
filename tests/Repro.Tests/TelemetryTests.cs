using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Repro.Core.Config;
using Repro.Core.Telemetry;
using Xunit;

namespace Repro.Tests;

/// <summary>
/// The two silent failures HistogramBuckets.cs and build-dashboards.py spend paragraphs
/// warning about, turned into test failures.
/// </summary>
/// <remarks>
/// Neither of these can be caught at runtime. A colliding bucket key is resolved by a coin
/// flip at process start; a mistyped metric name in a dashboard expression produces an
/// empty panel and never an error. Both look exactly like a working system.
/// </remarks>
public class HistogramBucketsTests
{
    /// <summary>
    /// The workflow task heartbeat timeout this stack ships, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>history.workflowTaskHeartbeatTimeout</c> in
    /// observability/dynamicconfig/development-sql.yaml, which overrides the server's own 30m
    /// default. It is duplicated here rather than parsed out of that YAML because the file is
    /// read by the SERVER, not by this process, and a test that parsed it would be asserting
    /// its own parser. The dynamicconfig file names this constant in a comment so the pair
    /// stays visible from both sides.
    /// </remarks>
    private const double HeartbeatTimeoutMs = 60_000;

    [Fact]
    public void NoScrapeKeyIsASubstringOfAnother()
    {
        // Core matches PrometheusOptions.HistogramBucketOverrides with
        // metric_name.Contains(key) and iterates the map in NONDETERMINISTIC order, so one
        // key being a substring of another is resolved by a coin flip at process start.
        // repro_simple_latency against repro_simple_activity_latency is exactly the pair
        // this guards, and the next name added here may not be so lucky.
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
    public void CustomHistogramsHaveTheirOwnRow(string name) =>
        // A missing row does not read "no data", it reads a plausible CONSTANT out of
        // Core's catch-all, which tops out at 10s while all four of these run longer.
        //
        // HeartbeatStaleness was the missing fourth for one commit, and the gap was not
        // theoretical: MEASURED, flipping its row's Custom flag to false left the whole
        // suite green while the key became temporal_repro_heartbeat_staleness, which the
        // emitted name can never Contains-match on the scrape path and which ForInstrument
        // misses on the push path. The metric silently fell to the 10s-capped default against
        // a row documented to need 30s boundaries.
        Assert.NotEqual(HistogramBuckets.DefaultMs, HistogramBuckets.ForInstrument(name));

    [Fact]
    public void EveryCustomHistogramRowIsReachableUnderItsOwnName()
    {
        // The reverse direction of CustomHistogramsHaveTheirOwnRow, and the one that catches
        // a flipped Custom flag rather than a missing row. Every repro_ key in the table must
        // resolve to ITSELF: a custom metric name is never prefixed, so if a lookup by the
        // literal name falls through to the catch-all, the key in the table is unreachable
        // and the row is decoration.
        var reproKeys = HistogramBuckets.ScrapeOverrides.Keys
            .Where(k => k.StartsWith("repro_", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(reproKeys);

        foreach (var key in reproKeys)
        {
            Assert.NotEqual(HistogramBuckets.DefaultMs, HistogramBuckets.ForInstrument(key));
        }

        // And no repro_ row may hide behind the temporal_ prefix, which is what a flipped
        // Custom flag produces.
        Assert.DoesNotContain(
            HistogramBuckets.ScrapeOverrides.Keys,
            k => k.StartsWith("temporal_repro_", StringComparison.Ordinal));

        // And the mirror image, which is the one that got through. A NON-custom row whose
        // Name already carries the "temporal_" prefix is prefixed a second time, producing
        // temporal_temporal_*. That key matches nothing on either path, so the metric falls
        // silently to Core's catch-all.
        //
        // MEASURED before this assertion existed: the row for local_activity_execution_latency
        // was written as "temporal_local_activity_execution_latency" and the entire suite
        // stayed green. NoScrapeKeyIsASubstringOfAnother could not see it, because a
        // double-prefixed key collides with nothing; the loop above could not see it, because
        // it only inspects repro_ keys. The only symptom was a live scrape returning
        // DefaultMs, which looks like a working panel.
        Assert.DoesNotContain(
            HistogramBuckets.ScrapeOverrides.Keys,
            k => k.StartsWith("temporal_temporal_", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalActivityBucketsResolveBelowTheDurationFloorAndAtTheHeartbeatTimeout()
    {
        // TWO separate failures, one at each end of the row, and neither reads "no data".
        //
        // BELOW THE FLOOR. localActivity.minDuration floors every run that gets to record a
        // sample at all, but not every run does so at its full length: CancellationType
        // defaults to TryCancel, so a hand `temporal workflow cancel` records at ~T+1s, and
        // maximumAttempts is 1 so a throwing activity ends the run immediately. With no
        // boundary under the floor those pile into the floor bucket and p95 for them reads a
        // plausible constant just under it. This is the same trap
        // SimpleActivityBucketsStraddleTheSleepFloor guards from the other direction.
        var config = ConfigLoader.Load(ConfigLoader.Resolve(null));
        var floorMs = config.LocalActivity.MinDuration.TotalMilliseconds;

        var buckets = HistogramBuckets.ForInstrument(MetricNames.LocalActivityLatency);

        Assert.Contains(floorMs, buckets);
        Assert.True(
            buckets.Count(b => b < floorMs) >= 3,
            $"repro_local_activity_latency needs boundaries BELOW the {floorMs}ms duration floor: "
            + "a cancelled or immediately-failed run records well under it, and without them "
            + "every such run lands in the floor bucket and p95 pins just below the floor "
            + "forever");

        // AT THE TIMEOUT. The workflow task heartbeat timeout is what separates the runs that
        // complete from the runs that are re-executed, so the boundary has to exist for the
        // split to be resolvable at all. It is asserted against the dynamic-config value this
        // repo actually ships rather than against a literal, so lowering one and not the other
        // is a test failure instead of a silently unreadable panel.
        Assert.Contains(HeartbeatTimeoutMs, buckets);
    }

    [Fact]
    public void SimpleActivityBucketsStraddleTheSleepFloor()
    {
        // simpleActivity.sleepDuration floors every observation, so the interesting signal
        // is the Open-Meteo round trip sitting just above it. Boundaries only at and below
        // 5000, or a jump straight from 5000 to 10000, cannot resolve that at all.
        var config = ConfigLoader.Load(ConfigLoader.Resolve(null));
        var floorMs = config.SimpleActivity.SleepDuration.TotalMilliseconds;

        var buckets = HistogramBuckets.ForInstrument(MetricNames.SimpleActivityLatency);

        Assert.Contains(floorMs, buckets);

        // The window is the DOCUMENTED fetch cost, not a loose multiple of the floor. A
        // (floor, floor x 1.5] window was 2500ms wide and MEASURED useless: deleting
        // 5100/5250/5500 kept the whole suite green while collapsing the refused mode
        // (~5.02s) and the live mode (~5.77s) into one bucket, which is exactly what the row
        // exists to prevent.
        Assert.True(
            buckets.Count(b => b > floorMs && b <= floorMs + 1000) >= 3,
            $"need 3+ boundaries in ({floorMs}, {floorMs + 1000}]ms to resolve the fetch cost " +
            $"sitting on top of the sleep; got {string.Join(", ", buckets)}");

        // The assertion that actually encodes the invariant. A refused run lands a few tens of
        // ms above the floor and a live one 100-700ms above it; if the FIRST boundary past the
        // floor is far away, both share a bucket and the source difference is invisible no
        // matter how many boundaries follow.
        var firstAboveFloor = buckets.Where(b => b > floorMs).DefaultIfEmpty(double.MaxValue).Min();
        Assert.True(
            firstAboveFloor <= floorMs + 200,
            $"the first boundary above the {floorMs}ms floor must be within 200ms of it or a " +
            $"refused run shares a bucket with a live one; got {firstAboveFloor}");
    }
}

/// <summary>Every <c>repro_</c> metric a dashboard queries must be a MetricNames constant.</summary>
/// <remarks>
/// build-dashboards.py's own header says a typo there "produces a silently empty panel,
/// never an error". This is the only thing in the repo that makes it an error.
/// <para>
/// DIRECTION MATTERS. Dashboard -> constants catches the typo. The reverse would fail
/// today on repro_simple_completed, repro_simple_latency and repro_simple_message, which
/// are emitted but have no panels. That is deliberate, so it is not a bug to assert against.
/// </para>
/// </remarks>
public class DashboardMetricNameTests
{
    /// <summary>
    /// Prometheus's histogram series suffixes. ONE copy: the strip below and the
    /// queried-bare check need the SAME list, and a suffix in one but not the other is a hole
    /// in the dimension this class advertises.
    /// </summary>
    private static readonly string[] HistogramSuffixes = ["_bucket", "_count", "_sum"];

    [Fact]
    public void EveryReproMetricInADashboardIsAMetricNamesConstant()
    {
        // Reuses ConfigLoader's upward search for config.yaml rather than adding a second
        // way to find the repo root.
        var root = Path.GetDirectoryName(Path.GetFullPath(ConfigLoader.Resolve(null)))!;
        var boards = Path.Combine(root, "observability", "grafana", "dashboards", "sandbox");

        Assert.True(Directory.Exists(boards), $"dashboard directory not found at {boards}");

        // Reflection over the consts, NOT a regex over the source file: the class doc
        // comment contains a deliberately misspelled "repro_hearbeat_sent" as an example,
        // and a regex would happily accept it as a valid name.
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
                    if (t.TryGetProperty("expr", out var expr) && expr.GetString() is { } raw_)
                    {
                        // STRIP QUOTED LABEL VALUES FIRST. In PromQL a label value is always
                        // double-quoted and a metric name never is, so everything inside
                        // quotes is by definition not a metric name.
                        //
                        // This is not hygiene. The local-activity case runs in a namespace
                        // called repro-local-activity, and the SERVER sanitizes label values
                        // while the SDK does not, so its server-side panels carry
                        // namespace="repro_local_activity" -- which the pattern below cannot
                        // tell apart from a metric name. Without this line the test fails on
                        // a namespace, names it as an unknown metric, and the obvious "fix"
                        // is to add a bogus constant to MetricNames.
                        var text = Regex.Replace(raw_, "\"[^\"]*\"", "\"\"");

                        foreach (Match m in Regex.Matches(text, "repro_[a-z0-9_]+"))
                        {
                            // Prometheus appends _bucket / _count / _sum to a HISTOGRAM's
                            // series names, so the constant is the bare name, but only if the
                            // base really is a histogram.
                            //
                            // The strip used to be unconditional, and MEASURED that left a
                            // hole in exactly the dimension this test advertises: pasting
                            // _bucket onto a COUNTER name kept the suite green, while the
                            // panel rendered a flat zero line forever because
                            // repro_simple_activity_completed_bucket does not exist. Gate it
                            // on HistogramBuckets, which is the repo's own register of which
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

                                // A histogram suffix on something that is not a histogram:
                                // keep the RAW token so the assertion names what it saw.
                                name = HistogramBuckets.ScrapeOverrides.ContainsKey(bare) ? bare : raw;
                                break;
                            }

                            // The mirror image: a histogram queried BARE. rate() over a
                            // histogram's base name selects nothing, which is the same flat
                            // zero line arrived at from the other direction.
                            // string.Equals, not ReferenceEquals: equivalent today only
                            // because `bare` is strictly shorter than `raw`, and the day the
                            // strip above returns a copy the reference test stops firing
                            // SILENTLY.
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
            "dashboard expressions select a histogram by its bare name, which matches no "
            + $"series and renders a flat zero line: {string.Join(", ", bareHistograms)}");

        var orphans = referenced.Where(n => !known.Contains(n)).ToList();
        Assert.True(
            orphans.Count == 0,
            "dashboard expressions reference repro_ metrics that are not MetricNames constants, " +
            $"so they can only ever render empty: {string.Join(", ", orphans)}");
    }
}
