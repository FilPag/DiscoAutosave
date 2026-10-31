namespace DiscoAutosave;

internal readonly unsafe record struct DialogueMemoryStamp(nint Address, long Value, byte Width)
{
    internal static DialogueMemoryStamp Capture(nint address, byte width) => new(address, Read(address, width), width);
    internal bool Matches => Read(Address, Width) == Value;
    private static long Read(nint address, byte width) => width switch
    {
        1 => *(byte*)address,
        4 => *(int*)address,
        8 => *(long*)address,
        _ => throw new ArgumentOutOfRangeException(nameof(width))
    };
}

internal sealed class DialogueBlockCache
{
    private sealed record Block(object Anchor, byte[] Bytes, DialogueMemoryStamp[] Stamps);
    private readonly Dictionary<nint, Block> blocks = new();
    private readonly HashSet<nint> used = new();
    internal int Hits { get; private set; }
    internal int Misses { get; private set; }
    internal int Checks { get; private set; }

    internal void Begin() { used.Clear(); Hits = Misses = Checks = 0; }

    internal bool Append(nint key, FlatDialogueSnapshot output)
    {
        used.Add(key);
        if (blocks.TryGetValue(key, out var block))
        {
            // Parents precede descendants: never dereference a removed child's old address.
            foreach (var stamp in block.Stamps)
            {
                Checks++;
                if (!stamp.Matches) { Misses++; return false; }
            }
            output.Append(block.Bytes);
            Hits++;
            GC.KeepAlive(block.Anchor);
            return true;
        }
        Misses++;
        return false;
    }

    internal void Store(nint key, object anchor, byte[] bytes, DialogueMemoryStamp[] stamps) => blocks[key] = new(anchor, bytes, stamps);
    internal void End()
    {
        foreach (var key in blocks.Keys.Where(key => !used.Contains(key)).ToArray()) blocks.Remove(key);
    }
    internal void Clear() { blocks.Clear(); Begin(); }
}
