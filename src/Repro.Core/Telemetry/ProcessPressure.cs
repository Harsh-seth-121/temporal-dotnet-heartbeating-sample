namespace Repro.Core.Telemetry;

/// <summary>One coherent read of every process pressure value the file-scan case publishes.</summary>
/// <param name="ManagedHeapBytes">GC.GetTotalMemory(false). Live.</param>
/// <param name="LohBytes">GCMemoryInfo.GenerationInfo[3].SizeAfterBytes. A last-GC snapshot.</param>
/// <param name="WorkingSetBytes">Environment.WorkingSet. Live, and resident pages only.</param>
/// <param name="GcPausePercent">GCMemoryInfo.PauseTimePercentage. Also a last-GC snapshot.</param>
/// <param name="AllocatedBytesDelta">Process-wide bytes this sample won from the watermark, safe to add straight into a counter. 0 on the process's first sample.</param>
/// <param name="Gen0CollectedDelta">Collections of generation 0 or higher won from the watermark.</param>
/// <param name="Gen1CollectedDelta">Collections of generation 1 or higher, same shape.</param>
/// <param name="Gen2CollectedDelta">Collections of generation 2, same shape.</param>
/// <remarks>One struct because the console progress line and the Grafana gauges render one moment;
/// read the values twice and they disagree by a sample. See docs/GOTCHAS.md, "GCMemoryInfo
/// describes the LAST collection, not now" and "GC.CollectionCount(g) counts generation g OR
/// HIGHER".</remarks>
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
    /// <remarks>Throws rather than returning 0: a silent 0 draws a flat line indistinguishable from
    /// "no collections happened".</remarks>
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
/// <remarks>Publishes nothing: the activity owns the instruments, because metrics must come from
/// ActivityExecutionContext.MetricMeter to carry the namespace/task_queue/activity_type root tags
/// (HeartbeatActivities states why), and TemporalRuntime.MetricMeter's untagged series are dropped
/// by every {namespace="$namespace"} selector. Not on a background timer either:
/// ActivityExecutionContext.Current is an AsyncLocal and does not flow into a timer callback.</remarks>
public static class ProcessPressure
{
    /// <summary>Index of the large object heap in <c>GCMemoryInfo.GenerationInfo</c>.</summary>
    /// <remarks>Measured: a live 100 MiB byte[] plus a forced blocking gen2 collect moved index 3
    /// from 0 to 104,857,680 bytes and left 0, 1, 2 and 4 in the kilobytes; index 4 is the pinned
    /// object heap.</remarks>
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

    /// <summary>Read every pressure value once. Advances the watermarks, so the result must be published.</summary>
    /// <remarks>The deltas are consumed exactly once: discard the result and those bytes and
    /// collections are gone from repro_file_scan_bytes_allocated permanently.</remarks>
    public static PressureSample Sample()
    {
        // First, and held in a local: GenerationInfo is a ReadOnlySpan into the ~288-byte object
        // this call allocates, so two calls would be two snapshots.
        var info = GC.GetGCMemoryInfo();

        if (!generationLayoutVerified)
        {
            // Benign race: concurrent scans may run this more than once, to the same verdict.
            VerifyGenerationLayout(info.GenerationInfo.Length);
            generationLayoutVerified = true;
        }

        // false, never true: GC.GetTotalMemory(true) forces a blocking collection, so the sampler
        // would flatten the sawtooth it draws and add its own pause to the pause-percent gauge.
        var managedHeapBytes = GC.GetTotalMemory(false);

        // Not Process.GetCurrentProcess().WorkingSet64, which allocates and needs disposing.
        var workingSetBytes = Environment.WorkingSet;

        // Read after GetGCMemoryInfo so the 288 bytes this sampler just allocated land inside the
        // window it publishes. precise: false is not byte-exact: measured, it lags by up to one
        // thread allocation-context budget, 2,376 bytes here, so a sample can observe a smaller
        // total than a concurrent sampler published. See docs/GOTCHAS.md, "A near-zero allocation
        // counter is the read path working, not a dead metric".
        var allocatedBytesDelta = Advance(ref allocatedWatermark, GC.GetTotalAllocatedBytes(precise: false));

        // Inclusive counts, published raw. MetricNames.Gens carries the measurement.
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

    /// <summary>Move a watermark forward over a process-wide cumulative value, returning only the
    /// difference this caller won.</summary>
    /// <remarks>
    /// GC.GetTotalAllocatedBytes and GC.CollectionCount are process-wide totals that every in-flight
    /// scan reads, so the de-duplication must be process-wide too. Interlocked.Exchange is not
    /// enough, which is why this loops: sampler A reads T1, B reads a later T2 and adds T2 - T0,
    /// then A exchanges to T1 and adds T1 - T0 again, stranding the watermark behind T2 so the next
    /// sample re-adds T2 - T1. The compare-exchange refuses to move backwards, so the total across
    /// all callers is exactly max(observed) - seed, once. Seeded lazily at the first sample, not at
    /// 0, which would attribute the worker's startup to whichever scan sampled first.
    /// </remarks>
    private static long Advance(ref long watermark, long current)
    {
        while (true)
        {
            // Volatile: a long read is not atomic on a 32-bit runtime, and a torn watermark
            // would be a plausible number rather than a crash.
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
                // Already published, or the imprecise allocation counter came back behind it.
                return 0;
            }

            if (Interlocked.CompareExchange(ref watermark, current, seen) == seen)
            {
                return current - seen;
            }

            // Lost the race. The winner published at least `seen`, so the retry wins a smaller
            // difference or returns 0, and every iteration observes a strictly higher watermark.
        }
    }

    /// <summary>Assert the generation layout once, rather than blind-indexing the LOH.</summary>
    /// <remarks>Loud on purpose: a wrong index makes repro_file_scan_loh_bytes report the pinned
    /// object heap or gen2 as the LOH.</remarks>
    private static void VerifyGenerationLayout(int generationCount)
    {
        if (generationCount != ExpectedGenerationCount)
        {
            throw new InvalidOperationException(
                $"GCMemoryInfo.GenerationInfo has {generationCount} entries, expected {ExpectedGenerationCount} " +
                "(gen0, gen1, gen2, LOH, POH). This runtime's layout is not the one ProcessPressure " +
                $"was measured against, so index {LargeObjectHeapGeneration} is no longer the large " +
                "object heap. Re-verify which index the LOH occupies before publishing " +
                "repro_file_scan_loh_bytes.");
        }
    }
}
