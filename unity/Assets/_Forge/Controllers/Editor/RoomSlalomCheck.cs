using System;
using System.Collections.Generic;
using System.Text;
using Forge.Artifacts;
using Forge.Controllers;
using UnityEditor;
using UnityEngine;

/// <summary>
/// ML A on Person 5's room slalom (PROJECT_CONTEXT 16.8, "room slalom, part 2"): the demo's brain beat on courses the
/// <c>floor_slalom</c> generator lays out in rooms, not the mock corridor.
/// 1. Geometry: the mock truck senses a room course the way a sphere-cast ray fan would (the corridor rebuilt as a
///    course reads exactly like the built-in one; plain rays agree with the room's own free-floor test).
/// 2. <c>course.bearing</c> points at the next gate in order, then the finish (C-19 2.1, proposed), on rooms and the corridor.
/// 3. Scripted laps finish in every mock room.
/// 4. The demo loop in each mock room, as <see cref="FiveInARowCheck"/> runs it: two laps, three candidates, the
///    Autopilot from a hand-placed start, then the pretrained fallback. It passes when every attempt drove the course.
/// 5. Rooms nobody tested (<see cref="RoomCourses.Random"/>): one attempt each, and the fallback alone.
/// Forge > Controllers > Check Room Slalom; <see cref="Run"/> returns the report (about a minute). Mock truck, Editor.
/// </summary>
public static class RoomSlalomCheck
{
    const int Drivers = 5, UnseenRooms = 20;
    static readonly float[] Offsets = { 0f, 0.1f, -0.1f }, Turns = { 0f, 0.08f, -0.08f };

    [MenuItem("Forge/Controllers/Check Room Slalom")]
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

    public static string Run()
    {
        var sb = new StringBuilder();
        bool ok = true;
        ok &= Check(sb, "the corridor rebuilt as a course senses exactly like the built-in one", CorridorAsCourse);
        ok &= Check(sb, "room courses: plain rays agree with the room's own walls and furniture", RoomsAgree);
        ok &= Check(sb, "course.bearing: the next gate in order, then the finish", Bearing);
        ok &= Check(sb, "scripted laps finish in every mock room", ScriptedLaps);
        ok &= Check(sb, "the demo loop drives every mock room", DemoLoop);
        ok &= Check(sb, "rooms nobody tested: the demo loop and the fallback", Unseen);
        ManualInputs.Clear();
        return (ok ? "PASS" : "FAIL") + " room slalom check (mock truck, Editor)\n" + sb;
    }

    static string CorridorAsCourse(out string note)
    {
        var corridor = new MockCourse { ConeRadius = MockTruckSim.ConeRadius };
        foreach (float x in MockTruckSim.ConeX) corridor.AddCone(x, 0f);
        float x0 = MockTruckSim.WallXMin, x1 = MockTruckSim.WallXMax, y0 = MockTruckSim.WallYMin, y1 = MockTruckSim.WallYMax;
        corridor.AddWall(x0, y0, x1, y0).AddWall(x1, y0, x1, y1).AddWall(x1, y1, x0, y1).AddWall(x0, y1, x0, y0);
        var a = new MockTruckSim();
        var b = new MockTruckSim(corridor);
        var sa = new float[MockTruckSim.SensorNames.Length];
        var sb = new float[MockTruckSim.SensorNames.Length];
        var rng = new System.Random(5);
        float worst = 0f;
        int poses = 0;
        for (int k = 0; k < 2000; k++)
        {
            a.X = b.X = (float)rng.NextDouble() * 9.5f - 0.3f;
            a.Y = b.Y = ((float)rng.NextDouble() * 2 - 1) * 1.3f;
            a.Heading = b.Heading = ((float)rng.NextDouble() * 2 - 1) * Mathf.PI;
            if (a.HitsSomething()) continue;
            poses++;
            a.Sense(sa); b.Sense(sb);
            for (int i = 0; i < MockTruckSim.Rays; i++) worst = Mathf.Max(worst, Mathf.Abs(sa[i] - sb[i]) * MockTruckSim.RayRange);
        }
        note = $"{poses} poses × {MockTruckSim.Rays} sphere-cast rays agree within {worst * 1000:0.0} mm";
        return worst > 1e-4f ? note : null;
    }

