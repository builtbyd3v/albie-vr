using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// Driving aids for the brain truck's training laps (PROJECT_CONTEXT 16.8, Nov 18 headset test: "the slalom with the
    /// remote is too tough"). They shape what the RC transmitter writes to <c>truck_manual</c> for the brain truck only (the
    /// drone and other trucks get the sticks as they are), and the Drive window sets them. Session settings, no allocations.
    /// - <see cref="TrainingSpeed"/>: a full left stick holds this speed (m/s at full size, any design or course scale).
    ///   Slower is easier, and the Autopilot drives at the speed of the laps. The transmitter's FULL switch lifts the cap.
    /// - <see cref="SteerExpo"/> and <see cref="SteerRate"/>: a softer middle on the steering stick (small thumb moves are
    ///   small turns) and how much of the steering lock a full stick gives.
    /// - <see cref="SteadyThrottle"/> (optional, off by default): push the left stick forward once and the truck holds the
    ///   training speed by itself, so the person only steers; pull the stick back to brake and stop. A lap's end, Stop, a
    ///   Reset or the Autopilot taking over all end it. Steering-only laps are much easier and train cleaner brains.
    /// </summary>
    public static class DriveAids
    {
        public const float DefaultTrainingSpeed = 0.6f, MinTrainingSpeed = 0.3f, MaxTrainingSpeed = 1.2f;
        public const float DefaultSteerExpo = 0.25f, DefaultSteerRate = 1f, MinSteerRate = 0.5f;
        /// <summary>The left stick past this forward engages the steady throttle, and back past <see cref="CruiseOff"/> ends it.</summary>
        public const float CruiseOn = 0.4f, CruiseOff = -0.3f;

        public static float TrainingSpeed = DefaultTrainingSpeed;
        public static float SteerExpo = DefaultSteerExpo;
        public static float SteerRate = DefaultSteerRate;
        public static bool SteadyThrottle;

        /// <summary>True while the steady throttle holds the truck's speed (the left stick pushed once).</summary>
        public static bool Cruising { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            TrainingSpeed = DefaultTrainingSpeed;
            SteerExpo = DefaultSteerExpo;
            SteerRate = DefaultSteerRate;
            SteadyThrottle = false;
            Cruising = false;
        }

        /// <summary>Ends the steady throttle (a lap's end, Stop, Reset, the Autopilot).</summary>
        public static void EndCruise() => Cruising = false;

        /// <summary>The steering stick (+ right, -1..1) with the expo and the rate: the end of the stick is still <see cref="SteerRate"/>.</summary>
        public static float Steer(float x)
        {
            float m = Mathf.Abs(x);
            if (m < 1e-5f) return 0f;
            float e = Mathf.Clamp01(SteerExpo);
            float shaped = ((1f - e) * m + e * m * m * m) * Mathf.Clamp(SteerRate, MinSteerRate, 1f);
            return x < 0f ? -shaped : shaped;
        }

        /// <summary>
        /// The stick.y to write for the brain truck from the left stick's forward value (-1..1). <paramref name="trainingThrottle"/>
        /// is the throttle that holds <see cref="TrainingSpeed"/> (<see cref="ExampleTruckBody.ThrottleFor"/>); with
        /// <paramref name="full"/> (the FULL switch) the stick gets the whole range and there is no steady throttle.
        /// </summary>
        public static float Throttle(float stickForward, float trainingThrottle, bool full)
        {
            if (full) { Cruising = false; return stickForward; }
            if (SteadyThrottle)
            {
                if (!Cruising && stickForward >= CruiseOn) Cruising = true;
                else if (Cruising && stickForward <= CruiseOff) Cruising = false;
                if (Cruising) return trainingThrottle;
            }
            else Cruising = false;
            return stickForward * trainingThrottle;
        }

        /// <summary>One line for the Drive window: what the aids are set to now.</summary>
        public static string Summary(bool docked)
        {
            return (docked ? "Docked" : "Handheld") + " · training speed " + TrainingSpeed.ToString("0.0") + " m/s · steering "
                 + Mathf.RoundToInt(SteerRate * 100f) + "%" + (SteerExpo > 0.01f ? ", soft centre" : "")
                 + (SteadyThrottle ? " · steady throttle on" : "");
        }
    }
}
