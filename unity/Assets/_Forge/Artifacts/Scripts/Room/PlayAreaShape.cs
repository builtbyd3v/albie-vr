using System.Collections.Generic;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// Polygon maths for a play area on the floor (world xz, metres): area, a check that a drawn outline doesn't cross
    /// itself, the rectangle dragged out from one corner, snapping, and the nearest corner to a point. Uses only
    /// <see cref="Vector2"/> and <see cref="Mathf"/>, no scene, so it is tested on its own. No allocations except
    /// <see cref="Rectangle"/>'s result when no buffer is given.
    /// </summary>
    public static class PlayAreaShape
    {
        /// <summary>Corners a drawn area can have.</summary>
        public const int MaxCorners = 16;
        /// <summary>A drawn area smaller than this (m²) can't hold any course, so Done refuses it.</summary>
        public const float MinArea = 1.0f;
        /// <summary>
        /// A Quest boundary smaller than this (m²) is a stationary (seated) one or close to it: the roomscale minimum is
        /// 2 x 2 m, and with the 0.3 m margin a course needs inside it, nothing but a parking bay fits in a smaller one.
        /// </summary>
        public const float TinyArea = 4.5f;
        /// <summary>Drawn corners snap to this grid (m), so a wobbly pinch still makes a straight edge.</summary>
        public const float Snap = 0.05f;

        /// <summary>The area (m², always positive) of a polygon; 0 for fewer than 3 points.</summary>
        public static float Area(IList<Vector2> polygon, int count = -1)
        {
            int n = count < 0 ? (polygon != null ? polygon.Count : 0) : count;
            if (n < 3) return 0f;
            float twice = 0f;
            for (int i = 0, j = n - 1; i < n; j = i++)
                twice += polygon[j].x * polygon[i].y - polygon[i].x * polygon[j].y;
            return Mathf.Abs(twice) * 0.5f;
        }

        /// <summary>Inside the polygon (even-odd), ignoring any margin.</summary>
        public static bool Contains(IList<Vector2> polygon, Vector2 p)
        {
            if (polygon == null || polygon.Count < 3) return false;
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var a = polygon[i];
                var b = polygon[j];
                if (((a.y > p.y) != (b.y > p.y)) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>
        /// True when no two edges that aren't neighbours cross or touch, and no corner repeats: an outline a person can
        /// walk round. A bow tie (corners placed out of order) is false.
        /// </summary>
        public static bool IsSimple(IList<Vector2> polygon, int count = -1)
        {
            int n = count < 0 ? (polygon != null ? polygon.Count : 0) : count;
            if (n < 3) return false;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if ((polygon[i] - polygon[j]).sqrMagnitude < 1e-6f) return false;
            if (n == 3) return Area(polygon, n) > 1e-6f;
            for (int i = 0; i < n; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % n];
                for (int j = i + 1; j < n; j++)
                {
                    if (j == i || (j + 1) % n == i || j == (i + 1) % n) continue;  // neighbours share a corner
                    if (Cross(a, b, polygon[j], polygon[(j + 1) % n])) return false;
                }
            }
            return true;
        }

        // Segments ab and cd intersect (including touching).
        static bool Cross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float d1 = Side(c, d, a), d2 = Side(c, d, b), d3 = Side(a, b, c), d4 = Side(a, b, d);
            if (((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) && ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f))) return true;
            return (Mathf.Abs(d1) < 1e-7f && OnSegment(c, d, a)) || (Mathf.Abs(d2) < 1e-7f && OnSegment(c, d, b))
                || (Mathf.Abs(d3) < 1e-7f && OnSegment(a, b, c)) || (Mathf.Abs(d4) < 1e-7f && OnSegment(a, b, d));
        }

        static float Side(Vector2 a, Vector2 b, Vector2 p) => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

        static bool OnSegment(Vector2 a, Vector2 b, Vector2 p) =>
            p.x >= Mathf.Min(a.x, b.x) - 1e-6f && p.x <= Mathf.Max(a.x, b.x) + 1e-6f && p.y >= Mathf.Min(a.y, b.y) - 1e-6f && p.y <= Mathf.Max(a.y, b.y) + 1e-6f;

        /// <summary>
        /// The rectangle with opposite corners <paramref name="from"/> and <paramref name="to"/>, its sides along and
        /// across <paramref name="yawDegrees"/> (the room's axis: yaw 0 is +z), into <paramref name="into"/> (4 long).
        /// </summary>
        public static void Rectangle(Vector2 from, Vector2 to, float yawDegrees, Vector2[] into)
        {
            float r = yawDegrees * Mathf.Deg2Rad;
            var along = new Vector2(Mathf.Sin(r), Mathf.Cos(r));  // +z turned by yaw, as Quaternion.Euler(0, yaw, 0) * forward
            var across = new Vector2(along.y, -along.x);
            var d = to - from;
            float u = d.x * along.x + d.y * along.y, v = d.x * across.x + d.y * across.y;
            into[0] = from;
            into[1] = from + along * u;
            into[2] = from + along * u + across * v;
            into[3] = from + across * v;
        }

        public static Vector2[] Rectangle(Vector2 from, Vector2 to, float yawDegrees)
        {
            var corners = new Vector2[4];
            Rectangle(from, to, yawDegrees, corners);
            return corners;
        }

        /// <summary>The point on the <see cref="Snap"/> grid nearest <paramref name="p"/>.</summary>
        public static Vector2 Snapped(Vector2 p) => new Vector2(Mathf.Round(p.x / Snap) * Snap, Mathf.Round(p.y / Snap) * Snap);

        /// <summary>The index of the corner within <paramref name="radius"/> of <paramref name="p"/>, nearest first, or -1.</summary>
        public static int Nearest(IList<Vector2> corners, int count, Vector2 p, float radius)
        {
            int best = -1;
            float bestSq = radius * radius;
            for (int i = 0; i < count; i++)
            {
                float sq = (corners[i] - p).sqrMagnitude;
                if (sq <= bestSq)
                {
                    bestSq = sq;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>Why an outline can't be used yet, in words for the panel, or null when it can.</summary>
        public static string Problem(IList<Vector2> corners, int count)
        {
            if (count < 3) return "Place at least 3 corners.";
            if (!IsSimple(corners, count)) return "The outline crosses itself. Drag a corner, or Undo, so the edges don't cross.";
            if (Area(corners, count) < MinArea) return "That area is too small for any course. Make it at least 1 m².";
            return null;
        }
    }
}
