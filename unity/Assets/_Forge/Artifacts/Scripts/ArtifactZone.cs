using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// A <c>zone</c> node (C-12 3.2): a box that a run tests the bound Thing's position against each FixedUpdate, with
    /// no trigger colliders (section 5). When visible it shows as an outline on the floor, so a course reads from above.
    /// </summary>
    public sealed class ArtifactZone : MonoBehaviour
    {
        public Vector3 Size;
        LineRenderer outline;

        /// <summary>Whether a world point is inside the box. No allocations: safe in FixedUpdate.</summary>
        public bool Contains(Vector3 world)
        {
            var local = transform.InverseTransformPoint(world);
            var half = Size * 0.5f;
            return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z;
        }

        public void SetColor(Color color)
        {
            if (outline != null) outline.startColor = outline.endColor = color;
        }

        public static GameObject Build(JObject n, ArtifactBuild build)
        {
            var go = new GameObject(n.Value<string>("id"));
            var zone = go.AddComponent<ArtifactZone>();
            var line = go.AddComponent<LineRenderer>();
            zone.outline = line;
            line.useWorldSpace = false;
            line.loop = true;
            line.positionCount = 4;
            line.sharedMaterial = ArtifactLook.Trail();
            line.numCornerVertices = 2;
            line.alignment = LineAlignment.View;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.widthMultiplier = 0.015f;
            zone.SetColor(Tint(n.Value<string>("id")));
            var size = n["size"];
            build.Apply(() =>
            {
                zone.Size = build.Inputs.Vector(size, new Vector3(0.5f, 0.5f, 0.5f));
                // Flat on the bottom face, a few millimetres up so it doesn't fight the floor.
                float y = -zone.Size.y * 0.5f + 0.004f, x = zone.Size.x * 0.5f, z = zone.Size.z * 0.5f;
                line.SetPosition(0, new Vector3(-x, y, -z));
                line.SetPosition(1, new Vector3(-x, y, z));
                line.SetPosition(2, new Vector3(x, y, z));
                line.SetPosition(3, new Vector3(x, y, -z));
            }, ArtifactInputs.IsBound(size));
            return go;
        }

        // The start reads as "go", the finish as the line to cross, everything else in the tool colour.
        static Color Tint(string id)
        {
            if (id == "start") return new Color(0.49f, 1f, 0.70f, 0.9f);
            if (id == "finish") return new Color(1f, 1f, 1f, 0.9f);
            var c = ForgeTheme.Tool;
            return new Color(c.r, c.g, c.b, 0.75f);
        }
    }

    /// <summary>
    /// An <c>array</c> node (C-12 3.2) over a generator slot: one copy of the template on each pose. The template's own
    /// <c>pos</c> and <c>rot</c> are offsets from the slot, so a cone template lifts itself by half its height.
    /// A table-sourced array (repo_city) isn't built yet; <see cref="ArtifactNodes.Missing"/> says so.
    /// </summary>
    public static class ArtifactArray
    {
        public static GameObject Build(JObject n, ArtifactBuild build)
        {
            string id = n.Value<string>("id");
            var go = new GameObject(id);
            string source = n.Value<string>("source");
            var template = (JObject)n["template"];
            if (source == null || !build.Slots.TryGetValue(source, out var poses)) return go;
            var copies = new List<GameObject>(poses.Count);
            for (int i = 0; i < poses.Count; i++)
            {
                var copy = (JObject)template.DeepClone();
                copy["id"] = $"{id}_{i}";
                var child = ArtifactNodes.Build(copy, build);
                child.transform.SetParent(go.transform, false);
                var pose = poses[i];
                var pos = template["pos"];
                var rot = template["rot"];
                build.Apply(() =>
                {
                    child.transform.localPosition = pose.position + pose.rotation * build.Inputs.Vector(pos, Vector3.zero);
                    child.transform.localRotation = pose.rotation * Quaternion.Euler(build.Inputs.Vector(rot, Vector3.zero));
                }, ArtifactInputs.IsBound(pos) || ArtifactInputs.IsBound(rot));
                copies.Add(child);
            }
            build.Arrays[id] = copies;
            return go;
        }
    }
}
