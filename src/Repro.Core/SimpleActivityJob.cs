using Repro.Core.Config;

namespace Repro.Core;

/// <summary>Input to <c>WorkflowSimpleActivity</c> and its single activity.</summary>
/// <param name="SleepDurationMs">
/// How long the activity sleeps BEFORE it fetches anything, in milliseconds. An int
/// rather than a TimeSpan for the same reason as <see cref="JobInput.StepDurationMs"/>:
/// this crosses the payload converter and shows up in `temporal workflow show`, where
/// "SleepDurationMs": 5000 reads better than a serialized TimeSpan.
/// </param>
/// <param name="Latitude">Degrees north, -90 to 90. Validated by ConfigLoader, not here.</param>
/// <param name="Longitude">Degrees east, -180 to 180.</param>
/// <param name="Activity">
/// The timeouts and retry policy the workflow schedules the activity with, plus the
/// activity's own HTTP deadline. Optional with a null default so a history captured
/// before this field existed still deserializes. See
/// <see cref="SimpleActivityOptionsInput"/>.
/// </param>
/// <remarks>
/// WHY THE COORDINATES TRAVEL IN THE INPUT rather than through the activity's
/// constructor the way <see cref="FaultConfig"/> does. That constructor rule exists to
/// stop WORKFLOW code reading mutable process state, which is a determinism rule.
/// Coordinates are job SHAPE, like <see cref="JobInput.Steps"/>: they describe what this
/// particular run was asked to do. Putting them in the input also means a captured
/// history says which city it was, so a fixture is self-describing.
/// <para>
/// <see cref="SimpleActivityConfig.BaseUrl"/> and
/// <see cref="SimpleActivityConfig.RequireLiveWeather"/> deliberately do NOT travel here.
/// Those are infrastructure and policy, not job shape, and they reach the activity
/// through its constructor exactly like FaultConfig does.
/// </para>
/// </remarks>
public record SimpleActivityInput(
    int SleepDurationMs = 5_000,
    double Latitude = 47.6062,
    double Longitude = -122.3321,
    SimpleActivityOptionsInput? Activity = null)
{
    /// <summary>Project config.yaml's <c>simpleActivity:</c> block onto the wire shape.</summary>
    /// <remarks>
    /// Call this in CLIENT code, meaning the loadgen driver, never in the workflow. The
    /// config read has to happen once, before the workflow exists.
    /// </remarks>
    public static SimpleActivityInput From(SimpleActivityConfig simpleActivity)
    {
        ArgumentNullException.ThrowIfNull(simpleActivity);

        // NAMED arguments, for the reason WeatherReading's remarks give below: Latitude and
        // Longitude are ADJACENT doubles, so swapping them compiles clean and any config with
        // both inside [-90, 90] silently fetches the wrong city.
        return new SimpleActivityInput(
            SleepDurationMs: (int)simpleActivity.SleepDuration.TotalMilliseconds,
            Latitude: simpleActivity.Latitude,
            Longitude: simpleActivity.Longitude,
            Activity: SimpleActivityOptionsInput.From(simpleActivity));
    }
}

/// <summary>The activity's timeouts, retry policy and HTTP deadline, carried in the workflow INPUT.</summary>
/// <remarks>
/// A SEPARATE record from <see cref="ActivityOptionsInput"/>, not a reuse. Reusing that
/// record would drag HeartbeatTimeoutMs and ScheduleToCloseTimeoutMs into the payload and
/// then into the ActivityOptions, and the absence of both is what this case exists to
/// demonstrate.
/// <para>
/// For WHY options travel in the input at all, see
/// <see cref="ActivityOptionsInput"/>: activity options are baked into the
/// ScheduleActivityTask command when the activity is scheduled, so values that arrive in
/// the input are in the history and a replay reads back the same bytes it wrote. That
/// argument is not repeated here.
/// </para>
/// <para>
/// <see cref="HttpTimeoutMs"/> travels here for the same reason the timeouts do. The
/// activity's own request deadline is derived from it, and a deadline read from
/// config.yaml at replay time would not be reproducible from the history.
/// </para>
/// <para>
/// The defaults below are the shipped config.yaml values. They are what a null Activity
/// falls back to, so histories that predate this field still replay clean. Do not "tidy"
/// them. Change config.yaml instead.
/// </para>
/// </remarks>
public record SimpleActivityOptionsInput(
    int StartToCloseTimeoutMs = 30_000,
    int HttpTimeoutMs = 3_000,
    int RetryInitialIntervalMs = 1_000,
    double RetryBackoffCoefficient = 2.0,
    int RetryMaximumIntervalMs = 10_000,
    int RetryMaximumAttempts = 3) : IRetryInput
{
    /// <inheritdoc cref="SimpleActivityInput.From"/>
    public static SimpleActivityOptionsInput From(SimpleActivityConfig simpleActivity)
    {
        ArgumentNullException.ThrowIfNull(simpleActivity);

        // NAMED, and here the hazard is worse than the one above: RetryMaximumIntervalMs and
        // RetryMaximumAttempts are ADJACENT ints. Swapped positionally you get a 3ms maximum
        // interval and 10,000 attempts against a third-party endpoint. That is exactly what
        // ConfigLoader.ValidateSimpleActivity refuses to let through config, arrived at
        // silently.
        return new SimpleActivityOptionsInput(
            StartToCloseTimeoutMs: (int)simpleActivity.StartToCloseTimeout.TotalMilliseconds,
            HttpTimeoutMs: (int)simpleActivity.HttpTimeout.TotalMilliseconds,
            RetryInitialIntervalMs: (int)simpleActivity.Retry.InitialInterval.TotalMilliseconds,
            RetryBackoffCoefficient: simpleActivity.Retry.BackoffCoefficient,
            RetryMaximumIntervalMs: (int)simpleActivity.Retry.MaximumInterval.TotalMilliseconds,
            RetryMaximumAttempts: simpleActivity.Retry.MaximumAttempts);
    }
}

