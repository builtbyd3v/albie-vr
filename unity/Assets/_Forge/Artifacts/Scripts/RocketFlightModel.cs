using System;
using UnityEngine;

namespace Forge.Artifacts
{
    /// <summary>The rocket.py parameters the launch uses (millimetres), at the part's defaults.</summary>
    public struct RocketDesign
    {
        public float bodyLength, bodyDiameter, noseLength, finRoot, finTip, finSpan, finSweep;
        public int finCount;
        public bool cone;

        public static RocketDesign Default => new RocketDesign
        {
            bodyLength = 300f, bodyDiameter = 40f, noseLength = 90f, finCount = 3, finRoot = 70f, finTip = 35f, finSpan = 60f, finSweep = 35f,
        };

        public float LengthMm => bodyLength + noseLength;

        public bool SameAs(RocketDesign o) =>
            Mathf.Abs(bodyLength - o.bodyLength) < 0.5f && Mathf.Abs(bodyDiameter - o.bodyDiameter) < 0.5f && Mathf.Abs(noseLength - o.noseLength) < 0.5f
            && finCount == o.finCount && cone == o.cone && Mathf.Abs(finRoot - o.finRoot) < 0.5f && Mathf.Abs(finTip - o.finTip) < 0.5f
            && Mathf.Abs(finSpan - o.finSpan) < 0.5f && Mathf.Abs(finSweep - o.finSweep) < 0.5f;
    }

    /// <summary>
    /// One flight, pad to landing, as samples every <see cref="SampleDt"/> s (the last one is the landing) plus the numbers
    /// a launch reports. Preallocated: filling it again never allocates.
    /// </summary>
    public sealed class RocketTrajectory
    {
        public const int Capacity = 4096;
        public const float SampleDt = 0.05f;

        public readonly float[] t = new float[Capacity], x = new float[Capacity], z = new float[Capacity], speed = new float[Capacity];
        public int count;
        public float apogeeM, timeToApogeeS, maxSpeedMps, railExitSpeedMps, burnoutS, deployS = -1f, landingS, driftM, descentRateMps, liftoffG;
        public float marginCal, cgMm, cpMm, apogeeX;
        /// <summary>True: RocketPy's flight from the hub or cloud (<c>GET /cad/flight</c>); false: <see cref="RocketFlightModel"/>.</summary>
        public bool rocketPy;
        public int designVersion = -1;

        public bool Deploys => deployS >= 0f && deployS < landingS;

        public void Clear()
        {
            count = 0;
            apogeeM = timeToApogeeS = maxSpeedMps = railExitSpeedMps = burnoutS = landingS = driftM = descentRateMps = liftoffG = apogeeX = 0f;
            deployS = -1f;
            rocketPy = false;
            designVersion = -1;
        }

        public void Add(float time, float downrange, float altitude, float v)
        {
            if (count >= Capacity) return;
            t[count] = time;
            x[count] = downrange;
            z[count] = altitude;
            speed[count] = v;
            count++;
        }

        /// <summary>Downrange, altitude and speed at <paramref name="time"/>. <paramref name="cursor"/> remembers where the
        /// last lookup was, so playing forward costs nothing; it walks back if time went back.</summary>
        public void At(float time, ref int cursor, out float downrange, out float altitude, out float v)
        {
            if (count == 0) { downrange = altitude = v = 0f; return; }
            if (cursor < 0 || cursor >= count) cursor = 0;
            while (cursor > 0 && t[cursor] > time) cursor--;
            while (cursor < count - 1 && t[cursor + 1] <= time) cursor++;
            if (cursor >= count - 1 || time <= t[cursor])
            {
                downrange = x[cursor];
                altitude = z[cursor];
                v = speed[cursor];
                return;
            }
            float f = (time - t[cursor]) / Mathf.Max(t[cursor + 1] - t[cursor], 1e-5f);
            downrange = Mathf.Lerp(x[cursor], x[cursor + 1], f);
            altitude = Mathf.Lerp(z[cursor], z[cursor + 1], f);
            v = Mathf.Lerp(speed[cursor], speed[cursor + 1], f);
        }
    }

