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

## Part core-5.1: shell, bootstrap, tools, keys and a first window manager

Done in `unity/Assets/_Forge/OS/` (namespace `Forge.OS`, no asmdef). Everything below was run in the live editor (Unity 6000.6.7f1).

### What was built
- `Scripts/OSBootstrap.cs`: AfterSceneLoad + sceneLoaded; skips scenes with no `Workspace`; loads `Resources/OSShell`; sets `shell.workspace` while the instance sits under an inactive holder (the `DeskBootstrap` trick), then moves it to the scene root. The shell is a root object of the workspace's scene.
- `Scripts/OSShell.cs`, `OSEntry.cs` (also holds `OSTab` and `OSSummon`), `OSRegistry.cs`, `OSSettings.cs` per the contract. `OSShell` holds state and raises `MenuChanged` / `TabChanged`; it needs no menu view. `OpenItem` sends tool, project and artifact to `OSRegistry.Find(...).open` and system to a handler table filled by `OSShell.RegisterSystem(id, open)`. An unregistered system id gives "The 'agents' page isn't available yet." (and "There is no system page called ..." for an unknown one).
- `Scripts/OSWindows.cs`, `OSWindow.cs`: `Open` (a `ForgePanel` from `Workspace.Current.panelPrefab` at the scene root, `PanelManipulator.Init(head, desk frame or head)`, GraphicRaycaster ensured, same id again brings the existing window forward), `Adopt` (a root, or null for a group such as a Workspace mode), `Find`, `IsOpen`, `Close`, `CloseAll`, `All`, `Count`, `Changed`, `BringHere`. Placement is only in `PlaceNew` (nearest free spot in front of the user) and `PlaceRow` (`BringHere`: one row, edge to edge, centred; the distance grows from 0.95 m to 1.8 m if the row would be wider than 150 degrees).
- `Scripts/OSTools.cs` + `Resources/AlbieTools/os.json`: `os_menu{open}`, `os_tab{tab}`, `os_open{kind,id}`, `os_windows{action}`; registered at BeforeSceneLoad; with no shell each answers `{"error":"The Albie menu isn't available in this scene."}`.
- `Scripts/OSKeys.cs`: M (no Shift) toggles the menu through the Input System; the left controller menu button (`OVRInput.Button.Start`, LTouch) too. It is the same one-line `OVRInput.GetDown` that `DeskDock` already uses every frame, so it is safe on the desktop rig. A key typed into a TMP input field is ignored.
- `Editor/OSPrefabBuilder.cs` (`Forge/OS/Build OS Prefabs`, `Build()`; builds `Resources/OSShell.prefab` with OSShell, OSWindows, OSKeys; safe to re-run, `Ensure<T>` helper for later parts), `Editor/OSCheck.cs` (`Forge/OS/Check OS`, `Run()`, `RunBatch()`), and two helpers for play tests: `Editor/OSShot.cs` (head-camera screenshot to `unity/Logs/os/NAME.png`) and `Editor/OSInput.cs` (`OSInput.Tap(keys)`: a keyboard state event on a virtual keyboard, queued from inside the player loop; `Busy`).

### Checks (verbatim)
Compile: `unity recompile` gave `"errors": 0, "warnings": 0`; the console buffer holds no warning from `_Forge/OS` (baseline warnings are the old CS0618 ones).

`Forge.OS.Editor.OSCheck.Run()`:
```
PASS os check
  ok   the OSShell prefab has the shell, the windows and the keys
  ok   os.json parses and its four tools have handlers (4 tools)
  ok   the tools answer an error, not an exception, with no shell
  ok   the registry fills, finds, replaces and orders
  ok   every tab has an empty-state sentence
  ok   settings round-trip, clamp, and survive a damaged file (file 211 bytes)
  ok   the voice rules still pass
```
`VoiceRulesCheck.Run()` (after the OS rules were added): `PASS voice rules check` / `ok   28 tool names and prefixes in the rules, all real (85 tools on the headset); every demo beat is routed`.
`GestureSelfTest.RunMenu()`: `[GestureSelfTest] 104 passed, 0 failed`. Also run, as a bonus: `GesturePlayTest.Run()` gave `[GesturePlayTest] 32 passed, 0 failed` (same as baseline).

