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
    /// step of the open project's loop, as tapping it in the Project window does; <c>project_copy {name}</c> makes the person's
    /// own copy of a sample project (<see cref="OSProjects.CopySample"/>). The four starters are sample projects: their
    /// answers carry <c>sample: true</c> and what the sample shows. Errors come back as {"error"} sentences.
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
                return AlbieTools.Error("There is no project called '" + said.Trim() + "'. The sample projects are RC truck, Quadcopter, Rocket and Image classifier.");
            OSProjects.OpenProject(id);
            if (NotebookStore.ProjectId != id) return AlbieTools.Error("The notebook couldn't open " + OSProjects.TitleFor(id) + ".");
            var result = new JObject { ["ok"] = true, ["id"] = id, ["title"] = OSProjects.TitleFor(id) };
            Sample(id, result);
            Describe(View(), result);
            return result;
        }

        /// <summary>project_copy: the person's own copy of a sample project (the one named, or the open one), opened.</summary>
        public static JObject CopyTool(JObject args)
        {
            string said = (string)args["name"] ?? (string)args["id"];
            string sample = string.IsNullOrWhiteSpace(said) ? NotebookStore.ProjectId : OSProjects.Resolve(said);
            if (sample == null || !OSProjects.IsSample(sample))
                return AlbieTools.Error("Only a sample project can be copied: RC truck, Quadcopter, Rocket or Image classifier.");
            string id = OSProjects.CopySample(sample, out string error);
            if (id == null) return AlbieTools.Error(error ?? "The copy couldn't be made.");
            var result = new JObject
            {
                ["ok"] = true, ["id"] = id, ["title"] = OSProjects.TitleFor(id), ["copied_from"] = sample,
                ["kept"] = "the plan and the design link; not the sample's versions, runs or notes",
            };
            Describe(View(), result);
            result["say"] = OSProjects.TitleFor(id) + " is yours now, with the same plan. " + (string)result["say"];
            return result;
        }

        // A sample project says so, and what it shows, so Albie can offer to explore it or copy it.
        static void Sample(string id, JObject into)
        {
            if (!OSProjects.IsSample(id)) return;
            into["sample"] = true;
            into["sample_shows"] = OSProjects.SampleShows(id);
            into["sample_note"] = "A worked example the user can explore, continue, or copy with project_copy to make their own.";
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
