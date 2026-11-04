using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Forge.ML;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Forge.Controllers
{
    /// <summary>
    /// Behaviour cloning on the device (PROJECT_CONTEXT 15.4, 16.8): learns (sensors → steer, throttle) from recorded
    /// laps with the Neural Net Lab's <see cref="NNNetwork"/>.
    ///
    /// Data: the completed laps (optionally plus a left-right mirror of each, contract 2.3), standardised per input with
    /// the training rows' mean and std. Every <see cref="Options.ValidationEvery"/>-th row is held out for validation.
    ///
    /// Defaults come from closed-loop sweeps on the mock slalom (PROJECT_CONTEXT 16.8, Nov 8 decision): 8×8 tanh,
    /// 120 epochs, no mirroring. Mirroring hurts on a course with a handedness, like a slalom: it teaches "pass left"
    /// and "pass right" for the same view, and the net averages them into "straight into the cone". One training run
    /// finishes the mock slalom about 70-90% of the time; the best of 3 seeds about 99%. Validation loss can't tell
    /// the good seed apart (it picked worse than chance), so train a few and let the first arena run decide.
    /// Training: mini-batch SGD, shuffled each epoch. <see cref="Step"/> trains until its time budget is used, so it can
    /// run in Update without dropping frames (72 fps leaves about 14 ms a frame; the default budget is 4 ms). No
    /// allocations after the constructor. <see cref="ToControllerJson"/> writes the albie.controller/1 learned file.
    /// </summary>
    public sealed class BehaviourCloner
    {
        public sealed class Options
        {
            public int[] Hidden = { 8, 8 };
            public NNActivation Activation = NNActivation.Tanh;
            public int Epochs = 120;
            public int BatchSize = 16;
            public float LearningRate = 0.3f;
            /// <summary>Add a left-right mirror of every row. Off by default: see the class notes.</summary>
            public bool Mirror;
            public int ValidationEvery = 7;
            public int Seed = 1;
        }

        public readonly Options Settings;
        public readonly LapSet Laps;
        public readonly NNNetwork Net;
        public readonly float[] Mean, Std;
        public int TrainCount => train.Length;
        public int ValidationCount => validation.Length;
        public int Epoch { get; private set; }
        public bool Done => Epoch >= Settings.Epochs;
        /// <summary>Mean ½Σ(o−y)² over the last finished epoch's training rows (NaN before the first epoch).</summary>
        public float TrainLoss { get; private set; } = float.NaN;
        public float ValidationLoss { get; private set; } = float.NaN;
        /// <summary>Time spent inside <see cref="Step"/>, in seconds: the compute cost, not the wall-clock time.</summary>
        public double ComputeSeconds => compute.Elapsed.TotalSeconds;
        /// <summary>How many <see cref="Step"/> calls (frames) training has used so far.</summary>
        public int Steps { get; private set; }

        readonly float[][] xs, ys;
        readonly int[] train, validation;
        readonly Random rng;
        readonly Stopwatch compute = new Stopwatch(), slice = new Stopwatch();
        readonly float[] scratch;
        int cursor;
        double epochLoss;

        public BehaviourCloner(LapSet laps, Options options = null)
        {
            Laps = laps ?? throw new ArgumentNullException(nameof(laps));
            Settings = options ?? new Options();
            int sensors = laps.Sensors.Length, actuators = laps.Actuators.Length;

            var rowsX = new List<float[]>(); var rowsY = new List<float[]>();
            foreach (var lap in laps.Laps)
            {
                if (lap.Outcome != "completed") continue;
                for (int t = 0; t < lap.X.Length; t++) { rowsX.Add(lap.X[t]); rowsY.Add(lap.Y[t]); }
            }
            if (rowsX.Count == 0) throw new InvalidOperationException("no completed laps to learn from");

            int n = rowsX.Count, total = Settings.Mirror ? n * 2 : n;
            xs = new float[total][]; ys = new float[total][];
            for (int i = 0; i < n; i++) { xs[i] = (float[])rowsX[i].Clone(); ys[i] = (float[])rowsY[i].Clone(); }
            if (Settings.Mirror) MirrorInto(laps, n);

            // Split: every k-th original row and its mirror go to validation, so the two never straddle the split.
            int k = Math.Max(2, Settings.ValidationEvery);
            var trainList = new List<int>(); var valList = new List<int>();
            for (int i = 0; i < n; i++)
            {
                var into = i % k == k - 1 ? valList : trainList;
                into.Add(i);
                if (Settings.Mirror) into.Add(n + i);
            }
            train = trainList.ToArray(); validation = valList.ToArray();

            Mean = new float[sensors]; Std = new float[sensors];
            for (int f = 0; f < sensors; f++)
            {
                double sum = 0, sq = 0;
                foreach (int i in train) { sum += xs[i][f]; sq += xs[i][f] * (double)xs[i][f]; }
                double mean = sum / train.Length, std = Math.Sqrt(Math.Max(0, sq / train.Length - mean * mean));
                Mean[f] = (float)mean; Std[f] = std < 1e-6 ? 1f : (float)std;
            }
            foreach (var row in xs) for (int f = 0; f < sensors; f++) row[f] = (row[f] - Mean[f]) / Std[f];

            Net = new NNNetwork(sensors, Settings.Hidden, actuators, Settings.Activation, Settings.Seed);
            scratch = new float[sensors];
            rng = new Random(Settings.Seed);
            Shuffle();
        }

        void MirrorInto(LapSet laps, int n)
        {
            var swapA = new List<int>(); var swapB = new List<int>();
            foreach (var pair in laps.MirrorSwap)
            {
                int a = Array.IndexOf(laps.Sensors, pair[0]), b = Array.IndexOf(laps.Sensors, pair[1]);
                if (a < 0 || b < 0) throw new InvalidOperationException($"mirror pair {pair[0]}/{pair[1]} isn't in the sensors");
                swapA.Add(a); swapB.Add(b);
            }
            for (int i = 0; i < n; i++)
            {
                var x = (float[])xs[i].Clone(); var y = (float[])ys[i].Clone();
                for (int s = 0; s < swapA.Count; s++) { float tmp = x[swapA[s]]; x[swapA[s]] = x[swapB[s]]; x[swapB[s]] = tmp; }
                foreach (var name in laps.MirrorNegate)
                {
                    int si = Array.IndexOf(laps.Sensors, name), ai = Array.IndexOf(laps.Actuators, name);
                    if (si >= 0) x[si] = -x[si];
                    else if (ai >= 0) y[ai] = -y[ai];
                }
                xs[n + i] = x; ys[n + i] = y;
            }
        }

        void Shuffle()
        {
            for (int i = train.Length - 1; i > 0; i--) { int r = rng.Next(i + 1); int t = train[i]; train[i] = train[r]; train[r] = t; }
        }

        /// <summary>Trains until <paramref name="budgetMs"/> milliseconds are used or training is done. Returns <see cref="Done"/>.</summary>
        public bool Step(double budgetMs)
        {
            if (Done) return true;
            Steps++;
            compute.Start();
            slice.Restart();
            int batch = Math.Max(1, Settings.BatchSize);
            while (!Done)
            {
                // A whole batch at a time, so the time check stays cheap.
                for (int b = 0; b < batch && cursor < train.Length; b++, cursor++)
                    epochLoss += Net.Accumulate(xs[train[cursor]], ys[train[cursor]]);
                Net.Apply(Settings.LearningRate);
                if (cursor >= train.Length) EndEpoch();
                if (slice.Elapsed.TotalMilliseconds >= budgetMs) break;
            }
            compute.Stop();
            return Done;
        }

        /// <summary>Trains to the end in one call (tests, the Editor, a background thread).</summary>
        public void TrainToEnd() { while (!Step(double.MaxValue)) { } }

        void EndEpoch()
        {
            TrainLoss = (float)(epochLoss / train.Length);
            ValidationLoss = Loss(validation);
            epochLoss = 0;
            cursor = 0;
            Epoch++;
            if (!Done) Shuffle();
        }

        float Loss(int[] rows)
        {
            if (rows.Length == 0) return float.NaN;
            double total = 0;
            foreach (int i in rows) { Net.Forward(xs[i]); total += Net.Loss(ys[i]); }
            return (float)(total / rows.Length);
        }

        /// <summary>
        /// Steer and throttle for raw (unstandardised) sensors, into <paramref name="result"/>. For closed-loop checks
        /// before the learned controller exists; it uses a scratch buffer, so no allocations.
        /// </summary>
        public void Predict(float[] sensors, float[] result)
        {
            for (int f = 0; f < scratch.Length; f++) scratch[f] = (sensors[f] - Mean[f]) / Std[f];
            Net.Forward(scratch, result);
        }


        /// <summary>
        /// The controller file's <c>training</c> object (C-19 section 3.4): provenance the runner ignores and the notebook
        /// copies into <c>observed.training</c> (C-17). Losses are null until the first epoch ends.
        /// </summary>
        public JObject TrainingBlock()
        {
            JToken Loss(float v) => float.IsNaN(v) || float.IsInfinity(v) ? JValue.CreateNull() : new JValue(Math.Round(v, 5));
            return new JObject
            {
                ["samples"] = train.Length + validation.Length,
                ["mirrored"] = Settings.Mirror,
                ["epochs"] = Epoch,
                ["seconds"] = Math.Round(ComputeSeconds, 3),
                ["frames"] = Steps,
                ["train_loss"] = Loss(TrainLoss),
                ["val_loss"] = Loss(ValidationLoss),
                ["laps"] = Laps.CompletedLaps,
                ["seed"] = Settings.Seed,
                ["hidden"] = JArray.FromObject(Settings.Hidden),
            };
        }

        /// <summary>The <c>albie.controller/1</c> learned file (contract section 3.4) for the trained net.</summary>
        public string ToControllerJson(string name, int version, float maxSpeed, float maxSpeedMin = 0.2f, float maxSpeedMax = 4f)
        {
            var inv = CultureInfo.InvariantCulture;
            string N(float v) => float.IsNaN(v) || float.IsInfinity(v) ? "null" : v.ToString("0.#####", inv);
            var sb = new StringBuilder(16 * 1024);
            sb.Append("{\n");
            sb.Append("  \"schema\": \"albie.controller/1\",\n");
            sb.Append("  \"name\": ").Append(JsonConvert.SerializeObject(name)).Append(",\n");
            sb.Append("  \"kind\": \"learned\",\n");
            sb.Append("  \"version\": ").Append(version).Append(",\n");
            sb.Append("  \"thing\": ").Append(JsonConvert.SerializeObject(Laps.Thing)).Append(",\n");
            sb.Append("  \"inputs\": [\n");
            for (int f = 0; f < Mean.Length; f++)
                sb.Append("    {\"channel\": ").Append(JsonConvert.SerializeObject(Laps.Sensors[f]))
                  .Append(", \"mean\": ").Append(N(Mean[f])).Append(", \"std\": ").Append(N(Std[f])).Append(f + 1 < Mean.Length ? "},\n" : "}\n");
            sb.Append("  ],\n  \"net\": {\n    \"sizes\": [").Append(string.Join(", ", Net.sizes)).Append("],\n");
            sb.Append("    \"activation\": \"").Append(Settings.Activation.ToString().ToLowerInvariant()).Append("\",\n");
            sb.Append("    \"output_activation\": \"tanh\",\n    \"weights\": [\n");
            for (int l = 1; l < Net.Layers; l++)
            {
                sb.Append("      [\n");
                for (int j = 0; j < Net.sizes[l]; j++)
                {
                    sb.Append("        [");
                    for (int k = 0; k < Net.sizes[l - 1]; k++) { if (k > 0) sb.Append(", "); sb.Append(N(Net.weights[l][j][k])); }
                    sb.Append(j + 1 < Net.sizes[l] ? "],\n" : "]\n");
                }
                sb.Append(l + 1 < Net.Layers ? "      ],\n" : "      ]\n");
            }
            sb.Append("    ],\n    \"biases\": [\n");
            for (int l = 1; l < Net.Layers; l++)
            {
                sb.Append("      [");
                for (int j = 0; j < Net.sizes[l]; j++) { if (j > 0) sb.Append(", "); sb.Append(N(Net.biases[l][j])); }
                sb.Append(l + 1 < Net.Layers ? "],\n" : "]\n");
            }
            sb.Append("    ]\n  },\n  \"outputs\": [\n");
            for (int a = 0; a < Laps.Actuators.Length; a++)
                sb.Append("    {\"actuator\": ").Append(JsonConvert.SerializeObject(Laps.Actuators[a])).Append(", \"scale\": 1.0")
                  .Append(a + 1 < Laps.Actuators.Length ? "},\n" : "}\n");
            sb.Append("  ],\n");
            sb.Append("  \"tunables\": {\"max_speed\": {\"value\": ").Append(N(maxSpeed)).Append(", \"min\": ").Append(N(maxSpeedMin))
              .Append(", \"max\": ").Append(N(maxSpeedMax)).Append(", \"unit\": \"m/s\", \"label\": \"Top speed\"}},\n");
            sb.Append("  \"limits\": {\"speed\": \"$max_speed\"},\n");
            sb.Append("  \"training\": ").Append(TrainingBlock().ToString(Formatting.None)).Append("\n");
            sb.Append("}\n");
            return sb.ToString();
        }
    }
}
