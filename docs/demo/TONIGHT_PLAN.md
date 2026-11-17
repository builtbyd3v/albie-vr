# Tonight: build the scripted demo and record the video

> Written 2026-11-16, 7:30 PM ET. Goal: by about **3:30 AM** Hudson has 5+ clean hero takes of `VIDEO_SCRIPT.md` plus the clips. Submission deadline is 2026-11-18, 12:00 PM PT (3:00 PM ET).
> This build is **for the video**. It is a scripted Director that plays the story beat by beat, on top of the real app. The real version judges can use is a separate list: `JUDGE_PATH.md`.

## 1. What we're building

A new module, `unity/Assets/_Forge/Demo/`, off by default and turned on by a flag. It has five parts:

| Part | Folder | What it does |
|---|---|---|
| **Director** | `Demo/Scripts/Director/` | Reads `Demo/Resources/demo_script.json`, plays beats in order: Albie's line (pre-recorded audio plus caption), then the actions at their times. It moves on when Hudson stops talking (any final speech-to-text result), when a piece raises a signal, or when the laptop remote says so. It also does the B1 palm summon. |
| **Research** | `Demo/Scripts/Research/` | Left panel: queries typing out, product cards streaming in with specs and fit checks, then the verdict card. Right panel: the model-hub lane. |
| **Pipeline** | `Demo/Scripts/Pipeline/` | Pull, then Train (curves, 20x badge, sample frames with boxes), then Push (commit card with a Confirm poke). |
| **Flight** | `Demo/Scripts/Flight/` | The drone (the quadcopter CAD mock) builds itself, spins up, hovers, gets the camera mounted, then follows the user round the room, plus the drone-cam window with a tracking box. |
| **Content and remote** | `Demo/Resources/DemoAudio/`, `DemoImages/`, `DemoModels/`, `hub/demo/` | Albie's lines as audio in his real voice, pictures of the made-up cameras, a camera 3D model, and a laptop page with Next / Back / Reset. |

The shared contract is already written: `Demo/Scripts/Core/DemoActions.cs` (`DemoActions.Register/Fire/Raise`, `DemoLayout.Get(slot)`). Every piece talks only through it.

## 2. Decisions I made (change any of them, nothing is built around them yet)

1. **How beats advance.** Three ways at once: Hudson finishing a sentence (any final speech-to-text result), a signal from a piece (for example the verdict card appearing), and a laptop remote (`http://<laptop>:<hub port>/demo`) for Next, Back and Reset. No hidden hand gesture, because his hands are in the shot.
2. **Albie's voice is pre-recorded**, in his real voice, one file per line. Timing is the same on every take. If the recording script fails, the Director falls back to live speech through the hub (`speech.speak`).
3. **Brand names stay out of the UI** (contest rule). Camera names are made up; the model hub cards use plain descriptive names.
4. **The data is scripted but realistic.** Specs and prices are plausible for real small camera modules; model cards look like real open tracking models.
5. **The drone-cam window renders the virtual room** (the scanned room mesh, drawn in a clean wireframe style) plus a body marker where Hudson's head is, and the tracking box sits on that marker. The real camera feed is not used.
6. **Same branch, same checkout.** Work happens on `feat/albie-os` in the main checkout (disk: 30 GB free, no worktrees), and each station touches only its own folder.
7. **Demo mode is off by default.** It turns on from `Demo/Resources/demo_config.json` (`"enabled": true`) or with the editor menu, so judges never see the scripted Director.

## 3. Schedule (ET)

| Time | What | Gate |
|---|---|---|
| 7:30 to 8:00 PM | Docs done, stations started. **Hudson:** put on the headset, make a 20 s test recording on the Quest and check passthrough and the mic are both in it. Charge the headset. | **G0:** the Quest recording shows passthrough. If it doesn't, plan the Link fallback now. |
| 8:00 to 10:00 PM | Wave 1: all five stations in parallel. Each piece works in `DemoSandbox` on the desktop rig. | |
| 10:00 PM | **G1:** the Director plays B1 to B7 in the editor, start to end, without errors. Placeholder visuals are fine. | Run through on the desktop rig. |
| 10:00 to 10:45 PM | Integration: the Director is on in the Main scene, first APK on the Quest. | **G2:** Hudson walks the script once in the headset and writes a list of what's wrong. |
| 10:45 PM to 12:30 AM | Wave 2: fix Hudson's list only, plus the look pass (type, spacing, motion, sound). | |
| 12:30 AM | **G3:** second APK, full dry run on the headset, timing check with a stopwatch (hero section under 2:10). | |
| 12:45 to 2:30 AM | Record: 5+ hero takes, then clips C1 to C6. | Footage copied off the Quest and backed up. |
| 2:30 to 3:30 AM | Buffer: re-takes, voiceover lines, hand-off to the edit. | |

