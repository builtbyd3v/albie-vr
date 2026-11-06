using System;
using System.Threading;
using Unity.Profiling;

/// <summary>
/// Counts the bytes code allocates, for the Editor checks' "no allocations per step" claims.
/// GC.GetAllocatedBytesForCurrentThread reads 0 under Unity's garbage collector, so a check built on it passes whatever
/// the code does. This reads the profiler's "GC Allocated In Frame" counter instead, which counts each allocation as it
/// happens. The counter starts again each Editor frame, so measure work that runs inside one call (a check does).
/// <see cref="Start"/> first proves the counter counts a known allocation, and throws if it doesn't, so a check can't
/// pass on a counter that reads 0.
///
/// The counter counts every thread, not just this one. So <see cref="Start"/> also waits until nothing in the process
/// allocates for two short windows in a row, and throws if that doesn't happen within a second. A check that starts
/// background work (the notebook writes its files on a worker thread) should let it finish before measuring
/// (<c>NotebookFiles.Flush()</c>).
/// </summary>
public sealed class AllocationCounter : IDisposable
{
    ProfilerRecorder recorder;

    AllocationCounter(ProfilerRecorder recorder) { this.recorder = recorder; }

    /// <summary>The bytes counted so far this frame: read it before and after the work, and subtract.</summary>
    public long Now => recorder.CurrentValue;

    public static AllocationCounter Start()
    {
        var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
        if (!recorder.Valid)
        {
            recorder.Dispose();
            throw new InvalidOperationException("the profiler's GC Allocated In Frame counter isn't available");
        }
        long before = recorder.CurrentValue;
        var probe = new byte[1024];
        probe[0] = 1;
        if (recorder.CurrentValue - before < probe.Length)
        {
            recorder.Dispose();
            throw new InvalidOperationException("the allocation counter didn't count a 1 KB probe");
        }
        if (!WaitForQuiet(recorder))
        {
            recorder.Dispose();
            throw new InvalidOperationException("another thread kept allocating for a second, so allocations here can't be measured: let background work finish first");
        }
        return new AllocationCounter(recorder);
    }

    // Two 5 ms windows in a row with nothing allocated anywhere in the process. Sleeping allocates nothing.
    static bool WaitForQuiet(ProfilerRecorder recorder)
    {
        int quiet = 0;
        for (int window = 0; window < 200 && quiet < 2; window++)
        {
            long before = recorder.CurrentValue;
            Thread.Sleep(5);
            quiet = recorder.CurrentValue == before ? quiet + 1 : 0;
        }
        return quiet >= 2;
    }

    /// <summary>Bytes allocated while <paramref name="work"/> runs.</summary>
    public static long BytesDuring(Action work)
    {
        using (var counter = Start())
        {
            long before = counter.Now;
            work();
            return counter.Now - before;
        }
    }

    public void Dispose() => recorder.Dispose();
}
