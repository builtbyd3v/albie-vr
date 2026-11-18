using System.Collections.Generic;
using Forge.Pet;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Forge.Demo
{
    // The headset remote: A on the right controller plays Hudson's next move, B goes back a beat (DemoConfig.stepButton).
    // One press per thing Hudson does in docs/demo/VIDEO_SCRIPT.md:
    //   A  start (B0, then B1 begins by itself)        A  palm: Albie runs over and lands on the hand, says hello
    //   A  line 1 (B1 to B2: the quad builds, flies)   A  line 2 (B2 to B3: research, then the verdict card, B4)
    //   A  poke "Add to parts list" (B4)               A  poke "Confirm" (B5b, after the training of B5)
    //   A  line 5 (B5b to B6: the flight test)         A  palm out again (B6 to B7: the drone comes home)
    //   A  end of the take; A once more resets for the next take.
    // A press never cuts Albie off: when he is still talking, the move is held and happens as soon as his last line ends.
    // While this is on the live voice stays off (A is push-to-talk outside the demo, and Hudson's lines must not be answered).
    public sealed partial class DemoDirector
    {
        const string AddedSignal = "research.added", PushedSignal = "pipeline.pushed", PalmSignal = "hand.palm_up";

        bool stepQueued;      // Hudson "said his line": the beat moves on once Albie's lines are done
        string pokeQueued;    // a poke A asked for that wasn't possible yet (card still animating in), retried every frame
        float pokeQueuedAt;
        OVRCameraRig rig;
        float nextRigSearch;
        bool liveVoiceOff;
        readonly List<Behaviour> silenced = new List<Behaviour>();

        bool StepButton => config != null && config.stepButton;
        /// <summary>The script is cued by hand (the laptop's Cue, A, or the finger tap), so Hudson's speech does not move it.</summary>
        bool ManualStep => config != null && (config.cueOnly || config.stepButton || config.handStep);

        void PollController()
        {
            Keyboard kb = Keyboard.current;
            if (kb != null && kb.enterKey.wasPressedThisFrame) Step();   // the cue, on the desktop rig
            if (!StepButton) return;
            // RawButton.A / B are the right controller's buttons. OVRInput needs OVRManager, which Main's rig has.
            if (OVRInput.GetDown(OVRInput.RawButton.A)) Step();
            if (OVRInput.GetDown(OVRInput.RawButton.B)) Back();
        }

        /// <summary>Hudson's next move (the A button). See the table at the top of this file.</summary>
        public void Step()
        {
            if (script == null) return;
            DismissArrival();
            if (Finished) { ResetAll(); Debug.Log("[Demo] A: take over, reset for the next one."); return; }
            if (pendingStart >= 0 || catchUp.Count > 0) return;   // between beats
            if (!beatRunning)
            {
                if (BeatIndex < 0) { Debug.Log("[Demo] A: start."); BeginBeat(0, "A"); }
                return;
            }

            DemoBeatDef beat = script.beats[BeatIndex];
            if (beat.id == "B0") { AdvanceFrom("A"); return; }   // the quiet opening: straight on to the palm

            switch (summon)
            {
                case Summon.WaitPalm:
                    Debug.Log("[Demo] A: palm (Albie comes to the hand).");
                    ForceSummon();
                    return;
                case Summon.Approach:
                case Summon.Perching:
                    return;   // he is on his way; his line follows when he lands
            }

            // The summon fell through (he was busy, or got knocked off): A calls him to the hand again rather than moving on.
            if (summon == Summon.Idle && LineWaits("albie.on_hand"))
            {
                Debug.Log("[Demo] A: Albie isn't on the hand yet, calling him again.");
                OnSummonAction(null);
                if (summon == Summon.WaitPalm) ForceSummon();
                return;
            }

            string gate = PendingUserGate();
            if (gate != null)
            {
                QueuePoke(gate);
                return;
            }
            if (WaitsFor(PalmSignal))
            {
                Debug.Log("[Demo] A: palm out (hand.palm_up).");
                DemoActions.Raise(PalmSignal);
                return;
            }
            // B3, B4 after the poke, and B5 run by themselves: a stray press there must not cut the research or the training short.
            if (!advanceHasSpeech && alts.Count > 0)
            {
                Debug.Log($"[Demo] A: {beat.id} runs by itself; press again when it's Hudson's turn (laptop Next skips it).");
                return;
            }
            if (!stepQueued) Debug.Log($"[Demo] A: {beat.id} moves on" + (linesDone ? "." : " once Albie has finished his line."));
            stepQueued = true;
        }

        // A cue or line of the running beat that waits for one of Hudson's pokes, and hasn't had it.
        string PendingUserGate()
        {
            for (int i = 0; i < cues.Count; i++)
                if (!cues[i].fired && IsPoke(cues[i].def.after) && !SignalSeen(cues[i].def.after, out _)) return cues[i].def.after;
            for (int i = 0; i < lines.Count; i++)
                if (!lines[i].started && IsPoke(lines[i].def.after) && !SignalSeen(lines[i].def.after, out _)) return lines[i].def.after;
            return null;
        }

        bool LineWaits(string signal)
        {
            for (int i = 0; i < lines.Count; i++)
                if (!lines[i].started && lines[i].def.after == signal && !SignalSeen(signal, out _)) return true;
            return false;
        }

        static bool IsPoke(string signal) => signal == AddedSignal || signal == PushedSignal;

        bool WaitsFor(string signal)
        {
            for (int i = 0; i < alts.Count; i++)
                if (alts[i].kind == AltKind.Signal && alts[i].name == signal) return true;
            return false;
        }

        void QueuePoke(string gate)
        {
            if (pokeQueued == gate) return;
            pokeQueued = gate;
            pokeQueuedAt = Time.time;
            Debug.Log("[Demo] A: " + (gate == AddedSignal ? "poke Add to parts list." : "poke Confirm."));
            TryPoke();
        }

        // Pokes the button the way a finger would, as soon as its card is up. Gives up after 20 s (the beat's own timeout runs on).
        void TryPoke()
        {
            if (pokeQueued == null) return;
            if (!beatRunning || Time.time - pokeQueuedAt > 20f || SignalSeen(pokeQueued, out _)) { pokeQueued = null; return; }
            bool done = false;
            if (pokeQueued == AddedSignal)
            {
                ResearchStation research = ResearchStation.Instance;
                done = research != null && research.PokeAdd();
                if (research == null) { DemoActions.Raise(AddedSignal); done = true; }   // no station in this scene: the beat still goes on
            }
            else if (pokeQueued == PushedSignal)
            {
                PipelineDemo pipeline = PipelineDemo.Instance;
                done = pipeline != null && pipeline.PokeConfirm();
                if (pipeline == null) { DemoActions.Raise(PushedSignal); done = true; }
            }
            if (done) pokeQueued = null;
        }

        void ClearStep()
        {
            stepQueued = false;
            pokeQueued = null;
        }

        // ---------------------------------------------------------------- Hudson's left hand with a controller in it

        // With controllers in hand Quest turns hand tracking off, so the palm is the left controller: Albie sits just above it.
        // Null when the left controller isn't tracked either (put down): then he lands in front of Hudson.
        bool LeftControllerPoint(out Vector3 point)
        {
            point = default;
            if (rig == null && Time.unscaledTime >= nextRigSearch)
            {
                nextRigSearch = Time.unscaledTime + 1f;
                rig = FindAnyObjectByType<OVRCameraRig>();
            }
            if (rig == null || rig.leftControllerAnchor == null) return false;
            if (!OVRInput.GetControllerPositionTracked(OVRInput.Controller.LTouch)) return false;
            Transform t = rig.leftControllerAnchor;
            point = t.position + Vector3.up * 0.06f;
            return true;
        }

        // ---------------------------------------------------------------- the rest of the app stays out of the take

        // The live voice: A opens it outside the demo, and the wake word, follow-ups and the whistle would answer Hudson's lines.
        void SilenceLiveVoice()
        {
            if (liveVoiceOff || !ManualStep || albie == null) return;
            liveVoiceOff = true;
            if (talk != null && talk.SessionActive) talk.CancelSession();
            Silence(FindObjectsByType<PushToTalk>());
            Silence(FindObjectsByType<WakeListener>());
            Silence(FindObjectsByType<AlbieFollowUp>());
            Silence(FindObjectsByType<WhistleButton>());
            Debug.Log($"[Demo] the script is cued by hand: live voice off ({silenced.Count} components).");
        }

        void Silence<T>(T[] found) where T : Behaviour
        {
            for (int i = 0; i < found.Length; i++)
            {
                if (found[i] == null || !found[i].enabled) continue;
                found[i].enabled = false;
                silenced.Add(found[i]);
            }
        }

        void RestoreLiveVoice()
        {
            for (int i = 0; i < silenced.Count; i++) if (silenced[i] != null) silenced[i].enabled = true;
            silenced.Clear();
            liveVoiceOff = false;
        }

        // The first-arrival card (digest + START) would sit in the shot and hold Albie at its spot.
        void DismissArrival()
        {
            var arrival = FindAnyObjectByType<ArrivalDirector>();
            if (arrival != null) arrival.DismissGreeting();
            if (albie != null) albie.ReleaseStation();
        }
    }
}
