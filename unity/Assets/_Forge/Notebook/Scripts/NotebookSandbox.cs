using System.IO;
using System.Text;
using Forge.Controllers;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Forge.Notebook
{
    /// <summary>
    /// Desktop sandbox for the notebook (hub off). It opens its own project folder under
    /// <c>persistentDataPath/notebook_sandbox/</c>, so the real notebook stays clean. Stop and start Play to check
    /// that everything comes back after a restart.
    ///
    /// Keys: N new attempt, F "that didn't work", K Albie writes the lesson, W worked, C / X / A a run completed /
    /// failed / aborted, I the truck rolled over, T a training run ends, V cut a version, S seed the demo story,
    /// H hide or show the newest lesson, Z wipe the sandbox notebook.
    /// </summary>
    public sealed class NotebookSandbox : MonoBehaviour
    {
        public const string Project = "proj_sandbox";
        public static string SandboxRoot => Path.Combine(Application.persistentDataPath, "notebook_sandbox");

        public string Status { get; private set; } = "";
        int runs;
        string readout = "";
        GUIStyle style;

        void Start()
        {
            NotebookStore.Open(Project, SandboxRoot);
            int problems = NotebookStore.LoadProblems.Count;
            Status = $"opened {NotebookStore.Entries.Count} entries, {NotebookStore.AllLessons.Count} lessons" +
                     (problems > 0 ? $"; {problems} damaged file(s) moved aside" : "");
            NotebookStore.EntryCreated += OnChanged;
            NotebookStore.EntryUpdated += OnChanged;
            BuildReadout();
        }

        void OnDestroy()
        {
            NotebookStore.EntryCreated -= OnChanged;
            NotebookStore.EntryUpdated -= OnChanged;
            NotebookStore.Flush();
        }

        void OnChanged(JObject _) => BuildReadout();

        void Update()
        {
            var k = Keyboard.current;
            if (k == null) return;
            if (k.nKey.wasPressedThisFrame) NewAttempt();
            if (k.fKey.wasPressedThisFrame) ThatDidntWork();
            if (k.kKey.wasPressedThisFrame) WriteLesson();
            if (k.wKey.wasPressedThisFrame) Report(NotebookTools.MarkTool(new JObject { ["outcome"] = "worked" }));
            if (k.cKey.wasPressedThisFrame) Run("completed");
            if (k.xKey.wasPressedThisFrame) Run("failed");
            if (k.aKey.wasPressedThisFrame) Run("aborted");
            if (k.iKey.wasPressedThisFrame) RolledOver();
            if (k.tKey.wasPressedThisFrame) Training();
            if (k.vKey.wasPressedThisFrame) CutVersion();
            if (k.sKey.wasPressedThisFrame) Seed();
            if (k.hKey.wasPressedThisFrame) ToggleNewestLesson();
            if (k.zKey.wasPressedThisFrame) Wipe();
        }

        // Each action goes through the same tool or API the agent, the artifact host and ML A use.

        public void NewAttempt() => Report(Tool("notebook_log", new JObject
        {
            ["change"] = "Truck: rear drive, servo steering.", ["rationale"] = "Simplest truck for the slalom.",
            ["expected"] = "Drives around the couch without tipping.", ["tags"] = new JArray("vehicle", "stability"),
        }));

        public void ThatDidntWork() => Report(Tool("notebook_mark", new JObject { ["outcome"] = "failed" }));

        public void WriteLesson()
        {
            var failed = NewestFailed();
            if (failed == null) { Status = "no failed entry to learn from: press F first"; return; }
            Report(Tool("notebook_mark", new JObject
            {
                ["entry"] = failed["id"], ["outcome"] = "failed",
                ["lesson"] = "A narrow track rolls over on sharp turns at speed.", ["tags"] = new JArray("vehicle", "stability"),
            }));
        }

        public void Run(string outcome)
        {
            string id = NotebookStore.AttachRun($"run_{++runs:0000}_{Time.frameCount}", outcome);
            Status = $"run {outcome} → {id}";
        }

        public void RolledOver()
        {
            string id = NotebookStore.AddIncident(new JObject
            {
                ["kind"] = "rolled_over", ["thing"] = "truck", ["thing_version"] = NotebookStore.CurrentVersion(), ["speed_ms"] = 2.1,
            });
            Status = $"incident → {id}";
        }

        // Exactly what ML A's sandbox does when training ends (Controllers/ControllersSandbox).
        public void Training()
        {
            var block = new JObject { ["samples"] = 1676, ["mirrored"] = false, ["epochs"] = 120, ["seconds"] = 2.4, ["val_loss"] = 0.0065, ["laps"] = 2, ["seed"] = 1 };
            block["candidates"] = new JArray(block.DeepClone(), block.DeepClone(), block.DeepClone());
            TrainingLog.Report("truck_autopilot", 1, block);
            Status = $"training reported → {(string)NotebookStore.Newest()?["id"]}";
        }

        public void CutVersion()
        {
            int v = NotebookStore.CurrentVersion() + 1;
            NotebookStore.CutVersion("thing", $"Truck v{v}: wider track.", new JObject { ["things"] = new JObject { ["truck"] = v } }, "It rolled over.", "person");
            Status = $"cut v{v}";
        }

        public void Seed()
        {
            int added = NotebookStore.SeedFixtures();
            Status = added > 0 ? $"seeded the demo story: {added} entries" : "the sandbox notebook isn't empty: press Z to wipe it first";
            BuildReadout();
        }

        public void ToggleNewestLesson()
        {
            var all = NotebookStore.AllLessons;
            if (all.Count == 0) { Status = "no lessons yet"; return; }
            var lesson = all[all.Count - 1];
            bool hide = (bool?)lesson["hidden"] != true;
            Status = NotebookStore.SetLessonHidden((string)lesson["id"], hide) ?? $"{lesson["id"]} {(hide ? "hidden" : "shown")}";
            BuildReadout();
        }

        public void Wipe()
        {
            NotebookStore.Close();
            if (Directory.Exists(SandboxRoot)) Directory.Delete(SandboxRoot, true);
            NotebookStore.Open(Project, SandboxRoot);
            runs = 0;
            Status = "wiped the sandbox notebook";
            BuildReadout();
        }

        static JObject Tool(string name, JObject args) => AlbieTools.Has(name) ? AlbieTools.Call(name, args) : AlbieTools.Error($"{name} isn't registered");

        void Report(JObject result)
        {
            Status = result["error"] != null ? "refused: " + result["error"] : result.ToString(Newtonsoft.Json.Formatting.None);
            BuildReadout();
        }

        static JObject NewestFailed()
        {
            var entries = NotebookStore.Entries;
            for (int i = entries.Count - 1; i >= 0; i--) if ((string)entries[i]["outcome"] == "failed") return entries[i];
            return null;
        }

        void BuildReadout()
        {
            var sb = new StringBuilder(2048);
            sb.Append($"<b>Notebook sandbox</b>   {NotebookStore.ProjectId} v{NotebookStore.CurrentVersion()}   {NotebookStore.Entries.Count} entries\n");
            sb.Append("N attempt   F that didn't work   K lesson   W worked   C/X/A run done/failed/aborted   I rolled over   T training   V cut version   S seed   H hide lesson   Z wipe\n\n");
            var entries = NotebookStore.Entries;
            for (int i = Mathf.Max(0, entries.Count - 12); i < entries.Count; i++)
            {
                var e = entries[i];
                string outcome = (string)e["outcome"], by = (string)e["outcome_by"];
                string color = outcome == "worked" ? "#5be08a" : outcome == "failed" ? "#ff7a6b" : outcome == "partly" ? "#ffd166" : "#9aa4b2";
                int runCount = (e["observed"]?["runs"] as JArray)?.Count ?? 0, incidents = (e["observed"]?["incidents"] as JArray)?.Count ?? 0;
                sb.Append($"{e["id"]}  v{e["project_version"]}  {e["kind"],-10} <color={color}>{outcome}{(by != null ? " (" + by + ")" : "")}</color>  {e["change"]}");
                if (runCount > 0) sb.Append($"  [{runCount} run{(runCount == 1 ? "" : "s")}]");
                if (incidents > 0) sb.Append($"  [{incidents} incident{(incidents == 1 ? "" : "s")}]");
                if ((string)e["observed"]?["note"] != null) sb.Append($"\n      note: {e["observed"]["note"]}");
                if ((string)e["lesson"] != null) sb.Append($"\n      <color=#ffd166>lesson: {e["lesson"]}</color>");
                sb.Append('\n');
            }
            sb.Append("\n<b>Lessons</b> (what Albie reads)\n");
            foreach (var lesson in NotebookStore.Lessons(null)) sb.Append($"  {lesson.Id} [{string.Join(", ", lesson.Tags)}] {lesson.Text}\n");
            int hidden = 0;
            foreach (var l in NotebookStore.AllLessons) if ((bool?)l["hidden"] == true) hidden++;
            if (hidden > 0) sb.Append($"  ({hidden} hidden)\n");
            readout = sb.ToString();
        }

        void OnGUI()
        {
            if (style == null) style = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true, wordWrap = true, alignment = TextAnchor.UpperLeft };
            style.normal.textColor = new Color(0.9f, 0.92f, 0.95f);
            GUI.Label(new Rect(16, 12, Screen.width - 32, Screen.height - 60), readout, style);
            GUI.Label(new Rect(16, Screen.height - 40, Screen.width - 32, 30), Status, style);
        }
    }
}
