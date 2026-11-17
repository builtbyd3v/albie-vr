using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>What a node builder gets to work with while a scene is being built.</summary>
    public sealed class ArtifactBuild
    {
        public ArtifactHost Host;
        public ArtifactInputs Inputs;
        public Transform Root;
        public Dictionary<string, GameObject> Nodes;
        /// <summary>Generator slots, local to the scene root (C-12 3.3). Empty for a scene with no generators.</summary>
        public Dictionary<string, List<Pose>> Slots = new Dictionary<string, List<Pose>>();
        /// <summary>Each <c>array</c> node's copies, in slot order, for runs to count gates and hits.</summary>
        public readonly Dictionary<string, List<GameObject>> Arrays = new Dictionary<string, List<GameObject>>();
        /// <summary>The manifest's data tables (C-12 section 7), resolved before the build.</summary>
        public Dictionary<string, ArtifactTable> Tables = new Dictionary<string, ArtifactTable>();
        /// <summary>The <c>gltf</c> nodes' files, by path, read and hash-checked before the build.</summary>
        public Dictionary<string, byte[]> Assets = new Dictionary<string, byte[]>();
        /// <summary>Things the build had to change that Albie should say (an array cut to the copy limit).</summary>
        public readonly List<string> Notes = new List<string>();
        public readonly ArtifactMetrics Metrics = new ArtifactMetrics();
        readonly List<Action> bound = new List<Action>();
        readonly List<Action> after = new List<Action>();
        readonly List<ArtifactTrail> trails = new List<ArtifactTrail>();

        /// <summary>Runs <paramref name="apply"/> now, and again whenever an input changes if <paramref name="live"/>.</summary>
        public void Apply(Action apply, bool live)
        {
            apply();
            if (live) bound.Add(apply);
        }

        public void Refresh()
        {
            for (int i = 0; i < bound.Count; i++) bound[i]();
        }

        /// <summary>Runs once every node exists, for anything that points at another node.</summary>
        public void AfterBuild(Action action) => after.Add(action);

        public void Finish()
        {
            for (int i = 0; i < after.Count; i++) after[i]();
            after.Clear();
        }

        public void AddTrail(ArtifactTrail trail) => trails.Add(trail);

        public void ClearTrails()
        {
            for (int i = 0; i < trails.Count; i++) trails[i].Clear();
        }
    }

    /// <summary>
    /// Turns scene nodes into GameObjects. The contract says which kinds are legal; this says which ones this host can
    /// build today. It's a registry on purpose: an artifact shouldn't be limited to a short list of types (see
    /// docs/ARTIFACTS_BRIEF.md), so a new kind is one <see cref="Register"/> call, not a change to the host.
    /// </summary>
    public static class ArtifactNodes
    {
        public delegate GameObject Builder(JObject node, ArtifactBuild build);

        static readonly Dictionary<string, Builder> builders = new Dictionary<string, Builder>
        {
            { "primitive", Primitive },
            { "text", Text },
            { "panel", ArtifactWidgets.Build },
            { "sim", ArtifactSim.Build },
            { "line", ArtifactTrail.Build },
            { "zone", ArtifactZone.Build },
            { "array", ArtifactArray.Build },
            { "gltf", ArtifactGltf.Build },
            { "timeline", ArtifactTimeline.Build },
        };

        public static void Register(string kind, Builder builder) => builders[kind] = builder;

        public static bool CanBuild(string kind) => builders.ContainsKey(kind);

        public static GameObject Build(JObject node, ArtifactBuild build) => builders[node.Value<string>("kind")](node, build);

        /// <summary>The parts of a valid scene this host can't build yet. Empty: it can open.</summary>
        public static List<string> Missing(JObject scene)
        {
            var missing = new List<string>();
            if (scene["nodes"] is JArray nodes)
                foreach (var node in nodes)
                {
                    string kind = node.Value<string>("kind");
                    if (!CanBuild(kind) && !missing.Contains(kind)) missing.Add(kind);
                    if (kind == "array" && node["source"] is JObject s && s["grid"] == null && !missing.Contains("array from a table without a grid"))
                        missing.Add("array from a table without a grid");
                }
            if (scene["generators"] is JArray generators)
                foreach (var generator in generators)
                {
                    string type = generator.Value<string>("type");
                    if (!ArtifactGenerators.CanRun(type) && !missing.Contains(type + " generator")) missing.Add(type + " generator");
                }
            // A run block doesn't stop a scene opening: the course shows, and the run engine scores it.
            return missing;
        }

        // The mesh is a child, so `size` never reaches the node's own children and `scale` stays the author's.
        static GameObject Primitive(JObject n, ArtifactBuild build)
        {
            var root = new GameObject(n.Value<string>("id"));
            string shape = n.Value<string>("shape");
            bool collider = n.Value<bool?>("collider") == true;
            var mesh = ArtifactLook.Shape(shape, collider);
            mesh.transform.SetParent(root.transform, false);
            mesh.GetComponent<Renderer>().sharedMaterial = ArtifactLook.Material(n.Value<string>("color"), n.Value<bool?>("glow") == true);
            var size = n["size"];
            build.Apply(() => mesh.transform.localScale = ArtifactLook.ShapeScale(shape, build.Inputs.Vector(size, Vector3.one * 0.1f)),
                ArtifactInputs.IsBound(size));
            return root;
        }

        static GameObject Text(JObject n, ArtifactBuild build)
        {
            var go = new GameObject(n.Value<string>("id"));
            var text = go.AddComponent<TextMeshPro>();
            if (ForgeTheme.Font != null) text.font = ForgeTheme.Font;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.color = ArtifactLook.Color(n.Value<string>("color"), Color.white);
            text.rectTransform.sizeDelta = new Vector2(2f, 0.5f);
            // "align": "left" or "right" puts the node's position at that edge of the text instead of its middle, so a label
            // can stand beside a model with its first letter clear of it.
            string align = n.Value<string>("align");
            if (align == "left" || align == "right")
            {
                text.alignment = align == "left" ? TextAlignmentOptions.Left : TextAlignmentOptions.Right;
                text.rectTransform.pivot = new Vector2(align == "left" ? 0f : 1f, 0.5f);
            }
            // A dark edge round every label, so it reads over any floor or wall the room has behind it.
            text.outlineWidth = 0.22f;
            text.outlineColor = new Color32(8, 10, 14, 235);
            var height = n["height"];
            build.Apply(() => text.fontSize = build.Inputs.Number(height, 0.05f) * 10f, ArtifactInputs.IsBound(height));
            string raw = n.Value<string>("text");
            text.text = ArtifactLook.WithoutMetrics(raw);
            text.ForceMeshUpdate();
            if (ArtifactLook.HasMetrics(raw)) go.AddComponent<ArtifactMetricLabel>().Bind(raw, build.Metrics);
            if (n.Value<bool?>("billboard") == true) go.AddComponent<ArtifactBillboard>();
            return go;
        }
    }

    /// <summary>
    /// A <c>text</c> node with one <c>{metric}</c>. TMP formats the number itself, so a live timer doesn't allocate.
    /// Stays on "--" until the metric has a value.
    /// </summary>
    public sealed class ArtifactMetricLabel : MonoBehaviour
    {
        TMP_Text text;
        ArtifactMetrics metrics;
        int slot;
        string format, empty;
        double last = double.PositiveInfinity;

        public void Bind(string template, ArtifactMetrics board)
        {
            text = GetComponent<TMP_Text>();
            metrics = board;
            int open = template.IndexOf('{'), close = template.IndexOf('}');
            string name = open >= 0 && close > open ? template.Substring(open + 1, close - open - 1) : "";
            string prefix = open > 0 ? template.Substring(0, open) : "";
            string suffix = close >= 0 && close + 1 < template.Length ? template.Substring(close + 1) : "";
            slot = board.Slot(name);
            format = prefix + "{0:2}" + suffix;
            empty = prefix + ArtifactLook.NoValue + suffix;
            if (text != null) text.SetText(empty);
        }

        void LateUpdate()
        {
            if (metrics == null || text == null) return;
            double v = metrics.Value(slot);
            if (v == last || (double.IsNaN(v) && double.IsNaN(last))) return;
            last = v;
            if (double.IsNaN(v)) text.SetText(empty);
            else text.SetText(format, (float)v);
        }
    }

    /// <summary>Keeps a label facing the viewer.</summary>
    public sealed class ArtifactBillboard : MonoBehaviour
    {
        Camera cam;

        void LateUpdate()
        {
            if (cam == null) cam = Camera.main;
            if (cam != null) transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
        }
    }
}