Play mode, Main, desktop rig, voice off (`Forge.ForceDesktop` and `Albie.VoiceOffInEditor` set true, restored to false afterwards):
- `OSShell.Current != null`, `FindObjectsByType<OSShell>(Include).Length == 1`; console line `[OS] Albie OS shell added to Main`; no error and no warning from OS code (only the old Ellipsis / MRUK / WakeListener noise).
- A real M key: `OSInput.Tap(Key.M)` then `OSShell.Current.MenuOpen`: `after M: menu=True summon=Key`; Shift+M: `after Shift+M: menu=True` (unchanged); M again: `after second M: menu=False`.
- `AlbieTools.Call("os_menu", {open:true})` -> `{"ok":true,"open":true}`, summon Voice; no `open` toggles it back (`{"ok":true,"open":false}`). `os_tab` -> `{"ok":true,"tab":"artifacts","open":true}`; bad tab -> `{"error":"The tabs are tools, projects and artifacts."}`; `os_open tool cad` -> `{"error":"There is no tool called 'cad' in the menu."}` (no tools are registered yet; later parts add them); `os_open system settings` -> `{"error":"The 'settings' page isn't available yet."}`; `os_windows close_all` -> `{"ok":true,"windows":0}`.
- Two windows: `shells=1 w1=True w2=True samePanelOnReopen=True count=2`, each `active=True handles=True raycaster=True parent=root dist=0.95`. After `BringHere` they sit at yaw -16.2 and +16.2 degrees, side by side. Screenshot `unity/Logs/os/04_windows_final.png`: two dark panels "First window" / "Window A" and "Second window" / "Window B" side by side in front of the camera, not overlapping, text readable. (`01_two_windows.png` is the first try, where the gesture display hid window B: it showed that placement must see every panel, fixed.) Also run live: `Adopt` of a group and of the gesture display panel, `BringHere` with three windows (yaw -38.5, -6.0, 32.5 for widths 0.50, 0.50, 0.72 m, gaps 3 degrees), `Close` of a group (action ran, `Closed` fired once, count dropped at once), `Close` of an opened window (gone from `All` in the same frame, GameObject gone), `os_windows close_all`.
- Sandbox: opened `Artifacts/ArtifactsSandbox.unity`, Play: `scene=ArtifactsSandbox workspace=False OSShell.Current=False shellsInScene=0 osToolsRegistered=True osMenuToolResult={"error":"The Albie menu isn't available in this scene."} artifactHost=True`; 10 log lines, no warning, no error. Back to Main, not saved (`dirty=False`).

### Decisions (one line of why each)
- **ViewNotes: no entry added.** `ViewNotes` is keyed by Workspace mode and fires only on `workspace.mode`, which the hub also reads (it gates the crew tools on it); the menu is not a mode, so a note would never fire. ToolRules and Keyterms got the OS lines ("Albie menu", "projects", "artifacts").
- **`os_open` is preferred over `ui_open_view` in the rules, with `ui_open_view` as the fallback** if `os_open` has no such tool. Why: until the Tools are registered (a later part) "open CAD" should still work at the desk.
- **`os_tab` also opens the menu if it is closed.** Why: "show my projects" by voice means "show me", and the tab alone changes nothing visible.
- **`OSShell.OpenItem` leaves the menu as it is.** Why: whether a tap closes the menu is the menu view's call (next part).
- **Added `OSShell.LastSummon` / `SummonPoint`, `OSShell.SystemIds`, `OSShell.HasSystem`, `OSShell.FindHead()`, `OSRegistry.UnregisterTool/UnregisterSource`, `OSSettings.SetPathForTests`, `OSTools.Names/Register` (public).** Why: the menu view needs the summon, the check needs to clean up after itself and to point settings at a temp file, and the editor assembly can only call public members.
- **Panel width for placement is `SizeMeters.x`, not times the scale.** Why: the canvas root carries the 0.001 scale, so `SizeMeters` already is metres (my first version multiplied again and put windows on top of each other).
- **`Open` of an id that is already in view (within 40 degrees, 0.4 to 1.6 m) leaves it where it is**; out of view it moves it to the nearest free spot. Why: "bring forward" should not shuffle a window the user is looking at.
- **Placement avoids every `ForgePanel` in the scene, not only OS windows.** Why: the gesture display and desk windows were being covered.
- **`Close` removes a window from the lists immediately** and deactivates it, then destroys it. Why: `Destroy` only lands at the end of the frame, so `Count` right after `close_all` was stale.
- **Desktop key test needs a virtual keyboard and a player-loop queue (`OSInput`).** Why: with the editor window unfocused the real keyboard drops queued events, and an event queued from editor code can be applied in an editor update, where `wasPressedThisFrame` never sees it.
- **No close button yet** (the task says a later part adds it).

### Changes outside `OS/`
- `Core/Scripts/AlbieVoice.cs`: one added sentence in the `ToolRules` const (the OS tools and "open CAD", "bring my windows here", "close everything") and three added `Keyterms` ("Albie menu", "projects", "artifacts"). Still a `const string ToolRules`; `VoiceRulesCheck` passes.
- `CAD/Scripts/CadViewer.cs`: "measure" moves from M to Shift+M (`keys.mKey.wasPressedThisFrame && keys.shiftKey.isPressed`), because M is now the menu key. Comment lines in `CadViewer.cs` and `CadSandboxKeys.cs` and two lines of `CAD/README.md` say Shift+M. There is no on-screen hint for it (grep of `_Forge` for "M measure", "key M" etc. finds only those comments and the README).
- No Project Settings, no manifest, no `.unity` change. `Main.unity` clean (`git status`).

### Not done / not working
- No menu view, no close button, no table button: later parts.
- `os_open` has nothing to open yet (no tool, project or artifact registers an entry in this part).
- Left controller menu button: coded, not tested (no headset, no controller in the desktop rig).
- `Resources/OSShell.prefab` is the only prefab; no `README.md` or `THIRD_PARTY_NOTICES.md` yet.
