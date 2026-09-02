using Repro.Core.Config;

namespace Repro.Core;

/// <summary>Input to <c>WorkflowFileScan</c> and its single long-running activity.</summary>
/// <param name="Path">
/// The corpus, as an ABSOLUTE path. ConfigLoader resolves fileScan.path against the config
/// file's directory before it ever reaches here, so what travels in the payload is a path
/// that means the same file from any working directory.
/// <para>
/// It is in the INPUT rather than on the activity's constructor even though it is closer to
/// infrastructure than to job shape, and the reason is resume: the corpus-identity check
/// compares a checkpoint written by one worker process against a file opened by another,
/// and the two must be talking about the same job. A path read from each worker's own
/// config.yaml can differ between them with nothing in the history to show it did.
/// </para>
/// </param>
/// <param name="TargetRowsPerSecond">
/// Rows per second. 0 means unthrottled. See <see cref="FileScanConfig.TargetRowsPerSecond"/>
/// for why an unthrottled scan demonstrates nothing.
/// </param>
/// <param name="BatchRows">
/// Rows between one pace / cancel / drain / heartbeat / log check and the next. Also the
/// loop's reaction time to a drain, which is what ConfigLoader bounds rather than the rate.
/// </param>
/// <param name="MaxRows">0 means the whole file. A checkpoint written under a different value is refused.</param>
/// <param name="LogIntervalMs">
/// Milliseconds between progress lines and pressure samples. An int rather than a TimeSpan
/// for the reason <see cref="JobInput.StepDurationMs"/> gives: this crosses the payload
/// converter and "LogIntervalMs": 10000 reads better in `temporal workflow show` than a
/// serialized TimeSpan.
/// </param>
/// <param name="BufferBytes">
/// The single read buffer. There is no second buffer, because the scan finds its own line
/// breaks instead of using a StreamReader. 84,976 and up puts this array on the LOH.
/// </param>
/// <param name="Activity">
/// The timeouts and retry policy the workflow schedules the activity with. Optional with a
/// null default so a history captured before this field existed still deserializes -- see
/// <see cref="FileScanOptionsInput"/> for why the fallback values are not allowed to drift.
/// </param>
/// <remarks>
/// Job shape travels in the INPUT, the rule <see cref="ActivityOptionsInput"/> states in
/// full: activity options are baked into the ScheduleActivityTask command when the activity
/// is scheduled, so values that arrive in the input are in the history and a replay reads
/// back the bytes it wrote. Nothing here may be read from config.yaml inside workflow code.
/// <para>
/// fileScan.taskQueue is deliberately NOT here. It is not job shape -- it decides which
/// worker picks the task up, which is the host's business, and it is already fixed by the
/// time a workflow exists. The three fault knobs are not here either, for the stronger
/// reason <see cref="FaultConfig"/> records: they reach the activity through its constructor
/// so that workflow code provably cannot read them.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// Call this in CLIENT code -- the loadgen driver or the starter -- never in the workflow.
    /// The config read has to happen once, before the workflow exists.
    /// <para>
    /// NAMED arguments, and the hazard here is the worst of the four job files:
    /// <see cref="TargetRowsPerSecond"/> and <see cref="MaxRows"/> are both longs with an int
    /// between them, and <see cref="BatchRows"/>, <see cref="LogIntervalMs"/> and
    /// <see cref="BufferBytes"/> are three adjacent
    /// ints. Positionally swapping batchRows with bufferBytes compiles clean and gives a
    /// 65,536-row batch -- a 10.9-second batch period at the shipped rate, which silently
    /// breaks the drain reaction time ConfigLoader validated and nothing else.
    /// </para>
    /// </remarks>
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

