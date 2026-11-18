using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Forge.Artifacts
{
    /// <summary>
    /// Courses from a description (host 7). A course spec says what the course is for and what is on it, in order:
    /// <code>
    /// { "title": "Kitchen run", "thing": "truck", "surface": "floor", "around": "TABLE",
    ///   "elements": [ {"type": "straight", "length_m": 2}, {"type": "cones", "count": 4}, {"type": "bay"} ] }
    /// </code>
    /// <see cref="Compile"/> checks it and writes the full C-12 manifest and scene: one <c>room_course</c> generator
    /// (<see cref="RoomCourse"/>) that the headset fits to the room, the nodes that stand on its slots, the metrics, the
    /// scoreboard and the run block. The agent never writes a room position, a zone or a rule, so "make me a course with
    /// a straight, four cones and a parking bay" can't put a cone in the couch. No scripts: the spec is data.
    ///
    /// The built-in courses the planner's tests need are specs too (<see cref="Templates"/>): <c>dash</c>, <c>park</c>,
    /// <c>hover</c> and <c>landing</c>. <c>artifact_create {template: "dash"}</c> makes one, as it makes a slalom.
    /// </summary>
    public static class ArtifactCourses
    {
        /// <summary>Course templates made here, besides <see cref="ArtifactLibrary.Templates"/>.</summary>
        public static readonly string[] Templates = { "dash", "park", "hover", "landing" };

        public static readonly string[] SpecKeys = { "id", "title", "thing", "surface", "around", "elements", "timeout_s" };

        public const int MaxTitle = 40;
        const string Cone = "#FF7A1A", Post = "#4C8DFF", Mark = "#FFD24C", Guide = "#7CFFB2";

        public static bool IsTemplate(string name) => name != null && Array.IndexOf(Templates, name) >= 0;

        /// <summary>What a template's tile and Albie call it.</summary>
        public static string Title(string template)
        {
            switch (template)
            {
                case "dash": return "Dash";
                case "park": return "Parking";
                case "hover": return "Hover";
                case "landing": return "Landing";
                default: return template;
            }
        }

        /// <summary>The Thing a template is for: "truck" or "drone".</summary>
        public static string ThingOf(string template) => template == "hover" || template == "landing" ? "drone" : "truck";

        /// <summary>
        /// A built-in course as a spec, or null. <paramref name="inputs"/> gets the sliders it has (a spec from the agent
        /// has none): the dash's length, the hover's hold time. On a desk it is a separate artifact (<c>dash_desk</c>),
        /// so its runs never mix with the floor course's.
        /// </summary>
        public static JObject Preset(string template, string surface, out JArray inputs)
        {
            inputs = new JArray();
            bool desk = surface == "desk";
            JArray elements;
            switch (template)
            {
                case "dash":
                    inputs.Add(Input("length_m", "number", 1.5, 10, 6, "Length"));
                    elements = new JArray(new JObject { ["type"] = "straight", ["length_m"] = "$length_m", ["min_length_m"] = 1.5 });
                    break;
                case "park":
                    elements = new JArray(new JObject { ["type"] = "straight", ["length_m"] = 2.0, ["min_length_m"] = 0.8 }, new JObject { ["type"] = "bay" });
                    break;
                case "hover":
                    inputs.Add(Input("hold_s", "integer", 5, 120, 60, "Hold"));
                    elements = new JArray(new JObject { ["type"] = "hover", ["hold_s"] = "$hold_s" });
                    break;
                case "landing":
                    elements = new JArray(new JObject { ["type"] = "gates", ["count"] = 1, ["spacing_m"] = 1.2 }, new JObject { ["type"] = "pad" });
                    break;
                default:
                    return null;
            }
            return new JObject
            {
                ["id"] = desk ? template + "_desk" : template,
                ["title"] = desk ? Title(template) + " (desk)" : Title(template),
                ["thing"] = ThingOf(template),
                ["surface"] = desk ? "desk" : "floor",
                ["elements"] = elements,
            };
        }

        static JObject Input(string name, string type, double min, double max, double value, string label) => new JObject
        {
            ["name"] = name, ["type"] = type,
            ["min"] = type == "integer" ? (JToken)(int)min : min, ["max"] = type == "integer" ? (JToken)(int)max : max,
            ["default"] = type == "integer" ? (JToken)(int)value : value, ["label"] = label,
        };

        /// <summary>What is wrong with a course spec from the agent, with hints it can act on. Empty when it's sound.</summary>
        public static List<ArtifactError> Check(JObject spec, bool refs = false)
        {
            var errors = new List<ArtifactError>();
            if (spec == null)
            {
                errors.Add(new ArtifactError("bad_value", "course", "must be a JSON object", "send course as an object: {\"title\": ..., \"thing\": \"truck\", \"elements\": [...]}"));
                return errors;
            }
            foreach (var p in spec.Properties())
                if (Array.IndexOf(SpecKeys, p.Name) < 0)
                    errors.Add(new ArtifactError("unknown_field", "course." + p.Name, $"a course has no field \"{p.Name}\"",
                        (p.Name == "nodes" || p.Name == "rules" || p.Name == "run" || p.Name == "generators" || p.Name == "pos" || p.Name == "positions"
                            ? "a course is only a list of elements: the headset places them and scores the run itself. "
                            : "remove it; ") + "a course takes: " + string.Join(", ", SpecKeys), SpecKeys));
            var title = spec["title"];
            if (title != null && title.Type != JTokenType.Null && (title.Type != JTokenType.String || ((string)title).Trim().Length == 0 || ((string)title).Length > MaxTitle))
                errors.Add(new ArtifactError("bad_value", "course.title", $"must be a short name, up to {MaxTitle} characters", "two or three words the person would say, like \"Kitchen run\""));
            var id = spec["id"];
            if (id != null && id.Type != JTokenType.Null && (id.Type != JTokenType.String || ArtifactValidator.Slug((string)id) != (string)id))
                errors.Add(new ArtifactError("bad_value", "course.id", "use lowercase letters, digits and _", "leave id out: it is made from the title"));
            var timeout = spec["timeout_s"];
            if (timeout != null && timeout.Type != JTokenType.Null &&
                !((timeout.Type == JTokenType.Integer || timeout.Type == JTokenType.Float) && (double)timeout >= 10 && (double)timeout <= 600))
                errors.Add(new ArtifactError("bad_value", "course.timeout_s", "must be a number of seconds from 10 to 600", "leave it out for 90 s"));
            RoomCourse.Check(spec, "course", refs, errors);
            return errors;
        }

        /// <summary>
        /// A checked spec as a manifest and scene (C-12, host 7), or false with what to fix. <paramref name="inputs"/>
        /// are the sliders a built-in course has (<see cref="Preset"/>); null for none.
        /// </summary>
        public static bool Compile(JObject spec, JArray inputs, out JObject manifest, out JObject scene, out List<ArtifactError> errors)
        {
            manifest = scene = null;
            errors = Check(spec, inputs != null && inputs.Count > 0);
            if (errors.Count > 0) return false;

            var s = RoomCourse.Read(spec, null);
            string title = spec.Value<string>("title")?.Trim();
            if (string.IsNullOrEmpty(title)) title = "My course";
            string id = spec.Value<string>("id");
            if (string.IsNullOrEmpty(id)) id = ArtifactValidator.Slug(title);
            if (string.IsNullOrEmpty(id)) id = "my_course";
            // The folder is the id: a written course never takes a template's (a built-in one passes its own id in).
            if (spec["id"] == null && (Array.IndexOf(ArtifactLibrary.Templates, id) >= 0 || IsTemplate(id) || id.EndsWith("_desk"))) id = "my_" + id;
            bool desk = spec.Value<string>("surface") == "desk";
            bool drone = s.Drone;
            var end = s.End;
            bool cones = s.Has("cones"), gates = cones || s.Has("gates"), posts = s.Has("gates") && !drone, hoops = s.Has("hoops");
            bool stops = end.HasValue && end.Value.Type != "hover", hovers = end.HasValue && end.Value.Type == "hover";
            JToken holdToken = null;
            if (end.HasValue) holdToken = (spec["elements"] as JArray)?[s.Elements.Count - 1]?["hold_s"];
            JToken hold = holdToken != null && holdToken.Type != JTokenType.Null ? holdToken.DeepClone() : end.HasValue ? (JToken)Math.Round(end.Value.HoldS, 2) : null;

            // ---- the generator: the spec's own fields, nothing else
            var generatorParams = new JObject { ["thing"] = s.Thing, ["surface"] = desk ? "desk" : "floor" };
            if (!string.IsNullOrEmpty(s.Around)) generatorParams["around"] = s.Around;
            generatorParams["elements"] = spec["elements"].DeepClone();

            // ---- nodes
            var nodes = new JArray();
            var hit = new JArray();
            if (cones)
            {
                nodes.Add(Copies("cones", "course.cones", new JObject
                {
                    ["kind"] = "primitive", ["shape"] = "cone", ["pos"] = V(0, 0.12, 0), ["size"] = V(0.12, 0.24, 0.12), ["color"] = Cone, ["collider"] = true,
                }));
                hit.Add("cones");
            }
            if (posts)
            {
                nodes.Add(Copies("posts", "course.posts", new JObject
                {
                    ["kind"] = "primitive", ["shape"] = "cylinder", ["pos"] = V(0, 0.15, 0), ["size"] = V(0.05, 0.3, 0.05), ["color"] = Post, ["collider"] = true,
                }));
                hit.Add("posts");
            }
            if (gates)
                nodes.Add(Copies("gates", "course.gates", new JObject
                {
                    ["kind"] = "zone", ["pos"] = drone ? V(0, 1.0, 0) : V(0, 0.25, 0), ["size"] = drone ? V(0.8, 0.8, 0.3) : V(0.6, 0.5, 0.2),
                }));
            if (hoops)
            {
                var h = s.First("hoops");
                double d = h.Width, up = h.Height, post = RoomHoops.Post, side = d * 0.5 + post * 0.5, tall = up + d * 0.5 + post * 0.5;
                nodes.Add(Copies("hoop_left", "course.hoops", Bar(V(-side, tall * 0.5, 0), V(post, tall, post))));
                nodes.Add(Copies("hoop_right", "course.hoops", Bar(V(side, tall * 0.5, 0), V(post, tall, post))));
                nodes.Add(Copies("hoop_top", "course.hoops", Bar(V(0, up + d * 0.5, 0), V(d + 2 * post, post, post))));
                nodes.Add(Copies("hoop_bottom", "course.hoops", Bar(V(0, up - d * 0.5 - post * 0.5, 0), V(d + 2 * post, post, post))));
                nodes.Add(Copies("hoops", "course.hoops", new JObject { ["kind"] = "zone", ["pos"] = V(0, up, 0), ["size"] = V(d, d, 0.2) }));
                hit.Add("hoop_left"); hit.Add("hoop_right"); hit.Add("hoop_top"); hit.Add("hoop_bottom");
            }
            nodes.Add(new JObject
            {
                ["id"] = "start", ["kind"] = "zone", ["at"] = "course.start",
                // The drone waits landed at the start, and a run starts on entering the start zone: its zone begins 0.3 m
                // up (the lowest hover is 0.2 m), so the clock starts at take-off, not when the run is armed.
                ["pos"] = drone ? V(0, 0.9, 0) : V(0, 0.25, 0), ["size"] = drone ? V(0.9, 1.2, 0.6) : V(0.9, 0.5, 0.3),
            });
            var finish = new JObject { ["id"] = "finish", ["kind"] = "zone", ["at"] = "course.finish" };
            if (hovers)
            {
                double w = end.Value.Width, up = end.Value.Height;
                finish["pos"] = V(0, up, 0);
                finish["size"] = V(w, w, w);
                nodes.Add(finish);
                // A zone draws only its bottom edge: four thin corner posts show how tall the box is.
                for (int i = 0; i < 4; i++)
                    nodes.Add(new JObject
                    {
                        ["id"] = "box_edge_" + (i + 1), ["kind"] = "primitive", ["shape"] = "box", ["parent"] = "finish",
                        ["pos"] = V((i & 1) == 0 ? -w * 0.5 : w * 0.5, 0, (i & 2) == 0 ? -w * 0.5 : w * 0.5), ["size"] = V(0.01, w, 0.01), ["color"] = Mark,
                    });
            }
            else if (stops)
            {
                // A pad's zone stops below the drone's lowest hover (0.2 m), so only a drone standing on it is inside.
                double w = end.Value.Width, l = end.Value.Length, tall = drone ? 0.16 : 0.5;
                finish["pos"] = V(0, tall * 0.5, 0);
                finish["size"] = V(w, tall, l);
                nodes.Add(finish);
                var spot = new JObject
                {
                    ["id"] = "spot", ["kind"] = "primitive", ["shape"] = "box", ["at"] = "course.finish",
                    ["pos"] = V(0, 0.003, 0), ["size"] = V(w, 0.004, l), ["color"] = Mark,
                };
                // The drone lands on colliders (away from its start, a pad without one is flown through to the floor).
                // The truck gets none: it would bump over the edge.
                if (drone) spot["collider"] = true;
                nodes.Add(spot);
            }
            else
            {
                finish["pos"] = drone ? V(0, 0.6, 0) : V(0, 0.25, 0);
                finish["size"] = drone ? V(0.9, 1.2, 0.4) : V(0.9, 0.5, 0.3);
                nodes.Add(finish);
                nodes.Add(new JObject
                {
                    ["id"] = "time_label", ["kind"] = "text", ["parent"] = "finish", ["pos"] = V(0, drone ? 0.75 : 0.4, 0),
                    ["text"] = "{completion_time_s} s", ["height"] = 0.08, ["color"] = "#FFFFFF", ["billboard"] = true, ["visible"] = false,
                });
            }
            nodes.Add(new JObject { ["id"] = "guide", ["kind"] = "line", ["source"] = "course.path", ["width"] = 0.01, ["color"] = Guide, ["pos"] = V(0, 0.01, 0) });

            // ---- metrics, in the order the scoreboard shows them
            var rules = new JArray();
            var outputs = new JArray();
            var stats = new JArray();
            var columns = new JArray();
            void Metric(JObject rule, string label, string unit, bool column, bool whole = false)
            {
                rule.AddFirst(new JProperty("type", "metric"));
                rules.Add(rule);
                string name = (string)rule["name"];
                var output = new JObject { ["name"] = name, ["type"] = whole ? "integer" : "number" };
                var stat = new JObject { ["type"] = "stat", ["metric"] = name, ["label"] = label };
                if (unit != null)
                {
                    output["unit"] = unit;
                    stat["unit"] = unit;
                }
                output["label"] = label;
                outputs.Add(output);
                stats.Add(stat);
                if (column) columns.Add(name);
            }
            Metric(new JObject { ["name"] = "completion_time_s", ["op"] = "elapsed" }, "Time", "s", true);
            if (hovers)
            {
                Metric(new JObject { ["name"] = "held_s", ["op"] = "hold" }, "Held", "s", false);
                Metric(new JObject { ["name"] = "drift_m", ["op"] = "zone_drift", ["zones"] = "finish" }, "Drift", "m", true);
            }
            if (gates) Metric(new JObject { ["name"] = "gates_passed", ["op"] = "zone_count", ["zones"] = "gates", ["ordered"] = true }, "Gates", null, false, true);
            if (hoops) Metric(new JObject { ["name"] = "hoops_passed", ["op"] = "zone_count", ["zones"] = "hoops", ["ordered"] = true }, "Hoops", null, false, true);
            if (hit.Count > 0) Metric(new JObject { ["name"] = "collisions", ["op"] = "hit_count", ["nodes"] = hit }, "Hits", null, true, true);
            if (stops)
            {
                Metric(new JObject { ["name"] = "off_centre_m", ["op"] = "zone_offset", ["zones"] = "finish" }, "Off centre", "m", true);
                Metric(new JObject { ["name"] = "in_a_row", ["op"] = "streak" }, "In a row", null, false, true);
            }
            Metric(new JObject { ["name"] = "top_speed_ms", ["op"] = "max", ["probe"] = "speed_ms" }, "Top speed", "m/s", !hovers);
            while (columns.Count > 3) columns.RemoveAt(columns.Count - 1);

            var reset = new JArray(new JObject { ["run"] = "reset" });
            bool timed = !hovers && !stops;
            if (timed) reset.Add(new JObject { ["set"] = "time_label.visible", ["value"] = false });
            rules.Add(new JObject { ["type"] = "on_input", ["input"] = "rerun", ["do"] = reset });
            if (timed)
                rules.Add(new JObject
                {
                    ["type"] = "on_zone", ["zone"] = "finish", ["event"] = "enter",
                    ["do"] = new JArray(new JObject { ["set"] = "time_label.visible", ["value"] = true }),
                });

            // ---- the scoreboard
            var widgets = new JArray { new JObject { ["type"] = "note", ["text"] = Steps(s, hold), ["after_run"] = After(s) } };
            foreach (var shown in stats) widgets.Add(shown);
            widgets.Add(new JObject { ["type"] = "runs", ["columns"] = columns, ["limit"] = 5 });
            if (inputs != null)
                foreach (var input in inputs) widgets.Add(new JObject { ["type"] = "slider", ["input"] = input["name"] });
            widgets.Add(new JObject { ["type"] = "button", ["id"] = "rerun", ["label"] = drone ? "Fly again" : "Reset" });
            nodes.Add(new JObject { ["id"] = "scoreboard", ["kind"] = "panel", ["title"] = title, ["widgets"] = widgets });

            // ---- the run
            var needs = new JArray();
            if (gates) needs.Add(new JObject { ["metric"] = "gates_passed", ["min"] = "@course.gates" });
            if (hoops) needs.Add(new JObject { ["metric"] = "hoops_passed", ["min"] = "@course.hoops" });
            var finishRule = new JObject { ["zone"] = "finish", ["needs"] = needs, ["else_reason"] = hoops && !gates ? "missed_hoop" : "missed_gate" };
            if (end.HasValue) finishRule["hold_s"] = hold;
            if (stops) finishRule["max_speed_ms"] = drone ? 0.1 : 0.05;
            double longest = hold != null && hold.Type != JTokenType.String ? (double)hold : hovers ? 120 : 0;
            double timeout = spec.Value<double?>("timeout_s") ?? (hovers ? longest + 60 : 90);
            if (hovers) timeout = Math.Max(timeout, longest + 30);
            var run = new JObject
            {
                ["start"] = new JObject { ["zone"] = "start" },
                ["finish"] = finishRule,
                ["fail"] = new JArray(
                    new JObject { ["probe"] = "upright", ["below"] = 0.3, ["for_s"] = 1, ["reason"] = drone ? "crashed" : "rolled_over" },
                    new JObject { ["timeout_s"] = timeout, ["reason"] = "timeout" }),
            };

            scene = new JObject
            {
                ["schema"] = ArtifactSchema.Scene,
                ["space"] = "room",
                ["generators"] = new JArray(new JObject { ["id"] = "course", ["type"] = RoomCourse.Type, ["params"] = generatorParams }),
                ["nodes"] = nodes,
                ["rules"] = rules,
                ["run"] = run,
            };
            manifest = new JObject
            {
                ["schema"] = ArtifactSchema.Manifest,
                ["id"] = id,
                ["title"] = title,
                ["version"] = 1,
                ["kind"] = "project",   // a standalone artifact can't name its Thing, and a course is for one
                ["category"] = "sim",
                ["template"] = null,
                ["links"] = new JObject { ["project"] = null, ["project_version"] = null, ["repo"] = null, ["thing"] = s.Thing },
                ["runtime"] = new JObject { ["type"] = "scene", ["min_runtime"] = 7 },
                ["scene"] = "scene.json",
                ["script"] = null,
                ["inputs"] = inputs != null ? inputs.DeepClone() : new JArray(),
                ["scenarios"] = new JArray(new JObject { ["id"] = "default", ["seed"] = 42, ["params"] = new JObject() }),
                ["data"] = new JArray(),
                ["assets"] = new JArray(),
                ["permissions"] = new JObject
                {
                    ["network"] = new JArray(), ["connectors"] = new JArray(), ["storage"] = "artifact-local",
                    ["write_project"] = "none", ["room"] = "read", ["things"] = "read",
                },
                ["outputs"] = outputs,
            };
            return true;
        }

        /// <summary>The course in a few words, for the tool result ("truck, on the floor: a straight, 4 cones, a bay").</summary>
        public static string Describe(JObject spec)
        {
            var s = RoomCourse.Read(spec, null);
            var parts = new List<string>();
            foreach (var e in s.Elements)
                switch (e.Type)
                {
                    case "straight": parts.Add("a straight"); break;
                    case "cones": parts.Add(e.Count == 1 ? "1 cone" : e.Count + " cones"); break;
                    case "gates": parts.Add(e.Count == 1 ? "1 gate" : e.Count + " gates"); break;
                    case "hoops": parts.Add(e.Count == 1 ? "1 hoop" : e.Count + " hoops"); break;
                    case "bay": parts.Add("a parking bay"); break;
                    case "pad": parts.Add(s.Drone ? "a landing pad" : "a marked spot"); break;
                    case "hover": parts.Add("a hover box"); break;
                }
            if (!s.End.HasValue) parts.Add("a finish line");
            return s.Thing + ", on the " + (spec.Value<string>("surface") == "desk" ? "table" : "floor") + ": " + string.Join(", ", parts);
        }

        // "1. Put the truck in the green box.\n2. Drive the straight, then stop inside the yellow bay.\n3. ..."
        static string Steps(RoomCourse.Settings s, JToken hold)
        {
            bool drone = s.Drone;
            var steps = new List<string>();
            foreach (var e in s.Elements)
                switch (e.Type)
                {
                    case "straight": steps.Add(drone ? "fly the straight" : "drive the straight"); break;
                    case "cones": steps.Add("weave the cones through each gate"); break;
                    case "gates": steps.Add(drone ? "fly through each box in order" : "go between the posts in order"); break;
                    case "hoops": steps.Add("fly through every hoop"); break;
                    case "bay": steps.Add("stop inside the yellow bay"); break;
                    case "pad": steps.Add(drone ? "land on the yellow pad and stay still" : "stop on the yellow spot"); break;
                    case "hover":
                        steps.Add(hold != null && hold.Type != JTokenType.String
                            ? string.Format(CultureInfo.InvariantCulture, "hold inside the yellow box for {0:0.#} s", (double)hold)
                            : "hold inside the yellow box until the time is up");
                        break;
                }
            if (!s.End.HasValue) steps.Add("cross the finish line");
            string go = string.Join(", then ", steps);
            go = char.ToUpperInvariant(go[0]) + go.Substring(1) + ".";
            return (drone ? "1. Take off from the green box.\n2. " : "1. Start the truck in the green box.\n2. ") + go +
                   "\n3. Tap " + (drone ? "Fly again" : "Reset") + " to go again.";
        }

        static string After(RoomCourse.Settings s)
        {
            var end = s.End;
            if (end.HasValue && end.Value.Type != "hover") return "Three in a row passes the test. Tap " + (s.Drone ? "Fly again" : "Reset") + " for the next one.";
            return "Tap " + (s.Drone ? "Fly again" : "Reset") + " to go again.";
        }

        static JObject Copies(string id, string source, JObject template) =>
            new JObject { ["id"] = id, ["kind"] = "array", ["source"] = source, ["template"] = template };

        static JObject Bar(JArray pos, JArray size) =>
            new JObject { ["kind"] = "primitive", ["shape"] = "box", ["pos"] = pos, ["size"] = size, ["color"] = Cone, ["collider"] = true };

        static JArray V(double x, double y, double z) => new JArray(Math.Round(x, 4), Math.Round(y, 4), Math.Round(z, 4));
    }
}
