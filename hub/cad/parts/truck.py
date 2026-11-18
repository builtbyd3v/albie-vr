"""RC truck: a 1:16-style short-course truck (chassis, four wheels, body, battery). Save this file and it regenerates.

Millimetres, Z up, standing on z=0, nose towards +Y, the origin under the middle of the wheelbase. The parameters
and the driving physics are the headset's (unity/.../Controllers/Scripts/Thing/TruckDesign.cs, same names, same
formulas): on the device that model is the truth and works offline; this part lets the CAD panel show, analyse and
export the same truck. Every part needs a unique label: it becomes the GLB node name and the object ID Unity selects.
"""
import math

from build123d import Align, Box, Color, Compound, Cylinder, Location

from cad.part_api import (Analysis, BuildResult, Constant, Dimension, Explode, ExplodeMove, FixedMass, Metric, Param,
                          Verdict, band)

TITLE = "RC truck"
DESCRIPTION = "Short-course RC truck: wheelbase, track, wheels, ride height, tyres, motor, battery. Physics from the design."
GLYPH = "box"

PARAMS = [
    Param("wheelbase_mm", 170.0, 120, 280, "mm", 5, label="Wheelbase", group="Chassis", parts=("chassis", "body"),
          dimension="wheelbase", help="Front axle to rear axle. Longer turns wider and feels steadier."),
    Param("track_mm", 144.0, 110, 220, "mm", 2, label="Track width", group="Chassis", parts=("wheel_*", "chassis"),
          dimension="track", help="Left wheel centre to right. Wider is harder to roll over."),
    Param("wheel_diameter_mm", 68.0, 50, 110, "mm", 2, label="Wheel size", group="Chassis", parts=("wheel_*",),
          help="Tyre diameter. Bigger is faster but pushes less and sits taller."),
    Param("ride_height_mm", 21.0, 10, 60, "mm", 1, label="Ride height", group="Chassis", parts=("chassis",),
          dimension="height", help="Ground to the chassis. Higher clears bumps but tips sooner."),
    Param("steer_deg", 35.0, 20, 45, "deg", 1, label="Steering lock", group="Handling", parts=("wheel_f*",),
          help="Front wheel angle at full lock. More turns tighter."),
    Param("tyre_grip", 1.1, 0.5, 1.6, "", 0.05, label="Tyre grip", group="Handling", parts=("wheel_*",),
          help="Tyre compound (friction coefficient): 0.6 hard plastic, 1.1 rubber, 1.5 soft foam."),
    Param("motor_kv", 2000, 1000, 4500, "kV", 100, label="Motor", group="Power", parts=("motor",),
          help="rpm per volt. More is faster flat out but pushes less off the line."),
    Param("battery_cells", 2, 1, 4, "S", 1, label="Battery", group="Power", parts=("battery",),
          help="LiPo cells, 3.7 V and 45 g each. More is faster and heavier."),
    Param("mass_g", 460.0, 250, 1500, "g", 10, label="Chassis + body", group="Power", parts=("chassis", "body"),
          help="Without the battery. Heavier is slower to speed up, same top speed."),
]

PRESETS = {
    "Monster truck": {"wheel_diameter_mm": 110, "ride_height_mm": 50, "track_mm": 180},
    "Racer": {"ride_height_mm": 12, "track_mm": 170, "tyre_grip": 1.4, "motor_kv": 3000},
    "Tall and narrow": {"track_mm": 120, "ride_height_mm": 50},
}

# Fixed parts of the truck, as on the headset.
GEAR_RATIO, CELL_V, CELL_G, G = 14.0, 3.7, 45.0, 9.81
AIR, CD = 1.2, 0.4
COM_ABOVE_CHASSIS, ROOF_ABOVE_CHASSIS, TYRE_W, CHASSIS_UNDER_AXLE = 0.034, 0.109, 0.032, 0.013
REF_TOP, REF_ACCEL = 3.0, 3.0   # the original tuning the stock design drives as
DEFAULTS = {p.name: p.default for p in PARAMS}
STABILITY_BANDS = ((0.85, "bad"), (1.0, "warn"), (3.0, "ok"))  # roll-over limit over the grip limit: over 1 slides first


def geometry(p):
    r = p["wheel_diameter_mm"] / 2000
    chassis = max(p["ride_height_mm"] / 1000, r - CHASSIS_UNDER_AXLE)
    return r, chassis, chassis + COM_ABOVE_CHASSIS


def mass_kg(p) -> float:
    return (p["mass_g"] + p["battery_cells"] * CELL_G) / 1000


