using System;
using System.Diagnostics;

namespace Forge.Controllers
{
    /// <summary>
    /// Before the Autopilot drives for real, each trained candidate rehearses the open course in the mock truck sim
    /// (<see cref="MockTruckSim"/> on a <see cref="MockCourse"/>) from <see cref="Starts"/> hand-placed starts, and the
    /// candidates are tried best-first (<see cref="Order"/>). Validation loss can't pick the candidate that drives
    /// (PROJECT_CONTEXT 16.8), but the course is known, so a rehearsal can.
    /// Measured on the mock sim (2 demo laps, 3 candidates, one random hand-placed start each):
    /// - 36 rooms (the 6 mock rooms plus 30 random), 144 attempts: the first driver finished 113 times with today's order
    ///   and 127 with the rehearsal's; 130 had some candidate that could.
    /// - 30 unseen random rooms, 120 attempts: 89 and 102; 105 could.
    /// It runs a few ms a frame (<see cref="Step"/>), so the headset holds its frame rate. The whole rehearsal took at most
    /// 250 ms of compute in the Editor (M4 Pro).
    /// </summary>
    public sealed class Rehearsal
    {
        public const float Dt = 0.02f;
        public const float TimeoutS = 30f;
        /// <summary>Starts as (offset left of the line in m, heading in rad): on the line, and a little off it either way.</summary>
        public static readonly float[] StartOffsets = { 0f, 0.08f, -0.08f }, StartTurns = { 0f, 0.05f, -0.05f };
        public static int Starts => StartOffsets.Length * StartTurns.Length;

        readonly ControllerFile[] files;
        readonly MockCourse course;
        readonly ControllerHost host = new ControllerHost(new ControllerIO(MockTruckSim.SensorNames, MockTruckSim.ActuatorNames));
        readonly int[] finished;
        readonly Stopwatch clock = new Stopwatch();
        int candidate, start;
        MockTruckSim sim;

        /// <summary>Rehearsal finishes per candidate (out of <see cref="Starts"/>), in the files' order.</summary>
        public int[] Finished => finished;
        public bool Done => candidate >= files.Length;
        public double ComputeSeconds => clock.Elapsed.TotalSeconds;

        public Rehearsal(ControllerFile[] files, MockCourse course)
        {
            this.files = files ?? throw new ArgumentNullException(nameof(files));
            this.course = course ?? throw new ArgumentNullException(nameof(course));
            finished = new int[files.Length];
        }

        /// <summary>Drives for up to <paramref name="budgetMs"/> of compute. Returns true when every candidate has rehearsed.</summary>
        public bool Step(double budgetMs)
        {
            if (Done) return true;
            long until = clock.ElapsedTicks + (long)(budgetMs * Stopwatch.Frequency / 1000.0);
            clock.Start();
            try
            {
                while (!Done && clock.ElapsedTicks < until)
                {
                    if (sim == null && !Begin()) continue;
                    // A few sim steps between clock reads.
                    for (int i = 0; i < 25 && !sim.Crashed && !sim.Finished && sim.Time < TimeoutS; i++)
                    {
                        sim.Sense(host.IO.Sensors);
                        host.Step(Dt, sim.Speed);
                        sim.Step(host.IO.Actuators, Dt);
                    }
                    if (sim.Crashed || sim.Finished || sim.Time >= TimeoutS) End(sim.Finished);
                }
            }
            finally { clock.Stop(); }
            return Done;
        }

        // Starts the next run, or skips a candidate the host refuses (it scores 0). False when nothing was started.
        bool Begin()
        {
            if (start == 0 && host.Attach(files[candidate]) != null) { candidate++; return false; }
            host.ResetState();
            sim = new MockTruckSim(course, StartOffsets[start / StartTurns.Length], StartTurns[start % StartTurns.Length]);
            return true;
        }

        void End(bool ok)
        {
            if (ok) finished[candidate]++;
            sim = null;
            if (++start < Starts) return;
            start = 0;
            candidate++;
        }

        /// <summary>Candidate indexes, most rehearsal finishes first; ties keep training order.</summary>
        public int[] Order()
        {
            var order = new int[files.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            // Insertion sort: stable, and there are only a few candidates.
            for (int i = 1; i < order.Length; i++)
                for (int j = i; j > 0 && finished[order[j]] > finished[order[j - 1]]; j--)
                    (order[j], order[j - 1]) = (order[j - 1], order[j]);
            return order;
        }
    }
}
