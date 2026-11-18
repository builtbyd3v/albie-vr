using System.Collections;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.Universal;

namespace Forge.Demo.PipelineTools
{
    /// <summary>
    /// Forge > Demo > Pipeline > Play Test: opens PipelineSandbox, enters Play, fires open, pull, train and push through
    /// <see cref="DemoActions"/> the way the Director does, simulates a pointer click on Confirm, then closes, and runs a quick second
    /// round that cancels the first confirm. Screenshots of the head's view of the panel go to Logs/demo/pipeline_*.png and the verdict
    /// to Logs/demo/pipeline_result.txt (and the console: "[PipelineTest] PASS" or "FAIL"). Take unity/Temp/demo-editor.lock first.
    /// </summary>
    [InitializeOnLoad]
    public static class PipelinePlayTest
    {
        const string RunKey = "PipelineTest.Run";
        public const string Dir = "Logs/demo";

        static PipelinePlayTest()
        {
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        [MenuItem("Forge/Demo/Pipeline/Play Test")]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogWarning("[PipelineTest] Stop Play mode first."); return; }
            if (!File.Exists(PipelineSandboxBuilder.ScenePath)) PipelineSandboxBuilder.Build();
            else if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(PipelineSandboxBuilder.ScenePath);
            Directory.CreateDirectory(Dir);
            SessionState.SetBool(RunKey, true);
            EditorApplication.EnterPlaymode();
        }

