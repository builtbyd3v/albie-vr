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

## Part menu-5.2: the menu and the look foundation

Done in `unity/Assets/_Forge/OS/` (Unity 6000.6.7f1, the live editor). Every control is an instance of a Meta UI Set prefab, referenced from the package through `Resources/OSKit.asset`.

### What was built
- `Scripts/OSKit.cs` + `Resources/OSKit.asset`: references to the package's backplate, Primary / Secondary / Borderless / Destructive buttons (the `UnityUIButton` family), the TextTile (button form), the ButtonShelf toggle (tabs), the switch, the Large slider; the Albie theme; the font; 35 icons by friendly name from the 24 px Filled atlas; five sounds from the Interaction SDK samples (press = `BasicPoke_ButtonPress`, release, open = `Object_Reappear_01`, close = `UIRelease_01`, error = `Locomotion_Declined`); `unitMeters = 0.00065`. Nothing is copied except the theme.
- `Themes/AlbieDark.asset`: a copy of `UIThemeQuest_Dark` (keeps the package's animators, materials and font), tuned in two places: `backplateColor` a touch cooler, `textPrimaryColor` fully opaque (the package's 90 % white read thin). Secondary text alpha 0.72. No per-tab accent: the one accent is the white Primary pill.
- `Scripts/OSUi.cs`: `Icon`, `Body(panel)`, `Text`, `Button`, `Toggle`, `Slider`, `Row`, `Column`, `Section`, `Clear`, `Confirm` per the contract, plus `Tab`, `Tile`, `Select`, `Size`, `OutlineSprite`. Handlers go through `Button.onClick`; every control adds the press sound. `Scripts/OSBody.cs` is the window body root (scale 0.65 under a ForgePanel's Content, resizes with the panel, adds a `UIThemeManager` with the Albie theme and applies it once after controls are added). `Scripts/OSToggleSync.cs` keeps a UI Set toggle's lit look right (see decisions). `Scripts/OSTileView.cs` is one pooled tile. `Scripts/OSSounds.cs` plays the kit clips through one `AudioSource` on the shell (silent with no shell, kit or clip).
- `Scripts/OSMenu.cs` + `Prefabs/OSMenu.prefab` (nested inside `Resources/OSShell.prefab`): the UI Set backplate (poke, ray, mouse as shipped, a `UIThemeManager` with the Albie theme), a tab bar (Tools, Projects, Artifacts as a toggle group, paging, Close X), a 4 x 2 tile grid from `OSRegistry.Fill`, the empty-state sentence, and the strip (Build something primary, Agents, Settings, Bring windows here, Close). The contents are built from the kit on the first open; tiles are pooled (two pools: plain and outline). It refreshes when the menu opens, the tab changes, and on `OSRegistry.Changed` and `OSShell.Windows.Changed`.
- Placement and motion: world-locked where summoned (`menuDistance` from the eyes, `menuDropDegrees` below eye level, facing the eyes), grows from `SummonPoint` over 220 ms (scale 0.3 to 1, ease-out cubic, fade), fades out in 120 ms, open and close sounds. No `Update` in the menu.
- `Scripts/OSTableButton.cs`: a UI Set backplate disc with an icon-only Primary button, lying flat on the desk frame at the dock's centre (local `(0, 0.012, min(0.09, depth/2))`, rotated like the dock), shown while `AtDesk && !Held && tableButton`, repositioned on `presence.frameChanged`, toggles the menu with `OSSummon.TableButton` and its own position.
- `Editor/OSPrefabBuilder.cs`: builds theme, kit, menu prefab and the shell prefab (adds the menu, the table button, an `AudioSource`). `Editor/OSCheck.cs`: seven new checks. `Editor/OSMenuTest.cs` (`Forge/OS/Run Menu Test`, `OSMenuTest.Run()`, `LastResult`, `Measurements`; `RunAndStay()` / `Stop()` for looking around by hand): the Play test below. `OS/README.md` (the icon table only) and `OS/THIRD_PARTY_NOTICES.md`.

### Checks (verbatim)
Compile: `"errors": 0, "warnings": 0`; no warning or error from `_Forge/OS` in the console after the Play runs.

`Forge.OS.Editor.OSCheck.Run()`:
```
PASS os check
  ok   the OSShell prefab has the shell, the windows and the keys
  ok   os.json parses and its four tools have handlers (4 tools)
  ok   the tools answer an error, not an exception, with no shell
  ok   the registry fills, finds, replaces and orders
  ok   every tab has an empty-state sentence
  ok   settings round-trip, clamp, and survive a damaged file (file 211 bytes)
  ok   the kit asset loads and every UI Set reference resolves (9 prefabs, theme, font, 5 clips)
  ok   every friendly icon name resolves to the Filled atlas, and the builder maps them all (35 icons)
  ok   the Albie theme is derived from the UI Set dark theme
  ok   the menu prefab and the shell's menu and table button exist
  ok   touch targets and gaps at the OS unit are at least 22 mm and 12 mm (22.1 mm target, 12.4 mm gap)
  ok   OSUi builds each control from the kit and a press runs its handler (4 buttons, icon button, switch, slider, tab, tile, text, row, column, section)
  ok   sounds are silent and safe with no shell
  ok   the voice rules still pass
```
`GesturePlayTest.Run()` (the menu and table button now live in Main): `[GesturePlayTest] 32 passed, 0 failed`.

`OSMenuTest.Run()` (Main, desktop rig, voice off, throwaway sample entries registered for the run only): `[OSMenuTest] 57 passed, 0 failed`. Every press goes through the EventSystem: a raycast on the real canvases, then pointer enter, down, up and click on what was on top (the test fails if something else is on top). It covers: sit at the desk (`presence.forceSeated` + `DesktopRig.Current.ToggleSeat()`); the table button shows and a pointer press opens the menu (summon TableButton); distance, drop and field of view; tab presses; paging; a tile press runs its action and closes the menu; M opens it; the strip Close and the tab bar's close X close it; Agents with no page registered presses without throwing (one log line `[OS] The 'agents' page isn't available yet.`); the empty tab; then walking away (the table button goes), M, tabs, a tile and Close again away from the desk.

Measurements (`OSMenuTest.Measurements`, first run; only the strip buttons and the table button changed by a few mm afterwards):
```
table button: plate 47 mm, button 34 mm   (grown to 55 / 40 mm after the first screenshot review)
menu: 0.564 m wide, 0.311 m tall, 0.50 m from the eyes, 15.0 degrees below eye level, 58.9 degrees of horizontal view
tile: 122 x 70 mm
gap between tiles: 13.0 mm
Button Build something: 121 x 31 mm
Button Agents: 79 x 31 mm
Button Settings: 84 x 31 mm
Button Bring windows here: 138 x 31 mm
Button Close: 73 x 31 mm
Tab Tools: 98 x 42 mm
```
The close X and the paging buttons are 56 x 56 units = 36 mm.

Screenshots (in `unity/Logs/os/`, ignored by git), each opened and graded against spec 6.3:
- `00_table_button.png`: the white round button with a grid icon at the front-edge centre of the desk, inside the old dock's frame (expected until the dock is retired). Reads as a button; small from the seat because it is 55 mm.
- `01_menu_desk_tools.png`, `02_menu_desk_projects.png` (a "New" section with an outline tile), `03_menu_desk_artifacts.png` (nine entries: page 1 of 2 with the page controls), `04_menu_desk_artifacts_page2.png` (one tile; a long title ends in an ellipsis), `05_menu_desk_empty.png` ("No artifacts yet. Tap Build something or ask Albie."), `06_menu_away.png` (Tools, away from the desk, seen from off-axis).
- Grades: polish pass (Meta's backplate, tiles, pills and shelf tabs, one radius and type scale); hierarchy pass (the white Build something is the only primary, the lit tab and the grid read at a glance); legibility pass (labels 16, titles 18, status 14 units; white on dark about 10:1; the lit tab's and Primary's dark labels are bold because dark-on-white SDF text reads thin); touch targets pass (numbers above; hover and press come from the UI Set animators, which a still can't show); honest content pass (sample entries exist only in the test, shipped empty states say what to do, no brand names); comfort pass (58.9 degrees, nothing clipped). Integration with the older windows was not graded (this part leaves them alone).
- Fixes the screenshots forced: tile and tab labels were invisible (the shelf toggle ships its main label off and a small italic one on; the tile's text boxes were too short and silently truncated); the strip's last two buttons overlapped; the lit tab stayed half-dark; the pet's greeting START pill sat over the menu (the test now ends the greeting quietly); the build icon (`concepts`) looked like a logo and became `add-circle`; selected-tab and Primary labels were thin.

### Decisions (one line of why each)
- **Font: keep the UI Set's LiberationSans SDF (the theme's font), not Inter.** Why: it is the font the UI Set's materials and animators were built and tested with, it has 250 glyphs (accents in names) where `ForgeLook/Inter SDF` is a static atlas of 115 glyphs, and using Inter would need fallback assets. The old ForgePanel headers stay Inter; a window's own text is LiberationSans.
- **Icons: only the Filled 24 px atlas, one name table (`IconMap` in the builder, copied in `README.md`).** Why: one style, and `OSCheck` fails if a name stops resolving.
- **The menu's contents are built from the kit on first open, not baked into the prefab.** Why: baking 30 nested instances with overrides is fragile; the prefab holds the backplate, theme and wiring, and the kit is the single source of the look.
- **The tab bar uses the ButtonShelf toggle (icon over label), 150 x 64 units.** Why: it is the UI Set's tab-like toggle with a lit state.
- **Toggles get `OSToggleSync`.** Why: the UI Set only moves a toggle's "Selected Layer" when the value changes while running; a toggle built on, set from code or re-enabled looked half lit. The component applies the state a frame after start and snaps it after the UI Set's 0.2 s transition.
- **The outline ("new") tile is a Borderless-animated TextTile with a procedural rounded outline sprite** (a 64 px texture made once, 9-sliced). Why: the UI Set has no outline tile; the outline is not a Meta asset and uses no file.
- **Section headings: a heading starts a new row, a page holds two rows and at most one heading, and a heading repeats at the top of a page its section carries onto.** Why: keeps the plate a fixed size.
- **A tile press clears the EventSystem selection, and the menu clears it on close.** Why: a pressed UI Set control keeps its "selected" highlight otherwise.
- **The strip's Agents, Settings and Build something close the menu only if their page is registered.** Why: with nothing registered the button should press, log one line, and leave the menu where it is. Bring windows here closes the menu after the windows move.
- **The backplate's own poke-sound wrapper is switched off on the menu and the table button.** Why: every control plays the press sound itself; the wrapper would double it.
- **The table button is a Primary (white) circle on a dark backplate disc.** Why: dark-on-dark vanished next to the dock.
- **The menu plate is 868 x 478 units (0.564 x 0.311 m).** Why: four 188-unit tiles with 20-unit gaps; 58.9 degrees at 0.5 m is inside the 70 degrees limit.
- **No Project Settings change, no tags** (all 21 `QDSUI*` tags already exist).

### Changes outside `OS/`
None. `git status` shows no `.unity` file modified; `Main.unity` untouched.

### Not done / not working
- Real hand poke and ray on the menu were not exercised (no headset): the backplate is the package's own, its poke surface's `BoundsClipper` was read in Play and matches the plate (868 x 478 units), and every press in the test is a mouse-style EventSystem press. Hover lift and press depress come from the UI Set animators and were not captured in stills.
- The 220 ms grow-in and 120 ms fade-out were not captured mid-way; the end states are what the screenshots show.
- Menu entries are empty until the Tools, Projects and Artifacts parts register theirs; the screenshots use test-only sample entries.
- `OSTableButton` and the old dock both show until the dock is retired (next part).
- On the real first arrival the pet's greeting holds the desk (`DockHeld`), so the table button stays hidden until START is pressed, as the contract says. The test ends the greeting itself.
- `OSKit` caches its icon lookup per load; a rebuilt asset is picked up on the next Play or reload.

## Part windows-5.3: window manager, modes with and without a desk, the dock retired

### What was built
- **`OSWindows` / `OSWindow` (placement replaced, API kept).** A window is a free root `ForgePanel` with a `PanelManipulator` (`Init(head, desk frame)`), a `GestureWindow` (so the flick in, the flick out and the minimise dot work) and a UI Set close button (48 units = 31 mm) at the right end of its header; the panel's own Face me / Straight pills and the title make room for it. Opening animates scale 0.92 to 1 and fade over 200 ms (`Busy` until it lands); closing shrinks and fades over 180 ms, then destroys. `Adopt` is as before, and a group entry (no object) tracks the open Workspace mode.
- **Placement is in angles seen from the eyes.** Each candidate place is a yaw (3 degree steps out to 110) on one of four rows (0.8 m at a desk / 0.95 m away, a little below eye level; the same row raised 0.42 m; and both again 0.25 m further out, at a 15 degree cost). The window takes the free place nearest to straight ahead. "Free" means clear (with a 2.5 degree gap) of every other `ForgePanel` and `ForgeVolume` in the scene, of the open mode's laid-out slots (`Workspace.GetSlots`, so a mode that is still unfolding counts where it will be), of windows that are still gliding (their landing place), and of the room's walls (`RoomFit`, in a yaw-only frame at the user's head; a place the walls would move is only used if none is clear of them, and only if it is still clear after the move). At a desk the window's bottom stays above the desk top. If the view is full the window goes straight ahead on the second ring rather than not at all.
- **Nothing moves by itself.** Walking away or up to a desk moves no window. When a Workspace mode opens where windows already are, only the windows its panels would cover move aside (`ClearModeSlots`).
- **`BringHere()`** lays the open mode out again around the user (`Workspace.Reanchor`), then takes a free place for each window in the order they were opened, each gliding there over 550 ms (`ForgeTween.SpringDamped`), staggered 50 ms. The menu's Bring windows here button and the `os_windows recenter` tool already called it; `CloseAll()` closes windows, runs the adopted ones' close actions and closes the open mode (`os_windows close_all`).
- **`Workspace` hook** (`Core/Scripts/Workspace.cs`, nothing changes until the OS sets it): `FreeFrame` (a delegate giving the pose of a virtual desk frame), `KeepOpenAway`, `DockRetired`, `CanOpen`, `InFreeFrame`, `Reanchor()`, `GetSlots()`, `IsOpenItem()`. Inside `Workspace`, the frame, its width and depth and the eye now come from an "active frame": the desk's, as ever, unless the open mode is in a virtual one. Away from a desk `Open` makes a frame in front of the user whose eye relation is a seated user's (eyes 0.5 m above and 0.5 m behind its front edge), so `LayOut` and `ClampEye` run unchanged. On Away with `KeepOpenAway` the open mode is moved onto a pinned copy of the desk frame (same pose, so nothing moves) instead of folding, so it doesn't jump if the desk frame later goes to another table. `LayoutEye`, `TopEdge` and `BesidePose` (used by the desk extras) return desk-frame values even when the mode is in a virtual frame.
- **`OSModes`** (`Open`, `IsOpen`, `Close`, `Title`, `ModeIds`) plus `Attach` / `Detach`, called from `OSShell.OnEnable` / `OnDisable`: they set the hook, and apply `OSSettings.flatLayout` to `Workspace.SetLayout` now and on every settings change. The open mode is one group entry in `OSWindows` (id `mode:<id>`), kept in step with `Workspace.modeChanged`, so a mode opened by the menu, a voice tool, the pet or another module shows up and replaces the old entry.
- **Dock retired.** With the OS the dock never shows (`DockRetired`); hold and release still run (`DockHeld`), so the table button stays hidden through the greeting and appears on release. The whistle and the crew egg fall back to their desk corners (seen in `21_arrival_after_start.png`); no error from `AlbieMrukAdapter`.
- **`OSCheck`** gained two edit-mode checks (the Workspace hook is inert until set, in a preview scene; `OSModes` is quiet with no scene) and **`OSWindowsTest`** (`Forge/OS/Run Windows Test`, Play in Main) was added.

### Checks (verbatim)
`Forge.OS.Editor.OSCheck.Run()`:
```
PASS os check
  ok   the OSShell prefab has the shell, the windows and the keys
  ok   os.json parses and its four tools have handlers (4 tools)
  ok   the tools answer an error, not an exception, with no shell
  ok   the registry fills, finds, replaces and orders
  ok   every tab has an empty-state sentence
  ok   settings round-trip, clamp, and survive a damaged file (file 211 bytes)
  ok   the kit asset loads and every UI Set reference resolves (9 prefabs, theme, font, 5 clips)
  ok   every friendly icon name resolves to the Filled atlas, and the builder maps them all (35 icons)
  ok   the Albie theme is derived from the UI Set dark theme
  ok   the menu prefab and the shell's menu and table button exist
  ok   touch targets and gaps at the OS unit are at least 22 mm and 12 mm (22.1 mm target, 12.4 mm gap)
  ok   OSUi builds each control from the kit and a press runs its handler (4 buttons, icon button, switch, slider, tab, tile, text, row, column, section)
  ok   sounds are silent and safe with no shell
  ok   the voice rules still pass
  ok   the workspace hook is inert until the OS sets it, and the OS names resolve
  ok   OSModes is quiet with no scene and the OS window pieces are in place
```
`OSWindowsTest.Run()` (Main, desktop rig, voice off): `[OSWindowsTest] 58 passed, 0 failed`. In order: the OS hooked the workspace and the dock isn't showing; the first arrival (the greeting holds the desk, the table button and the dock stay hidden, START opens the code view and releases the desk, the table button then shows, the open code view is a window group); at the desk (`forceSeated` + `ToggleSeat`) two OS windows and CAD open at once, three entries in the list, handles set and not Busy, free root objects, no overlap measured by the corners of each panel seen from the eyes; the close button (31 x 31 mm) is pressed through the EventSystem and closes its window; a flick away minimises a window into a dot; forced Away: all three are still there and active, at the same world positions (0.000 m), the dot stays, no dock and no table button; the dot's window comes back when opened; Bring here: every window and CAD panel within 1.3 m and within 100 degrees of where the user faces, window 1 moved 4.5 m, nothing overlaps; `OSModes.Open("ml")` away: ML replaces CAD as the mode and as the group entry, its panels are around the user and not at the old desk; `ui_open_view` returns ok for messages and cad away from the desk (no `not_at_desk`); `os_windows recenter` and `close_all` return ok; the flat layout setting reaches the workspace and back; Close all leaves no windows, no mode and no panels; at the desk again a mode opens in the desk frame; no error or exception was logged during the run.
```
at the desk: window 1 0.80 m / 86 deg, window 2 0.90 m / 48 deg from the eyes
close button: 31 x 31 mm
after Bring here: farthest 1.04 m, widest 87 deg from where the user faces (mode panels: 3)
the room is known at the user's place (walls are fitted against): True
ML away: 4 panels, farthest 0.80 m from the head
```
Existing checks, unchanged expectations: `[GestureSelfTest] 104 passed, 0 failed`; `[GesturePlayTest] 32 passed, 0 failed`; `[ML Main smoke] PASS: saved Main scene, ML desk, flat/curved layout, ml.layout aliases, seven VizCard formats, scatter selection, regression row detail and restoration.`

Screenshots (`unity/Logs/os/`, ignored by git), each opened:
- `20_arrival_greeting.png`, `21_arrival_after_start.png`: the first arrival at the desk. After START the code view is open, the small round table button sits at the front-edge centre, the whistle (orange puck) is on the right and the crew egg on the left; nothing overlaps and there is no dock.
- `10_desk_three.png`: CAD's drawing, versions and parameters panels around the desk, the rocket on the desk; the two OS windows are out of this narrow desk view, to the sides.
- `11_close_button.png`: a window with the TEST badge, title, the close X at the right end of its header, a text and a Primary button.
- `12_away_same_place.png`: from 2 m away, CAD still open at the desk with the same panels, and the two windows (one a minimised dot) where they were.
- `13_after_bring_here.png`: after Bring here, CAD's panels and a window around the user, none overlapping.
- `14_ml_away.png`: away from the desk, ML's training, dataset and results panels in front of the user, not at the old desk.

### Decisions (one line of why each)
- **Workspace modes stay Workspace modes (contract decisions 1 to 3).** Why: eleven call sites depend on `ActiveMode`; the OS only gives the workspace a way to open in a virtual frame.
- **The workspace creates and owns the virtual frame; the OS only supplies its pose (`FreeFrame` delegate).** Why: the workspace parents and animates the items, so it must also create, replace and later destroy the frame (2.5 s after a close, so folding items finish).
- **At a desk nothing changes: the mode lives on the desk's own frame.** Why: desk behaviour stays exactly as it was; the virtual frame exists only away from a desk or after leaving one.
- **Away with `KeepOpenAway` the mode moves onto a pinned copy of the desk frame instead of staying on the desk frame.** Why: the desk frame is rewritten when the user later uses another table, which would have moved the items.
- **The group entry follows `Workspace.modeChanged`, and a replaced entry is detached, not closed.** Why: any opener works, and closing the old entry would close the new mode.
- **Windows keep out of the walls by `RoomFit`, but a place clear of the walls is preferred to one the walls move.** Why: fitting each candidate moved several windows onto the same spot.
- **Yaw widths use the horizontal distance.** Why: a window high above the eyes covers more yaw than its slant distance says; a first version let two windows touch.
- **A mode that opens over windows moves only the windows it covers.** Why: the done list wants none overlapping whichever opens first, and unrelated windows shouldn't move.
- **`GestureWindow.StaysAway`, also true for every window while `KeepOpenAway`.** Why: its dot was dropped on Away, which left an OS window hidden for ever.
- **`Workspace.CanOpen` replaces the desk check in `WorkspaceRemote`, `CodeTools.OpenView` and `SlackRemote.Show`.** Why: the same rule everywhere; without the OS it is exactly `AtDesk`.
- **The test measures overlap by each panel's corners seen from the eyes.** Why: it is what a user sees, and independent of the placement code's own estimate.

### Changes outside `OS/`
- `Core/Scripts/Workspace.cs`: the OS hook above (properties, `Reanchor`, `GetSlots`, `IsOpenItem`, the active frame, Away keeps the mode when asked, dock retired). Desk behaviour is unchanged when the hook is not set (`GesturePlayTest` and `Validate Main` pass with the same expectations).
- `Core/Scripts/WorkspaceRemote.cs`, `Code/Scripts/CodeTools.cs`, `Messages/Scripts/SlackRemote.cs`: the `AtDesk` guard became `Workspace.CanOpen` (one line each).
- `Core/Scripts/Gestures/GestureWindow.cs`: `StaysAway` (6 lines).
No Project Settings, no scene, no `manifest.json`.

### Other users of the desk frame (looked at, what was left)
- Fixed: `WorkspaceExtras` (desk-frame values from `LayoutEye`, `TopEdge`, `BesidePose` now convert when the mode is in a virtual frame); `WorkspaceRemote`, `CodeTools`, `SlackRemote` (guards).
- Left, desk-only on purpose: the desk extras (timers, notes: hidden away, as before); `DigestTrigger` and `DraftPresenter` (spawn on the desk); `ArrivalDirector.OpenAfterDock` (arrival is a desk event); `ArrivalGreeting`, `DesktopRig.ToggleSeat`, `AlbieMrukAdapter` (desk nav mesh; the dock keep-out never applies with the dock retired).
- Left, not checked: the pet walking to a mode's panel (`AlbieController` work target via `TryGetItemTransform`) when the mode is in a virtual frame away from a desk; a minimised dot of a Workspace panel stays parented to the desk frame when the mode is pinned.
- Not affected: `CodeMode`, `CadViewer`, `MessagesLayout`, `MentionsPanel` (they use mode items or `modeChanged` only).

### Not done / not working
- With a wide mode open (CAD) a third panel goes beside it, at the edge of view (86 degrees from straight ahead for one window at the seat in the test). The raised row is blocked by the mode's tall side panels. Bring here and the next open pick the nearest free place again.
- The Interaction SDK ray and poke on the window close button and handles were not exercised (no headset); the mouse-style EventSystem press was.
- The first-arrival greeting did not come up in one of the runs of the test (the arrival checks are then skipped with a log line); it did in the verified run. Cause not found.
- `OSCheck.Run()` marks the open scene as changed (an older check creates scene objects); running any Play test reloads it clean.
- The open and close animations were not captured mid-way.
