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
/// One genuinely long activity over a real file: a raw-byte scan that checkpoints an EXACT
/// byte cursor, resumes from it idempotently, and publishes what it costs the worker.
/// </summary>
/// <remarks>
/// Registered as an INSTANCE with
/// <c>.AddAllActivities(new FileScanActivities(cfg.Fault, cfg.Worker))</c>, alongside the
/// other three activity objects rather than as a second argument to one of them --
/// AddAllActivities takes exactly one instance. The wire name is <c>ScanFile</c>, because the
/// SDK trims the <c>Async</c> suffix, and that string is pinned in the dashboards as
/// <c>activity_type="ScanFile"</c>.
/// <para>
/// TWO HALVES, and they are separable. The RESUME half is the byte cursor plus the rewound
/// accumulator: <see cref="FileScanJob"/>'s <see cref="FileScanCheckpoint"/> remarks carry the
/// proof, and the rule to take away is THE ACCUMULATOR'S ORIGIN AND THE READ CURSOR'S ORIGIN
/// MUST BE THE SAME CHECKPOINT. Restore the cursor from the heartbeat and the accumulator from
/// zero and every row between the checkpoint and the crash is counted twice; restore the
/// accumulator and leave the cursor at zero and they are counted twice the other way. Neither
/// throws. The closed form at the end is what turns both into a loud failure. A version that
/// starts the accumulator at 0 and adds the checkpointed sum once at the end is EQUALLY
/// CORRECT -- the rule is about the origins agreeing, not about where the addition happens,
/// and a reader who memorises the ritual instead of the rule will get the next one wrong.
/// </para>
/// <para>
/// The PRESSURE half is <see cref="ProcessPressure"/> plus the three fault knobs. It exists
/// because the seed case's <c>Task.Delay</c> loop allocates nothing and touches no I/O, so
/// every memory panel reads the same whether the activity is working or asleep.
/// </para>
/// <para>
/// THE FAULT CONFIG ARRIVES THROUGH THE CONSTRUCTOR, the pattern
/// <see cref="HeartbeatActivities"/> establishes, so there is no ambient global for workflow
/// code to reach and "workflows must never read the fault config" is enforced by the type
/// system rather than by a comment.
/// </para>
/// <para>
/// The WORKER config arrives for the same duller reason it does there: the drain line below
/// reports how long this activity has before <c>ctx.CancellationToken</c> fires, and that is
/// <c>worker.gracefulShutdownTimeout</c>. A literal agrees with config.yaml right up until
/// someone edits config.yaml, and then the one line that tells a reader how long the wedge
/// lasts is wrong.
/// </para>
/// <para>
/// <see cref="FileScanConfig"/> is deliberately NOT injected, which departs from the plan this
/// was built to. Every value the scan needs is job shape and travels in
/// <see cref="FileScanInput"/> -- that record's remarks explain why, and the corpus PATH is
/// the load-bearing case: two workers' config.yaml files can resolve the same corpus
/// differently, so a resume has to compare a checkpoint against the path the WORKFLOW named,
/// not the one this process happens to hold. That leaves nothing in the block for this class
/// to read, and an unread primary-constructor parameter is CS9113, which is an error at this
/// repo's settings.
/// </para>
/// <para>
/// Wall clock, <c>Stopwatch</c>, real file handles and <c>GC.</c> reads are all fine in here,
/// because this is activity code. None of it may move into a workflow.
/// </para>
/// </remarks>
public sealed class FileScanActivities(FaultConfig fault, WorkerConfig? worker = null)
{
    /// <summary>Fixed bytes per corpus row, INCLUDING its LF. <c>gen_samples.py:48</c>.</summary>
    /// <remarks>
    /// 10 index digits + one space + <c>[</c> + six inner separators + <c>]</c> + LF = 20. The
    /// variable part is the seven words, so
    /// <c>rowBytesIncludingLf == RowOverhead + wordBytes</c> and therefore
    /// <c>ByteOffset == headerLen + (RowOverhead x rows) + wordByteSum</c> at every checkpoint.
    /// That identity is what would catch a byte cursor that had drifted, which is the whole
    /// reason this scan finds its own line breaks instead of using a StreamReader.
    /// </remarks>
    public const int RowOverhead = 20;

    /// <summary>Width of the zero-padded row index, <c>%010d</c>.</summary>
    public const int IndexDigits = 10;

    /// <summary>A malformed row, from <see cref="ParseRowIndex"/>. Row indices are 1-based.</summary>
    /// <remarks>
    /// A SENTINEL RATHER THAN AN EXCEPTION, so the parser stays pure, allocation-free and
    /// testable with no <c>ActivityEnvironment</c> -- the
    /// <see cref="WeatherActivities.IsTransportFailure"/> precedent -- while the caller, which
    /// is the only thing that knows the row number, the byte offset and the path, owns the
    /// message.
    /// </remarks>
    public const long MalformedRow = -1;

    /// <summary>Longest legal decimal the header parser will accept, to keep it overflow-free.</summary>
    private const int MaxHeaderDigits = 18;

    // Optional for the reason HeartbeatActivities gives: a caller that has not been taught to
    // pass it still lands on the SDK's own defaults rather than a null deref. Any worker built
    // from a config.yaml with a different worker.gracefulShutdownTimeout MUST pass its
    // WorkerConfig, or the drain line below names a grace window nothing is using.
    private readonly WorkerConfig workerConfig = worker ?? new WorkerConfig();

    /// <summary>Header length for a corpus of <paramref name="fileRows"/> rows: digits + LF.</summary>
    /// <remarks>
    /// PURE ARITHMETIC over a number the checkpoint already carries, which is what lets
    /// <see cref="CheckpointDisagreement"/> verify the byte identity with NO I/O at all --
    /// before the file is opened, before anything is seeked, and therefore before a bad
    /// checkpoint can cost a syscall.
    /// <para>
    /// No <c>ToString().Length</c>, because CA1305 is an error here and the provider-less
    /// overload would not compile; the loop also allocates nothing.
    /// </para>
    /// </remarks>
    public static int HeaderLength(long fileRows)
    {
        var digits = 1;
        for (var remaining = fileRows; remaining >= 10; remaining /= 10)
        {
            digits++;
        }

        return digits + 1;
    }