    /// <summary>
    /// The simplified rocket flight, for when the cloud's RocketPy simulation can't be had (hub off, a what-if design on the
    /// launch's sliders, wind). A 2D point mass with drag; hub/cad/flight_simple.py is the same model line for line and is
    /// tested there against RocketPy (within 5 % on apogee and times with no wind) and against hand calculations.
    /// The Estes C6 thrust curve, the propellant burning off with the impulse; the 0.9 m rod at 85 degrees; off the rod the
    /// thrust points along the airflow (a stable rocket weathercocks into the wind); body drag Cd 0.6 relative to the air;
    /// standard atmosphere; the parachute opens at burnout + 5 s, not before apogee, fills in 0.5 s, then drifts with the
    /// wind. No rotation: an unstable design flies the same path here, so its margin is reported instead.
    /// </summary>
    public static class RocketFlightModel
    {
        public const float G = 9.80665f, DragCd = 0.6f, ParachuteCd = 0.75f, ChuteOpenS = 0.5f, RailM = 0.9f, InclinationDeg = 85f;
        public const float EjectionDelayS = 5f, ParachuteMm = 305f, MotorLoadedG = 24.1f, MotorPropellantG = 10.8f, MotorLengthMm = 70f, RecoveryG = 12f;
        const float Step = 0.01f;

        // Estes C6 (NAR certification data, hub/cad/data/estes_c6.eng), with (0, 0) in front.
        static readonly float[] CurveT = { 0f, 0.031f, 0.092f, 0.139f, 0.192f, 0.209f, 0.231f, 0.248f, 0.292f, 0.370f, 0.475f, 0.671f, 0.702f, 0.723f, 0.850f, 1.063f, 1.211f, 1.242f, 1.303f, 1.468f, 1.656f, 1.821f, 1.834f, 1.847f, 1.860f };
        static readonly float[] CurveF = { 0f, 0.946f, 4.826f, 9.936f, 14.090f, 11.446f, 7.381f, 6.151f, 5.489f, 4.921f, 4.448f, 4.258f, 4.542f, 4.164f, 4.448f, 4.353f, 4.353f, 4.069f, 4.258f, 4.353f, 4.448f, 4.448f, 2.933f, 1.325f, 0f };
        static readonly float[] Impulse = BuildImpulse();
        public static float BurnoutS => CurveT[CurveT.Length - 1];
        public static float TotalImpulse => Impulse[Impulse.Length - 1];

        static float[] BuildImpulse()
        {
            var cum = new float[CurveT.Length];
            for (int i = 1; i < CurveT.Length; i++) cum[i] = cum[i - 1] + 0.5f * (CurveF[i - 1] + CurveF[i]) * (CurveT[i] - CurveT[i - 1]);
            return cum;
        }

        static void Thrust(float t, out float force, out float impulse)
        {
            force = 0f;
            impulse = 0f;
            if (t <= 0f) return;
            if (t >= BurnoutS) { impulse = TotalImpulse; return; }
            for (int i = 1; i < CurveT.Length; i++)
            {
                if (t > CurveT[i]) continue;
                float t0 = CurveT[i - 1], f0 = CurveF[i - 1];
                force = f0 + (CurveF[i] - f0) * (t - t0) / (CurveT[i] - t0);
                impulse = Impulse[i - 1] + 0.5f * (f0 + force) * (t - t0);
                return;
            }
        }

        public static float AirDensity(float z) => 1.225f * Mathf.Pow(Mathf.Max(1f - 2.25577e-5f * z, 0f), 4.25588f);

        static readonly float[] FinFactor = { 0f, 0f, 0f, 0f, 0f, 2.37f, 2.74f, 2.99f, 3.24f };

        /// <summary>Barrowman CP from the nose tip (mm): cad/analysis.barrowman, root leading edge at z = fin root.</summary>
        public static float CenterOfPressure(RocketDesign d)
        {
            float D = d.bodyDiameter, r = D * 0.5f, length = d.LengthMm;
            float cnNose = 2f, xNose = (d.cone ? 2f / 3f : 0.466f) * d.noseLength;
            float cr = d.finRoot, ct = d.finTip, s = d.finSpan, xr = d.finSweep;
            float mid = Mathf.Sqrt(s * s + Sq(xr + ct * 0.5f - cr * 0.5f));
            float nEff = 2f * (d.finCount >= 5 && d.finCount <= 8 ? FinFactor[d.finCount] : d.finCount * 0.5f);
            float cnFins = (1f + r / (s + r)) * (4f * nEff * Sq(s / D)) / (1f + Mathf.Sqrt(1f + Sq(2f * mid / (cr + ct))));
            float xFins = (length - cr) + xr / 3f * (cr + 2f * ct) / (cr + ct) + (cr + ct - cr * ct / (cr + ct)) / 6f;
            return (cnNose * xNose + cnFins * xFins) / (cnNose + cnFins);
        }

