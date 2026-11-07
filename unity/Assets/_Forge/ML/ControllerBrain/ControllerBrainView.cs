using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Forge.Controllers;

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
        TMP_Text trainingText;
        TMP_Text performanceText;
        readonly TMP_Text[] inputValues = new TMP_Text[8];
        readonly Image[] inputBars = new Image[8];
        TMP_Text steerValue, throttleValue;
        TMP_Text controllerStatus;
        TMP_InputField maxSpeedInput;
        Image steerBar, throttleBar, trainProgress;
        MLCurve sparkline;
        readonly List<float> lossHistory = new List<float>();
        readonly List<float[]> samples = new List<float[]>();
        readonly List<float[]> outputs = new List<float[]>();
        Coroutine training;
        float sampleClock;
        int sampleIndex;
        BrainMode mode;
        JArray laps;
        string[] sensors;
        int recordedSamples;
        float fpsClock;
        int fpsFrames;
        bool initialized;

        enum BrainMode { Ready, Recording, Training, Autopilot }

        public string Mode => mode.ToString().ToLowerInvariant();
        public int SampleCount => samples.Count;

        void Start()
        {
            if (initialized) return;
            initialized = true;
            root = GetComponent<ForgePanel>().Content;
            LoadFixture();
            BuildView();
            SetMode(BrainMode.Ready);
            RefreshSample();
        }

        void Update()
        {
            fpsFrames++;
            fpsClock += Time.unscaledDeltaTime;
            if (fpsClock >= 0.5f)
            {
                float fps = fpsFrames / Mathf.Max(0.001f, fpsClock);
                if (performanceText != null) performanceText.text = $"Frame monitor  ·  {fps:0} FPS  ·  Quest target 72";
                fpsClock = 0; fpsFrames = 0;
            }
            if (mode == BrainMode.Training) return;
            sampleClock += Time.unscaledDeltaTime;
            if (sampleClock < SampleSeconds) return;
            sampleClock = 0;
            sampleIndex = (sampleIndex + 1) % Mathf.Max(1, samples.Count);
            if (mode == BrainMode.Recording && samples.Count > 0)
            {
                samples.Add((float[])samples[sampleIndex].Clone());
                outputs.Add((float[])outputs[sampleIndex].Clone());
                recordedSamples++;
                if (lapText != null) lapText.text = $"Recording lap  ·  {recordedSamples} samples  ·  ray + poke controls ready";
            }
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
                string channel = i < 7 ? "ray_front." + (sensors != null && i < sensors.Length ? sensors[i] : InputChannels[i]) : "speed.forward";
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
            MLUI.Caps(root, "CONTROLLER FILE", outputX + 14, 214, 150);
            MLUI.Text(root, "truck_autopilot  ·  max_speed", outputX + 14, 231, 170, 18, 9).color = MLUI.Sub;
            maxSpeedInput = MLUI.Input(root, "max_speed", "1.5", outputX + 184, 224, 54, 26);
            maxSpeedInput.text = "1.5";
            MLUI.Button(root, "Apply", outputX + 244, 224, 58, 26, ApplyMaxSpeed);
            controllerStatus = MLUI.Text(root, "ML A hot reload when the Thing runtime is open", outputX + 14, 252, width * .52f - 28, 14, 8.5f);
            controllerStatus.color = MLUI.Faint;

            MLUI.Caps(root, "CONTROLLER TRACE", 2, 270, 180);
            MLUI.Well(root, 0, 292, width, 92, 10);
            var chart = MLUI.Rect("Loss sparkline", root, 12, 314, width - 24, 56).gameObject;
            chart.AddComponent<CanvasRenderer>(); sparkline = chart.AddComponent<MLCurve>(); sparkline.externalColor = MLUI.Train;
            sparkline.SetExternalValues(lossHistory);
            lossText = MLUI.Text(root, "loss  —", 14, 298, 120, 14, 9); lossText.color = MLUI.Sub;
            sampleText = MLUI.Text(root, "sample 0", width - 142, 298, 128, 14, 9); sampleText.alignment = TextAlignmentOptions.Right; sampleText.color = MLUI.Sub;

            float buttonY = 402;
            MLUI.Button(root, "Record lap", 2, buttonY, 132, 30, ToggleRecording);
            MLUI.Button(root, "Train", 142, buttonY, 112, 30, StartTraining);
            MLUI.Button(root, "Autopilot", 262, buttonY, 132, 30, ToggleAutopilot);
            lapText = MLUI.Text(root, "Mock lap fixture  ·  2 completed laps", 2, 442, width - 4, 16, 10); lapText.color = MLUI.Faint;
            trainingText = MLUI.Text(root, "Hands only  ·  ray + poke  ·  training takes about 10 s", 2, 456, width - 4, 16, 9); trainingText.color = MLUI.Sub;
            performanceText = MLUI.Text(root, "Frame monitor  ·  waiting  ·  Quest target 72", 2, 482, width - 4, 16, 8.5f); performanceText.color = MLUI.Faint;
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
            if (on) recordedSamples = 0;
            if (lapText != null) lapText.text = mode == BrainMode.Recording ? "Recording lap  ·  0 samples  ·  ray + poke controls ready" : $"Recorded lap  ·  {recordedSamples} samples  ·  2 completed mock laps";
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
                lossHistory.Add(loss); sparkline.SetExternalValues(lossHistory); lossText.text = "loss  " + loss.ToString("0.000");
                if (trainingText != null) trainingText.text = $"Training brain  ·  {Mathf.RoundToInt(progress * 120f)}/120 epochs  ·  {progress * 10.1f:0.0} s  ·  keep hands clear";
                trainProgress.rectTransform.sizeDelta = new Vector2((root.rect.width - 4) * progress, 6);
                yield return new WaitForSecondsRealtime(0.1f);
            }
            trainProgress.gameObject.SetActive(false); training = null; SetMode(BrainMode.Ready);
            lapText.text = "Training complete  ·  120 epochs  ·  0.006 loss  ·  ready for Autopilot";
            if (trainingText != null) trainingText.text = "Hands only  ·  ray + poke  ·  trained brain is ready to preview";
        }

        void ToggleAutopilot()
        {
            if (training != null) return;
            SetAutopilotPreview(mode != BrainMode.Autopilot);
        }

        void ApplyMaxSpeed()
        {
            if (maxSpeedInput == null || !float.TryParse(maxSpeedInput.text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float value))
            {
                if (controllerStatus != null) controllerStatus.text = "Enter a numeric max_speed in m/s";
                return;
            }
            var sandbox = UnityEngine.Object.FindAnyObjectByType<ControllersSandbox>();
            string error = sandbox == null ? null : sandbox.SetMaxSpeed(value);
            if (error != null)
            {
                if (controllerStatus != null) controllerStatus.text = "Hot reload refused  ·  " + error;
                return;
            }
            if (controllerStatus != null)
                controllerStatus.text = sandbox == null ? $"Preview max_speed  ·  {value:0.0} m/s  ·  Thing runtime not open" : $"Hot reloaded  ·  max_speed {value:0.0} m/s";
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
                ["controller"] = "truck_autopilot",
                ["max_speed_editable"] = true,
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

}
