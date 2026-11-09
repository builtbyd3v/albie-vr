using System;
using System.Collections.Generic;

namespace Forge.Controllers
{
    /// <summary>
    /// A course for <see cref="MockTruckSim"/> other than its built-in 5-cone corridor: a room slalom from Person 5's
    /// <c>floor_slalom</c> generator, in the course's own frame (x along the course from the start, y to the left).
    /// Cones are circles; walls and furniture are line segments; the truck senses both with the same sphere-cast rays.
    /// A run is scored as the artifact scores it (slalom <c>scene.json</c>):
    /// - each gate is a box beside its cone, counted in order when the truck's centre is inside it;
    /// - the run finishes when the truck enters the finish box with every gate passed. Entering it with a gate missed is
    ///   <c>missed_gate</c>; driving past it is <c>missed_finish</c>.
    /// Build one with <see cref="AddCone"/>, <see cref="AddWall"/>, <see cref="AddGate"/> and <see cref="SetFinish"/>
    /// (Controllers/Editor's <c>RoomCourses</c> makes them from rooms). Arrays, so the sim allocates nothing per step.
    /// </summary>
    public sealed class MockCourse
    {
        public string Name = "course";
        public float ConeRadius = 0.06f;
        /// <summary>The finish box: centred on <see cref="FinishX"/>, <see cref="FinishHalfAlong"/> deep and <see cref="FinishHalfAcross"/> wide (each half).</summary>
        public float FinishX, FinishY, FinishHalfAlong = 0.15f, FinishHalfAcross = 0.45f;

        public float[] ConeX = new float[0], ConeY = new float[0];
        /// <summary>Segments as (ax, ay, bx, by), four floats each.</summary>
        public float[] Walls = new float[0];
        public float[] GateX = new float[0], GateY = new float[0];
        public float GateHalfAlong = 0.1f, GateHalfAcross = 0.3f;

        public int Cones => ConeX.Length;
        public int Gates => GateX.Length;
        public int WallCount => Walls.Length / 4;

        readonly List<float> cx = new List<float>(), cy = new List<float>(), walls = new List<float>(), gx = new List<float>(), gy = new List<float>();

        public MockCourse AddCone(float x, float y) { cx.Add(x); cy.Add(y); ConeX = cx.ToArray(); ConeY = cy.ToArray(); return this; }
        public MockCourse AddWall(float ax, float ay, float bx, float by) { walls.Add(ax); walls.Add(ay); walls.Add(bx); walls.Add(by); Walls = walls.ToArray(); return this; }
        public MockCourse AddGate(float x, float y) { gx.Add(x); gy.Add(y); GateX = gx.ToArray(); GateY = gy.ToArray(); return this; }
        public MockCourse SetFinish(float x, float y) { FinishX = x; FinishY = y; return this; }

        /// <summary>Which side of its cone (cone i) gate <paramref name="i"/> is on: +1 left, -1 right.</summary>
        public int Side(int i) => GateY[i] - ConeY[i] >= 0f ? 1 : -1;

        /// <summary>
        /// The weave a driver follows: <paramref name="weave"/> m out on each gate's side at its cone, joined by half
        /// cosines, eased in from the start line and out to the finish's centre line. Gate i belongs to cone i (any
        /// extra cones, such as gate posts, come after the gates' own).
        /// </summary>
        public float PathY(float x, float weave)
        {
            int n = Math.Min(Gates, Cones);
            if (n == 0) return FinishY;
            float first = ConeX[0], last = ConeX[n - 1];
            if (x <= first) return Blend(0f, ConeY[0] + Side(0) * weave, Clamp01(x / Math.Max(first, 1e-3f)));
            if (x >= last) return Blend(ConeY[n - 1] + Side(n - 1) * weave, FinishY, Clamp01((x - last) / Math.Max(FinishX - last, 1e-3f)));
            int i = 0;
            while (i < n - 2 && x > ConeX[i + 1]) i++;
            float f = (x - ConeX[i]) / Math.Max(ConeX[i + 1] - ConeX[i], 1e-3f);
            return Blend(ConeY[i] + Side(i) * weave, ConeY[i + 1] + Side(i + 1) * weave, Clamp01(f));
        }

