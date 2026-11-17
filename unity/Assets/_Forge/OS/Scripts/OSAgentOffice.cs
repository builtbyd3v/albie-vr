using System;
using System.Collections.Generic;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// An office visualization of the existing crew, not a second agent runtime. Mesh-only portraits share the
    /// crew's identity and pose; no controller, socket, tool, voice or collider is copied. Closing the office never
    /// cancels a task, moves a live character or changes the roster. Work states come from the hub and agent bridge.
    /// </summary>
    public sealed class OSAgentOffice : MonoBehaviour
    {
        public const string WindowId = "agent-office";
        const int PreviewLayer = 31;
        readonly List<Desk> desks = new List<Desk>();
        readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        GameObject stage;
        Camera officeCamera;
        RenderTexture target;
        TMP_Text summary, detail;
        RectTransform roster;
        IDisposable updates;
        JObject build;
        string selected = "", rosterKey = "";
        bool pending;
        float nextRefresh, nextPose;
        int stageResourceCount;

        sealed class Portrait
        {
            public Transform root;
            public bool fallback;
            public readonly List<Part> parts = new List<Part>();
        }
        sealed class Part
        {
            public Renderer source;
            public Transform target;
            public Mesh mesh;
            public bool skinned;
        }
        sealed class Desk
        {
            public string id, name, state = "idle", task = "";
            public AlbieController source;
            public AlbieAgentBridge bridge;
            public Transform root;
            public Renderer monitor;
            public TMP_Text label;
            public Color color;
            public Portrait portrait;
            public Vector3 seat;
        }

        public int StationCount => desks.Count;
        public string SelectedAgent => selected;
        public string WorkflowState => (string)build?["state"] ?? "none";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Register() => OSShell.RegisterSystem(WindowId, () => Open());

        public static OSWindow Open()
        {
            if (OSShell.Current == null) return null;
            return OSShell.Current.Windows.Open(WindowId, "Albie agent office", new Vector2(1.05f, 0.94f), panel =>
            {
                panel.gameObject.AddComponent<OSAgentOffice>().Init(panel);
            }, "OFFICE");
        }

        void Init(ForgePanel panel)
        {
            var column = OSUi.Column(OSUi.Body(panel), 12f);
            var actions = OSUi.Row(column, OSUi.ButtonHeight, 19f);
            actions.GetComponent<LayoutElement>().flexibleHeight = 0;
            OSUi.Button(actions, "Project workflow", () => OSWorkflowWindow.Open(), OSButtonStyle.Primary);
            OSUi.Button(actions, "Manage crew", () => OSAgentsWindow.Open());
            summary = OSUi.Text(column, "Connecting to the existing crew…", OSUi.BodyText);
            summary.richText = false;
            var viewport = new GameObject("Live office viewport", typeof(RectTransform), typeof(RawImage), typeof(LayoutElement));
            viewport.transform.SetParent(column, false);
            var layout = viewport.GetComponent<LayoutElement>();
            layout.preferredWidth = layout.minWidth = 1400f;
            layout.preferredHeight = 800f; layout.flexibleHeight = 0; layout.minHeight = 400f;
            viewport.GetComponent<RawImage>().raycastTarget = false;
            roster = OSUi.Column(column, 8f);
            detail = OSUi.Text(column, "Choose a crew member below the office to inspect their task.", OSUi.SmallText, true);
            detail.richText = false;
            BuildStage();
            stageResourceCount = owned.Count;
            viewport.GetComponent<RawImage>().texture = target;
            updates = ForgeNet.On("build.updated", value => { build = (JObject)value.DeepClone(); }, true);
            SyncCrew();
            RefreshBuild();
        }

        void BuildStage()
        {
            stage = new GameObject("Albie office preview (visuals only)");
            // Isolated from the room: no physical colliders, lighting, navmesh, or scene registry changes.
            stage.transform.position = new Vector3(10000f, 10000f, 10000f);
            Box(stage.transform, "Oak office floor", new Vector3(0, -0.035f, 0), new Vector3(2.5f, 0.07f, 2.05f), new Color(.14f, .19f, .23f));
            for (int i = 0; i < 11; i++)
                Box(stage.transform, "Floor seam", new Vector3(-1.18f + i * .235f, .002f, 0), new Vector3(.006f, .003f, 2f), new Color(.21f, .27f, .30f));
            Box(stage.transform, "Back wall", new Vector3(0, .20f, .99f), new Vector3(2.5f, .44f, .035f), new Color(.24f, .31f, .35f));
            Box(stage.transform, "Left wall", new Vector3(-1.23f, .12f, 0), new Vector3(.035f, .28f, 2.02f), new Color(.20f, .27f, .31f));
            Box(stage.transform, "Team board", new Vector3(.22f, .26f, .96f), new Vector3(1.15f, .24f, .015f), new Color(.08f, .12f, .16f));
            var title = WorldText(stage.transform, "ALBIE / CREW OFFICE", new Vector3(.22f, .27f, .944f), .048f);
            title.transform.localRotation = Quaternion.identity;
            for (int i = 0; i < 3; i++)
            {
                Box(stage.transform, "Window", new Vector3(-.91f + i * .20f, .25f, .963f), new Vector3(.15f, .19f, .015f), new Color(.41f, .68f, .76f));
                Box(stage.transform, "Window mullion", new Vector3(-.91f + i * .20f, .25f, .95f), new Vector3(.009f, .19f, .008f), new Color(.2f, .31f, .36f));
            }
            Plant(new Vector3(1.08f, 0, .79f)); Plant(new Vector3(-1.07f, 0, -.82f));
            target = new RenderTexture(1400, 800, 24) { name = "Albie office viewport", antiAliasing = 2 };
            target.Create();
            var cameraObject = new GameObject("Office camera", typeof(Camera));
            cameraObject.transform.SetParent(stage.transform, false);
            officeCamera = cameraObject.GetComponent<Camera>();
            officeCamera.transform.localPosition = new Vector3(2.6f, 3.4f, -4.2f);
            officeCamera.transform.LookAt(stage.transform.position + new Vector3(0, .05f, 0));
            officeCamera.orthographic = true; officeCamera.orthographicSize = 1.26f;
            officeCamera.nearClipPlane = .05f; officeCamera.farClipPlane = 12f;
            officeCamera.cullingMask = 1 << PreviewLayer;
            officeCamera.clearFlags = CameraClearFlags.SolidColor;
            officeCamera.backgroundColor = new Color(.065f, .09f, .12f);
            officeCamera.targetTexture = target;
            officeCamera.allowHDR = false; officeCamera.allowMSAA = true;
        }

        void Plant(Vector3 p)
        {
            Box(stage.transform, "Plant pot", p + Vector3.up * .055f, new Vector3(.11f, .11f, .11f), new Color(.73f, .46f, .30f));
            for (int i = 0; i < 3; i++)
            {
                var leaf = Box(stage.transform, "Plant leaves", p + new Vector3((i - 1) * .025f, .15f, 0), new Vector3(.045f, .13f, .07f), new Color(.27f, .57f, .39f));
                leaf.transform.localRotation = Quaternion.Euler(0, i * 50, (i - 1) * 25);
            }
        }

        void SyncCrew()
        {
            var crew = AlbieCrew.Instance;
            string key = crew != null && crew.Lead != null ? crew.Lead.GetEntityId().ToString() : "";
            if (crew != null)
                foreach (var m in crew.Members) if (m != null && !m.Leaving) key += ":" + m.GetEntityId();
            if (key == rosterKey) return;
            rosterKey = key;
            foreach (var d in desks) if (d.root != null) Destroy(d.root.gameObject);
            desks.Clear();
            for (int i = owned.Count - 1; i >= stageResourceCount; i--)
            {
                if (owned[i] != null) Destroy(owned[i]);
                owned.RemoveAt(i);
            }
            OSUi.Clear(roster);
            if (crew != null && crew.Lead != null)
                AddDesk("albie", "Albie", new Color(.45f, .85f, .73f), crew.Lead);
            foreach (var type in AlbieAgentTypes.All)
            {
                var member = crew != null ? crew.Find(type) : null;
                if (member != null && !member.Leaving && member.Controller != null)
                    AddDesk(type.Id, type.Name, type.Color, member.Controller);
            }
            RectTransform row = null;
            for (int i = 0; i < desks.Count; i++)
            {
                if (i % 5 == 0)
                {
                    row = OSUi.Row(roster, OSUi.ButtonHeight, 19f);
                    row.GetComponent<LayoutElement>().flexibleHeight = 0;
                }
                string id = desks[i].id;
                OSUi.Button(row, desks[i].name, () => selected = id);
            }
            if (desks.Count == 0) OSUi.Text(roster, "Open Main and use Manage crew to hatch your existing agent types.", OSUi.SmallText, true);
        }

        void AddDesk(string id, string displayName, Color color, AlbieController source)
        {
            int slot = desks.Count;
            var root = new GameObject("Office station " + id).transform;
            root.SetParent(stage.transform, false);
            // Lead has a station at the front. Up to nine existing crew types fill three rows behind it.
            root.localPosition = slot == 0 ? new Vector3(.70f, 0, -.77f)
                : new Vector3(-.72f + (slot - 1) % 3 * .72f, 0, -.46f + (slot - 1) / 3 * .43f);
            Box(root, "Role rug", new Vector3(0, .006f, -.03f), new Vector3(.55f, .009f, .36f), Color.Lerp(color, new Color(.15f, .19f, .23f), .72f));
            Box(root, "Desk top", new Vector3(0, .10f, .09f), new Vector3(.34f, .025f, .15f), new Color(.66f, .48f, .32f));
            foreach (float x in new[] { -.13f, .13f })
                Box(root, "Desk leg", new Vector3(x, .047f, .09f), new Vector3(.018f, .09f, .095f), new Color(.23f, .28f, .32f));
            Box(root, "Monitor stand", new Vector3(0, .14f, .12f), new Vector3(.018f, .07f, .018f), new Color(.12f, .15f, .20f));
            Box(root, "Monitor frame", new Vector3(0, .19f, .12f), new Vector3(.18f, .11f, .024f), new Color(.07f, .1f, .14f));
            var monitor = Box(root, "Live work indicator", new Vector3(0, .19f, .105f), new Vector3(.15f, .081f, .005f), color * .4f);
            Box(root, "Keyboard", new Vector3(0, .116f, .045f), new Vector3(.12f, .007f, .035f), new Color(.25f, .31f, .37f));
            var label = WorldText(root, displayName + " / idle", new Vector3(0, .024f, -.23f), .037f);
            var desk = new Desk { id = id, name = displayName, root = root, color = color, source = source,
                bridge = source.GetComponent<AlbieAgentBridge>(), monitor = monitor.GetComponent<Renderer>(), label = label,
                seat = new Vector3(0, .015f, -.075f) };
            desk.portrait = MakePortrait(root, source);
            if (desk.portrait.fallback) FallbackAvatar(desk.portrait.root, color, id == "albie");
            desks.Add(desk);
        }

        Portrait MakePortrait(Transform parent, AlbieController source)
        {
            var portrait = new Portrait { root = new GameObject("Crew visual (no agent components)").transform };
            portrait.root.SetParent(parent, false);
            // Copy only mesh data. Never Instantiate an agent GameObject: Awake could connect sockets or register tools.
            foreach (var renderer in source.GetComponentsInChildren<Renderer>(false))
            {
                var skin = renderer as SkinnedMeshRenderer;
                var filter = renderer.GetComponent<MeshFilter>();
                if (skin == null && (filter == null || renderer.GetComponent<TMP_Text>() != null)) continue;
                if (skin != null && skin.sharedMesh == null || skin == null && filter.sharedMesh == null) continue;
                var go = new GameObject(renderer.name, typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(portrait.root, false); go.layer = PreviewLayer;
                Mesh mesh = skin != null ? new Mesh { name = "Office pose " + source.name } : filter.sharedMesh;
                if (skin != null) owned.Add(mesh);
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                var materials = renderer.sharedMaterials;
                var copies = new Material[materials.Length];
                for (int i = 0; i < materials.Length; i++)
                {
                    var original = materials[i];
                    Color color = original != null && original.HasProperty("_BaseColor") ? original.GetColor("_BaseColor")
                        : original != null && original.HasProperty("_Color") ? original.color : Color.white;
                    copies[i] = Material(color);
                    if (original != null)
                    {
                        Texture texture = original.HasProperty("_BaseMap") ? original.GetTexture("_BaseMap") : original.mainTexture;
                        copies[i].mainTexture = texture;
                    }
                }
                var mr = go.GetComponent<MeshRenderer>(); mr.sharedMaterials = copies;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                // Hats use property blocks for the crew color.
                var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block); mr.SetPropertyBlock(block);
                portrait.parts.Add(new Part { source = renderer, target = go.transform, mesh = mesh, skinned = skin != null });
            }
            portrait.root.localScale = source.transform.lossyScale;
            // This checkout may lack the separately supplied Albie body asset. Show an honest visual fallback,
            // retaining the existing identity/task, rather than an invisible character or a new fake agent.
            portrait.fallback = source.GetComponentInChildren<Animator>(true) == null || portrait.parts.Count == 0;
            return portrait;
        }

        void FallbackAvatar(Transform root, Color role, bool lead)
        {
            Color green = new Color(.29f, .67f, .46f), light = new Color(.53f, .82f, .49f);
            Box(root, "Temporary croc body", new Vector3(0, .055f, .015f), new Vector3(.085f, .065f, .14f), green);
            Box(root, "Temporary croc head", new Vector3(0, .074f, -.055f), new Vector3(.09f, .055f, .075f), green);
            Box(root, "Temporary croc snout", new Vector3(0, .052f, -.108f), new Vector3(.075f, .028f, .085f), light);
            Box(root, "Temporary croc tail", new Vector3(0, .037f, .13f), new Vector3(.038f, .027f, .13f), green);
            foreach (float x in new[] { -.041f, .041f })
                foreach (float z in new[] { -.03f, .06f })
                    Box(root, "Temporary croc foot", new Vector3(x, .016f, z), new Vector3(.035f, .026f, .035f), green);
            foreach (float x in new[] { -.029f, .029f })
            {
                Box(root, "Temporary croc eye", new Vector3(x, .103f, -.073f), new Vector3(.026f, .025f, .023f), Color.white);
                Box(root, "Temporary croc pupil", new Vector3(x, .103f, -.087f), new Vector3(.012f, .015f, .004f), new Color(.06f, .11f, .09f));
            }
            if (!lead)
            {
                Box(root, "Temporary role hat", new Vector3(0, .123f, -.048f), new Vector3(.11f, .012f, .08f), role);
                Box(root, "Temporary role cap", new Vector3(0, .139f, -.044f), new Vector3(.075f, .025f, .05f), role);
            }
        }

        void RefreshBuild()
        {
            if (pending || ForgeNet.Status != ForgeNetStatus.Live) return;
            pending = true;
            ForgeNet.Get("/agent/builds", text =>
            {
                if (this == null) return;
                pending = false;
                var list = JArray.Parse(text); build = list.Count > 0 ? list[0] as JObject : null;
            }, _ => { if (this != null) pending = false; });
        }

        void Update()
        {
            if (stage == null) return;
            if (Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + 3f; SyncCrew(); RefreshBuild();
            }
            bool pose = Time.unscaledTime >= nextPose;
            if (pose) nextPose = Time.unscaledTime + .15f;
            bool live = ForgeNet.Status == ForgeNetStatus.Live;
            summary.text = !live ? "Hub disconnected · showing crew visuals; task status may be stale"
                : build == null ? "Existing crew · no project workflow yet"
                : ((bool?)build["demo"] == true ? "SCRIPTED DEMO · " : "") + (string)build["title"] + " · " + WorkflowState;
            if (desks.Exists(d => d.portrait.fallback)) summary.text += "\nTemporary avatars · original Albie body asset is missing";
            foreach (var d in desks)
            {
                d.state = d.bridge != null && d.bridge.Busy ? "working" : "idle";
                d.task = "No workflow step assigned. Use Project workflow or Manage crew.";
                if (build?["steps"] is JArray steps)
                    foreach (var step in steps)
                        if ((string)step["type"] == d.id)
                        {
                            d.state = (string)step["state"] ?? "idle";
                            d.task = (string)step[(d.state == "running") ? "progress" : "summary"];
                            if (string.IsNullOrEmpty(d.task)) d.task = (string)step["task"] ?? "";
                            // A later queued step for the same role must not hide a running one.
                            if (d.state == "running") break;
                        }
                if (d.id == "albie") { d.state = live ? "coordinating" : "offline"; d.task = "Albie coordinates the crew. Plan and publication approvals stay in Project workflow."; }
                bool busy = live && (d.state == "running" || d.state == "working");
                Color light = d.state == "failed" ? new Color(1f, .24f, .22f) : d.state == "done" ? new Color(.35f, .88f, .6f) : d.color;
                if (!live) light = Color.gray;
                d.monitor.sharedMaterial.color = light * (busy ? .8f + Mathf.Sin(Time.unscaledTime * 4f) * .18f : .45f);
                d.label.text = d.name + " / " + (live ? d.state : "offline");
                d.label.transform.rotation = officeCamera.transform.rotation;
                // Visual-only motion: work at the desk, otherwise take a small idle walk on that station's rug.
                Vector3 destination = d.seat + (busy ? Vector3.zero : new Vector3(Mathf.Sin(Time.unscaledTime * .45f + d.root.localPosition.x) * .08f, 0, -.04f));
                d.portrait.root.localPosition = Vector3.Lerp(d.portrait.root.localPosition, destination, Time.unscaledDeltaTime * 3f);
                if (d.source != null && pose)
                    foreach (var part in d.portrait.parts)
                    {
                        if (part.source == null) continue;
                        var matrix = d.source.transform.worldToLocalMatrix * part.source.localToWorldMatrix;
                        part.target.localPosition = matrix.GetColumn(3); part.target.localRotation = matrix.rotation;
                        part.target.localScale = matrix.lossyScale;
                        if (part.skinned) ((SkinnedMeshRenderer)part.source).BakeMesh(part.mesh);
                    }
                if (d.id == selected) detail.text = d.name + " · " + (live ? d.state : "offline") + "\n" + d.task;
            }
        }

        Material Material(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var material = new Material(shader) { color = color };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            owned.Add(material); return material;
        }

        GameObject Box(Transform parent, string label, Vector3 position, Vector3 size, Color color)
        {
            var go = new GameObject(label, typeof(MeshFilter), typeof(MeshRenderer));
            // Built-in cube mesh without even a temporary collider.
            go.GetComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            go.transform.SetParent(parent, false); go.transform.localPosition = position; go.transform.localScale = size;
            go.layer = PreviewLayer; go.GetComponent<MeshRenderer>().sharedMaterial = Material(color);
            return go;
        }

        TMP_Text WorldText(Transform parent, string text, Vector3 position, float size)
        {
            var go = new GameObject("Office label", typeof(TextMeshPro));
            go.transform.SetParent(parent, false); go.transform.localPosition = position; go.layer = PreviewLayer;
            var label = go.GetComponent<TextMeshPro>(); label.text = text; label.richText = false;
            label.fontSize = size * 10f; label.alignment = TextAlignmentOptions.Center;
            label.rectTransform.sizeDelta = new Vector2(1.2f, .12f);
            label.color = new Color(.86f, .92f, .94f);
            return label;
        }

        void OnDestroy()
        {
            updates?.Dispose();
            if (officeCamera != null) officeCamera.targetTexture = null;
            if (target != null) { target.Release(); Destroy(target); }
            if (stage != null) Destroy(stage);
            foreach (var item in owned) if (item != null) Destroy(item);
            owned.Clear();
        }
    }
}
