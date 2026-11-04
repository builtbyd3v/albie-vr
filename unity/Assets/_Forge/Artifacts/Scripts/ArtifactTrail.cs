using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// A <c>line</c> node: fixed <c>points</c>, or with <c>follow</c> a fading trail behind another node. The trail is a
    /// preallocated ring, so it costs nothing to the garbage collector however long it runs.
    /// </summary>
    public sealed class ArtifactTrail : MonoBehaviour
    {
        const float MinMove = 0.004f;  // scene units between samples, so a paused body doesn't eat the trail

        LineRenderer line;
        Transform target;
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
