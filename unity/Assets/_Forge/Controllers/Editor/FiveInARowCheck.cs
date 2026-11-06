using System;
using System.Collections.Generic;
using System.Text;
using Forge.Controllers;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The Nov 16 bar for ML A (PROJECT_CONTEXT 16.8): "live training has worked 5 times in a row, or the fallback is
/// used and disclosed". Each attempt is the demo loop from scratch, the way the Controllers sandbox runs it:
/// 1. two fresh laps through the real manual controller and recorder (the scripted driver stands in for a person);
/// 2. three candidates trained 4 ms a "frame", seeds 1-3, as key B does;
/// 3. the Autopilot drives the mock slalom from the start line, trying the candidates in order, then the pretrained
///    fallback, as key P does. A run that hasn't finished in 30 s counts as failed (a stalled brain). The truck is put
///    down by hand before each run, so it starts up to <see cref="StartOffset"/> m to the side and
///    <see cref="StartTurn"/> rad turned (seeded).
/// Laps are recorded the demo way (<see cref="LapStyle.Demo"/>): both from the start line at one speed.
/// It PASSES on the Nov 16 rule: all 5 attempts drove the course, live (a trained candidate) or with the fallback, which
/// must then be disclosed. The report says how many were live, how many needed a second candidate (a crash on camera
/// first) and the odds from a sweep over 30 more drivers, some sloppier, plus the same with a faster second lap.
/// Forge > Controllers > Check Five In A Row; <see cref="Run"/> returns the report (about 30 s). Mock truck in the
/// Editor: the Quest and the real truck are still to do.
/// </summary>
public static class FiveInARowCheck
{
    const float Dt = 0.02f;
    const double FrameBudgetMs = 4.0;
    public const float RunTimeoutS = 30f;
    const int Candidates = 3;
    /// <summary>How far off the start line the truck is put down by hand (settable for experiments).</summary>
    public static float StartOffset = 0.15f, StartTurn = 0.08f;
    static readonly int[] GateSeeds = { 501, 502, 503, 504, 505 };

    public sealed class Attempt
    {
        public int DriverSeed, Redos, Frames;
        public float Noise, LapSeconds, StartY, StartTurn;
        public double ComputeMs;
        /// <summary>1-based trained candidate that drove the course, 0 for the fallback, -1 for none.</summary>
        public int Kept = -1;
        public int FailedRuns;
        public string Failures = "";
        public bool Trained => Kept >= 1;
        public bool FirstTry => Kept == 1;
        public string Who => Kept >= 1 ? $"candidate {Kept}" : Kept == 0 ? "FALLBACK" : "NOTHING";
    }

    [MenuItem("Forge/Controllers/Check Five In A Row")]
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

    public static string Run() => Run(sweep: 30);

    public static string Run(int sweep)
    {
        var sb = new StringBuilder();
        var fallback = LoadFallback();
        bool ok = true;
        int live = 0, firstTry = 0;
        sb.Append("  5 in a row (drivers ").Append(string.Join(", ", GateSeeds)).Append(", demo laps):\n");
        for (int i = 0; i < GateSeeds.Length; i++)
        {
            var a = Attempt1(GateSeeds[i], 1f, fallback);
            ok &= a.Kept >= 0;
            if (a.Trained) live++;
            if (a.FirstTry) firstTry++;
            sb.Append($"    {i + 1}. {(a.Kept < 0 ? "FAIL" : a.Trained ? "ok  " : "DISCLOSE")} {a.Who} drove it from {a.StartY:+0.00;-0.00} m, {a.StartTurn:+0.00;-0.00} rad");
            sb.Append($"; laps {a.LapSeconds:0.0} s{(a.Redos > 0 ? $" ({a.Redos} lap redone)" : "")}, trained in {a.Frames} frames ({a.ComputeMs:0} ms)");
            if (a.FailedRuns > 0) sb.Append($"; before that {a.Failures}");
            sb.Append('\n');
        }
        if (sweep > 0) sb.Append(Sweep(sweep, fallback));
        string verdict = !ok ? "nothing drove the course at least once"
            : live == GateSeeds.Length ? $"live training worked {live} times in a row ({firstTry} on the first candidate)"
            : $"{live}/{GateSeeds.Length} live, the rest by the pretrained fallback: the demo must say so";
        return (ok ? "PASS" : "FAIL") + " five in a row check (mock truck, Editor): " + verdict + "\n" + sb;
    }

