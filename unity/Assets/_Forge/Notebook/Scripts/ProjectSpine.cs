using System;
using Forge.Controllers;
using Forge.ML;
using Forge.OS;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Notebook
{
    /// <summary>
    /// Makes the open project the record of what happens to it: it listens to the events CAD, the controller brain and
    /// the ML desk already raise (it changes none of them) and cuts a project version when
    /// - the open project's CAD design (<see cref="OSProjects.DesignOf"/>: the quadcopter, the rocket) gets a new version
    ///   (<see cref="CadSession.ModelChanged"/>; opening a design is not a change, and a burst of changes, such as a slider
    ///   drag that builds several versions, is one cut <see cref="CadSettleSeconds"/> after the last), <c>cut_by: cad</c>,
    ///   pinning <c>cad.&lt;part&gt;</c>;
    /// - a controller training run finishes (<see cref="TrainingLog.Finished"/>, already logged as a <c>training</c> entry by
    ///   <see cref="NotebookTools"/>), <c>cut_by: training</c>, pinning <c>controllers.&lt;controller&gt;</c>;
    /// - an ML desk run finishes for an ML project (<see cref="MLSession.Changed"/> with the run done; not the bundled sample
    ///   playing back), logged as a <c>training</c> entry and cut the same way, pinning <c>checkpoints.&lt;project&gt;</c>.
    /// Only for a project the person opened (<see cref="OSProjects.ChosenOpen"/>), or the truck project in the app's own
    /// notebook. One hidden object, made after the scene loads; Update only compares a few fields and allocates nothing.
    /// </summary>
    public sealed class ProjectSpine : MonoBehaviour
    {
        /// <summary>How long CAD must stay on one version before it is cut (a drag builds several in a row).</summary>
        public const float CadSettleSeconds = 2f;
        const float FindSeconds = 2f;

        static ProjectSpine instance;

        /// <summary>The newest version CAD showed for each design this session ("quadcopter" v7), for the Project window.</summary>
        public static string LastCadPart { get; private set; }
        public static int LastCadVersion { get; private set; }

        CadSession cad;
        MLSession ml;
        bool mlWasDone;
        string mlLastRun;
        float nextFind;
        string kindProject;  // the open project's kind, read again only when another project opens
        bool kindIsMl;

        // A CAD version waiting to settle before it is cut.
        bool cadPending;
        string cadPart, cadLabel, cadProject;
        int cadVersion;
        float cadDue;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            instance = null;
            LastCadPart = null;
            LastCadVersion = 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (instance != null) return;
            var go = new GameObject(nameof(ProjectSpine)) { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            instance = go.AddComponent<ProjectSpine>();
        }

        void OnEnable()
        {
            TrainingLog.Finished -= OnTraining;
            TrainingLog.Finished += OnTraining;
        }

        void OnDisable()
        {
            TrainingLog.Finished -= OnTraining;
            Watch(null);
            WatchMl(null);
        }

        /// <summary>True when a version may be cut for the open project: one the person opened, or the app's truck project.</summary>
        public static bool MayCut =>
            NotebookStore.IsOpen && (OSProjects.ChosenOpen
                || (NotebookStore.ProjectId == NotebookStore.DefaultProject && NotebookStore.Root == NotebookStore.DefaultRoot));

        void Update()
        {
            if (CadSession.Instance != cad) Watch(CadSession.Instance);

            if (cadPending && Time.unscaledTime >= cadDue)
            {
                cadPending = false;
                CutCad();
            }

            if (ml == null && Time.unscaledTime >= nextFind)
            {
                nextFind = Time.unscaledTime + FindSeconds;
                // The ML desk's session exists only while it is (or was) open; look for it only for an ML project.
                string open = NotebookStore.ProjectId;
                if (open != kindProject)
                {
                    kindProject = open;
                    kindIsMl = open != null && OSProjects.KindOf(open) == OSProjectKind.Ml;
                }
                if (kindIsMl) WatchMl(FindAnyObjectByType<MLSession>());
            }
        }

        // ---------------------------------------------------------------- CAD

        void Watch(CadSession session)
        {
            if (cad != null) cad.ModelChanged -= OnCadModel;
            cad = session;
            if (cad != null) cad.ModelChanged += OnCadModel;
        }

        void OnCadModel(CadUpdate update)
        {
            CadModelUpdated info = update?.info;
            if (info == null || string.IsNullOrEmpty(info.part)) return;
            LastCadPart = info.part;
            LastCadVersion = info.version;
            if (!MayCut) return;
            string project = NotebookStore.ProjectId;
            if (OSProjects.DesignOf(project) != info.part) return;
            // Opening a design, or the first version of one, shows what is there: nothing changed.
            if (info.source == "open" || info.source == "startup" || !info.previousVersion.HasValue) return;
            if (PinnedCad(info.part) == info.version) return;
            cadPending = true;
            cadPart = info.part;
            cadVersion = info.version;
            cadLabel = info.label;
            cadProject = project;
            cadDue = Time.unscaledTime + CadSettleSeconds;
        }

        void CutCad()
        {
            if (!MayCut || NotebookStore.ProjectId != cadProject || PinnedCad(cadPart) == cadVersion) return;
            string what = OSProjects.TitleFor(cadProject) + " design v" + cadVersion;
            string summary = string.IsNullOrWhiteSpace(cadLabel) ? what + "." : what + ": " + cadLabel.Trim().TrimEnd('.') + ".";
            try
            {
                NotebookStore.CutVersion("cad", summary, new JObject { ["cad"] = new JObject { [cadPart] = cadVersion } });
            }
            catch (Exception e)
            {
                Debug.Log("[Notebook] Couldn't cut a version for the new design: " + e.Message);
            }
        }

        /// <summary>The CAD version of <paramref name="part"/> the open project's newest version pins, or 0.</summary>
        public static int PinnedCad(string part)
        {
            var versions = NotebookStore.Versions;
            if (string.IsNullOrEmpty(part) || versions.Count == 0) return 0;
            return (int?)versions[versions.Count - 1]["cad"]?[part] ?? 0;
        }

        // ---------------------------------------------------------------- controller training (truck, drone)

        static void OnTraining(string controller, int version, JObject training)
        {
            if (!MayCut) return;
            try
            {
                var entries = NotebookStore.Entries;
                for (int i = entries.Count - 1; i >= 0 && i >= entries.Count - 4; i--)
                {
                    JObject e = entries[i];
                    if ((string)e["kind"] != "training") continue;
                    JToken block = e["observed"]?["training"];
                    if ((string)block?["controller"] != controller || (int?)block?["controller_version"] != version) continue;
                    NotebookStore.CutTrainingVersion((string)e["change"] ?? ("Trained " + controller + "."),
                        new JObject { ["controllers"] = new JObject { [controller] = version } }, (string)e["id"]);
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[Notebook] Couldn't cut a version for the training run: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- the ML desk (image classifier, cloud projects)

        void WatchMl(MLSession session)
        {
            if (ml != null) ml.Changed -= OnMlChanged;
            ml = session;
            if (ml == null) return;
            mlWasDone = ml.Done;
            ml.Changed += OnMlChanged;
        }

        void OnMlChanged()
        {
            if (ml == null) return;
            bool done = ml.Done;
            bool finished = done && !mlWasDone;
            mlWasDone = done;
            if (!finished || (ml.replay && !ml.HubData)) return;  // the bundled sample run playing back trained nothing
            string run = string.IsNullOrEmpty(ml.RunId) ? "run" : ml.RunId;
            if (run != "on-device" && run == mlLastRun) return;     // a replay of the run already recorded
            mlLastRun = run;
            if (!MayCut || OSProjects.KindOf(NotebookStore.ProjectId) != OSProjectKind.Ml) return;
            RecordMlRun(NotebookStore.ProjectId, run, ml.TotalEpochs, LastAccuracy(ml), ml.IsLive);
        }

        static float LastAccuracy(MLSession s)
        {
            var metrics = s.Data?.metrics;
            if (metrics == null) return float.NaN;
            for (int i = metrics.Length - 1; i >= 0; i--)
                if (metrics[i] != null && !float.IsNaN(metrics[i].val_acc)) return metrics[i].val_acc;
            return float.NaN;
        }

        /// <summary>
        /// Logs a finished ML run for <paramref name="projectId"/> (the open project) as a training entry and cuts a
        /// <c>training</c> version pinning the run. <paramref name="accuracy"/> is NaN when the run reported none.
        /// </summary>
        public static int RecordMlRun(string projectId, string runId, int epochs, float accuracy, bool cloud)
        {
            if (!NotebookStore.IsOpen || NotebookStore.ProjectId != projectId) return 0;
            string title = OSProjects.TitleFor(projectId);
            var block = new JObject { ["task"] = "multiclass", ["model"] = title, ["run_id"] = runId, ["where"] = cloud ? "cloud" : "device" };
            if (epochs > 0) block["epochs"] = epochs;
            if (!float.IsNaN(accuracy)) block["val_accuracy"] = Math.Round(accuracy, 4);
            string change = "Trained the " + title.ToLowerInvariant() + (epochs > 0 ? " for " + epochs + " epochs" : "") + (cloud ? " in the cloud." : " on this device.");
            try
            {
                string entry = NotebookStore.LogTrainingRun(change, "Learn the classes from the labelled examples.",
                    "It recognises held-out examples it wasn't trained on.", block);
                return entry == null ? 0 : NotebookStore.CutTrainingVersion(change,
                    new JObject { ["checkpoints"] = new JObject { [projectId] = runId } }, entry);
            }
            catch (Exception e)
            {
                Debug.Log("[Notebook] Couldn't record the ML run: " + e.Message);
                return 0;
            }
        }
    }
}
