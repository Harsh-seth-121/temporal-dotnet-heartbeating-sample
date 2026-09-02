using Repro.Core.Config;

namespace Repro.Core;

/// <summary>Input to <c>WorkflowFileScan</c> and its single long-running activity.</summary>
/// <param name="Path">The corpus, absolute; ConfigLoader resolves fileScan.path against the config
/// file's directory. In the input, not on the activity's constructor, because resume compares a
/// checkpoint written by one worker against a file opened by another.</param>
/// <param name="TargetRowsPerSecond">Rows per second, 0 unthrottled. See <see cref="FileScanConfig.TargetRowsPerSecond"/>.</param>
/// <param name="BatchRows">Rows between one pace / cancel / drain / heartbeat / log check and the next, so also the drain reaction time, which is what ConfigLoader bounds rather than the rate.</param>
/// <param name="MaxRows">0 means the whole file. A checkpoint written under a different value is refused.</param>
/// <param name="LogIntervalMs">Milliseconds between progress lines and pressure samples. An int rather than a TimeSpan for the reason <see cref="JobInput.StepDurationMs"/> gives.</param>
/// <param name="BufferBytes">The single read buffer; the scan finds its own line breaks, so there is no second one. 84,976 and up puts this array on the LOH.</param>
/// <param name="Activity">The activity's timeouts and retry policy. Null default so a history captured before this field existed still deserializes.</param>
/// <remarks>Job shape travels in the input, per <see cref="ActivityOptionsInput"/>. Nothing here
/// may be read from config.yaml inside workflow code. fileScan.taskQueue is not job shape; the
/// fault knobs are excluded for the reason <see cref="FaultConfig"/> records.</remarks>
public record FileScanInput(
    string Path = "",
    long TargetRowsPerSecond = 6000,
    int BatchRows = 600,
    long MaxRows = 0,
    int LogIntervalMs = 10_000,
    int BufferBytes = 65_536,
    FileScanOptionsInput? Activity = null)
{
    /// <summary>Project config.yaml's <c>fileScan:</c> block onto the wire shape.</summary>
    /// <remarks>Call this in client code, never in the workflow: the config read has to happen
    /// once, before the workflow exists. Named arguments, because <see cref="BatchRows"/>,
    /// <see cref="LogIntervalMs"/> and <see cref="BufferBytes"/> are three adjacent ints and
    /// swapping batchRows with bufferBytes gives a 10.9-second batch period at the shipped
    /// rate.</remarks>
    public static FileScanInput From(FileScanConfig fileScan)
    {
        ArgumentNullException.ThrowIfNull(fileScan);

        return new FileScanInput(
            Path: fileScan.Path,
            TargetRowsPerSecond: fileScan.TargetRowsPerSecond,
            BatchRows: fileScan.BatchRows,
            MaxRows: fileScan.MaxRows,
            LogIntervalMs: (int)fileScan.LogInterval.TotalMilliseconds,
            BufferBytes: fileScan.BufferBytes,
            Activity: FileScanOptionsInput.From(fileScan));
    }
}

/// <summary>The activity's timeouts and retry policy, carried in the workflow input.</summary>
/// <remarks>
/// A separate record from <see cref="ActivityOptionsInput"/> despite the identical field list,
/// because the defaults are each record's contract with the histories in history/. They are the
/// shipped config.yaml values; change config.yaml, not these. The rungs are derived in
/// docs/CONFIG.md, "The timeout ladder, derived". <see cref="HeartbeatTimeoutMs"/> is chosen for
/// the staleness it produces, not for liveness: it sets Core's throttle,
/// min(0.8 x this, worker.maxHeartbeatThrottleInterval) = 24s, and so how much work a kill -9
/// destroys the record of. <see cref="RetryMaximumAttempts"/> is 10 rather than the usual 5 because
/// each kill -9 consumes one, and must not be 0, which RetryPolicy reads as unlimited.
/// </remarks>
public record FileScanOptionsInput(
    int HeartbeatTimeoutMs = 30_000,
    int StartToCloseTimeoutMs = 1_800_000,
    int ScheduleToCloseTimeoutMs = 3_600_000,
    int RetryInitialIntervalMs = 1_000,
    double RetryBackoffCoefficient = 2.0,
    int RetryMaximumIntervalMs = 10_000,
    int RetryMaximumAttempts = 10) : IRetryInput
{
    /// <inheritdoc cref="FileScanInput.From"/>
    public static FileScanOptionsInput From(FileScanConfig fileScan)
    {
        ArgumentNullException.ThrowIfNull(fileScan);

        // Named, for the reason SimpleActivityOptionsInput.From records. Swapping the adjacent
        // RetryMaximumIntervalMs and RetryMaximumAttempts gives 10,000 half-hour corpus scans.
        return new FileScanOptionsInput(
            HeartbeatTimeoutMs: (int)fileScan.HeartbeatTimeout.TotalMilliseconds,
            StartToCloseTimeoutMs: (int)fileScan.StartToCloseTimeout.TotalMilliseconds,
            ScheduleToCloseTimeoutMs: (int)fileScan.ScheduleToCloseTimeout.TotalMilliseconds,
            RetryInitialIntervalMs: (int)fileScan.Retry.InitialInterval.TotalMilliseconds,
            RetryBackoffCoefficient: fileScan.Retry.BackoffCoefficient,
            RetryMaximumIntervalMs: (int)fileScan.Retry.MaximumInterval.TotalMilliseconds,
            RetryMaximumAttempts: fileScan.Retry.MaximumAttempts);
    }
}

