using Repro.Core.Cli;
using Repro.Core.Config;
using Xunit;

namespace Repro.Tests;

/// <summary>
/// These cover the three places where a mistake is SILENT rather than loud. The Go
/// original shipped no tests, and mostly did not need them; these three earn their
/// place because each guards a failure that looks like a working system.
/// </summary>
public class GoDurationTests
{
    [Theory]
    [InlineData("150ms", 0, 0, 0, 150)]
    [InlineData("10s", 0, 0, 10, 0)]
    [InlineData("1m30s", 0, 1, 30, 0)]
    [InlineData("2h", 2, 0, 0, 0)]
    [InlineData("1h2m3s", 1, 2, 3, 0)]
    [InlineData("0", 0, 0, 0, 0)]
    [InlineData("0s", 0, 0, 0, 0)]
    public void Parses(string input, int h, int m, int s, int ms) =>
        Assert.Equal(new TimeSpan(0, h, m, s, ms), GoDuration.Parse(input));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("10")]      // no unit, and not the special "0"
    [InlineData("10sx")]    // trailing junk must not be silently dropped
    [InlineData("10 s")]
    public void RejectsGarbage(string input) =>
        Assert.ThrowsAny<Exception>(() => GoDuration.Parse(input));

    [Theory]
    [InlineData("150ms")]
    [InlineData("10s")]
    [InlineData("1m30s")]
    [InlineData("2h")]
    public void RoundTrips(string input) =>
        Assert.Equal(GoDuration.Parse(input), GoDuration.Parse(GoDuration.ToGoString(GoDuration.Parse(input))));
}

public class BindAddressTests
{
    [Theory]
    [InlineData(":8077")]        // Go's idiom, which Rust's SocketAddr parser rejects
    [InlineData("8077")]
    [InlineData("0.0.0.0:8077")]
    public void NormalizesToAllInterfaces(string input) =>
        Assert.Equal("0.0.0.0:8077", BindAddress.Normalize(input, "test"));

    [Theory]
    [InlineData("127.0.0.1:8077")]
    [InlineData("localhost:8077")]   // Core does not resolve names
    [InlineData("[::1]:8077")]
    public void RejectsUnreachableFromContainer(string input) =>
        Assert.Throws<ArgumentException>(() => BindAddress.Normalize(input, "test"));

    [Theory]
    [InlineData("")]
    [InlineData("0.0.0.0:notaport")]
    [InlineData("0.0.0.0:0")]
    [InlineData("0.0.0.0:99999")]
    public void RejectsMalformed(string input) =>
        Assert.Throws<ArgumentException>(() => BindAddress.Normalize(input, "test"));

    [Theory]
    [InlineData("localhost")]
    [InlineData("off")]          // only the --metrics FLAG understands this; see IsOff
    [InlineData("0.0.0.0")]
    [InlineData("example.com")]
    [InlineData("0x8077")]
    public void RejectsMissingPort(string input) =>
        // These reached s[..-1] and threw a raw ArgumentOutOfRangeException reading
        // "length ('-1') must be a non-negative value", which named neither the option
        // nor the value. ArgumentOutOfRangeException DERIVES from ArgumentException, and
        // Assert.Throws matches the exact type -- so this assertion is what pins the fix.
        Assert.Throws<ArgumentException>(() => BindAddress.Normalize(input, "test"));

    [Fact]
    public void KeepsBracketsOnIpv6() =>
        // Rust's SocketAddr wants the brackets back, so they must survive the round trip.
        Assert.Equal("[::]:8077", BindAddress.Normalize("[::]:8077", "test"));

    [Theory]
    [InlineData("::1")]     // starts with ':' but is NOT Go's ":port" form
    [InlineData("::")]
    [InlineData("[::]")]    // bracketed, but no port
    public void RejectsUnbracketedOrPortlessIpv6(string input) =>
        Assert.Throws<ArgumentException>(() => BindAddress.Normalize(input, "test"));

    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("  off  ")]
    public void RecognizesOff(string input) => Assert.True(BindAddress.IsOff(input));

    [Theory]
    [InlineData("0.0.0.0:8077")]
    [InlineData(null)]
    public void OffIsNotAnAddress(string? input) => Assert.False(BindAddress.IsOff(input));
}

/// <summary>
/// The parser is fifty lines and hand-rolled, which is fine right up until a flag
/// means the OPPOSITE of what it says.
/// </summary>
public class FlagsTests
{
    [Fact]
    public void SwitchIsOnOnlyWhenPresent()
    {
        Assert.True(Flags.Parse(["--restart"]).Switch("--restart"));
        Assert.False(Flags.Parse([]).Switch("--restart"));
    }

    [Theory]
    [InlineData("--restart=false")]
    [InlineData("--restart=0")]
    [InlineData("--restart=true")]
    [InlineData("--delete-push-group=false")]
    [InlineData("--no-cancel-on-interrupt=no")]
    public void SwitchRejectsAnyValue(string arg) =>
        // Go's flag package accepts -restart=false, so people type it. Storing the text
        // and testing ContainsKey turned every one of these ON, silently -- including
        // the ones that spell out "off". A misused flag is a hard error here.
        Assert.Throws<ArgumentException>(() => Flags.Parse([arg]));