    /// <summary>
    /// More drivers: demo laps at 1×, 1.5× and 2× steering noise, then demo laps with the second lap faster (a coaching
    /// tip to compare). How often the loop works.
    /// </summary>
    static string Sweep(int count, ControllerFile fallback)
    {
        var sb = new StringBuilder();
        var runs = new[] { (1f, LapStyle.Demo), (1.5f, LapStyle.Demo), (2f, LapStyle.Demo), (1f, LapStyle.SecondLapFaster) };
        foreach (var (noise, style) in runs)
        {
            int n = count / 3, first = 0, trained = 0, fell = 0, none = 0, redos = 0;
            var misses = new List<string>();
            for (int s = 0; s < n; s++)
            {
                var a = Attempt1(900 + s, noise, fallback, style);
                redos += a.Redos;
                if (a.FirstTry) first++;
                if (a.Trained) trained++;
                else { if (a.Kept == 0) fell++; else none++; misses.Add($"{a.DriverSeed}: {a.Who}"); }
            }
            double p = trained / (double)n;
            sb.Append($"  sweep, {(style == LapStyle.Demo ? "demo laps" : "second lap faster")}, noise {noise:0.0}x, {n} drivers: candidate 1 drove {first}/{n}, a trained candidate {trained}/{n}, fallback {fell}, nothing {none}");
            if (redos > 0) sb.Append($", {redos} laps redone");
            sb.Append($"; chance of 5 live in a row ≈ {Math.Pow(p, 5):P0}");
            if (misses.Count > 0) sb.Append(" (").Append(string.Join(", ", misses)).Append(')');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The training settings for candidate seed 1-3 (null: the trainer's defaults, as the sandbox uses).</summary>
    public static Func<int, BehaviourCloner.Options> OptionsFor;

    /// <summary>One demo attempt from scratch.</summary>
    /// <summary>One demo attempt from scratch, on the corridor or on a room slalom (<paramref name="course"/>).</summary>
    public static Attempt Attempt1(int driverSeed, float noise, ControllerFile fallback, LapStyle style = LapStyle.Demo, MockCourse course = null)
    {
        var a = new Attempt { DriverSeed = driverSeed, Noise = noise };
        var laps = RecordLaps(driverSeed, noise, a, style, course);

        var cloners = new BehaviourCloner[Candidates];
        for (int i = 0; i < Candidates; i++)
        {
            cloners[i] = new BehaviourCloner(laps, OptionsFor?.Invoke(i + 1) ?? new BehaviourCloner.Options { Seed = i + 1 });
            while (!cloners[i].Step(FrameBudgetMs)) a.Frames++;
            a.Frames++;
            a.ComputeMs += cloners[i].ComputeSeconds * 1000;
        }

        var files = new List<ControllerFile>();
        foreach (var c in cloners)
        {
            var f = ControllerFile.Parse(c.ToControllerJson("truck_autopilot", 1, 1.5f), out string error);
            if (f == null) throw new InvalidOperationException(error);
            files.Add(f);
        }
        var host = new ControllerHost(new ControllerIO(MockTruckSim.SensorNames, MockTruckSim.ActuatorNames));
        var autopilot = new Autopilot(files, fallback);
        if (autopilot.Engage(host) != null) return a;
        var failures = new List<string>();
        var placement = new System.Random(driverSeed * 7919);
        while (!autopilot.Exhausted)
        {
            bool fallbackDriving = autopilot.UsingFallback;
            int index = autopilot.Index;
            host.ResetState();
            float y0 = (float)(placement.NextDouble() * 2 - 1) * StartOffset, turn = (float)(placement.NextDouble() * 2 - 1) * StartTurn;
            if (a.FailedRuns == 0) { a.StartY = y0; a.StartTurn = turn; }
            var sim = course != null ? new MockTruckSim(course, y0, turn) : new MockTruckSim(y0, turn);
            while (!sim.Crashed && !sim.Finished && sim.Time < RunTimeoutS)
            {
                sim.Sense(host.IO.Sensors);
                host.Step(Dt, sim.Speed);
                sim.Step(host.IO.Actuators, Dt);
            }
            autopilot.RunEnded(host, sim.Finished);
            if (sim.Finished) { a.Kept = fallbackDriving ? 0 : index + 1; break; }
            a.FailedRuns++;
            string how = sim.Missed != null ? $"{sim.Missed} at x {sim.X:0.0} m" : sim.Crashed ? $"crashed at x {sim.X:0.0} m" : "stalled";
            failures.Add($"{(fallbackDriving ? "fallback" : "candidate " + (index + 1))} {how}");
        }
        a.Failures = string.Join(", ", failures);
        return a;
    }

    /// <summary>How the two laps are driven (<see cref="MockLaps.Style"/>, same order).</summary>
    public enum LapStyle { Demo, SecondLapFaster, Varied }

    // Two completed laps, like a person: a crashed lap is driven again.
    public static LapSet RecordLaps(int driverSeed, float noise, Attempt a, LapStyle style, MockCourse course = null)
    {
        var set = MockLaps.Record(driverSeed, noise, (MockLaps.Style)style, out float seconds, out int redos, course);
        a.LapSeconds += seconds;
        a.Redos += redos;
        return set;
    }

    public static ControllerFile LoadFallback()
    {
        var asset = Resources.Load<TextAsset>("Controllers/truck_autopilot_fallback");
        if (asset == null) return null;
        var file = ControllerFile.Parse(asset.text, out string error);
        if (file == null) throw new InvalidOperationException("the pretrained fallback doesn't load: " + error);
        return file;
    }
}
