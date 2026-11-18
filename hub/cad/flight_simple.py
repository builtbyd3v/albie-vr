"""The simplified rocket flight: a 2D point mass with drag, no RocketPy, no build123d.

This is the same model the headset runs when the cloud is down (unity/Assets/_Forge/Artifacts/Scripts/
RocketFlightModel.cs mirrors it line for line), kept here so it can be tested against RocketPy and so the hub still
answers without RocketPy installed. Every result says `"model": "simplified"`.

What it does: the Estes C6 thrust curve (cad/data/estes_c6.eng) and the propellant burning off with the impulse; the
0.9 m rod at 85 degrees; off the rod the thrust points along the airflow (a stable rocket turns into the relative
wind, so wind makes it weathercock); drag 0.5 rho Cd A v^2 on the body (Cd 0.6) relative to the air; standard
atmosphere; the parachute opens at burnout + the ejection delay, not before apogee, and fills in 0.5 s; then it
drifts with the wind. Stability is Barrowman (cad/analysis.barrowman) against a CG from a mass estimate of the
parts made with rocket.py's materials (`estimate_mass`), or the real one when it has the built part.

What it does not do: rotation, so an unstable rocket flies the same path as a stable one here (the margin is
reported and the run is marked unstable instead); wind shear; launch-rod friction; Mach effects.
"""
from __future__ import annotations

import math
from pathlib import Path
from typing import Any

THRUST_CURVE = Path(__file__).parent / "data" / "estes_c6.eng"
G = 9.80665
DRAG_CD, PARACHUTE_CD, CHUTE_OPEN_S = 0.6, 0.75, 0.5
RAIL_M, INCLINATION_DEG = 0.9, 85.0
EJECTION_DELAY_S, PARACHUTE_MM = 5.0, 305.0
MOTOR_LOADED_G, MOTOR_PROPELLANT_G, MOTOR_LENGTH_MM = 24.1, 10.8, 70.0
RECOVERY_G = 12.0
STEP_S, SAMPLE_DT_S, MAX_SAMPLES = 0.01, 0.05, 400

# rocket.py's materials (g/cm^3) and wall/part sizes (mm)
DENSITY = {"paper": 0.70, "polystyrene": 1.05, "balsa": 0.16, "plywood": 0.68}
BODY_WALL, NOSE_WALL, FIN_THICKNESS = 0.8, 1.5, 3.2
MOUNT_ID, MOUNT_WALL, MOUNT_LENGTH, RING_THICKNESS = 18.3, 0.5, 76.0, 3.0
FIN_COUNT_FACTOR = {5: 2.37, 6: 2.74, 7: 2.99, 8: 3.24}

DEFAULTS = {"body_length": 300.0, "body_diameter": 40.0, "nose_length": 90.0, "nose_shape": "ogive",
            "fin_count": 3, "fin_root": 70.0, "fin_tip": 35.0, "fin_span": 60.0, "fin_sweep": 35.0}

ASSUMPTIONS = [
    "Simplified model: a point mass with drag in 2D, not RocketPy's 6-DOF.",
    f"C6 thrust curve (NAR certification), body Cd {DRAG_CD}, standard atmosphere at sea level.",
    f"{RAIL_M} m rod at {INCLINATION_DEG:g} deg; off the rod the thrust points along the airflow (weathercocking).",
    "No rotation: an unstable design flies the same path here; a real one would likely tumble.",
    f"Parachute {PARACHUTE_MM / 25.4:.0f} in, Cd {PARACHUTE_CD}, opens at burnout + {EJECTION_DELAY_S:g} s "
    f"(not before apogee) and fills in {CHUTE_OPEN_S:g} s.",
]


def _thrust_curve() -> list[tuple[float, float]]:
    points = [(0.0, 0.0)]
    for line in THRUST_CURVE.read_text(encoding="utf-8").splitlines():
        parts = line.split()
        if len(parts) == 2 and not line.startswith(";"):
            points.append((float(parts[0]), float(parts[1])))
    return points


CURVE = _thrust_curve()
BURNOUT_S = CURVE[-1][0]
# Cumulative impulse at each curve point (trapezoids), for the propellant burned so far.
_IMPULSE = [0.0]
for (t0, f0), (t1, f1) in zip(CURVE, CURVE[1:]):
    _IMPULSE.append(_IMPULSE[-1] + 0.5 * (f0 + f1) * (t1 - t0))
TOTAL_IMPULSE = _IMPULSE[-1]


