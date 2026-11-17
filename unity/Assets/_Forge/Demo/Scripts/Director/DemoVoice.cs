using System;
using Forge.Pet;
using UnityEngine;

namespace Forge.Demo
{
    /// <summary>
    /// Plays one of Albie's scripted lines: the recorded clip from Resources/DemoAudio (3D, from his head, at the same
    /// cartoon pitch the live voice uses), his speech bubble (a second AlbieCaption of its own, so the live agent's bubble
    /// can be muted while the Director runs), and his mouth (jaw, nod and squash, the way AlbieTalkAnimator does it, driven
    /// by the loudness of the clip). A line with no clip waits 0.06 s per character with a synthetic mouth. No allocations
    /// per frame.
    /// </summary>
    public sealed class DemoVoice : MonoBehaviour
    {
        const float BubbleRefreshSeconds = 4f;   // AlbieCaption hides final text after 6 s: a long line re-shows it
        const float BubbleLingerSeconds = 1.1f;
        const float LevelGain = 4f;
        const float Attack = 0.03f, Release = 0.12f;
        const float NodDegrees = 9f, JawDegrees = 20f, PulseAmount = 0.05f;

        public bool Speaking { get; private set; }
        /// <summary>Time.time when the last line ended (0 before any).</summary>
        public float EndedAt { get; private set; }
        public float Level => env;
        public string LastText { get; private set; } = "";
        public float VoicePitch = 1.2f;
        public float SecondsPerChar = 0.06f;
        public Action Ended;

        AudioSource source;
        AlbieCaption bubble;
        AlbieExpressions expressions;
        Transform head, jaw, pulseRoot;
        Quaternion jawRest = Quaternion.identity;
        Vector3 baseScale = Vector3.one;
        bool scaled, jawMoved, bubbleUp;
        readonly float[] samples = new float[512];
        float env, startedAt, duration, nextRefresh, bubbleClearAt;
        string text = "";
        bool hasClip;
        string warnedMissing;

        /// <summary>Builds the voice under <paramref name="albie"/> (audio source, bubble, bone lookups).</summary>
        public static DemoVoice Create(Transform albie)
        {
            var go = new GameObject("DemoVoice");
            go.transform.SetParent(albie, false);
            go.transform.localPosition = new Vector3(0f, 0.3f, 0f);
            var voice = go.AddComponent<DemoVoice>();
            voice.Build(albie);
            return voice;
        }

        void Build(Transform albie)
        {
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 1f;
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 1f;
            source.maxDistance = 12f;

            var bubbleGo = new GameObject("DemoCaption");
            bubbleGo.transform.SetParent(albie, false);
            bubble = bubbleGo.AddComponent<AlbieCaption>();

            expressions = albie.GetComponentInChildren<AlbieExpressions>(true);
            if (expressions == null) expressions = albie.GetComponentInParent<AlbieExpressions>();
            Animator animator = albie.GetComponentInChildren<Animator>(true);
            if (animator != null)
            {
                pulseRoot = animator.transform;
                baseScale = pulseRoot.localScale;
                head = FindChild(animator.transform, "head");
                jaw = FindChild(animator.transform, "jaw");
                if (jaw != null) jawRest = jaw.localRotation;
            }
        }

        public void Say(string line, string clipName)
        {
            Stop(false);
            text = line ?? "";
            LastText = text;
            AudioClip clip = string.IsNullOrEmpty(clipName) ? null : Resources.Load<AudioClip>("DemoAudio/" + clipName);
            hasClip = clip != null;
            float pitch = Mathf.Clamp(VoicePitch, 0.75f, 1.6f);
            if (hasClip)
            {
                source.clip = clip;
                source.pitch = pitch;
                source.Play();
                duration = clip.length / pitch;
            }
            else
            {
                if (!string.IsNullOrEmpty(clipName) && warnedMissing != clipName)
                {
                    warnedMissing = clipName;
                    Debug.LogWarning($"[Demo] audio clip '{clipName}' is missing: caption only (Resources/DemoAudio/{clipName}.wav).");
                }
                duration = Mathf.Max(0.8f, text.Length * SecondsPerChar);
            }
            Speaking = true;
            startedAt = Time.time;
            nextRefresh = Time.time + BubbleRefreshSeconds;
            bubbleClearAt = -1f;
            ShowBubble();
            if (expressions != null) expressions.SetMood(Mood.Happy, 0.15f);
        }

