using System;
using Forge.Artifacts;
using Forge.Pet;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// The Settings window (window id "settings", opened by the Settings button in the menu strip). Four pages, each a few
    /// sections of rows (label left, control right), paged with a back and next button the way the menu pages:
    /// 1 Menu button and Voice, 2 Windows and Menu position, 3 Hands, 4 Reset and About. Every change is written through
    /// <see cref="OSSettings"/> at once and takes effect without a restart: the table button, the corner button and the
    /// wrist turn read the setting each frame, the layout is applied to the workspace now, the menu distance and height are
    /// read the next time the menu opens. The two resets ask first and do nothing until the confirm tap. Voice is shown as a
    /// plain sentence, not a switch, because the app has no setting that turns hands-free off.
    /// </summary>
    public sealed class OSSettingsWindow : MonoBehaviour
    {
        public const string WindowId = "settings";
        public const int PageCount = 4;

        /// <summary>Ranges the two sliders offer (inside what <see cref="OSSettings"/> allows).</summary>
        public const float DistanceMin = OSSettings.MinDistance, DistanceMax = OSSettings.MaxDistance, DropMin = OSSettings.MinDrop, DropMax = OSSettings.MaxDrop;

        static readonly Vector2 SizeMeters = new Vector2(0.74f, 0.62f);
        const float NoteSize = OSUi.SmallText;

        ForgePanel panel;
        int page;
        bool dirty, saving;
        float saveAt = -1f;  // a slider edit is in memory and the write waits for the drag to settle (-1 = nothing waiting)

        /// <summary>Seconds a slider drag must rest before the settings file is written (toggles and choices write at once).</summary>
        public const float SliderSaveDelay = 0.25f;

        /// <summary>True while a slider edit is held in memory and not yet written.</summary>
        public bool SavePending => saveAt >= 0f;
        string resetNote = "";

        /// <summary>The page showing, 0 to 3.</summary>
        public int Page => page;

        /// <summary>What the last reset said (empty until one was done).</summary>
        public string ResetNote => resetNote;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Register() => OSShell.RegisterSystem(WindowId, () => Open());

        /// <summary>Opens Settings, or brings it forward. Null when the scene has no shell.</summary>
        public static OSWindow Open()
        {
            OSShell shell = OSShell.Current;
            if (shell == null)
            {
                Debug.Log("[OS] Settings need the Albie menu, and this scene has none.");
                return null;
            }
            return shell.Windows.Open(WindowId, "Settings", SizeMeters, Build, "SETTINGS");
        }

        static void Build(ForgePanel forgePanel)
        {
            var view = forgePanel.gameObject.AddComponent<OSSettingsWindow>();
            view.panel = forgePanel;
            view.Rebuild();
        }

        void OnEnable()
        {
            OSSettings.Changed += OnSettingsChanged;
            if (panel != null) dirty = true;  // back from being put away: the file may have changed
        }

        // The Hands setting lives with the gestures (GestureLibrary), not in OSSettings: it can also change from the gesture
        // display's pill or by voice, and the page shows the real one.
        GestureLibrary watchedGestures;

        void WatchGestures(GestureLibrary library)
        {
            if (watchedGestures == library) return;
            if (watchedGestures != null) watchedGestures.Changed -= OnGesturesChanged;
            watchedGestures = library;
            if (library != null) library.Changed += OnGesturesChanged;
        }

        void OnGesturesChanged()
        {
            if (page == HandsPage) dirty = true;
        }

        void OnDisable()
        {
            WatchGestures(null);
            OSSettings.Changed -= OnSettingsChanged;
            FlushSave();  // a slider edit still waiting for its write is not lost when the window is put away
        }

        void OnDestroy() => FlushSave();

        void OnApplicationPause(bool paused)
        {
            if (paused) FlushSave();  // the headset pauses or quits without a clean OnDisable
        }

        void OnSettingsChanged()
        {
            if (!saving) dirty = true;  // somebody else changed them (a reload, a reset): show the real values
        }

        void Update()
        {
            if (saveAt >= 0f && Time.unscaledTime >= saveAt) FlushSave();
            if (dirty && panel != null) Rebuild();
        }

        /// <summary>Goes to a page (0 to 3) and rebuilds.</summary>
        public void SetPage(int value)
        {
            value = Mathf.Clamp(value, 0, PageCount - 1);
            if (value == page) return;
            page = value;
            Rebuild();
        }

        // Applies an edit to the settings and saves it at once. Our own save does not rebuild the page (a slider mid-drag
        // would lose the finger).
        void Change(Action<OSSettings> edit)
        {
            OSSettings settings = OSSettings.Current;
            edit(settings);
            saving = true;
            try { settings.Save(); }
            finally { saving = false; }
        }

        // A slider edit: the setting changes in memory now (the menu reads it on its next open) and the file is written once,
        // SliderSaveDelay after the last change. A drag changes the value nearly every frame; writing the file each time
        // would be three filesystem calls a frame on the headset's flash storage.
        void ChangeLive(Action<OSSettings> edit)
        {
            edit(OSSettings.Current);
            saveAt = Time.unscaledTime + SliderSaveDelay;
        }

        /// <summary>Writes a waiting slider edit now (a no-op when nothing waits).</summary>
        public void FlushSave()
        {
            if (saveAt < 0f) return;
            saveAt = -1f;
            saving = true;
            try { OSSettings.Current.Save(); }
            finally { saving = false; }
        }

        // ---------------------------------------------------------------- the pages

        void Rebuild()
        {
            FlushSave();
            dirty = false;
            RectTransform body = OSUi.Body(panel);
            if (body == null) return;
            OSUi.Clear(body);
            RectTransform column = OSUi.Column(body, 14f);

            if (page == 0)
            {
                BuildMenuButton(Section(column, "Menu button"));
                BuildVoice(Section(column, "Voice"));
            }
            else if (page == 1)
            {
                BuildWindows(Section(column, "Windows"));
                BuildMenuPosition(Section(column, "Menu position"));
            }
            else if (page == HandsPage)
            {
                BuildHands(Section(column, "Hands"));
            }
            else
            {
                BuildReset(Section(column, "Reset"));
                BuildAbout(Section(column, "About"));
            }

            var spacer = new GameObject("Spacer", typeof(RectTransform));
            spacer.transform.SetParent(column, false);
            spacer.AddComponent<LayoutElement>().flexibleHeight = 1f;
            BuildPager(column);
        }

        // A section that keeps its own height: the kit's controls report flexible height, which would stretch the sections apart.
        static RectTransform Section(RectTransform column, string heading)
        {
            RectTransform inner = OSUi.Section(column, heading);
            LayoutElement outer = inner.parent.GetComponent<LayoutElement>();
            if (outer != null) outer.flexibleHeight = 0f;
            return inner;
        }

        void BuildPager(RectTransform column)
        {
            RectTransform row = OSUi.Row(column, OSUi.ButtonHeight, 16f);
            row.name = "Pager";
            row.GetComponent<LayoutElement>().flexibleHeight = 0f;
            row.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
            Button back = OSUi.Button(row, null, () => SetPage(page - 1), OSButtonStyle.Borderless, "chevron_left");
            TMP_Text label = OSUi.Text(row, (page + 1) + " of " + PageCount, OSUi.BodyText, true);
            label.alignment = TextAlignmentOptions.Center;
            Button next = OSUi.Button(row, null, () => SetPage(page + 1), OSButtonStyle.Borderless, "chevron_right");
            if (back != null) back.interactable = page > 0;
            if (next != null) next.interactable = page < PageCount - 1;
        }

        // ---------------------------------------------------------------- page 1: menu button, voice

        void BuildMenuButton(RectTransform section)
        {
            OSSettings s = OSSettings.Current;
            OSUi.Toggle(section, "Table button", s.tableButton, on => Change(v => v.tableButton = on));
            OSUi.Toggle(section, "Watch turn", s.watchTurn, on => Change(v => v.watchTurn = on));
            OSUi.Toggle(section, "Corner button", s.cornerButton, on => Change(v => v.cornerButton = on));
            Choice(section, "Corner side", "Left", "Right", s.cornerOnLeft, left => Change(v => v.cornerOnLeft = left));
        }

        // Two buttons in a row, the chosen one lit. Choosing rebuilds the page so the lit one moves.
        void Choice(RectTransform section, string label, string first, string second, bool firstChosen, Action<bool> onChosen)
        {
            RectTransform row = OSUi.Row(section, OSUi.ButtonHeight, 16f);
            row.name = "Choice " + label;
            OSUi.Text(row, label, OSUi.BodyText);
            OSUi.Button(row, first, () => Choose(firstChosen, true, onChosen), firstChosen ? OSButtonStyle.Primary : OSButtonStyle.Secondary);
            OSUi.Button(row, second, () => Choose(!firstChosen, false, onChosen), firstChosen ? OSButtonStyle.Secondary : OSButtonStyle.Primary);
        }

        void Choose(bool alreadyChosen, bool firstWanted, Action<bool> onChosen)
        {
            if (alreadyChosen) return;
            onChosen(firstWanted);
            Rebuild();
        }

        // The app has no hands-free switch to flip (the only voice flag is an editor preference), so this says what is true.
        void BuildVoice(RectTransform section)
        {
            RectTransform row = OSUi.Row(section, 60f, 16f);
            row.name = "Voice state";
            OSUi.Text(row, "Hands-free", OSUi.BodyText);
            OSUi.Text(row, VoiceSentence(), NoteSize, true);
        }

        /// <summary>The one-sentence state of voice, in plain words.</summary>
        public static string VoiceSentence()
        {
            if (!(ForgeNet.LocalPeer is AlbieVoice))
                return "Voice is off in this session, so Albie is not listening.";
            PushToTalk talk = FindAnyObjectByType<PushToTalk>();
            if (talk != null && !talk.CanListenHandsFree)
                return "Voice is on, but there is no microphone to listen with here.";
            return "On. Albie listens after you call him (Call Albie in the menu's Tools, or F) or hold out your hand. This can't be turned off here yet.";
        }

        // ---------------------------------------------------------------- page 2: windows, menu position

        void BuildWindows(RectTransform section)
        {
            Workspace workspace = OSShell.Current != null && OSShell.Current.workspace != null ? OSShell.Current.workspace : Workspace.Current;
            bool flat = workspace != null ? workspace.layout == WorkspaceLayout.Flat : OSSettings.Current.flatLayout;
            Choice(section, "Layout", "Curved", "Flat", !flat, curved =>
            {
                Change(v => v.flatLayout = !curved);
                Workspace ws = OSShell.Current != null && OSShell.Current.workspace != null ? OSShell.Current.workspace : Workspace.Current;
                WorkspaceLayout want = curved ? WorkspaceLayout.Curved : WorkspaceLayout.Flat;
                if (ws != null && ws.layout != want) ws.SetLayout(want);  // OSModes does the same on the change; harmless twice
            });

            GesturePanel display = FindAnyObjectByType<GesturePanel>(FindObjectsInactive.Include);
            // The real state: a saved flag with no panel made yet (or one that was never shown) is not "on" until something is on screen.
            bool shown = display != null && display.gameObject.activeInHierarchy;
            OSUi.Toggle(section, "Gesture display", shown, SetGestureDisplay);

            // Where room courses may go: the whole room, an area drawn on the floor, or the Quest boundary (PlayAreaEditor).
            RectTransform area = OSUi.Row(section, OSUi.ButtonHeight, 16f);
            area.name = "Course play area";
            OSUi.Text(area, "Course play area", OSUi.BodyText);
            OSUi.Button(area, "Set play area", () => PlayAreaEditor.Open(), OSButtonStyle.Secondary);
        }

        static void SetGestureDisplay(bool on)
        {
            OSSettings settings = OSSettings.Current;
            settings.gestureDisplay = on;
            settings.Save();
            GestureEngine engine = GestureEngine.Current;
            if (engine == null || engine.Actions == null) return;
            engine.Actions.ShowDisplay(on);  // the same path as the display.toggle gesture
            OSEntry entry = OSRegistry.Find(OSTab.Tools, "gestures");
            if (on && entry != null && entry.isOpen != null) entry.isOpen();  // hands it to the window manager, as the Tools tile does
        }

        void BuildMenuPosition(RectTransform section)
        {
            OSSettings s = OSSettings.Current;
            Slider distance = OSUi.Slider(section, "Distance", DistanceMin, DistanceMax, s.menuDistance, v =>
            {
                float metres = Mathf.Round(v * 100f) / 100f;
                if (Mathf.Approximately(OSSettings.Current.menuDistance, metres)) return;  // same step as before: nothing to do
                ChangeLive(x => x.menuDistance = metres);
            });
            Slider drop = OSUi.Slider(section, "Height", DropMin, DropMax, s.menuDropDegrees, v =>
            {
                float degrees = Mathf.Round(v);
                if (Mathf.Approximately(OSSettings.Current.menuDropDegrees, degrees)) return;
                ChangeLive(x => x.menuDropDegrees = degrees);
            });
            // The kit's readout shows the bare number; these show it with its unit.
            WireReadout(distance, v => (Mathf.Round(v * 100f) / 100f).ToString("0.00") + " m");
            WireReadout(drop, v => Mathf.Round(v).ToString("0") + " degrees down");
            OSUi.Text(section, "The menu moves to the new place the next time it opens.", NoteSize, true);
        }

        // The text at the right of the slider's label row: written after the kit's own listener (added first) has run.
        static void WireReadout(Slider slider, Func<float, string> format)
        {
            if (slider == null) return;
            TMP_Text readout = ReadoutOf(slider);
            if (readout == null) return;
            readout.text = format(slider.value);
            readout.GetComponent<LayoutElement>().minWidth = 150f;
            slider.onValueChanged.AddListener(v => readout.text = format(v));
        }

        // OSUi.Slider puts a Column (name "Slider ...") around a label Row and the slider; the readout is the Row's last text.
        static TMP_Text ReadoutOf(Slider slider)
        {
            Transform column = slider.transform.parent;
            if (column == null || column.childCount == 0) return null;
            TMP_Text[] texts = column.GetChild(0).GetComponentsInChildren<TMP_Text>(true);
            return texts.Length > 0 ? texts[texts.Length - 1] : null;
        }

        // ---------------------------------------------------------------- page 3: hands

        /// <summary>The page with the Hands setting (0-based).</summary>
        public const int HandsPage = 2;

        static readonly HandSplit[] HandChoices = { HandSplit.LeftAll, HandSplit.RightAll, HandSplit.Off };
        static readonly string[] HandLabels = { "Left hand", "Right hand", "Off" };

        // Which hand hides and shows every window; the other hand works on one window.
        void BuildHands(RectTransform section)
        {
            GestureEngine engine = GestureEngine.Current;
            GestureLibrary library = engine != null ? engine.Library : null;
            WatchGestures(library);
            if (library == null)
            {
                OSUi.Text(section, "Hand gestures aren't running in this scene.", NoteSize, true);
                return;
            }
            HandSplit split = library.Split;

            RectTransform row = OSUi.Row(section, OSUi.ButtonHeight, 16f);
            row.name = "Choice All windows";
            OSUi.Text(row, "All windows", OSUi.BodyText);
            for (int i = 0; i < HandChoices.Length; i++)
            {
                HandSplit choice = HandChoices[i];
                OSUi.Button(row, HandLabels[i], () =>
                {
                    if (library.Split == choice) return;
                    library.Split = choice;  // saved with the gestures; the gesture display and Albie see it too
                    Rebuild();
                }, choice == split ? OSButtonStyle.Primary : OSButtonStyle.Secondary);
            }
            OSUi.Text(section, HandsSentence(split), NoteSize, true);
        }

        /// <summary>What the Hands setting means, in a sentence.</summary>
        public static string HandsSentence(HandSplit split)
        {
            if (split == HandSplit.Off)
                return "No hand hides every window. Either hand closes the window you look at, reopens it, and flicks the one you point at.";
            string all = split == HandSplit.LeftAll ? "left" : "right", one = split == HandSplit.LeftAll ? "right" : "left";
            return $"Close your {all} hand to hide every window, and open it to show them again. Your {one} hand closes the window you look at, reopens it, and flicks the one you point at.";
        }

        // ---------------------------------------------------------------- page 4: reset, about

        void BuildReset(RectTransform section)
        {
            ResetRow(section, "Window positions", "Bring windows here",
                "Every open window comes back in front of you. Nothing is closed.",
                "Bring them here", () =>
                {
                    OSShell shell = OSShell.Current;
                    if (shell != null) shell.Windows.BringHere();
                    resetNote = "Windows brought back in front of you.";
                });
            ResetRow(section, "Artifact permissions", "Forget permissions",
                "Forget every permission you gave artifacts? Each one asks again the next time it opens.",
                "Forget them", () =>
                {
                    ArtifactApprovals.Forget();
                    resetNote = "Artifact permissions cleared. Each one will ask again.";
                });
            if (resetNote.Length > 0) OSUi.Text(section, resetNote, NoteSize, true);
        }

        void ResetRow(RectTransform section, string label, string button, string question, string confirm, Action action)
        {
            RectTransform row = OSUi.Row(section, OSUi.ButtonHeight, 16f);
            row.name = "Reset " + label;
            OSUi.Text(row, label, OSUi.BodyText);
            OSUi.Button(row, button, () =>
            {
                OSUi.Confirm(panel.transform, question, confirm, () =>
                {
                    action();
                    Rebuild();
                });
            }, OSButtonStyle.Secondary);
        }

        void BuildAbout(RectTransform section)
        {
            OSUi.Text(section, "Albie, version " + Application.version, OSUi.BodyText);
            OSUi.Text(section, "Biology models in the artifacts (cell, plant cell, heart, brain, eye, DNA): Grab3D, public domain (CC0).", NoteSize, true);
            OSUi.Text(section, "Albie the gator: Quirky Series, used under its asset licence. Pet font: Fredoka, SIL Open Font License.", NoteSize, true);
            OSUi.Text(section, "Menu and controls: a third-party UI kit, used under its licence.", NoteSize, true);
        }
    }
}
