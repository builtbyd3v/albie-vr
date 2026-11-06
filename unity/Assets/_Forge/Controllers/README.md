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
| `Scripts/NetFile.cs` | Reads and checks the `inputs` and `net` blocks, shared by `learned` controllers and graph `model` nodes. |
| `Scripts/Learning/ObstacleData.cs` | The ML template's dataset: "obstacle ahead?" from the truck's sensors, labelled by physics (driving straight for 0.6 s at its speed, at least 1 m/s). Balanced and deterministic. `ObstacleData.Maker` makes it a few ms a frame, with the same samples as `Generate`. |
| `Scripts/Learning/ObstacleClassifier.cs` | Trains the classifier on the device a few ms a frame, with no allocations. It scores it against the best front-ray rule and writes `albie.model/1` and the graph `model` node. |
| `Scripts/Learning/ObstacleTemplate.cs` | **The bundled on-device ML template:** data, then training, then `WithLearnedBrake(truck_cruise)`. It saves `obstacle_ahead.json` and `truck_cruise_learned_brake.json`. |
| `Scripts/Learning/TemplateRun.cs` | One template run, shared by the sandbox's M and Albie's tools: makes the dataset and trains a few ms a frame, then scores the net and saves the files a step a frame, reports it to the notebook, and attaches or detaches the learned brake. |
| `Scripts/TemplateTools.cs`, `Resources/AlbieTools/controllers.json` | Albie's tools for the template: `template_train`, `template_status`, `learned_brake {on}`. They run on the headset with no hub. |
| `Scripts/ThingHosts.cs` | The Things running a controller, by name, so the tools can reach them. The sandbox registers its truck as `truck`. |
| `Scripts/Sandbox/ObstacleTrial.cs` | Closed-loop brake test: cruise straight at a cone or wall from seeded starts that a perfect brake can always stop from. |
| `Editor/MLTemplateCheck.cs` | **Forge > Controllers > Check ML Template**: 14 headless checks (data, label, training budget, the dataset made over frames, accuracy against the rule, the model file, bad nodes, ticking allocates nothing, closed loop, plugging in, `learned` still loads, and the three tools; every frame of a `template_train` run within budget). |
| `Editor/AllocationCounter.cs` | How every Controllers check counts allocations: the profiler's "GC Allocated In Frame" counter. `GC.GetAllocatedBytesForCurrentThread` reads 0 under Unity's garbage collector, so a check built on it can't fail. `AllocationCounter.Start()` first proves the counter counts a 1 KB probe, and throws if it doesn't. The counter counts every thread, so `Start()` also waits until nothing allocates for two 5 ms windows in a row. A check that starts background work should let it finish first (`NotebookFiles.Flush()`). The ML and Artifacts checks use it too. |
| `Scripts/Sandbox/MockTruckSim.cs` | The fake Thing (PROJECT_CONTEXT 16.5): the Python mock's truck and slalom, ported, with 0.1 m beam rays. |
| `Scripts/Sandbox/MockDriver.cs` | A scripted driver that writes `ManualInputs`, for auto laps and checks. |
| `Scripts/Sandbox/MockTrial.cs` | Scores a trained net on the mock slalom from 15 starts (sandbox and checks only). |
| `Scripts/Sandbox/ControllersSandbox.cs` | Desktop sandbox: keyboard driving, controller switching, live `max_speed`, lap recording, training. **9** rehearses the demo 5 times in a row on the live frame loop (about 2.5 min). An Autopilot run that hasn't finished in 30 s now counts as failed, so a stalled brain can't hang the demo. |
| `Editor/ControllersSandboxBuilder.cs` | **Forge > Controllers > Build Sandbox** builds `ControllersSandbox.unity`. |
| `Editor/ControllersCheck.cs` | **Forge > Controllers > Check Runner**: 9 headless checks. |
| `Editor/HotReloadCheck.cs` | **Forge > Controllers > Check Hot Reload**: 9 headless checks (reload state rules, bad files, watcher, live max_speed, fallback, training record). |
| `Editor/PretrainedFallbackBuilder.cs` | Builds the fallback from 4 scripted laps and 5 seeds, keeping the best on the mock slalom. **Use Controller File As Fallback** replaces it with a candidate that drove the real course. |
| `Editor/AutopilotCheck.cs` | **Forge > Controllers > Check Autopilot**: 7 headless checks (trainer match, bad files, max_speed, candidate fallback). |
| `Editor/FiveInARowCheck.cs` | **Forge > Controllers > Check Five In A Row** (about 30 s): the Nov 16 bar. The whole demo loop 5 times from scratch: two demo-style laps, 3 candidates trained 4 ms a frame, the Autopilot from a hand-placed start. It says who drove each time and gives the odds from 40 more drivers. |
| `Editor/BehaviourCloningCheck.cs` | **Forge > Controllers > Check Behaviour Cloning**: 8 headless checks (recording, files, frame budget, closed loop). |
| `Scripts/Sandbox/MockLaps.cs` | Two completed laps of the mock slalom through the real manual controller and recorder, with the scripted driver; a crashed lap is driven again. Used by `FiveInARowCheck` and the timing kit. |
| `Scripts/Timing/QuestTimingKit.cs` | **The Quest timing kit:** times idle frames, Autopilot training, Autopilot driving and the ML template on the frame loop, and shows the results on its panel (also logcat and JSON). |
| `Editor/QuestTimingBuilder.cs` | **Forge > Controllers > Build Quest Timing Panel / Scene / APK:** `Prefabs/QuestTimingPanel.prefab` (a ForgePanel variant with the kit), `ControllersQuestTiming.unity` (passthrough rig, panel 1 m ahead) and `Builds/ControllersTiming.apk` (that scene only; Main and the build settings' scene list aren't touched). |
| `Scripts/Thing/ControllerThing.cs` | **The plug for the Thing runtime:** `IThingBody` (sensors, speed, actuators) plus `ControllerThing`, which does the contract's call order each physics step, swaps controllers, records laps and registers the Thing with `ThingHosts`. |
| `Scripts/Thing/RayFan.cs` | The ray-fan sensor on real colliders (contract section 2.1): sphere casts, d0 leftmost, 1 = nothing hit. Allocation-free. |
| `Scripts/Thing/ExampleTruckBody.cs` | A worked `IThingBody`: the mock truck's driving model on a Unity transform, sensing real colliders. |
| `Editor/ThingRuntimeCheck.cs` | **Forge > Controllers > Check Thing Runtime**: the example truck on a slalom of real colliders. It checks plugging in, sensors against the mock, Albie's tools reaching it, 0 bytes per step, and the real-laps loop (record, save, train, drive). |

