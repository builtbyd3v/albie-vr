using UnityEngine;

namespace Forge.Controllers
{
    // The hands: who holds the transmitter, where it goes, and what each fingertip does to the sticks and buttons.
    public sealed partial class RcTransmitter
    {
        // ---- Tuning ----------------------------------------------------------------------------------------------------

        /// <summary>Palm centre within this of the body (m) can close round it.</summary>
        public const float GripReach = 0.05f;
        /// <summary>Middle, ring and little finger bend (0 straight, 1 fist) that closes a grip, and below which it opens.</summary>
        public const float GripClose = 0.5f, GripOpen = 0.3f;
        /// <summary>The index finger's bend a grip also needs to close (keeping it doesn't).</summary>
        public const float GripIndexClose = 0.35f;
        /// <summary>A pinch (thumb to index, hand lengths) within this of the body (m) picks it up too.</summary>
        public const float PinchReach = 0.02f;
        /// <summary>A holding hand that loses tracking keeps holding (frozen) this long (s).</summary>
        public const float LostGraceSeconds = 0.5f;
        /// <summary>Time constant of the transmitter following the hands (s): smooths tracking jitter.</summary>
        public const float PoseSmoothing = 0.025f;
        /// <summary>While held, the drawn pose follows the hands this slowly (s): steadier, and the sticks are read in each
        /// hand's own frame (unsmoothed), so the smoothing never reads as a thumb moving.</summary>
        public const float HeldPoseSmoothing = 0.07f;
        /// <summary>A hand holding it that loses tracking keeps holding (frozen where it was) this long (s).</summary>
        public const float HeldLostGraceSeconds = 0.8f;

        // ---- Docked (the default): it waits on a fixed spot and the fingertips work the sticks -------------------------

        /// <summary>A thumb or index tip takes a docked stick within this of the cap across the face (m), between
        /// <see cref="DockEngageBelow"/> under and <see cref="DockEngageAbove"/> over the cap's top.</summary>
        public const float DockCaptureRadius = 0.04f, DockEngageAbove = 0.03f, DockEngageBelow = 0.025f;
        /// <summary>It lets go when the tip lifts this far above the cap's top, or slides this far from where it touched (m).</summary>
        public const float DockReleaseHeight = 0.05f, DockReleaseRadius = 0.065f;
        /// <summary>Tip travel from where it touched for a full stick (m), the dead zone (a fraction of it) and the expo.</summary>
        public const float DockTravel = 0.02f, DockDeadzone = 0.15f, DockExpo = 0.25f;
        /// <summary>A tip lost to tracking holds its stick this long (s), then the stick centres.</summary>
        public const float DockLostGrace = 0.35f;
        /// <summary>A hand that closes round the docked body and moves it this far (m) picks it up (handheld).</summary>
        public const float LiftToHold = 0.07f;
        /// <summary>A pinch on the body this far (m, across the face) from every stick and button drags the dock.</summary>
        public const float DragClear = 0.022f;
        /// <summary>Button: the cap's radius, how far a fingertip goes in to press it, and where it lets go (m).</summary>
        public const float ButtonRadius = 0.011f, ButtonPressDepth = 0.003f, ButtonReleaseDepth = 0.0012f;
        /// <summary>A fingertip more than this under a cap's rest top (m) is ignored: it is behind the face, not pressing.</summary>
        public const float MaxPressDepth = 0.016f;

        // ---- Thumb lock: a holding hand's thumb is bound to its stick the moment it grips -------------------------------

        /// <summary>Thumb movement from where it rested at the grip for a full stick (m): a comfortable thumb arc.</summary>
        public const float ThumbTravel = 0.012f;
        /// <summary>Radial dead zone (fraction of <see cref="ThumbTravel"/>) and expo for the locked thumb.</summary>
        public const float ThumbDeadzone = 0.2f, ThumbExpo = 0.3f;
        /// <summary>A locked thumb lets go (the stick centres) when lifted this far above where it rested (m)...</summary>
        public const float ThumbLiftRelease = 0.025f;
        /// <summary>...or slid this far from it across the face (m), as onto a button.</summary>
        public const float ThumbSlideRelease = 0.028f;
        /// <summary>A let-go thumb locks again within this height (m) and this radius (m) of where it rested at the grip.</summary>
        public const float ThumbRelockLift = 0.012f, ThumbRelockRadius = 0.016f;
        /// <summary>A re-lock re-centres where the thumb lands, at most this far from the grip's neutral (m).</summary>
        public const float ThumbRecentreMax = 0.004f;
        /// <summary>The thumb tip rests this far above the cap's top when seated (m): the pad of the thumb is on the cap.</summary>
        public const float ThumbSeatHeight = 0.006f;
        /// <summary>At a grip it moves in the hands at most this far (m) so the sticks sit under the thumbs.</summary>
        public const float SeatMaxShift = 0.05f;