        static void OnPlayMode(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(RunKey, false))
            {
                SessionState.SetBool(RunKey, false);
                new GameObject("PipelinePlayTestRunner").AddComponent<Runner>();
            }
        }

        sealed class Runner : MonoBehaviour
        {
            readonly StringBuilder log = new StringBuilder();
            int failures;
            string signalSeen = "";
            Camera shotCamera;

            void OnEnable() => DemoActions.Signal += OnSignal;
            void OnDisable() => DemoActions.Signal -= OnSignal;
            void OnSignal(string s) => signalSeen += s + ";";

            IEnumerator Start()
            {
                yield return Test();
                string verdict = (failures == 0 ? "PASS" : "FAIL") + " pipeline play test: " + failures + " failed";
                log.Insert(0, verdict + "\n");
                File.WriteAllText(Path.Combine(Dir, "pipeline_result.txt"), log.ToString());
                Debug.Log("[PipelineTest] " + verdict + "\n" + log);
                yield return null;
                EditorApplication.isPlaying = false;
            }

            IEnumerator Test()
            {
                yield return new WaitForSeconds(1.0f);
                var demo = FindFirstObjectByType<PipelineDemo>();
                Check(demo != null, "the station is in the scene");
                if (demo == null) yield break;
                Check(DemoActions.Has(PipelineDemo.Open) && DemoActions.Has(PipelineDemo.Pull) && DemoActions.Has(PipelineDemo.Train)
                    && DemoActions.Has(PipelineDemo.Push) && DemoActions.Has(PipelineDemo.Close) && DemoActions.Has(DemoActions.Reset), "all actions registered");
                Check(demo.View != null, "the panel is built before the first beat");
                DemoLayout.Capture();

                // Round one: the real timings.
                DemoActions.Fire(PipelineDemo.Open);
                yield return new WaitForSeconds(1.0f);
                Shot("01_open");
                DemoActions.Fire(PipelineDemo.Pull);
                yield return new WaitForSeconds(1.8f);
                Shot("02_pull_mid");
                yield return WaitFor("pipeline.pulled", 8f);
                Check(signalSeen.Contains("pipeline.pulled"), "pipeline.pulled raised");
                yield return new WaitForSeconds(0.5f);
                Shot("03_pull_done");

                DemoActions.Fire(PipelineDemo.Train);
                yield return new WaitForSeconds(2.6f);
                Shot("04_train_early");
                yield return new WaitForSeconds(4.2f);
                Shot("05_train_mid");
                yield return WaitFor("pipeline.trained", 12f);
                Check(signalSeen.Contains("pipeline.trained"), "pipeline.trained raised");
                yield return new WaitForSeconds(0.4f);
                Shot("06_train_done");

                DemoActions.Fire(PipelineDemo.Push);
                yield return new WaitForSeconds(1.2f);
                Check(demo.WaitingForConfirm, "push waits for Confirm");
                Check(!signalSeen.Contains("pipeline.pushed"), "nothing pushed before Confirm");
                Shot("07_push_confirm");
                foreach (var b in new[] { demo.View.CancelButton, demo.View.ConfirmButton })
                {
                    var root = (RectTransform)b.transform;
                    log.AppendLine("info  " + b.name + " root " + root.rect.width * root.lossyScale.x * 1000f + " x " + root.rect.height * root.lossyScale.y * 1000f + " mm in the world");
                    foreach (var g in b.GetComponentsInChildren<UnityEngine.UI.Graphic>())
                        if (g.raycastTarget && !(g is TMPro.TMP_Text)) log.AppendLine("info    target " + g.name + " " + g.rectTransform.rect.width * g.transform.lossyScale.x * 1000f + " x " + g.rectTransform.rect.height * g.transform.lossyScale.y * 1000f + " mm in the world");
                }
                Press(demo.View.ConfirmButton, "Confirm");
                yield return new WaitForSeconds(1.7f);
                Shot("08_push_upload");
                yield return WaitFor("pipeline.pushed", 8f);
                Check(signalSeen.Contains("pipeline.pushed"), "pipeline.pushed raised after Confirm");
                yield return new WaitForSeconds(0.9f);
                Shot("09_push_commit");
                DemoActions.Fire(PipelineDemo.Close);
                yield return new WaitForSeconds(0.8f);
                Check(!demo.IsOpen, "closed");

                // Round two, fast: Cancel closes the card and sends nothing, then Confirm after asking again does push.
                DemoActions.Fire(DemoActions.Reset);
                yield return null;
                signalSeen = "";
                demo.pullSeconds = 1.2f;
                demo.trainSeconds = 2.2f;
                demo.uploadSeconds = 0.8f;
                DemoActions.Fire(PipelineDemo.Open);
                DemoActions.Fire(PipelineDemo.Push);  // asked for early: pull and train play first
                yield return new WaitForSeconds(5.5f);
                Check(demo.WaitingForConfirm, "a push asked for early plays pull and train first, then waits");
                Press(demo.View.CancelButton, "Cancel");
                yield return new WaitForSeconds(0.8f);
                Shot("10_push_cancelled");
                Check(!signalSeen.Contains("pipeline.pushed"), "Cancel pushes nothing");
                DemoActions.Fire(PipelineDemo.Push);
                yield return new WaitForSeconds(0.8f);
                Check(demo.WaitingForConfirm, "the confirm card comes back after Cancel");
                Press(demo.View.ConfirmButton, "Confirm");
                yield return WaitFor("pipeline.pushed", 6f);
                Check(signalSeen.Contains("pipeline.pushed"), "pushed after Confirm the second time");
                DemoActions.Fire(DemoActions.Reset);
                yield return null;
                Check(!demo.IsOpen, "reset closes the panel");
            }

            IEnumerator WaitFor(string signal, float timeout)
            {
                float end = Time.realtimeSinceStartup + timeout;
                while (!signalSeen.Contains(signal) && Time.realtimeSinceStartup < end) yield return null;
            }

            void Check(bool ok, string what)
            {
                log.AppendLine((ok ? "ok    " : "FAIL  ") + what);
                if (!ok) failures++;
            }

            readonly System.Collections.Generic.List<RaycastResult> hits = new System.Collections.Generic.List<RaycastResult>();

            // A real pointer press on a button: the head turns to it, the EventSystem is asked what is under that point, and only if it
            // is the button (the whole 40 mm box counts) do enter, down, up and click go to it, as the mouse and the Interaction SDK send them.
            void Press(UnityEngine.UI.Button button, string name)
            {
                var cam = Camera.main;
                var rect = (RectTransform)button.transform;
                Vector3 centre = rect.TransformPoint(rect.rect.center);
                cam.transform.rotation = Quaternion.LookRotation(centre - cam.transform.position, Vector3.up);
                Vector3 s = cam.WorldToScreenPoint(centre);
                var data = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, position = new Vector2(s.x, s.y) };
                hits.Clear();
                EventSystem.current.RaycastAll(data, hits);
                GameObject top = hits.Count > 0 ? hits[0].gameObject : null;
                bool onButton = top != null && top.GetComponentInParent<UnityEngine.UI.Button>() == button;
                Check(onButton, name + " is what the pointer finds at its centre (found " + (top != null ? top.name : "nothing") + ")");
                // And at the edges of the 40 mm box, which the kit's own 29 mm plate would not have covered.
                foreach (float f in new[] { 0.46f, -0.46f })
                {
                    Vector3 edge = rect.TransformPoint(rect.rect.center + new Vector2(0f, rect.rect.height * f));
                    Vector3 es = cam.WorldToScreenPoint(edge);
                    hits.Clear();
                    EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = new Vector2(es.x, es.y) }, hits);
                    GameObject etop = hits.Count > 0 ? hits[0].gameObject : null;
                    Check(etop != null && etop.GetComponentInParent<UnityEngine.UI.Button>() == button, name + " takes a poke near its " + (f > 0 ? "top" : "bottom") + " edge");
                }
                GameObject target = top != null ? top : button.gameObject;
                ExecuteEvents.ExecuteHierarchy(target, data, ExecuteEvents.pointerEnterHandler);
                ExecuteEvents.ExecuteHierarchy(target, data, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.ExecuteHierarchy(target, data, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.ExecuteHierarchy(target, data, ExecuteEvents.pointerClickHandler);
            }

            // The head's view of the panel: from the head to the panel's centre, 55 degrees tall, 1600 x 1000.
            void Shot(string name)
            {
                var head = DemoLayout.Head;
                var demo = FindFirstObjectByType<PipelineDemo>();
                if (head == null || demo == null || demo.View == null) return;
                if (shotCamera == null)
                {
                    var go = new GameObject("PipelineShotCamera");
                    shotCamera = go.AddComponent<Camera>();
                    shotCamera.enabled = false;
                    shotCamera.clearFlags = CameraClearFlags.SolidColor;
                    shotCamera.backgroundColor = new Color(0.02f, 0.025f, 0.035f);
                    shotCamera.fieldOfView = 55f;
                    shotCamera.nearClipPlane = 0.05f;
                    var data = go.AddComponent<UniversalAdditionalCameraData>();
                    data.allowXRRendering = false;
                }
                Vector3 target = demo.View.Panel.transform.position;
                shotCamera.transform.SetPositionAndRotation(head.position, Quaternion.LookRotation(target - head.position, Vector3.up));
                Canvas.ForceUpdateCanvases();
                var rt = new RenderTexture(1600, 1000, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                shotCamera.targetTexture = rt;
                shotCamera.Render();
                shotCamera.targetTexture = null;
                RenderTexture.active = rt;
                var tex = new Texture2D(1600, 1000, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1600, 1000), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                Directory.CreateDirectory(Dir);
                File.WriteAllBytes(Path.Combine(Dir, "pipeline_" + name + ".png"), tex.EncodeToPNG());
                DestroyImmediate(tex);
                DestroyImmediate(rt);
                log.AppendLine("shot  pipeline_" + name + ".png");
            }
        }
    }
}