/// <summary>What the activity puts in each heartbeat. The centre of the case.</summary>
/// <param name="Rows">Index of the last completed row, 1-based. Resume starts at Rows + 1, which the activity proves from the row's own leading 10 digits before reading a new byte.</param>
/// <param name="ByteOffset">First byte of row Rows + 1, exact. Hence the read loop finding its own
/// line breaks over a raw byte[]; see docs/GOTCHAS.md, "StreamReader cannot tell you the logical
/// byte offset it has reached".</param>
/// <param name="IndexSum">The rewound accumulator. See the remarks.</param>
/// <param name="WordByteSum">The byte cursor's witness. Sum of (rowLength - 19) over completed rows, tied to the offset by ByteOffset == HeaderLen + 20 x Rows + WordByteSum, so resume validation is arithmetic rather than a second pass over the file.</param>
/// <param name="FileRows">Row count from the corpus header, line 1. O(1), and with <paramref name="FileBytes"/> it discriminates the shipped corpora, so there is no digest field. No path field either: a path fails a legitimate resume when a second worker resolves the corpus differently, and passes a regenerated corpus at the same path.</param>
/// <param name="FileBytes">FileStream.Length. Also O(1).</param>
/// <param name="RecordedAtUtc">When the activity called Heartbeat(), not when the server received it. Compared to now on resume, it is what repro_file_scan_staleness measures.</param>
/// <remarks>
/// The invariant: the accumulator's origin and the read cursor's origin must be the same
/// checkpoint. On resume <see cref="IndexSum"/> is restored to the checkpoint's value, so every row
/// between the checkpoint and the crash is re-read and counted exactly once. Restore one from the
/// heartbeat and the other from a surviving process-local variable, or from zero, and the answer is
/// wrong with no exception. A sum rather than an XOR or hash fold, because under a self-inverse
/// operation double-counting cancels and the bug would look correct. No cumulative RowsRead field:
/// see docs/GOTCHAS.md, "A checkpoint cannot measure the work a crash made you redo". These details
/// round-trip through the data converter, so a renamed parameter binds nothing and yields
/// default(T) (see <see cref="WeatherReading"/>) while a conversion failure cancels the activity
/// with ActivityCancelReason.HeartbeatRecordFailure and then fails it.
/// </remarks>
public record FileScanCheckpoint(
    long Rows = 0,
    long ByteOffset = 0,
    long IndexSum = 0,
    long WordByteSum = 0,
    long FileRows = 0,
    long FileBytes = 0,
    DateTimeOffset RecordedAtUtc = default);

/// <summary>What the activity returns, and therefore what lands in the history.</summary>
/// <param name="Rows">Rows counted, which on a full scan equals the corpus header's N.</param>
/// <param name="Bytes">The end offset. On a full scan it equals the file length exactly.</param>
/// <param name="IndexSum">Accumulated sum of row indices, 1,487,102,747,166 for the shipped 100 MB corpus. The idempotency verdict is decided on this.</param>
/// <param name="WordByteSum">Accumulated word bytes, 65,508,200 for the shipped 100 MB corpus. The second, independent witness: it ties the answer to the byte cursor where <paramref name="IndexSum"/> ties it to the row cursor.</param>
/// <param name="Verified">Whether both closed forms matched. Always true in a returned result, since a mismatch throws non-retryable, but carried so `temporal workflow show` states it.</param>
/// <remarks>A replay-visible schema keyed on names, not positions; see
/// <see cref="WeatherReading"/>. Four adjacent longs here, three plausible at the same order of
/// magnitude for the 100 MB corpus (99,999,968 bytes against 65,508,200 word bytes), so every
/// construction site uses named arguments.</remarks>
public record FileScanResult(
    long Rows = 0,
    long Bytes = 0,
    long IndexSum = 0,
    long WordByteSum = 0,
    bool Verified = false);
