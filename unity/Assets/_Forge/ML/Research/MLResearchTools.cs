using System.Collections.Generic;
using UnityEngine;

namespace Forge.ML
{
    /// <summary>
    /// The on-device voice's research tools (Resources/AlbieTools/ml_research.json, the same definitions as
    /// hub/ml/tools.py): ml_search_models and ml_search_datasets run on Albie's cloud (POST /tools/{name}), like the
    /// other ML tools in cloud.json. Their ml.research events open the Research panel (<see cref="MLWorkspaceBridge"/>).
    /// With the hub live, the hub's own voice has these tools and this path isn't used.
    /// </summary>
    static class MLResearchTools
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register()
        {
            var handlers = new Dictionary<string, AlbieTools.AsyncHandler>
            {
                ["ml_search_models"] = (args, done) => ForgeCloud.CallTool("ml_search_models", args, done),
                ["ml_search_datasets"] = (args, done) => ForgeCloud.CallTool("ml_search_datasets", args, done),
            };
            AlbieTools.Register("ml_research", handlers);
        }
    }
}
