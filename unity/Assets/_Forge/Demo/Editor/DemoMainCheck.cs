using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Forge.OS;
using Forge.OS.Editor;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Forge.Demo.Editor
{
    /// <summary>
    /// Forge > Demo > Check Main: Main.unity in Play mode on the desktop rig with demo mode on (Resources/demo_config.json).
    /// Checks that the Director and the four pieces (research, pipeline, drone, drone cam) install at runtime, that the OS menu
    /// opens before and after the demo, and plays B0 to B7 (every beat moves on 4 s after its lines and cues; the palm is held
    /// out with PressPalm). Screenshots go to unity/Logs/demo/main_*.png, the verdict to unity/Logs/demo/main_result.txt.
    /// </summary>
    [InitializeOnLoad]
    public static class DemoMainCheck
    {
        const string ActiveKey = "DemoMainCheck.Active", ResultKey = "DemoMainCheck.Result";
        const string Station = "integrate";
        const string Dir = "Logs/demo";
        const string MainPath = "Assets/_Forge/Core/Main.unity";
        const double GiveUpSeconds = 420;

        static IEnumerator<float> steps;
        static double wakeAt, startedAt;
        static int passed, failed;
        static readonly List<string> failures = new List<string>();
        static readonly List<string> logged = new List<string>();
        static readonly HashSet<string> missing = new HashSet<string>();

        static DemoMainCheck()
        {
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        public static bool Running => SessionState.GetBool(ActiveKey, false);
        public static string LastResult => SessionState.GetString(ResultKey, "");

        [MenuItem("Forge/Demo/Check Main")]
        public static void Run()
        {
            if (Running) { Debug.LogError("[DemoMainCheck] a run is already in progress."); return; }
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogError("[DemoMainCheck] leave Play mode first."); return; }
            if (!DemoConfig.Load().enabled) { Debug.LogError("[DemoMainCheck] demo mode is off (Forge > Demo > Toggle Demo Mode)."); return; }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (!DemoLock.TryAcquire(Station)) return;

            OSTestEnv.Enter("DemoMainCheck");
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetString(ResultKey, "running");
            EditorSceneManager.OpenScene(MainPath);
            EditorApplication.isPlaying = true;
        }

        static void OnPlayMode(PlayModeStateChange change)
        {
            if (!Running) return;
            if (change == PlayModeStateChange.EnteredPlayMode) Begin();
            else if (change == PlayModeStateChange.EnteredEditMode)
            {
                EditorApplication.update -= Tick;
                Application.logMessageReceived -= OnLog;
                if (failed == 0 && passed == 0) Check(false, "Play mode ended before the check started");
                Finish(false);
            }
        }

        static void Begin()
        {
            Directory.CreateDirectory(Dir);
            foreach (string old in Directory.GetFiles(Dir, "main_*"))
            {
                try { File.Delete(old); } catch (IOException) { }
            }
            passed = failed = 0;
            failures.Clear();
            logged.Clear();
            missing.Clear();
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
            steps = Script();
            wakeAt = 0;
            startedAt = EditorApplication.timeSinceStartup;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup - startedAt > GiveUpSeconds)
            {
                Check(false, "timed out after " + GiveUpSeconds + " s");
                Finish(true);
                return;
            }
            if (EditorApplication.timeSinceStartup < wakeAt) return;
            try
            {
                if (steps.MoveNext()) wakeAt = EditorApplication.timeSinceStartup + steps.Current;
                else Finish(true);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Check(false, "the script threw " + e.Message);
                Finish(true);
            }
        }

        static void Finish(bool stopPlay)
        {
            EditorApplication.update -= Tick;
            Application.logMessageReceived -= OnLog;
            var d = DemoDirector.Current;
            if (d != null) d.forceAdvanceSeconds = 0f;

            var text = new StringBuilder();
            text.Append(failed == 0 ? "PASS" : "FAIL").Append(" demo on Main: ").Append(passed).Append(" passed, ").Append(failed).Append(" failed\n");
            for (int i = 0; i < failures.Count; i++) text.Append("  ").Append(failures[i]).Append('\n');
            if (missing.Count > 0) text.Append("actions with no handler: ").Append(string.Join(", ", missing)).Append('\n');
            if (logged.Count > 0) text.Append("errors and exceptions logged during the run:\n  ").Append(string.Join("\n  ", logged)).Append('\n');
            string result = text.ToString();
            SessionState.SetString(ResultKey, result);
            try { File.WriteAllText(Path.Combine(Dir, "main_result.txt"), result); } catch (IOException) { }
            if (failed == 0) Debug.Log("[DemoMainCheck] " + result); else Debug.LogError("[DemoMainCheck] " + result);

            OSTestEnv.Leave();
            SessionState.SetBool(ActiveKey, false);
            DemoLock.Release(Station);
            if (stopPlay) EditorApplication.isPlaying = false;
        }

        static void OnLog(string message, string stack, LogType type)
        {
            if (message.StartsWith("[Demo] no handler for '", StringComparison.Ordinal))
            {
                missing.Add(message.Substring(23).TrimEnd('\''));
                return;
            }
            if (type == LogType.Exception || type == LogType.Error)
            {
                if (message.StartsWith("[DemoMainCheck]", StringComparison.Ordinal)) return;
                string first = message.Split('\n')[0];
                if (logged.Count < 12) logged.Add(type + ": " + first);
            }
        }

        static void Check(bool condition, string what)
        {
            Debug.Log($"[DemoMainCheck] {(condition ? "ok  " : "FAIL")} {what}");
            if (condition) { passed++; return; }
            failed++;
            failures.Add(what);
        }

        static IEnumerator<float> Script()
        {
            double end = EditorApplication.timeSinceStartup + 45;
            while ((DemoDirector.Current == null || DemoDirector.Current.Voice == null) && EditorApplication.timeSinceStartup < end) yield return 0.3f;
            DemoDirector d = DemoDirector.Current;
            Check(d != null && d.Voice != null, "demo mode on: the Director installed itself on Main and found Albie");
            if (d == null || d.Voice == null) yield break;
            Check(UnityEngine.Object.FindAnyObjectByType<ResearchStation>(FindObjectsInactive.Include) != null, "the research station is in Main");
            Check(UnityEngine.Object.FindAnyObjectByType<PipelineDemo>(FindObjectsInactive.Include) != null, "the pipeline station is in Main");
            Check(UnityEngine.Object.FindAnyObjectByType<DemoDrone>(FindObjectsInactive.Include) != null, "the drone is in Main");
            Check(UnityEngine.Object.FindAnyObjectByType<DroneCamWindow>(FindObjectsInactive.Include) != null, "the drone-cam window is in Main");
            Check(d.Armed, "armed at the start, nothing playing");
            yield return 6f;   // the OS, the NavMesh and Albie's arrival settle

            // ---- the OS menu before the demo
            var shell = UnityEngine.Object.FindAnyObjectByType<OSShell>();
            var menu = UnityEngine.Object.FindAnyObjectByType<OSMenu>(FindObjectsInactive.Include);
            Check(shell != null && menu != null, "the OS shell and menu are in Main");
            if (shell != null && menu != null)
            {
                shell.OpenMenu();
                yield return 2.5f;
                Check(menu.Visible, "the OS menu opens with demo mode on, before the demo");
                Shot("main_os_before");
                shell.CloseMenu();
                yield return 1.5f;
            }

            // ---- the whole script on its own
            d.forceAdvanceSeconds = 4f;
            var started = new List<string>();
            Action<int> onBeat = i => started.Add(d.Script.beats[i].id);
            d.BeatStarted += onBeat;
            d.Next();
            bool palm = false;
            int shotBeat = -1;
            double giveUp = EditorApplication.timeSinceStartup + 300;
            while (!d.Finished && EditorApplication.timeSinceStartup < giveUp)
            {
                if (!palm && d.BeatId == "B1" && d.BeatSeconds > 1.5f) { palm = true; d.PressPalm(); }
                if (d.BeatRunning && d.BeatIndex != shotBeat && d.BeatSeconds >= 3.5f)
                {
                    shotBeat = d.BeatIndex;
                    Shot("main_" + d.BeatId);
                }
                yield return 0.1f;
            }
            d.BeatStarted -= onBeat;
            d.forceAdvanceSeconds = 0f;
            Check(d.Finished, "the script ran to the end on Main");
            Check(started.Count == d.Script.beats.Count, "every beat started once: " + string.Join(" ", started));
            yield return 1f;
            Shot("main_end");

            // ---- reset, then the OS menu again
            ForgeNet.Inject("demo.reset", new JObject());
            yield return 2f;
            Check(d.Armed && !d.Voice.Speaking, "demo.reset: armed at B0, Albie silent");
            if (shell != null && menu != null)
            {
                shell.OpenMenu();
                yield return 2.5f;
                Check(menu.Visible, "the OS menu opens after the demo and a reset");
                Shot("main_os_after");
                shell.CloseMenu();
                yield return 1f;
            }
        }

        static void Shot(string name)
        {
            Camera cam = PetUser.HeadCamera;
            if (cam == null || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            const int w = 1280, h = 800;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            RenderTexture previousTarget = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = previousTarget;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            UnityEngine.Object.DestroyImmediate(rt);
            string path = Path.GetFullPath(Path.Combine(Dir, name + ".png"));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            Debug.Log("[DemoMainCheck] shot " + path);
        }
    }
}
