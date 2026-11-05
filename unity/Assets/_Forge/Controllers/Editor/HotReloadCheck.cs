using System;
using System.IO;
using System.Text;
using Forge.Controllers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Headless checks for ML A's Nov 12 goals (PROJECT_CONTEXT 16.3, 16.8): hot reload (contract section 6), the file
/// watcher, a live max_speed edit on the Autopilot (15.6 step 9), the pretrained fallback, and the training record the
/// notebook gets (C-17). Forge > Controllers > Check Hot Reload logs the report; <see cref="Run"/> returns it.
/// </summary>
public static class HotReloadCheck
{
    const float Dt = 0.02f;

    [MenuItem("Forge/Controllers/Check Hot Reload")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        bool ok = true;
        ok &= Check(sb, "tunable edit keeps PID state", TunableEditKeepsState);
        ok &= Check(sb, "structure edit resets state", StructureEditResets);
        ok &= Check(sb, "bad files refused, old one keeps driving", BadReloadKeepsOld);
        ok &= Check(sb, "reload switches kind (graph → learned)", SwitchesKind);
        ok &= Check(sb, "file watcher", FileWatcher);
        ok &= Check(sb, "max_speed edited live on the Autopilot", LiveMaxSpeed);
        ok &= Check(sb, "pretrained fallback drives the slalom", FallbackDrives);
        ok &= Check(sb, "autopilot falls back after failed candidates", AutopilotFallsBack);
        ok &= Check(sb, "training record for the notebook", TrainingRecord);
        return (ok ? "PASS" : "FAIL") + " hot reload check\n" + sb;
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

    static ControllerHost NewHost() => new ControllerHost(new ControllerIO(MockTruckSim.SensorNames, MockTruckSim.ActuatorNames));
    static JObject Mock(string name) => JObject.Parse(Resources.Load<TextAsset>("Mocks/controllers/" + name).text);
    static ControllerFile Parse(JObject json) => ControllerFile.Parse(json.ToString(), out _);

    static void Steps(ControllerHost host, MockTruckSim sim, int n)
    {
        for (int i = 0; i < n && !sim.Crashed && !sim.Finished; i++) { sim.Sense(host.IO.Sensors); host.Step(Dt, sim.Speed); sim.Step(host.IO.Actuators, Dt); }
    }

    // Cruise for a second, then reload with only max_speed and the version changed: the PID output stays continuous.
    static string TunableEditKeepsState(out string note)
    {
        var host = NewHost();
        var json = Mock("truck_cruise");
        host.Attach(Parse(json));
        bool? kept = null;
        host.Reloaded += (f, k) => kept = k;
        var sim = new MockTruckSim(0.1f, 0f);
        Steps(host, sim, 50);
        sim.Sense(host.IO.Sensors);
        var sensors = (float[])host.IO.Sensors.Clone();
        host.Step(Dt, sim.Speed);
        float before = host.IO.Actuators[1];
        json["version"] = 2; json["tunables"]["max_speed"]["value"] = 1.6;
        string error = host.Reload(json.ToString());
        if (error != null) { note = null; return error; }
        Array.Copy(sensors, host.IO.Sensors, sensors.Length);
        host.Step(Dt, sim.Speed);
        float after = host.IO.Actuators[1];
        note = $"throttle {before:0.0000} → {after:0.0000}, state kept: {kept}, limit {host.SpeedLimit:0.0} m/s, version {host.File.Version}";
        return kept == true && Math.Abs(after - before) < 0.02f && Math.Abs(host.SpeedLimit - 1.6f) < 1e-5f && host.File.Version == 2 ? null : note;
    }

    // Change a PID gain written as a number in the node: that's a structure change, so the integrator restarts.
    static string StructureEditResets(out string note)
    {
        var host = NewHost();
        var json = Mock("truck_cruise");
        host.Attach(Parse(json));
        bool? kept = null;
        host.Reloaded += (f, k) => kept = k;
        var sim = new MockTruckSim(0.1f, 0f);
        Steps(host, sim, 50);
        sim.Sense(host.IO.Sensors);
        var sensors = (float[])host.IO.Sensors.Clone();
        json["nodes"][0]["kp"] = 5.0;
        string error = host.Reload(json.ToString());
        if (error != null) { note = null; return error; }
        Array.Copy(sensors, host.IO.Sensors, sensors.Length);
        host.Step(Dt, sim.Speed);
        float reloaded = host.IO.Actuators[1];
        var fresh = NewHost();
        fresh.Attach(Parse(json));
        Array.Copy(sensors, fresh.IO.Sensors, sensors.Length);
        fresh.Step(Dt, sim.Speed);
        note = $"state kept: {kept}; throttle {reloaded:0.0000}, a fresh controller gives {fresh.IO.Actuators[1]:0.0000}";
        return kept == false && Math.Abs(reloaded - fresh.IO.Actuators[1]) < 1e-6f ? null : note;
    }

    static string BadReloadKeepsOld(out string note)
    {
        var host = NewHost();
        host.Attach(Parse(Mock("truck_cruise")));
        string failed = null;
        host.ReloadFailed += r => failed = r;
        var bad = Mock("truck_cruise");
        bad["nodes"][1]["a"] = "ray_front.d9";
        string e1 = host.Reload(bad.ToString());
        string e2 = host.Reload("{ \"schema\": \"albie.controller/1\", ");   // a half-written file
        var sim = new MockTruckSim(0.1f, 0f);
        Steps(host, sim, 100);
        note = $"refused \"{e1}\" and a half-written file; still running {host.Current?.Name}, speed {sim.Speed:0.00} m/s";
        if (e1 == null || !e1.StartsWith("unknown_channel") || e2 == null) return note;
        if (failed == null || host.Fault != null) return "the reload failure should be reported without faulting the running controller";
        return host.Current?.Name == "truck_cruise" && sim.Speed > 0.8f ? null : note;
    }

    static string SwitchesKind(out string note)
    {
        var host = NewHost();
        host.Attach(Parse(Mock("truck_cruise")));
        string error = host.Reload(Mock("truck_autopilot").ToString());
        if (error != null) { note = null; return error; }
        var sim = new MockTruckSim();
        Steps(host, sim, 1500);
        note = $"now {host.Current.Kind} {host.Current.Name}: {(sim.Finished ? "finished" : "crashed")} in {sim.Time:0.0} s";
        return host.Current.Kind == "learned" && sim.Finished ? null : note;
    }

    static string FileWatcher(out string note)
    {
        string dir = Path.Combine(Path.GetTempPath(), "albie-hot-reload-check");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "truck_cruise.json");
        var json = Mock("truck_cruise");
        ControllerFileWatcher.WriteAtomic(path, json.ToString());
        var host = NewHost();
        host.Attach(Parse(json));
        var watcher = new ControllerFileWatcher(host, path);
        bool idle = watcher.Poll(0);   // nothing changed since it started watching

        json["version"] = 2; json["tunables"]["max_speed"]["value"] = 1.0;
        ControllerFileWatcher.WriteAtomic(path, json.ToString());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        bool early = watcher.Poll(0.1);   // before Interval: not checked yet
        bool reloaded = watcher.Poll(1.0);
        float limit = host.SpeedLimit; int version = host.File.Version;

        ControllerFileWatcher.WriteAtomic(path, "{ not json");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        watcher.Poll(2.0);
        string badError = watcher.LastError;
        Directory.Delete(dir, true);

        note = $"idle {idle}, early {early}, reloaded {reloaded} → v{version}, limit {limit:0.0} m/s; a broken save: \"{badError}\", still v{host.File.Version}";
        if (idle || early || !reloaded || version != 2 || Math.Abs(limit - 1f) > 1e-5f) return note;
        return badError != null && host.File.Version == 2 && watcher.Reloads == 1 ? null : note;
    }

