using System;
using System.Collections.Generic;
using Forge.Controllers;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.ML
{
    /// <summary>
    /// ML B's voice/agent bridge for the brain beat. When a brain loop is running (<see cref="BrainLoops.Active"/>,
    /// ML A's truck with a controller), the tools drive it for real: laps recorded by hand, training, the Autopilot.
    /// With none running, they control the brain panel's visual preview, as before.
    /// </summary>
    public static class ControllerBrainTools
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register() => AlbieTools.Register("controller_brain", new Dictionary<string, AlbieTools.Handler>
        {
            ["brain_record"] = Record,
            ["brain_train"] = Train,
            ["autopilot_on"] = _ => Autopilot(true),
            ["autopilot_off"] = _ => Autopilot(false),
            ["brain_status"] = _ => BrainLoops.Active != null ? BrainLoops.Active.ToolState() : View().ToolState(),
        });

        static ControllerBrainView View()
        {
            var view = UnityEngine.Object.FindAnyObjectByType<ControllerBrainView>();
            if (view == null) throw new ArgumentException("The controller brain panel is not open.");
            return view;
        }

        /// <summary>A loop call's result: its state, or the reason it refused, as the tool's error.</summary>
        static JObject Real(IBrainLoop loop, string error)
        {
            if (error != null) throw new ArgumentException(error);
            return loop.ToolState();
        }

        static JObject Record(JObject args)
        {
            bool start = args["start"] == null || (bool)args["start"];
            var loop = BrainLoops.Active;
            if (loop != null)
            {
                if (!start) return Real(loop, loop.StopRecording());
                int laps = args["laps"] != null ? (int)args["laps"] : 2;
                bool then = args["then_autopilot"] != null && (bool)args["then_autopilot"];
                return Real(loop, loop.Record(laps, then));
            }
            var view = View();
            if (!view.SetRecording(start)) throw new ArgumentException("The brain is training; wait for training to finish first.");
            var result = view.ToolState(); result["recording"] = start; return result;
        }

        static JObject Train(JObject args)
        {
            var loop = BrainLoops.Active;
            if (loop != null) return Real(loop, loop.Train());
            var view = View();
            if (!view.TrainPreview()) throw new ArgumentException("The brain is already training, or no lap samples are available.");
            var result = view.ToolState(); result["training_started"] = true; return result;
        }

        static JObject Autopilot(bool on)
        {
            var loop = BrainLoops.Active;
            if (loop != null) return Real(loop, on ? loop.StartAutopilot() : loop.StopAutopilot());
            var view = View();
            if (!view.SetAutopilotPreview(on)) throw new ArgumentException("The brain is training; wait for training to finish first.");
            var result = view.ToolState(); result["autopilot_preview"] = on; return result;
        }
    }
}
