using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// Runs a scene's generator against the room and gives the host its slots and where the course goes (C-12 3.3, 6.1).
    /// Reuses the saved layout for this room when there is one and it still has free floor, so reruns compare like for
    /// like. A new generator type is a host change (it bumps <c>min_runtime</c>). <c>room_hoops</c> isn't built yet.
    /// </summary>
    public static class ArtifactGenerators
    {
        public static bool CanRun(string type) => type == "floor_slalom";

        public sealed class Placement
        {
            public bool Ok;
            public string Code, Message;   // Message: what Albie says (a shrink, a missing couch, why it failed)
            public JObject Layout;
            public bool Reused;
            public Pose Origin;            // world: where the scene root goes
            public Dictionary<string, List<Pose>> Slots;
        }

        /// <param name="forceNew">Make a new layout even if one is saved (<c>artifact_relayout</c>).</param>
        public static Placement Place(JObject manifest, JObject generator, ArtifactInputs inputs, JObject scenario, IFloorPlan room, bool forceNew)
        {
            string id = generator.Value<string>("id");
            var p = generator["params"] as JObject ?? new JObject();
            var settings = new FloorSlalom.Settings
            {
                Gates = Mathf.RoundToInt(inputs.Number(p["gates"], 5)),
                Spacing = inputs.Number(p["spacing_m"], 0.8f),
                MinSpacing = inputs.Number(p["min_spacing_m"], 0.6f),
                MinGates = Mathf.RoundToInt(inputs.Number(p["min_gates"], 3)),
                Lane = inputs.Number(p["lane_width_m"], 0.6f),
                Clearance = inputs.Number(p["clearance_m"], 0.15f),
                Around = p.Value<string>("around"),
            };
            var requested = new JObject { ["gates"] = settings.Gates, ["spacing_m"] = System.Math.Round(settings.Spacing, 2) };
            string artifactId = manifest.Value<string>("id");
            int version = manifest.Value<int?>("version") ?? 1;
            string scenarioId = scenario?.Value<string>("id") ?? "default";
            int seed = scenario?.Value<int?>("seed") ?? 0;

            if (room == null || !room.HasFloor)
            {
                var none = FloorSlalom.Fit(null, settings, id);
                return new Placement { Code = none.Code, Message = none.Message };
            }

            bool moved = false, lostAnchor = false;
            if (!forceNew)
            {
                var saved = ArtifactLayouts.Find(artifactId, version, scenarioId, room.Key, requested);
                if (saved != null)
                {
                    string anchor = (string)saved["room"]?["anchor"];
                    if (anchor != null && !ArtifactAnchors.Exists(anchor))
                        lostAnchor = true;
                    else
                    {
                        var origin = ArtifactLayouts.Origin(saved, room);
                        float yaw = origin.rotation.eulerAngles.y;
                        if (FloorSlalom.StillFits(room, origin.position, yaw, ArtifactLayouts.Placed(saved, "gates"), ArtifactLayouts.PlacedNumber(saved, "spacing_m"), settings))
                            return new Placement
                            {
                                Ok = true, Layout = saved, Reused = true, Origin = origin, Slots = ArtifactLayouts.Slots(saved),
                                Message = (string)saved["fit"]?["message"],
                            };
                        moved = true;
                    }
                }
            }

            var fit = FloorSlalom.Fit(room, settings, id);
            if (!fit.Ok) return new Placement { Code = fit.Code, Message = fit.Message };
            string message = fit.Message;
            if (lostAnchor) message = "I can't find the saved course in this room, so I made a new one." + (message != null ? " " + message : "");
            else if (moved) message = "Something in the room has moved since I made the course, so I made a new one." + (message != null ? " " + message : "");
            var layout = ArtifactLayouts.Make(artifactId, version, scenarioId, seed, room, requested, fit, message);
            ArtifactLayouts.Save(layout);
            return new Placement
            {
                Ok = true, Layout = layout, Message = message, Slots = fit.Slots,
                Origin = new Pose(fit.Origin, Quaternion.Euler(0f, fit.Yaw, 0f)),
            };
        }
    }
}
