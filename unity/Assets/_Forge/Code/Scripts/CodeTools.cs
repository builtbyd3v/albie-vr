using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Albie's code tools on the headset, on the sample project in <see cref="CodeProject"/>; the device's take on
/// hub/vscode/tools.py (open, highlight, errors) plus the hub's Claude runner (hub/agent/runner.py) for questions and
/// edits. Definitions: Resources/AlbieTools/code.json. code_ask and code_edit call Claude through
/// <see cref="ClaudeClient"/>: they return "working" at once, and the answer reaches Albie's voice as an
/// <c>albie.say</c> event (AlbieVoice speaks it) and the panel's status line.
/// </summary>
public static class CodeTools
{
    /// <summary>The last finished code_ask / code_edit (for tests and logs): {tool, ok, say, ...}.</summary>
    public static JObject LastResult { get; private set; }
    public static bool Busy { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        LastResult = null;
        Busy = false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Register() => AlbieTools.Register("code", new Dictionary<string, AlbieTools.Handler>
    {
        ["code_list_files"] = _ => ListFiles(),
        ["code_open_file"] = OpenFile,
        ["code_highlight"] = Highlight,
        ["code_get_errors"] = GetErrors,
        ["code_ask"] = Ask,
        ["code_edit"] = Edit,
        ["code_undo"] = Undo,
        ["code_reset"] = _ => { CodeProject.Reset(); return new JObject { ["ok"] = true, ["note"] = "all files are back to the original sample" }; },
    });

    static JObject ListFiles()
    {
        var list = new JArray();
        foreach (var f in CodeProject.Files)
            list.Add(new JObject
            {
                ["path"] = f.path, ["lines"] = CodeProject.LineCount(f), ["problems"] = CodeProject.Check(f).Count,
                ["edited"] = f.text != f.original,
            });
        return new JObject { ["project"] = CodeProject.Name, ["files"] = list, ["open"] = CodeProject.Open?.path };
    }

    static JObject OpenFile(JObject args)
    {
        var f = CodeProject.Find((string)args["path"]);
        int line = Mathf.Clamp(args.Value<int?>("line") ?? 1, 1, CodeProject.LineCount(f));
        OpenView();
        CodeProject.Show(f, args["line"] != null ? line : 0, 0);
        return new JObject { ["opened"] = f.path, ["line"] = line, ["lines"] = CodeProject.LineCount(f) };
    }

    static JObject Highlight(JObject args)
    {
        var f = CodeProject.Find((string)args["path"]);
        int lines = CodeProject.LineCount(f);
        int start = args.Value<int?>("start") ?? 0;
        if (start < 1 || start > lines) throw new ArgumentException($"{f.path} has lines 1 to {lines}");
        int end = Mathf.Clamp(args.Value<int?>("end") ?? start, start, lines);
        OpenView();
        CodeProject.Show(f, start, end, (string)args["note"]);
        return new JObject { ["highlighted"] = f.path, ["start"] = start, ["end"] = end };
    }

    static JObject GetErrors(JObject args)
    {
        string path = (string)args["path"];
        var list = new JArray();
        var files = string.IsNullOrWhiteSpace(path) ? new List<CodeProject.File>(CodeProject.Files) : new List<CodeProject.File> { CodeProject.Find(path) };
        foreach (var f in files)
            foreach (var p in CodeProject.Check(f))
                list.Add(new JObject { ["file"] = p.file, ["line"] = p.line, ["severity"] = "error", ["message"] = p.message });
        var result = new JObject { ["errors"] = list };
        if (list.Count == 0) result["note"] = "the check finds no problems";
        return result;
    }

    static JObject Undo(JObject args)
    {
        var f = CodeProject.Find((string)args["path"]);
        if (!CodeProject.Undo(f)) throw new ArgumentException($"there's nothing to undo in {f.path}");
        return new JObject { ["undone"] = f.path, ["undo_left"] = f.undo.Count };
    }

    // ---------------------------------------------------------------- Claude

    static readonly JObject AskSchema = JObject.Parse(@"{""type"":""object"",""properties"":{
        ""answer"":{""type"":""string""},""start_line"":{""type"":""integer""},""end_line"":{""type"":""integer""}},
        ""required"":[""answer"",""start_line"",""end_line""],""additionalProperties"":false}");