All three kinds run: manual, graph and learned. Graphs can also hold a `model` node, which is proposed in C-19 section 3.5.

## The ML template (Nov 14)

The bundled template runs with no network: **"train a model, then plug it into a Thing"**. In `ControllersSandbox.unity`, press **M**. It makes 3,000 samples and trains the obstacle-ahead net, about 4 ms a frame, saves it, and attaches `truck_cruise` with the learned brake in place of its hand-written one. Press **4** to attach it again, or **2** for the hand-written brake. The readout shows `P(obstacle ahead)` live. To see it brake, set `steer_gain` to 0: the hand-written brake hits the wall, and the learned one stops short of it.

```csharp
var t = ObstacleTemplate.Begin();               // nothing made yet
while (!t.Step(4.0)) yield return null;         // the data, then training, 4 ms a frame
var learned = t.WithLearnedBrake(truckCruise);  // a graph controller with the model node
host.Attach(ControllerFile.Parse(learned.ToString(), out _));
```

**By voice:** "train the ML template" calls `template_train`. It answers when training is done, with a `say` line: "98% right on examples it hadn't seen; the simple front-sensor rule gets 86%." "Put it on the truck" calls `learned_brake {on: true}`, and `{on: false}` gives the truck back what it had. Each run also writes a `training` entry in the lab notebook.

The Python templates (`ml-examples/cifar10`, `text-classification`) need the laptop hub to train (PROJECT_CONTEXT 16.8, Nov 14 decision).

