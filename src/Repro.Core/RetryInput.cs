using Temporalio.Common;

namespace Repro.Core;

/// <summary>
/// The four retry fields every activity-options input in this repo carries, so the
/// <c>RetryPolicy</c> they describe can be built in ONE place.
/// </summary>
/// <remarks>
/// FOUR IDENTICAL COPIES of the construction existed, one per workflow, and the float cast below
/// is the reason to have only one: it is not obvious, it is easy to drop, and dropping it does
/// not fail the build in an obvious place.
/// <para>
/// AN INTERFACE RATHER THAN A SHARED RECORD, deliberately. Each input record keeps its own
/// declaration and its own DEFAULTS -- 5 attempts for the heartbeat case, 3 for the weather one,
/// 10 for the file scan, 1 for the local activity -- because those defaults are the contract
/// with the captured histories in <c>history/</c>: a field's default is what a payload recorded
/// before that field existed deserializes to. Folding the four records into one would rewrite
/// four replay contracts at once. This interface adds no field and changes no payload; it only
/// names the accessors the four records already had.
/// </para>
/// </remarks>
public interface IRetryInput
{
    /// <summary>First retry delay, in milliseconds.</summary>
    int RetryInitialIntervalMs { get; }

    /// <summary>Multiplier applied to the interval after each attempt.</summary>
    double RetryBackoffCoefficient { get; }

    /// <summary>Ceiling on the backed-off interval, in milliseconds.</summary>
    int RetryMaximumIntervalMs { get; }

    /// <summary>Attempt cap. NEVER 0 -- zero means UNLIMITED in <see cref="RetryPolicy"/>.</summary>
    int RetryMaximumAttempts { get; }
}

/// <summary>Builds the <see cref="RetryPolicy"/> an <see cref="IRetryInput"/> describes.</summary>
public static class RetryInputExtensions
{
    /// <summary>Projects the four wire fields onto the SDK's policy type.</summary>
    /// <remarks>
    /// Called from WORKFLOW code, so it must stay a pure projection: no clock, no randomness, no
    /// process state. It reads four fields off the input and constructs one object.
    /// </remarks>
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
