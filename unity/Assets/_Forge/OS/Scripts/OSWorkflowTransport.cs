using System;
using Newtonsoft.Json.Linq;

namespace Forge.OS
{
    /// <summary>Same workflow UI on a laptop hub or the headset's device-scoped cloud tools.</summary>
    public static class OSWorkflowTransport
    {
        public static bool Available => ForgeCloud.Serves("/tools/");
        public static string ConnectionMessage => !ForgeCloud.HasAppKey
            ? "This build isn't configured to connect to Albie's cloud. Ask the app owner for an updated build, or connect a laptop hub."
            : ForgeCloud.Ask() ?? "Connect to Albie's cloud or a laptop hub to start a workflow.";

        static void Cloud(string name, JObject args, Action<JObject> ok, Action<string> fail)
        {
            ForgeCloud.CallTool(name, args, result =>
            {
                string error = (string)result?["error"];
                if (error != null) fail(error);
                else ok(result ?? new JObject());
            });
        }

        public static void List(Action<JArray> ok, Action<string> fail)
        {
            if (ForgeNet.Status == ForgeNetStatus.Live)
            {
                ForgeNet.Get("/agent/builds", text => ok(JArray.Parse(text)), fail);
                return;
            }
            ForgeCloud.CallTool("build_status", new JObject(), result =>
            {
                string error = (string)result?["error"];
                if (error != null && !error.StartsWith("no build yet", StringComparison.Ordinal)) { fail(error); return; }
                ok(error != null || result?["id"] == null ? new JArray() : new JArray(result));
            });
        }

        public static void Start(string goal, string repo, Action<JObject> ok, Action<string> fail)
        {
            if (ForgeNet.Status == ForgeNetStatus.Live)
            {
                ForgeNet.Post("/agent/builds", new { goal, repo }, text => ok(JObject.Parse(text)), fail);
                return;
            }
            var args = new JObject { ["goal"] = goal };
            if (!string.IsNullOrWhiteSpace(repo)) args["repo"] = repo;
            Cloud("build_start", args, created =>
            {
                string id = (string)created["build_id"];
                if (string.IsNullOrEmpty(id)) { fail("The cloud did not return a workflow ID."); return; }
                Cloud("build_status", new JObject { ["build_id"] = id }, ok, fail);
            }, fail);
        }

        public static void Act(string id, string action, Action<JObject> ok, Action<string> fail)
        {
            if (action != "approve" && action != "cancel" && action != "ship")
            {
                fail("Unknown workflow action."); return;
            }
            if (ForgeNet.Status == ForgeNetStatus.Live)
            {
                ForgeNet.Post("/agent/builds/" + id + "/" + action, new { confirm = true }, text => ok(JObject.Parse(text)), fail);
                return;
            }
            // Cloud publication retains the tool's two-call confirmation: first ask, then a separate button press.
            Cloud("build_" + action, new JObject { ["build_id"] = id, ["confirm"] = true }, ok, fail);
        }

        public static void Review(string id, Action<JObject> ok, Action<string> fail)
        {
            if (ForgeNet.Status == ForgeNetStatus.Live)
            {
                ForgeNet.Get("/agent/builds/" + id + "/review", text => ok(JObject.Parse(text)), fail);
                return;
            }
            Cloud("build_review", new JObject { ["build_id"] = id }, ok, fail);
        }
    }
}
