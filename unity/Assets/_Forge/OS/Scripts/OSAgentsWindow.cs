using System.Collections.Generic;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// The Agents window (window id "agents", opened by the menu's Agents button): Albie as the lead at the top, then one row
    /// per crew member with its type, what it is doing (its real task, or its state, or "Idle") and whether it is walking or
    /// home. Each crew row has Bring home / Send out (one button that flips) and Dismiss, which asks first and stops the
    /// agent's task. Above the rows are Bring all home and Send all out. Bring home keeps the agent working: it only stops it
    /// wandering (<see cref="OSAgentHome"/>). Under the rows a small row of type buttons makes a new agent through the
    /// <c>spawn_agent</c> tool, the same call the voice path uses, which is not gated on the code mode (the crew egg and the
    /// N and X keys still are). Rows beyond four are paged like the menu, never scrolled. The window reads the crew twice a
    /// second and when <see cref="AlbieCrew.Changed"/> says so, and rebuilds only when something it shows has changed.
    /// </summary>
    public sealed class OSAgentsWindow : MonoBehaviour
    {
        /// <summary>Window id; opening it again brings the existing window forward.</summary>
        public const string WindowId = "agents";

        static readonly Vector2 SizeMeters = new Vector2(0.96f, 0.70f);
        const float PollSeconds = 0.5f;
        const int RowsPerPage = 4, TypeButtons = 5;
        const float BodySize = OSUi.BodyText, NameSize = OSUi.BodyText, SmallSize = OSUi.SmallText;
        const float RowHeight = 64f, NameWidth = 450f, StateWidth = 120f, ButtonsWidth = 300f, FlipWidth = 170f, DismissWidth = 110f;

        const string WalkingWord = "Walking", HomeWord = "Home", HiddenWord = "Home, hidden", WithYouWord = "With you", FollowingWord = "Following you";

        struct Line
        {
            public string id, name, role, doing, state;
            public bool home, hidden;
        }

        ForgePanel panel;
        RectTransform column;
        AlbieCrew bound;
        bool dirty;
        float nextPoll;
        int page, typeOffset;
        string status = "";

        // What the window shows now, and what it shows from the last build: a rebuild happens only when they differ.
        readonly List<Line> current = new List<Line>(AlbieAgentTypes.All.Length);
        readonly List<Line> shown = new List<Line>(AlbieAgentTypes.All.Length);
        readonly List<AlbieAgentType> freeTypes = new List<AlbieAgentType>(AlbieAgentTypes.All.Length);
        Line leadNow, leadShown;
        bool hasLeadNow, hasLeadShown, crewNow, crewShown;
        int freeShown = -1;
        int pageShown = -1, typeOffsetShown = -1;
        string statusShown;

        /// <summary>The last line the status area showed.</summary>
        public string StatusLine => status;
        /// <summary>How many crew rows the window shows (all pages).</summary>
        public int RowCount => shown.Count;
        /// <summary>How many pages the crew rows take.</summary>
        public int PageCount => Mathf.Max(1, (shown.Count + RowsPerPage - 1) / RowsPerPage);
        /// <summary>The page showing, from 0.</summary>
        public int Page => page;

        /// <summary>Tells the shell what the menu's Agents button opens. The app does this at start; a check can call it again.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Register() => OSShell.RegisterSystem(WindowId, () => Open());

        /// <summary>Opens the Agents window, or brings the open one forward and rereads it. Null when this scene has no shell.</summary>
        public static OSWindow Open()
        {
            OSShell shell = OSShell.Current;
            if (shell == null)
            {
                Debug.Log("[OS] The Agents window needs the Albie menu, and this scene has none.");
                return null;
            }
            OSWindow window = shell.Windows.Open(WindowId, "Agents", SizeMeters, Build, "AGENTS");
            if (window != null)
            {
                var view = window.GetComponent<OSAgentsWindow>();
                if (view != null) view.dirty = true;
            }
            return window;
        }

        static void Build(ForgePanel forgePanel)
        {
            var view = forgePanel.gameObject.AddComponent<OSAgentsWindow>();
            view.Init(forgePanel);
        }

        void Init(ForgePanel owner)
        {
            panel = owner;
            RectTransform body = OSUi.Body(panel);
            if (body == null) return;
            column = OSUi.Column(body, 14f);
            BindCrew();
            Rebuild();
        }

        void OnEnable()
        {
            if (column != null) dirty = true;  // back from being put away: events were missed meanwhile
        }

        void OnDestroy()
        {
            if (bound != null) bound.Changed -= OnCrewChanged;
            bound = null;
        }

        void OnCrewChanged() => dirty = true;

        // The crew is made after the scene loads, so the window follows whichever AlbieCrew exists.
        void BindCrew()
        {
            AlbieCrew now = AlbieCrew.Instance;
            if (now == bound) return;
            if (bound != null) bound.Changed -= OnCrewChanged;
            bound = now;
            if (bound != null) bound.Changed += OnCrewChanged;
            dirty = true;
        }

        void Update()
        {
            if (panel == null || column == null) return;
            if (dirty)
            {
                Rebuild();
                return;
            }
            float now = Time.unscaledTime;
            if (now < nextPoll) return;
            nextPoll = now + PollSeconds;
            BindCrew();
            Collect();
            if (!SameAsShown()) Rebuild();
        }

        // ---------------------------------------------------------------- reading the crew

        // The state words come from constants and the agent's own text, so a poll that finds nothing new builds no strings.
        // The one allocation is crew_status's small answer, twice a second while the window is open.
        void Collect()
        {
            current.Clear();
            freeTypes.Clear();
            hasLeadNow = false;
            AlbieCrew crew = AlbieCrew.Instance;
            crewNow = crew != null;
            if (crew == null) return;

            JArray tasks = null;
            if (AlbieTools.Has("crew_status"))
            {
                JObject answer = AlbieTools.Call("crew_status", new JObject());
                tasks = answer != null ? answer["crew"] as JArray : null;
            }

            AlbieController lead = crew.Lead;
            if (lead != null)
            {
                AlbieAgentBridge bridge = lead.GetComponent<AlbieAgentBridge>();
                leadNow = new Line
                {
                    id = "lead",
                    name = PetConfig.DisplayName,
                    role = "leads the crew",
                    doing = bridge != null && bridge.TaskInProgress ? "Working on a task" : StateWord(lead),
                    state = lead.State == AlbieState.Following ? FollowingWord : (lead.State == AlbieState.Wandering ? WalkingWord : WithYouWord),
                };
                hasLeadNow = true;
            }

            IReadOnlyList<AlbieCrewMember> members = crew.Members;
            for (int i = 0; i < members.Count; i++)
            {
                AlbieCrewMember m = members[i];
                if (m == null || m.Type == null || m.Leaving) continue;
                current.Add(new Line
                {
                    id = m.PetId,
                    name = m.Type.Name,
                    role = m.Type.Role,
                    doing = Doing(m, TaskOf(tasks, m.PetId)),
                    state = m.Home ? (m.BodyHidden ? HiddenWord : HomeWord) : WalkingWord,
                    home = m.Home,
                    hidden = m.BodyHidden,
                });
            }

            for (int i = 0; i < AlbieAgentTypes.All.Length; i++)
                if (!crew.IsHatched(AlbieAgentTypes.All[i])) freeTypes.Add(AlbieAgentTypes.All[i]);
        }

        static string TaskOf(JArray tasks, string petId)
        {
            if (tasks == null) return null;
            for (int i = 0; i < tasks.Count; i++)
            {
                var item = tasks[i] as JObject;
                if (item == null || (string)item["agent"] != petId) continue;
                return (string)item["task"];
            }
            return null;
        }

        // Its real task text if it has one, "Working on a task" for a task the hub gave it, else what its body is doing.
        static string Doing(AlbieCrewMember m, string task)
        {
            if (!string.IsNullOrEmpty(task)) return task;
            AlbieAgentBridge bridge = m.Bridge;
            if (bridge != null && bridge.TaskInProgress) return "Working on a task";
            return m.Controller != null ? StateWord(m.Controller) : "Idle";
        }

        static string StateWord(AlbieController c)
        {
            switch (c.State)
            {
                case AlbieState.Intro: return "Arriving";
                case AlbieState.Listening: return "Listening";
                case AlbieState.Thinking: return "Thinking";
                case AlbieState.Working: return "Working";
                case AlbieState.Answering: return "Answering";
                case AlbieState.TaskComplete: return "Just finished";
                case AlbieState.Error: return "Stuck";
                default: return "Idle";
            }
        }

        bool SameAsShown()
        {
            if (crewNow != crewShown || hasLeadNow != hasLeadShown || freeTypes.Count != freeShown) return false;
            if (page != pageShown || typeOffset != typeOffsetShown || status != statusShown) return false;
            if (hasLeadNow && !Same(leadNow, leadShown)) return false;
            if (current.Count != shown.Count) return false;
            for (int i = 0; i < current.Count; i++)
                if (!Same(current[i], shown[i])) return false;
            return true;
        }

        static bool Same(Line a, Line b) =>
            a.id == b.id && a.doing == b.doing && a.state == b.state && a.home == b.home && a.hidden == b.hidden;

        // ---------------------------------------------------------------- building

        void Rebuild()
        {
            dirty = false;
            nextPoll = Time.unscaledTime + PollSeconds;
            BindCrew();
            Collect();
            dirty = false;

            int pages = Mathf.Max(1, (current.Count + RowsPerPage - 1) / RowsPerPage);
            page = Mathf.Clamp(page, 0, pages - 1);
            if (freeTypes.Count > 0) typeOffset = ((typeOffset % freeTypes.Count) + freeTypes.Count) % freeTypes.Count; else typeOffset = 0;

            shown.Clear();
            shown.AddRange(current);
            leadShown = leadNow;
            hasLeadShown = hasLeadNow;
            crewShown = crewNow;
            freeShown = freeTypes.Count;
            pageShown = page;
            typeOffsetShown = typeOffset;
            statusShown = status;

            OSUi.Clear(column);
            RectTransform workflowActions = OSUi.Row(column, OSUi.ButtonHeight, 19f);
            workflowActions.GetComponent<LayoutElement>().flexibleHeight = 0f;
            OSUi.Button(workflowActions, "Project workflow", () => OSWorkflowWindow.Open(), OSButtonStyle.Primary);
            OSUi.Button(workflowActions, "Agent office", () => OSAgentOffice.Open());

            if (!crewNow)
            {
                Label(column, "The crew isn't available in this scene.", BodySize, true);
                return;
            }

            bool anyOut = false, anyHome = false;
            for (int i = 0; i < current.Count; i++)
            {
                if (current[i].home) anyHome = true; else anyOut = true;
            }
            panel.Subtitle = current.Count == 0 ? "no agents yet" : current.Count + (current.Count == 1 ? " agent" : " agents") + (anyHome ? ", " + HomeCountIn(current) + " home" : "");

            RectTransform actions = OSUi.Row(column, OSUi.ButtonHeight, 19f);
            actions.GetComponent<LayoutElement>().flexibleHeight = 0f;
            Button bringAll = OSUi.Button(actions, "Bring all home", BringAllHome, OSButtonStyle.Secondary, "home");
            if (bringAll != null) bringAll.interactable = anyOut;
            Button sendAll = OSUi.Button(actions, "Send all out", SendAllOut, OSButtonStyle.Secondary);
            if (sendAll != null) sendAll.interactable = anyHome;

            if (hasLeadNow)
            {
                RectTransform lead = OSUi.Section(column, "Lead");
                Row(lead, leadNow, false);
            }

            RectTransform crew = OSUi.Section(column, "Crew");
            if (current.Count == 0)
            {
                Label(crew, "No agents yet. Tap a type below to hatch one, or ask Albie to hatch a builder.", BodySize, true);
            }
            else
            {
                int first = page * RowsPerPage;
                int last = Mathf.Min(current.Count, first + RowsPerPage);
                for (int i = first; i < last; i++) Row(crew, current[i], true);
                if (pages > 1) Pager(crew, pages);
                Label(crew, "Bring home keeps an agent working. Dismiss sends it away for good and stops its task.", SmallSize, true);
            }

            RectTransform make = OSUi.Section(column, "Make an agent");
            if (freeTypes.Count == 0)
            {
                Label(make, "Every kind of agent is already out.", BodySize, true);
            }
            else
            {
                RectTransform types = OSUi.Row(make, OSUi.ButtonHeight, 16f);
                int count = Mathf.Min(TypeButtons, freeTypes.Count);
                for (int i = 0; i < count; i++)
                {
                    AlbieAgentType type = freeTypes[(typeOffset + i) % freeTypes.Count];
                    string typeId = type.Id;
                    OSUi.Button(types, type.Name, () => Spawn(typeId), OSButtonStyle.Secondary);
                }
                if (freeTypes.Count > TypeButtons)
                {
                    Button more = OSUi.Button(types, null, () => { typeOffset += TypeButtons; dirty = true; }, OSButtonStyle.Borderless, "chevron_right");
                    if (more != null) OSUi.Size(more.transform, 56f, 56f);
                }
            }

            if (status.Length > 0) Label(column, status, BodySize, false);
        }

        static int HomeCountIn(List<Line> lines)
        {
            int n = 0;
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].home) n++;
            return n;
        }

        // One agent: name and what it is for, what it is doing, walking or home, and (crew only) the two buttons.
        void Row(RectTransform parent, Line line, bool buttons)
        {
            RectTransform row = OSUi.Row(parent, RowHeight, 16f);
            row.name = "Agent " + line.id;

            RectTransform names = OSUi.Column(row, 2f);
            names.anchorMin = names.anchorMax = new Vector2(0f, 1f);
            names.pivot = new Vector2(0f, 1f);
            OSUi.Size(names, NameWidth, 52f);
            TMP_Text title = Label(names, line.name, NameSize, false);
            title.fontStyle = FontStyles.Bold;
            title.textWrappingMode = TextWrappingModes.NoWrap;
            title.overflowMode = TextOverflowModes.Ellipsis;
            TMP_Text role = Label(names, line.role, SmallSize, true);
            role.textWrappingMode = TextWrappingModes.NoWrap;
            role.overflowMode = TextOverflowModes.Ellipsis;

            TMP_Text doing = Label(row, line.doing, BodySize, string.IsNullOrEmpty(line.doing) || line.doing == "Idle");
            doing.textWrappingMode = TextWrappingModes.NoWrap;
            doing.overflowMode = TextOverflowModes.Ellipsis;
            var doingLayout = doing.GetComponent<LayoutElement>();
            doingLayout.minWidth = 200f;
            doingLayout.preferredWidth = 380f;
            doingLayout.flexibleWidth = 1f;

            RectTransform state = OSUi.Row(row, 36f, 8f);
            OSUi.Size(state, StateWidth, 36f);
            if (line.home) Glyph(state, "home", 28f);
            TMP_Text stateText = Label(state, line.state, BodySize, !line.home);
            stateText.textWrappingMode = TextWrappingModes.NoWrap;
            stateText.overflowMode = TextOverflowModes.Ellipsis;

            RectTransform cell = OSUi.Row(row, OSUi.ButtonHeight, 16f);
            OSUi.Size(cell, ButtonsWidth, OSUi.ButtonHeight);
            if (!buttons) return;
            string id = line.id;
            Button flip = line.home
                ? OSUi.Button(cell, "Send out", () => Flip(id))
                : OSUi.Button(cell, "Bring home", () => Flip(id), OSButtonStyle.Secondary, "home");
            if (flip != null) OSUi.Size(flip.transform, FlipWidth, OSUi.ButtonHeight);
            Button dismiss = OSUi.Button(cell, "Dismiss", () => AskDismiss(id), OSButtonStyle.Destructive);
            if (dismiss != null) OSUi.Size(dismiss.transform, DismissWidth, OSUi.ButtonHeight);
        }

        // < 1 / 2 >, only when there is more than a page (the menu's paging, in a row).
        void Pager(RectTransform parent, int pages)
        {
            RectTransform row = OSUi.Row(parent, 56f, 12f);
            Button previous = OSUi.Button(row, null, () => SetPage(page - 1), OSButtonStyle.Borderless, "chevron_left");
            if (previous != null)
            {
                OSUi.Size(previous.transform, 56f, 56f);
                previous.interactable = page > 0;
            }
            TMP_Text label = Label(row, (page + 1) + " / " + pages, BodySize, true);
            label.alignment = TextAlignmentOptions.Center;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            var labelLayout = label.GetComponent<LayoutElement>();
            labelLayout.flexibleWidth = 0f;
            labelLayout.minWidth = labelLayout.preferredWidth = 76f;
            Button next = OSUi.Button(row, null, () => SetPage(page + 1), OSButtonStyle.Borderless, "chevron_right");
            if (next != null)
            {
                OSUi.Size(next.transform, 56f, 56f);
                next.interactable = page < pages - 1;
            }
        }

        // A kit icon as a small picture in a row.
        static void Glyph(Transform parent, string icon, float size)
        {
            Sprite sprite = OSUi.Icon(icon);
            if (sprite == null) return;
            var go = new GameObject("Icon " + icon, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = true;
            image.raycastTarget = false;
            OSUi.Size(go.transform, size, size);
        }

        // Plain text: no rich-text parsing (a task is the person's own words), wrapped.
        static TMP_Text Label(Transform parent, string text, float size, bool secondary)
        {
            TMP_Text label = OSUi.Text(parent, text, size, secondary);
            if (label == null) return null;
            label.richText = false;
            label.textWrappingMode = TextWrappingModes.Normal;
            return label;
        }

        void SetPage(int value)
        {
            int pages = PageCount;
            value = Mathf.Clamp(value, 0, pages - 1);
            if (value == page) return;
            page = value;
            dirty = true;
        }

        // ---------------------------------------------------------------- actions (the buttons call these)

        static AlbieCrewMember Member(string id)
        {
            AlbieCrew crew = AlbieCrew.Instance;
            AlbieAgentType type = AlbieAgentTypes.Find(id);
            return crew != null && type != null ? crew.Find(type) : null;
        }

        /// <summary>Bring home or Send out, whichever the agent's row offers: flips the agent between walking and home.</summary>
        public void Flip(string id)
        {
            AlbieCrewMember m = Member(id);
            if (m == null)
            {
                SetStatus("That agent has left.");
                return;
            }
            string name = m.Type.Name;
            if (m.Home)
            {
                OSAgentHome.SendOut(m);
                SetStatus(name + " is out again.");
            }
            else
            {
                bool onPad = OSAgentHome.BringHome(m);
                bool working = m.Bridge != null && m.Bridge.Busy;
                SetStatus(name + (onPad ? " is home on its pad." : " is home, out of sight.") + (working ? " Its task keeps running." : ""));
            }
        }

        /// <summary>Brings every agent that is out home.</summary>
        public void BringAllHome()
        {
            int n = OSAgentHome.BringAllHome();
            SetStatus(n == 0 ? "Everyone is home already." : n == 1 ? "One agent is home." : n + " agents are home.");
        }

        /// <summary>Sends every agent that is home out again.</summary>
        public void SendAllOut()
        {
            int n = OSAgentHome.SendAllOut();
            SetStatus(n == 0 ? "Everyone is out already." : n == 1 ? "One agent is out again." : n + " agents are out again.");
        }

        /// <summary>Asks first, then dismisses: the question, then Dismiss or Cancel.</summary>
        public void AskDismiss(string id)
        {
            AlbieCrewMember m = Member(id);
            if (m == null)
            {
                SetStatus("That agent has left.");
                return;
            }
            OSUi.Confirm(panel.transform, "Dismiss " + m.Type.Name + "? It goes away and its task stops.", "Dismiss", () => Dismiss(id));
        }

        /// <summary>Dismisses an agent through the <c>dismiss_agent</c> tool, which also drops its task. The Dismiss confirm calls this.</summary>
        public void Dismiss(string id)
        {
            AlbieAgentType type = AlbieAgentTypes.Find(id);
            string name = type != null ? type.Name : id;
            JObject result = AlbieTools.Call("dismiss_agent", new JObject { ["agent"] = id });
            string error = result != null ? (string)result["error"] : "That didn't work.";
            if (error != null)
            {
                SetStatus(OSProjects.Plain(error));
                OSSounds.Error();
                return;
            }
            bool stopped = result != null && (bool?)result["stopped_task"] == true;
            SetStatus(name + " is dismissed." + (stopped ? " Its task stopped." : ""));
        }

        /// <summary>Hatches an agent of this type through the <c>spawn_agent</c> tool (the voice path's call, not gated on the code mode).</summary>
        public void Spawn(string typeId)
        {
            AlbieAgentType type = AlbieAgentTypes.Find(typeId);
            string name = type != null ? type.Name : typeId;
            JObject result = AlbieTools.Call("spawn_agent", new JObject { ["type"] = typeId });
            string error = result != null ? (string)result["error"] : "That didn't work.";
            if (error != null)
            {
                SetStatus(OSProjects.Plain(error));
                OSSounds.Error();
                return;
            }
            SetStatus(name + " hatched.");
        }

        void SetStatus(string text)
        {
            status = text ?? "";
            dirty = true;
        }
    }
}
