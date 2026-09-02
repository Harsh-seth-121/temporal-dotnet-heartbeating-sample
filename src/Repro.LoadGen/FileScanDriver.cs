using Microsoft.Extensions.Logging;
using Repro.Core;
using Repro.Core.Config;
using Repro.Core.Workflows;
using Temporalio.Client;

namespace Repro.LoadGen;

/// <summary>
/// The fifth loadgen loop: starts <c>WorkflowFileScan</c> runs on a jittered interval, each one
/// a multi-minute raw-byte scan of a generated corpus, on the SCAN task queue.
/// </summary>
/// <remarks>
/// THE CORPUS CHECK IS THE ONE THING THAT MAKES THIS DIFFERENT from the other four drivers, and
/// it is not defensive coding. <c>sample_files/</c> is gitignored and generated, so on a fresh
/// clone the corpus is absent while <c>fileScan.enabled</c> is still true, and
/// <c>ConfigLoader.ValidateFileScan</c> deliberately never stats it -- ConfigTests loads the
/// committed config.yaml, so a stat there would fail <c>dotnet test</c> on every fresh clone.
/// Something has to notice, and this is the cheapest place: ONE
/// <see cref="File.Exists(string)"/> at construction, then the whole loop is skipped with a
/// banner naming the path and the generator command, so <c>./scripts/demo-up.sh</c> still comes
/// up green on a clone that has never run the generator.
/// <para>
/// It does NOT retry and does NOT poll for the file. A corpus that appears later is picked up by
/// the next process start, which is a restart of a demo the user is already restarting; a
/// watcher would buy nothing and would turn a one-line banner into state.
/// </para>
/// <para>
/// EXPECT SOME TICKS TO SKIP, and do not read that as breakage. At the shipped values one scan
/// is about 4m47s against a rate of 6m +/-20%, so roughly a fifth of the intervals drawn are
/// shorter than the scan already in flight, and at <c>concurrency: 1</c> those ticks are
/// skipped. The summary line prints skipped alongside started for exactly that reason.
/// </para>
/// <para>
/// THIS DRIVER IS ALMOST SILENT BY DESIGN. Ten starts at a 6m rate is an hour, so the summary
/// below is roughly hourly; the place to watch a scan is the ACTIVITY's own progress line, once
/// per <c>fileScan.logInterval</c>, which carries the row cursor, the achieved rate and the
/// pressure sample. A client cannot see any of that.
/// </para>
/// <para>
/// Everything here is CLIENT code, so Random.Shared, wall clock and File.Exists are all fine.
/// Nothing in this file may leak into workflow code.
/// </para>
/// <para>
/// NO SemaphoreSlim, for the reason <see cref="SimpleDriver"/> records at length. Interlocked
/// counters have no disposal semantics at all.
/// </para>
/// </remarks>
internal sealed class FileScanDriver(
    ITemporalClient client,
    FileScanConfig fileScan,
    ILogger log)
{
    /// <summary>Bounds the StartWorkflowAsync RPC, so a wedged frontend cannot park the loop.</summary>
    /// <remarks>
    /// Not applied to GetResultAsync, and it matters more here than in any other driver: a scan
    /// long-polls for its whole run, about 4m47s at the shipped config and 23m57s on the
    /// largest shipped corpus. See <see cref="OneRunAsync"/>.
    /// </remarks>
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Checked ONCE, at construction. See the class remarks.</summary>
    private readonly bool corpusPresent = File.Exists(fileScan.Path);

    private int inFlight;
    private int started;
    private int skipped;
    private int completed;
    private int interrupted;
    private int failed;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!corpusPresent)
        {
            // ONE banner and no loop. Logged from here rather than from the constructor so it
            // lands after Program.cs's readiness banner, which scripts/demo-lib.sh greps with a
            // 45s budget.
            log.LogInformation(
                "file-scan: OFF (corpus not found at {Path}; run " +
                "scripts/gen-samples/gen-samples.sh). Checked once, at startup: sample_files/ is " +
                "gitignored and generated, so a fresh clone has no corpus and this loop starts " +
                "nothing rather than failing a scan a hundred times. The scan WORKER is still " +
                "polling {TaskQueue}, so a corpus generated later can be scanned by hand with " +
                "`starter --file-scan` and needs no restart of this process.",
                fileScan.Path, fileScan.TaskQueue);
            return;
        }

        // Built ONCE, outside the loop: every scan is identical, and projecting config.yaml here
        // rather than inside the workflow is the point of FileScanInput.From. fileScan.Path is
        // already ABSOLUTE -- ConfigLoader resolved it against the config file's directory --
        // which is what makes it safe to put in the payload for another process to open.
        var input = FileScanInput.From(fileScan);

        log.LogInformation(
            "file-scan: 1 scan every {Rate} +/-{JitterPercent}%, up to {Concurrency} in flight, " +
            "corpus {Path}, target {RowsPerSecond} rows/s in batches of {BatchRows} " +
            "({BufferBytes}-byte reads), queue {TaskQueue}, heartbeat timeout {HeartbeatTimeout}, " +
            "startToClose {StartToClose}, up to {MaxAttempts} attempts",
            GoDuration.ToGoString(fileScan.Rate), (int)(fileScan.Jitter * 100),
            fileScan.Concurrency, fileScan.Path, fileScan.TargetRowsPerSecond, fileScan.BatchRows,
            fileScan.BufferBytes, fileScan.TaskQueue,
            GoDuration.ToGoString(fileScan.HeartbeatTimeout),
            GoDuration.ToGoString(fileScan.StartToCloseTimeout), fileScan.Retry.MaximumAttempts);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Task.Delay, not PeriodicTimer: the period varies, which is the point. The
                // token is forwarded because CA2016 is an error in this repo, and because
                // without it a shutdown waits out a full interval -- six minutes here.
                await Task.Delay(
                    Jitter.NextInterval(fileScan.Rate, fileScan.Jitter),
                    cancellationToken).ConfigureAwait(false);

                // SKIP at capacity, never queue. Same contract as the other four loops, and see
                // the class remarks for why it fires regularly at the shipped values.
                if (Interlocked.Increment(ref inFlight) > fileScan.Concurrency)
                {
                    Interlocked.Decrement(ref inFlight);
                    Interlocked.Increment(ref skipped);
                    continue;
                }

                var n = Interlocked.Increment(ref started);

                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await OneRunAsync(input, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            // A TOTAL catch, or an unobserved TaskException tears down the
                            // process on finalization.
                            //
                            // Shutdown is counted SEPARATELY from failure, for the reason
                            // SimpleDriver records: a run whose RPCs were cancelled because the
                            // process is going down did not fail, and folding the two together
                            // makes every clean Ctrl-C look like breakage. That split is worth
                            // more here than anywhere else, because a teardown lands INSIDE a
                            // scan most of the time -- one scan covers 4m47s of every 6m.
                            //
                            // Everything that reaches `failed` is genuine: exhausted retries, or
                            // one of the activity's non-retryable throws -- a checkpoint that
                            // disagrees with itself, a corpus that changed under a resume, or an
                            // aggregate that does not match its closed form. The last of those
                            // is the one failure this whole case exists to rule out, so it must
                            // never be quietly reclassified as an expected ending.
                            if (cancellationToken.IsCancellationRequested)
                            {
                                Interlocked.Increment(ref interrupted);
                            }
                            else
                            {
                                Interlocked.Increment(ref failed);
                                log.LogWarning("file-scan run failed: {Message}", e.Message);
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
                    LogSummary();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the shutdown token cancelled Task.Delay.
        }

        LogSummary();
    }

    /// <summary>Start one scan, wait for it, and report the first verdict that comes back.</summary>
    private async Task OneRunAsync(FileScanInput input, CancellationToken cancellationToken)
    {
        var handle = await client.StartWorkflowAsync(
            (WorkflowFileScan wf) => wf.RunAsync(input),

            // "repro-scan-", checked disjoint as a string PREFIX against every other id this
            // repo generates: repro-loadgen-, repro-simple-, repro-weather-, repro-pi-,
            // repro-workflow and the starter's repro-file-scan. The reason is the one
            // SimpleActivityDriver records: a prefix collision makes `WorkflowId STARTS_WITH`
            // visibility queries and `grep` silently merge two cases and report a count that is
            // quietly too high.
            //
            // A Guid rather than the `started` counter, even though `repro-scan-3` would read
            // better in `temporal workflow list`. A counter restarts at 1 with this process, and
            // a scan that is still open -- retrying from a checkpoint after a kill -9, which is
            // this case's whole recipe -- then collides with its own id and the start fails as
            // WorkflowAlreadyStarted. Guid.NewGuid is fine: this is client code.
            new WorkflowOptions(id: $"repro-scan-{Guid.NewGuid():N}", taskQueue: fileScan.TaskQueue)
            {
                Rpc = new RpcOptions { CancellationToken = cancellationToken, Timeout = RpcTimeout },
            }).ConfigureAwait(false);

        // NO Timeout here, unlike the start call, and this driver is where that distinction
        // stops being theoretical: GetResultAsync long-polls for the whole run, which is about
        // 4m47s at the shipped config and 23m57s on the largest shipped corpus -- longer than
        // any other case in this repo by two orders of magnitude. A 10s RpcTimeout here would
        // fail every single scan on the client side while the scan itself ran happily to
        // completion, which is the most confusing shape this loop could have. The token still
        // releases it at shutdown.
        var result = await handle.GetResultAsync(
            rpcOptions: new RpcOptions { CancellationToken = cancellationToken }).ConfigureAwait(false);

        // First completed scan only. This is the line that proves an aggregate made it all the
        // way back to the client, and it carries the two closed forms the idempotency verdict is
        // decided on -- so a resume that double-counted is visible here without opening Grafana.
        // Verified is always true on this path by construction: a mismatch throws non-retryably
        // from the activity and arrives in the catch above instead.
        if (Interlocked.Increment(ref completed) == 1)
        {
            log.LogInformation(
                "file-scan: first scan returned verified={Verified} after {Rows} rows and " +
                "{Bytes} bytes, indexSum {IndexSum}, wordByteSum {WordByteSum}",
                result.Verified, result.Rows, result.Bytes, result.IndexSum, result.WordByteSum);
        }
    }

    /// <summary>One line, every ten starts and once at shutdown.</summary>
    /// <remarks>
    /// CONCATENATED STRING LITERALS, not interpolation: CA2254 requires a compile-time constant
    /// message and CA1727 requires PascalCase placeholders, both build errors here.
    /// <para>
    /// `interrupted` climbing on a teardown is the healthy board for this case rather than a
    /// symptom: a scan covers 4m47s of every 6m, so a Ctrl-C almost always lands inside one.
    /// `failed` is the counter to read, and any value above zero deserves the log line above it.
    /// </para>
    /// </remarks>
    private void LogSummary() =>
        log.LogInformation(
            "file-scan: {Started} started, {Skipped} skipped at capacity | {Completed} " +
            "completed and verified | {Interrupted} interrupted by shutdown, {Failed} failed",
            started, skipped, completed, interrupted, failed);
}
