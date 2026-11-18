using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.OS
{
    /// <summary>
    /// Albie's voice tool for Build something (Resources/AlbieTools/os_build.json): <c>os_answer</c> hands the user's
    /// spoken answer to the card in front of them, which counts as tapping it (<see cref="OSBuildFlow.Choose"/>). It has its
    /// own file so os.json keeps the four menu tools the check counts. It answers with an error line, the way the other
    /// tools do, when the card isn't open or the words match none of its choices.
    /// </summary>
    public static class OSBuildTools
    {
        /// <summary>The tool names this file registers; os_build.json must define exactly these.</summary>
        public static readonly string[] Names = { "os_answer" };

        /// <summary>Adds the tool to Albie. The app does this at start; a check does it by hand in the editor.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Register() => AlbieTools.Register("os_build", new Dictionary<string, AlbieTools.Handler>
        {
            ["os_answer"] = Answer,
        });

        static JObject Answer(JObject args)
        {
            string said = ((string)args["answer"] ?? "").Trim();
            if (said.Length == 0) return AlbieTools.Error("Say which answer the user gave.");
            if (OSBuildFlow.Current == null) return AlbieTools.Error("The New project card isn't open. Open it with os_open, kind system, id build.");
            if (!OSBuildFlow.Choose(said)) return AlbieTools.Error(OSBuildFlow.ChooseProblem ?? "That isn't one of the choices.");
            return new JObject { ["ok"] = true, ["step"] = OSBuildFlow.LastStep };
        }
    }
}