def thrust(t: float) -> tuple[float, float]:
    """(thrust N, impulse delivered so far N s) at t seconds after ignition."""
    if t <= 0.0:
        return 0.0, 0.0
    if t >= BURNOUT_S:
        return 0.0, TOTAL_IMPULSE
    for i in range(1, len(CURVE)):
        t1, f1 = CURVE[i]
        if t <= t1:
            t0, f0 = CURVE[i - 1]
            f = f0 + (f1 - f0) * (t - t0) / (t1 - t0)
            return f, _IMPULSE[i - 1] + 0.5 * (f0 + f) * (t - t0)
    return 0.0, TOTAL_IMPULSE


def air_density(z_m: float) -> float:
    return 1.225 * max(1.0 - 2.25577e-5 * z_m, 0.0) ** 4.25588


def barrowman(length_mm, diameter_mm, nose_length_mm, nose_shape, count, root, tip, span, sweep, root_le_z):
    """(CP from the nose tip, mm): the same equations as cad/analysis.barrowman."""
    d, r = diameter_mm, diameter_mm / 2
    cn_nose = 2.0
    x_nose = (2 / 3 if nose_shape == "cone" else 0.466) * nose_length_mm
    mid_chord = math.sqrt(span * span + (sweep + tip / 2 - root / 2) ** 2)
    n_eff = 2 * FIN_COUNT_FACTOR.get(count, count / 2)
    cn_fins = (1 + r / (span + r)) * (4 * n_eff * (span / d) ** 2) / (1 + math.sqrt(1 + (2 * mid_chord / (root + tip)) ** 2))
    x_fins = (length_mm - root_le_z) + sweep / 3 * (root + 2 * tip) / (root + tip) + (root + tip - root * tip / (root + tip)) / 6
    return (cn_nose * x_nose + cn_fins * x_fins) / (cn_nose + cn_fins)


def _nose_shell(r: float, length: float, shape: str) -> tuple[float, float]:
    """(area mm^2, centroid height above the base mm) of the nose's curved surface."""
    def radius(h):  # radius at height h above the base
        if shape == "cone":
            return r * (1 - h / length)
        rho = (r * r + length * length) / (2 * r)
        return math.sqrt(max(rho * rho - h * h, 0.0)) + r - rho
    area = moment = 0.0
    n = 48
    for i in range(n):
        h0, h1 = length * i / n, length * (i + 1) / n
        r0, r1 = radius(h0), radius(h1)
        a = math.pi * (r0 + r1) * math.hypot(r1 - r0, h1 - h0)
        area += a
        moment += a * (h0 + h1) / 2
    return area, moment / area


def estimate_mass(p: dict[str, Any]) -> tuple[float, float, list[tuple[str, float, float]]]:
    """(liftoff grams, CG mm from the tail, [(part, g, z)]) from the parameters, with rocket.py's materials."""
    L, D, ln = float(p["body_length"]), float(p["body_diameter"]), float(p["nose_length"])
    r = D / 2
    cr, ct, s, sw, n = float(p["fin_root"]), float(p["fin_tip"]), float(p["fin_span"]), float(p["fin_sweep"]), int(p["fin_count"])
    items = []
    items.append(("body_tube", math.pi * (r * r - (r - BODY_WALL) ** 2) * L / 1000 * DENSITY["paper"], L / 2))
    area, h = _nose_shell(r, ln, p.get("nose_shape", "ogive"))
    items.append(("nose_cone", area * NOSE_WALL / 1000 * DENSITY["polystyrene"], L + h))
    mount_or = MOUNT_ID / 2 + MOUNT_WALL
    items.append(("mount_tube", math.pi * (mount_or ** 2 - (MOUNT_ID / 2) ** 2) * MOUNT_LENGTH / 1000 * DENSITY["paper"],
                  MOUNT_LENGTH / 2))
    ring = math.pi * ((r - BODY_WALL) ** 2 - mount_or ** 2) * RING_THICKNESS / 1000 * DENSITY["plywood"]
    items.append(("centering_ring_aft", ring, 3 + RING_THICKNESS / 2))
    items.append(("centering_ring_fwd", ring, MOUNT_LENGTH - 8 + RING_THICKNESS / 2))
    # One fin: the trapezoid (root LE at z=cr, root TE at 0, tip from z=cr-sw down to cr-sw-ct), its area centroid in z.
    pts = [(0.0, cr), (0.0, 0.0), (s, cr - sw - ct), (s, cr - sw)]
    a2 = cz = 0.0
    for (x0, z0), (x1, z1) in zip(pts, pts[1:] + pts[:1]):
        cross = x0 * z1 - x1 * z0
        a2 += cross
        cz += (z0 + z1) * cross
    fin_area, fin_z = abs(a2) / 2, cz / (3 * a2)
    for i in range(n):
        items.append((f"fin_{i + 1}", fin_area * FIN_THICKNESS / 1000 * DENSITY["balsa"], fin_z))
    items.append(("motor", MOTOR_LOADED_G, MOTOR_LENGTH_MM / 2))
    items.append(("recovery", RECOVERY_G, L - 50))
    total = sum(m for _, m, _ in items)
    return total, sum(m * z for _, m, z in items) / total, items


