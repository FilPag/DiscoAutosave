using System.Diagnostics;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using PixelCrushers.DialogueSystem;
using Sunshine;
using UnityEngine;
using NativeBuilder = Il2CppSystem.Text.StringBuilder;
using NativeStream = Il2CppSystem.IO.MemoryStream;

namespace DiscoAutosave;

internal sealed class ThreadedSave
{
    private static ThreadedSave current = null!;
    [ThreadStatic] private static bool replay;
    private readonly ManualLogSource log;
    private readonly ConfigEntry<bool> deferGc;
    private readonly DialogueSnapshot dialogue = new();
    private SunshinePersistence? owner;
    private Il2CppSystem.Action? completion;
    private Task<(byte[] Bytes, double Ms)>? worker;
    private Il2CppStructArray<byte>? zip;
    private Il2CppStructArray<byte>? thumbnail;
    private SunshinePersistence._SaveCoR_d__54? iterator;
    private string baseName = "", datedName = "", archivePath = "";
    private Il2CppSystem.DateTime date;
    private bool issuing, waitingForCapture, captureReady;
    private bool quickSave, aborted;
    private string? requestedName;
    internal bool QuickSaveInProgress => quickSave;
    private float beganAt;
    private int beganFrame;
    private double captureMs;
    internal bool Preparing { get; private set; }
    internal bool ReadyToAdvance => Preparing && (waitingForCapture || zip != null || worker?.IsCompleted == true);

    internal ThreadedSave(Harmony harmony, ManualLogSource log, ConfigEntry<bool> deferGc)
    {
        current = this;
        this.log = log;
        this.deferGc = deferGc;
        JsonSnapshot.Install(harmony, log);
        var builderRef = typeof(NativeBuilder).MakeByRefType();
        Patch(harmony, typeof(SunshinePersistence), "SaveCoR", postfix: nameof(CaptureIterator));
        Patch(harmony, typeof(SunshinePersistence), "AutoSave", nameof(AllowNativeAutosave));
        harmony.Patch(AccessTools.Method(typeof(SunshinePersistence._SaveCoR_d__54), "MoveNext"),
            prefix: new HarmonyMethod(typeof(ThreadedSave), nameof(EnterReplay)),
            finalizer: new HarmonyMethod(typeof(ThreadedSave), nameof(ExitReplay)));
        Patch(harmony, typeof(SunshinePersistenceFileManager), "PutDateSuffixOnPath", nameof(ReplayDate));
        Patch(harmony, typeof(SunshinePersistenceFileManager), "GetZipBytes", nameof(ReplayZip));
        Patch(harmony, typeof(SunshinePersistenceThumbnail), "GetScreenshotTextureBytes", nameof(ReplayThumbnail));
        Patch(harmony, typeof(SunshinePersistence), "SetPersistentManagerSettings", nameof(AllowSerialization));
        Patch(harmony, typeof(PersistentDataManager), "GetRawData", nameof(AllowSerialization));
        Patch(harmony, typeof(SunshinePersistenceLuaDataCollector), "GetSaveDataSerializedString", nameof(AllowSerialization));
        Patch(harmony, typeof(SunshinePersistenceJsonSerializer), "Serialize", nameof(AllowSerialization), parameters: new[] { builderRef, builderRef });
        Patch(harmony, typeof(FOWPersister), "PersistFOW", nameof(AllowSerialization));
        Patch(harmony, typeof(JsonUtil), "Serialize", nameof(AllowSerialization), parameters: new[] { typeof(Il2CppSystem.Object), builderRef });
        Patch(harmony, typeof(SunshinePersistence), "UTF8_Custom_GetBytes", nameof(AllowSerialization));
        Patch(harmony, typeof(SunshinePersistenceFileManager), "ZipItFromMemory", nameof(AllowSerialization));
        foreach (string method in new[] { "CanSave", "CanLoad", "CanQuickLoad", "CanSaveFromMainMenu" })
            Patch(harmony, typeof(SunshinePersistence), method, nameof(AllowSaveOrLoad));
        foreach (string method in new[] { "Save", "ReplaceSave", "DoQuickSave", "DoQuickLoad", "Load" })
            Patch(harmony, typeof(SunshinePersistence), method, nameof(AllowOperation));
        try
        {
            Patch(harmony, typeof(Il2CppSystem.GC), "Collect", nameof(AllowForcedGc), parameters: Type.EmptyTypes);
            Patch(harmony, typeof(Il2CppSystem.GC), "WaitForPendingFinalizers", nameof(AllowForcedGc), parameters: Type.EmptyTypes);
        }
        catch (Exception error)
        {
            deferGc.Value = false;
            log.LogWarning("Save-tail GC hooks unavailable: " + error.Message);
        }
    }

