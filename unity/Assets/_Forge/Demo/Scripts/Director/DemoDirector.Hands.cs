using UnityEngine;

namespace Forge.Demo
{
    // The take with bare hands, no controllers on screen (DemoConfig.handStep). Hudson's moves:
    //   palm up      Albie runs over and lands on it (B1); again later and the drone comes home (B6 to B7)
    //   finger poke  "Add to parts list" (B4) and "Confirm" (B5b), on the cards themselves
    //   finger tap   thumb to middle fingertip with the other fingers open, either hand: everything A does on the controller (start, each of his
    //                lines, the end of the take, and the palm or a poke when the real one didn't take)
    // The tap is thumb to MIDDLE finger because thumb to index is the Interaction SDK's click and Quest's menu pinch. It
    // can be made low, out of the shot. While this is on the app's own gestures are paused: opening and closing a hand
    // would hide every window mid-take, and its "call Albie" palm would race the Director's.
    public sealed partial class DemoDirector
    {
        const float TapClose = 0.24f, TapOpen = 0.42f;   // thumb tip to middle tip, in hand lengths (touching is about 0.1)
        const float TapIndexClear = 0.3f;                // thumb tip to index tip stays wider than this: not an index pinch or a fist
        const float TapMaxCurl = 0.55f;                  // index and ring fingers stay open: a pointing (poking) hand or a fist rests the thumb on the middle finger too
        const float TapHold = 0.08f, TapGap = 0.7f;

        readonly float[] tapSince = { -1f, -1f };
        readonly bool[] tapArmed = { true, true };
        float lastTap = -10f;
        GestureEngine pausedEngine;

        bool HandStep => config != null && config.handStep;

        /// <summary>Thumb tip on middle fingertip, index finger clear of both. Distances in hand lengths, so any hand size.</summary>
        public static float TapDistance(HandSample s, out float indexDistance)
        {
            Vector3[] j = s.joints;
            float size = Mathf.Max(0.01f, Vector3.Distance(j[HandJoint.Wrist], j[HandJoint.Middle1]));
            indexDistance = Vector3.Distance(j[HandJoint.ThumbTip], j[HandJoint.IndexTip]) / size;
            return Vector3.Distance(j[HandJoint.ThumbTip], j[HandJoint.MiddleTip]) / size;
        }

        void PollHands()
        {
            if (!HandStep) return;
            GestureEngine engine = GestureEngine.Current;
            if (engine == null) return;
            if (pausedEngine != engine)
            {
                pausedEngine = engine;
                engine.Paused = true;
                engine.Actions.ShowDisplay(false);
                Debug.Log("[Demo] bare hands: thumb to middle finger is the step; the app's own gestures are paused.");
            }

            float now = Time.time;
            bool busy = PanelManipulator.Grabbing > 0;
            for (int h = 0; h < 2; h++)
            {
                GestureHand hand = engine.Hands[h];
                if (!hand.Usable) { tapSince[h] = -1f; continue; }   // an untracked hand keeps its arming: no tap out of a glitch
                float middle = TapDistance(hand.sample, out float index);
                if (middle > TapOpen) { tapArmed[h] = true; tapSince[h] = -1f; continue; }
                bool open = hand.values[(int)HandFeature.IndexCurl] < TapMaxCurl && hand.values[(int)HandFeature.RingCurl] < TapMaxCurl;
                if (!tapArmed[h] || busy || !open || middle > TapClose || index < TapIndexClear) { tapSince[h] = -1f; continue; }
                if (tapSince[h] < 0f) { tapSince[h] = now; continue; }
                if (now - tapSince[h] < TapHold) continue;
                tapArmed[h] = false;   // one tap per touch: the fingers have to part again first
                tapSince[h] = -1f;
                if (now - lastTap < TapGap) continue;
                lastTap = now;
                Debug.Log(h == 0 ? "[Demo] finger tap (left hand)." : "[Demo] finger tap (right hand).");
                Step();
            }
        }

        void ReleaseHands()
        {
            if (pausedEngine != null) pausedEngine.Paused = false;
            pausedEngine = null;
        }
    }
}
