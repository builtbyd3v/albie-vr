using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// Hands-first handling for an artifact's model, the way you'd handle a real object:
    /// - Pinch on (or right next to) the model to pick it up. It follows your hand; turning your wrist turns it.
    /// - Pinch with your other hand too: pull your hands apart to make it bigger, together to make it smaller, and
    ///   turn them like a steering wheel to spin it.
    /// - Once the model is separated, pinch one part to take it out and turn it in your hand. Assemble or Reset view
    ///   brings it home.
    /// Hand tracking jitters, so the pinch point and hand direction are smoothed: heavily while the hand is still,
    /// lightly while it moves fast, which keeps it steady without feeling laggy. The pinch has hysteresis (closes at
    /// 24% of the hand's size, opens at 40%) and survives a quarter second of lost tracking, so it doesn't drop.
    /// While a hand is near or holding the model, the volume's ray grips (<see cref="VolumeManipulator"/>, for
    /// pointing from afar) stand aside, so the two never fight over it.
    /// The logic is <see cref="HandGrabCore"/>, which checks drive with synthetic hands; this component feeds it the
    /// real ones (from the gesture engine, else the Interaction SDK rig) and shows what it's doing.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ForgeVolume))]
    public sealed class ArtifactHandGrab : MonoBehaviour, HandGrabCore.ITarget
    {
        const float RescanSeconds = 0.5f;

        readonly HandGrabCore core = new HandGrabCore();
        readonly HandSample[] own = { new HandSample { isLeft = true }, new HandSample() };
        readonly List<Renderer> scratch = new List<Renderer>();
        readonly List<Renderer> meshes = new List<Renderer>();
        ForgeVolume volumeField;
        VolumeManipulator grips;
        ArtifactExplorer explorer;
        IHandSource source;
        float rescanAt;
        bool ownsBusy, homeSaved;
        Vector3 homePosition;
        float homeScale, homeYaw, homeTilt;
        HandGrabCore.Mode shownMode = HandGrabCore.Mode.None;
        bool shownHover;
        Transform shownPart;

        /// <summary>What the hands are doing with this model now.</summary>
        public HandGrabCore Core => core;

        // Looked up when first needed: a component added outside Play mode gets no Awake.
        ForgeVolume volume => volumeField != null ? volumeField : volumeField = GetComponent<ForgeVolume>();

        void OnDisable()
        {
            core.Release(this);
            StandGripsDown(false);
        }

        void Update()
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
                // No gesture engine (a sandbox): read the headset's hands directly. The desktop rig has no hands to read.
                if (source == null)
                {
                    if (DesktopRig.Current != null) return;
                    source = new IsdkHandSource();
                }
                source.Read(0, own[0]);
                source.Read(1, own[1]);
                left = own[0];
                right = own[1];
            }
            if (Time.unscaledTime >= rescanAt) Rescan();
            core.Step(left, right, Time.deltaTime, this);
            Show();
        }

        /// <summary>Puts the model back where it stood before hands first moved it (Reset view).</summary>
        public void ResetPose()
        {
            core.Release(this);
            if (!homeSaved) return;
            transform.position = homePosition;
            transform.localScale = Vector3.one * homeScale;
            volume.SetView(homeYaw, homeTilt);
            homeSaved = false;
        }

        // ---------------------------------------------------------------- HandGrabCore.ITarget
        public Vector3 Position
        {
            get => transform.position;
            set => transform.position = value;
        }

        public float Scale
        {
            get => transform.localScale.x;
            set => transform.localScale = Vector3.one * value;
        }

        public float Yaw => volume.ViewYaw;
        public float Tilt => volume.ViewTilt;
        public void SetView(float yaw, float tilt) => volume.SetView(yaw, tilt);

        public bool Near(Vector3 world, float margin)
        {
            bool any = false;
            Bounds b = default;
            for (int i = 0; i < meshes.Count; i++)
            {
                var r = meshes[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            if (!any) return false;
            b.Expand(margin * 2f);
            return b.Contains(world);
        }

        public Transform PartAt(Vector3 world, float margin) => explorer != null ? explorer.PartAt(world, margin) : null;

        public void Grabbed(HandGrabCore.Mode mode, Transform part)
        {
            if (mode == HandGrabCore.Mode.None) return;
            if (!homeSaved && mode != HandGrabCore.Mode.Part)
            {
                homeSaved = true;
                homePosition = transform.position;
                homeScale = transform.localScale.x;
                homeYaw = volume.ViewYaw;
                homeTilt = volume.ViewTilt;
            }
            if (part != null && explorer != null) explorer.MarkMoved(part);
        }

        // ---------------------------------------------------------------- upkeep and feedback
        // The model's own renderers (not its labels or leader lines), and the explorer once its glTF has loaded.
        void Rescan()
        {
            rescanAt = Time.unscaledTime + RescanSeconds;
            if (grips == null) grips = GetComponent<VolumeManipulator>();
            var root = volume.ContentRoot;
            if (root == null) return;
            if (explorer == null) explorer = root.GetComponentInChildren<ArtifactExplorer>();
            root.GetComponentsInChildren(false, scratch);
            meshes.Clear();
            for (int i = 0; i < scratch.Count; i++)
            {
                var r = scratch[i];
                if (r is LineRenderer || r.TryGetComponent<TMP_Text>(out _)) continue;
                meshes.Add(r);
            }
            scratch.Clear();
        }

        void Show()
        {
            bool near = core.Hovering || core.Grab != HandGrabCore.Mode.None;
            StandGripsDown(near);
            if (core.Grab == shownMode && core.Hovering == shownHover && core.HoverPart == shownPart) return;
            shownMode = core.Grab;
            shownHover = core.Hovering;
            shownPart = core.HoverPart;
            volume.SetHighlight(near);
            volume.SetHint(Hint());
        }

        string Hint()
        {
            switch (core.Grab)
            {
                case HandGrabCore.Mode.One: return "Turn your wrist to turn it  ·  Pinch with your other hand to resize";
                case HandGrabCore.Mode.Two: return "Pull apart to grow  ·  Turn your hands to spin it";
                case HandGrabCore.Mode.Part: return "Holding the " + ArtifactExplorer.NameOf(core.HeldPart) + "  ·  Assemble puts it back";
            }
            if (!core.Hovering) return null;
            return core.HoverPart != null ? "Pinch to take the " + ArtifactExplorer.NameOf(core.HoverPart)
                : "Pinch to pick it up  ·  Both hands to resize";
        }

        // The ray grips step aside while hands are on the model, and only if this component was the one that set them aside.
        void StandGripsDown(bool down)
        {
            if (grips == null) return;
            if (down && !ownsBusy && !grips.Busy)
            {
                grips.Cancel();
                grips.Busy = true;
                ownsBusy = true;
            }
            else if (!down && ownsBusy)
            {
                grips.Busy = false;
                ownsBusy = false;
            }
        }
    }

    /// <summary>
    /// What two hands do to one model, frame by frame, with no Unity objects of its own: see <see cref="ArtifactHandGrab"/>.
    /// <see cref="Step"/> takes both hands' joints (world space) and moves the <see cref="ITarget"/>. No allocations per step.
    /// </summary>
    public sealed class HandGrabCore
    {
        public enum Mode { None, One, Two, Part }

        /// <summary>The model the hands act on.</summary>
        public interface ITarget
        {
            Vector3 Position { get; set; }
            float Scale { get; set; }
            float Yaw { get; }
            float Tilt { get; }
            void SetView(float yaw, float tilt);
            /// <summary>Whether a pinch here is on the model (within <paramref name="margin"/> metres of it).</summary>
            bool Near(Vector3 world, float margin);
            /// <summary>The separated part a pinch here takes, or null.</summary>
            Transform PartAt(Vector3 world, float margin);
            /// <summary>A grab started (<paramref name="part"/> set for a part).</summary>
            void Grabbed(Mode mode, Transform part);
        }

        public const float PinchStart = 0.24f, PinchEnd = 0.40f;  // thumb to index tip, over the hand's size
        public const float NearMargin = 0.06f;      // m around the model where a pinch still picks it up
        public const float SecondHandMargin = 0.12f; // the second hand of a two-handed grab can be a little further out
        public const float PartMargin = 0.015f;
        public const float LostGrace = 0.25f;       // s of lost tracking a pinch survives
        public const float MinScale = 0.4f, MaxScale = 3f;
        public const float YawGain = 1.5f, TiltGain = 1.2f, MaxTilt = 80f;
        const float StillTau = 0.09f, FastTau = 0.02f, FastSpeed = 0.6f;  // smoothing: time constants (s), speed (m/s)

        sealed class Hand
        {
            public bool pinching, wasPinching, hasPose;
            public float lost;
            public Vector3 pinch, forward, up;
        }

        readonly Hand[] hands = { new Hand(), new Hand() };
        Vector3 pos0, pinch0, mid0, partPos0;
        float scale0, yaw0, tilt0, handYaw0, handPitch0, dist0, angle0;
        Quaternion handRot0, partRot0;

        public Mode Grab { get; private set; }
        /// <summary>The hand holding it (0 left, 1 right) in One or Part.</summary>
        public int Holder { get; private set; }
        public Transform HeldPart { get; private set; }
        /// <summary>A free hand is near enough to pick the model up.</summary>
        public bool Hovering { get; private set; }
        /// <summary>The part a free hand would take, if the model is separated.</summary>
        public Transform HoverPart { get; private set; }
        public bool Pinching(int hand) => hands[hand].pinching;
        public Vector3 PinchPoint(int hand) => hands[hand].pinch;

        public void Step(HandSample left, HandSample right, float dt, ITarget target)
        {
            Read(hands[0], left, dt);
            Read(hands[1], right, dt);
            bool began0 = hands[0].pinching && !hands[0].wasPinching, began1 = hands[1].pinching && !hands[1].wasPinching;
            bool ended0 = !hands[0].pinching && hands[0].wasPinching, ended1 = !hands[1].pinching && hands[1].wasPinching;

            switch (Grab)
            {
                case Mode.None:
                    if (began0) TryStart(0, target);
                    if (Grab == Mode.None && began1) TryStart(1, target);
                    break;
                case Mode.One:
                {
                    int other = 1 - Holder;
                    bool holderEnded = Holder == 0 ? ended0 : ended1;
                    bool otherBegan = other == 0 ? began0 : began1;
                    if (holderEnded) Grab = Mode.None;
                    else if (otherBegan && target.Near(hands[other].pinch, SecondHandMargin)) StartTwo(target);
                    break;
                }
                case Mode.Two:
                    if (ended0 || ended1)
                    {
                        int stays = ended0 ? 1 : 0;
                        if (hands[stays].pinching) StartOne(stays, target);  // carry on with the hand that's still holding
                        else Grab = Mode.None;
                    }
                    break;
                case Mode.Part:
                    if (Holder == 0 ? ended0 : ended1) { Grab = Mode.None; HeldPart = null; }
                    break;
            }

            switch (Grab)
            {
                case Mode.One: HoldOne(target); break;
                case Mode.Two: HoldTwo(target); break;
                case Mode.Part: HoldPart(); break;
            }
            Hover(target);
        }

        /// <summary>Lets go of whatever is held (the component turns off, Reset view).</summary>
        public void Release(ITarget target)
        {
            Grab = Mode.None;
            HeldPart = null;
        }

        // ---------------------------------------------------------------- hands
        static void Read(Hand h, HandSample s, float dt)
        {
            h.wasPinching = h.pinching;
            if (s == null || !s.tracked)
            {
                h.lost += dt;
                if (h.lost > LostGrace) { h.pinching = false; h.hasPose = false; }
                return;
            }
            h.lost = 0f;
            var j = s.joints;
            float size = (j[HandJoint.Middle1] - j[HandJoint.Wrist]).magnitude;
            if (size < 0.04f) size = 0.09f;
            float gap = (j[HandJoint.ThumbTip] - j[HandJoint.IndexTip]).magnitude / size;
            h.pinching = h.pinching ? gap < PinchEnd : gap < PinchStart;

            Vector3 pinch = (j[HandJoint.ThumbTip] + j[HandJoint.IndexTip]) * 0.5f;
            Vector3 forward = j[HandJoint.Middle1] - j[HandJoint.Wrist];
            forward = forward.sqrMagnitude > 1e-8f ? forward.normalized : Vector3.forward;
            Vector3 up = HandFeatures.PalmNormal(s);
            up = Vector3.ProjectOnPlane(up, forward);
            up = up.sqrMagnitude > 1e-8f ? up.normalized : Vector3.up;
            if (!h.hasPose || dt <= 0f)
            {
                h.pinch = pinch; h.forward = forward; h.up = up; h.hasPose = true;
                return;
            }
            // Adaptive smoothing: still hands are steadied hard, fast ones barely, so it neither shakes nor lags.
            float speed = (pinch - h.pinch).magnitude / dt;
            float tau = Mathf.Lerp(StillTau, FastTau, Mathf.Clamp01(speed / FastSpeed));
            float k = 1f - Mathf.Exp(-dt / tau);
            h.pinch = Vector3.Lerp(h.pinch, pinch, k);
            h.forward = Vector3.Slerp(h.forward, forward, k).normalized;
            h.up = Vector3.Slerp(h.up, up, k).normalized;
        }

        static void Angles(Vector3 f, out float yaw, out float pitch)
        {
            yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            pitch = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        // ---------------------------------------------------------------- grabs
        void TryStart(int hand, ITarget target)
        {
            Vector3 p = hands[hand].pinch;
            Transform part = target.PartAt(p, PartMargin);
            if (part != null)
            {
                Grab = Mode.Part;
                Holder = hand;
                HeldPart = part;
                partPos0 = part.position;
                partRot0 = part.rotation;
                pinch0 = p;
                handRot0 = Quaternion.LookRotation(hands[hand].forward, hands[hand].up);
                target.Grabbed(Mode.Part, part);
                return;
            }
            if (target.Near(p, NearMargin)) StartOne(hand, target);
        }

        void StartOne(int hand, ITarget target)
        {
            Grab = Mode.One;
            Holder = hand;
            HeldPart = null;
            pos0 = target.Position;
            pinch0 = hands[hand].pinch;
            yaw0 = target.Yaw;
            tilt0 = target.Tilt;
            Angles(hands[hand].forward, out handYaw0, out handPitch0);
            target.Grabbed(Mode.One, null);
        }

        void StartTwo(ITarget target)
        {
            Grab = Mode.Two;
            HeldPart = null;
            Vector3 a = hands[0].pinch, b = hands[1].pinch;
            pos0 = target.Position;
            mid0 = (a + b) * 0.5f;
            scale0 = target.Scale;
            yaw0 = target.Yaw;
            tilt0 = target.Tilt;
            dist0 = Mathf.Max((a - b).magnitude, 0.02f);
            angle0 = Flat(b - a);
            target.Grabbed(Mode.Two, null);
        }

        static float Flat(Vector3 v) => Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;

        void HoldOne(ITarget target)
        {
            var h = hands[Holder];
            target.Position = pos0 + (h.pinch - pinch0);
            Angles(h.forward, out float yaw, out float pitch);
            float newYaw = yaw0 + Mathf.DeltaAngle(handYaw0, yaw) * YawGain;
            float newTilt = Mathf.Clamp(tilt0 + Mathf.DeltaAngle(handPitch0, pitch) * TiltGain, -MaxTilt, MaxTilt);
            target.SetView(newYaw, newTilt);
        }

        void HoldTwo(ITarget target)
        {
            Vector3 a = hands[0].pinch, b = hands[1].pinch;
            float d = Mathf.Max((a - b).magnitude, 0.02f);
            target.Scale = Mathf.Clamp(scale0 * d / dist0, MinScale, MaxScale);
            target.Position = pos0 + ((a + b) * 0.5f - mid0);
            target.SetView(yaw0 + Mathf.DeltaAngle(angle0, Flat(b - a)), tilt0);
        }

        void HoldPart()
        {
            if (HeldPart == null) { Grab = Mode.None; return; }
            var h = hands[Holder];
            Quaternion turn = Quaternion.LookRotation(h.forward, h.up) * Quaternion.Inverse(handRot0);
            HeldPart.SetPositionAndRotation(h.pinch + turn * (partPos0 - pinch0), turn * partRot0);
        }

        void Hover(ITarget target)
        {
            Hovering = false;
            HoverPart = null;
            if (Grab != Mode.None) return;
            for (int i = 0; i < 2; i++)
            {
                var h = hands[i];
                if (!h.hasPose || h.pinching) continue;
                Transform part = target.PartAt(h.pinch, PartMargin);
                if (part != null) { Hovering = true; HoverPart = part; return; }
                if (target.Near(h.pinch, NearMargin)) Hovering = true;
            }
        }
    }
}
