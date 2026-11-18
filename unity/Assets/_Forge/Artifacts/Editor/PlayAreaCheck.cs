using System;
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
/// Headless checks for the play area chosen in Albie (<see cref="UserPlayArea"/>) and the desk fallback, on mock rooms:
/// a tiny (stationary) Quest boundary falls back to the room's clear floor; "quest" mode keeps it a hard limit and the
/// dash goes on the desk instead; a drawn outline limits placement and survives a reload in a turned room; whole-room
/// mode ignores the boundary; hoops never go on a desk; a saved course outside a new drawn area is remade.
/// Forge > Artifacts > Check Play Area.
/// </summary>
public static class PlayAreaCheck
{
    static string root;

    [MenuItem("Forge/Artifacts/Check Play Area")]
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
        root = Path.Combine(Application.temporaryCachePath, "play_area_check");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string before = ArtifactLibrary.RootOverride;
        string notebookProject = NotebookStore.ProjectId, notebookRoot = NotebookStore.Root;
        ArtifactLibrary.RootOverride = root;
        UserPlayArea.Reload();
        NotebookStore.Close();
        var sb = new StringBuilder();
        bool ok = true;
        try
        {
            ok &= Check(sb, "polygon maths: area, crossing edges, rectangle, snap, nearest corner", Maths);
            ok &= Check(sb, "auto + tiny Quest boundary: the dash uses the room's clear floor, says so once", TinyBoundary);
            ok &= Check(sb, "auto + roomy Quest boundary: a course that fits stays inside it", RoomyBoundary);
            ok &= Check(sb, "quest mode: a hard limit; the dash goes on the desk at 1:N and says why", QuestModeDesk);
            ok &= Check(sb, "room mode: the whole clear floor, past the Quest boundary", RoomMode);
            ok &= Check(sb, "a drawn area limits placement, and is saved in the room's frame", Drawn);
            ok &= Check(sb, "hoops in a too-small drawn area: refused with buttons, never on the desk", HoopsRefused);
            ok &= Check(sb, "a saved course outside a new drawn area is remade (boundary changed)", BoundaryChanged);
            ok &= Check(sb, "no floor fits and there's a table: dash and slalom go on the desk", NoFloorDesk);
        }
        finally
        {
            ArtifactLibrary.RootOverride = before;
            UserPlayArea.Reload();
            NotebookStore.Close();
            if (notebookProject != null) NotebookStore.Open(notebookProject, notebookRoot);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return (ok ? "PASS" : "FAIL") + " play area check\n" + sb;
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

    static Vector2[] Square(float cx, float cz, float half) =>
        new[] { new Vector2(cx - half, cz - half), new Vector2(cx + half, cz - half), new Vector2(cx + half, cz + half), new Vector2(cx - half, cz + half) };

    // A built-in course compiled, its generator, and live inputs. A fresh artifact id each time, so layouts never mix.
    static int made;
    static void Course(string template, out JObject manifest, out JObject generator, out ArtifactInputs inputs, float length = 0f)
    {
        JObject scene;
        if (ArtifactCourses.IsTemplate(template))
        {
            var spec = ArtifactCourses.Preset(template, null, out var sliders);
            spec["id"] = template + "_pa" + (++made);
            ArtifactCourses.Compile(spec, sliders, out manifest, out scene, out _);
        }
        else
        {
            ArtifactLibrary.TryTemplate(template, out manifest, out scene);
            manifest["id"] = template + "_pa" + (++made);
        }
        generator = (JObject)scene["generators"][0];
        inputs = new ArtifactInputs(manifest);
        if (length > 0f && inputs.Has("length_m")) inputs.TrySet("length_m", length);
    }

    static ArtifactGenerators.Placement Place(string template, IFloorPlan room, float length = 0f, bool forceNew = true)
    {
        Course(template, out var manifest, out var generator, out var inputs, length);
        return ArtifactGenerators.Place(manifest, generator, inputs, null, room, forceNew);
    }

    // Every slot of a floor placement, in world xz, is inside the polygon by at least margin.
    static string AllInside(ArtifactGenerators.Placement placed, Vector2[] polygon, float margin)
    {
        foreach (var pair in placed.Slots)
            foreach (var slot in pair.Value)
            {
                var world = placed.Origin.position + placed.Origin.rotation * slot.position;
                if (!PlayArea.Inside(polygon, world, margin)) return $"{pair.Key} at ({F(world.x)}, {F(world.z)}) is outside the area";
            }
        return null;
    }

    static bool AnyOutside(ArtifactGenerators.Placement placed, Vector2[] polygon)
    {
        foreach (var pair in placed.Slots)
            foreach (var slot in pair.Value)
                if (!PlayAreaShape.Contains(polygon, new Vector2((placed.Origin.position + placed.Origin.rotation * slot.position).x,
                                                                 (placed.Origin.position + placed.Origin.rotation * slot.position).z))) return true;
        return false;
    }

    // ---------- checks ----------

    static string Maths(out string note)
    {
        note = null;
        var sq = Square(0f, 0f, 1f);
        if (Mathf.Abs(PlayAreaShape.Area(sq) - 4f) > 1e-4f) return "2 x 2 square area " + F(PlayAreaShape.Area(sq));
        if (!PlayAreaShape.IsSimple(sq)) return "a square isn't simple";
        var bowTie = new[] { new Vector2(-1f, -1f), new Vector2(1f, 1f), new Vector2(1f, -1f), new Vector2(-1f, 1f) };
        if (PlayAreaShape.IsSimple(bowTie)) return "a bow tie counted as simple";
        if (PlayAreaShape.Problem(bowTie, 4) == null) return "a bow tie has no problem";
        if (PlayAreaShape.Problem(Square(0f, 0f, 0.3f), 4) == null) return "a 0.36 m² area was accepted";
        if (PlayAreaShape.Problem(sq, 2) == null) return "two corners were accepted";
        var r = PlayAreaShape.Rectangle(new Vector2(0f, 0f), new Vector2(2f, 3f), 0f);
        if (Mathf.Abs(PlayAreaShape.Area(r) - 6f) > 1e-3f || !PlayAreaShape.IsSimple(r)) return "rectangle 2 x 3 area " + F(PlayAreaShape.Area(r));
        var turned = PlayAreaShape.Rectangle(new Vector2(0f, 0f), new Vector2(2f, 3f), 30f);
        if (!PlayAreaShape.IsSimple(turned) || PlayAreaShape.Area(turned) <= 0.1f) return "turned rectangle is degenerate";
        if ((turned[2] - new Vector2(2f, 3f)).sqrMagnitude > 1e-4f) return "turned rectangle's far corner isn't where the drag ended";
        var snapped = PlayAreaShape.Snapped(new Vector2(1.234f, -0.776f));
        if (Mathf.Abs(snapped.x - 1.25f) > 1e-4f || Mathf.Abs(snapped.y + 0.8f) > 1e-4f) return $"snap ({F(snapped.x)}, {F(snapped.y)})";
        if (PlayAreaShape.Nearest(sq, 4, new Vector2(0.9f, 0.95f), 0.2f) != 2 || PlayAreaShape.Nearest(sq, 4, Vector2.zero, 0.2f) != -1) return "nearest corner";
        if (!UserPlayArea.IsTiny(Square(0f, 0f, 0.9f)) || UserPlayArea.IsTiny(Square(0f, 0f, 1.5f))) return "tiny: a 1.8 m square is tiny, a 3 m one isn't";
        note = "bow tie refused, 30° rectangle ends under the drag";
        return null;
    }

    static string TinyBoundary(out string note)
    {
        note = null;
        var tiny = Square(0f, 0f, 0.8f);  // 2.6 m², about a stationary boundary
        var room = MockFloorPlan.Loft().WithBoundary(tiny);
        var direct = RoomCourse.Fit(room, RoomCourse.Read(ArtifactCourses.Preset("dash", null, out _), null));
        if (direct.Ok || !direct.BoundaryLimited) return "the bare generator should be stopped by the tiny boundary: " + direct.Message;
        if (direct.Message == null || direct.Message.Contains("Redraw") || !direct.Message.Contains("Play area")) return "bare generator message: " + direct.Message;
        var placed = Place("dash", room);
        if (!placed.Ok) return placed.Code + ": " + placed.Message;
        if (placed.Scale < 0.99f) return "went on a desk, not the floor";
        if (placed.Area == null || placed.Area.Kind != PlayAreaPlan.Source.Room) return "not fitted on the room's clear floor";
        if (!AnyOutside(placed, tiny)) return "the dash should reach past the tiny boundary";
        if (placed.Message == null || !placed.Message.Contains("room scan") || !placed.Message.Contains(UserPlayArea.Safety)) return "first message: " + placed.Message;
        var again = Place("dash", MockFloorPlan.Loft().WithBoundary(tiny));
        if (!again.Ok || (again.Message != null && again.Message.Contains(UserPlayArea.Safety))) return "the safety line was said twice: " + again.Message;
        note = "\"" + placed.Message + "\"";
        return null;
    }

    static string RoomyBoundary(out string note)
    {
        note = null;
        var big = Square(0f, 0f, 2.5f);  // 25 m²
        var room = new MockFloorPlan("roomy", new Vector2(8f, 6f)).WithBoundary(big);
        var placed = Place("park", room);
        if (!placed.Ok) return placed.Code + ": " + placed.Message;
        if (placed.Area == null || placed.Area.Kind != PlayAreaPlan.Source.Quest) return "should be fitted inside the Quest boundary";
        string inside = AllInside(placed, big, PlayArea.Margin - FloorSlalom.Cell);
        if (inside != null) return inside;
        // A 3 x 3 m boundary (9 m², not tiny) leaves a 2.1 m square for a course: even the smallest slalom (4.6 m)
        // doesn't fit, the 8 x 6 m room does. Auto goes past the boundary, and says so.
        var smaller = new MockFloorPlan("roomy_small_boundary", new Vector2(8f, 6f)).WithBoundary(Square(0f, 0f, 1.5f));
        var slalom = Place("slalom", smaller);
        if (!slalom.Ok || slalom.Area == null || slalom.Area.Kind != PlayAreaPlan.Source.Room) return "the slalom should use the room's floor: " + slalom.Code + " " + slalom.Message;
        if (slalom.Message == null || !slalom.Message.Contains("doesn't fit inside your Quest boundary")) return "slalom message: " + slalom.Message;
        note = "parking inside the boundary; the slalom past a 9 m² one: \"" + slalom.Message + "\"";
        return null;
    }

    static string QuestModeDesk(out string note)
    {
        note = null;
        var tiny = Square(-1f, 0.5f, 0.8f);
        var room = MockFloorPlan.LivingRoom().WithBoundary(tiny);  // has a 1.0 x 0.5 m coffee table
        UserPlayArea.SetMode(room, UserPlayArea.Mode.Quest);
        if (UserPlayArea.ModeOf(room) != UserPlayArea.Mode.Quest) return "mode didn't save";
        Course("dash", out var manifest, out var generator, out var inputs);
        var placed = ArtifactGenerators.Place(manifest, generator, inputs, null, room, false);
        if (!placed.Ok) return placed.Code + ": " + placed.Message;
        if (!placed.DeskFallback || placed.Scale >= 0.99f) return "the dash should go on the desk";
        if (placed.Message == null || !placed.Message.Contains("on your desk at 1:") || !placed.Message.Contains("Play area")) return "message: " + placed.Message;
        if ((string)placed.Layout["room"]["surface"] != "desk") return "the layout doesn't say desk";
        var reused = ArtifactGenerators.Place(manifest, generator, inputs, null, room, false);
        UserPlayArea.SetMode(room, UserPlayArea.Mode.Auto);
        if (!reused.Ok || !reused.Reused || !reused.DeskFallback || Mathf.Abs(reused.Scale - placed.Scale) > 0.001f) return "the second open should reuse the desk layout: " + reused.Message;
        note = $"scale {F(placed.Scale)}: \"{placed.Message}\"";
        return null;
    }

    static string RoomMode(out string note)
    {
        note = null;
        var boundary = Square(0f, 0f, 1.6f);  // 10 m²: roomy enough not to count as tiny
        var room = new MockFloorPlan("room_mode", new Vector2(7f, 3f)).WithBoundary(boundary);
        UserPlayArea.SetMode(room, UserPlayArea.Mode.Quest);
        var limited = Place("dash", room, 6f);
        if (!limited.Ok || limited.Area == null || limited.Area.Kind != PlayAreaPlan.Source.Quest) return "quest mode should fit a short dash inside: " + limited.Message;
        if (AnyOutside(limited, boundary)) return "quest mode let the dash out";
        UserPlayArea.SetMode(room, UserPlayArea.Mode.Room);
        var whole = Place("dash", room, 6f);
        UserPlayArea.SetMode(room, UserPlayArea.Mode.Auto);
        if (!whole.Ok || whole.Area == null || whole.Area.Kind != PlayAreaPlan.Source.Room) return "room mode: " + whole.Message;
        float quest = limited.Slots["course.finish"][0].position.z, full = whole.Slots["course.finish"][0].position.z;
        if (full <= quest + 0.5f) return $"room mode should give a longer dash: {F(quest)} vs {F(full)} m";
        note = $"dash {F(quest)} m in the boundary, {F(full)} m on the whole floor";
        return null;
    }

    static string Drawn(out string note)
    {
        note = null;
        var room = MockFloorPlan.TurnedLivingRoom();
        var r = Quaternion.Euler(0f, room.Yaw, 0f);
        // A 3 x 2.4 m rectangle on the open side of the turned room, in the room's own frame, then to world.
        var local = new[] { new Vector3(0.2f, 0f, -0.6f), new Vector3(3.2f, 0f, -0.6f), new Vector3(3.2f, 0f, 1.8f), new Vector3(0.2f, 0f, 1.8f) };
        var drawn = new Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            var w = room.Origin + r * local[i];
            drawn[i] = new Vector2(w.x, w.z);
        }
        UserPlayArea.SetDrawn(room, drawn);
        UserPlayArea.Reload();  // from disk, as on the next launch
        var back = UserPlayArea.Drawn(room);
        if (back == null || back.Length != 4) return "the drawn area didn't come back";
        for (int i = 0; i < 4; i++)
            if ((back[i] - drawn[i]).magnitude > 0.005f) return $"corner {i} moved by {F((back[i] - drawn[i]).magnitude)} m";
        if (UserPlayArea.ModeOf(room) != UserPlayArea.Mode.Draw) return "drawing didn't choose draw mode";
        var placed = Place("park", room);
        if (!placed.Ok) return placed.Code + ": " + placed.Message;
        if (placed.Area == null || placed.Area.Kind != PlayAreaPlan.Source.Drawn) return "not fitted in the drawn area";
        string inside = AllInside(placed, drawn, 0f);
        if (inside != null) return inside;
        UserPlayArea.SetMode(room, UserPlayArea.Mode.Auto);
        if (UserPlayArea.Drawn(room) == null) return "choosing auto threw the drawn outline away";
        note = "park inside the 7.2 m² outline; the outline round-trips through the file in a 30° room";
        return null;
    }

