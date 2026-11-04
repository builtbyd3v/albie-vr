using System;
using Newtonsoft.Json.Linq;

namespace Forge.Controllers
{
    /// <summary>
    /// The fixed manual inputs (contract section 3.2). Input sources write them every frame: the hands RC transmitter
    /// and desk joystick (Person 1), voice drive commands (Person 3), the sandbox keyboard. All values are in [-1, 1].
    /// One set for the whole app: only one Thing is driven by hand at a time.
    /// </summary>
    public static class ManualInputs
    {
        public static readonly string[] Names =
            { "stick.x", "stick.y", "stick2.x", "stick2.y", "hand.roll", "voice.throttle", "voice.steer" };

        public static readonly float[] Values = new float[Names.Length];

        public static int IndexOf(string name) => Array.IndexOf(Names, name);

        /// <summary>Sets one input by name; cache <see cref="IndexOf"/> and write <see cref="Values"/> in per-frame code.</summary>
        public static void Set(string name, float value)
        {
            int i = IndexOf(name);
            if (i >= 0) Values[i] = Math.Max(-1f, Math.Min(1f, value));
        }

        public static void Clear() => Array.Clear(Values, 0, Values.Length);
    }

    /// <summary>
    /// <c>kind: manual</c>: bindings from <see cref="ManualInputs"/> to actuators (contract section 3.2).
    /// out = sign(v) · |v|^(1+expo) · scale, after a deadzone that is rescaled so a full stick is still 1. Bindings to
    /// the same actuator add up; the host clamps the sum.
    /// </summary>
    public sealed class ManualController : IThingController
    {
        readonly ControllerFile file;
        int[] input, actuator;
        float[] scale, deadzone, expo;

        public ManualController(ControllerFile file) { this.file = file; }

        public string Kind => "manual";
        public string Name => file.Name;

        public string Bind(ControllerIO io)
        {
            if (!(file.Raw["bindings"] is JArray bindings) || bindings.Count == 0)
                return "bad_value: bindings: a manual controller needs at least one binding";
            int n = bindings.Count;
            var input = new int[n]; var actuator = new int[n];
            var scale = new float[n]; var deadzone = new float[n]; var expo = new float[n];
            for (int i = 0; i < n; i++)
            {
                var b = bindings[i];
                string where = $"bindings[{i}]";
                input[i] = ManualInputs.IndexOf((string)b["input"]);
                if (input[i] < 0)
                    return $"unknown_channel: {where}.input: no manual input \"{(string)b["input"]}\" (inputs are {string.Join(", ", ManualInputs.Names)})";
                actuator[i] = io.ActuatorIndex((string)b["actuator"]);
                if (actuator[i] < 0)
                    return $"unknown_channel: {where}.actuator: no actuator \"{(string)b["actuator"]}\" on this Thing (it has {io.DescribeActuators()})";
                if (!Optional(b["scale"], 1f, where + ".scale", out scale[i], out string error)) return error;
                if (!Optional(b["deadzone"], 0f, where + ".deadzone", out deadzone[i], out error)) return error;
                if (!Optional(b["expo"], 0f, where + ".expo", out expo[i], out error)) return error;
                if (deadzone[i] < 0 || deadzone[i] >= 1) return $"bad_value: {where}.deadzone must be in [0, 1)";
                if (expo[i] < 0) return $"bad_value: {where}.expo must not be negative";
            }
            this.input = input; this.actuator = actuator; this.scale = scale; this.deadzone = deadzone; this.expo = expo;
            return null;
        }

        bool Optional(JToken token, float fallback, string where, out float value, out string error)
        {
            if (token == null || token.Type == JTokenType.Null) { value = fallback; error = null; return true; }
            return file.TryNumber(token, where, out value, out error);
        }

        public void Tick(ControllerIO io, float dt)
        {
            float[] values = ManualInputs.Values, outputs = io.Actuators;
            for (int i = 0; i < input.Length; i++)
            {
                float v = values[input[i]], mag = Math.Abs(v);
                if (mag <= deadzone[i]) continue;
                mag = (mag - deadzone[i]) / (1f - deadzone[i]);
                if (expo[i] > 0) mag = (float)Math.Pow(mag, 1f + expo[i]);
                outputs[actuator[i]] += (v < 0 ? -mag : mag) * scale[i];
            }
        }

        public void ResetState() { }
    }
}
