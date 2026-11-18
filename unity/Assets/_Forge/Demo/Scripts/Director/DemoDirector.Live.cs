using Forge.Pet;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Demo
{
    // The parts of the Director that touch the live app: Hudson's left palm (B1), the microphone, and the live agent's reply.
    public sealed partial class DemoDirector
    {
        // ---------------------------------------------------------------- B1 palm summon

        enum Summon { Idle, WaitPalm, Approach, Perching, Landed }

        const float PalmHoldSeconds = 0.35f;       // palm up this long counts as "held out"
        const float EngineGraceSeconds = 1.7f;     // the gesture engine's own "Albie to my hand" gets this long to act first
        const float PalmLift = 0.012f;             // m above the palm (GesturePerch's)
        const float FollowRate = 16f;
        const float LostSeconds = 1.2f, TurnedSeconds = 0.5f, ForcedHoldSeconds = 90f;
        const float NearEnough = 0.9f;             // m: Albie jumps to the palm from this close, no further

        Summon summon;
        Transform anchor;
        bool forced, ownsPerch, palmKey, approachStarted, onController;
        int forcedBeat = -1, palmHand;
        float summonStart, palmHeldSince = -1f, graceStart = -1f, approachStart, perchStart, landedAt, handLostAt = -1f, turnedAt = -1f;
        float palmUpSince = -1f, lastPalmSignal = -10f;
        Vector3 forcedPos;

        void OnSummonAction(DemoCue cue)
        {
            if (albie == null)
            {
                Debug.LogWarning("[Demo] albie.summon_hand: no Albie in the scene; raising albie.on_hand anyway.");
                DemoActions.Raise("albie.on_hand");
                return;
            }
            ResetSummon();
            summon = Summon.WaitPalm;
            summonStart = Time.time;
            Debug.Log("[Demo] waiting for the left palm (` on the desktop, remote Next to skip).");
        }

        void ForceSummon()
        {
            if (summon != Summon.WaitPalm) return;
            Transform head = PetUser.Head;
            // A hand is already held out (bare hands): he lands on it and rides it, as with the real gesture.
            if (HoldableHand(out int open)) { palmHand = open; Debug.Log("[Demo] Albie comes to the open hand."); BeginApproach(); return; }
            forced = true;
            forcedPos = head != null ? head.TransformPoint(new Vector3(-0.1f, -0.2f, 0.45f)) : albie.transform.position;   // a palm held out, inside the view
            onController = LeftControllerPoint(out Vector3 pad);
            if (onController) forcedPos = pad;
            forcedBeat = BeatIndex;
            Debug.Log(onController ? "[Demo] no tracked palm: Albie lands on the left controller." : "[Demo] no palm: Albie lands in front of Hudson anyway.");
            BeginApproach();
        }

        void ResetSummon()
        {
            if (albie != null && albie.Perched) albie.LeavePerch(albie.ComeToUser);
            else if (albie != null && albie.State != AlbieState.Wandering) albie.SetState(AlbieState.Wandering);
            if (anchor != null) { Destroy(anchor.gameObject); anchor = null; }
            summon = Summon.Idle;
            forced = ownsPerch = approachStarted = onController = false;
            forcedBeat = -1;
            palmHeldSince = graceStart = handLostAt = turnedAt = -1f;
        }

        void OnPerchArrived()
        {
            if (summon != Summon.Perching && summon != Summon.WaitPalm && summon != Summon.Approach) return;
            Land();
        }

        void Land()
        {
            summon = Summon.Landed;
            landedAt = Time.time;
            handLostAt = turnedAt = -1f;
            Debug.Log("[Demo] Albie is on the hand.");
            DemoActions.Raise("albie.on_hand");
        }

        // The hand Albie is called to, as the gesture engine reads it (the left, or the right when that is the one held out);
        // null off-headset or before the engine starts.
        GestureHand PalmHand()
        {
            GestureEngine engine = GestureEngine.Current;
            return engine != null ? engine.Hands[palmHand] : null;
        }

        static GestureHand Hand(int index)
        {
            GestureEngine engine = GestureEngine.Current;
            return engine != null ? engine.Hands[index] : null;
        }

        // A tracked hand turned up enough for him to sit in: the left first.
        bool HoldableHand(out int index)
        {
            for (index = 0; index < 2; index++)
            {
                GestureHand h = Hand(index);
                if (h != null && h.Usable && GestureLibrary.CanHoldPerch(h.values)) return true;
            }
            index = 0;
            return false;
        }

        static bool PalmHeldUp(GestureHand h) =>
            h != null && h.Usable && h.values[(int)HandFeature.PalmUp] > 0.5f && h.values[(int)HandFeature.InView] > 0.2f;

        Vector3 PalmTarget(GestureHand h)
        {
            if (h != null && h.Usable && !forced) return h.Palm + HandFeatures.PalmNormal(h.sample) * PalmLift;
            if (onController && LeftControllerPoint(out Vector3 pad)) forcedPos = pad;   // he rides the controller as it moves
            return forcedPos;
        }

        void UpdatePalm()
        {
            float now = Time.time;
            // Before he is on his way either palm will do; after that he stays with the hand he was called to.
            if ((summon == Summon.Idle || summon == Summon.WaitPalm) && !PalmHeldUp(Hand(palmHand)) && PalmHeldUp(Hand(1 - palmHand)))
                palmHand = 1 - palmHand;
            GestureHand hand = PalmHand();
            bool palmUp = PalmHeldUp(hand);
            bool key = palmKey;
            palmKey = false;

            // "hand.palm_up": a signal beats can end on (B6 to B7). The palm has to stay up for a moment; one per 1.5 s.
            if (summon != Summon.Idle && albie == null) summon = Summon.Idle;
            if (beatRunning && (summon == Summon.Idle || summon == Summon.Landed))
            {
                if (palmUp) { if (palmUpSince < 0f) palmUpSince = now; } else palmUpSince = -1f;
                if ((key || (palmUpSince > 0f && now - palmUpSince >= 0.6f)) && now - lastPalmSignal > 1.5f && beatRunning)
                {
                    lastPalmSignal = now;
                    DemoActions.Raise("hand.palm_up");
                }
            }

            switch (summon)
            {
                case Summon.WaitPalm:
                    if (key || (config.palmTimeoutSeconds > 0f && now - summonStart >= config.palmTimeoutSeconds)) { ForceSummon(); break; }
                    if (!palmUp) { palmHeldSince = -1f; graceStart = -1f; break; }
                    if (palmHeldSince < 0f) palmHeldSince = now;
                    if (now - palmHeldSince < PalmHoldSeconds) break;
                    if (graceStart < 0f) graceStart = now;
                    if (albie.Perched) { summon = Summon.Perching; perchStart = now; break; }   // the engine's own gesture got there first
                    if (now - graceStart >= (GestureEngine.Current != null && !GestureEngine.Current.Paused ? EngineGraceSeconds : 0f)) BeginApproach();
                    break;

                case Summon.Approach:
                    UpdateApproach(hand);
                    break;

                case Summon.Perching:
                    FollowAnchor(hand);
                    if (!albie.Perched && now - perchStart > 1f) { summon = Summon.Idle; break; }
                    if (now - perchStart > 3.5f && albie.Perched) Land();   // the arrival event was missed
                    break;

                case Summon.Landed:
                    FollowAnchor(hand);
                    if (!albie.Perched) { summon = Summon.Idle; break; }
                    if (ownsPerch) CheckLeave(hand, now);
                    break;
            }
        }

        void BeginApproach()
        {
            summon = Summon.Approach;
            approachStarted = false;
            approachStart = Time.time;
        }

        // Run over first when he is far, then jump to the palm: a long jump from across the room looks like flying.
        void UpdateApproach(GestureHand hand)
        {
            float now = Time.time;
            Vector3 target = PalmTarget(hand);
            float distance = Vector3.Distance(albie.transform.position, target);
            if (albie.Stationed) albie.ReleaseStation();   // the arrival greeting's spot: PerchOn refuses while he holds it
            if (!approachStarted && distance > NearEnough && !albie.Perched)
            {
                approachStarted = true;
                approachStart = now;
                albie.ReleaseStation();
                albie.ComeToUser();
            }
            if (distance > NearEnough && approachStarted && now - approachStart < 5f) return;

            if (anchor == null)
            {
                anchor = new GameObject("DemoPalmAnchor").transform;
                anchor.position = target;
            }
            if (albie.Perched) { ownsPerch = false; summon = Summon.Perching; perchStart = now; return; }
            anchor.position = target;
            if (albie.PerchOn(anchor))
            {
                ownsPerch = true;
                summon = Summon.Perching;
                perchStart = now;
            }
            else if (now - approachStart > 8f)
            {
                Debug.LogWarning("[Demo] Albie would not jump (busy); raising albie.on_hand anyway.");
                Land();
            }
        }

        void FollowAnchor(GestureHand hand)
        {
            if (anchor == null || !ownsPerch) return;
            if (!forced && (hand == null || !hand.Usable)) return;   // palm out of view: he stays where it was until CheckLeave says go
            anchor.position = Vector3.Lerp(anchor.position, PalmTarget(hand), 1f - Mathf.Exp(-FollowRate * Time.deltaTime));
        }

        // He hops down when the palm goes away, as GesturePerch does; a forced landing just times out.
        void CheckLeave(GestureHand hand, float now)
        {
            bool leave = false;
            // No real palm to read: he stays through his hello and Hudson's first line, and hops down once the drone opens.
            if (forced) leave = (BeatIndex > forcedBeat && BeatSeconds > 2.5f) || now - landedAt > ForcedHoldSeconds;
            else if (hand == null || !hand.Usable)
            {
                if (handLostAt < 0f) handLostAt = now; else leave = now - handLostAt > LostSeconds;
            }
            else
            {
                handLostAt = -1f;
                if (GestureLibrary.CanHoldPerch(hand.values)) turnedAt = -1f;
                else if (turnedAt < 0f) turnedAt = now; else leave = now - turnedAt > TurnedSeconds;
            }
            if (!leave) return;
            albie.LeavePerch();
            if (anchor != null) { Destroy(anchor.gameObject); anchor = null; }
            ownsPerch = false;
            summon = Summon.Idle;
        }

        // ---------------------------------------------------------------- the microphone

        float nextMicTry;

        // While a beat waits for Hudson's speech and Albie is done talking, a hands-free session is always open.
        void UpdateMic()
        {
            if (!config.keepMicOpen || ManualStep || talk == null || !advanceHasSpeech || !linesDone) return;
            float now = Time.time;
            if (voice != null && (voice.Speaking || now < voice.EndedAt + 0.5f)) return;
            if (talk.SessionActive || now < nextMicTry) return;
            nextMicTry = now + 0.6f;
            if (!talk.CanListenHandsFree) return;
            talk.BeginHandsFree(30f);
        }

        // ---------------------------------------------------------------- the live agent stays quiet

        const float MuteSeconds = 7f;
        float muteUntil, nextMute;

        // Hudson's speech still reaches the live agent (it has to: that is where the transcript comes from), and it will start
        // to answer. For a few seconds after every final transcript the Director barges in on it the way a user would
        // (tts.stop cancels the reply and flushes its audio; agent.cancel stops a text answer). The bubble is muted for good.
        void UpdateMute()
        {
            if (muteUntil <= 0f) return;
            float now = Time.time;
            if (now >= muteUntil)
            {
                muteUntil = 0f;
                if (albie != null && !albie.Perched && (voice == null || !voice.Speaking) &&
                    (albie.State == AlbieState.Answering || albie.State == AlbieState.Working))
                    albie.SetState(AlbieState.Wandering);
                return;
            }
            if (now < nextMute) return;
            nextMute = now + 0.25f;
            string petId = bridge != null ? bridge.PetId : "bit";
            ForgeNet.Send("tts.stop", new JObject { ["pet_id"] = petId });
            ForgeNet.Send("agent.cancel", new JObject { ["pet_id"] = petId });
        }
    }
}