    static readonly JObject EditSchema = JObject.Parse(@"{""type"":""object"",""properties"":{
        ""summary"":{""type"":""string""},""text"":{""type"":""string""}},
        ""required"":[""summary"",""text""],""additionalProperties"":false}");

    const string AskSystem =
        "You are Albie, a small gator who helps an engineer with code in a mixed-reality workspace. Answer the question " +
        "about the file in at most two short spoken sentences (under 35 words): plain words, no markdown, never read code " +
        "aloud. start_line and end_line are the lines your answer is about (0 and 0 if none).";

    const string EditSystem =
        "You edit one file of a small Python project on the user's request. Make the smallest change that does it and keep " +
        "every other line exactly as it is. text is the whole new file, without the added line numbers. summary is one short spoken sentence (under 20 " +
        "words) saying what you changed, no markdown.";

    static JObject Ask(JObject args)
    {
        string question = ((string)args["question"] ?? "").Trim();
        if (question.Length == 0) throw new ArgumentException("the question is empty");
        var f = CodeProject.Find((string)args["path"]);
        Start($"Reading {f.path}...", () => AskAsync(f, question));
        return new JObject { ["status"] = "working", ["file"] = f.path, ["note"] = "Reading the file; the answer arrives as a system note in a few seconds." };
    }

    static JObject Edit(JObject args)
    {
        string instruction = ((string)args["instruction"] ?? "").Trim();
        if (instruction.Length == 0) throw new ArgumentException("the instruction is empty");
        var f = CodeProject.Find((string)args["path"]);
        Start($"Editing {f.path}...", () => EditAsync(f, instruction));
        return new JObject { ["status"] = "working", ["file"] = f.path, ["note"] = "Making the change; a system note says what changed in a few seconds." };
    }

    static void Start(string status, Func<Task<JObject>> work)
    {
        if (Busy) throw new ArgumentException("still working on the last code request; try again in a moment");
        Busy = true;
        OpenView();
        CodeProject.SetStatus(status);
        _ = Run(work);
    }

    static async Task Run(Func<Task<JObject>> work)
    {
        JObject result;
        try { result = await work(); }
        catch (Exception e)
        {
            Debug.LogWarning($"[CodeTools] {e.Message}");
            result = new JObject { ["ok"] = false, ["say"] = "That didn't work: " + e.Message + "." };
        }
        Busy = false;
        LastResult = result;
        CodeProject.SetStatus((string)result["say"]);
        Debug.Log($"[CodeTools] done: {result.ToString(Newtonsoft.Json.Formatting.None)}");
        Say((string)result["say"]);
    }

    static async Task<JObject> AskAsync(CodeProject.File f, string question)
    {
        var answer = await ClaudeClient.Json(AskSystem, Prompt(f) + "\nQuestion: " + question, AskSchema, 1024);
        string say = ((string)answer["answer"] ?? "").Trim();
        int start = answer.Value<int?>("start_line") ?? 0, end = answer.Value<int?>("end_line") ?? 0;
        if (start > 0 && CodeProject.Open == f) CodeProject.Show(f, start, end);
        return new JObject { ["tool"] = "code_ask", ["ok"] = true, ["file"] = f.path, ["say"] = say, ["start"] = start, ["end"] = end };
    }

    static async Task<JObject> EditAsync(CodeProject.File f, string instruction)
    {
        string before = f.text;
        var answer = await ClaudeClient.Json(EditSystem, Prompt(f) + "\nChange: " + instruction, EditSchema, 4096);
        string text = (string)answer["text"];
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("the edit came back empty");
        if (f.text != before) throw new InvalidOperationException($"{f.path} changed while I worked; ask again");
        var (start, end) = CodeProject.Write(f, text, "changed by Albie");
        string say = start == 0 ? "Nothing needed to change." : ((string)answer["summary"] ?? "Done.").Trim();
        return new JObject
        {
            ["tool"] = "code_edit", ["ok"] = start > 0, ["file"] = f.path, ["say"] = say, ["start"] = start, ["end"] = end,
            ["problems"] = CodeProject.Check(f).Count,
        };
    }

    // The file (numbered) and what the built-in check says about it: small enough for one short call.
    internal static string Prompt(CodeProject.File f)
    {
        var problems = CodeProject.Check(f);
        string checks = problems.Count == 0 ? "none" : string.Join("; ", problems.ConvertAll(p => $"line {p.line}: {p.message}"));
        return $"Project {CodeProject.Name}, file {f.path} (line numbers added, not part of the file):\n{CodeProject.Numbered(f)}\nProblems found by the check: {checks}\n";
    }

    // ---------------------------------------------------------------- the desk and the voice

    /// <summary>Open the code view if the user is at the desk and another view (or none) is showing.</summary>
    internal static void OpenView()
    {
        var ws = Workspace.Current;
        if (ws != null && ws.ActiveMode != "code" && ws.presence != null && ws.presence.AtDesk) ws.Open("code");
    }

    /// <summary>Hub off: have Albie say this now (AlbieVoice, the local peer). Hub live: the hub speaks for itself.</summary>
    internal static void Say(string text)
    {
        if (string.IsNullOrEmpty(text) || ForgeNet.Status == ForgeNetStatus.Live || ForgeNet.LocalPeer == null) return;
        ForgeNet.Send("albie.say", new JObject { ["text"] = text });
    }
}
