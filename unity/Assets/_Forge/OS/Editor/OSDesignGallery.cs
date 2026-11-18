using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS.Editor
{
    /// <summary>
    /// Forge > OS > Design Gallery (Play mode): opens one OS window that shows every design-system component and state, for
    /// review and screenshots (Capture Look Set takes it as the "design_gallery" section). Type scale, colours, buttons
    /// (primary, secondary, quiet, destructive, disabled), icon buttons, pills, a toggle and a slider, a card with pairs and a
    /// progress rail, the stepper (done, current, upcoming, locked), list rows, the floating and selected surfaces, chips
    /// and an empty state. Everything is built with the same helpers the windows use (OSUi, ForgeLook), so what it shows is
    /// what they get. Tokens: ForgeTheme; the language: docs/DESIGN_SYSTEM.md.
    /// </summary>
    public static class OSDesignGallery
    {
        public const string WindowId = "design_gallery";
        static readonly Vector2 Size = new Vector2(1.5f, 0.9f);

        [MenuItem("Forge/OS/Design Gallery")]
        static void Menu()
        {
            if (!Application.isPlaying)
            {
                Debug.Log("[Design Gallery] Enter Play mode (Main scene) first, then run Forge > OS > Design Gallery again.");
                return;
            }
            if (Open() == null) Debug.LogWarning("[Design Gallery] No OS shell in this scene (open Main).");
        }

        /// <summary>Opens (or brings forward) the gallery window. Null without an OS shell.</summary>
        public static OSWindow Open()
        {
            OSShell shell = OSShell.Current;
            if (shell == null || shell.Windows == null) return null;
            return shell.Windows.Open(WindowId, "Design system", Size, Build, "OS", ForgeTheme.Accent);
        }

        static void Build(ForgePanel panel)
        {
            RectTransform body = OSUi.Body(panel);
            if (body == null) return;
            var holder = new GameObject("Gallery", typeof(RectTransform));
            holder.transform.SetParent(body, false);
            var rect = (RectTransform)holder.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var row = holder.AddComponent<HorizontalLayoutGroup>();
            int pad = (int)ForgeTheme.Space6;
            row.padding = new RectOffset(pad, pad, (int)ForgeTheme.Space5, pad);
            row.spacing = ForgeTheme.Space7;
            row.childControlWidth = row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = true;

            TypeAndColour(Col(rect, 1f));
            Controls(Col(rect, 1f));
            Structure(Col(rect, 1.35f));
        }

        static RectTransform Col(RectTransform parent, float weight)
        {
            RectTransform c = OSUi.Column(parent, ForgeTheme.Space4);
            var e = c.gameObject.AddComponent<LayoutElement>();
            e.flexibleWidth = weight;
            e.minWidth = 300f;
            return c;
        }

        static void TypeAndColour(RectTransform col)
        {
            OSUi.Heading(col, "Design system");
            OSUi.Paragraph(col, "Deep glass, one accent, few sizes, room to breathe. Every control below is the real helper.", true);

            RectTransform type = OSUi.Section(col, "Type");
            OSUi.Heading(type, "Display 34");
            TMP_Text title = OSUi.Text(type, "Title 26, bold", OSUi.TitleText);
            title.fontStyle = FontStyles.Bold;
            OSUi.Text(type, "Body 22: the text people read.", OSUi.BodyText);
            OSUi.Text(type, "Secondary 21.5: hints and notes.", OSUi.SmallText, true);
            OSUi.Tag(type, "Tag · caps caption");

            RectTransform colour = OSUi.Section(col, "Colour");
            RectTransform a = OSUi.Row(colour, 36f, ForgeTheme.Space2);
            OSUi.Chip(a, "Accent", ForgeTheme.Accent);
            OSUi.Chip(a, "Neutral");
            RectTransform b = OSUi.Row(colour, 36f, ForgeTheme.Space2);
            OSUi.Chip(b, "Done", ForgeTheme.Success);
            OSUi.Chip(b, "Check", ForgeTheme.Warning);
            OSUi.Chip(b, "Failed", ForgeTheme.Danger);
            RectTransform c = OSUi.Row(colour, 36f, ForgeTheme.Space2);
            OSUi.Chip(c, "ML", ForgeTheme.ML);
            OSUi.Chip(c, "CAD", ForgeTheme.CAD);
            OSUi.Chip(c, "Code", ForgeTheme.Code);
            OSUi.Chip(c, "Msg", ForgeTheme.Messages);
            OSUi.Tinted(colour, "Success text", ForgeTheme.SuccessText);
            OSUi.Tinted(colour, "Warning text", ForgeTheme.WarningText);
            OSUi.Tinted(colour, "Danger text", ForgeTheme.DangerText);
        }

        static void Controls(RectTransform col)
        {
            RectTransform buttons = OSUi.Section(col, "Buttons");
            RectTransform r1 = OSUi.Row(buttons, OSUi.ButtonHeight, ForgeTheme.Space3);
            OSUi.Button(r1, "Primary", null, OSButtonStyle.Primary);
            OSUi.Button(r1, "Secondary", null);
            RectTransform r2 = OSUi.Row(buttons, OSUi.ButtonHeight, ForgeTheme.Space3);
            OSUi.Button(r2, "Quiet", null, OSButtonStyle.Borderless);
            OSUi.Button(r2, "Delete", null, OSButtonStyle.Destructive);
            Button off = OSUi.Button(r2, "Disabled", null);
            if (off != null) off.interactable = false;

            RectTransform icons = OSUi.Section(col, "Icon buttons");
            RectTransform r3 = OSUi.Row(icons, 48f, ForgeTheme.Space3);
            OSUi.IconButton(r3, "close", null);
            OSUi.IconButton(r3, "minimise", null);
            OSUi.IconButton(r3, "plus", null, 48f, true);
            OSUi.IconButton(r3, "check", null, 48f, true);
            OSUi.IconButton(r3, "chevron_left", null);
            OSUi.IconButton(r3, "more", null);

            RectTransform pills = OSUi.Section(col, "Pills (older panels)");
            RectTransform r4 = OSUi.Row(pills, 48f, ForgeTheme.Space3);
            ForgePill p1 = ForgePill.Create(r4, "Off", () => { }, 48f, OSUi.BodyText);
            ForgePill p2 = ForgePill.Create(r4, "On", () => { }, 48f, OSUi.BodyText);
            p2.On = true;
            ForgePill p3 = ForgePill.Create(r4, "Quiet on", () => { }, 48f, OSUi.BodyText);
            p3.Quiet = true;
            p3.On = true;
            foreach (ForgePill p in new[] { p1, p2, p3 }) OSUi.Size(p.transform, p.Rect.sizeDelta.x, 48f);

            RectTransform inputs = OSUi.Section(col, "Toggle and slider");
            OSUi.Toggle(inputs, "Hands-free voice", true, null);
            OSUi.Slider(inputs, "Distance", 0.35f, 0.7f, 0.5f, null);
        }

        static void Structure(RectTransform col)
        {
            RectTransform steps = OSUi.Section(col, "Stepper");
            RectTransform stepper = OSUi.Stepper(steps);
            OSUi.StepChip(stepper, 1, "Design", "cube", OSUi.StepState.Done, () => { });
            OSUi.StepChip(stepper, 2, "Build", "build", OSUi.StepState.Current, () => { });
            OSUi.StepChip(stepper, 3, "Train", "train", OSUi.StepState.Upcoming, () => { });
            OSUi.StepChip(stepper, 4, "Race", "drive", OSUi.StepState.Locked, () => { });

            RectTransform card = OSUi.Card(col);
            OSUi.Tag(card, "Plan v2");
            OSUi.Pair(card, "Milestones", "3 of 7 done");
            OSUi.Pair(card, "Next", "A clean lap at 2 m/s or more");
            OSUi.ProgressRail(card, 3f / 7f);

            OSUi.ListRow(col, "RC truck", "Version 2 · driven today", "truck", null, () => { });
            OSUi.ListRow(col, "Lap time", null, null, "41.2 s");

            RectTransform row = OSUi.Row(col, 120f, ForgeTheme.Space4);
            RectTransform floating = OSUi.Surface(row, OSSurface.Floating, 20f, 6f);
            OSUi.Text(floating, "Floating: toasts, confirms", OSUi.BodyText);
            RectTransform selected = OSUi.Surface(row, OSSurface.Selected, 20f, 6f);
            OSUi.Text(selected, "Selected", OSUi.BodyText);

            OSUi.EmptyState(col, "No runs yet", "Record two laps, then train.", "train");
        }
    }
}
