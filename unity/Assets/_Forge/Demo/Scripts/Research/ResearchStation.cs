using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static Forge.Demo.ResearchUi;

namespace Forge.Demo
{
    // =====================================================================================================================
    // Research station: beats B3 and B4 of docs/demo/VIDEO_SCRIPT.md ("find a camera that fits your drone, and a model that can track you").
    //
    // Actions (see DemoActions, docs/demo/TONIGHT_PLAN.md section 5):
    //   research.open       fade in the two panels: "Research" (Slot.Left) and "Model hub" (Slot.Right), constraint chips first
    //   research.cameras    queries type out one by one, then six camera cards stream in and their checks flip
    //                       green or red one by one (about 12 s). Raises  research.cameras.done
    //   research.models     one model-hub query, then five model cards stream in, the pick gets a ring (about 4 s). Raises research.models.done
    //   research.verdict    the winning card lifts out of the grid and grows in front (between Left and Center), the others dim,
    //                       four verdict lines tick green one by one, then "Add to parts list" shows. Raises research.verdict.shown
    //                       When that button is poked: pressed state, a check, the card shrinks back. Raises research.added
    //   research.close      fade both panels (and the card) out
    //   demo.reset          back to the start, everything destroyed
    //   ResearchStation.VerdictPosition is the verdict card's world position, for drone.mount_camera ({from: [x, y, z]}).
    //
    // Data: Resources/research_data.json. It is also the shape the real research tools will return (JUDGE_PATH J3 cameras,
    // J4 models), so only the source of the file changes later, never the panels:
    //
    //   {
    //     "constraints": [ "5 V rail (from 4S battery BEC)", "under 30 g", ... ],          // the drone's, shown as chips
    //     "queries":     [ { "text": "small camera module 5V 60fps wide FOV", "results": 14 }, ... ],   // typed out, 4 of them
    //     "cameras": [ {                                                                    // 6 products, made-up names and shops
    //         "id": "cam_a", "name": "Tern C2 Mini", "image": "cam_a",                      // image id: Resources/DemoImages/{id}; a drawn placeholder until it exists
    //         "source": "partsbay.example",
    //         "specs":  { "resolution": "1080p", "fps": 60, "fov": 120, "voltage": "5 V", "weight_g": 9, "price_usd": 24, "ships_days": 3 },
    //         "checks": { "power": {"pass": true, "label": "5 V", "reason": "..."},       // power, weight, mount, price: label is the chip text,
    //                     "weight": {...}, "mount": {...}, "price": {...} }                // reason is the sentence behind it
    //     }, ... ],
    //     "models_query": "small face and person tracker for a flight board",
    //     "models": [ { "id": "model_a", "name": "face-track-nano", "image": "model_a", "task": "Face tracking", "params": "3.2 M",
    //                   "license": "Apache-2.0", "latency_ms": 6, "downloads": "48k", "pick": true }, ... ],   // 5 cards, one "pick"
    //     "verdict": { "winner": "cam_a", "lines": [ { "key": "power", "text": "Runs off your 5 V rail" }, ... ] }   // the winner passes all four checks
    //   }
    //
    // Layout: every number below is in millimetres on a ForgePanel (1 canvas unit = 1 mm). Body text is 14 mm or more, so it is
    // readable at 0.6 m on a Quest 3S. The only accent is the OS blue; green and red are used for pass and fail only.
    // Nothing allocates per frame: Update only moves the spinners and the shimmer lines.
    // =====================================================================================================================

    public sealed class ResearchStation : MonoBehaviour
    {
        public static ResearchStation Instance { get; private set; }

        static Vector3 verdictPosition;
        static bool verdictPositionSet;

        /// <summary>
        /// World position of the verdict card (where it settles in front of the user), for the Flight station's
        /// <c>drone.mount_camera</c> {from}. Valid before the card exists too: it is where the card will be.
        /// </summary>
        public static Vector3 VerdictPosition => verdictPositionSet ? verdictPosition : VerdictPose().position;

        [SerializeField] ForgePanel panelPrefab;

        // ------------------------------------------------------------------ sizes (mm)
        const float LeftW = 560f, RightW = 460f, VerdictW = 320f;
        const float HeaderBlock = 110f;          // panel header 64 + gap 14 above the content, 16 below
        const float ChipH = 28f, ChipGap = 6f;
        const float QueryH = 24f;
        const float CardH = 124f, CardGapX = 12f, CardGapY = 10f;
        const float ModelH = 84f, ModelGap = 8f;
        const int GridColumns = 2;

        // ------------------------------------------------------------------ timing (s)
        const float TypeCps = 25f, QueryHold = 0.4f, StartDelay = 0.5f;
        const float CardInterval = 0.35f, ChipInterval = 0.16f, ChipFade = 0.18f;
        const float ModelInterval = 0.4f;

        // ------------------------------------------------------------------ state
        ResearchData data;
        Lane left, right;
        Verdict verdict;
        readonly List<QueryRow> leftQueries = new List<QueryRow>();
        QueryRow modelQuery;
        readonly List<CameraCard> cameraCards = new List<CameraCard>();
        readonly List<ModelCard> modelCards = new List<ModelCard>();
        readonly List<RectTransform> spinning = new List<RectTransform>();
        bool camerasRunning, camerasDone, modelsRunning, modelsDone, verdictRunning, verdictShown, added;

