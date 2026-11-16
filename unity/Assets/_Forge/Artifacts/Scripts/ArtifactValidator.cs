using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Forge.Artifacts
{
    /// <summary>One thing wrong with an artifact. Returned as data so the agent can fix it (C-12 section 9).</summary>
    public sealed class ArtifactError
    {
        public string code, path, message;
        public ArtifactError(string code, string path, string message)
        {
            this.code = code;
            this.path = path;
            this.message = message;
        }
        public override string ToString() => string.IsNullOrEmpty(path) ? $"{code}: {message}" : $"{code}: {path}: {message}";
    }

    /// <summary>
    /// The device validator (C-12 section 9). It runs before anything is shown and needs no network. It only reads
    /// JSON, so it knows nothing about Unity scenes. Asset hashes are checked when assets load, not here.
    /// </summary>
    public static class ArtifactValidator
    {
        static readonly Regex IdRule = new Regex("^[a-z0-9_-]+$");
        static readonly Regex NodeIdRule = new Regex("^[a-z0-9_]+$");
        static readonly Regex InputRef = new Regex(@"^\$[a-z][a-z0-9_]*$");
        static readonly Regex SourceRule = new Regex(@"^(builtin:[a-z0-9_]+|connector:[a-z0-9_]+/[a-z0-9_]+)$");
        static readonly Regex SnapshotRule = new Regex(@"^data/[a-z0-9_-]+\.json$");
        static readonly Regex HashRule = new Regex("^[0-9a-fA-F]{64}$");
        static readonly Regex ColorRule = new Regex("^#[0-9a-fA-F]{6}$");
        static readonly HashSet<string> FreeText = new HashSet<string> { "text", "title", "label" };

        // Common slips, so the agent gets a fix and not just a refusal.
        static readonly Dictionary<string, string> KindHints = new Dictionary<string, string>
        {
            { "cube", "primitive with shape box" }, { "box", "primitive with shape box" },
            { "sphere", "primitive with shape sphere" }, { "cylinder", "primitive with shape cylinder" },
            { "cone", "primitive with shape cone" }, { "label", "text" }, { "model", "gltf" }, { "mesh", "gltf" },
        };

        public static List<ArtifactError> Validate(JObject manifest, JObject scene)
        {
            var v = new Pass { manifest = manifest, scene = scene };
            try
            {
                v.Run();
            }
            catch (Exception e) when (e is InvalidOperationException || e is InvalidCastException || e is ArgumentException || e is FormatException || e is OverflowException)
            {
                // The agent wrote this file, so a wrong shape is an answer, not a crash.
                v.errors.Add(new ArtifactError("bad_value", "", "a field has the wrong type: " + e.Message));
            }
            return v.errors;
        }

        sealed class Pass
        {
            public JObject manifest, scene;
            public readonly List<ArtifactError> errors = new List<ArtifactError>();
            readonly HashSet<string> inputs = new HashSet<string>(), numericInputs = new HashSet<string>();
            readonly HashSet<string> slots = new HashSet<string>(), metrics = new HashSet<string>(), buttons = new HashSet<string>();
            readonly HashSet<string> tables = new HashSet<string>(), assets = new HashSet<string>();
            readonly Dictionary<string, JObject> nodes = new Dictionary<string, JObject>();

            void Add(string code, string path, string message) => errors.Add(new ArtifactError(code, path, message));

            public void Run()
            {
                if (manifest == null) { Add("bad_value", "manifest", "missing or not a JSON object"); return; }
                if (scene == null) { Add("bad_value", "scene", "missing or not a JSON object"); return; }
                bool readable = Schema(manifest, ArtifactSchema.Manifest, "schema") & Schema(scene, ArtifactSchema.Scene, "scene.schema");
                if (!readable) return;  // a newer file can't be judged by this version's rules
                Manifest();
                Permissions();
                Scene();
            }

            bool Schema(JObject o, string expected, string path)
            {
                string found = o.Value<string>("schema");
                int slash = expected.LastIndexOf('/');
                string name = expected.Substring(0, slash + 1);
                if (found == null || !found.StartsWith(name))
                {
                    Add("bad_value", path, $"must be {expected}, found {found ?? "nothing"}");
                    return false;
                }
                if (!int.TryParse(found.Substring(slash + 1), out int major) || major > int.Parse(expected.Substring(slash + 1)))
                {
                    Add("newer_schema", path, $"{found}: this device reads up to {expected}");
                    return false;
                }
                return true;
            }

            void Manifest()
            {
                string id = manifest.Value<string>("id");
                if (id == null || !IdRule.IsMatch(id)) Add("bad_value", "id", "use lowercase letters, digits, _ and -");
                if (string.IsNullOrEmpty(manifest.Value<string>("title"))) Add("bad_value", "title", "required");
                if (manifest["version"]?.Type != JTokenType.Integer || manifest.Value<int>("version") < 1)
                    Add("bad_value", "version", "must be an integer from 1");
                OneOf(manifest, "kind", ArtifactSchema.Kinds, "kind");
                OneOf(manifest, "category", ArtifactSchema.Categories, "category");

                if (manifest["script"] != null && manifest["script"].Type != JTokenType.Null)
                    Add("script_not_allowed", "script", "script must be null in Phase 1");

                var runtime = manifest["runtime"] as JObject;
                if (runtime == null || runtime.Value<string>("type") != "scene") Add("bad_value", "runtime.type", "must be scene in Phase 1");
                else if (runtime.Value<int?>("min_runtime") > ArtifactSchema.HostRuntime)
                    Add("newer_schema", "runtime.min_runtime", $"needs host {runtime.Value<int>("min_runtime")}, this device runs {ArtifactSchema.HostRuntime}");

                if (manifest.Value<string>("kind") == "standalone" && manifest["links"] is JObject links)
                    foreach (var link in links.Properties())
                        if (link.Value.Type != JTokenType.Null) Add("bad_value", "links." + link.Name, "a standalone artifact has no links");

                Inputs();

                var scenarios = manifest["scenarios"] as JArray;
                if (scenarios == null || scenarios.Count == 0) Add("bad_value", "scenarios", "at least one is required");

                if (manifest["data"] is JArray data)
                    for (int i = 0; i < data.Count; i++)
                    {
                        string name = data[i].Value<string>("name"), source = data[i].Value<string>("source");
                        if (name != null) tables.Add(name);
                        if (source == null || !SourceRule.IsMatch(source))
                            Add("bad_value", $"data[{i}].source", "use builtin:<name> or connector:<connector>/<tool>");
                        else if (source.StartsWith("connector:") && !Asks(ArtifactData.Permission(source)))
                            Add("permission_missing", $"data[{i}].source", $"{source} needs \"{ArtifactData.Permission(source)}\" in permissions.connectors");
                        string snapshot = data[i].Value<string>("snapshot");
                        if (snapshot != null && !SnapshotRule.IsMatch(snapshot))
                            Add("bad_value", $"data[{i}].snapshot", "use data/<name>.json");
                    }

                if (manifest["assets"] is JArray list)
                    for (int i = 0; i < list.Count; i++)
                    {
                        string path = list[i].Value<string>("path"), at = $"assets[{i}]";
                        if (path == null || !ArtifactPackage.SafeAsset.IsMatch(path)) Add("bad_value", at + ".path", "use assets/<name>.glb");
                        else if (!assets.Add(path)) Add("bad_value", at + ".path", $"\"{path}\" is listed twice");
                        if (!HashRule.IsMatch(list[i].Value<string>("sha256") ?? "")) Add("bad_value", at + ".sha256", "needs the file's sha256, as 64 hex digits");
                        if (string.IsNullOrEmpty(list[i].Value<string>("license"))) Add("bad_value", at + ".license", "required (CC0, CC-BY-4.0, own)");
                    }
            }

            void Inputs()
            {
                if (!(manifest["inputs"] is JArray list)) return;
                for (int i = 0; i < list.Count; i++)
                {
                    string path = $"inputs[{i}]", name = list[i].Value<string>("name"), type = list[i].Value<string>("type");
                    if (string.IsNullOrEmpty(name)) { Add("bad_value", path, "needs a name"); continue; }
                    inputs.Add(name);
                    if (type == null || !ArtifactSchema.InputTypes.Contains(type)) { Add("bad_value", path, $"unknown type {type}"); continue; }
                    if (type != "number" && type != "integer") continue;
                    numericInputs.Add(name);
                    double? min = list[i].Value<double?>("min"), max = list[i].Value<double?>("max"), value = list[i].Value<double?>("default");
                    if (min == null || max == null || value == null) { Add("bad_value", path, "needs min, max and default"); continue; }
                    if (min > max) Add("bad_value", path, $"min {min} is above max {max}");
                    if (value > max) Add("bad_value", path, $"default {value} is above max {max}");
                    if (value < min) Add("bad_value", path, $"default {value} is below min {min}");
                }
            }

            void Permissions()
            {
                if (!(manifest["permissions"] is JObject p)) return;
                foreach (var key in p.Properties())
                    if (!ArtifactSchema.Permissions.Contains(key.Name))
                        Add("permission_denied", "permissions." + key.Name, "not a permission in the schema");
                if (p["network"] is JArray network && network.Count > 0)
                    Add("permission_denied", "permissions.network", "permissions.network must be empty in Phase 1");
                if (p["connectors"] is JArray connectors)
                    foreach (var connector in connectors)
                        if (!((string)connector ?? "").EndsWith(":read"))
                            Add("permission_denied", "permissions.connectors", $"{connector}: only :read is allowed in Phase 1");
            }

            void Scene()
            {
                string space = scene.Value<string>("space");
                if (space == null || !ArtifactSchema.Spaces.Contains(space)) Add("bad_value", "scene.space", $"unknown space {space}");
                if (space == "room" && Permission("room") != "read")
                    Add("permission_missing", "permissions.room", "space is room, so permissions.room must be read");
                if (scene.ToString().Length > ArtifactSchema.MaxSceneChars) Add("too_big", "scene", "over the 256 KB limit");
                if (scene["view"] is JObject view)
                {
                    if (space != "volume") Add("bad_value", "scene.view", "view needs space volume");
                    if (view.Value<double?>("radius") == null || view.Value<double>("radius") <= 0.0) Add("bad_value", "scene.view.radius", "must be above 0");
                }

                Generators(space);
                var list = scene["nodes"] as JArray ?? new JArray();
                if (list.Count > ArtifactSchema.MaxNodes) Add("too_big", "nodes", $"{list.Count} is over the limit of {ArtifactSchema.MaxNodes}");
                CollectNodes(list);
                Rules();  // before nodes, so widgets and text can see the metrics
                for (int i = 0; i < list.Count; i++)
                    if (list[i] is JObject node) Node(node, $"nodes[{i}]");
                Cycles();
                RunBlock();
                Outputs();
                InputRefs(scene["generators"], "generators");
                InputRefs(scene["nodes"], "nodes");
            }

            string Permission(string key) => (manifest["permissions"] as JObject)?.Value<string>(key);

            bool Asks(string connector)
            {
                if (manifest["permissions"]?["connectors"] is JArray list)
                    foreach (var c in list) if ((string)c == connector) return true;
                return false;
            }

            void Generators(string space)
            {
                if (!(scene["generators"] is JArray list)) return;
                if (list.Count > 0 && space != "room") Add("bad_value", "generators", "generators need space room");
                for (int i = 0; i < list.Count; i++)
                {
                    string id = list[i].Value<string>("id"), type = list[i].Value<string>("type");
                    if (id == null || type == null || !ArtifactSchema.Generators.TryGetValue(type, out var fills))
                    {
                        Add("bad_value", $"generators[{i}]", $"unknown generator {type}");
                        continue;
                    }
                    foreach (var slot in fills) slots.Add(id + "." + slot);
                }
            }

            void CollectNodes(JArray list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    string path = $"nodes[{i}]";
                    if (!(list[i] is JObject node)) { Add("bad_value", path, "must be an object"); continue; }
                    string id = node.Value<string>("id");
                    if (id == null || !NodeIdRule.IsMatch(id)) { Add("bad_value", path, "needs an id of lowercase letters, digits and _"); continue; }
                    if (nodes.ContainsKey(id)) { Add("bad_value", path, $"id \"{id}\" is used twice"); continue; }
                    nodes[id] = node;
                    if (node["widgets"] is JArray widgets)
                        foreach (var w in widgets)
                            if (w.Value<string>("type") == "button" && w.Value<string>("id") != null)
                            {
                                buttons.Add(w.Value<string>("id"));
                                if (w.Value<string>("alt_id") != null) buttons.Add(w.Value<string>("alt_id"));
                            }
                }
                int zones = 0;
                foreach (var node in nodes.Values) if (IsZone(node)) zones++;
                if (zones > ArtifactSchema.MaxZones) Add("too_big", "nodes", $"{zones} zones is over the limit of {ArtifactSchema.MaxZones}");
            }

            static bool IsZone(JObject n) =>
                n.Value<string>("kind") == "zone" || (n.Value<string>("kind") == "array" && n["template"]?.Value<string>("kind") == "zone");

            static bool IsNumber(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float);

            void Node(JObject n, string path)
            {
                string kind = n.Value<string>("kind");
                if (kind != null && ArtifactSchema.ReservedKinds.Contains(kind))
                {
                    Add("unsupported_kind", path, $"{kind} isn't available in Phase 1");
                    return;
                }
                if (kind == null || !ArtifactSchema.NodeKinds.Contains(kind))
                {
                    string hint = kind != null && KindHints.TryGetValue(kind, out var fix) ? $" (did you mean {fix}?)" : "";
                    Add("unknown_kind", path, $"unknown kind \"{kind}\"{hint}");
                    return;
                }

                string parent = n.Value<string>("parent");
                if (parent != null && !nodes.ContainsKey(parent)) Add("unknown_ref", path + ".parent", $"no node \"{parent}\"");
                string at = n.Value<string>("at");
                if (at != null && !slots.Contains(at)) Add("unknown_slot", path + ".at", $"no slot \"{at}\"");

                switch (kind)
                {
                    case "primitive": Primitive(n, path); break;
                    case "text":
                        if (n["text"] == null) Add("bad_value", path + ".text", "required");
                        else foreach (Match m in Regex.Matches(n.Value<string>("text"), @"\{([a-z0-9_]+)\}"))
                            if (!metrics.Contains(m.Groups[1].Value)) Add("unknown_ref", path + ".text", $"no metric \"{m.Groups[1].Value}\"");
                        break;
                    case "panel": Panel(n, path); break;
                    case "sim": Sim(n, path); break;
                    case "array": Array(n, path); break;
                    case "gltf":
                        string asset = n.Value<string>("asset");
                        if (asset == null || !assets.Contains(asset)) Add("asset_not_listed", path + ".asset", $"\"{asset}\" isn't in the manifest");
                        break;
                    case "timeline":
                        var duration = n["duration_s"];
                        if (!IsNumber(duration) || duration.Value<double>() <= 0.0) Add("bad_value", path + ".duration_s", "needs a number of seconds above 0");
                        foreach (string flag in new[] { "loop", "autoplay" })
                            if (n[flag] != null && n[flag].Type != JTokenType.Boolean) Add("bad_value", path + "." + flag, "true or false");
                        break;
                    case "line":
                        if (n["points"] == null && n["source"] == null && n["follow"] == null && n["through"] == null)
                            Add("bad_value", path, "needs points, a source, follow or through");
                        else Source(n, path);
                        Follow(n, path);
                        Through(n, path);
                        break;
                }
            }

            // A line through nodes reads their local positions each frame, so they have to share its parent too.
            void Through(JObject n, string path)
            {
                if (n["through"] == null) return;
                if (!(n["through"] is JArray through) || through.Count < 2)
                {
                    Add("bad_value", path + ".through", "a list of at least 2 node ids");
                    return;
                }
                if (through.Count > ArtifactSchema.MaxBodies + 1)
                    Add("too_big", path + ".through", $"{through.Count} is over the limit of {ArtifactSchema.MaxBodies + 1}");
                for (int i = 0; i < through.Count; i++)
                {
                    string id = through[i].Type == JTokenType.String ? (string)through[i] : null;
                    if (id == null || !nodes.TryGetValue(id, out var target)) Add("unknown_ref", $"{path}.through[{i}]", $"no node \"{through[i]}\"");
                    else if (target.Value<string>("parent") != n.Value<string>("parent"))
                        Add("bad_value", $"{path}.through[{i}]", $"a line through nodes needs the same parent as \"{id}\"");
                }
            }

            // A trail records its target's local position, so the two have to share a parent to line up.
            void Follow(JObject n, string path)
            {
                string follow = n.Value<string>("follow");
                if (follow == null) return;
                if (!nodes.TryGetValue(follow, out var target)) Add("unknown_ref", path + ".follow", $"no node \"{follow}\"");
                else if (target.Value<string>("parent") != n.Value<string>("parent"))
                    Add("bad_value", path + ".follow", $"a trail needs the same parent as \"{follow}\"");
                if (n.Value<int?>("length") > ArtifactSchema.MaxTrailPoints)
                    Add("too_big", path + ".length", $"{n.Value<int>("length")} is over the limit of {ArtifactSchema.MaxTrailPoints}");
            }

            void Sim(JObject n, string path)
            {
                string solver = n.Value<string>("solver");
                if (solver == null || !ArtifactSchema.Solvers.ContainsKey(solver)) Add("bad_value", path + ".solver", $"unknown solver \"{solver}\"");
                if (!(n["bodies"] is JArray bodies) || bodies.Count == 0)
                {
                    Add("bad_value", path + ".bodies", "at least one is required");
                    return;
                }
                if (bodies.Count > ArtifactSchema.MaxBodies) Add("too_big", path + ".bodies", $"{bodies.Count} is over the limit of {ArtifactSchema.MaxBodies}");
                var seen = new HashSet<string>();
                for (int i = 0; i < bodies.Count; i++)
                {
                    string at = $"{path}.bodies[{i}]", node = bodies[i].Value<string>("node");
                    if (node == null || !nodes.ContainsKey(node)) Add("unknown_ref", at + ".node", $"no node \"{node}\"");
                    else if (!seen.Add(node)) Add("bad_value", at + ".node", $"\"{node}\" is driven twice");
                    if (bodies[i]["mass"]?.Type == JTokenType.Float || bodies[i]["mass"]?.Type == JTokenType.Integer)
                        if (bodies[i].Value<double>("mass") <= 0.0) Add("bad_value", at + ".mass", "must be above 0");
                }
                if (n["params"] is JObject p && (p["step"]?.Type == JTokenType.Float || p["step"]?.Type == JTokenType.Integer) && p.Value<double>("step") <= 0.0)
                    Add("bad_value", path + ".params.step", "must be above 0");
                if (solver == "pendulum")
                {
                    if (bodies.Count > PendulumSolver.MaxLinks)
                        Add("too_big", path + ".bodies", $"a pendulum has 1 or {PendulumSolver.MaxLinks} links, not {bodies.Count}");
                    for (int i = 0; i < bodies.Count; i++)
                        if ((bodies[i]["length"]?.Type == JTokenType.Float || bodies[i]["length"]?.Type == JTokenType.Integer) && bodies[i].Value<double>("length") <= 0.0)
                            Add("bad_value", $"{path}.bodies[{i}].length", "must be above 0");
                }
            }

            void Primitive(JObject n, string path)
            {
                string shape = n.Value<string>("shape");
                if (shape == null || !ArtifactSchema.Shapes.Contains(shape)) Add("bad_value", path + ".shape", $"unknown shape \"{shape}\"");
            }

            void Panel(JObject n, string path)
            {
                if (!(n["widgets"] is JArray widgets)) return;
                for (int i = 0; i < widgets.Count; i++)
                {
                    string at = $"{path}.widgets[{i}]", type = widgets[i].Value<string>("type");
                    if (type == null || !ArtifactSchema.Widgets.Contains(type)) { Add("bad_value", at, $"unknown widget \"{type}\""); continue; }
                    string metric = widgets[i].Value<string>("metric");
                    if (metric != null && !metrics.Contains(metric)) Add("unknown_ref", at + ".metric", $"no metric \"{metric}\"");
                    if (widgets[i]["columns"] is JArray columns)
                        foreach (var column in columns)
                            if (!metrics.Contains((string)column)) Add("unknown_ref", at + ".columns", $"no metric \"{column}\"");
                    if (type == "button" && widgets[i].Value<string>("id") == null) Add("bad_value", at, "a button needs an id");
                    if (type == "slider")
                    {
                        string input = widgets[i].Value<string>("input");
                        if (input == null || !inputs.Contains(input)) Add("unknown_input", at + ".input", $"no input \"{input}\"");
                        else if (!numericInputs.Contains(input)) Add("bad_value", at + ".input", $"\"{input}\" isn't a number, so it can't have a slider");
                    }
                }
            }

            void Array(JObject n, string path)
            {
                string template = n["template"]?.Value<string>("kind");
                if (template != "primitive" && template != "zone") Add("bad_value", path + ".template", "must be a primitive or a zone");
                if (template == "primitive") Primitive((JObject)n["template"], path + ".template");
                if (n["source"] == null) Add("bad_value", path + ".source", "required");
                else Source(n, path);
            }

            void Source(JObject n, string path)
            {
                var source = n["source"];
                if (source == null) return;
                if (source.Type == JTokenType.String)
                {
                    if (!slots.Contains((string)source)) Add("unknown_slot", path + ".source", $"no slot \"{source}\"");
                }
                else if (source["table"] != null && !tables.Contains(source.Value<string>("table")))
                    Add("unknown_ref", path + ".source.table", $"no data \"{source.Value<string>("table")}\" in the manifest");
            }

            void Rules()
            {
                if (!(scene["rules"] is JArray rules)) return;
                if (rules.Count > ArtifactSchema.MaxRules) Add("too_big", "rules", $"{rules.Count} is over the limit of {ArtifactSchema.MaxRules}");
                foreach (var rule in rules)
                    if (rule.Value<string>("type") == "metric" && rule.Value<string>("name") != null) metrics.Add(rule.Value<string>("name"));
                for (int i = 0; i < rules.Count; i++)
                {
                    string path = $"rules[{i}]", type = rules[i].Value<string>("type");
                    if (type == null || !ArtifactSchema.Rules.Contains(type)) { Add("bad_value", path, $"unknown rule \"{type}\""); continue; }
                    if (type == "metric") Metric(rules[i], path);
                    if (type == "on_input" && !buttons.Contains(rules[i].Value<string>("input") ?? ""))
                        Add("unknown_ref", path + ".input", $"no button \"{rules[i].Value<string>("input")}\"");
                    if (type == "on_zone") ZoneRef(rules[i].Value<string>("zone"), path + ".zone");
                    if (type == "animate") Animate(rules[i], path);
                    if (rules[i]["do"] is JArray actions)
                        for (int a = 0; a < actions.Count; a++)
                        {
                            string target = actions[a].Value<string>("set");
                            if (target != null && !nodes.ContainsKey(target.Split('.')[0]))
                                Add("unknown_ref", $"{path}.do[{a}].set", $"no node \"{target.Split('.')[0]}\"");
                            string timeline = actions[a].Value<string>("timeline");
                            if (timeline == null) continue;
                            TimelineRef(timeline, $"{path}.do[{a}].timeline");
                            if (!ArtifactSchema.TimelineActions.Contains(actions[a].Value<string>("action") ?? ""))
                                Add("bad_value", $"{path}.do[{a}].action", "play, pause or restart");
                        }
                }
            }

            readonly HashSet<string> animated = new HashSet<string>();

            void Animate(JToken rule, string path)
            {
                string target = rule.Value<string>("target"), property = rule.Value<string>("property");
                JObject clock = TimelineRef(rule.Value<string>("timeline"), path + ".timeline");
                if (target == null || !nodes.TryGetValue(target, out var node)) Add("unknown_ref", path + ".target", $"no node \"{target}\"");
                else if (node.Value<string>("kind") == "timeline" || node.Value<string>("kind") == "panel")
                    Add("bad_value", path + ".target", $"a {node.Value<string>("kind")} can't be animated");
                else if (node["at"] != null && (property == "pos" || property == "rot"))
                    Add("bad_value", path + ".property", $"\"{target}\" stands on a slot, so its {property} can't be animated");
                if (property == null || !ArtifactSchema.AnimateProperties.TryGetValue(property, out string shape))
                {
                    Add("bad_value", path + ".property", "use pos, rot, scale, visible or color");
                    return;
                }
                if (target != null && !animated.Add(target + "." + property)) Add("bad_value", path, $"{target}.{property} is animated twice");
                if (!(rule["keys"] is JArray keys) || keys.Count == 0)
                {
                    Add("bad_value", path + ".keys", "at least one [t, value] is required");
                    return;
                }
                if (keys.Count > ArtifactSchema.MaxKeys) Add("too_big", path + ".keys", $"{keys.Count} is over the limit of {ArtifactSchema.MaxKeys}");
                double end = clock != null && IsNumber(clock["duration_s"]) ? clock.Value<double>("duration_s") : double.MaxValue, before = -1.0;
                for (int k = 0; k < keys.Count; k++)
                {
                    string at = $"{path}.keys[{k}]";
                    if (!(keys[k] is JArray key) || key.Count != 2 || !IsNumber(key[0])) { Add("bad_value", at, "use [t, value]"); continue; }
                    double t = key[0].Value<double>();
                    if (t < 0.0 || t > end) Add("bad_value", at, $"t {t} is outside the timeline (0 to {end} s)");
                    else if (t < before) Add("bad_value", at, "times have to go up");
                    before = t;
                    if (!KeyValue(property, key[1])) Add("bad_value", at, $"{property} takes {shape}");
                }
            }

            static bool KeyValue(string property, JToken v)
            {
                switch (property)
                {
                    case "visible": return v.Type == JTokenType.Boolean;
                    case "color": return v.Type == JTokenType.String && ColorRule.IsMatch((string)v);
                    case "scale" when IsNumber(v): return true;
                    default: return v is JArray xyz && xyz.Count == 3 && IsNumber(xyz[0]) && IsNumber(xyz[1]) && IsNumber(xyz[2]);
                }
            }

            JObject TimelineRef(string id, string path)
            {
                if (id == null || !nodes.TryGetValue(id, out var node)) Add("unknown_ref", path, $"no node \"{id}\"");
                else if (node.Value<string>("kind") != "timeline") Add("unknown_ref", path, $"\"{id}\" isn't a timeline");
                else return node;
                return null;
            }

            void Metric(JToken rule, string path)
            {
                string op = rule.Value<string>("op"), probe = rule.Value<string>("probe");
                if (op == null || !ArtifactSchema.MetricOps.Contains(op)) Add("bad_value", path + ".op", $"unknown op \"{op}\"");
                if (probe != null && !ArtifactSchema.Probes.Contains(probe)) Add("bad_value", path + ".probe", $"unknown probe \"{probe}\"");
                if (rule["zones"] != null) ZoneRef(rule.Value<string>("zones"), path + ".zones");
                var targets = rule["nodes"] as JArray ?? (rule["nodes"] != null ? new JArray(rule["nodes"]) : new JArray());
                foreach (var t in targets)
                {
                    string target = (string)t;
                    if (target == null || !nodes.ContainsKey(target)) Add("unknown_ref", path + ".nodes", $"no node \"{target}\"");
                }
                if (op == "sim") SimMetric(rule, path);
            }

            void SimMetric(JToken rule, string path)
            {
                string sim = rule.Value<string>("sim"), value = rule.Value<string>("value");
                if (sim == null || !nodes.TryGetValue(sim, out var node) || node.Value<string>("kind") != "sim")
                {
                    Add("unknown_ref", path + ".sim", $"no sim node \"{sim}\"");
                    return;
                }
                string solver = node.Value<string>("solver");
                if (solver != null && ArtifactSchema.Solvers.TryGetValue(solver, out var values) && System.Array.IndexOf(values, value) < 0)
                    Add("unknown_ref", path + ".value", $"{solver} has no value \"{value}\"");
            }

            void ZoneRef(string id, string path)
            {
                if (id == null || !nodes.TryGetValue(id, out var node)) Add("unknown_ref", path, $"no node \"{id}\"");
                else if (!IsZone(node)) Add("unknown_ref", path, $"\"{id}\" isn't a zone");
            }

            void Cycles()
            {
                foreach (var start in nodes)
                {
                    var seen = new HashSet<string> { start.Key };
                    for (string at = start.Value.Value<string>("parent"); at != null && nodes.TryGetValue(at, out var next); at = next.Value<string>("parent"))
                        if (!seen.Add(at))
                        {
                            Add("bad_value", "nodes", $"\"{start.Key}\" is its own ancestor");
                            break;
                        }
                }
            }

            void RunBlock()
            {
                if (!(scene["run"] is JObject run)) return;
                if (Permission("things") != "read") Add("permission_missing", "permissions.things", "the scene has a run, so permissions.things must be read");
                ZoneRef(run["start"]?.Value<string>("zone"), "run.start.zone");
                ZoneRef(run["finish"]?.Value<string>("zone"), "run.finish.zone");
                if (run["finish"]?["needs"] is JArray needs)
                    foreach (var need in needs)
                    {
                        string metric = need.Value<string>("metric"), min = need["min"]?.Type == JTokenType.String ? (string)need["min"] : null;
                        if (metric == null || !metrics.Contains(metric)) Add("unknown_ref", "run.finish.needs", $"no metric \"{metric}\"");
                        if (min != null && min.StartsWith("@") && !slots.Contains(min.Substring(1))) Add("unknown_slot", "run.finish.needs", $"no slot \"{min.Substring(1)}\"");
                    }
                if (run["fail"] is JArray fails)
                    for (int i = 0; i < fails.Count; i++)
                    {
                        if (fails[i].Value<string>("reason") == null) Add("bad_value", $"run.fail[{i}]", "needs a reason");
                        string probe = fails[i].Value<string>("probe");
                        if (probe != null && !ArtifactSchema.Probes.Contains(probe)) Add("bad_value", $"run.fail[{i}].probe", $"unknown probe \"{probe}\"");
                    }
            }

            void Outputs()
            {
                if (!(manifest["outputs"] is JArray outputs)) return;
                foreach (var output in outputs)
                {
                    string name = output.Value<string>("name");
                    if (name != null && !metrics.Contains(name)) Add("output_mismatch", "outputs", $"\"{name}\" has no metric rule");
                }
            }

            void InputRefs(JToken token, string path)
            {
                if (token is JObject o)
                {
                    foreach (var p in o.Properties())
                        if (!FreeText.Contains(p.Name)) InputRefs(p.Value, path + "." + p.Name);
                }
                else if (token is JArray a)
                {
                    for (int i = 0; i < a.Count; i++) InputRefs(a[i], $"{path}[{i}]");
                }
                else if (token?.Type == JTokenType.String)
                {
                    string s = (string)token;
                    if (InputRef.IsMatch(s) && !inputs.Contains(s.Substring(1))) Add("unknown_input", path, $"no input \"{s.Substring(1)}\"");
                }
            }

            void OneOf(JObject o, string key, HashSet<string> allowed, string path)
            {
                string value = o.Value<string>(key);
                if (value == null || !allowed.Contains(value)) Add("bad_value", path, $"\"{value}\" isn't one of {string.Join(", ", allowed)}");
            }
        }
    }
}
