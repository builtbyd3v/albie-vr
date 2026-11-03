using System;
using UnityEngine;
using UnityEngine.AI;

namespace Forge.Pet
{
    /// <summary>
    /// Albie pet brain: a small timer-driven state machine over a NavMeshAgent + Animator.
    /// Public API per docs/archive/PET_AGENT_CONTRACT.md. Locomotion/animator helpers live in
    /// AlbieController.Motion.cs, surfaces/desk hop in AlbieController.Surfaces.cs, the crew area in
    /// AlbieController.Territory.cs. No allocations in Update.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [DisallowMultipleComponent]
    public partial class AlbieController : MonoBehaviour
    {
        [Header("States")]
        [SerializeField, Tooltip("State entered at Start (unless something called SetState earlier).")]
        AlbieState startState = AlbieState.Intro;
        [SerializeField, Tooltip("State used by FinishIntro(). The bridge decides when to leave Intro.")]
        AlbieState stateAfterIntro = AlbieState.Wandering;
        [SerializeField, Tooltip("Leave Intro automatically this many seconds after the greeting. 0 = wait for FinishIntro()/SetState().")]
        float autoLeaveIntroSeconds = 0f;
        [SerializeField, Tooltip("TaskComplete: seconds of spin before returning to Wandering.")]
        float spinSeconds = 1.2f;
        [SerializeField, Tooltip("Error: seconds of fear before returning to Wandering.")]
        float errorSeconds = 3f;
        [SerializeField, Tooltip("Future: Working morphs Albie into a blob (not implemented yet).")]
        bool workingBlobMorph = false;

        [Header("Working at a panel (pet.state target_panel)")]
        [SerializeField, Tooltip("Panels float at eye level: stand at the desk point nearest the panel, this far (m) in from the edge (plus deskEdgeInset).")]
        float panelStandOff = 0.10f;
        [SerializeField, Tooltip("Volumes sit on the desk (the rocket): stand beside them, this far (m) past their footprint.")]
        float volumeStandOff = 0.06f;
        [SerializeField, Tooltip("Emote played on arriving at the panel (\"\" = none).")]
        string workingEmote = "Bounce";

        [Header("Wandering")]
        [SerializeField, Tooltip("Optional wander center. Empty = around the user, or around the spawn point if there is no user.")]
        Transform wanderCenter;
        [SerializeField, Tooltip("Radius (m) of random wander points.")]
        float wanderRadius = 3f;
        [SerializeField, Range(0f, 1f), Tooltip("Chance a wander trip is a run instead of a walk.")]
        float runChance = 0.3f;
        [SerializeField, Tooltip("Idle pause between trips, seconds (min, max).")]
        Vector2 idlePause = new Vector2(1f, 4f);
        [SerializeField, Range(0f, 1f), Tooltip("Chance a pause plays Idle_B/Idle_C.")]
        float idleVariantChance = 0.35f;
        [SerializeField, Range(0f, 1f), Tooltip("Chance a pause plays Jump or Bounce.")]
        float emoteChance = 0.15f;

        [Header("User distances (m)")]
        [SerializeField, Tooltip("Intro/ComeToUser stop distance in front of the head.")]
        float frontDistance = 1.2f;
        [SerializeField, Tooltip("Following: distance beside/behind the user.")]
        float followDistance = 1f;
        [SerializeField, Tooltip("Re-path when the user moved more than this.")]
        float repathDistance = 0.5f;
        [SerializeField, Tooltip("Thinking/Working: come closer if farther than this.")]
        float nearUserDistance = 2f;

        public AlbieState State { get; private set; }
        public event Action<AlbieState> StateChanged;
        /// <summary>Raised once the intro greeting (arrive + Jump) has played.</summary>
        public event Action IntroComplete;

        AlbieExpressions expressions;
        Vector3 homePosition;
        float stateEnterTime, nextActionTime, approachStartTime, greetTime;
        bool started, tripActive, approachingUser, introGreeted, needRepath, workTripActive;
        Vector3 lastUserPos;
        string workTargetId;             // pet.state target_panel while Thinking / Working
        Transform workTarget;            // the open panel / volume it resolved to (PetWorkspace)
        Action workLanded;
        bool stationed, stationArrived;  // StationAt: Wandering stays at one spot (the arrival greeting)
        Vector3 stationPoint;
        Transform stationFace;

        /// <summary>Wandering is parked at a spot (StationAt) until ReleaseStation.</summary>
        public bool Stationed => stationed;

        float StateElapsed => Time.time - stateEnterTime;

        void Awake()
        {
            InitMotion();
            expressions = GetComponent<AlbieExpressions>();
            homePosition = transform.position;
        }

