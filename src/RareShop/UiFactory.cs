using System;
using UnityEngine;
using UnityEngine.UI;

namespace NightRaider.RareShop
{
    /// <summary>
    /// Small helpers for building uGUI hierarchies at runtime.
    /// </summary>
    internal static class UiFactory
    {
        private static Font _cachedFont;

        /// <summary>
        /// Finds a font that can render the game's display names. The mod is built
        /// from code, so it cannot reference a serialized font asset; instead it
        /// borrows whatever font the running game already uses, which is guaranteed
        /// to have the CJK glyphs the item names need.
        /// </summary>
        internal static Font FindGameFont()
        {
            if (_cachedFont != null)
            {
                return _cachedFont;
            }
            _cachedFont = GameRefs.FindUiFont();
            if (_cachedFont == null)
            {
                RareShopPlugin.Log.LogWarning("No game font found; text may not render.");
            }
            return _cachedFont;
        }

        internal static GameObject CreateCanvasRoot(string name, Transform parent)
        {
            GameObject go = new GameObject(name);
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }
            Canvas canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 32000;

            // A nested canvas must own a GraphicRaycaster: the GraphicRegistry keys
            // graphics to their nearest Canvas, so the parent canvas' raycaster cannot
            // see these graphics.
            go.AddComponent<GraphicRaycaster>();
            return go;
        }

        /// <summary>
        /// Copies the game canvas' scaling setup so the mod's UI matches its sizes.
        /// </summary>
        internal static void MatchScaler(GameObject go, Canvas gameCanvas)
        {
            CanvasScaler scaler = go.GetComponent<CanvasScaler>();
            if (scaler == null)
            {
                scaler = go.AddComponent<CanvasScaler>();
            }
            CanvasScaler source = gameCanvas != null ? gameCanvas.GetComponent<CanvasScaler>() : null;
            if (source != null)
            {
                scaler.uiScaleMode = source.uiScaleMode;
                scaler.referenceResolution = source.referenceResolution;
                scaler.screenMatchMode = source.screenMatchMode;
                scaler.matchWidthOrHeight = source.matchWidthOrHeight;
                scaler.referencePixelsPerUnit = source.referencePixelsPerUnit;
            }
            else
            {
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;
            }
        }

        internal static GameObject Panel(string name, Transform parent, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rect = go.AddComponent<RectTransform>();
            rect.localScale = Vector3.one;
            Image img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = true;
            return go;
        }

        internal static Text Label(string name, Transform parent, string text, Font font, int size, TextAnchor anchor, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rect = go.AddComponent<RectTransform>();
            rect.localScale = Vector3.one;
            Text t = go.AddComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.text = text;
            t.alignment = anchor;
            t.color = color;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.supportRichText = true;
            return t;
        }

        internal static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>Stretch with insets: left, bottom, right, top.</summary>
        internal static void AnchorStretch(RectTransform rect, float left, float bottom, float right, float top)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        internal static void AnchorCenter(RectTransform rect, float width, float height)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(width, height);
        }

        internal static void AnchorTop(RectTransform rect, float width, float height, float topOffset)
        {
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -topOffset);
            rect.sizeDelta = new Vector2(width, height);
        }
    }
}
