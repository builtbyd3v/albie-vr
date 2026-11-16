using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Headless checks for the first-run flow (<see cref="FirstRunGuide"/>) in a preview scene: no headset, no hub, no
/// voice (Albie's lines are captured). Forge > Core > Check First Run logs the report; <see cref="Run"/> returns it (for
/// `unity command eval`). PASS on the first line means every step passed. Works in Edit mode; in Play mode it also
/// checks the tools are registered with <see cref="AlbieTools"/>. Leaves the "shown" PlayerPref as it found it.
/// </summary>
public static class FirstRunCheck
{
    const string PanelPath = "Assets/_Forge/Core/Prefabs/ForgePanel.prefab";
    static readonly string[] Brands = { "muse", "claude", "meta", "oculus", "quest", "grok", "github", "slack", "openai", "google" };

    [MenuItem("Forge/Core/Check First Run")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    /// <summary>For -executeMethod.</summary>
    public static void RunBatch()
    {
        string report = Run();
        Debug.Log(report);
        EditorApplication.Exit(report.StartsWith("PASS") ? 0 : 1);
    }

    class World
    {
        public Scene scene;
        public FirstRunGuide guide;
        public Transform head;
        public readonly List<string> said = new List<string>();
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        int failed = 0;
        bool hadPref = PlayerPrefs.HasKey(FirstRunGuide.ShownPref);
        int pref = PlayerPrefs.GetInt(FirstRunGuide.ShownPref, 0);
        var previous = FirstRunGuide.Instance;
        var w = new World { scene = EditorSceneManager.NewPreviewScene() };
        FirstRunGuide.SayHook = (line, hint) => w.said.Add(line);
        try
        {
            failed += Step(sb, "No brand names in the copy", NoBrands);
            failed += Step(sb, "help_show tool file", HelpShowFile);
            failed += Step(sb, "Each button resolves to a real tool", ButtonsResolve);
            Build(w);
            failed += Step(sb, "Shown once", () => ShownOnce(w));
            failed += Step(sb, "Placed in view and within reach", () => InView(w));
            failed += Step(sb, "Text readable at 1 m", () => Readable(w));
            failed += Step(sb, "Got it shrinks it into the ? dot", () => GotIt(w));
            failed += Step(sb, "Reopen works (dot tap and help_show)", () => Reopen(w));
            failed += Step(sb, "Drive button before a truck", () => BrainNeedsTruck(w));
            failed += Step(sb, "Try button with no tool says the phrase", () => NoToolSaysPhrase(w));
            failed += Step(sb, "OnResumed text", () => Resumed(w));
            failed += Step(sb, "Panels in the way are seen", Overlap);
        }
        finally
        {
            FirstRunGuide.SayHook = null;
            if (hadPref) PlayerPrefs.SetInt(FirstRunGuide.ShownPref, pref); else PlayerPrefs.DeleteKey(FirstRunGuide.ShownPref);
            EditorSceneManager.ClosePreviewScene(w.scene);
            if (previous != null) previous.MakeCurrent();
        }
        return (failed == 0 ? "PASS" : $"FAIL ({failed})") + " first run check (" + (Application.isPlaying ? "Play" : "Edit") + " mode)\n" + sb;
    }

    static int Step(StringBuilder sb, string name, Func<string> check)
    {
        string result;
        try { result = check(); }
        catch (Exception e) { result = "FAIL " + e.GetType().Name + ": " + e.Message; }
        sb.Append("  ").Append(name).Append(": ").Append(result).Append('\n');
        return result.StartsWith("FAIL") ? 1 : 0;
    }

    static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void Build(World w)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<ForgePanel>(PanelPath);
        if (prefab == null) throw new Exception("no " + PanelPath);
        var holder = new GameObject("FirstRunCheck");
        SceneManager.MoveGameObjectToScene(holder, w.scene);
        w.head = new GameObject("Head").transform;
        w.head.SetParent(holder.transform, false);
        w.head.position = new Vector3(0f, 1.2f, 0f);
        var go = new GameObject("FirstRunGuide");
        go.transform.SetParent(holder.transform, false);
        w.guide = go.AddComponent<FirstRunGuide>();
        w.guide.Holder = holder.transform;
        w.guide.Init(prefab, w.head, null);
    }

    // ---- Steps -----------------------------------------------------------------------------------------------

