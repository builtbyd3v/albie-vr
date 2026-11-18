using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

// The checks (Assembly-CSharp-Editor) reach the internal seams below: Forge > Core > Check Voice / Check Lifecycle.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Assembly-CSharp-Editor")]

/// <summary>
/// Albie's voice with the hub off: the headset talks to the realtime voice API (Grok) itself. It is ForgeNet's
/// <see cref="ForgeNet.ILocalPeer"/>, so the pet scripts don't change: what they would send the hub (hello, stt.start,
/// 16 kHz PCM16 mic frames, stt.stop / stt.cancel, tts.stop, workspace.mode) arrives here, and it answers with the
/// hub's events (voice.ready, stt.partial/final, agent.*, tts.start + PCM16 frames + tts.end/stop, pet.state), as
/// hub/agent/voice.py does (contract: docs/archive/PET_AGENT_CONTRACT.md Addendum B). Tool calls run on the device through
/// <see cref="AlbieTools"/>. Kept from voice.py: barge-in, the no-reply guard, tool results after response.done with
/// one response.create. Not yet: name gating, the reply-length cut, delegate (the Claude runner).
/// Cost: xAI bills every minute the session is open, so it opens only for a turn (lazy), room speech (WakeListener's
/// wake, follow-up and barge-in turns) reaches it only while the user faces Albie, and it closes FollowUpS + IdleCloseS after his last word.
/// A new session gets the last few lines in its instructions, so a reconnect keeps the thread. Cold start: a turn's
/// mic frames wait in the outbox while the session connects, then go out in order after session.update, so the first
/// words aren't lost (the follow-up / wake pre-roll is in there too).
/// Headset off / app paused: <see cref="AppLifecycle"/> calls <see cref="Suspend"/> (session closed, nothing sent) and
/// <see cref="Resume"/> (the next turn reconnects lazily).
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
    const float RetryS = 3f;          // committed turn with no reply by then: ask once more (Grok sometimes skips its auto-reply)
    const float FollowUpS = 15.5f;    // AlbieFollowUp's reply window after his speech (listenSeconds 15 + startDelay)
    const float IdleCloseS = 20f;     // then close (billed per open minute); the next turn reconnects while the user still talks
    const int HistoryLines = 8;       // recent lines carried into a new session's instructions
    const int HistoryChars = 200;
    static readonly int[] SupportedRates = { 8000, 16000, 22050, 24000, 32000, 44100, 48000 };

    // From hub/agent/voice.py (PERSONA, BREVITY, BREVITY_NOW); the tool rules cover only the tools on the device.
    const string Persona =
        "You are Albie (said AL-bee), a small, cute and very competent assistant who lives on the user's desk in " +
        "a mixed-reality workspace for engineers. Talk like a cheerful cartoon sidekick: warm, bouncy and always " +
        "helpful. Never use interjections or filler sounds such as 'ooh', 'hehe', 'wow' or 'yay'; show " +
        "personality through word choice instead. No lists, no markdown, never read code aloud. Transcripts often turn \"ML\" into \"email\": there is no email panel here, so " +
        "\"email\" means ML (the machine learning desk).";
    const string ToolRules =
        " Use your tools yourself for quick actions: call them instead of guessing, then say in one short line what you did. " +
        "ui_open_view opens a view at the desk (cad is 3D modeling, code is the code editor, ml is machine learning, messages is the messenger). " +
        "Set focus timers and keep the user's notes with your desk tools (one notes_add call per item: 'add X and Y' is two calls). " +
        "Use the crew (spawn_agent, assign_task, dismiss_agent, crew_status) only when the user names a crew member or asks for parallel help; after assigning, just say who's on it. " +
        "The Albie menu holds the user's tools, projects and artifacts: os_menu opens or closes it and os_tab shows one tab. 'Open CAD' or 'open the notebook' is os_open with kind tool and its id (cad, code, train, notebook, drive, messages, gestures), which works away from the desk too; if os_open has no such tool, try ui_open_view. 'Open the truck project' is kind project, id proj_truck, and 'open settings' is kind system. 'Bring my windows here' is os_windows with action recenter, and 'close everything' is action close_all. " +
        "The Projects tab shows the user's own projects first, then four samples (RC truck, Quadcopter, Rocket, Image classifier): worked examples, already part built with history, to look around. There is one way to start a project: Build something ('build me a ...', or os_open kind system, id build), a few questions and then a plan. From a sample, project_copy is Make my own: it opens those questions with the sample's kind picked, and Start makes the user's own project ('My RC truck') with its own plan; nothing is copied. The teammates in the sample projects' message threads are a sample team, not real people. " +
        "When the user wants to teach you a hand gesture, say 'show me three times' in the same reply as calling gesture_teach; it answers when the takes are over, so then say if it worked. " +
        "If a tool returns an error, say it plainly. Change 3D designs with the cad_ tools and train models in the machine learning view " +
        "with the ml_ tools; they run on Albie's cloud, so if one says the cloud isn't reachable, say that part needs the internet right now. " +
        "To pull an ML repo from GitHub ('clone the pytorch cifar repo'), call ml_github: it finds the repo first, so say its name and ask, " +
        "and only after a yes call it again with confirm true. The user's own repos and pull requests in the code view are the github_ tools; " +
        "github_approve_pr and github_merge_pr first read the pull request back: ask 'approve it?' or 'merge it?' and only after a yes call again with confirm true. " +
        "The template_, brain_ and autopilot_ tools run on this headset with no internet: 'train the ML template' is template_train, never ml_train. " +
        "'Drive it yourself twice and I'll learn it' is one call, brain_record with start true, laps 2 and then_autopilot true. " +
        "Starting a new machine-learning project or model ('start an RNN project', 'build a CNN on handwritten digits'), or fine-tuning a pretrained Hugging Face model ('fine-tune distilbert on movie reviews'), is one call, ml_new, never artifact_create or ml_open; pass base_model when they name the model to fine-tune, and it downloads the data and weights, writes the code, opens the ML desk and trains by itself. To put that project on GitHub ('create a PR', 'push it to GitHub') call ml_publish: it says the repo first, so ask, and only after a yes call it again with confirm true; if the user asked for a PR up front, pass publish true to ml_new. " +
        "On a 3D model that's open or that the user names (heart, eye, brain, cell, plant_cell, dna), act with artifact_explore, never by making a new artifact: 'expand the heart', 'take it apart' or 'explode it' is action separate, 'put it back' is assemble, 'cut it open' or 'show me inside' cuts it, 'X-ray it' sees through it, 'show me the mitral valve' is focus with that part, 'turn it' turns it, 'reset it' puts everything back; pass template when they name a model that may not be open, and it opens it first. " +
        "To explain how something works ('explain the blood flow', 'how does the eye see', 'tour the brain', 'how is a protein made'), call artifact_press with what they said: it plays the model's walkthrough and returns its steps; explain them in order, one short sentence per step, as they play. Before answering a question about the open model, call artifact_describe so you talk about the parts that are really there. " +
        "To make, build or show something, call artifact_create. Use a template when one fits: 'make me a slalom' or an obstacle course is template slalom, " +
        "'a hoop course' is template hoop_course, 'what shipped this week' is template repo_activity, and the tool lists the others. For anything else, " +
        "write the manifest and scene yourself in the same call, as the tool's guide says. You choose positions inside the artifact (pos in metres from its " +
        "centre); you never choose room positions, so never use space room yourself. First say in under eight words what you're building. If the result " +
        "is invalid, fix every error and call again without talking; when it says stop, say what went wrong and offer the closest template. " +
        "If the result has say, tell the user that. " +
        "Keep the lab notebook with the notebook_ tools: 'that didn't work' is notebook_mark failed, then ask one question about why; " +
        "comparing versions, like v1 against v2, is notebook_compare, and speak its say line.";
    const string Brevity =
        " Most important rule: every reply is one or two short sentences, 20 words at most, under eight seconds out " +
        "loud. Lead with the answer, no warm-up, no recap. Even if the user asks for everything or a long answer, give " +
        "only the key point, then offer to say more. If the user asks you to do something (make, build, create, show, open, " +
        "close, set, add, remove), call the tool for it first and never say it is done without the tool call; then confirm in one " +
        "short sentence, under 10 words. Never say you are doing or starting something ('setting it up now', 'downloading') " +
        "unless you call its tool in this same reply; if you still need a yes or a detail, ask for it instead.";
    const string BrevityNow =
        " If the user asked you to do something (make, build, create, show, open, close, set, add, remove), call the tool for it in this reply " +
        "first; never say it is done without the tool call, and never say you can't make something before calling artifact_create. " +
        "Then confirm in one short sentence, under 10 words. A question: " +
        "at most 20 words. Reading a tool result: say only the one or two things that answer the user, never a list of values.";
    const string Instructions = Persona + ToolRules + Brevity;
    static readonly string[] Keyterms =
        { "Albie", "ML", "ML panel", "CAD", "notes", "focus timer", "quadcopter", "rocket", "gearbox", "slalom", "hoop course", "Autopilot", "notebook", "Albie menu", "projects", "artifacts", "fine-tune", "DistilBERT", "Hugging Face", "X-ray", "mitral valve", "ventricle", "cornea", "cerebellum" };
    static readonly Dictionary<string, string> ViewNotes = new Dictionary<string, string>
    {
        ["code"] = "The user switched to the CODE view (the code editor on the main screen).",
        ["cad"] = "The user switched to the CAD view (the open design on the desk).",
        ["ml"] = "The user switched to the ML view (training data and runs).",
        ["messages"] = "The user switched to the MESSAGES view.",
        [""] = "The user closed the workspace views.",
    };

    WebSocket ws;  // a ClientWebSocket in the app (OpenGrok)
    Task<bool> connecting;
    CancellationTokenSource life;
    readonly Queue<string> outbox = new Queue<string>();
    bool pumping;
    float lastActivity;
    float speechEnd;  // when the reply's audio stops playing on the device (deltas arrive faster than real time)
    readonly List<string> history = new List<string>();
    string instructions = Instructions;  // this session's: Instructions + the recent lines it started with
    int outRate = 48000;
    bool away;  // Suspend..Resume: nothing goes to Grok and no session opens
    int epoch;  // bumped by Suspend: a Connect that started before it gives up instead of opening a session
    int connectingEpoch;
    float connectBackoff, nextConnectAt;  // after a failed connect (a 401 token): no new one before nextConnectAt

    // Seams for Forge > Core > Check Voice (Core/Editor/VoiceCheck.cs). The app keeps these defaults; the check swaps in
    // a fake clock, token and socket, so the outbox, the idle close and a pause run with no network.
    internal Func<float> clock = RealTime;
    internal Func<Task<string>> fetchToken = AlbieVoiceToken.Fetch;
    internal Func<string, CancellationToken, Task<WebSocket>> openSocket = OpenGrok;
    /// <summary>Between <see cref="Suspend"/> and <see cref="Resume"/>.</summary>
    internal bool Suspended => away;
    static float RealTime() => Time.realtimeSinceStartup;

    // The turn and the reply in progress.
    int turnSeq, fed;
    bool turnOpen, turnCancelled, committed, finalSent, droppedTurn;
    bool heardText, replied, retried;  // this turn: a non-empty final, a response.created, the one retry sent
    int guardSeq = -1;                 // the committed turn the no-reply guard watches (turnSeq), from guardAt
    float guardAt;
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
        NoReplyGuard();
        if (IdleCloseDue(ws != null && ws.State == WebSocketState.Open))
        {
            Debug.Log("[AlbieVoice] idle: closing the voice session (it reopens on the next turn).");
            var idle = ws;
            ws = null;
            idle.Abort();
            idle.Dispose();
        }
    }

    /// <summary>Close the open session: no turn, reply or slow tool, and more than FollowUpS + IdleCloseS since Albie's
    /// last word or the last turn / reply message (<see cref="CountsAsActivity"/>).</summary>
    internal bool IdleCloseDue(bool open) =>
        open && !turnOpen && !responseActive && toolsRunning == 0 && clock() - Mathf.Max(lastActivity, speechEnd) > FollowUpS + IdleCloseS;

    /// <summary>Headset off / app paused: end the turn and the reply, close the session (it bills per open minute and
    /// its socket doesn't survive a suspend) and send nothing until <see cref="Resume"/>. The history stays, so the
    /// next session keeps the thread.</summary>
    public void Suspend()
    {
        if (away) return;
        away = true;
        epoch++;
        if (turnOpen || committed) Final("");
        turnOpen = committed = false;
        turnSeq++;  // that turn's NoReplyGuard and slow tool results drop out
        bool streaming = audioStarted;
        StopSpeech("pause");
        // A finished reply's tail can still be playing from the speaker's buffer: quiet it too.
        if (!streaming) ForgeNet.Inject("tts.stop", new JObject { ["pet_id"] = PetId, ["utterance_id"] = utterance, ["reason"] = "pause" });
        pendingSay = null;
        outbox.Clear();
        if (petMode != "idle")
        {
            petMode = "idle";
            ForgeNet.Inject("pet.state", new JObject { ["pet_id"] = PetId, ["mode"] = "idle" });
        }
        if (ws != null)
        {
            var open = ws;
            ws = null;
            open.Abort();
            open.Dispose();
        }
        Debug.Log("[AlbieVoice] suspended: voice session closed (the next turn reconnects).");
    }

    /// <summary>Back from <see cref="Suspend"/>. Connects nothing: the next turn opens the session.</summary>
    public void Resume() => away = false;

    // ---------------------------------------------------------------- from Unity (ForgeNet.Send / SendBinary)

    public void OnEvent(string type, JToken payload)
    {
        switch (type)
        {
            case "hello":
                int rate = payload?.Value<int?>("audio_out_rate") ?? 0;
                outRate = Array.IndexOf(SupportedRates, rate) >= 0 ? rate : 48000;
                ForgeNet.Inject("voice.ready", new JObject { ["pet_id"] = PetId, ["provider"] = "grok", ["sample_rate"] = outRate });
                break;  // no connect here: the first turn opens the session
            case "stt.start":
                // Hands-free speech reaches Grok only while the user faces Albie (each open minute is billed, and side
                // talk kept answering itself). Wake turns carry facing; follow-up / barge-in turns carry require_name
                // when not facing (AlbieFollowUp). Button and whistle turns always count. The hub drops these too.
                bool wake = payload?.Value<bool?>("wake") == true;
                if (wake ? payload.Value<bool?>("facing") != true : payload?.Value<bool?>("require_name") == true)
                {
                    droppedTurn = !wake;  // PushToTalk waits for this turn's stt.final; WakeListener doesn't
                    break;
                }
                lastActivity = clock();
                if (responseActive || audioStarted) StopSpeech("barge_in");
                turnSeq++;
                turnOpen = true;
                turnCancelled = committed = finalSent = heardText = false;
                fed = 0;
                partial = "";
                Send(new JObject { ["type"] = "input_audio_buffer.clear" });
                break;
            case "stt.cancel":
                if (!turnOpen) break;  // a dropped wake turn: nothing to clear, and no reason to connect
                turnCancelled = true;
                Send(new JObject { ["type"] = "input_audio_buffer.clear" });
                break;
            case "stt.stop":
                if (droppedTurn)
                {
                    droppedTurn = false;
                    Emit("stt.final", new JObject { ["text"] = "", ["voice"] = true }, null);
                    break;
                }
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
                Remember("Note: ", (string)payload?["text"]);
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
                if (!ViewNotes.TryGetValue((string)payload?["mode"] ?? "", out string note)) break;
                Remember("Note: ", note);
                if (ws != null && ws.State == WebSocketState.Open)
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
        lastActivity = clock();
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
        guardSeq = turnSeq;
        guardAt = clock();
        replied = retried = false;
    }

    // Grok drops a turn it hears no speech in (no transcript, no reply, no error): end it, or Unity stays in Thinking.
    // It also sometimes skips the reply to a turn it did transcribe (live 2026-11-17 19:22:21: heard, never answered):
    // RetryS after the commit with no response.created, ask once more.
    void NoReplyGuard()
    {
        if (guardSeq < 0) return;
        if (guardSeq != turnSeq) { guardSeq = -1; return; }  // a new turn, or a pause
        float waited = clock() - guardAt;
        if (!retried && !replied && waited >= RetryS && (!finalSent || heardText))
        {
            retried = true;
            Debug.Log($"[AlbieVoice] no reply {RetryS:0} s after the turn{(heardText ? " (it was heard)" : "")}: asking again.");
            RequestResponse();
        }
        if (waited < NoReplyS) return;
        guardSeq = -1;
        if (!finalSent && !responseActive) Final("");
    }

    void StopSpeech(string reason)
    {
        if (responseActive) Send(new JObject { ["type"] = "response.cancel" });
        if (audioStarted) ForgeNet.Inject("tts.stop", new JObject { ["pet_id"] = PetId, ["utterance_id"] = utterance, ["reason"] = reason });
        speechEnd = clock();
        responseActive = audioStarted = false;
        // The cancelled reply's tool results still go in, with no reply: the next turn then knows what was done (an artifact made).
        if (ws != null && ws.State == WebSocketState.Open) foreach (var output in pendingOutputs) Send(output);
        pendingOutputs.Clear();
    }

    // A response's instructions replace the session's, so they start from this session's (with its recent lines).
    void RequestResponse(string now = BrevityNow) =>
        Send(new JObject { ["type"] = "response.create", ["response"] = new JObject { ["instructions"] = instructions + now } });

    void Remember(string who, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        history.Add(who + (text.Length > HistoryChars ? text.Substring(0, HistoryChars) + "..." : text));
        if (history.Count > HistoryLines) history.RemoveAt(0);
    }

    // ---------------------------------------------------------------- from Grok

    void Handle(JObject msg)
    {
        string type = (string)msg["type"] ?? "";
        if (CountsAsActivity(type)) lastActivity = clock();
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
                replied = true;
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
                speechEnd = Mathf.Max(speechEnd, clock()) + pcm.Length / (outRate * 2f);
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
                Emit("agent.error", new JObject { ["message"] = "My voice had a hiccup. Try that again." }, "idle");
                break;
        }
    }

    // Only a turn or a reply is activity: Grok also sends a message every ~10 s while idle, which kept the old
    // 120 s idle close from ever firing (the session stayed open, and billed, as long as the app ran).
    internal static bool CountsAsActivity(string type) => type.StartsWith("response.") || type.StartsWith("conversation.item.input_audio");

    void RunTool(string name, string callId, string arguments)
    {
        JObject args;
        try { args = string.IsNullOrWhiteSpace(arguments) ? new JObject() : JObject.Parse(arguments); }
        catch (JsonException) { args = new JObject(); }
        Emit("agent.tool_call", new JObject { ["call_id"] = callId, ["name"] = name, ["args"] = args }, "thinking");
        int seq = turnSeq;
        var socket = ws;
        toolsRunning++;
        AlbieTools.CallAsync(name, args, result =>
        {
            toolsRunning--;
            Debug.Log($"[AlbieVoice] tool {name} call {callId} {args.ToString(Formatting.None)} -> {result.ToString(Formatting.None)}");
            var output = new JObject
            {
                ["type"] = "conversation.item.create",
                ["item"] = new JObject { ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = result.ToString(Formatting.None) },
            };
            if (seq != turnSeq)  // the user moved on to a new turn: the result still goes in (same session only), with no reply
            {
                if (socket != null && ws == socket && ws.State == WebSocketState.Open) Send(output);
                return;
            }
            pendingOutputs.Add(output);
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
        heardText = text.Length > 0;
        if (heardText) Debug.Log($"[AlbieVoice] heard: {text}");
        Remember("User: ", text);
        Emit("stt.final", new JObject { ["text"] = text, ["voice"] = true }, text.Length > 0 ? "thinking" : null);
    }

    void AnswerDone(string text)
    {
        if (answerDone) return;
        answerDone = true;
        Remember("Albie: ", text);
        Emit("agent.answer_done", new JObject { ["text"] = text }, "speaking");
        Debug.Log($"[AlbieVoice] said: {text}");
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
        if (away) return;
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
                int at = epoch;
                if (!await EnsureConnected())
                {
                    if (at != epoch && !away) continue;  // a connect from before a pause gave up: connect again for what came after
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
        // Every mic frame starts a send: without this, a 401 token fetch repeated about 10 times a second (live 18:59:28).
        if ((connecting == null || connecting.IsCompleted) && clock() < nextConnectAt) return Task.FromResult(false);
        if (connecting == null || connecting.IsCompleted || connectingEpoch != epoch)
        {
            connectingEpoch = epoch;
            connecting = Connect();
        }
        return connecting;
    }

    async Task<bool> Connect()
    {
        WebSocket socket = null;
        int started = epoch;
        try
        {
            string token = await fetchToken();
            if (started != epoch) return false;  // the headset came off meanwhile
            socket = await openSocket(token, life.Token);
            if (started != epoch)
            {
                socket.Dispose();
                return false;
            }
            instructions = history.Count == 0 ? Instructions
                : Instructions + " Earlier in this conversation, before a short pause: " + string.Join(" ", history);
            await Raw(socket, SessionUpdate().ToString(Formatting.None));
            if (started != epoch)  // the headset came off during the send: never keep a billed session open while away
            {
                socket.Abort();
                socket.Dispose();
                return false;
            }
            ws = socket;
            lastActivity = clock();
            connectBackoff = 0f;
            _ = Receive(socket);
            Debug.Log("[AlbieVoice] connected to the voice service from the device.");
            return true;
        }
        catch (Exception e)
        {
            socket?.Dispose();
            if (life.IsCancellationRequested || started != epoch) return false;
            connectBackoff = Mathf.Min(connectBackoff > 0f ? connectBackoff * 2f : 2f, 60f);
            nextConnectAt = clock() + connectBackoff;
            Debug.LogWarning($"[AlbieVoice] can't reach the voice service: {e.Message} (next try in {connectBackoff:0} s)");
            if (turnOpen || committed) Final("");
            Emit("agent.error", new JObject { ["message"] = "I can't reach my voice service right now." }, "idle");
            return false;
        }
    }

    static async Task<WebSocket> OpenGrok(string token, CancellationToken cancel)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            await socket.ConnectAsync(new Uri(Url), cancel);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    JObject SessionUpdate() => new JObject
    {
        ["type"] = "session.update",
        ["session"] = new JObject
        {
            ["instructions"] = instructions,
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

    Task Raw(WebSocket socket, string text) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, life.Token);

    async Task Receive(WebSocket socket)
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
