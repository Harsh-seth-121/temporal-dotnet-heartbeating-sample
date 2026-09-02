using System.Globalization;
using Repro.Core.Config;

namespace Repro.Core.Cli;

/// <summary>Hand-rolled arg parser, taking <c>--flag value</c> and <c>--flag=value</c>.</summary>
/// <remarks>
/// Nineteen flags do not justify System.CommandLine, and samples-dotnet and omes still pin
/// 2.0.0-beta4, whose AddOption/SetHandler API does not compile against 2.0.11 stable. Unknown
/// flags are a hard error: a silently ignored <c>--concurrancy</c> lies about what the process
/// is doing.
/// </remarks>
public sealed class Flags
{
    /// <summary>Flags that take no value. Everything else consumes the next argv entry.</summary>
    /// <remarks>
    /// <c>--no-simple</c>, <c>--no-simple-activity</c> and <c>--no-local-activity</c> are matched
    /// exactly, never by prefix, so <c>--no-simple</c> does not turn off the simple-activity
    /// loop. Register a switch here only: <see cref="Known"/> is derived from this set plus
    /// <see cref="ValueFlags"/>, so a switch can be neither an unknown-flag error in one binary
    /// nor a value flag that eats the next argv entry.
    /// </remarks>
    private static readonly HashSet<string> Switches = new(StringComparer.Ordinal)
    {
        "--restart", "--no-cancel-on-interrupt", "--delete-push-group", "--no-simple",
        "--no-simple-activity", "--no-local-activity", "--no-file-scan",
        "--file-scan",
    };

    /// <summary>Flags that consume the next argv entry. Register a value flag here only.</summary>
    private static readonly HashSet<string> ValueFlags = new(StringComparer.Ordinal)
    {
        "--config", "--rate", "--concurrency", "--steps", "--step-duration",
        "--history", "--metrics", "--file", "--rows-per-second", "--max-rows",
    };

    /// <summary>Every recognised flag. Derived, never hand-maintained.</summary>
    /// <remarks>
    /// The constructor form, not <c>[.. ValueFlags, .. Switches]</c>: a collection expression
    /// targets the parameterless ctor and drops the Ordinal comparer. Both source sets must stay
    /// above this line, or the first Parse throws TypeInitializationException.
    /// </remarks>
    private static readonly HashSet<string> Known =
        new(ValueFlags.Concat(Switches), StringComparer.Ordinal);

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

            // Go's flag package accepts -restart=false, so people type it here too. Switch()
            // tests ContainsKey, so an accepted --restart=false would turn the switch on.
            // Rejecting every = form leaves no spelling that means the reverse of what it says.
            if (eq >= 0 && Switches.Contains(name))
            {
                throw new ArgumentException(
                    $"{name} is a switch and takes no value (got \"{arg}\"). Write \"{name}\" to turn it on " +
                    $"and omit it to leave it off; \"{name}=false\" would have turned it on.");
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
