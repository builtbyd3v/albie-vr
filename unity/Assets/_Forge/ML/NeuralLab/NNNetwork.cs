using System;

namespace Forge.ML
{
    public enum NNActivation { Tanh, ReLU, Sigmoid, Linear }

    /// <summary>
    /// A small fully connected network in the TensorFlow Playground mould: input features (x, y, x², ...) → hidden
    /// layers → tanh outputs in [-1, 1], squared-error loss, mini-batch gradient descent. Any hidden neuron can be
    /// switched off: it outputs 0 and learns nothing until switched back on, so its effect on the boundary shows at once.
    /// The Neural Net Lab uses one output. A learned controller (C-19, docs/CONTROLLER_CONTRACT.md) uses several, e.g.
    /// steer and throttle; its weights use this same [layer][to][from] layout.
    /// No allocations after construction: Forward / Accumulate / Apply reuse the same buffers (runs every frame on Quest).
    /// </summary>
    public sealed class NNNetwork
    {
        public readonly int[] sizes;            // layer sizes: inputs, hidden..., outputs (1 for the Lab)
        public readonly float[][][] weights;    // [layer][to][from], layer 1..L
        public readonly float[][] biases;       // [layer][neuron]
        public readonly bool[][] disabled;      // [layer][neuron], hidden layers only
        public NNActivation activation;
        readonly float[][] outputs, totals, delta;
        readonly float[][][] gradW;
        readonly float[][] gradB;
        int accumulated;

        public int Layers => sizes.Length;
        public int OutputCount => sizes[sizes.Length - 1];
        /// <summary>The first output (the Lab's only one).</summary>
        public float Output => outputs[sizes.Length - 1][0];
        /// <summary>Output <paramref name="i"/> from the last <see cref="Forward(float[])"/>.</summary>
        public float OutputAt(int i) => outputs[sizes.Length - 1][i];

        /// <summary>One output: the Neural Net Lab's network.</summary>
        public NNNetwork(int inputs, int[] hidden, NNActivation activation, int seed)
            : this(inputs, hidden, 1, activation, seed) { }

        /// <summary><paramref name="outputCount"/> tanh outputs, e.g. 2 for steer and throttle.</summary>
        public NNNetwork(int inputs, int[] hidden, int outputCount, NNActivation activation, int seed)
        {
            if (outputCount < 1) throw new ArgumentOutOfRangeException(nameof(outputCount), "a network needs at least one output");
            sizes = new int[hidden.Length + 2];
            sizes[0] = inputs; for (int i = 0; i < hidden.Length; i++) sizes[i + 1] = hidden[i]; sizes[sizes.Length - 1] = outputCount;
            this.activation = activation;
            int n = sizes.Length;
            weights = new float[n][][]; biases = new float[n][]; disabled = new bool[n][];
            outputs = new float[n][]; totals = new float[n][]; delta = new float[n][];
            gradW = new float[n][][]; gradB = new float[n][];
            var rng = new Random(seed);
            for (int l = 0; l < n; l++)
            {
                outputs[l] = new float[sizes[l]]; totals[l] = new float[sizes[l]]; delta[l] = new float[sizes[l]];
                disabled[l] = new bool[sizes[l]];
                if (l == 0) continue;
                weights[l] = new float[sizes[l]][]; gradW[l] = new float[sizes[l]][];
                biases[l] = new float[sizes[l]]; gradB[l] = new float[sizes[l]];
                for (int j = 0; j < sizes[l]; j++)
                {
                    weights[l][j] = new float[sizes[l - 1]]; gradW[l][j] = new float[sizes[l - 1]];
                    for (int k = 0; k < sizes[l - 1]; k++) weights[l][j][k] = (float)rng.NextDouble() - 0.5f;  // Playground: U(-0.5, 0.5)
                    biases[l][j] = 0.1f;
                }
            }
        }

        public float NeuronOutput(int layer, int neuron) => outputs[layer][neuron];

        /// <summary>Forward pass over already computed features; returns the first output in [-1, 1]. Read the others
        /// with <see cref="OutputAt"/>, or use <see cref="Forward(float[], float[])"/>.</summary>
        public float Forward(float[] features)
        {
            var input = outputs[0];
            for (int i = 0; i < input.Length; i++) input[i] = features[i];
            int last = sizes.Length - 1;
            for (int l = 1; l <= last; l++)
            {
                float[] prev = outputs[l - 1], outL = outputs[l], totL = totals[l];
                bool[] off = disabled[l];
                for (int j = 0; j < outL.Length; j++)
                {
                    if (off[j]) { totL[j] = 0; outL[j] = 0; continue; }
                    float sum = biases[l][j]; float[] w = weights[l][j];
                    for (int k = 0; k < prev.Length; k++) sum += w[k] * prev[k];
                    totL[j] = sum;
                    outL[j] = l == last ? (float)Math.Tanh(sum) : Activate(sum);
                }
            }
            return outputs[last][0];
        }

