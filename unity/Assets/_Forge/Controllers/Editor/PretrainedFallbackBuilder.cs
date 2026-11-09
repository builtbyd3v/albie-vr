using System.IO;
using Forge.Controllers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the Autopilot's pretrained fallback (PROJECT_CONTEXT 16.3, Nov 12: "Pretrained fallback ready"): the
/// controller the Autopilot uses when every candidate trained live fails, so the demo's brain beat always lands.
///
/// It has to drive a room slalom it has never seen, so it learns from many rooms (16.8, "room slalom, part 2"):
/// - laps: 4 scripted laps of the corridor, plus 2 of each of <see cref="Rooms"/> random rooms
///   (<see cref="RoomCourses.Random"/>, seeds from 1000), all through the real manual controller and recorder;
/// - <see cref="Seeds"/> candidates, 16×16 (one room's 8×8 is too small for many rooms);
/// - it keeps the one that drives every corridor start (the physics check's slalom is the corridor) and the most starts
///   on the 6 mock rooms and <see cref="SelectionRooms"/> other random rooms (seeds from 3000). RoomSlalomCheck tests it
///   on rooms from seed 5000, which nothing here has seen.
/// Deterministic: rebuilding gives the same weights (only the timing fields differ). About 5 minutes.
///
/// It's trained on the fake truck (MockTruckSim), whose sensors match a sphere-cast ray fan on real colliders, and
/// ThingRuntimeCheck checks it drives the physics example from all 15 starts. If the video uses it, the Devpost text says
/// so (16.3). Forge > Controllers > Build Pretrained Fallback.
///
/// On the real truck, the proof is a brain that drove the real course: after laps by hand, B trains the candidates and
/// the Autopilot keeps the one that finishes. Pull its file off the headset and make it the fallback with
/// Forge > Controllers > Use Controller File As Fallback (<see cref="Promote"/>). Laps alone can't pick one:
/// validation loss doesn't say which net drives (PROJECT_CONTEXT 16.8), and there's no copy of the real course to test on.
/// </summary>
public static class PretrainedFallbackBuilder
{
    public const string ResourcePath = "Controllers/truck_autopilot_fallback";
    const string AssetPath = "Assets/_Forge/Controllers/Resources/Controllers/truck_autopilot_fallback.json";
    public const int CorridorLaps = 4, Rooms = 32, SelectionRooms = 20, Seeds = 6;
    static readonly int[] Hidden = { 16, 16 };

    [MenuItem("Forge/Controllers/Build Pretrained Fallback")]
    static void Menu() => Debug.Log("[Controllers] " + Build());

    public static string Build()
    {
        var laps = BehaviourCloningCheck.DriveLaps(400, CorridorLaps);
        if (laps.CompletedLaps < CorridorLaps) return $"only {laps.CompletedLaps}/{CorridorLaps} scripted corridor laps completed; not written";
        int rooms = 0;
        for (int seed = 1000; rooms < Rooms; seed++)
        {
            var (_, course) = RoomCourses.Random(seed);
            if (course == null) continue;
            var set = MockLaps.Record(seed, 1f, MockLaps.Style.Varied, out _, out _, course);
            foreach (var lap in set.Laps) if (lap.Outcome == "completed") laps.Laps.Add(lap);
            rooms++;
        }
        var selection = new System.Collections.Generic.List<MockCourse>();
        foreach (var (_, course) in RoomCourses.MockRooms()) selection.Add(course);
        for (int seed = 3000, added = 0; added < SelectionRooms; seed++)
        {
            var (_, course) = RoomCourses.Random(seed);
            if (course == null) continue;
            selection.Add(course);
            added++;
        }
        int roomStarts = selection.Count * MockTrial.RoomStarts;

        BehaviourCloner best = null;
        int bestCorridor = -1, bestRooms = -1;
        var scores = new System.Text.StringBuilder();
        for (int seed = 1; seed <= Seeds; seed++)
        {
            var cloner = new BehaviourCloner(laps, new BehaviourCloner.Options { Seed = seed, Hidden = Hidden });
            cloner.TrainToEnd();
            int corridor = MockTrial.Finishes(cloner);
            int inRooms = 0;
            foreach (var course in selection) inRooms += MockTrial.Finishes(cloner, course);
            scores.Append(seed == 1 ? "" : ", ").Append($"seed {seed}: corridor {corridor}/{MockTrial.Starts}, rooms {inRooms}/{roomStarts}");
            bool better = corridor == MockTrial.Starts && (bestCorridor < MockTrial.Starts || inRooms > bestRooms);
            if (best == null || better) { best = cloner; bestCorridor = corridor; bestRooms = inRooms; }
        }
        if (bestCorridor < MockTrial.Starts) return $"no candidate drove every corridor start ({scores}); not written";

        var file = JObject.Parse(best.ToControllerJson("truck_autopilot_fallback", 1, 1.5f));
        file["training"]["sim_check"] = $"{bestCorridor}/{MockTrial.Starts} corridor starts, {bestRooms}/{roomStarts} starts in {selection.Count} rooms ({scores})";
        file["training"]["rooms"] = rooms;
        file["notes"] = "Pretrained fallback for the Autopilot: used only when every candidate trained live fails. " +
                        $"Trained on the fake truck (MockTruckSim): 4 scripted corridor laps and 2 laps in each of {rooms} random room slaloms, " +
                        "with course.bearing toward the next gate. Once the real truck has driven, replace it with a candidate that drove the real course " +
                        "(Forge > Controllers > Use Controller File As Fallback). If the video uses it, the Devpost text says so.";
        Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
        File.WriteAllText(AssetPath, file.ToString(Formatting.Indented) + "\n");
        AssetDatabase.Refresh();
        return $"wrote {AssetPath}: seed {best.Settings.Seed}, {bestCorridor}/{MockTrial.Starts} corridor starts, {bestRooms}/{roomStarts} room starts, val loss {best.ValidationLoss:0.0000} ({scores})";
    }

