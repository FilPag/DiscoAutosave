using Il2CppInterop.Runtime;
using Language.Lua;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using NativeDictionary = Il2CppSystem.Collections.Generic.Dictionary<Language.Lua.LuaValue, Language.Lua.LuaValue>;
using NativeList = Il2CppSystem.Collections.Generic.List<Language.Lua.LuaValue>;

namespace DiscoAutosave;

internal sealed unsafe class LuaSnapshotReader
{
    private static readonly int Header = 2 * IntPtr.Size;
    private static readonly int ArrayData = 4 * IntPtr.Size;
    private static readonly nint TableClass = Il2CppClassPointerStore<LuaTable>.NativeClassPtr;
    private static readonly nint StringClass = Il2CppClassPointerStore<LuaString>.NativeClassPtr;
    private static readonly nint NumberClass = Il2CppClassPointerStore<LuaNumber>.NativeClassPtr;
    private static readonly nint BooleanClass = Il2CppClassPointerStore<LuaBoolean>.NativeClassPtr;
    private static readonly nint NilClass = Il2CppClassPointerStore<LuaNil>.NativeClassPtr;
    private static readonly int TableList = Offset(TableClass, "list");
    private static readonly int TableDict = Offset(TableClass, "dict");
    private static readonly int StringText = Offset(StringClass, "<Text>k__BackingField");
    private static readonly int NumberValue = Offset(NumberClass, "<Number>k__BackingField");
    private static readonly int BooleanValue = Offset(BooleanClass, "<BoolValue>k__BackingField");
    private static readonly int ListItems = Offset(Il2CppClassPointerStore<NativeList>.NativeClassPtr, "_items");
    private static readonly int ListCount = Offset(Il2CppClassPointerStore<NativeList>.NativeClassPtr, "_size");
    private static readonly int ListVersion = Offset(Il2CppClassPointerStore<NativeList>.NativeClassPtr, "_version");
    private static readonly int DictEntries = Offset(Il2CppClassPointerStore<NativeDictionary>.NativeClassPtr, "entries");
    private static readonly int DictCount = Offset(Il2CppClassPointerStore<NativeDictionary>.NativeClassPtr, "count");
    private static readonly int DictFree = Offset(Il2CppClassPointerStore<NativeDictionary>.NativeClassPtr, "freeCount");
    private static readonly int DictVersion = Offset(Il2CppClassPointerStore<NativeDictionary>.NativeClassPtr, "version");
    private static readonly nint EntryClass = Il2CppClassPointerStore<NativeDictionary.Entry>.NativeClassPtr;
    private static readonly int EntryHash = Offset(EntryClass, "hashCode") - Header;
    private static readonly int EntryKey = Offset(EntryClass, "key") - Header;
    private static readonly int EntryValue = Offset(EntryClass, "value") - Header;
    private static readonly int EntrySize = GetEntrySize();
    private nint checkedEntryArrayClass;
    private readonly DialogueBlockCache cache = new();
    private readonly List<DialogueMemoryStamp> stamps = new(1024);
    private bool cachingConversation, watching;
    private nint warmingRoot;
    private int warmingIndex;
    private static bool cacheSupported;
    internal static bool CacheSupported => cacheSupported;

    internal void ResetWarmup() { warmingRoot = 0; warmingIndex = 0; }
    internal void ClearCache() { cache.Clear(); ResetWarmup(); }