        static float Sq(float v) => v * v;

        /// <summary>Liftoff mass (g) and CG (mm from the tail), estimated from the parameters with rocket.py's materials
        /// (paper tube, polystyrene nose, balsa fins, plywood rings, a loaded C6, 12 g of recovery gear).</summary>
        public static void Mass(RocketDesign d, out float totalG, out float cgFromTailMm)
        {
            const float paper = 0.70f, polystyrene = 1.05f, balsa = 0.16f, plywood = 0.68f;
            const float bodyWall = 0.8f, noseWall = 1.5f, finThickness = 3.2f, mountId = 18.3f, mountWall = 0.5f, mountLength = 76f, ring = 3f;
            float L = d.bodyLength, r = d.bodyDiameter * 0.5f, ln = d.noseLength;
            float m = 0f, mz = 0f;
            void Add(float g, float z) { m += g; mz += g * z; }

            Add(Mathf.PI * (r * r - Sq(r - bodyWall)) * L / 1000f * paper, L * 0.5f);
            NoseShell(r, ln, d.cone, out float area, out float h);
            Add(area * noseWall / 1000f * polystyrene, L + h);
            float mountOr = mountId * 0.5f + mountWall;
            Add(Mathf.PI * (mountOr * mountOr - Sq(mountId * 0.5f)) * mountLength / 1000f * paper, mountLength * 0.5f);
            float ringG = Mathf.PI * (Sq(r - bodyWall) - mountOr * mountOr) * ring / 1000f * plywood;
            Add(ringG, 3f + ring * 0.5f);
            Add(ringG, mountLength - 8f + ring * 0.5f);
            FinCentroid(d, out float finArea, out float finZ);
            for (int i = 0; i < d.finCount; i++) Add(finArea * finThickness / 1000f * balsa, finZ);
            Add(MotorLoadedG, MotorLengthMm * 0.5f);
            Add(RecoveryG, L - 50f);
            totalG = m;
            cgFromTailMm = mz / m;
        }

        static void NoseShell(float r, float length, bool cone, out float area, out float centroid)
        {
            const int n = 48;
            float rho = (r * r + length * length) / (2f * r), moment = 0f;
            area = 0f;
            float Radius(float h) => cone ? r * (1f - h / length) : Mathf.Sqrt(Mathf.Max(rho * rho - h * h, 0f)) + r - rho;
            for (int i = 0; i < n; i++)
            {
                float h0 = length * i / n, h1 = length * (i + 1) / n, r0 = Radius(h0), r1 = Radius(h1);
                float a = Mathf.PI * (r0 + r1) * Mathf.Sqrt(Sq(r1 - r0) + Sq(h1 - h0));
                area += a;
                moment += a * (h0 + h1) * 0.5f;
            }
            centroid = moment / area;
        }

        // One fin's area (mm^2) and the height of its centroid above the tail: root LE at z = root, root TE at 0.
        static void FinCentroid(RocketDesign d, out float area, out float z)
        {
            float cr = d.finRoot, ct = d.finTip, s = d.finSpan, sw = d.finSweep;
            float x0 = 0f, z0 = cr, x1 = 0f, z1 = 0f, x2 = s, z2 = cr - sw - ct, x3 = s, z3 = cr - sw;
            float a2 = 0f, cz = 0f;
            void Edge(float ax, float az, float bx, float bz) { float c = ax * bz - bx * az; a2 += c; cz += (az + bz) * c; }
            Edge(x0, z0, x1, z1);
            Edge(x1, z1, x2, z2);
            Edge(x2, z2, x3, z3);
            Edge(x3, z3, x0, z0);
            area = Mathf.Abs(a2) * 0.5f;
            z = Mathf.Abs(a2) > 1e-6f ? cz / (3f * a2) : cr * 0.5f;
        }

        /// <summary>Fills <paramref name="into"/> with the design's flight in a steady wind (m/s along the launch heading).</summary>
        public static void Simulate(RocketDesign d, float windMps, RocketTrajectory into)
        {
            Mass(d, out float liftoffG, out float cgTail);
            float cp = CenterOfPressure(d), cg = d.LengthMm - cgTail;
            Fly(liftoffG, d.bodyDiameter, windMps, into);
            into.marginCal = (cp - cg) / d.bodyDiameter;
            into.cgMm = cg;
            into.cpMm = cp;
        }

