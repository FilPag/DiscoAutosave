using DiscoAutosave;
using System.IO.Compression;
using System.Text;

void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
byte[] Archive(params (string Name, string Text)[] entries)
{
    using var stream = new MemoryStream();
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        foreach (var entry in entries)
        {
            using var output = zip.CreateEntry(entry.Name).Open();
            output.Write(Encoding.UTF8.GetBytes(entry.Text));
        }
    return stream.ToArray();
}
var ordered = Archive(("world.json", "café 漢字"), ("dialogue.lua", ""));
var reordered = Archive(("dialogue.lua", ""), ("world.json", "café 漢字"));
Check(SavePayloadComparison.Compare(ordered, reordered).All(entry => entry.Equal),
    "Payload equality must ignore ZIP entry order and compare Unicode/empty payloads.");
var changed = SavePayloadComparison.Compare(Archive(("a", "abc")), Archive(("a", "axc"))).Single();
Check(!changed.Equal && changed.FirstDifference == 1 && changed.OptimizedSha256 != changed.NativeSha256,
    "Changed bytes must report their first mismatch and different hashes.");
var truncated = SavePayloadComparison.Compare(Archive(("a", "abc")), Archive(("a", "ab"))).Single();
Check(!truncated.Equal && truncated.FirstDifference == 2 && truncated.NativeLength == 2,
    "Truncation must report the first missing byte.");
var missing = SavePayloadComparison.Compare(Archive(("left", "")), Archive(("right", "")));
Check(missing.Length == 2 && missing.All(entry => !entry.Equal), "Missing entries must not compare equal to empty files.");
try
{
    SavePayloadComparison.Compare(Archive(("a", "first"), ("a", "second")), Archive(("a", "first")));
    throw new Exception("Duplicate archive names must not silently overwrite a payload.");
}
catch (ArgumentException) { }
Console.WriteLine("PASS: native/optimized ZIP payload comparison, entry ordering, Unicode, empty/missing entries, mismatch offsets and hashes.");
var now = DateTime.UtcNow;
var pauses = RuntimeGcEvents.Pauses(new[]
{
    new RuntimeGcEvents.Sample(9, now, -1, -1),
    new RuntimeGcEvents.Sample(1, now.AddMilliseconds(1), 2, 1),
    new RuntimeGcEvents.Sample(2, now.AddMilliseconds(8), -1, -1),
    new RuntimeGcEvents.Sample(3, now.AddMilliseconds(10), -1, -1)
});
Check(pauses.Length == 1 && (pauses[0].End - pauses[0].Start).TotalMilliseconds == 10,
    "Pause must cover suspension through restart, not just the GC start/end interval.");
Check(pauses[0].Generation == 2 && pauses[0].Reason == 1, "GC metadata was lost.");
Check(RuntimeGcEvents.Pauses(new[] { new RuntimeGcEvents.Sample(9, now, -1, -1) }).Length == 0,
    "An unfinished pause must not get a fabricated duration.");

using var listener = new RuntimeGcEvents();
var start = DateTime.UtcNow;
// Forced collections run only in this standalone test process, never in the game.
GC.Collect(2, GCCollectionMode.Forced, true, true);
var deadline = DateTime.UtcNow.AddSeconds(5);
while (DateTime.UtcNow < deadline && RuntimeGcEvents.Pauses(listener.Snapshot(start, DateTime.UtcNow)).Length == 0)
    await Task.Delay(50);
var observed = RuntimeGcEvents.Pauses(listener.Snapshot(start, DateTime.UtcNow));
Check(listener.ProviderEnabled && observed.Length > 0, "Runtime GC suspension/resume events were not received.");
Check(observed.All(p => p.End >= p.Start), "Runtime GC pause timestamps are invalid.");
Console.WriteLine($"PASS: real CLR pause events, suspension timing, metadata and incomplete-pause handling; {observed.Length} observed pause(s).");
