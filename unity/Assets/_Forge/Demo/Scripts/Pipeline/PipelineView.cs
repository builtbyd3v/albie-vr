using Forge.OS;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Forge.Demo
{
    /// <summary>
    /// The pipeline panel's UI, built in code under a <see cref="ForgePanel"/> (OS units, like the OS windows): the three-step stepper
    /// and a stage per step (Pull, Train, Push). It only draws; <see cref="PipelineDemo"/> drives it. Everything is laid out from the top
    /// left in OS units (1 unit = 0.65 mm), text 22 units (14 mm) and up.
    /// </summary>
    public sealed class PipelineView
    {
        public static readonly Color Accent = new Color32(0x4D, 0x8D, 0xFF, 255);
        static readonly Color CardColor = new Color(1f, 1f, 1f, 0.07f), ChipColor = new Color(1f, 1f, 1f, 0.11f), HairColor = new Color(1f, 1f, 1f, 0.09f);
        static readonly Color AccentChip = new Color(Accent.r, Accent.g, Accent.b, 0.24f);
        static readonly Vector2 TopLeft = new Vector2(0f, 1f);
        static readonly float[] CheckAt = { 0.04f, 0.62f, 0.94f };  // where in the pull each check appears

        public ForgePanel Panel { get; private set; }
        public float Width { get; private set; }
        public float Height { get; private set; }
        public Button ConfirmButton { get; private set; }
        public Button CancelButton { get; private set; }

        PipelineData data;
        RectTransform body, stageRoot;
        PipelineStepper stepper;
        PipelineFader pullStage, trainStage, pushStage, pullCard;

        // Pull
        PipelineBar pullBar;
        TMP_Text pullMb, pullPercent;
        readonly CanvasGroup[] pullChecks = new CanvasGroup[3];

        // Train
        PipelineBar epochBar;
        TMP_Text epochNumber, trainPhase, lossValue, mapValue;
        PipelineChart lossChart, mapChart;
        PipelineSampleFrame[] frames;
        TMP_Text[] frameConf;
        int shownEpoch = -1, shownLoss = -1, shownMap = -1, phase = -1;
        readonly int[] shownConf = { -1, -1, -1, -1 };

        // Push
        PipelineFader pushCard, pushConfirm, pushUpload, pushCommit, pushCancelled;
        PipelineBar uploadBar;
        TMP_Text uploadMb;
        Image[] uploadChecks;
        CanvasGroup[] uploadRows;

        sealed class PipelineBar
        {
            public ForgeSurface fill;
            public float width, height;
            public void Set(float p)
            {
                p = Mathf.Clamp01(p);
                fill.gameObject.SetActive(p > 0.002f);
                fill.rectTransform.sizeDelta = new Vector2(Mathf.Lerp(height, width, p), height);
            }
        }

        // ---------------------------------------------------------------- build

        /// <summary>Builds the UI under the panel's content. Returns false (and logs) when the panel has no content area.</summary>
        public bool Build(ForgePanel panel, PipelineData pipelineData)
        {
            Panel = panel;
            data = pipelineData;
            body = OSUi.Body(panel);
            if (body == null)
            {
                Debug.LogWarning("[Demo] the pipeline panel has no content area.");
                return false;
            }
            Width = body.sizeDelta.x;
            Height = body.sizeDelta.y;

            BuildStepper();
            float top = 92f;
            stageRoot = Rect(body, "Stages", 0f, top, Width, Height - top);
            BuildPull(Height - top);
            BuildTrain(Height - top);
            BuildPush(Height - top);
            Reset();
            return true;
        }

        void BuildStepper()
        {
            const float nodeW = 216f, nodeH = 56f, y = 6f;
            var row = Rect(body, "Stepper", 0f, 0f, Width, 68f);
            stepper = row.gameObject.AddComponent<PipelineStepper>();
            stepper.accent = Accent;
            stepper.lineLength = 0f;
            string[] names = { "Pull", "Train", "Push" };
            string[] icons = { "bring", "train", "repo" };
            float[] xs = { 0f, (Width - nodeW) * 0.5f, Width - nodeW };

            // Lines first, so the nodes sit on top of their ends.
            for (int i = 0; i < 2; i++)
            {
                float x0 = xs[i] + nodeW - 8f, x1 = xs[i + 1] + 8f;
                Surface(row, "Line " + (i + 1), x0, y + nodeH * 0.5f - 2f, x1 - x0, 4f, new Color(1f, 1f, 1f, 0.12f), 2f, true);
                ForgeSurface fill = Surface(row, "Line fill " + (i + 1), x0, y + nodeH * 0.5f - 2f, 0.01f, 4f, Accent, 2f, true);
                stepper.lineFill[i] = fill.rectTransform;
                stepper.lineLength = x1 - x0;
            }
            for (int i = 0; i < 3; i++)
            {
                ForgeSurface plate = Surface(row, "Node " + names[i], xs[i], y, nodeW, nodeH, new Color(1f, 1f, 1f, 0.05f), 28f, true);
                ForgeSurface badge = Surface(plate.transform, "Badge", 8f, 8f, 40f, 40f, new Color(1f, 1f, 1f, 0.14f), 20f, true);
                TMP_Text number = Label(badge.transform, (i + 1).ToString(), 24f, 0f, 0f, 40f, 40f, TextAlignmentOptions.Center, false, true);
                Image check = Icon(badge.transform, "check", 8f, 8f, 24f, new Color(1f, 1f, 1f, 0f));
                TMP_Text label = Label(plate.transform, names[i], OSUi.TitleText, 62f, 0f, nodeW - 70f, nodeH, TextAlignmentOptions.MidlineLeft, false, true);
                stepper.plate[i] = plate;
                stepper.badge[i] = badge;
                stepper.number[i] = number;
                stepper.check[i] = check;
                stepper.label[i] = label;
                _ = icons;
            }
        }

        // ---------------------------------------------------------------- pull

        void BuildPull(float stageH)
        {
            var stage = Group(stageRoot, "Pull", Width, stageH);
            pullStage = stage.gameObject.AddComponent<PipelineFader>();
            pullStage.offset = new Vector2(0f, -18f);

            float cardH = 392f;
            var card = Group(stage, "Model card", 500f, cardH);
            card.anchoredPosition = new Vector2(0f, -6f);
            pullCard = card.gameObject.AddComponent<PipelineFader>();
            pullCard.offset = new Vector2(560f, 0f);
            pullCard.duration = 0.85f;
            Surface(card, "Card", 0f, 0f, 500f, cardH, CardColor, 28f);
            var tile = Surface(card, "Icon tile", 30f, 30f, 60f, 60f, new Color(Accent.r, Accent.g, Accent.b, 0.22f), 18f);
            Icon(tile.transform, "cube", 14f, 14f, 32f, Color.white);
            Label(card, data.model.name, 30f, 108f, 24f, 370f, 40f, TextAlignmentOptions.MidlineLeft, false, true);
            Label(card, data.model.task, OSUi.SmallText, 108f, 64f, 370f, 30f, TextAlignmentOptions.MidlineLeft, true);
            Rule(card, 30f, 118f, 440f);

            string latency = (data.model.latency ?? "").Replace(" on device", "");
            Stat(card, 30f, 138f, data.model.@params, "parameters");
            Stat(card, 190f, 138f, data.model.sizeMb.ToString("0.0") + " MB", "download");
            Stat(card, 340f, 138f, latency, "on device");
            Rule(card, 30f, 226f, 440f);

            float x = 30f;
            x += Chip(card, data.model.licence, x, 246f, "check", true) + 12f;
            Chip(card, "from the " + data.model.source, x, 246f);
            Label(card, "A small open tracker for faces and objects, light enough to run on the drone's board.", OSUi.SmallText, 30f, 304f, 440f, 64f,
                TextAlignmentOptions.TopLeft, true, false, true);

            float bx = 540f, bw = Width - bx;
            Label(stage, "Pulling from the model hub", OSUi.TitleText, bx, 12f, bw, 40f, TextAlignmentOptions.MidlineLeft, false, true);
            pullBar = Bar(stage, bx, 68f, bw, 16f);
            pullMb = Label(stage, "", OSUi.BodyText, bx, 96f, bw * 0.7f, 34f, TextAlignmentOptions.MidlineLeft);
            pullPercent = Label(stage, "", OSUi.BodyText, bx + bw * 0.7f, 96f, bw * 0.3f, 34f, TextAlignmentOptions.MidlineRight, true);
            string[] checks = { "Connected to the model hub", "Verified licence: " + data.model.licence, "Checksum matches" };
            for (int i = 0; i < 3; i++)
            {
                var row = Group(stage, "Check " + (i + 1), bw, 44f);
                row.anchoredPosition = new Vector2(bx, -(168f + i * 58f));
                Chip(row, checks[i], 0f, 0f, "check", i == 1);
                pullChecks[i] = row.gameObject.AddComponent<CanvasGroup>();
            }
        }

        // ---------------------------------------------------------------- train

        void BuildTrain(float stageH)
        {
            var stage = Group(stageRoot, "Train", Width, stageH);
            trainStage = stage.gameObject.AddComponent<PipelineFader>();
            float cardH = stageH - 14f;
            float colA = 270f, colB = 380f, gap = 15f, colC = Width - colA - colB - gap * 2f;

            // Left: the run.
            var run = Group(stage, "Run", colA, cardH);
            run.anchoredPosition = new Vector2(0f, -6f);
            Surface(run, "Card", 0f, 0f, colA, cardH, CardColor, 24f);
            float pad = 18f, inner = colA - pad * 2f;
            Label(run, "Epoch", OSUi.SmallText, pad, 14f, inner, 28f, TextAlignmentOptions.MidlineLeft, true);
            epochNumber = Label(run, "1", 76f, pad, 40f, 120f, 84f, TextAlignmentOptions.MidlineLeft, false, true);
            Label(run, "/ " + data.train.epochs, 30f, pad + 124f, 52f, inner - 124f, 60f, TextAlignmentOptions.BottomLeft, true);
            epochBar = Bar(run, pad, 132f, inner, 12f);
            trainPhase = Label(run, "", OSUi.SmallText, pad, 150f, inner, 28f, TextAlignmentOptions.MidlineLeft, true);
            Rule(run, pad, 186f, inner);
            var badge = Surface(run, "Speed badge", pad, 200f, 148f, 46f, Accent, 23f, true);
            Label(badge.transform, data.gpu.speedup + " faster", 24f, 0f, 0f, 148f, 46f, TextAlignmentOptions.Center, false, true);
            Chip(run, data.gpu.label, pad, 258f, null, false, 11f);
            Rule(run, pad, 312f, inner);
            Label(run, data.dataset.name, OSUi.BodyText, pad, 326f, inner, 32f, TextAlignmentOptions.MidlineLeft, false, true);
            Label(run, data.dataset.frames.ToString("N0") + " frames, " + data.dataset.lightingSets + " lighting sets", OSUi.SmallText, pad, 360f, inner,
                60f, TextAlignmentOptions.TopLeft, true, false, true);

            // Middle: the two curves.
            float chartH = (cardH - 14f) * 0.5f;
            float bx = colA + gap;
            lossChart = ChartCard(stage, "Loss", bx, 6f, colB, chartH, true, out lossValue);
            lossChart.seriesA = data.train.loss;
            lossChart.seriesB = data.train.valLoss;
            lossChart.colorA = new Color(1f, 1f, 1f, 0.95f);
            lossChart.colorB = new Color(1f, 1f, 1f, 0.38f);
            lossChart.yMin = 0f;
            lossChart.yMax = 2.1f;
            mapChart = ChartCard(stage, "mAP@50", bx, 6f + chartH + 14f, colB, chartH, false, out mapValue);
            mapChart.seriesA = data.train.map50;
            mapChart.colorA = Accent;
            mapChart.yMin = 0f;
            mapChart.yMax = 1f;

            // Right: sample frames with the model's boxes.
            float cx = colA + colB + gap * 2f;
            Label(stage, "Live samples", OSUi.SmallText, cx, 6f, colC, 30f, TextAlignmentOptions.MidlineLeft, true);
            float tile = (colC - 12f) * 0.5f;
            int count = Mathf.Min(4, data.samples.Length);
            frames = new PipelineSampleFrame[count];
            frameConf = new TMP_Text[count];
            for (int i = 0; i < count; i++)
            {
                PipelineData.SampleInfo s = data.samples[i];
                float fx = cx + (i % 2) * (tile + 12f), fy = 42f + (i / 2) * (tile + 12f);
                var rt = Rect(stage, "Frame " + (i + 1), fx, fy, tile, tile);
                var frame = rt.gameObject.AddComponent<PipelineSampleFrame>();
                frame.raycastTarget = false;
                frame.top = Rgb(s.top);
                frame.bottom = Rgb(s.bottom);
                frame.subject = Rgb(s.subject);
                frame.head = new Vector3(s.head[0], s.head[1], s.head[2]);
                frame.truth = s.truth;
                frame.start = s.start;
                frame.falseBox = s.falseBox;
                frame.conf0 = s.conf0;
                frame.conf1 = s.conf1;
                frame.accent = Accent;
                frame.seed = i + 1f;
                frames[i] = frame;
                var tag = Surface(rt, "Confidence", 8f, tile - 44f, 112f, 36f, new Color(0f, 0f, 0f, 0.55f), 18f, true);
                frameConf[i] = Label(tag.transform, "", OSUi.SmallText, 0f, 0f, 112f, 36f, TextAlignmentOptions.Center, false, true);
            }
            Label(stage, "Boxes tighten as the model learns.", OSUi.SmallText, cx, 42f + (tile + 12f) * 2f, colC, 30f, TextAlignmentOptions.MidlineLeft, true);
        }

        PipelineChart ChartCard(Transform parent, string title, float x, float y, float w, float h, bool legend, out TMP_Text value)
        {
            var card = Group(parent, "Chart " + title, w, h);
            card.anchoredPosition = new Vector2(x, -y);
            Surface(card, "Card", 0f, 0f, w, h, CardColor, 24f);
            Label(card, title, OSUi.BodyText, 18f, 10f, 110f, 36f, TextAlignmentOptions.MidlineLeft, false, true);
            float lx = 18f + (title.Length > 5 ? 118f : 74f);
            if (legend)
            {
                Surface(card, "Dot train", lx, 22f, 12f, 12f, new Color(1f, 1f, 1f, 0.95f), 6f, true);
                Label(card, "train", OSUi.SmallText, lx + 18f, 10f, 70f, 36f, TextAlignmentOptions.MidlineLeft, true);
                Surface(card, "Dot val", lx + 92f, 22f, 12f, 12f, new Color(1f, 1f, 1f, 0.38f), 6f, true);
                Label(card, "val", OSUi.SmallText, lx + 110f, 10f, 60f, 36f, TextAlignmentOptions.MidlineLeft, true);
            }
            value = Label(card, "", 28f, w - 118f, 8f, 100f, 40f, TextAlignmentOptions.MidlineRight, false, true);
            var rt = Rect(card, "Plot", 18f, 54f, w - 36f, h - 54f - 16f);
            var chart = rt.gameObject.AddComponent<PipelineChart>();
            chart.raycastTarget = false;
            return chart;
        }

        // ---------------------------------------------------------------- push

        void BuildPush(float stageH)
        {
            var stage = Group(stageRoot, "Push", Width, stageH);
            pushStage = stage.gameObject.AddComponent<PipelineFader>();
            float cardH = stageH - 14f;
            float leftW = Mathf.Round(Width * 0.60f), gap = 15f, rightW = Width - leftW - gap;
            PipelineData.PushInfo push = data.push;

            // The recap of what the run produced, always there.
            var recap = Group(stage, "Recap", rightW, cardH);
            recap.anchoredPosition = new Vector2(leftW + gap, -6f);
            Surface(recap, "Card", 0f, 0f, rightW, cardH, CardColor, 24f);
            float pad = 26f, inner = rightW - pad * 2f;
            Label(recap, "Fine-tune result", OSUi.TitleText, pad, 20f, inner, 40f, TextAlignmentOptions.MidlineLeft, false, true);
            float[] map = data.train.map50, loss = data.train.loss;
            Result(recap, pad, 78f, inner, "mAP@50", map[0].ToString("0.00"), map[map.Length - 1].ToString("0.00"), true);
            Result(recap, pad, 168f, inner, "Loss", loss[0].ToString("0.00"), loss[loss.Length - 1].ToString("0.00"), false);
            Rule(recap, pad, 258f, inner);
            Label(recap, data.train.epochs + " epochs on a " + data.gpu.label, OSUi.SmallText, pad, 272f, inner, 58f, TextAlignmentOptions.TopLeft, true, false, true);
            Label(recap, "face-track-nano-ft-v1, " + push.files[0].size, OSUi.SmallText, pad, 332f, inner, 58f, TextAlignmentOptions.TopLeft, true, false, true);

            // The left card: confirm, upload and commit states stacked in one place.
            var card = Group(stage, "Push card", leftW, cardH);
            card.anchoredPosition = new Vector2(0f, -6f);
            pushCard = card.gameObject.AddComponent<PipelineFader>();
            pushCard.offset = new Vector2(0f, -18f);
            Surface(card, "Card", 0f, 0f, leftW, cardH, CardColor, 24f);
            float cp = 30f, ci = leftW - cp * 2f;

            // Confirm.
            var confirm = Group(card, "Confirm", leftW, cardH);
            pushConfirm = confirm.gameObject.AddComponent<PipelineFader>();
            pushConfirm.offset = new Vector2(0f, -10f);
            Label(confirm, "Push to private repo?", 30f, cp, 20f, ci, 44f, TextAlignmentOptions.MidlineLeft, false, true);
            Lock(confirm, cp, 80f, 26f, 30f);
            float rx = cp + 38f;
            Label(confirm, push.repoLabel, OSUi.BodyText, rx, 76f, 330f, 36f, TextAlignmentOptions.MidlineLeft);
            Chip(confirm, "branch " + push.branch, leftW - cp - 190f, 74f);
            Rule(confirm, cp, 124f, ci);
            for (int i = 0; i < push.files.Length; i++)
            {
                float fy = 136f + i * 46f;
                Label(confirm, push.files[i].path, OSUi.BodyText, cp, fy, ci - 130f, 40f, TextAlignmentOptions.MidlineLeft);
                Label(confirm, push.files[i].size, OSUi.SmallText, cp + ci - 120f, fy, 120f, 40f, TextAlignmentOptions.MidlineRight, true);
            }
            Rule(confirm, cp, 136f + push.files.Length * 46f + 6f, ci);
            Label(confirm, "Nothing leaves your room until you confirm.", OSUi.SmallText, cp, 292f, ci, 32f, TextAlignmentOptions.MidlineLeft, true);
            float buttonY = cardH - 86f;
            ConfirmButton = ActionButton(confirm, "Confirm", OSButtonStyle.Primary, buttonY, out RectTransform confirmHolder, out float confirmW);
            CancelButton = ActionButton(confirm, "Cancel", OSButtonStyle.Secondary, buttonY, out RectTransform cancelHolder, out float cancelW);
            confirmHolder.anchoredPosition = new Vector2(leftW - cp - confirmW, -buttonY);
            cancelHolder.anchoredPosition = new Vector2(leftW - cp - confirmW - 16f - cancelW, -buttonY);

            // Upload.
            var upload = Group(card, "Upload", leftW, cardH);
            pushUpload = upload.gameObject.AddComponent<PipelineFader>();
            pushUpload.offset = new Vector2(0f, -10f);
            Label(upload, "Pushing to " + push.repoLabel, 28f, cp, 20f, ci, 44f, TextAlignmentOptions.MidlineLeft, false, true);
            uploadBar = Bar(upload, cp, 88f, ci, 16f);
            uploadMb = Label(upload, "", OSUi.BodyText, cp, 114f, ci, 34f, TextAlignmentOptions.MidlineLeft);
            uploadChecks = new Image[push.files.Length];
            uploadRows = new CanvasGroup[push.files.Length];
            for (int i = 0; i < push.files.Length; i++)
            {
                var row = Group(upload, "File " + (i + 1), ci, 40f);
                row.anchoredPosition = new Vector2(cp, -(176f + i * 50f));
                uploadChecks[i] = Icon(row, "check", 0f, 7f, 26f, new Color(Accent.r, Accent.g, Accent.b, 0f));
                Label(row, push.files[i].path, OSUi.BodyText, 40f, 0f, ci - 170f, 40f, TextAlignmentOptions.MidlineLeft);
                Label(row, push.files[i].size, OSUi.SmallText, ci - 120f, 0f, 120f, 40f, TextAlignmentOptions.MidlineRight, true);
                uploadRows[i] = row.gameObject.AddComponent<CanvasGroup>();
            }

            // Commit.
            var commit = Group(card, "Commit", leftW, cardH);
            pushCommit = commit.gameObject.AddComponent<PipelineFader>();
            pushCommit.offset = new Vector2(0f, -10f);
            var seal = Surface(commit, "Seal", cp, 24f, 52f, 52f, Accent, 26f, true);
            Icon(seal.transform, "check", 12f, 12f, 28f, Color.white);
            Label(commit, "Pushed to " + push.repoLabel, 28f, cp + 66f, 20f, ci - 66f, 40f, TextAlignmentOptions.MidlineLeft, false, true);
            Label(commit, "Version one is in your repo.", OSUi.SmallText, cp + 66f, 54f, ci - 66f, 30f, TextAlignmentOptions.MidlineLeft, true);
            Transform info = Surface(commit, "Commit card", cp, 104f, ci, 252f, new Color(0f, 0f, 0f, 0.22f), 20f).transform;
            float ix = 22f;
            float cxp = ix;
            cxp += Chip(info, push.branch, cxp, 20f, "repo") + 10f;
            cxp += Chip(info, push.sha, cxp, 20f) + 10f;
            var lockChip = Surface(info, "Private chip", cxp, 20f, 120f, 40f, ChipColor, 20f, true);
            Lock(lockChip.transform, 14f, 8f, 18f, 24f);
            Label(lockChip.transform, "private", OSUi.BodyText, 40f, 0f, 76f, 40f, TextAlignmentOptions.MidlineLeft);
            Label(info, NoBreaks(push.commitMessage), 26f, ix, 82f, ci - ix * 2f, 76f, TextAlignmentOptions.TopLeft, false, true, true);
            Rule(info, ix, 172f, ci - ix * 2f);
            long files = push.files.Length;
            Label(info, files + " files: " + push.files[0].size + " of weights, config and eval report", OSUi.SmallText, ix, 184f, ci - ix * 2f, 56f,
                TextAlignmentOptions.TopLeft, true, false, true);

            // Cancelled: the card is gone, and a quiet line says nothing was sent.
            var cancelled = Group(stage, "Cancelled", leftW, cardH);
            cancelled.anchoredPosition = new Vector2(0f, -6f);
            pushCancelled = cancelled.gameObject.AddComponent<PipelineFader>();
            Label(cancelled, "Push cancelled.", 30f, 0f, cardH * 0.5f - 40f, leftW, 44f, TextAlignmentOptions.Center, false, true);
            Label(cancelled, "Nothing was sent. The model stays on this device.", OSUi.BodyText, 0f, cardH * 0.5f + 8f, leftW, 36f, TextAlignmentOptions.Center, true);
        }

        void Result(Transform parent, float x, float y, float w, string label, string from, string to, bool lead)
        {
            Label(parent, label, OSUi.SmallText, x, y, w, 28f, TextAlignmentOptions.MidlineLeft, true);
            Label(parent, from, 30f, x, y + 30f, 90f, 46f, TextAlignmentOptions.MidlineLeft, true);
            Icon(parent, "chevron_right", x + 96f, y + 42f, 24f, new Color(1f, 1f, 1f, 0.55f));
            var big = Label(parent, to, 40f, x + 134f, y + 26f, 140f, 54f, TextAlignmentOptions.MidlineLeft, false, true);
            if (lead) big.color = Accent;
        }

        const float ButtonScale = 1.4f;  // 48 units x 1.4 = 43 mm tall: the poke target is at least 4 cm

        // A kit button drawn bigger: it sits in a holder that carries the scale (the kit's animator resets the button's own). Returns the
        // holder through <paramref name="holder"/>, which is what gets positioned, and its width in units.
        Button ActionButton(Transform parent, string text, OSButtonStyle style, float y, out RectTransform holder, out float width)
        {
            width = 0f;
            holder = Rect(parent, "Button holder " + text, 0f, y, 10f, 48f);
            holder.localScale = Vector3.one * ButtonScale;
            Button button = OSUi.Button(holder, text, null, style);
            if (button == null) return null;
            var rt = (RectTransform)button.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = TopLeft;
            rt.anchoredPosition = Vector2.zero;
            holder.sizeDelta = rt.sizeDelta;
            width = rt.sizeDelta.x * ButtonScale;
            // The whole box takes the poke, not just the kit's plate inside it.
            var area = Rect(rt, "Poke area", 0f, 0f, rt.sizeDelta.x, rt.sizeDelta.y);
            Stretch(area);
            var catcher = area.gameObject.AddComponent<Image>();
            catcher.color = Color.clear;
            catcher.raycastTarget = true;
            area.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            return button;
        }

        static void Stretch(Transform t)
        {
            var rt = t as RectTransform;
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        // ---------------------------------------------------------------- control (used by PipelineDemo)

        /// <summary>Everything back to the start: stepper pending, stages hidden, bars and curves at zero.</summary>
        public void Reset()
        {
            for (int i = 0; i < 3; i++) stepper.Set(i, PipelineStepper.Pending);
            stepper.Snap();
            pullStage.Hide(true);
            trainStage.Hide(true);
            pushStage.Hide(true);
            pullCard.Hide(true);
            pushCard.Hide(true);
            pushConfirm.Hide(true);
            pushUpload.Hide(true);
            pushCommit.Hide(true);
            pushCancelled.Hide(true);
            SetPull(0f);
            SetTrain(0f);
            SetUpload(0f);
            Panel.Subtitle = data.model.name + " on " + data.dataset.name;
        }

        public void SetStep(int node, int state) => stepper.Set(node, state);

        public void ShowPull()
        {
            trainStage.Hide();
            pushStage.Hide();
            pullStage.Show();
        }

        public void ShowPullCard() => pullCard.Show();

        public void SetPull(float p)
        {
            pullBar.Set(p);
            float mb = p * data.model.sizeMb;
            pullMb.SetText("{0:1} / {1:1} MB", mb, data.model.sizeMb);
            pullPercent.SetText("{0:0}%", p * 100f);
            for (int i = 0; i < 3; i++)
                if (pullChecks[i] != null) pullChecks[i].alpha = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(CheckAt[i], CheckAt[i] + 0.06f, p));
        }

        public void ShowTrain()
        {
            pullStage.Hide();
            pushStage.Hide();
            trainStage.Show();
        }

        /// <summary>Epochs completed so far, fractional (0 to the epoch count): the counter, bar, both curves and the sample boxes.</summary>
        public void SetTrain(float epochs)
        {
            int total = data.train.epochs;
            epochs = Mathf.Clamp(epochs, 0f, total);
            float done = epochs / total;
            epochBar.Set(done);
            lossChart.SetProgress(epochs);
            mapChart.SetProgress(epochs);
            int shown = Mathf.Clamp(Mathf.CeilToInt(epochs), 1, total);
            if (shown != shownEpoch)
            {
                shownEpoch = shown;
                epochNumber.SetText("{0}", shown);
            }
            int next = epochs >= total ? 3 : epochs < 1.5f ? 0 : epochs > total - 1.5f ? 2 : 1;
            if (next != phase)
            {
                phase = next;
                trainPhase.text = next == 0 ? "Warming up" : next == 1 ? "Fine-tuning" : next == 2 ? "Evaluating" : "Done";
            }
            float position = Mathf.Max(0f, epochs - 1f);
            int loss = Mathf.RoundToInt(PipelineData.At(data.train.loss, position) * 100f);
            if (loss != shownLoss) { shownLoss = loss; lossValue.SetText("{0:2}", loss * 0.01f); }
            int map = Mathf.RoundToInt(PipelineData.At(data.train.map50, position) * 100f);
            if (map != shownMap) { shownMap = map; mapValue.SetText("{0:2}", map * 0.01f); }
            for (int i = 0; i < frames.Length; i++)
            {
                frames[i].SetLearn(done);
                int conf = Mathf.RoundToInt(frames[i].Confidence * 100f);
                if (conf != shownConf[i]) { shownConf[i] = conf; frameConf[i].SetText("face {0:2}", conf * 0.01f); }
            }
        }

        public void ShowPush()
        {
            pullStage.Hide();
            trainStage.Hide();
            pushStage.Show();
        }

        public void ShowConfirm()
        {
            pushCancelled.Hide();
            pushUpload.Hide(true);
            pushCommit.Hide(true);
            pushCard.Show();
            pushConfirm.Show();
        }

        public void ShowCancelled()
        {
            pushCard.Hide();
            pushCancelled.Show();
        }

        public void ShowUpload()
        {
            pushConfirm.Hide();
            pushUpload.Show();
        }

        public void SetUpload(float p)
        {
            uploadBar.Set(p);
            float total = 0f;
            for (int i = 0; i < data.push.files.Length; i++) total += data.push.files[i].mb;
            uploadMb.SetText("{0:1} / {1:1} MB", p * total, total);
            // Files finish in order, by their share of the total.
            float cumulative = 0f;
            for (int i = 0; i < data.push.files.Length; i++)
            {
                cumulative += data.push.files[i].mb / Mathf.Max(0.001f, total);
                float on = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(cumulative - 0.02f, cumulative + 0.01f, p));
                uploadChecks[i].color = new Color(Accent.r, Accent.g, Accent.b, on);
            }
        }

        public void ShowCommit()
        {
            pushUpload.Hide();
            pushCommit.Show();
        }

        // ---------------------------------------------------------------- building blocks

        // Keeps the names with hyphens in one piece when a line wraps.
        string NoBreaks(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = text.Replace(data.model.name, "<nobr>" + data.model.name + "</nobr>");
            return text.Replace(data.dataset.name, "<nobr>" + data.dataset.name + "</nobr>");
        }

        static Color Rgb(float[] c) => c != null && c.Length >= 3 ? new Color(c[0], c[1], c[2], 1f) : Color.gray;

        static RectTransform Rect(Transform parent, string name, float x, float y, float w, float h)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = TopLeft;
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        static RectTransform Group(Transform parent, string name, float w, float h) => Rect(parent, name, 0f, 0f, w, h);

        static ForgeSurface Surface(Transform parent, string name, float x, float y, float w, float h, Color color, float corner, bool pill = false)
        {
            var rt = Rect(parent, name, x, y, w, h);
            var surface = rt.gameObject.AddComponent<ForgeSurface>();
            surface.Style = ForgeSurface.Kind.Fill;
            surface.color = color;
            surface.Corner = corner;
            surface.Pill = pill;
            surface.raycastTarget = false;
            return surface;
        }

        static void Rule(Transform parent, float x, float y, float w) => Surface(parent, "Rule", x, y, w, 1.5f, HairColor, 0.5f);

        static TMP_Text Label(Transform parent, string text, float size, float x, float y, float w, float h, TextAlignmentOptions align,
            bool secondary = false, bool bold = false, bool wrap = false)
        {
            TMP_Text t = OSUi.Text(parent, text, size, secondary);
            var rt = (RectTransform)t.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = TopLeft;
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            t.alignment = align;
            t.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Overflow;
            if (bold) t.fontStyle = FontStyles.Bold;
            t.raycastTarget = false;
            var layout = t.GetComponent<LayoutElement>();
            if (layout != null) layout.ignoreLayout = true;
            return t;
        }

        static Image Icon(Transform parent, string name, float x, float y, float size, Color color)
        {
            var rt = Rect(parent, "Icon " + name, x, y, size, size);
            var image = rt.gameObject.AddComponent<Image>();
            image.sprite = OSUi.Icon(name);
            image.preserveAspect = true;
            image.color = color;
            image.raycastTarget = false;
            image.enabled = image.sprite != null;
            return image;
        }

        static void Lock(Transform parent, float x, float y, float w, float h)
        {
            var rt = Rect(parent, "Lock", x, y, w, h);
            var padlock = rt.gameObject.AddComponent<PipelineLock>();
            padlock.color = new Color(1f, 1f, 1f, 0.9f);
            padlock.raycastTarget = false;
        }

        /// <summary>A pill with an optional icon and a label; returns its width.</summary>
        static float Chip(Transform parent, string text, float x, float y, string icon = null, bool accent = false, float pad = 16f)
        {
            ForgeSurface chip = Surface(parent, "Chip " + text, x, y, 100f, 40f, accent ? AccentChip : ChipColor, 20f, true);
            if (accent) chip.Outline = 0.5f;
            float left = pad;
            if (icon != null)
            {
                Icon(chip.transform, icon, pad - 2f, 7f, 26f, accent ? Color.white : new Color(1f, 1f, 1f, 0.85f));
                left += 30f;
            }
            TMP_Text label = Label(chip.transform, text, OSUi.BodyText, left, 0f, 400f, 40f, TextAlignmentOptions.MidlineLeft);
            float width = left + label.GetPreferredValues(text).x + pad;
            label.rectTransform.sizeDelta = new Vector2(width - left, 40f);
            chip.rectTransform.sizeDelta = new Vector2(width, 40f);
            return width;
        }

        static void Stat(Transform parent, float x, float y, string value, string caption)
        {
            Label(parent, value, 28f, x, y, 150f, 40f, TextAlignmentOptions.MidlineLeft, false, true);
            Label(parent, caption, OSUi.SmallText, x, y + 40f, 150f, 30f, TextAlignmentOptions.MidlineLeft, true);
        }

        static PipelineBar Bar(Transform parent, float x, float y, float w, float h)
        {
            Surface(parent, "Bar track", x, y, w, h, new Color(1f, 1f, 1f, 0.12f), h * 0.5f, true);
            ForgeSurface fill = Surface(parent, "Bar fill", x, y, h, h, Accent, h * 0.5f, true);
            fill.Glow = 0.35f;
            return new PipelineBar { fill = fill, width = w, height = h };
        }
    }
}
