using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using PixelCrushers.DialogueSystem;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using NativeObject = UnityEngine.Object;

namespace DiscoAutosave;

internal static class SaveSceneObjects
{
    private static readonly OrderedObjectIndex<GameObject> receivers = new();
    private static readonly OrderedObjectIndex<Sequencer> sequencers = new();
    private static readonly Dictionary<nint, bool> receiverClasses = new();
    private static ManualLogSource log = null!;
    private static UnityAction<Scene, LoadSceneMode>? sceneLoaded;
    private static bool installed, ready;

    internal static void Install(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        try
        {
            foreach (string method in new[] { "Internal_CloneSingle", "Internal_CloneSingleWithParent",
                "Internal_InstantiateSingle_Injected", "Internal_InstantiateSingleWithParent_Injected" })
                harmony.Patch(AccessTools.Method(typeof(NativeObject), method),
                    postfix: new HarmonyMethod(typeof(SaveSceneObjects), nameof(Cloned)));
            foreach (string method in new[] { "Internal_AddComponentWithType", "AddComponentInternal" })
                harmony.Patch(AccessTools.Method(typeof(GameObject), method),
                    postfix: new HarmonyMethod(typeof(SaveSceneObjects), nameof(Added)));
            sceneLoaded = (UnityAction<Scene, LoadSceneMode>)Loaded;
            SceneManager.add_sceneLoaded(sceneLoaded);
            installed = true;
        }
        catch (Exception error) { Failed(error); }
    }

    internal static void Prepare()
    {
        if (!installed || ready) return;
        try
        {
            foreach (var component in NativeObject.FindObjectsOfType<MonoBehaviour>(true)) Track(component);
            ready = true;
        }
        catch (Exception error) { Failed(error); }
    }

    private static void Loaded(Scene scene, LoadSceneMode mode)
    {
        if (!installed) return;
        if (!ready) { Prepare(); return; }
        try { foreach (var root in scene.GetRootGameObjects()) TrackTree(root); }
        catch (Exception error) { Failed(error); }
    }

    private static void Cloned(NativeObject __result)
    {
        if (!ready || !__result) return;
        try
        {
            var root = __result.TryCast<GameObject>() ?? __result.TryCast<Component>()?.gameObject;
            if (root) TrackTree(root!);
        }
        catch (Exception error) { Failed(error); }
    }

    private static void Added(Component __result)
    {
        if (!ready || !__result) return;
        try
        {
            // Include components Unity added implicitly through RequireComponent.
            foreach (var component in __result.gameObject.GetComponents<MonoBehaviour>()) Track(component);
        }
        catch (Exception error) { Failed(error); }
    }

    private static void TrackTree(GameObject root)
    {
        foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true)) Track(component);
    }

    private static void Track(MonoBehaviour? component)
    {
        if (!component || (component!.hideFlags & HideFlags.DontSave) != 0) return;
        var target = component.gameObject;
        if ((target.hideFlags & HideFlags.DontSave) != 0) return;
        var sequence = component.TryCast<Sequencer>();
        if (sequence) sequencers.Add(sequence!.GetInstanceID(), sequence);
        if (!Receives(IL2CPP.il2cpp_object_get_class(component.Pointer))) return;
        receivers.Add(target.GetInstanceID(), target);
    }

    private static bool Receives(nint type)
    {
        if (receiverClasses.TryGetValue(type, out bool found)) return found;
        for (nint parent = type; parent != 0; parent = IL2CPP.il2cpp_class_get_parent(parent))
            if (IL2CPP.il2cpp_class_get_method_from_name(parent, "OnRecordPersistentData", -1) != IntPtr.Zero)
                return receiverClasses[type] = true;
        return receiverClasses[type] = false;
    }

    internal static GameObject[] ReceiverSnapshot()
    {
        if (!ready) return DiscoverReceivers();
        return receivers.Snapshot(static target => target);
    }

    private static GameObject[] DiscoverReceivers()
    {
        var targets = new SortedDictionary<int, GameObject>();
        foreach (var component in NativeObject.FindObjectsOfType<MonoBehaviour>())
            if (component && Receives(IL2CPP.il2cpp_object_get_class(component.Pointer)))
                targets[component.gameObject.GetInstanceID()] = component.gameObject;
        return targets.Values.ToArray();
    }

    internal static bool HasPlayingSequence()
    {
        var values = ready ? sequencers.Snapshot(static sequence => sequence) : NativeObject.FindObjectsOfType<Sequencer>().ToArray();
        foreach (var sequence in values)
            if (sequence && sequence.gameObject.activeInHierarchy && sequence.isPlaying) return true;
        return false;
    }

    private static void Failed(Exception error)
    {
        installed = ready = false;
        log.LogWarning("Scene-object tracking unavailable; native save safety/discovery scans retained: " + error.Message);
    }
}
