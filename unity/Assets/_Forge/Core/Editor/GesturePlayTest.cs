using System;
using System.Collections.Generic;
using System.IO;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Forge > Gestures > Run Play Test: the real Main scene in Play mode on the desktop rig, with the simulated hand making
/// each gesture: real windows move, Albie really jumps, a gesture is really taught through Albie's tool. Writes a
/// result line to the log and screenshots to Logs/gestures/. Voice stays off, so it costs nothing. Batch (not -nographics,
/// or there are no screenshots):
/// <c>Unity -batchmode -projectPath unity -executeMethod GesturePlayTest.RunBatch -logFile -</c>
/// </summary>
[InitializeOnLoad]
public static class GesturePlayTest
{
    const string ActiveKey = "GesturePlayTest.Active", BatchKey = "GesturePlayTest.Batch";
    const string ScenePath = "Assets/_Forge/Core/Main.unity";
    const string ShotDir = "Logs/gestures";
    const string Backup = "gestures.playtest-backup.json";
    const double GiveUpSeconds = 120;

    static IEnumerator<float> steps;
    static double wakeAt, startedAt;
    static int passed, failed;
    static readonly List<string> failures = new List<string>();
    static bool savedForceDesktop, savedVoiceOff;

    static GesturePlayTest()
    {
        if (SessionState.GetBool(ActiveKey, false)) EditorApplication.playModeStateChanged += OnPlayMode;
    }

    public static void RunBatch()
    {
        SessionState.SetBool(BatchKey, true);
        Run();
    }

