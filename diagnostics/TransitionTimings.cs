using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using FortressOccident;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Voidforge;

namespace DiscoAutosave;

internal static class TransitionTimings
{
    private static readonly Dictionary<nint, int> addressCounts = new();
    private static readonly HashSet<nint> inspected = new();
    private static readonly Dictionary<nint, (nint Values, int Version)> lists = new();
    private static Harmony harmony = null!;
    private static ManualLogSource log = null!;
    private static nint transition, standalone;
    internal static bool InTransition => transition != 0;

    internal static void Install(Harmony patcher, ManualLogSource logger)
    {
        harmony = patcher;
        log = logger;
        try
        {
            InventoryNativeMethods();
            foreach (string name in new[] { "OnMapChanged", "PlatformTweakLightShadows", "ShowLoadingScreen", "HideLoadingScreen" })
                HookUnique(AccessTools.DeclaredMethod(typeof(SceneTransitionManager), name));
            foreach (var type in typeof(SceneTransitionManager).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (type.Name.Contains("ShowLoadingScreenCoR") || type.Name.Contains("UnloadScenesCoR"))
                    HookUnique(AccessTools.DeclaredMethod(type, "MoveNext"));
            foreach (string name in new[] { "Enable", "Disable" })
                HookUnique(AccessTools.DeclaredMethod(typeof(SceneTransitionManager.LiveAgent), name));
            foreach (string name in new[] { "UpdateAssets", "UpdateRuntimes", "InternalUpdate", "UpdatePageTables",
                "UpdateState", "CreatePageTableDescTexture", "CheckPageTableDescTexture", "InitializeCollections", "InternalStart" })
                HookUnique(AccessTools.DeclaredMethod(typeof(AmplifyTextureManager), name));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(PrioritizedActionList), "Invoke"),
                prefix: new HarmonyMethod(typeof(TransitionTimings), nameof(DiscoverHandlers)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(SceneTransitionManager._LoadSceneCoR_d__36), "MoveNext"),
                prefix: new HarmonyMethod(typeof(TransitionTimings), nameof(BeginTransition)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(TransitionTimings), nameof(EndTransition)));
            log.LogInfo("Transition child timings enabled; only native-unique methods are hooked. Callback lists and dispatch are unchanged.");
        }
        catch (Exception error) { log.LogWarning("Additional transition timings unavailable: " + error); }
    }

    private static unsafe void InventoryNativeMethods()
    {
        uint count = 0;
        var assemblies = IL2CPP.il2cpp_domain_get_assemblies(IL2CPP.il2cpp_domain_get(), ref count);
        for (int i = 0; i < count; i++)
        {
            var image = IL2CPP.il2cpp_assembly_get_image(assemblies[i]);
            uint classes = IL2CPP.il2cpp_image_get_class_count(image);
            for (uint j = 0; j < classes; j++)
            {
                var type = IL2CPP.il2cpp_image_get_class(image, j);
                nint cursor = 0, method;
                while ((method = IL2CPP.il2cpp_class_get_methods(type, ref cursor)) != 0)
                {
                    nint address = Marshal.ReadIntPtr(method);
                    if (address != 0) addressCounts[address] = addressCounts.GetValueOrDefault(address) + 1;
                }
            }
        }
    }

    private static void HookUnique(MethodInfo? method, nint expected = 0)
    {
        if (method == null || method.ContainsGenericParameters || method.DeclaringType!.ContainsGenericParameters) return;
        var type = IL2CPP.il2cpp_class_from_system_type(Il2CppType.From(method.DeclaringType).Pointer);
        var matches = AccessTools.GetDeclaredMethods(method.DeclaringType)
            .Count(candidate => candidate.Name == method.Name && candidate.GetParameters().Length == method.GetParameters().Length);
        if (matches != 1) return;
        nint native = IL2CPP.il2cpp_class_get_method_from_name(type, method.Name, method.GetParameters().Length);
        if (native == 0 || (expected != 0 && expected != native)) return;
        nint address = Marshal.ReadIntPtr(native);
        if (addressCounts.GetValueOrDefault(address) != 1)
        {
            log.LogInfo("Transition timing skipped shared native method: " + method.DeclaringType.FullName + "." + method.Name);
            return;
        }
        SaveDiagnostics.Hook(harmony, method, transitionOnly: true);
    }

    private static void DiscoverHandlers(PrioritizedActionList __instance)
    {
        if (!SaveDiagnostics.Active || !InTransition) return;
        long began = SaveDiagnostics.Measure();
        try
        {
            var values = __instance._values;
            if (values == null) return;
            var stamp = (values.Pointer, values._version);
            if (lists.TryGetValue(__instance.Pointer, out var previous) && previous == stamp) return;
            lists[__instance.Pointer] = stamp;
            for (int i = 0; i < values.Count; i++)
            {
                var action = values[i];
                if (action == null) continue;
                foreach (var callback in action.GetInvocationList())
                {
                    nint native = callback.method;
                    if (native == 0 || !inspected.Add(native)) continue;
                    var info = callback.Method;
                    var type = typeof(SunshinePersistence).Assembly.GetType(info.DeclaringType.FullName);
                    if (type == null) continue;
                    var candidates = AccessTools.GetDeclaredMethods(type).Where(method =>
                        method.Name == info.Name && method.GetParameters().Length == info.GetParameters().Length).ToArray();
                    if (candidates.Length == 1) HookUnique(candidates[0], native);
                }
            }
        }
        catch (Exception error) { log.LogWarning("Transition handler timing discovery skipped: " + error.Message); }
        finally { SaveDiagnostics.Measured("transition-handler-discovery", began); }
    }

    internal static void Reset() => lists.Clear();

    private static void BeginTransition(SceneTransitionManager._LoadSceneCoR_d__36 __instance)
    {
        if (__instance.__1__state != 0) return;
        transition = __instance.Pointer;
        if (SaveDiagnostics.Active) return;
        SaveDiagnostics.Begin(transition: true);
        standalone = transition;
    }

    private static Exception? EndTransition(SceneTransitionManager._LoadSceneCoR_d__36 __instance,
        bool __result, Exception? __exception)
    {
        if (__result && __exception == null) return __exception;
        if (transition == __instance.Pointer) transition = 0;
        if (standalone == __instance.Pointer)
        {
            standalone = 0;
            SaveDiagnostics.Complete(__exception != null);
        }
        return __exception;
    }
}
