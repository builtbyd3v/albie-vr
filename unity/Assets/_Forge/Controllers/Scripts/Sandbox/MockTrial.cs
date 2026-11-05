namespace Forge.Controllers
{
    /// <summary>
    /// A closed-loop trial on the mock slalom: lets a trained net drive <see cref="MockTruckSim"/> from 15 starts
    /// (5 offsets × 3 headings) with the host's speed governor. Sandbox and checks only: on a headset the trial is the
    /// real arena run. Allocates its buffers per call, so don't call it every frame.
    /// </summary>
    public static class MockTrial
    {
        public const int Starts = 15;
        static readonly float[] Offsets = { 0f, 0.1f, -0.1f, 0.3f, -0.3f };
        static readonly float[] Headings = { 0f, 0.1f, -0.1f };

        /// <summary>How many of the 15 starts the net drives to the finish line.</summary>
        public static int Finishes(BehaviourCloner cloner, float speedLimit = 1.5f, float dt = 0.02f)
        {
            int finished = 0;
            var sensors = new float[MockTruckSim.SensorNames.Length];
            var outputs = new float[MockTruckSim.ActuatorNames.Length];
            foreach (float y0 in Offsets)
                foreach (float heading in Headings)
                {
                    var sim = new MockTruckSim(y0, heading);
                    for (int i = 0; i < 1500 && !sim.Crashed && !sim.Finished; i++)
                    {
                        sim.Sense(sensors);
                        cloner.Predict(sensors, outputs);
                        if (sim.Speed > speedLimit && outputs[1] > 0) outputs[1] = 0;
                        sim.Step(outputs, dt);
                    }
                    if (sim.Finished) finished++;
                }
            return finished;
        }
    }
}
