using System;
using UnityEngine;

namespace Forge.Pet
{
    /// <summary>
    /// GatorController riding on something that moves, such as the user's palm (Core's GesturePerch owns the transform,
    /// so this part takes no Meta types). He jumps to it in an arc with the agent off, shrinks to fit, sits and follows
    /// it, then hops back where he came from. While perched, nothing else can move him: AgentReady says no, as do hops
    /// and rolls.
    /// </summary>
    public partial class GatorController
    {
        [Header("Perching on the user's hand")]
        [SerializeField, Tooltip("Longest side (m) while on the palm. He shrinks to this if he's bigger.")]
        float perchLength = 0.15f;
        [SerializeField, Tooltip("Shortest jump to the palm (s). Further jumps take a little longer.")]
        float perchJumpSeconds = 0.65f;
        [SerializeField, Tooltip("How long (s) he takes to shrink or grow.")]
        float perchScaleSeconds = 0.35f;

        enum PerchPhase { None, Jumping, Sitting }

        PerchPhase perchPhase;
        Transform perchAnchor;
        Vector3 perchFrom, perchRestScale, perchHomePoint, perchScaleTarget;
        GatorNavSurface perchHomeSurface;
        float perchStart, perchDuration, perchApex, nextPerchSit;
        bool perchScaling;

        /// <summary>On (or jumping to) a perch.</summary>
        public bool Perched => perchPhase != PerchPhase.None;
        /// <summary>He has landed on the perch.</summary>
        public event Action PerchArrived;

        /// <summary>Jump onto <paramref name="anchor"/> and stay on it. False if he's busy (mid-hop, rolling, or holding a greeting spot).</summary>
        public bool PerchOn(Transform anchor)
        {
            if (Perched || hopping || AnticsBusy || stationed || anchor == null || agent == null) return false;
            StandUp(false);
            if (OnMesh) StopMoving();
            tripActive = approachingUser = false;
            faceTarget = null;
            faceUser = true;
            PlayEmote("Jump");
            agent.enabled = false;

            perchHomeSurface = CurrentSurface;
            perchHomePoint = transform.position;
            if (!perchScaling) perchRestScale = transform.localScale;
            perchScaleTarget = perchRestScale * PerchFit();
            perchScaling = true;

            perchAnchor = anchor;
            perchFrom = transform.position;
            float distance = Vector3.Distance(perchFrom, anchor.position);
            perchDuration = perchJumpSeconds + Mathf.Min(0.5f, distance * 0.2f);
            perchApex = 0.12f + distance * 0.2f;
            perchStart = Time.time;
            perchPhase = PerchPhase.Jumping;
            return true;
        }

        /// <summary>Hop back to where he jumped from, and on with his day. <paramref name="landed"/> runs when he lands.</summary>
        public void LeavePerch(Action landed = null)
        {
            if (!Perched) return;
            perchPhase = PerchPhase.None;
            perchAnchor = null;
            perchScaleTarget = perchRestScale;
            perchScaling = true;
            Vector3 landing = perchHomeSurface != null && perchHomeSurface.IsBuilt ? KeepOutSafePoint(perchHomeSurface, perchHomePoint) : perchHomePoint;
            faceUser = false;
            HopTo(perchHomeSurface, landing, landed);
            SetMood(Mood.Neutral);
        }

        // How much to scale him so his longest side is perchLength. Never bigger than he is.
        float PerchFit()
        {
            // His body only: the caption, name tag and cards are renderers under him too, and much bigger.
            Renderer[] renderers = GetComponentsInChildren<SkinnedMeshRenderer>();
            if (renderers.Length == 0) return 1f;
            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            float length = Mathf.Max(b.size.x, b.size.z);
            return length > perchLength ? perchLength / length : 1f;
        }

        // Runs whether or not he's perched: he is still growing back during the hop home.
        void UpdatePerchScale()
        {
            if (!perchScaling) return;
            float span = Mathf.Max(Mathf.Abs(perchRestScale.x - perchScaleTarget.x), perchRestScale.x * 0.5f);
            transform.localScale = Vector3.MoveTowards(transform.localScale, perchScaleTarget, span * Time.deltaTime / Mathf.Max(0.05f, perchScaleSeconds));
            if (!Perched && transform.localScale == perchScaleTarget) perchScaling = false;
        }

        // True while he's on a perch: the rest of Update stays out of it.
        bool UpdatePerch()
        {
            UpdatePerchScale();
            if (!Perched) return false;
            if (perchAnchor == null)
            {
                LeavePerch();
                return false;
            }

            if (perchPhase == PerchPhase.Jumping)
            {
                float t = Mathf.Clamp01((Time.time - perchStart) / perchDuration);
                Vector3 p = Vector3.Lerp(perchFrom, perchAnchor.position, t);
                p.y += perchApex * 4f * t * (1f - t);
                transform.position = p;
                if (t < 1f) return true;
                perchPhase = PerchPhase.Sitting;
                SetMood(Mood.Attentive);
                nextPerchSit = Time.time;
                PerchArrived?.Invoke();
                return true;
            }

            transform.position = perchAnchor.position;
            faceTarget = null;
            faceUser = true;
            UpdateRest();
            // A state change (he heard you, he answered) stands him up: sit back down in a moment.
            if (restPose == RestPose.Idle && Time.time >= nextPerchSit)
            {
                nextPerchSit = Time.time + 1.5f;
                restUntil = Time.time + 3600f;
                restPose = RestPose.Sit;
                restApplied = false;
            }
            return true;
        }
    }
}
