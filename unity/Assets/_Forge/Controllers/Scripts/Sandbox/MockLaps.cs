using System;
using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// Two completed laps of the mock slalom, recorded the way a person drives them: through the real manual controller
    /// and <see cref="LapRecorder"/>, with <see cref="MockDriver"/> standing in for the person. A crashed lap is driven
    /// again. Used by <c>FiveInARowCheck</c> and the Quest timing kit (<see cref="QuestTimingKit"/>). Runs in one call
    /// and allocates, so don't call it every frame.
    /// </summary>
    public static class MockLaps
    {
        /// <summary>
        /// Demo: both laps from the start line at 1.2 m/s, as a person drives them and as the sandbox's L key records them.
        /// SecondLapFaster: the second lap at 1.5 m/s. Varied: speeds and start offsets vary (BehaviourCloningCheck's laps).
        /// </summary>
        public enum Style { Demo, SecondLapFaster, Varied }

        const float Dt = 0.02f;

        public static LapSet Record(int driverSeed, float noise, Style style, out float lapSeconds, out int redos)
        {
            lapSeconds = 0f; redos = 0;
            var host = new ControllerHost(new ControllerIO(MockTruckSim.SensorNames, MockTruckSim.ActuatorNames));
            var manual = ControllerFile.Parse(Resources.Load<TextAsset>("Mocks/controllers/truck_manual").text, out string error);
            if (manual == null) throw new InvalidOperationException(error);
            host.Attach(manual);
            var recorder = new LapRecorder(host.IO, "truck", 2, "truck_manual", MockTruckSim.MirrorSwap, MockTruckSim.MirrorNegate);
            var sim = new MockTruckSim();
            // One driver for the session, so each lap (and a redo) has its own noise, like a person's.
            var driver = new MockDriver(driverSeed, 1.2f, noise);
            int lap = 0;
            try
            {
                while (recorder.Set.CompletedLaps < 2)
                {
                    if (lap >= 6) throw new InvalidOperationException($"driver {driverSeed} couldn't finish 2 laps in 6 tries");
                    int done2 = recorder.Set.CompletedLaps;
                    driver.Cruise = style == Style.Varied ? 1.1f + 0.2f * (lap % 2) : style == Style.SecondLapFaster && done2 == 1 ? 1.5f : 1.2f;
                    if (style == Style.Varied) sim.Reset(0.04f * (lap % 3), 0.03f * (lap % 2)); else sim.Reset();
                    host.ResetState();
                    recorder.BeginLap();
                    for (int i = 0; i < 1500 && !sim.Crashed && !sim.Finished; i++)
                    {
                        sim.Sense(host.IO.Sensors);
                        driver.Drive(sim);
                        host.Step(Dt, sim.Speed);
                        recorder.Record(host.IO.Sensors, host.Commanded, Dt);
                        sim.Step(host.IO.Actuators, Dt);
                    }
                    var done = recorder.EndLap(sim.Finished ? "completed" : sim.Crashed ? "crashed" : "aborted");
                    if (sim.Finished) lapSeconds += done.Seconds; else redos++;
                    lap++;
                }
            }
            finally { ManualInputs.Clear(); }
            return recorder.Set;
        }
    }
}
