using NotificationSystem;
using UnityEngine;
using UnityEngine.UI;

namespace DiscoAutosave;

internal static class AutosaveIndicator
{
    private static GameObject? root;
    private static Image? icon;
    private static float hideAt;
    private static bool visible;

    internal static void Show()
    {
        var settings = NotificationManager._settingsDict;
        if (settings == null || !settings.TryGetValue(NotificationType.AutosaveComplete, out var notification)) return;
        var sprite = notification.MainSprite;
        if (!sprite) return;
        if (!root)
        {
            root = new GameObject("DiscoAutosaveIndicator");
            UnityEngine.Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 800);
            scaler.matchWidthOrHeight = 1f;
            var child = new GameObject("Icon");
            child.AddComponent<RectTransform>();
            child.transform.SetParent(root.transform, false);
            icon = child.AddComponent<Image>();
            icon.raycastTarget = false;
            icon.preserveAspect = true;
            var rect = icon.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 1);
            rect.anchoredPosition = new Vector2(-16, -16);
            rect.sizeDelta = new Vector2(32, 32);
        }
        icon!.sprite = sprite;
        root!.SetActive(true);
        hideAt = Time.unscaledTime + 2f;
        visible = true;
    }

    internal static void Tick()
    {
        if (visible && Time.unscaledTime >= hideAt) Hide();
    }

    internal static void Hide()
    {
        if (root) root!.SetActive(false);
        visible = false;
    }
}
