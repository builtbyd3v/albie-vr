using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// Inspects an imported glTF model without changing its file: turn, cut open, separate into its parts, see through,
    /// and show one part (or a few) on their own.
    /// - Authored models (the bundled anatomy): a part is a mesh node. Where it goes when separated comes from an empty
    ///   child node named <c>explode</c> (its local position is the move, authored with the model; see <c>Tools~/anatomy</c>).
    /// - Any other model (one brought in with model_import): parts are built from its node tree (<see cref="AutoParts"/>):
    ///   the first level with more than one branch, tiny fragments merged into their neighbours, at most
    ///   <see cref="MaxAutoParts"/>, named from their nodes or "Part 3 (top)". A model that is one mesh is one piece.
    /// A part without an <c>explode</c> marker moves straight out from the model's centre. Separated parts show their names.
    /// <see cref="Isolate"/> lifts parts out for a close look: the rest fade to a faint silhouette (or stay see-through),
    /// the chosen ones glow a little, come toward the viewer, grow, and show their name and what they do.
    /// </summary>
    public sealed class ArtifactExplorer : MonoBehaviour
    {
        public const string ExplodeMarker = "explode", XRayMarker = "xray";
        /// <summary>The most parts an unauthored model is split into, so the names stay readable.</summary>
        public const int MaxAutoParts = 20;
        const float MoveSeconds = 0.7f;
        const float GhostAlpha = 0.18f;
        const float FaintAlpha = 0.06f;
        /// <summary>A part whose bounds take up this share of the model's goes see-through in X-ray; smaller ones stay solid.</summary>
        const float GhostShare = 0.12f;
        /// <summary>An unauthored branch smaller than this share of the model's size is merged into its nearest neighbour.</summary>
        const float FragmentShare = 0.06f;
        const float TurnDegreesPerSecond = 160f;
        static readonly int BaseColorFactor = Shader.PropertyToID("baseColorFactor");
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        /// <summary>The colour a chosen or pointed-at part is lifted toward (the theme's pale tool blue).</summary>
        static readonly Color Lift = ForgeTheme.Tool;
        public const int PickLayer = 2;  // Ignore Raycast: nothing else's default raycasts hit the picking colliders

        public enum Look { Isolate, Highlight }

        sealed class Piece
        {
            public MeshFilter filter;
            public Renderer renderer;
            public Mesh original, section;
            public Material[] materials, ghosts, faint, shown;
            public MeshCollider pick;
            public int tint;
        }

        sealed class Part
        {
            public Transform transform;
            public Piece[] pieces;
            public Vector3 position, offset, scale, centre;  // assembled, in its parent's space
            public bool large;
            public TextMeshPro label;
            public LineRenderer leader;
            public bool labelOn;
            public int lines = 1;
            public int side;                       // the callout column it's in: -1 left, 1 right, 0 not yet set out
            public int column;                     // the column it goes in this frame
            public float across, height, slot;     // layout scratch, metres
            public float width;                    // the name's width, metres
            public Quaternion rotation;            // its local rotation as the model was built
            public bool moved;                     // picked up and put somewhere by hand (ArtifactHandGrab)
            public Vector3 drift;                  // how far a moved part was from its layout spot when it started home
            public Quaternion heldRotation;        // and how it was turned then
            public bool chosen;                    // shown on its own (Isolate)
            public float lifted, liftTarget;       // 0 in place to 1 brought forward
            public Vector3 lift;                   // how far forward, in its parent's space
            public float grow = 1f;                // how much bigger once brought forward
        }

        readonly List<Part> parts = new List<Part>();
        readonly Dictionary<Collider, Part> byCollider = new Dictionary<Collider, Part>();
        readonly List<Transform> chosen = new List<Transform>();
        Quaternion rotation;
        Vector3 position, scale, centre;
        float radius, apart, apartTarget, homeFrom = 1f, turnLeft;
        bool moving;
        public int PartCount => parts.Count;
        public bool Separated { get; private set; }
        public bool Sectioned { get; private set; }
        public bool XRay { get; private set; }
        /// <summary>The parts come from the model's node tree, not from markers authored with it.</summary>
        public bool AutoNamed { get; private set; }
        /// <summary>The first part shown on its own (<see cref="Isolate"/>, <see cref="Focus"/>), or null.</summary>
        public Transform Focused => chosen.Count > 0 ? chosen[0] : null;
        /// <summary>Every part shown on its own, in the order asked.</summary>
        public IReadOnlyList<Transform> Chosen => chosen;
        public bool Isolating => chosen.Count > 0;
        /// <summary>How the rest look while parts are chosen: a faint silhouette (Isolate) or see-through (Highlight).</summary>
        public Look Mode { get; private set; }
        /// <summary>The part a hand, ray or mouse is on now (lit a little, so the user sees what "this" will mean), or null.</summary>
        public Transform Hovered { get; private set; }
        /// <summary>What the parts are called when people talk, from the template's parts.json (null for an imported model).</summary>
        public PartWords Words;
        /// <summary>What the user points at and looks at (added by <see cref="ArtifactGltf"/>; null in checks unless they add one).</summary>
        public ArtifactPartPointer Pointer;
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
            var filters = new List<MeshFilter>();
            foreach (var filter in GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh != null && filter.sharedMesh.isReadable && filter.GetComponent<TMP_Text>() == null) filters.Add(filter);
            bool authored = false;
            foreach (var f in filters) authored |= Marker(f.transform, ExplodeMarker) != null || Marker(f.transform, XRayMarker) != null;
            if (authored || filters.Count <= 1)
                foreach (var f in filters) parts.Add(NewPart(f.transform, new[] { f }));
            else
            {
                AutoNamed = true;
                foreach (var unit in AutoParts(filters))
                {
                    var inside = new List<MeshFilter>();
                    foreach (var f in unit.GetComponentsInChildren<MeshFilter>(true)) if (filters.Contains(f)) inside.Add(f);
                    if (inside.Count > 0) parts.Add(NewPart(unit, inside.ToArray()));
                }
            }
            var bounds = new Bounds();
            bool first = true;
            foreach (var part in parts)
                foreach (var piece in part.pieces)
                {
                    if (piece.renderer == null) continue;
                    var b = Local(piece.renderer.bounds);
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
            bool marked = false;
            foreach (var part in parts) marked |= Marker(part.transform, XRayMarker) != null;
            foreach (var part in parts)
            {
                bool any = WorldBounds(part, false, out var wb);
                part.centre = part.transform.parent != null ? part.transform.parent.InverseTransformPoint(any ? wb.center : part.transform.position) : part.position;
                part.offset = Offset(part);
                if (!any) continue;
                part.large = marked ? Marker(part.transform, XRayMarker) != null : Volume(Local(wb)) / volume >= GhostShare;
            }
            if (AutoNamed) NameParts();
        }

        Part NewPart(Transform t, MeshFilter[] filters)
        {
            var pieces = new Piece[filters.Length];
            for (int i = 0; i < filters.Length; i++)
            {
                var r = filters[i].GetComponent<Renderer>();
                pieces[i] = new Piece { filter = filters[i], original = filters[i].sharedMesh, renderer = r, materials = r != null ? r.sharedMaterials : null };
                pieces[i].shown = pieces[i].materials;
            }
            return new Part { transform = t, pieces = pieces, position = t.localPosition, rotation = t.localRotation, heldRotation = t.localRotation, scale = t.localScale };
        }

        // ---------------------------------------------------------------- parts of a model nobody split for us
        /// <summary>
        /// The branches of an unauthored model's node tree to use as its parts: down through the wrappers exporters add
        /// (Sketchfab_model / root / GLTF_SceneRootNode, each with one child) to the first node with several branches
        /// that hold meshes; a branch that is itself several branches is split while there are fewer than four parts;
        /// fragments under <see cref="FragmentShare"/> of the model's size join their nearest neighbour; and the smallest
        /// keep joining theirs until there are at most <see cref="MaxAutoParts"/>. One branch in the end is one piece.
        /// </summary>
        List<Transform> AutoParts(List<MeshFilter> filters)
        {
            var counts = new Dictionary<Transform, int>();
            foreach (var f in filters)
                for (var t = f.transform; t != null && t != transform.parent; t = t.parent)
                {
                    counts.TryGetValue(t, out int n);
                    counts[t] = n + 1;
                }
            bool Own(Transform t) => t != transform && t.TryGetComponent<MeshFilter>(out var mf) && filters.Contains(mf);
            // glTFast puts a mesh's second and later primitives on children at no offset: they're the same piece.
            bool SamePiece(Transform parent, Transform kid) => Own(parent) && kid.childCount == 0 && Own(kid)
                && kid.localPosition == Vector3.zero && kid.localRotation == Quaternion.identity && kid.localScale == Vector3.one;
            List<Transform> Branches(Transform t)
            {
                var list = new List<Transform>();
                for (int i = 0; i < t.childCount; i++)
                    if (counts.ContainsKey(t.GetChild(i)) && !SamePiece(t, t.GetChild(i))) list.Add(t.GetChild(i));
                return list;
            }
            // Down through single-child wrappers to where it branches. A node with its own mesh and branches under it:
            // with apply, the branches move up beside it, so each moves alone.
            List<Transform> Split(Transform t, bool apply)
            {
                var level = t;
                while (!Own(level) && Branches(level).Count == 1) level = Branches(level)[0];
                var kids = Branches(level);
                var list = new List<Transform>();
                if (kids.Count == 0) { list.Add(level); return list; }
                if (Own(level))
                {
                    if (apply) foreach (var k in kids) k.SetParent(level.parent, true);
                    list.Add(level);
                }
                list.AddRange(kids);
                return list;
            }
            var units = Split(transform, true);
            // Too few to be worth separating: open up the biggest branch that is itself several.
            for (int guard = 0; units.Count < 4 && guard < 8; guard++)
            {
                Transform widest = null;
                int most = 1;
                foreach (var u in units)
                {
                    if (!counts.TryGetValue(u, out int n) || n <= most) continue;
                    int inner = Split(u, false).Count;
                    if (inner < 2 || units.Count - 1 + inner > MaxAutoParts) continue;
                    most = n; widest = u;
                }
                if (widest == null) break;
                int at = units.IndexOf(widest);
                units.RemoveAt(at);
                units.InsertRange(at, Split(widest, true));
            }
            if (units.Count < 2) return units;

            // Fragments join their nearest neighbour, then the smallest join theirs until there are few enough.
            var boxes = new List<Bounds>(units.Count);
            var model = new Bounds();
            bool any = false;
            for (int i = 0; i < units.Count; i++)
            {
                bool has = BranchBounds(units[i], out var b);
                boxes.Add(has ? b : new Bounds(transform.InverseTransformPoint(units[i].position), Vector3.zero));
                if (!has) continue;
                if (!any) { model = b; any = true; } else model.Encapsulate(b);
            }
            float size = any ? model.size.magnitude : 1f;
            while (units.Count > 2)
            {
                int smallest = -1;
                float least = float.MaxValue;
                for (int i = 0; i < units.Count; i++)
                {
                    float s = boxes[i].size.magnitude;
                    if (s < least) { least = s; smallest = i; }
                }
                if (least >= size * FragmentShare && units.Count <= MaxAutoParts) break;
                int nearest = -1;
                float closest = float.MaxValue;
                for (int i = 0; i < units.Count; i++)
                {
                    if (i == smallest) continue;
                    float d = boxes[i].SqrDistance(boxes[smallest].center);
                    if (d < closest) { closest = d; nearest = i; }
                }
                if (nearest < 0) break;
                units[nearest] = Merge(units[nearest], units[smallest]);
                var grown = boxes[nearest];
                grown.Encapsulate(boxes[smallest]);
                boxes[nearest] = grown;
                units.RemoveAt(smallest);
                boxes.RemoveAt(smallest);
            }
            return units;
        }

        readonly HashSet<Transform> madeGroups = new HashSet<Transform>();

        // Puts a fragment with a part: under a group made for that part, at no offset from where it was, so nothing skews.
        Transform Merge(Transform into, Transform other)
        {
            if (!madeGroups.Contains(into))
            {
                var group = new GameObject(into.name).transform;
                group.SetParent(into.parent, false);
                group.SetSiblingIndex(into.GetSiblingIndex());
                into.SetParent(group, true);
                madeGroups.Add(group);
                into = group;
            }
            other.SetParent(into, true);
            return into;
        }

        bool BranchBounds(Transform t, out Bounds b)
        {
            b = default;
            bool any = false;
            foreach (var r in t.GetComponentsInChildren<Renderer>(true))
            {
                if (r is LineRenderer) continue;
                var lb = Local(r.bounds);
                if (!any) { b = lb; any = true; } else b.Encapsulate(lb);
            }
            return any;
        }

        // Names for parts the model didn't name for people: from the branch or a node inside it, else "Part 3 (top)".
        // The branch is renamed, so the name shows wherever a part's node name does (hand hints, voice).
        void NameParts()
        {
            var names = new string[parts.Count];
            var keep = new bool[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                string raw = DisplayName(parts[i].transform.name);
                string name = PartNaming.Clean(raw);
                // Already a name a person would use ("Mitral valve.001", "Fan blades"): it stays as it is.
                keep[i] = name != null && string.Equals(name, raw.Replace('_', ' ').Trim(), System.StringComparison.OrdinalIgnoreCase);
                if (name == null)
                    foreach (var t in parts[i].transform.GetComponentsInChildren<Transform>(true))
                        if ((name = PartNaming.Clean(t.name)) != null) break;
                names[i] = name;
            }
            var repeated = new bool[parts.Count];
            for (int i = 0; i < parts.Count; i++)
                for (int j = 0; j < parts.Count; j++) repeated[i] |= j != i && names[i] != null && names[j] == names[i];
            for (int i = 0; i < parts.Count; i++)
            {
                if (names[i] == null) names[i] = $"Part {i + 1} ({Where(parts[i])})";
                else if (repeated[i]) names[i] = names[i] + " (" + Where(parts[i]) + ")";
                else continue;
                keep[i] = false;
            }
            // Still the same (two "Wheel (left)"): number them.
            for (int i = 0; i < parts.Count; i++)
            {
                int n = 1;
                for (int j = i + 1; j < parts.Count; j++)
                    if (names[j] == names[i]) { names[j] = names[j] + " " + (++n); keep[j] = false; }
                if (n > 1) { names[i] = names[i] + " 1"; keep[i] = false; }
                if (!keep[i]) parts[i].transform.name = names[i];
            }
        }

        // Where a part sits seen from the front of the artifact (a glTF faces +z; the artifact turns it to the viewer).
        string Where(Part part)
        {
            if (!WorldBounds(part, false, out var b)) return "middle";
            var space = transform.parent != null && transform.parent.parent != null ? transform.parent.parent : transform.parent;
            Vector3 d = b.center - transform.TransformPoint(centre);
            float r = transform.TransformVector(Vector3.right * radius).magnitude;
            if (space != null) { d = space.InverseTransformVector(d); r = space.InverseTransformVector(Vector3.right * r).magnitude; }
            r = Mathf.Max(r, 1e-6f);
            return PartNaming.Where(d.x / r, d.y / r, -d.z / r);
        }

        public void Turn() => Turn(30f);

        /// <summary>Turns the model about its own centre by <paramref name="degrees"/> (positive: to the right, seen from above).</summary>
        public void Turn(float degrees) => transform.RotateAround(transform.TransformPoint(centre), transform.up, degrees);

        /// <summary>Separates the parts, or puts them back. Only models made of more than one part can separate.</summary>
        public void Separate()
        {
            if (parts.Count < 2) return;
            Separated = !Separated;
            apartTarget = Separated ? 1f : 0f;
            moving = true;
            if (Separated) MakeLabels();
            else StartHome();
            SceneLabels();
            Changed?.Invoke();
        }

        // The scene's own labels point at where parts sit assembled: they hide while parts are apart or shown on their own.
        void SceneLabels()
        {
            bool hide = Separated || Isolating;
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

        /// <summary>The part at <paramref name="index"/> in <see cref="PartNames"/>.</summary>
        public Transform PartAtIndex(int index) => index >= 0 && index < parts.Count ? parts[index].transform : null;

        /// <summary>The part's one-line description from the template's words, or null.</summary>
        public string About(Transform part) => part != null && Words != null ? Words.About(NameOf(part)) : null;

        /// <summary>What <paramref name="spoken"/> asks for on this model (<see cref="PartMatch.Resolve"/>).</summary>
        public PartMatchResult Match(string spoken) => PartMatch.Resolve(PartNames(), Words, spoken);

        /// <summary>
        /// The one part a spoken name means: "mitral valve" is the Mitral valve, "the lens" the Lens. Several fit ("ventricle"):
        /// the first of them. Null when none does. <see cref="Match"/> says when it's a choice.
        /// </summary>
        public Transform FindPart(string spoken)
        {
            if (string.IsNullOrWhiteSpace(spoken)) return null;
            var m = Match(spoken);
            if (m.Parts.Count > 0) return PartAtIndex(m.Parts[0]);
            foreach (var o in m.Options)
                if (o != null && o.StartsWith("#") && int.TryParse(o.Substring(1), out int i)) return PartAtIndex(i);
            return null;
        }

        /// <summary>Shows <paramref name="part"/> solid and every other part see-through, so it stands out. Null shows them all again.</summary>
        public void Focus(Transform part)
        {
            if (part == null) { Unfocus(); return; }
            Isolate(new[] { part }, Look.Highlight, false);
        }

        public void Unfocus()
        {
            if (Isolating) Clear();
        }

        void Clear()
        {
            chosen.Clear();
            foreach (var p in parts) { p.chosen = false; p.liftTarget = 0f; }
            moving = true;
            Restyle();
            Relabel();
            ShowLabels();
            SceneLabels();
            Changed?.Invoke();
        }

        /// <summary>
        /// Shows <paramref name="show"/> on their own. <see cref="Look.Isolate"/>: every other part fades to a faint
        /// silhouette, there for context but out of the way; <see cref="Look.Highlight"/>: the others go see-through, as
        /// in X-ray. The chosen parts glow a little and show their name and what they do. With <paramref name="expand"/>
        /// they also turn to face the viewer if they're round the back, come toward them and grow a little.
        /// <see cref="Unfocus"/> or <see cref="ResetView"/> puts them back.
        /// </summary>
        public void Isolate(IList<Transform> show, Look look, bool expand)
        {
            chosen.Clear();
            foreach (var p in parts)
            {
                p.chosen = false;
                foreach (var t in show) if (t == p.transform) { p.chosen = true; break; }
                if (p.chosen) chosen.Add(p.transform);
            }
            if (chosen.Count == 0) { Clear(); return; }
            Mode = look;
            float grow = chosen.Count == 1 ? 1.3f : chosen.Count <= 3 ? 1.12f : 1f;
            Vector3 toward = Vector3.zero;
            if (expand) toward = Forward(out turnLeft);
            foreach (var p in parts)
            {
                p.liftTarget = p.chosen && expand ? 1f : 0f;
                if (!p.chosen || !expand) continue;
                p.grow = grow;
                p.lift = p.transform.parent != null ? p.transform.parent.InverseTransformVector(toward) : toward;
            }
            moving = true;
            MakeLabels();
            Restyle();
            Relabel();
            ShowLabels();
            SceneLabels();
            Changed?.Invoke();
        }

        // Where the chosen parts come toward: the viewer (or the model's front, +z, with no camera), by a quarter of the
        // model's size. If they're round the far side the model turns them round first, and the lift is worked out for
        // where it ends up.
        Vector3 Forward(out float turn)
        {
            turn = 0f;
            var c = transform.TransformPoint(centre);
            float worldRadius = transform.TransformVector(Vector3.right * radius).magnitude;
            var cam = Camera.main;
            Vector3 dir;
            if (cam != null && (cam.transform.position - c).sqrMagnitude > 1e-6f)
            {
                dir = cam.transform.position - c;
                dir.y *= 0.3f;
                dir.Normalize();
                Vector3 mid = Vector3.zero;
                int n = 0;
                foreach (var p in parts) if (p.chosen && WorldBounds(p, false, out var b)) { mid += b.center; n++; }
                if (n > 0)
                {
                    var from = mid / n - c;
                    Vector3 up = transform.up;
                    Vector3 a = Vector3.ProjectOnPlane(from, up), v = Vector3.ProjectOnPlane(dir, up);
                    if (a.magnitude > radius * 0.15f * transform.lossyScale.x && v.sqrMagnitude > 1e-6f)
                    {
                        float angle = Vector3.SignedAngle(a, v, up);
                        if (Mathf.Abs(angle) > 70f) turn = angle;
                    }
                }
                dir = Quaternion.AngleAxis(-turn, transform.up) * dir;
            }
            else dir = transform.TransformDirection(Vector3.forward);
            return dir * worldRadius * 0.3f;
        }

        /// <summary>The part a hand, ray or mouse is on, lit a little; null for none. Only changes looks when it changes.</summary>
        public void SetHover(Transform part)
        {
            if (part == Hovered) return;
            Hovered = part;
            Restyle();
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
        /// Only once the model is fully apart, or a part brought forward on its own; null otherwise.
        /// </summary>
        public Transform PartAt(Vector3 world, float margin)
        {
            bool apartNow = Separated && apart >= 0.99f;
            if (!apartNow && !Isolating) return null;
            Transform best = null;
            float bestVolume = float.MaxValue;
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (Isolating && (!part.chosen || (!apartNow && part.lifted < 0.99f))) continue;
                if (!WorldBounds(part, true, out var b)) continue;
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
            if (!moving) return;
            float step = Time.deltaTime / MoveSeconds;
            bool still = true;
            if (apart != apartTarget) { apart = Mathf.MoveTowards(apart, apartTarget, step); still = false; }
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.lifted == p.liftTarget) continue;
                p.lifted = Mathf.MoveTowards(p.lifted, p.liftTarget, step);
                still = false;
            }
            if (turnLeft != 0f)
            {
                float d = Mathf.MoveTowards(0f, turnLeft, TurnDegreesPerSecond * Time.deltaTime);
                Turn(d);
                turnLeft -= d;
                still = false;
            }
            Place(apart);
            if (still) moving = false;
        }

        /// <summary>Jumps every glide (apart, brought forward, turning) to where it's going. Checks call it.</summary>
        public void Finish()
        {
            if (turnLeft != 0f) { Turn(turnLeft); turnLeft = 0f; }
            foreach (var p in parts) p.lifted = p.liftTarget;
            Place(apartTarget);
            moving = false;
        }

        /// <summary>Puts every part <paramref name="t"/> of the way apart (0 together, 1 separated). Checks call it directly.</summary>
        public void Place(float t)
        {
            apart = t;
            float eased = Ease(t);
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                // Brought forward: out toward the viewer, and bigger about its own middle (not its node's origin).
                float w = Ease(part.lifted);
                float s = 1f + (part.grow - 1f) * w;
                Vector3 forward = part.lift * w + (part.centre - part.position) * (1f - s);
                if (part.lifted > 0f || part.transform.localScale != part.scale) part.transform.localScale = part.scale * s;
                if (!part.moved)
                {
                    part.transform.localPosition = part.position + part.offset * eased + forward;
                    continue;
                }
                // On its way home from where a hand put it: the drift and the turn fade out with the separation.
                float k = Mathf.Clamp01(eased / homeFrom);
                part.transform.localPosition = part.position + part.offset * eased + part.drift * eased + forward;
                part.transform.localRotation = Quaternion.Slerp(part.rotation, part.heldRotation, k);
                if (t <= 0f)
                {
                    part.moved = false;
                    part.drift = Vector3.zero;
                    part.transform.localRotation = part.rotation;
                }
            }
            ShowLabels();
        }

        /// <summary>Cuts the model open on one plane through its centre, facing the viewer, and keeps the back half.</summary>
        public void Section()
        {
            Sectioned = !Sectioned;
            foreach (var part in parts)
                foreach (var piece in part.pieces)
                {
                    if (Sectioned && piece.section == null) piece.section = Cut(part, piece);
                    piece.filter.sharedMesh = Sectioned ? piece.section : piece.original;
                }
            Changed?.Invoke();
        }

        /// <summary>Large outer parts go see-through so what's inside shows; small inner parts stay solid.</summary>
        public void ToggleXRay()
        {
            Unfocus();
            XRay = !XRay;
            Restyle();
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
            Separated = false;
            SceneLabels();
            apartTarget = 0f;
            turnLeft = 0f;
            foreach (var p in parts) { p.lifted = p.liftTarget = 0f; p.transform.localScale = p.scale; }
            Place(0f);
            transform.localRotation = rotation;
            transform.localScale = scale;
            transform.localPosition = position;
            Changed?.Invoke();
        }

        // ---------------------------------------------------------------- looks: X-ray, isolate, highlight
        // Which materials each piece wears, and how lit it is, from the state. Runs when the state changes, not per frame.
        void Restyle()
        {
            foreach (var part in parts)
            {
                int tint = part.chosen ? 2 : part.transform == Hovered ? 1 : 0;
                foreach (var piece in part.pieces)
                {
                    if (piece.renderer == null || piece.materials == null) continue;
                    Material[] want = piece.materials;
                    if (Isolating && !part.chosen) want = Mode == Look.Isolate ? Faint(piece) : Ghosts(piece);
                    else if (XRay && part.large && !part.chosen) want = Ghosts(piece);
                    if (want == null) want = piece.materials;
                    if (want != piece.shown) { piece.renderer.sharedMaterials = want; piece.shown = want; piece.tint = -1; }
                    if (tint != piece.tint) Tint(piece, tint);
                }
            }
        }

        static MaterialPropertyBlock block;

        // A chosen part (2) or the one being pointed at (1) is lifted toward the theme's pale blue and a little brighter.
        static void Tint(Piece piece, int tint)
        {
            piece.tint = tint;
            if (block == null) block = new MaterialPropertyBlock();
            var mats = piece.shown;
            for (int i = 0; i < mats.Length; i++)
            {
                // Nothing to lift: no block at all, so the renderer keeps batching as before.
                if (tint <= 0 || mats[i] == null) { piece.renderer.SetPropertyBlock(null, i); continue; }
                block.Clear();
                if (tint > 0 && mats[i] != null)
                {
                    var c = mats[i].HasProperty(BaseColorFactor) ? mats[i].GetColor(BaseColorFactor)
                        : mats[i].HasProperty(BaseColor) ? mats[i].GetColor(BaseColor) : Color.white;
                    float k = tint == 2 ? 0.3f : 0.16f;
                    var lifted = Color.Lerp(c, Lift, k) * (1f + 0.5f * k);
                    lifted.a = Mathf.Min(1f, c.a + (tint == 1 ? 0.12f : 0f));
                    block.SetColor(BaseColorFactor, lifted);
                    block.SetColor(BaseColor, lifted);
                }
                piece.renderer.SetPropertyBlock(block, i);
            }
        }

        Material[] Ghosts(Piece piece)
        {
            if (piece.ghosts == null) piece.ghosts = Ghosts(piece.materials);
            return piece.ghosts;
        }

        static Material faintShared;

        // One shared, nearly clear material for every part that isn't shown: the whole model's outline, for context.
        static Material[] Faint(Piece piece)
        {
            if (piece.faint != null) return piece.faint;
            if (faintShared == null)
            {
                var template = ArtifactLook.XRay();
                if (template == null) return null;
                faintShared = new Material(template) { name = "part silhouette" };
                var c = Lift;
                c.a = FaintAlpha;
                faintShared.SetColor(BaseColor, c);
            }
            piece.faint = new Material[piece.materials.Length];
            for (int i = 0; i < piece.faint.Length; i++) piece.faint[i] = faintShared;
            return piece.faint;
        }

        // ---------------------------------------------------------------- picking: what a ray is on
        /// <summary>
        /// Gives every part a picking collider (its mesh, on the Ignore Raycast layer, so nothing else's raycasts hit it),
        /// so <see cref="PartOnRay"/> knows which part a hand, ray or the eyes are on. In play mode
        /// <see cref="ArtifactPartPointer"/> spreads this over frames; checks call it directly.
        /// </summary>
        public bool AddPickers(int most = int.MaxValue)
        {
            int made = 0;
            foreach (var part in parts)
                foreach (var piece in part.pieces)
                {
                    if (piece.pick != null || piece.original == null) continue;
                    if (made++ >= most) return false;
                    var go = new GameObject("pick");
                    go.layer = PickLayer;
                    go.transform.SetParent(piece.filter.transform, false);
                    piece.pick = go.AddComponent<MeshCollider>();
                    piece.pick.sharedMesh = piece.original;
                    byCollider[piece.pick] = part;
                }
            return true;
        }

        /// <summary>
        /// The part <paramref name="ray"/> meets first, within <paramref name="distance"/>: a solid part before a
        /// see-through one, never one faded out by <see cref="Isolate"/> or in the half cut away. Null for none.
        /// <paramref name="hits"/> is the caller's buffer, so nothing is allocated.
        /// </summary>
        public Transform PartOnRay(Ray ray, float distance, RaycastHit[] hits)
        {
            if (byCollider.Count == 0) return null;
            int n = Physics.RaycastNonAlloc(ray, hits, distance, 1 << PickLayer, QueryTriggerInteraction.Collide);
            Part solid = null, clear = null;
            float solidAt = float.MaxValue, clearAt = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (hits[i].collider == null || !byCollider.TryGetValue(hits[i].collider, out var part)) continue;
                if (Isolating && Mode == Look.Isolate && !part.chosen) continue;
                if (Sectioned && transform.InverseTransformPoint(hits[i].point).z > centre.z) continue;
                bool seeThrough = (Isolating && !part.chosen) || (XRay && part.large && !part.chosen);
                if (seeThrough) { if (hits[i].distance < clearAt) { clearAt = hits[i].distance; clear = part; } }
                else if (hits[i].distance < solidAt) { solidAt = hits[i].distance; solid = part; }
            }
            var best = solid ?? clear;
            return best != null ? best.transform : null;
        }

        // ---------------------------------------------------------------- cut
        Mesh Cut(Part part, Piece piece)
        {
            // One plane for the whole model: through its centre, normal along the model's local +Z (toward the viewer
            // in the templates). Each piece is tested in model space with its part assembled, so the cut lines up.
            var mesh = Instantiate(piece.original);
            mesh.name = piece.original.name + " section";
            var toModel = transform.worldToLocalMatrix * part.transform.parent.localToWorldMatrix *
                          Matrix4x4.TRS(part.position, part.rotation, part.scale) *
                          part.transform.worldToLocalMatrix * piece.filter.transform.localToWorldMatrix;
            var vertices = piece.original.vertices;
            var front = new bool[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) front[i] = toModel.MultiplyPoint3x4(vertices[i]).z > centre.z;
            var kept = new List<int>();
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                var indices = piece.original.GetTriangles(sub);
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
            var partCentre = WorldBounds(part, false, out var b) ? Local(b).center : part.position;
            var dir = partCentre - centre;
            if (dir.sqrMagnitude < 1e-10f) dir = Vector3.up;
            var world = transform.TransformVector(dir.normalized * radius * 0.35f);
            return part.transform.parent.InverseTransformVector(world);
        }

        // A part's box in world space, over its pieces (shown ones only, if asked). No allocation.
        static bool WorldBounds(Part part, bool shownOnly, out Bounds b)
        {
            b = default;
            bool any = false;
            var pieces = part.pieces;
            for (int i = 0; i < pieces.Length; i++)
            {
                var r = pieces[i].renderer;
                if (r == null || (shownOnly && (!r.enabled || !r.gameObject.activeInHierarchy))) continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return any;
        }

        // Names of separated parts, set out as callouts: two columns beside the model as the viewer sees it, each
        // sorted by the height of its parts and spaced a line apart, with a leader line to each part. Laid out every
        // frame they show, so they stay readable while the parts move and as the viewer walks round. A part shown on
        // its own also says what it does, on a second line.
        const float LabelSize = 0.13f;      // TMP font size at world scale 1: about 1.3 cm tall
        const float LineSpacing = 0.021f;   // between label rows, metres
        const float ColumnGap = 0.02f;      // from the model's edge on that side to its column
        static readonly Color LeaderColour = new Color(0.82f, 0.83f, 0.86f, 0.85f);
        Part[] labelled = new Part[0], column = new Part[0];
        Transform labelRoot;
        bool anyLabel;

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
                if (!WorldBounds(part, false, out _)) continue;
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
                go.SetActive(false);
                list.Add(part);
            }
            labelled = list.ToArray();
            column = new Part[labelled.Length];
            Relabel();
            ShowLabels();
        }

        // A chosen part's label says what it does too; the others just their names.
        void Relabel()
        {
            for (int i = 0; i < labelled.Length; i++)
            {
                var p = labelled[i];
                string name = DisplayName(p.transform.name);
                string about = p.chosen ? About(p.transform) : null;
                string text = about != null ? name + "\n<size=70%><color=#C9D3E6>" + about + "</color></size>" : name;
                if (p.label.text == text) continue;
                p.label.text = text;
                p.label.color = p.chosen ? Lift : Color.white;
                p.lines = about != null ? 2 : 1;
                p.width = p.label.GetPreferredValues(text).x * p.label.transform.lossyScale.x;
                p.side = 0;
            }
        }

        // Names show once the parts are well apart, or, while parts are shown on their own, just theirs.
        void ShowLabels()
        {
            bool apartNow = apart > 0.6f;
            anyLabel = false;
            for (int i = 0; i < labelled.Length; i++)
            {
                var p = labelled[i];
                bool on = Isolating ? p.chosen : apartNow;
                anyLabel |= on;
                if (p.labelOn == on) continue;
                p.labelOn = on;
                p.label.gameObject.SetActive(on);
            }
        }

        void LateUpdate()
        {
            if (!anyLabel) return;
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
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (!WorldBounds(p, true, out var b)) continue;
                if (Isolating && Mode == Look.Isolate && !p.chosen) continue;
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
                if (!p.labelOn) { p.column = 0; continue; }
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
                column[i].slot = i == 0 ? column[i].height
                    : Mathf.Min(column[i].height, column[i - 1].slot - LineSpacing * (column[i - 1].lines + column[i].lines) * 0.5f);
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
                if (WorldBounds(p, false, out var b)) p.leader.SetPosition(1, b.center);
            }
        }

        static Material[] Ghosts(Material[] source)
        {
            var template = ArtifactLook.XRay();
            if (template == null || source == null) return null;
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
                foreach (var piece in part.pieces)
                {
                    Free(piece.section);
                    if (piece.ghosts != null) foreach (var g in piece.ghosts) Free(g);
                }
        }

        static void Free(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        // ---------------------------------------------------------------- words, from the template
        static readonly Dictionary<string, PartWords> words = new Dictionary<string, PartWords>();

        /// <summary>
        /// What a template's parts are called when people talk (<c>Resources/ArtifactTemplates/&lt;template&gt;/parts.json</c>):
        /// other names, groups, one-line descriptions. Null for a template without one (and for imported models).
        /// </summary>
        public static PartWords WordsFor(string template)
        {
            if (string.IsNullOrEmpty(template)) return null;
            if (words.TryGetValue(template, out var cached)) return cached;
            PartWords w = null;
            var asset = Resources.Load<TextAsset>($"ArtifactTemplates/{template}/parts");
            if (asset != null)
            {
                try { w = ParseWords(JObject.Parse(asset.text)); }
                catch (System.Exception e) { Debug.LogWarning($"[Artifacts] {template}/parts.json: {e.Message}"); }
            }
            words[template] = w;
            return w;
        }

        public static PartWords ParseWords(JObject json)
        {
            var w = new PartWords { Bilateral = json.Value<bool?>("bilateral") == true };
            if (json["ignore"] is JArray ignore) foreach (var i in ignore) w.AddIgnore((string)i);
            if (json["parts"] is JObject entries)
                foreach (var p in entries.Properties())
                {
                    var aka = new List<string>();
                    if (p.Value["aka"] is JArray a) foreach (var s in a) aka.Add((string)s);
                    w.AddPart(p.Name, p.Value.Value<string>("about"), aka);
                }
            if (json["groups"] is JArray groups)
                foreach (var g in groups)
                {
                    var ws = new List<string>();
                    var ps = new List<string>();
                    if (g["words"] is JArray gw) foreach (var s in gw) ws.Add((string)s);
                    if (g["parts"] is JArray gp) foreach (var s in gp) ps.Add((string)s);
                    w.AddGroup(ws, ps);
                }
            return w;
        }
    }
}
