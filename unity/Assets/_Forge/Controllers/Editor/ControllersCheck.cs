using System;
using System.Text;
using Forge.Controllers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Headless checks for the controller runner (C-19, docs/CONTROLLER_CONTRACT.md) on the fake Thing (MockTruckSim),
/// with the mock files from hub/ml/make_controller_mocks.py. Forge > Controllers > Check Runner logs the report;
/// <see cref="Run"/> returns it (for `unity command eval`). PASS on the first line means every check passed.
/// </summary>
public static class ControllersCheck
{
    const float Dt = 0.02f;
    static readonly string[] Files = { "Mocks/controllers/truck_manual", "Mocks/controllers/truck_cruise", "Mocks/controllers/truck_autopilot" };

    [MenuItem("Forge/Controllers/Check Runner")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        bool ok = true;
        ok &= Check(sb, "mock files", MockFilesParse);
        ok &= Check(sb, "manual maths", ManualMaths);
        ok &= Check(sb, "speed governor", SpeedGovernor);
        ok &= Check(sb, "cruise on the fake truck", CruiseDrives);
        ok &= Check(sb, "validation errors", ValidationErrors);
        ok &= Check(sb, "failed swap keeps the old controller", FailedSwapKeepsOld);
        ok &= Check(sb, "fault handling", FaultHandling);
        ok &= Check(sb, "tunable change keeps PID state", TunableKeepsState);
        ok &= Check(sb, "no allocations per step", NoAllocations);
        ManualInputs.Clear();
        return (ok ? "PASS" : "FAIL") + " controller runner check\n" + sb;
    }

    // Each check returns null on success or why it failed, and may add detail to `note`.
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

    static ControllerIO NewIO() => new ControllerIO(MockTruckSim.SensorNames, MockTruckSim.ActuatorNames);

    static ControllerFile Load(int index)
    {
        var asset = Resources.Load<TextAsset>(Files[index]);
        if (asset == null) throw new InvalidOperationException($"missing Resources/{Files[index]}.json (run `python -m ml.make_controller_mocks` in hub/)");
        var file = ControllerFile.Parse(asset.text, out string error);
        if (file == null) throw new InvalidOperationException(error);
        return file;
    }

    static string MockFilesParse(out string note)
    {
        var host = new ControllerHost(NewIO());
        string manual = host.Attach(Load(0)), cruise = host.Attach(Load(1)), learned = host.Attach(Load(2));
        note = "manual, cruise and the learned autopilot all bind";
        if (manual != null) return "manual: " + manual;
        if (cruise != null) return "cruise: " + cruise;
        return learned == null ? null : "learned: " + learned;
    }

    // truck_manual: stick.y → drive.throttle (deadzone 0.05), hand.roll → steer.angle (deadzone 0.08, expo 0.3).
    static string ManualMaths(out string note)
    {
        var host = new ControllerHost(NewIO());
        string error = host.Attach(Load(0));
        if (error != null) { note = null; return error; }
        ManualInputs.Clear();
        ManualInputs.Set("stick.y", 1f); ManualInputs.Set("hand.roll", 0.5f);
        host.Step(Dt, 0f);
        float steer = host.IO.Actuators[0], throttle = host.IO.Actuators[1];
        float expected = Mathf.Pow((0.5f - 0.08f) / (1f - 0.08f), 1.3f);
        note = $"steer {steer:0.0000} (expected {expected:0.0000}), throttle {throttle:0.00}";
        if (Mathf.Abs(steer - expected) > 1e-4f) return note;
        if (Mathf.Abs(throttle - 1f) > 1e-5f) return note;
        ManualInputs.Set("stick.y", 0.04f); ManualInputs.Set("hand.roll", -1f);
        host.Step(Dt, 0f);
        if (host.IO.Actuators[1] != 0f) return "a stick inside the deadzone should give 0 throttle";
        if (Mathf.Abs(host.IO.Actuators[0] + 1f) > 1e-5f) return "a full right roll (-1) should give steer -1";
        return null;
    }