def fly(liftoff_g: float, diameter_mm: float, wind_mps: float = 0.0, parachute_mm: float = PARACHUTE_MM,
        ejection_delay_s: float = EJECTION_DELAY_S) -> dict[str, Any]:
    """The flight of a C6 rocket of this liftoff mass and body diameter: the result fields of cad/flight.simulate."""
    area = math.pi * (diameter_mm / 2000) ** 2
    chute_area = math.pi * (parachute_mm / 2000) ** 2
    dry_kg = (liftoff_g - MOTOR_PROPELLANT_G) / 1000
    ux, uz = math.cos(math.radians(INCLINATION_DEG)), math.sin(math.radians(INCLINATION_DEG))
    t_eject = BURNOUT_S + ejection_delay_s

    x = z = vx = vz = t = 0.0
    on_rail, t_rail, v_rail = True, None, 0.0
    t_apogee, apogee, max_speed = None, 0.0, 0.0
    t_open = None
    trace = [(0.0, 0.0, 0.0, 0.0, 0.0)]   # t, x, z, vz, speed

    def accel(t, x, z, vx, vz):
        f, impulse = thrust(t)
        m = dry_kg + (MOTOR_PROPELLANT_G / 1000) * (1 - impulse / TOTAL_IMPULSE)
        ax, az = vx - wind_mps, vz                       # air-relative velocity
        v_air = math.hypot(ax, az)
        cda = DRAG_CD * area
        if t_open is not None:
            cda += PARACHUTE_CD * chute_area * min(1.0, max(0.0, (t - t_open) / CHUTE_OPEN_S))
        drag = 0.5 * air_density(z) * cda * v_air
        if on_rail:   # along the rod only, and it sits on the pad until the thrust beats its weight
            along = (f - drag * v_air - m * G * uz) / m
            if vx * ux + vz * uz <= 0.0 and along < 0.0:
                along = 0.0
            return along * ux, along * uz
        dx, dz = (ax / v_air, az / v_air) if v_air > 0.1 else (ux, uz)
        return f * dx / m - drag * ax / m, f * dz / m - drag * az / m - G

    while True:
        # Midpoint step (RK2): the same in the C# copy.
        a1x, a1z = accel(t, x, z, vx, vz)
        hx, hz, hvx, hvz = x + vx * STEP_S / 2, z + vz * STEP_S / 2, vx + a1x * STEP_S / 2, vz + a1z * STEP_S / 2
        a2x, a2z = accel(t + STEP_S / 2, hx, hz, hvx, hvz)
        x, z = x + hvx * STEP_S, z + hvz * STEP_S
        nvx, nvz = vx + a2x * STEP_S, vz + a2z * STEP_S
        t += STEP_S
        if on_rail and (x * ux + z * uz) >= RAIL_M:
            on_rail, t_rail, v_rail = False, t, math.hypot(nvx, nvz)
        if t_apogee is None and not on_rail and vz > 0 >= nvz:
            t_apogee, apogee = t, z
        vx, vz = nvx, nvz
        if t_open is None and t_apogee is not None and t >= t_eject:
            t_open = max(t_eject, t_apogee)
        speed = math.hypot(vx, vz)
        if t_apogee is None:
            max_speed = max(max_speed, speed)
        if z < 0 and t > 0.5:   # landed: back to where it crossed the ground
            px, pz = trace[-1][1], trace[-1][2]
            f = pz / (pz - z) if pz != z else 1.0
            t = trace[-1][0] + f * STEP_S
            x = px + f * (x - px)
            z = 0.0
            trace.append((t, x, z, vz, speed))
            break
        if t > 900:
            break
        trace.append((t, x, z, vz, speed))
    t_land, drift = t, x

    dt = SAMPLE_DT_S * max(1, math.ceil(t_land / SAMPLE_DT_S / (MAX_SAMPLES - 2)))
    samples, k = [], 0.0
    for row in trace:
        if row[0] + 1e-9 >= k or row is trace[-1]:
            samples.append({"t": round(row[0], 3), "alt_m": round(row[2], 2), "downrange_m": round(row[1], 2),
                            "vz_mps": round(row[3], 2), "speed_mps": round(row[4], 2)})
            k += dt
    deployed = t_open is not None and t_open < t_land

    def event(key, label, at):
        row = min(trace, key=lambda r: abs(r[0] - at))
        return {"id": key, "label": label, "t": round(at, 2), "alt_m": round(row[2], 1), "speed_mps": round(row[4], 1)}

    events = [event("liftoff", "Liftoff", 0.0), event("rail_exit", "Off the rail", t_rail or 0.0),
              event("burnout", "Burnout", BURNOUT_S), event("apogee", "Apogee", t_apogee or t_land)]
    if deployed:
        events.append(event("deploy", "Parachute", t_open))
    events.append(event("landing", "Landing", t_land))
    events.sort(key=lambda e: e["t"])
    return {
        "model": "simplified",
        "wind_mps": float(wind_mps),
        "apogee_m": round(apogee, 1),
        "time_to_apogee_s": round(t_apogee or 0.0, 2),
        "max_speed_mps": round(max_speed, 1),
        "rail_exit_speed_mps": round(v_rail, 1),
        "rail_exit_time_s": round(t_rail or 0.0, 2),
        "burnout_time_s": round(BURNOUT_S, 2),
        "deploy_time_s": round(t_open, 2) if deployed else None,
        "descent_rate_mps": round(abs(trace[-1][3]), 1),
        "landing_time_s": round(t_land, 2),
        "flight_time_s": round(t_land, 2),
        "drift_m": round(abs(drift), 1),
        "landing_downrange_m": round(drift, 1),
        "liftoff_mass_g": round(liftoff_g, 1),
        "motor": "C6",
        "sample_dt_s": round(dt, 3),
        "trajectory_m": [[0.0, s["alt_m"], s["downrange_m"]] for s in samples if s["t"] <= (t_apogee or 0) + 1e-9],
        "events": events,
        "samples": samples,
        "assumptions": list(ASSUMPTIONS) + ([f"A steady {wind_mps:g} m/s wind along the launch heading."] if wind_mps else []),
        "warning": None if deployed else "It lands before the ejection charge fires: no parachute.",
    }


