# Artifact mode: contract proposals for the Core owner

For Hudson (Core owner) to accept or reject. They come from the artifact mode work on `feat/artifact-mode`. Each
entry says what changed in code, what it changes in C-12 (`docs/ARTIFACT_CONTRACT.md`), and whether it needs a
HostRuntime bump. Nothing here adds a node kind, a field the host reads, or a generator, so HostRuntime stays 6.

Later steps add their own entries below.

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

No edits were made to `Core/` (`AlbieVoice.cs`) or `hub/main.py` in steps A0 to A2, so there's nothing in Core to sign off yet.
