#if DEVELOPMENT_BUILD || UNITY_EDITOR
// Security: this is a remote control for the app (it runs any of Albie's tools and types turns to him), so it
// exists only in the Editor and in development builds; a release APK compiles none of this. It listens on
// 127.0.0.1 only, never on Wi-Fi: on the headset the only way in is a USB cable plus `adb forward tcp:8765 tcp:8765`,
// which needs a developer-mode headset that trusts that laptop. No auth beyond that, on purpose.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Debug bridge: lets a laptop drive a headset nobody is wearing. Newline-delimited JSON over TCP on 127.0.0.1:<see cref="Port"/>;
/// one request line gets exactly one response line, in order, and a connection may carry many requests.
/// Request <c>{"id": any, "cmd": "...", ...}</c>; response <c>{"id", "ok": true, ...}</c> or <c>{"id", "ok": false, "error"}</c>.
/// Commands: ping, tools, call, state, logs, say, plus the feature sweep's (DebugBridgeFeatures.cs: lifecycle, desk,
/// voice_guard, inject, first_run). Socket threads only read and write; every command runs on the main thread.
/// Installs itself when Play starts. Check: Forge > Core > Check Debug Bridge.
/// </summary>
public partial class DebugBridge : MonoBehaviour
{
    public const int Port = 8765;
    const int LogCapacity = 500;
    const int StackChars = 2000;
    const float DefaultTimeoutS = 30f;

    sealed class Job
    {
        public JObject req;
        public JObject res;
        public readonly ManualResetEventSlim done = new ManualResetEventSlim();
        public void Finish(JObject response)
        {
            lock (this)
            {
                if (res != null) return;
                res = response;
            }
            done.Set();
        }
    }

    struct LogLine { public long seq; public string type, msg, stack; }

