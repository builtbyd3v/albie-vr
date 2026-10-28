using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Albie's voice with the hub off: the headset talks to the realtime voice API (Grok) itself. It is ForgeNet's
/// <see cref="ForgeNet.ILocalPeer"/>, so the pet scripts don't change: what they would send the hub (hello, stt.start,
/// 16 kHz PCM16 mic frames, stt.stop / stt.cancel, tts.stop, workspace.mode) arrives here, and it answers with the
/// hub's events (voice.ready, stt.partial/final, agent.*, tts.start + PCM16 frames + tts.end/stop, pet.state), as
/// hub/agent/voice.py does (contract: docs/PET_AGENT_CONTRACT.md Addendum B). Tool calls run on the device through
/// <see cref="AlbieTools"/>. Kept from voice.py: barge-in, the no-reply guard, tool results after response.done with
/// one response.create. Not yet: wake/name gating, the reply-length cut, delegate (the Claude runner).
/// Everything runs on the main thread (the socket's awaits resume on Unity's synchronization context).
/// Installs itself; off in a build with no voice token server (<see cref="AlbieVoiceToken"/>).
/// </summary>
public class AlbieVoice : MonoBehaviour, ForgeNet.ILocalPeer
{
    const string PetId = "bit";
    const string Url = "wss://api.x.ai/v1/realtime?model=grok-voice-3.0";
    const string VoiceName = "sirius";
    const float Speed = 0.85f;
    const int InputRate = 16000;
    const int MinCommitBytes = 3200;  // under 0.1 s of audio: nothing was said
    const float NoReplyS = 8f;        // committed turn with no transcript and no reply by then: an empty turn
    const float IdleCloseS = 120f;    // close the socket when unused (a session costs while open); reopens on the next turn
    static readonly int[] SupportedRates = { 8000, 16000, 22050, 24000, 32000, 44100, 48000 };

    // From hub/agent/voice.py (PERSONA, BREVITY, BREVITY_NOW); the tool rules cover only the tools on the device.
    const string Persona =
        "You are Albie (said AL-bee), a small, cute and very competent alligator who lives on the user's desk in Forge, " +
        "a mixed-reality workspace for engineers. Talk like a cheerful cartoon sidekick: warm, bouncy and always " +
        "helpful. Never use interjections or filler sounds such as 'ooh', 'hehe', 'wow' or 'yay'; show " +
        "personality through word choice instead. No lists, no markdown, never read code aloud. Transcripts often turn \"ML\" into \"email\": Forge has no email panel, so " +
        "\"email\" means ML (the machine learning desk).";
    const string ToolRules =
        " Use your tools yourself for quick actions: call them instead of guessing, then say in one short line what you did. " +
        "ui_open_view opens a view at the desk (cad is 3D modeling, code is the code editor, ml is machine learning, messages is the messenger). " +
        "Set focus timers, keep the user's notes and run the movie (movie_ tools) with your desk tools (one notes_add call per item: 'add X and Y' is two calls). " +
        "Use the crew (spawn_agent, assign_task, dismiss_agent, crew_status) only when the user names a crew member or asks for parallel help; after assigning, just say who's on it. " +
        "If a tool returns an error, say it plainly. Change 3D designs with the cad_ tools and train models in the machine learning view " +
        "with the ml_ tools; they run on Albie's cloud, so if one says the cloud isn't reachable, say that part needs the internet right now.";
    const string Brevity =
        " Most important rule: every reply is one or two short sentences, 20 words at most, under eight seconds out " +
        "loud. Lead with the answer, no warm-up, no recap. Even if the user asks for everything or a long answer, give " +
        "only the key point, then offer to say more. If the user asks you to do something (open, close, set, add, " +
        "remove, show), call the tool for it first and never say it is done without the tool call; then confirm in one " +
        "short sentence, under 10 words.";
    const string BrevityNow =
        " If the user asked you to do something (open, close, set, add, remove, show), call the tool for it in this reply " +
        "first; never say it is done without the tool call. Then confirm in one short sentence, under 10 words. A question: " +
        "at most 20 words. Reading a tool result: say only the one or two things that answer the user, never a list of values.";
    const string Instructions = Persona + ToolRules + Brevity;
    static readonly string[] Keyterms = { "Albie", "ML", "ML panel", "CAD", "notes", "focus timer", "quadcopter", "rocket", "gearbox" };
    static readonly Dictionary<string, string> ViewNotes = new Dictionary<string, string>
    {
        ["code"] = "The user switched to the CODE view (the code editor on the main screen).",
        ["cad"] = "The user switched to the CAD view (the open design on the desk).",
        ["ml"] = "The user switched to the ML view (training data and runs).",
        ["messages"] = "The user switched to the MESSAGES view.",
        [""] = "The user closed the workspace views.",
    };

