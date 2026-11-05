using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Forge.Controllers
{
    /// <summary>A named number a person or Albie can turn in code mode (contract section 3.1).</summary>
    public struct Tunable
    {
        public float Value, Min, Max;
        public string Unit, Label;
    }

    /// <summary>
    /// A parsed <c>albie.controller/1</c> file (contract section 3). It holds the common fields and the raw JSON; the
    /// kind-specific parts are read by each controller's Bind. Errors are strings that start with a contract
    /// section 7 code, e.g. "tunable_out_of_range: max_speed 9 is above its max of 4", so the agent can fix them.
    /// </summary>
    public sealed class ControllerFile
    {
        public const string Schema = "albie.controller/1";

        public string Name { get; private set; }
        public string Kind { get; private set; }
        public int Version { get; private set; }
        public string Thing { get; private set; }
        public readonly Dictionary<string, Tunable> Tunables = new Dictionary<string, Tunable>();
        /// <summary>The speed governor in m/s (<c>limits.speed</c>), or +infinity when the file sets none.</summary>
        public float SpeedLimit { get; private set; } = float.PositiveInfinity;
        public JObject Raw { get; private set; }

        /// <summary>Parses and checks the common fields. Returns null and sets <paramref name="error"/> if invalid.</summary>
        public static ControllerFile Parse(string json, out string error)
        {
            JObject raw;
            try { raw = JObject.Parse(json); }
            catch (JsonException e) { error = "bad_value: not valid JSON (" + e.Message + ")"; return null; }

            error = null;
            if ((string)raw["schema"] != Schema) { error = $"bad_value: schema must be \"{Schema}\""; return null; }
            var file = new ControllerFile { Raw = raw, Name = (string)raw["name"], Kind = (string)raw["kind"], Thing = (string)raw["thing"] };
            if (string.IsNullOrEmpty(file.Name)) { error = "bad_value: name is missing"; return null; }
            if (file.Kind != "manual" && file.Kind != "graph" && file.Kind != "learned")
            {
                error = $"bad_value: kind \"{file.Kind}\" must be manual, graph or learned";
                return null;
            }
            file.Version = raw["version"]?.Type == JTokenType.Integer ? (int)raw["version"] : 1;

            if (raw["tunables"] is JObject tunables)
                foreach (var pair in tunables)
                {
                    if (!(pair.Value is JObject t) || !IsNumber(t["value"]))
                    {
                        error = $"bad_value: tunables.{pair.Key} needs a numeric value";
                        return null;
                    }
                    var tunable = new Tunable
                    {
                        Value = (float)t["value"],
                        Min = IsNumber(t["min"]) ? (float)t["min"] : float.NegativeInfinity,
                        Max = IsNumber(t["max"]) ? (float)t["max"] : float.PositiveInfinity,
                        Unit = (string)t["unit"],
                        Label = (string)t["label"],
                    };
                    if ((error = CheckRange(pair.Key, tunable.Value, tunable)) != null) return null;
                    file.Tunables[pair.Key] = tunable;
                }

            var limit = raw["limits"]?["speed"];
            if (limit != null && limit.Type != JTokenType.Null)
            {
                if (!file.TryNumber(limit, "limits.speed", out float speed, out error)) return null;
                if (speed <= 0) { error = "bad_value: limits.speed must be above 0 m/s"; return null; }
                file.SpeedLimit = speed;
            }
            return file;
        }

        /// <summary>A number, or a "$tunable" reference resolved to its current value.</summary>
        public bool TryNumber(JToken token, string where, out float value, out string error)
        {
            value = 0; error = null;
            if (IsNumber(token)) { value = (float)token; return true; }
            if (token?.Type == JTokenType.String && ((string)token).StartsWith("$", StringComparison.Ordinal))
            {
                string key = ((string)token).Substring(1);
                if (Tunables.TryGetValue(key, out var t)) { value = t.Value; return true; }
                error = $"bad_value: {where}: no tunable \"{key}\" in this file";
                return false;
            }
            error = $"bad_value: {where} must be a number or a $tunable";
            return false;
        }

        /// <summary>
        /// Sets a tunable (code mode, <c>controller_tune</c>). Out-of-range values are rejected, not clamped. The
        /// caller re-binds the controller so the new value takes effect.
        /// </summary>
        public string SetTunable(string key, float value)
        {
            if (!Tunables.TryGetValue(key, out var t)) return $"bad_value: no tunable \"{key}\" in {Name}";
            string error = CheckRange(key, value, t);
            if (error != null) return error;
            t.Value = value;
            Tunables[key] = t;
            ((JObject)Raw["tunables"][key])["value"] = value;
            var limit = Raw["limits"]?["speed"];
            if (limit != null && TryNumber(limit, "limits.speed", out float speed, out _)) SpeedLimit = speed;
            return null;
        }

        static string CheckRange(string key, float value, Tunable t)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return $"bad_value: tunables.{key} is not a finite number";
            if (value < t.Min) return $"tunable_out_of_range: {key} {Fmt(value)} is below its min of {Fmt(t.Min)}";
            if (value > t.Max) return $"tunable_out_of_range: {key} {Fmt(value)} is above its max of {Fmt(t.Max)}";
            return null;
        }

        public static bool IsNumber(JToken token) =>
            token != null && (token.Type == JTokenType.Float || token.Type == JTokenType.Integer);

        public static string Fmt(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
