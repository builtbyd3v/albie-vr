# C-12 Artifacts: contract proposal

> **Status: PROPOSAL** (2026-11-03). Drafted by Person 5 (Satyabrata) for the Nov 4 goal in `PROJECT_CONTEXT.md` 16.3. It needs Person 2's answers on the Thing and room questions (section 12) and the Core owner's acceptance before anyone builds on it as law. Until then, build against it as written (16.5).
>
> Index entry: `PROJECT_CONTEXT.md` section 13, C-12. Background: sections 9 (artifacts), 15.3 (room arenas), 15.6 (demo spine), D-18, D-28, D-36. Sister contract: `docs/NOTEBOOK_CONTRACT.md` (C-17).

An **artifact** is a generated tool, visual or simulation. It's a folder of plain data that a fixed Unity host reads. Phase 1 runs no code (D-18).

This contract freezes five files and the rules that join them:

| File | Schema | Written by | Portable? |
|---|---|---|---|
| `manifest.json` | `albie.artifact/1` | The agent or a template | Yes |
| `scene.json` | `albie.scene/1` | The agent or a template | Yes |
| `data/<name>.json` | Snapshot table (section 7) | The connector host | Yes |
| `layouts/<layout_id>.json` | `albie.layout/1` | The host, on the device | **No.** It belongs to one room. |
| `runs/<run_id>.json` | `albie.run/1` | The host, on the device | Yes |

Fixtures for all of them are in `unity/Assets/_Forge/Mocks/Resources/Mocks/artifacts/` (section 11).

---

## 1. What this changes from section 9

Section 9 was written before the room-aware arena (15.3). As written, it can't express the slalom, which is the artifact the demo needs. These are the proposed changes. Everything else in 9.2 and 9.3 stands.

| # | Section 9 says | This contract says | Why |
|---|---|---|---|
| 1 | 7 node kinds, 3 rule types (9.3) | Adds node kinds `array` and `zone`, rule `on_zone`, and top-level `generators` and `run` blocks | Nothing in 9.3 can place cones in free floor space, notice a truck passing a gate, or start and end a run. |
| 2 | `camera_rig` is a node kind (9.3) | Reserved. The validator refuses it in Phase 1. | On a headset the user's head is the camera, and no competition artifact needs one. It's one less thing to build. |
| 3 | The course layout is part of the artifact version (9.7), but the arena is generated from your room (15.3) | The **recipe** is in the artifact version. The **resolved positions** are a separate per-room layout file. | Both can't be true of one file. Two rooms need two layouts of the same artifact version. |
| 4 | The cloud validator checks every artifact (9.4) | The **device** validator is the one that must pass. A cloud check is optional. | "Make me a slalom" has to work with no network (15.6 fallbacks, D-33). |
| 5 | `format` key, version `"1.2.0"` (9.2) | `schema` key, integer `version` | Matches `albie.controller/1` (C-19). Nothing has been built on the old form. |
| 6 | `category` is in C-12 but not in the 9.2 example | Required: `viz`, `tool` or `sim` | Fills the gap. |
| 7 | `source: "project:proj_123/runs"` (9.2) | `source: "builtin:runs"` | Keeps artifacts reusable across projects, and matches the built-in backend (9.6, C-18). |
| 8 | Writes to a project need a confirm each time (9.10) | Unchanged for the artifact. **Run records aren't artifact writes**: the host saves them. | Otherwise every lap would raise a confirm. |
| 9 | Permissions are `network`, `connectors`, `storage`, `write_project` | Adds `room` and `things` | The room scan is sensitive data, so the first-open sheet should say an artifact reads it. |

## 2. Manifest: `albie.artifact/1`

Each artifact is a folder with `manifest.json` at its root. Zip bundles wait until sharing exists (9.9).

```json
{
  "schema": "albie.artifact/1",
  "id": "slalom",
  "title": "Slalom",
  "version": 1,
  "kind": "project",
  "category": "sim",
  "template": "slalom",
  "links": { "project": "proj_truck", "project_version": 2, "repo": null, "thing": "truck" },
  "runtime": { "type": "scene", "min_runtime": 1 },
  "scene": "scene.json",
  "script": null,
  "inputs": [
    { "name": "gates", "type": "integer", "min": 3, "max": 12, "default": 5, "label": "Gates" },
    { "name": "spacing_m", "type": "number", "min": 1.0, "max": 2.0, "default": 1.2, "label": "Spacing" }
  ],
  "scenarios": [ { "id": "default", "seed": 42, "params": {} } ],
  "data": [],
  "assets": [],
  "permissions": {
    "network": [], "connectors": [], "storage": "artifact-local",
    "write_project": "none", "room": "read", "things": "read"
  },
  "outputs": [ { "name": "completion_time_s", "type": "number", "unit": "s", "label": "Time" } ]
}
```

| Field | Rule |
|---|---|
| `schema` | Must be `albie.artifact/1`. The host refuses a higher major version than it knows. |
| `id` | Lowercase letters, digits, `_` and `-`. Unique in the project or the user's library. Never changes, including when the artifact is brought into a project. |
| `version` | Integer from 1. Bumped on any change to the manifest, the scene or an asset. |
| `kind` | `project`, `code` or `standalone`. A `standalone` artifact has all `links` null. |
| `category` | `viz`, `tool` or `sim`. |
| `template` | The starter it came from, or null. |
| `links.project_version` | The project version it was built from (9.6). Integer, shown as `v2`. |
| `links.thing` | The Thing a run binds to. Null means no Thing, or the one in focus if `run` is declared. |
| `runtime.type` | `scene` in Phase 1. `min_runtime` is the host version it needs. |
| `script` | Must be null in Phase 1. Reserved for Phase 2. |
| `inputs` | Types are `number`, `integer`, `boolean` and `choice` (with `options`). They're the only values that change without a rebuild. `$name` uses one anywhere the scene allows a number. Out-of-range values are rejected, not clamped (same as C-19). |
| `scenarios` | At least one. `seed` feeds the generators, and `params` overrides `inputs` for that scenario. |
| `data` | `{name, source, snapshot}`. `source` is `builtin:<name>` or `connector:<connector>/<tool>`, never a vendor API. With a `snapshot`, the artifact opens with no network. |
| `assets` | `{path, sha256, license}`. The host loads only the hashes listed. |
| `permissions` | See section 8. |
| `outputs` | One per `metric` rule the run record keeps. Names must match (section 5). |

