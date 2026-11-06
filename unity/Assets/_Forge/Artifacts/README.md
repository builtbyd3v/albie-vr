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

## "What shipped this week?" (repo activity)

Albie calls `artifact_create {template: "repo_activity"}`. This shows commits as a small city on a base: one block per commit, height for lines changed, and colour for the author. The data is read through the `array` node's `source: {table, grid}` (C-12 section 7). The rows are never typed into the scene.
- **Where the rows come from** (`Scripts/ArtifactData.cs`). The host tries `ArtifactData.Provider` first. That's the hook for Person 3's connector host, and it's null until that's on main. Next it tries the artifact's own `data/<name>.json`, then the bundled sample in `Resources/ArtifactData/`. Today that means the bundled sample: 18 made-up commits.
- **Sample data** always gets a yellow "Sample data" label, and Albie says the commits are made up. A commit with no line count is a grey stub, not a zero.
- **Albie's own artifacts.** `artifact_create {manifest, scene}` validates the artifact and saves it versioned. If it's invalid, the result is `status: "invalid"` with every error, and nothing is saved. A source this headset has no copy of opens as `no_data`.

The template needs host runtime 3.

## Models and timelines (`gltf`, `timeline`, `animate`)

These are the last Phase 1 node kinds. The `launch` fixture uses all of them: the CAD mocks' v1 rocket on a pad, and a Launch button that restarts a 3.5 s timeline. On that timeline the rocket climbs and leans, and its flame lights, grows, turns orange and burns out.
- **Models** (`Scripts/ArtifactGltf.cs`). The host only loads a `.glb` that the manifest lists, from the artifact's own `assets/`, with a matching sha256. It checks the hash before building anything, so a changed file never shows. glTFast loads the model under the node.
- **Timelines** (`Scripts/ArtifactTimeline.cs`). This is a clock with `animate` tracks built once into arrays, so it doesn't allocate while it plays. The `timeline` action (`play`, `pause`, `restart`) drives it from a button or a zone.
- **Player builds** need glTFast's shader graph included, or models render magenta (same as CAD: see "Notes for Core" in `CAD/README.md`). That's a Project Settings change for Core.

The `launch` fixture needs host runtime 4.

## Files

| Path | What it is |
|---|---|
| `Scripts/ArtifactHost.cs` | Opens, validates, asks permission, places generated scenes in the room, relayouts, and scores runs. `ArtifactHost.Current` is the host the tools use. |
| `Scripts/ArtifactRun.cs`, `Scripts/ArtifactRuns.cs` | The run engine (`albie.run/1`) and the device records. |
| `Scripts/ArtifactThing.cs` | `IArtifactThing` probes, plus a kinematic stand-in until Person 2's Thing runtime exposes them. |
| `Scripts/Room/` | `IFloorPlan` (free floor and ceiling height), plus the MRUK and mock rooms, the `floor_slalom` and `room_hoops` generators, layout records, spatial anchors, and `ArtifactGenerators`. |
| `Scripts/ArtifactZone.cs` | The `zone` node (with `Contains` for runs) and the `array` node over a slot. |
| `Scripts/ArtifactLibrary.cs`, `Scripts/ArtifactTools.cs` | The templates and the device library. Tools: `artifact_list`, `artifact_create`, `artifact_open`, `artifact_close`, `artifact_relayout`, `artifact_run`, `artifact_compare`. |
| `SlalomSandbox.unity` | Hub off, no headset. Keys: 2 make a slalom, 7 make a hoop course, 3 arm a run, 4 scripted lap, 5 roll over, 6 forget the saved anchor, WASD drive, M / N next room, R relayout, [ ] spacing, G gates, O view. Rebuild it with **Forge > Artifacts > Build Slalom Sandbox**. |
| `Editor/SlalomCheck.cs` | 13 headless checks: **Forge > Artifacts > Check Slalom**. |
| `Editor/HoopCheck.cs` | 14 headless hoop checks (mock rooms, template, scored laps): **Forge > Artifacts > Check Hoops**. |
| `Scripts/ArtifactData.cs` | Data tables: parsing and checking snapshots, the Provider → snapshot → bundled sample order, `data_sources`. |
| `ArtifactsSandbox.unity` | Hub off. Keys: 1 stance, 2 slalom, 3 broken, 4 missing, 5 three-body, 6 repo city (its own snapshot), 7 make repo activity, 8 launch, 0 close, 9 forget approvals. |
| `Scripts/ArtifactGltf.cs`, `Scripts/ArtifactTimeline.cs` | The `gltf` node and asset checks; the `timeline` node and `animate` tracks. |
| `Editor/GltfTimelineCheck.cs` | 5 headless checks: **Forge > Artifacts > Check glTF and Timelines**. The model load runs in an open Editor: **Forge > Artifacts > Check glTF Load**. |
| `Editor/RepoActivityCheck.cs` | 10 headless data and city checks: **Forge > Artifacts > Check Repo Activity**. |
| `Editor/ArtifactRunCheck.cs` | 13 headless run checks: **Forge > Artifacts > Check Runs**. `Forge > Check Artifacts` runs the rest. |

## For other modules

```csharp
var host = ArtifactHost.Current;      // the ArtifactHost prefab in the scene
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