def aero_c(p) -> float:
    _, chassis, _ = geometry(p)
    return 0.5 * AIR * CD * (p["track_mm"] / 1000 + TYRE_W) * (chassis + ROOF_ABOVE_CHASSIS)


def _calibrate():
    """Drivetrain efficiency and the ESC's launch current, fitted so the stock design drives as the original tuning."""
    p = DEFAULTS
    r, m = p["wheel_diameter_mm"] / 2000, mass_kg(p)
    cq = aero_c(p) / m
    v_gear = REF_TOP * REF_ACCEL / (REF_ACCEL - REF_TOP ** 2 * cq)
    no_load = p["motor_kv"] * p["battery_cells"] * CELL_V / GEAR_RATIO / 60 * 2 * math.pi * r
    eta = v_gear / no_load
    kt = 60 / (2 * math.pi * p["motor_kv"])
    return eta, REF_ACCEL * m * r / (kt * GEAR_RATIO * eta)


EFFICIENCY, LAUNCH_CURRENT = _calibrate()


def physics(p) -> dict:
    """The headset's driving model (TruckDesign.Derive), full size, SI units."""
    r, chassis, h = geometry(p)
    kv, volts, m, mu = p["motor_kv"], p["battery_cells"] * CELL_V, mass_kg(p), p["tyre_grip"]
    v_gear = EFFICIENCY * kv * volts / GEAR_RATIO / 60 * 2 * math.pi * r
    force = LAUNCH_CURRENT * (60 / (2 * math.pi * kv)) * GEAR_RATIO * EFFICIENCY / r
    grip = mu * G
    accel = min(force / m, grip)
    lin, q = accel / v_gear, aero_c(p) / m
    top = (-lin + math.sqrt(lin * lin + 4 * q * accel)) / (2 * q)
    turn = p["wheelbase_mm"] / 1000 / math.tan(math.radians(p["steer_deg"]))
    tip = G * (p["track_mm"] / 2000) / h
    limit = min(tip, grip)
    return {"top_speed": top, "accel": accel, "traction_limited": force / m > grip, "turn_radius": turn,
            "grip_accel": grip, "tip_accel": tip, "tips_first": tip < grip, "corner_speed": math.sqrt(limit * turn),
            "com_height": h, "chassis_height": chassis, "mass_kg": m, "volts": volts}


def labelled(shape, label, rgb):
    shape.label, shape.color = label, Color(*rgb)
    return shape


BOTTOM = (Align.CENTER, Align.CENTER, Align.MIN)
PAINT, DARK, TYRE, RIM, PACK = (1.0, 0.42, 0.1), (0.14, 0.15, 0.18), (0.08, 0.08, 0.08), (1.0, 0.88, 0.3), (0.15, 0.3, 0.75)


def build(p) -> BuildResult:
    wb, tr, d = p["wheelbase_mm"], p["track_mm"], p["wheel_diameter_mm"]
    r = d / 2
    _, chassis_m, _ = geometry(p)
    ride = chassis_m * 1000
    kx, kz = tr / 144, wb / 170
    tyre_w = TYRE_W * 1000 * d / 68

    chassis = labelled(Box(85 * kx, 220 * kz, 10, align=BOTTOM).moved(Location((0, 0, ride))), "chassis", DARK)
    lift = ride - 21
    body_z = 51 + max(lift, (r - 34) * 1.6)
    tub = Box(110 * kx, 236 * kz, 30, align=BOTTOM).moved(Location((0, 0, body_z)))
    cab = Box(100 * kx, 70 * kz, 48, align=BOTTOM).moved(Location((0, -4 * kz, body_z + 30)))
    body = labelled(tub + cab, "body", PAINT)
    battery = labelled(Box(34 * kx, 70 + 18 * p["battery_cells"], 14 + 4 * p["battery_cells"], align=BOTTOM)
                       .moved(Location((0, -20 * kz, ride + 10))), "battery", PACK)
    motor = labelled(Cylinder(14, 30, rotation=(90, 0, 0)).moved(Location((0, -45 * kz, ride + 10 + 14))), "motor",
                     (0.55, 0.57, 0.6))

    wheels = []
    for name, x, y in (("wheel_fl", -1, 1), ("wheel_fr", 1, 1), ("wheel_rl", -1, -1), ("wheel_rr", 1, -1)):
        tyre = Cylinder(r, tyre_w, rotation=(0, 90, 0)) - Cylinder(r * 0.64, tyre_w * 0.4, rotation=(0, 90, 0)).moved(
            Location((x * tyre_w * 0.3, 0, 0)))
        wheels.append(labelled(tyre.moved(Location((x * tr / 2, y * wb / 2, r))), name, TYRE))

    parts = [chassis, body, battery, motor, *wheels]

    def fixed(shape, grams):
        c = shape.bounding_box().center()
        return FixedMass(grams, (c.X, c.Y, c.Z))

    m = p["mass_g"]
    return BuildResult(
        assembly=Compound(label="truck", children=[Compound(label="running_gear", children=wheels), chassis, motor,
                                                   battery, body]),
        masses={"chassis": fixed(chassis, 0.45 * m), "body": fixed(body, 0.2 * m), "motor": fixed(motor, 0.15 * m),
                **{w.label: fixed(w, 0.05 * m) for w in wheels},
                "battery": fixed(battery, CELL_G * p["battery_cells"])},
    )


