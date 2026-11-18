using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// Albie's tools for the RC transmitter (<c>Resources/AlbieTools/transmitter.json</c>): <c>transmitter_show</c> hands it to
    /// the person, <c>transmitter_hide</c> puts it away. Both run on the headset with no hub and send nothing anywhere.
    /// </summary>
    public static class TransmitterTools
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register() => AlbieTools.Register("transmitter", Handlers());

        /// <summary>The handlers by tool name (public for the checks).</summary>
        public static Dictionary<string, AlbieTools.Handler> Handlers() => new Dictionary<string, AlbieTools.Handler>
        {
            ["transmitter_show"] = Show,
            ["transmitter_hide"] = Hide,
            ["transmitter_aids"] = Aids,
        };

        public static JObject Show(JObject args)
        {
            var tx = RcTransmitter.Ensure();
            // docked (optional): true docks it on its fixed spot, false makes it handheld; absent keeps the setting.
            var docked = args?["docked"];
            if (docked != null && docked.Type == JTokenType.Boolean && (bool)docked != RcTransmitter.Docked) tx.SetDocked((bool)docked);
            string where = tx.Show();
            var result = new JObject { ["shown"] = true, ["where"] = where, ["drives"] = Drives(tx.Driving), ["docked"] = RcTransmitter.Docked };
            result["how"] = tx.Driving == RcTransmitter.Target.Drone
                ? "Mode 2: left stick climbs and turns, right stick flies. Take off/Land and Home/Reset buttons. Let go and it hovers."
                : tx.Driving == RcTransmitter.Target.None
                    ? "Nothing to drive yet: open a truck or drone course and the transmitter links to it."
                    : "Left stick throttle, right stick steering. Horn and Reset buttons. Let go and it stops.";
            if (RcTransmitter.Docked) result["how"] += " Docked: rest a thumb on each stick; lifting a thumb centres that stick.";
            return result;
        }

        /// <summary>transmitter_aids: sets any of the truck's driving aids given, and returns them all.</summary>
        public static JObject Aids(JObject args)
        {
            if (args != null)
            {
                if (args["training_speed"] != null && (args["training_speed"].Type == JTokenType.Float || args["training_speed"].Type == JTokenType.Integer))
                    DriveAids.TrainingSpeed = Mathf.Clamp(Mathf.Round((float)args["training_speed"] * 10f) / 10f, DriveAids.MinTrainingSpeed, DriveAids.MaxTrainingSpeed);
                if (args["steering_percent"] != null && (args["steering_percent"].Type == JTokenType.Float || args["steering_percent"].Type == JTokenType.Integer))
                    DriveAids.SteerRate = Mathf.Clamp(Mathf.Round((float)args["steering_percent"]) / 100f, DriveAids.MinSteerRate, 1f);
                if (args["soft_centre"] != null && args["soft_centre"].Type == JTokenType.Boolean)
                    DriveAids.SteerExpo = (bool)args["soft_centre"] ? DriveAids.DefaultSteerExpo : 0f;
                if (args["steady_throttle"] != null && args["steady_throttle"].Type == JTokenType.Boolean)
                {
                    DriveAids.SteadyThrottle = (bool)args["steady_throttle"];
                    if (!DriveAids.SteadyThrottle) DriveAids.EndCruise();
                }
            }
            return new JObject
            {
                ["training_speed"] = System.Math.Round(DriveAids.TrainingSpeed, 2),
                ["steering_percent"] = Mathf.RoundToInt(DriveAids.SteerRate * 100f),
                ["soft_centre"] = DriveAids.SteerExpo > 0.01f,
                ["steady_throttle"] = DriveAids.SteadyThrottle,
                ["docked"] = RcTransmitter.Docked,
                ["summary"] = DriveAids.Summary(RcTransmitter.Docked),
            };
        }

        public static JObject Hide(JObject args)
        {
            var tx = RcTransmitter.Instance;
            if (tx == null || !tx.Shown) return new JObject { ["hidden"] = true, ["note"] = "it was already put away" };
            tx.Hide();
            return new JObject { ["hidden"] = true };
        }

        static string Drives(RcTransmitter.Target t)
        {
            switch (t)
            {
                case RcTransmitter.Target.Drone: return "the drone";
                case RcTransmitter.Target.Truck: return "the brain truck on the slalom";
                case RcTransmitter.Target.OtherTruck: return "the truck";
                default: return "nothing yet";
            }
        }
    }
}
