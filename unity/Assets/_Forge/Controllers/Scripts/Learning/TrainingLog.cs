using System;
using Newtonsoft.Json.Linq;

namespace Forge.Controllers
{
    /// <summary>
    /// Where a finished training run is reported, for the lab notebook (C-17, docs/NOTEBOOK_CONTRACT.md section 3:
    /// "A training run ends → ML A's BehaviourCloner → new training entry with observed.training").
    /// The Notebook module (Person 5) isn't built yet, so this is a hook rather than a direct call: when it lands,
    /// it subscribes once with
    /// <c>TrainingLog.Finished += (controller, version, training) => NotebookStore.LogTraining(controller, version, training);</c>
    /// The training block is the controller file's <c>training</c> object (C-19 section 3.4), plus the candidates.
    /// </summary>
    public static class TrainingLog
    {
        public static event Action<string, int, JObject> Finished;

        /// <summary>The last report, so a notebook that opens later can still pick it up.</summary>
        public static (string controller, int version, JObject training)? Last { get; private set; }

        public static void Report(string controller, int version, JObject training)
        {
            Last = (controller, version, training);
            Finished?.Invoke(controller, version, training);
        }
    }
}
