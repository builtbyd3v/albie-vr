using Forge.Artifacts;
using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// Follow-me: the drone keeps <see cref="Distance"/> metres from the user's headset (on the side it is already on, so it
    /// trails them as they walk), at <see cref="Height"/> above the floor (or just under head height), turned to face them.
    /// A planner, ten times a second, gives <see cref="DroneBody.FollowTo"/> a point; the body flies there speed-capped and
    /// smooth. How it keeps clear, in order:
    /// 1. the room scan (<see cref="IFloorPlan.IsFree"/>: inside the walls, outside every furniture volume by
    ///    <see cref="Clearance"/> plus the drone's radius): a blocked spot is swapped for the cheapest free one round the
    ///    user (a ring search, nearer rather than further, small turns first);
    /// 2. a blocked straight line to that spot gets a waypoint to one side (up to 90 degrees off, 0.6 m out) whose line is clear;
    /// 3. it never plans through the user: when the spot is across them it goes round at least <see cref="TooClose"/> away;
    /// 4. the body's own swept-sphere collision against the room's colliders is the last resort.
    /// Safe holds: the user within <see cref="TooClose"/> (it brakes and hovers facing them, and goes on when they step back);
    /// no free spot anywhere near (it hovers where it is); the room data gone (follow ends, it hovers); no head camera.
    /// Any stick, Stop, Land, Home or a crash ends it (the body does that). Nothing here allocates per frame.
    /// The room scan is the floor plan's footprint test, so it treats furniture as full height: it goes round a table, never over it.
    /// </summary>
    public sealed class DroneFollow : MonoBehaviour
    {
        public enum Mode { Off, Starting, Following, TooClose, Blocked }

        [Tooltip("Default distance kept from the headset, metres.")] public float defaultDistance = 2f;
        [Tooltip("Top speed while following, m/s (also capped by the body's maxSpeed).")] public float followSpeed = 0.8f;
        [Tooltip("Hold (do not come nearer) while the user is closer than this, metres, flat.")] public float TooClose = 0.8f;
        [Tooltip("Extra gap kept from walls and furniture beyond the drone's radius, metres.")] public float Clearance = 0.15f;
        [Tooltip("Planner rate, Hz.")] public float planHz = 10f;
        [Tooltip("Default height: this far under the headset.")] public float underHead = 0.15f;

        public const float MinDistance = 1f, MaxDistance = 4f, MinHeight = 0.5f;

        public Mode State { get; private set; } = Mode.Off;
        public bool Active => State != Mode.Off;
        public float Distance { get; private set; } = 2f;
        /// <summary>Height above the floor to hold, metres; NaN: just under head height.</summary>
        public float Height { get; private set; } = float.NaN;
        /// <summary>Flat distance to the headset at the last plan, metres (NaN with no head).</summary>
        public float UserDistance { get; private set; } = float.NaN;
        /// <summary>True when it steers by the room scan; false when it only has its own collision sweep (Editor without a scan).</summary>
        public bool UsesScan => room != null;
        /// <summary>Why the last follow ended, in words (null while it runs or before the first).</summary>
        public string EndedWhy { get; private set; }

        DroneBody body;
        IFloorPlan room;
        float nextPlan, startBy;
        Vector3 lastGoal;

        public void Init(DroneBody owner) => body = owner;

        /// <summary>
        /// Starts following. <paramref name="plan"/> is the room scan (null: none). On a headset a scan is required; in the
        /// Editor it starts without one and steers by the collision sweep only (it says so). The drone must be flying or
        /// taking off (the caller takes it off first). Returns null, or why not.
        /// </summary>
        public string Begin(float distance, float height, IFloorPlan plan)
        {
            if (body == null) return "no_drone";
            if (body.Crashed) return "crashed: reset the drone first";
            if (Camera.main == null) return "no_head: there is no headset camera to follow";
            bool scan = plan != null && plan.HasFloor;
            if (!scan && !Application.isEditor)
                return "no_room_data: follow-me needs the room scan to steer round your furniture. Scan the room (Settings > Space setup), then try again.";
            if (body.State != DroneBody.Phase.Flying && body.State != DroneBody.Phase.TakingOff) return "not flying: take off first";
            room = scan ? plan : null;
            Distance = Mathf.Clamp(distance > 0f ? distance : defaultDistance, MinDistance, MaxDistance);
            Height = height > 0f ? height : float.NaN;
            EndedWhy = null;
            State = Mode.Starting;
            startBy = Time.time + 5f;
            nextPlan = 0f;
            return null;
        }

        /// <summary>Ends follow-me; the drone brakes and hovers where it is.</summary>
        public void End(string why = "stopped")
        {
            if (State == Mode.Off) return;
            State = Mode.Off;
            EndedWhy = why;
            if (body != null) body.EndFollow(why);
        }

        void OnDisable() => End("hidden");

        void Update()
        {
            if (State == Mode.Off || body == null) return;
            if (State != Mode.Starting && !body.Following)
            {
                State = Mode.Off;
                EndedWhy = Plain(body.FollowEnded);
                return;
            }
            if (body.Crashed || body.State == DroneBody.Phase.Landed || body.State == DroneBody.Phase.Landing) { End("landed"); return; }
            if (Time.time < nextPlan) return;
            nextPlan = Time.time + 1f / Mathf.Max(1f, planHz);
            if (State == Mode.Starting && body.State != DroneBody.Phase.Flying)
            {
                if (Time.time > startBy) End("it did not take off");
                return;
            }
            Plan();
        }

        static string Plain(string why)
        {
            switch (why)
            {
                case "stick": return "you took the sticks";
                case "stop": return "Stop";
                case "land": return "Land";
                case "home": return "Home";
                case "crash": return "it crashed";
                case "placed": return "it was reset";
                default: return why ?? "ended";
            }
        }

        void Plan()
        {
            var cam = Camera.main;
            if (cam == null) { Hold(Mode.Blocked); End("the headset camera went away"); return; }
            if (room != null && !room.HasFloor) { End("the room scan went away"); return; }
            Vector3 head = cam.transform.position;
            Vector3 pos = body.transform.position;
            Vector3 away = pos - head;
            away.y = 0f;
            float flat = away.magnitude;
            UserDistance = flat;

            float floor = room != null ? room.FloorY : body.transform.position.y - body.HeightAboveFloor;
            float y = float.IsNaN(Height) ? head.y - underHead - floor : Height;
            float ceilingRoom = room != null && !float.IsNaN(room.CeilingHeight) ? room.CeilingHeight - 0.35f : body.maxAltitude;
            y = floor + Mathf.Clamp(y, MinHeight, Mathf.Max(MinHeight, Mathf.Min(ceilingRoom, body.maxAltitude)));

            // Too close: brake and hover facing them; go on once they are a step further away.
            if (flat < TooClose || (State == Mode.TooClose && flat < TooClose + 0.3f))
            {
                Hold(Mode.TooClose, head, new Vector3(pos.x, y, pos.z));
                return;
            }

            Vector3 dir = flat > 1e-3f ? away / flat : -Flat(cam.transform.forward);
            float clear = body.radius + Clearance;
            Vector3 goal = head + dir * Distance;
            goal.y = y;
            if (room != null && !room.IsFree(goal, clear) && !Search(head, dir, y, clear, pos, out goal))
            {
                Hold(Mode.Blocked, head, new Vector3(pos.x, y, pos.z));
                return;
            }

            Vector3 steer = goal;
            // The spot is across the user: go round them, never through.
            if (PassesNear(pos, goal, head, TooClose + body.radius))
            {
                float sign = Vector3.Cross(away, goal - head).y >= 0f ? 1f : -1f;
                steer = head + Quaternion.AngleAxis(sign * 60f, Vector3.up) * dir * Mathf.Max(TooClose + 0.5f, Mathf.Min(Distance, flat + 0.3f));
                steer.y = y;
                if (room != null && !room.IsFree(steer, clear)) steer = goal;
            }
            // Furniture in the way: a waypoint to one side whose line is clear.
            if (room != null && !LineClear(pos, steer, clear)) steer = Waypoint(pos, steer, y, clear);

            lastGoal = steer;
            body.FollowTo(steer, head, followSpeed);
            State = Mode.Following;
        }

        void Hold(Mode why) => State = why;

        void Hold(Mode why, Vector3 head, Vector3 at)
        {
            State = why;
            body.FollowTo(at, head, followSpeed);
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }

        // The cheapest free spot round the head: small turns from where it is first, nearer rather than further.
        bool Search(Vector3 head, Vector3 dir, float y, float clear, Vector3 pos, out Vector3 best)
        {
            best = default;
            float bestCost = float.MaxValue;
            for (int ri = 0; ri < 3; ri++)
            {
                float r = Mathf.Max(MinDistance, Distance * (1f - 0.2f * ri));
                for (int a = 1; a <= 24; a++)
                {
                    float ang = ((a + 1) / 2) * 15f * ((a & 1) == 1 ? 1f : -1f);
                    Vector3 c = head + Quaternion.AngleAxis(ang, Vector3.up) * dir * r;
                    c.y = y;
                    float cost = Mathf.Abs(ang) * 0.012f + ri * 1.2f + (c - pos).magnitude * 0.25f;
                    if (cost >= bestCost || !room.IsFree(c, clear)) continue;
                    bestCost = cost;
                    best = c;
                }
            }
            return bestCost < float.MaxValue;
        }

        Vector3 Waypoint(Vector3 pos, Vector3 target, float y, float clear)
        {
            Vector3 to = target - pos;
            to.y = 0f;
            if (to.sqrMagnitude < 1e-4f) return target;
            to.Normalize();
            Vector3 fallback = target;
            for (int i = 1; i <= 6; i++)
            {
                float ang = ((i + 1) / 2) * 30f * ((i & 1) == 1 ? 1f : -1f);
                Vector3 w = pos + Quaternion.AngleAxis(ang, Vector3.up) * to * 0.6f;
                w.y = y;
                if (!room.IsFree(w, clear) || !LineClear(pos, w, clear)) continue;
                if (LineClear(w, target, clear)) return w;
                if (fallback == target) fallback = w;
            }
            return fallback;
        }

        bool LineClear(Vector3 a, Vector3 b, float clear)
        {
            Vector3 d = b - a;
            d.y = 0f;
            int n = Mathf.Clamp(Mathf.CeilToInt(d.magnitude / 0.25f), 1, 16);
            for (int i = 1; i <= n; i++)
                if (!room.IsFree(Vector3.Lerp(a, b, i / (float)n), clear)) return false;
            return true;
        }

        static bool PassesNear(Vector3 a, Vector3 b, Vector3 p, float r)
        {
            a.y = b.y = p.y = 0f;
            Vector3 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-4f) return false;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2);
            return (a + ab * t - p).sqrMagnitude < r * r && t > 0.05f && t < 0.95f;
        }

        /// <summary>The point it is flying to now (world), for the drone cam's overlay and checks.</summary>
        public Vector3 Goal => lastGoal;
    }
}
