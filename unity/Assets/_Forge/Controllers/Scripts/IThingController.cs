namespace Forge.Controllers
{
    /// <summary>
    /// The brain of a Thing (C-19, docs/CONTROLLER_CONTRACT.md section 1). <see cref="ControllerHost"/> calls it once
    /// per physics step: the runtime fills the sensors, the controller writes the actuators.
    /// </summary>
    public interface IThingController
    {
        /// <summary>"manual", "graph" or "learned".</summary>
        string Kind { get; }

        /// <summary>The controller file's name, without .json.</summary>
        string Name { get; }

        /// <summary>
        /// Once per Thing version or controller swap: resolve channel names to indexes and allocate buffers.
        /// Returns null if it can run, else a reason the agent and the UI can show (contract section 7).
        /// </summary>
        string Bind(ControllerIO io);

        /// <summary>Every FixedUpdate. Read io.Sensors, write io.Actuators. No allocations, no LINQ, no logging.</summary>
        void Tick(ControllerIO io, float dt);

        /// <summary>Clear PID integrators and any history (Thing reset, respawn, structure change).</summary>
        void ResetState();
    }
}
