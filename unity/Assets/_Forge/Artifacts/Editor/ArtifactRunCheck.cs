using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Forge.Artifacts;
using Forge.Notebook;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Headless checks for slalom run records (PROJECT_CONTEXT 16.3, Person 5, Nov 10 second half): zone detection,
/// metrics, albie.run/1 files, NotebookStore.AttachRun, and like-for-like compare (C-12 sections 4–6).
/// Forge > Artifacts > Check Runs.
/// </summary>
public static class ArtifactRunCheck
{
    static string root;
    static JObject scene, manifest;
    static ArtifactInputs inputs;
    static Bounds start, finish;
    static Bounds[] gates, cones;

    [MenuItem("Forge/Artifacts/Check Runs")]
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

    // The notebook goes beside the artifact library, never inside it: its writer runs on a worker thread, and a folder
    // it creates in the library while ArtifactRuns.NextId lists the library can hide another artifact's runs from
    // that listing on macOS (HoopCheck's run-id check failed about 1 run in 30). On the device the notebook is
    // persistentDataPath/projects, beside artifacts/, so only the checks had this.
    static string NotebookRoot => root + "_nb";

    public static string Run()
    {
        root = Path.Combine(Application.temporaryCachePath, "artifact_run_check");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        if (Directory.Exists(NotebookRoot)) Directory.Delete(NotebookRoot, true);
        string libBefore = ArtifactLibrary.RootOverride;
        string notebookProject = NotebookStore.ProjectId, notebookRoot = NotebookStore.Root;
        ArtifactLibrary.RootOverride = root;
        var sb = new StringBuilder();
        bool ok = true;
        try
        {
            SetupCourse();
            ok &= Check(sb, "arm → start → running, elapsed grows", Starts);
            ok &= Check(sb, "gates in order complete the course", Completes);
            ok &= Check(sb, "skipping a gate fails as missed_gate", MissedGate);
            ok &= Check(sb, "hitting a cone counts once, not per frame", Collisions);
            ok &= Check(sb, "upright below 0.3 for 1 s fails as rolled_over", RolledOver);
            ok &= Check(sb, "90 s without a finish fails as timeout", Timeout);
            ok &= Check(sb, "abort while running writes aborted", Abort);
            ok &= Check(sb, "records are written once and never overwritten", Records);
            ok &= Check(sb, "like-for-like compare, different course labelled", Compare);
            ok &= Check(sb, "a finished run attaches to the notebook", Notebook);
            ok &= Check(sb, "no Thing is refused, not thrown", NoThing);
            ok &= Check(sb, "fixture runs load as albie.run/1", Fixtures);
            ok &= Check(sb, "ticking allocates nothing", NoAlloc);
        }
        finally
        {
            ArtifactLibrary.RootOverride = libBefore;
            NotebookStore.Close();
            if (notebookProject != null) NotebookStore.Open(notebookProject, notebookRoot);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(NotebookRoot)) Directory.Delete(NotebookRoot, true);
        }
        return (ok ? "PASS" : "FAIL") + " run check\n" + sb;
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

    static void SetupCourse()
    {
        ArtifactLibrary.TryTemplate("slalom", out manifest, out scene);
        inputs = new ArtifactInputs(manifest);
        var slots = FloorSlalom.Slots("course", 5, 0.8f, 0.6f);
        start = Zone(slots["course.start"][0].position, new Vector3(0.9f, 0.5f, 0.3f), 0.25f);
        finish = Zone(slots["course.finish"][0].position, new Vector3(0.9f, 0.5f, 0.3f), 0.25f);
        gates = new Bounds[5];
        cones = new Bounds[5];
        for (int i = 0; i < 5; i++)
        {
            gates[i] = Zone(slots["course.gates"][i].position, new Vector3(0.6f, 0.5f, 0.2f), 0.25f);
            cones[i] = Zone(slots["course.cones"][i].position, new Vector3(0.12f, 0.24f, 0.12f), 0.12f);
        }
    }

    static Bounds Zone(Vector3 floor, Vector3 size, float lift) => new Bounds(floor + Vector3.up * lift, size);