        sealed class Lane
        {
            public ForgePanel panel;
            public CanvasGroup group;
            public Vector3 baseScale;
            public Pose pose;
            public RectTransform body;
            public TMP_Text status;
            public RectTransform track;
            public ForgeSurface segment;
            public CanvasGroup shimmerGroup;
            public float trackWidth;
            public bool thinking;
        }

        sealed class QueryRow
        {
            public RectTransform root;
            public CanvasGroup group;
            public Vector2 basePos;
            public TMP_Text text, results;
            public RectTransform orbit;
            public ForgeSurface disc;
            public RectTransform check;
            public CanvasGroup checkGroup;
        }

        sealed class CameraCard
        {
            public CameraData data;
            public RectTransform root;
            public CanvasGroup group;
            public Vector2 basePos;
            public ForgeSurface fill;
            public readonly ForgeSurface[] chipFill = new ForgeSurface[ChecksData.Count];
            public readonly TMP_Text[] chipText = new TMP_Text[ChecksData.Count];
            public readonly bool[] chipDone = new bool[ChecksData.Count];
            public bool revealed;
            public RectTransform addedMark;
            public CanvasGroup addedGroup;
        }

        sealed class ModelCard
        {
            public ModelData data;
            public RectTransform root;
            public CanvasGroup group;
            public Vector2 basePos;
            public Image ring;
            public CanvasGroup pickGroup;
            public bool revealed;
        }

        sealed class VerdictRow
        {
            public CanvasGroup group;
            public RectTransform root;
            public Vector2 basePos;
            public RectTransform check;
        }

        sealed class Verdict
        {
            public ForgePanel panel;
            public CanvasGroup group;
            public Vector3 baseScale;
            public readonly List<VerdictRow> rows = new List<VerdictRow>();
            public ForgePill pill;
            public CanvasGroup pillGroup;
            public bool poked;
        }

        // ------------------------------------------------------------------ registration

        void OnEnable()
        {
            Instance = this;
            DemoActions.Register("research.open", OnOpen);
            DemoActions.Register("research.close", OnClose);
            DemoActions.Register("research.cameras", OnCameras);
            DemoActions.Register("research.models", OnModels);
            DemoActions.Register("research.verdict", OnVerdict);
            DemoActions.Register(DemoActions.Reset, OnReset);
        }

        void OnDisable()
        {
            DemoActions.Unregister("research.open", OnOpen);
            DemoActions.Unregister("research.close", OnClose);
            DemoActions.Unregister("research.cameras", OnCameras);
            DemoActions.Unregister("research.models", OnModels);
            DemoActions.Unregister("research.verdict", OnVerdict);
            DemoActions.Unregister(DemoActions.Reset, OnReset);
            if (Instance == this) Instance = null;
            StopAllCoroutines();
            Teardown();
        }

        void OnOpen(DemoCue cue) => OpenPanels(false);
        void OnCameras(DemoCue cue) { if (!camerasRunning && !camerasDone) StartCoroutine(RunCameras()); }
        void OnModels(DemoCue cue) { if (!modelsRunning && !modelsDone) StartCoroutine(RunModels()); }
        void OnVerdict(DemoCue cue) { if (!verdictRunning && !verdictShown) StartCoroutine(RunVerdict()); }

        void OnClose(DemoCue cue)
        {
            StopAllCoroutines();
            camerasRunning = modelsRunning = verdictRunning = false;
            StartCoroutine(CloseRoutine());
        }

        void OnReset(DemoCue cue)
        {
            StopAllCoroutines();
            Teardown();
            verdictPositionSet = false;
        }

        /// <summary>For tests and the sandbox: pokes "Add to parts list" the way a finger would.</summary>
        public bool PokeAdd()
        {
            if (verdict == null || verdict.pill == null || !verdictShown || verdict.poked) return false;
            verdict.pill.Button.onClick.Invoke();
            return true;
        }

        public bool VerdictCardUp => verdict != null && verdictShown;

        // For the sandbox's screenshots.
        public Transform LeftPanel => left != null && left.panel != null ? left.panel.transform : null;
        public Transform RightPanel => right != null && right.panel != null ? right.panel.transform : null;
        public Transform VerdictPanel => verdict != null && verdict.panel != null ? verdict.panel.transform : null;

        // ------------------------------------------------------------------ per frame

        void Update()
        {
            float angle = -Time.time * 480f;
            for (int i = 0; i < spinning.Count; i++)
                if (spinning[i] != null) spinning[i].localRotation = Quaternion.Euler(0f, 0f, angle);
            Shimmer(left);
            Shimmer(right);
        }

        void Shimmer(Lane lane)
        {
            if (lane == null || lane.segment == null) return;
            float target = lane.thinking ? 1f : 0f;
            lane.shimmerGroup.alpha = Mathf.MoveTowards(lane.shimmerGroup.alpha, target, Time.deltaTime * 4f);
            if (lane.shimmerGroup.alpha <= 0.001f) return;
            const float segWidth = 150f;
            float p = ForgeTween.EaseInOutCubic(Mathf.Repeat(Time.time / 1.5f, 1f));
            float x = Mathf.Lerp(-segWidth, lane.trackWidth, p);
            float l = Mathf.Max(0f, x), r = Mathf.Min(lane.trackWidth, x + segWidth);
            RectTransform rt = lane.segment.rectTransform;
            rt.anchoredPosition = new Vector2(l, 0f);
            rt.sizeDelta = new Vector2(Mathf.Max(0.01f, r - l), rt.sizeDelta.y);
        }

        // ------------------------------------------------------------------ open and close

        void EnsureData()
        {
            if (data == null) data = ResearchData.Load();
        }

