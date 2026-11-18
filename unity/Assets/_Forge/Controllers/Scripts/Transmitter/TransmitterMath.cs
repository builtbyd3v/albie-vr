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

        /// <summary>
        /// <see cref="StickFromOffset"/> with expo on the length: <c>(1 - expo) m + expo m^3</c>, so small thumb movements
        /// are fine control and the end is still exactly 1. <paramref name="expo"/> 0 is linear.
        /// </summary>
        public static void ThumbStick(float dx, float dz, float travel, float deadzone, float expo, out float x, out float y)
        {
            StickFromOffset(dx, dz, travel, deadzone, out x, out y);
            float m = (float)Math.Sqrt(x * x + y * y);
            if (m < 1e-6f || expo <= 0f) return;
            float e = Math.Min(1f, expo);
            float k = ((1f - e) * m + e * m * m * m) / m;
            x *= k;
            y *= k;
        }
    }

    /// <summary>The thumb lock's distances (m) and times (s): see <see cref="ThumbLock"/>.</summary>
    public struct ThumbTuning
    {
        /// <summary>Thumb movement across the face from the neutral for a full stick, and the radial dead zone (a fraction of it).</summary>
        public float Travel, Deadzone, Expo;
        /// <summary>A locked thumb lets go when it lifts this far above the neutral, or slides this far from it across the face.</summary>
        public float LiftRelease, SlideRelease;
        /// <summary>A let-go thumb locks again within this height of the neutral and this far from it across the face.</summary>
        public float RelockLift, RelockRadius;
        /// <summary>A thumb that locks again re-centres where it lands, pulled back to within this of the grip's neutral.</summary>
        public float RecentreMax;
        /// <summary>A tracking gap up to this long holds the stick where it was; longer, it centres.</summary>
        public float Grace;
    }

    /// <summary>
    /// One thumb bound to its stick while its hand holds the transmitter. <see cref="Grip"/> takes the thumb tip's position
    /// (in the transmitter's frame) as the stick's neutral, so the thumb never has to find the cap. Each frame
    /// <see cref="Step"/> reads the thumb's movement from the neutral across the face (x right, z forward) as the stick.
    /// Lifting the thumb well off, or sliding it well away (onto a button), lets go: the stick centres. Coming back near the
    /// neutral locks again, re-centred where it lands (within <see cref="ThumbTuning.RecentreMax"/> of the grip's neutral).
    /// A tracking gap holds the last value for <see cref="ThumbTuning.Grace"/>, then centres. No Unity types, no allocations.
    /// </summary>
    public struct ThumbLock
    {
        /// <summary>Its hand holds the transmitter and the thumb is bound to the stick.</summary>
        public bool Seated;
        /// <summary>The thumb is on the stick now (it drives it).</summary>
        public bool Locked;
        /// <summary>The neutral it drives from now.</summary>
        public float Nx, Ny, Nz;
        /// <summary>The neutral captured at the grip (a re-lock is pulled back towards it).</summary>
        public float Ox, Oy, Oz;
        /// <summary>The last value it gave (held through a short tracking gap).</summary>
        public float LastX, LastY;

        public const int None = 0, LockedNow = 1, LetGo = 2;

        /// <summary>The hand closed on the transmitter: the thumb is where the stick's centre is.</summary>
        public void Grip(float x, float y, float z)
        {
            Seated = Locked = true;
            Nx = Ox = x; Ny = Oy = y; Nz = Oz = z;
            LastX = LastY = 0f;
        }

        /// <summary>The hand let go.</summary>
        public void Release()
        {
            Seated = Locked = false;
            LastX = LastY = 0f;
        }

        /// <summary>The transmitter moved in the hands by (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) in its own frame: the neutral moves the other way, so the thumb's deflection is unchanged.</summary>
        public void Shift(float x, float y, float z)
        {
            Nx -= x; Ny -= y; Nz -= z;
            Ox -= x; Oy -= y; Oz -= z;
        }

        /// <summary>
        /// One frame: the thumb tip at (<paramref name="px"/>, <paramref name="py"/>, <paramref name="pz"/>) in the
        /// transmitter's frame, or not tracked for <paramref name="lostFor"/> s. Gives the stick (x right, y forward) and
        /// returns <see cref="LockedNow"/>, <see cref="LetGo"/> or <see cref="None"/>.
        /// </summary>
        public int Step(bool tracked, float lostFor, float px, float py, float pz, in ThumbTuning t, out float x, out float y)
        {
            x = y = 0f;
            if (!Seated) return None;
            if (!tracked)
            {
                if (Locked && lostFor <= t.Grace) { x = LastX; y = LastY; return None; }
                if (!Locked) return None;
                Locked = false;
                LastX = LastY = 0f;
                return LetGo;
            }
            int happened = None;
            if (Locked)
            {
                float dx = px - Nx, dz = pz - Nz;
                if (py - Ny > t.LiftRelease || dx * dx + dz * dz > t.SlideRelease * t.SlideRelease)
                {
                    Locked = false;
                    LastX = LastY = 0f;
                    return LetGo;
                }
            }
            else
            {
                float dx = px - Ox, dz = pz - Oz, dy = py - Oy;
                if (dy > t.RelockLift || dy < -2f * t.RelockLift || dx * dx + dz * dz > t.RelockRadius * t.RelockRadius) return None;
                TransmitterMath.ClampAnchor(dx, dz, t.RecentreMax, out float ax, out float az);
                Nx = Ox + ax; Ny = Oy; Nz = Oz + az;
                Locked = true;
                happened = LockedNow;
            }
            TransmitterMath.ThumbStick(px - Nx, pz - Nz, t.Travel, t.Deadzone, t.Expo, out x, out y);
            LastX = x; LastY = y;
            return happened;
        }
    }
}