    static (ArtifactRun run, FakeThing thing) Fresh(string layoutId = "lay_0001")
    {
        var thing = new FakeThing();
        var layout = new JObject { ["layout_id"] = layoutId, ["scenario"] = "default", ["seed"] = 42 };
        var zones = new Dictionary<string, Bounds[]>
        {
            ["start"] = new[] { start },
            ["finish"] = new[] { finish },
            ["gates"] = gates,
        };
        var hits = new Dictionary<string, Bounds[]> { ["cones"] = cones };
        var slots = new Dictionary<string, int> { ["course.gates"] = 5, ["course.cones"] = 5, ["course.start"] = 1, ["course.finish"] = 1 };
        var run = new ArtifactRun();
        run.Bind(manifest, scene, layout, inputs, thing, new ArtifactMetrics(), zones, hits, slots);
        return (run, thing);
    }

    static string Starts(out string note)
    {
        note = null;
        var (run, thing) = Fresh();
        string error = run.Arm();
        if (error != null) return error;
        if (run.State != ArtifactRunState.Armed) return "arm left it " + run.State;
        if (!double.IsNaN(run.Board.Value("completion_time_s"))) return "elapsed was set before the start";
        thing.Position = start.center;
        run.Tick(0.02f);
        if (run.State != ArtifactRunState.Running) return "entering start didn't start the run";
        run.Tick(0.5f);
        double t = run.Board.Value("completion_time_s");
        if (t < 0.49 || t > 0.53) return "elapsed " + t;
        note = "armed, then 0.5 s in";
        return null;
    }

    static string Completes(out string note)
    {
        note = null;
        var (run, thing) = Fresh();
        Lap(run, thing, skip: -1, hitCone: -1);
        if (run.State != ArtifactRunState.Finished || run.Outcome != "completed") return run.Outcome + " " + run.Reason;
        var record = run.TakeRecord();
        if (record == null) return "no record";
        if ((int)record["metrics"]["gates_passed"] != 5) return "gates " + record["metrics"]["gates_passed"];
        if ((int)record["metrics"]["collisions"] != 0) return "collisions " + record["metrics"]["collisions"];
        if (record["reason"].Type != JTokenType.Null) return "reason " + record["reason"];
        if ((string)record["schema"] != ArtifactRuns.Schema) return "schema " + record["schema"];
        note = "5 gates, 0 hits, " + record["metrics"]["completion_time_s"] + " s";
        return null;
    }

    static string MissedGate(out string note)
    {
        note = null;
        var (run, thing) = Fresh();
        Lap(run, thing, skip: 2, hitCone: -1);
        if (run.Outcome != "failed" || run.Reason != "missed_gate") return run.Outcome + " " + run.Reason;
        if ((int)run.TakeRecord()["metrics"]["gates_passed"] != 2) return "counted a skipped gate";
        note = "gates 0 and 1, skipped 2";
        return null;
    }

    static string Collisions(out string note)
    {
        note = null;
        var (run, thing) = Fresh();
        run.Arm();
        thing.Position = start.center;
        run.Tick(0.02f);
        thing.Position = cones[0].center;
        for (int i = 0; i < 8; i++) run.Tick(0.02f);
        if ((int)Math.Round(run.Board.Value("collisions")) != 1) return "staying in a cone counted " + run.Board.Value("collisions");
        thing.Position = start.center;
        run.Tick(0.02f);
        thing.Position = cones[0].center;
        run.Tick(0.02f);
        if ((int)Math.Round(run.Board.Value("collisions")) != 2) return "re-entering didn't count; " + run.Board.Value("collisions");
        note = "enter counts, stay doesn't";
        return null;
    }

    static string RolledOver(out string note)
    {
        note = null;
        var (run, thing) = Fresh();
        run.Arm();
        thing.Position = start.center;
        run.Tick(0.02f);
        thing.Upright = 0f;
        run.Tick(0.5f);
        if (run.State != ArtifactRunState.Running) return "failed too soon at 0.5 s";
        run.Tick(0.5f);
        if (run.Outcome != "failed" || run.Reason != "rolled_over") return run.Outcome + " " + run.Reason;
        note = "held for 1 s";
        return null;
    }

