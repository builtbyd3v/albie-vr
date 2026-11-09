using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Forge.Artifacts;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Headless checks for the biology templates (cell, heart, brain, plant_cell, eye, dna): all are valid, buildable,
/// offered by <c>artifact_list</c> and made by <c>artifact_create</c>; every model is bundled, CC0, and matches its hash;
/// every label has a leader and the show and hide buttons cover them all; step captions and moving parts stay hidden
/// until their button is pressed; the heart beats about its own centre; the DNA turns a full circle and stopping puts
/// it back at rest. Forge > Artifacts > Check Biology Templates.
/// </summary>
public static class BioTemplatesCheck
{
    static readonly string[] Bio = { "cell", "heart", "brain", "plant_cell", "eye", "dna" };

    [MenuItem("Forge/Artifacts/Check Biology Templates")]
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
        string root = Path.Combine(Application.temporaryCachePath, "bio_templates_check");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string before = ArtifactLibrary.RootOverride;
        ArtifactLibrary.RootOverride = root;
        var sb = new StringBuilder();
        bool ok = true;
        try
        {
            ok &= Check(sb, "every biology template is valid and this host builds it", Valid);
            ok &= Check(sb, "artifact_list offers them and artifact_create makes them", Made);
            ok &= Check(sb, "every model is bundled, CC0, and matches its hash", Model);
            ok &= Check(sb, "every label has a leader, and Show and Hide labels cover them all", Labels);
            ok &= Check(sb, "captions and moving parts stay hidden until their button starts them", Hidden);
            ok &= Check(sb, "the heart squeezes about its own centre", Beat);
            ok &= Check(sb, "the DNA turns a full circle, and Stop puts it back at rest", Spin);
        }
        finally
        {
            ArtifactLibrary.RootOverride = before;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return (ok ? "PASS" : "FAIL") + " biology templates check\n" + sb;
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

    static JObject Scene(string template)
    {
        ArtifactLibrary.TryTemplate(template, out _, out var scene);
        return scene;
    }

    static JObject Node(JObject scene, string id)
    {
        foreach (JObject n in (JArray)scene["nodes"]) if ((string)n["id"] == id) return n;
        return null;
    }

    static string Valid(out string note)
    {
        note = null;
        var counts = new List<string>();
        foreach (string t in Bio)
        {
            if (!ArtifactLibrary.TryTemplate(t, out var manifest, out var scene)) return t + " isn't in the library";
            var errors = ArtifactValidator.Validate(manifest, scene);
            if (errors.Count > 0) return t + ": " + errors[0];
            var missing = ArtifactNodes.Missing(scene);
            if (missing.Count > 0) return t + " can't build: " + string.Join(", ", missing);
            if ((int)manifest["runtime"]["min_runtime"] > ArtifactSchema.HostRuntime) return t + " needs a newer host";
            var p = manifest["permissions"];
            if ((string)p["room"] != "none" || ((JArray)p["network"]).Count > 0 || ((JArray)p["connectors"]).Count > 0)
                return t + " asks for permissions; a picture to look at needs none";
            counts.Add($"{t} {((JArray)scene["nodes"]).Count} nodes, {((JArray)scene["rules"]).Count} rules");
        }
        note = string.Join("; ", counts);
        return null;
    }

    static string Made(out string note)
    {
        note = null;
        var list = ArtifactTools.List(new JObject());
        foreach (string t in Bio)
        {
            bool offered = false;
            foreach (var entry in (JArray)list["templates"]) offered |= (string)entry["template"] == t;
            if (!offered) return t + " isn't offered by artifact_list";
            var made = ArtifactTools.Create(new JObject { ["template"] = t, ["open"] = false });
            if ((string)made["id"] != t || (int)made["version"] != 1) return "create " + t + ": " + made;
        }
        var offeredByTool = CreateSchema()?["template"]?["enum"] as JArray;
        foreach (string t in ArtifactLibrary.Templates)
        {
            bool listed = false;
            if (offeredByTool != null) foreach (var e in offeredByTool) listed |= (string)e == t;
            if (!listed) return "artifact_create's template enum doesn't list " + t;
        }
        note = "made all " + Bio.Length + " with no network; the tool lists every template in the library";
        return null;
    }

    /// <summary>The properties of <c>artifact_create</c>'s parameters, from <c>Resources/AlbieTools/artifacts.json</c>.</summary>
    public static JObject CreateSchema()
    {
        foreach (JObject tool in JArray.Parse(Resources.Load<TextAsset>("AlbieTools/artifacts").text))
            if ((string)tool["name"] == "artifact_create") return tool["parameters"]?["properties"] as JObject;
        return null;
    }

    static string Model(out string note)
    {
        note = null;
        var sizes = new List<string>();
        foreach (string t in Bio)
        {
            var p = ArtifactPackage.Load(t, out string error);
            if (p == null) return t + ": " + error;
            var asset = (JObject)p.Manifest["assets"][0];
            if ((string)asset["license"] != "CC0") return t + " license " + asset["license"];
            byte[] bytes = p.ReadAsset((string)asset["path"], out string why);
            if (bytes == null) return t + ": " + why;
            if (bytes.Length > ArtifactSchema.MaxAssetBytes) return $"{t}: {bytes.Length} bytes is over the limit";
            string sha;
            using (var hash = SHA256.Create()) sha = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            if (sha != (string)asset["sha256"]) return t + " hash " + sha;
            sizes.Add($"{t} {bytes.Length / 1024} KB");
        }
        note = string.Join(", ", sizes);
        return null;
    }

    static string Labels(out string note)
    {
        note = null;
        int total = 0;
        foreach (string t in Bio)
        {
            var scene = Scene(t);
            var ids = new HashSet<string>();
            foreach (JObject n in (JArray)scene["nodes"]) ids.Add((string)n["id"]);
            var shown = Sets(scene, "labels_on", true);
            var hidden = Sets(scene, "labels_off", false);
            foreach (string id in ids)
            {
                if (!id.StartsWith("label_")) continue;
                string leader = "leader_" + id.Substring(6);
                if (!ids.Contains(leader)) return $"{t}: {id} has no leader";
                foreach (string part in new[] { id, leader })
                    if (!shown.Contains(part) || !hidden.Contains(part)) return $"{t}: Show or Hide labels misses {part}";
                total++;
            }
        }
        note = total + " labels";
        return null;
    }

    static HashSet<string> Sets(JObject scene, string input, bool value)
    {
        var set = new HashSet<string>();
        foreach (JObject r in (JArray)scene["rules"])
            if ((string)r["type"] == "on_input" && (string)r["input"] == input)
                foreach (JObject a in (JArray)r["do"])
                    if (a.Value<bool>("value") == value && ((string)a["set"]).EndsWith(".visible")) set.Add(((string)a["set"]).Split('.')[0]);
        return set;
    }

    // A rule on a timeline that doesn't autoplay must show nothing at t = 0, or the caption is up before anyone asks.
    static string Hidden(out string note)
    {
        note = null;
        int count = 0;
        foreach (string t in Bio)
        {
            var scene = Scene(t);
            var still = new HashSet<string>();
            foreach (JObject n in (JArray)scene["nodes"])
                if ((string)n["kind"] == "timeline" && n.Value<bool?>("autoplay") != true) still.Add((string)n["id"]);
            foreach (JObject r in (JArray)scene["rules"])
            {
                if ((string)r["type"] != "animate" || (string)r["property"] != "visible" || !still.Contains((string)r["timeline"])) continue;
                var first = (JArray)r["keys"][0];
                if ((float)first[0] != 0f || (bool)first[1]) return $"{t}: {r["target"]} shows at t = 0";
                var last = (JArray)r["keys"][((JArray)r["keys"]).Count - 1];
                if ((bool)last[1]) return $"{t}: {r["target"]} stays up after the timeline ends";
                count++;
            }
        }
        note = count + " parts";
        return null;
    }

    static string Beat(out string note)
    {
        note = null;
        var scene = Scene("heart");
        JArray scale = null, pos = null;
        foreach (JObject r in (JArray)scene["rules"])
            if ((string)r["type"] == "animate" && (string)r["target"] == "heart")
            {
                if ((string)r["property"] == "scale") scale = (JArray)r["keys"];
                if ((string)r["property"] == "pos") pos = (JArray)r["keys"];
            }
        if (scale == null || pos == null || scale.Count != pos.Count) return "the beat needs matching scale and pos keys";
        Vector3 Raw(int i) => new Vector3((float)pos[i][1][0], (float)pos[i][1][1], (float)pos[i][1][2]);
        Vector3 At(int i) => Raw(i) - Raw(0);
        // The model can rest at any size; the squeeze is relative to that.
        var heart = Node(scene, "heart");
        float rest = heart?["scale"] is JArray size ? (float)size[0] : 1f;
        if ((float)scale[0][1] != rest || (float)scale[scale.Count - 1][1] != rest) return "the beat doesn't start and end at the resting size " + rest;
        int deepest = 0;
        for (int i = 1; i < scale.Count; i++) if ((float)scale[i][1] < (float)scale[deepest][1]) deepest = i;
        float smallest = (float)scale[deepest][1] / rest;
        if (smallest >= 1f) return "the heart never squeezes";
        Vector3 centre = At(deepest) / (1f - smallest);
        for (int i = 0; i < scale.Count; i++)
        {
            if ((float)scale[i][0] != (float)pos[i][0]) return "key " + i + " times differ";
            float s = (float)scale[i][1] / rest;
            if ((At(i) - centre * (1f - s)).magnitude > 1e-4f) return $"key {i} is at {At(i)}, so it doesn't squeeze about {centre}";
        }
        note = $"down to {smallest:0.00} about {centre.ToString("F3")}";
        return null;
    }

    static string Spin(out string note)
    {
        note = null;
        var scene = Scene("dna");
        var model = Node(scene, "dna");
        var timeline = Node(scene, "spin");
        if (model == null || timeline == null) return "no dna model or spin timeline";
        if (timeline.Value<bool?>("autoplay") == true || timeline.Value<bool?>("loop") != true) return "the spin should loop and wait for its button";
        JArray keys = null;
        foreach (JObject r in (JArray)scene["rules"])
            if ((string)r["type"] == "animate" && (string)r["target"] == "dna" && (string)r["property"] == "rot") keys = (JArray)r["keys"];
        if (keys == null) return "nothing turns the model";
        var rest = (JArray)model["rot"];
        var first = (JArray)keys[0][1];
        var last = (JArray)keys[keys.Count - 1][1];
        for (int i = 0; i < 3; i++)
        {
            if ((float)first[i] != (float)rest[i]) return "the spin doesn't start at the model's resting rotation";
            float turn = (float)last[i] - (float)first[i];
            if (Mathf.Abs(turn) != (i == 1 ? 360f : 0f)) return "the spin should be one whole turn about y, not " + last;
        }
        if ((float)keys[keys.Count - 1][0] != (float)timeline["duration_s"]) return "the turn doesn't take the whole loop, so it would jump";
        var stop = new List<string>();
        foreach (JObject r in (JArray)scene["rules"])
            if ((string)r["type"] == "on_input" && (string)r["input"] == "spin_off")
                foreach (JObject a in (JArray)r["do"]) if ((string)a["timeline"] == "spin") stop.Add((string)a["action"]);
        if (string.Join(",", stop) != "restart,pause") return "Stop spinning should restart then pause the spin, not " + string.Join(",", stop);
        note = $"one turn in {(float)timeline["duration_s"]} s";
        return null;
    }
}
