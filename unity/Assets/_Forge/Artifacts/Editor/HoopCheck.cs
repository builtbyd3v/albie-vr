using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Forge.Artifacts;
using Forge.Notebook;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Headless checks for "Make me a hoop course" (PROJECT_CONTEXT 16.3, Person 5, Nov 14): the room_hoops generator, its
/// fit rules in small, unlabelled and low rooms (C-12 3.3), the template, and a scored drone lap through the template's
/// own geometry. Forge > Artifacts > Check Hoops.
/// </summary>
public static class HoopCheck
{
    static string root;
    const float DroneRadius = 0.12f;

    [MenuItem("Forge/Artifacts/Check Hoops")]
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
        root = Path.Combine(Application.temporaryCachePath, "hoop_check");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string before = ArtifactLibrary.RootOverride;
        string notebookProject = NotebookStore.ProjectId, notebookRoot = NotebookStore.Root;
        ArtifactLibrary.RootOverride = root;
        NotebookStore.Close();
        var sb = new StringBuilder();
        bool ok = true;
        try
        {
            ok &= Check(sb, "living room: the full course", LivingRoom);
            ok &= Check(sb, "small room: shrinks and says so", SmallRoom);
            ok &= Check(sb, "unlabelled room, no ceiling: placed, and says it couldn't check", Unlabelled);
            ok &= Check(sb, "low ceiling: no_headroom, with the heights", Attic);
            ok &= Check(sb, "closet: no_free_floor, with the size it needs", Closet);
            ok &= Check(sb, "never scanned: no_room_data", Unscanned);
            ok &= Check(sb, "turned room: still on free floor", Turned);
            ok &= Check(sb, "slots: weaving hoops, start, finish, all on the floor", Slots);
            ok &= Check(sb, "template: validates, builds, needs host 2", Template);
            ok &= Check(sb, "layout saved as hoops, reused, refused under a low ceiling", Layouts);
            ok &= Check(sb, "the tools make it by name", Tools);
            ok &= Check(sb, "a lap through every hoop completes, frames not hit", Completes);
            ok &= Check(sb, "skipping a hoop fails as missed_hoop", MissedHoop);
            ok &= Check(sb, "clipping a post or a bar counts as a hit", Hits);
            ok &= Check(sb, "run ids don't clash across courses; the notebook keeps both", RunIds);
        }
        finally
        {
            ArtifactLibrary.RootOverride = before;
            NotebookStore.Close();
            if (notebookProject != null) NotebookStore.Open(notebookProject, notebookRoot);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return (ok ? "PASS" : "FAIL") + " hoop check\n" + sb;
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

    // Independent of the generator's grid: walks the footprint every 5 cm and asks the room directly.
    static string OnFreeFloor(IFloorPlan room, FloorSlalom.Result fit, RoomHoops.Settings s)
    {
        var r = Quaternion.Euler(0f, fit.Yaw, 0f);
        float lane = RoomHoops.Lane(s), end = fit.Spacing * (fit.Gates + 1) + FloorSlalom.EndMargin;
        float clearance = s.Clearance - FloorSlalom.Cell * 0.75f;
        for (float z = -FloorSlalom.EndMargin; z <= end; z += 0.05f)
            for (float x = -lane; x <= lane; x += 0.05f)
                if (!room.IsFree(fit.Origin + r * new Vector3(x, 0f, z), clearance)) return $"footprint point ({F(x)}, {F(z)}) isn't free floor";
        // Both posts of every hoop stand on free floor.
        float post = s.Diameter * 0.5f + RoomHoops.Post * 0.5f;
        foreach (var hoop in fit.Slots["course.hoops"])
            for (int side = -1; side <= 1; side += 2)
                if (!room.IsFree(fit.Origin + r * (hoop.position + new Vector3(side * post, 0f, 0f)), clearance)) return "a post is off free floor";
        return null;
    }

    static string LivingRoom(out string note)
    {
        note = null;
        var room = MockFloorPlan.LivingRoom();
        var s = RoomHoops.Settings.Default;
        var fit = RoomHoops.Fit(room, s);
        if (!fit.Ok) return fit.Code + ": " + fit.Message;
        if (fit.Gates != 4 || Mathf.Abs(fit.Spacing - 1f) > 0.001f || fit.Shrunk || fit.Message != null) return $"{fit.Gates} hoops at {F(fit.Spacing)}: {fit.Message}";
        note = $"4 hoops at 1.0 m, frames {F(RoomHoops.Top(s))} m tall under a 2.5 m ceiling; {fit.Tried} placements tried";
        return OnFreeFloor(room, fit, s);
    }

    static string SmallRoom(out string note)
    {
        note = null;
        var room = MockFloorPlan.SmallRoom();
        var s = RoomHoops.Settings.Default;
        s.Hoops = 6;
        var fit = RoomHoops.Fit(room, s);
        if (!fit.Ok) return fit.Code + ": " + fit.Message;
        if (!fit.Shrunk || fit.Gates < 3 || fit.Message == null || !fit.Message.Contains("tight") || !fit.Message.Contains("hoops")) return $"{fit.Gates} at {F(fit.Spacing)}: {fit.Message}";
        if (fit.Message.Contains("couch")) return "mentions a couch it never asked for: " + fit.Message;
        note = $"asked for 6: \"{fit.Message}\"";
        return OnFreeFloor(room, fit, s);
    }

    static string Unlabelled(out string note)
    {
        note = null;
        var room = MockFloorPlan.Unlabelled();
        var s = RoomHoops.Settings.Default;
        var fit = RoomHoops.Fit(room, s);
        if (!fit.Ok) return fit.Code + ": " + fit.Message;
        if (fit.Message == null || !fit.Message.Contains("can't find the ceiling") || !fit.Message.Contains("1.37 m")) return "message: " + fit.Message;
        note = $"{fit.Gates} hoops at {F(fit.Spacing)}: \"{fit.Message}\"";
        return OnFreeFloor(room, fit, s);
    }

    static string Attic(out string note)
    {
        note = null;
        var fit = RoomHoops.Fit(MockFloorPlan.Attic(), RoomHoops.Settings.Default);
        if (fit.Ok || fit.Code != "no_headroom" || !fit.Message.Contains("only 1.40 m") || !fit.Message.Contains("need 1.52 m") || !fit.Message.Contains("stand 1.37 m") || !fit.Message.Contains("15 cm")) return fit.Code + ": " + fit.Message;
        var slalom = FloorSlalom.Fit(MockFloorPlan.Attic(), FloorSlalom.Settings.Default);
        if (!slalom.Ok) return "a slalom should still fit in the attic: " + slalom.Code;
        note = $"\"{fit.Message}\" (a slalom still fits there)";
        return null;
    }

    static string Closet(out string note)
    {
        note = null;
        var fit = RoomHoops.Fit(MockFloorPlan.Closet(), RoomHoops.Settings.Default);
        if (fit.Ok || fit.Code != "no_free_floor" || !fit.Message.Contains("hoop course") || !fit.Message.Contains("3 hoops")) return fit.Code + ": " + fit.Message;
        note = $"\"{fit.Message}\"";
        return null;
    }

    static string Unscanned(out string note)
    {
        note = null;
        var fit = RoomHoops.Fit(MockFloorPlan.Unscanned(), RoomHoops.Settings.Default);
        var none = RoomHoops.Fit(null, RoomHoops.Settings.Default);
        if (fit.Ok || fit.Code != "no_room_data" || none.Code != "no_room_data") return $"{fit.Code} / {none.Code}";
        return null;
    }

    static string Turned(out string note)
    {
        note = null;
        var room = MockFloorPlan.TurnedLivingRoom();
        var s = RoomHoops.Settings.Default;
        var fit = RoomHoops.Fit(room, s);
        if (!fit.Ok || fit.Shrunk) return $"ok {fit.Ok}, shrunk {fit.Shrunk}: {fit.Message}";
        note = $"course at {fit.Yaw:0}° in a room turned 30°";
        return OnFreeFloor(room, fit, s);
    }

    static string Slots(out string note)
    {
        note = null;
        var slots = RoomHoops.Slots("course", 4, 1f, 0.7f);
        var hoops = slots["course.hoops"];
        if (hoops.Count != 4 || slots["course.start"].Count != 1 || slots["course.finish"].Count != 1) return "counts";
        if (Mathf.Abs(slots["course.finish"][0].position.z - 5f) > 0.001f) return "finish at " + slots["course.finish"][0].position.z;
        float weave = RoomHoops.Weave(0.7f);
        for (int i = 0; i < 4; i++)
        {
            var p = hoops[i].position;
            if (Mathf.Abs(p.x - (i % 2 == 0 ? weave : -weave)) > 0.001f || Mathf.Abs(p.z - (i + 1)) > 0.001f) return $"hoop {i} at {p}";
        }
        foreach (var pair in slots) foreach (var pose in pair.Value) if (pose.position.y != 0f) return pair.Key + " isn't on the floor";
        note = $"weave ±{F(weave)} m";
        return null;
    }

    static string Template(out string note)
    {
        note = null;
        if (!ArtifactLibrary.TryTemplate("hoop_course", out var manifest, out var scene)) return "no hoop_course template";
        var errors = ArtifactValidator.Validate(manifest, scene);
        if (errors.Count > 0) return "validator: " + errors[0];
        var missing = ArtifactNodes.Missing(scene);
        if (missing.Count > 0) return "host can't build " + string.Join(", ", missing);
        if ((int)manifest["runtime"]["min_runtime"] != 2 || ArtifactSchema.HostRuntime < 2) return "min_runtime " + manifest["runtime"]["min_runtime"];
        var old = (JObject)manifest.DeepClone();
        old["runtime"]["min_runtime"] = ArtifactSchema.HostRuntime + 1;
        if (!ArtifactValidator.Validate(old, scene).Exists(e => e.code == "newer_schema")) return "a newer runtime wasn't refused";
        // The scene's frame matches the generator: posts at the hoop's edge, the top where RoomHoops says it is.
        var s = RoomHoops.Settings.Default;
        var gen = (JObject)scene["generators"][0]["params"];
        if (Mathf.Abs((float)gen["diameter_m"] - s.Diameter) > 0.001f || Mathf.Abs((float)gen["height_m"] - s.Height) > 0.001f) return "generator params differ from RoomHoops defaults";
        var top = NodeBox(scene, "hoop_top", Vector3.zero);
        if (Mathf.Abs(top.max.y - RoomHoops.Top(s)) > 0.005f) return $"frame top {F(top.max.y)}, generator thinks {F(RoomHoops.Top(s))}";
        var left = NodeBox(scene, "hoop_left", Vector3.zero);
        if (left.min.y > 0.001f) return "the posts don't stand on the floor";
        // A bad hit_count target is refused, a good list is fine.
        var bad = (JObject)scene.DeepClone();
        bad["rules"][2]["nodes"] = new JArray("hoop_left", "nope");
        if (!ArtifactValidator.Validate(manifest, bad).Exists(e => e.code == "unknown_ref")) return "hit_count on a missing node wasn't refused";
        note = "validator and host agree; frame top " + F(top.max.y) + " m";
        return null;
    }

    static string Layouts(out string note)
    {
        note = null;
        ArtifactLibrary.TryTemplate("hoop_course", out var manifest, out var scene);
        var generator = (JObject)scene["generators"][0];
        var scenario = (JObject)manifest["scenarios"][0];
        var inputs = new ArtifactInputs(manifest);
        var room = MockFloorPlan.LivingRoom();
        var first = ArtifactGenerators.Place(manifest, generator, inputs, scenario, room, false);
        if (!first.Ok || first.Reused) return "first: " + first.Code + " " + first.Message;
        var placed = first.Layout["fit"]["placed"];
        if ((int?)placed["hoops"] != 4 || placed["gates"] != null) return "placed " + placed;
        if (!first.Slots.ContainsKey("course.hoops")) return "no course.hoops slot";
        var again = ArtifactGenerators.Place(manifest, generator, inputs, scenario, room, false);
        if (!again.Ok || !again.Reused || (string)again.Layout["layout_id"] != (string)first.Layout["layout_id"]) return "a rerun didn't reuse the layout";
        inputs.TrySet("hoops", 5);
        var five = ArtifactGenerators.Place(manifest, generator, inputs, scenario, room, false);
        if (five.Reused || (int)five.Layout["fit"]["placed"]["hoops"] != 5) return "5 hoops reused the 4-hoop layout";
        var attic = ArtifactGenerators.Place(manifest, generator, inputs, scenario, MockFloorPlan.Attic(), false);
        if (attic.Ok || attic.Code != "no_headroom") return "attic: " + attic.Code;
        note = $"{first.Layout["layout_id"]} reused; 5 hoops made {five.Layout["layout_id"]}; attic refused";
        return null;
    }

    static string Tools(out string note)
    {
        note = null;
        var list = ArtifactTools.List(new JObject());
        bool listed = false;
        foreach (var t in (JArray)list["templates"]) if ((string)t["template"] == "hoop_course") listed = true;
        if (!listed) return "artifact_list doesn't offer hoop_course";
        var made = ArtifactTools.Create(new JObject { ["template"] = "hoop_course", ["inputs"] = new JObject { ["hoops"] = 6 }, ["open"] = false });
        if ((string)made["id"] != "hoop_course" || made["error"] != null) return "create: " + made;
        try { ArtifactLibrary.FromTemplate("hoop_course", new JObject { ["hoops"] = 20 }); return "20 hoops wasn't refused"; }
        catch (ArgumentException e) when (e.Message.StartsWith("bad_value")) { }
        var json = Resources.Load<TextAsset>("AlbieTools/artifacts");
        var templates = json == null ? null : JArray.Parse(json.text).SelectToken("[?(@.name == 'artifact_create')].parameters.properties.template.enum") as JArray;
        if (templates == null || !templates.Any(t => (string)t == "hoop_course")) return "artifact_create's enum doesn't list hoop_course";
        note = $"made {made["id"]} v{made["version"]} ({made["change"]})";
        return null;
    }

    // ---- Runs, with the template's own zone and frame boxes on a 4-hoop course ---------------------------------

    static JObject runScene, runManifest;
    static Bounds start, finish;
    static Bounds[] hoopZones;
    static Dictionary<string, Bounds[]> frames;

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

    static void Setup()
    {
        if (runScene != null) return;
        ArtifactLibrary.TryTemplate("hoop_course", out runManifest, out runScene);
        var slots = RoomHoops.Slots("course", 4, 1f, 0.7f);
        start = NodeBox(runScene, "start", slots["course.start"][0].position);
        finish = NodeBox(runScene, "finish", slots["course.finish"][0].position);
        hoopZones = new Bounds[4];
        frames = new Dictionary<string, Bounds[]>();
        foreach (string part in new[] { "hoop_left", "hoop_right", "hoop_top", "hoop_bottom" }) frames[part] = new Bounds[4];
        for (int i = 0; i < 4; i++)
        {
            var at = slots["course.hoops"][i].position;
            hoopZones[i] = NodeBox(runScene, "hoops", at);
            foreach (var part in frames) part.Value[i] = NodeBox(runScene, part.Key, at);
        }
    }

    static (ArtifactRun run, Drone drone) Fresh()
    {
        Setup();
        var drone = new Drone();
        var run = new ArtifactRun();
        var zones = new Dictionary<string, Bounds[]> { ["start"] = new[] { start }, ["finish"] = new[] { finish }, ["hoops"] = hoopZones };
        var slots = new Dictionary<string, int> { ["course.hoops"] = 4, ["course.start"] = 1, ["course.finish"] = 1 };
        run.Bind(runManifest, runScene, new JObject { ["layout_id"] = "lay_0001" }, new ArtifactInputs(runManifest), drone, new ArtifactMetrics(), zones, frames, slots);
        return (run, drone);
    }

    // Takes off in the start zone, flies through the middle of each hoop (stepping 5 cm so the frames get a chance to
    // register), and lands in the finish.
    static void Fly(ArtifactRun run, Drone drone, int skip, Vector3 offset)
    {
        run.Arm();
        drone.Position = start.center;
        run.Tick(0.02f);
        var at = start.center;
        for (int i = 0; i <= hoopZones.Length; i++)
        {
            Vector3 next = i < hoopZones.Length ? hoopZones[i].center + offset : finish.center;
            if (i == skip) next = new Vector3(next.x + 1.2f, next.y, next.z);
            int steps = Mathf.CeilToInt((next - at).magnitude / 0.05f);
            for (int k = 1; k <= steps; k++)
            {
                drone.Position = Vector3.Lerp(at, next, k / (float)steps);
                run.Tick(0.02f);
                if (run.State == ArtifactRunState.Finished) return;
            }
            at = next;
        }
    }

    static string Completes(out string note)
    {
        note = null;
        var (run, drone) = Fresh();
        Fly(run, drone, -1, Vector3.zero);
        if (run.Outcome != "completed") return run.Outcome + " " + run.Reason;
        var record = run.TakeRecord();
        if ((int)record["metrics"]["hoops_passed"] != 4) return "hoops " + record["metrics"]["hoops_passed"];
        if ((int)record["metrics"]["collisions"] != 0) return "a clean line through the middle hit the frame " + record["metrics"]["collisions"] + " times";
        note = $"4 hoops, 0 hits, {record["metrics"]["completion_time_s"]} s, with a {DroneRadius * 200f:0} cm drone";
        return null;
    }

    static string MissedHoop(out string note)
    {
        note = null;
        var (run, drone) = Fresh();
        Fly(run, drone, 2, Vector3.zero);
        if (run.Outcome != "failed" || run.Reason != "missed_hoop") return run.Outcome + " " + run.Reason;
        return null;
    }

    static string Hits(out string note)
    {
        note = null;
        var (run, drone) = Fresh();
        Fly(run, drone, -1, new Vector3(0.3f, 0f, 0f));  // through the edge of every hoop: the post side
        int side = (int)Math.Round(run.Board.Value("collisions"));
        var (low, lowDrone) = Fresh();
        Fly(low, lowDrone, -1, new Vector3(0f, -0.3f, 0f));  // skimming the bottom bar
        int bottom = (int)Math.Round(low.Board.Value("collisions"));
        if (side < 4 || bottom < 4) return $"edge laps hit {side} (posts) and {bottom} (bottom bars), expected at least 4 each";
        note = $"post side {side}, bottom bar {bottom}";
        return null;
    }

    // The notebook keeps bare run ids, so a slalom run and a hoop run must never share one.
    static string RunIds(out string note)
    {
        note = null;
        foreach (string a in new[] { "slalom", "hoop_course" })
        {
            string f = ArtifactRuns.Folder(a);
            if (Directory.Exists(f)) Directory.Delete(f, true);
        }
        NotebookStore.Close();
        string nb = Path.Combine(root, "nb_ids");
        NotebookStore.Open("proj_check", nb);
        var ids = new List<string>();
        foreach (string a in new[] { "slalom", "hoop_course", "slalom" })
        {
            string id = ArtifactRuns.NextId(a);
            ArtifactRuns.Save(new JObject { ["schema"] = ArtifactRuns.Schema, ["run_id"] = id, ["artifact_id"] = a, ["outcome"] = "completed" });
            NotebookStore.AttachRun(id, "completed");
            ids.Add(id);
        }
        if (ids[0] != "run_0001" || ids[1] != "run_0002" || ids[2] != "run_0003") return "ids " + string.Join(", ", ids);
        var entry = NotebookStore.Newest();
        var runs = entry?["observed"]?["runs"] as JArray;
        if (runs == null || runs.Count != 3) return "the notebook kept " + (runs?.Count ?? 0) + " of 3 runs";
        if (File.Exists(Path.Combine(ArtifactRuns.Folder("slalom"), "run_0002.json")))
            return "the hoop run landed in the slalom's folder";
        note = "slalom run_0001, hoop run_0002, slalom run_0003; all 3 on " + entry["id"];
        return null;
    }

    sealed class Drone : IArtifactThing
    {
        public string Name => "drone";
        public int Version => 1;
        public string ControllerName => "drone_manual";
        public string ControllerKind => "manual";
        public int ControllerVersion => 1;
        public Vector3 Position { get; set; }
        public float SpeedMs => 1f;
        public float Upright => 1f;
        public float HeightM => Position.y;
        public float Radius => DroneRadius;
        public bool Bound => true;
    }
}
