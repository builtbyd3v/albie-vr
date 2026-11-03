using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.ML
{
    /// <summary>
    /// Albie's Neural Net Lab tools on the headset (hub off): hub/ml/nn_lab.py ported, same tools and result shapes.
    /// Definitions: <c>ML/Resources/AlbieTools/nn.json</c>. The tools drive <see cref="NeuralLabSim"/> directly (no
    /// round trip). With the hub off this also does the hub's part around them: when Play stops by itself short of 98%
    /// Albie offers his fix (caption + Apply card), and after nn_apply he says the new accuracy once it settles.
    /// Hub live: the hub does both (it gets the sim's state), so this stays quiet.
    /// </summary>
    public sealed class NeuralLabTools : MonoBehaviour
    {
        const string PetId = "albie";
        const float Good = 0.98f, SettleS = 3f, SettleMaxS = 40f;
        static readonly string[] Keep = { "view", "dataset", "hidden", "activation", "learning_rate", "features", "noise", "epoch",
            "playing", "auto_paused_at_epoch", "train_accuracy", "test_accuracy", "train_loss", "test_loss", "panel_open" };

        struct Recipe { public string[] features; public int[] hidden; public string activation; public float rate; public string expected; }
        // hub/ml/nn_lab.py RECIPES (measured in ML/Editor/NeuralLabRecipeCheck.cs).
        static readonly Dictionary<string, Recipe> Recipes = new Dictionary<string, Recipe>
        {
            ["spiral"] = new Recipe { features = new[] { "x", "y", "x2", "y2", "sinx", "siny" }, hidden = new[] { 8, 8 }, activation = "tanh", rate = 0.005f,
                expected = "about 92% test accuracy after 10 to 15 seconds of training" },
            ["circle"] = new Recipe { features = new[] { "x", "y", "x2", "y2" }, hidden = new[] { 4 }, activation = "tanh", rate = 0.0003f,
                expected = "about 92% test accuracy after 5 to 10 seconds of training" },
            ["xor"] = new Recipe { features = new[] { "x", "y", "xy" }, hidden = new[] { 4, 2 }, activation = "tanh", rate = 0.0001f,
                expected = "about 92% test accuracy after 5 to 10 seconds of training" },
            ["gauss"] = new Recipe { features = new[] { "x", "y" }, hidden = new[] { 4, 2 }, activation = "tanh", rate = 0.0001f,
                expected = "about 92% test accuracy after a few seconds of training" },
        };
        static readonly Dictionary<string, string> FeatureNames = new Dictionary<string, string>
            { ["x"] = "x", ["y"] = "y", ["x2"] = "x²", ["y2"] = "y²", ["xy"] = "x·y", ["sinx"] = "sin x", ["siny"] = "sin y" };

        static NeuralLabTools instance;
        static JObject proposal;  // nn_improve's fix, waiting for a yes
        static int offer;         // the live Apply card's number (a tap on an older card does nothing)
        static bool fixing;       // Albie's fix is training: its own stop isn't a new reason to offer
        int offeredStop;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { instance = null; proposal = null; offer = 0; fixing = false; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register()
        {
            AlbieTools.Register("nn", new Dictionary<string, AlbieTools.Handler>
            {
                ["nn_open"] = _ => OpenView("lab", "The Neural Net Lab didn't open: the user has to sit at the desk first."),
                ["nn_close"] = _ => OpenView("project", "The ML desk didn't open: the user has to sit at the desk first."),
                ["nn_status"] = _ => { var s = Summary(Sim.State()); s["seconds_ago"] = 0; return s; },
                ["nn_control"] = Control,
                ["nn_neuron"] = Neuron,
                ["nn_improve"] = _ => Improve(false, false),
                ["nn_apply"] = _ => { offer = 0; return Apply(); },  // the spoken yes: the card's Apply button is spent too
            });
            AlbieCards.Register("nn:", CardAction);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (FindAnyObjectByType<Workspace>() == null) return;  // Main only, like NeuralLabSim
            instance = Sim.gameObject.AddComponent<NeuralLabTools>();
        }

        static NeuralLabSim Sim => NeuralLabSim.Instance;

        void Update()
        {
            // hub/ml/nn_lab.py report(): Play stopping by itself short of Good makes Albie offer his fix, once per stop.
            var sim = Sim;
            if (sim.StopCount == offeredStop) return;
            if (sim.Playing || (sim.StalledAt < 0 && sim.AutoPausedAt < 0)) return;
            offeredStop = sim.StopCount;
            if (ForgeNet.Status == ForgeNetStatus.Live || fixing || sim.TestAccuracy >= Good) return;  // hub live: the hub offers
            Improve(true, sim.StalledAt >= 0);
            if (proposal != null) ForgeNet.Send("albie.note", new JObject { ["text"] = "You just offered to fix the Neural Net Lab's network; a yes means nn_apply." });
        }

        // ---------------------------------------------------------------- tools

        static JObject Command(JObject fields)
        {
            Sim.ApplyCommand(fields);
            return Summary(Sim.State());
        }

        static JObject OpenView(string view, string error)
        {
            var output = Command(new JObject { ["view"] = view });
            if ((string)output["view"] == view) return output;
            output["error"] = error;
            return output;
        }

        static JObject Control(JObject args)
        {
            var fields = new JObject();
            foreach (string k in new[] { "action", "dataset", "activation", "learning_rate", "hidden", "features", "noise" })
                if (args[k] != null && args[k].Type != JTokenType.Null) fields[k] = args[k];
            if (fields.Count == 0) throw new ArgumentException("Say what to change: an action, the dataset, activation, learning rate, layers or features.");
            return Command(fields);
        }

        static JObject Neuron(JObject args)
        {
            string neuron = ((string)args["neuron"] ?? "").Trim().ToUpperInvariant();
            if (!neuron.StartsWith("L") || neuron.IndexOf('N', 1) < 0)
                throw new ArgumentException("Name the neuron as L<layer>N<index>, e.g. L1N3 for layer 1, neuron 3.");
            var output = Command(new JObject { ["neuron"] = neuron, ["enabled"] = args["enabled"] != null && args["enabled"].Type == JTokenType.Boolean && (bool)args["enabled"] });
            foreach (var n in (JArray)output["neurons"]) if ((string)n["id"] == neuron) return output;
            output["error"] = $"There's no neuron {neuron}; the layers are {List(output["hidden"].Values<int>())}.";
            return output;
        }

        /// <summary>Diagnose and, when there's a fix, show the Apply card. spoken: an unprompted offer (the caption says the diagnosis).</summary>
        static JObject Improve(bool spoken, bool stalled)
        {
            var output = Diagnose(Sim.State(), stalled);
            var fix = output["fix"] as JObject;
            output.Remove("fix");
            if (fix == null) { proposal = null; return output; }
            fix["before"] = output["test_accuracy"];
            proposal = fix;
            offer++;
            var bullets = new JArray();
            foreach (var c in (JArray)output["changes"]) if (bullets.Count < 5) bullets.Add(c);
            var card = new JObject
            {
                ["title"] = "Apply the fix and retrain?", ["bullets"] = bullets,
                ["actions"] = new JArray(new JObject { ["label"] = "Apply", ["action_id"] = $"nn:apply:{offer}" },
                                         new JObject { ["label"] = "Not now", ["action_id"] = $"nn:dismiss:{offer}" }),
            };
            // ponytail: the hub also speaks an unprompted offer; on the device it's the caption only (no speak path yet).
            string text = spoken ? $"{output["diagnosis"]} Want me to fix it?" : "Apply the fix and retrain?";
            ForgeNet.Inject("pet.say", new JObject { ["pet_id"] = PetId, ["text"] = text, ["card"] = card });
            return output;
        }

        static JObject Apply()
        {
            if (proposal == null) throw new ArgumentException("There's no fix waiting. Ask me what to improve first.");
            var fix = proposal;
            proposal = null; offer = 0;
            float? before = fix["before"]?.Type == JTokenType.Float || fix["before"]?.Type == JTokenType.Integer ? (float?)fix.Value<float>("before") : null;
            bool to92 = before == null || before < NeuralLabSim.AutoPauseAccuracy;
            var fields = new JObject { ["action"] = "play", ["auto_pause"] = to92 };
            foreach (string k in new[] { "features", "hidden", "activation", "learning_rate" }) fields[k] = fix[k];
            fixing = true;
            Command(fields);
            if (instance != null) instance.StartCoroutine(instance.Announce(before, to92));
            else fixing = false;
            return new JObject
            {
                ["applied"] = true, ["note"] = "Training live now; the new test accuracy shows when it settles.",
                ["expected"] = to92 ? fix["expected"] : "close to 100% test accuracy after 20 to 30 seconds",
            };
        }

        /// <summary>A tap on the Apply / Not now card ("nn:apply:N" / "nn:dismiss:N"). True when it was handled.</summary>
        static bool CardAction(string actionId)
        {
            var parts = actionId.Split(':');
            if (parts.Length < 3 || !int.TryParse(parts[2], out int n) || n != offer) return true;  // a stale card: nothing to do
            if (parts[1] != "apply")
            {
                proposal = null; offer = 0;
                Say("Okay, I'll leave the network as it is.");
                return true;
            }
            try { Apply(); Say("Applied. Watch the boundary: it's training now."); }
            catch (ArgumentException e) { Say(e.Message); }
            return true;
        }

        /// <summary>Wait for the fix to finish training (auto-pause at 92%, or test accuracy no longer climbing), make sure
        /// it's paused and say the result (hub/ml/nn_lab.py _announce).</summary>
        IEnumerator Announce(float? before, bool to92)
        {
            var sim = Sim;
            float start = Time.realtimeSinceStartup, lastGain = start, best = -1f;
            var wait = new WaitForSecondsRealtime(0.5f);
            while (Time.realtimeSinceStartup - start < SettleMaxS)
            {
                yield return wait;
                if (to92 && !sim.Playing) break;  // auto-paused at 92% (or stuck): the sim stopped it
                float acc = sim.TestAccuracy;
                if (acc > best + 0.005f) { best = acc; lastGain = Time.realtimeSinceStartup; }
                else if (!to92 && acc >= Good && Time.realtimeSinceStartup - lastGain >= SettleS) break;
            }
            if (sim.Playing) sim.ApplyCommand(new JObject { ["action"] = "pause" });  // freeze it so people can poke neurons
            fixing = false;
            float after = sim.TestAccuracy;
            string more = to92 ? " Press Play to keep training, or touch a neuron to see what it does." : "";
            Say(before != null ? $"Done! Test accuracy went from {before:0%} to {after:0%}.{more}" : $"Done! Test accuracy is {after:0%}.{more}");
        }

        static void Say(string text) => ForgeNet.Inject("pet.say", new JObject { ["pet_id"] = PetId, ["text"] = text });

        // ---------------------------------------------------------------- hub/ml/nn_lab.py summary / diagnose

        /// <summary>What Albie reads: the headline numbers, every neuron, and the last change.</summary>
        public static JObject Summary(JObject state)
        {
            var output = new JObject();
            foreach (string k in Keep) if (state[k] != null) output[k] = state[k];
            var neurons = new JArray();
            var off = new JArray();
            string key = null; float keyAcc = float.MaxValue;
            foreach (var n in (JArray)state["neurons"])
            {
                neurons.Add(new JObject { ["id"] = n["id"], ["enabled"] = n["enabled"], ["detects"] = n["detects"], ["test_accuracy_if_toggled"] = n["test_accuracy_if_toggled"] });
                if (!(bool)n["enabled"]) { off.Add(n["id"]); continue; }
                float acc = (float)n["test_accuracy_if_toggled"];
                if (acc < keyAcc) { keyAcc = acc; key = (string)n["id"]; }
            }
            output["neurons"] = neurons;
            output["switched_off"] = off;
            if (key != null) output["most_important"] = key;
            var history = (JArray)state["history"];
            if (history.Count > 0)
            {
                output["last_change"] = history[history.Count - 1];
                var recent = new JArray();
                for (int i = Math.Max(0, history.Count - 5); i < history.Count; i++) recent.Add(history[i]);
                output["recent_changes"] = recent;
            }
            return output;
        }

        /// <summary>What's holding the network back, and the measured fix for its dataset (Recipes). "fix" is set only when there is one.</summary>
        public static JObject Diagnose(JObject state, bool stalled)
        {
            string dataset = (string)state["dataset"] ?? "circle";
            if (!Recipes.TryGetValue(dataset, out var recipe)) recipe = Recipes["circle"];
            float acc = (float)state["test_accuracy"];
            var features = new List<string>(); foreach (var f in (JArray)state["features"]) features.Add((string)f);
            var hidden = new List<int>(); foreach (var h in (JArray)state["hidden"]) hidden.Add((int)h);
            var off = new List<string>(); foreach (var n in (JArray)state["neurons"]) if (!(bool)n["enabled"]) off.Add((string)n["id"]);
            string activation = (string)state["activation"];
            float rate = (float)state["learning_rate"];
            var findings = new List<string>();
            if (off.Count > 0) findings.Add($"{off.Count} neuron{(off.Count > 1 ? "s are" : " is")} switched off ({string.Join(", ", off)})");
            if (activation == "linear") findings.Add("a linear activation can only draw straight lines, however many neurons it has");
            else if (activation == "sigmoid") findings.Add("sigmoid neurons only output 0 to 1 and pass back tiny gradients, so they learn very slowly");
            else if (activation == "relu" && recipe.activation != "relu") findings.Add($"ReLU neurons are sharp and can go dead; tanh fits the {dataset} more smoothly");
            if (rate >= 0.3f) findings.Add($"the learning rate ({Py(rate)}) is so high the weights jump around");
            var missing = new List<string>(); foreach (var f in recipe.features) if (!features.Contains(f)) missing.Add(FeatureNames[f]);
            if (missing.Count > 0)
            {
                var have = features.ConvertAll(f => FeatureNames.TryGetValue(f, out var name) ? name : f);
                findings.Add($"it only sees {string.Join(", ", have)}; the {dataset} needs {string.Join(", ", missing)} as inputs to bend around");
            }
            if (Sum(hidden) < Sum(recipe.hidden)) findings.Add($"layers {List(hidden)} are too small for the {dataset} pattern");

            bool sameFeatures = SameSet(features, recipe.features), sameHidden = List(hidden) == List(recipe.hidden);
            bool sameRate = Mathf.Abs(rate - recipe.rate) <= 1e-6f, sameActivation = activation == recipe.activation;
            var result = new JObject { ["test_accuracy"] = state["test_accuracy"] };
            if (acc >= Good && off.Count == 0)
            {
                result["diagnosis"] = $"It's already at {acc:0%} on the {dataset}; nothing to fix.";
                result["changes"] = new JArray();
                return result;
            }
            if (findings.Count == 0)
            {
                if (sameFeatures && sameHidden && sameActivation && sameRate && off.Count == 0)
                {
                    result["changes"] = new JArray();
                    result["diagnosis"] = $"It's at {acc:0%} on the {dataset} with the best setup I know for it. Press Play to keep training: it creeps up from here.";
                    return result;
                }
                findings.Add($"it's stuck at {acc:0%}: this setup can't fit the {dataset} pattern");
            }
            var changes = new JArray();
            if (!sameFeatures) changes.Add("inputs " + string.Join(", ", Array.ConvertAll(recipe.features, f => FeatureNames[f])));
            if (!sameHidden) changes.Add($"hidden layers {List(recipe.hidden)}");
            if (!sameActivation) changes.Add($"{recipe.activation} activation");
            if (!sameRate) changes.Add($"learning rate {Py(recipe.rate)}");
            if (off.Count > 0) changes.Add("every neuron back on");
            result["diagnosis"] = $"It's {(stalled ? "stuck " : "")}at {acc:0%} because " + string.Join("; ", findings) + ".";
            result["changes"] = changes;
            result["expected"] = recipe.expected;
            result["fix"] = new JObject
            {
                ["features"] = new JArray(recipe.features), ["hidden"] = new JArray(recipe.hidden), ["activation"] = recipe.activation,
                ["learning_rate"] = recipe.rate, ["expected"] = recipe.expected,
            };
            return result;
        }

        static int Sum(IEnumerable<int> xs) { int s = 0; foreach (int x in xs) s += x; return s; }
        static string List(IEnumerable<int> xs) => "[" + string.Join(", ", xs) + "]";  // Python's list repr
        static string Py(float v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        static bool SameSet(List<string> a, string[] b)
        {
            if (a.Count != b.Length) return false;
            foreach (var x in b) if (!a.Contains(x)) return false;
            return true;
        }
    }
}
