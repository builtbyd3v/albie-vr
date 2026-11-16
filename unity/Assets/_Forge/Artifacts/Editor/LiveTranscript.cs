using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor-only live-test recorder: while it runs (Play mode), every voice and artifact event the app sees goes into a
/// JSONL file, one object per line: <c>{"t": seconds since start, "utc", "type", "payload"}</c>. Events come through
/// <see cref="ForgeNet.On(string, Action{JToken}, bool)"/> (no socket of its own): the user's turn (stt.*), the tool
/// calls, Albie's partial and final answers, errors, speech start/end, pet state and the artifact events. The log lines
/// of <c>[AlbieVoice]</c> (tool results with call ids, what he said), <c>[Artifacts]</c>, <c>[VoiceTurn]</c> and
/// <c>[ArtifactShot]</c> go in too, as type <c>log</c>, so a silent turn can be explained afterwards.
/// <para>It never writes inside the project: <see cref="Start"/> refuses a folder under it. The menu uses EditorPrefs
/// <see cref="DirPref"/>, or the system temp folder. It stops itself when Play mode ends.</para>
/// </summary>
public static class LiveTranscript
{
    public const string DirPref = "Albie.LiveTranscriptDir";

    /// <summary>The ForgeNet events recorded.</summary>
    public static readonly string[] Events =
    {
        "stt.start", "stt.partial", "stt.final", "stt.cancel",
        "agent.thinking", "agent.tool_call", "agent.partial_answer", "agent.answer_done", "agent.error",
        "tts.start", "tts.end", "tts.stop", "pet.state", "voice.ready",
        "artifact.layout", "artifact.run", "artifact.updated",
    };

    static readonly string[] LogTags = { "[AlbieVoice]", "[Artifacts]", "[VoiceTurn]", "[ArtifactShot]" };

    static readonly List<IDisposable> subscriptions = new List<IDisposable>();
    static StreamWriter writer;
    static double started;
    static int lines;

    /// <summary>The file being written, or null.</summary>
    public static string Path { get; private set; }

    [MenuItem("Forge/Artifacts/Live/Start Transcript")]
    static void StartMenu() => Debug.Log("[LiveTranscript] " + Start(EditorPrefs.GetString(DirPref, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "albie-live"))));

    [MenuItem("Forge/Artifacts/Live/Stop Transcript")]
    static void StopMenu() => Debug.Log("[LiveTranscript] " + Stop());

    /// <summary>Starts a new file in <paramref name="dir"/> (Play mode only). Returns its path, or why it didn't start.</summary>
    public static string Start(string dir)
    {
        if (!Application.isPlaying) return "FAIL not in Play mode";
        if (string.IsNullOrEmpty(dir)) return "FAIL no folder";
        string full = System.IO.Path.GetFullPath(dir);
        string project = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", ".."));
        if (Inside(full, project)) return "FAIL refusing to write inside the project (" + project + "); pick a folder outside it";
        Stop();
        Directory.CreateDirectory(full);
        Path = System.IO.Path.Combine(full, "live_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".jsonl");
        writer = new StreamWriter(Path, true) { AutoFlush = true };
        started = EditorApplication.timeSinceStartup;
        lines = 0;
        foreach (string type in Events)
        {
            string t = type;
            subscriptions.Add(ForgeNet.On(t, payload => Write(t, payload)));
        }
        Application.logMessageReceived += OnLog;
        EditorApplication.playModeStateChanged += OnPlayMode;
        Write("recorder.start", new JObject { ["events"] = new JArray(Events), ["forgenet"] = ForgeNet.Status.ToString() });
        return Path;
    }

    /// <summary>Stops recording. Returns the file and its line count.</summary>
    public static string Stop()
    {
        if (writer == null) return "not recording";
        Write("recorder.stop", new JObject());
        foreach (var s in subscriptions) s.Dispose();
        subscriptions.Clear();
        Application.logMessageReceived -= OnLog;
        EditorApplication.playModeStateChanged -= OnPlayMode;
        writer.Dispose();
        writer = null;
        string done = $"{Path} ({lines} lines)";
        Path = null;
        return done;
    }

    /// <summary>The file and its line count so far, or "not recording".</summary>
    public static string Status() => writer == null ? "not recording" : $"{Path} ({lines} lines)";

    static void OnPlayMode(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.ExitingPlayMode) Stop();
    }

    static void OnLog(string message, string stack, LogType type)
    {
        if (message == null) return;
        foreach (string tag in LogTags)
            if (message.StartsWith(tag, StringComparison.Ordinal))
            {
                Write("log", new JObject { ["level"] = type.ToString(), ["text"] = message });
                return;
            }
    }

    static void Write(string type, JToken payload)
    {
        if (writer == null) return;
        var line = new JObject
        {
            ["t"] = Math.Round(EditorApplication.timeSinceStartup - started, 3),
            ["utc"] = DateTime.UtcNow.ToString("o"),
            ["type"] = type,
            ["payload"] = payload,
        };
        writer.WriteLine(line.ToString(Formatting.None));
        lines++;
    }

    internal static bool InsideForCheck(string path, string root) => Inside(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(root));

    static bool Inside(string path, string root)
    {
        string sep = System.IO.Path.DirectorySeparatorChar.ToString();
        string p = path.TrimEnd('/', '\\') + sep, r = root.TrimEnd('/', '\\') + sep;
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}
