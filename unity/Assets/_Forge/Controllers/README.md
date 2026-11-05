# Controllers (Person 4 / ML A)

The brain of a Thing: the C-19 runner from `docs/CONTROLLER_CONTRACT.md`. Namespace `Forge.Controllers`. No asmdef.

| File | What |
|---|---|
| `Scripts/ControllerIO.cs` | Sensor and actuator buffers for one Thing (`part_id.channel` names, normalised values). |
| `Scripts/IThingController.cs` | `Bind` / `Tick` / `ResetState`, the contract's tick interface. |
| `Scripts/ControllerFile.cs` | Parses `albie.controller/1`: tunables, `$name` references, `limits.speed`. Errors use the contract's codes. |
| `Scripts/ManualController.cs` | `kind: manual` and `ManualInputs`, the fixed inputs that input sources write. |
| `Scripts/GraphController.cs` | `kind: graph`: const, gain, pid and rule nodes. |
| `Scripts/LearnedController.cs` | `kind: learned`: runs the MLP from a controller file with `NNNetwork`. Checks every shape; no allocations per tick. |
| `Scripts/Autopilot.cs` | The trained candidates in order, then the optional pretrained fallback: `Engage` attaches one, `RunEnded(success)` keeps it or moves to the next. `UsingFallback` is for the Devpost disclosure. |
| `Scripts/ControllerFileWatcher.cs` | Polls a controller file and hot-reloads it into a host when it changes. `WriteAtomic` writes through a temp file. |
| `Scripts/Learning/TrainingLog.cs` | Where a finished training run is reported for the notebook (C-17). The Notebook module subscribes when it lands. |
| `Resources/Controllers/truck_autopilot_fallback.json` | The pretrained fallback. It ships in the APK. Built by **Forge > Controllers > Build Pretrained Fallback**. |
| `Scripts/ControllerHost.cs` | Runs one controller for one Thing: swap with fallback, fault handling, clamp, speed governor, tunables. |
| `Scripts/Learning/LapSet.cs` | `albie.laps/1` files: read and write, the same layout as the Python generator. |
| `Scripts/Learning/LapRecorder.cs` | Records (sensors → `host.Commanded`) each physics step, with no allocations. It drops idle stretches. |
| `Scripts/Learning/BehaviourCloner.cs` | On-device training with `NNNetwork`. `Step(ms)` trains within a frame budget; `ToControllerJson` writes the learned file. |
| `Scripts/Sandbox/MockTruckSim.cs` | The fake Thing (PROJECT_CONTEXT 16.5): the Python mock's truck and slalom, ported, with 0.1 m beam rays. |
| `Scripts/Sandbox/MockDriver.cs` | A scripted driver that writes `ManualInputs`, for auto laps and checks. |
| `Scripts/Sandbox/MockTrial.cs` | Scores a trained net on the mock slalom from 15 starts (sandbox and checks only). |
| `Scripts/Sandbox/ControllersSandbox.cs` | Desktop sandbox: keyboard driving, controller switching, live `max_speed`, lap recording, training. |
| `Editor/ControllersSandboxBuilder.cs` | **Forge > Controllers > Build Sandbox** builds `ControllersSandbox.unity`. |
| `Editor/ControllersCheck.cs` | **Forge > Controllers > Check Runner**: 9 headless checks. |
| `Editor/HotReloadCheck.cs` | **Forge > Controllers > Check Hot Reload**: 9 headless checks (reload state rules, bad files, watcher, live max_speed, fallback, training record). |
| `Editor/PretrainedFallbackBuilder.cs` | Builds the fallback from 4 scripted laps and 5 seeds, keeping the best on the mock slalom. Rebuild it on the real Thing runtime. |
| `Editor/AutopilotCheck.cs` | **Forge > Controllers > Check Autopilot**: 7 headless checks (trainer match, bad files, max_speed, candidate fallback). |
| `Editor/BehaviourCloningCheck.cs` | **Forge > Controllers > Check Behaviour Cloning**: 8 headless checks (recording, files, frame budget, closed loop). |

All three kinds run: manual, graph and learned.

## For the arena and voice (Person 5, Person 3)

```csharp
var autopilot = new Autopilot(candidateFiles, fallbackFile);   // trained candidates, then Resources "Controllers/truck_autopilot_fallback"
autopilot.Engage(host);                           // "Autopilot"
// ... when the arena run ends:
string line = autopilot.RunEnded(host, finished); // keeps the candidate, or attaches the next (reset the Thing for another run)
if (autopilot.UsingFallback) { /* the video and Devpost text must say so */ }
```

## For the code panel (Person 1): hot reload

```csharp
string why = host.Reload(json);   // from Update. null = applied at the next physics step
```

When only the tunables, version or notes change, the PID state is kept. Any other change resets it. A file that doesn't parse or bind is refused, `host.ReloadFailed` fires with the reason, and the running controller keeps driving. To follow a file on disk instead, use `new ControllerFileWatcher(host, path)` and call `Poll(Time.unscaledTimeAsDouble)` from Update.

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
| `3` | The mock autopilot file |
| `W` / `S` | Throttle |
| `A` / `D` | Steer |
| `[` / `]` | Lower / raise `max_speed` live |
| `R` | Reset the truck |
| `T` | Record a lap you drive. Press `T` again to stop; it also stops at the finish or a crash |
| `L` | Record a lap the scripted driver drives |
| `B` | Train 3 candidate nets on the completed laps, 4 ms a frame. Saves the laps and the controller files under `Application.persistentDataPath/controllers/` |
| `P` | Autopilot: drives with the trained candidates in order (the mock autopilot file if nothing is trained), then the pretrained fallback. A crash moves to the next one |
| `E` | Writes the running controller to `persistentDataPath/controllers/live/<name>.json` and watches it: save an edit and it hot-reloads live |

The mock files come from `hub/ml/make_controller_mocks.py` (run `python -m ml.make_controller_mocks` from `hub/`).
