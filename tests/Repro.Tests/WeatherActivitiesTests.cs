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
/// The only branch in the repo that can turn a failure into a green run, so a wrong answer here
/// is invisible. The predicate takes <c>cancellationRequested</c> and <c>sawResponse</c> as
/// parameters rather than reading <c>ActivityExecutionContext.Current</c>, so it needs no
/// activity environment, worker or network. Against a live worker: a 200 that stalls its body
/// fails after 3 attempts in 27.1s with no synthetic reading, a connection-refused endpoint
/// returns synthetic in 21ms, and the live path returns a real reading in 5.8s.
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

    /// <summary>The regression this file exists for: a server that answered is never smoothed
    /// over.</summary>
    /// <remarks>Before the fix the deadline token covered the body read and the parse, and the
    /// default HttpCompletionOption buffered the whole body inside GetAsync, so a 200 with a slow
    /// body raised TaskCanceledException with no observable status and was reported as a green
    /// synthetic run. Grep "smoothed over" for the other three statements of the rule.</remarks>
    [Theory]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(HttpRequestException))]
    public void AServerThatAnsweredIsNeverSmoothedOver(Type exceptionType)
    {
        var e = (Exception)Activator.CreateInstance(exceptionType)!;
        Assert.False(WeatherActivities.IsTransportFailure(e, cancellationRequested: false, sawResponse: true));
    }

    /// <summary>A real activity cancellation must propagate, never become a synthetic reading.</summary>
    /// <remarks>HttpClient wraps a cancellation of the token you handed it into
    /// TaskCanceledException, which derives from OperationCanceledException, so a bare catch on
    /// either type makes the activity uncancellable at worker drain. That is the defect
    /// fault.ignoreCancellation demonstrates on purpose.</remarks>
    [Fact]
    public void WorkerDrainCancellationIsNotATransportFailure() =>
        Assert.False(WeatherActivities.IsTransportFailure(
            new TaskCanceledException(), cancellationRequested: true, sawResponse: false));

    /// <summary>A body that arrived and did not parse, or stopped mid-stream, is retryable.</summary>
    /// <remarks>HttpIOException derives from IOException, not HttpRequestException. Under the old
    /// buffering read mode HttpContent wrapped a truncated body into HttpRequestException and it
    /// was smoothed over; headers-only completion is what lets it escape and retry.</remarks>
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
