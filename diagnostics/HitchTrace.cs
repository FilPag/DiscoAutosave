using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace DiscoAutosave;

internal static class HitchTrace
{
    internal readonly record struct Span(string Name, double StartMs, double DurationMs, int Thread, int Frame, long AllocatedBytes);
    private readonly record struct Frame(int Number, double StartMs, double WallMs, double UnityMs,
        double? MainCpuMs, double ProcessCpuMs, long MainAllocatedBytes, long TotalAllocatedBytes,
        int Gen0, int Gen1, int Gen2);
    private sealed class Window
    {
        internal string Kind = "save";
        internal readonly long Start = Stopwatch.GetTimestamp();
        internal readonly DateTime Utc = DateTime.UtcNow;
        internal readonly ConcurrentQueue<Span> Spans = new();
        internal readonly Queue<Frame> Frames = new();
        internal readonly List<object> Collections = new();
        internal int SpanCount, DroppedSpans, DroppedFrames;
        internal long Completed;
        internal string Status = "in-progress";
    }

    private static readonly Process process = Process.GetCurrentProcess();
    private static RuntimeGcEvents? gc;
    private static Window? window;
    private static ManualLogSource log = null!;
    private static string directory = "", session = "";
    private static int mainThread, previousFrame;
    private static long previousTick, previousCpu, previousProcessCpu, previousMainBytes, previousTotalBytes;
    private static readonly int[] previousGc = new int[3];
    internal const double TailMs = 12000;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    internal static void Install(ManualLogSource logger)
    {
        log = logger;
        mainThread = Environment.CurrentManagedThreadId;
        session = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        directory = Path.Combine(Paths.BepInExRootPath, "DiscoAutosave-profiles");
        try { Directory.CreateDirectory(directory); gc = new RuntimeGcEvents(); }
        catch (Exception error) { log.LogWarning("Diagnostic trace capability: " + error.Message); }
        log.LogInfo("Hitch tracing: CLR suspend/resume events + GC pause-duration fallback, main/process CPU, actual file operations, allocations, and 12 seconds after completion. Unity GPU/automatic IL2CPP-GC markers are unavailable in these stripped interop bindings.");
    }

    internal static void Begin(string kind = "save")
    {
        window = new Window { Kind = kind };
        previousTick = Stopwatch.GetTimestamp();
        previousFrame = Time.frameCount;
        previousCpu = MainCpuTicks();
        previousProcessCpu = process.TotalProcessorTime.Ticks;
        previousMainBytes = GC.GetAllocatedBytesForCurrentThread();
        previousTotalBytes = GC.GetTotalAllocatedBytes(false);
        for (int i = 0; i < previousGc.Length; i++) previousGc[i] = GC.CollectionCount(i);
    }

    internal static void Complete(bool failed = false)
    {
        var current = window;
        if (current == null || current.Completed != 0) return;
        current.Status = failed ? "aborted" : current.Kind == "transition" ? "completed" : "written";
        current.Completed = Stopwatch.GetTimestamp();
        Add(current.Kind == "transition" ? "transition-complete" : "save-callback", current.Completed, current.Completed, 0);
    }

    internal static void Add(string name, long start, long end, long allocated = -1)
    {
        var current = window;
        if (current == null) return;
        if (Interlocked.Increment(ref current.SpanCount) > 8192)
        {
            Interlocked.Increment(ref current.DroppedSpans);
            return;
        }
        int thread = Environment.CurrentManagedThreadId;
        current.Spans.Enqueue(new(name, Ms(start - current.Start), Ms(end - start), thread,
            thread == mainThread ? Time.frameCount : -1, allocated));
    }