    internal bool WarmConversation(LuaTable root, double budgetMs)
    {
        if (!cacheSupported) return true;
        if (warmingRoot != root.Pointer) { warmingRoot = root.Pointer; warmingIndex = 0; }
        nint list = *(nint*)(root.Pointer + TableList);
        int count = list == 0 ? 0 : *(int*)(list + ListCount);
        nint items = list == 0 ? 0 : *(nint*)(list + ListItems);
        CheckArray(items, count);
        nint dict = *(nint*)(root.Pointer + TableDict);
        int used = dict == 0 ? 0 : *(int*)(dict + DictCount);
        nint entries = dict == 0 ? 0 : *(nint*)(dict + DictEntries);
        CheckArray(entries, used);
        if (used > 0) CheckEntryArray(entries);
        long began = Stopwatch.GetTimestamp();
        using var scratch = new FlatDialogueSnapshot();
        while (warmingIndex < count + used)
        {
            int index = warmingIndex++;
            nint value;
            if (index < count) value = *(nint*)(items + ArrayData + index * IntPtr.Size);
            else
            {
                nint entry = entries + ArrayData + (index - count) * EntrySize;
                if (*(int*)(entry + EntryHash) < 0) continue;
                value = *(nint*)(entry + EntryValue);
            }
            if (value != 0 && *(nint*)value == TableClass)
            {
                scratch.Reset();
                ReadCached(value, scratch, 1);
            }
            if ((Stopwatch.GetTimestamp() - began) * 1000.0 / Stopwatch.Frequency >= budgetMs) return false;
        }
        GC.KeepAlive(root);
        return true;
    }
#if DISCO_DIAGNOSTICS
    private long values, tables, strings, characters, listSlots, dictionarySlots;
#endif

    internal static void WarmUp()
    {
        _ = EntrySize;
        var key = new LuaNumber(1);
        var first = new LuaNumber(2);
        var second = new LuaNumber(3);
        var dict = new NativeDictionary();
        dict[key] = first;
        int dictBefore = *(int*)(dict.Pointer + DictVersion);
        dict[key] = second;
        var list = new NativeList();
        list.Add(first);
        int listBefore = *(int*)(list.Pointer + ListVersion);
        list[0] = second;
        cacheSupported = dictBefore != *(int*)(dict.Pointer + DictVersion) &&
            listBefore != *(int*)(list.Pointer + ListVersion);
        GC.KeepAlive(dict);
        GC.KeepAlive(list);
    }

    private static int Offset(nint type, string name)
    {
        var field = IL2CPP.GetIl2CppField(type, name);
        if (field == IntPtr.Zero) throw new MissingFieldException(name);
        return checked((int)IL2CPP.il2cpp_field_get_offset(field));
    }

    private static int GetEntrySize()
    {
        uint alignment = 0;
        int size = IL2CPP.il2cpp_class_value_size(EntryClass, ref alignment);
        if (IntPtr.Size != 8 || !IL2CPP.il2cpp_class_is_valuetype(EntryClass) ||
            EntryHash < 0 || EntryKey < 0 || EntryValue < 0 || EntryValue + IntPtr.Size > size)
            throw new NotSupportedException("Unrecognized native dialogue dictionary layout.");
        return size;
    }

