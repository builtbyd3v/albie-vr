# C-19 Controllers: contract proposal

> **Status: PROPOSAL** (2026-11-03). Drafted by Person 4 / ML A (Satyabrata) for the Nov 4 goal in `PROJECT_CONTEXT.md` 16.3. It needs Person 2's agreement (the Thing runtime calls it) and the Core owner's acceptance before anyone builds on it as law. Until then, build against it as written (16.5: "Build against the proposals as written").
>
> Index entry: `PROJECT_CONTEXT.md` section 13, C-19. Background: sections 15.2 (parts kit), 15.4 (controllers), 15.6 (demo spine), D-40.

A **controller** is the brain of a Thing. It reads the Thing's sensors and writes its actuators once per physics step. There are three kinds, all swappable on the same Thing between two ticks:

| Kind | What drives it | Example | Owner |
|---|---|---|---|
| `manual` | The hands RC transmitter, the desk joystick or voice | Drive two laps yourself | Person 1 (inputs), Person 4 (runner) |
| `graph` | A list of rule and PID nodes | Cruise at `max_speed`, slow down near a wall | Person 4 |
| `learned` | MLP weights plus an input and output map | `truck_autopilot` after behaviour cloning | Person 4 |

This contract has four parts: the tick interface (C#), the channel names, the controller file, and the lap recording file that training reads.

---

## 1. Tick interface (Unity, C#)

Namespace `Forge.Controllers`, folder `unity/Assets/_Forge/Controllers/` (new, owned by Person 4). No asmdef, so it compiles into Assembly-CSharp like the other modules.

```csharp
namespace Forge.Controllers
{
    /// Sensor and actuator buffers for one Thing. The Thing runtime owns and fills it.
    public sealed class ControllerIO
    {
        public readonly string[] SensorNames;    // fixed per Thing version, e.g. "ray_front.d3"
        public readonly float[]  Sensors;        // written by the runtime before each Tick
        public readonly string[] ActuatorNames;  // e.g. "drive.throttle", "steer.angle"
        public readonly float[]  Actuators;      // written by the controller in Tick, all in [-1, 1]
        public int SensorIndex(string name);     // -1 if missing. Call in Bind only.
        public int ActuatorIndex(string name);   // -1 if missing. Call in Bind only.
    }

    public interface IThingController
    {
        string Kind { get; }                     // "manual" | "graph" | "learned"
        string Name { get; }                     // the file name without .json
        /// Once per Thing version or controller swap: resolve names to indices, allocate buffers.
        /// Returns null if it can run, else a reason the agent and the UI can show.
        string Bind(ControllerIO io);
        /// Every FixedUpdate. No allocations, no LINQ, no logging in the hot path.
        void Tick(ControllerIO io, float dt);
        /// Clears PID integrators and any history (Thing reset, respawn).
        void ResetState();
    }
}
```

**Call order, each `FixedUpdate` (Person 2's runtime):**
1. The runtime writes every sensor value into `io.Sensors`.
2. It calls `controller.Tick(io, Time.fixedDeltaTime)`.
3. It sanitises `io.Actuators`: NaN or infinity becomes 0, and everything is clamped to [-1, 1].
4. Then the speed limit is applied (`limits.speed`, section 3.1), and the actuators are mapped to physical units (torque, steer angle, thrust) from the part's spec parameters.

**Rules:**
- The physics step stays at the project's **0.02 s (50 Hz)**. Controllers must not assume a rate: use `dt`.
- One controller per Thing at a time. A swap happens between two ticks and calls `Bind` on the new controller first. If `Bind` returns a reason, the old controller keeps running and the reason is shown.
- If `Tick` throws, the runtime zeroes the actuators, marks the controller **faulted**, shows the reason and stops calling it. It never crashes the scene.
- The controller doesn't touch Rigidbodies, colliders or transforms. It only reads `Sensors` and writes `Actuators`.

**As built (ML A, 2026-11-06): a reference plug for the runtime.** `Controllers/Scripts/Thing/` has an optional adapter. It adds nothing to the contract; it's one way to meet it. The Thing implements `IThingBody`:
- `SensorNames` and `ActuatorNames`;
- the mirror map;
- `Sense(float[])`;
- `ForwardSpeed`;
- `Apply(float[], dt)`.

`ControllerThing` then does the call order above each `FixedUpdate`, records laps (section 4) and registers the Thing with `ThingHosts` (section 5). `RayFan` is the section 2.1 ray fan on real colliders. `ExampleTruckBody` is a worked example, and `ThingRuntimeCheck` runs it on a slalom of real colliders. The steps are in `Controllers/README.md`, "For the Thing runtime".

## 2. Channel names

A channel is `<part_id>.<channel>`. Part ids come from the Thing spec (C-16), so the truck spec decides that its motor is `drive` and its servo is `steer`. Channel names per part kind are part of this contract, and Person 2 confirms them against C-16.

All values are **normalised**, so a controller works at desk scale and at true scale, and a learned net sees inputs of about the same size.

### 2.1 Sensors

| Part kind | Channels | Range | Meaning |
|---|---|---|---|
| `sensor` (ray fan) | `<id>.d0` … `<id>.d{n-1}` | 0 to 1 | Hit distance / range. **1 = nothing hit.** `d0` is the leftmost ray, rays are evenly spread across the fan's angle. Each ray is a sphere cast with the part's `beam` radius (proposed default 0.1 m), so a thin object can't slip between two rays. |
| `sensor` (IMU) | `<id>.roll`, `<id>.pitch` | -1 to 1 | Angle / 90°. |
| | `<id>.yaw_rate` | -1 to 1 | Yaw rate / the part's `max_rate` (default 360°/s). Positive turns left. |
| `sensor` (speed) | `<id>.forward` | -1 to 1 | Forward speed / the drive's rated top speed from the Thing spec (C-16). This is a fixed scale for the net's inputs. It isn't the controller's `max_speed` tunable. |
| `sensor` (contact) | `<id>.hit` | 0 or 1 | Touching anything other than the floor this step. |
| `body` | `<id>.upright` | -1 to 1 | Dot of the body's up and world up. Below about 0.3 means it has rolled over. |
| `battery` | `<id>.charge` | 0 to 1 | Remaining charge. |

**As built (ML A, 2026-11-06): the mock truck now follows this at walls too.** Until 2026-11-06, `MockTruckSim` and the Python mock gave only cones the beam and cast plain rays at walls. A sphere-cast fan on real colliders read those walls up to 0.21 m nearer, and the pretrained fallback crashed on the physics example. Both mocks now sphere-cast walls: the mock and `RayFan` on colliders agree to 0.0 mm on cones and walls (`ThingRuntimeCheck`), and the rebuilt fallback drives the physics example from all 15 starts.

### 2.2 Actuators

| Part kind | Channels | Range | Meaning |
|---|---|---|---|
| `motor` | `<id>.throttle` | -1 to 1 | Fraction of max torque. Negative is reverse. |
| `servo` | `<id>.angle` | -1 to 1 | Fraction of the joint's range. For steering, positive turns left. |
| `rotor` | `<id>.thrust` | 0 to 1 | Fraction of max thrust (negative is clamped to 0). |
| `thruster` | `<id>.fire` | 0 or 1 | Ignite (one-shot, after the competition). |

### 2.3 Mirror map

Behaviour cloning doubles its data by mirroring each lap left to right (15.11). The runtime knows which channels are left-right pairs, so it publishes a mirror map with each Thing version:

```json
{ "swap":   [["ray_front.d0", "ray_front.d6"], ["ray_front.d1", "ray_front.d5"], ["ray_front.d2", "ray_front.d4"]],
  "negate": ["imu.roll", "imu.yaw_rate", "steer.angle"] }
```

The recorder writes it into each lap file (section 4). For a centred ray fan, `d_i` pairs with `d_{n-1-i}`.

## 3. Controller file: `albie.controller/1`

**Where it lives:**
- In the project repo, at `controllers/<name>.json` (D-25).
- On the device, at the same relative path inside the project folder that Person 5 picks for the notebook (2.2, Nov 6).
- Mocks go in `Mocks/Resources/Mocks/controllers/`.

Every field is plain JSON, so code mode can diff it, version it and roll it back.

### 3.1 Common fields

```json
{
  "schema": "albie.controller/1",
  "name": "truck_autopilot",
  "kind": "learned",
  "version": 3,
  "thing": "truck",
  "tunables": {
    "max_speed": { "value": 1.5, "min": 0.2, "max": 4.0, "unit": "m/s", "label": "Top speed" }
  },
  "limits": { "speed": "$max_speed" },
  "notes": "Learned from 2 laps, mirrored. v2 truck."
}
```

| Field | Rule |
|---|---|
| `schema` | Must be `albie.controller/1`. |
| `name` | Matches the file name. Lowercase letters, digits and `_`. |
| `kind` | `manual`, `graph` or `learned`. |
| `version` | Starts at 1. Bumped on every saved change, including a tunable change. |
| `thing` | The Thing spec name it was made for. It's a hint, not a lock: `Bind` checks the channels it actually uses. |
| `tunables` | Named numbers that a person or Albie can turn in code mode. `$name` uses one anywhere a number is allowed. Values outside `[min, max]` are rejected, not clamped. |
| `limits.speed` | A speed governor that every kind gets: when the forward speed passes this (in m/s), the runtime scales down the drive throttle. **This is how "turn `max_speed` up and the truck drives faster" (15.6, step 9) works for a learned controller too.** |

### 3.2 `manual`

```json
{
  "schema": "albie.controller/1", "name": "truck_manual", "kind": "manual", "version": 1, "thing": "truck",
  "bindings": [
    { "input": "stick.y",   "actuator": "drive.throttle", "scale": 1.0, "deadzone": 0.05 },
    { "input": "hand.roll", "actuator": "steer.angle",    "scale": 1.0, "deadzone": 0.08, "expo": 0.3 }
  ],
  "tunables": { "max_speed": { "value": 2.0, "min": 0.2, "max": 4.0, "unit": "m/s" } },
  "limits": { "speed": "$max_speed" }
}
```

Manual inputs are a fixed set that the input sources write each frame (Person 1 owns the sources):

| Input | Range | Sources |
|---|---|---|
| `stick.x`, `stick.y` | -1 to 1 | Pinch-drag thumb stick, desk joystick |
| `stick2.x`, `stick2.y` | -1 to 1 | Second stick (drones) |
| `hand.roll` | -1 to 1 | Hand roll on the transmitter |
| `voice.throttle`, `voice.steer` | -1 to 1 | "Go", "left", "stop". Held until the next command. |

`out = sign(v) * |v|^(1+expo) * scale`, after the deadzone. When several bindings write one actuator, they add up, then the result is clamped.

### 3.3 `graph` (rules and PID)

Nodes run **in list order** each tick. A later write to an actuator overrides an earlier one, so put the safety rules last.

```json
{
  "schema": "albie.controller/1", "name": "truck_cruise", "kind": "graph", "version": 1, "thing": "truck",
  "tunables": {
    "max_speed":  { "value": 1.2, "min": 0.2, "max": 4.0, "unit": "m/s" },
    "steer_gain": { "value": 1.5, "min": 0.0, "max": 5.0 }
  },
  "nodes": [
    { "id": "cruise", "type": "pid", "input": "speed.forward", "setpoint": 0.4,
      "kp": 4.0, "ki": 2.0, "kd": 0.0, "i_limit": 0.5, "output": "drive.throttle" },
    { "id": "avoid", "type": "gain", "a": "ray_front.d2", "b": "ray_front.d4",
      "k": "$steer_gain", "offset": 0.2, "output": "steer.angle" },
    { "id": "brake", "type": "rule", "if": ["ray_front.d3", "<", 0.15],
      "set": { "drive.throttle": -0.3 } }
  ],
  "limits": { "speed": "$max_speed" }
}
```

| Node `type` | Fields | Output |
|---|---|---|
| `const` | `value`, `output` | `value` |
| `gain` | `a`, optional `b`, `k`, optional `offset`, `output` | `k * (a - b) + offset`. `a` and `b` are channel names or numbers. |
| `pid` | `input`, `setpoint`, `kp`, `ki`, `kd`, optional `i_limit`, `output` | Standard PID on `setpoint - input`. The integral is clamped to ±`i_limit` (default 1). |
| `rule` | `if: [channel, op, value]`, `set: {actuator: value}` | Writes each value only when the condition holds. `op` is one of `<`, `<=`, `>`, `>=`. |
| `model` (proposed, 3.5) | `model: {inputs, net}`, `threshold`, `set` | Like `rule`, but the condition is a trained classifier's probability ≥ `threshold`. |

There's deliberately **no expression language and no script**: the agent writes these, and the file is data, not code (D-10, D-18). Sandboxed scripts are Phase 2.

### 3.4 `learned` (MLP)

```json
{
  "schema": "albie.controller/1", "name": "truck_autopilot", "kind": "learned", "version": 2, "thing": "truck",
  "inputs": [
    { "channel": "ray_front.d0", "mean": 0.62, "std": 0.28 },
    { "channel": "ray_front.d1", "mean": 0.55, "std": 0.30 },
    { "channel": "speed.forward", "mean": 0.41, "std": 0.15 }
  ],
  "net": {
    "sizes": [3, 8, 8, 2],
    "activation": "tanh",
    "output_activation": "tanh",
    "weights": [ [[0.1, -0.2, 0.3], "... 8 rows of 3"], ["... 8 rows of 8"], ["... 2 rows of 8"] ],
    "biases":  [ ["... 8"], ["... 8"], ["... 2"] ]
  },
  "outputs": [
    { "actuator": "steer.angle",    "scale": 1.0 },
    { "actuator": "drive.throttle", "scale": 1.0 }
  ],
  "tunables": { "max_speed": { "value": 1.5, "min": 0.2, "max": 4.0, "unit": "m/s" } },
  "limits": { "speed": "$max_speed" },
  "training": {
    "laps": ["laps/2026-11-10T14-02-11.json"], "samples": 1840, "mirrored": true,
    "epochs": 120, "seconds": 9.6, "train_loss": 0.021, "val_loss": 0.031, "device": "quest3"
  }
}
```

| Field | Rule |
|---|---|
| `inputs` | One entry per input neuron, in order. Each is standardised as `(x - mean) / std` (a `std` of 0 is treated as 1). |
| `net.sizes` | `[inputs, hidden..., outputs]`. `sizes[0] == len(inputs)` and `sizes[-1] == len(outputs)`. At most 4 hidden layers of up to 32 neurons each: it runs every tick on the Quest. |
| `net.weights` | `weights[l][to][from]` for layers 1 to L, the same layout as `NNNetwork.cs` (`weights[layer][to][from]`) without its unused layer 0. |
| `net.activation` | `tanh`, `relu`, `sigmoid` or `linear` (hidden layers). `output_activation` defaults to `tanh`. |
| `outputs` | One per output neuron. The value is `y * scale`, and `scale` may be a `$tunable`. |
| `training` | Provenance only. The runner ignores it. The notebook (C-17) copies it into the attempt record. Optional extras: `sim_check` (a sim result, as text), `candidates` (every candidate's block when several were trained), and `speed_jitter` / `ray_jitter` (the training-time input noise, when it was on). |

The weights are stored as plain decimals, not base64, so a diff in code mode shows which layer changed. A net this small is a few KB.

### 3.5 `model` node and `albie.model/1` (proposed, ML A, 2026-11-05: needs Person 2's OK)

The ML template (PROJECT_CONTEXT 15.5: "train a model, then plug it into a Thing") needs a trained classifier to drive part of a controller. The smallest way to do that without adding expressions or scripts is a graph node that works like a `rule` whose condition was learned:

```json
{ "id": "brake", "type": "model",
  "model": { "name": "obstacle_ahead",
             "inputs": [ { "channel": "ray_front.d0", "mean": 0.62, "std": 0.28 }, "... one per input" ],
             "net": { "sizes": [8, 8, 1], "activation": "tanh", "output_activation": "tanh", "weights": [], "biases": [] } },
  "threshold": 0.5,
  "set": { "drive.throttle": -1 } }
```

| Field | Rule |
|---|---|
| `model.inputs`, `model.net` | The same blocks and checks as a `learned` controller (section 3.4), with **one** output. The probability is `(y + 1) / 2` of the tanh output. |
| `threshold` | A probability from 0 to 1, or a `$tunable`. Default 0.5. |
| `set` | The same as `rule`: these actuators get these values on every tick where the probability is at or above `threshold`. |

- Put it last, like a safety rule, so it overrides the cruise throttle (section 3.3).
- The net is **inline**, so the controller file stays one self-contained file that code mode can diff. There's no file reference to resolve.
- The errors use the same codes: `unknown_channel`, `shape_mismatch`, `too_big`, `bad_value`. Paths are prefixed with the node, for example `nodes[3].model.net.sizes[0]`.
- It ticks with no allocations, like a `learned` controller.

**`albie.model/1`** is the trained classifier on its own (`controllers/obstacle_ahead.json`), for the notebook and code mode. It has `schema`, `name`, `version`, `thing`, `task` (`binary`), `classes`, `inputs`, `net` and `threshold`, plus `training` (data, accuracy, precision, recall, confusion, and the front-ray baseline it was compared with). The runner never reads it: the graph node carries its own copy of the net.

**As built:** `GraphController` (the `model` node), `NetFile` (the parser shared with `learned`), and `ObstacleClassifier.ToModelJson` / `ToGraphNode`. The checks are **Forge > Controllers > Check ML Template**. If Person 2 prefers a reference to a model file over an inline net, only `ToGraphNode` and the node's `model` field change.

## 4. Lap recording: `albie.laps/1`

The recorder (ML A) writes one file per recording session. Training reads it, and so does the NN view (ML B).

```json
{
  "schema": "albie.laps/1",
  "thing": "truck", "thing_version": 2, "controller": "truck_manual",
  "hz": 50,
  "sensors":   ["ray_front.d0", "ray_front.d1", "ray_front.d2", "ray_front.d3", "ray_front.d4", "ray_front.d5", "ray_front.d6", "speed.forward"],
  "actuators": ["steer.angle", "drive.throttle"],
  "mirror": { "swap": [["ray_front.d0", "ray_front.d6"], ["ray_front.d1", "ray_front.d5"], ["ray_front.d2", "ray_front.d4"]],
              "negate": ["steer.angle"] },
  "laps": [
    { "id": 1, "seconds": 18.4, "outcome": "completed",
      "x": [[0.91, 0.84, 0.77, 1.0, 1.0, 1.0, 1.0, 0.0], "..."],
      "y": [[0.0, 0.6], "..."] }
  ]
}
```

- `x[t]` lines up with `sensors`, and `y[t]` lines up with `actuators`. They're the values the manual controller sent, after the deadzone and before the speed limit.
- The recorder **drops idle frames** (throttle and steer both near 0 for more than 1 s), so a pause doesn't teach the net to stop.
- Recording is a preallocated ring buffer. It's written to disk only when a lap ends, never inside `FixedUpdate`.
- `outcome` is `completed`, `crashed` (contact or rolled over) or `aborted`. Training uses completed laps by default.

## 5. Events and tools

**Events (proposed, through `ForgeEvents` like the rest):**

| Event | Payload | When |
|---|---|---|
| `controller.updated` | `{project_id, thing_id, name, kind, version}` | A controller file was saved or hot-reloaded. |
| `controller.attached` | `{project_id, thing_id, name, kind}` | A Thing switched controller. |
| `controller.fault` | `{project_id, thing_id, name, reason}` | `Bind` refused or `Tick` threw. |
| `brain.training` | `{project_id, run_id, epoch, train_loss, val_loss, seconds, done}` | During on-device training, about 4 times a second. Feeds ML B's loss curve. |

**Tools (proposed names under C-14; descriptions go in `ML/Resources/AlbieTools/` JSON):**

| Tool | Does | Confirm? |
|---|---|---|
| `controller_list` | Lists the project's controllers and which one each Thing runs. | No |
| `controller_attach {thing, name}` | Swaps a Thing's controller. | No |
| `controller_tune {name, key, value}` | Sets a tunable, bumps `version` and hot-reloads. The diff goes to code mode. | No (it's a commit, so it can be undone) |
| `brain_record {start\|stop}` | Records laps while you drive. | No |
| `brain_train {laps?, epochs?}` | Trains on the device and writes `controllers/<thing>_autopilot.json`. | No |
| `autopilot_on` / `autopilot_off` | Attaches the learned or the manual controller. | No |
| `template_train` | Runs the on-device ML template (section 3.5): makes the data, trains, saves `obstacle_ahead.json` and `<base>_learned_brake.json`, and logs a notebook `training` entry. A slow tool: it answers when training is done (about 24 frames). | No |
| `template_status {thing?}` | The template's state (idle, training with the epoch, done with its accuracy and the rule it was compared with, failed) and whether the learned brake is on the Thing. | No |
| `learned_brake {on, thing?}` | Puts the learned brake on a Thing, or gives it back the controller it had before. | No |

None of these send anything off the device or spend money, so none of them need a confirm step under the 14.6 rules.

**As built (ML A, 2026-11-05):** the three template tools are in `Controllers/Resources/AlbieTools/controllers.json` (handlers in `TemplateTools.cs`), not in `ML/`: they're ML A's, and the `ml_` prefix is taken by the cloud tools. They reach a Thing through `ThingHosts`: whoever runs a Thing registers its `ControllerHost` by Thing name (`ThingHosts.Register("truck", host)`). Today that's the Controllers sandbox's mock truck. **Person 2's Thing runtime should do the same** when it attaches a controller.

## 6. Hot reload

1. A file change comes from the code panel, from `controller_tune` or from a git checkout.
2. The runner parses and validates the file **off the physics thread**, then `Bind`s the new controller.
3. The swap happens at the start of the next tick.
4. **Tunable-only change:** the PID integrators and other state are kept, so the truck doesn't jerk.
5. **Structure change** (nodes, net or bindings): `ResetState()` runs.
6. **Invalid file:** the running controller stays, and `controller.fault` explains why. A typo never stops the truck mid-lap.

## 7. Validation errors (returned as data, so the agent can fix them)

| Code | Example message |
|---|---|
| `unknown_channel` | `nodes[1].a: no sensor "ray_front.d9" on this truck (it has d0-d6)` |
| `shape_mismatch` | `net.weights[2]: 2 rows of 8 expected, got 2 rows of 6` |
| `tunable_out_of_range` | `max_speed 9.0 is above its max of 4.0` |
| `bad_value` | `net.weights[0][3][1] is NaN` |
| `too_big` | `net.sizes: 4 hidden layers of up to 32 neurons each on the device` |

## 8. Questions for Person 2 (to settle by Nov 4)

1. Are the part kinds and channel names in section 2 right for C-16, and is `<part_id>.<channel>` the naming you want?
2. Is one ray fan per `sensor` part fine, with `rays` (count) and `fov` (degrees) as its spec parameters? 7 rays over 120° is proposed for the truck.
3. Does the runtime own the speed governor (`limits.speed`), or should each controller apply it? Proposed: the runtime, so it's the same for all three kinds.
4. Can the runtime publish the mirror map (2.3) with each Thing version?
5. Does `WheelCollider` vs `ArticulationBody` (your Nov 5 decision) change any channel? Proposed: no, the channels are the same either way.
6. Can the ray fan cast spheres (`Physics.SphereCast`, radius `beam`, default 0.1 m) instead of plain rays? With plain rays, 7 rays over 120° are about 0.5 m apart at 1.6 m, so a 0.16 m cone flickers in and out of view. On the mock slalom, a 0.1 m beam raised how often a learned driver finished from 26-48% to 64-70% for one training run (ML A sweeps, 2026-11-04).

## 9. What's out of this contract (for now)

- ONNX policies and the Inference Engine package. The default is our own C# forward pass (2.2, Person 4, Nov 8).
- Sandboxed script controllers (Phase 2, D-18).
- Cloud RL policies. They'd arrive as `learned` files with the same schema, after the competition.
