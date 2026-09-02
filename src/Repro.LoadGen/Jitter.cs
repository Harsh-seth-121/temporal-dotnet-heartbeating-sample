namespace Repro.LoadGen;

/// <summary>The jittered-interval formula, shared by every driver loop that has one.</summary>
/// <remarks>
/// What keeps this safe lives in a third file: ConfigLoader.Validate rejects jitter outside
/// [0, 1) and rate at or below zero for all four blocks carrying them. The 1ms floor below is
/// belt and braces, not what stands between you and a spin loop.
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
