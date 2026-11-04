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
        public const int MaxHiddenLayers = 4, MaxNeurons = 32;

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
            if (!(raw["net"] is JObject n)) return "bad_value: net is missing";
            if (!(n["sizes"] is JArray sizesToken) || sizesToken.Count < 2) return "bad_value: net.sizes must list at least the inputs and outputs";
            var sizes = new int[sizesToken.Count];
            for (int l = 0; l < sizes.Length; l++)
            {
                if (sizesToken[l].Type != JTokenType.Integer || (int)sizesToken[l] < 1) return $"bad_value: net.sizes[{l}] must be a positive whole number";
                sizes[l] = (int)sizesToken[l];
            }
            if (sizes.Length - 2 > MaxHiddenLayers) return $"too_big: net.sizes: at most {MaxHiddenLayers} hidden layers on the device (got {sizes.Length - 2})";
            for (int l = 1; l < sizes.Length - 1; l++)
                if (sizes[l] > MaxNeurons) return $"too_big: net.sizes[{l}] is {sizes[l]}: at most {MaxNeurons} neurons per hidden layer on the device";
            if (sizes[0] != inputs.Count) return $"shape_mismatch: net.sizes[0] is {sizes[0]} but there are {inputs.Count} inputs";
            if (sizes[sizes.Length - 1] != outputs.Count) return $"shape_mismatch: net.sizes[{sizes.Length - 1}] is {sizes[sizes.Length - 1]} but there are {outputs.Count} outputs";

            if (!TryActivation((string)n["activation"] ?? "tanh", out var activation)) return $"bad_value: net.activation \"{(string)n["activation"]}\" must be tanh, relu, sigmoid or linear";
            string outputActivation = (string)n["output_activation"] ?? "tanh";
            if (outputActivation != "tanh") return $"bad_value: net.output_activation \"{outputActivation}\": only tanh runs on the device for now";

            // Inputs: sensor indexes and standardisation.
            var inputSensor = new int[inputs.Count]; var mean = new float[inputs.Count]; var invStd = new float[inputs.Count];
            for (int i = 0; i < inputs.Count; i++)
            {
                string channel = (string)inputs[i]["channel"];
                inputSensor[i] = io.SensorIndex(channel);
                if (inputSensor[i] < 0) return $"unknown_channel: inputs[{i}]: no sensor \"{channel}\" on this Thing (it has {io.DescribeSensors()})";
                if (!Finite(inputs[i]["mean"], out mean[i])) return $"bad_value: inputs[{i}].mean must be a number";
                if (!Finite(inputs[i]["std"], out float std)) return $"bad_value: inputs[{i}].std must be a number";
                invStd[i] = std == 0 ? 1f : 1f / std;
            }

            // Outputs: actuator indexes and scales.
            var outputActuator = new int[outputs.Count]; var scale = new float[outputs.Count];
            for (int j = 0; j < outputs.Count; j++)
            {
                string actuator = (string)outputs[j]["actuator"];
                outputActuator[j] = io.ActuatorIndex(actuator);
                if (outputActuator[j] < 0) return $"unknown_channel: outputs[{j}]: no actuator \"{actuator}\" on this Thing (it has {io.DescribeActuators()})";
                var s = outputs[j]["scale"];
                if (s == null || s.Type == JTokenType.Null) scale[j] = 1f;
                else if (!file.TryNumber(s, $"outputs[{j}].scale", out scale[j], out string error)) return error;
            }

            // Weights and biases, checked shape by shape.
            if (!(n["weights"] is JArray weights) || weights.Count != sizes.Length - 1) return $"shape_mismatch: net.weights needs {sizes.Length - 1} layers";
            if (!(n["biases"] is JArray biases) || biases.Count != sizes.Length - 1) return $"shape_mismatch: net.biases needs {sizes.Length - 1} layers";
            var hidden = new int[sizes.Length - 2];
            Array.Copy(sizes, 1, hidden, 0, hidden.Length);
            var built = new NNNetwork(sizes[0], hidden, sizes[sizes.Length - 1], activation, 0);
            for (int l = 1; l < sizes.Length; l++)
            {
                if (!(weights[l - 1] is JArray rows) || rows.Count != sizes[l])
                    return $"shape_mismatch: net.weights[{l - 1}]: {sizes[l]} rows of {sizes[l - 1]} expected, got {(weights[l - 1] as JArray)?.Count ?? 0} rows";
                if (!(biases[l - 1] is JArray bs) || bs.Count != sizes[l])
                    return $"shape_mismatch: net.biases[{l - 1}]: {sizes[l]} values expected, got {(biases[l - 1] as JArray)?.Count ?? 0}";
                for (int j = 0; j < sizes[l]; j++)
                {
                    if (!(rows[j] is JArray row) || row.Count != sizes[l - 1])
                        return $"shape_mismatch: net.weights[{l - 1}][{j}]: {sizes[l - 1]} values expected, got {(rows[j] as JArray)?.Count ?? 0}";
                    for (int k = 0; k < sizes[l - 1]; k++)
                        if (!Finite(row[k], out built.weights[l][j][k])) return $"bad_value: net.weights[{l - 1}][{j}][{k}] is not a finite number";
                    if (!Finite(bs[j], out built.biases[l][j])) return $"bad_value: net.biases[{l - 1}][{j}] is not a finite number";
                }
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

        static bool Finite(JToken token, out float value)
        {
            value = 0;
            if (!ControllerFile.IsNumber(token)) return false;
            value = (float)token;
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        static bool TryActivation(string name, out NNActivation activation)
        {
            switch (name)
            {
                case "tanh": activation = NNActivation.Tanh; return true;
                case "relu": activation = NNActivation.ReLU; return true;
                case "sigmoid": activation = NNActivation.Sigmoid; return true;
                case "linear": activation = NNActivation.Linear; return true;
                default: activation = NNActivation.Tanh; return false;
            }
        }
    }
}