        ForgePanel PanelPrefab()
        {
            if (panelPrefab != null) return panelPrefab;
            Workspace ws = Workspace.Current;
            return ws != null ? ws.panelPrefab : null;
        }

        void OpenPanels(bool instant)
        {
            if (left != null) return;
            EnsureData();
            if (PanelPrefab() == null)
            {
                Debug.LogWarning("[Research] No ForgePanel prefab assigned; nothing to show.");
                return;
            }
            left = CreateLane("research", "Research", new Vector2(LeftW, 600f), DemoLayout.Get(DemoLayout.Slot.Left));
            right = CreateLane("modelhub", "Model hub", new Vector2(RightW, 560f), DemoLayout.Get(DemoLayout.Slot.Right));
            BuildLeft();
            BuildRight();
            if (instant)
            {
                SetLaneShown(left, 1f);
                SetLaneShown(right, 1f);
                for (int i = 0; i < constraintChips.Count; i++) { constraintChips[i].group.alpha = 1f; }
            }
            else
            {
                Tween(0.4f, 0f, t => SetLaneShown(left, ForgeTween.EaseOutCubic(t)));
                Tween(0.4f, 0.12f, t => SetLaneShown(right, ForgeTween.EaseOutCubic(t)));
                for (int i = 0; i < constraintChips.Count; i++)
                    Pop(constraintChips[i].group, constraintChips[i].rect, constraintChips[i].basePos, 0.3f + i * 0.12f);
            }
        }

        static void SetLaneShown(Lane lane, float e)
        {
            if (lane == null || lane.panel == null) return;
            lane.group.alpha = e;
            lane.panel.transform.localScale = lane.baseScale * Mathf.Lerp(0.92f, 1f, e);
        }

        Lane CreateLane(string id, string title, Vector2 sizeMm, Pose pose)
        {
            var lane = new Lane();
            lane.panel = SpawnPanel(id, title, sizeMm, pose, out lane.group, out lane.baseScale);
            lane.pose = pose;
            lane.body = Body(lane.panel.Content);

            // The thinking shimmer sits on the header's lower edge, and a short status line at the header's right.
            var root = (RectTransform)lane.panel.transform;
            lane.trackWidth = sizeMm.x - 40f;
            lane.track = Box(root, "Shimmer", 20f, 64f, lane.trackWidth, 3f);
            Fill(lane.track, "Track", 0f, 0f, lane.trackWidth, 3f, new Color(1f, 1f, 1f, 0.07f), 1.5f, true);
            lane.segment = Fill(lane.track, "Glow", 0f, 0f, 0.01f, 3f, Accent, 1.5f, true);
            lane.shimmerGroup = Group(lane.segment.gameObject, 0f);
            lane.status = Label(root, "", 14f, Sub, 0f, 0f, 300f, 24f, FontStyles.Normal, TextAlignmentOptions.MidlineRight);
            lane.status.rectTransform.anchorMin = lane.status.rectTransform.anchorMax = new Vector2(1f, 1f);
            lane.status.rectTransform.pivot = new Vector2(1f, 0.5f);
            lane.status.rectTransform.anchoredPosition = new Vector2(-24f, -32f);
            return lane;
        }

        ForgePanel SpawnPanel(string id, string title, Vector2 sizeMm, Pose pose, out CanvasGroup group, out Vector3 baseScale)
        {
            ForgePanel panel = Instantiate(PanelPrefab(), transform);
            panel.name = "Research " + id;
            panel.panelId = "research-" + id;
            panel.Title = title;
            panel.SetBadge(null, Accent);
            panel.SizeMeters = sizeMm * ForgePanel.MetersPerUnit;
            panel.transform.SetPositionAndRotation(pose.position, pose.rotation);
            if (panel.GetComponent<GraphicRaycaster>() == null) panel.gameObject.AddComponent<GraphicRaycaster>();
            group = panel.GetComponent<CanvasGroup>();
            if (group == null) group = panel.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            baseScale = panel.transform.localScale;
            return panel;
        }

        IEnumerator CloseRoutine()
        {
            Lane l = left, r = right;
            Verdict v = verdict;
            float elapsed = 0f;
            const float duration = 0.3f;
            while (elapsed < duration)
            {
                float e = 1f - ForgeTween.EaseInOutCubic(elapsed / duration);
                if (l != null) SetLaneShown(l, e);
                if (r != null) SetLaneShown(r, e);
                if (v != null && v.panel != null) { v.group.alpha = e; v.panel.transform.localScale = v.baseScale * Mathf.Lerp(0.92f, 1f, e); }
                elapsed += Time.deltaTime;
                yield return null;
            }
            Teardown();
        }

        void Teardown()
        {
            if (left != null && left.panel != null) Destroy(left.panel.gameObject);
            if (right != null && right.panel != null) Destroy(right.panel.gameObject);
            if (verdict != null && verdict.panel != null) Destroy(verdict.panel.gameObject);
            left = right = null;
            verdict = null;
            leftQueries.Clear();
            modelQuery = null;
            cameraCards.Clear();
            modelCards.Clear();
            constraintChips.Clear();
            spinning.Clear();
            camerasRunning = camerasDone = modelsRunning = modelsDone = verdictRunning = verdictShown = added = false;
        }

        // ------------------------------------------------------------------ the Research panel

        sealed class ChipRef
        {
            public RectTransform rect;
            public CanvasGroup group;
            public Vector2 basePos;
        }

        readonly List<ChipRef> constraintChips = new List<ChipRef>();

