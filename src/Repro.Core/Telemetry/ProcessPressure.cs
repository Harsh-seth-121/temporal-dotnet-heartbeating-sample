namespace Repro.Core.Telemetry;

/// <summary>One coherent read of every process pressure value the file-scan case publishes.</summary>
/// <param name="ManagedHeapBytes">
/// GC.GetTotalMemory(false). A LIVE reading: it reflects what is allocated now.
/// </param>
/// <param name="LohBytes">
/// GCMemoryInfo.GenerationInfo[3].SizeAfterBytes. A LAST-GC SNAPSHOT, not a live reading --
/// see <see cref="ProcessPressure.Sample"/>. Zero before the process's first GC.
/// </param>
/// <param name="WorkingSetBytes">Environment.WorkingSet. Live, and RESIDENT pages only.</param>
/// <param name="GcPausePercent">
/// GCMemoryInfo.PauseTimePercentage. Also a last-GC snapshot: it does not move between
/// collections, so it is only meaningful when the collection deltas below are non-zero.
/// </param>
/// <param name="AllocatedBytesDelta">
/// Bytes allocated process-wide that THIS sample won from the watermark, so it is safe to add
/// straight into a counter. 0 on the first sample of the process by construction.
/// </param>
/// <param name="Gen0CollectedDelta">
/// Collections of generation 0 OR HIGHER won from the watermark. Inclusive, not a partition --
/// <see cref="MetricNames.Gens"/> carries the measurement.
/// </param>
/// <param name="Gen1CollectedDelta">Collections of generation 1 or higher, same shape.</param>
/// <param name="Gen2CollectedDelta">Collections of generation 2, same shape.</param>
/// <remarks>
/// A STRUCT with everything in it, rather than the activity reading each value where it needs
/// it, and the reason is not tidiness. The console progress line and the Grafana gauges are
/// two renderings of one moment. Read the values twice and they disagree by a sample -- the
/// heap prints 5 MiB while the panel shows 41 MiB because a GC landed in between -- and a
/// reader spends the next twenty minutes chasing a discrepancy that does not exist.
/// <para>
/// THE FOUR GAUGE VALUES DO NOT ALL HAVE THE SAME "AS OF" TIME, which is the one thing this
/// struct cannot fix and the reader has to know. <paramref name="ManagedHeapBytes"/> and
/// <paramref name="WorkingSetBytes"/> are live; <paramref name="LohBytes"/> and
/// <paramref name="GcPausePercent"/> describe the last collection. They are consistent with
/// each other across the two sinks, which is what this type buys; they are not simultaneous.
/// </para>
/// </remarks>
public readonly record struct PressureSample(
    long ManagedHeapBytes = 0,
    long LohBytes = 0,
    long WorkingSetBytes = 0,
    double GcPausePercent = 0,
    long AllocatedBytesDelta = 0,
    long Gen0CollectedDelta = 0,
    long Gen1CollectedDelta = 0,
    long Gen2CollectedDelta = 0)
{
    /// <summary>The won delta for one generation, so the caller's <c>gen</c> tag loop stays honest.</summary>
    /// <remarks>
    /// Throws rather than returning 0 for an unknown generation. A silent 0 would render as a
    /// flat line on the collections panel, which is indistinguishable from "no collections
    /// happened" -- the exact reading this case relies on being true.
    /// </remarks>
    public long GcCollectedDelta(int generation) => generation switch
    {
        0 => Gen0CollectedDelta,
        1 => Gen1CollectedDelta,
        2 => Gen2CollectedDelta,
        _ => throw new ArgumentOutOfRangeException(
            nameof(generation), generation, "The runtime has exactly three collectible generations: 0, 1 and 2."),
    };
}

/// <summary>Reads process memory and GC pressure once per sample, and de-duplicates the cumulative ones.</summary>
/// <remarks>
/// IT PUBLISHES NOTHING. It returns a <see cref="PressureSample"/> and the activity owns the
/// instruments, for the reason HeartbeatActivities.cs:62 states: metrics have to come from
/// ActivityExecutionContext.MetricMeter, which carries the namespace/task_queue/activity_type
/// root tags. A static helper here could only reach TemporalRuntime.MetricMeter, whose series
/// arrive with no root tags at all, so every {namespace="$namespace"} selector on every panel
/// would silently drop them.
/// <para>
/// NOT ON A BACKGROUND TIMER, either. ActivityExecutionContext.Current is an AsyncLocal and
/// does not flow into a timer callback, so a sampling timer would throw the moment it tried to
/// publish -- on a thread pool thread, where nothing in this repo is watching.
/// </para>
/// <para>
/// This class is process-wide state shared by every concurrent scan in the worker. That is
/// deliberate: one heap, one thread pool, one RSS. Per-scan attribution of a process-wide
/// number is not available at any price, and pretending otherwise is what the watermark below
/// exists to prevent.
/// </para>
/// </remarks>
public static class ProcessPressure
{
    /// <summary>Index of the large object heap in <c>GCMemoryInfo.GenerationInfo</c>.</summary>
    /// <remarks>
    /// MEASURED rather than assumed, because a silent off-by-one would make every LOH claim on
    /// the board confidently wrong: a live 100 MiB byte[] plus a forced blocking gen2 collect
    /// moved index 3 from 0 to 104,857,680 bytes and left indices 0, 1, 2 and 4 in the
    /// kilobytes. Index 4 is the pinned object heap. <see cref="Sample"/> asserts the length
    /// rather than trusting this constant blindly.
    /// </remarks>
    private const int LargeObjectHeapGeneration = 3;

