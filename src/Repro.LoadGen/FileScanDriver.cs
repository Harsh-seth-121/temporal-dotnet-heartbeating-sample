using Microsoft.Extensions.Logging;
using Repro.Core;
using Repro.Core.Config;
using Repro.Core.Workflows;
using Temporalio.Client;

namespace Repro.LoadGen;

/// <summary>
/// The fifth loadgen loop: starts <c>WorkflowFileScan</c> runs on a jittered interval, each one
/// a multi-minute raw-byte scan of a generated corpus, on the scan task queue.
/// </summary>
/// <remarks>
/// Pacing and the shared counters come from <see cref="DriverLoop{TRun}"/>. The corpus check is
/// what differs: one <see cref="File.Exists(string)"/> at construction, and an absent corpus
/// skips the whole loop with a banner, so <c>./scripts/demo-up.sh</c> stays green on a fresh
/// clone. It neither retries nor polls. See docs/CONFIG.md, "Absent corpus, and why
/// `dotnet test` still passes without one". Expect some ticks to skip: one scan runs about 4m47s
/// against a 6m +/-20% rate at <c>concurrency: 1</c>. Watch the activity's progress line, once
/// per <c>fileScan.logInterval</c>, for the row cursor, achieved rate and pressure sample.
/// </remarks>
internal sealed class FileScanDriver(
    ITemporalClient client,
    FileScanConfig fileScan,
    ILogger log)
{
    /// <summary>Bounds the StartWorkflowAsync RPC, so a wedged frontend cannot park the loop.</summary>
    /// <remarks>Not applied to GetResultAsync; see <see cref="OneRunAsync"/>.</remarks>
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Checked once, at construction. See the class remarks.</summary>
    private readonly bool corpusPresent = File.Exists(fileScan.Path);

    /// <summary>The tick loop, its capacity accounting, and the started/skipped/interrupted/failed counters.</summary>
    private readonly DriverLoop<FileScanInput> loop =
        new(fileScan.Rate, fileScan.Jitter, fileScan.Concurrency);

    private int completed;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!corpusPresent)
        {
            // Logged from here, not the constructor, so it lands after the readiness banner.
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

        // Built once: every scan is identical, and fileScan.Path is already absolute, which is
        // what makes it safe in a payload another process opens.
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

        // Every scan takes the same prebuilt input, so the draw is a constant.
        await loop.RunAsync(
            () => input,
            OneRunAsync,

            // Everything reaching `failed` is genuine. An aggregate that misses its closed form
            // is the failure this case exists to rule out, never an expected ending.
            (_, e) => log.LogWarning("file-scan run failed: {Message}", e.Message),
            LogSummary,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Start one scan, wait for it, and report the first verdict that comes back.</summary>
    private async Task OneRunAsync(FileScanInput input, CancellationToken cancellationToken)
    {
        var handle = await client.StartWorkflowAsync(
            (WorkflowFileScan wf) => wf.RunAsync(input),

            // Prefix-disjoint from every other id this repo generates; see SimpleActivityDriver.
            // A Guid rather than the `started` counter, which restarts at 1 and would collide
            // with a scan still retrying from a checkpoint after a kill -9.
            new WorkflowOptions(id: $"repro-scan-{Guid.NewGuid():N}", taskQueue: fileScan.TaskQueue)
            {
                Rpc = new RpcOptions { CancellationToken = cancellationToken, Timeout = RpcTimeout },
            }).ConfigureAwait(false);

        // No Timeout, unlike the start call: GetResultAsync long-polls for the whole run, about
        // 4m47s at the shipped config and 23m57s on the largest corpus.
        var result = await handle.GetResultAsync(
            rpcOptions: new RpcOptions { CancellationToken = cancellationToken }).ConfigureAwait(false);

        // First scan only, carrying the two closed forms the idempotency verdict rests on.
        // Verified is always true here: a mismatch throws from the activity instead.
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
    /// `interrupted` climbing on a teardown is healthy for this case: a scan covers 4m47s of
    /// every 6m, so a Ctrl-C almost always lands inside one. `failed` is the counter to read.
    /// </remarks>
    private void LogSummary() =>
        log.LogInformation(
            "file-scan: {Started} started, {Skipped} skipped at capacity | {Completed} " +
            "completed and verified | {Interrupted} interrupted by shutdown, {Failed} failed",
            loop.Started, loop.Skipped, completed, loop.Interrupted, loop.Failed);
}
