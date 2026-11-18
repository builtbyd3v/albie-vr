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
        };

        public static JObject Show(JObject args)
        {
            var tx = RcTransmitter.Ensure();
            string where = tx.Show();
            var result = new JObject { ["shown"] = true, ["where"] = where, ["drives"] = Drives(tx.Driving) };
            result["how"] = tx.Driving == RcTransmitter.Target.Drone
                ? "Mode 2: left stick climbs and turns, right stick flies. Take off/Land and Home/Reset buttons. Let go and it hovers."
                : tx.Driving == RcTransmitter.Target.None
                    ? "Nothing to drive yet: open a truck or drone course and the transmitter links to it."
                    : "Left stick throttle, right stick steering. Horn and Reset buttons. Let go and it stops.";
            return result;
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