    /// <summary>Parse and structurally validate one row, LF excluded. THE PER-ROW HOT PATH.</summary>
    /// <param name="row">
    /// The row's bytes WITHOUT its trailing LF, exactly as the read loop slices it out of the
    /// buffer between two line breaks.
    /// </param>
    /// <returns>The 1-based row index, or <see cref="MalformedRow"/>.</returns>
    /// <remarks>
    /// <c>public static</c> and pure so it is testable with no <c>ActivityEnvironment</c>.
    /// <para>
    /// IT DOES NOT CHECK THAT THE INDEX IS THE ONE EXPECTED HERE, and that omission is
    /// deliberate rather than an oversight. Comparing every row's index against
    /// <c>rows + 1</c> would NOT catch the bug this case exists to demonstrate: a resume that
    /// rewinds the cursor but not the accumulator restores <c>rows</c> from the checkpoint
    /// too, so every row it reads satisfies <c>index == rows + 1</c> and the per-row check
    /// passes while the total comes out short. Only the closed form at completion catches it.
    /// The index identity IS checked once, against the row a resume lands on, because there it
    /// answers a different question -- "is this the row the checkpoint says it is" -- and that
    /// one a per-row check cannot answer.
    /// </para>
    /// <para>
    /// A CRLF CORPUS FAILS HERE, LOUDLY, which is the point. The read loop splits on LF, so on
    /// CRLF input the last byte of every row would be <c>\r</c> rather than <c>]</c> and this
    /// returns <see cref="MalformedRow"/> on row 1. The alternative -- tolerating it -- is the
    /// <c>line.Length + 1</c> failure mode: one byte of cursor drift per row, no exception, and
    /// a resume that lands mid-row several hundred thousand rows later.
    /// </para>
    /// <para>
    /// The length floor is the STRUCTURAL minimum (10 digits, a space, a bracket, a bracket)
    /// and not <see cref="RowOverhead"/>. The overhead accounting stays exact for any length
    /// whatever, because the word-byte accumulator is DEFINED as
    /// <c>rowLength + 1 - RowOverhead</c>; a hypothetical row with no words would simply
    /// contribute a negative term and the offset identity would still hold. Rows in the
    /// shipped corpora are 41 to 76 bytes including the LF.
    /// </para>
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

