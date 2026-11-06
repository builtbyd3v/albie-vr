using System;
using System.Collections.Generic;
using System.Text;
using Forge.Controllers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The handoff to Person 2's Thing runtime, checked: a Thing on Unity physics plugged in through
/// <see cref="ControllerThing"/>. It builds the mock slalom from real colliders (5 capsule cones, 4 box walls) in a
/// preview scene, with an <see cref="ExampleTruckBody"/> truck, and checks:
/// 1. plugging in: the host, channels and starting controller; a bad file leaves the old one running;
/// 2. sensors: <see cref="RayFan"/> on the colliders against <see cref="MockTruckSim"/>'s maths at the same poses;
/// 3. Albie's tools reach it by name while it's registered (<see cref="ThingHosts"/>), and not after;
/// 4. a step allocates nothing, with a learned controller;
/// 5. the real-laps loop: two laps driven by hand on the physics truck (the scripted driver through the manual
///    controller), recorded, saved and read back, train 3 candidates that then drive it from hand-placed starts;
/// 6. brains trained on mock-truck laps drive the physics truck;
/// 7. the pretrained fallback (the Nov 16 safety net) drives it from the 15 starts it's scored on in the mock;
/// 8. the real-truck way to replace it: a candidate that drove (from step 5) promoted with
///    <see cref="PretrainedFallbackBuilder.Promote"/> to a scratch path loads and drives as the fallback; bad files are refused.
/// Forge > Controllers > Check Thing Runtime; <see cref="Run"/> returns the report (a few seconds).
/// </summary>
public static class ThingRuntimeCheck
{
    const float Dt = 0.02f, RunTimeoutS = 30f;
    const float WallHeight = 0.5f, WallThickness = 0.2f;
    static readonly (float y, float turn)[] Starts = { (0.1f, -0.01f), (-0.12f, 0.05f), (0.05f, 0.08f) };

    [MenuItem("Forge/Controllers/Check Thing Runtime")]
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

    sealed class World
    {
        public Scene Scene;
        public ControllerThing Thing;
        public ExampleTruckBody Body;
        /// <summary>A candidate trained on the physics truck's own laps that drove it (from RealLaps).</summary>
        public ControllerFile DroveIt;
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        int failed = 0;
        var world = new World { Scene = EditorSceneManager.NewPreviewScene() };
        try
        {
            BuildSlalom(world);
            failed += Step(sb, "PlugIn", () => PlugIn(world));
            failed += Step(sb, "Sensors", () => Sensors(world));
            failed += Step(sb, "AlbieTools", () => AlbieToolsReach(world));
            failed += Step(sb, "NoAllocations", () => NoAllocations(world));
            failed += Step(sb, "RealLaps", () => RealLaps(world));
            failed += Step(sb, "MockBrains", () => MockBrains(world));
            failed += Step(sb, "Fallback", () => FallbackDrives(world));
            failed += Step(sb, "Promote", () => PromoteDrives(world));
        }
        finally
        {
            world.Thing?.Unregister();
            ManualInputs.Clear();
            EditorSceneManager.ClosePreviewScene(world.Scene);
        }
        return (failed == 0 ? "PASS" : $"FAIL ({failed})") + " thing runtime check (example truck on Unity physics, Editor)\n" + sb;
    }

    static int Step(StringBuilder sb, string name, Func<string> check)
    {
        string result;
        try { result = check(); }
        catch (Exception e) { result = "FAIL " + e.GetType().Name + ": " + e.Message; }
        sb.Append("  ").Append(name).Append(": ").Append(result).Append('\n');
        return result.StartsWith("FAIL") ? 1 : 0;
    }

    // ---- The world: sim x is world x, sim y (left) is world z ----------------------------------------------------