    static string NoBrands()
    {
        var copy = new List<string>
        {
            FirstRunGuide.GreetingLine, FirstRunGuide.WelcomeLine, FirstRunGuide.CardLine, FirstRunGuide.TalkLine,
            FirstRunGuide.TryLine, FirstRunGuide.DriveHint, FirstRunGuide.NeedSlalom, FirstRunGuide.FirstHint,
        };
        foreach (var t in FirstRunGuide.Tries) { copy.Add(t.label); copy.Add(t.done); }
        foreach (string line in copy)
            foreach (string brand in Brands)
                Expect(line.ToLowerInvariant().IndexOf(brand, StringComparison.Ordinal) < 0, $"\"{brand}\" in \"{line}\"");
        return $"{copy.Count} lines clean";
    }

    static string HelpShowFile()
    {
        var asset = Resources.Load<TextAsset>("AlbieTools/first_run");
        Expect(asset != null, "no Resources/AlbieTools/first_run.json");
        var defs = JArray.Parse(asset.text);
        Expect(defs.Count == 1 && (string)defs[0]["name"] == "help_show", "expected one tool, help_show");
        Expect(defs[0]["description"] != null && (string)defs[0]["parameters"]?["type"] == "object", "description or parameters missing");
        if (Application.isPlaying) Expect(AlbieTools.Has("help_show"), "help_show isn't registered");
        return Application.isPlaying ? "help_show defined and registered" : "help_show defined";
    }

    static string ButtonsResolve()
    {
        var defs = new Dictionary<string, JObject>();
        foreach (var asset in Resources.LoadAll<TextAsset>("AlbieTools"))
            foreach (JObject def in JArray.Parse(asset.text)) defs[(string)def["name"]] = def;
        var parts = new List<string>();
        foreach (var t in FirstRunGuide.Tries)
        {
            Expect(defs.TryGetValue(t.tool, out var def), $"\"{t.label}\": no tool {t.tool} in any Resources/AlbieTools json");
            var args = JObject.Parse(t.argsJson);
            var props = (JObject)def["parameters"]?["properties"];
            foreach (var arg in args)
            {
                Expect(props != null && props[arg.Key] != null, $"\"{t.label}\": {t.tool} has no parameter {arg.Key}");
                if (props[arg.Key]["enum"] is JArray allowed)
                    Expect(allowed.ToString().Contains("\"" + (string)arg.Value + "\""), $"\"{t.label}\": {arg.Key} {arg.Value} not in {t.tool}'s list");
            }
            if (Application.isPlaying) Expect(AlbieTools.Has(t.tool), $"{t.tool} isn't registered");
            parts.Add(t.label + " -> " + t.tool + " " + t.argsJson);
        }
        return string.Join("; ", parts);
    }

    static string ShownOnce(World w)
    {
        PlayerPrefs.DeleteKey(FirstRunGuide.ShownPref);
        Expect(FirstRunGuide.ShouldShowWelcome, "a fresh install should show the card");
        w.guide.ShowCard(FirstRunGuide.CardLine);
        Expect(w.guide.CardShown, "card isn't showing");
        Expect(!FirstRunGuide.ShouldShowWelcome, "still marked as not shown");
        Expect(w.said.Count > 0 && w.said[w.said.Count - 1] == FirstRunGuide.CardLine, "Albie didn't say the card line");
        return "fresh: show; after one show the pref says shown, so the next launch only gets the ? dot";
    }

    static string InView(World w)
    {
        Transform card = w.guide.Card.transform;
        Vector3 d = card.position - w.head.position;
        float yaw = Vector3.Angle(Vector3.ProjectOnPlane(d, Vector3.up), Vector3.forward);
        Expect(d.magnitude < 0.65f, $"{d.magnitude:0.00} m away, out of reach");
        Expect(yaw <= 35f, $"{yaw:0} degrees off centre");
        Expect(Vector3.Dot(card.forward, d) > 0f, "facing away from the eyes");
        return $"{d.magnitude:0.00} m from the eyes, {yaw:0} degrees off centre, {-d.y:0.00} m below eye height, facing them";
    }

    static string Readable(World w)
    {
        float min = float.MaxValue;
        foreach (var t in w.guide.Card.Content.GetComponentsInChildren<TMP_Text>(true)) min = Mathf.Min(min, t.fontSize);
        Expect(min >= 13f, $"smallest text {min} mm");
        return $"smallest text {min} mm (0.7 degrees at 1 m)";
    }

