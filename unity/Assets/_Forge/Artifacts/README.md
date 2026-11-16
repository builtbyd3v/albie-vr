# Artifacts (Person 5: Satyabrata)

Generated tools, visuals and simulations (C-12, `docs/ARTIFACT_CONTRACT.md`). An artifact is a folder of plain data that `ArtifactHost` reads, validates (`ArtifactValidator`) and builds, with no scripts (D-18).

## "Make me a slalom" (Nov 10)

Albie calls `artifact_create {template: "slalom"}`. That call:
1. copies `Resources/ArtifactTemplates/slalom/` into the library, at `persistentDataPath/artifacts/slalom/`, linked to the open notebook project;
2. opens the artifact. On the first open, a permissions sheet asks to use the room;
3. runs the `floor_slalom` generator (`Scripts/Room/FloorSlalom.cs`), which finds free floor in the real room and places the course, around the couch if there is one;
4. saves the layout (`Scripts/Room/ArtifactLayouts.cs`) with a spatial-anchor uuid (`room.anchor`), so reruns in that room reuse it;
5. builds the cones (`array`), the gates, start and finish (`zone`) and the guide line, on the real floor at true scale;
6. scores a run when `artifact_run` arms it: the bound Thing entering start starts the timer, every gate in order plus finish completes, rolling over / missing a gate / timing out fails. The record is saved and attached to the notebook.

**When the course doesn't fit in full.** If the requested course doesn't fit, the generator follows C-12's fit rules:
- First it reduces the spacing, then the number of gates. The tool result's `say` tells Albie what to tell the person, for example "This room is tight, so I made a shorter course: 4 gates, 0.6 m apart."
- Then it makes a narrow course: 3 gates with 0.8 m of free floor across instead of 1.2 m, and the spacing down to 0.5 m. The gates stay where they were, so a narrow course scores the same. Albie says it's a short, narrow course and to drive carefully. The layout saves the width it used (`fit.placed.lane_m`), so reruns reuse it. A hoop course does the same by putting its hoops in a straight line.
- If even the narrow course doesn't fit, the result is `no_free_floor`, with the size it needs.
- If the room has no scan, the result is `no_room_data`.
- Nothing is ever placed from a guess.

**Where the room comes from.** `ArtifactHost.Room` sets the room. When it's null, the host uses MRUK's current room (`MrukFloorPlan`). Checks and sandboxes use `MockFloorPlan` instead.

**Runs.** `ArtifactRun` ticks in `FixedUpdate` with no allocations. Zones use `ArtifactZone.Contains` (no trigger colliders). A finish that hasn't seen `@course.gates` entries fails as `missed_gate`. Closing the artifact while a run is going writes `aborted`. Records live at `artifacts/<id>/runs/run_0001.json` and are never overwritten.

**Saved anchors (Nov 12).** Each new layout gets a uuid from `ArtifactAnchors`. On a Quest that also calls `OVRSpatialAnchor.SaveAnchorAsync`. If the uuid is gone, the host makes a new layout and Albie says "I can't find the saved course in this room, so I made a new one." Furniture that has moved still uses the existing "Something in the room has moved" line. Old runs stay, but they no longer compare like for like.

## "Make me a hoop course" (Nov 14)

Albie calls `artifact_create {template: "hoop_course"}`. It works the same way as the slalom, with the `room_hoops` generator (`Scripts/Room/RoomHoops.cs`):
- It places standing hoops for a drone: 4 hoops 1.0 m apart by default, weaving ±0.21 m, each a 0.7 m frame with its centre 1.0 m up. They go on free floor along the room's main axis and use the same search and fit rules as the slalom: less spacing first (down to 0.6 m), then fewer hoops (down to 3).
- **Small rooms.** The mock 4.5 × 3.8 m bedroom gets 3 hoops 0.6 m apart, and Albie says so.
- **Missing labels.** The hoop course doesn't look for any furniture label, so an unlabelled scan places normally. If the scan has no ceiling, the course is still placed, and Albie says it couldn't check the ceiling and how tall the hoops are.
- **Low ceilings.** A ceiling lower than the frame top (1.37 m) plus `clearance_m` gives `no_headroom`, with both heights.
- **Scoring.** A run counts hoops in order (`hoops_passed`, else `missed_hoop`). `collisions` counts touches on any part of the frame (`hit_count` over a list of nodes). A drone that stays upside down for 1 s fails as `crashed`.

