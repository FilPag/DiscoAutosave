using System.Diagnostics;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using PixelCrushers.DialogueSystem;
using FullSerializer;
using NativeBuilder = Il2CppSystem.Text.StringBuilder;

namespace DiscoAutosave;

internal static class SaveInternals
{
    private enum Kind { Record, RecordWork, Json, Persister, TreeAndPrint, Print }
    private sealed class Metric
    {
        internal readonly Kind Kind;
        internal readonly string Name;
        internal int Count;
        internal double Total, Max;
        internal long Bytes;
        internal Metric(Kind kind, string name) { Kind = kind; Name = name; }
        internal void Reset() { Count = 0; Total = Max = 0; Bytes = 0; }
        public override string ToString() => $"{Name} calls={Count}, total={Total:F2} ms, max={Max:F2} ms, allocated={Bytes} B";
    }
    private readonly record struct State(long Started, long Bytes, int JsonPart);
    private static readonly Dictionary<MethodBase, Metric> metrics = new();
    private static readonly Metric[] jsonParts =
    {
        new(Kind.TreeAndPrint, "preload-tree+print"), new(Kind.Print, "preload-print"),
        new(Kind.TreeAndPrint, "world-tree+print"), new(Kind.Print, "world-print")
    };
    private static int mainThread, recordDepth, jsonDepth, jsonPart;
    private static bool capturing;
    private static string recordMode = "not captured";

    internal static void Install(Harmony harmony, ManualLogSource log)
    {
        mainThread = Environment.CurrentManagedThreadId;
        Hook(harmony, log, typeof(PersistentDataManager), "Record", Kind.Record, Type.EmptyTypes);
        Hook(harmony, log, typeof(PersistentDataManager), "SendPersistentDataMessage", Kind.RecordWork, new[] { typeof(string) });
        Hook(harmony, log, typeof(Tools), "SendMessageToEveryone", Kind.RecordWork, new[] { typeof(string) });
        foreach (var type in new[] { typeof(LevelManager), typeof(PersistentPositionData), typeof(DialogueSystemTrigger), typeof(BarkStarter) })
            Hook(harmony, log, type, "OnRecordPersistentData", Kind.RecordWork, Type.EmptyTypes);
        Hook(harmony, log, typeof(SunshinePersistenceJsonSerializer), "Serialize", Kind.Json,
            new[] { typeof(NativeBuilder).MakeByRefType(), typeof(NativeBuilder).MakeByRefType() });
        foreach (var type in new[] { typeof(PartyPersister), typeof(VariousItemsPersister), typeof(SunshineClockTimePersister),
            typeof(CharacterSheetPersister), typeof(PlayerCharacterPersister), typeof(HudStatePersister), typeof(JournalPersister),
            typeof(FailedWhiteChecksPersister), typeof(WeatherPersister), typeof(InventoryPersister), typeof(ThoughtCabinetPersister),
            typeof(ContainerSourcePersister), typeof(KubujussPersister), typeof(GameModePersister) })
            Hook(harmony, log, type, "Serialize", Kind.Persister, Type.EmptyTypes);
        Hook(harmony, log, typeof(Sunshine.JsonUtil), "Serialize", Kind.TreeAndPrint,
            new[] { typeof(Il2CppSystem.Object), typeof(NativeBuilder).MakeByRefType() });
        Hook(harmony, log, typeof(fsJsonPrinter), "PrettyJson", Kind.Print,
            new[] { typeof(fsData), typeof(NativeBuilder).MakeByRefType() });
        log.LogInfo($"Record/JSON internals: {metrics.Count} hooks; scoped to this mod's live capture only. Tree time is tree+print minus print; nested totals must not be added.");
    }

    private static void Hook(Harmony harmony, ManualLogSource log, Type type, string name, Kind kind, Type[] args)
    {
        var method = AccessTools.Method(type, name, args);
        if (method == null) { log.LogWarning("Internal timing unavailable: " + type.FullName + "." + name); return; }
        try
        {
            metrics.Add(method, new(kind, type.Name + "." + name));
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(SaveInternals), nameof(Enter)),
                finalizer: new HarmonyMethod(typeof(SaveInternals), nameof(Exit)));
        }
        catch (Exception error)
        {
            metrics.Remove(method);
            log.LogWarning("Internal timing unavailable: " + type.FullName + "." + name + ": " + error.Message);
        }
    }

    internal static void Reset()
    {
        capturing = false;
        foreach (var metric in metrics.Values) metric.Reset();
        foreach (var metric in jsonParts) metric.Reset();
        recordMode = "not captured";
    }
    internal static void BeginCapture() { recordDepth = jsonDepth = jsonPart = 0; capturing = true; }
    internal static void EndCapture() => capturing = false;
    internal static void BeginTargetedRecord(int targets)
    {
        recordDepth++;
        recordMode = $"TargetedAllGameObjects, targets={targets}";
    }
    internal static void EndTargetedRecord() => recordDepth--;

    private static void Enter(MethodBase __originalMethod, out State __state)
    {
        __state = default;
        if (!capturing || Environment.CurrentManagedThreadId != mainThread) return;
        var metric = metrics[__originalMethod];
        if (metric.Kind == Kind.Record)
        {
            recordDepth++;
            recordMode = PersistentDataManager.recordPersistentDataOn.ToString();
        }
        else if (metric.Kind == Kind.Json) { jsonDepth++; jsonPart = 0; }
        else if (metric.Kind == Kind.RecordWork ? recordDepth == 0 : jsonDepth == 0) return;
        if (metric.Kind == Kind.TreeAndPrint) jsonPart++;
        __state = new(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread(), jsonPart);
    }

    private static Exception? Exit(MethodBase __originalMethod, State __state, Exception? __exception)
    {
        if (__state.Started == 0) return __exception;
        double ms = (Stopwatch.GetTimestamp() - __state.Started) * 1000.0 / Stopwatch.Frequency;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - __state.Bytes;
        var metric = metrics[__originalMethod];
        Add(metric, ms, bytes);
        if (metric.Kind is Kind.TreeAndPrint or Kind.Print && __state.JsonPart is 1 or 2)
            Add(jsonParts[(__state.JsonPart - 1) * 2 + (metric.Kind == Kind.Print ? 1 : 0)], ms, bytes);
        if (metric.Kind == Kind.Record) recordDepth--;
        if (metric.Kind == Kind.Json) jsonDepth--;
        return __exception;
    }

    private static void Add(Metric metric, double ms, long bytes)
    {
        metric.Count++; metric.Total += ms; metric.Max = Math.Max(metric.Max, ms); metric.Bytes += bytes;
    }

    internal static void Report(ManualLogSource log)
    {
        log.LogInfo("Save Record internals: mode=" + recordMode + "; " + string.Join("; ", metrics.Values
            .Where(m => m.Kind is Kind.Record or Kind.RecordWork).OrderByDescending(m => m.Total)));
        log.LogInfo("Save JSON persisters: " + string.Join("; ", metrics.Values
            .Where(m => m.Kind is Kind.Json or Kind.Persister).OrderByDescending(m => m.Total)));
        log.LogInfo("Save JSON formatting: " + string.Join("; ", jsonParts.Select(m => m.ToString())));
        for (int i = 0; i < 2; i++)
            log.LogInfo($"Save JSON split: part={(i == 0 ? "preload" : "world")}, tree~={Math.Max(0, jsonParts[i * 2].Total - jsonParts[i * 2 + 1].Total):F2} ms, print={jsonParts[i * 2 + 1].Total:F2} ms. Includes diagnostic overhead.");
    }
}
