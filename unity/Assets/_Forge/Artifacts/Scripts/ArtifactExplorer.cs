using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// Inspects an imported glTF model without changing it: turn, cut open, separate into its parts, see through.
    /// A part is a mesh node. Where a part goes when separated comes from an empty child node named <c>explode</c>
    /// (its local position is the move, authored with the model; see <c>Tools~/anatomy</c>); a part without one moves
    /// straight out from the model's centre. Separated parts show their node names.
    /// </summary>
    public sealed class ArtifactExplorer : MonoBehaviour
    {
        public const string ExplodeMarker = "explode", XRayMarker = "xray";
        const float MoveSeconds = 0.7f;
        const float GhostAlpha = 0.18f;
        /// <summary>A part whose bounds take up this share of the model's goes see-through in X-ray; smaller ones stay solid.</summary>
        const float GhostShare = 0.12f;
        static readonly int BaseColorFactor = Shader.PropertyToID("baseColorFactor");
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        sealed class Part
        {
            public Transform transform;
            public Vector3 position, offset;
            public MeshFilter filter;
            public Renderer renderer;
            public Mesh original, section;
            public Material[] materials, ghosts;
            public bool large;
            public TextMeshPro label;
            public LineRenderer leader;
            public int side;                       // the callout column it's in: -1 left, 1 right, 0 not yet set out
            public int column;                     // the column it goes in this frame
            public float across, height, slot;     // layout scratch, metres
            public float width;                    // the name's width, metres
            public Quaternion rotation;            // its local rotation as the model was built
            public bool moved;                     // picked up and put somewhere by hand (ArtifactHandGrab)
            public Vector3 drift;                  // how far a moved part was from its layout spot when it started home
            public Quaternion heldRotation;        // and how it was turned then
        }

        readonly List<Part> parts = new List<Part>();
        Quaternion rotation;
        Vector3 position, scale, centre;
        float radius, apart, apartTarget, homeFrom = 1f;
        public int PartCount => parts.Count;
        public bool Separated { get; private set; }
        public bool Sectioned { get; private set; }
        public bool XRay { get; private set; }
        /// <summary>The one part shown solid while the rest are see-through (<see cref="Focus"/>), or null.</summary>
        public Transform Focused { get; private set; }
        /// <summary>
        /// The artifact's scene nodes by id. Its "label_" and "leader_" nodes point at where parts sit when assembled, so
        /// they hide while the parts are apart (set by <see cref="ArtifactGltf"/>; null in checks, which have none).
        /// </summary>
        public IReadOnlyDictionary<string, GameObject> SceneNodes;
        /// <summary>Apart, cut, X-ray or focus changed, by a button, a hand or Albie's voice (the panel's pills follow it).</summary>
        public event System.Action Changed;
        readonly List<GameObject> hiddenScene = new List<GameObject>();
        /// <summary>0 assembled to 1 fully separated; moves over <see cref="MoveSeconds"/>.</summary>
        public float Apart => apart;

        public void Initialize()
        {
            rotation = transform.localRotation;
            position = transform.localPosition;
            scale = transform.localScale;
            var bounds = new Bounds();
            bool first = true;
            foreach (var filter in GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null || !filter.sharedMesh.isReadable) continue;
                var renderer = filter.GetComponent<Renderer>();
                var part = new Part { transform = filter.transform, position = filter.transform.localPosition, filter = filter,
                    rotation = filter.transform.localRotation, heldRotation = filter.transform.localRotation,
                    original = filter.sharedMesh, renderer = renderer, materials = renderer != null ? renderer.sharedMaterials : null };
                parts.Add(part);
                if (renderer == null) continue;
                var b = Local(renderer.bounds);
                if (first) { bounds = b; first = false; }
                else bounds.Encapsulate(b);
            }
            // No readable mesh (a skinned or unreadable model): it still turns about its own middle.
            if (first)
                foreach (var renderer in GetComponentsInChildren<Renderer>(true))
                {
                    var b = Local(renderer.bounds);
                    if (first) { bounds = b; first = false; }
                    else bounds.Encapsulate(b);
                }
            centre = bounds.center;
            radius = Mathf.Max(bounds.extents.magnitude, 1e-4f);
            float volume = Mathf.Max(Volume(bounds), 1e-12f);
            // A model can say which parts X-ray shows through (an empty "xray" child); otherwise the big ones do.
            bool authored = false;
            foreach (var part in parts) authored |= Marker(part.transform, XRayMarker) != null;
            foreach (var part in parts)
            {
                part.offset = Offset(part);
                if (part.renderer == null) continue;
                part.large = authored ? Marker(part.transform, XRayMarker) != null
                    : Volume(Local(part.renderer.bounds)) / volume >= GhostShare;
            }
        }

        public void Turn() => Turn(30f);

        /// <summary>Turns the model about its own centre by <paramref name="degrees"/> (positive: to the right, seen from above).</summary>
        public void Turn(float degrees) => transform.RotateAround(transform.TransformPoint(centre), transform.up, degrees);

        /// <summary>Separates the parts, or puts them back. Only models made of more than one mesh can separate.</summary>
        public void Separate()
        {
            if (parts.Count < 2) return;
            Separated = !Separated;
            apartTarget = Separated ? 1f : 0f;
            if (Separated) MakeLabels();
            else StartHome();
            HideSceneLabels(Separated);
            Changed?.Invoke();
        }

        void HideSceneLabels(bool hide)
        {
            if (hide && SceneNodes != null)
            {
                foreach (var pair in SceneNodes)
                    if (pair.Value != null && pair.Value.activeSelf && (pair.Key.StartsWith("label_") || pair.Key.StartsWith("leader_")))
                    {
                        pair.Value.SetActive(false);
                        hiddenScene.Add(pair.Value);
                    }
            }
            else if (!hide)
            {
                foreach (var go in hiddenScene) if (go != null) go.SetActive(true);
                hiddenScene.Clear();
            }
        }

        /// <summary>Every part's name, in model order.</summary>
        public List<string> PartNames()
        {
            var names = new List<string>(parts.Count);
            foreach (var part in parts) names.Add(DisplayName(part.transform.name));
            return names;
        }

        /// <summary>
        /// The part a spoken name means: "mitral valve" is the Mitral valve, "the lens" the Lens, "frontal lobe" the first
        /// frontal lobe. Exact name first, then a name that contains every word asked for, then one that contains the most.
        /// </summary>
        public Transform FindPart(string spoken)
        {
            if (string.IsNullOrWhiteSpace(spoken)) return null;
            string asked = Clean(spoken);
            var words = asked.Split(' ');
            Transform best = null;
            int bestScore = 0;
            foreach (var part in parts)
            {
                string name = Clean(DisplayName(part.transform.name));
                if (name == asked) return part.transform;
                int hits = 0;
                foreach (var w in words) if (w.Length > 2 && (" " + name + " ").Contains(" " + w)) hits++;
                int score = hits == CountLong(words) && hits > 0 ? 100 + hits : hits;
                if (score > bestScore) { bestScore = score; best = part.transform; }
            }
            return best;
        }

        static int CountLong(string[] words)
        {
            int n = 0;
            foreach (var w in words) if (w.Length > 2) n++;
            return n;
        }

        static string Clean(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
            string t = " " + sb.ToString() + " ";
            foreach (var filler in new[] { " the ", " a ", " an ", " its ", " of " }) t = t.Replace(filler, " ");
            while (t.Contains("  ")) t = t.Replace("  ", " ");
            return t.Trim();
        }

        /// <summary>Shows <paramref name="part"/> solid and every other part see-through, so it stands out. Null shows them all again.</summary>
        public void Focus(Transform part)
        {
            if (part == null) { Unfocus(); return; }
            Focused = part;
            foreach (var p in parts)
            {
                if (p.renderer == null || p.materials == null) continue;
                if (p.transform == part) { p.renderer.sharedMaterials = p.materials; continue; }
                if (p.ghosts == null) p.ghosts = Ghosts(p.materials);
                if (p.ghosts != null) p.renderer.sharedMaterials = p.ghosts;
            }
            Changed?.Invoke();
        }

        public void Unfocus()
        {
            if (Focused == null) return;
            Focused = null;
            foreach (var p in parts)
            {
                if (p.renderer == null || p.materials == null) continue;
                p.renderer.sharedMaterials = XRay && p.large && p.ghosts != null ? p.ghosts : p.materials;
            }
            Changed?.Invoke();
        }

        // Parts moved by hand glide home from wherever they were put, instead of jumping to their spot in the layout.
        void StartHome()
        {
            float eased = Ease(apart);
            homeFrom = Mathf.Max(eased, 0.01f);
            foreach (var part in parts)
            {
                if (!part.moved) continue;
                part.drift = (part.transform.localPosition - part.position - part.offset * eased) / homeFrom;
                part.heldRotation = part.transform.localRotation;
            }
        }

        static float Ease(float t) => t * t * (3f - 2f * t);

        /// <summary>
        /// The part a hand pinching at <paramref name="world"/> picks up: of the parts whose bounds (grown by
        /// <paramref name="margin"/> metres) hold the point, the smallest, so a valve beats the ventricle around it.
        /// Only once the model is fully apart; null otherwise.
        /// </summary>
        public Transform PartAt(Vector3 world, float margin)
        {
            if (!Separated || apart < 0.99f) return null;
            Transform best = null;
            float bestVolume = float.MaxValue;
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part.renderer == null || !part.renderer.enabled) continue;
                var b = part.renderer.bounds;
                b.Expand(margin * 2f);
                if (!b.Contains(world)) continue;
                float v = Volume(b);
                if (v < bestVolume) { bestVolume = v; best = part.transform; }
            }
            return best;
        }

        /// <summary>A hand moved this part: Assemble and Reset view bring it home from where it is.</summary>
        public void MarkMoved(Transform part)
        {
            for (int i = 0; i < parts.Count; i++)
                if (parts[i].transform == part) { parts[i].moved = true; return; }
        }

        /// <summary>The name a part shows (its node name without a ".001").</summary>
        public static string NameOf(Transform part) => part != null ? DisplayName(part.name) : null;

        void Update()
        {
            if (apart == apartTarget) return;
            apart = Mathf.MoveTowards(apart, apartTarget, Time.deltaTime / MoveSeconds);
            Place(apart);
        }

        /// <summary>Puts every part <paramref name="t"/> of the way apart (0 together, 1 separated). Checks call it directly.</summary>
        public void Place(float t)
        {
            apart = t;
            float eased = Ease(t);
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (!part.moved)
                {
                    part.transform.localPosition = part.position + part.offset * eased;
                    continue;
                }
                // On its way home from where a hand put it: the drift and the turn fade out with the separation.
                float k = Mathf.Clamp01(eased / homeFrom);
                part.transform.localPosition = part.position + part.offset * eased + part.drift * eased;
                part.transform.localRotation = Quaternion.Slerp(part.rotation, part.heldRotation, k);
                if (t <= 0f)
                {
                    part.moved = false;
                    part.drift = Vector3.zero;
                    part.transform.localRotation = part.rotation;
                }
            }
            ShowLabels(t > 0.6f);
        }

        /// <summary>Cuts the model open on one plane through its centre, facing the viewer, and keeps the back half.</summary>
        public void Section()
        {
            Sectioned = !Sectioned;
            foreach (var part in parts)
            {
                if (Sectioned && part.section == null) part.section = Cut(part);
                part.filter.sharedMesh = Sectioned ? part.section : part.original;
            }
            Changed?.Invoke();
        }

        /// <summary>Large outer parts go see-through so what's inside shows; small inner parts stay solid.</summary>
        public void ToggleXRay()
        {
            Unfocus();
            XRay = !XRay;
            foreach (var part in parts)
            {
                if (part.renderer == null || part.materials == null || !part.large) continue;
                if (XRay && part.ghosts == null) part.ghosts = Ghosts(part.materials);
                if (part.ghosts != null) part.renderer.sharedMaterials = XRay ? part.ghosts : part.materials;
            }
            Changed?.Invoke();
        }

        /// <summary>How many parts X-ray turns see-through.</summary>
        public int LargeParts
        {
            get
            {
                int n = 0;
                foreach (var part in parts) if (part.large) n++;
                return n;
            }
        }

        public void ResetView()
        {
            if (Sectioned) Section();
            Unfocus();
            if (XRay) ToggleXRay();
            if (Separated) StartHome();
            HideSceneLabels(false);
            Separated = false;
            apartTarget = 0f;
            Place(0f);
            transform.localRotation = rotation;
            transform.localScale = scale;
            transform.localPosition = position;
            Changed?.Invoke();
        }

        Mesh Cut(Part part)
        {
            // One plane for the whole model: through its centre, normal along the model's local +Z (toward the viewer
            // in the templates). Each part is tested in model space at its assembled position, so the cut lines up.
            var mesh = Instantiate(part.original);
            mesh.name = part.original.name + " section";
            var toModel = transform.worldToLocalMatrix * part.transform.parent.localToWorldMatrix *
                          Matrix4x4.TRS(part.position, part.transform.localRotation, part.transform.localScale);
            var vertices = part.original.vertices;
            var front = new bool[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) front[i] = toModel.MultiplyPoint3x4(vertices[i]).z > centre.z;
            var kept = new List<int>();
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                var indices = part.original.GetTriangles(sub);
                kept.Clear();
                for (int i = 0; i < indices.Length; i += 3)
                {
                    if (front[indices[i]] || front[indices[i + 1]] || front[indices[i + 2]]) continue;
                    kept.Add(indices[i]); kept.Add(indices[i + 1]); kept.Add(indices[i + 2]);
                }
                mesh.SetTriangles(kept, sub, false);
            }
            return mesh;
        }

        /// <summary>Blender makes object names unique, so markers export as "explode", "explode.001" and so on.</summary>
        public static bool IsExplodeMarker(string name) => IsMarker(name, ExplodeMarker);

        static bool IsMarker(string name, string marker) =>
            name != null && (name == marker || name.StartsWith(marker + "."));

        static Transform Marker(Transform part, string marker)
        {
            for (int i = 0; i < part.childCount; i++)
                if (IsMarker(part.GetChild(i).name, marker)) return part.GetChild(i);
            return null;
        }

        /// <summary>The name to show: the node's, without a ".001" a modelling tool added to keep names unique.</summary>
        public static string DisplayName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int dot = name.LastIndexOf('.');
            if (dot > 0 && dot == name.Length - 4 && char.IsDigit(name[dot + 1]) && char.IsDigit(name[dot + 2]) && char.IsDigit(name[dot + 3]))
                return name.Substring(0, dot);
            return name;
        }

        Vector3 Offset(Part part)
        {
            var marker = Marker(part.transform, ExplodeMarker);
            if (marker != null)
                return part.transform.localRotation * Vector3.Scale(part.transform.localScale, marker.localPosition);
            // No authored layout: straight out from the centre, by a third of the model's size.
            var partCentre = part.renderer != null ? Local(part.renderer.bounds).center : part.position;
            var dir = partCentre - centre;
            if (dir.sqrMagnitude < 1e-10f) dir = Vector3.up;
            var world = transform.TransformVector(dir.normalized * radius * 0.35f);
            return part.transform.parent.InverseTransformVector(world);
        }

        // Names of separated parts, set out as callouts: two columns beside the model as the viewer sees it, each
        // sorted by the height of its parts and spaced a line apart, with a leader line to each part. Laid out every
        // frame they show, so they stay readable while the parts move and as the viewer walks round.
        const float LabelSize = 0.13f;      // TMP font size at world scale 1: about 1.3 cm tall
        const float LineSpacing = 0.021f;   // between label rows, metres
        const float ColumnGap = 0.02f;      // from the model's edge on that side to its column
        static readonly Color LeaderColour = new Color(0.82f, 0.83f, 0.86f, 0.85f);
        Part[] labelled = new Part[0], column = new Part[0];
        Transform labelRoot;

        /// <summary>Something beside the model the names must not run under (the artifact's panel), and its half width.</summary>
        public Transform Avoid;
        public float AvoidHalfWidth;

        void MakeLabels()
        {
            if (labelRoot != null) return;
            labelRoot = new GameObject("labels").transform;
            labelRoot.SetParent(transform, false);
            var list = new List<Part>();
            foreach (var part in parts)
            {
                if (part.renderer == null) continue;
                string name = DisplayName(part.transform.name);
                if (string.IsNullOrEmpty(name) || name.StartsWith("Mesh") || name.StartsWith("Node")) continue;
                var go = new GameObject("label " + name);
                go.transform.SetParent(labelRoot, false);
                var text = go.AddComponent<TextMeshPro>();
                if (ForgeTheme.Font != null) text.font = ForgeTheme.Font;
                text.text = name;
                text.textWrappingMode = TextWrappingModes.NoWrap;
                text.outlineWidth = 0.22f;
                text.outlineColor = new Color32(8, 10, 14, 235);
                text.rectTransform.sizeDelta = new Vector2(2f, 0.5f);
                text.fontSize = LabelSize;
                go.AddComponent<ArtifactBillboard>();
                float world = go.transform.lossyScale.x;
                if (world > 1e-6f) go.transform.localScale *= 1f / world;
                var leader = new GameObject("leader").AddComponent<LineRenderer>();
                leader.transform.SetParent(go.transform, false);
                leader.useWorldSpace = true;
                leader.sharedMaterial = ArtifactLook.Trail();
                leader.startColor = leader.endColor = LeaderColour;
                leader.widthMultiplier = 0.0008f;
                leader.positionCount = 2;
                leader.numCapVertices = 2;
                leader.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                leader.receiveShadows = false;
                leader.alignment = LineAlignment.View;
                part.label = text;
                part.leader = leader;
                part.width = text.GetPreferredValues(name).x * go.transform.lossyScale.x;
                part.side = 0;
                list.Add(part);
            }
            labelled = list.ToArray();
            column = new Part[labelled.Length];
            ShowLabels(apart > 0.6f);
        }

        void ShowLabels(bool show)
        {
            for (int i = 0; i < labelled.Length; i++) labelled[i].label.gameObject.SetActive(show);
        }

        void LateUpdate()
        {
            if (labelled.Length == 0 || !labelled[0].label.gameObject.activeSelf) return;
            var cam = Camera.main;
            if (cam != null) LayOutLabels(cam.transform.position);
        }

        /// <summary>Sets out the part names as two columns of callouts seen from <paramref name="eye"/>. Checks call it directly.</summary>
        public void LayOutLabels(Vector3 eye)
        {
            if (labelled.Length == 0) return;
            var c = transform.TransformPoint(centre);
            var toModel = c - eye; toModel.y = 0f;
            var right = toModel.sqrMagnitude > 1e-8f ? Vector3.Cross(Vector3.up, toModel.normalized) : Vector3.right;
            // How far the model reaches to each side right now, so each column just clears its own side (the panel
            // usually stands on one side, so a column no further out than it needs to be stays clear of it).
            float reachLeft = 0f, reachRight = 0f;
            for (int i = 0; i < labelled.Length; i++)
            {
                var p = labelled[i];
                var b = p.renderer.bounds;
                float half = Mathf.Abs(b.extents.x * right.x) + Mathf.Abs(b.extents.y * right.y) + Mathf.Abs(b.extents.z * right.z);
                float across = Vector3.Dot(b.center - c, right);
                reachLeft = Mathf.Max(reachLeft, half - across);
                reachRight = Mathf.Max(reachRight, half + across);
                p.across = across;
                p.height = Vector3.Dot(b.center - c, Vector3.up);
            }
            float xLeft = reachLeft + ColumnGap, xRight = reachRight + ColumnGap;
            // Where the panel's near edge is, if it's beside the model: a name that would run under it goes to the other side.
            int blocked = 0; float edge = 0f;
            if (Avoid != null)
            {
                float a = Vector3.Dot(Avoid.position - c, right);
                blocked = a < 0f ? -1 : 1;
                edge = Mathf.Abs(a) - AvoidHalfWidth - 0.01f;
            }
            for (int i = 0; i < labelled.Length; i++)
            {
                var p = labelled[i];
                p.column = p.across < 0f ? -1 : 1;
                if (blocked != 0 && p.column == blocked && (blocked < 0 ? xLeft : xRight) + p.width > edge) p.column = -blocked;
            }
            Column(-1, c, right, xLeft);
            Column(1, c, right, xRight);
        }

        void Column(int side, Vector3 c, Vector3 right, float x)
        {
            int n = 0;
            for (int i = 0; i < labelled.Length; i++)
                if (labelled[i].column == side) column[n++] = labelled[i];
            if (n == 0) return;
            // Top to bottom by the parts' heights (insertion sort: a couple of dozen parts, no allocation).
            for (int i = 1; i < n; i++)
            {
                var p = column[i]; int j = i - 1;
                while (j >= 0 && column[j].height < p.height) { column[j + 1] = column[j]; j--; }
                column[j + 1] = p;
            }
            // Each row as close to its part's height as it can be while staying a line below the row above, then the
            // whole column moved back so on average the rows sit level with their parts.
            float shift = 0f;
            for (int i = 0; i < n; i++)
            {
                column[i].slot = i == 0 ? column[i].height : Mathf.Min(column[i].height, column[i - 1].slot - LineSpacing);
                shift += column[i].height - column[i].slot;
            }
            shift /= n;
            for (int i = 0; i < n; i++)
            {
                var p = column[i];
                var spot = c + right * (side * x) + Vector3.up * (p.slot + shift);
                if (p.side != side)
                {
                    // Left column reads toward the model (right-aligned), right column away from it.
                    p.side = side;
                    p.label.alignment = side < 0 ? TextAlignmentOptions.Right : TextAlignmentOptions.Left;
                    p.label.rectTransform.pivot = new Vector2(side < 0 ? 1f : 0f, 0.5f);
                }
                p.label.transform.position = spot;
                p.leader.SetPosition(0, spot - right * (side * 0.006f));
                p.leader.SetPosition(1, p.renderer.bounds.center);
            }
        }

        static Material[] Ghosts(Material[] source)
        {
            var template = ArtifactLook.XRay();
            if (template == null) return null;
            var ghosts = new Material[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                var color = Color.white;
                if (source[i] != null && source[i].HasProperty(BaseColorFactor)) color = source[i].GetColor(BaseColorFactor);
                else if (source[i] != null && source[i].HasProperty(BaseColor)) color = source[i].GetColor(BaseColor);
                color.a = GhostAlpha;
                ghosts[i] = new Material(template) { name = (source[i] != null ? source[i].name : "part") + " x-ray" };
                ghosts[i].SetColor(BaseColor, color);
            }
            return ghosts;
        }

        Bounds Local(Bounds world)
        {
            var b = new Bounds(transform.InverseTransformPoint(world.center), Vector3.zero);
            var e = world.extents;
            for (int i = 0; i < 8; i++)
                b.Encapsulate(transform.InverseTransformPoint(world.center + new Vector3((i & 1) == 0 ? -e.x : e.x,
                    (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z)));
            return b;
        }

        static float Volume(Bounds b) => b.size.x * b.size.y * b.size.z;

        void OnDestroy()
        {
            foreach (var part in parts)
            {
                Free(part.section);
                if (part.ghosts != null) foreach (var g in part.ghosts) Free(g);
            }
        }

        static void Free(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