    // Full throttle under truck_manual (limit 2 m/s): the truck must not run away past the limit.
    static string SpeedGovernor(out string note)
    {
        var host = new ControllerHost(NewIO());
        string error = host.Attach(Load(0));
        if (error != null) { note = null; return error; }
        var sim = new MockTruckSim();
        ManualInputs.Clear();
        ManualInputs.Set("stick.y", 1f);
        float top = 0;
        for (int i = 0; i < 150; i++)   // 3 s, straight down the middle until the first cone
        {
            sim.Sense(host.IO.Sensors); host.Step(Dt, sim.Speed); sim.Step(host.IO.Actuators, Dt);
            top = Mathf.Max(top, sim.Speed);
            if (sim.X > 1.0f) break;
        }
        note = $"limit {host.SpeedLimit:0.0} m/s, top speed {top:0.00} m/s (truck top speed {MockTruckSim.TopSpeed:0.0})";
        return top <= host.SpeedLimit + 0.1f && top > host.SpeedLimit - 0.3f ? null : note;
    }

    // truck_cruise: PID to 1.2 m/s, steer away from the nearer side, brake when something is dead ahead. It must
    // finish the slalom from 15 starts, including dead centre (the sandbox's reset), where left and right rays tie.
    static string CruiseDrives(out string note)
    {
        ManualInputs.Clear();
        float speedAt2s = 0, slowest = 0;
        int finished = 0, runs = 0;
        string firstFailure = null;
        foreach (float y0 in new[] { 0f, 0.1f, -0.1f, 0.3f, -0.3f })
            foreach (float heading in new[] { 0f, 0.1f, -0.1f })
            {
                var host = new ControllerHost(NewIO());
                string error = host.Attach(Load(1));
                if (error != null) { note = null; return error; }
                var sim = new MockTruckSim(y0, heading);
                for (int i = 0; i < 1500 && !sim.Crashed && !sim.Finished; i++)
                {
                    sim.Sense(host.IO.Sensors); host.Step(Dt, sim.Speed); sim.Step(host.IO.Actuators, Dt);
                    if (i == 99 && y0 == 0 && heading == 0) speedAt2s = sim.Speed;
                }
                runs++;
                if (sim.Finished) { finished++; slowest = Mathf.Max(slowest, sim.Time); }
                else if (firstFailure == null)
                    firstFailure = $"start y {y0:+0.0;-0.0} m, heading {heading:+0.0;-0.0} rad: {(sim.Crashed ? "crashed" : "stuck")} at x {sim.X:0.00} m";
            }
        note = $"1.2 m/s target, {speedAt2s:0.00} m/s after 2 s from centre; finished {finished}/{runs} starts, slowest {slowest:0.0} s";
        if (firstFailure != null) return note + "; " + firstFailure;
        return Mathf.Abs(speedAt2s - 1.2f) < 0.2f ? null : note;
    }

    static string ValidationErrors(out string note)
    {
        note = null;
        var cases = new (string what, Func<JObject, JObject> edit, string code)[]
        {
            ("unknown sensor", f => { f["nodes"][1]["a"] = "ray_front.d9"; return f; }, "unknown_channel"),
            ("unknown actuator", f => { f["nodes"][0]["output"] = "rotor.thrust"; return f; }, "unknown_channel"),
            ("bad node type", f => { f["nodes"][0]["type"] = "lua"; return f; }, "bad_value"),
            ("missing tunable", f => { f["nodes"][1]["k"] = "$nope"; return f; }, "bad_value"),
            ("tunable out of range", f => { f["tunables"]["max_speed"]["value"] = 9; return f; }, "tunable_out_of_range"),
            ("bad schema", f => { f["schema"] = "albie.controller/9"; return f; }, "bad_value"),
        };
        var good = (JObject)Load(1).Raw.DeepClone();
        var messages = new StringBuilder();
        foreach (var (what, edit, code) in cases)
        {
            var json = edit((JObject)good.DeepClone()).ToString();
            var file = ControllerFile.Parse(json, out string error);
            if (file != null) error = new ControllerHost(NewIO()).Attach(file);
            if (error == null || !error.StartsWith(code + ":")) return $"{what}: expected {code}, got {error ?? "no error"}";
            if (messages.Length == 0) messages.Append(error);
        }
        note = $"{cases.Length} bad files refused, e.g. \"{messages}\"";
        return messages.ToString().Contains("ray_front.d0-d6") ? null : "the unknown-channel message should list the Thing's sensors";
    }

