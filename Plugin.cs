using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using FortressOccident;
using HarmonyLib;
using Il2CppInterop.Runtime;
using PixelCrushers.DialogueSystem;
using Sunshine;
using Sunshine.Views;
using UnityEngine;
using Voidforge;
using NotificationSystem;

namespace DiscoAutosave;

#if DISCO_DIAGNOSTICS
[BepInPlugin("local.disco.autosave", "Disco Autosave", "1.6.9-debug.1")]
#else
[BepInPlugin("local.disco.autosave", "Disco Autosave", "1.6.9")]
#endif
public sealed class Plugin : BasePlugin
{
    internal static Plugin Instance = null!;
    private ConfigEntry<bool> enabled = null!;
    private ConfigEntry<int> minutes = null!;
    private ConfigEntry<bool> afterEntry = null!;
    private ConfigEntry<bool> afterExit = null!;
    private ConfigEntry<bool> afterOutdoor = null!;
    private readonly SaveSchedule schedule = new();
    private bool menuVisible;
    private float previousTimeScale;
    private float startedAt;
    private bool inFlight;
    private bool stopped;
    private bool warned;
    private GameObject? cornerMenu;
    private GameObject? selectHint;
    private Il2CppSystem.Action? callback;
    private ThreadedSave threaded = null!;
    private ConfigEntry<bool> smoothCamera = null!;
    private ConfigEntry<int> fpsLimit = null!;
    private ConfigEntry<bool> vSync = null!;
    private volatile bool displaySettingsPending = true;
    private SmoothPartyMotion smoothMotion = null!;
    private string fpsInput = "0";
    internal bool OwnsAutosave => enabled.Value && !stopped;
    internal bool CanOptimizeQuicksave => !stopped && !inFlight && SafeForSnapshot();

    internal void SaveFailed(Exception error)
    {
        stopped = true;
        Log.LogError("Optimized save verification failed; stopped until restart: " + error);
    }

