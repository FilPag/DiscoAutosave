using System.Diagnostics;
using System.Reflection;
using System.Collections.Concurrent;
using BepInEx.Logging;
using HarmonyLib;
using PixelCrushers.DialogueSystem;
using UnityEngine;

namespace DiscoAutosave;

internal static class SaveDiagnostics
{
    private sealed class Metric
    {
        internal bool TransitionOnly;
        internal int Count;
        internal double Total, Max;
        internal void Add(double ms) { lock (this) { Count++; Total += ms; Max = Math.Max(Max, ms); } }
        internal void Reset() { lock (this) { Count = 0; Total = Max = 0; } }
        public override string ToString() { lock (this) return $"calls={Count}, total={Total:F2} ms, max={Max:F2} ms"; }
    }

    private static readonly ConcurrentDictionary<MethodBase, Metric> methods = new();
    private static readonly ConcurrentDictionary<MethodBase, string> names = new();
    private static readonly ConcurrentQueue<string> causes = new();
    private static readonly HashSet<MethodBase> contexts = new();
    private static int causeCount;
    [ThreadStatic] private static Stack<MethodBase>? scopes;
    private readonly record struct CallState(long Started, bool Scoped, string Caller, int TransitionState);
    private static readonly List<(string Name, double Ms, long Bytes, int Frame)> stages = new(32);
    private static readonly List<string> dialogueCopies = new(5);
    private static readonly Metric native = new();
    private static ManualLogSource log = null!;
    private static int mainThread, preparationDepth, serial, beganFrame, lastWorkFrame, worstFrameNumber;
    private static long began, stageAt, stageBytes, nativeAt, completedAt;
    private static int nativeState;
    [ThreadStatic] private static long workerQueuedAt, workerAt, workerBytes;
    private static double worstFrame;
    private static string lastWork = "waiting", worstWork = "waiting";
    private static volatile bool active;
    internal static bool Active => active;
    private static bool complete;
    private static string profileKind = "save";
    private static readonly int[] collections = new int[3];