        static readonly ThumbTuning Thumb = new ThumbTuning
        {
            Travel = ThumbTravel, Deadzone = ThumbDeadzone, Expo = ThumbExpo,
            LiftRelease = ThumbLiftRelease, SlideRelease = ThumbSlideRelease,
            RelockLift = ThumbRelockLift, RelockRadius = ThumbRelockRadius,
            RecentreMax = ThumbRecentreMax, Grace = LostGraceSeconds,
        };

        // Hand h's thumb works stick h (left hand, left stick).
        readonly ThumbLock[] locks = new ThumbLock[2];
        readonly bool[] atEnd = new bool[2];

        enum GripKind { None, Palm, Pinch }

        struct HandState
        {
            public bool tracked, own, sdk, armed, pinchArmed;
            public GripKind kind;
            public float lostFor, curl, pinch;
            public Vector3 pos, thumb, index, palm;
            public Quaternion rot;
            public Vector3 offPos;
            public Quaternion offRot;
            public bool Held => own || sdk;
            // A pinch hold uses the thumb to hold it: that thumb can't also be on a stick.
            public bool ThumbFree => Held && !(own && kind == GripKind.Pinch);
        }

        readonly HandState[] hands = new HandState[2];
        readonly HandSample[] samples = { new HandSample { isLeft = true }, new HandSample { isLeft = false } };
        readonly float[] features = new float[HandFeatures.Count];
        IHandSource rig;
        int lastMask;

        // Sticks: which hand's fingertip has each (-1 none), whether that tip is the thumb (else the index), where it landed.
        readonly int[] owner = { -1, -1 };
        readonly bool[] ownerThumb = new bool[2];
        readonly Vector2[] anchor = new Vector2[2];

        // One-hand tilt: the rotation it settled at in the hand.
        Quaternion tiltRef;
        bool tiltValid;
        float tiltArmAt;

        // Buttons A, B and the mode switch.
        readonly bool[] pressed = new bool[3];
        readonly bool[] pressArmed = { true, true, true };

        // The body's box for "is a hand on it" (local, metres): the body and the grips under it.
        static readonly Vector3 GripBoxCentre = new Vector3(0f, -0.022f, -0.01f), GripBoxHalf = new Vector3(0.10f, 0.048f, 0.065f);

        int HeldMask() => (hands[0].Held ? 1 : 0) | (hands[1].Held ? 2 : 0);

        /// <summary>Called by <see cref="TransmitterIsdk"/>: the Interaction SDK's hand grab took (or let go of) it with hand 0 (left) or 1 (right).</summary>
        public void SdkGrip(int hand, bool on)
        {
            if (hand < 0 || hand > 1) return;
            // Docked, the SDK's grab doesn't pick it up (fingers resting on the sticks would): closing a hand round it and
            // lifting it does (DockGestures).
            if (on && Docked && !hands[hand].own) return;
            // Placing it needs that hand's joints: a grab from a hand this hasn't tracked yet waits for the joint grip.
            if (on && !hands[hand].tracked) return;
            hands[hand].sdk = on;
        }

        void ReleaseAll()
        {
            for (int h = 0; h < 2; h++) { hands[h].own = false; hands[h].sdk = false; locks[h].Release(); }
            lastMask = 0;
        }

        // Where a seated thumb tip sits over stick s, in the transmitter's frame.
        static Vector3 SeatPoint(int s)
        {
            Vector2 p = StickPivot(s);
            return new Vector3(p.x, CapTopY + ThumbSeatHeight, p.y);
        }

        static Vector3 Neutral(in ThumbLock l) => new Vector3(l.Ox, l.Oy, l.Oz);

        IHandSource Source()
        {
            if (HandsTransmitter.Source != null) return HandsTransmitter.Source;
            if (rig == null && OVRManager.instance != null) rig = new IsdkHandSource();
            return rig;
        }

