using System;
using System.Collections.Generic;
using System.IO;
using Forge.Pet;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Forge.OS.Editor
{
    /// <summary>
    /// Forge > OS > Take Restyle Shots: the real Main scene in Play mode on the desktop rig. For each older window (CAD, Code,
    /// Train, Messages, Notebook, Gestures, the notes and timers) it opens it next to an OS window (Settings), looks at both and
    /// writes Logs/os/restyle_&lt;tag&gt;_&lt;window&gt;.png. Pictures only, no pass or fail; the tag ("before" or "after") is passed to
    /// <see cref="Run"/>. Everything the run could write goes to a scratch folder (<see cref="OSTestEnv"/>).
    /// </summary>
    [InitializeOnLoad]
    public static class OSRestyleShots
    {
        const string ActiveKey = "OSRestyleShots.Active", TagKey = "OSRestyleShots.Tag";
        const string ScenePath = "Assets/_Forge/Core/Main.unity";

        /// <summary>"running", or "done: N pictures" when finished.</summary>
        public static string LastResult { get; private set; }

        static IEnumerator<float> steps;
        static double wakeAt, startedAt;
        static int taken;

        static OSRestyleShots()
        {
            if (SessionState.GetBool(ActiveKey, false)) EditorApplication.playModeStateChanged += OnPlayMode;
        }

        public static void Run(string tag)
        {
            if (EditorApplication.isPlaying) { Debug.Log("[OSRestyleShots] Leave Play mode first."); return; }
            SessionState.SetString(TagKey, tag);
            OSTestEnv.Enter("OSRestyleShots");
            // The notes on this machine are saved by the desk's notes panel itself, outside the scratch folder: keep a copy and put it back.
            string notes = NotesPath;
            SessionState.SetBool(NotesHadKey, File.Exists(notes));
            SessionState.SetString(NotesKey, File.Exists(notes) ? Convert.ToBase64String(File.ReadAllBytes(notes)) : "");
            SessionState.SetBool(ActiveKey, true);
            LastResult = "running";
            EditorSceneManager.OpenScene(ScenePath);
            EditorApplication.playModeStateChanged += OnPlayMode;
            EditorApplication.isPlaying = true;
        }

        static void OnPlayMode(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredPlayMode) return;
            taken = 0;
            steps = Script(SessionState.GetString(TagKey, "after"));
            wakeAt = 0;
            startedAt = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup - startedAt > 240) { Finish(); return; }
            if (EditorApplication.timeSinceStartup < wakeAt) return;
            try
            {
                if (steps.MoveNext()) wakeAt = EditorApplication.timeSinceStartup + steps.Current;
                else Finish();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Finish();
            }
        }

        static void Finish()
        {
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            RestoreNotes();
            OSTestEnv.Leave();
            LastResult = "done: " + taken + " pictures";
            Debug.Log("[OSRestyleShots] " + LastResult);
            SessionState.SetBool(ActiveKey, false);
            EditorApplication.isPlaying = false;
        }

        const string NotesKey = "OSRestyleShots.Notes", NotesHadKey = "OSRestyleShots.NotesHad";
        static string NotesPath => Path.Combine(Application.persistentDataPath, "notes.json");

        static void RestoreNotes()
        {
            try
            {
                if (SessionState.GetBool(NotesHadKey, false)) File.WriteAllBytes(NotesPath, Convert.FromBase64String(SessionState.GetString(NotesKey, "")));
                else if (File.Exists(NotesPath)) File.Delete(NotesPath);
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        static void Shot(string tag, string name)
        {
            var workspace = Workspace.Current;
            var greeting = UnityEngine.Object.FindAnyObjectByType<ArrivalGreeting>();
            if (greeting != null && greeting.IsShown) greeting.Fold(true);
            if (workspace != null && workspace.DockHeld) workspace.HoldDock(false);
            string path = OSShot.Take("restyle_" + tag + "_" + name, 1600, 1000);
            if (path != null) taken++;
            Debug.Log("[OSRestyleShots] shot " + (path ?? "(no camera)"));
        }

        // The panels that are not OS windows, near the user, biggest first (mm area).
        static readonly List<ForgePanel> found = new List<ForgePanel>();
        static void FindOld(OSShell shell, bool titledOnly, bool gestures = false)
        {
            found.Clear();
            foreach (var p in ForgePanel.All)
            {
                if (p == null || !p.gameObject.activeInHierarchy) continue;
                var osWindow = p.GetComponent<OSWindow>();
                if (osWindow != null && (osWindow.Id == OSSettingsWindow.WindowId || !gestures)) continue;
                if (p.transform.lossyScale.x < 0.0005f) continue;
                if (titledOnly && string.IsNullOrEmpty(p.Title)) continue;
                if (!gestures && p.name.StartsWith("GesturePanel", StringComparison.Ordinal)) continue;
                var r = (RectTransform)p.transform;
                if (r.sizeDelta.x < 150f || r.sizeDelta.y < 100f) continue;
                found.Add(p);
            }
            found.Sort((x, y) =>
            {
                var a = ((RectTransform)x.transform).sizeDelta; var b = ((RectTransform)y.transform).sizeDelta;
                return (b.x * b.y).CompareTo(a.x * a.y);
            });
        }

        // Puts the OS window right beside the old panel (same plane, same turn), then takes a picture of the pair from a
        // camera of its own placed straight in front of them.
        static void Pair(string tag, string name, ForgePanel old, OSWindow os)
        {
            Transform o = old.transform, s = os.Panel.transform;
            var orect = (RectTransform)o; var srect = (RectTransform)s;
            float ow = orect.rect.width * o.lossyScale.x, oh = orect.rect.height * o.lossyScale.y;
            float sw = srect.rect.width * s.lossyScale.x, sh = srect.rect.height * s.lossyScale.y;
            s.rotation = o.rotation;
            Vector3 oc = orect.TransformPoint(orect.rect.center);
            s.position += o.right * 0f;  // (position is the pivot, which is centred on a ForgePanel)
            s.position = oc + o.right * (ow * 0.5f + sw * 0.5f + 0.06f);
            Vector3 sc = srect.TransformPoint(srect.rect.center);
            Vector3 mid = (oc + sc) * 0.5f;
            float width = ow + sw + 0.06f + 0.12f, height = Mathf.Max(oh, sh) + 0.12f;
            const float vfov = 40f; const float aspect = 1.6f;
            float dW = width * 0.5f / (Mathf.Tan(vfov * 0.5f * Mathf.Deg2Rad) * aspect), dH = height * 0.5f / Mathf.Tan(vfov * 0.5f * Mathf.Deg2Rad);
            var go = new GameObject("RestyleCam");
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = vfov; cam.aspect = aspect; cam.nearClipPlane = 0.05f; cam.farClipPlane = 20f;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.36f, 0.38f, 0.45f);
            go.transform.position = mid - o.forward * Mathf.Max(dW, dH);
            go.transform.rotation = Quaternion.LookRotation(o.forward, o.up);
            const int w = 1920, h = 1200;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;
            // A plain backdrop: the room and the desk are switched off for this one frame, so only windows are in the picture.
            var hidden = new List<Renderer>();
            foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude))
            {
                if (r == null || !r.enabled || r.GetComponentInParent<Canvas>() != null) continue;
                r.enabled = false;
                hidden.Add(r);
            }
            // Only the two windows: every other canvas is off for this frame too.
            var offCanvases = new List<Canvas>();
            foreach (var c in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude))
            {
                if (c == null || !c.enabled || !c.isRootCanvas || c.transform == o || c.transform == s) continue;
                if (c.transform.IsChildOf(o) || c.transform.IsChildOf(s) || o.IsChildOf(c.transform) || s.IsChildOf(c.transform)) continue;
                c.enabled = false;
                offCanvases.Add(c);
            }
            cam.Render();
            foreach (var c in offCanvases) if (c != null) c.enabled = true;
            foreach (var r in hidden) if (r != null) r.enabled = true;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            Directory.CreateDirectory(OSShot.Dir);
            string path = Path.GetFullPath(Path.Combine(OSShot.Dir, "restyle_" + tag + "_" + name + ".png"));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.Destroy(go);
            taken++;
            Debug.Log("[OSRestyleShots] shot " + path + " (old panel '" + old.name + "' title '" + old.Title + "', OS window '" + os.Id + "')");
        }

        // Every text in the OS windows and the menu, with its size in OS units, em height and cap height in mm, to a text file.
        static readonly System.Text.StringBuilder audit = new System.Text.StringBuilder();

        static void AuditText(string where, GameObject root)
        {
            if (root == null) return;
            OSKit kit = OSKit.Current;
            float unit = kit != null ? kit.unitMeters : 0.00065f;
            foreach (var t in root.GetComponentsInChildren<TMPro.TMP_Text>(false))
            {
                if (t == null || string.IsNullOrWhiteSpace(t.text) || !t.gameObject.activeInHierarchy) continue;
                if (t.GetComponentInParent<CanvasGroup>() != null && t.GetComponentInParent<CanvasGroup>().alpha < 0.05f) continue;
                float scale = t.transform.lossyScale.x;
                float emMm = t.fontSize * scale * 1000f;
                var face = t.font != null ? t.font.faceInfo : default;
                float capRatio = face.pointSize > 0f ? face.capLine / face.pointSize : 0.72f;
                float capMm = emMm * (capRatio > 0.2f ? capRatio : 0.72f);
                string text = t.text.Length > 28 ? t.text.Substring(0, 28) : t.text;
                audit.AppendLine(string.Format("{0} | {1:F1} units | em {2:F1} mm | cap {3:F1} mm | {4} | {5}", where, emMm / (unit * 1000f), emMm, capMm, t.gameObject.name, text.Replace((char)10, ' ')));
            }
        }

        static void WriteAudit(string tag)
        {
            Directory.CreateDirectory(OSShot.Dir);
            File.WriteAllText(Path.Combine(OSShot.Dir, "restyle_" + tag + "_text.txt"), audit.ToString());
            audit.Clear();
        }

        static IEnumerator<float> Script(string tag)
        {
            OSTestEnv.UseScratch("osrestyle");
            Workspace workspace = null;
            while (workspace == null || !workspace.presence.AtDesk)
            {
                yield return 0.25f;
                workspace = Workspace.Current;
                if (workspace != null) workspace.presence.forceSeated = true;
            }
            yield return 0.5f;
            DesktopRig.Current.ToggleSeat();
            yield return 1.2f;
            OSShell shell = OSShell.Current;
            if (shell == null) yield break;
            var greeting = UnityEngine.Object.FindAnyObjectByType<ArrivalGreeting>();
            if (greeting != null) greeting.Fold(true);
            workspace.HoldDock(false);
            workspace.Close();
            yield return 1.2f;
            shell.Windows.CloseAll();
            yield return 0.5f;

            // Text audit of the OS windows and the menu.
            audit.Clear();
            shell.OpenMenu(OSSummon.Code);
            yield return 1.5f;
            AuditText("menu", shell.GetComponentInChildren<OSMenu>(true).gameObject);
            shell.CloseMenu();
            yield return 0.8f;
            foreach (string sys in new[] { "settings", "agents", "build" })
            {
                shell.OpenSystem(sys);
                yield return 2.2f;
                foreach (var w in shell.Windows.All)
                    if (w != null && w.Panel != null && w.gameObject.activeInHierarchy) AuditText(sys + ":" + w.Id, w.gameObject);
                shell.Windows.CloseAll();
                yield return 0.6f;
            }
            foreach (string toolId in new[] { "notebook", "drive" })
            {
                OSRegistry.Find(OSTab.Tools, toolId)?.open?.Invoke();
                yield return 2.2f;
                foreach (var w in shell.Windows.All)
                    if (w != null && w.Panel != null && w.gameObject.activeInHierarchy) AuditText(toolId + ":" + w.Id, w.gameObject);
                shell.Windows.CloseAll();
                yield return 0.6f;
            }
            {
                var projects = new List<OSEntry>();
                OSRegistry.Fill(OSTab.Projects, projects);
                if (projects.Count > 0) projects[0].open?.Invoke();
                yield return 2.2f;
                foreach (var w in shell.Windows.All)
                    if (w != null && w.Panel != null && w.gameObject.activeInHierarchy) AuditText("project:" + w.Id, w.gameObject);
                shell.Windows.CloseAll();
                yield return 0.6f;
            }
            WriteAudit(tag);

            string[] items = { "cad", "code", "train", "messages", "gestures" };
            int[] counts = { 2, 1, 2, 1, 1 };
            for (int k = 0; k < items.Length; k++)
            {
                string id = items[k];
                OSEntry entry = OSRegistry.Find(OSTab.Tools, id);
                if (entry == null || entry.open == null) { Debug.Log("[OSRestyleShots] no tool " + id); continue; }
                entry.open();
                yield return 2.5f;
                shell.OpenSystem("settings");
                yield return 3f;
                FindOld(shell, true, id == "gestures");
                OSWindow os = shell.Windows.Find("settings") ?? FirstOs(shell);
                int n = Mathf.Min(counts[k], found.Count);
                var list = new List<ForgePanel>(found);
                for (int i = 0; i < n; i++)
                {
                    if (os == null || os.Panel == null) break;
                    Pair(tag, id + (i == 0 ? "" : (i + 1).ToString()), list[i], os);
                    yield return 0.1f;
                }
                shell.Windows.CloseAll();
                workspace.Close();
                yield return 1.5f;
            }

            // Notes and timers.
            DeskApi.NoteAdd("Order the motor controller");
            DeskApi.NoteAdd("Print the new bumper");
            DeskApi.TimerStart(600f, "Glue");
            yield return 2f;
            shell.OpenSystem("settings");
            yield return 3f;
            FindOld(shell, true);
            {
                OSWindow os = FirstOs(shell);
                var list = new List<ForgePanel>(found);
                for (int i = 0; i < Mathf.Min(2, list.Count) && os != null; i++) Pair(tag, "notes_timers" + (i == 0 ? "" : "2"), list[i], os);
            }
            shell.Windows.CloseAll();
            yield return 0.5f;

            // Artifact panel (the Allow sheet, which is the first thing a new artifact shows).
            OSRegistry.Find(OSTab.Artifacts, "new_repo_activity")?.open?.Invoke();
            yield return 2.5f;
            shell.OpenSystem("settings");
            yield return 3f;
            {
                OSWindow os = FirstOs(shell);
                OSWindow sheet = null;
                foreach (var w in shell.Windows.All)
                    if (w != null && w.Panel != null && w.Id.StartsWith("artifact:", StringComparison.Ordinal)) { sheet = w; break; }
                if (os != null && sheet != null) Pair(tag, "artifact", sheet.Panel, os);
                else Debug.Log("[OSRestyleShots] no artifact panel (" + (sheet != null) + ", " + (os != null) + ")");
            }
            shell.Windows.CloseAll();
            yield return 0.5f;
        }

        static OSWindow FirstOs(OSShell shell)
        {
            foreach (var w in shell.Windows.All)
                if (w != null && w.Panel != null && !w.Id.StartsWith("artifact:", StringComparison.Ordinal) && w.Id.Contains("settings")) return w;
            return null;
        }
    }
}
