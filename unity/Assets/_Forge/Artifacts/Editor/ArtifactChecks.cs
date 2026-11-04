using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Forge.Artifacts;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Forge > Check Artifacts: the C-12 rules against the fixtures, plus one deliberate mistake per error code. Tests live
/// here because test assemblies can't see Assembly-CSharp. In batch mode, exits non-zero if anything failed.
/// </summary>
public static class ArtifactChecks
{
    static int passed;
    static readonly List<string> failures = new List<string>();

    static void Check(bool ok, string what)
    {
        if (ok) passed++;
        else failures.Add(what);
    }

    static void Expect(string code, Action<JObject, JObject> mistake, string fixture = "slalom")
    {
        var p = ArtifactPackage.Load(fixture, out _);
        mistake(p.Manifest, p.Scene);
        var errors = ArtifactValidator.Validate(p.Manifest, p.Scene);
        Check(errors.Any(e => e.code == code), $"expected {code}, got [{string.Join("; ", errors)}]");
    }

    static JObject Node(JObject scene, string id) => (JObject)((JArray)scene["nodes"]).First(n => n.Value<string>("id") == id);

    [MenuItem("Forge/Check Artifacts")]
    public static void Run()
    {
        passed = 0;
        failures.Clear();
        Fixtures();
        Mistakes();
        Refusals();
        Inputs();
        Approvals();
        Looks();
        foreach (var f in failures) Debug.LogError("[ArtifactChecks] FAIL " + f);
        Debug.Log($"[ArtifactChecks] {passed} passed, {failures.Count} failed");
    }

    /// <summary>For -executeMethod.</summary>
    public static void RunBatch()
    {
        Run();
        EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
    }

    static void Fixtures()
    {
        foreach (var id in new[] { "slalom", "repo_city", "stance" })
        {
            var p = ArtifactPackage.Load(id, out string error);
            Check(p != null, $"{id} loads: {error}");
            if (p == null) continue;
            var errors = ArtifactValidator.Validate(p.Manifest, p.Scene);
            Check(errors.Count == 0, $"{id} is valid: {string.Join("; ", errors)}");
        }
        var broken = ArtifactPackage.Load("broken", out _);
        Check(ArtifactValidator.Validate(broken.Manifest, broken.Scene).Any(e => e.code == "unknown_kind"), "broken is refused");
        Check(ArtifactNodes.Missing(ArtifactPackage.Load("stance", out _).Scene).Count == 0, "stance can be built today");
        var missing = ArtifactNodes.Missing(ArtifactPackage.Load("slalom", out _).Scene);
        Check(missing.Contains("zone") && missing.Contains("generators") && missing.Contains("runs"), "slalom reports what isn't built");
    }

    // One per code in C-12 section 9. hash_mismatch waits for gltf loading, which is when assets are read.
    static void Mistakes()
    {
        Expect("newer_schema", (m, s) => s["schema"] = "albie.scene/2");
        Expect("newer_schema", (m, s) => m["runtime"]["min_runtime"] = 2);
        Expect("unknown_kind", (m, s) => Node(s, "cones")["kind"] = "cube");
        Expect("unsupported_kind", (m, s) => Node(s, "guide")["kind"] = "camera_rig");
        Expect("unknown_ref", (m, s) => s["rules"][1]["zones"] = "gate");
        Expect("unknown_ref", (m, s) => Node(s, "time_label")["parent"] = "nowhere");
        Expect("unknown_slot", (m, s) => Node(s, "cones")["source"] = "course.pins");
        Expect("unknown_slot", (m, s) => s["run"]["finish"]["needs"][0]["min"] = "@course.pins");
        Expect("unknown_input", (m, s) => s["generators"][0]["params"]["gates"] = "$gate_count");
        Expect("output_mismatch", (m, s) => ((JArray)s["rules"]).RemoveAt(2));
        Expect("asset_not_listed", (m, s) => { Node(s, "guide")["kind"] = "gltf"; Node(s, "guide")["asset"] = "assets/cone.glb"; });
        Expect("permission_denied", (m, s) => m["permissions"]["network"] = new JArray("example.com"));
        Expect("permission_denied", (m, s) => m["permissions"]["camera"] = "read");
        Expect("permission_denied", (m, s) => m["permissions"]["connectors"] = new JArray("source_control:write"));
        Expect("permission_missing", (m, s) => m["permissions"]["room"] = "none");
        Expect("permission_missing", (m, s) => m["permissions"]["things"] = "none");
        Expect("script_not_allowed", (m, s) => m["script"] = "main.js");
        Expect("too_big", (m, s) =>
        {
            var nodes = (JArray)s["nodes"];
            for (int i = 0; i < ArtifactSchema.MaxNodes + 1; i++) nodes.Add(new JObject { ["id"] = "n" + i, ["kind"] = "primitive", ["shape"] = "box" });
        });
        Expect("bad_value", (m, s) => m["inputs"][0]["default"] = 20);
        Expect("bad_value", (m, s) => Node(s, "time_label")["id"] = "cones");
        Expect("bad_value", (m, s) => { Node(s, "start")["parent"] = "finish"; Node(s, "finish")["parent"] = "start"; });
        Expect("bad_value", (m, s) => m["version"] = "1.2.0");
        Expect("bad_value", (m, s) => m["links"]["thing"] = "truck", "stance");
    }