    private static void Patch(Harmony harmony, Type type, string method, string? prefix = null,
        string? postfix = null, Type[]? parameters = null) => harmony.Patch(
        AccessTools.Method(type, method, parameters) ?? throw new MissingMethodException(type.FullName, method),
        prefix: prefix == null ? null : new HarmonyMethod(typeof(ThreadedSave), prefix),
        postfix: postfix == null ? null : new HarmonyMethod(typeof(ThreadedSave), postfix));

    internal void Begin(SunshinePersistence persistence, Il2CppSystem.Action? callback, string? name = null)
    {
        if (Preparing) throw new InvalidOperationException("Autosave already in progress.");
        if (SunshinePersistenceFileManager.SavesAsSlots || persistence.saveContentType != SunshinePersistence.SAVE_CONTENT_TYPE.BYTES)
            throw new NotSupportedException("This save pipeline requires Windows byte-based local saves.");
        owner = persistence;
        completion = callback;
        requestedName = name;
        aborted = false;
        worker = null;
        zip = thumbnail = null;
        iterator = null;
        Preparing = waitingForCapture = true;
        captureReady = false;
        beganAt = Time.realtimeSinceStartup;
        beganFrame = Time.frameCount;
        dialogue.ResetPreparation();
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.Begin();
#endif
        persistence.SetPersistentManagerSettings();
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.Mark("settings");
#endif
        persistence.TakeSaveGameScreenshot();
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.Mark("screenshot-request");
#endif
        SaveSceneObjects.Prepare();
    }

    internal void Poll(bool safe)
    {
        if (!Preparing) return;
        if (Time.realtimeSinceStartup - beganAt > 120) throw new TimeoutException("Autosave preparation timed out.");
        if (!safe) { InvalidateCapture(); return; }
        if (waitingForCapture)
        {
            AdvanceCapture();
            return;
        }
        if (CollectWorker())
        {
            if (quickSave) Preparing = false;
            else IssueNativeSave();
        }
    }

    private void EncodeThumbnail()
    {
        if (!SunshinePersistenceThumbnail.ScreenshotTexture) return;
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.RestartStage();
#endif
        thumbnail = SunshinePersistenceThumbnail.GetScreenshotTextureBytes()
            ?? throw new InvalidOperationException("Autosave thumbnail capture failed.");
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.Mark("thumbnail-encode-early");
#endif
    }

    private void AdvanceCapture()
    {
        if (thumbnail == null && Time.frameCount - beganFrame >= 2 &&
            Time.realtimeSinceStartup - beganAt >= 0.2f && SunshinePersistenceThumbnail.ScreenshotTexture)
        {
            EncodeThumbnail();
            return;
        }
        if (!captureReady) { captureReady = dialogue.AdvancePreparation(); return; }
        if (thumbnail == null) return;
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.StartCapture();
#endif
        if (SaveSceneObjects.HasPlayingSequence()) { InvalidateCapture(); return; }
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.Mark("sequence-safety-scan");
#endif
        QueueSnapshot();
    }

    private void QueueSnapshot()
    {
#if DISCO_LOGS
        var timer = Stopwatch.StartNew();
#endif
#if DISCO_DIAGNOSTICS
        SaveInternals.BeginCapture();
        var dump = NativeSaveDump.Begin();
        (string Name, FlatDialogueSnapshot Flat, byte[] Tail, List<(string Name, byte[] Bytes)> Entries, JsonSnapshot Json) data;
        try { data = Capture(); }
        finally { NativeSaveDump.End(); SaveInternals.EndCapture(); }
#else
        var data = Capture();
#endif
        try
        {
#if DISCO_LOGS
            captureMs = timer.Elapsed.TotalMilliseconds;
#endif
#if DISCO_DIAGNOSTICS
            long queuedAt = SaveDiagnostics.Measure();
#endif
            worker = Task.Run(() =>
            {
#if DISCO_DIAGNOSTICS
                SaveDiagnostics.WorkerBegin(queuedAt);
#endif
                using (data.Flat)
                {
#if DISCO_LOGS
                    var clock = Stopwatch.StartNew();
#endif
                    data.Json.AddEntries(data.Entries);
#if DISCO_DIAGNOSTICS
                    SaveDiagnostics.WorkerMark("worker-json-print-utf8");
#endif
                    var bytes = SnapshotZip.Compress(data.Name, data.Flat, data.Tail, data.Entries);
#if DISCO_DIAGNOSTICS
                    SaveDiagnostics.WorkerMark("worker-output-copy");
                    if (dump != null)
                    {
                        dump.Write(bytes, data.Name, data.Entries);
                        SaveDiagnostics.WorkerMark("debug-native-comparison-write");
                    }
#endif
#if DISCO_LOGS
                    return (bytes, clock.Elapsed.TotalMilliseconds);
#else
                    return (bytes, 0.0);
#endif
                }
            });
        }
        catch { data.Flat.Dispose(); throw; }
        waitingForCapture = false;
    }

