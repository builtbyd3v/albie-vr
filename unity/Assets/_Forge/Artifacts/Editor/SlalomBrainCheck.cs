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
/// and the run records' scoring. Then the brain beat by voice: the controller_brain tools drive the brain truck (one
/// brain_record call records 2 laps, trains and runs the Autopilot), stop it, and fall back to the panel preview.
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
        QuestStick.Override = null;
        HandsTransmitter.Source = null;
        DriveAids.SteadyThrottle = false;
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
                lap = StickLaps(sandbox, brain);
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

        // The headset test of Nov 18: the docked transmitter, then laps by a shaky human hand on its stick channels.
        IEnumerator docked = DockedTransmitter(sandbox);
        while (docked.MoveNext()) yield return null;
        IEnumerator human = HumanLaps(sandbox);
        while (human.MoveNext()) yield return null;

        IEnumerator voice = Voice(sandbox);
        while (voice.MoveNext()) yield return null;
    }

    // ---- The docked transmitter (Nov 18) --------------------------------------------------------------------------

    // A hand for the transmitter with no headset: open and flat (no grip, no pinch), well to the side, with its thumb tip
    // put wherever the check says.
    sealed class FakeHands : IHandSource
    {
        public readonly Vector3[] Thumb = new Vector3[2];
        public readonly bool[] On = new bool[2];
        public Vector3 Away = new Vector3(0.25f, 0f, -0.2f);
        public Transform Frame;

        public void Read(int hand, HandSample sample)
        {
            sample.isLeft = hand == 0;
            sample.tracked = On[hand];
            sample.highConfidence = On[hand];
            if (!On[hand] || Frame == null) return;
            Vector3 side = Frame.right * (hand == 0 ? -1f : 1f);
            Vector3 o = Frame.position + side * Away.x + Frame.forward * Away.z + Frame.up * Away.y;
            Vector3 f = Frame.forward, r = -side;
            var j = sample.joints;
            for (int i = 0; i < j.Length; i++) j[i] = o;
            j[HandJoint.Thumb1] = o + r * 0.02f + f * 0.02f;
            j[HandJoint.Thumb2] = o + r * 0.035f + f * 0.04f;
            j[HandJoint.Thumb3] = o + r * 0.05f + f * 0.06f;
            void Finger(int b, int tip, float across)
            {
                Vector3 bas = o + f * 0.09f + r * across;
                j[b] = bas; j[b + 1] = bas + f * 0.03f; j[b + 2] = bas + f * 0.055f; j[tip] = bas + f * 0.075f;
            }
            Finger(HandJoint.Index1, HandJoint.IndexTip, 0.02f);
            Finger(HandJoint.Middle1, HandJoint.MiddleTip, 0f);
            Finger(HandJoint.Ring1, HandJoint.RingTip, -0.02f);
            Finger(HandJoint.Pinky1, HandJoint.PinkyTip, -0.04f);
            j[HandJoint.ThumbTip] = Thumb[hand];
        }
    }

    // The transmitter docks in front of the user, stays put, and a thumb resting on a stick drives the truck through
    // stick.y relative to where it touched; lifted, the stick centres. The drone's left stick (take-off) uses the same path.
    static IEnumerator DockedTransmitter(SlalomSandbox sandbox)
    {
        var brain = sandbox.Brain;
        var tx = RcTransmitter.Ensure();
        if (brain == null || tx == null) { Fail("docked transmitter", "no brain truck or no transmitter"); yield break; }
        bool wasDocked = RcTransmitter.Docked;
        var hands = new FakeHands();
        HandsTransmitter.Source = hands;
        brain.KeysFromKeyboard = false;
        brain.PlaceAtStart();
        ManualInputs.Clear();
        string where = tx.SetDocked(true) ?? tx.Show();
        float until = Time.realtimeSinceStartup + 1.5f;
        while (Time.realtimeSinceStartup < until && tx.Driving != RcTransmitter.Target.Truck) yield return null;
        until = Time.realtimeSinceStartup + 0.6f;  // the glide to the dock
        while (Time.realtimeSinceStartup < until) yield return null;
        hands.Frame = tx.transform;
        Vector3 dockedAt = tx.transform.position;
        Quaternion dockedRot = tx.transform.rotation;
        // The left thumb lands 1 cm off the cap's centre: forgiving, it reads zero there.
        Vector3 Cap(float x, float z, float up) => tx.transform.TransformPoint(new Vector3(-0.05f + x, 0.0445f + up, -0.012f + z));
        hands.On[0] = true;
        hands.Thumb[0] = Cap(0.01f, -0.006f, 0.004f);
        until = Time.realtimeSinceStartup + 0.3f;
        while (Time.realtimeSinceStartup < until) yield return null;
        float atTouch = tx.Stick(0).y;
        // Push forward 2.5 cm from where it touched: full throttle (training speed).
        for (int i = 0; i < 20; i++)
        {
            hands.Thumb[0] = Cap(0.01f, -0.006f + 0.025f * (i + 1) / 20f, 0.004f);
            yield return null;
        }
        until = Time.realtimeSinceStartup + 0.4f;
        while (Time.realtimeSinceStartup < until) yield return null;
        int stickY = ManualInputs.IndexOf("stick.y");
        float pushed = tx.Stick(0).y, wrote = ManualInputs.Values[stickY];
        float moved = Vector3.Distance(tx.transform.position, dockedAt) + Quaternion.Angle(tx.transform.rotation, dockedRot) * 0.001f;
        var start = brain.transform.position;
        until = Time.time + 1f;
        while (Time.time < until) yield return null;
        float drove = Vector3.Distance(start, brain.transform.position);
        // Lift the thumb 6 cm: the stick springs home and stick.y goes to zero.
        hands.Thumb[0] = Cap(0.01f, 0.019f, 0.06f);
        until = Time.realtimeSinceStartup + 0.5f;
        while (Time.realtimeSinceStartup < until) yield return null;
        float lifted = tx.Stick(0).y, wroteLifted = ManualInputs.Values[stickY];
        float expected = brain.Body.ThrottleFor(DriveAids.TrainingSpeed);
        Expect(RcTransmitter.Docked && tx.Shown && !tx.Held && Mathf.Abs(atTouch) < 0.05f && pushed > 0.85f && Mathf.Abs(wrote - expected) < 0.02f
               && moved < 0.002f && drove > 0.15f && Mathf.Abs(lifted) < 0.05f && Mathf.Abs(wroteLifted) < 1e-4f,
            "the docked transmitter stays put; a resting thumb drives stick.y from where it touched, and lifting it centres the stick",
            $"{where}; at touch {atTouch:0.00}, pushed 2.5 cm {pushed:0.00} (stick.y {wrote:0.000}, training throttle {expected:0.000}), " +
            $"the transmitter moved {moved * 1000f:0.0} mm, the truck drove {drove:0.00} m in 1 s, lifted {lifted:0.00} (stick.y {wroteLifted:0.000})");

        // Steady throttle: one push and the truck holds the training speed with the thumb lifted; a pull back stops it.
        DriveAids.SteadyThrottle = true;
        brain.PlaceAtStart();
        hands.Thumb[0] = Cap(0f, 0f, 0.004f);
        until = Time.realtimeSinceStartup + 0.2f;
        while (Time.realtimeSinceStartup < until) yield return null;
        for (int i = 0; i < 10; i++) { hands.Thumb[0] = Cap(0f, 0.02f * (i + 1) / 10f, 0.004f); yield return null; }
        until = Time.realtimeSinceStartup + 0.3f;
        while (Time.realtimeSinceStartup < until) yield return null;
        hands.Thumb[0] = Cap(0f, 0.02f, 0.07f);  // lifted: the stick centres, the cruise holds
        until = Time.realtimeSinceStartup + 0.5f;
        while (Time.realtimeSinceStartup < until) yield return null;
        bool cruising = DriveAids.Cruising;
        float cruiseWrote = ManualInputs.Values[stickY];
        hands.Thumb[0] = Cap(0f, 0f, 0.004f);
        until = Time.realtimeSinceStartup + 0.2f;
        while (Time.realtimeSinceStartup < until) yield return null;
        for (int i = 0; i < 10; i++) { hands.Thumb[0] = Cap(0f, -0.02f * (i + 1) / 10f, 0.004f); yield return null; }
        until = Time.realtimeSinceStartup + 0.3f;
        while (Time.realtimeSinceStartup < until) yield return null;
        bool ended = !DriveAids.Cruising;
        DriveAids.SteadyThrottle = false;
        Expect(cruising && Mathf.Abs(cruiseWrote - expected) < 0.02f && ended,
            "steady throttle: one push holds the training speed with the thumb lifted, a pull back ends it",
            $"cruising {cruising} (stick.y {cruiseWrote:0.000} with the thumb lifted), ended by pulling back {ended}");

        hands.On[0] = false;
        HandsTransmitter.Source = null;
        tx.CentreSticks();
        RcTransmitter.Docked = wasDocked;
        ManualInputs.Clear();
        brain.KeysFromKeyboard = true;
        until = Time.time + 0.5f;
        while (Time.time < until) yield return null;
        brain.PlaceAtStart();
    }

    // ---- Laps by a shaky human hand (Nov 18) ----------------------------------------------------------------------

    // A person on the transmitter, as written to the channels it writes (stick.x steering with DriveAids' soft centre,
    // stick.y the training-speed throttle): late, trembling, drifting, steering dropouts, a hesitant start, the throttle
    // wobbling, let go and pulled back near cones. Pure pursuit through the gates' sides, as the scripted driver aims.
    sealed class ShakyHand
    {
        readonly System.Random rng;
        readonly float react, pref;
        readonly bool pulser, brakes;
        float t, drift, wob, sx, sy, releaseLeft, dropLeft, stopLeft, pulse;
        readonly float[] delay = new float[6];
        int head;

        public ShakyHand(int seed)
        {
            rng = new System.Random(seed);
            react = 0.4f + 0.6f * (float)rng.NextDouble();
            pref = 0.65f + 0.35f * (float)rng.NextDouble();
            pulser = rng.NextDouble() < 0.4;
            brakes = rng.NextDouble() < 0.5;
        }

        float G() { double u = 1 - rng.NextDouble(), v = rng.NextDouble(); return (float)(Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v)); }

        /// <summary>The two sticks (x right, y forward) for the truck at (x, y, heading) in the course frame (x along it, y left).</summary>
        public void Sticks(MockCourse course, float x, float y, float heading, float dt, out float steerStick, out float throttleStick)
        {
            t += dt;
            float tx = x + 0.35f, ty = course.PathY(tx, 0.3f);
            float alpha = Mathf.Atan2(ty - y, tx - x) - heading;
            float dist = Mathf.Sqrt((tx - x) * (tx - x) + (ty - y) * (ty - y));
            float want = Mathf.Clamp(Mathf.Atan(2f * MockTruckSim.Wheelbase * Mathf.Sin(alpha) / Mathf.Max(dist, 0.3f)) / (MockTruckSim.MaxSteerDeg * Mathf.Deg2Rad), -1f, 1f);
            delay[head] = want; head = (head + 1) % delay.Length;
            float late = delay[(head + 1) % delay.Length];
            float m = Mathf.Abs(late);
            float stick = m < 0.02f ? 0f : 0.1f + 0.9f * Mathf.Pow(m, 1f / 1.3f);
            float sxNow = late < 0f ? stick : -stick;  // + left steer is a stick to the left (truck_manual's stick.x is -1)
            drift += -drift / 0.5f * dt + 0.25f * Mathf.Sqrt(dt) * G();
            sxNow += drift + 0.1f * G();
            if (t < react) sxNow = 0.6f * Mathf.Sin(t * 9f);
            if (dropLeft > 0f) { dropLeft -= dt; sxNow = 0f; }
            else if (rng.NextDouble() < 0.15 * dt) dropLeft = 0.1f + 0.15f * (float)rng.NextDouble();
            float syNow;
            if (t < react) syNow = 0f;
            else
            {
                wob += -wob / 0.4f * dt + 0.3f * Mathf.Sqrt(dt) * G();
                syNow = pref + wob - 0.25f * m;
                if (pulser) { pulse += dt; syNow = pulse % 0.9f < 0.55f ? 1f : 0f; }
                if (releaseLeft > 0f) { releaseLeft -= dt; syNow = 0f; }
                else if (rng.NextDouble() < 0.25 * dt) releaseLeft = 0.2f + 0.3f * (float)rng.NextDouble();
                if (stopLeft > 0f) { stopLeft -= dt; syNow = stopLeft > 0.45f && brakes ? -0.5f : 0f; }
                else if (m > 0.5f && rng.NextDouble() < 0.6 * dt) stopLeft = 0.6f + 0.6f * (float)rng.NextDouble();
            }
            float k = 1f - Mathf.Exp(-dt / 0.05f);  // the transmitter's stick smoothing
            sx += (Mathf.Clamp(sxNow, -1f, 1f) - sx) * k;
            sy += (Mathf.Clamp(syNow, -1f, 1f) - sy) * k;
            steerStick = Mathf.Abs(sx) < 0.01f ? 0f : sx;
            throttleStick = Mathf.Abs(sy) < 0.01f ? 0f : sy;
        }
    }

    static MockCourse CourseOf(SlalomSandbox sandbox)
    {
        var course = new MockCourse { Name = "open slalom" };
        var slots = sandbox.host.Build.Slots;
        if (slots.TryGetValue("course.cones", out var cones)) foreach (var c in cones) course.AddCone(c.position.z, -c.position.x);
        if (slots.TryGetValue("course.gates", out var gates)) foreach (var g in gates) course.AddGate(g.position.z, -g.position.x);
        if (slots.TryGetValue("course.finish", out var finish) && finish.Count > 0) course.SetFinish(finish[0].position.z, -finish[0].position.x);
        return course;
    }

    // Two completed laps by the shaky hand, written to stick.x and stick.y exactly as the transmitter writes them
    // (DriveAids), on their own (the earlier rooms' laps are set aside): training must clean them, and the Autopilot
    // must then drive the slalom on a scored run, by a brain trained on those laps.
    static IEnumerator HumanLaps(SlalomSandbox sandbox)
    {
        var brain = sandbox.Brain;
        if (brain == null || sandbox.host.Run == null) { Fail("human laps", "no brain truck with an open slalom"); yield break; }
        var set = brain.Thing.Recorder.Set;
        var saved = new List<Lap>(set.Laps);
        set.Laps.Clear();
        brain.KeysFromKeyboard = false;
        brain.KeyForward = brain.KeyLeft = 0f;
        var course = CourseOf(sandbox);
        int stickX = ManualInputs.IndexOf("stick.x"), stickY = ManualInputs.IndexOf("stick.y");
        var outcomes = new List<string>();
        for (int attempt = 1; attempt <= 8 && brain.CompletedLaps < 2; attempt++)
        {
            var hand = new ShakyHand(7000 + attempt);
            string error = brain.RecordLap();
            if (error != null) { Fail("human laps: a lap", error); break; }
            float top = 0f;
            while (brain.State == SlalomBrain.Mode.Lap)
            {
                var origin = sandbox.host.SceneOrigin;
                var toCourse = Quaternion.Inverse(origin.rotation);
                float toFull = 1f / brain.CourseScale;
                var local = toCourse * (brain.transform.position - origin.position) * toFull;
                var fwd = toCourse * brain.transform.forward;
                hand.Sticks(course, local.z, -local.x, Mathf.Atan2(-fwd.x, fwd.z), Time.deltaTime, out float steer, out float throttle);
                // What RcTransmitter.WriteChannels writes for the brain truck.
                ManualInputs.Values[stickX] = Mathf.Clamp(DriveAids.Steer(steer), -1f, 1f);
                ManualInputs.Values[stickY] = Mathf.Clamp(DriveAids.Throttle(throttle, brain.Body.ThrottleFor(DriveAids.TrainingSpeed), false), -1f, 1f);
                top = Mathf.Max(top, brain.Body.Speed);
                yield return null;
            }
            var laps = set.Laps;
            outcomes.Add($"{(laps.Count > 0 ? laps[laps.Count - 1].Outcome : "?")} ({top:0.00} m/s)");
            ManualInputs.Clear();
        }
        brain.KeysFromKeyboard = true;
        int humanLaps = brain.CompletedLaps;
        Expect(humanLaps >= 2, "a shaky human hand on the transmitter's channels completes 2 laps", $"{humanLaps} completed: {string.Join(", ", outcomes)}");
        if (humanLaps >= 2)
        {
            string error = brain.Train();
            while (brain.State == SlalomBrain.Mode.Training) yield return null;
            var c = brain.Cleaning;
            int[] reh = brain.RehearsalFinished;
            string said = brain.LastSaid;
            Expect(error == null && c.LapsKept >= 2 && c.StartRowsDropped > 0 && c.CruiseSpeed > 0.25f && reh != null && said != null && said.Length > 0,
                "training cleans the human laps (start dropped, steering smoothed, steady speed) and says the result",
                $"{error ?? "trained"}: kept {c.LapsKept}/{c.LapsIn} laps, {c.RowsKept}/{c.RowsIn} rows, {c.StartRowsDropped} start rows dropped, " +
                $"steady {c.CruiseSpeed:0.00} m/s, steering noise {c.SteerNoise:0.000}; rehearsal {(reh != null ? string.Join("/", reh) : "-")} " +
                $"(fallback {brain.FallbackRehearsal}); said \"{said}\"");
            error = brain.StartAutopilot();
            float limit = Time.time + 5 * SlalomBrain.RunTimeoutS;
            while (brain.State == SlalomBrain.Mode.Autopilot && Time.time < limit) yield return null;
            if (brain.State == SlalomBrain.Mode.Autopilot) brain.StopAutopilot();
            string attempts = string.Join(", ", brain.Attempts);
            Expect(error == null && brain.Drove != null && brain.Drove != "nothing",
                "after training on shaky human laps the Autopilot drives the slalom on a scored run", $"{error ?? "drove: " + brain.Drove}; {attempts}");
            Expect(brain.Drove != null && brain.Drove.StartsWith("candidate") && !attempts.Contains("stalled"),
                "a brain trained on those human laps drives it (not the fallback), and none stalls", $"{brain.Drove}; {attempts}; said \"{brain.LastSaid}\"");
        }
        ManualInputs.Clear();
        // The earlier rooms' laps back in front, the human ones kept.
        set.Laps.InsertRange(0, saved);
        brain.PlaceAtStart();
    }

    // ---- The brain beat by voice --------------------------------------------------------------------------------

    // The tools in controller_brain.json, called as the agent calls them: with the brain truck running they drive it
    // (laps, training, the Autopilot), and with none they fall back to the brain panel's preview.
    static IEnumerator Voice(SlalomSandbox sandbox)
    {
        var brain = sandbox.Brain;
        if (brain == null || sandbox.host.Run == null) { Fail("voice: the brain tools drive the brain truck", "no brain truck with an open slalom after the rooms"); yield break; }

        var status = AlbieTools.Call("brain_status", new JObject());
        Expect(ReferenceEquals(BrainLoops.Active, brain) && status["preview_only"]?.Type == JTokenType.Boolean && !(bool)status["preview_only"]
               && (string)status["mode"] == "idle", "voice: brain_status reads the running brain truck, not the panel preview", Short(status));

        // "Drive it yourself twice, and I'll learn it": one call records 2 laps, trains, then runs the Autopilot.
        // The laps are the scripted driver's here (a check has no hands); by voice they're always the person's.
        brain.ScriptedLaps = true;
        int lapsBefore = brain.CompletedLaps, entriesBefore = TrainingEntries();
        var started = AlbieTools.Call("brain_record", new JObject { ["start"] = true, ["laps"] = 2, ["then_autopilot"] = true });
        bool lapMode = started["error"] == null && (string)started["mode"] == "lap" && (int?)started["laps_to_go"] == 2;
        var modes = new List<SlalomBrain.Mode>();
        float limit = Time.time + 6 * SlalomBrain.RunTimeoutS;
        while (brain.State != SlalomBrain.Mode.Idle && Time.time < limit)
        {
            if (modes.Count == 0 || modes[modes.Count - 1] != brain.State) modes.Add(brain.State);
            yield return null;
        }
        brain.ScriptedLaps = false;
        string order = string.Join(" > ", modes).ToLowerInvariant();
        int laps = brain.CompletedLaps - lapsBefore, entries = TrainingEntries() - entriesBefore;
        status = AlbieTools.Call("brain_status", new JObject());
        bool drove = brain.Drove != null && brain.Drove != "nothing";
        Expect(lapMode && order == "lap > training > autopilot" && laps == 2 && entries == 1 && drove && (string)status["drove"] == brain.Drove,
            "voice: brain_record {start, laps 2, then_autopilot} records 2 laps, trains, then the Autopilot drives, with no other call",
            $"started in {(string)started["mode"] ?? (string)started["error"]} ({(int?)started["laps_to_go"]} to go); {order}; " +
            $"{laps} laps, {entries} training entry; the Autopilot: {string.Join(", ", brain.Attempts)}");

        // Stop mid-lap: the lap is aborted, and the completed laps are kept.
        int kept = brain.CompletedLaps;
        var lap = AlbieTools.Call("brain_record", new JObject { ["start"] = true, ["laps"] = 1 });
        float until = Time.time + 1f;
        while (Time.time < until) yield return null;
        var stopped = AlbieTools.Call("brain_record", new JObject { ["start"] = false });
        var lapList = brain.Thing.Recorder.Set.Laps;
        string last = lapList.Count > 0 ? lapList[lapList.Count - 1].Outcome : null;
        Expect((string)lap["mode"] == "lap" && (string)stopped["mode"] == "idle" && last == "aborted" && brain.CompletedLaps == kept
               && sandbox.host.Run.State != ArtifactRunState.Running,
            "voice: brain_record {start false} aborts the lap being driven and keeps the completed laps",
            $"{(string)lap["mode"] ?? (string)lap["error"]} > {(string)stopped["mode"] ?? (string)stopped["error"]}, last lap {last}, {brain.CompletedLaps} completed laps kept");

        // The Autopilot on, then off: the truck goes back to the manual controller.
        var on = AlbieTools.Call("autopilot_on", new JObject());
        string autopilotRun = sandbox.host.Run.RunId;
        until = Time.time + 1f;
        while (Time.time < until) yield return null;
        var off = AlbieTools.Call("autopilot_off", new JObject());
        var again = AlbieTools.Call("autopilot_off", new JObject());
        var file = brain.Thing.Host.File;
        // The host writes the run's record a frame after it ends: wait for it, so it lands in the scratch library.
        until = Time.time + 0.5f;
        while (Time.time < until) yield return null;
        NotebookFiles.Flush();
        string recorded = (string)ArtifactRuns.Load("slalom", autopilotRun)?["outcome"];
        Expect((string)on["mode"] == "autopilot" && (string)off["mode"] == "idle" && file != null && file.Kind == "manual"
               && sandbox.host.Run.State != ArtifactRunState.Running && recorded == "aborted" && again["error"] != null,
            "voice: autopilot_on drives, autopilot_off stops the run and gives the truck back to the manual controller",
            $"on: {(string)on["mode"] ?? (string)on["error"]} ({(string)on["autopilot"]}), off: {(string)off["mode"] ?? (string)off["error"]}, " +
            $"run {recorded}, controller {file?.Name} ({file?.Kind}); off again: {(string)again["error"]}");

        // Another artifact opens: the truck gets out of the way (unseen, parked, not the host's Thing) but stays the brain
        // loop. The slalom again: it's back at the start, as the Thing.
        sandbox.host.Open("three_body");
        yield return null;
        bool seen = false;
        foreach (var r in brain.GetComponentsInChildren<Renderer>()) seen |= r.enabled;
        bool hiddenAway = brain.Hidden, releasedAway = !ReferenceEquals(sandbox.host.Thing, brain.Scored);
        bool away = hiddenAway && !seen && releasedAway && ReferenceEquals(BrainLoops.Active, brain);
        string whileAway = (string)AlbieTools.Call("brain_record", new JObject { ["start"] = true, ["laps"] = 1 })["error"];
        string reopened = Open(sandbox);
        yield return null;
        seen = false;
        foreach (var r in brain.GetComponentsInChildren<Renderer>()) seen |= r.enabled;
        float fromStart = Vector3.Distance(brain.transform.position, sandbox.host.SceneOrigin.position);
        bool back = reopened == null && !brain.Hidden && seen && ReferenceEquals(sandbox.host.Thing, brain.Scored) && fromStart < 0.05f;
        Expect(away && back && whileAway != null,
            "another artifact hides and parks the truck, and the slalom brings it back to the start",
            $"three_body: hidden {hiddenAway}, released {releasedAway}, still the brain loop; brain_record said \"{whileAway}\"; " +
            $"slalom again: {reopened ?? "open"}, hidden {brain.Hidden}, {fromStart:F2} m from the start");

        // With no brain truck running, the tools answer from the brain panel's preview (not open in this sandbox).
        brain.enabled = false;
        var preview = AlbieTools.Call("brain_status", new JObject());
        bool unregistered = BrainLoops.Active == null;
        brain.enabled = true;
        Expect(unregistered && ReferenceEquals(BrainLoops.Active, brain)
               && (((string)preview["error"])?.Contains("brain panel") == true || (bool?)preview["preview_only"] == true),
            "voice: with no brain truck the tools fall back to the brain panel's preview", Short(preview));
    }

    static int TrainingEntries()
    {
        NotebookFiles.Flush();
        int n = 0;
        foreach (var entry in NotebookStore.Entries) if ((string)entry["kind"] == "training") n++;
        return n;
    }

    static string Short(JObject o)
    {
        string text = o.ToString(Newtonsoft.Json.Formatting.None);
        return text.Length > 240 ? text.Substring(0, 240) + "…" : text;
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

    // Hand laps on a headset: a Touch controller's thumbstick (QuestStick, overridden here: no headset), and another
    // input writer (the hands transmitter) that the lap's own sources no longer overwrite every frame.
    static IEnumerator StickLaps(SlalomSandbox sandbox, SlalomBrain brain)
    {
        brain.KeysFromKeyboard = false;
        brain.KeyForward = brain.KeyLeft = 0f;

        // Full forward: straight into the first cone, at the keys' speed.
        QuestStick.Override = () => new Vector2(0f, 1f);
        string error = brain.RecordLap();
        float top = 0f, limit = Time.time + SlalomBrain.RunTimeoutS + 1f;
        while (error == null && brain.State == SlalomBrain.Mode.Lap && Time.time < limit) { top = Mathf.Max(top, brain.Body.Speed); yield return null; }
        var laps = brain.Thing.Recorder.Set.Laps;
        string outcome = laps.Count > 0 ? laps[laps.Count - 1].Outcome : null;
        Expect(error == null && brain.Body.Crashed && outcome == "crashed" && top > 0.3f && top <= SlalomBrain.KeySpeed + 0.01f,
            "a lap by thumbstick (full forward) drives the manual controller into a cone",
            $"{error ?? "lap " + outcome}, top speed {top:0.00} m/s (a full stick holds {SlalomBrain.KeySpeed} m/s)");

        // Forward and right: the truck turns right (truck_manual binds stick.x to steer.angle, negated).
        QuestStick.Override = () => new Vector2(1f, 1f);
        error = brain.RecordLap();
        var start = brain.transform.forward;
        float turned = 0f, until = Time.time + 1f;
        while (error == null && brain.State == SlalomBrain.Mode.Lap && Time.time < until)
        {
            turned = Vector3.SignedAngle(start, brain.transform.forward, Vector3.up);
            yield return null;
        }
        if (brain.State == SlalomBrain.Mode.Lap) brain.StopRecording();
        Expect(error == null && turned > 10f, "thumbstick right turns the truck right",
            error ?? $"turned {turned:0} degrees in 1 s (+ is right)");

        // Another writer holds half throttle every frame, as the transmitter would; the lap's sources leave it alone.
        QuestStick.Override = null;
        int stickY = ManualInputs.IndexOf("stick.y");
        float half = 0.5f * SlalomBrain.KeySpeed * MockTruckSim.Drag / MockTruckSim.MaxAccel;
        error = brain.RecordLap();
        top = 0f; until = Time.time + 1.5f;
        while (error == null && brain.State == SlalomBrain.Mode.Lap && Time.time < until)
        {
            ManualInputs.Values[stickY] = half;
            top = Mathf.Max(top, brain.Body.Speed);
            yield return null;
        }
        if (brain.State == SlalomBrain.Mode.Lap) brain.StopRecording();
        Expect(error == null && top > 0.1f, "another input writer (the hands transmitter) drives the lap: the keys and stick don't overwrite it",
            error ?? $"top speed {top:0.00} m/s with stick.y held at {half:0.00} by another writer");

        // The hands transmitter itself (overridden: no headset): a full pinch holds the keys' speed, a tilt to the right
        // turns the truck right through truck_manual's hand.roll (deadzone 0.08, expo 0.3).
        HandsTransmitter.Override = () => (1f, 0f);
        error = brain.RecordLap();
        top = 0f; limit = Time.time + SlalomBrain.RunTimeoutS + 1f;
        while (error == null && brain.State == SlalomBrain.Mode.Lap && Time.time < limit) { top = Mathf.Max(top, brain.Body.Speed); yield return null; }
        laps = brain.Thing.Recorder.Set.Laps;
        outcome = laps.Count > 0 ? laps[laps.Count - 1].Outcome : null;
        Expect(error == null && brain.Body.Crashed && outcome == "crashed" && top > 0.3f && top <= SlalomBrain.KeySpeed + 0.01f,
            "a lap by hands (full pinch) drives the manual controller into a cone",
            $"{error ?? "lap " + outcome}, top speed {top:0.00} m/s (a full pinch holds {SlalomBrain.KeySpeed} m/s)");

        HandsTransmitter.Override = () => (1f, 0.8f);
        error = brain.RecordLap();
        start = brain.transform.forward;
        turned = 0f; until = Time.time + 1f;
        while (error == null && brain.State == SlalomBrain.Mode.Lap && Time.time < until)
        {
            turned = Vector3.SignedAngle(start, brain.transform.forward, Vector3.up);
            yield return null;
        }
        if (brain.State == SlalomBrain.Mode.Lap) brain.StopRecording();
        HandsTransmitter.Override = null;
        Expect(error == null && turned > 10f, "a hand tilted to the right (pinky side down) turns the truck right",
            error ?? $"turned {turned:0} degrees in 1 s (+ is right)");

        var free = FreeDrive(brain);
        while (free.MoveNext()) yield return null;
        brain.KeysFromKeyboard = true;
    }

    // Between laps the stick drives the truck around (SlalomBrain.FreeDrive), as the drone flies free: no lap is recorded,
    // a cone stops it where it touched instead of freezing it, it backs away, and let go it coasts to a stop.
    static IEnumerator FreeDrive(SlalomBrain brain)
    {
        brain.PlaceAtStart();
        var start = brain.transform.position;
        int laps = brain.Thing.Recorder.Set.Laps.Count;
        QuestStick.Override = () => new Vector2(0f, 1f);
        float until = Time.time + 1f;
        while (Time.time < until) yield return null;
        float drove = Vector3.Distance(start, brain.transform.position);
        Expect(brain.State == SlalomBrain.Mode.Idle && drove > 0.2f && brain.Thing.Recorder.Set.Laps.Count == laps,
            "between laps the thumbstick drives the truck freely, recording nothing",
            $"{brain.State}, drove {drove:0.00} m in 1 s, laps {laps} > {brain.Thing.Recorder.Set.Laps.Count}");

        // Straight on into the first cone (as the lap test): stopped against it, not frozen as crashed.
        until = Time.time + 8f;
        Vector3 last = brain.transform.position;
        float stillFor = 0f;
        while (Time.time < until && stillFor < 0.5f)
        {
            yield return null;
            var now = brain.transform.position;
            stillFor = (now - last).sqrMagnitude < 1e-8f ? stillFor + Time.deltaTime : 0f;
            last = now;
        }
        var stoppedAt = brain.transform.position;
        Expect(stillFor >= 0.5f && !brain.Body.Crashed && Vector3.Distance(start, stoppedAt) > 0.3f,
            "free driving into a cone stops the truck where it touched, not crashed",
            $"still {stillFor:0.0} s at {Vector3.Distance(start, stoppedAt):0.00} m from the start, crashed {brain.Body.Crashed}");

        QuestStick.Override = () => new Vector2(0f, -1f);
        until = Time.time + 1f;
        while (Time.time < until) yield return null;
        float backed = Vector3.Distance(stoppedAt, brain.transform.position);
        Expect(backed > 0.1f, "after touching a cone the truck backs away", $"backed {backed:0.00} m in 1 s");

        QuestStick.Override = () => Vector2.zero;
        until = Time.time + 5f;
        while (Time.time < until && Mathf.Abs(brain.Body.Speed) > 0.05f) yield return null;
        Expect(Mathf.Abs(brain.Body.Speed) <= 0.05f && brain.Thing.Recorder.Set.Laps.Count == laps,
            "let go, the free-driving truck coasts to a stop", $"speed {brain.Body.Speed:0.00} m/s");
        QuestStick.Override = null;
        ManualInputs.Clear();
        brain.PlaceAtStart();
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