        void BuildLeft()
        {
            RectTransform body = left.body;
            float contentW = LeftW - 36f;

            // The drone's constraints, as chips that wrap onto a second row.
            float x = 0f, y = 0f;
            for (int i = 0; i < data.constraints.Count; i++)
            {
                RectTransform chip = Box(body, "Constraint", 0f, 0f, 10f, ChipH);
                Fill(chip, "Bg", 0f, 0f, 10f, ChipH, ChipFill, ChipH * 0.5f, true);
                TMP_Text label = Label(chip, data.constraints[i], 14f, Ink, 24f, 0f, 400f, ChipH);
                float w = label.GetPreferredValues(data.constraints[i]).x + 24f + 14f;
                if (x > 0f && x + w > contentW) { x = 0f; y += ChipH + ChipGap; }
                chip.sizeDelta = new Vector2(w, ChipH);
                chip.GetChild(0).GetComponent<RectTransform>().sizeDelta = new Vector2(w, ChipH);
                label.rectTransform.sizeDelta = new Vector2(w - 24f - 8f, ChipH);
                Fill(chip, "Dot", 11f, (ChipH - 6f) * 0.5f, 6f, 6f, Accent, 3f, true);
                Place(chip, x, y);
                var group = Group(chip.gameObject, 0f);
                constraintChips.Add(new ChipRef { rect = chip, group = group, basePos = chip.anchoredPosition });
                x += w + ChipGap;
            }
            float chipsBottom = y + ChipH;

            // Queries.
            float queriesY = chipsBottom + 12f;
            for (int i = 0; i < data.queries.Count; i++)
                leftQueries.Add(BuildQueryRow(body, data.queries[i], queriesY + i * QueryH, contentW));

            // Camera grid.
            float gridY = queriesY + data.queries.Count * QueryH + 12f;
            float cardW = (contentW - CardGapX * (GridColumns - 1)) / GridColumns;
            int rows = Mathf.CeilToInt(data.cameras.Count / (float)GridColumns);
            for (int i = 0; i < data.cameras.Count; i++)
            {
                int col = i % GridColumns, row = i / GridColumns;
                cameraCards.Add(BuildCameraCard(body, data.cameras[i], col * (cardW + CardGapX), gridY + row * (CardH + CardGapY), cardW));
            }
            float contentH = gridY + rows * CardH + (rows - 1) * CardGapY;
            left.panel.SizeMeters = new Vector2(LeftW, contentH + HeaderBlock) * ForgePanel.MetersPerUnit;
        }

        QueryRow BuildQueryRow(RectTransform parent, QueryData q, float y, float width)
        {
            var row = new QueryRow();
            row.root = Box(parent, "Query", 0f, y, width, QueryH);
            row.basePos = row.root.anchoredPosition;
            row.group = Group(row.root.gameObject, 0f);

            RectTransform spinner = Box(row.root, "Spinner", 2f, 4f, 16f, 16f);
            row.disc = Fill(spinner, "Disc", 0f, 0f, 16f, 16f, new Color(1f, 1f, 1f, 0.10f), 8f, true);
            row.orbit = Box(spinner, "Orbit", 0f, 0f, 16f, 16f);
            row.orbit.pivot = new Vector2(0.5f, 0.5f);
            row.orbit.anchoredPosition = new Vector2(8f, -8f);
            Fill(row.orbit, "Dot", 5.5f, 0.5f, 5f, 5f, Accent, 2.5f, true);
            row.check = Check(spinner, 8f, 8f, 12f, PassText);
            row.checkGroup = Group(row.check.gameObject, 0f);

            row.text = Label(row.root, q.text, 14f, Ink, 28f, 0f, width - 28f - 96f, QueryH);
            row.results = Label(row.root, q.results + " results", 14f, Sub, width - 96f, 0f, 96f, QueryH, FontStyles.Normal, TextAlignmentOptions.MidlineRight);
            row.results.alpha = 0f;
            return row;
        }

        CameraCard BuildCameraCard(RectTransform parent, CameraData c, float x, float y, float width)
        {
            var card = new CameraCard { data = c };
            card.root = Box(parent, "Card " + c.id, x, y, width, CardH);
            card.basePos = card.root.anchoredPosition;
            card.group = Group(card.root.gameObject, 0f);
            card.fill = Fill(card.root, "Bg", 0f, 0f, width, CardH, CardFill, 14f);

            RoundedPicture(card.root, Picture(c.image), 8f, 8f, 52f, 52f, 10f);
            float tx = 70f, tw = width - tx - 8f;
            Label(card.root, c.name, 16f, Ink, tx, 6f, tw, 22f, FontStyles.Bold);
            Label(card.root, c.source, 14f, Sub, tx, 27f, tw, 18f);
            SpecsData s = c.specs;
            Label(card.root, s.resolution + ", " + s.fps + " fps, " + s.fov + " deg", 14f, Ink, tx, 45f, tw, 18f);
            string ships = "Ships in " + s.shipsDays + (s.shipsDays == 1 ? " day" : " days");
            Label(card.root, ships, 14f, s.shipsDays > 7 ? Amber : Sub, 10f, 68f, width - 20f, 18f);

            // Four check chips: grey until the check runs, then green or red.
            float cx = 8f;
            for (int i = 0; i < ChecksData.Count; i++)
            {
                CheckData check = c.checks.At(i);
                RectTransform chip = Box(card.root, "Chip " + i, cx, 92f, 10f, 26f);
                card.chipFill[i] = Fill(chip, "Bg", 0f, 0f, 10f, 26f, PendingFill, 13f, true);
                card.chipText[i] = Label(chip, check.label, 14f, PendingText, 0f, 0f, 10f, 26f, FontStyles.Normal, TextAlignmentOptions.Midline);
                float w = card.chipText[i].GetPreferredValues(check.label).x + 18f;
                chip.sizeDelta = new Vector2(w, 26f);
                card.chipFill[i].rectTransform.sizeDelta = new Vector2(w, 26f);
                card.chipText[i].rectTransform.sizeDelta = new Vector2(w, 26f);
                card.chipText[i].overflowMode = TextOverflowModes.Overflow;
                cx += w + 5f;
            }

            // "In your parts list": shown on the winner after it was added.
            card.addedMark = Box(card.root, "Added", width - 30f, 8f, 22f, 22f);
            Fill(card.addedMark, "Disc", 0f, 0f, 22f, 22f, new Color(Pass.r, Pass.g, Pass.b, 0.22f), 11f, true);
            Check(card.addedMark, 11f, 11f, 15f, PassText);
            card.addedGroup = Group(card.addedMark.gameObject, 0f);
            return card;
        }

