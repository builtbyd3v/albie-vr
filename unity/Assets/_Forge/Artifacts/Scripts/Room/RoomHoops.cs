using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// The <c>room_hoops</c> generator (C-12 section 3.3): a line of standing hoops for a drone, on free floor and under
    /// the ceiling. Same search and fit rules as <see cref="FloorSlalom"/>: the requested size, then less spacing, then
    /// fewer hoops, then a straight line of hoops (no weave, a narrower strip) with the spacing down to the tight
    /// minimum, then <c>no_free_floor</c>; no floor data at all is <c>no_room_data</c>.
    ///
    /// The course runs along its local +z from the start (z = 0): a hoop every <c>spacing</c>, weaving
    /// ±<see cref="Weave"/> sideways on alternating sides, and the finish one spacing past the last hoop. Every slot is
    /// on the floor (y = 0); the scene lifts each hoop's frame so its centre is <c>height_m</c> up. The footprint under
    /// the frames' posts has to be free floor, and the top of the frame has to clear the ceiling by <c>clearance_m</c>
    /// (<c>no_headroom</c> if it can't). A scan with no ceiling isn't a failure: Albie says it couldn't check.
    /// </summary>
    public static class RoomHoops
    {
        public const float Post = 0.04f;  // frame thickness (m)

        public struct Settings
        {
            public int Hoops, MinHoops;
            public float Spacing, MinSpacing, Diameter, Height, Clearance;
            public float TightSpacing;  // a tight room: the hoops stand in a straight line, down to this spacing

            public static Settings Default => new Settings
                { Hoops = 4, MinHoops = 3, Spacing = 1.0f, MinSpacing = 0.6f, Diameter = 0.7f, Height = 1.0f, Clearance = 0.15f, TightSpacing = 0.5f };
        }

        /// <summary>How far each hoop sits off the centre line, alternating sides.</summary>
        public static float Weave(float diameter) => 0.3f * diameter;
        /// <summary>Free floor needed either side of the centre line: the weave, half a hoop, and the post.</summary>
        public static float Lane(Settings s) => Weave(s.Diameter) + StraightLane(s);
        /// <summary>The same for a straight line of hoops (a tight room): half a hoop and the post.</summary>
        public static float StraightLane(Settings s) => s.Diameter * 0.5f + Post;
        /// <summary>Floor to the top of a hoop's frame.</summary>
        public static float Top(Settings s) => s.Height + s.Diameter * 0.5f + Post * 0.5f;

        public static FloorSlalom.Result Fit(IFloorPlan room, Settings s, string id = "course")
        {
            if (room == null || !room.HasFloor) return FloorSlalom.NoRoomData();
            s.MinHoops = Mathf.Clamp(s.MinHoops, 1, Mathf.Max(1, s.Hoops));
            s.MinSpacing = Mathf.Clamp(s.MinSpacing, 0.1f, Mathf.Max(0.1f, s.Spacing));
            s.Height = Mathf.Max(s.Height, s.Diameter * 0.5f + Post);

            float ceiling = room.CeilingHeight, need = Top(s) + s.Clearance;
            bool noCeiling = float.IsNaN(ceiling);
            if (!noCeiling && need > ceiling + 0.001f)
                return new FloorSlalom.Result
                {
                    Code = "no_headroom",
                    Message = string.Format(CultureInfo.InvariantCulture,
                        "The ceiling here is only {0:0.00} m, and the hoops need {1:0.00} m: they stand {2:0.00} m tall, with {3:0} cm to spare above them. They don't fit in this room.",
                        ceiling, need, Top(s), s.Clearance * 100f),
                };

            float lane = Lane(s), straight = StraightLane(s);
            s.TightSpacing = s.TightSpacing > 0f ? Mathf.Clamp(s.TightSpacing, 0.1f, s.MinSpacing) : s.MinSpacing;
            var slots = (System.Func<int, float, Dictionary<string, List<Pose>>>)( (hoops, spacing) => Slots(id, hoops, spacing, s.Diameter));
            var fit = FloorSlalom.Search(room, s.Hoops, s.MinHoops, s.Spacing, s.MinSpacing, lane, s.Clearance, null, slots, true);
            if (!fit.Ok)
                fit = FloorSlalom.Narrow(fit, FloorSlalom.Search(room, s.MinHoops, s.MinHoops, s.MinSpacing, s.TightSpacing, straight, s.Clearance, null,
                                                                 (hoops, spacing) => Slots(id, hoops, spacing, s.Diameter, true), true));
            bool boundaryTooSmall = !fit.Ok && room.Boundary != null && FitsWithoutBoundary(room, s, id, lane, straight);
            if (!fit.Ok)
            {
                fit.BoundaryLimited = boundaryTooSmall;
                fit.Message = boundaryTooSmall ? FloorSlalom.BoundaryMessage(room, "a hoop course") : string.Format(CultureInfo.InvariantCulture,
                    "There isn't enough clear floor here for a hoop course. Even the smallest course ({0} hoops in a straight line, {1:0.0} m apart) needs a clear strip about {2:0.0} by {3:0.0} m, with {4:0} cm to spare around it.",
                    s.MinHoops, s.TightSpacing, FloorSlalom.Length(s.MinHoops, s.TightSpacing), 2f * straight, s.Clearance * 100f);
                return fit;
            }
            fit.Message = Message(fit.Shrunk, fit.Narrow, fit.Gates, fit.Spacing, noCeiling, Top(s));
            return fit;
        }

        static bool FitsWithoutBoundary(IFloorPlan room, Settings s, string id, float lane, float straight)
        {
            var slots = (System.Func<int, float, Dictionary<string, List<Pose>>>)( (hoops, spacing) => Slots(id, hoops, spacing, s.Diameter));
            var fit = FloorSlalom.Search(room, s.Hoops, s.MinHoops, s.Spacing, s.MinSpacing, lane, s.Clearance, null, slots, false);
            if (!fit.Ok) fit = FloorSlalom.Narrow(fit, FloorSlalom.Search(room, s.MinHoops, s.MinHoops, s.MinSpacing, s.TightSpacing, straight, s.Clearance, null,
                                                                          (hoops, spacing) => Slots(id, hoops, spacing, s.Diameter, true), false));
            return fit.Ok;
        }

        /// <summary>Whether a saved hoop course still has free floor and headroom.</summary>
        /// <param name="lane">The lane it was placed with (a straight course is narrower); 0 means the weaving one.</param>
        public static bool StillFits(IFloorPlan room, Vector3 origin, float yaw, int hoops, float spacing, Settings s, float lane = 0f)
        {
            if (room == null || !room.HasFloor) return false;
            float ceiling = room.CeilingHeight;
            if (!float.IsNaN(ceiling) && Top(s) + s.Clearance > ceiling + 0.001f) return false;
            return FloorSlalom.StillFits(room, origin, yaw, hoops, spacing, lane > 0f ? lane : Lane(s), s.Clearance);
        }

        internal static bool StillFitsWithoutBoundary(IFloorPlan room, Vector3 origin, float yaw, int hoops, float spacing, Settings s, float lane = 0f)
        {
            if (room == null || !room.HasFloor) return false;
            float ceiling = room.CeilingHeight;
            if (!float.IsNaN(ceiling) && Top(s) + s.Clearance > ceiling + 0.001f) return false;
            return FloorSlalom.StillFitsWithoutBoundary(room, origin, yaw, hoops, spacing, lane > 0f ? lane : Lane(s), s.Clearance);
        }

        /// <summary>The slots, local to the course origin, all on the floor.</summary>
        public static Dictionary<string, List<Pose>> Slots(string id, int hoops, float spacing, float diameter, bool straight = false)
        {
            float weave = straight ? 0f : Weave(diameter);
            var list = new List<Pose>();
            for (int i = 1; i <= hoops; i++)
                list.Add(new Pose(new Vector3(i % 2 == 1 ? weave : -weave, 0f, spacing * i), Quaternion.identity));
            return new Dictionary<string, List<Pose>>
            {
                [id + ".hoops"] = list,
                [id + ".start"] = new List<Pose> { new Pose(Vector3.zero, Quaternion.identity) },
                [id + ".finish"] = new List<Pose> { new Pose(new Vector3(0f, 0f, spacing * (hoops + 1)), Quaternion.identity) },
            };
        }

        static string Message(bool shrunk, bool straight, int hoops, float spacing, bool noCeiling, float top)
        {
            string size = string.Format(CultureInfo.InvariantCulture, "{0} hoops, {1:0.0#} m apart", hoops, spacing);
            string ceiling = string.Format(CultureInfo.InvariantCulture,
                "I can't find the ceiling in this scan, so check the hoops clear it: they stand {0:0.00} m tall.", top);
            if (straight)
                return $"This room is very tight, so I put the hoops in a straight line: {size}. Fly carefully near the furniture." +
                       (noCeiling ? " " + ceiling : "");
            if (shrunk && noCeiling) return $"This room is tight, so I made a shorter hoop course: {size}. {ceiling}";
            if (shrunk) return $"This room is tight, so I made a shorter hoop course: {size}.";
            if (noCeiling) return ceiling;
            return null;
        }
    }
}
