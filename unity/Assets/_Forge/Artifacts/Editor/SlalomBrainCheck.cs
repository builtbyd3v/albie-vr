using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Forge.Artifacts;
using Forge.Controllers;
using Forge.Notebook;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// The demo's brain beat in the app, end to end (PROJECT_CONTEXT 16.8, "room slalom, in the app"): in Play mode, in the
/// slalom sandbox, every mock room that fits a course gets "Make me a slalom", the brain truck (ML A's physics truck
/// with a controller), 2 scripted laps as scored runs, training, and the Autopilot on a scored run. Also a hand lap
/// into a cone and one into a wall (the truck senses and touches the room's colliders), the truck parked after a run,
/// and the run records' scoring.
///
/// Storage goes to a scratch folder (library, approvals, notebook) and everything is put back after, as the Editor
/// checks do. Play mode runs at <see cref="TimeScale"/>× so the 6 rooms take about a minute.
/// Menu: Forge/Artifacts/Check Brain Loop (Play). From the CLI: <see cref="Start"/>, then poll <see cref="Result"/>.
/// </summary>
[InitializeOnLoad]
public static class SlalomBrainCheck
{
    const string Scene = "Assets/_Forge/Artifacts/SlalomSandbox.unity";
    const string KeyRunning = "Forge.SlalomBrainCheck.Running", KeyResult = "Forge.SlalomBrainCheck.Result";
    const string KeyScene = "Forge.SlalomBrainCheck.Scene", KeyProject = "Forge.SlalomBrainCheck.Project", KeyRoot = "Forge.SlalomBrainCheck.Root";
    const float TimeScale = 4f;

    static IEnumerator script;
    static string scratch;
    static readonly StringBuilder log = new StringBuilder();
    static bool ok;

    /// <summary>"PASS ..." or "FAIL ..." once a check has finished and Play mode has stopped; "running" before; null if none ran.</summary>
    public static string Result => SessionState.GetBool(KeyRunning, false) ? "running" : SessionState.GetString(KeyResult, null);

    static SlalomBrainCheck() { EditorApplication.playModeStateChanged += OnPlayMode; }

    [MenuItem("Forge/Artifacts/Check Brain Loop (Play)")]
    static void Menu() => Debug.Log("[SlalomBrainCheck] " + Start());