    // Demo step 9 through the file: the Autopilot drives at max_speed 1.0, the file is saved with 1.6 mid-run, and the
    // same run speeds up without a stop.
    static string LiveMaxSpeed(out string note)
    {
        var json = Mock("truck_autopilot");
        json["tunables"]["max_speed"]["value"] = 1.0;
        var host = NewHost();
        host.Attach(Parse(json));
        var sim = new MockTruckSim();
        float topBefore = 0, topAfter = 0;
        for (int i = 0; i < 1500 && !sim.Crashed && !sim.Finished; i++)
        {
            if (i == 150)
            {
                json["version"] = 2; json["tunables"]["max_speed"]["value"] = 1.6;
                string error = host.Reload(json.ToString());
                if (error != null) { note = null; return error; }
            }
            sim.Sense(host.IO.Sensors); host.Step(Dt, sim.Speed); sim.Step(host.IO.Actuators, Dt);
            if (i < 150) topBefore = Math.Max(topBefore, sim.Speed); else topAfter = Math.Max(topAfter, sim.Speed);
        }
        note = $"top {topBefore:0.00} m/s at max_speed 1.0, then {topAfter:0.00} m/s after the save; {(sim.Finished ? "finished" : "crashed")} in {sim.Time:0.0} s";
        return topAfter > topBefore + 0.1f ? null : note;
    }