        /// <summary>Forward pass that copies every output into <paramref name="result"/> (length <see cref="OutputCount"/>).</summary>
        public void Forward(float[] features, float[] result)
        {
            Forward(features);
            var o = outputs[sizes.Length - 1];
            for (int j = 0; j < o.Length; j++) result[j] = o[j];
        }

        /// <summary>Forward + backward for one point of a one-output network; gradients accumulate until <see cref="Apply"/>.</summary>
        public void Accumulate(float[] features, float label)
        {
            if (OutputCount != 1) throw new InvalidOperationException($"this network has {OutputCount} outputs: pass one label per output");
            float o = Forward(features);
            delta[sizes.Length - 1][0] = (o - label) * (1f - o * o);  // d(½(o-y)²)/d total through tanh
            Backward();
        }

        /// <summary>Forward + backward for one point, one label per output (loss ½Σ(o-y)²); gradients accumulate until
        /// <see cref="Apply"/>. Returns that point's loss, for training curves.</summary>
        public float Accumulate(float[] features, float[] labels)
        {
            Forward(features);
            float[] o = outputs[sizes.Length - 1], d = delta[sizes.Length - 1];
            float loss = 0;
            for (int j = 0; j < o.Length; j++)
            {
                float err = o[j] - labels[j];
                loss += 0.5f * err * err;
                d[j] = err * (1f - o[j] * o[j]);
            }
            Backward();
            return loss;
        }

        /// <summary>½Σ(o-y)² of the last <see cref="Forward(float[])"/> against <paramref name="labels"/>, with no training.</summary>
        public float Loss(float[] labels)
        {
            float[] o = outputs[sizes.Length - 1];
            float loss = 0;
            for (int j = 0; j < o.Length; j++) { float err = o[j] - labels[j]; loss += 0.5f * err * err; }
            return loss;
        }

        // Backpropagates delta[last] (set by the caller) and adds this point's gradients.
        void Backward()
        {
            int last = sizes.Length - 1;
            for (int l = last; l >= 1; l--)
            {
                float[] prev = outputs[l - 1], dl = delta[l];
                for (int j = 0; j < dl.Length; j++)
                {
                    if (disabled[l][j]) continue;
                    gradB[l][j] += dl[j];
                    float[] g = gradW[l][j];
                    for (int k = 0; k < prev.Length; k++) g[k] += dl[j] * prev[k];
                }
                if (l == 1) break;
                float[] dp = delta[l - 1], tp = totals[l - 1];
                for (int k = 0; k < dp.Length; k++)
                {
                    if (disabled[l - 1][k]) { dp[k] = 0; continue; }
                    float sum = 0;
                    for (int j = 0; j < dl.Length; j++) if (!disabled[l][j]) sum += dl[j] * weights[l][j][k];
                    dp[k] = sum * ActivateDer(tp[k]);
                }
            }
            accumulated++;
        }

        public void Apply(float learningRate)
        {
            if (accumulated == 0) return;
            float scale = learningRate / accumulated;
            for (int l = 1; l < sizes.Length; l++)
                for (int j = 0; j < sizes[l]; j++)
                {
                    float[] w = weights[l][j], g = gradW[l][j];
                    if (!disabled[l][j])
                    {
                        for (int k = 0; k < w.Length; k++) w[k] -= scale * g[k];
                        biases[l][j] -= scale * gradB[l][j];
                    }
                    Array.Clear(g, 0, g.Length); gradB[l][j] = 0;
                }
            accumulated = 0;
        }

        float Activate(float x)
        {
            switch (activation)
            {
                case NNActivation.ReLU: return x > 0 ? x : 0;
                case NNActivation.Sigmoid: return 1f / (1f + (float)Math.Exp(-x));
                case NNActivation.Linear: return x;
                default: return (float)Math.Tanh(x);
            }
        }
        float ActivateDer(float x)
        {
            switch (activation)
            {
                case NNActivation.ReLU: return x > 0 ? 1 : 0;
                case NNActivation.Sigmoid: { float s = 1f / (1f + (float)Math.Exp(-x)); return s * (1 - s); }
                case NNActivation.Linear: return 1;
                default: { float t = (float)Math.Tanh(x); return 1 - t * t; }
            }
        }
    }
}