    internal static void Install(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        mainThread = Environment.CurrentManagedThreadId;
        HitchTrace.Install(logger);
        SaveInternals.Install(harmony, logger);
        Hook(harmony, typeof(PersistentDataManager), "PrepConversationSimStatusForRawData");
        Hook(harmony, typeof(Lua), "Run", new[] { typeof(string) });
        foreach (string name in new[] { "CreateSaveGame", "DeleteExcessAutosavesOps", "DeleteExcessAutosavesSlots", "PopulateListOfSaves" })
            Hook(harmony, typeof(SunshinePersistenceFileManager), name);
        Hook(harmony, typeof(SunshinePersistenceThumbnail), "CaptureAndResizeScreenshot");
        Hook(harmony, typeof(SunshinePersistenceThumbnail), "Release");
        Hook(harmony, typeof(FileOpCommandBuffer), "Execute");
        foreach (var type in new[] { typeof(CreateFileOp), typeof(MoveFileOp), typeof(DeleteFileOp), typeof(ReadFileOp) })
            Hook(harmony, type, "Execute");
        Hook(harmony, typeof(FileEx), "WriteAllBytes");
        Hook(harmony, typeof(FileEx), "ReadAllBytes");
        Hook(harmony, typeof(Resources), "UnloadUnusedAssets");
        foreach (string name in new[] { "SetActive", "TransitionProcessScene" })
            Hook(harmony, typeof(FortressOccident.SceneTransitionManager), name);
        Hook(harmony, typeof(AmplifyTextureManager), "Warmup");
        Hook(harmony, typeof(AmplifyTextureManager), "SetActiveCollection");
        Hook(harmony, typeof(Voidforge.PrioritizedActionList), "Invoke");
        Hook(harmony, typeof(NavMeshDataCollection), "GetNavMeshDataByScene");
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(FortressOccident.TravelDestination))
            .Where(m => m.Name == "ArriveAt")) Hook(harmony, method);
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(Il2CppSystem.GC))
            .Where(m => m.Name is "Collect" or "WaitForPendingFinalizers")) Hook(harmony, method);
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(Il2CppSystem.IO.FileStream))
            .Where(m => m.Name is "Write" or "Flush" or "Dispose")) Hook(harmony, method);
        foreach (var owner in new[] { typeof(SaveFolderAccessor), typeof(SunshinePersistenceThumbnail) })
            foreach (var type in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (type.Name.Contains("SaveGame") || type.Name.Contains("SlotExecute") || type.Name.Contains("GameSaves") ||
                    type.Name.Contains("SaveSlotCache") || type.Name.Contains("RunThumbnailer"))
                {
                    var method = AccessTools.GetDeclaredMethods(type).FirstOrDefault(method => method.Name == "MoveNext");
                    if (method != null) Hook(harmony, method);
                }
        foreach (string name in new[] { "UnloadBundleForLanguage", "DyslexicImageBundles" })
            HookContext(harmony, AccessTools.Method(typeof(I2.Loc.LocalizationAssetBundlesManager), name));
        foreach (var owner in new[] { typeof(I2.Loc.LocalizationAssetBundlesManager), typeof(FastLoadManager),
            typeof(FortressOccident.SceneTransitionManager), typeof(SunshinePersistence) })
            foreach (var type in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (type.Name.Contains("DelayedUnloadBundles") || type.Name.Contains("LoadAllScenes") ||
                    type.Name.Contains("LoadSceneCoR") || type.Name.Contains("LoadCoR") || type.Name.Contains("SaveCoR"))
                    HookContext(harmony, AccessTools.Method(type, "MoveNext"));
        log.LogInfo("GC caller contexts installed: " + string.Join("; ", contexts.Select(m => names[m])));
        log.LogWarning("Additional transition hooks disabled for crash isolation; existing save/GC diagnostics remain enabled.");
        log.LogInfo("Save diagnostics enabled; optimized Release code, unchanged save algorithm. Profiles include 12 s after the callback; wait 15 s after the last save before requesting logs.");
    }

    private static void Hook(Harmony harmony, Type type, string name, Type[]? args = null)
    {
        var method = AccessTools.Method(type, name, args);
        if (method == null) { log.LogWarning("Diagnostic hook unavailable: " + type.FullName + "." + name); return; }
        Hook(harmony, method);
    }

    internal static void Hook(Harmony harmony, MethodBase method, bool transitionOnly = false)
    {
        if (methods.ContainsKey(method)) return;
        try
        {
            methods.TryAdd(method, new Metric { TransitionOnly = transitionOnly });
            names[method] = method.DeclaringType?.FullName + "." + method.Name;
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(SaveDiagnostics), nameof(Enter)),
                finalizer: new HarmonyMethod(typeof(SaveDiagnostics), nameof(Exit)));
        }
        catch (Exception error)
        {
            methods.TryRemove(method, out _);
            names.TryRemove(method, out _);
            log.LogWarning("Diagnostic hook unavailable: " + method.DeclaringType?.FullName + "." + method.Name + ": " + error.Message);
        }
    }

    private static void HookContext(Harmony harmony, MethodBase? method)
    {
        if (method == null) { log.LogWarning("Diagnostic caller-context method missing."); return; }
        Hook(harmony, method);
        if (methods.ContainsKey(method)) contexts.Add(method);
    }

    private static void Enter(MethodBase __originalMethod, object? __instance, out CallState __state)
    {
        __state = default;
        if (!active) return;
        if (methods.TryGetValue(__originalMethod, out var metric) && metric.TransitionOnly && !TransitionTimings.InTransition) return;
        if (__originalMethod.DeclaringType == typeof(Lua) &&
            (Environment.CurrentManagedThreadId != mainThread || preparationDepth == 0)) return;
        if (__originalMethod.Name == "PrepConversationSimStatusForRawData") preparationDepth++;
        bool scoped = __originalMethod.DeclaringType != typeof(Lua) && __originalMethod.DeclaringType != typeof(PersistentDataManager);
        string caller = "untracked";
        if ((__originalMethod.DeclaringType == typeof(Il2CppSystem.GC) || contexts.Contains(__originalMethod)) && scopes?.Count > 0)
            caller = string.Join(" > ", scopes.Reverse().Select(m => names[m]));
        if (scoped) (scopes ??= new Stack<MethodBase>(16)).Push(__originalMethod);
        int transitionState = __instance switch
        {
            FortressOccident.SceneTransitionManager._LoadSceneCoR_d__36 transition => transition.__1__state,
            FortressOccident.SceneTransitionManager._ShowLoadingScreenCoR_d__37 loading => loading.__1__state,
            FortressOccident.SceneTransitionManager._UnloadScenesCoR_d__38 unload => unload.__1__state,
            _ => -1
        };
        __state = new(Stopwatch.GetTimestamp(), scoped, caller, transitionState);
    }

    private static Exception? Exit(MethodBase __originalMethod, CallState __state, Exception? __exception)
    {
        if (__state.Started == 0) return __exception;
        if (__state.Scoped) scopes!.Pop();
        double ms = Elapsed(__state.Started);
        if (methods.TryGetValue(__originalMethod, out var metric)) metric.Add(ms);
        if (__originalMethod.Name == "PrepConversationSimStatusForRawData") preparationDepth--;
        if (__originalMethod.DeclaringType != typeof(Lua) &&
            (__originalMethod.DeclaringType != typeof(PersistentDataManager) || ms >= 2))
            HitchTrace.Add(names[__originalMethod] + (__state.TransitionState >= 0 ? " [state=" + __state.TransitionState + "]" : "") +
                (__originalMethod.DeclaringType == typeof(Il2CppSystem.GC) ? " <- " + __state.Caller : ""),
                __state.Started, Stopwatch.GetTimestamp());
        if ((__originalMethod.DeclaringType == typeof(Il2CppSystem.GC) && ms >= 5) ||
            (contexts.Contains(__originalMethod) && ms >= 20))
            if (Interlocked.Increment(ref causeCount) <= 32)
                causes.Enqueue($"{names[__originalMethod]}={ms:F2} ms; caller={__state.Caller}; " +
                    (complete ? $"after-callback={Elapsed(completedAt):F1} ms" : "before callback"));
        if (Environment.CurrentManagedThreadId == mainThread) Work(__originalMethod.Name);
        return __exception;
    }

    internal static void Begin(bool transition = false)
    {
        if (active) Report(true);
        profileKind = transition ? "transition" : "save";
        serial++;
        while (causes.TryDequeue(out _)) { }
        causeCount = 0;
        HitchTrace.Begin(transition ? "transition" : "save");
        stages.Clear();
        dialogueCopies.Clear();
        SaveInternals.Reset();
        TransitionTimings.Reset();
        native.Reset();
        foreach (var metric in methods.Values) metric.Reset();
        preparationDepth = 0;
        worstFrame = 0;
        lastWorkFrame = -1;
        worstWork = "waiting";
        complete = false;
        active = true;
        beganFrame = Time.frameCount;
        began = Stopwatch.GetTimestamp();
        for (int i = 0; i < collections.Length; i++) collections[i] = GC.CollectionCount(i);
        RestartStage();
    }

    internal static void RestartStage()
    {
        stageBytes = GC.GetAllocatedBytesForCurrentThread();
        stageAt = Stopwatch.GetTimestamp();
    }

    internal static void StartCapture()
    {
        RestartStage();
        Work("capture");
    }

    internal static void Mark(string name)
    {
        double ms = Elapsed(stageAt);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - stageBytes;
        stages.Add((name, ms, bytes, Time.frameCount));
        HitchTrace.Add(name, stageAt, Stopwatch.GetTimestamp(), bytes);
        Work(name);
        RestartStage();
    }

    private static void Work(string name) { lastWorkFrame = Time.frameCount; lastWork = name; }
    internal static void NativeEnter(int state) { nativeState = state; nativeAt = Stopwatch.GetTimestamp(); }
    internal static void NativeExit()
    {
        native.Add(Elapsed(nativeAt));
        HitchTrace.Add("SaveCoR-state-" + nativeState, nativeAt, Stopwatch.GetTimestamp());
        Work("native-save-step");
    }

    internal static void Complete(bool failed = false)
    {
        if (!active || complete) return;
        SaveInternals.EndCapture();
        complete = true;
        completedAt = Stopwatch.GetTimestamp();
        HitchTrace.Complete(failed);
    }

    internal static long Measure() => Stopwatch.GetTimestamp();
    internal static void DialogueCache(int hits, int misses, int checks) => dialogueCopies.Add(
        $"cache: reused={hits}, rebuilt={misses}, mutation-checks={checks}; cached blocks are copied into the detached worker buffer.");
    internal static void DialogueCopied(string root, long started, long values, long tables, long strings,
        long characters, long listSlots, long dictionarySlots, int bytes, int capacity)
    {
        double ms = Elapsed(started);
        dialogueCopies.Add($"{root}: {ms:F2} ms, values={values}, tables={tables}, strings={strings}, " +
            $"UTF16-chars={characters}, list-slots={listSlots}, dictionary-slots={dictionarySlots}, flat-bytes={bytes}, capacity={capacity}");
    }
    internal static void SaveStarted(string mode, bool optimized) =>
        log.LogInfo($"Save origin: mode={mode}, pipeline={(optimized ? "optimized" : "native")}.");
    internal static void Measured(string name, long start)
    {
        long end = Stopwatch.GetTimestamp();
        if (active && Elapsed(start) >= 2) HitchTrace.Add(name, start, end);
    }

    internal static void WorkerBegin(long queued)
    {
        workerQueuedAt = queued;
        workerAt = Stopwatch.GetTimestamp();
        workerBytes = GC.GetAllocatedBytesForCurrentThread();
        HitchTrace.Add("worker-queue-delay", workerQueuedAt, workerAt);
    }

    internal static void WorkerMark(string name)
    {
        HitchTrace.Add(name, workerAt, Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread() - workerBytes);
        workerAt = Stopwatch.GetTimestamp();
        workerBytes = GC.GetAllocatedBytesForCurrentThread();
    }

    internal static void Tick()
    {
        if (!active) return;
        try { TickCore(); }
        catch (Exception error)
        {
            active = false;
            log.LogWarning("Save diagnostics stopped (saving unaffected): " + error.Message);
        }
    }

    private static void TickCore()
    {
        HitchTrace.Tick();
        int previousFrame = Time.frameCount - 1;
        double frameMs = Time.unscaledDeltaTime * 1000;
        if (previousFrame >= beganFrame && frameMs > worstFrame)
        {
            worstFrame = frameMs;
            worstFrameNumber = previousFrame;
            worstWork = lastWorkFrame == previousFrame ? lastWork : "waiting/other-game-work";
        }
        if (!complete || TransitionTimings.InTransition || Elapsed(completedAt) < HitchTrace.TailMs) return;
        Report(false);
    }

    private static void Report(bool truncated)
    {
        active = false;
        log.LogInfo($"Save profile #{serial}: kind={profileKind}, elapsed={Elapsed(began):F1} ms, frames={Time.frameCount - beganFrame}, worst-frame={worstFrame:F1} ms at {worstFrameNumber} (last work: {worstWork}), managed-GC={GC.CollectionCount(0) - collections[0]}/{GC.CollectionCount(1) - collections[1]}/{GC.CollectionCount(2) - collections[2]}.");
        log.LogInfo("Save stages: " + string.Join("; ", stages.Select(s => $"{s.Name}={s.Ms:F2} ms/{s.Bytes} B@{s.Frame}")));
        foreach (string copy in dialogueCopies) log.LogInfo("Save dialogue: " + copy);
        SaveInternals.Report(log);
        log.LogInfo("Save native: iterator " + native + "; " + string.Join("; ", methods
            .Where(p => p.Key.DeclaringType != typeof(Lua) && p.Key.DeclaringType != typeof(PersistentDataManager))
            .Select(p => p.Key.DeclaringType?.Name + "." + p.Key.Name + " " + p.Value)));
        log.LogInfo("Save prep: " + string.Join("; ", methods
            .Where(p => p.Key.DeclaringType == typeof(Lua) || p.Key.DeclaringType == typeof(PersistentDataManager))
            .Select(p => p.Key.Name + " " + p.Value)));
        while (causes.TryDequeue(out string? cause)) log.LogInfo("Save cause: " + cause);
        HitchTrace.Flush(serial, truncated);
    }

    private static double Elapsed(long timestamp) => (Stopwatch.GetTimestamp() - timestamp) * 1000.0 / Stopwatch.Frequency;
}
