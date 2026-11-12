# Albie OS run log

Run of `docs/ALBIE_OS_SPEC.md`. Orchestrator: Fable. Workers: Sonnet and Haiku through the Workflow tool. Newest entries at the bottom. Times are local (2026-11-12 unless a date is given).

## 0. Setup (§3.1)

- `git fetch`. `origin/main` is at `e69b8db` (PR #62), the commit the spec's integration map was verified on.
- Created `feat/albie-os` from `origin/main`. Removed the auto-set upstream (`origin/main`) so a bare `git push` can never target `main`.
- Merged `origin/chore/remove-movie-rename-albie` (D-41, 11 commits) with `--no-ff`: clean, merge commit `3bb20ef`.
- Disk at start: 21 GB free on `C:`.
- No Unity editor was running. Opened one editor on `albie-vr/unity` with `unity open .`.
- Left untouched (not mine, untracked before the run): `.nodeterm/`, `docs/CONVERSATION_HANDS_3D_RESEARCH.md`, `docs/EXECUTION_PLAN.md`, `docs/OPERATING_MODEL.md`, `unity/Assets/Resources/OVRBuildConfig.asset`, `unity/Assets/StreamingAssets/RuntimeActionBindings.json`, `unity/Assets/_Forge/Pet/Models/`, `unity/RuntimeActionBindings.json`. They are not committed on this branch.

### Decisions
- **Unset the branch upstream.** `git checkout -b … origin/main` made `origin/main` the upstream; with it unset, pushes must name `origin feat/albie-os`. Why: rule "never push to main".

## 1. Baseline after the D-41 merge (§3.1 step 3, §6.2)

Unity 6000.6.7f1 opened on `albie-vr/unity`; the merged code compiled with **0 errors** (48 warnings, all pre-existing: 41 CS0618, 1 CS0252, 6 UAC). No fix was needed for the merge.

Every existing check was run once in the live editor through `unity command eval` (workflow `albie-os-baseline`, one integrator agent). All passed:

| Check | Result (verbatim) |
|---|---|
| Gestures: Run Self-Test | `[GestureSelfTest] 104 passed, 0 failed` |
| Gestures: Run Play Test (Play) | `[GesturePlayTest] 32 passed, 0 failed` |
| Check Artifacts (suite) | `[ArtifactChecks] 97 passed, 0 failed` |
| Artifacts: Runs | `PASS run check` |
| Artifacts: Biology Templates | `PASS biology templates check` |
| Artifacts: glTF and Timelines | `PASS glTF and timeline check` |
| Artifacts: Hoops | `PASS hoop check` |
| Artifacts: Repo Activity | `PASS repo activity check` |
| Artifacts: Repo Connector | `PASS repo connector check` |
| Artifacts: Physics Templates | `PASS physics templates check` |
| Artifacts: Slalom | `PASS slalom check` |
| Artifacts: Voice Rules | `PASS voice rules check` |
| Artifacts: Brain Loop (Play) | `PASS slalom brain check (Play mode, the brain truck on its colliders)` |
| Controllers: Autopilot | `PASS autopilot check` |
| Controllers: Behaviour Cloning | `PASS behaviour cloning check` |
| Controllers: Runner | `PASS controller runner check` |
| Controllers: Five In A Row | `PASS five in a row check (mock truck, Editor): live training worked 5 times in a row (4 on the first candidate)` |
| Controllers: Hot Reload | `PASS hot reload check` |
| Controllers: ML Template | `PASS ML template check` |
| Controllers: Room Slalom | `PASS room slalom check (mock truck, Editor)` |
| Controllers: Thing Runtime | `PASS thing runtime check (example truck on Unity physics, Editor)` |
| Notebook: Check Notebook | `PASS notebook check` |
| ML: Validate Main (Play) | `[ML Main smoke] PASS: saved Main scene, ML desk, flat/curved layout, …` |

Main.unity was not modified. Pre-existing console noise, not from this run: 5 "Scaling mismatch (1.5)" asserts (Windows display scale), no-headset XR errors in Play, a deliberate "approvals.json is damaged" warning from the artifact checks.

### Where the code differs from spec §4 (the code wins)
- **Crew class names:** the code is `AlbieCrew` / `AlbieCrewMember` / `AlbieAgentTypes` (D-41 rename), not `Gator*`.
- **`screen-main` is still there after D-41:** in `Workspace.DefaultModes`, `MainSceneBuilder`, `CodeMode.ItemId`, and Main.unity's `code` and `ml` modes (with a prefab guid that no longer resolves; `CodeMode` loads the code panel from Resources itself). Left alone: not OS work.
- **The M key is not free:** `CadViewer.cs:447` binds M to "measure" whenever a CAD session exists.
- **No hands-free voice toggle exists** in `AlbieVoice` (only the editor-only `Albie.VoiceOffInEditor` pref).
- **`three_body` is a mock fixture, not a template.** `cad_*` and `ml_open` tools exist only in the cloud tool list and fail offline.
- **`ControllerBrainView`** has no creator and nothing in Main shows it (prefab `MLPanels/ControllerBrain`).
- **UI Set canvases are 0.5 mm per unit** (ForgePanel is 1 mm per unit). All 21 `QDSUI*` tags are already in `TagManager.asset`, so no Project Settings change is needed for the theme.
- **Gesture tests return void** and report through the console line, not a PASS string. `SimulatedHandSource` never tracks the left hand.
- **`GestureFeedback` has no sounds** and `_Forge` has no audio files; UI sounds come from the Interaction SDK sample audio.

### Decisions
- **Workspace modes stay Workspace modes (one at a time), opened through a small Workspace hook; everything else is a free OS window (many at once).** Why: eleven call sites in CAD, ML, Messages, Code and the pet depend on `Workspace.ActiveMode`; re-hosting their prefabs as loose windows would break them. The hook gives a virtual desk frame in front of the user when away, and stops the fold on Away.
- **The dock is hidden, not deleted, when the OS is present.** The table button is held back while `Workspace.DockHeld` is true, so the pet's greeting → START → code-mode arrival is unchanged.
- **OS UI unit is the UI Set canvas unit scaled to about 0.65 mm** (UI Set ships at 0.5 mm). Why: 16-unit body text then measures about 10 mm, which meets the Quest 3S legibility line in §6.3, and a 4-column tile menu comes out about 0.52 m wide (inside 70° at 0.5 m). It is one parameter on the kit, so Hudson can tune it.
- **Icons: the UI Set's own 24 px filled icon atlas only.** Why: one consistent set that ships with the SDK; no mixed styles.
- **M stays the menu key; CAD's desktop "measure" shortcut moves to Shift+M.** Why: the spec names M and the Done check uses it; the CAD shortcut is a desktop-only convenience. Listed as a change outside `OS/`.
- **Writers stage files outside `Assets/`** (`.os-run/staging/`, ignored locally) and the single integrator lane moves them in. Why: half-written files from a parallel writer must not break the one editor's compile.
- **At most 3 agents run at once** (the lane plus 2 writers). Why: see the disk note below.

### Disk (§3.3)
Free space on `C:` went from 21 GB at the start to **5.8 GB** after the baseline. Unity's `Library` is unchanged at 13.0 GB. The space went to Windows' page file, which grew to 27 GB because memory commit reached 55 GB of a 58.5 GB limit (34 `claude` processes at 11.7 GB, 4 Unity processes at 6.9 GB, plus browsers and editors from other sessions). The page file does not shrink until a restart. The run continues above the 5 GB line with fewer agents at once; every lane step checks free space first and stops under 5 GB.

### Other notes
- `unity/RuntimeActionBindings.json` and `unity/Assets/StreamingAssets/RuntimeActionBindings.json` (untracked before the run) disappeared when the editor opened; the XR package generates them. Not restored.
- `unity/ProjectSettings/Packages/com.unity.pipeline/EditorPipelineConfig.json` shows as modified with a line-ending-only diff after the baseline agent toggled and restored a CLI setting. Not committed.
