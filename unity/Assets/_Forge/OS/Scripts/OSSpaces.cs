using System;
using System.Collections;
using System.Collections.Generic;
using Forge.Artifacts;
using Forge.Controllers;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.OS
{
    /// <summary>What a row of the Spaces list stands for.</summary>
    public enum SpaceKind { Window, Mode, Artifact, Course, Thing, Transmitter, Panel, Card, Albie }

    /// <summary>
    /// One thing open around the user, as the Spaces list shows it: what it is, where it is, and what its three buttons do.
    /// An action that does not apply is null (its button is shown switched off).
    /// </summary>
    public sealed class SpaceItem
    {
        /// <summary>Stable while the thing is open: "mode:cad", "window:settings", "artifact", "course", "panel:123".</summary>
        public string key;
        public SpaceKind kind;
        public string title, icon;
        /// <summary>Where it is in the world (its centre), when it has a place.</summary>
        public bool hasPlace;
        public Vector3 place;
        /// <summary>Put away (minimised into its dot, or hidden into the Spaces tray).</summary>
        public bool away;
        /// <summary>Out of the tray if it was put away, then in front of the user.</summary>
        public Action bring;
        /// <summary>Into the tray (a dot where it was, or hidden), restorable.</summary>
        public Action send;
        /// <summary>Back out of the tray, where it was.</summary>
        public Action restore;
        /// <summary>Closes it for real (a Thing is put away: its course closes).</summary>
        public Action close;
        public string closeLabel = "Close";
        /// <summary>Set when closing it now loses work in progress (a scored run, a lap being recorded): ask first.</summary>
        public string closeQuestion;
    }

    /// <summary>
    /// The Spaces manager: everything open around the user in one list (<see cref="Collect"/>), with Bring here, Send away
    /// and Close for each, and the same three for everything at once. It reads what is there from the registries that
    /// already exist rather than keeping its own: the window manager (<see cref="OSWindows"/>: OS windows, the open
    /// Workspace mode as a group, the open artifact as a whole and its adopted panels and volumes), the artifact host (the
    /// room course on the floor), the Things that host drives (the brain truck, the drone), the RC transmitter,
    /// <see cref="ForgePanel.All"/> for any panel nobody tracks (Albie's card, a module's own panel) and Albie himself.
    /// What it sends away it remembers in a small tray (a dot per panel, or a hidden object) so it can bring it back.
    /// <para>
    /// Close everything really closes: the transmitter is put away, the open artifact is closed (its course comes down and
    /// its truck or drone are put away), every window and the open mode close, and loose panels are hidden. When a scored
    /// run or a lap recording is going on it asks first (<see cref="AtRisk"/>), as the app's other destructive buttons do.
    /// The voice tools (Resources/AlbieTools/spaces.json) and <c>os_windows close_all</c> come here too.
    /// </para>
    /// Nothing here runs per frame: the Spaces window reads the list once a second.
    /// </summary>
    public static class OSSpaces
    {
        /// <summary>The Spaces window's id in the window manager, and its system page id ("os_open kind system id spaces").</summary>
        public const string WindowId = "spaces";
        /// <summary>The tool names this file registers; spaces.json must define exactly these.</summary>
        public static readonly string[] ToolNames = { "spaces_show", "spaces_bring_here", "spaces_send_away", "spaces_close", "spaces_close_all" };

        const float BringAfterRestoreSeconds = 0.45f;  // a dot grows back in 0.4 s; the windows glide in after that

        // What was put away and how, so it can come back: the dots the panels shrank into and the objects that were hidden.
        sealed class Tray
        {
            public string key, title, icon;
            public string owner;  // the mode or artifact id it was put away from: a record for one that has closed since is dropped
            public SpaceKind kind;
            public readonly List<GestureWindow> dots = new List<GestureWindow>();
            public readonly List<GameObject> hidden = new List<GameObject>();
            public Action restore;  // anything else (the transmitter's Show)
            public bool custom;

            public bool Still()
            {
                if (custom) return true;
                for (int i = 0; i < dots.Count; i++)
                    if (dots[i] != null && dots[i].IsMinimized) return true;
                for (int i = 0; i < hidden.Count; i++)
                    if (hidden[i] != null && !hidden[i].activeSelf) return true;
                return false;
            }
        }

        static readonly List<Tray> tray = new List<Tray>();
        static readonly List<ForgePanel> panelScratch = new List<ForgePanel>();
        static readonly List<SpaceItem> itemScratch = new List<SpaceItem>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            tray.Clear();
            panelScratch.Clear();
            itemScratch.Clear();
        }

        /// <summary>The voice tools and the "spaces" system page. The app does this at start; a check can do it by hand.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Register()
        {
            OSShell.RegisterSystem(WindowId, () => OSSpacesWindow.Open());
            AlbieTools.Register("spaces", new Dictionary<string, AlbieTools.Handler>
            {
                ["spaces_show"] = ShowTool,
                ["spaces_bring_here"] = BringTool,
                ["spaces_send_away"] = SendTool,
                ["spaces_close"] = CloseTool,
                ["spaces_close_all"] = CloseAllTool,
            });
        }

        // ---------------------------------------------------------------- what is open

        /// <summary>
        /// Fills <paramref name="into"/> (cleared first) with everything open around the user: the open mode, the OS windows,
        /// the open artifact and its room course, the truck or drone it drives, the transmitter, loose panels and Albie's
        /// card, then Albie. The Spaces window itself is left out. Allocates (closures): call it on a timer, not every frame.
        /// </summary>
        public static void Collect(List<SpaceItem> into)
        {
            if (into == null) return;
            into.Clear();
            OSShell shell = OSShell.Current;
            Workspace ws = shell != null && shell.workspace != null ? shell.workspace : Workspace.Current;
            ArtifactHost host = ArtifactHost.Current;
            string artifactId = OSArtifacts.OpenId();
            PruneTray(ws != null ? ws.ActiveMode : null, artifactId);

            // The open Workspace mode (CAD, Train, Code, Messages): one row for all its panels and models.
            if (ws != null && !string.IsNullOrEmpty(ws.ActiveMode))
            {
                string mode = ws.ActiveMode;
                string key = "mode:" + mode;
                var item = new SpaceItem { key = key, kind = SpaceKind.Mode, title = OSModes.Title(mode), icon = ModeIcon(mode) };
                item.away = InTray(key);
                item.hasPlace = ModePlace(ws, out item.place);
                item.bring = () => { RestoreTray(key); ws.Reanchor(); };
                item.send = () => SendModeAway(ws, key, item.title, item.icon);
                item.restore = () => RestoreTray(key);
                item.close = () => ws.Close();
                into.Add(item);
            }

            // The OS windows (Settings, Agents, the notebook, a project, the gesture display ...). An artifact's own panels and
            // volumes are under its artifact row below; the groups (the mode, the artifact as a whole) have rows of their own.
            if (shell != null)
            {
                OSWindows windows = shell.Windows;
                var all = windows.All;
                for (int i = 0; i < all.Count; i++)
                {
                    OSWindow w = all[i];
                    if (w == null || w.IsGroup || w.Root == null || w.Id == WindowId) continue;
                    if (w.Id.StartsWith("artifact:", StringComparison.Ordinal)) continue;
                    OSWindow window = w;
                    var item = new SpaceItem { key = "window:" + w.Id, kind = SpaceKind.Window, title = string.IsNullOrEmpty(w.Title) ? w.Id : w.Title, icon = WindowIcon(w.Id) };
                    item.away = !w.gameObject.activeSelf;
                    item.hasPlace = true;
                    item.place = CenterOf(w.Root);
                    item.bring = () => windows.BringOne(window);
                    item.send = () => SendWindowAway(window);
                    item.restore = () => windows.Restore(window);
                    item.close = window.Close;
                    into.Add(item);
                }
            }

            // The open artifact (or the one waiting on its Allow sheet), its room course, and the Thing it drives.
            if (host != null && artifactId != null)
            {
                string title = string.IsNullOrEmpty(host.OpenTitle) ? artifactId : host.OpenTitle;
                string question = AtRisk() ? "Close " + title + "? The run going on now stops and isn't kept." : null;
                var item = new SpaceItem { key = "artifact", kind = SpaceKind.Artifact, title = title, icon = "artifact", closeQuestion = question };
                item.away = InTray("artifact");
                item.hasPlace = ArtifactPlace(host, out item.place);
                item.bring = () => BringArtifact();
                item.send = () => SendArtifactAway(host, title);
                item.restore = () => RestoreTray("artifact");
                item.close = () => OSArtifacts.CloseArtifact();
                into.Add(item);

                if (host.HasRoomCourse && host.ContentRoot != null && !host.ContentRoot.IsChildOf(host.transform))
                {
                    var course = new SpaceItem { key = "course", kind = SpaceKind.Course, title = title + " course", icon = "slalom", closeQuestion = question };
                    course.hasPlace = true;
                    course.place = host.SceneOrigin.position;
                    course.close = () => OSArtifacts.CloseArtifact();  // the course is the artifact: closing it takes both down
                    into.Add(course);
                }
            }
            AddThings(into, host, artifactId);

            // The transmitter, out in the room or tucked in the tray.
            RcTransmitter transmitter = RcTransmitter.Instance;
            bool transmitterAway = InTray("transmitter");
            if (transmitter != null && (transmitter.Shown || transmitterAway))
            {
                var item = new SpaceItem { key = "transmitter", kind = SpaceKind.Transmitter, title = "RC transmitter", icon = "drive" };
                item.away = !transmitter.Shown;
                item.hasPlace = transmitter.Shown;
                item.place = transmitter.transform.position;
                item.bring = () => { ForgetTray("transmitter"); transmitter.Show(); };
                item.send = () => SendTransmitterAway(transmitter);
                item.restore = () => { ForgetTray("transmitter"); transmitter.Show(); };
                item.close = () => { ForgetTray("transmitter"); transmitter.Hide(); };
                item.closeLabel = "Put away";
                into.Add(item);
            }

            AddLoosePanels(into, ws, host);

            // Albie himself: Here calls him over (what the whistle did), Away sends him off to idle. He is never closed.
            AlbieController albie = AlbieCrew.Instance != null ? AlbieCrew.Instance.Lead : null;
            if (albie == null) albie = UnityEngine.Object.FindAnyObjectByType<AlbieController>();
            if (albie != null && albie.isActiveAndEnabled)
            {
                AlbieController a = albie;
                var item = new SpaceItem { key = "albie", kind = SpaceKind.Albie, title = "Albie", icon = "agents" };
                item.hasPlace = true;
                item.place = a.transform.position;
                item.bring = () => CallAlbie(a);
                item.send = () => { if (a != null) a.WanderAway(); };
                into.Add(item);
            }
        }

        static void AddThings(List<SpaceItem> into, ArtifactHost host, string artifactId)
        {
            // A Thing belongs to the open course: putting it away is closing that course (it parks and hides itself then).
            Action putAway = artifactId != null ? (Action)(() => OSArtifacts.CloseArtifact()) : null;
            string question = AtRisk() ? "Put it away? Its course closes, and the run going on now stops and isn't kept." : null;
            foreach (SlalomBrain brain in UnityEngine.Object.FindObjectsByType<SlalomBrain>())
            {
                if (brain == null || brain.Hidden || !brain.isActiveAndEnabled) continue;
                into.Add(new SpaceItem
                {
                    key = "thing:truck:" + brain.GetEntityId(), kind = SpaceKind.Thing, title = "RC truck", icon = "truck",
                    hasPlace = true, place = brain.transform.position, close = putAway, closeLabel = "Put away", closeQuestion = question,
                });
            }
            ArtifactDrone drone = ArtifactDrone.Live;
            if (drone != null)
            {
                into.Add(new SpaceItem
                {
                    key = "thing:drone", kind = SpaceKind.Thing, title = "Drone", icon = "quadcopter",
                    hasPlace = true, place = drone.transform.position, close = putAway, closeLabel = "Put away", closeQuestion = question,
                });
            }
        }

        // Panels nobody tracks: not an OS window, not part of the open mode, not under the artifact host. Albie's card is one.
        static void AddLoosePanels(List<SpaceItem> into, Workspace ws, ArtifactHost host)
        {
            panelScratch.Clear();
            panelScratch.AddRange(ForgePanel.All);
            for (int i = 0; i < panelScratch.Count; i++)
            {
                ForgePanel p = panelScratch[i];
                if (!IsLoose(p, ws, host)) continue;
                string key = "panel:" + p.GetEntityId();
                if (InTray(key)) continue;  // listed from the tray below
                into.Add(LooseRow(p, key, false));
            }
            panelScratch.Clear();
            for (int i = 0; i < tray.Count; i++)
            {
                Tray t = tray[i];
                if (!t.key.StartsWith("panel:", StringComparison.Ordinal) || !t.Still()) continue;
                ForgePanel p = t.dots.Count > 0 && t.dots[0] != null ? t.dots[0].GetComponent<ForgePanel>() : t.hidden.Count > 0 && t.hidden[0] != null ? t.hidden[0].GetComponent<ForgePanel>() : null;
                if (p != null) into.Add(LooseRow(p, t.key, true));
            }
        }

        static bool IsLoose(ForgePanel p, Workspace ws, ArtifactHost host)
        {
            if (p == null || !p.gameObject.activeInHierarchy) return false;
            if (p.GetComponent<OSWindow>() != null) return false;
            if (ws != null && ws.IsOpenItem(p.gameObject)) return false;
            if (host != null && p.transform.IsChildOf(host.transform)) return false;
            return true;
        }

        static SpaceItem LooseRow(ForgePanel p, string key, bool away)
        {
            bool card = p.name.IndexOf("AlbieCard", StringComparison.Ordinal) >= 0;
            string title = card ? "Albie's card" : (string.IsNullOrEmpty(p.Title) ? p.name : p.Title);
            var item = new SpaceItem { key = key, kind = card ? SpaceKind.Card : SpaceKind.Panel, title = title, icon = card ? "agents" : "apps", away = away };
            item.hasPlace = true;
            item.place = CenterOf(p.transform);
            item.bring = () =>
            {
                RestoreTray(key);
                OSShell shell = OSShell.Current;
                if (shell != null && p != null && p.gameObject.activeInHierarchy) GestureWindow.Of(p).ToggleNear(shell, OSShell.FindHead());
            };
            item.send = () => SendPanelAway(p, key, title, item.icon, item.kind);
            item.restore = () => RestoreTray(key);
            item.close = () => HidePanel(p, key);
            return item;
        }

        // ---------------------------------------------------------------- where it is

        /// <summary>Where a thing is, said plainly from the user's eyes: "within reach, ahead", "2.4 m, behind you", "in the tray".</summary>
        public static string Where(SpaceItem item, Transform head)
        {
            if (item == null) return "";
            if (item.away) return "in the tray";
            if (!item.hasPlace || head == null) return "";
            Vector3 offset = item.place - head.position;
            float distance = offset.magnitude;
            string far = distance < 0.7f ? "within reach" : distance < 1.5f ? "near" : distance.ToString("0.0") + " m";
            return far + ", " + Direction(offset, head);
        }

        static string Direction(Vector3 offset, Transform head)
        {
            Vector3 flat = offset;
            flat.y = 0f;
            Vector3 facing = head.forward;
            facing.y = 0f;
            if (flat.sqrMagnitude < 0.01f || facing.sqrMagnitude < 1e-4f) return offset.y < 0f ? "below you" : "above you";
            float angle = Vector3.SignedAngle(facing, flat, Vector3.up);
            float abs = Mathf.Abs(angle);
            if (abs < 35f) return "ahead";
            if (abs > 120f) return "behind you";
            return angle > 0f ? "to your right" : "to your left";
        }

        // ---------------------------------------------------------------- everything at once

        /// <summary>Something going on now would be lost by closing: a scored run, or the truck recording, training or driving itself.</summary>
        public static bool AtRisk()
        {
            ArtifactHost host = ArtifactHost.Current;
            if (host != null && host.Run != null && host.Run.State == ArtifactRunState.Running) return true;
            foreach (SlalomBrain brain in UnityEngine.Object.FindObjectsByType<SlalomBrain>())
                if (brain != null && !brain.Hidden && brain.State != SlalomBrain.Mode.Idle) return true;
            return false;
        }

        /// <summary>Everything that was sent away comes out of the tray, then every window and the open mode glide in front of the user.</summary>
        public static void BringEverything()
        {
            OSShell shell = OSShell.Current;
            Collect(itemScratch);
            bool restored = false;
            for (int i = 0; i < itemScratch.Count; i++)
            {
                SpaceItem item = itemScratch[i];
                if (!item.away || item.restore == null) continue;
                Run(item.restore);
                restored = true;
            }
            itemScratch.Clear();
            if (shell == null) return;
            if (restored && Application.isPlaying && shell.isActiveAndEnabled) shell.StartCoroutine(BringAfter(shell));
            else shell.Windows.BringHere();
        }

        static IEnumerator BringAfter(OSShell shell)
        {
            yield return new WaitForSecondsRealtime(BringAfterRestoreSeconds);
            if (shell != null) shell.Windows.BringHere();
        }

        /// <summary>Everything that can be is sent into the tray (Albie is left alone). Returns how many things went.</summary>
        public static int SendEverythingAway()
        {
            Collect(itemScratch);
            int sent = 0;
            for (int i = 0; i < itemScratch.Count; i++)
            {
                SpaceItem item = itemScratch[i];
                if (item.away || item.send == null || item.kind == SpaceKind.Albie) continue;
                Run(item.send);
                sent++;
            }
            itemScratch.Clear();
            return sent;
        }

        /// <summary>
        /// Closes everything: the transmitter is put away, the open artifact closes (its course comes down, its Thing is put
        /// away), every OS window and the open mode close, loose panels and Albie's card are hidden, the tray is emptied. The
        /// Spaces window stays when <paramref name="keepSpaces"/> (pressed from it). Does not ask: see <see cref="AtRisk"/>.
        /// </summary>
        public static void CloseEverything(bool keepSpaces = false)
        {
            OSShell shell = OSShell.Current;
            Workspace ws = shell != null && shell.workspace != null ? shell.workspace : Workspace.Current;
            ArtifactHost host = ArtifactHost.Current;

            // Whatever is in the tray as a dot is shown for a moment so the dot goes (a closing workspace or host may only hide
            // the panel, and its dot would be left floating), then hidden again for its owner to close.
            for (int i = 0; i < tray.Count; i++)
            {
                Tray t = tray[i];
                for (int d = 0; d < t.dots.Count; d++)
                    if (t.dots[d] != null && t.dots[d].IsMinimized) DropDot(t.dots[d].gameObject);
            }

            RcTransmitter transmitter = RcTransmitter.Instance;
            if (transmitter != null && transmitter.Shown) Run(transmitter.Hide);
            if (OSArtifacts.OpenId() != null) Run(() => OSArtifacts.CloseArtifact());

            // Loose panels and Albie's card.
            panelScratch.Clear();
            panelScratch.AddRange(ForgePanel.All);
            for (int i = 0; i < panelScratch.Count; i++)
                if (IsLoose(panelScratch[i], ws, host)) panelScratch[i].gameObject.SetActive(false);
            panelScratch.Clear();
            tray.Clear();

            if (shell != null) shell.Windows.CloseAllBut(keepSpaces ? WindowId : null);
            else if (ws != null && !string.IsNullOrEmpty(ws.ActiveMode)) ws.Close();
        }

        // ---------------------------------------------------------------- sending away and back

        static void SendWindowAway(OSWindow window)
        {
            if (window == null || window.Root == null || !window.gameObject.activeSelf) return;
            if (window.Panel != null)
            {
                OSShell shell = OSShell.Current;
                string said = shell != null ? GestureWindow.Of(window.Panel).Close(shell, OSShell.FindHead()) : "no shell";
                if (said == "Nowhere to minimize to" || said == "no shell") window.gameObject.SetActive(false);
            }
            else window.gameObject.SetActive(false);  // a volume window: hidden, and back from the list
        }

        static void SendModeAway(Workspace ws, string key, string title, string icon)
        {
            if (ws == null) return;
            Tray t = NewTray(key, title, icon, SpaceKind.Mode);
            t.owner = ws.ActiveMode;
            panelScratch.Clear();
            panelScratch.AddRange(ForgePanel.All);
            for (int i = 0; i < panelScratch.Count; i++)
            {
                ForgePanel p = panelScratch[i];
                if (p != null && p.gameObject.activeInHierarchy && ws.IsOpenItem(p.gameObject)) Minimise(p, t);
            }
            panelScratch.Clear();
            foreach (ForgeVolume v in UnityEngine.Object.FindObjectsByType<ForgeVolume>())
                if (v != null && v.gameObject.activeInHierarchy && ws.IsOpenItem(v.gameObject)) Hide(v.gameObject, t);
        }

        static void SendArtifactAway(ArtifactHost host, string title)
        {
            if (host == null) return;
            Tray t = NewTray("artifact", title, "artifact", SpaceKind.Artifact);
            t.owner = OSArtifacts.OpenId();
            panelScratch.Clear();
            host.GetComponentsInChildren(false, panelScratch);
            for (int i = 0; i < panelScratch.Count; i++) Minimise(panelScratch[i], t);
            panelScratch.Clear();
            foreach (ForgeVolume v in host.GetComponentsInChildren<ForgeVolume>(false)) Hide(v.gameObject, t);
        }

        static void SendPanelAway(ForgePanel p, string key, string title, string icon, SpaceKind kind)
        {
            if (p == null || !p.gameObject.activeInHierarchy) return;
            Minimise(p, NewTray(key, title, icon, kind));
        }

        static void SendTransmitterAway(RcTransmitter transmitter)
        {
            if (transmitter == null || !transmitter.Shown) return;
            Tray t = NewTray("transmitter", "RC transmitter", "drive", SpaceKind.Transmitter);
            t.custom = true;
            t.restore = () => { if (transmitter != null) transmitter.Show(); };
            transmitter.Hide();
        }

        // Into its dot where it is (the gesture minimise), or hidden when there is no dot to make.
        static void Minimise(ForgePanel p, Tray t)
        {
            if (p == null || !p.gameObject.activeInHierarchy) return;
            OSShell shell = OSShell.Current;
            GestureWindow gesture = GestureWindow.Of(p);
            string said = shell != null ? gesture.Close(shell, OSShell.FindHead()) : "no shell";
            if (said == null) t.dots.Add(gesture);
            else if (said != "Already closed") Hide(p.gameObject, t);
        }

        static void Hide(GameObject go, Tray t)
        {
            if (go == null || !go.activeSelf) return;
            go.SetActive(false);
            t.hidden.Add(go);
        }

        static void HidePanel(ForgePanel p, string key)
        {
            ForgetTray(key);
            if (p == null) return;
            if (!p.gameObject.activeSelf) DropDot(p.gameObject);
            p.gameObject.SetActive(false);
        }

        // A panel minimised into its dot: shown for a moment, which drops the dot (GestureWindow.OnEnable), then hidden.
        static void DropDot(GameObject panel)
        {
            if (panel == null) return;
            panel.SetActive(true);
            panel.SetActive(false);
        }

        static Tray NewTray(string key, string title, string icon, SpaceKind kind)
        {
            ForgetTray(key);
            var t = new Tray { key = key, title = title, icon = icon, kind = kind };
            tray.Add(t);
            return t;
        }

        static Tray FindTray(string key)
        {
            for (int i = 0; i < tray.Count; i++)
                if (tray[i].key == key) return tray[i];
            return null;
        }

        static bool InTray(string key)
        {
            Tray t = FindTray(key);
            return t != null && t.Still();
        }

        static void ForgetTray(string key)
        {
            for (int i = tray.Count - 1; i >= 0; i--)
                if (tray[i].key == key) tray.RemoveAt(i);
        }

        /// <summary>Brings what was put away under this key back where it was. False when nothing was.</summary>
        static bool RestoreTray(string key)
        {
            Tray t = FindTray(key);
            if (t == null) return false;
            ForgetTray(key);
            OSShell shell = OSShell.Current;
            for (int i = 0; i < t.dots.Count; i++)
            {
                GestureWindow dot = t.dots[i];
                if (dot == null || !dot.IsMinimized) continue;
                if (shell != null) dot.Restore(shell);
                else dot.gameObject.SetActive(true);
            }
            for (int i = 0; i < t.hidden.Count; i++)
                if (t.hidden[i] != null) t.hidden[i].SetActive(true);
            if (t.restore != null) Run(t.restore);
            return true;
        }

        // Records for things that are back (a dot tapped) or gone (closed) are dropped, and so are those of a mode or an
        // artifact that is no longer the open one (the workspace or the host has its objects now).
        static void PruneTray(string activeMode, string artifactId)
        {
            for (int i = tray.Count - 1; i >= 0; i--)
            {
                Tray t = tray[i];
                bool stale = (t.kind == SpaceKind.Mode && t.owner != activeMode) || (t.kind == SpaceKind.Artifact && t.owner != artifactId);
                if (stale || !t.Still()) tray.RemoveAt(i);
            }
        }

        static void BringArtifact()
        {
            RestoreTray("artifact");
            OSShell shell = OSShell.Current;
            if (shell == null) return;
            var all = shell.Windows.All;
            for (int i = 0; i < all.Count; i++)
            {
                OSWindow w = all[i];
                if (w != null && !w.IsGroup && w.Id.StartsWith("artifact:", StringComparison.Ordinal)) shell.Windows.BringOne(w);
            }
        }

        // What the whistle did: summon, then listen hands-free. With no whistle component in the scene Albie just walks over.
        static void CallAlbie(AlbieController albie)
        {
            if (WhistleButton.CallAlbie()) return;
            if (albie == null) return;
            albie.ReleaseStation();
            if (albie.Perched) albie.LeavePerch(albie.ComeToUser);
            else albie.ComeToUser();
        }

        static void Run(Action action)
        {
            try { action?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        // ---------------------------------------------------------------- places and icons

        static Vector3 CenterOf(Transform t) =>
            t is RectTransform rect ? rect.TransformPoint(rect.rect.center) : t.position;

        static readonly List<Workspace.ItemSlot> slots = new List<Workspace.ItemSlot>();

        static bool ModePlace(Workspace ws, out Vector3 place)
        {
            place = default;
            ws.GetSlots(slots);
            if (slots.Count == 0) return false;
            for (int i = 0; i < slots.Count; i++) place += slots[i].center;
            place /= slots.Count;
            slots.Clear();
            return true;
        }

        static bool ArtifactPlace(ArtifactHost host, out Vector3 place)
        {
            ForgeVolume volume = host.GetComponentInChildren<ForgeVolume>(false);
            if (volume != null)
            {
                place = volume.transform.position;
                return true;
            }
            ForgePanel panel = host.GetComponentInChildren<ForgePanel>(false);
            if (panel != null)
            {
                place = CenterOf(panel.transform);
                return true;
            }
            place = host.transform.position;
            return true;
        }

        static string ModeIcon(string mode)
        {
            switch (mode)
            {
                case "cad": return "cube";
                case "ml": return "train";
                case "code": return "code";
                case "messages": return "messages";
                default: return "apps";
            }
        }

        static string WindowIcon(string id)
        {
            switch (id)
            {
                case "settings": return "settings";
                case "agents": return "agents";
                case "notebook": return "notebook";
                case "gestures": return "gestures";
                case "drive": return "drive";
                case "build": return "build";
                default: return id != null && id.StartsWith("project", StringComparison.Ordinal) ? "projects" : "apps";
            }
        }

        // ---------------------------------------------------------------- voice tools

        static OSShell Shell()
        {
            var shell = OSShell.Current;
            if (shell == null) throw new ArgumentException("The Albie menu isn't available in this scene.");
            return shell;
        }

        static JArray Describe(List<SpaceItem> items)
        {
            Transform head = OSShell.FindHead();
            var list = new JArray();
            for (int i = 0; i < items.Count; i++)
            {
                SpaceItem item = items[i];
                list.Add(new JObject { ["name"] = item.title, ["kind"] = item.kind.ToString().ToLowerInvariant(), ["where"] = Where(item, head), ["away"] = item.away });
            }
            return list;
        }

        // The items whose title matches what was said (case-insensitive, either way round: "the truck" finds "RC truck").
        static void Matching(string name, List<SpaceItem> items, List<SpaceItem> into)
        {
            into.Clear();
            string want = (name ?? "").Trim().ToLowerInvariant();
            if (want.StartsWith("the ", StringComparison.Ordinal)) want = want.Substring(4);
            for (int i = 0; i < items.Count; i++)
            {
                string title = (items[i].title ?? "").ToLowerInvariant();
                if (title == want || title.Contains(want) || (title.Length > 2 && want.Contains(title))) into.Add(items[i]);
            }
        }

        static JObject ShowTool(JObject args)
        {
            Shell();
            OSSpacesWindow.Open();
            var items = new List<SpaceItem>();
            Collect(items);
            return new JObject { ["ok"] = true, ["open"] = Describe(items), ["count"] = items.Count };
        }

        static JObject BringTool(JObject args)
        {
            Shell();
            string name = (string)args?["name"];
            if (string.IsNullOrWhiteSpace(name))
            {
                BringEverything();
                return new JObject { ["ok"] = true, ["brought"] = "everything" };
            }
            var items = new List<SpaceItem>();
            var hits = new List<SpaceItem>();
            Collect(items);
            Matching(name, items, hits);
            int done = 0;
            for (int i = 0; i < hits.Count; i++)
                if (hits[i].bring != null) { Run(hits[i].bring); done++; }
            if (done == 0) return AlbieTools.Error("Nothing called '" + name + "' can be brought here. Open now: " + Describe(items).ToString(Newtonsoft.Json.Formatting.None));
            return new JObject { ["ok"] = true, ["brought"] = done };
        }

        static JObject SendTool(JObject args)
        {
            Shell();
            string name = (string)args?["name"];
            if (string.IsNullOrWhiteSpace(name)) return new JObject { ["ok"] = true, ["sent"] = SendEverythingAway() };
            var items = new List<SpaceItem>();
            var hits = new List<SpaceItem>();
            Collect(items);
            Matching(name, items, hits);
            int done = 0;
            for (int i = 0; i < hits.Count; i++)
                if (hits[i].send != null && !hits[i].away) { Run(hits[i].send); done++; }
            if (done == 0) return AlbieTools.Error("Nothing called '" + name + "' can be sent away. Open now: " + Describe(items).ToString(Newtonsoft.Json.Formatting.None));
            return new JObject { ["ok"] = true, ["sent"] = done };
        }

        static JObject CloseTool(JObject args)
        {
            Shell();
            string name = (string)args?["name"];
            if (string.IsNullOrWhiteSpace(name)) return AlbieTools.Error("Say which one to close, or use spaces_close_all.");
            var items = new List<SpaceItem>();
            var hits = new List<SpaceItem>();
            Collect(items);
            Matching(name, items, hits);
            for (int i = 0; i < hits.Count; i++)
            {
                SpaceItem item = hits[i];
                if (item.close == null) continue;
                if (item.closeQuestion != null)
                {
                    OSSpacesWindow.AskToClose(item);
                    return new JObject { ["ok"] = true, ["asked"] = true, ["say"] = "Tap Close on the Spaces list to confirm: " + item.closeQuestion };
                }
                Run(item.close);
                return new JObject { ["ok"] = true, ["closed"] = item.title };
            }
            return AlbieTools.Error("Nothing called '" + name + "' can be closed. Open now: " + Describe(items).ToString(Newtonsoft.Json.Formatting.None));
        }

        static JObject CloseAllTool(JObject args) => CloseAllByVoice();

        /// <summary>
        /// "Close everything" by voice (spaces_close_all, os_windows close_all): closes everything now, unless something going
        /// on would be lost (<see cref="AtRisk"/>): then the Spaces list opens with the question on it and nothing closes until
        /// the user taps.
        /// </summary>
        public static JObject CloseAllByVoice()
        {
            OSShell shell = Shell();
            if (AtRisk())
            {
                OSSpacesWindow.AskToCloseEverything();
                return new JObject { ["ok"] = true, ["asked"] = true, ["say"] = "A run is going on. Tap Close everything on the Spaces list to stop it and close everything." };
            }
            CloseEverything();
            return new JObject { ["ok"] = true, ["windows"] = shell.Windows.Count };
        }
    }
}
