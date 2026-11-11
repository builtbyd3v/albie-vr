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
        static void Register() => AlbieTools.Register("artifacts", new Dictionary<string, AlbieTools.AsyncHandler>
        {
            ["artifact_list"] = Now(List),
            ["artifact_create"] = CreateAsync,
            ["artifact_open"] = Now(Open),
            ["artifact_close"] = Now(Close),
            ["artifact_relayout"] = Now(Relayout),
            ["artifact_run"] = Now(Run),
            ["artifact_compare"] = Now(Compare),
        });

        static AlbieTools.AsyncHandler Now(AlbieTools.Handler run) => (args, done) => done(run(args));

        public static JObject List(JObject args)
        {
            var templates = new JArray();
            foreach (string name in ArtifactLibrary.Templates)
                if (ArtifactLibrary.TryTemplate(name, out var manifest, out _))
                    templates.Add(new JObject { ["template"] = name, ["title"] = manifest["title"], ["inputs"] = manifest["inputs"] });
            var host = ArtifactHost.Current;
            return new JObject
            {
                ["templates"] = templates, ["data_sources"] = ArtifactData.Sources(), ["library"] = ArtifactLibrary.List(),
                ["open"] = host != null ? host.OpenId : null,
            };
        }

        // ---------- tries: a fix loop that isn't converging is stopped, not repeated ----------

        /// <summary>After this many refused written artifacts in a row, the result tells the agent to stop and say so.</summary>
        public const int MaxAttempts = 3;
        /// <summary>A refusal this long after the last one starts a new count: it's a new request, not a fix.</summary>
        public const double AttemptWindowS = 90;

        static int attempts;
        static DateTime lastAttempt;

        /// <summary>Written artifacts refused in a row (within <see cref="AttemptWindowS"/> of each other). 0 after a success.</summary>
        public static int Attempts => attempts;

        /// <summary>Starts the count again. Checks call it; so does every success.</summary>
        public static void ResetAttempts() => attempts = 0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => attempts = 0;

        public static JObject Create(JObject args) => Create(args, out _);

        /// <summary><see cref="Create(JObject)"/>; <paramref name="review"/> is the body for the hub's advisory review
        /// (C2): set only when a written artifact was saved, null for templates and refusals.</summary>
        public static JObject Create(JObject args, out JObject review)
        {
            review = null;
            string template = (string)args["template"];
            ArtifactLibrary.Created made;
            JObject written = null;
            List<string> filled = null;
            List<ArtifactError> warnings = null;
            string facts = null;
            if (args["manifest"] != null || args["scene"] != null)
            {
                if (template != null) throw new ArgumentException("bad_value: give a template or a manifest and scene, not both");
                var parseErrors = new List<ArtifactError>();
                var manifest = Written(args["manifest"], "manifest", parseErrors);
                var scene = Written(args["scene"], "scene", parseErrors);
                if (parseErrors.Count > 0) return Refused(parseErrors, null);
                if (manifest == null || scene == null)
                    throw new ArgumentException("bad_value: a written artifact needs both manifest and scene, as JSON objects");
                filled = Fill(ref manifest, scene);
                made = ArtifactLibrary.FromScene(manifest, scene, out var errors, out warnings, out facts);
                if (made == null)
                {
                    // Returned as data, not thrown, so the agent can fix every problem in one go (C-12 section 9).
                    var refused = Refused(errors, filled);
                    if (warnings.Count > 0) refused["checks"] = Items(warnings, ArtifactValidator.MaxReported);
                    if (facts != null) refused["facts"] = facts;
                    return refused;
                }
                written = scene;
                review = ReviewBody(args["request"], manifest, scene, facts);
            }
            else
            {
                if (string.IsNullOrEmpty(template)) throw new ArgumentException("bad_value: say which template (" + string.Join(", ", ArtifactLibrary.Templates) + "), or give a manifest and scene");
                made = ArtifactLibrary.FromTemplate(template, args["inputs"] as JObject);
            }
            ResetAttempts();
            var result = new JObject { ["id"] = made.Id, ["version"] = made.Version, ["change"] = made.Change };
            if (filled != null && filled.Count > 0) result["filled"] = new JArray(filled.ToArray());
            if (written != null)
            {
                var built = Built(written);
                result["built"] = built;
                if (((JArray)built["controls"]).Count == 0)
                    result["say"] = "It has no controls: it's there to look at.";
                if (facts != null) result["facts"] = facts;
                if (warnings != null && warnings.Count > 0)
                {
                    result["checks"] = Items(warnings, ArtifactValidator.MaxReported);
                    result["next"] = $"Saved, but {warnings.Count} thing{(warnings.Count == 1 ? "" : "s")} may look physically off (checks). "
                                     + "If the person would notice, fix them and call artifact_create again with the same id; otherwise carry on.";
                }
            }
            if (args.Value<bool?>("open") != false) result["opened"] = Opened(Host(), made.Id, h => h.Open(made.Id));
            return result;
        }

        /// <summary>
        /// A manifest or scene as the agent sent it: an object, or the same object written as a JSON string (a common slip).
        /// Null if absent. A string that isn't a JSON object is an error at that path.
        /// </summary>
        static JObject Written(JToken token, string path, List<ArtifactError> errors)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token is JObject o) return (JObject)o.DeepClone();
            if (token.Type == JTokenType.String)
            {
                try
                {
                    if (JToken.Parse((string)token) is JObject parsed) return parsed;
                }
                catch (Newtonsoft.Json.JsonException e)
                {
                    errors.Add(new ArtifactError("bad_value", path, "isn't valid JSON: " + e.Message, $"send {path} as a JSON object, not a string"));
                    return null;
                }
            }
            errors.Add(new ArtifactError("bad_value", path, "must be a JSON object", $"send {path} as a JSON object"));
            return null;
        }

        /// <summary>
        /// Fills what every written artifact has and only the host can know or would always say the same way, when it's
        /// absent: schema, id (from the title), version, kind, category, template, links, runtime, scene file, script,
        /// inputs, scenarios, data, assets, outputs, and a permissions block that grants nothing. It never fills
        /// <c>space</c> or generators, and never grants a permission. The saved files are still full C-12. Returns the
        /// paths it filled, for the result's <c>filled</c>. <paramref name="manifest"/> is rebuilt in the contract's key
        /// order when anything was filled.
        /// </summary>
        public static List<string> Fill(ref JObject manifest, JObject scene)
        {
            var given = manifest;
            var filled = new List<string>();
            var fills = new Dictionary<string, JToken>();
            void Default(string key, JToken value)
            {
                var have = given[key];
                // An explicit null script or template is already the default; any other value is the agent's to keep.
                if (have != null && (have.Type != JTokenType.Null || key == "script" || key == "template")) return;
                fills[key] = value;
                filled.Add(key);
            }
            Default("schema", ArtifactSchema.Manifest);
            string slug = ArtifactValidator.Slug(S(manifest, "title"));
            if ((manifest["id"] == null || manifest["id"].Type == JTokenType.Null) && !string.IsNullOrEmpty(slug))
            {
                // The folder is the id, and FromScene refuses a template's: never fill one it would refuse ("heart" is a template).
                fills["id"] = Array.IndexOf(ArtifactLibrary.Templates, slug) >= 0 ? "my_" + slug : slug;
                filled.Add("id");
            }
            Default("version", 1);
            Default("kind", "standalone");
            Default("category", HasKind(scene, "sim") ? "sim" : "tool");
            Default("template", JValue.CreateNull());
            Default("links", new JObject { ["project"] = null, ["project_version"] = null, ["repo"] = null, ["thing"] = null });
            Default("runtime", new JObject { ["type"] = "scene", ["min_runtime"] = MinRuntime(scene) });
            Default("scene", "scene.json");
            Default("script", JValue.CreateNull());
            Default("inputs", new JArray());
            Default("scenarios", new JArray(new JObject { ["id"] = "default", ["seed"] = 0, ["params"] = new JObject() }));
            Default("data", new JArray());
            Default("assets", new JArray());
            Default("permissions", new JObject
            {
                ["network"] = new JArray(), ["connectors"] = new JArray(), ["storage"] = "artifact-local",
                ["write_project"] = "none", ["room"] = "none", ["things"] = "none",
            });
            Default("outputs", new JArray());
            if (scene["schema"] == null)
            {
                scene.AddFirst(new JProperty("schema", ArtifactSchema.Scene));
                filled.Add("scene.schema");
            }
            if (fills.Count == 0) return filled;

            var ordered = new JObject();
            foreach (string key in ArtifactSchema.Keys.Manifest)
                if (fills.TryGetValue(key, out var value)) ordered[key] = value;
                else if (manifest[key] != null) ordered[key] = manifest[key];
            foreach (var p in manifest.Properties())  // keys the contract doesn't have stay, so the validator names them
                if (ordered[p.Name] == null && Array.IndexOf(ArtifactSchema.Keys.Manifest, p.Name) < 0) ordered[p.Name] = p.Value;
            manifest = ordered;
            return filled;
        }

        // Fill runs before the validator, so it reads the agent's fields without trusting their types.
        static string S(JObject o, string key) => o[key] is JValue v && v.Type == JTokenType.String ? (string)v : null;

        static bool HasKind(JObject scene, string kind)
        {
            if (scene["nodes"] is JArray nodes)
                foreach (var n in nodes)
                    if (n is JObject o && S(o, "kind") == kind) return true;
            return false;
        }

        /// <summary>
        /// The lowest host that builds what this scene uses, from the history in <see cref="ArtifactSchema.HostRuntime"/>:
        /// 2 room_hoops; 3 arrays from tables; 4 gltf, timeline, animate; 5 the versions widget; 6 the pendulum solver and
        /// lines through nodes.
        /// </summary>
        public static int MinRuntime(JObject scene)
        {
            int need = 1;
            if (scene["generators"] is JArray generators)
                foreach (var g in generators)
                    if (g is JObject o && S(o, "type") == "room_hoops") need = Math.Max(need, 2);
            if (scene["rules"] is JArray rules)
                foreach (var r in rules)
                    if (r is JObject o && S(o, "type") == "animate") need = Math.Max(need, 4);
            if (scene["nodes"] is JArray nodes)
                foreach (var n in nodes)
                {
                    if (!(n is JObject node)) continue;
                    switch (S(node, "kind"))
                    {
                        case "array": if (node["source"] is JObject) need = Math.Max(need, 3); break;
                        case "gltf": case "timeline": need = Math.Max(need, 4); break;
                        case "sim": if (S(node, "solver") == "pendulum") need = Math.Max(need, 6); break;
                        case "line": if (node["through"] != null) need = Math.Max(need, 6); break;
                        case "panel":
                            if (node["widgets"] is JArray widgets)
                                foreach (var w in widgets)
                                    if (w is JObject wo && S(wo, "type") == "versions") need = Math.Max(need, 5);
                            break;
                    }
                }
            return need;
        }

        static readonly HashSet<string> PartKinds = new HashSet<string> { "primitive", "text", "line", "gltf", "array" };

        /// <summary>
        /// What was built, so the agent can tell the person what they can do with it: how many parts, every control (a
        /// slider names its input, a button its id and what it does), and what moves (a node and what moves it: a
        /// timeline, a sim, or a slider through a bound pos, rot, scale or size).
        /// </summary>
        public static JObject Built(JObject scene)
        {
            int parts = 0;
            var controls = new JArray();
            var moving = new JArray();
            var seen = new HashSet<string>();
            void Moves(string node, string by)
            {
                if (node != null && seen.Add(node + "|" + by)) moving.Add(new JObject { ["node"] = node, ["by"] = by });
            }
            var rules = scene["rules"] as JArray ?? new JArray();
            var nodes = scene["nodes"] as JArray ?? new JArray();
            foreach (var t in nodes)
            {
                if (!(t is JObject n)) continue;
                string kind = n.Value<string>("kind"), id = n.Value<string>("id");
                if (PartKinds.Contains(kind)) parts++;
                if (kind == "panel" && n["widgets"] is JArray widgets)
                    foreach (var w in widgets)
                    {
                        if (!(w is JObject widget)) continue;
                        string type = widget.Value<string>("type");
                        if (type == "slider") controls.Add(new JObject { ["slider"] = widget["input"] });
                        else if (type == "button")
                            controls.Add(new JObject { ["button"] = widget["id"], ["label"] = widget["label"], ["does"] = Does(rules, widget.Value<string>("id")) });
                    }
                if (kind == "sim" && n["bodies"] is JArray bodies)
                    foreach (var b in bodies) Moves(b.Value<string>("node"), "sim " + id);
                foreach (string field in new[] { "pos", "rot", "scale", "size" })
                    if (n[field] is JArray v)
                        foreach (var c in v)
                            if (c.Type == JTokenType.String) { Moves(id, "slider " + ((string)c).TrimStart('$')); break; }
            }
            foreach (var r in rules)
                if (r is JObject rule && rule.Value<string>("type") == "animate" && rule.Value<string>("property") != "visible")
                    Moves(rule.Value<string>("target"), "timeline " + rule.Value<string>("timeline"));
            return new JObject { ["parts"] = parts, ["controls"] = controls, ["moving"] = moving };
        }

        // "play clock", "show lamp_on", ...: what a button's on_input rules do, in a few words.
        static string Does(JArray rules, string button)
        {
            var said = new List<string>();
            foreach (var r in rules)
            {
                if (!(r is JObject rule) || rule.Value<string>("type") != "on_input" || rule.Value<string>("input") != button || !(rule["do"] is JArray actions)) continue;
                foreach (var a in actions)
                {
                    if (!(a is JObject action)) continue;
                    if (action["timeline"] != null) said.Add($"{action["action"]} {action["timeline"]}");
                    else if (action["sim"] != null) said.Add($"{action["action"]} {action["sim"]}");
                    else if (action["run"] != null) said.Add($"{action["run"]} the run");
                    else if (action["set"] != null)
                    {
                        string set = (string)action["set"];
                        int dot = set?.IndexOf('.') ?? -1;
                        said.Add((action.Value<bool?>("value") == true ? "show " : "hide ") + (dot > 0 ? set.Substring(0, dot) : set));
                    }
                }
            }
            return said.Count > 0 ? string.Join(", ", said) : null;
        }

        // A refusal, counted: the third in a row tells the agent to stop instead of trying again.
        static JObject Refused(List<ArtifactError> errors, List<string> filled)
        {
            var now = DateTime.UtcNow;
            if ((now - lastAttempt).TotalSeconds > AttemptWindowS) attempts = 0;
            lastAttempt = now;
            attempts++;
            var result = Invalid(errors);
            result["attempt"] = attempts;
            if (filled != null && filled.Count > 0) result["filled"] = new JArray(filled.ToArray());
            if (attempts >= MaxAttempts)
                result["next"] = $"Nothing was saved, and this was try {attempts}. Stop trying: tell the person in one short sentence what still "
                                 + "doesn't work (the first error, in plain words), and offer the closest template from artifact_list, or a simpler version.";
            return result;
        }

        /// <summary>
        /// A refusal as data: the first <see cref="ArtifactValidator.MaxReported"/> errors (structural first, as the
        /// validator sorts them), each with its hint and allowed values when it has them, and a count of the rest. The
        /// same unknown key with the same hint on many objects (position on every node) is one entry, with the other
        /// paths under <c>also</c>, so one slip doesn't crowd out every other problem.
        /// </summary>
        public static JObject Invalid(List<ArtifactError> errors)
        {
            var entries = new List<ArtifactError>(errors.Count);
            var repeats = new Dictionary<string, List<string>>();
            var others = new Dictionary<ArtifactError, List<string>>();
            foreach (var e in errors)
            {
                if (e.code == "unknown_field" && e.path != null)
                {
                    string key = Leaf(e.path) + "\n" + e.hint;
                    if (repeats.TryGetValue(key, out var paths)) { paths.Add(e.path); continue; }
                    repeats[key] = paths = new List<string>();
                    others[e] = paths;
                }
                entries.Add(e);
            }
            var list = new JArray();
            for (int i = 0; i < entries.Count && i < ArtifactValidator.MaxReported; i++)
            {
                var e = entries[i];
                var item = new JObject { ["code"] = e.code, ["path"] = e.path, ["message"] = e.message };
                if (others.TryGetValue(e, out var also) && also.Count > 0)
                {
                    item["message"] = $"{e.message}, and in {also.Count} more place{(also.Count == 1 ? "" : "s")}";
                    var shown = new JArray();
                    for (int j = 0; j < also.Count && j < 10; j++) shown.Add(also[j]);
                    item["also"] = shown;
                }
                if (e.hint != null) item["hint"] = e.hint;
                if (e.allowed != null) item["allowed"] = new JArray(e.allowed);
                list.Add(item);
            }
            int more = entries.Count - list.Count;
            var result = new JObject
            {
                ["status"] = "invalid", ["errors"] = list,
                ["next"] = "Nothing was saved. Fix these and call artifact_create again with the whole manifest and scene."
                           + (more > 0 ? $" {more} more problems aren't listed; fixing these first usually clears them." : ""),
            };
            if (more > 0) result["more"] = more;
            return result;
        }

        /// <summary>Errors or warnings as the tool result lists them: code, path, message, and hint when there is one.</summary>
        static JArray Items(List<ArtifactError> list, int most)
        {
            var items = new JArray();
            for (int i = 0; i < list.Count && i < most; i++)
            {
                var e = list[i];
                var item = new JObject { ["code"] = e.code, ["path"] = e.path, ["message"] = e.message };
                if (e.hint != null) item["hint"] = e.hint;
                if (e.allowed != null) item["allowed"] = new JArray(e.allowed);
                items.Add(item);
            }
            return items;
        }

        static string Leaf(string path)
        {
            int dot = path.LastIndexOf('.');
            return dot < 0 ? path : path.Substring(dot + 1);
        }

        // ---------- C2: an advisory review by the hub (POST /artifacts/review), never a gate ----------

        /// <summary>How long a create waits for the hub's review before answering without it. The hub gives up at 2.2 s.</summary>
        public const float ReviewTimeoutS = 2.5f;
        public const string ReviewPath = "/artifacts/review";
        /// <summary>The hub refuses (422) a longer request or facts text: both are cut to fit.</summary>
        public const int ReviewRequestMax = 500, ReviewFactsMax = 6000;

        /// <summary>What happened to the last create's review: "reviewed", or why not ("template_or_refused",
        /// "edit_mode", "mock", "hub_off", "timeout", "failed", "skipped:no_key", "empty", ...). Checks and logs read it.</summary>
        public static string LastReview { get; private set; }

        /// <summary>
        /// artifact_create for Albie. The artifact is saved and opened first, exactly as <see cref="Create(JObject)"/>
        /// does; only then, for a written artifact with the hub live, the tool result waits up to
        /// <see cref="ReviewTimeoutS"/> for the hub's review, which goes into <c>result.review</c> and nowhere else.
        /// Mock mode, hub off, no key, a hub error, a fail or the timeout all answer with the result unchanged.
        /// A template or a refusal answers at once.
        /// </summary>
        public static void CreateAsync(JObject args, Action<JObject> done)
        {
            var result = Create(args, out var body);
            string skip = body == null ? "template_or_refused" : NoReview();
            if (skip != null)
            {
                LastReview = skip;
                done(result);
                return;
            }

            bool answered = false;
            void Finish(JObject review, string why)
            {
                if (answered) return;
                answered = true;
                LastReview = why;
                if (review != null) result["review"] = review;
                Debug.Log($"[Artifacts] review of {(string)result["id"]}: {why}"
                          + (review != null ? " " + review.ToString(Newtonsoft.Json.Formatting.None) : ""));
                done(result);
            }
            try
            {
                ForgeNet.Post(ReviewPath, body, text => Finish(Review(text, out string why), why), error => Finish(null, "failed: " + error));
                ForgeNet.Instance.StartCoroutine(After(ReviewTimeoutS, () => Finish(null, "timeout")));
            }
            catch (Exception e)  // the artifact is saved: its result goes back whatever happens to the review
            {
                Finish(null, "failed: " + e.Message);
            }
        }

        /// <summary>Why no review is asked for right now, or null when the hub is live.</summary>
        public static string NoReview()
        {
            if (!Application.isPlaying) return "edit_mode";
            if (ForgeNet.IsMock) return "mock";  // the mock Post answers "{}" (ForgeNet.Request)
            if (!ForgeNet.Connected) return "hub_off";
            return null;
        }

        static System.Collections.IEnumerator After(float seconds, Action then)
        {
            yield return new WaitForSecondsRealtime(seconds);
            then();
        }

        /// <summary>The request body: the person's words (optional), the manifest and scene as saved, and the facts.</summary>
        public static JObject ReviewBody(JToken request, JObject manifest, JObject scene, string facts)
        {
            string said = request != null && request.Type == JTokenType.String ? ((string)request).Trim() : "";
            if (said.Length > ReviewRequestMax) said = said.Substring(0, ReviewRequestMax);
            facts = facts ?? "";
            if (facts.Length > ReviewFactsMax) facts = facts.Substring(0, ReviewFactsMax);
            return new JObject { ["request"] = said, ["manifest"] = manifest, ["scene"] = scene, ["facts"] = facts };
        }

        /// <summary>
        /// The hub's answer as <c>result.review</c>, or null when it isn't a review (<paramref name="why"/> says what it
        /// was): "{}" from the mock, status skipped or error, no verdicts, or text that isn't JSON. Advisory only.
        /// </summary>
        public static JObject Review(string text, out string why)
        {
            JObject answer;
            try { answer = JObject.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text); }
            catch (Newtonsoft.Json.JsonException)
            {
                why = "bad_response";
                return null;
            }
            string status = answer["status"]?.Type == JTokenType.String ? (string)answer["status"] : null;
            if (status != "ok")
            {
                string reason = answer["reason"]?.Type == JTokenType.String ? (string)answer["reason"] : null;
                why = status == null ? "empty" : reason != null ? status + ":" + reason : status;
                return null;
            }
            var verdicts = new JArray();
            var flagged = new List<string>();
            if (answer["verdicts"] is JArray list)
                foreach (var v in list)
                {
                    if (!(v is JObject o) || o["question"]?.Type != JTokenType.String) continue;
                    bool ok = o["ok"]?.Type != JTokenType.Boolean || (bool)o["ok"];
                    double confidence = o["confidence"] != null && (o["confidence"].Type == JTokenType.Float || o["confidence"].Type == JTokenType.Integer)
                        ? Math.Round((double)o["confidence"], 2) : 0;
                    verdicts.Add(new JObject { ["question"] = o["question"], ["choice"] = o["choice"], ["ok"] = ok, ["confidence"] = confidence });
                    if (!ok) flagged.Add((string)o["question"]);
                }
            if (verdicts.Count == 0)
            {
                why = "no_verdicts";
                return null;
            }
            why = "reviewed";
            var review = new JObject();
            if (answer["model"]?.Type == JTokenType.String) review["by"] = answer["model"];
            review["verdicts"] = verdicts;
            review["note"] = flagged.Count == 0
                ? "A text reviewer found nothing wrong. Advisory only."
                : "Advisory only, it's already saved: a text reviewer (it can't see it) doubts " + string.Join(", ", flagged)
                  + ". If you agree, fix it and call artifact_create again with the same id; otherwise carry on.";
            if (flagged.Count > 0) review["flagged"] = new JArray(flagged.ToArray());
            return review;
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
            ArtifactHost.Current ?? ArtifactBootstrap.Ensure()
            ?? throw new ArgumentException("no_host: there's nowhere to show artifacts in this scene");

        // What the agent needs after an open: waiting for a tap, refused (with why), or open with its layout and the line to say.
        static JObject Opened(ArtifactHost host, string id, Action<ArtifactHost> open)
        {
            if (host.FollowsUser) host.ComeToUser();  // open() closes whatever was showing first
            open(host);
            if (host.AwaitingApproval)
            {
                // Say what this artifact actually asks for (the sheet's own lines), not what the slalom asks for.
                var asks = host.OpenManifest != null ? ArtifactApprovals.Lines(host.OpenManifest) : new List<string>();
                return new JObject
                {
                    ["id"] = id, ["status"] = "awaiting_approval",
                    ["asks"] = new JArray(asks.ToArray()),
                    ["next"] = "It's asking for permission the first time. Tell the person in your own words what it will use ("
                               + string.Join("; ", asks) + ") and ask them to tap Allow.",
                };
            }
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
            if (host.Tables.Count > 0)
            {
                var data = new JArray();
                foreach (var t in host.Tables.Values)
                    data.Add(new JObject
                    {
                        ["name"] = t.Name, ["source"] = t.Source, ["rows"] = t.Rows.Count, ["fetched_at"] = t.FetchedAt,
                        ["sample"] = t.Sample, ["from"] = t.From,
                    });
                result["data"] = data;
            }
            if (host.DataMessage != null)
                result["say"] = result["say"] != null ? (string)result["say"] + " " + host.DataMessage : host.DataMessage;
            return result;
        }
    }
}
