using DiscoAutosave;
using System.Runtime.InteropServices;

internal static unsafe class DialogueCacheTests
{
    internal static void Run()
    {
        nint memory = Marshal.AllocHGlobal(32);
        try
        {
            *(int*)memory = 1;
            *(long*)(memory + 8) = BitConverter.DoubleToInt64Bits(7);
            *(byte*)(memory + 16) = 0;
            *(nint*)(memory + 24) = 123;
            var stamps = new[]
            {
                DialogueMemoryStamp.Capture(memory, 4),
                DialogueMemoryStamp.Capture(memory + 8, 8),
                DialogueMemoryStamp.Capture(memory + 16, 1),
                DialogueMemoryStamp.Capture(memory + 24, 8)
            };
            using var source = new FlatDialogueSnapshot();
            source.WriteNumber(7);
            var cache = new DialogueBlockCache();
            cache.Store(1, new object(), source.CopyFrom(0), stamps);
            using var output = new FlatDialogueSnapshot();
            cache.Begin();
            output.BeginRoot();
            if (!cache.Append(1, output)) throw new Exception("Unchanged dialogue block not reused.");
            foreach (int offset in new[] { 0, 8, 16, 24 })
            {
                *(byte*)(memory + offset) ^= 1;
                if (cache.Append(1, output)) throw new Exception("Dialogue mutation returned stale data.");
                *(byte*)(memory + offset) ^= 1;
            }
            cache.Store(2, new object(), source.CopyFrom(0), new[]
            {
                new DialogueMemoryStamp(memory, 0, 4),
                new DialogueMemoryStamp(1, 0, 8)
            });
            if (cache.Append(2, output)) throw new Exception("Removed child accepted after parent mutation.");
            using var next = new FlatDialogueSnapshot();
            cache.Begin(); cache.Append(1, next); cache.End();
            if (cache.Append(2, output)) throw new Exception("Unused block retained.");
            cache.Clear();
            using var reader = new BinaryReader(new MemoryStream(output.Encode(Array.Empty<byte>())));
            if (reader.ReadByte() != 'N' || reader.ReadDouble() != 7)
                throw new Exception("Worker bytes changed when cache was cleared.");
            if (cache.Append(1, output)) throw new Exception("Cache survived reset.");
            Console.WriteLine("PASS: cached dialogue bytes, collection/scalar/string-pointer invalidation, parent-before-child checks, eviction, reset and detached worker ownership.");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
}
