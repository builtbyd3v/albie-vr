# Controllers (Person 4 / ML A)

The brain of a Thing: the C-19 runner from `docs/CONTROLLER_CONTRACT.md`. Namespace `Forge.Controllers`. No asmdef.

| File | What |
|---|---|
| `Scripts/ControllerIO.cs` | Sensor and actuator buffers for one Thing (`part_id.channel` names, normalised values). |
| `Scripts/IThingController.cs` | `Bind` / `Tick` / `ResetState`, the contract's tick interface. |
| `Scripts/ControllerFile.cs` | Parses `albie.controller/1`: tunables, `$name` references, `limits.speed`. Errors use the contract's codes. |
| `Scripts/ManualController.cs` | `kind: manual` and `ManualInputs`, the fixed inputs that input sources write. |
| `Scripts/GraphController.cs` | `kind: graph`: const, gain, pid and rule nodes. |
| `Scripts/ControllerHost.cs` | Runs one controller for one Thing: swap with fallback, fault handling, clamp, speed governor, tunables. |
| `Scripts/Sandbox/MockTruckSim.cs` | The fake Thing (PROJECT_CONTEXT 16.5): the Python mock's truck and slalom, ported. |
| `Scripts/Sandbox/ControllersSandbox.cs` | Desktop sandbox: keyboard driving, controller switching, live `max_speed`. |
| `Editor/ControllersSandboxBuilder.cs` | **Forge > Controllers > Build Sandbox** builds `ControllersSandbox.unity`. |
| `Editor/ControllersCheck.cs` | **Forge > Controllers > Check Runner**: 9 headless checks. |

`kind: learned` is refused for now. It lands with the on-device trainer (16.8, Nov 10).

## For the Thing runtime (Person 2)

One host per Thing. In `FixedUpdate`:

```csharp
// once per Thing version
var io = new ControllerIO(sensorNames, actuatorNames);   // e.g. "ray_front.d0" ... "speed.forward"; "steer.angle", "drive.throttle"
var host = new ControllerHost(io);
string why = host.Attach(ControllerFile.Parse(json, out string parseError));   // null = running

// every physics step
WriteSensors(io.Sensors);                    // normalised, contract section 2
host.Step(Time.fixedDeltaTime, forwardSpeedMetresPerSecond);
ApplyActuators(io.Actuators);                // each in [-1, 1]; map to torque, steer angle, thrust
```

The host zeroes the actuators before each tick. It also turns NaN into 0, clamps everything to [-1, 1], and applies `limits.speed` to every `*.throttle` actuator. A failed `Attach` leaves the old controller running. A controller that throws stops and raises `Faulted`.

## For input sources (Person 1, Person 3)

Write `ManualInputs.Values[ManualInputs.IndexOf("stick.y")]` (cache the index) each frame, or `ManualInputs.Set(name, value)`. The inputs are `stick.x`, `stick.y`, `stick2.x`, `stick2.y`, `hand.roll`, `voice.throttle` and `voice.steer`, all in [-1, 1]. Only write when your value changes, so you don't overwrite another source.

## Sandbox

**Forge > Controllers > Build Sandbox**, then press Play in `ControllersSandbox.unity`:

| Key | Does |
|---|---|
| `1` | Manual controller |
| `2` | Cruise controller |
| `W` / `S` | Throttle |
| `A` / `D` | Steer |
| `[` / `]` | Lower / raise `max_speed` live |
| `R` | Reset the truck |

The mock files come from `hub/ml/make_controller_mocks.py` (run `python -m ml.make_controller_mocks` from `hub/`).
