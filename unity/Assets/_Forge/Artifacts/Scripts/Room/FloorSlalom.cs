using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// The <c>floor_slalom</c> generator (C-12 section 3.3): turns a room into cone, gate, start and finish positions.
    /// The agent never computes positions; this does, so a bad model output can't put a cone inside the couch.
    ///
    /// The course runs along its local +z from the start (z = 0): a line of cones <c>spacing</c> apart, a gate beside
    /// each cone on alternating sides (the side the truck passes), and the finish one spacing past the last cone.
    /// Every slot is on the floor (y = 0); a node's own <c>pos</c> lifts it (a cone's centre is half its height up).
    ///
    /// The spacing never goes below 1.0 m by default, even in a tight room: the truck turns in about 0.24 m, and closer
    /// cones can't be weaved at all (PROJECT_CONTEXT 16.8, "room slalom"; until 2026-11-06 it shrank to 0.6 m, then 0.5 m).
    ///
    /// Fit rules (shrink and say so, never fail silently): the requested size; then less spacing, down to the minimum;
    /// then fewer gates, down to the minimum; then a narrow course (less free floor either side, the gates where they
    /// were) with the spacing down to the tight minimum; then <c>no_free_floor</c>. No floor data at all is
    /// <c>no_room_data</c>.
    /// </summary>
    public static class FloorSlalom
    {
        public const float Cell = 0.1f;          // free-floor grid and footprint sampling (m)
        public const float EndMargin = 0.3f;     // floor before the start and after the finish (m): the truck's length
        const float CentreStride = 0.2f;         // course centres tried (m)
        const float YawStep = 15f;

        public struct Settings
        {
            public int Gates, MinGates;
            public float Spacing, MinSpacing, Lane, Clearance;
            // A tight room: the free floor either side narrows to NarrowLane, then the spacing goes down to TightSpacing.
            public float NarrowLane, TightSpacing;
            public string Around;  // an MRUK label, or null

            public static Settings Default => new Settings
                { Gates = 5, MinGates = 3, Spacing = 1.2f, MinSpacing = 1.0f, Lane = 0.6f, Clearance = 0.15f, NarrowLane = 0.4f, TightSpacing = 1.0f };
        }

        public sealed class Result
        {
            public bool Ok;
            public string Code, Message;           // Code is null when Ok
            public int Gates;                      // gates, or hoops for room_hoops
            public float Spacing;
            public float Lane;                     // the free floor either side of the centre line it was placed with (m)
            public bool Shrunk, Narrow, AroundFound;
            public Vector3 Origin;                 // the start, on the floor (world)
            public float Yaw;                      // the course's direction (degrees, world)
            public Dictionary<string, List<Pose>> Slots;
            public int Tried;                      // placements tested, for the checks
        }

        /// <summary>Course length from the floor before the start to the floor after the finish.</summary>
        public static float Length(int gates, float spacing) => spacing * (gates + 1) + 2f * EndMargin;

        public static Result Fit(IFloorPlan room, Settings s, string id = "course")
        {
            if (room == null || !room.HasFloor) return NoRoomData();
            s.MinGates = Mathf.Clamp(s.MinGates, 1, Mathf.Max(1, s.Gates));
            s.MinSpacing = Mathf.Clamp(s.MinSpacing, 0.1f, Mathf.Max(0.1f, s.Spacing));
            Tight(ref s.NarrowLane, ref s.TightSpacing, s.Lane, s.MinSpacing);
            // Gates stay half the full lane out, so a narrow course scores the same; only the floor it needs shrinks.
            Func<int, float, Dictionary<string, List<Pose>>> slots = (gates, spacing) => Slots(id, gates, spacing, s.Lane);
            var fit = Search(room, s.Gates, s.MinGates, s.Spacing, s.MinSpacing, s.Lane, s.Clearance, s.Around, slots, true);
            if (!fit.Ok && (s.NarrowLane < s.Lane || s.TightSpacing < s.MinSpacing))
                fit = Narrow(fit, Search(room, s.MinGates, s.MinGates, s.MinSpacing, s.TightSpacing, s.NarrowLane, s.Clearance, s.Around, slots, true));
            bool boundaryTooSmall = !fit.Ok && room.Boundary != null && FitsWithoutBoundary(room, s, slots);
            if (boundaryTooSmall)
                fit.Message = "Your Quest boundary is too small for a slalom here. Redraw it bigger, or ask for hoops.";
            if (fit.Ok) fit.Message = Message(fit.Shrunk, fit.Narrow, fit.Gates, fit.Spacing, s.Around, fit.AroundFound);
            else if (!boundaryTooSmall) fit.Message = string.Format(CultureInfo.InvariantCulture,
                "There isn't enough clear floor here for a slalom. Even the smallest course ({0} gates, {1:0.0} m apart) needs a clear strip about {2:0.0} by {3:0.0} m, with {4:0} cm to spare around it.",
                s.MinGates, s.TightSpacing, Length(s.MinGates, s.TightSpacing), 2f * s.NarrowLane, s.Clearance * 100f);
            return fit;
        }

        /// <summary>The tight-room limits can only be smaller than the normal ones (0 or less means "no tighter").</summary>
        internal static void Tight(ref float narrowLane, ref float tightSpacing, float lane, float minSpacing)
        {
            narrowLane = narrowLane > 0f ? Mathf.Clamp(narrowLane, 0.1f, lane) : lane;
            tightSpacing = tightSpacing > 0f ? Mathf.Clamp(tightSpacing, 0.1f, minSpacing) : minSpacing;
        }

        internal static Result Narrow(Result wide, Result fit)
        {
            fit.Tried += wide.Tried;
            if (fit.Ok) fit.Shrunk = fit.Narrow = true;
            return fit;
        }

        internal static Result NoRoomData() =>
            Fail("no_room_data", "I don't have a scan of this room, so I can't place a course. Run Space Setup on the headset first.");

        /// <summary>
        /// The fit rules' search, shared by every straight-line course: <paramref name="count"/> markers
        /// <paramref name="spacing"/> apart along +z, with ±<paramref name="lane"/> of free floor either side, end to end.
        /// Fewer spacing first, then fewer markers. A failure has code <c>no_free_floor</c> and no message.
        /// </summary>
        internal static Result Search(IFloorPlan room, int count, int minCount, float spacing, float minSpacing, float lane, float clearance,
                                      string around, Func<int, float, Dictionary<string, List<Pose>>> slots, bool useBoundary = true)
        {
            var grid = new FloorGrid(room, clearance, useBoundary);
            Vector3 target = grid.Centroid;
            float baseYaw = room.AxisYaw;
            bool aroundFound = false;
            if (!string.IsNullOrEmpty(around) && room.TryFind(around, out var aroundAt, out float aroundYaw, out _))
            {
                aroundFound = true;
                target = aroundAt;
                baseYaw = aroundYaw;
            }
            var centres = grid.Centres(target);
            // Along or across the room (or the couch) first; an angled course only when no square one fits at that size.
            var passes = new[] { new[] { baseYaw, baseYaw + 180f, baseYaw + 90f, baseYaw + 270f }, Angled(baseYaw) };
            int tried = 0;

            for (int n = count; n >= minCount; n--)
                foreach (float gap in Spacings(spacing, minSpacing))
                {
                    if (Length(n, gap) > grid.Diagonal) continue;
                    foreach (var yaws in passes)
                    foreach (var centre in centres)
                        foreach (float yaw in yaws)
                        {
                            tried++;
                            var forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                            var origin = centre - forward * (gap * (n + 1) * 0.5f);
                            if (!grid.Clear(origin, yaw, n, gap, lane)) continue;
                            return new Result
                            {
                                Ok = true, Gates = n, Spacing = gap, Lane = lane, AroundFound = aroundFound,
                                Shrunk = n != count || Mathf.Abs(gap - spacing) > 0.001f,
                                Origin = new Vector3(origin.x, room.FloorY, origin.z), Yaw = Normalise(yaw),
                                Slots = slots(n, gap), Tried = tried,
                            };
                        }
                }
            var fail = Fail("no_free_floor", null);
            fail.AroundFound = aroundFound;
            fail.Tried = tried;
            return fail;
        }

        /// <summary>Whether a saved course still has free floor (furniture can move between sessions).</summary>
        public static bool StillFits(IFloorPlan room, Vector3 origin, float yaw, int gates, float spacing, Settings s, float lane = 0f) =>
            StillFits(room, origin, yaw, gates, spacing, lane > 0f ? lane : s.Lane, s.Clearance);

        internal static bool StillFitsWithoutBoundary(IFloorPlan room, Vector3 origin, float yaw, int gates, float spacing, Settings s, float lane = 0f) =>
            StillFitsWithoutBoundary(room, origin, yaw, gates, spacing, lane > 0f ? lane : s.Lane, s.Clearance);

        internal static bool StillFits(IFloorPlan room, Vector3 origin, float yaw, int count, float spacing, float lane, float clearance)
            => StillFits(room, origin, yaw, count, spacing, lane, clearance, true);

        internal static bool StillFitsWithoutBoundary(IFloorPlan room, Vector3 origin, float yaw, int count, float spacing, float lane, float clearance)
            => StillFits(room, origin, yaw, count, spacing, lane, clearance, false);

        static bool StillFits(IFloorPlan room, Vector3 origin, float yaw, int count, float spacing, float lane, float clearance, bool useBoundary)
        {
            if (room == null || !room.HasFloor) return false;
            return new FloorGrid(room, clearance, useBoundary).Clear(origin, yaw, count, spacing, lane);
        }

        static bool FitsWithoutBoundary(IFloorPlan room, Settings s, Func<int, float, Dictionary<string, List<Pose>>> slots)
        {
            var fit = Search(room, s.Gates, s.MinGates, s.Spacing, s.MinSpacing, s.Lane, s.Clearance, s.Around, slots, false);
            if (!fit.Ok && (s.NarrowLane < s.Lane || s.TightSpacing < s.MinSpacing))
                fit = Narrow(fit, Search(room, s.MinGates, s.MinGates, s.MinSpacing, s.TightSpacing, s.NarrowLane, s.Clearance, s.Around, slots, false));
            return fit.Ok;
        }

        /// <summary>The slots, local to the course origin, all on the floor.</summary>
        public static Dictionary<string, List<Pose>> Slots(string id, int gates, float spacing, float lane)
        {
            var cones = new List<Pose>();
            var gateList = new List<Pose>();
            for (int i = 1; i <= gates; i++)
            {
                cones.Add(new Pose(new Vector3(0f, 0f, spacing * i), Quaternion.identity));
                gateList.Add(new Pose(new Vector3((i % 2 == 1 ? 0.5f : -0.5f) * lane, 0f, spacing * i), Quaternion.identity));
            }
            return new Dictionary<string, List<Pose>>
            {
                [id + ".cones"] = cones,
                [id + ".gates"] = gateList,
                [id + ".start"] = new List<Pose> { new Pose(Vector3.zero, Quaternion.identity) },
                [id + ".finish"] = new List<Pose> { new Pose(new Vector3(0f, 0f, spacing * (gates + 1)), Quaternion.identity) },
            };
        }

        static IEnumerable<float> Spacings(float requested, float min)
        {
            float s = requested;
            yield return Round(s);
            while (s - 0.1f > min + 0.001f)
            {
                s -= 0.1f;
                yield return Round(s);
            }
            if (Mathf.Abs(Round(s) - Round(min)) > 0.001f) yield return Round(min);
        }

        // Every 15° that isn't along or across.
        static float[] Angled(float baseYaw)
        {
            var list = new List<float>();
            for (float a = YawStep; a < 360f; a += YawStep)
                if (Mathf.Abs(a % 90f) > 0.01f) list.Add(baseYaw + a);
            return list.ToArray();
        }

        static string Message(bool shrunk, bool narrow, int gates, float spacing, string around, bool aroundFound)
        {
            bool missing = !string.IsNullOrEmpty(around) && !aroundFound;
            string thing = Thing(around);
            string size = string.Format(CultureInfo.InvariantCulture, "{0} gates, {1:0.0#} m apart", gates, spacing);
            if (narrow)
                return (missing ? $"I can't find {thing}, and this" : "This") +
                       $" room is very tight, so I made a short, narrow course: {size}, with less room either side. Drive carefully near the furniture.";
            if (shrunk && missing) return $"This room is tight and I can't find {thing}, so I made a shorter course: {size}.";
            if (shrunk) return $"This room is tight, so I made a shorter course: {size}.";
            if (missing) return $"I can't find {thing}, so I put the course on the most open floor: {size}.";
            return null;
        }

        static string Thing(string label)
        {
            switch (label)
            {
                case null: return "it";
                case "COUCH": return "a couch";
                case "TABLE": return "a table";
                case "BED": return "a bed";
                case "STORAGE": return "a cupboard";
                default: return "a " + label.ToLowerInvariant().Replace('_', ' ');
            }
        }

        static Result Fail(string code, string message) => new Result { Ok = false, Code = code, Message = message };
        static float Round(float v) => Mathf.Round(v * 100f) / 100f;
        static float Normalise(float yaw) => Mathf.Repeat(yaw, 360f);

        /// <summary>
        /// Free floor sampled once on a 10 cm grid, so testing thousands of placements is array lookups, not thousands
        /// of MRUK queries. A cell is free if its centre is free with the clearance.
        /// </summary>
        sealed class FloorGrid
        {
            readonly bool[] free;
            readonly int nx, nz;
            readonly Vector3 min;
            readonly float y;
            public readonly Vector3 Centroid;
            public readonly float Diagonal;
            readonly List<Vector3> freeCells = new List<Vector3>();

            public FloorGrid(IFloorPlan room, float clearance, bool useBoundary)
            {
                var b = room.FloorBounds;
                min = b.min;
                y = room.FloorY;
                nx = Mathf.Max(1, Mathf.CeilToInt(b.size.x / Cell));
                nz = Mathf.Max(1, Mathf.CeilToInt(b.size.z / Cell));
                Diagonal = new Vector2(b.size.x, b.size.z).magnitude;
                free = new bool[nx * nz];
                var sum = Vector3.zero;
                for (int iz = 0; iz < nz; iz++)
                    for (int ix = 0; ix < nx; ix++)
                    {
                        var p = new Vector3(min.x + (ix + 0.5f) * Cell, y, min.z + (iz + 0.5f) * Cell);
                        if (!room.IsFree(p, clearance)) continue;
                        if (useBoundary && room.Boundary != null && !PlayArea.Inside(room.Boundary, p, clearance + PlayArea.Margin)) continue;
                        free[iz * nx + ix] = true;
                        freeCells.Add(p);
                        sum += p;
                    }
                Centroid = freeCells.Count > 0 ? sum / freeCells.Count : b.center;
            }

            bool Free(Vector3 p)
            {
                int ix = Mathf.FloorToInt((p.x - min.x) / Cell), iz = Mathf.FloorToInt((p.z - min.z) / Cell);
                return ix >= 0 && iz >= 0 && ix < nx && iz < nz && free[iz * nx + ix];
            }

            /// <summary>Free cells every <see cref="CentreStride"/>, nearest <paramref name="target"/> first.</summary>
            public List<Vector3> Centres(Vector3 target)
            {
                int stride = Mathf.Max(1, Mathf.RoundToInt(CentreStride / Cell));
                var list = new List<Vector3>();
                for (int iz = 0; iz < nz; iz += stride)
                    for (int ix = 0; ix < nx; ix += stride)
                        if (free[iz * nx + ix]) list.Add(new Vector3(min.x + (ix + 0.5f) * Cell, y, min.z + (iz + 0.5f) * Cell));
                var t = new Vector3(target.x, y, target.z);
                list.Sort((a, b) => (a - t).sqrMagnitude.CompareTo((b - t).sqrMagnitude));
                return list;
            }

            /// <summary>The whole footprint (the lane each side of the cones, end to end) is on free floor.</summary>
            public bool Clear(Vector3 origin, float yaw, int gates, float spacing, float lane)
            {
                var r = Quaternion.Euler(0f, yaw, 0f);
                var forward = r * Vector3.forward;
                var right = r * Vector3.right;
                float end = spacing * (gates + 1) + EndMargin;
                for (float z = -EndMargin; z <= end + 0.001f; z += Cell)
                    for (float x = -lane; x <= lane + 0.001f; x += Cell)
                        if (!Free(origin + forward * z + right * x)) return false;
                return true;
            }
        }
    }
}