        void Start()
        {
            // The prefab saves the NavMeshAgent disabled: enabling it with no NavMesh around logs a warning, and in
            // MR the floor/desk NavMesh only exists once MRUK has loaded the room. AgentReady() enables it when a
            // NavMesh is within reach (sandbox: immediately, surfaces build in Awake).
            if (!started) SetState(startState);
        }

        // ---------------------------------------------------------------- public API

        public void SetState(AlbieState s)
        {
            if (started && s == State) return;
            started = true;
            State = s;
            StandUp();                          // any state change ends a sit/lay rest immediately
            EnterState(s);
            StateChanged?.Invoke(s);
        }

        /// <summary>
        /// SetState with a pet.state target_panel ("screen-main", "cad-viewer", "messages"): Thinking / Working walk (or
        /// hop) to the desk spot in front of that open panel, face it and play the working emote there; an unknown or
        /// closed panel falls back to the plain state. Same state again with a new panel re-targets.
        /// </summary>
        public void SetState(AlbieState s, string targetPanel)
        {
            bool busyState = s == AlbieState.Thinking || s == AlbieState.Working;
            workTargetId = busyState && !string.IsNullOrEmpty(targetPanel) ? targetPanel : null;
            if (started && s == State)
            {
                if (busyState && workTargetId != null && !hopping && !AnticsBusy && !BusyInTerritory) TryStartWorkTrip();
                return;
            }
            SetState(s);
        }

        /// <summary>Leave Intro to the serialized stateAfterIntro (no-op in other states).</summary>
        public void FinishIntro()
        {
            if (State == AlbieState.Intro) SetState(stateAfterIntro);
        }

        /// <summary>Path to ~frontDistance in front of PetUser.Head, then face the user.</summary>
        public void ComeToUser()
        {
            Transform head = PetUser.Head;
            if (head == null) return;
            StandUp();
            ComeToUserInternal(!OnDesk && FlatDistance(head.position) > 3f);
        }

        /// <summary>
        /// Wandering, parked: walk to <paramref name="point"/> (on the current surface, keep-out safe) and stay there,
        /// facing <paramref name="face"/> (null = the user) and fidgeting in place, until <see cref="ReleaseStation"/>.
        /// Survives a detour through other states (a question answered mid-greeting): back in Wandering it walks back.
        /// The arrival greeting uses it; plain Wandering, not Listening, so the mood and the mic are untouched.
        /// </summary>
        public void StationAt(Vector3 point, Transform face = null)
        {
            stationed = true;
            stationArrived = false;
            stationPoint = point;
            stationFace = face;
            if (hopping) return;                      // Land() -> Wandering -> UpdateStation walks there
            SetState(AlbieState.Wandering);           // no-op if already Wandering: start the walk ourselves
            StandUp();
            tripActive = approachingUser = false;
            faceTarget = null; faceUser = false;
            nextActionTime = Time.time;
        }

        /// <summary>Leave the station spot: ordinary wandering resumes with the usual pause first.</summary>
        public void ReleaseStation()
        {
            if (!stationed) return;
            stationed = stationArrived = false;
            if (faceTarget == stationFace) faceTarget = null;
            stationFace = null;
            faceUser = false;
            tripActive = false;
            nextActionTime = Time.time + UnityEngine.Random.Range(idlePause.x, idlePause.y);
        }

        /// <summary>
        /// Wandering, with the next trip chosen: walk to <paramref name="point"/> (clamped onto the current surface, keep-out
        /// safe), then carry on wandering as usual. Releases a station. E.g. "move over to the right of the desk" after START.
        /// </summary>
        public void WanderTo(Vector3 point)
        {
            if (hopping) return;
            ReleaseStation();
            SetState(AlbieState.Wandering);
            faceTarget = null; faceUser = false; approachingUser = false;
            StandUp();
            if (OnDesk) point = KeepOutSafePoint(CurrentSurface, point);
            if (MoveTo(point, false))
            {
                tripActive = true;
                nextActionTime = Time.time + 10f;     // trip timeout, then the normal idle pause
            }
        }

