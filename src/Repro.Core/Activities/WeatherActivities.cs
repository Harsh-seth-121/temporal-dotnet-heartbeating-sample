using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Repro.Core.Config;
using Repro.Core.Telemetry;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Repro.Core.Activities;

/// <summary>
/// One activity: sleep, then fetch the current weather. No heartbeats, no checkpoints, no
/// resume.
/// </summary>
/// <remarks>
/// Only infrastructure is injected; the timeouts, sleep and coordinates travel in the workflow
/// input. Wire name <c>FetchWeather</c>, the <c>activity_type</c> root tag SDK panels split on.
/// <para>
/// The synthetic fallback is a deliberate exception to this repo's rule that a broken thing
/// must never look like a working one: a network outage produces outcome="completed". Four
/// things keep it honest. Source is a payload field and a metric label, the fallback logs at
/// warning, and it covers transport failure only, so a server that answered is never smoothed
/// over. <c>simpleActivity.requireLiveWeather</c> turns it off.
/// </para>
/// </remarks>
public sealed class WeatherActivities(SimpleActivityConfig simpleActivity)
{
    /// <summary>Headroom subtracted from start-to-close when deriving the HTTP deadline.</summary>
    /// <remarks>Covers activity-task scheduling, payload conversion, and the gap between the
    /// server starting its start-to-close timer and Task.Delay starting ours.</remarks>
    private static readonly TimeSpan HttpHeadroom = TimeSpan.FromSeconds(2);

    /// <summary>One client for the life of the process, with a bounded connection lifetime and
    /// no timeout of its own.</summary>
    /// <remarks>
    /// Static because CA1001, an error here, would force IDisposable onto an activity class the
    /// SDK never disposes. <c>Timeout.InfiniteTimeSpan</c> moves the deadline to the per-call
    /// linked CancellationTokenSource in <see cref="FetchWeatherAsync"/>, which is mandatory:
    /// <c>HttpClient.Timeout</c> is instance-wide and throws InvalidOperationException if
    /// changed after the first request. <c>PooledConnectionLifetime</c> bounds the DNS a
    /// long-lived client would pin forever; a per-call <c>new HttpClient()</c> would starve the
    /// ephemeral port range.
    /// </remarks>
    private static readonly HttpClient Http = new(
        new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Sleep, then fetch. Wire name <c>FetchWeather</c>.</summary>
    [Activity]
    public async Task<WeatherReading> FetchWeatherAsync(SimpleActivityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Capture once; see HeartbeatActivities.ProcessBatchAsync.
        var ctx = ActivityExecutionContext.Current;
        var log = ctx.Logger;

        log.LogInformation(
            "sleeping {SleepMs}ms, then fetching weather for {Latitude},{Longitude} (attempt {Attempt})",
            input.SleepDurationMs, input.Latitude, input.Longitude, ctx.Info.Attempt);

        // The sleep this workflow exists for. With no heartbeat timeout the server has no
        // channel to deliver a cancellation, so anything reaching this token comes from the
        // worker side. Measured: with SleepDurationMs 60000 against startToClose 30s the server
        // timed out each attempt at ~30s and retried, ending ACTIVITY_TASK_TIMED_OUT /
        // TIMEOUT_TYPE_START_TO_CLOSE with RETRY_STATE_MAXIMUM_ATTEMPTS_REACHED after 3.
        await Task.Delay(TimeSpan.FromMilliseconds(input.SleepDurationMs), ctx.CancellationToken)
            .ConfigureAwait(false);

        // Our own deadline, strictly inside start-to-close, linked to ctx.CancellationToken so
        // a worker drain still aborts the request in flight.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
        deadline.CancelAfter(HttpBudget(ctx, input));

        // Set by FetchAsync the instant response headers arrive; from then on nothing is
        // eligible for the synthetic fallback. See ResponsePhase.
        var phase = new ResponsePhase();

        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var reading = await FetchAsync(input, startedAt, phase, deadline.Token).ConfigureAwait(false);

            log.LogInformation(
                "open-meteo: {Temperature}{TemperatureUnit}, wind {WindSpeed}{WindSpeedUnit} at " +
                "{ObservedAt}, in {ElapsedMs}ms",
                reading.TemperatureCelsius, reading.TemperatureUnit,
                reading.WindSpeedKmh, reading.WindSpeedUnit, reading.ObservedAt,
                reading.HttpElapsedMs);

            return reading;
        }
        catch (Exception e) when (!simpleActivity.RequireLiveWeather
            && IsTransportFailure(e, ctx.CancellationToken.IsCancellationRequested, phase.HeadersSeen))
        {
            var elapsedMs = (int)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            // Warning, not Information: this run is about to report completed and turn the
            // board green on a reading nobody measured.
            log.LogWarning(
                "open-meteo unreachable after {ElapsedMs}ms; returning a synthetic reading: {Message}",
                elapsedMs, e.Message);

            return Synthetic(elapsedMs);
        }
    }

