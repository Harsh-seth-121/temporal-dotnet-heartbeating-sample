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
/// Register this as an INSTANCE with its OWN
/// <c>.AddAllActivities(new WeatherActivities(cfg.SimpleActivity))</c> call, alongside
/// HeartbeatActivities' rather than as a second argument. AddAllActivities takes exactly one
/// instance.
/// <para>
/// THE SYNTHETIC FALLBACK IS A DELIBERATE EXCEPTION to this repo's doctrine that a broken
/// thing must never look like a working one. A network outage here produces
/// outcome="completed" and a green board. Four things keep it honest and all four are
/// load-bearing. Source is a field in the returned payload, so it is in the history. Source
/// is a metric label, so it is on the dashboard. The fallback logs at WARNING. And it covers
/// TRANSPORT failure only, so a server that answered is never smoothed over.
/// <c>simpleActivity.requireLiveWeather</c> turns it off entirely.
/// </para>
/// <para>
/// NOTHING IS INJECTED EXCEPT INFRASTRUCTURE, which departs from
/// <c>HeartbeatActivities(FaultConfig, WorkerConfig?)</c> on purpose. That precedent exists
/// so workflow code cannot reach the fault config, which is a determinism rule. Here the
/// timeouts, the sleep and the coordinates travel in the workflow INPUT, because they are job
/// shape and a captured history should say what the run was asked to do. That leaves
/// <see cref="SimpleActivityConfig.BaseUrl"/> and
/// <see cref="SimpleActivityConfig.RequireLiveWeather"/>, infrastructure and policy, to
/// arrive through the constructor on exactly the channel FaultConfig uses.
/// </para>
/// <para>
/// Wall clock, real sockets and Stopwatch are all fine, because this is activity code. That
/// licence is why the sleep lives here rather than in a workflow timer.
/// </para>
/// <para>
/// The wire name is <c>FetchWeather</c>, because the SDK trims the <c>Async</c> suffix. It
/// becomes the <c>activity_type</c> root tag, which is how every built-in SDK panel separates
/// this from <c>ProcessBatch</c> without a single custom metric.
/// </para>
/// </remarks>
public sealed class WeatherActivities(SimpleActivityConfig simpleActivity)
{
    /// <summary>Headroom subtracted from start-to-close when deriving the HTTP deadline.</summary>
    /// <remarks>
    /// Covers activity-task scheduling, payload conversion, and the gap between the server
    /// starting its start-to-close timer and Task.Delay starting ours.
    /// </remarks>
    private static readonly TimeSpan HttpHeadroom = TimeSpan.FromSeconds(2);

    /// <summary>
    /// One client for the life of the process, with a bounded connection lifetime and no
    /// timeout of its own.
    /// </summary>
    /// <remarks>
    /// STATIC because of an analyzer, not a preference. CA1001 says a type owning a disposable
    /// instance field must implement IDisposable, and it is an ERROR at this repo's settings,
    /// so an HttpClient instance field would force IDisposable onto an activity class the SDK
    /// never disposes. It would also buy nothing. AddAllActivities registers ONE instance for
    /// the life of the worker, so an instance field has exactly this lifetime anyway.
    /// <para>
    /// <c>Timeout.InfiniteTimeSpan</c> is NOT "no timeout". It moves the deadline to a
    /// per-call linked CancellationTokenSource, and it has to. <c>HttpClient.Timeout</c> is a
    /// single instance-wide value that throws InvalidOperationException if changed after the
    /// first request, so it cannot express a deadline that arrived in the workflow input. The
    /// linked CTS in <see cref="FetchWeatherAsync"/> is therefore MANDATORY, not defensive.
    /// Forget it and a hung connection runs until start-to-close.
    /// </para>
    /// <para>
    /// <c>PooledConnectionLifetime</c> is the singleton's price. A long-lived HttpClient pins
    /// its connections and therefore its resolved DNS forever. That is the classic failure
    /// IHttpClientFactory exists to solve: it keeps talking to a stale IP after the endpoint
    /// moves, with no error anywhere. Two minutes bounds it.
    /// </para>
    /// <para>
    /// Do NOT copy the <c>using var http = new HttpClient()</c> pattern from
    /// PushMetrics.DeleteGroupAsync. That one runs once per process, so it gets away with it.
    /// This runs on a loadgen loop for the length of a demo, and every
    /// <c>new HttpClient()</c> brings its own handler, its own pool and a fresh TCP+TLS
    /// handshake, leaving sockets in TIME_WAIT for minutes. Ephemeral-port starvation showing
    /// up as a SocketException twenty minutes into a working demo is exactly the
    /// silent-failure class this repo exists to document.
    /// </para>
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