## 3. Scene: `albie.scene/1`

```json
{
  "schema": "albie.scene/1",
  "space": "room",
  "generators": [],
  "nodes": [],
  "rules": [],
  "run": null
}
```

`space` decides where the host puts the scene:

| `space` | Host | Scale | Needs |
|---|---|---|---|
| `room` | Directly in the room, on the real floor | True scale. Physics is real. | MRUK room data, the `room` permission |
| `volume` | `ForgeVolume` | Scaled to fit | Nothing. Must open with no desk (C-04). |
| `panel` | `ForgePanel` | Flat | Nothing. Must open with no desk. |

Units are metres, degrees and seconds. Axes are Unity's: Y up, left-handed, with `rot` as Euler angles. Colours are `#RRGGBB`.

### 3.1 Node fields

`nodes` is a flat list. Children point at a parent, so a validator never has to walk a tree.

| Field | Rule |
|---|---|
| `id` | Required and unique in the scene. Lowercase letters, digits and `_`. |
| `kind` | One of the kinds in 3.2. An unknown kind fails validation. |
| `parent` | Another node's `id`, or absent. No cycles. |
| `pos`, `rot`, `scale` | `[x, y, z]`. Default to zero, zero and one. Local to the parent. `pos` is the centre of a shape. `size` doesn't reach a node's children; `scale` does. |
| `at` | A generator slot (3.3) with exactly one entry. The node stands on the slot's pose, and its own `pos` and `rot` are offsets from it (a zone lifts itself by half its height). |
| `visible` | Default `true`. |

### 3.2 Node kinds (frozen for Phase 1)

| Kind | Fields | Needed by |
|---|---|---|
| `primitive` | `shape` (`box`, `sphere`, `cylinder`, `capsule`, `cone`, `plane`), `size`, `color`, `collider` (default false) | Nov 6 |
| `text` | `text`, `height`, `color`, `billboard`. `{metric}` in the text shows a live value. Rebuilt only when the value changes. | Nov 6 |
| `panel` | `title`, `widgets` (below). Always inside a `ForgePanel`, working with mouse, ray and poke. With no `pos` or `at`, the host places it facing the user. | Nov 6 |
| `array` | `source`, `template`, optional `map`. **New.** One draw call for many copies of a primitive, or many zones. | Nov 10 |
| `zone` | `size`. **New.** A box that notices when the bound Thing enters. It shows as an outline when `visible`. | Nov 10 |
| `gltf` | `asset`, which must be listed in the manifest's `assets`. Loaded with glTFast. | Nov 12 |
| `line` | `points`, or `source` (a slot list), plus `width` and `color`. | Nov 12 |
| `timeline` | `duration_s`, `loop`, `autoplay`. A clock that `animate` rules follow. | Nov 14 |
| `camera_rig` | Reserved. Refused with `unsupported_kind`. | Not built |

**`gltf` and `timeline` (as built, 2026-11-05, host runtime 4).**
- **`gltf`.** Phase 1 reads binary glTF only. Each manifest asset is `{path: "assets/<name>.glb", sha256, license}`, and each of the three fields is required.
  - Before anything is built, the host reads each file a `gltf` node names, in this order: the artifact's device folder, the bundled fixture (`<name>.glb.bytes`), then the template it came from.
  - It refuses the artifact with `asset_missing`, with `too_big` if a file is over 8 MB, with `hash_mismatch`, or with `bad_value` if the file isn't a glb.
  - glTFast then loads it under the node, spread over frames in play mode. The model is in metres, at its own scale. Use the node's `scale` to change its size.
  - A volume with no `view` fits again once the model arrives. Textures over 2048 px are logged, not refused.
- **`timeline`.** `duration_s` (above 0) is required. `loop` and `autoplay` default to false. At the end it stops on the last frame, and `play` after the end starts over.
- **`animate` rules.** `property` is one of:
  - `pos`, `rot` or `scale`, each `[x, y, z]`. `scale` can also be a single number.
  - `visible`, `true` or `false`. It steps at each key rather than blending.
  - `color`, `#RRGGBB`. It tints the target's meshes, or a `text` node.

  Keys are `[t, value]`, times going up, within the timeline, up to 64 per rule. Values are linear between keys and hold before the first and after the last. Rotation blends each Euler angle separately.

  The validator refuses:
  - a target that's a panel or a timeline;
  - `pos` or `rot` on a node that stands on a slot (`at`);
  - the same target and property animated twice.
- **The `timeline` action.** `{timeline, action}` takes `play`, `pause` or `restart`. `restart` jumps to 0 and plays.

**`panel` widgets:**

| Widget | Fields | Shows |
|---|---|---|
| `stat` | `metric`, `label`, `unit` | One live value |
| `runs` | `columns` (metric names), `limit` | Run history, the current project version against the last ones (section 6.3) |
| `versions` | none | As built, 2026-11-06 (host runtime 5). The open project's previous version against its current one, from the notebook (`notebook_compare`), for example "v1 rolled over (free drive) · v2 8.6 s". A version that only flipped on a free drive is never shown as a run on this course. It refreshes when a notebook entry changes or a version is cut, so a finished run updates it. With no project, or only one version, it says so. |
| `button` | `id`, `label` | Fires `on_input` with that `id` |
| `slider` | `input` | Changes a manifest input |
| `note` | `text` | Fixed text, such as "Sample data" |

**`array` sources.** Use `"source": "course.cones"` for a generator slot, or a table:

```json
{ "id": "city", "kind": "array",
  "source": { "table": "repo_activity", "grid": { "columns": 6, "gap_m": 0.04 } },
  "map": { "height": { "column": "lines_changed", "range": [0.02, 0.4] },
           "color":  { "column": "author" } },
  "template": { "kind": "primitive", "shape": "box", "size": [0.05, 1.0, 0.05], "color": "#4C8DFF" } }
```

