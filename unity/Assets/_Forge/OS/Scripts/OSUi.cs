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
        // Text sizes in units. Cap height is 0.446 mm per unit at the OS unit of 0.65 mm (Inter's cap line is 0.686 of its em, measured by
        // OSLookShots' text audit), so at 0.5 m the rubric's 9 to 10 mm cap height is 20.2 to 22.4 units. Body text 22 units is 14.3 mm em and
        // 9.8 mm cap height; secondary text (hints, empty states, notes) 21.5 is 9.6 mm; the one-line caption under a tile title 20 is 8.9 mm
        // (a caption stays below its title so the title leads); section headings 26 are 11.6 mm. OSUi.Text lifts anything smaller to the first two.
        public const float ButtonHeight = 48f, ButtonMinWidth = 133f, BodyText = 22f, SmallText = 21.5f, CaptionText = 20f, TitleText = 26f;

        /// <summary>The name a tab's title is given, so the UI Set's label animation (which finds it as "Label") no longer drives it.</summary>
        public const string TabTitleName = "Title";

        static readonly UnityAction PressAction = OSSounds.Press;
        static Sprite outlineSprite;
        static Oculus.Interaction.UITheme theme, themeSource;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            outlineSprite = null;
            theme = themeSource = null;
        }

        /// <summary>
        /// Draw OS text in Inter (Core's font, the one every Forge panel uses) instead of the kit theme's font (TextMeshPro's
        /// default sans). One font across the menu, the OS windows and the older panels. Glyphs Inter's atlas lacks fall back
        /// to TextMeshPro's default font.
        /// </summary>
        public const bool UseInter = true;

        /// <summary>
        /// The theme every OS surface is themed with: a runtime copy of the kit's Albie theme with the design system's font and
        /// text colours (never saved; the kit asset is untouched). Null without the kit.
        /// </summary>
        public static Oculus.Interaction.UITheme Theme
        {
            get
            {
                OSKit kit = OSKit.Current;
                if (kit == null || kit.theme == null) return null;
                if (theme != null && themeSource == kit.theme) return theme;
                themeSource = kit.theme;
                theme = ScriptableObject.Instantiate(kit.theme);
                theme.name = kit.theme.name + " (design system)";
                theme.hideFlags = HideFlags.DontSave;
                TMP_FontAsset inter = UseInter ? ForgeTheme.Font : null;
                if (inter != null) theme.textFontBold = theme.textFontMedium = theme.textFontRegular = inter;
                theme.textPrimaryColor = ForgeTheme.TextPrimary;
                theme.textSecondaryColor = ForgeTheme.TextSecondary;
                return theme;
            }
        }

        /// <summary>The OS text font (the design system's), or null without the kit.</summary>
        public static TMP_FontAsset Font
        {
            get
            {
                Oculus.Interaction.UITheme t = Theme;
                return t != null ? t.textFontMedium : null;
            }
        }

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

        /// <summary>A line of text in the theme's font and colour. Size in units; anything under <see cref="BodyText"/> (or <see cref="SmallText"/> when secondary) is lifted to it, so a window cannot ask for smaller text.</summary>
        public static TMP_Text Text(Transform parent, string text, float size, bool secondary = false)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            TMP_FontAsset font = Font;
            if (font != null) tmp.font = font;
            else if (ForgeTheme.Font != null) tmp.font = ForgeTheme.Font;
            tmp.color = secondary ? ForgeTheme.TextSecondary : ForgeTheme.TextPrimary;
            if (secondary) go.tag = "QDSUITextSecondaryColor";
            tmp.text = text ?? "";
            tmp.fontSize = Mathf.Max(size, secondary ? SmallText : BodyText);
            tmp.lineSpacing = ForgeTheme.LineSpacing;  // generous leading for anything that wraps
            if (tmp.fontSize >= TitleText) tmp.characterSpacing = ForgeTheme.HeadingTracking;
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
                if (Font != null) labelText.font = Font;  // before the heavier face below, which is per font
                labelText.fontSize = BodyText;
                labelText.fontStyle = style == OSButtonStyle.Primary ? FontStyles.Bold : FontStyles.Normal;  // dark on white reads thin otherwise
                ForgeTheme.DarkOnLight(labelText, style == OSButtonStyle.Primary);  // and so does the stroke weight alone: a heavier face on top
                labelText.textWrappingMode = TextWrappingModes.NoWrap;
                labelText.overflowMode = TextOverflowModes.Overflow;
                if (hasLabel)
                {
                    float labelWidth = labelText.GetPreferredValues(label).x;
                    width = labelWidth + ButtonPadding * 2f + (sprite != null ? 32f : 0f);
                    // The label's own box would otherwise stay at the prefab's 40 units wide.
                    // The Text box and its label sit in layout groups that don't size them, so give them the label's size.
                    float fit = Mathf.Ceil(labelWidth) + 2f;
                    ((RectTransform)labelText.transform).sizeDelta = new Vector2(fit, 30f);
                    ((RectTransform)labelText.transform.parent).sizeDelta = new Vector2(fit, 34f);
                }
            }
            if (hasLabel) width = Mathf.Max(hasLabel && sprite == null ? 96f : ButtonMinWidth * 0.7f, width);
            Size(go.transform, width, ButtonHeight);

            if (button != null)
            {
                button.onClick.AddListener(PressAction);
                if (onClick != null) button.onClick.AddListener(() => onClick());
            }
