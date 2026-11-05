using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Forge.Artifacts;
using Forge.Controllers;
using Forge.Notebook;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Headless checks for the notebook (C-17): Nov 8 v1 plus Nov 12 plan revision, session-start read and v1 vs v2
/// compare. Uses a scratch folder under <c>temporaryCachePath</c>, never the real notebook.
/// Forge > Notebook > Check Notebook logs the report; <see cref="Run"/> returns it.
/// </summary>
public static class NotebookCheck
{
    static string root;

    [MenuItem("Forge/Notebook/Check Notebook")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    /// <summary>For -executeMethod.</summary>
    public static void RunBatch()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
        EditorApplication.Exit(report.StartsWith("PASS") ? 0 : 1);
    }

    public static string Run()
    {
        root = Path.Combine(Application.temporaryCachePath, "notebook_check");
        string openBefore = NotebookStore.ProjectId, rootBefore = NotebookStore.Root;
        var sb = new StringBuilder();
        bool ok = true;
        try
        {
            ok &= Check(sb, "tool definitions match the handlers", ToolDefinitions);
            ok &= Check(sb, "an attempt is saved to disk", AttemptSaved);
            ok &= Check(sb, "\"that didn't work\" logs a failure, then the lesson", ThatDidntWork);
            ok &= Check(sb, "survives a restart", SurvivesRestart);
            ok &= Check(sb, "outcome rules (a person's mark wins)", OutcomeRules);
            ok &= Check(sb, "runs attach to the open entry", Runs);
            ok &= Check(sb, "an incident is added, outcome unchanged", Incident);
            ok &= Check(sb, "a training run becomes an entry", Training);
            ok &= Check(sb, "versions are cut and pinned", Versions);
            ok &= Check(sb, "damaged files are moved aside", Damaged);
            ok &= Check(sb, "lessons carry across projects; hidden skipped", LessonsCarry);
            ok &= Check(sb, "session-start read fits 2,000 characters", SessionRead);
            ok &= Check(sb, "plan_revise writes a postmortem and keeps the old plan", PlanRevise);
            ok &= Check(sb, "v1 vs v2: free drive labelled, numbers from records", CompareVersions);
            ok &= Check(sb, "writes stay off the main thread", WriteSpeed);
        }
        finally
        {
            NotebookTools.DisconnectTraining();
            NotebookStore.Close();
            NotebookStore.Clock = () => DateTime.UtcNow;
            if (openBefore != null) NotebookStore.Open(openBefore, rootBefore);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return (ok ? "PASS" : "FAIL") + " notebook check\n" + sb;
    }

    delegate string CheckFn(out string note);

    static bool Check(StringBuilder sb, string name, CheckFn fn)
    {
        string failure, note;
        try { failure = fn(out note); }
        catch (Exception e) { failure = e.GetType().Name + ": " + e.Message; note = null; }
        sb.Append(failure == null ? "  ok   " : "  FAIL ").Append(name);
        if (failure != null) sb.Append(": ").Append(failure);
        else if (!string.IsNullOrEmpty(note)) sb.Append(": ").Append(note);
        sb.Append('\n');
        return failure == null;
    }

    // A fresh notebook for one check: an empty scratch folder and a fixed clock.
    static void Fresh(string project = "proj_check")
    {
        NotebookStore.Close();
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var t = new DateTime(2026, 11, 10, 13, 55, 0, DateTimeKind.Utc);
        NotebookStore.Clock = () => t = t.AddSeconds(1);
        NotebookStore.Open(project, root);
    }

    static string EntryPath(string project, string id) => Path.Combine(root, "projects", project, "notebook", id + ".json");

    static JObject OnDisk(string project, string id)
    {
        NotebookStore.Flush();
        string path = EntryPath(project, id);
        return File.Exists(path) ? (JObject)NotebookFiles.Parse(File.ReadAllText(path)) : null;
    }

    static string Tool(Func<JObject, JObject> tool, JObject args)
    {
        try { tool(args); return null; }
        catch (ArgumentException e) { return e.Message; }
    }

    static JObject LogAttempt(string change = "Truck: rear drive.", params string[] tags) =>
        NotebookTools.LogTool(new JObject { ["change"] = change, ["rationale"] = "For the slalom.", ["tags"] = new JArray(tags.Length == 0 ? new[] { "vehicle" } : tags) });

    static string ToolDefinitions(out string note)
    {
        note = null;
        var asset = Resources.Load<TextAsset>("AlbieTools/notebook");
        if (asset == null) return "no Resources/AlbieTools/notebook.json";
        var names = new System.Collections.Generic.List<string>();
        foreach (JObject def in JArray.Parse(asset.text))
        {
            names.Add((string)def["name"]);
            if (def["parameters"]?["type"]?.ToString() != "object") return $"{def["name"]} has no object parameters";
        }
        string expected = "notebook_read,notebook_log,notebook_mark,notebook_lessons,plan_revise,notebook_compare";
        if (string.Join(",", names) != expected) return $"tools are {string.Join(",", names)}, expected {expected}";
        note = names.Count + " tools";
        return null;
    }

    static string AttemptSaved(out string note)
    {
        note = null;
        Fresh();
        var result = LogAttempt("First truck: 4 wheels, rear drive, servo steering.", "vehicle", "stability");
        string id = (string)result["entry"];
        if (id != "ent_0001") return $"first id is {id}";
        var disk = OnDisk("proj_check", id);
        if (disk == null) return "no file after the write queue flushed";
        if ((string)disk["schema"] != NotebookStore.EntrySchema) return "schema " + disk["schema"];
        if ((string)disk["outcome"] != "open" || disk["outcome_by"].Type != JTokenType.Null) return $"starts {disk["outcome"]} by {disk["outcome_by"]}";
        if ((string)disk["rationale_by"] != "albie") return "rationale_by " + disk["rationale_by"];
        if ((string)disk["created_at"] != "2026-11-10T13:55:01Z") return "created_at " + disk["created_at"];
        foreach (string field in new[] { "id", "project_id", "project_version", "kind", "change", "rationale", "rationale_by", "expected", "observed", "outcome", "outcome_by", "lesson", "lesson_id", "tags", "created_at", "updated_at" })
            if (disk[field] == null) return "missing field " + field;
        if (File.Exists(EntryPath("proj_check", id) + ".tmp")) return "a temp file was left behind";
        note = $"{id}, {new FileInfo(EntryPath("proj_check", id)).Length} bytes";
        return null;
    }

    static string ThatDidntWork(out string note)
    {
        note = null;
        Fresh();
        LogAttempt("First truck.", "vehicle", "stability");
        var first = NotebookTools.MarkTool(new JObject { ["outcome"] = "failed" });  // "that didn't work"
        if ((string)first["outcome"] != "failed" || (string)first["outcome_by"] != "person") return $"marked {first["outcome"]} by {first["outcome_by"]}";
        if (first["next"] == null) return "no prompt to ask why";
        var second = NotebookTools.MarkTool(new JObject { ["outcome"] = "failed", ["lesson"] = "Track width under 1.6x the CoG height rolls at this speed." });
        if ((string)second["outcome_by"] != "person") return "the lesson call changed outcome_by to " + second["outcome_by"];
        if ((string)second["lesson_id"] != "les_0001") return "lesson id " + second["lesson_id"];
        if (second["next"] != null) return "still asking why after the lesson";
        var disk = OnDisk("proj_check", "ent_0001");
        if ((string)disk["lesson"] == null || (string)disk["lesson_id"] != "les_0001") return "lesson not on the entry file";
        var lessons = (JArray)NotebookFiles.Parse(File.ReadAllText(Path.Combine(root, "user", "lessons.json")));
        var lesson = (JObject)lessons[0];
        if ((string)lesson["by"] != "albie" || (string)lesson["from"]["entry"] != "ent_0001" || (int)lesson["seen"] != 1) return "lesson record " + lesson.ToString(Newtonsoft.Json.Formatting.None);
        if (string.Join(",", lesson["tags"]) != "vehicle,stability") return "lesson tags " + string.Join(",", lesson["tags"]);
        string albie = Tool(NotebookTools.MarkTool, new JObject { ["outcome"] = "worked", ["by"] = "albie" });
        if (albie == null || !albie.StartsWith("person_marked")) return "Albie overwrote the person's mark: " + albie;
        note = "failed (person), lesson les_0001 (albie); Albie's overwrite refused";
        return null;
    }

    static string SurvivesRestart(out string note)
    {
        note = null;
        Fresh();
        NotebookStore.CutVersion("thing", "First truck.", new JObject { ["things"] = new JObject { ["truck"] = 1 } });
        NotebookTools.MarkTool(new JObject { ["outcome"] = "failed", ["lesson"] = "Narrow trucks roll.", ["tags"] = new JArray("vehicle") });
        LogAttempt("Second try.");
        NotebookStore.AttachRun("run_0001", "completed");
        var before = new JArray();
        foreach (var e in NotebookStore.Entries) before.Add(e.DeepClone());

        NotebookStore.Close();  // the app quits: memory is gone, only the files are left
        if (NotebookStore.IsOpen || NotebookStore.Entries.Count != 0) return "Close left state behind";
        NotebookStore.Open("proj_check", root);

        if (NotebookStore.Entries.Count != before.Count) return $"{NotebookStore.Entries.Count} entries after restart, {before.Count} before";
        for (int i = 0; i < before.Count; i++)
            if (!JToken.DeepEquals(before[i], NotebookStore.Entries[i])) return $"{before[i]["id"]} changed across the restart";
        if (NotebookStore.CurrentVersion() != 1) return "version after restart " + NotebookStore.CurrentVersion();
        if (NotebookStore.Lessons(new[] { "vehicle" }).Count != 1) return "the lesson didn't come back";
        if ((string)NotebookStore.Entry("ent_0001")["outcome"] != "failed") return "the failure didn't come back";
        string next = (string)LogAttempt("Third try.")["entry"];
        if (next != "ent_0003") return "next id after restart is " + next;
        if (NotebookStore.LoadProblems.Count != 0) return "load problems: " + NotebookStore.LoadProblems[0];
        note = $"{before.Count} entries, 1 version and 1 lesson identical after reopening; next id {next}";
        return null;
    }

    static string OutcomeRules(out string note)
    {
        note = null;
        Fresh();
        string a = (string)LogAttempt()["entry"];
        string Expect(string error, string code) => error != null && error.StartsWith(code + ":") ? null : $"expected {code}, got {error ?? "success"}";
        string r;
        if ((r = Expect(NotebookStore.Mark(a, "done", "person"), "bad_outcome")) != null) return r;
        if ((r = Expect(NotebookStore.Mark("ent_0042", "failed", "person"), "unknown_entry")) != null) return r;
        if ((r = Expect(NotebookStore.Mark(a, "failed", "person", tags: new[] { "physics" }), "unknown_tag")) != null) return r;
        if ((r = Expect(NotebookStore.Mark(a, "partly", "run"), "bad_outcome")) != null) return r;
        if ((r = Expect(Tool(NotebookTools.LogTool, new JObject { ["change"] = "x", ["rationale"] = "y", ["tags"] = new JArray("physics") }), "unknown_tag")) != null) return r;
        string untagged = NotebookStore.Log("attempt", "No tags.", null, null, null, null);
        if ((r = Expect(NotebookStore.Mark(untagged, "failed", "person", lesson: "Something."), "lesson_needs_tag")) != null) return r;

        if (NotebookStore.Mark(a, "failed", "run") != null) return "a run couldn't mark an open entry";
        if (NotebookStore.Mark(a, "partly", "albie") != null) return "Albie couldn't overwrite a run";
        if ((r = Expect(NotebookStore.Mark(a, "worked", "run"), "albie_marked")) != null) return r;
        if (NotebookStore.Mark(a, "worked", "person") != null) return "a person couldn't overwrite Albie";
        if ((r = Expect(NotebookStore.Mark(a, "failed", "albie"), "person_marked")) != null) return r;
        if ((r = Expect(NotebookStore.Mark(a, "failed", "run"), "person_marked")) != null) return r;
        if (NotebookStore.Mark(a, "failed", "person") != null) return "a person couldn't change their own mark";
        var e = NotebookStore.Entry(a);
        if ((string)e["outcome"] != "failed" || (string)e["outcome_by"] != "person") return $"ended {e["outcome"]} by {e["outcome_by"]}";
        NotebookStore.Close();
        string noProject = Tool(NotebookTools.MarkTool, new JObject { ["outcome"] = "failed" });
        if ((r = Expect(noProject, "no_project")) != null) return r;
        note = "8 refusals with their codes; run < Albie < person";
        return null;
    }

    static string Runs(out string note)
    {
        note = null;
        Fresh();
        NotebookStore.CutVersion("thing", "Wider truck.", new JObject { ["things"] = new JObject { ["truck"] = 2 } });
        string id = (string)NotebookStore.NewestOpen()["id"];
        if (NotebookStore.AttachRun("run_0001", "failed") != id) return "run 1 went elsewhere";
        if ((string)NotebookStore.Entry(id)["outcome"] != "failed") return "a failed run didn't suggest failed";
        if (NotebookStore.AttachRun("run_0002", "completed") != id) return "run 2 went elsewhere";
        var e = NotebookStore.Entry(id);
        if ((string)e["outcome"] != "worked" || (string)e["outcome_by"] != "run") return $"after a completed run: {e["outcome"]} by {e["outcome_by"]}";
        NotebookStore.AttachRun("run_0003", "aborted");
        if ((string)e["outcome"] != "worked") return "an aborted run changed the outcome";
        NotebookStore.AttachRun("run_0003", "aborted");
        if (((JArray)e["observed"]["runs"]).Count != 3) return "runs " + e["observed"]["runs"].ToString(Newtonsoft.Json.Formatting.None);
        NotebookStore.Mark(id, "failed", "person");  // "that didn't work": the entry is the person's now
        string next = NotebookStore.AttachRun("run_0004", "completed");
        if (next == id) return "a run was added to an entry a person had judged";
        var opened = NotebookStore.Entry(next);
        if ((string)opened["kind"] != "attempt" || (string)opened["outcome"] != "worked" || (int)opened["project_version"] != 1) return "the new attempt is " + opened.ToString(Newtonsoft.Json.Formatting.None);
        note = $"{id}: failed → worked (run), aborted ignored; after a person's mark a run opened {next}";
        return null;
    }

    static string Incident(out string note)
    {
        note = null;
        Fresh();
        string id = (string)LogAttempt("First truck.")["entry"];
        string got = NotebookStore.AddIncident(new JObject { ["kind"] = "rolled_over", ["thing"] = "truck", ["thing_version"] = 1, ["speed_ms"] = 2.1, ["project_id"] = "proj_check" });
        if (got != id) return "incident went to " + got;
        var e = OnDisk("proj_check", id);
        var incident = (JObject)e["observed"]["incidents"][0];
        if ((string)e["outcome"] != "open") return "the incident changed the outcome";
        if ((double)incident["speed_ms"] != 2.1 || incident["at"] == null || incident["project_id"] != null) return "incident " + incident.ToString(Newtonsoft.Json.Formatting.None);
        note = "rolled_over at 2.1 m/s on " + id;
        return null;
    }

    static string Training(out string note)
    {
        note = null;
        Fresh();
        NotebookStore.CutVersion("thing", "Wider truck.");
        NotebookStore.AttachRun("run_0001", "completed");
        // A real training run, the way the Controllers sandbox reports it: candidate 1's block plus every candidate's.
        var laps = BehaviourCloningCheck.DriveLaps(400, 2);
        var block = (JObject)null;
        var candidates = new JArray();
        for (int seed = 1; seed <= 3; seed++)
        {
            var cloner = new BehaviourCloner(laps, new BehaviourCloner.Options { Seed = seed, Epochs = 3 });
            cloner.TrainToEnd();
            if (block == null) block = cloner.TrainingBlock();
            candidates.Add(cloner.TrainingBlock());
        }
        block["candidates"] = candidates;
        NotebookTools.ConnectTraining();
        NotebookTools.ConnectTraining();  // twice: still one entry per run
        int before = NotebookStore.Entries.Count;
        TrainingLog.Report("truck_autopilot", 2, block);
        if (NotebookStore.Entries.Count != before + 1) return $"{NotebookStore.Entries.Count - before} entries for one training run";
        var e = OnDisk("proj_check", (string)NotebookStore.Newest()["id"]);
        var training = (JObject)e["observed"]["training"];
        if ((string)e["kind"] != "training" || (string)e["outcome"] != "open" || (int)e["project_version"] != 1) return $"entry {e["kind"]} {e["outcome"]} v{e["project_version"]}";
        if ((string)training["controller"] != "truck_autopilot" || (int)training["controller_version"] != 2) return "controller fields " + training["controller"] + " " + training["controller_version"];
        foreach (var field in block) if (!JToken.DeepEquals(field.Value, training[field.Key])) return $"training.{field.Key} wasn't copied as it is";
        if (string.Join(",", e["tags"]) != "ml,control") return "tags " + string.Join(",", e["tags"]);
        string noteText = (string)e["observed"]["note"];
        if (noteText == null || !noteText.Contains($"{block["samples"]} samples") || !noteText.Contains("3 candidates")) return "note: " + noteText;
        if ((string)e["change"] != "Trained truck_autopilot on 2 laps.") return "change: " + e["change"];
        // The next run is added to the training entry (demo step 8).
        if (NotebookStore.AttachRun("run_0002", "completed") != (string)e["id"]) return "the Autopilot's run didn't go to the training entry";
        note = $"{e["id"]}: \"{noteText}\"";
        return null;
    }

    static string Versions(out string note)
    {
        note = null;
        Fresh();
        if (NotebookStore.CurrentVersion() != 0) return "a new project is at v" + NotebookStore.CurrentVersion();
        string beforeCut = (string)LogAttempt()["entry"];
        if ((int)NotebookStore.Entry(beforeCut)["project_version"] != 0) return "an entry before the first cut isn't on v0";
        int v1 = NotebookStore.CutVersion("thing", "First truck.", new JObject { ["things"] = new JObject { ["truck"] = 1 }, ["controllers"] = new JObject { ["truck_manual"] = 1 } });
        int v2 = NotebookStore.CutVersion("commit", "Autopilot max_speed raised to 2.2 m/s.", new JObject { ["controllers"] = new JObject { ["truck_autopilot"] = 2 } });
        if (v1 != 1 || v2 != 2) return $"cut v{v1}, v{v2}";
        NotebookStore.Flush();
        var m = (JObject)NotebookFiles.Parse(File.ReadAllText(Path.Combine(root, "projects", "proj_check", "versions", "v0002.json")));
        if ((int)m["parent"] != 1 || (string)m["cut_by"] != "commit") return "manifest " + m["parent"] + " " + m["cut_by"];
        if ((int?)m["things"]["truck"] != 1 || (int?)m["controllers"]["truck_manual"] != 1 || (int?)m["controllers"]["truck_autopilot"] != 2) return "v2 pins " + m.ToString(Newtonsoft.Json.Formatting.None);
        var opened = NotebookStore.NewestOpen();
        if ((int)opened["project_version"] != 2 || (string)opened["change"] != "Autopilot max_speed raised to 2.2 m/s.") return "the cut didn't open an attempt";
        if (NotebookStore.CurrentVersion("proj_check") != 2) return "CurrentVersion(id) " + NotebookStore.CurrentVersion("proj_check");
        try { NotebookStore.CutVersion("slider", "x"); return "a slider drag cut a version"; }
        catch (ArgumentException) { }
        note = "v1 (thing), v2 (commit) with v1's pins carried over; each opened an attempt";
        return null;
    }

    static string Damaged(out string note)
    {
        note = null;
        Fresh();
        LogAttempt("Good one.");
        LogAttempt("Another good one.");
        NotebookStore.Close();
        string folder = Path.Combine(root, "projects", "proj_check", "notebook");
        File.WriteAllText(Path.Combine(folder, "ent_0099.json"), "{ \"schema\": \"albie.notebook.entry/1\", \"id\": ");  // cut off mid-write
        File.WriteAllText(Path.Combine(folder, "ent_0050.json"), "{ \"schema\": \"something.else/1\" }");
        NotebookStore.Open("proj_check", root);
        if (NotebookStore.Entries.Count != 2) return $"{NotebookStore.Entries.Count} entries loaded";
        if (NotebookStore.LoadProblems.Count != 2) return $"{NotebookStore.LoadProblems.Count} problems reported";
        if (File.Exists(Path.Combine(folder, "ent_0099.json")) || !File.Exists(Path.Combine(folder, "damaged", "ent_0099.json"))) return "ent_0099 wasn't moved to damaged/";
        string next = (string)LogAttempt("After the damage.")["entry"];
        if (next != "ent_0100") return "next id " + next + " (an id from a damaged file was reused)";
        NotebookStore.Close();
        NotebookStore.Open("proj_check", root);  // the damaged ids stay used after another restart
        if (NotebookStore.LoadProblems.Count != 0) return "problems came back: " + NotebookStore.LoadProblems[0];
        string after = (string)LogAttempt("Later.")["entry"];
        if (after != "ent_0101") return "id after the second restart " + after;
        Directory.CreateDirectory(Path.Combine(root, "user"));
        File.WriteAllText(Path.Combine(root, "user", "lessons.json"), "[{");
        NotebookStore.Open("proj_check", root);
        if (NotebookStore.LoadProblems.Count != 1 || NotebookStore.Entries.Count != 4) return "a broken lessons.json stopped the notebook";
        note = "2 broken entries + a broken lessons.json moved aside; 4 good entries open; ids never reused";
        return null;
    }

    static string LessonsCarry(out string note)
    {
        note = null;
        Fresh();
        LogAttempt("Narrow truck.", "vehicle", "stability");
        NotebookTools.MarkTool(new JObject { ["outcome"] = "failed", ["lesson"] = "Narrow trucks roll on sharp turns." });
        LogAttempt("Autopilot.", "ml");
        NotebookTools.MarkTool(new JObject { ["outcome"] = "partly", ["lesson"] = "Retrain after a big speed change." });
        NotebookStore.Open("proj_rocket", root);  // a new project, same person
        if (NotebookStore.Entries.Count != 0) return "the new project isn't empty";
        var vehicle = NotebookStore.Lessons(new[] { "vehicle" });
        if (vehicle.Count != 1 || vehicle[0].FromProject != "proj_check") return $"{vehicle.Count} vehicle lessons";
        var tool = (JArray)NotebookTools.LessonsTool(new JObject { ["tags"] = new JArray("ml", "stability") })["lessons"];
        if (tool.Count != 2 || (string)tool[0]["id"] != "les_0002") return "notebook_lessons gave " + tool.ToString(Newtonsoft.Json.Formatting.None);
        if (NotebookStore.SetLessonHidden("les_0001", true) != null) return "couldn't hide a lesson";
        if (NotebookStore.Lessons(new[] { "vehicle" }).Count != 0) return "a hidden lesson is still read";
        if (NotebookStore.AllLessons.Count != 2) return "hiding deleted the record";
        NotebookStore.Open("proj_check", root);
        if (NotebookStore.Lessons(null).Count != 1) return "hidden didn't survive a restart";
        note = "proj_rocket reads proj_check's lessons; a hidden one is kept but not read";
        return null;
    }

    static string SessionRead(out string note)
    {
        note = null;
        Fresh("proj_truck");
        int seeded = NotebookStore.SeedFixtures();
        if (seeded != 5) return $"seeded {seeded} entries from Mocks/notebook";
        if (NotebookStore.SeedFixtures() != 0) return "seeded a notebook that wasn't empty";
        var read = NotebookTools.ReadTool(new JObject());
        int length = read.ToString(Newtonsoft.Json.Formatting.None).Length;
        if (length > 2000) return $"{length} characters";
        if ((int)read["version"] != 3) return "version " + read["version"];
        if (read["plan"] == null || read["plan"].Type == JTokenType.Null || (int)read["plan"]["version"] != 2) return "plan " + read["plan"];
        if ((string)read["say"] == null || !((string)read["say"]).StartsWith("Last time:")) return "say " + read["say"];
        if (((JArray)read["lessons"]).Count != 2) return $"{((JArray)read["lessons"]).Count} lessons (the hidden one must be skipped)";
        if (((JArray)read["entries"]).Count != 5) return $"{((JArray)read["entries"]).Count} entries";
        for (int i = 0; i < 40; i++) LogAttempt("A long change description that goes on and on to fill the session-start read. " + new string('x', 120));
        var full = NotebookTools.ReadTool(new JObject { ["last"] = 20 });
        int fullLength = full.ToString(Newtonsoft.Json.Formatting.None).Length;
        if (fullLength > 2000) return $"{fullLength} characters with 45 entries";
        var entries = (JArray)full["entries"];
        if (entries.Count == 0 || (string)entries[entries.Count - 1]["id"] != "ent_0045") return "the newest entry was dropped";
        note = $"demo story {length} chars; with 45 entries {fullLength} chars, newest {entries.Count} kept";
        return null;
    }

    static string PlanRevise(out string note)
    {
        note = null;
        Fresh();
        string empty = null;
        try { NotebookStore.RevisePlan("No goal yet."); }
        catch (ArgumentException e) { empty = e.Message; }
        if (empty == null || !empty.StartsWith("bad_value:")) return "a first plan with no goal was accepted: " + empty;
        int v1 = NotebookStore.RevisePlan("Start from a slalom around the couch.", "An RC truck that does a slalom around the couch.", "Finish the slalom.");
        if (v1 != 1) return "first plan is v" + v1;
        var first = OnDiskPlan(1);
        if (first == null || (string)first["schema"] != NotebookStore.PlanSchema || first["supersedes"].Type != JTokenType.Null) return "plan v1 " + first;
        if ((string)NotebookStore.Newest()["kind"] != "postmortem") return "no postmortem for the first plan";
        int v2 = NotebookStore.RevisePlan("Chasing speed kept costing cones.", test: "Finish the slalom with no cones hit.");
        if (v2 != 2) return "revision is v" + v2;
        NotebookStore.Flush();
        var kept = OnDiskPlan(1);
        var next = OnDiskPlan(2);
        if ((string)kept["test"] == (string)next["test"]) return "the old plan was edited";
        if ((int)next["supersedes"] != 1 || (string)next["postmortem"] != (string)NotebookStore.Newest()["id"]) return "v2 " + next.ToString(Newtonsoft.Json.Formatting.None);
        if ((string)next["goal"] != (string)kept["goal"]) return "omitted goal wasn't copied";
        var tool = NotebookTools.RevisePlanTool(new JObject { ["reason"] = "The test is a clean lap." });
        if ((int)tool["version"] != 3) return "tool version " + tool;
        note = "v1 written, v2 keeps v1 on disk, v3 through the tool; each opened a postmortem";
        return null;
    }

    static JObject OnDiskPlan(int version)
    {
        NotebookStore.Flush();
        string path = Path.Combine(root, "projects", "proj_check", "plans", $"plan_v{version:0000}.json");
        return File.Exists(path) ? (JObject)NotebookFiles.Parse(File.ReadAllText(path)) : null;
    }

    static string CompareVersions(out string note)
    {
        note = null;
        string libBefore = ArtifactLibrary.RootOverride;
        ArtifactLibrary.RootOverride = Path.Combine(root, "artifacts");
        try
        {
            Fresh("proj_truck");
            if (NotebookStore.SeedFixtures() != 5) return "fixtures didn't seed";
            var cmp = NotebookTools.CompareTool(new JObject { ["from"] = 1, ["to"] = 2 });
            if ((string)cmp["from"]["label"] != "free drive, not this course") return "v1 label " + cmp["from"]["label"];
            if ((string)cmp["from"]["outcome"] != "failed") return "v1 outcome " + cmp["from"]["outcome"];
            if (((JArray)cmp["from"]["incidents"]).Count == 0 || (string)cmp["from"]["incidents"][0]["kind"] != "rolled_over") return "v1 incident missing";
            if (((JArray)cmp["from"]["runs"]).Count != 0) return "v1 shouldn't have a slalom run";
            var toRuns = (JArray)cmp["to"]["runs"];
            if (toRuns.Count < 1) return "v2 has no runs";
            bool sawCompleted = false;
            foreach (var run in toRuns) sawCompleted |= (string)run["outcome"] == "completed";
            if (!sawCompleted) return "v2 runs " + toRuns.ToString(Newtonsoft.Json.Formatting.None);
            string say = (string)cmp["say"];
            if (say == null || !say.Contains("rolled over") || !say.Contains("8.6")) return "say invented or incomplete: " + say;
            string same = Tool(NotebookTools.CompareTool, new JObject { ["from"] = 2, ["to"] = 2 });
            if (same == null || !same.StartsWith("bad_value:")) return "same version: " + same;
            note = say;
            return null;
        }
        finally { ArtifactLibrary.RootOverride = libBefore; }
    }

    static string WriteSpeed(out string note)
    {
        note = null;
        Fresh();
        const int n = 200;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) LogAttempt($"Attempt {i}.");
        double logMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        if (!NotebookStore.Flush()) return "the write queue didn't finish in 5 s";
        double flushMs = watch.Elapsed.TotalMilliseconds;
        int files = Directory.GetFiles(Path.Combine(root, "projects", "proj_check", "notebook"), "*.json").Length;
        if (files != n) return $"{files} files for {n} entries";
        if (Directory.GetFiles(Path.Combine(root, "projects", "proj_check", "notebook"), "*.tmp").Length != 0) return "temp files left behind";
        if (NotebookFiles.LastError != null) return NotebookFiles.LastError;
        double per = logMs / n;
        if (per > 1.0) return $"{per:0.000} ms per entry on the main thread";
        note = $"{per:0.000} ms per entry on the main thread; the queue wrote {n} files in another {flushMs:0} ms";
        return null;
    }
}