- Over a slot, the template's own `pos` and `rot` are offsets from each pose, the same as `at` (a cone template lifts itself by half its height).
- `map.height` and `map.width` scale a column linearly into `range`.
- `map.color` gives each distinct value of a text column its own colour.
- A null cell shows as the grey "no data" colour at the bottom of the range. It's never drawn as zero (9.8).

### 3.3 Generators

A generator is fixed C# in the host that turns a room into positions. **The agent never computes positions.** It can't see the room, and this keeps a bad model output from putting a cone inside the couch.

```json
{ "id": "course", "type": "floor_slalom",
  "params": { "gates": "$gates", "spacing_m": "$spacing_m", "min_spacing_m": 1.0, "tight_spacing_m": 1.0, "min_gates": 3,
              "lane_width_m": 0.6, "clearance_m": 0.15, "around": "COUCH" } }
```

| `type` | Params | Slots it fills |
|---|---|---|
| `floor_slalom` | `gates`, `spacing_m`, `min_spacing_m`, `min_gates`, `lane_width_m`, `clearance_m`, optional `around` (an MRUK label), optional `narrow_lane_width_m` (default 0.4) and `tight_spacing_m` (default 0.5) for the narrow course | `<id>.cones`, `<id>.gates`, `<id>.start`, `<id>.finish` |
| `room_hoops` | `hoops`, `spacing_m`, `min_spacing_m`, `min_hoops`, `diameter_m`, `clearance_m`, optional `height_m` (the hoop centre, default 1.0), optional `tight_spacing_m` (default 0.5) for the straight line | `<id>.hoops`, `<id>.start`, `<id>.finish` |

A slot is a named list of poses (`pos`, `rot`), relative to the layout's origin. **Every slot is on the floor (`y` = 0)**: a node's own `pos` lifts it. Adding a generator type is a host change, so it bumps `min_runtime`.

**`floor_slalom` geometry (as built, 2026-11-04).** The course runs along its local +z from the start (z = 0): a cone every `spacing_m`, a gate beside each cone on alternating sides at ±`lane_width_m`/2 (the side the Thing passes), and the finish one spacing past the last cone. Its footprint is ±`lane_width_m` either side of the cones, from 0.3 m before the start to 0.3 m after the finish, all on free floor. The search tries courses along or across the room (or the `around` object) first, nearest that object (or the middle of the free floor), and turns a course only when no square one fits at that size.

**`room_hoops` geometry (as built, 2026-11-05, Nov 14).** This generator needs host runtime 2, so the `hoop_course` template sets `min_runtime` 2.
- **Layout.** A hoop every `spacing_m` along +z, weaving ±0.3 × `diameter_m` sideways on alternating sides. The finish is one spacing past the last hoop. The slots stay on the floor; the scene lifts each frame so the hoop's centre is at `height_m`.
- **Footprint.** It uses the same search as the slalom. Its half-width is the weave, plus half a hoop, plus the 4 cm post.
- **Ceiling.** The frame top plus `clearance_m` must be under the ceiling (`IFloorPlan.CeilingHeight`). If it isn't, the layout fails with **`no_headroom`** (new) and Albie says both heights. A scan with no ceiling anchor still gets a course, and the `say` line notes that the ceiling couldn't be checked. A missing label is never a reason to fail.
- **Template defaults.** 4 hoops at 1.0 m, minimum 3 hoops at 0.6 m, 0.7 m hoops at 1.0 m, `clearance_m` 0.15.
- **Scoring.** `collisions` uses `hit_count` over a list of the four frame arrays.
- **Fail reasons.** A missed hoop fails as `missed_hoop`, and upside down for 1 s fails as `crashed`.
- **Mock-room results.** The 7 × 5 m living room gets the full course. The 4.5 × 3.8 m bedroom shrinks to 3 hoops at 0.6 m. An unlabelled 5 × 4 m room with no ceiling gets 4 hoops at 0.8 m. A 1.4 m attic gives `no_headroom`. The closet gives `no_free_floor`.
- **MRUK prefab rooms.** Measured on 2026-11-05: 7 of 8 rooms get a course (see "Narrow courses" below). It hasn't been run on a Quest yet.

**Default size.** 5 gates at 0.8 m makes a 4.8 m course, which is meant to fit a living room. These are starting guesses until someone drives a truck through one on a Quest.

**Measured on MRUK's 8 prefab rooms (Person 5, 2026-11-04).** With `lane_width_m` 0.7 and `clearance_m` 0.25 (this contract's first guess), 3 of 8 rooms got a course. With 0.6 and 0.15, 4 of 8 did, and the courses were longer: LivingRoom02 5 gates at 0.8 m, Bedroom00 5 at 0.7, LivingRoom01 4 at 0.6, LivingRoom04 3 at 0.7. The other 4 are small or cluttered and fit no slalom at any setting tried, down to a 0.5 m lane; they get `no_free_floor` with the size needed. The template now uses 0.6 and 0.15. A 0.6 m gate is still almost 3 times the width of the 0.22 m mock truck.

**Free floor** means a point on the floor that passes `MRUKRoom.IsPositionInRoom` and fails `IsPositionInSceneVolume` with `clearance_m` as the buffer. Both exist in MRUK 207. `around` is a preference. If that label isn't in the room, the generator ignores it and says so.

**Fit rules (the Nov 8 default in 2.2: shrink and say so, never fail silently):**

1. Try the requested `gates` at `spacing_m`.
2. If that doesn't fit, reduce the spacing, down to `min_spacing_m`.
3. Then reduce the gates, down to `min_gates`.
4. Then (as built, 2026-11-05) make a **narrow course** at `min_gates`: less free floor either side, with the spacing from `min_spacing_m` down to `tight_spacing_m`. See the note below.
5. If that still doesn't fit, the layout fails with `no_free_floor` and Albie says why, quoting the narrow course's size.
6. If the room has no floor data at all, it fails with `no_room_data`. Nothing is placed from a guess.