    TcpListener listener;
    readonly CancellationTokenSource stop = new CancellationTokenSource();
    readonly ConcurrentQueue<Job> jobs = new ConcurrentQueue<Job>();
    readonly List<TcpClient> clients = new List<TcpClient>();
    readonly LogLine[] logs = new LogLine[LogCapacity];
    long logSeq;
    float fps = 60f;
    bool stopped;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        // HideInHierarchy, not HideAndDontSave: a DontSave object survives leaving Play, and its listener with it.
        var go = new GameObject("DebugBridge") { hideFlags = HideFlags.HideInHierarchy };
        DontDestroyOnLoad(go);
        go.AddComponent<DebugBridge>();
    }

    void Awake()
    {
        Application.logMessageReceivedThreaded += OnLog;
#if UNITY_EDITOR
        UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Stop;  // a recompile during Play
#endif
        try
        {
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
        }
        catch (SocketException e)  // the port is taken (another Play session, or an adb forward on this laptop)
        {
            Debug.LogWarning($"[DebugBridge] can't listen on 127.0.0.1:{Port}: {e.Message}");
            listener = null;
            return;
        }
        new Thread(AcceptLoop) { IsBackground = true, Name = "DebugBridge accept" }.Start();
        Debug.Log($"[DebugBridge] listening on 127.0.0.1:{Port}");
    }

    void OnDestroy() => Stop();
    void OnApplicationQuit() => Stop();

    void Stop()
    {
        if (stopped) return;
        stopped = true;
        Application.logMessageReceivedThreaded -= OnLog;
#if UNITY_EDITOR
        UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Stop;
#endif
        stop.Cancel();
        listener?.Stop();
        lock (clients)
        {
            foreach (var c in clients) c.Close();
            clients.Clear();
        }
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        if (dt > 0f) fps += (1f / dt - fps) * 0.05f;
        while (jobs.TryDequeue(out var job)) Run(job);
    }

    // ---------------------------------------------------------------- socket threads

    void AcceptLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = listener.AcceptTcpClient();
                lock (clients) clients.Add(client);
                new Thread(() => Serve(client)) { IsBackground = true, Name = "DebugBridge client" }.Start();
            }
        }
        catch (Exception) { }  // listener stopped
    }

    void Serve(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
            {
                string line;
                while (!stop.IsCancellationRequested && (line = reader.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    writer.WriteLine(Handle(line).ToString(Formatting.None));
                }
            }
        }
        catch (Exception) { }  // the laptop hung up, or Stop closed the socket
        finally
        {
            lock (clients) clients.Remove(client);
        }
    }

    // Parse here, run on the main thread, wait (in order: the next line isn't read until this one answers).
    JObject Handle(string line)
    {
        JObject req;
        try { req = JObject.Parse(line); }
        catch (JsonException e) { return Fail(null, "bad JSON: " + e.Message); }
        float timeout = DefaultTimeoutS;
        if (req["timeout_s"] != null && req["timeout_s"].Type is JTokenType.Integer or JTokenType.Float) timeout = (float)req["timeout_s"];
        var job = new Job { req = req };
        jobs.Enqueue(job);
        try
        {
            if (!job.done.Wait(TimeSpan.FromSeconds(Math.Max(0.1f, timeout)), stop.Token))
                job.Finish(Fail(req["id"], $"timed out after {timeout:0.#} s"));
        }
        catch (OperationCanceledException) { job.Finish(Fail(req["id"], "app is stopping")); }
        return job.res;
    }

    // ---------------------------------------------------------------- commands (main thread)

    void Run(Job job)
    {
        JToken id = job.req["id"];
        try
        {
            string cmd = (string)job.req["cmd"];
            switch (cmd)
            {
                case "ping":
                    job.Finish(Ok(id, new JObject
                    {
                        ["app"] = Application.identifier, ["version"] = Application.version, ["unity"] = Application.unityVersion,
                        ["scene"] = SceneManager.GetActiveScene().name, ["frame"] = Time.frameCount, ["platform"] = Application.platform.ToString(),
                    }));
                    break;
                case "tools":
                    var names = new List<string>(AlbieTools.Names);
                    names.Sort(StringComparer.Ordinal);
                    job.Finish(Ok(id, new JObject { ["names"] = new JArray(names) }));
                    break;
                case "call":
                    string tool = (string)job.req["tool"];
                    if (string.IsNullOrEmpty(tool)) { job.Finish(Fail(id, "call needs \"tool\"")); break; }
                    var args = job.req["args"] as JObject ?? new JObject();
                    // A result with "error" is still ok:true: the tool answered, the client decides.
                    AlbieTools.CallAsync(tool, args, result => job.Finish(Ok(id, new JObject { ["result"] = result })));
                    break;
                case "state":
                    job.Finish(Ok(id, State()));
                    break;
                case "logs":
                    long since = job.req["since"] != null && job.req["since"].Type == JTokenType.Integer ? (long)job.req["since"] : 0;
                    job.Finish(Ok(id, Logs(since)));
                    break;
                case "say":
                    string text = (string)job.req["text"];
                    string error = Say(text);
                    job.Finish(error == null ? Ok(id, new JObject { ["text"] = text.Trim() }) : Fail(id, error));
                    break;
                default:
                    if (cmd != null && RunFeature(cmd, id, job)) break;
                    job.Finish(Fail(id, cmd == null ? "missing \"cmd\"" : $"unknown cmd '{cmd}'"));
                    break;
            }
        }
        catch (Exception e)
        {
            if (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;  // say: the real error, not the reflection wrapper
            job.Finish(Fail(id, $"{e.GetType().Name}: {e.Message}"));
        }
    }

    JObject State()
    {
        var cam = Camera.main;
        Vector3 head = cam != null ? cam.transform.position : Vector3.zero;
        var windows = new JArray();
        foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            if (mb == null || !mb.isActiveAndEnabled) continue;
            string type = mb.GetType().Name;
            if (type != "ForgePanel" && type != "OSWindow") continue;
            var t = mb.transform;
            var w = new JObject { ["name"] = t.name, ["type"] = type, ["path"] = PathOf(t), ["pos"] = Vec(t.position) };
            if (mb is ForgePanel panel && !string.IsNullOrEmpty(panel.panelId)) w["id"] = panel.panelId;  // e.g. messages-mentions
            if (cam != null)
            {
                // Panels are turned with LookRotation(panel - head) (PanelManipulator, GestureWindow), so a panel's
                // forward points AWAY from the viewer. facing_deg = angle between forward and head->panel:
                // 0 = squarely facing the user, 90 = edge-on, 180 = its back to the user.
                w["distance_m"] = Vector3.Distance(head, t.position);
                w["facing_deg"] = Vector3.Angle(t.forward, t.position - head);
            }
            else
            {
                w["distance_m"] = null;
                w["facing_deg"] = null;
            }
            windows.Add(w);
        }
        var state = new JObject
        {
            ["head"] = cam != null ? new JObject { ["pos"] = Vec(head), ["fwd"] = Vec(cam.transform.forward) } : null,
            ["fps"] = Math.Round(fps, 1),
            ["windows"] = windows,
        };
        AddFeatureState(state);
        return state;
    }

    JObject Logs(long since)
    {
        var lines = new JArray();
        long latest;
        lock (logs)
        {
            latest = logSeq;
            long first = Math.Max(since + 1, logSeq - LogCapacity + 1);
            for (long s = Math.Max(1, first); s <= logSeq; s++)
            {
                var l = logs[s % LogCapacity];
                var o = new JObject { ["seq"] = l.seq, ["type"] = l.type, ["msg"] = l.msg };
                if (l.stack != null) o["stack"] = l.stack;
                lines.Add(o);
            }
        }
        return new JObject { ["seq"] = latest, ["lines"] = lines };
    }

    // Any thread. Never logs (it would call itself).
    void OnLog(string msg, string stack, LogType type)
    {
        bool withStack = type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
        string trimmed = withStack && !string.IsNullOrEmpty(stack) ? stack.Trim() : null;
        if (trimmed != null && trimmed.Length > StackChars) trimmed = trimmed.Substring(0, StackChars) + "...";
        lock (logs)
        {
            logSeq++;
            logs[logSeq % LogCapacity] = new LogLine { seq = logSeq, type = type.ToString(), msg = msg, stack = trimmed };
        }
    }

    // A typed user turn into Albie's open voice session, as if heard. Reaches into AlbieVoice's private members by
    // reflection (the same steps as Artifacts/Editor/VoiceTurn.cs), so no existing file changes. Safe on IL2CPP while
    // managed stripping stays at the default (Minimal: Assembly-CSharp isn't stripped). Returns null or the error.
    static string Say(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "say needs \"text\"";
        var voice = FindAnyObjectByType<AlbieVoice>();
        if (voice == null) return "Albie's voice is off (no AlbieVoice running: no token server, or voice off in the Editor)";
        const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        var t = typeof(AlbieVoice);
        var turnSeq = t.GetField("turnSeq", Any);
        var turnOpen = t.GetField("turnOpen", Any);
        var committed = t.GetField("committed", Any);
        var finalSent = t.GetField("finalSent", Any);
        var responseActive = t.GetField("responseActive", Any);
        var audioStarted = t.GetField("audioStarted", Any);
        var send = t.GetMethod("Send", Any, null, new[] { typeof(JObject) }, null);
        var respond = t.GetMethod("RequestResponse", Any, null, new[] { typeof(string) }, null);
        var stopSpeech = t.GetMethod("StopSpeech", Any, null, new[] { typeof(string) }, null);
        var emit = t.GetMethod("Emit", Any, null, new[] { typeof(string), typeof(JObject), typeof(string) }, null);
        var brevityNow = t.GetField("BrevityNow", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        if (turnSeq == null || turnOpen == null || committed == null || finalSent == null || responseActive == null || audioStarted == null
            || send == null || respond == null || stopSpeech == null || emit == null || brevityNow == null)
            return "AlbieVoice changed shape: say needs updating (see DebugBridge.Say)";

        text = text.Trim();
        string brevity = (string)brevityNow.GetRawConstantValue();  // read before touching the turn: a throw here must not leave a half-sent turn
        if ((bool)responseActive.GetValue(voice) || (bool)audioStarted.GetValue(voice)) stopSpeech.Invoke(voice, new object[] { "barge_in" });
        turnSeq.SetValue(voice, (int)turnSeq.GetValue(voice) + 1);
        turnOpen.SetValue(voice, false);
        committed.SetValue(voice, false);
        finalSent.SetValue(voice, true);  // this turn's transcript is the typed text, sent below
        t.GetMethod("Remember", Any, null, new[] { typeof(string), typeof(string) }, null)?.Invoke(voice, new object[] { "User: ", text });  // voice-cost branch
        Debug.Log($"[DebugBridge] say: {text}");
        emit.Invoke(voice, new object[] { "stt.final", new JObject { ["text"] = text, ["voice"] = true }, "thinking" });
        send.Invoke(voice, new object[] { new JObject
        {
            ["type"] = "conversation.item.create",
            ["item"] = new JObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JArray(new JObject { ["type"] = "input_text", ["text"] = text }),
            },
        } });
        respond.Invoke(voice, new object[] { brevity });
        return null;
    }

    // ---------------------------------------------------------------- helpers

    static JObject Ok(JToken id, JObject fields)
    {
        var o = new JObject { ["id"] = id?.DeepClone(), ["ok"] = true };
        foreach (var p in fields) o[p.Key] = p.Value;
        return o;
    }

    static JObject Fail(JToken id, string error) => new JObject { ["id"] = id?.DeepClone(), ["ok"] = false, ["error"] = error };

    static JArray Vec(Vector3 v) => new JArray(Math.Round(v.x, 3), Math.Round(v.y, 3), Math.Round(v.z, 3));

    static string PathOf(Transform t)
    {
        string path = t.name;
        for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return path;
    }
}
#endif
