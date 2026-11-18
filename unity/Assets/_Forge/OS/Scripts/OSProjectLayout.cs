using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// Layout pieces of the project screens that <see cref="OSUi"/> doesn't have: the one big primary button (Next), a stage
    /// chip of the project bar, a stage row of the project window, and spacers. Sizes are OS units (0.65 mm).
    /// Everything is built from <see cref="OSUi"/> controls and <see cref="ForgeTheme"/> / <see cref="OSUi"/> tokens, so the
    /// shared look, sound and input (mouse, ray and poke) are the same as every other button and a restyle of those helpers
    /// reaches these too.
    /// </summary>
    public static class OSProjectLayout
    {
        /// <summary>The big button is 72 units tall (47 mm) and at least 380 wide (247 mm), its label 26 units (the title size).</summary>
        public const float HeroHeight = 72f, HeroMinWidth = 380f, HeroText = OSUi.TitleText;
        /// <summary>Space between the groups of the window (lead, big button, stages, bottom row): 24 units, 16 mm.</summary>
        public const float GroupGap = ForgeTheme.Space5;
        /// <summary>The project bar: its chips are <see cref="OSUi.ChipHeight"/> tall (34 mm, a hand-sized target), this far apart, with this padding around the row.</summary>
        public const float RailGap = ForgeTheme.Space2, RailPadding = ForgeTheme.Space3;
        /// <summary>The stage rows of the project window are this far apart.</summary>
        public const float StageRowGap = ForgeTheme.Space2;
        /// <summary>How much of its look a locked stage keeps (it stays tappable: a tap says why and where to go first).</summary>
        public const float LockedAlpha = 0.45f;
        /// <summary>The ring around the stage the person is at, this far outside the chip (units).</summary>
        public const float RingOutset = ForgeTheme.Space1;

        /// <summary>The window's one big primary button, alone on its row. Named "Button " + label like every OSUi button.</summary>
        public static Button Hero(Transform parent, string label, Action onClick, string icon)
        {
            RectTransform row = OSUi.Row(parent, HeroHeight, 0f);
            OSUi.Rigid(row);
            Button button = OSUi.Button(row, label, onClick, OSButtonStyle.Primary, icon);
            if (button == null) return null;
            bool hasIcon = button.transform.Find("Content/Background/Elements/Icon") is Transform i && i.gameObject.activeSelf;
            float width = HeroMinWidth;
            TMP_Text text = LabelOf(button);
            if (text != null && !string.IsNullOrEmpty(label))
            {
                text.fontSize = HeroText;
                float labelWidth = text.GetPreferredValues(label).x;
                float fit = Mathf.Ceil(labelWidth) + 2f;
                ((RectTransform)text.transform).sizeDelta = new Vector2(fit, 36f);
                ((RectTransform)text.transform.parent).sizeDelta = new Vector2(fit, 40f);
                width = Mathf.Max(HeroMinWidth, labelWidth + 64f + (hasIcon ? 40f : 0f));
            }
            OSUi.Size(button.transform, width, HeroHeight);
            return button;
        }

        /// <summary>
        /// One stage on the project bar: its name; a check when done, a warning mark when stale (it learned on an older
        /// design), the stage's icon when it is the one the person is at (which is also ringed); quiet when done, faded when
        /// locked (still tappable). Named "Button " + the stage's name.
        /// </summary>
        public static Button RailChip(Transform parent, StageState s, Action onClick)
        {
            string icon = s.Status == StageStatus.Done ? "check" : s.Status == StageStatus.Stale ? "warning" : s.IsCurrent ? s.Def.Icon : null;
            OSButtonStyle style = s.Status == StageStatus.Done && !s.IsCurrent ? OSButtonStyle.Borderless : OSButtonStyle.Secondary;
            Button chip = OSUi.Button(parent, s.Def.Name, onClick, style, icon);
            if (chip == null) return null;
            Fit(chip, OSUi.ChipHeight);
            if (s.IsCurrent) Ring(chip);
            if (s.Status == StageStatus.Locked) Fade(chip);
            return chip;
        }

        /// <summary>
        /// One stage of the project window, as a list row (<see cref="OSUi.ListRow"/>): a check when done, a warning mark when
        /// stale, else the stage's icon; the stage's name; and at the right its one-line result ("v7 · 160 mm arms", "2 laps
        /// flown"). A locked stage says why under its name and "Go to Course" at the right (a tap goes there), faded. The stage
        /// that is Next, or where the person is, is ringed. Named "Button " + the stage's name.
        /// </summary>
        public static Button StageRow(Transform parent, StageState s, string goTo, Action onClick)
        {
            bool locked = s.Status == StageStatus.Locked;
            string icon = s.Status == StageStatus.Done ? "check" : s.Status == StageStatus.Stale ? "warning" : s.Def.Icon;
            string detail = locked ? s.Reason : null;
            string trailing = locked ? (goTo != null ? "Go to " + goTo : null) : s.Result;
            Button row = OSUi.ListRow(parent, s.Def.Name, detail, icon, trailing, onClick);
            if (row == null) return null;
            if (s.IsCurrent || s.IsNext) Ring(row);
            if (locked) Fade(row);
            return row;
        }

        // The outline ring of the stage at hand (the same sprite as the "new" tile and the stepper's current chip).
        static void Ring(Button button)
        {
            var ring = new GameObject("Ring", typeof(RectTransform));
            ring.transform.SetParent(button.transform, false);
            var rect = (RectTransform)ring.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(-RingOutset, -RingOutset);
            rect.offsetMax = new Vector2(RingOutset, RingOutset);
            ring.AddComponent<LayoutElement>().ignoreLayout = true;
            var image = ring.AddComponent<Image>();
            image.sprite = OSUi.OutlineSprite();
            image.type = Image.Type.Sliced;
            image.color = ForgeTheme.PillOn;
            image.raycastTarget = false;
        }

        static void Fade(Button button)
        {
            var group = button.GetComponent<CanvasGroup>();
            if (group == null) group = button.gameObject.AddComponent<CanvasGroup>();
            group.alpha = LockedAlpha;
        }

        static TMP_Text LabelOf(Button button)
        {
            Transform t = button != null ? button.transform.Find("Content/Background/Elements/Text/Label") : null;
            return t != null ? t.GetComponent<TMP_Text>() : null;
        }

        /// <summary>A column made by <see cref="OSUi.Column"/> inside another: as tall as what it holds, as wide as its parent.</summary>
        public static void FitHeight(RectTransform column)
        {
            if (column == null) return;
            var element = column.GetComponent<LayoutElement>();
            if (element == null) element = column.gameObject.AddComponent<LayoutElement>();
            element.flexibleWidth = 1f;
            element.flexibleHeight = 0f;
        }

        /// <summary>An empty element that takes the spare height of a column (pushes what follows to the bottom).</summary>
        public static void FlexibleHeight(Transform parent)
        {
            var go = new GameObject("Spacer", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var element = go.AddComponent<LayoutElement>();
            element.flexibleHeight = 1f;
            element.minHeight = 0f;
        }

        /// <summary>An empty element that takes the spare width of a row (pushes what follows to the right).</summary>
        public static void FlexibleWidth(Transform parent)
        {
            var go = new GameObject("Spacer", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.AddComponent<LayoutElement>().flexibleWidth = 1f;
        }

        /// <summary>Gives a button made by <see cref="OSUi.Button"/> its own width at <paramref name="height"/> units tall.</summary>
        public static void Fit(Button button, float height)
        {
            if (button == null) return;
            var layout = button.GetComponent<LayoutElement>();
            OSUi.Size(button.transform, layout != null ? layout.preferredWidth : OSUi.ButtonMinWidth, height);
        }
    }
}