        /// <summary>
        /// "Done talking": Wandering, stop facing the user and walk (back turned) to the farthest of a few wander points
        /// from the user, then idle there as usual. Makes it obvious Albie stopped listening. Parked (StationAt): stays.
        /// </summary>
        public void WanderAway()
        {
            if (hopping) return;
            SetState(AlbieState.Wandering);           // no-op if already Wandering, so reset the bits below ourselves
            if (stationed) return;                    // the greeting spot wins: UpdateStation keeps / brings him there
            faceTarget = null; faceUser = false; approachingUser = false;
            SetMood(Mood.Neutral);
            Transform head = PetUser.Head;
            Vector3 best = transform.position;
            float bestSq = -1f;
            for (int i = 0; i < 4; i++)
            {
                if (!TryPickWanderPoint(out Vector3 p)) continue;
                Vector3 from = head != null ? head.position : transform.position;
                float dx = p.x - from.x, dz = p.z - from.z;
                if (dx * dx + dz * dz > bestSq) { bestSq = dx * dx + dz * dz; best = p; }
            }
            if (bestSq >= 0f && MoveTo(best, false))
            {
                tripActive = true;
                nextActionTime = Time.time + 10f;     // trip timeout, then the normal idle pause
            }
        }

        // ---------------------------------------------------------------- state machine

        void EnterState(AlbieState s)
        {
            stateEnterTime = Time.time;
            tripActive = approachingUser = introGreeted = workTripActive = false;
            stationArrived = false;             // StationAt: back in Wandering he walks to / turns to the spot again
            evacuating = false;                 // a state change cancels a keep-out run (re-checked within 0.5 s)
            needRepath = true;
            nextActionTime = Time.time + UnityEngine.Random.Range(idlePause.x, idlePause.y);
            if (s != AlbieState.Thinking && s != AlbieState.Working) ClearWorkTarget();

            switch (s)
            {
                case AlbieState.Intro:
                    faceUser = false;
                    ComeToUserInternal(true);
                    break;
                case AlbieState.Wandering:
                    faceTarget = null; faceUser = false;
                    StopMoving(); SetMood(Mood.Neutral);
                    break;
                case AlbieState.Following:
                    faceUser = true; SetMood(Mood.Neutral);
                    if (OnDesk) HopToFloor();          // contract: follow = floor following
                    break;
                case AlbieState.Listening:
                    StopMoving(); faceUser = true; SetMood(Mood.Attentive);
                    break;
                case AlbieState.Thinking:
                case AlbieState.Working:
                    faceUser = true; SetMood(Mood.Squint);
                    if (BusyInTerritory) GoToTerritoryFront();       // crew: work at the front of its own area
                    else if (TryStartWorkTrip()) { }                  // Albie: to the desk spot in front of target_panel
                    else if (IsFarFromUser(nearUserDistance)) ComeToUser(); else StopMoving();
                    if (s == AlbieState.Working && workingBlobMorph)
                    {
                        // TODO(blob): morph into the blob and arc-jump into the panel instead of walking there.
                    }
                    break;
                case AlbieState.Answering:
                    StopMoving(); faceUser = true;
                    PlayEmote("Bounce"); SetMood(Mood.Happy);
                    // TODO(lipsync): hook ITextToSpeech.SpeechStarted/Ended here (mouth/jaw anim while speaking).
                    break;
                case AlbieState.TaskComplete:
                    StopMoving(); PlayEmote("Spin"); SetMood(Mood.Excited);
                    break;
                case AlbieState.Error:
                    StopMoving(); PlayEmote("Fear"); SetMood(Mood.Sad);
                    break;
            }
        }

        void Update()
        {
            if (!started) return;
            if (hopping) { UpdateHop(); UpdateMotion(); return; }   // state logic resumes after landing
            if (UpdateAntics()) { UpdateMotion(); return; }        // roll / fall / stun (AlbieController.Antics.cs)
            AutoDetectSurface();
            if (UpdateKeepOut()) { UpdateMotion(); return; }   // running out of the desk keep-out (AlbieController.KeepOut.cs)
            switch (State)
            {
                case AlbieState.Intro: UpdateIntro(); break;
                case AlbieState.Wandering: UpdateWander(); break;
                case AlbieState.Following: UpdateFollow(); break;
                case AlbieState.Thinking:
                case AlbieState.Working: UpdateFidget(); break;
                case AlbieState.TaskComplete: if (StateElapsed >= spinSeconds) SetState(AlbieState.Wandering); break;
                case AlbieState.Error: if (StateElapsed >= errorSeconds) SetState(AlbieState.Wandering); break;
            }
            UpdateApproach();
            UpdateMotion();
        }

        void UpdateIntro()
        {
            if (introGreeted)
            {
                if (autoLeaveIntroSeconds > 0f && Time.time - greetTime >= autoLeaveIntroSeconds) FinishIntro();
                return;
            }
            Transform head = PetUser.Head;
            if (head == null || !approachingUser)
            {
                // No user (yet) or no path: retry, greet in place after a short grace period.
                if (head != null && ComeToUserInternal(true)) return;
                if (StateElapsed > 2f) GreetUser();
                return;
            }
            if (UserMoved(head)) ComeToUserInternal(true);
            if (HasArrived() || StateElapsed > 12f) GreetUser();
        }

