using System;

namespace Forge.Controllers
{
    /// <summary>
    /// The handheld transmitter's arithmetic, kept free of Unity types so a plain .NET check can test it
    /// (<see cref="RcTransmitter"/> does the geometry and calls these). Distances are metres in the transmitter's own frame:
    /// x to the right, y out of the face, z towards the antenna (away from the pilot). A stick's value is x right, y forward,
    /// each -1..1. No allocations.
    /// </summary>
    public static class TransmitterMath
    {
        /// <summary>
        /// A thumb on a stick: its offset across the face from where it touched down (<paramref name="dx"/> right,
        /// <paramref name="dz"/> forward) as a stick value. Radial: inside <paramref name="deadzone"/> (a fraction of
        /// <paramref name="travel"/>) it reads zero, beyond it rescales so the edge of the dead zone is 0 and
        /// <paramref name="travel"/> is 1, and further out it stays on the unit circle. So a resting thumb's tracking jitter
        /// never moves the Thing, and a full push is exactly 1.
        /// </summary>
        public static void StickFromOffset(float dx, float dz, float travel, float deadzone, out float x, out float y)
        {
            float r = (float)Math.Sqrt(dx * dx + dz * dz);
            float n = travel > 1e-5f ? r / travel : 0f;
            if (n <= deadzone || r < 1e-6f) { x = 0f; y = 0f; return; }
            float m = Math.Min(1f, (n - deadzone) / Math.Max(1e-4f, 1f - deadzone));
            x = dx / r * m;
            y = dz / r * m;
        }

        /// <summary>
        /// Where a thumb's neutral point is: where it touched down, pulled back to within <paramref name="maxOff"/> of the
        /// stick's centre. A thumb that lands a little off the cap (tracking puts the tip a few mm from where it feels) reads
        /// zero; one that lands far off still deflects by the part beyond <paramref name="maxOff"/>.
        /// </summary>
        public static void ClampAnchor(float ax, float az, float maxOff, out float x, out float z)
        {
            float r = (float)Math.Sqrt(ax * ax + az * az);
            if (r <= maxOff || r < 1e-6f) { x = ax; z = az; return; }
            float k = maxOff / r;
            x = ax * k;
            z = az * k;
        }

        /// <summary>
        /// Whether a fingertip is on a stick, with hysteresis so it doesn't flicker: it takes the stick when it is within
        /// <paramref name="captureRadius"/> of the cap across the face and no higher than <paramref name="engageHeight"/>
        /// above it, and lets go only when it lifts past <paramref name="releaseHeight"/> or slides past
        /// <paramref name="releaseRadius"/>. Heights are above the cap's top (negative is pressed into it).
        /// </summary>
        public static bool Engaged(bool was, float height, float across, float captureRadius, float engageHeight,
                                   float releaseRadius, float releaseHeight)
        {
            if (was) return height <= releaseHeight && across <= releaseRadius;
            return height <= engageHeight && height >= -engageHeight * 2f && across <= captureRadius;
        }

        /// <summary>First-order low-pass towards <paramref name="target"/> with time constant <paramref name="tau"/> seconds,
        /// frame-rate independent. A <paramref name="tau"/> of zero or less returns the target.</summary>
        public static float Smooth(float current, float target, float dt, float tau)
        {
            if (tau <= 0f || dt <= 0f) return tau <= 0f ? target : current;
            float k = 1f - (float)Math.Exp(-dt / tau);
            return current + (target - current) * k;
        }

        /// <summary>A released stick coming home: it moves towards 0 at a full stick per <paramref name="seconds"/>.</summary>
        public static float Spring(float current, float dt, float seconds)
        {
            float step = seconds > 0f ? dt / seconds : 1f;
            if (current > 0f) return Math.Max(0f, current - step);
            if (current < 0f) return Math.Min(0f, current + step);
            return 0f;
        }

        /// <summary>A tilt in degrees as a stick axis: 0 inside <paramref name="dead"/>, 1 at <paramref name="full"/>, signed.</summary>
        public static float TiltAxis(float degrees, float dead, float full)
        {
            float a = Math.Abs(degrees);
            if (a <= dead) return 0f;
            float v = Math.Min(1f, (a - dead) / Math.Max(1e-3f, full - dead));
            return degrees < 0f ? -v : v;
        }

        /// <summary>A value small enough to be noise is exactly zero, so "let go" writes a clean 0 and then nothing.</summary>
        public static float Snap(float v, float epsilon) => v > -epsilon && v < epsilon ? 0f : v;

        /// <summary>Whether a channel needs writing: it moved by more than <paramref name="epsilon"/>, or it reached zero.</summary>
        public static bool Changed(float last, float now, float epsilon) =>
            now == 0f ? last != 0f : Math.Abs(now - last) > epsilon;

        /// <summary>
        /// A finger pressing a button: pressed once the tip is <paramref name="pressDepth"/> below the cap's rest top within
        /// <paramref name="radius"/> across, released when it comes back above <paramref name="releaseDepth"/> or slides off.
        /// <paramref name="armed"/> must have been set by the tip being clear above the cap first, so a hand that closes
        /// round the body doesn't press anything.
        /// </summary>
        public static bool Pressed(bool was, bool armed, float depth, float across, float radius, float pressDepth, float releaseDepth)
        {
            if (was) return depth >= releaseDepth && across <= radius * 1.4f;
            return armed && depth >= pressDepth && across <= radius;
        }

        /// <summary>A grip from finger curl, with hysteresis: closes at <paramref name="close"/>, opens below <paramref name="open"/>.</summary>
        public static bool Gripping(bool was, float curl, float close, float open) => was ? curl >= open : curl >= close;
    }
}