        // ------------------------------------------------------------------ the model-hub lane

        void BuildRight()
        {
            RectTransform body = right.body;
            float contentW = RightW - 36f;
            modelQuery = BuildQueryRow(body, new QueryData { text = data.modelsQuery, results = data.models.Count }, 0f, contentW);
            float y = QueryH + 14f;
            for (int i = 0; i < data.models.Count; i++)
                modelCards.Add(BuildModelCard(body, data.models[i], 0f, y + i * (ModelH + ModelGap), contentW));
            float contentH = y + data.models.Count * ModelH + (data.models.Count - 1) * ModelGap;
            right.panel.SizeMeters = new Vector2(RightW, contentH + HeaderBlock) * ForgePanel.MetersPerUnit;
        }

        ModelCard BuildModelCard(RectTransform parent, ModelData m, float x, float y, float width)
        {
            var card = new ModelCard { data = m };
            card.root = Box(parent, "Model " + m.id, x, y, width, ModelH);
            card.basePos = card.root.anchoredPosition;
            card.group = Group(card.root.gameObject, 0f);
            Fill(card.root, "Bg", 0f, 0f, width, ModelH, CardFill, 14f);
            RoundedPicture(card.root, Picture(m.image), 12f, 12f, 60f, 60f, 10f);

            float tx = 84f, tw = width - tx - 12f;
            Label(card.root, m.name, 16f, Ink, tx, 8f, tw - 76f, 22f, FontStyles.Bold);
            Label(card.root, m.task + ", " + m.@params + " params", 14f, Sub, tx, 30f, tw, 20f);
            Label(card.root, m.license + ", " + m.latencyMs + " ms, " + m.downloads + " downloads", 14f, Ink, tx, 52f, tw, 20f);

            // The pick: a ring and a small "Best fit" pill, both drawn from the start and shown at the end.
            if (m.pick)
            {
                RectTransform ringRect = Box(card.root, "Ring", -3f, -3f, width + 6f, ModelH + 6f);
                card.ring = ringRect.gameObject.AddComponent<Image>();
                card.ring.sprite = Forge.OS.OSUi.OutlineSprite();
                card.ring.raycastTarget = false;
                SetCorner(card.ring, 17f, 22);
                card.ring.color = new Color(Accent.r, Accent.g, Accent.b, 0f);

                RectTransform pill = Box(card.root, "Pick", width - 12f - 74f, 8f, 74f, 24f);
                Fill(pill, "Bg", 0f, 0f, 74f, 24f, Accent, 12f, true);
                Label(pill, "Best fit", 14f, Color.white, 0f, 0f, 74f, 24f, FontStyles.Bold, TextAlignmentOptions.Midline);
                card.pickGroup = Group(pill.gameObject, 0f);
            }
            return card;
        }

        // ------------------------------------------------------------------ B3: the searches

        IEnumerator RunCameras()
        {
            camerasRunning = true;
            OpenPanels(false);
            if (left == null) { camerasRunning = false; yield break; }
            yield return new WaitForSeconds(StartDelay);

            SetStatus(left, "Searching", true);
            for (int i = 0; i < leftQueries.Count; i++)
                yield return RunQuery(leftQueries[i], i == 0 ? 0f : 0.05f);

            SetStatus(left, "Checking against your drone", true);
            for (int i = 0; i < cameraCards.Count; i++)
            {
                RevealCard(cameraCards[i]);
                yield return new WaitForSeconds(CardInterval);
            }
            yield return new WaitForSeconds(ChipInterval * ChecksData.Count + 0.45f);

            SetStatus(left, cameraCards.Count + " cameras checked", false);
            camerasRunning = false;
            camerasDone = true;
            DemoActions.Raise("research.cameras.done");
        }

        IEnumerator RunQuery(QueryRow row, float extraDelay)
        {
            if (extraDelay > 0f) yield return new WaitForSeconds(extraDelay);
            Pop(row.group, row.root, row.basePos, 0f, 0.2f);
            spinning.Add(row.orbit);
            row.text.maxVisibleCharacters = 0;
            row.text.ForceMeshUpdate();
            int count = row.text.textInfo.characterCount;
            float typed = 0f;
            while (typed < count)
            {
                typed += Time.deltaTime * TypeCps;
                row.text.maxVisibleCharacters = Mathf.Min(count, (int)typed);
                yield return null;
            }
            row.text.maxVisibleCharacters = 99999;
            yield return new WaitForSeconds(QueryHold);
            FinishQuery(row, false);
        }