    // The course's walls against the room itself: march each plain ray in world space and ask room.IsFree.
    static string RoomsAgree(out string note)
    {
        note = null;
        float worst = 0f;
        int rays = 0, hits = 0;
        foreach (var (room, course) in RoomCourses.MockRooms())
        {
            var fit = FloorSlalom.Fit(room, RoomCourses.TemplateSettings());
            var rot = Quaternion.Euler(0f, fit.Yaw, 0f);
            Vector3 World(float x, float y) => fit.Origin + rot * new Vector3(-y, 0f, x);
            var walls = new MockCourse { Walls = course.Walls };
            var rng = new System.Random(3);
            for (int k = 0; k < 300; k++)
            {
                float x = (float)rng.NextDouble() * course.FinishX, y = ((float)rng.NextDouble() * 2 - 1) * 0.5f;
                if (!room.IsFree(World(x, y), 0.01f)) continue;
                float h = ((float)rng.NextDouble() * 2 - 1) * Mathf.PI, dx = Mathf.Cos(h), dy = Mathf.Sin(h);
                float cast = walls.Cast(x, y, dx, dy, 0f, MockTruckSim.RayRange);
                float t = 0f;
                for (; t < MockTruckSim.RayRange; t += 0.002f) if (!room.IsFree(World(x + dx * t, y + dy * t), 0f)) break;
                t = Mathf.Min(t, MockTruckSim.RayRange);
                rays++;
                if (t < MockTruckSim.RayRange) hits++;
                worst = Mathf.Max(worst, Mathf.Abs(cast - t));
            }
        }
        note = $"{rays} rays in {RoomCourses.MockRooms().Count} rooms ({hits} hit a wall or furniture) agree within {worst * 1000:0} mm (the test steps 2 mm)";
        return worst > 0.005f ? note : null;
    }

    static string Bearing(out string note)
    {
        note = null;
        var (_, course) = RoomCourses.MockRooms()[0];
        var sim = new MockTruckSim(course);
        var s = new float[MockTruckSim.SensorNames.Length];
        sim.Sense(s);
        float start = s[MockTruckSim.BearingIndex];
        // Gate 1 is on the right of cone 1 (FloorSlalom: odd gates on the course's +x), so it reads negative.
        if (course.Side(0) > 0 ? start <= 0f : start >= 0f) return $"from the start, course.bearing is {start:+0.00;-0.00}, but gate 1 is on the {(course.Side(0) > 0 ? "left" : "right")}";
        // Drive it by hand through gate 1: the target moves to gate 2, which is on the other side.
        sim.X = course.GateX[0]; sim.Y = course.GateY[0];
        sim.Step(new[] { 0f, 0f }, 0.02f);
        if (sim.GatesPassed != 1) return "standing in gate 1 didn't count it";
        sim.Sense(s);
        float next = s[MockTruckSim.BearingIndex];
        if (Mathf.Sign(next) == Mathf.Sign(start)) return $"after gate 1, course.bearing {next:+0.00;-0.00} still points to gate 1's side";
        // The corridor is a course too: its first gate is left of the first cone.
        var corridor = new MockTruckSim();
        corridor.Sense(s);
        float corridorStart = s[MockTruckSim.BearingIndex];
        if (corridorStart <= 0f) return $"on the corridor, course.bearing from the start is {corridorStart:+0.00;-0.00}, but its first gate is on the left";
        note = $"from the start {start:+0.00;-0.00} (gate 1), after gate 1 {next:+0.00;-0.00} (gate 2); on the corridor {corridorStart:+0.00;-0.00}";
        return null;
    }

