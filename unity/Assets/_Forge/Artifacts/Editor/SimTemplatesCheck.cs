using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Forge.Artifacts;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Headless checks for the physics templates (double_pendulum, solar_system) and what they added to the host (the
/// <c>pendulum</c> solver, <c>line.through</c>): both templates are valid, buildable, need no permissions, and are made
/// offline; a small swing keeps the textbook period; a double pendulum keeps its energy; two copies a hundredth of a degree
/// apart agree at first and then split, while two with no gap never do; friction drains the swing; Earth's year is a year
/// and nothing drifts; half a Sun lets the planets go; the validator refuses the new mistakes.
/// Forge > Artifacts > Check Physics Templates.
/// </summary>
public static class SimTemplatesCheck
{
    static readonly string[] Sims = { "double_pendulum", "solar_system" };

    [MenuItem("Forge/Artifacts/Check Physics Templates")]
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
        string root = Path.Combine(Application.temporaryCachePath, "sim_templates_check");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string before = ArtifactLibrary.RootOverride;
        ArtifactLibrary.RootOverride = root;
        var sb = new StringBuilder();
        bool ok = true;
        try
        {
            ok &= Check(sb, "both physics templates are valid, need no permissions, and this host builds them", Valid);
            ok &= Check(sb, "artifact_list offers them and artifact_create makes them offline", Made);
            ok &= Check(sb, "a small swing keeps the textbook period, 2π√(L/g)", Period);
            ok &= Check(sb, "the double pendulum keeps its energy over a minute", Energy);
            ok &= Check(sb, "a hundredth of a degree: together at first, then apart; no gap: together for good", Chaos);
            ok &= Check(sb, "friction drains the swing", Friction);
            ok &= Check(sb, "solar system: Earth's year is a year, and the energy holds", Year);
            ok &= Check(sb, "solar system: with half a Sun the planets get away", HalfSun);
            ok &= Check(sb, "the validator refuses a 3-link pendulum, a 0 m rod, and bad line.through", Refusals);
        }
        finally
        {
            ArtifactLibrary.RootOverride = before;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return (ok ? "PASS" : "FAIL") + " physics templates check\n" + sb;
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

    static JObject Node(JObject scene, string id)
    {
        foreach (JObject n in (JArray)scene["nodes"]) if ((string)n["id"] == id) return n;
        return null;
    }

    static string Valid(out string note)
    {
        note = null;
        var counts = new List<string>();
        foreach (string t in Sims)
        {
            if (!ArtifactLibrary.TryTemplate(t, out var manifest, out var scene)) return t + " isn't in the library";
            var errors = ArtifactValidator.Validate(manifest, scene);
            if (errors.Count > 0) return t + ": " + errors[0];
            var missing = ArtifactNodes.Missing(scene);
            if (missing.Count > 0) return t + " can't build: " + string.Join(", ", missing);
            if ((int)manifest["runtime"]["min_runtime"] != 6 || ArtifactSchema.HostRuntime < 6) return t + " min_runtime " + manifest["runtime"]["min_runtime"];
            var p = manifest["permissions"];
            if ((string)p["room"] != "none" || ((JArray)p["network"]).Count > 0 || ((JArray)p["connectors"]).Count > 0 || (string)p["things"] != "none")
                return t + " asks for permissions; a simulation to watch needs none";
            counts.Add($"{t} {((JArray)scene["nodes"]).Count} nodes, {((JArray)scene["rules"]).Count} rules");
        }
        note = string.Join("; ", counts) + $"; host runtime {ArtifactSchema.HostRuntime}";
        return null;
    }

    static string Made(out string note)
    {
        note = null;
        var list = ArtifactTools.List(new JObject());
        foreach (string t in Sims)
        {
            bool offered = false;
            foreach (var entry in (JArray)list["templates"]) offered |= (string)entry["template"] == t;
            if (!offered) return t + " isn't offered by artifact_list";
            var made = ArtifactTools.Create(new JObject { ["template"] = t, ["open"] = false });
            if ((string)made["id"] != t || (int)made["version"] != 1) return "create " + t + ": " + made;
        }
        var made2 = ArtifactTools.Create(new JObject { ["template"] = "double_pendulum", ["inputs"] = new JObject { ["angle"] = 170 }, ["open"] = false });
        if ((int)made2["version"] != 2) return "a new starting angle should be version 2: " + made2;
        try
        {
            var refused = ArtifactTools.Create(new JObject { ["template"] = "double_pendulum", ["inputs"] = new JObject { ["angle"] = 200 }, ["open"] = false });
            return "an angle of 200 should be refused: " + refused;
        }
        catch (ArgumentException e) when (e.Message.StartsWith("bad_value")) { }
        note = "made both with no network; a 170° start is version 2; 200° is refused";
        return null;
    }

    // A pendulum solver for one sim node of the template, with the template's defaults or the given overrides.
    static PendulumSolver Pendulum(string sim, JObject overrides = null, Action<JObject> edit = null)
    {
        ArtifactLibrary.TryTemplate("double_pendulum", out var manifest, out var scene);
        var node = (JObject)Node(scene, sim).DeepClone();
        edit?.Invoke(node);
        var solver = new PendulumSolver();
        solver.Configure(node, new ArtifactInputs(manifest, overrides));
        solver.Restart();
        return solver;
    }

    static double Run(IArtifactSolver solver, double seconds)
    {
        int steps = (int)Math.Round(seconds / solver.StepSize);
        for (int i = 0; i < steps; i++) solver.Step();
        return seconds;
    }

    static string Period(out string note)
    {
        note = null;
        var solver = Pendulum("swing_a", null, n =>
        {
            var bodies = (JArray)n["bodies"];
            bodies.RemoveAt(1);
            bodies[0]["angle"] = 4;
            n["params"]["damping"] = 0;
        });
        int angle = solver.ValueIndex("angle_1"), time = solver.ValueIndex("time");
        // Time between successive downward zero crossings, averaged over 10 swings.
        double last = solver.Value(angle), first = -1, at = -1;
        int crossings = 0;
        while (crossings < 11 && solver.Value(time) < 30)
        {
            solver.Step();
            double now = solver.Value(angle);
            if (last > 0 && now <= 0)
            {
                double t = solver.Value(time) - solver.StepSize * now / (now - last);
                if (first < 0) first = t;
                at = t;
                crossings++;
            }
            last = now;
        }
        if (crossings < 11) return "only " + crossings + " swings in 30 s";
        double measured = (at - first) / 10.0, textbook = 2 * Math.PI * Math.Sqrt(0.5 / 9.81);
        double error = Math.Abs(measured - textbook) / textbook;
        // At 4° the true period is longer than the small-angle one by about θ²/16 = 0.03 %.
        if (error > 0.002) return $"period {measured:F4} s, textbook {textbook:F4} s ({error * 100:F2} % off)";
        note = $"0.5 m at 4°: {measured:F4} s against {textbook:F4} s ({error * 100:F3} % off)";
        return null;
    }

    static string Energy(out string note)
    {
        note = null;
        var solver = Pendulum("swing_a");
        Run(solver, 60);
        double drift = solver.Value(solver.ValueIndex("energy_drift_pct"));
        double flips = solver.Value(solver.ValueIndex("flips"));
        if (drift > 1e-4) return $"energy drifted {drift:G3} % in 60 s";
        if (flips < 1) return "a 120° start should go over the top at least once in a minute";
        note = $"120° start, 60 s at 0.5 ms steps: drift {drift:G2} %, {flips} flips";
        return null;
    }

    static string Chaos(out string note)
    {
        note = null;
        var a = Pendulum("swing_a");
        var b = Pendulum("swing_b");
        var same = Pendulum("swing_b", new JObject { ["nudge"] = 0 });
        int tip = 1;
        Run(a, 1); Run(b, 1); Run(same, 1);
        float early = Vector3.Distance(a.Position(tip), b.Position(tip));
        if (early > 0.005f) return $"already {early * 100:F1} cm apart after 1 s";
        double splitAt = -1;
        for (int s = 1; s < 40 && splitAt < 0; s++)
        {
            Run(a, 1); Run(b, 1); Run(same, 1);
            if (Vector3.Distance(a.Position(tip), b.Position(tip)) > 0.3f) splitAt = s + 1;
            if (Vector3.Distance(a.Position(tip), same.Position(tip)) != 0f) return $"two with no gap disagreed at {s + 1} s";
        }
        if (splitAt < 0) return "a 0.01° gap never split them in 40 s";
        note = $"tips {early * 1000:F2} mm apart at 1 s, more than 30 cm apart by {splitAt} s; no gap stays identical";
        return null;
    }

    static string Friction(out string note)
    {
        note = null;
        var free = Pendulum("swing_a");
        var damped = Pendulum("swing_a", new JObject { ["friction"] = 0.3 });
        Run(free, 20);
        Run(damped, 20);
        float freeTip = free.Position(1).y, dampedTip = damped.Position(1).y;
        double drift = damped.Value(damped.ValueIndex("energy_drift_pct"));
        if (drift < 50) return $"with friction 0.3 only {drift:F1} % of the swing's energy went in 20 s";
        if (dampedTip > -0.8f) return $"after 20 s with friction the tip should hang low, it's at y = {dampedTip:F2}";
        note = $"friction 0.3 lost {drift:F0} % in 20 s; the tip hangs at y = {dampedTip:F2} m (free: {freeTip:F2})";
        return null;
    }

    static NBodySolver Solar(JObject overrides = null)
    {
        ArtifactLibrary.TryTemplate("solar_system", out var manifest, out var scene);
        var solver = new NBodySolver();
        solver.Configure(Node(scene, "orbits"), new ArtifactInputs(manifest, overrides));
        solver.Restart();
        return solver;
    }

    static string Year(out string note)
    {
        note = null;
        var solver = Solar();
        const int Earth = 3;  // sun, mercury, venus, earth, mars
        var start = solver.Position(Earth);
        double startAngle = Math.Atan2(start.y, start.x), turned = 0, last = startAngle, year = -1;
        int time = solver.ValueIndex("time");
        while (solver.Value(time) < 1.5)
        {
            solver.Step();
            var p = solver.Position(Earth);
            double angle = Math.Atan2(p.y, p.x), d = angle - last;
            if (d > Math.PI) d -= 2 * Math.PI;
            if (d < -Math.PI) d += 2 * Math.PI;
            turned += d;
            last = angle;
            if (turned >= 2 * Math.PI)
            {
                year = solver.Value(time);
                break;
            }
        }
        if (year < 0) return "Earth didn't come round in 1.5 years";
        if (Math.Abs(year - 1.0) > 0.01) return $"Earth's year is {year:F3} years";
        while (solver.Value(time) < 10) solver.Step();
        double drift = solver.Value(solver.ValueIndex("energy_drift_pct"));
        double r = solver.Position(Earth).magnitude;
        if (drift > 0.01) return $"energy drifted {drift:G3} % in 10 years";
        if (Math.Abs(r - 1.0) > 0.02) return $"after 10 years Earth is {r:F3} AU out";
        if (solver.Value(solver.ValueIndex("escaped")) != 0) return "a planet escaped with the real Sun";
        note = $"Earth's year {year:F4} years; after 10 years Earth is at {r:F4} AU and energy drift is {drift:G2} %";
        return null;
    }

    static string HalfSun(out string note)
    {
        note = null;
        var solver = Solar(new JObject { ["sun_mass"] = 0.5 });
        int time = solver.ValueIndex("time");
        while (!solver.Finished && solver.Value(time) < 20) solver.Step();
        if (!solver.Finished) return "with half a Sun nothing escaped in 20 years";
        note = $"a planet passed 4 AU after {solver.Value(time):F2} years, and the sim stopped there";
        return null;
    }

    static string Refusals(out string note)
    {
        note = null;
        ArtifactLibrary.TryTemplate("double_pendulum", out var manifest, out var scene);
        var cases = new (string code, Action<JObject> mistake)[]
        {
            ("too_big", s => ((JArray)Node(s, "swing_a")["bodies"]).Add(new JObject { ["node"] = "a_pivot", ["mass"] = 1, ["length"] = 0.5 })),
            ("bad_value", s => Node(s, "swing_a")["bodies"][0]["length"] = 0),
            ("unknown_ref", s => ((JArray)Node(s, "a_rods")["through"])[1] = "nowhere"),
            ("bad_value", s => ((JArray)Node(s, "a_rods")["through"])[2] = "b_tip"),
            ("bad_value", s => Node(s, "a_rods")["through"] = new JArray("a_pivot")),
            ("unknown_ref", s => ((JArray)s["rules"])[1]["value"] = "closest_approach"),
        };
        var codes = new List<string>();
        foreach (var (code, mistake) in cases)
        {
            var broken = (JObject)scene.DeepClone();
            mistake(broken);
            var errors = ArtifactValidator.Validate(manifest, broken);
            bool found = false;
            foreach (var e in errors) found |= e.code == code;
            if (!found) return $"expected {code}, got [{string.Join("; ", errors)}]";
            codes.Add(code);
        }
        note = $"{cases.Length} broken copies refused ({string.Join(", ", codes)})";
        return null;
    }
}
