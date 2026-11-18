using System.Collections.Generic;
using Forge.OS;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Notebook
{
    /// <summary>
    /// Albie's project tools (<c>Resources/AlbieTools/project.json</c>), on the device with no hub:
    /// <c>project_open {name}</c> opens a project by what the person called it (<see cref="OSProjects.Resolve"/>: "drone",
    /// "the quadcopter", a title) and answers with its loop and the next step to offer; <c>project_step {step}</c> does one
    /// step of the open project's loop, as tapping it in the Project window does; <c>project_copy {name}</c> (its old name) is Make my
    /// own: Build something with the sample's kind picked (<see cref="OSProjects.MakeOwn"/>), never an empty copy. The four
    /// starters are samples: their answers carry <c>sample: true</c> and what the sample shows. Errors come back as {"error"} sentences.
    /// </summary>
    public static class ProjectTools
    {
        public static readonly string[] Names = { "project_open", "project_step", "project_copy" };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Register() => AlbieTools.Register("project", new Dictionary<string, AlbieTools.Handler>
        {
            ["project_open"] = OpenTool,
            ["project_step"] = StepTool,
            ["project_copy"] = CopyTool,
        });

        public static JObject OpenTool(JObject args)
        {
            string said = (string)args["name"] ?? (string)args["id"];
            if (string.IsNullOrWhiteSpace(said)) return AlbieTools.Error("Say which project to open.");
            string id = OSProjects.Resolve(said);
            if (id == null)
                return AlbieTools.Error("There is no project called '" + said.Trim() + "'. The samples are RC truck, Quadcopter, Rocket and Image classifier.");
            OSProjects.OpenProject(id);
            if (NotebookStore.ProjectId != id) return AlbieTools.Error("The notebook couldn't open " + OSProjects.TitleFor(id) + ".");
            var result = new JObject { ["ok"] = true, ["id"] = id, ["title"] = OSProjects.TitleFor(id) };
            Sample(id, result);
            Describe(View(), result);
            return result;
        }

        /// <summary>
        /// project_copy (the old name, kept): Make my own. A sample is never copied; this opens Build something with the
        /// sample's kind already picked (the one named, or the open one), so the person answers the rest and Start makes their
        /// own project with its own plan ("My RC truck"). The answer has the question now on the card and its choices.
        /// </summary>
        public static JObject CopyTool(JObject args)
        {
            string said = (string)args["name"] ?? (string)args["id"];
            string sample = string.IsNullOrWhiteSpace(said) ? NotebookStore.ProjectId : OSProjects.Resolve(said);
            if (sample == null || !OSProjects.IsSample(sample))
                return AlbieTools.Error("Make my own works from a sample: RC truck, Quadcopter, Rocket or Image classifier. For anything else, open Build something.");
            if (!OSProjects.MakeOwn(sample)) return AlbieTools.Error("Build something couldn't open right now.");
            var result = new JObject
            {
                ["ok"] = true, ["opened"] = "build", ["kind"] = OSProjects.TitleFor(sample), ["will_make"] = OSProjects.OwnTitle(sample),
                ["note"] = "Nothing is made until the user taps Start on the plan card. It is a new project with its own plan; the sample stays as it is.",
            };
            OSBuildFlow flow = OSBuildFlow.Current;
            OSPlanner.Question q = flow != null ? flow.Planner.Current : null;
            if (q != null)
            {
                result["question"] = q.text;
                result["choices"] = flow.OptionsLine;
                result["say"] = "Ask the user: " + q.text + " The choices are " + flow.OptionsLine + ".";
            }
            return result;
        }

        // A sample says so, and what it shows, so Albie can offer to look around or make their own.
        static void Sample(string id, JObject into)
        {
            if (!OSProjects.IsSample(id)) return;
            into["sample"] = true;
            into["sample_shows"] = OSProjects.SampleShows(id);
            into["sample_note"] = "A finished example to look around: the user can try any step here. To keep their work, offer "
                                  + OSProjects.MakeOwnLabel(id) + " (project_copy), which plans a new project of their own.";
        }

        public static JObject StepTool(JObject args)
        {
            if (!NotebookStore.IsOpen) return AlbieTools.Error("No project is open. Open one first with project_open.");
            string id = NotebookStore.ProjectId;
            OSProjectWindow view = View();
            if (view == null || view.ProjectId != id)
            {
                OSWindow window = OSProjectWindow.Open(id);
                view = window != null ? window.GetComponent<OSProjectWindow>() : null;
            }
            if (view == null) return AlbieTools.Error("The Project window isn't available in this scene.");
            string step = ((string)args["step"] ?? "").Trim().ToLowerInvariant();
            if (step.Length == 0) step = view.NextAction();
            if (string.IsNullOrEmpty(step)) return AlbieTools.Error("This project has no next step to suggest. Ask the user what to do.");
            for (int i = 0; i < view.Steps.Count; i++)
            {
                OSProjectWindow.Step s = view.Steps[i];
                if (s.action == step && s.unavailable != null)
                    return AlbieTools.Error(s.label + " isn't available: " + s.unavailable);
            }
            string line = view.Do(step);
            var result = new JObject { ["ok"] = true, ["project"] = id, ["step"] = step, ["result"] = line };
            Describe(view, result);
            return result;
        }

        static OSProjectWindow View()
        {
            OSShell shell = OSShell.Current;
            OSWindow window = shell != null ? shell.Windows.Find(OSProjectWindow.WindowId) : null;
            return window != null ? window.GetComponent<OSProjectWindow>() : null;
        }

        // The loop (each step, and why one is off) and the next step with the line to offer it.
        static void Describe(OSProjectWindow view, JObject into)
        {
            if (view == null) return;
            var loop = new JArray();
            for (int i = 0; i < view.Steps.Count; i++)
            {
                OSProjectWindow.Step s = view.Steps[i];
                var item = new JObject { ["step"] = s.action, ["label"] = s.label, ["available"] = s.unavailable == null };
                if (s.unavailable != null) item["why_not"] = s.unavailable;
                loop.Add(item);
            }
            into["steps"] = loop;
            string next = view.NextAction();
            if (next != null) into["next_step"] = next;
            into["say"] = view.NextStepLine();
        }
    }
}
