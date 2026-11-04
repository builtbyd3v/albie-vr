"""`python -m ml.make_controller_mocks [out_dir]` (from hub/): C-19 controller mocks for hub-off sandboxes.

Builds the files in docs/CONTROLLER_CONTRACT.md from a small top-down truck sim, so the ML and controller work can
start before the Thing runtime (C-16) exists:

- laps/truck_v2.json      albie.laps/1: two slalom laps from a scripted driver, 50 Hz, 7-ray fan + speed.
- truck_manual.json       albie.controller/1 manual: stick throttle, hand-roll steering.
- truck_cruise.json       albie.controller/1 graph: PID cruise, steer away from the nearer side, brake rule.
- truck_autopilot.json    albie.controller/1 learned: an MLP trained here on the mirrored laps.

The autopilot's weights are **mock weights** for sandboxes and the brain view. They are not the demo's pretrained
fallback: that one is trained on the real truck (PROJECT_CONTEXT 16.8, Nov 12).

Default out_dir: unity/Assets/_Forge/Mocks/Resources/Mocks/controllers (Unity loads it with
Resources.Load("Mocks/controllers/...")). Output is deterministic, so a rerun with no code change leaves git clean.
Unity .meta files are written only where missing, so GUIDs stay stable.
"""

from __future__ import annotations

import json
import math
import sys
import uuid
from dataclasses import dataclass
from pathlib import Path

import numpy as np

HUB = Path(__file__).resolve().parents[1]
OUT = HUB.parent / "unity" / "Assets" / "_Forge" / "Mocks" / "Resources" / "Mocks" / "controllers"

# --- the mock truck (true scale, metres). Bicycle model; top speed = MAX_ACCEL / DRAG. -------------------------------
HZ = 50
DT = 1.0 / HZ
WHEELBASE = 0.28
MAX_STEER = math.radians(30)          # steer.angle 1.0 = 30 degrees left
MAX_ACCEL = 3.0
DRAG = 1.0
TOP_SPEED = MAX_ACCEL / DRAG          # 3 m/s: the scale for speed.forward
TRUCK_RADIUS = 0.12

# --- sensors: one ray fan, d0 leftmost (contract 2.1) ---------------------------------------------------------------
RAYS = 7
FOV = math.radians(120)
RAY_RANGE = 2.0

# --- the course: a slalom of cones, inside four walls ---------------------------------------------------------------
CONE_SPACING = 1.6
CONES = [(1.5 + CONE_SPACING * i, 0.0) for i in range(5)]
CONE_RADIUS = 0.08
WEAVE = 0.4                            # how far the driver passes to each side of a cone
WALLS = (-0.5, 9.8, -1.5, 1.5)         # x_min, x_max, y_min, y_max
FINISH_X = CONES[-1][0] + 1.0
LAP_TIMEOUT_S = 30.0

