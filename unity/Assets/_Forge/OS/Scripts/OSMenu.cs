using System;
using System.Collections;
using System.Collections.Generic;
using Oculus.Interaction;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// The Albie menu view. A UI Set backplate (world-space canvas with poke, ray and mouse input) holding a tab bar (Tools,
    /// Projects, Artifacts, Close), a grid of tiles (4 columns by 2 rows a page, with paging when there are more) read from
    /// <see cref="OSRegistry"/>, and a bottom strip (New project, Agents, Settings, Spaces, Bring here, Close). It
    /// follows <see cref="OSShell"/> events: when the shell opens the menu it appears where it was summoned and grows from
    /// the summon point. It sits within easy poking reach (see <see cref="Pose"/>: 0.47 m from the eyes and 20 degrees below
    /// eye level by default, never nearer than 0.42 m, facing the user, its lower edge above the desk) and stays put while the user looks at it;
    /// when the user turns well away from it (or walks off) it glides round to where they face now. Sizes are OS units
    /// (0.65 mm) times <see cref="MenuScale"/>. Tiles are pooled; Update only compares a few numbers (no allocations).
    /// </summary>
    public sealed class OSMenu : MonoBehaviour
    {
        // ---- layout, in menu units (one unit is OSKit.unitMeters times MenuScale: 0.611 mm)
        const float Pad = 28f, TabHeight = 72f, TabWidth = 156f, Gap = 22f;  // 22 units is 13.4 mm at the menu scale
        // 226 wide tiles make the plate as wide as the strip of 22 unit body text buttons needs, with 22 units (13.4 mm) between them (New project, Agents, Settings, Spaces, Bring here, Close).
        // A tile is 138 x 66 mm at the menu scale.
        const float TileWidth = 226f, TileHeight = 108f, TileGap = 22f, HeadingHeight = 30f;
        const int Columns = 4, Rows = 2;
        const float GridWidth = Columns * TileWidth + (Columns - 1) * TileGap;
        const float GridHeight = Rows * TileHeight + (Rows - 1) * TileGap + HeadingHeight;
        // The strip's buttons are this tall: 50 units at the menu's scale is 30.6 mm, a hand-sized poke target.
        const float StripButtonHeight = 50f, StripHeight = 56f;
        const float PlateWidth = GridWidth + 2f * Pad;
        const float PlateHeight = Pad + TabHeight + Gap + GridHeight + Gap + StripHeight + Pad;
        const float GridTop = Pad + TabHeight + Gap;
        const float StripTop = GridTop + GridHeight + Gap;

        // ---- motion
        const float OpenSeconds = 0.22f, CloseSeconds = 0.12f, StartScale = 0.3f;

        // ---- size: the plate is drawn at this fraction of the OS unit, so at arm's length it still fits in the view
        // (0.63 m wide, about 67 degrees at 0.47 m), its text keeps the rubric's cap height for its distance, and every control
        // stays at least 28 mm (see OSCheck.MenuReach).
        /// <summary>The menu's scale against the OS unit.</summary>
        public const float MenuScale = 0.94f;

        // ---- placement: within easy poking reach (see Pose). Tune on the headset.
        /// <summary>The menu never comes nearer than this to the eyes (m): any nearer and the hand can't get in front of it.</summary>
        public const float ReachNearest = OSSettings.MinDistance;
        /// <summary>Nor further than this (m): any further and the arm has to lock straight to poke it.</summary>
        public const float ReachFarthest = OSSettings.MaxDistance;
        /// <summary>Degrees below eye level it may sit at (the setting is kept inside these).</summary>
        public const float MinDropDegrees = OSSettings.MinDrop, MaxDropDegrees = OSSettings.MaxDrop;
        // When a panel or a model stands in the way the menu comes at most this much nearer than its setting (m), in steps.
        const float NearerAtMost = 0.04f;
        // At a desk the plate's lower edge stays this far above the desk top (m), clear of the hands resting on it.
        const float DeskClearMeters = 0.04f;
        // Turned more than RefaceDegrees from the menu (or moved RefaceMeters from where it was placed) for RefaceHoldSeconds:
        // it glides round to where the user faces now, in RefaceSeconds.
        const float RefaceDegrees = 40f, RefaceMeters = 0.35f, RefaceHoldSeconds = 0.6f, RefaceSeconds = 0.3f;
        const float DistanceStep = 0.02f, ProbeYawDegrees = 10f, ProbeTiltDegrees = 5f;
        // The menu keeps this far in front of a panel's plane (m) and counts a panel's rectangle this much bigger (canvas units).
        const float ClearMarginMeters = 0.05f, ClearMarginUnits = 20f;
        // Cost of a place, in "degrees": turning or tilting costs TurnCost a degree, coming nearer costs one per centimetre.
        const float TurnCost = 2f;
        const int MaxProbeSteps = 3, MaxTiltSteps = 4, ProbeColumns = 7, ProbeRows = 4;  // tilt steps go down, up, further down, further up

        [SerializeField] GameObject visual;
        [SerializeField] RectTransform canvasRoot, plate;
        [SerializeField] CanvasGroup group;
        [SerializeField] UIThemeManager themeManager;

        struct Slot
        {
            public int entry;      // index into entries; -1 for a heading
            public int page;
            public float x, y;     // in the grid, units from its top-left
            public string heading;
        }

        OSShell shell;
        bool built, announcedMissingKit, syncingTabs;
        Coroutine motion;

        readonly List<OSEntry> entries = new List<OSEntry>();
        readonly List<Slot> slots = new List<Slot>();
        readonly List<OSTileView> tiles = new List<OSTileView>(), outlineTiles = new List<OSTileView>();
        readonly List<TMP_Text> headings = new List<TMP_Text>();
        int page, pageCount = 1;
        bool needTheme;

        Toggle[] tabs;
        Button prevButton, nextButton, closeX;
        TMP_Text pageText, emptyText;

        /// <summary>Where the menu last opened (for checks and tests).</summary>
        public Vector3 LastCenter { get; private set; }
        /// <summary>The way the menu faced when it last opened.</summary>
        public Quaternion LastRotation { get; private set; } = Quaternion.identity;
        /// <summary>The plate's size in metres at the OS unit.</summary>
        public Vector2 SizeMeters => new Vector2(PlateWidth, PlateHeight) * UnitMeters;
        /// <summary>Metres per menu unit: the OS unit times <see cref="MenuScale"/>.</summary>
        public static float UnitMeters => (OSKit.Current != null ? OSKit.Current.unitMeters : 0.00065f) * MenuScale;
        /// <summary>How many times the open menu glided round to the user because they turned away from it.</summary>
        public int Refaces { get; private set; }
        public bool Visible => visual != null && visual.activeSelf;
        public int PageCount => pageCount;
        public int Page => page;
        public IReadOnlyList<OSTileView> ActiveTiles => activeTiles;
        readonly List<OSTileView> activeTiles = new List<OSTileView>();

        void Awake()
        {
            shell = GetComponentInParent<OSShell>();
            if (visual != null) visual.SetActive(false);
        }

        void OnEnable()
        {
            if (shell == null) shell = GetComponentInParent<OSShell>();
            if (shell == null) return;
            shell.MenuChanged += OnMenuChanged;
            shell.TabChanged += OnTabChanged;
            shell.Windows.Changed += OnDataChanged;
            OSRegistry.Changed += OnDataChanged;
            if (shell.MenuOpen) OnMenuChanged(true);
        }

        void OnDisable()
        {
            if (shell != null)
            {
                shell.MenuChanged -= OnMenuChanged;
                shell.TabChanged -= OnTabChanged;
                if (shell.Windows != null) shell.Windows.Changed -= OnDataChanged;
            }
            OSRegistry.Changed -= OnDataChanged;
            if (motion != null) StopCoroutine(motion);
            motion = null;
            if (visual != null) visual.SetActive(false);
            RestoreContent();
        }

        // ---------------------------------------------------------------- shell events

        void OnMenuChanged(bool open)
        {
            if (motion != null) StopCoroutine(motion);
            motion = null;
            if (open) Show(); else Hide();
        }

        void OnTabChanged(OSTab tab)
        {
            page = 0;
            if (Visible) Refresh();
        }

        void OnDataChanged()
        {
            if (Visible && shell != null && shell.MenuOpen) Refresh();
        }

        // ---------------------------------------------------------------- show and hide

        void Show()
        {
            if (!Build()) return;
            page = 0;
            Vector3 to; Quaternion rotation;
            Pose(out to, out rotation);
            LastCenter = to;
            LastRotation = rotation;
            placedFrom = shell.Head != null ? shell.Head.position : Vector3.zero;
            turnedFor = 0f;
            HideCoveringContent(shell.Head != null ? shell.Head.position : Vector3.zero, to, rotation, SizeMeters);
            Vector3 from = shell.SummonPoint ?? to;
            transform.SetPositionAndRotation(from, rotation);
            transform.localScale = Vector3.one * StartScale;
            group.alpha = 0f;
            group.interactable = true;
            group.blocksRaycasts = true;
            // The tab bar's toggles come alive here and their group may switch one on by itself: that is not the user's
            // choice, so the shell's tab (set by voice or a key before the menu was ever built) must survive it.
            syncingTabs = true;
            try
            {
                visual.SetActive(true);  // before Refresh: the tab toggles' animators need to be running
                Refresh();
            }
            finally
            {
                syncingTabs = false;
            }
            OSSounds.Open();
            motion = StartCoroutine(Animate(from, to, OpenSeconds, true));
        }

        void Hide()
        {
            if (!Visible) return;
            OSSounds.Close();
            ClearSelection();
            group.interactable = false;
            group.blocksRaycasts = false;
            motion = StartCoroutine(Animate(transform.position, transform.position, CloseSeconds, false));
        }

        IEnumerator Animate(Vector3 from, Vector3 to, float seconds, bool opening)
        {
            float t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                Step(from, to, Mathf.Clamp01(t / seconds), opening);
                yield return null;
            }
            Step(from, to, 1f, opening);
            if (!opening)
            {
                visual.SetActive(false);
                RestoreContent();
            }
            motion = null;
        }

        void Step(Vector3 from, Vector3 to, float k, bool opening)
        {
            if (opening)
            {
                float eased = ForgeTween.EaseOutCubic(k);
                transform.position = Vector3.LerpUnclamped(from, to, eased);
                transform.localScale = Vector3.one * Mathf.Lerp(StartScale, 1f, eased);
                group.alpha = Mathf.Clamp01(k * 1.6f);  // the fade lands a little before the grow does
            }
            else
            {
                float eased = ForgeTween.EaseInCubic(k);
                transform.localScale = Vector3.one * Mathf.Lerp(1f, 0.94f, eased);
                group.alpha = 1f - eased;
            }
        }

        Vector3 placedFrom;
        float turnedFor;

        // While it is open and still: if the user has turned well away from it (or walked off) for a moment, it glides round
        // to where they face now. Only compares a few vectors a frame; the new place is worked out once, when it moves.
        void Update()
        {
            if (motion != null || !Visible || shell == null || !shell.MenuOpen || PanelManipulator.Grabbing > 0)
            {
                turnedFor = 0f;
                return;
            }
            Transform head = shell.Head;
            if (head == null) return;
            Vector3 toMenu = LastCenter - head.position;
            toMenu.y = 0f;
            Vector3 facing = head.forward;
            facing.y = 0f;
            bool turned = toMenu.sqrMagnitude > 1e-4f && facing.sqrMagnitude > 1e-4f && Vector3.Angle(facing, toMenu) > RefaceDegrees;
            bool moved = (head.position - placedFrom).sqrMagnitude > RefaceMeters * RefaceMeters;
            if (!turned && !moved)
            {
                turnedFor = 0f;
                return;
            }
            turnedFor += Time.unscaledDeltaTime;
            if (turnedFor < RefaceHoldSeconds) return;
            Reface();
        }

        /// <summary>Places the open menu again in front of the user, gliding there. For the turn above, and for checks.</summary>
        public void Reface()
        {
            if (!Visible || shell == null) return;
            turnedFor = 0f;
            Pose(out Vector3 to, out Quaternion rotation);
            LastCenter = to;
            LastRotation = rotation;
            placedFrom = shell.Head != null ? shell.Head.position : Vector3.zero;
            HideCoveringContent(placedFrom, to, rotation, SizeMeters);
            Refaces++;
            if (motion != null) StopCoroutine(motion);
            motion = StartCoroutine(Glide(transform.position, transform.rotation, to, rotation));
        }

        IEnumerator Glide(Vector3 fromPosition, Quaternion fromRotation, Vector3 toPosition, Quaternion toRotation)
        {
            float t = 0f;
            while (t < RefaceSeconds)
            {
                t += Time.unscaledDeltaTime;
                float e = ForgeTween.EaseOutCubic(Mathf.Clamp01(t / RefaceSeconds));
                transform.SetPositionAndRotation(Vector3.LerpUnclamped(fromPosition, toPosition, e), Quaternion.SlerpUnclamped(fromRotation, toRotation, e));
                yield return null;
            }
            transform.SetPositionAndRotation(toPosition, toRotation);
            motion = null;
        }

        /// <summary>The distance (m) and drop (degrees) the menu aims for: the settings, kept inside easy reach.</summary>
        public static void ReachSetting(out float distance, out float dropDegrees)
        {
            OSSettings settings = OSSettings.Current;
            distance = Mathf.Clamp(settings.menuDistance, ReachNearest, ReachFarthest);
            dropDegrees = Mathf.Clamp(settings.menuDropDegrees, MinDropDegrees, MaxDropDegrees);
        }

        /// <summary>
        /// The plain place within reach in front of <paramref name="head"/> (no search around obstacles), for a system surface
        /// the user touches that opens without the menu (the Spaces list by voice): centre and the rotation facing the eyes.
        /// </summary>
        public static void ReachPose(Transform head, out Vector3 center, out Quaternion rotation)
        {
            ReachSetting(out float distance, out float drop);
            Vector3 eye = head != null ? head.position : Vector3.zero;
            Vector3 flat = head != null ? head.forward : Vector3.forward;
            flat.y = 0f;
            flat = flat.sqrMagnitude < 1e-4f ? Vector3.forward : flat.normalized;
            Vector3 direction = Direction(flat, 0f, drop);
            center = eye + direction * distance;
            rotation = Quaternion.LookRotation(direction, Vector3.up);
        }

        /// <summary>
        /// Where the menu sits: within easy poking reach, straight ahead of where the head faces now, turned to face the eyes:
        /// <c>menuDistance</c> from the eyes and <c>menuDropDegrees</c> below eye level, kept inside 0.42 to 0.6 m and 10 to 30
        /// degrees (<see cref="ReachSetting"/>). At a desk the drop is lessened until the plate's lower edge is above the desk top,
        /// clear of hands resting there. The menu is the system surface, so it is in front of every window and mode panel:
        /// when a panel stands in the way it comes a little nearer (never nearer than <see cref="ReachNearest"/>), then tilts
        /// a little down or up, then turns a little to the side, to the cheapest place where nothing is in front of it. With
        /// no clear place it takes the one with the least in front of it (and hides the model parts that are left in front:
        /// <see cref="HideCoveringContent"/>), rather than coming close to the face.
        /// </summary>
        void Pose(out Vector3 position, out Quaternion rotation)
        {
            ReachSetting(out float wantDistance, out float wantDrop);
            Transform head = shell.Head;
            Vector3 eye = head != null ? head.position : Vector3.zero;
            Vector3 flat = head != null ? head.forward : Vector3.forward;
            flat.y = 0f;
            flat = flat.sqrMagnitude < 1e-4f ? Vector3.forward : flat.normalized;
            Vector2 size = SizeMeters;
            CollectVolumes();
            float deskTop = DeskTop();
            wantDrop = AboveDesk(eye, flat, wantDistance, wantDrop, size.y, deskTop);

            // Every place worth trying has a cost: coming nearer than the setting costs one per centimetre (at most
            // NearerAtMost, never nearer than ReachNearest), tilting or turning aside costs TurnCost per degree, so nearer
            // is preferred to turning. The cheapest place with nothing in front of any part of the plate wins; the settings'
            // own place costs nothing and is tried first, so with nothing in the way the menu sits where it is set.
            float nearest = Mathf.Max(ReachNearest, wantDistance - NearerAtMost);
            float bestDistance = wantDistance, bestCost = float.MaxValue;
            Vector3 bestDirection = Direction(flat, 0f, wantDrop);
            // First a place clear of panels, volumes and the model solids under them. A model's solids can stand nearer than the
            // menu may come (Train's slabs); then the place clear of panels and volumes alone is taken, and the open menu hides
            // the solids that are left in front of it (see HideCoveringContent).
            for (int pass = 0; pass < 2 && bestCost == float.MaxValue; pass++)
            {
                bool meshes = pass == 0;
                int fewestBlocked = int.MaxValue;
                for (int tilt = 0; tilt <= MaxTiltSteps; tilt++)
                {
                    // 0, then down, up, further down, further up; each kept inside the reach band and above the desk.
                    float step = ((tilt + 1) / 2) * ProbeTiltDegrees * (tilt % 2 == 1 ? 1f : -1f);
                    float drop = Mathf.Clamp(wantDrop + step, MinDropDegrees, MaxDropDegrees);
                    if (tilt > 0 && Mathf.Abs(drop - wantDrop) < 0.5f) continue;  // clamped onto a place already tried
                    for (int k = 0; k <= 2 * MaxProbeSteps; k++)
                    {
                        float yaw = ((k + 1) / 2) * ProbeYawDegrees * (k % 2 == 1 ? 1f : -1f);
                        float turning = TurnCost * (Mathf.Abs(yaw) + Mathf.Abs(drop - wantDrop));
                        if (turning >= bestCost) continue;
                        for (float d = wantDistance; d >= nearest - 0.001f; d -= DistanceStep)
                        {
                            float placeDrop = AboveDesk(eye, flat, d, drop, size.y, deskTop);
                            Vector3 direction = Direction(flat, yaw, placeDrop);
                            Quaternion facing = Quaternion.LookRotation(direction, Vector3.up);
                            float cost = turning + (wantDistance - d) * 100f;
                            // Once a clear place is known only a count of 0 matters, and before that only a count under the fewest so far:
                            // the count may stop at that limit (the answer to both questions is the same as with the full count).
                            int limit = bestCost == float.MaxValue ? fewestBlocked : 1;
                            int blocked = BlockedCore(eye, eye + direction * d, facing, size, meshes, limit);
                            if (blocked == 0)
                            {
                                if (cost < bestCost)
                                {
                                    bestCost = cost;
                                    bestDirection = direction;
                                    bestDistance = d;
                                    fewestBlocked = 0;
                                }
                                break;  // nearer only costs more
                            }
                            if (bestCost == float.MaxValue && blocked < fewestBlocked)
                            {
                                fewestBlocked = blocked;  // nothing clear yet: remember the place with the least in front of it
                                bestDirection = direction;
                                bestDistance = d;
                            }
                        }
                    }
                }
            }
            position = eye + bestDirection * bestDistance;
            rotation = Quaternion.LookRotation(bestDirection, Vector3.up);
        }

        // The desk top's height when the user is at a desk, else minus infinity.
        float DeskTop()
        {
            Workspace ws = shell != null ? shell.workspace : null;
            if (ws == null || ws.presence == null || !ws.presence.AtDesk || ws.presence.Frame == null) return float.NegativeInfinity;
            return ws.presence.Frame.position.y;
        }

        /// <summary>
        /// The drop (degrees) at or under <paramref name="drop"/> that keeps a plate <paramref name="height"/> m tall, centred
        /// <paramref name="distance"/> m from the eye and facing it, with its lower edge <c>DeskClearMeters</c> above
        /// <paramref name="deskTop"/>. Never under 0 (eye level): a desk that high leaves the menu level with the eyes.
        /// </summary>
        public static float AboveDesk(Vector3 eye, Vector3 flat, float distance, float drop, float height, float deskTop)
        {
            if (float.IsNegativeInfinity(deskTop)) return drop;
            for (float d = drop; d > 0f; d -= 2f)
            {
                float rad = d * Mathf.Deg2Rad;
                float bottom = eye.y - Mathf.Sin(rad) * distance - Mathf.Cos(rad) * height * 0.5f;
                if (bottom >= deskTop + DeskClearMeters) return d;
            }
            return 0f;
        }

        // ---------------------------------------------------------------- content in front of the plate

        // The model renderers the open menu switched off, so it can be restored when the menu goes.
        readonly List<Renderer> hiddenContent = new List<Renderer>();

        /// <summary>How many scene renderers the open menu has switched off because they stand in front of its plate (for checks).</summary>
        public int HiddenContentCount => hiddenContent.Count;

        // The menu is the system surface and nothing may draw over it. Placement already looks for a place with no panel or
        // model in front; when there is none (a model's solids stand nearer than the menu may come, as Train's slabs can),
        // whatever of them is still between the eyes and the plate is switched off while the menu is open and put back when
        // it closes. A solid really in front would otherwise hide tiles however the menu is sorted.
        void HideCoveringContent(Vector3 eye, Vector3 center, Quaternion rotation, Vector2 size)
        {
            RestoreContent();
            Vector3 right = rotation * Vector3.right, up = rotation * Vector3.up;
            for (int v = 0; v < contentRoots.Count; v++)
            {
                if (contentRoots[v] == null) continue;
                contentRoots[v].GetComponentsInChildren(false, rendererScratch);
                for (int i = 0; i < rendererScratch.Count; i++)
                {
                    Renderer r = rendererScratch[i];
                    if (r == null || !r.enabled || r.forceRenderingOff || !CoversPlate(r.bounds, eye, center, right, up, size)) continue;
                    r.forceRenderingOff = true;  // not `enabled`: a module may switch that back on every frame (Train's slabs do)
                    hiddenContent.Add(r);
                }
                rendererScratch.Clear();
            }
            if (hiddenContent.Count > 0) Debug.Log("[OS] The menu hides " + hiddenContent.Count + " model part(s) in front of it while it is open.");
        }

        void RestoreContent()
        {
            for (int i = 0; i < hiddenContent.Count; i++)
                if (hiddenContent[i] != null) hiddenContent[i].forceRenderingOff = false;
            hiddenContent.Clear();
        }

        // Whether this box stands between the eyes and any of a grid of points across the plate.
        static bool CoversPlate(Bounds box, Vector3 eye, Vector3 center, Vector3 right, Vector3 up, Vector2 size)
        {
            for (int ix = 0; ix < ProbeColumns; ix++)
            {
                float u = (ix / (ProbeColumns - 1f) - 0.5f) * size.x;
                for (int iy = 0; iy < ProbeRows; iy++)
                {
                    float v = (iy / (ProbeRows - 1f) - 0.5f) * size.y;
                    Vector3 toPoint = center + right * u + up * v - eye;
                    float length = toPoint.magnitude;
                    if (length < 1e-4f) continue;
                    if (box.IntersectRay(new Ray(eye, toPoint / length), out float distance) && distance <= length) return true;
                }
            }
            return false;
        }

        // The way the menu faces: the flat forward turned `yaw` degrees to the right and tilted `drop` degrees down.
        static Vector3 Direction(Vector3 flat, float yaw, float drop)
        {
            Vector3 turned = Quaternion.AngleAxis(yaw, Vector3.up) * flat;
            Vector3 right = new Vector3(turned.z, 0f, -turned.x);
            return Quaternion.AngleAxis(drop, right) * turned;
        }

        /// <summary>
        /// How many of a grid of points across a menu plate (centre <paramref name="center"/>, facing <paramref name="rotation"/>,
        /// <paramref name="size"/> in metres) have a panel in front of them or too close behind them as seen from
        /// <paramref name="eye"/>: 0 means a pointer reaches the whole plate. Panels are every <see cref="ForgePanel"/> (windows,
        /// a mode's panels, artifact panels) that is showing and takes pointer hits, or a model's solids (the meshes under a
        /// panel or volume; with <paramref name="meshes"/> false only the panel planes and volume boxes count, because the open
        /// menu switches the solids that remain in front of it off). For placing the menu and for the play test. The count stops
        /// as soon as it reaches <paramref name="limit"/> (the placement search only needs to know "none" or "fewer than the best so far").
        /// </summary>
        public static int Blocked(Vector3 eye, Vector3 center, Quaternion rotation, Vector2 size, bool meshes = true, int limit = int.MaxValue)
        {
            CollectVolumes();
            return BlockedCore(eye, center, rotation, size, meshes, limit);
        }

        static readonly List<ForgeVolume> volumes = new List<ForgeVolume>();
        // Everything with meshes that could stand in front of the plate: each volume and each panel (a panel can carry a 3D
        // stage whose solids reach well out of its plane, as Train's layer slabs do) with the world box its renderers cover.
        // Canvas graphics are not renderers, so a panel's own face isn't in here: the panel test handles that.
        static readonly List<Transform> contentRoots = new List<Transform>();
        static readonly List<Bounds> contentBounds = new List<Bounds>();
        static readonly List<Renderer> rendererScratch = new List<Renderer>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            volumes.Clear();
            contentRoots.Clear();
            contentBounds.Clear();
            rendererScratch.Clear();
        }

        // The 3D volumes (a mode's models, an artifact) and panels' 3D stages are in the way too: a solid in front of the plate
        // hides it.
        static void CollectVolumes()
        {
            volumes.Clear();
            contentRoots.Clear();
            contentBounds.Clear();
            foreach (ForgeVolume volume in UnityEngine.Object.FindObjectsByType<ForgeVolume>())
            {
                if (volume == null || !volume.gameObject.activeInHierarchy) continue;
                volumes.Add(volume);
                AddContent(volume.transform);
            }
            var panels = ForgePanel.All;
            for (int i = 0; i < panels.Count; i++)
                if (panels[i] != null && panels[i].gameObject.activeInHierarchy) AddContent(panels[i].transform);
        }

        static void AddContent(Transform root)
        {
            root.GetComponentsInChildren(false, rendererScratch);
            bool any = false;
            Bounds box = default;
            for (int i = 0; i < rendererScratch.Count; i++)
            {
                Renderer r = rendererScratch[i];
                if (r == null || !r.enabled) continue;
                if (!any) { box = r.bounds; any = true; }
                else box.Encapsulate(r.bounds);
            }
            rendererScratch.Clear();
            if (!any) return;
            contentRoots.Add(root);
            contentBounds.Add(box);
        }

        static int BlockedCore(Vector3 eye, Vector3 center, Quaternion rotation, Vector2 size, bool meshes, int limit)
        {
            int blocked = 0;
            Vector3 right = rotation * Vector3.right, up = rotation * Vector3.up;
            var panels = ForgePanel.All;
            for (int ix = 0; ix < ProbeColumns; ix++)
            {
                float u = (ix / (ProbeColumns - 1f) - 0.5f) * size.x * 0.96f;
                for (int iy = 0; iy < ProbeRows; iy++)
                {
                    float v = (iy / (ProbeRows - 1f) - 0.5f) * size.y * 0.96f;
                    Vector3 point = center + right * u + up * v;
                    Vector3 toPoint = point - eye;
                    float length = toPoint.magnitude;
                    if (length < 1e-4f) continue;
                    Vector3 ray = toPoint / length;
                    bool covered = false;
                    for (int i = 0; i < panels.Count && !covered; i++) covered = PanelCovers(panels[i], eye, ray, length);
                    for (int i = 0; i < volumes.Count && !covered; i++) covered = VolumeCovers(volumes[i], eye, ray, length);
                    for (int i = 0; meshes && i < contentBounds.Count && !covered; i++) covered = RayMeetsBounds(contentBounds[i], eye, ray, length);
                    if (covered && ++blocked >= limit) return blocked;
                }
            }
            return blocked;
        }

        // Whether the ray from the eye meets a world box (grown by the margin) no further than `length`. A box with no size
        // (a volume with nothing to draw) never does; a box the eye is inside does.
        static bool RayMeetsBounds(Bounds box, Vector3 eye, Vector3 ray, float length)
        {
            if (box.size == Vector3.zero) return false;
            box.Expand(2f * ClearMarginMeters);
            return box.IntersectRay(new Ray(eye, ray), out float distance) && distance <= length;
        }

        // Whether the ray from the eye meets the volume's box (its base on the origin, `size` metres, in the volume's own
        // axes) no further than the menu point plus the margin. A box the eye is inside counts as in the way.
        static bool VolumeCovers(ForgeVolume volume, Vector3 eye, Vector3 ray, float length)
        {
            if (volume == null) return false;
            Transform t = volume.transform;
            Vector3 origin = t.InverseTransformPoint(eye);
            Vector3 direction = t.InverseTransformPoint(eye + ray) - origin;  // local, not normalised: one unit along it is one metre in the world
            Vector3 half = volume.size * 0.5f + Vector3.one * ClearMarginMeters;
            Vector3 centre = new Vector3(0f, volume.size.y * 0.5f, 0f);
            float enter = float.NegativeInfinity, exit = float.PositiveInfinity;
            for (int axis = 0; axis < 3; axis++)
            {
                float o = origin[axis] - centre[axis], d = direction[axis];
                if (Mathf.Abs(d) < 1e-6f)
                {
                    if (Mathf.Abs(o) > half[axis]) return false;
                    continue;
                }
                float a = (-half[axis] - o) / d, b = (half[axis] - o) / d;
                if (a > b) { float swap = a; a = b; b = swap; }
                enter = Mathf.Max(enter, a);
                exit = Mathf.Min(exit, b);
                if (enter > exit) return false;
            }
            return exit > 0f && enter <= length;
        }

        // Whether the ray from the eye along `ray` meets this panel's rectangle no further than `length` (the menu point)
        // plus a small margin, so the menu is clear of the panel by more than a hair.
        static bool PanelCovers(ForgePanel panel, Vector3 eye, Vector3 ray, float length)
        {
            if (panel == null || !panel.gameObject.activeInHierarchy) return false;
            var rect = panel.transform as RectTransform;
            if (rect == null) return false;
            var group = panel.GetComponent<CanvasGroup>();
            if (group != null && (group.alpha < 0.05f || !group.blocksRaycasts)) return false;  // gone or on its way out
            Vector3 normal = rect.forward;
            float along = Vector3.Dot(normal, ray);
            if (Mathf.Abs(along) < 0.02f) return false;
            float t = Vector3.Dot(normal, rect.position - eye) / along;
            if (t <= 0f || t > length + ClearMarginMeters) return false;  // behind the eye, or well behind the menu point
            Vector3 local = rect.InverseTransformPoint(eye + ray * t);
            Rect area = rect.rect;
            area.xMin -= ClearMarginUnits;
            area.xMax += ClearMarginUnits;
            area.yMin -= ClearMarginUnits;
            area.yMax += ClearMarginUnits;
            return area.Contains(new Vector2(local.x, local.y));
        }

        // ---------------------------------------------------------------- building

        /// <summary>True once the plate's controls exist (built on the first open, or ahead of it by <see cref="Prewarm"/>).</summary>
        public bool IsBuilt => built;

        /// <summary>Builds the plate's controls now, while the menu is put away, so the first open does not have to. Does nothing once built or while open.</summary>
        public void Prewarm()
        {
            if (built || Visible || (shell != null && shell.MenuOpen)) return;
            Build();
        }

        /// <summary>Builds the plate's contents from the kit on first use. False when the kit is missing.</summary>
        bool Build()
        {
            if (built) return true;
            OSKit kit = OSKit.Current;
            if (kit == null || kit.tabToggle == null || kit.textTile == null || canvasRoot == null || plate == null)
            {
                if (!announcedMissingKit)
                {
                    announcedMissingKit = true;
                    Debug.Log("[OS] The menu can't open: Resources/OSKit.asset is missing or incomplete (Forge > OS > Build OS Prefabs).");
                }
                return false;
            }
            built = true;

            // The backplate prefab lays itself out; this plate is placed by hand.
            foreach (var layout in canvasRoot.GetComponents<LayoutGroup>()) layout.enabled = false;
            foreach (var layout in plate.GetComponents<LayoutGroup>()) layout.enabled = false;
            canvasRoot.sizeDelta = new Vector2(PlateWidth, PlateHeight);
            canvasRoot.localScale = Vector3.one * kit.unitMeters * MenuScale;
            plate.anchorMin = plate.anchorMax = plate.pivot = new Vector2(0.5f, 0.5f);
            plate.anchoredPosition = Vector2.zero;
            plate.sizeDelta = new Vector2(PlateWidth, PlateHeight);
            var rounded = plate.GetComponent<Oculus.Interaction.Samples.RoundedBoxUIProperties>();
            if (rounded != null) rounded.borderRadius = new Vector4(36f, 36f, 36f, 36f);
            OSBody.EnsureCanvas(canvasRoot.GetComponent<Canvas>());
            OSBody.RaiseAboveScene(canvasRoot.gameObject);
            // The menu plays its own press sound for every control, so the plate's poke sound would double it.
            var wrapper = GetComponentInChildren<PointableCanvasUnityEventWrapper>(true);
            if (wrapper != null) wrapper.enabled = false;

            // Tab bar
            var group2 = plate.gameObject.AddComponent<ToggleGroup>();
            group2.allowSwitchOff = false;
            tabs = new Toggle[3];
            string[] labels = { "Tools", "Projects", "Artifacts" };
            string[] icons = { "tools", "projects", "artifacts" };
            for (int i = 0; i < 3; i++)
            {
                OSTab tab = (OSTab)i;
                // The shell's tab is the truth. The toggle group lights its first toggle when the bar comes alive, and that must
                // not be taken for the user choosing Tools (see Show).
                tabs[i] = OSUi.Tab(plate, labels[i], icons[i], group2, () => { if (shell != null && !syncingTabs) shell.SetTab(tab); }, TabWidth, TabHeight);
                PlaceTopLeft(tabs[i].transform, Pad + i * (TabWidth + Gap), Pad);
            }

            float closeSize = 56f;
            closeX = OSUi.Button(plate, null, CloseMenu, OSButtonStyle.Borderless, "close");
            OSUi.Size(closeX.transform, closeSize, closeSize);
            PlaceTopLeft(closeX.transform, PlateWidth - Pad - closeSize, Pad + (TabHeight - closeSize) * 0.5f);

            // Paging: < 1 / 2 >, only shown when there is more than a page.
            float x = PlateWidth - Pad - closeSize - Gap - 56f;
            nextButton = OSUi.Button(plate, null, () => SetPage(page + 1), OSButtonStyle.Borderless, "chevron_right");
            OSUi.Size(nextButton.transform, 56f, 56f);
            PlaceTopLeft(nextButton.transform, x, Pad + (TabHeight - 56f) * 0.5f);
            pageText = OSUi.Text(plate, "1 / 2", OSUi.BodyText, true);
            pageText.GetComponent<LayoutElement>().ignoreLayout = true;
            pageText.alignment = TextAlignmentOptions.Center;
            pageText.textWrappingMode = TextWrappingModes.NoWrap;
            PlaceTopLeft(pageText.transform, x - 76f, Pad + (TabHeight - 24f) * 0.5f);
            ((RectTransform)pageText.transform).sizeDelta = new Vector2(76f, 24f);
            prevButton = OSUi.Button(plate, null, () => SetPage(page - 1), OSButtonStyle.Borderless, "chevron_left");
            OSUi.Size(prevButton.transform, 56f, 56f);
            PlaceTopLeft(prevButton.transform, x - 76f - 56f, Pad + (TabHeight - 56f) * 0.5f);

            // Empty-state sentence, centred in the grid area.
            emptyText = OSUi.Text(plate, "", OSUi.SmallText, true);
            emptyText.GetComponent<LayoutElement>().ignoreLayout = true;
            emptyText.alignment = TextAlignmentOptions.Center;
            var emptyRect = (RectTransform)emptyText.transform;
            PlaceTopLeft(emptyRect, Pad + GridWidth * 0.2f, GridTop);
            emptyRect.sizeDelta = new Vector2(GridWidth * 0.6f, GridHeight);

            // Bottom strip: the system pages, the Spaces list (everything open around the user), Bring here, and Close at
            // the right end, nearest the hand that reaches up from below.
            Button[] strip =
            {
                StripButton("New project", "build", OSButtonStyle.Primary, () => OpenSystem("build")),
                StripButton("Agents", "agents", OSButtonStyle.Secondary, () => OpenSystem("agents")),
                StripButton("Settings", "settings", OSButtonStyle.Secondary, () => OpenSystem("settings")),
                StripButton("Spaces", "apps", OSButtonStyle.Secondary, OpenSpaces),
                StripButton("Bring here", "bring", OSButtonStyle.Secondary, BringWindows),
            };
            Button close = StripButton("Close", null, OSButtonStyle.Secondary, CloseMenu);
            LayStrip(strip, close);

            if (themeManager != null) themeManager.ApplyCurrentTheme();
            return true;
        }

        Button StripButton(string label, string icon, OSButtonStyle style, Action onClick)
        {
            Button button = OSUi.Button(plate, label, onClick, style, icon);
            if (button == null) return null;
            var rect = (RectTransform)button.transform;
            OSUi.Size(rect, rect.sizeDelta.x, StripButtonHeight);
            return button;
        }

        // Left to right with Gap between, Close at the right end. If the labels came out too wide for the plate, the icons go
        // from the secondary buttons, rightmost first, until the row fits (a label alone is still a hand-sized target).
        void LayStrip(Button[] strip, Button close)
        {
            float available = PlateWidth - 2f * Pad;
            float closeWidth = close != null ? ((RectTransform)close.transform).sizeDelta.x + Gap : 0f;
            for (int drop = strip.Length - 1; drop >= 1 && StripWidth(strip) + closeWidth > available; drop--)
                DropIcon(strip[drop]);
            float y = StripTop + (StripHeight - StripButtonHeight) * 0.5f;
            float x = Pad;
            for (int i = 0; i < strip.Length; i++)
            {
                if (strip[i] == null) continue;
                var rect = (RectTransform)strip[i].transform;
                PlaceTopLeft(rect, x, y);
                x += rect.sizeDelta.x + Gap;
            }
            if (close != null)
            {
                var closeRect = (RectTransform)close.transform;
                PlaceTopLeft(closeRect, PlateWidth - Pad - closeRect.sizeDelta.x, y);
            }
        }

        static float StripWidth(Button[] strip)
        {
            float width = 0f;
            for (int i = 0; i < strip.Length; i++)
                if (strip[i] != null) width += ((RectTransform)strip[i].transform).sizeDelta.x + Gap;
            return width - Gap;
        }

        static void DropIcon(Button button)
        {
            if (button == null) return;
            Transform elements = button.transform.Find("Content/Background/Elements");
            Transform icon = elements != null ? elements.Find("Icon") : null;
            if (icon == null || !icon.gameObject.activeSelf) return;
            icon.gameObject.SetActive(false);
            Transform gap = elements.Find("Gap");
            if (gap != null) gap.gameObject.SetActive(false);
            var rect = (RectTransform)button.transform;
            OSUi.Size(rect, Mathf.Max(96f, rect.sizeDelta.x - 32f), rect.sizeDelta.y);
        }

        static void PlaceTopLeft(Transform t, float x, float y)
        {
            var rect = (RectTransform)t;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);  // from the plate's top-left corner
        }

        // ---------------------------------------------------------------- strip actions

        void CloseMenu()
        {
            if (shell != null) shell.CloseMenu();
        }

        void OpenSystem(string id)
        {
            if (shell == null) return;
            shell.OpenSystem(id);
            if (OSShell.HasSystem(id)) shell.CloseMenu();
        }

        void BringWindows()
        {
            if (shell == null) return;
            shell.Windows.BringHere();
            shell.CloseMenu();
        }

        // The Spaces list opens where the menu is now (within reach), and the menu goes.
        void OpenSpaces()
        {
            if (shell == null) return;
            OSSpacesWindow.Open(LastCenter, LastRotation);
            shell.CloseMenu();
        }

        void SetPage(int value)
        {
            value = Mathf.Clamp(value, 0, pageCount - 1);
            if (value == page) return;
            page = value;
            Refresh();
        }

        // ---------------------------------------------------------------- data

        /// <summary>Reads the current tab from the registry and lays the page out. Reuses tile objects.</summary>
        public void Refresh()
        {
            if (!built || shell == null) return;
            // A tile's status line can change the lists while they are being laid out (the Gestures line hands the gesture display
            // to the window manager the first time it is seen, which tells the registry). Run it again after this pass, not inside it.
            if (refreshing)
            {
                refreshAgain = true;
                return;
            }
            refreshing = true;
            try
            {
                do
                {
                    refreshAgain = false;
                    RefreshOnce();
                }
                while (refreshAgain);
            }
            finally
            {
                refreshing = false;
            }
        }

        bool refreshing, refreshAgain;

        void RefreshOnce()
        {
            OSTab tab = shell.Tab;
            for (int i = 0; i < tabs.Length; i++) OSUi.Select(tabs[i], i == (int)tab);

            OSRegistry.Fill(tab, entries);
            LayoutSlots();
            page = Mathf.Clamp(page, 0, pageCount - 1);

            for (int i = 0; i < tiles.Count; i++) tiles[i].gameObject.SetActive(false);
            for (int i = 0; i < outlineTiles.Count; i++) outlineTiles[i].gameObject.SetActive(false);
            for (int i = 0; i < headings.Count; i++) headings[i].gameObject.SetActive(false);
            activeTiles.Clear();

            int tileIndex = 0, outlineIndex = 0, headingIndex = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                Slot slot = slots[i];
                if (slot.page != page) continue;
                if (slot.entry < 0)
                {
                    TMP_Text heading = Pooled(headings, ref headingIndex);
                    heading.text = slot.heading;
                    PlaceTopLeft(heading.transform, Pad, GridTop + slot.y);
                    ((RectTransform)heading.transform).sizeDelta = new Vector2(GridWidth, HeadingHeight);
                    continue;
                }
                OSEntry entry = entries[slot.entry];
                OSTileView tile = entry.outline ? PooledTile(outlineTiles, ref outlineIndex, true) : PooledTile(tiles, ref tileIndex, false);
                if (tile == null) continue;
                tile.Show(entry);
                PlaceTopLeft(tile.rect, Pad + slot.x, GridTop + slot.y);
                activeTiles.Add(tile);
            }

            emptyText.gameObject.SetActive(entries.Count == 0);
            if (entries.Count == 0) emptyText.text = OSRegistry.EmptyText(tab);

            bool paged = pageCount > 1;
            prevButton.gameObject.SetActive(paged);
            nextButton.gameObject.SetActive(paged);
            pageText.gameObject.SetActive(paged);
            if (paged)
            {
                prevButton.interactable = page > 0;
                nextButton.interactable = page < pageCount - 1;
                pageText.text = (page + 1) + " / " + pageCount;
            }

            if (needTheme && themeManager != null)
            {
                needTheme = false;
                themeManager.ApplyCurrentTheme();
            }
        }

        TMP_Text Pooled(List<TMP_Text> pool, ref int index)
        {
            if (index < pool.Count) return Activate(pool[index++]);
            TMP_Text heading = OSUi.Text(plate, "", OSUi.BodyText, true);
            heading.GetComponent<LayoutElement>().ignoreLayout = true;
            heading.fontStyle = FontStyles.Bold;
            heading.textWrappingMode = TextWrappingModes.NoWrap;
            pool.Add(heading);
            index++;
            needTheme = true;
            return heading;
        }

        static TMP_Text Activate(TMP_Text text)
        {
            text.gameObject.SetActive(true);
            return text;
        }

        OSTileView PooledTile(List<OSTileView> pool, ref int index, bool outline)
        {
            if (index < pool.Count)
            {
                OSTileView existing = pool[index++];
                existing.gameObject.SetActive(true);
                return existing;
            }
            OSTileView tile = OSUi.Tile(plate, outline, TileWidth, TileHeight);
            if (tile == null) return null;
            tile.GetComponent<LayoutElement>().ignoreLayout = true;
            OSTileView captured = tile;
            tile.button.onClick.AddListener(() => OnTile(captured));
            pool.Add(tile);
            index++;
            needTheme = true;
            return tile;
        }

        void OnTile(OSTileView tile)
        {
            OSEntry entry = tile.entry;
            if (entry == null) return;
            if (entry.open == null)
            {
                OSSounds.Error();
                Debug.Log("[OS] '" + entry.title + "' can't be opened right now.");
                return;
            }
            try
            {
                entry.open();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            ClearSelection();
            if (shell != null) shell.CloseMenu();
        }

        // A pressed UI Set control stays "selected" in the EventSystem and keeps its highlight; the next visit shouldn't.
        static void ClearSelection()
        {
            var system = UnityEngine.EventSystems.EventSystem.current;
            if (system != null && system.currentSelectedGameObject != null) system.SetSelectedGameObject(null);
        }

        // Works out which page, column and row every entry lands on. A section heading starts a new row; a page holds two rows
        // of tiles and at most one heading, and a heading is repeated at the top of a page its section carries onto.
        void LayoutSlots()
        {
            slots.Clear();
            int pageIndex = 0, column = 0, rows = 0;
            float y = 0f;
            bool headingOnPage = false, pageStart = true;
            string previous = null;
            for (int i = 0; i < entries.Count; i++)
            {
                OSEntry e = entries[i];
                string section = e.section;
                bool changed = !pageStart && section != previous;
                if (changed && column != 0)
                {
                    y += TileHeight + TileGap;
                    column = 0;
                }
                if (column == 0)
                {
                    bool wantHeading = section != null && (pageStart || changed);
                    if (!pageStart && (rows >= Rows || (wantHeading && headingOnPage)))
                    {
                        pageIndex++;
                        y = 0f;
                        rows = 0;
                        headingOnPage = false;
                        pageStart = true;
                        wantHeading = section != null;
                    }
                    if (wantHeading)
                    {
                        slots.Add(new Slot { entry = -1, page = pageIndex, x = 0f, y = y, heading = section });
                        y += HeadingHeight;
                        headingOnPage = true;
                    }
                    rows++;
                }
                slots.Add(new Slot { entry = i, page = pageIndex, x = column * (TileWidth + TileGap), y = y });
                column++;
                if (column == Columns)
                {
                    column = 0;
                    y += TileHeight + TileGap;
                }
                pageStart = false;
                previous = section;
            }
            pageCount = entries.Count == 0 ? 1 : pageIndex + 1;
        }
    }
}
