using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// Headless checks for "Make me a slalom" (PROJECT_CONTEXT 16.3, Person 5, Nov 10): the floor_slalom generator and its fit
/// rules (C-12 3.3), layout records (6.1) and the template. Runs against the mock rooms, in a scratch library under
/// temporaryCachePath. Forge > Artifacts > Check Slalom logs the report; <see cref="Run"/> returns it.
/// </summary>
public static class SlalomCheck
{
    static string root;

    [MenuItem("Forge/Artifacts/Check Slalom")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    /// <summary>For -executeMethod.</summary>
    public static void RunBatch()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
        EditorApplication.Exit(report.StartsWith("PASS") ? 0 : 1);
    }

    public static string Run()
    {
        root = Path.Combine(Application.temporaryCachePath, "slalom_check");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string before = ArtifactLibrary.RootOverride;
        string notebookProject = NotebookStore.ProjectId, notebookRoot = NotebookStore.Root;
        ArtifactLibrary.RootOverride = root;
        var sb = new StringBuilder();
        bool ok = true;
        try
        {
            ok &= Check(sb, "living room: the full course, beside the couch", LivingRoom);
            ok &= Check(sb, "small room: shrinks and says so", SmallRoom);
            ok &= Check(sb, "closet: no_free_floor, with the size it needs", Closet);
            ok &= Check(sb, "never scanned: no_room_data", Unscanned);
            ok &= Check(sb, "no couch: full course, and says so", Loft);
            ok &= Check(sb, "turned room: still on free floor", Turned);
            ok &= Check(sb, "spacing shrinks before gates", FitOrder);
            ok &= Check(sb, "the same room gives the same course", Deterministic);
            ok &= Check(sb, "slots: cones, alternating gates, start, finish", Slots);
            ok &= Check(sb, "layout saved, reused, remade when furniture moves", Layouts);
            ok &= Check(sb, "layout origin survives the room frame", OriginRoundTrip);
            ok &= Check(sb, "template → library, versions, links", Template);
            ok &= Check(sb, "the slalom scene can be built", Buildable);
        }
        finally
        {
            ArtifactLibrary.RootOverride = before;
            NotebookStore.Close();
            if (notebookProject != null) NotebookStore.Open(notebookProject, notebookRoot);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return (ok ? "PASS" : "FAIL") + " slalom check\n" + sb;
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

    static FloorSlalom.Settings Default(string around = "COUCH")
    {
        var s = FloorSlalom.Settings.Default;
        s.Around = around;
        return s;
    }

    static FloorSlalom.Result Fit(IFloorPlan room, FloorSlalom.Settings s, out double ms)
    {
        var watch = Stopwatch.StartNew();
        var fit = FloorSlalom.Fit(room, s);
        ms = watch.Elapsed.TotalMilliseconds;
        return fit;
    }

    // Independent of the generator's grid: walks the footprint every 5 cm in world space and asks the room directly.
    static string OnFreeFloor(IFloorPlan room, FloorSlalom.Result fit, FloorSlalom.Settings s)
    {
        var r = Quaternion.Euler(0f, fit.Yaw, 0f);
        float end = fit.Spacing * (fit.Gates + 1) + FloorSlalom.EndMargin;
        // The grid tests cell centres; allow half a cell of slack at the clearance edge.
        float clearance = s.Clearance - FloorSlalom.Cell * 0.75f;
        for (float z = -FloorSlalom.EndMargin; z <= end; z += 0.05f)
            for (float x = -s.Lane; x <= s.Lane; x += 0.05f)
            {
                var p = fit.Origin + r * new Vector3(x, 0f, z);
                if (!room.IsFree(p, clearance)) return $"footprint point ({x:0.00}, {z:0.00}) isn't free floor";
            }
        foreach (var pair in fit.Slots)
            foreach (var pose in pair.Value)
                if (!room.IsFree(fit.Origin + r * pose.position, clearance)) return pair.Key + " has a slot off free floor";
        return null;
    }

    static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    static string LivingRoom(out string note)
    {
        note = null;
        var room = MockFloorPlan.LivingRoom();
        var s = Default();
        var fit = Fit(room, s, out double ms);
        if (!fit.Ok) return fit.Code + ": " + fit.Message;
        if (fit.Gates != 5 || Mathf.Abs(fit.Spacing - 0.8f) > 0.001f || fit.Shrunk) return $"placed {fit.Gates} gates at {F(fit.Spacing)}";
        if (!fit.AroundFound || fit.Message != null) return "around/message: " + fit.Message;
        string free = OnFreeFloor(room, fit, s);
        if (free != null) return free;
        room.TryFind("COUCH", out var couch, out float couchYaw, out _);
        var middle = fit.Origin + Quaternion.Euler(0f, fit.Yaw, 0f) * new Vector3(0f, 0f, fit.Spacing * (fit.Gates + 1) * 0.5f);
        float distance = Vector2.Distance(new Vector2(middle.x, middle.z), new Vector2(couch.x, couch.z));
        float parallel = Mathf.Abs(Mathf.DeltaAngle(fit.Yaw, couchYaw)) % 180f;
        parallel = Mathf.Min(parallel, 180f - parallel);
        // The coffee table sits between the couch and the floor in front of it, so "beside" is just past the table.
        if (distance > 2.5f) return $"the course's middle is {F(distance)} m from the couch";
        if (parallel > 1f) return $"the course is {parallel:0}° off the couch's line";
        note = $"5 gates, 0.8 m, middle {F(distance)} m from the couch and parallel to it; {fit.Tried} placements tried in {ms:0} ms";
        return null;
    }

    static string SmallRoom(out string note)
    {
        note = null;
        var room = MockFloorPlan.SmallRoom();
        var s = Default();
        var fit = Fit(room, s, out double ms);
        if (!fit.Ok) return fit.Code + ": " + fit.Message;
        if (!fit.Shrunk || fit.Gates < 3 || fit.Spacing < 0.6f - 0.001f) return $"{fit.Gates} gates at {F(fit.Spacing)}, shrunk {fit.Shrunk}";
        if (fit.AroundFound || fit.Message == null || !fit.Message.Contains("tight") || !fit.Message.Contains("couch")) return "message: " + fit.Message;
        string free = OnFreeFloor(room, fit, s);
        if (free != null) return free;
        note = $"\"{fit.Message}\" ({ms:0} ms)";
        return null;
    }

    static string Closet(out string note)
    {
        note = null;
        var fit = Fit(MockFloorPlan.Closet(), Default(), out double ms);
        if (fit.Ok) return $"placed {fit.Gates} gates in a closet";
        if (fit.Code != "no_free_floor" || !fit.Message.Contains("3 gates") || !fit.Message.Contains("3.0 by 1.2 m") || !fit.Message.Contains("15 cm")) return fit.Code + ": " + fit.Message;
        note = $"\"{fit.Message}\" ({ms:0} ms)";
        return null;
    }

    static string Unscanned(out string note)
    {
        note = null;
        var fit = FloorSlalom.Fit(MockFloorPlan.Unscanned(), Default());
        var none = FloorSlalom.Fit(null, Default());
        if (fit.Ok || fit.Code != "no_room_data" || none.Code != "no_room_data") return $"{fit.Code} / {none.Code}";
        note = $"\"{fit.Message}\"";
        return null;
    }

    static string Loft(out string note)
    {
        note = null;
        var room = MockFloorPlan.Loft();
        var s = Default();
        var fit = Fit(room, s, out double ms);
        if (!fit.Ok || fit.Shrunk || fit.AroundFound) return $"ok {fit.Ok}, shrunk {fit.Shrunk}, around {fit.AroundFound}";
        if (fit.Message == null || !fit.Message.Contains("can't find a couch")) return "message: " + fit.Message;
        var noAround = FloorSlalom.Fit(room, Default(null));
        if (noAround.Message != null) return "with no around asked, it still said: " + noAround.Message;
        note = $"\"{fit.Message}\" ({ms:0} ms)";
        return OnFreeFloor(room, fit, s);
    }

    static string Turned(out string note)
    {
        note = null;
        var room = MockFloorPlan.TurnedLivingRoom();
        var s = Default();
        var fit = Fit(room, s, out double ms);
        if (!fit.Ok || fit.Shrunk || !fit.AroundFound) return $"ok {fit.Ok}, shrunk {fit.Shrunk}, around {fit.AroundFound}: {fit.Message}";
        // The couch runs along the room's long side, which points at 30 + 90 = 120° once the room is turned 30°.
        room.TryFind("COUCH", out _, out float couchYaw, out _);
        float off = Mathf.Abs(Mathf.DeltaAngle(fit.Yaw, couchYaw)) % 180f;
        if (Mathf.Abs(Mathf.DeltaAngle(couchYaw, 120f)) > 0.01f || Mathf.Min(off, 180f - off) > 1f) return $"course yaw {fit.Yaw:0}, couch {couchYaw:0}, in a room turned 30°";
        note = $"course at {fit.Yaw:0}°, along the couch, in a room turned 30° ({ms:0} ms)";
        return OnFreeFloor(room, fit, s);
    }

    static string FitOrder(out string note)
    {
        note = null;
        // 5 m long: 5 gates fit at 0.6 m (4.2 m + 0.5 m clearance) but not 0.7 m (4.8 m); 4 gates at 0.8 m would need 4.6 m.
        var room = new MockFloorPlan("corridor", new Vector2(5.0f, 2.1f));
        var wide = Default(null);  // the contract's first guess, which these room sizes were worked out for
        wide.Lane = 0.7f;
        wide.Clearance = 0.25f;
        var fit = FloorSlalom.Fit(room, wide);
        if (!fit.Ok || fit.Gates != 5 || Mathf.Abs(fit.Spacing - 0.6f) > 0.001f) return $"placed {fit.Gates} gates at {F(fit.Spacing)}";
        // 4.4 m: no 5-gate course fits (4.2 m + 0.5 m), so 4 gates, trying 0.8 and 0.7 m before 0.6 m (3.6 m).
        var shorter = FloorSlalom.Fit(new MockFloorPlan("short", new Vector2(4.4f, 2.1f)), wide);
        if (!shorter.Ok || shorter.Gates != 4 || Mathf.Abs(shorter.Spacing - 0.6f) > 0.001f) return $"4.4 m room: {shorter.Gates} gates at {F(shorter.Spacing)}";
        note = $"5 m corridor: 5 gates at 0.6 m; 4.4 m: 4 gates at 0.6 m (\"{shorter.Message}\")";
        return null;
    }

    static string Deterministic(out string note)
    {
        note = null;
        var a = FloorSlalom.Fit(MockFloorPlan.LivingRoom(), Default());
        var b = FloorSlalom.Fit(MockFloorPlan.LivingRoom(), Default());
        if ((a.Origin - b.Origin).sqrMagnitude > 1e-8f || Mathf.Abs(a.Yaw - b.Yaw) > 1e-4f) return "two fits of the same room differ";
        return null;
    }

    static string Slots(out string note)
    {
        note = null;
        var slots = FloorSlalom.Slots("course", 4, 0.6f, 0.6f);
        if (slots["course.cones"].Count != 4 || slots["course.gates"].Count != 4) return "counts";
        if (slots["course.start"].Count != 1 || slots["course.finish"].Count != 1) return "start/finish";
        if (Mathf.Abs(slots["course.finish"][0].position.z - 3.0f) > 0.001f) return "finish at " + slots["course.finish"][0].position.z;
        for (int i = 0; i < 4; i++)
        {
            var gate = slots["course.gates"][i].position;
            if (Mathf.Abs(gate.x - (i % 2 == 0 ? 0.3f : -0.3f)) > 0.001f || Mathf.Abs(gate.z - 0.6f * (i + 1)) > 0.001f) return $"gate {i} at {gate}";
        }
        foreach (var pair in slots) foreach (var pose in pair.Value) if (pose.position.y != 0f) return pair.Key + " isn't on the floor";
        // The fixture's small-room layout agrees with this geometry.
        var fixture = (JObject)NotebookFiles.Parse(Resources.Load<TextAsset>("Mocks/artifacts/slalom/layouts/lay_0002").text);
        var fromFixture = ArtifactLayouts.Slots(fixture);
        foreach (var pair in slots)
            for (int i = 0; i < pair.Value.Count; i++)
                if ((fromFixture[pair.Key][i].position - pair.Value[i].position).sqrMagnitude > 1e-6f) return $"lay_0002 differs at {pair.Key}[{i}]";
        note = "matches the lay_0002 fixture";
        return null;
    }

    static string Layouts(out string note)
    {
        note = null;
        ArtifactLibrary.TryTemplate("slalom", out var manifest, out var scene);
        var generator = (JObject)scene["generators"][0];
        var scenario = (JObject)manifest["scenarios"][0];
        var inputs = new ArtifactInputs(manifest);
        var room = MockFloorPlan.LivingRoom();

        var first = ArtifactGenerators.Place(manifest, generator, inputs, scenario, room, false);
        if (!first.Ok || first.Reused || (string)first.Layout["layout_id"] != "lay_0001") return "first: " + first.Code + " " + first.Layout?["layout_id"];
        string path = Path.Combine(root, "slalom", "layouts", "lay_0001.json");
        if (!File.Exists(path)) return "no file at " + path;
        var saved = (JObject)NotebookFiles.Parse(File.ReadAllText(path));
        if ((string)saved["room"]["key"] != "mock:living_room" || (int)saved["fit"]["placed"]["gates"] != 5 || saved["room"]["origin"] == null) return "saved " + saved["room"] + saved["fit"];
        string anchor = (string)saved["room"]["anchor"];
        if (string.IsNullOrEmpty(anchor) || !ArtifactAnchors.Exists(anchor)) return "no saved spatial anchor: " + saved["room"]["anchor"];

        var again = ArtifactGenerators.Place(manifest, generator, inputs, scenario, room, false);
        if (!again.Ok || !again.Reused || (string)again.Layout["layout_id"] != "lay_0001") return "a rerun didn't reuse lay_0001";
        if ((again.Origin.position - first.Origin.position).magnitude > 0.01f) return "the reused origin moved " + (again.Origin.position - first.Origin.position).magnitude;
        foreach (var pair in first.Slots)
            for (int i = 0; i < pair.Value.Count; i++)
                if ((again.Slots[pair.Key][i].position - pair.Value[i].position).magnitude > 0.002f) return $"reused {pair.Key}[{i}] moved";

        var forced = ArtifactGenerators.Place(manifest, generator, inputs, scenario, room, true);
        if ((string)forced.Layout["layout_id"] != "lay_0002") return "relayout gave " + forced.Layout["layout_id"];

        inputs.TrySet("spacing_m", 0.7);
        var other = ArtifactGenerators.Place(manifest, generator, inputs, scenario, room, false);
        if (other.Reused || (string)other.Layout["layout_id"] != "lay_0003") return "a new spacing reused a layout";
        inputs.TrySet("spacing_m", 0.8);

        // A box dropped in the middle of the newest 0.8 m course: the saved layout no longer fits.
        var middle = forced.Origin.position + forced.Origin.rotation * new Vector3(0f, 0f, 2.4f);
        var local = Quaternion.Euler(0f, -room.Yaw, 0f) * (middle - room.Origin);
        room.Add("OTHER", new Vector2(local.x, local.z), new Vector3(0.4f, 0.4f, 0.4f));
        var moved = ArtifactGenerators.Place(manifest, generator, inputs, scenario, room, false);
        if (!moved.Ok || moved.Reused || moved.Message == null || !moved.Message.StartsWith("Something in the room has moved")) return "moved furniture: " + moved.Message;
        if ((string)moved.Layout["layout_id"] != "lay_0004") return "moved gave " + moved.Layout["layout_id"];

        var elsewhere = ArtifactGenerators.Place(manifest, generator, inputs, scenario, MockFloorPlan.Loft(), false);
        if (elsewhere.Reused || (string)elsewhere.Layout["layout_id"] != "lay_0005") return "another room reused a layout";

        File.WriteAllText(Path.Combine(root, "slalom", "layouts", "lay_0099.json"), "{ broken");
        var afterDamage = ArtifactGenerators.Place(manifest, generator, inputs, scenario, MockFloorPlan.Loft(), false);
        if (!afterDamage.Ok || !afterDamage.Reused) return "a damaged layout file broke the lookup";
        ArtifactAnchors.Forget((string)afterDamage.Layout["room"]["anchor"]);
        var lost = ArtifactGenerators.Place(manifest, generator, inputs, scenario, MockFloorPlan.Loft(), false);
        if (!lost.Ok || lost.Reused || lost.Message == null || !lost.Message.StartsWith("I can't find the saved course")) return "lost anchor: " + lost.Message;
        var none = ArtifactGenerators.Place(manifest, generator, inputs, scenario, MockFloorPlan.Unscanned(), false);
        if (none.Ok || none.Code != "no_room_data") return "unscanned: " + none.Code;
        note = "lay_0001 reused on rerun; relayout, a new spacing, moved furniture, another room and a lost anchor each made a new one";
        return null;
    }

    static string OriginRoundTrip(out string note)
    {
        note = null;
        ArtifactLibrary.TryTemplate("slalom", out var manifest, out _);
        var room = MockFloorPlan.TurnedLivingRoom();
        var fit = FloorSlalom.Fit(room, Default());
        var layout = ArtifactLayouts.Make("slalom", 1, "default", 42, room, new JObject { ["gates"] = 5, ["spacing_m"] = 0.8 }, fit, null);
        var back = ArtifactLayouts.Origin(layout, room);
        float dp = (back.position - fit.Origin).magnitude, da = Mathf.Abs(Mathf.DeltaAngle(back.rotation.eulerAngles.y, fit.Yaw));
        if (dp > 0.005f || da > 0.2f) return $"origin moved {dp * 100f:0.0} cm, {da:0.0}°";
        var moved = new MockFloorPlan("living_room_turned", room.Size, true, room.Origin + new Vector3(10f, 0f, 5f), room.Yaw + 90f);
        var there = ArtifactLayouts.Origin(layout, moved);
        var expected = moved.RoomToWorld.MultiplyPoint3x4(room.RoomToWorld.inverse.MultiplyPoint3x4(fit.Origin));
        if ((there.position - expected).magnitude > 0.005f) return "didn't follow the room when it was loaded somewhere else";
        note = $"{dp * 1000f:0.0} mm, {da:0.00}°; follows the room when it loads elsewhere";
        return null;
    }

    static string Template(out string note)
    {
        note = null;
        NotebookStore.Close();
        var a = ArtifactLibrary.FromTemplate("slalom", null);
        if (a.Change != "created" || a.Version != 1) return $"first: {a.Change} v{a.Version}";
        var manifest = (JObject)NotebookFiles.Parse(File.ReadAllText(Path.Combine(root, "slalom", "manifest.json")));
        if ((string)manifest["kind"] != "standalone" || manifest["links"]["thing"].Type != JTokenType.Null) return "with no project, it isn't standalone";
        var b = ArtifactLibrary.FromTemplate("slalom", null);
        if (b.Change != "unchanged" || b.Version != 1) return $"again: {b.Change} v{b.Version}";
        var c = ArtifactLibrary.FromTemplate("slalom", new JObject { ["gates"] = 6 });
        if (c.Change != "updated" || c.Version != 2) return $"6 gates: {c.Change} v{c.Version}";
        string Refused(JObject inputs, string template = "slalom")
        {
            try { ArtifactLibrary.FromTemplate(template, inputs); return "accepted"; }
            catch (ArgumentException e) { return e.Message.Split(':')[0]; }
        }
        if (Refused(new JObject { ["gates"] = 20 }) != "bad_value") return "gates 20 wasn't refused";
        if (Refused(new JObject { ["gates"] = 4.5 }) != "bad_value") return "gates 4.5 wasn't refused";
        if (Refused(new JObject { ["cones"] = 4 }) != "unknown_input") return "an unknown input wasn't refused";
        if (Refused(null, "hoops") != "unknown_template") return "an unknown template wasn't refused";

        string nb = Path.Combine(root, "nb");
        NotebookStore.Open("proj_truck", nb);
        NotebookStore.CutVersion("thing", "Wider truck.");
        NotebookStore.CutVersion("thing", "Wider still.");
        var d = ArtifactLibrary.FromTemplate("slalom", new JObject { ["gates"] = 6 });
        manifest = (JObject)NotebookFiles.Parse(File.ReadAllText(Path.Combine(root, "slalom", "manifest.json")));
        if ((string)manifest["kind"] != "project" || (string)manifest["links"]["project"] != "proj_truck" || (int)manifest["links"]["project_version"] != 2) return "links " + manifest["links"];
        if (d.Change != "unchanged" || d.Version != 2) return $"only the links changed, but it was {d.Change} v{d.Version}";
        var package = ArtifactPackage.Load("slalom", out string error);
        if (package == null) return "the host can't load it: " + error;
        var errors = ArtifactValidator.Validate(package.Manifest, package.Scene);
        if (errors.Count > 0) return "validator: " + errors[0];
        var list = ArtifactLibrary.List();
        if (list.Count != 1 || (int)list[0]["version"] != 2) return "list " + list;
        note = "v1 created, unchanged, v2 with 6 gates; refusals: range, whole number, unknown input, unknown template; linked to proj_truck v2";
        return null;
    }

    static string Buildable(out string note)
    {
        note = null;
        ArtifactLibrary.TryTemplate("slalom", out var manifest, out var scene);
        var missing = ArtifactNodes.Missing(scene);
        if (missing.Count > 0) return "missing " + string.Join(", ", missing);
        var errors = ArtifactValidator.Validate(manifest, scene);
        if (errors.Count > 0) return "template: " + errors[0];
        var bundled = (JObject)NotebookFiles.Parse(Resources.Load<TextAsset>("Mocks/artifacts/slalom/scene").text);
        if (!JToken.DeepEquals(bundled, scene)) return "the template scene and the fixture scene differ";
        note = "validator and host agree; template matches the fixture";
        return null;
    }
}
