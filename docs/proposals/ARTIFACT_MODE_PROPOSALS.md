# Artifact mode: contract proposals for the Core owner

For Hudson (Core owner) to accept or reject. They come from the artifact mode work on `feat/artifact-mode`. Each
entry says what changed in code, what it changes in C-12 (`docs/ARTIFACT_CONTRACT.md`), and whether it needs a
HostRuntime bump. Nothing here adds a node kind, a field the host reads, or a generator, so HostRuntime stays 6.

Later steps add their own entries below.

## P-B-1. Physical checks before saving a written artifact: new codes, `checks` and `facts` (C-12 sections 9 and 10)

**Built** (plan steps B1 to B4) in the Artifacts module only: `Artifacts/Scripts/ArtifactPlausibility.cs`, called from
`ArtifactLibrary.FromScene` after the validator, `ArtifactNodes.Missing` and the dry open. It is never called from
`ArtifactHost.Open`, so templates, sandboxes and artifacts already saved open exactly as before. No node kind, no field
the host reads and no generator is added, so there is no HostRuntime bump. It reads only JSON, with its own
double-precision maths (Unity's Euler order), runs once per `artifact_create` and never in `Update`. It skips any node
on a generator slot (`at`) and everything under it: it never guesses room positions.

What it measures: every visible primitive (not gltf, text, line, zone; not sim bodies, which are stepped with their own
solver), as an oriented box in scene space, composed down the parent chain with full 3x3 matrices so a rotated,
non-uniformly scaled parent shears its children correctly. Round shapes are measured by their rims, not box corners.
A speck under 4 mm that only holds children is treated as a hinge (there is no group kind), not as a part.

New error codes (refuse the save, `status: invalid`, each with a `hint`):

| code | rule |
|---|---|
| `size_missing` | a primitive with no `size` and a `scale` other than 1: size defaults to 0.1 m, so the part is 10x smaller than meant |
| `parts_collapsed` | at least 3 top-level parts, and at least half of them, have no `pos` and share one centre (the `position` slip) |
| `parts_coincident` | two parts with the same centre and the same box (within 2% or 1 mm per axis), unless both are inside a bigger part |
| `part_too_small` | a part under max(4 mm, 1/250 of the scene) across; planes exempt |
| `too_big_for_space` | a panel scene over 0.8 m across (shown at true size), or a room course's own parts outside 0.05 to 8 m |
| `outside_view` | in a volume with `view.radius` r, a part (at rest, at any of 9 swept poses, or a sim body over its first 2 s) reaches past 1.15 r |
| `part_floating` | a group of parts touches nothing in the main group (contact: gap at most max(3 mm, 5% of the smaller part)). Error when grounded (volume with no view, panel, room); a **warning** in a volume with a view |
| `control_unbound` | a slider whose input no host-read field uses (`$name` outside panels and free text, or in generator params) |
| `control_invisible` | a slider whose min-to-max sweep moves no part corner more than max(5 mm, 1% of the scene) and changes no sim it drives; or a button whose actions only play/pause timelines that animate nothing that changes |

New warning codes (saved and opened; returned as `checks`):

| code | rule |
|---|---|
| `would_tip` | grounded: the volume-weighted centre of the main group is past its footprint by more than 5% |
| `pivot_unsupported` | grounded: a part that turns (bound or animated `rot`) touches a part that holds it, but its pivot (its own origin) is more than max(1 cm, 0.55 x its thinnest size) from it |
| `moving_part_collides` | over 9 swept poses (input min to max, or animate keys and midpoints), a moving part goes more than max(5 mm, 10% of the thinner part) into a static part it didn't touch at rest, or below the base |
| `motion_invisible` | the keys or range are all one value, or the part turns about the round axis of everything riding on it |
| `sim_not_checked` | a slider drives a sim and the 20,000-step budget ran out before a difference showed |
| `not_checked` | the checks themselves threw; never blocks a save the validator allowed |

A sim input counts as working if it changes the sim at all (start, rate, or path within the first watched second),
however small: a chaos demo's 1-degree nudge is tiny at first. It catches a slider wired to a number the motion ignores.

The moving-part checks and `would_tip` are warnings until their thresholds are tuned on live harness output (audit 3:
the windmill gap of 5 mm sat next to the 4 mm tolerance). Proposal: promote them to errors once the D5 corpus shows
no false refusals.

Tool result changes (additive):
- On a refusal, `checks` (warnings) and `facts` are added next to `errors`.
- On a save, `facts` is always added; when there are warnings, `checks` (code, path, message, hint) and a `next` line:
  "Saved, but N things may look physically off (checks). If the person would notice, fix them and call artifact_create
  again with the same id; otherwise carry on."
- `facts` is a few plain lines (under 1,500 characters): how it is shown, the scene's size, each part's box, what
  touches what, what each slider moves and by how much, and what turns about which world axis. It is for the agent to
  say what it built and to fix from; it is not a contract field the host reads.

The `artifact_create` guide (`Resources/AlbieTools/artifacts.json`) gains one line saying the parts are measured before
saving, and its worked example's pivot moved from z -0.01 to -0.004 so it touches the dial (it was 5 mm in front of
it, which would come back as a part_floating warning). Guide length 5,091 characters, inside the check's 3,500 to 5,200.

