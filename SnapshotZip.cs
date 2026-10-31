using System.IO.Compression;

namespace DiscoAutosave;

public static class SnapshotZip
{
    public static byte[] Compress(string dialogueName, FlatDialogueSnapshot dialogue, byte[] tail,
        IReadOnlyList<(string Name, byte[] Bytes)> otherEntries)
    {
        using var output = new MemoryStream(2 * 1024 * 1024);
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.WorkerMark("worker-zip-allocation");
#endif
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (var entry = zip.CreateEntry(dialogueName, CompressionLevel.Fastest).Open())
            using (var buffered = new BufferedStream(entry, 64 * 1024))
                dialogue.WriteTo(buffered, tail);
#if DISCO_DIAGNOSTICS
            SaveDiagnostics.WorkerMark("worker-dialogue-zip");
#endif
            foreach (var item in otherEntries)
            {
                using (var entry = zip.CreateEntry(item.Name, CompressionLevel.Fastest).Open())
                    entry.Write(item.Bytes);
#if DISCO_DIAGNOSTICS
                SaveDiagnostics.WorkerMark("worker-other-zip-entry");
#endif
            }
        }
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.WorkerMark("worker-zip-finalize");
#endif
        return output.ToArray();
    }
}
