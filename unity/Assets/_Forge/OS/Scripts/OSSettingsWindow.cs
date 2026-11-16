using System;
using Forge.Artifacts;
using Forge.Pet;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// The Settings window (window id "settings", opened by the Settings button in the menu strip). Three pages, each a few
    /// sections of rows (label left, control right), paged with a back and next button the way the menu pages:
    /// 1 Menu button and Voice, 2 Windows and Menu position, 3 Reset and About. Every change is written through
    /// <see cref="OSSettings"/> at once and takes effect without a restart: the table button, the corner button and the
    /// wrist turn read the setting each frame, the layout is applied to the workspace now, the menu distance and height are
    /// read the next time the menu opens. The two resets ask first and do nothing until the confirm tap. Voice is shown as a
    /// plain sentence, not a switch, because the app has no setting that turns hands-free off.
    /// </summary>
    public sealed class OSSettingsWindow : MonoBehaviour
    {
        public const string WindowId = "settings";
        public const int PageCount = 3;

        /// <summary>Ranges the two sliders offer (inside what <see cref="OSSettings"/> allows).</summary>
        public const float DistanceMin = 0.35f, DistanceMax = 0.7f, DropMin = 0f, DropMax = 30f;

        static readonly Vector2 SizeMeters = new Vector2(0.74f, 0.62f);
        const float NoteSize = OSUi.SmallText;

        ForgePanel panel;
        int page;
        bool dirty, saving;
        string resetNote = "";

        /// <summary>The page showing, 0 to 2.</summary>
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

        void OnDisable() => OSSettings.Changed -= OnSettingsChanged;

        void OnSettingsChanged()
        {
            if (!saving) dirty = true;  // somebody else changed them (a reload, a reset): show the real values
        }

        void Update()
        {
            if (dirty && panel != null) Rebuild();
        }

        /// <summary>Goes to a page (0 to 2) and rebuilds.</summary>
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

        // ---------------------------------------------------------------- the pages

        void Rebuild()
        {
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
            return "On. Albie listens after you whistle or hold out your hand. This can't be turned off here yet.";
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
                Change(x => x.menuDistance = metres);
            });
            Slider drop = OSUi.Slider(section, "Height", DropMin, DropMax, s.menuDropDegrees, v =>
            {
                float degrees = Mathf.Round(v);
                Change(x => x.menuDropDegrees = degrees);
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

        // ---------------------------------------------------------------- page 3: reset, about

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
            OSUi.Text(section, "Menu and controls: a third-party UI kit, used under its licence. Full notice in OS/THIRD_PARTY_NOTICES.md.", NoteSize, true);
        }
    }
}
