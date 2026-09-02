using Temporalio.Common;

namespace Repro.Core;

/// <summary>
/// The four retry fields every activity-options input in this repo carries, so the
/// <c>RetryPolicy</c> they describe is built in one place.
/// </summary>
/// <remarks>An interface rather than a shared record, because each input record's defaults are its
/// contract with the captured histories in <c>history/</c>: a field's default is what a payload
/// recorded before that field existed deserializes to, and the four cases differ (5 attempts, 3,
/// 10, 1). This interface adds no field and changes no payload.</remarks>
public interface IRetryInput
{
    /// <summary>First retry delay, in milliseconds.</summary>
    int RetryInitialIntervalMs { get; }

    /// <summary>Multiplier applied to the interval after each attempt.</summary>
    double RetryBackoffCoefficient { get; }

    /// <summary>Ceiling on the backed-off interval, in milliseconds.</summary>
    int RetryMaximumIntervalMs { get; }

    /// <summary>Attempt cap. Never 0: zero means unlimited in <see cref="RetryPolicy"/>.</summary>
    int RetryMaximumAttempts { get; }
}

/// <summary>Builds the <see cref="RetryPolicy"/> an <see cref="IRetryInput"/> describes.</summary>
public static class RetryInputExtensions
{
    /// <summary>Projects the four wire fields onto the SDK's policy type.</summary>
    /// <remarks>Called from workflow code, so it must stay a pure projection: no clock, no
    /// randomness, no process state.</remarks>
    public static RetryPolicy ToRetryPolicy(this IRetryInput retry)
    {
        ArgumentNullException.ThrowIfNull(retry);

        return new RetryPolicy
        {
            InitialInterval = TimeSpan.FromMilliseconds(retry.RetryInitialIntervalMs),

            // float, not double. Temporalio.Common.RetryPolicy takes a float and config.yaml's
            // 2.0 is parsed as a double.
            BackoffCoefficient = (float)retry.RetryBackoffCoefficient,
            MaximumInterval = TimeSpan.FromMilliseconds(retry.RetryMaximumIntervalMs),
            MaximumAttempts = retry.RetryMaximumAttempts,
        };
    }
}
