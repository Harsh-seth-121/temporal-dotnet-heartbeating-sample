using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Repro.Core.Config;
using Repro.Core.Telemetry;
using Temporalio.Activities;
using Temporalio.Common;
using Temporalio.Exceptions;

namespace Repro.Core.Activities;

/// <summary>
/// A raw-byte scan over a real file that checkpoints an exact byte cursor, resumes from it
/// idempotently, and publishes what it costs the worker.
/// </summary>
/// <remarks>
/// Wire name <c>ScanFile</c>, pinned in the dashboards as <c>activity_type="ScanFile"</c>.
/// <para>
/// The rule this case teaches: the accumulator's origin and the read cursor's origin must be
/// the same checkpoint. Restore one from the heartbeat and the other from zero and the rows
/// between the checkpoint and the crash are counted twice, in either direction, without
/// throwing; only the closed form at completion catches it.
/// </para>
/// <para>
/// <see cref="FileScanConfig"/> is deliberately not injected. Every value the scan needs
/// travels in <see cref="FileScanInput"/>, notably the corpus path, because a resume must
/// compare a checkpoint against the path the workflow named, and an unread
/// primary-constructor parameter is CS9113, an error here.
/// </para>
/// </remarks>
public sealed class FileScanActivities(FaultConfig fault, WorkerConfig? worker = null)
{
    /// <summary>Fixed bytes per corpus row, including its LF. Set by <c>gen_samples.py</c>.</summary>
    /// <remarks>10 index digits + one space + <c>[</c> + six inner separators + <c>]</c> + LF
    /// = 20, so <c>ByteOffset == headerLen + (RowOverhead x rows) + wordByteSum</c> at every
    /// checkpoint. That identity catches a drifted byte cursor; see docs/GOTCHAS.md,
    /// "`StreamReader` cannot tell you the logical byte offset it has reached".</remarks>
    public const int RowOverhead = 20;

    /// <summary>Width of the zero-padded row index, <c>%010d</c>.</summary>
    public const int IndexDigits = 10;

    /// <summary>A malformed row, from <see cref="ParseRowIndex"/>. Row indices are 1-based.</summary>
    /// <remarks>A sentinel rather than an exception keeps the parser pure and testable with no
    /// <c>ActivityEnvironment</c>, and leaves the message to the caller, which is the only
    /// thing holding the row number, the byte offset and the path.</remarks>
    public const long MalformedRow = -1;

    /// <summary>Longest legal decimal the header parser will accept, to keep it overflow-free.</summary>
    private const int MaxHeaderDigits = 18;

    // Optional for the reason HeartbeatActivities gives. A worker whose config.yaml sets a
    // different worker.gracefulShutdownTimeout must pass its WorkerConfig, or the drain line
    // below names a grace window nothing is using.
    private readonly WorkerConfig workerConfig = worker ?? new WorkerConfig();

    /// <summary>Header length for a corpus of <paramref name="fileRows"/> rows: digits + LF.</summary>
    /// <remarks>Arithmetic over a number the checkpoint already carries, so
    /// <see cref="CheckpointDisagreement"/> can verify the byte identity before the file is
    /// opened. Not <c>ToString().Length</c>: CA1305 is an error here.</remarks>
    public static int HeaderLength(long fileRows)
    {
        var digits = 1;
        for (var remaining = fileRows; remaining >= 10; remaining /= 10)
        {
            digits++;
        }

        return digits + 1;
    }

    /// <summary>Parse and structurally validate one row, LF excluded. The per-row hot path.</summary>
    /// <param name="row">The row's bytes without its trailing LF, as the read loop slices it.</param>
    /// <returns>The 1-based row index, or <see cref="MalformedRow"/>.</returns>
    /// <remarks>
    /// It deliberately does not check the index against the one expected here: a resume that
    /// rewinds the cursor but not the accumulator restores <c>rows</c> too, so every row
    /// satisfies <c>index == rows + 1</c> while the total comes out short. A CRLF corpus fails
    /// here on row 1, since the read loop splits on LF; tolerating it gives one byte of cursor
    /// drift per row. The length floor is the structural minimum, not
    /// <see cref="RowOverhead"/>; shipped-corpus rows are 41 to 76 bytes with the LF.
    /// </remarks>
    public static long ParseRowIndex(ReadOnlySpan<byte> row)
    {
        // 10 digits, then ' ', '[' and a closing ']' that must not be one of those two.
        if (row.Length < IndexDigits + 3)
        {
            return MalformedRow;
        }

        if (row[IndexDigits] != (byte)' '
            || row[IndexDigits + 1] != (byte)'['
            || row[^1] != (byte)']')
        {
            return MalformedRow;
        }

        return ParseDigits(row[..IndexDigits]);
    }

