# Artifact mode: contract proposals for the Core owner

For Hudson (Core owner) to accept or reject. They come from the artifact mode work on `feat/artifact-mode`. Each
entry says what changed in code, what it changes in C-12 (`docs/ARTIFACT_CONTRACT.md`), and whether it needs a
HostRuntime bump. Nothing here adds a node kind, a field the host reads, or a generator, so HostRuntime stays 6.

Later steps add their own entries below.

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

No edits were made to `Core/` (`AlbieVoice.cs`) or `hub/main.py` in steps A0 to A4, so there's nothing in Core to sign off yet.