Whenever a step other than 1 ran, `fit.shrunk` is true and `fit.message` says what happened in plain words (section 6.1).

**Narrow courses (as built, 2026-11-05, Person 5; a proposal for the Core owner).** MRUK's LivingRoom00 (3.45 × 3.69 m, three couches, a table) had no clear strip 1.2 m wide longer than 2.1 m, so it got no slalom. Step 4 handles rooms like it:
- **`floor_slalom`.** The footprint narrows from ±`lane_width_m` to ±`narrow_lane_width_m` (default 0.4, so 0.8 m wide instead of 1.2 m), and the spacing can drop to `tight_spacing_m` (default 0.5). The gates stay at ±`lane_width_m`/2, so a narrow course scores the same way and the gate zones only reach past the narrow footprint by its clearance. Albie says: "This room is very tight, so I made a short, narrow course: 3 gates, 0.5 m apart, with less room either side. Drive carefully near the furniture."
- **`room_hoops`.** A hoop's width is fixed, so the hoops stop weaving and stand in a straight line instead. The half-width becomes half a hoop plus the post, 0.39 m for 0.7 m hoops. The spacing can drop to `tight_spacing_m` (default 0.5). Albie says: "This room is very tight, so I put the hoops in a straight line: ..."
- **Params.** Both new params are optional. Setting `narrow_lane_width_m` to `lane_width_m` and `tight_spacing_m` to `min_spacing_m` turns step 4 off. Since 2026-11-06 the slalom template sets `tight_spacing_m` to 1.0, so a narrow course only narrows, and the spacing stays at 1.0 m or more (next note).
- **Layout record.** `fit.placed.lane_m` records the half-width the course was placed with. A rerun checks that the saved course still fits at that width, so a narrow course is reused rather than remade every time. A layout without `lane_m` is checked at the full width.
- **MRUK's 8 prefab rooms, after this change (superseded by the next note).** Slaloms fit 6 of 8 rooms, up from 4. LivingRoom00 gets 3 gates at 0.5 m, narrow. LivingRoom03 gets 3 at 0.6 m, narrow. Bedroom01 and Office00 still get `no_free_floor`. Hoop courses fit 7 of 8: LivingRoom00 and LivingRoom03 get straight lines, Bedroom01 gets 3 straight hoops at 0.6 m, and only Office00 fails. The other rooms are unchanged, because steps 1 to 3 already fit them.

**Spacing the truck can weave (as built, 2026-11-06, Person 5 with ML A; a proposal for the Core owner).** The mock truck turned no tighter than 0.48 m, so a scripted driver passed 0 of 60 driver settings on cones 0.8 m apart, or closer, and passes started at 1.0 m. The truck is now smaller (0.17 m wheelbase, 35° steering, a 0.24 m turn; `MockTruckSim`), and the slalom template's spacing changed:
- `spacing_m`: default 1.2 m (was 0.8), minimum 1.0 m (was 0.6). `min_spacing_m` is 1.0, and `tight_spacing_m` is 1.0 (was 0.5), so no course has cones closer than 1.0 m. The generator's code defaults are the same.
- The new truck passes 40/60 driver settings at 1.0 m and 52/60 at 1.2 m. It still can't pass at 0.6 m.
- **The cost: slaloms fit 3 of MRUK's 8 prefab rooms, down from 6.** LivingRoom01 gets 3 gates at 1.0 m, narrow. LivingRoom02 gets 3 at 1.2 m, and Bedroom00 3 at 1.1 m. LivingRoom00, LivingRoom03 and LivingRoom04 now get `no_free_floor`, and so do Bedroom01 and Office00, as before. The old 6 were only on paper: no truck we've tested could weave them.
  - A 0.9 m minimum fits the same 3 rooms.
  - Allowing 2-gate courses (`min_gates` 2) would add LivingRoom04.
  - LivingRoom00 and LivingRoom03 need a different course shape.
- In the 7 × 5 m mock living room, 5 gates at 1.2 m need 7.8 m, so the course shrinks to 5 gates at 1.0 m. Albie says "This room is tight, so I made a shorter course: 5 gates, 1.0 m apart." The 4.5 m mock bedroom now gets `no_free_floor`.
- Hoop courses are unchanged.

## 4. Rules

| Rule | Fields | Does |
|---|---|---|
| `metric` | `name`, `op`, plus the fields below | Computes a value during a run |
| `on_input` | `input`, `do` | Runs actions when a `button` fires |
| `on_zone` | `zone`, `event` (`enter`, `exit`), `do` | **New.** Runs actions when the bound Thing enters or leaves a zone |
| `animate` | `target`, `property`, `keys`, `timeline` | Moves a node along keyframes (`[[t, value], ...]`) |

**Metric `op`s:**

| `op` | Fields | Value |
|---|---|---|
| `elapsed` | | Seconds since the run started |
| `zone_count` | `zones` (an array or zone id), `ordered` | Zones entered. With `ordered`, only the next one in order counts. |
| `hit_count` | `nodes` (an array or node id) | Times the Thing touched those nodes. As built, a list of ids is summed |
| `max`, `min`, `mean` | `probe` | Over the run |
| `last` | `probe` | The final value |

**Actions in `do`:**

| Action | Example |
|---|---|
| `set` | `{ "set": "lap_note.visible", "value": true }` |
| `run` | `{ "run": "reset" }` (`arm` or `reset`) |
| `timeline` | `{ "timeline": "replay", "action": "play" }` (`play`, `pause`, `restart`) |

That's the whole language. There are no expressions and no scripts, for the same reason as C-19 (D-10, D-18). If a scene can't be expressed this way, it becomes a precomputed data series that the scene plays back (9.3 fallback).

**Probes** are the Thing's true state, read from its Rigidbody. They aren't C-19 channels, so a truck with no speed sensor still gets metrics.

| Probe | Unit | Meaning |
|---|---|---|
| `speed_ms` | m/s | Forward speed |
| `upright` | -1 to 1 | Dot of the body's up and world up, same as C-19's `<id>.upright` |
| `height_m` | m | Height above the floor |