    static string ScriptedLaps(out string note)
    {
        note = null;
        var host = new ControllerHost(new ControllerIO(MockTruckSim.SensorNames, MockTruckSim.ActuatorNames));
        host.Attach(ControllerFile.Parse(Resources.Load<TextAsset>("Mocks/controllers/truck_manual").text, out _));
        int done = 0, all = 0;
        var misses = new List<string>();
        foreach (var (room, course) in RoomCourses.MockRooms())
            for (int seed = 0; seed < 10; seed++)
            {
                var driver = MockDriver.ForRoom(seed);
                var sim = new MockTruckSim(course);
                host.ResetState();
                for (int i = 0; i < 2000 && !sim.Crashed && !sim.Finished; i++)
                {
                    sim.Sense(host.IO.Sensors); driver.Drive(sim); host.Step(0.02f, sim.Speed); sim.Step(host.IO.Actuators, 0.02f);
                }
                all++;
                if (sim.Finished) done++; else misses.Add($"{room.Id} {seed}");
            }
        ManualInputs.Clear();
        note = $"{done}/{all} laps (10 drivers × {all / 10} rooms){(misses.Count > 0 ? "; missed: " + string.Join(", ", misses) : "")}";
        return done < all * 0.95 ? note : null;
    }

    static string DemoLoop(out string note)
    {
        var fallback = FiveInARowCheck.LoadFallback();
        var sb = new StringBuilder();
        int live = 0, first = 0, fell = 0, none = 0, all = 0;
        foreach (var (room, course) in RoomCourses.MockRooms())
        {
            int rl = 0, rf = 0, rb = 0;
            for (int d = 0; d < Drivers; d++)
            {
                var a = FiveInARowCheck.Attempt1(501 + d, 1f, fallback, FiveInARowCheck.LapStyle.Demo, course);
                all++;
                if (a.Trained) { live++; rl++; }
                if (a.FirstTry) { first++; rf++; }
                if (a.Kept == 0) { fell++; rb++; }
                if (a.Kept < 0) none++;
            }
            int fb = FallbackStarts(fallback, course);
            sb.Append($"\n      {course.Name}: live {rl}/{Drivers} (candidate 1 {rf}), fallback needed {rb}; the fallback alone {fb}/{Offsets.Length * Turns.Length} starts");
        }
        note = $"{live}/{all} live ({first} on candidate 1), {fell} by the fallback, {none} not at all" + sb;
        return none > 0 ? note : null;
    }

    static string Unseen(out string note)
    {
        var fallback = FiveInARowCheck.LoadFallback();
        int live = 0, first = 0, fell = 0, none = 0, rooms = 0, fallbackAlone = 0;
        var misses = new List<string>();
        for (int seed = 5000; rooms < UnseenRooms; seed++)
        {
            var (room, course) = RoomCourses.Random(seed);
            if (course == null) continue;
            rooms++;
            var a = FiveInARowCheck.Attempt1(700 + rooms, 1f, fallback, FiveInARowCheck.LapStyle.Demo, course);
            if (a.Trained) live++;
            if (a.FirstTry) first++;
            if (a.Kept == 0) fell++;
            if (a.Kept < 0) { none++; misses.Add(course.Name); }
            if (Drives(fallback, course, 0f, 0f)) fallbackAlone++;
        }
        note = $"{rooms} random rooms: live {live}/{rooms} ({first} on candidate 1), fallback {fell}, nothing {none}; the fallback alone drove {fallbackAlone}/{rooms}" +
               (misses.Count > 0 ? " (missed: " + string.Join("; ", misses) + ")" : "");
        return none > 1 ? note : null;
    }

    static int FallbackStarts(ControllerFile fallback, MockCourse course)
    {
        int n = 0;
        foreach (float y0 in Offsets) foreach (float turn in Turns) if (Drives(fallback, course, y0, turn)) n++;
        return n;
    }

    static bool Drives(ControllerFile file, MockCourse course, float y0, float turn)
    {
        if (file == null) return false;
        var host = new ControllerHost(new ControllerIO(MockTruckSim.SensorNames, MockTruckSim.ActuatorNames));
        if (host.Attach(file) != null) return false;
        var sim = new MockTruckSim(course, y0, turn);
        while (!sim.Crashed && !sim.Finished && sim.Time < FiveInARowCheck.RunTimeoutS)
        {
            sim.Sense(host.IO.Sensors); host.Step(0.02f, sim.Speed); sim.Step(host.IO.Actuators, 0.02f);
        }
        return sim.Finished;
    }
}
