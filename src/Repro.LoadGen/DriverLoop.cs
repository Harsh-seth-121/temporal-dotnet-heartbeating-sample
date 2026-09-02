namespace Repro.LoadGen;

/// <summary>
/// The tick loop all four drivers share: start one run per <see cref="Jitter.NextInterval"/>,
/// skip instead of queueing at capacity, and never let a run's exception reach the finalizer.
/// </summary>
/// <remarks>
/// The one place the pacing contract is written down; the drivers reference it. Skipping rather
/// than queueing keeps <c>rate</c> describing what the process does, and how often a skip fires
/// is per-case: near never for the simple loop, most ticks for the local-activity one. No
/// SemaphoreSlim, because a <c>using var slots</c> is disposed while fire-and-forget run bodies
/// still call Release() in a finally. All client code, and none of it may leak into a workflow.
/// </remarks>
/// <typeparam name="TRun">
/// What one run needs, drawn per tick: a prebuilt input for the three identical cases, the drawn
/// burn duration and seed for the local-activity one.
/// </typeparam>
internal sealed class DriverLoop<TRun>(TimeSpan rate, double jitter, int concurrency)
{
    private int inFlight;
    private int started;
    private int skipped;
    private int interrupted;
    private int failed;

    /// <summary>Runs started, including those that later failed. For the driver's summary line.</summary>
    public int Started => started;

    /// <summary>Ticks that found the loop at capacity and started nothing.</summary>
    public int Skipped => skipped;

    /// <summary>Runs whose RPCs were cancelled because the process is going down.</summary>
    public int Interrupted => interrupted;

    /// <summary>Runs that threw for any other reason.</summary>
    public int Failed => failed;

    /// <param name="draw">Produces the payload for one run. Called on the loop, once per tick.</param>
    /// <param name="runOnce">Starts that run and waits for it. Called on a pool thread.</param>
    /// <param name="logFailure">One line. Called only when the token is not cancelled.</param>
    /// <param name="logSummary">
    /// Called every ten starts and once after the loop ends. Every driver's template is
    /// concatenated string literals: CA2254 wants a constant message, CA1727 PascalCase
    /// placeholders, both errors here.
    /// </param>
    public async Task RunAsync(
        Func<TRun> draw,
        Func<TRun, CancellationToken, Task> runOnce,
        Action<TRun, Exception> logFailure,
        Action logSummary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(runOnce);
        ArgumentNullException.ThrowIfNull(logFailure);
        ArgumentNullException.ThrowIfNull(logSummary);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Task.Delay, not PeriodicTimer, whose period is fixed. The token is forwarded
                // so a shutdown does not wait out a full interval, six minutes for file-scan.
                await Task.Delay(
                    Jitter.NextInterval(rate, jitter), cancellationToken).ConfigureAwait(false);

                // Skip at capacity, never queue. See the class remarks.
                if (Interlocked.Increment(ref inFlight) > concurrency)
                {
                    Interlocked.Decrement(ref inFlight);
                    Interlocked.Increment(ref skipped);
                    continue;
                }

                var n = Interlocked.Increment(ref started);

                // Drawn on the loop, not inside Task.Run, so the classifier reads the same value
                // the run was given.
                var run = draw();

                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await runOnce(run, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            // A total catch: an unobserved TaskException tears down the process
                            // on finalization. Shutdown counts separately from failure, so a
                            // clean Ctrl-C does not read as breakage.
                            if (cancellationToken.IsCancellationRequested)
                            {
                                Interlocked.Increment(ref interrupted);
                            }
                            else
                            {
                                Interlocked.Increment(ref failed);
                                logFailure(run, e);
                            }
                        }
                        finally
                        {
                            Interlocked.Decrement(ref inFlight);
                        }
                    },
                    CancellationToken.None);

                if (n % 10 == 0)
                {
                    logSummary();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the shutdown token cancelled Task.Delay.
        }

        logSummary();
    }
}
