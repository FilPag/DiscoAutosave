using System.Diagnostics;
using Language.Lua;
using PixelCrushers.DialogueSystem;
using NativeStream = Il2CppSystem.IO.MemoryStream;

namespace DiscoAutosave;

internal sealed class DialogueSnapshot
{
    private PersistentDataManager._PrepSimStatusForRawDataAsync_d__67? preparation;
    private bool prepared;
    private double totalMs;
    private double maxSliceMs;
    private int frames;
    private readonly LuaSnapshotReader reader = new();
    private bool cachePrepared;

    internal bool AdvancePreparation()
    {
        if (prepared)
        {
            if (!cachePrepared)
            {
                long warm = Stopwatch.GetTimestamp();
#if DISCO_DIAGNOSTICS
                SaveDiagnostics.RestartStage();
#endif
                cachePrepared = reader.WarmConversation(GetTable("Conversation"),
                    Math.Clamp(UnityEngine.Time.unscaledDeltaTime * 200.0, 1.0, 5.0));
                double warmMs = ElapsedMs(warm);
                totalMs += warmMs;
                maxSliceMs = Math.Max(maxSliceMs, warmMs);
                frames++;
#if DISCO_DIAGNOSTICS
                SaveDiagnostics.Mark("dialogue-cache-warm");
#endif
            }
            return cachePrepared;
        }
        preparation ??= PersistentDataManager.PrepSimStatusForRawDataAsync(GetTable("Conversation"))
            .Cast<PersistentDataManager._PrepSimStatusForRawDataAsync_d__67>();
        int previousBatch = PersistentDataManager.asyncDialogueEntryBatchSize;
        double budgetMs = Math.Clamp(UnityEngine.Time.unscaledDeltaTime * 200.0, 1.0, 5.0);
        long timer = Stopwatch.GetTimestamp();
        try
        {
            PersistentDataManager.asyncDialogueEntryBatchSize = 16;
            for (int steps = 0; steps < 64; steps++)
            {
                if (!preparation.MoveNext())
                {
                    preparation.System_IDisposable_Dispose();
                    preparation = null;
                    prepared = true;
                    break;
                }
                if (ElapsedMs(timer) >= budgetMs) break;
            }
        }
        finally { PersistentDataManager.asyncDialogueEntryBatchSize = previousBatch; }
        double elapsed = ElapsedMs(timer);
        totalMs += elapsed;
        maxSliceMs = Math.Max(maxSliceMs, elapsed);
        frames++;
        return false;
    }

    internal (FlatDialogueSnapshot Flat, byte[] Tail) Capture()
    {
        if (!prepared) throw new InvalidOperationException("Dialogue preparation is incomplete.");
        RecordSnapshot.Record();
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.Mark("dialogue-record");
#endif
        var flat = new FlatDialogueSnapshot();
        try
        {
            reader.ReadFlat(GetTable("Actor"), flat, "Actor");
#if DISCO_DIAGNOSTICS
            SaveDiagnostics.Mark("flat-actor");
#endif
            reader.ReadFlat(GetTable("Item"), flat, "Item");
#if DISCO_DIAGNOSTICS
            SaveDiagnostics.Mark("flat-item");
#endif
            reader.ReadFlat(GetTable("Location"), flat, "Location");
#if DISCO_DIAGNOSTICS
            SaveDiagnostics.Mark("flat-location");
#endif
            reader.ReadFlat(GetTable("Variable"), flat, "Variable");
#if DISCO_DIAGNOSTICS
            SaveDiagnostics.Mark("flat-variable");
#endif
            reader.ReadFlat(GetTable("Conversation"), flat, "Conversation");
#if DISCO_DIAGNOSTICS
            SaveDiagnostics.Mark("flat-conversation");
#endif
            var tail = new NativeStream();
            var writer = new Il2CppSystem.IO.BinaryWriter(tail);
            try
            {
                PersistentDataManager.WriteExtraData(writer);
                writer.Flush();
                var bytes = tail.GetBuffer().AsSpan()[..checked((int)tail.Length)].ToArray();
#if DISCO_DIAGNOSTICS
                SaveDiagnostics.Mark("dialogue-extra");
                NativeSaveDump.CaptureDialogue();
#endif
                return (flat, bytes);
            }
            finally { writer.Dispose(); tail.Dispose(); }
        }
        catch { flat.Dispose(); throw; }
    }

    internal string Timing => $"prep={totalMs:F1} ms/{frames} frames, max slice={maxSliceMs:F1} ms";

    internal void ResetPreparation()
    {
        preparation?.System_IDisposable_Dispose();
        preparation = null;
        prepared = false;
        cachePrepared = false;
        reader.ResetWarmup();
        totalMs = maxSliceMs = 0;
        frames = 0;
    }

    internal void ClearCache() => reader.ClearCache();

    private static double ElapsedMs(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

    private static LuaTable GetTable(string name) => Lua.Run("return " + name).AsTable.luaTable;
}
