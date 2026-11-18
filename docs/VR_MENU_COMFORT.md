# VR menu comfort fix

The floating menu shortcut keeps its position and rotation in the room. Turning the head, leaning, and walking
do not re-anchor it. It appears again at a fresh anchor after being hidden, or changes side when explicitly
requested in Settings. The desk button stays attached to the desk.

While a visible task is open, the floating shortcut is hidden. Hidden and minimised adopted panels do not
count as visible work. Opening or selecting a task dismisses Tools. Explicitly summoning Tools searches for
a clear angle beside or below the workspace at the user's configured distance; it checks panels and models
behind the menu as well as objects in front. It no longer solves overlap by moving the menu closer to the face.

Bringing out the RC transmitter dismisses an existing menu and hides the floating and wrist shortcuts.
The controller menu button, desktop M key, and voice can still explicitly summon Tools during RC use.
Reading a gesture tile's status can adopt its existing panel without dismissing the menu.

## Verification — 2026-11-18

The PR is based on main `4ffb042` (PR #136). It retains main's 0.42–0.6 m reach limits,
20-degree default drop, desk clearance, Spaces UI, and long-press controls. Automatic following in main's
Tools window and corner launcher is removed.

- **68 focused backend tests passed**, including dependency installation, real npm tests/build scripts,
  publication confirmation, local Git pushes, and mocked GitHub API responses.
- **Android-target C# compilation passed** in Unity 6000.6.7f1 in an isolated checkout of the merged source.
- **OSCornerTest: 67 passed, 0 failed** in Main with scratch settings. Checks cover small and large head turns,
  translation, fixed rotation, task dismissal and overlap, unchanged task positions, settings, RC dismissal and
  explicit reopening, shortcut restoration, and object cleanup. Batch mode explicitly creates the RC transmitter
  because its normal automatic installer skips batch mode.
- **OSMenuTest: 85 passed, 1 failed** in a windowed Android-target editor. Menu interactions, Spaces, reach and
  room anchoring passed. The remaining rendered-label contrast assertion measured 4.7:1 against its 5.5:1 threshold;
  label styling is unchanged by this PR. Batch-mode keyboard failures were excluded from this windowed result.
- The Node/npm runtime additions were also exercised as uid 1000 in a runtime-only Linux container, including
  a clean package install, two real login-field validation tests, and the package build script without network access.
- A real provider attempt previously stopped before file generation because the configured local Anthropic key
  was rejected. Real agent-generated login-page behavior and live GitHub publication remain unverified.
- Test preferences and files were restored. Validation output is local under `unity/Logs/`, not committed.

No Quest was connected and no APK was packaged. Merge the PR, update the build checkout to main, and make a
new headset build. Cloud web builds also need the Node/npm image deployed by the existing HF deployment workflow.

## Headset check

1. Away from the desk with no task open, turn the head left/right, look down, and lean: the shortcut stays in the room.
2. Open a task: Tools closes and the floating shortcut disappears. Turn and lean: the task remains where it was placed.
3. Summon Tools explicitly: it leaves the task visible and stays in place when the head turns.
4. Bring out the RC transmitter with Tools open: Tools closes and passive shortcuts stay hidden while driving/flying.
5. Put the transmitter away and close the task: the shortcut returns. At the desk, use the fixed desk button.
