# Station: demo-pipeline

Read `docs/demo/briefs/_COMMON.md` first and follow it.

You own: `unity/Assets/_Forge/Demo/Scripts/Pipeline/`, `Demo/Resources/pipeline_data.json`, `Demo/Prefabs/Pipeline*.prefab`, `Demo/Editor/Pipeline/`, `Demo/Sandboxes/PipelineSandbox.unity`.

Beat B5 and B5b in `VIDEO_SCRIPT.md`: pull a tracking model from the model hub, fine-tune it on Hudson, push it to his private repo after a Confirm poke. On camera it lasts about 25 s, so it is a fast-forward that still reads as real ML.

## Build

1. **`pipeline_data.json`**: `model` (same name as the picked model in the research data: "face-track-nano", 3.2 M params, Apache-2.0, 12.8 MB), `dataset` ("hudson-faces-v1: 1,240 frames from the headset cameras, 3 lighting sets" — fine, Hudson is an entrant), `train` {epochs: 30, per-epoch `loss`, `val_loss`, `map50` arrays that look like a real fine-tune: loss from ~1.9 to ~0.31 with noise, mAP@50 from 0.41 to 0.93, a little overfitting wobble near the end}, `samples` (4 frames: you can't use real photos; draw abstract silhouettes or use the `model_a`/`model_b` images from `Demo/Resources/DemoImages/` when they exist, with boxes and confidence that tighten as epochs rise), `push` {repo: "hudson/drone-tracker" shown as "drone-tracker (private)", branch "main", files: `weights/face-track-nano-ft-v1.onnx 12.6 MB`, `config.yaml`, `eval/report.md`, commit message "Fine-tune face-track-nano on hudson-faces-v1 (mAP50 0.93)", short sha}.
2. **Pipeline panel** (`DemoLayout.Slot.Low`, a wide low panel, tilted up ~25° toward the face): three step nodes joined by a line, **Pull**, **Train**, **Push**, the active one glowing.
   - `pipeline.pull` (~4 s): a model card slides into the Pull node, a progress bar with MB counting up, "verified licence: Apache-2.0" chip. Raise `pipeline.pulled`.
   - `pipeline.train` (~12 s on screen): an epoch counter 1/30 → 30/30, a "20x" badge, two live line charts (loss falling, mAP rising; reuse `ML/Scripts/**/MLCurve.cs` / `TrainingDash` if they fit, otherwise draw lines with a UI line or `LineRenderer`), a GPU chip ("cloud GPU, 1x A10G"), and the sample frames whose boxes snap tighter. Raise `pipeline.trained`.
   - `pipeline.push`: a confirm card, "Push to private repo?" with the file list and sizes, buttons **Cancel** / **Confirm** (poke + mouse). On Confirm: an upload bar, then a commit card (repo, branch, sha, message, a lock icon "private"). Raise `pipeline.pushed`. Cancel just closes the card.
   - `pipeline.open` / `pipeline.close`, `demo.reset`.
3. All motion eased; the charts draw smoothly (interpolate between epochs), no allocation per frame in `Update` (pre-size the point arrays).

## Done when
Your Play Test fires open, pull, train, push, a simulated Confirm, close, and the screenshots at each step look polished and legible at 0.5 m. Committed, report, stop.