    /// <summary>Is this checkpoint self-consistent? Arithmetic only, no file is opened.</summary>
    /// <param name="checkpoint">What came back from <c>HeartbeatDetailAtAsync</c>.</param>
    /// <returns><c>null</c> when it agrees with itself, otherwise why it does not.</returns>
    /// <remarks>
    /// The schema-drift tripwire. Heartbeat details bind by name, so a renamed record parameter
    /// yields <c>default(T)</c>, and a zeroed <see cref="FileScanCheckpoint.IndexSum"/> beside
    /// a correct <see cref="FileScanCheckpoint.ByteOffset"/> surfaces at completion as an
    /// aggregate mismatch, blaming a resume bug that does not exist. Both closed forms are
    /// checked: <c>IndexSum == rows x (rows + 1) / 2</c> and
    /// <c>ByteOffset == headerLen + (RowOverhead x rows) + wordByteSum</c>. Those plus the
    /// non-negativity bounds force <c>ByteOffset >= headerLen >= 2</c>, so the line-boundary
    /// proof's <c>Seek(ByteOffset - 1)</c> cannot go negative.
    /// </remarks>
    public static string? CheckpointDisagreement(FileScanCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        // FileRows first: everything below derives the header length from it, and zero is
        // what a renamed or dropped record parameter produces.
        if (checkpoint.FileRows <= 0 || checkpoint.FileBytes <= 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"the checkpoint carries fileRows {checkpoint.FileRows} and fileBytes "
                + $"{checkpoint.FileBytes}, and both must be positive. A zero here means a renamed "
                + $"FileScanCheckpoint parameter: heartbeat details bind by name, and an unbound "
                + $"parameter yields default(T).");
        }

