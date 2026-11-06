using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// One run of the on-device ML template (<see cref="ObstacleTemplate"/>), shared by the sandbox's M key and Albie's
    /// tools (<see cref="TemplateTools"/>), so both go down the same path:
    /// 1. <see cref="Start"/> makes the dataset and starts training. A hidden pump trains it a few ms a frame in Play
    ///    (<see cref="BudgetMs"/>); edit-mode checks call <see cref="Pump"/> themselves.
    /// 2. When it's done, it saves <c>obstacle_ahead.json</c> and the learned-brake controller to the controllers
    ///    folder, reports the run to the lab notebook (<see cref="TrainingLog"/>, C-17 section 3) and raises
    ///    <see cref="Finished"/>.
    /// 3. <see cref="AttachBrake"/> puts the learned brake on a Thing, and <see cref="DetachBrake"/> gives it back the
    ///    controller it had before.
    /// One run at a time. No allocations per frame while training (the classifier's own rule).
    /// </summary>
    public static class TemplateRun
    {
        public enum Phase { Idle, Training, Done, Failed }

        /// <summary>Training time per frame. 72 fps leaves about 14 ms a frame.</summary>
        public static double BudgetMs = 4.0;
        /// <summary>The controller the learned brake goes into when the Thing isn't running a graph with a brake.</summary>
        public const string DefaultBase = "Mocks/controllers/truck_cruise";

        public static Phase State { get; private set; }
        public static ObstacleTemplate Current { get; private set; }
        public static bool Training => State == Phase.Training;
        /// <summary>The base controller with the trained net as its brake (null until a run is done).</summary>
        public static JObject LearnedBrake { get; private set; }
        public static JObject TrainingBlock { get; private set; }
        public static string ModelPath { get; private set; }
        public static string ControllerPath { get; private set; }
        public static string SaveError { get; private set; }
        public static string Error { get; private set; }
        public static float WallSeconds { get; private set; }
        /// <summary>Raised once per run when it's done or has failed.</summary>
        public static event Action Finished;

        static string folder;
        static float started;
        static readonly List<Action> waiting = new List<Action>();
        // What each Thing ran before the learned brake, so turning it off gives that back.
        static readonly Dictionary<ControllerHost, ControllerFile> before = new Dictionary<ControllerHost, ControllerFile>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Reset(); Finished = null; }

        /// <summary>Forgets the run and what the brake replaced (Play start, and the checks). Keeps <see cref="Finished"/> subscribers.</summary>
        public static void Reset()
        {
            State = Phase.Idle; Current = null; LearnedBrake = null; TrainingBlock = null; pendingBase = null;
            ModelPath = ControllerPath = SaveError = Error = null; WallSeconds = 0f;
            waiting.Clear(); before.Clear(); BudgetMs = 4.0; FolderOverride = null;
        }

        /// <summary>Checks set this so a run doesn't write into the real controllers folder.</summary>
        public static string FolderOverride;

        public static string SaveFolder => FolderOverride ?? Path.Combine(Application.persistentDataPath, "controllers");

        /// <summary>
        /// Makes the dataset and starts training. <paramref name="baseController"/> is the graph the learned brake goes
        /// into (default: the bundled <c>truck_cruise</c>); <paramref name="saveFolder"/> is where the files go (default
        /// <see cref="SaveFolder"/>). Returns an error, or null. A run already training is left alone (null).
        /// </summary>
        public static string Start(JObject baseController = null, string saveFolder = null)
        {
            if (Training) return null;
            baseController = baseController ?? LoadDefaultBase();
            if (baseController == null) return Error = $"no_controller: missing Resources/{DefaultBase}.json";
            if ((string)baseController["kind"] != "graph") return Error = "bad_value: the learned brake goes into a graph controller";
            pendingBase = (JObject)baseController.DeepClone();
            folder = saveFolder ?? SaveFolder;
            Error = SaveError = ModelPath = ControllerPath = null;
            LearnedBrake = null; TrainingBlock = null;
            try { Current = new ObstacleTemplate(); }
            catch (Exception e) { Fail(e.Message); return Error; }
            started = Time.realtimeSinceStartup;
            State = Phase.Training;
            if (Application.isPlaying) TemplatePump.Ensure();
            return null;
        }

        static JObject pendingBase;

        public static JObject LoadDefaultBase()
        {
            var asset = Resources.Load<TextAsset>(DefaultBase);
            return asset == null ? null : JObject.Parse(asset.text);
        }

        /// <summary>The graph a Thing's learned brake should go into: its own controller if that's a graph with a brake, else the default.</summary>
        public static JObject BaseFor(ControllerHost host)
        {
            var raw = host?.File?.Raw;
            if (raw != null && (string)raw["kind"] == "graph" && raw["nodes"] is JArray nodes)
                foreach (var n in nodes) if ((string)n["id"] == ObstacleTemplate.BrakeNode) return raw;
            return LoadDefaultBase();
        }

        /// <summary>Trains for one frame's budget; finishes the run when the classifier is done. Returns true when it isn't training any more.</summary>
        public static bool Pump(double budgetMs = -1)
        {
            if (!Training) return true;
            try
            {
                if (!Current.Step(budgetMs < 0 ? BudgetMs : budgetMs)) return false;
                Finish();
            }
            catch (Exception e) { Debug.LogException(e); Fail(e.Message); }
            return true;
        }

        /// <summary>Calls <paramref name="done"/> when the current run ends (now if it isn't training).</summary>
        public static void WhenDone(Action done)
        {
            if (!Training) { done(); return; }
            waiting.Add(done);
        }

        static void Finish()
        {
            WallSeconds = Time.realtimeSinceStartup - started;
            TrainingBlock = Current.Classifier.TrainingBlock(ObstacleTemplate.ModelName);
            TrainingBlock["wall_seconds"] = Math.Round(WallSeconds, 2);
            LearnedBrake = Current.WithLearnedBrake(pendingBase);
            try
            {
                var paths = Current.Save(folder, pendingBase);
                ModelPath = paths.model; ControllerPath = paths.controller;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { SaveError = e.Message; }
            State = Phase.Done;
            // One notebook entry per run (C-17 section 3), on the controller the net was trained for.
            TrainingLog.Report((string)LearnedBrake["name"], (int?)LearnedBrake["version"] ?? 1, (JObject)TrainingBlock.DeepClone());
            Raise();
        }

        static void Fail(string message)
        {
            Error = "training_failed: " + message;
            State = Phase.Failed;
            Raise();
        }

        static void Raise()
        {
            var callbacks = waiting.ToArray();
            waiting.Clear();
            foreach (var c in callbacks)
            {
                try { c(); } catch (Exception e) { Debug.LogException(e); }
            }
            Finished?.Invoke();
        }

        /// <summary>
        /// Puts the learned brake on <paramref name="host"/>, remembering what it ran before. Returns an error, or null.
        /// </summary>
        public static string AttachBrake(ControllerHost host)
        {
            if (host == null) return "no_thing: no Thing is running a controller";
            if (LearnedBrake == null) return "no_model: train the ML template first";
            var file = ControllerFile.Parse(LearnedBrake.ToString(), out string error);
            if (error != null) return error;
            var previous = host.File;
            error = host.Attach(file);
            if (error != null) return error;
            if (previous != null && !IsLearnedBrake(previous)) before[host] = previous;
            return null;
        }

        /// <summary>Gives <paramref name="host"/> back the controller it had before the learned brake. Returns an error, or null.</summary>
        public static string DetachBrake(ControllerHost host)
        {
            if (host == null) return "no_thing: no Thing is running a controller";
            if (!BrakeOn(host)) return "not_on: the learned brake isn't on";
            if (!before.TryGetValue(host, out var previous))
            {
                var fallback = ControllerFile.Parse(LoadDefaultBase()?.ToString() ?? "", out string parseError);
                if (fallback == null) return parseError ?? "no_controller: nothing to go back to";
                previous = fallback;
            }
            string error = host.Attach(previous);
            if (error == null) before.Remove(host);
            return error;
        }

        public static bool BrakeOn(ControllerHost host) => host?.File != null && IsLearnedBrake(host.File);

        /// <summary>The name of what the learned brake replaced on <paramref name="host"/> (null when it's off).</summary>
        public static string Replaced(ControllerHost host) => host != null && before.TryGetValue(host, out var f) ? f.Name : null;

        static bool IsLearnedBrake(ControllerFile file) => file.Name != null && file.Name.EndsWith(ObstacleTemplate.LearnedSuffix, StringComparison.Ordinal);
    }

    /// <summary>Trains the template run a few ms a frame in Play. Made on demand and kept across scenes.</summary>
    sealed class TemplatePump : MonoBehaviour
    {
        static TemplatePump instance;

        public static void Ensure()
        {
            if (instance != null) return;
            // Not HideAndDontSave: that would outlive Play mode in the Editor.
            var go = new GameObject("ML template (training)");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<TemplatePump>();
        }

        void Update() => TemplateRun.Pump();

        void OnDestroy() { if (instance == this) instance = null; }
    }
}