    [MenuItem("Forge/Controllers/Use Controller File As Fallback...")]
    static void PromoteMenu()
    {
        string path = EditorUtility.OpenFilePanel("A learned controller that drove the real course", "", "json");
        if (string.IsNullOrEmpty(path)) return;
        string report = Promote(path);
        if (report.StartsWith("wrote")) Debug.Log("[Controllers] " + report); else Debug.LogError("[Controllers] " + report);
    }

    /// <summary>
    /// Makes <paramref name="source"/> the pretrained fallback: a learned controller file that drove the real course,
    /// such as the candidate the Autopilot kept on the headset (<c>persistentDataPath/controllers/truck_autopilot.json</c>
    /// or <c>candidates/truck_autopilot_2.json</c>; <c>adb pull</c> it). It must load as a learned controller; it's renamed
    /// <c>truck_autopilot_fallback</c> and says where it came from. Returns "wrote …" or why not. <paramref name="assetPath"/>
    /// is for the checks.
    /// </summary>
    public static string Promote(string source, string assetPath = AssetPath)
    {
        if (!File.Exists(source)) return $"no file at {source}";
        JObject json;
        try { json = JObject.Parse(File.ReadAllText(source)); }
        catch (JsonException e) { return $"{Path.GetFileName(source)} isn't JSON: {e.Message}"; }
        if ((string)json["kind"] != "learned") return $"{Path.GetFileName(source)} is a {(string)json["kind"] ?? "?"} controller; the fallback must be a learned one";
        string from = (string)json["name"];
        json["name"] = "truck_autopilot_fallback";
        json["version"] = 1;
        var file = ControllerFile.Parse(json.ToString(), out string error);
        if (file == null) return $"{Path.GetFileName(source)} doesn't load: {error}";
        var training = json["training"] as JObject ?? new JObject();
        training.Remove("sim_check");
        training["promoted_from"] = $"{Path.GetFileName(source)} ({from}), {System.DateTime.Now:yyyy-MM-dd}";
        json["training"] = training;
        json["notes"] = "Pretrained fallback for the Autopilot: used only when every candidate trained live fails. " +
                        $"Promoted from {Path.GetFileName(source)}, a candidate that drove the real course. If the video uses it, the Devpost text says so.";
        Directory.CreateDirectory(Path.GetDirectoryName(assetPath));
        File.WriteAllText(assetPath, json.ToString(Formatting.Indented) + "\n");
        AssetDatabase.Refresh();
        return $"wrote {assetPath} from {Path.GetFileName(source)} ({from}): {file.Kind}, inputs {string.Join(" ", InputNames(json))}";
    }

    static string[] InputNames(JObject json)
    {
        var inputs = json["inputs"] as JArray;
        var names = new string[inputs?.Count ?? 0];
        for (int i = 0; i < names.Length; i++) names[i] = (string)inputs[i]["channel"];
        return names;
    }
}
