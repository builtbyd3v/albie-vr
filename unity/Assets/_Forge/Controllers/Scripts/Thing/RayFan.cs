using UnityEngine;

namespace Forge.Controllers
{
    /// <summary>
    /// The ray-fan sensor of C-19 section 2.1 on real colliders, for the Thing runtime: channels <c>&lt;id&gt;.d0</c> …
    /// <c>d{n-1}</c>, each the hit distance / range (1 = nothing hit). d0 is the leftmost ray, and the rays are spread
    /// evenly across the fan's angle around the origin's up axis. Each ray is a sphere cast with the part's
    /// <c>beam</c> radius (default 0.1 m), so a thin cone can't slip between two rays. With the mock truck's numbers
    /// (7 rays, 120°, 2 m, 0.1 m) it gives the same angles as <see cref="MockTruckSim"/>.
    /// Allocation-free; call it once per physics step.
    /// </summary>
    public static class RayFan
    {
        static readonly Collider[] overlap = new Collider[1];

        /// <summary>Ray <paramref name="i"/>'s angle from straight ahead in degrees, positive to the left (d0 is +fov/2).</summary>
        public static float AngleDeg(int i, int count, float fovDeg) => count <= 1 ? 0f : fovDeg / 2f - i * fovDeg / (count - 1);

        /// <summary>
        /// Writes <paramref name="count"/> normalised distances into <paramref name="sensors"/> from
        /// <paramref name="offset"/>, and the distances in metres into <paramref name="metres"/> if it isn't null.
        /// A sphere cast can't see what the sphere already touches at the origin, so if anything is inside the beam
        /// there, each ray falls back to a plain ray (keep the beam smaller than the body's half-width).
        /// </summary>
        public static void Sense(PhysicsScene physics, Vector3 origin, Vector3 forward, Vector3 up, int count, float fovDeg,
                                 float beam, float range, int layerMask, float[] sensors, int offset, float[] metres = null)
        {
            bool sphere = beam > 0f && physics.OverlapSphere(origin, beam, overlap, layerMask, QueryTriggerInteraction.Ignore) == 0;
            for (int i = 0; i < count; i++)
            {
                // AngleAxis turns clockwise seen from above, so a left (positive) angle is a negative turn.
                var direction = Quaternion.AngleAxis(-AngleDeg(i, count, fovDeg), up) * forward;
                float d = range;
                bool hit = sphere
                    ? physics.SphereCast(origin, beam, direction, out RaycastHit h, range, layerMask, QueryTriggerInteraction.Ignore)
                    : physics.Raycast(origin, direction, out h, range, layerMask, QueryTriggerInteraction.Ignore);
                if (hit) d = Mathf.Clamp(h.distance, 0f, range);
                sensors[offset + i] = d / range;
                if (metres != null) metres[i] = d;
            }
        }
    }
}
