using System.Diagnostics;
using BepInEx.Configuration;
using BepInEx.Logging;
using FortressOccident;
using HarmonyLib;
using Transition = FortressOccident.SceneTransitionManager._LoadSceneCoR_d__36;

namespace DiscoAutosave;

internal static class LoadingScreenGc
{
    private static ConfigEntry<bool> enabled = null!;
    private static ManualLogSource log = null!;
    private static nint collected;
    [ThreadStatic] private static bool skipTail;

    internal static void Install(Harmony harmony, ConfigEntry<bool> setting, ManualLogSource logger)
    {
        enabled = setting;
        log = logger;
        harmony.Patch(AccessTools.Method(typeof(Transition), "MoveNext"),
            prefix: new HarmonyMethod(typeof(LoadingScreenGc), nameof(Enter)),
            postfix: new HarmonyMethod(typeof(LoadingScreenGc), nameof(BeforeReveal)),
            finalizer: new HarmonyMethod(typeof(LoadingScreenGc), nameof(Exit)));
        harmony.Patch(AccessTools.Method(typeof(Il2CppSystem.GC), "Collect", Type.EmptyTypes),
            prefix: new HarmonyMethod(typeof(LoadingScreenGc), nameof(AllowCollection)));
    }

    private static void Enter(Transition __instance, out bool __state)
    {
        __state = skipTail;
        skipTail = __instance.__1__state == 10 && collected == __instance.Pointer;
        if (skipTail || __instance.__1__state == 0) Forget(__instance);
    }

    private static void BeforeReveal(Transition __instance, bool __result)
    {
        // State 10 yields the hide coroutine; Unity has not advanced its fade yet.
        if (!enabled.Value || !__result || __instance.__1__state != 10) return;
        try
        {
            var hide = __instance.__2__current?.TryCast<SceneTransitionManager._ShowLoadingScreenCoR_d__37>();
            if (hide == null || hide.show || hide.__1__state != 0 ||
                hide.__4__this.Pointer != __instance.__4__this.Pointer)
            {
                log.LogWarning("Loading-screen GC: native tail retained; unexpected hide coroutine.");
                return;
            }
            long started = Stopwatch.GetTimestamp();
            Il2CppSystem.GC.Collect();
            collected = __instance.Pointer;
            BuildLog.Info(log, $"Loading-screen GC: collected before reveal in {(Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency:F2} ms; transition={__instance.Pointer}.");
        }
        catch (Exception error)
        {
            log.LogWarning("Loading-screen GC unavailable; native tail collection retained: " + error.Message);
        }
    }

    private static bool AllowCollection()
    {
        if (!skipTail) return true;
        skipTail = false;
        BuildLog.Info(log, "Loading-screen GC: corresponding transition tail collection suppressed.");
        return false;
    }

    private static void Forget(Transition __instance)
    {
        if (collected == __instance.Pointer) collected = 0;
    }

    private static Exception? Exit(Transition __instance, bool __state, Exception? __exception)
    {
        skipTail = __state;
        if (__exception != null) Forget(__instance);
        return __exception;
    }
}