    static string FailedSwapKeepsOld(out string note)
    {
        var host = new ControllerHost(NewIO());
        host.Attach(Load(1));
        var bad = (JObject)Load(0).Raw.DeepClone();
        bad["bindings"][0]["actuator"] = "nope.throttle";
        string error = host.Attach(ControllerFile.Parse(bad.ToString(), out _));
        note = $"refused with \"{error}\"; still running {host.Current?.Name}";
        return error != null && host.Current?.Name == "truck_cruise" ? null : note;
    }

    sealed class Throws : IThingController
    {
        public string Kind => "graph";
        public string Name => "throws";
        public string Bind(ControllerIO io) => null;
        public void Tick(ControllerIO io, float dt) { io.Actuators[1] = 1f; throw new InvalidOperationException("boom"); }
        public void ResetState() { }
    }

    static string FaultHandling(out string note)
    {
        var host = new ControllerHost(NewIO());
        string raised = null;
        host.Faulted += reason => raised = reason;
        host.Attach(new Throws());
        host.Step(Dt, 0f);
        host.Step(Dt, 0f);
        note = $"\"{host.Fault}\", actuators zeroed";
        if (raised == null || host.Fault == null) return "no fault raised";
        if (host.IO.Actuators[0] != 0 || host.IO.Actuators[1] != 0) return "actuators must be zero after a fault";
        if (host.Attach(Load(1)) != null || host.Fault != null) return "attaching a new controller should clear the fault";
        return null;
    }

    // Change max_speed while cruising: the PID output must stay continuous, not restart from a zero integral.
    static string TunableKeepsState(out string note)
    {
        var host = new ControllerHost(NewIO());
        host.Attach(Load(1));
        var sim = new MockTruckSim(0.1f, 0f);
        for (int i = 0; i < 40; i++) { sim.Sense(host.IO.Sensors); host.Step(Dt, sim.Speed); sim.Step(host.IO.Actuators, Dt); }
        sim.Sense(host.IO.Sensors);
        var sensors = (float[])host.IO.Sensors.Clone();
        host.Step(Dt, sim.Speed);
        float before = host.IO.Actuators[1];
        string error = host.SetTunable("max_speed", 1.6f);
        if (error != null) { note = null; return error; }
        Array.Copy(sensors, host.IO.Sensors, sensors.Length);
        host.Step(Dt, sim.Speed);
        float after = host.IO.Actuators[1];

        var fresh = new ControllerHost(NewIO());
        fresh.Attach(Load(1));
        Array.Copy(sensors, fresh.IO.Sensors, sensors.Length);
        fresh.Step(Dt, sim.Speed);
        float restarted = fresh.IO.Actuators[1];
        note = $"throttle {before:0.0000} → {after:0.0000} (a reset PID would give {restarted:0.0000}); limit now {host.SpeedLimit:0.0} m/s";
        if (Mathf.Abs(host.SpeedLimit - 1.6f) > 1e-5f) return "the speed limit should follow max_speed";
        if (host.SetTunable("max_speed", 99f) == null) return "an out-of-range value must be refused";
        return Mathf.Abs(after - before) < Mathf.Abs(restarted - before) ? null : note;
    }

    static string NoAllocations(out string note)
    {
        long total = 0;
        foreach (int index in new[] { 0, 1 })
        {
            var host = new ControllerHost(NewIO());
            host.Attach(Load(index));
            var sim = new MockTruckSim();
            ManualInputs.Set("stick.y", 0.6f);
            for (int i = 0; i < 10; i++) { sim.Sense(host.IO.Sensors); host.Step(Dt, sim.Speed); sim.Step(host.IO.Actuators, Dt); }
            total += AllocationCounter.BytesDuring(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    if (sim.Crashed || sim.Finished) sim.Reset();
                    sim.Sense(host.IO.Sensors); host.Step(Dt, sim.Speed); sim.Step(host.IO.Actuators, Dt);
                }
            });
        }
        note = $"{total} bytes over 2 × 1000 steps (manual and graph)";
        return total == 0 ? null : note;
    }
}