Verified (offline, see the run log in the work report): every template gives 0 errors and 0 warnings; the 5 Mocks
fixtures and 4 archetypes give 0 errors (warnings: fan hub 15 mm off its mast, stopwatch pivot 5 mm off its dial,
launch arm 4 mm off its tower, stance CoG marker passing through the track); the saved live seesaw gives size_missing
x3 and parts_collapsed; the saved live traffic light gives size_missing x5, parts_coincident x3 and parts_collapsed.
The prototype's fixtures and 10 one-problem mutations are in `Mocks/artifacts/authoring/plaus_*.json`, each with its
expected codes. Forge/Artifacts/Check Authoring asserts all of it (steps B1 to B4).

Not measured: the time on Quest. On this Mac it is under 30 ms for every template and fixture (worst: the double
pendulum, with its sim sweeps).

## P-A8-1. Voice routing for artifact creation, and two log lines (Core: `AlbieVoice.cs`, needs sign-off)

**Built** in two commits of their own ("Voice A8", "Voice D1"). They are Core code, so the Core owner signs off.
No contract field changes and no HostRuntime bump.

- `ToolRules`: the old artifact sentence ("never make up positions") is now a general create rule. Any "make, build
  or show" request goes to `artifact_create`. A template is used when one fits (slalom, hoop_course, repo_activity,
  and the tool lists the others). Otherwise the model writes the manifest and scene itself. It picks positions inside
  the artifact, never room positions, so it never uses space room. It says what it's building in under eight words,
  fixes an invalid result without talking, and stops when the result says stop. `VoiceRulesCheck` still needs
  `hoop_course` and `repo_activity` in a tool description, and they are still there.
- `Brevity` / `BrevityNow`: the verb list now includes make, build and create. BrevityNow also says "never say you
  can't make something before calling artifact_create".
- Stale tool results are no longer dropped. Before, a result that finished after a new turn started (`seq !=
  turnSeq`), or one queued when a reply was cut off (`StopSpeech`), was thrown away, so the next turn didn't know
  an artifact had been made. Now it is still sent as `function_call_output`, with no `response.create`, and only on
  the socket the call came from.
- D1: `Debug.Log("[AlbieVoice] said: ...")` after `agent.answer_done`, and `call <call_id>` on the tool result line.
- Not done, from the audit's list: the Keyterms additions (artifact, slider, button) and the optional 6-round cap
  per turn. The cap would touch the `stt.start` case, which feat/voice-cost rewrites.

**How it merges with `origin/feat/voice-cost`.** `git merge-tree --write-tree origin/feat/voice-cost HEAD`
auto-merges `AlbieVoice.cs` with no conflict. The merged `AnswerDone` has voice-cost's `Remember("Albie: ", text)`
and then the D1 log, and `StopSpeech` has voice-cost's `speechEnd` line before the stale-output send. The merge does
report one conflict, in `Pet/Scripts/Voice/AlbieFollowUp.cs`, but that one comes from the GatorFollowUp to
AlbieFollowUp rename on main. The same conflict shows up with the commit before these (1acf7dc).

