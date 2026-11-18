using System;
using System.Collections.Generic;
using System.Globalization;
using Forge.Notebook;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.Artifacts
{
    /// <summary>
    /// Builds a <c>panel</c> node's widgets inside a <see cref="ForgePanel"/>, so they work with the mouse (DesktopRig) and
    /// Interaction SDK ray and poke. Rows stack from the top of the panel's content area.
    /// </summary>
    public static class ArtifactWidgets
    {
        public const float PanelWidth = 500f;  // mm
        const float ContentWidth = PanelWidth - 2f * ForgeTheme.PanelInset;
        const float Chrome = ForgeTheme.PanelHeader + 14f + 16f;  // header and insets around ForgePanel's content
        const float Gap = 14f, StatHeight = 30f, SliderHeight = 56f, PillRow = ForgeTheme.PillHeight;
        const float LabelWidth = 92f;  // room kept for a row's label ("v14 vs v15") before its value starts

        public static GameObject Build(JObject n, ArtifactBuild build)
        {
            var panel = build.Host.NewPanel(n.Value<string>("title") ?? build.Host.OpenTitle);
            float y = 0f;
            float rowX = 0f;  // buttons sit side by side until something else comes along
            TMP_Text hintText = null;  // a note that changes once there is a run (see "after_run")
            string hintAfter = null;
            if (n["widgets"] is JArray widgets)
                foreach (var widget in widgets)
                {
                    string type = widget.Value<string>("type");
                    if (type != "button" && rowX > 0f)
                    {
                        y += PillRow + Gap;
                        rowX = 0f;
                    }
                    switch (type)
                    {
                        case "note":
                            // The course's first line: in the app, where the course is from here goes on top (see CourseNote).
                            string noteLine = widget.Value<string>("text");
                            if (hintText == null && widget.Value<string>("after_run") != null && build.Host.CourseNote != null)
                                noteLine = build.Host.CourseNote + "\n" + noteLine;
                            y += Note(panel.Content, y, noteLine, false, out var noteText) + Gap;
                            if (widget.Value<string>("after_run") != null) { hintText = noteText; hintAfter = widget.Value<string>("after_run"); }
                            break;
                        case "stat": y += Stat(panel.Content, y, widget, build) + Gap; break;
                        case "runs": y += Runs(panel.Content, y, widget, build) + Gap; break;
                        case "versions": y += Versions(panel.Content, y, build) + Gap; break;
                        case "slider": y += Slider(panel, y, widget.Value<string>("input"), build) + Gap; break;
                        case "button":
                            float width = Button(panel.Content, y, rowX, widget, build, out var pillRect);
                            if (rowX > 0f && rowX + width > ContentWidth)
                            {
                                y += PillRow + 10f;
                                rowX = 0f;
                                pillRect.anchoredPosition = new Vector2(0f, -y);
                            }
                            rowX += width + 10f;
                            break;
                    }
                }
            if (rowX > 0f) y += PillRow + Gap;
            // Resolve at press time: glTF loads asynchronously, after the panel has been built.
            if (build.Assets.Count > 0)
            {
                // A model brought in with model_import isn't authored in named parts: the explorer builds them from its node
                // tree, so Separate and X-ray show once it has loaded and turns out to have parts. It also gets the credit
                // its licence asks for (CC BY), from the manifest.
                string credit = ModelImport.Credits(build.Host.OpenManifest);
                y += Note(panel.Content, y, credit != null
                    ? "With your hands: pinch the model to pick it up and turn it, and pinch with both hands to resize it. If it comes apart, Separate spreads its parts and you can pinch one out."
                    : "With your hands: pinch the model to pick it up and turn it, pinch with both hands to resize it, and once it's separated, pinch a part to take it out. The buttons cut it open, separate its parts, or see through the outer layers.") + Gap;
                // Each view pill says what a press does next, like the scene's own toggles (Hide labels / Show labels);
                // X-ray lights up while it's on. Reset view puts all three back.
                ForgePill cutPill = null, xrayPill = null, apartPill = null;
                // Voice and hands change the view too: the pills follow the explorer, not just their own presses.
                Action<ArtifactExplorer> sync = e =>
                {
                    SetLabel(cutPill, e.Sectioned ? "Make whole" : "Cut open");
                    SetLabel(apartPill, e.Separated ? "Assemble" : "Separate");
                    if (xrayPill != null) xrayPill.On = e.XRay;
                };
                foreach (var node in build.Nodes.Values)
                    if (node != null && node.TryGetComponent<ArtifactGltf>(out var loading)) WhenExplorable(loading, e => e.Changed += () => sync(e));
                Action<Action<ArtifactExplorer>> inspect = action =>
                {
                    foreach (var node in build.Nodes.Values)
                        if (node != null && node.TryGetComponent<ArtifactGltf>(out var model) && model.Loaded && model.Explorer != null)
                        {
                            // The part names lay themselves out round this panel rather than under it.
                            model.Explorer.Avoid = panel.transform;
                            model.Explorer.AvoidHalfWidth = panel.SizeMeters.x * 0.5f;
                            action(model.Explorer);  // the explorer hides the scene's labels while its parts are apart
                            SetLabel(cutPill, model.Explorer.Sectioned ? "Make whole" : "Cut open");
                            SetLabel(apartPill, model.Explorer.Separated ? "Assemble" : "Separate");
                            if (xrayPill != null) xrayPill.On = model.Explorer.XRay;
                        }
                };
                float x = 0f;
                x += Pill(panel.Content, y, x, "Turn 30°", () => inspect(e => e.Turn())) + 10f;
                if (credit != null)
                {
                    // Turn, then Separate and X-ray (hidden until the model turns out to have parts); Cut open and Reset below.
                    apartPill = PillControl(panel.Content, y, x, "Assemble", () => inspect(e => e.Separate()));
                    SetLabel(apartPill, "Separate");
                    xrayPill = PillControl(panel.Content, y, x + apartPill.Rect.sizeDelta.x + 10f, "X-ray", () => inspect(e => e.ToggleXRay()));
                    apartPill.gameObject.SetActive(false);
                    xrayPill.gameObject.SetActive(false);
                    var apart = apartPill; var xray = xrayPill;
                    foreach (var node in build.Nodes.Values)
                        if (node != null && node.TryGetComponent<ArtifactGltf>(out var imported))
                            WhenExplorable(imported, e =>
                            {
                                bool hasParts = e.PartCount >= 2;
                                if (apart != null) apart.gameObject.SetActive(hasParts);
                                if (xray != null) xray.gameObject.SetActive(hasParts);
                            });
                    y += PillRow + Gap;
                    cutPill = PillControl(panel.Content, y, 0f, "Make whole", () => inspect(e => e.Section()));
                    SetLabel(cutPill, "Cut open");
                    Pill(panel.Content, y, cutPill.Rect.sizeDelta.x + 10f, "Reset view", () => inspect(e =>
                    {
                        e.ResetView();
                        var hands = e.GetComponentInParent<ArtifactHandGrab>();  // and undo what hands did to it
                        if (hands != null) hands.ResetPose();
                    }));
                    y += PillRow + Gap;
                    y += Note(panel.Content, y, credit) + Gap;
                }
                else
                {
                    cutPill = PillControl(panel.Content, y, x, "Make whole", () => inspect(e => e.Section()));  // made as wide as its longer label
                    SetLabel(cutPill, "Cut open");
                    x += cutPill.Rect.sizeDelta.x + 10f;
                    xrayPill = PillControl(panel.Content, y, x, "X-ray", () => inspect(e => e.ToggleXRay()));
                    y += PillRow + Gap;
                    apartPill = PillControl(panel.Content, y, 0f, "Separate", () => inspect(e => e.Separate()));
                    x = apartPill.Rect.sizeDelta.x + 10f;
                    Pill(panel.Content, y, x, "Reset view", () => inspect(e =>
                    {
                        e.ResetView();
                        var hands = e.GetComponentInParent<ArtifactHandGrab>();  // and undo what hands did to it
                        if (hands != null) hands.ResetPose();
                    }));
                    y += PillRow + Gap;
                }
            }
            // A room course: where courses may go (the play area) is one tap from its scoreboard, on the floor or a desk.
            if (build.Host.HasRoomCourse)
            {
                Pill(panel.Content, y, 0f, "Play area", () => PlayAreaEditor.Open(build.Host.CourseRoom, false));
                y += PillRow + Gap;
            }
            panel.SizeMeters = new Vector2(PanelWidth, Chrome + Mathf.Max(y - Gap, 60f)) * ForgePanel.MetersPerUnit;
            if (hintText != null && build.Host.RunsRow != null) build.Host.RunsRow.SetHint(hintText, hintText.text, hintAfter);
            build.Host.KeepAboveBase(panel);
            return panel.gameObject;
        }

        /// <summary>Plain sentences in a panel: the permissions sheet and "this can't open" messages use it too.</summary>
        public static float Note(RectTransform parent, float y, string message, bool strong = false) =>
            Note(parent, y, message, strong, out _);

        static float Note(RectTransform parent, float y, string message, bool strong, out TMP_Text shown)
        {
            var text = Text(parent, "Note", 15f, strong ? ForgeTheme.PanelText : ForgeTheme.PanelSubtext, TextAlignmentOptions.TopLeft);
            text.textWrappingMode = TextWrappingModes.Normal;
            text.text = message;
            float height = text.GetPreferredValues(message, ContentWidth, 0f).y;
            Place(text.rectTransform, y, height);
            shown = text;
            return height;
        }

        /// <summary>
        /// A button widget. With <c>alt_id</c> and <c>alt_label</c> it is one toggle: each press fires the current input, then the
        /// label flips to the other (Pause the beat / Start the beat). <c>primary</c> draws it as the filled main action.
        /// </summary>
        static float Button(RectTransform parent, float y, float x, JToken widget, ArtifactBuild build, out RectTransform rect)
        {
            string id = widget.Value<string>("id"), label = widget.Value<string>("label");
            string altId = widget.Value<string>("alt_id"), altLabel = widget.Value<string>("alt_label");
            bool toggle = altId != null && altLabel != null;
            bool second = false;
            ForgePill pill = null;
            Action press = () =>
            {
                build.Host.Fire(second ? altId : id);
                if (!toggle) return;
                second = !second;
                var shown = pill != null ? pill.GetComponentInChildren<TMP_Text>() : null;
                if (shown != null) shown.text = second ? altLabel : label;
            };
            // The pill is as wide as the longer of its two labels, so it doesn't jump when it flips.
            string widest = toggle && altLabel.Length > label.Length ? altLabel : label;
            pill = PillControl(parent, y, x, widest, press);
            var text = pill.GetComponentInChildren<TMP_Text>();
            if (text != null) text.text = label;
            if (widget.Value<bool?>("primary") == true) pill.On = true;
            rect = pill.Rect;
            return rect.sizeDelta.x;
        }

        public static float PanelHeight(float contentHeight) => Chrome + contentHeight;

        // The pill keeps the width it was made with, as a toggle button does.
        // Calls back with the model's explorer once its glTF has loaded (at once if it already has).
        static async void WhenExplorable(ArtifactGltf model, Action<ArtifactExplorer> then)
        {
            try { if (model.Loading != null) await model.Loading; }
            catch (Exception) { return; }
            if (model != null && model.Explorer != null) then(model.Explorer);
        }

        static void SetLabel(ForgePill pill, string label)
        {
            var shown = pill != null ? pill.GetComponentInChildren<TMP_Text>() : null;
            if (shown != null) shown.text = label;
        }

        static float Stat(RectTransform parent, float y, JToken widget, ArtifactBuild build)
        {
            string unit = widget.Value<string>("unit");
            Row(parent, y, widget.Value<string>("label"), ArtifactLook.NoValue, unit, out var value);
            build.Metrics.Show(widget.Value<string>("metric"), value, widget.Value<int?>("decimals") ?? 2, unit);
            return StatHeight;
        }

        static float Runs(RectTransform parent, float y, JToken widget, ArtifactBuild build)
        {
            Row(parent, y, "Runs", "None yet", null, out var value);
            FitBesideLabel(value);
            int n = widget["columns"] is JArray cols ? cols.Count : 0;
            var columns = new string[n];
            for (int i = 0; i < n; i++) columns[i] = (string)widget["columns"][i];
            var row = parent.gameObject.AddComponent<ArtifactRunsRow>();
            row.Bind(value, build.Host.OpenId, () => build.Host.LastLayout?.Value<string>("layout_id"), columns, widget.Value<int?>("limit") ?? 5);
            build.Host.RunsRow = row;
            row.Refresh();
            return StatHeight;
        }

        static float Versions(RectTransform parent, float y, ArtifactBuild build)
        {
            Row(parent, y, "Versions", ArtifactLook.NoValue, null, out var value, out var label);
            FitBesideLabel(value);
            var row = parent.gameObject.AddComponent<ArtifactVersionsRow>();
            row.Bind(label, value, build.Host.OpenId);
            row.Refresh();
            return StatHeight;
        }

        // A long value shrinks, then ends in "…", and never runs over its label or off the panel.
        static void FitBesideLabel(TMP_Text value)
        {
            value.margin = new Vector4(LabelWidth, 0f, 0f, 0f);
            value.enableAutoSizing = true;
            value.fontSizeMin = 12f;
            value.fontSizeMax = value.fontSize;
            value.overflowMode = TextOverflowModes.Ellipsis;
        }

        static float Row(RectTransform parent, float y, string label, string value, string unit) =>
            Row(parent, y, label, value, unit, out _, out _);

        static float Row(RectTransform parent, float y, string label, string value, string unit, out TMP_Text shown) =>
            Row(parent, y, label, value, unit, out shown, out _);

        static float Row(RectTransform parent, float y, string label, string value, string unit, out TMP_Text shown, out TMP_Text left)
        {
            left = Text(parent, "Label", 15f, ForgeTheme.PanelSubtext, TextAlignmentOptions.Left);
            left.text = label;
            Place(left.rectTransform, y, StatHeight);
            var right = Text(parent, "Value", 19f, ForgeTheme.PanelText, TextAlignmentOptions.Right);
            right.text = string.IsNullOrEmpty(unit) ? value : value + " " + unit;
            Place(right.rectTransform, y, StatHeight);
            shown = right;
            return StatHeight;
        }

        /// <summary>A pill at <paramref name="x"/> along a row. Returns its width.</summary>
        public static float Pill(RectTransform parent, float y, float x, string label, Action onClick) =>
            Pill(parent, y, x, label, onClick, out _);

        /// <summary>A pill at <paramref name="x"/> along a row, returned so the caller can switch it on (the near-white primary look).</summary>
        public static ForgePill PillControl(RectTransform parent, float y, float x, string label, Action onClick)
        {
            var pill = ForgePill.Create(parent, label, () => onClick());
            var rt = pill.Rect;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            return pill;
        }

        static float Pill(RectTransform parent, float y, float x, string label, Action onClick, out RectTransform rt)
        {
            var pill = ForgePill.Create(parent, label, () => onClick());
            rt = pill.Rect;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            return rt.sizeDelta.x;
        }

        static float Slider(ForgePanel panel, float y, string input, ArtifactBuild build)
        {
            var inputs = build.Inputs;
            var parent = panel.Content;
            var label = Text(parent, "Label", 15f, ForgeTheme.PanelSubtext, TextAlignmentOptions.Left);
            label.text = inputs.Label(input);
            Place(label.rectTransform, y, 24f);
            var value = Text(parent, "Value", 17f, ForgeTheme.PanelText, TextAlignmentOptions.Right);
            Place(value.rectTransform, y, 24f);

            // A tall invisible track, so a hand doesn't have to hit a 6 mm rail.
            var track = NewRect("Track", parent);
            Place(track, y + 24f, SliderHeight - 24f);
            var hit = track.gameObject.AddComponent<ForgeSurface>();
            hit.color = new Color(1f, 1f, 1f, 0.004f);

            var rail = Surface(track, "Rail", new Color(1f, 1f, 1f, 0.14f));
            Band(rail.rectTransform, 6f);
            var fillArea = NewRect("Fill Area", track);
            Band(fillArea, 6f);
            var fill = Surface(fillArea, "Fill", panel.Accent);
            var handleArea = NewRect("Handle Slide Area", track);
            Band(handleArea, 18f);
            handleArea.offsetMin = new Vector2(9f, handleArea.offsetMin.y);
            handleArea.offsetMax = new Vector2(-9f, handleArea.offsetMax.y);
            var knob = Surface(handleArea, "Knob", Color.white);
            knob.rectTransform.sizeDelta = new Vector2(18f, 0f);
            knob.Glow = ForgeTheme.HoverGlow;  // design system: the thumb sits a little above the rail
            fill.Glow = 0.3f;

            var slider = track.gameObject.AddComponent<Slider>();
            slider.transition = Selectable.Transition.None;
            slider.navigation = new Navigation { mode = Navigation.Mode.None };
            slider.targetGraphic = hit;
            slider.fillRect = fill.rectTransform;
            slider.handleRect = knob.rectTransform;
            slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            slider.wholeNumbers = inputs.Whole(input);
            slider.minValue = (float)inputs.Min(input);
            slider.maxValue = (float)inputs.Max(input);
            slider.SetValueWithoutNotify((float)inputs.Value(input));

            // An input named like spacing_m is in metres (as the runs row reads _s as seconds), so its value says so.
            string unit = input != null && input.EndsWith("_m", StringComparison.Ordinal) ? " m" : "";
            void Show() => value.text = inputs.Value(input).ToString(inputs.Whole(input) ? "0" : "0.00", CultureInfo.InvariantCulture) + unit;
            Show();
            slider.onValueChanged.AddListener(v =>
            {
                // A slider can land a hair outside its range in float, and that isn't the user's mistake.
                inputs.TrySet(input, Math.Min(Math.Max(v, inputs.Min(input)), inputs.Max(input)));
                Show();
            });
            return SliderHeight;
        }

        static ForgeSurface Surface(RectTransform parent, string name, Color color)
        {
            var rt = NewRect(name, parent);
            Stretch(rt);
            var surface = rt.gameObject.AddComponent<ForgeSurface>();
            surface.Pill = true;
            surface.color = color;
            surface.raycastTarget = false;
            return surface;
        }

        static void Band(RectTransform rt, float height)
        {
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(0f, height);
            rt.anchoredPosition = Vector2.zero;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        static void Place(RectTransform rt, float y, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, height);
            rt.anchoredPosition = new Vector2(0f, -y);
        }

        static RectTransform NewRect(string name, RectTransform parent)
        {
            var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rt.SetParent(parent, false);
            return rt;
        }

        static TextMeshProUGUI Text(RectTransform parent, string name, float size, Color color, TextAlignmentOptions alignment)
        {
            var text = NewRect(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
            if (ForgeTheme.Font != null) text.font = ForgeTheme.Font;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            return text;
        }
    }

    /// <summary>The <c>runs</c> widget: a short line of recent records. Like-for-like with this layout; anything else is labelled.</summary>
    public sealed class ArtifactRunsRow : MonoBehaviour
    {
        TMP_Text text;
        string artifactId;
        Func<string> layoutId;
        string[] columns;
        int limit;
        TMP_Text hint;
        string hintBefore, hintAfter;

        /// <summary>A note above that says what to do while there is no run, and something else once there is one.</summary>
        public void SetHint(TMP_Text note, string before, string after)
        {
            hint = note;
            hintBefore = before;
            hintAfter = after;
            Refresh();
        }

        public void Bind(TMP_Text value, string id, Func<string> layout, string[] cols, int shown)
        {
            text = value;
            artifactId = id;
            layoutId = layout;
            columns = cols ?? Array.Empty<string>();
            limit = shown;
        }

        public void Refresh()
        {
            if (text == null) return;
            var runs = ArtifactRuns.List(artifactId);
            if (hint != null) hint.text = hintBefore;
            if (runs.Count == 0) { text.text = "None yet"; return; }
            int shown = 0;
            string current = layoutId?.Invoke();
            var parts = new System.Text.StringBuilder();
            for (int i = runs.Count - 1; i >= 0 && shown < limit; i--)
            {
                // Runs on another course say nothing about this one, so they are not listed here.
                if (current != null && (string)runs[i]["layout_id"] != current) continue;
                shown++;
                if (parts.Length > 0) parts.Append(" · ");
                var run = runs[i];
                string outcome = (string)run["outcome"];
                parts.Append(Summary(run));
                if (outcome == "failed") parts.Append(' ').Append(((string)run["reason"] ?? "failed").Replace('_', ' '));
                else if (outcome == "aborted") parts.Append(" stop");
            }
            text.text = parts.Length > 0 ? parts.ToString() : "None on this course";
            if (hint != null && parts.Length > 0) hint.text = hintAfter;
        }

        string Summary(JObject run)
        {
            var metrics = run["metrics"] as JObject;
            if (metrics == null) return (string)run["run_id"];
            string col = columns.Length > 0 ? columns[0] : "completion_time_s";
            var v = metrics[col];
            if (v == null || v.Type == JTokenType.Null) return ArtifactLook.NoValue;
            if (col.EndsWith("_s")) return ((double)v).ToString("0.0", CultureInfo.InvariantCulture) + "s";
            return v.Type == JTokenType.Integer ? v.ToString() : ((double)v).ToString("0.0", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// The <c>versions</c> widget: the open project's previous version against its current one, from the notebook
    /// (<see cref="NotebookStore.CompareVersions"/>, C-12 question 7). A version that flipped before this course existed
    /// shows as a free drive, never as a run on the course. Refreshes when an entry changes or a version is cut.
    /// </summary>
    public sealed class ArtifactVersionsRow : MonoBehaviour
    {
        TMP_Text label, text;
        string artifactId;

        public string Shown => text != null ? text.text : null;

        public void Bind(TMP_Text labelText, TMP_Text value, string id)
        {
            label = labelText;
            text = value;
            artifactId = id;
        }

        void OnEnable()
        {
            NotebookStore.EntryUpdated += OnNotebook;
            NotebookStore.EntryCreated += OnNotebook;
            NotebookStore.VersionCut += OnNotebook;
        }

        void OnDisable()
        {
            NotebookStore.EntryUpdated -= OnNotebook;
            NotebookStore.EntryCreated -= OnNotebook;
            NotebookStore.VersionCut -= OnNotebook;
        }

        void OnNotebook(JObject _) => Refresh();

        public void Refresh()
        {
            if (text == null) return;
            int current = NotebookStore.IsOpen ? NotebookStore.CurrentVersion() : 0;
            if (current < 2)
            {
                label.text = "Versions";
                text.text = NotebookStore.IsOpen ? (current == 1 ? "Only v1 so far" : "No versions yet") : "No project open";
                return;
            }
            label.text = $"v{current - 1} vs v{current}";
            var compare = NotebookStore.CompareVersions(current - 1, current, artifactId);
            string from = Side((JObject)compare["from"]), to = Side((JObject)compare["to"]);
            // Nothing to compare yet: say so once instead of twice.
            text.text = from.EndsWith(" no run yet", StringComparison.Ordinal) && to.EndsWith(" no run yet", StringComparison.Ordinal)
                ? "No runs yet" : from + "  ·  " + to;
        }

        /// <summary>One version in a few words, from the records only.</summary>
        public static string Side(JObject side)
        {
            string name = "v" + side["version"];
            if ((string)side["label"] == "free drive, not this course")
            {
                string kind = (string)(side["incidents"] as JArray)?[0]?["kind"];
                return name + (kind == "rolled_over" ? " rolled over (free drive)" : " free drive only");
            }
            string failedReason = null;
            bool anyRun = false;
            if (side["runs"] is JArray runs)
                for (int i = runs.Count - 1; i >= 0; i--)
                {
                    var run = runs[i];
                    if ((string)run["label"] == "different course") continue;
                    anyRun = true;
                    var time = run["metrics"]?["completion_time_s"];
                    if ((string)run["outcome"] == "completed" && time != null && time.Type != JTokenType.Null)
                        return name + " " + ((double)time).ToString("0.0", CultureInfo.InvariantCulture) + " s";
                    if (failedReason == null && (string)run["outcome"] == "failed") failedReason = (string)run["reason"] ?? "failed";
                }
            if (failedReason != null) return name + " failed (" + failedReason.Replace('_', ' ') + ")";
            if (anyRun) return name + " no finished run";
            string outcome = (string)side["outcome"];
            return name + (outcome == "failed" ? " failed" : outcome == "worked" ? " worked" : " no run yet");
        }
    }
}
