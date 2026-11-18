using System.Collections.Generic;
using Forge.Controllers;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>
    /// The drone as the Thing a course scores: the peer of the brain truck (<see cref="SlalomBrain"/>) for a manifest whose
    /// <c>links.thing</c> is <c>"drone"</c> (<see cref="ArtifactBootstrap.EnsureDrone"/>). One object, built in code:
    /// - <see cref="DroneBody"/> flies it (height hold, self-levelling, soft floor and ceiling, take-off, landing, crash);
    /// - <see cref="ControllerThing"/> runs its controller (<c>drone_manual</c>: the four manual inputs to the four
    ///   actuators) and registers it with <see cref="ThingHosts"/> as <c>drone</c>;
    /// - this component is the run's probes (<see cref="IArtifactThing"/>) and follows the host: at the course's start
    ///   when a drone course is built, hidden and unbound when the host shows anything else.
    /// Between runs: whenever the host arms a run (the panel's "Fly again", <c>artifact_run</c>) the drone is put back at
    /// the start, landed. <see cref="TakeOff"/> from the start arms a run by itself when none is armed, so the first
    /// flight counts. A crash ends on its back (<see cref="Upright"/> below zero, for a course's own fail rule); a run
    /// still going <see cref="CrashAbortSeconds"/> later is aborted.
    /// </summary>
    public sealed class ArtifactDrone : MonoBehaviour, IArtifactThing
    {
        public const string ThingName = "drone";
        public const int ThingVersion = 1;
        public const string ManualResource = "Controllers/drone_manual";
        /// <summary>A run still armed or running this long after a crash is aborted (a course's own fail rule gets the time before).</summary>
        public const float CrashAbortSeconds = 1.5f;
        /// <summary>Taking off within this of the start (metres) arms a run when none is armed.</summary>
        public const float StartRadius = 0.3f;

        static ArtifactDrone instance;

        /// <summary>The drone on the open course, or null: none spawned yet, or it is hidden because the host shows something else.</summary>
        public static ArtifactDrone Live => instance != null && !instance.Hidden ? instance : null;

        public DroneBody Body { get; private set; }
        public ControllerThing Thing { get; private set; }
        public bool Hidden { get; private set; }
        /// <summary>The body's look from the project's quadcopter CAD design (the primitive shell when there is none).</summary>
        public DroneDesign Design { get; private set; }
        /// <summary>Follow-me (<see cref="Follow"/>).</summary>
        public DroneFollow Follower { get; private set; }
        /// <summary>The nose camera (<see cref="SetCam"/>), off by default.</summary>
        public DroneCam Cam { get; private set; }
        /// <summary>The open course's scale the drone was sized for (1 on the floor, smaller on a desk course).</summary>
        public float CourseScale { get; private set; } = 1f;

        ArtifactHost host;
        string armedId;
        float crashedFor;
        readonly int stickX = ManualInputs.IndexOf("stick.x"), stickY = ManualInputs.IndexOf("stick.y"),
                     stick2X = ManualInputs.IndexOf("stick2.x"), stick2Y = ManualInputs.IndexOf("stick2.y");

        // ---- The probes a run reads (IArtifactThing) -------------------------------------------------------------------

        public string Name => ThingName;
        public int Version => ThingVersion;
        public string ControllerName => Thing != null && Thing.Host != null && Thing.Host.Current != null ? Thing.Host.Current.Name : "drone_manual";
        public string ControllerKind => Thing != null && Thing.Host != null && Thing.Host.Current != null ? Thing.Host.Current.Kind : "manual";
        public int ControllerVersion => Thing != null && Thing.Host != null && Thing.Host.File != null ? Thing.Host.File.Version : 1;
        /// <summary>The body's centre. It moves in FixedUpdate, where runs tick.</summary>
        public Vector3 Position => transform.position;
        /// <summary>The body's own speed, all three axes: it is stepped in FixedUpdate, so it never reads 0 between frames.</summary>
        public float SpeedMs => Body.Speed;
        public float Upright => Body.Upright;
        /// <summary>Height of the body's centre above the room's floor, metres (about 0.05 when it stands on the floor).</summary>
        public float HeightM => Body.HeightAboveFloor;
        public float Radius => Body.radius;
        public bool Bound => isActiveAndEnabled && !Hidden;

        // ---- Spawning --------------------------------------------------------------------------------------------------

        /// <summary>Makes the drone and binds it to <paramref name="host"/>'s runs. It then follows the host by itself.</summary>
        public static ArtifactDrone Spawn(ArtifactHost host)
        {
            var go = new GameObject("Drone");
            go.SetActive(false);  // so ControllerThing's Awake sees the name and the body
            var body = go.AddComponent<DroneBody>();
            var drone = go.AddComponent<ArtifactDrone>();
            drone.Body = body;
            drone.host = host;
            drone.Thing = go.AddComponent<ControllerThing>();
            drone.Thing.thingName = ThingName;
            drone.Thing.thingVersion = ThingVersion;
            go.SetActive(true);

            // The fallback look, until (or unless) the quadcopter design loads. Three shared materials: dark body, orange
            // nose and front props, pale back props.
            var shell = new GameObject("Shell").transform;
            shell.SetParent(body.Visual, false);
            body.SetProps(DroneShell.Build(shell, ArtifactLook.Material("#2B2F36"), ArtifactLook.Material("#FF7A1A"),
                                           ArtifactLook.Material("#D5DAE0"), body.restHeight));
            drone.Design = go.AddComponent<DroneDesign>();
            drone.Design.Init(body, shell, null);
            drone.Design.Changed += drone.Retune;
            drone.Follower = go.AddComponent<DroneFollow>();
            drone.Follower.Init(body);
            drone.Cam = go.AddComponent<DroneCam>();
            drone.Cam.Init(drone.Design.Nose, body);

            string error = "no Resources/" + ManualResource;
            var asset = Resources.Load<TextAsset>(ManualResource);
            if (asset != null)
            {
                var file = ControllerFile.Parse(asset.text, out error);
                if (file != null) error = drone.Thing.Host.Attach(file);
            }
            if (error != null)
            {
                // The file is the contract's way; without it the drone still flies on the same four inputs.
                Debug.LogWarning("[Artifacts] drone_manual did not attach (" + error + "); flying on the built-in sticks.");
                drone.Thing.Host.Attach(new DroneSticks());
            }

            instance = drone;
            host.Shown += drone.OnHostShown;
            host.Closed += drone.OnHostClosed;
            drone.OnHostShown(host);
            return drone;
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
            if (host == null) return;
            host.Shown -= OnHostShown;
            host.Closed -= OnHostClosed;
            if (ReferenceEquals(host.Thing, this)) host.Thing = null;
        }

        /// <summary>True for a manifest that links the drone.</summary>
        public static bool IsDroneCourse(ArtifactHost h) => h != null && (string)h.OpenManifest?["links"]?["thing"] == ThingName;

        // The host built a scene: a drone course gets the drone at its start, as the host's Thing. Anything else hides it,
        // so it isn't scored as that scene's Thing and isn't left hovering beside it.
        void OnHostShown(ArtifactHost h)
        {
            if (!IsDroneCourse(h)) { OnHostClosed(h); return; }
            h.Thing = this;
            if (h.Run != null) h.Run.SetThing(this);
            Hide(false);
            var plan = h.Room ?? MrukFloorPlan.Current();
            if (plan != null && plan.HasFloor) Body.SetRoom(plan.FloorY, plan.FloorY + plan.CeilingHeight);
            else Body.SetRoom(h.SceneOrigin.position.y, float.NaN);
            if (Cam != null) Cam.Room = plan;
            // A desk course is a miniature (1/4 to 1/10): the drone, its speeds and its limits shrink with it.
            CourseScale = Mathf.Clamp(h.CourseScale, 0.05f, 1f);
            Retune();
            // Every build starts from the start: a rebuild (a slider on the course's panel) tears the scene down first,
            // which hides the drone, and the course may have moved.
            PlaceAtStart();
            armedId = h.Run != null ? h.Run.RunId : null;
        }

        void OnHostClosed(ArtifactHost h)
        {
            if (ReferenceEquals(h.Thing, this)) h.Thing = null;
            Hide(true);
        }

        void Hide(bool hidden)
        {
            if (Hidden == hidden && Body.enabled == !hidden) return;
            Hidden = hidden;
            if (hidden)
            {
                if (Follower != null) Follower.End("hidden");
                if (Cam != null) Cam.SetOn(false);
            }
            // The whole look (shell or design, and the nose camera) goes with the Visual.
            if (Body.Visual != null) Body.Visual.gameObject.SetActive(!hidden);
            // Off: no physics step, not in ThingHosts, not DroneBody.Active, and nothing reads the sticks for it.
            Thing.enabled = !hidden;
            Body.enabled = !hidden;
            if (hidden) ZeroSticks();
        }

        // The course's scale and the design's factors into the body's tuning (from its own base values: never compounds).
        void Retune()
        {
            var d = Design;
            bool loaded = d != null && d.Loaded;
            Body.Retune(CourseScale, loaded ? d.RadiusFullSize : 0f, loaded ? d.AccelK : 1f, loaded ? d.BrakeK : 1f,
                        loaded ? d.ClimbK : 1f, loaded ? d.YawK : 1f);
            Body.ThrustToWeight = loaded ? d.ThrustToWeight : float.NaN;
        }

        void ZeroSticks()
        {
            var v = ManualInputs.Values;
            if (stickX >= 0) v[stickX] = 0f;
            if (stickY >= 0) v[stickY] = 0f;
            if (stick2X >= 0) v[stick2X] = 0f;
            if (stick2Y >= 0) v[stick2Y] = 0f;
        }

        // ---- Commands (the Drive window's buttons and Albie's drone_* tools) -------------------------------------------

        /// <summary>The course's start: where the truck would stand, on the floor (or the desk) under the start zone.</summary>
        public Pose Start => host != null ? host.SceneOrigin : new Pose(transform.position, transform.rotation);

        /// <summary>Back at the start, landed, stopped, whole. The controller's state is cleared.</summary>
        public void PlaceAtStart()
        {
            var at = Start;
            Body.Place(at.position, at.rotation);
            if (Thing.Host != null) Thing.Host.ResetState();
            crashedFor = 0f;
        }

        /// <summary>
        /// Takes off. From the start of a scored course with no run armed (never armed, or the last one finished), it arms
        /// one first, so the flight is scored. Returns null, or why it did not take off.
        /// </summary>
        public string TakeOff()
        {
            if (Hidden) return "no_drone: open a drone course first";
            var run = host != null ? host.Run : null;
            if (run != null && Body.State == DroneBody.Phase.Landed
                && (run.State == ArtifactRunState.Idle || run.State == ArtifactRunState.Finished) && AtStart())
            {
                host.Thing = this;
                if (host.ArmRun() == null) armedId = run.RunId;  // ours: Update must not put the drone back down for it
            }
            return Body.TakeOff();
        }

        bool AtStart()
        {
            var d = transform.position - Start.position;
            d.y = 0f;
            return d.sqrMagnitude <= StartRadius * StartRadius;
        }

        /// <summary>Lands where it is, or with <paramref name="home"/> flies back over the start first.</summary>
        public string Land(bool home = false)
        {
            if (Hidden) return "no_drone: open a drone course first";
            return home ? Body.ComeHome() : Body.Land();
        }

        /// <summary>The safe stop: the four stick inputs are zeroed and it brakes to a hover where it is.</summary>
        public void Stop()
        {
            ZeroSticks();
            Body.Hold();
        }

        /// <summary>
        /// Follow-me: takes off first if it is on the ground, then keeps <paramref name="distance"/> metres from the headset at
        /// <paramref name="height"/> metres above the floor (0 or less: just under head height), facing the user, steering
        /// round the scanned furniture. Refused on a desk course (a miniature). Returns null, or why not.
        /// </summary>
        public string Follow(float distance, float height)
        {
            if (Hidden) return "no_drone: open a drone course first";
            if (CourseScale < 0.99f) return "desk_course: follow-me is for a course on the floor; this drone is a miniature on a desk course.";
            if (Body.Crashed) return "crashed: reset the drone first";
            var plan = host != null && host.Room != null ? host.Room : MrukFloorPlan.Current();
            if (Body.State == DroneBody.Phase.Landed || Body.State == DroneBody.Phase.Landing)
            {
                if (plan == null && !Application.isEditor)
                    return "no_room_data: follow-me needs the room scan to steer round your furniture. Scan the room, then try again.";
                string error = TakeOff();
                if (error != null) return error;
            }
            ZeroSticks();
            return Follower.Begin(distance, height, plan);
        }

        /// <summary>Ends follow-me: it brakes and hovers where it is.</summary>
        public void StopFollow()
        {
            if (Follower != null) Follower.End("stopped");
        }

        /// <summary>Switches the nose camera on or off (the drone cam window shows it).</summary>
        public void SetCam(bool on)
        {
            if (Cam != null && !Hidden) Cam.SetOn(on);
        }

        /// <summary>Follow-me's state in one word: off, starting, following, too_close (holding), blocked (holding).</summary>
        public string FollowState
        {
            get
            {
                if (Follower == null) return "off";
                switch (Follower.State)
                {
                    case DroneFollow.Mode.Starting: return "starting";
                    case DroneFollow.Mode.Following: return "following";
                    case DroneFollow.Mode.TooClose: return "too_close";
                    case DroneFollow.Mode.Blocked: return "blocked";
                    default: return "off";
                }
            }
        }

        /// <summary>Flat distance from the drone to the headset, metres, or NaN with no headset camera.</summary>
        public float UserDistance
        {
            get
            {
                var cam = Camera.main;
                if (cam == null) return float.NaN;
                var d = cam.transform.position - transform.position;
                d.y = 0f;
                return d.magnitude;
            }
        }

        /// <summary>Drops the run in progress, if any, and puts the drone back at the start. The next take-off arms a new run.</summary>
        public void ResetToStart()
        {
            var run = host != null ? host.Run : null;
            if (run != null) run.Abort();
            ZeroSticks();
            PlaceAtStart();
        }

        /// <summary>The open course's run state for a status line: idle, armed, running or finished; null with no scored course.</summary>
        public string RunState
        {
            get
            {
                var run = host != null ? host.Run : null;
                if (run == null) return null;
                switch (run.State)
                {
                    case ArtifactRunState.Armed: return "armed";
                    case ArtifactRunState.Running: return "running";
                    case ArtifactRunState.Finished: return "finished";
                    default: return "idle";
                }
            }
        }

        /// <summary>0 with no scored course, else 1 idle, 2 armed, 3 running, 4 finished: a number a status line can key on.</summary>
        public int RunKey => host != null && host.Run != null ? 1 + (int)host.Run.State : 0;

        /// <summary>The run's outcome once finished ("completed", "failed", "aborted"), else null.</summary>
        public string RunOutcome => host != null && host.Run != null && host.Run.State == ArtifactRunState.Finished ? host.Run.Outcome : null;

        // ---- The frame -------------------------------------------------------------------------------------------------

        void Update()
        {
            if (Hidden) return;
            var run = host != null ? host.Run : null;
            if (run == null) { armedId = null; return; }

            // Someone armed a run (the panel's "Fly again", artifact_run): the drone starts it from the start, landed.
            string id = run.RunId;
            if (!ReferenceEquals(id, armedId))
            {
                armedId = id;
                if (run.State == ArtifactRunState.Armed || run.State == ArtifactRunState.Running) PlaceAtStart();
            }

            if (!Body.Crashed) { crashedFor = 0f; return; }
            crashedFor += Time.deltaTime;
            if (crashedFor >= CrashAbortSeconds && (run.State == ArtifactRunState.Armed || run.State == ArtifactRunState.Running))
                run.Abort();
        }

        /// <summary>The drone's state for <c>drone_status</c> and the other tools' results.</summary>
        public JObject ToolState()
        {
            var away = transform.position - Body.Home;
            away.y = 0f;
            return new JObject
            {
                ["thing"] = ThingName,
                ["state"] = PhaseName(Body.State),
                ["height_m"] = System.Math.Round(Body.HeightAboveFloor, 2),
                ["speed_ms"] = System.Math.Round(Body.Speed, 2),
                ["from_start_m"] = System.Math.Round(away.magnitude, 2),
                ["flying_home"] = Body.Returning,
                ["bumps"] = Body.Bumps,
                ["upright"] = System.Math.Round(Body.Upright, 2),
                ["controller"] = ControllerName,
                ["follow"] = FollowState,
                ["follow_distance_m"] = Follower != null && Follower.Active ? System.Math.Round(Follower.Distance, 2) : (double?)null,
                ["follow_ended"] = Follower != null && !Follower.Active ? Follower.EndedWhy : null,
                ["user_distance_m"] = float.IsNaN(UserDistance) ? (double?)null : System.Math.Round(UserDistance, 2),
                ["camera_on"] = Cam != null && Cam.On,
                ["body"] = Design != null && Design.Loaded ? "quadcopter v" + Design.Version + " (" + Design.Source + ")" : "built-in shape (no quadcopter design loaded)",
                ["design_flight"] = Design != null && Design.Loaded ? Design.Note : null,
                ["course_scale"] = System.Math.Round(CourseScale, 3),
                ["course"] = host != null ? host.OpenId : null,
                ["run"] = RunState,
                ["run_outcome"] = RunOutcome,
            };
        }

        public static string PhaseName(DroneBody.Phase phase)
        {
            switch (phase)
            {
                case DroneBody.Phase.TakingOff: return "taking_off";
                case DroneBody.Phase.Flying: return "flying";
                case DroneBody.Phase.Landing: return "landing";
                case DroneBody.Phase.Crashed: return "crashed";
                default: return "landed";
            }
        }
    }

    /// <summary>
    /// The drone's manual controller in code, used only when <c>Resources/Controllers/drone_manual.json</c> is missing or
    /// refused: the same four manual inputs to the same four actuators, with a small dead zone.
    /// </summary>
    public sealed class DroneSticks : IThingController
    {
        const float Deadzone = 0.1f;
        readonly int stickX = ManualInputs.IndexOf("stick.x"), stickY = ManualInputs.IndexOf("stick.y"),
                     stick2X = ManualInputs.IndexOf("stick2.x"), stick2Y = ManualInputs.IndexOf("stick2.y");
        int forward, right, climb, yaw;

        public string Kind => "manual";
        public string Name => "drone_manual";

        public string Bind(ControllerIO io)
        {
            forward = io.ActuatorIndex("fly.forward");
            right = io.ActuatorIndex("fly.right");
            climb = io.ActuatorIndex("fly.climb");
            yaw = io.ActuatorIndex("fly.yaw");
            if (forward < 0 || right < 0 || climb < 0 || yaw < 0) return "unknown_channel: this Thing has no fly.* actuators (it has " + io.DescribeActuators() + ")";
            if (stickX < 0 || stickY < 0 || stick2X < 0 || stick2Y < 0) return "unknown_channel: the manual inputs have no stick and stick2";
            return null;
        }

        public void Tick(ControllerIO io, float dt)
        {
            float[] v = ManualInputs.Values, a = io.Actuators;
            a[forward] = Dead(v[stickY]);
            a[right] = Dead(v[stickX]);
            a[climb] = Dead(v[stick2Y]);
            a[yaw] = Dead(v[stick2X]);
        }

        static float Dead(float v)
        {
            float size = v < 0f ? -v : v;
            if (size <= Deadzone) return 0f;
            size = (size - Deadzone) / (1f - Deadzone);
            return v < 0f ? -size : size;
        }

        public void ResetState() { }
    }

    /// <summary>
    /// Albie's tools for the drone (<c>Resources/AlbieTools/drone.json</c>): take off, land (or come home and land), the
    /// safe stop, reset to the start, and status. They fly a simulated drone in the room and can all be undone, so none
    /// needs a confirm step. With no drone on a course they say so and what to open.
    /// </summary>
    public static class DroneTools
    {
        const string NoDrone = "no_drone: there is no drone yet. Open a drone course first (a course whose Thing is the drone, such as the hoop course); the drone then stands at its start.";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register() => AlbieTools.Register("drone", Handlers());

        /// <summary>The handlers by tool name (public for checks).</summary>
        public static Dictionary<string, AlbieTools.Handler> Handlers() => new Dictionary<string, AlbieTools.Handler>
        {
            ["drone_takeoff"] = args => Do(d => d.TakeOff(), "Taking off. It climbs to a hover and holds there."),
            ["drone_land"] = args =>
            {
                bool home = (bool?)args?["home"] ?? false;
                return Do(d => d.Land(home), home ? "Flying back to the start to land." : "Landing where it is.");
            },
            ["drone_stop"] = args => Do(d => { d.Stop(); return null; }, "Stopped: it hovers where it is."),
            ["drone_reset"] = args => Do(d => { d.ResetToStart(); return null; }, "The drone is back at the start, landed."),
            ["drone_follow"] = args =>
            {
                float distance = (float?)args?["distance_m"] ?? 2f;
                float height = (float?)args?["height_m"] ?? 0f;
                return Do(d => d.Follow(distance, height),
                    "Following you, " + Mathf.Clamp(distance, DroneFollow.MinDistance, DroneFollow.MaxDistance).ToString("0.#") + " m back, facing you. Move a stick or say stop to take over.");
            },
            ["drone_follow_stop"] = args => Do(d => { d.StopFollow(); return null; }, "Stopped following: it hovers where it is."),
            ["drone_cam"] = args =>
            {
                bool on = (bool?)args?["on"] ?? true;
                return Do(d =>
                {
                    if (on) { Forge.OS.OSDroneCamWindow.Open(); d.SetCam(true); }
                    else { Forge.OS.OSDroneCamWindow.Close(); d.SetCam(false); }
                    return null;
                }, on ? "The drone cam is on: its virtual view (the scene and the room scan, not the camera passthrough) in a window." : "The drone cam is off.");
            },
            ["drone_status"] = args =>
            {
                var drone = ArtifactDrone.Live;
                return drone != null ? drone.ToolState() : new JObject { ["thing"] = null, ["state"] = "none", ["note"] = NoDrone };
            },
        };

        static JObject Do(System.Func<ArtifactDrone, string> act, string said)
        {
            var drone = ArtifactDrone.Live;
            if (drone == null) return AlbieTools.Error(NoDrone);
            string error = act(drone);
            if (error != null) return AlbieTools.Error(error);
            var result = drone.ToolState();
            result["ok"] = true;
            result["said"] = said;
            return result;
        }
    }
}
