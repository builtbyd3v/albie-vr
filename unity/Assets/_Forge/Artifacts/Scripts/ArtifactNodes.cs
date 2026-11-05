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
                    if (kind == "array" && node["source"] is JObject && !missing.Contains("array from a table")) missing.Add("array from a table");
                }
            if (scene["generators"] is JArray generators)
                foreach (var generator in generators)
                {
                    string type = generator.Value<string>("type");
                    if (!ArtifactGenerators.CanRun(type) && !missing.Contains(type + " generator")) missing.Add(type + " generator");
                }
            // Runs (C-12 section 5) don't stop a scene opening: the course shows, and scoring arrives with the run engine.
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
            var height = n["height"];
            build.Apply(() => text.fontSize = build.Inputs.Number(height, 0.05f) * 10f, ArtifactInputs.IsBound(height));
            text.text = ArtifactLook.WithoutMetrics(n.Value<string>("text"));
            text.ForceMeshUpdate();
            if (n.Value<bool?>("billboard") == true) go.AddComponent<ArtifactBillboard>();
            return go;
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