        /// <summary>The flight of a C6 rocket of this liftoff mass and body diameter. Allocation-free.</summary>
        public static void Fly(float liftoffG, float diameterMm, float wind, RocketTrajectory into)
        {
            into.Clear();
            into.liftoffG = liftoffG;
            into.burnoutS = BurnoutS;
            float area = Mathf.PI * Sq(diameterMm / 2000f), chuteArea = Mathf.PI * Sq(ParachuteMm / 2000f);
            float dry = (liftoffG - MotorPropellantG) / 1000f, prop = MotorPropellantG / 1000f;
            float ux = Mathf.Cos(InclinationDeg * Mathf.Deg2Rad), uz = Mathf.Sin(InclinationDeg * Mathf.Deg2Rad);
            float tEject = BurnoutS + EjectionDelayS;

            float x = 0f, z = 0f, vx = 0f, vz = 0f, t = 0f, tOpen = -1f, tApogee = -1f;
            bool onRail = true;
            float lastT = 0f, lastX = 0f, lastZ = 0f, nextSample = RocketTrajectory.SampleDt;
            into.Add(0f, 0f, 0f, 0f);

            for (int guard = 0; guard < 200000; guard++)
            {
                Accel(t, x, z, vx, vz, out float a1x, out float a1z);
                float h = Step * 0.5f;
                float hx = x + vx * h, hz = z + vz * h, hvx = vx + a1x * h, hvz = vz + a1z * h;
                Accel(t + h, hx, hz, hvx, hvz, out float a2x, out float a2z);
                x += hvx * Step;
                z += hvz * Step;
                float nvx = vx + a2x * Step, nvz = vz + a2z * Step;
                t += Step;
                if (onRail && x * ux + z * uz >= RailM)
                {
                    onRail = false;
                    into.railExitSpeedMps = Mathf.Sqrt(nvx * nvx + nvz * nvz);
                }
                if (tApogee < 0f && !onRail && vz > 0f && nvz <= 0f)
                {
                    tApogee = t;
                    into.apogeeM = z;
                    into.apogeeX = x;
                    into.timeToApogeeS = t;
                }
                vx = nvx;
                vz = nvz;
                if (tOpen < 0f && tApogee >= 0f && t >= tEject) tOpen = Mathf.Max(tEject, tApogee);
                float speed = Mathf.Sqrt(vx * vx + vz * vz);
                if (tApogee < 0f) into.maxSpeedMps = Mathf.Max(into.maxSpeedMps, speed);
                if (z < 0f && t > 0.5f)
                {
                    float f = lastZ != z ? lastZ / (lastZ - z) : 1f;
                    t = lastT + f * Step;
                    x = lastX + f * (x - lastX);
                    if (into.count >= RocketTrajectory.Capacity) into.count = RocketTrajectory.Capacity - 1;  // the landing is always last
                    into.Add(t, x, 0f, speed);
                    into.descentRateMps = Mathf.Abs(vz);
                    break;
                }
                if (t >= nextSample)
                {
                    into.Add(t, x, z, speed);
                    nextSample += RocketTrajectory.SampleDt;
                }
                lastT = t;
                lastX = x;
                lastZ = z;
                if (t > 900f) break;
            }
            into.landingS = t;
            into.driftM = Mathf.Abs(x);
            into.deployS = tOpen >= 0f && tOpen < t ? tOpen : -1f;

            void Accel(float tt, float px, float pz, float pvx, float pvz, out float ax, out float az)
            {
                Thrust(tt, out float force, out float impulse);
                float mass = dry + prop * (1f - impulse / TotalImpulse);
                float rx = pvx - wind, rz = pvz;  // air-relative velocity
                float vAir = Mathf.Sqrt(rx * rx + rz * rz);
                float cda = DragCd * area;
                if (tOpen >= 0f) cda += ParachuteCd * chuteArea * Mathf.Clamp01((tt - tOpen) / ChuteOpenS);
                float drag = 0.5f * AirDensity(pz) * cda * vAir;
                if (onRail)
                {
                    float along = (force - drag * vAir - mass * G * uz) / mass;
                    if (pvx * ux + pvz * uz <= 0f && along < 0f) along = 0f;
                    ax = along * ux;
                    az = along * uz;
                    return;
                }
                float dx = ux, dz = uz;
                if (vAir > 0.1f) { dx = rx / vAir; dz = rz / vAir; }
                ax = force * dx / mass - drag * rx / mass;
                az = force * dz / mass - drag * rz / mass - G;
            }
        }

    }
}
