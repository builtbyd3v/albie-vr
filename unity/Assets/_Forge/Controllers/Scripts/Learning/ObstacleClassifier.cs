using System;
using System.Diagnostics;
using Forge.ML;
using Newtonsoft.Json.Linq;

namespace Forge.Controllers
{
    /// <summary>
    /// Trains the ML template's "obstacle ahead" classifier on the device with the Neural Net Lab's <see cref="NNNetwork"/>
    /// (PROJECT_CONTEXT 15.5: "train a model, then plug it into a Thing"). One tanh output, trained toward +1 (obstacle)
    /// and -1 (clear); its probability is (y + 1) / 2.
    ///
    /// Same shape as <see cref="BehaviourCloner"/>: inputs standardised with the training rows' mean and std, every
    /// <see cref="Options.ValidationEvery"/>-th row held out, shuffled mini-batch SGD, and <see cref="Step"/> trains until
    /// its time budget is used so it can run in Update at 72 fps. No allocations after the constructor.
    ///
    /// <see cref="Evaluate"/> scores the validation rows and the baseline: the best single threshold on the front ray
    /// (<c>ray_front.d3</c>), found on the training rows. The baseline is what a hand-written rule would do, so the
    /// template shows whether learning bought anything. <see cref="ToModelJson"/> writes the <c>albie.model/1</c> file, and
    /// <see cref="ToGraphNode"/> the graph <c>model</c> node (contract section 3.5, proposed) that brakes the Thing.
    /// </summary>
    public sealed class ObstacleClassifier
    {
        public sealed class Options
        {
            public int[] Hidden = { 8 };
            public NNActivation Activation = NNActivation.Tanh;
            public int Epochs = 60;
            public int BatchSize = 16;
            public float LearningRate = 0.2f;
            public int ValidationEvery = 5;
            public int Seed = 1;
        }

        public sealed class Score
        {
            public int TruePositive, FalsePositive, TrueNegative, FalseNegative;
            public int Count => TruePositive + FalsePositive + TrueNegative + FalseNegative;
            public float Accuracy => Count == 0 ? float.NaN : (TruePositive + TrueNegative) / (float)Count;
            public float Precision => TruePositive + FalsePositive == 0 ? float.NaN : TruePositive / (float)(TruePositive + FalsePositive);
            public float Recall => TruePositive + FalseNegative == 0 ? float.NaN : TruePositive / (float)(TruePositive + FalseNegative);
            public void Add(bool predicted, bool actual)
            {
                if (predicted && actual) TruePositive++; else if (predicted) FalsePositive++;
                else if (actual) FalseNegative++; else TrueNegative++;
            }
        }

        public const string FrontRay = "ray_front.d3";

        public readonly Options Settings;
        public readonly ObstacleData Data;
        public readonly NNNetwork Net;
        public readonly float[] Mean, Std;
        public int TrainCount => train.Length;
        public int ValidationCount => validation.Length;
        public int Epoch { get; private set; }
        public bool Done => Epoch >= Settings.Epochs;
        public float TrainLoss { get; private set; } = float.NaN;
        public float ValidationLoss { get; private set; } = float.NaN;
        public double ComputeSeconds => compute.Elapsed.TotalSeconds;
        public int Steps { get; private set; }

        readonly float[][] xs;
        readonly float[][] ys;
        readonly int[] train, validation;
        readonly Random rng;
        readonly Stopwatch compute = new Stopwatch(), slice = new Stopwatch();
        readonly float[] scratch;
        int cursor;
        double epochLoss;

        public ObstacleClassifier(ObstacleData data, Options options = null)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            Settings = options ?? new Options();
            int features = ObstacleData.Features.Length, n = data.Count;
            xs = new float[n][]; ys = new float[n][];
            for (int i = 0; i < n; i++) { xs[i] = (float[])data.X[i].Clone(); ys[i] = new[] { data.Y[i] ? 1f : -1f }; }

            int k = Math.Max(2, Settings.ValidationEvery), valCount = n / k;
            train = new int[n - valCount]; validation = new int[valCount];
            for (int i = 0, t = 0, v = 0; i < n; i++)
                if (i % k == k - 1 && v < valCount) validation[v++] = i; else train[t++] = i;

            Mean = new float[features]; Std = new float[features];
            for (int f = 0; f < features; f++)
            {
                double sum = 0, sq = 0;
                foreach (int i in train) { sum += xs[i][f]; sq += xs[i][f] * (double)xs[i][f]; }
                double mean = sum / train.Length, std = Math.Sqrt(Math.Max(0, sq / train.Length - mean * mean));
                Mean[f] = (float)mean; Std[f] = std < 1e-6 ? 1f : (float)std;
            }
            foreach (var row in xs) for (int f = 0; f < features; f++) row[f] = (row[f] - Mean[f]) / Std[f];