        // Capture ONCE. ActivityExecutionContext.Current is an AsyncLocal lookup that throws
        // outside an activity, which matters the moment any of this moves into a Task.Run or a
        // continuation.
        var ctx = ActivityExecutionContext.Current;
        var log = ctx.Logger;

        log.LogInformation(
            "sleeping {SleepMs}ms, then fetching weather for {Latitude},{Longitude} (attempt {Attempt})",
            input.SleepDurationMs, input.Latitude, input.Longitude, ctx.Info.Attempt);

        // THE sleep. It is why this workflow is worth having.
        //
        // The token is forwarded because CA2016 is an error here, and because it is the only
        // way this activity can observe cancellation at all. With no heartbeat timeout the
        // SERVER has no channel to deliver one, so whatever reaches this token comes from the
        // worker side: graceful shutdown, and possibly the SDK's own start-to-close
        // bookkeeping.
        //
        // What IS measured: an attempt is not unbounded. With SleepDurationMs 60000 against
        // startToClose 30s, the server timed out each attempt at ~30s and retried, ending
        // ACTIVITY_TASK_TIMED_OUT / TIMEOUT_TYPE_START_TO_CLOSE with
        // RETRY_STATE_MAXIMUM_ATTEMPTS_REACHED after 3 attempts. Whether the local token also
        // fires a few seconds later is NOT settled by that run, because the server-side
        // timeout preempts it. This comment does not claim it either way.
        await Task.Delay(TimeSpan.FromMilliseconds(input.SleepDurationMs), ctx.CancellationToken)
            .ConfigureAwait(false);

        // Our own deadline, strictly inside start-to-close, LINKED to ctx.CancellationToken so
        // a worker drain still aborts the request in flight.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
        deadline.CancelAfter(HttpBudget(ctx, input));

        // Set by FetchAsync the instant response HEADERS arrive. It is what makes "a server
        // that answered is never smoothed over" true rather than aspirational. Once the server
        // has answered, NOTHING below is eligible for the synthetic fallback, whatever
        // exception type the body phase produces.
        //
        // ResponsePhase's own remarks carry the measurement that made this a flag rather than
        // an exception-type test.
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

            // WARNING, not Information. This run is about to be reported `completed` and the
            // board is about to go green on a reading nobody measured.
            log.LogWarning(
                "open-meteo unreachable after {ElapsedMs}ms; returning a SYNTHETIC reading: {Message}",
                elapsedMs, e.Message);

