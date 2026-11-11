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

The expected error codes and paths for each fixture live in the check, so that a rule change shows up as a diff there.