        void GreetUser()
        {
            StopMoving();
            approachingUser = false;
            faceUser = true;
            PlayEmote("Jump");
            SetMood(Mood.Happy);
            introGreeted = true;
            greetTime = Time.time;
            IntroComplete?.Invoke();
        }

        void UpdateWander()
        {
            if (approachingUser) return;
            if (stationed) { UpdateStation(); return; }
            if (tripActive)
            {
                if (HasArrived() || Time.time >= nextActionTime) { tripActive = false; StopMoving(); BeginPause(); }
                return;
            }
            // Outside its own area (just hatched, after a summon): walk home now, rest once there.
            bool homesick = HasTerritory && OnTerritorySurface && !InTerritory(transform.position, 0.05f);
            if (!homesick && Time.time < nextActionTime) return;
            if (ReturnToTerritory()) return;          // crew: on the wrong surface, head home first

            bool run = UnityEngine.Random.value < runChance;
            if (TryPickWanderPoint(out Vector3 p) && MoveTo(p, run))
            {
                faceUser = false;                     // a hop (HopToUserDesk) left him facing the user: the first trip ends that
                tripActive = true;
                nextActionTime = Time.time + 10f;     // trip timeout
            }
            else BeginPause();
        }

        // StationAt: get to the spot (walk, or re-walk after a keep-out scoot / a detour through another state), then
        // stand there facing stationFace / the user, with an idle variant now and then. No rests: he is "presenting".
        void UpdateStation()
        {
            if (tripActive)
            {
                if (!HasArrived() && Time.time < nextActionTime) return;
                tripActive = false;
                StopMoving();
                stationArrived = true;
                faceTarget = stationFace;
                faceUser = stationFace == null;
                nextActionTime = Time.time + UnityEngine.Random.Range(3f, 6f);
                return;
            }
            if (!stationArrived || FlatDistance(stationPoint) > 0.12f)
            {
                if (Time.time < nextActionTime) return;
                Vector3 goal = OnDesk ? KeepOutSafePoint(CurrentSurface, stationPoint) : stationPoint;
                if (FlatDistance(goal) < 0.06f || !MoveTo(goal, false))
                {
                    stationArrived = true;            // there already, or no path: present from here
                    faceTarget = stationFace;
                    faceUser = stationFace == null;
                    nextActionTime = Time.time + UnityEngine.Random.Range(3f, 6f);
                    return;
                }
                StandUp();
                tripActive = true;
                nextActionTime = Time.time + 10f;
                return;
            }
            if (Time.time < nextActionTime) return;
            nextActionTime = Time.time + UnityEngine.Random.Range(3f, 6f);
            if (UnityEngine.Random.value < 0.5f) PlayIdleVariant();
        }

        // Idle pause between trips: often a long rest (sit / lay / idle, see Antics.cs), else a short pause
        // with an occasional fidget or emote.
        void BeginPause()
        {
            if (HasTerritory && !InTerritory(transform.position, 0.05f)) { nextActionTime = Time.time + 0.3f; return; }   // not home yet
            if (TryBeginRest()) return;
            nextActionTime = Time.time + UnityEngine.Random.Range(idlePause.x, idlePause.y);
            float r = UnityEngine.Random.value;
            if (r < emoteChance) PlayEmote(UnityEngine.Random.value < 0.5f ? "Jump" : "Bounce");
            else if (r < emoteChance + idleVariantChance) PlayIdleVariant();
        }

        void UpdateFollow()
        {
            Transform head = PetUser.Head;
            if (head == null) { UpdateWander(); return; }
            if (!needRepath && !UserMoved(head)) return;

            needRepath = false;
            Vector3 userPos = Ground(head.position);
            lastUserPos = userPos;
            Vector3 fwd = FlatForward(head);
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
            Vector3 goal = OnDesk ? UserGoal(head)   // stuck on the desk (no floor): desk edge nearest the user
                                  : userPos + (right * 0.6f - fwd * 0.8f).normalized * followDistance;
            float dist = FlatDistance(goal);
            if (dist > 0.3f) MoveTo(goal, dist > 3f);
        }

        // Thinking / Working: stay near the user (or at the target panel), idle with occasional variants.
        void UpdateFidget()
        {
            if (approachingUser) return;
            if (!BusyInTerritory && workTarget == null && IsFarFromUser(nearUserDistance + 1f)) { ComeToUser(); return; }
            if (Time.time < nextActionTime) return;
            nextActionTime = Time.time + UnityEngine.Random.Range(3f, 6f);
            if (UnityEngine.Random.value < 0.5f) PlayIdleVariant();
        }

