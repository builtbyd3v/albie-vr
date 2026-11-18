using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Forge.Artifacts;
using Forge.Controllers;
using Forge.Notebook;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// The drone's learning loop as a person uses it, end to end in Play mode (the slalom sandbox's host, a hoop course in
/// the mock living room, the real drone, its DroneBody physics and the real DroneBrain):
/// 1. two noisy, human-like laps flown through <see cref="ManualInputs"/> (the channels the transmitter, the Drive pads
///    and a Touch stick write), each started only by taking off from the start, as the Drive window's Take off does:
///    the brain must pick both up by itself, with "lap 1 of 2" in its status and Albie saying so;
/// 2. Train: progress in the status, then "vN ready: passed k of 5 practice flights" or the honest reason with the
///    "Follow course line (not learned)" option;
/// 3. the Autopilot (or the course-line follower when the version isn't ready) flies a scored run with a held
///    transmitter's resting thumbs on the sticks (0.12 off centre) and must not hand back; a firm stick then hands back;
/// 4. a design change from CAD (arm length 220 mm) retunes the drone while it flies, with a before/after line.
/// Storage goes to a scratch folder and is put back after. Menu: Forge/Artifacts/Check Drone Brain (Play).
/// From the CLI: <see cref="Start"/>, then poll <see cref="Result"/>.
/// </summary>
[InitializeOnLoad]
public static class DroneBrainCheck
{
    const string Scene = "Assets/_Forge/Artifacts/SlalomSandbox.unity";
    const string KeyRunning = "Forge.DroneBrainCheck.Running", KeyResult = "Forge.DroneBrainCheck.Result";
    const string KeyScene = "Forge.DroneBrainCheck.Scene", KeyProject = "Forge.DroneBrainCheck.Project", KeyRoot = "Forge.DroneBrainCheck.Root";
    const float TimeScale = 3f;

    static IEnumerator script;
    static string scratch;
    static readonly StringBuilder log = new StringBuilder();
    static readonly List<string> said = new List<string>();
    static bool ok;

    public static string Result => SessionState.GetBool(KeyRunning, false) ? "running" : SessionState.GetString(KeyResult, null);

    static DroneBrainCheck() { EditorApplication.playModeStateChanged += OnPlayMode; }

