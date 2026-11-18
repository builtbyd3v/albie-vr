using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Forge.Artifacts;
using Forge.Notebook;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Headless checks for the host 7 courses: the room_course generator against every mock room, the built-in dash, parking,
/// hover and landing courses, a course from a description (artifact_create's course), a course on the table, and the
/// run endings they need (stopped inside a bay, held inside a box, three in a row). Forge > Artifacts > Check Courses.
/// </summary>
public static class CourseCheck
{
    static string root;
    static string NotebookRoot => root + "_nb";

    [MenuItem("Forge/Artifacts/Check Courses")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    public static void RunBatch()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
        EditorApplication.Exit(report.StartsWith("PASS") ? 0 : 1);
    }

    public static string Run()
    {
        root = Path.Combine(Application.temporaryCachePath, "course_check");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        if (Directory.Exists(NotebookRoot)) Directory.Delete(NotebookRoot, true);
        string before = ArtifactLibrary.RootOverride;
        string notebookProject = NotebookStore.ProjectId, notebookRoot = NotebookStore.Root;
        ArtifactLibrary.RootOverride = root;
        NotebookStore.Close();
        var sb = new StringBuilder();
        bool ok = true;
        try
        {
            ok &= Check(sb, "dash, living room: a straight on free floor, up to what was asked", DashLivingRoom);
            ok &= Check(sb, "dash, small room: the longest clear straight, and says so", DashSmallRoom);
            ok &= Check(sb, "closet: no_free_floor with the strip it needs; never scanned: no_room_data", NoFloor);
            ok &= Check(sb, "every built-in course in every mock room: placed on free floor, or refused with a reason", EveryRoom);
            ok &= Check(sb, "a low ceiling refuses the hover box, not the dash", Headroom);
            ok &= Check(sb, "slots: elements in order, a bay ends the course, unused slots empty", Slots);
            ok &= Check(sb, "the built-in courses compile to scenes the validator takes, host 7", Presets);
            ok &= Check(sb, "a course from a description compiles; a bad one says what to fix", Specs);
            ok &= Check(sb, "on the table: scaled to the top, inside its edge; no table says so", Desk);
            ok &= Check(sb, "the slalom on the table too: same generator, smaller", SlalomOnDesk);
            ok &= Check(sb, "layout saved and reused, floor and desk", Layouts);
            ok &= Check(sb, "parking: stopped inside the bay completes; driving through does not", Parking);
            ok &= Check(sb, "parking: three in a row counts, a failed run resets it", Streak);
            ok &= Check(sb, "hover: held for the time completes; leaving the box starts the count again; drift measured", Hover);
            ok &= Check(sb, "landing: the clock starts at take-off; the pad needs the gate first", Landing);
            ok &= Check(sb, "the slalom's finish still ends on entering it", SlalomUnchanged);
            ok &= Check(sb, "the tools make them by name and from a course", Tools);
        }
        finally
        {
            ArtifactLibrary.RootOverride = before;
            NotebookStore.Close();
            if (notebookProject != null) NotebookStore.Open(notebookProject, notebookRoot);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(NotebookRoot)) Directory.Delete(NotebookRoot, true);
        }
        return (ok ? "PASS" : "FAIL") + " course check\n" + sb;
    }

    delegate string CheckFn(out string note);

    static bool Check(StringBuilder sb, string name, CheckFn fn)
    {
        string failure, note;
        try { failure = fn(out note); }
        catch (Exception e) { failure = e.GetType().Name + ": " + e.Message; note = null; }
        sb.Append(failure == null ? "  ok   " : "  FAIL ").Append(name);
        if (failure != null) sb.Append(": ").Append(failure);
        else if (!string.IsNullOrEmpty(note)) sb.Append(": ").Append(note);
        sb.Append('\n');
        return failure == null;
    }

    static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    // ---------- the generator ----------

    static RoomCourse.Settings Settings(string template, float length = 6f, float hold = 60f)
    {
        var spec = ArtifactCourses.Preset(template, null, out var inputs);
        var manifest = new JObject { ["inputs"] = inputs };
        var live = new ArtifactInputs(manifest);
        if (live.Has("length_m")) live.TrySet("length_m", length);
        if (live.Has("hold_s")) live.TrySet("hold_s", hold);
        return RoomCourse.Read(spec, live);
    }

    // Independent of the generator's grid: walks the footprint every 5 cm and asks the room directly.
    static string OnFreeFloor(IFloorPlan room, FloorSlalom.Result fit, float clearance)
    {
        var r = Quaternion.Euler(0f, fit.Yaw, 0f);
        float end = fit.Spacing * (fit.Gates + 1) + FloorSlalom.EndMargin;
        float c = clearance - FloorSlalom.Cell * 0.75f;
        for (float z = -FloorSlalom.EndMargin; z <= end; z += 0.05f)
            for (float x = -fit.Lane; x <= fit.Lane; x += 0.05f)
                if (!room.IsFree(fit.Origin + r * new Vector3(x, 0f, z), c)) return $"footprint point ({F(x)}, {F(z)}) isn't free floor";
        foreach (var slot in fit.Slots)
            foreach (var pose in slot.Value)
                if (!room.IsFree(fit.Origin + r * pose.position, c)) return slot.Key + " has a place off free floor";
        return null;
    }

    static string DashLivingRoom(out string note)
    {
        note = null;
        var room = MockFloorPlan.LivingRoom();
        var s = Settings("dash", 4f);
        var fit = RoomCourse.Fit(room, s);
        if (!fit.Ok) return fit.Code + ": " + fit.Message;
        float length = fit.Slots["course.finish"][0].position.z;
        if (Mathf.Abs(length - 4f) > 0.03f || fit.Shrunk || fit.Message != null) return $"asked for 4 m, got {F(length)} (shrunk {fit.Shrunk}): {fit.Message}";
        if (fit.Slots["course.cones"].Count != 0 || fit.Slots["course.path"].Count != 2) return "a dash has no cones and a two-point guide";
        note = $"4.00 m at yaw {F(fit.Yaw)}; {fit.Tried} placements tried";
        return OnFreeFloor(room, fit, s.Clearance);
    }

    static string DashSmallRoom(out string note)
    {
        note = null;
        var room = MockFloorPlan.SmallRoom();
        var s = Settings("dash", 10f);
        var fit = RoomCourse.Fit(room, s);
        if (!fit.Ok) return fit.Code + ": " + fit.Message;
        float length = fit.Slots["course.finish"][0].position.z;
        if (!fit.Shrunk || length >= 10f || length < 1.5f - 0.02f) return $"length {F(length)}, shrunk {fit.Shrunk}";
        if (fit.Message == null || !fit.Message.Contains("longest clear straight")) return "message: " + fit.Message;
        // Longest: 20 cm more doesn't fit anywhere.
        var longer = Settings("dash", Mathf.Min(10f, length + 0.4f));
        var e = longer.Elements[0];
        e.MinLength = e.Length;
        longer.Elements[0] = e;
        if (RoomCourse.Fit(room, longer).Ok) return $"a dash 40 cm longer than {F(length)} m fits too";
        note = $"asked for 10 m: \"{fit.Message}\"";
        return OnFreeFloor(room, fit, s.Clearance);
    }

    static string NoFloor(out string note)
    {
        note = null;
        var closet = RoomCourse.Fit(MockFloorPlan.Closet(), Settings("park"));
        if (closet.Ok || closet.Code != "no_free_floor" || closet.Message == null || !closet.Message.Contains("clear strip")) return "closet: " + closet.Code + " " + closet.Message;
        var none = RoomCourse.Fit(MockFloorPlan.Unscanned(), Settings("dash"));
        var missing = RoomCourse.Fit(null, Settings("dash"));
        if (none.Ok || none.Code != "no_room_data" || missing.Code != "no_room_data") return $"{none.Code} / {missing.Code}";
        note = $"\"{closet.Message}\"";
        return null;
    }

    static string EveryRoom(out string note)
    {
        note = null;
        int placed = 0, refused = 0;
        foreach (var room in MockFloorPlan.All())
            foreach (string template in ArtifactCourses.Templates)
            {
                var s = Settings(template);
                var fit = RoomCourse.Fit(room, s);
                if (!fit.Ok)
                {
                    if (string.IsNullOrEmpty(fit.Code) || string.IsNullOrEmpty(fit.Message)) return $"{template} in {room.Name}: refused with no reason";
                    refused++;
                    continue;
                }
                string off = OnFreeFloor(room, fit, s.Clearance);
                if (off != null) return $"{template} in {room.Name}: {off}";
                if (!RoomCourse.StillFits(room, fit.Origin, fit.Yaw, fit.Spacing, s, fit.Lane)) return $"{template} in {room.Name}: doesn't still fit where it was put";
                placed++;
            }
        if (placed == 0) return "nothing was placed anywhere";
        note = $"{placed} placed, {refused} refused with a reason";
        return null;
    }

    static string Headroom(out string note)
    {
        note = null;
        var attic = MockFloorPlan.Attic();
        var hover = RoomCourse.Fit(attic, Settings("hover"));
        if (hover.Ok || hover.Code != "no_headroom" || !hover.Message.Contains("1.40 m")) return "hover: " + hover.Code + " " + hover.Message;
        if (!RoomCourse.Fit(attic, Settings("dash")).Ok) return "a dash should fit in the attic";
        var unlabelled = RoomCourse.Fit(MockFloorPlan.Unlabelled(), Settings("hover"));
        if (!unlabelled.Ok || unlabelled.Message == null || !unlabelled.Message.Contains("can't find the ceiling")) return "no ceiling: " + unlabelled.Code + " " + unlabelled.Message;
        note = $"\"{hover.Message}\"";
        return null;
    }

    static JObject Spec(string thing, params JObject[] elements) =>
        new JObject { ["title"] = "Test run", ["thing"] = thing, ["elements"] = new JArray(elements) };

    static JObject E(string type, int count = 0)
    {
        var e = new JObject { ["type"] = type };
        if (count > 0) e["count"] = count;
        return e;
    }

    static string Slots(out string note)
    {
        note = null;
        var spec = Spec("truck", new JObject { ["type"] = "straight", ["length_m"] = 2.0 }, E("cones", 3), E("gates", 2), E("bay"));
        var s = RoomCourse.Read(spec, null);
        var slots = RoomCourse.Slots("course", s, 1f, false);
        if (slots["course.cones"].Count != 3 || slots["course.gates"].Count != 5 || slots["course.posts"].Count != 4 || slots["course.hoops"].Count != 0) return "counts";
        float z = 2f;
        foreach (var cone in slots["course.cones"])
        {
            z += 1.2f;
            if (Mathf.Abs(cone.position.z - z) > 0.001f || Mathf.Abs(cone.position.x) > 0.001f) return "a cone is off the line";
        }
        if (Mathf.Abs(slots["course.gates"][0].position.x - 0.3f) > 0.001f || Mathf.Abs(slots["course.gates"][1].position.x + 0.3f) > 0.001f) return "weave gates don't alternate";
        if (Mathf.Abs(slots["course.gates"][3].position.x) > 0.001f) return "a drive-through gate is off the centre line";
        float finish = slots["course.finish"][0].position.z;
        if (Mathf.Abs(finish - (2f + 3 * 1.2f + 2 * 1.5f + 1.2f)) > 0.001f) return "finish at " + F(finish);
        if (Mathf.Abs(RoomCourse.FinishZ(s, 1f) - finish) > 0.001f) return "FinishZ and the slots disagree";
        float squeezed = RoomCourse.Slots("course", s, 0f, false)["course.finish"][0].position.z;
        if (squeezed >= finish || Mathf.Abs(RoomCourse.FinishZ(s, 0f) - squeezed) > 0.001f) return "squeezed finish at " + F(squeezed);
        foreach (var slot in slots)
            foreach (var pose in slot.Value)
                if (Mathf.Abs(pose.position.y) > 1e-5f) return slot.Key + " isn't on the floor";
        note = $"finish {F(finish)} m at full size, {F(squeezed)} m squeezed";
        return null;
    }

    // ---------- compiling ----------

    static string Presets(out string note)
    {
        note = null;
        var said = new List<string>();
        foreach (string template in ArtifactCourses.Templates)
            foreach (string surface in new[] { "floor", "desk" })
            {
                var spec = ArtifactCourses.Preset(template, surface, out var inputs);
                if (!ArtifactCourses.Compile(spec, inputs, out var manifest, out var scene, out var errors)) return $"{template}/{surface}: {errors[0]}";
                var problems = ArtifactValidator.Validate(manifest, scene);
                if (problems.Count > 0) return $"{template}/{surface}: {problems[0]}";
                if (ArtifactNodes.Missing(scene).Count > 0) return $"{template}/{surface} can't be built: {string.Join(", ", ArtifactNodes.Missing(scene))}";
                if ((int)manifest["runtime"]["min_runtime"] != 7 || ArtifactTools.MinRuntime(scene) != 7) return template + " min_runtime";
                if ((string)manifest["links"]["thing"] != ArtifactCourses.ThingOf(template)) return template + " thing";
                var plausible = ArtifactPlausibility.Check(manifest, scene);
                if (plausible.Errors.Count > 0) return $"{template}/{surface}: {plausible.Errors[0]}";
                if (surface == "floor") said.Add($"{template}: {((JArray)scene["nodes"]).Count} nodes");
            }
        // An old host refuses them instead of showing half a course.
        var dash = ArtifactCourses.Preset("dash", null, out var sliders);
        ArtifactCourses.Compile(dash, sliders, out var m, out var sc, out _);
        m["runtime"]["min_runtime"] = ArtifactSchema.HostRuntime + 1;
        var newer = ArtifactValidator.Validate(m, sc);
        if (newer.Count == 0 || newer[0].code != "newer_schema") return "a newer min_runtime wasn't refused";
        note = string.Join("; ", said);
        return null;
    }

    static string Has(List<ArtifactError> errors, string code, string pathPart)
    {
        foreach (var e in errors)
            if (e.code == code && e.path != null && e.path.Contains(pathPart)) return e.hint == null ? "no hint on " + e : null;
        return $"no {code} at {pathPart} (got {(errors.Count > 0 ? errors[0].ToString() : "nothing")})";
    }

    static string Specs(out string note)
    {
        note = null;
        var good = Spec("truck", new JObject { ["type"] = "straight", ["length_m"] = 2 }, E("cones", 4), E("bay"));
        good["around"] = "COUCH";
        if (!ArtifactCourses.Compile(good, null, out var manifest, out var scene, out var errors)) return "a sound course was refused: " + errors[0];
        var problems = ArtifactValidator.Validate(manifest, scene);
        if (problems.Count > 0) return "its scene doesn't validate: " + problems[0];
        if ((string)manifest["id"] != "test_run" || (string)scene["space"] != "room") return "id " + manifest["id"];
        if ((string)scene["run"]["finish"]["else_reason"] != "missed_gate" || scene["run"]["finish"]["hold_s"] == null || scene["run"]["finish"]["max_speed_ms"] == null) return "a bay's finish isn't held and slow";

        var drone = Spec("drone", E("hoops", 3), new JObject { ["type"] = "hover", ["hold_s"] = 5 });
        if (!ArtifactCourses.Compile(drone, null, out manifest, out scene, out errors)) return "a drone course was refused: " + errors[0];
        problems = ArtifactValidator.Validate(manifest, scene);
        if (problems.Count > 0) return "the drone scene doesn't validate: " + problems[0];
        if (scene["run"]["finish"]["max_speed_ms"] != null || (double)scene["run"]["finish"]["hold_s"] != 5) return "a hover's finish is held, not slow";

        string miss;
        if ((miss = Has(ArtifactCourses.Check(Spec("truck", E("hoops", 3))), "bad_value", "elements[0].type")) != null) return "hoops for a truck: " + miss;
        if ((miss = Has(ArtifactCourses.Check(Spec("truck", E("bay"), E("cones", 3))), "bad_value", "elements[0]")) != null) return "a bay first: " + miss;
        if ((miss = Has(ArtifactCourses.Check(Spec("truck", new JObject { ["type"] = "cones", ["count"] = 40 })), "bad_value", "count")) != null) return "40 cones: " + miss;
        if ((miss = Has(ArtifactCourses.Check(Spec("truck", new JObject { ["type"] = "cones", ["pos"] = new JArray(0, 0, 1) })), "unknown_field", "pos")) != null) return "a position: " + miss;
        if ((miss = Has(ArtifactCourses.Check(Spec("boat", E("cones", 3))), "bad_value", "thing")) != null) return "a boat: " + miss;
        if ((miss = Has(ArtifactCourses.Check(new JObject { ["thing"] = "truck" }), "bad_value", "elements")) != null) return "no elements: " + miss;
        var withNodes = Spec("truck", E("cones", 3));
        withNodes["nodes"] = new JArray();
        if ((miss = Has(ArtifactCourses.Check(withNodes), "unknown_field", "nodes")) != null) return "nodes in a course: " + miss;
        // A room scene written by hand is still refused, and now says how to ask for a course.
        var hand = ArtifactValidator.Validate(manifest, new JObject { ["schema"] = ArtifactSchema.Scene, ["space"] = "room", ["nodes"] = new JArray() });
        bool pointed = false;
        foreach (var e in hand) pointed |= e.hint != null && e.hint.Contains("course:");
        if (!pointed) return "a hand-written room scene isn't pointed at course";
        note = "truck and drone courses compile and validate; six bad specs each say what to fix";
        return null;
    }

    // ---------- the desk ----------

    static JObject Generator(JObject scene) => (JObject)((JArray)scene["generators"])[0];

    static string Desk(out string note)
    {
        note = null;
        var room = MockFloorPlan.LivingRoom();  // a 1.0 x 0.5 m coffee table, 0.45 m tall
        var spec = ArtifactCourses.Preset("dash", "desk", out var inputs);
        ArtifactCourses.Compile(spec, inputs, out var manifest, out var scene, out _);
        var live = new ArtifactInputs(manifest);
        var placed = ArtifactGenerators.Place(manifest, Generator(scene), live, null, room, false);
        if (!placed.Ok) return placed.Code + ": " + placed.Message;
        if (placed.Scale >= 1f || placed.Scale < 0.1f - 0.001f) return "scale " + F(placed.Scale);
        if (Mathf.Abs(placed.Origin.position.y - 0.45f) > 0.002f) return "not on the table top: y " + F(placed.Origin.position.y);
        if (placed.Message == null || !placed.Message.Contains("on the table")) return "message: " + placed.Message;
        if (!room.TryFind("TABLE", out var centre, out float yaw, out var size)) return "the mock has no table";
        var toTable = Quaternion.Euler(0f, -yaw, 0f);
        foreach (var slot in placed.Slots)
            foreach (var pose in slot.Value)
            {
                var world = placed.Origin.position + placed.Origin.rotation * (pose.position * placed.Scale);
                var local = toTable * (world - centre);
                if (Mathf.Abs(local.z) > size.x * 0.5f || Mathf.Abs(local.x) > size.z * 0.5f) return $"{slot.Key} is off the table top ({F(local.x)}, {F(local.z)})";
            }
        if ((string)placed.Layout["room"]["surface"] != "desk" || Mathf.Abs((float)placed.Layout["room"]["scale"] - placed.Scale) > 0.001f) return "the layout doesn't record the desk and its scale";

        var none = ArtifactGenerators.Place(manifest, Generator(scene), live, null, MockFloorPlan.Loft(), true);
        if (none.Ok || none.Code != "no_table" || !none.Message.Contains("table")) return "no table: " + none.Code + " " + none.Message;

        // A table too small for anything: a 20 x 15 cm stool top.
        var stool = new MockFloorPlan("stool_room", new Vector2(4f, 4f)).Add("TABLE", Vector2.zero, new Vector3(0.2f, 0.4f, 0.15f));
        var small = ArtifactGenerators.Place(manifest, Generator(scene), live, null, stool, true);
        if (small.Ok || small.Code != "no_free_floor" || !small.Message.Contains("too small")) return "tiny table: " + small.Code + " " + small.Message;
        note = $"scale {F(placed.Scale)}: \"{placed.Message}\"; no table: \"{none.Message}\"";
        return null;
    }

    static string SlalomOnDesk(out string note)
    {
        note = null;
        if (!ArtifactLibrary.TryTemplate("slalom", out var manifest, out var scene)) return "no slalom template";
        manifest["id"] = "slalom_desk_check";
        Generator(scene)["params"]["surface"] = "desk";
        var problems = ArtifactValidator.Validate(manifest, scene);
        if (problems.Count > 0) return "surface desk on the slalom: " + problems[0];
        var desk = new MockFloorPlan("desk_room", new Vector2(4f, 4f)).Add("TABLE", new Vector2(0f, 1f), new Vector3(1.6f, 0.75f, 0.8f));
        var placed = ArtifactGenerators.Place(manifest, Generator(scene), new ArtifactInputs(manifest), null, desk, false);
        if (!placed.Ok) return placed.Code + ": " + placed.Message;
        if (placed.Scale >= 1f || Mathf.Abs(placed.Origin.position.y - 0.75f) > 0.002f) return $"scale {F(placed.Scale)}, y {F(placed.Origin.position.y)}";
        if (placed.Slots["course.cones"].Count < 3) return "cones " + placed.Slots["course.cones"].Count;
        if (placed.Message.Contains("couch")) return "mentions a couch on a desk: " + placed.Message;
        Generator(scene)["params"]["surface"] = "shelf";
        if (ArtifactValidator.Validate(manifest, scene).Count == 0) return "surface shelf wasn't refused";
        note = $"{placed.Slots["course.cones"].Count} cones at scale {F(placed.Scale)}: \"{placed.Message}\"";
        return null;
    }

    static string Layouts(out string note)
    {
        note = null;
        var room = MockFloorPlan.LivingRoom();
        foreach (string surface in new[] { "floor", "desk" })
        {
            var spec = ArtifactCourses.Preset("park", surface, out var inputs);
            ArtifactCourses.Compile(spec, inputs, out var manifest, out var scene, out _);
            var live = new ArtifactInputs(manifest);
            var first = ArtifactGenerators.Place(manifest, Generator(scene), live, null, room, false);
            if (!first.Ok || first.Reused) return $"{surface} first: {first.Code} {first.Message}";
            var again = ArtifactGenerators.Place(manifest, Generator(scene), live, null, room, false);
            if (!again.Ok || !again.Reused || (string)again.Layout["layout_id"] != (string)first.Layout["layout_id"]) return surface + ": not reused";
            if ((again.Origin.position - first.Origin.position).magnitude > 0.01f || Mathf.Abs(again.Scale - first.Scale) > 0.001f) return surface + ": the reused course moved";
            var forced = ArtifactGenerators.Place(manifest, Generator(scene), live, null, room, true);
            if (!forced.Ok || forced.Reused || (string)forced.Layout["layout_id"] == (string)first.Layout["layout_id"]) return surface + ": relayout didn't make a new one";
        }
        note = "floor and desk: saved, reused in place, a relayout makes a new one";
        return null;
    }

    // ---------- runs ----------

    sealed class Body : IArtifactThing
    {
        public Vector3 At;
        public float Speed, Up = 1f;
        public string Thing = "truck";
        public string Name => Thing;
        public int Version => 1;
        public string ControllerName => "check";
        public string ControllerKind => "manual";
        public int ControllerVersion => 1;
        public Vector3 Position => At;
        public float SpeedMs => Speed;
        public float Upright => Up;
        public float HeightM => At.y;
        public float Radius => 0.1f;
        public bool Bound => true;
    }

    static Bounds NodeBox(JObject scene, string id, Vector3 floor)
    {
        foreach (JObject n in (JArray)scene["nodes"])
        {
            if ((string)n["id"] != id) continue;
            var t = n["kind"]?.ToString() == "array" ? (JObject)n["template"] : n;
            var pos = t["pos"].ToObject<float[]>();
            var size = t["size"].ToObject<float[]>();
            return new Bounds(floor + new Vector3(pos[0], pos[1], pos[2]), new Vector3(size[0], size[1], size[2]));
        }
        throw new ArgumentException("no node " + id);
    }

    sealed class Rig
    {
        public ArtifactRun Run;
        public Body Thing;
        public Bounds Start, Finish;
        public Bounds[] Gates;
    }

    static Rig Bind(string template, float hold = 0f)
    {
        var spec = ArtifactCourses.Preset(template, null, out var inputs);
        ArtifactCourses.Compile(spec, inputs, out var manifest, out var scene, out _);
        var live = new ArtifactInputs(manifest);
        if (hold > 0f && live.Has("hold_s")) live.TrySet("hold_s", hold);
        var s = RoomCourse.Read(Generator(scene)["params"] as JObject, live);
        var slots = RoomCourse.Slots("course", s, 1f, false);
        var rig = new Rig { Thing = new Body { Thing = ArtifactCourses.ThingOf(template) }, Run = new ArtifactRun() };
        rig.Start = NodeBox(scene, "start", slots["course.start"][0].position);
        rig.Finish = NodeBox(scene, "finish", slots["course.finish"][0].position);
        var zones = new Dictionary<string, Bounds[]> { ["start"] = new[] { rig.Start }, ["finish"] = new[] { rig.Finish } };
        var counts = new Dictionary<string, int>();
        foreach (var slot in slots) counts[slot.Key] = slot.Value.Count;
        if (slots["course.gates"].Count > 0)
        {
            rig.Gates = new Bounds[slots["course.gates"].Count];
            for (int i = 0; i < rig.Gates.Length; i++) rig.Gates[i] = NodeBox(scene, "gates", slots["course.gates"][i].position);
            zones["gates"] = rig.Gates;
        }
        rig.Run.Bind(manifest, scene, new JObject { ["layout_id"] = "lay_0001" }, live, rig.Thing, new ArtifactMetrics(), zones, null, counts);
        return rig;
    }

    static void Move(Rig rig, Vector3 to, float speed)
    {
        var from = rig.Thing.At;
        int steps = Mathf.Max(1, Mathf.CeilToInt((to - from).magnitude / 0.04f));
        rig.Thing.Speed = speed;
        for (int k = 1; k <= steps && rig.Run.State != ArtifactRunState.Finished; k++)
        {
            rig.Thing.At = Vector3.Lerp(from, to, k / (float)steps);
            rig.Run.Tick(0.02f);
        }
    }

    static void Wait(Rig rig, float seconds, float speed = 0f)
    {
        rig.Thing.Speed = speed;
        for (float t = 0f; t < seconds && rig.Run.State != ArtifactRunState.Finished; t += 0.02f) rig.Run.Tick(0.02f);
    }

    static string Park(Rig rig, bool stop)
    {
        rig.Thing.At = rig.Start.center;
        rig.Run.Arm();
        rig.Run.Tick(0.02f);
        if (rig.Run.State != ArtifactRunState.Running) return "the run didn't start in the start box";
        Move(rig, rig.Finish.center + new Vector3(0.05f, 0f, 0.1f), 1f);
        if (rig.Run.State == ArtifactRunState.Finished) return "it finished on entering the bay";
        if (stop) Wait(rig, 1.5f);
        else Move(rig, rig.Finish.center + new Vector3(0f, 0f, 3f), 1f);
        return null;
    }

    static string Parking(out string note)
    {
        note = null;
        var rig = Bind("park");
        string problem = Park(rig, true);
        if (problem != null) return problem;
        if (rig.Run.State != ArtifactRunState.Finished || rig.Run.Outcome != "completed") return $"stopped in the bay: {rig.Run.State} {rig.Run.Outcome} {rig.Run.Reason}";
        var record = rig.Run.TakeRecord();
        double off = (double)record["metrics"]["off_centre_m"];
        if (Math.Abs(off - 0.112) > 0.01) return "off centre " + off;
        if ((int)record["metrics"]["in_a_row"] != 1) return "in a row " + record["metrics"]["in_a_row"];

        var through = Bind("park");
        problem = Park(through, false);
        if (problem != null) return problem;
        if (through.Run.State == ArtifactRunState.Finished) return "driving through the bay finished the run: " + through.Run.Outcome;

        // Creeping in the bay isn't stopped.
        var creep = Bind("park");
        creep.Thing.At = creep.Start.center;
        creep.Run.Arm();
        creep.Run.Tick(0.02f);
        Move(creep, creep.Finish.center, 1f);
        Wait(creep, 3f, 0.3f);
        if (creep.Run.State == ArtifactRunState.Finished) return "creeping at 0.3 m/s counted as parked";
        note = $"parked {off:0.00} m off centre; driving through and creeping don't count";
        return null;
    }

    static string Streak(out string note)
    {
        note = null;
        var rig = Bind("park");
        for (int i = 1; i <= 3; i++)
        {
            string problem = Park(rig, true);
            if (problem != null) return problem;
            if (rig.Run.Outcome != "completed" || rig.Run.Streak != i) return $"park {i}: {rig.Run.Outcome}, streak {rig.Run.Streak}";
            if ((int)rig.Run.TakeRecord()["metrics"]["in_a_row"] != i) return "the record's in_a_row isn't " + i;
        }
        // A roll-over fails the run and the count starts again.
        rig.Thing.At = rig.Start.center;
        rig.Run.Arm();
        rig.Run.Tick(0.02f);
        rig.Thing.Up = 0f;
        Wait(rig, 1.5f, 0.5f);
        rig.Thing.Up = 1f;
        if (rig.Run.Outcome != "failed" || rig.Run.Reason != "rolled_over" || rig.Run.Streak != 0) return $"rolled over: {rig.Run.Outcome} {rig.Run.Reason}, streak {rig.Run.Streak}";
        note = "1, 2, 3, then 0 after a roll-over";
        return null;
    }

    static string Hover(out string note)
    {
        note = null;
        var rig = Bind("hover", 5f);
        // Landed at the start: below the start zone, so nothing runs yet.
        rig.Thing.At = new Vector3(rig.Start.center.x, 0.05f, rig.Start.center.z);
        rig.Run.Arm();
        Wait(rig, 0.5f);
        if (rig.Run.State != ArtifactRunState.Armed) return "the clock started before take-off";
        Move(rig, new Vector3(rig.Start.center.x, 0.8f, rig.Start.center.z), 0.5f);
        if (rig.Run.State != ArtifactRunState.Running) return "taking off didn't start the run";
        Move(rig, rig.Finish.center, 0.6f);
        Wait(rig, 3f, 0.05f);
        if (rig.Run.State == ArtifactRunState.Finished) return "3 s counted as 5";
        Move(rig, rig.Finish.center + new Vector3(0.9f, 0f, 0f), 0.6f);  // out of the box
        if (rig.Run.HeldS > 0.001f) return "leaving the box kept the count";
        Move(rig, rig.Finish.center + new Vector3(0.2f, 0.1f, 0f), 0.6f);
        Wait(rig, 4.8f, 0.05f);
        if (rig.Run.State == ArtifactRunState.Finished) return "the count didn't start again";
        Wait(rig, 0.5f, 0.05f);
        if (rig.Run.Outcome != "completed") return $"held 5 s: {rig.Run.State} {rig.Run.Outcome} {rig.Run.Reason}";
        var record = rig.Run.TakeRecord();
        double drift = (double)record["metrics"]["drift_m"], held = (double)record["metrics"]["held_s"];
        if (drift < 0.2 || drift > 0.75 || held < 4.9) return $"drift {drift}, held {held}";
        note = $"held {held:0.0} s, drift {drift:0.00} m";
        return null;
    }

    static string Landing(out string note)
    {
        note = null;
        var rig = Bind("landing");
        if (rig.Gates == null || rig.Gates.Length != 1) return "the landing course has one gate to climb through";
        if (rig.Start.min.y < 0.25f) return "the start zone reaches the floor, so the clock would start at arming";
        if (rig.Finish.max.y > 0.19f) return "the pad's zone reaches the lowest hover";

        // Straight to the pad along the floor: landed, but the gate was never passed.
        rig.Thing.At = new Vector3(0f, 0.05f, 0f);
        rig.Run.Arm();
        Move(rig, new Vector3(0f, 0.8f, 0f), 0.5f);
        Move(rig, new Vector3(0.7f, 0.8f, 0.3f), 0.5f);  // around the gate, not through it
        Move(rig, new Vector3(0.7f, 0.25f, rig.Finish.center.z), 0.5f);
        Move(rig, new Vector3(0f, 0.05f, rig.Finish.center.z), 0.5f);
        Wait(rig, 2f);
        if (rig.Run.Outcome != "failed" || rig.Run.Reason != "missed_gate") return $"skipping the gate: {rig.Run.Outcome} {rig.Run.Reason}";

        rig.Thing.At = new Vector3(0f, 0.05f, 0f);
        rig.Run.Arm();
        Move(rig, new Vector3(0f, 0.8f, 0f), 0.5f);
        Move(rig, rig.Gates[0].center, 0.8f);
        Move(rig, new Vector3(0f, 0.9f, rig.Finish.center.z), 0.8f);
        Wait(rig, 2f, 0.02f);  // hovering over the pad isn't landed
        if (rig.Run.State == ArtifactRunState.Finished) return "hovering over the pad counted as landed";
        Move(rig, new Vector3(0f, 0.05f, rig.Finish.center.z), 0.4f);
        Wait(rig, 2f);
        if (rig.Run.Outcome != "completed") return $"landed: {rig.Run.State} {rig.Run.Outcome} {rig.Run.Reason}";
        note = "take-off starts the clock; the gate, then at rest on the pad";
        return null;
    }

    static string SlalomUnchanged(out string note)
    {
        note = null;
        ArtifactLibrary.TryTemplate("slalom", out var manifest, out var scene);
        var slots = FloorSlalom.Slots("course", 5, 1.2f, 0.6f);
        var start = NodeBox(scene, "start", slots["course.start"][0].position);
        var finish = NodeBox(scene, "finish", slots["course.finish"][0].position);
        var gates = new Bounds[5];
        for (int i = 0; i < 5; i++) gates[i] = NodeBox(scene, "gates", slots["course.gates"][i].position);
        var rig = new Rig { Thing = new Body(), Run = new ArtifactRun(), Start = start, Finish = finish, Gates = gates };
        rig.Run.Bind(manifest, scene, new JObject { ["layout_id"] = "lay_0001" }, new ArtifactInputs(manifest), rig.Thing, new ArtifactMetrics(),
            new Dictionary<string, Bounds[]> { ["start"] = new[] { start }, ["finish"] = new[] { finish }, ["gates"] = gates }, null,
            new Dictionary<string, int> { ["course.gates"] = 5, ["course.cones"] = 5, ["course.start"] = 1, ["course.finish"] = 1 });
        rig.Thing.At = start.center;
        rig.Run.Arm();
        rig.Run.Tick(0.02f);
        foreach (var gate in gates) Move(rig, gate.center, 1f);
        Move(rig, finish.center, 1f);
        if (rig.Run.State != ArtifactRunState.Finished || rig.Run.Outcome != "completed") return $"{rig.Run.State} {rig.Run.Outcome} {rig.Run.Reason}";
        if (rig.Run.HeldS != 0f) return "a slalom finish was held";
        note = "completed on entering the finish, as before";
        return null;
    }

    // ---------- the tools ----------

    static string Tools(out string note)
    {
        note = null;
        var list = ArtifactTools.List(new JObject());
        foreach (string template in ArtifactCourses.Templates)
        {
            bool listed = false;
            foreach (var t in (JArray)list["templates"]) listed |= (string)t["template"] == template;
            if (!listed) return "artifact_list doesn't offer " + template;
            var made = ArtifactTools.Create(new JObject { ["template"] = template, ["open"] = false });
            if ((string)made["id"] != template || made["error"] != null || made["status"] != null) return $"create {template}: {made}";
        }
        var longer = ArtifactTools.Create(new JObject { ["template"] = "dash", ["inputs"] = new JObject { ["length_m"] = 3 }, ["open"] = false });
        if ((string)longer["change"] != "updated") return "a 3 m dash wasn't a new version: " + longer;
        try { ArtifactTools.Create(new JObject { ["template"] = "dash", ["inputs"] = new JObject { ["length_m"] = 40 }, ["open"] = false }); return "a 40 m dash wasn't refused"; }
        catch (ArgumentException e) when (e.Message.StartsWith("bad_value")) { }

        var desk = ArtifactTools.Create(new JObject { ["template"] = "park", ["surface"] = "desk", ["open"] = false });
        if ((string)desk["id"] != "park_desk") return "park on the desk: " + desk;
        var slalom = ArtifactTools.Create(new JObject { ["template"] = "slalom", ["surface"] = "desk", ["open"] = false });
        if ((string)slalom["id"] != "slalom_desk") return "slalom on the desk: " + slalom;

        var own = ArtifactTools.Create(new JObject
        {
            ["course"] = new JObject
            {
                ["title"] = "Kitchen run", ["thing"] = "truck",
                ["elements"] = new JArray(new JObject { ["type"] = "straight", ["length_m"] = 2 }, new JObject { ["type"] = "cones", ["count"] = 4 }, new JObject { ["type"] = "bay" }),
            },
            ["open"] = false,
        });
        if ((string)own["id"] != "kitchen_run" || own["course"] == null) return "a course from a description: " + own;

        ArtifactTools.ResetAttempts();
        var bad = ArtifactTools.Create(new JObject
        {
            ["course"] = new JObject { ["thing"] = "truck", ["elements"] = new JArray(new JObject { ["type"] = "hoops" }) },
            ["open"] = false,
        });
        if ((string)bad["status"] != "invalid" || !(bad["errors"] is JArray errors) || errors.Count == 0 || errors[0]["hint"] == null) return "a bad course: " + bad;
        if (!((string)bad["next"]).Contains("course")) return "the refusal doesn't say to resend the course";
        ArtifactTools.ResetAttempts();

        var json = Resources.Load<TextAsset>("AlbieTools/artifacts");
        var schema = json == null ? null : JArray.Parse(json.text).SelectToken("[?(@.name == 'artifact_create')].parameters.properties") as JObject;
        if (schema == null || schema["course"] == null || schema["surface"] == null) return "artifact_create's schema has no course or surface";
        var types = schema["course"]["properties"]["elements"]["items"]["properties"]["type"]["enum"] as JArray;
        if (types == null || types.Count != RoomCourse.ElementTypes.Length) return "the schema's element types don't match the generator's";
        foreach (var t in types) if (Array.IndexOf(RoomCourse.ElementTypes, (string)t) < 0) return "the schema lists an element the generator doesn't have: " + t;
        note = $"dash, park, hover, landing; park and slalom on the desk; \"{own["course"]}\"";
        return null;
    }
}
