using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// Albie's tools for the on-device ML template (<c>Resources/AlbieTools/controllers.json</c>, ML A). They run on the
    /// headset with no hub:
    /// - <c>template_train</c> runs <see cref="TemplateRun"/> and answers when training is done (it's a slow tool);
    /// - <c>template_status</c> reads its state;
    /// - <c>learned_brake {on}</c> puts the learned brake on a Thing (<see cref="ThingHosts"/>), or gives back what it had.
    /// None of them send anything off the device or spend money, so none need a confirm step (C-19 section 5).
    /// Every number in a result comes from the run's training block.
    /// </summary>
    public static class TemplateTools
    {
        public const string DefaultThing = "truck";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register() => AlbieTools.Register("controllers", Handlers());

        /// <summary>The handlers by tool name (public for the checks).</summary>
        public static Dictionary<string, AlbieTools.AsyncHandler> Handlers() => new Dictionary<string, AlbieTools.AsyncHandler>
        {
            ["template_train"] = TrainTool,
            ["template_status"] = (args, done) => done(StatusTool(args)),
            ["learned_brake"] = (args, done) => done(BrakeTool(args)),
        };

        /// <summary>Starts a run (or joins the one training) and answers when it ends.</summary>
        public static void TrainTool(JObject args, Action<JObject> done)
        {
            var host = ThingHosts.Find(DefaultThing);
            string error = TemplateRun.Start(TemplateRun.Training ? null : TemplateRun.BaseFor(host));
            if (error != null) { done(AlbieTools.Error(error)); return; }
            TemplateRun.WhenDone(() =>
            {
                if (TemplateRun.State != TemplateRun.Phase.Done) { done(AlbieTools.Error(TemplateRun.Error ?? "training_failed: it stopped before the end")); return; }
                var result = Summary();
                result["next"] = host == null
                    ? "Say how it did in one line. No truck is running right now, so the learned brake can go on it later."
                    : "Say how it did in one line, then offer to put it on the truck as its brake (learned_brake on).";
                done(result);
            });
        }

        public static JObject StatusTool(JObject args)
        {
            string thing = (string)args?["thing"] ?? DefaultThing;
            var host = ThingHosts.Find(thing);
            JObject result;
            switch (TemplateRun.State)
            {
                case TemplateRun.Phase.Training:
                    var c = TemplateRun.Current.Classifier;
                    result = new JObject { ["state"] = "training", ["epoch"] = c.Epoch, ["epochs"] = c.Settings.Epochs, ["samples"] = TemplateRun.Current.Data.Count };
                    break;
                case TemplateRun.Phase.Done:
                    result = Summary();
                    break;
                case TemplateRun.Phase.Failed:
                    result = new JObject { ["state"] = "failed", ["error"] = TemplateRun.Error };
                    break;
                default:
                    result = new JObject { ["state"] = "idle", ["next"] = "Nothing trained yet in this session: template_train trains it." };
                    break;
            }
            result["thing"] = thing;
            result["thing_running"] = host != null;
            result["brake_on"] = TemplateRun.BrakeOn(host);
            if (host?.File != null) result["controller"] = host.File.Name;
            return result;
        }

        public static JObject BrakeTool(JObject args)
        {
            if (args?["on"] == null || args["on"].Type != JTokenType.Boolean) throw new ArgumentException("bad_value: on must be true or false");
            bool on = (bool)args["on"];
            string thing = (string)args["thing"] ?? DefaultThing;
            var host = ThingHosts.Find(thing);
            if (host == null) throw new ArgumentException($"no_thing: the {thing} isn't running right now");
            if (on && TemplateRun.Training) throw new ArgumentException("training: the ML template is still training; wait for it to finish");
            if (on && TemplateRun.BrakeOn(host)) return new JObject { ["thing"] = thing, ["on"] = true, ["controller"] = host.File.Name, ["changed"] = false };
            string error = on ? TemplateRun.AttachBrake(host) : TemplateRun.DetachBrake(host);
            if (error != null) throw new ArgumentException(error);
            var result = new JObject { ["thing"] = thing, ["on"] = on, ["controller"] = host.File.Name, ["changed"] = true };
            if (on && TemplateRun.Replaced(host) != null) result["replaced"] = TemplateRun.Replaced(host);
            return result;
        }

        /// <summary>A finished run, from its training block.</summary>
        static JObject Summary()
        {
            var t = TemplateRun.TrainingBlock;
            var result = new JObject
            {
                ["state"] = "done",
                ["model"] = t["model"],
                ["samples"] = t["data"]?["samples"],
                ["epochs"] = t["epochs"],
                ["frames"] = t["frames"],
                ["seconds"] = t["wall_seconds"],
                ["val_accuracy"] = t["val_accuracy"],
                ["rule"] = t["baseline"]?["rule"],
                ["rule_accuracy"] = t["baseline"]?["val_accuracy"],
                ["controller"] = TemplateRun.LearnedBrake?["name"],
                ["saved"] = TemplateRun.SaveError == null,
            };
            if (TemplateRun.SaveError != null) result["save_error"] = TemplateRun.SaveError;
            string say = Say(t);
            if (say != null) result["say"] = say;
            return result;
        }

        // "97% right on examples it hadn't seen; the simple front-sensor rule gets 86%." Only numbers from the block.
        public static string Say(JObject training)
        {
            if (!(Number(training?["val_accuracy"]) is double model)) return null;
            string line = Percent(model) + " right on examples it hadn't seen";
            if (Number(training["baseline"]?["val_accuracy"]) is double rule) line += "; the simple front-sensor rule gets " + Percent(rule);
            return line + ".";
        }

        static double? Number(JToken t) => t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) ? (double)t : (double?)null;
        static string Percent(double v) => Math.Round(v * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
    }
}