    static string FallbackDrives(out string note)
    {
        var asset = Resources.Load<TextAsset>(PretrainedFallbackBuilder.ResourcePath);
        if (asset == null) { note = null; return $"missing Resources/{PretrainedFallbackBuilder.ResourcePath}.json (Forge > Controllers > Build Pretrained Fallback)"; }
        var file = ControllerFile.Parse(asset.text, out string error);
        if (file == null) { note = null; return error; }
        var host = NewHost();
        error = host.Attach(file);
        if (error != null) { note = null; return error; }
        int finished = 0;
        foreach (float y0 in new[] { 0f, 0.1f, -0.1f, 0.3f, -0.3f })
            foreach (float heading in new[] { 0f, 0.1f, -0.1f })
            {
                var sim = new MockTruckSim(y0, heading);
                Steps(host, sim, 1500);
                if (sim.Finished) finished++;
            }
        note = $"{file.Name}: {finished}/15 starts, {(string)file.Raw["training"]?["sim_check"]}";
        return finished == 15 ? null : note;
    }

    // Two candidates that drive straight into cone 0, then the fallback.
    static string AutopilotFallsBack(out string note)
    {
        var broken = Mock("truck_autopilot");
        foreach (JArray layer in (JArray)broken["net"]["weights"]) foreach (JArray row in layer) for (int k = 0; k < row.Count; k++) row[k] = 0;
        foreach (JArray layer in (JArray)broken["net"]["biases"]) for (int j = 0; j < layer.Count; j++) layer[j] = 0;
        ((JArray)broken["net"]["biases"]).Last[1] = 2.0;
        var fallback = ControllerFile.Parse(Resources.Load<TextAsset>(PretrainedFallbackBuilder.ResourcePath).text, out _);
        var host = NewHost();
        var autopilot = new Autopilot(new[] { Parse(broken), Parse(broken) }, fallback);
        autopilot.Engage(host);
        var log = new System.Collections.Generic.List<string>();
        for (int run = 0; run < 4 && !autopilot.Kept && !autopilot.Exhausted; run++)
        {
            var sim = new MockTruckSim();
            host.ResetState();
            Steps(host, sim, 1500);
            log.Add(autopilot.RunEnded(host, sim.Finished));
        }
        note = string.Join(" → ", log);
        return autopilot.Kept && autopilot.UsingFallback && autopilot.Count == 2 ? null : note;
    }

    static string TrainingRecord(out string note)
    {
        var cloner = new BehaviourCloner(BehaviourCloningCheck.DriveLaps(300));
        var early = cloner.TrainingBlock();
        cloner.TrainToEnd();
        (string controller, int version, JObject training)? got = null;
        void Handler(string c, int v, JObject t) => got = (c, v, t);
        TrainingLog.Finished += Handler;
        try { TrainingLog.Report("truck_autopilot", 3, cloner.TrainingBlock()); }
        finally { TrainingLog.Finished -= Handler; }
        if (got == null) { note = null; return "TrainingLog.Finished wasn't raised"; }
        var t = got.Value.training;
        note = $"{got.Value.controller} v{got.Value.version}: {t.ToString(Newtonsoft.Json.Formatting.None)}";
        if (early["val_loss"].Type != JTokenType.Null) return "before the first epoch the losses must be null, not NaN";
        if ((int)t["epochs"] != 120 || (int)t["samples"] < 800 || t["val_loss"].Type != JTokenType.Float) return note;
        var inFile = JObject.Parse(cloner.ToControllerJson("truck_autopilot", 3, 1.5f))["training"];
        return JToken.DeepEquals(inFile, t) ? null : "the controller file's training block differs from the notebook's";
    }
}
