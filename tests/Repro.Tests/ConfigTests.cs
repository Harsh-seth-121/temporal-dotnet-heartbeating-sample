using Repro.Core.Cli;
using Repro.Core.Config;
using Xunit;

namespace Repro.Tests;

/// <summary>
/// The places where a config mistake is silent rather than loud: Go-duration parsing,
/// bind-address normalization, flag parsing, and config load plus startup validation. Each
/// rejection's reasoning lives once, on the <c>ConfigLoader.Validate*</c> method that raises it.
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
    [InlineData("off")]          // only the --metrics flag understands this; see IsOff
    [InlineData("0.0.0.0")]
    [InlineData("example.com")]
    [InlineData("0x8077")]
    public void RejectsMissingPort(string input) =>
        // These reached s[..-1] and threw a raw ArgumentOutOfRangeException naming neither the
        // option nor the value. It derives from ArgumentException, and Assert.Throws matches the
        // exact type, so this pins the fix.
        Assert.Throws<ArgumentException>(() => BindAddress.Normalize(input, "test"));

    [Fact]
    public void KeepsBracketsOnIpv6() =>
        // Rust's SocketAddr wants the brackets back, so they must survive the round trip.
        Assert.Equal("[::]:8077", BindAddress.Normalize("[::]:8077", "test"));

    [Theory]
    [InlineData("::1")]     // starts with ':' but is not Go's ":port" form
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

