using System;
using System.Collections.Generic;
using Forge.Pet;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Forge.Demo
{
    /// <summary>
    /// The scripted video demo (docs/demo/TONIGHT_PLAN.md). Reads Resources/demo_script.json and plays its beats in order:
    /// Albie's lines (recorded audio, caption, mouth), then the cues, each an action the other demo pieces registered with
    /// <see cref="DemoActions"/>. A beat moves on when its advance condition comes true: Hudson finishes a sentence (any
    /// final speech-to-text result), a piece raises a signal, a timer runs out, or the laptop remote says so
    /// (ForgeNet demo.next / demo.back / demo.reset / demo.goto; keys ] [ \ on the desktop). It also does the B1 palm
    /// summon, the A/B buttons on the right controller (DemoDirector.Controller.cs, DemoConfig.stepButton), keeps the
    /// microphone open between lines when A is off, and cancels the live agent's own reply to Hudson's speech
    /// (see DemoDirector.Live.cs). Cues that never ran when a beat ends are fired at once, in order, so Next never leaves the
    /// scene half-built. Off unless Resources/demo_config.json says so (DemoInstaller) or a scene holds the prefab.
    /// </summary>
    public sealed partial class DemoDirector : MonoBehaviour
    {
        public static DemoDirector Current { get; private set; }

        [Tooltip("Above 0, every beat moves on by itself this many seconds after its lines and cues are done (Forge > Demo > Play Whole Script).")]
        public float forceAdvanceSeconds;
        public string scriptResource = "demo_script";

        public DemoScriptDef Script => script;
        public DemoConfig Config => config;
        public DemoVoice Voice => voice;
        /// <summary>Index of the running (or last run) beat; -1 before the first Next.</summary>
        public int BeatIndex { get; private set; } = -1;
        public bool BeatRunning => beatRunning;
        /// <summary>Every line of the running beat has been said (a beat with none counts as done).</summary>
        public bool LinesDone => beatRunning && linesDone;
        /// <summary>The last beat's advance came true: nothing is left to play.</summary>
        public bool Finished { get; private set; }
        public bool Armed => BeatIndex < 0 && !beatRunning && pendingStart < 0 && !Finished;
        public string BeatId => script != null && BeatIndex >= 0 && BeatIndex < script.beats.Count ? script.beats[BeatIndex].id : "";
        /// <summary>Seconds since the running beat started (0 when none runs).</summary>
        public float BeatSeconds => beatRunning ? Time.time - beatStart : 0f;
        public event Action<int> BeatStarted;
        public event Action ScriptFinished;

        sealed class CueRun { public DemoCueDef def; public bool fired; }
        sealed class LineRun { public DemoLineDef def; public bool started; }
        enum AltKind { Speech, Signal, Auto }
        struct Alt { public AltKind kind; public string name; public float seconds; }

        DemoScriptDef script;
        DemoConfig config;
        DemoVoice voice;
        AlbieController albie;
        AlbieCaption liveCaption;
        AlbieAgentBridge bridge;
        PushToTalk talk;
        float nextAlbieSearch, enabledAt;
        bool autoStarted, resetting;

        readonly List<IDisposable> subs = new List<IDisposable>();
        readonly Dictionary<string, float> signalTimes = new Dictionary<string, float>();
        readonly List<CueRun> cues = new List<CueRun>();
        readonly List<LineRun> lines = new List<LineRun>();
        readonly List<Alt> alts = new List<Alt>();
        readonly Queue<DemoCueDef> catchUp = new Queue<DemoCueDef>();

        bool beatRunning, linesDone, advanceHasSpeech, speechPending, forceDone;
        int pendingStart = -1, lineNow = -1;
        float beatStart, timelineStart, linesDoneAt, lastLineEnd, nextCatchUp, forceDoneAt;
        string speechText = "";

        const float EchoGuardSeconds = 0.9f;
        const float CatchUpGap = 0.12f;

        // ---------------------------------------------------------------- lifecycle

        void OnEnable()
        {
            Current = this;
            enabledAt = Time.time;
            config = DemoConfig.Load();
            script = DemoScriptDef.Load(scriptResource);
            DemoActions.Signal += OnSignal;
            DemoActions.Register(DemoActions.Reset, OnResetAction);
            RegisterActions();
            subs.Add(ForgeNet.On("demo.next", (JToken _) => Next()));
            subs.Add(ForgeNet.On("demo.back", (JToken _) => Back()));
            subs.Add(ForgeNet.On("demo.reset", (JToken _) => ResetAll()));
            subs.Add(ForgeNet.On("demo.goto", (JToken p) => Goto(p is JObject o ? (string)o["beat"] : null)));
            subs.Add(ForgeNet.On("stt.final", (JToken p) => OnFinalSpeech(p is JObject o ? (string)o["text"] : null)));
            DemoLayout.Capture();
            Debug.Log($"[Demo] Director on: {(script != null ? script.beats.Count : 0)} beats. ] next, [ back, \\ reset, ` palm.");
        }

        void OnDisable()
        {
            if (Current == this) Current = null;
            DemoActions.Signal -= OnSignal;
            DemoActions.Unregister(DemoActions.Reset, OnResetAction);
            UnregisterActions();
            for (int i = 0; i < subs.Count; i++) subs[i]?.Dispose();
            subs.Clear();
            RestoreLiveVoice();
            ReleaseAlbie();
        }

        void Update()
        {
            if (script == null) return;
            PollKeys();
            PollController();
            ResolveAlbie();
            SilenceLiveVoice();
            UpdatePalm();
            UpdateMute();

            if (Armed && !autoStarted && config.autoStartSeconds > 0f && Time.time - enabledAt >= config.autoStartSeconds)
            {
                autoStarted = true;
                Next();
            }

            DrainCatchUp();
            if (!beatRunning) return;
            RunTimeline();
            TryPoke();
            UpdateMic();
            CheckAdvance();
        }

        void PollKeys()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null) return;
            if (kb.rightBracketKey.wasPressedThisFrame) Next();
            if (kb.leftBracketKey.wasPressedThisFrame) Back();
            if (kb.backslashKey.wasPressedThisFrame) ResetAll();
            if (kb.backquoteKey.wasPressedThisFrame) palmKey = true;
        }

        // ---------------------------------------------------------------- the remote (and the keys)

        /// <summary>Next beat now. Before the first beat: starts B0. Cues of the beat that are still to run fire at once.</summary>
        public void Next()
        {
            if (script == null || Finished || pendingStart >= 0) return;
            if (!beatRunning)
            {
                if (BeatIndex < 0) BeginBeat(0, "remote");
                return;
            }
            if (summon == Summon.WaitPalm) ForceSummon();
            AdvanceFrom("remote");
        }

        /// <summary>Back one beat: everything resets, the beats before it are run through in a blink, and that beat plays again.</summary>
        public void Back()
        {
            if (script == null) return;
            int target = beatRunning || BeatIndex >= 0 ? Mathf.Max(0, BeatIndex - 1) : 0;
            GotoIndex(target);
        }

        /// <summary>Jump to a beat by id ("B3") or number. Reset first, then the earlier beats' cues run through quickly.</summary>
        public void Goto(string beat)
        {
            if (script == null) return;
            int index = script.IndexOf(beat);
            if (index < 0 && int.TryParse(beat, out int n)) index = Mathf.Clamp(n, 0, script.beats.Count - 1);
            if (index < 0)
            {
                Debug.LogWarning($"[Demo] goto: no beat '{beat}'.");
                return;
            }
            GotoIndex(index);
        }

        void GotoIndex(int index)
        {
            ResetAll();
            for (int b = 0; b < index; b++)
            {
                var cuesOfBeat = script.beats[b].cues;
                for (int c = 0; c < cuesOfBeat.Count; c++) catchUp.Enqueue(cuesOfBeat[c]);
            }
            pendingStart = index;
            nextCatchUp = Time.time + CatchUpGap;
        }

        /// <summary>Everything back to the start: audio stopped, Albie idle near the chair, every piece reset, the director armed at B0.</summary>
        public void ResetAll()
        {
            if (script == null || resetting) return;
            resetting = true;
            try
            {
                Debug.Log("[Demo] reset");
                ResetState();
                DemoActions.Fire(DemoActions.Reset);
            }
            finally { resetting = false; }
        }

        // The Director's own half of a reset. Also what runs when another station fires demo.reset by hand.
        void OnResetAction(DemoCue cue)
        {
            if (!resetting) ResetState();
        }

        void ResetState()
        {
            beatRunning = false;
            BeatIndex = -1;
            Finished = false;
            pendingStart = -1;
            lineNow = -1;
            speechPending = false;
            forceDone = false;
            ClearStep();
            catchUp.Clear();
            cues.Clear();
            lines.Clear();
            alts.Clear();
            signalTimes.Clear();
            autoStarted = true;   // an auto-start is for launch only, never after a reset
            muteUntil = 0f;
            if (voice != null) voice.Silence();
            if (talk != null && talk.SessionActive) talk.CancelSession();
            ResetSummon();
            DemoLayout.Capture();
        }

        // ---------------------------------------------------------------- beats

        void BeginBeat(int index, string reason)
        {
            BeatIndex = index;
            DemoBeatDef beat = script.beats[index];
            beatRunning = true;
            beatStart = Time.time;
            timelineStart = beatStart + Mathf.Max(0f, beat.lead);
            lineNow = -1;
            lastLineEnd = 0f;
            speechPending = false;
            forceDone = false;
            forceDoneAt = -1f;
            ClearStep();
            if (index == 0) DemoLayout.Capture();
            DismissArrival();   // the first-arrival card can come up mid-take (Hudson sits down after B0)

            cues.Clear();
            for (int i = 0; i < beat.cues.Count; i++) cues.Add(new CueRun { def = beat.cues[i] });
            lines.Clear();
            for (int i = 0; i < beat.lines.Count; i++) lines.Add(new LineRun { def = beat.lines[i] });
            linesDone = lines.Count == 0;
            linesDoneAt = timelineStart;
            ParseAdvance(beat.advance);

            Debug.Log($"[Demo] beat {beat.id} ({beat.title}), advance '{beat.advance}', started by {reason}.");
            BeatStarted?.Invoke(index);
        }

        void ParseAdvance(string advance)
        {
            alts.Clear();
            advanceHasSpeech = false;
            if (string.IsNullOrWhiteSpace(advance)) return;
            string[] parts = advance.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p == "speech") { alts.Add(new Alt { kind = AltKind.Speech }); advanceHasSpeech = true; }
                else if (p.StartsWith("signal:", StringComparison.Ordinal)) alts.Add(new Alt { kind = AltKind.Signal, name = p.Substring(7).Trim() });
                else if (p.StartsWith("auto:", StringComparison.Ordinal) && float.TryParse(p.Substring(5), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s))
                    alts.Add(new Alt { kind = AltKind.Auto, seconds = s });
                // "remote" (or anything else): only Next moves on
            }
        }

        void RunTimeline()
        {
            float now = Time.time;
            float t = now - timelineStart;
            if (t < 0f) return;

            for (int i = 0; i < cues.Count; i++)
            {
                CueRun c = cues[i];
                if (c.fired || !CueReady(c.def, t, now)) continue;
                c.fired = true;
                FireCue(c.def);
            }

            if (voice == null && !linesDone)
            {
                // No Albie in the scene: nothing can speak, so the lines count as said (the cues still run).
                linesDone = true;
                linesDoneAt = now;
            }
            if (linesDone || voice.Speaking) return;
            int next = -1;
            for (int i = 0; i < lines.Count; i++)
                if (!lines[i].started) { next = i; break; }
            if (next < 0) return;
            DemoLineDef line = lines[next].def;
            if (next > 0 && now < lastLineEnd + config.lineGapSeconds) return;
            if (!LineReady(line, t, now)) return;
            StartLine(next);
        }

        // The test run (forceAdvanceSeconds > 0) waits a quarter as long for a signal that never comes.
        float TimeoutScale => forceAdvanceSeconds > 0f ? 0.25f : 1f;

        /// <summary>The ` key: Hudson's palm is up (B1 summon, and the hand.palm_up signal). For tests and the desktop rig.</summary>
        public void PressPalm() => palmKey = true;

        bool CueReady(DemoCueDef c, float t, float now)
        {
            if (!string.IsNullOrEmpty(c.after))
            {
                if (SignalSeen(c.after, out float at)) return now >= at + c.delay;
                return t >= c.timeout * TimeoutScale;
            }
            return t >= c.at + c.delay;
        }

        bool LineReady(DemoLineDef l, float t, float now)
        {
            if (!string.IsNullOrEmpty(l.after))
            {
                if (SignalSeen(l.after, out float at)) return now >= at + l.delay;
                return t >= l.timeout * TimeoutScale;
            }
            return t >= Mathf.Max(0f, l.at) + l.delay;
        }

        void StartLine(int index)
        {
            lines[index].started = true;
            lineNow = index;
            DemoLineDef line = lines[index].def;
            if (talk != null && talk.SessionActive) talk.CancelSession();   // he must not hear himself
            if (albie != null && !albie.Perched && albie.State != AlbieState.Answering) albie.SetState(AlbieState.Answering);
            if (voice != null) voice.Say(line.text, line.audio);
            Debug.Log($"[Demo] {BeatId} line {index + 1}: {line.text}");
        }

        void OnLineEnded()
        {
            lastLineEnd = Time.time;
            bool more = false;
            for (int i = 0; i < lines.Count; i++) if (!lines[i].started) { more = true; break; }
            if (!more && beatRunning && !linesDone)
            {
                linesDone = true;
                linesDoneAt = Time.time;
            }
            if (albie != null && !albie.Perched && albie.State == AlbieState.Answering) albie.SetState(AlbieState.Wandering);
        }

        void CheckAdvance()
        {
            float now = Time.time;
            if (forceAdvanceSeconds > 0f)
            {
                bool done = linesDone && AllCuesFired();
                if (done && forceDoneAt < 0f) forceDoneAt = now;
                if ((forceDoneAt > 0f && now - forceDoneAt >= forceAdvanceSeconds) || now - beatStart > 45f) AdvanceFrom("forced");
                return;
            }
            // A (Hudson's line): once Albie has said his lines, whatever the beat's own condition or minimum.
            if (stepQueued && linesDone && (voice == null || now >= voice.EndedAt + 0.3f)) { AdvanceFrom("A"); return; }
            if (!linesDone || now - beatStart < script.beats[BeatIndex].min) return;
            for (int i = 0; i < alts.Count; i++)
            {
                Alt a = alts[i];
                switch (a.kind)
                {
                    case AltKind.Speech:
                        if (speechPending) { AdvanceFrom("speech: " + speechText); return; }
                        break;
                    case AltKind.Signal:
                        if (SignalSeen(a.name, out _)) { AdvanceFrom("signal " + a.name); return; }
                        break;
                    case AltKind.Auto:
                        if (now - Mathf.Max(timelineStart, linesDoneAt) >= a.seconds) { AdvanceFrom("timer"); return; }
                        break;
                }
            }
        }

        bool AllCuesFired()
        {
            for (int i = 0; i < cues.Count; i++) if (!cues[i].fired) return false;
            return true;
        }

        void AdvanceFrom(string reason)
        {
            if (!beatRunning) return;
            Debug.Log($"[Demo] {BeatId} done ({reason}).");
            for (int i = 0; i < cues.Count; i++)
                if (!cues[i].fired) { cues[i].fired = true; catchUp.Enqueue(cues[i].def); }
            beatRunning = false;
            speechPending = false;
            if (voice != null && voice.Speaking) voice.Stop(true);
            int next = BeatIndex + 1;
            if (next >= script.beats.Count)
            {
                Finished = true;
                Debug.Log("[Demo] script finished.");
                ScriptFinished?.Invoke();
                return;
            }
            pendingStart = next;
            nextCatchUp = Time.time;
        }

        // A beat starts once the cues left over from the one before (or the jump's run-through) have all fired.
        void DrainCatchUp()
        {
            float now = Time.time;
            if (catchUp.Count > 0)
            {
                if (now >= nextCatchUp)
                {
                    FireCue(catchUp.Dequeue());
                    nextCatchUp = now + CatchUpGap;
                }
                return;
            }
            if (pendingStart >= 0 && !beatRunning)
            {
                int index = pendingStart;
                pendingStart = -1;
                BeginBeat(index, "advance");
            }
        }

        void FireCue(DemoCueDef def)
        {
            if (string.IsNullOrEmpty(def.action)) return;
            DemoActions.Fire(def.action, ResolveArgs(def.args), BeatIndex);
        }

        // "slot:Left" in a cue's args becomes that slot's world position [x, y, z]. Cues fire rarely, so a clone is fine.
        static JObject ResolveArgs(JObject src)
        {
            if (src == null) return null;
            var args = (JObject)src.DeepClone();
            var names = new List<string>();
            foreach (JProperty p in args.Properties()) names.Add(p.Name);
            for (int i = 0; i < names.Count; i++)
            {
                if (args[names[i]] is JValue v && v.Type == JTokenType.String)
                {
                    string s = (string)v;
                    if (s.StartsWith("slot:", StringComparison.Ordinal) && Enum.TryParse(s.Substring(5), true, out DemoLayout.Slot slot))
                    {
                        Vector3 pos = DemoLayout.Get(slot).position;
                        args[names[i]] = new JArray(pos.x, pos.y, pos.z);
                    }
                }
            }
            return args;
        }

        // ---------------------------------------------------------------- signals and speech

        void OnSignal(string signal)
        {
            if (!string.IsNullOrEmpty(signal)) signalTimes[signal] = Time.time;
        }

        bool SignalSeen(string signal, out float at)
        {
            at = 0f;
            return beatRunning && signalTimes.TryGetValue(signal, out at) && at >= beatStart - 0.001f;
        }

        void OnFinalSpeech(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (config != null && config.muteLiveAgent) muteUntil = Time.time + MuteSeconds;   // always, even for chatter and his own echo
            if (StepButton) return;   // A is the only trigger: his voice and a press must not both move a beat on
            if (!beatRunning || !advanceHasSpeech || !linesDone) return;
            float now = Time.time;
            if (voice != null && (voice.Speaking || now < voice.EndedAt + EchoGuardSeconds)) return;
            if (now - beatStart < script.beats[BeatIndex].min) return;
            if (CountWords(text) < config.minSpeechWords) return;
            speechPending = true;
            speechText = text.Trim();
            if (albie != null && albie.State != AlbieState.Thinking) albie.SetState(AlbieState.Thinking);   // "Albie nods, thinking pose"
        }

        static int CountWords(string text)
        {
            int words = 0;
            bool inWord = false;
            for (int i = 0; i < text.Length; i++)
            {
                bool letter = !char.IsWhiteSpace(text[i]);
                if (letter && !inWord) words++;
                inWord = letter;
            }
            return words;
        }

        // ---------------------------------------------------------------- Albie

        void ResolveAlbie()
        {
            if (albie != null || Time.unscaledTime < nextAlbieSearch) return;
            nextAlbieSearch = Time.unscaledTime + 0.5f;
            var crew = FindAnyObjectByType<AlbieCrew>();
            albie = crew != null && crew.Lead != null ? crew.Lead : FindAnyObjectByType<AlbieController>();
            if (albie == null) return;

            bridge = albie.GetComponent<AlbieAgentBridge>();
            if (bridge == null) bridge = FindAnyObjectByType<AlbieAgentBridge>();
            talk = albie.GetComponent<PushToTalk>();
            if (talk == null) talk = FindAnyObjectByType<PushToTalk>();

            // The live agent's bubble goes quiet (the scripted lines have a bubble of their own); put back in OnDisable.
            liveCaption = albie.GetComponentInChildren<AlbieCaption>(true);
            if (liveCaption != null && config.muteLiveAgent) liveCaption.Muted = true;

            albie.PerchArrived += OnPerchArrived;
            voice = DemoVoice.Create(albie.transform);
            voice.VoicePitch = config.voicePitch;
            voice.SecondsPerChar = config.secondsPerChar;
            voice.Ended += OnLineEnded;
            Debug.Log("[Demo] Albie found: " + albie.name);
        }

        void ReleaseAlbie()
        {
            if (albie != null) albie.PerchArrived -= OnPerchArrived;
            if (liveCaption != null) liveCaption.Muted = false;
            if (voice != null)
            {
                voice.Ended -= OnLineEnded;
                voice.Silence();
                Destroy(voice.gameObject);
            }
            if (anchor != null) Destroy(anchor.gameObject);
            albie = null;
            voice = null;
            liveCaption = null;
            bridge = null;
            talk = null;
        }

        // ---------------------------------------------------------------- actions the Director owns

        void RegisterActions()
        {
            DemoActions.Register("albie.summon_hand", OnSummonAction);
            DemoActions.Register("albie.release", OnReleaseAction);
            DemoActions.Register("albie.say", OnSayAction);
            DemoActions.Register("albie.think", OnThinkAction);
            DemoActions.Register("albie.mood", OnMoodAction);
        }

        void UnregisterActions()
        {
            DemoActions.Unregister("albie.summon_hand", OnSummonAction);
            DemoActions.Unregister("albie.release", OnReleaseAction);
            DemoActions.Unregister("albie.say", OnSayAction);
            DemoActions.Unregister("albie.think", OnThinkAction);
            DemoActions.Unregister("albie.mood", OnMoodAction);
        }

        void OnReleaseAction(DemoCue cue)
        {
            if (albie != null && albie.Perched) albie.LeavePerch();
            if (anchor != null) { Destroy(anchor.gameObject); anchor = null; }
            summon = Summon.Idle;
        }

        void OnSayAction(DemoCue cue)
        {
            if (voice != null) voice.Say((string)cue.Args["text"] ?? "", (string)cue.Args["audio"]);
        }

        void OnThinkAction(DemoCue cue)
        {
            if (albie != null) albie.SetState(AlbieState.Thinking);
        }

        void OnMoodAction(DemoCue cue)
        {
            var expressions = albie != null ? albie.GetComponentInChildren<AlbieExpressions>(true) : null;
            if (expressions != null && Enum.TryParse((string)cue.Args["mood"], true, out Mood mood)) expressions.SetMood(mood);
        }
    }
}
