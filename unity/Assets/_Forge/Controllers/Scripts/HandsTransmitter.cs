using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// Hands as a manual input source (contract section 3.2), so a Thing can be driven with no controller paired, which
    /// is what the competition's Stage 1 asks for. Pinch to drive, tilt the hand to steer:
    /// - The right hand's thumb-index pinch is the throttle (0 open, 1 closed; it ramps over the last stretch so a
    ///   light pinch crawls). The left hand's pinch is reverse, at half throttle. When both pinch, the right hand wins.
    /// - Roll is the pinching hand's palm tilt about the forward axis: palm flat is straight, pinky side down is right,
    ///   thumb side down is left, 45 degrees is full lock. <c>truck_manual</c> binds <c>hand.roll</c> to steering with
    ///   its own dead zone and expo, so a hand at rest doesn't wander.
    /// Reads the Interaction SDK's plain Hand components through <see cref="IsdkHandSource"/> (the gestures module's
    /// reader): nothing with controllers held, nothing when a hand is out of tracking or the tracker doesn't trust the
    /// frame. The brain truck reads it during a hand lap and writes the inputs only when they change, as it does for
    /// the thumbstick, so the keys, the stick and the hands never overwrite each other every frame. No allocations
    /// after the first call (the source rescans the rig, which allocates, only while a hand is missing, every 2 s).
    /// </summary>
    public static class HandsTransmitter
    {
        /// <summary>Checks only: stands in for the hands (a check has no headset). Throttle then roll, both -1..1.</summary>
        public static System.Func<(float throttle, float roll)> Override;

        /// <summary>Checks only: where hand joints come from instead of the headset rig.</summary>
        public static IHandSource Source;

        /// <summary>Pinch distance (thumb tip to index tip, in hand lengths) at and below which the throttle is full.</summary>
        public const float PinchClosed = 0.3f;
        /// <summary>Pinch distance at and above which the throttle is zero; between the two it ramps.</summary>
        public const float PinchOpen = 0.6f;
        /// <summary>Palm tilt that gives full lock, degrees.</summary>
        public const float FullLockDegrees = 45f;
        /// <summary>The left hand's pinch drives backwards at this fraction of full throttle.</summary>
        public const float ReverseThrottle = 0.5f;

        static readonly HandSample[] samples = { new HandSample { isLeft = true }, new HandSample { isLeft = false } };
        static IHandSource rig;

        /// <summary>True when a tracked hand is pinching, so a status line can say the hands are driving.</summary>
        public static bool Driving { get; private set; }

        /// <summary>Throttle -1..1 (+ forward) and roll -1..1 (+ right). Both zero with no tracked, pinching hand.</summary>
        public static void Read(out float throttle, out float roll)
        {
            if (Override != null)
            {
                (throttle, roll) = Override();
                Driving = throttle != 0f;
                return;
            }
            throttle = 0f;
            roll = 0f;
            Driving = false;
            var source = Source ?? rig;
            if (source == null)
            {
                // The rig's hands update OVRInput through OVRManager; with none (the Editor sandboxes) there are no hands to read.
                if (OVRManager.instance == null) return;
                source = rig = new IsdkHandSource();
            }
            var cam = Camera.main;
            var right = cam != null ? Vector3.ProjectOnPlane(cam.transform.right, Vector3.up) : Vector3.right;
            if (right.sqrMagnitude < 1e-4f) right = Vector3.right;
            right.Normalize();

            // The right hand first: when both pinch it wins.
            for (int hand = 1; hand >= 0; hand--)
            {
                var s = samples[hand];
                source.Read(hand, s);
                if (!s.tracked || !s.highConfidence) continue;
                float pinch = Pinch(s);
                if (pinch >= PinchOpen) continue;
                float grip = Mathf.Clamp01((PinchOpen - pinch) / (PinchOpen - PinchClosed));
                throttle = hand == 1 ? grip : -grip * ReverseThrottle;
                roll = Roll(s, right);
                Driving = true;
                return;
            }
        }

        /// <summary>Thumb tip to index tip, in hand lengths (wrist to middle knuckle), as the gestures' pinch feature.</summary>
        public static float Pinch(HandSample s)
        {
            var j = s.joints;
            float size = Mathf.Max(0.01f, Vector3.Distance(j[HandJoint.Wrist], j[HandJoint.Middle1]));
            return Vector3.Distance(j[HandJoint.ThumbTip], j[HandJoint.IndexTip]) / size;
        }

        /// <summary>Palm tilt about the forward axis as -1..1, + for the pinky side down on a palm-down hand (the hand on a wheel
        /// turning right). Dropping the pinky side turns the palm to face the thumb side, so the palm normal leans away from
        /// the head's right; <see cref="FullLockDegrees"/> of tilt gives 1.</summary>
        public static float Roll(HandSample s, Vector3 headRight)
        {
            var normal = HandFeatures.PalmNormal(s);
            float lean = -Vector3.Dot(normal, headRight);
            return Mathf.Clamp(lean / Mathf.Sin(FullLockDegrees * Mathf.Deg2Rad), -1f, 1f);
        }
    }
}