            Net = new NNNetwork(features, Settings.Hidden, 1, Settings.Activation, Settings.Seed);
            scratch = new float[features];
            rng = new Random(Settings.Seed);
            Shuffle();
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
                for (int b = 0; b < batch && cursor < train.Length; b++, cursor++)
                    epochLoss += Net.Accumulate(xs[train[cursor]], ys[train[cursor]]);
                Net.Apply(Settings.LearningRate);
                if (cursor >= train.Length) EndEpoch();
                if (slice.Elapsed.TotalMilliseconds >= budgetMs) break;
            }
            compute.Stop();
            return Done;
        }

        public void TrainToEnd() { while (!Step(double.MaxValue)) { } }

        void EndEpoch()
        {
            TrainLoss = (float)(epochLoss / train.Length);
            double total = 0;
            foreach (int i in validation) { Net.Forward(xs[i]); total += Net.Loss(ys[i]); }
            ValidationLoss = validation.Length == 0 ? float.NaN : (float)(total / validation.Length);
            epochLoss = 0;
            cursor = 0;
            Epoch++;
            if (!Done) Shuffle();
        }

        /// <summary>P(obstacle) for raw (unstandardised) sensors, in <see cref="ObstacleData.Features"/> order. No allocations.</summary>
        public float Probability(float[] sensors)
        {
            for (int f = 0; f < scratch.Length; f++) scratch[f] = (sensors[f] - Mean[f]) / Std[f];
            return (Net.Forward(scratch) + 1f) * 0.5f;
        }

        /// <summary>The net on the validation rows at P ≥ 0.5, and the front-ray baseline tuned on the training rows.</summary>
        public void Evaluate(out Score model, out Score baseline, out float baselineThreshold)
        {
            int front = Array.IndexOf(ObstacleData.Features, FrontRay);
            // Baseline: "obstacle if the front ray is shorter than t", with t chosen for the best training accuracy.
            baselineThreshold = 0f;
            int best = -1;
            for (int step = 1; step <= 100; step++)
            {
                float t = step / 100f;
                int right = 0;
                foreach (int i in train) if ((Data.X[i][front] < t) == Data.Y[i]) right++;
                if (right > best) { best = right; baselineThreshold = t; }
            }
            model = new Score(); baseline = new Score();
            foreach (int i in validation)
            {
                Net.Forward(xs[i]);
                model.Add(Net.Output >= 0f, Data.Y[i]);
                baseline.Add(Data.X[i][front] < baselineThreshold, Data.Y[i]);
            }
        }

        /// <summary>
        /// Provenance for the model file and the notebook (C-17 <c>observed.training</c>). <c>task</c> is <c>binary</c>
        /// and <c>model</c> names the classifier, so the notebook words the entry as a classifier, not as laps.
        /// </summary>
        public JObject TrainingBlock(string modelName = "obstacle_ahead")
        {
            Evaluate(out var model, out var baseline, out float threshold);
            JToken R(float v, int digits = 4) => float.IsNaN(v) || float.IsInfinity(v) ? JValue.CreateNull() : new JValue(Math.Round(v, digits));
            return new JObject
            {
                ["task"] = "binary", ["model"] = modelName,
                ["data"] = new JObject
                {
                    ["source"] = "mock_truck_sim", ["samples"] = Data.Count, ["obstacle"] = Data.Positives,
                    ["label"] = $"hits a cone or wall within {ObstacleData.Horizon} s driving straight at its speed (at least {Data.PlanSpeed} m/s)",
                    ["seed"] = Data.Seed,
                },
                ["epochs"] = Epoch, ["seconds"] = Math.Round(ComputeSeconds, 3), ["frames"] = Steps,
                ["train_loss"] = R(TrainLoss, 5), ["val_loss"] = R(ValidationLoss, 5),
                ["val_accuracy"] = R(model.Accuracy), ["precision"] = R(model.Precision), ["recall"] = R(model.Recall),
                ["confusion"] = new JArray(new JArray(model.TrueNegative, model.FalsePositive), new JArray(model.FalseNegative, model.TruePositive)),
                ["baseline"] = new JObject { ["rule"] = $"{FrontRay} < {threshold:0.00}", ["val_accuracy"] = R(baseline.Accuracy), ["recall"] = R(baseline.Recall) },
                ["seed"] = Settings.Seed, ["hidden"] = JArray.FromObject(Settings.Hidden),
            };
        }

        JArray Inputs()
        {
            var inputs = new JArray();
            for (int f = 0; f < Mean.Length; f++)
                inputs.Add(new JObject { ["channel"] = ObstacleData.Features[f], ["mean"] = Math.Round(Mean[f], 5), ["std"] = Math.Round(Std[f], 5) });
            return inputs;
        }

        /// <summary>The <c>albie.model/1</c> file (contract section 3.5, proposed): the trained classifier on its own.</summary>
        public JObject ToModelJson(string name = "obstacle_ahead", int version = 1, string thing = "truck") => new JObject
        {
            ["schema"] = "albie.model/1",
            ["name"] = name,
            ["version"] = version,
            ["thing"] = thing,
            ["task"] = "binary",
            ["classes"] = new JArray(ObstacleData.Classes),
            ["inputs"] = Inputs(),
            ["net"] = NetFile.Write(Net),
            ["threshold"] = 0.5,
            ["training"] = TrainingBlock(name),
        };

        /// <summary>
        /// A graph <c>model</c> node that sets <paramref name="set"/> while P(obstacle) ≥ <paramref name="threshold"/>
        /// (a number or a $tunable name). Put it last in the graph so it overrides the cruise throttle (section 3.3).
        /// </summary>
        public JObject ToGraphNode(string id, JObject set, JToken threshold = null) => new JObject
        {
            ["id"] = id,
            ["type"] = "model",
            ["model"] = new JObject { ["name"] = "obstacle_ahead", ["inputs"] = Inputs(), ["net"] = NetFile.Write(Net) },
            ["threshold"] = threshold ?? 0.5,
            ["set"] = set,
        };
    }
}
