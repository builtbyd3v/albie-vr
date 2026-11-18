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
    /// Where room courses may go, chosen in Albie, saved per room (keyed by <see cref="IFloorPlan.Key"/>, like layouts):
    /// <list type="bullet">
    /// <item><c>auto</c> (the default): the Quest boundary when the course fits inside it; the room scan's clear floor when
    /// the boundary is a stationary or tiny one (under <see cref="PlayAreaShape.TinyArea"/>), when there is none, or when
    /// the course doesn't fit inside it. Albie says so the first time, with the safety line.</item>
    /// <item><c>room</c>: the room scan's whole clear floor (walls and furniture still respected), ignoring the Quest boundary.</item>
    /// <item><c>draw</c>: an outline the person drew on the floor (<see cref="PlayAreaEditor"/>), saved in the room's frame.</item>
    /// <item><c>quest</c>: the Quest boundary only, as before.</item>
    /// </list>
    /// <see cref="ArtifactGenerators.Place"/> asks <see cref="Areas"/> for the plans to try, in order; every generator then
    /// sees its choice as <see cref="IFloorPlan.Boundary"/>, so no course has code of its own for it. Saved in
    /// <c>artifacts/_play_areas.json</c> (<see cref="ArtifactLibrary.Root"/>), schema <c>albie.play_area/1</c>.
    /// </summary>
    public static class UserPlayArea
    {
        public enum Mode { Auto, Room, Draw, Quest }

        public const string Schema = "albie.play_area/1";
        public const string Safety = "Stay aware of your surroundings: the Quest guardian still protects you, and furniture the scan missed is still there.";

        static string FilePath => Path.Combine(ArtifactLibrary.Root, "_play_areas.json");
        static string loadedFrom;
        static JObject store;

        /// <summary>Something changed the play area (a mode, a drawn outline). The outline and Settings listen.</summary>
        public static event Action Changed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            loadedFrom = null;
            store = null;
            Changed = null;
        }

        /// <summary>Forgets the cached file, so the next read loads it again (checks).</summary>
        public static void Reload()
        {
            loadedFrom = null;
            store = null;
        }

        static JObject Rooms()
        {
            string path = FilePath;
            if (store == null || loadedFrom != path)
            {
                loadedFrom = path;
                store = null;
                try
                {
                    if (File.Exists(path)) store = NotebookFiles.Parse(File.ReadAllText(path)) as JObject;
                }
                catch (Exception e) when (e is JsonException || e is IOException)
                {
                    Debug.LogWarning("[PlayArea] couldn't read " + path + ": " + e.Message + ". Starting again.");
                }
                if (store == null || (string)store["schema"] != Schema) store = new JObject { ["schema"] = Schema, ["rooms"] = new JObject() };
                if (!(store["rooms"] is JObject)) store["rooms"] = new JObject();
            }
            return (JObject)store["rooms"];
        }

        static JObject Entry(string key, bool make)
        {
            var rooms = Rooms();
            if (rooms[key] is JObject entry) return entry;
            if (!make) return null;
            entry = new JObject { ["mode"] = "auto" };
            rooms[key] = entry;
            return entry;
        }

        static void Save()
        {
            try { NotebookFiles.WriteAtomic(FilePath, store.ToString(Formatting.Indented)); }
            catch (IOException e) { Debug.LogWarning("[PlayArea] couldn't save: " + e.Message); }
            Changed?.Invoke();
        }

        public static string Name(Mode mode) => mode == Mode.Room ? "room" : mode == Mode.Draw ? "draw" : mode == Mode.Quest ? "quest" : "auto";

        public static bool TryParse(string text, out Mode mode)
        {
            switch (text)
            {
                case "room": mode = Mode.Room; return true;
                case "draw": mode = Mode.Draw; return true;
                case "quest": mode = Mode.Quest; return true;
                case "auto": mode = Mode.Auto; return true;
                default: mode = Mode.Auto; return false;
            }
        }

        /// <summary>The mode saved for this room. <c>draw</c> with no outline saved reads as <c>auto</c>.</summary>
        public static Mode ModeOf(IFloorPlan room)
        {
            room = PlayAreaPlan.Unwrap(room);
            if (room == null) return Mode.Auto;
            var entry = Entry(room.Key, false);
            if (entry == null || !TryParse((string)entry["mode"], out var mode)) return Mode.Auto;
            if (mode == Mode.Draw && Drawn(room) == null) return Mode.Auto;
            return mode;
        }

        /// <summary>Chooses <c>room</c>, <c>quest</c> or <c>auto</c> for this room. A drawn outline is kept, to go back to.</summary>
        public static void SetMode(IFloorPlan room, Mode mode)
        {
            room = PlayAreaPlan.Unwrap(room);
            if (room == null) return;
            var entry = Entry(room.Key, true);
            if (mode == Mode.Draw && entry["points"] == null) mode = Mode.Auto;
            entry["mode"] = Name(mode);
            entry["updated_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            Save();
        }

        /// <summary>Saves a drawn outline (world xz) for this room and chooses it. Stored in the room's floor frame.</summary>
        public static void SetDrawn(IFloorPlan room, IList<Vector2> world, int count = -1)
        {
            room = PlayAreaPlan.Unwrap(room);
            if (room == null) return;
            int n = count < 0 ? world.Count : count;
            var toRoom = room.RoomToWorld.inverse;
            var points = new JArray();
            for (int i = 0; i < n; i++)
            {
                var local = toRoom.MultiplyPoint3x4(new Vector3(world[i].x, room.FloorY, world[i].y));
                points.Add(new JArray(Math.Round(local.x, 3), Math.Round(local.z, 3)));
            }
            var entry = Entry(room.Key, true);
            entry["points"] = points;
            entry["mode"] = "draw";
            entry["updated_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            Save();
        }

        /// <summary>The drawn outline for this room in world xz, or null when none is saved.</summary>
        public static Vector2[] Drawn(IFloorPlan room)
        {
            room = PlayAreaPlan.Unwrap(room);
            if (room == null || !(Entry(room.Key, false)?["points"] is JArray points) || points.Count < 3) return null;
            var toWorld = room.RoomToWorld;
            var result = new Vector2[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                if (!(points[i] is JArray xz) || xz.Count != 2) return null;
                var w = toWorld.MultiplyPoint3x4(new Vector3((float)xz[0], 0f, (float)xz[1]));
                result[i] = new Vector2(w.x, w.z);
            }
            return result;
        }

        /// <summary>The Quest boundary as the room reports it (a mock's own, or the headset's Play Area), or null.</summary>
        public static Vector2[] Quest(IFloorPlan room) => PlayAreaPlan.Unwrap(room)?.Boundary;

        /// <summary>A stationary (seated) Quest boundary or one close to it: too small for courses, so <c>auto</c> skips it.</summary>
        public static bool IsTiny(Vector2[] boundary) => boundary != null && boundary.Length >= 3 && PlayAreaShape.Area(boundary) < PlayAreaShape.TinyArea;

        /// <summary>What courses in this room use now, in words for the panel and Albie.</summary>
        public static string Describe(IFloorPlan room)
        {
            var mode = ModeOf(room);
            var quest = Quest(room);
            switch (mode)
            {
                case Mode.Room: return "Your whole room: the clear floor from the room scan.";
                case Mode.Draw: return string.Format(CultureInfo.InvariantCulture, "The area you drew: {0:0.0} m².", PlayAreaShape.Area(Drawn(room)));
                case Mode.Quest:
                    return quest != null
                        ? string.Format(CultureInfo.InvariantCulture, "Your Quest boundary: {0:0.0} m².", PlayAreaShape.Area(quest))
                        : "Your Quest boundary, but the headset isn't reporting one, so the room's clear floor.";
                default:
                    if (quest == null) return "Automatic: no Quest boundary is reported, so the room's clear floor.";
                    if (IsTiny(quest)) return string.Format(CultureInfo.InvariantCulture, "Automatic: your Quest boundary is a small one ({0:0.0} m²), so the room's clear floor.", PlayAreaShape.Area(quest));
                    return string.Format(CultureInfo.InvariantCulture, "Automatic: your Quest boundary ({0:0.0} m²), or the room's clear floor when a course doesn't fit inside it.", PlayAreaShape.Area(quest));
            }
        }

        /// <summary>
        /// The plans a floor course tries, in order (C-12 fit rules run on each until one fits). <paramref name="notes"/>
        /// gets, per plan, what Albie says when that one is used (null for nothing), with the safety line the first time
        /// a room's clear floor is used past its Quest boundary.
        /// </summary>
        public static List<PlayAreaPlan> Areas(IFloorPlan room, List<string> notes)
        {
            room = PlayAreaPlan.Unwrap(room);
            var list = new List<PlayAreaPlan>();
            notes.Clear();
            var quest = room.Boundary;
            switch (ModeOf(room))
            {
                case Mode.Room:
                    list.Add(new PlayAreaPlan(room, PlayAreaPlan.Source.Room, null));
                    notes.Add(FirstTime(room) ? Safety : null);
                    break;
                case Mode.Draw:
                    list.Add(new PlayAreaPlan(room, PlayAreaPlan.Source.Drawn, Drawn(room)));
                    notes.Add(null);
                    break;
                case Mode.Quest:
                    list.Add(new PlayAreaPlan(room, quest != null ? PlayAreaPlan.Source.Quest : PlayAreaPlan.Source.Room, quest));
                    notes.Add(null);
                    break;
                default:
                    if (quest == null)
                    {
                        list.Add(new PlayAreaPlan(room, PlayAreaPlan.Source.Room, null));
                        notes.Add(null);
                    }
                    else if (IsTiny(quest))
                    {
                        list.Add(new PlayAreaPlan(room, PlayAreaPlan.Source.Room, null));
                        notes.Add(FirstTime(room)
                            ? "Your Quest boundary is a small stationary one, so I used the clear floor from your room scan instead. " + Safety
                            : null);
                    }
                    else
                    {
                        list.Add(new PlayAreaPlan(room, PlayAreaPlan.Source.Quest, quest));
                        notes.Add(null);
                        list.Add(new PlayAreaPlan(room, PlayAreaPlan.Source.Room, null));
                        notes.Add("This course doesn't fit inside your Quest boundary, so I used the clear floor from your room scan instead." + (FirstTime(room) ? " " + Safety : ""));
                    }
                    break;
            }
            return list;
        }

        // True when Albie hasn't yet said, in this room, that a course uses floor past the Quest boundary. Doesn't mark it.
        static bool FirstTime(IFloorPlan room)
        {
            var entry = Entry(room.Key, false);
            return entry == null || entry.Value<bool?>("told") != true;
        }

        /// <summary>Records that the safety line was said in this room, so it's said once.</summary>
        public static void MarkTold(IFloorPlan room)
        {
            room = PlayAreaPlan.Unwrap(room);
            if (room == null) return;
            var entry = Entry(room.Key, true);
            if (entry.Value<bool?>("told") == true) return;
            entry["told"] = true;
            Save();
        }
    }
}