**Follow-up once voice-cost is in** (it can't be written before, because `Remember` exists only there). Put this in
`RunTool`'s callback, after the Debug.Log:

```csharp
// A saved artifact's result has id and version and no status; a refusal has status "invalid".
if (name == "artifact_create" && result["status"] == null && result["id"] != null)
    Remember("Albie: ", $"made artifact {(string)result["id"]} v{(int?)result["version"] ?? 1}");
```

## P-D-1. Live-test harness tools (editor only, Artifacts module)

No contract change. These are all in `Artifacts/Editor`, so they are never in a build.
- `VoiceTurn.TypeTurn(text)` types a turn into the open voice session. It sends a user `input_text` item, then one
  `response.create` with the usual BrevityNow, over AlbieVoice's own send queue. It reaches AlbieVoice by
  reflection, so Core has no test code. `VoiceTurn.Check()` lists any member that has gone missing. The members it
  uses (`turnSeq`, `finalSent`, `Send`, `RequestResponse`, `StopSpeech`, `Emit`, `BrevityNow`) are all still in the
  voice-cost version, and it calls `Remember` when that exists.
- `LiveTranscript.Start(dir)` / `Stop()` record the voice and artifact events from `ForgeNet.On`, plus the
  `[AlbieVoice]`/`[Artifacts]` log lines, as JSONL. It refuses any folder inside the repo.
- `ArtifactShot` (D4) collects renderers from `ArtifactHost.ContentRoot` and waits for the reveal. When nothing
  opened, it says why.
- Not done: playing a WAV through stt.start, OnBinary and stt.stop (optional in the plan).

## P-A7-1. The artifact_create guide and a shallow typed schema (C-12 section 10, tool text only)

**Built** (commit "Artifacts A7"). No contract field changes and no HostRuntime bump. It is in the Artifacts module
(`Resources/AlbieTools/artifacts.json`), but every voice session is billed for it, so the size is recorded here.

- The `artifact_create` description is now a general authoring guide: units and axes, the node kinds and their
  fields, the only four ways an artifact acts (slider binding, button to action, timeline plus animate, sim), the
  results, and one worked example (a stopwatch, short form). It's 4,894 characters, of which the example is 930. The
  old description was about 3,900. It's still a little over the ~4.5k target. The next thing to cut is the example's
  second button.
- It follows the code as built: on_input matches `input`, inputs are number or integer only, line points are
  bindable, timeline autoplay defaults to false, and only `<node>.visible` can be set. It tells the agent to say what
  it's building in under 8 words before calling, and which fields the tool fills.
- The parameters now carry a shallow typed schema: enums for template, category, input type, space (volume or panel
  only, because room is for templates), node kind, shape, solver, widget type, rule type, metric op, animate
  property, zone event, action and run verb. It has no `additionalProperties: false`; the validator is the guarantee.
  The manifest requires only `title`; the scene requires `space` and `nodes`.
- Check Authoring (A7) keeps the two in sync. Each enum must equal its `ArtifactSchema` set
  (`NodeKinds`, `Shapes`, `Solvers`, `Widgets`, `Rules`, `MetricOps`, `AnimateProperties`, `ZoneEvents`,
  `TimelineActions`, `RunVerbs`, `BuiltInputTypes`, `Categories`, `Spaces` minus room, `ArtifactLibrary.Templates`).
  Every property the schema names must be a key the validator takes (`ArtifactSchema.Keys`). The worked example is
  parsed out of the description and sent through `artifact_create`, and it must save valid, with controls and
  motion.
- Not verified: whether grok-voice-3.0 enforces nested `enum`/`required`, and its size limit for a tool definition.

## P-A6-1. `artifact_create` accepts a short form, and says what it built (C-12 section 10)

**Built** (commit "Artifacts A6"). No HostRuntime bump. The files on disk are still full C-12.

Proposed text for section 10's `artifact_create` row, as built:
1. **Looser input.** `manifest` and `scene` can be JSON objects or the same objects written as JSON strings. When
   these are absent, the tool fills them: `schema`, `id` (a slug of `title`), `version` 1, `kind` standalone,
   `category` (sim if there's a sim node, otherwise tool), `template` null, all-null `links`,
   `runtime {type: scene, min_runtime}`, `scene` "scene.json", `script` null, `inputs` [], `scenarios`
   `[{id: default, seed: 0, params: {}}]`, `data` [], `assets` [], `outputs` [], and a `permissions` block that grants
   nothing (network [], connectors [], storage artifact-local, write_project none, room none, things none). It also
   fills `scene.schema`. It never fills `space` or generators, and never grants a permission, so a room scene
   written with no permissions is still refused at `permissions.room`. The result lists what was filled:
   `filled: [paths]`.
2. `min_runtime` is computed from the HostRuntime history in `ArtifactSchema`: room_hoops 2; table arrays 3; gltf,
   timeline and animate 4; the versions widget 5; the pendulum solver and lines `through` nodes 6. **For the Core
   owner:** the templates don't follow this history consistently. `solar_system` (n_body) says 6, while the
   `three_body` fixture (n_body) and `repo_city` (table array) say 1. Please confirm the history, or say what
   `min_runtime` should mean.
3. **Success** adds `built: {parts, controls: [{slider: input} | {button, label, does}], moving: [{node, by}]}`.
   `by` is "timeline <id>", "sim <id>" or "slider <input>". When there are no controls, it adds
   `say: "It has no controls: it's there to look at."`.
4. **Refusal** adds `attempt: n`, which counts written artifacts refused in a row, reset by any success or after
   90 s. On the third refusal, `next` says to stop, tell the person the first problem in plain words, and offer the
   closest template or a simpler version.
5. **Repeats collapse.** An unknown key with the same hint on many objects (such as `position` on every node) is one
   entry, at the first path, with up to 10 more under `also`. The message says how many more places there are. Live
   traffic light try 5 goes from 16 errors to 7 entries. `ArtifactValidator.Validate` still returns every error.

## P-A5-1. A dry open before saving (C-12 section 9: open-time codes now also come from `artifact_create`)

**Built** (commit "Artifacts A5"). No HostRuntime bump.

- `ArtifactLibrary.FromScene` validates, refuses a template's id, runs `ArtifactNodes.Missing`, and then runs
  `DryOpen` on an in-memory package (`ArtifactPackage.InMemory`) before writing anything. The dry open runs the
  same reads the open does:
  - `ArtifactData.Resolve` gives `no_data` at `data`;
  - `ArtifactAssets.Resolve` gives `asset_missing`, `asset_not_listed`, `too_big`, `hash_mismatch` or `bad_value` at
    `assets[i]`;
  - `ArtifactArray.Problem` for each table array gives `unknown_ref` at `nodes[i].source`.
  Each error has a hint, and `artifact_create` returns them with `status: invalid`. Nothing is saved.
- **The 1,024-copies-per-scene limit** (section 9 "Limits") is enforced here. A table array counts
  `min(rows, 256)`. A slot array counts its slot's poses: 1 for start and finish; for a list, the generator's count
  param, at its input's max when a slider sets it, or the generator's default. Over 1,024 is `too_big` at `nodes`.
  Proposed: section 9 should say this is checked at `artifact_create`, not by the validator, because the count
  depends on the data.
- Room placement (`no_free_floor`, `no_headroom`, `no_room_data`) stays an open-time error, because it depends on
  the room.
- Templates are unaffected (`FromTemplate` doesn't call it, and neither does open). slalom and hoop_course written
  under new ids pass the dry open, and repo_city written under a new id still saves from the bundled sample.
- One existing check changed with it. `RepoActivityCheck` used to save an artifact whose connector has no data
  anywhere, and then resolve it. That save is now refused with the same `no_data` message, and the check asserts the
  refusal.

## P-A4-1. Refuse what the host can't run (C-12 sections 2, 3, 7, 8 and 9, as built)

**Built** (commit "Artifacts A4"). Each rule refuses input that the host ignored, dropped or mishandled. None of
them adds a field or a generator, so HostRuntime stays 6. Every template and Mocks fixture gives byte-identical
validator output before and after (slalom and hoop_course: 0 errors).

Actions and controls (proposed text for section 7, "as built"):
1. An action holds exactly one verb: `set`, `run`, `timeline` or `sim`. No verb, or two of them, is `bad_value` at
   `rules[i].do[j]`. Before this, `ArtifactHost.Do` ran only the first verb it found and dropped the rest silently.
2. `set` is `"<node>.visible"` with a boolean `value`. Anything else (for example `"lamp.color"`) is `bad_value` at
   `.set`, with the hint "use an animate rule on a timeline". The host already logged "can't set ... yet" and did
   nothing.
3. `sim` names a sim node (`unknown_ref` otherwise). `sim` and `timeline` actions take `action`: play, pause or
   restart. `run` is arm or reset, and it needs a `run` block in the scene.
4. Buttons count only on `panel` nodes, because only panels draw widgets. Every button needs an `on_input` rule
   naming its id. Otherwise it's `bad_value` at the widget ("pressing it does nothing").
5. `on_zone` takes `event` enter or exit, and it needs a run block. Metric ops other than `sim` need a run block too,
   because they count only while a run is scored.
6. `hit_count` targets are arrays whose template is a primitive with `collider: true` (what `ArtifactRun.Bind`
   attaches).
7. Metric names are unique. A text node shows at most one `{metric}`.

Nodes and space:
8. A `parent` can't be a panel (it sits beside the scene) or a timeline (it's only a clock).
9. `at` only on a single slot (`start`, `finish`). The host used `poses[0]` of a list slot (cones, gates, hoops).
10. A line's `source` is a slot name, not a table.
11. **Contract change, section 3 "Needs":** `space: room` needs exactly one generator. Without one, it's `bad_value`
    at `scene.space`, with allowed `volume, panel`. Room positions come only from a generator, never from the scene
    (the model never supplies room coordinates). This refuses the live test's saved seesaw and traffic_light copies.
    That's intended. They're user-local data, not repo files.

Inputs and manifest:
12. Input names match `^[a-z][a-z0-9_]*$` and are unique. An integer input's min, max and default are whole numbers.
13. **Contract change, section 2:** `boolean` and `choice` inputs are refused with `bad_value` at `inputs[i].type`
    ("not built on this host yet"), and the hint is to use an integer 0..N-1 with a slider. `ArtifactInputs` already
    skipped them, so nothing could set them. Section 2 should mark them "not built on HostRuntime 6". They remain in
    `ArtifactSchema.InputTypes` (the contract list). `ArtifactSchema.BuiltInputTypes` is what the host builds.
14. A string that starts with `$` is an exact reference to a number input. `"$gates*2"`, or a reference to a boolean,
    is `bad_value`. Generator params are covered too, because they are read through `ArtifactInputs.Number`.
15. Scenario `params` name inputs (`unknown_input`) and stay inside each input's range (whole for integers).
16. Permission values come from section 8's table: storage `artifact-local`; write_project `none|confirm`; room and
    things `none|read`. Before this, any value other than `read` counted as none.
17. `manifest.template` is null or a known template.
18. `artifact_create` with an id that is a template's (for example `slalom`) is refused at `id`, because the folder
    is the id and saving would overwrite the copy made from that template. This is in `ArtifactLibrary.FromScene`,
    not the validator, so opening the templates is unaffected.

Not built, deferred: the 1,024-copies-per-scene cap. A copy count is known only once the data tables and the slots
resolve, so it belongs in the A5 dry open, not in the validator.

## P-A3-1. Unknown fields are an error: new code `unknown_field` (C-12 section 9)

**Built** (commit "Artifacts A3"). No HostRuntime bump.

- Each object has a key table (`ArtifactSchema.Keys`): the manifest and its parts, the scene, view, generators and
  their params by type, nodes by kind, array templates, table sources, grids, maps, sim bodies and params by solver,
  widgets by type, rules by type (metric keys by op), actions, and the run block. `grid.base` is included.
- A key outside its table is `unknown_field` at its own path: `nodes[3].position`, `scene.scripts`, `name` (manifest
  top-level keys have no prefix, while scene top-level keys use `scene.`). `allowed` lists the keys that object takes.
- The hint picks, in order:
  - a case or underscore slip (`durationS` gives `duration_s`);
  - a general rename (`position` gives `pos`, `emissive` gives `glow`, `type` gives `kind`), offered only when the
    object takes that key;
  - advice when there's no counterpart (`onClick` or `scripts`: "a button is a panel widget plus an on_input rule";
    `children`: "set parent on each child"; `widgets` on a primitive: "controls go on a panel");
  - otherwise the nearest allowed key within edit distance 2 (1 for keys of 4 letters or fewer).
  The vocabulary is general model vocabulary, not words from any test request.
- A panel takes only id, kind, title, widgets, pos and visible. A timeline takes only id, kind, duration_s, loop and
  autoplay. An array takes no `at`, and line `length` is allowed only with `follow`.
- A node whose kind is missing or unknown is skipped, so `unknown_kind` stays the first error (`broken` still fails
  first with `unknown_kind`).
- Rank: `unknown_field` sorts with the structural errors.
- Proposed text for section 9's code table: "`unknown_field`: a key that object doesn't take. The host would ignore it,
  so it's refused rather than dropped."

## P-A2-1. Validation errors carry `hint` and `allowed` (C-12 sections 9 and 10, additive)

**Built** (commit "Artifacts A2").

- `ArtifactError` gains two optional fields. `hint` says what to write instead. `allowed` lists the values an enum
  field takes, or the ids a reference can name. `code`, `path` and `message` are unchanged.
- The `artifact_create` refusal becomes
  `{status: "invalid", errors: [{code, path, message, hint?, allowed?}], next, more?}`.
- Errors are sorted structural first (schema, `unknown_kind`, `unsupported_kind`), then references (`unknown_ref`,
  `unknown_slot`, `unknown_input`, `asset_not_listed`, `output_mismatch`), then everything else. The order is stable
  within each group.
- The tool lists at most 25 errors and puts the count of the rest in `more`. `ArtifactValidator.Validate` still
  returns them all, so the host and the checks see every error.
- Proposed text for section 10's `artifact_create` row: "fails validation, saves nothing and returns
  `{status: "invalid", errors: [{code, path, message, hint?, allowed?}], next, more?}`, structural errors first, at most
  25 listed."

## P-A1-1. How validation reads a file (C-12 section 9, as built)

**Built** (commit "Artifacts A1"). These are validator fixes, not new rules. Each one either refuses input that the host
already mishandled or keeps checking where the old code stopped. They are written down here because they change what
the agent sees.

1. A missing or malformed `schema` is reported as `bad_value` and the file is read as version 1. Only a newer major
   version (`newer_schema`) stops the pass. A version that doesn't parse, such as `albie.artifact/1.0`, is now
   `bad_value`. Before, it was `newer_schema`.
2. `manifest.scene` is required and must match `^[a-z0-9_-]+\.json$`, which is the rule `ArtifactPackage` already
   enforced at open. `FromScene` saves the manifest with `scene: "scene.json"`, because that's the only file it writes.
   Before this, traffic light try 5 passed validation, was saved, and then couldn't open.
3. More than one generator is `too_big` at `generators`, because the host places only `generators[0]`.
4. A node with no `kind` gives `unknown_kind` at `nodes[i].kind`, "kind is required".
5. Typed checks at each field's own path. `pos`, `rot`, `scale`, `size` and line `points[i]` are three numbers or exact
   `"$input"`s. Colours are `#RRGGBB`. `visible`, `collider`, `glow`, `billboard`, `loop` and `autoplay` are booleans.
   `text`, `title`, `parent`, `at` and `asset` are strings. `height`, `width`, sim `rate` and body numbers are a number
   or an `"$input"`. `runtime.min_runtime` and line `length` are whole numbers. Every list entry that should be an
   object is one.
6. There is no catch-all any more. Every error has a non-empty path, and `Validate` never throws.

Stricter than the host in three places. In every case the host already ignored or mishandled the value:
- colour names: `ArtifactLook.Color` accepts names like "red", while the contract text says `#RRGGBB`.
- a number for `scale`: `ArtifactInputs.Vector` falls back to `[1, 1, 1]`.
- a number for `text`: the host prints it as a string.

No template or Mocks fixture uses any of these. Their validator output is byte-identical before and after.

## P-A0-1. Two changes on main that went in without a HostRuntime bump

These were found while planning. Neither breaks anything. Both are worth recording in the contract:
- `ArtifactZone` reads an optional `source.grid.base` plate.
- `ArtifactTrail` line `points` can be bound to `$inputs` (through `build.Apply`).

An older host that reads a scene using either one ignores the field, without an error.

## Not part of this work

Steps A0 to A7 and B1 to B4 made no edits to `Core/` or `hub/main.py`. The Core edits to sign off are the two `AlbieVoice.cs` commits in P-A8-1. `hub/main.py` gets one change, in C1 (two lines that mount the artifacts router; sign-off below). A line's `width` is read once when it is built, so the guide no longer says a slider can drive it (proposal: make it live in a runtime bump).

## C1. Hub: POST /artifacts/review (advisory Jev review)

Branch `feat/artifact-review-hub`. Code: `hub/artifacts/` (`router.py`, `review.py`), tests in
`hub/tests/test_artifact_review.py`.

### Needs sign-off: hub/main.py (shared file)

Two lines, in their own commit: `from artifacts import router as artifacts_router` with the other imports, and
`app.include_router(artifacts_router)` after `ml_router`. Nothing else in `main.py` changes.

### Proposed contract (new REST route; no event, no HostRuntime change)

`POST /artifacts/review`, JSON in:

| field | type | notes |
|---|---|---|
| `request` | str, at most 500 chars, default "" | the person's words. Unity would get them from a new optional `artifact_create` arg `request` (plan C2) |
| `manifest` | object | the artifact's `manifest.json` |
| `scene` | object | the artifact's `scene.json` |
| `facts` | str, at most 6000 chars, default "" | Unity's deterministic facts text (plan B4) |
| `questions` | list of names, or null | null asks every question that applies |

JSON out, always HTTP 200 for a valid body (422 only for a malformed one):

```json
{"status": "ok" | "skipped" | "error", "reason": "no_key" | "no_questions" | "timeout" | "bad_response" | "<Jev error>" | null,
 "model": "jev-1.14.0", "elapsed_ms": 412,
 "verdicts": [{"question": "matches_request", "choice": "yes", "confidence": 0.91,
               "probabilities": {"yes": 0.91, "missing_main_part": 0.05}, "ok": true}]}
```

Questions, each a Jev `choice` (the form `ml/setup/classify_jev.py` uses):

| name | options (the ok ones first) | asked when |
|---|---|---|
| `matches_request` | yes, unsure / missing_main_part, different_thing | `request` is not empty |
| `parts_supported` | supported, unsure / floating_or_unattached | `facts` is not empty |
| `controls_effective` | all_visible, unsure / some_do_nothing | the scene has a slider, toggle, choice or button |
| `controls_match_labels` | matches, unsure / wrong_effect | same as above |
| `arrangement_sensible` | sensible, unsure / misordered_or_upside_down, wrong_proportions | always |
| `space_fits` | volume, panel, room. ok when the choice equals `scene.space` | `scene.space` is one of the three |

- `ok` is false only when Jev names a problem. "unsure" counts as ok, so a later gate can't refuse on doubt alone.
  This differs from audit 3, which listed only the positive option as ok.
- What Jev sees: the request, title, space, and a summary of parts, inputs, controls and rules (ids, kinds, shapes,
  parents, labels, `$input` bindings), plus the facts text. It never sees raw coordinates: `pos`, `rot`,
  `size` and unknown fields such as `position` are left out. Each section is capped at 120 lines and each
  string at 160 chars.
- Timing: Jev runs in a thread under `asyncio.wait_for`. The budget is 2.2 s (`FORGE_ARTIFACT_REVIEW_TIMEOUT`),
  so the hub answers inside Unity's 2.5 s timer (plan C2). The same budget is passed to `jev.ask`, so its HTTP
  call ends too.
- With no `TYPESAFE_API_KEY` it returns `{"status":"skipped","reason":"no_key"}` at once and makes no call. The key
  is read only inside `ml/setup/jev.py` and goes only to TypeSafe.
- Phase 1 is advisory: Unity puts verdicts in `result.review` and the log only. Gating (phase 2: `review_failed`
  when ok is false at confidence 0.85 or more, at most one refusal per id) waits until agreement has been measured
  on a labelled set. Jev's calibration on spatial facts text is untested.

### Open points for the owner

- The facts text (B4) and the optional `request` arg on `artifact_create` (C2) don't exist yet. Until then
  `parts_supported` and `matches_request` are never asked.
- Test fixtures: `hub/tests/fixtures/artifact_review/{seesaw,traffic_light}/` are copies of the two broken
  model-written artifacts saved on the device. `fan.json` is audit 2's archetype A.

## P-C2-1. `artifact_create` asks the hub for an advisory review: optional `request` arg, `result.review` (C-12 section 10, tool only)

**Built** (plan step C2) in the Artifacts module only: `Artifacts/Scripts/ArtifactTools.cs` and `artifacts.json`. No
node kind, no field the host reads and no generator, so there is no HostRuntime bump. Core (`AlbieTools.cs`,
`AlbieVoice.cs`, `ForgeNet.cs`) is unchanged: it uses the existing `AlbieTools.AsyncHandler` and `ForgeNet.Post`.

- `artifact_create` is registered as an `AsyncHandler` (the other artifact tools are wrapped and stay synchronous).
  It saves and opens exactly as before. Then, **only for a written artifact that was saved, with the hub live**
  (`Application.isPlaying`, not `ForgeNet.IsMock`, `ForgeNet.Connected`), it posts `POST /artifacts/review` with
  `{request, manifest, scene, facts}` (request cut to 500 chars, facts to 6000, the C1 limits) and waits at most 2.5 s.
- Only `status: "ok"` with at least one verdict adds `result.review`:
  `{by, verdicts: [{question, choice, ok, confidence}], flagged?: [question], note}`. The note says it's advisory, the
  artifact is already saved, and to fix it with the same id only if Albie agrees. Nothing else changes in the result.
- Everything else answers with the result unchanged: edit mode, mock, hub off, `{}`, `skipped` (no key), `error`,
  no verdicts, a reply that isn't JSON, a failed request, an exception, the 2.5 s timeout. Templates and refusals answer
  at once with no review, so `AlbieTools.Call` (SlalomSandbox) still gets the result synchronously.
- Never a gate, and never delivered through `albie.note`. Each create logs `[Artifacts] review of <id>: <why>`
  when it posted; `ArtifactTools.LastReview` keeps the last reason for checks.
- New optional tool arg `request` (string): "The person's own words for what to make, for an advisory review." Without
  it the hub drops `matches_request`. The guide text is unchanged.

**Asks for sign-off:** the two tool-contract additions (`request` in, `review` out) in C-12 section 10. Phase 2 (a gate
with code `review_failed`) stays out until agreement with Jev has been measured on a labelled set (audit 3).

**Risk to review:** with the hub live, a written create's tool result now arrives up to 2.5 s after the artifact
appears. `AlbieVoice.RunTool` already handles late results (A8: a result after a new turn still goes in, with no
reply), but this hasn't been run live yet.

## P-B-2. Physical checks after review: fewer false refusals (amends P-B-1, as built)

A review wrote fresh artifacts that the validator accepts and the physical checks wrongly refused. As built now:

- `parts_coincident` compares the solids, not their bounding boxes: the same shape with the same oriented corners
  (or two equal round spheres). Two boxes crossed at +45 and -45 degrees (an X, diagonal sails, scissors) save.
- `parts_collapsed` counts only top-level parts with no `pos` that cut through each other or are alike. Parts nested
  one inside another (a target's rings, nested shells) share the origin by design and aren't a collapse. The hint now
  also says: if they share that centre on purpose, write `"pos": [0, 0, 0]` on each. Residual: unplaced parts that
  cross but are concentric on purpose (a planet with two flat rings, no pos written) are still refused, with that hint.
- `control_invisible` sweeps a `$input` in `pos`/`rot`/`scale` only on a node that is, or holds, a measured part. A
  slider moving a text or a line is taken on trust (as a text's height already was): the host applies it, the check
  can't measure it.
- A line `through` parts joins them in the contact graph (bonds and ropes don't make `part_floating`), and a primitive
  a line is drawn through or follows is exempt from `part_too_small` (a rope's 2 mm hook, a tracer point).

No new code, no runtime bump; templates, sandboxes and the saved live fixtures give the same results as before.

## P-A6-2. Two tool-fill fixes (C-12 section 10, as built), and one open behaviour question

- A filled `id` that slugs to a template's id is filled as `my_<slug>` ("Heart" gives `my_heart`), because `FromScene`
  refuses a template's id. Before, the tool filled an id and then refused it on try 1.
- The `kind` hint no longer says "use standalone; it's linked to the open project for you". `FromScene` links to the
  open notebook project only when `kind` isn't `standalone`, and the tool fills an absent `kind` as `standalone`.
  The hint now says to use `project` to link it.

**Open for the Core owner:** since the A6 fill, an artifact written while a notebook is open is never linked unless
the model writes `kind` itself (in 51f83ba the model's own kind decided linking). Option: fill `kind` as `project`
when `NotebookStore.IsOpen`. Not built: it changes what gets linked to a project.
