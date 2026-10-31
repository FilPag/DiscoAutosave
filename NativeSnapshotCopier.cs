#if SNAPSHOT_CLR_TEST
using NativeObject = System.Object;
using NativeType = System.Type;
using NativeField = System.Reflection.FieldInfo;
using NativeFlags = System.Reflection.BindingFlags;
using ObjectKey = System.Object;
using TypeKey = System.Type;
#else
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using NativeObject = Il2CppSystem.Object;
using NativeType = Il2CppSystem.Type;
using NativeField = Il2CppSystem.Reflection.FieldInfo;
using NativeFlags = Il2CppSystem.Reflection.BindingFlags;
using ObjectKey = System.IntPtr;
using TypeKey = System.IntPtr;
#endif

namespace DiscoAutosave;

internal sealed class NativeSnapshotCopier
{
    private sealed record Layout(NativeType Type, bool Immutable, bool Delegate, bool Array, bool CopyElements, NativeField[] Fields);
    private static readonly Dictionary<TypeKey, Layout> layouts = new();
#if SNAPSHOT_CLR_TEST
    private readonly Dictionary<ObjectKey, NativeObject> copies = new(ReferenceEqualityComparer.Instance);
#else
    private readonly Dictionary<ObjectKey, NativeObject> copies = new();
#endif

    internal NativeObject Copy(NativeObject value)
    {
        try { return Copy(value, 0)!; }
        finally { copies.Clear(); }
    }

    private NativeObject? Copy(NativeObject? value, int depth)
    {
        if (value == null) return null;
        if (depth > 128 || copies.Count > 100_000) throw new NotSupportedException("JSON snapshot graph exceeds limits.");
#if SNAPSHOT_CLR_TEST
        var key = value;
        var type = value.GetType();
#else
        var key = value.Pointer;
        var type = IL2CPP.il2cpp_object_get_class(value.Pointer);
#endif
        var layout = GetLayout(type);
        if (layout.Immutable) return value;
        // FullSerializer excludes delegates; event listeners are not save state.
        if (layout.Delegate) return null;
        if (copies.TryGetValue(key, out var previous)) return previous;
#if SNAPSHOT_CLR_TEST
        var copy = typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(value, null)!;
#else
        var copy = value.MemberwiseClone();
#endif
        copies.Add(key, copy);
        if (layout.Array)
        {
            if (layout.CopyElements)
            {
#if SNAPSHOT_CLR_TEST
                var source = (Array)value;
                var target = (Array)copy;
#else
                var source = value.Cast<Il2CppSystem.Array>();
                var target = copy.Cast<Il2CppSystem.Array>();
#endif
                if (source.Rank != 1 || source.GetLowerBound(0) != 0)
                    throw new NotSupportedException("JSON snapshot requires zero-based, one-dimensional arrays.");
                for (int i = 0; i < source.Length; i++) target.SetValue(Copy(source.GetValue(i), depth + 1), i);
            }
        }
        else
        {
            foreach (var field in layout.Fields)
                field.SetValue(copy, Copy(field.GetValue(value), depth + 1));
        }
        return copy;
    }

    private static Layout GetLayout(TypeKey type)
    {
        if (layouts.TryGetValue(type, out var layout)) return layout;
#if SNAPSHOT_CLR_TEST
        var nativeType = type;
        bool immutable = type.IsPrimitive || type.IsEnum || type == typeof(string) || typeof(Type).IsAssignableFrom(type);
        bool isDelegate = typeof(Delegate).IsAssignableFrom(type);
        if (type.IsPointer || type.IsByRef || type == typeof(IntPtr) || type == typeof(UIntPtr))
            throw new NotSupportedException("Native handle in JSON data: " + type.FullName);
#else
        var nativeType = NativeType.internal_from_handle(IL2CPP.il2cpp_class_get_type(type));
        bool immutable = nativeType.IsPrimitive || nativeType.IsEnum ||
            type == Il2CppClassPointerStore<Il2CppSystem.String>.NativeClassPtr ||
            IL2CPP.il2cpp_class_is_assignable_from(Il2CppClassPointerStore<NativeType>.NativeClassPtr, type);
        bool isDelegate = IL2CPP.il2cpp_class_is_assignable_from(Il2CppClassPointerStore<Il2CppSystem.Delegate>.NativeClassPtr, type);
        if (IL2CPP.il2cpp_class_is_assignable_from(Il2CppClassPointerStore<UnityEngine.Object>.NativeClassPtr, type) ||
            nativeType.IsPointer || nativeType.IsByRef || nativeType.FullName is "System.IntPtr" or "System.UIntPtr")
            throw new NotSupportedException("Live engine/native handle in JSON data: " + nativeType.FullName);
#endif
        var fields = new List<NativeField>();
        if (!immutable && !isDelegate && !nativeType.IsArray)
        {
            for (var current = nativeType; current != null; current = current.BaseType)
                foreach (var field in current.GetFields(NativeFlags.Instance | NativeFlags.Public | NativeFlags.NonPublic | NativeFlags.DeclaredOnly))
                    if (NeedsCopy(field.FieldType)) fields.Add(field);
        }
        layout = new(nativeType, immutable, isDelegate, nativeType.IsArray,
            nativeType.IsArray && NeedsCopy(nativeType.GetElementType()), fields.ToArray());
        layouts.Add(type, layout);
        return layout;
    }

    private static bool NeedsCopy(NativeType type)
    {
        if (type.IsPointer || type.IsByRef || type.FullName is "System.IntPtr" or "System.UIntPtr")
            throw new NotSupportedException("Native handle in JSON data: " + type.FullName);
        if (type.IsPrimitive || type.IsEnum || type.FullName == "System.String") return false;
        if (!type.IsValueType) return true;
        foreach (var field in type.GetFields(NativeFlags.Instance | NativeFlags.Public | NativeFlags.NonPublic))
            if (NeedsCopy(field.FieldType)) return true;
        return false;
    }
}
