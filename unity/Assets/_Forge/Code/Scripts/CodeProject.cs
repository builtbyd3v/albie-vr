using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// The sample code that ships in the app, one small set of files per starter project (Resources/CodeSample.json,
/// <c>projects</c> by project id: the truck's autopilot, the quadcopter's hover test, the rocket's stability check with
/// one deliberate "try: fix the error" typo, the classifier's training), so the code panel works with no laptop. It
/// follows the open project (<see cref="ProjectRepos.CurrentProject"/>); <see cref="SyncProject"/> switches the set.
/// Edits are saved on the device under persistentDataPath/code/&lt;project&gt;/;
/// <see cref="Reset"/> brings back the originals. Also holds what the panel shows: the open file, the highlighted
/// lines and a status line. <see cref="Changed"/> fires on any of it. Main thread only.
/// </summary>
public static class CodeProject
{
    public sealed class File
    {
        public string path, original, text;
        public readonly List<string> undo = new List<string>();
    }

    public struct Problem
    {
        public string file;
        public int line;
        public string message;
    }

    const int MaxUndo = 10;
    static readonly List<File> files = new List<File>();
    static string project = "rc-truck";
    static string loadedFor;   // the project id whose files are loaded
    static bool switched;      // the set changed since the panel last rebuilt its file list
    static File open;

    public static event Action Changed;

