using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// Albie's artifact tools on the device, with no hub (C-12 section 10). "Make me a slalom" is
    /// <c>artifact_create {template: "slalom"}</c>, which makes it and opens it in the room: the course appears in free
    /// floor, and the result carries the line Albie says when the course had to shrink. <c>artifact_run</c> arms a scored
    /// lap; <c>artifact_compare</c> puts run metrics side by side. Refusals come back as <c>{"error": "code: message"}</c>.
    /// </summary>
    public static class ArtifactTools
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register() => AlbieTools.Register("artifacts", new Dictionary<string, AlbieTools.Handler>
        {
            ["artifact_list"] = List,
            ["artifact_create"] = Create,
            ["artifact_open"] = Open,
            ["artifact_close"] = Close,
            ["artifact_relayout"] = Relayout,
            ["artifact_run"] = Run,
            ["artifact_compare"] = Compare,
        });

        public static JObject List(JObject args)
        {
            var templates = new JArray();
            foreach (string name in ArtifactLibrary.Templates)
                if (ArtifactLibrary.TryTemplate(name, out var manifest, out _))
                    templates.Add(new JObject { ["template"] = name, ["title"] = manifest["title"], ["inputs"] = manifest["inputs"] });
            var host = ArtifactHost.Current;
            return new JObject { ["templates"] = templates, ["library"] = ArtifactLibrary.List(), ["open"] = host != null ? host.OpenId : null };
        }

        public static JObject Create(JObject args)
        {
            string template = (string)args["template"];
            if (string.IsNullOrEmpty(template)) throw new ArgumentException("bad_value: say which template (" + string.Join(", ", ArtifactLibrary.Templates) + ")");
            var made = ArtifactLibrary.FromTemplate(template, args["inputs"] as JObject);
            var result = new JObject { ["id"] = made.Id, ["version"] = made.Version, ["change"] = made.Change };
            if (args.Value<bool?>("open") != false) result["opened"] = Opened(Host(), made.Id, h => h.Open(made.Id));
            return result;
        }

        public static JObject Open(JObject args)
        {
            string id = (string)args["id"];
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("bad_value: say which artifact");
            return Opened(Host(), id, h => h.Open(id));
        }

        public static JObject Close(JObject args)
        {
            var host = Host();
            string was = host.OpenId;
            host.Close();
            return new JObject { ["closed"] = was };
        }

        public static JObject Relayout(JObject args)
        {
            var host = Host();
            string id = (string)args["id"] ?? host.OpenId;
            if (id == null || host.OpenId != id) throw new ArgumentException($"not_open: open {id ?? "the artifact"} first");
            return Opened(host, id, h => h.Relayout());
        }

        public static JObject Run(JObject args)
        {
            var host = Host();
            string id = (string)args["id"] ?? host.OpenId;
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("bad_value: say which artifact");
            if (host.OpenId != id)
            {
                var opened = Opened(host, id, h => h.Open(id));
                if ((string)opened["status"] != "open") return opened;
            }
            string error = host.ArmRun();
            if (error != null) throw new ArgumentException(error);
            var run = host.Run;
            return new JObject { ["id"] = id, ["run_id"] = run.RunId, ["state"] = "armed" };
        }

        public static JObject Compare(JObject args)
        {
            string id = (string)args["id"];
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("bad_value: say which artifact");
            string[] ids = null;
            if (args["runs"] is JArray listed)
            {
                ids = new string[listed.Count];
                for (int i = 0; i < listed.Count; i++) ids[i] = (string)listed[i];
            }
            return ArtifactRuns.Compare(id, ids);
        }

        static ArtifactHost Host() =>
            ArtifactHost.Current ?? throw new ArgumentException("no_host: there's nowhere to show artifacts in this scene");

        // What the agent needs after an open: waiting for a tap, refused (with why), or open with its layout and the line to say.
        static JObject Opened(ArtifactHost host, string id, Action<ArtifactHost> open)
        {
            open(host);
            if (host.AwaitingApproval)
                return new JObject
                {
                    ["id"] = id, ["status"] = "awaiting_approval",
                    ["next"] = "It's asking for permission the first time: tell the person what it will use (the room's layout and the truck's position) and ask them to tap Allow.",
                };
            if (host.LastProblem != null)
            {
                string code = host.LastErrors.Count > 0 ? host.LastErrors[0].code : "cant_open";
                throw new ArgumentException($"{code}: {host.LastProblem}");
            }
            var result = new JObject { ["id"] = id, ["status"] = "open", ["title"] = host.OpenTitle };
            var layout = host.LastLayout;
            if (layout != null)
            {
                result["layout"] = new JObject
                {
                    ["layout_id"] = layout["layout_id"], ["placed"] = layout["fit"]["placed"], ["requested"] = layout["fit"]["requested"],
                    ["shrunk"] = layout["fit"]["shrunk"], ["around_found"] = layout["fit"]["around_found"], ["reused"] = host.LayoutReused,
                };
                if (host.LayoutMessage != null) result["say"] = host.LayoutMessage;
            }
            return result;
        }
    }
}