    /// <summary>How long the HTTP call gets, strictly inside start-to-close.</summary>
    /// <remarks>
    /// <c>ctx.Info.StartToCloseTimeout</c> first, because it is what the server applied.
    /// Aborting ourselves buys a log line naming the elapsed time instead of an opaque
    /// server-side TimeoutFailure. ConfigLoader.Validate requires startToCloseTimeout &gt;=
    /// sleepDuration + httpTimeout + 2s but does not constrain the workflow input, so the floor
    /// covers a hand-written <c>SleepDurationMs</c> that drives the subtraction negative
    /// (CancelAfter throws) or to zero (CancelAfter fires immediately).
    /// </remarks>
    private static TimeSpan HttpBudget(ActivityExecutionContext ctx, SimpleActivityInput input)
    {
        var options = input.Activity ?? new SimpleActivityOptionsInput();

        var startToClose = ctx.Info.StartToCloseTimeout
            ?? TimeSpan.FromMilliseconds(options.StartToCloseTimeoutMs);

        var remaining = startToClose
            - TimeSpan.FromMilliseconds(input.SleepDurationMs)
            - HttpHeadroom;

        var configured = TimeSpan.FromMilliseconds(options.HttpTimeoutMs);

        var budget = remaining < configured ? remaining : configured;
        return budget > TimeSpan.Zero ? budget : TimeSpan.FromSeconds(1);
    }

    /// <summary>One request to Open-Meteo, parsed, or an exception describing why not.</summary>
    private async Task<WeatherReading> FetchAsync(
        SimpleActivityInput input, long startedAt, ResponsePhase phase, CancellationToken cancellationToken)
    {
        // InvariantCulture: under a comma-decimal culture a double formats as "47,6062" and
        // Open-Meteo answers HTTP 400, so runs fail only on machines whose locale differs from
        // CI's. InvariantGlobalization=true also forces it, but that is a flippable property.
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{simpleActivity.BaseUrl}?latitude={input.Latitude}&longitude={input.Longitude}"
            + $"&current=temperature_2m,wind_speed_10m");

        // ResponseHeadersRead, not the default ResponseContentRead: it buys the instant at
        // which "the server answered" is observable (see ResponsePhase), at the price of a
        // mid-body drop escaping as HttpIOException (see IsTransportFailure).
        using var response = await Http
            .GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // The server answered. From here on the fallback is off, whatever goes wrong.
        phase.HeadersSeen = true;

        // Not EnsureSuccessStatusCode(): it throws HttpRequestException, which
        // IsTransportFailure treats as offline, so a 429 or 500 would report a green synthetic
        // run against a server that was actively refusing us.
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;