    [MenuItem("Forge/Gestures/Run Play Test")]
    public static void Run()
    {
        EditorPrefs.SetBool("GesturePlayTest.ForceDesktopWas", EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false));
        EditorPrefs.SetBool("GesturePlayTest.VoiceOffWas", EditorPrefs.GetBool(AlbieVoice.EditorOffPref, false));
        EditorPrefs.SetBool(RigSelector.ForceDesktopPref, true);
        EditorPrefs.SetBool(AlbieVoice.EditorOffPref, true);
        SessionState.SetBool(ActiveKey, true);
        EditorSceneManager.OpenScene(ScenePath);
        EditorApplication.playModeStateChanged += OnPlayMode;
        EditorApplication.isPlaying = true;
    }

    static void OnPlayMode(PlayModeStateChange change)
    {
        if (change != PlayModeStateChange.EnteredPlayMode) return;
        Directory.CreateDirectory(ShotDir);
        passed = failed = 0;
        failures.Clear();
        steps = Script();
        wakeAt = 0;
        startedAt = EditorApplication.timeSinceStartup;
        EditorApplication.update += Tick;
    }

    static void Tick()
    {
        if (EditorApplication.timeSinceStartup - startedAt > GiveUpSeconds)
        {
            Check(false, "timed out");
            Finish();
            return;
        }
        if (EditorApplication.timeSinceStartup < wakeAt) return;
        try
        {
            if (steps.MoveNext()) wakeAt = EditorApplication.timeSinceStartup + steps.Current;
            else Finish();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            Check(false, "threw " + e.Message);
            Finish();
        }
    }

    static void Finish()
    {
        EditorApplication.update -= Tick;
        EditorApplication.playModeStateChanged -= OnPlayMode;
        string summary = $"[GesturePlayTest] {passed} passed, {failed} failed";
        if (failed == 0) Debug.Log(summary);
        else Debug.LogError(summary + "\n" + string.Join("\n", failures));
        RestoreSaveFile();
        EditorPrefs.SetBool(RigSelector.ForceDesktopPref, EditorPrefs.GetBool("GesturePlayTest.ForceDesktopWas", false));
        EditorPrefs.SetBool(AlbieVoice.EditorOffPref, EditorPrefs.GetBool("GesturePlayTest.VoiceOffWas", false));
        SessionState.SetBool(ActiveKey, false);
        bool batch = SessionState.GetBool(BatchKey, false);
        EditorApplication.isPlaying = false;
        if (batch) EditorApplication.Exit(failed == 0 ? 0 : 1);
    }

    static void Check(bool condition, string what)
    {
        Debug.Log($"[GesturePlayTest] {(condition ? "ok  " : "FAIL")} {what}");
        if (condition)
        {
            passed++;
            return;
        }
        failed++;
        failures.Add(what);
    }

    static string SavePath => Path.Combine(Application.persistentDataPath, "gestures.json");

    static void SetAsideSaveFile()
    {
        string backup = Path.Combine(Application.persistentDataPath, Backup);
        if (File.Exists(SavePath)) File.Copy(SavePath, backup, true);
    }

    static void RestoreSaveFile()
    {
        string backup = Path.Combine(Application.persistentDataPath, Backup);
        if (File.Exists(backup))
        {
            File.Copy(backup, SavePath, true);
            File.Delete(backup);
        }
        else if (File.Exists(SavePath)) File.Delete(SavePath);
    }

    static void Shot(string name)
    {
        Camera cam = PetUser.HeadCamera;
        if (cam == null || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
        var rt = new RenderTexture(1280, 800, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        var previous = cam.targetTexture;
        cam.targetTexture = rt;
        cam.Render();
        cam.targetTexture = previous;
        RenderTexture.active = rt;
        var tex = new Texture2D(1280, 800, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, 1280, 800), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        File.WriteAllBytes(Path.Combine(ShotDir, name + ".png"), tex.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(rt);
        UnityEngine.Object.DestroyImmediate(tex);
    }

    static float DistanceToEyes(ForgePanel panel) =>
        Vector3.Distance(panel.transform.TransformPoint(((RectTransform)panel.transform).rect.center), PetUser.Head.position);

    static IEnumerator<float> Script()
    {
        SetAsideSaveFile();
        if (File.Exists(SavePath)) File.Delete(SavePath);

        // Sit at the desk with a view open.
        Workspace workspace = null;
        while (workspace == null || !workspace.presence.AtDesk)
        {
            yield return 0.25f;
            workspace = Workspace.Current;
            if (workspace != null) workspace.presence.forceSeated = true;  // the room may still be loading
        }
        yield return 0.5f;
        DesktopRig.Current.ToggleSeat();
        yield return 0.8f;
        workspace.Open("code");
        yield return 1.5f;

        var engine = GestureEngine.Current;
        Check(engine != null, "the gesture engine installed itself in Main");
        if (engine == null) yield break;
        var sim = engine.GetComponent<SimulatedHandSource>();
        Check(sim != null, "the desktop rig got a simulated hand");
        if (sim == null) yield break;
        GestureHand hand = engine.Hands[1];
        yield return 0.3f;
        Check(hand.Usable, "the simulated hand is tracked");

        // The display: it comes up on the first hand.
        var display = UnityEngine.Object.FindAnyObjectByType<GesturePanel>();
        Check(display != null && display.gameObject.activeSelf, "the gesture display came up when the hand appeared");

        // Pick the code view's main window and aim at it.
        ForgePanel window = null;
        foreach (var p in ForgePanel.All)
            if (p.Handles != null && p.GetComponent<GesturePanel>() == null && (window == null || p.SizeMeters.x > window.SizeMeters.x)) window = p;
        Check(window != null, "a window to point at");
        if (window == null) yield break;
        DesktopRig.Current.LookAt(window.transform.TransformPoint(((RectTransform)window.transform).rect.center));
        yield return 0.2f;
        Vector3 Center() => window.transform.TransformPoint(((RectTransform)window.transform).rect.center);
        sim.AimPoint = Center();
        yield return 0.1f;
        Shot("0_relaxed");

        sim.SetShape(HandShape.Point);
        yield return 0.5f;
        sim.AimPoint = Center();
        yield return 0.2f;
        Check(GestureLibrary.PoseName(hand.values) == "Point", "the display would read Point: " + GestureLibrary.PoseName(hand.values));
        Check(hand.Target == window, $"pointing finds the window (target {(hand.Target != null ? hand.Target.name : "none")})");
        Check(window.Handles.Aimed, "the window lights up under the aim");
        Shot("1_pointing");

        float homeDistance = DistanceToEyes(window);
        Vector3 homePosition = window.transform.position;
        sim.Flick(new Vector3(0f, 0.03f, -0.14f));
        yield return 1f;
        float nearDistance = DistanceToEyes(window);
        Check(nearDistance < homeDistance - 0.1f, $"flick toward you brings it closer ({homeDistance:F2} m to {nearDistance:F2} m)");
        Shot("2_summoned");

        sim.AimPoint = Center();
        yield return 0.8f;
        sim.Flick(new Vector3(0f, 0f, 0.16f));
        yield return 1f;
        Check(Vector3.Distance(window.transform.position, homePosition) < 0.02f, "flick away sends a summoned window back where it was");

        sim.AimPoint = Center();
        yield return 0.8f;
        Check(hand.Target == window, "still aimed at it");
        Vector3 scale = window.transform.localScale;
        sim.Flick(new Vector3(0f, 0f, 0.16f));
        yield return 1f;
        var gestureWindow = window.GetComponent<GestureWindow>();
        Check(gestureWindow != null && gestureWindow.IsMinimized && !window.gameObject.activeSelf, "flick away again minimizes it into a dot");
        Shot("3_minimized");
        var orb = UnityEngine.Object.FindAnyObjectByType<PanelOrb>();
        Check(orb != null, "the dot is there");

        // Tap the dot, as a pointer would.
        if (gestureWindow != null) gestureWindow.Restore(engine);
        yield return 0.8f;
        Check(window.gameObject.activeSelf && window.transform.localScale == scale && Vector3.Distance(window.transform.position, homePosition) < 0.02f,
              "tapping the dot opens it again, same place and size");

        // The right hand's open and close: look at the window, open the hand and close it, and it minimizes; burst the
        // fist open and it comes back.
        DesktopRig.Current.LookAt(Center());
        sim.SetShape(HandShape.Open);
        yield return 0.8f;
        sim.SetShape(HandShape.Fist);
        yield return 1f;
        Check(gestureWindow != null && gestureWindow.IsMinimized && !window.gameObject.activeSelf, "closing the right hand on a window you look at minimizes it");
        sim.SetShape(HandShape.Open);
        yield return 0.8f;
        Check(window.gameObject.activeSelf && gestureWindow != null && !gestureWindow.IsMinimized, "bursting the fist open reopens it");
        sim.SetShape(HandShape.Relaxed);
        yield return 0.5f;

        // Swapped hands: the right hand now hides every window and shows them again.
        engine.Library.Split = HandSplit.RightAll;
        sim.SetShape(HandShape.Open);
        yield return 0.8f;
        sim.SetShape(HandShape.Fist);
        yield return 1f;
        Check(!window.gameObject.activeSelf && WindowStash.HiddenCount > 0, $"with the right hand doing every window, closing it hides them all ({WindowStash.HiddenCount} hidden)");
        Shot("3b_all_hidden");
        sim.SetShape(HandShape.Open);
        yield return 0.8f;
        Check(window.gameObject.activeSelf && WindowStash.HiddenCount == 0, "and bursting it open shows them again");
        engine.Library.Split = HandSplit.LeftAll;
        sim.SetShape(HandShape.Relaxed);
        yield return 0.5f;
        if (display != null && !display.gameObject.activeSelf) engine.Actions.ShowDisplay(true);

        // Albie in the palm.
        AlbieController albie = null;
        var crew = UnityEngine.Object.FindAnyObjectByType<AlbieCrew>();
        if (crew != null) albie = crew.Lead;
        if (albie == null) albie = UnityEngine.Object.FindAnyObjectByType<AlbieController>();
        Check(albie != null, "Albie is in the scene");
        if (albie != null)
        {
            while (albie.Stationed && EditorApplication.timeSinceStartup - startedAt < GiveUpSeconds * 0.5)
            {
                albie.ReleaseStation();
                yield return 0.2f;
            }
            Vector3 albieScale = albie.transform.localScale;

            // Gator_Model.fbx is gitignored (the mesh comes from a paid pack), so a fresh checkout has no body to size.
            // A stand-in 40 cm long, the same kind of renderer, lets the fit and the screenshots still mean something.
            if (albie.GetComponentsInChildren<SkinnedMeshRenderer>().Length == 0)
            {
                Debug.Log("[GesturePlayTest] no Gator_Model in this checkout: using a stand-in body.");
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var standIn = new GameObject("StandInBody").AddComponent<SkinnedMeshRenderer>();
                standIn.transform.SetParent(albie.transform, false);
                standIn.transform.localPosition = new Vector3(0f, 0.06f, 0f);
                standIn.transform.localScale = new Vector3(0.14f, 0.12f, 0.4f);
                standIn.sharedMesh = cube.GetComponent<MeshFilter>().sharedMesh;
                standIn.sharedMaterial = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.3f, 0.7f, 0.35f) };
                standIn.localBounds = new Bounds(Vector3.zero, Vector3.one);
                UnityEngine.Object.Destroy(cube);
            }
            sim.SetShape(HandShape.Open, true);
            yield return 2.2f;
            Check(albie.Perched, "palm up calls Albie");
            Check(Vector3.Distance(albie.transform.position, hand.Palm) < 0.08f, $"he's on the palm ({Vector3.Distance(albie.transform.position, hand.Palm):F2} m off)");
            Check(albie.transform.localScale.x <= albieScale.x, "he fits");
            var body = albie.GetComponentsInChildren<SkinnedMeshRenderer>();
            Bounds size = body.Length > 0 ? body[0].bounds : new Bounds();
            foreach (var r in body) size.Encapsulate(r.bounds);
            Debug.Log($"[GesturePlayTest] Albie on the palm: scale {albie.transform.localScale.x:F3} (was {albieScale.x:F3}), {body.Length} body meshes, size {size.size}, at {albie.transform.position}, palm {hand.Palm}, state {albie.State}");
            Check(body.Length > 0 && Mathf.Max(size.size.x, size.size.z) > 0.05f && Mathf.Max(size.size.x, size.size.z) < 0.3f, $"he's palm-sized ({size.size})");
            Shot("4_albie_in_hand");

            sim.Flick(new Vector3(0.1f, 0.03f, 0f), 0.6f);
            yield return 0.3f;
            Check(Vector3.Distance(albie.transform.position, hand.Palm) < 0.08f, "he stays on the palm as it moves");
            yield return 2f;

            sim.SetShape(HandShape.Fist);
            yield return 2f;
            Check(!albie.Perched, "closing your fist sends him off");
            Check(Vector3.Distance(albie.transform.localScale, albieScale) < 0.001f, "he's back to his own size");
            Shot("5_albie_gone");
            sim.SetShape(HandShape.Relaxed);
            yield return 0.5f;

            // The other way down: turning the hand over.
            sim.SetShape(HandShape.Open, true);
            yield return 2.2f;
            Check(albie.Perched, "palm up calls him again");
            sim.SetShape(HandShape.Open);
            yield return 1.2f;
            Check(!albie.Perched, "turning your hand over sends him off too");
            yield return 1f;
        }

        // Teach a gesture through Albie's tool, as he would when asked: peace sign opens CAD.
        sim.SetShape(HandShape.Relaxed);
        yield return 0.4f;
        JObject taught = null;
        AlbieTools.CallAsync("gesture_teach", new JObject
        {
            ["name"] = "peace sign",
            ["action"] = "tool:ui_open_view",
            ["args"] = new JObject { ["view"] = "cad" },
        }, result => taught = result);
        Check(engine.TeachPrompt != null, "Albie's watching: " + engine.TeachPrompt);
        yield return 1f;
        Shot("6_teaching");
        while (taught == null)
        {
            string prompt = engine.TeachPrompt ?? "";
            if (prompt.StartsWith("Show me", StringComparison.Ordinal)) sim.SetShape(HandShape.Peace);
            else sim.SetShape(HandShape.Relaxed);
            yield return 0.05f;
        }
        Debug.Log("[GesturePlayTest] gesture_teach said: " + taught.ToString(Newtonsoft.Json.Formatting.None));
        Check(taught["ok"] != null, "teaching worked: " + taught);
        Check(File.Exists(SavePath), "it's saved to gestures.json");
        sim.SetShape(HandShape.Relaxed);
        yield return 0.6f;
        Check(workspace.ActiveMode == "code", "still on the code view");
        sim.SetShape(HandShape.Peace);
        yield return 1f;
        Check(workspace.ActiveMode == "cad", $"the taught gesture opens CAD (view is {workspace.ActiveMode})");
        Shot("7_taught_gesture_fired");
        if (display != null) PanelShot.Save(GesturePanel.PanelId, Path.Combine(ShotDir, "8_display.png"), 1400);

        Debug.Log($"[GesturePlayTest] average {Time.frameCount / Mathf.Max(1f, Time.time):F0} fps");
        var list = AlbieTools.Call("gesture_list", new JObject());
        Check(list["gestures"] is JArray all && all.Count == 6, "gesture_list reports 5 built-ins and the new one");
        var refused = AlbieTools.Call("gesture_bind", new JObject { ["name"] = "peace sign", ["action"] = "tool:messages_confirm" });
        Check(refused["error"] != null, "a gesture can't be bound to confirming a send");
    }
}