    public override void Load()
    {
        Instance = this;
        enabled = Config.Bind("Autosave", "Enabled", true);
        minutes = Config.Bind("Autosave", "IntervalMinutes", 5,
            new ConfigDescription("Minutes of gameplay, including dialogue, between autosaves. Saving waits until dialogue ends.",
                new AcceptableValueRange<int>(1, 30)));
        afterEntry = Config.Bind("Autosave", "AfterBuildingEntry", true);
        afterExit = Config.Bind("Autosave", "AfterBuildingExit", true);
        afterOutdoor = Config.Bind("Autosave", "AfterOutdoorAreaChange", true);
        var harmony = new Harmony("local.disco.autosave");
        SaveSceneObjects.Install(harmony, Log);
        smoothCamera = Config.Bind("Camera", "SmoothPartyMotion", true,
            "Use per-render-frame animation/root motion for the player party during exploration. Native animation timing is restored for dialogue/scripted movement.");
        fpsLimit = Config.Bind("Display", "FpsLimit", 0,
            new ConfigDescription("FPS cap while VSync is off. 0 = no cap. External/driver caps can still limit FPS.",
                new AcceptableValueRange<int>(0, int.MaxValue)));
        vSync = Config.Bind("Display", "VSync", true, "Apply VSync on/off at startup and when these display settings change; does not continuously override the game.");
        fpsLimit.SettingChanged += DisplaySettingsChanged;
        vSync.SettingChanged += DisplaySettingsChanged;
        fpsInput = fpsLimit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        smoothMotion = new SmoothPartyMotion(harmony, smoothCamera, Log);
        harmony.Patch(AccessTools.Method(typeof(NotificationManager), "ShowNotification",
                new[] { typeof(NotificationType), typeof(string) }),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(QuietAutosave)));
        var deferGc = Config.Bind("Autosave", "DeferForcedGarbageCollection", true,
            "Skip the forced collection/finalizer wait at the end of this plugin's background autosaves. Normal automatic GC remains enabled.");
        threaded = new ThreadedSave(harmony, Log, deferGc);
        LoadingScreenGc.Install(harmony, Config.Bind("Loading", "CollectBeforeReveal", true,
            "Move the scene-transition forced GC before the loading-screen hide coroutine starts. Other collections remain unchanged."), Log);
        FlatDialogueSnapshot.WarmUp();
        LuaSnapshotReader.WarmUp();
        BuildLog.Info(Log, "Dialogue block cache: native collection overwrite-version checks " +
            (LuaSnapshotReader.CacheSupported ? "passed; enabled." : "failed; full traversal retained."));
#if DISCO_DIAGNOSTICS
        NativeSaveDump.Install(Config.Bind("Diagnostics", "NativeComparisonSaves", 0,
            new ConfigDescription("Number of saves per session with same-frame native comparison dumps. Causes extra hitches and writes full save data under BepInEx/DiscoAutosave-comparisons. 0 disables.",
                new AcceptableValueRange<int>(0, 20))), Log);
        SaveDiagnostics.Install(harmony, Log);
#endif
        foreach (var method in new[] { "Start", "ShowHub", "ShowMenuButton" })
            harmony.Patch(AccessTools.Method(typeof(HudMainMenuButtonTrigger), method),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(HideMainMenuCorner)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(MainMenuView), "Start"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(HideSelectHint)));
        harmony.Patch(AccessTools.Method(typeof(ApplicationManager), "ChangeArea"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeChangeArea)));
        foreach (var target in new[] {
            AccessTools.Method(typeof(SunshinePersistence), "Load"),
            AccessTools.Method(typeof(World), "NewGameCoR"),
            AccessTools.Method(typeof(World), "LoadLobby"),
            AccessTools.Method(typeof(ApplicationManager), "LoadLobby") })
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(Plugin), nameof(ResetSession)));
        AddComponent<Driver>();
        BuildLog.Info(Log, "Autosave loaded. F8: settings. Top-left main-menu hover control hidden; Esc still opens the menu.");
    }

    private static bool QuietAutosave(NotificationType notificationType) => notificationType != NotificationType.AutosaveComplete;

    private bool SafeForSnapshot(bool countDialogue = false)
    {
        var persistence = SingletonComponent<SunshinePersistence>.Singleton;
        var world = SingletonComponent<World>.Singleton;
        var app = SingletonScriptable<ApplicationManager>.Singleton;
        var party = Party.Player;
        var player = party ? party.Main : null;
        bool dialogueActive = DialogueManager.hasInstance && DialogueManager.isConversationActive;
        return !menuVisible && persistence && !persistence.IsLoading &&
            !SunshinePersistence.LoadingInitialized && !World.IsLoadingLevel &&
            world && world._isRunning && app && app.IsGameArea && player != null && player &&
            SceneTransitionManager.IsReady && !SceneTransitionManager.IsQuitting && AreaManager.isReady &&
            Time.timeScale > 0f &&
            ((countDialogue && dialogueActive) ||
             ((int)ViewsPagesBridge.Current == 2 && !dialogueActive &&
              !player.IsDestinationSetBySequence && !player.movementCommandLock));
    }

    private static void HideMainMenuCorner(HudMainMenuButtonTrigger __instance)
    {
        var button = __instance.menuButton;
        if (!button) return;
        var container = button.transform.parent;
        if (!container) return;
        Instance.cornerMenu = container.gameObject;
        Instance.HideCornerAfterAnimations();
    }

    internal void HideCornerAfterAnimations()
    {
        if (cornerMenu && cornerMenu!.activeSelf) cornerMenu.SetActive(false);
        if (selectHint && selectHint!.activeSelf) selectHint.SetActive(false);
    }

    private static void HideSelectHint(MainMenuView __instance)
    {
        var prompt = __instance.selectOverlayPrompt;
        if (!prompt) return;
        Instance.selectHint = prompt.gameObject;
        Instance.selectHint.SetActive(false);
    }

    private static void ResetSession()
    {
        Instance.schedule.Reset();
        AutosaveIndicator.Hide();
        Instance.smoothMotion.Restore();
        Instance.threaded.InvalidateCapture();
        Instance.threaded.ClearDialogueCache();
    }

    private static void BeforeChangeArea(ApplicationManager __instance, string areaId, bool isGameLoad)
    {
        var plugin = Instance;
        plugin.threaded.InvalidateCapture();
        if (isGameLoad)
        {
            plugin.schedule.Reset();
            return;
        }
        plugin.schedule.CancelTravel();
        if (!plugin.enabled.Value || !__instance.IsGameArea) return;
        var source = __instance.CurrentSceneProperties;
        if (source == null || source.IsDreamScene || source.SceneId == areaId) return;
        foreach (var target in __instance.ScenePropertiesList)
        {
            if (target.SceneId != areaId || target.IsDreamScene) continue;
            bool trigger = (plugin.afterEntry.Value && !target.IsOutside) ||
                           (plugin.afterExit.Value && !source.IsOutside) ||
                           (plugin.afterOutdoor.Value && source.IsOutside && target.IsOutside);
            if (trigger) plugin.schedule.Travel(areaId, Time.realtimeSinceStartup);
            return;
        }
    }

    private void ToggleMenu()
    {
        menuVisible = !menuVisible;
        if (menuVisible)
        {
            previousTimeScale = Time.timeScale;
            Time.timeScale = 0;
            fpsInput = fpsLimit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else Time.timeScale = previousTimeScale;
    }

    internal void DrawMenu()
    {
        if (!menuVisible) return;
        float scale = Mathf.Min(1.6f, (Screen.width - 20f) / 440f, (Screen.height - 20f) / 560f);
        var previousMatrix = GUI.matrix;
        try
        {
            GUI.matrix = previousMatrix * Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            DrawMenuContents(scale);
        }
        finally
        {
            GUI.matrix = previousMatrix;
        }
    }

    private void DrawMenuContents(float scale)
    {
        float x = (Screen.width / scale - 440) / 2f;
        float y = (Screen.height / scale - 560) / 2f;
        var panel = new Rect(x, y, 440, 560);
        var previousColor = GUI.color;
        try
        {
            GUI.color = new Color(0.06f, 0.06f, 0.07f, 0.97f);
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
        }
        finally { GUI.color = previousColor; }
        GUI.Box(panel, "Disco Autosave — Settings");
        GUI.Label(new Rect(x + 20, y + 48, 400, 28), "AUTOSAVE");
        enabled.Value = GUI.Toggle(new Rect(x + 20, y + 80, 400, 28), enabled.Value, "Enable autosave");
        GUI.Label(new Rect(x + 20, y + 118, 260, 28), $"Every {minutes.Value} minute(s) of gameplay");
        if (GUI.Button(new Rect(x + 310, y + 116, 45, 30), "−")) minutes.Value = Math.Max(1, minutes.Value - 1);
        if (GUI.Button(new Rect(x + 365, y + 116, 45, 30), "+")) minutes.Value = Math.Min(30, minutes.Value + 1);
        afterEntry.Value = GUI.Toggle(new Rect(x + 20, y + 156, 400, 28), afterEntry.Value, "After entering a building");
        afterExit.Value = GUI.Toggle(new Rect(x + 20, y + 190, 400, 28), afterExit.Value, "After leaving a building");
        afterOutdoor.Value = GUI.Toggle(new Rect(x + 20, y + 224, 400, 28), afterOutdoor.Value, "After changing outdoor areas");
        GUI.Label(new Rect(x + 20, y + 270, 400, 28), "DISPLAY & CAMERA");
        bool previousSmoothing = smoothCamera.Value;
        smoothCamera.Value = GUI.Toggle(new Rect(x + 20, y + 300, 400, 28), smoothCamera.Value, "Smooth camera / party motion");
        if (previousSmoothing && !smoothCamera.Value) smoothMotion.Restore();
        vSync.Value = GUI.Toggle(new Rect(x + 20, y + 334, 400, 28), vSync.Value, "VSync");
        GUI.Label(new Rect(x + 20, y + 376, 220, 28), "FPS cap (0 = unlimited)");
        bool controlsEnabled = GUI.enabled;
        try
        {
            GUI.enabled = controlsEnabled && QualitySettings.vSyncCount == 0;
            fpsInput = GUI.TextField(new Rect(x + 260, y + 374, 95, 30), fpsInput, 10);
            bool valid = int.TryParse(fpsInput, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int value) && value >= 0;
            GUI.enabled = GUI.enabled && valid;
            if (GUI.Button(new Rect(x + 365, y + 374, 45, 30), "Set"))
                fpsLimit.Value = value;
        }
        finally { GUI.enabled = controlsEnabled; }
        GUI.Label(new Rect(x + 20, y + 412, 400, 28), QualitySettings.vSyncCount > 0
            ? "Turn VSync off to use the FPS cap."
            : $"Configured cap: {(fpsLimit.Value == 0 ? "unlimited" : fpsLimit.Value.ToString())}. External caps still apply.");
        GUI.Label(new Rect(x + 20, y + 460, 400, 28), stopped ? "Autosave stopped — see log; restart game." : "Game paused · Settings saved automatically");
        if (GUI.Button(new Rect(x + 130, y + 504, 180, 32), "Close (F8 / Esc)")) ToggleMenu();
    }

    private void DisplaySettingsChanged(object? sender, EventArgs args) => displaySettingsPending = true;

    private void ApplyDisplaySettings()
    {
        int syncInterval = vSync.Value ? 1 : 0;
        if (QualitySettings.vSyncCount != syncInterval) QualitySettings.vSyncCount = syncInterval;
        if (QualitySettings.vSyncCount != 0) return;
        int target = fpsLimit.Value == 0 ? -1 : fpsLimit.Value;
        if (Application.targetFrameRate != target) Application.targetFrameRate = target;
    }

    internal void Tick()
    {
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.Tick();
#endif
        if (displaySettingsPending)
        {
            displaySettingsPending = false;
            ApplyDisplaySettings();
        }
        try { AutosaveIndicator.SetSaving(inFlight && !stopped); }
        catch (Exception error) { Log.LogWarning("Autosave indicator unavailable: " + error.Message); }
        if (Input.GetKeyDown(KeyCode.F8) || (menuVisible && Input.GetKeyDown(KeyCode.Escape))) ToggleMenu();
        try { threaded.Poll(threaded.ReadyToAdvance && SafeForSnapshot()); }
        catch (Exception error)
        {
            threaded.AbortPreparation();
            inFlight = false;
            stopped = true;
            Log.LogError($"Background autosave stopped: {error}");
        }
        if (menuVisible || stopped || !enabled.Value || threaded.QuickSaveInProgress)
            return;
        try
        {
            if (inFlight)
            {
                if (Time.realtimeSinceStartup - startedAt > 120f)
                {
                    stopped = true;
                    Log.LogError("Autosave callback timed out; stopped until game restart to prevent overlapping saves.");
                }
                return;
            }

            schedule.Advance(Time.unscaledDeltaTime, SafeForSnapshot(countDialogue: true));
            if (!SafeForSnapshot()) return;

            var persistence = SingletonComponent<SunshinePersistence>.Singleton;
            var app = SingletonScriptable<ApplicationManager>.Singleton;
            if (!schedule.Due(minutes.Value, app.CurrentSceneProperties?.SceneId ?? "", Time.realtimeSinceStartup, true) ||
                SunshinePersistence.saveCoroutine != null ||
                !SunshinePersistence.CanSave()) return;
            if (SaveSceneObjects.HasPlayingSequence()) return;

            callback ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((System.Action)AutosaveCompleted);
            if (callback == null) throw new InvalidOperationException("Cannot create autosave callback.");
            startedAt = Time.realtimeSinceStartup;
            inFlight = true;
            try { AutosaveIndicator.SetSaving(true); }
            catch (Exception error) { Log.LogWarning("Autosave indicator unavailable: " + error.Message); }
            BuildLog.Info(Log, "Requesting native autosave.");
            threaded.Begin(persistence, callback);
        }
        catch (Exception error)
        {
            if (threaded.Preparing) threaded.AbortPreparation();
            stopped = true;
            if (!warned)
            {
                warned = true;
                Log.LogError($"Autosave stopped until game restart: {error}");
            }
        }
    }

    private void AutosaveCompleted()
    {
        inFlight = false;
        AutosaveIndicator.Hide();
        try
        {
            threaded.CheckWrittenSave();
            schedule.Reset();
            BuildLog.Info(Log, "Autosave written.");
        }
        catch (Exception error)
        {
            stopped = true;
            Log.LogError($"Autosave verification failed; stopped until restart: {error}");
        }
    }
}

public sealed class Driver : MonoBehaviour
{
    public Driver(IntPtr pointer) : base(pointer) { }

#if DISCO_DIAGNOSTICS
    private void Update()
    {
        long start = SaveDiagnostics.Measure();
        try { Plugin.Instance.Tick(); }
        finally { SaveDiagnostics.Measured("mod-update", start); }
    }
    private void OnGUI()
    {
        long start = SaveDiagnostics.Measure();
        try { Plugin.Instance.DrawMenu(); }
        finally { SaveDiagnostics.Measured("mod-gui", start); }
    }
    private void LateUpdate()
    {
        long start = SaveDiagnostics.Measure();
        try { Plugin.Instance.HideCornerAfterAnimations(); }
        finally { SaveDiagnostics.Measured("mod-late-update", start); }
    }
#else
    private void Update() => Plugin.Instance.Tick();
    private void OnGUI() => Plugin.Instance.DrawMenu();
    private void LateUpdate() => Plugin.Instance.HideCornerAfterAnimations();
#endif
}