        public void Stop(bool fireEnded = true)
        {
            if (source != null && source.isPlaying) source.Stop();
            if (!Speaking) return;
            Speaking = false;
            EndedAt = Time.time;
            bubbleClearAt = Time.time + (fireEnded ? BubbleLingerSeconds : 0f);
            if (fireEnded)
            {
                if (expressions != null) expressions.SetMood(Mood.Neutral, 0.4f);
                Ended?.Invoke();
            }
        }

        /// <summary>Drops the bubble and the mouth at once (reset).</summary>
        public void Silence()
        {
            Stop(false);
            if (bubble != null) bubble.Clear();
            bubbleClearAt = -1f;
            env = 0f;
            Restore();
        }

        void ShowBubble()
        {
            if (bubble == null) return;
            bubble.SetText(text, false);
            bubbleUp = true;
        }

        void Update()
        {
            if (Speaking)
            {
                float elapsed = Time.time - startedAt;
                bool over = hasClip ? (!source.isPlaying && elapsed > 0.25f) || elapsed > duration + 0.5f : elapsed >= duration;
                if (over)
                {
                    Stop(true);
                }
                else if (Time.time >= nextRefresh)
                {
                    nextRefresh = Time.time + BubbleRefreshSeconds;
                    ShowBubble();
                }
            }
            if (!Speaking && bubbleUp && bubbleClearAt > 0f && Time.time >= bubbleClearAt)
            {
                bubbleUp = false;
                bubbleClearAt = -1f;
                if (bubble != null) bubble.Clear();
            }
        }

        void LateUpdate()
        {
            float target = 0f;
            if (Speaking)
            {
                if (hasClip && source.isPlaying)
                {
                    source.GetOutputData(samples, 0);
                    double sum = 0.0;
                    for (int i = 0; i < samples.Length; i++) sum += samples[i] * samples[i];
                    target = Mathf.Clamp01(Mathf.Sqrt((float)(sum / samples.Length)) * LevelGain);
                }
                else target = SyntheticLevel(Time.time - startedAt);
            }
            float tau = Mathf.Max(0.001f, target > env ? Attack : Release);
            env = Mathf.Lerp(env, target, 1f - Mathf.Exp(-Time.deltaTime / tau));

            if (env < 0.001f)
            {
                Restore();
                return;
            }
            if (head != null) head.localRotation = head.localRotation * Quaternion.AngleAxis(env * NodDegrees, Vector3.right);
            if (jaw != null) { jaw.localRotation = jawRest * Quaternion.AngleAxis(env * JawDegrees, Vector3.right); jawMoved = true; }
            if (pulseRoot != null)
            {
                float k = env * PulseAmount;
                pulseRoot.localScale = new Vector3(baseScale.x * (1f - 0.5f * k), baseScale.y * (1f + k), baseScale.z * (1f - 0.5f * k));
                scaled = true;
            }
        }

        void Restore()
        {
            if (scaled && pulseRoot != null) { pulseRoot.localScale = baseScale; scaled = false; }
            if (jawMoved && jaw != null) { jaw.localRotation = jawRest; jawMoved = false; }
        }

        void OnDisable() => Restore();

        static float SyntheticLevel(float t) =>
            Mathf.Abs(Mathf.Sin(t * Mathf.PI * 3.1f)) * (0.65f + 0.35f * Mathf.Sin(t * Mathf.PI * 0.7f));

        static Transform FindChild(Transform root, string childName)
        {
            if (root.name == childName) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform hit = FindChild(root.GetChild(i), childName);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
