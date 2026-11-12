using System;
using System.Collections.Generic;
using UnityEngine;

namespace Forge.OS
{
    /// <summary>
    /// The window manager. A window is a free ForgePanel at the scene root (never under the desk frame, so it doesn't
    /// fold when the user leaves the desk) with a PanelManipulator, so the move, turn and resize handles and the hand
    /// gestures reach it. Opening an id that is already open brings that window forward instead of making another.
    /// <see cref="Adopt"/> tracks something that already exists, so <see cref="BringHere"/> and <see cref="CloseAll"/> reach
    /// it too. All placement is in <see cref="PlaceNew"/> and <see cref="PlaceRow"/>.
    /// </summary>
    public class OSWindows : MonoBehaviour
    {
        // Where windows go: in front of the user, this far away, with a small gap between neighbours (degrees).
        const float WindowDistance = 0.95f, MaxRowDistance = 1.8f, DistanceStep = 0.15f, GapDegrees = 3f, MaxRowDegrees = 150f;
        const float DropMeters = 0.05f, VolumeDropMeters = 0.5f, FallbackWidth = 0.5f;
        const int FreeSlots = 7;

        readonly List<OSWindow> windows = new List<OSWindow>();
        readonly Dictionary<string, OSWindow> byId = new Dictionary<string, OSWindow>(StringComparer.Ordinal);
        readonly List<OSWindow> scratch = new List<OSWindow>();

        public event Action Changed;

        public IReadOnlyList<OSWindow> All => windows;
        public int Count => windows.Count;

        /// <summary>
        /// A new free window (a ForgePanel root object with a PanelManipulator), or the existing one with this id brought in
        /// front of the user. <paramref name="build"/> fills the panel's Content the first time. Size in metres. Null if
        /// this scene has no panel prefab.
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
            if (panel.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null) panel.gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            if (panel.Content != null)
            {
                Transform placeholder = panel.Content.Find("Placeholder");
                if (placeholder != null) placeholder.gameObject.SetActive(false);
            }

            Transform head = OSShell.FindHead();
            var handles = panel.GetComponent<PanelManipulator>();
            if (handles == null) handles = panel.gameObject.AddComponent<PanelManipulator>();
            handles.Init(head, DeskFrame(head));

            var window = panel.gameObject.AddComponent<OSWindow>();
            window.Init(this, id, title, panel, null, false, true, null);
            Track(window);
            PlaceNew(window);

            try
            {
                build?.Invoke(panel);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
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
            Track(window);
            return window;
        }

        public OSWindow Find(string id) =>
            !string.IsNullOrEmpty(id) && byId.TryGetValue(id, out OSWindow w) && w != null ? w : null;

        public bool IsOpen(string id) => Find(id) != null;

        public void Close(string id) => Find(id)?.Close();

        public void CloseAll()
        {
            scratch.Clear();
            scratch.AddRange(windows);
            for (int i = scratch.Count - 1; i >= 0; i--)
                if (scratch[i] != null) scratch[i].Close();
            scratch.Clear();
        }

        /// <summary>Springs every window back around the user: one row in front, side by side, not overlapping.</summary>
        public void BringHere()
        {
            Transform head = OSShell.FindHead();
            if (head == null) return;
            scratch.Clear();
            for (int i = 0; i < windows.Count; i++)
            {
                OSWindow w = windows[i];
                if (w == null || w.IsGroup || w.Root == null || !w.gameObject.activeInHierarchy) continue;
                scratch.Add(w);
            }
            if (scratch.Count > 0) PlaceRow(scratch, head);
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
            Workspace workspace = Workspace.Current;
            if (workspace == null && OSShell.Current != null) workspace = OSShell.Current.workspace;
            return workspace != null ? workspace.panelPrefab : null;
        }

