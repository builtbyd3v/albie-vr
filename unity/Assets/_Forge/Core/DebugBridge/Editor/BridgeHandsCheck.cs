using System;
using System.Collections.Generic;
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
/// Forge > Core > Check Bridge Hands: the Main scene in Play mode (desktop rig, voice off, so it costs nothing), seated at
/// the desk with the code view open, then a real TcpClient on 127.0.0.1:<see cref="DebugBridge.ActivePort"/> drives the
/// hand, hands, hand_replay, hand_matrix and tap commands. One PASS/FAIL line per check. No person needed. Batch (exits 1
/// on any failure): <c>Unity -batchmode -projectPath unity -executeMethod BridgeHandsCheck.RunBatch -logFile -</c>
/// </summary>
[InitializeOnLoad]
public static class BridgeHandsCheck
{
    const string PhaseKey = "BridgeHandsCheck.Phase", BatchKey = "BridgeHandsCheck.Batch";
    const string ScenePath = "Assets/_Forge/Core/Main.unity";
    const double GiveUpSeconds = 300;

    static Task<int> client;
    static double startedAt;
    static IEnumerator<float> setup;
    static double wakeAt;

    static BridgeHandsCheck()
    {
        if (SessionState.GetInt(PhaseKey, 0) > 0) EditorApplication.playModeStateChanged += OnPlayMode;
    }

    public static void RunBatch()
    {
        SessionState.SetBool(BatchKey, true);
        Run();
    }

    [MenuItem("Forge/Core/Check Bridge Hands")]
    public static void Run()
    {
        EditorPrefs.SetBool("BridgeHandsCheck.ForceDesktopWas", EditorPrefs.GetBool(RigSelector.ForceDesktopPref, false));
        EditorPrefs.SetBool("BridgeHandsCheck.VoiceOffWas", EditorPrefs.GetBool(AlbieVoice.EditorOffPref, false));
        EditorPrefs.SetBool(RigSelector.ForceDesktopPref, true);
        EditorPrefs.SetBool(AlbieVoice.EditorOffPref, true);
        SessionState.SetInt(PhaseKey, 1);
        EditorSceneManager.OpenScene(ScenePath);
        EditorApplication.playModeStateChanged += OnPlayMode;
        EditorApplication.isPlaying = true;
    }

    static void OnPlayMode(PlayModeStateChange change)
    {
        if (change != PlayModeStateChange.EnteredPlayMode || SessionState.GetInt(PhaseKey, 0) == 0) return;
        startedAt = EditorApplication.timeSinceStartup;
        setup = Setup();
        wakeAt = 0;
        client = null;
        EditorApplication.update += Tick;
    }

    // Sit at the desk and open the code view (GesturePlayTest's steps), so there are windows to point at and tap.
    static IEnumerator<float> Setup()
    {
        Workspace workspace = null;
        while (workspace == null || !workspace.presence.AtDesk)
        {
            yield return 0.25f;
            workspace = Workspace.Current;
            if (workspace != null) workspace.presence.forceSeated = true;
        }
        yield return 0.5f;
        DesktopRig.Current.ToggleSeat();
        yield return 0.8f;
        workspace.Open("code");
        yield return 1.5f;
    }

    static void Tick()
    {
        bool late = EditorApplication.timeSinceStartup - startedAt > GiveUpSeconds;
        if (client == null && !late)
        {
            if (EditorApplication.timeSinceStartup < wakeAt) return;
            try
            {
                if (setup.MoveNext()) { wakeAt = EditorApplication.timeSinceStartup + setup.Current; return; }
            }
            catch (Exception e)
            {
                Line(false, "setup threw " + e.Message);
                Finish(1);
                return;
            }
            client = Task.Run(Checks);
            return;
        }
        if (client != null && !client.IsCompleted && !late) return;
        if (late) Line(false, "finished in time");
        if (client != null && client.IsFaulted) Line(false, "client threw " + client.Exception?.GetBaseException().Message);
        Finish(late || client == null || client.IsFaulted ? 1 : client.Result);
    }

    static void Finish(int failed)
    {
        EditorApplication.update -= Tick;
        string summary = $"[BridgeHandsCheck] {(failed == 0 ? "all passed" : failed + " failed")}";
        if (failed == 0) Debug.Log(summary);
        else Debug.LogError(summary);
        EditorApplication.playModeStateChanged -= OnPlayMode;
        EditorPrefs.SetBool(RigSelector.ForceDesktopPref, EditorPrefs.GetBool("BridgeHandsCheck.ForceDesktopWas", false));
        EditorPrefs.SetBool(AlbieVoice.EditorOffPref, EditorPrefs.GetBool("BridgeHandsCheck.VoiceOffWas", false));
        SessionState.SetInt(PhaseKey, 0);
        bool batch = SessionState.GetBool(BatchKey, false);
        EditorApplication.isPlaying = false;
        if (batch) EditorApplication.Exit(failed == 0 ? 0 : 1);
    }

    static bool Line(bool pass, string what)
    {
        Debug.Log($"[BridgeHandsCheck] {(pass ? "PASS" : "FAIL")} {what}");
        return pass;
    }

