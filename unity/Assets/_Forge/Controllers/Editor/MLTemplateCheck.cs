using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Forge.Controllers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Headless checks for the bundled on-device ML template (PROJECT_CONTEXT 15.5, 16.8 ML A Nov 14): the "obstacle ahead"
/// dataset, frame-budgeted training, the albie.model/1 file, the graph <c>model</c> node (contract section 3.5,
/// proposed), and the learned brake in closed loop on the mock truck. Forge > Controllers > Check ML Template logs the
/// report; <see cref="Run"/> returns it (for `unity command eval`). Timings are Editor timings.
/// Allocations are counted by <see cref="AllocationCounter"/> (GC.GetAllocatedBytesForCurrentThread reads 0 under Unity's
/// garbage collector, so a check on it can't fail).
/// </summary>
public static class MLTemplateCheck
{
    const double FrameBudgetMs = 4.0;
    static ObstacleTemplate template;

    [MenuItem("Forge/Controllers/Check ML Template")]
    static void Menu()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
    }

    public static void RunBatch()
    {
        string report = Run();
        if (report.StartsWith("PASS")) Debug.Log(report); else Debug.LogError(report);
        EditorApplication.Exit(report.StartsWith("PASS") ? 0 : 1);
    }

    public static string Run()
    {
        template = null;
        var sb = new StringBuilder();
        bool ok = true;
        ok &= Check(sb, "dataset: balanced, deterministic, labelled by physics", Dataset);
        ok &= Check(sb, "the label sees speed: faster needs more room", LabelSeesSpeed);
        ok &= Check(sb, "frame-budgeted training, no allocations per step", Training);
        ok &= Check(sb, "dataset made a slice a frame: same samples, within budget, no allocations", MadeOverFrames);
        ok &= Check(sb, "beats the best front-ray rule on held-out samples", BeatsRule);
        ok &= Check(sb, "albie.model/1 file: same predictions after a round trip", ModelFile);
        ok &= Check(sb, "graph model node: bad files refused with the contract's codes", BadNodes);
        ok &= Check(sb, "graph model node: ticking allocates nothing", TickAllocatesNothing);
        ok &= Check(sb, "learned brake in closed loop: as safe as the best rule, drives further", ClosedLoop);
        ok &= Check(sb, "plugs into truck_cruise in place of its hand-written brake", PlugsIn);
        ok &= Check(sb, "learned controllers still load (shared net parser)", LearnedStillLoads);
        ok &= Check(sb, "voice tools: definitions match the handlers", ToolDefinitions);
        ok &= Check(sb, "template_train answers when done, saves the files, reports one training run", TrainTool);
        ok &= Check(sb, "learned_brake on/off swaps the truck's controller and gives it back", BrakeTool);
        TemplateRun.Reset();
        return (ok ? "PASS" : "FAIL") + " ML template check\n" + sb;
    }

    delegate string CheckFn(out string note);

    static bool Check(StringBuilder sb, string name, CheckFn fn)
    {
        string failure, note;
        try { failure = fn(out note); }
        catch (Exception e) { failure = e.GetType().Name + ": " + e.Message; note = null; }
        sb.Append(failure == null ? "  ok   " : "  FAIL ").Append(name);
        if (failure != null) sb.Append(": ").Append(failure);
        else if (!string.IsNullOrEmpty(note)) sb.Append(": ").Append(note);
        sb.Append('\n');
        return failure == null;
    }

    static ObstacleTemplate Trained()
    {
        if (template != null && template.Done) return template;
        template = new ObstacleTemplate();
        while (!template.Step(FrameBudgetMs)) { }
        return template;
    }

    static string Dataset(out string note)
    {
        note = null;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var a = ObstacleData.Generate(3000, 7);
        long ms = started.ElapsedMilliseconds;
        var b = ObstacleData.Generate(3000, 7);
        if (a.Count != 3000 || a.Positives != 1500) return $"{a.Positives} of {a.Count} are obstacles, expected 1500";
        for (int i = 0; i < a.Count; i++)
        {
            if (a.Y[i] != b.Y[i]) return "same seed, different labels at " + i;
            for (int f = 0; f < a.X[i].Length; f++) if (a.X[i][f] != b.X[i][f]) return "same seed, different features at " + i;
        }
        if (a.X[0].Length != MockTruckSim.SensorNames.Length) return "features aren't the truck's sensors";
        var c = ObstacleData.Generate(3000, 8);
        bool differs = false;
        for (int i = 0; i < 20 && !differs; i++) differs |= c.X[i][0] != a.X[i][0];
        if (!differs) return "another seed gave the same data";
        note = $"3000 samples (1500 obstacle) in {ms} ms, from {a.Tried} random poses";
        return null;
    }

    // A truck 1 m short of cone 1, pointing at it: an obstacle when fast, clear when slow (with the 1 m/s floor, 0.6 m).
    static string LabelSeesSpeed(out string note)
    {
        note = null;
        var probe = new MockTruckSim();
        var sim = new MockTruckSim { StopAtFinish = false };
        float coneX = MockTruckSim.ConeX[1];
        sim.Reset(); sim.X = coneX - 1.0f; sim.Y = 0f; sim.Heading = 0f;
        sim.Speed = 2.5f; bool fast = ObstacleData.Ahead(sim, probe);
        sim.Speed = 0.5f; bool slow = ObstacleData.Ahead(sim, probe);
        sim.X = coneX - 0.45f; bool closeSlow = ObstacleData.Ahead(sim, probe);
        sim.Reset(); sim.X = 2.3f; sim.Y = 1.0f; sim.Heading = 0f; sim.Speed = 2.5f;  // open lane beside the cones
        bool open = ObstacleData.Ahead(sim, probe);
        if (!fast) return "2.5 m/s, 1 m from a cone isn't an obstacle";
        if (slow) return "0.5 m/s, 1 m from a cone is an obstacle";
        if (!closeSlow) return "0.5 m/s, 0.45 m from a cone isn't an obstacle (the 1 m/s floor should catch it)";
        if (open) return "an open lane is an obstacle";
        note = "1 m from a cone: obstacle at 2.5 m/s, clear at 0.5 m/s; 0.45 m away it's an obstacle even crawling";
        return null;
    }

    static string Training(out string note)
    {
        note = null;
        var t = new ObstacleTemplate();
        t.Step(FrameBudgetMs);  // warm up (JIT)
        int frames = 1;
        double worst = 0;
        var frame = new System.Diagnostics.Stopwatch();
        long bytes = BytesAllocated(() =>
        {
            while (true)
            {
                frame.Restart();
                bool done = t.Step(FrameBudgetMs);
                worst = Math.Max(worst, frame.Elapsed.TotalMilliseconds);
                frames++;
                if (done) break;
            }
        });
        if (bytes > 0) return $"training allocated {bytes} bytes";
        if (worst > FrameBudgetMs + 2.5) return $"a frame took {worst:0.0} ms (budget {FrameBudgetMs} ms)";
        if (t.Classifier.Epoch != t.Classifier.Settings.Epochs) return "stopped at epoch " + t.Classifier.Epoch;
        template = t;
        note = $"{t.Classifier.Settings.Epochs} epochs in {frames} frames, {t.Classifier.ComputeSeconds * 1000:0} ms compute, worst frame {worst:0.0} ms, 0 bytes";
        return null;
    }

    // What the headset runs (TemplateRun): the dataset a slice a frame, then the classifier's setup, then training.
    static string MadeOverFrames(out string note)
    {
        note = null;
        var whole = ObstacleData.Generate(3000, 7);
        var t = ObstacleTemplate.Begin();
        if (!t.MakingData || t.Data != null || t.Classifier != null) return "Begin made the data at once";
        var frame = new System.Diagnostics.Stopwatch();
        double worst = 0, setupFrame = 0;
        int dataFrames = 0, trainFrames = 0;
        // Only the last slice allocates: it wraps the rows it filled.
        long bytes = BytesAllocated(() =>
        {
            while (!t.Maker.Done)
            {
                frame.Restart();
                t.Step(FrameBudgetMs);
                worst = Math.Max(worst, frame.Elapsed.TotalMilliseconds);
                dataFrames++;
            }
        });
        if (bytes > 256) return $"making the data allocated {bytes} bytes (expected none but the result)";
        if (dataFrames < 2) return "the dataset was made in one frame";
        if (worst > FrameBudgetMs + 2.5) return $"a data frame took {worst:0.0} ms (budget {FrameBudgetMs} ms)";
        bool done = false;
        while (!done)
        {
            frame.Restart();
            done = t.Step(FrameBudgetMs);
            double ms = frame.Elapsed.TotalMilliseconds;
            if (trainFrames++ == 0) setupFrame = ms;
            worst = Math.Max(worst, ms);
        }
        if (worst > FrameBudgetMs + 2.5) return $"a frame took {worst:0.0} ms (budget {FrameBudgetMs} ms)";
        var a = t.Data;
        if (a.Count != whole.Count || a.Tried != whole.Tried) return $"{a.Count} samples from {a.Tried} poses, at once {whole.Count} from {whole.Tried}";
        for (int i = 0; i < a.Count; i++)
        {
            if (a.Y[i] != whole.Y[i]) return "a different label at " + i;
            for (int f = 0; f < a.X[i].Length; f++) if (a.X[i][f] != whole.X[i][f]) return "a different feature at " + i;
        }
        var trained = Trained().Classifier.TrainingBlock();
        var spread = t.Classifier.TrainingBlock();
        if ((double)spread["val_accuracy"] != (double)trained["val_accuracy"] || (double)spread["val_loss"] != (double)trained["val_loss"])
            return $"trained to {spread["val_accuracy"]} (loss {spread["val_loss"]}), at once {trained["val_accuracy"]} ({trained["val_loss"]})";
        note = $"3000 samples in {dataFrames} frames ({t.Maker.ComputeSeconds * 1000:0.0} ms, {bytes} bytes), the same samples and net as at once; " +
               $"setup + first training frame {setupFrame:0.0} ms, worst frame {worst:0.0} ms";
        return null;
    }

    static string BeatsRule(out string note)
    {
        note = null;
        var c = Trained().Classifier;
        c.Evaluate(out var model, out var rule, out float threshold);
        if (model.Accuracy < 0.93f) return $"accuracy {model.Accuracy:P1}";
        if (model.Accuracy < rule.Accuracy + 0.05f) return $"{model.Accuracy:P1} isn't clearly better than the rule's {rule.Accuracy:P1}";
        if (model.Recall < 0.9f) return $"misses {1 - model.Recall:P0} of obstacles";
        note = $"{model.Accuracy:P1} right (precision {model.Precision:0.00}, recall {model.Recall:0.00}) on {model.Count} held-out samples; " +
               $"\"{ObstacleClassifier.FrontRay} < {threshold:0.00}\" gets {rule.Accuracy:P1}";
        return null;
    }

    static string ModelFile(out string note)
    {
        note = null;
        var t = Trained();
        string folder = Path.Combine(Application.temporaryCachePath, "ml_template_check");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        var paths = t.Save(folder, null);
        var model = JObject.Parse(File.ReadAllText(paths.model));
        if ((string)model["schema"] != "albie.model/1" || (string)model["name"] != "obstacle_ahead") return "schema/name: " + model["schema"] + " " + model["name"];
        if (model["training"]?["val_accuracy"] == null || model["training"]?["baseline"] == null) return "the training block is missing accuracy or the baseline";
        // The file's net, built the way the graph node builds it, gives the trainer's probabilities.
        var io = new ControllerIO(MockTruckSim.SensorNames, MockTruckSim.ActuatorNames);
        string error = NetFile.ReadInputs(model["inputs"] as JArray, io, "inputs", out var sensor, out var mean, out var invStd);
        if (error != null) return error;
        if ((error = NetFile.Build(model["net"] as JObject, sensor.Length, 1, "net", out var rebuilt)) != null) return error;
        var features = new float[sensor.Length];
        float worst = 0;
        for (int i = 0; i < 200; i++)
        {
            var x = t.Data.X[i];
            for (int f = 0; f < features.Length; f++) features[f] = (x[sensor[f]] - mean[f]) * invStd[f];
            float fromFile = (rebuilt.Forward(features) + 1f) * 0.5f;
            worst = Math.Max(worst, Math.Abs(fromFile - t.Classifier.Probability(x)));
        }
        long size = new FileInfo(paths.model).Length;
        Directory.Delete(folder, true);
        if (worst > 0.002f) return $"probabilities differ by up to {worst:0.0000} after the round trip";
        note = $"{size / 1024f:0.0} KB; 200 samples agree within {worst:0.00000}";
        return null;
    }

    static long BytesAllocated(Action work) => AllocationCounter.BytesDuring(work);

    static ControllerHost Host() => new ControllerHost(new ControllerIO(MockTruckSim.SensorNames, MockTruckSim.ActuatorNames));

    static string Attach(JObject controller) =>
        Host().Attach(ControllerFile.Parse(controller.ToString(), out string parseError)) is string e ? e : parseError;

    static string BadNodes(out string note)
    {
        note = null;
        var c = Trained().Classifier;
        JObject Graph(Action<JObject> breakIt)
        {
            var node = c.ToGraphNode("brake", new JObject { ["drive.throttle"] = -1 });
            breakIt(node);
            return ObstacleTrial.CruiseGraph(node, 1.2f);
        }
        var cases = new List<(string code, Action<JObject> breakIt)>
        {
            ("unknown_channel", n => n["model"]["inputs"][0]["channel"] = "lidar.d0"),
            ("shape_mismatch", n => ((JArray)n["model"]["inputs"]).RemoveAt(0)),
            ("shape_mismatch", n => ((JArray)n["model"]["net"]["weights"][0]).RemoveAt(0)),
            ("bad_value", n => n["threshold"] = 1.5),
            ("bad_value", n => n.Remove("set")),
            ("unknown_channel", n => n["set"] = new JObject { ["rotor.thrust"] = 1 }),
            ("bad_value", n => n.Remove("model")),
            ("too_big", n => n["model"]["net"]["sizes"] = new JArray(8, 64, 1)),
        };
        if (Attach(Graph(_ => { })) is string good) return "the good node was refused: " + good;
        var seen = new List<string>();
        foreach (var (code, breakIt) in cases)
        {
            string error = Attach(Graph(breakIt));
            if (error == null || !error.StartsWith(code)) return $"expected {code}, got: {error ?? "accepted"}";
            seen.Add(code);
        }
        note = $"{cases.Count} broken nodes refused ({string.Join(", ", new HashSet<string>(seen))}); the error names the node path";
        return null;
    }

    static string TickAllocatesNothing(out string note)
    {
        note = null;
        var c = Trained().Classifier;
        var host = Host();
        string error = host.Attach(ControllerFile.Parse(ObstacleTrial.CruiseGraph(c.ToGraphNode("brake", new JObject { ["drive.throttle"] = -1 }), 1.5f).ToString(), out _));
        if (error != null) return error;
        var sim = new MockTruckSim { StopAtFinish = false };
        for (int i = 0; i < 10; i++) { sim.Sense(host.IO.Sensors); host.Step(0.02f, sim.Speed); sim.Step(host.IO.Actuators, 0.02f); }
        long bytes = BytesAllocated(() =>
        {
            for (int i = 0; i < 5000; i++)
            {
                if (sim.Crashed) sim.Reset();
                sim.Sense(host.IO.Sensors); host.Step(0.02f, sim.Speed); sim.Step(host.IO.Actuators, 0.02f);
            }
        });
        if (bytes > 0) return $"{bytes} bytes over 5000 ticks";
        float p = ((GraphController)host.Current).ModelProbability;
        if (float.IsNaN(p) || p < 0 || p > 1) return "ModelProbability is " + p;
        note = "5000 ticks, 0 bytes";
        return null;
    }

    // 3 trial seeds x 60 starts that a perfect brake can always stop from.
    static string ClosedLoop(out string note)
    {
        note = null;
        var c = Trained().Classifier;
        var learned = c.ToGraphNode("brake", new JObject { ["drive.throttle"] = ObstacleTemplate.BrakeThrottle });
        JObject Rule(float below, float throttle) => new JObject
            { ["id"] = "brake", ["type"] = "rule", ["if"] = new JArray(ObstacleClassifier.FrontRay, "<", below), ["set"] = new JObject { ["drive.throttle"] = throttle } };
        int none = 0, hand = 0, rule = 0, model = 0;
        float ruleDistance = 0, modelDistance = 0;
        foreach (int seed in new[] { 11, 12, 13 })
        {
            none += ObstacleTrial.Run(null, seed: seed).Crashes;
            hand += ObstacleTrial.Run(Rule(0.15f, -0.3f), seed: seed).Crashes;   // truck_cruise's hand-written brake
            var r = ObstacleTrial.Run(Rule(0.4f, -1f), seed: seed);              // the best fixed rule in the sweep
            var m = ObstacleTrial.Run(learned, seed: seed);
            rule += r.Crashes; ruleDistance += r.MeanDistance / 3f;
            model += m.Crashes; modelDistance += m.MeanDistance / 3f;
        }
        if (none < 170) return $"without a brake only {none}/180 crashed: the trial isn't aiming at anything";
        if (model > rule + 2) return $"learned brake crashed {model}/180, the best rule {rule}/180";
        if (model > 10) return $"learned brake crashed {model}/180";
        if (modelDistance < ruleDistance) return $"learned brake drove {modelDistance:0.00} m, the rule {ruleDistance:0.00} m";
        note = $"crashes out of 180: none {none}, truck_cruise's rule {hand}, best fixed rule {rule} ({ruleDistance:0.00} m driven), " +
               $"learned {model} ({modelDistance:0.00} m driven)";
        return null;
    }

    static string PlugsIn(out string note)
    {
        note = null;
        var asset = Resources.Load<TextAsset>("Mocks/controllers/truck_cruise");
        if (asset == null) return "missing Mocks/controllers/truck_cruise";
        var cruise = JObject.Parse(asset.text);
        var learned = Trained().WithLearnedBrake(cruise);
        if ((string)learned["name"] != "truck_cruise_learned_brake") return "name " + learned["name"];
        var nodes = (JArray)learned["nodes"];
        int models = 0, rules = 0;
        foreach (var n in nodes) { if ((string)n["type"] == "model") models++; if ((string)n["type"] == "rule") rules++; }
        if (models != 1 || rules != 0) return $"{models} model and {rules} rule nodes: the hand-written brake should be replaced";
        if ((string)nodes[nodes.Count - 1]["type"] != "model") return "the learned brake isn't last, so the cruise would override it";
        if ((string)cruise["nodes"][2]["type"] != "rule") return "the original controller was changed";
        string error = Attach(learned);
        if (error != null) return error;
        note = $"truck_cruise_learned_brake: cruise, avoid, then the learned brake ({learned.ToString().Length / 1024f:0.0} KB)";
        return null;
    }

    static string LearnedStillLoads(out string note)
    {
        note = null;
        foreach (string path in new[] { "Mocks/controllers/truck_autopilot", "Controllers/truck_autopilot_fallback" })
        {
            var asset = Resources.Load<TextAsset>(path);
            if (asset == null) return "missing " + path;
            string error = Attach(JObject.Parse(asset.text));
            if (error != null) return path + ": " + error;
        }
        var broken = JObject.Parse(Resources.Load<TextAsset>("Mocks/controllers/truck_autopilot").text);
        broken["net"]["sizes"][0] = 3;
        string refused = Attach(broken);
        if (refused == null || !refused.StartsWith("shape_mismatch: net.sizes[0]")) return "a wrong input count gave: " + (refused ?? "accepted");
        note = "truck_autopilot and the pretrained fallback attach; a bad shape still says net.sizes[0]";
        return null;
    }

    // ---- Albie's template tools (Resources/AlbieTools/controllers.json) ------------------------------------------

    static string ToolDefinitions(out string note)
    {
        note = null;
        var asset = Resources.Load<TextAsset>("AlbieTools/controllers");
        if (asset == null) return "no Resources/AlbieTools/controllers.json";
        var handlers = TemplateTools.Handlers();
        var names = new List<string>();
        foreach (JObject def in JArray.Parse(asset.text))
        {
            string name = (string)def["name"];
            names.Add(name);
            if (def["parameters"]?["type"]?.ToString() != "object") return name + " has no object parameters";
            if (string.IsNullOrEmpty((string)def["description"])) return name + " has no description";
            if (!handlers.ContainsKey(name)) return name + " has no handler";
            if (name.StartsWith("ml_")) return name + ": ml_ tools are the cloud's, and Albie says they need the internet";
        }
        if (names.Count != handlers.Count) return $"{names.Count} definitions for {handlers.Count} handlers";
        note = string.Join(", ", names);
        return null;
    }

    static string checkFolder;

    // Calls a tool the way AlbieTools does: a refusal (ArgumentException) comes back as {"error"}.
    static List<JObject> CallTool(string name, JObject args)
    {
        var answers = new List<JObject>();
        try { TemplateTools.Handlers()[name](args ?? new JObject(), r => answers.Add(r)); }
        catch (ArgumentException e) { answers.Add(AlbieTools.Error(e.Message)); }
        return answers;
    }

    static ControllerHost FakeTruck()
    {
        var host = new ControllerHost(new ControllerIO(MockTruckSim.SensorNames, MockTruckSim.ActuatorNames));
        var manual = ControllerFile.Parse(Resources.Load<TextAsset>("Mocks/controllers/truck_manual").text, out string error);
        if (error != null) throw new InvalidOperationException(error);
        host.Attach(manual);
        return host;
    }

    static void FreshRun()
    {
        TemplateRun.Reset();
        checkFolder = Path.Combine(Path.GetTempPath(), "albie_ml_template_check");
        if (Directory.Exists(checkFolder)) Directory.Delete(checkFolder, true);
        TemplateRun.FolderOverride = checkFolder;
    }

    static string TrainTool(out string note)
    {
        note = null;
        FreshRun();
        var host = FakeTruck();
        ThingHosts.Register("truck", host);
        var reports = new List<(string controller, int version, JObject training)>();
        void OnReport(string c, int v, JObject t) => reports.Add((c, v, t));
        TrainingLog.Finished += OnReport;
        try
        {
            var idle = CallTool("template_status", null)[0];
            if ((string)idle["state"] != "idle" || (bool)idle["brake_on"]) return "status before training: " + idle.ToString(Newtonsoft.Json.Formatting.None);
            var noModel = CallTool("learned_brake", new JObject { ["on"] = true })[0];
            if (!((string)noModel["error"] ?? "").StartsWith("no_model")) return "learned_brake before training: " + noModel.ToString(Newtonsoft.Json.Formatting.None);

            var answers = CallTool("template_train", null);
            if (answers.Count != 0) return "template_train answered before training: " + answers[0].ToString(Newtonsoft.Json.Formatting.None);
            var joined = CallTool("template_train", null);   // a second call joins the run
            var mid = CallTool("template_status", null)[0];
            if ((string)mid["state"] != "training") return "status while training: " + mid["state"];
            // Every frame of the run within budget: making data, training, and the finish (scoring, a file a frame, the report).
            int frames = 0;
            double worst = 0;
            var frame = new System.Diagnostics.Stopwatch();
            while (true)
            {
                frame.Restart();
                bool over = TemplateRun.Pump(FrameBudgetMs);
                worst = Math.Max(worst, frame.Elapsed.TotalMilliseconds);
                if (over) break;
                if (++frames > 2000) return "training never finished";
            }
            if (worst > FrameBudgetMs + 2.5) return $"a frame of the run took {worst:0.0} ms (budget {FrameBudgetMs} ms)";
            if (answers.Count != 1 || joined.Count != 1) return $"answers: {answers.Count} and {joined.Count} (expected one each)";
            var r = answers[0];
            if (r["error"] != null) return "template_train: " + r["error"];
            if ((string)r["state"] != "done" || (string)r["controller"] != "truck_cruise_learned_brake" || !(bool)r["saved"]) return "result " + r.ToString(Newtonsoft.Json.Formatting.None);
            if ((double)r["val_accuracy"] < 0.9 || (double)r["val_accuracy"] <= (double)r["rule_accuracy"]) return $"accuracy {r["val_accuracy"]} vs the rule's {r["rule_accuracy"]}";
            string say = (string)r["say"];
            string expected = $"{Math.Round((double)r["val_accuracy"] * 100)}% right on examples it hadn't seen; the simple front-sensor rule gets {Math.Round((double)r["rule_accuracy"] * 100)}%.";
            if (say != expected) return $"say \"{say}\", expected \"{expected}\"";
            if (!((string)r["next"]).Contains("learned_brake")) return "next doesn't offer the brake: " + r["next"];
            if (!File.Exists(Path.Combine(checkFolder, "obstacle_ahead.json")) || !File.Exists(Path.Combine(checkFolder, "truck_cruise_learned_brake.json")))
                return "files not saved in " + checkFolder;
            if (reports.Count != 1) return reports.Count + " training reports for one run";
            var rep = reports[0];
            if (rep.controller != "truck_cruise_learned_brake" || (string)rep.training["task"] != "binary" || (string)rep.training["model"] != "obstacle_ahead")
                return $"report {rep.controller} task {rep.training["task"]} model {rep.training["model"]}";
            var done = CallTool("template_status", null)[0];
            if ((string)done["state"] != "done" || (bool)done["brake_on"] || (string)done["controller"] != "truck_manual") return "status after training: " + done.ToString(Newtonsoft.Json.Formatting.None);
            note = $"{frames + 1} frames, worst {worst:0.0} ms; \"{say}\"";
            return null;
        }
        finally
        {
            TrainingLog.Finished -= OnReport;
            ThingHosts.Unregister("truck", host);
        }
    }

    static string BrakeTool(out string note)
    {
        note = null;
        if (TemplateRun.State != TemplateRun.Phase.Done) return "needs the run from the previous check";
        var host = FakeTruck();
        var noThing = CallTool("learned_brake", new JObject { ["on"] = true })[0];
        if (!((string)noThing["error"] ?? "").StartsWith("no_thing")) return "with no truck: " + noThing.ToString(Newtonsoft.Json.Formatting.None);
        ThingHosts.Register("truck", host);
        try
        {
            var bad = CallTool("learned_brake", new JObject { ["on"] = "yes" })[0];
            if (!((string)bad["error"] ?? "").StartsWith("bad_value")) return "on \"yes\": " + bad.ToString(Newtonsoft.Json.Formatting.None);
            var on = CallTool("learned_brake", new JObject { ["on"] = true })[0];
            if (on["error"] != null) return "on: " + on["error"];
            if (host.File.Name != "truck_cruise_learned_brake" || (string)on["replaced"] != "truck_manual") return $"on: running {host.File.Name}, replaced {on["replaced"]}";
            if (!(host.Current is GraphController)) return "the learned brake isn't a graph controller";
            var again = CallTool("learned_brake", new JObject { ["on"] = true })[0];
            if ((bool)again["changed"]) return "turning it on twice attached it twice";
            if (!(bool)CallTool("template_status", null)[0]["brake_on"]) return "status doesn't say the brake is on";

            // Retraining with the brake on goes into the same controller: the name doesn't grow.
            var retrain = CallTool("template_train", null);
            while (!TemplateRun.Pump(FrameBudgetMs)) { }
            if ((string)retrain[0]["controller"] != "truck_cruise_learned_brake") return "retrained into " + retrain[0]["controller"];

            var off = CallTool("learned_brake", new JObject { ["on"] = false })[0];
            if (off["error"] != null) return "off: " + off["error"];
            if (host.File.Name != "truck_manual") return "off: running " + host.File.Name + ", expected truck_manual back";
            var offAgain = CallTool("learned_brake", new JObject { ["on"] = false })[0];
            if (!((string)offAgain["error"] ?? "").StartsWith("not_on")) return "off twice: " + offAgain.ToString(Newtonsoft.Json.Formatting.None);
            note = "truck_manual → truck_cruise_learned_brake → truck_manual; refusals no_model, no_thing, bad_value, not_on";
            return null;
        }
        finally
        {
            ThingHosts.Unregister("truck", host);
            if (checkFolder != null && Directory.Exists(checkFolder)) Directory.Delete(checkFolder, true);
        }
    }
}
