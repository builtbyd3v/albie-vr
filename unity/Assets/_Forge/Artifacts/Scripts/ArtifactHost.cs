using System.Collections;
using System.Collections.Generic;
using Forge.Notebook;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.Artifacts
{
    /// <summary>
    /// Opens an artifact: read it, validate it, ask for its permissions the first time, then build it. It needs no project,
    /// no desk and no hub, so a standalone artifact is as normal as a project one (docs/ARTIFACTS_BRIEF.md). Anything it
    /// can't do it says so in a panel: it never shows half an artifact.
    /// Put the prefab (Artifacts/Resources/ArtifactHost.prefab) where artifacts should appear, then call <see cref="Open"/>.
    /// </summary>
    public class ArtifactHost : MonoBehaviour
    {
        public ForgePanel panelPrefab;
        public ForgeVolume volumePrefab;
        public Vector3 volumeSize = new Vector3(0.45f, 0.45f, 0.45f);
        [Tooltip("Where a panel with no pos goes, from this transform (m).")]
        public Vector3 panelOffset = new Vector3(0.5f, 0.3f, 0f);
        public Color accent = ForgeTheme.Tool;

        readonly List<GameObject> spawned = new List<GameObject>();
        readonly Dictionary<string, GameObject> nodes = new Dictionary<string, GameObject>();
        readonly HashSet<string> generatorInputs = new HashSet<string>();
        ArtifactPackage open;
        ArtifactBuild build;
        ArtifactRun run;
        ForgeVolume volume;
        bool fitOnLoad;
        Coroutine reveal;
        Pose courseOrigin;

        /// <summary>The host Albie's artifact tools use: the last one enabled.</summary>
        public static ArtifactHost Current { get; private set; }

        /// <summary>
        /// The room generators place courses in. Null means MRUK's current room (<see cref="MrukFloorPlan.Current"/>);
        /// sandboxes and checks set a <see cref="MockFloorPlan"/>.
        /// </summary>
        public IFloorPlan Room;

        /// <summary>The layout the open artifact is using (C-12 6.1), or null for a scene with no generators.</summary>
        public JObject LastLayout { get; private set; }
        /// <summary>True when the layout came from a saved one (a rerun), false when it was just made.</summary>
        public bool LayoutReused { get; private set; }
        /// <summary>What Albie should say about the layout: a shrink, a missing couch, or why nothing fits. Null if nothing to say.</summary>
        public string LayoutMessage { get; private set; }
        /// <summary>What Albie should say about the open artifact's data (sample data, a saved copy, rows cut), or null.</summary>
        public string DataMessage { get; private set; }
        /// <summary>Where <see cref="ComeToUser"/> put the host, for Albie to say ("It's on the table to your right."), or null
        /// when it stands straight ahead in the air as always and there's nothing to point out.</summary>
        public string PlacementNote { get; private set; }
        /// <summary>In the app, where a room course starts from where the person stands ("The course starts behind you, about
        /// 3 m away."), shown at the top of its scoreboard and said by Albie; null in the sandboxes and for other scenes.</summary>
        public string CourseNote { get; private set; }
        /// <summary>Everything the open scene put in the world outside the host: its panel and any room course on the floor.</summary>
        public IReadOnlyList<GameObject> Spawned => spawned;
        /// <summary>The open artifact's data tables (C-12 section 7). Empty when it reads none.</summary>
        public IReadOnlyDictionary<string, ArtifactTable> Tables => build != null ? build.Tables : new Dictionary<string, ArtifactTable>();
        /// <summary>True while the first-open permissions sheet is waiting for a tap.</summary>
        public bool AwaitingApproval { get; private set; }
        /// <summary>Where a room-space scene's origin is (world): the course start.</summary>
        public Pose SceneOrigin => courseOrigin;
        public ArtifactBuild Build => build;

        /// <summary>
        /// The Thing this host scores (C-12 probes). The slalom sandbox sets an <see cref="ArtifactThing"/>; Person 2's
        /// runtime can set any <see cref="IArtifactThing"/>.
        /// </summary>
        public IArtifactThing Thing;
        /// <summary>The open scene's run engine, or null if the scene has no <c>run</c> block.</summary>
        public ArtifactRun Run => run;
        /// <summary>The scoreboard's runs row, so a finished run can refresh it.</summary>
        public ArtifactRunsRow RunsRow;

        /// <summary>
        /// True when <see cref="ArtifactBootstrap"/> added this host to a scene that had none (the app's Main). Nobody
        /// placed it, so each open from Albie's tools brings it in front of whoever is looking.
        /// </summary>
        public bool FollowsUser;

        /// <summary>A scene was built: after an open (and its permissions sheet) or a relayout. <see cref="Run"/>,
        /// <see cref="SceneOrigin"/> and <see cref="OpenManifest"/> are the new scene's.</summary>
        public event System.Action<ArtifactHost> Shown;

        /// <summary>The scene came down: a close, an open of something else (before its permissions sheet), or a relayout
        /// (<see cref="Shown"/> follows). <see cref="Run"/> is gone.</summary>
        public event System.Action<ArtifactHost> Closed;

        /// <summary>
        /// Stands the host in front of the camera at desk height, turned to face the same way: 0.8 m ahead, 0.2 m left
        /// (its panel opens 0.5 m to the right) and 0.5 m below the eyes, as the sandbox's table is. Level, whatever the
        /// head's pitch. With a room (<see cref="Room"/>, else MRUK's current one), the biggest table in front whose top
        /// is at a working height (0.3 to 1 m below the eyes: a desk when standing, the coffee table when sitting) gets
        /// the host on its top, inside its edge; and a spot inside a wall is skipped. If another panel (the Hands panel,
        /// a desk window) already sits where the host or its panel would go, it turns to either side, up to 80 degrees,
        /// then tries the same closer in. With nowhere clear it stands straight ahead. Does nothing with no main camera.
        /// </summary>
        public void ComeToUser()
        {
            PlacementNote = null;
            var cam = Camera.main;
            if (cam == null) return;
            var eye = cam.transform;
            var ahead = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
            if (ahead.sqrMagnitude < 1e-4f) ahead = Vector3.ProjectOnPlane(eye.up, Vector3.up);  // looking straight down
            ahead.Normalize();
            // Once per open, never per frame.
            var others = FindObjectsByType<ForgePanel>(FindObjectsSortMode.None);
            var plan = Room ?? MrukFloorPlan.Current();
            bool table = false;
            Vector3 tableCentre = default, tableSize = default;
            float tableYaw = 0f, top = 0f;
            if (plan != null && plan.TryFind("TABLE", out tableCentre, out tableYaw, out tableSize))
            {
                top = tableCentre.y + tableSize.y * 0.5f;
                // Any table the person sees over and can reach down to: a desk or dining table when standing, those
                // and the coffee table when sitting. Lower than that and the host floats clear above it anyway.
                table = top < eye.position.y - 0.2f && top > eye.position.y - 1.0f;
            }
            var toTable = Quaternion.Euler(0f, -tableYaw, 0f);  // world to the table's frame: its long side along +z
            bool walls = plan != null && plan.HasFloor;
            var room = walls ? plan.FloorBounds : default;
            float panelSide = Mathf.Abs(panelOffset.x);
            bool haveFirst = false, haveSecond = false, secondOnTable = false;
            float secondYaw = 0f;
            Vector3 firstAt = default, secondAt = default, secondOffset = default;
            Quaternion firstFacing = default, secondFacing = default;
            for (int pass = 0; pass < ComeToUserReach.Length; pass++)
                for (int i = 0; i < ComeToUserYaws.Length; i++)
                {
                    var dir = Quaternion.AngleAxis(ComeToUserYaws[i], Vector3.up) * ahead;
                    var right = Vector3.Cross(Vector3.up, dir);
                    var facing = Quaternion.LookRotation(dir, Vector3.up);
                    // The panel opens to the right, the host a little left of centre to make room; if that side is
                    // blocked (a wall, another panel) the panel takes the left and the host moves right.
                    for (int s = 0; s < 2; s++)
                    {
                        float side = s == 0 ? 1f : -1f;
                        var offset = panelOffset;
                        offset.x = side * panelSide;
                        var at = eye.position + dir * ComeToUserReach[pass] - right * (side * 0.2f) + Vector3.down * 0.5f;
                        if (!haveFirst)
                        {
                            firstAt = at;
                            firstFacing = facing;
                            haveFirst = true;
                        }
                        bool onTable = table && OnTable(ref at, tableCentre, toTable, tableSize, top);
                        var panelAt = at + facing * offset;
                        if (walls && !onTable && !InsideRoom(room, at)) continue;
                        if (!ClearOfPanels(at, panelAt, others)) continue;
                        if (walls && !InsideRoom(room, panelAt))
                        {
                            // The host fits but its panel is too near a wall: keep the first such spot in case nothing better turns up.
                            if (!haveSecond)
                            {
                                secondAt = at;
                                secondFacing = facing;
                                secondOffset = offset;
                                secondOnTable = onTable;
                                secondYaw = ComeToUserYaws[i];
                                haveSecond = true;
                            }
                            continue;
                        }
                        Settle(at, facing, offset, onTable, ComeToUserYaws[i]);
                        return;
                    }
                }
            if (haveSecond)
            {
                Settle(secondAt, secondFacing, secondOffset, secondOnTable, secondYaw);
                return;
            }
            var straight = panelOffset;
            straight.x = panelSide;
            Settle(firstAt, firstFacing, straight, false, 0f);
        }

        // Takes the spot ComeToUser picked and writes down where it is for Albie: on a table, or off to a side, or nothing.
        void Settle(Vector3 at, Quaternion facing, Vector3 offset, bool onTable, float yaw)
        {
            panelOffset = offset;
            transform.SetPositionAndRotation(at, facing);
            string where = yaw > 0f ? "to your right" : yaw < 0f ? "to your left" : "in front of you";
            PlacementNote = onTable ? "It's on the table " + where + "." : yaw != 0f ? "It's " + where + "." : null;
        }

        /// <summary>How far, on the floor plan, the host and its panel keep from any other panel's centre.</summary>
        public const float PanelClearance = 0.5f;
        /// <summary>How far inside a table's edge the host stays when it sits on one.</summary>
        public const float TableMargin = 0.12f;
        /// <summary>How far outside a table's edge a spot can be and still be pulled onto the table.</summary>
        public const float TableSnap = 0.4f;
        /// <summary>How far from the room's walls the host keeps.</summary>
        public const float WallMargin = 0.3f;
        // Straight ahead first, then a little to either side, then further; the whole fan again closer in.
        static readonly float[] ComeToUserYaws = { 0f, 40f, -40f, 80f, -80f };
        static readonly float[] ComeToUserReach = { 0.8f, 0.55f };

        // Moves a spot onto the table's top when it's on the table, or within TableSnap of its edge; else leaves it alone.
        static bool OnTable(ref Vector3 at, Vector3 centre, Quaternion toTable, Vector3 size, float top)
        {
            var local = toTable * (at - centre);
            float halfShort = size.z * 0.5f - TableMargin;  // across, local x
            float halfLong = size.x * 0.5f - TableMargin;   // along, local z
            if (halfShort <= 0f || halfLong <= 0f) return false;
            if (Mathf.Abs(local.x) - halfShort > TableSnap || Mathf.Abs(local.z) - halfLong > TableSnap) return false;
            local.x = Mathf.Clamp(local.x, -halfShort, halfShort);
            local.z = Mathf.Clamp(local.z, -halfLong, halfLong);
            local.y = 0f;
            var p = centre + Quaternion.Inverse(toTable) * local;
            at = new Vector3(p.x, top, p.z);
            return true;
        }

        static bool InsideRoom(Bounds room, Vector3 at) =>
            at.x > room.min.x + WallMargin && at.x < room.max.x - WallMargin && at.z > room.min.z + WallMargin && at.z < room.max.z - WallMargin;

        bool ClearOfPanels(Vector3 at, Vector3 panelAt, ForgePanel[] others)
        {
            foreach (var other in others)
            {
                if (other == null || !other.gameObject.activeInHierarchy || other.transform.IsChildOf(transform)) continue;
                var centre = other.transform.position;
                if (FlatDistance(centre, panelAt) < PanelClearance || FlatDistance(centre, at) < PanelClearance) return false;
            }
            return true;
        }

        static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        void OnEnable() => Current = this;

        void OnDisable()
        {
            if (Current == this) Current = null;
        }

        /// <summary>The artifact showing right now, or null.</summary>
        public string OpenId => build != null ? open.Id : null;
        public string OpenTitle => open != null ? open.Title : "";
        /// <summary>The manifest of the artifact being opened (also while its permissions sheet waits), or null.</summary>
        public JObject OpenManifest => open?.Manifest;

        /// <summary>The last reason an artifact didn't open, for tools and the agent. Null if it did.</summary>
        public string LastProblem { get; private set; }

        /// <summary>What was wrong, as data, so the agent can fix it (C-12 section 9).</summary>
        public IReadOnlyList<ArtifactError> LastErrors { get; private set; } = new List<ArtifactError>();

        public ArtifactInputs Inputs => build?.Inputs;

        /// <summary>The open scene's content root, or null. A room scene's is not under this host (it stands on the floor).</summary>
        public Transform ContentRoot => build?.Root;

        /// <summary>True while the open scene is still growing in (<see cref="ForgeTheme.UnfoldTime"/> after it was shown).</summary>
        public bool Revealing => reveal != null;

        /// <summary>The open scene's rules, for nodes that act on them.</summary>
        public JToken Rules => open?.Scene["rules"];

        /// <summary>The open artifact's live numbers.</summary>
        public ArtifactMetrics Metrics => build?.Metrics;

        public void Open(string id)
        {
            Close();
            var package = ArtifactPackage.Load(id, out string error);
            if (package == null)
            {
                Refuse(id, error);
                return;
            }
            open = package;
            LastErrors = ArtifactValidator.Validate(package.Manifest, package.Scene);
            if (LastErrors.Count > 0)
            {
                foreach (var e in LastErrors) Debug.LogWarning($"[Artifacts] {id}: {e}");
                Refuse(package.Title, $"This artifact has {LastErrors.Count} problem{(LastErrors.Count == 1 ? "" : "s")}, so it didn't open.\n\n{LastErrors[0].message}");
                return;
            }
            if (ArtifactApprovals.NeedsApproval(package.Manifest)) AskPermission();
            else Show(null, false);
        }

        public void Close()
        {
            Teardown();
            open = null;
            LastProblem = null;
            LastErrors = new List<ArtifactError>();
            LastLayout = null;
            LayoutMessage = null;
            DataMessage = null;
            CourseNote = null;
            LayoutReused = false;
            AwaitingApproval = false;
        }

        /// <summary>Makes a new layout for this room and rebuilds (C-12 <c>artifact_relayout</c>). The old layout and its runs are kept.</summary>
        public bool Relayout()
        {
            if (build == null) return false;
            var inputs = build.Inputs;
            Teardown();
            Show(inputs, true);
            return build != null;
        }

        void Teardown()
        {
            if (build != null) Closed?.Invoke(this);
            relayoutAt = -1f;
            if (build != null) build.Inputs.Changed -= OnInputChanged;
            if (reveal != null) StopCoroutine(reveal);
            reveal = null;
            foreach (var go in spawned)
            {
                if (go == null) continue;
                // Destroy waits for the end of the frame. Detached, the old artifact is gone for anyone looking now.
                go.SetActive(false);
                go.transform.SetParent(null, false);
                Destroy(go);
            }
            spawned.Clear();
            volume = null;
            fitOnLoad = false;
            nodes.Clear();
            generatorInputs.Clear();
            if (run != null)
            {
                run.Abort();
                FlushRun();
                run.ZoneChanged -= OnZone;
                run.StateChanged -= OnRunState;
                run = null;
            }
            RunsRow = null;
            build = null;
        }

        /// <summary>A ForgePanel owned by the open artifact, with mouse support and no placeholder.</summary>
        public ForgePanel NewPanel(string title)
        {
            var panel = Instantiate(panelPrefab, transform);
            panel.name = "Panel " + title;
            panel.Title = title;
            panel.SetBadge("ART", accent);
            if (panel.GetComponent<GraphicRaycaster>() == null) panel.gameObject.AddComponent<GraphicRaycaster>();  // mouse (DesktopRig)
            var placeholder = panel.Content.Find("Placeholder");
            if (placeholder != null) placeholder.gameObject.SetActive(false);
            PlacePanel(panel.transform, panelOffset);
            spawned.Add(panel.gameObject);
            return panel;
        }

        /// <summary>A tall panel rises so its bottom edge stays above the host's base, clear of whatever that stands on.</summary>
        public void KeepAboveBase(ForgePanel panel)
        {
            var at = panel.transform.localPosition;
            at.y = Mathf.Max(at.y, panel.SizeMeters.y * 0.5f + 0.04f);
            PlacePanel(panel.transform, at);
        }

        /// <summary>A panel button was pressed, by its id, before its rules run. The brain truck listens for the slalom's
        /// "drive_lap" and "albie_drives", so the course works with no voice.</summary>
        public event System.Action<string> Fired;

        /// <summary>A button was pressed: run its <c>on_input</c> rules.</summary>
        public void Fire(string input)
        {
            Fired?.Invoke(input);
            if (!(open?.Scene["rules"] is JArray rules)) return;
            foreach (var rule in rules)
                if (rule.Value<string>("type") == "on_input" && rule.Value<string>("input") == input && rule["do"] is JArray actions)
                    foreach (var action in actions) Do(action);
        }

        void Do(JToken action)
        {
            string sim = action.Value<string>("sim");
            if (sim != null)
            {
                Control(sim, action.Value<string>("action"));
                return;
            }
            string verb = action.Value<string>("run");
            if (verb != null)
            {
                if (verb == "reset")
                {
                    PlaceThingAtStart();
                    run?.Abort();
                    FlushRun();
                    ArmRun();
                }
                else if (verb == "arm") ArmRun();
                return;
            }
            string timeline = action.Value<string>("timeline");
            if (timeline != null)
            {
                Clock(timeline, action.Value<string>("action"));
                return;
            }
            string target = action.Value<string>("set");
            if (target == null) return;
            var parts = target.Split('.');
            if (parts.Length == 2 && parts[1] == "visible" && nodes.TryGetValue(parts[0], out var node))
                node.SetActive(action.Value<bool>("value"));
            else Debug.LogWarning($"[Artifacts] can't set {target} yet: only <node>.visible is built");
        }

        void Control(string id, string verb)
        {
            if (!nodes.TryGetValue(id, out var node) || !node.TryGetComponent<ArtifactSim>(out var sim)) return;
            if (verb == "play") sim.Play();
            else if (verb == "pause") sim.Pause();
            else if (verb == "restart") sim.Restart();
        }

        void Clock(string id, string verb)
        {
            if (!nodes.TryGetValue(id, out var node) || !node.TryGetComponent<ArtifactTimeline>(out var clock)) return;
            if (verb == "play") clock.Play();
            else if (verb == "pause") clock.Pause();
            else if (verb == "restart") clock.Restart();
        }

        /// <summary>A <c>gltf</c> node finished loading. A volume fitted to its contents as they stood fits again.</summary>
        public void ModelLoaded()
        {
            if (volume != null && fitOnLoad) volume.Fit();
        }

        void AskPermission()
        {
            var manifest = open.Manifest;
            AwaitingApproval = true;
            var panel = NewPanel(open.Title);
            panel.Subtitle = "First time opening";
            float y = ArtifactWidgets.Note(panel.Content, 0f, "This artifact would like to:", true) + 10f;
            foreach (var line in ArtifactApprovals.Lines(manifest)) y += ArtifactWidgets.Note(panel.Content, y, "•  " + line) + 6f;
            y += 12f;
            var allow = ArtifactWidgets.PillControl(panel.Content, y, 0f, "Allow", () =>
            {
                ArtifactApprovals.Approve(manifest);
                string id = open.Id;
                Close();
                Open(id);
            });
            allow.On = true;   // the one bright control on the sheet
            float x = allow.Rect.sizeDelta.x + 10f;
            ArtifactWidgets.Pill(panel.Content, y, x, "Not now", Close);
            panel.SizeMeters = new Vector2(ArtifactWidgets.PanelWidth, ArtifactWidgets.PanelHeight(y + 34f)) * ForgePanel.MetersPerUnit;
        }

        // inputs: keep these values (a rebuild after a generator input changed). forceNew: ignore the saved layout.
        void Show(ArtifactInputs keep, bool forceNew)
        {
            AwaitingApproval = false;
            var missing = ArtifactNodes.Missing(open.Scene);
            if (missing.Count > 0)
            {
                Refuse(open.Title, $"This artifact is valid, but this version can't show it yet. It needs: {string.Join(", ", missing)}.");
                return;
            }

            var tables = ArtifactData.Resolve(open, out string dataProblem);
            if (tables == null)
            {
                LastErrors = new List<ArtifactError> { new ArtifactError("no_data", "data", dataProblem) };
                Refuse(open.Title, dataProblem);
                return;
            }
            var assets = ArtifactAssets.Resolve(open, out var assetError);
            if (assets == null)
            {
                LastErrors = new List<ArtifactError> { assetError };
                Refuse(open.Title, $"One of its files can't be used, so it didn't open.\n\n{assetError}");
                return;
            }
            if (open.Scene["nodes"] is JArray tableNodes)
                foreach (JObject node in tableNodes)
                {
                    string problem = node.Value<string>("kind") == "array" ? ArtifactArray.Problem(node, tables) : null;
                    if (problem == null) continue;
                    LastErrors = new List<ArtifactError> { new ArtifactError("unknown_ref", "nodes." + node.Value<string>("id"), problem) };
                    Refuse(open.Title, problem);
                    return;
                }

            var scenario = (open.Manifest["scenarios"] as JArray)?[0] as JObject;
            var inputs = keep ?? new ArtifactInputs(open.Manifest, scenario?["params"] as JObject);
            bool room = open.Scene.Value<string>("space") == "room";
            var slots = new Dictionary<string, List<Pose>>();
            courseOrigin = new Pose(transform.position, transform.rotation);
            if (open.Scene["generators"] is JArray generators && generators.Count > 0)
            {
                if (generators.Count > 1)
                {
                    Refuse(open.Title, "This version places one generator per scene.");
                    return;
                }
                var placement = ArtifactGenerators.Place(open.Manifest, (JObject)generators[0], inputs, scenario, Room ?? MrukFloorPlan.Current(), forceNew);
                LayoutMessage = placement.Message;
                if (!placement.Ok)
                {
                    LastErrors = new List<ArtifactError> { new ArtifactError(placement.Code, "generators[0]", placement.Message) };
                    Emit("artifact.layout", new JObject { ["artifact_id"] = open.Id, ["layout_id"] = null, ["placed"] = null, ["shrunk"] = false, ["code"] = placement.Code, ["message"] = placement.Message });
                    Refuse(open.Title, placement.Message);
                    return;
                }
                LastLayout = placement.Layout;
                LayoutReused = placement.Reused;
                courseOrigin = placement.Origin;
                slots = placement.Slots;
                if (room && FollowsUser) CourseNote = WhereIsCourse(courseOrigin.position);
                CollectInputs(generators[0]["params"], generatorInputs);
                Emit("artifact.layout", new JObject
                {
                    ["artifact_id"] = open.Id, ["layout_id"] = LastLayout["layout_id"], ["placed"] = LastLayout["fit"]["placed"],
                    ["shrunk"] = LastLayout["fit"]["shrunk"], ["reused"] = LayoutReused, ["message"] = LayoutMessage,
                });
            }

            var root = new GameObject("Content").transform;
            build = new ArtifactBuild { Host = this, Inputs = inputs, Root = root, Nodes = nodes, Slots = slots, Tables = tables, Assets = assets };

            var list = open.Scene["nodes"] as JArray ?? new JArray();
            foreach (JObject node in list) nodes[node.Value<string>("id")] = ArtifactNodes.Build(node, build);
            foreach (JObject node in list) Arrange(node, root);
            build.Finish();
            LabelSample(root);
            var said = new List<string>();
            string aboutData = ArtifactData.Say(tables);
            if (aboutData != null) said.Add(aboutData);
            said.AddRange(build.Notes);
            DataMessage = said.Count > 0 ? string.Join(" ", said) : null;

            float scale = 1f;
            if (open.Scene.Value<string>("space") == "volume") scale = ShowVolume(root);
            else if (room) PlaceInRoom(root);
            else Hold(root);

            inputs.Changed += OnInputChanged;
            BindRun();
            reveal = StartCoroutine(Reveal(root, scale));
            Shown?.Invoke(this);
        }

        // C-12 section 7: sample rows always carry a "Sample data" label, floating just above everything else in the scene.
        void LabelSample(Transform root)
        {
            bool sample = false;
            foreach (var t in build.Tables.Values) sample |= t.Sample;
            if (!sample) return;
            var label = ArtifactNodes.Build(new JObject
            {
                ["id"] = "sample_label", ["kind"] = "text", ["text"] = "Sample data", ["height"] = 0.04, ["color"] = "#FFD24C", ["billboard"] = true,
            }, build);
            var bounds = new Bounds(root.position, Vector3.zero);
            foreach (var r in root.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(r.bounds);
            label.transform.SetParent(root, false);
            label.transform.localPosition = root.InverseTransformPoint(new Vector3(bounds.center.x, bounds.max.y + 0.06f, bounds.center.z));
            nodes["_sample_label"] = label;  // not a legal node id, so it can't clash with the scene's own
        }

        void BindRun()
        {
            if (!(open.Scene["run"] is JObject)) return;
            if (Thing == null) Thing = FindFirstObjectByType<ArtifactThing>();
            run = new ArtifactRun();
            run.Bind(open.Manifest, open.Scene, LastLayout, build.Inputs, Thing, build.Metrics, build);
            run.ZoneChanged += OnZone;
            run.StateChanged += OnRunState;
        }

        /// <summary>Arms a run on the open scene (C-12 <c>artifact_run</c>). Places a sandbox Thing at the start.</summary>
        public string ArmRun()
        {
            if (run == null) return "no_run: this scene isn't scored";
            if (Thing == null) Thing = FindFirstObjectByType<ArtifactThing>();
            run.SetThing(Thing);
            PlaceThingAtStart();
            return run.Arm();
        }

        void PlaceThingAtStart()
        {
            if (!(Thing is ArtifactThing body)) return;
            body.Place(courseOrigin.position + Vector3.up * 0.06f, courseOrigin.rotation);
        }

        void OnZone(string zone, bool enter)
        {
            if (!(open?.Scene["rules"] is JArray rules)) return;
            string ev = enter ? "enter" : "exit";
            foreach (var rule in rules)
                if (rule.Value<string>("type") == "on_zone" && rule.Value<string>("zone") == zone
                    && rule.Value<string>("event") == ev && rule["do"] is JArray actions)
                    foreach (var action in actions) Do(action);
        }

        void OnRunState()
        {
            if (run == null) return;
            Emit("artifact.run", run.Event());
        }

        void FlushRun()
        {
            if (run == null) return;
            var record = run.TakeRecord();
            if (record == null) return;
            if (open?.Manifest["links"] is JObject links)
            {
                record["project_id"] = links["project"] ?? JValue.CreateNull();
                record["project_version"] = links["project_version"] ?? JValue.CreateNull();
            }
            if (NotebookStore.IsOpen)
            {
                record["project_id"] = NotebookStore.ProjectId;
                record["project_version"] = NotebookStore.CurrentVersion();
            }
            ArtifactRuns.Save(record);
            NotebookStore.AttachRun((string)record["run_id"], (string)record["outcome"]);
            RunsRow?.Refresh();
        }

        // A room scene stands on the real floor at the course origin, at true scale. It isn't parented to the host, so
        // moving the host (or the desk it sits on) never drags a course across the room.
        void PlaceInRoom(Transform root)
        {
            root.SetParent(null, false);
            root.SetPositionAndRotation(courseOrigin.position, courseOrigin.rotation);
            spawned.Add(root.gameObject);
        }

        static void CollectInputs(JToken token, HashSet<string> into)
        {
            if (token == null) return;
            if (token.Type == JTokenType.String && ((string)token).StartsWith("$")) into.Add(((string)token).Substring(1));
            foreach (var child in token.Children()) CollectInputs(child, into);
        }

        static void Emit(string type, JObject payload)
        {
            if (!Application.isPlaying) return;
            try { ForgeNet.Emit(type, payload); }
            catch (System.Exception e) { Debug.LogException(e); }
        }

        void Hold(Transform root)
        {
            root.SetParent(transform, false);
            spawned.Add(root.gameObject);
        }

        // A scene with a view keeps that sphere in the box however its contents move. Without one, it's fitted once as it
        // stands. Returns the scale the root should end at.
        float ShowVolume(Transform root)
        {
            volume = Instantiate(volumePrefab, transform);
            volume.name = "Volume " + open.Title;
            volume.Title = open.Title;
            volume.SetAccent(accent);
            volume.SetSize(volumeSize);
            spawned.Add(volume.gameObject);
            float radius = open.Scene["view"]?.Value<float?>("radius") ?? 0f;
            fitOnLoad = radius <= 0f;
            if (fitOnLoad)
            {
                volume.SetContent(root.gameObject);
                return 1f;
            }
            volume.SetContent(root.gameObject, false);
            root.localPosition = new Vector3(0f, volumeSize.y * 0.5f, 0f);
            return Mathf.Min(volumeSize.x, volumeSize.y, volumeSize.z) / (2f * radius);
        }

        // Panels live beside the scene, not in it: they keep their real size however the scene is scaled to fit.
        void Arrange(JObject n, Transform root)
        {
            var go = nodes[n.Value<string>("id")];
            bool panel = n.Value<string>("kind") == "panel";
            if (!panel)
            {
                string parent = n.Value<string>("parent");
                go.transform.SetParent(parent != null ? nodes[parent].transform : root, false);
                var pos = n["pos"];
                var rot = n["rot"];
                var scale = n["scale"];
                // On a generator slot, pos and rot are offsets from the slot's pose (a zone lifts itself off the floor).
                string at = n.Value<string>("at");
                var slot = at != null && build.Slots.TryGetValue(at, out var poses) && poses.Count > 0 ? poses[0] : new Pose(Vector3.zero, Quaternion.identity);
                build.Apply(() =>
                {
                    go.transform.localPosition = slot.position + slot.rotation * build.Inputs.Vector(pos, Vector3.zero);
                    go.transform.localRotation = slot.rotation * Quaternion.Euler(build.Inputs.Vector(rot, Vector3.zero));
                    go.transform.localScale = build.Inputs.Vector(scale, Vector3.one);
                }, ArtifactInputs.IsBound(pos) || ArtifactInputs.IsBound(rot) || ArtifactInputs.IsBound(scale));
            }
            else if (n["pos"] != null) PlacePanel(go.transform, build.Inputs.Vector(n["pos"], panelOffset));
            else if (open.Scene.Value<string>("space") == "room")
            {
                // In the app the course goes where the floor is free, often beside or behind the person, so its
                // scoreboard comes to them like any artifact's panel, and says where the course is (CourseNote).
                if (FollowsUser && go.TryGetComponent<ForgePanel>(out var board))
                {
                    PlacePanel(go.transform, panelOffset);
                    KeepAboveBase(board);
                }
                else PlaceBesideCourse(go.transform);
            }
            if (n.Value<bool?>("visible") == false) go.SetActive(false);
        }

        // Facing whoever is looking, from wherever it was put. A canvas faces -Z, so it looks along eye to panel.
        void PlacePanel(Transform panel, Vector3 offset)
        {
            panel.localPosition = offset;
            var cam = Camera.main;
            if (cam != null) panel.rotation = Quaternion.LookRotation(panel.position - cam.transform.position);
        }

        // "The course starts behind you, about 3 m away." From the eyes, flat on the floor: ahead within 35 degrees,
        // behind past 110, else to a side. Null with no camera.
        static string WhereIsCourse(Vector3 start)
        {
            var cam = Camera.main;
            if (cam == null) return null;
            var eye = cam.transform;
            var ahead = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
            var to = Vector3.ProjectOnPlane(start - eye.position, Vector3.up);
            if (ahead.sqrMagnitude < 1e-4f || to.sqrMagnitude < 1e-4f) return null;
            float angle = Vector3.SignedAngle(ahead, to, Vector3.up);  // + is to the right
            float a = Mathf.Abs(angle);
            string where = a <= 35f ? "in front of you" : a > 110f ? "behind you" : angle > 0f ? "to your right" : "to your left";
            int metres = Mathf.Max(1, Mathf.RoundToInt(to.magnitude));
            return $"The course starts {where}, about {metres} m away. The truck waits in the green box.";
        }

        // A course's scoreboard stands beside the start at chest height, on whichever side is free floor (never in a
        // wall or a cupboard), turned to whoever is looking. With neither side free, it floats just behind the start.
        void PlaceBesideCourse(Transform panel)
        {
            var room = Room ?? MrukFloorPlan.Current();
            var spot = new Vector3(0f, 1.1f, -0.7f);
            foreach (var side in new[] { new Vector3(-1.1f, 1.1f, 0.4f), new Vector3(1.1f, 1.1f, 0.4f) })
                if (room != null && room.HasFloor && room.IsFree(courseOrigin.position + courseOrigin.rotation * side, 0.05f))
                {
                    spot = side;
                    break;
                }
            panel.position = courseOrigin.position + courseOrigin.rotation * spot;
            var cam = Camera.main;
            if (cam != null) panel.rotation = Quaternion.LookRotation(panel.position - cam.transform.position);
            else panel.rotation = courseOrigin.rotation;
        }

        void Refuse(string title, string message)
        {
            Debug.Log($"[Artifacts] {title}: {message}");
            string problem = message;
            var panel = NewPanel(title);
            float height = ArtifactWidgets.Note(panel.Content, 0f, message, true) + 16f;
            ArtifactWidgets.Pill(panel.Content, height, 0f, "Close", Close);
            panel.SizeMeters = new Vector2(ArtifactWidgets.PanelWidth, ArtifactWidgets.PanelHeight(height + 34f)) * ForgePanel.MetersPerUnit;
            LastProblem = problem;
        }

        // A generator input (gates, spacing) changes the course itself, so it's laid out again; anything else follows live.
        void OnInputChanged(string input)
        {
            if (!generatorInputs.Contains(input))
            {
                build.Refresh();
                return;
            }
            relayoutAt = Time.unscaledTime + RelayoutDelay;  // wait until a slider drag settles
        }

        const float RelayoutDelay = 0.4f;
        float relayoutAt = -1f;

        void Update()
        {
            FlushRun();
            if (relayoutAt < 0f || Time.unscaledTime < relayoutAt) return;
            relayoutAt = -1f;
            if (build == null) return;
            var inputs = build.Inputs;
            Teardown();
            Show(inputs, false);
        }

        void FixedUpdate() => run?.Tick(Time.fixedDeltaTime);

        // Grows in rather than popping, so it feels placed. Scales the root's child space only, never the Fit.
        IEnumerator Reveal(Transform root, float scale)
        {
            for (float t = 0f; t < 1f; t += Time.deltaTime / ForgeTheme.UnfoldTime)
            {
                root.localScale = Vector3.one * (scale * Mathf.SmoothStep(ForgeTheme.UnfoldScale, 1f, t));
                yield return null;
            }
            root.localScale = Vector3.one * scale;
            reveal = null;
        }
    }
}
