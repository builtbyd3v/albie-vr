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
    ///
    /// The handheld <see cref="RcTransmitter"/> is the default way to drive now: the air pinch is an option
    /// (<see cref="PinchDriveEnabled"/>, the Drive window's switch, off by default), and both air inputs read zero while
    /// the transmitter is in use (<see cref="TransmitterInUse"/>), so a hand closed round it never also pinch-drives.
    /// </summary>
    public static class HandsTransmitter
    {
        /// <summary>The air pinch drives the truck only while this is on (off by default: the handheld transmitter is the
        /// default). Checks that set <see cref="Source"/> or <see cref="Override"/> are not affected.</summary>
        public static bool PinchDriveEnabled;

        /// <summary>Set by <see cref="RcTransmitter"/> while a hand or the mouse holds it or works one of its sticks: the air
        /// pinch and the pinch sticks read zero meanwhile.</summary>
        public static bool TransmitterInUse;

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
            if (Source == null && (!PinchDriveEnabled || TransmitterInUse)) return;
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

        // ---- Four axes, for a Thing that flies ----------------------------------------------------------------------

        /// <summary>The pinch sticks are read only while this is on (the Drive window's switch). Off: <see cref="ReadFlight"/> is all zeros.</summary>
        public static bool FlightEnabled;
        /// <summary>How many of the Drive window's pads are held. While one is, the pinch sticks read zero: the pinch that
        /// holds a pad through a hand ray must not also fly the drone.</summary>
        public static int PadsHeld;
        /// <summary>Checks only: stands in for the pinch sticks. Forward, right, climb, yaw, each -1..1.</summary>
        public static System.Func<(float forward, float right, float climb, float yaw)> FlightOverride;
        /// <summary>How far a pinched hand moves from where it pinched for a full stick, metres.</summary>
        public const float StickTravel = 0.08f;
        /// <summary>A pinched hand within this of where it pinched reads zero, metres.</summary>
        public const float StickDeadzone = 0.015f;

        static readonly bool[] gripped = new bool[2];
        static readonly Vector3[] anchor = new Vector3[2];

        /// <summary>True while a pinch stick is held (after <see cref="ReadFlight"/>).</summary>
        public static bool Flying { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetFlight()
        {
            FlightEnabled = false;
            PinchDriveEnabled = false;
            TransmitterInUse = false;
            PadsHeld = 0;
            FlightOverride = null;
            gripped[0] = gripped[1] = false;
            Flying = false;
        }

        /// <summary>
        /// Two pinch sticks, for a drone. Pinch (thumb to index) to take hold of a stick where the hand is, move the hand
        /// to push it, let go and it is back at the middle:
        /// - the right hand is the right stick: away from you is forward, sideways is sideways;
        /// - the left hand is the left stick: up and down climbs and descends, sideways turns.
        /// Directions are the pilot's (the head's heading). <see cref="StickTravel"/> of hand movement is a full stick,
        /// with a <see cref="StickDeadzone"/> around where the pinch began. A pinch closes at <see cref="PinchClosed"/>
        /// and lets go at <see cref="PinchOpen"/>, so a stick isn't dropped by a wobble. All zeros when
        /// <see cref="FlightEnabled"/> is off, while a Drive pad is held, and for a hand that is not tracked or trusted.
        /// No allocations after the first call, as <see cref="Read"/>.
        /// </summary>
        public static void ReadFlight(out float forward, out float right, out float climb, out float yaw)
        {
            if (FlightOverride != null)
            {
                (forward, right, climb, yaw) = FlightOverride();
                Flying = forward != 0f || right != 0f || climb != 0f || yaw != 0f;
                return;
            }
            forward = right = climb = yaw = 0f;
            Flying = false;
            if (!FlightEnabled || PadsHeld > 0 || TransmitterInUse)
            {
                gripped[0] = gripped[1] = false;
                return;
            }
            var source = Source ?? rig;
            if (source == null)
            {
                if (OVRManager.instance == null) return;
                source = rig = new IsdkHandSource();
            }
            var cam = Camera.main;
            var ahead = cam != null ? Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up) : Vector3.forward;
            if (ahead.sqrMagnitude < 1e-4f) ahead = Vector3.forward;
            ahead.Normalize();
            var side = Vector3.Cross(Vector3.up, ahead);

            for (int hand = 0; hand < 2; hand++)
            {
                var s = samples[hand];
                source.Read(hand, s);
                if (!s.tracked || !s.highConfidence) { gripped[hand] = false; continue; }
                float pinch = Pinch(s);
                var at = (s.joints[HandJoint.ThumbTip] + s.joints[HandJoint.IndexTip]) * 0.5f;
                if (!gripped[hand])
                {
                    if (pinch > PinchClosed) continue;
                    gripped[hand] = true;
                    anchor[hand] = at;
                }
                else if (pinch >= PinchOpen) { gripped[hand] = false; continue; }
                Flying = true;
                var moved = at - anchor[hand];
                if (hand == 1)
                {
                    forward = StickAxis(Vector3.Dot(moved, ahead));
                    right = StickAxis(Vector3.Dot(moved, side));
                }
                else
                {
                    climb = StickAxis(moved.y);
                    yaw = StickAxis(Vector3.Dot(moved, side));
                }
            }
        }

        static float StickAxis(float metres)
        {
            float size = Mathf.Abs(metres);
            if (size <= StickDeadzone) return 0f;
            float v = Mathf.Clamp01((size - StickDeadzone) / (StickTravel - StickDeadzone));
            return metres < 0f ? -v : v;
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