    static string HoopsRefused(out string note)
    {
        note = null;
        var room = MockFloorPlan.LivingRoom();
        UserPlayArea.SetDrawn(room, Square(1.5f, 0.5f, 0.6f));
        var placed = Place("hoop_course", room);
        UserPlayArea.SetMode(room, UserPlayArea.Mode.Auto);
        if (placed.Ok) return "hoops fitted in a 1.44 m² area";
        if (placed.Code != "no_free_floor" || !placed.CanDraw || !placed.CanUseRoom) return $"{placed.Code}: buttons draw {placed.CanDraw}, room {placed.CanUseRoom}";
        if (placed.Message == null || !placed.Message.Contains("play area you drew") || placed.Message.Contains("Redraw")) return "message: " + placed.Message;
        if (ArtifactGenerators.DeskCapable(new JObject { ["type"] = "room_hoops" })) return "hoops counted as desk-capable";
        note = "\"" + placed.Message + "\"";
        return null;
    }

    static string BoundaryChanged(out string note)
    {
        note = null;
        var room = MockFloorPlan.Loft();
        UserPlayArea.SetMode(room, UserPlayArea.Mode.Room);
        Course("slalom", out var manifest, out var generator, out var inputs);
        var first = ArtifactGenerators.Place(manifest, generator, inputs, null, room, false);
        if (!first.Ok) return "first: " + first.Message;
        var rerun = ArtifactGenerators.Place(manifest, generator, inputs, null, room, false);
        if (!rerun.Ok || !rerun.Reused) return "the rerun didn't reuse the layout";
        // An area on one side of the loft, clear of where the first course stood.
        float side = first.Origin.position.x > 0f ? -1f : 1f;
        var area = new[] { new Vector2(side * 0.3f, -2.9f), new Vector2(side * 3.9f, -2.9f), new Vector2(side * 3.9f, 2.9f), new Vector2(side * 0.3f, 2.9f) };
        if (side < 0f) Array.Reverse(area);
        UserPlayArea.SetDrawn(room, area);
        var remade = ArtifactGenerators.Place(manifest, generator, inputs, null, room, false);
        UserPlayArea.SetMode(room, UserPlayArea.Mode.Auto);
        if (!remade.Ok || remade.Reused) return "should be remade: " + remade.Message;
        if (remade.Message == null || !remade.Message.Contains("boundary has changed")) return "message: " + remade.Message;
        string inside = AllInside(remade, area, 0f);
        if (inside != null) return inside;
        note = "\"" + remade.Message + "\"";
        return null;
    }

