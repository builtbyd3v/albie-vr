using System;
using System.IO;
using System.Text;
using Forge.Notebook;
using Forge.Pet;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>
    /// Build something: Albie comes to the user and plans with them, one question at a time, then shows a plan card to
    /// confirm. It registers the "build" page the menu strip opens (<see cref="OSShell.RegisterSystem"/>). The questions
    /// come from <see cref="OSPlanner"/>; each is a card (an OS window, id "build") with two to four tappable answers, and
    /// "Something else" (say it in your own words) only while the voice is live. The plan card has Start (primary), Change
    /// (back to the first question, keeping nothing) and Cancel. NOTHING is created or written before Start: no
    /// notebook call, no folder. Cancel and closing the window leave no files. Start makes the truck's project
    /// (<c>proj_truck</c>) or a new one (<c>proj_my_drone</c> and so on), saves the plan with
    /// <see cref="NotebookStore.RevisePlan"/> (the answers go in as its reason, and the parts and answers are added to the
    /// saved plan), opens the Project window and offers its first step there.
    /// With the voice live (<see cref="VoiceLive"/>) Albie says each question and a spoken answer that matches a choice
    /// counts as a tap: <see cref="Choose"/> does that, and the os_answer tool (OSBuildTools) calls it.
    /// </summary>
    public sealed class OSBuildFlow : MonoBehaviour
    {
        /// <summary>The id of the card's window, and of the system page the strip's Build something button opens.</summary>
        public const string WindowId = "build";
        public const string SystemId = "build";

        static readonly Vector2 SizeMeters = new Vector2(0.66f, 0.56f);
        const float AnswerWidth = 520f;                       // OS units: every answer button is this wide, so the column reads as one list
        const float LeadSize = 26f, LineSize = 20f, NoteSize = 16f;

        /// <summary>For tests: true or false stands in for <see cref="VoiceLive"/>; null (the default) asks the voice.</summary>
        public static bool? VoiceLiveOverride;

        /// <summary>The flow that is on screen, or null.</summary>
        public static OSBuildFlow Current { get; private set; }
        /// <summary>The project the last Start made or opened, and the plan version it wrote (null and 0 before any Start).</summary>
        public static string LastProjectId { get; private set; }
        public static int LastPlanVersion { get; private set; }
        /// <summary>Where the last step left the flow: "question 2", "plan", "started", "changed" or "cancelled".</summary>
        public static string LastStep { get; private set; } = "";
        /// <summary>Why the last <see cref="Choose"/> returned false, as a sentence; null when it worked.</summary>
        public static string ChooseProblem { get; private set; }

        /// <summary>
        /// True when Albie's own voice is running: the on-device voice is installed and connected as the local peer
        /// (the way AlbieTips decides to speak). There is no finer signal: the voice's socket is private and opens and
        /// closes by itself, so "installed" is as close as the code gets to "live".
        /// </summary>
        public static bool VoiceLive => VoiceLiveOverride ?? (ForgeNet.Connected && ForgeNet.LocalPeer is AlbieVoice);

        ForgePanel panel;
        OSWindow window;
        readonly OSPlanner planner = new OSPlanner();
        bool dirty, freeMode;
        string notice = "";

        /// <summary>The conversation so far.</summary>
        public OSPlanner Planner => planner;
        /// <summary>True after "Something else" was tapped: the next spoken words are taken as the answer.</summary>
        public bool FreeMode => freeMode;
        /// <summary>The line under the card's buttons (why Start failed), or "".</summary>
        public string Notice => notice;
        /// <summary>The choices of the current question as one sentence ("RC truck, Drone or Rocket"), or "" on the plan card.</summary>
        public string OptionsLine => planner.Current == null ? "" : Options(planner.Current);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Current = null;
            VoiceLiveOverride = null;
            OpenedForTests = null;
            LastProjectId = null;
            LastPlanVersion = 0;
            LastStep = "";
            ChooseProblem = null;
        }

        /// <summary>Registers the "build" page for the menu strip. The app does this at start; a check can call it again.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Register() => OSShell.RegisterSystem(SystemId, () => Open());

        // ---------------------------------------------------------------- opening

        /// <summary>
        /// Albie comes to the user, and the first question appears. If the card is already open it is brought forward and
        /// the conversation carries on. Null when this scene has no shell (a sandbox).
        /// </summary>
        public static OSWindow Open()
        {
            OSShell shell = OSShell.Current;
            if (shell == null)
            {
                Debug.Log("[OS] Build something needs the Albie menu, and this scene has none.");
                return null;
            }
            bool fresh = !shell.Windows.IsOpen(WindowId);
            OSWindow opened = shell.Windows.Open(WindowId, "Build something", SizeMeters, Build, "BUILD");
            if (opened != null && fresh) AlbieComes();
            return opened;
        }

        static void Build(ForgePanel forgePanel)
        {
            OSBuildFlow flow = forgePanel.gameObject.AddComponent<OSBuildFlow>();
            flow.Init(forgePanel);
        }

        void Init(ForgePanel owner)
        {
            panel = owner;
            window = GetComponent<OSWindow>();
            if (window != null) window.Closed += OnWindowClosed;
            Current = this;
            planner.Reset();
            LastStep = "question 1";
            Render();
            SpeakQuestion();
        }

        void OnWindowClosed()
        {
            if (Current == this) Current = null;
            if (LastStep != "started" && LastStep != "cancelled") LastStep = "cancelled";  // closed with its X
            planner.Reset();  // nothing is kept
            freeMode = false;
        }

        void OnDestroy()
        {
            if (window != null) window.Closed -= OnWindowClosed;
            if (Current == this) Current = null;
        }

        // Albie walks over: the lead gator, off its station and its perch. Nothing happens in a scene with no Albie.
        static void AlbieComes()
        {
            try
            {
                AlbieController albie = null;
                AlbieCrew crew = AlbieCrew.Instance;
                if (crew != null) albie = crew.Lead;
                if (albie == null) albie = UnityEngine.Object.FindAnyObjectByType<AlbieController>();
                if (albie == null) return;
                albie.ReleaseStation();
                if (albie.Perched) albie.LeavePerch(albie.ComeToUser);  // he has to land first
                else albie.ComeToUser();
            }
            catch (Exception e)
            {
                Debug.Log("[OS] Albie couldn't come over: " + e.Message);
            }
        }

        void Update()
        {
            if (dirty) Render();
        }

        // ---------------------------------------------------------------- what the buttons do

        /// <summary>Answer number <paramref name="index"/> (from 0) of the question on the card. False when there is no such answer.</summary>
        public bool Pick(int index)
        {
            if (planner.Done || !planner.Answer(index)) return false;
            AfterAnswer();
            return true;
        }

        /// <summary>The user's own words as the answer to the question on the card.</summary>
        public bool PickFree(string words)
        {
            if (planner.Done || !planner.AnswerFree(words)) return false;
            AfterAnswer();
            return true;
        }

        void AfterAnswer()
        {
            freeMode = false;
            notice = "";
            dirty = true;
            if (planner.Done)
            {
                LastStep = "plan";
                SpeakPlan();
            }
            else
            {
                LastStep = "question " + planner.Number;
                SpeakQuestion();
            }
        }

        /// <summary>The "Something else" row: the next spoken words are taken as the answer. Voice only.</summary>
        public void StartFree()
        {
            if (planner.Done || planner.Asked == 0) return;
            freeMode = true;
            dirty = true;
            Speak("Ask the user to say what they have in mind, in a few words.");
        }

        /// <summary>Back to the first question, keeping nothing.</summary>
        public void Change()
        {
            planner.Reset();
            freeMode = false;
            notice = "";
            dirty = true;
            LastStep = "changed";
            SpeakQuestion();
        }

        /// <summary>Closes the card. Nothing was created, so nothing is left behind.</summary>
        public void Cancel()
        {
            LastStep = "cancelled";
            planner.Reset();
            if (window != null) window.Close();
        }

        /// <summary>
        /// Start: makes (or opens) the project, saves the plan, closes the card and opens the Project window with the first
        /// step offered. False with a plain sentence in <paramref name="error"/> when it can't; then nothing was kept.
        /// </summary>
        public bool StartBuild(out string error)
        {
            error = null;
            OSPlanner.Plan plan = planner.Result;
            if (plan == null)
            {
                error = "Answer the questions first.";
                return false;
            }

            string previous = NotebookStore.IsOpen ? NotebookStore.ProjectId : null;
            string id = plan.IsTruck ? NotebookStore.DefaultProject : NewProjectId(plan);
            // Whether the project's folder is already there, so a failed Start removes only what this Start made.
            string folder = ProjectFolder(id);
            bool existed = Directory.Exists(folder);
            if (!OSProjects.EnsureOpen(id, out error))
            {
                Discard(id, previous, folder, existed);
                notice = error;
                dirty = true;
                return false;
            }
            OpenedForTests?.Invoke(folder);

            int version;
            try
            {
                version = NotebookStore.RevisePlan(plan.reason, plan.goal, plan.test, Milestones(plan));
            }
            catch (Exception e)
            {
                if (e is ArgumentException) error = OSProjects.Plain(e.Message);
                else
                {
                    Debug.LogException(e);
                    error = "The plan couldn't be saved.";
                }
                Discard(id, previous, folder, existed);
                notice = error;
                dirty = true;
                return false;
            }
            SavePlanDetails(plan, version);
            LastProjectId = id;
            LastPlanVersion = version;
            LastStep = "started";

            OSShell shell = OSShell.Current;
            if (window != null) window.Close();  // the card goes first: the Project window takes its place
            OSProjects.OpenProject(id);
            OSWindow projectWindow = shell != null ? shell.Windows.Find(OSProjectWindow.WindowId) : null;
            OSProjectWindow view = projectWindow != null ? projectWindow.GetComponent<OSProjectWindow>() : null;
            if (view != null) view.Suggest(plan.firstStep);
            Speak("Tell the user the plan is saved and the project is open. " + plan.firstStep);
            return true;
        }

        /// <summary>For checks: called with the project's folder right after the project was opened and before the plan is written (a check makes the folder here to see a failed Start remove it).</summary>
        public static Action<string> OpenedForTests;

        static string ProjectFolder(string id) => Path.Combine(NotebookStore.Root ?? NotebookStore.DefaultRoot, "projects", id);

        // A Start that failed keeps nothing: the notebook goes back to the project that was open, and a project folder this Start
        // created (it was not there before) is removed. A folder that was already there is never touched.
        static void Discard(string id, string previous, string folder, bool existed)
        {
            if (previous != null && previous != id) OSProjects.EnsureOpen(previous, out string _);
            if (existed) return;
            try
            {
                NotebookStore.Flush();
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                // Still the open project (the one this Start was for): read the now-empty folder back, so nothing stale is in memory.
                if (previous == null || previous == id) OSProjects.EnsureOpen(previous ?? NotebookStore.DefaultProject, out string _);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.Log("[OS] Couldn't remove the folder of the project that was not started: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- spoken answers

        /// <summary>
        /// A spoken (or typed) reply to the card in front of the user. On a question it picks the matching answer; after
        /// "Something else" it takes any words as the answer; on the plan card "start", "change" and "cancel" do what the
        /// buttons do. True when it was taken; false with <see cref="ChooseProblem"/> saying why.
        /// </summary>
        public static bool Choose(string text)
        {
            ChooseProblem = null;
            OSBuildFlow flow = Current;
            if (flow == null)
            {
                ChooseProblem = "The Build something card isn't open.";
                return false;
            }
            return flow.ChooseText(text);
        }

        bool ChooseText(string text)
        {
            string heard = (text ?? "").Trim();
            if (heard.Length == 0)
            {
                ChooseProblem = "I didn't catch an answer.";
                return false;
            }
            if (planner.Done)
            {
                switch (OSPlanner.ParseCommand(heard))
                {
                    case OSPlanner.Command.Start:
                        if (StartBuild(out string error)) return true;
                        ChooseProblem = error;
                        return false;
                    case OSPlanner.Command.Change:
                        Change();
                        return true;
                    case OSPlanner.Command.Cancel:
                        Cancel();
                        return true;
                    default:
                        ChooseProblem = "The plan is ready. Say start, change or cancel.";
                        return false;
                }
            }

            int index = OSPlanner.Match(planner.Current, heard);
            if (index >= 0) return Pick(index);

            if (VoiceLive && planner.Asked > 0)
            {
                string said = OSPlanner.Normalize(heard);
                if (!freeMode && (said == "something else" || said == "other" || said == "something different"))
                {
                    StartFree();
                    return true;
                }
                if (freeMode && said.Length >= 3) return PickFree(heard);
            }
            ChooseProblem = "That isn't one of the choices. They are: " + OptionsLine + ".";
            return false;
        }

        // ---------------------------------------------------------------- Albie speaks (voice live only)

        void SpeakQuestion()
        {
            OSPlanner.Question q = planner.Current;
            if (q == null || !VoiceLive) return;
            Speak("Ask the user this one question and read out the choices: " + q.text + " The choices are " + Options(q) + ".");
        }

        void SpeakPlan()
        {
            OSPlanner.Plan plan = planner.Result;
            if (plan == null || !VoiceLive) return;
            Speak("Tell the user the plan is ready. The goal: " + plan.goal + " The test: " + plan.test + " Ask if they want to start, change it, or cancel.");
        }

        static void Speak(string text)
        {
            if (!VoiceLive || !Application.isPlaying) return;
            ForgeNet.Send("albie.say", new JObject { ["text"] = text });
        }

        static string Options(OSPlanner.Question q)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < q.choices.Length; i++)
            {
                if (i > 0) sb.Append(i == q.choices.Length - 1 ? " or " : ", ");
                sb.Append(q.choices[i].text);
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- the cards

        void Render()
        {
            dirty = false;
            if (panel == null) return;
            RectTransform body = OSUi.Body(panel);
            if (body == null) return;
            OSUi.Clear(body);
            RectTransform column = OSUi.Column(body, 16f);
            if (planner.Done) BuildPlan(column, planner.Result);
            else BuildQuestion(column, planner.Current);
        }

        void BuildQuestion(RectTransform column, OSPlanner.Question q)
        {
            panel.Subtitle = "Albie is planning with you";
            OSUi.Text(column, "Question " + planner.Number + " of " + OSPlanner.MaxQuestions, NoteSize, true);
            TMP_Text title = OSUi.Text(column, q.text, LeadSize);
            title.fontStyle = FontStyles.Bold;

            for (int i = 0; i < q.choices.Length; i++)
            {
                int index = i;
                Wide(OSUi.Button(column, q.choices[i].text, () => Pick(index), OSButtonStyle.Secondary));
            }

            bool voice = VoiceLive;
            if (voice && planner.Asked > 0) Rigid(OSUi.Button(column, "Something else", StartFree, OSButtonStyle.Borderless));
            if (freeMode) OSUi.Text(column, "Say what you have in mind, in a few words.", NoteSize, true);
            else if (voice) OSUi.Text(column, "You can say an answer, or tap it.", NoteSize, true);
            if (notice.Length > 0) OSUi.Text(column, notice, LineSize);

            RectTransform row = Rigid(OSUi.Row(column, OSUi.ButtonHeight, OSUi.MinGap));
            Rigid(OSUi.Button(row, "Cancel", Cancel, OSButtonStyle.Borderless));
        }

        void BuildPlan(RectTransform column, OSPlanner.Plan plan)
        {
            panel.Subtitle = plan.kindTitle;
            TMP_Text title = OSUi.Text(column, "Here is the plan", LeadSize);
            title.fontStyle = FontStyles.Bold;

            Labelled(column, "Goal", plan.goal);
            Labelled(column, "Parts", string.Join(", ", plan.parts));
            Labelled(column, "Test", plan.test);
            OSUi.Text(column, "Milestones", NoteSize, true);
            for (int i = 0; i < plan.milestones.Count; i++) OSUi.Text(column, (i + 1) + ". " + plan.milestones[i], LineSize);

            RectTransform row = Rigid(OSUi.Row(column, OSUi.ButtonHeight, OSUi.MinGap));
            Rigid(OSUi.Button(row, "Start", () => StartTapped(), OSButtonStyle.Primary));
            Rigid(OSUi.Button(row, "Change", Change, OSButtonStyle.Secondary));
            Rigid(OSUi.Button(row, "Cancel", Cancel, OSButtonStyle.Borderless));

            OSUi.Text(column, "Nothing is saved until you tap Start.", NoteSize, true);
            if (notice.Length > 0) OSUi.Text(column, notice, LineSize);
        }

        void StartTapped() => StartBuild(out string _);

        static void Labelled(Transform parent, string label, string text)
        {
            OSUi.Text(parent, label, NoteSize, true);
            OSUi.Text(parent, text, LineSize);
        }

        static void Wide(Button button)
        {
            if (button == null) return;
            OSUi.Size(button.transform, AnswerWidth, OSUi.ButtonHeight);
            Rigid(button);
        }

        // The stock button reports flexible height, so a column would hand it all the spare height and spread the card out.
        static void Rigid(Button button)
        {
            if (button == null) return;
            var layout = button.GetComponent<LayoutElement>();
            if (layout != null) layout.flexibleHeight = 0f;
        }

        static RectTransform Rigid(RectTransform row)
        {
            var layout = row.GetComponent<LayoutElement>();
            if (layout != null) layout.flexibleHeight = 0f;
            return row;
        }

        // ---------------------------------------------------------------- saving (Start only)

        static JArray Milestones(OSPlanner.Plan plan)
        {
            var list = new JArray();
            for (int i = 0; i < plan.milestones.Count; i++)
                list.Add(new JObject { ["id"] = "m" + (i + 1), ["text"] = plan.milestones[i], ["done"] = false });
            return list;
        }

        // proj_<name>, the naming the notebook and the Projects tab already use: "proj_my_drone", then "proj_my_drone_2".
        // An id is taken when a folder has it, when it is a built-in starter or when it is the open project.
        static string NewProjectId(OSPlanner.Plan plan)
        {
            NotebookStore.Flush();  // a project started a moment ago may still be on its way to disk
            string folder = Path.Combine(NotebookStore.Root ?? NotebookStore.DefaultRoot, "projects");
            string baseId = "proj_" + plan.projectBase;
            string id = baseId;
            for (int n = 2; n < 1000; n++)
            {
                bool taken = OSProjects.KindOf(id) != OSProjectKind.Generic
                    || NotebookStore.ProjectId == id
                    || Directory.Exists(Path.Combine(folder, id));
                if (!taken) break;
                id = baseId + "_" + n;
            }
            return id;
        }

        // RevisePlan keeps the old parts (none, for a new project) and has no place for the answers. The plan record is
        // the store's own JSON: add the parts and the answers to it, and write that same file again (writes keep their order).
        static void SavePlanDetails(OSPlanner.Plan plan, int version)
        {
            try
            {
                JObject saved = NotebookStore.CurrentPlan;
                if (saved == null || (int?)saved["version"] != version) return;
                var parts = new JArray();
                for (int i = 0; i < plan.parts.Count; i++) parts.Add(plan.parts[i]);
                saved["parts"] = parts;
                var answers = new JArray();
                for (int i = 0; i < plan.answers.Count; i++)
                    answers.Add(new JObject { ["question"] = plan.questions[i], ["answer"] = plan.answers[i] });
                saved["answers"] = answers;
                string path = Path.Combine(NotebookStore.ProjectFolder, "plans", "plan_v" + version.ToString("0000") + ".json");
                NotebookFiles.Write(path, saved.ToString(Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.Log("[OS] Couldn't add the parts and answers to the saved plan: " + e.Message);
            }
        }
    }
}