## 5. Runs

A scene with a `run` block can be scored.

```json
"run": {
  "start":  { "zone": "start" },
  "finish": { "zone": "finish",
              "needs": [ { "metric": "gates_passed", "min": "@course.gates" } ],
              "else_reason": "missed_gate" },
  "fail": [
    { "probe": "upright", "below": 0.3, "for_s": 1.0, "reason": "rolled_over" },
    { "timeout_s": 90, "reason": "timeout" }
  ]
}
```

- `@slot` is the number of entries in a slot. Written this way, a course that shrank to 4 gates needs 4, not the 5 that were asked for.
- **States:** `idle` → `armed` (the Thing is bound and the metrics are zeroed) → `running` (it entered `start`) → `finished`. `reset` goes back to `armed`.
- **Outcome:** `completed`, `failed` (with a `reason`) or `aborted` (the user stopped it, or the artifact closed).
- Zones are tested against the Thing's body position each `FixedUpdate`. That needs no trigger colliders and no allocations.

Every metric named in `outputs` must have a `metric` rule, and every `metric` kept in the run record must be in `outputs`.

A `run` block doesn't stop a scene opening: the course shows, and the run engine scores it (Nov 10, second step).

**As built, 2026-11-05.** `ArtifactRun` is the engine: it ticks from `FixedUpdate` with no allocations, tests zones with `ArtifactZone.Contains` (no trigger colliders), writes `albie.run/1` off that step, and calls `NotebookStore.AttachRun`. The probes come from `IArtifactThing` (C-12 question 1): Person 2's Thing runtime can implement it; until then the slalom sandbox drives `ArtifactThing`. `artifact_run` arms a lap; `artifact_compare` flags anything that isn't like for like as `"different course"`. Closing the artifact, or tapping Run again, aborts a running attempt.

**The next target, for the Thing's `course.bearing` (proposed, Person 5 with ML A, 2026-11-06; C-19 section 2.1).** While a run is armed or running, `ArtifactRun` publishes where the Thing should head next: the centre of the next zone in an ordered set (for the slalom, the next gate that `gates_passed` would count), then the centre of the `finish` zone. It publishes after every tick, when a run arms, and when it finishes. It clears the target when no run is armed or running. The target goes to `CourseBeacon` (Controllers), so the Thing runtime reads it without depending on Artifacts. A learned driver needs it on a room slalom: without it, two laps trained a net that drove 5 of 48 room attempts, and with it 48 of 48 (PROJECT_CONTEXT 16.8).

## 6. Device records

### 6.1 Layout: `albie.layout/1`

```json
{
  "schema": "albie.layout/1",
  "layout_id": "lay_0001",
  "artifact_id": "slalom", "artifact_version": 1, "scenario": "default", "seed": 42,
  "room": { "key": "<MRUKRoom.Anchor.Uuid>", "anchor": "<OVRSpatialAnchor uuid>" },
  "fit": { "requested": { "gates": 5, "spacing_m": 0.8 }, "placed": { "gates": 5, "spacing_m": 0.8 },
           "shrunk": false, "around_found": true, "message": null },
  "slots": { "course.cones": [ { "pos": [0.0, 0.0, 0.8], "rot": [0, 0, 0] } ] },
  "created_at": "2026-11-10T14:02:11Z"
}
```

- Made once, the first time the artifact opens in a room. It's saved at `artifacts/<artifact_id>/layouts/<layout_id>.json` on the device, and reruns reuse it: the newest layout with the same `artifact_version`, `scenario`, `room.key` and `fit.requested`.
- **Furniture moved:** a saved layout whose footprint is no longer free floor isn't reused. A new one is made, and `fit.message` starts "Something in the room has moved since I made the course".
- `slots` are relative to `room.anchor`, a spatial anchor the host places on the floor and saves (`OVRSpatialAnchor.SaveAnchorAsync`). This is the persistent anchor beat (15.3).
- **Anchor can't be found:** the host says so and makes a new layout with a new `layout_id`. Old runs stay, but they no longer compare like for like.
- **Before Nov 12** (saved anchors), the host may set `room.anchor` to null and use the floor anchor. Reruns in one session still match. As built, `room.origin {pos, yaw}` then holds the course start in the room's floor frame (the floor anchor's position, turned to the room's main axis), and `slots` are local to it.
- **As built, 2026-11-05 (Nov 12).** `ArtifactAnchors.Save` writes a uuid into `room.anchor` and a copy on the device. On a Quest it also calls `OVRSpatialAnchor.SaveAnchorAsync`. If the uuid is gone, the host makes a new layout and the `say` line is "I can't find the saved course in this room, so I made a new one." Furniture that has moved still uses the existing message. `room.origin` is kept so Editor and mock rooms can place the course without waiting on headset localisation.
- In the Editor, MRUK gives a prefab room a new UUID every time it loads, so layouts aren't reused across loads there. On a headset the scanned room keeps its UUID (question 3).

### 6.2 Run record: `albie.run/1`

```json
{
  "schema": "albie.run/1",
  "run_id": "run_0002",
  "artifact_id": "slalom", "artifact_version": 1, "scenario": "default", "seed": 42, "layout_id": "lay_0001",
  "project_id": "proj_truck", "project_version": 2,
  "thing": { "name": "truck", "version": 2 },
  "controller": { "name": "truck_manual", "kind": "manual", "version": 1 },
  "inputs": { "gates": 5, "spacing_m": 0.8 },
  "outcome": "completed", "reason": null,
  "metrics": { "completion_time_s": 8.6, "gates_passed": 5, "collisions": 0, "top_speed_ms": 1.6 },
  "started_at": "2026-11-10T14:06:02Z", "ended_at": "2026-11-10T14:06:11Z"
}
```