    internal static void Tick()
    {
        var current = window;
        if (current == null) return;
        long now = Stopwatch.GetTimestamp();
        long cpu = MainCpuTicks(), processCpu = process.TotalProcessorTime.Ticks;
        long mainBytes = GC.GetAllocatedBytesForCurrentThread(), totalBytes = GC.GetTotalAllocatedBytes(false);
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        if (gen0 != previousGc[0] || gen1 != previousGc[1] || gen2 != previousGc[2])
        {
            var info = GC.GetGCMemoryInfo();
            current.Collections.Add(new
            {
                ObservedMs = Ms(now - current.Start), ObservedFrame = previousFrame, info.Index,
                info.Generation, info.Concurrent, info.Compacted, info.HeapSizeBytes, info.FragmentedBytes,
                info.TotalCommittedBytes, info.PromotedBytes,
                PauseMs = info.PauseDurations.ToArray().Select(p => p.TotalMilliseconds).ToArray()
            });
        }
        current.Frames.Enqueue(new(previousFrame, Ms(previousTick - current.Start), Ms(now - previousTick),
            Time.unscaledDeltaTime * 1000, cpu < 0 || previousCpu < 0 ? null : (cpu - previousCpu) / 10000.0,
            (processCpu - previousProcessCpu) / 10000.0, mainBytes - previousMainBytes,
            totalBytes - previousTotalBytes, gen0 - previousGc[0], gen1 - previousGc[1], gen2 - previousGc[2]));
        if (current.Frames.Count > 4096) { current.Frames.Dequeue(); current.DroppedFrames++; }
        previousTick = now;
        previousFrame = Time.frameCount;
        previousCpu = cpu;
        previousProcessCpu = processCpu;
        previousMainBytes = mainBytes;
        previousTotalBytes = totalBytes;
        previousGc[0] = gen0; previousGc[1] = gen1; previousGc[2] = gen2;
    }

    internal static void Flush(int number, bool truncated = false)
    {
        var current = window;
        if (current == null) return;
        window = null;
        var end = DateTime.UtcNow;
        var frames = current.Frames.ToArray();
        var spans = current.Spans.ToArray();
        var collections = current.Collections.ToArray();
        var rawGc = gc?.Snapshot(current.Utc.AddSeconds(-1), end) ?? Array.Empty<RuntimeGcEvents.Sample>();
        var pauses = RuntimeGcEvents.Pauses(rawGc).Select(p => new
        {
            StartMs = (p.Start - current.Utc).TotalMilliseconds,
            DurationMs = (p.End - p.Start).TotalMilliseconds, p.Generation, p.Reason
        }).ToArray();
        var path = Path.Combine(directory, $"{session}-save-{number}.json");
        var report = new
        {
            Version = "1.6.7-debug.1", Save = number, current.Kind, current.Status, StartedUtc = current.Utc,
            CompletedMs = current.Completed == 0 ? (double?)null : Ms(current.Completed - current.Start),
            TailTruncated = truncated, MainThread = mainThread,
            RuntimeGcProviderEnabled = gc?.ProviderEnabled ?? false,
            RuntimeGcEventsSeen = gc == null ? 0 : Interlocked.Read(ref gc.EventsSeen),
            current.DroppedSpans, current.DroppedFrames,
            Frames = frames, Spans = spans, ManagedGcPauses = pauses, ManagedCollections = collections,
            RawGcEvents = rawGc,
            Limitations = "Nested spans are inclusive. CLR pauses do not measure automatic IL2CPP GC. CPU counters may be coarse. No direct GPU timing. Report writing occurs after the observation window."
        };
        // Formatting and disk output stay outside the measured window and off the main thread.
        _ = Task.Run(() =>
        {
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(report));
                log.LogInfo($"Save trace #{number}: {path}; CLR pauses={pauses.Length}, longest={(pauses.Length == 0 ? 0 : pauses.Max(p => p.DurationMs)):F2} ms; tail={(truncated ? "truncated" : "12 s")}.");
            }
            catch (Exception error) { log.LogWarning("Diagnostic trace write failed: " + error.Message); }
        });
    }

    private static long MainCpuTicks()
    {
        if (!OperatingSystem.IsWindows()) return -1;
        return GetThreadTimes(new IntPtr(-2), out _, out _, out long kernel, out long user) ? kernel + user : -1;
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