    static string NoFloorDesk(out string note)
    {
        note = null;
        // A 2 x 1.6 m study, mostly desk: no clear strip for any floor course, a 1.4 x 0.7 m desk to put one on.
        var study = new MockFloorPlan("study", new Vector2(2.0f, 1.6f)).Add("TABLE", new Vector2(0f, -0.35f), new Vector3(1.4f, 0.74f, 0.7f));
        var dash = Place("dash", study);
        if (!dash.Ok || !dash.DeskFallback) return "dash: " + dash.Code + " " + dash.Message;
        if (Mathf.Abs(dash.Origin.position.y - 0.74f) > 0.002f) return "dash not on the desk top: y " + F(dash.Origin.position.y);
        if (dash.Message == null || !dash.Message.StartsWith("There isn't enough clear floor for it here, so I put the dash (drag race) on your desk at 1:")) return "dash message: " + dash.Message;
        var slalom = Place("slalom", study);
        if (!slalom.Ok || !slalom.DeskFallback) return "slalom: " + slalom.Code + " " + slalom.Message;
        var park = Place("park", study);
        if (!park.Ok || !park.DeskFallback) return "park: " + park.Code + " " + park.Message;
        var closet = Place("dash", MockFloorPlan.Closet());  // no table: refused, and says how a desk would help
        if (closet.Ok || closet.Code != "no_free_floor" || !closet.Message.Contains("Space Setup")) return "closet: " + closet.Code + " " + closet.Message;
        note = $"dash at {DeskPlan.RatioWords(dash.Scale)}, slalom at {DeskPlan.RatioWords(slalom.Scale)}, park at {DeskPlan.RatioWords(park.Scale)}: \"{dash.Message}\"";
        return null;
    }
}