    static string Timeout(out string note)
    {
        note = null;
        var (run, thing) = Fresh();
        run.Arm();
        thing.Position = start.center;
        run.Tick(0.02f);
        run.Tick(90f);
        if (run.Outcome != "failed" || run.Reason != "timeout") return run.Outcome + " " + run.Reason;
        note = "90 s";
        return null;
    }

    static string Abort(out string note)
    {
        note = null;
        var (run, thing) = Fresh();
        run.Arm();
        thing.Position = start.center;
        run.Tick(0.02f);
        run.Abort();
        if (run.Outcome != "aborted") return run.Outcome;
        var record = run.TakeRecord();
        if ((string)record["outcome"] != "aborted") return "record " + record["outcome"];
        if (record["metrics"]["completion_time_s"].Type == JTokenType.Null) return "running abort lost elapsed";
        note = "aborted with metrics kept";
        return null;
    }

    static string Records(out string note)
    {
        note = null;
        Directory.CreateDirectory(Path.Combine(root, "slalom", "runs"));
        var (run, thing) = Fresh();
        Lap(run, thing, skip: -1, hitCone: -1);
        var first = run.TakeRecord();
        first["artifact_id"] = "slalom";
        ArtifactRuns.Save(first);
        string path = Path.Combine(root, "slalom", "runs", (string)first["run_id"] + ".json");
        if (!File.Exists(path)) return "not on disk";
        string written = File.ReadAllText(path);
        ArtifactRuns.Save(first);
        if (File.ReadAllText(path) != written) return "a second save changed the file";
        if (ArtifactRuns.NextId("slalom") == (string)first["run_id"]) return "next id reused " + first["run_id"];
        var loaded = ArtifactRuns.Load("slalom", (string)first["run_id"]);
        if ((string)loaded["outcome"] != "completed") return "reload " + loaded;
        note = first["run_id"] + " written once";
        return null;
    }

    static string Compare(out string note)
    {
        note = null;
        var a = ArtifactRuns.Load("slalom", "run_0002");
        var b = ArtifactRuns.Load("slalom", "run_0003");
        var c = ArtifactRuns.Load("slalom", "run_0001");
        if (a == null || b == null || c == null) return "fixtures missing";
        if (!ArtifactRuns.LikeForLike(a, b) || !ArtifactRuns.LikeForLike(a, c)) return "same-course fixtures didn't compare";
        Directory.CreateDirectory(Path.Combine(root, "slalom", "runs"));
        a["layout_id"] = "lay_other";
        a["run_id"] = "run_0098";
        ArtifactRuns.Save(a);
        b["run_id"] = "run_0099";
        ArtifactRuns.Save(b);
        var result = ArtifactTools.Compare(new JObject { ["id"] = "slalom", ["runs"] = new JArray("run_0098", "run_0099") });
        if ((bool)result["runs"][0]["like_for_like"] != true) return "first wasn't like for like with itself";
        if ((bool)result["runs"][1]["like_for_like"] != false || (string)result["runs"][1]["label"] != "different course")
            return "different layout wasn't labelled: " + result;
        note = "run_0002 vs run_0003 like for like; other layout labelled";
        return null;
    }

    static string Notebook(out string note)
    {
        note = null;
        string nb = NotebookRoot;
        NotebookStore.Open("proj_truck", nb);
        NotebookStore.CutVersion("thing", "Ready for a slalom.");
        var (run, thing) = Fresh();
        Lap(run, thing, skip: -1, hitCone: -1);
        run.StampProject(NotebookStore.ProjectId, NotebookStore.CurrentVersion());
        var record = run.TakeRecord();
        ArtifactRuns.Save(record);
        string entry = NotebookStore.AttachRun((string)record["run_id"], (string)record["outcome"]);
        if (entry == null) return "no entry";
        var e = NotebookStore.Entry(entry);
        if ((string)e["outcome"] != "worked" || (string)e["outcome_by"] != "run") return "outcome " + e["outcome"] + " by " + e["outcome_by"];
        bool found = false;
        foreach (var id in (JArray)e["observed"]["runs"]) found |= (string)id == (string)record["run_id"];
        if (!found) return "run id not on the entry";
        note = entry + " worked";
        return null;
    }

