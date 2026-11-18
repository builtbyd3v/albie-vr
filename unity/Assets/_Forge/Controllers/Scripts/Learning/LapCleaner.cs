using System;
using System.Collections.Generic;

namespace Forge.Controllers
{
    /// <summary>
    /// Makes a person's laps fit to learn from (PROJECT_CONTEXT 16.8, Nov 18: "after training Albie can't drive the truck").
    /// Laps driven by hand with the RC transmitter are not the scripted driver's: the truck waits at the start before the
    /// thumb pushes, the thumb slips off the throttle now and then, steering trembles and drops out when tracking flickers.
    /// Behaviour cloning copies all of it: the net learned "at rest at the start, throttle about 0", so the Autopilot sat
    /// on the start line until the 30 s timeout, candidate after candidate. Before training this:
    /// - keeps completed laps only (a crashed or aborted lap is not how to drive the course);
    /// - drops the start before the truck first moves and the coast at the end (no throttle, or the last rows);
    /// - smooths the steering with a centred window (<see cref="Options.SteerHalfWindow"/> rows each side);
    /// - when the steering is shaky (a hand on the transmitter, <see cref="Options.HandNoise"/>): shifts it
    ///   <see cref="Options.SteerLead"/> rows earlier (a hand's reaction lag) and replaces the throttle with the one that
    ///   holds the laps' own median speed (<see cref="Options.SteadyThrottle"/>), so the Autopilot drives at the speed the
    ///   laps were driven and always pulls away from rest; a steady driver's throttle is only smoothed;
    /// - drops a lap too short to teach anything (<see cref="Options.MinSeconds"/>).
    /// Plain C#, allocation per call only (not per frame). The laps it was given are not changed.
    /// </summary>
    public static class LapCleaner
    {
        public sealed class Options
        {
            /// <summary>The truck counts as moving above this (m/s, full size).</summary>
            public float MovingSpeed = 0.08f;
            /// <summary>Rows each side of a row its steering is averaged over (4 at 50 Hz: ±0.08 s).</summary>
            public int SteerHalfWindow = 4;
            /// <summary>Rows the steering label is taken from ahead (a hand turns about 0.1 s after it sees).</summary>
            public int SteerLead = 3;
            /// <summary>Relabel the throttle to the one holding the laps' median speed. Off: the throttle is smoothed instead.</summary>
            public bool SteadyThrottle = true;
            /// <summary>Rows each side for the throttle's smoothing when <see cref="SteadyThrottle"/> is off (±0.24 s).</summary>
            public int ThrottleHalfWindow = 12;
            /// <summary>A cleaned lap shorter than this (s) is dropped.</summary>
            public float MinSeconds = 1.5f;
            /// <summary>Rows dropped from a lap's end (the last moments into the finish).</summary>
            public int TrimEndRows = 3;
            /// <summary>
            /// Laps whose steering shakes less than this (RMS removed by the smoothing, steer.angle units) are driven by a
            /// steady hand or a script: they keep their own throttle (smoothed) and get no lead. Measured on the mock: scripted
            /// laps about 0.012, simulated transmitter laps by hand 0.036 (shaky) to 0.066 (very shaky).
            /// </summary>
            public float HandNoise = 0.025f;
            /// <summary>The steady speed is kept within this (m/s): never a crawl, never faster than a hand lap is meant to be.</summary>
            public float MinCruise = 0.3f, MaxCruise = 1.2f;
        }

        /// <summary>What cleaning did, for the training entry and what Albie says.</summary>
        public struct Report
        {
            public int LapsIn, LapsKept, RowsIn, RowsKept;
            /// <summary>The laps' median moving speed (m/s, full size) and the throttle that holds it (when relabelled).</summary>
            public float CruiseSpeed, CruiseThrottle;
            /// <summary>RMS of the steering removed by smoothing (steer.angle units): above about 0.05 the hand was very shaky.</summary>
            public float SteerNoise;
            /// <summary>The laps looked hand-driven (<see cref="Options.HandNoise"/>): the lead and the steady throttle were used.</summary>
            public bool HandDriven;
            /// <summary>Rows at rest dropped from the laps' starts, and rows of coasting dropped from their ends.</summary>
            public int StartRowsDropped, EndRowsDropped;
            public bool Jerky => SteerNoise > 0.05f;
        }

        public const float SensorTopSpeed = MockTruckSim.TopSpeed;

