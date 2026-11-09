using System;

namespace Forge.Controllers
{
    /// <summary>
    /// Records (sensors → commanded actuators) at the physics rate while a person drives (contract section 4).
    /// <see cref="Record"/> runs every FixedUpdate into preallocated buffers with no allocations; <see cref="EndLap"/>
    /// copies the lap out (that allocates, once per lap). Idle stretches (every actuator near 0 for over 1 s) are dropped,
    /// so a pause doesn't teach the net to stop. When the buffer is full, later frames are dropped and <see cref="Full"/>
    /// is set; the default holds two minutes at 50 Hz.
    /// </summary>
    public sealed class LapRecorder
    {
        public const float IdleThreshold = 0.05f, IdleSeconds = 1f;

        public readonly LapSet Set;
        public readonly int Capacity;
        readonly float[] xs, ys;
        readonly int sensorCount, actuatorCount;
        int frames, nextId = 1;
        float idle, seconds;

        public bool Recording { get; private set; }
        public bool Full { get; private set; }
        /// <summary>Frames kept in the current lap.</summary>
        public int Frames => frames;
        /// <summary>Seconds driven in the current lap, idle time included.</summary>
        public float Seconds => seconds;

        public LapRecorder(ControllerIO io, string thing, int thingVersion, string controller,
                           string[][] mirrorSwap, string[] mirrorNegate, int hz = 50, float maxSeconds = 120f)
        {
            sensorCount = io.Sensors.Length;
            actuatorCount = io.Actuators.Length;
            Capacity = (int)Math.Ceiling(hz * maxSeconds);
            xs = new float[Capacity * sensorCount];
            ys = new float[Capacity * actuatorCount];
            Set = new LapSet
            {
                Thing = thing, ThingVersion = thingVersion, Controller = controller, Hz = hz,
                Sensors = (string[])io.SensorNames.Clone(), Actuators = (string[])io.ActuatorNames.Clone(),
                MirrorSwap = mirrorSwap ?? Array.Empty<string[]>(), MirrorNegate = mirrorNegate ?? Array.Empty<string>(),
            };
        }

        public void BeginLap()
        {
            frames = 0; idle = 0; seconds = 0; Full = false;
            Recording = true;
        }

        /// <summary>One physics step: the sensors the controller saw and what it commanded (<see cref="ControllerHost.Commanded"/>).</summary>
        public void Record(float[] sensors, float[] commanded, float dt)
        {
            if (!Recording) return;
            seconds += dt;
            bool still = true;
            for (int i = 0; i < actuatorCount; i++) if (Math.Abs(commanded[i]) > IdleThreshold) { still = false; break; }
            idle = still ? idle + dt : 0f;
            if (idle > IdleSeconds) return;
            if (frames >= Capacity) { Full = true; return; }
            Array.Copy(sensors, 0, xs, frames * sensorCount, sensorCount);
            Array.Copy(commanded, 0, ys, frames * actuatorCount, actuatorCount);
            frames++;
        }

        /// <summary>Ends the lap and adds it to <see cref="Set"/>. Returns null if nothing was recorded.</summary>
        public Lap EndLap(string outcome)
        {
            Recording = false;
            if (frames == 0) return null;
            var lap = new Lap { Id = nextId++, Seconds = (float)Math.Round(seconds, 2), Outcome = outcome, X = new float[frames][], Y = new float[frames][] };
            for (int t = 0; t < frames; t++)
            {
                lap.X[t] = new float[sensorCount];
                lap.Y[t] = new float[actuatorCount];
                Array.Copy(xs, t * sensorCount, lap.X[t], 0, sensorCount);
                Array.Copy(ys, t * actuatorCount, lap.Y[t], 0, actuatorCount);
            }
            Set.Laps.Add(lap);
            frames = 0;
            return lap;
        }

        /// <summary>Drops the current lap without keeping it.</summary>
        public void Cancel() { Recording = false; frames = 0; }
    }
}