If we fall behind, cut from the bottom of this list, never the middle:
1. The 3D camera model flying onto the drone (use a glow on the nose instead).
2. Sample frames with boxes in the Train step.
3. The scan shimmer before the flight test.
4. Going round furniture (plain follow with a height floor is enough).
5. The drone-cam window: last resort. Without it the flight test loses its best shot.

## 4. Stations (nodeterm, Sonnet 5.5)

| Station | Owns | Starts |
|---|---|---|
| `demo-director` | `Demo/Scripts/Director/`, `Demo/Scripts/Core/` (contract owner), `Demo/Editor/`, `Demo/Resources/demo_script.json`, `demo_config.json`, `DemoSandbox.unity` (built by a MenuItem), the bootstrap. **The only station that builds the APK and changes the Main scene.** | now |
| `demo-research` | `Demo/Scripts/Research/`, `Demo/Resources/research_data.json` | now |
| `demo-pipeline` | `Demo/Scripts/Pipeline/`, `Demo/Resources/pipeline_data.json` | now |
| `demo-flight` | `Demo/Scripts/Flight/` | now |
| `demo-content` | `Demo/Resources/DemoAudio/`, `DemoImages/`, `DemoModels/`, `hub/demo/`, `hub/scripts/demo_*` | now |

The briefs are in `docs/demo/briefs/`. Every station:
- Works only in its own folders. Doesn't touch `Main.unity`, `manifest.json` or Project Settings (only `demo-director` touches Main, after G1).
- **Shares one Unity editor.** Before a Play-mode run, create `unity/Temp/demo-editor.lock` with its name in it; delete it when done; wait if it exists (unless it's older than 10 minutes). Never leave the project not compiling: check `unity/Logs/Editor.log` after each save, and fix your own errors at once. Never edit another station's files to fix an error: tell the orchestrator.
- Commits only its own paths (`git add <your folders>`), small commits, no `git add -A` (the Pet models in `Pet/Models/` are paid assets and stay out of git).
- Stops at its brief's done line and reports with `report-outcome`. No open-ended polishing.

## 5. Action names (the contract)

| Action | Owner | Args | Signals raised |
|---|---|---|---|
| `albie.summon_hand` | Director | `{hand: "left"}` | `albie.on_hand` |
| `drone.open` | Flight | | `drone.built` |
| `drone.takeoff` | Flight | | `drone.hovering` |
| `drone.mount_camera` | Flight | `{from: [x,y,z]}` world position of the verdict card, optional | `drone.mounted` |
| `drone.follow_start` / `drone.follow_stop` | Flight | `{distance: 2.0}` | `drone.following` / `drone.home` |
| `dronecam.show` / `dronecam.hide` | Flight | | |
| `research.open` / `research.close` | Research | | |
| `research.cameras` / `research.models` | Research | | `research.cameras.done` / `research.models.done` |
| `research.verdict` | Research | | `research.verdict.shown`, then `research.added` when the button is poked |
| `pipeline.open` / `pipeline.close` | Pipeline | | |
| `pipeline.pull` / `pipeline.train` / `pipeline.push` | Pipeline | | `pipeline.pulled` / `pipeline.trained` / `pipeline.pushed` (after Confirm) |
| `demo.reset` | everyone | | |

Layout: `DemoLayout.Get(Slot.Left)` for Research, `Slot.Right` for the model-hub lane, `Slot.Low` for Pipeline, `Slot.Drone` for the hover point. The drone-cam window sits at the left wrist (or `Slot.Left` lowered, when the hand isn't tracked).

## 6. Recording setup

- **Laptop:** hub running (`hub/`), remote page open at `/demo`. Someone else can press Next if a beat sticks; otherwise Hudson works it from the chair.
- **Quest:** demo APK, guardian set for walking, room scan up to date (the flight test uses it), battery above 80%.
- **Room:** lights on, clear floor for the walk, the chair facing the open part of the room.
- **Capture:** on-device recording, started by hand from the Quest menu (Camera, then Record video), then back into the app. Fallback: Meta Quest Developer Hub's Record over USB. G0 decides which one keeps passthrough in the footage. Copy footage off the Quest by USB after each block of takes.
- **Reset between takes:** remote Reset, Albie goes back to idle, panels close, the drone parks.

## 7. Hand-off

After recording: footage in a shared folder, the take list (which take is clean for which beat) in `docs/demo/TAKES.md`, and the voiceover lines as a separate audio file.