            // 429 and 5xx are transient, so they stay retryable and simpleActivity.retry backs
            // off into them. Any other 4xx is our request being wrong. ApplicationFailureException
            // also accepts nextRetryDelay, so a Retry-After header could drive the backoff.
            throw new ApplicationFailureException(
                $"open-meteo returned HTTP {status} for {url}",
                "OpenMeteoHttpStatus",
                nonRetryable: status is not 429 && status < 500);
        }

        // JsonDocument rather than JsonSerializer.Deserialize<T>: Open-Meteo is snake_case, so
        // a DTO needs a naming policy plus a cached JsonSerializerOptions (CA1869 is an error
        // here). A missing field also stays distinguishable from one present and zero.
        await using var body = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument
            .ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);

        return Parse(doc.RootElement, (int)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
    }

    /// <summary>Pull the reading out of Open-Meteo's response shape.</summary>
    /// <remarks>Every read is guarded on ValueKind first. Measured: <c>TryGetDouble</c> does
    /// not return false for a non-number, it throws InvalidOperationException, and its bool
    /// return means only "the number did not fit a double". So <c>{"temperature_2m": null}</c>
    /// threw, escaped as a retryable failure, and left the non-retryable OpenMeteoSchema throw
    /// below unreachable.</remarks>
    private static WeatherReading Parse(JsonElement root, int elapsedMs)
    {
        // A response that parsed as JSON without both as numbers means Open-Meteo changed its
        // shape, which is permanent, so it is non-retryable. TryGetDouble still belongs here
        // because it separates "present but does not fit a double" from "absent".
        if (root.ValueKind is not JsonValueKind.Object
            || !root.TryGetProperty("current", out var current)
            || current.ValueKind is not JsonValueKind.Object
            || !current.TryGetProperty("temperature_2m", out var temp)
            || temp.ValueKind is not JsonValueKind.Number
            || !temp.TryGetDouble(out var celsius)
            || !current.TryGetProperty("wind_speed_10m", out var wind)
            || wind.ValueKind is not JsonValueKind.Number
            || !wind.TryGetDouble(out var kmh))
        {
            throw new ApplicationFailureException(
                "open-meteo response has no numeric current.temperature_2m / "
                + "current.wind_speed_10m: the schema changed",
                "OpenMeteoSchema",
                nonRetryable: true);
        }

        // Cosmetic: these only label the numbers in `workflow show`, so absent, null and
        // wrong-typed all fall back rather than fail a run holding a real reading.
        var observedAt = current.TryGetProperty("time", out var time)
            && time.ValueKind is JsonValueKind.String
            ? time.GetString() ?? string.Empty
            : string.Empty;

        // Named arguments: swapping the two doubles positionally compiles clean.
        return new WeatherReading(
            Source: MetricNames.Sources.OpenMeteo,
            TemperatureCelsius: celsius,
            TemperatureUnit: Unit(root, "temperature_2m", "°C"),
            WindSpeedKmh: kmh,
            WindSpeedUnit: Unit(root, "wind_speed_10m", "km/h"),
            ObservedAt: observedAt,
            HttpElapsedMs: elapsedMs);
    }

    /// <summary>A unit string out of <c>current_units</c>, or the fallback.</summary>
    /// <remarks>Re-reads current_units from the root rather than caching the element:
    /// TryGetProperty on a <c>default(JsonElement)</c> throws InvalidOperationException,
    /// because ValueKind.Undefined is not an empty object. Every step is kind-checked for the
    /// same reason, since TryGetProperty throws on a non-object.</remarks>
    private static string Unit(JsonElement root, string key, string fallback) =>
        root.ValueKind is JsonValueKind.Object
        && root.TryGetProperty("current_units", out var units)
        && units.ValueKind is JsonValueKind.Object
        && units.TryGetProperty(key, out var unit)
        && unit.ValueKind is JsonValueKind.String
        && unit.GetString() is { Length: > 0 } value
            ? value
            : fallback;

    /// <summary>The stand-in returned when the transport failed.</summary>
    /// <remarks>Fixed numbers, not Random, so a CI assertion has a stable value;
    /// <c>Source == "synthetic"</c> is the assertion to write. ObservedAt is formatted to the
    /// minute in the shape Open-Meteo returns, so `workflow show` shows one format.</remarks>
    private static WeatherReading Synthetic(int elapsedMs) =>
        new(
            Source: MetricNames.Sources.Synthetic,
            TemperatureCelsius: 15.0,
            TemperatureUnit: "°C",
            WindSpeedKmh: 10.0,
            WindSpeedUnit: "km/h",
            ObservedAt: DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            HttpElapsedMs: elapsedMs);

    /// <summary>Tracks whether the server has answered yet.</summary>
    /// <remarks>
    /// Canonical site for why the synthetic fallback is gated on a flag, not an exception type.
    /// Measured both ways: under the default <c>HttpCompletionOption.ResponseContentRead</c> a
    /// 200-then-slow-body threw TaskCanceledException out of <c>GetAsync</c> at 1004ms,
    /// indistinguishable from an unreachable host. Under <c>ResponseHeadersRead</c> it returned
    /// at 15ms with status 200, this flag was set, and the body failed at 1004ms with the
    /// fallback correctly refusing to fire. A mutable holder because it crosses an await.
    /// </remarks>
    private sealed class ResponsePhase
    {
        /// <summary>True once response headers have arrived, whatever the status code.</summary>
        public bool HeadersSeen { get; set; }
    }

    /// <summary>Does this exception mean "we never reached Open-Meteo", and nothing else?</summary>
    /// <param name="e">The exception that escaped the request.</param>
    /// <param name="cancellationRequested">
    /// The activity token's <c>IsCancellationRequested</c>, passed in so this predicate is pure
    /// and testable outside an activity.
    /// </param>
    /// <param name="sawResponse">Whether response headers arrived before the failure.</param>
    /// <remarks>
    /// A method rather than a catch clause because HttpClient wraps a cancellation of the token
    /// you handed it into TaskCanceledException, so a bare <c>catch (TaskCanceledException)</c>
    /// would swallow a real activity cancellation and make this uncancellable at worker drain.
    /// <paramref name="cancellationRequested"/> is the discriminator rather than the
    /// exception's own CancellationToken property, because the token that fired is the linked
    /// one. <paramref name="sawResponse"/> dominates both: a 200 with a slow body and a host
    /// that does not exist both arrive as TaskCanceledException once our deadline fires.
    /// <para>
    /// JsonException and HttpIOException are deliberately absent: a garbled body and one
    /// truncated mid-stream fall through to <c>false</c> and the SDK retries them.
    /// HttpIOException derives from IOException, not HttpRequestException, so under the
    /// buffering default HttpContent wrapped the truncation into HttpRequestException and this
    /// method mapped it to unreachable.
    /// </para>
    /// </remarks>
    public static bool IsTransportFailure(Exception e, bool cancellationRequested, bool sawResponse)
    {
        if (sawResponse)
        {
            return false;
        }

        return e switch
        {
            // DNS failure, connection refused, TLS failure, no route. The offline/CI case.
            HttpRequestException => true,

            // Our own HTTP deadline elapsed before any header arrived.
            OperationCanceledException => !cancellationRequested,

            _ => false,
        };
    }
}
