using DiscoAutosave;
using System.IO.Compression;
using System.Text;

void Check(bool result) { if (!result) throw new Exception("Check failed"); }
SnapshotCopyTests.Run();
DialogueCacheTests.Run();
ObjectIndexTests.Run();
foreach (int fps in new[] { 30, 40, 60, 90, 120, 144, 240 })
    Check(Math.Abs(SavePreparationBudget.Milliseconds(1.0 / fps) * fps - 100.0) < 0.001);
Check(SavePreparationBudget.Milliseconds(1.0) == 4.0);
Check(SavePreparationBudget.Milliseconds(0) == 0.25);
Check(SavePreparationBudget.Milliseconds(double.NaN) == 0.25);
Check(SavePreparationBudget.Milliseconds(double.PositiveInfinity) == 0.25);
Console.WriteLine("PASS: equal preparation budget per second at 30–240 FPS; stalls and invalid frame times bounded.");
var schedule = new SaveSchedule();
for (int i = 0; i < 299; i++) schedule.Advance(1, true);
schedule.Advance(60, false);
Check(!schedule.Due(5, "street", 300, true));
schedule.Advance(1, true);
Check(schedule.Due(5, "street", 301, true));
Check(!schedule.Due(5, "street", 301, false));
schedule.Reset();
schedule.Travel("building", 0);
Check(!schedule.Due(5, "street", 1, true));
Check(!schedule.Due(5, "building", 2, false));
Check(schedule.Due(5, "building", 2.01, true));
Check(!schedule.Due(5, "building", 3, false));
Check(schedule.Due(5, "building", 4, true));
schedule.Reset();
Check(!schedule.Due(5, "building", 5, true));
schedule.Travel("street", 6);
schedule.CancelTravel();
Check(!schedule.Due(5, "street", 7, true));
schedule.Reset();
schedule.Travel("street", 0);
Check(!schedule.Due(5, "street", 121, true));
Console.WriteLine("PASS: scheduling, paused time, safe arrival, reset and expired travel.");

void CopyValue(BinaryReader input, FlatDialogueSnapshot flat)
{
    byte tag = input.ReadByte();
    switch ((char)tag)
    {
        case 'X': flat.Byte(tag); break;
        case 'B': flat.WriteBoolean(input.ReadBoolean()); break;
        case 'N': flat.WriteNumber(input.ReadDouble()); break;
        case 'S': flat.WriteText(input.ReadString()); break;
        case 'T':
            int count = input.ReadInt32();
            if (count == 0)
            {
                int pairsOnly = input.ReadInt32(); flat.BeginDictionaryTable(pairsOnly);
                for (int i = 0; i < pairsOnly; i++) { CopyValue(input, flat); CopyValue(input, flat); }
                break;
            }
            flat.BeginTable(count);
            for (int i = 0; i < count; i++) CopyValue(input, flat);
            count = input.ReadInt32(); flat.Int(count);
            for (int i = 0; i < count; i++) { CopyValue(input, flat); CopyValue(input, flat); }
            break;
        default: throw new InvalidDataException();
    }
}

var vectors = new List<byte[]>();
using (var stream = new MemoryStream())
{
    using var w = new BinaryWriter(stream, Encoding.UTF8, true);
    w.Write((byte)'T'); w.Write(4);
    w.Write((byte)'X'); w.Write((byte)'B'); w.Write(true);
    w.Write((byte)'N'); w.Write(-123.25);
    w.Write((byte)'S'); w.Write("café 漢字\0\n" + new string('x', 20000));
    w.Write(1); w.Write((byte)'S'); w.Write("key"); w.Write((byte)'N'); w.Write(double.PositiveInfinity);
    w.Flush(); vectors.Add(stream.ToArray());
}
foreach (string path in args)
{
    using var archive = ZipFile.OpenRead(path);
    using var entry = archive.Entries.Single(e => e.FullName.EndsWith(".ntwtf.lua")).Open();
    using var copy = new MemoryStream(); entry.CopyTo(copy); vectors.Add(copy.ToArray());
}
foreach (var bytes in vectors)
{
    using var flat = new FlatDialogueSnapshot();
    using var source = new MemoryStream(bytes);
    using var reader = new BinaryReader(source);
    int rootCount = ReferenceEquals(bytes, vectors[0]) ? 1 : 5;
    for (int i = 0; i < rootCount; i++) { flat.BeginRoot(); CopyValue(reader, flat); }
    var tail = bytes.AsSpan((int)source.Position).ToArray();
    int mainThread = Environment.CurrentManagedThreadId;
    var result = Task.Run(() => (Environment.CurrentManagedThreadId,
        SnapshotZip.Compress("save.ntwtf.lua", flat, tail, new[] { ("extra.json", Encoding.UTF8.GetBytes("{}")) }))).GetAwaiter().GetResult();
    Check(result.Item1 != mainThread);
    using var packed = new MemoryStream(result.Item2);
    using var zip = new ZipArchive(packed, ZipArchiveMode.Read);
    using var decoded = new MemoryStream(); zip.GetEntry("save.ntwtf.lua")!.Open().CopyTo(decoded);
    Check(decoded.ToArray().AsSpan().SequenceEqual(bytes));
    Check(zip.Entries.Count == 2);
}
Console.WriteLine($"PASS: streamed worker ZIP, Unicode/long strings/all tags; {args.Length} native payload(s) byte-exact.");

FlatDialogueSnapshot NumberSnapshot(double number)
{
    var flat = new FlatDialogueSnapshot();
    flat.BeginRoot(); flat.WriteNumber(number);
    return flat;
}
double ReadNumber(FlatDialogueSnapshot flat)
{
    using var reader = new BinaryReader(new MemoryStream(flat.Encode(Array.Empty<byte>())));
    Check(reader.ReadByte() == (byte)'N');
    return reader.ReadDouble();
}
using (var first = NumberSnapshot(1))
using (var second = NumberSnapshot(2))
{
    Task.Run(first.Dispose).GetAwaiter().GetResult();
    using var third = NumberSnapshot(3);
    Check(ReadNumber(second) == 2 && ReadNumber(third) == 3);
}
FlatDialogueSnapshot.WarmUp();
var handedOff = NumberSnapshot(4);
Task.Run(handedOff.Dispose).GetAwaiter().GetResult();
long before = GC.GetAllocatedBytesForCurrentThread();
using (var next = NumberSnapshot(5)) Check(ReadNumber(next) == 5);
long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
Check(allocated < 1024 * 1024);
Console.WriteLine($"PASS: independent snapshots, repeated disposal and cross-thread buffer reuse; warm capture allocated {allocated} B (<1 MiB).");
