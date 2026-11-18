using System;
using System.Collections.Generic;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Forge.OS
{
    /// <summary>The live coding workflow: plan, crew progress, tests, paged diff and explicit publication.</summary>
    public sealed class OSWorkflowWindow : MonoBehaviour
    {
        public const string WindowId = "workflow";
        ForgePanel panel;
        Button createPlan;
        readonly List<Button> actionButtons = new List<Button>();
        JObject current, review;
        IDisposable updates;
        string tab = "plan", notice = "", goal = "", repo = "";
        int page;
        bool pending, polling, publicationAsked, connectionWarning;
        float nextPoll;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Register() => OSShell.RegisterSystem(WindowId, () => Open());

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
                view.Render();
                view.Refresh();
            }, "WORKFLOW");
        }

        void OnDestroy() { updates?.Dispose(); AlbieCrew.WorkflowWindowOpen = false; }

        void Update()
        {
            // An initial status request can finish after New task is opened. Keep the button in step without
            // rebuilding the form (which would steal text focus), including when the cloud wakes or disconnects.
            if (createPlan != null) createPlan.interactable = OSWorkflowTransport.Available && !pending;
            foreach (var button in actionButtons)
                if (button != null) button.interactable = OSWorkflowTransport.Available && !pending;
            if (tab == "new" && OSWorkflowTransport.Available && connectionWarning)
            {
                connectionWarning = false;
                notice = "";
                Render();
            }
            if (Time.unscaledTime < nextPoll || pending || polling || tab == "new") return;
            nextPoll = Time.unscaledTime + 3f;
            Refresh();
        }

        void Refresh()
        {
            if (polling) return;
            if (!OSWorkflowTransport.Available)
            {
                notice = OSWorkflowTransport.ConnectionMessage;
                connectionWarning = true;
                Render();
                return;
            }
            polling = true;
            connectionWarning = false;
            OSWorkflowTransport.List(all =>
            {
                if (this == null) return;
                polling = false;
                nextPoll = Time.unscaledTime + 3f;
                if (pending) return;
                string previousId = (string)current?["id"];
                current = all.Count > 0 ? all[0] as JObject : null;
                if (previousId != (string)current?["id"]) publicationAsked = false;
                if (current == null) notice = "";
                if (tab != "new") Render();
            }, error =>
            {
                if (this == null) return;
                polling = false;
                nextPoll = Time.unscaledTime + 3f;
                if (!pending && tab != "new") Failed(error);
            });
        }

        void Failed(string error)
        {
            if (this == null) return;
            pending = false;
            connectionWarning = false;
            notice = "The workflow couldn't complete that action: " + error;
            Render();
        }

        void Act(string action)
        {
            if (pending || current == null) return;
            pending = true;
            OSWorkflowTransport.Act((string)current["id"], action, result =>
            {
                if (this == null) return;
                pending = false;
                publicationAsked = (string)result["status"] == "awaiting_confirmation";
                notice = publicationAsked ? "Review Changes and Checks, then tap Confirm publish and open PR to publish." : "";
                Refresh();
            }, Failed);
        }

        void LoadReview()
        {
            if (pending || current == null) return;
            pending = true;
            OSWorkflowTransport.Review((string)current["id"], value =>
            {
                if (this == null) return;
                pending = false;
                review = value;
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

        void ActionButton(Transform parent, string text, Action action, OSButtonStyle style = OSButtonStyle.Secondary)
        {
            var button = OSUi.Button(parent, text, action, style);
            if (button == null) return;
            button.interactable = OSWorkflowTransport.Available && !pending;
            actionButtons.Add(button);
        }

        void Render()
        {
            if (panel == null) return;
            actionButtons.Clear();
            var body = OSUi.Body(panel);
            OSUi.Clear(body);
            var column = OSUi.Column(body, 12f);
            var nav = OSUi.Row(column, OSUi.ButtonHeight, 19f);
            OSUi.Button(nav, "New task", () => { tab = "new"; Render(); });
            OSUi.Button(nav, "Plan", () => { tab = "plan"; Render(); });
            ActionButton(nav, "Changes", LoadReview);
            OSUi.Button(nav, "Checks", () => { tab = "checks"; page = 0; Render(); });
            if (tab == "new") { NewTask(column); FixLayout(body); return; }
            if (current == null)
            {
                Line(column, "No project workflow yet. Tap New task, or ask Albie to build a project.");
                Line(column, notice, true);
                FixLayout(body);
                return;
            }
            string state = (string)current["state"];
            panel.Subtitle = state + " · " + ((bool?)current["simulated"] == true ? "simulated" : (string)current["backend"]);
            Line(column, (string)current["title"] ?? (string)current["goal"]);
            Line(column, (bool?)current["demo"] == true
                ? "Fixed scripted sample · no API keys · publishing disabled"
                : (string)current["repo"] ?? "Local project; configure BUILD_REPO to publish it.", true);
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
                Line(column, "Long lines are shortened here. The full diff is available on the hub's build page.", true);
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
                foreach (var step in (JArray)current["steps"])
                {
                    Line(column, (string)step["agent"] + " · " + (string)step["state"]);
                    string detail = (string)step[((string)step["state"] == "running") ? "progress" : "summary"];
                    if (string.IsNullOrEmpty(detail)) detail = (string)step["task"];
                    Line(column, detail.Length > 150 ? detail.Substring(0, 150) + "…" : detail, true);
                }
            }
            if (state == "awaiting_plan") ActionButton(column, "Approve implementation plan", () => Act("approve"), OSButtonStyle.Primary);
            if (state == "ready" && current["repo"]?.Type == JTokenType.String)
            {
                Line(column, "Review Changes and Checks. This publishes the branch and opens a pull request.", true);
                ActionButton(column, publicationAsked ? "Confirm publish and open PR" : "Publish branch and open PR", () => Act("ship"), OSButtonStyle.Primary);
            }
            if (state == "planning" || state == "awaiting_plan" || state == "building" || state == "verifying")
                ActionButton(column, "Stop workflow", () => Act("cancel"), OSButtonStyle.Destructive);
            string url = (string)current["pr_url"];
            if (!string.IsNullOrEmpty(url)) Line(column, url);
            Line(column, (string)current["error"] ?? (pending ? "Contacting the workflow service…" : notice), true);
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
            Line(column, "Source control repository (optional owner/repo)", true);
            Input(column, repo, "Leave blank to use the hub's configured destination", value => repo = value);
            var create = OSUi.Button(column, "Create implementation plan", () =>
            {
                if (pending || string.IsNullOrWhiteSpace(goal)) return;
                pending = true;
                OSWorkflowTransport.Start(goal, string.IsNullOrWhiteSpace(repo) ? null : repo.Trim(), value =>
                {
                    if (this == null) return;
                    pending = false;
                    current = value;
                    publicationAsked = false;
                    notice = "";
                    tab = "plan";
                    Render();
                }, Failed);
            }, OSButtonStyle.Primary);
            createPlan = create;
            if (create != null) create.interactable = OSWorkflowTransport.Available && !pending;
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
