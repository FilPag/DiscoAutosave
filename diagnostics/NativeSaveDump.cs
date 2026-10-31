using System.IO.Compression;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using PixelCrushers.DialogueSystem;
using Sunshine;
using NativeBuilder = Il2CppSystem.Text.StringBuilder;
using NativeStream = Il2CppSystem.IO.MemoryStream;

namespace DiscoAutosave;

internal sealed class NativeSaveDump
{
    [ThreadStatic] private static NativeSaveDump? active;
    [ThreadStatic] internal static bool ReadingNative;
    private static ConfigEntry<int> limit = null!;
    private static ManualLogSource log = null!;
    private static int captures;
    private readonly List<byte[]> json = new(3);
    private byte[]? dialogue;
    private string? failure;
    private readonly string directory;

    private NativeSaveDump() => directory = Path.Combine(Paths.BepInExRootPath, "DiscoAutosave-comparisons",
        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N"));

    internal static void Install(ConfigEntry<int> setting, ManualLogSource logger) { limit = setting; log = logger; }

    internal static NativeSaveDump? Begin()
    {
        if (captures >= limit.Value) return null;
        captures++;
        log.LogWarning("Native comparison capture enabled for this save: extra main-thread serialization will hitch; dumps contain full save data.");
        return active = new NativeSaveDump();
    }

    internal static void End() => active = null;

    internal static void CaptureDialogue()
    {
        if (active == null) return;
        try
        {
            var stream = new NativeStream();
            var writer = new Il2CppSystem.IO.BinaryWriter(stream);
            try
            {
                // Do not call GetRawData: it would Record and prepare the live world a second time.
                foreach (string name in new[] { "Actor", "Item", "Location", "Variable", "Conversation" })
                    PersistentDataManager.WriteValue(writer, Lua.Run("return " + name).AsTable.luaTable);
                PersistentDataManager.WriteExtraData(writer);
                writer.Flush();
                active.dialogue = stream.GetBuffer().AsSpan()[..checked((int)stream.Length)].ToArray();
            }
            finally { writer.Dispose(); stream.Dispose(); }
        }
        catch (Exception error) { active.failure = error.ToString(); }
        SaveDiagnostics.Mark("debug-native-dialogue-dump");
    }

    internal static void CaptureJson(Il2CppSystem.Object value)
    {
        if (active == null || ReadingNative) return;
        long began = SaveDiagnostics.Measure();
        try
        {
            ReadingNative = true;
            var builder = new NativeBuilder();
            JsonUtil.Serialize(value, ref builder);
            active.json.Add(ThreadedSave.EncodeNativeText(builder));
        }
        catch (Exception error) { active.failure = error.ToString(); }
        finally { ReadingNative = false; SaveDiagnostics.Measured("debug-native-json-dump", began); }
    }

    internal void Write(byte[] optimized, string dialogueName, IReadOnlyList<(string Name, byte[] Bytes)> entries)
    {
        try
        {
            if (failure != null || dialogue == null || json.Count != 3)
                throw new InvalidDataException("Incomplete native comparison capture: " + failure);
            var replacements = new Dictionary<string, byte[]> { [dialogueName] = dialogue };
            for (int i = 0; i < 3; i++) replacements.Add(entries[i + 1].Name, json[i]);
            using var nativeStream = new MemoryStream();
            using (var archive = new ZipArchive(nativeStream, ZipArchiveMode.Create, true))
            {
                foreach (var entry in entries.Prepend((Name: dialogueName, Bytes: dialogue)))
                {
                    using var output = archive.CreateEntry(entry.Name, CompressionLevel.Fastest).Open();
                    output.Write(replacements.TryGetValue(entry.Name, out var bytes) ? bytes : entry.Bytes);
                }
            }
            byte[] native = nativeStream.ToArray();
            var comparison = SavePayloadComparison.Compare(optimized, native);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "optimized.zip"), optimized);
            File.WriteAllBytes(Path.Combine(directory, "native.zip"), native);
            File.WriteAllText(Path.Combine(directory, "comparison.json"), JsonSerializer.Serialize(new
            {
                Equal = comparison.All(entry => entry.Equal), Entries = comparison,
                Scope = "Same-frame native WriteValue/WriteExtraData and live JsonUtil versus optimized capture. Lua states use the shared native export. Does not validate receiver discovery or reload equivalence."
            }));
            string result = $"Native save comparison: {comparison.Count(entry => entry.Equal)}/{comparison.Length} entries byte-exact; {directory}";
            if (comparison.All(entry => entry.Equal)) log.LogInfo(result);
            else log.LogWarning(result);
        }
        catch (Exception error) { log.LogWarning("Native save comparison unavailable (saving unaffected): " + error); }
    }
}
