using System.Globalization;
using System.Net;

namespace Repro.Core.Config;

/// <summary>Normalizes and validates Prometheus exporter bind addresses.</summary>
/// <remarks>
/// Two failures become startup errors here. Core binds through Rust's <c>SocketAddr::from_str</c>,
/// which rejects Go's idiomatic bare <c>":8077"</c>, and the .NET layer only checks the string is
/// non-empty, so the failure surfaces from native code naming nothing. And a loopback bind is
/// unreachable from the Prometheus container over host.docker.internal while
/// <c>curl localhost:8077</c> on the host still works, so the target reads DOWN with every SDK panel
/// blank. See docs/CONFIG.md, "Metrics addresses".
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

        // Both arms insist the rest is digits: a bare IPv6 also starts with ':', so an unguarded
        // StartsWith would rewrite "::1" into "0.0.0.0::1" and then blame it for being a hostname.
        if (s.StartsWith(':') && IsPort(s[1..]))
        {
            s = "0.0.0.0" + s;
        }
        else if (IsPort(s))
        {
            s = "0.0.0.0:" + s;
        }

        var colon = s.LastIndexOf(':');
        if (colon < 0)
        {
            throw new ArgumentException(
                $"{origin}: \"{value}\" has no port. Expected host:port, e.g. \"0.0.0.0:8077\": Core parses " +
                "this with Rust's SocketAddr, which has no default port. The one non-address value is " +
                "\"off\", honoured for the --metrics flag only (BindAddress.IsOff), never for a config key.");
        }

        var host = s[..colon];
        var portText = s[(colon + 1)..];

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            throw new ArgumentException($"{origin}: \"{value}\" has no valid port. Expected host:port, e.g. \"0.0.0.0:8077\".");
        }

        // "[::]:8077" splits correctly on the last colon and keeps its brackets, which is what Rust's
        // SocketAddr wants. Unbracketed "::1" splits into host ":" and port "1", both nonsense.
        if (host.Contains(':', StringComparison.Ordinal)
            && !(host.StartsWith('[') && host.EndsWith(']')))
        {
            throw new ArgumentException(
                $"{origin}: \"{value}\" looks like an IPv6 address without brackets. Rust's SocketAddr " +
                "requires \"[addr]:port\", e.g. \"[::]:8077\"; without brackets the last colon is read " +
                "as the port separator.");
        }

        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            throw new ArgumentException(
                $"{origin}: \"{value}\" must be an IP:port, not a hostname. Core parses this with Rust's " +
                $"SocketAddr and does not resolve names. Use \"0.0.0.0:{portText}\".");
        }

        if (IPAddress.IsLoopback(ip))
        {
            throw new ArgumentException(
                $"{origin}: \"{value}\" binds loopback only, which the Prometheus container cannot reach over " +
                "host.docker.internal. The target reads DOWN with connection refused while " +
                $"`curl localhost:{portText}` on this host still succeeds. " +
                $"Use \"0.0.0.0:{portText}\".");
        }

        return $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>True when the value means "do not start an exporter at all".</summary>
    /// <remarks><c>--metrics off</c> runs a second worker on one host without a port clash, which docs/HEARTBEATING.md's kill recipe needs.</remarks>
    public static bool IsOff(string? value) =>
        value is not null && value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase);

    /// <summary>Digits only: no sign, no whitespace, no "0x". The range check happens later.</summary>
    private static bool IsPort(string text) =>
        text.Length > 0 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _);
}
