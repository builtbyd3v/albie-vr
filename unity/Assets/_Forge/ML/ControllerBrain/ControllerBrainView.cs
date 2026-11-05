using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.ML
{
    /// <summary>
    /// The controller brain view for the learned Thing demo. It deliberately consumes the C-19 lap fixture rather
    /// than owning the controller runtime: ML A can replace the sample source with ControllerIO events later without
    /// changing the view. The fixture makes the view useful with the hub and headset disconnected.
    /// </summary>
    [RequireComponent(typeof(ForgePanel))]
    public sealed class ControllerBrainView : MonoBehaviour
    {
        const string LapsResource = "Mocks/controllers/laps/truck_v2";
        const float SampleSeconds = 0.08f;
        static readonly string[] InputLabels = { "ray left", "ray left", "ray left", "ray ahead", "ray right", "ray right", "ray right", "speed" };
        static readonly string[] InputChannels = { "d0", "d1", "d2", "d3", "d4", "d5", "d6", "forward" };

        RectTransform root;
        TMP_Text modeText, sampleText, lossText, lapText;
        readonly TMP_Text[] inputValues = new TMP_Text[8];
        readonly Image[] inputBars = new Image[8];
        TMP_Text steerValue, throttleValue;
        Image steerBar, throttleBar, trainProgress;
        BrainSparkline sparkline;
        readonly List<float> lossHistory = new List<float>();
        readonly List<float[]> samples = new List<float[]>();
        readonly List<float[]> outputs = new List<float[]>();
        Coroutine training;
        float sampleClock;
        int sampleIndex;
        BrainMode mode;
        JArray laps;
        string[] sensors;

        enum BrainMode { Ready, Recording, Training, Autopilot }

        public string Mode => mode.ToString().ToLowerInvariant();
        public int SampleCount => samples.Count;

        void Start()
        {
            root = GetComponent<ForgePanel>().Content;
            LoadFixture();
            BuildView();
            SetMode(BrainMode.Ready);
            RefreshSample();
        }

        void Update()
        {
            if (mode == BrainMode.Training) return;
            sampleClock += Time.unscaledDeltaTime;
            if (sampleClock < SampleSeconds) return;
            sampleClock = 0;
            sampleIndex = (sampleIndex + 1) % Mathf.Max(1, samples.Count);
            RefreshSample();
        }

        void LoadFixture()
        {
            var text = Resources.Load<TextAsset>(LapsResource);
            if (text == null) { Debug.LogWarning("[ML] Missing controller lap fixture: " + LapsResource); return; }
            try
            {
                var json = JObject.Parse(text.text);
                sensors = json["sensors"]?.ToObject<string[]>() ?? new string[8];
                laps = json["laps"] as JArray;
                if (laps == null) return;
                foreach (var lap in laps)
                {
                    var x = lap["x"] as JArray; var y = lap["y"] as JArray;
                    if (x == null || y == null) continue;
                    for (int i = 0; i < Mathf.Min(x.Count, y.Count); i++)
                    {
                        samples.Add(x[i].ToObject<float[]>());
                        outputs.Add(y[i].ToObject<float[]>());
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[ML] Bad controller lap fixture: " + e.Message); }
        }

        void BuildView()
        {
            float width = root.rect.width;
            MLUI.Caps(root, "CONTROLLER BRAIN  ·  TRUCK V2", 2, 0, width - 4);
            modeText = MLUI.Text(root, "READY", width - 120, 2, 118, 16, 10); modeText.alignment = TextAlignmentOptions.Right;
            modeText.color = MLUI.Faint;

            MLUI.Card(root, 0, 28, width * .48f - 6, 230, 12);
            MLUI.Caps(root, "INPUTS  ·  7 RAY SENSORS + SPEED", 14, 42, width * .48f - 28);
            float rowY = 68;
            for (int i = 0; i < 8; i++)
            {
                string channel = i < 7 ? "ray_front." + InputChannels[i] : "speed.forward";
                var label = MLUI.Text(root, InputLabels[i] + "\n<size=70%>" + channel + "</size>", 14, rowY, 120, 28, 10);
                label.color = i == 3 ? MLUI.TextColor : MLUI.Sub;
                MLUI.Well(root, 138, rowY + 5, width * .48f - 190, 12, 5);
                inputBars[i] = MLUI.Fill(root, 138, rowY + 5, 0, 12, i == 7 ? MLUI.Accent : MLUI.ClassColor(i));
                inputValues[i] = MLUI.Text(root, "—", width * .48f - 47, rowY, 34, 22, 10); inputValues[i].alignment = TextAlignmentOptions.Right;
                rowY += 20;
            }

            float outputX = width * .48f + 6;
            MLUI.Card(root, outputX, 28, width * .52f - 6, 230, 12);
            MLUI.Caps(root, "OUTPUTS  ·  ACTUATORS", outputX + 14, 42, width * .52f - 28);
            MLUI.Text(root, "The brain turns the sensor snapshot into two bounded controls.", outputX + 14, 66, width * .52f - 28, 30, 11).color = MLUI.Sub;
            steerValue = Output(outputX + 14, 104, "STEER", "steer.angle", MLUI.Train, out steerBar);
            throttleValue = Output(outputX + 14, 164, "THROTTLE", "drive.throttle", MLUI.Good, out throttleBar);

            MLUI.Caps(root, "CONTROLLER TRACE", 2, 270, 180);
            MLUI.Well(root, 0, 292, width, 92, 10);
            var chart = MLUI.Rect("Loss sparkline", root, 12, 314, width - 24, 56).gameObject;
            chart.AddComponent<CanvasRenderer>(); sparkline = chart.AddComponent<BrainSparkline>();
            lossText = MLUI.Text(root, "loss  —", 14, 298, 120, 14, 9); lossText.color = MLUI.Sub;
            sampleText = MLUI.Text(root, "sample 0", width - 142, 298, 128, 14, 9); sampleText.alignment = TextAlignmentOptions.Right; sampleText.color = MLUI.Sub;

            float buttonY = 402;
            MLUI.Button(root, "Record lap", 2, buttonY, 132, 30, ToggleRecording);
            MLUI.Button(root, "Train", 142, buttonY, 112, 30, StartTraining);
            MLUI.Button(root, "Autopilot", 262, buttonY, 132, 30, ToggleAutopilot);
            lapText = MLUI.Text(root, "Mock lap fixture  ·  2 completed laps", 2, 442, width - 4, 16, 10); lapText.color = MLUI.Faint;
            MLUI.Well(root, 2, 468, width - 4, 6, 3);
            trainProgress = MLUI.Fill(root, 2, 468, 0, 6, MLUI.Train); trainProgress.gameObject.SetActive(false);
        }

        TMP_Text Output(float x, float y, string title, string channel, Color color, out Image bar)
        {
            MLUI.Caps(root, title, x, y, 100);
            MLUI.Text(root, channel, x, y + 18, 150, 16, 10).color = MLUI.Sub;
            MLUI.Well(root, x + 158, y + 8, root.rect.width - x - 220, 14, 6);
            bar = MLUI.Fill(root, x + 158, y + 8, 0, 14, color);
            return MLUI.Text(root, "—", root.rect.width - 52, y + 2, 40, 24, 15);
        }

        void ToggleRecording()
        {
            if (training != null) return;
            SetRecording(mode != BrainMode.Recording);
        }

        public bool SetRecording(bool on)
        {
            if (training != null) return false;
            SetMode(on ? BrainMode.Recording : BrainMode.Ready);
            if (lapText != null) lapText.text = mode == BrainMode.Recording ? "Recording mock lap 1  ·  samples are entering the ring" : "Mock lap fixture  ·  2 completed laps";
            return true;
        }

        void StartTraining()
        {
            TrainPreview();
        }

        public bool TrainPreview()
        {
            if (training != null || samples.Count == 0) return false;
            training = StartCoroutine(TrainMock());
            return true;
        }

        IEnumerator TrainMock()
        {
            SetMode(BrainMode.Training); trainProgress.gameObject.SetActive(true); lossHistory.Clear();
            for (int i = 0; i <= 100; i++)
            {
                float progress = i / 100f;
                float loss = Mathf.Lerp(0.18f, 0.006f, progress * progress);
                lossHistory.Add(loss); sparkline.SetValues(lossHistory); lossText.text = "loss  " + loss.ToString("0.000");
                trainProgress.rectTransform.sizeDelta = new Vector2((root.rect.width - 4) * progress, 6);
                yield return new WaitForSecondsRealtime(0.1f);
            }
            trainProgress.gameObject.SetActive(false); training = null; SetMode(BrainMode.Ready);
            lapText.text = "Training complete  ·  120 epochs  ·  0.006 loss  ·  ready for Autopilot";
        }

        void ToggleAutopilot()
        {
            if (training != null) return;
            SetAutopilotPreview(mode != BrainMode.Autopilot);
        }

        public bool SetAutopilotPreview(bool on)
        {
            if (training != null) return false;
            SetMode(on ? BrainMode.Autopilot : BrainMode.Ready);
            if (lapText != null) lapText.text = mode == BrainMode.Autopilot ? "Autopilot preview  ·  replaying the learned controller" : "Mock lap fixture  ·  2 completed laps";
            return true;
        }

        public JObject ToolState()
        {
            return new JObject
            {
                ["mode"] = Mode,
                ["samples"] = SampleCount,
                ["sample"] = samples.Count == 0 ? 0 : sampleIndex + 1,
                ["preview_only"] = true,
                ["note"] = "The panel is using the C-19 mock lap fixture; controller attachment stays with the runtime owner."
            };
        }

        void SetMode(BrainMode next)
        {
            mode = next; modeText.text = next.ToString().ToUpperInvariant();
            modeText.color = next == BrainMode.Autopilot ? MLUI.Good : next == BrainMode.Training ? MLUI.Train : next == BrainMode.Recording ? MLUI.Warn : MLUI.Faint;
        }

        void RefreshSample()
        {
            if (samples.Count == 0) return;
            var input = samples[sampleIndex]; var output = outputs[sampleIndex];
            for (int i = 0; i < inputBars.Length && i < input.Length; i++)
            {
                float value = input[i]; inputBars[i].rectTransform.sizeDelta = new Vector2(Mathf.Max(1, (root.rect.width * .48f - 190) * Mathf.Clamp01((value + (i == 7 ? 1 : 0)) / (i == 7 ? 2 : 1))), 12);
                inputValues[i].text = value.ToString("0.00");
            }
            float steer = output.Length > 0 ? output[0] : 0, throttle = output.Length > 1 ? output[1] : 0;
            steerValue.text = steer.ToString("+0.00;-0.00;0.00"); throttleValue.text = throttle.ToString("+0.00;-0.00;0.00");
            steerBar.rectTransform.sizeDelta = new Vector2(Mathf.Max(1, 120 * Mathf.Abs(steer)), 14); throttleBar.rectTransform.sizeDelta = new Vector2(Mathf.Max(1, 120 * Mathf.Abs(throttle)), 14);
            sampleText.text = "sample " + (sampleIndex + 1) + " / " + samples.Count;
        }
    }

    /// <summary>Small allocation-free-after-build line graphic used by the controller view's live trace.</summary>
    sealed class BrainSparkline : MaskableGraphic
    {
        readonly List<float> values = new List<float>();
        public void SetValues(IList<float> next) { values.Clear(); for (int i = 0; i < next.Count; i++) values.Add(next[i]); SetVerticesDirty(); }
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear(); if (values.Count < 2) return;
            var r = rectTransform.rect; Color color = MLUI.Train; float max = 0.2f;
            for (int i = 0; i < values.Count; i++) max = Mathf.Max(max, values[i]);
            for (int i = 1; i < values.Count; i++)
            {
                Vector2 a = new Vector2(r.xMin + r.width * (i - 1) / (values.Count - 1), r.yMin + r.height * (1 - values[i - 1] / max));
                Vector2 b = new Vector2(r.xMin + r.width * i / (values.Count - 1), r.yMin + r.height * (1 - values[i] / max));
                Vector2 n = new Vector2(-(b - a).y, (b - a).x).normalized * 1.5f; int start = vh.currentVertCount;
                vh.AddVert(a - n, color, Vector2.zero); vh.AddVert(a + n, color, Vector2.zero); vh.AddVert(b + n, color, Vector2.zero); vh.AddVert(b - n, color, Vector2.zero);
                vh.AddTriangle(start, start + 1, start + 2); vh.AddTriangle(start, start + 2, start + 3);
            }
        }
    }
}
