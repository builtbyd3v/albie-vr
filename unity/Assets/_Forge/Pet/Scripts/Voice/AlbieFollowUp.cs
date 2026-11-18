using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// Conversation follow-up: when Albie finishes speaking an answer it keeps listening hands-free for
    /// listenSeconds (PushToTalk.BeginHandsFree), so the user can just reply. If nobody speaks (or the reply was empty)
    /// it turns around and wanders off to idle (AlbieController.WanderAway), which reads clearly as "done".
    /// Skipped while the brain is still working (Thinking/Working: the spoken line was only "on it"), after errors,
    /// when a session is already open (barge-in, Space held), and with the typed STT field (it would just sit open).
    /// If Albie starts speaking again during the window, the window closes without asking anything.
    /// Voice barge-in: while Albie speaks, the mic is monitored (HubSpeechToText.StartMonitoring, nothing sent).
    /// Speech clearly louder than Albie's own echo opens a hands-free turn and cuts Albie off; the hub
    /// stops Grok on stt.start and the monitored pre-roll keeps the first words.
    /// Look-to-talk: the follow-up window only watches the mic (nothing sent). Speech while facing Albie opens a
    /// normal turn; speech while looking elsewhere opens a gated turn (stt.start {require_name}) that the hub answers
    /// only if it names Albie ("Albie, ..."), so teammates talking at the desk are not answered. Facing = within
    /// LookAngle of its body (plus its apparent size), within the last lookGrace seconds, or while it sits on the palm.
    /// Cutting Albie off: facing it stops it at once; otherwise only "Albie, stop" does (the hub decides).
    /// Space / A / the whistle always open a turn.
    /// WakeListener reads UserFacing too: facing Albie answers any hands-free turn, not only in this window.
    /// </summary>
    [DisallowMultipleComponent]
    public class AlbieFollowUp : MonoBehaviour
    {
        // Free speech: face Albie and talk. Max angle (degrees) between where the user looks and Albie's body; his
        // apparent size (~14 deg at the desk) is added on top. Quest 2026-11-03: facing him read 0-16 deg, side talk
        // 30-62 deg; 15 felt too strict, so 30 (+ size, ~44) catches a loose look and still drops most side talk.
        const float LookAngle = 30f;

        [SerializeField, Tooltip("TTS to follow (ITextToSpeech). Auto-found in children / parents if empty.")]
        MonoBehaviour tts;
        [SerializeField, Tooltip("Auto-found on this GameObject / scene if empty.")]
        PushToTalk pushToTalk;
        [SerializeField, Tooltip("Auto-found on this GameObject / parents / scene if empty.")]
        AlbieController controller;
        [SerializeField, Tooltip("How long to keep listening for a reply after an answer (s).")]
        float listenSeconds = 15f;
        [SerializeField, Tooltip("Pause after speech ends before the mic opens (s): skips the speaker's tail.")]
        float startDelay = 0.35f;
        [SerializeField, Tooltip("Nobody replied: walk off and idle away from the user.")]
        bool wanderAwayWhenDone = true;

        [Header("Voice barge-in")]
        [SerializeField, Tooltip("Talking over Albie cuts it off, no button needed (mic permission must already be granted).")]
        bool voiceBargeIn = true;
        // Defaults from an AirPods run: speech 0.05-0.3, noise floor 0.006, Albie's echo ~0.01 x its output level.
        [SerializeField, Tooltip("Mic RMS (0..1) that can count as the user talking over Albie.")]
        float bargeLevel = 0.012f;
        [SerializeField, Tooltip("Mic RMS must also be above this share of Albie's output level (rejects its own echo). Raise it if Albie cuts itself off.")]
        float echoShare = 0.2f;
        [SerializeField, Tooltip("The echo gate never asks for more than this mic RMS. Raise it if Albie cuts itself off.")]
        float maxEcho = 0.012f;
        [SerializeField, Tooltip("Seconds of speech before it counts; quiet gaps between syllables count down instead of resetting.")]
        float bargeSeconds = 0.3f;
        // A one-word "Stop!" lasts ~0.3 s, too short for bargeSeconds at normal level, but it is loud (0.15-0.25).
        [SerializeField, Tooltip("Mic RMS that counts as a short, loud word (\"Stop!\").")]
        float loudLevel = 0.05f;
        [SerializeField, Tooltip("Seconds above loudLevel that are enough on their own.")]
        float loudSeconds = 0.15f;
        // Live 2026-10-25: a short "Yeah." after "Want me to apply it?" often never opened a turn (0.3 s over 0.03 is
        // most of the word) while the WakeListener, which would hear it, is off during this window. No echo then.
        [SerializeField, Tooltip("After he stops talking: seconds over bargeLevel that open the reply turn.")]
        float replySeconds = 0.15f;

        [Header("Look to talk")]
        [SerializeField, Tooltip("Hands-free speech only counts while the user faces Albie, or names it (\"Albie, ...\"). Buttons and the whistle always work.")]
        bool lookToTalk = true;
        [SerializeField, Tooltip("Looking at Albie this recently (s) still counts: you glance at it, then turn to speak.")]
        float lookGrace = 1.5f;
        [SerializeField, Tooltip("Facing Albie counts as saying its name. Off: every hands-free sentence needs \"Albie\" (at the desk it sits in front of the screen, so looking at the screen read as looking at it). Pair with the hub's VOICE_FACING=1.")]
        bool lookCountsAsName = true;

        ITextToSpeech _tts;
        ITalkLevel _talk;
        HubSpeechToText _monitor;
        Renderer _body;
        bool _following, _subscribed, _speaking, _attending;
        float _startAt = -1f, _speechFor, _loudFor, _attendUntil, _lastLookAt = -10f, _retryAt = -1f, _peakMic, _peakEcho;

        /// <summary>Angle (degrees) to Albie's body at the last check, and the cone it had to fall in: for logs.</summary>
        public float LastLookAngle { get; private set; }
        public float LastLookLimit { get; private set; }

        void Awake()
        {
            if (pushToTalk == null) pushToTalk = GetComponent<PushToTalk>();
            if (pushToTalk == null) pushToTalk = FindAnyObjectByType<PushToTalk>();
            if (controller == null) controller = GetComponentInParent<AlbieController>();
            if (controller == null) controller = FindAnyObjectByType<AlbieController>();
        }

        void Start()   // after AlbieAgentBridge.Awake, which may add the runtime NullTextToSpeech
        {
            _tts = tts as ITextToSpeech;
            if (_tts == null) _tts = GetComponentInChildren<ITextToSpeech>(true);
            if (_tts == null) _tts = GetComponentInParent<ITextToSpeech>();
            if (_tts == null || pushToTalk == null || controller == null)
            {
                Debug.LogWarning("[AlbieFollowUp] Needs an ITextToSpeech, PushToTalk and AlbieController; disabled.", this);
                enabled = false;
                return;
            }
            _talk = _tts as ITalkLevel;
            _body = controller.GetComponentInChildren<SkinnedMeshRenderer>();   // aim at the body, not the feet
            _tts.SpeechStarted += OnSpeechStarted;
            _tts.SpeechEnded += OnSpeechEnded;
            pushToTalk.SessionEnded += OnSessionEnded;
            _subscribed = true;
        }

        void OnDestroy()
        {
            if (!_subscribed) return;
            if (_tts != null) { _tts.SpeechStarted -= OnSpeechStarted; _tts.SpeechEnded -= OnSpeechEnded; }
            if (pushToTalk != null) pushToTalk.SessionEnded -= OnSessionEnded;
        }

        void OnSpeechStarted()
        {
            _startAt = -1f;
            _speaking = true;
            _attending = false;                       // talking over our own window: close it, ask nothing
            if (_following)
            {
                _following = false;
                pushToTalk.CancelSession();
            }
            // Live 2026-10-25 (5x): Microphone.Start right after the WakeListener let go failed with error 80, and
            // that reply could not be stopped by voice. One retry a moment later.
            if (voiceBargeIn && !StartMonitor()) _retryAt = Time.time + 0.3f;
        }

        float projectPromptUntil = -1f;

        public void ExpectProjectReply() => projectPromptUntil = Time.unscaledTime + 30f;

        void OnSpeechEnded()
        {
            _speaking = false;
            // One line per answer: the loudest mic level heard over him vs the gate, to tune bargeLevel / maxEcho.
            Debug.Log($"[AlbieFollowUp] answer ended: peak mic {_peakMic:F3}, peak echo gate {_peakEcho:F3}, monitored={_monitor != null}");
            _peakMic = _peakEcho = 0f;
            if (pushToTalk.SessionActive) { StopMonitor(); return; }   // barge-in / Space: someone is already talking
            if (Time.unscaledTime < projectPromptUntil)
            {
                projectPromptUntil = -1f;
                StopMonitor();
                if (pushToTalk.Provider is HubSpeechToText mic) mic.RequireName = false;
                if (pushToTalk.CanListenHandsFree) _following = pushToTalk.BeginHandsFree(listenSeconds);
                return;
            }
            AlbieState s = controller.State;
            if (s == AlbieState.Answering || s == AlbieState.Wandering || s == AlbieState.TaskComplete)
                _startAt = Time.time + startDelay;    // the mic keeps running into the follow-up window (no restart)
            else StopMonitor();
        }

        void Update()
        {
            if (lookToTalk && FacingNow()) _lastLookAt = Time.time;
            if (_retryAt >= 0f && Time.time >= _retryAt)
            {
                _retryAt = -1f;
                if (_speaking && voiceBargeIn) StartMonitor();
            }
            if (_monitor != null && _monitor.IsMonitoring && (_speaking || _attending)) CheckSpeech();
            else if (!_speaking && !_attending && _startAt < 0f && _retryAt < 0f && !pushToTalk.SessionActive
                     && pushToTalk.Provider is HubSpeechToText stray && stray.IsMonitoring)
            {
                // Nobody is watching this mic, but it keeps the WakeListener off (sessionBusy): let it go.
                Debug.LogWarning("[AlbieFollowUp] stray mic monitor (not speaking, no follow-up window): stopping it.");
                stray.StopMonitoring();
                _monitor = null;
            }
            if (_attending && Time.time >= _attendUntil)
            {
                _attending = false;                   // nobody replied
                StopMonitor();
                if (wanderAwayWhenDone && !pushToTalk.SessionActive) controller.WanderAway();
            }
            if (_startAt < 0f || Time.time < _startAt) return;
            _startAt = -1f;
            AlbieState s = controller.State;
            if (pushToTalk.SessionActive || s == AlbieState.Thinking || s == AlbieState.Working || s == AlbieState.Error) { StopMonitor(); return; }
            if (lookToTalk && StartMonitor()) { _attending = true; _attendUntil = Time.time + listenSeconds; }
            else if (OpenFallbackTurn()) _following = true;
            else if (wanderAwayWhenDone) controller.WanderAway();
        }

        // The no-monitor follow-up turn (mic busy, other STT). True when it opened. Internal for Forge > Core > Check Voice.
        internal bool OpenFallbackTurn()
        {
            if (pushToTalk.CanListenHandsFree && GateNextTurn() && pushToTalk.BeginHandsFree(listenSeconds)) return true;
            // A gate set for a turn that never opened would gate the next one (the palm's, a button's) instead.
            if (pushToTalk.Provider is HubSpeechToText gated) gated.RequireName = false;
            return false;
        }

        // The no-monitor fallback (mic busy, other STT) is a follow-up too: gated like CheckSpeech's turns. Live 18:31 it
        // went out ungated and "He's not into that, bro." was answered.
        bool GateNextTurn()
        {
            if (pushToTalk.Provider is HubSpeechToText hub) hub.RequireName = !(lookCountsAsName && Facing());
            return true;
        }

        bool StartMonitor()
        {
            if (pushToTalk.SessionActive || !pushToTalk.CanListenHandsFree || !(pushToTalk.Provider is HubSpeechToText hub)) return false;
            _monitor = hub;
            _speechFor = _loudFor = 0f;
            hub.StartMonitoring();                    // no-op if the mic is already running
            return hub.IsMonitoring;
        }

        void StopMonitor()
        {
            if (_monitor != null) { _monitor.StopMonitoring(); _monitor = null; }
        }

        /// <summary>The user looks at Albie, or did within lookGrace, or he sits on their palm (the palm gesture called
        /// him: that conversation is with him wherever they look). False with look-to-talk off or no camera.</summary>
        public bool UserFacing => lookToTalk && CountsAsFacing(FacingNow(), Time.time - _lastLookAt, lookGrace, controller != null && controller.Perched);

        // Looking now, or within the grace, or perched on the palm. Internal for Forge > Core > Check Voice.
        internal static bool CountsAsFacing(bool lookingNow, float sinceLook, float grace, bool perched) => lookingNow || sinceLook <= grace || perched;

        // Within LookAngle of Albie's body, plus half its apparent size, so it works across the desk and up close.
        bool FacingNow()
        {
            var cam = Camera.main;
            if (cam == null || controller == null) return false;
            Vector3 target = controller.transform.position;
            float radius = 0.1f;
            if (_body != null) { Bounds b = _body.bounds; target = b.center; radius = b.extents.magnitude; }
            float angle = LastLookAngle, limit = LastLookLimit;
            bool inCone = InLookCone(cam.transform.position, cam.transform.forward, target, radius, ref angle, ref limit);
            LastLookAngle = angle;
            LastLookLimit = limit;
            return inCone;
        }

        // The look cone: within LookAngle of the target plus its apparent half-size; closer than 10 cm always counts (angle
        // and limit are then left as they were). Internal for Forge > Core > Check Voice.
        internal static bool InLookCone(Vector3 eye, Vector3 forward, Vector3 target, float radius, ref float angle, ref float limit)
        {
            Vector3 to = target - eye;
            float dist = to.magnitude;
            if (dist < 0.1f) return true;
            angle = Vector3.Angle(forward, to);
            limit = LookAngle + Mathf.Rad2Deg * Mathf.Atan2(radius, dist);
            return angle <= limit;
        }

        bool Facing() => !lookToTalk || Camera.main == null || UserFacing;   // no way to tell: every turn counts

        void CheckSpeech()
        {
            bool facing = lookCountsAsName && Facing();
            // Live 2026-10-25 11:04: talking over a 53 s answer and "stop" never cut it off. Level is 0.4-1.0 while he
            // talks, so the echo gate asked for 0.08-0.2: shouting. Cap it at maxEcho; a loud word ignores it.
            float echo = _speaking && _talk != null ? Mathf.Min(_talk.Level * echoShare, maxEcho) : 0f;
            float level = _monitor.Level, dt = Time.deltaTime;
            if (_speaking) { _peakMic = Mathf.Max(_peakMic, level); _peakEcho = Mathf.Max(_peakEcho, echo); }
            float need = _speaking ? bargeSeconds : replySeconds;
            _speechFor = level >= bargeLevel && level >= echo ? _speechFor + dt : Mathf.Max(0f, _speechFor - dt);
            _loudFor = level >= loudLevel ? _loudFor + dt : Mathf.Max(0f, _loudFor - dt);
            if (_speechFor < need && _loudFor < loudSeconds) return;
            var hub = _monitor;
            bool cutIn = _speaking, reply = _attending;
            _monitor = null;
            _attending = false;
            hub.RequireName = !facing;                // looking elsewhere: the hub answers (or stops him) only on "Albie, ..."
            // Open the turn first (stt.start sends the pre-roll; SessionActive keeps OnSpeechEnded from reopening), then go quiet locally.
            if (pushToTalk.BeginHandsFree())
            {
                pushToTalk.MarkSpeechHeard();         // 0.3 s of speech is already in the pre-roll
                // Quiet at once, but only when facing him: side talk over him must not cut him off (or reach Grok).
                if (cutIn && facing) _tts.Stop();
                _following = reply;
            }
            else { hub.RequireName = false; hub.StopMonitoring(); }
        }

        /// <summary>Headset off / back on (AppLifecycle): close the follow-up window and let go of the mic, asking nothing.</summary>
        public void Halt()
        {
            projectPromptUntil = -1f;
            _startAt = _retryAt = -1f;
            _attending = _following = _speaking = false;
            StopMonitor();
        }

        void OnSessionEnded(bool answered)
        {
            if (!_following) return;
            _following = false;
            if (!answered && wanderAwayWhenDone && !pushToTalk.SessionActive) controller.WanderAway();
        }
    }
}
