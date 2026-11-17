using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Forge.Demo.Editor
{
    /// <summary>
    /// Forge > Demo > Play Whole Script: DemoSandbox in Play mode on the desktop rig with the voice off. The Director plays B0 to
    /// B7 on its own (every beat moves on 4 s after its lines and cues are done; the palm is "held out" with the ` key), a
    /// screenshot of each beat goes to unity/Logs/demo/director_B*.png, then the laptop remote is tried through
    /// ForgeNet.Inject (reset, next, goto, back) and a spoken line through stt.final. The verdict is in
    /// unity/Logs/demo/director_result.txt and the console ("[DemoPlayTest] ..."). Takes Temp/demo-editor.lock while it runs.
    /// Poll with <see cref="Running"/> / <see cref="LastResult"/>.
    /// </summary>
    [InitializeOnLoad]
    public static class DemoPlayTest
    {
        const string ActiveKey = "DemoPlayTest.Active", ResultKey = "DemoPlayTest.Result";
        const string ForceDesktopHad = "DemoPlayTest.ForceDesktopHad", ForceDesktopWas = "DemoPlayTest.ForceDesktopWas";
        const string VoiceOffHad = "DemoPlayTest.VoiceOffHad", VoiceOffWas = "DemoPlayTest.VoiceOffWas";
        const string Station = "demo-director";
        const string Dir = "Logs/demo";
        const double GiveUpSeconds = 480;

        static IEnumerator<float> steps;
        static double wakeAt, startedAt;
        static int passed, failed;
        static readonly List<string> failures = new List<string>();
        static readonly List<string> logged = new List<string>();
        static readonly HashSet<string> missing = new HashSet<string>();

        static DemoPlayTest()
        {
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        public static bool Running => SessionState.GetBool(ActiveKey, false);
        public static string LastResult => SessionState.GetString(ResultKey, "");

        [MenuItem("Forge/Demo/Play Whole Script")]
        public static void Run()
        {
            if (Running) { Debug.LogError("[DemoPlayTest] a run is already in progress."); return; }
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogError("[DemoPlayTest] leave Play mode first."); return; }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (!DemoLock.TryAcquire(Station)) return;

            if (!File.Exists(DemoSandboxBuilder.ScenePath)) DemoSandboxBuilder.Build();
            if (!File.Exists(DemoSandboxBuilder.ScenePath)) { DemoLock.Release(Station); return; }

            SavePref(RigSelector.ForceDesktopPref, ForceDesktopHad, ForceDesktopWas);
            SavePref(AlbieVoice.EditorOffPref, VoiceOffHad, VoiceOffWas);
            EditorPrefs.SetBool(RigSelector.ForceDesktopPref, true);
            EditorPrefs.SetBool(AlbieVoice.EditorOffPref, true);

            SessionState.SetBool(ActiveKey, true);
            SessionState.SetString(ResultKey, "running");
            EditorSceneManager.OpenScene(DemoSandboxBuilder.ScenePath);
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
                if (failed == 0 && passed == 0) Check(false, "Play mode ended before the test started");
                Finish(false);
            }
        }

        static void Begin()
        {
            Directory.CreateDirectory(Dir);
            foreach (string old in Directory.GetFiles(Dir, "director_*"))
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
            text.Append(failed == 0 ? "PASS" : "FAIL").Append(" demo director: ").Append(passed).Append(" passed, ").Append(failed).Append(" failed\n");
            for (int i = 0; i < failures.Count; i++) text.Append("  ").Append(failures[i]).Append('\n');
            if (missing.Count > 0) text.Append("pieces with no handler (not built yet, or not in the sandbox): ").Append(string.Join(", ", missing)).Append('\n');
            if (logged.Count > 0) text.Append("errors and exceptions logged during the run:\n  ").Append(string.Join("\n  ", logged)).Append('\n');
            string result = text.ToString();
            SessionState.SetString(ResultKey, result);
            try { File.WriteAllText(Path.Combine(Dir, "director_result.txt"), result); } catch (IOException) { }
            if (failed == 0) Debug.Log("[DemoPlayTest] " + result); else Debug.LogError("[DemoPlayTest] " + result);

            RestorePref(RigSelector.ForceDesktopPref, ForceDesktopHad, ForceDesktopWas);
            RestorePref(AlbieVoice.EditorOffPref, VoiceOffHad, VoiceOffWas);
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
                if (message.StartsWith("[DemoPlayTest]", StringComparison.Ordinal)) return;
                string first = message.Split('\n')[0];
                if (logged.Count < 12) logged.Add(type + ": " + first);
            }
        }

        static void Check(bool condition, string what)
        {
            Debug.Log($"[DemoPlayTest] {(condition ? "ok  " : "FAIL")} {what}");
            if (condition) { passed++; return; }
            failed++;
            failures.Add(what);
        }

        // ---------------------------------------------------------------- the run

        static IEnumerator<float> Script()
        {
            double end = EditorApplication.timeSinceStartup + 20;
            while ((DemoDirector.Current == null || DemoDirector.Current.Voice == null) && EditorApplication.timeSinceStartup < end) yield return 0.2f;
            DemoDirector d = DemoDirector.Current;
            Check(d != null && d.Voice != null, "the Director is in the scene and found Albie");
            if (d == null || d.Voice == null) yield break;
            Check(d.Script != null && d.Script.beats.Count == 9, "demo_script.json has B0 to B7 plus B5b (" + (d.Script != null ? d.Script.beats.Count : 0) + " beats)");
            Check(d.Armed, "armed at the start, nothing playing");
            yield return 3f;   // the NavMesh and Albie's intro settle

            // ---- the whole script, on its own
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
                    Shot("director_" + d.BeatId);
                }
                yield return 0.1f;
            }
            d.BeatStarted -= onBeat;
            Check(d.Finished, "the script ran to the end");
            Check(started.Count == d.Script.beats.Count, "every beat started once: " + string.Join(" ", started));
            Check(palm, "B1 waited for the palm and Albie landed on it (albie.on_hand)");
            yield return 1f;
            Shot("director_end");

            // ---- a recorded line: the clip plays and the mouth follows its loudness (no WAVs exist until the content station records them)
            d.forceAdvanceSeconds = 0f;
            const int rate = 24000;
            var tone = AudioClip.Create("demo_test_tone", rate * 2, 1, rate, false);
            var data = new float[rate * 2];
            for (int i = 0; i < data.Length; i++)
                data[i] = Mathf.Sin(2f * Mathf.PI * 220f * i / rate) * 0.5f * Mathf.Abs(Mathf.Sin(i * Mathf.PI * 3f / rate));
            tone.SetData(data, 0);
            d.Voice.Say("A recorded line, with its audio.", tone, "demo_test_tone");
            float peak = 0f;
            double toneEnd = EditorApplication.timeSinceStartup + 4;
            while (d.Voice.Speaking && EditorApplication.timeSinceStartup < toneEnd)
            {
                peak = Mathf.Max(peak, d.Voice.Level);
                yield return 0.05f;
            }
            Check(!d.Voice.Speaking, "a recorded line ends when its clip does");
            Check(peak > 0.05f, "a recorded line: the mouth follows the clip's loudness (peak " + peak.ToString("F2") + ")");

            // ---- the laptop remote
            ForgeNet.Inject("demo.reset", new JObject());
            yield return 0.6f;
            Check(d.Armed, "demo.reset: armed at B0 again");
            ForgeNet.Inject("demo.next", new JObject());
            yield return 0.6f;
            Check(d.BeatId == "B0" && d.BeatRunning, "demo.next from armed starts B0 (" + d.BeatId + ")");
            ForgeNet.Inject("demo.goto", new JObject { ["beat"] = "B3" });
            yield return 3f;
            Check(d.BeatId == "B3" && d.BeatRunning, "demo.goto B3 runs the earlier cues and starts B3 (" + d.BeatId + ")");
            ForgeNet.Inject("demo.back", new JObject());
            yield return 3f;
            Check(d.BeatId == "B2" && d.BeatRunning, "demo.back goes to B2 (" + d.BeatId + ")");
            ForgeNet.Inject("demo.next", new JObject());
            yield return 1.5f;
            Check(d.BeatId == "B3" && d.BeatRunning, "demo.next from B2 goes to B3 (" + d.BeatId + ")");

            // ---- Hudson's voice moves a beat on (B1 waits for the palm, then Albie's line, then Hudson's sentence)
            ForgeNet.Inject("demo.goto", new JObject { ["beat"] = "B1" });
            yield return 2.5f;
            d.PressPalm();
            double wait = EditorApplication.timeSinceStartup + 20;
            while (EditorApplication.timeSinceStartup < wait && !(d.BeatId == "B1" && d.LinesDone)) yield return 0.2f;
            Check(d.BeatId == "B1" && d.LinesDone, "B1: Albie landed on the hand and said his line (albie.on_hand)");
            Shot("director_B1_landed");
            yield return 1.3f;   // past the echo guard
            ForgeNet.Inject("stt.final", new JObject { ["text"] = "Let's open up the drone we built yesterday.", ["voice"] = true });
            yield return 2.5f;
            Check(d.BeatId == "B2" && d.BeatRunning, "a final transcript after Albie's line moves B1 on to B2 (" + d.BeatId + ")");
            ForgeNet.Inject("stt.final", new JObject { ["text"] = "um", ["voice"] = true });
            yield return 0.5f;
            Check(d.BeatId == "B2", "a one-word transcript does not advance (" + d.BeatId + ")");
            Shot("director_B2_after_speech");

            ForgeNet.Inject("demo.reset", new JObject());
            yield return 0.6f;
            Check(d.Armed && !d.Voice.Speaking, "final reset: armed, Albie silent");
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
            Debug.Log("[DemoPlayTest] shot " + path);
        }

        static void SavePref(string pref, string hadKey, string valueKey)
        {
            EditorPrefs.SetBool(hadKey, EditorPrefs.HasKey(pref));
            EditorPrefs.SetBool(valueKey, EditorPrefs.GetBool(pref, false));
        }

        static void RestorePref(string pref, string hadKey, string valueKey)
        {
            if (!EditorPrefs.HasKey(hadKey)) return;
            if (EditorPrefs.GetBool(hadKey, false)) EditorPrefs.SetBool(pref, EditorPrefs.GetBool(valueKey, false));
            else if (EditorPrefs.HasKey(pref)) EditorPrefs.DeleteKey(pref);
            EditorPrefs.DeleteKey(hadKey);
            EditorPrefs.DeleteKey(valueKey);
        }
    }
}