- Runs are never overwritten (9.7). The host writes one when a run ends, off the physics step.
- **`run_id` is unique across every artifact on the device, not just its own folder** (as built, 2026-11-05). The notebook stores bare run ids (C-17 `observed.runs`). With per-artifact numbering, a hoop course's `run_0001` matched the slalom's `run_0001`, so the notebook dropped it as a duplicate. The file still lives in `artifacts/<id>/runs/`.
- `controller` is recorded here because training doesn't cut a project version (C-17, section 4). The project version alone can't say which brain drove.
- A `failed` run keeps the metrics it reached. Metrics that never got a value are null, never zero.

### 6.3 Comparing runs

Two runs compare **like for like** when `artifact_id`, `artifact_version`, `scenario` and `layout_id` all match. The `runs` widget and `artifact_compare` show anything else with a "different course" label, never as a clean comparison.

## 7. Data tables (placeholder until C-18)

The snapshot format is Person 3's and Person 5's decision for Nov 10. This is the 2.2 default, so fixtures and the `array` source have something to read:

```json
{ "fetched_at": "2026-11-12T09:00:00Z", "source": "connector:source_control/list_commits", "sample": false,
  "columns": [ { "name": "author", "type": "string" }, { "name": "lines_changed", "type": "number" } ],
  "rows": [ ["Sample Author A", 120], ["Sample Author B", null] ] }
```

`sample: true` makes the host show a "Sample data" label (9.6). Sample rows must be fictional (competition rule 4, section 0).

**As built, 2026-11-05 (host runtime 3).**
- **Where a table comes from.** For each manifest `data` entry, the host tries these sources in order:
  1. `ArtifactData.Provider`, the seam for C-18's connector host. Until that lands it's null.
  2. The artifact's own `data/<name>.json` snapshot.
  3. A bundled sample in `Artifacts/Resources/ArtifactData/<connector>_<tool>.json`. This is always treated as `sample: true`, whatever the file says.

  If none of them has the table, the artifact doesn't open and the result is `no_data`.
- **Checking a table.** A table is refused if:
  - it has no columns or no rows;
  - a column type is anything other than `number` or `string`;
  - a row has the wrong number of cells;
  - a cell's type doesn't match its column.

  `null` is allowed in any cell.
- **Arrays from a table.** An `array` node with `source: {table, grid: {columns, gap_m}}` builds one copy per row, on a grid centred on the node, with every copy standing on y = 0.
- **As built, 2026-11-09 (Person 5).** `grid.base` (a colour) draws a plate under the grid, sized to its columns and rows. A snapshot may say `saved_copy: true` (a connector's copy saved earlier, such as the local fixture connector's copy saved into a Quest build); Albie then says which day it's from, as for an artifact's own snapshot. Until C-18 lands, `LocalRepoConnector` stands in as the provider for `connector:source_control/list_commits` only.
  - `map.height` and `map.width` scale a number column linearly into `[min, max]`.
  - `map.color` gives each distinct value its own palette colour.
  - A `null` cell is grey (`#5A5F66`) at the bottom of the range. It is never drawn as zero.
  - Past 256 rows, the copies are cut, and `say` gives the row count.
  - A mapped column the table doesn't have, or a height or width mapped to a string column, is `unknown_ref` before anything is built.
- **Sample data.** When any table is sample data, the host places a "Sample data" label above the scene. `artifact_open` then returns `data: [{name, source, rows, fetched_at, sample, from}]`, and `say` tells the person the data isn't theirs. A snapshot says which day it was saved.
- **Templates.** `repo_activity` ("what shipped this week?") is commits as a small city. Height is lines changed, colour is the author, and the source is `connector:source_control/list_commits`.
- **Biology templates (as built, 2026-11-08, host runtime 4).** These add no new kinds and no data tables; they're listed here with the other templates.
  - `cell` is a bundled CC0 `gltf` cell cutaway (Grab3D) with labelled organelles and a timeline that follows a protein out of the cell.
  - `heart` is a bundled CC0 `gltf` heart cutaway (Grab3D) that beats on a looping timeline, with labelled chambers, valves and vessels and a timeline that traces the blood.
  - `brain`, `plant_cell`, `eye` and `dna` (as built, 2026-11-09) follow the same pattern: a bundled CC0 Grab3D `gltf`, labels with leaders, and a guided tour on a timeline. `dna` also has a looping `spin` timeline on the model's `rot`.
  - All of them set `view.radius`. Without it, `Fit()` measures only the renderers present when the scene is built, and a `gltf` that is still loading gives the wrong scale.
  - Both are standalone `viz` artifacts with no permissions. A `button` row in a panel wraps when the next button wouldn't fit.
- **Physics templates (as built, 2026-11-09, host runtime 6).** `double_pendulum` is two double pendulums on one pivot, started 0.01° apart, so they swing together and then split; `solar_system` is the Sun and the four rocky planets at their real distances and masses on `n_body`, with G = 4π² so time reads in years. Both are standalone `sim` artifacts with no permissions. Runtime 6 adds the `pendulum` solver and `line.through` (section 14).

## 8. Permissions

| Key | Values | First-open sheet says |
|---|---|---|
| `network` | `[]` in Phase 1 | Refused if not empty. Data arrives only through connectors. |
| `connectors` | `["source_control:read"]` | "Reads your connected source control" |
| `storage` | `artifact-local` | Not shown |
| `write_project` | `none`, `confirm` | "Can ask to change your project" |
| `room` | `none`, `read` | "Uses your room's layout" |
| `things` | `none`, `read` | "Reads your Thing's position and speed" |

Absent keys mean none. The first open lists everything that isn't none, with one approve button. The approval is stored on the device against a hash of the `permissions` block, so a changed list asks again (9.10). A connector with `:write` is refused in Phase 1.

## 9. Validation (returned as data, so the agent can fix it)

The validator is C# in `Artifacts/`, runs on the device, and runs before anything is shown. `artifact_create` returns the errors to the agent as data.

