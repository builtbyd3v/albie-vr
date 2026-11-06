using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// The <c>timeline</c> node (C-12 3.2): a clock that <c>animate</c> rules follow. It plays for <c>duration_s</c>, then
    /// stops on the last frame or loops. Keys are linear between frames and hold before the first and after the last.
    /// The tracks are arrays built once, so a playing timeline doesn't allocate.
    /// </summary>
    public sealed class ArtifactTimeline : MonoBehaviour
    {
        public float Duration { get; private set; }
        public bool Loop { get; private set; }
        public float Time { get; private set; }
        public bool Playing { get; private set; }

        Track[] tracks = new Track[0];

        public static GameObject Build(JObject n, ArtifactBuild build)
        {
            var go = new GameObject(n.Value<string>("id"));
            var clock = go.AddComponent<ArtifactTimeline>();
            clock.Duration = Mathf.Max(0.01f, n.Value<float?>("duration_s") ?? 1f);
            clock.Loop = n.Value<bool?>("loop") == true;
            bool autoplay = n.Value<bool?>("autoplay") == true;
            string id = n.Value<string>("id");
            build.AfterBuild(() =>
            {
                clock.Bind(id, build);
                if (autoplay) clock.Play();
            });
            return go;
        }

        public void Play()
        {
            if (Time >= Duration && !Loop) Time = 0f;
            Playing = true;
        }

        public void Pause() => Playing = false;

        public void Restart()
        {
            Time = 0f;
            Playing = true;
            Evaluate();
        }

        /// <summary>Jumps to <paramref name="t"/> seconds, for checks and scrubbing.</summary>
        public void Seek(float t)
        {
            Time = Mathf.Clamp(t, 0f, Duration);
            Evaluate();
        }

        void Update() => Advance(UnityEngine.Time.deltaTime);

        public void Advance(float dt)
        {
            if (!Playing) return;
            Time += dt;
            if (Time >= Duration)
            {
                if (Loop) Time %= Duration;
                else
                {
                    Time = Duration;
                    Playing = false;
                }
            }
            Evaluate();
        }

        void Evaluate()
        {
            for (int i = 0; i < tracks.Length; i++) tracks[i].Apply(Time);
        }

        void Bind(string id, ArtifactBuild build)
        {
            if (build.Host != null && build.Host.Rules is JArray rules) Bind(rules, id, build.Nodes);
        }

        /// <summary>Follows the <c>animate</c> rules for timeline <paramref name="id"/> (already validated) on these nodes.</summary>
        public void Bind(JArray rules, string id, IReadOnlyDictionary<string, GameObject> nodes)
        {
            var list = new List<Track>();
            foreach (var rule in rules)
                if (rule.Value<string>("type") == "animate" && rule.Value<string>("timeline") == id
                    && nodes.TryGetValue(rule.Value<string>("target") ?? "", out var target))
                {
                    var track = Track.From(target, rule.Value<string>("property"), rule["keys"] as JArray);
                    if (track != null) list.Add(track);
                }
            tracks = list.ToArray();
            Evaluate();
        }

        sealed class Track
        {
            static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

            string property;
            Transform target;
            GameObject node;
            float[] times;
            Vector4[] values;
            Renderer[] renderers;
            TMP_Text text;
            MaterialPropertyBlock block;
            int last = -1;

            public static Track From(GameObject target, string property, JArray keys)
            {
                if (target == null || keys == null || keys.Count == 0) return null;
                var track = new Track { property = property, target = target.transform, node = target };
                track.times = new float[keys.Count];
                track.values = new Vector4[keys.Count];
                for (int i = 0; i < keys.Count; i++)
                {
                    track.times[i] = keys[i][0].Value<float>();
                    track.values[i] = Value(property, keys[i][1]);
                }
                if (property == "color")
                {
                    track.renderers = target.GetComponentsInChildren<Renderer>(true);
                    track.text = target.GetComponent<TMP_Text>();
                    track.block = new MaterialPropertyBlock();
                }
                return track;
            }

            static Vector4 Value(string property, JToken v)
            {
                switch (property)
                {
                    case "visible": return new Vector4(v.Value<bool>() ? 1f : 0f, 0f, 0f, 0f);
                    case "color": return ArtifactLook.Color(v.Value<string>(), Color.white);
                    case "scale" when v.Type == JTokenType.Integer || v.Type == JTokenType.Float:
                        float s = v.Value<float>();
                        return new Vector4(s, s, s, 0f);
                    default: return new Vector4(v[0].Value<float>(), v[1].Value<float>(), v[2].Value<float>(), 0f);
                }
            }

            public void Apply(float t)
            {
                if (property == "visible")
                {
                    int at = 0;
                    while (at + 1 < times.Length && times[at + 1] <= t) at++;
                    if (at == last) return;
                    last = at;
                    node.SetActive(values[at].x > 0.5f);
                    return;
                }
                var v = Sample(t);
                switch (property)
                {
                    case "pos": target.localPosition = v; break;
                    case "rot": target.localRotation = Quaternion.Euler(v); break;
                    case "scale": target.localScale = v; break;
                    case "color":
                        var c = new Color(v.x, v.y, v.z, v.w);
                        if (text != null) text.color = c;
                        block.SetColor(BaseColor, c);
                        for (int i = 0; i < renderers.Length; i++) renderers[i].SetPropertyBlock(block);
                        break;
                }
            }

            Vector4 Sample(float t)
            {
                if (t <= times[0]) return values[0];
                int n = times.Length - 1;
                if (t >= times[n]) return values[n];
                int i = 0;
                while (times[i + 1] < t) i++;
                float span = times[i + 1] - times[i];
                return Vector4.Lerp(values[i], values[i + 1], span > 0f ? (t - times[i]) / span : 1f);
            }
        }
    }
}