    private bool CollectWorker()
    {
        if (zip != null) return true;
        if (worker?.IsCompleted != true) return false;
        var result = worker.GetAwaiter().GetResult();
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.RestartStage();
#endif
        zip = new Il2CppStructArray<byte>(result.Bytes);
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.Mark("zip-native-copy");
#endif
        BuildLog.Info(log, $"Autosave timings: {dialogue.Timing}, capture={captureMs:F1} ms, worker={result.Ms:F1} ms.");
        worker = null;
        return true;
    }

    private (string Name, FlatDialogueSnapshot Flat, byte[] Tail, List<(string Name, byte[] Bytes)> Entries, JsonSnapshot Json) Capture()
    {
        baseName = requestedName ?? SunshinePersistenceFileManager.ReplaceInvalidFileNameChars(
            $"{SunshinePersistenceFileManager.AUTO_SAVE_SLOT_NAME}, {SunshinePersistence.GetSaveDefaultName()}");
        datedName = SunshinePersistenceFileManager.PutDateSuffixOnPath(baseName, out date);
        string prefix = SunshinePersistenceFileManager.EncodeSaveGameNames
            ? SunshinePersistenceFileManager.EncodeSaveGameName(datedName, true) : datedName;
        archivePath = Path.Combine(SunshinePersistenceFileManager.GetSaveGameDirectoryPath(),
            SunshinePersistenceFileManager.GetZipSaveGamePath(prefix));
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.Mark("save-names");
#endif
        var data = dialogue.Capture();
        try
        {
            var lua = new NativeBuilder();
            var preload = new NativeBuilder();
            var world = new NativeBuilder();
            var fow = new NativeBuilder();
            SunshinePersistenceLuaDataCollector.GetSaveDataSerializedString(ref lua);
#if DISCO_DIAGNOSTICS
            SaveDiagnostics.Mark("lua-state-serialize");
#endif
            using var json = new JsonSnapshot(prefix);
            SunshinePersistenceJsonSerializer.Serialize(ref preload, ref world);
#if DISCO_DIAGNOSTICS
            SaveDiagnostics.Mark("world-json-serialize");
#endif
            JsonUtil.Serialize(FOWPersister.PersistFOW(), ref fow);
#if DISCO_DIAGNOSTICS
            SaveDiagnostics.Mark("fog-json-serialize");
#endif
            var entries = new List<(string Name, byte[] Bytes)>
            {
                (prefix + SunshinePersistenceFileManager.LUA_STATES_FILE_SUFFIX, EncodeNativeText(lua))
            };
#if DISCO_DIAGNOSTICS
            SaveDiagnostics.Mark("text-utf8-copy");
#endif
            return (prefix + SunshinePersistenceFileManager.LUA_SAVE_GAME_FILE_SUFFIX, data.Flat, data.Tail, entries, json);
        }
        catch { data.Flat.Dispose(); throw; }
    }

    internal static byte[] EncodeNativeText(NativeBuilder builder)
    {
        var stream = new NativeStream();
        try
        {
            SunshinePersistence.UTF8_Custom_GetBytes(builder, stream);
            return stream.GetBuffer().AsSpan()[..checked((int)stream.Length)].ToArray();
        }
        finally { stream.Dispose(); }
    }

    private void IssueNativeSave()
    {
        issuing = true;
        try
        {
            if (!SunshinePersistence.CanSave()) return;
            owner!.AutoSave(completion!, true);
            if (iterator == null) throw new InvalidOperationException("Native save coroutine was not created.");
            Preparing = false;
        }
        finally { issuing = false; }
    }

    internal void CheckWrittenSave()
    {
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.RestartStage();
#endif
        if (zip == null || !File.Exists(archivePath) || new FileInfo(archivePath).Length != zip.Length)
            throw new IOException("Native autosave completed without the expected archive on disk.");
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.Mark("archive-length-check");
        SaveDiagnostics.Complete();
#endif
    }

