using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Forge.Controllers
{
    /// <summary>
    /// The bundled on-device ML template (PROJECT_CONTEXT 15.5 and 16.8, ML A Nov 14): "train a model, then plug it into
    /// a Thing", with no network.
    /// 1. <see cref="ObstacleData"/> makes a labelled dataset from the truck's own sensors (a few ms a frame with
    ///    <see cref="Begin"/>).
    /// 2. <see cref="ObstacleClassifier"/> trains a small net on it, a few ms a frame.
    /// 3. <see cref="WithLearnedBrake"/> puts the trained net into a graph controller as a <c>model</c> node, so the truck
    ///    brakes when the net says there's an obstacle ahead.
    /// The Python ML templates (CIFAR-10, text classification) need the laptop hub to train: they don't run on a Quest.
    /// </summary>
    public sealed class ObstacleTemplate
    {
        public const string ModelName = "obstacle_ahead";
        /// <summary>The brake the learned node replaces in a graph (the mock <c>truck_cruise</c> calls its hand-written one this).</summary>
        public const string BrakeNode = "brake";
        public const float BrakeThrottle = -1f;
        /// <summary>Added to the base controller's name: <c>truck_cruise</c> becomes <c>truck_cruise_learned_brake</c>.</summary>
        public const string LearnedSuffix = "_learned_brake";

        /// <summary>The dataset (null while <see cref="MakingData"/>).</summary>
        public ObstacleData Data { get; private set; }
        /// <summary>The classifier (null while <see cref="MakingData"/>).</summary>
        public ObstacleClassifier Classifier { get; private set; }
        public readonly ObstacleClassifier.Options Settings;
        /// <summary>Making the dataset over frames (<see cref="Begin"/>); null when it was made at once.</summary>
        public readonly ObstacleData.Maker Maker;

        /// <summary>Makes the dataset and the classifier now; <see cref="Step"/> then trains. For checks and tools off the frame loop.</summary>
        public ObstacleTemplate(int samples = 3000, int dataSeed = 7, ObstacleClassifier.Options options = null)
        {
            Settings = options ?? new ObstacleClassifier.Options();
            Data = ObstacleData.Generate(samples, dataSeed);
            Classifier = new ObstacleClassifier(Data, Settings);
        }

        ObstacleTemplate(ObstacleData.Maker maker, ObstacleClassifier.Options options)
        {
            Settings = options ?? new ObstacleClassifier.Options();
            Maker = maker;
        }

        /// <summary>
        /// A template that makes its dataset inside <see cref="Step"/>, a slice a frame, so nothing takes a whole frame
        /// on the headset: the dataset first, then the classifier's setup in the next frame, then training.
        /// </summary>
        public static ObstacleTemplate Begin(int samples = 3000, int dataSeed = 7, ObstacleClassifier.Options options = null) =>
            new ObstacleTemplate(new ObstacleData.Maker(samples, dataSeed), options);

        public bool MakingData => Classifier == null;
        public int Samples => Data?.Count ?? Maker.Samples;
        /// <summary>Samples made so far.</summary>
        public int Made => Data?.Count ?? Maker.Made;
        public bool Done => Classifier != null && Classifier.Done;
        /// <summary>Time spent in <see cref="Step"/>: making the data, setting up the classifier and training.</summary>
        public double ComputeSeconds => (Maker?.ComputeSeconds ?? 0) + setupSeconds + (Classifier?.ComputeSeconds ?? 0);
        double setupSeconds;

        /// <summary>
        /// One frame's work within <paramref name="budgetMs"/> ms (call from Update): makes data, or sets up the
        /// classifier and trains with what's left, or trains. Returns <see cref="Done"/>.
        /// </summary>
        public bool Step(double budgetMs)
        {
            if (Classifier != null) return Classifier.Step(budgetMs);
            if (!Maker.Done)
            {
                Maker.Step(budgetMs);
                return false;
            }
            // Copying and standardising the rows takes under a millisecond, so training gets the rest of this frame.
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Data = Maker.Result;
            Classifier = new ObstacleClassifier(Data, Settings);
            setupSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) / (double)System.Diagnostics.Stopwatch.Frequency;
            return Classifier.Step(Math.Max(0.0, budgetMs - setupSeconds * 1000.0));
        }

        /// <summary>
        /// A copy of a graph controller with the trained net as its brake: the node called <see cref="BrakeNode"/> is
        /// replaced (or the model node is added last), and the name gets <c>_learned_brake</c>. The new node is last in the
        /// list or where the old brake was, so it still overrides the cruise throttle (contract section 3.3). A controller that
        /// already has a learned brake keeps its name.
        /// </summary>
        public JObject WithLearnedBrake(JObject controller)
        {
            if (controller == null || (string)controller["kind"] != "graph") throw new ArgumentException("the learned brake goes into a graph controller");
            var copy = (JObject)controller.DeepClone();
            string name = (string)controller["name"];
            // Retraining on a controller that already has a learned brake replaces it; the name doesn't grow.
            copy["name"] = name.EndsWith(LearnedSuffix, StringComparison.Ordinal) ? name : name + LearnedSuffix;
            var brake = Classifier.ToGraphNode(BrakeNode, new JObject { ["drive.throttle"] = BrakeThrottle });
            var nodes = copy["nodes"] as JArray ?? new JArray();
            int at = -1;
            for (int i = 0; i < nodes.Count; i++) if ((string)nodes[i]["id"] == BrakeNode) at = i;
            if (at >= 0) nodes[at] = brake; else nodes.Add(brake);
            copy["nodes"] = nodes;
            copy["notes"] = "The ML template's learned brake: a net trained on the device says when there's an obstacle ahead.";
            return copy;
        }

        /// <summary>The <c>albie.model/1</c> file; pass the run's training block so it isn't scored again.</summary>
        public JObject ModelJson(JObject training = null) => Classifier.ToModelJson(ModelName, training: training);

        /// <summary>Writes the model file and the learned-brake controller into <paramref name="folder"/>. Returns their paths.</summary>
        public (string model, string controller) Save(string folder, JObject controller)
        {
            Directory.CreateDirectory(folder);
            string modelPath = Path.Combine(folder, ModelName + ".json");
            ControllerFileWatcher.WriteAtomic(modelPath, ModelJson().ToString());
            string controllerPath = null;
            if (controller != null)
            {
                var learned = WithLearnedBrake(controller);
                controllerPath = Path.Combine(folder, (string)learned["name"] + ".json");
                ControllerFileWatcher.WriteAtomic(controllerPath, learned.ToString());
            }
            return (modelPath, controllerPath);
        }
    }
}
