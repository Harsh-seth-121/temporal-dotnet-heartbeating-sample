using System.Globalization;
using System.Text.RegularExpressions;

namespace Repro.Core.Config;

/// <summary>
/// Parses Go-style duration strings: <c>150ms</c>, <c>10s</c>, <c>1m30s</c>, <c>0</c>.
/// </summary>
/// <remarks>
/// The whole readability of config.yaml rides on this. The alternative is
/// TimeSpan's own format, which YamlDotNet parses natively but which turns
/// <c>latency: 150ms</c> into <c>latency: "00:00:00.150"</c> and
/// <c>heartbeatTimeout: 5s</c> into <c>"00:00:05"</c>. In a file whose entire job
/// is to be dialled up and down while watching Grafana, that is a real cost.
/// <para>
/// It also keeps the port honest: the Go config.yaml this repo mirrors used
/// <c>latency: "150ms"</c>, and the READMEs quote those literals.
/// </para>
/// </remarks>
public static partial class GoDuration
{
    [GeneratedRegex(@"(\d+(?:\.\d+)?)(ns|us|µs|μs|ms|s|m|h)", RegexOptions.CultureInvariant)]
    private static partial Regex Component();

    public static TimeSpan Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.Trim();

        // Go writes the zero duration as a bare "0" with no unit, and so does
        // `temporal workflow describe`. Accept it; requiring "0s" would be a
        // gratuitous divergence.
        if (s is "0" or "-0")
        {
            return TimeSpan.Zero;
        }

        var negative = s.StartsWith('-');
        if (negative || s.StartsWith('+'))
        {
            s = s[1..];
        }

        var matches = Component().Matches(s);
        if (matches.Count == 0)
        {
            throw new FormatException(
                $"invalid duration \"{text}\". Expected a Go-style duration such as 150ms, 10s, 1m30s, or 0.");
        }

        // Reject trailing junk: "10sx" matches "10s" and would otherwise pass.
        var consumed = matches.Sum(m => m.Length);
        if (consumed != s.Length)
        {
            throw new FormatException(
                $"invalid duration \"{text}\": unparsed trailing text. Expected e.g. 150ms, 10s, 1m30s, or 0.");
        }

        var total = TimeSpan.Zero;
        foreach (Match m in matches)
        {
            var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            total += m.Groups[2].Value switch
            {
                "ns" => TimeSpan.FromTicks((long)(value / 100)),   // 1 tick == 100ns
                "us" or "µs" or "μs" => TimeSpan.FromTicks((long)(value * 10)),
                "ms" => TimeSpan.FromMilliseconds(value),
                "s" => TimeSpan.FromSeconds(value),
                "m" => TimeSpan.FromMinutes(value),
                "h" => TimeSpan.FromHours(value),
                var u => throw new FormatException($"unknown duration unit \"{u}\" in \"{text}\""),
            };
        }

        return negative ? -total : total;
    }

    /// <summary>Round-trips back to the Go form, so a rewritten config still reads like the original.</summary>
    public static string ToGoString(TimeSpan d)
    {
        if (d == TimeSpan.Zero)
        {
            return "0s";
        }

        var sign = d < TimeSpan.Zero ? "-" : string.Empty;
        d = d.Duration();

        if (d.TotalMilliseconds < 1000 && d.Ticks % TimeSpan.TicksPerMillisecond == 0)
        {
            return $"{sign}{d.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)}ms";
        }

        var parts = new List<string>();
        if (d.Hours > 0 || d.Days > 0)
        {
            parts.Add($"{(int)d.TotalHours}h");
        }

        if (d.Minutes > 0)
        {
            parts.Add($"{d.Minutes}m");
        }

        var seconds = d.Seconds + (d.Milliseconds / 1000.0);
        if (seconds > 0 || parts.Count == 0)
        {
            parts.Add($"{seconds.ToString(CultureInfo.InvariantCulture)}s");
        }

        return sign + string.Concat(parts);
    }
}
