using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// Runs a scene's generator against the room and gives the host its slots and where the course goes (C-12 3.3, 6.1).
    /// Reuses the saved layout for this room when there is one and it still has free floor, so reruns compare like for
    /// like. A new generator type is a host change (it bumps <c>min_runtime</c>): <c>room_hoops</c> needs host 2.
    /// </summary>
    public static class ArtifactGenerators
    {
        public static bool CanRun(string type) => type == "floor_slalom" || type == "room_hoops";

        public sealed class Placement
        {
            public bool Ok;
            public string Code, Message;   // Message: what Albie says (a shrink, a missing couch, why it failed)
            public JObject Layout;
            public bool Reused;
            public Pose Origin;            // world: where the scene root goes
            public Dictionary<string, List<Pose>> Slots;
        }

        /// <summary>One generator type: what it was asked for, how to fit it, and how to check a saved one.</summary>
        sealed class Course
        {
            public string CountKey;
            public JObject Requested;
            public Func<IFloorPlan, FloorSlalom.Result> Fit;
            public Func<IFloorPlan, Vector3, float, int, float, float, bool> StillFits;  // ..., count, spacing, lane (0: the full one)
        }

        static Course Read(string type, string id, JObject p, ArtifactInputs inputs)
        {
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
                Around = p.Value<string>("around"),
            };
            return new Course
            {
                CountKey = "gates",
                Requested = new JObject { ["gates"] = settings.Gates, ["spacing_m"] = Math.Round(settings.Spacing, 2) },
                Fit = room => FloorSlalom.Fit(room, settings, id),
                StillFits = (room, origin, yaw, n, spacing, lane) => FloorSlalom.StillFits(room, origin, yaw, n, spacing, settings, lane),
            };
        }

        /// <param name="forceNew">Make a new layout even if one is saved (<c>artifact_relayout</c>).</param>
        public static Placement Place(JObject manifest, JObject generator, ArtifactInputs inputs, JObject scenario, IFloorPlan room, bool forceNew)
        {
            string id = generator.Value<string>("id");
            var course = Read(generator.Value<string>("type"), id, generator["params"] as JObject ?? new JObject(), inputs);
            string artifactId = manifest.Value<string>("id");
            int version = manifest.Value<int?>("version") ?? 1;
            string scenarioId = scenario?.Value<string>("id") ?? "default";
            int seed = scenario?.Value<int?>("seed") ?? 0;

            if (room == null || !room.HasFloor)
            {
                var none = FloorSlalom.NoRoomData();
                return new Placement { Code = none.Code, Message = none.Message };
            }

            bool moved = false, lostAnchor = false;
            if (!forceNew)
            {
                var saved = ArtifactLayouts.Find(artifactId, version, scenarioId, room.Key, course.Requested);
                if (saved != null)
                {
                    string anchor = (string)saved["room"]?["anchor"];
                    if (anchor != null && !ArtifactAnchors.Exists(anchor))
                        lostAnchor = true;
                    else
                    {
                        var origin = ArtifactLayouts.Origin(saved, room);
                        float yaw = origin.rotation.eulerAngles.y;
                        if (course.StillFits(room, origin.position, yaw, ArtifactLayouts.Placed(saved, course.CountKey),
                                             ArtifactLayouts.PlacedNumber(saved, "spacing_m"), ArtifactLayouts.PlacedNumber(saved, "lane_m")))
                            return new Placement
                            {
                                Ok = true, Layout = saved, Reused = true, Origin = origin, Slots = ArtifactLayouts.Slots(saved),
                                Message = (string)saved["fit"]?["message"],
                            };
                        moved = true;
                    }
                }
            }

            var fit = course.Fit(room);
            if (!fit.Ok) return new Placement { Code = fit.Code, Message = fit.Message };
            string message = fit.Message;
            if (lostAnchor) message = "I can't find the saved course in this room, so I made a new one." + (message != null ? " " + message : "");
            else if (moved) message = "Something in the room has moved since I made the course, so I made a new one." + (message != null ? " " + message : "");
            var layout = ArtifactLayouts.Make(artifactId, version, scenarioId, seed, room, course.Requested, fit, message, course.CountKey);
            ArtifactLayouts.Save(layout);
            return new Placement
            {
                Ok = true, Layout = layout, Message = message, Slots = fit.Slots,
                Origin = new Pose(fit.Origin, Quaternion.Euler(0f, fit.Yaw, 0f)),
            };
        }
    }
}
