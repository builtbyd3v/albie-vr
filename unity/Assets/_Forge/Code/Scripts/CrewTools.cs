using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Albie's crew on the headset: the device's take on hub/agent/crew.py. Definitions: Resources/AlbieTools/crew.json.
/// Agents hatch and leave through <see cref="AlbieCrew"/>. A task is one <see cref="ClaudeClient.Json"/> call over the
/// whole sample project that may change one file (through <see cref="CodeProject.Write"/>, so code_undo takes it
/// back). Captions: agent.task_started / agent.task_complete / agent.error injected with the crew member's pet_id, as
/// if the hub sent them; Albie speaks the result (<see cref="CodeTools.Say"/>).
/// </summary>
public static class CrewTools
{
    /// <summary>The last finished task (for tests and logs): {agent, ok, say, file?, start?, end?}.</summary>
    public static JObject LastResult { get; private set; }

    // pet_id -> the running task (one per Albie); dismissing removes it, so a late answer is dropped.
    static readonly Dictionary<string, (string id, string title)> running = new Dictionary<string, (string, string)>();
    static int taskCount;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        LastResult = null;
        running.Clear();
        taskCount = 0;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Register() => AlbieTools.Register("crew", new Dictionary<string, AlbieTools.Handler>
    {
        ["spawn_agent"] = args => { var m = Spawn((string)args["type"]); return new JObject { ["hatched"] = m.PetId, ["name"] = m.Type.Name, ["role"] = m.Type.Role }; },
        ["assign_task"] = Assign,
        ["dismiss_agent"] = Dismiss,
        ["crew_status"] = _ => Status(),
    });

    static AlbieCrew Crew => AlbieCrew.Instance != null ? AlbieCrew.Instance : throw new ArgumentException("the crew isn't available here");

    static AlbieAgentType TypeOf(string idOrName)
    {
        var t = AlbieAgentTypes.Find((idOrName ?? "").Trim());
        if (t == null) throw new ArgumentException($"no crew member '{idOrName}'; the types are {Types()}");
        return t;
    }

    static string Types()
    {
        var names = new StringBuilder();
        foreach (var t in AlbieAgentTypes.All) names.Append(names.Length > 0 ? ", " : "").Append(t.Id);
        return names.ToString();
    }

    static AlbieCrewMember Spawn(string type)
    {
        var m = Crew.Spawn(TypeOf(type));
        if (m == null) throw new ArgumentException("couldn't hatch an agent here");
        return m;
    }

    static JObject Dismiss(JObject args)
    {
        var t = TypeOf((string)args["agent"]);
        if (!Crew.Dismiss(t.Id)) throw new ArgumentException($"{t.Name} isn't on the crew");
        bool wasWorking = running.Remove(t.Id);
        return new JObject { ["dismissed"] = t.Id, ["stopped_task"] = wasWorking };
    }

    static JObject Status()
    {
        var crew = new JArray();
        var free = new JArray();
        foreach (var t in AlbieAgentTypes.All)
        {
            if (AlbieCrew.Instance == null || !AlbieCrew.Instance.IsHatched(t)) { free.Add(t.Id); continue; }
            crew.Add(new JObject { ["agent"] = t.Id, ["name"] = t.Name, ["role"] = t.Role, ["task"] = running.TryGetValue(t.Id, out var job) ? job.title : null });
        }
        return new JObject { ["crew"] = crew, ["available_types"] = free };
    }

    // ---------------------------------------------------------------- tasks

    static readonly JObject TaskSchema = JObject.Parse(@"{""type"":""object"",""properties"":{
        ""summary"":{""type"":""string""},""path"":{""type"":""string""},""text"":{""type"":""string""}},
        ""required"":[""summary"",""path"",""text""],""additionalProperties"":false}");

    static JObject Assign(JObject args)
    {
        string task = ((string)args["task"] ?? "").Trim();
        if (task.Length == 0) throw new ArgumentException("the task is empty");
        var m = Spawn((string)args["agent"]);
        if (running.TryGetValue(m.PetId, out var busy)) throw new ArgumentException($"{m.Type.Name} is still on '{busy.title}'");
        string id = m.PetId + "-" + ++taskCount;
        string title = task.Length > 60 ? task.Substring(0, 57) + "..." : task;
        running[m.PetId] = (id, title);
        ForgeNet.Inject("agent.task_started", new JObject { ["pet_id"] = m.PetId, ["task_id"] = id, ["title"] = title });
        _ = Run(m.Type, id, task, (string)args["mode"] == "qa");
        return new JObject { ["status"] = "working", ["agent"] = m.PetId, ["task_id"] = id, ["note"] = $"{m.Type.Name} is on it; a system note says what it did in a few seconds." };
    }

    static async Task Run(AlbieAgentType t, string id, string task, bool answerOnly)
    {
        JObject result;
        try { result = await Work(t, task, answerOnly); }
        catch (Exception e)
        {
            Debug.LogWarning($"[CrewTools] {t.Id}: {e.Message}");
            result = new JObject { ["ok"] = false, ["say"] = $"{t.Name} got stuck: {e.Message}." };
        }
        // Dismissed (or replaced) while Claude worked: Albie is gone, drop the answer.
        if (!running.TryGetValue(t.Id, out var job) || job.id != id) return;
        running.Remove(t.Id);
        result["agent"] = t.Id;
        LastResult = result;
        Debug.Log($"[CrewTools] done: {result.ToString(Newtonsoft.Json.Formatting.None)}");
        string say = (string)result["say"];
        if ((bool)result["ok"]) ForgeNet.Inject("agent.task_complete", new JObject { ["pet_id"] = t.Id, ["task_id"] = id, ["summary"] = say });
        else ForgeNet.Inject("agent.error", new JObject { ["pet_id"] = t.Id, ["task_id"] = id, ["message"] = say });
        CodeTools.Say(say);
    }

    static async Task<JObject> Work(AlbieAgentType t, string task, bool answerOnly)
    {
        var project = new StringBuilder();
        var before = new Dictionary<CodeProject.File, string>();
        foreach (var f in CodeProject.Files) { project.Append(CodeTools.Prompt(f)).Append('\n'); before[f] = f.text; }
        string system =
            $"You are {t.Name}, a helper albie who {t.Role}, working on a small project for the user. Do the task. " +
            (answerOnly ? "Answer only: path and text are empty. "
                        : "If it needs a code change, path is the one file to change and text is its whole new content without " +
                          "the added line numbers: the smallest change, every other line kept exactly. Otherwise path and text are empty. ") +
            $"summary is one short spoken sentence (under 20 words) starting with \"{t.Name}\", saying what you did or found, no markdown.";
        // ponytail: one file per task and one call, no retries: cheap and enough for the sample project.
        var answer = await ClaudeClient.Json(system, project + "Task: " + task, TaskSchema, 2048);
        string say = ((string)answer["summary"] ?? "").Trim();
        if (say.Length == 0) say = t.Name + " is done.";
        string path = ((string)answer["path"] ?? "").Trim(), text = (string)answer["text"];
        if (answerOnly || path.Length == 0 || string.IsNullOrWhiteSpace(text)) return new JObject { ["ok"] = true, ["say"] = say };

        var file = CodeProject.Find(path);
        if (file.text != before[file]) throw new InvalidOperationException($"{file.path} changed while it worked; ask again");
        CodeTools.OpenView();
        var (start, end) = CodeProject.Write(file, text, "changed by " + t.Name);
        if (start == 0) say = t.Name + " didn't need to change anything.";
        return new JObject { ["ok"] = true, ["say"] = say, ["file"] = file.path, ["start"] = start, ["end"] = end };
    }
}
