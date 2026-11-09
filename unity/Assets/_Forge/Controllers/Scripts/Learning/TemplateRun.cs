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
    /// 1. <see cref="Start"/> starts a run. A hidden pump works on it a few ms a frame in Play (<see cref="BudgetMs"/>):
    ///    it makes the dataset, then trains. Edit-mode checks call <see cref="Pump"/> themselves.
    /// 2. When training is done, the next frames score the net, save <c>obstacle_ahead.json</c> and the learned-brake
    ///    controller to the controllers folder (a file a frame), then report the run to the lab notebook
    ///    (<see cref="TrainingLog"/>, C-17 section 3) and raise <see cref="Finished"/>.
    /// 3. <see cref="AttachBrake"/> puts the learned brake on a Thing, and <see cref="DetachBrake"/> gives it back the
    ///    controller it had before.
    /// One run at a time. No frame does all of it: on the headset a whole dataset or the whole finish in one frame was
    /// a dropped frame. No allocations per frame while making data or training (the classifier's own rule).
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
        /// <summary>A run is going: making data, training or saving. The results are set when it's <see cref="Phase.Done"/>.</summary>
        public static bool Training => State == Phase.Training;
        /// <summary>What a run is doing now: <c>making_data</c>, <c>training</c> or <c>saving</c> (null when no run is going).</summary>
        public static string Activity => !Training ? null : finishing > 0 ? "saving" : Current.MakingData ? "making_data" : "training";
        /// <summary>Time spent in <see cref="Pump"/> since the run started, in seconds (the timing kit reads it each frame).</summary>
        public static double PumpSeconds { get; private set; }
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
        // 0 while making data and training; then the finish's steps, one a frame (see FinishStep).
        static int finishing;
        static string modelText;
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
            finishing = 0; modelText = null; PumpSeconds = 0;
            waiting.Clear(); before.Clear(); BudgetMs = 4.0; FolderOverride = null;
        }

        /// <summary>Checks set this so a run doesn't write into the real controllers folder.</summary>
        public static string FolderOverride;

        public static string SaveFolder => FolderOverride ?? Path.Combine(Application.persistentDataPath, "controllers");

        /// <summary>
        /// Starts a run: the pump makes the dataset, then trains. <paramref name="baseController"/> is the graph the learned brake goes
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
            finishing = 0; modelText = null; PumpSeconds = 0; WallSeconds = 0f;
            try { Current = ObstacleTemplate.Begin(); }
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

        /// <summary>
        /// One frame's work within the budget: making data, training, or one step of the finish. Returns true when the
        /// run isn't going any more (done or failed).
        /// </summary>
        public static bool Pump(double budgetMs = -1)
        {
            if (!Training) return true;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                if (finishing == 0)
                {
                    if (Current.Step(budgetMs < 0 ? BudgetMs : budgetMs))
                    {
                        WallSeconds = Time.realtimeSinceStartup - started;
                        finishing = 1;
                    }
                }
                else FinishStep();
            }
            catch (Exception e) { Debug.LogException(e); Fail(e.Message); }
            PumpSeconds += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) / (double)System.Diagnostics.Stopwatch.Frequency;
            return !Training;
        }

        /// <summary>Calls <paramref name="done"/> when the current run ends (now if it isn't training).</summary>
        public static void WhenDone(Action done)
        {
            if (!Training) { done(); return; }
            waiting.Add(done);
        }

        // The finish, a step a frame after the last training frame. Each is about a millisecond in the Editor; together
        // they were 3 ms in one frame.
        static void FinishStep()
        {
            switch (finishing)
            {
                case 1:  // score the net on the held-out samples and the front-ray rule
                    TrainingBlock = Current.Classifier.TrainingBlock(ObstacleTemplate.ModelName);
                    TrainingBlock["wall_seconds"] = Math.Round(WallSeconds, 2);
                    break;
                case 2:  // the learned-brake controller and the model file's text
                    LearnedBrake = Current.WithLearnedBrake(pendingBase);
                    modelText = Current.ModelJson((JObject)TrainingBlock.DeepClone()).ToString();
                    break;
                case 3:
                    Save(() =>
                    {
                        Directory.CreateDirectory(folder);
                        string path = Path.Combine(folder, ObstacleTemplate.ModelName + ".json");
                        ControllerFileWatcher.WriteAtomic(path, modelText);
                        ModelPath = path;
                    });
                    modelText = null;
                    break;
                case 4:
                    if (SaveError == null) Save(() =>
                    {
                        string path = Path.Combine(folder, (string)LearnedBrake["name"] + ".json");
                        ControllerFileWatcher.WriteAtomic(path, LearnedBrake.ToString());
                        ControllerPath = path;
                    });
                    break;
                default:
                    finishing = 0;
                    State = Phase.Done;
                    // One notebook entry per run (C-17 section 3), on the controller the net was trained for.
                    TrainingLog.Report((string)LearnedBrake["name"], (int?)LearnedBrake["version"] ?? 1, (JObject)TrainingBlock.DeepClone());
                    Raise();
                    return;
            }
            finishing++;
        }

        static void Save(Action write)
        {
            try { write(); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { SaveError = e.Message; }
        }

        static void Fail(string message)
        {
            Error = "training_failed: " + message;
            State = Phase.Failed;
            finishing = 0; modelText = null;
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

    /// <summary>
    /// Works on the template run a few ms a frame in Play. Made on demand and kept across scenes. It runs before other
    /// scripts' Update, so a script reading <see cref="TemplateRun.PumpSeconds"/> in its Update sees this frame's work.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
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
