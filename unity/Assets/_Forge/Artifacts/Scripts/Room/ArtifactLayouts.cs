using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Forge.Notebook;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// Layout records (C-12 section 6.1, <c>albie.layout/1</c>): where one artifact's generated course sits in one room.
    /// Made the first time the artifact opens in a room, saved, and reused by reruns, so runs compare like for like.
    /// Saved at <c>artifacts/&lt;artifact_id&gt;/layouts/lay_0001.json</c> (<see cref="ArtifactLibrary.Root"/>), beside the artifact's own files.
    ///
    /// <c>room.anchor</c> is a uuid from <see cref="ArtifactAnchors"/> (an <c>OVRSpatialAnchor</c> on a Quest). The
    /// course origin is also stored in the room's floor frame as <c>room.origin {pos, yaw}</c>, so Editor and mock rooms
    /// can place the same course when the headset uuid isn't localised yet. Slots are local to that origin.
    /// </summary>
    public static class ArtifactLayouts
    {
        public const string Schema = "albie.layout/1";

        public static string Folder(string artifactId) => Path.Combine(ArtifactLibrary.Folder(artifactId), "layouts");

        /// <summary>
        /// The newest saved layout for this artifact version, scenario and room that asked for the same size, or null.
        /// A damaged layout file is skipped, never fatal: a new layout gets made.
        /// </summary>
        public static JObject Find(string artifactId, int version, string scenario, string roomKey, JObject requested)
        {
            string folder = Folder(artifactId);
            if (!Directory.Exists(folder)) return null;
            JObject best = null;
            int bestNumber = 0;
            foreach (string path in Directory.GetFiles(folder, "lay_*.json"))
            {
                JObject layout;
                try { layout = NotebookFiles.Parse(File.ReadAllText(path)) as JObject; }
                catch (Exception e) when (e is JsonException || e is IOException) { Debug.LogWarning($"[Artifacts] skipped {path}: {e.Message}"); continue; }
                if (layout == null || (string)layout["schema"] != Schema) continue;
                if ((int?)layout["artifact_version"] != version || (string)layout["scenario"] != scenario) continue;
                if ((string)layout["room"]?["key"] != roomKey || !JToken.DeepEquals(layout["fit"]?["requested"], requested)) continue;
                if (layout["room"]?["origin"] == null) continue;  // a layout from before origins (fixtures) can't be placed
                int n = Number((string)layout["layout_id"]);
                if (n > bestNumber) { best = layout; bestNumber = n; }
            }
            return best;
        }

        /// <summary>A new layout record for a fit, with the next free id. Not saved: call <see cref="Save"/>.</summary>
        public static JObject Make(string artifactId, int version, string scenario, int seed, IFloorPlan room, JObject requested,
                                   FloorSlalom.Result fit, string message, string countKey = "gates")
        {
            var toRoom = room.RoomToWorld.inverse;
            var originInRoom = toRoom.MultiplyPoint3x4(fit.Origin);
            var forwardInRoom = toRoom.MultiplyVector(Quaternion.Euler(0f, fit.Yaw, 0f) * Vector3.forward);
            var slots = new JObject();
            foreach (var pair in fit.Slots)
            {
                var list = new JArray();
                foreach (var pose in pair.Value) list.Add(new JObject { ["pos"] = Vec(pose.position), ["rot"] = Vec(pose.rotation.eulerAngles) });
                slots[pair.Key] = list;
            }
            return new JObject
            {
                ["schema"] = Schema,
                ["layout_id"] = NextId(artifactId),
                ["artifact_id"] = artifactId,
                ["artifact_version"] = version,
                ["scenario"] = scenario,
                ["seed"] = seed,
                ["room"] = new JObject
                {
                    ["key"] = room.Key,
                    ["anchor"] = ArtifactAnchors.Save(new Pose(fit.Origin, Quaternion.Euler(0f, fit.Yaw, 0f))),
                    ["origin"] = new JObject { ["pos"] = Vec(originInRoom), ["yaw"] = R(Mathf.Atan2(forwardInRoom.x, forwardInRoom.z) * Mathf.Rad2Deg) },
                },
                ["fit"] = new JObject
                {
                    ["requested"] = requested.DeepClone(),
                    ["placed"] = new JObject { [countKey] = fit.Gates, ["spacing_m"] = R(fit.Spacing), ["lane_m"] = R(fit.Lane) },
                    ["shrunk"] = fit.Shrunk,
                    ["around_found"] = fit.AroundFound,
                    ["message"] = message,
                },
                ["slots"] = slots,
                ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            };
        }

        /// <summary>Writes the layout through a temp file. Layouts are written once and never edited.</summary>
        public static void Save(JObject layout)
        {
            string path = Path.Combine(Folder((string)layout["artifact_id"]), (string)layout["layout_id"] + ".json");
            NotebookFiles.WriteAtomic(path, layout.ToString(Formatting.Indented));
        }

        /// <summary>The course origin in world space, from the room it's in now.</summary>
        public static Pose Origin(JObject layout, IFloorPlan room)
        {
            var origin = layout["room"]["origin"];
            var toWorld = room.RoomToWorld;
            var pos = toWorld.MultiplyPoint3x4(ToVector(origin["pos"]));
            var forward = toWorld.MultiplyVector(Quaternion.Euler(0f, (float)origin["yaw"], 0f) * Vector3.forward);
            return new Pose(pos, Quaternion.LookRotation(new Vector3(forward.x, 0f, forward.z), Vector3.up));
        }

        /// <summary>The layout's slots, local to its origin.</summary>
        public static Dictionary<string, List<Pose>> Slots(JObject layout)
        {
            var result = new Dictionary<string, List<Pose>>();
            if (!(layout["slots"] is JObject slots)) return result;
            foreach (var pair in slots)
            {
                var list = new List<Pose>();
                foreach (var pose in (JArray)pair.Value) list.Add(new Pose(ToVector(pose["pos"]), Quaternion.Euler(ToVector(pose["rot"]))));
                result[pair.Key] = list;
            }
            return result;
        }

        public static int Placed(JObject layout, string key) => (int?)layout["fit"]?["placed"]?[key] ?? 0;
        public static float PlacedNumber(JObject layout, string key) => (float?)layout["fit"]?["placed"]?[key] ?? 0f;

        static string NextId(string artifactId)
        {
            int highest = 0;
            string folder = Folder(artifactId);
            if (Directory.Exists(folder))
                foreach (string path in Directory.GetFiles(folder, "lay_*.json"))
                    highest = Math.Max(highest, Number(Path.GetFileNameWithoutExtension(path)));
            return $"lay_{highest + 1:0000}";
        }

        static int Number(string id) =>
            id != null && id.StartsWith("lay_") && int.TryParse(id.Substring(4), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;

        static JArray Vec(Vector3 v) => new JArray(R(v.x), R(v.y), R(v.z));
        static double R(float v) => Math.Round(v, 3);
        static Vector3 ToVector(JToken t) => t is JArray a && a.Count == 3 ? new Vector3((float)a[0], (float)a[1], (float)a[2]) : Vector3.zero;
    }
}