        // Between the two palms, when both are tracked (for a summon).
        bool PalmsBetween(out Vector3 at)
        {
            at = default;
            var source = Source();
            if (source == null) return false;
            for (int h = 0; h < 2; h++)
            {
                source.Read(h, samples[h]);
                if (!samples[h].tracked) return false;
            }
            at = (HandFeatures.PalmCenter(samples[0]) + HandFeatures.PalmCenter(samples[1])) * 0.5f;
            return true;
        }

        void ReadHands(float dt)
        {
            var source = Source();
            var cam = Camera.main;
            Vector3 headPos = cam != null ? cam.transform.position : Vector3.zero;
            Vector3 headFwd = cam != null ? cam.transform.forward : Vector3.forward;
            for (int h = 0; h < 2; h++)
            {
                var sample = samples[h];
                bool ok = false;
                if (source != null)
                {
                    source.Read(h, sample);
                    ok = sample.tracked && sample.highConfidence;
                }
                ref HandState s = ref hands[h];
                if (!ok)
                {
                    // Lost or untrusted for a moment (a thumb hidden behind the other hand): keep holding, frozen.
                    s.tracked = false;
                    s.lostFor += dt;
                    if (s.own && s.lostFor > HeldLostGraceSeconds) s.own = false;
                    if (liftHand == h && s.lostFor > DockLostGrace) liftHand = -1;
                    if (dragHand == h && s.lostFor > DockLostGrace) EndDockDrag();
                    continue;
                }
                s.tracked = true;
                s.lostFor = 0f;
                var j = sample.joints;
                s.thumb = j[HandJoint.ThumbTip];
                s.index = j[HandJoint.IndexTip];
                s.palm = HandFeatures.PalmCenter(sample);
                s.pos = j[HandJoint.Wrist];
                Vector3 along = j[HandJoint.Middle1] - j[HandJoint.Wrist];
                Vector3 back = -HandFeatures.PalmNormal(sample);
                if (along.sqrMagnitude > 1e-6f && back.sqrMagnitude > 1e-6f) s.rot = Quaternion.LookRotation(along, back);
                HandFeatures.Compute(sample, headPos, headFwd, features);
                s.curl = (features[(int)HandFeature.MiddleCurl] + features[(int)HandFeature.MiddleFlex]
                        + features[(int)HandFeature.RingCurl] + features[(int)HandFeature.RingFlex]
                        + features[(int)HandFeature.PinkyCurl] + features[(int)HandFeature.PinkyFlex]) / 6f;
                s.pinch = features[(int)HandFeature.Pinch];
                float indexBend = (features[(int)HandFeature.IndexCurl] + features[(int)HandFeature.IndexFlex]) * 0.5f;

                if (s.own)
                {
                    bool keep = s.kind == GripKind.Pinch ? s.pinch < HandsTransmitter.PinchOpen
                                                         : TransmitterMath.Gripping(true, s.curl, GripClose, GripOpen);
                    if (!keep) { s.own = false; s.kind = GripKind.None; }
                    continue;
                }
                // A grip has to close near it from an open hand: a fist that wanders into it doesn't pick it up.
                if (s.curl < GripOpen) s.armed = true;
                if (s.pinch >= HandsTransmitter.PinchOpen) s.pinchArmed = true;
                // Docked and nobody holds it: fingertips drive the sticks (Fingers); a pinch on the body drags the dock, and
                // closing a hand round it and lifting picks it up.
                if (Docked && !mouseHeld && HeldMask() == 0) { DockGestures(h, ref s, indexBend); continue; }
                // The index has to close too: a pointing hand (three fingers curled, index out) pushing a stick isn't a grip.
                if (s.armed && s.curl >= GripClose && indexBend >= GripIndexClose && BoxGap(s.palm) <= GripReach)
                {
                    s.own = true; s.kind = GripKind.Palm; s.armed = false;
                }
                else if (s.pinchArmed && s.pinch <= HandsTransmitter.PinchClosed && BoxGap((s.thumb + s.index) * 0.5f) <= PinchReach)
                {
                    s.own = true; s.kind = GripKind.Pinch; s.pinchArmed = false;
                }
            }

            int mask = HeldMask();
            if (mask == lastMask) return;
            int before = lastMask;
            lastMask = mask;
            int added = mask & ~before;
            // Who holds it changed. Seat it in the hands: slide it (in its own frame, no turn) so each holding thumb's stick
            // is under that thumb, averaged over the thumbs, at most SeatMaxShift. A thumb already seated counts where its
            // neutral is, so a second hand joining moves it only half way for its own thumb.
            Vector3 shift = Vector3.zero;
            int seated = 0;
            for (int h = 0; h < 2; h++)
            {
                ref HandState s = ref hands[h];
                if (!s.ThumbFree || !s.tracked) continue;
                Vector3 at = (added & (1 << h)) == 0 && locks[h].Seated ? Neutral(locks[h]) : Local(s.thumb);
                shift += at - SeatPoint(h);
                seated++;
            }
            if (seated > 0)
            {
                shift /= seated;
                if (shift.sqrMagnitude > SeatMaxShift * SeatMaxShift) shift = shift.normalized * SeatMaxShift;
            }
            // Thumbs: a new grip locks its thumb where it rests (that is the stick's centre); a kept one keeps its
            // deflection through the slide; a hand that let go (or holds by pinching) frees its thumb.
            bool clicked = false;
            for (int h = 0; h < 2; h++)
            {
                ref HandState s = ref hands[h];
                if (!s.ThumbFree || !s.tracked) { if (!s.Held || !s.ThumbFree) locks[h].Release(); continue; }
                if ((added & (1 << h)) != 0 || !locks[h].Seated)
                {
                    Vector3 n = Local(s.thumb) - shift;
                    locks[h].Grip(n.x, n.y, n.z);
                    atEnd[h] = false;
                    Seat(h);
                    clicked = true;
                }
                else locks[h].Shift(shift.x, shift.y, shift.z);
            }
            if (clicked) Click(false);
            // Every holding hand keeps the transmitter where it is now (plus the seat slide), so nothing else jumps.
            Quaternion r = measureRot;
            Vector3 p = measurePos + r * shift;
            measurePos = p;
            for (int h = 0; h < 2; h++)
            {
                ref HandState s = ref hands[h];
                if (!s.Held) continue;
                Quaternion inv = Quaternion.Inverse(s.rot);
                s.offRot = inv * r;
                s.offPos = inv * (p - s.pos);
            }
            // An index finger drives a stick only while its hand doesn't hold (a holding hand's thumb is locked instead).
            for (int st = 0; st < 2; st++)
                if (owner[st] >= 0 && hands[owner[st]].Held) owner[st] = -1;
            if (before == 0 && mask != 0) PickedUp();
            tiltValid = false;
            tiltArmAt = Time.unscaledTime + TiltSettleSeconds;
            if (mask != 0)
            {
                mouseHeld = false;
                gliding = false;
                appear = 1f;
                if (model != null) model.transform.localScale = Vector3.one;
            }
            if (before != 0 && mask == 0) PutDown();
        }