        void FinishQuery(QueryRow row, bool instant)
        {
            spinning.Remove(row.orbit);
            row.orbit.gameObject.SetActive(false);
            row.disc.color = new Color(Pass.r, Pass.g, Pass.b, 0.20f);
            row.text.maxVisibleCharacters = 99999;
            if (instant)
            {
                row.group.alpha = 1f;
                row.root.anchoredPosition = row.basePos;
                row.root.localScale = Vector3.one;
                row.checkGroup.alpha = 1f;
                row.results.alpha = 1f;
                return;
            }
            Tween(0.25f, 0f, t =>
            {
                float e = ForgeTween.EaseOutBack(t);
                row.checkGroup.alpha = Mathf.Clamp01(t * 3f);
                row.check.localScale = Vector3.one * e;
                row.results.alpha = t;
            });
        }

        void RevealCard(CameraCard card)
        {
            if (card.revealed) return;
            card.revealed = true;
            Pop(card.group, card.root, card.basePos, 0f, 0.3f);
            StartCoroutine(FlipChips(card, 0.3f));
        }

        IEnumerator FlipChips(CameraCard card, float delay)
        {
            yield return new WaitForSeconds(delay);
            for (int i = 0; i < ChecksData.Count; i++)
            {
                FlipChip(card, i, false);
                yield return new WaitForSeconds(ChipInterval);
            }
        }

        void FlipChip(CameraCard card, int i, bool instant)
        {
            if (card.chipDone[i]) return;
            card.chipDone[i] = true;
            bool pass = card.data.checks.At(i).pass;
            Color fill = pass ? new Color(Pass.r, Pass.g, Pass.b, 0.20f) : new Color(Fail.r, Fail.g, Fail.b, 0.20f);
            Color text = pass ? PassText : FailText;
            ForgeSurface surface = card.chipFill[i];
            TMP_Text label = card.chipText[i];
            if (instant) { surface.color = fill; label.color = text; return; }
            Tween(ChipFade, 0f, t =>
            {
                surface.color = Color.Lerp(PendingFill, fill, t);
                label.color = Color.Lerp(PendingText, text, t);
            });
        }

        void SetStatus(Lane lane, string text, bool thinking)
        {
            if (lane == null) return;
            lane.status.text = text;
            lane.thinking = thinking;
        }

        // Finishes everything the cameras run would do, at once (the verdict was asked for before the cards were in).
        void FinishCamerasInstantly()
        {
            if (left == null) return;
            for (int i = 0; i < leftQueries.Count; i++) FinishQuery(leftQueries[i], true);
            for (int i = 0; i < cameraCards.Count; i++)
            {
                CameraCard card = cameraCards[i];
                card.revealed = true;
                card.group.alpha = 1f;
                card.root.anchoredPosition = card.basePos;
                card.root.localScale = Vector3.one;
                for (int c = 0; c < ChecksData.Count; c++) FlipChip(card, c, true);
            }
            SetStatus(left, cameraCards.Count + " cameras checked", false);
            camerasDone = true;
        }

        // ------------------------------------------------------------------ B3: the model hub

        IEnumerator RunModels()
        {
            modelsRunning = true;
            OpenPanels(false);
            if (right == null) { modelsRunning = false; yield break; }
            yield return new WaitForSeconds(StartDelay);

            SetStatus(right, "Searching", true);
            yield return RunQuery(modelQuery, 0f);
            SetStatus(right, "Reading model cards", true);
            for (int i = 0; i < modelCards.Count; i++)
            {
                ModelCard card = modelCards[i];
                card.revealed = true;
                Pop(card.group, card.root, card.basePos, 0f, 0.3f);
                yield return new WaitForSeconds(ModelInterval);
            }
            yield return new WaitForSeconds(0.35f);

            // The pick gets its ring, the others settle back a little.
            for (int i = 0; i < modelCards.Count; i++)
            {
                ModelCard card = modelCards[i];
                if (card.data.pick && card.ring != null)
                {
                    Image ring = card.ring;
                    CanvasGroup pick = card.pickGroup;
                    Tween(0.45f, 0f, t =>
                    {
                        float e = ForgeTween.EaseOutCubic(t);
                        ring.color = new Color(Accent.r, Accent.g, Accent.b, e);
                        pick.alpha = e;
                    });
                }
                else
                {
                    CanvasGroup g = card.group;
                    Tween(0.45f, 0f, t => g.alpha = Mathf.Lerp(1f, 0.62f, ForgeTween.EaseOutCubic(t)));
                }
            }
            yield return new WaitForSeconds(0.5f);
            SetStatus(right, modelCards.Count + " models compared", false);
            modelsRunning = false;
            modelsDone = true;
            DemoActions.Raise("research.models.done");
        }

        // ------------------------------------------------------------------ B4: the verdict

        /// <summary>Where the verdict card sits: between Left and Center, a little nearer the user, clear of the drone.</summary>
        static Pose VerdictPose()
        {
            Pose l = DemoLayout.Get(DemoLayout.Slot.Left), c = DemoLayout.Get(DemoLayout.Slot.Center);
            Vector3 p = Vector3.Lerp(l.position, c.position, 0.5f);
            Transform head = DemoLayout.Head;
            Vector3 eye = head != null ? head.position : p - l.rotation * Vector3.forward * 0.6f;
            Vector3 toEye = eye - p; toEye.y = 0f;
            if (toEye.sqrMagnitude > 1e-4f) p += toEye.normalized * 0.14f;
            Vector3 look = p - eye; look.y = 0f;
            if (look.sqrMagnitude < 1e-4f) look = Vector3.forward;
            look.Normalize();
            p -= Vector3.Cross(Vector3.up, look) * 0.05f;   // a little to the left, so the drone's nose stays clear
            return new Pose(p, Quaternion.LookRotation(look, Vector3.up));
        }