def _with_margin(out: dict[str, Any], margin: float) -> dict[str, Any]:
    out["stability_margin_cal"] = round(margin, 2)
    if margin < 1.0:
        note = (f"Static margin {margin:.2f} cal is under 1.0: a real rocket like this would likely weathercock or "
                "tumble; this point-mass model can't show that.")
        out["warning"] = " ".join(w for w in (out.get("warning"), note) if w)
    return out


def simulate_params(params: dict[str, Any] | None = None, wind_mps: float = 0.0) -> dict[str, Any]:
    """The rocket.py design with these parameters (missing ones at their defaults), masses estimated."""
    p = {**DEFAULTS, **(params or {})}
    total, cg_z, _ = estimate_mass(p)
    length = float(p["body_length"]) + float(p["nose_length"])
    cp = barrowman(length, float(p["body_diameter"]), float(p["nose_length"]), p["nose_shape"], int(p["fin_count"]),
                   float(p["fin_root"]), float(p["fin_tip"]), float(p["fin_span"]), float(p["fin_sweep"]),
                   float(p["fin_root"]))
    out = fly(total, float(p["body_diameter"]), wind_mps)
    out["cg_mm"], out["cp_mm"] = round(length - cg_z, 1), round(cp, 1)
    return _with_margin(out, (cp - (length - cg_z)) / float(p["body_diameter"]))


def simulate_spec(result, masses, wind_mps: float = 0.0) -> dict[str, Any]:
    """The fallback for cad/flight.simulate: the built part's real masses and CG, the simplified flight."""
    spec = result.rocket
    items = [(m, c[2]) for m, c in masses.values()] + [(p.mass_g, p.z_mm) for p in result.point_masses]
    total = sum(m for m, _ in items)
    cg_z = sum(m * z for m, z in items) / total
    f = spec.fins
    cp = barrowman(spec.length_mm, spec.diameter_mm, spec.nose_length_mm, spec.nose_shape, f.count, f.root_chord,
                   f.tip_chord, f.span, f.sweep, f.root_le_z)
    out = fly(total, spec.diameter_mm, wind_mps, spec.parachute_mm, spec.ejection_delay_s)
    out["motor"] = spec.motor
    return _with_margin(out, (cp - (spec.length_mm - cg_z)) / spec.diameter_mm)
