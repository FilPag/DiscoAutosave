using DiscoAutosave;

internal static class ObjectIndexTests
{
    private sealed class Item { internal bool Alive = true; internal bool Active; }

    internal static void Run()
    {
        var index = new OrderedObjectIndex<Item>();
        var disabled = new Item();
        var active = new Item { Active = true };
        index.Add(9, active); index.Add(2, disabled); index.Add(9, active);
        var before = index.Snapshot(item => item.Alive);
        if (before.Length != 2 || before[0] != disabled || before[1] != active)
            throw new Exception("Targets lost ordering, inactive membership or deduplication.");
        disabled.Active = true;
        active.Alive = false;
        var added = new Item { Active = true };
        index.Add(3, added);
        var after = index.Snapshot(item => item.Alive);
        if (after.Length != 2 || after[0] != disabled || !after[0].Active || after[1] != added ||
            before.Length != 2 || before[1] != active)
            throw new Exception("Creation/destruction/activation changed an existing dispatch snapshot.");
        active.Alive = true;
        if (index.Snapshot(item => item.Alive).Contains(active)) throw new Exception("Dead registration was not pruned.");
        index.Add(9, active);
        if (index.Snapshot(item => item.Alive).Length != 3) throw new Exception("Re-registration failed.");
        Console.WriteLine("PASS: ordered deduplicated object snapshots; creation, pruning, re-registration, inactive membership and dispatch isolation.");
    }
}
