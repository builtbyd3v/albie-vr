using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// The live values behind an open artifact's <c>$name</c> references. Changing one raises <see cref="Changed"/> and
    /// the scene follows without a rebuild: this is "change things and watch what happens". As in C-19, a value outside
    /// [min, max] is refused, never clamped.
    /// </summary>
    public sealed class ArtifactInputs
    {
        sealed class Input
        {
            public double value, min = double.NegativeInfinity, max = double.PositiveInfinity;
            public bool whole;
            public string label;
        }

        readonly Dictionary<string, Input> inputs = new Dictionary<string, Input>();

        /// <summary>An input changed. Not raised per frame: only when a value really moves.</summary>
        public event Action<string> Changed;

        /// <param name="overrides">The scenario's <c>params</c>, which replace the defaults.</param>
        public ArtifactInputs(JObject manifest, JObject overrides = null)
        {
            if (!(manifest["inputs"] is JArray list)) return;
            foreach (var entry in list)
            {
                string type = entry.Value<string>("type");
                if (type != "number" && type != "integer") continue;
                string name = entry.Value<string>("name");
                var input = new Input
                {
                    value = entry.Value<double>("default"),
                    min = entry.Value<double>("min"),
                    max = entry.Value<double>("max"),
                    whole = type == "integer",
                    label = entry.Value<string>("label") ?? name,
                };
                var over = overrides?[name];
                if (over != null && over.Type != JTokenType.String) input.value = over.Value<double>();
                inputs[name] = input;
            }
        }

        public bool Has(string name) => inputs.ContainsKey(name);
        public double Value(string name) => inputs[name].value;
        public double Min(string name) => inputs[name].min;
        public double Max(string name) => inputs[name].max;
        public bool Whole(string name) => inputs[name].whole;
        public string Label(string name) => inputs[name].label;

        /// <summary>False, with nothing changed, if there's no such input or the value is out of range.</summary>
        public bool TrySet(string name, double value)
        {
            if (!inputs.TryGetValue(name, out var input) || value < input.min || value > input.max) return false;
            if (input.whole) value = Math.Round(value);
            if (input.value == value) return true;
            input.value = value;
            Changed?.Invoke(name);
            return true;
        }

        /// <summary>Whether a token is a <c>$name</c>, or a list holding one, so it has to follow input changes.</summary>
        public static bool IsBound(JToken token)
        {
            if (token == null) return false;
            if (token.Type == JTokenType.String) return ((string)token).StartsWith("$");
            if (token is JArray list) foreach (var item in list) if (IsBound(item)) return true;
            return false;
        }

        public float Number(JToken token, float fallback)
        {
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type == JTokenType.String)
            {
                string text = (string)token;
                return text.StartsWith("$") && inputs.TryGetValue(text.Substring(1), out var input) ? (float)input.value : fallback;
            }
            return token.Value<float>();
        }

        public Vector3 Vector(JToken token, Vector3 fallback)
        {
            if (!(token is JArray a) || a.Count < 3) return fallback;
            return new Vector3(Number(a[0], fallback.x), Number(a[1], fallback.y), Number(a[2], fallback.z));
        }
    }
}