#if UNITY_EDITOR
            if (style == OSButtonStyle.Primary) WarnSecondPrimary(go.transform);
#endif
            Touch(parent);
            return button;
        }

        /// <summary>Space each side of a button's label (units). The UI Set plate was 16; 20 gives the label air.</summary>
        public const float ButtonPadding = 20f;

#if UNITY_EDITOR
        // The design system allows one primary button per view. Editor-only nudge (a warning, never a change in look).
        static void WarnSecondPrimary(Transform created)
        {
            OSBody body = created.GetComponentInParent<OSBody>();
            if (body == null) return;
            int primaries = 0;
            foreach (Button b in body.GetComponentsInChildren<Button>(false))
                if (b.CompareTag("QDSUIPrimaryButton")) primaries++;
            if (primaries > 1)
                Debug.LogWarning("[OSUi] More than one primary button in " + body.transform.root.name + " (design system: one per view; use Secondary for the rest).");
        }
#endif

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
                    title.gameObject.name = TabTitleName;  // OSToggleSync colours it (solid, dark when lit); the animation would leave it 10% clear
                    title.gameObject.tag = "QDSUISharedThemeColor";  // and the theme manager leaves this colour alone (it would paint it white)
                    ((RectTransform)title.transform).sizeDelta = new Vector2(width - 16f, 30f);
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

        /// <summary>The name a tile's status label gets, so the kit animator's colour curves (which name the label "Label (1)") no longer reach it.</summary>
        public const string TileCaptionName = "Caption";

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
                if (tile.status != null)
                {
                    // The kit animator drives this label's colour by its path: under another name the curves find nothing,
                    // so the caption colour OSTileView sets stays (see OSTileView.CaptionColor).
                    tile.status.gameObject.name = TileCaptionName;
                    Animator animator = go.GetComponent<Animator>();
                    if (animator != null) animator.Rebind();
                }
                float textWidth = width - 24f;  // the tile's own 12-unit padding each side
                if (tile.title != null)
                {
                    tile.title.fontSize = BodyText;
                    tile.title.textWrappingMode = TextWrappingModes.NoWrap;
                    tile.title.overflowMode = TextOverflowModes.Ellipsis;
                    ((RectTransform)tile.title.transform).sizeDelta = new Vector2(textWidth, 30f);
                }
                if (tile.status != null)
                {
                    tile.status.fontSize = CaptionText;
                    tile.status.textWrappingMode = TextWrappingModes.NoWrap;
                    tile.status.overflowMode = TextOverflowModes.Ellipsis;
                    ((RectTransform)tile.status.transform).sizeDelta = new Vector2(textWidth, 26f);
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
            RectTransform row = Row(parent, 48f, 16f);
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
            RectTransform line = Row(column, 32f, 12f);
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
        public static RectTransform Column(Transform parent, float spacing = 16f)
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
            RectTransform outer = Column(parent, ForgeTheme.Space3);
            outer.name = "Section " + heading;
            outer.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(0, 0, (int)ForgeTheme.Space2, 0);  // air above each section
            outer.anchorMin = outer.anchorMax = new Vector2(0f, 1f);
            outer.pivot = new Vector2(0f, 1f);
            outer.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var fit = outer.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            TMP_Text title = Text(outer, heading, TitleText);
            title.fontStyle = FontStyles.Bold;
            RectTransform inner = Column(outer, ForgeTheme.Space3);
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

        // ---------------------------------------------------------------- cards, pairs, chips

        /// <summary>The faint plate behind a <see cref="Card"/>: lighter than the window, so a group of controls reads as one thing.</summary>
        public static readonly Color CardFill = ForgeTheme.RaisedFill;
        /// <summary>The thin line between the steps of a <see cref="Stepper"/> row.</summary>
        public static readonly Color StepLine = new Color(1f, 1f, 1f, 0.16f);
        /// <summary>A step chip is this tall: 52 units, 34 mm, a hand-sized target.</summary>
        public const float ChipHeight = 52f, ChipMinWidth = 150f;

        /// <summary>
        /// A card: a rounded plate (the UI Set's rounded material, <see cref="CardFill"/>) holding a column, with
        /// <paramref name="padding"/> units inside and <paramref name="spacing"/> between its children. It is as tall as what
        /// it holds and as wide as its parent column. Returns the card, which children go straight into.
        /// </summary>
        public static RectTransform Card(Transform parent, float padding = 24f, float spacing = 12f)
        {
            RectTransform rect = Surface(parent, OSSurface.Raised, padding, spacing);
            if (rect != null) rect.name = "Card";
            return rect;
        }

        /// <summary>
        /// A rounded plate at one elevation of the design system (Raised = a card, Inset = a well, Floating = above the window
        /// with a soft shadow, Selected = the chosen one in the accent), holding a column with <paramref name="padding"/> inside
        /// and <paramref name="spacing"/> between children. As tall as what it holds, as wide as its parent column.
        /// </summary>
        public static RectTransform Surface(Transform parent, OSSurface kind, float padding = 24f, float spacing = 12f)
        {
            if (parent == null) return null;
            var go = new GameObject("Surface " + kind, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            ForgeSurface plate;
            if (kind == OSSurface.Floating)
            {
                // A parent draws before its children, so the shadow and the plate are both children, shadow first.
                ForgeLook.Shadow(rect, 22f, 10f, 0.5f);
                plate = ForgeLook.Fill(rect, "Plate", ForgeTheme.FloatingFill, ForgeTheme.RadiusCard);
                plate.raycastTarget = true;  // a floating plate stops rays reaching what is under it
            }
            else plate = go.AddComponent<ForgeSurface>();
            plate.Corner = ForgeTheme.RadiusCard;
            switch (kind)
            {
                case OSSurface.Inset: plate.color = ForgeTheme.InsetFill; plate.Outline = 0.25f; plate.raycastTarget = false; break;
                case OSSurface.Floating:
                    ForgeLook.Ring(rect, "Highlight", new Color(1f, 1f, 1f, ForgeTheme.FloatingRing), ForgeTheme.RadiusCard, 1.5f);
                    break;
                case OSSurface.Selected:
                    plate.color = ForgeTheme.SelectedFill;
                    plate.raycastTarget = false;
                    ForgeLook.Ring(rect, "Ring", WithAlpha(AccentFor(parent), ForgeTheme.SelectedRing), ForgeTheme.RadiusCard, 2f);
                    break;
                default: plate.color = ForgeTheme.RaisedFill; plate.Outline = ForgeTheme.RaisedRing; plate.raycastTarget = false; break;
            }
            var layout = go.AddComponent<VerticalLayoutGroup>();
            int pad = Mathf.RoundToInt(Mathf.Max(0f, padding));
            layout.padding = new RectOffset(pad, pad, pad, pad);
            layout.spacing = Mathf.Max(spacing, 0f);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            var element = go.AddComponent<LayoutElement>();
            element.flexibleWidth = 1f;
            element.flexibleHeight = 0f;
            Touch(parent);
            return rect;
        }

        /// <summary>
        /// One line of a two-column summary: <paramref name="label"/> (secondary, <paramref name="labelWidth"/> units wide) and
        /// its value beside it, wrapping in the space left. Returns the value's text.
        /// </summary>
        public static TMP_Text Pair(Transform parent, string label, string value, float labelWidth = 200f)
        {
            var go = new GameObject("Pair " + label, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var row = go.AddComponent<HorizontalLayoutGroup>();
            row.spacing = 16f;
            row.childAlignment = TextAnchor.UpperLeft;
            row.childControlWidth = row.childControlHeight = true;
            row.childForceExpandWidth = row.childForceExpandHeight = false;
            var element = go.AddComponent<LayoutElement>();
            element.flexibleWidth = 1f;
            TMP_Text name = Text(go.transform, label, SmallText, true);
            var nameLayout = name.GetComponent<LayoutElement>();
            nameLayout.minWidth = nameLayout.preferredWidth = labelWidth;
            nameLayout.flexibleWidth = 0f;
            TMP_Text text = Text(go.transform, value, BodyText);
            text.GetComponent<LayoutElement>().minWidth = 120f;
            return text;
        }

        /// <summary>A small all-capitals tag line ("SAMPLE · LOOK AROUND"), secondary colour, bold, widely tracked.</summary>
        public static TMP_Text Tag(Transform parent, string text)
        {
            TMP_Text tag = Text(parent, text, SmallText, true);
            tag.fontStyle = FontStyles.Bold | FontStyles.UpperCase;
            tag.characterSpacing = ForgeTheme.CapsTracking;
            return tag;
        }

        /// <summary>How a step of a <see cref="Stepper"/> row looks.</summary>
        public enum StepState { Done, Current, Upcoming, Locked }

        /// <summary>A step's node (the numbered disc above its chip), 34 units (22 mm) across.</summary>
        public const float StepNode = 34f;
        const float StepHeadGap = 10f;
        /// <summary>The height of one step: node, gap, chip.</summary>
        public const float StepCellHeight = StepNode + StepHeadGap + ChipHeight;

        /// <summary>A horizontal row of steps joined by a progress rail (<see cref="StepChip"/> fills it).</summary>
        public static RectTransform Stepper(Transform parent)
        {
            var go = new GameObject("Stepper", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var row = go.AddComponent<HorizontalLayoutGroup>();
            row.spacing = ForgeTheme.Space2;
            row.childAlignment = TextAnchor.LowerLeft;
            row.childControlWidth = row.childControlHeight = true;
            row.childForceExpandWidth = row.childForceExpandHeight = false;
            go.AddComponent<LayoutElement>().flexibleWidth = 1f;
            return (RectTransform)go.transform;
        }

        /// <summary>
        /// One step of a <see cref="Stepper"/>: a numbered node with its state word ("Done", "Next", "Locked") over a chip
        /// button. Done: a solid accent node with a check, a quiet chip. Current: an accent-ringed node and chip, the one
        /// thing on the row that glows. Upcoming: a hairline node. Locked: a faint node, the chip switched off. The rail
        /// between two steps is lit in the accent once the step before it is done. The chip is <see cref="ChipHeight"/> tall
        /// and at least <see cref="ChipMinWidth"/> wide. Returns the chip's button (named "Button " + label, like every
        /// <see cref="Button"/>). The accent is the window's (its badge colour), or the OS accent.
        /// </summary>
        public static Button StepChip(RectTransform stepper, int number, string label, string icon, StepState state, Action onClick)
        {
            if (stepper == null) return null;
            Color accent = AccentFor(stepper);
            if (stepper.childCount > 0) StepJoin(stepper, accent);
            var cell = new GameObject("Step " + number, typeof(RectTransform));
            cell.transform.SetParent(stepper, false);
            var column = cell.AddComponent<VerticalLayoutGroup>();
            column.spacing = StepHeadGap;
            column.childAlignment = TextAnchor.UpperCenter;
            column.childControlWidth = column.childControlHeight = true;
            column.childForceExpandWidth = column.childForceExpandHeight = false;
            cell.AddComponent<LayoutElement>().flexibleWidth = 0f;

            // Head: node and state word, centred over the chip.
            RectTransform head = Row(cell.transform, StepNode, ForgeTheme.Space2);
            head.name = "Head";
            var headLayout = head.GetComponent<HorizontalLayoutGroup>();
            headLayout.childAlignment = TextAnchor.MiddleCenter;
            StepNodeView(head, number, state, accent);
            string word = state == StepState.Done ? "Done" : state == StepState.Current ? "Next" : state == StepState.Locked ? "Locked" : null;
            if (word != null)
            {
                TMP_Text caption = Text(head, word, SmallText, true);
                caption.textWrappingMode = TextWrappingModes.NoWrap;
                caption.alignment = TextAlignmentOptions.MidlineLeft;
                caption.GetComponent<LayoutElement>().flexibleWidth = 0f;
                if (state == StepState.Current)
                {
                    caption.fontStyle = FontStyles.Bold;
                    ForgeLook.Tint(caption, accent);
                }
                else if (state == StepState.Locked) ForgeLook.Tint(caption, ForgeTheme.TextTertiary);
            }

            OSButtonStyle style = state == StepState.Done ? OSButtonStyle.Borderless : OSButtonStyle.Secondary;
            Button chip = Button(cell.transform, label, onClick, style, state == StepState.Done ? "check" : icon);
            if (chip == null) return null;
            var chipLayout = chip.GetComponent<LayoutElement>();
            float width = Mathf.Max(ChipMinWidth, chipLayout != null ? chipLayout.preferredWidth : ChipMinWidth);
            Size(chip.transform, width, ChipHeight);
            if (chipLayout != null) chipLayout.flexibleHeight = 0f;
            head.GetComponent<LayoutElement>().preferredWidth = width;
            if (state == StepState.Locked) chip.interactable = false;
            if (state == StepState.Current)
            {
                ForgeLook.Ring(chip.transform, "Ring", WithAlpha(accent, 0.9f), ForgeTheme.RadiusControl + 6f, 2.5f, 5f, true);
            }
            return chip;
        }

        // The numbered disc of a step.
        static void StepNodeView(Transform head, int number, StepState state, Color accent)
        {
            var node = new GameObject("Node " + state, typeof(RectTransform));
            node.transform.SetParent(head, false);
            Size(node.transform, StepNode, StepNode);
            var disc = node.AddComponent<ForgeSurface>();
            disc.Pill = true;
            disc.raycastTarget = false;
            Color ink;
            switch (state)
            {
                case StepState.Done: disc.color = accent; ink = ForgeTheme.TextOnAccent; break;
                case StepState.Current:
                    disc.color = WithAlpha(accent, 0.18f);
                    disc.Glow = 0.8f;
                    ForgeLook.Ring(node.transform, "Ring", accent, StepNode * 0.5f, 2.5f, 0f, true);
                    ink = accent;
                    break;
                case StepState.Locked: disc.color = new Color(1f, 1f, 1f, 0f); disc.Outline = 0.16f; ink = ForgeTheme.TextTertiary; break;
                default: disc.color = new Color(1f, 1f, 1f, 0.07f); disc.Outline = 0.22f; ink = ForgeTheme.TextSecondary; break;
            }
            TMP_Text mark = Text(node.transform, state == StepState.Done ? "\u2713" : number.ToString(), CaptionText);
            mark.GetComponent<LayoutElement>().ignoreLayout = true;
            var markRect = mark.rectTransform;
            markRect.anchorMin = Vector2.zero;
            markRect.anchorMax = Vector2.one;
            markRect.offsetMin = markRect.offsetMax = Vector2.zero;
            mark.alignment = TextAlignmentOptions.Center;
            mark.textWrappingMode = TextWrappingModes.NoWrap;
            mark.fontStyle = FontStyles.Bold;
            ForgeLook.Tint(mark, ink);
            ForgeTheme.DarkOnLight(mark, state == StepState.Done);
        }

        // The rail between two steps, through the nodes' centres, taking the spare width. Lit when the step before is done.
        static void StepJoin(RectTransform stepper, Color accent)
        {
            Transform previous = stepper.GetChild(stepper.childCount - 1);
            bool lit = previous != null && previous.Find("Head/Node " + StepState.Done) != null;
            var go = new GameObject("Join", typeof(RectTransform));
            go.transform.SetParent(stepper, false);
            var element = go.AddComponent<LayoutElement>();
            element.minWidth = 16f;
            element.preferredWidth = 40f;
            element.flexibleWidth = 1f;
            element.minHeight = element.preferredHeight = StepCellHeight;
            var line = new GameObject("Line", typeof(RectTransform));
            line.transform.SetParent(go.transform, false);
            var rect = (RectTransform)line.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(0f, 3f);
            rect.anchoredPosition = new Vector2(0f, -StepNode * 0.5f);
            var bar = line.AddComponent<ForgeSurface>();
            bar.Pill = true;
            bar.color = lit ? WithAlpha(accent, 0.75f) : StepLine;
            bar.raycastTarget = false;
        }

        // ---------------------------------------------------------------- design system helpers (additive)

        /// <summary>The accent for UI under <paramref name="t"/>: its window's badge colour when it has one, else the OS accent (Albie lime).</summary>
        public static Color AccentFor(Transform t)
        {
            ForgePanel panel = t != null ? t.GetComponentInParent<ForgePanel>() : null;
            if (panel != null)
            {
                Color a = panel.Accent;
                if (!(a.r > 0.99f && a.g > 0.99f && a.b > 0.99f)) return Color.Lerp(a, Color.white, 0.25f);  // lifted so it reads as text (>= 4.5:1)
            }
            return ForgeTheme.Accent;
        }

        static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, a);

        /// <summary>The screen's one big title: display size (34 units, 16 mm cap height), bold, tight tracking. One per view.</summary>
        public static TMP_Text Heading(Transform parent, string text)
        {
            TMP_Text t = Text(parent, text, ForgeTheme.TypeDisplay);
            t.fontStyle = FontStyles.Bold;
            t.characterSpacing = ForgeTheme.HeadingTracking;
            return t;
        }

        /// <summary>Wrapped running text, kept to a comfortable line length (<see cref="ForgeTheme.MaxLineWidth"/>), with generous leading.</summary>
        public static TMP_Text Paragraph(Transform parent, string text, bool secondary = false)
        {
            TMP_Text t = Text(parent, text, secondary ? SmallText : BodyText, secondary);
            var layout = t.GetComponent<LayoutElement>();
            layout.preferredWidth = ForgeTheme.MaxLineWidth;
            layout.minWidth = 0f;
            layout.flexibleWidth = 0f;
            return t;
        }

        /// <summary>A coloured line of text (success, warning, danger, the accent) that the theme pass leaves alone.</summary>
        public static TMP_Text Tinted(Transform parent, string text, Color color, float size = BodyText)
        {
            TMP_Text t = Text(parent, text, size);
            ForgeLook.Tint(t, color);
            return t;
        }

        /// <summary>
        /// A status chip (not a button): a pill with a label, tinted (a semantic or accent colour) or neutral when
        /// <paramref name="tint"/> is null. 36 units tall. Returns the chip.
        /// </summary>
        public static RectTransform Chip(Transform parent, string label, Color? tint = null)
        {
            if (parent == null) return null;
            var go = new GameObject("Chip " + label, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var plate = go.AddComponent<ForgeSurface>();
            plate.Pill = true;
            plate.raycastTarget = false;
            Color c = tint ?? Color.white;
            plate.color = tint.HasValue ? WithAlpha(c, 0.16f) : new Color(1f, 1f, 1f, 0.08f);
            plate.Outline = tint.HasValue ? 0.45f : 0.14f;
            var row = go.AddComponent<HorizontalLayoutGroup>();
            row.padding = new RectOffset((int)ForgeTheme.Space4, (int)ForgeTheme.Space4, 0, 0);
            row.childAlignment = TextAnchor.MiddleCenter;
            row.childControlWidth = row.childControlHeight = true;
            row.childForceExpandWidth = row.childForceExpandHeight = false;
            TMP_Text text = Text(go.transform, label, CaptionText);
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.alignment = TextAlignmentOptions.Center;
            text.GetComponent<LayoutElement>().flexibleWidth = 0f;
            if (tint.HasValue) ForgeLook.Tint(text, Color.Lerp(c, Color.white, 0.35f));
            float width = text.GetPreferredValues(label ?? "").x + 2f * ForgeTheme.Space4 + 2f;
            Size(go.transform, width, 36f);
            Touch(parent);
            return (RectTransform)go.transform;
        }

        /// <summary>
        /// A round icon button: no plate until a hand or ray comes near (then a soft glass disc and hairline), sinks when
        /// pressed. <paramref name="icon"/> is a kit icon name; close, minimise, plus, check, chevron_left, chevron_right and
        /// more are drawn as crisp glyphs. At least 46 units (30 mm). Named "Button " + icon.
        /// </summary>
        public static Button IconButton(Transform parent, string icon, Action onClick, float size = ButtonHeight, bool tonal = false)
        {
            if (parent == null) return null;
            size = Mathf.Max(size, 46f);
            ForgeButtonLook.Palette palette = tonal ? ForgeButtonLook.Tonal : ForgeButtonLook.Quiet;
            ForgeGlyph glyph;
            bool drawn = GlyphFor(icon, out glyph);
            Button button = ForgeLook.IconButton(parent, drawn ? glyph : ForgeGlyph.Dot, null, size, palette, icon);
            Transform glyphHolder = button.transform.Find("Glyph " + (drawn ? glyph : ForgeGlyph.Dot));
            if (!drawn)
            {
                Sprite sprite = Icon(icon);
                if (sprite != null && glyphHolder != null)
                {
                    UnityEngine.Object.Destroy(glyphHolder.gameObject);
                    var img = new GameObject("Icon", typeof(RectTransform)).AddComponent<Image>();
                    img.transform.SetParent(button.transform, false);
                    img.rectTransform.sizeDelta = Vector2.one * size * 0.5f;
                    img.sprite = sprite;
                    img.preserveAspect = true;
                    img.raycastTarget = false;
                    img.color = ForgeTheme.TextPrimary;
                    var look = button.GetComponent<ForgeButtonLook>();
                    look.Bind(button.GetComponent<ForgeSurface>(), palette, button.transform, img);
                }
            }
            button.onClick.AddListener(PressAction);
            if (onClick != null) button.onClick.AddListener(() => onClick());
            Size(button.transform, size, size);
            Touch(parent);
            return button;
        }

        static bool GlyphFor(string icon, out ForgeGlyph glyph)
        {
            switch (icon)
            {
                case "close": glyph = ForgeGlyph.Close; return true;
                case "minimise": case "minimize": glyph = ForgeGlyph.Minimise; return true;
                case "plus": glyph = ForgeGlyph.Plus; return true;
                case "check": glyph = ForgeGlyph.Check; return true;
                case "chevron_left": case "back": glyph = ForgeGlyph.ChevronLeft; return true;
                case "chevron_right": glyph = ForgeGlyph.ChevronRight; return true;
                case "more": glyph = ForgeGlyph.More; return true;
                default: glyph = ForgeGlyph.Dot; return false;
            }
        }

        /// <summary>
        /// A tappable list row: an optional icon, a title with an optional detail line under it, and an optional trailing
        /// value or chevron at the right. A raised plate that lifts on hover and sinks on press, 72 units (47 mm) tall, or 60
        /// without a detail. Returns its Button (named "Button " + title). <paramref name="onClick"/> null makes a plain row.
        /// </summary>
        public static Button ListRow(Transform parent, string title, string detail = null, string icon = null, string trailing = null, Action onClick = null)
        {
            if (parent == null) return null;
            var go = new GameObject("Button " + title, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var plate = go.AddComponent<ForgeSurface>();
            plate.Corner = ForgeTheme.RadiusControl;
            plate.raycastTarget = true;
            var button = go.AddComponent<Button>();
            button.targetGraphic = plate;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            var row = go.AddComponent<HorizontalLayoutGroup>();
            row.padding = new RectOffset((int)ForgeTheme.Space5, (int)ForgeTheme.Space5, 0, 0);
            row.spacing = ForgeTheme.Space4;
            row.childAlignment = TextAnchor.MiddleLeft;
            row.childControlWidth = row.childControlHeight = true;
            row.childForceExpandWidth = row.childForceExpandHeight = false;
            var element = go.AddComponent<LayoutElement>();
            float height = string.IsNullOrEmpty(detail) ? 60f : 72f;
            element.minHeight = element.preferredHeight = height;
            element.flexibleWidth = 1f;
            element.flexibleHeight = 0f;

            Sprite sprite = Icon(icon);
            if (sprite != null)
            {
                var img = new GameObject("Icon", typeof(RectTransform)).AddComponent<Image>();
                img.transform.SetParent(go.transform, false);
                img.sprite = sprite;
                img.preserveAspect = true;
                img.raycastTarget = false;
                img.color = ForgeTheme.TextPrimary;
                Size(img.transform, 32f, 32f);
            }
            RectTransform texts = Column(go.transform, 2f);
            texts.GetComponent<VerticalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
            texts.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            TMP_Text name = Text(texts, title, BodyText);
            name.textWrappingMode = TextWrappingModes.NoWrap;
            name.overflowMode = TextOverflowModes.Ellipsis;
            if (!string.IsNullOrEmpty(detail))
            {
                TMP_Text d = Text(texts, detail, CaptionText, true);
                d.textWrappingMode = TextWrappingModes.NoWrap;
                d.overflowMode = TextOverflowModes.Ellipsis;
            }
            if (!string.IsNullOrEmpty(trailing))
            {
                TMP_Text t = Text(go.transform, trailing, SmallText, true);
                t.textWrappingMode = TextWrappingModes.NoWrap;
                t.alignment = TextAlignmentOptions.MidlineRight;
                t.GetComponent<LayoutElement>().flexibleWidth = 0f;
            }
            else if (onClick != null)
            {
                var holder = new GameObject("Chevron", typeof(RectTransform));
                holder.transform.SetParent(go.transform, false);
                Size(holder.transform, 20f, 20f);
                ForgeLook.Glyph(holder.transform, ForgeGlyph.ChevronRight, 18f, ForgeTheme.TextSecondary);
            }

            var look = go.AddComponent<ForgeButtonLook>();
            look.Bind(plate, onClick != null ? ForgeButtonLook.Raised : Static(ForgeButtonLook.Raised), go.transform);
            if (onClick != null)
            {
                button.onClick.AddListener(PressAction);
                button.onClick.AddListener(() => onClick());
            }
            else button.interactable = false;
            Touch(parent);
            return button;
        }

        // A plain row keeps its rest look when it can't be pressed (not the faded disabled look).
        static ForgeButtonLook.Palette Static(ForgeButtonLook.Palette p)
        {
            p.disabled = p.rest;
            p.contentDisabled = p.content;
            return p;
        }

        /// <summary>
        /// A thin progress rail (a pill track with an accent fill) for "3 of 7 done". <paramref name="progress"/> is 0..1;
        /// change it later through the returned rail's <see cref="ForgeProgress"/> (it eases). 8 units tall, full width.
        /// </summary>
        public static ForgeProgress ProgressRail(Transform parent, float progress, Color? color = null)
        {
            if (parent == null) return null;
            var go = new GameObject("Progress", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var track = go.AddComponent<ForgeSurface>();
            track.Pill = true;
            track.color = new Color(1f, 1f, 1f, 0.10f);
            track.raycastTarget = false;
            var element = go.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = 8f;
            element.flexibleWidth = 1f;
            element.flexibleHeight = 0f;
            var fillRect = ForgeLook.NewRect("Fill", go.transform);
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = new Vector2(0f, 1f);
            fillRect.offsetMin = fillRect.offsetMax = Vector2.zero;
            var fill = fillRect.gameObject.AddComponent<ForgeSurface>();
            fill.Pill = true;
            fill.color = color ?? AccentFor(parent);
            fill.Glow = 0.4f;
            fill.raycastTarget = false;
            var bar = go.AddComponent<ForgeProgress>();
            bar.fill = fillRect;
            bar.Snap(progress);
            Touch(parent);
            return bar;
        }

        /// <summary>A hairline divider across the column (1.5 units, 8% white), with <see cref="ForgeTheme.Space2"/> of air each side.</summary>
        public static RectTransform Divider(Transform parent)
        {
            if (parent == null) return null;
            var go = new GameObject("Divider", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var element = go.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = 1.5f + 2f * ForgeTheme.Space2;
            element.flexibleWidth = 1f;
            var line = ForgeLook.NewRect("Line", go.transform);
            line.anchorMin = new Vector2(0f, 0.5f);
            line.anchorMax = new Vector2(1f, 0.5f);
            line.sizeDelta = new Vector2(0f, 1.5f);
            var s = line.gameObject.AddComponent<ForgeSurface>();
            s.color = ForgeTheme.Hairline;
            s.raycastTarget = false;
            return (RectTransform)go.transform;
        }

        /// <summary>
        /// An empty state: an optional icon in a soft disc, a heading and one sentence of what to do, centred. The one place a
        /// view says "nothing here yet". Returns the column (add one action button to it if there is one).
        /// </summary>
        public static RectTransform EmptyState(Transform parent, string heading, string caption, string icon = null)
        {
            if (parent == null) return null;
            RectTransform column = Column(parent, ForgeTheme.Space3);
            column.name = "Empty " + heading;
            column.anchorMin = column.anchorMax = column.pivot = new Vector2(0.5f, 0.5f);
            var layout = column.GetComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.padding = new RectOffset(0, 0, (int)ForgeTheme.Space6, (int)ForgeTheme.Space6);
            var element = column.gameObject.AddComponent<LayoutElement>();
            element.flexibleWidth = 1f;
            column.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            Sprite sprite = Icon(icon);
            if (sprite != null)
            {
                var holder = new GameObject("Mark", typeof(RectTransform));
                holder.transform.SetParent(column, false);
                Size(holder.transform, 72f, 72f);
                var disc = holder.AddComponent<ForgeSurface>();
                disc.Pill = true;
                disc.color = new Color(1f, 1f, 1f, 0.07f);
                disc.Outline = 0.14f;
                disc.raycastTarget = false;
                var img = new GameObject("Icon", typeof(RectTransform)).AddComponent<Image>();
                img.transform.SetParent(holder.transform, false);
                img.rectTransform.sizeDelta = new Vector2(34f, 34f);
                img.sprite = sprite;
                img.preserveAspect = true;
                img.raycastTarget = false;
                img.color = ForgeTheme.TextSecondary;
            }
            TMP_Text title = Text(column, heading, TitleText);
            title.fontStyle = FontStyles.Bold;
            title.alignment = TextAlignmentOptions.Center;
            if (!string.IsNullOrEmpty(caption))
            {
                TMP_Text line = Paragraph(column, caption, true);
                line.alignment = TextAlignmentOptions.Center;
                line.GetComponent<LayoutElement>().preferredWidth = 520f;
            }
            return column;
        }

        /// <summary>Stops a control (a button, a row) from taking spare height in a column.</summary>
        public static void Rigid(Component control)
        {
            if (control == null) return;
            var layout = control.GetComponent<LayoutElement>();
            if (layout != null) layout.flexibleHeight = 0f;
        }

        // ---------------------------------------------------------------- confirm

        /// <summary>
        /// An inline two-button confirm docked along the bottom of the given window (never a system dialog): the question, then the confirm
        /// button and Cancel. Nothing happens until the confirm tap; Cancel closes it.
        /// </summary>
        public static void Confirm(Transform window, string question, string confirmLabel, Action onConfirm)
        {
            OSKit kit = OSKit.Current;
            if (window == null || kit == null) return;
            OSBody body = window.GetComponentInChildren<OSBody>(true);
            Transform host = body != null ? body.transform : window;
            Transform old = host.Find("OSConfirm");
            if (old != null) UnityEngine.Object.Destroy(old.gameObject);

            // A strip docked along the bottom edge of the window, solid so nothing shows through it: the rows and notes above
            // it stay readable (nothing is dimmed or covered except the status line at the very bottom while it is open).
            var root = new GameObject("OSConfirm", typeof(RectTransform));
            root.transform.SetParent(host, false);
            var rootRect = (RectTransform)root.transform;
            rootRect.anchorMin = new Vector2(0f, 0f);
            rootRect.anchorMax = new Vector2(1f, 0f);
            rootRect.pivot = new Vector2(0.5f, 0f);
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;
            rootRect.sizeDelta = new Vector2(0f, 96f);
            root.AddComponent<LayoutElement>().ignoreLayout = true;
            // Above every control of the window, including ones that sort themselves (a pager at the bottom edge).
            var sort = root.AddComponent<Canvas>();
            sort.overrideSorting = true;
            sort.sortingOrder = 50;
            root.AddComponent<GraphicRaycaster>();
            // Floating elevation: an opaque plate (nothing shows through), a soft dark halo and a light hairline on top.
            var bg = root.AddComponent<ForgeSurface>();
            bg.color = new Color(ForgeTheme.FloatingFill.r, ForgeTheme.FloatingFill.g, ForgeTheme.FloatingFill.b, 1f);
            bg.Corner = ForgeTheme.RadiusCard;
            bg.Glow = ForgeTheme.FloatingHalo;
            bg.raycastTarget = true;
            ForgeLook.Ring(root.transform, "Highlight", new Color(1f, 1f, 1f, ForgeTheme.FloatingRing), ForgeTheme.RadiusCard, 1.5f);
            var strip = root.AddComponent<HorizontalLayoutGroup>();
            strip.padding = new RectOffset(32, 20, 12, 12);
            strip.spacing = ForgeTheme.Space4;
            strip.childAlignment = TextAnchor.MiddleLeft;
            strip.childControlWidth = strip.childControlHeight = true;
            strip.childForceExpandWidth = false;
            strip.childForceExpandHeight = false;

            TMP_Text text = Text(root.transform, question, BodyText);
            text.alignment = TextAlignmentOptions.MidlineLeft;
            var textLayout = text.GetComponent<LayoutElement>();
            if (textLayout == null) textLayout = text.gameObject.AddComponent<LayoutElement>();
            textLayout.flexibleWidth = 1f;
            textLayout.minWidth = 120f;
            Button(root.transform, confirmLabel, () =>
            {
                UnityEngine.Object.Destroy(root);
                onConfirm?.Invoke();
            }, OSButtonStyle.Primary);
            Button(root.transform, "Cancel", () => UnityEngine.Object.Destroy(root), OSButtonStyle.Secondary);
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

    /// <summary>An elevation of the design system for <see cref="OSUi.Surface"/>.</summary>
    public enum OSSurface { Raised, Inset, Floating, Selected }

    /// <summary>Which UI Set button a <see cref="OSUi.Button"/> is.</summary>
    public enum OSButtonStyle { Primary, Secondary, Borderless, Destructive }
}
