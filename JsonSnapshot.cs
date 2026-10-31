using System.Text;
using System.Diagnostics;
using FullSerializer;
using HarmonyLib;
using Il2CppInterop.Runtime;
using NativeBuilder = Il2CppSystem.Text.StringBuilder;
using BepInEx.Logging;

namespace DiscoAutosave;

internal sealed class JsonSnapshot : IDisposable
{
    [ThreadStatic] private static JsonSnapshot? active;
    private static ManualLogSource log = null!;
    private readonly List<(Il2CppSystem.Object? Value, fsData? Tree)> roots = new(3);
    private readonly NativeSnapshotCopier copier = new();
    private readonly fsSerializer? serializer;
    private readonly string[] names;

    internal static void Install(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        harmony.Patch(AccessTools.Method(typeof(Sunshine.JsonUtil), "Serialize",
            new[] { typeof(Il2CppSystem.Object), typeof(NativeBuilder).MakeByRefType() }),
            prefix: new HarmonyMethod(typeof(JsonSnapshot), nameof(DeferSerialization)));
        harmony.Patch(AccessTools.Method(typeof(fsJsonPrinter), "PrettyJson",
            new[] { typeof(fsData), typeof(NativeBuilder).MakeByRefType() }),
            prefix: new HarmonyMethod(typeof(JsonSnapshot), nameof(DeferPrint)));
    }

    internal JsonSnapshot(string prefix)
    {
        if (active != null) throw new InvalidOperationException("Nested JSON capture.");
        names = new[]
        {
            prefix + SunshinePersistenceFileManager.JSON_PRELOAD_SAVE_GAME_FILE_SUFFIX,
            prefix + SunshinePersistenceFileManager.JSON_SAVE_GAME_FILE_SUFFIX,
            prefix + SunshinePersistenceFileManager.FOW_SAVE_GAME_FILE_SUFFIX
        };
        try
        {
            // Never use JsonUtil's mutable singleton serializer on the worker.
            serializer = new fsSerializer
            {
                Config = Sunshine.JsonUtil.serializer.Config.MemberwiseClone().Cast<fsConfig>(),
                Context = copier.Copy(Sunshine.JsonUtil.serializer.Context).Cast<fsContext>()
            };
        }
        catch (Exception error)
        {
            ReportSnapshot("native tree retained; serializer context is not detached: " + error.Message);
        }
        active = this;
    }

    private static bool DeferSerialization(Il2CppSystem.Object value)
    {
#if DISCO_DIAGNOSTICS
        if (NativeSaveDump.ReadingNative) return true;
        if (active != null) NativeSaveDump.CaptureJson(value);
#endif
        if (active?.serializer == null) return true;
        long began = Stopwatch.GetTimestamp();
        try
        {
            var detached = active.copier.Copy(value);
            active.roots.Add((detached, null));
            ReportSnapshot($"detached {active.roots.Count}/3 in {(Stopwatch.GetTimestamp() - began) * 1000.0 / Stopwatch.Frequency:F2} ms; tree construction queued to worker.");
            return false;
        }
        catch (Exception error)
        {
            ReportSnapshot("native tree retained; unsupported snapshot graph: " + error.Message);
            return true;
        }
    }

    [Conditional("DISCO_LOGS")]
    private static void ReportSnapshot(string message) => BuildLog.Info(log, "Save JSON snapshot: " + message);

    private static bool DeferPrint(fsData data)
    {
#if DISCO_DIAGNOSTICS
        if (NativeSaveDump.ReadingNative) return true;
#endif
        if (active == null) return true;
        // Serialization has finished: fsData contains only JSON containers and scalar values.
        active.roots.Add((null, data));
        return false;
    }

    internal void AddEntries(List<(string Name, byte[] Bytes)> entries)
    {
        if (roots.Count != names.Length) throw new InvalidDataException("Expected preload, world and fog JSON trees.");
        bool attach = IL2CPP.il2cpp_thread_current() == IntPtr.Zero;
        var thread = attach ? IL2CPP.il2cpp_thread_attach(IL2CPP.il2cpp_domain_get()) : IntPtr.Zero;
        if (attach && thread == IntPtr.Zero) throw new InvalidOperationException("Could not attach JSON worker to IL2CPP.");
        try
        {
            for (int i = 0; i < roots.Count; i++)
            {
                var root = roots[i];
                fsData tree;
                if (root.Tree != null) tree = root.Tree;
                else
                {
                    var value = root.Value!;
                    var type = Il2CppSystem.Type.internal_from_handle(IL2CPP.il2cpp_class_get_type(IL2CPP.il2cpp_object_get_class(value.Pointer)));
                    serializer!.TrySerialize(type, value, out tree).AssertSuccessWithoutWarnings();
                }
                entries.Add((names[i], Encoding.UTF8.GetBytes(fsJsonPrinter.PrettyJson(tree))));
            }
        }
        finally
        {
            roots.Clear();
            if (attach) IL2CPP.il2cpp_thread_detach(thread);
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(active, this)) active = null;
    }
}
