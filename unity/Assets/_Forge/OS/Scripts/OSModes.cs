using UnityEngine;

namespace Forge.OS
{
    /// <summary>
    /// Opens the Workspace modes (cad, ml, code, messages) for the OS, at a desk and away from one. The modes stay
    /// Workspace modes (eleven call sites depend on <see cref="Workspace.ActiveMode"/>); the OS gives the workspace a few
    /// hooks when its shell starts (<see cref="Attach"/>): a virtual desk frame in front of the user for when there is no
    /// desk, keep-open-away, a retired dock, and the flat/curved layout from the settings. One mode is open at a time, as
    /// before. The open mode is tracked in <see cref="OSWindows"/> as one group window (<see cref="OSWindows.GroupId"/>),
    /// so the menu can show it as open, Close all closes it and Bring here lays it out again around the user.
    /// </summary>
    public static class OSModes
    {
        /// <summary>The Workspace mode ids the OS opens.</summary>
        public static readonly string[] ModeIds = { "cad", "ml", "code", "messages" };

        // The virtual desk frame sits so the eyes are in the same place relative to it as a seated user's are to a real desk's front edge.
        const float EyeBack = 0.5f, EyeAbove = 0.5f;

        static Workspace attached;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            OSSettings.Changed -= ApplyLayout;
            attached = null;
        }

        /// <summary>Opens a mode. Works at a desk and away from one. False if there is no such mode or the workspace can't open it.</summary>
        public static bool Open(string modeId)
        {
            Workspace ws = Find();
            if (ws == null || string.IsNullOrEmpty(modeId) || ws.modes.Find(m => m.id == modeId) == null) return false;
            if (ws.ActiveMode == modeId)
            {
                // Already open: if it is behind the user or far off, bring it round to them.
                if (IsAway(ws)) ws.Reanchor();
                return true;
            }
            if (!ws.CanOpen) return false;
            ws.Open(modeId);
            return ws.ActiveMode == modeId;
        }

        /// <summary>Whether this mode is the open one.</summary>
        public static bool IsOpen(string modeId)
        {
            Workspace ws = Find();
            return ws != null && !string.IsNullOrEmpty(modeId) && ws.ActiveMode == modeId;
        }

        /// <summary>Closes the open mode, if any.</summary>
        public static void Close() => Find()?.Close();

        /// <summary>The name the menu and the window list give a mode.</summary>
        public static string Title(string modeId)
        {
            switch (modeId)
            {
                case "cad": return "CAD";
                case "ml": return "Train";
                case "code": return "Code";
                case "messages": return "Messages";
                default: return modeId;
            }
        }

        static Workspace Find()
        {
            if (attached != null) return attached;
            OSShell shell = OSShell.Current;
            return shell != null && shell.workspace != null ? shell.workspace : Workspace.Current;
        }

        // The mode's items are mostly behind the user or well out of reach.
        static bool IsAway(Workspace ws)
        {
            Transform head = OSShell.FindHead();
            if (head == null) return false;
            var slots = new System.Collections.Generic.List<Workspace.ItemSlot>(8);
            ws.GetSlots(slots);
            if (slots.Count == 0) return false;
            int seen = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                Vector3 offset = slots[i].center - head.position;
                if (offset.magnitude < 1.6f && Vector3.Angle(head.forward, offset) < 70f) seen++;
            }
            return seen == 0;
        }

        // ---------------------------------------------------------------- the hooks into the workspace

        /// <summary>Called by the shell when it starts: gives the workspace the OS's hooks.</summary>
        internal static void Attach(OSShell shell)
        {
            Workspace ws = shell != null ? shell.workspace : null;
            if (ws == null) return;
            attached = ws;
            ws.KeepOpenAway = true;
            ws.DockRetired = true;
            ws.FreeFrame = FreePose;
            if (ws.dock != null && ws.dock.IsShown) ws.dock.Hide(immediate: true);
            OSSettings.Changed -= ApplyLayout;
            OSSettings.Changed += ApplyLayout;
            ApplyLayout();
        }

        /// <summary>Called by the shell when it goes away: the workspace works as it did without the OS.</summary>
        internal static void Detach(OSShell shell)
        {
            Workspace ws = shell != null ? shell.workspace : null;
            OSSettings.Changed -= ApplyLayout;
            if (ws == null) return;
            ws.KeepOpenAway = false;
            ws.DockRetired = false;
            ws.FreeFrame = null;
            if (attached == ws) attached = null;
        }

        // In front of the user, facing the way they face, level with the floor under their eyes' height minus a seated desk's.
        static bool FreePose(out Vector3 position, out Quaternion rotation)
        {
            Transform head = OSShell.FindHead();
            if (head == null)
            {
                position = default;
                rotation = Quaternion.identity;
                return false;
            }
            Vector3 forward = head.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude < 1e-4f ? Vector3.forward : forward.normalized;
            rotation = Quaternion.LookRotation(forward, Vector3.up);
            position = head.position + forward * EyeBack - Vector3.up * EyeAbove;
            return true;
        }

        static void ApplyLayout()
        {
            Workspace ws = attached;
            if (ws == null) return;
            WorkspaceLayout want = OSSettings.Current.flatLayout ? WorkspaceLayout.Flat : WorkspaceLayout.Curved;
            if (ws.layout != want) ws.SetLayout(want);
        }
    }
}
