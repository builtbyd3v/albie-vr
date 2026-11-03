using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Glue between the event source and Albie body. The source is the live hub (AgentClient on ForgeNet) unless a
    /// MockEventSource sits on this Albie, which only the sandbox scene builders add; Main has no mock mode.
    /// Implements the event → AlbieState table of docs/archive/PET_AGENT_CONTRACT.md.
    /// Only events for this Albie's pet_id are handled (several agents share one socket, see AlbieCrew); a crew
    /// Albie uses Albie's source (UseSharedSource) instead of opening its own.
    /// </summary>
    public class AlbieAgentBridge : MonoBehaviour
    {
        [SerializeField, Tooltip("Auto-found on this GameObject / parents / scene if empty.")]
        AlbieController controller;
        [SerializeField, Tooltip("Auto-found on this GameObject / parents / scene if empty.")]
        AlbieExpressions expressions;
        [SerializeField, Tooltip("Auto-found in children / scene if empty.")]
        AlbieCaption caption;
        [SerializeField, Tooltip("Auto-found on this GameObject if empty. stt.* only mean Listening while it has a turn open.")]
        PushToTalk pushToTalk;
        [SerializeField, Tooltip("Optional TTS (must implement ITextToSpeech). Falls back to a runtime NullTextToSpeech.")]
        MonoBehaviour tts;
        [SerializeField, Tooltip("Enter AlbieState.Intro on Start and go to Wandering when the controller reports IntroComplete.")]
        bool playIntroOnStart = true;
        [SerializeField, Tooltip("When TTS finishes and Albie is still Answering, return to Wandering.")]
        bool wanderAfterSpeech = true;

        /// <summary>True when pet_config.json selected the live hub (set once the config has loaded).</summary>
        public static bool EventSourceIsHub { get; private set; }

        public IPetEventSource Source { get; private set; }
        public PetConfig Config { get; private set; }
        /// <summary>This Albie's pet_id: the config's (Albie) or the crew type id.</summary>
        public string PetId => _crewPetId ?? (Config != null ? Config.petId : "albie");
        /// <summary>An ask is awaiting its answer or a code task is running.</summary>
        public bool TaskInProgress => _awaitingAnswer || _taskRunning;
        /// <summary>Working on something, incl. turns the hub started on its own (a crew task Albie assigned).</summary>
        public bool Busy => TaskInProgress || _hubBusy;

        ITextToSpeech _tts;
        bool _awaitingAnswer, _taskRunning, _hubBusy;
        string _pendingAsk, _pendingMode;
        string _crewPetId;                   // set for crew members: events / asks use this pet_id
        AlbieAgentBridge _sharedFrom;        // crew: Albie's bridge, whose Source we borrow (never connect / disconnect it)
        readonly HashSet<string> _unknownLogged = new HashSet<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => EventSourceIsHub = false;

        void Awake()
        {
            if (controller == null) controller = GetComponentInParent<AlbieController>();
            if (controller == null) controller = FindAnyObjectByType<AlbieController>();
            if (expressions == null) expressions = GetComponentInParent<AlbieExpressions>();
            if (expressions == null) expressions = FindAnyObjectByType<AlbieExpressions>();
            if (caption == null) caption = GetComponentInChildren<AlbieCaption>(true);
            if (caption == null) caption = FindAnyObjectByType<AlbieCaption>();
            if (pushToTalk == null) pushToTalk = GetComponent<PushToTalk>();

            _tts = tts != null ? tts as ITextToSpeech : null;   // Unity null: a crew copy's voice child was removed
            if (_tts == null) _tts = GetComponent<ITextToSpeech>();
            if (_tts == null) _tts = GetComponentInChildren<ITextToSpeech>(true);   // e.g. StreamedPcmTextToSpeech on a "Voice" child
            if (_tts == null) _tts = gameObject.AddComponent<NullTextToSpeech>();
            _tts.SpeechEnded += OnSpeechEnded;
        }

        void Start()
        {
            if (controller != null)
            {
                controller.IntroComplete += OnIntroComplete;
                if (playIntroOnStart) controller.SetState(AlbieState.Intro);
            }
            if (_sharedFrom == null) StartCoroutine(PetConfig.LoadAsync(OnConfigLoaded));
        }

        /// <summary>crew member: use <paramref name="lead"/>'s event source with its own pet_id. Call before Start.</summary>
        public void UseSharedSource(string petId, AlbieAgentBridge lead)
        {
            _crewPetId = petId;
            _sharedFrom = lead;
            playIntroOnStart = true;
        }

        // Crew: Albie's source exists once his config has loaded; attach then (polled, cheap).
        void Update()
        {
            if (_sharedFrom == null || Source != null || _sharedFrom.Source == null) return;
            Config = _sharedFrom.Config;
            Source = _sharedFrom.Source;
            Source.EventReceived += OnEvent;
            if (_pendingAsk != null) { var t = _pendingAsk; _pendingAsk = null; Ask(t, _pendingMode); }
        }

        void OnConfigLoaded(PetConfig cfg)
        {
            Config = cfg ?? new PetConfig();
            var hub = GetComponent<AgentClient>();
            var mock = GetComponent<MockEventSource>();
            // Mocks exist in the sandbox scenes only (the builder adds the component there); the runtime prefab never has
            // one, so Main always talks to the hub even if pet_config.json says "mock".
            bool useMock = mock != null && mock.enabled && (mock.OverridesConfig || !Config.IsHub);
            if (!Config.IsHub && !useMock)
                Debug.LogWarning("[AlbieAgentBridge] pet_config.json asks for eventSource \"mock\" but this albie has no enabled MockEventSource (sandbox only); using the hub.", this);
            EventSourceIsHub = !useMock;
            Debug.Log("[AlbieAgentBridge] " + Config + (useMock ? " -> MockEventSource (sandbox)" : " -> hub via ForgeNet"));

            if (useMock)
            {
                if (hub != null) { hub.Disconnect(); hub.enabled = false; }
                Source = mock;
            }
            else
            {
                if (hub == null) hub = gameObject.AddComponent<AgentClient>();
                if (mock != null) { mock.Disconnect(); mock.enabled = false; }
                Source = hub;
            }

            var hubStt = GetComponent<HubSpeechToText>();
            if (hubStt != null) hubStt.PetId = Config.petId;

            Source.EventReceived += OnEvent;
            Source.Connect(Config);

            if (_pendingAsk != null) { var t = _pendingAsk; _pendingAsk = null; Ask(t, _pendingMode); }
        }

        /// <summary>Sends agent.ask (cancelling any in-flight ask/task first). mode: "auto" | "qa" | "code".</summary>
        public void Ask(string text, string mode = "auto")
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (Source == null) { _pendingAsk = text; _pendingMode = mode; return; } // config still loading
            string petId = PetId;
            if (TaskInProgress) Source.Send("agent.cancel", new JObject { ["pet_id"] = petId });
            _taskRunning = false;
            _awaitingAnswer = true;
            Source.Send("agent.ask", new JObject
            {
                ["pet_id"] = petId, ["project_id"] = Config.projectId, ["text"] = text, ["mode"] = mode ?? "auto",
            });
        }

        // ---------------- event → state table ----------------

        void OnEvent(PetEvent e)
        {
            if (e == null || string.IsNullOrEmpty(e.type)) return;
            string pid = e.GetString("pet_id");
            if (!string.IsNullOrEmpty(pid) && pid != PetId) return;   // another Albie's event
            TrackBusy(e);
            switch (e.type)
            {
                case "stt.partial":
                case "stt.final":
                {
                    // Transcripts alone don't make him listen: only a turn this Albie opened (push-to-talk, hands-free,
                    // the whistle) does, or an explicit pet.state listening from the hub. A wake segment that was for
                    // someone else, or an empty final (dropped / cancelled / silent turn), changes nothing and shows nothing.
                    string text = e.GetString("text");
                    if (string.IsNullOrWhiteSpace(text)) break;
                    bool ownTurn = pushToTalk != null && pushToTalk.SessionActive;
                    // Don't drag Albie back to Listening once PushToTalk moved on to Thinking/Answering.
                    if (ownTurn && (State == AlbieState.Listening || State == AlbieState.Wandering || State == AlbieState.Following))
                        SetStateIfChanged(AlbieState.Listening);
                    Caption(text, e.type == "stt.partial");
                    break;
                }

                case "agent.thinking":
                    SetStateIfChanged(AlbieState.Thinking);
                    ApplyMood(Mood.Squint);
                    break;

                case "agent.partial_answer":
                    SetStateIfChanged(AlbieState.Answering);
                    Caption(e.GetString("text"), true);
                    break;

                case "agent.answer_done":
                    _awaitingAnswer = false;
                    Answer(e.GetString("text"));
                    break;

                case "pet.say":
                    Answer(e.GetString("text"));
                    break;

                case "agent.task_started":
                    _taskRunning = true;
                    SetStateIfChanged(AlbieState.Working);
                    Caption(e.GetString("title", "Working on it..."), true);
                    break;

                case "agent.task_progress":
                    _taskRunning = true;
                    SetStateIfChanged(AlbieState.Working);
                    string progress = e.GetString("text");
                    if (e.Has("pct")) progress += "  " + Mathf.RoundToInt(e.GetFloat("pct")) + "%";
                    Caption(progress, true);
                    break;

                case "agent.task_complete":
                    _taskRunning = false; _awaitingAnswer = false;
                    if (controller != null) controller.SetState(AlbieState.TaskComplete); // controller spins, then Wandering
                    Caption(e.GetString("summary", "Done!"), false);
                    break;

                case "agent.error":
                    _taskRunning = false; _awaitingAnswer = false;
                    if (_tts != null) _tts.Stop();
                    if (controller != null) controller.SetState(AlbieState.Error); // controller plays Fear + Sad on entry
                    ApplyMood(Mood.Sad);
                    Caption(e.GetString("message", "Something went wrong."), false);
                    break;

                case "pet.state":
                    HandlePetState(e.GetString("mode"), e.GetString("target_panel", null));
                    break;

                // Voice mode: the hub streams speech (StreamedPcmTextToSpeech plays it and raises SpeechStarted/Ended).
                case "tts.start":
                    SetStateIfChanged(AlbieState.Answering);
                    break;
                case "tts.end":
                case "tts.stop":
                case "voice.ready":
                    break;

                case "agent.tool_call":   // the voice model handed work to a tool (delegate = the Claude brain)
                    SetStateIfChanged(AlbieState.Thinking);
                    ApplyMood(Mood.Squint);
                    break;

                default:
                    if (_unknownLogged.Add(e.type)) Debug.Log("[AlbieAgentBridge] Ignoring event type '" + e.type + "' (logged once).");
                    break;
            }
        }

        void TrackBusy(PetEvent e)
        {
            switch (e.type)
            {
                case "agent.thinking": case "agent.partial_answer": case "agent.task_started": case "agent.task_progress":
                case "agent.tool_call":
                    _hubBusy = true; break;
                case "agent.answer_done": case "agent.task_complete": case "agent.error":
                    _hubBusy = false; break;
                case "pet.state":
                    string mode = e.GetString("mode");
                    if (mode == "idle") _hubBusy = false;
                    else if (mode == "thinking" || mode == "working") _hubBusy = true;
                    break;
            }
        }

        // target_panel ("screen-main", "cad-viewer", "messages"): Thinking / Working walk to that panel (AlbieController).
        void HandlePetState(string mode, string targetPanel)
        {
            switch (mode)
            {
                case "follow": SetStateIfChanged(AlbieState.Following); break;
                case "idle": SetStateIfChanged(AlbieState.Wandering); break;
                case "listening": SetStateIfChanged(AlbieState.Listening); break;
                case "thinking": SetBusyState(AlbieState.Thinking, targetPanel); ApplyMood(Mood.Squint); break;
                case "working": SetBusyState(AlbieState.Working, targetPanel); break;
                case "speaking": SetStateIfChanged(AlbieState.Answering); break;
                default:
                    if (_unknownLogged.Add("pet.state:" + mode)) Debug.Log("[AlbieAgentBridge] Unknown pet.state mode '" + mode + "'.");
                    break;
            }
        }

        void Answer(string text)
        {
            SetStateIfChanged(AlbieState.Answering); // controller plays Bounce + Happy on entry
            Caption(text, false);
            ApplyMood(Mood.Happy);
            if (_tts != null && !string.IsNullOrEmpty(text)) _tts.Speak(text);
        }

        // ---------------- helpers ----------------

        AlbieState State => controller != null ? controller.State : AlbieState.Wandering;

        void SetStateIfChanged(AlbieState s)
        {
            if (controller != null && controller.State != s) controller.SetState(s);
        }

        void SetBusyState(AlbieState s, string targetPanel)
        {
            if (controller == null) return;
            if (!string.IsNullOrEmpty(targetPanel)) controller.SetState(s, targetPanel);   // re-targets even in the same state
            else if (controller.State != s) controller.SetState(s);
        }

        void ApplyMood(Mood m)
        {
            if (expressions != null) expressions.SetMood(m);
        }

        void Caption(string text, bool partial)
        {
            if (caption != null && !string.IsNullOrEmpty(text)) caption.SetText(text, partial);
        }

        void OnIntroComplete()
        {
            if (controller != null && controller.State == AlbieState.Intro) controller.SetState(AlbieState.Wandering);
        }

        void OnSpeechEnded()
        {
            if (wanderAfterSpeech && controller != null && controller.State == AlbieState.Answering)
                controller.SetState(AlbieState.Wandering);
        }

        void OnDestroy()
        {
            if (controller != null) controller.IntroComplete -= OnIntroComplete;
            if (_tts != null) _tts.SpeechEnded -= OnSpeechEnded;
            if (Source != null)
            {
                Source.EventReceived -= OnEvent;
                if (_sharedFrom == null) Source.Disconnect();   // a crew member only borrowed Albie's
            }
        }
    }
}