The template needs host runtime 2 (`ArtifactSchema.HostRuntime`), because adding a generator type is a host change.

**v1 vs v2.** That's a notebook compare (`notebook_compare`), not `artifact_compare`. v1 flipped during free driving, before the slalom existed, so it is labelled "free drive, not this course".

**Demo step 7 on the scoreboard.** The slalom's scoreboard has a `versions` row (`ArtifactVersionsRow` in `Scripts/ArtifactWidgets.cs`). It shows the open project's previous version against its current one, for example "v1 rolled over (free drive) · v2 8.6 s", and updates by itself when a run finishes or a version is cut. The slalom template needs host runtime 5 for it.

**The brain beat in the app** (`Scripts/SlalomBrain.cs`, PROJECT_CONTEXT 16.8, "room slalom, part 3"). In the slalom sandbox, after 2 (make me a slalom), press **B** for the brain truck, which is ML A's physics truck (`ExampleTruckBody`) with a `ControllerThing`, standing in for Person 2's Thing runtime. Then:
- **L** records a lap you drive by hand: W/S hold 0.8 m/s, A/D steer, through the `truck_manual` controller. **K** records a scripted lap.
- Each lap is a scored run of the open slalom, and only completed laps train.
- **T** trains 3 candidates, 4 ms a frame, and writes the notebook's training entry.
- **P** runs the Autopilot: each candidate, then the pretrained fallback, gets a scored run until one completes.
- A touch on a cone or a wall aborts the run, and the truck parks after each run.
- The mock rooms' walls and furniture have colliders, so the truck's rays see them. **Forge > Artifacts > Check Brain Loop (Play)** runs every mock room through the whole loop in Play mode, with storage in a scratch folder.
- **In the app (Main), with no keys:** when a truck slalom is built (after the Allow tap, or a relayout), `ArtifactBootstrap.EnsureBrain` puts the brain truck at its start. The truck is then the active brain loop, so `brain_record {start: true, laps: 2, then_autopilot: true}` and ML B's brain panel drive it. Laps are driven with the hands (`HandsTransmitter`, Nov 15: pinch to drive, tilt the hand to steer, left-hand pinch to reverse; the lap's status says so for Albie to read) or the Touch thumbstick. The truck's sensors see the cones and Main's EffectMesh colliders (walls, tables, couches, storage, beds). Its status has no sandbox key hints, because Albie reads it aloud.
- **The truck follows the host** (`ArtifactHost.Shown` and `Closed`, which `SlalomBrain.Spawn` subscribes to, in the sandbox and the app alike). When the scene comes down (a close, another artifact opening, even one waiting on its permissions sheet) the truck hides: unseen, parked, no longer the host's Thing, but still the brain loop, so `brain_record` with the heart open says "open the slalom first" instead of losing the truck. When a truck course is built it's back at the start. Nothing is left standing on the floor beside the heart.
- **The host steps aside for other panels.** In the app, `ArtifactHost.ComeToUser` looks for other `ForgePanel`s (the Hands panel, desk windows) before it picks its spot: if the host or its own panel would land within `PanelClearance` (0.5 m, on the floor plan) of one, it turns 40° then 80° to either side for a clear spot, at the same distance from the user, then tries the same fan 0.55 m away. Checked in **Forge > Check Artifacts** (`Placement`).
- **The host lands on a table and keeps out of the walls** (Nov 14). `ComeToUser` reads the same floor plan the courses use (`Room`, else MRUK's current room). The biggest `TABLE` whose top is 0.2 to 1 m below the eyes (a desk or dining table when standing, those and the coffee table when sitting) gets the host on its top when the spot is on the table or within `TableSnap` (0.4 m) of its edge, pulled `TableMargin` (0.12 m) inside the edge; the panel opens 0.3 m above it as before. A spot that puts the host or its panel nearer than `WallMargin` (0.3 m) to the room's bounds is skipped: the panel first swaps to the left (and the host moves right of centre), then the host turns aside, then it comes closer. A table that's too low is ignored and the host floats at desk height, clear above it. Six more `Placement` checks cover the desk, the pull onto a side table, the coffee table, the wall ahead, the wall on the right (panel swaps sides) and the swap back.
- **Albie says where it put it** (Nov 15). `ComeToUser` writes down the spot it picked as `ArtifactHost.PlacementNote`: "It's on the table in front of you.", "It's on the table to your right.", "It's to your left.", or null when it stands straight ahead in the air as always. `artifact_create` and `artifact_open` append it to the result's `say` after the layout and data lines, so Albie points the person at the artifact instead of leaving them to look for it. Three more `Placement` checks: nothing to say straight ahead, the desk line, the side when turned. `ArtifactShot.Take(..., room: true)` (Editor) frames a floor course and the truck too, for stills.

**Scoring a body that moves in physics.** These apply to any Thing, not just the truck:
- A run counts a hit (`hit_count`) when the Thing comes within its radius of the collider's surface (`ClosestPoint`). It used to use the collider's bounds grown by the radius, which counted a clean weave 0.12 m off every cone as 4 hits.
- Arming a run syncs physics, so a course built that frame is where it's drawn.
- `ArtifactThing.SpeedSource` gives the body's own speed: runs tick in FixedUpdate, where a per-frame position difference reads 0.
- `ArtifactThing.ProbeHeight` lifts the scored point off the floor.

## "What shipped this week?" (repo activity)

Albie calls `artifact_create {template: "repo_activity"}`. This shows commits as a small city on a base: one block per commit, height for lines changed, and colour for the author. The data is read through the `array` node's `source: {table, grid}` (C-12 section 7). The rows are never typed into the scene.
- **Where the rows come from** (`Scripts/ArtifactData.cs`). The host tries `ArtifactData.Provider` first. That's the hook for Person 3's connector host, and it's null until that's on main. Next it tries the artifact's own `data/<name>.json`, then the bundled sample in `Resources/ArtifactData/`. Today that means the bundled sample: 18 made-up commits.
- **Sample data** always gets a yellow "Sample data" label, and Albie says the commits are made up. A commit with no line count is a grey stub, not a zero.
- **The local fixture connector** (`Scripts/LocalRepoConnector.cs`, Nov 9). This is the stand-in section 16.5 asks for until Person 3's connector host lands. It installs itself as `ArtifactData.Provider` only when nothing else has, so the real host replaces it with no change here. It answers only `connector:source_control/list_commits`, with this repo's real commits from the last 7 days: up to 120, newest first, merges left out, in the sample's five columns. `lines_changed` counts text lines, leaving out `.meta` files; a binary-only commit is null (a grey stub).
  - **On a computer** it runs `git log` on the checkout, cached for 60 s. The data comes from `connector`, isn't sample data, and Albie says nothing about it.
  - **On a Quest** (no git) it reads the copy the build step saved: `Editor/RepoSnapshotBuild.cs` runs before every player build, or from **Forge > Artifacts > Save Repo Commits For Quest**. The copy is marked `saved_copy`, so Albie says "This is a saved copy of the data, from November 9." It's git-ignored (`Resources/ArtifactData/Repo/.gitignore`), so builds don't leave a changed file.
  - **With neither**, the bundled sample comes back with its "Sample data" label, as before.
  - The template's grid has no fixed column count now, so any number of commits makes a square city, and `grid.base` draws a plate sized to it.
- **Albie's own artifacts.** `artifact_create {manifest, scene}` validates the artifact and saves it versioned. If it's invalid, the result is `status: "invalid"` with every error, and nothing is saved. A source this headset has no copy of opens as `no_data`.

The template needs host runtime 3.

## Models and timelines (`gltf`, `timeline`, `animate`)

These are the last Phase 1 node kinds. The `launch` fixture uses all of them: the CAD mocks' v1 rocket on a pad, and a Launch button that restarts a 3.5 s timeline. On that timeline the rocket climbs and leans, and its flame lights, grows, turns orange and burns out.
- **Models** (`Scripts/ArtifactGltf.cs`). The host only loads a `.glb` that the manifest lists, from the artifact's own `assets/`, with a matching sha256. It checks the hash before building anything, so a changed file never shows. glTFast loads the model under the node.
- **Timelines** (`Scripts/ArtifactTimeline.cs`). This is a clock with `animate` tracks built once into arrays, so it doesn't allocate while it plays. The `timeline` action (`play`, `pause`, `restart`) drives it from a button or a zone.
- **Player builds** need glTFast's shader graph included, or models render magenta (same as CAD: see "Notes for Core" in `CAD/README.md`). That's a Project Settings change for Core.

The `launch` fixture needs host runtime 4.

## Biology: cell, heart, brain, plant cell, eye and DNA

Six templates for things to look at rather than drive. They need no room, no network and no permissions. Albie makes them with `artifact_create {template: "cell"}` (or `heart`, `brain`, `plant_cell`, `eye`, `dna`). Its tool description tells it to use these templates for biology rather than writing a scene, so the labels stay the checked ones.
- **`cell`.** Grab3D's CC0 [animal cell cutaway](https://grab3d.com/models/anatomy/human-cell/), with a wedge removed to show the inside. It's bundled as `assets/cell.glb.bytes` (2.1 MB, about 52k triangles, simplified with gltf-transform and textures 1024 max). There are 8 labels, each with a leader line, set in two side columns: mitochondrion, nucleolus, rough ER, cytoplasm, cell membrane, lysosome, nucleus and Golgi apparatus. **Follow a protein** plays a 10 s timeline that traces the secretory pathway (nucleus, rough ER, Golgi, membrane), with a caption for each step.
- **`heart`.** Grab3D's CC0 [heart cutaway](https://grab3d.com/models/anatomy/human-heart-cutaway/), cut open from the front so all four chambers and the valves show. It's bundled as `assets/heart.glb.bytes` (2.5 MB, about 65k triangles, simplified the same way) and turned so the patient's right is on the person's left. It beats at 72 a minute, squeezing about its own centre. There are 13 labels: the four chambers, the four valves, the septum, the aorta, the pulmonary artery, the superior vena cava and the pulmonary veins. **Trace the blood** sends an oxygen-poor drop through the right side and an oxygen-rich drop through the left, lighting each step's labels. **Pause the beat** stops the heart at rest.
- **`brain`.** Grab3D's CC0 [human brain](https://grab3d.com/models/anatomy/human-brain/) (1.9 MB, about 52k triangles), left side on with the front to the person's left. There are 7 labels: the frontal, parietal, occipital and temporal lobes, the gyri and sulci, the cerebellum and the brainstem. **Tour the lobes** moves a glowing marker over each part in turn for 12 s, with a caption saying what it does.
- **`plant_cell`.** Grab3D's CC0 [plant cell](https://grab3d.com/models/anatomy/plant-cell/) (2.2 MB, about 53k triangles), tipped 40 degrees so its open top faces the person. There are 8 labels: rough ER, chloroplast, central vacuole, cell wall, nucleus, nucleolus, cytoplasm and Golgi apparatus. **Follow the sunlight** walks through photosynthesis: water leaves the vacuole for a chloroplast, a sunbeam reaches it, the drop turns into sugar while oxygen leaves through the wall.
- **`eye`.** Grab3D's CC0 [human eye](https://grab3d.com/models/anatomy/human-eye/) at 8 times life size (2.0 MB, about 52k triangles), in a three-quarter view so the muscles and the optic nerve show. There are 6 labels: iris, pupil, cornea, sclera, eye muscles and optic nerve. **Follow the light** sends a ray in through the cornea and pupil, explains the lens and retina, then sends a signal out along the optic nerve.
- **`dna`.** Grab3D's CC0 [DNA double helix](https://grab3d.com/models/anatomy/dna/) (1.7 MB, about 53k triangles). There are 5 labels: the backbone, a base, a base pair, the hydrogen bonds and the second strand. The model's rung colours don't follow a fixed A/T/G/C scheme, so no label names a letter by colour. **Explain it** gives four captions; **Spin it** hides the labels and turns the helix once every 12 s; **Stop spinning** puts it back and shows them.
- All six set `view.radius`, so the volume is fitted to the whole scene before the model finishes loading rather than to whatever has loaded by then.
- **Panels.** All use only `note` and `button` widgets. Buttons now wrap onto a new row when the next one wouldn't fit across the panel.
- The scenes were generated by a script and are plain `albie.scene/1`. Label and leader positions were measured on the loaded models in Play mode, checking the texture colour at each hit point. Each label's colour has one `animate` track per tour, so a label lit in several steps keeps one track with several lit spans.

All six need host runtime 4.

## Physics: double pendulum and solar system

Two templates for things you change and watch play out, like the three-body fixture. They need no room, no network and no permissions. Albie makes them with `artifact_create {template: "double_pendulum"}` or `{template: "solar_system"}`, and its tool description sends "show me chaos" and "how do planets orbit?" to them.
- **`double_pendulum`.** Two double pendulums (0.5 m rods, 1 kg bobs) on one pivot in front of a dark dial, started 0.01° apart. For about 5 s they swing as one, then they split and never agree again. The panel shows the time, how often each one has gone over the top, and the energy drift as a live accuracy check. Sliders: starting angle (30 to 175°), the gap (0 to 1°: at 0 they stay identical for good), friction at the joints and playback speed.
- **`solar_system`.** The Sun and the four rocky planets at their real average distances and masses, under real gravity (G = 4π², so the clock reads in years). Earth comes round in 1.000 years. The sizes aren't to scale and the panel says so. Sliders: the Sun's mass (0.5 to 2 Suns: at half a Sun the planets get away and "Planets thrown out" counts it), the planets' starting speed (their orbits stretch into ellipses) and years per second.
- **The `pendulum` solver** (`Scripts/ArtifactPendulum.cs`). One or two links hanging from the `sim` node's origin, swinging in its x-y plane. Each body has `mass`, `length` (m), `angle` and `omega` (degrees, from straight down). `params`: `g`, `step`, `damping`, and `angle_offset` (degrees, added to every link's starting angle, which is how the two copies differ). Fourth-order Runge-Kutta in doubles, with no allocation per step. It publishes `time`, `energy_drift_pct` (relative to the swing's height above its lowest point), `flips`, `angle_1` and `angle_2`.
- **`line` field `through`** (`Scripts/ArtifactTrail.cs`). A list of node ids with the line's parent. The line joins them every frame after the sims have moved, for the pendulums' rods.
- Both need host runtime 6. The scenes were written by a script (planet starting speeds are 2π/√a AU a year) and are plain `albie.scene/1`.

## How the fixtures look (Nov 9)

The four sandbox fixtures got the same finish as the templates. Every node and rule they had is kept, in its old order, so the checks that point at them still mean the same thing.
- **`stance`** is a truck on four wheels whose axle stretches with the track-width slider. Orange lines run from the centre of gravity to the front wheels, and they follow both sliders: wide and low gives flat lines (hard to roll), narrow and high gives steep ones.
- **`three_body`** shows the perfect figure eight as a faint line (one period, integrated offline from the same starting values) and the centre of mass, so a nudge visibly pulls the bodies off the path.
- **`repo_city`** stands on a dark base with an outline, like a model.
- **`launch`** has a pad, a red service tower with an arm that pulls back, a two-part flame, a glow on the pad, eight puffs of exhaust that roll out and cool from flame-lit to grey, and Ignition / Liftoff / Engine cut-off captions.

Two host fixes came out of this (`Scripts/ArtifactTrail.cs`):
- A `line`'s `points` can name inputs (`"$cog_m"`) and follow them live, like any other bound value.
- A line's width is set when it's built. Before, a new line reported bounds 1 m wide until its first frame, and a volume without `view.radius` fitted to those bounds: the scene stood too high and too small. That was making `repo_city` look small.

## Files

| Path | What it is |
|---|---|
| `Scripts/ArtifactHost.cs` | Opens, validates, asks permission, places generated scenes in the room, relayouts, and scores runs. `ArtifactHost.Current` is the host the tools use. |
| `Scripts/ArtifactBootstrap.cs`, `Resources/ArtifactHost.prefab` | **In the app.** Any scene with a `Workspace` (Main) and no host gets one from `Resources/ArtifactHost` at load, so nobody has to edit Main.unity. That host follows the user: each open from Albie's tools brings it 0.8 m in front of the eyes at desk height. Room courses still stand where the room generator puts them. Sandboxes place their own host and are left alone. |
| `Scripts/ArtifactRun.cs`, `Scripts/ArtifactRuns.cs` | The run engine (`albie.run/1`) and the device records. |
| `Scripts/ArtifactThing.cs` | `IArtifactThing` probes, plus a kinematic stand-in until Person 2's Thing runtime exposes them. |
| `Scripts/Room/` | `IFloorPlan` (free floor and ceiling height), plus the MRUK and mock rooms, the `floor_slalom` and `room_hoops` generators, layout records, spatial anchors, and `ArtifactGenerators`. |
| `Scripts/ArtifactZone.cs` | The `zone` node (with `Contains` for runs) and the `array` node over a slot. |
| `Scripts/ArtifactLibrary.cs`, `Scripts/ArtifactTools.cs` | The templates and the device library. Tools: `artifact_list`, `artifact_create`, `artifact_open`, `artifact_close`, `artifact_relayout`, `artifact_run`, `artifact_compare`. |
| `SlalomSandbox.unity` | Hub off, no headset. Keys: 2 make a slalom, 7 make a hoop course, 3 arm a run, 4 scripted lap, 5 roll over, 6 forget the saved anchor, WASD drive, M / N next room, R relayout, [ ] spacing, G gates, O view. The brain truck: B, then L lap by hand, K scripted lap, T train, P Autopilot. Rebuild it with **Forge > Artifacts > Build Slalom Sandbox**. |
| `Editor/SlalomBrainCheck.cs` | The brain beat in Play mode, in every mock room: **Forge > Artifacts > Check Brain Loop (Play)**, about a minute. |
| `Editor/SlalomCheck.cs` | 13 headless checks: **Forge > Artifacts > Check Slalom**. |
| `Editor/HoopCheck.cs` | 14 headless hoop checks (mock rooms, template, scored laps): **Forge > Artifacts > Check Hoops**. |
| `Scripts/ArtifactData.cs` | Data tables: parsing and checking snapshots, the Provider → snapshot → bundled sample order, `data_sources`. |
| `ArtifactsSandbox.unity` | Hub off. Keys: 1 stance, 2 slalom, 3 broken, 4 missing, 5 three-body, 6 repo city (its own snapshot), 7 make repo activity, 8 launch, C make the cell, H the heart, B the brain, P the plant cell, V the eye, N DNA, K the double pendulum, O the solar system, 0 close, 9 forget approvals. |
| `Scripts/ArtifactGltf.cs`, `Scripts/ArtifactTimeline.cs` | The `gltf` node and asset checks; the `timeline` node and `animate` tracks. |
| `Editor/GltfTimelineCheck.cs` | 5 headless checks: **Forge > Artifacts > Check glTF and Timelines**. The model load runs in an open Editor: **Forge > Artifacts > Check glTF Load**. |
| `Editor/RepoActivityCheck.cs` | 10 headless data and city checks: **Forge > Artifacts > Check Repo Activity**. |
| `Editor/ArtifactRunCheck.cs` | 13 headless run checks: **Forge > Artifacts > Check Runs**. `Forge > Check Artifacts` runs the rest. |
| `Editor/BioTemplatesCheck.cs` | 7 headless checks on the six biology templates (valid, made offline, model hash and licence, labels, hidden captions, beat centre, DNA spin): **Forge > Artifacts > Check Biology Templates**. |
| `Scripts/ArtifactPendulum.cs` | The `pendulum` solver (one or two links, RK4 in doubles). |
| `Editor/SimTemplatesCheck.cs` | 9 headless checks on the physics templates (valid, made offline, the textbook period, energy, chaos, friction, Earth's year, half a Sun, the new validator refusals): **Forge > Artifacts > Check Physics Templates**. |
| `Scripts/LocalRepoConnector.cs`, `Editor/RepoSnapshotBuild.cs` | The local fixture connector (this repo's commits by git, or a copy saved at build time) and its build step. |
| `Editor/RepoConnectorCheck.cs` | 7 headless checks on the fixture connector: **Forge > Artifacts > Check Repo Connector**. |
| `Editor/ArtifactShot.cs` | Test helper: `ArtifactShot.Take(path)` renders the open artifact and its panels to a PNG in Play mode, since the Game view is black in XR. |
| `Editor/VoiceRulesCheck.cs` | Every tool named in Albie's voice rules exists: **Forge > Artifacts > Check Voice Rules**. |

## For other modules

```csharp
var host = ArtifactHost.Current;      // the scene's host; in Main, the one ArtifactBootstrap added
host.Open("slalom");                  // or the tool: AlbieTools.Call("artifact_create", {template: "slalom"})
host.Thing = aThing;                  // any IArtifactThing (Person 2's runtime, or ArtifactThing)
host.ArmRun();                        // or the tool: artifact_run
host.LastLayout;                      // the albie.layout/1 record, or null
host.LayoutMessage;                   // what Albie should say about it
host.Run;                             // the engine: State, Outcome, PendingRecord
host.Build.Arrays["gates"];           // the gate zones, in order

// When a run ends (C-12): "completed" suggests worked, "failed" suggests failed, "aborted" changes nothing.
NotebookStore.AttachRun(runId, outcome);
```

**Events:** `artifact.layout` fires when a course is laid out. `artifact.run` fires when a run changes state (`armed`, `running`, `finished`) with `{artifact_id, run_id, state, outcome, reason, metrics}`. `artifact.updated` fires when the library changes.
