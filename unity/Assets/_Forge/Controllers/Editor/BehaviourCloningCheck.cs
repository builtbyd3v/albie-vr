using System;
using System.Text;
using Forge.Controllers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Headless checks for on-device behaviour cloning (PROJECT_CONTEXT 16.8, ML A Nov 8): recording laps through the real
/// manual controller and host, the albie.laps/1 file, frame-budgeted training, the exported controller file, and the
/// closed-loop result on the fake truck. Forge > Controllers > Check Behaviour Cloning logs the report; <see cref="Run"/>
/// returns it (for `unity command eval`). Timings are Editor timings: the Quest run is a separate step.
/// </summary>
public static class BehaviourCloningCheck
{
    const float Dt = 0.02f;
    const double FrameBudgetMs = 4.0;   // of about 14 ms a frame at 72 fps

    [MenuItem("Forge/Controllers/Check Behaviour Cloning")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        bool ok = true;
        LapSet laps = null;
        ok &= Check(sb, "record 2 laps through the manual controller", (out string note) => RecordTwoLaps(out laps, out note));
        ok &= Check(sb, "recording allocates nothing per step", RecordingAllocatesNothing);
        ok &= Check(sb, "idle time is dropped", IdleIsDropped);
        ok &= Check(sb, "laps file round trip", (out string note) => LapsRoundTrip(laps, out note));
        ok &= Check(sb, "reads the Python mock laps", ReadsMockLaps);
        ok &= Check(sb, "frame-budgeted training", (out string note) => FrameBudgetedTraining(laps, out note));
        ok &= Check(sb, "exported controller file", (out string note) => ExportedFile(laps, out note));
        ok &= Check(sb, "closed loop on the fake truck (3 seeds)", (out string note) => ClosedLoop(laps, out note));
        ManualInputs.Clear();
        return (ok ? "PASS" : "FAIL") + " behaviour cloning check\n" + sb;
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

    static ControllerHost ManualHost()
    {
        var host = new ControllerHost(new ControllerIO(MockTruckSim.SensorNames, MockTruckSim.ActuatorNames));
        var asset = Resources.Load<TextAsset>("Mocks/controllers/truck_manual");
        if (asset == null) throw new InvalidOperationException("missing Resources/Mocks/controllers/truck_manual.json");
        string error = host.Attach(ControllerFile.Parse(asset.text, out string parseError));
        if (error != null || parseError != null) throw new InvalidOperationException(error ?? parseError);
        return host;
    }

    static LapRecorder NewRecorder(ControllerHost host) =>
        new LapRecorder(host.IO, "truck", 2, "truck_manual", MockTruckSim.MirrorSwap, MockTruckSim.MirrorNegate);

    /// <summary>Two laps by the scripted driver, recorded the way a person's laps are (sandbox and checks share it).</summary>
    public static LapSet DriveLaps(int driverSeed, int count = 2)
    {
        var host = ManualHost();
        var recorder = NewRecorder(host);
        var driver = new MockDriver(driverSeed);
        var sim = new MockTruckSim();
        for (int lap = 0; lap < count; lap++)
        {
            driver.Cruise = 1.1f + 0.2f * (lap % 2);
            sim.Reset(0.04f * (lap % 3), 0.03f * (lap % 2));
            recorder.BeginLap();
            for (int i = 0; i < 1500 && !sim.Crashed && !sim.Finished; i++)
            {
                sim.Sense(host.IO.Sensors);
                driver.Drive(sim);
                host.Step(Dt, sim.Speed);
                recorder.Record(host.IO.Sensors, host.Commanded, Dt);
                sim.Step(host.IO.Actuators, Dt);
            }
            recorder.EndLap(sim.Finished ? "completed" : sim.Crashed ? "crashed" : "aborted");
        }
        ManualInputs.Clear();
        return recorder.Set;
    }

    static string RecordTwoLaps(out LapSet laps, out string note)
    {
        laps = DriveLaps(300);
        note = $"{laps.CompletedLaps}/2 completed, {laps.Laps[0].X.Length} + {laps.Laps[1].X.Length} frames, {laps.Laps[0].Seconds} s and {laps.Laps[1].Seconds} s";
        return laps.CompletedLaps == 2 ? null : note;
    }

    static string RecordingAllocatesNothing(out string note)
    {
        var host = ManualHost();
        var recorder = NewRecorder(host);
        var sim = new MockTruckSim();
        recorder.BeginLap();
        ManualInputs.Set("stick.y", 0.5f);
        for (int i = 0; i < 5; i++) { sim.Sense(host.IO.Sensors); host.Step(Dt, sim.Speed); recorder.Record(host.IO.Sensors, host.Commanded, Dt); sim.Step(host.IO.Actuators, Dt); }
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++)
        {
            if (sim.Crashed || sim.Finished) sim.Reset();
            sim.Sense(host.IO.Sensors); host.Step(Dt, sim.Speed); recorder.Record(host.IO.Sensors, host.Commanded, Dt); sim.Step(host.IO.Actuators, Dt);
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        ManualInputs.Clear();
        note = $"{bytes} bytes over 500 steps, {recorder.Frames} frames kept";
        return bytes == 0 ? null : note;
    }

    // 1 s driving, 3 s parked, 1 s driving: only the first second of the pause is kept.
    static string IdleIsDropped(out string note)
    {
        var host = ManualHost();
        var recorder = NewRecorder(host);
        var sim = new MockTruckSim();
        recorder.BeginLap();
        void Phase(float stick, int steps)
        {
            ManualInputs.Set("stick.y", stick);
            for (int i = 0; i < steps; i++) { sim.Sense(host.IO.Sensors); host.Step(Dt, sim.Speed); recorder.Record(host.IO.Sensors, host.Commanded, Dt); sim.Step(host.IO.Actuators, Dt); }
        }
        Phase(0.6f, 50); Phase(0f, 150); Phase(0.6f, 50);
        ManualInputs.Clear();
        int expected = 50 + 50 + 50;   // 1 s idle kept, 2 s dropped
        note = $"{recorder.Frames} of 250 frames kept (expected about {expected})";
        return Math.Abs(recorder.Frames - expected) <= 2 ? null : note;
    }

    static string LapsRoundTrip(LapSet laps, out string note)
    {
        var json = laps.ToJson();
        var back = LapSet.Parse(json, out string error);
        note = $"{json.Length / 1024} KB";
        if (back == null) return error;
        if (back.Laps.Count != laps.Laps.Count || back.Sensors.Length != laps.Sensors.Length) return "lap or channel count changed";
        float worst = 0;
        for (int l = 0; l < laps.Laps.Count; l++)
            for (int t = 0; t < laps.Laps[l].X.Length; t++)
            {
                for (int c = 0; c < laps.Sensors.Length; c++) worst = Math.Max(worst, Math.Abs(back.Laps[l].X[t][c] - laps.Laps[l].X[t][c]));
                for (int c = 0; c < laps.Actuators.Length; c++) worst = Math.Max(worst, Math.Abs(back.Laps[l].Y[t][c] - laps.Laps[l].Y[t][c]));
            }
        note += $", worst value change {worst:0.0000} (3 decimals in the file)";
        if (back.MirrorSwap.Length != 3 || back.MirrorNegate[0] != "steer.angle") return "mirror map lost";
        return worst <= 0.0006f ? null : note;
    }

    static string ReadsMockLaps(out string note)
    {
        var asset = Resources.Load<TextAsset>("Mocks/controllers/laps/truck_v2");
        if (asset == null) { note = null; return "missing Resources/Mocks/controllers/laps/truck_v2.json"; }
        var set = LapSet.Parse(asset.text, out string error);
        note = set == null ? null : $"{set.Laps.Count} laps, {set.CompletedLaps} completed, {set.Sensors.Length} sensors";
        return set == null ? error : null;
    }

    // The trainer checks its budget after every batch, so a frame can only run over by about one batch. The check
    // measures wall-clock time, and on a busy machine the OS can pause the thread mid-frame (25-30 ms spikes were seen
    // with Spotlight indexing), so it judges the 95th-percentile frame and reports the worst. A trainer that ignored
    // its budget would make every frame long, and fail this.
    static string FrameBudgetedTraining(LapSet laps, out string note)
    {
        var cloner = new BehaviourCloner(laps);
        cloner.Step(FrameBudgetMs);   // warm up (JIT)
        var frames = new System.Collections.Generic.List<double>();
        long bytes = 0;
        var clock = new System.Diagnostics.Stopwatch();
        while (!cloner.Done)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            clock.Restart();
            cloner.Step(FrameBudgetMs);
            clock.Stop();
            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
            frames.Add(clock.Elapsed.TotalMilliseconds);
        }
        frames.Sort();
        double p95 = frames[(int)Math.Ceiling(frames.Count * 0.95) - 1], median = frames[frames.Count / 2], worst = frames[frames.Count - 1];
        note = $"{cloner.TrainCount} train / {cloner.ValidationCount} validation rows, {cloner.Epoch} epochs in {cloner.Steps} frames " +
               $"({cloner.Steps / 72f:0.0} s at 72 fps), {cloner.ComputeSeconds * 1000:0} ms compute; frame times median {median:0.00} ms, " +
               $"95th percentile {p95:0.00} ms, worst {worst:0.00} ms (budget {FrameBudgetMs}); val loss {cloner.ValidationLoss:0.0000}, {bytes} bytes allocated";
        if (bytes != 0) return note;
        if (p95 > FrameBudgetMs + 1) return note;
        return cloner.ValidationLoss < 0.05f ? null : note;
    }