        // ---- Docked gestures ------------------------------------------------------------------------------------------

        int liftHand = -1, dragHand = -1;
        Vector3 liftFrom, dragFromPinch, dragFromPos, dragTo;

        /// <summary>True while a pinch on its body is moving the dock.</summary>
        public bool DraggingDock => dragHand >= 0;

        void DockGestures(int h, ref HandState s, float indexBend)
        {
            Vector3 pinchAt = (s.thumb + s.index) * 0.5f;
            if (dragHand == h)
            {
                if (s.pinch < HandsTransmitter.PinchOpen) dragTo = dragFromPos + (pinchAt - dragFromPinch);
                else EndDockDrag();
                return;
            }
            if (liftHand == h)
            {
                if (!TransmitterMath.Gripping(true, s.curl, GripClose, GripOpen)) { liftHand = -1; return; }
                if ((s.palm - liftFrom).sqrMagnitude < LiftToHold * LiftToHold) return;
                // Lifted off the dock: held in that hand, as a grip in handheld mode (the seat and thumb lock follow).
                liftHand = -1;
                s.own = true; s.kind = GripKind.Palm; s.armed = false;
                for (int st = 0; st < 2; st++) if (owner[st] == h) owner[st] = -1;
                return;
            }
            bool driving = owner[0] == h || owner[1] == h;
            if (dragHand < 0 && s.pinchArmed && s.pinch <= HandsTransmitter.PinchClosed && BoxGap(pinchAt) <= 0.015f && ClearOfControls(Local(pinchAt)))
            {
                dragHand = h;
                dragFromPinch = pinchAt;
                dragFromPos = dragTo = transform.position;
                s.pinchArmed = false;
                gliding = false;
                return;
            }
            // A hand driving a stick isn't picking it up; one that closes round the body from open may be.
            if (!driving && liftHand < 0 && s.armed && s.curl >= GripClose && indexBend >= GripIndexClose && BoxGap(s.palm) <= GripReach)
            {
                liftHand = h;
                liftFrom = s.palm;
                s.armed = false;
            }
        }

