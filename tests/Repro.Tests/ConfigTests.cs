using Repro.Core.Cli;
using Repro.Core.Config;
using Xunit;

namespace Repro.Tests;

/// <summary>
/// These cover the places where a mistake is SILENT rather than loud: Go-duration parsing,
/// bind-address normalization, flag parsing, and config load plus startup validation. The Go
/// original shipped no tests, and mostly did not need them; every class here earns its place
/// because each guards a failure that looks like a working system.
/// <para>
/// The telemetry equivalents live in TelemetryTests.cs, and the one branch that can turn a
/// failure into a green run lives in WeatherActivitiesTests.cs.
/// </para>
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
        // Assert.Throws matches the exact type, so this assertion is what pins the fix.
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
        // and testing ContainsKey turned every one of these ON, silently, including the
        // ones that spell out "off". A misused flag is a hard error here.
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
        // The flag sets are static and therefore GLOBAL to all four exes, so a flag the
        // loadgen wants but nobody registered in Switches is an unknown-flag hard error in
        // every binary, loadgen included.
        Assert.True(Flags.Parse(["--no-simple"]).Switch("--no-simple"));
        Assert.False(Flags.Parse([]).Switch("--no-simple"));
        Assert.Throws<ArgumentException>(() => Flags.Parse(["--no-simple=false"]));
    }

    [Fact]
    public void NoSimpleActivityIsASwitchOnEveryBinary()
    {
        Assert.True(Flags.Parse(["--no-simple-activity"]).Switch("--no-simple-activity"));
        Assert.False(Flags.Parse([]).Switch("--no-simple-activity"));
        Assert.Throws<ArgumentException>(() => Flags.Parse(["--no-simple-activity=false"]));

        // Known and Switches are matched EXACTLY, not by prefix, and --no-simple is a
        // string prefix of --no-simple-activity. If Flags ever grows prefix matching,
        // these two are the assertions that catch "I turned off the wrong driver and the
        // logs looked fine".
        Assert.False(Flags.Parse(["--no-simple"]).Switch("--no-simple-activity"));
        Assert.False(Flags.Parse(["--no-simple-activity"]).Switch("--no-simple"));
    }

    [Fact]
    public void NoLocalActivityIsASwitchOnEveryBinary()
    {
        Assert.True(Flags.Parse(["--no-local-activity"]).Switch("--no-local-activity"));
        Assert.False(Flags.Parse([]).Switch("--no-local-activity"));
        Assert.Throws<ArgumentException>(() => Flags.Parse(["--no-local-activity=false"]));

        // The near-homograph, and the reason this assertion is here rather than assumed.
        // --no-local-activity and --no-simple-activity differ by one word in the middle and
        // turn off different loops; neither is a prefix of the other, so nothing but exact
        // matching keeps them apart. Somebody will type the wrong one.
        Assert.False(Flags.Parse(["--no-simple-activity"]).Switch("--no-local-activity"));
        Assert.False(Flags.Parse(["--no-local-activity"]).Switch("--no-simple-activity"));
        Assert.False(Flags.Parse(["--no-local-activity"]).Switch("--no-simple"));
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

    /// <summary>Write <paramref name="yaml"/>, run <paramref name="body"/>, always delete.</summary>
    /// <remarks>
    /// Every temp-config test wants exactly this, and hand-writing the try/finally six times
    /// is six chances to leave a file in TMPDIR on an assertion failure.
    /// </remarks>
    private static void WithTempConfig(string yaml, Action<string> body)
    {
        var path = WriteTemp(yaml);
        try
        {
            body(path);
        }
        finally
        {
            File.Delete(path);
        }
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
        WithTempConfig("address: example:7233\n", path =>
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal("example:7233", config.Address);
            Assert.Equal("repro-task-queue", config.TaskQueue);   // default survived
            Assert.Equal(60, config.Job.Steps);
        });
    }

    [Fact]
    public void UnknownKeyIsAHardError()
    {
        // The whole "a typo is a crash, not a default" argument rests on
        // DeserializerBuilder NOT having IgnoreUnmatchedProperties.
        WithTempConfig(
            "fault:\n  failurRate: 0.4\n",
            path => Assert.ThrowsAny<Exception>(() => ConfigLoader.Load(path)));
    }

    [Fact]
    public void EmptyDurationValueIsAHardError()
    {
        // `latency:` with nothing after it is an EMPTY SCALAR, not an absent key: the
        // property IS assigned, so returning TimeSpan.Zero replaced the POCO default and
        // the fault injection quietly stopped adding latency.
        WithTempConfig(
            "fault:\n  latency:\n",
            path => Assert.ThrowsAny<Exception>(() => ConfigLoader.Load(path)));
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
    // Random.Shared.Next(gapMs + 1) throws on a negative bound.
    [InlineData("simple:\n  messageGap: -1s\n")]
    [InlineData("simple:\n  overflowRate: 1.5\n")]
    [InlineData("simple:\n  raceRate: -0.5\n")]
    // All-zero weights divide by zero in the ending picker.
    [InlineData("simple:\n  stopWeight: 0\n  cancelWeight: 0\n  expireWeight: 0\n")]
    public void RejectsUnusableSimpleConfig(string yaml) =>
        WithTempConfig(yaml, path => Assert.Throws<ArgumentException>(() => ConfigLoader.Load(path)));

    [Fact]
    public void RejectsHeartbeatTimeoutLongerThanStartToClose()
    {
        // Otherwise the attempt always dies of start-to-close first and no heartbeat
        // timeout is ever observed. The panel just stays empty.
        WithTempConfig(
            "activity:\n  heartbeatTimeout: 30s\n  startToCloseTimeout: 10s\n",
            path => Assert.Throws<ArgumentException>(() => ConfigLoader.Load(path)));
    }

    [Fact]
    public void LoadsTheCommittedSimpleActivityBlock()
    {
        var config = ConfigLoader.Load(ConfigLoader.Resolve(null));
        var sa = config.SimpleActivity;

        Assert.True(sa.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(5), sa.SleepDuration);
        Assert.Equal(TimeSpan.FromSeconds(30), sa.StartToCloseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), sa.HttpTimeout);
        Assert.Equal(3, sa.Retry.MaximumAttempts);
        Assert.Equal(47.6062, sa.Latitude);
        Assert.Equal(-122.3321, sa.Longitude);
        Assert.StartsWith("https://", sa.BaseUrl, StringComparison.Ordinal);
        Assert.False(sa.RequireLiveWeather);
        Assert.Equal(TimeSpan.FromSeconds(15), sa.Rate);
        Assert.Equal(0.5, sa.Jitter);
        Assert.Equal(4, sa.Concurrency);
    }

    [Fact]
    public void SimpleActivityWorstCaseRunFitsTheDrainBudget()
    {
        // demo-down.sh derives DRAIN_TIMEOUT = worker.gracefulShutdownTimeout + 15. A run
        // that can outlive it gets SIGKILLed mid-flight instead of drained, and the only
        // symptom is a teardown that looks slow. This is the constraint the simple.maxDuration
        // comment asserts in prose and nothing checked until now.
        var config = ConfigLoader.Load(ConfigLoader.Resolve(null));
        var sa = config.SimpleActivity;

        // The attempts themselves.
        var attempts = sa.Retry.MaximumAttempts * (sa.SleepDuration + sa.HttpTimeout);

        // Plus the gaps BETWEEN them, which the previous version of this test omitted and was
        // therefore blind to. ValidateSimpleActivity bounds the retry intervals only from
        // below, so raising initialInterval or maximumInterval pushes the real worst case past
        // the budget. Without this term the tripwire reported 24s and passed anyway.
        var backoff = TimeSpan.Zero;
        for (var i = 0; i < sa.Retry.MaximumAttempts - 1; i++)
        {
            var interval = sa.Retry.InitialInterval * Math.Pow(sa.Retry.BackoffCoefficient, i);
            backoff += interval < sa.Retry.MaximumInterval ? interval : sa.Retry.MaximumInterval;
        }

        var worstCase = attempts + backoff;
        var budget = config.Worker.GracefulShutdownTimeout + TimeSpan.FromSeconds(15);

        // At the shipped config this is 3 x (5s + 3s) + (1s + 2s) = 27s against a 45s budget,
        // which is the figure config.yaml states. MEASURED: a server that answered and
        // then stalled its body ran 27.13s before ending timed_out.
        Assert.True(
            worstCase < budget,
            $"simpleActivity worst case {worstCase} ({attempts} of attempts + {backoff} of " +
            $"backoff) must stay under the {budget} drain budget");
    }

    [Theory]
    // jitter 1.0 puts the low end of rate x [1-j, 1+j] at zero, and the driver loop spins,
    // here against api.open-meteo.com, which rate-limits you for it.
    [InlineData("simpleActivity:\n  jitter: 1.0\n")]
    [InlineData("simpleActivity:\n  jitter: -0.1\n")]
    // A zero or negative rate is the same spin.
    [InlineData("simpleActivity:\n  rate: 0\n")]
    // At zero concurrency every tick is skipped at capacity and the driver starts nothing.
    [InlineData("simpleActivity:\n  concurrency: 0\n")]
    // A zero sleep removes the only thing that makes this case slow enough to watch, and
    // turns repro_simple_activity_latency's 5s shoulder into a lie.
    [InlineData("simpleActivity:\n  sleepDuration: 0\n")]
    // An unbounded HTTP call outlives the drain budget on a blackholed route, where the
    // connect never fails fast the way a downed interface does.
    [InlineData("simpleActivity:\n  httpTimeout: 0\n")]
    // startToClose must clear sleep + httpTimeout + 2s of headroom, or every attempt dies
    // of start-to-close before the activity can return and the retry policy is exhausted
    // against a perfectly healthy network.
    [InlineData("simpleActivity:\n  sleepDuration: 5s\n  httpTimeout: 3s\n  startToCloseTimeout: 9s\n")]
    // 0 means UNLIMITED in Temporalio.Common.RetryPolicy, not "no retries". Unlimited
    // retries of a 5s-plus-HTTP activity against a third party park the loadgen past the
    // drain budget.
    [InlineData("simpleActivity:\n  retry:\n    maximumAttempts: 0\n")]
    [InlineData("simpleActivity:\n  retry:\n    maximumAttempts: -1\n")]
    // A backoff coefficient under 1 SHRINKS the interval on every retry, which is a retry
    // storm wearing a retry policy's clothes.
    [InlineData("simpleActivity:\n  retry:\n    backoffCoefficient: 0.5\n")]
    // A maximum below the initial makes backoffCoefficient do nothing at all.
    [InlineData("simpleActivity:\n  retry:\n    initialInterval: 10s\n    maximumInterval: 1s\n")]
    // Open-Meteo answers 400 for out-of-range coordinates, and the synthetic fallback would
    // mask that forever. The panel would read all-synthetic and blame your egress.
    [InlineData("simpleActivity:\n  latitude: 91.0\n")]
    [InlineData("simpleActivity:\n  longitude: -181.0\n")]
    // An unusable baseUrl fails inside a fire-and-forget run body, so the only symptom is
    // the driver's failure counter climbing.
    //
    // The guard has TWO clauses, TryCreate then the http/https scheme test, and the
    // first two rows both die on TryCreate, leaving the scheme test uncovered. MEASURED:
    // with the scheme clause deleted the whole suite still passed. The last two rows are
    // absolute URIs that .NET accepts, so each one reaches the second clause. The
    // host:port form is the dangerous one: it parses with Scheme "api.open-meteo.com" and
    // an EMPTY host, which is a plausible paste from a curl line, and HttpClient then
    // throws NotSupportedException, which IsTransportFailure does not treat as transport,
    // so it burns the retry budget instead of failing fast.
    [InlineData("simpleActivity:\n  baseUrl: \"\"\n")]
    [InlineData("simpleActivity:\n  baseUrl: \"api.open-meteo.com/v1/forecast\"\n")]
    [InlineData("simpleActivity:\n  baseUrl: \"api.open-meteo.com:443/v1/forecast\"\n")]
    [InlineData("simpleActivity:\n  baseUrl: \"ftp://api.open-meteo.com/v1/forecast\"\n")]
    public void RejectsUnusableSimpleActivityConfig(string yaml) =>
        WithTempConfig(
            yaml,
            // The exact type matters: it pins the failure to Validate rather than to
            // GoDuration.Parse or the YAML deserializer.
            path => Assert.Throws<ArgumentException>(() => ConfigLoader.Load(path)));
}
