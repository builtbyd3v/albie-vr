using System;
using System.Collections.Generic;
using System.Text;
using Forge.Controllers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Headless checks for the learned controller and the Autopilot (PROJECT_CONTEXT 16.8, ML A Nov 10): learned files run
/// through the host exactly as trained, bad files are refused with the contract's codes, max_speed makes the Autopilot
/// faster (15.6 step 9), and the Autopilot moves past a failing candidate to one that drives the course.
/// Forge > Controllers > Check Autopilot logs the report; <see cref="Run"/> returns it (for `unity command eval`).
/// </summary>
public static class AutopilotCheck
{
    const float Dt = 0.02f;

    [MenuItem("Forge/Controllers/Check Autopilot")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        bool ok = true;
        var laps = BehaviourCloningCheck.DriveLaps(300);
        var cloners = new BehaviourCloner[3];
        for (int i = 0; i < 3; i++) { cloners[i] = new BehaviourCloner(laps, new BehaviourCloner.Options { Seed = i + 1 }); cloners[i].TrainToEnd(); }
        ok &= Check(sb, "learned file matches the trainer", (out string note) => MatchesTrainer(cloners[0], out note));
        ok &= Check(sb, "mock autopilot drives the slalom", MockAutopilotDrives);
        ok &= Check(sb, "max_speed up drives faster", (out string note) => MaxSpeedDrivesFaster(cloners, out note));
        ok &= Check(sb, "bad learned files refused", BadFilesRefused);
        ok &= Check(sb, "autopilot skips a failing candidate", (out string note) => AutopilotSkipsFailure(cloners[0], out note));
        ok &= Check(sb, "autopilot reports when none is left", NoneLeft);
        ok &= Check(sb, "no allocations per step", (out string note) => NoAllocations(cloners[0], out note));
        return (ok ? "PASS" : "FAIL") + " autopilot check\n" + sb;
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

    static ControllerFile Parse(string json)
    {
        var file = ControllerFile.Parse(json, out string error);
        if (file == null) throw new InvalidOperationException(error);
        return file;
    }

    /// <summary>Drives one run through the host (governor included) until the finish, a crash or 30 s.</summary>
    static MockTruckSim Drive(ControllerHost host, float y0 = 0f, float heading = 0f, Action<MockTruckSim> each = null)
    {
        var sim = new MockTruckSim(y0, heading);
        for (int i = 0; i < 1500 && !sim.Crashed && !sim.Finished; i++)
        {
            sim.Sense(host.IO.Sensors);
            host.Step(Dt, sim.Speed);
            sim.Step(host.IO.Actuators, Dt);
            each?.Invoke(sim);
        }
        return sim;
    }

    static int Finishes(ControllerHost host)
    {
        int n = 0;
        foreach (float y0 in new[] { 0f, 0.1f, -0.1f, 0.3f, -0.3f })
            foreach (float heading in new[] { 0f, 0.1f, -0.1f })
            {
                host.ResetState();
                if (Drive(host, y0, heading).Finished) n++;
            }
        return n;
    }

    // Trainer → controller file → LearnedController must give the trainer's own outputs (5 decimals in the file).
    static string MatchesTrainer(BehaviourCloner cloner, out string note)
    {
        var host = NewHost();
        string error = host.Attach(Parse(cloner.ToControllerJson("truck_autopilot", 1, 1.5f)));
        if (error != null) { note = null; return error; }
        var sim = new MockTruckSim();
        var expected = new float[2];
        float worst = 0;
        for (int i = 0; i < 400 && !sim.Crashed && !sim.Finished; i++)
        {
            sim.Sense(host.IO.Sensors);
            cloner.Predict(host.IO.Sensors, expected);
            host.Step(Dt, 0f);   // no governor here: compare the raw outputs
            worst = Math.Max(worst, Math.Max(Math.Abs(host.IO.Actuators[0] - expected[0]), Math.Abs(host.IO.Actuators[1] - expected[1])));
            sim.Step(host.IO.Actuators, Dt);
        }
        note = $"worst difference {worst:0.000000} over a lap's steps";
        return worst < 1e-3f ? null : note;
    }

    static string MockAutopilotDrives(out string note)
    {
        var asset = Resources.Load<TextAsset>("Mocks/controllers/truck_autopilot");
        if (asset == null) { note = null; return "missing Resources/Mocks/controllers/truck_autopilot.json"; }
        var host = NewHost();
        string error = host.Attach(Parse(asset.text));
        if (error != null) { note = null; return error; }
        var centre = Drive(host);
        int starts = Finishes(host);
        note = $"from centre: {(centre.Finished ? "finished" : "crashed")} in {centre.Time:0.0} s; {starts}/15 starts";
        return centre.Finished ? null : note;   // the generator picked it to finish from centre
    }

    // Demo step 9: "turn max_speed up and it drives faster". Learned throttle sits near full, so the governor sets the pace.
    static string MaxSpeedDrivesFaster(BehaviourCloner[] cloners, out string note)
    {
        foreach (var cloner in cloners)
        {
            var host = NewHost();
            host.Attach(Parse(cloner.ToControllerJson("truck_autopilot", 1, 1.0f)));
            if (!Drive(host).Finished) continue;   // use a candidate that drives the course
            var slow = Drive(host);
            float slowTop = 0; Drive(host, each: s => slowTop = Math.Max(slowTop, s.Speed));
            string error = host.SetTunable("max_speed", 1.6f);
            if (error != null) { note = null; return error; }
            host.ResetState();
            var fast = Drive(host);
            float fastTop = 0; Drive(host, each: s => fastTop = Math.Max(fastTop, s.Speed));
            note = $"seed {cloner.Settings.Seed}: max_speed 1.0 → top {slowTop:0.00} m/s, lap {slow.Time:0.0} s; 1.6 → top {fastTop:0.00} m/s, lap {(fast.Finished ? fast.Time.ToString("0.0") + " s" : "crashed")}";
            return fastTop > slowTop + 0.2f ? null : note;
        }
        note = null;
        return "no candidate finished the course at max_speed 1.0";
    }

    static string BadFilesRefused(out string note)
    {
        var good = JObject.Parse(Resources.Load<TextAsset>("Mocks/controllers/truck_autopilot").text);
        var cases = new (string what, Func<JObject, JObject> edit, string code)[]
        {
            ("unknown sensor", f => { f["inputs"][3]["channel"] = "ray_front.d9"; return f; }, "unknown_channel"),
            ("unknown actuator", f => { f["outputs"][1]["actuator"] = "rotor.thrust"; return f; }, "unknown_channel"),
            ("missing weight row", f => { ((JArray)f["net"]["weights"][0]).RemoveAt(0); return f; }, "shape_mismatch"),
            ("short weight row", f => { ((JArray)f["net"]["weights"][1][0]).RemoveAt(0); return f; }, "shape_mismatch"),
            ("sizes vs inputs", f => { f["net"]["sizes"][0] = 9; return f; }, "shape_mismatch"),
            ("too big", f => { f["net"]["sizes"][1] = 64; return f; }, "too_big"),
            ("NaN weight", f => { f["net"]["weights"][0][0][0] = double.NaN; return f; }, "bad_value"),
            ("unsupported output activation", f => { f["net"]["output_activation"] = "linear"; return f; }, "bad_value"),
        };
        string first = null;
        foreach (var (what, edit, code) in cases)
        {
            var json = edit((JObject)good.DeepClone()).ToString();
            var file = ControllerFile.Parse(json, out string error);
            if (file != null) error = NewHost().Attach(file);
            if (error == null || !error.StartsWith(code + ":")) { note = null; return $"{what}: expected {code}, got {error ?? "no error"}"; }
            first = first ?? error;
        }
        note = $"{cases.Length} bad files refused, e.g. \"{first}\"";
        return null;
    }

    // A candidate whose net drives straight (all weights 0, throttle bias up) crashes into cone 0; a refused file is
    // skipped at once; the trained candidate then drives the course and is kept.
    static string AutopilotSkipsFailure(BehaviourCloner good, out string note)
    {
        var broken = JObject.Parse(good.ToControllerJson("truck_autopilot", 1, 1.5f));
        foreach (JArray layer in (JArray)broken["net"]["weights"]) foreach (JArray row in layer) for (int k = 0; k < row.Count; k++) row[k] = 0;
        foreach (JArray layer in (JArray)broken["net"]["biases"]) for (int j = 0; j < layer.Count; j++) layer[j] = 0;
        ((JArray)broken["net"]["biases"]).Last[1] = 2.0;   // throttle ≈ tanh(2) = 0.96, steer 0: straight into the first cone
        var refused = (JObject)broken.DeepClone();
        refused["inputs"][0]["channel"] = "lidar.d0";

        var host = NewHost();
        var autopilot = new Autopilot(new[] { Parse(broken.ToString()), Parse(refused.ToString()), Parse(good.ToControllerJson("truck_autopilot", 3, 1.5f)) });
        var log = new List<string>();
        if (autopilot.Engage(host) is string engageError) { note = null; return engageError; }
        for (int run = 0; run < 3 && !autopilot.Kept && !autopilot.Exhausted; run++)
        {
            host.ResetState();
            var sim = Drive(host);
            log.Add(autopilot.RunEnded(host, sim.Finished));
        }
        note = string.Join(" → ", log) + (autopilot.Skipped.Count > 0 ? $" (skipped: {autopilot.Skipped[0]})" : "");
        if (!autopilot.Kept || autopilot.Index != 2) return note;
        return autopilot.Skipped.Count == 1 && autopilot.Skipped[0].Contains("unknown_channel") ? null : note;
    }

    static string NoneLeft(out string note)
    {
        var host = NewHost();
        var file = Parse(Resources.Load<TextAsset>("Mocks/controllers/truck_autopilot").text);
        var autopilot = new Autopilot(new[] { file });
        autopilot.Engage(host);
        note = autopilot.RunEnded(host, false);
        if (!autopilot.Exhausted) return "should be exhausted after its only candidate failed";
        if (new Autopilot(new ControllerFile[0]).Engage(host) == null) return "an empty autopilot must not engage";
        return note.Contains("pretrained fallback") ? null : note;
    }

    static string NoAllocations(BehaviourCloner cloner, out string note)
    {
        var host = NewHost();
        host.Attach(Parse(cloner.ToControllerJson("truck_autopilot", 1, 1.5f)));
        var sim = new MockTruckSim();
        for (int i = 0; i < 10; i++) { sim.Sense(host.IO.Sensors); host.Step(Dt, sim.Speed); sim.Step(host.IO.Actuators, Dt); }
        long bytes = AllocationCounter.BytesDuring(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                if (sim.Crashed || sim.Finished) sim.Reset();
                sim.Sense(host.IO.Sensors); host.Step(Dt, sim.Speed); sim.Step(host.IO.Actuators, Dt);
            }
        });
        note = $"{bytes} bytes over 1000 learned steps";
        return bytes == 0 ? null : note;
    }
}
