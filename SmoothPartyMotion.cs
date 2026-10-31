using BepInEx.Configuration;
using BepInEx.Logging;
using FortressOccident;
using HarmonyLib;
using PixelCrushers.DialogueSystem;
using Sunshine.Views;
using Sunshine;
using UnityEngine;

namespace DiscoAutosave;

internal sealed class SmoothPartyMotion
{
    private static SmoothPartyMotion current = null!;
    private readonly ConfigEntry<bool> enabled;
    private readonly ManualLogSource log;
    private readonly Dictionary<IntPtr, (Animator Animator, AnimatorUpdateMode Mode)> originals = new();

    internal SmoothPartyMotion(Harmony harmony, ConfigEntry<bool> enabled, ManualLogSource log)
    {
        this.enabled = enabled;
        this.log = log;
        current = this;
        harmony.Patch(AccessTools.Method(typeof(CharacterAnimator), "FixedUpdate"),
            prefix: new HarmonyMethod(typeof(SmoothPartyMotion), nameof(BeforeAnimatorUpdate)));
    }

    private static void BeforeAnimatorUpdate(CharacterAnimator __instance)
    {
        Character character = __instance.character;
        Animator animator = __instance.animator;
        if (!character || !animator) return;
        bool smooth = current.enabled.Value && Party.IsInParty(character) &&
            SceneTransitionManager.IsReady && !SceneTransitionManager.IsQuitting && !World.IsLoadingLevel &&
            (int)ViewsPagesBridge.Current == 2 &&
            !(DialogueManager.hasInstance && DialogueManager.isConversationActive) &&
            !character.IsDestinationSetBySequence && !character.movementCommandLock;
        if (smooth)
        {
            if (animator.updateMode == AnimatorUpdateMode.AnimatePhysics)
            {
                if (!current.originals.ContainsKey(animator.Pointer))
                {
                    current.originals[animator.Pointer] = (animator, animator.updateMode);
                    BuildLog.Info(current.log, $"Smooth camera motion: {character.name} animation/root motion now updates per rendered frame.");
                }
                animator.updateMode = AnimatorUpdateMode.Normal;
            }
        }
        else if (current.originals.Remove(animator.Pointer, out var original))
            animator.updateMode = original.Mode;
    }

    internal void Restore()
    {
        foreach (var original in originals.Values)
            if (original.Animator) original.Animator.updateMode = original.Mode;
        originals.Clear();
    }
}