def analyze(result: BuildResult, masses) -> Analysis:
    """The headset's driving numbers for this design: top speed, launch, turn radius and the cornering limit."""
    p = result.params
    ph = physics(p)
    margin = ph["tip_accel"] / ph["grip_accel"]   # over 1: it slides before it can tip
    tip_status = band(margin, STABILITY_BANDS)
    metrics = [
        Metric("top_speed", "Top speed", ph["top_speed"], "m/s", digits=1),
        Metric("accel", "Launch", ph["accel"], "m/s²", "warn" if ph["traction_limited"] else "ok", digits=1,
               note="wheelspin" if ph["traction_limited"] else ""),
        Metric("turn_radius", "Turn radius", ph["turn_radius"], "m", digits=2),
        Metric("corner_speed", "Full-lock speed", ph["corner_speed"], "m/s", digits=1,
               note="then it tips" if ph["tips_first"] else "then it slides"),
        Metric("stability", "Roll-over margin", margin, "", tip_status, 0.0, 3.0, STABILITY_BANDS,
               note={"bad": "rolls over", "warn": "tippy", "ok": "slides first"}[tip_status], display=f"{margin:.2f} x grip"),
    ]
    if ph["tips_first"]:
        v = Verdict("bad" if tip_status == "bad" else "warn", "Rolls over",
                    f"Track / 2 over the {ph['com_height'] * 1000:.0f} mm centre of mass is {ph['tip_accel'] / G:.2f} g, under "
                    f"the tyres' {p['tyre_grip']:.2f} g: a tight turn above {ph['corner_speed']:.1f} m/s tips it over.",
                    "stability")
    else:
        v = Verdict("ok", "Slides, won't tip", f"Tops out at {ph['top_speed']:.1f} m/s and turns in "
                    f"{ph['turn_radius']:.2f} m; past {ph['corner_speed']:.1f} m/s at full lock the tyres slide wide.",
                    "corner_speed")
    boxes = {s.label: s.bounding_box() for s in result.assembly.leaves}
    fl, rl, fr = boxes["wheel_fl"], boxes["wheel_rl"], boxes["wheel_fr"]
    top = max(b.max.Z for b in boxes.values())
    dims = [Dimension("wheelbase", "Wheelbase", (fl.min.X, fl.center().Y, 0.0), (rl.min.X, rl.center().Y, 0.0),
                      (-1.0, 0.0, 0.0), 15.0),
            Dimension("track", "Track width", (fl.center().X, fl.max.Y, 0.0), (fr.center().X, fr.max.Y, 0.0),
                      (0.0, 1.0, 0.0), 15.0),
            Dimension("height", "Height", (0.0, rl.min.Y, 0.0), (0.0, rl.min.Y, top), (0.0, -1.0, 0.0), 15.0)]
    up = (0.0, 0.0, 1.0)
    moves = [ExplodeMove("body", up, 90.0), ExplodeMove("battery", up, 45.0, order=1),
             ExplodeMove("motor", (0.0, -1.0, 0.6), 50.0, order=1)]
    for w in ("wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr"):
        x = 1.0 if boxes[w].center().X > 0 else -1.0
        moves.append(ExplodeMove(w, (x, 0.0, 0.0), 40.0, order=2))
    return Analysis(metrics=metrics, dimensions=dims, explode=Explode("stack", moves), verdict=v,
                    constants=[Constant("Drivetrain", f"{GEAR_RATIO:g}:1 gearing, 4WD",
                                        f"{EFFICIENCY:.0%} efficient · {LAUNCH_CURRENT:.1f} A launch limit (fitted to the stock truck)"),
                               Constant("Battery", f"{CELL_V:g} V and {CELL_G:g} g a cell", "LiPo")])