    [Fact]
    public void ValueFlagsStillTakeEquals()
    {
        // Only SWITCHES reject '='. --metrics=... must keep working.
        var flags = Flags.Parse(["--metrics=0.0.0.0:8079", "--steps", "7"]);
        Assert.Equal("0.0.0.0:8079", flags.Str("--metrics"));
        Assert.Equal(7, flags.Number("--steps"));
    }

    [Fact]
    public void UnknownFlagIsAHardError() =>
        Assert.Throws<ArgumentException>(() => Flags.Parse(["--concurrancy", "4"]));

    [Fact]
    public void ValueFlagWithNoValueIsAHardError() =>
        Assert.Throws<ArgumentException>(() => Flags.Parse(["--steps"]));

    [Fact]
    public void NoSimpleIsASwitchOnEveryBinary()
    {
        // Known and Switches are GLOBAL to all four exes, so a flag added for the loadgen
        // is silently a hard error in the other three until it is registered here.
        Assert.True(Flags.Parse(["--no-simple"]).Switch("--no-simple"));
        Assert.False(Flags.Parse([]).Switch("--no-simple"));
        Assert.Throws<ArgumentException>(() => Flags.Parse(["--no-simple=false"]));
    }
}

public class ConfigLoaderTests
{
    private static string WriteTemp(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"repro-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    [Fact]
    public void LoadsTheCommittedConfig()
    {
        // The real file, so a bad edit to config.yaml fails the test suite rather
        // than the first `dotnet run`.
        var path = ConfigLoader.Resolve(null);
        var config = ConfigLoader.Load(path);

        Assert.Equal("repro-task-queue", config.TaskQueue);
        Assert.Equal("repro-workflow", config.WorkflowId);
        Assert.Equal("0.0.0.0:8077", config.Metrics.ListenAddress);
        Assert.Equal("0.0.0.0:8078", config.Metrics.LoadgenAddress);
        Assert.Equal(TimeSpan.FromMilliseconds(150), config.Fault.Latency);
        Assert.Equal(TimeSpan.FromSeconds(5), config.Activity.HeartbeatTimeout);
        Assert.EndsWith("/metrics", config.Metrics.PushgatewayUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void OmittedKeysKeepDefaults()
    {
        var path = WriteTemp("address: example:7233\n");
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal("example:7233", config.Address);
            Assert.Equal("repro-task-queue", config.TaskQueue);   // default survived
            Assert.Equal(60, config.Job.Steps);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnknownKeyIsAHardError()
    {
        // The whole "a typo is a crash, not a default" argument rests on
        // DeserializerBuilder NOT having IgnoreUnmatchedProperties.
        var path = WriteTemp("fault:\n  failurRate: 0.4\n");
        try
        {
            Assert.ThrowsAny<Exception>(() => ConfigLoader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EmptyDurationValueIsAHardError()
    {
        // `latency:` with nothing after it is an EMPTY SCALAR, not an absent key: the
        // property IS assigned, so returning TimeSpan.Zero replaced the POCO default and
        // the fault injection quietly stopped adding latency.
        var path = WriteTemp("fault:\n  latency:\n");
        try
        {
            Assert.ThrowsAny<Exception>(() => ConfigLoader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadsTheCommittedSimpleBlock()
    {
        var config = ConfigLoader.Load(ConfigLoader.Resolve(null));

        Assert.True(config.Simple.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(30), config.Simple.MaxDuration);
        Assert.Equal(TimeSpan.FromSeconds(3), config.Simple.Rate);
        Assert.Equal(0.5, config.Simple.Jitter);
        Assert.Equal(8, config.Simple.Concurrency);
        Assert.Equal(5, config.Simple.MaxMessages);
        Assert.True(config.Simple.StopWeight + config.Simple.CancelWeight
            + config.Simple.ExpireWeight > 0);
    }

    [Theory]
    // jitter 1.0 puts the low end of rate x [1-j, 1+j] at zero, and the driver loop spins.
    [InlineData("simple:\n  jitter: 1.0\n")]
    [InlineData("simple:\n  jitter: -0.1\n")]
    // A zero or negative rate is the same spin.
    [InlineData("simple:\n  rate: 0\n")]
    [InlineData("simple:\n  maxDuration: 0\n")]
    [InlineData("simple:\n  concurrency: 0\n")]
    // Random.Shared.Next(min, max + 1) throws when max < min.
    [InlineData("simple:\n  minMessages: 4\n  maxMessages: 2\n")]
    [InlineData("simple:\n  minMessages: -1\n")]
    [InlineData("simple:\n  overflowRate: 1.5\n")]
    [InlineData("simple:\n  raceRate: -0.5\n")]
    // All-zero weights divide by zero in the ending picker.
    [InlineData("simple:\n  stopWeight: 0\n  cancelWeight: 0\n  expireWeight: 0\n")]
    public void RejectsUnusableSimpleConfig(string yaml)
    {
        var path = WriteTemp(yaml);
        try
        {
            Assert.Throws<ArgumentException>(() => ConfigLoader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsHeartbeatTimeoutLongerThanStartToClose()
    {
        // Otherwise the attempt always dies of start-to-close first and no heartbeat
        // timeout is ever observed -- the panel just stays empty.
        var path = WriteTemp("activity:\n  heartbeatTimeout: 30s\n  startToCloseTimeout: 10s\n");
        try
        {
            Assert.Throws<ArgumentException>(() => ConfigLoader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
