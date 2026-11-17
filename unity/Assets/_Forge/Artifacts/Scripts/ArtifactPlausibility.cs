using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Forge.Artifacts
{
    /// <summary>
    /// Whether a written artifact makes physical sense, checked before it's saved (plan B1 to B4). The validator says the
    /// JSON is legal; this says the thing it describes holds together: its parts sit where they were meant to (not all at
    /// one point because "position" was written for "pos"), they're big enough to see, they touch, they fit the view, every
    /// slider moves something, and what moves turns about a point that is held.
    ///
    /// It runs only from <see cref="ArtifactLibrary.FromScene"/>, once per <c>artifact_create</c>, never at open: templates,
    /// sandboxes and saved artifacts open exactly as before. It reads JSON only, with its own double-precision maths, so
    /// it needs no Unity scene and no Unity maths. It never guesses room positions: a node on a generator slot (<c>at</c>)
    /// and everything under it are skipped, because the generator owns where they go.
    ///
    /// Errors refuse the save. Warnings are saved and returned to the agent as <c>checks</c>: the moving-part checks
    /// (pivot_unsupported, moving_part_collides, motion_invisible), would_tip, and part_floating in a scene with a view,
    /// whose thresholds are not tuned on enough real output yet to refuse with. <see cref="Result.Facts"/> is a short plain
    /// description of what was measured, so the agent can say what it built and fix what's off.
    /// </summary>
    public static class ArtifactPlausibility
    {
        // ---------- thresholds (metres unless said) ----------

        /// <summary>Two parts touch when the gap between them is at most this, or <see cref="ContactShare"/> of the smaller.</summary>
        public const double Contact = 0.003, ContactShare = 0.05;
        /// <summary>Parts whose centres are this close count as at one point.</summary>
        public const double SameCentre = 0.001;
        /// <summary>A part under this, or under 1/<see cref="SmallShare"/> of the scene, can't be seen once it is fitted.</summary>
        public const double Smallest = 0.004, SmallShare = 250;
        /// <summary>A panel scene is shown at true size beside its panel.</summary>
        public const double PanelMax = 0.8;
        public const double RoomMin = 0.05, RoomMax = 8;
        /// <summary>How far past view.radius a part may reach.</summary>
        public const double ViewSlack = 1.15;
        /// <summary>A control that moves nothing more than this (or 1% of the scene) changes nothing you can see.</summary>
        public const double SeenMove = 0.005, SeenShare = 0.01;
        /// <summary>Solver steps all sim sweeps may take together, per check.</summary>
        public const int MaxSimSteps = 20000;
        const int Samples = 9;
        const double Eps = 1e-6;

        public sealed class Result
        {
            public readonly List<ArtifactError> Errors = new List<ArtifactError>();
            public readonly List<ArtifactError> Warnings = new List<ArtifactError>();
            /// <summary>What was measured, in a few plain lines. Null when there was nothing solid to measure.</summary>
            public string Facts;
        }

        public static Result Check(JObject manifest, JObject scene)
        {
            var result = new Result();
            if (manifest == null || scene == null) return result;
            try
            {
                new Pass(manifest, scene, result).Run();
            }
            catch (Exception e)
            {
                // A check that can't read the scene never blocks a save the validator allowed: it says so instead.
                result.Errors.Clear();
                result.Warnings.Add(new ArtifactError("not_checked", "scene", "its physical checks couldn't run: " + e.Message));
            }
            return result;
        }

        // ---------- double-precision maths: Unity's Quaternion.Euler order (Z, then X, then Y) ----------

        public struct V
        {
            public double x, y, z;
            public V(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
            public static V operator +(V a, V b) => new V(a.x + b.x, a.y + b.y, a.z + b.z);
            public static V operator -(V a, V b) => new V(a.x - b.x, a.y - b.y, a.z - b.z);
            public static V operator *(V a, double s) => new V(a.x * s, a.y * s, a.z * s);
            public double this[int i] => i == 0 ? x : i == 1 ? y : z;
            public double Length => Math.Sqrt(x * x + y * y + z * z);
            public static double Dot(V a, V b) => a.x * b.x + a.y * b.y + a.z * b.z;
            public static V Cross(V a, V b) => new V(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
            public V Normalized { get { double l = Length; return l < 1e-12 ? new V(0, 0, 0) : this * (1.0 / l); } }
        }

        /// <summary>A 3x3 linear map, row-major.</summary>
        public struct M
        {
            public double a, b, c, d, e, f, g, h, i;
            public static readonly M Identity = new M { a = 1, e = 1, i = 1 };
            public static M operator *(M p, M q) => new M
            {
                a = p.a * q.a + p.b * q.d + p.c * q.g, b = p.a * q.b + p.b * q.e + p.c * q.h, c = p.a * q.c + p.b * q.f + p.c * q.i,
                d = p.d * q.a + p.e * q.d + p.f * q.g, e = p.d * q.b + p.e * q.e + p.f * q.h, f = p.d * q.c + p.e * q.f + p.f * q.i,
                g = p.g * q.a + p.h * q.d + p.i * q.g, h = p.g * q.b + p.h * q.e + p.i * q.h, i = p.g * q.c + p.h * q.f + p.i * q.i,
            };
            public static V operator *(M m, V v) => new V(m.a * v.x + m.b * v.y + m.c * v.z, m.d * v.x + m.e * v.y + m.f * v.z, m.g * v.x + m.h * v.y + m.i * v.z);
            public V Column(int k) => k == 0 ? new V(a, d, g) : k == 1 ? new V(b, e, h) : new V(c, f, i);
            public double Det => a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
            public static M Diagonal(V s) => new M { a = s.x, e = s.y, i = s.z };

            public static M Euler(V degrees)
            {
                double x = degrees.x * Math.PI / 180, y = degrees.y * Math.PI / 180, z = degrees.z * Math.PI / 180;
                double cx = Math.Cos(x), sx = Math.Sin(x), cy = Math.Cos(y), sy = Math.Sin(y), cz = Math.Cos(z), sz = Math.Sin(z);
                var rx = new M { a = 1, e = cx, f = -sx, h = sx, i = cx };
                var ry = new M { a = cy, c = sy, e = 1, g = -sy, i = cy };
                var rz = new M { a = cz, b = -sz, d = sz, e = cz, i = 1 };
                return ry * (rx * rz);
            }

            /// <summary>The rotation part, by normalising the columns (exact for rotation times a per-axis scale).</summary>
            public M Rotation()
            {
                V x = Column(0).Normalized, y = Column(1).Normalized, z = Column(2).Normalized;
                return new M { a = x.x, b = y.x, c = z.x, d = x.y, e = y.y, f = z.y, g = x.z, h = y.z, i = z.z };
            }

            public M Transposed => new M { a = a, b = d, c = g, d = b, e = e, f = h, g = c, h = f, i = i };
        }

        /// <summary>Where a node is: world point = A * local + t, in the scene's own space (the artifact's root).</summary>
        public struct Frame
        {
            public M A;
            public V t;
            public static readonly Frame Root = new Frame { A = M.Identity };
            public V Point(V local) => A * local + t;
        }

        // ---------- one check ----------

        sealed class Node
        {
            public int index;
            public string id, kind, parent;
            public JObject json;
            public bool hidden, skipped, simBody, holds;  // hidden: it or an ancestor isn't visible. skipped: on a slot, or under a sim body
        }

        /// <summary>A visible solid primitive, measured.</summary>
        sealed class Part
        {
            public Node node;
            public string shape;
            public V half;              // half of size, in the node's own axes (a plane has no thickness)
            public Frame frame;
            public V lo, hi, centre;    // the scene-space box around its corners
            public V[] corners;
            public double MaxExtent => Math.Max(hi.x - lo.x, Math.Max(hi.y - lo.y, hi.z - lo.z));
            public double MinExtent => Math.Min(hi.x - lo.x, Math.Min(hi.y - lo.y, hi.z - lo.z));
            /// <summary>Its thinnest real dimension, in its own axes.</summary>
            public double Thin
            {
                get
                {
                    double t = double.MaxValue;
                    for (int k = 0; k < 3; k++)
                    {
                        double len = frame.A.Column(k).Length * half[k] * 2;
                        if (len > Eps) t = Math.Min(t, len);
                    }
                    return t == double.MaxValue ? 0 : t;
                }
            }
            public double Volume => Math.Abs(frame.A.Det) * 8 * Math.Max(half.x, Eps) * Math.Max(half.y, Eps) * Math.Max(half.z, Eps);
        }

        /// <summary>One way of evaluating the scene: input values, plus animate values standing in for a node's own.</summary>
        sealed class Pose
        {
            public ArtifactInputs inputs;
            public Dictionary<string, Dictionary<string, JToken>> over;
            public string label;  // "$tilt=15", "t=1.5 s"
        }

        sealed class Pass
        {
            readonly JObject manifest, scene;
            readonly Result result;
            readonly List<Node> nodes = new List<Node>();
            readonly Dictionary<string, Node> byId = new Dictionary<string, Node>();
            readonly JArray rules;
            readonly string space;
            readonly double viewRadius;
            readonly bool grounded;
            readonly Pose rest;
            readonly StringBuilder facts = new StringBuilder();
            int simSteps;

            List<Part> parts;
            double extent;
            readonly HashSet<string> movingRoots = new HashSet<string>();
            readonly Dictionary<string, string> rootOf = new Dictionary<string, string>();  // part id -> the moving root it rides on
            readonly HashSet<string> lineEnds = new HashSet<string>();                       // nodes a line is drawn through or follows
            readonly Dictionary<string, HashSet<string>> linked = new Dictionary<string, HashSet<string>>();  // joined by a line
            HashSet<string> measurable;                                                      // parts at rest and their ancestors

            public Pass(JObject manifest, JObject scene, Result result)
            {
                this.manifest = manifest;
                this.scene = scene;
                this.result = result;
                rules = scene["rules"] as JArray ?? new JArray();
                space = Str(scene, "space");
                var radius = scene["view"] is JObject view ? view["radius"] : null;
                viewRadius = radius != null && (radius.Type == JTokenType.Float || radius.Type == JTokenType.Integer) ? (double)radius : 0;
                grounded = space == "panel" || space == "room" || (space == "volume" && viewRadius <= 0);
                rest = new Pose { inputs = Inputs(), label = "at rest" };
            }

            ArtifactInputs Inputs()
            {
                try { return new ArtifactInputs(manifest); }
                catch (Exception) { return new ArtifactInputs(new JObject()); }  // a bad input list is the validator's to report
            }

            void Error(string code, string path, string message, string hint) => result.Errors.Add(new ArtifactError(code, path, message, hint));
            void Warn(string code, string path, string message, string hint) => result.Warnings.Add(new ArtifactError(code, path, message, hint));
            void Flag(bool error, string code, string path, string message, string hint)
            {
                if (error) Error(code, path, message, hint); else Warn(code, path, message, hint);
            }

            public void Run()
            {
                ReadNodes();
                SizeMissing();
                FindMoving();
                parts = Parts(rest);
                Describe();
                if (parts.Count > 0)
                {
                    extent = Extent(parts);
                    Collapsed();
                    Coincident();
                    TooSmall();
                    TooBig();
                    Floating();
                    WouldTip();
                }
                OutsideView();
                Controls();
                MovingParts();
                result.Facts = facts.Length > 0 ? facts.ToString().TrimEnd() : null;
            }

            // ---------- reading ----------

            void ReadNodes()
            {
                if (!(scene["nodes"] is JArray list)) return;
                for (int i = 0; i < list.Count; i++)
                {
                    if (!(list[i] is JObject n)) continue;
                    string id = Str(n, "id");
                    if (id == null || byId.ContainsKey(id)) continue;
                    var node = new Node { index = i, id = id, kind = Str(n, "kind"), parent = Str(n, "parent"), json = n };
                    nodes.Add(node);
                    byId[id] = node;
                }
                foreach (var n in nodes)
                    if (n.parent != null && byId.TryGetValue(n.parent, out var holder)) holder.holds = true;
                var bodies = new HashSet<string>();
                foreach (var n in nodes)
                    if (n.kind == "sim" && n.json["bodies"] is JArray list2)
                        foreach (var b in list2)
                            if (b is JObject body && Str(body, "node") is string id) bodies.Add(id);
                foreach (var n in nodes)
                {
                    int guard = 0;
                    for (var a = n; a != null && guard < 64; a = a.parent != null && byId.TryGetValue(a.parent, out var p) ? p : null, guard++)
                    {
                        if (a.json["visible"] is JValue v && v.Type == JTokenType.Boolean && !(bool)v) n.hidden = true;
                        if (a.json["at"] != null && a.json["at"].Type != JTokenType.Null) n.skipped = true;
                        if (bodies.Contains(a.id)) { n.simBody = true; n.skipped = true; }
                    }
                    if (guard >= 64) n.skipped = true;  // a parent loop: the validator says so
                }
                // A line drawn through parts joins them (a rope, a rod, a bond), and the small parts it is drawn from or that a
                // trail follows are anchors on purpose, like a hinge speck that holds children.
                foreach (var n in nodes)
                {
                    if (n.kind != "line") continue;
                    if (Str(n.json, "follow") is string followed) lineEnds.Add(followed);
                    if (!(n.json["through"] is JArray through)) continue;
                    string before = null;
                    foreach (var t in through)
                    {
                        string id = t.Type == JTokenType.String ? (string)t : null;
                        if (id == null) { before = null; continue; }
                        lineEnds.Add(id);
                        if (before != null && before != id)
                        {
                            Link(before, id);
                            Link(id, before);
                        }
                        before = id;
                    }
                }
            }

            void Link(string a, string b)
            {
                if (!linked.TryGetValue(a, out var set)) linked[a] = set = new HashSet<string>();
                set.Add(b);
            }

            bool Linked(Part a, Part b) => linked.TryGetValue(a.node.id, out var set) && set.Contains(b.node.id);

            /// <summary>Whether a node, or something under it, is a part <see cref="Parts"/> measures: only then is a change
            /// to its pos, rot or scale something the sweep can see.</summary>
            bool HoldsPart(Node n)
            {
                if (measurable == null)
                {
                    measurable = new HashSet<string>();
                    foreach (var p in parts)
                    {
                        int guard = 0;
                        for (var a = p.node; a != null && guard < 64; a = a.parent != null && byId.TryGetValue(a.parent, out var up) ? up : null, guard++)
                            measurable.Add(a.id);
                    }
                }
                return measurable.Contains(n.id);
            }

            /// <summary>Animate rules and $input pos/rot/scale make a node a moving root; its children ride on it.</summary>
            void FindMoving()
            {
                foreach (var n in nodes)
                {
                    if (n.kind == "panel" || n.kind == "timeline" || n.skipped) continue;
                    if (ArtifactInputs.IsBound(n.json["pos"]) || ArtifactInputs.IsBound(n.json["rot"]) || ArtifactInputs.IsBound(n.json["scale"]))
                        movingRoots.Add(n.id);
                }
                foreach (var r in rules)
                    if (r is JObject rule && Str(rule, "type") == "animate" && Str(rule, "target") is string target && byId.ContainsKey(target))
                    {
                        string property = Str(rule, "property");
                        if (property == "pos" || property == "rot" || property == "scale") movingRoots.Add(target);
                    }
            }

            // ---------- geometry ----------

            Frame FrameOf(Node n, Pose pose, Dictionary<string, Frame> memo, int depth = 0)
            {
                if (memo.TryGetValue(n.id, out var known)) return known;
                var parentFrame = Frame.Root;
                if (depth < 64 && n.parent != null && byId.TryGetValue(n.parent, out var p) && p.kind != "panel")
                    parentFrame = FrameOf(p, pose, memo, depth + 1);
                var pos = Vec(Field(n, "pos", pose), new V(0, 0, 0), pose.inputs);
                var rot = Vec(Field(n, "rot", pose), new V(0, 0, 0), pose.inputs);
                var scaleToken = Field(n, "scale", pose);
                var scale = IsNumber(scaleToken) ? new V((double)scaleToken, (double)scaleToken, (double)scaleToken) : Vec(scaleToken, new V(1, 1, 1), pose.inputs);
                var frame = new Frame
                {
                    A = parentFrame.A * (M.Euler(rot) * M.Diagonal(scale)),
                    t = parentFrame.A * pos + parentFrame.t,
                };
                memo[n.id] = frame;
                return frame;
            }

            static JToken Field(Node n, string field, Pose pose)
            {
                if (pose.over != null && pose.over.TryGetValue(n.id, out var fields) && fields.TryGetValue(field, out var value)) return value;
                return n.json[field];
            }

            List<Part> Parts(Pose pose)
            {
                var memo = new Dictionary<string, Frame>();
                var list = new List<Part>();
                foreach (var n in nodes)
                {
                    if (n.kind != "primitive" || n.hidden || n.skipped) continue;
                    string shape = Str(n.json, "shape");
                    if (shape == null || !ArtifactSchema.Shapes.Contains(shape)) continue;
                    var size = Vec(n.json["size"], new V(0.1, 0.1, 0.1), pose.inputs);
                    // A speck that only holds children is a hinge or a handle (there's no group kind): not a part you see.
                    if (n.holds && Math.Max(Math.Abs(size.x), Math.Max(Math.Abs(size.y), Math.Abs(size.z))) < Smallest) continue;
                    var half = new V(Math.Abs(size.x) / 2, shape == "plane" ? 0 : Math.Abs(size.y) / 2, Math.Abs(size.z) / 2);
                    list.Add(Measure(n, shape, half, FrameOf(n, pose, memo)));
                }
                return list;
            }

            static Part Measure(Node n, string shape, V half, Frame frame)
            {
                var part = new Part { node = n, shape = shape, half = half, frame = frame, centre = frame.t, corners = new V[8] };
                int k = 0;
                for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    part.corners[k++] = frame.Point(new V(sx * half.x, sy * half.y, sz * half.z));
                part.lo = part.hi = part.corners[0];
                foreach (var c in part.corners)
                {
                    part.lo = new V(Math.Min(part.lo.x, c.x), Math.Min(part.lo.y, c.y), Math.Min(part.lo.z, c.z));
                    part.hi = new V(Math.Max(part.hi.x, c.x), Math.Max(part.hi.y, c.y), Math.Max(part.hi.z, c.z));
                }
                return part;
            }

            static bool Round(string shape) => shape == "sphere" || shape == "cylinder" || shape == "cone" || shape == "capsule";

            /// <summary>Points on the real surface: a round shape's rims (its box corners aren't part of it), a box's corners.</summary>
            static IEnumerable<V> Outline(Part p)
            {
                if (!Round(p.shape))
                {
                    foreach (var c in p.corners) yield return c;
                    yield break;
                }
                for (int k = 0; k < 32; k++)
                {
                    double a = 2 * Math.PI * k / 32, s = Math.Sin(a), c = Math.Cos(a);
                    if (p.shape == "sphere")
                    {
                        yield return p.frame.Point(new V(s * p.half.x, c * p.half.y, 0));
                        yield return p.frame.Point(new V(0, s * p.half.y, c * p.half.z));
                        yield return p.frame.Point(new V(s * p.half.x, 0, c * p.half.z));
                    }
                    else
                    {
                        double top = p.shape == "capsule" ? p.half.y - Math.Min(p.half.x, p.half.z) : p.half.y;
                        yield return p.frame.Point(new V(s * p.half.x, top, c * p.half.z));
                        yield return p.frame.Point(new V(s * p.half.x, -top, c * p.half.z));
                    }
                }
                if (p.shape == "capsule")
                {
                    yield return p.frame.Point(new V(0, p.half.y, 0));
                    yield return p.frame.Point(new V(0, -p.half.y, 0));
                }
            }

            static double Reach(Part p)
            {
                double far = 0;
                foreach (var q in Outline(p)) far = Math.Max(far, q.Length);
                return far;
            }

            static double Extent(List<Part> list)
            {
                V lo = list[0].lo, hi = list[0].hi;
                foreach (var p in list)
                {
                    lo = new V(Math.Min(lo.x, p.lo.x), Math.Min(lo.y, p.lo.y), Math.Min(lo.z, p.lo.z));
                    hi = new V(Math.Max(hi.x, p.hi.x), Math.Max(hi.y, p.hi.y), Math.Max(hi.z, p.hi.z));
                }
                return Math.Max(hi.x - lo.x, Math.Max(hi.y - lo.y, hi.z - lo.z));
            }

            /// <summary>The largest per-axis separation of two boxes: positive is a gap, negative an overlap.</summary>
            static double Gap(Part a, Part b, out V move)
            {
                double best = double.NegativeInfinity;
                move = new V(0, 0, 0);
                double[] m = new double[3];
                for (int k = 0; k < 3; k++)
                {
                    double ab = a.lo[k] - b.hi[k], ba = b.lo[k] - a.hi[k];
                    double g = Math.Max(ab, ba);
                    if (g > 0) m[k] = ab > ba ? -ab : ba;  // how far a moves along k to touch b
                    best = Math.Max(best, g);
                }
                move = new V(m[0], m[1], m[2]);
                return best;
            }

            static double Tolerance(Part a, Part b) => Math.Max(Contact, ContactShare * Math.Min(a.MaxExtent, b.MaxExtent)) + Eps;

            static bool Touch(Part a, Part b) => Gap(a, b, out _) <= Tolerance(a, b);

            // ---------- B1: what the parts are ----------

            void SizeMissing()
            {
                foreach (var n in nodes)
                {
                    if (n.kind != "primitive" || n.json["size"] != null) continue;
                    var scaleToken = n.json["scale"];
                    if (scaleToken == null || scaleToken.Type == JTokenType.Null) continue;
                    var scale = IsNumber(scaleToken) ? new V((double)scaleToken, (double)scaleToken, (double)scaleToken) : Vec(scaleToken, new V(1, 1, 1), rest.inputs);
                    if (Math.Abs(scale.x - 1) < Eps && Math.Abs(scale.y - 1) < Eps && Math.Abs(scale.z - 1) < Eps && !ArtifactInputs.IsBound(scaleToken)) continue;
                    var made = scale * 0.1;
                    Error("size_missing", $"nodes[{n.index}].size",
                        $"{n.id} has no size, so scale {Show(scale)} makes it {Show(made)} m (size defaults to 0.1 m)",
                        $"size is in metres: if {Show(scale)} was the size you meant, write \"size\": {Show(scale)} and leave scale out");
                }
            }

            void Collapsed()
            {
                if (parts.Count < 3) return;
                var groups = new List<List<Part>>();
                foreach (var p in parts)
                {
                    List<Part> into = null;
                    foreach (var g in groups)
                        if ((g[0].centre - p.centre).Length <= SameCentre) { into = g; break; }
                    if (into == null) groups.Add(into = new List<Part>());
                    into.Add(p);
                }
                foreach (var g in groups)
                {
                    // Only parts that never said where they go: concentric parts placed on purpose (a target's rings) are fine.
                    // A child with no pos sits on its parent's centre on purpose (a bulb in its lamp), so only top-level parts count.
                    var unplaced = new List<Part>();
                    foreach (var p in g) if (p.node.json["pos"] == null && p.node.parent == null) unplaced.Add(p);
                    // Parts that nest one inside another (a target's rings, a cell's shells) share a centre by design; a
                    // collapse is parts that cut through each other or are alike, which is what a forgotten pos gives.
                    var ids = new List<string>();
                    foreach (var p in unplaced)
                        foreach (var q in unplaced)
                            if (p != q && !Nested(p, q) && !Nested(q, p)) { ids.Add(p.node.id); break; }
                    if (ids.Count < Math.Max(3, (parts.Count + 1) / 2)) continue;
                    Error("parts_collapsed", "nodes",
                        $"{ids.Count} of {parts.Count} parts ({Join(ids, 6)}) have no pos, so they all sit at one point {Show(g[0].centre)} and cut through each other",
                        "give each part its own \"pos\": [x, y, z] in metres (fields are pos, rot, size: not position, rotation, scale); if they share that centre on purpose, write \"pos\": [0, 0, 0] on each");
                }
            }

            /// <summary>Whether <paramref name="inner"/>'s box fits in <paramref name="outer"/>'s on every axis (allowing a
            /// little extra thickness) and <paramref name="outer"/> is clearly the bigger.</summary>
            static bool Nested(Part outer, Part inner)
            {
                if (outer.MaxExtent <= 1.1 * inner.MaxExtent) return false;
                for (int k = 0; k < 3; k++)
                {
                    double eo = outer.hi[k] - outer.lo[k], ei = inner.hi[k] - inner.lo[k];
                    if (ei > eo + Math.Max(0.005, 0.25 * ei)) return false;
                }
                return true;
            }

            /// <summary>Whether two parts are the same solid: the same shape with the same oriented corners (a box turned
            /// +45 and one turned -45 degrees share a bounding box but make an X), or two equal round spheres.</summary>
            static bool SameSolid(Part a, Part b)
            {
                if (a.shape != b.shape) return false;
                double tol = Math.Max(0.001, 0.02 * Math.Max(a.MaxExtent, b.MaxExtent));
                if (a.shape == "sphere" && Ball(a, out double ra) && Ball(b, out double rb)) return Math.Abs(ra - rb) <= tol;
                foreach (var c in a.corners)
                {
                    bool match = false;
                    foreach (var d in b.corners)
                        if ((c - d).Length <= tol) { match = true; break; }
                    if (!match) return false;
                }
                return true;
            }

            /// <summary>A sphere that is round in the scene (any turn of it looks the same), and its radius.</summary>
            static bool Ball(Part p, out double radius)
            {
                double x = p.frame.A.Column(0).Length * p.half.x, y = p.frame.A.Column(1).Length * p.half.y, z = p.frame.A.Column(2).Length * p.half.z;
                radius = x;
                double tol = Math.Max(Eps, 0.01 * x);
                return Math.Abs(x - y) <= tol && Math.Abs(x - z) <= tol;
            }

            void Coincident()
            {
                int reported = 0;
                for (int i = 0; i < parts.Count && reported < 6; i++)
                for (int j = i + 1; j < parts.Count && reported < 6; j++)
                {
                    Part a = parts[i], b = parts[j];
                    double near = Math.Max(SameCentre, 0.05 * Math.Min(a.MinExtent, b.MinExtent));
                    if ((a.centre - b.centre).Length > near) continue;
                    if (!SameSolid(a, b) || (Enclosed(a) && Enclosed(b))) continue;  // both inside a bigger part: neither is seen
                    reported++;
                    Error("parts_coincident", $"nodes[{b.node.index}]",
                        $"{a.node.id} and {b.node.id} are the same shape in the same place {Show(a.centre)}, so only one of them shows",
                        $"move {b.node.id} with its own pos, or delete it; to switch between looks, give the others \"visible\": false and show them from an on_input rule");
                }
            }

            /// <summary>Whether a part sits wholly inside another, bigger part's box, so it can't be seen.</summary>
            bool Enclosed(Part p)
            {
                foreach (var q in parts)
                {
                    if (q == p || q.MaxExtent <= p.MaxExtent * 1.05) continue;
                    bool inside = true;
                    for (int k = 0; k < 3 && inside; k++) inside = p.lo[k] >= q.lo[k] - Eps && p.hi[k] <= q.hi[k] + Eps;
                    if (inside) return true;
                }
                return false;
            }

            void TooSmall()
            {
                double limit = Math.Max(Smallest, extent / SmallShare);
                foreach (var p in parts)
                {
                    if (p.shape == "plane" || p.MaxExtent >= limit || lineEnds.Contains(p.node.id)) continue;  // a line's anchor
                    Error("part_too_small", $"nodes[{p.node.index}].size",
                        $"{p.node.id} is {Mm(p.MaxExtent)} across in a scene {M2(extent)} m across, too small to see",
                        $"size is in metres: make {p.node.id} at least {Mm(limit)} across");
                }
            }

            void TooBig()
            {
                if (space == "panel" && extent > PanelMax)
                    Error("too_big_for_space", "scene.space",
                        $"a panel scene is shown at true size beside its panel, and this one is {M2(extent)} m across",
                        $"keep it under {PanelMax} m, or use space volume, which scales it to fit its box");
                if (space == "room" && (extent > RoomMax || extent < RoomMin))
                    Error("too_big_for_space", "scene.space", $"a room course's own parts span {M2(extent)} m",
                        $"keep the parts a room course adds between {RoomMin} and {RoomMax} m");
            }

            void Floating()
            {
                Components(parts, out var groups);
                if (groups.Count < 2) return;
                int main = 0;
                for (int g = 1; g < groups.Count; g++)
                    if (Volume(groups[g]) > Volume(groups[main])) main = g;  // what holds the rest up is the bulk of it
                for (int g = 0; g < groups.Count; g++)
                {
                    if (g == main) continue;
                    double best = double.MaxValue;
                    Part from = null, to = null;
                    V move = default;
                    foreach (var a in groups[g])
                    foreach (var b in groups[main])
                    {
                        double gap = Gap(a, b, out var m);
                        if (gap < best) { best = gap; from = a; to = b; move = m; }
                    }
                    if (from == null) continue;
                    var ids = new List<string>();
                    foreach (var p in groups[g]) ids.Add(p.node.id);
                    string where = grounded ? "it would fall" : "it floats in the view";
                    Flag(grounded, "part_floating", $"nodes[{from.node.index}].pos",
                        $"{Join(ids, 6)} touch{(ids.Count == 1 ? "es" : "")} nothing else: {where}. The nearest part is {to.node.id}, {Mm(best)} away",
                        $"move {from.node.id} by {Show(move)} m (scene axes{(from.node.parent != null ? ", or move its parent" : "")}) so it rests on {to.node.id}, or make it a child of {to.node.id}");
                }
            }

            static double Volume(List<Part> list)
            {
                double v = 0;
                foreach (var p in list) v += p.Volume;
                return v;
            }

            /// <summary>The contact graph's connected groups (parts that touch, or that a line joins); returns each part's group index.</summary>
            Dictionary<Part, int> Components(List<Part> list, out List<List<Part>> groups)
            {
                var of = new Dictionary<Part, int>();
                groups = new List<List<Part>>();
                foreach (var start in list)
                {
                    if (of.ContainsKey(start)) continue;
                    var group = new List<Part>();
                    var stack = new Stack<Part>();
                    stack.Push(start);
                    of[start] = groups.Count;
                    while (stack.Count > 0)
                    {
                        var x = stack.Pop();
                        group.Add(x);
                        foreach (var y in list)
                            if (!of.ContainsKey(y) && (Touch(x, y) || Linked(x, y))) { of[y] = groups.Count; stack.Push(y); }
                    }
                    groups.Add(group);
                }
                return of;
            }

            void WouldTip()
            {
                if (!grounded || space == "room" || parts.Count < 2) return;
                Components(parts, out var groups);
                List<Part> main = groups[0];
                foreach (var g in groups) if (Volume(g) > Volume(main)) main = g;
                if (main.Count < 2) return;
                double floor = double.MaxValue;
                foreach (var p in main) floor = Math.Min(floor, p.lo.y);
                double x0 = double.MaxValue, x1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue;
                var feet = new List<string>();
                foreach (var p in main)
                {
                    if (p.lo.y > floor + Contact) continue;
                    feet.Add(p.node.id);
                    x0 = Math.Min(x0, p.lo.x); x1 = Math.Max(x1, p.hi.x); z0 = Math.Min(z0, p.lo.z); z1 = Math.Max(z1, p.hi.z);
                }
                double total = 0, cx = 0, cz = 0;
                foreach (var p in main) { double v = p.Volume; total += v; cx += p.centre.x * v; cz += p.centre.z * v; }
                if (total <= 0) return;
                cx /= total;
                cz /= total;
                double margin = 0.05 * Math.Max(x1 - x0, z1 - z0);
                double outX = Math.Max(x0 - cx, cx - x1), outZ = Math.Max(z0 - cz, cz - z1);
                if (Math.Max(outX, outZ) <= margin) return;
                Warn("would_tip", "nodes",
                    $"it would fall over: its weight is centred at x {M2(cx)}, z {M2(cz)}, {Mm(Math.Max(outX, outZ))} past what it stands on ({Join(feet, 4)})",
                    $"widen {feet[0]}, or move the heavy parts back over it");
            }

            void OutsideView()
            {
                if (space != "volume" || viewRadius <= 0) return;
                double limit = ViewSlack * viewRadius;
                var said = new HashSet<string>();
                foreach (var p in parts)
                {
                    double far = Reach(p);
                    if (far > limit && said.Add(p.node.id))
                        Error("outside_view", $"nodes[{p.node.index}]",
                            $"{p.node.id} reaches {M2(far)} m from the centre, but view.radius is {M2(viewRadius)}: it is cut off by the box",
                            $"raise scene.view.radius to at least {M2(far)}, or bring {p.node.id} in");
                }
                // Sim bodies, over the first two seconds of the sim at the defaults.
                foreach (var n in nodes)
                {
                    if (n.kind != "sim") continue;
                    var track = SimTrack(n, rest.inputs, 2.0, 8);
                    if (track == null) continue;
                    foreach (var pair in track)
                    {
                        if (pair.Value > limit && said.Add(pair.Key))
                            Error("outside_view", $"nodes[{byId[pair.Key].index}]",
                                $"{pair.Key}, moved by {n.id}, reaches {M2(pair.Value)} m from the centre in its first two seconds, but view.radius is {M2(viewRadius)}",
                                $"raise scene.view.radius to at least {M2(pair.Value)}, or shorten what {n.id} moves");
                    }
                }
            }

            // ---------- B2: every control moves something you can see ----------

            void Controls()
            {
                var used = new HashSet<string>();
                var unmeasured = new HashSet<string>();
                foreach (var n in nodes)
                {
                    if (n.kind == "panel") continue;
                    foreach (var p in n.json.Properties())
                    {
                        if (p.Name == "text" || p.Name == "title" || p.Name == "label" || p.Name == "widgets") continue;
                        var refs = new HashSet<string>();
                        Refs(p.Value, refs);
                        used.UnionWith(refs);
                        // pos, rot and scale are measured only on what the sweep measures: a moved text or line works, but
                        // isn't a part, so it is taken on trust like a text's height.
                        bool measured = ((p.Name == "pos" || p.Name == "rot" || p.Name == "scale") && HoldsPart(n)) || (n.kind == "primitive" && p.Name == "size")
                                        || (n.kind == "sim" && (p.Name == "rate" || p.Name == "params" || p.Name == "bodies"));
                        if (!measured) unmeasured.UnionWith(refs);
                    }
                }
                var generatorRefs = new HashSet<string>();
                Refs(scene["generators"], generatorRefs);
                used.UnionWith(generatorRefs);
                unmeasured.UnionWith(generatorRefs);  // a generator lays the course out again: that is seen

                foreach (var n in nodes)
                {
                    if (n.kind != "panel" || !(n.json["widgets"] is JArray widgets)) continue;
                    for (int w = 0; w < widgets.Count; w++)
                    {
                        if (!(widgets[w] is JObject widget)) continue;
                        string type = Str(widget, "type"), path = $"nodes[{n.index}].widgets[{w}]";
                        if (type == "slider")
                        {
                            string input = Str(widget, "input");
                            if (input == null) continue;
                            if (!used.Contains(input))
                                Error("control_unbound", path + ".input",
                                    $"the {input} slider changes nothing: no part, sim or generator uses \"${input}\"",
                                    $"write \"${input}\" where it should act: in the pos, rot, scale or size of the part it moves, or a sim's rate or params. A timeline's speed can't follow a slider in this version: for speed, use a sim's rate");
                            else if (!unmeasured.Contains(input)) Sweep(input, path + ".input");
                        }
                        else if (type == "button") Button(Str(widget, "id"), path);
                    }
                }
            }

            void Sweep(string input, string path)
            {
                if (!rest.inputs.Has(input)) return;  // the validator reports a slider on an unknown input
                double min = rest.inputs.Min(input), max = rest.inputs.Max(input);
                var low = new Pose { inputs = Inputs() };
                var high = new Pose { inputs = Inputs() };
                if (!low.inputs.TrySet(input, min) || !high.inputs.TrySet(input, max)) return;
                var a = Parts(low);
                var b = Parts(high);
                double moved = 0;
                string mover = null;
                var at = new Dictionary<string, Part>();
                foreach (var p in a) at[p.node.id] = p;
                foreach (var q in b)
                {
                    if (!at.TryGetValue(q.node.id, out var p)) continue;
                    for (int k = 0; k < 8; k++)
                    {
                        double d = (p.corners[k] - q.corners[k]).Length;
                        if (d > moved) { moved = d; mover = q.node.id; }
                    }
                }
                double seen = Math.Max(SeenMove, SeenShare * extent);
                bool unchecked_ = false;
                string simMover = null;
                if (moved < seen)
                    foreach (var n in nodes)
                    {
                        if (n.kind != "sim" || !Mentions(n.json, input)) continue;
                        bool? differs = SimDiffers(n, low.inputs, high.inputs, out simMover);
                        if (differs == null) { unchecked_ = true; continue; }
                        if (differs == true) break;
                        simMover = null;
                    }
                string label = rest.inputs.Label(input);
                if (moved >= seen)
                {
                    facts.Append($"Slider {input} ({Fmt(min)} to {Fmt(max)}) moves {mover} up to {M2(moved)} m.\n");
                    return;
                }
                if (simMover != null)
                {
                    facts.Append($"Slider {input} ({Fmt(min)} to {Fmt(max)}) changes how {simMover} moves in its sim.\n");
                    return;
                }
                if (unchecked_)
                {
                    Warn("sim_not_checked", path, $"the {input} slider drives a sim too long to try out here, so whether it moves anything wasn't checked", null);
                    return;
                }
                Error("control_invisible", path,
                    $"moving {label} from {Fmt(min)} to {Fmt(max)} moves nothing more than {Mm(moved)}, so it seems to do nothing",
                    $"use \"${input}\" where a change shows: a part's pos or rot, its size, or a sim's rate; or widen its min and max");
            }

            static bool Mentions(JToken token, string input)
            {
                var refs = new HashSet<string>();
                Refs(token, refs);
                return refs.Contains(input);
            }

            void Button(string id, string path)
            {
                if (id == null) return;
                bool any = false, seen = false;
                foreach (var r in rules)
                {
                    if (!(r is JObject rule) || Str(rule, "type") != "on_input" || Str(rule, "input") != id || !(rule["do"] is JArray actions)) continue;
                    foreach (var a in actions)
                    {
                        if (!(a is JObject action)) continue;
                        any = true;
                        string timeline = Str(action, "timeline");
                        if (timeline == null || Animates(timeline)) seen = true;  // set, sim and run are the validator's
                    }
                }
                if (any && !seen)
                    Error("control_invisible", path,
                        $"the {id} button only plays or pauses timelines that animate nothing that changes",
                        "add an animate rule on that timeline whose keys differ (a rot from [0,0,0] to [0,0,-360], say), or point the button at a timeline that has one");
            }

            bool Animates(string timeline)
            {
                foreach (var r in rules)
                {
                    if (!(r is JObject rule) || Str(rule, "type") != "animate" || Str(rule, "timeline") != timeline || !(rule["keys"] is JArray keys)) continue;
                    for (int i = 1; i < keys.Count; i++)
                        if (keys[i] is JArray k && keys[0] is JArray k0 && k.Count > 1 && k0.Count > 1 && !JToken.DeepEquals(k[1], k0[1])) return true;
                }
                return false;
            }

            // ---------- B3: moving parts (warnings until the thresholds are tuned on real output) ----------

            void MovingParts()
            {
                if (movingRoots.Count == 0 || parts.Count == 0) return;
                var stat = new List<Part>();
                foreach (var p in parts)
                {
                    string root = RootOf(p.node);
                    if (root != null) rootOf[p.node.id] = root; else stat.Add(p);
                }
                double floor = double.MaxValue;
                foreach (var p in stat) floor = Math.Min(floor, p.lo.y);

                foreach (string rootId in movingRoots)
                {
                    var root = byId[rootId];
                    var riding = new List<Part>();
                    foreach (var p in parts) if (rootOf.TryGetValue(p.node.id, out var r) && r == rootId) riding.Add(p);
                    if (riding.Count == 0) continue;
                    var poses = PosesOf(root);
                    if (poses.Count == 0) continue;
                    var touching = new List<Part>();
                    foreach (var s in stat)
                        foreach (var p in riding)
                            if (Touch(p, s)) { touching.Add(s); break; }
                    bool turns = Turns(root);

                    // 12a: what turns has to turn about a point that is held.
                    if (grounded && turns && touching.Count > 0)
                    {
                        var memo = new Dictionary<string, Frame>();
                        var pivot = FrameOf(root, rest, memo).t;
                        double d = double.MaxValue;
                        Part support = null;
                        foreach (var s in touching)
                        {
                            double ds = Distance(pivot, s);
                            if (ds < d) { d = ds; support = s; }
                        }
                        double thin = double.MaxValue;
                        foreach (var p in riding) thin = Math.Min(thin, p.Thin);
                        double limit = Math.Max(0.01, 0.55 * thin);
                        if (d > limit)
                            Warn("pivot_unsupported", $"nodes[{root.index}].pos",
                                $"{rootId} turns about its own centre {Show(pivot)}, {Mm(d)} from {support.node.id}, which holds it: it would come off as it turns",
                                $"put {rootId}'s pos at the point it hinges on (on {support.node.id}), or make the moving part a child of a small part placed at the hinge");
                    }

                    // 12b and 12c, and outside_view over the swept poses.
                    var restRiding = new Dictionary<string, Part>();
                    foreach (var p in riding) restRiding[p.node.id] = p;
                    bool changed = false, visible = false, collided = false, outside = false;
                    V axis = new V(0, 0, 0);
                    var restFrame = FrameOf(root, rest, new Dictionary<string, Frame>());
                    foreach (var pose in poses)
                    {
                        var memo = new Dictionary<string, Frame>();
                        var frame = FrameOf(root, pose, memo);
                        bool differs = Differs(frame, restFrame);
                        if (differs) changed = true;
                        if (turns && differs && axis.Length < Eps) axis = Axis(restFrame.A, frame.A);
                        var now = new List<Part>();
                        foreach (var p in riding)
                        {
                            var size = Vec(p.node.json["size"], new V(0.1, 0.1, 0.1), pose.inputs);
                            var half = new V(Math.Abs(size.x) / 2, p.shape == "plane" ? 0 : Math.Abs(size.y) / 2, Math.Abs(size.z) / 2);
                            now.Add(Measure(p.node, p.shape, half, FrameOf(p.node, pose, memo)));
                        }
                        foreach (var q in now)
                            if (!Round(q.shape) || Seen(restRiding[q.node.id], q)) visible = true;  // a box shows any turn; too small a turn is control_invisible's

                        if (!collided)
                            foreach (var q in now)
                            {
                                double limitFloor = Math.Max(0.005, 0.1 * q.Thin);
                                if (grounded && stat.Count > 0)
                                {
                                    double low = double.MaxValue;
                                    foreach (var c in Outline(q)) low = Math.Min(low, c.y);
                                    if (floor - low > limitFloor)
                                    {
                                        collided = true;
                                        Warn("moving_part_collides", $"nodes[{q.node.index}]",
                                            $"{pose.label}, {q.node.id} goes {Mm(floor - low)} below the base it stands on",
                                            $"raise {rootId}, shorten {q.node.id}, or narrow the range it moves through");
                                        break;
                                    }
                                }
                                foreach (var s in stat)
                                {
                                    if (touching.Contains(s)) continue;
                                    double depth = Penetration(q, s);
                                    if (depth > Math.Max(0.005, 0.1 * Math.Min(q.Thin, s.Thin)))
                                    {
                                        collided = true;
                                        Warn("moving_part_collides", $"nodes[{q.node.index}]",
                                            $"{pose.label}, {q.node.id} goes {Mm(depth)} into {s.node.id}",
                                            $"move {s.node.id} out of its way, shorten {q.node.id}, or narrow the range it moves through");
                                        break;
                                    }
                                }
                                if (collided) break;
                            }

                        if (!outside && space == "volume" && viewRadius > 0)
                            foreach (var q in now)
                            {
                                double far = Reach(q);
                                if (far <= ViewSlack * viewRadius) continue;
                                outside = true;
                                Error("outside_view", $"nodes[{q.node.index}]",
                                    $"{pose.label}, {q.node.id} reaches {M2(far)} m from the centre, but view.radius is {M2(viewRadius)}: it is cut off as it moves",
                                    $"raise scene.view.radius to at least {M2(far)}, or narrow the range it moves through");
                                break;
                            }
                    }

                    string how = Moves(root);
                    if (!changed)
                        Warn("motion_invisible", MovePath(root), $"{rootId} is set to move by {how}, but every value is the same, so it stays still",
                            "give the keys (or the slider's min and max) different values");
                    else if (!visible)
                        Warn("motion_invisible", MovePath(root),
                            $"{rootId} turns about world {AxisName(axis)}, which is the round axis of everything that rides on it, so nothing seems to move",
                            $"turn it about another axis, or add a part that isn't round about that axis (a box, a spoke) as a child of {rootId}");
                    if (turns && changed)
                        facts.Append($"{rootId} turns about world {AxisName(axis)} (by {how}), pivot {Show(restFrame.t)}{(touching.Count > 0 ? ", held by " + touching[0].node.id : "")}.\n");
                    else if (changed) facts.Append($"{rootId} moves by {how}.\n");
                }
            }

            /// <summary>The moving root a node rides on: itself or its nearest moving ancestor. Null for a static part.</summary>
            string RootOf(Node n)
            {
                int guard = 0;
                for (var a = n; a != null && guard < 64; a = a.parent != null && byId.TryGetValue(a.parent, out var p) ? p : null, guard++)
                    if (movingRoots.Contains(a.id)) return a.id;
                return null;
            }

            bool Turns(Node root)
            {
                if (ArtifactInputs.IsBound(root.json["rot"])) return true;
                foreach (var r in rules)
                    if (r is JObject rule && Str(rule, "type") == "animate" && Str(rule, "target") == root.id && Str(rule, "property") == "rot") return true;
                return false;
            }

            string Moves(Node root)
            {
                var how = new List<string>();
                foreach (string f in new[] { "pos", "rot", "scale" })
                {
                    var refs = new HashSet<string>();
                    Refs(root.json[f], refs);
                    foreach (var r in refs) how.Add($"{f} ${r}");
                }
                foreach (var r in rules)
                    if (r is JObject rule && Str(rule, "type") == "animate" && Str(rule, "target") == root.id)
                    {
                        string p = Str(rule, "property");
                        if (p == "pos" || p == "rot" || p == "scale") how.Add($"{p} on timeline {Str(rule, "timeline")}");
                    }
                return string.Join(", ", how);
            }

            string MovePath(Node root)
            {
                for (int i = 0; i < rules.Count; i++)
                    if (rules[i] is JObject rule && Str(rule, "type") == "animate" && Str(rule, "target") == root.id) return $"rules[{i}].keys";
                return $"nodes[{root.index}]";
            }

            /// <summary>Up to <see cref="Samples"/> poses for one moving root: its inputs swept min to max, its animate keys and midpoints.</summary>
            List<Pose> PosesOf(Node root)
            {
                var poses = new List<Pose>();
                var refs = new HashSet<string>();
                foreach (string f in new[] { "pos", "rot", "scale" }) Refs(root.json[f], refs);
                foreach (string input in refs)
                {
                    if (!rest.inputs.Has(input)) continue;
                    double min = rest.inputs.Min(input), max = rest.inputs.Max(input);
                    for (int s = 0; s < Samples; s++)
                    {
                        double v = min + (max - min) * s / (Samples - 1);
                        var inputs = Inputs();
                        if (!inputs.TrySet(input, v)) continue;
                        poses.Add(new Pose { inputs = inputs, label = $"at ${input}={Fmt(rest.inputs.Whole(input) ? Math.Round(v) : v)}" });
                    }
                }
                foreach (var r in rules)
                {
                    if (!(r is JObject rule) || Str(rule, "type") != "animate" || Str(rule, "target") != root.id || !(rule["keys"] is JArray keys)) continue;
                    string property = Str(rule, "property");
                    if (property != "pos" && property != "rot" && property != "scale") continue;
                    var values = new List<(double t, JToken v)>();
                    for (int i = 0; i < keys.Count; i++)
                    {
                        if (!(keys[i] is JArray key) || key.Count < 2 || !IsNumber(key[0])) continue;
                        values.Add(((double)key[0], key[1]));
                        if (i + 1 < keys.Count && keys[i + 1] is JArray next && next.Count > 1 && IsNumber(next[0]))
                            values.Add((((double)key[0] + (double)next[0]) / 2, Mid(key[1], next[1])));
                    }
                    int step = Math.Max(1, (values.Count + Samples - 1) / Samples);
                    for (int i = 0; i < values.Count; i += step)
                    {
                        if (values[i].v == null) continue;
                        var over = new Dictionary<string, Dictionary<string, JToken>> { [root.id] = new Dictionary<string, JToken> { [property] = values[i].v } };
                        poses.Add(new Pose { inputs = rest.inputs, over = over, label = $"at t={Fmt(values[i].t)} s on {Str(rule, "timeline")}" });
                    }
                }
                return poses;
            }

            // Each angle blends on its own, as the timeline does.
            static JToken Mid(JToken a, JToken b)
            {
                if (IsNumber(a) && IsNumber(b)) return ((double)a + (double)b) / 2;
                if (a is JArray x && b is JArray y && x.Count >= 3 && y.Count >= 3 && IsNumber(x[0]) && IsNumber(x[1]) && IsNumber(x[2]) && IsNumber(y[0]) && IsNumber(y[1]) && IsNumber(y[2]))
                    return new JArray(((double)x[0] + (double)y[0]) / 2, ((double)x[1] + (double)y[1]) / 2, ((double)x[2] + (double)y[2]) / 2);
                return null;
            }

            static bool Differs(Frame a, Frame b)
            {
                if ((a.t - b.t).Length > 1e-4) return true;
                for (int k = 0; k < 3; k++) if ((a.A.Column(k) - b.A.Column(k)).Length > 1e-4) return true;
                return false;
            }

            /// <summary>Whether a part looks different: it moved, or turned about anything but its own round axis.</summary>
            static bool Seen(Part before, Part after)
            {
                if ((before.centre - after.centre).Length > 0.001) return true;
                for (int k = 0; k < 3; k++)
                    if (Math.Abs(before.frame.A.Column(k).Length * before.half[k] - after.frame.A.Column(k).Length * after.half[k]) > 0.0005) return true;
                if (before.shape == "sphere")
                    return Math.Abs(before.half.x - before.half.y) > 0.02 * before.half.x || Math.Abs(before.half.x - before.half.z) > 0.02 * before.half.x
                        ? Differs(before.frame, after.frame) : false;
                if (before.shape == "cylinder" || before.shape == "cone" || before.shape == "capsule")
                {
                    if (Math.Abs(before.half.x - before.half.z) > 0.02 * Math.Max(before.half.x, before.half.z)) return Differs(before.frame, after.frame);
                    var u = before.frame.A.Column(1).Normalized;
                    var w = after.frame.A.Column(1).Normalized;
                    return V.Dot(u, w) < Math.Cos(5 * Math.PI / 180);
                }
                for (int k = 0; k < 3; k++)
                    if ((before.frame.A.Column(k).Normalized - after.frame.A.Column(k).Normalized).Length > 0.01) return true;
                return false;
            }

            /// <summary>The world axis a turn from one pose to another is about.</summary>
            static V Axis(M from, M to)
            {
                var r = to.Rotation() * from.Rotation().Transposed;
                var axis = new V(r.h - r.f, r.c - r.g, r.d - r.b);
                if (axis.Length < 1e-6)
                {
                    // 180 degrees: the axis is the column of (R + I) with the largest length.
                    var plus = new M { a = r.a + 1, b = r.b, c = r.c, d = r.d, e = r.e + 1, f = r.f, g = r.g, h = r.h, i = r.i + 1 };
                    axis = plus.Column(0);
                    for (int k = 1; k < 3; k++) if (plus.Column(k).Length > axis.Length) axis = plus.Column(k);
                }
                return axis.Normalized;
            }

            static string AxisName(V axis)
            {
                double x = Math.Abs(axis.x), y = Math.Abs(axis.y), z = Math.Abs(axis.z);
                string name = y >= x && y >= z ? "Y (up)" : x >= z ? "X (left-right)" : "Z (front-back)";
                double best = Math.Max(x, Math.Max(y, z));
                return best > 0.996 ? name : $"{Show(axis)} (nearest {name})";
            }

            static double Distance(V point, Part box)
            {
                double dx = Math.Max(Math.Max(box.lo.x - point.x, 0), point.x - box.hi.x);
                double dy = Math.Max(Math.Max(box.lo.y - point.y, 0), point.y - box.hi.y);
                double dz = Math.Max(Math.Max(box.lo.z - point.z, 0), point.z - box.hi.z);
                return Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }

            /// <summary>How deep two parts' boxes overlap (separating-axis test on the oriented boxes); 0 when apart.</summary>
            static double Penetration(Part a, Part b)
            {
                var axes = new List<V>(15);
                for (int k = 0; k < 3; k++) { axes.Add(a.frame.A.Column(k).Normalized); axes.Add(b.frame.A.Column(k).Normalized); }
                for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    var c = V.Cross(a.frame.A.Column(i).Normalized, b.frame.A.Column(j).Normalized);
                    if (c.Length > 1e-6) axes.Add(c.Normalized);
                }
                double depth = double.MaxValue;
                foreach (var axis in axes)
                {
                    if (axis.Length < 0.5) continue;  // a plane's thin axis normalised to nothing
                    Project(a, axis, out double a0, out double a1);
                    Project(b, axis, out double b0, out double b1);
                    double overlap = Math.Min(a1, b1) - Math.Max(a0, b0);
                    if (overlap <= 0) return 0;
                    depth = Math.Min(depth, overlap);
                }
                return depth == double.MaxValue ? 0 : depth;
            }

            static void Project(Part p, V axis, out double lo, out double hi)
            {
                lo = double.MaxValue;
                hi = double.MinValue;
                foreach (var c in p.corners)
                {
                    double d = V.Dot(c, axis);
                    lo = Math.Min(lo, d);
                    hi = Math.Max(hi, d);
                }
            }

            // ---------- sims, stepped here with their own solver ----------

            /// <summary>Each body's furthest reach from the centre over <paramref name="seconds"/> (watched time, so rate counts).</summary>
            Dictionary<string, double> SimTrack(Node sim, ArtifactInputs inputs, double seconds, int samples)
            {
                var reach = new Dictionary<string, double>();
                if (!Step(sim, inputs, seconds, samples, (body, world, radius) =>
                    {
                        double far = world.Length + radius;
                        reach[body] = reach.TryGetValue(body, out var was) ? Math.Max(was, far) : far;
                    })) return null;
                return reach;
            }

            /// <summary>
            /// Whether a sim moves any differently with the inputs at <paramref name="a"/> and at <paramref name="b"/>: a
            /// different start, a different rate (when it moves at all), or paths that part within the first watched second.
            /// Any real difference counts, however small, because a sim's differences grow (a chaos demo's nudge is tiny at
            /// first). What it catches is a slider wired to a number the motion ignores. Null when it couldn't be tried.
            /// </summary>
            bool? SimDiffers(Node sim, ArtifactInputs a, ArtifactInputs b, out string body)
            {
                body = null;
                var sa = Solver(sim, a);
                var sb = Solver(sim, b);
                if (sa == null || sb == null || !(sim.json["bodies"] is JArray list)) return null;
                int count = Math.Min(Math.Min(sa.Bodies, sb.Bodies), list.Count);
                string Name(int i) => list[i] is JObject o ? Str(o, "node") : null;
                for (int i = 0; i < count; i++)
                    if (Apart(sa.Position(i), sb.Position(i))) { body = Name(i); return true; }
                double ra = Num(sim.json["rate"], 1, a), rb = Num(sim.json["rate"], 1, b);
                if (Math.Abs(ra - rb) > Eps)
                {
                    // Watched faster or slower: that shows as long as the sim moves at all.
                    var fast = ra > rb ? sa : sb;
                    var start = new V[count];
                    for (int i = 0; i < count; i++) start[i] = ToV(fast.Position(i));
                    int steps = (int)Math.Ceiling(Math.Max(ra, rb) / fast.StepSize);
                    for (int s = 0; s < steps && !fast.Finished; s++)
                    {
                        if (++simSteps > MaxSimSteps) return null;
                        fast.Step();
                        for (int i = 0; i < count; i++)
                            if ((ToV(fast.Position(i)) - start[i]).Length > 1e-6) { body = Name(i); return true; }
                    }
                    return false;
                }
                if (Math.Abs(sa.StepSize - sb.StepSize) > 1e-12) { body = Name(0); return true; }
                int n = (int)Math.Ceiling(ra / sa.StepSize);
                for (int s = 0; s < n && !sa.Finished && !sb.Finished; s++)
                {
                    simSteps += 2;
                    if (simSteps > MaxSimSteps) return null;
                    sa.Step();
                    sb.Step();
                    for (int i = 0; i < count; i++)
                        if (Apart(sa.Position(i), sb.Position(i))) { body = Name(i); return true; }
                }
                return false;
            }

            static bool Apart(UnityEngine.Vector3 p, UnityEngine.Vector3 q) => (ToV(p) - ToV(q)).Length > 1e-6;
            static V ToV(UnityEngine.Vector3 p) => new V(p.x, p.y, p.z);

            IArtifactSolver Solver(Node sim, ArtifactInputs inputs)
            {
                string name = Str(sim.json, "solver");
                var solver = name != null ? ArtifactSolvers.Create(name) : null;
                if (solver == null || !(sim.json["bodies"] is JArray list) || list.Count == 0) return null;
                try
                {
                    solver.Configure(sim.json, inputs);
                    solver.Restart();
                }
                catch (Exception) { return null; }  // the validator reports a malformed sim
                return solver.StepSize > 0 ? solver : null;
            }

            bool Step(Node sim, ArtifactInputs inputs, double seconds, int samples, Action<string, V, double> at)
            {
                var solver = Solver(sim, inputs);
                if (solver == null) return false;
                var list = (JArray)sim.json["bodies"];
                double rate = Num(sim.json["rate"], 1, inputs);
                double step = solver.StepSize;
                int total = (int)Math.Ceiling(Math.Max(0, seconds * rate) / step);
                if (simSteps + total > MaxSimSteps) return false;
                simSteps += total;
                var pose = new Pose { inputs = inputs };
                var memo = new Dictionary<string, Frame>();
                var bodies = new List<(string id, Frame parent, double radius)>();
                for (int i = 0; i < list.Count && i < solver.Bodies; i++)
                {
                    string id = list[i] is JObject b ? Str(b, "node") : null;
                    if (id == null || !byId.TryGetValue(id, out var node)) return false;
                    var parentFrame = node.parent != null && byId.TryGetValue(node.parent, out var p) ? FrameOf(p, pose, memo) : Frame.Root;
                    var own = FrameOf(node, pose, memo);
                    var size = Vec(node.json["size"], new V(0.1, 0.1, 0.1), inputs);
                    double radius = 0;
                    for (int k = 0; k < 3; k++) radius = Math.Max(radius, own.A.Column(k).Length * Math.Abs(size[k]) / 2);
                    bodies.Add((id, parentFrame, radius));
                }
                int every = samples <= 1 ? total : Math.Max(1, total / samples);
                for (int s = 0; s <= total; s++)
                {
                    if (s > 0)
                    {
                        if (solver.Finished) break;
                        solver.Step();
                    }
                    if (s == total || (samples > 1 && s % every == 0))
                        for (int i = 0; i < bodies.Count; i++)
                        {
                            var local = solver.Position(i);
                            at(bodies[i].id, bodies[i].parent.Point(new V(local.x, local.y, local.z)), bodies[i].radius);
                        }
                }
                return true;
            }

            // ---------- B4: what was measured, for the agent to say and to fix from ----------

            void Describe()
            {
                string fit = space == "volume"
                    ? viewRadius > 0 ? $"a volume whose view sphere (radius {M2(viewRadius)} m) is fitted to the box" : "a volume, stood on its base and scaled to fit the box"
                    : space == "panel" ? "beside its panel at true size" : space == "room" ? "in the room, laid out by its generator" : "in no known space";
                if (parts.Count == 0)
                {
                    facts.Append($"Shown {fit}. No solid parts to measure (models, text and lines aren't measured).\n");
                    return;
                }
                double ext = Extent(parts);
                V lo = parts[0].lo, hi = parts[0].hi;
                foreach (var p in parts)
                {
                    lo = new V(Math.Min(lo.x, p.lo.x), Math.Min(lo.y, p.lo.y), Math.Min(lo.z, p.lo.z));
                    hi = new V(Math.Max(hi.x, p.hi.x), Math.Max(hi.y, p.hi.y), Math.Max(hi.z, p.hi.z));
                }
                facts.Append($"Shown {fit}. {parts.Count} solid part{(parts.Count == 1 ? "" : "s")}, {M2(ext)} m across (x {M2(hi.x - lo.x)}, y {M2(hi.y - lo.y)}, z {M2(hi.z - lo.z)}).\n");
                int listed = 0;
                foreach (var p in parts)
                {
                    if (listed++ == 10) { facts.Append($"... and {parts.Count - 10} more.\n"); break; }
                    facts.Append($"{p.node.id}: {p.shape} {Dims(p)} m, centre {Show(p.centre)}, y {M2(p.lo.y)} to {M2(p.hi.y)}.\n");
                }
                var pairs = new List<string>();
                for (int i = 0; i < parts.Count && pairs.Count < 12; i++)
                for (int j = i + 1; j < parts.Count && pairs.Count < 12; j++)
                    if (Touch(parts[i], parts[j])) pairs.Add(parts[i].node.id + "-" + parts[j].node.id);
                if (pairs.Count > 0) facts.Append("Touching: ").Append(string.Join(", ", pairs)).Append(".\n");
            }

            static string Dims(Part p) => $"{M2(p.hi.x - p.lo.x)}x{M2(p.hi.y - p.lo.y)}x{M2(p.hi.z - p.lo.z)}";

            // ---------- tokens ----------

            static string Str(JObject o, string key) => o[key] is JValue v && v.Type == JTokenType.String ? (string)v : null;

            static bool IsNumber(JToken t) => t != null && (t.Type == JTokenType.Integer || (t.Type == JTokenType.Float && !double.IsNaN((double)t) && !double.IsInfinity((double)t)));

            static double Num(JToken t, double fallback, ArtifactInputs inputs)
            {
                if (IsNumber(t))
                {
                    double v = (double)t;
                    return double.IsNaN(v) || double.IsInfinity(v) ? fallback : v;
                }
                if (t != null && t.Type == JTokenType.String)
                {
                    string s = (string)t;
                    if (s.StartsWith("$") && inputs.Has(s.Substring(1))) return inputs.Value(s.Substring(1));
                }
                return fallback;
            }

            static V Vec(JToken t, V fallback, ArtifactInputs inputs)
            {
                if (!(t is JArray a) || a.Count < 3) return fallback;
                return new V(Num(a[0], fallback.x, inputs), Num(a[1], fallback.y, inputs), Num(a[2], fallback.z, inputs));
            }

            static void Refs(JToken token, HashSet<string> into)
            {
                if (token == null) return;
                if (token.Type == JTokenType.String)
                {
                    string s = (string)token;
                    if (s.Length > 1 && s[0] == '$') into.Add(s.Substring(1));
                }
                else if (token is JArray list) foreach (var item in list) Refs(item, into);
                else if (token is JObject o)
                    foreach (var p in o.Properties())
                        if (p.Name != "text" && p.Name != "title" && p.Name != "label" && p.Name != "widgets") Refs(p.Value, into);
            }
        }

        // ---------- numbers, as the agent reads them ----------

        static string Fmt(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
        static string M2(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
        static string Mm(double metres) => metres < 0.1
            ? Math.Round(metres * 1000, 1).ToString("0.#", CultureInfo.InvariantCulture) + " mm"
            : Math.Round(metres, 3).ToString("0.###", CultureInfo.InvariantCulture) + " m";
        static string Show(V v) => $"[{Fmt(v.x)}, {Fmt(v.y)}, {Fmt(v.z)}]";

        static string Join(List<string> ids, int most) =>
            ids.Count <= most ? string.Join(", ", ids) : string.Join(", ", ids.GetRange(0, most)) + $" and {ids.Count - most} more";
    }
}