/// <summary>What the activity returns, and therefore what lands in the history.</summary>
/// <param name="Source">
/// Which path produced this reading: see <c>MetricNames.Sources</c>. The single most
/// important field here, because "synthetic" is the difference between a working demo
/// and a demo whose network is down, and both report outcome="completed".
/// </param>
/// <param name="TemperatureCelsius">Open-Meteo's <c>current.temperature_2m</c>.</param>
/// <param name="TemperatureUnit">From <c>current_units</c>, or a fallback. Cosmetic.</param>
/// <param name="WindSpeedKmh">Open-Meteo's <c>current.wind_speed_10m</c>.</param>
/// <param name="WindSpeedUnit">From <c>current_units</c>, or a fallback. Cosmetic.</param>
/// <param name="ObservedAt">
/// Open-Meteo's <c>current.time</c>, VERBATIM as a string. Not a DateTime: the API
/// returns "2026-08-31T14:30" with no offset, so parsing it invents a timezone. Keeping
/// the string means `workflow show` prints exactly what the upstream said.
/// </param>
/// <param name="HttpElapsedMs">
/// How long the request took. Puts the network cost in the completed event, so you can
/// tell a real fetch from the synthetic one without reading <paramref name="Source"/>.
/// </param>
/// <remarks>
/// AN ACTIVITY'S RETURN RECORD IS A REPLAY-VISIBLE SCHEMA, and the rule is about NAMES, not
/// positions. The payload is name-keyed on the wire, so a parameter NAME is the contract. The
/// committed fixture decodes to
/// <c>{"Source":"open-meteo","TemperatureCelsius":16.3,...}</c>.
/// <para>
/// MEASURED against Temporalio 1.18.0, and the results are the opposite of what positional
/// intuition suggests. RENAME a parameter and it binds nothing: the value arrives as
/// <c>default(T)</c>, every fixture still reports "replay OK", the build stays at 0 warnings
/// and the tests stay green. A renamed TemperatureCelsius reads 0.0 degrees, a plausible
/// temperature. That is the "plausible CONSTANT" <c>HistogramBuckets</c>'s header calls the
/// worst failure mode in this repo, it is the one edit replay cannot protect you from, and it
/// is the only one worth fearing here.
/// </para>
/// <para>
/// REORDERING and REMOVING, by contrast, are invisible to replay: measured, swapping
/// TemperatureCelsius with WindSpeedKmh still deserializes correctly, and dropping
/// HttpElapsedMs still deserializes. The C# side is what constrains those: the two 7-arg
/// positional constructions in <c>WeatherActivities</c>, Parse and Synthetic. Because the type
/// sequence is (string, double, string, double, string, string, int), swapping the two doubles
/// compiles clean and silently reports 7.8 degrees with 16.3 km/h. Both sites therefore use
/// NAMED arguments, which turns that edit into a compile error. Keep it that way rather than
/// relying on this paragraph.
/// </para>
/// <para>
/// The positional defaults are NOT what makes this safe, though that is the obvious reading.
/// Measured with every default stripped: the same payload still deserializes with no
/// exception and Temperature still reads 0, because RespectRequiredConstructorParameters is
/// off by default. The defaults buy <c>""</c> instead of <c>null</c> for the reference-typed
/// members, and nothing more.
/// </para>
/// <para>
/// <see cref="Checkpoint"/> and <c>SimpleResult</c> carry the same wire contract. They are
/// not more fragile than this one, and they are not less: nothing about a committed fixture
/// changes the rules, it only changes whether you notice.
/// </para>
/// </remarks>
public record WeatherReading(
    string Source = "",
    double TemperatureCelsius = 0,
    string TemperatureUnit = "",
    double WindSpeedKmh = 0,
    string WindSpeedUnit = "",
    string ObservedAt = "",
    int HttpElapsedMs = 0);