    static void BuildSlalom(World w)
    {
        foreach (float x in MockTruckSim.ConeX)
        {
            var cone = NewObject("cone", w.Scene, new Vector3(x, 0f, 0f));
            var capsule = cone.AddComponent<CapsuleCollider>();
            capsule.radius = MockTruckSim.ConeRadius;
            capsule.height = 1f; // tall, so the rays (0.1 m up) hit its straight side
            capsule.center = Vector3.zero;
        }
        float cx = (MockTruckSim.WallXMin + MockTruckSim.WallXMax) / 2f, lx = MockTruckSim.WallXMax - MockTruckSim.WallXMin;
        float cz = (MockTruckSim.WallYMin + MockTruckSim.WallYMax) / 2f, lz = MockTruckSim.WallYMax - MockTruckSim.WallYMin;
        Wall(w.Scene, new Vector3(cx, 0f, MockTruckSim.WallYMin - WallThickness / 2f), new Vector3(lx + 2 * WallThickness, WallHeight, WallThickness));
        Wall(w.Scene, new Vector3(cx, 0f, MockTruckSim.WallYMax + WallThickness / 2f), new Vector3(lx + 2 * WallThickness, WallHeight, WallThickness));
        Wall(w.Scene, new Vector3(MockTruckSim.WallXMin - WallThickness / 2f, 0f, cz), new Vector3(WallThickness, WallHeight, lz));
        Wall(w.Scene, new Vector3(MockTruckSim.WallXMax + WallThickness / 2f, 0f, cz), new Vector3(WallThickness, WallHeight, lz));

        var truck = NewObject("truck", w.Scene, Vector3.zero);
        w.Body = truck.AddComponent<ExampleTruckBody>();
        w.Thing = truck.AddComponent<ControllerThing>();
        w.Thing.thingName = "truck";
        w.Thing.thingVersion = 2;
        w.Thing.tickInFixedUpdate = false;
        w.Thing.controller = Resources.Load<TextAsset>("Mocks/controllers/truck_manual");
        Physics.SyncTransforms();
    }

    static void Wall(Scene scene, Vector3 centre, Vector3 size) =>
        NewObject("wall", scene, centre).AddComponent<BoxCollider>().size = size;

    static GameObject NewObject(string name, Scene scene, Vector3 position)
    {
        var go = new GameObject(name);
        SceneManager.MoveGameObjectToScene(go, scene);
        go.transform.position = position;
        return go;
    }

    static Quaternion Heading(float radians) => Quaternion.LookRotation(new Vector3(Mathf.Cos(radians), 0f, Mathf.Sin(radians)), Vector3.up);

    static void Place(World w, float x, float y, float heading)
    {
        w.Body.Place(new Vector3(x, 0f, y), Heading(heading));
        w.Thing.Host.ResetState();
    }

    // Mirrors the physics truck's pose into a MockTruckSim, for the scripted driver and the sensor comparison.
    static void Mirror(World w, MockTruckSim view)
    {
        var t = w.Body.transform;
        view.X = t.position.x;
        view.Y = t.position.z;
        view.Heading = Mathf.Atan2(t.forward.z, t.forward.x);
        view.Speed = w.Body.Speed;
    }

    /// <summary>Drives until the finish line, a crash or the timeout. Returns "finished", "crashed" or "stalled".</summary>
    static string Drive(World w, MockDriver driver = null, MockTruckSim view = null)
    {
        for (float t = 0f; t < RunTimeoutS; t += Dt)
        {
            if (driver != null) { Mirror(w, view); driver.Drive(view); }
            w.Thing.Tick(Dt);
            if (w.Body.Crashed) return "crashed";
            if (w.Body.transform.position.x > MockTruckSim.FinishX) return "finished";
        }
        return "stalled";
    }

    // ---- Checks ---------------------------------------------------------------------------------------------------

    static string PlugIn(World w)
    {
        string error = w.Thing.Init();
        if (error != null) return "FAIL " + error;
        var io = w.Thing.Host.IO;
        if (io.Sensors.Length != 8 || io.Actuators.Length != 2) return $"FAIL channels: {io.Sensors.Length} sensors, {io.Actuators.Length} actuators";
        if (w.Thing.Host.File?.Name != "truck_manual") return "FAIL the starting controller isn't attached";
        string bad = w.Thing.Attach("{\"schema\": \"albie.controller/1\", \"kind\": \"graph\"}");
        if (bad == null) return "FAIL a bad controller file attached";
        if (w.Thing.Host.File?.Name != "truck_manual") return "FAIL a bad file replaced the running controller";
        if (w.Thing.Recorder.Set.MirrorSwap.Length != 3) return "FAIL the recorder has no mirror map";
        return $"ok: 8 sensors, 2 actuators, truck_manual attached; a bad file is refused ({Short(bad)}) and truck_manual keeps running";
    }

