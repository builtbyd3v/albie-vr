using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>Small, allocation-free helpers for the Quest Play Area polygon.</summary>
    public static class PlayArea
    {
        public const float Margin = 0.3f;
        static bool warned;

        public static Vector2[] WorldPolygon()
        {
            if (OVRManager.loadedXRDevice != OVRManager.XRDevice.Oculus)
                return Unavailable();
            if (OVRManager.boundary == null || !OVRManager.boundary.GetConfigured())
                return Unavailable();
            var points = OVRManager.boundary.GetGeometry(OVRBoundary.BoundaryType.PlayArea);
            if (points == null || points.Length < 3)
                return Unavailable();
            var rig = Object.FindFirstObjectByType<OVRCameraRig>();
            if (rig == null || rig.trackingSpace == null)
                return Unavailable();
            var polygon = new Vector2[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                var p = rig.trackingSpace.TransformPoint(points[i]);
                polygon[i] = new Vector2(p.x, p.z);
            }
            return polygon;
        }

        static Vector2[] Unavailable()
        {
            if (!warned)
            {
                warned = true;
                Debug.Log("[PlayArea] Quest Play Area boundary unavailable; floor placement has no boundary limit.");
            }
            return null;
        }

        /// <summary>How far inside <paramref name="room"/>'s boundary a course keeps: less for an area drawn on purpose.</summary>
        public static float MarginOf(IFloorPlan room) => room is PlayAreaPlan area ? area.Margin : Margin;

        /// <summary>"your Quest boundary" or "the play area you drew", for what Albie says about <paramref name="room"/>'s boundary.</summary>
        public static string Words(IFloorPlan room) => room is PlayAreaPlan area ? area.Words : "your Quest boundary";

        public static bool Inside(Vector2[] polygon, Vector3 point, float clearance)
        {
            if (polygon == null || polygon.Length < 3) return true;
            var p = new Vector2(point.x, point.z);
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                var a = polygon[i];
                var b = polygon[j];
                if (((a.y > p.y) != (b.y > p.y)) &&
                    p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
                if (DistanceToSegment(p, a, b) < clearance) return false;
            }
            return inside;
        }

        static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return Vector2.Distance(p, a + ab * t);
        }
    }
}
