using System.Text.Json;
using Repro.Core.Activities;
using Temporalio.Exceptions;
using Xunit;

namespace Repro.Tests;

/// <summary>
/// Locks the one decision that separates "the endpoint is unreachable" from "the endpoint
/// answered and then went wrong".
/// </summary>
/// <remarks>
/// This is the only branch in the repo that can turn a failure into a GREEN run, so it is
/// the only one where a wrong answer is invisible. It earns a test for the same reason
/// <c>ConfigTests</c> and <c>TelemetryTests</c> do: the failure looks like a working system.
/// <para>
/// The predicate is pure on purpose. It takes <c>cancellationRequested</c> and
/// <c>sawResponse</c> as parameters rather than reading
/// <c>ActivityExecutionContext.Current</c>, so it is testable without an activity
/// environment, a worker, or a network. That shape was chosen to make this file possible.
/// </para>
/// <para>
/// MEASURED end to end against a live worker before these cases were written: a server that
/// answers 200 and then stalls its body now fails after 3 attempts in 27.1s with no
/// synthetic reading, a connection-refused endpoint still returns synthetic in 21ms, and the
/// live path still returns a real reading in 5.8s.
/// </para>
/// </remarks>
public class WeatherActivitiesTests
{
    /// <summary>An unreachable endpoint is the case the synthetic fallback exists for.</summary>
    [Fact]
    public void UnreachableEndpointIsATransportFailure() =>
        Assert.True(WeatherActivities.IsTransportFailure(
            new HttpRequestException("Connection refused"), cancellationRequested: false, sawResponse: false));

    /// <summary>Our own HTTP deadline, before any header arrived, is also transport.</summary>
    [Fact]
    public void OurDeadlineBeforeAnyHeaderIsATransportFailure() =>
        Assert.True(WeatherActivities.IsTransportFailure(
            new TaskCanceledException(), cancellationRequested: false, sawResponse: false));

    /// <summary>
    /// THE REGRESSION THIS FILE EXISTS FOR: a server that answered is never smoothed over.
    /// </summary>
    /// <remarks>
    /// Before the fix, the deadline token covered the body read and the parse, and the
    /// default HttpCompletionOption buffered the whole body inside GetAsync. A 200 with a slow
    /// body therefore raised TaskCanceledException with no observable status, and it was
    /// reported as a green synthetic run. Both of these cases returned true. Now both return
    /// false, and the invariant is true rather than aspirational. Grep `smoothed over` for
    /// the three places that state it: WeatherActivities.cs's class remark, config.yaml's
    /// simpleActivity block, and docs/CONFIG.md's fallback list. Those are phrase anchors
    /// rather than line numbers on purpose, because line numbers stale silently.
    /// </remarks>
    [Theory]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(HttpRequestException))]
    public void AServerThatAnsweredIsNeverSmoothedOver(Type exceptionType)
    {
        var e = (Exception)Activator.CreateInstance(exceptionType)!;
        Assert.False(WeatherActivities.IsTransportFailure(e, cancellationRequested: false, sawResponse: true));
    }

    /// <summary>
    /// A REAL activity cancellation must propagate, never become a synthetic reading.
    /// </summary>
    /// <remarks>
    /// HttpClient wraps a cancellation of the token you handed it into TaskCanceledException,
    /// which derives from OperationCanceledException. A bare catch on either type therefore
    /// makes the activity uncancellable at worker drain. That is the defect
    /// fault.ignoreCancellation demonstrates on purpose, and this asserts it is not reached by
    /// accident.
    /// </remarks>
    [Fact]
    public void WorkerDrainCancellationIsNotATransportFailure() =>
        Assert.False(WeatherActivities.IsTransportFailure(
            new TaskCanceledException(), cancellationRequested: true, sawResponse: false));

    /// <summary>
    /// A body that arrived and did not parse, or stopped mid-stream, is transient: retry it.
    /// </summary>
    /// <remarks>
    /// HttpIOException derives from IOException, not HttpRequestException, which is the
    /// opposite of what most people assume. Under the old buffering read mode a truncated body
    /// never arrived as one at all, because HttpContent wrapped it into HttpRequestException
    /// and it was smoothed over. Headers-only completion is what lets it escape and retry.
    /// </remarks>
    [Theory]
    [InlineData(typeof(JsonException))]
    [InlineData(typeof(HttpIOException))]
    [InlineData(typeof(InvalidOperationException))]
    public void AnArrivedButBrokenBodyIsRetryable(Type exceptionType)
    {
        var e = exceptionType == typeof(HttpIOException)
            ? new HttpIOException(HttpRequestError.ResponseEnded)
            : (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.False(WeatherActivities.IsTransportFailure(e, cancellationRequested: false, sawResponse: false));
    }

    /// <summary>A non-success status is an application failure and must never be smoothed over.</summary>
    [Fact]
    public void AnApplicationFailureIsNeverATransportFailure() =>
        Assert.False(WeatherActivities.IsTransportFailure(
            new ApplicationFailureException("open-meteo returned HTTP 500", "OpenMeteoHttpStatus"),
            cancellationRequested: false,
            sawResponse: false));
}