    static string ExportedFile(LapSet laps, out string note)
    {
        var cloner = new BehaviourCloner(laps);
        cloner.TrainToEnd();
        string json = cloner.ToControllerJson("truck_autopilot", 3, 1.5f);
        var file = ControllerFile.Parse(json, out string error);
        note = null;
        if (file == null) return error;
        if (file.Kind != "learned" || file.Version != 3 || Math.Abs(file.SpeedLimit - 1.5f) > 1e-5f) return "common fields wrong";
        var raw = JObject.Parse(json);
        var sizes = raw["net"]["sizes"].ToObject<int[]>();
        int weights = 0;
        for (int l = 1; l < sizes.Length; l++) weights += ((JArray)raw["net"]["weights"][l - 1]).Count * ((JArray)raw["net"]["weights"][l - 1][0]).Count;
        note = $"sizes [{string.Join(", ", sizes)}], {weights} weights, {json.Length / 1024.0:0.0} KB, inputs {((JArray)raw["inputs"]).Count}";
        return sizes[0] == laps.Sensors.Length && sizes[sizes.Length - 1] == laps.Actuators.Length ? null : note;
    }

    // Train 3 seeds; each drives the slalom from 15 starts. Passes when at least one seed finishes every start
    // (the "train a few, let the first arena run decide" plan).
    static string ClosedLoop(LapSet laps, out string note)
    {
        var results = new int[3];
        for (int seed = 1; seed <= 3; seed++)
        {
            var cloner = new BehaviourCloner(laps, new BehaviourCloner.Options { Seed = seed });
            cloner.TrainToEnd();
            results[seed - 1] = Finishes(cloner);
        }
        note = $"starts finished per seed: {results[0]}/15, {results[1]}/15, {results[2]}/15";
        return Math.Max(results[0], Math.Max(results[1], results[2])) == 15 ? null : note;
    }

    public static int Finishes(BehaviourCloner cloner) => MockTrial.Finishes(cloner);
}
