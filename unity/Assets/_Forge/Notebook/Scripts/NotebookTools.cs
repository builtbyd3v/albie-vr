using System;
using System.Collections.Generic;
using Forge.Controllers;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Notebook
{
    /// <summary>
    /// Connects the notebook to the rest of the app at Play start (C-17 sections 3 and 9):
    /// - Albie's notebook tools (<c>Resources/AlbieTools/notebook.json</c>), which run on the device with no hub;
    /// - ML A's finished training runs (<see cref="TrainingLog"/>), each becoming a <c>training</c> entry;
    /// - the demo project, opened from <c>persistentDataPath</c> until the Project module opens projects itself;
    /// - a flush of queued writes on quit.
    /// </summary>
    public static class NotebookTools
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
            AlbieTools.Register("notebook", new Dictionary<string, AlbieTools.Handler>
            {
                ["notebook_read"] = ReadTool,
                ["notebook_log"] = LogTool,
                ["notebook_mark"] = MarkTool,
                ["notebook_lessons"] = LessonsTool,
            });
            ConnectTraining();
            Application.quitting -= OnQuit;
            Application.quitting += OnQuit;
            if (!NotebookStore.IsOpen)
            {
                try { NotebookStore.Open(NotebookStore.DefaultProject); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        static void OnQuit() => NotebookStore.Flush();

        /// <summary>Logs every finished training run as a notebook entry. Safe to call more than once.</summary>
        public static void ConnectTraining()
        {
            TrainingLog.Finished -= OnTraining;
            TrainingLog.Finished += OnTraining;
        }

        public static void DisconnectTraining() => TrainingLog.Finished -= OnTraining;

        static void OnTraining(string controller, int version, JObject training)
        {
            try { NotebookStore.LogTraining(controller, version, training); }
            catch (Exception e) { Debug.LogException(e); }
        }

        // ---- Tools: each returns data, and a refusal comes back as {"error": "code: message"} ----------------------

        public static JObject ReadTool(JObject args)
        {
            Require();
            int last = Mathf.Clamp(args.Value<int?>("last") ?? 5, 1, 20);
            return NotebookStore.Read(last);
        }

        public static JObject LogTool(JObject args)
        {
            Require();
            string by = (string)args["rationale_by"] ?? "albie";
            string id = NotebookStore.Log("attempt", (string)args["change"], (string)args["rationale"], by, (string)args["expected"], Strings(args["tags"]));
            return new JObject { ["entry"] = id, ["version"] = NotebookStore.CurrentVersion(), ["outcome"] = "open" };
        }

        /// <summary>
        /// "That didn't work" or a tapped ✗: the person judged it, so <c>outcome_by</c> is <c>person</c> unless the
        /// agent says <c>by: albie</c> for its own judgment. A lesson in the same call is written by Albie.
        /// </summary>
        public static JObject MarkTool(JObject args)
        {
            Require();
            string entryId = (string)args["entry"];
            if (string.IsNullOrEmpty(entryId))
            {
                var target = NotebookStore.NewestOpen() ?? NotebookStore.Newest();
                if (target == null) throw new ArgumentException("unknown_entry: this project has no entries yet; log one first");
                entryId = (string)target["id"];
            }
            string by = (string)args["by"] ?? "person";
            if (by == "run") throw new ArgumentException("bad_value: by \"run\" is only for run records; use person or albie");
            string error = NotebookStore.Mark(entryId, (string)args["outcome"], by, (string)args["note"], (string)args["lesson"], Strings(args["tags"]));
            if (error != null) throw new ArgumentException(error);
            var entry = NotebookStore.Entry(entryId);
            var result = new JObject
            {
                ["entry"] = entryId,
                ["outcome"] = entry["outcome"],
                ["outcome_by"] = entry["outcome_by"],
                ["lesson_id"] = entry["lesson_id"],
            };
            if ((string)entry["outcome"] == "failed" && (string)entry["lesson"] == null)
                result["next"] = "Ask the person one question about why it failed, then call notebook_mark again with the same outcome and a lesson. Any number in the lesson must come from the entry's records.";
            return result;
        }

        public static JObject LessonsTool(JObject args)
        {
            Require();
            var tags = Strings(args["tags"]);
            var list = new JArray();
            foreach (var lesson in NotebookStore.Lessons(tags))
                list.Add(new JObject
                {
                    ["id"] = lesson.Id, ["text"] = lesson.Text, ["tags"] = new JArray(lesson.Tags), ["seen"] = lesson.Seen,
                    ["from"] = lesson.Json["from"]?.DeepClone(),
                });
            return new JObject { ["lessons"] = list };
        }

        static void Require()
        {
            if (!NotebookStore.IsOpen) throw new ArgumentException("no_project: no project is open");
        }

        static string[] Strings(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.String) return new[] { (string)token };
            if (!(token is JArray array)) throw new ArgumentException("bad_value: tags must be a list of strings");
            var result = new string[array.Count];
            for (int i = 0; i < result.Length; i++) result[i] = (string)array[i];
            return result;
        }
    }
}
