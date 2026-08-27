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

        // Bare port, or Go's ":8077". BOTH arms insist the rest is digits: a bare
        // IPv6 also starts with ':', so an unguarded StartsWith rewrote "::1" into
        // "0.0.0.0::1" and then blamed the result on being a hostname.
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
            // GOTCHA: this used to fall straight into s[..colon] with colon == -1 and
            // throw a raw ArgumentOutOfRangeException reading "length ('-1') must be a
            // non-negative value" — naming neither the option nor the value. Every
            // other failure in this method explains itself; "localhost", "off",
            // "0.0.0.0" and "0x8077" got that instead.
            throw new ArgumentException(
                $"{origin}: \"{value}\" has no port. Expected host:port, e.g. \"0.0.0.0:8077\" — Core parses " +
                "this with Rust's SocketAddr, which has no default port. The one value that is not an " +
                "address is \"off\", and that is honoured for the --metrics FLAG only (BindAddress.IsOff), " +
                "never for a config key.");
        }

        var host = s[..colon];
        var portText = s[(colon + 1)..];

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            throw new ArgumentException($"{origin}: \"{value}\" has no valid port. Expected host:port, e.g. \"0.0.0.0:8077\".");
        }

        // "[::]:8077" splits correctly on the last colon and the brackets survive into
        // the return value, which is what Rust's SocketAddr wants. An UNBRACKETED IPv6
        // does not: "::1" splits into host ":" and port "1", both nonsense. Say which
        // thing is wrong rather than let it fail as a hostname.
        if (host.Contains(':', StringComparison.Ordinal)
            && !(host.StartsWith('[') && host.EndsWith(']')))
        {
            throw new ArgumentException(
                $"{origin}: \"{value}\" looks like an IPv6 address without brackets. Rust's SocketAddr " +
                "requires \"[addr]:port\", e.g. \"[::]:8077\"; without them the last colon is part of " +
                "the address and everything after it is read as the port.");
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

    /// <summary>Digits only — no sign, no whitespace, no "0x". The range check happens later.</summary>
    private static bool IsPort(string text) =>
        text.Length > 0 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _);
}