        static Transform DeskFrame(Transform head)
        {
            Workspace workspace = Workspace.Current;
            return workspace != null && workspace.presence != null && workspace.presence.Frame != null ? workspace.presence.Frame : head;
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

        // ---------------------------------------------------------------- placement (a later part may replace these two)

        /// <summary>
        /// Puts one window in front of the user at the nearest free spot: straight ahead if nothing is there, else the next
        /// spot to the right, then left, and so on, so a new window doesn't land on one that's already open.
        /// </summary>
        void PlaceNew(OSWindow window)
        {
            Transform head = OSShell.FindHead();
            if (head == null || window == null || window.Root == null) return;
            Vector3 forward = FlatForward(head);
            float half = HalfAngle(WidthOf(window), WindowDistance);
            float step = half * 2f + GapDegrees;

            float yaw = 0f;
            for (int slot = 0; slot < FreeSlots; slot++)
            {
                float candidate = ((slot + 1) / 2) * step * (slot % 2 == 1 ? 1f : -1f);
                if (slot == 0) candidate = 0f;
                if (IsFree(window, head, forward, candidate, half)) { yaw = candidate; break; }
            }
            PlaceAt(window, head, forward, yaw, WindowDistance);
        }

        // Free of every other panel in the scene (the gesture display, a desk's windows, a card) and of the OS's own volumes.
        bool IsFree(OSWindow moving, Transform head, Vector3 forward, float yaw, float half)
        {
            var all = ForgePanel.All;
            for (int i = 0; i < all.Count; i++)
            {
                ForgePanel panel = all[i];
                if (panel == null || (moving.Panel != null && panel == moving.Panel)) continue;
                if (!Overlaps(((RectTransform)panel.transform).TransformPoint(((RectTransform)panel.transform).rect.center), panel.SizeMeters.x, head, forward, yaw, half)) continue;
                return false;
            }
            for (int i = 0; i < windows.Count; i++)
            {
                OSWindow other = windows[i];
                if (other == moving || other == null || other.IsGroup || other.Root == null || other.Panel != null || !other.gameObject.activeInHierarchy) continue;
                if (Overlaps(CenterOf(other), WidthOf(other), head, forward, yaw, half)) return false;
            }
            return true;
        }

        static bool Overlaps(Vector3 center, float width, Transform head, Vector3 forward, float yaw, float half)
        {
            Vector3 offset = center - head.position;
            offset.y = 0f;
            float distance = offset.magnitude;
            if (distance < 0.2f) return false;
            float otherYaw = Vector3.SignedAngle(forward, offset, Vector3.up);
            return Mathf.Abs(Mathf.DeltaAngle(yaw, otherYaw)) < half + HalfAngle(width, distance) + GapDegrees - 0.5f;  // spots a gap apart are free, not touching
        }

        /// <summary>Lays the windows in one row in front of the user, left to right in the order given, edge to edge with a small gap.</summary>
        void PlaceRow(List<OSWindow> row, Transform head)
        {
            Vector3 forward = FlatForward(head);
            float distance = WindowDistance;
            float total = RowSpan(row, distance);
            while (total > MaxRowDegrees && distance < MaxRowDistance)
            {
                distance += DistanceStep;
                total = RowSpan(row, distance);
            }
            float left = -total * 0.5f;
            for (int i = 0; i < row.Count; i++)
            {
                float half = HalfAngle(WidthOf(row[i]), distance);
                PlaceAt(row[i], head, forward, left + half, distance);
                left += half * 2f + GapDegrees;
            }
        }

        static float RowSpan(List<OSWindow> row, float distance)
        {
            float total = GapDegrees * (row.Count - 1);
            for (int i = 0; i < row.Count; i++) total += HalfAngle(WidthOf(row[i]), distance) * 2f;
            return total;
        }

        // yaw: degrees to the right of straight ahead. A panel stands upright facing the user; a volume rests lower.
        static void PlaceAt(OSWindow window, Transform head, Vector3 forward, float yaw, float distance)
        {
            Vector3 direction = Quaternion.Euler(0f, yaw, 0f) * forward;
            Quaternion facing = Quaternion.LookRotation(direction, Vector3.up);
            Transform root = window.Root;
            if (root == null) return;
            if (root is RectTransform rect)
            {
                Vector3 center = head.position + direction * distance + Vector3.down * DropMeters;
                root.rotation = facing;
                root.position += center - root.TransformPoint(rect.rect.center);
            }
            else
            {
                root.rotation = facing;
                root.position = head.position + direction * distance + Vector3.down * VolumeDropMeters;
            }
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

        static float HalfAngle(float width, float distance) => Mathf.Atan2(width * 0.5f, Mathf.Max(0.1f, distance)) * Mathf.Rad2Deg;
    }
}
