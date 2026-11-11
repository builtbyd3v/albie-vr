# Authoring fixtures

Inputs for `Forge/Artifacts/Check Authoring` (`Artifacts/Editor/AuthoringCheck.cs`). They sit outside `Resources`, so
nothing here ships in a build. Each `.json` file is `{ "manifest": ..., "scene": ... }`, which is the shape of an
`artifact_create` call. A folder holds `manifest.json` and `scene.json`, which is the shape of a saved artifact.

- `saved_seesaw/`, `saved_traffic_light/`: byte copies of two artifacts the voice model wrote in the live test, as
  the device saved them (`persistentDataPath/artifacts/`). They passed the validator but open broken: they use
  `position`/`scale` and `scripts`, and they ask for `room` without a generator. Never edit these copies.
- `live_traffic_light_1..5.json`, `live_seesaw_1.json`: the model's real `artifact_create` arguments from the same test,
  taken verbatim from `Logs/Editor-prev.log` in the order they were sent.
- `arch_fan`, `arch_stopwatch`, `arch_lever`, `arch_status_beacon`: hand-written archetypes (a spinning part with a
  speed slider, a timeline with buttons, a slider-bound pose, visibility states). They must pass the validator.
- `probe_mixed.json`: one artifact that combines many mistakes (a bad scene file name, a boolean input, a button on a
  primitive, set on a colour).

- `plaus_*.json`: inputs for the physical checks (`ArtifactPlausibility`, plan B1 to B4). Each one is valid C-12, so
  `FromScene` reaches those checks, and carries its own `note` and `expect` (`errors` and `warnings`: "code" or
  "code@path"; an empty list means none). `plaus_seesaw_ok`, `plaus_seesaw_gap` and `plaus_windmill_bad` are ports of the
  audit prototype's fixtures; the others are one-problem mutations of the archetypes. `plaus_xsign_ok`, `plaus_target_ok`,
  `plaus_textslide_ok`, `plaus_swing_ok` and `plaus_molecule_ok` are designs a review found wrongly refused (an X of
  two crossed boxes, concentric rings with no pos, a slider that moves a text, a rope from a 2 mm hook, parts joined
  only by line bonds): they must pass. `plaus_pile_collapsed` is the real collapse they are told apart from.

The expected error codes and paths for each fixture live in the check, so that a rule change shows up as a diff there.
