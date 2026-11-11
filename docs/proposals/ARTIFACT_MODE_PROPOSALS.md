# Artifact mode: contract proposals for the Core owner

For Hudson (Core owner) to accept or reject. They come from the artifact mode work on `feat/artifact-mode`. Each
entry says what changed in code, what it changes in C-12 (`docs/ARTIFACT_CONTRACT.md`), and whether it needs a
HostRuntime bump. Nothing here adds a node kind, a field the host reads, or a generator, so HostRuntime stays 6.

Later steps add their own entries below.

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

Steps A0 to A7 made no edits to `Core/` or `hub/main.py`. The Core edits to sign off are the two `AlbieVoice.cs` commits in P-A8-1. `hub/main.py` is untouched.
