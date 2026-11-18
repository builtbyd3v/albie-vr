using System;
using System.IO;
using Forge.Notebook;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// The truck design of the open project, kept on the device: <c>&lt;persistentDataPath&gt;/truck_designs/&lt;project&gt;.json</c>
    /// holds the live values (what the truck drives with now, saved or not), the last saved version and every saved version
    /// with what it changed. The project is the notebook's open one (<see cref="NotebookStore.ProjectId"/>), else the app's
    /// truck project (<see cref="NotebookStore.DefaultProject"/>). Works with no hub and no cloud: this is the truth for how
    /// the truck drives; the CAD part (hub/cad/parts/truck.py) takes the same parameter names.
    ///
    /// <see cref="Set"/> changes the live design (the truck on the course changes at once, <see cref="Changed"/>); a slider
    /// drag is not a version. <see cref="SaveVersion"/> cuts one: design version n+1, and a project version in the notebook
    /// (<c>cut_by: thing</c>, pinning <c>things.truck</c>) with a summary of what changed in driving.
    /// </summary>
    public static class TruckDesignStore
    {
        public const string Schema = "albie.truck_design/1";

        /// <summary>The live design changed (a value, a reset, a project switch). Raised on the main thread.</summary>
        public static event Action Changed;

        static TruckDesign live, saved;
        static string project, announced;
        static int version;
        static JArray history = new JArray();
        static bool dirty;
        static float dirtySince;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Changed = null;
            live = saved = null;
            project = announced = null;
            version = 0;
            history = new JArray();
            dirty = false;
        }

        /// <summary>The project whose design this is now.</summary>
        public static string ProjectId { get { Ensure(); return project; } }
        /// <summary>The live design (do not change its values directly: use <see cref="Set"/>).</summary>
        public static TruckDesign Current { get { Ensure(); return live; } }
        /// <summary>The last saved version's design (the stock truck before the first save).</summary>
        public static TruckDesign Saved { get { Ensure(); return saved; } }
        /// <summary>The newest saved design version (0: never saved, the stock truck).</summary>
        public static int Version { get { Ensure(); return version; } }
        /// <summary>The live design differs from the last saved one.</summary>
        public static bool Edited { get { Ensure(); return !live.Same(saved); } }
        public static TruckPhysics Physics => Current.Derive();
        /// <summary>The live design's key: a brain trained on another key learned on a different truck.</summary>
        public static string Fingerprint => Current.Fingerprint();

        static string WantedProject => NotebookStore.IsOpen ? NotebookStore.ProjectId : NotebookStore.DefaultProject;

        /// <summary>Loads the open project's design if the project changed since. Cheap when it didn't (one string compare).</summary>
        public static bool Ensure()
        {
            string want = WantedProject;
            if (live != null && want == project) return false;
            if (live != null) Flush();
            project = want;
            Load();
            return true;
        }

        /// <summary>Re-checks the project (a switch raises <see cref="Changed"/>) and writes pending live edits. Call about once a second.</summary>
        public static void Poll()
        {
            Ensure();
            if (project != announced) Raise();
            if (dirty && Time.unscaledTime - dirtySince > 1f) Flush();
        }

        // ---- Changing it ---------------------------------------------------------------------------------------

        /// <summary>Sets one live value by parameter name (clamped, snapped). Null when done, else the error.</summary>
        public static string Set(string name, float value, bool raise = true)
        {
            int i = TruckDesign.IndexOf(name);
            if (i < 0) return "unknown parameter \"" + name + "\": use " + Names();
            return Set(i, value, raise);
        }

        public static string Set(int index, float value, bool raise = true)
        {
            Ensure();
            float before = live.Values[index];
            live.Set(index, value);
            if (Mathf.Approximately(before, live.Values[index])) return null;
            MarkDirty();
            if (raise) Raise();
            return null;
        }

        /// <summary>Back to the stock truck (live; save to make it a version).</summary>
        public static void ResetToStock()
        {
            Ensure();
            live = new TruckDesign();
            MarkDirty();
            Raise();
        }

        /// <summary>Back to the last saved version.</summary>
        public static void RevertToSaved()
        {
            Ensure();
            live.CopyFrom(saved);
            MarkDirty();
            Raise();
        }

        public static void Raise()
        {
            announced = project;
            try { Changed?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
        }

        static void MarkDirty()
        {
            if (!dirty) dirtySince = Time.unscaledTime;
            dirty = true;
        }

        public static string Names()
        {
            var names = new string[TruckDesign.Params.Length];
            for (int i = 0; i < names.Length; i++) names[i] = TruckDesign.Params[i].Name;
            return string.Join(", ", names);
        }

        /// <summary>
        /// Saves the live design as the next version: written on the device, and cut as a project version when the notebook
        /// may cut one for the open project (<see cref="ProjectSpine.MayCut"/>). <paramref name="summary"/> says what changed
        /// in driving. Returns the new design version, or 0 when nothing changed since the last save.
        /// </summary>
        public static int SaveVersion(out string summary, out int projectVersion)
        {
            Ensure();
            projectVersion = 0;
            string change = TruckDesign.Compare(saved, live);
            if (string.IsNullOrEmpty(change))
            {
                summary = version == 0 ? "This is the stock truck: change something first." : "Nothing changed since truck v" + version + ".";
                return 0;
            }
            version++;
            summary = "Truck v" + version + ": " + change;
            history.Add(new JObject
            {
                ["version"] = version,
                ["params"] = ToJson(live),
                ["summary"] = summary,
                ["at"] = DateTime.UtcNow.ToString("o"),
            });
            saved = live.Clone();
            dirty = true;
            Flush();
            if (ProjectSpine.MayCut && NotebookStore.ProjectId == project)
            {
                try
                {
                    projectVersion = NotebookStore.CutVersion("thing", summary, new JObject { ["things"] = new JObject { ["truck"] = version } });
                }
                catch (Exception e)
                {
                    Debug.Log("[Truck] Couldn't cut a project version for the design: " + e.Message);
                }
            }
            Raise();
            return version;
        }

        // ---- Disk ----------------------------------------------------------------------------------------------

        static string FilePath(string id) => Path.Combine(Application.persistentDataPath, "truck_designs", (string.IsNullOrEmpty(id) ? "default" : id) + ".json");

        public static JObject ToJson(TruckDesign d)
        {
            var o = new JObject();
            for (int i = 0; i < TruckDesign.Params.Length; i++) o[TruckDesign.Params[i].Name] = Math.Round(d.Values[i], 3);
            return o;
        }

        static TruckDesign FromJson(JToken token)
        {
            var d = new TruckDesign();
            if (!(token is JObject o)) return d;
            for (int i = 0; i < TruckDesign.Params.Length; i++)
            {
                JToken v = o[TruckDesign.Params[i].Name];
                if (v != null && (v.Type == JTokenType.Float || v.Type == JTokenType.Integer)) d.Set(i, (float)v);
            }
            return d;
        }

        static void Load()
        {
            live = new TruckDesign();
            saved = new TruckDesign();
            version = 0;
            history = new JArray();
            dirty = false;
            string path = FilePath(project);
            try
            {
                if (!File.Exists(path)) return;
                var root = JObject.Parse(File.ReadAllText(path));
                live = FromJson(root["params"]);
                saved = FromJson(root["saved"]);
                version = (int?)root["version"] ?? 0;
                history = root["history"] as JArray ?? new JArray();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Truck] The truck design at " + path + " didn't load (" + e.Message + "); using the stock truck.");
                live = new TruckDesign();
                saved = new TruckDesign();
            }
        }

        /// <summary>Writes the live design now if it changed.</summary>
        public static void Flush()
        {
            if (!dirty || live == null) return;
            dirty = false;
            string path = FilePath(project);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var root = new JObject
                {
                    ["schema"] = Schema,
                    ["project_id"] = project,
                    ["version"] = version,
                    ["params"] = ToJson(live),
                    ["saved"] = ToJson(saved),
                    ["history"] = history,
                };
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, root.ToString(Formatting.Indented));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Truck] Couldn't save the truck design: " + e.Message);
            }
        }

        /// <summary>The saved versions, oldest first ({version, params, summary, at}).</summary>
        public static JArray History { get { Ensure(); return history; } }

        /// <summary>The design and its numbers for a tool result.</summary>
        public static JObject Describe()
        {
            Ensure();
            var p = live.Derive();
            return new JObject
            {
                ["project"] = project,
                ["version"] = version,
                ["edited_since_save"] = Edited,
                ["params"] = ToJson(live),
                ["physics"] = new JObject
                {
                    ["top_speed_m_s"] = Math.Round(p.TopSpeed, 2),
                    ["accel_m_s2"] = Math.Round(p.Accel, 2),
                    ["time_to_90pct_s"] = Math.Round(p.TimeTo90, 2),
                    ["stop_from_top_m"] = Math.Round(p.StopDistance, 2),
                    ["turn_radius_m"] = Math.Round(p.TurnRadius, 3),
                    ["grip_limit_g"] = Math.Round(p.GripAccel / TruckDesign.Gravity, 2),
                    ["rollover_limit_g"] = Math.Round(p.TipAccel / TruckDesign.Gravity, 2),
                    ["full_lock_max_speed_m_s"] = Math.Round(p.CornerSpeed, 2),
                    ["past_the_limit"] = p.TipsFirst ? "rolls over" : "slides (understeer)",
                    ["traction_limited_launch"] = p.TractionLimited,
                    ["mass_g"] = Math.Round(p.MassKg * 1000f),
                    ["battery_v"] = Math.Round(p.Volts, 1),
                },
                ["readout"] = TruckDesign.Readout(p),
            };
        }
    }
}
