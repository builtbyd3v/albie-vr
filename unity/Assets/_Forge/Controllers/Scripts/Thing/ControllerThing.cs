using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// What a Thing's body does for its controller (C-19 sections 1 and 2): fill the sensors, report its forward speed,
    /// and turn the actuators into physics. Person 2's Thing runtime implements it per Thing (wheels, servo, rotors);
    /// <see cref="ExampleTruckBody"/> is a small worked example.
    /// </summary>
    public interface IThingBody
    {
        /// <summary>Fixed per Thing version, e.g. <c>ray_front.d0</c> … <c>speed.forward</c>.</summary>
        string[] SensorNames { get; }
        /// <summary>e.g. <c>steer.angle</c>, <c>drive.throttle</c>.</summary>
        string[] ActuatorNames { get; }
        /// <summary>The left-right mirror map (section 2.3): pairs to swap and channels to negate. Empty if it has none.</summary>
        string[][] MirrorSwap { get; }
        string[] MirrorNegate { get; }
        /// <summary>Writes every sensor, normalised, in <see cref="SensorNames"/> order. Called before each tick.</summary>
        void Sense(float[] sensors);
        /// <summary>Forward speed in m/s, for the controller's <c>limits.speed</c>.</summary>
        float ForwardSpeed { get; }
        /// <summary>Turns the actuators (each in [-1, 1], already clamped and speed-limited) into torque, angle or thrust.</summary>
        void Apply(float[] actuators, float dt);
    }

    /// <summary>
    /// The plug between a Thing and its controller, for Person 2's Thing runtime (C-19). Put it beside the Thing's
    /// <see cref="IThingBody"/>. Each physics step it does the contract's call order: the body writes the sensors, the
    /// <see cref="ControllerHost"/> ticks the controller (sanitise, clamp, speed limit), a lap being recorded gets the
    /// step, and the body applies the actuators. While it's enabled the Thing is registered with
    /// <see cref="ThingHosts"/> under <see cref="thingName"/>, so Albie's tools reach it (template_status, learned_brake).
    /// Swap controllers with <see cref="Attach"/>; record laps for training with <see cref="BeginLap"/> and
    /// <see cref="EndLap"/>. Allocation-free per step.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ControllerThing : MonoBehaviour
    {
        /// <summary>The Thing's name, as Albie's tools and the lap files call it.</summary>
        public string thingName = "truck";
        public int thingVersion = 1;
        /// <summary>The controller it starts with (optional): an <c>albie.controller/1</c> file.</summary>
        public TextAsset controller;
        /// <summary>Tick in FixedUpdate. Off when something else steps it (a check, a replay).</summary>
        public bool tickInFixedUpdate = true;

        public IThingBody Body { get; private set; }
        public ControllerHost Host { get; private set; }
        public LapRecorder Recorder { get; private set; }
        /// <summary>Why the starting controller didn't attach (null when it did, or there wasn't one).</summary>
        public string StartError { get; private set; }

        /// <summary>Finds the body and makes the host and recorder (Awake does it; a check calls it in edit mode).</summary>
        public string Init()
        {
            if (Host != null) return null;
            Body = GetComponent<IThingBody>();
            if (Body == null) return StartError = $"no_body: {name} has no IThingBody beside its ControllerThing";
            var io = new ControllerIO(Body.SensorNames, Body.ActuatorNames);
            Host = new ControllerHost(io);
            Recorder = new LapRecorder(io, thingName, thingVersion, thingName + "_manual", Body.MirrorSwap, Body.MirrorNegate);
            if (controller != null) StartError = Attach(controller.text);
            return StartError;
        }

        void Awake()
        {
            string error = Init();
            if (error != null) Debug.LogWarning($"[Controllers] {name}: {error}");
        }

        void OnEnable() => Register();
        void OnDisable() => Unregister();

        /// <summary>Makes the Thing reachable by name (OnEnable does it).</summary>
        public void Register()
        {
            Init();
            if (Host != null) ThingHosts.Register(thingName, Host);
        }

        public void Unregister() => ThingHosts.Unregister(thingName, Host);

        /// <summary>Parses and attaches a controller file. Returns the error, or null; on an error the old controller keeps running.</summary>
        public string Attach(string json)
        {
            if (Host == null) Init();
            if (Host == null) return StartError;
            var file = ControllerFile.Parse(json, out string error);
            return error ?? Host.Attach(file);
        }

        void FixedUpdate()
        {
            if (tickInFixedUpdate && Host != null) Tick(Time.fixedDeltaTime);
        }

        /// <summary>One physics step, in the contract's order.</summary>
        public void Tick(float dt)
        {
            Body.Sense(Host.IO.Sensors);
            Host.Step(dt, Body.ForwardSpeed);
            Recorder.Record(Host.IO.Sensors, Host.Commanded, dt);
            Body.Apply(Host.IO.Actuators, dt);
        }

        /// <summary>Starts recording a lap of whatever drives the Thing now (by hand: a <c>manual</c> controller).</summary>
        public void BeginLap()
        {
            if (Host.File != null) Recorder.Set.Controller = Host.File.Name;
            Recorder.BeginLap();
        }

        /// <summary>Ends the lap: <c>completed</c>, <c>crashed</c> or <c>aborted</c> (contract section 4). Training uses completed laps.</summary>
        public Lap EndLap(string outcome) => Recorder.EndLap(outcome);
    }
}
