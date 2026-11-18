# ML panel layout verification

The Model summary, hint and right-aligned controls previously shared the same space. Results Overview used a 338 mm tall fit card inside a shorter panel and compressed class rows into space reserved for four classes. Neither panel rebuilt its layout after a window resize.

The Model now reserves separate rows for its summary, buttons and instructions, wraps its legend, and puts expanded layer details in a scrollable column. Results uses complete metric and class rows, stacks sections in narrow windows, and clips and scrolls content inside the panel. Resize updates the viewport, tabs and content bounds.

## Validation

Based on main `a730094` (includes PRs #137 and #138). Unity 6000.6.7f1 with Android selected:

- `MLPanelLayoutCheck.RunBatch`: 916 assertions for the real Model3D and ResultsWall prefabs with two-class text and ten-class image fixtures. Checks separate controls and rows, containment, text overflow, scrolling, filtering, and resizing down and back up. Writes screenshots and a report under `unity/Logs/MLPanelLayout`.
- `MLSmokeCheck.Run`: passed mock loading, initialization, relabel, removal, pair filtering, completion, overfit and restart; offline image training completed and Results matched held-out accuracy.
- Rendered PNGs inspected at default, short, narrow and wide sizes. These are Unity camera renders with synthetic data; physical Quest hand/controller interaction still needs a headset check.

Run from the repository root, omitting `-quit` because each check exits after Play mode:

```sh
"/Applications/Unity/Hub/Editor/6000.6.7f1/Unity.app/Contents/MacOS/Unity" -projectPath "$PWD/unity" -buildTarget Android -executeMethod MLPanelLayoutCheck.RunBatch -logFile /tmp/ml-panel-layout.log
"/Applications/Unity/Hub/Editor/6000.6.7f1/Unity.app/Contents/MacOS/Unity" -batchmode -projectPath "$PWD/unity" -buildTarget Android -executeMethod MLSmokeCheck.Run -logFile /tmp/ml-smoke.log
```

## Rendered examples

Model controls have separate rows:

![Model controls](images/ml-panel-layout/model-default.png)

Expanded details remain separate and scroll in a small Model window:

![Small expanded Model](images/ml-panel-layout/model-small-expanded.png)

Two-class Results uses separate labels, bars and notes:

![Two-class Results](images/ml-panel-layout/results-two-class.png)

Scrolling a short Results window reveals the footer and complete class cards within the frame:

![Short Results scrolled to bottom](images/ml-panel-layout/results-short-bottom.png)
