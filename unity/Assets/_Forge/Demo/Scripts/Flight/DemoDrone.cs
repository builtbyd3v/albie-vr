using System.Collections;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Demo
{
    /// <summary>
    /// The flight station of the video demo: the drone (yesterday's quadcopter CAD mock) builds itself on a glowing pad, takes off to
    /// hover in front of the user, takes the camera on its nose, then follows the user round the room and comes home to their palm.
    /// Actions (docs/demo/TONIGHT_PLAN.md section 5): drone.open, drone.takeoff, drone.mount_camera {from}, drone.follow_start
    /// {distance}, drone.follow_stop, dronecam.show, dronecam.hide, demo.reset. Signals: drone.built, drone.hovering, drone.mounted,
    /// drone.following, drone.home. Mock first: with no scan the room is the mock living room, with no hand it hovers at a fixed spot.
    /// </summary>
    public sealed class DemoDrone : MonoBehaviour
    {
        public enum Mode { Parked, Building, Built, TakingOff, Hover, Following, Returning, Home }

        [Header("Parts")]
        public DroneBody body;
        public DroneCamWindow cam;
        [Tooltip("Forge/Demo/FlightUnlit, additive.")] public Material additive;

        [Header("Flight")]
        public float hoverSpan = 0.32f;
        public float maxSpeed = 1.5f;
        public float minAltitude = 0.5f;
        public float wallClearance = 0.4f;
        public float followHeightBelowHead = 0.1f;

        public Mode State { get; private set; } = Mode.Parked;
        /// <summary>The drone as the flight sees it (the yaw-only root, without bob and lean).</summary>
        public Transform Drone => drone;
        public DroneRoom Room => room;
        public float TargetDistance { get; private set; } = 2f;

        Transform drone, lean;
        Transform pad;
        Renderer padGlow, padRing;
        MaterialPropertyBlock block;
        DroneRoom room;
        Vector3 pos, vel, hoverPoint, padPoint, goal;
        float yaw, yawVel;
        Vector2 leanNow, leanVel;
        float bobT;
        float padAlpha, padTarget;
        float followDistance = 2f;
        float nextGoalAt, nextAvoidAt;
        Vector3 repel;
        float smoothTime = 0.7f, speedLimit = 1.5f;
        float returningSince;
        bool announcedFollowing, announcedHome, followingReady;
        float followStartAt;
        AudioSource humSrc, sfxSrc;
        AudioClip humClip, clickClip;
        Coroutine openRoutine, takeoffRoutine, mountRoutine, followRoutine, sweepRoutine;
        GameObject scanRoot;
        Material scanWire, scanWall, scanBox;

        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        static readonly Vector3[] Probe = new Vector3[8];

        // ---- Lifecycle -----------------------------------------------------------------------------------------------------

        void Awake()
        {
            block = new MaterialPropertyBlock();
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI * 0.25f;
                Probe[i] = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            }

            drone = new GameObject("Drone").transform;
            drone.SetParent(transform, false);
            lean = new GameObject("Lean").transform;
            lean.SetParent(drone, false);
            if (body == null) body = GetComponentInChildren<DroneBody>(true);
            if (body != null)
            {
                body.transform.SetParent(lean, false);
                body.span = hoverSpan;
                body.softDot = FlightTextures.SoftDot();
                if (body.additive == null) body.additive = additive;
            }

            pad = new GameObject("Pad").transform;
            pad.SetParent(transform, false);
            padGlow = MakeFlat("Glow", pad, 0.42f, FlightTextures.SoftDot());
            padRing = MakeFlat("Ring", pad, 0.34f, FlightTextures.Ring());
            SetPadAlpha(0f);

            humClip = FlightSound.Hum();
            clickClip = FlightSound.Click();
            humSrc = MakeSource("Hum", true);
            humSrc.clip = humClip;
            humSrc.loop = true;
            sfxSrc = MakeSource("Sfx", false);
            if (cam != null) cam.gameObject.SetActive(true);
            pos = Park();
        }

        AudioSource MakeSource(string n, bool loop)
        {
            var go = new GameObject(n);
            go.transform.SetParent(drone, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 0.4f;
            s.maxDistance = 8f;
            s.volume = 0f;
            s.loop = loop;
            return s;
        }

        Renderer MakeFlat(string n, Transform parent, float size, Texture2D tex)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(q.GetComponent<Collider>());
            q.name = n;
            q.transform.SetParent(parent, false);
            q.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);   // faces up
            q.transform.localScale = Vector3.one * size;
            var r = q.GetComponent<MeshRenderer>();
            r.sharedMaterial = additive;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            block.SetTexture(MainTexId, tex);
            block.SetColor(ColorId, new Color(1f, 1f, 1f, 0f));
            r.SetPropertyBlock(block);
            return r;
        }

        void SetPadAlpha(float a)
        {
            padAlpha = a;
            if (padGlow == null) return;
            block.SetTexture(MainTexId, FlightTextures.SoftDot());
            block.SetColor(ColorId, new Color(0.45f, 0.85f, 1f, 0.55f * a));
            padGlow.SetPropertyBlock(block);
            block.SetTexture(MainTexId, FlightTextures.Ring());
            block.SetColor(ColorId, new Color(0.75f, 0.95f, 1f, 0.9f * a));
            padRing.SetPropertyBlock(block);
            padGlow.enabled = padRing.enabled = a > 0.01f;
        }

        void OnEnable()
        {
            DemoActions.Register("drone.open", OnOpen);
            DemoActions.Register("drone.takeoff", OnTakeoff);
            DemoActions.Register("drone.mount_camera", OnMount);
            DemoActions.Register("drone.follow_start", OnFollowStart);
            DemoActions.Register("drone.follow_stop", OnFollowStop);
            DemoActions.Register("dronecam.show", OnCamShow);
            DemoActions.Register("dronecam.hide", OnCamHide);
            DemoActions.Register(DemoActions.Reset, OnReset);
        }

        void OnDisable()
        {
            DemoActions.Unregister("drone.open", OnOpen);
            DemoActions.Unregister("drone.takeoff", OnTakeoff);
            DemoActions.Unregister("drone.mount_camera", OnMount);
            DemoActions.Unregister("drone.follow_start", OnFollowStart);
            DemoActions.Unregister("drone.follow_stop", OnFollowStop);
            DemoActions.Unregister("dronecam.show", OnCamShow);
            DemoActions.Unregister("dronecam.hide", OnCamHide);
            DemoActions.Unregister(DemoActions.Reset, OnReset);
        }

        void OnDestroy()
        {
            room?.Dispose();
            if (scanWire != null) Destroy(scanWire);
            if (scanWall != null) Destroy(scanWall);
            if (scanBox != null) Destroy(scanBox);
        }

        // ---- Places ----------------------------------------------------------------------------------------------------------

        Vector3 HeadPos()
        {
            var h = DemoLayout.Head;
            return h != null ? h.position : new Vector3(0f, 1.15f, 0f);
        }

        /// <summary>Where the pad is: the hover point, lower.</summary>
        void PlacePad()
        {
            hoverPoint = DemoLayout.Get(DemoLayout.Slot.Drone).position;
            padPoint = hoverPoint + Vector3.down * 0.2f;
            pad.position = padPoint;
            pad.rotation = Quaternion.identity;
        }

        Vector3 Park()
        {
            PlacePad();
            float rest = (body != null ? Mathf.Max(body.HalfHeight, 0.02f) : 0.03f) + 0.012f;
            return padPoint + Vector3.up * rest;
        }

        static bool TryPalm(out Vector3 p)
        {
            var e = GestureEngine.Current;
            if (e != null)
            {
                // The hand held palm up, whichever it is; else any tracked hand, the left first.
                GestureHand hand = null;
                for (int i = 0; i < e.Hands.Length; i++)
                {
                    GestureHand h = e.Hands[i];
                    if (!h.Usable) continue;
                    if (hand == null) hand = h;
                    if (GestureLibrary.CanHoldPerch(h.values)) { hand = h; break; }
                }
                if (hand != null)
                {
                    p = hand.Palm;
                    return true;
                }
            }
            // Controllers in hand (the A-button demo): hand tracking is off, so the left controller stands in for the palm.
            if (OVRInput.GetControllerPositionTracked(OVRInput.Controller.LTouch))
            {
                if (s_rig == null) s_rig = FindAnyObjectByType<OVRCameraRig>();
                if (s_rig != null && s_rig.leftControllerAnchor != null)
                {
                    p = s_rig.leftControllerAnchor.position;
                    return true;
                }
            }
            p = default;
            return false;
        }

        static OVRCameraRig s_rig;

        /// <summary>Where the drone comes home to: over the left palm (or left controller) if tracked, else a spot in front of the head.</summary>
        Vector3 HomePoint()
        {
            if (TryPalm(out var p)) return p + Vector3.up * 0.2f;
            var h = DemoLayout.Head;
            if (h == null) return hoverPoint;
            Vector3 f = h.forward; f.y = 0f;
            f = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
            return h.position + f * 0.7f + Vector3.down * 0.3f;
        }

        // ---- Actions ---------------------------------------------------------------------------------------------------------

        void Stop(ref Coroutine c)
        {
            if (c != null) StopCoroutine(c);
            c = null;
        }

        void OnOpen(DemoCue cue)
        {
            Stop(ref openRoutine);
            Stop(ref takeoffRoutine);
            Stop(ref followRoutine);
            openRoutine = StartCoroutine(OpenRoutine());
        }

        IEnumerator OpenRoutine()
        {
            State = Mode.Building;
            vel = Vector3.zero;
            if (body != null) { body.SetAssembled(false); }
            pos = Park();
            ApplyPose(true);
            FadePad(1f, 0.5f);
            float wait = 0f;
            while (body != null && !body.Ready && !body.Failed && wait < 8f) { wait += Time.deltaTime; yield return null; }
            if (body != null && body.Ready)
            {
                PlaySfx(clickClip, 0.25f);
                Coroutine sweep = StartCoroutine(SweepRoutine(2.1f));
                yield return body.Assemble(3f);
                yield return sweep;
            }
            else yield return new WaitForSeconds(0.5f);
            State = Mode.Built;
            DemoActions.Raise("drone.built");
            openRoutine = null;
        }

        /// <summary>A ring of light sweeping up the drone, once, after the last part clicks in.</summary>
        IEnumerator SweepRoutine(float delay)
        {
            yield return new WaitForSeconds(delay);
            var ring = MakeFlat("Sweep", transform, 0.2f, FlightTextures.Ring());
            const float dur = 0.8f;
            for (float t = 0f; t < dur; t += Time.deltaTime)
            {
                float u = t / dur;
                ring.transform.position = pos + Vector3.down * 0.03f + Vector3.up * (u * 0.22f);
                ring.transform.localScale = Vector3.one * Mathf.Lerp(0.18f, 0.5f, u);
                block.SetTexture(MainTexId, FlightTextures.Ring());
                block.SetColor(ColorId, new Color(0.7f, 0.95f, 1f, 0.9f * Mathf.Sin(u * Mathf.PI)));
                ring.SetPropertyBlock(block);
                yield return null;
            }
            Destroy(ring.gameObject);
        }

        void OnTakeoff(DemoCue cue)
        {
            Stop(ref takeoffRoutine);
            takeoffRoutine = StartCoroutine(TakeoffRoutine());
        }

        IEnumerator TakeoffRoutine()
        {
            // takeoff can arrive while the build is still running (the Director fires both for B2): wait for it.
            float guard = 0f;
            while (State == Mode.Building && guard < 10f) { guard += Time.deltaTime; yield return null; }
            if (State == Mode.Parked)
            {
                if (body != null) body.SetAssembled(true);
                pos = Park();
                FadePad(1f, 0.3f);
            }
            State = Mode.TakingOff;
            PlacePad();
            float t0 = Time.time;
            // Props spin up first, then it rises.
            while (Time.time - t0 < 1.3f)
            {
                float u = (Time.time - t0) / 1.3f;
                SetSpin(u * u);
                yield return null;
            }
            SetSpin(1f);
            goal = hoverPoint;
            smoothTime = 0.75f;
            speedLimit = 0.6f;
            float climbStart = Time.time;
            while ((pos - hoverPoint).sqrMagnitude > 0.0004f || vel.sqrMagnitude > 0.0025f)
            {
                if (Time.time - climbStart > 4f) break;
                yield return null;
            }
            State = Mode.Hover;
            FadePad(0.3f, 1.0f);
            DemoActions.Raise("drone.hovering");
            takeoffRoutine = null;
        }

        void SetSpin(float s)
        {
            if (body != null) body.Spin = s;
            if (humSrc != null)
            {
                if (s > 0.02f && !humSrc.isPlaying) humSrc.Play();
                if (s <= 0.02f && humSrc.isPlaying) humSrc.Stop();
                humSrc.volume = 0.07f * Mathf.Clamp01(s);
                humSrc.pitch = 0.55f + 0.5f * Mathf.Clamp01(s);
            }
        }

        void OnMount(DemoCue cue)
        {
            Vector3 from;
            var arr = cue.Args["from"] as JArray;
            if (arr != null && arr.Count >= 3) from = new Vector3((float)arr[0], (float)arr[1], (float)arr[2]);
            else if (ResearchStation.Instance != null)
            {
                // The script gives a slot name, not a point: come in from where the research verdict card is.
                from = ResearchStation.VerdictPosition;
            }
            else
            {
                // No card position: come in from the upper left, where the research panel is.
                var l = DemoLayout.Get(DemoLayout.Slot.Left).position;
                from = l + Vector3.up * 0.05f;
            }
            Stop(ref mountRoutine);
            mountRoutine = StartCoroutine(MountRoutine(from));
        }

        IEnumerator MountRoutine(Vector3 from)
        {
            if (body != null) yield return body.MountCamera(from, 1f);
            PlaySfx(clickClip, 0.4f);
            if (cam != null) cam.OnMounted();
            DemoActions.Raise("drone.mounted");
            mountRoutine = null;
        }

        void OnFollowStart(DemoCue cue)
        {
            float d = cue.Args["distance"] != null ? (float)cue.Args["distance"] : 2f;
            followDistance = Mathf.Clamp(d, 1f, 4f);
            TargetDistance = followDistance;
            Stop(ref followRoutine);
            followRoutine = StartCoroutine(FollowStartRoutine());
        }

        IEnumerator FollowStartRoutine()
        {
            if (State == Mode.Parked || State == Mode.Built || State == Mode.Building)
            {
                // The flight test was asked for before takeoff finished: take off now.
                if (body != null) body.SetAssembled(true);
                if (State != Mode.TakingOff) { pos = Park(); SetSpin(1f); }
            }
            room?.Dispose();
            room = DroneRoom.Create(HeadPos());
            if (cam != null) cam.SetRoom(room);
            FadePad(0f, 0.6f);
            Coroutine scan = StartCoroutine(ScanRoutine(2.4f));
            followingReady = false;
            announcedFollowing = false;
            followStartAt = Time.time;
            State = Mode.Following;
            nextGoalAt = 0f;
            yield return scan;
            // Followed for a moment already: announce once it has caught up to its place.
            float guard = 0f;
            while ((pos - goal).sqrMagnitude > 0.16f && guard < 5f) { guard += Time.deltaTime; yield return null; }
            followingReady = true;
            if (!announcedFollowing) { announcedFollowing = true; DemoActions.Raise("drone.following"); }
            followRoutine = null;
        }

        void OnFollowStop(DemoCue cue)
        {
            Stop(ref followRoutine);
            State = Mode.Returning;
            returningSince = Time.time;
            announcedHome = false;
            smoothTime = 0.6f;
            speedLimit = maxSpeed;
        }

        void OnCamShow(DemoCue cue) { if (cam != null) cam.Show(this); }
        void OnCamHide(DemoCue cue) { if (cam != null) cam.Hide(); }

        void OnReset(DemoCue cue)
        {
            Stop(ref openRoutine);
            Stop(ref takeoffRoutine);
            Stop(ref mountRoutine);
            Stop(ref followRoutine);
            Stop(ref sweepRoutine);
            State = Mode.Parked;
            SetSpin(0f);
            if (body != null) body.SetAssembled(false);
            if (cam != null) cam.ResetState();
            if (scanRoot != null) scanRoot.SetActive(false);
            vel = Vector3.zero;
            leanNow = leanVel = Vector2.zero;
            pos = Park();
            SetPadAlpha(0f);
            ApplyPose(true);
        }

        // ---- Per frame -----------------------------------------------------------------------------------------------------------

        void FadePad(float to, float seconds) => StartCoroutine(PadFade(to, seconds));

        float padFadeId;

        IEnumerator PadFade(float to, float seconds)
        {
            float id = ++padFadeId;
            float from = padAlpha;
            for (float t = 0f; t < seconds && id == padFadeId; t += Time.deltaTime)
            {
                float u = t / seconds;
                SetPadAlpha(Mathf.Lerp(from, to, u * u * (3f - 2f * u)));
                yield return null;
            }
            if (id == padFadeId) SetPadAlpha(to);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            switch (State)
            {
                case Mode.Parked:
                case Mode.Building:
                case Mode.Built:
                    pos = Park();
                    vel = Vector3.zero;
                    break;
                case Mode.TakingOff:
                    if (body != null && body.Spin >= 0.99f)
                        pos = Vector3.SmoothDamp(pos, goal, ref vel, smoothTime, speedLimit, dt);
                    else pos = Park();
                    break;
                case Mode.Hover:
                    pos = Vector3.SmoothDamp(pos, hoverPoint, ref vel, 0.5f, 1f, dt);
                    break;
                case Mode.Following:
                    TickFollow(dt);
                    break;
                case Mode.Returning:
                case Mode.Home:
                    TickReturn(dt);
                    break;
            }
            ApplyPose(false);
        }

        void TickFollow(float dt)
        {
            var headT = DemoLayout.Head;
            if (headT == null) return;
            Vector3 head = headT.position;
            if (Time.time >= nextGoalAt)
            {
                nextGoalAt = Time.time + 0.2f;
                goal = PickGoal(head, headT.forward, followDistance);
            }
            if (Time.time >= nextAvoidAt)
            {
                nextAvoidAt = Time.time + 0.1f;
                repel = Avoid();
            }
            smoothTime = 0.55f;
            speedLimit = maxSpeed;

            // A turn can put the goal on the far side of the user. Flying straight there would pass through them, so while the drone
            // and its goal are on opposite sides it steers to a point 60 degrees round the circle round the head, toward the goal.
            Vector3 steer = goal;
            Vector3 toPos = pos - head; toPos.y = 0f;
            Vector3 toGoal = goal - head; toGoal.y = 0f;
            if (toPos.sqrMagnitude > 1e-4f && toGoal.sqrMagnitude > 1e-4f && Vector3.Dot(toPos.normalized, toGoal.normalized) < 0.2f)
            {
                float sign = Vector3.Cross(toPos, toGoal).y >= 0f ? 1f : -1f;
                steer = head + Quaternion.AngleAxis(sign * 60f, Vector3.up) * toPos.normalized * Mathf.Max(1.5f, Mathf.Min(followDistance, toPos.magnitude + 0.3f));
                steer.y = goal.y;
            }
            Vector3 before = pos;
            pos = Vector3.SmoothDamp(pos, steer, ref vel, smoothTime, speedLimit, dt);
            pos += repel * dt;
            // Near something and in the clear: don't step into it, slide along it instead.
            if (nearObstacle && freeHere && room != null && !room.Free(pos, 0.15f))
            {
                Vector3 tx = new Vector3(pos.x, pos.y, before.z), tz = new Vector3(before.x, pos.y, pos.z);
                if (room.Free(tx, 0.15f)) pos = tx;
                else if (room.Free(tz, 0.15f)) pos = tz;
                else pos = new Vector3(before.x, pos.y, before.z);
                vel *= 0.5f;
            }
            // And never inside 1.1 m of the head, whatever else is going on.
            Vector3 away = pos - head; away.y = 0f;
            float flat = away.magnitude;
            const float minHead = 1.1f;
            if (flat < minHead)
            {
                if (flat < 1e-3f) away = -headT.forward; else away /= flat;
                away.y = 0f;
                pos += away.normalized * (minHead - flat);
            }
            if (room != null)
            {
                float lo = room.FloorY + minAltitude, hi = Mathf.Max(lo, room.Ceiling - 0.3f);
                pos.y = Mathf.Clamp(pos.y, lo, hi);
            }
        }

        /// <summary>
        /// A point <paramref name="dist"/> m from the head on the side the head faces, at head height minus a little, kept off the walls
        /// and furniture. When that spot is taken it looks round the head for the closest free one, nearer the head rather than farther.
        /// Called five times a second; no allocation.
        /// </summary>
        Vector3 PickGoal(Vector3 head, Vector3 forward, float dist)
        {
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
            float y = head.y - followHeightBelowHead;
            if (room != null) y = Mathf.Clamp(y, room.FloorY + minAltitude, Mathf.Max(room.FloorY + minAltitude, room.Ceiling - 0.3f));
            Vector3 ideal = head + forward * dist;
            ideal.y = y;
            if (room == null || (room.Free(ideal, wallClearance) && LineClear(pos, ideal))) return ideal;

            Vector3 best = ideal;
            float bestCost = float.MaxValue;
            bool found = false;
            for (int ri = 0; ri < 3; ri++)
            {
                float r = Mathf.Max(1f, dist * (1f - 0.2f * ri));
                for (int a = 0; a <= 24; a++)
                {
                    float ang = ((a + 1) / 2) * 15f * ((a & 1) == 1 ? 1f : -1f);
                    Vector3 c = head + Quaternion.AngleAxis(ang, Vector3.up) * forward * r;
                    c.y = y;
                    if (!room.Free(c, wallClearance)) continue;
                    float cost = Mathf.Abs(ang) * 0.012f + ri * 1.2f + (c - pos).magnitude * 0.25f;
                    if (!LineClear(pos, c)) cost += 4f;   // prefer a spot it can fly straight to: the way round furniture
                    if (cost < bestCost) { bestCost = cost; best = c; found = true; }
                }
            }
            return found ? best : ideal;
        }

        /// <summary>True when a straight flight from a to b stays clear of walls and furniture (tested every 0.3 m, 15 cm clearance).</summary>
        bool LineClear(Vector3 a, Vector3 b)
        {
            if (room == null) return true;
            Vector3 d = b - a; d.y = 0f;
            float len = d.magnitude;
            int n = Mathf.Min(14, Mathf.CeilToInt(len / 0.3f));
            for (int i = 1; i < n; i++)
                if (!room.Free(Vector3.Lerp(a, b, i / (float)n), 0.15f)) return false;
            return true;
        }

        /// <summary>Pushes away from walls and furniture within about 40 cm of the drone. Eight probes, ten times a second.</summary>
        Vector3 Avoid()
        {
            nearObstacle = false;
            if (room == null) return Vector3.zero;
            Vector3 push = Vector3.zero;
            for (int i = 0; i < 8; i++)
                if (!room.Free(pos + Probe[i] * 0.45f, 0.05f)) { push -= Probe[i]; nearObstacle = true; }
            freeHere = room.Free(pos, 0.15f);
            if (!freeHere) { push += (goal - pos).normalized * 1.5f; nearObstacle = true; }
            if (push.sqrMagnitude < 1e-4f) return Vector3.zero;
            return push.normalized * 1.4f;
        }

        bool nearObstacle, freeHere = true;

        void TickReturn(float dt)
        {
            Vector3 home = HomePoint();
            smoothTime = State == Mode.Home ? 0.25f : 0.6f;
            pos = Vector3.SmoothDamp(pos, home, ref vel, smoothTime, maxSpeed, dt);
            if (State == Mode.Returning)
            {
                bool near = (pos - home).sqrMagnitude < 0.0144f && vel.sqrMagnitude < 0.04f;
                if (near || Time.time - returningSince > 8f)
                {
                    State = Mode.Home;
                    if (!announcedHome) { announcedHome = true; DemoActions.Raise("drone.home"); }
                }
            }
        }

        // ---- Pose -------------------------------------------------------------------------------------------------------------

        void ApplyPose(bool snap)
        {
            float dt = Time.deltaTime;
            Vector3 headPos = HeadPos();
            Vector3 toHead = headPos - pos; toHead.y = 0f;
            bool airborne = State >= Mode.TakingOff;
            if (toHead.sqrMagnitude > 1e-4f)
            {
                float target = Mathf.Atan2(toHead.x, toHead.z) * Mathf.Rad2Deg;
                yaw = snap ? target : Mathf.SmoothDampAngle(yaw, target, ref yawVel, State == Mode.Following ? 0.3f : 0.5f, 240f, dt);
            }

            // Bank into the move: tilt toward where it is going (nose down for forward), in the drone's own frame.
            Vector3 vLocal = Quaternion.Euler(0f, -yaw, 0f) * vel;
            Vector2 leanGoal = airborne ? new Vector2(Mathf.Clamp(vLocal.z * 8f, -14f, 14f), Mathf.Clamp(-vLocal.x * 8f, -14f, 14f)) : Vector2.zero;
            if (snap) leanNow = leanGoal;
            else
            {
                leanNow.x = Mathf.SmoothDamp(leanNow.x, leanGoal.x, ref leanVel.x, 0.25f, 200f, dt);
                leanNow.y = Mathf.SmoothDamp(leanNow.y, leanGoal.y, ref leanVel.y, 0.25f, 200f, dt);
            }

            Vector3 bob = Vector3.zero;
            float noiseP = 0f, noiseR = 0f;
            if (airborne)
            {
                bobT += dt;
                float amp = State == Mode.Following ? 0.008f : 0.014f;
                bob.y = Mathf.Sin(bobT * 1.7f) * amp + (Mathf.PerlinNoise(bobT * 0.6f, 3.1f) - 0.5f) * amp;
                bob.x = (Mathf.PerlinNoise(bobT * 0.5f, 7.7f) - 0.5f) * 0.01f;
                noiseP = (Mathf.PerlinNoise(bobT * 0.9f, 11.3f) - 0.5f) * 3f;
                noiseR = (Mathf.PerlinNoise(21.9f, bobT * 0.8f) - 0.5f) * 3f;
            }
            drone.position = pos + bob;
            drone.rotation = Quaternion.Euler(0f, yaw, 0f);
            lean.localRotation = Quaternion.Euler(leanNow.x + noiseP, 0f, leanNow.y + noiseR);
        }

        // ---- Scan shimmer ---------------------------------------------------------------------------------------------------------

        IEnumerator ScanRoutine(float seconds)
        {
            if (room == null || additive == null) { yield return new WaitForSeconds(seconds); yield break; }
            if (scanRoot == null)
            {
                scanRoot = new GameObject("Scan shimmer");
                scanWire = new Material(additive) { name = "scan wire" };
                scanWall = new Material(additive) { name = "scan wall" };
                scanBox = new Material(additive) { name = "scan box" };
            }
            foreach (Transform c in scanRoot.transform) Destroy(c.gameObject);
            AddScanLayer("Edges", room.Edges, scanWire, MeshTopology.Lines);
            AddScanLayer("Walls", room.WallFill, scanWall, MeshTopology.Triangles);
            AddScanLayer("Furniture", room.BoxFill, scanBox, MeshTopology.Triangles);
            scanRoot.SetActive(true);
            Vector3 head = HeadPos();
            float reach = Mathf.Max(room.FloorBounds.size.x, room.FloorBounds.size.z) + 2f;
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                float u = t / seconds;
                float r = Mathf.Lerp(0f, reach, u * (2f - u));
                float fade = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((u - 0.55f) / 0.45f));
                SetScan(scanWire, head, r, 0.7f, new Color(1f, 1f, 1f, 2.5f * fade));
                SetScan(scanWall, head, r, 0.7f, new Color(1f, 1f, 1f, 2f * fade));
                SetScan(scanBox, head, r, 0.7f, new Color(0.45f, 1f, 1f, 0.5f * fade));
                yield return null;
            }
            scanRoot.SetActive(false);
        }

        static void SetScan(Material m, Vector3 origin, float radius, float width, Color tint)
        {
            m.SetVector("_Scan", new Vector4(origin.x, origin.y, origin.z, radius));
            m.SetFloat("_ScanWidth", width);
            m.SetColor(ColorId, tint);
        }

        void AddScanLayer(string n, Mesh mesh, Material mat, MeshTopology topo)
        {
            var go = new GameObject(n, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(scanRoot.transform, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        // ---- Sound ---------------------------------------------------------------------------------------------------------------

        void PlaySfx(AudioClip clip, float volume)
        {
            if (sfxSrc == null || clip == null) return;
            sfxSrc.volume = volume;
            sfxSrc.PlayOneShot(clip, 1f);
        }
    }
}