        static float Blend(float a, float b, float f) => a + (b - a) * (1f - (float)Math.Cos(Math.PI * f)) * 0.5f;
        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

        // ---- geometry the sim uses ---------------------------------------------------------------------------------

        /// <summary>True when a circle of <paramref name="radius"/> at (x, y) touches a cone or a wall.</summary>
        public bool Touches(float x, float y, float radius)
        {
            for (int i = 0; i < ConeX.Length; i++)
            {
                float dx = x - ConeX[i], dy = y - ConeY[i], r = ConeRadius + radius;
                if (dx * dx + dy * dy < r * r) return true;
            }
            for (int w = 0; w < Walls.Length; w += 4)
                if (SegmentDistance2(x, y, Walls[w], Walls[w + 1], Walls[w + 2], Walls[w + 3]) < radius * radius) return true;
            return false;
        }

        /// <summary>
        /// How far the centre of a beam of <paramref name="beam"/> m radius travels along the unit ray from (px, py)
        /// before the beam touches a cone or a wall, capped at <paramref name="range"/> (a sphere cast). Pass 0 for a
        /// plain ray: the sim does when anything is already inside the beam (<see cref="Touches"/>), as RayFan does.
        /// </summary>
        public float Cast(float px, float py, float dx, float dy, float beam, float range)
        {
            float best = range;
            for (int i = 0; i < ConeX.Length; i++)
                best = RayCircle(px, py, dx, dy, ConeX[i], ConeY[i], ConeRadius + beam, best);
            for (int w = 0; w < Walls.Length; w += 4)
                best = RayCapsule(px, py, dx, dy, Walls[w], Walls[w + 1], Walls[w + 2], Walls[w + 3], beam, best);
            return best;
        }

        public bool InGate(int i, float x, float y) => Math.Abs(x - GateX[i]) <= GateHalfAlong && Math.Abs(y - GateY[i]) <= GateHalfAcross;
        public bool InFinish(float x, float y) => Math.Abs(x - FinishX) <= FinishHalfAlong && Math.Abs(y - FinishY) <= FinishHalfAcross;
        public bool PastFinish(float x) => x > FinishX + FinishHalfAlong;

        static float RayCircle(float px, float py, float dx, float dy, float cx, float cy, float r, float best)
        {
            float ox = cx - px, oy = cy - py;
            float along = ox * dx + oy * dy;
            if (along <= 0) return best;
            float miss2 = ox * ox + oy * oy - along * along;
            if (miss2 > r * r) return best;
            float t = Math.Max(0f, along - (float)Math.Sqrt(r * r - miss2));
            return t < best ? t : best;
        }

        // A segment swept by a circle of radius r: its two end circles and its two sides offset by r.
        static float RayCapsule(float px, float py, float dx, float dy, float ax, float ay, float bx, float by, float r, float best)
        {
            float ex = bx - ax, ey = by - ay, len = (float)Math.Sqrt(ex * ex + ey * ey);
            if (len < 1e-6f) return RayCircle(px, py, dx, dy, ax, ay, r, best);
            float ux = ex / len, uy = ey / len, nx = -uy, ny = ux;
            float denom = dx * nx + dy * ny;
            if (Math.Abs(denom) > 1e-9f)
                for (int s = -1; s <= 1; s += 2)
                {
                    float qx = ax + s * r * nx, qy = ay + s * r * ny;
                    float t = ((qx - px) * nx + (qy - py) * ny) / denom;
                    if (t <= 0 || t >= best) continue;
                    float hx = px + t * dx - ax, hy = py + t * dy - ay, along = hx * ux + hy * uy;
                    if (along >= 0 && along <= len) best = t;
                }
            if (r > 0)
            {
                best = RayCircle(px, py, dx, dy, ax, ay, r, best);
                best = RayCircle(px, py, dx, dy, bx, by, r, best);
            }
            return best;
        }

        static float SegmentDistance2(float px, float py, float ax, float ay, float bx, float by)
        {
            float ex = bx - ax, ey = by - ay, l2 = ex * ex + ey * ey;
            float t = l2 < 1e-12f ? 0f : ((px - ax) * ex + (py - ay) * ey) / l2;
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            float qx = ax + t * ex - px, qy = ay + t * ey - py;
            return qx * qx + qy * qy;
        }
    }
}