SENSORS = [f"ray_front.d{i}" for i in range(RAYS)] + ["speed.forward"]
ACTUATORS = ["steer.angle", "drive.throttle"]
MIRROR = {"swap": [[f"ray_front.d{i}", f"ray_front.d{RAYS - 1 - i}"] for i in range(RAYS // 2)],
          "negate": ["steer.angle"]}

MAX_SPEED_TUNABLE = {"value": 1.5, "min": 0.2, "max": 4.0, "unit": "m/s", "label": "Top speed"}


@dataclass
class Truck:
    x: float = 0.0
    y: float = 0.0
    heading: float = 0.0
    speed: float = 0.0

    def step(self, steer: float, throttle: float) -> None:
        steer = min(1.0, max(-1.0, steer))
        throttle = min(1.0, max(-1.0, throttle))
        accel = throttle * MAX_ACCEL - DRAG * self.speed
        self.speed = max(-1.0, self.speed + accel * DT)
        self.heading += self.speed / WHEELBASE * math.tan(steer * MAX_STEER) * DT
        self.x += self.speed * math.cos(self.heading) * DT
        self.y += self.speed * math.sin(self.heading) * DT

    def crashed(self) -> bool:
        if any(math.hypot(self.x - cx, self.y - cy) < CONE_RADIUS + TRUCK_RADIUS for cx, cy in CONES):
            return True
        x0, x1, y0, y1 = WALLS
        return not (x0 + TRUCK_RADIUS < self.x < x1 - TRUCK_RADIUS and y0 + TRUCK_RADIUS < self.y < y1 - TRUCK_RADIUS)


def _ray(px: float, py: float, dx: float, dy: float) -> float:
    """Distance to the first cone or wall along a unit ray, capped at RAY_RANGE."""
    best = RAY_RANGE
    for cx, cy in CONES:
        ox, oy = cx - px, cy - py
        along = ox * dx + oy * dy
        if along <= 0:
            continue
        miss2 = ox * ox + oy * oy - along * along
        if miss2 <= CONE_RADIUS * CONE_RADIUS:
            best = min(best, along - math.sqrt(CONE_RADIUS * CONE_RADIUS - miss2))
    x0, x1, y0, y1 = WALLS
    for wall, p, d in ((x0, px, dx), (x1, px, dx), (y0, py, dy), (y1, py, dy)):
        if abs(d) > 1e-9:
            t = (wall - p) / d
            if 0 < t < best:
                best = t
    return best


def sense(truck: Truck) -> list[float]:
    """The sensor row, in SENSORS order, normalised as in contract 2.1."""
    row = []
    for i in range(RAYS):
        a = truck.heading + FOV / 2 - i * FOV / (RAYS - 1)
        row.append(_ray(truck.x, truck.y, math.cos(a), math.sin(a)) / RAY_RANGE)
    row.append(max(-1.0, min(1.0, truck.speed / TOP_SPEED)))
    return row


def _path_y(x: float) -> float:
    """The weave the scripted driver follows: left of cone 0, right of cone 1, ... eased in and out."""
    first, last = CONES[0][0], CONES[-1][0]
    ease_in = min(1.0, max(0.0, (x - 0.3) / (first - 0.3)))
    ease_out = min(1.0, max(0.0, (FINISH_X + 0.3 - x) / (FINISH_X + 0.3 - last)))
    return WEAVE * math.cos(math.pi * (x - first) / CONE_SPACING) * ease_in * ease_out


def drive_lap(lap_id: int, seed: int, cruise: float, y0: float, heading0: float) -> dict:
    """One slalom pass by a pure-pursuit driver with a little smoothed hand noise, recorded at HZ."""
    rng = np.random.default_rng(seed)
    truck = Truck(y=y0, heading=heading0)
    xs, ys, noise = [], [], 0.0
    outcome = "aborted"
    for _ in range(int(LAP_TIMEOUT_S * HZ)):
        tx = truck.x + 0.5
        alpha = math.atan2(_path_y(tx) - truck.y, tx - truck.x) - truck.heading
        lookahead = math.hypot(tx - truck.x, _path_y(tx) - truck.y)
        noise = 0.9 * noise + 0.1 * float(rng.normal(0.0, 0.25))
        steer = math.atan(2 * WHEELBASE * math.sin(alpha) / max(lookahead, 0.3)) / MAX_STEER + noise
        throttle = DRAG * cruise / MAX_ACCEL + 1.5 * (cruise - truck.speed)
        steer, throttle = max(-1.0, min(1.0, steer)), max(-1.0, min(1.0, throttle))
        xs.append(sense(truck))
        ys.append([steer, throttle])
        truck.step(steer, throttle)
        if truck.crashed():
            outcome = "crashed"
            break
        if truck.x > FINISH_X:
            outcome = "completed"
            break
    return {"id": lap_id, "seconds": round(len(xs) * DT, 2), "outcome": outcome,
            "x": [[round(v, 3) for v in r] for r in xs], "y": [[round(v, 3) for v in r] for r in ys]}


# --- behaviour cloning, offline (the on-device trainer is ML A's Nov 8 goal) -----------------------------------------

def mirror(x: np.ndarray, y: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Left-right mirror of rows, using MIRROR (contract 2.3)."""
    x, y = x.copy(), y.copy()
    for a, b in MIRROR["swap"]:
        i, j = SENSORS.index(a), SENSORS.index(b)
        x[:, [i, j]] = x[:, [j, i]]
    for name in MIRROR["negate"]:
        if name in SENSORS:
            x[:, SENSORS.index(name)] *= -1
        else:
            y[:, ACTUATORS.index(name)] *= -1
    return x, y


def train_mlp(x: np.ndarray, y: np.ndarray, hidden: list[int], epochs: int, seed: int) -> dict:
    """Tanh MLP, MSE, Adam. Weights come out as [layer][to][from], the NNNetwork.cs layout."""
    rng = np.random.default_rng(seed)
    order = rng.permutation(len(x))
    n_val = len(x) // 7
    val, tr = order[:n_val], order[n_val:]
    mean, std = x[tr].mean(axis=0), x[tr].std(axis=0)
    std[std < 1e-6] = 1.0
    xn = (x - mean) / std
    sizes = [x.shape[1], *hidden, y.shape[1]]
    ws = [rng.uniform(-0.5, 0.5, (sizes[l + 1], sizes[l])) for l in range(len(sizes) - 1)]
    bs = [np.full(sizes[l + 1], 0.1) for l in range(len(sizes) - 1)]
    m = [np.zeros_like(p) for p in ws + bs]
    v = [np.zeros_like(p) for p in ws + bs]
    lr, b1, b2, step = 0.01, 0.9, 0.999, 0

    def forward(inp: np.ndarray) -> list[np.ndarray]:
        acts = [inp]
        for w, b in zip(ws, bs):
            acts.append(np.tanh(acts[-1] @ w.T + b))
        return acts

    for _ in range(epochs):
        rng.shuffle(tr)
        for start in range(0, len(tr), 64):
            idx = tr[start:start + 64]
            acts = forward(xn[idx])
            delta = (acts[-1] - y[idx]) * (1 - acts[-1] ** 2) * (2 / len(idx))
            grads_w, grads_b = [], []
            for l in range(len(ws) - 1, -1, -1):
                grads_w.insert(0, delta.T @ acts[l])
                grads_b.insert(0, delta.sum(axis=0))
                if l:
                    delta = (delta @ ws[l]) * (1 - acts[l] ** 2)
            step += 1
            for k, (p, g) in enumerate(zip(ws + bs, grads_w + grads_b)):
                m[k] = b1 * m[k] + (1 - b1) * g
                v[k] = b2 * v[k] + (1 - b2) * g * g
                p -= lr * (m[k] / (1 - b1 ** step)) / (np.sqrt(v[k] / (1 - b2 ** step)) + 1e-8)

    def loss(idx: np.ndarray) -> float:
        return float(((forward(xn[idx])[-1] - y[idx]) ** 2).mean())

    return {"sizes": sizes, "mean": mean, "std": std, "weights": ws, "biases": bs,
            "train_loss": loss(tr), "val_loss": loss(val)}


def run_learned(net: dict, max_speed: float) -> tuple[str, float]:
    """Closed-loop check: drive the slalom with the trained net and the runtime's speed governor (contract 3.1)."""
    truck = Truck()
    for n in range(int(LAP_TIMEOUT_S * HZ)):
        h = (np.array(sense(truck)) - net["mean"]) / net["std"]
        for w, b in zip(net["weights"], net["biases"]):
            h = np.tanh(w @ h + b)
        steer, throttle = float(h[0]), float(h[1])
        if truck.speed > max_speed:
            throttle = min(throttle, 0.0)
        truck.step(steer, throttle)
        if truck.crashed():
            return "crashed", n * DT
        if truck.x > FINISH_X:
            return "completed", n * DT
    return "aborted", LAP_TIMEOUT_S


# --- files ----------------------------------------------------------------------------------------------------------

def _fmt(o, depth: int = 0) -> str:
    """JSON with one line per flat list, so lap rows and weight rows stay readable and diffable."""
    pad = "  " * depth
    if isinstance(o, dict):
        items = [f"{pad}  {json.dumps(k)}: {_fmt(v, depth + 1)}" for k, v in o.items()]
        return "{\n" + ",\n".join(items) + "\n" + pad + "}" if items else "{}"
    if isinstance(o, list):
        if all(not isinstance(v, (dict, list)) for v in o):
            return json.dumps(o)
        return "[\n" + ",\n".join(f"{pad}  {_fmt(v, depth + 1)}" for v in o) + "\n" + pad + "]"
    return json.dumps(o)


def _meta(path: Path, folder: bool) -> None:
    meta = path.with_name(path.name + ".meta")
    if meta.exists():
        return
    importer = "folderAsset: yes\nDefaultImporter" if folder else "TextScriptImporter"
    meta.write_text(f"fileFormatVersion: 2\nguid: {uuid.uuid4().hex}\n{importer}:\n  externalObjects: {{}}\n"
                    "  userData: \n  assetBundleName: \n  assetBundleVariant: \n")


def _write(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(_fmt(data) + "\n")


def manual_controller() -> dict:
    return {
        "schema": "albie.controller/1", "name": "truck_manual", "kind": "manual", "version": 1, "thing": "truck",
        "bindings": [
            {"input": "stick.y", "actuator": "drive.throttle", "scale": 1.0, "deadzone": 0.05},
            {"input": "hand.roll", "actuator": "steer.angle", "scale": 1.0, "deadzone": 0.08, "expo": 0.3},
        ],
        "tunables": {"max_speed": dict(MAX_SPEED_TUNABLE, value=2.0)},
        "limits": {"speed": "$max_speed"},
        "notes": "Mock. Drive it with the hands transmitter; stick.y is throttle, hand roll steers.",
    }


def cruise_controller() -> dict:
    return {
        "schema": "albie.controller/1", "name": "truck_cruise", "kind": "graph", "version": 1, "thing": "truck",
        "tunables": {"max_speed": dict(MAX_SPEED_TUNABLE, value=1.2),
                     "steer_gain": {"value": 1.5, "min": 0.0, "max": 5.0, "label": "Steer away gain"}},
        "nodes": [
            {"id": "cruise", "type": "pid", "input": "speed.forward", "setpoint": round(1.2 / TOP_SPEED, 3),
             "kp": 0.8, "ki": 0.2, "kd": 0.0, "i_limit": 0.5, "output": "drive.throttle"},
            # d0 is the leftmost ray and 1 means clear: left clearer than right gives a positive (left) steer.
            {"id": "avoid", "type": "gain", "a": "ray_front.d0", "b": f"ray_front.d{RAYS - 1}",
             "k": "$steer_gain", "output": "steer.angle"},
            {"id": "brake", "type": "rule", "if": [f"ray_front.d{RAYS // 2}", "<", 0.15],
             "set": {"drive.throttle": -0.3}},
        ],
        "limits": {"speed": "$max_speed"},
        "notes": "Mock. Cruise at 1.2 m/s, steer away from the nearer side, brake when something is dead ahead.",
    }


def build(out: Path = OUT, epochs: int = 300) -> dict:
    laps = [drive_lap(1, seed=1, cruise=1.1, y0=0.0, heading0=0.0),
            drive_lap(2, seed=2, cruise=1.3, y0=0.08, heading0=0.05)]
    laps_file = {
        "schema": "albie.laps/1", "thing": "truck", "thing_version": 2, "controller": "truck_manual", "hz": HZ,
        "sensors": SENSORS, "actuators": ACTUATORS, "mirror": MIRROR,
        "notes": "Mock: a scripted driver on a 5-cone slalom (hub/ml/make_controller_mocks.py), not real laps.",
        "laps": laps,
    }

    done = [lap for lap in laps if lap["outcome"] == "completed"]
    x = np.array([r for lap in done for r in lap["x"]], dtype=float)
    y = np.array([r for lap in done for r in lap["y"]], dtype=float)
    mx, my = mirror(x, y)
    # Two laps of rays alone don't train a reliable slalom driver: in a sweep, 1 to 4 of 5 seeds finished
    # (PROJECT_CONTEXT 15.11). For the mock, keep the first seed whose net completes the course, and say so.
    tried = []
    for seed in range(10):
        net = train_mlp(np.vstack([x, mx]), np.vstack([y, my]), hidden=[8, 8], epochs=epochs, seed=seed)
        check, seconds = run_learned(net, MAX_SPEED_TUNABLE["value"])
        tried.append(check)
        if check == "completed":
            break

    autopilot = {
        "schema": "albie.controller/1", "name": "truck_autopilot", "kind": "learned", "version": 1, "thing": "truck",
        "inputs": [{"channel": c, "mean": round(float(mu), 4), "std": round(float(sd), 4)}
                   for c, mu, sd in zip(SENSORS, net["mean"], net["std"])],
        "net": {
            "sizes": net["sizes"], "activation": "tanh", "output_activation": "tanh",
            "weights": [[[round(float(v), 4) for v in row] for row in w] for w in net["weights"]],
            "biases": [[round(float(v), 4) for v in b] for b in net["biases"]],
        },
        "outputs": [{"actuator": "steer.angle", "scale": 1.0}, {"actuator": "drive.throttle", "scale": 1.0}],
        "tunables": {"max_speed": MAX_SPEED_TUNABLE},
        "limits": {"speed": "$max_speed"},
        "training": {"laps": ["laps/truck_v2.json"], "samples": int(len(x) * 2), "mirrored": True, "epochs": epochs,
                     "train_loss": round(net["train_loss"], 4), "val_loss": round(net["val_loss"], 4),
                     "device": "mock", "seed": len(tried) - 1,
                     "sim_check": f"{check} in {seconds:.1f} s; seeds tried: {', '.join(tried)}"},
        "notes": "Mock weights from scripted laps, for sandboxes and the brain view. Not the demo fallback. "
                 "Picked as the first training seed that finishes the mock slalom.",
    }

    _write(out / "laps" / "truck_v2.json", laps_file)
    _write(out / "truck_manual.json", manual_controller())
    _write(out / "truck_cruise.json", cruise_controller())
    _write(out / "truck_autopilot.json", autopilot)
    _meta(out, folder=True)
    _meta(out / "laps", folder=True)
    for f in ("laps/truck_v2.json", "truck_manual.json", "truck_cruise.json", "truck_autopilot.json"):
        _meta(out / f, folder=False)

    return {"laps": [(lap["id"], lap["outcome"], lap["seconds"], len(lap["x"])) for lap in laps],
            "train_loss": net["train_loss"], "val_loss": net["val_loss"], "seeds_tried": tried,
            "closed_loop": check, "closed_loop_seconds": round(seconds, 2)}


if __name__ == "__main__":
    target = Path(sys.argv[1]) if len(sys.argv) > 1 else OUT
    report = build(target)
    for lap_id, outcome, secs, frames in report["laps"]:
        print(f"lap {lap_id}: {outcome} in {secs} s, {frames} frames")
    print(f"autopilot: train loss {report['train_loss']:.4f}, val loss {report['val_loss']:.4f}")
    print(f"closed loop: {report['closed_loop']} after {report['closed_loop_seconds']} s "
          f"(seeds tried: {', '.join(report['seeds_tried'])})")
    print(f"wrote {target}")