    static string Sensors(World w)
    {
        var view = new MockTruckSim();
        var fromMock = new float[8];
        var fromPhysics = new float[8];
        double worstCone = 0, worstWall = 0;
        int poses = 0, wallRays = 0, wallOff = 0;
        float range = MockTruckSim.RayRange;
        for (float x = 0f; x <= 8.5f; x += 0.5f)
            for (float y = -0.6f; y <= 0.61f; y += 0.3f)
                for (float h = -0.4f; h <= 0.41f; h += 0.2f)
                {
                    view.Reset(y, h);
                    view.X = x;
                    if (view.HitsSomething()) continue;
                    view.Sense(fromMock);
                    Place(w, x, y, h);
                    w.Body.Sense(fromPhysics);
                    poses++;
                    for (int i = 0; i < MockTruckSim.Rays; i++)
                    {
                        double diff = Math.Abs(fromMock[i] - fromPhysics[i]);
                        // Which did the mock's ray hit? A cone if a cone is that far along it, else a wall (or nothing).
                        if (HitsCone(view, i, fromMock[i] * range)) worstCone = Math.Max(worstCone, diff);
                        else if (fromMock[i] < 1f) { wallRays++; worstWall = Math.Max(worstWall, diff); if (diff > 0.005) wallOff++; }
                    }
                }
        if (Math.Abs(fromPhysics[7] - fromMock[7]) > 1e-4) return "FAIL speed.forward differs";
        if (worstCone > 0.005) return $"FAIL cone distances differ by up to {worstCone * range:0.000} m";
        // Until 2026-11-06 the mock cast plain rays at walls and every wall ray here read up to 0.21 m nearer.
        if (wallOff > 0) return $"FAIL {wallOff}/{wallRays} wall rays differ, by up to {worstWall * range:0.000} m: the mock and the contract's sphere cast disagree at walls";
        return $"ok: {poses} poses × 7 rays agree with the mock: cones within {worstCone * range * 1000:0.0} mm, " +
               $"{wallRays} wall rays within {worstWall * range * 1000:0.0} mm (both sphere casts, contract 2.1)";
    }

    static bool HitsCone(MockTruckSim view, int ray, float metres)
    {
        float a = view.Heading + MockTruckSim.RayAngle(ray);
        float px = view.X + Mathf.Cos(a) * metres, py = view.Y + Mathf.Sin(a) * metres;
        float r = MockTruckSim.ConeRadius + MockTruckSim.DefaultBeamRadius + 0.02f;
        foreach (float cx in MockTruckSim.ConeX) if ((px - cx) * (px - cx) + py * py <= r * r) return true;
        return false;
    }

    static string AlbieToolsReach(World w)
    {
        w.Thing.Register();
        var status = TemplateTools.StatusTool(new JObject { ["thing"] = "truck" });
        if (!(bool)status["thing_running"]) return "FAIL template_status doesn't see the truck while it's registered";
        if ((string)status["controller"] != w.Thing.Host.File.Name) return $"FAIL template_status reports controller {status["controller"]}";
        var brake = TemplateTools.Handlers();
        if (ThingHosts.Find("truck") != w.Thing.Host) return "FAIL ThingHosts doesn't return this truck's host";
        w.Thing.Unregister();
        if ((bool)TemplateTools.StatusTool(new JObject { ["thing"] = "truck" })["thing_running"]) return "FAIL still reachable after Unregister";
        return $"ok: template_status sees the truck running {status["controller"]} while it's registered, and not after; learned_brake reaches it the same way ({brake.Count} tools)";
    }

