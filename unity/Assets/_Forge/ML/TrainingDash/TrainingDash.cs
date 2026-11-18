using System;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

namespace Forge.ML
{
    /// <summary>
    /// Training as it happens: epoch, losses, accuracy and learning rate as big numbers, loss and accuracy curves (the
    /// best epoch marked once known), a banner for overfitting, the hub or the finished run, then per-class accuracy
    /// over the epochs and the probe strip. The evaluation (confusion, per-class scores) lives on the Results panel.
    /// </summary>
    [RequireComponent(typeof(ForgePanel))]
    public class TrainingDash : MonoBehaviour
    {
        public MLSession session;
        [Tooltip("Epochs for the Train button (the CIFAR-10 preset import allows 5–10).")][Range(5, 10)] public int trainEpochs = 10;
        const float Kpi = 0, ProgressY = 52, ChartY = 64, ChartHeight = 132, ImportY = 204, PresetY = 236, BannerY = 266, SectionY = 302, SummaryY = 320;
        TMP_Text epochValue, trainLossValue, valLossValue, valAccValue, lrValue, statusText, bannerLabel, bannerText, sectionTitle, lossMax, runMetrics;
        ForgeSurface progress, statusChip, banner, bannerDot;
        ForgePill train;
        TMP_InputField githubUrl;
        RectTransform presetRow, actionRow;
        string actionKey, shownSource;  // what the action row and the subtitle last showed
        bool presetsLoaded;
        MLCurve loss, accuracy;
        RectTransform root, summary;
        float width;
        readonly List<float[]> classHistory = new List<float[]>();
        readonly List<MLProbeSample> probes = new List<MLProbeSample>();
        int firstHistoryEpoch = 1;
        public int AccuracyEpochCount => classHistory.Count;
        public int ProbeCount => probes.Count;
        // The live-graph area needs room for nine readable class rows and the probe strip under them: the panel is at least
        // this tall (set in Awake, before the workspace reads the size it resets to).
        const float MinPanelHeightMeters = 0.68f;
        void Awake()
        {
            var panel = GetComponent<ForgePanel>();
            if (panel == null) return;
            Vector2 size = panel.SizeMeters;
            if (size.y < MinPanelHeightMeters) panel.SizeMeters = new Vector2(size.x, MinPanelHeightMeters);
        }
        void Start()
        {
            session = MLSession.Resolve(session); root = GetComponent<ForgePanel>().Content; width = root.rect.width;
            epochValue = MLUI.Stat(root, "Epoch", "—", 2, Kpi, 86, MLUI.TextColor);
            trainLossValue = MLUI.Stat(root, "Train loss", "—", 92, Kpi, 86, MLUI.Train);
            valLossValue = MLUI.Stat(root, "Val loss", "—", 182, Kpi, 86, MLUI.Val);
            valAccValue = MLUI.Stat(root, "Val accuracy", "—", 272, Kpi, 100, MLUI.Good);
            lrValue = MLUI.Stat(root, "LR", "—", 376, Kpi, 70, MLUI.TextColor);
            foreach (var stat in new[] { epochValue, trainLossValue, valLossValue, valAccValue, lrValue }) stat.fontSize = 21;
            statusChip = MLUI.Surface(root, width - 292, 4, 88, 22, new Color(1, 1, 1, 0.08f), 11);
            statusText = MLUI.Caps(root, "", width - 292, 8, 88, 14, 9); statusText.alignment = TextAlignmentOptions.Top;
            train = MLUI.Pill(root, "Train", width - 196, 1, 92, 28, () => session.Train(trainEpochs)); train.On = true;
            MLUI.Button(root, "Replay", width - 98, 1, 98, 28, session.Restart);
            MLUI.Surface(root, 0, ProgressY, width, 4, new Color(1, 1, 1, 0.07f), 2);
            progress = MLUI.Surface(root, 0, ProgressY, 0, 4, MLUI.Accent, 2); progress.Glow = 0.5f;

            float chart = (width - 12) * 0.5f;
            loss = Chart("Loss", 0, chart, false);
            accuracy = Chart("Accuracy", chart + 12, chart, true);
            lossMax = MLUI.Text(root, "", 12, ChartY + 24, 40, 12, 8.5f); lossMax.color = MLUI.Faint;
            var top = MLUI.Text(root, "100%", chart + 24, ChartY + 24, 40, 12, 8.5f); top.color = MLUI.Faint;
            // A run that logs neither loss nor accuracy (a backtest, a regression with only r2) shows its own numbers here.
            runMetrics = MLUI.Text(root, "", 12, ChartY + 34, width - 24, ChartHeight - 40, 13);

            // Import a public GitHub repository (POST /ml/github/import): the CIFAR-10 preset trains our checked-in copy,
            // anything else is cloned and set up on the laptop (progress in the banner), then Train runs its own code.
            githubUrl = MLUI.Input(root, "Public code repository", "Paste the repository link", 0, ImportY, width - 110, 28);
            MLUI.Button(root, "Import", width - 100, ImportY, 100, 28, () => session.ImportGitHub(githubUrl.text));
            // Preset repos from the hub (GET /ml/github/presets): one press fills the URL and imports, no typing in the headset.
            presetRow = MLUI.Rect("GitHub presets", root, 0, PresetY, width - 170, 24);
            // Same place, while setup waits on the user: Show patch / Accept / Skip, or one pill per project type.
            actionRow = MLUI.Rect("Project setup actions", root, 0, PresetY, width - 170, 24);
            actionRow.gameObject.SetActive(false);
            MLUI.Button(root, "Neural Net Lab", width - 160, PresetY, 160, 24, () => MLWorkspaceBridge.ShowLab(true));
            ForgeNet.StatusChanged += OnNetStatus;
            cloudReady = ForgeNet.On("cloud.ready", _ => OnNetStatus(ForgeNet.Status));  // hub off: Albie's cloud came up
            if (ForgeCloud.Serves("/ml/")) LoadPresets();

            // One banner line: overfitting, a hub notice, or the finished run.
            banner = MLUI.Surface(root, 0, BannerY, width, 28, Color.clear, 10, 0.5f);
            bannerDot = MLUI.Dot(banner.transform, 12, 10, 8, MLUI.Warn);
            bannerLabel = MLUI.Caps(banner.transform, "", 28, 8, 110, 14, 9.5f);
            bannerText = MLUI.Text(banner.transform, "", 128, 6, width - 140, 16, 11); bannerText.textWrappingMode = TextWrappingModes.NoWrap;
            bannerText.overflowMode = TextOverflowModes.Truncate;

            sectionTitle = MLUI.Caps(root, "", 2, SectionY, 560, 14, 9.5f);
            summary = MLUI.Rect("Live graphs", root, 0, SummaryY, width, root.rect.height - SummaryY);
            session.Changed += Refresh;
            session.Reloaded += OnSessionReloaded;
            session.Metrics += OnMetrics;
            session.ClassAccuracyUpdated += OnClassAccuracy;
            session.ProbeUpdated += OnProbe;
            if (session.UsesGeneratedMocks && !session.HubData && !session.HasReplayLiveGraphs) BuildMockProbes();
            Refresh();
        }
        MLCurve Chart(string title, float x, float w, bool acc)
        {
            MLUI.Well(root, x, ChartY, w, ChartHeight);
            MLUI.Caps(root, title, x + 12, ChartY + 9, 100);
            float key = x + w - 112;
            MLUI.Dot(root, key, ChartY + 12, 7, MLUI.Train); MLUI.Text(root, "Train", key + 10, ChartY + 8, 40, 14, 9.5f).color = MLUI.Sub;
            MLUI.Dot(root, key + 54, ChartY + 12, 7, MLUI.Val); MLUI.Text(root, "Val", key + 64, ChartY + 8, 40, 14, 9.5f).color = MLUI.Sub;
            // The CanvasRenderer first: added at runtime, the Graphic doesn't always bring one and then draws nothing.
            var go = MLUI.Rect(title + " curves", root, x + 12, ChartY + 36, w - 24, ChartHeight - 46).gameObject; go.AddComponent<CanvasRenderer>();
            var curve = go.AddComponent<MLCurve>(); curve.session = session; curve.accuracy = acc; curve.raycastTarget = false; return curve;
        }
        void Refresh()
        {
            int e = session.EpochIndex;
            var m = e >= 0 && e < session.Data.metrics.Length ? session.Data.metrics[e] : null;
            epochValue.text = m == null ? "—" : $"{e + 1}<size=60%><color=#FFFFFF80> / {session.TotalEpochs}</color></size>";
            // A metric the run didn't log is NaN (MLNullAsNaN): "—", like an epoch that hasn't happened yet.
            trainLossValue.text = Num(m?.train_loss, "0.000");
            valLossValue.text = Num(m?.val_loss, "0.000");
            valAccValue.text = Num(m?.val_acc, "0.0%");
            lrValue.text = Num(m?.lr, "G3");
            ShowRunMetrics(m);
            // Hub off and nothing trained here: the curves are the bundled sample run playing back, not a training run.
            bool sampleRun = session.replay && !session.HubData;
            string state = session.Done ? "Done" : e >= 0 ? (sampleRun ? "Replaying" : "Training") : session.NoImageData ? "Ready" : "Waiting";
            Color stateColor = session.Done ? MLUI.Good : e >= 0 ? MLUI.Accent : MLUI.Faint;
            statusText.text = state.ToUpperInvariant(); statusText.color = Color.Lerp(stateColor, Color.white, 0.2f);
            statusChip.color = new Color(stateColor.r, stateColor.g, stateColor.b, 0.16f);
            progress.rectTransform.sizeDelta = new Vector2(width * (e + 1) / Mathf.Max(1, session.TotalEpochs), 4);
            progress.color = session.Done ? MLUI.Good : MLUI.Accent;
            train.Button.interactable = !session.TrainBusy;  // a second press while starting or training does nothing
            RefreshProject();

            int best = MLReport.BestEpoch(session.Data.metrics, e);
            loss.marker = accuracy.marker = session.Done || session.Overfit ? best : -1;
            if (session.ProjectSettingUp && !string.IsNullOrEmpty(session.Notice))  // a new project's step beats the old run's banner
                Banner(MLUI.Accent, "Setup", session.Notice);
            else if (session.Overfit)
                Banner(MLUI.Warn, "Overfitting", (string.IsNullOrEmpty(session.OverfitReason) ? "validation loss rose twice while training loss fell" : session.OverfitReason)
                    + (best >= 0 ? $"  ·  best epoch {best + 1}" : ""));
            else if (!string.IsNullOrEmpty(session.Notice)) Banner(MLUI.Accent, session.IsLive ? "Hub" : "On device", session.Notice);
            else if (session.Done && best >= 0 && !float.IsNaN(session.Data.metrics[best].val_acc))
                Banner(MLUI.Good, "Finished", $"best val accuracy {session.Data.metrics[best].val_acc:0.0%} at epoch {best + 1}  ·  full evaluation on the Results panel");
            else banner.gameObject.SetActive(false);

            float max = 1;
            for (int i = 0; i <= e && i < session.Data.metrics.Length; i++)
            {
                var t = session.Data.metrics[i];
                if (t == null) continue;
                if (!float.IsNaN(t.train_loss)) max = Mathf.Max(max, t.train_loss);
                if (!float.IsNaN(t.val_loss)) max = Mathf.Max(max, t.val_loss);
            }
            lossMax.text = max.ToString("0.0");
            loss.SetVerticesDirty(); accuracy.SetVerticesDirty(); MLUI.Clear(summary);
            if (session.NoImageData) { sectionTitle.text = ""; return; }  // per-class accuracy and probes are image-classifier views
            DrawLiveGraphs();
        }
        static string Num(float? v, string format) => v == null || float.IsNaN(v.Value) ? "—" : v.Value.ToString(format);
        void ShowRunMetrics(MLMetrics tick)
        {
            bool curves = tick == null || tick.HasLoss || tick.HasAccuracy || tick.extra == null || tick.extra.Count == 0;
            if (curves) { runMetrics.text = ""; return; }
            var lines = new System.Text.StringBuilder("<color=#FFFFFF99>RUN METRICS</color>\n");
            int shown = 0;
            foreach (var pair in tick.extra)
            {
                if (pair.Value == null || shown >= 8) continue;
                bool left = shown++ % 2 == 0;  // two columns: name, then value, via TMP <pos> tabs
                lines.Append(left ? "" : "<pos=50%>").Append(Pretty(pair.Key))
                     .Append(left ? "<pos=30%>" : "<pos=80%>").Append(pair.Value.Value.ToString("0.###"));
                if (!left) lines.Append('\n');
            }
            runMetrics.text = lines.ToString();
        }
        // val_max_drawdown -> "val max drawdown"
        static string Pretty(string name) => name.Replace('_', ' ');
        void Banner(Color tone, string label, string text)
        {
            banner.gameObject.SetActive(true);
            banner.color = new Color(tone.r, tone.g, tone.b, 0.12f);
            bannerDot.color = tone; bannerLabel.text = label.ToUpperInvariant(); bannerLabel.color = tone; bannerText.text = text;
        }
        void DrawLiveGraphs()
        {
            sectionTitle.text = session.RunId == "on-device" ? "PER-CLASS ACCURACY  ·  HELD-OUT IMAGES" : session.HubData ? "PER-CLASS ACCURACY  ·  LIVE" : "PER-CLASS ACCURACY  ·  SAMPLE RUN";
            int classes = session.Data.classes.Length;
            int columns = classHistory.Count;
            // The heat map keeps every class name readable: a row is never under MinRow units, even when that leaves the
            // probe strip less room (it then drops its photos and keeps only its caption).
            const float MinRow = 14f;
            float heatHeight = Mathf.Min(150f, summary.rect.height - 100f);
            heatHeight = Mathf.Max(heatHeight, Mathf.Min(10f + 18f + classes * MinRow, summary.rect.height - 46f));
            const float labelWidth = 112;
            float top = 10, rowHeight = (heatHeight - top - 18) / Mathf.Max(1, classes);
            bool names = rowHeight >= 12f && columns > 0;
            MLUI.Well(summary, 0, 0, width, heatHeight);
            float cellWidth = Mathf.Min(40f, (width - labelWidth - 14) / Mathf.Max(1, columns));
            for (int c = 0; c < classes; c++)
            {
                if (names)
                {
                    var name = MLUI.Text(summary, HeatName(session.Data.classes[c]), 6, top + c * rowHeight + rowHeight * 0.5f - 7, labelWidth - 12, 14, 11f);
                    name.color = MLUI.Sub; name.alignment = TextAlignmentOptions.TopRight;
                }
                for (int e = 0; e < columns; e++)
                {
                    float value = classHistory[e].Length > c ? classHistory[e][c] : -1;
                    var fill = MLUI.Surface(summary, labelWidth + e * cellWidth, top + c * rowHeight,
                        Mathf.Max(2, cellWidth - 2), rowHeight - 2, HeatColor(value), 2);
                    fill.gameObject.name = $"Accuracy epoch {firstHistoryEpoch + e} class {c}";
                }
            }
            for (int e = 0; e < columns; e++)
            {
                int epoch = firstHistoryEpoch + e;
                if (e == columns - 1 || epoch % 5 == 0)
                {
                    var label = MLUI.Text(summary, epoch.ToString(), labelWidth + e * cellWidth, top + classes * rowHeight + 2, Mathf.Max(16, cellWidth), 13, 10f);
                    label.color = MLUI.Faint;
                }
            }
            if (columns == 0)
            {
                string note = session.HubData ? "Per-class results appear after the first epoch" : "Per-class results appear after you press Train";
                var wait = MLUI.Text(summary, note, 12, heatHeight * 0.5f - 8, Mathf.Max(200, width - 24), 16, 12); wait.color = MLUI.Sub;
            }

            float probeY = heatHeight + 8, probeHeight = summary.rect.height - probeY;
            bool showProbeStrip = probes.Count > 0 || session.HubData;
            if (showProbeStrip)
            {
                MLUI.Well(summary, 0, probeY, width, probeHeight);
                MLUI.Caps(summary, "Probe strip  ·  16 fixed validation images", 12, probeY + 8, 400);
            }
            if (probes.Count == 0 && showProbeStrip) { var wait = MLUI.Text(summary, "Probe images show up here once training reports them", 12, probeY + 30, Mathf.Max(260, width - 24), 16, 11); wait.color = MLUI.Sub; }
            float slotWidth = (width - 16) / 16f, imageY = probeY + 26, photo = Mathf.Min(34f, slotWidth - 8);
            bool roomForPhotos = probeHeight >= 26f + photo + 26f;
            for (int i = 0; i < Mathf.Min(16, probes.Count) && roomForPhotos; i++)
            {
                var probe = probes[i];
                int sampleIndex = FindSampleIndex(probe.id);
                if (sampleIndex < 0) continue;
                var sample = session.Data.samples[sampleIndex];
                float x = 8 + i * slotWidth;
                int predicted = probe.pred_idx;
                bool right = predicted == sample.true_idx;
                Color tone = predicted < 0 ? MLUI.Faint : right ? MLUI.Good : MLUI.Bad;
                MLUI.Surface(summary, x + (slotWidth - photo) * 0.5f - 2, imageY - 2, photo + 4, photo + 4, new Color(tone.r, tone.g, tone.b, 0.35f), 5);
                var image = MLUI.Rect("Probe " + probe.id, summary, x + (slotWidth - photo) * 0.5f, imageY, photo, photo)
                    .gameObject.AddComponent<RawImage>();
                image.texture = session.Atlas; image.uvRect = session.UV(sampleIndex); image.raycastTarget = false;
                string caption = predicted >= 0 && predicted < session.Data.classes.Length
                    ? (right ? ShortName(session.Data.classes[predicted]) : "→ " + ShortName(session.Data.classes[predicted])) : ShortName(session.Data.classes[sample.true_idx]);
                var text = MLUI.Text(summary, caption, x, imageY + photo + 4, slotWidth, 12, 8);
                text.color = tone; text.alignment = TextAlignmentOptions.Top; text.textWrappingMode = TextWrappingModes.NoWrap;
                float confidence = Confidence(probe), barWidth = slotWidth - 12;
                MLUI.Surface(summary, x + 6, imageY + photo + 18, barWidth, 3, new Color(1, 1, 1, 0.08f), 1.5f);
                MLUI.Surface(summary, x + 6, imageY + photo + 18, Mathf.Max(3, barWidth * confidence), 3, tone, 1.5f);
            }
        }
        void OnMetrics(MLMetrics tick)
        {
            if (tick == null) return;
            if (tick.epoch <= 1)
            {
                classHistory.Clear(); firstHistoryEpoch = 1;
                if (session.UsesGeneratedMocks && !session.HubData && !session.HasReplayLiveGraphs) BuildMockProbes();
            }
            if (session.UsesGeneratedMocks && !session.HubData && !session.HasReplayLiveGraphs) AdvanceMockGraphs(tick.epoch);
        }
        void OnClassAccuracy(int epoch, float[] values)
        {
            if (epoch <= 1) { classHistory.Clear(); firstHistoryEpoch = 1; }
            StoreClassAccuracy(epoch, values);
        }
        void OnProbe(MLProbePayload update)
        {
            if (update?.samples == null) return;
            probes.Clear();
            foreach (var sample in update.samples) if (sample != null && FindSampleIndex(sample.id) >= 0) probes.Add(sample);
            if (probes.Count > 16) probes.RemoveRange(16, probes.Count - 16);
        }
        void OnSessionReloaded()
        {
            classHistory.Clear(); probes.Clear(); firstHistoryEpoch = 1;
        }
        void StoreClassAccuracy(int epoch, float[] values)
        {
            if (values == null || values.Length == 0) return;
            int index = Mathf.Max(0, epoch - firstHistoryEpoch);
            while (classHistory.Count <= index)
            {
                var emptyEpoch = new float[session.Data.classes.Length];
                for (int i = 0; i < emptyEpoch.Length; i++) emptyEpoch[i] = -1f;
                classHistory.Add(emptyEpoch);
            }
            classHistory[index] = values;
            while (classHistory.Count > 24) { classHistory.RemoveAt(0); firstHistoryEpoch++; }
        }
        void BuildMockProbes()
        {
            probes.Clear();
            var samples = session.Data.samples;
            var selected = new List<MLSample>(16);
            foreach (var sample in samples) if (!sample.correct && selected.Count < 3) selected.Add(sample);
            foreach (var sample in samples) if (sample.correct && selected.Count < 16) selected.Add(sample);
            foreach (var sample in samples) if (selected.Count < 16 && !selected.Contains(sample)) selected.Add(sample);
            foreach (var sample in selected) probes.Add(new MLProbeSample { id = sample.id, pred_idx = -1 });
        }
        void AdvanceMockGraphs(int epoch)
        {
            int classCount = session.Data.classes.Length;
            var seen = new int[classCount]; var finalCorrect = new int[classCount];
            foreach (var sample in session.Data.samples)
                if (!sample.removed && sample.true_idx >= 0 && sample.true_idx < classCount)
                {
                    seen[sample.true_idx]++;
                    if (sample.correct) finalCorrect[sample.true_idx]++;
                }
            float progress = Mathf.Clamp01(epoch / (float)Mathf.Max(1, session.TotalEpochs));
            var values = new float[classCount];
            for (int c = 0; c < classCount; c++)
            {
                float final = seen[c] == 0 ? 0 : finalCorrect[c] / (float)seen[c];
                float curve = Mathf.Pow(progress, .7f + (c % 4) * .12f);
                values[c] = Mathf.Lerp(.08f, final, curve);
            }
            StoreClassAccuracy(epoch, values);

            for (int i = 0; i < probes.Count; i++)
            {
                var probe = probes[i]; int index = FindSampleIndex(probe.id); if (index < 0) continue;
                var sample = session.Data.samples[index]; bool finalIsCorrect = sample.pred_idx == sample.true_idx;
                int initialWrong = (sample.true_idx + 1 + i % Mathf.Max(1, classCount - 1)) % classCount;
                float threshold = .18f + ((i * 37) % 67) / 100f;
                int predicted = finalIsCorrect && progress >= threshold ? sample.true_idx :
                    (finalIsCorrect ? initialWrong : sample.pred_idx);
                float confidence = finalIsCorrect ? Mathf.Lerp(.42f, .96f, progress) : Mathf.Lerp(.7f, .96f, progress);
                var probs = new float[classCount];
                for (int c = 0; c < classCount; c++) probs[c] = c == predicted ? confidence : (1 - confidence) / Mathf.Max(1, classCount - 1);
                probe.pred_idx = predicted; probe.probs = probs;
            }
        }
        int FindSampleIndex(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < session.Data.samples.Length; i++) if (session.Data.samples[i].id == id) return i;
            return -1;
        }
        static string HeatName(string value) => string.IsNullOrEmpty(value) ? "?" : value.Length <= 12 ? value : value.Substring(0, 11) + "…";
        static string ShortName(string value) => string.IsNullOrEmpty(value) ? "?" : value.Length <= 9 ? value : value.Substring(0, 8) + "…";
        static float Confidence(MLProbeSample probe)
        {
            if (probe.probs == null || probe.probs.Length == 0) return .5f;
            float max = 0; foreach (float value in probe.probs) max = Mathf.Max(max, value);
            return Mathf.Clamp01(max);
        }
        // Red through amber to green, so the middle doesn't go muddy; unavailable is a faint empty cell.
        static Color HeatColor(float value)
        {
            if (value < 0) return new Color(1, 1, 1, 0.04f);
            value = Mathf.Clamp01(value);
            return value < 0.5f ? Color.Lerp(MLUI.Bad, MLUI.Warn, value * 2f) : Color.Lerp(MLUI.Warn, MLUI.Good, value * 2f - 1f);
        }
        // The setup's waiting steps get their buttons; the header says whose code a Train press runs.
        void RefreshProject()
        {
            string status = session.ProjectStatus;
            string key = status == "needs_approval" ? status : status == "confirm_type" && session.ProjectCandidates != null
                ? status + ":" + string.Join(",", session.ProjectCandidates) : null;
            if (key != actionKey)
            {
                actionKey = key;
                for (int i = actionRow.childCount - 1; i >= 0; i--) Destroy(actionRow.GetChild(i).gameObject);
                if (key == "needs_approval")
                {
                    MLUI.Pill(actionRow, "Show patch", 0, 0, 108, 24, session.ShowPatch);
                    MLUI.Pill(actionRow, "Accept", 116, 0, 86, 24, () => session.ApprovePatch(true)).On = true;
                    MLUI.Pill(actionRow, "Skip", 210, 0, 70, 24, () => session.ApprovePatch(false));
                }
                else if (key != null)
                {
                    var caption = MLUI.Caps(actionRow, "Type", 2, 6, 40, 14, 9.5f); caption.color = MLUI.Faint;
                    float x = 44;
                    foreach (var archetype in session.ProjectCandidates)
                    {
                        string type = archetype, label = archetype.Replace('_', ' ');
                        float w = 26 + label.Length * 7f;
                        if (x + w > actionRow.rect.width) break;
                        MLUI.Pill(actionRow, label, x, 0, w, 24, () => session.ConfirmProjectType(type));
                        x += w + 8;
                    }
                }
                actionRow.gameObject.SetActive(key != null);
                presetRow.gameObject.SetActive(key == null);
            }
            string source = session.ProjectSource ?? (session.replay && !session.HubData ? "Sample run  ·  bundled images" : null);
            if (source != shownSource)
            {
                shownSource = source;
                GetComponent<ForgePanel>().Subtitle = shownSource ?? "";
            }
        }
        System.IDisposable cloudReady;
        void OnNetStatus(ForgeNetStatus status) { if (ForgeCloud.Serves("/ml/") && !presetsLoaded) LoadPresets(); }
        // The hub's default presets (hub/ml/router.py DEFAULT_GITHUB_PRESETS): Albie's cloud blocks /ml/github/, so with the
        // hub off the panel offers these itself and imports them through the cloud's ml_github tool (MLSession.ImportGitHub).
        const string BuiltInPresets = "[{\"label\":\"CIFAR-10 example\",\"url\":\"https://github.com/Satyabratadas/forge-cifar10-demo\"},"
                                    + "{\"label\":\"PyTorch MNIST\",\"url\":\"https://github.com/pytorch/examples/tree/main/mnist\"}]";
        void LoadPresets()
        {
            if (ForgeNet.Status != ForgeNetStatus.Live) { ShowPresets(BuiltInPresets); return; }
            ForgeNet.Get("/ml/github/presets", ShowPresets, e => Debug.LogWarning("[ML] GitHub presets unavailable: " + e));
        }
        void ShowPresets(string text)
        {
            {
                Newtonsoft.Json.Linq.JArray list;
                try { list = Newtonsoft.Json.Linq.JArray.Parse(text); }
                catch (Exception) { return; }
                if (presetRow == null) return;
                presetsLoaded = true;
                for (int i = presetRow.childCount - 1; i >= 0; i--) Destroy(presetRow.GetChild(i).gameObject);
                var caption = MLUI.Caps(presetRow, "Try", 2, 6, 34, 14, 9.5f); caption.color = MLUI.Faint;
                float x = 38;
                foreach (var item in list)
                {
                    string label = item.Value<string>("label"), url = item.Value<string>("url");
                    if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(url)) continue;
                    float w = 26 + label.Length * 7f;
                    if (x + w > presetRow.rect.width) break;
                    MLUI.Pill(presetRow, label, x, 0, w, 24, () => { githubUrl.text = url; session.ImportGitHub(url); });
                    x += w + 8;
                }
            }
        }
        void OnDestroy()
        {
            ForgeNet.StatusChanged -= OnNetStatus;
            cloudReady?.Dispose(); cloudReady = null;
            if (session == null) return;
            session.Changed -= Refresh; session.Reloaded -= OnSessionReloaded; session.Metrics -= OnMetrics;
            session.ClassAccuracyUpdated -= OnClassAccuracy; session.ProbeUpdated -= OnProbe;
        }
    }
}