        IEnumerator RunVerdict()
        {
            verdictRunning = true;
            OpenPanels(true);
            if (left == null) { verdictRunning = false; yield break; }
            if (!camerasDone) FinishCamerasInstantly();
            EnsureData();

            int winner = 0;
            for (int i = 0; i < cameraCards.Count; i++)
                if (cameraCards[i].data.id == data.verdict.winner) winner = i;
            CameraCard win = cameraCards[winner];

            // The other cards dim, the winner's cell empties as the card lifts out of it.
            for (int i = 0; i < cameraCards.Count; i++)
            {
                CanvasGroup g = cameraCards[i].group;
                float to = i == winner ? 0.12f : 0.28f;
                Tween(0.35f, 0f, t => g.alpha = Mathf.Lerp(1f, to, ForgeTween.EaseOutCubic(t)));
            }
            SetStatus(left, "Best match found", false);

            Pose target = VerdictPose();
            verdictPosition = target.position;
            verdictPositionSet = true;
            Vector3 cellCenter = win.root.TransformPoint(win.root.rect.center) - left.panel.transform.forward * 0.004f;
            float startScale = win.root.rect.width / VerdictW;

            verdict = BuildVerdict(win);
            Verdict v = verdict;
            Transform vt = v.panel.transform;
            Quaternion startRot = left.panel.transform.rotation;
            vt.SetPositionAndRotation(cellCenter, startRot);
            vt.localScale = v.baseScale * startScale;
            v.group.alpha = 0f;

            const float lift = 0.6f;
            float elapsed = 0f;
            while (elapsed < lift)
            {
                float t = elapsed / lift;
                float move = ForgeTween.SpringDamped(t, 0.85f, 1.3f);
                vt.SetPositionAndRotation(Vector3.LerpUnclamped(cellCenter, target.position, move), Quaternion.Slerp(startRot, target.rotation, ForgeTween.EaseInOutCubic(t)));
                vt.localScale = v.baseScale * Mathf.LerpUnclamped(startScale, 1f, move);
                v.group.alpha = Mathf.Clamp01(t * 4f);
                elapsed += Time.deltaTime;
                yield return null;
            }
            vt.SetPositionAndRotation(target.position, target.rotation);
            vt.localScale = v.baseScale;
            v.group.alpha = 1f;

            // Four lines tick on, one by one.
            yield return new WaitForSeconds(0.25f);
            for (int i = 0; i < v.rows.Count; i++)
            {
                VerdictRow row = v.rows[i];
                Pop(row.group, row.root, row.basePos, 0f, 0.28f);
                Tween(0.3f, 0.08f, t => row.check.localScale = Vector3.one * ForgeTween.EaseOutBack(t));
                yield return new WaitForSeconds(0.42f);
            }

            // Then the button.
            v.pillGroup.alpha = 0f;
            Tween(0.3f, 0f, t => v.pillGroup.alpha = ForgeTween.EaseOutCubic(t));
            yield return new WaitForSeconds(0.35f);
            v.pillGroup.blocksRaycasts = true;
            verdictShown = true;
            verdictRunning = false;
            DemoActions.Raise("research.verdict.shown");
        }

        Verdict BuildVerdict(CameraCard win)
        {
            var v = new Verdict();
            CameraData c = win.data;
            v.panel = SpawnPanel("verdict", "Best match", new Vector2(VerdictW, 520f), VerdictPose(), out v.group, out v.baseScale);
            RectTransform body = Body(v.panel.Content);
            float w = VerdictW - 36f;

            RoundedPicture(body, Picture(c.image), 0f, 0f, w, 100f, 14f);
            Label(body, c.name, 22f, Ink, 0f, 110f, w, 30f, FontStyles.Bold);
            Label(body, c.source, 14f, Sub, 0f, 140f, w, 18f);
            SpecsData s = c.specs;
            Label(body, s.resolution + ", " + s.fps + " fps, " + s.fov + " deg, " + s.weightG + " g", 14f, Ink, 0f, 160f, w, 18f);

            float y = 192f;
            for (int i = 0; i < data.verdict.lines.Count; i++)
            {
                VerdictLine line = data.verdict.lines[i];
                var row = new VerdictRow();
                row.root = Box(body, "Line " + line.key, 0f, y, w, 36f);
                row.basePos = row.root.anchoredPosition;
                row.group = Group(row.root.gameObject, 0f);
                Fill(row.root, "Disc", 0f, 6f, 24f, 24f, new Color(Pass.r, Pass.g, Pass.b, 0.20f), 12f, true);
                row.check = Check(row.root, 12f, 18f, 15f, PassText);
                row.check.localScale = Vector3.zero;
                Label(row.root, line.text, 17f, Ink, 36f, 0f, w - 36f, 36f);
                v.rows.Add(row);
                y += 36f;
            }

            y += 14f;
            v.pill = ForgePill.Create(body, "Add to parts list", OnAddClicked, 56f, 20f);
            v.pill.On = true;
            v.pill.Rect.anchorMin = v.pill.Rect.anchorMax = v.pill.Rect.pivot = new Vector2(0f, 1f);
            v.pill.Rect.anchoredPosition = new Vector2(0f, -y);
            v.pill.Rect.sizeDelta = new Vector2(w, 56f);
            var pillLabel = v.pill.GetComponentInChildren<TMP_Text>();
            if (pillLabel != null)
            {
                if (Typeface != null) pillLabel.font = Typeface;
                pillLabel.fontStyle = FontStyles.Bold;
                pillLabel.characterSpacing = 0f;
            }
            v.pillGroup = Group(v.pill.gameObject, 0f);
            float contentH = y + 56f;
            v.panel.SizeMeters = new Vector2(VerdictW, contentH + HeaderBlock) * ForgePanel.MetersPerUnit;
            return v;
        }