## Timing on a Quest (the Nov 8 and Nov 14 checks)

One APK times ML A's on-device work on the headset, so nobody has to rig timers on the day.

1. In Unity: **Forge > Controllers > Build Quest Timing APK** (about 3 min; it switches to Android if it isn't already).
2. `adb install -r unity/Builds/ControllersTiming.apk`. **It has the same app id as Albie, so it replaces Albie on the headset.** Reinstall `Forge.apk` when you're done.
3. Open it, put the headset on and keep still for about 10 s. The panel 1 m ahead shows the results, starting with **HOLDS THE FRAME RATE** or **DROPS FRAMES**. A, X, a trigger or a pinch runs it again.
4. To get the numbers off the headset:
   - live: `adb logcat -s Unity | grep QuestTiming`;
   - or the JSON files: `adb pull /sdcard/Android/data/com.UnityTechnologies.com.unity.template.urpblank/files/quest_timing/`.

   Paste the JSON into PROJECT_CONTEXT 16.8.

What it runs:
1. 300 idle frames, the baseline.
2. Two demo laps, then 3 Autopilot candidates trained 4 ms a frame (key B).
3. The Autopilot drives the mock slalom from a hand-placed start (in the Editor, candidate 1 drives it). Each controller tick is timed.
4. The ML template, as `template_train` runs it: the dataset and training a few ms a frame, then the scoring and saving a step a frame.

How to read it:
- A slow frame took more than 1.5× the refresh interval.
- Training holds the frame rate when it adds no slow frames beyond idle's share plus 1%.
- The kit writes nothing to the lab notebook or the real controllers folder.
- Bytes allocated come from the profiler's "GC Allocated In Frame" counter. A build without it leaves them out rather than print a false 0.

| Measure | Editor (M4 Pro) | What to look for on the Quest |
|---|---|---|
| Autopilot training | 3 nets, about 130 frames (1.1 s), 3.97 ms a frame | More frames is fine (a slower CPU does less per 4 ms). Slow frames shouldn't rise above idle. |
| Autopilot driving | candidate 1 drove it, about 1 µs a tick | The same candidate. A tick well under 1 ms. |
| Template dataset | 3,000 samples in 2 frames (7.8 ms in all) | More frames is fine. Each one stays within the 4 ms budget. |
| Template, the whole run | 29 frames (data, training, saving), at most 4.1 ms a frame, 0 slow | Like Autopilot training: no frame much over 4 ms, slow frames no higher than idle. |

The panel prefab can also go into Main, to time training under Main's own load.

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

**The plug is `ControllerThing` plus your `IThingBody`** (`Scripts/Thing/`). Put both on the Thing. Your body does three things, and `ControllerThing` does everything else:
- **the controller's call order each `FixedUpdate`:** sense, tick (NaN to 0, clamp, `limits.speed`), record, apply;
- **swapping controllers** (`Attach(json)`; a bad file leaves the old one running);
- **recording laps** (`BeginLap` / `EndLap`);
- **registering the Thing** under its name, so Albie's tools reach it (`template_status`, `learned_brake`).

```csharp
public sealed class TruckBody : MonoBehaviour, IThingBody   // a sketch for a WheelCollider truck
{
    public Rigidbody body; public WheelCollider frontLeft, frontRight, rearLeft, rearRight;
    public Transform sensorOrigin; public LayerMask obstacles;   // obstacles: not the floor
    public float topSpeed = 3f, maxSteerDeg = 30f, maxTorque = 2f;

    public string[] SensorNames => MockTruckSim.SensorNames;     // ray_front.d0-d6, speed.forward
    public string[] ActuatorNames => MockTruckSim.ActuatorNames; // steer.angle, drive.throttle
    public string[][] MirrorSwap => MockTruckSim.MirrorSwap;
    public string[] MirrorNegate => MockTruckSim.MirrorNegate;
    public float ForwardSpeed => Vector3.Dot(body.linearVelocity, transform.forward);

    public void Sense(float[] sensors)
    {
        RayFan.Sense(gameObject.scene.GetPhysicsScene(), sensorOrigin.position, transform.forward, transform.up,
                     7, 120f, 0.1f, 2f, obstacles, sensors, 0);            // 7 rays over 120°, 0.1 m beam, 2 m range
        sensors[7] = Mathf.Clamp(ForwardSpeed / topSpeed, -1f, 1f);
    }

    public void Apply(float[] a, float dt)
    {
        frontLeft.steerAngle = frontRight.steerAngle = -a[0] * maxSteerDeg; // contract: + is left; WheelCollider: + is right
        rearLeft.motorTorque = rearRight.motorTorque = a[1] * maxTorque;
    }
}
```

Then on the same GameObject, add `ControllerThing` with `thingName = "truck"`, `thingVersion` from the spec, and `controller = truck_manual.json`. For two laps by hand:

```csharp
thing.BeginLap();  /* drive */  thing.EndLap("completed");
File.WriteAllText(path, thing.Recorder.Set.ToJson());   // albie.laps/1 (contract section 4), mirror map included
```

Then `BehaviourCloner` trains on them, as the sandbox's B key does.

**The rules that bite:**
- **`RayFan` follows contract section 2.1:** d0 is the leftmost ray, and 1 means nothing was hit. Each ray is a sphere cast with a `beam` radius, so a thin cone can't slip between two rays.
- **Keep the beam smaller than the truck's half-width.** A sphere cast can't see what it already touches. If something is inside the beam at the origin, `RayFan` falls back to plain rays.
- **Leave the floor out of `obstacles`.**
- **`speed.forward` is speed / the drive's rated top speed** (a fixed scale), not `max_speed`.
- **The actuators arrive already clamped to [-1, 1] and speed-limited.** Map them to torque and angle. Mind the steering sign: the contract's + is left, and `WheelCollider.steerAngle`'s + is right.
- **`ControllerThing` allocates nothing per step.** Keep `Sense` and `Apply` that way too.

**The worked example and its check:**
- `ExampleTruckBody` is the mock truck's driving model on a Unity transform, with `RayFan` on real colliders.
- **Forge > Controllers > Check Thing Runtime** builds the slalom from colliders in a preview scene and checks:
  1. plugging in and refusing a bad file;
  2. sensors against the mock's maths (cones and walls agree to 0.0 mm);
  3. Albie's tools reaching the truck;
  4. 0 bytes per step (about 6 µs, with 7 sphere casts);
  5. **the real-laps loop on Unity physics:** two laps driven by hand, recorded, saved and read back, then trained. The new candidates drove it from 3 hand-placed starts, candidate 1 every time;
  6. brains trained on mock laps drive the physics truck (candidate 1 from all 3 starts);
  7. **the pretrained fallback drives the physics truck from all 15 starts** it's scored on in the mock;
  8. promoting a candidate that drove into the fallback (below) works, and bad files are refused.

**The fallback on the real truck.** The bundled fallback is trained on the mock, whose walls are sphere casts too since 2026-11-06, so it carries over to a contract ray fan on real colliders. The real truck still differs from the mock (wheels, grip, the course), so once it drives, replace the fallback with a brain that drove the real course:
1. Drive two laps by hand on the headset, train (B, or `brain_train`), and let the Autopilot drive (P).
2. The candidate it keeps is in `persistentDataPath/controllers/`: `truck_autopilot.json` for candidate 1, `candidates/truck_autopilot_2.json` and `_3` for the others. `adb pull` it.
3. In Unity: **Forge > Controllers > Use Controller File As Fallback...**, pick the file, then commit `Resources/Controllers/truck_autopilot_fallback.json`.

**The lower-level path, with no MonoBehaviour,** is one host per Thing. In `FixedUpdate`:

```csharp
// once per Thing version
var io = new ControllerIO(sensorNames, actuatorNames);   // e.g. "ray_front.d0" ... "speed.forward"; "steer.angle", "drive.throttle"
var host = new ControllerHost(io);
string why = host.Attach(ControllerFile.Parse(json, out string parseError));   // null = running
ThingHosts.Register("truck", host);                      // so Albie's tools reach it; Unregister when it goes

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