        /// <summary>
        /// The cleaned laps (completed only) in a new set with the same channels. <paramref name="physics"/> is the truck's
        /// design (null: the original tuning), for the throttle that holds the median speed.
        /// </summary>
        public static LapSet Clean(LapSet laps, TruckPhysics? physics, out Report report, Options options = null)
        {
            if (laps == null) throw new ArgumentNullException(nameof(laps));
            var o = options ?? new Options();
            report = default;
            var outSet = new LapSet
            {
                Thing = laps.Thing, ThingVersion = laps.ThingVersion, Controller = laps.Controller, Hz = laps.Hz,
                Sensors = laps.Sensors, Actuators = laps.Actuators, MirrorSwap = laps.MirrorSwap, MirrorNegate = laps.MirrorNegate,
                Notes = "cleaned (LapCleaner)",
            };
            int speed = Array.IndexOf(laps.Sensors, "speed.forward");
            int steer = Array.IndexOf(laps.Actuators, "steer.angle"), throttle = Array.IndexOf(laps.Actuators, "drive.throttle");
            float hz = laps.Hz > 0 ? laps.Hz : 50;
            float moving = o.MovingSpeed / SensorTopSpeed;

            // Pass 1: the rows of each completed lap worth keeping.
            var spans = new List<(Lap lap, int from, int to)>();
            var speeds = new List<float>();
            foreach (var lap in laps.Laps)
            {
                report.LapsIn++;
                report.RowsIn += lap.X.Length;
                if (lap.Outcome != "completed" || lap.X.Length == 0) continue;
                int from = 0, to = lap.X.Length;
                if (speed >= 0)
                    while (from < to && Math.Abs(lap.X[from][speed]) < moving && (throttle < 0 || lap.Y[from][throttle] < 0.5f)) from++;
                int end = to;
                if (throttle >= 0) while (to > from && lap.Y[to - 1][throttle] <= 0.02f) to--;
                to = Math.Max(from, to - o.TrimEndRows);
                report.StartRowsDropped += from;
                report.EndRowsDropped += end - to;
                if ((to - from) / hz < o.MinSeconds) continue;
                spans.Add((lap, from, to));
                if (speed >= 0) for (int t = from; t < to; t++) if (Math.Abs(lap.X[t][speed]) >= moving) speeds.Add(lap.X[t][speed] * SensorTopSpeed);
            }
            if (spans.Count == 0) return outSet;

            float cruise = 0f, cruiseThrottle = 0f;
            if (speeds.Count > 0)
            {
                speeds.Sort();
                cruise = Math.Max(o.MinCruise, Math.Min(o.MaxCruise, speeds[speeds.Count / 2]));
                cruiseThrottle = ThrottleFor(cruise, physics);
            }
            report.CruiseSpeed = cruise;

            // How shaky the steering is (what a centred window takes out of it): a hand's laps get the lead and the steady
            // throttle, a steady driver's keep their own throttle (smoothed) and no lead.
            double shake = 0; int shakeRows = 0;
            if (steer >= 0)
                foreach (var (lap, from, to) in spans)
                    for (int src = from; src < to; src++)
                    {
                        int a = Math.Max(from, src - o.SteerHalfWindow), b = Math.Min(to - 1, src + o.SteerHalfWindow);
                        double sum = 0;
                        for (int k = a; k <= b; k++) sum += lap.Y[k][steer];
                        double d = lap.Y[src][steer] - sum / (b - a + 1);
                        shake += d * d; shakeRows++;
                    }
            report.SteerNoise = shakeRows > 0 ? (float)Math.Sqrt(shake / shakeRows) : 0f;
            report.HandDriven = report.SteerNoise >= o.HandNoise;
            int lead = report.HandDriven ? o.SteerLead : 0;
            bool steady = o.SteadyThrottle && report.HandDriven;
            report.CruiseThrottle = steady ? cruiseThrottle : 0f;

            // Pass 2: the cleaned rows.
            int id = 1;
            foreach (var (lap, from, to) in spans)
            {
                int n = to - from;
                var lx = new float[n][]; var ly = new float[n][];
                for (int t = 0; t < n; t++)
                {
                    int src = from + t;
                    lx[t] = (float[])lap.X[src].Clone();
                    ly[t] = (float[])lap.Y[src].Clone();
                    if (steer >= 0)
                    {
                        // Centred average around the row a little ahead (the lead), within the lap's kept rows.
                        int c = Math.Min(to - 1, src + lead);
                        int a = Math.Max(from, c - o.SteerHalfWindow), b = Math.Min(to - 1, c + o.SteerHalfWindow);
                        double sum = 0;
                        for (int k = a; k <= b; k++) sum += lap.Y[k][steer];
                        ly[t][steer] = (float)(sum / (b - a + 1));
                    }
                    if (throttle >= 0)
                    {
                        if (steady && cruiseThrottle > 0f) ly[t][throttle] = cruiseThrottle;
                        else
                        {
                            int a = Math.Max(from, src - o.ThrottleHalfWindow), b = Math.Min(to - 1, src + o.ThrottleHalfWindow);
                            double sum = 0;
                            for (int k = a; k <= b; k++) sum += lap.Y[k][throttle];
                            ly[t][throttle] = (float)(sum / (b - a + 1));
                        }
                    }
                }
                outSet.Laps.Add(new Lap { Id = id++, Seconds = (float)Math.Round(n / hz, 2), Outcome = "completed", X = lx, Y = ly });
                report.LapsKept++;
                report.RowsKept += n;
            }
            return outSet;
        }

        /// <summary>The throttle that holds <paramref name="speed"/> (m/s, full size) on a design (null: the original tuning).</summary>
        public static float ThrottleFor(float speed, TruckPhysics? physics)
        {
            float v = Math.Max(0f, speed);
            if (physics.HasValue && physics.Value.Accel > 0f)
            {
                var p = physics.Value;
                return Math.Max(0f, Math.Min(1f, (p.LinearDrag * v + p.QuadDrag * v * v) / p.Accel));
            }
            return Math.Max(0f, Math.Min(1f, v * MockTruckSim.Drag / MockTruckSim.MaxAccel));
        }
    }
}
