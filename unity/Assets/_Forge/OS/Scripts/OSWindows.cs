using System;
using System.Collections;
using System.Collections.Generic;
using Oculus.Interaction;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// The window manager. A window is a free ForgePanel at the scene root (never under the desk frame, so it doesn't
    /// fold when the user leaves the desk) with a PanelManipulator, so the move, turn and resize handles and the hand
    /// gestures reach it, and a close button in its header. Opening an id that is already open brings that window forward
    /// instead of making another. <see cref="Adopt"/> tracks something that already exists (the gesture display, an
    /// artifact's panels, the open Workspace mode as one group), so <see cref="BringHere"/> and <see cref="CloseAll"/>
    /// reach it too.
    ///
    /// Placement is in angles seen from the user's eyes: a window goes to the nearest free place in an arc about 0.8 m
    /// from the eyes at a desk (0.95 m away from one), a little below eye level, facing the user, clear of every other
    /// panel and volume in the scene (the open mode's items are taken from where the workspace lays them out), and out of
    /// the room's walls when the room is known. When a row is full the next one goes above it, then further out. Nothing
    /// moves by itself when the user walks to or from a desk; <see cref="BringHere"/> is what moves things.
    /// </summary>
    public class OSWindows : MonoBehaviour
    {
        const float OpenSeconds = 0.2f, CloseSeconds = 0.18f, BringSeconds = 0.55f, BringStagger = 0.05f, StartScale = 0.92f;
        // Where windows go: this far from the eyes (m), this far below them, the second tier this far above them.
        const float DeskDistance = 0.8f, AwayDistance = 0.95f, DropMeters = 0.05f, RaiseMeters = 0.42f, RingStep = 0.25f, DeskClear = 0.03f;
        // Search in yaw steps (degrees) out to MaxYaw, with this gap (degrees seen from the eyes) between neighbours.
        const float YawStep = 3f, MaxYaw = 110f, GapDegrees = 2.5f;
        const int Tiers = 4;
        const int MaxAttempts = 40;
        const float OuterRingCost = 15f;  // degrees: a place on the further ring counts as this much further from straight ahead
        const float FallbackWidth = 0.5f, FallbackHeight = 0.4f;
        const float CloseMargin = 14f;  // mm from the panel's right edge

        readonly List<OSWindow> windows = new List<OSWindow>();
        readonly Dictionary<string, OSWindow> byId = new Dictionary<string, OSWindow>(StringComparer.Ordinal);
        readonly List<OSWindow> scratch = new List<OSWindow>();
        readonly List<OSWindow> modeScratch = new List<OSWindow>();
        readonly List<ForgePanel> pending = new List<ForgePanel>();     // windows not yet placed in this Bring here: they don't block
        readonly List<OSWindow> closing = new List<OSWindow>();
        readonly List<Workspace.ItemSlot> slots = new List<Workspace.ItemSlot>();
        Transform arc;
        Workspace workspace;

        public event Action Changed;

        public IReadOnlyList<OSWindow> All => windows;
        public int Count => windows.Count;

        void OnEnable()
        {
            var shell = GetComponent<OSShell>();
            workspace = shell != null && shell.workspace != null ? shell.workspace : Workspace.Current;
            if (workspace == null) return;
            workspace.modeChanged.RemoveListener(OnModeChanged);
            workspace.modeChanged.AddListener(OnModeChanged);
            OnModeChanged(workspace.ActiveMode);
        }

        void OnDisable()
        {
            if (workspace != null) workspace.modeChanged.RemoveListener(OnModeChanged);
        }

        void OnDestroy()
        {
            for (int i = 0; i < closing.Count; i++)
                if (closing[i] != null) Destroy(closing[i].gameObject);
            closing.Clear();
        }

        // ---------------------------------------------------------------- the open Workspace mode, as one group entry

        /// <summary>The window id the open Workspace mode is tracked under.</summary>
        public static string GroupId(string modeId) => "mode:" + modeId;

        // Whoever opens or closes a mode (the menu, a voice tool, the pet, another module), the list follows the workspace.
        void OnModeChanged(string modeId)
        {
            string want = string.IsNullOrEmpty(modeId) ? null : GroupId(modeId);
            modeScratch.Clear();
            modeScratch.AddRange(windows);
            for (int i = 0; i < modeScratch.Count; i++)
            {
                OSWindow w = modeScratch[i];
                if (w != null && w.IsGroup && w.Id.StartsWith("mode:", StringComparison.Ordinal) && w.Id != want) w.Detach();
            }
            modeScratch.Clear();
            if (want == null) return;
            if (!byId.ContainsKey(want))
            {
                Workspace ws = workspace;
                Adopt(want, OSModes.Title(modeId), null, () => { if (ws != null) ws.Close(); });
            }
            ClearModeSlots();
        }

        // A mode just opened where windows already are: the windows its panels would cover move aside, to the nearest free
        // place. The rest stay exactly where they are.
        void ClearModeSlots()
        {
            Transform head = OSShell.FindHead();
            if (head == null || workspace == null || workspace.ActiveMode == null) return;
            workspace.GetSlots(slots);
            if (slots.Count == 0) return;
            Vector3 eye = head.position;
            Vector3 forward = FlatForward(head);
            modeScratch.Clear();
            for (int i = 0; i < windows.Count; i++)
            {
                OSWindow w = windows[i];
                if (w == null || w.IsGroup || w.Panel == null || !w.gameObject.activeInHierarchy) continue;
                Cover mine = CoverOf(w.Motion != null ? w.TargetCenter : CenterOf(w), WidthOf(w), HeightOf(w), eye, forward);
                for (int s = 0; s < slots.Count; s++)
                {
                    if (!Overlap(mine, CoverOf(slots[s].center, slots[s].size.x, slots[s].size.y, eye, forward))) continue;
                    modeScratch.Add(w);
                    break;
                }
            }
            pending.Clear();
            for (int i = 0; i < modeScratch.Count; i++) pending.Add(modeScratch[i].Panel);
            for (int i = 0; i < modeScratch.Count; i++)
            {
                OSWindow w = modeScratch[i];
                pending.Remove(w.Panel);
                if (FindSpot(w, head, out Vector3 center, out Quaternion rotation)) Glide(w, center, rotation, i * BringStagger);
            }
            pending.Clear();
            modeScratch.Clear();
        }

        // ---------------------------------------------------------------- open, adopt, close

        /// <summary>
        /// A new free window (a ForgePanel root object with a PanelManipulator and a close button), or the existing one with
        /// this id brought in front of the user. <paramref name="build"/> fills the panel's Content the first time. Size in
        /// metres. Null if this scene has no panel prefab.
        /// </summary>
        public OSWindow Open(string id, string title, Vector2 sizeMeters, Action<ForgePanel> build, string badge = null, Color? accent = null)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (byId.TryGetValue(id, out OSWindow existing) && existing != null)
            {
                BringForward(existing);
                return existing;
            }

            ForgePanel prefab = PanelPrefab();
            if (prefab == null)
            {
                Debug.LogWarning("[OS] There is no panel prefab in this scene, so no window '" + id + "'.");
                return null;
            }

            ForgePanel panel = Instantiate(prefab);
            panel.name = "OSWindow " + id;
            panel.panelId = id;
            panel.Title = title ?? "";
            panel.SetBadge(badge, accent ?? ForgeTheme.Tool);  // a null badge hides the prefab's sample one
            panel.SizeMeters = sizeMeters;
            if (panel.GetComponent<GraphicRaycaster>() == null) panel.gameObject.AddComponent<GraphicRaycaster>();
            if (panel.Content != null)
            {
                Transform placeholder = panel.Content.Find("Placeholder");
                if (placeholder != null) placeholder.gameObject.SetActive(false);
            }
            CanvasGroup group = panel.GetComponent<CanvasGroup>();
            if (group == null) group = panel.gameObject.AddComponent<CanvasGroup>();
            if (Application.isPlaying) group.alpha = 0f;  // no flash before the first frame of the open animation

            Transform head = OSShell.FindHead();
            var handles = panel.GetComponent<PanelManipulator>();
            if (handles == null) handles = panel.gameObject.AddComponent<PanelManipulator>();
            handles.Init(head, DeskFrame(head));

            var gesture = GestureWindow.Of(panel);
            if (gesture != null) gesture.StaysAway = true;  // its minimise dot stays when the user leaves the desk

            var window = panel.gameObject.AddComponent<OSWindow>();
            window.Init(this, id, title, panel, null, false, true, null);
            Track(window);
            AddCloseButton(window);
            PlaceNew(window);

            try
            {
                build?.Invoke(panel);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            AnimateIn(window);
            OSSounds.Open();
            return window;
        }

        /// <summary>
        /// Track something that already exists (the gesture display, an artifact's panels, a Workspace mode) so
        /// <see cref="BringHere"/> and <see cref="CloseAll"/> reach it. A null <paramref name="root"/> tracks a group (a
        /// Workspace mode) that only has a close action. <paramref name="close"/> does the real closing.
        /// </summary>
        public OSWindow Adopt(string id, string title, GameObject root, Action close)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (byId.TryGetValue(id, out OSWindow existing) && existing != null) return existing;

            bool group = root == null;
            if (group) root = new GameObject("OSWindow " + id + " (group)");
            if (group) root.transform.SetParent(transform, false);
            var already = root.GetComponent<OSWindow>();
            if (already != null) return already;

            var window = root.AddComponent<OSWindow>();
            window.Init(this, id, title, root.GetComponent<ForgePanel>(), root.GetComponent<ForgeVolume>(), group, false, close);
            if (window.Panel != null)
            {
                var gesture = window.Panel.GetComponent<GestureWindow>();
                if (gesture != null) gesture.StaysAway = true;
            }
            Track(window);
            return window;
        }

        public OSWindow Find(string id) =>
            !string.IsNullOrEmpty(id) && byId.TryGetValue(id, out OSWindow w) && w != null ? w : null;

        public bool IsOpen(string id) => Find(id) != null;

        public void Close(string id) => Find(id)?.Close();

        /// <summary>Closes every window: opened ones go, adopted ones run their close action, and so does the open Workspace mode.</summary>
        public void CloseAll()
        {
            scratch.Clear();
            scratch.AddRange(windows);
            for (int i = scratch.Count - 1; i >= 0; i--)
                if (scratch[i] != null) scratch[i].Close();
            scratch.Clear();
        }

        internal void Forget(OSWindow window)
        {
            if (!windows.Remove(window)) return;
            if (byId.TryGetValue(window.Id, out OSWindow mapped) && mapped == window) byId.Remove(window.Id);
            Changed?.Invoke();
        }

        void Track(OSWindow window)
        {
            windows.Add(window);
            byId[window.Id] = window;
            Changed?.Invoke();
        }

        ForgePanel PanelPrefab()
        {
            Workspace ws = Workspace.Current;
            if (ws == null && OSShell.Current != null) ws = OSShell.Current.workspace;
            return ws != null ? ws.panelPrefab : null;
        }

        static Transform DeskFrame(Transform head)
        {
            Workspace ws = Workspace.Current;
            return ws != null && ws.presence != null && ws.presence.Frame != null ? ws.presence.Frame : head;
        }

        // An existing window comes back: out of its minimised dot if it was put away, then in front of the user.
        void BringForward(OSWindow window)
        {
            if (window.IsGroup || window.Root == null) return;
            if (!window.gameObject.activeSelf)
            {
                var gesture = window.Panel != null ? window.Panel.GetComponent<GestureWindow>() : null;
                if (gesture != null && gesture.IsMinimized) gesture.Restore(this);
                else window.gameObject.SetActive(true);
            }
            if (!IsInFront(window)) PlaceNew(window);
        }

        // Already in view, a sensible distance away: leave it where the user has it.
        static bool IsInFront(OSWindow window)
        {
            Transform head = OSShell.FindHead();
            if (head == null || window.Root == null) return false;
            Vector3 offset = CenterOf(window) - head.position;
            float distance = offset.magnitude;
            return distance > 0.4f && distance < 1.6f && Vector3.Angle(head.forward, offset) < 40f;
        }

        // ---------------------------------------------------------------- the header's close button

        // A UI Set icon button at the right end of the header, in OS units (the host is scaled to them), 48 units (31 mm).
        // The panel's own Face me and Straight pills move left to make room.
        void AddCloseButton(OSWindow window)
        {
            ForgePanel panel = window.Panel;
            OSKit kit = OSKit.Current;
            if (panel == null || kit == null) return;

            const float units = 48f;
            float scale = kit.unitMeters / ForgePanel.MetersPerUnit;  // OS units to the panel's millimetres
            var host = new GameObject("OSClose", typeof(RectTransform));
            var rect = (RectTransform)host.transform;
            rect.SetParent(panel.transform, false);
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.sizeDelta = new Vector2(units, units);
            rect.localScale = Vector3.one * scale;
            rect.anchoredPosition = new Vector2(-CloseMargin, -ForgeTheme.PanelHeader * 0.5f);
            OSBody.EnsureCanvas(panel.GetComponent<Canvas>());
            UIThemeManager manager = null;
            if (kit.theme != null)
            {
                manager = host.AddComponent<UIThemeManager>();
                OSBody.SetThemes(manager, kit.theme);
            }

            Button button = OSUi.Button(host.transform, null, window.Close, OSButtonStyle.Borderless, "close");
            if (button == null)
            {
                Destroy(host);
                return;
            }
            var buttonRect = (RectTransform)button.transform;
            buttonRect.anchorMin = buttonRect.anchorMax = buttonRect.pivot = new Vector2(0.5f, 0.5f);
            buttonRect.anchoredPosition = Vector2.zero;
            OSUi.Size(buttonRect, units, units);
            var layout = button.GetComponent<LayoutElement>();
            if (layout != null) layout.ignoreLayout = true;
            if (manager != null) manager.ApplyCurrentTheme();

            // Make room: the pills sit at the right end of the header and fade in when the panel is pointed at.
            float shift = units * scale + 10f;
            Transform controls = panel.transform.Find("Manipulation");
            if (controls != null)
            {
                foreach (string pill in new[] { "Face me", "Straight" })
                {
                    var t = controls.Find(pill) as RectTransform;
                    if (t != null) t.anchoredPosition += new Vector2(-shift, 0f);
                }
            }
            // The title keeps clear of the button as well.
            foreach (TMP_Text text in panel.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text.name != "Title") continue;
                var title = text.rectTransform;
                title.offsetMax = new Vector2(title.offsetMax.x - shift, title.offsetMax.y);
                break;
            }
        }

        // ---------------------------------------------------------------- open and close motion

        // Scale and fade in, about 200 ms. Grabs wait until it has landed.
        void AnimateIn(OSWindow window)
        {
            ForgePanel panel = window.Panel;
            if (panel == null) return;
            var group = panel.GetComponent<CanvasGroup>();
            var handles = panel.Handles;
            if (!Application.isPlaying || !gameObject.activeInHierarchy)
            {
                if (group != null) group.alpha = 1f;
                return;
            }
            Transform t = panel.transform;
            Vector3 rest = t.localScale;
            if (handles != null) handles.Busy = true;
            StopMotion(window);
            window.Opening = true;
            window.OpenRest = rest;
            window.Motion = StartCoroutine(ForgeTween.Run(OpenSeconds, 0f, k =>
            {
                if (t == null) return;
                float e = ForgeTween.EaseOutCubic(k);
                t.localScale = rest * Mathf.Lerp(StartScale, 1f, e);
                if (group != null) group.alpha = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 0.8f, k));
            }, () =>
            {
                window.Motion = null;
                window.Opening = false;
                if (t == null) return;
                t.localScale = rest;
                if (group != null) group.alpha = 1f;
                if (handles != null) handles.Busy = false;
            }));
        }

        // Scale and fade out, then gone. A window that is hidden (minimised) or an edit-mode call just goes.
        internal void Dismiss(OSWindow window)
        {
            StopMotion(window);
            GameObject go = window.gameObject;
            if (!Application.isPlaying || !gameObject.activeInHierarchy || !go.activeInHierarchy || window.Panel == null)
            {
                go.SetActive(false);
                Destroy(go);
                return;
            }
            ForgePanel panel = window.Panel;
            var group = panel.GetComponent<CanvasGroup>();
            var handles = panel.Handles;
            if (handles != null)
            {
                handles.Cancel();
                handles.Busy = true;
            }
            if (group != null) group.blocksRaycasts = false;  // on its way out: clicks go past it
            Transform t = panel.transform;
            Vector3 rest = t.localScale;
            float a0 = group != null ? group.alpha : 1f;
            closing.Add(window);
            OSSounds.Close();
            window.Motion = StartCoroutine(ForgeTween.Run(CloseSeconds, 0f, k =>
            {
                if (t == null) return;
                float e = ForgeTween.EaseInCubic(k);
                t.localScale = rest * Mathf.Lerp(1f, StartScale, e);
                if (group != null) group.alpha = a0 * (1f - Mathf.SmoothStep(0f, 1f, k));
            }, () =>
            {
                closing.Remove(window);
                if (go != null) Destroy(go);
            }));
        }

        void StopMotion(OSWindow window)
        {
            if (window.Motion != null) StopCoroutine(window.Motion);
            window.Motion = null;
            window.Opening = false;
        }

        // Lets a window that is still scaling and fading in land fully (scale 1, alpha 1) before something else moves it.
        void FinishOpen(OSWindow window)
        {
            if (!window.Opening) return;
            ForgePanel panel = window.Panel;
            StopMotion(window);
            if (panel == null) return;
            panel.transform.localScale = window.OpenRest;
            var group = panel.GetComponent<CanvasGroup>();
            if (group != null) group.alpha = 1f;
        }

        // ---------------------------------------------------------------- placement

        /// <summary>
        /// Puts one new window at the nearest free place in front of the user: straight ahead if nothing is there, else
        /// alternately to the right and left, then the next row, so a new window never lands on one that is already open.
        /// </summary>
        void PlaceNew(OSWindow window)
        {
            Transform head = OSShell.FindHead();
            if (head == null || window == null || window.Root == null) return;
            pending.Clear();
            if (FindSpot(window, head, out Vector3 center, out Quaternion rotation)) Put(window, center, rotation);
        }

        /// <summary>
        /// Springs every window back around the user, and the open Workspace mode with them: the mode is laid out again
        /// around the user first, then the windows take the nearest free places beside it, one by one in the order they were
        /// opened, not overlapping. Each glides there.
        /// </summary>
        public void BringHere()
        {
            Transform head = OSShell.FindHead();
            if (head == null) return;
            if (workspace != null && workspace.ActiveMode != null) workspace.Reanchor();

            scratch.Clear();
            pending.Clear();
            for (int i = 0; i < windows.Count; i++)
            {
                OSWindow w = windows[i];
                if (w == null || w.IsGroup || w.Root == null || !w.gameObject.activeInHierarchy) continue;
                scratch.Add(w);
                if (w.Panel != null) pending.Add(w.Panel);
            }
            for (int i = 0; i < scratch.Count; i++)
            {
                OSWindow w = scratch[i];
                if (w.Panel != null) pending.Remove(w.Panel);
                if (FindSpot(w, head, out Vector3 center, out Quaternion rotation)) Glide(w, center, rotation, i * BringStagger);
            }
            scratch.Clear();
            pending.Clear();
        }

        // The pose of a window's root that puts its centre at `center`.
        static Vector3 RootPositionFor(OSWindow window, Vector3 center, Quaternion rotation)
        {
            Transform root = window.Root;
            if (root is RectTransform rect) return center - rotation * (Quaternion.Inverse(root.rotation) * (root.TransformPoint(rect.rect.center) - root.position));
            return center - Vector3.up * (HeightOf(window) * 0.5f);
        }

        void Put(OSWindow window, Vector3 center, Quaternion rotation)
        {
            Transform root = window.Root;
            if (root == null) return;
            Vector3 position = RootPositionFor(window, center, rotation);
            window.TargetCenter = center;
            root.SetPositionAndRotation(position, rotation);
            if (window.Panel != null && window.Panel.Handles != null) window.Panel.Handles.SetPlacement(PanelFacing.FaceMe, false);
        }

        void Glide(OSWindow window, Vector3 center, Quaternion rotation, float delay)
        {
            Transform root = window.Root;
            if (root == null) return;
            Vector3 toPosition = RootPositionFor(window, center, rotation);
            window.TargetCenter = center;
            if (!Application.isPlaying)
            {
                Put(window, center, rotation);
                return;
            }
            PanelManipulator handles = window.Panel != null ? window.Panel.Handles : null;
            if (handles != null)
            {
                handles.Cancel();
                handles.Busy = true;
            }
            FinishOpen(window);
            StopMotion(window);
            Vector3 fromPosition = root.position;
            Quaternion fromRotation = root.rotation;
            window.Motion = StartCoroutine(ForgeTween.Run(BringSeconds, delay, k =>
            {
                if (root == null) return;
                float e = ForgeTween.SpringDamped(k, 0.9f, 1.3f);
                root.SetPositionAndRotation(Vector3.LerpUnclamped(fromPosition, toPosition, e), Quaternion.SlerpUnclamped(fromRotation, rotation, e));
            }, () =>
            {
                window.Motion = null;
                if (root == null) return;
                root.SetPositionAndRotation(toPosition, rotation);
                if (handles != null)
                {
                    handles.Busy = false;
                    handles.SetPlacement(PanelFacing.FaceMe, false);
                }
            }));
        }

        // What a thing covers seen from the eyes, in degrees: its centre's yaw (right of straight ahead) and pitch, and
        // half its width and height.
        struct Cover
        {
            public float yaw, pitch, halfWidth, halfHeight;
        }

        static Cover CoverOf(Vector3 center, float width, float height, Vector3 eye, Vector3 forward)
        {
            Vector3 offset = center - eye;
            Vector3 flat = new Vector3(offset.x, 0f, offset.z);
            float distance = Mathf.Max(0.1f, offset.magnitude);
            return new Cover
            {
                yaw = flat.sqrMagnitude < 1e-6f ? 0f : Vector3.SignedAngle(forward, flat, Vector3.up),
                pitch = Mathf.Atan2(offset.y, flat.magnitude) * Mathf.Rad2Deg,
                // Yaw is measured in the horizontal plane, so a window high above or low below the eyes covers more of it.
                halfWidth = Mathf.Atan2(width * 0.5f, Mathf.Max(0.1f, flat.magnitude)) * Mathf.Rad2Deg,
                halfHeight = Mathf.Atan2(height * 0.5f, distance) * Mathf.Rad2Deg,
            };
        }

        static bool Overlap(Cover a, Cover b) =>
            Mathf.Abs(Mathf.DeltaAngle(a.yaw, b.yaw)) < a.halfWidth + b.halfWidth + GapDegrees &&
            Mathf.Abs(a.pitch - b.pitch) < a.halfHeight + b.halfHeight + GapDegrees;

        /// <summary>
        /// Finds the free place nearest to straight ahead of the user (measured as the angle from where they face): in a row
        /// at the preferred distance a little below eye level, in a row above it, or in the same two rows further out
        /// (those cost a little more). If the user's view is full, the window goes straight ahead one ring out rather than
        /// not at all.
        /// </summary>
        bool FindSpot(OSWindow window, Transform head, out Vector3 center, out Quaternion rotation)
        {
            center = default;
            rotation = Quaternion.identity;
            if (window == null || window.Root == null || head == null) return false;

            Vector3 eye = head.position;
            Vector3 forward = FlatForward(head);
            bool atDesk = workspace != null && workspace.presence != null && workspace.presence.AtDesk;
            float baseDistance = atDesk ? DeskDistance : AwayDistance;
            float width = WidthOf(window), height = HeightOf(window);
            float floor = atDesk && workspace.presence.Frame != null ? workspace.presence.Frame.position.y + height * 0.5f + DeskClear : float.NegativeInfinity;

            // The things the new window must stay clear of: other panels and volumes, and the mode's laid-out slots.
            var others = CollectCovers(window, eye, forward);

            // The best clear place that is also clear of the room's walls. Only if there is none, the best clear place with the
            // walls pushing it in (rule that place out if that leaves it covering something, and take the next best).
            bool room = BeginRoom(head, forward);
            rejected.Clear();
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                bool fitting = attempt > 0;  // attempt 0: places in the walls don't count at all
                float best = float.MaxValue;
                int bestId = -1;
                Vector3 bestCenter = default;
                for (int tier = 0; tier < Tiers; tier++)
                {
                    float distance = baseDistance + (tier >= 2 ? RingStep : 0f);
                    float lift = tier % 2 == 1 ? RaiseMeters : -DropMeters;
                    float penalty = tier >= 2 ? OuterRingCost : 0f;
                    for (int i = 0; i * YawStep <= MaxYaw; i++)
                    {
                        for (int side = 0; side < (i == 0 ? 1 : 2); side++)
                        {
                            int id = tier * 1000 + i * 2 + side;
                            if (rejected.Contains(id)) continue;
                            float yaw = i * YawStep * (side == 0 ? 1f : -1f);
                            Vector3 candidate = eye + Quaternion.Euler(0f, yaw, 0f) * forward * distance;
                            candidate.y = Mathf.Max(eye.y + lift, floor);
                            Cover mine = CoverOf(candidate, width, height, eye, forward);
                            float cost = Mathf.Sqrt(mine.yaw * mine.yaw + mine.pitch * mine.pitch) + penalty;
                            if (cost >= best || !IsClear(mine, others)) continue;
                            if (room && !fitting && InWall(candidate, width, height)) continue;
                            best = cost;
                            bestId = id;
                            bestCenter = candidate;
                        }
                    }
                }
                if (bestId < 0)
                {
                    if (!fitting && room) continue;  // nothing clear of the walls: now allow places the walls will move
                    break;
                }
                if (room && FitWalls(ref bestCenter, width, height) && !IsClear(CoverOf(bestCenter, width, height, eye, forward), others))
                {
                    rejected.Add(bestId);
                    continue;
                }
                center = bestCenter;
                rotation = FacingFrom(eye, center);
                return true;
            }
            center = eye + forward * (baseDistance + 2f * RingStep);
            center.y = Mathf.Max(eye.y - DropMeters, floor);
            if (room) FitWalls(ref center, width, height);
            rotation = FacingFrom(eye, center);
            return true;
        }

        readonly List<int> rejected = new List<int>();
        static bool IsClear(Cover mine, List<Cover> others)
        {
            for (int i = 0; i < others.Count; i++)
                if (Overlap(mine, others[i])) return false;
            return true;
        }

        readonly List<Cover> covers = new List<Cover>();
        Transform ignoreRoot;  // while set, panels and volumes under it don't count as in the way (see ClearOf)

        /// <summary>
        /// A panel that something else placed (an artifact host puts its panels where it likes, before the OS adopts them) is
        /// moved to the nearest free place if, seen from the eyes, it covers another window, a panel of the open mode or a
        /// volume. Things under <paramref name="ownRoot"/> (the artifact's own panels and model) don't count: they were laid
        /// out together. True when it moved.
        /// </summary>
        public bool ClearOf(OSWindow window, Transform ownRoot)
        {
            Transform head = OSShell.FindHead();
            if (window == null || window.IsGroup || window.Panel == null || window.Root == null || head == null || !window.gameObject.activeInHierarchy) return false;
            Vector3 eye = head.position;
            Vector3 forward = FlatForward(head);
            ignoreRoot = ownRoot;
            List<Cover> others = CollectCovers(window, eye, forward);
            ignoreRoot = null;
            Cover mine = CoverOf(window.Motion != null ? window.TargetCenter : CenterOf(window), WidthOf(window), HeightOf(window), eye, forward);
            if (IsClear(mine, others)) return false;
            pending.Clear();
            if (!FindSpot(window, head, out Vector3 center, out Quaternion rotation)) return false;
            Glide(window, center, rotation, 0f);
            return true;
        }

        /// <summary>
        /// Something new stands in the room (a volume came up): every other open window that now covers it moves to the
        /// nearest free place. Windows under <paramref name="ownRoot"/> are not moved (they were laid out with the thing); it counts as an obstacle for all the others.
        /// </summary>
        public void ClearAll(Transform ownRoot)
        {
            for (int i = 0; i < windows.Count; i++)
            {
                OSWindow w = windows[i];
                if (w == null || w.IsGroup || w.Root == null || closing.Contains(w)) continue;
                if (ownRoot != null && w.Root.IsChildOf(ownRoot)) continue;
                ClearOf(w, null);  // the new thing counts like any other: only its own windows are skipped above
            }
        }

        /// <summary>
        /// <see cref="ClearAll"/> now, and again after the model and its labels have loaded and settled (they are built a
        /// moment after the volume stands there, so the first look sees only the box).
        /// </summary>
        public void ClearAllSoon(Transform ownRoot)
        {
            ClearAll(ownRoot);
            if (Application.isPlaying && isActiveAndEnabled) StartCoroutine(ClearAfter(ownRoot));
        }

        IEnumerator ClearAfter(Transform ownRoot)
        {
            yield return new WaitForSeconds(0.8f);
            if (ownRoot != null) ClearAll(ownRoot);
            yield return new WaitForSeconds(1.7f);
            if (ownRoot != null) ClearAll(ownRoot);
        }

        readonly List<Renderer> volumeRenderers = new List<Renderer>();

        // A volume's box plus everything drawn around it (the model's labels stand outside the box).
        Bounds VolumeBounds(ForgeVolume volume)
        {
            Vector3 box = Vector3.Scale(volume.size, volume.transform.lossyScale);
            var b = new Bounds(volume.transform.position + Vector3.up * (box.y * 0.5f), box);
            volume.GetComponentsInChildren(false, volumeRenderers);
            for (int i = 0; i < volumeRenderers.Count; i++)
            {
                Renderer r = volumeRenderers[i];
                if (r != null && r.enabled && r.bounds.size.sqrMagnitude < 9f) b.Encapsulate(r.bounds);
            }
            volumeRenderers.Clear();
            return b;
        }

        List<Cover> CollectCovers(OSWindow moving, Vector3 eye, Vector3 forward)
        {
            covers.Clear();
            var all = ForgePanel.All;
            for (int i = 0; i < all.Count; i++)
            {
                ForgePanel panel = all[i];
                if (panel == null || (moving.Panel != null && panel == moving.Panel) || pending.Contains(panel)) continue;
                if (ignoreRoot != null && panel.transform.IsChildOf(ignoreRoot)) continue;
                if (workspace != null && workspace.IsOpenItem(panel.gameObject)) continue;  // its laid-out slot is used instead
                var rect = (RectTransform)panel.transform;
                Vector2 size = panel.SizeMeters;
                Vector3 at = rect.TransformPoint(rect.rect.center);
                var other = panel.GetComponent<OSWindow>();
                if (other != null && closing.Contains(other)) continue;
                if (other != null && other.Motion != null) at = other.TargetCenter;  // on its way: keep clear of where it lands
                covers.Add(CoverOf(at, size.x, size.y, eye, forward));
            }
            var volumes = FindObjectsByType<ForgeVolume>(FindObjectsSortMode.None);
            for (int i = 0; i < volumes.Length; i++)
            {
                ForgeVolume volume = volumes[i];
                if (volume == null || !volume.gameObject.activeInHierarchy || (moving.Volume != null && volume == moving.Volume)) continue;
                if (ignoreRoot != null && volume.transform.IsChildOf(ignoreRoot)) continue;
                if (workspace != null && workspace.IsOpenItem(volume.gameObject)) continue;
                Bounds vb = VolumeBounds(volume);
                covers.Add(CoverOf(vb.center, Mathf.Max(vb.size.x, vb.size.z), vb.size.y, eye, forward));
            }
            if (workspace != null && workspace.ActiveMode != null)
            {
                workspace.GetSlots(slots);
                for (int i = 0; i < slots.Count; i++)
                    covers.Add(CoverOf(slots[i].center, slots[i].size.x, slots[i].size.y, eye, forward));
            }
            return covers;
        }

        // Out of the room's walls, if the room is known: the same fitting the workspace uses, in a frame that stands where the
        // user does and faces the way they face. BeginRoom says whether there is a room to fit against.
        bool BeginRoom(Transform head, Vector3 forward)
        {
            if (arc == null)
            {
                arc = new GameObject("OSArcFrame").transform;
                arc.SetParent(transform, false);
            }
            arc.SetPositionAndRotation(head.position, Quaternion.LookRotation(forward, Vector3.up));
            return RoomFit.Begin(arc, Vector3.zero);
        }

        bool InWall(Vector3 center, float width, float height)
        {
            Vector3 local = arc.InverseTransformPoint(center);
            return RoomFit.InWall(local, Quaternion.LookRotation(local, Vector3.up), new Vector3(width, height, 0f), false);
        }

        // True if the walls moved the place.
        bool FitWalls(ref Vector3 center, float width, float height)
        {
            Vector3 local = arc.InverseTransformPoint(center);
            Quaternion localRotation = Quaternion.LookRotation(local, Vector3.up);
            var size = new Vector3(width, height, 0f);
            if (!RoomFit.InWall(local, localRotation, size, false)) return false;
            if (!RoomFit.Fit(ref local, ref localRotation, size, false, true)) return false;
            center = arc.TransformPoint(local);
            return true;
        }

        static Quaternion FacingFrom(Vector3 eye, Vector3 center)
        {
            Vector3 toward = center - eye;
            return toward.sqrMagnitude < 1e-6f ? Quaternion.identity : Quaternion.LookRotation(toward, Vector3.up);
        }

        static Vector3 FlatForward(Transform head)
        {
            Vector3 flat = head.forward;
            flat.y = 0f;
            return flat.sqrMagnitude < 1e-4f ? Vector3.forward : flat.normalized;
        }

        static Vector3 CenterOf(OSWindow window) =>
            window.Root is RectTransform rect ? rect.TransformPoint(rect.rect.center) : window.Root.position;

        static float WidthOf(OSWindow window)
        {
            if (window.Panel != null) return window.Panel.SizeMeters.x;  // the canvas carries the 0.001 scale, so this already is metres
            if (window.Volume != null) return window.Volume.size.x * Mathf.Abs(window.Volume.transform.lossyScale.x);
            return FallbackWidth;
        }

        static float HeightOf(OSWindow window)
        {
            if (window.Panel != null) return window.Panel.SizeMeters.y;
            if (window.Volume != null) return window.Volume.size.y * Mathf.Abs(window.Volume.transform.lossyScale.y);
            return FallbackHeight;
        }
    }
}
