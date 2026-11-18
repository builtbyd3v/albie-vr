using Forge.Controllers;
using UnityEngine;

namespace Forge.OS
{
    /// <summary>
    /// The fixed entries of the Tools tab: CAD, Code, Train, Notebook, Messages, Gestures, Drive and Agents. Drive's tile is made by
    /// <see cref="OSDriveWindow.RegisterTool"/> and comes after Gestures. The four
    /// Workspace modes open through <see cref="OSModes"/>, so they work at a desk and away from one; Train also opens the
    /// controller brain trainer when a brain truck is running; Notebook opens <see cref="OSNotebookWindow"/>; Gestures shows
    /// the gesture display the same way the gesture "display.toggle" does and hands it to the window manager, so Bring windows
    /// here and Close all reach it. Every status line is read from real state, and a status string is rebuilt only when what
    /// it says changes.
    /// </summary>
    public static class OSToolsCatalog
    {
        /// <summary>Window id of the controller brain trainer.</summary>
        public const string BrainId = "brain";
        /// <summary>Window id of the gesture display once the window manager tracks it.</summary>
        public const string GesturesId = "gestures";

        static readonly Vector2 BrainSize = new Vector2(0.78f, 0.62f);  // metres; the view lays itself out in ~500 mm of height

        // Status strings that are rebuilt only when their inputs change (the menu may ask often).
        static int trainKey = -1;
        static string trainText = "";
        static int gestureCount = -1;
        static string gestureText = "";

        // The gesture display is found once and kept (it is made by GestureActions on first show and never destroyed).
        static GesturePanel gestureDisplay;
        static float nextGestureFind;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            trainKey = -1;
            trainText = "";
            gestureCount = -1;
            gestureText = "";
            gestureDisplay = null;
            nextGestureFind = 0f;
        }

        /// <summary>Adds the Tools tab's entries. The app does this at start; a check can call it again (same ids replace).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Register()
        {
            OSRegistry.RegisterTool(ModeTool("cad", "CAD", "cube"));
            OSRegistry.RegisterTool(ModeTool("code", "Code", "code"));
            OSRegistry.RegisterTool(new OSEntry
            {
                id = "train",
                title = "Train",
                icon = "train",
                status = TrainStatus,
                isOpen = () => OSModes.IsOpen("ml") || WindowOpen(BrainId),
                open = OpenTrain,
            });
            OSRegistry.RegisterTool(new OSEntry
            {
                id = "notebook",
                title = "Notebook",
                icon = "notebook",
                status = NotebookStatus,
                isOpen = () => WindowOpen(OSNotebookWindow.WindowId),
                open = () => OSNotebookWindow.Open(),
            });
            OSRegistry.RegisterTool(ModeTool("messages", "Messages", "messages"));
            OSRegistry.RegisterTool(new OSEntry
            {
                id = "gestures",
                title = "Gestures",
                icon = "gestures",
                status = GesturesStatus,
                isOpen = GesturesOpen,
                open = OpenGestures,
            });
            OSDriveWindow.RegisterTool();  // Drive follows Gestures
            OSRegistry.RegisterTool(new OSEntry
            {
                id = "agents",
                title = "Agents",
                icon = "agents",
                status = () => "crew and project workflow",
                isOpen = () => WindowOpen(OSAgentsWindow.WindowId),
                open = () => OSAgentsWindow.Open(),
            });
        }

        // ---------------------------------------------------------------- Workspace modes (CAD, Code, Messages)

        static OSEntry ModeTool(string modeId, string title, string icon)
        {
            return new OSEntry
            {
                id = modeId,
                title = title,
                icon = icon,
                status = () => OSModes.IsOpen(modeId) ? "open" : "",
                isOpen = () => OSModes.IsOpen(modeId),
                open = () =>
                {
                    if (!OSModes.Open(modeId)) Debug.Log("[OS] " + title + " couldn't open here.");
                },
            };
        }

        static bool WindowOpen(string id)
        {
            OSShell shell = OSShell.Current;
            return shell != null && shell.Windows.IsOpen(id);
        }

        // ---------------------------------------------------------------- Train: the ML desk, plus the brain trainer when a truck runs

        static void OpenTrain()
        {
            if (!OSModes.Open("ml")) Debug.Log("[OS] Train couldn't open the ML desk here.");
            // Nothing in Main shows the controller brain panel, so the OS does: as a window of its own, beside the ML desk,
            // whenever a brain truck is running (OSProjects.LiveLoop is the SlalomBrain that ArtifactBootstrap.EnsureBrain spawns).
            if (OSProjects.LiveLoop != null) OpenBrain();
            // The hint about the slalom is for the truck project only: the classifier and the other projects train on the ML desk.
            else if (OSShell.Current != null && (!Forge.Notebook.NotebookStore.IsOpen || OSProjects.KindOf(Forge.Notebook.NotebookStore.ProjectId) == OSProjectKind.Truck))
                OSShell.Current.Windows.Open("train_help", "Learn to drive", new Vector2(0.48f, 0.28f), BuildTrainHelp);
        }

        static void BuildTrainHelp(ForgePanel panel)
        {
            var body = OSUi.Body(panel);
            if (body == null) return;
            var column = OSUi.Column(body, 19f);
            var status = OSUi.Text(column, "Open the slalom first, then I can learn to drive it.", OSUi.BodyText);
            OSUi.Button(column, "Open slalom", () =>
            {
                OSProjects.OpenProject(Forge.Notebook.NotebookStore.DefaultProject);
                var project = OSShell.Current?.Windows.Find(OSProjectWindow.WindowId)?.GetComponent<OSProjectWindow>();
                status.text = project != null ? project.Do("course") : "The truck project couldn't open.";
            }, OSButtonStyle.Primary);
        }

