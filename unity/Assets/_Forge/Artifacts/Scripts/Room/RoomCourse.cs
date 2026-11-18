using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// The <c>room_course</c> generator (host 7): one straight-line course made of an ordered list of elements, fitted to
    /// free floor with the same search as <see cref="FloorSlalom"/> (clearance, the Quest boundary, an optional piece of
    /// furniture to go around, saved layouts). It is what the dash, parking, hover and landing courses are made of, and
    /// what a course Albie writes from the user's idea is made of: the agent names the elements, this places them.
    ///
    /// The course runs along its local +z from the start (z = 0). Elements, in order:
    /// <c>straight</c> (clear floor, <c>length_m</c>); <c>cones</c> (a cone every <c>spacing_m</c> with a gate beside it
    /// on alternating sides, as the slalom); <c>gates</c> (checkpoints on the centre line, between two posts for a
    /// truck); <c>hoops</c> (standing hoops for a drone); and, last only, <c>bay</c> / <c>pad</c> (a marked spot to stop
    /// or land on) or <c>hover</c> (a box in the air to hold inside). Without a last element of that kind the course
    /// ends in a finish line. Every slot is on the floor (y = 0); the scene lifts what stands above it.
    ///
    /// Fit rules (shrink and say so): the requested size; then every spacing and straight squeezed together, down to
    /// each element's minimum; then the same with less free floor either side (cones and hoops only); then
    /// <c>no_free_floor</c>. Counts are never cut: a course that still doesn't fit says how big a strip it needs.
    /// </summary>
    public static class RoomCourse
    {
        public const string Type = "room_course";
        public const int MaxElements = 8, MaxCount = 12;
        public const float WeaveLane = 0.6f;    // the slalom's: a weave gate sits half of this off the cones
        public const float GateHalf = 0.35f;    // a gate's posts stand this far either side of the centre line
        public const float Tail = 1.0f, TailMin = 0.6f;  // from the last element to the finish line
        public const float MaxLength = 25f;

        public static readonly string[] ElementTypes = { "straight", "cones", "gates", "hoops", "bay", "pad", "hover" };
        public static readonly string[] Things = { "truck", "drone" };
        public static readonly string[] Labels = { "COUCH", "TABLE", "BED", "STORAGE", "PLANT", "SCREEN", "LAMP", "OTHER" };

        /// <summary>The keys each element type takes besides <c>type</c>.</summary>
        public static readonly Dictionary<string, string[]> ElementKeys = new Dictionary<string, string[]>
        {
            { "straight", new[] { "length_m", "min_length_m" } },
            { "cones", new[] { "count", "spacing_m" } },
            { "gates", new[] { "count", "spacing_m" } },
            { "hoops", new[] { "count", "spacing_m", "width_m", "height_m" } },
            { "bay", new[] { "spacing_m", "width_m", "length_m", "hold_s" } },
            { "pad", new[] { "spacing_m", "width_m", "length_m", "hold_s" } },
            { "hover", new[] { "spacing_m", "width_m", "height_m", "hold_s" } },
        };

        /// <summary>The range each element number takes: min, max.</summary>
        static readonly Dictionary<string, float[]> Ranges = new Dictionary<string, float[]>
        {
            { "count", new[] { 1f, MaxCount } }, { "spacing_m", new[] { 0.3f, 3f } }, { "length_m", new[] { 0.3f, 12f } },
            { "min_length_m", new[] { 0.3f, 12f } }, { "width_m", new[] { 0.3f, 2f } }, { "height_m", new[] { 0.4f, 2f } },
            { "hold_s", new[] { 0.5f, 120f } },
        };

        public struct Element
        {
            public string Type;
            public int Count;
            public float Spacing, MinSpacing;  // between its items and before the first; for a bay, pad or hover box, the run-up to it
            public float Length, MinLength;    // a straight's length; a bay's or pad's depth
            public float Width, Height, HoldS; // a bay's width, a hoop's diameter, a hover box's side; a hoop's or box's centre height

            public bool Ends => Type == "bay" || Type == "pad" || Type == "hover";
        }

        public sealed class Settings
        {
            public string Thing = "truck";
            public readonly List<Element> Elements = new List<Element>();
            public float Clearance = 0.15f;
            public string Around;  // an MRUK label, or null
            public bool Drone => Thing == "drone";

            /// <summary>The last element when it ends the run (bay, pad, hover), else null.</summary>
            public Element? End => Elements.Count > 0 && Elements[Elements.Count - 1].Ends ? Elements[Elements.Count - 1] : (Element?)null;
            public bool Has(string type)
            {
                for (int i = 0; i < Elements.Count; i++) if (Elements[i].Type == type) return true;
                return false;
            }
            public Element First(string type)
            {
                for (int i = 0; i < Elements.Count; i++) if (Elements[i].Type == type) return Elements[i];
                return Defaults(type, Drone);
            }
        }

        /// <summary>What an element is when the spec leaves a number out.</summary>
        public static Element Defaults(string type, bool drone)
        {
            var e = new Element { Type = type };
            switch (type)
            {
                case "straight": e.Length = 3f; e.MinLength = 1f; break;
                case "cones": e.Count = 4; e.Spacing = drone ? 1.0f : 1.2f; e.MinSpacing = drone ? 0.6f : 1.0f; break;
                case "gates": e.Count = 3; e.Spacing = 1.5f; e.MinSpacing = 0.8f; break;
                case "hoops": e.Count = 3; e.Spacing = 1.0f; e.MinSpacing = 0.6f; e.Width = 0.7f; e.Height = 1.0f; break;
                case "bay":
                case "pad":
                    e.Spacing = 1.2f; e.MinSpacing = 0.6f; e.Width = 0.6f; e.Length = drone ? 0.6f : 0.9f; e.HoldS = drone ? 1.5f : 1.0f;
                    break;
                case "hover": e.Spacing = 1.0f; e.MinSpacing = 0.5f; e.Width = 0.8f; e.Height = 1.0f; e.HoldS = 10f; break;
            }
            return e;
        }

        /// <summary>Reads a generator's params. Numbers can be <c>$input</c> references. Assumes <see cref="Check"/> passed; a bad value falls back to its default.</summary>
        public static Settings Read(JObject p, ArtifactInputs inputs)
        {
            var s = new Settings();
            if (p == null) return s;
            string thing = Text(p["thing"]);
            if (Array.IndexOf(Things, thing) >= 0) s.Thing = thing;
            s.Clearance = Mathf.Clamp(Number(p["clearance_m"], inputs, s.Clearance), 0.02f, 1f);
            s.Around = Text(p["around"]);
            if (p["elements"] is JArray list)
                foreach (var item in list)
                {
                    if (!(item is JObject o) || s.Elements.Count >= MaxElements) continue;
                    string type = Text(o["type"]);
                    if (Array.IndexOf(ElementTypes, type) < 0) continue;
                    var e = Defaults(type, s.Drone);
                    e.Count = Mathf.Clamp(Mathf.RoundToInt(Number(o["count"], inputs, e.Count)), type == "cones" || type == "gates" || type == "hoops" ? 1 : 0, MaxCount);
                    e.Spacing = Mathf.Clamp(Number(o["spacing_m"], inputs, e.Spacing), 0.3f, 3f);
                    e.MinSpacing = Mathf.Min(e.MinSpacing, e.Spacing);
                    e.Length = Mathf.Clamp(Number(o["length_m"], inputs, e.Length), 0.3f, 12f);
                    e.MinLength = type == "straight" ? Mathf.Clamp(Number(o["min_length_m"], inputs, Mathf.Min(e.MinLength, e.Length)), 0.3f, e.Length) : e.Length;
                    e.Width = Mathf.Clamp(Number(o["width_m"], inputs, e.Width), 0f, 2f);
                    e.Height = Mathf.Clamp(Number(o["height_m"], inputs, e.Height), 0f, 2f);
                    if (type == "hoops" || type == "hover") e.Height = Mathf.Max(e.Height, e.Width * 0.5f + RoomHoops.Post);
                    e.HoldS = Mathf.Clamp(Number(o["hold_s"], inputs, e.HoldS), 0f, 120f);
                    s.Elements.Add(e);
                }
            return s;
        }

        /// <summary>What was asked for, with every number resolved: two opens that ask for the same course share a saved layout.</summary>
        public static JObject Requested(Settings s)
        {
            var list = new JArray();
            foreach (var e in s.Elements)
            {
                var o = new JObject { ["type"] = e.Type };
                if (e.Count > 0) o["count"] = e.Count;
                if (e.Type != "straight") o["spacing_m"] = R(e.Spacing);
                if (e.Type == "straight" || e.Type == "bay" || e.Type == "pad") o["length_m"] = R(e.Length);
                if (e.Type == "straight") o["min_length_m"] = R(e.MinLength);
                if (e.Width > 0f) o["width_m"] = R(e.Width);
                if (e.Height > 0f) o["height_m"] = R(e.Height);
                list.Add(o);
            }
            var requested = new JObject { ["thing"] = s.Thing, ["elements"] = list };
            if (!string.IsNullOrEmpty(s.Around)) requested["around"] = s.Around;
            return requested;
        }

        // ---------- geometry ----------

        /// <summary>How far past the finish the course still needs floor, beyond the search's own end margin.</summary>
        public static float Overhang(Settings s)
        {
            var end = s.End;
            if (!end.HasValue) return 0f;
            float half = end.Value.Type == "hover" ? end.Value.Width * 0.5f : end.Value.Length * 0.5f;
            return Mathf.Max(0f, half + 0.1f - FloorSlalom.EndMargin);
        }

        /// <summary>From the start to the finish, with the spacings a share <paramref name="t"/> (0 to 1) of the way from their minimum to what was asked.</summary>
        public static float FinishZ(Settings s, float t)
        {
            float z = 0f;
            foreach (var e in s.Elements)
            {
                if (e.Type == "straight") z += Mathf.Lerp(e.MinLength, e.Length, t);
                else z += Mathf.Lerp(e.MinSpacing, e.Spacing, t) * Mathf.Max(1, e.Count);
            }
            if (!s.End.HasValue) z += Mathf.Lerp(TailMin, Tail, t);
            return z;
        }

        /// <summary>Free floor needed either side of the centre line.</summary>
        public static float Lane(Settings s, bool narrow)
        {
            float lane = s.Drone ? 0.45f : 0.4f;
            foreach (var e in s.Elements)
                switch (e.Type)
                {
                    case "cones": lane = Mathf.Max(lane, narrow ? 0.4f : WeaveLane); break;
                    case "gates": lane = Mathf.Max(lane, GateHalf + 0.1f); break;
                    case "hoops": lane = Mathf.Max(lane, (narrow ? 0f : RoomHoops.Weave(e.Width)) + e.Width * 0.5f + RoomHoops.Post); break;
                    case "bay":
                    case "pad": lane = Mathf.Max(lane, e.Width * 0.5f + 0.1f); break;
                    case "hover": lane = Mathf.Max(lane, e.Width * 0.5f); break;
                }
            return lane;
        }

        /// <summary>Floor to the top of the tallest thing the course stands up or asks the Thing to fly through; 0 for a course on the floor.</summary>
        public static float Headroom(Settings s)
        {
            float top = 0f;
            foreach (var e in s.Elements)
            {
                if (e.Type == "hoops") top = Mathf.Max(top, e.Height + e.Width * 0.5f + RoomHoops.Post * 0.5f);
                if (e.Type == "hover") top = Mathf.Max(top, e.Height + e.Width * 0.5f);
            }
            return top;
        }

        /// <summary>The slots, local to the course origin, all on the floor. Slots an element doesn't use are empty.</summary>
        public static Dictionary<string, List<Pose>> Slots(string id, Settings s, float t, bool narrow)
        {
            var cones = new List<Pose>();
            var gates = new List<Pose>();
            var posts = new List<Pose>();
            var hoops = new List<Pose>();
            var path = new List<Pose> { At(0f, 0f) };
            float z = 0f;
            int side = 0;
            foreach (var e in s.Elements)
            {
                float gap = Mathf.Lerp(e.MinSpacing, e.Spacing, t);
                switch (e.Type)
                {
                    case "straight":
                        z += Mathf.Lerp(e.MinLength, e.Length, t);
                        path.Add(At(0f, z));
                        break;
                    case "cones":
                        for (int i = 0; i < e.Count; i++)
                        {
                            z += gap;
                            float x = (side++ % 2 == 0 ? 0.5f : -0.5f) * WeaveLane;
                            cones.Add(At(0f, z));
                            gates.Add(At(x, z));
                            path.Add(At(x, z));
                        }
                        break;
                    case "gates":
                        for (int i = 0; i < e.Count; i++)
                        {
                            z += gap;
                            gates.Add(At(0f, z));
                            if (!s.Drone)
                            {
                                posts.Add(At(-GateHalf, z));
                                posts.Add(At(GateHalf, z));
                            }
                            path.Add(At(0f, z));
                        }
                        break;
                    case "hoops":
                        float weave = narrow ? 0f : RoomHoops.Weave(e.Width);
                        for (int i = 0; i < e.Count; i++)
                        {
                            z += gap;
                            float x = side++ % 2 == 0 ? weave : -weave;
                            hoops.Add(At(x, z));
                            path.Add(At(x, z));
                        }
                        break;
                    default:  // bay, pad, hover: the run-up, then the spot itself
                        z += gap;
                        path.Add(At(0f, z));
                        break;
                }
            }
            if (!s.End.HasValue)
            {
                z += Mathf.Lerp(TailMin, Tail, t);
                path.Add(At(0f, z));
            }
            return new Dictionary<string, List<Pose>>
            {
                [id + ".cones"] = cones, [id + ".gates"] = gates, [id + ".posts"] = posts, [id + ".hoops"] = hoops, [id + ".path"] = path,
                [id + ".start"] = new List<Pose> { At(0f, 0f) },
                [id + ".finish"] = new List<Pose> { At(0f, z) },
            };
        }

        static Pose At(float x, float z) => new Pose(new Vector3(x, 0f, z), Quaternion.identity);

        // ---------- fitting ----------

        // The shared search takes "count markers, spacing apart" and tests a strip spacing * (count + 1) long. One marker
        // with half the course's length as its spacing is the same strip, so the whole course shrinks 20 cm at a time.
        static float Half(float length) => Mathf.Round(length * 50f) / 100f;

        static float Share(float length, float min, float full) => full - min < 0.005f ? 1f : Mathf.Clamp01((length - min) / (full - min));

        public static FloorSlalom.Result Fit(IFloorPlan room, Settings s, string id = "course")
        {
            if (room == null || !room.HasFloor) return FloorSlalom.NoRoomData();
            if (s.Elements.Count == 0)
                return new FloorSlalom.Result { Code = "bad_value", Message = "This course has nothing in it, so there's nothing to place." };

            float top = Headroom(s), ceiling = room.CeilingHeight;
            bool noCeiling = top > 0f && float.IsNaN(ceiling);
            if (top > 0f && !noCeiling && top + s.Clearance > ceiling + 0.001f)
                return new FloorSlalom.Result
                {
                    Code = "no_headroom",
                    Message = string.Format(CultureInfo.InvariantCulture,
                        "The ceiling here is only {0:0.00} m, and this course needs {1:0.00} m: it reaches {2:0.00} m up, with {3:0} cm to spare above it. It doesn't fit in this room.",
                        ceiling, top + s.Clearance, top, s.Clearance * 100f),
                };

            float over = Overhang(s);
            float full = FinishZ(s, 1f) + over, min = FinishZ(s, 0f) + over;
            float lane = Lane(s, false), narrowLane = Lane(s, true);
            bool canNarrow = narrowLane < lane - 0.001f;
            Func<int, float, Dictionary<string, List<Pose>>> wide = (n, gap) => Slots(id, s, Share(2f * gap, min, full), false);
            Func<int, float, Dictionary<string, List<Pose>>> tight = (n, gap) => Slots(id, s, Share(2f * gap, min, full), true);

            var fit = FloorSlalom.Search(room, 1, 1, Half(full), Half(min), lane, s.Clearance, s.Around, wide, true);
            if (!fit.Ok && canNarrow)
                fit = FloorSlalom.Narrow(fit, FloorSlalom.Search(room, 1, 1, Half(min), Half(min), narrowLane, s.Clearance, s.Around, tight, true));
            if (!fit.Ok)
            {
                bool boundaryTooSmall = room.Boundary != null &&
                    (FloorSlalom.Search(room, 1, 1, Half(full), Half(min), lane, s.Clearance, s.Around, wide, false).Ok ||
                     (canNarrow && FloorSlalom.Search(room, 1, 1, Half(min), Half(min), narrowLane, s.Clearance, s.Around, tight, false).Ok));
                fit.BoundaryLimited = boundaryTooSmall;
                fit.Message = boundaryTooSmall
                    ? FloorSlalom.BoundaryMessage(room, "this course")
                    : string.Format(CultureInfo.InvariantCulture,
                        "There isn't enough clear floor here for this course. Even squeezed up it needs a clear strip about {0:0.0} by {1:0.0} m, with {2:0} cm to spare around it. Ask for fewer parts, or a smaller one.",
                        2f * Half(min) + 2f * FloorSlalom.EndMargin, 2f * narrowLane, s.Clearance * 100f);
                return fit;
            }
            fit.Message = Message(s, fit, 2f * fit.Spacing - over, noCeiling, top);
            return fit;
        }

        /// <summary>Whether a saved course still has free floor and headroom. <paramref name="spacing"/> and <paramref name="lane"/> are the layout's own.</summary>
        public static bool StillFits(IFloorPlan room, Vector3 origin, float yaw, float spacing, Settings s, float lane = 0f, bool useBoundary = true)
        {
            if (room == null || !room.HasFloor) return false;
            float top = Headroom(s), ceiling = room.CeilingHeight;
            if (top > 0f && !float.IsNaN(ceiling) && top + s.Clearance > ceiling + 0.001f) return false;
            float side = lane > 0f ? lane : Lane(s, false);
            return useBoundary
                ? FloorSlalom.StillFits(room, origin, yaw, 1, spacing, side, s.Clearance)
                : FloorSlalom.StillFitsWithoutBoundary(room, origin, yaw, 1, spacing, side, s.Clearance);
        }

        /// <summary>True for a course that is one straight and a finish line: the dash.</summary>
        public static bool IsDash(Settings s) => s.Elements.Count == 1 && s.Elements[0].Type == "straight";

        static string Message(Settings s, FloorSlalom.Result fit, float length, bool noCeiling, float top)
        {
            bool missing = !string.IsNullOrEmpty(s.Around) && !fit.AroundFound;
            string thing = FloorSlalom.Thing(s.Around);
            string size = string.Format(CultureInfo.InvariantCulture, "{0:0.0} m", length);
            string say = null;
            if (fit.Narrow)
                say = (missing ? $"I can't find {thing}, and this" : "This") + $" room is very tight, so I made the course short and narrow: {size} end to end, with less room either side.";
            else if (fit.Shrunk && IsDash(s))
                say = (missing ? $"I can't find {thing}. " : "") + $"The longest clear straight I found here is {size}, so the dash is that long.";
            else if (fit.Shrunk)
                say = (missing ? $"This room is tight and I can't find {thing}" : "This room is tight") + $", so I squeezed the course up: {size} end to end.";
            else if (missing) say = $"I can't find {thing}, so I put the course on the most open floor.";
            if (noCeiling)
                say = (say != null ? say + " " : "") + string.Format(CultureInfo.InvariantCulture,
                    "I can't find the ceiling in this scan, so check there's room above: the course reaches {0:0.00} m up.", top);
            return say;
        }

        // ---------- checking a spec (the validator, and artifact_create's course) ----------

        /// <summary>
        /// What is wrong with a <c>room_course</c> params block, or with the same fields of a course spec, as errors the
        /// agent can act on. <paramref name="refs"/>: numbers may be <c>$input</c> references (a scene), or must be plain
        /// numbers (a spec from the agent).
        /// </summary>
        public static void Check(JObject p, string path, bool refs, List<ArtifactError> errors)
        {
            string Sub(string key) => string.IsNullOrEmpty(path) ? key : path + "." + key;

            string thing = "truck";
            var thingToken = p["thing"];
            if (thingToken != null && thingToken.Type != JTokenType.Null)
            {
                if (Array.IndexOf(Things, Text(thingToken)) < 0)
                    errors.Add(new ArtifactError("bad_value", Sub("thing"), $"{Show(thingToken)} isn't a Thing a course can be for", "use \"truck\" (drives on the floor or a table) or \"drone\" (flies)", Things));
                else thing = Text(thingToken);
            }
            var surface = p["surface"];
            if (surface != null && surface.Type != JTokenType.Null && Array.IndexOf(ArtifactSchema.Surfaces, Text(surface)) < 0)
                errors.Add(new ArtifactError("bad_value", Sub("surface"), $"{Show(surface)} isn't a surface", "use \"floor\", or \"desk\" for the top of a table (the course is made smaller to fit it)", ArtifactSchema.Surfaces));
            var around = p["around"];
            if (around != null && around.Type != JTokenType.Null && Array.IndexOf(Labels, Text(around)) < 0)
                errors.Add(new ArtifactError("bad_value", Sub("around"), $"{Show(around)} isn't a furniture label", "leave \"around\" out for the most open floor, or name what the course goes beside in capitals, like \"COUCH\"", Labels));
            var clearance = p["clearance_m"];
            if (clearance != null && clearance.Type != JTokenType.Null && !IsRef(clearance, refs) && !(Num(clearance) is double c && c >= 0.02 && c <= 1.0))
                errors.Add(new ArtifactError("bad_value", Sub("clearance_m"), $"must be a number from 0.02 to 1, found {Show(clearance)}", "leave it out for 0.15 m"));

            string at = Sub("elements");
            if (!(p["elements"] is JArray list) || list.Count == 0)
            {
                errors.Add(new ArtifactError("bad_value", at, p["elements"] == null ? "required: the ordered list of what the course is made of" : $"must be a list with at least one element, found {Show(p["elements"])}",
                    "for example \"elements\": [{\"type\": \"straight\", \"length_m\": 2}, {\"type\": \"cones\", \"count\": 4}, {\"type\": \"bay\"}]", ElementTypes));
                return;
            }
            if (list.Count > MaxElements)
                errors.Add(new ArtifactError("too_big", at, $"{list.Count} elements is over the limit of {MaxElements}", "merge neighbours of the same type by raising their count, or drop some"));

            double shortest = 0;
            for (int i = 0; i < list.Count; i++)
            {
                string here = $"{at}[{i}]";
                if (!(list[i] is JObject e))
                {
                    errors.Add(new ArtifactError("bad_value", here, $"must be an object like {{\"type\": \"cones\", \"count\": 4}}, found {Show(list[i])}", null, ElementTypes));
                    continue;
                }
                string type = Text(e["type"]);
                if (type == null || !ElementKeys.TryGetValue(type, out var keys))
                {
                    errors.Add(new ArtifactError("bad_value", here + ".type", $"{Show(e["type"])} isn't a course element",
                        "straight: clear floor. cones: cones to weave. gates: checkpoints to pass in order. hoops: hoops to fly through. bay or pad: a marked spot to stop or land on. hover: a box in the air to hold inside", ElementTypes));
                    continue;
                }
                foreach (var prop in e.Properties())
                {
                    if (prop.Name == "type") continue;
                    if (Array.IndexOf(keys, prop.Name) < 0)
                    {
                        errors.Add(new ArtifactError("unknown_field", here + "." + prop.Name, $"a {type} element has no field \"{prop.Name}\"", $"remove it; a {type} element takes: type, {string.Join(", ", keys)}", keys));
                        continue;
                    }
                    if (prop.Value.Type == JTokenType.Null || IsRef(prop.Value, refs)) continue;
                    var range = Ranges[prop.Name];
                    bool whole = prop.Name == "count";
                    if (!(Num(prop.Value) is double v) || v < range[0] - 1e-9 || v > range[1] + 1e-9 || (whole && Math.Abs(v - Math.Round(v)) > 1e-9))
                        errors.Add(new ArtifactError("bad_value", here + "." + prop.Name,
                            string.Format(CultureInfo.InvariantCulture, "must be a {0} from {1} to {2}, found {3}", whole ? "whole number" : "number", range[0], range[1], Show(prop.Value)),
                            prop.Value.Type == JTokenType.String ? "write the number itself, not text" : "metres and seconds; leave it out for the usual size"));
                }
                if ((type == "hoops" || type == "hover") && thing != "drone")
                    errors.Add(new ArtifactError("bad_value", here + ".type", $"a {type} element needs a Thing that flies, and this course is for the {thing}",
                        "set \"thing\": \"drone\", or use cones, gates and a bay for the truck", new[] { "straight", "cones", "gates", "bay" }));
                bool ends = type == "bay" || type == "pad" || type == "hover";
                if (ends && i != list.Count - 1)
                    errors.Add(new ArtifactError("bad_value", here, $"a {type} ends the run, so it has to be the last element",
                        $"move it to the end of the list, or drop it: a course with no bay, pad or hover ends in a finish line"));
                var d = Defaults(type, thing == "drone");
                shortest += type == "straight" ? (Num(e["min_length_m"]) ?? Math.Min(d.MinLength, Num(e["length_m"]) ?? d.MinLength))
                    : Math.Min(d.MinSpacing, Num(e["spacing_m"]) ?? d.MinSpacing) * Math.Max(1, Num(e["count"]) ?? d.Count);
            }
            if (shortest > MaxLength)
                errors.Add(new ArtifactError("too_big", at, string.Format(CultureInfo.InvariantCulture, "even squeezed up this course is {0:0.#} m long, over the limit of {1:0} m", shortest, MaxLength),
                    "use fewer elements, lower counts or shorter straights"));
        }

        static bool IsRef(JToken t, bool refs) => refs && t.Type == JTokenType.String && ((string)t).StartsWith("$");
        static string Text(JToken t) => t != null && t.Type == JTokenType.String ? (string)t : null;
        static double? Num(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) ? (double)t : (double?)null;
        static double R(float v) => Math.Round(v, 2);

        static float Number(JToken t, ArtifactInputs inputs, float fallback)
        {
            if (t == null) return fallback;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return (float)t;
            if (t.Type == JTokenType.String && inputs != null) return inputs.Number(t, fallback);
            return fallback;
        }

        static string Show(JToken t)
        {
            if (t == null) return "nothing";
            string text = t.ToString(Newtonsoft.Json.Formatting.None);
            return text.Length > 40 ? text.Substring(0, 37) + "..." : text;
        }
    }
}
