using Forge.OS;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.Controllers
{
    /// <summary>
    /// The Truck design window (id "truckdesign"), an OS window (a <see cref="ForgePanel"/>, so mouse, ray and poke all work):
    /// a slider for each of <see cref="TruckDesign.Params"/>, a live readout of how it drives ("top speed 3.0 m/s · turns in
    /// 0.24 m · tips over above 1.3 m/s in a tight turn"), what changed since the saved version, the autopilot's honesty note
    /// when it learned on another design, and Save as version / Back to saved / Stock truck. A slider changes the truck on the
    /// course at once (<see cref="TruckDesignStore.Set"/>); Save cuts a version (<see cref="TruckDesignStore.SaveVersion"/>)
    /// and Albie says what changed in driving. Rebuilt text only on a change; nothing per frame but a cheap poll.
    /// Open it with <see cref="Open"/> (the Drive window, the project's Design step, or <c>truck_design_show</c>).
    /// </summary>
    public sealed class TruckDesignPanel : MonoBehaviour
    {
        public const string WindowId = "truckdesign";
        static readonly Vector2 SizeMeters = new Vector2(0.64f, 0.62f);

        readonly Slider[] sliders = new Slider[TruckDesign.Params.Length];
        readonly TMP_Text[] readouts = new TMP_Text[TruckDesign.Params.Length];
        TMP_Text heading, readout, changes, status;
        bool applying, pending;
        float nextPoll, nextRefresh;

        /// <summary>Opens the Truck design window (or brings it forward). Null when this scene has no OS shell.</summary>
        public static OSWindow Open()
        {
            OSShell shell = OSShell.Current;
            if (shell == null) return null;
            return shell.Windows.Open(WindowId, "Truck design", SizeMeters, Build, "RC");
        }

        public static void Close()
        {
            OSShell shell = OSShell.Current;
            if (shell != null) shell.Windows.Close(WindowId);
        }

        public static bool IsOpen => OSShell.Current != null && OSShell.Current.Windows.IsOpen(WindowId);

        static void Build(ForgePanel panel) => panel.gameObject.AddComponent<TruckDesignPanel>().Init(panel);

        void Init(ForgePanel panel)
        {
            RectTransform body = OSUi.Body(panel);
            if (body == null) return;
            RectTransform column = OSUi.Column(body, 12f);
            heading = OSUi.Text(column, "", OSUi.TitleText);
            heading.fontStyle = FontStyles.Bold;
            readout = OSUi.Text(column, "", OSUi.BodyText);

            // Two columns of sliders: the chassis on the left, the running gear on the right.
            int half = (TruckDesign.Params.Length + 1) / 2;
            RectTransform pair = OSUi.Row(column, half * 92f, 32f);
            pair.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.UpperLeft;
            RectTransform left = SliderColumn(pair), right = SliderColumn(pair);
            var design = TruckDesignStore.Current;
            for (int i = 0; i < TruckDesign.Params.Length; i++)
            {
                int index = i;
                var p = TruckDesign.Params[i];
                Slider slider = OSUi.Slider(i < half ? left : right, p.Label, p.Min, p.Max, design.Values[i], v => OnSlider(index, v));
                if (slider == null) continue;
                slider.wholeNumbers = p.Step >= 1f && Mathf.Approximately(p.Step, 1f);
                sliders[i] = slider;
                // OSUi's readout shows the raw value: take it over to show the snapped value with its unit.
                Transform line = slider.transform.parent != null && slider.transform.parent.childCount > 0 ? slider.transform.parent.GetChild(0) : null;
                readouts[i] = line != null && line.childCount > 1 ? line.GetChild(1).GetComponent<TMP_Text>() : null;
                if (readouts[i] != null) readouts[i].GetComponent<LayoutElement>().minWidth = 120f;
            }

            changes = OSUi.Text(column, "", OSUi.SmallText, true);
            RectTransform buttons = OSUi.Row(column, OSUi.ButtonHeight, OSUi.MinGap);
            OSUi.Button(buttons, "Save as version", Save, OSButtonStyle.Primary);
            OSUi.Button(buttons, "Back to saved", () => { TruckDesignStore.RevertToSaved(); Status("Back to the saved truck."); });
            OSUi.Button(buttons, "Stock truck", () => { TruckDesignStore.ResetToStock(); Status("Back to the stock truck (save to keep it as a version)."); });
            status = OSUi.Text(column, "", OSUi.SmallText, true);

            TruckDesignStore.Changed += MarkPending;
            Refresh();
        }

        void MarkPending() => pending = true;

        static RectTransform SliderColumn(RectTransform parent)
        {
            RectTransform c = OSUi.Column(parent, 12f);
            var layout = c.gameObject.AddComponent<LayoutElement>();
            layout.flexibleWidth = 1f;
            layout.minWidth = 300f;
            return c;
        }

        void OnDestroy() => TruckDesignStore.Changed -= MarkPending;

        void Update()
        {
            float now = Time.unscaledTime;
            // A drag changes the design every frame: the text is rebuilt at most ten times a second.
            if (pending && now >= nextRefresh) { pending = false; nextRefresh = now + 0.1f; Refresh(); }
            if (now < nextPoll) return;
            nextPoll = now + 1f;
            TruckDesignStore.Poll();  // a project switch while the window is open shows that project's truck
        }

        void OnSlider(int index, float value)
        {
            if (applying) return;
            TruckDesignStore.Set(index, value);
        }

        void Save()
        {
            int v = TruckDesignStore.SaveVersion(out string summary, out int projectVersion);
            if (v <= 0) { Status(summary); return; }
            string note = TruckDesignTools.AutopilotNote();
            string line = summary + (projectVersion > 0 ? " (project version " + projectVersion + ")" : "") + (note != null ? " " + note : "");
            Status(line);
            FirstRunGuide.Say(summary + (note != null ? " " + note : ""));
        }

        void Status(string text)
        {
            if (status != null) status.text = text ?? "";
        }

        // Everything shown from the store: the sliders (without calling back), their values, the readout, the changes.
        void Refresh()
        {
            if (readout == null) return;
            var design = TruckDesignStore.Current;
            applying = true;
            for (int i = 0; i < sliders.Length; i++)
            {
                if (sliders[i] != null && !Mathf.Approximately(sliders[i].value, design.Values[i])) sliders[i].SetValueWithoutNotify(design.Values[i]);
                if (readouts[i] != null) readouts[i].text = TruckDesign.Format(i, design.Values[i]);
            }
            applying = false;
            var p = design.Derive();
            int version = TruckDesignStore.Version;
            bool edited = TruckDesignStore.Edited;
            heading.text = "RC truck" + (version > 0 ? " v" + version : " (stock)") + (edited ? " · edited, not saved" : "");
            readout.text = Capital(TruckDesign.Readout(p));
            string diff = edited ? TruckDesign.Compare(TruckDesignStore.Saved, design) : "";
            string note = TruckDesignTools.AutopilotNote();
            string lift = design.ChassisHeightM > design.Values[TruckDesign.RideHeight] * 0.001f + 1e-4f
                ? "The big wheels lift the chassis to " + Mathf.RoundToInt(design.ChassisHeightM * 1000f) + " mm. " : "";
            changes.text = (string.IsNullOrEmpty(diff) ? "Drag a slider: the truck on the course changes as you go." : "Since the saved truck: " + diff)
                         + (lift.Length > 0 ? "\n" + lift : "") + (note != null ? "\n" + note : "");
        }

        static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
