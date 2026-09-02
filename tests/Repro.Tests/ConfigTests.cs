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

    [Fact]
    public void NoFileScanIsASwitchOnEveryBinary()
    {
        Assert.True(Flags.Parse(["--no-file-scan"]).Switch("--no-file-scan"));
        Assert.False(Flags.Parse([]).Switch("--no-file-scan"));
        Assert.Throws<ArgumentException>(() => Flags.Parse(["--no-file-scan=false"]));

        // The three other --no-* switches, none of which may answer for this one. The scan is
        // the only loop whose runs last minutes and hold an activity slot the whole time, so
        // "I turned the scan off" being silently false is the expensive version of the mistake
        // NoSimpleActivityIsASwitchOnEveryBinary describes.
        Assert.False(Flags.Parse(["--no-simple"]).Switch("--no-file-scan"));
        Assert.False(Flags.Parse(["--no-simple-activity"]).Switch("--no-file-scan"));
        Assert.False(Flags.Parse(["--no-local-activity"]).Switch("--no-file-scan"));
        Assert.False(Flags.Parse(["--no-file-scan"]).Switch("--no-simple"));
        Assert.False(Flags.Parse(["--no-file-scan"]).Switch("--no-local-activity"));

        // And the starter's OPT-IN switch, which is the near-homograph in this family:
        // --file-scan runs one scan and --no-file-scan suppresses the loadgen's loop, so they
        // are not merely different, they are opposites.
        Assert.False(Flags.Parse(["--file-scan"]).Switch("--no-file-scan"));
        Assert.False(Flags.Parse(["--no-file-scan"]).Switch("--file-scan"));
    }

    [Fact]
    public void FileScanValueFlagsConsumeTheirValue()
    {
        // All three are registered in ValueFlags, and registration is the whole test: the sets
        // are static, so a flag the starter wants but nobody registered is an unknown-flag hard
        // error in every binary.
        var flags = Flags.Parse(
            ["--file", "/corpus/sample-200mb.txt", "--rows-per-second", "3000", "--max-rows=500000"]);

        Assert.Equal("/corpus/sample-200mb.txt", flags.Str("--file"));
        Assert.Equal(3000, flags.Number("--rows-per-second"));
        Assert.Equal(500_000, flags.Number("--max-rows"));
    }

    [Fact]
    public void FileIsAValueFlagAndFileScanIsASwitch()
    {
        // THE DANGEROUS PAIR IN THIS FAMILY. "--file" is a string PREFIX of "--file-scan", and
        // they are on opposite sides of the value/switch split: if Known or Switches ever
        // matched by prefix, "--file-scan" would be classified as a value flag and would EAT
        // THE NEXT ARGV ENTRY. The symptom is not an error -- it is a starter that runs one
        // scan and silently ignores the flag it swallowed.
        var flags = Flags.Parse(["--file-scan", "--restart"]);

        Assert.True(flags.Switch("--file-scan"));
        Assert.True(flags.Switch("--restart"));
        Assert.Null(flags.Str("--file"));

        // And the reverse: "--file" must still demand a value rather than being treated as the
        // switch its prefix-mate is.
        Assert.Throws<ArgumentException>(() => Flags.Parse(["--file"]));
        Assert.Throws<ArgumentException>(() => Flags.Parse(["--file-scan=true"]));
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

    [Fact]
    public void LoadsTheCommittedLocalActivityBlock()
    {
        var config = ConfigLoader.Load(ConfigLoader.Resolve(null));
        var la = config.LocalActivity;

        // The namespace and queue are the two values the second worker, the second client,
        // create-namespace.sh, compose.yml and the dynamic-config override all have to agree
        // on. Nothing in the .NET build catches a drift between them and the YAML, so it is
        // pinned here.
        Assert.Equal("repro-local-activity", la.Namespace);
        Assert.Equal("repro-la-queue", la.TaskQueue);

        // 30s..2m against the 1m workflowTaskHeartbeatTimeout is what makes exactly two thirds
        // of runs re-execute. docs/WORKFLOWS.md quotes (120-60)/(120-30); if either bound moves
        // that number is wrong and this assertion is how you find out.
        Assert.Equal(TimeSpan.FromSeconds(30), la.MinDuration);
        Assert.Equal(TimeSpan.FromMinutes(2), la.MaxDuration);

        // RunTimeout is the only rung that actually ends a run whose burn outlives the
        // heartbeat timeout, so it is the one number here that cannot be allowed to go missing.
        Assert.Equal(TimeSpan.FromMinutes(6), la.RunTimeout);

        // Not 0 and not unset. Both of those mean retry FOREVER on a local activity, which is
        // an unbounded chain of two-minute CPU burns rather than a slow test.
        Assert.Equal(1, la.Retry.MaximumAttempts);
    }

    // ValidateLocalActivity, which until now had no test of any kind. Every row is a config a
    // careless edit produces and that the server would otherwise accept.
    [Theory]
    // The whole reason this workflow has a second namespace is that
    // history.workflowTaskHeartbeatTimeout is namespace-scoped. Collapsing the two applies this
    // case's 1m override to the other three workflows, which have no local activities, so
    // heartbeat behaviour appears from nowhere in workflows that cannot cause it.
    [InlineData("localActivity:\n  namespace: default\n")]
    // Prefix collision, tested in BOTH directions because the check is symmetric and a
    // one-directional implementation passes the first row and fails the second.
    [InlineData("localActivity:\n  taskQueue: repro-task-queue\n")]
    [InlineData("localActivity:\n  taskQueue: repro-task-queue-la\n")]
    [InlineData("taskQueue: repro-la\n")]
    // At a zero or inverted draw the driver's uniform draw over a closed interval throws
    // inside a fire-and-forget run body, so the only symptom is a counter climbing.
    [InlineData("localActivity:\n  minDuration: 0s\n")]
    [InlineData("localActivity:\n  maxDuration: 10s\n")]
    // Start-to-close below the longest possible burn turns the case into an ordinary activity
    // timeout and the workflow task never times out at all, which is a different bug entirely.
    [InlineData("localActivity:\n  startToCloseTimeout: 1m\n")]
    // Zero means UNLIMITED in Temporalio.Common.RetryPolicy, so this is the config that gives
    // an unbounded chain of two-minute CPU burns.
    [InlineData("localActivity:\n  retry:\n    maximumAttempts: 0\n")]
    // jitter is multiplied into the delay, so at 1.0 the low end of the draw is zero and the
    // driver spins against the frontend.
    [InlineData("localActivity:\n  jitter: 1.0\n")]
    [InlineData("localActivity:\n  rate: 0s\n")]
    [InlineData("localActivity:\n  concurrency: 0\n")]
    public void RejectsUnusableLocalActivityConfig(string yaml) =>
        WithTempConfig(
            yaml,
            path => Assert.Throws<ArgumentException>(() => ConfigLoader.Load(path)));

    [Fact]
    public void AllowsScheduleToCloseBelowStartToClose()
    {
        // NOT a rejection, and this test exists so nobody adds the rule that would make it one.
        // Dropping scheduleToCloseTimeout below the 1m workflow task heartbeat timeout is the
        // DOCUMENTED MITIGATION for this whole case: the local activity then fails with a
        // timeout the workflow catches, and the workflow task is never re-executed. Ordering it
        // against startToCloseTimeout looks like an obvious missing validation and would make
        // the fix unconfigurable.
        WithTempConfig(
            "localActivity:\n  scheduleToCloseTimeout: 45s\n",
            path => Assert.Equal(
                TimeSpan.FromSeconds(45),
                ConfigLoader.Load(path).LocalActivity.ScheduleToCloseTimeout));
    }

    /// <summary>Rows in <c>sample-500mb.txt</c>, the largest corpus scripts/gen-samples produces.</summary>
    /// <remarks>
    /// THE SAME CONSTANT <c>ConfigLoader.ValidateFileScan</c> derives rule 6's timeout floors
    /// from, where it is <c>LargestShippedCorpusRows</c> and private. Duplicated here rather
    /// than read off disk for the reason that method's remarks give: sample_files/ is gitignored
    /// and generated, so it is absent on a fresh clone and NOTHING in the validation path or in
    /// this file may stat it. scripts/gen-samples/MANIFEST.txt is the record on the generator's
    /// side and all three copies are kept in step by hand.
    /// </remarks>
    private const long LargestShippedCorpusRows = 8_622_570;

    [Fact]
    public void LoadsTheCommittedFileScanBlock()
    {
        var path = ConfigLoader.Resolve(null);
        var config = ConfigLoader.Load(path);
        var fs = config.FileScan;

        Assert.True(fs.Enabled);

        // The queue name, which the second TemporalWorker, the loadgen's fifth driver, the
        // filescan board's slot panel and the heartbeat board's newly-pinned slot expressions
        // all have to agree on. Nothing in the .NET build catches a drift between them and the
        // YAML, so it is pinned here the way localActivity.taskQueue is.
        Assert.Equal("repro-scan-queue", fs.TaskQueue);

        // The pace, and the two numbers every magnitude in the docs is derived from: 6000
        // rows/s over the 100 MB corpus's 1,724,588 rows is 4m47s, and 600 rows per batch is a
        // 100ms batch period, which IS the loop's reaction time to a drain.
        Assert.Equal(6000L, fs.TargetRowsPerSecond);
        Assert.Equal(600, fs.BatchRows);
        Assert.Equal(0L, fs.MaxRows);
        Assert.Equal(TimeSpan.FromSeconds(10), fs.LogInterval);

        // 65536 is SOH: a byte[] reaches the 85,000-byte LOH threshold at 84,976, so
        // repro_file_scan_loh_bytes sits at a TRUE zero and fault.slurpWholeFile is the only
        // thing that can move it. Raise this past ~83 KiB and that panel's NODATA reason in
        // docs/DASHBOARDS.md becomes wrong.
        Assert.Equal(65_536, fs.BufferBytes);

        // The ladder. heartbeatTimeout is chosen for the STALENESS it produces, not for
        // liveness: it sets Core's throttle to min(0.8 x 30s, worker.maxHeartbeatThrottleInterval)
        // = 24s, and therefore how much work a kill -9 destroys the record of -- 24 x 6000 =
        // 144,000 rows, the figure docs/HEARTBEATING.md's recipe and the cursor panel both
        // quote. It is also the value repro_file_scan_staleness' 24_000 boundary is derived
        // from, which TelemetryTests checks from the other side.
        Assert.Equal(TimeSpan.FromSeconds(30), fs.HeartbeatTimeout);
        Assert.Equal(TimeSpan.FromMinutes(30), fs.StartToCloseTimeout);
        Assert.Equal(TimeSpan.FromHours(1), fs.ScheduleToCloseTimeout);

        // 10, not the usual 5, because each kill -9 spends one and HEARTBEATING.md's recipe
        // does three cycles. NOT 0, which Temporalio.Common.RetryPolicy reads as UNLIMITED.
        Assert.Equal(10, fs.Retry.MaximumAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), fs.Retry.InitialInterval);
        Assert.Equal(2.0, fs.Retry.BackoffCoefficient);
        Assert.Equal(TimeSpan.FromSeconds(10), fs.Retry.MaximumInterval);

        // The loadgen's fifth loop. 6m is just over one 4m47s scan, so a scan is in flight
        // essentially always without a second one ever being started.
        Assert.Equal(TimeSpan.FromMinutes(6), fs.Rate);
        Assert.Equal(0.2, fs.Jitter);
        Assert.Equal(1, fs.Concurrency);

        // All three pressure knobs ship OFF. They are turned on ONE AT A TIME, and the whole
        // pressure ladder in docs/WORKFLOWS.md reads a baseline that only exists while every
        // one of them is false: with any of them on by default, "allocation is not growth" is
        // measured against a moving floor and attributes nothing.
        Assert.False(config.Fault.DecodeRowsToStrings);
        Assert.False(config.Fault.RetainScannedRows);
        Assert.False(config.Fault.SlurpWholeFile);
    }

    [Fact]
    public void ResolvesTheCorpusPathAgainstTheConfigFileNotTheWorkingDirectory()
    {
        // ASSERTED AS A BEHAVIOUR, not against the literal in the YAML, because ValidateFileScan
        // REWRITES fileScan.path in place during Validate -- the BindAddress.Normalize
        // mutate-during-validate precedent. Asserting the literal "sample_files/sample-100mb.txt"
        // would pass while the rewrite was deleted.
        var path = ConfigLoader.Resolve(null);
        var fs = ConfigLoader.Load(path).FileScan;
        var configDir = Path.GetDirectoryName(Path.GetFullPath(path))!;

        Assert.True(Path.IsPathRooted(fs.Path), $"fileScan.path must be absolute; got \"{fs.Path}\"");
        Assert.Equal(Path.Combine(configDir, "sample_files", "sample-100mb.txt"), fs.Path);

        // AND NOT THE CWD-RELATIVE RESOLUTION, which is the difference the rewrite exists to
        // prevent and the only assertion here that can fail while the one above passes. Under
        // `dotnet test` the working directory is the test assembly's output directory, five
        // levels below the repo root, so the two resolutions genuinely differ -- the same way
        // they differ between docs/HEARTBEATING.md's recipe, which runs the built binary from
        // the repo root, and demo-up.sh, which runs from elsewhere. A cwd-relative path there
        // silently means two DIFFERENT files across a resume, and the checkpoint's
        // corpus-identity check is the only thing that would ever notice.
        //
        // MEASURED: replacing the config-dir resolution with the cwd-relative
        // Path.GetFullPath(fs.Path) fails ONLY this test out of 227. LoadsTheCommittedFileScanBlock
        // stays green, and so does everything that loads config.yaml, which is exactly why the
        // path is pinned here as a BEHAVIOUR rather than there as a value.
        Assert.NotEqual(
            Path.GetFullPath(Path.Combine("sample_files", "sample-100mb.txt")),
            fs.Path);
    }

    // ValidateFileScan, one row per rule. Every row is a config a careless edit produces, and
    // every one of them would otherwise be accepted by the server.
    [Theory]
    // 1. An empty path is not caught here as a missing FILE -- nothing in ValidateFileScan
    // stats anything -- it reaches the activity, which throws non-retryably, so every scan
    // dies on attempt 1 with the cause buried under an ActivityFailure chain.
    [InlineData("fileScan:\n  path: \"\"\n")]
    // 2. A negative rate makes the pacer's absolute due time run backwards, so every batch is
    // already overdue and the scan runs flat out while every panel reports the configured rate.
    [InlineData("fileScan:\n  targetRowsPerSecond: -1\n")]
    // At zero batchRows the loop completes no rows between checks: the cursor never advances
    // while the activity keeps heartbeating an unchanged checkpoint, which on the board is
    // indistinguishable from a stalled disk.
    [InlineData("fileScan:\n  batchRows: 0\n")]
    // 0 is the documented sentinel for "the whole file"; a negative bound is not "unlimited".
    // It also makes the completion aggregate negative, so a CORRECT scan reports
    // result="mismatch" -- the one failure this case must never produce spuriously.
    [InlineData("fileScan:\n  maxRows: -1\n")]
    // At a zero log interval every batch takes a GC.GetGCMemoryInfo() sample (~400 B a call)
    // and prints a line, so the sampler dominates the allocation counter it publishes and the
    // memory panels measure the measurement.
    [InlineData("fileScan:\n  logInterval: 0s\n")]
    // Below the longest row (76 bytes in the shipped corpora) a full buffer with no LF is
    // terminal, so a legal file fails; below one page the read syscall rate IS the scan.
    [InlineData("fileScan:\n  bufferBytes: 1024\n")]
    // Above the ceiling the buffer is a slurp with extra steps: one LOH allocation held for the
    // whole attempt, stepping loh_bytes and working_set_bytes exactly the way
    // fault.slurpWholeFile is supposed to, so neither knob can attribute the step to itself.
    [InlineData("fileScan:\n  bufferBytes: 33554432\n")]
    // 3. THE REAL HAZARD. 1,000,000 rows at 6000 rows/s is a 167-second batch, and the batch
    // boundary is the ONLY place the loop observes ctx.CancellationToken, polls
    // ctx.WorkerShutdownToken or calls Heartbeat(). Such a batch is not slow, it is DEAF.
    [InlineData("fileScan:\n  batchRows: 1000000\n")]
    // 4. And the other end: 1 row at 6000 rows/s is a 167us sleep, which Task.Delay cannot
    // express and rounds UP to the platform tick, so the process runs SLOWER than the
    // configured rate while every rows/s panel and the console line report the configured one.
    [InlineData("fileScan:\n  batchRows: 1\n")]
    // 5. Below the absolute 5s floor a kill -9 redoes under 4s of rows: visible on a panel,
    // invisible in a demo, which is the entire point of the case.
    [InlineData("fileScan:\n  heartbeatTimeout: 1s\n")]
    // And the 10 x batchPeriod clause, which the row above cannot reach: a 1s batch period
    // needs a 10s heartbeat timeout, and ten batch periods is the margin that keeps ONE GC
    // pause from timing the attempt out on a healthy worker -- which reads as "resume is
    // broken", the worst way for this case to fail.
    [InlineData("fileScan:\n  batchRows: 6000\n  heartbeatTimeout: 5s\n")]
    // startToClose must EXCEED heartbeatTimeout, or every attempt dies of start-to-close before
    // a heartbeat timeout can be observed, the server never reschedules from the checkpoint,
    // and the resume path this case exists to demonstrate is never taken.
    [InlineData("fileScan:\n  startToCloseTimeout: 30s\n")]
    // 6. The derived floor, from the largest SHIPPED corpus when maxRows is 0. 8,622,570 rows
    // at 6000 rows/s is 23m57s, so 25m does not clear worstScan + 2m: attempt 1 dies of
    // start-to-close part-way through the corpus on a healthy worker, and every retry then
    // resumes and dies at the same place until maximumAttempts is gone.
    [InlineData("fileScan:\n  startToCloseTimeout: 25m\n")]
    // The schedule rung, whose floor is 23m57s + 9 x 64s + 2m = 35m33s. Below it the WORKFLOW
    // fails schedule-to-close mid-scan with attempts still on the clock, which also reads as
    // "resume is broken".
    [InlineData("fileScan:\n  scheduleToCloseTimeout: 30m\n")]
    // And the same rule derived from maxRows instead: 20,000,000 rows at 6000 rows/s is 55m33s,
    // which the shipped 30m start-to-close cannot cover. This row is what proves rule 6 reads
    // maxRows at all, since every other row here exercises the corpus-constant branch.
    [InlineData("fileScan:\n  maxRows: 20000000\n")]
    // 7. 0 means UNLIMITED in Temporalio.Common.RetryPolicy, not "do not retry", and an
    // unbounded chain of half-hour scans holds an activity slot on the scan queue forever.
    [InlineData("fileScan:\n  retry:\n    maximumAttempts: 0\n")]
    // An empty queue name is not a fallback to taskQueue: the server rejects it when the worker
    // polls, so the worker starts, logs nothing useful and takes no scan task.
    [InlineData("fileScan:\n  taskQueue: \"\"\n")]
    // Prefix-disjointness against config.taskQueue, tested in BOTH directions because the check
    // is symmetric and a one-directional implementation passes one row and fails the other.
    // These two queues are in the SAME namespace, so a collision puts a second heartbeating
    // activity type on the seed case's queue, and temporal_worker_task_slots_used carries no
    // activity_type label to separate them again.
    [InlineData("fileScan:\n  taskQueue: repro-task-queue-scan\n")]
    [InlineData("taskQueue: repro-scan\n")]
    // And against localActivity.taskQueue, also both directions. These two ARE in different
    // namespaces, so the server permits it and nothing fails at startup; what breaks is every
    // human-facing lookup that matches on queue name without a namespace, which is all of them.
    [InlineData("fileScan:\n  taskQueue: repro-la-queue-scan\n")]
    [InlineData("localActivity:\n  taskQueue: repro-scan\n")]
    // The loadgen's fourth jittered loop, and the fourth copy of Jitter.cs's contract: its
    // formula is safe only because rate > 0 and jitter in [0, 1) are enforced here. At zero
    // rate the driver loop is a busy spin whose every non-skipped iteration starts a
    // multi-minute scan.
    [InlineData("fileScan:\n  rate: 0s\n")]
    [InlineData("fileScan:\n  jitter: 1.0\n")]
    [InlineData("fileScan:\n  jitter: -0.1\n")]
    [InlineData("fileScan:\n  concurrency: 0\n")]
    // 8. The one CROSS-BLOCK refusal, and the only rule here whose failure is not an empty
    // panel: one retained scan of the largest shipped corpus is about 1.3 GB of live promoted
    // heap, so two concurrent ones share a workstation-GC heap and the outcome is an
    // OOM-killed worker, which takes the whole demo's signal down with it.
    [InlineData("fault:\n  retainScannedRows: true\nfileScan:\n  concurrency: 2\n")]
    public void RejectsUnusableFileScanConfig(string yaml) =>
        WithTempConfig(
            yaml,
            // The EXACT type, not ThrowsAny: every row above is valid YAML carrying a valid Go
            // duration, so the only thing that can reject it is Validate. A row that needed
            // ThrowsAny would be a row that never reached ValidateFileScan at all, which is the
            // distinction RejectsUnusableSimpleActivityConfig and RejectsGarbage split on.
            path => Assert.Throws<ArgumentException>(() => ConfigLoader.Load(path)));

    [Fact]
    public void FileScanBatchPeriodFitsTheDrainReactionBudget()
    {
        // NOT a copy of SimpleActivityWorstCaseRunFitsTheDrainBudget, and the difference is the
        // whole point. That test demands the worst-case RUN finish inside
        // gracefulShutdownTimeout + 15s; demanding the same of a 4m47s scan would cap the
        // corpus at about 270,000 rows and destroy the case.
        //
        // A long-running activity is not meant to finish inside the drain budget. It
        // checkpoints on the WorkerShutdownToken EDGE, keeps reading, and unwinds when
        // ctx.CancellationToken fires gracefulShutdownTimeout later -- so total drain is about
        // 30s against the 45s budget, the same shape HeartbeatActivities already has, and the
        // corpus size does not enter into it at all.
        //
        // What the budget actually constrains is the loop's REACTION TIME: the batch boundary is
        // the only place ctx.CancellationToken is observed, ctx.WorkerShutdownToken is polled
        // and Heartbeat() is called, so a drain arriving just after a batch starts is not seen
        // for one batch period. The hazard this catches is a batchRows large enough to straddle
        // the whole grace window -- at which point the activity can observe neither the drain
        // nor the cancel nor emit a heartbeat inside ANY window, and demo-down.sh SIGKILLs a
        // worker that never got the chance to checkpoint.
        var config = ConfigLoader.Load(ConfigLoader.Resolve(null));
        var fs = config.FileScan;

        var batchPeriod = fs.TargetRowsPerSecond > 0
            ? TimeSpan.FromSeconds((double)fs.BatchRows / fs.TargetRowsPerSecond)
            : TimeSpan.Zero;

        // Both terms read from the LIVE config: demo-down.sh derives its DRAIN_TIMEOUT as
        // worker.gracefulShutdownTimeout + 15, so raising the grace window raises both sides
        // and this stays a statement about the reaction time rather than about a literal 45s.
        var drainAndReact = config.Worker.GracefulShutdownTimeout + batchPeriod;
        var budget = config.Worker.GracefulShutdownTimeout + TimeSpan.FromSeconds(15);

        // At the shipped config this is 30s + 100ms against 45s.
        Assert.True(
            drainAndReact < budget,
            $"fileScan.batchRows ({fs.BatchRows}) over targetRowsPerSecond " +
            $"({fs.TargetRowsPerSecond}) is a {batchPeriod} batch period, so a drain is observed " +
            $"at worst {drainAndReact} after it is signalled, against demo-down.sh's {budget} " +
            "budget. The scan itself is expected to outlive the budget; its REACTION to a drain " +
            "is not.");
    }

    [Fact]
    public void FileScanTimeoutLadderCoversTheLargestShippedCorpus()
    {
        // FILESYSTEM-FREE, so it runs on a fresh clone where sample_files/ does not exist --
        // the same constraint ValidateFileScan is under, and the reason
        // LargestShippedCorpusRows above is a hand-maintained constant rather than a
        // FileInfo().Length.
        //
        // This is NOT redundant with ValidateFileScan's rule 6, and the gap is exactly the
        // place a corpus can outgrow its ladder unnoticed: rule 6 derives its floors from
        // fileScan.maxRows WHEN THAT IS SET, and goes vacuous entirely when
        // targetRowsPerSecond is 0. So setting maxRows to something small, or unthrottling,
        // moves rule 6's floors down and the committed config still validates -- while a run
        // that later passes --max-rows 0 or --file sample-500mb.txt is provisioned for a scan
        // that no longer fits. This tripwire pins the ladder against the largest corpus the
        // generator actually produces, whatever maxRows currently says.
        var config = ConfigLoader.Load(ConfigLoader.Resolve(null));
        var fs = config.FileScan;

        Assert.True(fs.TargetRowsPerSecond > 0, "an unthrottled scan has no derivable duration");

        // 8,622,570 rows at 6000 rows/s = 23m57s.
        var worstScan = TimeSpan.FromSeconds((double)LargestShippedCorpusRows / fs.TargetRowsPerSecond);
        var headroom = TimeSpan.FromMinutes(2);

        Assert.True(
            fs.StartToCloseTimeout >= worstScan + headroom,
            $"fileScan.startToCloseTimeout ({fs.StartToCloseTimeout}) must cover one worst-case " +
            $"scan of the largest shipped corpus ({LargestShippedCorpusRows} rows at " +
            $"{fs.TargetRowsPerSecond} rows/s = {worstScan}) plus {headroom}");

        // "attempts x startToClose" is the WRONG model and gives an absurd number. Useful work
        // is ONE worst-case scan however many attempts it takes; each RESUME adds
        // heartbeatTimeout (the server noticing) + retry.maximumInterval (backoff) + the
        // throttle (the reading that is redone). The throttle is the real SDK formula,
        // min(0.8 x heartbeatTimeout, worker.maxHeartbeatThrottleInterval), so that this
        // number is the number Core actually takes -- 24s at the shipped config, where the 60s
        // ceiling is not yet binding.
        var throttle = TimeSpan.FromTicks(Math.Min(
            (long)(fs.HeartbeatTimeout.Ticks * 0.8),
            config.Worker.MaxHeartbeatThrottleInterval.Ticks));
        var perResume = fs.HeartbeatTimeout + fs.Retry.MaximumInterval + throttle;

        // Nine resumes: maximumAttempts - 1 at the shipped 10. HEARTBEATING.md's recipe does
        // three kill cycles, and a careless extra kill must not fail the workflow terminally.
        var scheduleFloor = worstScan + (perResume * 9) + headroom;

        Assert.True(
            fs.ScheduleToCloseTimeout >= scheduleFloor,
            $"fileScan.scheduleToCloseTimeout ({fs.ScheduleToCloseTimeout}) must cover " +
            $"{worstScan} of scanning + 9 x {perResume} of resume cost + {headroom} = " +
            $"{scheduleFloor}");

        // And the rung ordering, which no arithmetic above implies: schedule-to-close bounds
        // every attempt together, so a value below start-to-close makes the longer rung
        // unreachable and turns a mid-scan schedule-to-close into the only way any attempt can
        // ever end.
        Assert.True(
            fs.ScheduleToCloseTimeout > fs.StartToCloseTimeout,
            $"fileScan.scheduleToCloseTimeout ({fs.ScheduleToCloseTimeout}) must exceed " +
            $"startToCloseTimeout ({fs.StartToCloseTimeout})");
    }
}