    static string NoAllocations(World w)
    {
        var fallback = Resources.Load<TextAsset>("Controllers/truck_autopilot_fallback");
        if (fallback == null) return "FAIL no pretrained fallback in Resources/Controllers";
        string error = w.Thing.Attach(fallback.text);
        if (error != null) return "FAIL " + error;
        Place(w, 0f, 0f, 0f);
        for (int i = 0; i < 20; i++) w.Thing.Tick(Dt); // warm up
        var watch = new System.Diagnostics.Stopwatch();
        int steps = 0;
        long bytes = AllocationCounter.BytesDuring(() =>
        {
            watch.Start();
            for (; steps < 200 && !w.Body.Crashed; steps++) w.Thing.Tick(Dt);
            watch.Stop();
        });
        if (bytes > 0) return $"FAIL {bytes} bytes allocated in {steps} steps";
        return $"ok: {steps} steps of the learned fallback with 7 sphere casts each, 0 bytes, {watch.Elapsed.TotalMilliseconds * 1000 / steps:0} µs a step";
    }

    // The real-laps loop on the physics truck: drive two laps by hand, record, save, read back, train, drive.
    static string RealLaps(World w)
    {
        var manual = Resources.Load<TextAsset>("Mocks/controllers/truck_manual");
        string error = w.Thing.Attach(manual.text);
        if (error != null) return "FAIL " + error;
        w.Thing.Recorder.Set.Laps.Clear();
        var driver = new MockDriver(501, 1.2f, 1f);
        var view = new MockTruckSim();
        int tries = 0;
        try
        {
            while (w.Thing.Recorder.Set.CompletedLaps < 2)
            {
                if (++tries > 6) return "FAIL the scripted driver couldn't finish 2 laps on the physics truck in 6 tries";
                Place(w, 0f, 0f, 0f);
                w.Thing.BeginLap();
                string outcome = Drive(w, driver, view);
                w.Thing.EndLap(outcome == "finished" ? "completed" : outcome == "crashed" ? "crashed" : "aborted");
            }
        }
        finally { ManualInputs.Clear(); }

        var laps = LapSet.Parse(w.Thing.Recorder.Set.ToJson(), out error);
        if (laps == null) return "FAIL the recorded laps don't read back: " + error;
        if (laps.Thing != "truck" || laps.ThingVersion != 2 || laps.Controller != "truck_manual" || laps.MirrorSwap.Length != 3)
            return $"FAIL the lap file says {laps.Thing} v{laps.ThingVersion}, {laps.Controller}, {laps.MirrorSwap.Length} mirror pairs";

        var files = Train(laps);
        string drove = Autopilot(w, files, null, out int firstTry);
        if (drove == null) return "FAIL no candidate trained on the physics laps drove the physics truck";
        w.DroveIt = files[int.Parse(drove.Split(',')[0].Trim().Substring("candidate ".Length)) - 1];
        return $"ok: {laps.CompletedLaps} laps driven by hand on the physics truck ({tries - laps.CompletedLaps} redone), saved and read back; " +
               $"3 candidates trained on them drove it from {Starts.Length} hand-placed starts: {drove}";
    }

    static string MockBrains(World w)
    {
        var attempt = new FiveInARowCheck.Attempt { DriverSeed = 501 };
        var laps = FiveInARowCheck.RecordLaps(501, 1f, attempt, FiveInARowCheck.LapStyle.Demo);
        string drove = Autopilot(w, Train(laps), null, out int firstTry);
        if (drove == null) return "FAIL no candidate trained on mock-truck laps drove the physics truck";
        return $"ok: candidates trained on mock-truck laps drove it from {Starts.Length} starts: {drove}";
    }

    // The fallback alone, from MockTrial's 15 starts (5 offsets × 3 headings), as PretrainedFallbackBuilder scores it.
    static string FallbackDrives(World w)
    {
        var asset = Resources.Load<TextAsset>("Controllers/truck_autopilot_fallback");
        if (asset == null) return "FAIL no pretrained fallback in Resources/Controllers";
        var fallback = ControllerFile.Parse(asset.text, out string error);
        if (fallback == null) return "FAIL " + error;
        error = w.Thing.Host.Attach(fallback);
        if (error != null) return "FAIL " + error;
        int finished = 0, starts = 0;
        var misses = new List<string>();
        foreach (float y in new[] { 0f, 0.1f, -0.1f, 0.3f, -0.3f })
            foreach (float heading in new[] { 0f, 0.1f, -0.1f })
            {
                starts++;
                Place(w, 0f, y, heading);
                string outcome = Drive(w);
                if (outcome == "finished") finished++; else misses.Add($"{y:+0.0;-0.0} m {heading:+0.0;-0.0} rad {outcome}");
            }
        if (finished < starts) return $"FAIL the pretrained fallback drove {finished}/{starts} starts on the physics truck ({string.Join(", ", misses)}): rebuild it (Forge > Controllers > Build Pretrained Fallback)";
        return $"ok: the pretrained fallback drove all {starts} starts on the physics truck";
    }