    ClientWebSocket ws;
    Task<bool> connecting;
    CancellationTokenSource life;
    readonly Queue<string> outbox = new Queue<string>();
    bool pumping;
    float lastActivity;
    int outRate = 48000;

    // The turn and the reply in progress.
    int turnSeq, fed;
    bool turnOpen, turnCancelled, committed, finalSent;
    string partial = "";
    bool responseActive, audioStarted, answerDone;
    string utterance, answer = "", petMode;
    readonly List<JObject> pendingOutputs = new List<JObject>();
    int toolsRunning;   // slow tools (the cloud's) still working: their results go back with the others
    string pendingSay;  // an albie.say that came in while a reply was running

    /// <summary>Editor only: EditorPrefs bool; true keeps the voice off in Play, so tests spend no voice credits.</summary>
    public const string EditorOffPref = "Albie.VoiceOffInEditor";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
#if UNITY_EDITOR
        if (UnityEditor.EditorPrefs.GetBool(EditorOffPref, false))
        {
            Debug.Log($"[AlbieVoice] off in the Editor (EditorPrefs {EditorOffPref}).");
            return;
        }
#endif
        if (!Application.isEditor && string.IsNullOrEmpty(AlbieVoiceToken.TokenUrl))
        {
            Debug.Log($"[AlbieVoice] off: no voice token server (PlayerPrefs {AlbieVoiceToken.TokenUrlPref}).");
            return;
        }
        if (FindAnyObjectByType<AlbieVoice>() == null) new GameObject("AlbieVoice").AddComponent<AlbieVoice>();
    }

    void OnEnable()
    {
        life = new CancellationTokenSource();
        ForgeNet.LocalPeer = this;
    }

    void OnDisable()
    {
        if (ForgeNet.LocalPeer == this) ForgeNet.LocalPeer = null;
        life.Cancel();
        ws?.Dispose();
        ws = null;
    }

    void Update()
    {
        if (ws != null && ws.State == WebSocketState.Open && !turnOpen && !responseActive && Time.realtimeSinceStartup - lastActivity > IdleCloseS)
        {
            Debug.Log("[AlbieVoice] idle: closing the voice session (it reopens on the next turn).");
            var idle = ws;
            ws = null;
            idle.Abort();
            idle.Dispose();
        }
    }

    // ---------------------------------------------------------------- from Unity (ForgeNet.Send / SendBinary)

    public void OnEvent(string type, JToken payload)
    {
        lastActivity = Time.realtimeSinceStartup;
        switch (type)
        {
            case "hello":
                int rate = payload?.Value<int?>("audio_out_rate") ?? 0;
                outRate = Array.IndexOf(SupportedRates, rate) >= 0 ? rate : 48000;
                ForgeNet.Inject("voice.ready", new JObject { ["pet_id"] = PetId, ["provider"] = "grok", ["sample_rate"] = outRate });
                _ = EnsureConnected();  // connect now, so the first turn doesn't wait for it
                break;
            case "stt.start":
                if (responseActive || audioStarted) StopSpeech("barge_in");
                turnSeq++;
                turnOpen = true;
                turnCancelled = committed = finalSent = false;
                fed = 0;
                partial = "";
                Send(new JObject { ["type"] = "input_audio_buffer.clear" });
                break;
            case "stt.cancel":
                turnCancelled = true;
                Send(new JObject { ["type"] = "input_audio_buffer.clear" });
                break;
            case "stt.stop":
                CommitTurn();
                break;
            case "tts.stop":
                StopSpeech("cancel");
                break;
            case "messages.focus":  // "Ask Albie" on a message (the demo conversations send its text along)
                string text = (string)payload?["text"];
                if (string.IsNullOrEmpty(text)) break;
                if (responseActive || audioStarted) StopSpeech("barge_in");
                // In this reply's own instructions: as a system note alone, Grok answered with a greeting instead.
                RequestResponse($" Right now the user tapped Ask Albie on this message from {(string)payload["user_name"] ?? "someone"}: \"{text}\". " +
                                $"Say who it's from and what they want, in one short sentence, and ask if they want to reply. If they do, call messages_send with reply_to_ts \"{(string)payload["ts"]}\" and their words.");
                break;
            case "albie.note":  // a module's context for the voice model (the hub's note()), e.g. the Neural Net Lab's fix offer
                if (ws != null && ws.State == WebSocketState.Open && !string.IsNullOrEmpty((string)payload?["text"]))
                    Send(new JObject { ["type"] = "conversation.item.create", ["item"] = new JObject
                    {
                        ["type"] = "message", ["role"] = "system",
                        ["content"] = new JArray(new JObject { ["type"] = "input_text", ["text"] = (string)payload["text"] }),
                    } });
                break;
            case "pet.speak":  // a line from Albie's cloud (ForgeCloud: an ML proposal, a finished run)
            case "albie.say":  // a slow tool finished (code_ask, code_edit, a crew task): say its result now
                string say = (string)payload?["text"];
                if (string.IsNullOrEmpty(say)) break;
                string now = $" Right now, without being asked, tell the user this result in your own words, in one or two short sentences: \"{say}\"";
                if (responseActive || turnOpen) pendingSay = now;  // after the reply in progress, or the user's turn
                else RequestResponse(now);
                break;
            case "workspace.mode":
                if (ws != null && ws.State == WebSocketState.Open && ViewNotes.TryGetValue((string)payload?["mode"] ?? "", out string note))
                    Send(new JObject { ["type"] = "conversation.item.create", ["item"] = new JObject
                    {
                        ["type"] = "message", ["role"] = "system",
                        ["content"] = new JArray(new JObject { ["type"] = "input_text", ["text"] = note }),
                    } });
                break;
        }
    }

    public void OnBinary(byte[] data, int offset, int count)
    {
        if (!turnOpen) return;
        fed += count;
        lastActivity = Time.realtimeSinceStartup;
        Send(new JObject { ["type"] = "input_audio_buffer.append", ["audio"] = Convert.ToBase64String(data, offset, count) });
    }

    void CommitTurn()
    {
        if (!turnOpen) return;
        turnOpen = false;
        if (fed < MinCommitBytes || turnCancelled)
        {
            Send(new JObject { ["type"] = "input_audio_buffer.clear" });
            Final("");
            if (pendingSay != null) { RequestResponse(pendingSay); pendingSay = null; }  // an empty turn gets no reply to wait for
            return;
        }
        // Grok answers every commit by itself: no response.create here (a second one is rejected or answers twice).
        Send(new JObject { ["type"] = "input_audio_buffer.commit" });
        committed = true;
        Emit("agent.thinking", new JObject(), "thinking");
        StartCoroutine(NoReplyGuard(turnSeq));
    }

    // Grok drops a turn it hears no speech in (no transcript, no reply, no error): end it, or Unity stays in Thinking.
    IEnumerator NoReplyGuard(int seq)
    {
        yield return new WaitForSecondsRealtime(NoReplyS / 2);
        if (seq == turnSeq && !finalSent && !responseActive) RequestResponse();  // ask once, halfway
        yield return new WaitForSecondsRealtime(NoReplyS / 2);
        if (seq == turnSeq && !finalSent && !responseActive) Final("");
    }

    void StopSpeech(string reason)
    {
        if (responseActive) Send(new JObject { ["type"] = "response.cancel" });
        if (audioStarted) ForgeNet.Inject("tts.stop", new JObject { ["pet_id"] = PetId, ["utterance_id"] = utterance, ["reason"] = reason });
        responseActive = audioStarted = false;
        pendingOutputs.Clear();
    }

    void RequestResponse(string now = BrevityNow) =>
        Send(new JObject { ["type"] = "response.create", ["response"] = new JObject { ["instructions"] = Instructions + now } });

    // ---------------------------------------------------------------- from Grok

    void Handle(JObject msg)
    {
        lastActivity = Time.realtimeSinceStartup;
        string type = (string)msg["type"] ?? "";
        switch (type)
        {
            case "conversation.item.input_audio_transcription.updated":
            case "conversation.item.input_audio_transcription.delta":
                Transcript((string)msg["transcript"] ?? partial + ((string)msg["delta"] ?? ""));
                break;
            case "conversation.item.input_audio_transcription.completed":
                // Grok also sends one per segment while the user is still talking; only the one after commit is final.
                if (committed) Final((string)msg["transcript"]);
                else Transcript((string)msg["transcript"] ?? "");
                break;
            case "response.created":
                if (partial.Length > 0) Final(null);  // no .completed yet: the last transcript is final
                utterance = Guid.NewGuid().ToString("N").Substring(0, 12);
                answer = "";
                responseActive = true;
                audioStarted = answerDone = false;
                break;
            case "response.output_audio.delta":
            case "response.audio.delta":
                if (!responseActive || msg["delta"] == null) break;
                Final(null);
                byte[] pcm = Convert.FromBase64String((string)msg["delta"]);
                if (!audioStarted)
                {
                    audioStarted = true;
                    ForgeNet.Inject("tts.start", new JObject
                    { ["pet_id"] = PetId, ["utterance_id"] = utterance, ["sample_rate"] = outRate, ["encoding"] = "pcm16", ["channels"] = 1 });
                }
                ForgeNet.InjectBinary(pcm, 0, pcm.Length);
                break;
            case "response.output_audio_transcript.delta":
            case "response.audio_transcript.delta":
                if (!responseActive || msg["delta"] == null) break;
                Final(null);
                answer += (string)msg["delta"];
                Emit("agent.partial_answer", new JObject { ["text"] = answer }, "speaking");
                break;
            case "response.output_audio_transcript.done":
            case "response.audio_transcript.done":
                if (responseActive) AnswerDone((string)msg["transcript"] ?? answer);
                break;
            case "response.done":
                if (responseActive)
                {
                    if (audioStarted) ForgeNet.Inject("tts.end", new JObject { ["pet_id"] = PetId, ["utterance_id"] = utterance });
                    if (answer.Length > 0) AnswerDone(answer);
                }
                responseActive = audioStarted = false;
                if (toolsRunning > 0) { }  // the last slow tool to answer sends them all
                else if (pendingOutputs.Count > 0) SendToolOutputs();  // after the calling reply, then ONE response.create
                else if (pendingSay != null)
                {
                    RequestResponse(pendingSay);
                    pendingSay = null;
                }
                break;
            case "response.function_call_arguments.done":
                RunTool((string)msg["name"] ?? "", (string)msg["call_id"] ?? "", (string)msg["arguments"]);
                break;
            case "error":
                string message = msg["error"] is JObject err ? (string)err["message"] : msg["error"]?.ToString();
                string low = (message ?? "").ToLowerInvariant();
                // Races with Grok that lose nothing: a cancel after the reply ended, a create while its own reply runs.
                if (low.Contains("item not found") || low.Contains("no active response") ||
                    (low.Contains("response") && (low.Contains("active") || low.Contains("in progress"))))
                {
                    Debug.Log($"[AlbieVoice] grok: {message} (harmless)");
                    break;
                }
                Debug.LogWarning($"[AlbieVoice] grok error: {msg.ToString(Formatting.None)}");
                if (committed) Final("");
                Emit("agent.error", new JObject { ["message"] = "Voice error: " + (message ?? "unknown") }, "idle");
                break;
        }
    }

    void RunTool(string name, string callId, string arguments)
    {
        JObject args;
        try { args = string.IsNullOrWhiteSpace(arguments) ? new JObject() : JObject.Parse(arguments); }
        catch (JsonException) { args = new JObject(); }
        Emit("agent.tool_call", new JObject { ["call_id"] = callId, ["name"] = name, ["args"] = args }, "thinking");
        int seq = turnSeq;
        toolsRunning++;
        AlbieTools.CallAsync(name, args, result =>
        {
            toolsRunning--;
            Debug.Log($"[AlbieVoice] tool {name} {args.ToString(Formatting.None)} -> {result.ToString(Formatting.None)}");
            if (seq != turnSeq) return;  // the user moved on to a new turn
            pendingOutputs.Add(new JObject
            {
                ["type"] = "conversation.item.create",
                ["item"] = new JObject { ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = result.ToString(Formatting.None) },
            });
            if (!responseActive && toolsRunning == 0) SendToolOutputs();  // a slow tool, after its reply ended
        });
    }

    void SendToolOutputs()
    {
        foreach (var output in pendingOutputs) Send(output);
        pendingOutputs.Clear();
        RequestResponse();
    }

    void Transcript(string text)
    {
        if (finalSent || string.IsNullOrEmpty(text)) return;
        partial = text;
        Emit("stt.partial", new JObject { ["text"] = text }, "listening");
    }

    // The turn's transcript; null = the last partial. Every turn Unity opened ends with exactly one (empty is fine).
    void Final(string text)
    {
        if (finalSent) return;
        finalSent = true;
        text = (text ?? partial).Trim();
        if (text.Length > 0) Debug.Log($"[AlbieVoice] heard: {text}");
        Emit("stt.final", new JObject { ["text"] = text, ["voice"] = true }, text.Length > 0 ? "thinking" : null);
    }

    void AnswerDone(string text)
    {
        if (answerDone) return;
        answerDone = true;
        Emit("agent.answer_done", new JObject { ["text"] = text }, "speaking");
    }

    // An event for the pet, after the pet.state its mode implies (the hub's Emitter does this; only on a change).
    void Emit(string type, JObject payload, string mode)
    {
        payload["pet_id"] = PetId;
        if (mode != null && mode != petMode)
        {
            petMode = mode;
            ForgeNet.Inject("pet.state", new JObject { ["pet_id"] = PetId, ["mode"] = mode });
        }
        ForgeNet.Inject(type, payload);
    }

    // ---------------------------------------------------------------- the socket

    void Send(JObject message)
    {
        outbox.Enqueue(message.ToString(Formatting.None));
        if (!pumping) _ = Pump();
    }

    // One sender, in order (a WebSocket takes one send at a time; append must stay before commit).
    async Task Pump()
    {
        pumping = true;
        try
        {
            while (outbox.Count > 0)
            {
                if (!await EnsureConnected())
                {
                    outbox.Clear();
                    break;
                }
                await Raw(ws, outbox.Dequeue());
            }
        }
        catch (Exception e) when (!(e is OperationCanceledException))
        {
            Debug.LogWarning($"[AlbieVoice] send failed: {e.Message}");
            outbox.Clear();
        }
        catch (OperationCanceledException) { }
        finally { pumping = false; }
    }

    Task<bool> EnsureConnected()
    {
        if (ws != null && ws.State == WebSocketState.Open) return Task.FromResult(true);
        if (connecting == null || connecting.IsCompleted) connecting = Connect();
        return connecting;
    }

    async Task<bool> Connect()
    {
        ClientWebSocket socket = null;
        try
        {
            string token = await AlbieVoiceToken.Fetch();
            socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            await socket.ConnectAsync(new Uri(Url), life.Token);
            await Raw(socket, SessionUpdate().ToString(Formatting.None));
            ws = socket;
            lastActivity = Time.realtimeSinceStartup;
            _ = Receive(socket);
            Debug.Log("[AlbieVoice] connected to the voice service from the device.");
            return true;
        }
        catch (Exception e)
        {
            socket?.Dispose();
            if (life.IsCancellationRequested) return false;
            Debug.LogWarning($"[AlbieVoice] can't reach the voice service: {e.Message}");
            if (turnOpen || committed) Final("");
            Emit("agent.error", new JObject { ["message"] = "Voice error: can't reach the voice service" }, "idle");
            return false;
        }
    }

    JObject SessionUpdate() => new JObject
    {
        ["type"] = "session.update",
        ["session"] = new JObject
        {
            ["instructions"] = Instructions,
            ["turn_detection"] = null,  // Unity says when a turn ends (stt.stop)
            ["reasoning"] = new JObject { ["effort"] = "none" },
            ["audio"] = new JObject
            {
                ["input"] = new JObject
                {
                    ["format"] = new JObject { ["type"] = "audio/pcm", ["rate"] = InputRate },
                    ["transcription"] = new JObject { ["model"] = "grok-transcribe", ["keyterms"] = new JArray(Keyterms) },
                },
                ["output"] = new JObject { ["format"] = new JObject { ["type"] = "audio/pcm", ["rate"] = outRate }, ["speed"] = Speed },
            },
            ["voice"] = VoiceName,
            ["tools"] = AlbieTools.Definitions(),
        },
    };

    Task Raw(ClientWebSocket socket, string text) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, life.Token);

    async Task Receive(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), life.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                JObject msg;
                try { msg = JObject.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)); }
                catch (JsonException) { continue; }
                try { Handle(msg); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (ws == socket) { Debug.LogWarning($"[AlbieVoice] voice connection dropped: {e.Message}"); }
        catch (Exception) { }  // a socket we closed ourselves (idle)
        finally
        {
            if (ws == socket)
            {
                ws = null;
                if (responseActive || audioStarted) StopSpeech("error");
                if (committed) Final("");
            }
            socket.Dispose();
        }
    }
}
