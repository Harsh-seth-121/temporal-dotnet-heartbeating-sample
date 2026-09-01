namespace Repro.LoadGen;

/// <summary>The jittered-interval formula, shared by every driver loop that has one.</summary>
/// <remarks>
/// Extracted rather than copied, and not for the eight lines. The formula's
/// correctness rests on a rule enforced in a THIRD file: ConfigLoader.Validate rejecting
/// jitter outside [0, 1) and rate at or below zero, which is why the floor below is
/// belt-and-braces rather than the thing standing between you and a spin loop. THREE config
/// blocks are validated by that rule now (<c>simple.jitter</c>, <c>simpleActivity.jitter</c>
/// and <c>localActivity.jitter</c>), so three copies of the formula plus three copies of the
/// prose explaining which rule makes it safe is how the next change to the jitter contract
/// lands in one loop and not the other two. The symptom is a busy spin against the frontend
/// in exactly one driver.
/// <para>
/// Client code, so Random.Shared is fine. Nothing here may leak into workflow code.
/// </para>
/// </remarks>
internal static class Jitter
{
    /// <summary><paramref name="rate"/> x [1-jitter, 1+jitter], floored at 1ms.</summary>
    /// <param name="rate">Mean interval. Validated to be above zero.</param>
    /// <param name="jitter">Fractional spread. Validated to be in [0, 1).</param>
    internal static TimeSpan NextInterval(TimeSpan rate, double jitter)
    {
        var factor = 1.0 + (jitter * ((2.0 * Random.Shared.NextDouble()) - 1.0));
        return TimeSpan.FromMilliseconds(Math.Max(1.0, rate.TotalMilliseconds * factor));
    }
}
