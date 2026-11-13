using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// Builds OS UI from the Meta UI Set prefabs in <see cref="OSKit"/>, so every window and the menu share one look. Each
    /// control is an instance of a UI Set prefab (its animators give hover lift and press depress), is sized for fingers
    /// (at least 34 units = 22 mm at the OS unit, with 19 units = 12 mm between neighbours when a Row or Column spaces
    /// them), and plays the press sound. Sizes are OS units (1 unit = <see cref="OSKit.unitMeters"/>, 0.65 mm). Handlers run
    /// through UnityEngine.UI.Button.onClick, so the mouse, rays and pokes all work. Everything degrades to nothing, never
    /// an exception, when the kit isn't there.
    /// </summary>
    public static class OSUi
    {
        /// <summary>A finger-sized control is at least this many units each way (22 mm).</summary>
        public const float MinTarget = 34f;
        /// <summary>Minimum space between neighbours (12 mm).</summary>
        public const float MinGap = 19f;
        public const float ButtonHeight = 48f, ButtonMinWidth = 133f, BodyText = 16f, SmallText = 14f, TitleText = 22f;

        static readonly UnityAction PressAction = OSSounds.Press;
        static Sprite outlineSprite;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => outlineSprite = null;

        /// <summary>The sprite for a friendly icon name ("cube", "close", "send"), or null.</summary>
        public static Sprite Icon(string name)
        {
            OSKit kit = OSKit.Current;
            return kit != null ? kit.Icon(name) : null;
        }

        /// <summary>The content root of an OS window, in OS units. Made on first call.</summary>
        public static RectTransform Body(ForgePanel panel)
        {
            OSBody body = OSBody.Create(panel);
            return body != null ? (RectTransform)body.transform : null;
        }

        // ---------------------------------------------------------------- text

        /// <summary>A line of text in the theme's font and colour. Size in units: body 16 or more, secondary 14 or more, titles 20 to 24.</summary>
        public static TMP_Text Text(Transform parent, string text, float size, bool secondary = false)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            OSKit kit = OSKit.Current;
            if (kit != null && kit.theme != null)
            {
                tmp.font = kit.theme.textFontMedium != null ? kit.theme.textFontMedium : tmp.font;
                tmp.color = secondary ? kit.theme.textSecondaryColor : kit.theme.textPrimaryColor;
            }
            else
            {
                tmp.color = secondary ? new Color(1f, 1f, 1f, 0.7f) : new Color(1f, 1f, 1f, 0.9f);
            }
            if (secondary) go.tag = "QDSUITextSecondaryColor";
            tmp.text = text ?? "";
            tmp.fontSize = size;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.raycastTarget = false;
            var layout = go.AddComponent<LayoutElement>();
            layout.flexibleWidth = 1f;
            Touch(parent);
            return tmp;
        }

        // ---------------------------------------------------------------- buttons

        /// <summary>
        /// A UI Set button. <paramref name="label"/> and <paramref name="icon"/> (a kit icon name) are both optional. The
        /// handler is the Button's onClick. Wide enough for its label, 48 units (31 mm) tall.
        /// </summary>
        public static Button Button(Transform parent, string label, Action onClick, OSButtonStyle style = OSButtonStyle.Secondary, string icon = null)
        {
            OSKit kit = OSKit.Current;
            GameObject prefab = kit != null ? kit.ButtonPrefab(style) : null;
            if (prefab == null || parent == null) return null;
            GameObject go = UnityEngine.Object.Instantiate(prefab, parent, false);
            go.name = "Button " + (label ?? icon ?? "");
            var button = go.GetComponent<Button>();

            Transform elements = go.transform.Find("Content/Background/Elements");
            var labelText = elements != null ? elements.Find("Text/Label")?.GetComponent<TMP_Text>() : null;
            var subtitle = elements != null ? elements.Find("Text/Subtitle") : null;
            Transform iconTransform = elements != null ? elements.Find("Icon") : null;
            Transform gap = elements != null ? elements.Find("Gap") : null;
            if (subtitle != null) subtitle.gameObject.SetActive(false);

            bool hasLabel = !string.IsNullOrEmpty(label);
            Sprite sprite = Icon(icon);
            if (iconTransform != null)
            {
                iconTransform.gameObject.SetActive(sprite != null);
                if (sprite != null) iconTransform.GetComponent<Image>().sprite = sprite;
            }
            if (gap != null) gap.gameObject.SetActive(sprite != null && hasLabel);

            float width = ButtonHeight;
            if (labelText != null)
            {
                labelText.gameObject.SetActive(hasLabel);
                labelText.text = label ?? "";
                labelText.fontSize = BodyText;
                labelText.fontStyle = style == OSButtonStyle.Primary ? FontStyles.Bold : FontStyles.Normal;  // dark on white reads thin otherwise
                labelText.textWrappingMode = TextWrappingModes.NoWrap;
                labelText.overflowMode = TextOverflowModes.Overflow;
                if (hasLabel)
                {
                    float labelWidth = labelText.GetPreferredValues(label).x;
                    width = labelWidth + 32f + (sprite != null ? 32f : 0f);
                    // The label's own box would otherwise stay at the prefab's 40 units wide.
                    // The Text box and its label sit in layout groups that don't size them, so give them the label's size.
                    float fit = Mathf.Ceil(labelWidth) + 2f;
                    ((RectTransform)labelText.transform).sizeDelta = new Vector2(fit, 22f);
                    ((RectTransform)labelText.transform.parent).sizeDelta = new Vector2(fit, 30f);
                }
            }
            if (hasLabel) width = Mathf.Max(hasLabel && sprite == null ? 96f : ButtonMinWidth * 0.7f, width);
            Size(go.transform, width, ButtonHeight);

            if (button != null)
            {
                button.onClick.AddListener(PressAction);
                if (onClick != null) button.onClick.AddListener(() => onClick());
            }
            Touch(parent);
            return button;
        }

        /// <summary>A shelf-style tab toggle (icon over label, lit when on). Selecting it calls <paramref name="onSelected"/>.</summary>
        public static Toggle Tab(Transform parent, string label, string icon, ToggleGroup group, Action onSelected, float width = 150f, float height = 64f)
        {
            OSKit kit = OSKit.Current;
            if (kit == null || kit.tabToggle == null || parent == null) return null;
            GameObject go = UnityEngine.Object.Instantiate(kit.tabToggle, parent, false);
            go.name = "Tab " + label;
            var toggle = go.GetComponent<Toggle>();
            Transform elements = go.transform.Find("Content/Background/Elements");
            if (elements != null)
            {
                // The shelf toggle ships with its main label switched off and a small italic one on; use the main one.
                var title = elements.Find("Label")?.GetComponent<TMP_Text>();
                if (title != null)
                {
                    title.gameObject.SetActive(true);
                    title.text = label;
                    title.fontSize = BodyText;
                    title.fontStyle = FontStyles.Bold;  // the lit tab's dark label reads thin otherwise
                    title.textWrappingMode = TextWrappingModes.NoWrap;
                    title.overflowMode = TextOverflowModes.Overflow;
                    title.alignment = TextAlignmentOptions.Center;
                    var c = title.color;
                    title.color = new Color(c.r, c.g, c.b, 1f);  // the lit tab's dark label should be solid
                    ((RectTransform)title.transform).sizeDelta = new Vector2(width - 16f, 22f);
                }
                var sub = elements.Find("Label (1)");
                if (sub != null) sub.gameObject.SetActive(false);
                var iconImage = elements.Find("Icon")?.GetComponent<Image>();
                Sprite sprite = Icon(icon);
                if (iconImage != null)
                {
                    iconImage.sprite = sprite;
                    iconImage.gameObject.SetActive(sprite != null);
                }
            }
            Size(go.transform, width, height);
            if (toggle != null)
            {
                go.AddComponent<OSToggleSync>();
                toggle.group = group;
                toggle.onValueChanged.AddListener(on =>
                {
                    if (!on) return;
                    OSSounds.Press();
                    onSelected?.Invoke();
                });
            }
            Touch(parent);
            return toggle;
        }

        /// <summary>One grid tile: a UI Set text tile, optionally in the "new" outline style (see <see cref="OSMenu"/>).</summary>
        public static OSTileView Tile(Transform parent, bool outline, float width, float height)
        {
            OSKit kit = OSKit.Current;
            if (kit == null || kit.textTile == null || parent == null) return null;
            GameObject go = UnityEngine.Object.Instantiate(kit.textTile, parent, false);
            go.name = outline ? "Tile (outline)" : "Tile";
            var tile = go.AddComponent<OSTileView>();
            tile.rect = (RectTransform)go.transform;
            tile.button = go.GetComponent<Button>();
            Transform elements = go.transform.Find("Content/Background/Elements");
            if (elements != null)
            {
                tile.icon = elements.Find("Icon")?.GetComponent<Image>();
                tile.title = elements.Find("Label")?.GetComponent<TMP_Text>();
                tile.status = elements.Find("Label (1)")?.GetComponent<TMP_Text>();
                float textWidth = width - 24f;  // the tile's own 12-unit padding each side
                if (tile.title != null)
                {
                    tile.title.fontSize = 18f;
                    tile.title.textWrappingMode = TextWrappingModes.NoWrap;
                    tile.title.overflowMode = TextOverflowModes.Ellipsis;
                    ((RectTransform)tile.title.transform).sizeDelta = new Vector2(textWidth, 26f);
                }
                if (tile.status != null)
                {
                    tile.status.fontSize = SmallText;
                    tile.status.textWrappingMode = TextWrappingModes.NoWrap;
                    tile.status.overflowMode = TextOverflowModes.Ellipsis;
                    ((RectTransform)tile.status.transform).sizeDelta = new Vector2(textWidth, 20f);
                }
                var space = elements.Find("Space");
                if (space != null) ((RectTransform)space).sizeDelta = new Vector2(10f, 8f);
            }
            Size(go.transform, width, height);
            if (outline)
            {
                // The "new" style: a tile that is only a line until you reach for it. The Borderless animators leave the plate clear.
                go.tag = "QDSUIBorderlessButton";
                var background = go.transform.Find("Content/Background");
                if (background != null) background.tag = "QDSUIBorderlessButton";
                var ring = new GameObject("Outline", typeof(RectTransform));
                ring.transform.SetParent(go.transform, false);
                var ringRect = (RectTransform)ring.transform;
                ringRect.anchorMin = Vector2.zero;
                ringRect.anchorMax = Vector2.one;
                ringRect.offsetMin = ringRect.offsetMax = Vector2.zero;
                ring.AddComponent<LayoutElement>().ignoreLayout = true;
                tile.outline = ring.AddComponent<Image>();
                tile.outline.sprite = OutlineSprite();
                tile.outline.type = Image.Type.Sliced;
                tile.outline.color = new Color(1f, 1f, 1f, 0.42f);
                tile.outline.raycastTarget = false;
            }
            if (tile.button != null) tile.button.onClick.AddListener(PressAction);
            Touch(parent);
            return tile;
        }

        // ---------------------------------------------------------------- toggle, slider

        /// <summary>Sets a toggle made by <see cref="Tab"/> or <see cref="Toggle"/> from code, without calling its handler, and lights it properly.</summary>
        public static void Select(Toggle toggle, bool on)
        {
            if (toggle == null) return;
            toggle.SetIsOnWithoutNotify(on);
            var sync = toggle.GetComponent<OSToggleSync>();
            if (sync != null) sync.Apply();
        }

        /// <summary>A labelled UI Set switch. The whole row is tappable, so the target is far bigger than the 22 mm minimum.</summary>
        public static Toggle Toggle(Transform parent, string label, bool value, Action<bool> onChanged)
        {
            OSKit kit = OSKit.Current;
            if (kit == null || kit.switchToggle == null || parent == null) return null;
            RectTransform row = Row(parent, 44f, 16f);
            row.name = "Toggle " + label;
            var rowImage = row.gameObject.AddComponent<Image>();  // invisible plate: makes the label tappable too
            rowImage.color = Color.clear;
            var rowButton = row.gameObject.AddComponent<Button>();
            rowButton.transition = Selectable.Transition.None;
            Text(row, label, BodyText);

            var holder = new GameObject("Switch", typeof(RectTransform));
            holder.transform.SetParent(row, false);
            var holderLayout = holder.AddComponent<LayoutElement>();
            holderLayout.minWidth = holderLayout.preferredWidth = 60f;
            holderLayout.minHeight = holderLayout.preferredHeight = 38f;
            holderLayout.flexibleWidth = 0f;
            GameObject sw = UnityEngine.Object.Instantiate(kit.switchToggle, holder.transform, false);
            var swRect = (RectTransform)sw.transform;
            swRect.anchorMin = swRect.anchorMax = swRect.pivot = new Vector2(0.5f, 0.5f);
            swRect.anchoredPosition = Vector2.zero;
            swRect.localScale = Vector3.one * 1.45f;  // 40 x 24 units is too small a target; 58 x 35 units is 38 x 23 mm
            var toggle = sw.GetComponent<Toggle>();
            if (toggle == null) return null;
            sw.AddComponent<OSToggleSync>();
            toggle.SetIsOnWithoutNotify(value);
            toggle.onValueChanged.AddListener(on =>
            {
                OSSounds.Press();
                onChanged?.Invoke(on);
            });
            rowButton.onClick.AddListener(() => toggle.isOn = !toggle.isOn);
            Touch(parent);
            return toggle;
        }

        /// <summary>A labelled UI Set slider (the large one, 40 units = 26 mm tall) with its value shown at the right.</summary>
        public static Slider Slider(Transform parent, string label, float min, float max, float value, Action<float> onChanged)
        {
            OSKit kit = OSKit.Current;
            if (kit == null || kit.slider == null || parent == null) return null;
            RectTransform column = Column(parent, 8f);
            column.name = "Slider " + label;
            column.anchorMin = column.anchorMax = new Vector2(0f, 1f);
            column.pivot = new Vector2(0f, 1f);
            column.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            RectTransform line = Row(column, 28f, 12f);
            Text(line, label, BodyText);
            TMP_Text readout = Text(line, "", BodyText, true);
            readout.alignment = TextAlignmentOptions.Right;
            readout.GetComponent<LayoutElement>().flexibleWidth = 0f;
            readout.GetComponent<LayoutElement>().minWidth = 64f;

            GameObject go = UnityEngine.Object.Instantiate(kit.slider, column, false);
            var layout = go.GetComponent<LayoutElement>();
            if (layout == null) layout = go.AddComponent<LayoutElement>();
            layout.minHeight = layout.preferredHeight = 40f;
            layout.minWidth = 160f;
            layout.flexibleWidth = 1f;
            var slider = go.GetComponent<Slider>();
            if (slider == null) return null;
            slider.minValue = min;
            slider.maxValue = max;
            slider.SetValueWithoutNotify(Mathf.Clamp(value, min, max));
            readout.text = slider.value.ToString("0.##");
            slider.onValueChanged.AddListener(v =>
            {
                readout.text = v.ToString("0.##");
                onChanged?.Invoke(v);
            });
            Touch(parent);
            return slider;
        }

        // ---------------------------------------------------------------- layout

        /// <summary>A horizontal strip. Children keep the sizes the controls give them; the row spaces them.</summary>
        public static RectTransform Row(Transform parent, float height, float spacing = 12f)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = Mathf.Max(spacing, 0f);
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            var element = go.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = height;
            element.flexibleWidth = 1f;
            return (RectTransform)go.transform;
        }

        /// <summary>A vertical stack that fills its parent, top to bottom.</summary>
        public static RectTransform Column(Transform parent, float spacing = 12f)
        {
            var go = new GameObject("Column", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.spacing = Mathf.Max(spacing, 0f);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            return rect;
        }

        /// <summary>A heading and a column under it; returns the column to put the section's controls in.</summary>
        public static RectTransform Section(Transform parent, string heading)
        {
            RectTransform outer = Column(parent, 12f);
            outer.name = "Section " + heading;
            outer.anchorMin = outer.anchorMax = new Vector2(0f, 1f);
            outer.pivot = new Vector2(0f, 1f);
            outer.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var fit = outer.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            TMP_Text title = Text(outer, heading, TitleText);
            title.fontStyle = FontStyles.Bold;
            RectTransform inner = Column(outer, 12f);
            inner.anchorMin = inner.anchorMax = new Vector2(0f, 1f);
            inner.pivot = new Vector2(0f, 1f);
            inner.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            inner.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return inner;
        }

        /// <summary>Destroys everything under a transform (for rebuilding a window's content).</summary>
        public static void Clear(Transform parent)
        {
            if (parent == null) return;
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                Transform child = parent.GetChild(i);
                child.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(child.gameObject);
            }
        }

        // ---------------------------------------------------------------- confirm

        /// <summary>
        /// An inline two-button confirm laid over the given window (never a system dialog): the question, then the confirm
        /// button and Cancel. Nothing happens until the confirm tap; Cancel or the dimmed area around it dismisses it.
        /// </summary>
        public static void Confirm(Transform window, string question, string confirmLabel, Action onConfirm)
        {
            OSKit kit = OSKit.Current;
            if (window == null || kit == null) return;
            OSBody body = window.GetComponentInChildren<OSBody>(true);
            Transform host = body != null ? body.transform : window;
            Transform old = host.Find("OSConfirm");
            if (old != null) UnityEngine.Object.Destroy(old.gameObject);

            var root = new GameObject("OSConfirm", typeof(RectTransform));
            root.transform.SetParent(host, false);
            var rootRect = (RectTransform)root.transform;
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = rootRect.offsetMax = Vector2.zero;
            root.AddComponent<LayoutElement>().ignoreLayout = true;
            var dim = root.AddComponent<Image>();
            dim.color = new Color(0f, 0f, 0f, 0.55f);
            var dimButton = root.AddComponent<Button>();
            dimButton.transition = Selectable.Transition.None;
            dimButton.onClick.AddListener(() => UnityEngine.Object.Destroy(root));

            var card = new GameObject("Card", typeof(RectTransform));
            card.transform.SetParent(root.transform, false);
            var cardRect = (RectTransform)card.transform;
            cardRect.anchorMin = cardRect.anchorMax = cardRect.pivot = new Vector2(0.5f, 0.5f);
            cardRect.sizeDelta = new Vector2(420f, 200f);
            card.tag = "QDSUIBackplateGradient";
            var cardImage = card.AddComponent<Image>();
            cardImage.color = kit.theme != null ? kit.theme.backplateColor : new Color(0.13f, 0.13f, 0.15f, 1f);
            if (kit.theme != null && kit.theme.backplateGradientMaterial != null) cardImage.material = kit.theme.backplateGradientMaterial;
            card.AddComponent<Oculus.Interaction.Samples.RoundedBoxUIProperties>().borderRadius = new Vector4(24f, 24f, 24f, 24f);
            var cardLayout = card.AddComponent<VerticalLayoutGroup>();
            cardLayout.padding = new RectOffset(28, 28, 28, 28);
            cardLayout.spacing = 22f;
            cardLayout.childControlWidth = cardLayout.childControlHeight = true;
            cardLayout.childForceExpandWidth = false;
            cardLayout.childForceExpandHeight = false;
            var fit = card.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            TMP_Text text = Text(card.transform, question, TitleText - 2f);
            text.alignment = TextAlignmentOptions.Center;
            RectTransform buttons = Row(card.transform, ButtonHeight, 20f);
            buttons.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
            Button(buttons, confirmLabel, () =>
            {
                UnityEngine.Object.Destroy(root);
                onConfirm?.Invoke();
            }, OSButtonStyle.Primary);
            Button(buttons, "Cancel", () => UnityEngine.Object.Destroy(root), OSButtonStyle.Secondary);
            Touch(host);
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>Gives a control its size, both as the rect and as the layout hint a Row or Column reads.</summary>
        public static void Size(Transform t, float width, float height)
        {
            var rect = (RectTransform)t;
            rect.sizeDelta = new Vector2(width, height);
            var layout = t.GetComponent<LayoutElement>();
            if (layout == null) layout = t.gameObject.AddComponent<LayoutElement>();
            layout.minWidth = layout.preferredWidth = width;
            layout.minHeight = layout.preferredHeight = height;
            layout.flexibleWidth = 0f;
        }

        // A window body re-applies the theme once after controls were added.
        static void Touch(Transform parent)
        {
            if (parent == null) return;
            OSBody body = parent.GetComponentInParent<OSBody>();
            if (body != null) body.MarkThemeDirty();
        }

        /// <summary>A hollow rounded rectangle for the "new" tile: one small procedural texture, made once.</summary>
        public static Sprite OutlineSprite()
        {
            if (outlineSprite != null) return outlineSprite;
            const int size = 64, radius = 22;
            const float line = 2.4f;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "OSOutline", hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Signed distance to a rounded square of half-size `half`, corner radius `radius`.
                    float qx = Mathf.Abs(x + 0.5f - half) - (half - radius);
                    float qy = Mathf.Abs(y + 0.5f - half) - (half - radius);
                    float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                    float alpha = Mathf.Clamp01(0.5f - Mathf.Abs(outside + line * 0.5f) + line * 0.5f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            outlineSprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
            outlineSprite.name = "OSOutline";
            outlineSprite.hideFlags = HideFlags.HideAndDontSave;
            return outlineSprite;
        }
    }

    /// <summary>Which UI Set button a <see cref="OSUi.Button"/> is.</summary>
    public enum OSButtonStyle { Primary, Secondary, Borderless, Destructive }
}
