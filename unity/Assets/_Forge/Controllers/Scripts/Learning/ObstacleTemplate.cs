using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Forge.Controllers
{
    /// <summary>
    /// The bundled on-device ML template (PROJECT_CONTEXT 15.5 and 16.8, ML A Nov 14): "train a model, then plug it into
    /// a Thing", with no network.
    /// 1. <see cref="ObstacleData"/> makes a labelled dataset from the truck's own sensors.
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

        public readonly ObstacleData Data;
        public readonly ObstacleClassifier Classifier;

        public ObstacleTemplate(int samples = 3000, int dataSeed = 7, ObstacleClassifier.Options options = null)
        {
            Data = ObstacleData.Generate(samples, dataSeed);
            Classifier = new ObstacleClassifier(Data, options);
        }

        public bool Done => Classifier.Done;
        /// <summary>Trains within <paramref name="budgetMs"/> ms (call from Update). Returns <see cref="Done"/>.</summary>
        public bool Step(double budgetMs) => Classifier.Step(budgetMs);

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

        /// <summary>Writes the model file and the learned-brake controller into <paramref name="folder"/>. Returns their paths.</summary>
        public (string model, string controller) Save(string folder, JObject controller)
        {
            Directory.CreateDirectory(folder);
            string modelPath = Path.Combine(folder, ModelName + ".json");
            ControllerFileWatcher.WriteAtomic(modelPath, Classifier.ToModelJson(ModelName).ToString());
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
