using System;
using System.Globalization;
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
        public const float PanelWidth = 420f;  // mm
        const float ContentWidth = PanelWidth - 2f * ForgeTheme.PanelInset;
        const float Chrome = ForgeTheme.PanelHeader + 14f + 16f;  // header and insets around ForgePanel's content
        const float Gap = 14f, StatHeight = 30f, SliderHeight = 56f, PillRow = ForgeTheme.PillHeight;

        public static GameObject Build(JObject n, ArtifactBuild build)
        {
            var panel = build.Host.NewPanel(n.Value<string>("title") ?? build.Host.OpenTitle);
            float y = 0f;
            float rowX = 0f;  // buttons sit side by side until something else comes along
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
                        case "note": y += Note(panel.Content, y, widget.Value<string>("text")) + Gap; break;
                        case "stat": y += Row(panel.Content, y, widget.Value<string>("label"), ArtifactLook.NoValue, widget.Value<string>("unit")) + Gap; break;
                        case "runs": y += Row(panel.Content, y, "Runs", "None yet", null) + Gap; break;
                        case "slider": y += Slider(panel, y, widget.Value<string>("input"), build) + Gap; break;
                        case "button":
                            rowX += Pill(panel.Content, y, rowX, widget.Value<string>("label"), widget.Value<string>("id"), build) + 10f;
                            break;
                    }
                }
            if (rowX > 0f) y += PillRow + Gap;
            panel.SizeMeters = new Vector2(PanelWidth, Chrome + Mathf.Max(y - Gap, 60f)) * ForgePanel.MetersPerUnit;
            return panel.gameObject;
        }

        /// <summary>Plain sentences in a panel: the permissions sheet and "this can't open" messages use it too.</summary>
        public static float Note(RectTransform parent, float y, string message, bool strong = false)
        {
            var text = Text(parent, "Note", 15f, strong ? ForgeTheme.PanelText : ForgeTheme.PanelSubtext, TextAlignmentOptions.TopLeft);
            text.textWrappingMode = TextWrappingModes.Normal;
            text.text = message;
            float height = text.GetPreferredValues(message, ContentWidth, 0f).y;
            Place(text.rectTransform, y, height);
            return height;
        }

        public static float PanelHeight(float contentHeight) => Chrome + contentHeight;

        static float Row(RectTransform parent, float y, string label, string value, string unit)
        {
            var left = Text(parent, "Label", 15f, ForgeTheme.PanelSubtext, TextAlignmentOptions.Left);
            left.text = label;
            Place(left.rectTransform, y, StatHeight);
            var right = Text(parent, "Value", 19f, ForgeTheme.PanelText, TextAlignmentOptions.Right);
            right.text = string.IsNullOrEmpty(unit) ? value : value + " " + unit;
            Place(right.rectTransform, y, StatHeight);
            return StatHeight;
        }

        /// <summary>A pill at <paramref name="x"/> along a row. Returns its width.</summary>
        public static float Pill(RectTransform parent, float y, float x, string label, Action onClick)
        {
            var pill = ForgePill.Create(parent, label, () => onClick());
            var rt = pill.Rect;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            return rt.sizeDelta.x;
        }

        static float Pill(RectTransform parent, float y, float x, string label, string id, ArtifactBuild build) =>
            Pill(parent, y, x, label, () => build.Host.Fire(id));

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

            void Show() => value.text = inputs.Value(input).ToString(inputs.Whole(input) ? "0" : "0.00", CultureInfo.InvariantCulture);
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
}
