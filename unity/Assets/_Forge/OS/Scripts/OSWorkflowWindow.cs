using System;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>The live coding workflow: plan, crew progress, tests, paged diff and explicit publication. The builds
    /// come from the hub when it is live, else from Albie's cloud (<see cref="ForgeCloud"/> routes /agent/ there);
    /// while neither answers, the window stays and says why.</summary>
    public sealed class OSWorkflowWindow : MonoBehaviour
    {
        public const string WindowId = "workflow";
        internal const string BuildsPath = "/agent/builds";
        const string PetId = "bit";
        const string WakingLine = CloudHealth.WakingStatus + " Builds start when it's awake.";
        const string OfflineLine = CloudHealth.OfflineStatus + " Builds start when it's back.";
        ForgePanel panel;
        JObject current, review;
        IDisposable updates;
        string tab = "plan", notice = "", goal = "", repo = "";
        int page;
        bool pending, wasServed, pollFailed;
        float nextPoll;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Register()
        {
            OSShell.RegisterSystem(WindowId, () => Open());
            AlbieCards.Register("build:", TapCard);  // hub off: the buttons on Albie's plan and "built" cards
        }

        /// <summary>The hub or Albie's cloud serves the builds right now.</summary>
        internal static bool Served => ForgeCloud.Serves(BuildsPath);

        /// <summary>Why there are no builds right now, in one line: the cloud is waking or offline, or nothing is set up.</summary>
        internal static string Unreachable()
        {
            if (ForgeNet.Status == ForgeNetStatus.Connecting || !ForgeCloud.Probed) return "Looking for Albie's cloud…";
            if (ForgeCloud.State == CloudState.Waking) return WakingLine;
            if (ForgeCloud.State == CloudState.Offline) return OfflineLine;
            return ForgeCloud.Configured ? "Albie's cloud isn't answering yet. Builds start when it does."
                : "Connect the Albie hub to build a project with Claude.";
        }

        /// <summary>The reason inside a failed request ("503 ... {"detail": "..."}"), as the server worded it.</summary>
        internal static string Reason(string error)
        {
            if (string.IsNullOrEmpty(error)) return "That didn't work. Try again.";
            int brace = error.IndexOf('{');
            if (brace >= 0)
            {
                try
                {
                    var body = JObject.Parse(error.Substring(brace));
                    var detail = body["detail"] ?? body["error"];
                    if (detail != null && detail.Type == JTokenType.String) return (string)detail;
                }
                catch (Newtonsoft.Json.JsonException) { }
            }
            return (ForgeNet.Status == ForgeNetStatus.Live ? "The hub" : "Albie's cloud") + " couldn't complete that action: " + error;
        }

        // build:approve:<id>, build:cancel:<id>, build:ship:<id> (the user tapped Open pull request), build:later:<id>.
        // Hub live: the hub handles the tap itself (AlbieFixCard only asks here with the hub off).
        static bool TapCard(string actionId)
        {
            if (!ForgeCloud.Routes(BuildsPath)) return false;
            string[] parts = actionId.Split(':');
            if (parts.Length < 3) return false;
            string verb = parts[1];
            if (verb == "later") return true;
            if (verb != "approve" && verb != "cancel" && verb != "ship") return false;
            ForgeNet.Post(BuildsPath + "/" + parts[2] + "/" + verb, new { confirm = true }, null,
                error => ForgeNet.Inject("pet.say", new JObject { ["pet_id"] = PetId, ["text"] = Reason(error) }));
            return true;
        }

        public static OSWindow Open()
        {
            if (OSShell.Current == null) return null;
            return OSShell.Current.Windows.Open(WindowId, "Project workflow", new Vector2(0.82f, 0.78f), p =>
            {
                var view = p.gameObject.AddComponent<OSWorkflowWindow>();
                view.panel = p;
                AlbieCrew.WorkflowWindowOpen = true;
                view.updates = ForgeNet.On("build.updated", value =>
                {
                    if (view == null) return;
                    view.current = (JObject)value.DeepClone();
                    if (view.tab != "new") view.Render();
                }, true);
                ForgeCloud.Ask();  // a sleeping cloud is checked again now, and Albie says when it's awake
                view.wasServed = Served;
                view.Render();
                view.Refresh();
            }, "WORKFLOW");
        }

        void OnDestroy() { updates?.Dispose(); AlbieCrew.WorkflowWindowOpen = false; }

        void Update()
        {
            if (Time.unscaledTime < nextPoll || pending) return;
            nextPoll = Time.unscaledTime + 3f;
            bool served = Served;
            if (served != wasServed)
            {
                wasServed = served;
                if (served) notice = "";
                if (tab == "new") Render();  // the Create button follows it
            }
            if (!served) ShowUnreachable();
            else if (tab != "new") Refresh();
        }

        // Neither the hub nor the cloud has the builds: say which, once per change (the window is not rebuilt each poll).
        void ShowUnreachable()
        {
            string line = Unreachable();
            if (line == notice) return;
            notice = line;
            Render();
        }

        void Refresh()
        {
            if (!Served)
            {
                ShowUnreachable();
                return;
            }
            pending = true;
            ForgeNet.Get(BuildsPath, text =>
            {
                if (this == null) return;
                pending = false;
                if (pollFailed) { pollFailed = false; notice = ""; }
                var all = JArray.Parse(text);
                current = all.Count > 0 ? all[0] as JObject : null;
                if (tab != "new") Render();
            }, error =>
            {
                pollFailed = true;  // e.g. the cloud has no Claude key: its own sentence, until a poll works again
                Failed(error);
            });
        }

        void Failed(string error)
        {
            if (this == null) return;
            pending = false;
            string line = Reason(error);
            if (line == notice) return;
            notice = line;
            Render();
        }

        void Act(string action)
        {
            if (pending || current == null) return;
            if (!Served) { ShowUnreachable(); return; }
            pending = true;
            ForgeNet.Post(BuildsPath + "/" + (string)current["id"] + "/" + action, new { confirm = true }, _ =>
            {
                if (this == null) return;
                pending = false;
                notice = "";
                Refresh();
            }, Failed);
        }

        void LoadReview()
        {
            if (pending || current == null) return;
            if (!Served) { ShowUnreachable(); return; }
            pending = true;
            ForgeNet.Get(BuildsPath + "/" + (string)current["id"] + "/review", text =>
            {
                if (this == null) return;
                pending = false;
                review = JObject.Parse(text);
                tab = "changes";
                page = 0;
                Render();
            }, Failed);
        }

        TMP_Text Line(Transform parent, string text, bool secondary = false)
        {
            var label = OSUi.Text(parent, text, OSUi.BodyText, secondary);
            label.richText = false;
            return label;
        }

        void Render()
        {
            if (panel == null) return;
            var body = OSUi.Body(panel);
            OSUi.Clear(body);
            var column = OSUi.Column(body, 12f);
            var nav = OSUi.Row(column, OSUi.ButtonHeight, 19f);
            OSUi.Button(nav, "New task", () => { tab = "new"; Render(); });
            OSUi.Button(nav, "Plan", () => { tab = "plan"; Render(); });
            OSUi.Button(nav, "Changes", LoadReview);
            OSUi.Button(nav, "Checks", () => { tab = "checks"; page = 0; Render(); });
            if (tab == "new") { NewTask(column); FixLayout(body); return; }
            if (current == null)
            {
                panel.Subtitle = "";
                Line(column, "No project workflow yet. Tap New task, or ask Albie to build a project.");
                Line(column, notice, true);
                FixLayout(body);
                return;
            }
            string state = (string)current["state"] ?? "";
            panel.Subtitle = state.Replace('_', ' ') + " · " + ((bool?)current["simulated"] == true ? "simulated" : (string)current["backend"]);
            Line(column, (string)current["title"] ?? (string)current["goal"]);
            Line(column, (bool?)current["demo"] == true
                ? "Scripted sample · runs without API keys · publishing is off"
                : (string)current["repo"] ?? "No GitHub repository is set, so the project is kept but can't be published.", true);
            if (tab == "changes")
            {
                string diff = (string)review?["diff"] ?? "No committed diff yet.";
                string[] lines = diff.Split('\n');
                int pages = Mathf.Max(1, (lines.Length + 11) / 12);
                page = Mathf.Clamp(page, 0, pages - 1);
                Line(column, "Changed files: " + string.Join(", ", review?["files"]?.ToObject<string[]>() ?? Array.Empty<string>()), true);
                var chunk = new System.Text.StringBuilder();
                for (int i = page * 12; i < Mathf.Min(lines.Length, page * 12 + 12); i++)
                    chunk.AppendLine(lines[i].Length > 100 ? lines[i].Substring(0, 100) + "…" : lines[i]);
                Line(column, diff.Length > 0 ? chunk.ToString() : "No changes yet.");
                Line(column, "Long lines are shortened here.", true);
                var pager = OSUi.Row(column, OSUi.ButtonHeight);
                OSUi.Button(pager, "Previous", () => { page--; Render(); });
                Line(pager, (page + 1) + " / " + pages, true);
                OSUi.Button(pager, "Next", () => { page++; Render(); });
            }
            else if (tab == "checks")
            {
                var checks = current["verification"] as JArray;
                if (checks == null || checks.Count == 0) Line(column, "Independent checks run after the crew finishes.");
                else
                {
                    page = Mathf.Clamp(page, 0, checks.Count - 1);
                    var check = checks[page];
                    string command = string.Join(" ", check["command"].ToObject<string[]>());
                    Line(column, (string)check["state"] + ": " + (command.Length > 130 ? command.Substring(0, 130) + "…" : command));
                    string output = (string)check["output"] ?? "";
                    var lines = output.Split('\n');
                    var chunk = new System.Text.StringBuilder();
                    for (int i = Mathf.Max(0, lines.Length - 12); i < lines.Length; i++)
                        chunk.AppendLine(lines[i].Length > 100 ? lines[i].Substring(0, 100) + "…" : lines[i]);
                    Line(column, chunk.ToString(), true);
                    var pager = OSUi.Row(column, OSUi.ButtonHeight);
                    OSUi.Button(pager, "Previous check", () => { page--; Render(); });
                    Line(pager, (page + 1) + " / " + checks.Count, true);
                    OSUi.Button(pager, "Next check", () => { page++; Render(); });
                }
            }
            else
            {
                var steps = current["steps"] as JArray;
                if (steps == null || steps.Count == 0) Line(column, "No steps planned yet.", true);
                else foreach (var step in steps)
                {
                    Line(column, (string)step["agent"] + " · " + ((string)step["state"] ?? "").Replace('_', ' '));
                    string detail = (string)step[((string)step["state"] == "running") ? "progress" : "summary"];
                    if (string.IsNullOrEmpty(detail)) detail = (string)step["task"] ?? "";
                    Line(column, detail.Length > 150 ? detail.Substring(0, 150) + "…" : detail, true);
                }
            }
            if (state == "awaiting_plan") OSUi.Button(column, "Approve implementation plan", () => Act("approve"), OSButtonStyle.Primary);
            if (state == "ready" && current["repo"]?.Type == JTokenType.String)
            {
                Line(column, "Review Changes and Checks. This publishes the branch and opens a GitHub PR.", true);
                OSUi.Button(column, "Publish branch and open PR", () => Act("ship"), OSButtonStyle.Primary);
            }
            if (state == "planning" || state == "awaiting_plan" || state == "building" || state == "verifying")
                OSUi.Button(column, "Stop workflow", () => Act("cancel"), OSButtonStyle.Destructive);
            string url = (string)current["pr_url"];
            if (!string.IsNullOrEmpty(url)) Line(column, url);
            Line(column, (string)current["error"] ?? notice, true);
            FixLayout(body);
        }

        // UI Set button plates report flexible height through their nested layout groups.
        // Pin each row and button so the plan starts at the top rather than below a stretched navigation row.
        static void FixLayout(RectTransform body)
        {
            foreach (var button in body.GetComponentsInChildren<Button>())
            {
                var element = button.GetComponent<LayoutElement>();
                if (element != null) element.flexibleHeight = 0f;
            }
            foreach (var row in body.GetComponentsInChildren<HorizontalLayoutGroup>())
            {
                var element = row.GetComponent<LayoutElement>();
                if (element != null) element.flexibleHeight = 0f;
            }
        }

        void NewTask(RectTransform column)
        {
            Line(column, "What should Albie's crew build or fix?");
            Input(column, goal, "Describe the project or change", value => goal = value);
            Line(column, "GitHub repository (optional owner/repo)", true);
            Input(column, repo, "Leave blank to use Albie's own repository", value => repo = value);
            var create = OSUi.Button(column, "Create implementation plan", () =>
            {
                if (pending || string.IsNullOrWhiteSpace(goal)) return;
                if (!Served) { ShowUnreachable(); return; }  // in mock mode a POST "succeeds" with {}
                pending = true;
                ForgeNet.Post(BuildsPath, new { goal, repo = string.IsNullOrWhiteSpace(repo) ? null : repo.Trim() }, text =>
                {
                    if (this == null) return;
                    pending = false;
                    current = JObject.Parse(text);
                    notice = "";
                    tab = "plan";
                    Render();
                }, Failed);
            }, OSButtonStyle.Primary);
            if (create != null) create.interactable = Served && !pending;
            Line(column, "Albie proposes a plan first. You approve implementation and publication separately.", true);
            Line(column, notice, true);
        }

        static void Input(Transform parent, string value, string placeholder, Action<string> changed)
        {
            var go = new GameObject("Workflow input", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
            go.transform.SetParent(parent, false);
            OSUi.Size(go.transform, 900f, 90f);
            go.GetComponent<Image>().color = new Color(0.08f, 0.09f, 0.11f, 1f);
            var area = new GameObject("Text area", typeof(RectTransform), typeof(RectMask2D));
            area.transform.SetParent(go.transform, false);
            var rect = (RectTransform)area.transform;
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(16f, 10f); rect.offsetMax = new Vector2(-16f, -10f);
            var label = OSUi.Text(area.transform, "", OSUi.BodyText);
            var hint = OSUi.Text(area.transform, placeholder, OSUi.SmallText, true);
            foreach (var text in new[] { label, hint })
            {
                text.rectTransform.anchorMin = Vector2.zero; text.rectTransform.anchorMax = Vector2.one;
                text.rectTransform.offsetMin = text.rectTransform.offsetMax = Vector2.zero;
                text.richText = false;
            }
            var input = go.GetComponent<TMP_InputField>();
            input.textViewport = rect; input.textComponent = label; input.placeholder = hint;
            input.characterLimit = 2000;
            input.text = value;
            input.onValueChanged.AddListener(v => changed(v));
            input.onSelect.AddListener(_ => { if (global::DesktopRig.Current != null) global::DesktopRig.Current.FreeCursor(); });
        }
    }
}