            return Synthetic(elapsedMs);
        }
    }

    /// <summary>How long the HTTP call gets, strictly inside start-to-close.</summary>
    /// <remarks>
    /// Read <c>ctx.Info.StartToCloseTimeout</c> first, because that is what the SERVER
    /// actually applied, read back off the activity task. The input's copy is the fallback for
    /// a task that somehow carries no timeout. The two genuinely cannot disagree: both derive
    /// from the same <see cref="SimpleActivityOptionsInput"/>, both null-coalesce to the same
    /// defaults, and there is exactly one ExecuteActivityAsync call site for this activity.
    /// Nothing a client can put in the input separates them.
    /// <para>
    /// Aborting the request OURSELVES is what this method buys. We get a log line naming the
    /// elapsed time and a synthetic reading, instead of an opaque server-side TimeoutFailure
    /// that says nothing about which of sleep, DNS, TLS or response ran long.
    /// </para>
    /// <para>
    /// ConfigLoader.Validate requires startToCloseTimeout &gt;= sleepDuration + httpTimeout +
    /// 2s, so for any CONFIG-driven run the subtraction is positive and httpTimeout is the
    /// binding constraint.
    /// </para>
    /// <para>
    /// The floor still earns its place, because Validate constrains config.yaml and NOT the
    /// workflow input. A hand-written <c>SleepDurationMs</c> larger than startToCloseTimeout
    /// minus 2s drives the subtraction negative, and CancelAfter throws on a negative
    /// TimeSpan. That is reachable with <c>temporal workflow execute --input</c>, though no
    /// doc here teaches it. The floor also intercepts an exactly-zero budget, which
    /// CancelAfter accepts and fires immediately.
    /// </para>
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
        // CultureInfo.InvariantCulture earns its place here. A double formatted under a
        // comma-decimal culture becomes "47,6062", Open-Meteo answers HTTP 400, and the
        // activity throws that NON-retryably. The symptom is then "every run fails, but only
        // on machines whose locale differs from CI's". InvariantGlobalization=true in
        // Directory.Build.props already forces this, but that is a project property someone
        // can flip, and CA1305 is an error here for exactly this class of bug.
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{simpleActivity.BaseUrl}?latitude={input.Latitude}&longitude={input.Longitude}"
            + $"&current=temperature_2m,wind_speed_10m");

        // ResponseHeadersRead, NOT the default ResponseContentRead. This is the single most
        // load-bearing argument in the file. It buys the instant at which "the server
        // answered" is observable, and without that instant the synthetic fallback reports a
        // green run for a server that answered. Measured both ways. See ResponsePhase.
        //
        // The other half of the trade: with headers-only completion a connection dropped
        // mid-body escapes the stream read as HttpIOException instead of being wrapped into
        // HttpRequestException by the buffering path. See IsTransportFailure, which is where
        // that flips the classification from smoothed-over to retried.
        using var response = await Http
            .GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // The server answered. From here on the fallback is off, whatever goes wrong.
        phase.HeadersSeen = true;

        // NOT EnsureSuccessStatusCode(). That throws HttpRequestException, which is exactly
        // what IsTransportFailure treats as "offline". A 429 or a 500 would then be silently
        // reported as unreachable, the retry policy would never see it, and the panel would
        // show a green synthetic run against a server that was actively refusing us. Status
        // handling has to be explicit for the fallback to mean exactly one thing: the
        // TRANSPORT failed.
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;

            // 429 and 5xx are transient, so they stay RETRYABLE and simpleActivity.retry backs
            // off into them. It is the one place in this repo where you can watch a retry
            // policy do the job it exists for against a real remote service.
            //
            // Any other 4xx is OUR request being wrong: a latitude outside [-90,90], a renamed
            // `current=` field. Retrying three times only delays the diagnosis.
            //
            // Note: ApplicationFailureException also accepts nextRetryDelay, so a Retry-After
            // header on a 429 could drive the backoff exactly. Worth doing if this ever
            // becomes more than a demo.
            throw new ApplicationFailureException(
                $"open-meteo returned HTTP {status} for {url}",
                "OpenMeteoHttpStatus",
                nonRetryable: status is not 429 && status < 500);
        }

        // JsonDocument rather than JsonSerializer.Deserialize<T>, for four reasons. No DTO
        // whose only purpose is to be filled by reflection. Open-Meteo is snake_case, so a DTO
        // would need [JsonPropertyName] on every member or a naming policy plus a CACHED
        // JsonSerializerOptions, and CA1869 is an error here, so a per-call
        // `new JsonSerializerOptions()` would not even compile. A MISSING field stays
        // distinguishable from a field that is present and zero, which is the difference
        // between "the schema changed" and "it is 0 degrees". And the failure message can name
        // the exact field.
        await using var body = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument
            .ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);

        return Parse(doc.RootElement, (int)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
    }

    /// <summary>Pull the reading out of Open-Meteo's response shape.</summary>
    /// <remarks>
    /// EVERY read here is guarded on ValueKind first, and that guard is load-bearing rather
    /// than habit. MEASURED: <c>TryGetDouble</c> does NOT return false for a non-number. It
    /// runs the same CheckExpectedType as <c>GetDouble</c> and throws
    /// InvalidOperationException, and its bool return means only "the number did not fit a
    /// double". So <c>{"temperature_2m": null}</c> threw, the throw escaped
    /// <see cref="IsTransportFailure"/>'s default arm as a RETRYABLE failure, and the
    /// non-retryable OpenMeteoSchema throw below was unreachable for precisely the case its
    /// message describes. The cosmetic reads carry the same trap: a numeric
    /// <c>current.time</c> or a non-object <c>current_units</c> threw and discarded a real
    /// reading already in hand.
    /// <para>
    /// The <see cref="Unit"/> remark below has documented half of this trap since the file was
    /// written. It explains why it re-reads the root instead of threading a possibly absent
    /// JsonElement, and then the next line called TryGetProperty on a value whose kind it had
    /// not checked.
    /// </para>
    /// </remarks>
    private static WeatherReading Parse(JsonElement root, int elapsedMs)
    {
        // current.temperature_2m and current.wind_speed_10m are MANDATORY. A response that
        // parsed as JSON but does not carry both as NUMBERS means Open-Meteo changed its shape.
        // That is permanent, so it is non-retryable, so the retry budget is not spent on it.
        //
        // TryGetDouble is still the right call for the numbers: it distinguishes "present but
        // does not fit a double" from "absent". It just is not a kind check, which is what the
        // ValueKind tests in front of it are for.
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
                "open-meteo response has no numeric current.temperature_2m / current.wind_speed_10m; "
                + "the schema changed and retrying will not help",
                "OpenMeteoSchema",
                nonRetryable: true);
        }

        // current.time and current_units are COSMETIC: they only label the numbers in
        // `workflow show`. Nothing here may fail a run that already holds a real reading, so
        // absent, null AND wrong-typed all fall back rather than throw.
        var observedAt = current.TryGetProperty("time", out var time)
            && time.ValueKind is JsonValueKind.String
            ? time.GetString() ?? string.Empty
            : string.Empty;

        // NAMED arguments, not positional. The type sequence is
        // (string, double, string, double, string, string, int), so swapping the two doubles
        // positionally compiles clean and silently reports the wind speed as a temperature.
        // Naming them turns that class of edit into a compile error, which this repo prefers
        // to a comment wherever the choice exists.
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
    /// <remarks>
    /// Takes the root and re-reads current_units on each call rather than caching the element.
    /// TryGetProperty on a <c>default(JsonElement)</c> throws InvalidOperationException,
    /// because ValueKind.Undefined is not an empty object. That is the trap in threading a
    /// possibly-absent JsonElement through a helper.
    /// <para>
    /// Every step is kind-checked for the same reason, one level down. TryGetProperty throws
    /// on any non-object and GetString throws on any non-string, so a <c>current_units</c>
    /// that arrives as null, an array or a string would have thrown out of a method whose only
    /// job is to return a fallback. Purely cosmetic labels must never fail a run that already
    /// holds a real reading.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// FIXED numbers, not Random. A demo script or a CI assertion needs a stable value. The
    /// only thing that has to vary is <see cref="WeatherReading.Source"/>, and
    /// <c>Source == "synthetic"</c> is the assertion to write.
    /// <para>
    /// ObservedAt is formatted to the minute, invariant, in the same shape Open-Meteo returns,
    /// so `workflow show` output has ONE format for that field whichever path produced it.
    /// Wall clock is fine here, because this is activity code.
    /// </para>
    /// </remarks>
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
    /// THE MEASUREMENT THIS TYPE EXISTS FOR. It is why the synthetic fallback is gated on a
    /// flag rather than on an exception type, and this remark is the only copy. Its three
    /// readers point here.
    /// <para>
    /// Under the default <c>HttpCompletionOption.ResponseContentRead</c> the whole body is
    /// buffered before <c>GetAsync</c> returns, so a 200-then-slow-body threw
    /// TaskCanceledException out of <c>GetAsync</c> itself at 1004ms. That is byte-identical
    /// to an unreachable host, with no moment at which the status was observable. Under
    /// <c>ResponseHeadersRead</c>, <c>GetAsync</c> returned at 15ms with status 200, this flag
    /// was set, and the body then failed at 1004ms with the fallback correctly refusing to
    /// fire. Both readings are measured.
    /// </para>
    /// <para>
    /// A mutable holder rather than an <c>out</c> parameter because the flag has to cross an
    /// <c>await</c> in <see cref="FetchAsync"/>, and async methods cannot take <c>out</c> or
    /// <c>ref</c>. One field, one writer, no concurrency: <see cref="FetchWeatherAsync"/>
    /// creates it per attempt and the request that writes it is awaited before the catch
    /// filter reads it.
    /// </para>
    /// </remarks>
    private sealed class ResponsePhase
    {
        /// <summary>True once response headers have arrived, whatever the status code.</summary>
        public bool HeadersSeen { get; set; }
    }

    /// <summary>Does this exception mean "we never reached Open-Meteo", and nothing else?</summary>
    /// <param name="e">The exception that escaped the request.</param>
    /// <param name="cancellationRequested">
    /// <c>ActivityExecutionContext.Current.CancellationToken.IsCancellationRequested</c>, passed
    /// in rather than read here so this predicate is pure and unit-testable outside an activity.
    /// </param>
    /// <param name="sawResponse">
    /// Whether response headers arrived before the failure. When true the answer is always
    /// false: a server that answered is never smoothed over.
    /// </param>
    /// <remarks>
    /// THE TRAP is why this is a method rather than a catch clause. HttpClient wraps a
    /// cancellation of the token you handed it into a TaskCanceledException, which derives from
    /// OperationCanceledException. A bare <c>catch (TaskCanceledException)</c> around the
    /// request therefore swallows a REAL activity cancellation and returns a synthetic
    /// reading, making this activity uncancellable at worker drain. That is the same defect
    /// fault.ignoreCancellation demonstrates on purpose, arrived at by accident.
    /// <para>
    /// <paramref name="cancellationRequested"/> is the discriminator, NOT the exception's own
    /// CancellationToken property. The token that fired is the LINKED one, so comparing it to
    /// the activity's token never matches.
    /// </para>
    /// <para>
    /// <paramref name="sawResponse"/> is the second discriminator and it dominates the other
    /// two. Exception TYPE alone cannot separate "unreachable" from "answered then stalled",
    /// because a 200 with a slow body and a host that does not exist both arrive as
    /// TaskCanceledException once our deadline fires. <see cref="ResponsePhase"/> holds the
    /// measurement, and it is why <see cref="FetchAsync"/> requests headers-only completion.
    /// </para>
    /// <para>
    /// JsonException and HttpIOException are deliberately NOT here, and with headers-only
    /// completion that omission now means what it says. A garbled body reaches JsonException
    /// and a body truncated mid-stream reaches HttpIOException. Both fall through to
    /// <c>false</c> and the SDK retries them, which is correct, because both are transient and
    /// one retry costs one sleep. Note HttpIOException derives from IOException, not
    /// HttpRequestException, which is the opposite of what most people assume. Under the
    /// buffering default this was NOT true: HttpContent wrapped the truncation into
    /// HttpRequestException, which this method mapped to "unreachable" and the fallback then
    /// smoothed over. Both readings are measured.
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
            // DNS failure, connection refused, TLS failure, no route. THE offline / CI case.
            HttpRequestException => true,

            // Our own HTTP deadline elapsed before any header arrived.
            OperationCanceledException => !cancellationRequested,

            _ => false,
        };
    }
}
