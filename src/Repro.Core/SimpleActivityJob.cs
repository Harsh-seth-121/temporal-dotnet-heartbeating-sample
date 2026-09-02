using Repro.Core.Config;

namespace Repro.Core;

/// <summary>Input to <c>WorkflowSimpleActivity</c> and its single activity.</summary>
/// <param name="SleepDurationMs">How long the activity sleeps before it fetches anything. An int rather than a TimeSpan for the reason <see cref="JobInput.StepDurationMs"/> gives.</param>
/// <param name="Latitude">Degrees north, -90 to 90. Validated by ConfigLoader, not here.</param>
/// <param name="Longitude">Degrees east, -180 to 180.</param>
/// <param name="Activity">The activity's timeouts, retry policy and HTTP deadline. Null default so a history captured before this field existed still deserializes.</param>
/// <remarks>The coordinates travel in the input because they are job shape, so a captured history
/// says which city it was. <see cref="SimpleActivityConfig.BaseUrl"/> and
/// <see cref="SimpleActivityConfig.RequireLiveWeather"/> are infrastructure and policy, so they
/// reach the activity through its constructor, the rule <see cref="FaultConfig"/> records.</remarks>
public record SimpleActivityInput(
    int SleepDurationMs = 5_000,
    double Latitude = 47.6062,
    double Longitude = -122.3321,
    SimpleActivityOptionsInput? Activity = null)
{
    /// <summary>Project config.yaml's <c>simpleActivity:</c> block onto the wire shape.</summary>
    /// <remarks>Call this in client code, the loadgen driver, never in the workflow: the config
    /// read has to happen once, before the workflow exists.</remarks>
    public static SimpleActivityInput From(SimpleActivityConfig simpleActivity)
    {
        ArgumentNullException.ThrowIfNull(simpleActivity);

        // Named arguments: Latitude and Longitude are adjacent doubles, so swapping them
        // compiles clean and any config with both inside [-90, 90] fetches the wrong city.
        return new SimpleActivityInput(
            SleepDurationMs: (int)simpleActivity.SleepDuration.TotalMilliseconds,
            Latitude: simpleActivity.Latitude,
            Longitude: simpleActivity.Longitude,
            Activity: SimpleActivityOptionsInput.From(simpleActivity));
    }
}

/// <summary>The activity's timeouts, retry policy and HTTP deadline, carried in the workflow input.</summary>
/// <remarks>
/// A separate record from <see cref="ActivityOptionsInput"/>, which would drag HeartbeatTimeoutMs
/// and ScheduleToCloseTimeoutMs into the payload and then into the ActivityOptions; the absence of
/// both is what this case demonstrates. For why options travel in the input, see
/// <see cref="ActivityOptionsInput"/>. <see cref="HttpTimeoutMs"/> travels here for the same
/// reason: the activity's request deadline derives from it. The defaults below are the shipped
/// config.yaml values and are what a null Activity falls back to; change config.yaml, not these.
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

        // Named. RetryMaximumIntervalMs and RetryMaximumAttempts are adjacent ints, and swapping
        // them gives a 3ms maximum interval and 10,000 attempts against a third-party endpoint,
        // which is what ConfigLoader.ValidateSimpleActivity refuses to let through config.
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
/// <param name="Source">Which path produced this reading; see <c>MetricNames.Sources</c>. "synthetic" is the difference between a working demo and one whose network is down, and both report outcome="completed".</param>
/// <param name="TemperatureCelsius">Open-Meteo's <c>current.temperature_2m</c>.</param>
/// <param name="TemperatureUnit">From <c>current_units</c>, or a fallback. Cosmetic.</param>
/// <param name="WindSpeedKmh">Open-Meteo's <c>current.wind_speed_10m</c>.</param>
/// <param name="WindSpeedUnit">From <c>current_units</c>, or a fallback. Cosmetic.</param>
/// <param name="ObservedAt">Open-Meteo's <c>current.time</c>, verbatim as a string. Not a DateTime: the API returns "2026-08-31T14:30" with no offset, so parsing it invents a timezone.</param>
/// <param name="HttpElapsedMs">How long the request took, so the completed event tells a real fetch from a synthetic one without reading <paramref name="Source"/>.</param>
/// <remarks>
/// The canonical statement of the return-record rule; the other return records point here. The
/// payload is name-keyed on the wire, so a parameter name is the contract. Measured against
/// Temporalio 1.18.0: rename a parameter and it binds nothing, the value arrives as
/// <c>default(T)</c>, every fixture still reports "replay OK" and the tests stay green. A renamed
/// TemperatureCelsius reads a plausible 0.0 degrees. Reordering and removing are invisible to
/// replay: swapping TemperatureCelsius with WindSpeedKmh still deserializes, and so does dropping
/// HttpElapsedMs. The C# side constrains those instead, since the type sequence is (string, double,
/// string, double, string, string, int) and swapping the two doubles compiles clean; both 7-arg
/// construction sites in <c>WeatherActivities</c> use named arguments. The positional defaults do
/// not help: with every default stripped the same payload still deserializes and Temperature still
/// reads 0, because RespectRequiredConstructorParameters is off by default.
/// </remarks>
public record WeatherReading(
    string Source = "",
    double TemperatureCelsius = 0,
    string TemperatureUnit = "",
    double WindSpeedKmh = 0,
    string WindSpeedUnit = "",
    string ObservedAt = "",
    int HttpElapsedMs = 0);
