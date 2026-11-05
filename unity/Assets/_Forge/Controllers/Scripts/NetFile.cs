using System;
using Forge.ML;
using Newtonsoft.Json.Linq;

namespace Forge.Controllers
{
    /// <summary>
    /// Reads the <c>inputs</c> and <c>net</c> blocks shared by a <c>learned</c> controller (contract section 3.4) and a
    /// graph <c>model</c> node (section 3.5, proposed): input channels with their standardisation, and an MLP in the
    /// <see cref="NNNetwork"/> [layer][to][from] layout. Every shape is checked; errors use the contract's codes. Runs
    /// in Bind, never per tick.
    /// </summary>
    public static class NetFile
    {
        public const int MaxHiddenLayers = 4, MaxNeurons = 32;

        /// <summary>Sensor indexes, means and 1/std for each entry of <paramref name="inputs"/>.</summary>
        public static string ReadInputs(JArray inputs, ControllerIO io, string where, out int[] sensor, out float[] mean, out float[] invStd)
        {
            sensor = null; mean = null; invStd = null;
            if (inputs == null || inputs.Count == 0) return $"bad_value: {where}: at least one input is needed";
            var s = new int[inputs.Count]; var m = new float[inputs.Count]; var inv = new float[inputs.Count];
            for (int i = 0; i < inputs.Count; i++)
            {
                string channel = (string)inputs[i]["channel"];
                s[i] = io.SensorIndex(channel);
                if (s[i] < 0) return $"unknown_channel: {where}[{i}]: no sensor \"{channel}\" on this Thing (it has {io.DescribeSensors()})";
                if (!Finite(inputs[i]["mean"], out m[i])) return $"bad_value: {where}[{i}].mean must be a number";
                if (!Finite(inputs[i]["std"], out float std)) return $"bad_value: {where}[{i}].std must be a number";
                inv[i] = std == 0 ? 1f : 1f / std;
            }
            sensor = s; mean = m; invStd = inv;
            return null;
        }

        /// <summary>
        /// Builds the MLP in <paramref name="n"/> (the <c>net</c> object) with <paramref name="inputs"/> inputs and
        /// <paramref name="outputs"/> outputs. <paramref name="where"/> prefixes error paths (<c>net</c>, or a node's).
        /// </summary>
        public static string Build(JObject n, int inputs, int outputs, string where, out NNNetwork net)
        {
            net = null;
            if (n == null) return $"bad_value: {where} is missing";
            if (!(n["sizes"] is JArray sizesToken) || sizesToken.Count < 2) return $"bad_value: {where}.sizes must list at least the inputs and outputs";
            var sizes = new int[sizesToken.Count];
            for (int l = 0; l < sizes.Length; l++)
            {
                if (sizesToken[l].Type != JTokenType.Integer || (int)sizesToken[l] < 1) return $"bad_value: {where}.sizes[{l}] must be a positive whole number";
                sizes[l] = (int)sizesToken[l];
            }
            if (sizes.Length - 2 > MaxHiddenLayers) return $"too_big: {where}.sizes: at most {MaxHiddenLayers} hidden layers on the device (got {sizes.Length - 2})";
            for (int l = 1; l < sizes.Length - 1; l++)
                if (sizes[l] > MaxNeurons) return $"too_big: {where}.sizes[{l}] is {sizes[l]}: at most {MaxNeurons} neurons per hidden layer on the device";
            if (sizes[0] != inputs) return $"shape_mismatch: {where}.sizes[0] is {sizes[0]} but there are {inputs} inputs";
            if (sizes[sizes.Length - 1] != outputs) return $"shape_mismatch: {where}.sizes[{sizes.Length - 1}] is {sizes[sizes.Length - 1]} but there are {outputs} outputs";

            if (!TryActivation((string)n["activation"] ?? "tanh", out var activation)) return $"bad_value: {where}.activation \"{(string)n["activation"]}\" must be tanh, relu, sigmoid or linear";
            string outputActivation = (string)n["output_activation"] ?? "tanh";
            if (outputActivation != "tanh") return $"bad_value: {where}.output_activation \"{outputActivation}\": only tanh runs on the device for now";

            if (!(n["weights"] is JArray weights) || weights.Count != sizes.Length - 1) return $"shape_mismatch: {where}.weights needs {sizes.Length - 1} layers";
            if (!(n["biases"] is JArray biases) || biases.Count != sizes.Length - 1) return $"shape_mismatch: {where}.biases needs {sizes.Length - 1} layers";
            var hidden = new int[sizes.Length - 2];
            Array.Copy(sizes, 1, hidden, 0, hidden.Length);
            var built = new NNNetwork(sizes[0], hidden, sizes[sizes.Length - 1], activation, 0);
            for (int l = 1; l < sizes.Length; l++)
            {
                if (!(weights[l - 1] is JArray rows) || rows.Count != sizes[l])
                    return $"shape_mismatch: {where}.weights[{l - 1}]: {sizes[l]} rows of {sizes[l - 1]} expected, got {(weights[l - 1] as JArray)?.Count ?? 0} rows";
                if (!(biases[l - 1] is JArray bs) || bs.Count != sizes[l])
                    return $"shape_mismatch: {where}.biases[{l - 1}]: {sizes[l]} values expected, got {(biases[l - 1] as JArray)?.Count ?? 0}";
                for (int j = 0; j < sizes[l]; j++)
                {
                    if (!(rows[j] is JArray row) || row.Count != sizes[l - 1])
                        return $"shape_mismatch: {where}.weights[{l - 1}][{j}]: {sizes[l - 1]} values expected, got {(rows[j] as JArray)?.Count ?? 0}";
                    for (int k = 0; k < sizes[l - 1]; k++)
                        if (!Finite(row[k], out built.weights[l][j][k])) return $"bad_value: {where}.weights[{l - 1}][{j}][{k}] is not a finite number";
                    if (!Finite(bs[j], out built.biases[l][j])) return $"bad_value: {where}.biases[{l - 1}][{j}] is not a finite number";
                }
            }
            net = built;
            return null;
        }

        /// <summary>The <c>net</c> object for <paramref name="net"/> (weights rounded to 5 decimals, as plain numbers).</summary>
        public static JObject Write(NNNetwork net)
        {
            var weights = new JArray(); var biases = new JArray();
            for (int l = 1; l < net.Layers; l++)
            {
                var rows = new JArray();
                for (int j = 0; j < net.sizes[l]; j++)
                {
                    var row = new JArray();
                    for (int k = 0; k < net.sizes[l - 1]; k++) row.Add(Math.Round(net.weights[l][j][k], 5));
                    rows.Add(row);
                }
                weights.Add(rows);
                var b = new JArray();
                for (int j = 0; j < net.sizes[l]; j++) b.Add(Math.Round(net.biases[l][j], 5));
                biases.Add(b);
            }
            return new JObject
            {
                ["sizes"] = new JArray(net.sizes),
                ["activation"] = net.activation.ToString().ToLowerInvariant(),
                ["output_activation"] = "tanh",
                ["weights"] = weights,
                ["biases"] = biases,
            };
        }

        public static bool Finite(JToken token, out float value)
        {
            value = 0;
            if (!ControllerFile.IsNumber(token)) return false;
            value = (float)token;
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public static bool TryActivation(string name, out NNActivation activation)
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
