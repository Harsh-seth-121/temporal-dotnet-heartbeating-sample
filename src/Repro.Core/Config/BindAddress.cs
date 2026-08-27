using System.Globalization;
using System.Net;

namespace Repro.Core.Config;

/// <summary>
/// Normalizes and validates Prometheus exporter bind addresses.
/// </summary>
/// <remarks>
/// This class exists to turn two silent 30-minute debugging sessions into startup
/// errors.
/// <para>
/// FIRST: Go's idiomatic <c>":8077"</c> does not work here. Core binds through
/// Rust's <c>SocketAddr::from_str</c>, which rejects a bare <c>:port</c>. The .NET
/// layer only checks the string is non-empty, so the failure surfaces from native
/// code with no indication of which option was wrong. Since the Go config.yaml
/// this repo mirrors used <c>":8077"</c> and its README documents that form, people
/// WILL type it. Accept it and normalize.
/// </para>
/// <para>
/// SECOND: a loopback bind is unreachable from the Prometheus container over
/// host.docker.internal, while <c>curl localhost:8077</c> on the host still
/// succeeds. The target simply reads DOWN with a connection refused, and every SDK
/// panel is blank. Reject 127.0.0.1 and ::1 outright rather than let that happen.
/// </para>
/// </remarks>
public static class BindAddress
{
    /// <summary>Accepts <c>:8077</c>, <c>8077</c>, <c>0.0.0.0:8077</c>; always returns <c>0.0.0.0:8077</c>.</summary>
    /// <param name="value">The configured value.</param>
    /// <param name="origin">Where it came from, for the error message.</param>
    public static string Normalize(string value, string origin)
    {
        var s = (value ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            throw new ArgumentException(
                $"{origin} is empty. PrometheusOptions.BindAddress has no default; set something like \"0.0.0.0:8077\".");
        }

        // Bare port, or Go's ":8077".
        if (s.StartsWith(':'))
        {
            s = "0.0.0.0" + s;
        }
        else if (!s.Contains(':', StringComparison.Ordinal)
                 && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            s = "0.0.0.0:" + s;
        }

        var colon = s.LastIndexOf(':');
        var host = s[..colon];
        var portText = s[(colon + 1)..];

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            throw new ArgumentException($"{origin}: \"{value}\" has no valid port. Expected host:port, e.g. \"0.0.0.0:8077\".");
        }

        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            throw new ArgumentException(
                $"{origin}: \"{value}\" must be an IP:port, not a hostname. Core parses this with Rust's " +
                "SocketAddr and does not resolve names. Use \"0.0.0.0:{port}\".".Replace("{port}", portText, StringComparison.Ordinal));
        }

        if (IPAddress.IsLoopback(ip))
        {
            throw new ArgumentException(
                $"{origin}: \"{value}\" binds loopback only, which the Prometheus container cannot reach over " +
                "host.docker.internal. The target will read DOWN with connection refused while " +
                $"`curl localhost:{portText}` on this host still succeeds, which makes it a genuinely nasty debug. " +
                $"Use \"0.0.0.0:{portText}\".");
        }

        return $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>True when the value means "do not start an exporter at all".</summary>
    /// <remarks>
    /// <c>--metrics off</c> is how you run a second worker on one host without a
    /// port clash, which the "kill the worker mid-activity" recipe needs.
    /// </remarks>
    public static bool IsOff(string? value) =>
        value is not null && value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase);
}