        // A point (the transmitter's frame) away from both sticks and every button and the switch, across the face.
        static bool ClearOfControls(Vector3 local)
        {
            for (int st = 0; st < 2; st++)
            {
                Vector2 p = StickPivot(st);
                float dx = local.x - p.x, dz = local.z - p.y;
                if (dx * dx + dz * dz < (DockCaptureRadius * 0.75f) * (DockCaptureRadius * 0.75f)) return false;
            }
            for (int c = 0; c < 3; c++)
            {
                Vector2 p = ControlAt(c);
                float dx = local.x - p.x, dz = local.z - p.y;
                if (dx * dx + dz * dz < DragClear * DragClear) return false;
            }
            return true;
        }

        void EndDockDrag()
        {
            if (dragHand < 0) return;
            dragHand = -1;
            SetDockHere();
        }

        // Where the holding hands put it: each hand's own idea, averaged (two hands: halfway between).
        bool HandTarget(out Vector3 p, out Quaternion r)
        {
            p = default; r = Quaternion.identity;
            int n = 0;
            for (int h = 0; h < 2; h++)
            {
                ref HandState s = ref hands[h];
                if (!s.Held) continue;
                Vector3 hp = s.pos + s.rot * s.offPos;
                Quaternion hr = s.rot * s.offRot;
                if (n == 0) { p = hp; r = hr; }
                else { p = (p + hp) * 0.5f; r = Quaternion.Slerp(r, hr, 0.5f); }
                n++;
            }
            return n > 0;
        }

        // Distance from a world point to the body's box (0 inside).
        float BoxGap(Vector3 world)
        {
            Vector3 d = Local(world) - GripBoxCentre;
            float x = Mathf.Max(0f, Mathf.Abs(d.x) - GripBoxHalf.x);
            float y = Mathf.Max(0f, Mathf.Abs(d.y) - GripBoxHalf.y);
            float z = Mathf.Max(0f, Mathf.Abs(d.z) - GripBoxHalf.z);
            return Mathf.Sqrt(x * x + y * y + z * z);
        }

        // A holding hand's own idea of the transmitter's frame: a thumb is read against its own hand, so the other hand's
        // tracking jitter (and the drawn pose's smoothing) never moves its stick.
        Vector3 LocalToHand(int h, Vector3 world)
        {
            ref HandState s = ref hands[h];
            if (!s.Held) return Local(world);
            Quaternion r = s.rot * s.offRot;
            return Quaternion.Inverse(r) * (world - (s.pos + s.rot * s.offPos));
        }

        // ---- Docked: the nearest fingertip on each stick ---------------------------------------------------------------

        float lastDockTouch = -10f;

