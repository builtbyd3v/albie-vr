using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.ML
{
    /// <summary>ML B's voice/agent bridge for the brain panel. It controls the visual preview only.</summary>
    public static class ControllerBrainTools
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register() => AlbieTools.Register("controller_brain", new Dictionary<string, AlbieTools.Handler>
        {
            ["brain_record"] = Record,
            ["brain_train"] = Train,
            ["autopilot_on"] = _ => Autopilot(true),
            ["autopilot_off"] = _ => Autopilot(false),
            ["brain_status"] = _ => View().ToolState(),
        });

        static ControllerBrainView View()
        {
            var view = UnityEngine.Object.FindAnyObjectByType<ControllerBrainView>();
            if (view == null) throw new ArgumentException("The controller brain panel is not open.");
            return view;
        }

        static JObject Record(JObject args)
        {
            bool start = args["start"] == null || (bool)args["start"];
            var view = View();
            if (!view.SetRecording(start)) throw new ArgumentException("The brain is training; wait for training to finish first.");
            var result = view.ToolState(); result["recording"] = start; return result;
        }

        static JObject Train(JObject args)
        {
            var view = View();
            if (!view.TrainPreview()) throw new ArgumentException("The brain is already training, or no lap samples are available.");
            var result = view.ToolState(); result["training_started"] = true; return result;
        }

        static JObject Autopilot(bool on)
        {
            var view = View();
            if (!view.SetAutopilotPreview(on)) throw new ArgumentException("The brain is training; wait for training to finish first.");
            var result = view.ToolState(); result["autopilot_preview"] = on; return result;
        }
    }
}