        // ------------------------------------------------------------------ B4: Add to parts list

        void OnAddClicked()
        {
            if (verdict == null || verdict.poked || !verdictShown || added) return;
            verdict.poked = true;
            StartCoroutine(AddRoutine());
        }

        IEnumerator AddRoutine()
        {
            Verdict v = verdict;
            ForgePill pill = v.pill;
            v.pillGroup.blocksRaycasts = false;
            pill.Button.interactable = false;

            // Pressed: a small dip, then the fill turns green and the label becomes "Added" with a check.
            RectTransform pr = pill.Rect;
            Vector3 one = Vector3.one;
            float elapsed = 0f;
            while (elapsed < 0.09f)
            {
                pr.localScale = one * Mathf.Lerp(1f, 0.95f, elapsed / 0.09f);
                elapsed += Time.deltaTime;
                yield return null;
            }
            pill.enabled = false;   // its own hover colouring would paint over ours
            ForgeSurface surface = pill.GetComponent<ForgeSurface>();
            TMP_Text label = pill.GetComponentInChildren<TMP_Text>();
            surface.color = Pass;
            label.text = "Added";
            label.color = new Color32(0x0E, 0x2A, 0x1A, 255);
            float labelW = label.GetPreferredValues("Added").x;
            RectTransform check = Check(pr, pr.rect.width * 0.5f - labelW * 0.5f - 18f, pr.rect.height * 0.5f, 20f, new Color32(0x0E, 0x2A, 0x1A, 255));
            Tween(0.2f, 0f, t => pr.localScale = one * Mathf.Lerp(0.95f, 1f, ForgeTween.EaseOutBack(t)));
            yield return new WaitForSeconds(0.65f);

            // The card shrinks back into its cell.
            CameraCard win = null;
            for (int i = 0; i < cameraCards.Count; i++)
                if (cameraCards[i].data.id == data.verdict.winner) win = cameraCards[i];
            if (win == null) win = cameraCards[0];
            Vector3 cellCenter = win.root.TransformPoint(win.root.rect.center) - left.panel.transform.forward * 0.004f;
            float endScale = win.root.rect.width / VerdictW;
            Transform vt = v.panel.transform;
            Vector3 fromPos = vt.position;
            Quaternion fromRot = vt.rotation;
            const float shrink = 0.5f;
            elapsed = 0f;
            while (elapsed < shrink)
            {
                float t = ForgeTween.EaseInOutCubic(elapsed / shrink);
                vt.SetPositionAndRotation(Vector3.Lerp(fromPos, cellCenter, t), Quaternion.Slerp(fromRot, left.panel.transform.rotation, t));
                vt.localScale = v.baseScale * Mathf.Lerp(1f, endScale, t);
                v.group.alpha = 1f - Mathf.Clamp01((t - 0.55f) / 0.45f);
                elapsed += Time.deltaTime;
                yield return null;
            }
            Destroy(v.panel.gameObject);
            verdict = null;

            // The winner's cell comes back with a green tick; the others stay dimmed a little.
            CanvasGroup winGroup = win.group;
            ForgeSurface winFill = win.fill;
            CanvasGroup mark = win.addedGroup;
            Tween(0.4f, 0f, t =>
            {
                float e = ForgeTween.EaseOutCubic(t);
                winGroup.alpha = Mathf.Lerp(0.12f, 1f, e);
                winFill.color = Color.Lerp(CardFill, new Color(Pass.r, Pass.g, Pass.b, 0.085f), e);
                mark.alpha = e;
            });
            for (int i = 0; i < cameraCards.Count; i++)
            {
                if (cameraCards[i] == win) continue;
                CanvasGroup g = cameraCards[i].group;
                Tween(0.4f, 0f, t => g.alpha = Mathf.Lerp(0.28f, 0.42f, t));
            }
            SetStatus(left, "Added to your parts list", false);
            yield return new WaitForSeconds(0.4f);
            added = true;
            verdictShown = false;
            DemoActions.Raise("research.added");
        }

        // ------------------------------------------------------------------ small animation helpers

        void Tween(float duration, float delay, System.Action<float> step, System.Action done = null)
        {
            StartCoroutine(ForgeTween.Run(duration, delay, step, done));
        }

        /// <summary>Fade in, slide up a little and settle: a card or line arriving.</summary>
        void Pop(CanvasGroup group, RectTransform rect, Vector2 basePos, float delay, float duration = 0.3f)
        {
            Tween(duration, delay, t =>
            {
                float e = ForgeTween.EaseOutCubic(t);
                group.alpha = e;
                rect.anchoredPosition = basePos + new Vector2(0f, -(1f - e) * 12f);
                rect.localScale = Vector3.one * Mathf.Lerp(0.96f, 1f, e);
            });
        }
    }
}