    // Wrong shapes and hostile ids: the host answers, it doesn't throw.
    static void Refusals()
    {
        foreach (var id in new[] { "../../secrets", "a/b", "", null, "UPPER" })
        {
            Check(ArtifactPackage.Load(id, out string error) == null && error != null, $"id \"{id}\" is refused");
        }
        Check(ArtifactPackage.Load("nothing_here", out string none) == null && none.Contains("nothing_here"), "a missing artifact says so");
        foreach (var bad in new[] { "{\"schema\":\"albie.artifact/1\",\"inputs\":[1,2]}", "{\"schema\":\"albie.artifact/1\",\"scenarios\":\"x\",\"data\":[3]}" })
        {
            var errors = ArtifactValidator.Validate(JObject.Parse(bad), new JObject { ["schema"] = "albie.scene/1", ["nodes"] = new JArray(1, "x") });
            Check(errors.Count > 0, "a wrong shape comes back as data: " + bad);
        }
        Check(ArtifactValidator.Validate(null, null).Count > 0, "null files come back as data");
    }

    static void Inputs()
    {
        var p = ArtifactPackage.Load("stance", out _);
        var inputs = new ArtifactInputs(p.Manifest);
        int changes = 0;
        inputs.Changed += _ => changes++;
        double start = inputs.Value("track_m");
        Check(!inputs.TrySet("track_m", 99) && inputs.Value("track_m") == start, "out of range is refused, not clamped");
        Check(!inputs.TrySet("nope", 1), "an unknown input is refused");
        Check(changes == 0, "a refused value raises nothing");
        Check(inputs.TrySet("track_m", 0.5) && changes == 1, "a good value raises Changed");
        Check(inputs.TrySet("track_m", 0.5) && changes == 1, "the same value raises nothing");
        Check(Mathf.Approximately(inputs.Number("$track_m", -1f), 0.5f), "$name reads the live value");
        Check(inputs.Vector(new JArray("$track_m", 2, 3), Vector3.zero) == new Vector3(0.5f, 2f, 3f), "a vector can mix $name and numbers");
        Check(ArtifactInputs.IsBound(new JArray(1, "$track_m", 3)) && !ArtifactInputs.IsBound(new JArray(1, 2, 3)), "IsBound finds a $name in a list");
        var over = new ArtifactInputs(p.Manifest, new JObject { ["track_m"] = 0.3 });
        Check(Mathf.Approximately((float)over.Value("track_m"), 0.3f), "scenario params replace defaults");
    }

    static void Approvals()
    {
        string file = Path.Combine(Path.GetTempPath(), "artifact-approvals-" + Guid.NewGuid().ToString("N") + ".json");
        ArtifactApprovals.FileOverride = file;
        try
        {
            var stance = ArtifactPackage.Load("stance", out _).Manifest;
            var slalom = ArtifactPackage.Load("slalom", out _).Manifest;
            Check(!ArtifactApprovals.NeedsApproval(stance), "asking for nothing needs no sheet");
            Check(ArtifactApprovals.Lines(slalom).Count == 2, "slalom asks for room and things");
            Check(ArtifactApprovals.NeedsApproval(slalom), "first open asks");
            ArtifactApprovals.Approve(slalom);
            Check(!ArtifactApprovals.NeedsApproval(slalom), "second open doesn't");

            var same = (JObject)slalom.DeepClone();
            var reordered = new JObject();
            foreach (var p in ((JObject)same["permissions"]).Properties().Reverse()) reordered[p.Name] = p.Value;
            same["permissions"] = reordered;
            Check(!ArtifactApprovals.NeedsApproval(same), "reordered keys don't ask again");

            var more = (JObject)slalom.DeepClone();
            more["permissions"]["connectors"] = new JArray("source_control:read");
            Check(ArtifactApprovals.NeedsApproval(more), "a changed list asks again");

            File.WriteAllText(file, "{ not json");
            Check(ArtifactApprovals.NeedsApproval(slalom), "a damaged store asks again");
            File.Delete(file);
            ArtifactApprovals.Approve(slalom);
            ArtifactApprovals.Forget(slalom.Value<string>("id"));
            Check(ArtifactApprovals.NeedsApproval(slalom), "forgetting asks again");
        }
        finally
        {
            ArtifactApprovals.FileOverride = null;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    static void Looks()
    {
        var size = new Vector3(0.2f, 0.4f, 0.6f);
        Check(ArtifactLook.ShapeScale("box", size) == size, "a box is its size");
        Check(ArtifactLook.ShapeScale("capsule", size).y == 0.2f, "a 2 m capsule is halved");
        Check(ArtifactLook.ShapeScale("cylinder", size) == size, "our cylinder is its size");
        Check(ArtifactLook.ShapeScale("plane", size) == new Vector3(0.02f, 1f, 0.06f), "a 10 m plane is scaled down");
        Check(ArtifactLook.WithoutMetrics("{completion_time_s} s") == ArtifactLook.NoValue + " s", "an empty metric never shows a number");
        Check(ArtifactLook.Color("#FF0000", Color.white) == Color.red && ArtifactLook.Color("nope", Color.white) == Color.white, "colours parse, with a fallback");
    }
}
