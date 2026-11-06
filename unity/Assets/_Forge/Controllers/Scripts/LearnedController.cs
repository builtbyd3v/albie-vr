using System;
using Forge.ML;
using Newtonsoft.Json.Linq;

namespace Forge.Controllers
{
    /// <summary>
    /// <c>kind: learned</c>: an MLP from a controller file (contract section 3.4), run each tick with the Neural Net
    /// Lab's <see cref="NNNetwork"/>. Inputs are standardised as (x − mean) / std; outputs are written to their actuators
    /// times <c>scale</c> (a number or a $tunable). Weights use the [layer][to][from] layout. Bind builds the net and
    /// checks every shape; Tick allocates nothing. The speed governor (<c>limits.speed</c>) is the host's, so turning
    /// <c>max_speed</c> up makes a learned controller drive faster (PROJECT_CONTEXT 15.6, step 9).
    /// </summary>
    public sealed class LearnedController : IThingController
    {
        public const int MaxHiddenLayers = NetFile.MaxHiddenLayers, MaxNeurons = NetFile.MaxNeurons;

        readonly ControllerFile file;
        NNNetwork net;
        int[] inputSensor, outputActuator;
        float[] mean, invStd, scale, features, result;

        public LearnedController(ControllerFile file) { this.file = file; }

        public string Kind => "learned";
        public string Name => file.Name;
        public NNNetwork Net => net;

        public string Bind(ControllerIO io)
        {
            var raw = file.Raw;
            if (!(raw["inputs"] is JArray inputs) || inputs.Count == 0) return "bad_value: inputs: a learned controller needs at least one input";
            if (!(raw["outputs"] is JArray outputs) || outputs.Count == 0) return "bad_value: outputs: a learned controller needs at least one output";
            string error = NetFile.ReadInputs(inputs, io, "inputs", out var inputSensor, out var mean, out var invStd);
            if (error != null) return error;
            if ((error = NetFile.Build(raw["net"] as JObject, inputs.Count, outputs.Count, "net", out var built)) != null) return error;

            // Outputs: actuator indexes and scales.
            var outputActuator = new int[outputs.Count]; var scale = new float[outputs.Count];
            for (int j = 0; j < outputs.Count; j++)
            {
                string actuator = (string)outputs[j]["actuator"];
                outputActuator[j] = io.ActuatorIndex(actuator);
                if (outputActuator[j] < 0) return $"unknown_channel: outputs[{j}]: no actuator \"{actuator}\" on this Thing (it has {io.DescribeActuators()})";
                var s = outputs[j]["scale"];
                if (s == null || s.Type == JTokenType.Null) scale[j] = 1f;
                else if (!file.TryNumber(s, $"outputs[{j}].scale", out scale[j], out error)) return error;
            }

            net = built;
            this.inputSensor = inputSensor; this.mean = mean; this.invStd = invStd;
            this.outputActuator = outputActuator; this.scale = scale;
            features = new float[inputs.Count];
            result = new float[outputs.Count];
            return null;
        }

        public void Tick(ControllerIO io, float dt)
        {
            float[] sensors = io.Sensors, actuators = io.Actuators;
            for (int i = 0; i < features.Length; i++) features[i] = (sensors[inputSensor[i]] - mean[i]) * invStd[i];
            net.Forward(features, result);
            for (int j = 0; j < result.Length; j++) actuators[outputActuator[j]] = result[j] * scale[j];
        }

        public void ResetState() { }
    }
}