/// <summary>The activity's timeouts and retry policy, carried in the workflow INPUT.</summary>
/// <remarks>
/// A SEPARATE record from <see cref="ActivityOptionsInput"/>, not a reuse, even though the
/// field list is identical. The two records' DEFAULTS are their contract with the histories
/// in history/, and this case's ladder is nothing like the seed case's: 30s against 5s on the
/// heartbeat timeout, 30m against 10m, 10 attempts against 5. Sharing the record would mean
/// one set of fallback values could not be right for both, and the one that lost would
/// silently replay old histories under the other case's ladder.
/// <para>
/// READ THE LADDER BEFORE CHANGING A NUMBER. Every rung is derived, and the derivations are
/// in <see cref="FileScanConfig"/> rather than repeated here. The short version:
/// </para>
/// <para>
/// <see cref="HeartbeatTimeoutMs"/> is chosen for the STALENESS IT PRODUCES, not for
/// liveness. The maximum gap between two Heartbeat() calls is one batch period (100ms at the
/// shipped config), so 30s is 300x margin. What it actually sets is Core's throttle,
/// min(0.8 x this, worker.maxHeartbeatThrottleInterval) = 24s, and therefore how much work a
/// kill -9 destroys the record of. It saturates: past 75s the 60s throttle ceiling binds and
/// the redone work stops growing.
/// </para>
/// <para>
/// <see cref="ScheduleToCloseTimeoutMs"/> is NOT attempts x startToClose, which is the model
/// most readers reach for and which gives an absurd number. Useful work is one worst-case
/// scan regardless of attempt count; each RESUME adds heartbeatTimeout (the server noticing)
/// plus retry.maximumInterval (backoff) plus the throttle (the reading that is redone) = 64s.
/// </para>
/// <para>
/// <see cref="RetryMaximumAttempts"/> is 10 rather than the repo's usual 5 because each
/// kill -9 consumes one and docs/HEARTBEATING.md's recipe does three cycles. It must not be
/// 0: Temporalio.Common.RetryPolicy treats 0 as UNLIMITED.
/// </para>
/// <para>
/// The defaults below are the shipped config.yaml values. They are what a null Activity falls
/// back to, so histories that predate this field still replay clean. Do not "tidy" them.
/// Change config.yaml instead.
/// </para>
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

        // NAMED, for the reason SimpleActivityOptionsInput.From records: RetryMaximumIntervalMs
        // and RetryMaximumAttempts are ADJACENT ints. Swapped positionally you get a 10ms
        // maximum interval and 10,000 attempts, which here means 10,000 half-hour scans of the
        // corpus, each one holding an activity slot.
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

