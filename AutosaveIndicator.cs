using NotificationSystem;
using UnityEngine;
using UnityEngine.UI;

namespace DiscoAutosave;

internal static class AutosaveIndicator
{
    private static GameObject? root;
    private static Image? icon;
    private static bool saving;

    internal static void SetSaving(bool value)
    {
        if (saving == value) return;
        saving = value;
        if (value) Show();
        else Hide();
    }

    private static void Show()
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
            scaler.referenceResolution = new Vector2(2560, 1440);
            scaler.matchWidthOrHeight = 1f;
            var child = new GameObject("Icon");
            child.AddComponent<RectTransform>();
            child.transform.SetParent(root.transform, false);
            icon = child.AddComponent<Image>();
            icon.raycastTarget = false;
            icon.preserveAspect = true;
            var rect = icon.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 1);
            rect.anchoredPosition = new Vector2(-10, 75);
            rect.sizeDelta = new Vector2(150, 150);

            var caption = new GameObject("Caption");
            caption.AddComponent<RectTransform>();
            caption.transform.SetParent(root.transform, false);
            var text = caption.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.text = "Autosaving…";
            text.fontSize = 20;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false;
            var labelRect = text.rectTransform;
            labelRect.anchorMin = labelRect.anchorMax = labelRect.pivot = new Vector2(1, 1);
            labelRect.anchoredPosition = new Vector2(-10, -75);
            labelRect.sizeDelta = new Vector2(150, 32);
            var shadow = caption.AddComponent<Shadow>();
            shadow.effectColor = new Color(0, 0, 0, 0.8f);
            shadow.effectDistance = new Vector2(1, -1);
        }
        icon!.sprite = sprite;
        root!.SetActive(true);
    }

    internal static void Hide()
    {
        if (root) root!.SetActive(false);
        saving = false;
    }
}