    // ---------------------------------------------------------------- the client (worker thread)

    sealed class Conn : IDisposable
    {
        readonly TcpClient tcp;
        readonly StreamReader reader;
        readonly StreamWriter writer;
        int id;

        public Conn()
        {
            var until = DateTime.UtcNow.AddSeconds(10);
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
            tcp.ReceiveTimeout = 150000;  // hand_matrix runs for a while
            var stream = tcp.GetStream();
            reader = new StreamReader(stream, new UTF8Encoding(false));
            writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }

        public JObject Ask(JObject req)
        {
            req["id"] = ++id;
            writer.WriteLine(req.ToString(Formatting.None));
            return JObject.Parse(reader.ReadLine() ?? "{}");
        }

        public void Dispose() => tcp.Close();
    }

    static List<string> Names(JObject res)
    {
        var names = new List<string>();
        foreach (var r in res["recognized"] as JArray ?? new JArray()) names.Add((string)r["gesture"]);
        return names;
    }

    static bool IsFlick(string g) => g == GestureLibrary.FlickIn || g == GestureLibrary.FlickOut;
    static bool IsFist(string g) => g == GestureLibrary.FistClose || g == GestureLibrary.FistOpen || g == GestureLibrary.FistCloseAll || g == GestureLibrary.FistOpenAll;