| Code | Example message |
|---|---|
| `newer_schema` | `schema albie.scene/2: this device reads up to albie.scene/1` |
| `unknown_kind` | `nodes[3]: unknown kind "cube" (did you mean primitive with shape box?)` |
| `unsupported_kind` | `nodes[1]: camera_rig isn't available in Phase 1` |
| `unknown_ref` | `rules[2].zones: no node "gate"` |
| `unknown_slot` | `nodes[0].source: generator "course" has no slot "pins"` |
| `unknown_input` | `generators[0].params.gates: no input "gate_count"` |
| `output_mismatch` | `outputs: "collisions" has no metric rule` |
| `asset_not_listed` | `nodes[4].asset: "assets/cone.glb" isn't in the manifest` |
| `hash_mismatch` | `assets[0]: sha256 doesn't match` (on open, as built) |
| `asset_missing` | `assets[0]: assets/rocket.glb is missing` (on open, as built) |
| `permission_denied` | `permissions.network must be empty in Phase 1` |
| `permission_missing` | `space is room, so permissions.room must be read` |
| `script_not_allowed` | `script must be null in Phase 1` |
| `too_big` | `nodes: 900 is over the limit of 512` |
| `bad_value` | `inputs[0]: default 20 is above max 12` |
| `cant_build` | `scene: valid, but this version can't build: gltf` (from `artifact_create {manifest, scene}`) |
| `no_data` | `It needs "commits" from connector:issues/list_issues, and there's no copy of it on this headset.` (on open) |

**Limits (proposed, not yet measured: 9.11, question 2):** 512 nodes, 256 copies in one `array`, 1,024 copies in a scene, 64 zones, 64 rules, 256 KB for `scene.json`, 2048 px textures.

## 10. Events and tools

**Events.** Modules subscribe with `ForgeNet.On("artifact.run", ...)` and the host raises them with `ForgeNet.Emit`. Both work with no hub, and neither needs a change to `ForgeEvents.cs`, which answers 9.11 question 8.

| Event | Payload | When |
|---|---|---|
| `artifact.updated` | `{project_id, artifact_id, version}` | An artifact was created or changed. |
| `artifact.layout` | `{artifact_id, layout_id, placed, shrunk, message}` | A layout was made. Albie speaks `message`. |
| `artifact.run` | `{artifact_id, run_id, state, outcome, reason, metrics}` | The run changed state. |

**Tools** go in `Artifacts/Resources/AlbieTools/artifacts.json`, registered with `AlbieTools.Register`, so they run on the device with no hub. They replace `artifact.create`, `.run`, `.bring_in` and `.pin` in section 10, under C-14 naming.

| Tool | Does | Confirm? |
|---|---|---|
| `artifact_list` | The library, the project's artifacts and the templates. As built, also `data_sources`: `{source, permission, columns, sample_only}` for each source this headset can read. | No |
| `artifact_create {template, inputs?, open?}` or `{manifest, scene}` | Makes an artifact. A template needs no model output, so "Make me a slalom" works with no network. Validation errors come back as data. As built, `open` (default true) opens it straight away, and the result's `say` is the layout's `fit.message` for Albie to speak. Making the same thing again changes nothing; a different one bumps `version`. As built, `{manifest, scene}` that fails validation saves nothing and returns `{status: "invalid", errors: [{code, path, message}], next}`. | No |
| `artifact_open {id}` / `artifact_close {id}` | Opens it, with the permissions sheet on first open. | Sheet on first open |
| `artifact_run {id, scenario?}` | Arms a run on the current version. | No |
| `artifact_compare {id, runs?}` | Run metrics side by side, with the like-for-like flag. | No |
| `artifact_relayout {id}` | Makes a new layout for this room. The old one and its runs are kept. | No |
| `artifact_bring_in {id, project}` | Copies a standalone artifact into a project. | Yes (writes to a project) |

Nothing in this list sends anything off the device. The `connector:` reads are C-18's.

## 11. Fixtures

Under `Mocks/Resources/Mocks/artifacts/`, read with `ForgeNet.Get("/artifacts/slalom/manifest")` in mock mode:

| Path | Covers |
|---|---|
| `list.json` | `artifact_list` |
| `slalom/manifest.json`, `slalom/scene.json` | The competition artifact |
| `slalom/layouts/lay_0001.json` | A full fit |
| `slalom/layouts/lay_0002.json` | A small room: shrunk, with a message |
| `slalom/runs/run_0001.json` to `run_0004.json` | Failed (rolled over), completed by hand, completed by the Autopilot, completed with a cone hit. All on truck v2 or v3, so they don't assume an answer to question 7. |
| `repo_city/manifest.json`, `repo_city/scene.json`, `repo_city/data/repo_activity.json` | `volume` space, an `array` from a table, a null cell, a `sample` label |
| `stance/manifest.json`, `stance/scene.json` | A standalone artifact the host builds today: no permissions, live `slider` inputs, `on_input` rules |
| `launch/manifest.json`, `launch/scene.json`, `launch/assets/rocket.glb.bytes` | A `gltf` node (the v1 rocket from the CAD mocks) on a `timeline`, with `animate` rules for each property and a Launch button that restarts it |
| `broken/manifest.json`, `broken/scene.json` | Fails validation on purpose (`unknown_kind`), for the refusal path |
| `three_body/manifest.json`, `three_body/scene.json` | A live `sim` artifact (section 14): the figure-eight orbit, with trails, live stats and sliders |

`Mocks/artifacts/sequence.json` plays a whole run through `ForgeMockPlayer`.

## 12. Questions to settle

**For Person 2 (Thing runtime and Core):**
1. Can the Thing runtime expose the probes in section 4 (`speed_ms`, `upright`, `height_m`), plus the body position, the Thing's name and version, and the attached controller's name, kind and version? Proposed: a small read-only interface on the running Thing.
2. Does `thing_reset` put the Thing back at a given pose? A rerun needs to return it to `start`.
3. Is `MRUKRoom.Anchor.Uuid` stable across sessions as the room key? It's the proposed key, not yet tested on a Quest.
4. C-04 has to land for `volume` and `panel` artifacts to open away from the desk. Is it still on track for Nov 6?

**For Person 3 (brain, connectors):**

5. Is `artifact_create {template}` the right first step for "Make me a slalom", with hand-written scenes for anything else?
6. Is section 7 close enough to what the connector host will return?

**For Person 1 (demo):**

