using System.IO;
using Forge.Controllers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the Autopilot's pretrained fallback (PROJECT_CONTEXT 16.3, Nov 12: "Pretrained fallback ready"): the
/// controller the Autopilot uses when every candidate trained live fails, so the demo's brain beat always lands.
/// Drives 4 scripted laps through the real manual controller and recorder, trains 5 candidates, scores each on the
/// mock slalom, and keeps the best. Deterministic: rebuilding gives the same weights (only the timing fields differ).
///
/// Today it's trained on the fake truck (MockTruckSim). When the Thing runtime (C-16) has the real truck, rebuild it
/// there, with the same sensors and laps on the real floor. If the video uses it, the Devpost text says so (16.3).
/// Forge > Controllers > Build Pretrained Fallback.
/// </summary>
public static class PretrainedFallbackBuilder
{
    public const string ResourcePath = "Controllers/truck_autopilot_fallback";
    const string AssetPath = "Assets/_Forge/Controllers/Resources/Controllers/truck_autopilot_fallback.json";
    const int Laps = 4, Seeds = 5;

    [MenuItem("Forge/Controllers/Build Pretrained Fallback")]
    static void Menu() => Debug.Log("[Controllers] " + Build());

    public static string Build()
    {
        var laps = BehaviourCloningCheck.DriveLaps(400, Laps);
        if (laps.CompletedLaps < Laps) return $"only {laps.CompletedLaps}/{Laps} scripted laps completed; not written";
        BehaviourCloner best = null;
        int bestScore = -1;
        var scores = new System.Text.StringBuilder();
        for (int seed = 1; seed <= Seeds; seed++)
        {
            var cloner = new BehaviourCloner(laps, new BehaviourCloner.Options { Seed = seed });
            cloner.TrainToEnd();
            int score = MockTrial.Finishes(cloner);
            scores.Append(seed == 1 ? "" : ", ").Append($"seed {seed}: {score}/{MockTrial.Starts}");
            if (score > bestScore) { best = cloner; bestScore = score; }
        }
        if (bestScore < MockTrial.Starts) return $"no candidate finished every start ({scores}); not written";

        var file = JObject.Parse(best.ToControllerJson("truck_autopilot_fallback", 1, 1.5f));
        file["training"]["sim_check"] = $"{bestScore}/{MockTrial.Starts} mock slalom starts ({scores})";
        file["notes"] = "Pretrained fallback for the Autopilot: used only when every candidate trained live fails. " +
                        "Trained on the fake truck (MockTruckSim) from 4 scripted laps; rebuild it on the real Thing runtime " +
                        "(Forge > Controllers > Build Pretrained Fallback). If the video uses it, the Devpost text says so.";
        Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
        File.WriteAllText(AssetPath, file.ToString(Formatting.Indented) + "\n");
        AssetDatabase.Refresh();
        return $"wrote {AssetPath}: seed {best.Settings.Seed}, {bestScore}/{MockTrial.Starts} starts, val loss {best.ValidationLoss:0.0000} ({scores})";
    }
}