        // Finish a ComeToUser / panel trip: stop and face the user (the panel wins while workTarget is set).
        void UpdateApproach()
        {
            if (!approachingUser || State == AlbieState.Intro) return;
            if (HasArrived() || Time.time - approachStartTime > 12f)
            {
                StopMoving();
                approachingUser = false;
                faceUser = true;
                nextActionTime = Time.time + UnityEngine.Random.Range(idlePause.x, idlePause.y);
                if (workTripActive) { workTripActive = false; ArriveAtWorkTarget(); }
            }
        }

        // ---------------------------------------------------------------- working at a panel

        // Resolve workTargetId to an open panel / volume and head for the desk spot in front of it. False = nothing to do
        // (no target, panel not open, no desk): the caller falls back to the usual come-to-user.
        bool TryStartWorkTrip()
        {
            if (workTargetId == null) return false;
            if (!PetWorkspace.TryGetItemTransform(workTargetId, out Transform t))
            {
                Debug.Log("[AlbieController] target_panel '" + workTargetId + "' isn't open; staying with the user.", this);
                workTarget = null;
                return false;
            }
            workTarget = t;
            faceTarget = t;
            faceUser = false;
            AlbieNavSurface desk = AlbieNavSurface.Desk;
            if (desk == null || !desk.IsBuilt) return false;    // no desk to stand on: at least face it

            Vector3 goal = PanelStandPoint(desk, t);
            if (!OnDesk)
            {
                if (hopping) return true;
                if (workLanded == null) workLanded = ArriveAtWorkTarget;
                HopTo(desk, goal, workLanded);
                return hopping;
            }
            if (FlatDistance(goal) < 0.06f) { StopMoving(); ArriveAtWorkTarget(); return true; }
            if (!MoveTo(goal, false)) return false;
            approachingUser = true;             // UpdateApproach finishes the trip
            approachStartTime = Time.time;
            workTripActive = true;
            return true;
        }

        void ArriveAtWorkTarget()
        {
            if (workTarget == null) return;
            faceTarget = workTarget;
            if (!string.IsNullOrEmpty(workingEmote)) PlayEmote(workingEmote);
        }

        void ClearWorkTarget()
        {
            workTargetId = null;
            if (workTarget != null && faceTarget == workTarget) faceTarget = null;
            workTarget = null;
        }

        // Desk spot for an open item: volumes (the rocket) sit on the desk, so stand beside them on the side nearer the
        // Albie; panels float at eye level, so stand at the desk point nearest / under them, pulled in from the edge
        // (screen-main: front-centre; messages: the right edge beside the card stack). Keep-out safe.
        Vector3 PanelStandPoint(AlbieNavSurface desk, Transform item)
        {
            Vector3 p = item.position;
            ForgeVolume volume = item.GetComponent<ForgeVolume>();
            if (volume != null)
            {
                float half = 0.5f * Mathf.Max(volume.size.x * Mathf.Abs(item.lossyScale.x), volume.size.z * Mathf.Abs(item.lossyScale.z), 0.1f);
                Vector3 right = desk.Rotation * Vector3.right;
                Vector3 a = p + right * (half + volumeStandOff), b = p - right * (half + volumeStandOff);
                p = desk.ClosestPoint(FlatDistance(a) <= FlatDistance(b) ? a : b, deskEdgeInset);
            }
            else p = desk.ClosestPoint(p, deskEdgeInset + panelStandOff);
            return KeepOutSafePoint(desk, p);
        }

        bool ComeToUserInternal(bool run)
        {
            Transform head = PetUser.Head;
            if (head == null) return false;
            if (!MoveTo(UserGoal(head), run)) return false;
            approachingUser = true;
            approachStartTime = Time.time;
            faceUser = true;
            lastUserPos = Ground(head.position);
            return true;
        }

        bool UserMoved(Transform head) =>
            (Ground(head.position) - lastUserPos).sqrMagnitude > repathDistance * repathDistance;

        bool IsFarFromUser(float meters)
        {
            Transform head = PetUser.Head;
            if (head == null) return false;
            // On the desk "near" is measured to the desk point nearest the head, with the same hysteresis ratio.
            if (OnDesk) return FlatDistance(UserGoal(head)) > deskNearDistance * meters / Mathf.Max(0.01f, nearUserDistance);
            return FlatDistance(head.position) > meters;
        }

        void SetMood(Mood m)
        {
            if (expressions != null) expressions.SetMood(m);
        }
    }
}
