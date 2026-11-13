using System;
using System.Collections.Generic;
using Forge.Pet;
using UnityEngine;

namespace Forge.OS
{
    /// <summary>
    /// The one Albie OS shell in a scene: whether the menu is open, which tab it is on, the open windows, and the events
    /// and calls everything else uses (voice tools, keys, buttons). It holds state and raises events and needs no menu
    /// view: the view subscribes to <see cref="MenuChanged"/> and <see cref="TabChanged"/>. <see cref="OSBootstrap"/> adds
    /// it to any scene with a Workspace, as a root object (not under the workspace or the desk frame).
    /// </summary>
    public class OSShell : MonoBehaviour
    {
        /// <summary>System pages that parts register with <see cref="RegisterSystem"/>.</summary>
        public static readonly string[] SystemIds = { "agents", "settings", "build" };

        static readonly Dictionary<string, Action> systems = new Dictionary<string, Action>();
        static OSTab lastTab = OSTab.Tools;  // remembered for the session, across scenes

        /// <summary>The shell in this scene, or null (a sandbox has none).</summary>
        public static OSShell Current { get; private set; }

        /// <summary>Set by <see cref="OSBootstrap"/> before OnEnable runs.</summary>
        public Workspace workspace;

        OSWindows windows;
        bool menuOpen;

        public bool MenuOpen => menuOpen;
        public OSTab Tab => lastTab;
        /// <summary>Where the last <see cref="OpenMenu"/> came from.</summary>
        public OSSummon LastSummon { get; private set; } = OSSummon.Code;
        /// <summary>Where the last summon says the menu grows from (the table button, the wrist, the corner), if it said.</summary>
        public Vector3? SummonPoint { get; private set; }

        /// <summary>The window manager on this shell.</summary>
        public OSWindows Windows
        {
            get
            {
                if (windows == null)
                {
                    windows = GetComponent<OSWindows>();
                    if (windows == null) windows = gameObject.AddComponent<OSWindows>();
                }
                return windows;
            }
        }

        /// <summary>Whether the user is at a desk (the workspace says so).</summary>
        public bool AtDesk => workspace != null && workspace.presence != null && workspace.presence.AtDesk;
        /// <summary>True while the pet's first-arrival greeting holds the desk.</summary>
        public bool Held => workspace != null && workspace.DockHeld;
        /// <summary>The user's head: the pet's idea of it, else the main camera.</summary>
        public Transform Head => FindHead();

        public event Action<bool> MenuChanged;
        public event Action<OSTab> TabChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Current = null;
            systems.Clear();
            lastTab = OSTab.Tools;
        }

        /// <summary>The user's head transform, or null if nothing renders.</summary>
        public static Transform FindHead()
        {
            Transform head = PetUser.Head;
            if (head != null) return head;
            Camera main = Camera.main;
            return main != null ? main.transform : null;
        }

        /// <summary>
        /// A later part registers what "agents", "settings" or "build" opens. A second call for the same id replaces the
        /// first. Reset when the app starts.
        /// </summary>
        public static void RegisterSystem(string id, Action open)
        {
            if (string.IsNullOrEmpty(id) || open == null) return;
            systems[id] = open;
        }

        public static bool HasSystem(string id) => !string.IsNullOrEmpty(id) && systems.ContainsKey(id);

        void OnEnable()
        {
            if (Current != null && Current != this)
            {
                Debug.Log("[OS] A shell is already running; this one goes away.");
                Destroy(gameObject);
                return;
            }
            Current = this;
            OSModes.Attach(this);
        }

        void OnDisable()
        {
            if (Current == this)
            {
                Current = null;
                OSModes.Detach(this);
            }
        }

        public void OpenMenu(OSSummon from = OSSummon.Code, Vector3? summonPoint = null)
        {
            LastSummon = from;
            SummonPoint = summonPoint;
            if (menuOpen) return;
            menuOpen = true;
            MenuChanged?.Invoke(true);
        }

        public void CloseMenu()
        {
            if (!menuOpen) return;
            menuOpen = false;
            MenuChanged?.Invoke(false);
        }

        public void ToggleMenu(OSSummon from = OSSummon.Code, Vector3? summonPoint = null)
        {
            if (menuOpen) CloseMenu(); else OpenMenu(from, summonPoint);
        }

        public void SetTab(OSTab tab)
        {
            if (tab == lastTab) return;
            lastTab = tab;
            TabChanged?.Invoke(tab);
        }

        /// <summary>
        /// Opens what a tile would. kind: "tool", "project", "artifact" (the entry's open action) or "system" ("agents",
        /// "settings", "build"). False with the reason in <paramref name="error"/> when it can't. Leaves the menu as it is.
        /// </summary>
        public bool OpenItem(string kind, string id, out string error)
        {
            error = null;
            kind = (kind ?? "").Trim().ToLowerInvariant();
            id = (id ?? "").Trim();
            if (id.Length == 0)
            {
                error = "Say which one to open.";
                return false;
            }
            if (kind == "system") return OpenSystemPage(id, out error);

            OSTab tab;
            switch (kind)
            {
                case "tool": tab = OSTab.Tools; break;
                case "project": tab = OSTab.Projects; break;
                case "artifact": tab = OSTab.Artifacts; break;
                default:
                    error = "Unknown kind '" + kind + "'. It is tool, project, artifact or system.";
                    return false;
            }
            OSEntry entry = OSRegistry.Find(tab, id);
            if (entry == null)
            {
                error = "There is no " + kind + " called '" + id + "' in the menu.";
                return false;
            }
            if (entry.open == null)
            {
                error = "'" + entry.title + "' can't be opened right now.";
                return false;
            }
            try
            {
                entry.open();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                error = "Opening '" + entry.title + "' failed: " + e.Message;
                return false;
            }
            return true;
        }

        /// <summary>Opens a system page: "agents", "settings" or "build".</summary>
        public void OpenSystem(string id)
        {
            if (!OpenSystemPage(id, out string error)) Debug.Log("[OS] " + error);
        }

        static bool OpenSystemPage(string id, out string error)
        {
            error = null;
            if (systems.TryGetValue(id, out Action open))
            {
                try
                {
                    open();
                    return true;
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    error = "Opening '" + id + "' failed: " + e.Message;
                    return false;
                }
            }
            error = Array.IndexOf(SystemIds, id) >= 0
                ? "The '" + id + "' page isn't available yet."
                : "There is no system page called '" + id + "'. They are agents, settings and build.";
            return false;
        }
    }
}