    static string NoThing(out string note)
    {
        note = null;
        var run = new ArtifactRun();
        run.Bind(manifest, scene, new JObject { ["layout_id"] = "lay_0001", ["scenario"] = "default", ["seed"] = 42 },
            inputs, null, new ArtifactMetrics(),
            new Dictionary<string, Bounds[]> { ["start"] = new[] { start }, ["finish"] = new[] { finish }, ["gates"] = gates },
            new Dictionary<string, Bounds[]> { ["cones"] = cones },
            new Dictionary<string, int> { ["course.gates"] = 5 });
        string error = run.Arm();
        if (error == null || !error.StartsWith("no_thing")) return "arm without a Thing: " + error;
        note = error;
        return null;
    }

    static string Fixtures(out string note)
    {
        note = null;
        // Read the bundled files, not the scratch library this check writes into.
        JObject Bundled(string id)
        {
            var asset = Resources.Load<TextAsset>("Mocks/artifacts/slalom/runs/" + id);
            return asset != null ? NotebookFiles.Parse(asset.text) as JObject : null;
        }
        foreach (var id in new[] { "run_0001", "run_0002", "run_0003", "run_0004" })
        {
            var run = Bundled(id);
            if (run == null) return id + " missing";
            if ((string)run["schema"] != ArtifactRuns.Schema) return id + " schema " + run["schema"];
            if ((string)run["artifact_id"] != "slalom") return id + " artifact " + run["artifact_id"];
        }
        var failed = Bundled("run_0001");
        if ((string)failed["outcome"] != "failed" || (string)failed["reason"] != "rolled_over") return "run_0001 " + failed["outcome"];
        var done = Bundled("run_0002");
        if ((string)done["outcome"] != "completed") return "run_0002 " + done["outcome"];
        note = "run_0001 rolled over; 0002 hand; 0003 autopilot; 0004 with a hit";
        return null;
    }

    static string NoAlloc(out string note)
    {
        note = null;
        var (run, thing) = Fresh();
        run.Arm();
        thing.Position = start.center;
        run.Tick(0.02f);
        thing.SpeedMs = 1.2f;
        for (int i = 0; i < 10; i++) run.Tick(0.02f);
        // AllocationCounter (Controllers/Editor): GC.GetAllocatedBytesForCurrentThread reads 0 under Unity's GC. It
        // counts every thread, so the notebook's background writes from the earlier steps must be on disk first.
        if (!NotebookFiles.Flush()) return "the notebook's writes didn't finish";
        // 4000 ticks is 80 s, inside the slalom's 90 s timeout: the run must still be going, so every tick measured is
        // a live one. The tick that ends a run builds its record (once), which is fine.
        long grew = AllocationCounter.BytesDuring(() => { for (int i = 0; i < 4000; i++) run.Tick(0.02f); });
        if (run.State != ArtifactRunState.Running) return "the run ended during the measured ticks (" + run.State + "): measure a live run";
        if (grew != 0) return "allocated " + grew + " bytes";
        note = "4000 ticks of a live run, 0 bytes";
        return null;
    }

    static void Lap(ArtifactRun run, FakeThing thing, int skip, int hitCone)
    {
        run.Arm();
        thing.Position = start.center;
        thing.SpeedMs = 1.4f;
        run.Tick(0.02f);
        for (int i = 0; i < gates.Length; i++)
        {
            if (i == skip) continue;
            if (i == hitCone) { thing.Position = cones[i].center; run.Tick(0.02f); }
            thing.Position = gates[i].center;
            run.Tick(0.02f);
        }
        thing.Position = finish.center;
        run.Tick(0.02f);
    }

    sealed class FakeThing : IArtifactThing
    {
        public string Name => "truck";
        public int Version => 2;
        public string ControllerName => "truck_manual";
        public string ControllerKind => "manual";
        public int ControllerVersion => 1;
        public Vector3 Position { get; set; }
        public float SpeedMs { get; set; }
        public float Upright { get; set; } = 1f;
        public float HeightM => Position.y;
        public float Radius => 0.12f;
        public bool Bound => true;
    }
}
