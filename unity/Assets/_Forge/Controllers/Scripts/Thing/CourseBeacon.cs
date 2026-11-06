using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// Where the <c>course.bearing</c> sensor points (C-19 section 2, proposed 2026-11-06): the running course's next
    /// target in world space. Person 5's run engine (<c>ArtifactRun</c>) sets it as a run goes: the next gate in order,
    /// then the finish. It clears it when no run is armed or running. A Thing's body reads <see cref="Bearing"/> each step.
    /// On a room slalom a learned driver needs it: a ray fan alone can't tell which side of a cone the next gate is on
    /// once the truck is behind the cone, so 2 laps trained a net that drove 5 of 48 room attempts without it and 48 of
    /// 48 with it (PROJECT_CONTEXT 16.8, "room slalom, part 2"). One course at a time.
    /// </summary>
    public static class CourseBeacon
    {
        public static bool HasTarget { get; private set; }
        public static Vector3 Target { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => HasTarget = false;

        public static void Set(Vector3 world) { Target = world; HasTarget = true; }
        public static void Clear() => HasTarget = false;

        /// <summary>
        /// <c>course.bearing</c> for a Thing at <paramref name="position"/> facing <paramref name="forward"/>: the angle to
        /// the target about <paramref name="up"/>, positive to the left, over 90°, clamped to [-1, 1]. 0 with no target.
        /// </summary>
        public static float Bearing(Vector3 position, Vector3 forward, Vector3 up)
        {
            if (!HasTarget) return 0f;
            var to = Vector3.ProjectOnPlane(Target - position, up);
            var ahead = Vector3.ProjectOnPlane(forward, up);
            if (to.sqrMagnitude < 1e-8f || ahead.sqrMagnitude < 1e-8f) return 0f;
            // SignedAngle is positive clockwise seen from above (to the right); the contract's left is positive.
            return MockTruckSim.BearingValue(-Vector3.SignedAngle(ahead, to, up) * Mathf.Deg2Rad);
        }
    }
}