    static string GotIt(World w)
    {
        Press(w, "Got it");
        Expect(!w.guide.CardShown, "card still showing");
        Expect(w.guide.OrbShown, "no ? dot");
        return "card hidden, ? dot showing";
    }

    static string Reopen(World w)
    {
        w.guide.Reopen();
        Expect(w.guide.CardShown && !w.guide.OrbShown, "dot tap didn't bring the card back");
        w.guide.CloseCard();
        var result = FirstRunGuide.HelpShowTool(new JObject());
        Expect(w.guide.CardShown, "help_show didn't bring the card back");
        Expect((string)result["status"] == "shown" && result["try"] is JArray tries && tries.Count == 3, "help_show result: " + result);
        return "dot tap and help_show both reopen it; help_show returns the 3 ideas";
    }

    static string BrainNeedsTruck(World w)
    {
        if (Application.isPlaying && Forge.Controllers.BrainLoops.Active != null) return "skipped in Play mode (a truck is out; a tap would start real laps)";
        Press(w, FirstRunGuide.Tries[2].label);
        Expect(FirstRunGuide.LastSaid == FirstRunGuide.NeedSlalom, "said: " + FirstRunGuide.LastSaid);
        Expect(w.guide.CardShown, "card closed without running anything");
        return "Albie: \"" + FirstRunGuide.NeedSlalom + "\"";
    }

    static string NoToolSaysPhrase(World w)
    {
        if (Application.isPlaying) return "skipped in Play mode (the tool is registered; a tap would build a real slalom)";
        Expect(!AlbieTools.Has("artifact_create"), "artifact_create registered in Edit mode");
        Press(w, FirstRunGuide.Tries[0].label);
        Expect(FirstRunGuide.LastSaid == "Say \"Make me a slalom\" and I'll do it.", "said: " + FirstRunGuide.LastSaid);
        Expect(!w.guide.CardShown && w.guide.OrbShown, "the card should fold into the dot on a try tap");
        return "Albie: \"" + FirstRunGuide.LastSaid + "\", card folded into the dot";
    }

    static string Resumed(World w)
    {
        Expect(FirstRunGuide.ResumeLine(5f, "x", "y") == null, "a line after 5 s away");
        Expect(FirstRunGuide.ResumeLine(FirstRunGuide.ResumeAfterS, "x", "y") == null, "a line at exactly the threshold");
        string truck = FirstRunGuide.ResumeLine(60f, "driving the slalom truck", FirstRunGuide.DriveHint);
        Expect(truck == "Welcome back! We were driving the slalom truck. " + FirstRunGuide.DriveHint, "truck line: " + truck);
        int before = w.said.Count;
        w.guide.OnResumed(10f);
        Expect(w.said.Count == before, "spoke after 10 s away");
        w.guide.OnResumed(60f);
        string said = w.said.Count > before ? w.said[w.said.Count - 1] : "";
        Expect(said.StartsWith("Welcome back! We were ", StringComparison.Ordinal) && said.EndsWith(" " + w.guide.LastHint, StringComparison.Ordinal), "said: " + said);
        return "10 s: nothing; 60 s: \"" + said + "\"";
    }

    static string Overlap()
    {
        Vector3 eye = new Vector3(0f, 1.2f, 0f);
        var card = new Vector2(0.19f, 0.19f);
        var panel = new Vector2(0.3f, 0.2f);
        Expect(FirstRunGuide.Overlaps(eye, new Vector3(0f, 1.04f, 0.5f), card, new Vector3(0f, 1.2f, 0.9f), panel), "a panel straight ahead behind the card isn't seen");
        Expect(!FirstRunGuide.Overlaps(eye, new Vector3(-0.19f, 1.04f, 0.46f), card, new Vector3(0.6f, 1.2f, 0.7f), panel), "a panel well to the right counts as covered");
        return "straight ahead behind it: covered; well to the side: clear";
    }

    static void Press(World w, string label)
    {
        foreach (var pill in w.guide.Card.GetComponentsInChildren<ForgePill>(true))
            if (pill.name == label)
            {
                pill.Button.onClick.Invoke();
                return;
            }
        throw new Exception("no button \"" + label + "\" on the card");
    }
}
