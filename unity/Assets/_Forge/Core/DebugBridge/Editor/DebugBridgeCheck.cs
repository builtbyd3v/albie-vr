using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Forge > Core > Check Debug Bridge: the Main scene in Play mode (desktop rig, voice off, so it costs nothing), a real
/// TcpClient on 127.0.0.1:<see cref="DebugBridge.ActivePort"/>, one PASS/FAIL line per check. Then leaves Play and enters it
/// again, to prove the listener was shut down and the port comes back. Batch (exits 1 on any failure):
/// <c>Unity -batchmode -projectPath unity -executeMethod DebugBridgeCheck.RunBatch -logFile -</c>
/// The client runs on a worker thread: the bridge answers from the main thread, which must keep ticking.
/// </summary>
[InitializeOnLoad]
public static class DebugBridgeCheck
{
    const string PhaseKey = "DebugBridgeCheck.Phase", BatchKey = "DebugBridgeCheck.Batch", FailedKey = "DebugBridgeCheck.Failed";
    const string ScenePath = "Assets/_Forge/Core/Main.unity";
    const double GiveUpSeconds = 180;

    static Task<int> client;
    static double startedAt;

    static DebugBridgeCheck()
    {
        if (SessionState.GetInt(PhaseKey, 0) > 0) EditorApplication.playModeStateChanged += OnPlayMode;
    }

    public static void RunBatch()
    {
        SessionState.SetBool(BatchKey, true);
        Run();
    }

