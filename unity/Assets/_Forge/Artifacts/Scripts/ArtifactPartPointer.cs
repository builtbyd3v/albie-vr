using Oculus.Interaction;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// What "this" means on a model: the part the user is pointing at or looking at, so "show me this lobe" or "what's
    /// this?" works. Fifteen times a second it asks, in order:
    /// - a hand holding a part, or about to take one (<see cref="ArtifactHandGrab"/>);
    /// - an index finger pointed at the model (the other fingers curled), along the finger;
    /// - the Interaction SDK's hand or controller rays;
    /// - the mouse (DesktopRig, in the Editor);
    /// and, apart from those, what's in the middle of the view (the eyes' ray).
    /// What was pointed at is kept for <see cref="HoldSeconds"/>, so the voice round trip doesn't lose it; the part under
    /// a pointer is lit a little while it's there, so the user sees what "this" will mean. Rays meet the parts' picking
    /// colliders (<see cref="ArtifactExplorer.AddPickers"/>), which this adds a couple a frame once the model has loaded.
    /// No allocations per probe, apart from looking for the SDK's rays every few seconds while there are none.
    /// </summary>
    public sealed class ArtifactPartPointer : MonoBehaviour
    {
        public const float HoldSeconds = 2.5f;
        const float ProbeSeconds = 1f / 15f, HoverHoldSeconds = 0.3f, RayLength = 4f, RescanSeconds = 5f;

        public enum How { None, Held, Hand, Finger, Ray, Mouse, Gaze }

        ArtifactExplorer explorer;
        readonly RaycastHit[] hits = new RaycastHit[32];
        readonly HandSample[] own = { new HandSample { isLeft = true }, new HandSample() };
        IHandSource source;
        RayInteractor[] rays = new RayInteractor[0];
        ArtifactHandGrab hands;
        bool pickersReady;
        float probeAt, rescanAt, hoverSeen;
        Transform pointed, looked;
        float pointedTime = -100f, lookedTime = -100f;
        How pointedHow;

        public void Bind(ArtifactExplorer e)
        {
            explorer = e;
            e.Pointer = this;
        }

        void Update()
        {
            if (explorer == null) return;
            // The picking colliders, two pieces a frame while the model opens (cooking a mesh collider takes a moment).
            if (!pickersReady) { pickersReady = explorer.AddPickers(2); return; }
            float now = Time.unscaledTime;
            if (now < probeAt) return;
            probeAt = now + ProbeSeconds;
            Probe(now);
        }

        void Probe(float now)
        {
            How how = How.None;
            Transform hit = null;
            if (hands == null) hands = GetComponentInParent<ArtifactHandGrab>();
            if (hands != null && hands.isActiveAndEnabled)
            {
                if (hands.Core.HeldPart != null) { hit = hands.Core.HeldPart; how = How.Held; }
                else if (hands.Core.HoverPart != null) { hit = hands.Core.HoverPart; how = How.Hand; }
            }
            if (hit == null && (hit = Finger()) != null) how = How.Finger;
            if (hit == null && (hit = SdkRay(now)) != null) how = How.Ray;
            var rig = DesktopRig.Current;
            if (hit == null && rig != null && rig.isActiveAndEnabled && (hit = Cast(rig.PointerRay)) != null) how = How.Mouse;
            if (hit != null)
            {
                Note(hit, how, now);
                hoverSeen = now;
                explorer.SetHover(hit);
            }
            else if (now - hoverSeen > HoverHoldSeconds) explorer.SetHover(null);

            var cam = Camera.main;
            if (cam != null)
            {
                var seen = Cast(new Ray(cam.transform.position, cam.transform.forward));
                if (seen != null) Note(seen, How.Gaze, now);
            }
        }

        /// <summary>The part <paramref name="ray"/> is on (<see cref="ArtifactExplorer.PartOnRay"/>), or null. Checks call it with a made-up ray.</summary>
        public Transform Cast(Ray ray) => explorer != null ? explorer.PartOnRay(ray, RayLength, hits) : null;

        /// <summary>Remembers that <paramref name="part"/> was pointed at (or, with <see cref="How.Gaze"/>, looked at) at <paramref name="now"/>.</summary>
        public void Note(Transform part, How how, float now)
        {
            if (part == null) return;
            if (how == How.Gaze) { looked = part; lookedTime = now; return; }
            pointed = part;
            pointedTime = now;
            pointedHow = how;
        }

        /// <summary>The part pointed at in the last <see cref="HoldSeconds"/>, or null.</summary>
        public Transform PointedAt(float now, out How how)
        {
            how = pointed != null && now - pointedTime <= HoldSeconds ? pointedHow : How.None;
            return how != How.None ? pointed : null;
        }

        /// <summary>The part looked at in the last <see cref="HoldSeconds"/>, or null.</summary>
        public Transform LookedAt(float now) => looked != null && now - lookedTime <= HoldSeconds ? looked : null;

        /// <summary>What "this" is now: what was pointed at, else what was looked at. Null when neither.</summary>
        public Transform This(float now, out How how)
        {
            var p = PointedAt(now, out how);
            if (p != null) return p;
            var l = LookedAt(now);
            how = l != null ? How.Gaze : How.None;
            return l;
        }

        public Transform This(out How how) => This(Time.unscaledTime, out how);

        /// <summary>How it was picked, for Albie: "you're holding", "you're pointing at", "you're looking at".</summary>
        public static string Say(How how)
        {
            switch (how)
            {
                case How.Held: return "holding";
                case How.Gaze: return "looking at";
                case How.None: return null;
                default: return "pointing at";
            }
        }

        // An index finger held out at the model, the middle finger curled: along the finger from its knuckle, so a
        // fingertip touching a part counts too.
        Transform Finger()
        {
            HandSample left, right;
            var engine = GestureEngine.Current;
            if (engine != null)
            {
                left = engine.Hands[0].sample;
                right = engine.Hands[1].sample;
            }
            else
            {
                if (DesktopRig.Current != null) return null;
                if (source == null) source = new IsdkHandSource();
                source.Read(0, own[0]);
                source.Read(1, own[1]);
                left = own[0];
                right = own[1];
            }
            Transform part = Pointing(right);
            return part != null ? part : Pointing(left);
        }

        Transform Pointing(HandSample s)
        {
            if (s == null || !s.tracked) return null;
            var j = s.joints;
            float bones = (j[HandJoint.Index2] - j[HandJoint.Index1]).magnitude + (j[HandJoint.Index3] - j[HandJoint.Index2]).magnitude
                          + (j[HandJoint.IndexTip] - j[HandJoint.Index3]).magnitude;
            Vector3 finger = j[HandJoint.IndexTip] - j[HandJoint.Index1];
            if (bones < 1e-3f || finger.magnitude < bones * 0.9f) return null;  // the index isn't straight
            float knuckle = (j[HandJoint.Middle1] - j[HandJoint.Wrist]).magnitude;
            if ((j[HandJoint.MiddleTip] - j[HandJoint.Wrist]).magnitude > knuckle * 1.4f) return null;  // an open hand, not a point
            return Cast(new Ray(j[HandJoint.Index1], finger.normalized));
        }

        Transform SdkRay(float now)
        {
            if (now >= rescanAt)
            {
                rescanAt = now + RescanSeconds;
                if (rays.Length == 0 || rays[0] == null) rays = FindObjectsByType<RayInteractor>(FindObjectsSortMode.None);
            }
            for (int i = 0; i < rays.Length; i++)
            {
                var ray = rays[i];
                if (ray == null || !ray.isActiveAndEnabled || ray.State == InteractorState.Disabled) continue;
                var part = Cast(ray.Ray);
                if (part != null) return part;
            }
            return null;
        }
    }
}