    public static string Name { get { Load(); return project; } }
    public static IReadOnlyList<File> Files { get { Load(); return files; } }
    public static File Open { get { Load(); return open; } private set => open = value; }
    public static int HighlightStart { get; private set; }  // 1-based, 0 = none
    public static int HighlightEnd { get; private set; }
    public static string Note { get; private set; } = "";
    public static string Status { get; private set; } = "";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        files.Clear();
        open = null;
        loadedFor = null;
        switched = false;
        HighlightStart = HighlightEnd = 0;
        Note = Status = "";
        Changed = null;
    }

    static string Folder => Path.Combine(Application.persistentDataPath, "code", project);

    /// <summary>Loads the open project's sample files if another project's are loaded. Cheap; the panel calls it every frame.</summary>
    public static void SyncProject() => Load();

    /// <summary>True once after the sample switched to another project's files (the panel rebuilds its file list).</summary>
    public static bool TakeSwitched()
    {
        bool was = switched;
        switched = false;
        return was;
    }

    static void Load()
    {
        string want = ProjectRepos.CurrentProject;
        if (files.Count > 0 && want == loadedFor) return;
        var asset = Resources.Load<TextAsset>("CodeSample");
        if (asset == null)
        {
            if (files.Count == 0) Debug.LogError("[CodeProject] no Resources/CodeSample.json");
            return;
        }
        var json = JObject.Parse(asset.text);
        var projects = json["projects"] as JObject;
        var set = projects?[want ?? ""] as JObject ?? projects?[(string)json["fallback"] ?? ""] as JObject ?? json;
        bool reload = files.Count > 0;
        files.Clear();
        loadedFor = want;
        project = (string)set["project"] ?? project;
        foreach (JObject f in set["files"])
        {
            var file = new File { path = (string)f["path"], original = ((string)f["text"]).Replace("\r\n", "\n") };
            string saved = Path.Combine(Folder, file.path);
            file.text = System.IO.File.Exists(saved) ? System.IO.File.ReadAllText(saved) : file.original;
            files.Add(file);
        }
        open = files.Count > 0 ? files[0] : null;
        if (!reload) return;
        switched = true;
        HighlightStart = HighlightEnd = 0;
        Note = Status = "";
        Changed?.Invoke();
    }

    /// <summary>A file by path or name (case-insensitive, "rocket" finds rocket.py); empty = the open file.</summary>
    public static File Find(string path)
    {
        Load();
        path = (path ?? "").Trim().Replace('\\', '/');
        if (path.Length == 0) return Open ?? throw new ArgumentException("no file is open");
        string name = Path.GetFileName(path);
        foreach (var f in files) if (string.Equals(f.path, path, StringComparison.OrdinalIgnoreCase)) return f;
        foreach (var f in files) if (string.Equals(f.path, name, StringComparison.OrdinalIgnoreCase)) return f;
        foreach (var f in files) if (string.Equals(Path.GetFileNameWithoutExtension(f.path), name, StringComparison.OrdinalIgnoreCase)) return f;
        throw new ArgumentException($"no file '{path}' in {project}; the files are {string.Join(", ", files.ConvertAll(f => f.path))}");
    }

    public static int LineCount(File f) => f.text.Split('\n').Length;

    public static void Show(File f, int start = 0, int end = 0, string note = null)
    {
        Open = f;
        int lines = LineCount(f);
        HighlightStart = Mathf.Clamp(start, 0, lines);
        HighlightEnd = HighlightStart == 0 ? 0 : Mathf.Clamp(Mathf.Max(end, start), HighlightStart, lines);
        Note = note ?? "";
        Changed?.Invoke();
    }

    public static void SetStatus(string text)
    {
        Status = text ?? "";
        Changed?.Invoke();
    }

    /// <summary>Replace a file's text (saved on the device, undo kept); opens it with the changed lines highlighted.
    /// Returns the changed range in the new text (1-based; start 0 = nothing changed).</summary>
    public static (int start, int end) Write(File f, string text, string note = "changed")
    {
        text = (text ?? "").Replace("\r\n", "\n");
        var (start, end) = ChangedLines(f.text, text);
        if (start == 0) return (0, 0);
        f.undo.Add(f.text);
        if (f.undo.Count > MaxUndo) f.undo.RemoveAt(0);
        f.text = text;
        Save(f);
        Show(f, start, end, note);
        return (start, end);
    }

    public static bool Undo(File f)
    {
        if (f.undo.Count == 0) return false;
        string before = f.text;
        f.text = f.undo[f.undo.Count - 1];
        f.undo.RemoveAt(f.undo.Count - 1);
        Save(f);
        var (start, end) = ChangedLines(before, f.text);
        Show(f, start, end, "undone");
        return true;
    }

    /// <summary>Every file back to the version that shipped with the app; the saved edits are deleted.</summary>
    public static void Reset()
    {
        Load();
        try { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); }
        catch (IOException e) { Debug.LogWarning($"[CodeProject] reset: {e.Message}"); }
        foreach (var f in files)
        {
            f.text = f.original;
            f.undo.Clear();
        }
        Status = "Back to the original files.";
        Show(Open ?? files[0]);  // Open loads the set first
    }

    static void Save(File f)
    {
        string path = Path.Combine(Folder, f.path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path, f.text);
        }
        catch (IOException e) { Debug.LogWarning($"[CodeProject] can't save {f.path}: {e.Message}"); }
    }

    // First and last differing line of `after` (common prefix and suffix trimmed). Pure deletion: the line after it.
    static (int, int) ChangedLines(string before, string after)
    {
        if (before == after) return (0, 0);
        string[] a = before.Split('\n'), b = after.Split('\n');
        int top = 0;
        while (top < a.Length && top < b.Length && a[top] == b[top]) top++;
        int tail = 0;
        while (tail < a.Length - top && tail < b.Length - top && a[a.Length - 1 - tail] == b[b.Length - 1 - tail]) tail++;
        int start = top + 1, end = b.Length - tail;
        if (end < start) end = start = Mathf.Min(start, b.Length);
        return (start, end);
    }

    /// <summary>The file with line numbers ("12| text"), for prompts.</summary>
    public static string Numbered(File f)
    {
        var sb = new StringBuilder();
        string[] lines = f.text.Split('\n');
        for (int i = 0; i < lines.Length; i++) sb.Append(i + 1).Append("| ").Append(lines[i]).Append('\n');
        return sb.ToString();
    }

    // ---------------------------------------------------------------- the built-in check

    /// <summary>
    /// A small Python check, no interpreter on the device: brackets that don't match, a block line (def, if, for...)
    /// without its colon, and tabs mixed with spaces. ponytail: catches typos like the demo's, not type or name errors.
    /// </summary>
    public static List<Problem> Check(File f)
    {
        var found = new List<Problem>();
        if (!f.path.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) return found;
        var open = new Stack<(char ch, int line)>();
        string[] lines = f.text.Split('\n');
        string triple = null;  // inside a """ or ''' string
        bool tabs = false, spaces = false;
        for (int n = 0; n < lines.Length; n++)
        {
            string line = lines[n];
            var code = new StringBuilder();
            var top = new StringBuilder();  // what's outside brackets: a block line's colon must be there
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (triple != null)
                {
                    if (string.CompareOrdinal(line, i, triple, 0, 3) == 0) { i += 2; triple = null; code.Append("\"\""); }
                    continue;
                }
                if (c == '#') break;
                if (c == '"' || c == '\'')
                {
                    if (i + 2 < line.Length && line[i + 1] == c && line[i + 2] == c) { triple = new string(c, 3); i += 2; continue; }
                    int j = i + 1;
                    while (j < line.Length && line[j] != c) j += line[j] == '\\' ? 2 : 1;
                    i = j;
                    code.Append("\"\"");
                    continue;
                }
                code.Append(c);
                if (open.Count == 0) top.Append(c);
                if (c == '(' || c == '[' || c == '{') open.Push((c, n + 1));
                else if (c == ')' || c == ']' || c == '}')
                {
                    char want = c == ')' ? '(' : c == ']' ? '[' : '{';
                    if (open.Count == 0 || open.Peek().ch != want)
                        found.Add(new Problem { file = f.path, line = n + 1, message = $"unmatched '{c}'" });
                    else open.Pop();
                }
            }
            if (triple != null && code.Length == 0) continue;
            string body = line.TrimStart();
            if (body.Length > 0 && body.Length < line.Length)
            {
                string indent = line.Substring(0, line.Length - body.Length);
                if (indent.Contains("\t")) tabs = true;
                if (indent.Contains(" ")) spaces = true;
            }
            string stmt = code.ToString().Trim();
            if (open.Count == 0 && stmt.Length > 0 && !stmt.EndsWith("\\") && StartsBlock(stmt) && top.ToString().IndexOf(':') < 0)
                found.Add(new Problem { file = f.path, line = n + 1, message = "expected ':' at the end of the line" });
        }
        foreach (var (ch, line) in open)
            found.Add(new Problem { file = f.path, line = line, message = $"'{ch}' is never closed" });
        if (tabs && spaces) found.Add(new Problem { file = f.path, line = 1, message = "tabs and spaces are mixed in the indentation" });
        found.Sort((x, y) => x.line.CompareTo(y.line));
        return found;
    }

    static readonly string[] BlockWords = { "def ", "class ", "if ", "elif ", "else", "for ", "while ", "try", "except", "finally", "with " };

    static bool StartsBlock(string stmt)
    {
        foreach (string w in BlockWords)
        {
            if (!stmt.StartsWith(w, StringComparison.Ordinal)) continue;
            if (w.EndsWith(" ")) return true;
            // else / try / finally / except stand alone or are followed by a space or a colon.
            return stmt.Length == w.Length || stmt[w.Length] == ' ' || stmt[w.Length] == ':';
        }
        return false;
    }
}