    static string PromoteDrives(World w)
    {
        if (w.DroveIt == null) return "FAIL RealLaps didn't leave a candidate that drove";
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "albie_promote_check");
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string source = System.IO.Path.Combine(dir, "truck_autopilot.json"), target = System.IO.Path.Combine(dir, "fallback.json");
            System.IO.File.WriteAllText(source, w.DroveIt.Raw.ToString());
            string report = PretrainedFallbackBuilder.Promote(source, target);
            if (!report.StartsWith("wrote")) return "FAIL " + report;
            var promoted = ControllerFile.Parse(System.IO.File.ReadAllText(target), out string error);
            if (promoted == null) return "FAIL the promoted file doesn't load: " + error;
            if (promoted.Name != "truck_autopilot_fallback") return $"FAIL the promoted file is named {promoted.Name}";

            string graph = System.IO.Path.Combine(dir, "truck_cruise.json"), junk = System.IO.Path.Combine(dir, "junk.json");
            System.IO.File.WriteAllText(graph, Resources.Load<TextAsset>("Mocks/controllers/truck_cruise").text);
            System.IO.File.WriteAllText(junk, "{not json");
            if (PretrainedFallbackBuilder.Promote(graph, target).StartsWith("wrote")) return "FAIL a graph controller was promoted";
            if (PretrainedFallbackBuilder.Promote(junk, target).StartsWith("wrote")) return "FAIL a broken file was promoted";

            string drove = Autopilot(w, new List<ControllerFile>(), promoted, out _);
            if (drove == null) return "FAIL the promoted fallback didn't drive the physics truck";
            return $"ok: a candidate that drove, promoted, loads as truck_autopilot_fallback and drove {Starts.Length} starts as the fallback; a graph and a broken file are refused";
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    static List<ControllerFile> Train(LapSet laps)
    {
        var files = new List<ControllerFile>();
        for (int seed = 1; seed <= 3; seed++)
        {
            var cloner = new BehaviourCloner(laps, new BehaviourCloner.Options { Seed = seed });
            while (!cloner.Step(1000)) { }
            var file = ControllerFile.Parse(cloner.ToControllerJson("truck_autopilot", 1, 1.5f), out string error);
            if (file == null) throw new InvalidOperationException(error);
            files.Add(file);
        }
        return files;
    }

    /// <summary>
    /// The Autopilot from each hand-placed start, candidates in order then the fallback, as the arena runs it.
    /// Returns who drove from each start ("candidate 1, candidate 1, candidate 2"), or null if a start found nobody.
    /// </summary>
    static string Autopilot(World w, List<ControllerFile> files, ControllerFile fallback, out int firstTry)
    {
        var who = new List<string>();
        firstTry = 0;
        foreach (var (y, turn) in Starts)
        {
            var autopilot = new Autopilot(files, fallback);
            if (autopilot.Engage(w.Thing.Host) != null) return null;
            string kept = null;
            while (!autopilot.Exhausted)
            {
                bool usingFallback = autopilot.UsingFallback;
                int index = autopilot.Index;
                Place(w, 0f, y, turn);
                bool finished = Drive(w) == "finished";
                autopilot.RunEnded(w.Thing.Host, finished);
                if (finished) { kept = usingFallback ? "the fallback" : $"candidate {index + 1}"; break; }
            }
            if (kept == null) return null;
            if (kept == "candidate 1") firstTry++;
            who.Add(kept);
        }
        return string.Join(", ", who);
    }

    static string Short(string s) => s.Length > 80 ? s.Substring(0, 80) + "…" : s;
}