/// <summary>What the activity puts in each heartbeat. THE CENTRE OF THE CASE.</summary>
/// <param name="Rows">
/// Index of the last COMPLETED row, 1-based. Resume starts at Rows + 1, which the activity
/// proves before reading a new byte by checking that the row it lands on carries exactly that
/// index in its own leading 10 digits.
/// </param>
/// <param name="ByteOffset">
/// First byte of row Rows + 1. EXACT, which is why the read loop finds its own line breaks
/// over a raw byte[]: StreamReader buffers ahead, so BaseStream.Position is somewhere past
/// the last returned line and there is no public API for the logical position, while
/// line.Length + 1 counts chars rather than bytes and drifts one byte per row on CRLF. An
/// inexact byte cursor destroys the resume half of this case.
/// </param>
/// <param name="IndexSum">
/// The REWOUND accumulator. See the remarks: this one field is what makes resume idempotent.
/// </param>
/// <param name="WordByteSum">
/// The byte cursor's witness. Sum of (rowLength - 19) over completed rows, which the corpus
/// contract ties to the offset by ByteOffset == HeaderLen + 20 x Rows + WordByteSum. Carried
/// so that resume validation is arithmetic rather than a second pass over the file.
/// </param>
/// <param name="FileRows">Row count from the corpus header, line 1. O(1) to obtain.</param>
/// <param name="FileBytes">FileStream.Length. Also O(1).</param>
/// <param name="RecordedAtUtc">
/// When the activity called Heartbeat(), not when the server received it. Comparing this to
/// now, on resume, is what repro_file_scan_staleness measures.
/// </param>
/// <remarks>
/// <see cref="IndexSum"/> IS THE FIELD THAT MAKES RESUME IDEMPOTENT. On resume the
/// accumulator is restored to the checkpoint's value, so every row between the checkpoint and
/// the crash is physically re-read and arithmetically counted exactly once. The rule that
/// generalises is not "carry the sum in the checkpoint" -- it is THE ACCUMULATOR'S ORIGIN AND
/// THE READ CURSOR'S ORIGIN MUST BE THE SAME CHECKPOINT. Restore one from the heartbeat and
/// the other from a process-local variable that survived, or from zero, and the answer is
/// wrong in a way no exception reports.
/// <para>
/// THERE IS DELIBERATELY NO CUMULATIVE RowsRead FIELD, and the reason is a proof. Such a
/// field would have to be A_k = A_(k-1) + (C_k - C_(k-1)), where C_k is the checkpoint row
/// that attempt k resumed from. That telescopes to A_k = C_k identically, so the field would
/// equal <see cref="Rows"/> and carry no information at all. The reads that get lost are
/// exactly the reads that were never checkpointed, so THE CHECKPOINT IS STRUCTURALLY
/// INCAPABLE of measuring redone work. That is what forces the metric route:
/// MetricNames.FileScanRowsRead is emitted per attempt from the activity, never rewound, and
/// the redone figure is derived against MetricNames.FileScanRowsExpected on the board.
/// </para>
/// <para>
/// AN XOR OR HASH-FOLD AGGREGATE IS REJECTED, and it is worth saying why, because it is the
/// aggregate a reader reaches for first. Under a self-inverse operation double-counting
/// CANCELS: fold the same row in twice and the accumulator returns to where it was. So a
/// naive resume -- one that rewinds the cursor but not the accumulator -- would produce the
/// RIGHT answer, the verdict counter would read match, and the entire lesson of the case
/// would vanish. A sum does not forgive: every double-counted row moves it, and
/// rows x (rows + 1) / 2 says by how much.
/// </para>
/// <para>
/// NO Sha256 FIELD. A digest of the corpus would have to be recomputed before the first new
/// byte is read on every resume, turning an O(1) identity check into a full extra pass over
/// up to 500 MB, and the header pair (<see cref="FileRows"/>, <see cref="FileBytes"/>) already
/// discriminates the shipped corpora exactly -- gen_samples keys the word seed on target size,
/// so any two of them differ in both numbers. A digest would only add value against a
/// same-length, same-row-count adversarial rewrite, which is not a failure mode a sandbox has.
/// </para>
/// <para>
/// NO Path FIELD either, and this one is the wrong discriminator in BOTH directions rather
/// than merely redundant. It would FAIL a legitimate resume, because a second worker's
/// config.yaml can resolve the same corpus to a different absolute path; and it would PASS the
/// dangerous case, because a regenerated corpus at the same path is a different stream. The
/// path is still printed on the RESUMING line every attempt, so a working-directory change
/// stays visible -- it is just not load-bearing.
/// </para>
/// <para>
/// Heartbeat details must round-trip through the data converter, and a failure there does not
/// arrive as an error: the conversion cancels the activity with
/// ActivityCancelReason.HeartbeatRecordFailure and then fails it. Six longs and a
/// DateTimeOffset is about 100 bytes against a 2 MB default payload limit, so size is not the
/// risk here; the risk is a RENAMED parameter, which binds nothing and yields default(T) --
/// see <see cref="WeatherReading"/> for the measurements. A zeroed IndexSum next to a correct
/// ByteOffset is a silent wrong answer that surfaces only at completion, which is why the
/// activity's resume path validates both corpus identities and both closed forms before it
/// reads a byte.
/// </para>
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
/// <param name="IndexSum">
/// The accumulated sum of row indices. 1,487,102,747,166 for the shipped 100 MB corpus, and
/// the number the idempotency verdict is decided on.
/// </param>
/// <param name="WordByteSum">
/// The accumulated word bytes. 65,508,200 for the shipped 100 MB corpus, and the second,
/// independent witness: it ties the answer to the BYTE cursor, where
/// <paramref name="IndexSum"/> ties it to the ROW cursor. A resume bug that moved one and not
/// the other shows up in exactly one of the two.
/// </param>
/// <param name="Verified">
/// Whether both closed forms matched. Always true in a returned result, because a mismatch
/// throws non-retryable rather than returning. It is in the payload anyway so that
/// `temporal workflow show` states the verdict rather than implying it.
/// </param>
/// <remarks>
/// AN ACTIVITY'S RETURN RECORD IS A REPLAY-VISIBLE SCHEMA and the contract is about NAMES,
/// not positions -- <see cref="WeatherReading"/> carries the measurements for that and they
/// are not repeated here.
/// <para>
/// The hazard specific to this record is FOUR ADJACENT LONGS, three of which are large and
/// plausible at the same order of magnitude for the 100 MB corpus (99,999,968 bytes against
/// 65,508,200 word bytes). Positional construction compiles clean and would report the word
/// byte sum as the byte count. Every construction site uses NAMED arguments.
/// </para>
/// </remarks>
public record FileScanResult(
    long Rows = 0,
    long Bytes = 0,
    long IndexSum = 0,
    long WordByteSum = 0,
    bool Verified = false);