    /// <summary>Opens the slalom sandbox and enters Play mode; the check runs there. Returns an error, or "started".</summary>
    public static string Start()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return "leave Play mode first";
        var active = EditorSceneManager.GetActiveScene();
        if (active.isDirty) return "save or discard the open scene first (" + active.path + ")";
        SessionState.SetString(KeyScene, active.path);
        SessionState.SetString(KeyProject, NotebookStore.ProjectId ?? "");
        SessionState.SetString(KeyRoot, NotebookStore.IsOpen ? NotebookStore.Root : "");
        SessionState.EraseString(KeyResult);
        EditorSceneManager.OpenScene(Scene);
        SessionState.SetBool(KeyRunning, true);
        EditorApplication.isPlaying = true;
        return "started";
    }

    static void OnPlayMode(PlayModeStateChange state)
    {
        if (!SessionState.GetBool(KeyRunning, false)) return;
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            log.Clear();
            ok = true;
            script = Script();
            EditorApplication.update += Step;
        }
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            EditorApplication.update -= Step;
            Restore();
            SessionState.SetBool(KeyRunning, false);
            string previous = SessionState.GetString(KeyScene, "");
            if (!string.IsNullOrEmpty(previous) && previous != Scene && File.Exists(previous)) EditorSceneManager.OpenScene(previous);
            if (SessionState.GetString(KeyResult, null) == null)
                SessionState.SetString(KeyResult, "FAIL slalom brain check: Play mode stopped before the check finished\n" + log);
            Debug.Log("[SlalomBrainCheck] " + Result);
        }
    }

    static void Step()
    {
        bool more;
        try { more = script.MoveNext(); }
        catch (Exception e)
        {
            Fail("threw", e.GetType().Name + ": " + e.Message);
            more = false;
        }
        if (more) return;
        EditorApplication.update -= Step;
        SessionState.SetString(KeyResult, (ok ? "PASS" : "FAIL") + " slalom brain check (Play mode, the brain truck on its colliders)\n" + log);
        Restore();
        EditorApplication.isPlaying = false;
    }

    // Puts every static the check moved back (Play mode keeps statics in this project, so they'd outlive it).
    static void Restore()
    {
        // Only in Play mode: Play puts it back by itself, and a write in edit mode dirties ProjectSettings/TimeManager.
        if (EditorApplication.isPlaying && Time.timeScale != 1f) Time.timeScale = 1f;
        ManualInputs.Clear();
        ArtifactLibrary.RootOverride = null;
        ArtifactApprovals.FileOverride = null;
        NotebookFiles.Flush();
        string project = SessionState.GetString(KeyProject, ""), root = SessionState.GetString(KeyRoot, "");
        NotebookStore.Close();
        if (project != "") NotebookStore.Open(project, root != "" ? root : null);
        if (scratch != null && Directory.Exists(scratch)) Directory.Delete(scratch, true);
        scratch = null;
    }

    static void Pass(string name, string note) => log.Append("  ok   ").Append(name).Append(note != null ? ": " + note : "").Append('\n');

    static void Fail(string name, string why)
    {
        ok = false;
        log.Append("  FAIL ").Append(name).Append(": ").Append(why).Append('\n');
    }

    static void Expect(bool condition, string name, string note) { if (condition) Pass(name, note); else Fail(name, note); }

    // ---- The script -----------------------------------------------------------------------------------------------

    struct RoomResult
    {
        public string Name, Drove, Attempts, LapNotes;
        public int Laps;
        public bool SawBearing, ClearedAfter, Parked;
    }

    static IEnumerator Script()
    {
        // The sandbox loads its first MRUK room asynchronously at Start; wait for that before taking the host over.
        var sandbox = UnityEngine.Object.FindAnyObjectByType<SlalomSandbox>();
        float wait = Time.realtimeSinceStartup + 20f;
        while (sandbox == null || !sandbox.Status.StartsWith("room:"))
        {
            if (Time.realtimeSinceStartup > wait) { Fail("the sandbox starts", sandbox == null ? "no SlalomSandbox in the scene" : sandbox.Status); yield break; }
            yield return null;
            if (sandbox == null) sandbox = UnityEngine.Object.FindAnyObjectByType<SlalomSandbox>();
        }

        scratch = Path.Combine(Application.temporaryCachePath, "slalom_brain_check_" + DateTime.Now.Ticks);
        Directory.CreateDirectory(scratch);
        ArtifactLibrary.RootOverride = Path.Combine(scratch, "artifacts");
        ArtifactApprovals.FileOverride = Path.Combine(scratch, "approvals.json");
        NotebookStore.Close();
        NotebookStore.Open("proj_brain_check", Path.Combine(scratch, "notebook"));
        Time.timeScale = TimeScale;

        var expected = new HashSet<string>();
        foreach (var (room, _) in RoomCourses.MockRooms()) expected.Add(room.Name);
        var results = new List<RoomResult>();
        var refused = new List<string>();
        var all = MockFloorPlan.All();
        bool handDone = false;

        for (int index = 0; index < all.Length; index++)
        {
            sandbox.UseMock(index);
            string name = all[index].Name;
            string error = Open(sandbox);
            if (error != null) { refused.Add(name); continue; }
            var brain = sandbox.UseBrainTruck();
            float settle = Time.time + 0.5f;  // as a person would: the course and truck are up before the first lap
            while (Time.time < settle) yield return null;

            if (!handDone)
            {
                handDone = true;
                var hand = new List<string>();
                IEnumerator lap = HandLap(sandbox, brain, 1f, "a cone", hand);
                while (lap.MoveNext()) yield return null;
                lap = HandLap(sandbox, brain, -1f, "a wall", hand);
                while (lap.MoveNext()) yield return null;
            }

            var r = new RoomResult { Name = name };
            for (int seed = 1; seed <= 2; seed++)
            {
                string e = brain.RecordScriptedLap(seed);
                if (e != null) { Fail(name + ": scripted lap", e); break; }
                string lapRun = sandbox.host.Run.RunId;
                float closest = 9f;
                var cones = sandbox.host.Build.Slots.TryGetValue("course.cones", out var conePoses) ? conePoses : null;
                var origin = sandbox.host.SceneOrigin;
                while (brain.State == SlalomBrain.Mode.Lap)
                {
                    if (cones != null)
                    {
                        var local = Quaternion.Inverse(origin.rotation) * (brain.transform.position - origin.position);
                        foreach (var c in cones) closest = Mathf.Min(closest, new Vector2(local.x - c.position.x, local.z - c.position.z).magnitude);
                    }
                    if (sandbox.host.Run != null && sandbox.host.Run.State == ArtifactRunState.Running && CourseBeacon.HasTarget
                        && Mathf.Abs(brain.Thing.Host.IO.Sensors[MockTruckSim.BearingIndex]) > 0.01f) r.SawBearing = true;
                    yield return null;
                }
                r.ClearedAfter |= seed == 1 && !CourseBeacon.HasTarget;
                NotebookFiles.Flush();
                var rec = ArtifactRuns.Load("slalom", lapRun);
                var lapList = brain.Thing.Recorder.Set.Laps;
                r.LapNotes += $"{(r.LapNotes != null ? "; " : "")}seed {seed}: lap {(lapList.Count > 0 ? lapList[lapList.Count - 1].Outcome : "?")}, run {rec?["outcome"]}" +
                              $"{(rec?["reason"]?.Type == JTokenType.String ? " (" + rec["reason"] + ")" : "")}, gates {rec?["metrics"]?["gates_passed"]}, cones hit {rec?["metrics"]?["collisions"]}, " +
                              $"closest {closest:0.000} m{(brain.Body.Crashed ? ", crashed at " + brain.transform.position.ToString("F2") : "")}";
            }
            r.Laps = brain.CompletedLaps;

            if (r.Laps >= 2)
            {
                string e = brain.Train();
                if (e != null) Fail(name + ": training", e);
                while (brain.State == SlalomBrain.Mode.Training) yield return null;
                e = brain.StartAutopilot();
                if (e != null) Fail(name + ": Autopilot", e);
                while (brain.State == SlalomBrain.Mode.Autopilot) yield return null;
                r.Drove = brain.Drove;
                r.Attempts = string.Join(", ", brain.Attempts);
                var at = brain.transform.position;
                float until = Time.time + 1f;
                while (Time.time < until) yield return null;
                r.Parked = (brain.transform.position - at).sqrMagnitude < 1e-8f;
            }
            results.Add(r);
        }

        // Every room the Editor checks fit a course opens one here; the rest refuse.
        var opened = new List<string>();
        foreach (var r in results) opened.Add(r.Name);
        bool same = opened.Count == expected.Count;
        foreach (var n in opened) same &= expected.Contains(n);
        Expect(same, "the rooms that fit a course open one in the app",
            $"{opened.Count} opened ({string.Join(", ", opened)}), {refused.Count} refused ({string.Join(", ", refused)})");

        int laps = 0, bearing = 0, cleared = 0, drove = 0, live = 0, parked = 0;
        var lines = new StringBuilder();
        foreach (var r in results)
        {
            laps += r.Laps >= 2 ? 1 : 0;
            bearing += r.SawBearing ? 1 : 0;
            cleared += r.ClearedAfter ? 1 : 0;
            drove += r.Drove != null && r.Drove != "nothing" ? 1 : 0;
            live += r.Drove != null && r.Drove.StartsWith("candidate") ? 1 : 0;
            parked += r.Parked ? 1 : 0;
            lines.Append($"      {r.Name}: {r.Laps} laps ({r.LapNotes}); Autopilot: {r.Attempts ?? "-"}; {(r.Parked ? "parked" : "NOT parked")}\n");
        }
        int n2 = results.Count;
        Expect(laps == n2, "2 scripted laps complete as scored runs in every room (physics truck, cone and wall colliders)", $"{laps}/{n2}");
        Expect(bearing == n2 && cleared == n2, "the run engine points course.bearing at the next gate while running, and clears it after",
            $"seen while running {bearing}/{n2}, cleared after {cleared}/{n2}");
        Expect(drove == n2, "the Autopilot drives every room on a scored run (a candidate, else the fallback)",
            $"{drove}/{n2} ({live} by a trained candidate)\n" + lines.ToString().TrimEnd('\n'));
        Expect(parked == n2, "the truck parks after the Autopilot's run", $"{parked}/{n2}");

        NotebookFiles.Flush();
        Records(results.Count);
    }

    // Opens the slalom in the sandbox's room (approving it the first time). Returns the refusal, or null.
    static string Open(SlalomSandbox sandbox)
    {
        var result = sandbox.MakeSlalom();
        if (result["error"] != null) return (string)result["error"];
        if ((string)result["opened"]?["status"] == "awaiting_approval")
        {
            string id = (string)result["id"];
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(ArtifactLibrary.Root, id, "manifest.json")));
            ArtifactApprovals.Approve(manifest);
            result = sandbox.MakeSlalom();
            if (result["error"] != null) return (string)result["error"];
        }
        string status = (string)result["opened"]?["status"];
        return status == "open" ? null : status ?? result.ToString(Newtonsoft.Json.Formatting.None);
    }

    // A lap by hand: W (forward +1) straight at the first cone, or S (-1) back into the wall behind the start.
    static IEnumerator HandLap(SlalomSandbox sandbox, SlalomBrain brain, float forward, string into, List<string> notes)
    {
        string name = "a hand lap (" + (forward > 0 ? "W" : "S") + ") drives the manual controller into " + into + ": the lap is a crash and the run ends";
        brain.KeysFromKeyboard = false;
        string error = brain.RecordLap();
        if (error != null) { Fail(name, error); yield break; }
        string runId = sandbox.host.Run.RunId;
        brain.KeyForward = forward;
        brain.KeyLeft = 0f;
        float top = 0f;
        while (brain.State == SlalomBrain.Mode.Lap) { top = Mathf.Max(top, Mathf.Abs(brain.Body.Speed)); yield return null; }
        brain.KeyForward = 0f;
        brain.KeysFromKeyboard = true;

        var touching = new Collider[4];
        int n = Physics.OverlapSphereNonAlloc(brain.transform.position + brain.transform.up * brain.Body.sensorHeight, MockTruckSim.TruckRadius + 0.01f,
            touching, ~0, QueryTriggerInteraction.Ignore);
        string what = n > 0 ? touching[0].name : "nothing";
        var lap = brain.Thing.Recorder.Set.Laps;
        string outcome = lap.Count > 0 ? lap[lap.Count - 1].Outcome : null;
        NotebookFiles.Flush();
        var record = ArtifactRuns.Load("slalom", runId);
        string recorded = (string)record?["outcome"];
        bool rightThing = forward > 0 ? what == "cone" : what == "Wall";
        // The run ends either way: the artifact's own rule may fail it first (failed), else the brain aborts it.
        Expect(brain.Body.Crashed && outcome == "crashed" && (recorded == "aborted" || recorded == "failed") && rightThing && top > 0.3f && top <= SlalomBrain.KeySpeed + 0.01f, name,
            $"touching {what}, lap {outcome}, run {recorded}, top speed {top:0.00} m/s (keys hold {SlalomBrain.KeySpeed} m/s)");
    }

    // The run records the scoreboard shows: scripted laps through the gates touch no cone, and top speed is the truck's.
    static void Records(int rooms)
    {
        var runs = ArtifactRuns.List("slalom");
        int manual = 0, manualClean = 0, learned = 0, learnedCompleted = 0;
        float minTop = float.MaxValue, maxTop = 0f;
        int learnedHits = 0;
        foreach (var run in runs)
        {
            string kind = (string)run["controller"]?["kind"], outcome = (string)run["outcome"];
            var metrics = run["metrics"] as JObject;
            int hits = (int?)metrics?["collisions"] ?? -1;
            float top = (float?)metrics?["top_speed_ms"] ?? 0f;
            if (kind == "manual" && outcome == "completed")
            {
                manual++;
                if (hits == 0) manualClean++;
                minTop = Mathf.Min(minTop, top);
                maxTop = Mathf.Max(maxTop, top);
            }
            else if (kind == "learned")
            {
                learned++;
                if (outcome == "completed") { learnedCompleted++; learnedHits += Math.Max(0, hits); }
            }
        }
        Expect(manual == 2 * rooms && manualClean == manual && minTop > 0.5f && maxTop < 1f,
            "run records: scripted laps touch no cone, and top speed is the truck's (the driver cruises at 0.7 m/s)",
            $"{manualClean}/{manual} completed laps with 0 cones hit, top speed {minTop:0.00} to {maxTop:0.00} m/s");
        Expect(learnedCompleted == rooms, "run records: each room has one completed Autopilot run, named as the learned controller",
            $"{learned} Autopilot runs, {learnedCompleted} completed, {learnedHits} cones hit in those");

        int training = 0, candidates = 0;
        foreach (var entry in NotebookStore.Entries)
            if ((string)entry["kind"] == "training")
            {
                training++;
                if (entry["observed"]?["training"]?["candidates"] is JArray list && list.Count == SlalomBrain.Candidates) candidates++;
            }
        Expect(training == rooms && candidates == rooms, "the notebook gets a training entry per room, with its 3 candidates",
            $"{training} training entries, {candidates} with {SlalomBrain.Candidates} candidates");
    }
}