    internal void InvalidateCapture()
    {
        if (!waitingForCapture) return;
        captureReady = false;
        dialogue.ResetPreparation();
    }
    internal void ClearDialogueCache() => dialogue.ClearCache();
    internal void AbortPreparation()
    {
        aborted = true;
        Preparing = false;
        dialogue.ResetPreparation();
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.Complete(true);
#endif
    }
    private static bool AllowSerialization() => !replay;
    private static bool AllowNativeAutosave(Il2CppSystem.Action onSaveComplete)
    {
        if (current.issuing || !Plugin.Instance.OwnsAutosave) return true;
        BuildLog.Info(current.log, "Built-in autosave suppressed; mod scheduler owns autosaving.");
        // Native AutoSave also completes its callback when CanSave rejects a request.
        onSaveComplete?.Invoke();
        return false;
    }
    private static bool AllowForcedGc() => !replay || !current.deferGc.Value;
    private static bool AllowOperation() => !current.Preparing || current.issuing;
    private static bool AllowSaveOrLoad(ref bool __result)
    {
        if (AllowOperation()) return true;
        __result = false;
        return false;
    }
    private static void CaptureIterator(SunshinePersistence __instance, string fileNamePrefix,
        SunshinePersistence.SAVE_MODE saveMode, ref Il2CppSystem.Collections.IEnumerator __result)
    {
        if (current.issuing && saveMode == SunshinePersistence.SAVE_MODE.AUTOSAVE)
            current.iterator = __result.Cast<SunshinePersistence._SaveCoR_d__54>();
        else if (saveMode == SunshinePersistence.SAVE_MODE.QUICKSAVE && !current.Preparing &&
            !current.quickSave && Plugin.Instance.CanOptimizeQuicksave &&
            !SunshinePersistenceFileManager.SavesAsSlots && __instance.saveContentType == SunshinePersistence.SAVE_CONTENT_TYPE.BYTES)
            __result = current.PrepareQuicksave(__instance, fileNamePrefix,
                __result.Cast<SunshinePersistence._SaveCoR_d__54>()).WrapToIl2Cpp();
    }

    private System.Collections.IEnumerator PrepareQuicksave(SunshinePersistence persistence, string name,
        SunshinePersistence._SaveCoR_d__54 native)
    {
        bool claimed = false;
        try
        {
            if (!Preparing && !quickSave && Plugin.Instance.CanOptimizeQuicksave)
            {
                claimed = true;
                BeginQuicksave(persistence, name, native);
            }
            while (claimed && Preparing) yield return null;
            bool optimized = claimed && !aborted && zip != null;
            if (claimed && !optimized)
            {
                iterator = null;
                log.LogWarning("Optimized quicksave unavailable; continuing the original native quicksave.");
            }
            while (true)
            {
                bool next;
                try { next = native.MoveNext(); }
                catch (Exception error) { Plugin.Instance.SaveFailed(error); throw; }
                if (!next) break;
                yield return native.__2__current;
            }
            if (optimized)
            {
                try { CheckWrittenSave(); BuildLog.Info(log, "Quicksave written through optimized pipeline."); }
                catch (Exception error) { Plugin.Instance.SaveFailed(error); }
            }
        }
        finally
        {
            if (claimed)
            {
                if (Preparing) AbortPreparation();
                iterator = null;
                quickSave = false;
            }
            native.System_IDisposable_Dispose();
        }
    }

    private void BeginQuicksave(SunshinePersistence persistence, string name, SunshinePersistence._SaveCoR_d__54 native)
    {
        try
        {
            Begin(persistence, native.onSaveComplete, name);
            iterator = native;
            quickSave = true;
        }
        catch (Exception error)
        {
            AbortPreparation();
            log.LogWarning("Quicksave preparation failed; retaining native save: " + error);
        }
    }

    private static void EnterReplay(SunshinePersistence._SaveCoR_d__54 __instance, out bool __state)
    {
        __state = replay;
        replay = current.iterator != null && __instance.Pointer == current.iterator.Pointer;
#if DISCO_DIAGNOSTICS
        if (__instance.__1__state == 0) SaveDiagnostics.SaveStarted(__instance.saveMode.ToString(), replay);
        if (replay) SaveDiagnostics.NativeEnter(__instance.__1__state);
#endif
        if (replay && __instance.__1__state == 0) __instance.fileNamePrefix = current.baseName;
    }
    private static Exception? ExitReplay(Exception? __exception, bool __state)
    {
#if DISCO_DIAGNOSTICS
        if (replay) SaveDiagnostics.NativeExit();
#endif
        replay = __state;
        return __exception;
    }
    private static bool ReplayDate(ref string __result, ref Il2CppSystem.DateTime date)
    {
        if (!replay) return true;
        __result = current.datedName;
        date = current.date;
        return false;
    }
    private static bool ReplayZip(ref Il2CppStructArray<byte> zipBytes, ref int zipByteLength)
    {
        if (!replay) return true;
        zipBytes = current.zip ?? throw new InvalidOperationException("Missing autosave ZIP.");
        zipByteLength = zipBytes.Length;
        return false;
    }
    private static bool ReplayThumbnail(ref Il2CppStructArray<byte> __result)
    {
        if (!replay) return true;
        __result = current.thumbnail!;
        return false;
    }
}