        if (checkpoint.Rows < 0 || checkpoint.WordByteSum < 0 || checkpoint.IndexSum < 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"the checkpoint carries rows {checkpoint.Rows}, wordByteSum "
                + $"{checkpoint.WordByteSum} and indexSum {checkpoint.IndexSum}, and none of the "
                + $"three may be negative. All are monotone accumulators over a forward scan.");
        }

        if (checkpoint.Rows > checkpoint.FileRows)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"the checkpoint claims {checkpoint.Rows} completed rows out of a corpus of "
                + $"{checkpoint.FileRows}. A scan cannot complete more rows than the header it "
                + $"read declares.");
        }

        var headerLen = HeaderLength(checkpoint.FileRows);
        var expectedOffset = headerLen + (RowOverhead * checkpoint.Rows) + checkpoint.WordByteSum;
        if (checkpoint.ByteOffset != expectedOffset)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"the checkpoint's byte offset {checkpoint.ByteOffset} disagrees with its own row "
                + $"and word-byte counts: headerLen {headerLen} + ({RowOverhead} x "
                + $"{checkpoint.Rows} rows) + wordByteSum {checkpoint.WordByteSum} = "
                + $"{expectedOffset}. Every corpus row is exactly {RowOverhead} bytes of "
                + $"overhead plus its words.");
        }

        var expectedIndexSum = checkpoint.Rows * (checkpoint.Rows + 1) / 2;
        if (checkpoint.IndexSum != expectedIndexSum)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"the checkpoint's indexSum {checkpoint.IndexSum} is not the closed form for its "
                + $"own {checkpoint.Rows} rows, {checkpoint.Rows} x ({checkpoint.Rows} + 1) / 2 = "
                + $"{expectedIndexSum}. Either this scan did not write the checkpoint, or a "
                + $"record parameter was renamed and IndexSum bound to default(T).");
        }

        if (checkpoint.RecordedAtUtc == default)
        {
            return "the checkpoint carries no RecordedAtUtc, the same unbound-parameter failure "
                + "as fileRows. A default DateTimeOffset records a two-thousand-year staleness "
                + "into repro_file_scan_staleness, whose top boundary is 90 seconds.";
        }

        return null;
    }

    /// <summary>Is this checkpoint about this file, and inside this job's bounds?</summary>
    /// <param name="checkpoint">A checkpoint that has already passed <see cref="CheckpointDisagreement"/>.</param>
    /// <param name="path">The resolved absolute corpus path, for the message.</param>
    /// <param name="fileRows">Row count from line 1 of the file now open.</param>
    /// <param name="fileBytes"><c>FileStream.Length</c> of the file now open.</param>
    /// <param name="rowsToScan">Rows this invocation intends to read.</param>
    /// <returns><c>null</c> when the checkpoint may be resumed from, otherwise why not.</returns>
    /// <remarks>
    /// Runs before a resumed byte is read, because a resume into a different corpus can land on
    /// a valid line boundary whose leading index matches and return a sum over two files as
    /// <c>outcome="completed"</c>. <c>(fileRows, fileBytes)</c> discriminates the shipped
    /// corpora at two O(1) reads, since <c>gen_samples.py</c> keys its word seed on target
    /// size; <see cref="FileScanCheckpoint"/> argues against a digest and against carrying the
    /// path. The bounds are <c>&lt;=</c> because an attempt that read the last row,
    /// heartbeated and died before its result reached the server leaves
    /// <c>Rows == rowsToScan</c>, and rejecting that would fail the workflow for finishing.
    /// </remarks>
    public static string? CorpusDisagreement(
        FileScanCheckpoint checkpoint, string path, long fileRows, long fileBytes, long rowsToScan)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        if (checkpoint.FileRows != fileRows || checkpoint.FileBytes != fileBytes)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"checkpoint does not match {path}: checkpoint recorded {checkpoint.FileRows} rows "
                + $"/ {checkpoint.FileBytes} bytes, this file has {fileRows} rows / {fileBytes} "
                + $"bytes. Refusing to resume: the cursor would land in a different stream.");
        }

        if (checkpoint.Rows > rowsToScan)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"the checkpoint has already completed {checkpoint.Rows} rows but this invocation "
                + $"only intends to scan {rowsToScan}. A checkpoint written under a larger maxRows "
                + $"is a different job.");
        }

        if (checkpoint.ByteOffset > fileBytes)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"the checkpoint's byte offset {checkpoint.ByteOffset} is past the end of {path} "
                + $"({fileBytes} bytes). The corpus identity matched, so the checkpoint's own "
                + $"arithmetic and the file disagree.");
        }

        return null;
    }

    /// <summary>Scan the corpus, checkpointing an exact byte cursor. Wire name <c>ScanFile</c>.</summary>
    /// <remarks>The resume path runs entirely before a new byte is read, cheapest checks
    /// first, so an unusable checkpoint costs no I/O and a changed corpus is named as such
    /// rather than surfacing later as a wrong total.</remarks>
    [Activity]
    public async Task<FileScanResult> ScanFileAsync(FileScanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Capture once; see HeartbeatActivities.ProcessBatchAsync.
        var ctx = ActivityExecutionContext.Current;
        var meter = ctx.MetricMeter;
        var log = ctx.Logger;

        var startedAt = Stopwatch.GetTimestamp();

        // ConfigLoader.ValidateFileScan bounds config.yaml but nothing bounds a workflow
        // input. batchRows 0 heartbeats an unchanged checkpoint forever, logIntervalMs 0 lets
        // the sampler dominate the allocation counter, and bufferBytes 0 fails a legal file as
        // "a full buffer with no LF".
        if (input.BatchRows <= 0 || input.BufferBytes <= 0 || input.LogIntervalMs <= 0)
        {
            throw Terminal(log,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"batchRows ({input.BatchRows}), bufferBytes ({input.BufferBytes}) and "
                    + $"logIntervalMs ({input.LogIntervalMs}) must all be > 0. ConfigLoader bounds "
                    + $"the fileScan: block but not a hand-written workflow input."),
                "FileScanInputInvalid");
        }

        if (string.IsNullOrWhiteSpace(input.Path))
        {
            throw Terminal(log,
                "fileScan.path reached the activity empty. Generate the corpora with "
                + "scripts/gen-samples/gen-samples.sh.",
                "FileScanCorpusMissing");
        }

        var logInterval = TimeSpan.FromMilliseconds(input.LogIntervalMs);

        // Every instrument is hoisted: CreateCounter/CreateGauge cross the native bridge, and
        // at 2,874 batches per scan that cost would land on the pace budget. attempt tags these
        // two counters only; see docs/GOTCHAS.md, "Tagging a gauge with `attempt` hides the
        // drop it was added to show".
        var attemptTagged = meter.WithTags(new Dictionary<string, object>
        {
            [MetricNames.Tags.Attempt] = ctx.Info.Attempt.ToString(CultureInfo.InvariantCulture),
        });
        var rowsReadCounter = attemptTagged.CreateCounter<long>(MetricNames.FileScanRowsRead, "rows");
        var bytesReadCounter = attemptTagged.CreateCounter<long>(MetricNames.FileScanBytesRead, "bytes");

        var rowCursorGauge = meter.CreateGauge<long>(MetricNames.FileScanRowCursor, "rows");
        var rowsExpectedGauge = meter.CreateGauge<long>(MetricNames.FileScanRowsExpected, "rows");
        var resumedFromRowGauge = meter.CreateGauge<long>(MetricNames.FileScanResumedFromRow, "rows");

        var allocatedCounter = meter.CreateCounter<long>(MetricNames.FileScanBytesAllocated, "bytes");
        var managedHeapGauge = meter.CreateGauge<long>(MetricNames.FileScanManagedHeapBytes, "bytes");
        var lohGauge = meter.CreateGauge<long>(MetricNames.FileScanLohBytes, "bytes");
        var workingSetGauge = meter.CreateGauge<long>(MetricNames.FileScanWorkingSetBytes, "bytes");
        var gcPauseGauge = meter.CreateGauge<double>(MetricNames.FileScanGcPausePercent, "percent");

        // Indexed by generation so the publish loop can pair MetricNames.Gens with
        // PressureSample.GcCollectedDelta positionally. The three series nest rather than
        // partition, and are published raw anyway; see docs/GOTCHAS.md,
        // "`GC.CollectionCount(g)` counts generation g OR HIGHER".
        var gcCollectedCounters = new[]
        {
            GenCounter(meter, MetricNames.Gens.Gen0),
            GenCounter(meter, MetricNames.Gens.Gen1),
            GenCounter(meter, MetricNames.Gens.Gen2),
        };

        // The Count guard is required, not defensive: .NET has no HasHeartbeatDetails helper
        // and HeartbeatDetailAtAsync uses ElementAt, which throws when the index is absent.
        FileScanCheckpoint? checkpoint = null;
        if (ctx.Info.HeartbeatDetails.Count > 0)
        {
            checkpoint = await ctx.Info.HeartbeatDetailAtAsync<FileScanCheckpoint>(0).ConfigureAwait(false);
        }

        // Before validation, so an attempt that dies on a bad checkpoint still counts as one
        // that started with resumed="true". Tag values are lowercased by hand; see Bool.
        meter.WithTags(new Dictionary<string, object>
        {
            [MetricNames.Tags.Retried] = Bool(ctx.Info.Attempt > 1),
            [MetricNames.Tags.Resumed] = Bool(checkpoint is not null),
        }).CreateCounter<long>(MetricNames.FileScanStarted).Add(1);

        if (checkpoint is not null && CheckpointDisagreement(checkpoint) is { } selfDisagreement)
        {
            throw Terminal(log,
                "refusing to resume from a checkpoint that disagrees with itself: " + selfDisagreement,
                "FileScanCheckpointInvalid");
        }

        // FileOptions.Asynchronous so ReadAsync is a real async read rather than a thread-pool
        // thread parked on a blocking one; at fileScan.concurrency 8 that is eight held
        // threads. BufferSize 0 means FileStream adds no buffer of its own, so this scan owns
        // the only buffer and the LOH gauge stays attributable. Only the two not-found
        // exceptions map to terminal; a mid-read IOException is transport, so the SDK retries.
        FileStream fs;
        try
        {
            fs = new FileStream(input.Path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous,
                BufferSize = 0,
            });
        }
        catch (FileNotFoundException e)
        {
            throw Terminal(log,
                $"no corpus at {input.Path}: {e.Message} Run scripts/gen-samples/gen-samples.sh.",
                "FileScanCorpusMissing");
        }
        catch (DirectoryNotFoundException e)
        {
            throw Terminal(log,
                $"no corpus directory on the way to {input.Path}: {e.Message} Paths resolve "
                + "against the config file's directory, never the working directory. Run "
                + "scripts/gen-samples/gen-samples.sh.",
                "FileScanCorpusMissing");
        }

        await using (fs.ConfigureAwait(false))
        {
            var fileBytes = fs.Length;
            var buffer = new byte[input.BufferBytes];

            // One fill from offset 0; any bufferBytes ConfigLoader accepts holds line 1.
            var headerFill = await FillAsync(fs, buffer, 0, ctx.CancellationToken).ConfigureAwait(false);
            var headerNewline = buffer.AsSpan(0, headerFill).IndexOf((byte)'\n');
            var fileRows = headerNewline > 0 ? ParseDigits(buffer.AsSpan(0, headerNewline)) : MalformedRow;

            if (fileRows <= 0)
            {
                throw Terminal(log,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{input.Path} does not start with a corpus header. Line 1 must be the row "
                        + $"count as plain ASCII digits followed by one LF; the first {headerFill} "
                        + $"bytes do not parse as that. A non-corpus file, a truncated generator "
                        + $"run and a CRLF rewrite all land here."),
                    "FileScanCorpusMalformed");
            }

            var headerLen = headerNewline + 1;
            if (headerLen != HeaderLength(fileRows))
            {
                throw Terminal(log,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{input.Path} declares {fileRows} rows in a {headerLen}-byte header, but "
                        + $"{fileRows} takes {HeaderLength(fileRows)} bytes to write plus its LF. "
                        + $"Every checkpoint validation derives the header length from the row "
                        + $"count alone."),
                    "FileScanCorpusMalformed");
            }

            // maxRows 0 is the documented sentinel for the whole file. Clamped rather than
            // rejected when it exceeds the corpus: the alternative is an EOF failure minutes
            // in that names the wrong thing.
            var rowsToScan = input.MaxRows > 0 && input.MaxRows < fileRows ? input.MaxRows : fileRows;
            var fullScan = rowsToScan == fileRows;

            // Set once per attempt so no panel hard-codes 1,724,588, which is wrong for three
            // of the four shipped corpora and wrong in a way that still renders.
            rowsExpectedGauge.Set(rowsToScan);

            // Invariant after every row, and what makes a checkpoint verifiable with no
            // second pass: consumed == headerLen + (RowOverhead x rows) + wordByteSum.
            long rows;
            long consumed;
            long indexSum;
            long wordByteSum;

            // Buffer state. Invariant: the file byte at offset `consumed` is buffer[bufStart],
            // and buffer[bufStart..bufEnd] is the unconsumed tail of what has been read.
            int bufStart;
            int bufEnd;

            if (checkpoint is null)
            {
                rows = 0;
                consumed = headerLen;
                indexSum = 0;
                wordByteSum = 0;

                // Reuse the header fill rather than seeking back: no extra syscall.
                bufStart = headerLen;
                bufEnd = headerFill;
            }
            else
            {
                // Corpus identity and bounds, before any resumed byte.
                if (CorpusDisagreement(checkpoint, input.Path, fileRows, fileBytes, rowsToScan)
                    is { } corpusDisagreement)
                {
                    throw Terminal(log, corpusDisagreement, "FileScanCorpusMismatch");
                }

                // Line-boundary proof: the byte before the cursor must be an LF. No special
                // case at the start, because at Rows == 0 the byte before headerLen is line
                // 1's own LF. The subtraction cannot go negative; see CheckpointDisagreement.
                var boundaryFill = await FillAsync(fs, buffer, checkpoint.ByteOffset - 1, 1, ctx.CancellationToken)
                    .ConfigureAwait(false);
                if (boundaryFill != 1 || buffer[0] != (byte)'\n')
                {
                    throw Terminal(log,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"the checkpoint's byte offset {checkpoint.ByteOffset} into {input.Path} "
                            + $"is not a line boundary: the byte before it is not an LF. Resuming "
                            + $"there would start mid-row, and the row parser would call a well "
                            + $"formed file malformed."),
                        "FileScanCheckpointInvalid");
                }

                rows = checkpoint.Rows;
                consumed = checkpoint.ByteOffset;

                // The line the whole case turns on: the accumulators are restored from the same
                // checkpoint as the read cursor, so rows between that checkpoint and the crash
                // are physically re-read and arithmetically counted once.
                indexSum = checkpoint.IndexSum;
                wordByteSum = checkpoint.WordByteSum;

                bufStart = 0;
                bufEnd = 0;

                if (rows < rowsToScan)
                {
                    // Fill at the cursor, then prove the row's identity. Skipped when the
                    // checkpoint already completed every row; see CorpusDisagreement.
                    bufEnd = await FillAsync(fs, buffer, checkpoint.ByteOffset, ctx.CancellationToken)
                        .ConfigureAwait(false);
                    var probeNewline = buffer.AsSpan(0, bufEnd).IndexOf((byte)'\n');
                    var probeIndex = probeNewline >= 0
                        ? ParseRowIndex(buffer.AsSpan(0, probeNewline))
                        : MalformedRow;

                    if (probeIndex != rows + 1)
                    {
                        throw Terminal(log,
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"the row at the checkpoint's byte offset {checkpoint.ByteOffset} in "
                                + $"{input.Path} carries index {probeIndex}, not the {rows + 1} the "
                                + $"checkpoint's {rows} completed rows imply. The corpus identity "
                                + $"and the line boundary matched, so the row cursor and the byte "
                                + $"cursor disagree."),
                            "FileScanCheckpointInvalid");
                    }

                    // The probed row is left unconsumed, so the read loop's first iteration
                    // processes it. No special-cased first row, so nothing double-counts it.
                }
            }

            var paceClause = input.TargetRowsPerSecond > 0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"target {input.TargetRowsPerSecond} rows/s, ~{rowsToScan / input.TargetRowsPerSecond}s")
                : "unthrottled, so this finishes inside one heartbeat throttle interval and shows "
                    + "nothing about resume";

            // The absolute resolved path, on every attempt: two corpora of the same target
            // size are byte-identical, so the identity check cannot name the directory.
            log.LogInformation(
                "scanning {Path}: {FileRows} rows, {FileBytes} bytes, from row {StartRow} at offset "
                + "{StartOffset} (attempt {Attempt}, {Pace})",
                input.Path, fileRows, fileBytes, rows + 1, consumed, ctx.Info.Attempt, paceClause);

            if (checkpoint is not null)
            {
                // The number this case exists to show. Core's throttle (see
                // HeartbeatActivities.ThrottleMs) is 24s at the shipped config.
                var staleness = DateTimeOffset.UtcNow - checkpoint.RecordedAtUtc;
                meter.CreateHistogram<TimeSpan>(MetricNames.FileScanStaleness).Record(staleness);

                // Each tooth's floor on the cursor panel. Absent rather than 0 until the first
                // resume, which is why the panel's floor target needs `or vector(0)`.
                resumedFromRowGauge.Set(rows);

                // Staleness x target rate, labelled an estimate in the line itself. See
                // docs/GOTCHAS.md, "A checkpoint cannot measure the work a crash made you
                // redo".
                var estimatedRedone = (long)(staleness.TotalSeconds * input.TargetRowsPerSecond);
                log.LogInformation(
                    "RESUMING at row {StartRow} of {RowsToScan}, offset {StartOffset}; checkpoint was "
                    + "{StalenessMs}ms old, so about {EstimatedRedone} rows will be re-read; that "
                    + "figure is staleness x target rate, an estimate (attempt {Attempt})",
                    rows + 1, rowsToScan, consumed, (long)staleness.TotalMilliseconds,
                    estimatedRedone, ctx.Info.Attempt);
            }

            // Fault: read the whole corpus into one array. A large read is a single LOH object
            // and the LOH is not compacted by default, so committed bytes and RSS step up at
            // the next collection and stay. File.ReadAllBytes touches every byte, which is why
            // the working set moves here and stays flat through a 500 MB streaming scan, and it
            // is synchronous, so it holds an activity-task thread.
            if (fault.SlurpWholeFile)
            {
                var slurped = File.ReadAllBytes(input.Path);
                log.LogWarning(
                    "FAULT slurpWholeFile: read {SlurpedBytes} bytes of {Path} into one array before "
                    + "scanning. loh_bytes and working_set_bytes step at the next collection, not "
                    + "now, and do not come back down",
                    slurped.Length, input.Path);
            }

            // Fault: decode every row to a string, and optionally keep them all. Pre-sized
            // from the corpus header: an un-sized List<string> grown to 8.6M elements doubles
            // into a 128 MiB array while the previous 64 MiB one is still garbage, ~192 MiB of
            // LOH churn that would move the LOH panel for an unrelated reason. ConfigLoader
            // refuses this knob with fileScan.concurrency > 1, about 10 GB of retained rows.
            var retained = fault.RetainScannedRows
                ? new List<string>((int)Math.Min(rowsToScan, int.MaxValue))
                : null;
            var decodeRows = fault.DecodeRowsToStrings || retained is not null;

            if (decodeRows)
            {
                log.LogWarning(
                    "FAULT {Knob}: decoding every row to a string. Expect allocated near 2.4x bytes "
                    + "read and a climbing gen0 rate; the live heap floor stays flat unless the "
                    + "strings are retained",
                    retained is not null ? "retainScannedRows" : "decodeRowsToStrings");
            }

            // Reset per attempt and never carried in the checkpoint, which stops a resumed
            // attempt sprinting to catch up. The pacer's clock starts when the attempt does.
            long rowsThisAttempt = 0;

            // The drain checkpoint is an edge, not a level. See the loop.
            var checkpointedForDrain = false;

            var lastLogAt = startedAt;
            var rowsAtLastLog = rows;
            var bytesAtLastLog = consumed;

            try
            {
                while (rows < rowsToScan)
                {
                    // Unconditional, once per batch. When the machine cannot keep up,
                    // `due <= elapsed` forever and the Task.Delay at the bottom never runs,
                    // which is otherwise the only place the token is observed.
                    ctx.CancellationToken.ThrowIfCancellationRequested();

                    // Polled separately, never folded into one `||`; see PiActivities. Gated
                    // on the edge for the reason HeartbeatActivities gives; here an ungated
                    // branch would fire every 100ms for 30s at the shipped config.
                    if (!checkpointedForDrain && ctx.WorkerShutdownToken.IsCancellationRequested)
                    {
                        checkpointedForDrain = true;
                        ctx.Heartbeat(new FileScanCheckpoint(
                            Rows: rows,
                            ByteOffset: consumed,
                            IndexSum: indexSum,
                            WordByteSum: wordByteSum,
                            FileRows: fileRows,
                            FileBytes: fileBytes,
                            RecordedAtUtc: DateTimeOffset.UtcNow));

                        log.LogInformation(
                            "worker draining; checkpointed at row {Rows} offset {Offset}. Still "
                            + "reading until ctx.CancellationToken fires in at most {GraceMs}ms",
                            rows, consumed,
                            (long)workerConfig.GracefulShutdownTimeout.TotalMilliseconds);
                    }

                    var rowsInBatch = 0;
                    var offsetAtBatchStart = consumed;

                    while (rowsInBatch < input.BatchRows && rows < rowsToScan)
                    {
                        var pending = buffer.AsSpan(bufStart, bufEnd - bufStart);
                        var newline = pending.IndexOf((byte)'\n');

                        if (newline < 0)
                        {
                            // Nothing but this loop decides where a line ends, so `consumed`
                            // is a byte count rather than a character count and cannot drift.
                            var remaining = bufEnd - bufStart;

                            if (remaining == buffer.Length)
                            {
                                throw Terminal(log,
                                    string.Create(
                                        CultureInfo.InvariantCulture,
                                        $"no LF in a full {buffer.Length}-byte buffer at offset "
                                        + $"{consumed} of {input.Path}, after {rows} rows. Either "
                                        + $"the file is not a corpus, or fileScan.bufferBytes is "
                                        + $"below the longest row, 76 bytes in the shipped "
                                        + $"corpora."),
                                    "FileScanCorpusMalformed");
                            }

                            if (remaining > 0 && bufStart > 0)
                            {
                                Buffer.BlockCopy(buffer, bufStart, buffer, 0, remaining);
                            }

                            bufStart = 0;
                            bufEnd = remaining;

                            // Token forwarded: a read stalled on a hung mount is unbounded.
                            var read = await fs.ReadAsync(buffer.AsMemory(remaining), ctx.CancellationToken)
                                .ConfigureAwait(false);

                            if (read == 0)
                            {
                                throw Terminal(log,
                                    string.Create(
                                        CultureInfo.InvariantCulture,
                                        $"{input.Path} ended at offset {consumed} after {rows} of "
                                        + $"{rowsToScan} rows. Its header declares {fileRows} "
                                        + $"rows, so the file is shorter than it says. An "
                                        + $"interrupted generator run looks like this."),
                                    "FileScanCorpusMalformed");
                            }

                            bufEnd += read;
                            continue;
                        }

                        var row = buffer.AsSpan(bufStart, newline);
                        var index = ParseRowIndex(row);

                        if (index == MalformedRow)
                        {
                            throw Terminal(log,
                                string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"malformed row at offset {consumed} of {input.Path}, after "
                                    + $"{rows} rows: expected {IndexDigits} digits, a space, '[', "
                                    + $"words, ']'. A CRLF rewrite of the corpus lands here on row "
                                    + $"1, where the byte before the LF is a CR."),
                                "FileScanCorpusMalformed");
                        }

                        // All of the per-row work: two adds against a 167us budget at the
                        // shipped rate. indexSum witnesses the row cursor and wordByteSum the
                        // byte cursor, so a resume bug that moves one shows up in one.
                        indexSum += index;
                        wordByteSum += newline + 1 - RowOverhead;

                        if (decodeRows)
                        {
                            // Garbage the instant it exists, unless retained. The same
                            // allocation kept and not kept separates two fault rungs.
                            var decoded = Encoding.ASCII.GetString(row);
                            retained?.Add(decoded);
                        }

                        bufStart += newline + 1;
                        consumed += newline + 1;
                        rows++;
                        rowsInBatch++;
                    }

                    rowsThisAttempt += rowsInBatch;

                    // Physically read, never rewound: a resumed attempt re-reads the rows
                    // between the checkpoint and the crash, which cost real time and I/O, while
                    // the cursor gauge below counts them once. Deltas, because both are
                    // counters; adding the absolute cursor per batch reports the triangular
                    // sum, 143 GB for a 2,875-batch 100 MB scan. The byte figure is the
                    // cursor's advance, not the syscall total, which keeps it paired one-to-one
                    // with the rows above.
                    rowsReadCounter.Add(rowsInBatch);
                    bytesReadCounter.Add(consumed - offsetAtBatchStart);
                    rowCursorGauge.Set(rows);

                    ctx.Heartbeat(new FileScanCheckpoint(
                        Rows: rows,
                        ByteOffset: consumed,
                        IndexSum: indexSum,
                        WordByteSum: wordByteSum,
                        FileRows: fileRows,
                        FileBytes: fileBytes,
                        RecordedAtUtc: DateTimeOffset.UtcNow));

                    // Cadence bounded by wall time, not row count, which would go sparse
                    // exactly when the system slows down. ~29 lines for the shipped corpus.
                    var sinceLastLog = Stopwatch.GetElapsedTime(lastLogAt);
                    if (sinceLastLog >= logInterval)
                    {
                        // One Sample() feeds both sinks and must be published: the call
                        // advances process-wide watermarks, so a discarded sample loses those
                        // bytes and collections permanently.
                        var pressure = ProcessPressure.Sample();

                        // Unconditional, so the series exists and reads near zero rather than
                        // NODATA. See docs/GOTCHAS.md, "A near-zero allocation counter is the
                        // read path working, not a dead metric".
                        //
                        // Measured (Release, net10.0, arm64, full 100 MB corpus), the figure
                        // MetricNames.FileScanBytesAllocated cites. The per-batch cost
                        // dominates, not the sampler: a FileScanCheckpoint plus the params
                        // object[] carrying it into Heartbeat() is 117 B per batch, the slope
                        // between a 2,875-batch scan (407,520 B) and a 29-batch one (74,648 B).
                        // A shipped-config scan is ~415 KB over 4m47s, 1.4 KB/s against
                        // 348 KB/s of reading; decodeRowsToStrings makes it 2.41x bytes read.
                        allocatedCounter.Add(pressure.AllocatedBytesDelta);

                        // Guarded on > 0. Core creates a series on first increment, so gen="2"
                        // stays absent rather than reading zero. Adding a zero would draw a
                        // flat line that reads the same as "this build never samples gen2".
                        for (var generation = 0; generation < gcCollectedCounters.Length; generation++)
                        {
                            var collected = pressure.GcCollectedDelta(generation);
                            if (collected > 0)
                            {
                                gcCollectedCounters[generation].Add(collected);
                            }
                        }

                        managedHeapGauge.Set(pressure.ManagedHeapBytes);

                        // Both are last-GC snapshots; see docs/GOTCHAS.md, "`GCMemoryInfo`
                        // describes the LAST collection, not now". PauseTimePercentage is not
                        // a rolling window either, so a scan that triggers no collection
                        // reports the worker's startup GCs forever.
                        lohGauge.Set(pressure.LohBytes);
                        gcPauseGauge.Set(pressure.GcPausePercent);

                        workingSetGauge.Set(pressure.WorkingSetBytes);

                        var intervalSeconds = sinceLastLog.TotalSeconds;
                        var rowsPerSecond = (long)((rows - rowsAtLastLog) / intervalSeconds);
                        var kilobytesPerSecond = (long)((consumed - bytesAtLastLog) / intervalSeconds / 1024);

                        log.LogInformation(
                            "row {Rows}/{RowsToScan} ({Percent}%) offset {Offset}/{FileBytes} at "
                            + "{RowsPerSecond} rows/s ({KilobytesPerSecond} KB/s); heap {HeapMib} "
                            + "MiB, alloc {AllocMibPerSecond} MiB/s, gc {Gen0}/{Gen1}/{Gen2}",
                            rows, rowsToScan, rows * 100 / rowsToScan, consumed, fileBytes,
                            rowsPerSecond, kilobytesPerSecond,
                            pressure.ManagedHeapBytes / (1024 * 1024),
                            Mib(pressure.AllocatedBytesDelta / intervalSeconds),
                            pressure.Gen0CollectedDelta, pressure.Gen1CollectedDelta,
                            pressure.Gen2CollectedDelta);

                        lastLogAt = Stopwatch.GetTimestamp();
                        rowsAtLastLog = rows;
                        bytesAtLastLog = consumed;
                    }

                    // The pacer: an absolute deadline, per batch. Absolute so a GC pause is
                    // absorbed rather than accumulated. Per batch because Task.Delay has a
                    // floor near one platform tick and cannot express the 167us a row gets at
                    // the shipped rate, so a per-row sleep would run at about 1000 rows/s
                    // whatever targetRowsPerSecond said. It degrades to full speed rather than
                    // falling behind silently.
                    if (input.TargetRowsPerSecond > 0)
                    {
                        var due = TimeSpan.FromSeconds((double)rowsThisAttempt / input.TargetRowsPerSecond);
                        var elapsed = Stopwatch.GetElapsedTime(startedAt);
                        if (due > elapsed)
                        {
                            await Task.Delay(due - elapsed, ctx.CancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Reused because a per-reason cancellation breakdown makes no claim a second
                // contributor falsifies. By that test repro_heartbeat_sent,
                // repro_activity_started and repro_activity_progress must not be reused: their
                // panels are unfiltered max()/sum() across activity types. The try covers the
                // read loop only, so a cancellation before it escapes without incrementing this.
                meter.WithTags(new Dictionary<string, object>
                {
                    [MetricNames.Tags.Reason] = ctx.CancelReason.ToString(),
                }).CreateCounter<long>(MetricNames.ActivityCancel).Add(1);

                // No ignoreCancellation knob here: it belongs to HeartbeatActivities, where
                // swallowing the exception wedges a one-minute batch rather than a five-minute
                // scan. The accidental version is covered by the token check at the loop top.
                log.LogInformation(
                    "scan cancelled ({Reason}) at row {Rows} of {RowsToScan}, offset {Offset}; "
                    + "attempt {Attempt} had read {RowsThisAttempt} rows",
                    ctx.CancelReason, rows, rowsToScan, consumed, ctx.Info.Attempt, rowsThisAttempt);
                throw;
            }

            // The completion check, the point of the whole case.
            var expectedIndexSum = rowsToScan * (rowsToScan + 1) / 2;
            var indexSumMatches = indexSum == expectedIndexSum;

            // The byte half of the verdict, with a closed form only on a full scan:
            // wordByteSum == fileBytes - headerLen - (RowOverhead x rows) says the cursor ended
            // at EOF. The words in an arbitrary prefix have no closed-form total.
            var expectedWordByteSum = fileBytes - headerLen - (RowOverhead * rowsToScan);
            var byteCursorMatches = !fullScan
                || (consumed == fileBytes && wordByteSum == expectedWordByteSum);

            var verified = indexSumMatches && byteCursorMatches;

            meter.WithTags(new Dictionary<string, object>
            {
                [MetricNames.Tags.Result] = verified ? MetricNames.Results.Match : MetricNames.Results.Mismatch,
            }).CreateCounter<long>(MetricNames.FileScanVerified).Add(1);

            if (retained is not null)
            {
                log.LogWarning(
                    "FAULT retainScannedRows: {Retained} decoded rows are still live. The managed heap "
                    + "gauge reads as a staircase with no falling edge, and gen1/gen2 should have "
                    + "appeared",
                    retained.Count);
            }

            if (!verified)
            {
                // A wrong aggregate must never return as a success, and retrying reproduces it
                // exactly while the verdict counter above already reads mismatch.
                throw Terminal(log, string.Create(
                    CultureInfo.InvariantCulture,
                    $"idempotency check failed on {input.Path} after {rows} of {rowsToScan} rows: "
                    + $"indexSum {indexSum} against the closed form {rowsToScan} x ({rowsToScan} + 1) "
                    + $"/ 2 = {expectedIndexSum} (off by {indexSum - expectedIndexSum}); wordByteSum "
                    + $"{wordByteSum} against {expectedWordByteSum}; ended at offset {consumed} of "
                    + $"{fileBytes}. Rows were double-counted or skipped across a resume: the "
                    + $"accumulator's origin and the read cursor's origin were not the same "
                    + $"checkpoint."),
                    "FileScanAggregateMismatch");
            }

            var byteVerdict = fullScan ? "== expected" : "(no closed form below a full scan)";
            log.LogInformation(
                "scan COMPLETE: {Rows} rows, {Bytes} bytes, ended at offset {EndOffset} of "
                + "{FileBytes}; indexSum {IndexSum} == expected, wordByteSum {WordByteSum} "
                + "{ByteVerdict}; attempt {Attempt} read {RowsThisAttempt} rows in {ElapsedSeconds}s",
                rows, consumed, consumed, fileBytes, indexSum, wordByteSum, byteVerdict,
                ctx.Info.Attempt, rowsThisAttempt,
                (long)Stopwatch.GetElapsedTime(startedAt).TotalSeconds);

            // Named arguments. Four adjacent longs, three at the same order of magnitude for
            // the 100 MB corpus, so a positional construction compiles clean and swaps them.
            return new FileScanResult(
                Rows: rows,
                Bytes: consumed,
                IndexSum: indexSum,
                WordByteSum: wordByteSum,
                Verified: true);
        }
    }

    /// <summary>One <c>gen</c>-tagged collections counter.</summary>
    private static MetricCounter<long> GenCounter(MetricMeter meter, string generation) =>
        meter.WithTags(new Dictionary<string, object>
        {
            [MetricNames.Tags.Gen] = generation,
        }).CreateCounter<long>(MetricNames.FileScanGcCollected, "collections");

    /// <summary>Seek and read once, into the whole buffer.</summary>
    private static Task<int> FillAsync(
        FileStream fs, byte[] buffer, long offset, CancellationToken cancellationToken) =>
        FillAsync(fs, buffer, offset, buffer.Length, cancellationToken);

    /// <summary>Seek to <paramref name="offset"/> and read at most <paramref name="count"/> bytes.</summary>
    /// <remarks>One read, not a loop. Every caller wants a short prefix or hands the result
    /// to the read loop, which refills on demand, so a short read is the normal case at the end
    /// of a file rather than an error.</remarks>
    private static async Task<int> FillAsync(
        FileStream fs, byte[] buffer, long offset, int count, CancellationToken cancellationToken)
    {
        fs.Seek(offset, SeekOrigin.Begin);
        return await fs.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Plain ASCII digits to a long, or <see cref="MalformedRow"/>.</summary>
    /// <remarks>Hand-rolled rather than <c>long.TryParse</c> over a decoded string, because
    /// the raw-byte path exists so a row never becomes a string. It is also stricter: no sign,
    /// whitespace, separators or leading plus, all of which TryParse accepts.</remarks>
    private static long ParseDigits(ReadOnlySpan<byte> digits)
    {
        if (digits.IsEmpty || digits.Length > MaxHeaderDigits)
        {
            return MalformedRow;
        }

        long value = 0;
        foreach (var b in digits)
        {
            var digit = b - (byte)'0';
            if (digit is < 0 or > 9)
            {
                return MalformedRow;
            }

            value = (value * 10) + digit;
        }

        return value;
    }

    /// <summary>Log at Error, then hand back a non-retryable application failure to throw.</summary>
    /// <remarks>Terminal means structural disagreement: the checkpoint against itself or the
    /// file, the file against its own header, or the aggregate against its closed form. None
    /// improve on a second attempt, and retrying spends the attempt budget
    /// docs/HEARTBEATING.md's three kill -9 cycles need. It logs as well as throwing so the two
    /// disagreeing numbers land on one line beside the progress lines.</remarks>
    private static ApplicationFailureException Terminal(ILogger log, string message, string errorType)
    {
        // A constant template with one PascalCase hole. CA2254 makes a non-constant template
        // an error and CA1727 a camelCase hole, so `log.LogError(message)` does not compile.
        log.LogError("{Message}", message);

        return new ApplicationFailureException(message, errorType, nonRetryable: true);
    }

    /// <summary>Go-style lowercase booleans, because that is what the dashboards match on.</summary>
    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>Bytes as MiB to one decimal, invariant. CA1305 is an error here.</summary>
    private static string Mib(double bytes) =>
        (bytes / (1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture);
}
