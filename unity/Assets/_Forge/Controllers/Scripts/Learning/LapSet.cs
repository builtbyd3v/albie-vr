using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Forge.Controllers
{
    /// <summary>One recorded lap: sensor rows x[t] and commanded actuator rows y[t] (contract section 4).</summary>
    public sealed class Lap
    {
        public int Id;
        public float Seconds;
        /// <summary>"completed", "crashed" or "aborted". Training uses completed laps.</summary>
        public string Outcome;
        public float[][] X, Y;
    }

    /// <summary>
    /// An <c>albie.laps/1</c> file (contract section 4): the laps from one recording session, with the channel names and
    /// the Thing's mirror map. Written by <see cref="LapRecorder"/> and hub/ml/make_controller_mocks.py; read by
    /// <see cref="BehaviourCloner"/> and the NN view.
    /// </summary>
    public sealed class LapSet
    {
        public const string Schema = "albie.laps/1";

        public string Thing = "truck";
        public int ThingVersion = 1;
        public string Controller = "";
        public int Hz = 50;
        public string[] Sensors = Array.Empty<string>();
        public string[] Actuators = Array.Empty<string>();
        public string[][] MirrorSwap = Array.Empty<string[]>();
        public string[] MirrorNegate = Array.Empty<string>();
        public string Notes;
        public readonly List<Lap> Laps = new List<Lap>();

        public int CompletedLaps
        {
            get { int n = 0; foreach (var lap in Laps) if (lap.Outcome == "completed") n++; return n; }
        }

        public static LapSet Parse(string json, out string error)
        {
            JObject raw;
            try { raw = JObject.Parse(json); }
            catch (JsonException e) { error = "bad_value: not valid JSON (" + e.Message + ")"; return null; }
            if ((string)raw["schema"] != Schema) { error = $"bad_value: schema must be \"{Schema}\""; return null; }
            var set = new LapSet
            {
                Thing = (string)raw["thing"],
                ThingVersion = raw["thing_version"]?.Type == JTokenType.Integer ? (int)raw["thing_version"] : 1,
                Controller = (string)raw["controller"],
                Hz = raw["hz"]?.Type == JTokenType.Integer ? (int)raw["hz"] : 50,
                Sensors = raw["sensors"]?.ToObject<string[]>() ?? Array.Empty<string>(),
                Actuators = raw["actuators"]?.ToObject<string[]>() ?? Array.Empty<string>(),
                MirrorSwap = raw["mirror"]?["swap"]?.ToObject<string[][]>() ?? Array.Empty<string[]>(),
                MirrorNegate = raw["mirror"]?["negate"]?.ToObject<string[]>() ?? Array.Empty<string>(),
                Notes = (string)raw["notes"],
            };
            if (set.Sensors.Length == 0 || set.Actuators.Length == 0) { error = "bad_value: sensors and actuators must not be empty"; return null; }
            foreach (var l in raw["laps"] ?? new JArray())
            {
                var lap = new Lap
                {
                    Id = (int)l["id"],
                    Seconds = (float)l["seconds"],
                    Outcome = (string)l["outcome"],
                    X = l["x"].ToObject<float[][]>(),
                    Y = l["y"].ToObject<float[][]>(),
                };
                if (lap.X.Length != lap.Y.Length) { error = $"bad_value: lap {lap.Id} has {lap.X.Length} x rows and {lap.Y.Length} y rows"; return null; }
                for (int t = 0; t < lap.X.Length; t++)
                    if (lap.X[t].Length != set.Sensors.Length || lap.Y[t].Length != set.Actuators.Length)
                    {
                        error = $"shape_mismatch: lap {lap.Id} row {t} doesn't match {set.Sensors.Length} sensors and {set.Actuators.Length} actuators";
                        return null;
                    }
                set.Laps.Add(lap);
            }
            error = null;
            return set;
        }

        /// <summary>The file, one line per row so it stays diffable (same layout as the Python generator).</summary>
        public string ToJson()
        {
            var sb = new StringBuilder(64 * 1024);
            sb.Append("{\n");
            Field(sb, "schema", Schema); Field(sb, "thing", Thing);
            sb.Append("  \"thing_version\": ").Append(ThingVersion).Append(",\n");
            Field(sb, "controller", Controller);
            sb.Append("  \"hz\": ").Append(Hz).Append(",\n");
            sb.Append("  \"sensors\": ").Append(JsonConvert.SerializeObject(Sensors)).Append(",\n");
            sb.Append("  \"actuators\": ").Append(JsonConvert.SerializeObject(Actuators)).Append(",\n");
            sb.Append("  \"mirror\": {\"swap\": ").Append(JsonConvert.SerializeObject(MirrorSwap))
              .Append(", \"negate\": ").Append(JsonConvert.SerializeObject(MirrorNegate)).Append("},\n");
            if (!string.IsNullOrEmpty(Notes)) Field(sb, "notes", Notes);
            sb.Append("  \"laps\": [");
            for (int i = 0; i < Laps.Count; i++)
            {
                var lap = Laps[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    {\n      \"id\": ").Append(lap.Id)
                  .Append(", \"seconds\": ").Append(Num(lap.Seconds, "0.##"))
                  .Append(", \"outcome\": ").Append(JsonConvert.SerializeObject(lap.Outcome)).Append(",\n");
                Rows(sb, "x", lap.X); sb.Append(",\n");
                Rows(sb, "y", lap.Y); sb.Append("\n    }");
            }
            sb.Append("\n  ]\n}\n");
            return sb.ToString();
        }

        static void Field(StringBuilder sb, string key, string value) =>
            sb.Append("  \"").Append(key).Append("\": ").Append(JsonConvert.SerializeObject(value)).Append(",\n");

        static void Rows(StringBuilder sb, string key, float[][] rows)
        {
            sb.Append("      \"").Append(key).Append("\": [");
            for (int t = 0; t < rows.Length; t++)
            {
                sb.Append(t == 0 ? "\n        [" : ",\n        [");
                for (int c = 0; c < rows[t].Length; c++) { if (c > 0) sb.Append(", "); sb.Append(Num(rows[t][c], "0.###")); }
                sb.Append(']');
            }
            sb.Append(rows.Length == 0 ? "]" : "\n      ]");
        }

        static string Num(float v, string format) => v.ToString(format, CultureInfo.InvariantCulture);
    }
}