    /// <summary>gen0, gen1, gen2, LOH, POH.</summary>
    private const int ExpectedGenerationCount = 5;

    /// <summary>No sample has been taken yet, so there is no origin to difference against.</summary>
    private const long Unseeded = -1;

    private static long allocatedWatermark = Unseeded;
    private static long gen0Watermark = Unseeded;
    private static long gen1Watermark = Unseeded;
    private static long gen2Watermark = Unseeded;

    private static bool generationLayoutVerified;

    /// <summary>Read every pressure value ONCE. ADVANCES THE WATERMARKS: the result must be published.</summary>
    /// <remarks>
    /// THIS METHOD HAS A SIDE EFFECT and the returned deltas are consumed exactly once. Call it
    /// and discard the result -- inside an `if (log.IsEnabled(...))`, say, or in a branch that
    /// decides afterwards not to emit -- and those bytes and collections are gone from
    /// repro_file_scan_bytes_allocated permanently, because the watermark has already moved
    /// past them. There is no second chance to add them. Sample when you intend to publish.
    /// </remarks>
    public static PressureSample Sample()
    {
        // GetGCMemoryInfo FIRST, and its result held in a local, because GenerationInfo is a
        // ReadOnlySpan into the ~288-byte data object this call allocates. Two calls would be
        // two snapshots of possibly different collections, which is the disagreement this
        // whole type exists to rule out.
        var info = GC.GetGCMemoryInfo();

        if (!generationLayoutVerified)
        {
            // Benign race: under concurrent scans this may run more than once and each run
            // reaches the same verdict. A lock here would serialise every sampler in the
            // process to protect a check that costs one integer comparison.
            VerifyGenerationLayout(info.GenerationInfo.Length);
            generationLayoutVerified = true;
        }

        // false, NEVER true. GC.GetTotalMemory(true) forces a blocking collection, so the
        // sampler would then be measuring itself: every 10 seconds it would flatten the
        // sawtooth it is supposed to be drawing and add its own pause to
        // repro_file_scan_gc_pause_percent.
        var managedHeapBytes = GC.GetTotalMemory(false);

        // Environment.WorkingSet, not Process.GetCurrentProcess().WorkingSet64, which
        // allocates a Process object per call and needs disposing.
        var workingSetBytes = Environment.WorkingSet;

        // Read AFTER GetGCMemoryInfo on purpose, so the 288 bytes this sampler just allocated
        // are inside the window it publishes rather than trailing it by a whole logInterval.
        // At the shipped config the sampler is the largest single contributor to this counter;
        // see MetricNames.FileScanBytesAllocated.
        //
        // precise: false is the cheap read, and it is NOT byte-exact: measured, it LAGS the
        // precise value by up to one thread allocation-context budget -- 2,376 bytes here --
        // so the sampler's own 288 may surface in this delta or in the next one. Counted once
        // either way, which is the only property that matters. The same coarseness means a
        // sample can observe a SMALLER total than a concurrent sampler has already published,
        // so the watermark's refusal to move backwards covers granularity as well as the
        // interleaving below. That is a second, independent reason for the CAS rather than an
        // aside. precise: true walks every thread's context and is documented as expensive.
        var allocatedBytesDelta = Advance(ref allocatedWatermark, GC.GetTotalAllocatedBytes(precise: false));

        // INCLUSIVE counts: CollectionCount(0) counts collections of gen0 or higher, so one
        // gen2 collection increments all three. Published raw; MetricNames.Gens carries the
        // measurement and what it does to a panel.
        var gen0Delta = Advance(ref gen0Watermark, GC.CollectionCount(0));
        var gen1Delta = Advance(ref gen1Watermark, GC.CollectionCount(1));
        var gen2Delta = Advance(ref gen2Watermark, GC.CollectionCount(2));

        return new PressureSample(
            ManagedHeapBytes: managedHeapBytes,
            LohBytes: info.GenerationInfo[LargeObjectHeapGeneration].SizeAfterBytes,
            WorkingSetBytes: workingSetBytes,
            GcPausePercent: info.PauseTimePercentage,
            AllocatedBytesDelta: allocatedBytesDelta,
            Gen0CollectedDelta: gen0Delta,
            Gen1CollectedDelta: gen1Delta,
            Gen2CollectedDelta: gen2Delta);
    }

