using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Forge.Demo
{
    /// <summary>
    /// The Research station's test bench (Demo/Sandboxes/ResearchSandbox.unity, built by Forge > Demo > Research > Build Sandbox).
    /// By hand: 1 open, 2 cameras, 3 models, 4 verdict, 5 poke "Add to parts list", 6 close, 7 reset (the desktop rig's mouse
    /// also pokes the button). The Play Test menu item sets the editor's "ResearchPlayTest" session flag; then this runs the whole
    /// story through <see cref="DemoActions.Fire"/>, a simulated poke included, saves screenshots to Logs/demo/research_*.png
    /// and leaves Play mode. Mocks only, no hub.
    /// </summary>
    public sealed class ResearchSandbox : MonoBehaviour
    {
        public const string PlayTestFlag = "ResearchPlayTest";
        public const string LockPath = "Temp/demo-editor.lock";

        readonly HashSet<string> signals = new HashSet<string>();
        readonly List<string> report = new List<string>();
        Camera cam;
        string shotDir;

        void OnEnable() => DemoActions.Signal += OnSignal;
        void OnDisable() => DemoActions.Signal -= OnSignal;

        void OnSignal(string signal) => signals.Add(signal);

        void Start()
        {
            cam = Camera.main;
            DemoLayout.Capture();
            shotDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "demo"));
#if UNITY_EDITOR
            if (UnityEditor.SessionState.GetBool(PlayTestFlag, false)) StartCoroutine(PlayTest());
#endif
        }

        void Update()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null) return;
            if (kb.digit1Key.wasPressedThisFrame) DemoActions.Fire("research.open");
            if (kb.digit2Key.wasPressedThisFrame) DemoActions.Fire("research.cameras");
            if (kb.digit3Key.wasPressedThisFrame) DemoActions.Fire("research.models");
            if (kb.digit4Key.wasPressedThisFrame) DemoActions.Fire("research.verdict");
            if (kb.digit5Key.wasPressedThisFrame && ResearchStation.Instance != null) ResearchStation.Instance.PokeAdd();
            if (kb.digit6Key.wasPressedThisFrame) DemoActions.Fire("research.close");
            if (kb.digit7Key.wasPressedThisFrame) DemoActions.Fire(DemoActions.Reset);
        }

        // ------------------------------------------------------------------ the play test

        IEnumerator WaitFor(string signal, float timeout)
        {
            float t = 0f;
            while (!signals.Contains(signal) && t < timeout)
            {
                t += Time.deltaTime;
                yield return null;
            }
            Check(signals.Contains(signal), "signal " + signal + (signals.Contains(signal) ? "" : " never came"));
        }

        void Check(bool ok, string what)
        {
            report.Add((ok ? "PASS " : "FAIL ") + what);
        }

        IEnumerator PlayTest()
        {
            Application.logMessageReceived += OnLog;
            yield return new WaitForSeconds(0.6f);
            ResearchStation station = ResearchStation.Instance;
            Check(station != null, "station is in the scene");
            Check(DemoActions.Has("research.open") && DemoActions.Has("research.cameras") && DemoActions.Has("research.models")
                && DemoActions.Has("research.verdict") && DemoActions.Has("research.close"), "all research actions are registered");

            float started = Time.time;
            DemoActions.Fire("research.open");
            yield return new WaitForSeconds(1.2f);
            yield return Shoot("01_open", 1700, 1000, 90f, null);

            DemoActions.Fire("research.cameras");
            yield return new WaitForSeconds(2.6f);
            yield return Shoot("02_typing", 1700, 1000, 90f, null);
            yield return new WaitForSeconds(4.2f);
            DemoActions.Fire("research.models");
            yield return new WaitForSeconds(0.8f);
            yield return Shoot("03_cards_streaming", 1700, 1000, 90f, null);
            yield return WaitFor("research.cameras.done", 30f);
            yield return WaitFor("research.models.done", 30f);
            float elapsedSearch = Time.time - started;
            report.Add("INFO cameras + models finished " + elapsedSearch.ToString("0.0") + " s after open");
            yield return new WaitForSeconds(0.4f);
            yield return Shoot("04_both_panels", 1700, 1000, 90f, null);
            yield return Shoot("05_left_closeup", 1200, 1500, 62f, station != null ? station.LeftPanel : null);
            yield return Shoot("06_right_closeup", 1200, 1500, 62f, station != null ? station.RightPanel : null);

            DemoActions.Fire("research.verdict");
            yield return new WaitForSeconds(0.18f);
            yield return Shoot("07_verdict_lifting", 1700, 1000, 90f, null);
            yield return WaitFor("research.verdict.shown", 15f);
            yield return new WaitForSeconds(0.3f);
            yield return Shoot("08_verdict", 1700, 1000, 90f, null);
            yield return Shoot("09_verdict_closeup", 1200, 1500, 62f, station != null ? station.VerdictPanel : null);
            Vector3 verdictPos = ResearchStation.VerdictPosition;
            report.Add("INFO VerdictPosition " + verdictPos.ToString("0.00"));
            Check(station != null && station.VerdictPanel != null && Vector3.Distance(station.VerdictPanel.position, verdictPos) < 0.01f, "VerdictPosition is where the card sits");

            bool poked = station != null && station.PokeAdd();
            Check(poked, "poke on Add to parts list was accepted");
            yield return new WaitForSeconds(0.35f);
            yield return Shoot("10_added_pressed", 1700, 1000, 90f, null);
            yield return WaitFor("research.added", 15f);
            yield return new WaitForSeconds(0.2f);
            yield return Shoot("11_added_done", 1700, 1000, 90f, null);

            DemoActions.Fire("research.close");
            yield return new WaitForSeconds(0.15f);
            yield return Shoot("12_closing", 1700, 1000, 90f, null);
            yield return new WaitForSeconds(0.6f);
            Check(station != null && station.LeftPanel == null && station.RightPanel == null, "close removes the panels");

            // A second run after reset: the actions must work again from the start.
            DemoActions.Fire(DemoActions.Reset);
            signals.Clear();
            DemoActions.Fire("research.cameras");
            yield return WaitFor("research.cameras.done", 30f);
            DemoActions.Fire("research.verdict");
            yield return WaitFor("research.verdict.shown", 15f);
            Check(station != null && station.PokeAdd(), "second run: poke accepted");
            yield return WaitFor("research.added", 15f);
            DemoActions.Fire(DemoActions.Reset);
            yield return null;
            Check(station != null && station.LeftPanel == null, "reset removes everything");

            Application.logMessageReceived -= OnLog;
            Finish();
        }

        int errors;

        void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                if (message.StartsWith("[MetaXRFeature]", System.StringComparison.Ordinal)) return;
                errors++;
                report.Add("FAIL logged " + type + ": " + message);
            }
        }

        void Finish()
        {
            Check(errors == 0, "no errors or exceptions were logged");
            Directory.CreateDirectory(shotDir);
            string path = Path.Combine(shotDir, "research_report.txt");
            File.WriteAllLines(path, report);
            int failed = 0;
            for (int i = 0; i < report.Count; i++) if (report[i].StartsWith("FAIL", System.StringComparison.Ordinal)) failed++;
            Debug.Log("[ResearchSandbox] play test done: " + failed + " failed. " + path + "\n" + string.Join("\n", report));
#if UNITY_EDITOR
            UnityEditor.SessionState.SetBool(PlayTestFlag, false);
            try
            {
                string lockFile = Path.GetFullPath(Path.Combine(Application.dataPath, "..", LockPath));
                if (File.Exists(lockFile) && File.ReadAllText(lockFile).Contains("demo-research")) File.Delete(lockFile);
            }
            catch (System.Exception) { }
            UnityEditor.EditorApplication.isPlaying = false;
#endif
        }

        // ------------------------------------------------------------------ screenshots

        /// <summary>Renders the head camera (optionally turned to look at a panel, with another field of view) to Logs/demo/research_{name}.png.</summary>
        IEnumerator Shoot(string name, int width, int height, float fov, Transform focus)
        {
            yield return null;
            if (cam == null || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) yield break;
            Transform ct = cam.transform;
            Quaternion rotation = ct.rotation;
            float savedFov = cam.fieldOfView;
            RenderTexture previous = cam.targetTexture;
            if (focus != null) ct.rotation = Quaternion.LookRotation(focus.position - ct.position, Vector3.up);
            cam.fieldOfView = fov;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = previous;
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            Directory.CreateDirectory(shotDir);
            File.WriteAllBytes(Path.Combine(shotDir, "research_" + name + ".png"), tex.EncodeToPNG());
            Destroy(tex);
            Destroy(rt);
            ct.rotation = rotation;
            cam.fieldOfView = savedFov;
        }
    }
}
