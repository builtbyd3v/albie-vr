# Artifact mode: contract proposals

Proposals for the Core owner (Hudson) from the artifact-mode work. Nothing here is live until it's accepted.
Each section names its plan step. Append new sections at the end.

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
