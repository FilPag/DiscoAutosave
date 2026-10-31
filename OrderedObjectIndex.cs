namespace DiscoAutosave;

internal sealed class OrderedObjectIndex<T> where T : class
{
    private readonly SortedDictionary<int, T> objects = new();

    internal void Add(int id, T value) => objects[id] = value;

    internal T[] Snapshot(Func<T, bool> alive)
    {
        var result = new List<T>(objects.Count);
        var dead = new List<int>();
        foreach (var pair in objects)
            if (alive(pair.Value)) result.Add(pair.Value);
            else dead.Add(pair.Key);
        foreach (int id in dead) objects.Remove(id);
        return result.ToArray();
    }
}
