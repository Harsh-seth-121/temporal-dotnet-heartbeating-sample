using System.Globalization;
using Repro.Core.Config;

namespace Repro.Core.Cli;

/// <summary>
/// Hand-rolled arg parser. Supports both <c>--flag value</c> and <c>--flag=value</c>.
/// </summary>
/// <remarks>
/// Ten flags do not justify System.CommandLine. Note also that samples-dotnet and
/// omes still pin System.CommandLine 2.0.0-beta4, whose API differs substantially
/// from the 2.0.11 stable release: copying their AddOption/SetHandler code into a
/// net10 repo does not compile. Fifty lines here, zero surprises.
/// <para>
/// Unknown flags are a hard error, for the same reason unknown YAML keys are: a
/// silently ignored <c>--concurrancy</c> is a lie about what the process is doing.
/// </para>
/// </remarks>
public sealed class Flags
{
    /// <summary>Flags that take no value. Everything else consumes the next argv entry.</summary>
    private static readonly HashSet<string> Switches = new(StringComparer.Ordinal)
    {
        "--restart", "--no-cancel-on-interrupt", "--delete-push-group",
    };

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "--config", "--rate", "--concurrency", "--steps", "--step-duration",
        "--history", "--metrics", "--restart", "--no-cancel-on-interrupt", "--delete-push-group",
    };

    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    public static Flags Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var flags = new Flags();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var eq = arg.IndexOf('=', StringComparison.Ordinal);
            var name = eq >= 0 ? arg[..eq] : arg;

            if (!Known.Contains(name))
            {
                throw new ArgumentException(
                    $"unknown flag \"{name}\". Known: {string.Join(", ", Known.Order(StringComparer.Ordinal))}");
            }

            // GOTCHA: Go's flag package accepts -restart=false, so people type it here
            // too. This used to store the text and Switch() only tested ContainsKey, so
            // --restart=false, --restart=0 and --delete-push-group=false all turned the
            // switch ON — the exact opposite of what was typed, with no output. Rejecting
            // every =form beats parsing it: there is then one spelling that means on, and
            // no spelling that quietly means the reverse of what it says.
            if (eq >= 0 && Switches.Contains(name))
            {
                throw new ArgumentException(
                    $"{name} is a switch and takes no value (got \"{arg}\"). Write \"{name}\" to turn it on " +
                    $"and omit it to leave it off — \"{name}=false\" would have turned it ON.");
            }

            if (eq >= 0)
            {
                flags.values[name] = arg[(eq + 1)..];
            }
            else if (Switches.Contains(name))
            {
                flags.values[name] = "true";
            }
            else if (i + 1 < args.Length)
            {
                flags.values[name] = args[++i];
            }
            else
            {
                throw new ArgumentException($"{name} requires a value");
            }
        }

        return flags;
    }

    public string? Str(string name) => values.GetValueOrDefault(name);

    public bool Switch(string name) => values.ContainsKey(name);

    public int? Number(string name) =>
        Str(name) is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : null;

    /// <summary>Go-duration valued flags: <c>--rate 500ms</c>, <c>--step-duration 150ms</c>.</summary>
    public TimeSpan? Duration(string name) => Str(name) is { } s ? GoDuration.Parse(s) : null;
}