        /// <summary>
        /// Opens the controller brain trainer as an OS window (id "brain"), or brings it forward. The view is the same
        /// ForgePanel + ControllerBrainView as the MLPanels/ControllerBrain prefab; it needs nothing else. Null when this
        /// scene has no shell or no panel prefab.
        /// </summary>
        public static OSWindow OpenBrain()
        {
            OSShell shell = OSShell.Current;
            if (shell == null)
            {
                Debug.Log("[OS] The brain trainer needs the Albie menu, and this scene has none.");
                return null;
            }
            return shell.Windows.Open(BrainId, "Controller brain", BrainSize, BuildBrain);
        }

        static void BuildBrain(ForgePanel panel)
        {
            if (panel.GetComponent<Forge.ML.ControllerBrainView>() == null) panel.gameObject.AddComponent<Forge.ML.ControllerBrainView>();
        }

        static string TrainStatus()
        {
            bool open = OSModes.IsOpen("ml") || WindowOpen(BrainId);
            IBrainLoop loop = OSProjects.LiveLoop;
            var brain = loop as Forge.Artifacts.SlalomBrain;
            int state = loop == null ? -1 : (brain != null ? (int)brain.State : 0);
            int laps = brain != null ? Mathf.Clamp(brain.CompletedLaps, 0, 99) : 0;
            int key = (open ? 1 : 0) | ((state + 1) << 1) | (laps << 4);
            if (key == trainKey) return trainText;

            string truck;
            if (loop == null) truck = "Open slalom to train";
            else if (state == (int)Forge.Artifacts.SlalomBrain.Mode.Lap) truck = "recording a lap";
            else if (state == (int)Forge.Artifacts.SlalomBrain.Mode.Training) truck = "training";
            else if (state == (int)Forge.Artifacts.SlalomBrain.Mode.Autopilot) truck = "autopilot driving";
            else if (laps == 1) truck = "brain ready · 1 lap";
            else if (laps > 1) truck = "brain ready · " + laps + " laps";
            else truck = "brain ready";

            trainKey = key;
            trainText = open ? "open · " + truck : truck;
            return trainText;
        }

        // ---------------------------------------------------------------- Notebook

        static string NotebookStatus()
        {
            return Forge.Notebook.NotebookStore.IsOpen ? OSNotebookWindow.ProjectName(Forge.Notebook.NotebookStore.ProjectId) : "no project open";
        }

        // ---------------------------------------------------------------- Gestures

        // The display is made by GestureActions.ShowDisplay (what the "display.toggle" gesture runs) and lives for the
        // whole session, hidden or shown. Looked up at most once a second until found.
        static GesturePanel FindDisplay(bool force)
        {
            if (gestureDisplay != null) return gestureDisplay;
            if (!force && Time.unscaledTime < nextGestureFind) return null;
            nextGestureFind = Time.unscaledTime + 1f;
            gestureDisplay = UnityEngine.Object.FindAnyObjectByType<GesturePanel>(FindObjectsInactive.Include);
            return gestureDisplay;
        }

        // True while the display is on screen. The first time it is seen on screen (the gesture engine also shows it when a
        // hand first appears) it is handed to the window manager, so Bring windows here and Close all reach it.
        static bool GesturesOpen()
        {
            GesturePanel display = FindDisplay(false);
            if (display == null || !display.gameObject.activeInHierarchy) return false;
            Track(display, dismissMenu: false);  // reading a tile's status must not dismiss the menu being built
            return true;
        }

        static void Track(GesturePanel display, bool dismissMenu = true)
        {
            OSShell shell = OSShell.Current;
            if (shell == null || display == null) return;
            OSWindows windows = shell.Windows;
            if (windows.Find(GesturesId) != null) return;
            windows.Adopt(GesturesId, "Hands", display.gameObject, CloseGestures, dismissMenu);
        }

        static void CloseGestures()
        {
            GestureEngine engine = GestureEngine.Current;
            if (engine != null && engine.Actions != null) engine.Actions.ShowDisplay(false);
        }

        /// <summary>
        /// Puts the saved Gesture display setting into effect: with the flag on, shows the display (the same path as the Tools tile).
        /// Returns false while the gesture engine isn't there yet, so the caller can try again; true once there is nothing left
        /// to do (the flag is off, or the display was shown). It never hides a display something else showed.
        /// </summary>
        public static bool ApplySavedGestureDisplay()
        {
            if (!OSSettings.Current.gestureDisplay) return true;
            GestureEngine engine = GestureEngine.Current;
            if (engine == null || engine.Actions == null) return false;
            OpenGestures();
            return true;
        }

        static void OpenGestures()
        {
            GestureEngine engine = GestureEngine.Current;
            if (engine == null || engine.Actions == null)
            {
                Debug.Log("[OS] Hand gestures aren't running here.");
                return;
            }
            engine.Actions.ShowDisplay(true);  // the same path as display.toggle: makes the panel on first show, then shows it
            GesturePanel display = FindDisplay(true);
            if (display == null) return;
            Track(display);
            // Already tracked, so this only brings it forward (out of its dot, in front of the user if it isn't).
            OSShell shell = OSShell.Current;
            if (shell != null) shell.Windows.Open(GesturesId, "Hands", Vector2.one, null);
        }

        static string GesturesStatus()
        {
            if (GesturesOpen()) return "open";
            GestureEngine engine = GestureEngine.Current;
            if (engine == null || engine.Library == null) return "not running here";
            int count = engine.Library.All.Count;
            if (count != gestureCount)
            {
                gestureCount = count;
                gestureText = count == 1 ? "1 gesture" : count + " gestures";
            }
            return gestureText;
        }
    }
}