    [MenuItem("Forge/Artifacts/Check Drone Brain (Play)")]
    static void Menu() => Debug.Log("[DroneBrainCheck] " + Start());

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
            said.Clear();
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
                SessionState.SetString(KeyResult, "FAIL drone brain check: Play mode stopped before the check finished\n" + log);
            Debug.Log("[DroneBrainCheck] " + Result);
        }
    }

    static void Step()
    {
        bool more;
        try { more = script.MoveNext(); }
        catch (Exception e)
        {
            Fail("threw", e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace);
            more = false;
        }
        if (more) return;
        EditorApplication.update -= Step;
        SessionState.SetString(KeyResult, (ok ? "PASS" : "FAIL") + " drone brain check (Play mode, hoop course, human-like laps)\n" + log
                                          + "  Albie said:\n    " + string.Join("\n    ", said) + "\n");
        Restore();
        EditorApplication.isPlaying = false;
    }

    static void Restore()
    {
        if (EditorApplication.isPlaying && Time.timeScale != 1f) Time.timeScale = 1f;
        DroneBrain.SayHook = null;
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
    static void Fail(string name, string why) { ok = false; log.Append("  FAIL ").Append(name).Append(": ").Append(why).Append('\n'); }
    static void Expect(bool condition, string name, string note) { if (condition) Pass(name, note); else Fail(name, note); }

    static readonly int StickX = ManualInputs.IndexOf("stick.x"), StickY = ManualInputs.IndexOf("stick.y"),
                        Stick2X = ManualInputs.IndexOf("stick2.x"), Stick2Y = ManualInputs.IndexOf("stick2.y");

    // What the transmitter does: writes the four stick channels.
    static void Sticks(float x, float y, float x2, float y2)
    {
        var v = ManualInputs.Values;
        v[StickX] = Mathf.Clamp(x, -1f, 1f); v[StickY] = Mathf.Clamp(y, -1f, 1f);
        v[Stick2X] = Mathf.Clamp(x2, -1f, 1f); v[Stick2Y] = Mathf.Clamp(y2, -1f, 1f);
    }

    // ---- The script -----------------------------------------------------------------------------------------------

    static IEnumerator Script()
    {
        var sandbox = UnityEngine.Object.FindAnyObjectByType<SlalomSandbox>();
        float wait = Time.realtimeSinceStartup + 20f;
        while (sandbox == null || !sandbox.Status.StartsWith("room:"))
        {
            if (Time.realtimeSinceStartup > wait) { Fail("the sandbox starts", sandbox == null ? "no SlalomSandbox" : sandbox.Status); yield break; }
            yield return null;
            if (sandbox == null) sandbox = UnityEngine.Object.FindAnyObjectByType<SlalomSandbox>();
        }
        scratch = Path.Combine(Application.temporaryCachePath, "drone_brain_check_" + DateTime.Now.Ticks);
        Directory.CreateDirectory(scratch);
        ArtifactLibrary.RootOverride = Path.Combine(scratch, "artifacts");
        ArtifactApprovals.FileOverride = Path.Combine(scratch, "approvals.json");
        NotebookStore.Close();
        NotebookStore.Open("proj_drone_brain_check", Path.Combine(scratch, "notebook"));
        DroneBrain.SayHook = line => said.Add(line);
        Time.timeScale = TimeScale;

        sandbox.UseMock(0);
        string error = OpenHoops(sandbox);
        if (error != null) { Fail("make a hoop course", error); yield break; }
        var host = sandbox.host;
        var drone = ArtifactDrone.Live != null ? ArtifactDrone.Live : ArtifactDrone.Spawn(host);
        var brain = DroneBrain.Ensure(host);
        Expect(brain != null && ReferenceEquals(BrainLoops.Active, brain), "the hoop course gets the drone and its brain, in front of the loops",
               brain != null ? brain.Status : "no brain");
        if (brain == null) yield break;
        float settle = Time.time + 0.5f;
        while (Time.time < settle) yield return null;

        // 1. Two human-like laps, started only by taking off (no Record call).
        var rng = new System.Random(7);
        int attempts = 0;
        while (brain.CompletedLaps < 2 && attempts < 6)
        {
            attempts++;
            drone.ResetToStart();
            yield return null;
            error = drone.TakeOff();
            if (error != null) { Fail("take off", error); yield break; }
            float until = Time.time + 3f;
            while (brain.State != DroneBrain.Mode.Lap && Time.time < until) yield return null;
            if (brain.State != DroneBrain.Mode.Lap) { Fail("a hand flight is recorded by itself", brain.Status + " (run " + drone.RunState + ")"); yield break; }
            if (attempts == 1) Expect(brain.Passive && brain.Status.Contains("lap 1 of 2"), "taking off from the start records the flight as lap 1 of 2", brain.Status);
            IEnumerator fly = HumanLap(brain, drone, host, rng);
            while (fly.MoveNext()) yield return null;
            Sticks(0, 0, 0, 0);
            log.Append("       flight ").Append(attempts).Append(": ").Append(brain.Status).Append('\n');
        }
        Expect(brain.CompletedLaps >= 2, "two hand laps completed and recorded", $"{brain.CompletedLaps} laps in {attempts} flights, {brain.ToolState()["recorded_steps"]} steps");
        Expect(said.Exists(s => s.StartsWith("Lap 1 recorded")) && said.Exists(s => s.Contains("tap Train")), "Albie says each lap and when to train", said.Count + " lines");
        if (brain.CompletedLaps < 2) yield break;

        // 2. Train.
        while (drone.Body.Airborne && drone.Body.State != DroneBody.Phase.Landing) { drone.Land(); yield return null; }
        error = brain.Train();
        Expect(error == null && brain.State == DroneBrain.Mode.Training, "Train starts", error ?? brain.Status);
        var seen = new HashSet<string>();
        float trainUntil = Time.time + 120f;
        while (brain.State == DroneBrain.Mode.Training && Time.time < trainUntil) { seen.Add(brain.Status); yield return null; }
        bool percents = false;
        foreach (var s in seen) if (s.Contains("%")) percents = true;
        var v = brain.Versions.Count > 0 ? brain.Versions[brain.Versions.Count - 1] : null;
        bool honest = v != null && (v.Ready ? brain.Status.StartsWith("v" + v.Number + " ready: passed") : brain.Status.Contains("Follow course line"));
        Expect(brain.State == DroneBrain.Mode.Idle && v != null && percents && honest, "training finishes with progress and a plain result",
               $"{seen.Count} status lines with %: {percents}; \"{brain.Status}\"");

        // 3. The Autopilot (or the follower), with resting thumbs that must not hand back.
        error = v != null && v.Ready ? brain.StartAutopilot() : brain.StartFollowLine();
        string who = v != null && v.Ready ? "the Autopilot" : "follow the course line (version not ready)";
        if (error != null) { Fail(who + " starts", error); yield break; }
        float end = Time.time + DroneBrain.RunTimeoutS + 2f;
        while (brain.State == DroneBrain.Mode.Autopilot && Time.time < end)
        {
            Sticks(0.12f, -0.1f, 0.05f, 0.11f);  // a held transmitter: thumbs resting a little off centre
            yield return null;
        }
        bool handedBack = brain.Attempts.Exists(a => a.EndsWith("handed back"));
        Expect(!handedBack && !string.IsNullOrEmpty(brain.Drove) && brain.Drove != "nothing", who + " flies the course with resting thumbs on the sticks",
               $"drove {brain.Drove ?? "-"}; attempts: {string.Join("; ", brain.Attempts)}");

        // A firm stick hands back.
        Sticks(0, 0, 0, 0);
        error = brain.StartFollowLine();
        float t0 = Time.time;
        while (error == null && brain.State == DroneBrain.Mode.Autopilot && Time.time < t0 + 1.2f) { Sticks(0.12f, 0f, 0f, 0f); yield return null; }
        while (error == null && brain.State == DroneBrain.Mode.Autopilot && Time.time < t0 + 4f) { Sticks(0.12f, 0.8f, 0f, 0f); yield return null; }
        Sticks(0, 0, 0, 0);
        Expect(error == null && brain.State == DroneBrain.Mode.Idle && brain.Attempts.Exists(a => a.EndsWith("handed back")), "a firm stick takes it back", error ?? brain.Status);

        // 4. A design change while it flies.
        float yawBefore = drone.Body.yawRate, climbBefore = drone.Body.climbSpeed;
        var request = typeof(DroneDesign).GetMethod("OnRequested", BindingFlags.Instance | BindingFlags.NonPublic);
        if (request == null) { Fail("design change", "DroneDesign.OnRequested not found"); yield break; }
        request.Invoke(drone.Design, new object[] { DroneDesign.Part, new Dictionary<string, object> { ["arm_length"] = 220.0 } });
        yield return null;
        Expect(drone.Design.HasFlight && drone.Body.yawRate < yawBefore * 0.8f && !string.IsNullOrEmpty(drone.Design.LastChange),
               "longer arms from CAD retune the flying drone", $"turn {yawBefore:0} -> {drone.Body.yawRate:0} °/s, climb {climbBefore:0.00} -> {drone.Body.climbSpeed:0.00}; {drone.Design.LastChange}; \"{drone.Design.LastSaid}\"");
    }

    static string OpenHoops(SlalomSandbox sandbox)
    {
        var result = sandbox.MakeHoops();
        if (result["error"] != null) return (string)result["error"];
        if ((string)result["opened"]?["status"] == "awaiting_approval")
        {
            string id = (string)result["id"];
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(ArtifactLibrary.Root, id, "manifest.json")));
            ArtifactApprovals.Approve(manifest);
            result = sandbox.MakeHoops();
            if (result["error"] != null) return (string)result["error"];
        }
        string status = (string)result["opened"]?["status"];
        return status == "open" ? null : status ?? result.ToString(Newtonsoft.Json.Formatting.None);
    }

    // A person on the transmitter: perception 0.25 s late, an aim point a few cm off each hoop's centre, proportional
    // sideways correction with noise and a slow wander, steady forward stick, bang-bang climb, a pause now and then.
    // The finish is flown into at the height it has (people don't aim for its centre). Sticks are in the pilot's frame.
    static IEnumerator HumanLap(DroneBrain brain, ArtifactDrone drone, ArtifactHost host, System.Random rng)
    {
        var history = new Queue<KeyValuePair<float, Vector3>>();
        Vector3 aim = Vector3.zero, lastTarget = Vector3.positiveInfinity;
        float wander = 0f, pause = 0f, cruise = 0.55f + 0.2f * (float)rng.NextDouble();
        bool climbing = false; float climbDir = 0f;
        float until = Time.time + DroneBrain.RunTimeoutS;
        while (brain.State == DroneBrain.Mode.Lap && Time.time < until)
        {
            yield return null;
            if (!CourseBeacon.HasTarget) continue;
            Vector3 target = CourseBeacon.Target;
            if ((target - lastTarget).sqrMagnitude > 1e-4f)
            {
                if (!float.IsInfinity(lastTarget.x) && rng.NextDouble() < 0.25) pause = 0.5f + (float)rng.NextDouble() * 0.5f;
                lastTarget = target;
                aim = new Vector3((float)(rng.NextDouble() - 0.5) * 0.16f, (float)(rng.NextDouble() - 0.5) * 0.16f, 0f);
            }
            Vector3 to = target - drone.transform.position;
            history.Enqueue(new KeyValuePair<float, Vector3>(Time.time, to));
            while (history.Count > 1 && history.Peek().Key < Time.time - 0.25f) history.Dequeue();
            Vector3 seen = history.Peek().Value;
            // Into the course frame (x right, z forward).
            float yaw = host.SceneOrigin.rotation.eulerAngles.y;
            Vector3 local = Quaternion.Euler(0f, -yaw, 0f) * seen + aim;
            Vector3 vel = Quaternion.Euler(0f, -yaw, 0f) * drone.Body.Velocity;
            bool finish = local.y < -0.25f;  // the finish centre is 0.6 m up, under the hoops' 1.0 m
            float fwd, right, climb = 0f;
            if (pause > 0f) { pause -= Time.deltaTime; fwd = right = 0f; }
            else
            {
                wander = 0.97f * wander + 0.03f * (float)(rng.NextDouble() * 2 - 1);
                right = 2.2f * local.x - 0.6f * vel.x + 0.25f * (float)(rng.NextDouble() * 2 - 1) + 0.8f * wander;
                fwd = Mathf.Abs(local.x) > 0.25f && local.z < 0.8f ? 0.25f : cruise;
                float ey = finish ? 0f : local.y;
                if (!climbing && Mathf.Abs(ey) > 0.12f) { climbing = true; climbDir = Mathf.Sign(ey); }
                if (climbing && (Mathf.Abs(ey) < 0.05f || Mathf.Sign(ey) != climbDir)) climbing = false;
                climb = climbing ? 0.75f * climbDir : 0f;
            }
            // Course frame to the pilot's (headless: forward is the camera's heading).
            var cam = Camera.main;
            float pilot = drone.Body.headless ? (cam != null ? cam.transform.eulerAngles.y : 0f) : drone.transform.eulerAngles.y;
            Vector3 world = Quaternion.Euler(0f, yaw, 0f) * new Vector3(right, 0f, fwd);
            Vector3 stick = Quaternion.Euler(0f, -pilot, 0f) * world;
            Sticks(stick.x, stick.z, 0f, climb);
        }
    }
}