    /// <summary>Is this checkpoint self-consistent? ARITHMETIC ONLY -- no file is opened.</summary>
    /// <param name="checkpoint">What came back from <c>HeartbeatDetailAtAsync</c>.</param>
    /// <returns><c>null</c> when it agrees with itself, otherwise why it does not.</returns>
    /// <remarks>
    /// THE SCHEMA-DRIFT TRIPWIRE, and it runs before any I/O because it needs none: every fact
    /// it checks is carried in the checkpoint. Heartbeat details round-trip through the data
    /// converter by NAME, so a renamed record parameter binds nothing and yields
    /// <c>default(T)</c> -- and a zeroed <see cref="FileScanCheckpoint.IndexSum"/> beside a
    /// correct <see cref="FileScanCheckpoint.ByteOffset"/> is not an error, it is a silent
    /// wrong answer that surfaces at completion as an aggregate mismatch, sending the reader
    /// after a resume bug that does not exist.
    /// <para>
    /// Both closed forms are checked. <c>IndexSum == rows x (rows + 1) / 2</c> ties the
    /// accumulator to the ROW cursor and holds because a checkpoint is only ever written by a
    /// scan whose accumulator started at row 1.
    /// <c>ByteOffset == headerLen + (RowOverhead x rows) + wordByteSum</c> ties it to the BYTE
    /// cursor, with <c>headerLen</c> derived from
    /// <see cref="FileScanCheckpoint.FileRows"/> by <see cref="HeaderLength"/>.
    /// </para>
    /// <para>
    /// The non-negativity bounds are not decoration: together with the byte identity they are
    /// what guarantees <c>ByteOffset >= headerLen >= 2</c>, and therefore that the
    /// line-boundary proof's <c>Seek(ByteOffset - 1)</c> cannot go negative.
    /// </para>
    /// </remarks>
    public static string? CheckpointDisagreement(FileScanCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        // FileRows first, because everything below derives the header length from it. Zero is
        // exactly what a renamed or dropped record parameter produces.
        if (checkpoint.FileRows <= 0 || checkpoint.FileBytes <= 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"the checkpoint carries fileRows {checkpoint.FileRows} and fileBytes "
                + $"{checkpoint.FileBytes}, and both must be positive. A zero here is what a "
                + $"RENAMED FileScanCheckpoint parameter produces: heartbeat details bind by name "
                + $"through the data converter, an unbound parameter yields default(T), and the "
                + $"result is a checkpoint that deserializes without error and means nothing.");
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
                + $"{expectedOffset}. Every corpus row is exactly {RowOverhead} bytes of fixed "
                + $"overhead plus its words, so these three numbers cannot disagree in a "
                + $"checkpoint this scan wrote.");
        }

        var expectedIndexSum = checkpoint.Rows * (checkpoint.Rows + 1) / 2;
        if (checkpoint.IndexSum != expectedIndexSum)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"the checkpoint's indexSum {checkpoint.IndexSum} is not the closed form for its "
                + $"own {checkpoint.Rows} rows, {checkpoint.Rows} x ({checkpoint.Rows} + 1) / 2 = "
                + $"{expectedIndexSum}. The accumulator and the row cursor were written from the "
                + $"same batch, so they cannot disagree unless the checkpoint was not produced by "
                + $"this scan -- or a parameter was renamed and IndexSum bound to default(T).");
        }

        if (checkpoint.RecordedAtUtc == default)
        {
            return "the checkpoint carries no RecordedAtUtc. That is the same unbound-parameter "
                + "failure the fileRows check describes, and its specific cost is "
                + "repro_file_scan_staleness: a default DateTimeOffset would record a two-thousand "
                + "year staleness into a histogram whose top boundary is 90 seconds.";
        }

        return null;
    }

    /// <summary>Is this checkpoint about THIS file, and inside THIS job's bounds?</summary>
    /// <param name="checkpoint">A checkpoint that has already passed <see cref="CheckpointDisagreement"/>.</param>
    /// <param name="path">The resolved absolute corpus path, for the message.</param>
    /// <param name="fileRows">Row count from line 1 of the file now open.</param>
    /// <param name="fileBytes"><c>FileStream.Length</c> of the file now open.</param>
    /// <param name="rowsToScan">Rows this invocation intends to read.</param>
    /// <returns><c>null</c> when the checkpoint may be resumed from, otherwise why not.</returns>
    /// <remarks>
    /// THE FAILURE THIS PREVENTS IS A PLAUSIBLE WRONG ANSWER, which is why it runs before a
    /// single resumed byte is read. Without it a resume can seek into a DIFFERENT corpus, land
    /// on a valid line boundary whose leading index happens to match, pass the row-identity
    /// proof, run to completion, report <c>outcome="completed"</c> and return a sum computed
    /// over a mixture of two files. The aggregate check would eventually notice, but the
    /// failure would land on the AGGREGATE, which sends the reader after a resume bug instead
    /// of after "the corpus changed underneath you".
    /// <para>
    /// <c>(fileRows, fileBytes)</c> is a sufficient discriminator for the shipped corpora and
    /// costs two O(1) reads. <c>gen_samples.py</c> keys its word seed on TARGET SIZE, so any
    /// two of the four differ in both numbers, and a regenerated corpus of the same target
    /// size is byte-identical. <see cref="FileScanCheckpoint"/>'s remarks carry the argument
    /// against a digest and against carrying the path.
    /// </para>
    /// <para>
    /// The bounds are <c>&lt;=</c> and not <c>&lt;</c>, and that matters for one reachable
    /// case: an attempt that read the last row, heartbeated, and was killed before its result
    /// reached the server leaves a checkpoint with <c>Rows == rowsToScan</c> and
    /// <c>ByteOffset == fileBytes</c>. Rejecting that as out of bounds would fail the workflow
    /// terminally for having finished, which reads as "resume is broken" -- the worst way for
    /// this case to fail. The caller skips the row-identity proof when there is nothing left to
    /// read, and the completion check then verifies the restored aggregate and returns.
    /// </para>
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
                + $"bytes. Refusing to resume: the cursor would land in a different stream and the "
                + $"answer would look plausible.");
        }

        if (checkpoint.Rows > rowsToScan)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"the checkpoint has already completed {checkpoint.Rows} rows but this invocation "
                + $"only intends to scan {rowsToScan}. A checkpoint written under a larger maxRows "
                + $"is a DIFFERENT JOB, and resuming from it would report a total for a question "
                + $"nobody asked.");
        }

        if (checkpoint.ByteOffset > fileBytes)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"the checkpoint's byte offset {checkpoint.ByteOffset} is past the end of {path} "
                + $"({fileBytes} bytes). The corpus identity above already matched, so this means "
                + $"the checkpoint's own arithmetic and the file disagree.");
        }

        return null;
    }

    /// <summary>Scan the corpus, checkpointing an exact byte cursor. Wire name <c>ScanFile</c>.</summary>
    /// <remarks>
    /// READ THE RESUME PATH FIRST. Steps 0 through 6 all run before a single new byte is read,
    /// and the ORDER is the design: the cheapest and most decisive checks come first, so a
    /// checkpoint that cannot be resumed from costs no I/O at all, and a corpus that changed is
    /// named as such rather than surfacing five minutes later as a wrong total.
    /// </remarks>
    [Activity]
    public async Task<FileScanResult> ScanFileAsync(FileScanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Capture ONCE. ActivityExecutionContext.Current is an AsyncLocal lookup that throws
        // outside an activity, which matters the moment any of this moves into a Task.Run, a
        // continuation or a sampling timer -- and ProcessPressure's remarks record that a
        // background Timer is exactly where it stops flowing.
        var ctx = ActivityExecutionContext.Current;
        var meter = ctx.MetricMeter;
        var log = ctx.Logger;

        var startedAt = Stopwatch.GetTimestamp();

        // A HAND-WRITTEN INPUT IS NOT A VALIDATED CONFIG, and these are checked before
        // anything else because two of them are hangs rather than errors.
        // ConfigLoader.ValidateFileScan bounds config.yaml and this activity deliberately does
        // not re-check its shape, but nothing bounds a `temporal workflow execute --input`
        // payload -- the same gap WeatherActivities.HttpBudget's floor exists to cover.
        // batchRows 0 completes no rows between checks, so the loop heartbeats an unchanged
        // checkpoint forever and reads on the board as a stalled disk. logIntervalMs 0 samples
        // and prints every batch, so the sampler comes to dominate the allocation counter it
        // publishes. bufferBytes 0 fails a legal file as "a full buffer with no LF", which
        // names the wrong cause entirely.
        if (input.BatchRows <= 0 || input.BufferBytes <= 0 || input.LogIntervalMs <= 0)
        {
            throw Terminal(log,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"batchRows ({input.BatchRows}), bufferBytes ({input.BufferBytes}) and "
                    + $"logIntervalMs ({input.LogIntervalMs}) must all be > 0. ConfigLoader bounds "
                    + $"the fileScan: block but not a hand-written workflow input, and each of "
                    + $"these at zero fails as something other than what it is."),
                "FileScanInputInvalid");
        }

        if (string.IsNullOrWhiteSpace(input.Path))
        {
            throw Terminal(log,
                "fileScan.path reached the activity empty. A missing corpus is a CONFIG BUG, not "
                + "a transient fault, so this is non-retryable: burning ten attempts on it proves "
                + "nothing and buries the cause under an ActivityFailure chain. Generate the "
                + "corpora with scripts/gen-samples/gen-samples.sh.",
                "FileScanCorpusMissing");
        }

        var logInterval = TimeSpan.FromMilliseconds(input.LogIntervalMs);

        // EVERY INSTRUMENT IS HOISTED. CreateCounter/CreateGauge cross the native bridge and
        // resolve a name plus a tag set; at 2,874 batches and 29 samples per scan, creating
        // them inside the loop would put that cost on the pace budget the loop is trying to
        // hold. The SDK's own doc says the same: "performance is better if this is reused".
        //
        // attempt is the ONE tag in this file beyond the meter's roots, on the two cost
        // counters only, and MetricNames.FileScanRowsRead carries the argument for why it is
        // safe there and destroys the panel on a gauge. InvariantCulture because CA1305 is an
        // error here.
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

        // One counter per generation, indexed BY generation, so the publish loop below can
        // pair MetricNames.Gens with PressureSample.GcCollectedDelta positionally. Read
        // MetricNames.Gens before building a panel on these: GC.CollectionCount(g) counts
        // generation g OR HIGHER, so the three series NEST rather than partition and their sum
        // is not "total collections". They are published raw anyway, because raw is what
        // dotnet-counters and every other .NET exporter reports.
        var gcCollectedCounters = new[]
        {
            GenCounter(meter, MetricNames.Gens.Gen0),
            GenCounter(meter, MetricNames.Gens.Gen1),
            GenCounter(meter, MetricNames.Gens.Gen2),
        };

        // ---------------------------------------------------------------------------------
        // STEP 0. The checkpoint, and its arithmetic-only validation.
        // ---------------------------------------------------------------------------------

        // The Count guard is REQUIRED, not defensive: there is no HasHeartbeatDetails helper
        // in .NET and HeartbeatDetailAtAsync uses ElementAt, which throws when the index is
        // absent.
        FileScanCheckpoint? checkpoint = null;
        if (ctx.Info.HeartbeatDetails.Count > 0)
        {
            checkpoint = await ctx.Info.HeartbeatDetailAtAsync<FileScanCheckpoint>(0).ConfigureAwait(false);
        }

        // Counted here, BEFORE validation, so an attempt that dies on a bad checkpoint is
        // still counted as an attempt that started with resumed="true". Both tag values are
        // LOWERCASED BY HAND: bool.ToString() returns "True", every dashboard selector matches
        // resumed="true", and the capitalized value does not error -- the panel is simply
        // empty forever.
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

        // ---------------------------------------------------------------------------------
        // STEP 1. Open, measure, and read the header.
        // ---------------------------------------------------------------------------------

        // FileOptions.Asynchronous so ReadAsync is a real async read rather than a thread-pool
        // thread parked on a blocking one -- at fileScan.concurrency 8 that difference is eight
        // held threads. BufferSize 0 means FileStream adds NO buffer of its own: this scan owns
        // the only buffer in the path, which is what makes bufferBytes mean what it says and
        // keeps the LOH gauge attributable to one allocation.
        //
        // FileNotFoundException and DirectoryNotFoundException are mapped to TERMINAL below
        // and everything else is left alone. That is the discriminator this case needs, and it
        // is a narrower thing than WeatherActivities.IsTransportFailure: every STRUCTURAL
        // failure here is one this activity raises itself, with the numbers in hand, so there
        // is no exception type to classify. What is left -- IOException and
        // UnauthorizedAccessException mid-read -- is transport against a file that still
        // matches, and the SDK's retry policy is exactly right for it.
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
                $"no corpus at {input.Path}: {e.Message} sample_files/ is gitignored and generated, "
                + "so a fresh clone has no corpus. Run scripts/gen-samples/gen-samples.sh. This is "
                + "non-retryable because a missing file is a config bug, not a transient fault.",
                "FileScanCorpusMissing");
        }
        catch (DirectoryNotFoundException e)
        {
            throw Terminal(log,
                $"no corpus directory on the way to {input.Path}: {e.Message} The path is resolved "
                + "against the CONFIG FILE's directory, never the working directory, so this names "
                + "the file config.yaml pointed at. Run scripts/gen-samples/gen-samples.sh.",
                "FileScanCorpusMissing");
        }

        await using (fs.ConfigureAwait(false))
        {
            var fileBytes = fs.Length;
            var buffer = new byte[input.BufferBytes];

            // One fill from offset 0. The header is line 1 and it is 8 bytes for the shipped
            // 100 MB corpus, so any bufferBytes ConfigLoader accepts holds it many times over.
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
                        + $"bytes read do not parse as that. This is what a non-corpus file, a "
                        + $"truncated generator run or a CRLF rewrite looks like from here."),
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
                        + $"The header length is load-bearing: every byte identity in this case, "
                        + $"and every checkpoint validation, derives it from the row count alone."),
                    "FileScanCorpusMalformed");
            }

            // maxRows 0 is the documented sentinel for the whole file. Clamped rather than
            // rejected when it exceeds the corpus, because "stop after this many rows" of a
            // shorter file is honestly the whole file -- and the alternative is an EOF failure
            // several minutes in that names the wrong thing.
            var rowsToScan = input.MaxRows > 0 && input.MaxRows < fileRows ? input.MaxRows : fileRows;
            var fullScan = rowsToScan == fileRows;

            // Set once per attempt. It exists so no panel hard-codes 1,724,588, which would be
            // wrong for three of the four shipped corpora and wrong in a way that RENDERS.
            rowsExpectedGauge.Set(rowsToScan);

            // The three accumulators, and the invariant that ties them together:
            //   consumed == headerLen + (RowOverhead x rows) + wordByteSum
            // holds after every single row, which is what makes a checkpoint verifiable with
            // no second pass over the file.
            long rows;
            long consumed;
            long indexSum;
            long wordByteSum;

            // Buffer state. INVARIANT: the file byte at offset `consumed` is buffer[bufStart],
            // and buffer[bufStart..bufEnd] is the unconsumed tail of what has been read.
            int bufStart;
            int bufEnd;

            if (checkpoint is null)
            {
                rows = 0;
                consumed = headerLen;
                indexSum = 0;
                wordByteSum = 0;

                // Reuse the header fill rather than seeking back: the buffer already holds the
                // file from offset 0, so row 1 starts at buffer[headerLen] and the invariant
                // holds with no extra syscall.
                bufStart = headerLen;
                bufEnd = headerFill;
            }
            else
            {
                // -------------------------------------------------------------------------
                // STEPS 2 and 3. Corpus identity and bounds, before any resumed byte.
                // -------------------------------------------------------------------------
                if (CorpusDisagreement(checkpoint, input.Path, fileRows, fileBytes, rowsToScan)
                    is { } corpusDisagreement)
                {
                    throw Terminal(log, corpusDisagreement, "FileScanCorpusMismatch");
                }

                // -------------------------------------------------------------------------
                // STEP 4. LINE-BOUNDARY PROOF. The byte before the cursor must be an LF.
                // -------------------------------------------------------------------------
                // Uniform, with no special case at the start: at Rows == 0 the offset is
                // headerLen and the byte before it is line 1's own LF. The subtraction cannot
                // go negative -- CheckpointDisagreement's byte identity plus its
                // non-negativity bounds force ByteOffset >= headerLen >= 2.
                var boundaryFill = await FillAsync(fs, buffer, checkpoint.ByteOffset - 1, 1, ctx.CancellationToken)
                    .ConfigureAwait(false);
                if (boundaryFill != 1 || buffer[0] != (byte)'\n')
                {
                    throw Terminal(log,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"the checkpoint's byte offset {checkpoint.ByteOffset} into {input.Path} "
                            + $"is not a line boundary: the byte before it is not an LF. Resuming "
                            + $"there would start mid-row, and the row parser would report a "
                            + $"malformed corpus for a file that is perfectly well formed."),
                        "FileScanCheckpointInvalid");
                }

                rows = checkpoint.Rows;
                consumed = checkpoint.ByteOffset;

                // THE LINE THE WHOLE CASE TURNS ON. The accumulators are restored from the SAME
                // checkpoint the read cursor is, so the rows between that checkpoint and the
                // crash are physically re-read and arithmetically counted exactly ONCE.
                indexSum = checkpoint.IndexSum;
                wordByteSum = checkpoint.WordByteSum;

                bufStart = 0;
                bufEnd = 0;

                if (rows < rowsToScan)
                {
                    // ---------------------------------------------------------------------
                    // STEPS 5 and 6. Fill at the cursor and prove the row's own identity.
                    // ---------------------------------------------------------------------
                    // Skipped when the checkpoint has already completed every row, which is
                    // reachable: an attempt that read the last row, heartbeated, and died
                    // before its result reached the server leaves exactly that. There is no
                    // row at ByteOffset then, and demanding one would fail the workflow for
                    // having finished.
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
                                + $"checkpoint's {rows} completed rows imply. The corpus identity and "
                                + $"the line boundary both matched, so this is the row cursor and the "
                                + $"byte cursor disagreeing -- the one failure a resume must never "
                                + $"read past."),
                            "FileScanCheckpointInvalid");
                    }

                    // The probed row is left UNCONSUMED: bufStart stays 0, so the read loop's
                    // first iteration finds this same LF and processes it as the batch's first
                    // row. There is deliberately no special-cased first row, and therefore no
                    // path on which the resume row is counted twice or skipped.
                }
            }

            // -----------------------------------------------------------------------------
            // STEP 7. Staleness, the resume gauges, and the console lines.
            // -----------------------------------------------------------------------------
            var paceClause = input.TargetRowsPerSecond > 0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"target {input.TargetRowsPerSecond} rows/s, ~{rowsToScan / input.TargetRowsPerSecond}s")
                : "UNTHROTTLED, so this will finish inside one heartbeat throttle interval and "
                    + "demonstrate nothing about resume";

            // The ABSOLUTE RESOLVED PATH, on every attempt, so a working-directory change is
            // visible even in the cases the corpus-identity check would miss -- two corpora of
            // the same target size are byte-identical, so identity alone cannot tell you which
            // directory you are in.
            log.LogInformation(
                "scanning {Path}: {FileRows} rows, {FileBytes} bytes, from row {StartRow} at offset "
                + "{StartOffset} (attempt {Attempt}, {Pace})",
                input.Path, fileRows, fileBytes, rows + 1, consumed, ctx.Info.Attempt, paceClause);

            if (checkpoint is not null)
            {
                // THE number this case exists to show. Core throttles heartbeats to
                // min(heartbeatTimeout x 0.8, worker.maxHeartbeatThrottleInterval) = 24s at the
                // shipped config, so the details the server holds lag what the activity did.
                // This measures that lag directly, and it is why resume must be idempotent:
                // some work WILL be redone.
                var staleness = DateTimeOffset.UtcNow - checkpoint.RecordedAtUtc;
                meter.CreateHistogram<TimeSpan>(MetricNames.FileScanStaleness).Record(staleness);

                // Each tooth's FLOOR on the cursor panel. Written only on a resumed attempt, so
                // the series is ABSENT rather than 0 until the first resume -- which is why the
                // panel's floor target needs `or vector(0)`.
                resumedFromRowGauge.Set(rows);

                // The redone figure is labelled an ESTIMATE in the line itself, because it is
                // staleness x target rate. The exact number is the difference between this
                // line's row and the last periodic line in the scrollback, and the checkpoint
                // is STRUCTURALLY INCAPABLE of carrying it -- FileScanJob.cs has the proof.
                var estimatedRedone = (long)(staleness.TotalSeconds * input.TargetRowsPerSecond);
                log.LogInformation(
                    "RESUMING at row {StartRow} of {RowsToScan}, offset {StartOffset}; checkpoint was "
                    + "{StalenessMs}ms old, so about {EstimatedRedone} rows will be re-read -- that "
                    + "figure is staleness x target rate, an ESTIMATE (attempt {Attempt})",
                    rows + 1, rowsToScan, consumed, (long)staleness.TotalMilliseconds,
                    estimatedRedone, ctx.Info.Attempt);
            }

            // FAULT: read the whole corpus into ONE array before scanning anything.
            //
            // Proves a large read is a single LOH object and that the LOH is not compacted by
            // default, so committed bytes and RSS step up and do not come back. Two corrections
            // to the obvious reading, both measured and both in MetricNames: the LOH gauge is a
            // LAST-GC SNAPSHOT, so it steps at the next collection rather than in this sample;
            // and File.ReadAllBytes touches every byte, which is why the working set moves here
            // and stays flat through a 500 MB streaming scan.
            //
            // ReadAllBytes is also SYNCHRONOUS, so it holds an activity-task thread for its
            // whole duration -- that is what moves the thread-pool panel. It does NOT produce a
            // heartbeat timeout: 500 MB off page cache is well under a second.
            if (fault.SlurpWholeFile)
            {
                var slurped = File.ReadAllBytes(input.Path);
                log.LogWarning(
                    "FAULT slurpWholeFile: read {SlurpedBytes} bytes of {Path} into ONE array before "
                    + "scanning. Watch loh_bytes and working_set_bytes step at the NEXT collection, "
                    + "not now, and not come back down",
                    slurped.Length, input.Path);
            }

            // FAULT: decode every row to a string, and optionally keep them all.
            //
            // PRE-SIZED FROM THE CORPUS HEADER, and that is not a micro-optimisation. An
            // un-sized List<string> grown to 8.6M elements doubles into a 128 MiB backing array
            // while the previous 64 MiB one is still garbage, adding ~192 MiB of LOH churn --
            // so the LOH panel would move for a second, unrelated reason and the attribution
            // this knob exists for would be gone. The pre-sized array is itself one LOH object
            // (13.8 MB of references for the 100 MB corpus); that cost is paid once, up front,
            // and is visible as a step rather than as churn.
            //
            // ConfigLoader REFUSES this knob together with fileScan.concurrency > 1: eight
            // retained scans of the 500 MB corpus is about 10 GB and the failure is an
            // OOM-killed worker, not an empty panel.
            var retained = fault.RetainScannedRows
                ? new List<string>((int)Math.Min(rowsToScan, int.MaxValue))
                : null;
            var decodeRows = fault.DecodeRowsToStrings || retained is not null;

            if (decodeRows)
            {
                log.LogWarning(
                    "FAULT {Knob}: decoding every row to a string. Expect allocated to land near 2.4x "
                    + "bytes read and the gen0 rate to climb; the live heap floor stays flat unless "
                    + "the strings are retained",
                    retained is not null ? "retainScannedRows" : "decodeRowsToStrings");
            }

            // -----------------------------------------------------------------------------
            // STEP 8 onwards. The read loop.
            // -----------------------------------------------------------------------------

            // Reset PER ATTEMPT and never carried in the checkpoint, which is what stops a
            // resumed attempt sprinting to catch up on the work it "owes". The pacer's clock
            // starts when this attempt starts.
            long rowsThisAttempt = 0;

            // The drain checkpoint is an EDGE, not a level. See the loop.
            var checkpointedForDrain = false;

            var lastLogAt = startedAt;
            var rowsAtLastLog = rows;
            var bytesAtLastLog = consumed;

            try
            {
                while (rows < rowsToScan)
                {
                    // UNCONDITIONALLY, once per batch, and this is THE ONE REAL BUG IN THE
                    // PACER, guarded here. When the machine cannot keep up, `due <= elapsed`
                    // forever and the Task.Delay at the bottom of the loop never runs -- and
                    // that Delay is otherwise the only place in the whole loop the cancellation
                    // token is observed. A saturated worker would then be UNCANCELLABLE, which
                    // is fault.ignoreCancellation arrived at by accident: the workflow reports
                    // cancelled, demo-down.sh's drain window expires, and the worker is
                    // SIGKILLed with the terminal wedged.
                    ctx.CancellationToken.ThrowIfCancellationRequested();

                    // POLLED SEPARATELY from the line above, never folded into one `||`.
                    // PiActivities.cs:145-159 records what folding them costs: the first
                    // version of that activity reported "worker drain cut the burn short"
                    // seventeen times in a demo where nothing had drained, because every one of
                    // those was a workflow task timeout arriving on the OTHER token.
                    //
                    // AND IT IS AN EDGE. WorkerShutdownToken fires at shutdown START and stays
                    // signalled for the entire graceful window, so an ungated branch would add
                    // a bogus heartbeat and a bogus log line every batch period -- every 100ms
                    // for 30s at the shipped config. The gap between this token and
                    // ctx.CancellationToken is the only chance to checkpoint, and taking it is
                    // what lets the restarted worker resume near where this one stopped rather
                    // than at the last throttled heartbeat.
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
                            // Compact the tail forward and read into the space behind it. This
                            // is the whole reason the cursor can be exact: nothing but this
                            // loop decides where a line ends, so `consumed` is a byte count
                            // and not a character count, and it cannot drift.
                            var remaining = bufEnd - bufStart;

                            if (remaining == buffer.Length)
                            {
                                throw Terminal(log,
                                    string.Create(
                                        CultureInfo.InvariantCulture,
                                        $"no LF in a full {buffer.Length}-byte buffer at offset "
                                        + $"{consumed} of {input.Path}, after {rows} rows. Either "
                                        + $"the file is not a corpus, or fileScan.bufferBytes is "
                                        + $"below the longest row (76 bytes in the shipped "
                                        + $"corpora) and is failing a legal file."),
                                    "FileScanCorpusMalformed");
                            }

                            if (remaining > 0 && bufStart > 0)
                            {
                                Buffer.BlockCopy(buffer, bufStart, buffer, 0, remaining);
                            }

                            bufStart = 0;
                            bufEnd = remaining;

                            // The token is forwarded because CA2016 is an error here, and
                            // because a read stalled on a hung mount is otherwise unbounded.
                            var read = await fs.ReadAsync(buffer.AsMemory(remaining), ctx.CancellationToken)
                                .ConfigureAwait(false);

                            if (read == 0)
                            {
                                throw Terminal(log,
                                    string.Create(
                                        CultureInfo.InvariantCulture,
                                        $"{input.Path} ended at offset {consumed} after {rows} of "
                                        + $"{rowsToScan} rows. Its header declares {fileRows} "
                                        + $"rows, so the file is shorter than it says it is -- an "
                                        + $"interrupted generator run looks exactly like this."),
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
                                    + $"1, because the last byte before the LF would be a CR."),
                                "FileScanCorpusMalformed");
                        }

                        // THE PER-ROW WORK, and all of it. Two adds against a 167us per-row
                        // budget at the shipped rate. indexSum is the ROW cursor's witness and
                        // wordByteSum the BYTE cursor's, so a resume bug that moved one and not
                        // the other shows up in exactly one of the two.
                        indexSum += index;
                        wordByteSum += newline + 1 - RowOverhead;

                        if (decodeRows)
                        {
                            // Garbage the instant it exists, unless retained. That contrast --
                            // the same allocation, kept and not kept -- is the difference
                            // between the first and second rungs of the fault ladder, and it is
                            // only legible because the default path's heap floor is flat.
                            var decoded = Encoding.ASCII.GetString(row);
                            retained?.Add(decoded);
                        }

                        bufStart += newline + 1;
                        consumed += newline + 1;
                        rows++;
                        rowsInBatch++;
                    }

                    rowsThisAttempt += rowsInBatch;

                    // PHYSICALLY read, never rewound: a resumed attempt re-reads every row
                    // between the checkpoint and the crash, and those rows cost real time and
                    // real I/O. The cursor gauge below does the opposite, because those rows
                    // are arithmetically counted once. Two different questions; one series
                    // cannot answer both.
                    //
                    // DELTAS, because both are counters. Adding the absolute cursor once per
                    // batch would report the triangular sum of the corpus -- 2,875 batches into
                    // a 100 MB scan that is 143 GB of "bytes read", a number large enough to
                    // look like a units bug rather than a counter bug.
                    //
                    // The byte figure is the CURSOR'S ADVANCE, not the syscall total: the
                    // header read and the resume probe each pull a full buffer that is not
                    // counted here. That is 64 KiB against 100 MB per attempt, 0.06%, and
                    // counting them would break the one thing this counter is for -- pairing
                    // one-to-one with the rows above so allocation amplification is a ratio of
                    // two numbers about the same bytes.
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

                    // Cadence is bounded by WALL TIME, checked once per batch. A row-count
                    // cadence goes sparse exactly when the system slows down, which is when the
                    // line is worth having. ~29 lines for the shipped corpus.
                    var sinceLastLog = Stopwatch.GetElapsedTime(lastLogAt);
                    if (sinceLastLog >= logInterval)
                    {
                        // ONE Sample() feeds both sinks, and it MUST be published: the call
                        // advances process-wide watermarks, so a sample taken and discarded
                        // loses those bytes and collections from the counters permanently.
                        // Reading each value twice instead would make the console and Grafana
                        // disagree by a tick and send a reader chasing a discrepancy that does
                        // not exist.
                        var pressure = ProcessPressure.Sample();

                        // Unconditional, so the series exists and reads near zero rather than
                        // NODATA. A near-zero rate is THE RAW-BYTE PATH WORKING, not a broken
                        // counter, and that is the reading a newcomer gets backwards. The loop
                        // allocates NOTHING per row -- no string, no char[], one 65,536-byte
                        // buffer for the whole attempt -- so what is left is two fixed costs.
                        //
                        // MEASURED (Release, net10.0, arm64, full 100 MB corpus), and this
                        // is the measurement MetricNames.FileScanBytesAllocated cites. The
                        // sampler is NOT the main allocator, which was the first guess and was
                        // wrong by two orders of magnitude. The per-BATCH cost wins
                        // by a wide margin: one FileScanCheckpoint plus the params object[]
                        // that carries it into Heartbeat() is 117 B per batch -- the SLOPE
                        // between a 2,875-batch scan (407,520 B) and a 29-batch scan of the
                        // same corpus (74,648 B), which separates it from the ~71 KB fixed cost
                        // of the read buffer and the FileStream. So a shipped-config scan is
                        // ~336 KB of checkpoints, ~71 KB fixed, and 8.4 KB of samples
                        // (29 x 288 B): about 415 KB over 4m47s, or 1.4 KB/s against 348 KB/s
                        // of reading. 0.4%, which is why the CONCLUSION is unchanged even
                        // though the attribution was. Turn on fault.decodeRowsToStrings and the
                        // same counter reports 2.41x bytes read (measured), at which point
                        // every fixed cost here is noise.
                        allocatedCounter.Add(pressure.AllocatedBytesDelta);

                        // GUARDED on > 0, and that guard is load-bearing. Core creates a series
                        // on FIRST INCREMENT, so gen="2" stays ABSENT rather than reading zero
                        // through a shipped-config scan -- which is correct, because nothing in
                        // the default read path promotes anything far enough to trigger one.
                        // Adding a zero would manufacture a flat line that says "no gen2
                        // collections happened", which is the same picture as "this build never
                        // samples gen2".
                        for (var generation = 0; generation < gcCollectedCounters.Length; generation++)
                        {
                            var collected = pressure.GcCollectedDelta(generation);
                            if (collected > 0)
                            {
                                gcCollectedCounters[generation].Add(collected);
                            }
                        }

                        managedHeapGauge.Set(pressure.ManagedHeapBytes);

                        // A LAST-GC SNAPSHOT, not a live reading, on both of these. Measured:
                        // a 100 MB File.ReadAllBytes left the LOH gauge reporting the PREVIOUS
                        // collection's value until a forced blocking gen2 collect. And
                        // PauseTimePercentage is not a rolling window -- the runtime computes
                        // it at the end of a GC and leaves it alone, so on a scan that triggers
                        // no collection it reports the WORKER'S STARTUP GCs forever. Believe
                        // movement in it only when the collection counters are moving too.
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

                    // THE PACER: an ABSOLUTE deadline, per batch, never per row. Absolute so a
                    // GC pause or a page-cache miss is absorbed rather than accumulated -- a
                    // per-batch relative sleep would let every hiccup push the whole scan
                    // permanently late. Per batch rather than per row because Task.Delay has a
                    // floor near one platform tick and cannot express the 167us a row gets at
                    // the shipped rate, so a per-row sleep would run at about 1000 rows/s
                    // whatever targetRowsPerSecond said; batching is what makes the rate
                    // expressible at all. Same reasoning as PiActivities.CheckEvery.
                    //
                    // It DEGRADES TO FULL SPEED when the machine cannot keep up, which is the
                    // honest signal and the seam where the pressure half shows up: rows/s
                    // falling below target with gc_pause_percent rising is the second fault
                    // rung's whole story.
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
                // REUSED rather than given its own name, and the test for reuse is whether the
                // existing panel makes a claim a second contributor falsifies. A per-reason
                // breakdown of cancellations does not. By that same test repro_heartbeat_sent,
                // repro_activity_started and repro_activity_progress must NOT be reused: their
                // panels are unfiltered max()/sum() across activity types.
                //
                // The try only covers the READ LOOP, so a cancellation arriving during the
                // header read or the resume probe escapes without incrementing this. Accepted:
                // that window is microseconds, no rows have been read, and the workflow still
                // classifies the run as canceled from the exception itself. What would be lost
                // is one tick on a per-reason breakdown of a run that did nothing.
                meter.WithTags(new Dictionary<string, object>
                {
                    [MetricNames.Tags.Reason] = ctx.CancelReason.ToString(),
                }).CreateCounter<long>(MetricNames.ActivityCancel).Add(1);

                // There is no ignoreCancellation knob on this activity. fault.ignoreCancellation
                // belongs to HeartbeatActivities, where swallowing the exception wedges a
                // one-minute batch; here it would wedge a five-minute scan, and the accidental
                // version of the same defect is already covered by the unconditional token
                // check at the top of the loop.
                log.LogInformation(
                    "scan cancelled ({Reason}) at row {Rows} of {RowsToScan}, offset {Offset}; "
                    + "attempt {Attempt} had read {RowsThisAttempt} rows",
                    ctx.CancelReason, rows, rowsToScan, consumed, ctx.Info.Attempt, rowsThisAttempt);
                throw;
            }

            // -----------------------------------------------------------------------------
            // THE COMPLETION CHECK. The point of the whole case.
            // -----------------------------------------------------------------------------
            var expectedIndexSum = rowsToScan * (rowsToScan + 1) / 2;
            var indexSumMatches = indexSum == expectedIndexSum;

            // The BYTE half of the verdict, and it only has a closed form on a full scan:
            // wordByteSum == fileBytes - headerLen - (RowOverhead x rows) is the same statement
            // as "the cursor ended exactly at EOF", which is the strongest available check on
            // the byte cursor. Below a full scan there is no header fact to check it against,
            // because the words in an arbitrary prefix of the corpus have no closed-form total.
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
                    + "gauge should read as a staircase with no falling edge, and gen1/gen2 should "
                    + "have appeared",
                    retained.Count);
            }

            if (!verified)
            {
                // ERROR, and NON-RETRYABLE, and both are deliberate -- Terminal does both. A
                // wrong aggregate must never be returned as a success: outcome="completed" on a
                // mixed-up total is the plausible-constant failure this repo cares most about,
                // and it would land in the single place this whole case exists to rule out.
                // Retrying reproduces it exactly, so ten attempts would only make the log
                // longer while the verdict counter above already reads mismatch.
                throw Terminal(log, string.Create(
                    CultureInfo.InvariantCulture,
                    $"IDEMPOTENCY CHECK FAILED on {input.Path} after {rows} of {rowsToScan} rows: "
                    + $"indexSum {indexSum} against the closed form {rowsToScan} x ({rowsToScan} + 1) "
                    + $"/ 2 = {expectedIndexSum} (off by {indexSum - expectedIndexSum}); wordByteSum "
                    + $"{wordByteSum} against {expectedWordByteSum}; ended at offset {consumed} of "
                    + $"{fileBytes}. Rows were double-counted or skipped across a resume, which means "
                    + $"the accumulator's origin and the read cursor's origin were not the same "
                    + $"checkpoint. Non-retryable: a retry reproduces this exactly."),
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

            // NAMED arguments. Four adjacent longs, three of which are large and plausible at
            // the same order of magnitude for the 100 MB corpus (99,999,968 bytes against
            // 65,508,200 word bytes), so a positional construction compiles clean and reports
            // the word byte sum as the byte count.
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
    /// <remarks>
    /// One read, not a loop, on purpose. Every caller either wants a short prefix (the header,
    /// the boundary byte) or hands the result to the read loop, which refills on demand -- so a
    /// short read here is not an error, it is the normal case at the end of a file.
    /// </remarks>
    private static async Task<int> FillAsync(
        FileStream fs, byte[] buffer, long offset, int count, CancellationToken cancellationToken)
    {
        fs.Seek(offset, SeekOrigin.Begin);
        return await fs.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Plain ASCII digits to a long, or <see cref="MalformedRow"/>.</summary>
    /// <remarks>
    /// Hand-rolled rather than <c>long.TryParse</c> over a decoded string, because this runs
    /// once per row and the whole point of the raw-byte path is that a row never becomes a
    /// string. It is also STRICTER: no sign, no whitespace, no separators, no leading plus --
    /// TryParse accepts all of those and the corpus contract allows none of them.
    /// </remarks>
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

    /// <summary>Log at Error, then hand back a NON-RETRYABLE application failure to throw.</summary>
    /// <remarks>
    /// TERMINAL means structural disagreement: the checkpoint against itself, the checkpoint
    /// against the file, the file against its own header, or the aggregate against its closed
    /// form. None of those improve on a second attempt, and retrying spends the attempt budget
    /// that docs/HEARTBEATING.md's three kill -9 cycles need.
    /// <para>
    /// IT LOGS AS WELL AS THROWING, and that is not redundant with the SDK's own failure log.
    /// The SDK reports the failure with an exception and a stack trace around it; these
    /// messages are written to be READ, they carry the two disagreeing numbers, and the plan's
    /// console transcript shows them as single lines next to the progress lines they follow. A
    /// reader scrolling back for "why did the scan stop" should find one sentence, not a frame
    /// list.
    /// </para>
    /// <para>
    /// ERROR for every one of them, including the missing corpus. There is no such thing as an
    /// EXPECTED terminal failure of this activity: it retries nothing and fails the workflow.
    /// A fresh clone with no corpus is supposed to be handled upstream, by FileScanDriver's
    /// existence check skipping the loop with a named banner -- so reaching this method at all
    /// means that guard was bypassed.
    /// </para>
    /// </remarks>
    private static ApplicationFailureException Terminal(ILogger log, string message, string errorType)
    {
        // A CONSTANT template with one PascalCase hole. CA2254 makes a non-constant template an
        // error and CA1727 makes a camelCase hole one, so `log.LogError(message)` -- the
        // obvious thing to write here -- does not compile.
        log.LogError("{Message}", message);

        return new ApplicationFailureException(message, errorType, nonRetryable: true);
    }

    /// <summary>Go-style lowercase booleans, because that is what the dashboards match on.</summary>
    /// <remarks>A copy of HeartbeatActivities.Bool, which is private there. Same reason.</remarks>
    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>Bytes as MiB to one decimal, invariant. CA1305 is an error here.</summary>
    private static string Mib(double bytes) =>
        (bytes / (1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture);
}
