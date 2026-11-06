using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Forge.Artifacts;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Headless checks for the last Phase 1 node kinds (C-12 3.2): <c>gltf</c> (Nov 12) and <c>timeline</c> with
/// <c>animate</c> rules and the <c>timeline</c> action (Nov 14), on the <c>launch</c> fixture. Forge > Artifacts > Check glTF and Timelines.
/// The model load itself needs editor updates: Forge > Artifacts > Check glTF Load, in an open Editor.
/// </summary>
public static class GltfTimelineCheck
{
    const string RocketSha = "dfd052f208c5e0037eef5eb5f4ca895724d16b3fa74ac916e4e61c8f1562b435";
    static string root;

    [MenuItem("Forge/Artifacts/Check glTF and Timelines")]
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
        root = Path.Combine(Application.temporaryCachePath, "gltf_timeline_check");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string before = ArtifactLibrary.RootOverride;
        ArtifactLibrary.RootOverride = root;
        var sb = new StringBuilder();
        bool ok = true;
        try
        {
            ok &= Check(sb, "the launch fixture is valid and buildable", Fixture);
            ok &= Check(sb, "validator: asset entries, timelines, animate keys, timeline actions", Validator);
            ok &= Check(sb, "assets: only listed files, only under assets/, hash and size checked", Assets);
            ok &= Check(sb, "a timeline moves, shows, tints and scales its targets", Timeline);
            ok &= Check(sb, "play, pause, restart, loop", Clock);
        }
        finally
        {
            ArtifactLibrary.RootOverride = before;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return (ok ? "PASS" : "FAIL") + " glTF and timeline check\n" + sb;
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

    static ArtifactPackage Launch() => ArtifactPackage.Load("launch", out _);

    static JObject Node(JObject scene, string id)
    {
        foreach (JObject n in (JArray)scene["nodes"]) if ((string)n["id"] == id) return n;
        return null;
    }

    static JObject Rule(JObject scene, string target, string property)
    {
        foreach (JObject r in (JArray)scene["rules"])
            if ((string)r["target"] == target && (string)r["property"] == property) return r;
        return null;
    }

    static string Fixture(out string note)
    {
        note = null;
        var p = Launch();
        if (p == null) return "no launch fixture";
        var errors = ArtifactValidator.Validate(p.Manifest, p.Scene);
        if (errors.Count > 0) return errors[0].ToString();
        var missing = ArtifactNodes.Missing(p.Scene);
        if (missing.Count > 0) return "can't build: " + string.Join(", ", missing);
        if ((int)p.Manifest["runtime"]["min_runtime"] != ArtifactSchema.HostRuntime) return "min_runtime " + p.Manifest["runtime"]["min_runtime"];
        note = $"host runtime {ArtifactSchema.HostRuntime}; gltf and timeline both build";
        return null;
    }

    static string Validator(out string note)
    {
        note = null;
        var p = Launch();
        var cases = new List<(string code, string path, Action<JObject, JObject> edit)>
        {
            ("bad_value", "assets[0].path", (m, s) => m["assets"][0]["path"] = "../rocket.glb"),
            ("bad_value", "assets[0].path", (m, s) => m["assets"][0]["path"] = "assets/rocket.gltf"),
            ("bad_value", "assets[0].sha256", (m, s) => m["assets"][0]["sha256"] = "abc"),
            ("bad_value", "assets[0].license", (m, s) => ((JObject)m["assets"][0]).Remove("license")),
            ("bad_value", "assets[1].path", (m, s) => ((JArray)m["assets"]).Add(m["assets"][0].DeepClone())),
            ("bad_value", "nodes[3].duration_s", (m, s) => Node(s, "flight")["duration_s"] = 0),
            ("bad_value", "nodes[3].loop", (m, s) => Node(s, "flight")["loop"] = "yes"),
            ("unknown_ref", "rules[1].target", (m, s) => Rule(s, "rocket", "pos")["target"] = "booster"),
            ("unknown_ref", "rules[1].timeline", (m, s) => Rule(s, "rocket", "pos")["timeline"] = "pad"),
            ("bad_value", "rules[1].property", (m, s) => Rule(s, "rocket", "pos")["property"] = "position"),
            ("bad_value", "rules[1].keys[2]", (m, s) => Rule(s, "rocket", "pos")["keys"][2][0] = 9.0),
            ("bad_value", "rules[1].keys[2]", (m, s) => Rule(s, "rocket", "pos")["keys"][2][0] = 0.2),
            ("bad_value", "rules[1].keys[0]", (m, s) => Rule(s, "rocket", "pos")["keys"][0][1] = 0.5),
            ("bad_value", "rules[3].keys[1]", (m, s) => Rule(s, "flame", "visible")["keys"][1][1] = 1),
            ("bad_value", "rules[4].keys[0]", (m, s) => Rule(s, "flame", "color")["keys"][0][1] = "orange"),
            ("bad_value", "rules[2]", (m, s) => Rule(s, "rocket", "rot")["property"] = "pos"),
            ("bad_value", "rules[1].target", (m, s) => Rule(s, "rocket", "pos")["target"] = "info"),
            ("too_big", "rules[1].keys", (m, s) =>
            {
                var keys = new JArray();
                for (int i = 0; i <= ArtifactSchema.MaxKeys; i++) keys.Add(new JArray(i * 0.01, new JArray(0, 0, 0)));
                Rule(s, "rocket", "pos")["keys"] = keys;
            }),
            ("unknown_ref", "rules[0].do[0].timeline", (m, s) => s["rules"][0]["do"][0]["timeline"] = "countdown"),
            ("bad_value", "rules[0].do[0].action", (m, s) => s["rules"][0]["do"][0]["action"] = "rewind"),
        };
        var seen = new List<string>();
        foreach (var (code, path, edit) in cases)
        {
            var m = (JObject)p.Manifest.DeepClone();
            var s = (JObject)p.Scene.DeepClone();
            edit(m, s);
            var errors = ArtifactValidator.Validate(m, s);
            bool found = false;
            foreach (var e in errors) found |= e.code == code && e.path == path;
            if (!found) return $"expected {code} at {path}, got {(errors.Count > 0 ? string.Join("; ", errors) : "no errors")}";
            if (!seen.Contains(code)) seen.Add(code);
        }
        note = $"{cases.Count} broken copies refused ({string.Join(", ", seen)})";
        return null;
    }

    // A copy of the fixture on the device, with its own rocket file, so the bytes can be broken.
    static ArtifactPackage OnDevice(byte[] rocket, string sha)
    {
        var p = Launch();
        string folder = ArtifactLibrary.Folder("launch");
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        var manifest = (JObject)p.Manifest.DeepClone();
        manifest["assets"][0]["sha256"] = sha ?? RocketSha;
        File.WriteAllText(Path.Combine(folder, "manifest.json"), manifest.ToString());
        File.WriteAllText(Path.Combine(folder, "scene.json"), p.Scene.ToString());
        string file = Path.Combine(folder, "assets", "rocket.glb");
        if (rocket != null) File.WriteAllBytes(file, rocket);
        else if (File.Exists(file)) File.Delete(file);
        return ArtifactPackage.Load("launch", out _);
    }

    static string Assets(out string note)
    {
        note = null;
        var bundled = Launch();
        var read = ArtifactAssets.Resolve(bundled, out var error);
        if (read == null) return "bundled: " + error;
        if (!read.TryGetValue("assets/rocket.glb", out var bytes) || ArtifactAssets.Sha256(bytes) != RocketSha) return "the bundled rocket didn't read";
        if (bundled.ReadAsset("assets/../manifest.json", out _) != null || bundled.ReadAsset("data/x.json", out _) != null) return "read outside assets/";

        string Code(ArtifactPackage p)
        {
            var r = ArtifactAssets.Resolve(p, out var e);
            return r == null ? e.code : "read";
        }
        var changed = (byte[])bytes.Clone();
        changed[changed.Length - 1] ^= 0xFF;
        string tampered = Code(OnDevice(changed, null));
        string onDevice = Code(OnDevice(bytes, null));
        var junk = Encoding.ASCII.GetBytes("not a model at all");
        string notGlb = Code(OnDevice(junk, ArtifactAssets.Sha256(junk)));
        var huge = new byte[ArtifactSchema.MaxAssetBytes + 1];
        string tooBig = Code(OnDevice(huge, ArtifactAssets.Sha256(huge)));
        string fallsBack = Code(OnDevice(null, null));
        var nowhere = OnDevice(null, null);
        nowhere.Manifest["assets"][0]["path"] = "assets/nothing.glb";
        Node(nowhere.Scene, "rocket")["asset"] = "assets/nothing.glb";
        string noFile = Code(nowhere);
        Directory.Delete(ArtifactLibrary.Folder("launch"), true);

        if (tampered != "hash_mismatch") return "a changed byte: " + tampered;
        if (onDevice != "read") return "the device copy: " + onDevice;
        if (fallsBack != "read") return "no device file, so the bundled one: " + fallsBack;
        if (notGlb != "bad_value") return "not a glb: " + notGlb;
        if (tooBig != "too_big") return "9 MB: " + tooBig;
        if (noFile != "asset_missing") return "a file that's nowhere: " + noFile;
        note = $"device copy first, then bundled; refused: {tampered}, {notGlb}, {tooBig}, {noFile}";
        return null;
    }

    // glTFast finishes over editor updates, which a batch run with -quit never gets, so this one runs in an open Editor.
    static GameObject loading;
    static double loadStarted;

    [MenuItem("Forge/Artifacts/Check glTF Load")]
    public static void StartLoad()
    {
        var p = Launch();
        var build = new ArtifactBuild
        {
            Inputs = new ArtifactInputs(p.Manifest), Nodes = new Dictionary<string, GameObject>(),
            Assets = ArtifactAssets.Resolve(p, out _),
        };
        loading = ArtifactNodes.Build(Node(p.Scene, "rocket"), build);
        loading.hideFlags = HideFlags.DontSave;
        loadStarted = EditorApplication.timeSinceStartup;
        EditorApplication.update += PollLoad;
    }

    static void PollLoad()
    {
        var model = loading != null ? loading.GetComponent<ArtifactGltf>() : null;
        bool done = model == null || model.Loading == null || model.Loading.IsCompleted;
        if (!done && EditorApplication.timeSinceStartup - loadStarted < 20) return;
        EditorApplication.update -= PollLoad;
        string note = null;
        string failure = !done ? "still loading after 20 s" : LoadResult(model, out note);
        if (failure == null) Debug.Log("PASS glTF load check: " + note);
        else Debug.LogError("FAIL glTF load check: " + failure);
        if (loading != null) UnityEngine.Object.DestroyImmediate(loading);
    }

    static string LoadResult(ArtifactGltf model, out string note)
    {
        note = null;
        if (model == null || model.Loading == null) return "no load started: " + model?.Problem;
        if (!model.Loaded) return "didn't load: " + (model.Problem ?? model.Loading.Exception?.GetBaseException().Message);
        var renderers = model.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return "no meshes";
        var bounds = renderers[0].bounds;
        foreach (var r in renderers) bounds.Encapsulate(r.bounds);
        if (bounds.size.y < 0.3f || bounds.size.y > 0.7f || bounds.size.y < bounds.size.x) return "it isn't standing up: " + bounds.size;
        if (Mathf.Abs(bounds.min.y) > 0.02f) return "its base is at " + bounds.min.y;
        note = $"{renderers.Length} meshes, {bounds.size.y:0.00} m tall, base at y = {bounds.min.y:0.000}";
        return null;
    }

    // The fixture's scene with a stand-in rocket (the timeline doesn't care what it moves).
    static (ArtifactTimeline clock, GameObject rocket, GameObject flame, GameObject all) Rig()
    {
        var p = Launch();
        var all = new GameObject("launch_check");
        var build = new ArtifactBuild { Inputs = new ArtifactInputs(p.Manifest), Nodes = new Dictionary<string, GameObject>() };
        var rocket = new GameObject("rocket");
        rocket.transform.SetParent(all.transform, false);
        var flame = ArtifactNodes.Build(Node(p.Scene, "flame"), build);
        flame.transform.SetParent(rocket.transform, false);
        flame.SetActive(false);
        var clock = ArtifactNodes.Build(Node(p.Scene, "flight"), build).GetComponent<ArtifactTimeline>();
        clock.transform.SetParent(all.transform, false);
        clock.Bind((JArray)p.Scene["rules"], "flight", new Dictionary<string, GameObject> { ["rocket"] = rocket, ["flame"] = flame });
        return (clock, rocket, flame, all);
    }

    static Color Tint(GameObject go)
    {
        var block = new MaterialPropertyBlock();
        go.GetComponentInChildren<Renderer>(true).GetPropertyBlock(block);
        return block.GetColor("_BaseColor");
    }

    static string Timeline(out string note)
    {
        note = null;
        var (clock, rocket, flame, all) = Rig();
        try
        {
            if (clock.Playing) return "autoplay is false, but it's playing";
            if (Mathf.Abs(rocket.transform.localPosition.y + 0.5f) > 0.001f || flame.activeSelf) return "t = 0 isn't on the pad, unlit";
            clock.Seek(0.5f);
            if (!flame.activeSelf) return "the flame didn't light at 0.5 s";
            if (Mathf.Abs(flame.transform.localScale.x - 0.6f) > 0.001f) return "flame scale at 0.5 s: " + flame.transform.localScale;
            clock.Seek(1.9f);
            float y = -0.5f + 0.6f * (1.9f - 0.8f) / 2.2f;
            if (Mathf.Abs(rocket.transform.localPosition.y - y) > 0.001f) return $"1.9 s: y {rocket.transform.localPosition.y}, expected {y}";
            float roll = rocket.transform.localEulerAngles.z;
            if (Mathf.Abs(roll - (360f - 3f)) > 0.05f) return "1.9 s: roll " + roll;
            var mid = Color.Lerp(ArtifactLook.Color("#FFD24C", Color.black), ArtifactLook.Color("#FF5A1F", Color.black), (1.9f - 0.5f) / 2.1f);
            var tint = Tint(flame);
            if (Mathf.Abs(tint.g - mid.g) > 0.01f || Mathf.Abs(tint.b - mid.b) > 0.01f) return $"1.9 s: flame {tint}, expected {mid}";
            clock.Seek(3.5f);
            if (Mathf.Abs(rocket.transform.localPosition.y - 0.1f) > 0.001f || flame.activeSelf) return "the end isn't up high with the flame out";
            note = $"pad at -0.50 → {y:0.00} m at 1.9 s → 0.10 m; lit 0.5 to 2.6 s; flame tint and scale follow keys";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(all); }
    }

    static string Clock(out string note)
    {
        note = null;
        var (clock, rocket, flame, all) = Rig();
        try
        {
            clock.Restart();
            for (int i = 0; i < 50; i++) clock.Advance(0.02f);
            if (!clock.Playing || Mathf.Abs(clock.Time - 1f) > 0.001f) return $"after 1 s: playing {clock.Playing}, t {clock.Time}";
            clock.Pause();
            clock.Advance(1f);
            if (Mathf.Abs(clock.Time - 1f) > 0.001f) return "pause didn't hold";
            clock.Play();
            clock.Advance(10f);
            if (clock.Playing || clock.Time != clock.Duration) return $"didn't stop at the end: t {clock.Time}";
            clock.Play();
            if (!clock.Playing || clock.Time != 0f) return "play after the end didn't start over";
            var p = Launch();
            var looping = (JObject)Node(p.Scene, "flight").DeepClone();
            looping["loop"] = true;
            var build = new ArtifactBuild { Inputs = new ArtifactInputs(p.Manifest), Nodes = new Dictionary<string, GameObject>() };
            var loop = ArtifactNodes.Build(looping, build).GetComponent<ArtifactTimeline>();
            loop.transform.SetParent(all.transform, false);
            loop.Play();
            loop.Advance(4f);
            if (!loop.Playing || Mathf.Abs(loop.Time - 0.5f) > 0.001f) return "loop: t " + loop.Time;
            note = "stops on the last frame, play starts over, a loop wraps (4 s → 0.5 s)";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(all); }
    }
}