    [MenuItem("Forge/Core/Check Debug Bridge")]
    public static void Run()
    {
        EditorPrefs.SetBool("DebugBridgeCheck.ForceDesktopWas", EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false));
        EditorPrefs.SetBool("DebugBridgeCheck.VoiceOffWas", EditorPrefs.GetBool(AlbieVoice.EditorOffPref, false));
        EditorPrefs.SetBool(RigSelector.ForceDesktopPref, true);
        EditorPrefs.SetBool(AlbieVoice.EditorOffPref, true);
        SessionState.SetInt(PhaseKey, 1);
        SessionState.SetInt(FailedKey, 0);
        EditorSceneManager.OpenScene(ScenePath);
        EditorApplication.playModeStateChanged += OnPlayMode;
        EditorApplication.isPlaying = true;
    }

    static void OnPlayMode(PlayModeStateChange change)
    {
        int phase = SessionState.GetInt(PhaseKey, 0);
        if (change == PlayModeStateChange.EnteredEditMode && phase == 2)
        {
            EditorApplication.delayCall += () => EditorApplication.isPlaying = true;  // second Play
            return;
        }
        if (change != PlayModeStateChange.EnteredPlayMode || phase == 0) return;
        startedAt = EditorApplication.timeSinceStartup;
        client = Task.Run(phase == 1 ? (Func<int>)Checks : SecondPlay);
        EditorApplication.update += Tick;
    }

    static void Tick()
    {
        bool late = EditorApplication.timeSinceStartup - startedAt > GiveUpSeconds;
        if (!client.IsCompleted && !late) return;
        EditorApplication.update -= Tick;
        int failed = late ? 1 : client.IsFaulted ? 1 : client.Result;
        if (late) Line(false, "finished in time");
        if (client.IsFaulted) Line(false, "client threw " + client.Exception?.GetBaseException().Message);
        SessionState.SetInt(FailedKey, SessionState.GetInt(FailedKey, 0) + failed);
        if (SessionState.GetInt(PhaseKey, 0) == 1 && !late)
        {
            SessionState.SetInt(PhaseKey, 2);
            EditorApplication.isPlaying = false;
            return;
        }
        Finish();
    }

    static void Finish()
    {
        int failed = SessionState.GetInt(FailedKey, 0);
        string summary = $"[DebugBridgeCheck] {(failed == 0 ? "all passed" : failed + " failed")}";
        if (failed == 0) Debug.Log(summary);
        else Debug.LogError(summary);
        EditorApplication.playModeStateChanged -= OnPlayMode;
        EditorPrefs.SetBool(RigSelector.ForceDesktopPref, EditorPrefs.GetBool("DebugBridgeCheck.ForceDesktopWas", false));
        EditorPrefs.SetBool(AlbieVoice.EditorOffPref, EditorPrefs.GetBool("DebugBridgeCheck.VoiceOffWas", false));
        SessionState.SetInt(PhaseKey, 0);
        bool batch = SessionState.GetBool(BatchKey, false);
        EditorApplication.isPlaying = false;
        if (batch) EditorApplication.Exit(failed == 0 ? 0 : 1);
    }

    static bool Line(bool pass, string what)
    {
        Debug.Log($"[DebugBridgeCheck] {(pass ? "PASS" : "FAIL")} {what}");
        return pass;
    }

    // ---------------------------------------------------------------- the client (worker thread)

    sealed class Conn : IDisposable
    {
        readonly TcpClient tcp;
        readonly StreamReader reader;
        readonly StreamWriter writer;

        public Conn()
        {
            var until = DateTime.UtcNow.AddSeconds(10);  // the bridge installs after the scene loads
            while (true)
            {
                try
                {
                    tcp = new TcpClient();
                    tcp.Connect("127.0.0.1", DebugBridge.ActivePort);
                    break;
                }
                catch (SocketException) when (DateTime.UtcNow < until)
                {
                    tcp.Close();
                    System.Threading.Thread.Sleep(200);
                }
            }
            tcp.ReceiveTimeout = 20000;
            var stream = tcp.GetStream();
            reader = new StreamReader(stream, new UTF8Encoding(false));
            writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }

        public void Write(string raw) => writer.Write(raw);
        public JObject Read() => JObject.Parse(reader.ReadLine() ?? "{}");
        public JObject Ask(JObject req)
        {
            writer.WriteLine(req.ToString(Formatting.None));
            return Read();
        }
        public void Dispose() => tcp.Close();
    }

    static int Checks()
    {
        int failed = 0;
        void Check(bool pass, string what, JToken got = null)
        {
            if (!Line(pass, pass || got == null ? what : $"{what}; got {got.ToString(Formatting.None)}")) failed++;
        }
        using (var c = new Conn())
        {
            var ping = c.Ask(new JObject { ["id"] = 1, ["cmd"] = "ping" });
            Check((bool?)ping["ok"] == true && (int?)ping["id"] == 1 && !string.IsNullOrEmpty((string)ping["app"]) && ping["frame"] != null,
                $"ping answers (app {ping["app"]}, scene {ping["scene"]}, platform {ping["platform"]})", ping);

            var tools = c.Ask(new JObject { ["id"] = 2, ["cmd"] = "tools" });
            var names = tools["names"] as JArray;
            Check((bool?)tools["ok"] == true && names != null && names.Count > 0, $"tools lists names ({names?.Count ?? 0})", tools);

            var call = c.Ask(new JObject { ["id"] = 3, ["cmd"] = "call", ["tool"] = "timer_status", ["args"] = new JObject() });
            Check((bool?)call["ok"] == true && call["result"] is JObject r && r["error"] == null,
                $"call timer_status returns a result: {call["result"]?.ToString(Formatting.None)}", call);

            var unknown = c.Ask(new JObject { ["id"] = 4, ["cmd"] = "call", ["tool"] = "no_such_tool" });
            Check((bool?)unknown["ok"] == true && ((string)unknown["result"]?["error"] ?? "").Contains("Unknown tool"),
                "call of an unknown tool gives the tool error inside ok:true", unknown);

            c.Write("{this is not json\n");
            var bad = c.Read();
            var after = c.Ask(new JObject { ["id"] = 5, ["cmd"] = "ping" });
            Check((bool?)bad["ok"] == false && bad["error"] != null && (bool?)after["ok"] == true && (int?)after["id"] == 5,
                "malformed JSON gives ok:false and the connection survives", new JArray(bad, after));

            var badCmd = c.Ask(new JObject { ["id"] = 6, ["cmd"] = "nope" });
            Check((bool?)badCmd["ok"] == false && (int?)badCmd["id"] == 6, "unknown cmd gives ok:false", badCmd);

            c.Write("{\"id\":\"a\",\"cmd\":\"ping\"}\n{\"id\":\"b\",\"cmd\":\"tools\"}\n");
            var first = c.Read();
            var second = c.Read();
            Check((string)first["id"] == "a" && (string)second["id"] == "b" && (bool?)first["ok"] == true && (bool?)second["ok"] == true,
                "two requests in one write both answer, in order", new JArray(first, second));

            var before = c.Ask(new JObject { ["id"] = 7, ["cmd"] = "logs" });
            long seq = (long?)before["seq"] ?? 0;
            string marker = "[DebugBridgeCheck] marker " + Guid.NewGuid().ToString("N");
            Debug.Log(marker);
            var logs = c.Ask(new JObject { ["id"] = 8, ["cmd"] = "logs", ["since"] = seq });
            bool sawLog = false;
            foreach (var l in logs["lines"] as JArray ?? new JArray())
                if ((string)l["msg"] == marker && (string)l["type"] == "Log" && (long)l["seq"] > seq && l["stack"] == null) sawLog = true;
            Check((bool?)logs["ok"] == true && sawLog, $"logs returns the line just logged (after seq {seq}, now {logs["seq"]})", logs["lines"]);

            var state = c.Ask(new JObject { ["id"] = 9, ["cmd"] = "state" });
            var windows = state["windows"] as JArray;
            Check((bool?)state["ok"] == true && (state["head"]?["pos"] as JArray)?.Count == 3 && windows != null,
                $"state returns a head and windows ({windows?.Count ?? 0}, fps {state["fps"]}); first: {(windows != null && windows.Count > 0 ? windows[0].ToString(Formatting.None) : "none")}", state);
        }
        return failed;
    }

    static int SecondPlay()
    {
        using (var c = new Conn())
        {
            var ping = c.Ask(new JObject { ["id"] = "again", ["cmd"] = "ping" });
            return Line((bool?)ping["ok"] == true && (string)ping["id"] == "again", $"second Play: the bridge listens again (frame {ping["frame"]})") ? 0 : 1;
        }
    }
}