        void DockedFingers()
        {
            bool anyBefore = owner[0] >= 0 || owner[1] >= 0;
            for (int st = 0; st < 2; st++)
            {
                if (mouseStick == st) continue;
                Vector2 pivot = StickPivot(st);
                int o = owner[st];
                if (o >= 0)
                {
                    ref HandState hs = ref hands[o];
                    if (!hs.tracked)
                    {
                        // A tracking flicker holds the stick where it was; a long gap lets it spring home.
                        if (hs.lostFor > DockLostGrace) { owner[st] = -1; continue; }
                        raw[st] = stick[st];
                        driven[st] = true;
                        continue;
                    }
                    Vector3 local = Local(ownerThumb[st] ? hs.thumb : hs.index);
                    float dx = local.x - pivot.x - anchor[st].x, dz = local.z - pivot.y - anchor[st].y;
                    if (local.y - CapTopY > DockReleaseHeight || dx * dx + dz * dz > DockReleaseRadius * DockReleaseRadius)
                    {
                        owner[st] = -1;  // lifted or slid away: the stick springs home (StepSticks)
                        continue;
                    }
                    TransmitterMath.ThumbStick(dx, dz, DockTravel, DockDeadzone, DockExpo, out float x, out float y);
                    raw[st] = new Vector2(x, y);
                    driven[st] = true;
                    float m2 = x * x + y * y;
                    if (!atEnd[st] && m2 >= 0.94f) { atEnd[st] = true; Seat(st); Click(true); }
                    else if (atEnd[st] && m2 < 0.7f) atEnd[st] = false;
                    continue;
                }
                // Free: the nearest thumb or index tip in the capture zone takes it, centred where it touched.
                int bestHand = -1; bool bestThumb = false; float best = float.PositiveInfinity; Vector2 at = default;
                for (int h = 0; h < 2; h++)
                {
                    ref HandState hs = ref hands[h];
                    if (!hs.tracked || dragHand == h) continue;
                    for (int f = 0; f < 2; f++)
                    {
                        bool thumb = f == 0;
                        int other = owner[1 - st];
                        if (other == h && ownerThumb[1 - st] == thumb) continue;  // that tip has the other stick
                        Vector3 local = Local(thumb ? hs.thumb : hs.index);
                        float dx = local.x - pivot.x, dz = local.z - pivot.y, height = local.y - CapTopY;
                        float d2 = dx * dx + dz * dz;
                        if (d2 > DockCaptureRadius * DockCaptureRadius || height > DockEngageAbove || height < -DockEngageBelow) continue;
                        if (d2 >= best) continue;
                        best = d2; bestHand = h; bestThumb = thumb; at = new Vector2(dx, dz);
                    }
                }
                if (bestHand < 0) continue;
                owner[st] = bestHand;
                ownerThumb[st] = bestThumb;
                anchor[st] = at;
                atEnd[st] = false;
                driven[st] = true;  // centred this frame
                Seat(st);
                Click(false);
            }
            bool anyNow = owner[0] >= 0 || owner[1] >= 0;
            if (anyNow && !anyBefore)
            {
                // Fingers back on the sticks after a while: what to do next, as a pick-up would say it.
                if (Time.unscaledTime - lastDockTouch > 3f) PickedUp();
            }
            if (anyNow) lastDockTouch = Time.unscaledTime;
        }

        // ---- Fingertips: the sticks, then the buttons --------------------------------------------------------------------