    /// <summary>
    /// Move a watermark forward over a process-wide CUMULATIVE value and return only the
    /// difference this caller won. THE ONLY CORRECTNESS-CRITICAL CODE IN THE PRESSURE HALF.
    /// </summary>
    /// <remarks>
    /// The problem: GC.GetTotalAllocatedBytes and GC.CollectionCount are process-wide totals,
    /// and at fileScan.concurrency above 1 every in-flight scan reads them. Adding the raw
    /// value to a counter counts the same bytes once per scan. Differencing against a
    /// PER-ACTIVITY local is no better: eight activities each differencing from their own
    /// start would still add the same interval eight times. The de-duplication has to be
    /// process-wide, which means one shared watermark and a caller that adds only what it won.
    /// <para>
    /// INTERLOCKED.EXCHANGE IS NOT SUFFICIENT, and this is the whole reason for the loop. Take
    /// two samplers A and B and a counter at T0:
    /// </para>
    /// <para>
    /// A reads the total as T1. B reads it as T2, with T2 greater than T1. B exchanges the
    /// watermark to T2 and adds T2 - T0. A then exchanges the watermark to T1 and adds
    /// T1 - T0 -- so T1 - T0 is added twice over, AND the watermark is now left at T1, which
    /// is BEHIND T2. The next sample differences against T1 and re-adds T2 - T1 a third time.
    /// A backwards Exchange does not lose an update, it manufactures them, and it keeps doing
    /// so on every subsequent sample until the total climbs past the stale value.
    /// </para>
    /// <para>
    /// The compare-exchange refuses to move backwards, so a loser adds nothing and the
    /// watermark only ever holds the highest value any sampler has observed. Total added
    /// across all callers is then exactly max(observed) - seed, once.
    /// </para>
    /// <para>
    /// SEEDED LAZILY at the first sample rather than at 0, which is the second half of the
    /// design. A watermark starting at 0 would attribute the worker's entire startup -- SDK
    /// initialisation, gRPC connection, config parsing, and every gen0 collection they caused
    /// -- to whichever scan happened to sample first. Excluded by construction instead: the
    /// seeding sample establishes the origin and contributes nothing.
    /// </para>
    /// </remarks>
    private static long Advance(ref long watermark, long current)
    {
        while (true)
        {
            // Volatile, because a long read is not guaranteed atomic on a 32-bit runtime and a
            // torn watermark would be a plausible number rather than a crash.
            var seen = Volatile.Read(ref watermark);

            if (seen == Unseeded)
            {
                if (Interlocked.CompareExchange(ref watermark, current, Unseeded) == Unseeded)
                {
                    return 0;
                }

                // Another sampler seeded it first. Re-read and difference against theirs.
                continue;
            }

            if (current <= seen)
            {
                // Either another sampler already published this ground, or the imprecise
                // allocation counter came back behind it. Nothing to add, and nothing to move.
                return 0;
            }

            if (Interlocked.CompareExchange(ref watermark, current, seen) == seen)
            {
                return current - seen;
            }

            // Lost the race. The winner published a value at least as high as `seen`, so the
            // retry either wins a smaller difference or finds `current` already covered and
            // returns 0. It cannot double-count and it cannot spin forever: every iteration
            // observes a strictly higher watermark.
        }
    }

    /// <summary>Assert the generation layout ONCE, rather than blind-indexing the LOH.</summary>
    /// <remarks>
    /// A LOUD FAILURE ON PURPOSE. The alternative is not "a missing gauge" -- it is
    /// repro_file_scan_loh_bytes reporting the pinned object heap, or gen2, as the LOH: a
    /// populated panel, a plausible number, and every LOH claim in docs/ quietly false. That
    /// is the plausible-constant failure HistogramBuckets's header calls the worst one in this
    /// repo. It fires at the first sample, one logInterval into a scan, not deep into one.
    /// </remarks>
    private static void VerifyGenerationLayout(int generationCount)
    {
        if (generationCount != ExpectedGenerationCount)
        {
            throw new InvalidOperationException(
                $"GCMemoryInfo.GenerationInfo has {generationCount} entries, expected {ExpectedGenerationCount} " +
                "(gen0, gen1, gen2, LOH, POH). This runtime's generation layout is not the one " +
                $"ProcessPressure was measured against, so index {LargeObjectHeapGeneration} is no longer the " +
                "large object heap. Re-verify which index the LOH occupies before publishing " +
                "repro_file_scan_loh_bytes, rather than shipping a plausible wrong number.");
        }
    }
}
