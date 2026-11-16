using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Forge.Artifacts
{
    /// <summary>One thing wrong with an artifact. Returned as data so the agent can fix it (C-12 section 9).</summary>
    public sealed class ArtifactError
    {
        public string code, path, message;
        /// <summary>What to write instead, when the validator can tell. Null otherwise.</summary>
        public string hint;
        /// <summary>The values this field takes, for an enum or a reference. Null otherwise.</summary>
        public string[] allowed;
        public ArtifactError(string code, string path, string message, string hint = null, string[] allowed = null)
        {
            this.code = code;
            this.path = path;
            this.message = message;
            this.hint = hint;
            this.allowed = allowed;
        }
        public override string ToString() => string.IsNullOrEmpty(path) ? $"{code}: {message}" : $"{code}: {path}: {message}";
    }

    /// <summary>
    /// The device validator (C-12 section 9). It runs before anything is shown and needs no network. It only reads
    /// JSON, so it knows nothing about Unity scenes. Asset hashes are checked when assets load, not here.
    /// The agent writes these files, so every field is read through a guarded accessor: a wrong shape is an error at
    /// that field's own path, and the pass goes on. Only a newer schema stops it.
    /// </summary>
    public static class ArtifactValidator
    {
        static readonly Regex IdRule = new Regex("^[a-z0-9_-]+$");
        static readonly Regex NodeIdRule = new Regex("^[a-z0-9_]+$");
        static readonly Regex InputRef = new Regex(@"^\$[a-z][a-z0-9_]*$");
        static readonly Regex InputName = new Regex("^[a-z][a-z0-9_]*$");
        static readonly Regex SourceRule = new Regex(@"^(builtin:[a-z0-9_]+|connector:[a-z0-9_]+/[a-z0-9_]+)$");
        static readonly Regex SnapshotRule = new Regex(@"^data/[a-z0-9_-]+\.json$");
        static readonly Regex SceneFile = new Regex(@"^[a-z0-9_-]+\.json$");
        static readonly Regex HashRule = new Regex("^[0-9a-fA-F]{64}$");
        static readonly Regex ColorRule = new Regex("^#[0-9a-fA-F]{6}$");
        static readonly HashSet<string> FreeText = new HashSet<string> { "text", "title", "label", "alt_label", "after_run" };

        // Common slips, so the agent gets a fix and not just a refusal. These read as "did you mean ...?".
        static readonly Dictionary<string, string> KindHints = new Dictionary<string, string>
        {
            { "cube", "primitive with shape box" }, { "box", "primitive with shape box" },
            { "sphere", "primitive with shape sphere" }, { "cylinder", "primitive with shape cylinder" },
            { "cone", "primitive with shape cone" }, { "capsule", "primitive with shape capsule" }, { "plane", "primitive with shape plane" },
            { "label", "text" }, { "model", "gltf" }, { "mesh", "gltf" },
        };

        // Kinds a model reaches for that have no direct match: what to build instead.
        static readonly Dictionary<string, string> KindAdvice = BuildKindAdvice();

        static Dictionary<string, string> BuildKindAdvice()
        {
            var advice = new Dictionary<string, string>();
            void Say(string text, params string[] kinds) { foreach (var k in kinds) advice[k] = text; }
            Say("there's no group kind: make the base part (a primitive) the parent of the others, with \"parent\": \"<id>\"",
                "group", "node", "empty", "container", "transform", "object", "assembly", "root");
            Say("use a primitive with \"glow\": true", "light", "lamp", "bulb", "led");
            Say("controls live on a panel node: {\"kind\": \"panel\", \"widgets\": [{\"type\": \"button\", \"id\": \"go\", \"label\": \"Go\"}]}, with an on_input rule; a slider is {\"type\": \"slider\", \"input\": \"<manifest input>\"}",
                "button", "slider", "ui", "widget", "control", "controls", "switch", "knob");
            Say("use a line, with points [[x,y,z], ...] or through [node ids]", "arrow", "rope", "rod", "spring", "wire", "cable", "path");
            Say("use a line with follow: \"<node id>\"", "trail");
            Say("use a timeline node plus animate rules", "clock", "animation", "timer", "tween");
            Say("use a sim node with solver n_body or pendulum", "physics", "rigidbody", "body");
            Say("use a text node", "label", "title", "sign");
            foreach (var pair in KindHints) advice[pair.Key] = "use " + pair.Value;
            return advice;
        }

        // ---------- unknown fields: what the agent meant, from general vocabulary (no request-specific words) ----------

        /// <summary>
        /// Normalised slip (lowercase, no _ or -) to the real keys it may stand for, in order. The first one the object
        /// allows is offered.
        /// </summary>
        static readonly Dictionary<string, List<string>> FieldRenames = BuildRenames();

        static Dictionary<string, List<string>> BuildRenames()
        {
            var renames = new Dictionary<string, List<string>>();
            void To(string key, params string[] slips)
            {
                foreach (var s in slips)
                {
                    if (!renames.TryGetValue(s, out var list)) renames[s] = list = new List<string>();
                    list.Add(key);
                }
            }
            To("pos", "position", "location", "translate", "translation", "offset", "xyz", "center", "centre", "localposition");
            To("rot", "rotation", "rotate", "euler", "eulerangles", "angles", "angle", "orientation", "localrotation");
            To("scale", "scaling", "localscale");
            To("size", "dimensions", "dimension", "dims", "extents", "extent", "radius", "diameter", "width", "height", "depth", "length", "bounds");
            To("color", "colour", "material", "fill", "tint", "albedo", "rgb", "hex");
            To("glow", "emissive", "emission", "emit", "light", "lit", "glowing", "emissiveintensity", "intensity", "bright");
            To("kind", "type", "nodetype", "class");
            To("duration_s", "duration", "seconds", "length", "time", "period");
            To("loop", "repeat", "looping", "loops", "repeats");
            To("autoplay", "autostart", "playonstart", "startonload", "auto");
            To("title", "name", "label", "heading", "caption");
            To("text", "content", "string", "value", "label", "caption");
            To("parent", "parentid", "attach", "attachto", "attachedto");
            To("collider", "collision", "solid");
            To("billboard", "facecamera", "facesuser", "lookat");
            To("widgets", "controls", "buttons", "items");
            To("input", "inputs", "variable", "bind", "binding");
            To("do", "actions", "then", "effects", "steps");
            To("keys", "keyframes", "frames", "values");
            To("target", "node", "object");
            To("property", "prop", "field", "channel");
            To("timeline", "clock", "timer");
            To("solver", "physics", "integrator");
            To("bodies", "objects", "masses");
            To("points", "vertices", "path", "positions");
            To("through", "connect", "connects", "between");
            To("follow", "trail", "track");
            To("min", "minimum", "from", "low");
            To("max", "maximum", "to", "high");
            To("default", "initial", "start", "value");
            return renames;
        }

        /// <summary>Normalised slip to advice, for keys with no direct counterpart in this object.</summary>
        static readonly Dictionary<string, string> FieldAdvice = BuildFieldAdvice();

        const string NoScripts = "no scripts: a button is a panel widget {\"type\": \"button\", \"id\": \"go\", \"label\": \"Go\"} plus an on_input rule; motion is a timeline with animate rules, or a sim node";

        static Dictionary<string, string> BuildFieldAdvice()
        {
            var advice = new Dictionary<string, string>();
            void Say(string text, params string[] slips) { foreach (var s in slips) advice[s] = text; }
            Say(NoScripts, "onclick", "onpress", "ontap", "onselect", "ontouch", "onhover", "action", "actions", "script", "scripts", "code",
                "onupdate", "update", "behavior", "behaviour", "behaviors", "behaviours", "logic", "events", "handlers", "interactions", "interaction", "onstart", "program");
            Say("nodes are one flat list: give each child \"parent\": \"<this node's id>\"", "children", "nodes", "parts", "components", "child", "subnodes", "contents");
            Say("controls go on a panel node: {\"kind\": \"panel\", \"widgets\": [{\"type\": \"slider\", \"input\": \"<manifest input>\"}]}; a button also needs an on_input rule",
                "slider", "sliders", "button", "buttons", "controls", "control", "ui", "widgets", "widget", "knob", "switch", "toggle");
            Say("motion is an animate rule {\"type\": \"animate\", \"target\": \"<id>\", \"property\": \"rot\", \"keys\": [[0, [0, 0, 0]], [2, [0, 360, 0]]], \"timeline\": \"<timeline id>\"}",
                "animation", "animations", "animate", "keyframes", "tween", "motion", "spin", "rotationspeed", "speed", "velocity", "angularvelocity", "move", "movement");
            Say("not a field: say it in a note widget on a panel, or a text node", "description", "notes", "note", "summary", "author", "tags", "comment", "comments", "about", "info");
            Say("a name shown in the scene is a text node with \"parent\": \"<this node's id>\"", "name", "label", "caption", "title");
            Say("motion under gravity or a swing is a sim node {\"kind\": \"sim\", \"solver\": \"pendulum\", \"bodies\": [{\"node\": \"<id>\", \"length\": 0.2}]}", "mass", "gravity", "rigidbody", "physics", "weight");
            Say("only bundled models load: a gltf node with an \"asset\" listed in the manifest's assets", "mesh", "model", "url", "src", "file");
            return advice;
        }

        static string Norm(string key)
        {
            var sb = new System.Text.StringBuilder(key.Length);
            foreach (char c in key)
                if (c != '_' && c != '-' && c != ' ') sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        static int Distance(string a, string b)
        {
            var row = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) row[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                int diagonal = row[0];
                row[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int above = row[j];
                    row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                    diagonal = above;
                }
            }
            return row[b.Length];
        }

        /// <summary>The allowed key the agent most likely meant, or null.</summary>
        static string Suggest(string key, string[] allowed)
        {
            string norm = Norm(key);
            foreach (var a in allowed) if (Norm(a) == norm) return a;  // a case or underscore slip
            if (FieldRenames.TryGetValue(norm, out var candidates))
                foreach (var to in candidates)
                    if (System.Array.IndexOf(allowed, to) >= 0) return to;
            if (FieldAdvice.ContainsKey(norm)) return null;  // the advice says more than a near spelling would
            string best = null;
            int bestDistance = norm.Length <= 4 ? 1 : 2;
            foreach (var a in allowed)
            {
                int d = Distance(norm, Norm(a));
                if (d <= bestDistance && (best == null || d < Distance(norm, Norm(best)))) { best = a; bestDistance = d; }
            }
            return best;
        }

        /// <summary>The most a tool result carries; the rest is counted, not listed.</summary>
        public const int MaxReported = 25;

        public static List<ArtifactError> Validate(JObject manifest, JObject scene)
        {
            var v = new Pass { manifest = manifest, scene = scene };
            v.Run();
            return Sorted(v.errors);
        }

        /// <summary>Structural first (schema, kinds, fields), then references, then values. Stable within each group.</summary>
        static List<ArtifactError> Sorted(List<ArtifactError> errors)
        {
            var sorted = new List<ArtifactError>(errors.Count);
            for (int rank = 0; rank < 4; rank++)
                foreach (var e in errors)
                    if (Rank(e) == rank) sorted.Add(e);
            return sorted;
        }

        public static int Rank(ArtifactError e)
        {
            switch (e.code)
            {
                case "newer_schema": return 0;
                case "unknown_kind": case "unsupported_kind": case "unknown_field": return 1;
                case "unknown_ref": case "unknown_slot": case "unknown_input": case "asset_not_listed": case "output_mismatch": return 2;
                default: return e.path == "schema" || e.path == "scene.schema" ? 0 : 3;
            }
        }

        static string[] Values(IEnumerable<string> set)
        {
            var list = new List<string>(set);
            return list.ToArray();
        }

        internal static string Slug(string title)
        {
            if (string.IsNullOrEmpty(title)) return null;
            var sb = new System.Text.StringBuilder();
            foreach (char c in title.ToLowerInvariant())
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
            return sb.ToString().Trim('_');
        }

        // ---------- guarded reads: a wrong type gives null, never an exception ----------

        static JToken Get(JToken o, string key) => o is JObject j ? j[key] : null;
        static bool Absent(JToken t) => t == null || t.Type == JTokenType.Null;
        static bool IsNumber(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float);
        static string Str(JToken o, string key) => Get(o, key) is JToken t && t.Type == JTokenType.String ? (string)t : null;

        static double? Num(JToken t)
        {
            if (!IsNumber(t)) return null;
            object raw = ((JValue)t).Value;
            if (raw is System.Numerics.BigInteger big) return (double)big;
            return Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        }

        static double? Num(JToken o, string key) => Num(Get(o, key));

        static bool Whole(JToken t) => Num(t) is double d && Math.Abs(d - Math.Round(d)) < 1e-9;

        /// <summary>A short, quotable picture of what the agent wrote, for messages.</summary>
        static string Describe(JToken t)
        {
            if (t == null) return "nothing";
            string text = t.ToString(Formatting.None);
            if (text.Length > 40) text = text.Substring(0, 37) + "...";
            switch (t.Type)
            {
                case JTokenType.Object: return "an object " + text;
                case JTokenType.Array: return "a list " + text;
                case JTokenType.Null: return "null";
                default: return text;
            }
        }

        sealed class Pass
        {
            public JObject manifest, scene;
            public readonly List<ArtifactError> errors = new List<ArtifactError>();
            readonly HashSet<string> inputs = new HashSet<string>(), numericInputs = new HashSet<string>();
            readonly HashSet<string> slots = new HashSet<string>(), metrics = new HashSet<string>(), buttons = new HashSet<string>();
            readonly HashSet<string> tables = new HashSet<string>(), assets = new HashSet<string>();
            readonly Dictionary<string, JObject> nodes = new Dictionary<string, JObject>();
            readonly Dictionary<string, JObject> inputDefs = new Dictionary<string, JObject>();
            readonly Dictionary<string, string> buttonPaths = new Dictionary<string, string>();

            void Add(string code, string path, string message, string hint = null, string[] allowed = null) =>
                errors.Add(new ArtifactError(code, path, message, hint, allowed));

            /// <summary>Up to 40 names, for the allowed list of a reference; a longer list helps no one.</summary>
            static string[] Names(IEnumerable<string> set)
            {
                var list = new List<string>();
                foreach (var s in set) { if (list.Count == 40) break; list.Add(s); }
                return list.ToArray();
            }

            string[] NodesOfKind(string kind)
            {
                var list = new List<string>();
                foreach (var pair in nodes) if (kind == null || Str(pair.Value, "kind") == kind) list.Add(pair.Key);
                return Names(list);
            }

            public void Run()
            {
                if (manifest == null) { Add("bad_value", "manifest", "missing or not a JSON object"); return; }
                if (scene == null) { Add("bad_value", "scene", "missing or not a JSON object"); return; }
                bool newer = Schema(manifest, ArtifactSchema.Manifest, "schema") | Schema(scene, ArtifactSchema.Scene, "scene.schema");
                if (newer) return;  // a newer file can't be judged by this version's rules; a missing schema can
                Manifest();
                Permissions();
                Scene();
                Fields();
            }

            // ---------- unknown fields (strict allow-lists, ArtifactSchema.Keys) ----------

            /// <summary>Each key of <paramref name="o"/> outside <paramref name="allowed"/> is unknown_field, with what was meant.</summary>
            void Keys(JToken o, string[] allowed, string path, string what, string label = null)
            {
                if (!(o is JObject obj)) return;
                foreach (var p in obj.Properties())
                {
                    if (System.Array.IndexOf(allowed, p.Name) >= 0) continue;
                    string at = string.IsNullOrEmpty(path) ? p.Name : path + "." + p.Name;
                    string meant = Suggest(p.Name, allowed);
                    string takes = $"{label ?? what} takes: {string.Join(", ", allowed)}";
                    string hint;
                    if (meant != null) hint = $"did you mean \"{meant}\"? {takes}";
                    else if (FieldAdvice.TryGetValue(Norm(p.Name), out var advice)) hint = $"{advice}. {takes}";
                    else hint = $"remove it; {takes}";
                    Add("unknown_field", at, $"{what} has no field \"{p.Name}\"", hint, allowed);
                }
            }

            static string[] Join(string[] a, string[] b)
            {
                var all = new string[a.Length + b.Length];
                a.CopyTo(all, 0);
                b.CopyTo(all, a.Length);
                return all;
            }

            static string[] Union(IEnumerable<string[]> tables)
            {
                var seen = new List<string>();
                foreach (var t in tables) foreach (var k in t) if (!seen.Contains(k)) seen.Add(k);
                return seen.ToArray();
            }

            void EachObject(JToken list, string path, string[] allowed, string what)
            {
                if (!(list is JArray a)) return;
                for (int i = 0; i < a.Count; i++) Keys(a[i], allowed, $"{path}[{i}]", $"{path}[{i}]", what);
            }

            void Fields()
            {
                Keys(manifest, ArtifactSchema.Keys.Manifest, "", "the manifest");
                Keys(manifest["links"], ArtifactSchema.Keys.Links, "links", "links");
                Keys(manifest["runtime"], ArtifactSchema.Keys.Runtime, "runtime", "runtime");
                if (manifest["inputs"] is JArray inputList)
                    for (int i = 0; i < inputList.Count; i++)
                    {
                        var allowed = ArtifactSchema.Keys.Input;
                        if (Str(inputList[i], "type") != "choice") allowed = System.Array.FindAll(allowed, key => key != "options");
                        Keys(inputList[i], allowed, $"inputs[{i}]", $"inputs[{i}]", Str(inputList[i], "type") == "choice" ? "a choice input" : "an input");
                    }
                EachObject(manifest["scenarios"], "scenarios", ArtifactSchema.Keys.Scenario, "a scenario");
                EachObject(manifest["data"], "data", ArtifactSchema.Keys.Data, "a data entry");
                EachObject(manifest["assets"], "assets", ArtifactSchema.Keys.Asset, "an asset");
                EachObject(manifest["outputs"], "outputs", ArtifactSchema.Keys.Output, "an output");

                Keys(scene, ArtifactSchema.Keys.Scene, "scene", "the scene");
                Keys(scene["view"], ArtifactSchema.Keys.View, "scene.view", "view");
                if (scene["generators"] is JArray generators)
                    for (int i = 0; i < generators.Count; i++)
                    {
                        string at = $"generators[{i}]";
                        Keys(generators[i], ArtifactSchema.Keys.Generator, at, "a generator");
                        string type = Str(generators[i], "type");
                        if (type != null && ArtifactSchema.Keys.GeneratorParams.TryGetValue(type, out var paramKeys))
                            Keys(Get(generators[i], "params"), paramKeys, at + ".params", $"{type} params");
                    }
                if (scene["nodes"] is JArray nodeList)
                    for (int i = 0; i < nodeList.Count; i++)
                        if (nodeList[i] is JObject node) NodeFields(node, $"nodes[{i}]");
                if (scene["rules"] is JArray rules)
                    for (int i = 0; i < rules.Count; i++)
                    {
                        string at = $"rules[{i}]", type = Str(rules[i], "type");
                        if (type == null || !ArtifactSchema.Keys.Rule.TryGetValue(type, out var ruleKeys)) continue;  // an unknown rule is already refused
                        if (type == "metric")
                        {
                            string op = Str(rules[i], "op");
                            ruleKeys = op != null && ArtifactSchema.Keys.MetricOp.TryGetValue(op, out var opKeys)
                                ? Join(ruleKeys, opKeys) : Join(ruleKeys, Union(ArtifactSchema.Keys.MetricOp.Values));
                        }
                        Keys(rules[i], ruleKeys, at, $"a {type} rule");
                        EachObject(Get(rules[i], "do"), at + ".do", ArtifactSchema.Keys.Action, "an action");
                    }
                if (scene["run"] is JObject run)
                {
                    Keys(run, ArtifactSchema.Keys.Run, "run", "run");
                    Keys(run["start"], ArtifactSchema.Keys.RunStart, "run.start", "run.start");
                    Keys(run["finish"], ArtifactSchema.Keys.RunFinish, "run.finish", "run.finish");
                    EachObject(Get(run["finish"], "needs"), "run.finish.needs", ArtifactSchema.Keys.Need, "a need");
                    EachObject(run["fail"], "run.fail", ArtifactSchema.Keys.Fail, "a fail");
                }
            }

            void NodeFields(JObject n, string path)
            {
                string kind = Str(n, "kind"), id = Str(n, "id");
                if (kind == null || !ArtifactSchema.NodeKinds.Contains(kind)) return;  // an unknown kind is refused first; its fields can't be judged
                string what = $"{path} ({kind}{(id != null ? " \"" + id + "\"" : "")})";
                string[] allowed;
                if (!ArtifactSchema.Keys.NodeOnly.TryGetValue(kind, out allowed))
                {
                    allowed = Join(ArtifactSchema.Keys.NodeCommon, ArtifactSchema.Keys.NodeOwn[kind]);
                    if (kind == "array") allowed = System.Array.FindAll(allowed, key => key != "at");
                    if (kind == "line" && Absent(n["follow"])) allowed = System.Array.FindAll(allowed, key => key != "length");
                }
                Keys(n, allowed, path, what, (kind == "array" ? "an " : "a ") + kind);

                switch (kind)
                {
                    case "panel":
                        if (n["widgets"] is JArray widgets)
                            for (int i = 0; i < widgets.Count; i++)
                            {
                                string type = Str(widgets[i], "type");
                                if (type != null && ArtifactSchema.Keys.Widget.TryGetValue(type, out var widgetKeys))
                                    Keys(widgets[i], widgetKeys, $"{path}.widgets[{i}]", $"a {type} widget");
                            }
                        break;
                    case "sim":
                        string solver = Str(n, "solver");
                        bool known = solver != null && ArtifactSchema.Keys.Bodies.ContainsKey(solver);
                        var bodyKeys = known ? ArtifactSchema.Keys.Bodies[solver] : Union(ArtifactSchema.Keys.Bodies.Values);
                        EachObject(n["bodies"], path + ".bodies", bodyKeys, known ? "a " + solver + " body" : "a body");
                        Keys(n["params"], known ? ArtifactSchema.Keys.SimParams[solver] : Union(ArtifactSchema.Keys.SimParams.Values), path + ".params", (known ? solver : "sim") + " params");
                        break;
                    case "array":
                        string templateKind = Str(n["template"], "kind");
                        if (templateKind != null && ArtifactSchema.Keys.ArrayTemplate.TryGetValue(templateKind, out var templateKeys))
                            Keys(n["template"], templateKeys, path + ".template", $"an array's {templateKind} template");
                        if (n["source"] is JObject source)
                        {
                            Keys(source, ArtifactSchema.Keys.TableSource, path + ".source", "a table source");
                            Keys(source["grid"], ArtifactSchema.Keys.Grid, path + ".source.grid", "grid");
                        }
                        if (n["map"] is JObject map)
                        {
                            Keys(map, ArtifactSchema.Keys.Map, path + ".map", "map");
                            foreach (var entry in map.Properties())
                                Keys(entry.Value, ArtifactSchema.Keys.MapEntry, $"{path}.map.{entry.Name}", $"map.{entry.Name}");
                        }
                        break;
                }
            }

            // True only for a newer major version. A missing or wrong schema is reported and the file is read as /1.
            bool Schema(JObject o, string expected, string path)
            {
                var token = o["schema"];
                string found = token?.Type == JTokenType.String ? (string)token : null;
                int slash = expected.LastIndexOf('/');
                string name = expected.Substring(0, slash + 1);
                if (found == null || !found.StartsWith(name))
                {
                    Add("bad_value", path, $"must be {expected}, found {(Absent(token) ? "nothing" : Describe(token))}",
                        $"add \"schema\": \"{expected}\"", new[] { expected });
                    return false;
                }
                if (!int.TryParse(found.Substring(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int major))
                {
                    Add("bad_value", path, $"must be {expected}, found {found}", $"use \"schema\": \"{expected}\"", new[] { expected });
                    return false;
                }
                if (major > int.Parse(expected.Substring(slash + 1)))
                {
                    Add("newer_schema", path, $"{found}: this device reads up to {expected}");
                    return true;
                }
                return false;
            }

            // ---------- typed field checks; absent is fine unless the caller says otherwise ----------

            bool WrongType(JToken o, string key, string path, Func<JToken, bool> ok, string wants, string hint = null)
            {
                var t = Get(o, key);
                if (Absent(t) || ok(t)) return false;
                Add("bad_value", string.IsNullOrEmpty(path) ? key : path + "." + key, $"must be {wants}, found {Describe(t)}", hint);
                return true;
            }

            bool StringField(JToken o, string key, string path) => WrongType(o, key, path, t => t.Type == JTokenType.String, "a string");
            bool BoolField(JToken o, string key, string path) =>
                WrongType(o, key, path, t => t.Type == JTokenType.Boolean, "true or false", $"write \"{key}\": true or \"{key}\": false, without quotes");
            bool ColorField(JToken o, string key, string path) =>
                WrongType(o, key, path, t => t.Type == JTokenType.String && ColorRule.IsMatch((string)t), "a colour as \"#RRGGBB\"",
                    "six hex digits, like \"#4D8DFF\"; names and short forms aren't read");
            bool NumberField(JToken o, string key, string path) =>
                WrongType(o, key, path, NumberOrRef, "a number or an \"$input\"", "a plain number like 0.05, or \"$name\" of a manifest input; no expressions");
            bool Vec3Field(JToken o, string key, string path) =>
                WrongType(o, key, path, Vec3, "[x, y, z]: three numbers or \"$input\"s",
                    key == "scale" ? "a list of three, like [1, 1, 1]; for a primitive's size in metres use \"size\"" : "a list of three, like [0, 0.1, 0], in metres (rot in degrees); each may be \"$name\" of an input, no expressions");
            bool ObjectField(JToken o, string key, string path) => WrongType(o, key, path, t => t.Type == JTokenType.Object, "an object");
            bool ListField(JToken o, string key, string path) => WrongType(o, key, path, t => t.Type == JTokenType.Array, "a list");

            static bool NumberOrRef(JToken t) => IsNumber(t) || (t.Type == JTokenType.String && InputRef.IsMatch((string)t));
            static bool Vec3(JToken t) => t is JArray a && a.Count == 3 && NumberOrRef(a[0]) && NumberOrRef(a[1]) && NumberOrRef(a[2]);

            /// <summary>The list at <paramref name="key"/>, or null. A non-list is reported; so is every entry that isn't an object.</summary>
            JArray ObjectList(JToken o, string key, string path, string what)
            {
                var t = Get(o, key);
                if (Absent(t)) return null;
                if (!(t is JArray list))
                {
                    Add("bad_value", path, $"must be a list of {what}, found {Describe(t)}");
                    return null;
                }
                for (int i = 0; i < list.Count; i++)
                    if (!(list[i] is JObject)) Add("bad_value", $"{path}[{i}]", $"must be {what}, found {Describe(list[i])}");
                return list;
            }

            void Manifest()
            {
                var idToken = manifest["id"];
                string id = Str(manifest, "id");
                string slug = Slug(Str(manifest, "title") ?? Str(manifest, "name"));
                if (id == null || !IdRule.IsMatch(id))
                    Add("bad_value", "id", "use lowercase letters, digits, _ and -" + (Absent(idToken) || id != null ? "" : $", found {Describe(idToken)}"),
                        string.IsNullOrEmpty(slug) ? "add \"id\": a short lowercase name with _ between words" : $"add \"id\": \"{slug}\"");
                if (!StringField(manifest, "title", "") && string.IsNullOrEmpty(Str(manifest, "title")))
                    Add("bad_value", "title", "required", Str(manifest, "name") != null ? $"the name goes in \"title\": \"{Str(manifest, "name")}\"" : "add \"title\": the name the person sees");
                var version = manifest["version"];
                if (version?.Type != JTokenType.Integer || Num(version) < 1)
                    Add("bad_value", "version", "must be an integer from 1", "use \"version\": 1 (an integer, not \"1.0.0\"); saving sets the real one");
                OneOf(manifest, "kind", ArtifactSchema.Kinds, "kind", "use \"standalone\" for something on its own, or \"project\" to link it to the open notebook project");
                OneOf(manifest, "category", ArtifactSchema.Categories, "category", "\"sim\" if it has a sim node, \"tool\" for something you operate, \"viz\" for something you look at");

                var sceneFile = manifest["scene"];
                if (Absent(sceneFile)) Add("bad_value", "scene", "required: the scene's file name, \"scene.json\"", "add \"scene\": \"scene.json\" to the manifest", new[] { "scene.json" });
                else if (sceneFile.Type != JTokenType.String || !SceneFile.IsMatch((string)sceneFile))
                    Add("bad_value", "scene", $"must be a plain file name like \"scene.json\", found {Describe(sceneFile)}", "use \"scene\": \"scene.json\"", new[] { "scene.json" });

                if (!Absent(manifest["script"]))
                    Add("script_not_allowed", "script", "script must be null in Phase 1",
                        "no scripts: a button is a panel widget plus an on_input rule; motion is a timeline with animate rules, or a sim node");

                var runtimeToken = manifest["runtime"];
                if (!Absent(runtimeToken) && !(runtimeToken is JObject))
                    Add("bad_value", "runtime", $"must be an object {{\"type\": \"scene\"}}, found {Describe(runtimeToken)}", "use \"runtime\": {\"type\": \"scene\"}");
                else if (!(runtimeToken is JObject runtime) || Str(runtime, "type") != "scene")
                    Add("bad_value", "runtime.type", "must be scene in Phase 1", "use \"runtime\": {\"type\": \"scene\"}", new[] { "scene" });
                else
                {
                    var min = runtime["min_runtime"];
                    if (!Absent(min) && !Whole(min)) Add("bad_value", "runtime.min_runtime", $"must be a whole number, found {Describe(min)}");
                    else if (Num(min) > ArtifactSchema.HostRuntime)
                        Add("newer_schema", "runtime.min_runtime", $"needs host {min}, this device runs {ArtifactSchema.HostRuntime}");
                }

                if (!ObjectField(manifest, "links", "") && Str(manifest, "kind") == "standalone" && manifest["links"] is JObject links)
                    foreach (var link in links.Properties())
                        if (link.Value.Type != JTokenType.Null) Add("bad_value", "links." + link.Name, "a standalone artifact has no links");
                if (!StringField(manifest, "template", "") && Str(manifest, "template") is string madeFrom && System.Array.IndexOf(ArtifactLibrary.Templates, madeFrom) < 0)
                    Add("bad_value", "template", $"no template \"{madeFrom}\"", "a written artifact has \"template\": null; only one made from a template names it", ArtifactLibrary.Templates);

                Inputs();

                var scenarios = manifest["scenarios"];
                if (!(scenarios is JArray scenarioList) || scenarioList.Count == 0)
                    Add("bad_value", "scenarios", "at least one is required", "use \"scenarios\": [{\"id\": \"default\", \"seed\": 0, \"params\": {}}]");
                else
                {
                    ObjectList(manifest, "scenarios", "scenarios", "an object like {\"id\": \"default\"}");
                    for (int i = 0; i < scenarioList.Count; i++) ScenarioParams(scenarioList[i], $"scenarios[{i}]");
                }

                var data = ObjectList(manifest, "data", "data", "an object {name, source}");
                if (data != null)
                    for (int i = 0; i < data.Count; i++)
                    {
                        if (!(data[i] is JObject entry)) continue;
                        string name = Str(entry, "name"), source = Str(entry, "source");
                        if (name != null) tables.Add(name);
                        if (source == null || !SourceRule.IsMatch(source))
                            Add("bad_value", $"data[{i}].source", "use builtin:<name> or connector:<connector>/<tool>");
                        else if (source.StartsWith("connector:") && !Asks(ArtifactData.Permission(source)))
                            Add("permission_missing", $"data[{i}].source", $"{source} needs \"{ArtifactData.Permission(source)}\" in permissions.connectors");
                        if (!StringField(entry, "snapshot", $"data[{i}]"))
                        {
                            string snapshot = Str(entry, "snapshot");
                            if (snapshot != null && !SnapshotRule.IsMatch(snapshot))
                                Add("bad_value", $"data[{i}].snapshot", "use data/<name>.json");
                        }
                    }

                var list = ObjectList(manifest, "assets", "assets", "an object {path, sha256, license}");
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (!(list[i] is JObject entry)) continue;
                        string path = Str(entry, "path"), at = $"assets[{i}]";
                        if (path == null || !ArtifactPackage.SafeAsset.IsMatch(path)) Add("bad_value", at + ".path", "use assets/<name>.glb");
                        else if (!assets.Add(path)) Add("bad_value", at + ".path", $"\"{path}\" is listed twice");
                        if (!HashRule.IsMatch(Str(entry, "sha256") ?? "")) Add("bad_value", at + ".sha256", "needs the file's sha256, as 64 hex digits");
                        if (string.IsNullOrEmpty(Str(entry, "license"))) Add("bad_value", at + ".license", "required (CC0, CC-BY-4.0, own)");
                    }
            }

            void Inputs()
            {
                var list = ObjectList(manifest, "inputs", "inputs", "an object {name, type, min, max, default}");
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    if (!(list[i] is JObject input)) continue;
                    string path = $"inputs[{i}]", name = Str(input, "name"), type = Str(input, "type");
                    if (string.IsNullOrEmpty(name)) { Add("bad_value", path, "needs a name", "like {\"name\": \"speed\", \"type\": \"number\", \"min\": 0, \"max\": 1, \"default\": 0.5}"); continue; }
                    if (!InputName.IsMatch(name))
                        Add("bad_value", path + ".name", $"\"{name}\": use a lowercase letter, then lowercase letters, digits and _", Slug(name) is string fixedName && fixedName.Length > 0 && char.IsLetter(fixedName[0]) ? $"use \"{fixedName}\"" : "like \"speed\"");
                    if (!inputs.Add(name)) { Add("bad_value", path + ".name", $"\"{name}\" is used twice", "each input needs its own name"); continue; }
                    inputDefs[name] = input;
                    if (type == null || !ArtifactSchema.InputTypes.Contains(type))
                    {
                        Add("bad_value", path, $"unknown type {Describe(input["type"])}", "a slider needs \"type\": \"number\" (or \"integer\") with min, max and default",
                            Values(ArtifactSchema.BuiltInputTypes));
                        continue;
                    }
                    if (!ArtifactSchema.BuiltInputTypes.Contains(type))
                    {
                        Add("bad_value", path + ".type", $"{type} inputs aren't built on this host yet, so nothing could set it",
                            type == "boolean" ? "use \"type\": \"integer\" with min 0, max 1, and a slider; or a button whose on_input rule sets <node>.visible"
                                              : "use \"type\": \"integer\" with min 0 and max N-1, one number per option, and a slider",
                            Values(ArtifactSchema.BuiltInputTypes));
                        continue;
                    }
                    numericInputs.Add(name);
                    bool typed = false;
                    foreach (string key in new[] { "min", "max", "default" })
                        typed |= WrongType(input, key, path, IsNumber, "a number");
                    if (typed) continue;
                    double? min = Num(input, "min"), max = Num(input, "max"), value = Num(input, "default");
                    if (min == null || max == null || value == null)
                    {
                        Add("bad_value", path, "needs min, max and default", $"like {{\"name\": \"{name}\", \"type\": \"{type}\", \"min\": 0, \"max\": 1, \"default\": 0.5}}");
                        continue;
                    }
                    if (type == "integer")
                        foreach (string key in new[] { "min", "max", "default" })
                            if (!Whole(input[key])) Add("bad_value", $"{path}.{key}", $"an integer input needs a whole number, found {Describe(input[key])}", "use a whole number, or \"type\": \"number\"");
                    if (min > max) Add("bad_value", path, $"min {min} is above max {max}");
                    if (value > max) Add("bad_value", path, $"default {value} is above max {max}");
                    if (value < min) Add("bad_value", path, $"default {value} is below min {min}");
                }
            }

            // A scenario's params replace input defaults, so each one names an input and stays in its range.
            void ScenarioParams(JToken scenario, string path)
            {
                var token = Get(scenario, "params");
                if (Absent(token)) return;
                if (!(token is JObject values)) { Add("bad_value", path + ".params", $"must be an object {{\"<input>\": <number>}}, found {Describe(token)}", "like \"params\": {}"); return; }
                foreach (var p in values.Properties())
                {
                    string at = $"{path}.params.{p.Name}";
                    if (!inputDefs.TryGetValue(p.Name, out var input))
                    {
                        Add("unknown_input", at, $"no input \"{p.Name}\"", "a scenario's params set manifest inputs by name", Names(inputs));
                        continue;
                    }
                    if (!numericInputs.Contains(p.Name)) continue;  // its type is already refused
                    double? v = Num(p.Value), min = Num(input, "min"), max = Num(input, "max");
                    if (v == null) { Add("bad_value", at, $"must be a number, found {Describe(p.Value)}", "a plain number inside the input's range"); continue; }
                    if (Str(input, "type") == "integer" && !Whole(p.Value)) Add("bad_value", at, $"{p.Name} is an integer, found {v}", "use a whole number");
                    if ((min != null && v < min) || (max != null && v > max)) Add("bad_value", at, $"{v} is outside {min} to {max}", $"use a value from {min} to {max}");
                }
            }

            void Permissions()
            {
                var token = manifest["permissions"];
                if (Absent(token)) return;
                if (!(token is JObject p))
                {
                    Add("bad_value", "permissions", $"must be an object like {{\"room\": \"none\"}}, found {Describe(token)}");
                    return;
                }
                foreach (var key in p.Properties())
                    if (!ArtifactSchema.Permissions.Contains(key.Name))
                        Add("permission_denied", "permissions." + key.Name, "not a permission in the schema", null, Values(ArtifactSchema.Permissions));
                foreach (var pair in ArtifactSchema.PermissionValues)
                {
                    var value = p[pair.Key];
                    if (Absent(value)) continue;
                    if (value.Type != JTokenType.String || System.Array.IndexOf(pair.Value, (string)value) < 0)
                        Add("bad_value", "permissions." + pair.Key, $"must be one of {string.Join(", ", pair.Value)}, found {Describe(value)}",
                            "leave a permission out when it isn't needed; absent means none", pair.Value);
                }
                ListField(p, "network", "permissions");
                ListField(p, "connectors", "permissions");
                if (p["network"] is JArray network && network.Count > 0)
                    Add("permission_denied", "permissions.network", "permissions.network must be empty in Phase 1");
                if (p["connectors"] is JArray connectors)
                    foreach (var connector in connectors)
                        if (connector.Type != JTokenType.String || !((string)connector).EndsWith(":read"))
                            Add("permission_denied", "permissions.connectors", $"{Describe(connector)}: only :read is allowed in Phase 1");
            }

            void Scene()
            {
                string space = Str(scene, "space");
                if (space == null || !ArtifactSchema.Spaces.Contains(space))
                    Add("bad_value", "scene.space", $"unknown space {(Absent(scene["space"]) ? "" : Describe(scene["space"]))}", SpaceHint, Values(ArtifactSchema.Spaces));
                if (space == "room" && Permission("room") != "read")
                    Add("permission_missing", "permissions.room", "space is room, so permissions.room must be read",
                        "\"room\" is only for courses a generator lays out on the floor; for anything else use \"space\": \"volume\" and no room permission");
                if (scene.ToString().Length > ArtifactSchema.MaxSceneChars) Add("too_big", "scene", "over the 256 KB limit");
                if (!ObjectField(scene, "view", "scene") && scene["view"] is JObject view)
                {
                    if (space != "volume") Add("bad_value", "scene.view", "view needs space volume");
                    if (!(Num(view, "radius") > 0.0)) Add("bad_value", "scene.view.radius", "must be above 0");
                }

                Generators(space);
                if (space == "room" && !(scene["generators"] is JArray placed && placed.Count > 0))
                    Add("bad_value", "scene.space", "room needs a generator: positions in the room come only from one, never from the scene",
                        "use \"space\": \"volume\" (positions in metres around 0,0,0, scaled to fit) and drop permissions.room; \"room\" is for courses a generator lays out on the floor",
                        new[] { "volume", "panel" });
                var nodesToken = scene["nodes"];
                if (!Absent(nodesToken) && !(nodesToken is JArray)) Add("bad_value", "nodes", $"must be a list of node objects, found {Describe(nodesToken)}");
                var list = nodesToken as JArray ?? new JArray();
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

            const string SpaceHint = "use \"volume\" for a thing to look at (it's scaled to fit, positions in metres around 0,0,0), \"panel\" for a flat board; \"room\" is only for courses a generator lays out";

            string Permission(string key) => Str(manifest["permissions"], key);

            bool Asks(string connector)
            {
                if (Get(manifest["permissions"], "connectors") is JArray list)
                    foreach (var c in list) if (c.Type == JTokenType.String && (string)c == connector) return true;
                return false;
            }

            void Generators(string space)
            {
                var list = ObjectList(scene, "generators", "generators", "an object {id, type, params}");
                if (list == null) return;
                if (list.Count > 0 && space != "room") Add("bad_value", "generators", "generators need space room");
                if (list.Count > 1) Add("too_big", "generators", $"{list.Count} generators: this host places one per scene", "keep one generator");
                for (int i = 0; i < list.Count; i++)
                {
                    if (!(list[i] is JObject generator)) continue;
                    string id = Str(generator, "id"), type = Str(generator, "type");
                    if (id == null || type == null || !ArtifactSchema.Generators.TryGetValue(type, out var fills))
                    {
                        Add("bad_value", $"generators[{i}]", $"unknown generator {type}", "a generator needs an id and a type", Values(ArtifactSchema.Generators.Keys));
                        continue;
                    }
                    ObjectField(generator, "params", $"generators[{i}]");
                    foreach (var slot in fills) slots.Add(id + "." + slot);
                }
            }

            void CollectNodes(JArray list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    string path = $"nodes[{i}]";
                    if (!(list[i] is JObject node)) { Add("bad_value", path, "must be an object"); continue; }
                    string id = Str(node, "id");
                    if (id == null || !NodeIdRule.IsMatch(id))
                    {
                        Add("bad_value", path, "needs an id of lowercase letters, digits and _", Absent(node["id"]) ? "add \"id\": \"<name>\" to this node" : "ids use a-z, 0-9 and _ only");
                        continue;
                    }
                    if (nodes.ContainsKey(id)) { Add("bad_value", path, $"id \"{id}\" is used twice"); continue; }
                    nodes[id] = node;
                    // Only a panel draws widgets, so a button anywhere else can't be pressed.
                    if (Str(node, "kind") == "panel" && node["widgets"] is JArray widgets)
                        for (int w = 0; w < widgets.Count; w++)
                        {
                            if (Str(widgets[w], "type") != "button") continue;
                            // With alt_id the button is one toggle that fires id, then alt_id, in turn: both are inputs.
                            foreach (var key in ButtonIds)
                            {
                                string button = Str(widgets[w], key);
                                if (button != null && buttons.Add(button)) buttonPaths[button] = $"{path}.widgets[{w}]";
                            }
                        }
                }
                int zones = 0;
                foreach (var node in nodes.Values) if (IsZone(node)) zones++;
                if (zones > ArtifactSchema.MaxZones) Add("too_big", "nodes", $"{zones} zones is over the limit of {ArtifactSchema.MaxZones}");
            }

            static bool IsZone(JObject n) =>
                Str(n, "kind") == "zone" || (Str(n, "kind") == "array" && Str(n["template"], "kind") == "zone");

            void Node(JObject n, string path)
            {
                var kindToken = n["kind"];
                string kind = Str(n, "kind");
                var allowedKinds = Values(ArtifactSchema.NodeKinds);
                if (Absent(kindToken))
                {
                    string wrote = Str(n, "type");
                    string advice = wrote != null && KindAdvice.TryGetValue(wrote.ToLowerInvariant(), out var said) ? said + "; " : "";
                    Add("unknown_kind", path + ".kind", "kind is required", $"{(wrote != null ? "\"type\" isn't read on a node; " : "")}{advice}add \"kind\", one of allowed", allowedKinds);
                    return;
                }
                if (kind != null && ArtifactSchema.ReservedKinds.Contains(kind))
                {
                    Add("unsupported_kind", path, $"{kind} isn't available in Phase 1", "use one of allowed", allowedKinds);
                    return;
                }
                if (kind == null || !ArtifactSchema.NodeKinds.Contains(kind))
                {
                    string suffix = kind != null && KindHints.TryGetValue(kind, out var fix) ? $" (did you mean {fix}?)" : "";
                    string advice = kind != null && KindAdvice.TryGetValue(kind.ToLowerInvariant(), out var said) ? said : "use one of allowed";
                    Add("unknown_kind", path, $"unknown kind {(kind != null ? "\"" + kind + "\"" : Describe(kindToken))}{suffix}", advice, allowedKinds);
                    return;
                }

                if (!StringField(n, "parent", path))
                {
                    string parent = Str(n, "parent");
                    JObject parentNode = null;
                    if (parent != null && !nodes.TryGetValue(parent, out parentNode)) Add("unknown_ref", path + ".parent", $"no node \"{parent}\"", "parent is the id of another node in this scene", NodesOfKind(null));
                    else if (parentNode != null && (Str(parentNode, "kind") == "panel" || Str(parentNode, "kind") == "timeline"))
                        Add("bad_value", path + ".parent", $"\"{parent}\" is a {Str(parentNode, "kind")}, so it can't hold parts",
                            Str(parentNode, "kind") == "panel" ? "a panel sits beside the scene; parent the part to a primitive instead" : "a timeline is only a clock; parent the part to a primitive instead");
                }
                if (!StringField(n, "at", path))
                {
                    string at = Str(n, "at");
                    if (at != null && !slots.Contains(at)) Add("unknown_slot", path + ".at", $"no slot \"{at}\"", slots.Count == 0 ? "slots come from a generator; without one, leave out \"at\"" : null, Names(slots));
                    else if (at != null && !ArtifactSchema.SingletonSlots.Contains(at.Substring(at.IndexOf('.') + 1)))
                        Add("bad_value", path + ".at", $"\"{at}\" is a list of places, so a single node can't stand on it", "stand on a single slot (start or finish), or make an array with \"source\": \"" + at + "\"", Singletons());
                }
                Vec3Field(n, "pos", path);
                Vec3Field(n, "rot", path);
                Vec3Field(n, "scale", path);
                BoolField(n, "visible", path);

                switch (kind)
                {
                    case "primitive": Primitive(n, path); break;
                    case "text":
                        if (Absent(n["text"])) Add("bad_value", path + ".text", "required");
                        else if (!StringField(n, "text", path))
                        {
                            var shown = Regex.Matches(Str(n, "text"), @"\{([a-z0-9_]+)\}");
                            foreach (Match m in shown)
                                if (!metrics.Contains(m.Groups[1].Value))
                                    Add("unknown_ref", path + ".text", $"no metric \"{m.Groups[1].Value}\"", "{name} shows a metric rule's value; add the rule or drop the braces", Names(metrics));
                            if (shown.Count > 1)
                                Add("bad_value", path + ".text", $"shows {shown.Count} metrics: a text node shows at most one {{metric}}", "use one text node per metric, or stat widgets on a panel");
                        }
                        NumberField(n, "height", path);
                        ColorField(n, "color", path);
                        BoolField(n, "billboard", path);
                        if (!StringField(n, "align", path) && Str(n, "align") is string align && align != "left" && align != "right")
                            Add("bad_value", path + ".align", $"unknown align \"{align}\"", "left or right puts the node's position at that edge of the text; leave it out to centre it", new[] { "left", "right" });
                        break;
                    case "panel": Panel(n, path); break;
                    case "sim": Sim(n, path); break;
                    case "array": Array(n, path); break;
                    case "zone": Vec3Field(n, "size", path); break;
                    case "gltf":
                        string asset = Str(n, "asset");
                        if (!StringField(n, "asset", path) && (asset == null || !assets.Contains(asset)))
                            Add("asset_not_listed", path + ".asset", $"\"{asset}\" isn't in the manifest");
                        break;
                    case "timeline":
                        var duration = n["duration_s"];
                        if (!(Num(duration) > 0.0)) Add("bad_value", path + ".duration_s", "needs a number of seconds above 0", "like \"duration_s\": 4");
                        foreach (string flag in new[] { "loop", "autoplay" })
                            if (n[flag] != null && n[flag].Type != JTokenType.Boolean) Add("bad_value", path + "." + flag, "true or false");
                        break;
                    case "line":
                        if (Absent(n["points"]) && Absent(n["source"]) && Absent(n["follow"]) && Absent(n["through"]))
                            Add("bad_value", path, "needs points, a source, follow or through", "for a fixed line use \"points\": [[0, 0, 0], [0, 0.2, 0]]");
                        else if (!Absent(n["source"]) && n["source"].Type != JTokenType.String)
                            Add("bad_value", path + ".source", $"a line's source is a slot name, found {Describe(n["source"])}", "for a fixed line use \"points\"; for a line between parts use \"through\": [node ids]", Names(slots));
                        else Source(n, path);
                        Points(n, path);
                        NumberField(n, "width", path);
                        ColorField(n, "color", path);
                        Follow(n, path);
                        Through(n, path);
                        break;
                }
            }

            void Points(JObject n, string path)
            {
                var t = n["points"];
                if (Absent(t)) return;
                if (!(t is JArray points))
                {
                    Add("bad_value", path + ".points", $"must be a list of [x, y, z], found {Describe(t)}");
                    return;
                }
                for (int i = 0; i < points.Count; i++)
                    if (!Vec3(points[i])) Add("bad_value", $"{path}.points[{i}]", $"must be [x, y, z]: three numbers or \"$input\"s, found {Describe(points[i])}");
            }

            // A line through nodes reads their local positions each frame, so they have to share its parent too.
            void Through(JObject n, string path)
            {
                if (Absent(n["through"])) return;
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
                    if (id == null || !nodes.TryGetValue(id, out var target)) Add("unknown_ref", $"{path}.through[{i}]", $"no node {Describe(through[i])}");
                    else if (Str(target, "parent") != Str(n, "parent"))
                        Add("bad_value", $"{path}.through[{i}]", $"a line through nodes needs the same parent as \"{id}\"");
                }
            }

            // A trail records its target's local position, so the two have to share a parent to line up.
            void Follow(JObject n, string path)
            {
                if (WrongType(n, "length", path, Whole, "a whole number")) return;
                if (Num(n, "length") > ArtifactSchema.MaxTrailPoints)
                    Add("too_big", path + ".length", $"{n["length"]} is over the limit of {ArtifactSchema.MaxTrailPoints}");
                if (StringField(n, "follow", path)) return;
                string follow = Str(n, "follow");
                if (follow == null) return;
                if (!nodes.TryGetValue(follow, out var target)) Add("unknown_ref", path + ".follow", $"no node \"{follow}\"");
                else if (Str(target, "parent") != Str(n, "parent"))
                    Add("bad_value", path + ".follow", $"a trail needs the same parent as \"{follow}\"");
            }

            void Sim(JObject n, string path)
            {
                string solver = Str(n, "solver");
                if (solver == null || !ArtifactSchema.Solvers.ContainsKey(solver))
                    Add("bad_value", path + ".solver", $"unknown solver {(solver != null ? "\"" + solver + "\"" : Describe(n["solver"]))}",
                        "pendulum swings or spins parts on a rod (g 0 and an omega spins it); n_body is orbits", Values(ArtifactSchema.Solvers.Keys));
                NumberField(n, "rate", path);
                bool paramsTyped = ObjectField(n, "params", path);
                if (!(n["bodies"] is JArray bodies) || bodies.Count == 0)
                {
                    Add("bad_value", path + ".bodies", "at least one is required");
                    return;
                }
                if (bodies.Count > ArtifactSchema.MaxBodies) Add("too_big", path + ".bodies", $"{bodies.Count} is over the limit of {ArtifactSchema.MaxBodies}");
                var seen = new HashSet<string>();
                for (int i = 0; i < bodies.Count; i++)
                {
                    string at = $"{path}.bodies[{i}]";
                    if (!(bodies[i] is JObject body)) { Add("bad_value", at, $"must be an object {{node, mass}}, found {Describe(bodies[i])}"); continue; }
                    string node = Str(body, "node");
                    if (node == null || !nodes.ContainsKey(node)) Add("unknown_ref", at + ".node", $"no node {(node != null ? "\"" + node + "\"" : Describe(body["node"]))}");
                    else if (!seen.Add(node)) Add("bad_value", at + ".node", $"\"{node}\" is driven twice");
                    foreach (string key in new[] { "mass", "length", "angle", "omega" }) NumberField(body, key, at);
                    Vec3Field(body, "pos", at);
                    Vec3Field(body, "vel", at);
                    if (Num(body, "mass") <= 0.0) Add("bad_value", at + ".mass", "must be above 0");
                }
                if (!paramsTyped && n["params"] is JObject p)
                {
                    NumberField(p, "step", path + ".params");
                    if (Num(p, "step") <= 0.0) Add("bad_value", path + ".params.step", "must be above 0");
                }
                if (solver == "pendulum")
                {
                    if (bodies.Count > PendulumSolver.MaxLinks)
                        Add("too_big", path + ".bodies", $"a pendulum has 1 or {PendulumSolver.MaxLinks} links, not {bodies.Count}");
                    for (int i = 0; i < bodies.Count; i++)
                        if (Num(Get(bodies[i], "length")) <= 0.0)
                            Add("bad_value", $"{path}.bodies[{i}].length", "must be above 0");
                }
            }

            void Primitive(JObject n, string path)
            {
                string shape = Str(n, "shape");
                if (shape == null || !ArtifactSchema.Shapes.Contains(shape))
                    Add("bad_value", path + ".shape", $"unknown shape {(shape != null ? "\"" + shape + "\"" : Absent(n["shape"]) ? "\"\"" : Describe(n["shape"]))}",
                        Absent(n["shape"]) ? "a primitive needs \"shape\"" : null, Values(ArtifactSchema.Shapes));
                Vec3Field(n, "size", path);
                ColorField(n, "color", path);
                BoolField(n, "collider", path);
                BoolField(n, "glow", path);
            }

            void Panel(JObject n, string path)
            {
                StringField(n, "title", path);
                var widgets = ObjectList(n, "widgets", path + ".widgets", "a widget object {type, ...}");
                if (widgets == null) return;
                for (int i = 0; i < widgets.Count; i++)
                {
                    if (!(widgets[i] is JObject widget)) continue;
                    string at = $"{path}.widgets[{i}]", type = Str(widget, "type");
                    if (type == null || !ArtifactSchema.Widgets.Contains(type))
                    {
                        Add("bad_value", at, $"unknown widget {(type != null ? "\"" + type + "\"" : Describe(widget["type"]))}", null, Values(ArtifactSchema.Widgets));
                        continue;
                    }
                    if (!StringField(widget, "metric", at))
                    {
                        string metric = Str(widget, "metric");
                        if (metric != null && !metrics.Contains(metric)) Add("unknown_ref", at + ".metric", $"no metric \"{metric}\"", "a stat shows a metric rule by its name", Names(metrics));
                    }
                    if (!ListField(widget, "columns", at) && widget["columns"] is JArray columns)
                        foreach (var column in columns)
                            if (column.Type != JTokenType.String || !metrics.Contains((string)column)) Add("unknown_ref", at + ".columns", $"no metric {Describe(column)}");
                    StringField(widget, "label", at);
                    StringField(widget, "alt_label", at);
                    StringField(widget, "after_run", at);
                    BoolField(widget, "primary", at);
                    if (type == "button" && !StringField(widget, "alt_id", at) && Str(widget, "alt_id") != null && Str(widget, "alt_label") == null)
                        Add("bad_value", at + ".alt_label", "a toggle button with alt_id needs alt_label, the label it shows for its second press", "like {\"type\": \"button\", \"id\": \"play\", \"label\": \"Start\", \"alt_id\": \"pause\", \"alt_label\": \"Pause\"}");
                    if (type == "button" && Str(widget, "id") == null) Add("bad_value", at, "a button needs an id", "like {\"type\": \"button\", \"id\": \"start\", \"label\": \"Start\"}; an on_input rule names that id");
                    if (type == "slider")
                    {
                        string input = Str(widget, "input");
                        if (input == null || !inputs.Contains(input))
                            Add("unknown_input", at + ".input", $"no input {(input != null ? "\"" + input + "\"" : Describe(widget["input"]))}",
                                "a slider drives a manifest input: add {\"name\": \"<name>\", \"type\": \"number\", \"min\": 0, \"max\": 1, \"default\": 0.5} to manifest.inputs", Names(inputs));
                        else if (!numericInputs.Contains(input)) Add("bad_value", at + ".input", $"\"{input}\" isn't a number, so it can't have a slider");
                    }
                }
            }

            void Array(JObject n, string path)
            {
                var templateToken = n["template"];
                var template = templateToken as JObject;
                string kind = Str(template, "kind");
                if (kind != "primitive" && kind != "zone") Add("bad_value", path + ".template", "must be a primitive or a zone", null, new[] { "primitive", "zone" });
                if (template != null)
                {
                    if (kind == "primitive") Primitive(template, path + ".template");
                    if (kind == "zone") Vec3Field(template, "size", path + ".template");
                    Vec3Field(template, "pos", path + ".template");
                    Vec3Field(template, "rot", path + ".template");
                }
                ObjectField(n, "map", path);
                if (Absent(n["source"])) Add("bad_value", path + ".source", "required");
                else Source(n, path);
            }

            void Source(JObject n, string path)
            {
                var source = n["source"];
                if (Absent(source)) return;
                if (source.Type == JTokenType.String)
                {
                    if (!slots.Contains((string)source)) Add("unknown_slot", path + ".source", $"no slot \"{source}\"", null, Names(slots));
                }
                else if (source is JObject table)
                {
                    if (StringField(table, "table", path + ".source")) return;
                    string name = Str(table, "table");
                    if (name != null && !tables.Contains(name))
                        Add("unknown_ref", path + ".source.table", $"no data \"{name}\" in the manifest", null, Names(tables));
                    ObjectField(table, "grid", path + ".source");
                }
                else Add("bad_value", path + ".source", $"must be a slot name or {{\"table\": ...}}, found {Describe(source)}");
            }

            void Rules()
            {
                var rules = ObjectList(scene, "rules", "rules", "a rule object {type, ...}");
                if (rules == null) return;
                if (rules.Count > ArtifactSchema.MaxRules) Add("too_big", "rules", $"{rules.Count} is over the limit of {ArtifactSchema.MaxRules}");
                for (int i = 0; i < rules.Count; i++)
                    if (Str(rules[i], "type") == "metric" && Str(rules[i], "name") is string metricName && !metrics.Add(metricName))
                        Add("bad_value", $"rules[{i}].name", $"metric \"{metricName}\" is named twice", "give each metric rule its own name");
                for (int i = 0; i < rules.Count; i++)
                {
                    if (!(rules[i] is JObject rule)) continue;
                    string path = $"rules[{i}]", type = Str(rule, "type");
                    if (type == null || !ArtifactSchema.Rules.Contains(type))
                    {
                        Add("bad_value", path, $"unknown rule {(type != null ? "\"" + type + "\"" : Describe(rule["type"]))}",
                            "a button press is on_input; motion is animate (with a timeline); a shown number is metric", Values(ArtifactSchema.Rules));
                        continue;
                    }
                    if (type == "metric") Metric(rule, path);
                    if (type == "on_input" && !buttons.Contains(Str(rule, "input") ?? ""))
                        Add("unknown_ref", path + ".input", $"no button {(Str(rule, "input") != null ? "\"" + Str(rule, "input") + "\"" : Describe(rule["input"]))}",
                            "on_input names a button id from a panel's widgets: {\"type\": \"button\", \"id\": \"<id>\", \"label\": \"...\"}", Names(buttons));
                    if (type == "on_zone")
                    {
                        ZoneRef(rule["zone"], path + ".zone");
                        if (!ArtifactSchema.ZoneEvents.Contains(Str(rule, "event") ?? ""))
                            Add("bad_value", path + ".event", $"must be enter or exit, found {Describe(rule["event"])}", "\"event\": \"enter\" fires when the Thing comes into the zone", Values(ArtifactSchema.ZoneEvents));
                        if (Absent(scene["run"]))
                            Add("bad_value", path, "on_zone fires only while a run is scored, and the scene has no run", "zones are for courses; for a button use on_input, for motion a timeline");
                    }
                    if (type == "animate") Animate(rule, path);
                    var actions = ObjectList(rule, "do", path + ".do", "an action object like {\"timeline\": \"clock\", \"action\": \"play\"}");
                    if (actions == null) continue;
                    for (int a = 0; a < actions.Count; a++)
                        if (actions[a] is JObject action) Action(action, $"{path}.do[{a}]");
                }

                // A button that runs nothing would look broken: each one needs an on_input rule.
                var pressed = new HashSet<string>();
                foreach (var rule in rules)
                    if (Str(rule, "type") == "on_input" && Str(rule, "input") is string input) pressed.Add(input);
                foreach (var button in buttonPaths)
                    if (!pressed.Contains(button.Key))
                        Add("bad_value", button.Value, $"button \"{button.Key}\" has no on_input rule, so pressing it does nothing",
                            $"add {{\"type\": \"on_input\", \"input\": \"{button.Key}\", \"do\": [{{\"timeline\": \"<id>\", \"action\": \"play\"}}]}}, or drop the button");
            }

            static readonly string[] Verbs = { "set", "run", "timeline", "sim" };
            static readonly string[] ButtonIds = { "id", "alt_id" };

            // What the host runs (ArtifactHost.Do): one verb per action, and only what it builds.
            void Action(JObject action, string at)
            {
                int count = 0;
                foreach (string verb in Verbs) if (!Absent(action[verb])) count++;
                if (count == 0)
                {
                    Add("bad_value", at, "an action needs one verb: set, run, timeline or sim",
                        "like {\"timeline\": \"<id>\", \"action\": \"play\"} or {\"set\": \"<node>.visible\", \"value\": true}", Verbs);
                    return;
                }
                if (count > 1)
                {
                    Add("bad_value", at, $"has {count} verbs, and only the first would run", "split it: one action object per verb, in the order they should run", Verbs);
                    return;
                }
                if (!Absent(action["set"]))
                {
                    if (StringField(action, "set", at)) return;
                    string target = Str(action, "set");
                    int dot = target.IndexOf('.');
                    string node = dot < 0 ? target : target.Substring(0, dot), property = dot < 0 ? null : target.Substring(dot + 1);
                    if (!nodes.ContainsKey(node)) Add("unknown_ref", at + ".set", $"no node \"{node}\"", "set is \"<node id>.visible\"", NodesOfKind(null));
                    else if (property != "visible")
                        Add("bad_value", at + ".set", $"only <node>.visible can be set, not \"{target}\"",
                            "to change a colour, a position or a turn, put an animate rule on a timeline and play it; to swap looks, show one part and hide another",
                            new[] { node + ".visible" });
                    if (action["value"]?.Type != JTokenType.Boolean)
                        Add("bad_value", at + ".value", $"must be true or false, found {Describe(action["value"])}", "\"value\": true shows the node, false hides it", new[] { "true", "false" });
                    return;
                }
                if (!Absent(action["run"]))
                {
                    string verb = Str(action, "run");
                    if (verb == null || !ArtifactSchema.RunVerbs.Contains(verb))
                        Add("bad_value", at + ".run", $"must be arm or reset, found {Describe(action["run"])}", "\"run\": \"reset\" puts the Thing back at the start", Values(ArtifactSchema.RunVerbs));
                    if (Absent(scene["run"]))
                        Add("bad_value", at + ".run", "the scene has no run to arm or reset", "run actions are for scored courses; play a timeline or a sim instead", Values(ArtifactSchema.RunVerbs));
                    return;
                }
                if (!Absent(action["timeline"])) TimelineRef(action["timeline"], at + ".timeline");
                else
                {
                    string sim = Str(action, "sim");
                    if (sim == null || !nodes.TryGetValue(sim, out var node))
                        Add("unknown_ref", at + ".sim", $"no node {(sim != null ? "\"" + sim + "\"" : Describe(action["sim"]))}", "sim names a sim node's id", NodesOfKind("sim"));
                    else if (Str(node, "kind") != "sim") Add("unknown_ref", at + ".sim", $"\"{sim}\" isn't a sim node", "sim names a sim node's id", NodesOfKind("sim"));
                }
                if (!ArtifactSchema.TimelineActions.Contains(Str(action, "action") ?? ""))
                    Add("bad_value", at + ".action", $"must be play, pause or restart, found {Describe(action["action"])}", "like \"action\": \"play\"; restart goes back to the start", Values(ArtifactSchema.TimelineActions));
            }

            readonly HashSet<string> animated = new HashSet<string>();

            void Animate(JObject rule, string path)
            {
                string target = Str(rule, "target"), property = Str(rule, "property");
                JObject clock = TimelineRef(rule["timeline"], path + ".timeline");
                if (target == null || !nodes.TryGetValue(target, out var node))
                    Add("unknown_ref", path + ".target", $"no node {(target != null ? "\"" + target + "\"" : Describe(rule["target"]))}", null, NodesOfKind(null));
                else if (Str(node, "kind") == "timeline" || Str(node, "kind") == "panel")
                    Add("bad_value", path + ".target", $"a {Str(node, "kind")} can't be animated");
                else if (!Absent(node["at"]) && (property == "pos" || property == "rot"))
                    Add("bad_value", path + ".property", $"\"{target}\" stands on a slot, so its {property} can't be animated");
                if (property == null || !ArtifactSchema.AnimateProperties.TryGetValue(property, out string shape))
                {
                    Add("bad_value", path + ".property", "use pos, rot, scale, visible or color", null, Values(ArtifactSchema.AnimateProperties.Keys));
                    return;
                }
                if (target != null && !animated.Add(target + "." + property)) Add("bad_value", path, $"{target}.{property} is animated twice");
                if (!(rule["keys"] is JArray keys) || keys.Count == 0)
                {
                    Add("bad_value", path + ".keys", "at least one [t, value] is required", "like \"keys\": [[0, [0, 0, 0]], [2, [0, 0, 360]]]");
                    return;
                }
                if (keys.Count > ArtifactSchema.MaxKeys) Add("too_big", path + ".keys", $"{keys.Count} is over the limit of {ArtifactSchema.MaxKeys}");
                double end = Num(Get(clock, "duration_s")) ?? double.MaxValue, before = -1.0;
                for (int k = 0; k < keys.Count; k++)
                {
                    string at = $"{path}.keys[{k}]";
                    if (!(keys[k] is JArray key) || key.Count != 2 || !IsNumber(key[0])) { Add("bad_value", at, "use [t, value]", $"t in seconds, then the {property} value, like [1.5, {(shape.StartsWith("[") ? "[0, 90, 0]" : shape.StartsWith("true") ? "true" : "\"#FF8A3D\"")}]"); continue; }
                    double t = Num(key[0]).Value;
                    if (t < 0.0 || t > end) Add("bad_value", at, $"t {t} is outside the timeline (0 to {end} s)");
                    else if (t < before) Add("bad_value", at, "times have to go up");
                    before = t;
                    if (!KeyValue(property, key[1])) Add("bad_value", at, $"{property} takes {shape}", "key values are plain numbers; \"$input\" isn't read here");
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

            JObject TimelineRef(JToken idToken, string path)
            {
                string id = idToken?.Type == JTokenType.String ? (string)idToken : null;
                if (id == null || !nodes.TryGetValue(id, out var node))
                    Add("unknown_ref", path, $"no node {(id != null ? "\"" + id + "\"" : Describe(idToken))}", "add a timeline node {\"id\": \"clock\", \"kind\": \"timeline\", \"duration_s\": 4, \"loop\": true}", NodesOfKind("timeline"));
                else if (Str(node, "kind") != "timeline") Add("unknown_ref", path, $"\"{id}\" isn't a timeline", null, NodesOfKind("timeline"));
                else return node;
                return null;
            }

            void Metric(JObject rule, string path)
            {
                string op = Str(rule, "op"), probe = Str(rule, "probe");
                if (op == null || !ArtifactSchema.MetricOps.Contains(op))
                    Add("bad_value", path + ".op", $"unknown op {(op != null ? "\"" + op + "\"" : Describe(rule["op"]))}", null, Values(ArtifactSchema.MetricOps));
                if (!StringField(rule, "probe", path) && probe != null && !ArtifactSchema.Probes.Contains(probe))
                    Add("bad_value", path + ".probe", $"unknown probe \"{probe}\"", null, Values(ArtifactSchema.Probes));
                if (rule["zones"] != null) ZoneRef(rule["zones"], path + ".zones");
                var raw = rule["nodes"];
                var targets = raw as JArray ?? (Absent(raw) ? new JArray() : new JArray(raw));
                foreach (var t in targets)
                {
                    string target = t.Type == JTokenType.String ? (string)t : null;
                    if (target == null || !nodes.ContainsKey(target)) Add("unknown_ref", path + ".nodes", $"no node {(target != null ? "\"" + target + "\"" : Describe(t))}", null, NodesOfKind(null));
                }
                if (op == "sim") SimMetric(rule, path);
                else if (op != null && ArtifactSchema.MetricOps.Contains(op) && Absent(scene["run"]))
                    Add("bad_value", path + ".op", $"{op} counts only while a run is scored, and the scene has no run",
                        "a number from a sim is op sim; a run is for courses; for a clock, show a timeline playing", Values(new[] { "sim" }));
                if (op == "hit_count")
                    foreach (var t in targets)
                        if (t.Type == JTokenType.String && nodes.TryGetValue((string)t, out var hit) &&
                            !(Str(hit, "kind") == "array" && Str(hit["template"], "kind") == "primitive" && Get(hit["template"], "collider")?.Type == JTokenType.Boolean && (bool)Get(hit["template"], "collider")))
                            Add("bad_value", path + ".nodes", $"\"{t}\" isn't an array of collider primitives, so nothing can hit it",
                                "hit_count counts touches on an array whose template is a primitive with \"collider\": true", NodesOfKind("array"));
            }

            void SimMetric(JObject rule, string path)
            {
                string sim = Str(rule, "sim"), value = Str(rule, "value");
                if (sim == null || !nodes.TryGetValue(sim, out var node) || Str(node, "kind") != "sim")
                {
                    Add("unknown_ref", path + ".sim", $"no sim node {(sim != null ? "\"" + sim + "\"" : Describe(rule["sim"]))}", null, NodesOfKind("sim"));
                    return;
                }
                string solver = Str(node, "solver");
                if (solver != null && ArtifactSchema.Solvers.TryGetValue(solver, out var values) && System.Array.IndexOf(values, value) < 0)
                    Add("unknown_ref", path + ".value", $"{solver} has no value \"{value}\"", null, values);
            }

            void ZoneRef(JToken idToken, string path)
            {
                string id = idToken?.Type == JTokenType.String ? (string)idToken : null;
                if (id == null || !nodes.TryGetValue(id, out var node))
                    Add("unknown_ref", path, $"no node {(id != null ? "\"" + id + "\"" : Absent(idToken) ? "\"\"" : Describe(idToken))}", null, NodesOfKind("zone"));
                else if (!IsZone(node)) Add("unknown_ref", path, $"\"{id}\" isn't a zone", null, NodesOfKind("zone"));
            }

            void Cycles()
            {
                foreach (var start in nodes)
                {
                    var seen = new HashSet<string> { start.Key };
                    for (string at = Str(start.Value, "parent"); at != null && nodes.TryGetValue(at, out var next); at = Str(next, "parent"))
                        if (!seen.Add(at))
                        {
                            Add("bad_value", "nodes", $"\"{start.Key}\" is its own ancestor");
                            break;
                        }
                }
            }

            void RunBlock()
            {
                var token = scene["run"];
                if (Absent(token)) return;
                if (!(token is JObject run))
                {
                    Add("bad_value", "run", $"must be an object {{start, finish}} or null, found {Describe(token)}");
                    return;
                }
                if (Permission("things") != "read")
                    Add("permission_missing", "permissions.things", "the scene has a run, so permissions.things must be read", "a run scores the Thing driving a course; for anything else use \"run\": null");
                ZoneRef(Get(run["start"], "zone"), "run.start.zone");
                ZoneRef(Get(run["finish"], "zone"), "run.finish.zone");
                if (Get(run["finish"], "needs") is JArray needs)
                    for (int i = 0; i < needs.Count; i++)
                    {
                        var need = needs[i];
                        if (!(need is JObject)) { Add("bad_value", $"run.finish.needs[{i}]", $"must be an object {{metric, min}}, found {Describe(need)}"); continue; }
                        string metric = Str(need, "metric"), min = Str(need, "min");
                        if (metric == null || !metrics.Contains(metric)) Add("unknown_ref", "run.finish.needs", $"no metric \"{metric}\"");
                        if (min != null && min.StartsWith("@") && !slots.Contains(min.Substring(1))) Add("unknown_slot", "run.finish.needs", $"no slot \"{min.Substring(1)}\"");
                    }
                var fails = ObjectList(run, "fail", "run.fail", "an object {reason, ...}");
                if (fails != null)
                    for (int i = 0; i < fails.Count; i++)
                    {
                        if (!(fails[i] is JObject fail)) continue;
                        if (Str(fail, "reason") == null) Add("bad_value", $"run.fail[{i}]", "needs a reason");
                        string probe = Str(fail, "probe");
                        if (probe != null && !ArtifactSchema.Probes.Contains(probe)) Add("bad_value", $"run.fail[{i}].probe", $"unknown probe \"{probe}\"");
                    }
            }

            void Outputs()
            {
                var outputs = ObjectList(manifest, "outputs", "outputs", "an object {name, ...}");
                if (outputs == null) return;
                foreach (var output in outputs)
                {
                    string name = Str(output, "name");
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
                    if (!s.StartsWith("$")) return;
                    if (!InputRef.IsMatch(s))
                    {
                        if (!Covered(path))
                            Add("bad_value", path, $"\"{s}\" isn't an input reference", "\"$name\" is read as it is, with no maths; put the value you need into the input itself", Names(numericInputs));
                    }
                    else if (!inputs.Contains(s.Substring(1)))
                        Add("unknown_input", path, $"no input \"{s.Substring(1)}\"", "\"$name\" reads manifest.inputs[].name", Names(inputs));
                    else if (!numericInputs.Contains(s.Substring(1)) && !Covered(path))
                        Add("bad_value", path, $"\"{s.Substring(1)}\" isn't a number input, so it can't stand for a number", "only number and integer inputs are read here", Names(numericInputs));
                }
            }

            /// <summary>True when an error is already reported at this path or one holding it.</summary>
            bool Covered(string path)
            {
                foreach (var e in errors)
                    if (!string.IsNullOrEmpty(e.path) && (e.path == path ||
                        (path.Length > e.path.Length && path.StartsWith(e.path) && (path[e.path.Length] == '.' || path[e.path.Length] == '[')))) return true;
                return false;
            }

            string[] Singletons()
            {
                var list = new List<string>();
                foreach (var slot in slots) if (ArtifactSchema.SingletonSlots.Contains(slot.Substring(slot.IndexOf('.') + 1))) list.Add(slot);
                return list.ToArray();
            }

            void OneOf(JObject o, string key, HashSet<string> allowed, string path, string hint)
            {
                string value = Str(o, key);
                if (value == null || !allowed.Contains(value))
                    Add("bad_value", path, $"{(value != null || Absent(o[key]) ? "\"" + value + "\"" : Describe(o[key]))} isn't one of {string.Join(", ", allowed)}", hint, Values(allowed));
            }
        }
    }
}
