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
        }

        readonly List<Part> parts = new List<Part>();
        Quaternion rotation;
        Vector3 position, scale, centre;
        float radius, apart, apartTarget;
        public int PartCount => parts.Count;
        public bool Separated { get; private set; }
        public bool Sectioned { get; private set; }
        public bool XRay { get; private set; }
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
                    original = filter.sharedMesh, renderer = renderer, materials = renderer != null ? renderer.sharedMaterials : null };
                parts.Add(part);
                if (renderer == null) continue;
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

        public void Turn() => transform.RotateAround(transform.TransformPoint(centre), transform.up, 30f);

        /// <summary>Separates the parts, or puts them back. Only models made of more than one mesh can separate.</summary>
        public void Separate()
        {
            if (parts.Count < 2) return;
            Separated = !Separated;
            apartTarget = Separated ? 1f : 0f;
            if (Separated) MakeLabels();
        }

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
            float eased = t * t * (3f - 2f * t);
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                part.transform.localPosition = part.position + part.offset * eased;
                if (part.label != null) part.label.gameObject.SetActive(t > 0.6f);
            }
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
        }

        /// <summary>Large outer parts go see-through so what's inside shows; small inner parts stay solid.</summary>
        public void ToggleXRay()
        {
            XRay = !XRay;
            foreach (var part in parts)
            {
                if (part.renderer == null || part.materials == null || !part.large) continue;
                if (XRay && part.ghosts == null) part.ghosts = Ghosts(part.materials);
                if (part.ghosts != null) part.renderer.sharedMaterials = XRay ? part.ghosts : part.materials;
            }
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
            if (XRay) ToggleXRay();
            Separated = false;
            apartTarget = 0f;
            Place(0f);
            transform.localRotation = rotation;
            transform.localScale = scale;
            transform.localPosition = position;
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

        void MakeLabels()
        {
            var placed = new List<Vector3>();  // where each label will be once the parts are apart (world)
            foreach (var part in parts)
            {
                if (part.label != null || part.renderer == null) continue;
                string name = DisplayName(part.transform.name);
                if (string.IsNullOrEmpty(name) || name.StartsWith("Mesh") || name.StartsWith("Node")) continue;
                var go = new GameObject("label " + name);
                go.transform.SetParent(part.transform, false);
                var text = go.AddComponent<TextMeshPro>();
                if (ForgeTheme.Font != null) text.font = ForgeTheme.Font;
                text.text = name;
                text.alignment = TextAlignmentOptions.Center;
                text.textWrappingMode = TextWrappingModes.NoWrap;
                text.outlineWidth = 0.22f;
                text.outlineColor = new Color32(8, 10, 14, 235);
                text.rectTransform.sizeDelta = new Vector2(2f, 0.5f);
                text.fontSize = 0.15f;
                go.AddComponent<ArtifactBillboard>();
                // Above the part, or below it when another label is already there, judged where the parts will be once
                // apart. The label rides on the part, so it's placed relative to where the part is now.
                var parent = part.transform.parent;
                var moved = parent.TransformVector(part.transform.localPosition - part.position);
                var full = parent.TransformVector(part.offset);
                var b = part.renderer.bounds;
                var apartCentre = b.center - moved + full;
                float lift = b.extents.y + 0.012f;
                var spot = apartCentre + Vector3.up * lift;
                if (Crowded(placed, spot))
                {
                    var below = apartCentre - Vector3.up * lift;
                    spot = Crowded(placed, below) ? spot + Vector3.up * 0.022f : below;
                }
                placed.Add(spot);
                go.transform.position = spot - full + moved;
                float world = go.transform.lossyScale.x;
                if (world > 1e-6f) go.transform.localScale *= 1f / world;
                go.SetActive(apart > 0.6f);
                part.label = text;
            }
        }

        // Two labels clash when they're within a word's width side to side and a line's height up and down.
        static bool Crowded(List<Vector3> placed, Vector3 spot)
        {
            foreach (var p in placed)
                if (Mathf.Abs(p.y - spot.y) < 0.02f && new Vector2(p.x - spot.x, p.z - spot.z).magnitude < 0.07f) return true;
            return false;
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