    internal void ReadFlat(LuaValue value, FlatDialogueSnapshot output, string root)
    {
#if DISCO_DIAGNOSTICS
        values = tables = strings = characters = listSlots = dictionarySlots = 0;
        int startSize = output.Size;
        long started = SaveDiagnostics.Measure();
#endif
        output.BeginRoot();
        cachingConversation = cacheSupported && root == "Conversation";
        if (cachingConversation) cache.Begin();
        try { ReadFlat(value.Pointer, output, 0); }
        finally { cachingConversation = false; }
        if (root == "Conversation") cache.End();
        GC.KeepAlive(value);
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.DialogueCopied(root, started, values, tables, strings, characters,
            listSlots, dictionarySlots, output.Size - startSize, output.Capacity);
        if (root == "Conversation") SaveDiagnostics.DialogueCache(cache.Hits, cache.Misses, cache.Checks);
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ReadFlat(nint value, FlatDialogueSnapshot output, int depth)
    {
        if (value == 0 || depth > 256) throw new InvalidDataException("Invalid/cyclic dialogue value.");
        if (cachingConversation && depth == 1 && *(nint*)value == TableClass)
        {
            ReadCached(value, output, depth);
            return;
        }
        ReadValue(value, output, depth);
    }

    private void ReadCached(nint value, FlatDialogueSnapshot output, int depth)
    {
        if (cache.Append(value, output)) return;
        int start = output.Size;
        stamps.Clear();
        watching = true;
        try { ReadValue(value, output, depth); }
        finally { watching = false; }
        cache.Store(value, Il2CppInterop.Runtime.Runtime.Il2CppObjectPool.Get<LuaTable>(value),
            output.CopyFrom(start), stamps.ToArray());
    }

    private void Watch(nint address, byte width)
    {
        if (watching) stamps.Add(DialogueMemoryStamp.Capture(address, width));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ReadValue(nint value, FlatDialogueSnapshot output, int depth)
    {
#if DISCO_DIAGNOSTICS
        values++;
#endif
        nint type = *(nint*)value;
        if (type == NilClass) { output.Byte((byte)'X'); return; }
        if (type == NumberClass) { Watch(value + NumberValue, 8); output.WriteNumber(*(double*)(value + NumberValue)); return; }
        if (type == BooleanClass) { Watch(value + BooleanValue, 1); output.WriteBoolean(*(byte*)(value + BooleanValue) != 0); return; }
        if (type == StringClass)
        {
            Watch(value + StringText, 8);
            nint text = *(nint*)(value + StringText);
            int size = text == 0 ? 0 : *(int*)(text + Header);
            if (size < 0 || size > 32_000_000) throw new InvalidDataException("Invalid dialogue string.");
#if DISCO_DIAGNOSTICS
            strings++;
            characters += size;
#endif
            output.WriteText(text == 0 ? ReadOnlySpan<char>.Empty : new ReadOnlySpan<char>((void*)(text + Header + sizeof(int)), size));
            return;
        }
        if (type != TableClass) throw new NotSupportedException("Dialogue value requires native serialization.");
#if DISCO_DIAGNOSTICS
        tables++;
#endif
        Watch(value + TableList, 8);
        Watch(value + TableDict, 8);
        nint list = *(nint*)(value + TableList);
        if (list != 0) Watch(list + ListVersion, 4);
        int count = list == 0 ? 0 : *(int*)(list + ListCount);
        nint items = list == 0 ? 0 : *(nint*)(list + ListItems);
        CheckArray(items, count);
#if DISCO_DIAGNOSTICS
        listSlots += count;
#endif
        nint dict = *(nint*)(value + TableDict);
        if (dict != 0) Watch(dict + DictVersion, 4);
        int used = dict == 0 ? 0 : *(int*)(dict + DictCount);
        int free = dict == 0 ? 0 : *(int*)(dict + DictFree);
        nint entries = dict == 0 ? 0 : *(nint*)(dict + DictEntries);
        CheckArray(entries, used);
#if DISCO_DIAGNOSTICS
        dictionarySlots += used;
#endif
        if (free < 0 || free > used) throw new InvalidDataException("Invalid dialogue dictionary count.");
        if (used > 0) CheckEntryArray(entries);
        if (count == 0) output.BeginDictionaryTable(used - free);
        else
        {
            output.BeginTable(count);
            for (int i = 0; i < count; i++) ReadFlat(*(nint*)(items + ArrayData + i * IntPtr.Size), output, depth + 1);
            output.Int(used - free);
        }
        int copied = 0;
        for (int i = 0; i < used; i++)
        {
            nint entry = entries + ArrayData + i * EntrySize;
            if (*(int*)(entry + EntryHash) < 0) continue;
            copied++;
            ReadFlat(*(nint*)(entry + EntryKey), output, depth + 1);
            ReadFlat(*(nint*)(entry + EntryValue), output, depth + 1);
        }
        if (copied != used - free) throw new InvalidDataException("Dialogue dictionary entry count mismatch.");
    }

    private void CheckEntryArray(nint entries)
    {
        nint type = *(nint*)entries;
        if (type == checkedEntryArrayClass) return;
        if (IL2CPP.il2cpp_class_get_element_class(type) != EntryClass)
            throw new NotSupportedException("Unrecognized dialogue dictionary entry type.");
        checkedEntryArrayClass = type;
    }

    private static void CheckArray(nint array, int count)
    {
        if (count < 0 || count > 2_000_000 ||
            (count > 0 && (array == 0 || (nuint)count > *(nuint*)(array + 3 * IntPtr.Size))))
            throw new InvalidDataException("Invalid native dialogue collection size.");
    }
}