/// <summary>The hand-rolled parser, where a flag can silently mean the opposite of what it says.</summary>
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
        // Go's flag package accepts -restart=false, so people type it. Storing the text and
        // testing ContainsKey turned every one of these on, including the ones spelling "off".
        Assert.Throws<ArgumentException>(() => Flags.Parse([arg]));

    [Fact]
    public void ValueFlagsStillTakeEquals()
    {
        // Only switches reject '='. --metrics=... must keep working.
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
        // The flag sets are static and global to all four exes, so a flag nobody registered in
        // Switches is an unknown-flag error in every binary.
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

        // Known and Switches match exactly, not by prefix, and --no-simple is a string prefix of
        // --no-simple-activity.
        Assert.False(Flags.Parse(["--no-simple"]).Switch("--no-simple-activity"));
        Assert.False(Flags.Parse(["--no-simple-activity"]).Switch("--no-simple"));
    }

    [Fact]
    public void NoLocalActivityIsASwitchOnEveryBinary()
    {
        Assert.True(Flags.Parse(["--no-local-activity"]).Switch("--no-local-activity"));
        Assert.False(Flags.Parse([]).Switch("--no-local-activity"));
        Assert.Throws<ArgumentException>(() => Flags.Parse(["--no-local-activity=false"]));

        // Near-homographs that turn off different loops; neither is a prefix of the other.
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

        // The three other --no-* switches, none of which may answer for this one.
        Assert.False(Flags.Parse(["--no-simple"]).Switch("--no-file-scan"));
        Assert.False(Flags.Parse(["--no-simple-activity"]).Switch("--no-file-scan"));
        Assert.False(Flags.Parse(["--no-local-activity"]).Switch("--no-file-scan"));
        Assert.False(Flags.Parse(["--no-file-scan"]).Switch("--no-simple"));
        Assert.False(Flags.Parse(["--no-file-scan"]).Switch("--no-local-activity"));

        // And the starter's opt-in switch: --file-scan runs one scan where --no-file-scan
        // suppresses the loadgen's loop, so they are opposites rather than variants.
        Assert.False(Flags.Parse(["--file-scan"]).Switch("--no-file-scan"));
        Assert.False(Flags.Parse(["--no-file-scan"]).Switch("--file-scan"));
    }

    [Fact]
    public void FileScanValueFlagsConsumeTheirValue()
    {
        // Registration is the whole test; see NoSimpleIsASwitchOnEveryBinary.
        var flags = Flags.Parse(
            ["--file", "/corpus/sample-200mb.txt", "--rows-per-second", "3000", "--max-rows=500000"]);

        Assert.Equal("/corpus/sample-200mb.txt", flags.Str("--file"));
        Assert.Equal(3000, flags.Number("--rows-per-second"));
        Assert.Equal(500_000, flags.Number("--max-rows"));
    }

    [Fact]
    public void FileIsAValueFlagAndFileScanIsASwitch()
    {
        // "--file" is a string prefix of "--file-scan" and the two are on opposite sides of the
        // value/switch split, so under prefix matching "--file-scan" would be classified as a
        // value flag and eat the next argv entry.
        var flags = Flags.Parse(["--file-scan", "--restart"]);

        Assert.True(flags.Switch("--file-scan"));
        Assert.True(flags.Switch("--restart"));
        Assert.Null(flags.Str("--file"));

        // And the reverse: "--file" must still demand a value.
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
        // The real file, so a bad edit to config.yaml fails the suite rather than the first run.
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
        // Rests on DeserializerBuilder not having IgnoreUnmatchedProperties.
        WithTempConfig(
            "fault:\n  failurRate: 0.4\n",
            path => Assert.ThrowsAny<Exception>(() => ConfigLoader.Load(path)));
    }

    [Fact]
    public void EmptyDurationValueIsAHardError()
    {
        // `latency:` with nothing after it is an empty scalar, not an absent key: the property is
        // assigned, so returning TimeSpan.Zero would replace the POCO default silently.
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
    [InlineData("simple:\n  jitter: 1.0\n")]
    [InlineData("simple:\n  jitter: -0.1\n")]
    [InlineData("simple:\n  rate: 0\n")]
    [InlineData("simple:\n  maxDuration: 0\n")]
    [InlineData("simple:\n  concurrency: 0\n")]
    [InlineData("simple:\n  minMessages: 4\n  maxMessages: 2\n")]
    [InlineData("simple:\n  minMessages: -1\n")]
    [InlineData("simple:\n  messageGap: -1s\n")]
    [InlineData("simple:\n  overflowRate: 1.5\n")]
    [InlineData("simple:\n  raceRate: -0.5\n")]
    [InlineData("simple:\n  stopWeight: 0\n  cancelWeight: 0\n  expireWeight: 0\n")]
    public void RejectsUnusableSimpleConfig(string yaml) =>
        WithTempConfig(yaml, path => Assert.Throws<ArgumentException>(() => ConfigLoader.Load(path)));

    [Fact]
    public void RejectsHeartbeatTimeoutLongerThanStartToClose()
    {
        // Otherwise the attempt always dies of start-to-close first, no heartbeat timeout is ever
        // observed, and the panel stays empty.
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
        // demo-down.sh derives DRAIN_TIMEOUT = worker.gracefulShutdownTimeout + 15. A run that
        // outlives it is SIGKILLed mid-flight, and the only symptom is a slow teardown.
        var config = ConfigLoader.Load(ConfigLoader.Resolve(null));
        var sa = config.SimpleActivity;

        var attempts = sa.Retry.MaximumAttempts * (sa.SleepDuration + sa.HttpTimeout);

        // Plus the gaps between attempts. ValidateSimpleActivity bounds the retry intervals only
        // from below, so raising either interval pushes the real worst case past the budget.
        var backoff = TimeSpan.Zero;
        for (var i = 0; i < sa.Retry.MaximumAttempts - 1; i++)
        {
            var interval = sa.Retry.InitialInterval * Math.Pow(sa.Retry.BackoffCoefficient, i);
            backoff += interval < sa.Retry.MaximumInterval ? interval : sa.Retry.MaximumInterval;
        }

        var worstCase = attempts + backoff;
        var budget = config.Worker.GracefulShutdownTimeout + TimeSpan.FromSeconds(15);

        // At the shipped config, 3 x (5s + 3s) + (1s + 2s) = 27s against a 45s budget; a server
        // that answered and then stalled its body ran 27.13s before ending timed_out.
        Assert.True(
            worstCase < budget,
            $"simpleActivity worst case {worstCase} ({attempts} of attempts + {backoff} of " +
            $"backoff) must stay under the {budget} drain budget");
    }

    [Theory]
    // A zero sleep turns repro_simple_activity_latency's 5s shoulder into a lie.
    [InlineData("simpleActivity:\n  jitter: 1.0\n")]
    [InlineData("simpleActivity:\n  jitter: -0.1\n")]
    [InlineData("simpleActivity:\n  rate: 0\n")]
    [InlineData("simpleActivity:\n  concurrency: 0\n")]
    [InlineData("simpleActivity:\n  sleepDuration: 0\n")]
    [InlineData("simpleActivity:\n  httpTimeout: 0\n")]
    [InlineData("simpleActivity:\n  sleepDuration: 5s\n  httpTimeout: 3s\n  startToCloseTimeout: 9s\n")]
    // 0 means unlimited in Temporalio.Common.RetryPolicy, not "no retries". Open-Meteo answers
    // 400 for out-of-range coordinates, which the synthetic fallback would mask forever.
    [InlineData("simpleActivity:\n  retry:\n    maximumAttempts: 0\n")]
    [InlineData("simpleActivity:\n  retry:\n    maximumAttempts: -1\n")]
    [InlineData("simpleActivity:\n  retry:\n    backoffCoefficient: 0.5\n")]
    [InlineData("simpleActivity:\n  retry:\n    initialInterval: 10s\n    maximumInterval: 1s\n")]
    [InlineData("simpleActivity:\n  latitude: 91.0\n")]
    [InlineData("simpleActivity:\n  longitude: -181.0\n")]
    // The baseUrl guard has two clauses, TryCreate then the scheme test; the first two rows die
    // on TryCreate and the last two reach the scheme test, which nothing else covers.
    [InlineData("simpleActivity:\n  baseUrl: \"\"\n")]
    [InlineData("simpleActivity:\n  baseUrl: \"api.open-meteo.com/v1/forecast\"\n")]
    [InlineData("simpleActivity:\n  baseUrl: \"api.open-meteo.com:443/v1/forecast\"\n")]
    [InlineData("simpleActivity:\n  baseUrl: \"ftp://api.open-meteo.com/v1/forecast\"\n")]
    public void RejectsUnusableSimpleActivityConfig(string yaml) =>
        WithTempConfig(
            yaml,
            path => Assert.Throws<ArgumentException>(() => ConfigLoader.Load(path)));

    [Fact]
    public void LoadsTheCommittedLocalActivityBlock()
    {
        var config = ConfigLoader.Load(ConfigLoader.Resolve(null));
        var la = config.LocalActivity;

        // The second worker, the second client, create-namespace.sh, compose.yml and the
        // dynamic-config override all have to agree on these, and no build step checks that.
        Assert.Equal("repro-local-activity", la.Namespace);
        Assert.Equal("repro-la-queue", la.TaskQueue);

        // 30s..2m against the 1m workflowTaskHeartbeatTimeout is what makes exactly two thirds of
        // runs re-execute. docs/WORKFLOWS.md quotes (120-60)/(120-30).
        Assert.Equal(TimeSpan.FromSeconds(30), la.MinDuration);
        Assert.Equal(TimeSpan.FromMinutes(2), la.MaxDuration);

        // RunTimeout is the only rung that ends a run whose burn outlives the heartbeat timeout.
        Assert.Equal(TimeSpan.FromMinutes(6), la.RunTimeout);

        // Not 0 and not unset: both mean retry forever, an unbounded chain of 2m CPU burns.
        Assert.Equal(1, la.Retry.MaximumAttempts);
    }

    [Theory]
    // history.workflowTaskHeartbeatTimeout is namespace-scoped, so collapsing the two namespaces
    // applies this case's 1m override to three workflows that have no local activities. The queue
    // prefix rows go both ways because the check is symmetric.
    [InlineData("localActivity:\n  namespace: default\n")]
    [InlineData("localActivity:\n  taskQueue: repro-task-queue\n")]
    [InlineData("localActivity:\n  taskQueue: repro-task-queue-la\n")]
    [InlineData("taskQueue: repro-la\n")]
    // Start-to-close below the longest possible burn turns this into an ordinary activity
    // timeout, which is a different bug.
    [InlineData("localActivity:\n  minDuration: 0s\n")]
    [InlineData("localActivity:\n  maxDuration: 10s\n")]
    [InlineData("localActivity:\n  startToCloseTimeout: 1m\n")]
    [InlineData("localActivity:\n  retry:\n    maximumAttempts: 0\n")]
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
        // Not a rejection, and this test exists so nobody adds the rule that would make it one.
        // Dropping scheduleToCloseTimeout below the 1m workflow task heartbeat timeout is the
        // documented mitigation for this case, so ordering it against startToCloseTimeout would
        // make the fix unconfigurable.
        WithTempConfig(
            "localActivity:\n  scheduleToCloseTimeout: 45s\n",
            path => Assert.Equal(
                TimeSpan.FromSeconds(45),
                ConfigLoader.Load(path).LocalActivity.ScheduleToCloseTimeout));
    }

    /// <summary>Rows in <c>sample-500mb.txt</c>, the largest corpus scripts/gen-samples produces.</summary>
    /// <remarks>A third hand-maintained copy of <c>ConfigLoader.LargestShippedCorpusRows</c> and
    /// scripts/gen-samples/MANIFEST.txt. Nothing in the validation path or in this file may stat
    /// sample_files/: it is gitignored, generated, and absent on a fresh clone.</remarks>
    private const long LargestShippedCorpusRows = 8_622_570;

    [Fact]
    public void LoadsTheCommittedFileScanBlock()
    {
        var path = ConfigLoader.Resolve(null);
        var config = ConfigLoader.Load(path);
        var fs = config.FileScan;

        Assert.True(fs.Enabled);

        // The second worker, the loadgen's fifth driver and both boards' slot panels all have to
        // agree on this name, and no build step checks that.
        Assert.Equal("repro-scan-queue", fs.TaskQueue);

        // 6000 rows/s over the 100 MB corpus's 1,724,588 rows is 4m47s, and 600 rows per batch is
        // a 100ms batch period, which is the loop's reaction time to a drain.
        Assert.Equal(6000L, fs.TargetRowsPerSecond);
        Assert.Equal(600, fs.BatchRows);
        Assert.Equal(0L, fs.MaxRows);
        Assert.Equal(TimeSpan.FromSeconds(10), fs.LogInterval);

        // 65536 keeps the buffer off the LOH: a byte[] reaches the 85,000-byte threshold at
        // 84,976, so repro_file_scan_loh_bytes sits at a true zero and only fault.slurpWholeFile
        // can move it. Past about 83 KiB, docs/DASHBOARDS.md's NODATA reason is wrong.
        Assert.Equal(65_536, fs.BufferBytes);

        // heartbeatTimeout is chosen for the staleness it produces, not for liveness: it sets
        // Core's throttle to min(0.8 x 30s, worker.maxHeartbeatThrottleInterval) = 24s, so a
        // kill -9 destroys the record of 24 x 6000 = 144,000 rows.
        Assert.Equal(TimeSpan.FromSeconds(30), fs.HeartbeatTimeout);
        Assert.Equal(TimeSpan.FromMinutes(30), fs.StartToCloseTimeout);
        Assert.Equal(TimeSpan.FromHours(1), fs.ScheduleToCloseTimeout);

        // 10, not the usual 5: each kill -9 spends one and the recipe does three cycles.
        Assert.Equal(10, fs.Retry.MaximumAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), fs.Retry.InitialInterval);
        Assert.Equal(2.0, fs.Retry.BackoffCoefficient);
        Assert.Equal(TimeSpan.FromSeconds(10), fs.Retry.MaximumInterval);

        // 6m is just over one 4m47s scan, so one is in flight almost always and never two.
        Assert.Equal(TimeSpan.FromMinutes(6), fs.Rate);
        Assert.Equal(0.2, fs.Jitter);
        Assert.Equal(1, fs.Concurrency);

        // All three pressure knobs ship off: the ladder in docs/WORKFLOWS.md reads a baseline
        // that exists only while every one is false.
        Assert.False(config.Fault.DecodeRowsToStrings);
        Assert.False(config.Fault.RetainScannedRows);
        Assert.False(config.Fault.SlurpWholeFile);
    }

    [Fact]
    public void ResolvesTheCorpusPathAgainstTheConfigFileNotTheWorkingDirectory()
    {
        // Asserted as a behaviour rather than against the YAML literal, because ValidateFileScan
        // rewrites fileScan.path in place during Validate, the BindAddress.Normalize precedent.
        var path = ConfigLoader.Resolve(null);
        var fs = ConfigLoader.Load(path).FileScan;
        var configDir = Path.GetDirectoryName(Path.GetFullPath(path))!;

        Assert.True(Path.IsPathRooted(fs.Path), $"fileScan.path must be absolute; got \"{fs.Path}\"");
        Assert.Equal(Path.Combine(configDir, "sample_files", "sample-100mb.txt"), fs.Path);

        // And not the cwd-relative resolution, the only assertion here that can fail while the
        // one above passes. Under `dotnet test` the working directory is the test assembly's
        // output directory, so the two genuinely differ, and a cwd-relative path silently means
        // two different files across a resume.
        Assert.NotEqual(
            Path.GetFullPath(Path.Combine("sample_files", "sample-100mb.txt")),
            fs.Path);
    }

    // ValidateFileScan, one row per rule; each rule's reasoning is on the method itself.
    [Theory]
    [InlineData("fileScan:\n  path: \"\"\n")]
    [InlineData("fileScan:\n  targetRowsPerSecond: -1\n")]
    [InlineData("fileScan:\n  batchRows: 0\n")]
    [InlineData("fileScan:\n  maxRows: -1\n")]
    [InlineData("fileScan:\n  logInterval: 0s\n")]
    // The bounds straddle the LOH threshold, so neither row can be moved on its own.
    [InlineData("fileScan:\n  bufferBytes: 1024\n")]
    [InlineData("fileScan:\n  bufferBytes: 33554432\n")]
    // The batch boundary is the only place the loop observes ctx.CancellationToken, polls
    // ctx.WorkerShutdownToken or calls Heartbeat(), so a 167-second batch is deaf, not slow.
    [InlineData("fileScan:\n  batchRows: 1000000\n")]
    [InlineData("fileScan:\n  batchRows: 1\n")]
    [InlineData("fileScan:\n  heartbeatTimeout: 1s\n")]
    [InlineData("fileScan:\n  batchRows: 6000\n  heartbeatTimeout: 5s\n")]
    // The last row is the only one that proves rule 6 reads maxRows at all rather than the
    // corpus constant: 20,000,000 rows at 6000 rows/s is 55m33s.
    [InlineData("fileScan:\n  startToCloseTimeout: 30s\n")]
    [InlineData("fileScan:\n  startToCloseTimeout: 25m\n")]
    [InlineData("fileScan:\n  scheduleToCloseTimeout: 30m\n")]
    [InlineData("fileScan:\n  maxRows: 20000000\n")]
    [InlineData("fileScan:\n  retry:\n    maximumAttempts: 0\n")]
    [InlineData("fileScan:\n  taskQueue: \"\"\n")]
    // Prefix-disjointness, both directions, because the check is symmetric.
    [InlineData("fileScan:\n  taskQueue: repro-task-queue-scan\n")]
    [InlineData("taskQueue: repro-scan\n")]
    [InlineData("fileScan:\n  taskQueue: repro-la-queue-scan\n")]
    [InlineData("localActivity:\n  taskQueue: repro-scan\n")]
    // Jitter.cs's formula is safe only because rate > 0 and jitter in [0, 1) are enforced here.
    // The last row is the only cross-block refusal in the file.
    [InlineData("fileScan:\n  rate: 0s\n")]
    [InlineData("fileScan:\n  jitter: 1.0\n")]
    [InlineData("fileScan:\n  jitter: -0.1\n")]
    [InlineData("fileScan:\n  concurrency: 0\n")]
    [InlineData("fault:\n  retainScannedRows: true\nfileScan:\n  concurrency: 2\n")]
    public void RejectsUnusableFileScanConfig(string yaml) =>
        WithTempConfig(
            yaml,
            // The exact type, not ThrowsAny: every row is valid YAML with a valid Go duration,
            // so only Validate can reject it.
            path => Assert.Throws<ArgumentException>(() => ConfigLoader.Load(path)));

    [Fact]
    public void FileScanBatchPeriodFitsTheDrainReactionBudget()
    {
        // Not a copy of SimpleActivityWorstCaseRunFitsTheDrainBudget. A long-running activity is
        // not meant to finish inside the drain budget: it checkpoints on the WorkerShutdownToken
        // edge and unwinds when ctx.CancellationToken fires gracefulShutdownTimeout later. What
        // the budget constrains is the loop's reaction time, so the hazard is a batchRows large
        // enough to straddle the whole grace window.
        var config = ConfigLoader.Load(ConfigLoader.Resolve(null));
        var fs = config.FileScan;

        var batchPeriod = fs.TargetRowsPerSecond > 0
            ? TimeSpan.FromSeconds((double)fs.BatchRows / fs.TargetRowsPerSecond)
            : TimeSpan.Zero;

        // Both terms read the live config, so raising the grace window raises both sides.
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
        // Filesystem-free, so it runs on a fresh clone. Not redundant with ValidateFileScan's
        // rule 6: that rule derives its floors from fileScan.maxRows when set and goes vacuous
        // when targetRowsPerSecond is 0, so shrinking maxRows or unthrottling moves the floors
        // down while a later --max-rows 0 or --file sample-500mb.txt no longer fits.
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

        // "attempts x startToClose" is the wrong model. Useful work is one worst-case scan
        // however many attempts it takes; each resume adds heartbeatTimeout (the server
        // noticing) + retry.maximumInterval (backoff) + the throttle (the reading that is
        // redone), which is Core's real min(0.8 x heartbeatTimeout, maxHeartbeatThrottleInterval)
        // and 24s at the shipped config.
        var throttle = TimeSpan.FromTicks(Math.Min(
            (long)(fs.HeartbeatTimeout.Ticks * 0.8),
            config.Worker.MaxHeartbeatThrottleInterval.Ticks));
        var perResume = fs.HeartbeatTimeout + fs.Retry.MaximumInterval + throttle;

        // Nine resumes: maximumAttempts - 1. The recipe does three kill cycles, and a careless
        // extra kill must not fail the workflow terminally.
        var scheduleFloor = worstScan + (perResume * 9) + headroom;

        Assert.True(
            fs.ScheduleToCloseTimeout >= scheduleFloor,
            $"fileScan.scheduleToCloseTimeout ({fs.ScheduleToCloseTimeout}) must cover " +
            $"{worstScan} of scanning + 9 x {perResume} of resume cost + {headroom} = " +
            $"{scheduleFloor}");

        // The rung ordering, which no arithmetic above implies: schedule-to-close bounds every
        // attempt together, so a value below start-to-close makes the longer rung unreachable.
        Assert.True(
            fs.ScheduleToCloseTimeout > fs.StartToCloseTimeout,
            $"fileScan.scheduleToCloseTimeout ({fs.ScheduleToCloseTimeout}) must exceed " +
            $"startToCloseTimeout ({fs.StartToCloseTimeout})");
    }
}
