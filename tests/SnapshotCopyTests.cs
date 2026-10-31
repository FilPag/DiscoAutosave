using DiscoAutosave;

internal static class SnapshotCopyTests
{
    private struct Entry { public List<int> Values; }
    private sealed class State
    {
        public string Name = "before";
        public Dictionary<string, List<int>> Data = new();
        public State? Next;
        public State? Alias;
        public object[] Objects = Array.Empty<object>();
        public Entry[] Entries = Array.Empty<Entry>();
        public HashSet<string> Tags = new(StringComparer.OrdinalIgnoreCase);
        public Action? Changed;
    }

    internal static void Run()
    {
        var shared = new List<int> { 1, 2 };
        var source = new State
        {
            Data = new() { ["a"] = shared, ["b"] = shared },
            Entries = new[] { new Entry { Values = shared } },
            Objects = new object[] { shared, new int[] { 3, 4 } },
            Changed = () => throw new Exception("Observer must not be copied.")
        };
        source.Next = source.Alias = source;
        source.Tags.Add("MixedCase");
        var copier = new NativeSnapshotCopier();
        var detached = (State)copier.Copy(source);
        shared[0] = 99;
        source.Name = "after";
        ((int[])source.Objects[1])[0] = 88;
        source.Tags.Add("new");
        Task.Run(() =>
        {
            if (detached.Name != "before" || detached.Data["a"][0] != 1 ||
                ((int[])detached.Objects[1])[0] != 3 || detached.Entries[0].Values[0] != 1 ||
                !detached.Tags.Contains("MIXEDCASE") || detached.Tags.Contains("new") || detached.Changed != null ||
                !ReferenceEquals(detached.Next, detached) || !ReferenceEquals(detached.Alias, detached) ||
                !ReferenceEquals(detached.Data["a"], detached.Data["b"]) ||
                !ReferenceEquals(detached.Objects[0], detached.Data["a"]))
                throw new Exception("Detached snapshot changed, lost aliases/cycles or collection behavior.");
        }).GetAwaiter().GetResult();
        var next = (State)copier.Copy(source);
        if (next.Data["a"][0] != 99) throw new Exception("Snapshot copier reused stale state.");
        bool rejected = false;
        try { copier.Copy(new object[] { new IntPtr(123) }); }
        catch (NotSupportedException) { rejected = true; }
        if (!rejected) throw new Exception("Native handle was not rejected.");
        if (((State)copier.Copy(source)).Data["a"][0] != 99) throw new Exception("Failed capture contaminated later snapshots.");
        Console.WriteLine("PASS: detached graph survives source mutation on worker; nested lists/dictionaries, struct/object arrays, aliases, cycles, comparers, observer exclusion, handle rejection and reuse.");
    }
}
