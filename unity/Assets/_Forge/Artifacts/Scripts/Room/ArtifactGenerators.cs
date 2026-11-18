using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// Runs a scene's generator against the room and gives the host its slots and where the course goes (C-12 3.3, 6.1).
    /// Reuses the saved layout for this room when there is one and it still has free floor, so reruns compare like for
    /// like. A new generator type is a host change (it bumps <c>min_runtime</c>): <c>room_hoops</c> needs host 2;
    /// <c>room_course</c> and <c>surface: "desk"</c> on any generator need host 7.
    ///
    /// On a desk (<c>params.surface: "desk"</c>) the same generator fits its full-size course on the room's table,
    /// magnified (<see cref="DeskPlan"/>), at the biggest scale that fits; the scene is then shown at that scale
    /// (<see cref="Placement.Scale"/>). No table is <c>no_table</c>; a table too small is <c>no_free_floor</c>.
    /// </summary>
    public static class ArtifactGenerators
    {
        public static bool CanRun(string type) => type == "floor_slalom" || type == "room_hoops" || type == RoomCourse.Type;

        public sealed class Placement
        {
            public bool Ok;
            public string Code, Message;   // Message: what Albie says (a shrink, a missing couch, why it failed)
            public JObject Layout;
            public bool Reused;
            public Pose Origin;            // world: where the scene root goes
            public float Scale = 1f;       // the scene root's scale: 1 on the floor, smaller on a desk
            public Dictionary<string, List<Pose>> Slots;
        }

        /// <summary>One generator type: what it was asked for, how to fit it, and how to check a saved one.</summary>
        sealed class Course
        {
            public string CountKey;
            public JObject Requested;
            public Func<IFloorPlan, FloorSlalom.Result> Fit;
            public Func<IFloorPlan, Vector3, float, int, float, float, bool> StillFits;  // ..., count, spacing, lane (0: the full one)
            public Func<IFloorPlan, Vector3, float, int, float, float, bool> StillFitsWithoutBoundary;
        }

        static Course Read(string type, string id, JObject p, ArtifactInputs inputs, bool desk)
        {
            if (type == RoomCourse.Type)
            {
                var c = RoomCourse.Read(p, inputs);
                if (desk) c.Around = null;  // nothing on a desk is in the scan
                return new Course
                {
                    CountKey = "segments",
                    Requested = RoomCourse.Requested(c),
                    Fit = room => RoomCourse.Fit(room, c, id),
                    StillFits = (room, origin, yaw, n, spacing, lane) => RoomCourse.StillFits(room, origin, yaw, spacing, c, lane),
                    StillFitsWithoutBoundary = (room, origin, yaw, n, spacing, lane) => RoomCourse.StillFits(room, origin, yaw, spacing, c, lane, false),
                };
            }
            if (type == "room_hoops")
            {
                var d = RoomHoops.Settings.Default;
                var s = new RoomHoops.Settings
                {
                    Hoops = Mathf.RoundToInt(inputs.Number(p["hoops"], d.Hoops)),
                    Spacing = inputs.Number(p["spacing_m"], d.Spacing),
                    MinSpacing = inputs.Number(p["min_spacing_m"], d.MinSpacing),
                    MinHoops = Mathf.RoundToInt(inputs.Number(p["min_hoops"], d.MinHoops)),
                    Diameter = inputs.Number(p["diameter_m"], d.Diameter),
                    Height = inputs.Number(p["height_m"], d.Height),
                    Clearance = inputs.Number(p["clearance_m"], d.Clearance),
                    TightSpacing = inputs.Number(p["tight_spacing_m"], d.TightSpacing),
                };
                return new Course
                {
                    CountKey = "hoops",
                    Requested = new JObject { ["hoops"] = s.Hoops, ["spacing_m"] = Math.Round(s.Spacing, 2) },
                    Fit = room => RoomHoops.Fit(room, s, id),
                    StillFits = (room, origin, yaw, n, spacing, lane) => RoomHoops.StillFits(room, origin, yaw, n, spacing, s, lane),
                    StillFitsWithoutBoundary = (room, origin, yaw, n, spacing, lane) => RoomHoops.StillFitsWithoutBoundary(room, origin, yaw, n, spacing, s, lane),
                };
            }
            var sd = FloorSlalom.Settings.Default;
            var settings = new FloorSlalom.Settings
            {
                Gates = Mathf.RoundToInt(inputs.Number(p["gates"], sd.Gates)),
                Spacing = inputs.Number(p["spacing_m"], sd.Spacing),
                MinSpacing = inputs.Number(p["min_spacing_m"], sd.MinSpacing),
                MinGates = Mathf.RoundToInt(inputs.Number(p["min_gates"], sd.MinGates)),
                Lane = inputs.Number(p["lane_width_m"], sd.Lane),
                Clearance = inputs.Number(p["clearance_m"], sd.Clearance),
                NarrowLane = inputs.Number(p["narrow_lane_width_m"], sd.NarrowLane),
                TightSpacing = inputs.Number(p["tight_spacing_m"], sd.TightSpacing),
                Around = desk ? null : p.Value<string>("around"),
            };
            return new Course
            {
                CountKey = "gates",
                Requested = new JObject { ["gates"] = settings.Gates, ["spacing_m"] = Math.Round(settings.Spacing, 2) },
                Fit = room => FloorSlalom.Fit(room, settings, id),
                StillFits = (room, origin, yaw, n, spacing, lane) => FloorSlalom.StillFits(room, origin, yaw, n, spacing, settings, lane),
                StillFitsWithoutBoundary = (room, origin, yaw, n, spacing, lane) => FloorSlalom.StillFitsWithoutBoundary(room, origin, yaw, n, spacing, settings, lane),
            };
        }

        /// <param name="forceNew">Make a new layout even if one is saved (<c>artifact_relayout</c>).</param>
        public static Placement Place(JObject manifest, JObject generator, ArtifactInputs inputs, JObject scenario, IFloorPlan room, bool forceNew)
        {
            string id = generator.Value<string>("id");
            var p = generator["params"] as JObject ?? new JObject();
            bool desk = p["surface"] != null && p["surface"].Type == JTokenType.String && (string)p["surface"] == "desk";
            var course = Read(generator.Value<string>("type"), id, p, inputs, desk);
            if (desk) course.Requested["surface"] = "desk";  // a floor layout's request is unchanged, so saved ones still match
            var job = new Job
            {
                ArtifactId = manifest.Value<string>("id"),
                Version = manifest.Value<int?>("version") ?? 1,
                Scenario = scenario?.Value<string>("id") ?? "default",
                Seed = scenario?.Value<int?>("seed") ?? 0,
            };

            if (room == null || !room.HasFloor)
            {
                var none = FloorSlalom.NoRoomData();
                return new Placement { Code = none.Code, Message = none.Message };
            }
            if (desk) return PlaceOnDesk(course, room, job, forceNew);

            bool moved = false, boundaryChanged = false, lostAnchor = false;
            if (!forceNew)
            {
                var reused = Reuse(course, room, null, job, out moved, out boundaryChanged, out lostAnchor);
                if (reused != null) return reused;
            }

            var fit = course.Fit(room);
            if (!fit.Ok) return new Placement { Code = fit.Code, Message = fit.Message };
            string message = fit.Message;
            if (lostAnchor) message = "I can't find the saved course in this room, so I made a new one." + (message != null ? " " + message : "");
            else if (moved) message = (boundaryChanged ? "Your boundary has changed since I made the course, so I made a new one." : "Something in the room has moved since I made the course, so I made a new one.") + (message != null ? " " + message : "");
            var layout = ArtifactLayouts.Make(job.ArtifactId, job.Version, job.Scenario, job.Seed, room, course.Requested, fit, message, course.CountKey);
            ArtifactLayouts.Save(layout);
            return new Placement
            {
                Ok = true, Layout = layout, Message = message, Slots = fit.Slots,
                Origin = new Pose(fit.Origin, Quaternion.Euler(0f, fit.Yaw, 0f)),
            };
        }

        struct Job
        {
            public string ArtifactId, Scenario;
            public int Version, Seed;
        }

        // The saved layout for this plan, if there is one and it still has free floor; else null, with why it can't be used.
        static Placement Reuse(Course course, IFloorPlan plan, DeskPlan desk, Job job, out bool moved, out bool boundaryChanged, out bool lostAnchor)
        {
            moved = boundaryChanged = lostAnchor = false;
            var saved = ArtifactLayouts.Find(job.ArtifactId, job.Version, job.Scenario, plan.Key, course.Requested);
            if (saved == null) return null;
            string anchor = (string)saved["room"]?["anchor"];
            if (anchor != null && !ArtifactAnchors.Exists(anchor))
            {
                lostAnchor = true;
                return null;
            }
            var origin = ArtifactLayouts.Origin(saved, plan);
            float yaw = origin.rotation.eulerAngles.y;
            int placed = ArtifactLayouts.Placed(saved, course.CountKey);
            float spacing = ArtifactLayouts.PlacedNumber(saved, "spacing_m"), lane = ArtifactLayouts.PlacedNumber(saved, "lane_m");
            if (course.StillFits(plan, origin.position, yaw, placed, spacing, lane))
                return new Placement
                {
                    Ok = true, Layout = saved, Reused = true, Slots = ArtifactLayouts.Slots(saved),
                    Origin = desk != null ? desk.ToReal(origin) : origin, Scale = desk != null ? desk.Scale : 1f,
                    Message = (string)saved["fit"]?["message"],
                };
            moved = true;
            boundaryChanged = plan.Boundary != null && course.StillFitsWithoutBoundary(plan, origin.position, yaw, placed, spacing, lane);
            return null;
        }

        // The table top, biggest scale first: a saved layout at any scale wins, then the largest course that fits.
        static Placement PlaceOnDesk(Course course, IFloorPlan room, Job job, bool forceNew)
        {
            if (!DeskPlan.TryMake(room, DeskPlan.Scales[0], out var first))
                return new Placement
                {
                    Code = "no_table",
                    Message = "I can't find a table in this room's scan, so I can't put a course on one. Add your desk in Space Setup on the headset, or ask for the course on the floor.",
                };
            if (!forceNew)
                foreach (float scale in DeskPlan.Scales)
                    if (DeskPlan.TryMake(room, scale, out var saved))
                    {
                        var reused = Reuse(course, saved, saved, job, out _, out _, out _);
                        if (reused != null) return reused;
                    }

            FloorSlalom.Result failed = null;
            foreach (float scale in DeskPlan.Scales)
            {
                if (!DeskPlan.TryMake(room, scale, out var plan)) continue;
                var fit = course.Fit(plan);
                if (!fit.Ok)
                {
                    failed = fit;
                    continue;
                }
                var real = plan.ToReal(new Pose(fit.Origin, Quaternion.Euler(0f, fit.Yaw, 0f)));
                string message = "I put it on the table, at " + DeskPlan.SizeWords(scale) + ". Clear the table top first.";
                // The fit's own words give sizes in full-size metres, which would be wrong on a table: say only that it shrank.
                if (fit.Shrunk) message += " I made the course shorter so it fits the table top.";
                var layout = ArtifactLayouts.Make(job.ArtifactId, job.Version, job.Scenario, job.Seed, plan, course.Requested, fit, message, course.CountKey, real, scale);
                ArtifactLayouts.Save(layout);
                return new Placement { Ok = true, Layout = layout, Message = message, Slots = fit.Slots, Origin = real, Scale = scale };
            }
            if (failed != null && failed.Code != "no_free_floor") return new Placement { Code = failed.Code, Message = failed.Message };  // no_headroom keeps its own words
            var top = first.TopSize;
            return new Placement
            {
                Code = "no_free_floor",
                Message = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Your table is too small for this course: its top is about {0:0.0} by {1:0.0} m, and the course doesn't fit even at a tenth of full size. Ask for a shorter course, or put it on the floor.",
                    top.x, top.y),
            };
        }
    }
}