7. **Demo step 7 can't work as written.** "Run v1's numbers against v2" needs a v1 run on the slalom, but v1 flipped in step 5, before the slalom existed. Proposed: compare v2's run with v1's incident from the notebook, labelled "free drive, not this course". The other option is to drive both versions on the course, which adds about 20 s to a demo that's already at 175 s.
   - **As built, 2026-11-06 (Person 5), for Person 1 to confirm.** The proposal is built in two places:
     - **Voice.** Albie speaks `notebook_compare`'s `say` line, for example "v1 rolled over on a free drive, not this course. v2 finished the slalom in 8.6 s."
     - **Scoreboard.** The slalom scoreboard has a `versions` row that shows the same thing, "v1 rolled over (free drive) · v2 8.6 s". It updates by itself when v2's lap finishes.
   - **No extra driving.** Neither version has to drive the course twice, so the step keeps its 20 s.
   - **Runtime.** The slalom template now needs host runtime 5.
   - **Scoreboard layout.** The scoreboard's `runs` and `versions` rows shrink a long value to fit, and end it with "…" if it still doesn't fit. Before, five runs spilled off the panel and over the label.
8. The first-open sheet adds a tap to step 7. Section 9.10 requires it, so it stays unless you want built-in templates to skip it. Proposed: keep it.

## 13. What's out of this contract (for now)

- Phase 2 scripts and QuickJS (D-18), and the `script` field's contents.
- WebView panels.
- Zip bundles, sharing, the cloud library and its cache rules (9.9).
- The cloud headless check (9.4). It can come back after the competition as an extra gate.
- Writing to projects from an artifact. `write_project: confirm` is reserved, but no competition artifact uses it.

## 14. Proposal: live simulation (needs Hudson's decision)

> **Status: PROPOSAL, added 2026-11-03.** `docs/ARTIFACTS_BRIEF.md` says an artifact should let you "edit things and see how they work over time". Section 9.8 says a three-body integrator needs Phase 2, and the Phase 1 fallback only plays back precomputed data, which can't react to an edit. This is one way through. It answers the first row of the brief's "Where this differs from the spec" table for simulations a solver covers, and **Hudson decides whether it stays**.

**The idea.** A `sim` node names a **solver**: fixed C# in the host that moves other nodes over time. The agent chooses a solver and writes its numbers. No generated code runs, so D-18 still holds and Phase 2 still needs its spike.

**What this doesn't do.** It isn't "do whatever we want". An artifact can only use solvers we've written, so anything else still waits for Phase 2 or a new solver. Each new solver is one registration, but it's a host change.

```json
{ "id": "orbits", "kind": "sim", "solver": "n_body", "rot": [38, 0, 0], "rate": "$speed",
  "params": { "g": 1, "step": 0.001, "softening": 0, "velocity_scale": "$kick", "bound_radius": 2.6 },
  "bodies": [
    { "node": "body_a", "mass": 1, "pos": [0.97000436, -0.24308753, 0], "vel": [0.466203685, 0.43236573, 0] },
    { "node": "body_c", "mass": "$mass_c", "pos": [0, 0, 0], "vel": [-0.93240737, -0.86473146, 0] }
  ] }
```

| Field | Rule |
|---|---|
| `solver` | `n_body` or `pendulum` (as built, runtime 6). An unknown solver fails validation. |
| `bodies` | 1 to 16. Each `node` is driven by exactly one body. Make those nodes children of the `sim` node, so its `rot` tilts the whole system. |
| `rate` | How fast you watch, in simulated time per second. Changing it never restarts the sim. |
| `params` | Solver numbers. **Changing any of these, or a body's numbers, restarts the sim**, so what's on screen always comes from the values on the panel. |

**`n_body`.** Leapfrog integration in doubles, with the centre of mass held at the origin. A run is repeatable: the same inputs give the same result. When a body passes `bound_radius`, the sim stops and stays on screen.

**`pendulum` (as built, runtime 6).** One or two links hanging from the `sim` node's origin, swinging in its x-y plane. Bodies have `mass`, `length` (m), `angle` and `omega` (degrees from straight down); `params` are `g` (default 9.81), `step`, `damping` and `angle_offset` (degrees, added to every link's start). Fourth-order Runge-Kutta in doubles. It publishes `time`, `energy_drift_pct`, `flips` (times any link goes over the top), `angle_1` and `angle_2`. More than 2 links is `too_big`; a `length` of 0 or less is `bad_value`.

**New in the scene:**

| Addition | Rule |
|---|---|
| `metric` op `sim` | `{ "type": "metric", "name": "closest", "op": "sim", "sim": "orbits", "value": "closest_approach" }`. `n_body` publishes `time`, `energy_drift_pct`, `closest_approach` and `escaped`. |
| Action `sim` | `{ "sim": "orbits", "action": "play" }`, with `play`, `pause` or `restart`. |
| `view` | `{ "radius": 1.25 }`, for `volume` space. Scales that sphere to fit the box, so a scene that moves isn't refitted as it moves. |
| `line` field `follow` | A fading trail behind a node with the same parent. `length` is at most 2,048 points. |
| `line` field `through` | As built, runtime 6. A list of 2 to 17 node ids with the line's parent; the line joins them every frame, after sims move (a pendulum's rods). An unknown id is `unknown_ref`, a different parent `bad_value`. |
| `line` `points` with inputs | As built, runtime 6. A point may name an input (`[0, "$cog_m", 0]`); the line follows it live. |
| `primitive` field `glow` | Self-lit, for things that should read as light. |
| `stat` field `decimals` | Default 2. |

**Validation.** It uses the existing codes: `bad_value` for an unknown solver, no bodies, a body driven twice, a mass or step of 0 or less, and a trail with a different parent; `unknown_ref` for a missing node, sim or value; `too_big` for over 16 bodies or a trail over the limit.

**Questions for Hudson:**
1. Is a set of built-in solvers the right Phase 1 answer to "do whatever we want", or should we go straight to the Phase 2 spike?
2. Which solvers come next? The brief's drone air simulation is the obvious one, and it probably belongs with the Thing runtime rather than here.
