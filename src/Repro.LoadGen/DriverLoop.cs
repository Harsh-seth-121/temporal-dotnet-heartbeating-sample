namespace Repro.LoadGen;

/// <summary>
/// The tick loop all four drivers in this process run: start one run on a JITTERED interval,
/// skip instead of queueing at capacity, and never let a run's exception reach the finalizer.
/// </summary>
/// <remarks>
/// EXTRACTED BECAUSE ALL FOUR COPIES WERE IDENTICAL, line for line, from the Task.Delay down to
/// the summary after the loop. What actually differs per case is the payload drawn for a run,
/// the workflow started with it, and how an ending is classified -- so those are the only things
/// a driver passes in. The reason to pull it out is the one
/// <see cref="Repro.Core.Temporal.LocalActivityWorkerOptions"/> records for worker knobs: four
/// hand-copied blocks drift, and the drift is silent. The comments on the two copies of the
/// total-catch had already started to diverge before this was pulled out.
/// <para>
/// WHAT DELIBERATELY STAYS IN THE DRIVERS: the per-case counters (a scan completes, a pi run
/// times out at runTimeout, a simple run stops or cancels or expires), the summary line, and the
/// failure log template. Those are not boilerplate, they are what each case is about, and
/// folding them into one generic counter set is how a board stops meaning anything.
/// </para>
/// <para>
/// NO SemaphoreSlim, for the reason <see cref="SimpleDriver"/> records at length: the heartbeat
/// loop's <c>using var slots</c> is disposed while fire-and-forget run bodies are still calling
/// Release() in a finally. Interlocked counters have no disposal semantics at all.
/// </para>
/// <para>
/// Everything here is CLIENT code, so wall-clock and Random.Shared are fine at the call sites.
/// Nothing in this file may leak into workflow code.
/// </para>
/// </remarks>
/// <typeparam name="TRun">
/// What one run needs, drawn per tick. A prebuilt input for the three cases where every run is
/// identical; the drawn burn duration and seed for the local-activity case, which is why this is
/// generic rather than a plain <c>Func&lt;CancellationToken, Task&gt;</c>: the failure log for
/// that case names the duration that was asked for, so the classifier has to see the draw.
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
    /// <param name="logFailure">
    /// Logs a GENUINE failure, one line. Called only when the token is not cancelled, so it never
    /// has to distinguish breakage from a clean shutdown -- this loop already did.
    /// </param>
    /// <param name="logSummary">Called every ten starts and once more after the loop ends.</param>
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
                // Task.Delay, not PeriodicTimer: a PeriodicTimer has one fixed period and the
                // period has to vary here. The token is forwarded because CA2016 is an error in
                // this repo, and because without it a shutdown waits out a full interval -- which
                // at the file-scan loop's shipped rate is six minutes. See Jitter for why the
                // formula lives in one place and which validation rule keeps it safe.
                await Task.Delay(
                    Jitter.NextInterval(rate, jitter), cancellationToken).ConfigureAwait(false);

                // SKIP at capacity, never queue. Queueing would build an unbounded backlog and
                // `rate` would stop describing what the process is doing. How often this fires is
                // per-case and documented on each driver: near never for the simple loop, most
                // ticks for the local-activity one.
                if (Interlocked.Increment(ref inFlight) > concurrency)
                {
                    Interlocked.Decrement(ref inFlight);
                    Interlocked.Increment(ref skipped);
                    continue;
                }

                var n = Interlocked.Increment(ref started);

                // Drawn on the LOOP, not inside Task.Run, so the value a run was given is fixed
                // before the run exists and the classifier below reads the same one.
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
                            // A TOTAL catch, or an unobserved TaskException tears down the process
                            // on finalization.
                            //
                            // Shutdown is counted SEPARATELY from failure. A run whose RPCs were
                            // cancelled because the process is going down did not fail, and
                            // folding the two together makes every clean Ctrl-C look like it broke
                            // something, which is exactly the kind of misleading signal this repo
                            // exists to avoid.
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
