using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// Albie's voice controls for the open artifact (<c>Resources/AlbieTools/artifact_voice.json</c>):
    /// - <c>artifact_explore</c>: "expand the heart", "cut the eye open", "X-ray it", "show me the mitral valve", "turn it",
    ///   "put it back together", "reset it". "Show me this lobe", "just the valves", "the frontal and temporal lobes" is
    ///   isolate: those parts on their own and brought forward, the rest a faint outline; "this" is the part pointed or
    ///   looked at (<see cref="ArtifactPartPointer"/>). Names match through the template's parts.json
    ///   (<see cref="PartMatch"/>). With <c>template</c>, a model that isn't open is opened first and waited for.
    /// - <c>artifact_press</c>: the artifact's own buttons by what the user said ("show the blood flow", "follow the light").
    ///   It presses the real panel button, so a toggle's label flips just as if it were tapped, and returns the walkthrough's
    ///   captions with their times, so Albie explains each step as it plays.
    /// - <c>artifact_describe</c>: what's open, its parts, buttons, captions, notes and state, so Albie talks about what's there.
    /// </summary>
    public static class ArtifactVoiceTools
    {
        const float LoadWaitSeconds = 20f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register() => AlbieTools.Register("artifact_voice", new Dictionary<string, AlbieTools.AsyncHandler>
        {
            ["artifact_explore"] = Explore,
            ["artifact_press"] = Press,
            ["artifact_describe"] = (args, done) => done(Describe()),
        });

        static JObject Error(string message) => new JObject { ["error"] = message };

        // ---------------------------------------------------------------- opening and waiting
        // Opens the named template if something else (or nothing) is open, then calls back once its model can be explored
        // (or right away for a scene with no model). done(null) means it never came.
        static void WithOpen(string template, bool needModel, Action<ArtifactHost, ArtifactExplorer> then, Action<JObject> fail)
        {
            var host = ArtifactHost.Current;
            if (host == null) { fail(Error("no_host: artifacts can't show here")); return; }
            string open = host.OpenManifest?.Value<string>("template");
            if (!string.IsNullOrWhiteSpace(template) && !string.Equals(open, template.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var made = ArtifactTools.Create(new JObject { ["template"] = template.Trim().ToLowerInvariant() });
                if (made["error"] != null) { fail(made); return; }
            }
            else if (host.OpenId == null)
            {
                fail(Error("nothing_open: nothing is open; pass template (heart, eye, brain, lungs, kidneys, stomach, cell, plant_cell or dna)"));
                return;
            }
            host.StartCoroutine(Wait(host, needModel, then, fail));
        }

        static IEnumerator Wait(ArtifactHost host, bool needModel, Action<ArtifactHost, ArtifactExplorer> then, Action<JObject> fail)
        {
            float until = Time.realtimeSinceStartup + LoadWaitSeconds;
            while (Time.realtimeSinceStartup < until)
            {
                if (host == null) { fail(Error("closed: the artifact closed")); yield break; }
                if (host.OpenId != null)
                {
                    var explorer = FindExplorer(host);
                    if (explorer != null || !needModel || !HasModel(host)) { then(host, explorer); yield break; }
                }
                yield return null;
            }
            fail(Error("not_ready: the model is still loading; try again in a moment"));
        }

        static ArtifactExplorer FindExplorer(ArtifactHost host)
        {
            foreach (var model in host.GetComponentsInChildren<ArtifactGltf>(true))
                if (model.Loaded && model.Explorer != null) return model.Explorer;
            return null;
        }

        static bool HasModel(ArtifactHost host) => host.OpenManifest?["assets"] is JArray assets && assets.Count > 0;

        // ---------------------------------------------------------------- artifact_explore
        static void Explore(JObject args, Action<JObject> done)
        {
            string action = (args.Value<string>("action") ?? "").Trim().ToLowerInvariant();
            WithOpen(args.Value<string>("template"), true, (host, explorer) =>
            {
                if (explorer == null) { done(Error("no_model: this artifact has no 3D model to explore")); return; }
                if (explorer.Avoid == null)
                {
                    var panel = host.GetComponentInChildren<ForgePanel>(true);
                    if (panel != null) { explorer.Avoid = panel.transform; explorer.AvoidHalfWidth = panel.SizeMeters.x * 0.5f; }
                }
                var result = new JObject();
                string did = Act(host, explorer, action, args, result, out string error);
                if (error != null) { result["error"] = error; done(result); return; }
                foreach (var pair in State(host, explorer)) if (result[pair.Key] == null) result[pair.Key] = pair.Value;
                result["did"] = did;
                done(result);
            }, done);
        }

        /// <summary>Does one explore action; returns what happened in a few words, or sets <paramref name="error"/>.</summary>
        public static string Act(ArtifactHost host, ArtifactExplorer e, string action, JObject args, out string error) =>
            Act(host, e, action, args, new JObject(), out error);

        /// <summary>
        /// Does one explore action; returns what happened in a few words, or sets <paramref name="error"/>. Anything
        /// else Albie should know (what a part does, which parts to ask between) goes in <paramref name="into"/>.
        /// </summary>
        public static string Act(ArtifactHost host, ArtifactExplorer e, string action, JObject args, JObject into, out string error)
        {
            error = null;
            switch (action)
            {
                case "separate": case "expand": case "explode":
                    if (e.PartCount < 2) { error = "one_piece: this model is one solid piece, so it can't come apart; it can still be cut open or turned"; return null; }
                    if (e.Isolating) e.Unfocus();
                    if (!e.Separated) e.Separate();
                    into["say"] = "Name three or four of its parts in one sentence, then offer to show one on its own.";
                    return "separated into its " + e.PartCount + " parts";
                case "assemble":
                    if (e.Separated) e.Separate();
                    return "put back together";
                case "cut_open":
                    if (!e.Sectioned) e.Section();
                    return "cut open";
                case "whole":
                    if (e.Sectioned) e.Section();
                    return "whole again";
                case "xray":
                    if (!e.XRay) e.ToggleXRay();
                    return "outer layers see-through";
                case "xray_off":
                    if (e.XRay) e.ToggleXRay();
                    return "solid again";
                case "isolate": case "show_part": case "focus": case "highlight":
                    return ShowParts(e, action, args, into, out error);
                case "unfocus": case "show_all":
                    e.Unfocus();
                    return "every part shown again";
                case "turn":
                {
                    float degrees = args.Value<float?>("degrees") ?? 30f;
                    e.Turn(degrees);
                    return $"turned {degrees:0} degrees";
                }
                case "reset":
                    e.ResetView();
                    var hands = e.GetComponentInParent<ArtifactHandGrab>();
                    if (hands != null) hands.ResetPose();
                    return "back as it was";
            }
            error = "bad_action: use separate, assemble, cut_open, whole, xray, xray_off, isolate, focus, unfocus, turn or reset";
            return null;
        }

        // "Show me the frontal lobe", "just the valves", "show me this lobe": the parts asked for, on their own.
        // isolate: the rest fade to a faint outline and the parts come forward; focus (and mode highlight): the rest go
        // see-through and nothing moves, unless expand says so.
        static string ShowParts(ArtifactExplorer e, string action, JObject args, JObject into, out string error)
        {
            error = null;
            string asked = args.Value<string>("part");
            if (args["parts"] is JArray list && list.Count > 0)
            {
                var all = new List<string>();
                foreach (var t in list) if (t.Type == JTokenType.String) all.Add((string)t);
                if (asked != null) all.Insert(0, asked);
                asked = string.Join(" and ", all);
            }
            var names = e.PartNames();
            if (e.PartCount < 2)
            {
                error = "one_piece: this model is one solid piece, so there are no parts to show on their own";
                return null;
            }
            if (string.IsNullOrWhiteSpace(asked))
            {
                error = "no_part: say which part; the parts are " + string.Join(", ", names);
                return null;
            }
            bool highlight = action == "focus" || action == "highlight" || string.Equals(args.Value<string>("mode"), "highlight", StringComparison.OrdinalIgnoreCase);
            bool expand = args.Value<bool?>("expand") ?? !highlight;
            var m = e.Match(asked);
            var show = new List<Transform>();
            foreach (int i in m.Parts) show.Add(e.PartAtIndex(i));
            if (m.Deictic)
            {
                var how = ArtifactPartPointer.How.None;
                var pointed = e.Pointer != null ? e.Pointer.This(out how) : null;
                if (pointed == null && show.Count == 0)
                {
                    error = "nothing_pointed: nothing is pointed at or looked at on the model right now; ask them to point at the part (or look at it) and say it again, or name it. The parts are " + string.Join(", ", names);
                    return null;
                }
                if (pointed != null)
                {
                    if (!show.Contains(pointed)) show.Insert(0, pointed);
                    into["this"] = ArtifactExplorer.NameOf(pointed);
                    into["this_was"] = "the part you're " + ArtifactPartPointer.Say(how);
                }
            }
            if (m.Ambiguous)
            {
                var options = PartMatch.OptionNames(m, names);
                into["options"] = new JArray(options);
                into["say"] = "Ask which one: " + Or(options) + (m.Group != null ? ", or all the " + m.Group : "") + ".";
                return "asked which part";
            }
            if (show.Count == 0)
            {
                var words = e.Words;
                error = $"no_part: no part like '{asked}' on this model; never pretend. The parts are " + string.Join(", ", names)
                        + (words != null && words.GroupNames().Count > 0 ? "; groups: " + string.Join(", ", words.GroupNames()) : "");
                return null;
            }
            e.Isolate(show, highlight ? ArtifactExplorer.Look.Highlight : ArtifactExplorer.Look.Isolate, expand);
            var shown = new List<string>();
            var about = new JObject();
            foreach (var t in e.Chosen)
            {
                string name = ArtifactExplorer.NameOf(t);
                shown.Add(name);
                string a = e.About(t);
                if (a != null) about[name] = a;
            }
            into["shown"] = new JArray(shown);
            if (about.Count > 0) into["about"] = about;
            if (m.BothSides) into["note"] = "The model has one on each side; both are shown.";
            if (m.Unmatched.Count > 0) into["not_found"] = new JArray(m.Unmatched);
            if (highlight && !expand) return "showing the " + And(shown);
            return (highlight ? "highlighted the " : "showing just the ") + And(shown) + (expand ? ", brought forward" : "")
                   + (highlight ? "" : "; the rest is a faint outline. Say 'show all' to put it back");
        }

        static string And(List<string> names)
        {
            if (names.Count == 0) return "";
            if (names.Count == 1) return names[0];
            if (names.Count > 4) return names.Count + " parts: " + string.Join(", ", names.GetRange(0, 3)) + " and more";
            return string.Join(", ", names.GetRange(0, names.Count - 1)) + " and " + names[names.Count - 1];
        }

        static string Or(List<string> names)
        {
            if (names.Count <= 1) return names.Count == 1 ? names[0] : "";
            return string.Join(", ", names.GetRange(0, names.Count - 1)) + " or " + names[names.Count - 1];
        }

        static JObject State(ArtifactHost host, ArtifactExplorer e)
        {
            var state = new JObject
            {
                ["open"] = host.OpenTitle,
                ["apart"] = e != null && e.Separated,
                ["cut_open"] = e != null && e.Sectioned,
                ["xray"] = e != null && e.XRay,
                ["focus"] = e != null && e.Focused != null ? ArtifactExplorer.NameOf(e.Focused) : null,
            };
            if (e == null) return state;
            if (e.Chosen.Count > 1)
            {
                var shown = new JArray();
                foreach (var t in e.Chosen) shown.Add(ArtifactExplorer.NameOf(t));
                state["isolated"] = shown;
            }
            state["parts"] = new JArray(e.PartNames());
            if (e.Words != null && e.Words.GroupNames().Count > 0) state["groups"] = new JArray(e.Words.GroupNames());
            if (e.PartCount < 2) state["one_piece"] = true;
            return state;
        }

        // ---------------------------------------------------------------- artifact_press
        sealed class Button
        {
            public string id, label, altId, altLabel;
            public bool primary;
        }

        static List<Button> Buttons(JObject scene)
        {
            var list = new List<Button>();
            if (scene?["nodes"] is JArray nodes)
                foreach (var n in nodes)
                    if ((string)n["kind"] == "panel" && n["widgets"] is JArray widgets)
                        foreach (var w in widgets)
                            if ((string)w["type"] == "button")
                                list.Add(new Button { id = (string)w["id"], label = (string)w["label"], altId = (string)w["alt_id"], altLabel = (string)w["alt_label"],
                                    primary = w.Value<bool?>("primary") == true });
            return list;
        }

        static void Press(JObject args, Action<JObject> done)
        {
            string asked = args.Value<string>("button") ?? "";
            WithOpen(args.Value<string>("template"), false, (host, _) => done(PressNow(host, asked)), done);
        }

        /// <summary>Presses the button <paramref name="asked"/> means on the open artifact. Checks call it directly.</summary>
        public static JObject PressNow(ArtifactHost host, string asked)
        {
            var scene = host.OpenScene;
            var buttons = Buttons(scene);
            Button best = Match(buttons, asked, out bool alt);
            if (best == null)
            {
                var names = new JArray();
                foreach (var b in buttons) { names.Add(b.label); if (b.altLabel != null) names.Add(b.altLabel); }
                return new JObject { ["error"] = $"no_button: no button like '{asked}'", ["buttons"] = names };
            }
            string input = alt ? best.altId : best.id;
            string label = alt ? best.altLabel : best.label;
            // Press the real pill so a toggle flips its label as if tapped. A toggle already showing its other label means
            // what was asked is already on.
            var result = new JObject { ["pressed"] = label };
            ForgePill pill = FindPill(host, best);
            if (pill != null)
            {
                string showing = pill.GetComponentInChildren<TMP_Text>()?.text;
                if (best.altLabel != null && !string.Equals(showing, label, StringComparison.OrdinalIgnoreCase))
                {
                    result["pressed"] = null;
                    result["already"] = label + " is already done";
                    return result;
                }
                pill.Button.onClick.Invoke();
            }
            else host.Fire(input);
            AddCaptions(scene, input, result);
            return result;
        }

        // The artifact's own pill for this button (showing either of its labels); else any panel's pill with that label,
        // since the OS may have moved the artifact's panel into a window of its own.
        static readonly string[] ExplainWords = { "explain", "how", "why", "works", "work", "walk", "tour", "tell", "teach", "show me" };

        static Button Match(List<Button> buttons, string asked, out bool alt)
        {
            alt = false;
            Button best = null;
            int bestScore = 0;
            // Asked to explain: the walkthrough (the primary button) beats an animation that shares more words with it.
            string a = " " + Clean(asked ?? "") + " ";
            bool explain = false;
            foreach (var w in ExplainWords) explain |= a.Contains(" " + w + " ");
            foreach (var b in buttons)
            {
                int first = Math.Max(Score(asked, b.label), Score(asked, b.id));
                if (explain && b.primary && first > 0) first += 15;
                int second = b.altId != null ? Math.Max(Score(asked, b.altLabel), Score(asked, b.altId)) : 0;
                if (first > bestScore) { bestScore = first; best = b; alt = false; }
                if (second > bestScore) { bestScore = second; best = b; alt = true; }
            }
            if (best != null) return best;
            // "How does the eye see?" names no button: a request to explain plays the model's main walkthrough.
            if (explain) foreach (var b in buttons) if (b.primary) return b;
            return null;
        }

        /// <summary>
        /// Which input a spoken request presses on a scene (its panel buttons), and that button's label. Null when none
        /// fits. Checks call it with the templates' scenes.
        /// </summary>
        public static string InputFor(JObject scene, string asked, out string label)
        {
            var b = Match(Buttons(scene), asked, out bool alt);
            label = b == null ? null : alt ? b.altLabel : b.label;
            return b == null ? null : alt ? b.altId : b.id;
        }

        /// <summary>The walkthrough <paramref name="input"/> starts on <paramref name="scene"/>: its captions in order, or empty.</summary>
        public static JArray WalkthroughFor(JObject scene, string input)
        {
            var result = new JObject();
            AddCaptions(scene, input, result);
            return result["walkthrough"] as JArray ?? new JArray();
        }

        static ForgePill FindPill(ArtifactHost host, Button b)
        {
            ForgePill elsewhere = null;
            foreach (var pill in UnityEngine.Object.FindObjectsByType<ForgePill>(FindObjectsInactive.Exclude))
            {
                string text = pill.GetComponentInChildren<TMP_Text>()?.text;
                bool match = text != null && (string.Equals(text, b.label, StringComparison.OrdinalIgnoreCase)
                    || (b.altLabel != null && string.Equals(text, b.altLabel, StringComparison.OrdinalIgnoreCase)));
                if (!match) continue;
                if (pill.transform.IsChildOf(host.transform)) return pill;
                if (elsewhere == null && pill.GetComponentInParent<ForgePanel>() != null) elsewhere = pill;
            }
            return elsewhere;
        }

        // How well what was said matches a label: words in common (3+ letters), with "the blood flow" ~ "Show blood flow".
        static int Score(string asked, string candidate)
        {
            if (string.IsNullOrEmpty(asked) || string.IsNullOrEmpty(candidate)) return 0;
            string a = Clean(asked), c = Clean(candidate);
            if (a == c) return 100;
            int hits = 0;
            foreach (var w in c.Split(' ')) if (w.Length > 2 && w != "the" && w != "and" && (" " + a + " ").Contains(" " + w)) hits++;
            // "show"/"hide"/"start"/"stop" decide which side of a toggle: a missing one costs a little.
            return hits * 10 - (Opposite(a, c) ? 15 : 0);
        }

        static bool Opposite(string asked, string label)
        {
            return (asked.Contains("hide") && label.Contains("show")) || (asked.Contains("show") && label.Contains("hide"))
                || (asked.Contains("stop") && (label.Contains("start") || label.Contains("spin it")))
                || ((asked.Contains("start") || asked.Contains("play")) && (label.Contains("stop") || label.Contains("pause")))
                || (asked.Contains("pause") && label.Contains("start"));
        }

        static string Clean(string text)
        {
            var chars = text.ToLowerInvariant().ToCharArray();
            for (int i = 0; i < chars.Length; i++) if (!char.IsLetterOrDigit(chars[i])) chars[i] = ' ';
            return string.Join(" ", new string(chars).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        // The walkthrough an input starts: its timeline, and the captions that timeline shows, in order with their times.
        static void AddCaptions(JObject scene, string input, JObject into)
        {
            if (scene?["rules"] is not JArray rules) return;
            // A button can start several timelines (Trace the blood starts the captioned walkthrough and the circulation):
            // the walkthrough is the one with the most captions.
            JArray captions = new JArray();
            foreach (var r in rules)
                if ((string)r["type"] == "on_input" && (string)r["input"] == input && r["do"] is JArray actions)
                    foreach (var a in actions)
                        if ((string)a["timeline"] != null && ((string)a["action"] == "restart" || (string)a["action"] == "play"))
                        {
                            var found = Captions(scene, (string)a["timeline"]);
                            if (found.Count > captions.Count) captions = found;
                        }
            if (captions.Count == 0) return;
            into["walkthrough"] = captions;
            into["say"] = "Explain these steps in this order, one short sentence each, as they come up on screen.";
        }

        static JArray Captions(JObject scene, string timeline)
        {
            var texts = new Dictionary<string, string>();
            if (scene["nodes"] is JArray nodes)
                foreach (var n in nodes)
                    if ((string)n["kind"] == "text" && ((string)n["id"] ?? "").StartsWith("step_")) texts[(string)n["id"]] = (string)n["text"];
            var steps = new List<(float at, float until, string text)>();
            if (scene["rules"] is JArray rules)
                foreach (var r in rules)
                {
                    if ((string)r["type"] != "animate" || (string)r["property"] != "visible" || (string)r["timeline"] != timeline) continue;
                    if (!texts.TryGetValue((string)r["target"] ?? "", out string text) || r["keys"] is not JArray keys) continue;
                    float at = -1f, until = -1f;
                    foreach (var k in keys)
                    {
                        bool on = k[1].Type == JTokenType.Boolean && (bool)k[1];
                        if (on && at < 0f) at = (float)k[0];
                        else if (!on && at >= 0f && until < 0f) until = (float)k[0];
                    }
                    if (at >= 0f) steps.Add((at, until, StripNumber(text)));
                }
            steps.Sort((a, b) => a.at.CompareTo(b.at));
            var list = new JArray();
            foreach (var s in steps) list.Add(new JObject { ["at_s"] = Math.Round(s.at, 1), ["for_s"] = s.until > s.at ? Math.Round(s.until - s.at, 1) : (double?)null, ["text"] = s.text });
            return list;
        }

        // "1  Oxygen-poor blood..." -> "Oxygen-poor blood..."
        static string StripNumber(string text)
        {
            text = (text ?? "").TrimStart();
            int i = 0;
            while (i < text.Length && char.IsDigit(text[i])) i++;
            return text.Substring(i).TrimStart();
        }

        // ---------------------------------------------------------------- artifact_describe
        /// <summary>What's open and what it can do. Checks call it directly.</summary>
        public static JObject Describe()
        {
            var host = ArtifactHost.Current;
            if (host == null || host.OpenId == null) return new JObject { ["open"] = null, ["note"] = "Nothing is open. artifact_create opens a model (heart, eye, brain, cell, plant_cell, dna)." };
            var scene = host.OpenScene;
            var explorer = FindExplorer(host);
            var result = State(host, explorer);
            result["template"] = host.OpenManifest?.Value<string>("template");
            var buttons = new JArray();
            foreach (var b in Buttons(scene)) { buttons.Add(b.label); if (b.altLabel != null) buttons.Add(b.altLabel); }
            result["buttons"] = buttons;
            var notes = new JArray();
            if (scene?["nodes"] is JArray nodes)
                foreach (var n in nodes)
                    if ((string)n["kind"] == "panel" && n["widgets"] is JArray widgets)
                        foreach (var w in widgets)
                            if ((string)w["type"] == "note") notes.Add((string)w["text"]);
            result["notes"] = notes;
            var walkthroughs = new JObject();
            if (scene?["nodes"] is JArray all)
                foreach (var n in all)
                    if ((string)n["kind"] == "timeline" && n.Value<bool?>("autoplay") != true)
                    {
                        var caps = Captions(scene, (string)n["id"]);
                        if (caps.Count > 0) walkthroughs[(string)n["id"]] = caps;
                    }
            result["walkthroughs"] = walkthroughs;
            if (explorer == null && HasModel(host)) result["note"] = "The model is still loading.";
            // "What's this?": the part under the user's hand or ray, and the one in the middle of their view.
            if (explorer != null && explorer.Pointer != null)
            {
                float now = Time.unscaledTime;
                var pointed = explorer.Pointer.PointedAt(now, out var how);
                if (pointed != null) result["pointed_at"] = Part(explorer, pointed, "you're " + ArtifactPartPointer.Say(how));
                var looked = explorer.Pointer.LookedAt(now);
                if (looked != null) result["looked_at"] = Part(explorer, looked, null);
            }
            return result;
        }

        static JObject Part(ArtifactExplorer e, Transform part, string how)
        {
            var o = new JObject { ["name"] = ArtifactExplorer.NameOf(part) };
            string about = e.About(part);
            if (about != null) o["about"] = about;
            if (how != null) o["how"] = how;
            return o;
        }
    }
}
