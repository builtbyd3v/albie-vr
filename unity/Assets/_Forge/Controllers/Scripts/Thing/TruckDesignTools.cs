using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// Albie's tools for the truck's design (<c>Resources/AlbieTools/truck_design.json</c>): change it
    /// (<c>truck_set_params</c>, optionally saving a version), show the panel (<c>truck_design_show</c>) and read it with its
    /// physics (<c>truck_describe</c>). They change a simulated truck and can all be undone (reset), so none needs a
    /// confirm step. Work offline: the device's model (<see cref="TruckDesign"/>) is the truth.
    /// </summary>
    public static class TruckDesignTools
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { brain = null; nextFind = float.NegativeInfinity; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register() => AlbieTools.Register("truck_design", Handlers());

        /// <summary>The handlers by tool name (public for checks).</summary>
        public static Dictionary<string, AlbieTools.Handler> Handlers() => new Dictionary<string, AlbieTools.Handler>
        {
            ["truck_set_params"] = SetParams,
            ["truck_design_show"] = args =>
            {
                bool on = (bool?)args?["on"] ?? true;
                if (!on)
                {
                    TruckDesignPanel.Close();
                    return new JObject { ["ok"] = true, ["said"] = "The truck design panel is closed." };
                }
                bool opened = TruckDesignPanel.Open() != null;
                var result = TruckDesignStore.Describe();
                result["ok"] = opened;
                result["said"] = opened ? "Here's the truck's design. Drag a slider and the truck drives differently straight away."
                                        : "There's no window system here to show the panel in; I can still change the design by voice.";
                return result;
            },
            ["truck_describe"] = args =>
            {
                var result = TruckDesignStore.Describe();
                var history = TruckDesignStore.History;
                var recent = new JArray();
                for (int i = Mathf.Max(0, history.Count - 3); i < history.Count; i++) recent.Add(history[i]?["summary"]);
                result["recent_versions"] = recent;
                string note = AutopilotNote();
                if (note != null) result["autopilot_note"] = note;
                return result;
            },
        };

        static JObject SetParams(JObject args)
        {
            args = args ?? new JObject();
            var before = TruckDesignStore.Current.Clone();
            string reset = (string)args["reset"];
            if (reset == "stock") TruckDesignStore.ResetToStock();
            else if (reset == "saved") TruckDesignStore.RevertToSaved();
            else if (!string.IsNullOrEmpty(reset)) return AlbieTools.Error("bad_value: reset is 'stock' or 'saved'");

            var unknown = new List<string>();
            int set = 0;
            foreach (var pair in args)
            {
                if (pair.Key == "reset" || pair.Key == "save_version") continue;
                if (TruckDesign.IndexOf(pair.Key) < 0) { unknown.Add(pair.Key); continue; }
                if (pair.Value == null || (pair.Value.Type != JTokenType.Float && pair.Value.Type != JTokenType.Integer))
                    return AlbieTools.Error("bad_value: " + pair.Key + " needs a number");
                TruckDesignStore.Set(pair.Key, (float)pair.Value, false);
                set++;
            }
            if (unknown.Count > 0 && set == 0 && string.IsNullOrEmpty(reset))
                return AlbieTools.Error("unknown parameter " + string.Join(", ", unknown) + ": use " + TruckDesignStore.Names());
            TruckDesignStore.Raise();

            var result = TruckDesignStore.Describe();
            string change = TruckDesign.Compare(before, TruckDesignStore.Current);
            result["ok"] = true;
            result["changed"] = string.IsNullOrEmpty(change) ? "nothing (already those values)" : change;
            if (unknown.Count > 0) result["ignored"] = new JArray(unknown.ToArray());
            string said = string.IsNullOrEmpty(change) ? "The truck already has those values." : change;
            if ((bool?)args["save_version"] ?? false)
            {
                int v = TruckDesignStore.SaveVersion(out string summary, out int projectVersion);
                result["saved_version"] = v;
                if (projectVersion > 0) result["project_version"] = projectVersion;
                said = v > 0 ? summary + (projectVersion > 0 ? " Saved as project version " + projectVersion + "." : " Saved on this headset.") : summary;
            }
            string note = AutopilotNote();
            if (note != null) { result["autopilot_note"] = note; said += " " + note; }
            result["said"] = said;
            return result;
        }

        static Forge.Artifacts.SlalomBrain brain;
        static float nextFind = float.NegativeInfinity;

        /// <summary>"Your autopilot learned on the old truck; ..." when the trained brain was trained on another design, else null.</summary>
        public static string AutopilotNote()
        {
            if (brain == null && Time.unscaledTime >= nextFind)
            {
                nextFind = Time.unscaledTime + 2f;
                brain = Object.FindAnyObjectByType<Forge.Artifacts.SlalomBrain>();
            }
            return brain != null ? brain.DesignNote : null;
        }
    }
}
