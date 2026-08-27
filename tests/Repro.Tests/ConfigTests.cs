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

    [Fact]
    public void RecognizesOff() => Assert.True(BindAddress.IsOff("off"));
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