    // A right hand closing into a fist from open, as hand_replay frames: head space, palm down, fingers forward. Made here,
    // on the client side, so the replay path is checked against frames the app didn't generate.
    static JArray FistFrames()
    {
        var frames = new JArray();
        var sample = new HandSample();
        var wrist = new Vector3(0.1f, -0.17f, 0.34f);
        const float dt = 1f / 72f;
        int total = 72;  // 1 s: open 0.4 s, closing 0.2 s, fist 0.4 s
        for (int i = 0; i < total; i++)
        {
            float t = i * dt;
            float k = Mathf.Clamp01((t - 0.4f) / 0.2f);
            SyntheticHand.Build(sample, HandShape.Lerp(HandShape.Open, HandShape.Fist, k), wrist, Quaternion.identity, false);
            var joints = new JArray();
            foreach (var j in sample.joints) { joints.Add(j.x); joints.Add(j.y); joints.Add(j.z); }
            frames.Add(new JObject { ["t"] = Math.Round(t, 4), ["right"] = joints });
        }
        return frames;
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
            var list = c.Ask(new JObject { ["cmd"] = "hands", ["list"] = true });
            var listed = new List<string>();
            foreach (var g in list["gestures"] as JArray ?? new JArray()) listed.Add((string)g["name"]);
            Check((bool?)list["ok"] == true && listed.Contains(GestureLibrary.FlickIn) && listed.Contains(GestureLibrary.FistClose) && listed.Count == 7,
                $"hands list names the gestures: {string.Join(", ", listed)} + {list["holds"]?.ToString(Formatting.None)}", list);

            var state = c.Ask(new JObject { ["cmd"] = "hands" });
            Check((bool?)state["ok"] == true && (string)state["source"] == "simulated" && (bool?)state["hands"]?["right"]?["tracked"] == true,
                $"hands reports the source ({state["source"]}) and the right hand: {state["hands"]?["right"]?.ToString(Formatting.None)}", state);

            // Each built-in, once: the right name, and never a flick for a fist or a fist for a flick.
            foreach (var (gesture, hand, action) in new[]
            {
                (GestureLibrary.FlickIn, "right", GestureActions.WindowSummon),
                (GestureLibrary.FlickOut, "right", GestureActions.WindowMinimize),
                (GestureLibrary.FistClose, "right", GestureActions.WindowClose),
                (GestureLibrary.FistOpen, "right", GestureActions.WindowReopen),
                (GestureLibrary.FistCloseAll, "left", GestureActions.WindowsHide),
                (GestureLibrary.FistOpenAll, "left", GestureActions.WindowsShow),
                (GestureLibrary.PalmUp, "left", GestureActions.AlbiePerch),
            })
            {
                var res = c.Ask(new JObject { ["cmd"] = "hand", ["gesture"] = gesture, ["hand"] = hand });
                var got = Names(res);
                bool crossed = got.Exists(g => IsFlick(gesture) ? IsFist(g) : IsFlick(g));
                Check((bool?)res["ok"] == true && got.Contains(gesture) && !crossed && (res["actions"] as JArray)?.ToObject<List<string>>().Contains(action) == true,
                    $"hand {gesture} ({hand}) is recognized as {string.Join(", ", got)}, fires {res["actions"]?.ToString(Formatting.None)}, target {res["target"]}", res);
                System.Threading.Thread.Sleep(300);
            }

            var varied = c.Ask(new JObject { ["cmd"] = "hand", ["gesture"] = GestureLibrary.FistClose, ["speed"] = 1.6, ["jitter_m"] = 0.002, ["sloppy"] = 0.2, ["seed"] = 7 });
            Check((bool?)varied["ok"] == true && Names(varied).Contains(GestureLibrary.FistClose) && (int?)varied["seed"] == 7 && Math.Abs((float)varied["speed"] - 1.6f) < 1e-3f,
                $"hand with speed/jitter/sloppy/seed still reads fist_close: {string.Join(", ", Names(varied))}", varied);

            var sloppy = c.Ask(new JObject { ["cmd"] = "hand", ["gesture"] = GestureLibrary.FistClose, ["sloppy"] = 1 });
            Check((bool?)sloppy["ok"] == true && Names(sloppy).Count == 0, "hand fist_close with sloppy 1 (a relaxed hand) is recognized as nothing", sloppy);

            var bad = c.Ask(new JObject { ["cmd"] = "hand", ["gesture"] = "wave" });
            Check((bool?)bad["ok"] == false && ((string)bad["error"] ?? "").Contains("unknown gesture"), "an unknown gesture gives ok:false", bad);

            var replay = c.Ask(new JObject { ["cmd"] = "hand_replay", ["frames"] = FistFrames() });
            var replayed = Names(replay);
            Check((bool?)replay["ok"] == true && replayed.Contains(GestureLibrary.FistClose) && !replayed.Exists(IsFlick),
                $"hand_replay of 72 test-made frames is recognized as {string.Join(", ", replayed)}", replay);

            var matrix = c.Ask(new JObject
            {
                ["cmd"] = "hand_matrix", ["n"] = 3, ["seed"] = 42, ["timeout_s"] = 120,
                ["speed"] = new JArray(0.9, 1.6), ["jitter_m"] = new JArray(0, 0.002), ["sloppy"] = new JArray(0, 0.25),
            });
            var table = matrix["table"] as JObject;
            bool allSeen = table != null, noCross = table != null;
            if (table != null)
                foreach (var row in table)
                {
                    if (((int?)row.Value[row.Key] ?? 0) == 0) allSeen = false;
                    foreach (var cell in (JObject)row.Value)
                        if (IsFlick(row.Key) ? IsFist(cell.Key) : IsFist(row.Key) && IsFlick(cell.Key)) noCross = false;
                }
            Check((bool?)matrix["ok"] == true && table?.Count == 5 && allSeen && noCross,
                $"hand_matrix (the right hand's 5 gestures x 3, seed 42, {matrix["duration_s"]} s): every gesture read at least once, no flick/fist confusion; table {table?.ToString(Formatting.None)}, correct {matrix["correct"]?.ToString(Formatting.None)}, confusions {matrix["confusions"]}", matrix);

            var after = c.Ask(new JObject { ["cmd"] = "hands" });
            Check((string)after["source"] == "simulated" && (bool?)after["playing"] == false && (string)after["hands"]?["right"]?["last"]?["gesture"] != null,
                $"the simulated source is back after playing; last right-hand gesture {after["hands"]?["right"]?["last"]?.ToString(Formatting.None)}", after);

            // tap: find a window with Face me / Straight pills, press Straight, see it light up, press Face me to put it back.
            var windows = c.Ask(new JObject { ["cmd"] = "state" })["windows"] as JArray ?? new JArray();
            string window = null;
            JArray controls = null;
            foreach (var w in windows)
            {
                if ((string)w["type"] != "ForgePanel") continue;
                var l = c.Ask(new JObject { ["cmd"] = "tap", ["list"] = true, ["window"] = (string)w["name"] });
                if ((bool?)l["ok"] != true) continue;
                foreach (var ctl in l["controls"] as JArray)
                    if ((string)ctl["label"] == "Straight") { window = (string)w["name"]; controls = (JArray)l["controls"]; }
                if (window != null) break;
            }
            Check(window != null && controls.Count > 0, $"tap list finds the controls of a window ({window}: {controls?.Count ?? 0} controls, first {controls?[0]?.ToString(Formatting.None)})");
            if (window == null) return failed + 1;

            bool? OnOf(string label)
            {
                var l = c.Ask(new JObject { ["cmd"] = "tap", ["list"] = true, ["window"] = window });
                foreach (var ctl in l["controls"] as JArray ?? new JArray())
                    if ((string)ctl["label"] == label) return (bool?)ctl["on"];
                return null;
            }
            bool? before = OnOf("Straight");
            var tap = c.Ask(new JObject { ["cmd"] = "tap", ["window"] = window, ["text"] = "Straight" });
            bool? afterOn = OnOf("Straight");
            Check((bool?)tap["ok"] == true && before == false && afterOn == true && tap["pressed"]?["clicked"] != null,
                $"tap Straight on {window} turns its pill on ({before} -> {afterOn}): {tap["pressed"]?.ToString(Formatting.None)}", tap);
            var back = c.Ask(new JObject { ["cmd"] = "tap", ["window"] = window, ["text"] = "Face me" });
            Check((bool?)back["ok"] == true && OnOf("Face me") == true && OnOf("Straight") == false, "tap Face me puts it back", back);

            var missing = c.Ask(new JObject { ["cmd"] = "tap", ["text"] = "no such button anywhere" });
            Check((bool?)missing["ok"] == false, "tap of a label that doesn't exist gives ok:false", missing);
        }
        return failed;
    }
}
