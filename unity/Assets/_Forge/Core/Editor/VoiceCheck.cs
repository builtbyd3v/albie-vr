using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Forge.Pet;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Headless checks for Albie's voice cost and free-speech rules (<see cref="AlbieVoice"/>, <see cref="AlbieFollowUp"/>).
/// A fake clock, token and socket stand in for the voice service, so no session opens, nothing is billed and no socket
/// is made. Forge > Core > Check Voice logs the report; <see cref="Run"/> returns it (for `unity command eval`). PASS on
/// the first line means every step passed. The rules: the service's idle messages are not activity; the session closes
/// FollowUpS + IdleCloseS after Albie's last word; a pause sends nothing, a connect from before it gives up, the next turn
/// after it connects and the last 8 lines carry over; hands-free speech reaches the service only while the user faces
/// Albie (30 degree cone, or Albie on the palm); a cold start sends the first words in order; a heard turn with no reply
/// is asked once more after RetryS. Edit mode runs every step;
/// Play mode only the pure ones (a test AlbieVoice would take over ForgeNet.LocalPeer and talk to the running pet).
/// </summary>
public static class VoiceCheck
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const string PlaySkip = "skipped in Play mode (a test AlbieVoice would take over ForgeNet.LocalPeer and talk to the running pet)";

    [MenuItem("Forge/Core/Check Voice")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    /// <summary>For -executeMethod.</summary>
    public static void RunBatch()
    {
        string report = Run();
        Debug.Log(report);
        EditorApplication.Exit(report.StartsWith("PASS") ? 0 : 1);
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        int failed = 0, skipped = 0;
        bool logs = Debug.unityLogger.logEnabled;
        Debug.unityLogger.logEnabled = false;  // the test voice's "connected" / "suspended" lines would read like the real one's
        try
        {
            failed += Step(sb, ref skipped, "1a Only turn and reply messages count as activity", ActivityTypes);
            failed += Step(sb, ref skipped, "1b A stream of idle messages still closes the session", IdleStreamCloses);
            failed += Step(sb, ref skipped, "2  Closes FollowUpS + IdleCloseS after Albie's last word", ClosesAfterLastWord);
            failed += Step(sb, ref skipped, "3a A pause closes the session and sends nothing", PauseSendsNothing);
            failed += Step(sb, ref skipped, "3b A connect from before the pause gives up; the next turn connects with the last 8 lines", PauseEpoch);
            failed += Step(sb, ref skipped, "3c Pause and return while connecting: one session, only the new turn", PauseRace);
            failed += Step(sb, ref skipped, "4a Look cone is 30 degrees", LookCone);
            failed += Step(sb, ref skipped, "4b Perched on the palm counts as facing", Perched);
            failed += Step(sb, ref skipped, "4c Speech not facing Albie opens no session", FacingGate);
            failed += Step(sb, ref skipped, "4d A gated turn that fails to open clears RequireName", GateCleared);
            failed += Step(sb, ref skipped, "5  Cold start: frames queued while connecting go out in order", ColdStart);
            failed += Step(sb, ref skipped, "6  A heard turn with no reply is asked once more after RetryS", HeardTurnRetried);
        }
        finally
        {
            Debug.unityLogger.logEnabled = logs;
        }
        string mode = Application.isPlaying ? "Play" : "Edit";
        return (failed == 0 ? "PASS" : $"FAIL ({failed})") + $" voice check ({mode} mode{(skipped > 0 ? $", {skipped} skipped" : "")})\n" + sb;
    }

    // ---- Steps -----------------------------------------------------------------------------------------------

    static string ActivityTypes()
    {
        string[] activity =
        {
            "response.created", "response.output_audio.delta", "response.output_audio_transcript.delta", "response.done",
            "response.function_call_arguments.done", "conversation.item.input_audio_transcription.delta",
            "conversation.item.input_audio_transcription.completed",
        };
        string[] idle =
        {
            "ping", "session.created", "session.updated", "conversation.created", "conversation.item.created",
            "input_audio_buffer.speech_started", "input_audio_buffer.committed", "rate_limits.updated", "error", "",
        };
        foreach (string t in activity) Expect(AlbieVoice.CountsAsActivity(t), $"\"{t}\" doesn't count as activity");
        foreach (string t in idle) Expect(!AlbieVoice.CountsAsActivity(t), $"\"{t}\" counts as activity");
        return $"{activity.Length} response./conversation.item.input_audio types count, {idle.Length} others don't";
    }

    static string IdleStreamCloses()
    {
        if (Application.isPlaying) return PlaySkip;
        using var r = new Rig();
        var s = OpenSession(r);  // t = 0
        string[] keepalive = { "ping", "session.updated", "conversation.created", "rate_limits.updated", "input_audio_buffer.speech_started" };
        for (int i = 1; i <= 7; i++)  // one every 5 s up to 35 s: under the window, still open
        {
            r.now = 5f * i;
            s.Feed(Msg(keepalive[i % keepalive.Length]));
            r.Tick();
            Expect(!s.aborted, $"closed at {r.now} s, before the {Window():0.0} s window");
        }
        r.now = 36f;
        s.Feed(Msg("ping"));
        r.Tick();
        Expect(s.aborted, "8 idle messages over 36 s kept the session open");

        // Control: a turn or reply message does reset it.
        r.now = 100f;
        var s2 = OpenSession(r);
        r.now = 120f;
        s2.Feed(new JObject { ["type"] = "conversation.item.input_audio_transcription.delta", ["delta"] = "hi" });
        r.now = 130f;
        s2.Feed(Msg("response.done"));
        r.now = 130f + Window() - 0.5f;
        r.Tick();
        Expect(!s2.aborted, "a reply message at 130 s didn't count: closed early");
        r.now = 130f + Window() + 0.5f;
        r.Tick();
        Expect(s2.aborted, "not closed after the window from the last reply message");
        return $"8 keepalives over 36 s: closed at 36 s; a reply message at 130 s held it to {130f + Window():0.0} s";
    }

    static string ClosesAfterLastWord()
    {
        float window = Window();
        Expect(window >= 30f && window <= 40f, $"FollowUpS + IdleCloseS is {window} s, not about 35 s");
        if (Application.isPlaying) return $"window {window:0.0} s; the rest " + PlaySkip;
        using var r = new Rig();
        var s = OpenSession(r);
        r.now = 1f;
        s.Feed(Msg("response.created"));
        // 0.25 s of 48 kHz PCM16: his speech ends at 1.25 s, after the reply's last message.
        s.Feed(new JObject { ["type"] = "response.output_audio.delta", ["delta"] = Convert.ToBase64String(new byte[24000]) });
        r.now = 1.1f;
        s.Feed(Msg("response.done"));
        const float lastWord = 1.25f;
        for (float t = 10f; t < 36f; t += 10f)
        {
            r.now = t;
            s.Feed(Msg("ping"));
        }
        r.now = lastWord + window - 0.1f;  // already past the window counted from the reply's last message (1.1 s)
        r.Tick();
        Expect(!s.aborted, $"closed at {r.now:0.00} s: counted from the reply's last message, not his last word");
        r.now = lastWord + window + 0.1f;
        r.Tick();
        Expect(s.aborted, $"still open at {r.now:0.00} s");
        return $"window {window:0.0} s; last word at {lastWord} s, open at {lastWord + window - 0.1f:0.00} s, closed at {lastWord + window + 0.1f:0.00} s; 3 idle pings in between";
    }

    static string PauseSendsNothing()
    {
        if (Application.isPlaying) return PlaySkip;
        using var r = new Rig();
        var s = OpenSession(r);
        int sent = s.sent.Count;
        r.voice.Suspend();
        Expect(r.voice.Suspended, "not suspended");
        Expect(s.aborted, "the open session stayed open");
        r.WakeTurn(true);
        r.Frame(1);
        r.Frame(2);
        r.Event("albie.say", new JObject { ["text"] = "a tip" });
        r.Event("messages.focus", new JObject { ["text"] = "hi", ["user_name"] = "Sam", ["ts"] = "1" });
        r.Event("stt.cancel");
        Expect(r.tokens == 1 && r.sockets.Count == 1, $"connected while away ({r.tokens} token fetches, {r.sockets.Count} sessions)");
        Expect(s.sent.Count == sent, $"{s.sent.Count - sent} messages sent while away");
        r.voice.Resume();
        Expect(!r.voice.Suspended, "Resume didn't clear it");
        return "session closed; a turn, 2 frames, a tip and Ask Albie while away: 0 sent, 0 connects";
    }

    static string PauseEpoch()
    {
        if (Application.isPlaying) return PlaySkip;
        using var r = new Rig { holdToken = true };
        for (int i = 0; i < 10; i++) r.Event("albie.note", new JObject { ["text"] = "line " + i });
        r.WakeTurn(true);
        Expect(r.tokens == 1 && r.held != null, "the turn didn't start a connect");
        r.voice.Suspend();
        r.Release();  // the token arrives after the headset came off
        Expect(r.sockets.Count == 0, "a connect from before the pause opened a session while away");

        r.voice.Resume();
        r.holdToken = false;
        r.WakeTurn(true);
        r.Frame(7);
        Expect(r.sockets.Count == 1, "the first turn after Resume opened no session");
        var s = r.Last;
        Expect(Types(s) == "session.update input_audio_buffer.clear input_audio_buffer.append", "sent: " + Types(s));
        string instructions = (string)s.sent[0]["session"]?["instructions"] ?? "";
        Expect(instructions.Contains("Earlier in this conversation"), "the new session got no earlier lines");
        int at = -1;
        for (int i = 2; i < 10; i++)
        {
            int next = instructions.IndexOf("Note: line " + i, StringComparison.Ordinal);
            Expect(next > at, $"line {i} missing or out of order");
            at = next;
        }
        Expect(!instructions.Contains("Note: line 0") && !instructions.Contains("Note: line 1"), "more than the last 8 lines carried");
        return "old connect gave up (0 sessions while away); after Resume 1 session, its instructions carry lines 2-9 of 10, in order";
    }

    static string PauseRace()
    {
        if (Application.isPlaying) return PlaySkip;
        using var r = new Rig { holdToken = true };
        r.WakeTurn(true);
        r.Frame(1);
        var old = r.held;
        r.voice.Suspend();
        r.voice.Resume();
        r.WakeTurn(true);
        r.Frame(9);
        old.SetResult("fake");  // the connect from before the pause finishes now
        Expect(r.sockets.Count == 0, "the connect from before the pause opened a session");
        Expect(r.tokens == 2 && r.held != null, "the turn after the return didn't start its own connect");
        r.Release();
        Expect(r.sockets.Count == 1, $"{r.sockets.Count} sessions");
        var s = r.Last;
        Expect(Types(s) == "session.update input_audio_buffer.clear input_audio_buffer.append", "sent: " + Types(s));
        Expect(Audio(s.sent[2]) == 9, "the frame from before the pause was sent");
        return "1 session; it got only the new turn (clear + frame 9), nothing from before the pause";
    }

    static string LookCone()
    {
        var field = typeof(AlbieFollowUp).GetField("LookAngle", Any);
        Expect(field != null, "AlbieFollowUp.LookAngle not found");
        float look = (float)field.GetRawConstantValue();
        Expect(look == 30f, $"LookAngle is {look}");
        // 50 m away Albie's size adds 0.1 deg, so the cone is the constant; at the desk (1 m) his body adds 5.7 deg.
        Expect(Cone(At(29.5f, 50f), 0.1f), "29.5 deg at 50 m isn't facing");
        Expect(!Cone(At(30.5f, 50f), 0.1f), "30.5 deg at 50 m counts as facing");
        Expect(Cone(At(16f, 1f), 0.1f), "16 deg at 1 m isn't facing (the look measured on Quest)");
        Expect(!Cone(At(45f, 1f), 0.1f), "45 deg at 1 m counts as facing (side talk measured 30-62)");
        Expect(Cone(new Vector3(0.03f, 0f, 0.05f), 0.1f), "closer than 10 cm isn't facing");
        float angle = 0f, limit = 0f;
        AlbieFollowUp.InLookCone(Vector3.zero, Vector3.forward, At(20f, 1f), 0.1f, ref angle, ref limit);
        return $"LookAngle {look}; 29.5 in / 30.5 out at 50 m; 16 in / 45 out at 1 m (limit {limit:0.0}); under 10 cm in";
    }

    static string Perched()
    {
        Expect(AlbieFollowUp.CountsAsFacing(false, 60f, 1.5f, true), "perched and looking away isn't facing");
        Expect(!AlbieFollowUp.CountsAsFacing(false, 60f, 1.5f, false), "looking away, not perched, counts as facing");
        Expect(AlbieFollowUp.CountsAsFacing(false, 1f, 1.5f, false), "a look 1 s ago (grace 1.5 s) isn't facing");
        Expect(AlbieFollowUp.CountsAsFacing(true, 60f, 1.5f, false), "looking now isn't facing");
        return "perched: facing wherever they look; not perched and looking away: not facing; grace and looking now: facing";
    }

    static string FacingGate()
    {
        if (Application.isPlaying) return PlaySkip;
        using var r = new Rig();
        r.WakeTurn(false);
        r.Frame(1);
        r.Event("stt.stop");
        Expect(r.tokens == 0 && r.sockets.Count == 0, "a wake turn not facing Albie connected");
        r.Event("stt.start", new JObject { ["require_name"] = true });
        r.Frame(2);
        r.Event("stt.stop");
        Expect(r.tokens == 0 && r.sockets.Count == 0, "a follow-up turn that needs the name connected");
        r.WakeTurn(true);
        Expect(r.sockets.Count == 1, "a wake turn facing Albie opened no session");
        r.Event("stt.cancel");
        r.Event("stt.stop");
        r.Event("stt.start", new JObject());  // a button turn always counts
        Expect(Count(r.Last, "input_audio_buffer.clear") == 4, "the button turn didn't reach the session: " + Types(r.Last));
        return "wake not facing: no session; follow-up needing the name: no session; wake facing: session; button: sent";
    }

    static string GateCleared()
    {
        if (Application.isPlaying) return "skipped in Play mode (a test PushToTalk would bind the talk key in the running app)";
        var go = new GameObject("VoiceCheck FollowUp") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            var hub = go.AddComponent<HubSpeechToText>();
            var ptt = go.AddComponent<PushToTalk>();
            var follow = go.AddComponent<AlbieFollowUp>();
            Set(ptt, "sttProvider", hub);
            Set(follow, "pushToTalk", ptt);
            Set(follow, "lookCountsAsName", false);  // so the turn needs the name, as for a talker looking elsewhere
            Set(ptt, "_held", true);                 // a key is held: BeginHandsFree refuses, with no mic
            Expect(ptt.CanListenHandsFree, "the test PushToTalk can't listen hands-free");
            typeof(AlbieFollowUp).GetMethod("GateNextTurn", Any).Invoke(follow, null);
            Expect(hub.RequireName, "GateNextTurn didn't set the gate (the step would prove nothing)");
            hub.RequireName = false;
            Expect(!follow.OpenFallbackTurn(), "the turn opened");
            Expect(!hub.RequireName, "RequireName stayed set after the turn failed to open: it would gate the next turn");
            return "gate set for the turn, the turn refused, gate cleared";
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    static string ColdStart()
    {
        if (Application.isPlaying) return PlaySkip;
        using var r = new Rig { holdToken = true };
        r.WakeTurn(true);
        for (byte b = 1; b <= 4; b++) r.Frame(b);
        Expect(r.sockets.Count == 0, "a session before the token came");
        r.Release();
        var s = r.Last;
        Expect(s != null, "no session after the token came");
        string want = "session.update input_audio_buffer.clear" + string.Concat(System.Linq.Enumerable.Repeat(" input_audio_buffer.append", 4));
        Expect(Types(s) == want, "sent: " + Types(s));
        for (int i = 0; i < 4; i++) Expect(Audio(s.sent[2 + i]) == i + 1, $"frame {i + 1} out of order");
        r.Frame(5);
        Expect(s.sent.Count == 7 && Audio(s.sent[6]) == 5, "the next frame didn't go straight out");
        return "session.update, clear, frames 1-4 in order, then frame 5 at once";
    }

    static string HeardTurnRetried()
    {
        if (Application.isPlaying) return PlaySkip;
        using var r = new Rig();
        float retry = Const("RetryS");
        r.WakeTurn(true);
        r.Frame(1);
        r.Frame(2);
        r.Event("stt.stop");  // committed at t = 0
        var s = r.Last;
        s.Feed(new JObject { ["type"] = "conversation.item.input_audio_transcription.completed", ["transcript"] = "pull this repo" });
        r.now = retry - 0.1f;
        r.Tick();
        Expect(Count(s, "response.create") == 0, $"asked again at {r.now:0.0} s, before {retry} s");
        r.now = retry + 0.1f;
        r.Tick();
        Expect(Count(s, "response.create") == 1, $"a heard turn with no reply wasn't asked again at {r.now:0.0} s");
        r.now = 20f;
        r.Tick();
        Expect(Count(s, "response.create") == 1, "asked more than once");

        // Control: a turn the service answers is not asked again.
        r.WakeTurn(true);
        r.Frame(3);
        r.Event("stt.stop");
        s.Feed(Msg("response.created"));
        r.now = 30f;
        r.Tick();
        Expect(Count(s, "response.create") == 1, "an answered turn was asked again");
        return $"heard, no reply: 0 asks at {retry - 0.1f:0.0} s, 1 at {retry + 0.1f:0.0} s, still 1 at 20 s; an answered turn: 0 more";
    }

    // ---- The fake voice service --------------------------------------------------------------------------------

    /// <summary>An AlbieVoice with a fake clock, token and socket. Edit mode only: its OnEnable never runs, so it never
    /// becomes ForgeNet.LocalPeer, and ForgeNet.Inject drops what it emits.</summary>
    sealed class Rig : IDisposable
    {
        public readonly AlbieVoice voice;
        readonly GameObject go;
        public float now;
        public int tokens;
        public bool holdToken;                       // the next token fetch waits for Release()
        public TaskCompletionSource<string> held;
        public readonly List<FakeSocket> sockets = new List<FakeSocket>();
        public FakeSocket Last => sockets.Count > 0 ? sockets[sockets.Count - 1] : null;

        public Rig()
        {
            go = new GameObject("VoiceCheck") { hideFlags = HideFlags.HideAndDontSave };
            voice = go.AddComponent<AlbieVoice>();
            typeof(AlbieVoice).GetField("life", Any).SetValue(voice, new CancellationTokenSource());
            voice.clock = () => now;
            voice.fetchToken = () =>
            {
                tokens++;
                if (!holdToken) return Task.FromResult("fake");
                held = new TaskCompletionSource<string>();
                return held.Task;
            };
            voice.openSocket = (token, cancel) =>
            {
                var s = new FakeSocket();
                sockets.Add(s);
                return Task.FromResult<WebSocket>(s);
            };
        }

        public void Release()
        {
            var h = held;
            held = null;
            h.SetResult("fake");
        }

        public void Event(string type, JObject payload = null) => voice.OnEvent(type, payload ?? new JObject());
        public void WakeTurn(bool facing) => Event("stt.start", new JObject { ["wake"] = true, ["facing"] = facing });

        public void Frame(byte fill)
        {
            var pcm = new byte[3200];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = fill;
            voice.OnBinary(pcm, 0, pcm.Length);
        }

        public void Tick() => typeof(AlbieVoice).GetMethod("Update", Any).Invoke(voice, null);

        public void Dispose()
        {
            voice.Suspend();  // nothing more goes out, the open session closes
            foreach (var s in sockets) s.Abort();
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    /// <summary>Stands in for the voice service's socket: records what is sent, and <see cref="Feed"/> delivers a message
    /// to AlbieVoice's real receive loop.</summary>
    sealed class FakeSocket : WebSocket
    {
        public readonly List<JObject> sent = new List<JObject>();
        public bool aborted;
        WebSocketState state = WebSocketState.Open;
        TaskCompletionSource<WebSocketReceiveResult> pending;
        ArraySegment<byte> into;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string CloseStatusDescription => null;
        public override string SubProtocol => null;
        public override WebSocketState State => state;

        public override void Abort()
        {
            aborted = true;
            End(WebSocketState.Aborted);
        }

        public override void Dispose() => End(WebSocketState.Closed);

        public override Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken cancel)
        {
            End(WebSocketState.Closed);
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken cancel) => CloseAsync(status, description, cancel);

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken cancel)
        {
            if (state != WebSocketState.Open) throw new InvalidOperationException("send on a closed socket");
            sent.Add(JObject.Parse(Encoding.UTF8.GetString(buffer.Array, buffer.Offset, buffer.Count)));
            return Task.CompletedTask;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancel)
        {
            into = buffer;
            pending = new TaskCompletionSource<WebSocketReceiveResult>();
            return pending.Task;
        }

        public void Feed(JObject message)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
            Expect(pending != null, "nobody is receiving on this socket");
            Expect(bytes.Length <= into.Count, "message bigger than the receive buffer");
            Buffer.BlockCopy(bytes, 0, into.Array, into.Offset, bytes.Length);
            var p = pending;
            pending = null;
            p.SetResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true));
        }

        void End(WebSocketState to)
        {
            if (state == WebSocketState.Open) state = to;
            var p = pending;
            pending = null;
            p?.SetResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        }
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    // A facing wake turn that ends empty: the session is open and no turn is left open.
    static FakeSocket OpenSession(Rig r)
    {
        int before = r.sockets.Count;
        r.WakeTurn(true);
        r.Event("stt.cancel");
        r.Event("stt.stop");
        Expect(r.sockets.Count == before + 1, "a facing turn opened no session");
        return r.Last;
    }

    static float Window() => Const("FollowUpS") + Const("IdleCloseS");

    static float Const(string name)
    {
        var field = typeof(AlbieVoice).GetField(name, Any);
        Expect(field != null, $"AlbieVoice.{name} not found");
        return (float)field.GetRawConstantValue();
    }

    static JObject Msg(string type) => new JObject { ["type"] = type };

    static string Types(FakeSocket s)
    {
        var types = new List<string>();
        foreach (var m in s.sent) types.Add((string)m["type"]);
        return string.Join(" ", types);
    }

    static int Count(FakeSocket s, string type)
    {
        int n = 0;
        foreach (var m in s.sent) if ((string)m["type"] == type) n++;
        return n;
    }

    static int Audio(JObject append) => Convert.FromBase64String((string)append["audio"])[0];

    static Vector3 At(float degrees, float distance) => Quaternion.Euler(0f, degrees, 0f) * Vector3.forward * distance;

    static bool Cone(Vector3 target, float radius)
    {
        float angle = 0f, limit = 0f;
        return AlbieFollowUp.InLookCone(Vector3.zero, Vector3.forward, target, radius, ref angle, ref limit);
    }

    static void Set(object target, string field, object value)
    {
        var f = target.GetType().GetField(field, Any);
        Expect(f != null, $"{target.GetType().Name}.{field} not found");
        f.SetValue(target, value);
    }

    sealed class Failed : Exception
    {
        public Failed(string message) : base(message) { }
    }

    static void Expect(bool condition, string message)
    {
        if (!condition) throw new Failed(message);
    }

    static int Step(StringBuilder sb, ref int skipped, string name, Func<string> check)
    {
        string tag, detail;
        try
        {
            detail = check();
            tag = detail.StartsWith("skipped") ? "skip" : "ok  ";
        }
        catch (Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            detail = e is Failed ? e.Message : e.GetType().Name + ": " + e.Message;
            tag = "FAIL";
        }
        if (tag == "skip") skipped++;
        sb.Append("  ").Append(tag).Append(' ').Append(name).Append(": ").Append(detail).Append('\n');
        return tag == "FAIL" ? 1 : 0;
    }
}
