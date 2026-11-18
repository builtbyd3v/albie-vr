using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Forge.Artifacts;
using Forge.Controllers;
using Forge.Notebook;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Forge.OS.Editor
{
    /// <summary>
    /// Forge > OS > Capture Look Set: the picture set the look-and-feel grading loop reviews every round. It plays the real Main
    /// scene on the desktop rig with voice off, on the shared scratch test environment (<see cref="OSTestEnv"/>) and the mock
    /// living room, walks the whole product and saves one picture per screen to unity/Logs/os/look/NN_name.png, plus
    /// unity/Logs/os/look/index.json: for every picture its file, screen (a short name), what (one sentence: what should be
    /// visible), distance (metres from the camera to the main surface in view) and the vertical field of view.
    /// Every picture is taken by the user's head camera, moved for the one render to where the user would stand to look at the
    /// surface under review: square on to the surface, 0.45 to 0.9 m away, framed so the surface fills most of the picture
    /// (a surface too small to fill it at 0.45 m is shown with a narrower field of view from 0.45 m: "zoomed"). Where context
    /// matters (an old window beside a new one, windows around the user) a second, wider picture is taken from the real head
    /// position. Nothing in the scene moves for a picture: the camera goes back where it was in the same call.
    /// Only unity/Logs/os/look is cleared at the start. Poll an eval with <see cref="Running"/> and <see cref="LastResult"/>.
    /// <c>RunOnly("settings,agents")</c> takes just those sections (the setup always runs), for fixing one screen quickly.
    /// </summary>
    [InitializeOnLoad]
    public static class OSLookShots
    {
        const string ActiveKey = "OSLookShots.Active", ResultKey = "OSLookShots.Result", OnlyKey = "OSLookShots.Only";
        const string ScenePath = "Assets/_Forge/Core/Main.unity";
        const double GiveUpSeconds = 1500;
        const int W = 1600, H = 1000;
        const float Aspect = (float)W / H;
        const string AgentTask = "Read the sample project and say what it does";

        /// <summary>Where the pictures go.</summary>
        public static string Dir => Path.Combine(OSShot.Dir, "look");

        static IEnumerator<float> steps;
        static double wakeAt, startedAt;
        static string currentStep, tempRoot;
        static float defaultFov = 60f;
        static int shotCount;
        static readonly List<JObject> index = new List<JObject>();
        static readonly List<string> problems = new List<string>();
        static readonly List<string> unreachable = new List<string>();
        // Product problems seen while capturing (not problems of the capture itself): reported, but the set is still complete.
        static readonly List<string> findings = new List<string>();
        static readonly Vector3[] corners = new Vector3[4];

        // What the sections share.
        static OSShell shell;
        static Workspace workspace;
        static global::DesktopRig rig;
        static OSTableButton tableButton;
        static OSMenu menu;
        static RectTransform menuPlate;
        static Vector3 deskRigPosition;
        static Quaternion deskRigRotation;
        static System.Net.Sockets.TcpListener agentHold;

        static OSLookShots()
        {
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        /// <summary>True from <see cref="Run"/> until the capture has finished (it survives the domain reload into Play).</summary>
        public static bool Running => SessionState.GetBool(ActiveKey, false);

        /// <summary>"running" while it runs, then "OK look set: N pictures ..." or "PARTIAL look set: ..." with the problems. Empty before the first run.</summary>
        public static string LastResult
        {
            get => SessionState.GetString(ResultKey, "");
            private set => SessionState.SetString(ResultKey, value ?? "");
        }

        [MenuItem("Forge/OS/Capture Look Set")]
        public static void Run()
        {
            SessionState.SetString(OnlyKey, "");
            RunNow();
        }

        /// <summary>Takes only the named sections (comma separated, see <see cref="Sections"/>); the setup always runs.</summary>
        public static void RunOnly(string sections)
        {
            SessionState.SetString(OnlyKey, sections ?? "");
            RunNow();
        }

        static void RunNow()
        {
            if (Running)
            {
                Debug.LogError("[OSLookShots] A run is already in progress.");
                return;
            }
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Refuse("leave Play mode first");
                return;
            }
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty)
            {
                Refuse("save or discard the open scene first");
                return;
            }
            OSTestEnv.Enter("OSLookShots");
            SessionState.SetBool(ActiveKey, true);
            LastResult = "running";
            EditorSceneManager.OpenScene(ScenePath);
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.playModeStateChanged += OnPlayMode;
            EditorApplication.isPlaying = true;
        }

        static void Refuse(string why)
        {
            LastResult = "FAIL look set: " + why;
            Debug.LogError("[OSLookShots] " + why);
        }

        static void OnPlayMode(PlayModeStateChange change)
        {
            if (!Running) return;
            if (change == PlayModeStateChange.EnteredPlayMode) Begin();
            else if (change == PlayModeStateChange.EnteredEditMode)
            {
                EditorApplication.update -= Tick;
                problems.Add("Play mode stopped before the capture finished");
                Finish(false);
            }
        }

        static void Begin()
        {
            ClearLookFolder();
            index.Clear();
            textAudit.Clear();
            problems.Clear();
            unreachable.Clear();
            findings.Clear();
            shotCount = 0;
            currentStep = null;
            Camera cam = PetUser.HeadCamera;
            defaultFov = cam != null ? Mathf.Clamp(cam.fieldOfView, 50f, 75f) : 60f;
            tempRoot = OSTestEnv.UseScratch("oslook");
            steps = Script();
            wakeAt = 0;
            startedAt = EditorApplication.timeSinceStartup;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        // Only this folder is cleared: the other tests' pictures in Logs/os stay.
        static void ClearLookFolder()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                foreach (string file in Directory.GetFiles(Dir)) File.Delete(file);
            }
            catch (IOException e) { Debug.Log("[OSLookShots] couldn't clear " + Dir + ": " + e.Message); }
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup - startedAt > GiveUpSeconds)
            {
                problems.Add("timed out after " + GiveUpSeconds + " s in section '" + currentStep + "'");
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
                problems.Add("the script threw " + e.Message);
                Finish(true);
            }
        }

        static void Finish(bool stopPlay)
        {
            EditorApplication.update -= Tick;
            try { CleanupLeftovers(); }
            catch (Exception e) { Debug.LogException(e); }
            currentStep = "cleanup";
            string envDiff = OSTestEnv.Leave();
            if (envDiff.Length > 0) problems.Add("the user's own preferences or files changed: " + envDiff.Replace("\n", " | "));
            WriteIndex();
            var sb = new StringBuilder();
            sb.Append(problems.Count == 0 && unreachable.Count == 0 ? "OK" : "PARTIAL").Append(" look set: ").Append(index.Count).Append(" pictures in ").Append(Path.GetFullPath(Dir));
            if (unreachable.Count > 0) sb.Append("\nunreachable: ").Append(string.Join("; ", unreachable));
            if (problems.Count > 0) sb.Append("\nproblems: ").Append(string.Join("; ", problems));
            if (findings.Count > 0) sb.Append("\nproduct findings: ").Append(string.Join("; ", findings));
            LastResult = sb.ToString();
            Debug.Log("[OSLookShots] " + LastResult);
            try { File.WriteAllText(Path.Combine(Dir, "result.txt"), LastResult); } catch (IOException) { }
            tempRoot = null;
            SessionState.SetBool(ActiveKey, false);
            if (stopPlay) EditorApplication.isPlaying = false;
        }

        static void CleanupLeftovers()
        {
            SimulatedHandSource.LeftTestActive = false;
            StopAgentHold();
            if (!Application.isPlaying) return;
            DismissAgents();
            ArtifactHost host = ArtifactHost.Current;
            if (host != null) host.Room = null;
            ManualInputs.Clear();
            if (shell != null) shell.Windows.CloseAll();
            OSModes.Close();
            NotebookStore.Flush();
            NotebookStore.Open(NotebookStore.DefaultProject, tempRoot);  // the run's own notebook again, never the app's
            OSProjects.Refresh();
        }

        // ---------------------------------------------------------------- bookkeeping

        static void Problem(string what)
        {
            problems.Add("[" + currentStep + "] " + what);
            Debug.Log("[OSLookShots] problem [" + currentStep + "] " + what);
        }

        static void Finding(string what)
        {
            findings.Add("[" + currentStep + "] " + what);
            Debug.Log("[OSLookShots] product finding [" + currentStep + "] " + what);
        }

        static void Unreachable(string screen, string why)
        {
            unreachable.Add(screen + " (" + why + ")");
            Debug.Log("[OSLookShots] unreachable " + screen + ": " + why);
        }

        // Every visible text of the menu and the OS windows at the moment of a picture: its size in OS units, its cap height in
        // millimetres (the font's own cap line), and a flag when the text does not fit its own box ("wide": a line that does not wrap
        // is longer than the box; "tall": wrapped text is taller than the box). The measurements the text-size rubric needs.
        static readonly StringBuilder textAudit = new StringBuilder();
        static readonly List<TMPro.TMP_Text> auditScratch = new List<TMPro.TMP_Text>();

        static void AuditText(string screen)
        {
            if (shell == null) return;
            float unit = OSKit.Current != null ? OSKit.Current.unitMeters : 0.00065f;
            if (menu != null && menu.Visible) AuditRoot(screen, "menu", menu.gameObject, unit);
            foreach (OSWindow w in shell.Windows.All)
                if (w != null && w.Panel != null && w.gameObject.activeInHierarchy) AuditRoot(screen, w.Id, w.gameObject, unit);
        }

        static void AuditRoot(string screen, string where, GameObject root, float unit)
        {
            root.GetComponentsInChildren(false, auditScratch);
            foreach (TMPro.TMP_Text t in auditScratch)
            {
                if (t == null || string.IsNullOrWhiteSpace(t.text) || !t.gameObject.activeInHierarchy) continue;
                CanvasGroup g = t.GetComponentInParent<CanvasGroup>();
                if (g != null && g.alpha < 0.05f) continue;
                float emMm = t.fontSize * t.transform.lossyScale.x * 1000f;
                var face = t.font != null ? t.font.faceInfo : default;
                float capRatio = face.pointSize > 0f ? face.capLine / face.pointSize : 0.72f;
                float capMm = emMm * (capRatio > 0.2f ? capRatio : 0.72f);
                Rect box = t.rectTransform.rect;
                string flag = "";
                if (t.textWrappingMode == TMPro.TextWrappingModes.NoWrap) { if (t.preferredWidth > box.width + 1f) flag = " WIDE(" + t.preferredWidth.ToString("F0") + ">" + box.width.ToString("F0") + ")"; }
                else if (t.preferredHeight > box.height + 1f) flag = " TALL(" + t.preferredHeight.ToString("F0") + ">" + box.height.ToString("F0") + ")";
                string text = t.text.Length > 40 ? t.text.Substring(0, 40) : t.text;
                textAudit.AppendLine(string.Format("{0} | {1} | {2:F1} units | cap {3:F2} mm | {4}{5} | {6}", screen, where, emMm / (unit * 1000f), capMm, t.gameObject.name, flag, text.Replace((char)10, ' ')));
            }
            auditScratch.Clear();
        }

        static void WriteIndex()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(Path.Combine(Dir, "index.json"), new JArray(index).ToString(Newtonsoft.Json.Formatting.Indented));
                File.WriteAllText(Path.Combine(Dir, "text_audit.txt"), textAudit.ToString());
            }
            catch (IOException e) { Debug.Log("[OSLookShots] couldn't write index.json: " + e.Message); }
        }

        static IEnumerable<float> Until(Func<bool> done, float seconds)
        {
            double end = EditorApplication.timeSinceStartup + seconds;
            while (!done() && EditorApplication.timeSinceStartup < end) yield return 0.1f;
        }

        static bool Ready()
        {
            shell = OSShell.Current;
            workspace = Workspace.Current;
            rig = global::DesktopRig.Current;
            if (shell == null || workspace == null || rig == null) return false;
            tableButton = shell.GetComponent<OSTableButton>();
            menu = shell.GetComponentInChildren<OSMenu>(true);
            if (menu != null && menuPlate == null)
            {
                FieldInfo field = typeof(OSMenu).GetField("plate", BindingFlags.NonPublic | BindingFlags.Instance);
                menuPlate = field != null ? field.GetValue(menu) as RectTransform : null;
            }
            return true;
        }

        // ---------------------------------------------------------------- taking a picture

        // Renders the head camera from this pose and field of view (it goes back at once), writes NN_screen.png and lists it.
        static bool Take(string screen, string what, Vector3 position, Quaternion rotation, float fov, float distance,
            List<Vector3> probes = null, Transform keep = null)
        {
            Camera cam = PetUser.HeadCamera;
            if (cam == null) { Unreachable(screen, "there is no head camera"); return false; }
            Transform t = cam.transform;
            Vector3 p0 = t.position;
            Quaternion r0 = t.rotation;
            float f0 = cam.fieldOfView;
            Texture2D tex = null;
            HideNoise(position, probes, keep);
            try
            {
                t.SetPositionAndRotation(position, rotation);
                cam.fieldOfView = fov;
                tex = OSShot.Capture(W, H);
            }
            finally
            {
                t.SetPositionAndRotation(p0, r0);
                cam.fieldOfView = f0;
                ShowNoise();
            }
            if (tex == null) { Unreachable(screen, "no graphics device"); return false; }
            string name = $"{++shotCount:00}_{screen}.png";
            string path = Path.GetFullPath(Path.Combine(Dir, name));
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            var entry = new JObject
            {
                ["file"] = name,
                ["path"] = path.Replace('\\', '/'),
                ["screen"] = screen,
                ["what"] = what,
                ["distance"] = Math.Round(distance, 2),
                ["fov"] = Math.Round(fov, 0),
                ["zoomed"] = fov < defaultFov - 1f,
            };
            index.Add(entry);
            AuditText(screen);
            WriteIndex();
            Debug.Log("[OSLookShots] shot " + path + " (" + distance.ToString("F2") + " m, fov " + fov.ToString("F0") + ")");
            return true;
        }

        // What is switched off for the one render. Albie's speech caption (with voice off it says "I can't hear you right now"
        // whenever Albie asks a question) is left out: it is the pet's, not part of any screen under review. The desktop rig draws its simulated right hand as dots (SimJoint) and a ray
        // line in front of the head, which would sit in every picture; they are not part of any screen. When a surface is framed
        // (probes: points on it), small scene models that stand between the camera and any of those points (Albie, props) are
        // switched off too, so the surface is seen whole; panels and windows are never switched off, so a window in front of
        // another still shows. forceRenderingOff only: nothing is disabled or destroyed, and it is undone right after the render.
        static readonly List<Renderer> noise = new List<Renderer>();
        // Extra models a picture asks to leave out (the open mode's 3D parts, when the picture is of one of its panels).
        static readonly List<Renderer> alsoHide = new List<Renderer>();

        static void HideNoise(Vector3 eye, List<Vector3> probes, Transform keep)
        {
            noise.Clear();
            foreach (Renderer r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude))
            {
                if (r == null || !r.enabled || r.forceRenderingOff) continue;
                bool hide = alsoHide.Contains(r) || r.GetComponentInParent<AlbieCaption>() != null || r.name == "SimJoint" || r.name == "GestureBeam" || (r is LineRenderer && r.GetComponentInParent<global::DesktopRig>() != null);
                if (!hide && probes != null) hide = Occludes(r, eye, probes, keep);
                if (!hide) continue;
                r.forceRenderingOff = true;
                noise.Add(r);
            }
        }

        static void ShowNoise()
        {
            for (int i = 0; i < noise.Count; i++) if (noise[i] != null) noise[i].forceRenderingOff = false;
            noise.Clear();
        }

        static bool Occludes(Renderer r, Vector3 eye, List<Vector3> probes, Transform keep)
        {
            if (r.GetComponentInParent<Canvas>() != null) return false;
            if (keep != null && r.transform.IsChildOf(keep)) return false;
            Bounds b = r.bounds;
            if (b.Contains(eye)) return false;
            // The room shell (walls, floor, ceiling) must reach well in front of the plane to hide; furniture and props that touch or poke
            // through the surface (a window placed into a sofa) are hidden when they reach a little behind it too.
            bool shell = r.name.StartsWith("WALL_FACE", System.StringComparison.Ordinal) || r.name.StartsWith("FLOOR", System.StringComparison.Ordinal) || r.name.StartsWith("CEILING", System.StringComparison.Ordinal);
            float slack = shell ? -0.02f : 0.12f;
            for (int i = 0; i < probes.Count; i++)
            {
                Vector3 to = probes[i] - eye;
                float length = to.magnitude;
                if (length < 1e-3f) continue;
                if (b.IntersectRay(new Ray(eye, to / length), out float hit) && hit < length + slack) return true;
            }
            return false;
        }

        // How wide the camera must see (vertical field of view in degrees) for every point to fall inside the middle `fill` of
        // the picture; infinity when a point is behind the camera.
        static float Needed(Vector3 position, Quaternion rotation, List<Vector3> pts, float fill)
        {
            Quaternion inverse = Quaternion.Inverse(rotation);
            float t = 0f;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 l = inverse * (pts[i] - position);
                if (l.z < 0.03f) return float.PositiveInfinity;
                t = Mathf.Max(t, Mathf.Abs(l.y) / l.z, Mathf.Abs(l.x) / l.z / Aspect);
            }
            return 2f * Mathf.Atan(t / fill) * Mathf.Rad2Deg;
        }

        static void AddRect(List<Vector3> pts, RectTransform rect)
        {
            rect.GetWorldCorners(corners);
            for (int i = 0; i < 4; i++) pts.Add(corners[i]);
        }

        static void AddBounds(List<Vector3> pts, Bounds b)
        {
            for (int i = 0; i < 8; i++)
                pts.Add(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
        }

        static void AddWindow(List<Vector3> pts, OSWindow w)
        {
            if (w == null || w.IsGroup || !w.gameObject.activeInHierarchy) return;
            if (w.Panel != null) AddRect(pts, (RectTransform)w.Panel.transform);
            else if (w.Volume != null)
            {
                Renderer[] renderers = w.Volume.GetComponentsInChildren<Renderer>(false);
                bool any = false;
                Bounds b = default;
                foreach (Renderer r in renderers)
                {
                    if (r == null || !r.enabled) continue;
                    if (!any) { b = r.bounds; any = true; }
                    else b.Encapsulate(r.bounds);
                }
                if (any) AddBounds(pts, b);
            }
        }

        // Square on to a surface: the camera stands in front of it along its normal. The distance is the one at which the surface
        // fills about `fill` of the picture at the head camera's own field of view, kept in minD..maxD; a surface too small to
        // fill the picture at minD is shown with a narrower field of view from minD, and one too big at maxD with a wider one
        // (up to maxFov), and past that the camera backs off.
        static bool FrameSquare(string screen, string what, List<Vector3> pts, Vector3 normal, Vector3 up, float fill = 0.88f,
            float minD = 0.45f, float maxD = 0.9f, float maxFov = 85f, Transform keep = null)
        {
            if (pts.Count == 0) { Unreachable(screen, "nothing to frame"); return false; }
            Quaternion rot = Quaternion.LookRotation(normal, up);
            Vector3 right = rot * Vector3.right, upv = rot * Vector3.up, fwd = rot * Vector3.forward;
            Vector3 mean = Vector3.zero;
            foreach (Vector3 p in pts) mean += p;
            mean /= pts.Count;
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (Vector3 p in pts)
            {
                Vector3 o = p - mean;
                minX = Mathf.Min(minX, Vector3.Dot(o, right)); maxX = Mathf.Max(maxX, Vector3.Dot(o, right));
                minY = Mathf.Min(minY, Vector3.Dot(o, upv)); maxY = Mathf.Max(maxY, Vector3.Dot(o, upv));
            }
            Vector3 center = mean + right * ((minX + maxX) * 0.5f) + upv * ((minY + maxY) * 0.5f);
            float d = minD;
            for (int i = 0; i < 8; i++)
            {
                float need = Needed(center - fwd * d, rot, pts, fill);
                if (float.IsInfinity(need)) { d *= 1.5f; continue; }
                float next = d * Mathf.Tan(need * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(defaultFov * 0.5f * Mathf.Deg2Rad);
                if (Mathf.Abs(next - d) < 0.004f) { d = next; break; }
                d = next;
            }
            d = Mathf.Max(d, minD);
            if (d > maxD && Needed(center - fwd * maxD, rot, pts, fill) <= maxFov) d = maxD;
            Vector3 pos = center - fwd * d;
            float fov = Mathf.Clamp(Needed(pos, rot, pts, fill), 12f, 100f);
            var probes = new List<Vector3>(pts) { center };
            return Take(screen, what, pos, rot, fov, d, probes, keep);
        }

        static bool FrameRect(string screen, string what, RectTransform rect, float fill = 0.88f, float minD = 0.45f, float maxD = 0.9f,
            float padX = 1f, float padY = 1f, Transform keep = null)
        {
            if (rect == null) { Unreachable(screen, "the surface is not there"); return false; }
            var pts = new List<Vector3>();
            rect.GetWorldCorners(corners);
            Vector3 c = rect.TransformPoint(rect.rect.center);
            for (int i = 0; i < 4; i++)
            {
                Vector3 o = corners[i] - c;
                pts.Add(c + rect.right * Vector3.Dot(o, rect.right) * padX + rect.up * Vector3.Dot(o, rect.up) * padY);
            }
            if (keep == null)
            {
                Canvas canvas = rect.GetComponentInParent<Canvas>();
                keep = canvas != null ? canvas.rootCanvas.transform : rect;
            }
            return FrameSquare(screen, what, pts, rect.forward, rect.up, fill, minD, maxD, 85f, keep);
        }

        static bool FrameWindow(string screen, string what, OSWindow window, float fill = 0.88f)
        {
            if (window == null || window.Panel == null) { Unreachable(screen, "the window is not there"); return false; }
            return FrameRect(screen, what, (RectTransform)window.Panel.transform, fill, 0.45f, 0.9f, 1f, 1f, window.Panel.transform);
        }

        // From where the user is: the camera stays at the real head position and looks at the middle of the points. It zooms in
        // (not past minFov) when everything fits, and backs off along the line of sight when even maxFov is not wide enough.
        static bool FrameFromHead(string screen, string what, List<Vector3> pts, float fill = 0.9f, float minFov = 35f, float maxFov = 95f)
        {
            Transform head = OSShell.FindHead();
            if (head == null || pts.Count == 0) { Unreachable(screen, "no head or nothing to frame"); return false; }
            var box = new Bounds(pts[0], Vector3.zero);
            foreach (Vector3 p in pts) box.Encapsulate(p);
            Vector3 eye = head.position, aim = box.center;
            Vector3 dir = (aim - eye).normalized;
            Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);
            // Aim at the middle of what the camera will see, so a window far to one side doesn't push everything off.
            for (int i = 0; i < 2; i++)
            {
                Quaternion inverse = Quaternion.Inverse(rot);
                float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                foreach (Vector3 p in pts)
                {
                    Vector3 l = inverse * (p - eye);
                    if (l.z < 0.05f) continue;
                    minX = Mathf.Min(minX, Mathf.Atan2(l.x, l.z)); maxX = Mathf.Max(maxX, Mathf.Atan2(l.x, l.z));
                    minY = Mathf.Min(minY, Mathf.Atan2(l.y, l.z)); maxY = Mathf.Max(maxY, Mathf.Atan2(l.y, l.z));
                }
                if (minX > maxX) break;
                rot = rot * Quaternion.Euler(-(minY + maxY) * 0.5f * Mathf.Rad2Deg, (minX + maxX) * 0.5f * Mathf.Rad2Deg, 0f);
            }
            Vector3 view = rot * Vector3.forward;
            Vector3 pos = eye;
            float need = Needed(pos, rot, pts, fill);
            for (int k = 0; k < 14 && (float.IsInfinity(need) || need > maxFov); k++)
            {
                pos -= view * 0.25f;
                need = Needed(pos, rot, pts, fill);
            }
            float fov = float.IsInfinity(need) ? maxFov : Mathf.Clamp(need, minFov, maxFov);
            return Take(screen, what, pos, rot, fov, Vector3.Distance(pos, box.center));
        }

        // From above and to the side, as a user standing a little way off would look down at something on the floor: the camera is
        // `back` metres away on the user's side and `height` metres above the middle of the points.
        static bool FrameFromAbove(string screen, string what, List<Vector3> pts, float back = 0.9f, float height = 0.8f)
        {
            Transform head = OSShell.FindHead();
            if (head == null || pts.Count == 0) { Unreachable(screen, "no head or nothing to frame"); return false; }
            var box = new Bounds(pts[0], Vector3.zero);
            foreach (Vector3 p in pts) box.Encapsulate(p);
            Vector3 aim = box.center;
            Vector3 away = Vector3.ProjectOnPlane(head.position - aim, Vector3.up);
            away = away.sqrMagnitude < 0.01f ? Vector3.back : away.normalized;
            Vector3 eye = aim + away * back + Vector3.up * height;
            Quaternion rot = Quaternion.LookRotation(aim - eye, Vector3.up);
            float need = Needed(eye, rot, pts, 0.8f);
            for (int k = 0; k < 6 && (float.IsInfinity(need) || need > 85f); k++)
            {
                back *= 1.3f;
                height *= 1.3f;
                eye = aim + away * back + Vector3.up * height;
                rot = Quaternion.LookRotation(aim - eye, Vector3.up);
                need = Needed(eye, rot, pts, 0.8f);
            }
            float fov = float.IsInfinity(need) ? 85f : Mathf.Clamp(need, 40f, 85f);
            return Take(screen, what, eye, rot, fov, Vector3.Distance(eye, aim));
        }

        // The head exactly as it is now, at the head camera's own field of view (what a user sees without moving).
        static bool TakeHeadView(string screen, string what, Vector3 target)
        {
            Camera cam = PetUser.HeadCamera;
            if (cam == null) { Unreachable(screen, "there is no head camera"); return false; }
            return Take(screen, what, cam.transform.position, cam.transform.rotation, defaultFov, Vector3.Distance(cam.transform.position, target));
        }

        // ---------------------------------------------------------------- pressing and holding, the way a user does

        static bool PressButton(string label, Component scope)
        {
            Selectable[] all = scope != null
                ? scope.GetComponentsInChildren<Selectable>(false)
                : UnityEngine.Object.FindObjectsByType<Selectable>(FindObjectsInactive.Exclude);
            foreach (Selectable s in all)
            {
                if (s == null) continue;
                if (s.name == "Button " + label || s.name == "Tab " + label || s.name == label) return PressButton(s.gameObject, label);
            }
            Problem("press '" + label + "': there is no control with that label on screen");
            return false;
        }

        static Selectable FindControl(string label, Component scope)
        {
            foreach (Selectable s in scope.GetComponentsInChildren<Selectable>(false))
                if (s != null && (s.name == "Button " + label || s.name == "Tab " + label || s.name == label)) return s;
            return null;
        }

        // A real pointer press (enter, down, up, click, exit) on what is under the control's centre on the screen.
        static bool PressButton(GameObject target, string what)
        {
            if (target == null || !target.activeInHierarchy) { Problem("press '" + what + "': it is not there"); return false; }
            var selectable = target.GetComponent<Selectable>();
            if (selectable == null || !selectable.IsInteractable()) { Problem("press '" + what + "': it can't be pressed now"); return false; }
            EventSystem events = EventSystem.current;
            Camera cam = PetUser.HeadCamera;
            if (events == null || cam == null) { Problem("press '" + what + "': no EventSystem or head camera"); return false; }
            var rect = target.transform as RectTransform;
            Vector3 world = rect != null ? rect.TransformPoint(rect.rect.center) : target.transform.position;
            Vector3 view = cam.WorldToViewportPoint(world);
            if (!(view.z > 0.1f && view.x > 0.08f && view.x < 0.92f && view.y > 0.08f && view.y < 0.92f) && rig != null)
            {
                rig.LookAt(world);
                view = cam.WorldToViewportPoint(world);
            }
            if (view.z <= 0f) { Problem("press '" + what + "': it is behind the user"); return false; }
            Vector3 s3 = cam.WorldToScreenPoint(world);
            var pointer = new PointerEventData(events) { button = PointerEventData.InputButton.Left, position = new Vector2(s3.x, s3.y), pressPosition = new Vector2(s3.x, s3.y) };
            var hits = new List<RaycastResult>();
            events.RaycastAll(pointer, hits);
            RaycastResult hit = default;
            float reach = rig != null ? rig.reach : 4f;
            foreach (RaycastResult h in hits)
                if (h.gameObject != null && h.distance <= reach) { hit = h; break; }
            if (hit.gameObject == null) { Problem("press '" + what + "': nothing under the pointer"); return false; }
            if (!hit.gameObject.transform.IsChildOf(target.transform)) { Problem("press '" + what + "': '" + hit.gameObject.name + "' is on top of it"); return false; }
            pointer.pointerCurrentRaycast = hit;
            pointer.pointerPressRaycast = hit;
            ExecuteEvents.ExecuteHierarchy(hit.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
            GameObject pressed = ExecuteEvents.ExecuteHierarchy(hit.gameObject, pointer, ExecuteEvents.pointerDownHandler)
                                 ?? ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
            pointer.pointerPress = pressed;
            pointer.rawPointerPress = hit.gameObject;
            pointer.eligibleForClick = true;
            if (pressed != null)
            {
                ExecuteEvents.Execute(pressed, pointer, ExecuteEvents.pointerUpHandler);
                if (ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject) == pressed)
                    ExecuteEvents.Execute(pressed, pointer, ExecuteEvents.pointerClickHandler);
            }
            ExecuteEvents.ExecuteHierarchy(hit.gameObject, pointer, ExecuteEvents.pointerExitHandler);
            return true;
        }

        // Hover and pressed states: the pointer enters the control, and (for pressed) goes down on it and stays down; Release
        // lets go without a click and leaves.
        static GameObject heldTarget, heldPressed;
        static PointerEventData heldData;

        static void Hover(GameObject target)
        {
            Camera cam = PetUser.HeadCamera;
            var rect = (RectTransform)target.transform;
            Vector3 s = cam.WorldToScreenPoint(rect.TransformPoint(rect.rect.center));
            heldData = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left, position = new Vector2(s.x, s.y), pressPosition = new Vector2(s.x, s.y) };
            heldTarget = target;
            heldPressed = null;
            ExecuteEvents.ExecuteHierarchy(target, heldData, ExecuteEvents.pointerEnterHandler);
        }

        static void Down()
        {
            if (heldTarget == null) return;
            heldPressed = ExecuteEvents.ExecuteHierarchy(heldTarget, heldData, ExecuteEvents.pointerDownHandler);
            heldData.pointerPress = heldPressed;
            heldData.rawPointerPress = heldTarget;
        }

        static void Release()
        {
            if (heldTarget == null) return;
            if (heldPressed != null) ExecuteEvents.Execute(heldPressed, heldData, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.ExecuteHierarchy(heldTarget, heldData, ExecuteEvents.pointerExitHandler);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            heldTarget = heldPressed = null;
            heldData = null;
        }

        static IEnumerable<float> EnsureMenuOpen()
        {
            if (shell.MenuOpen) yield break;
            if (shell.AtDesk && tableButton != null && tableButton.Shown && tableButton.Button != null)
                PressButton(tableButton.Button.gameObject, "table button");
            foreach (float w in Until(() => shell.MenuOpen, 1.5f)) yield return w;
            if (!shell.MenuOpen)
            {
                shell.OpenMenu(OSSummon.Key);
                foreach (float w in Until(() => shell.MenuOpen, 1f)) yield return w;
            }
            yield return 0.7f;
        }

        static IEnumerable<float> OpenTab(OSTab tab)
        {
            if (shell.Tab != tab)
            {
                PressButton("Tab " + tab, menu);
                yield return 0.4f;
                if (shell.Tab != tab) shell.SetTab(tab);
            }
            yield return 0.4f;
        }

        static bool FrameMenu(string screen, string what)
        {
            if (menu == null || !menu.Visible) { Unreachable(screen, "the menu is not showing"); return false; }
            if (menuPlate != null) return FrameRect(screen, what, menuPlate);
            Vector2 size = menu.SizeMeters;
            var pts = new List<Vector3>();
            Quaternion q = menu.LastRotation;
            for (int i = 0; i < 4; i++)
                pts.Add(menu.LastCenter + q * new Vector3((i & 1) == 0 ? -size.x : size.x, (i & 2) == 0 ? -size.y : size.y, 0f) * 0.5f);
            return FrameSquare(screen, what, pts, q * Vector3.forward, q * Vector3.up);
        }

        static void MenuPoints(List<Vector3> pts)
        {
            if (menuPlate != null) AddRect(pts, menuPlate);
        }

        static OSTileView FindTile(string id)
        {
            IReadOnlyList<OSTileView> tiles = menu.ActiveTiles;
            for (int i = 0; i < tiles.Count; i++)
                if (tiles[i] != null && tiles[i].entry != null && tiles[i].entry.id == id && tiles[i].gameObject.activeInHierarchy) return tiles[i];
            return null;
        }

        // ---------------------------------------------------------------- the sections

        sealed class Step
        {
            public readonly string name;
            public readonly Func<IEnumerator<float>> run;
            public Step(string name, Func<IEnumerator<float>> run) { this.name = name; this.run = run; }
        }

        /// <summary>The section names <see cref="RunOnly"/> takes.</summary>
        public const string Sections = "table,menu,states,windows,settings,agents,build,drive,modes,artifacts,truck,three,away,closed";

        static List<Step> Steps() => new List<Step>
        {
            new Step("setup", StepSetup),
            new Step("table", StepTable),
            new Step("menu", StepMenu),
            new Step("states", StepStates),
            new Step("windows", StepWindows),
            new Step("settings", StepSettings),
            new Step("agents", StepAgents),
            new Step("build", StepBuild),
            new Step("drive", StepDriveIdle),
            new Step("modes", StepModes),
            new Step("artifacts", StepArtifacts),
            new Step("truck", StepTruck),
            new Step("three", StepThree),
            new Step("away", StepAway),
            new Step("closed", StepClosed),
        };

        static IEnumerator<float> Script()
        {
            string only = SessionState.GetString(OnlyKey, "");
            var wanted = new HashSet<string>(only.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            foreach (Step step in Steps())
            {
                if (step.name != "setup" && wanted.Count > 0 && !wanted.Contains(step.name)) continue;
                currentStep = step.name;
                Debug.Log("[OSLookShots] --- section " + step.name);
                IEnumerator<float> it = step.run();
                while (true)
                {
                    bool more;
                    try { more = it.MoveNext(); }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                        Problem("the section threw " + e.GetType().Name + ": " + e.Message);
                        break;
                    }
                    if (!more) break;
                    yield return it.Current;
                }
                try { Release(); } catch (Exception) { }
            }
        }

        // Arrive, sit at the desk, let the greeting go, and give the run its own data: a started truck project (plan, versions,
        // attempts, a slalom result) and two made artifacts, so every list has something real in it.
        static IEnumerator<float> StepSetup()
        {
            foreach (float w in Until(() => Workspace.Current != null && OSShell.Current != null && global::DesktopRig.Current != null, 30f)) yield return w;
            if (!Ready()) { Problem("the OS shell, the workspace and the desktop rig are not all in Main"); yield break; }
            foreach (float w in Until(() =>
            {
                if (Workspace.Current != null) Workspace.Current.presence.forceSeated = true;
                return Workspace.Current != null && Workspace.Current.presence.AtDesk;
            }, 25f)) yield return w;
            yield return 0.5f;
            rig.ToggleSeat();
            yield return 1.2f;
            var greeting = UnityEngine.Object.FindAnyObjectByType<ArrivalGreeting>(FindObjectsInactive.Include);
            if (greeting != null && greeting.IsShown) greeting.Fold(true);
            if (workspace.DockHeld) workspace.HoldDock(false);
            workspace.Close();
            shell.Windows.CloseAll();
            if (shell.MenuOpen) shell.CloseMenu();
            OSBuildFlow.VoiceLiveOverride = false;
            yield return 1.2f;
            deskRigPosition = rig.transform.position;
            deskRigRotation = rig.transform.rotation;

            // The truck project, as a few sessions of work would leave it.
            NotebookStore.Open(NotebookStore.DefaultProject, tempRoot);
            if (NotebookStore.Entries.Count == 0 && NotebookStore.Versions.Count == 0) NotebookStore.SeedFixtures();
            var milestones = new JArray();
            string[] steps8 = { "Drives on the real floor", "Finishes the slalom by hand", "Autopilot finishes the slalom", "A clean lap at 2 m/s or more", "Three clean laps in a row", "Send the model to a friend", "Tune the steering near the cones" };
            for (int i = 0; i < steps8.Length; i++) milestones.Add(new JObject { ["id"] = "m" + (i + 1), ["text"] = steps8[i], ["done"] = i < 3 });
            NotebookStore.RevisePlan("The truck project, planned with Albie",
                "An RC truck that does a slalom around the couch and the coffee table without touching a single cone, at walking speed.",
                "Finish the slalom three times in a row with no cones hit and no hand on the controls.", milestones);
            NotebookStore.CutVersion("thing", "Autopilot max speed raised to 2.2 m/s");
            NotebookStore.CutVersion("thing", "Wider track and a lower centre of mass");
            NotebookStore.CutVersion("thing", "Softer steering near the cones, tuned on the real floor");
            string first = NotebookStore.Log("attempt", "Cut the top speed in the tight corners", "The truck was clipping the third cone.", "albie", "A clean lap", new[] { "vehicle" });
            NotebookStore.Mark(first, "worked", "person");
            string second = NotebookStore.Log("attempt", "Wider turning circle on the last cone", "It swung wide into the table leg.", "albie", "No contact with the cones", new[] { "vehicle" });
            NotebookStore.Mark(second, "failed", "person");
            NotebookStore.Log("attempt", "Softer steering near the cones", "Trying it on the next lap.", "albie", "A clean lap", new[] { "vehicle" });  // left waiting for a verdict
            var record = new JObject
            {
                ["schema"] = ArtifactRuns.Schema,
                ["artifact_id"] = "slalom",
                ["run_id"] = ArtifactRuns.NextId("slalom"),
                ["project_id"] = NotebookStore.DefaultProject,
                ["outcome"] = "completed",
                ["metrics"] = new JObject { ["completion_time_s"] = 12.4 },
            };
            ArtifactRuns.Save(record);
            // The slalom was made earlier too (written to the library, not opened), so the truck project lists it with its run.
            try { ArtifactLibrary.FromTemplate("slalom", null); }
            catch (Exception e) { Problem("could not make the slalom in the library: " + e.Message); }
            OSProjects.Register();
            OSProjects.Refresh();

            // Two artifacts made earlier (the heart and the solar system), so the Artifacts tab has its made ones and its New row.
            ArtifactHost host = ArtifactHost.Current ?? ArtifactBootstrap.Ensure();
            if (host != null) host.Room = MockFloorPlan.LivingRoom();
            foreach (string id in new[] { "new_heart", "new_solar_system" })
            {
                OSRegistry.Find(OSTab.Artifacts, id)?.open?.Invoke();
                yield return 1.5f;
                AlbieTools.Call("artifact_close", new JObject());
                yield return 0.6f;
            }
            shell.Windows.CloseAll();
            yield return 0.8f;
            OSRegistry.NotifyChanged();
        }

        // The button on the table: as the seated user sees it, and straight down on it.
        static IEnumerator<float> StepTable()
        {
            if (!Ready()) yield break;
            if (shell.MenuOpen) shell.CloseMenu();
            shell.Windows.CloseAll();
            foreach (float w in Until(() => tableButton != null && tableButton.Shown && tableButton.Button != null, 8f)) yield return w;
            if (tableButton == null || !tableButton.Shown) { Unreachable("table_button", "the table button never showed"); yield break; }
            yield return 0.5f;
            Vector3 at = ((RectTransform)tableButton.Button.transform).TransformPoint(((RectTransform)tableButton.Button.transform).rect.center);
            rig.LookAt(at);
            yield return 0.5f;
            TakeHeadView("table_button_seated", "The round Albie button on the table seen from the seated user's head, the desk around it.", at);
            // Straight down on it, as close as the user would lean (0.45 m).
            RectTransform plate = tableButton.Button.GetComponentInParent<Canvas>().rootCanvas.GetComponent<RectTransform>();
            FrameRect("table_button_close", "The round button on the table, the icon and its plate large, seen straight down from 0.45 m.", plate, 0.7f);
        }

        // The three tabs and their pages, from the table button.
        static IEnumerator<float> StepMenu()
        {
            if (!Ready()) yield break;
            shell.Windows.CloseAll();
            if (shell.MenuOpen) { shell.CloseMenu(); yield return 0.6f; }
            shell.SetTab(OSTab.Tools);
            foreach (float w in EnsureMenuOpen()) yield return w;
            Problem0(shell.MenuOpen, "the table button opens the menu");
            foreach (float w in OpenTab(OSTab.Tools)) yield return w;
            yield return 0.5f;
            FrameMenu("menu_tools", "The menu on the Tools tab: the tab bar, eight tool tiles (CAD, Code, Train, Notebook, Messages, Gestures, Drive, Agents) with a status line each, and the bottom strip (Build something, Agents, Settings, Bring windows here, Close).");
            foreach (float w in OpenTab(OSTab.Projects)) yield return w;
            FrameMenu("menu_projects", "The menu on the Projects tab: New project and the person's own projects first, then the four samples under their heading (RC truck started, with its version and a passed slalom; Quadcopter, Rocket and Image classifier saying what each shows), and the strip.");
            foreach (float w in OpenTab(OSTab.Artifacts)) yield return w;
            FrameMenu("menu_artifacts_page1", "The menu on the Artifacts tab, page 1: the artifacts already made (slalom, heart, solar system) first, then the New heading with template tiles, and the page arrows.");
            PressButton("chevron_right", menu);
            yield return 0.5f;
            FrameMenu("menu_artifacts_page2", "The menu on the Artifacts tab, page 2: the rest of the New templates (slalom, hoop course, repo activity, the biology models, the simulations).");
            PressButton("chevron_left", menu);
            yield return 0.4f;
            foreach (float w in OpenTab(OSTab.Tools)) yield return w;
        }

        static void Problem0(bool ok, string what)
        {
            if (!ok) Problem(what + " (it did not)");
        }

        // A tile and a strip button, with the pointer over them and with a press held on them.
        static IEnumerator<float> StepStates()
        {
            if (!Ready()) yield break;
            shell.Windows.CloseAll();
            foreach (float w in EnsureMenuOpen()) yield return w;
            foreach (float w in OpenTab(OSTab.Tools)) yield return w;
            OSTileView tile = FindTile("notebook");
            if (tile == null) { Unreachable("menu_tile_hover", "no Notebook tile on this page"); }
            else
            {
                Hover(tile.button.gameObject);
                yield return 0.5f;
                FrameMenu("menu_tile_hover", "The Tools menu with the pointer over the Notebook tile: that tile is lifted and lit, the others are at rest.");
                FrameRect("menu_tile_hover_close", "The Notebook tile and its neighbours with the pointer over it, close up: the lifted, lit tile against the resting ones.", tile.rect, 0.9f, 0.45f, 0.9f, 2.0f, 1.7f);
                Down();
                yield return 0.5f;
                FrameMenu("menu_tile_pressed", "The Tools menu with a press held on the Notebook tile: that tile is pushed in and darker.");
                FrameRect("menu_tile_pressed_close", "The Notebook tile held pressed, close up: pushed in against the resting tiles beside it.", tile.rect, 0.9f, 0.45f, 0.9f, 2.0f, 1.7f);
                Release();
                yield return 0.4f;
            }
            Selectable agents = FindControl("Agents", menu);
            if (agents == null) Unreachable("menu_strip_hover", "no Agents button on the strip");
            else
            {
                Hover(agents.gameObject);
                yield return 0.5f;
                FrameMenu("menu_strip_hover", "The menu with the pointer over the Agents button on the bottom strip: that button is lit.");
                FrameRect("menu_strip_hover_close", "The bottom strip close up with the pointer over Agents: the lit button next to its resting neighbours.", (RectTransform)agents.transform, 0.9f, 0.45f, 0.9f, 2.6f, 3.2f);
                Release();
                yield return 0.3f;
            }
            Selectable build = FindControl("New project", menu);
            if (build == null) Unreachable("menu_strip_pressed", "no Build something button on the strip");
            else
            {
                Hover(build.gameObject);
                Down();
                yield return 0.5f;
                FrameMenu("menu_strip_pressed", "The menu with a press held on the primary Build something button: the button is pushed in and darker.");
                FrameRect("menu_strip_pressed_close", "The bottom strip close up with a press held on Build something: the pushed-in primary button beside the others.", (RectTransform)build.transform, 0.9f, 0.45f, 0.9f, 2.6f, 3.2f);
                Release();
                yield return 0.4f;
            }
        }

        // The windows of the Tools and Projects tabs: Notebook, the truck project (full plan), the classifier project, Gestures.
        static IEnumerator<float> StepWindows()
        {
            if (!Ready()) yield break;
            if (shell.MenuOpen) shell.CloseMenu();
            shell.Windows.CloseAll();
            OSModes.Close();
            yield return 0.8f;

            OSNotebookWindow.Open();
            yield return 1.2f;
            FrameWindow("notebook_window", "The Notebook window for the RC truck: the plan (goal, test, milestones), the newest attempts with their outcomes, the newest attempt 'Waiting for your verdict' with Mark worked and Mark failed, the versions and the lessons.", shell.Windows.Find(OSNotebookWindow.WindowId));
            shell.Windows.CloseAll();
            yield return 0.6f;

            OSProjects.OpenProject("proj_truck");
            yield return 1.5f;
            FrameWindow("project_truck_full", "The RC truck sample's Project window with a full plan: the header card (SAMPLE · LOOK AROUND, what it shows, its test), Make my own RC truck as the one primary button beside Next, Notebook and Send, the steps card (Course, Drive, Train, Autopilot, Code as a stepper of chips: done, next, upcoming) with the last result, and the two-column summary (plan and milestones, newest version with its outcome, last training, last run, artifacts).", shell.Windows.Find(OSProjectWindow.WindowId));
            shell.Windows.CloseAll();
            OSModes.Close();
            yield return 0.8f;

            OSProjects.OpenProject("proj_classifier");
            yield return 2.5f;
            {
                // Opening the classifier also opens the Train mode: the pair first, as the user sees them, then the window alone.
                var pair = new List<Vector3>();
                ModePoints(pair, true);
                FrameFromHead("project_classifier_with_train", "What the user gets after tapping Image classifier: the Train mode opens and the Project window is fully visible beside it, clear of the Training panel.", pair);
            }
            OSModes.Close();
            yield return 1.5f;
            FrameWindow("project_classifier_offline", "The Image classifier Project window offline (opened together with Train): its honest line 'Needs Albie's cloud to load the dataset. Not available offline.' and the Load dataset button switched off.", shell.Windows.Find(OSProjectWindow.WindowId));
            shell.Windows.CloseAll();
            OSModes.Close();
            yield return 0.8f;
            OSProjects.OpenProject("proj_truck");  // the truck project is the open one again for what follows
            shell.Windows.CloseAll();
            OSModes.Close();
            yield return 0.8f;

            OSRegistry.Find(OSTab.Tools, "gestures")?.open?.Invoke();
            yield return 1.5f;
            FrameWindow("gesture_display", "The gesture display window (Hands): the hand display with its gesture list and Teach a gesture.", shell.Windows.Find(OSToolsCatalog.GesturesId));
            shell.Windows.CloseAll();
            yield return 1.0f;
        }

        static IEnumerator<float> StepSettings()
        {
            if (!Ready()) yield break;
            if (shell.MenuOpen) shell.CloseMenu();
            shell.Windows.CloseAll();
            yield return 0.6f;
            OSSettingsWindow.Open();
            yield return 1.2f;
            OSWindow win = shell.Windows.Find(OSSettingsWindow.WindowId);
            OSSettingsWindow view = win != null ? win.GetComponent<OSSettingsWindow>() : null;
            if (view == null) { Unreachable("settings_page1", "the Settings window did not open"); yield break; }
            string[] what =
            {
                "Settings, page 1 of 3: the three menu button switches (Table button, Watch turn, Corner button), the corner side, hands-free voice and the gesture display.",
                "Settings, page 2 of 3: window layout, the Distance and Height sliders with their values.",
                "Settings, page 3 of 3: Reset window positions, Forget permissions, and About with the version and credits.",
            };
            for (int page = 0; page < OSSettingsWindow.PageCount; page++)
            {
                view.SetPage(page);
                yield return 0.7f;
                FrameWindow("settings_page" + (page + 1), what[Mathf.Min(page, what.Length - 1)], win);
            }
            // The confirm card of Forget permissions (the last page).
            view.SetPage(OSSettingsWindow.PageCount - 1);
            yield return 0.6f;
            if (PressButton("Forget permissions", view))
            {
                yield return 0.7f;
                FrameWindow("settings_confirm_forget_permissions", "The Settings window with the confirm card open on 'Forget every permission...': the question and its Cancel and confirm buttons, nothing forgotten yet.", win);
                Selectable cancel = FindControl("Cancel", view);
                if (cancel != null) PressButton(cancel.gameObject, "Cancel");
                yield return 0.5f;
            }
            shell.Windows.CloseAll();
            yield return 0.6f;
        }

        static void DismissAgents()
        {
            AlbieCrew crew = AlbieCrew.Instance;
            if (crew == null) return;
            var ids = new List<string>();
            for (int i = 0; i < crew.Members.Count; i++) if (crew.Members[i] != null) ids.Add(crew.Members[i].PetId);
            foreach (string id in ids) AlbieTools.Call("dismiss_agent", new JObject { ["agent"] = id });
        }

        static void StopAgentHold()
        {
            if (agentHold != null) { try { agentHold.Stop(); } catch (System.Net.Sockets.SocketException) { } agentHold = null; }
            try { if (EditorPrefs.GetBool("OSTestEnv.Pending", false)) OSTestEnv.ClearClaudeUrl(); } catch (InvalidOperationException) { }
        }

        static IEnumerator<float> StepAgents()
        {
            if (!Ready()) yield break;
            if (shell.MenuOpen) shell.CloseMenu();
            shell.Windows.CloseAll();
            foreach (float w in Until(() => AlbieCrew.Instance != null, 8f)) yield return w;
            AlbieCrew crew = AlbieCrew.Instance;
            if (crew == null) { Unreachable("agents_two_rows", "no crew in Main"); yield break; }
            DismissAgents();
            yield return 0.8f;
            try
            {
                // The empty state first.
                OSAgentsWindow.Open();
                yield return 1.0f;
                FrameWindow("agents_empty", "The Agents window with no agents: Albie as the lead and the empty state telling what to do.", shell.Windows.Find(OSAgentsWindow.WindowId));
                shell.Windows.CloseAll();
                yield return 0.5f;

                agentHold = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                agentHold.Start();
                OSTestEnv.SetClaudeUrl("http://127.0.0.1:" + ((System.Net.IPEndPoint)agentHold.LocalEndpoint).Port + "/api/claude");
                AlbieTools.Call("spawn_agent", new JObject { ["type"] = "builder" });
                AlbieTools.Call("spawn_agent", new JObject { ["type"] = "scout" });
                yield return 1.0f;
                AlbieTools.Call("assign_task", new JObject { ["agent"] = "builder", ["task"] = AgentTask, ["mode"] = "qa" });
                yield return 1.2f;
                OSAgentsWindow.Open();
                yield return 1.5f;
                OSWindow win = shell.Windows.Find(OSAgentsWindow.WindowId);
                OSAgentsWindow view = win != null ? win.GetComponent<OSAgentsWindow>() : null;
                if (view == null) { Unreachable("agents_two_rows", "the Agents window did not open"); yield break; }
                FrameWindow("agents_two_rows", "The Agents window with two agents (a builder working on a task, a scout) each with its type, name, what it is doing, Walking, and the Bring home and Dismiss buttons.", win);

                view.Flip("builder");  // Bring home: the task keeps running
                yield return 1.2f;
                FrameWindow("agents_one_home", "The Agents window with the builder brought home: its row says Home and still shows its task, and its button reads Send out; the status line says its task keeps running.", win);
                var pts = new List<Vector3>();
                AddWindow(pts, win);
                OSHomePad pad = UnityEngine.Object.FindAnyObjectByType<OSHomePad>();
                if (pad != null) AddBounds(pts, new Bounds(pad.transform.position, new Vector3(0.25f, 0.2f, 0.25f)));
                FrameFromHead("agents_one_home_desk", "The Agents window beside the desk with the builder at home on its pad (context for the Bring home state).", pts);

                view.AskDismiss("scout");
                yield return 0.8f;
                FrameWindow("agents_dismiss_confirm", "The Agents window with the Dismiss confirm card: 'Dismiss Scout? It goes away and its task stops.' with Cancel and Dismiss.", win);
                Selectable cancel = FindControl("Cancel", view);
                if (cancel != null) PressButton(cancel.gameObject, "Cancel");
                yield return 0.5f;
            }
            finally
            {
                shell.Windows.CloseAll();
                DismissAgents();
                StopAgentHold();
            }
            yield return 1.0f;
        }

        // Build something: the first question, the second, and the plan card (then Cancel: nothing is created).
        static IEnumerator<float> StepBuild()
        {
            if (!Ready()) yield break;
            shell.Windows.CloseAll();
            if (shell.MenuOpen) shell.CloseMenu();
            yield return 0.5f;
            OSBuildFlow.VoiceLiveOverride = false;
            OSBuildFlow.Open();
            yield return 1.0f;
            OSWindow card = shell.Windows.Find(OSBuildFlow.WindowId);
            OSBuildFlow flow = OSBuildFlow.Current;
            if (card == null || flow == null) { Unreachable("build_question1", "the Build something card did not open"); yield break; }
            FrameWindow("build_question1", "Build something, question 1 of 3: 'What are we building?' with its tappable answers (RC truck, Drone, Rocket, A model that recognises things) and the one primary look.", card);
            flow.Pick(0);
            yield return 0.8f;
            FrameWindow("build_question2", "Build something, question 2: the first follow-up for the RC truck with its tappable answers.", card);
            flow.Pick(0);
            yield return 0.8f;
            if (!flow.Planner.Done)
            {
                FrameWindow("build_question3", "Build something, question 3: the second follow-up for the RC truck with its tappable answers.", card);
                flow.Pick(0);
                yield return 0.8f;
            }
            if (flow.Planner.Done) FrameWindow("build_plan_card", "The Build something plan card: the goal, the parts, the test and the milestones, with Start (primary), Change and Cancel.", card);
            else Problem("the planner did not reach the plan card");
            flow.Cancel();
            yield return 0.6f;
            shell.Windows.CloseAll();
            yield return 0.4f;
        }

        // The Drive window with no truck around: idle.
        static IEnumerator<float> StepDriveIdle()
        {
            if (!Ready()) yield break;
            shell.Windows.CloseAll();
            if (shell.MenuOpen) shell.CloseMenu();
            OSRegistry.Find(OSTab.Tools, "drive")?.open?.Invoke();
            yield return 1.2f;
            FrameWindow("drive_idle", "The Drive window idle with no truck: the two-axis pad with its knob at the centre, the big Stop, and the line saying there is nothing to drive yet.", shell.Windows.Find(OSDriveWindow.WindowId));
            shell.Windows.CloseAll();
            yield return 0.6f;
        }

        // The old modes beside an OS window, each from where the user sits.
        static IEnumerator<float> StepModes()
        {
            if (!Ready()) yield break;
            shell.Windows.CloseAll();
            if (shell.MenuOpen) shell.CloseMenu();
            OSModes.Close();
            yield return 1.0f;

            var pts = new List<Vector3>();

            // CAD plus Notebook
            OSModes.Open("cad");
            yield return 3.0f;
            OSNotebookWindow.Open();
            yield return 1.5f;
            ModePoints(pts, true);
            FrameFromHead("mode_cad_with_notebook", "The CAD mode (its panels and part) beside the OS Notebook window: do the old and the new windows look like one product (corners, header, colours, text)?", pts);
            FrameMainModePanel("mode_cad_panel", "The main CAD panel on its own, square on from where the user leans in: the old window's header, corners and text beside what the OS windows use.");
            shell.Windows.CloseAll();
            OSModes.Close();
            yield return 1.5f;

            // Code plus the project window
            OSModes.Open("code");
            yield return 3.0f;
            OSProjects.OpenProject("proj_truck");
            yield return 1.5f;
            ModePoints(pts, true);
            FrameFromHead("mode_code_with_project", "The Code mode beside the OS RC truck Project window: the old code window and the new project window side by side.", pts);
            FrameMainModePanel("mode_code_panel", "The Code panel on its own, square on: the code listing, its file list and buttons.");
            shell.Windows.CloseAll();
            OSModes.Close();
            yield return 1.5f;

            // Train plus the menu
            OSModes.Open("ml");
            yield return 3.0f;
            shell.OpenMenu(OSSummon.Key);
            yield return 1.2f;
            pts.Clear();
            MenuPoints(pts);
            ModePoints(pts, false);
            FrameFromHead("mode_train_with_menu", "The Train mode (the ML desk panels) with the menu open in front of it: the menu is complete and on top, the Train panels beside and behind it.", pts);
            FrameMenu("menu_over_train", "The menu opened over the Train mode, seen square on from 0.45 m: nothing from Train drawn over any tile.");
            shell.CloseMenu();
            yield return 0.8f;
            FrameMainModePanel("mode_train_panel", "The biggest Train panel on its own, square on: the old ML window's header, text and charts.");
            OSModes.Close();
            yield return 1.5f;

            // Messages plus an OS window
            OSModes.Open("messages");
            yield return 3.0f;
            OSSettingsWindow.Open();
            yield return 1.5f;
            ModePoints(pts, true);
            FrameFromHead("mode_messages_with_settings", "The Messages mode beside the OS Settings window: the old messages panel and the new window side by side.", pts);
            FrameMainModePanel("mode_messages_panel", "The Messages panel on its own, square on: channel list, conversation and the message box.");
            shell.Windows.CloseAll();
            OSModes.Close();
            yield return 1.2f;
        }

        // The biggest panel of the open mode, square on: the old window as the user would lean in to read it.
        static void FrameMainModePanel(string screen, string what)
        {
            ForgePanel best = null;
            float bestArea = 0f;
            foreach (ForgePanel p in ForgePanel.All)
            {
                if (p == null || !p.gameObject.activeInHierarchy || p.transform.lossyScale.x < 0.0005f || !workspace.IsOpenItem(p.gameObject)) continue;
                Rect r = ((RectTransform)p.transform).rect;
                float area = r.width * r.height * p.transform.lossyScale.x * p.transform.lossyScale.y;
                if (area > bestArea) { best = p; bestArea = area; }
            }
            if (best == null) { Unreachable(screen, "the mode opened no panel"); return; }
            // The mode's 3D parts (a CAD part and its plinth, Train's slabs) stand between the camera and the panel: left out of this picture.
            alsoHide.Clear();
            foreach (Renderer r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude))
            {
                if (r == null || r.transform.IsChildOf(best.transform) || r.GetComponentInParent<Canvas>() != null) continue;
                for (Transform t = r.transform; t != null; t = t.parent)
                    if (workspace.IsOpenItem(t.gameObject)) { alsoHide.Add(r); break; }
            }
            FrameRect(screen, what, (RectTransform)best.transform, 0.88f, 0.45f, 0.9f, 1f, 1f, best.transform);
            alsoHide.Clear();
        }

        // The corners of the open mode's panels (and, when asked, of every OS window).
        static void ModePoints(List<Vector3> pts, bool withWindows)
        {
            pts.Clear();
            foreach (ForgePanel p in ForgePanel.All)
            {
                if (p == null || !p.gameObject.activeInHierarchy || p.transform.lossyScale.x < 0.0005f) continue;
                if (!workspace.IsOpenItem(p.gameObject)) continue;
                AddRect(pts, (RectTransform)p.transform);
            }
            if (withWindows)
                for (int i = 0; i < shell.Windows.All.Count; i++)
                    AddWindow(pts, shell.Windows.All[i]);
        }

        static OSWindow FirstArtifactPanelWindow()
        {
            foreach (OSWindow w in shell.Windows.All)
                if (w != null && w.Id.StartsWith("artifact:", StringComparison.Ordinal) && w.Panel != null) return w;
            return null;
        }

        static void ArtifactPoints(List<Vector3> pts)
        {
            pts.Clear();
            foreach (OSWindow w in shell.Windows.All)
                if (w != null && w.Id.StartsWith("artifact:", StringComparison.Ordinal)) AddWindow(pts, w);
        }

        // Artifacts open: the heart, the repo activity (with its Allow sheet).
        static IEnumerator<float> StepArtifacts()
        {
            if (!Ready()) yield break;
            shell.Windows.CloseAll();
            if (shell.MenuOpen) shell.CloseMenu();
            AlbieTools.Call("artifact_close", new JObject());
            yield return 1.0f;
            var pts = new List<Vector3>();

            OSRegistry.Find(OSTab.Artifacts, "heart")?.open?.Invoke();
            yield return 1.5f;
            shell.Windows.BringHere();
            yield return 2.5f;
            ArtifactPoints(pts);
            if (pts.Count > 0) FrameFromHead("artifact_heart", "The heart artifact open: the 3D heart model as a window beside its info panel, the surface the user explores.", pts);
            else Unreachable("artifact_heart", "the heart opened no window");
            OSWindow heartPanel = FirstArtifactPanelWindow();
            if (heartPanel != null) FrameWindow("artifact_heart_panel", "The heart artifact's panel close up, square on: its title, labels and buttons.", heartPanel);
            AlbieTools.Call("artifact_close", new JObject());
            shell.Windows.CloseAll();
            yield return 1.0f;

            // Repo activity asks first: the Allow sheet, then the artifact.
            OSRegistry.Find(OSTab.Artifacts, "new_repo_activity")?.open?.Invoke();
            yield return 1.5f;
            shell.Windows.BringHere();
            yield return 2.5f;
            OSWindow sheet = FirstArtifactPanelWindow();
            if (sheet == null) Unreachable("artifact_repo_allow_sheet", "no Allow sheet appeared");
            else
            {
                FrameWindow("artifact_repo_allow_sheet", "The Allow sheet 'What shipped this week?' asks first: what the artifact will read, with Allow and Not now.", sheet);
                Button allow = null;
                foreach (Button b in sheet.GetComponentsInChildren<Button>(false))
                {
                    var text = b.GetComponentInChildren<TMPro.TMP_Text>(false);
                    if (text != null && text.text == "Allow") allow = b;
                }
                if (allow != null) PressButton(allow.gameObject, "Allow");
                else Problem("no Allow pill on the sheet");
                yield return 2.0f;
                shell.Windows.BringHere();
                yield return 2.5f;
                ArtifactPoints(pts);
                if (pts.Count > 0) FrameFromHead("artifact_repo_activity", "The repo activity artifact open after Allow: its panel (what shipped this week) and its 3D view.", pts);
                OSWindow repoPanel = FirstArtifactPanelWindow();
                if (repoPanel != null) FrameWindow("artifact_repo_activity_panel", "The repo activity panel close up, square on: its list and text.", repoPanel);
            }
            AlbieTools.Call("artifact_close", new JObject());
            shell.Windows.CloseAll();
            yield return 1.0f;
        }

        // Course from the truck project (the slalom asks first, then the brain truck appears): the slalom scoreboard, then Drive
        // recording.
        static IEnumerator<float> StepTruck()
        {
            if (!Ready()) yield break;
            shell.Windows.CloseAll();
            OSModes.Close();
            if (shell.MenuOpen) shell.CloseMenu();
            AlbieTools.Call("artifact_close", new JObject());
            ManualInputs.Clear();
            yield return 0.8f;
            ArtifactHost host = ArtifactHost.Current ?? ArtifactBootstrap.Ensure();
            if (host == null) { Unreachable("artifact_slalom_scoreboard", "no artifact host"); yield break; }
            host.Room = MockFloorPlan.LivingRoom();
            OSProjects.OpenProject("proj_truck");
            yield return 1.2f;
            OSProjectWindow project = shell.Windows.Find(OSProjectWindow.WindowId)?.GetComponent<OSProjectWindow>();
            if (project == null) { Unreachable("artifact_slalom_scoreboard", "the truck project window did not open"); yield break; }
            project.Do("course");
            yield return 1.0f;
            if (host.OpenManifest != null) ArtifactApprovals.Approve(host.OpenManifest);
            AlbieTools.Call("artifact_close", new JObject());
            yield return 0.6f;
            project.Do("course");
            foreach (float w in Until(() => BrainLoops.Active != null, 8f)) yield return w;
            yield return 1.0f;
            var brain = BrainLoops.Active as SlalomBrain;
            if (brain == null) { Unreachable("artifact_slalom_scoreboard", "Course gave no brain truck"); yield break; }
            brain.PlaceAtStart();
            bool truckHasModel = brain.GetComponentsInChildren<Renderer>(false).Length > 0;
            Debug.Log("[OSLookShots] the brain truck has " + brain.GetComponentsInChildren<Renderer>(false).Length + " renderers");
            shell.Windows.BringHere();
            yield return 2.5f;
            OSWindow board = FirstArtifactPanelWindow();
            if (board != null) FrameWindow("artifact_slalom_scoreboard", "The slalom scoreboard panel, square on: Time, Gates, Cones hit, Runs, the version comparison, the Spacing slider and Start a run.", board);
            else Unreachable("artifact_slalom_scoreboard", "the slalom opened no panel");
            var courseBox = new List<Vector3>();
            AddBounds(courseBox, new Bounds(brain.transform.position + Vector3.up * 0.08f, new Vector3(0.32f, 0.16f, 0.32f)));
            if (host.Build != null && host.Build.Slots.TryGetValue("course.gates", out var gatePoses))
            {
                Pose origin = host.SceneOrigin;
                for (int i = 0; i < Mathf.Min(3, gatePoses.Count); i++)
                    courseBox.Add(origin.position + origin.rotation * gatePoses[i].position);
            }
            FrameFromAbove("slalom_course_truck", "The slalom course on the floor, seen from above and a little to the side: the first gates and cones, the green line to drive, and the start gate where the brain truck stands" + (truckHasModel ? "." : " (the truck has no model in this desktop simulation, so only its start spot shows)."), courseBox, 1.0f, 0.9f);

            // Drive: the pad opens and a two-lap recording starts.
            project.Do("drive");
            yield return 1.5f;
            OSWindow drive = shell.Windows.Find(OSDriveWindow.WindowId);
            if (drive != null)
            {
                FrameWindow("drive_recording", "The Drive window while recording laps with the truck: the pad, the big Stop, and the lines saying it drives the truck and is recording lap 1 of 2.", drive);
                var pad = drive.GetComponent<OSDriveWindow>();
                if (pad != null) pad.Stop();
                yield return 0.5f;
            }
            else Unreachable("drive_recording", "the Drive action opened no window");
            shell.Windows.CloseAll();
            AlbieTools.Call("artifact_close", new JObject());
            host.Room = null;
            yield return 1.2f;
        }

        // Three windows open at the desk: Notebook, the project, an artifact.
        static IEnumerator<float> StepThree()
        {
            if (!Ready()) yield break;
            shell.Windows.CloseAll();
            OSModes.Close();
            if (shell.MenuOpen) shell.CloseMenu();
            AlbieTools.Call("artifact_close", new JObject());
            yield return 1.0f;
            OSNotebookWindow.Open();
            yield return 0.8f;
            OSProjects.OpenProject("proj_truck");
            yield return 0.8f;
            OSRegistry.Find(OSTab.Artifacts, "heart")?.open?.Invoke();
            yield return 1.5f;
            var pts = new List<Vector3>();
            for (int i = 0; i < shell.Windows.All.Count; i++) AddWindow(pts, shell.Windows.All[i]);
            rig.LookAt(CenterOf(pts));
            yield return 2.0f;  // the windows settle clear of each other
            pts.Clear();
            for (int i = 0; i < shell.Windows.All.Count; i++) AddWindow(pts, shell.Windows.All[i]);
            FrameFromHead("three_windows_desk", "Three windows open at once at the desk (Notebook, the RC truck project, the heart artifact): they sit around the desk without overlapping and each is readable.", pts);
        }

        static Vector3 CenterOf(List<Vector3> pts)
        {
            if (pts.Count == 0) return Vector3.zero;
            var b = new Bounds(pts[0], Vector3.zero);
            foreach (Vector3 p in pts) b.Encapsulate(p);
            return b.center;
        }

        // Walk away (the windows stay), then the menu, the corner button, the watch button and Bring windows here, all away from the desk.
        static IEnumerator<float> StepAway()
        {
            if (!Ready()) yield break;
            if (shell.Windows.Count == 0)
            {
                OSNotebookWindow.Open();
                OSProjects.OpenProject("proj_truck");
                yield return 1.5f;
            }
            var presence = workspace.presence;
            presence.forceSeated = false;
            rig.ToggleSeat();
            yield return 1.0f;
            // Away on the user's own side of the desk: opposite to the way the seated user looks at the desk, so the windows
            // (which face the seat) are seen from the front when the user turns round.
            Vector3 toDesk = Vector3.zero;
            if (tableButton != null && tableButton.Button != null) toDesk = Vector3.ProjectOnPlane(tableButton.Button.transform.position - rig.Head.position, Vector3.up);
            if (toDesk.sqrMagnitude < 0.01f) toDesk = Vector3.ProjectOnPlane(rig.Head.forward, Vector3.up);
            Vector3 back = -toDesk.normalized;
            var body = rig.GetComponent<CharacterController>();
            if (body != null) body.enabled = false;
            rig.transform.position += back * 2.0f;
            if (body != null) body.enabled = true;
            rig.LookAt(rig.Head.position + back * 2f);
            foreach (float w in Until(() => !presence.AtDesk, 5f)) yield return w;
            if (presence.AtDesk) Problem("the user did not go Away from the desk");
            yield return 0.8f;

            // The windows are still where they were: look back at them.
            var pts = new List<Vector3>();
            for (int i = 0; i < shell.Windows.All.Count; i++) AddWindow(pts, shell.Windows.All[i]);
            if (pts.Count > 0)
            {
                rig.LookAt(CenterOf(pts));
                yield return 0.6f;
                FrameFromHead("away_windows_stay", "Standing away from the desk and looking back: the windows are still open where they were at the desk.", pts, 0.9f, 35f, 80f);
                rig.LookAt(rig.Head.position + back * 2f);
                yield return 0.4f;
            }

            // The menu away from the desk.
            shell.OpenMenu(OSSummon.Key);
            yield return 1.2f;
            FrameMenu("menu_away", "The menu opened away from a desk, floating about arm's length in front of the user, nothing in front of it.");
            shell.CloseMenu();
            yield return 0.8f;

            // The corner button.
            var corner = shell.GetComponent<OSCornerButton>();
            if (corner == null) Unreachable("corner_button", "the shell has no corner button");
            else
            {
                foreach (float w in Until(() => corner.Shown, 3f)) yield return w;
                yield return 0.8f;
                if (!corner.Shown) Unreachable("corner_button", "the corner button never showed away from the desk");
                else
                {
                    TakeHeadView("corner_button_view", "What the user sees away from a desk: the small round corner button low in the corner of the view, the room behind it.", corner.Position);
                    var pb = new List<Vector3>();
                    AddRect(pb, (RectTransform)corner.Button.GetComponentInParent<Canvas>().rootCanvas.transform);
                    FrameSquare("corner_button_close", "The corner button close up, square on, from 0.45 m: the round plate and its icon.", pb, corner.Button.transform.forward, corner.Button.transform.up, 0.6f);
                }
            }

            // The watch button on the wrist.
            var watch = shell.GetComponent<OSWatch>();
            if (watch == null) Unreachable("watch_button", "the shell has no watch turn");
            else
            {
                Transform head = shell.Head;
                try
                {
                    SetLeftWrist(head, 0f);
                    foreach (float w in Until(() => watch.Shown, 3f)) yield return w;
                    yield return 0.6f;
                    if (!watch.Shown) Unreachable("watch_button", "turning the left wrist did not show the watch button");
                    else
                    {
                        rig.LookAt(watch.ButtonPosition);
                        yield return 0.4f;
                        TakeHeadView("watch_button_view", "The left wrist turned like a watch and the small round watch button hovering just above it, as the user sees it (the desktop simulation draws no left hand, so only the button shows; the corner button is also in view).", watch.ButtonPosition);
                        var pw = new List<Vector3>();
                        float r = watch.ButtonMeters * 0.5f * 1.6f;
                        Quaternion q = Quaternion.LookRotation(watch.ButtonForward, Vector3.up);
                        for (int i = 0; i < 4; i++) pw.Add(watch.ButtonPosition + q * new Vector3((i & 1) == 0 ? -r : r, (i & 2) == 0 ? -r : r, 0f));
                        FrameSquare("watch_button_close", "The watch button close up, square on, from 0.45 m: the round button above the wrist.", pw, watch.ButtonForward, Vector3.up, 0.5f);
                    }
                }
                finally
                {
                    SimulatedHandSource.LeftTestActive = false;
                }
                yield return 0.5f;
            }
            rig.LookAt(rig.Head.position + back * 2f);
            yield return 0.4f;

            // Bring windows here.
            shell.Windows.BringHere();
            yield return 2.5f;
            pts.Clear();
            for (int i = 0; i < shell.Windows.All.Count; i++) AddWindow(pts, shell.Windows.All[i]);
            if (pts.Count > 0)
            {
                rig.LookAt(CenterOf(pts));
                yield return 0.5f;
                FrameFromHead("away_after_bring_here", "After Bring windows here away from the desk: every window sprung back to a fan in front of the user, within reach.", pts, 0.9f, 35f, 100f);
            }
            else Unreachable("away_after_bring_here", "no windows to bring here");
        }

        static void SetLeftWrist(Transform head, float tilt)
        {
            Vector3 wrist = head.TransformPoint(new Vector3(-0.08f, -0.12f, 0.40f));
            Vector3 back = (head.position - wrist).normalized;
            Vector3 fingers = Vector3.ProjectOnPlane(head.right, back).normalized;
            SimulatedHandSource.LeftTestShape = HandShape.Relaxed;
            SimulatedHandSource.LeftTestWrist = wrist;
            SimulatedHandSource.LeftTestRotation = Quaternion.LookRotation(fingers, Quaternion.AngleAxis(tilt, fingers) * back);
            SimulatedHandSource.LeftTestActive = true;
        }

        // Close all (the tool), back at the desk: the empty desk with its table button.
        static IEnumerator<float> StepClosed()
        {
            if (!Ready()) yield break;
            if (shell.MenuOpen) shell.CloseMenu();
            JObject reply = AlbieTools.Call("os_windows", new JObject { ["action"] = "close_all" });
            if (reply == null || reply["error"] != null) { Problem("os_windows close_all answered an error"); shell.Windows.CloseAll(); }
            yield return 0.8f;
            if (shell.Windows.Count != 0) Problem("Close all left " + shell.Windows.Count + " windows");
            // Back at the desk.
            var presence = workspace.presence;
            if (!presence.AtDesk)
            {
                var body = rig.GetComponent<CharacterController>();
                if (body != null) body.enabled = false;
                rig.transform.SetPositionAndRotation(deskRigPosition, deskRigRotation);
                if (body != null) body.enabled = true;
                presence.forceSeated = true;
                foreach (float w in Until(() => presence.AtDesk, 5f)) yield return w;
                yield return 0.5f;
                if (!rig.Seated) rig.ToggleSeat();
                yield return 1.5f;
            }
            foreach (float w in Until(() => tableButton != null && tableButton.Shown, 4f)) yield return w;
            Transform frame = presence.Frame;
            if (frame == null) { Unreachable("desk_empty_after_close_all", "no desk frame"); yield break; }
            var pts = new List<Vector3>();
            float width = Mathf.Max(0.6f, presence.FrameWidth), depth = Mathf.Max(0.4f, presence.FrameDepth);
            for (int i = 0; i < 4; i++)
                pts.Add(frame.position + frame.right * ((i & 1) == 0 ? -width : width) * 0.5f + frame.forward * ((i & 2) == 0 ? 0f : depth));
            rig.LookAt(CenterOf(pts));
            yield return 0.8f;
            FrameFromHead("desk_empty_after_close_all", "The desk after Close all: nothing open, just the table button on the front edge and the empty desk top.", pts, 0.9f, 40f, 90f);
        }
    }
}
