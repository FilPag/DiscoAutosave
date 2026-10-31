using BepInEx.Logging;
using Voidforge;
using Transition = FortressOccident.SceneTransitionManager._LoadSceneCoR_d__36;

namespace DiscoAutosave;

internal static class TransitionTimings
{
    private static readonly HashSet<nint> running = new();
    private static ManualLogSource log = null!;
    private static nint standalone;
    internal static bool InTransition => running.Count != 0;

    internal static void Install(ManualLogSource logger)
    {
        log = logger;
        log.LogInfo("Transition profiling uses existing hooks only; native method scanning and callback detours removed. Ready-list timings are aggregate, not per-handler.");
    }

    internal static void Enter(object? instance)
    {
        if (instance is not Transition transition) return;
        try
        {
            if (transition.__1__state != 0) return;
            if (!SaveDiagnostics.Active)
            {
                SaveDiagnostics.Begin(transition: true);
                standalone = transition.Pointer;
            }
            running.Add(transition.Pointer);
        }
        catch (Exception error) { log.LogWarning("Transition trace start unavailable: " + error.Message); }
    }

    internal static void Exit(object? instance, Exception? error)
    {
        if (instance is not Transition transition) return;
        try
        {
            if (error == null && transition.__1__state >= 0) return;
            running.Remove(transition.Pointer);
            if (standalone != transition.Pointer) return;
            standalone = 0;
            SaveDiagnostics.Complete(error != null);
        }
        catch (Exception failure) { log.LogWarning("Transition trace completion unavailable: " + failure.Message); }
    }

    internal static string Label(object? instance) => InTransition && instance is PrioritizedActionList list
        ? $" [ready-list=0x{list.Pointer:X}]" : "";
}
