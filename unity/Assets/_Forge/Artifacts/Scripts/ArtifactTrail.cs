using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// A <c>line</c> node: fixed <c>points</c>, with <c>follow</c> a fading trail behind another node, or with <c>through</c>
    /// a line joining other nodes as they move (the rods of a pendulum). The trail is a preallocated ring and the joined
    /// points a preallocated array, so neither costs the garbage collector anything however long it runs.
    /// </summary>
    public sealed class ArtifactTrail : MonoBehaviour
    {
        const float MinMove = 0.004f;  // scene units between samples, so a paused body doesn't eat the trail

        LineRenderer line;
        Transform target;
        Transform[] through;
        Vector3[] joined;
        Vector3[] ring, ordered;
        int head, filled;
        float width, shownScale;

        public static GameObject Build(JObject n, ArtifactBuild build)
        {
            var go = new GameObject(n.Value<string>("id"));
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.sharedMaterial = ArtifactLook.Trail();
            line.numCapVertices = 4;
            line.numCornerVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.alignment = LineAlignment.View;
            var color = ArtifactLook.Color(n.Value<string>("color"), ForgeTheme.Tool);

            var trail = go.AddComponent<ArtifactTrail>();
            trail.line = line;
            trail.width = build.Inputs.Number(n["width"], 0.01f);

            string follow = n.Value<string>("follow");
            if (follow != null)
            {
                int length = Mathf.Clamp(n.Value<int?>("length") ?? 400, 2, ArtifactSchema.MaxTrailPoints);
                trail.ring = new Vector3[length];
                trail.ordered = new Vector3[length];
                line.colorGradient = Fade(color);
                line.positionCount = 0;
                build.AddTrail(trail);
                build.AfterBuild(() => trail.target = build.Nodes[follow].transform);
            }
            else if (n["through"] is JArray ids)
            {
                line.startColor = line.endColor = color;
                line.positionCount = ids.Count;
                trail.joined = new Vector3[ids.Count];
                build.AfterBuild(() =>
                {
                    trail.through = new Transform[ids.Count];
                    for (int i = 0; i < ids.Count; i++) trail.through[i] = build.Nodes[(string)ids[i]].transform;
                    trail.Join();
                });
            }
            else if (n.Value<string>("source") is string slot && build.Slots.TryGetValue(slot, out var poses))
            {
                // Through a generator slot's positions, such as the gates of a slalom: the line to drive.
                line.startColor = line.endColor = color;
                line.positionCount = poses.Count;
                for (int i = 0; i < poses.Count; i++) line.SetPosition(i, poses[i].position);
            }
            else if (n["points"] is JArray points)
            {
                line.startColor = line.endColor = color;
                line.positionCount = points.Count;
                for (int i = 0; i < points.Count; i++) line.SetPosition(i, build.Inputs.Vector(points[i], Vector3.zero));
            }
            return go;
        }

        // The oldest end is index 0, so the line fades out behind the body.
        static Gradient Fade(Color color)
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.35f, 0.6f), new GradientAlphaKey(1f, 1f) });
            return gradient;
        }

        // After the sim has moved its bodies this frame (sims run in Update, lines in LateUpdate).
        void Join()
        {
            for (int i = 0; i < through.Length; i++) joined[i] = through[i].localPosition;
            line.SetPositions(joined);
        }

        public void Clear()
        {
            head = filled = 0;
            if (line != null) line.positionCount = 0;
        }

        void LateUpdate()
        {
            // LineRenderer widths ignore the transform, and the scene may be scaled to fit a volume.
            float scale = transform.lossyScale.x;
            if (scale != shownScale)
            {
                shownScale = scale;
                line.widthMultiplier = width * scale;
            }
            if (through != null)
            {
                Join();
                return;
            }
            if (target == null || ring == null) return;

            var now = target.localPosition;
            if (filled > 0)
            {
                var last = ring[(head + ring.Length - 1) % ring.Length];
                if ((now - last).sqrMagnitude < MinMove * MinMove) return;
            }
            ring[head] = now;
            head = (head + 1) % ring.Length;
            if (filled < ring.Length) filled++;

            int start = (head + ring.Length - filled) % ring.Length;
            int tail = Mathf.Min(filled, ring.Length - start);
            System.Array.Copy(ring, start, ordered, 0, tail);
            if (tail < filled) System.Array.Copy(ring, 0, ordered, tail, filled - tail);
            line.positionCount = filled;
            line.SetPositions(ordered);
        }
    }
}
