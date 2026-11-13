using System.Reflection;
using Oculus.Interaction;
using UnityEngine;

namespace Forge.OS
{
    /// <summary>
    /// The content root of an OS window: a child of the panel's Content scaled to the OS unit (see <see cref="OSKit.unitMeters"/>)
    /// so everything built under it by <see cref="OSUi"/> is in OS units. It keeps its size in step with the panel when the
    /// window is resized, and applies the Albie theme once after controls were added (the UI Set only themes at Start).
    /// Made by <see cref="OSUi.Body"/>.
    /// </summary>
    public sealed class OSBody : MonoBehaviour
    {
        public const string ObjectName = "OSBody";

        RectTransform rect, parentRect;
        UIThemeManager manager;
        Vector2 lastParentSize = new Vector2(-1f, -1f);
        float scale = 0.65f;
        bool themeDirty;

        /// <summary>Makes (or finds) the body under a panel's Content. Null if the panel has no Content.</summary>
        public static OSBody Create(ForgePanel panel)
        {
            if (panel == null || panel.Content == null) return null;
            Transform existing = panel.Content.Find(ObjectName);
            if (existing != null) return existing.GetComponent<OSBody>();

            OSKit kit = OSKit.Current;
            float unit = kit != null ? kit.unitMeters : 0.00065f;

            // The prefab's placeholder text isn't ours.
            Transform placeholder = panel.Content.Find("Placeholder");
            if (placeholder != null) placeholder.gameObject.SetActive(false);

            var go = new GameObject(ObjectName, typeof(RectTransform));
            var body = go.AddComponent<OSBody>();
            body.rect = (RectTransform)go.transform;
            body.rect.SetParent(panel.Content, false);
            body.parentRect = panel.Content;
            body.scale = unit / ForgePanel.MetersPerUnit;
            body.rect.anchorMin = body.rect.anchorMax = body.rect.pivot = new Vector2(0f, 1f);
            body.rect.anchoredPosition = Vector2.zero;
            body.rect.localScale = Vector3.one * body.scale;
            body.Fit();

            EnsureCanvas(panel.GetComponent<Canvas>());
            if (panel.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null) panel.gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            if (kit != null && kit.theme != null)
            {
                body.manager = go.AddComponent<UIThemeManager>();
                SetThemes(body.manager, kit.theme);
            }
            return body;
        }

        /// <summary>The UI Set's rounded boxes need these vertex channels on the canvas.</summary>
        public static void EnsureCanvas(Canvas canvas)
        {
            if (canvas == null) return;
            canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.Normal | AdditionalCanvasShaderChannels.Tangent;
        }

        /// <summary>Points a theme manager at one theme (its fields are private).</summary>
        public static void SetThemes(UIThemeManager manager, UITheme theme)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(UIThemeManager).GetField("_themes", flags)?.SetValue(manager, new[] { theme });
            typeof(UIThemeManager).GetField("_currentThemeIndex", flags)?.SetValue(manager, 0);
        }

        /// <summary>Controls were added: theme them once, at the end of this frame.</summary>
        public void MarkThemeDirty() => themeDirty = true;

        void Fit()
        {
            lastParentSize = parentRect.rect.size;
            rect.sizeDelta = lastParentSize / scale;
        }

        void LateUpdate()
        {
            if (parentRect != null && parentRect.rect.size != lastParentSize) Fit();
            if (themeDirty)
            {
                themeDirty = false;
                if (manager != null) manager.ApplyCurrentTheme();
            }
        }
    }
}