        void Fingers(float now)
        {
            for (int st = 0; st < 2; st++) { driven[st] = false; raw[st] = Vector2.zero; }
            if (Docked && HeldMask() == 0 && !mouseHeld)
            {
                DockedFingers();
                for (int c = 0; c < 3; c++) Button(c);
                return;
            }

            for (int st = 0; st < 2; st++)
            {
                Vector2 pivot = StickPivot(st);
                // The holding hand's thumb, locked on its stick since the grip.
                ref ThumbLock lk = ref locks[st];
                if (lk.Seated && mouseStick != st)
                {
                    ref HandState th = ref hands[st];
                    Vector3 tl = th.tracked ? LocalToHand(st, th.thumb) : Vector3.zero;
                    int happened = lk.Step(th.tracked, th.lostFor, tl.x, tl.y, tl.z, in Thumb, out float tx, out float ty);
                    if (happened == ThumbLock.LockedNow) { Seat(st); Click(false); }
                    if (lk.Locked)
                    {
                        owner[st] = -1;
                        raw[st] = new Vector2(tx, ty);
                        driven[st] = true;
                        // The end of the stick's travel: a tick, once, until it comes back in.
                        float m2 = tx * tx + ty * ty;
                        if (!atEnd[st] && m2 >= 0.94f) { atEnd[st] = true; Seat(st); Click(true); }
                        else if (atEnd[st] && m2 < 0.7f) atEnd[st] = false;
                        continue;
                    }
                }
                int o = owner[st];
                if (o >= 0)
                {
                    ref HandState hs = ref hands[o];
                    if (!hs.tracked)
                    {
                        // A short tracking gap holds the stick where it was; a long one lets it spring home.
                        if (hs.lostFor > LostGraceSeconds) { owner[st] = -1; continue; }
                        raw[st] = stick[st];
                        driven[st] = true;
                        continue;
                    }
                    Vector3 local = Local(ownerThumb[st] ? hs.thumb : hs.index);
                    float dx = local.x - pivot.x, dz = local.z - pivot.y;
                    float height = local.y - CapTopY, across = Mathf.Sqrt(dx * dx + dz * dz);
                    if (!TransmitterMath.Engaged(true, height, across, CaptureRadius, EngageHeight, ReleaseRadius, ReleaseHeight))
                    {
                        owner[st] = -1;
                        continue;
                    }
                    TransmitterMath.StickFromOffset(dx - anchor[st].x, dz - anchor[st].y, StickTravel, StickDeadzone, out float x, out float y);
                    raw[st] = new Vector2(x, y);
                    driven[st] = true;
                    continue;
                }
                if (mouseStick == st) continue;
                for (int h = 0; h < 2; h++)
                {
                    ref HandState hs = ref hands[h];
                    // A holding hand's thumb is locked (above), its index is round the body: neither pushes a stick here.
                    if (!hs.tracked || hs.Held || owner[1 - st] == h) continue;
                    const bool thumb = false;
                    Vector3 local = Local(hs.index);
                    float dx = local.x - pivot.x, dz = local.z - pivot.y;
                    float height = local.y - CapTopY, across = Mathf.Sqrt(dx * dx + dz * dz);
                    if (!TransmitterMath.Engaged(false, height, across, CaptureRadius, EngageHeight, ReleaseRadius, ReleaseHeight)) continue;
                    owner[st] = h;
                    ownerThumb[st] = thumb;
                    TransmitterMath.ClampAnchor(dx, dz, AnchorMaxOff, out float ax, out float az);
                    anchor[st] = new Vector2(ax, az);
                    driven[st] = true;  // centred this frame
                    break;
                }
            }

            // One hand: the stick its thumb can't reach follows the tilt from where it settled in the hand.
            int mask = HeldMask();
            if (TiltFallback && (mask == 1 || mask == 2) && now >= tiltArmAt)
            {
                if (!tiltValid) { tiltRef = measureRot; tiltValid = true; }
                int st = mask == 1 ? 1 : 0;  // held in the left hand: the right stick tilts, and the other way round
                if (owner[st] < 0 && mouseStick != st && !locks[st].Locked)
                {
                    Vector3 up = Quaternion.Inverse(tiltRef) * (measureRot * Vector3.up);
                    float pitch = Mathf.Atan2(up.z, up.y) * Mathf.Rad2Deg;   // far edge down: forward
                    float roll = Mathf.Atan2(up.x, up.y) * Mathf.Rad2Deg;    // right side down: right
                    raw[st] = new Vector2(TransmitterMath.TiltAxis(roll, TiltDead, TiltFull), TransmitterMath.TiltAxis(pitch, TiltDead, TiltFull));
                    driven[st] = true;
                }
            }

            for (int c = 0; c < 3; c++) Button(c);
        }

        // A fingertip pressing a control: the deepest one in reach decides. Thumbs on sticks don't count.
        void Button(int c)
        {
            Vector2 at = ControlAt(c);
            float top = ControlTopY(c), radius = c == ModeSwitch ? ButtonRadius * 1.2f : ButtonRadius;
            float deepest = float.NegativeInfinity, across = float.PositiveInfinity;
            bool clear = true;
            for (int h = 0; h < 2; h++)
            {
                ref HandState hs = ref hands[h];
                if (!hs.tracked) continue;
                for (int f = 0; f < 2; f++)
                {
                    bool thumb = f == 0;
                    // A thumb locked on its stick isn't pressing; one slid off it (onto the button) or lifted is free to.
                    if (thumb && locks[h].Locked) continue;
                    // Nor is a tip working a stick (docked, or a free hand's index).
                    if ((owner[0] == h && ownerThumb[0] == thumb) || (owner[1] == h && ownerThumb[1] == thumb)) continue;
                    Vector3 local = Local(thumb ? hs.thumb : hs.index);
                    float dx = local.x - at.x, dz = local.z - at.y;
                    float a = Mathf.Sqrt(dx * dx + dz * dz), depth = top - local.y;
                    // A fingertip well under the cap is a finger round the body (a holding hand), not a press.
                    if (depth > MaxPressDepth) continue;
                    if (a <= radius * 1.6f && depth > -0.008f) clear = false;
                    if (a <= radius * 1.4f && depth > deepest) { deepest = depth; across = a; }
                }
            }
            bool was = pressed[c];
            bool now = !float.IsNegativeInfinity(deepest)
                    && TransmitterMath.Pressed(was, pressArmed[c], deepest, across, radius, ButtonPressDepth, ButtonReleaseDepth);
            if (clear) pressArmed[c] = true;
            if (now && !was)
            {
                pressArmed[c] = false;
                Press(c);
            }
            pressed[c] = now;
        }
    }
}
