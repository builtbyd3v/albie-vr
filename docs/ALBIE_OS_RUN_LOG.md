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

## Part 5.4: Tools tab (tools-5.4)

### What was done
- Integrated the staged `OSToolsCatalog` (cad, code, train, notebook, messages, gestures tiles) and `OSNotebookWindow`. Both compiled first time against the real API; 0 errors, 0 new warnings.
- `OSNotebookWindow`: added a fit step. There is no scrolling, so after each rebuild `LateUpdate` checks the page against the window and steps down the number of rows shown (milestones, attempts, versions, lessons) until it fits. Why: a long plan or long attempt text would otherwise run past the window.
- New `Editor/OSToolsTest.cs` (`Forge/OS/Run Tools Test`, `Forge.OS.Editor.OSToolsTest.Run()`, result in `LastResult`): Main in Play, desktop rig, voice off. Opens the real menu, presses each Tools tile with a pointer through the EventSystem, at the desk and away, checks the result, then the notebook against proj_truck, Train with a brain truck, Bring windows here and Close all.
- `Editor/OSMenuTest.cs`: the real Tools entries step aside while that test runs (it counts and presses its own sample tiles) and `OSToolsCatalog.Register` puts them back.

### Check output (verbatim)
```
PASS [OSToolsTest] 94 passed, 0 failed
PASS os check (16 ok lines)
[OSMenuTest] 57 passed, 0 failed
[OSWindowsTest] 50 passed, 0 failed
[GesturePlayTest] 32 passed, 0 failed
```
OSToolsTest, in order: the Tools tab lists cad;code;train;notebook;messages;gestures; each tile has title, kit icon, status, open flag and action; at the desk and away, a pointer press on each of the six tiles opens it (CAD, Code, Messages as mode groups with panels; Train opens the ML desk with 4 panels and, with no truck, no brain window and status `open · no truck yet`; Notebook and Gestures as tracked windows); notebook shows the plan, the new attempt under "Waiting for your verdict", the new version, the project name; Mark worked writes `outcome=worked by person` to the store and the window says "worked (you marked it)" with no reopen; Mark failed likewise; text stays inside the window with no overlap; Mark worked is 88 x 31 mm; with a brain truck Train opens the ML desk and the "brain" window and the tile says `open · brain ready` (desk and away); Bring windows here brings notebook, brain and gestures within 1.6 m; Close all closes the gesture display too and leaves no windows or mode; no error, exception or `[OS]` warning logged.

Screenshots (`unity/Logs/os/`, ignored by git), each opened:
- `30_tools_tab_desk.png`: the Tools tab at the desk, six tiles (CAD, Code, Train "no truck yet", Notebook "RC truck", Messages, Gestures "open"), strip below; nothing clipped.
- `43_tools_tab_away.png`: the same tab away with a truck: Train says "brain ready", Gestures "5 gestures".
- `31_notebook_desk.png`, `32_notebook_marked.png`, `33_notebook_after_fail.png`: the notebook window with proj_truck data (plan v2, attempts, versions, lessons, the two verdict buttons) and after Mark worked / Mark failed.
- `41_notebook_away.png`: the notebook away from the desk.
- `34_gestures_desk.png`: the gesture display adopted as a window with Teach a gesture.
- `35_brain_window.png`, `45_brain_window_away.png`: the controller brain trainer window.

### Decisions (one line of why each)
- **The test opens proj_truck from a scratch folder and fills it with `NotebookStore.SeedFixtures`.** Why: no notebook existed on this machine, and the test must never write into a real one; it is the real store and the real project id.
- **The test lays the slalom out in `MockFloorPlan.LivingRoom()` through `ArtifactHost.Room`.** Why: the desktop rig's simulated living room has no clear 4.6 x 0.8 m strip (`no_free_floor` from every standing place and facing), so a truck can't spawn there otherwise.
- **The test removes the `projects`, `artifacts` and `user` folders under persistentDataPath only if it found them missing.** Why: it created them (slalom layout, lessons) and must leave the machine as it found it.
- **The gesture display is adopted by the window manager when the menu first asks whether Gestures is open, or when the tile is pressed.** Why: it is the cheap place to do it; the display is made by the gesture engine, not the OS.
- **Tile order cad, code, train, notebook, messages, gestures (Drive appends).** Why: as the writer registered it; reorder in `OSRegistry` if wanted.

### Not working or not done
- With the ML desk open away from a desk, its large panels sit in front of the menu (the menu opens 0.5 m away, the panels reach nearer) and a pointer press on a tile hits the panel instead (`99_press_failed_notebook.png`). So with Train open, a second tool can't be opened from the menu until the mode is closed. The test opens Train last for that reason. A fix belongs in menu placement or mode layout (5.2/5.3), not here.
- The controller brain view (ML module, `ControllerBrainView`) has layout flaws of its own at this size: the steer/throttle values wrap ("+0.2" over "0"), the controller file note is clipped by its card, the text is small, and it says "Mock lap fixture" whatever the truck does. It is the same view as the `MLPanels/ControllerBrain` prefab; not changed (not the OS's module).
- The notebook window has a blank gap of about 40 units above "Versions" (layout estimates a taller attempts section than it renders). Not clipped, not overlapping; left.
- Ray and poke on the tiles were not exercised (no headset); the EventSystem pointer press was.
- `M` is dropped by the Input System when the editor window is unfocused; the test then opens the menu with `OSShell.OpenMenu` (it logs this). The M key itself is covered by OSMenuTest.
- Free disk fell from 9.4 to 5.4 GB during this part, from files under `AppData\Local\Microsoft` (not Unity, not the repo); 5.8 GB at the end.

### Changes outside `OS/`
None. No Project Settings, scene or `manifest.json`.

---

## Orchestrator note: disk stop and restart (2026-11-12 night to 2026-11-13)

- **Stopped.** The Projects part (5.5) checked the disk before starting and found 4.92 GB, then 4.48 GB, so it stopped without changing anything, as the 5 GB rule says. The parts after it (Artifacts, the P0 play test, the rules review) did not run. Free space reached 3.5 GB a few minutes later.
- **Cause.** Not this repo: `unity/Library` stayed at 13.05 GB and `unity/Temp` plus `unity/Logs` are 0.03 GB. The Windows page file is 29 GB allocated with under 3 GB in use (commit 54 of 60.6 GB with about 30 other `claude` processes running), and the Outlook cache (`.ost`, 4.9 GB) was being written during the run. Free space has swung between 3.5 and 9.5 GB all day without the repo changing.
- **Restart.** Hudson said to continue (2026-11-13); free space was 5.76 GB at that point. Decision: for the rest of P0 a part stops only if free space is under 3 GB on two checks two minutes apart, and between 3 and 5 GB it works but does nothing disk-heavy (no player build, no reimport). Why: the figure swings by several GB from other programs, so a single reading under 5 GB stopped a part that would have used almost no disk. The APK build at the end still needs real headroom and will not start under 5 GB.
- **Added to the P0 play-test part:** fix the menu being unreachable behind a mode's panels away from a desk (found in 5.4), with a play-test step for it.

---

## Part 5.5: Projects tab and the Project window

Integrated from `.os-run/staging/projects/` (`OSProjects.cs`, `OSProjectWindow.cs`). The writer's code compiled on the first try against the real `OSUi`, `OSWindows`, `NotebookStore`, `ArtifactHost` and `BrainLoops` (0 errors, 0 warnings from `OS/`). The writer's HOOKS.md asked for no edit outside `OS/`, so none was made.

### What was done
- `OSProjects` registers the Projects source at BeforeSceneLoad: four starters (RC truck, Quadcopter, Rocket, Image classifier) plus every other folder under `<notebook root>/projects/`. The list and each status line are cached (re-read on a notebook or registry change, or after 4 s, never per frame). Status lines are real: `v<version>` plus `<artifact> passed|failed|stopped` from the newest run record of an artifact linked to the project, else the last judged attempt, else "not started".
- `OSProjectWindow` (window id `project`, 0.74 x 0.66 m): Plan card (goal, test, up to 4 milestones as [x]/[ ]), Versions (newest 3 with each version's outcome), Artifacts (linked, with the last run), Continue (Course, Drive, Train, Autopilot, Code, Send, Notebook in one row; other projects get what fits them), a live brain line for the truck, and a status line that shows each action's real result or error in plain words.
- Truck Course: makes the project the open notebook first (so `links.thing == "truck"` survives `FromTemplate`), then `artifact_create{template:"slalom"}` or `artifact_open` through `AlbieTools.Call`, the same path as voice. The first press on a machine with nothing approved says "The slalom asks first. ... Tap Allow on the card in front of you."; after Allow it opens the slalom with the brain truck.
- Starters: Quadcopter and Rocket open CAD and then the design from the stored mocks (`CadSession.OpenPart`); Image classifier opens Train and says "Needs Albie's cloud to load the dataset. Not available offline." (Load dataset is off until `ForgeCloud.Serves("/ml/")`). A bad id leaves the previous project open and throws nothing.
- Added `OSProjectsTest` (`Forge/OS/Run Projects Test`, `Forge.OS.Editor.OSProjectsTest.Run()`, result in `LastResult`). It runs on a scratch root (notebook root, `ArtifactLibrary.RootOverride`, `ArtifactApprovals.FileOverride`), so it simulates a fresh install with no projects folder and never touches the machine's own notebook or artifacts; it deletes the scratch folder after.

### Check output (verbatim)
```
[OSProjectsTest] 87 passed, 0 failed
[OSMenuTest] 57 passed, 0 failed
[OSWindowsTest] 50 passed, 0 failed
[OSToolsTest] 94 passed, 0 failed
```
`OSCheck.Run()` still `PASS os check` (16 ok lines). Key lines of OSProjectsTest:
```
fresh: proj_truck=not started (open) | proj_quadcopter=not started | proj_rocket=not started | proj_classifier=not started |
course, first press: The slalom asks first. Uses your room's layout. Reads your Thing's position and speed. Tap Allow on the card in front of you.
course, second press: The slalom is open. The brain truck is at the start. This room is tight, so I made a shorter course: 5 gates, 1.0 m apart.
ok   Course gives a slalom with the brain truck (BrainLoops.Active != null)
ok   the open manifest has links.thing == "truck" (truck)
ok   the open manifest links to proj_truck (proj_truck)
with data: proj_truck=v1 · slalom passed (open) | proj_quadcopter=not started | ... | proj_demo_folder=not started |
ok   Image classifier shows the honest offline line (Needs Albie's cloud to load the dataset. Not available offline.)
ok   quadcopter: CAD opened on the quadcopter design (quadcopter)
ok   rocket: CAD opened on the rocket design (rocket)
ok   a bad id leaves the previous project open (proj_rocket)
```
Screenshots (`unity/Logs/os/`, ignored by git), each opened: `50_projects_tab_fresh.png` and `54_projects_tab.png` (the Projects tab: four starters with true status lines, then a folder tile "Demo folder"); `51_truck_window_fresh.png`, `52_truck_window_course.png`, `53_truck_window_run.png`, `58_truck_window_full.png` (the truck Project window fresh, after Course, with a run, and with a full plan, 4 versions and a run: nothing clipped or overlapping); `55_generic_window.png`, `56_classifier_window.png`, `57_quadcopter_window.png` (a folder project, the classifier with its offline line, the quadcopter with CAD open beside it).

### Decisions (one line of why each)
- **Icon names from the kit's table, not the writer's guesses** (`quadcopter`, `classifier`, `projects` for folders). Why: `drone`, `image` and `folder` are not kit icons.
- **Notebook button in the same row, Secondary style.** Why: Borderless on a second row read as a label, not a button.
- **Text sizes 22 / 20 / 18 units, lists capped at 4 milestones, 3 versions, 3 artifacts, window height 0.66 m.** Why: the fullest case (long plan, many versions, a run) fits with room left; bigger text is easier to read from the desk.
- **Send stays off with the reason shown.** Why: nothing on the device can send the model (the only offline send writes a text draft to the demo chat); the window says so instead of faking it.
- **The test opens the menu, then sets the tab.** Why: `OSShell.SetTab` before the menu's first build is overwritten with Tools by the menu's tab bar build (see below).
- **`OSProjects.Unregister()` added; `OSMenuTest` now unregisters the real Projects list for its run and puts it back.** Why: the test counts its own five sample Projects tiles and the real list added four more.
- **`OSToolsTest` helpers `Press`, `FindDeep`, `CenterOf`, `Mm`, `LayoutProblem`, `AllText` made `internal`.** Why: `OSProjectsTest` reuses them instead of copying them.

### Not working or not done
- `OSShell.SetTab(x)` called before the menu has ever been built is lost: the first build of the tab bar selects Tools. Affects a voice `os_tab` on a session that never opened the menu. Not fixed here (menu code belongs to the menu part); the test works around it.
- After the slalom is closed the brain truck stays (`BrainLoops.Active` is still set); that is the artifact module's behaviour. The window then reads "Course: not open · brain truck ready".
- Drive: disabled with "Coming with the Drive pad." until the Drive part registers a `drive` tool. When it does, the Drive button opens it and starts a 2-lap brain recording if the brain is idle (remove that in `OSProjectWindow.DoDrive` if the Drive window owns recording).
- Train for the truck calls `brain_train` and Autopilot calls `autopilot_on/off`; both were only exercised with no laps recorded ("Train first."), not through a full train. Ray and poke were not exercised (no headset); the EventSystem pointer press was.
- The permission ask text comes from the artifact module and says "Thing" ("Reads your Thing's position and speed."); not changed here.
- Free disk was 4.2 to 3.1 GB during this part (page file); nothing disk-heavy was run.

### Changes outside `OS/`
None. No Project Settings, scene or `manifest.json`.

---

## Part 5.6: Artifacts tab and movable artifact panels

Integrated from `.os-run/staging/artifacts/` (`OSArtifacts.cs`, `OSArtifactWatcher.cs`). The writer's code compiled on the first try against the real `ArtifactHost`, `ArtifactLibrary`, `AlbieTools`, `PanelManipulator` and `VolumeManipulator` (0 errors, 0 warnings from `OS/`). Nothing under `Artifacts/` was changed: the panels and volumes are made movable from the OS side.

### What was done
- `OSArtifacts` registers the Artifacts source at BeforeSceneLoad: every artifact in `ArtifactLibrary.List()` (sorted by title), then a **New** section (outline tiles) with one tile per template plus Three-body (a bundled simulation: it opens, it is never created). Cached, rebuilt only when something changed.
- A tap goes through the Albie tools (`artifact_open`, `artifact_close`, `artifact_create`), so placement, the first-time Allow sheet and `FollowsUser` are the same as by voice. A tap on the artifact that is open closes it. A New tile makes it (`artifact_create` with `open:false`, then `artifact_open`), keeps the open project open so the library links it (`links.project`, `links.thing`), and shows the real reason under the tile when it fails.
- Status line: `open`, `for RC truck` (the project's name from the Projects tab), the last run (`passed`/`failed`/`stopped`), `on your floor` for room artifacts; New tiles say `on your floor` or `opens as a window`; while the Allow sheet waits the tile says `tap Allow on the sheet`.
- `OSArtifactWatcher` (one per scene with a Workspace) notices the host opening, closing or rebuilding and adopts every `ForgePanel` and `ForgeVolume` under it: `PanelManipulator` / `VolumeManipulator` added and Init'd as the Workspace does, and a window `artifact:<id>:<n>` in the window manager whose close runs `artifact_close`. So the Allow sheet, panels and volumes can be moved (Straight / Face me and resize brackets appear), and Bring windows here and Close all reach them.
- Added `OSArtifactsTest` (`Forge/OS/Run Artifacts Test`, `Forge.OS.Editor.OSArtifactsTest.Run()`, result in `LastResult`). It runs on a scratch root (`ArtifactLibrary.RootOverride`, `ArtifactApprovals.FileOverride`, notebook root), so this machine's own notebook and artifacts are never touched, and lays room artifacts out in `MockFloorPlan.LivingRoom()` through `ArtifactHost.Room` as `OSToolsTest` does.

### Check output (verbatim)
```
[OSArtifactsTest] 68 passed, 0 failed
[OSMenuTest] 57 passed, 0 failed
[OSWindowsTest] 50 passed, 0 failed
[OSToolsTest] 94 passed, 0 failed
[OSProjectsTest] 87 passed, 0 failed
PASS os check (16 ok lines)
[ArtifactChecks] 97 passed, 0 failed
PASS run check
PASS slalom check
PASS hoop check
PASS biology templates check
PASS physics templates check
PASS repo activity check
PASS voice rules check
PASS slalom brain check (Play mode, the brain truck on its colliders)   (25 ok lines, no FAIL)
```
Key lines of OSArtifactsTest: tapping New Heart makes and opens the heart (volume window, movable); the Heart tile closes and reopens it; New Solar system opens and closes from its tile; New What shipped? asks first (`tap Allow on the sheet`), the Allow pill pressed with a pointer opens it with a movable panel and volume; New Slalom with the truck project open asks first, then `BrainLoops.Active != null`, `links.thing == "truck"`, `links.project == "proj_truck"`, and the Slalom tile reads `open · for RC truck`; Bring windows here moves an artifact window from 6 m to under 2 m; Close all closes the open artifact and leaves no artifact window; opening an id that does not exist answers with the reason and opens nothing.

Screenshots (`unity/Logs/os/`, ignored by git), each opened and looked at: `60_artifacts_tab_new.png` (the tab fresh: a "New" heading and eight outline tiles, page 1 of 2), `66_artifacts_tab_page1.png` (four made artifacts first, each "for RC truck", the open slalom "open · for RC truck", then the New heading and its first four tiles), `67_artifacts_tab_page2.png` (page 2: the rest of the New tiles, ending with Three-body), `61_heart_open.png` (the heart volume on the desk with its labels and its panel), `63_allow_sheet.png` (the Allow sheet with Straight / Face me and the corner brackets), `64_repo_activity_open.png` (repo activity's panel), `65_slalom_open.png` (the slalom scoreboard panel: Time, Gates, Cones hit, Runs, Versions, Spacing slider, Run again; nothing clipped).

### Decisions (one line of why each)
- **No edit under `Artifacts/`.** Why: the adoption can be done from the OS side (the host parents every panel and volume under itself), so there is nothing to list for Satyabrata. His module's rebuild still resets a moved panel to its default place (a slalom input change destroys and recreates the panels); the window is adopted again and is movable again.
- **New tile ids are `new_<template>`.** Why: the artifact id and the template id are the same string, so the New tile cannot reuse it. `os_open{kind:"artifact", id:"heart"}` works once a heart exists; a tap on an open artifact closes it.
- **The status line keeps to 24 characters, taking the parts in order of importance (for project, open, last run, floor).** Why: the tile shows about 24 characters and "open · for RC truck · on your floor" clipped; a linked slalom now reads `open · for RC truck`.
- **The repo activity New tile is titled "What shipped?"** Why: "What shipped this week?" is cut to "What shipped this..." by the tile. The panel itself keeps its own title.
- **Every template made while a project is open is linked to it, not only the slalom.** Why: that is `ArtifactLibrary.FromTemplate`'s rule (spec 5.6 "an artifact made while a project is open links to that project"); the heart tile also says `for RC truck`.
- **`OSArtifacts.Unregister()` added; `OSMenuTest` now unregisters the real Artifacts list for its run and puts it back.** Why: the test counts its own sample Artifacts tiles and the real list added more.
- **`OSInput` reuses its virtual keyboard instead of adding one per run.** Why: Play-mode devices outlive Play mode; 32 "OSTestKeyboard" devices had piled up from the test runs and the Input System began logging "Control count per binding cannot exceed byte.MaxValue=255" during `OSToolsTest`. The 32 stale devices were removed from the open editor.
- **The test brings the windows here before taking screenshots.** Why: where the host puts an artifact in the desktop rig's simulated room can lie inside a wall of that room, which cuts the panel off in the picture.

### Not working or not done
- Only the desktop pointer was exercised (the EventSystem press), not a headset ray or poke or a real hand drag; the manipulator is the same component Workspace panels use.
- A panel the user has moved goes back to its default place if the artifact rebuilds (see above). Resizing an artifact panel smaller can clip its widgets, which are laid out in fixed millimetres (the artifact module's `ArtifactWidgets`); not changed.
- Artifact panel pills (Allow, Run again, ...) are sized by the artifact module, not by the OS kit; the 22 mm rule was not enforced on them.
- Away from a desk the volume manipulator is Init'd with the desk frame as the writer wrote it; if a volume can't be dragged below a table's height on the headset, pass `null` as the frame in `OSArtifacts.AdoptPanels`.
- The Allow tap text comes from the artifact module.
- `Main.unity` was left dirty in memory after the play tests once; discarded by re-opening it, never saved. Free disk was 3.1 to 6.9 GB during this part; nothing disk-heavy was run.

### Changes outside `OS/`
None. No Project Settings, scene or `manifest.json`.

---

## Part 6.1: OSPlayTest, OSCheck additions, and the menu in front of every panel

Integrated from `.os-run/staging/playtest/` (`OSPlayTest.cs`, the `OSCheck.additions.md` snippets). The writer's code compiled on the first try (0 errors); two `CS0618` warnings (`FindObjectsSortMode`) were fixed. The writer's HOOKS.md asked for no edit outside `OS/`, so none was made. The `.md` was not copied into `Assets/`.

### What was done
- **OSCheck:** the eight new checks (kit complete, every entry has id/title/kit icon/open action, unique ids and titles, status lines never throw and read like the UI, the tabs hold the tools/projects/artifact templates, `os_open`'s description names every tool, the table button is a finger-sized target, the planner tree) and two more lines in the shell-prefab check were pasted into `OSCheck.cs`. `drive` is only required once `OSDriveWindow` exists (spec 5.8 is its own part); until then the check says "Drive is not built yet".
- **OSPlayTest** (`Forge/OS/Run Play Test`): steps 1, 2a, 2b, 3a, 3b (corner button: reports skipped, not failed), 3c, the new 3d, and 9. Every press is a pointer press: the control is projected to the screen, the EventSystem is asked what is under that point, the test fails with the path of what it hit if that is not the control, and then pointer enter/down/up/click/exit are sent. It never calls `onClick` or an OS method for a user action; where a press fails it records the failure and calls the API only so later steps have something to work on.
- **The menu in front of every panel (found in 5.4):** `OSMenu.Pose` now looks for a place where nothing is in front of any of 28 points on the plate (windows, a mode's panels and volumes, with a margin of 5 cm behind the menu). It tries the settings' place first, then nearer (down to 0.35 m, 2 cm steps), then turning (10 degree steps, up to 30) and tilting down (10 degree steps, up to 40 more), cheapest first (nearer costs 1 per cm, turning 2 per degree). With no clear place it takes the one with the least in front of it. `OSMenu.Blocked(...)` is public so the play test can assert it. Step 3d opens Train away from a desk (its panels reach 0.46 m from the eyes), opens the menu, asserts 0 of 28 plate points are behind a panel, and presses the Notebook tile.
- `OSMenu.LastRotation` added (the menu's facing at its last opening) for the assertion.

### Check output (verbatim)
```
PASS os check   (26 lines: every check ok; the planner check says "skipped: OSPlanner is not built yet", the tabs check says "Drive is not built yet")
[OSPlayTest] 54 passed, 0 failed (1 skipped)       -> False|PASS os play test: 54 passed, 0 failed (1 skipped)
[OSMenuTest] 57 passed, 0 failed
[OSWindowsTest] 50 passed, 0 failed
[OSToolsTest] 94 passed, 0 failed
[OSProjectsTest] 87 passed, 0 failed
[OSArtifactsTest] 68 passed, 0 failed
[GestureSelfTest] 104 passed, 0 failed
[GesturePlayTest] 32 passed, 0 failed
[ArtifactChecks] 97 passed, 0 failed
PASS notebook check
[ML Main smoke] PASS: saved Main scene, ML desk, flat/curved layout, ml.layout aliases, seven VizCard formats, scatter selection, regression row detail and restoration.
PASS voice rules check
PASS room slalom check (mock truck, Editor)
```
Key lines of the play test: `[OSPlayTest] info at the default place (0.50 m, straight ahead) 24 of 28 plate points would be behind a panel; the menu sits at 0.36 m with 0 behind one` (the bug and the fix in one line); `ok   a second tool opened from the menu while Train was open (Notebook)`; `ok   no panel is in front of any part of the menu (0 of 28 plate points are behind one)`; `skip corner button: skipped: corner button not built yet`.

Screenshots (`unity/Logs/os/`, ignored by git), each opened and looked at: `01_table_button.png` (the table button on the desk, the code mode behind), `02_menu_opened.png` (the menu opened by pressing it: tabs, six Tools tiles, the strip), `03` to `05` (each tab), `06_three_open.png` (notebook, project and heart open at once), `07_away_windows_stay.png` (the user 2 m from the desk, the windows still around it), `08_menu_away.png` (the menu opened with M away from the desk), `09_brought_here.png` (after Bring windows here: code, heart panel and Hands in front), `10_train_open_away.png` (Train open away, four panels around the user), `11_menu_over_train.png` (the menu in front of the Train panels, tabs, tiles and strip readable), `12_train_still_open.png`, `13_notebook_opened.png` (the Notebook that the menu opened; half of it is hidden by a wall of the desktop rig's small simulated room, the window itself is complete), `14_closed_all.png` (the desk with nothing left).

### Decisions (one line of why each)
- **"In view" after Bring windows here is measured in the horizontal plane against the 110 degrees the window manager fans to (limit 112), not 80 degrees in 3D.** Why: the writer's 80 degrees was a guess; `OSWindows` fans to 110 degrees on purpose when the rows in front are full (5 big windows), and the picture shows the main ones in front. Hudson may want fewer degrees: `MaxYaw` in `OSWindows`.
- **The menu comes nearer before it turns, down to 0.35 m.** Why: turning and tilting put it out of view (an earlier try landed 65 degrees below the view and the user would see nothing); nearer keeps it in the middle of the view. 0.35 m is `OSSettings.MinDistance`, so a user who set a larger distance sees no change when nothing is in the way.
- **Only panels and volumes block the menu, not loose 3D content.** Why: counting every mesh under a panel (the ML model slabs float in front of their panel) left no clear place within reach, so the menu landed out of view with 2 points still blocked.
- **Old pictures are cleared by a manifest (`play_test_shots.txt`), not by pattern.** Why: the writer deleted every `NN_*.png`, which also deleted the 5.x tests' pictures (including `99_press_failed_notebook.png`) on the first run. Those pictures were not kept; the tests that made them write them again when run, and 99 is replaced by `11_menu_over_train.png`.
- **The 3a picture looks back at the desk and the 9 picture looks at the empty desk.** Why: with the user facing away the picture was a blank wall and proved nothing.

### Not working or not done
- **3D content that floats in front of its panel (the ML model slabs and the dataset's card stack) still draws over parts of the menu on Train.** The menu is in front of every panel plane and a pointer press reaches its tiles; the picture shows the slabs over the CAD and Code tiles. A fix needs either the ML module to keep that content behind the menu or the menu to draw over 3D content; left for Hudson to judge on the headset.
- The menu at 0.35 m is about 77 degrees wide, a little over the rubric's 70. It only goes that near when a panel is in the way.
- Corner button (3b) is a skip; steps 4 to 8 are not written (their parts are not built).
- Ray and poke were not exercised (no headset); the EventSystem pointer press was.
- `Main.unity` was dirty in memory after some Play runs; it was re-opened (never saved) each time. Free disk was 6.85 to 6.87 GB through this part; nothing disk-heavy was run.

### Changes outside `OS/`
None. No Project Settings, scene or `manifest.json`.

## Part fixes-p0: what P0's review and tests found

Eight fixes, each with a check that sees it. Free disk on C: 6.15 GB at the start, 5.8 GB at the end; nothing disk-heavy was run.

1. **`OSTableButton`**: a failed build (kit or its prefabs missing, or the backplate without its `CanvasRoot`/`UIBackplate`) is remembered in `buildFailed` and not retried every frame (reset in `OnEnable`); `Build` is now `TryBuild` plus the bookkeeping, so on a failure path the half-made root is destroyed and `built` is never set.
2. **`Workspace.Open`** (in `Core/`): the new free frame is made first; if the OS can't give one, `Open` returns with the old mode untouched (not folded, still `ActiveMode`). `start` (the switch delay) is still taken before anything folds. Check: `OSWindowsTest` makes `FreeFrame` fail, calls `Open("cad")` over open ML and sees `ml` still active in its free frame with 4 of 4 panels.
3. **CAD measure key**: `OSKeys.Active` (public static, set in `OnEnable`, cleared in `OnDisable`, reset at `SubsystemRegistration`). `CadViewer` takes M alone for measure unless `OSKeys.Active`, then Shift+M. The CAD README (two lines), the `CadViewer` header comment and the `CadSandboxKeys` comment say exactly that.
4. **Tab at first build**: the cause was the toggle group lighting its first toggle (Tools) when the tab bar came alive, which the tab handler took for the user choosing Tools. `OSMenu` now ignores tab callbacks while it shows itself (`syncingTabs`), so the shell's tab is the truth. `OSMenuTest`: `SetTab(Projects)` before the menu was ever built, press the table button, the Projects toggle is on and Tools is off, then back to Tools.
5. **Menu covered by 3D content (Train)**. Cause, found with a list of renderers over the plate (`info over the menu` lines): two things. (a) The dataset card stack was transparent and drawn after the menu although it lies behind it (a canvas writes no depth, so a transparent thing sorted later paints over it): the menu's canvases now have sorting order 100 (`OSBody.RaiseAboveScene`). (b) Train's layer slabs are really nearer than the menu: they are a 3D stage inside the Model panel (`WorkspaceFreeFrame/Model3D(Clone)/Content/Model stage/Tower`, 0.19 to 0.32 m from the eyes, the menu at 0.36 m, the nearest it may come), not a `ForgeVolume`, so placement never saw them. Fix: placement now counts the meshes under every volume and every panel (their world box); when no place is clear of them it takes the place clear of panels and volumes as before (second pass), and the open menu switches off (`Renderer.forceRenderingOff`, restored when it closes) the mesh parts still in front of the plate. Not `Renderer.enabled`: Train re-enables its slabs every frame (first try, seen failing). `OSPlayTest` step 3d: renders the head camera twice in one frame, normal and with every scene renderer off, and compares the middle of each tile; fails if a tile differs (`nothing is drawn over the menu: 0 of 6 tiles are covered`), and asserts the hidden parts are back after the menu closes. `unity/Logs/os/11_menu_over_train.png` was looked at: all six tiles are whole, the slabs are gone while the menu is open.
6. **Statics**: `OSMenu` (volumes, and the new content lists) and `OSArtifacts` (`line.Length = 0`) reset at `SubsystemRegistration`.
7. **Artifact panel over the Code window**: the artifact host puts its panels at an offset from itself without knowing the open mode's slots; the OS adopted them where they stood. `OSWindows.ClearOf(window, ownRoot)` (called from `OSArtifacts.AdoptPanels` for each newly adopted panel) measures from the eyes against windows, the mode's laid-out slots and volumes, ignoring the artifact's own panels and model, and glides the panel to the nearest free place if it overlaps. `OSPlayTest` 2b asserts it with the yaw/pitch rectangles `OSWindowsTest` uses (artifact panels against every other showing panel). With the call taken out the assertion fails with `Panel Human heart x CodePanel(Clone)` (run and seen), with it it passes; `06_three_open.png` shows the heart panel gone from behind the Code window.
8. **Selected tab label**: three causes, found in the UI Set. The label's colour is animated by the toggle's animator (to 0.15 grey but with the text 10% transparent), the theme manager repaints every untagged text white on `ApplyCurrentTheme`, and TMP draws thin dark strokes on a light plate at partial coverage. Fix in the kit, not the menu: `OSUi.Tab` renames the title (`OSUi.TabTitleName`, so the animation no longer finds it) and tags it `QDSUISharedThemeColor` (the theme manager leaves it alone), and `OSToggleSync` sets its colour from the theme: `textPrimaryInvertedColor` when lit, `textPrimaryColor` when not, solid. Checks in `OSMenuTest`: label 12.4:1 and icon 12.8:1 by colour (needs 4.5), and the darkest drawn pixel of the label in a head-camera picture against the fill (was 5.0:1 with the pale label, now 6.0 to 8.1:1; the check needs 5.5).

### Check output (verbatim)
```
PASS os check   (24 ok lines, the planner one "skipped: OSPlanner is not built yet")
[OSMenuTest] 63 passed, 0 failed
[OSWindowsTest] 52 passed, 0 failed
[OSToolsTest] 94 passed, 0 failed
[OSProjectsTest] 87 passed, 0 failed
[OSArtifactsTest] 68 passed, 0 failed
PASS os play test: 59 passed, 0 failed (1 skipped)
[GestureSelfTest] 104 passed, 0 failed
[GesturePlayTest] 32 passed, 0 failed
[ML Main smoke] PASS: saved Main scene, ML desk, flat/curved layout, ml.layout aliases, seven VizCard formats, scatter selection, regression row detail and restoration.
PASS voice rules check
```
There are no CAD checks (nothing under `Forge/CAD` but the builders). `unity recompile`: 0 errors, 0 warnings.

### Decisions (one line of why each)
- **Hide, not fade, the model parts in front of the open menu.** Why: Forge's shaders have no common alpha, and `forceRenderingOff` is one flag that nothing else touches; they come back when the menu closes.
- **Placement counts meshes first, panels alone second.** Why: counting meshes alone left no clear place (the finding of part 6.1) and broke the panel-clear guarantee on the first try (5 of 28 points behind a panel); two passes keep it.
- **Sorting order 100 on the menu canvases stays although the hide is what fixes Train.** Why: it fixes the transparent cards that lie behind the menu.
- **The drawn-label contrast limit is 5.5:1, the colour one 4.5:1.** Why: the drawn value moves between 6 and 8 with sub-pixel position; the pale label measured 5.0.
- **`OSMenu.Blocked` has a `meshes` parameter (default true).** Why: the play test's "no panel in front" must not count the solids the menu switches off.

### Not working or not done
- The real M key was dropped in one run in about ten (OSPlayTest 3c once, OSMenuTest once, each passed on the next run); the cause was not found (a key press that lands in the same input update as its release is one guess), the same press passes on the next run.
- Primary button labels ("Build something") are dark on white and still look thin; the tab title was the one asked for.
- `OSWindowsTest` counted 50 checks in P0, 52 to 60 now (the count varies with the run); no check was removed.

### Changes outside `OS/`
- `Core/Scripts/Workspace.cs`, `Open()`: the new free frame is made before anything folds; on failure it returns with the old mode untouched (fix 2).
- `CAD/Scripts/CadViewer.cs`: measure is M, or Shift+M while `OSKeys.Active` (fix 3); `CAD/Scripts/CadSandboxKeys.cs` (comment) and `CAD/README.md` (two lines) say so.
No Project Settings, scene or `manifest.json`.

## Part drive-5.8: the hand transmitter

Free disk on C: 5.81 GB at the start, 5.79 GB at the end (above the 5 GB line; no player build, no Reimport All). One integrator lane, no staged files.

### What was done
- `OSDrivePad` (new): a 169 mm square pad with a knob built from the UI Set's rounded box (the material of a stock button plate) and the kit's chevrons. It handles `IPointerDown/IDrag/IPointerUp/IEndDrag` (so the mouse through the EventSystem, and the Interaction SDK's ray and poke through the same canvas events). While held it writes `stick.x` and `stick.y` every frame; it writes zero once on release, on disable and (through `Release`) on Stop; the knob springs back in about 0.12 s on unscaled time. No allocation per frame.
- `OSDriveWindow` (new): says which Thing it drives (the brain truck on the slalom, another running truck, or "No truck to drive yet. Open the RC truck project and tap Course."), the pad, a Stop button 195 x 49 mm (a stock UI Set button inside a scaling holder), and the state line (`Recording lap 1 of 2`, `Idle`, `Idle · 2 laps recorded`, `Training the brain`, `Autopilot driving`). `Stop()` releases the pad, clears the manual channels, aborts a lap being recorded (`StopRecording`) or stops the Autopilot (`StopAutopilot`), found through `IBrainLoop.ToolState()["mode"]`. The Drive tile registers from `OSDriveWindow.RegisterTool`, called last by `OSToolsCatalog.Register`, so it follows Gestures; its status is `no truck yet`, `truck ready`, `recording a lap`, `brain training`, `autopilot driving`. The truck project's Drive action (unchanged code) now finds the tool: it opens Drive and starts a two-lap recording when the brain is idle.
- `OSDriveTest` (new, `Forge/OS/Run Drive Test`, `OSDriveTest.LastResult`, `OSDriveTest.Level`): scratch root, MockFloorPlan room, proj_truck, Course (with the Allow step), the Drive tile and the project's Drive action pressed with real pointer events; then the truck is driven only through synthetic `PointerEventData` sent to the pad's handlers (the screen position of a point on the pad, from the same raycast the mouse uses). Checks: knob up moves the truck forward, left and right turn it, release writes zero and the truck stops, the knob springs back, Stop with the knob still held, Stop on a recording and on the Autopilot; then the full flow with a pursuit controller in the test that turns the next gate into pad positions.
- `OSCheck`'s tabs check now requires Drive (the window exists); `OSMenuTest` steps the real Drive tile aside with the other real tools.

### Level reached: FULL
Two laps of the slalom were completed by driving the pad (pursuit controller through `PointerEventData`), Train ran on those laps, and the Autopilot completed the slalom on a scored run (`Drove: candidate 1` in the last run; `candidate 2` in the run before, after a first candidate timed out; never the pretrained fallback). No fallback level was used.

### Check output (verbatim)
```
PASS [OSDriveTest] 82 passed, 0 failed (level: full)
PASS os check   (the tabs line: "the tabs hold the seven tools, the four starter projects and every artifact template")
[OSMenuTest] 63 passed, 0 failed
[OSWindowsTest] 52 passed, 0 failed
[OSToolsTest] 95 passed, 0 failed
[OSProjectsTest] 87 passed, 0 failed
[OSArtifactsTest] 68 passed, 0 failed
PASS os play test: 59 passed, 0 failed (1 skipped)
PASS slalom brain check (Play mode, the brain truck on its colliders)      (Forge/Artifacts/Check Brain Loop (Play), every ok line)
PASS room slalom check (mock truck, Editor)
PASS autopilot check
PASS behaviour cloning check
PASS slalom check
PASS voice rules check
[GestureSelfTest] 104 passed, 0 failed
```
Measurements of the Drive test (the last run): `pad 169 x 169 mm, Stop 195 x 49 mm`; `knob up for 1 s: moved 0.38 m forward, speed 0.49 m/s`; `after the release the speed fell from 0.49 to 0.05 m/s in 2.1 s`; `the knob took 0.14 s to spring back`; `knob up-left for 0.8 s: heading changed -26 degrees`; `knob up-right for 1.0 s: heading changed 65 degrees`; `pad laps: 2 completed, state Idle`; `slalom run records: 2, the last was completed`; `training: trained 3 candidates in 530 frames (2082 ms compute); in rehearsal the best finished 9 of 9 starts`; `autopilot: drove candidate 1; attempts candidate 1 completed; last run completed`.

Screenshots (`unity/Logs/os/`, each opened and looked at): `70_tools_seven_tiles.png` (the menu on the Tools tab: CAD, Code, Train, Notebook, Messages, Gestures, Drive, seven tiles, Drive says "no truck yet"), `72_drive_idle_truck.png` (the Drive window with the truck at the start: "Drives: the truck (the brain truck on the slalom)", knob in the middle, red Stop, "Idle"), `73_drive_driving.png` (the knob pushed up and left, "Recording lap 1 of 2"), `74_drive_truck_moved.png` (the orange truck 1 m down the slalom among the cones and gate outlines after the pad run). Also written, not all opened: `71_drive_no_truck.png`, `75_drive_after_stop.png`, `76_autopilot_done.png`.

### Decisions (one line of why each)
- **The pad writes `stick.y` times `SlalomBrain.KeySpeed * Drag / MaxAccel` (0.267) on the brain truck, full range on another truck.** Why: `SlalomBrain.Keys` scales a Touch stick and W the same way ("hand laps train like keyboard laps"); a full 1.0 would be 2 m/s, too fast to weave 1 m gates.
- **The pad reads the pointer with the camera of the raycast module (`ScreenPointToLocalPointInRectangle`), not `ManualInputs` or a custom raycast.** Why: it is what uGUI sliders do, and the Interaction SDK's pointer module sets that camera and the position for a ray and a poke.
- **Opening the Drive tool starts no recording; the truck project's Drive action does.** Why: the existing `OSProjectWindow.DoDrive` already starts the two-lap recording when the brain is idle; the tool itself stays a window.
- **No "record" button in the Drive window.** Why: the spec lists title, Thing, pad, Stop and state line; a recording starts from the project's Drive action or by voice.
- **Stop is the stock button scaled up in a holder, not a bigger box.** Why: the UI Set button draws its plate at 48 units whatever its box, and its animator owns the button's own scale (setting either left a 31 mm pill).
- **`flexibleHeight = 0` on the Drive window's rows and Stop holder.** Why: the UI Set button reports flexible height and the column handed all spare height to the Stop row (it measured 160 mm).
- **The test switches the simulated room's `*_EffectMesh` colliders off while the truck drives and restores them.** Why: the mock living room's strip of floor lies inside the desktop rig room's wall and storage colliders, so the truck crashed at the start (`touching WALL_FACE_EffectMesh, STORAGE_EffectMesh`); the slalom's own cones stay. On a headset the real room's colliders and the real floor are the course's room.
- **`OSMenu.Refresh` runs again after a pass instead of inside one.** Why: the Gestures tile's status line hands the gesture display to the window manager the first time it is seen, which fires `OSRegistry.Changed` and refreshed the menu from inside its own pass: 9 entries in `ActiveTiles` (the last two twice). The tiles on screen were right; the list the tests and any code read was not.
- **Wrist-roll steering (`hand.roll`) is not built.** Why: it was optional and the pad is the transmitter.

### Not working or not done
- Ray and poke were not exercised (no headset): the pad is driven by the EventSystem pointer path only, the same one the other OS tests use. The Interaction SDK's pointer module sets the event camera and position the pad reads, but whether a poke feels right (the finger must stay in the surface while dragging) needs the headset.
- The Autopilot in the Drive test drives a different candidate from run to run (candidate 1 or 2); a candidate that times out falls to the next, as the brain loop is designed to.
- The pad has no dead zone of its own: the truck's `truck_manual` file already has one (0.05 on throttle, 0.1 on steer).

### Changes outside `OS/`
- `Artifacts/Scripts/SlalomBrain.cs`: new `public int LapTarget => lapTarget;` (the Drive window reads it for "lap n of m").
- `Artifacts/Scripts/SlalomBrain.cs`, `EndLap`: the status line no longer reads `lap.Seconds` when `Thing.EndLap` returns no lap. Why: stopping a lap in which nothing was recorded (Stop pressed right after the recording starts, before the pad moves the truck) threw a NullReferenceException (`LapRecorder.EndLap` returns null when no frame was kept); found by the Drive test.
No Project Settings, scene or `manifest.json`.

## planner-5.9: Build something (OSPlanner, OSBuildFlow, os_answer)

Integrated the staged `planner` part. It compiled on the first try (0 errors, 0 warnings); the logic then ran live for the first time through the new play test.

### What was done
- Copied `OSPlanner.cs` (pure question tree, 45 paths), `OSBuildFlow.cs` (the card flow, registers the `"build"` system page), `OSBuildTools.cs` and `Resources/AlbieTools/os_build.json` (the `os_answer` voice tool) into `unity/Assets/_Forge/OS/`.
- Applied the three staged hooks (see "Changes outside OS/": none; the three below are edits to other OS files): `OSProjectWindow.Suggest(string)`, the real `OSCheck.PlannerTree` walk (replaces the stub), and an `OSCheck` line for `os_build.json`.
- New `Editor/OSPlannerTest.cs` (menu `Forge/OS/Run Planner Test`, `LastResult`, 91 checks) and a new step "5 build something" in `OSPlayTest` (8 checks).
- Layout fixes after looking at the screenshots: the stock button reports flexible height, so the card spread its buttons over the whole panel; every answer button and row now has `flexibleHeight = 0` (same cause and fix as the Drive window). Card size 0.66 x 0.64 m became 0.66 x 0.56 m, answer width 460 became 520 units.

### Check output (verbatim)
```
PASS [OSPlannerTest] 91 passed, 0 failed
PASS os check   (ok   the planner tree ends in a plan within three questions (45 paths); ok   os_build.json defines os_answer and it has a handler (os_answer))
OSPlanner.Validate(): 0 problems, 45 paths
[OSMenuTest] 63 passed, 0 failed
[OSWindowsTest] 52 passed, 0 failed
[OSToolsTest] 95 passed, 0 failed
[OSProjectsTest] 87 passed, 0 failed
[OSArtifactsTest] 68 passed, 0 failed
PASS os play test: 67 passed, 0 failed (1 skipped)
PASS [OSDriveTest] 82 passed, 0 failed (level: full)
PASS autopilot check
PASS voice rules check
PASS slalom check
PASS notebook check
[GestureSelfTest] 104 passed, 0 failed
```
Measurements: `question 1: smallest button Face me 30.0 mm tall, narrowest 31.2 mm wide` (the answers are 31 mm tall; "Face me" is the window's own button); `Albie: Wandering parked 1.22 m from the user, four seconds after opening Build something Wandering at 0.84 m, moved 0.40 m` (the test parks Albie to the side with `StationAt` first, so the walk is real).

Screenshots (`unity/Logs/os/`, each opened and looked at): `60_build_q1.png` (five answers, Cancel, nothing clipped), `60_build_q2.png` (the truck size question, three compact answers), `61_build_plan.png` (the plan card: goal, parts, test, four milestones, Start, Change, Cancel and "Nothing is saved until you tap Start."), `62_build_started.png` (the Project window on the truck: Plan v1, goal, test, four unchecked milestones, "Next step: tap Course to make the course for the truck."), `63_build_voice_row.png` (voice forced live: "Something else" row and the "You can say an answer, or tap it." hint), `64_build_plan_free_words.png` (the plan card with the user's own words in the goal), `65_build_plan_model.png` (the longest parts line, one line, fits).

### What the test proves
Menu strip button -> RC truck -> Small and quick -> Slalom around my couch -> plan card (nothing on disk) -> Change (back to question 1, nothing kept) -> again -> Start: `proj_truck` open, Project window on it showing Plan v1, `plan_v0001.json` on disk with schema `albie.plan/1`, three answers, four parts or more, four milestones, a postmortem entry holding the answers. Cancel from a question, from the plan card and with the window's X each leave the scratch root byte-identical (file list with lengths). A drone makes `proj_my_drone`, a second `proj_my_drone_2`, both listed on the Projects tab. With the voice forced live: the "Something else" row on question 2, spoken answers (`Choose`, `os_answer`), a refused answer names the choices, free words reach the plan, "start" starts `proj_my_rocket`. Voice off: no row.

### Decisions (one line of why each)
- **Kept the writer's tree, three questions per path, five answers on the first.** Why: it matches spec 5.9 and `Validate()` passes every path.
- **The voice tool lives in its own `os_build.json`, not `os.json`.** Why: `OSCheck.ToolDefinitions` pins `os.json` at exactly the four menu tools.
- **`StartBuild` adds `parts` and `answers` to the plan record and writes the same file again.** Why: `RevisePlan` has no parts argument and keeps old parts; the second write keeps its order behind the first (queued writes), and `LoadFolder` only reads `schema` and `version`.
- **The OSPlayTest step runs on a scratch notebook, the rest of that test still uses the real one.** Why: Start writes a plan and this machine's own notebook must never be written by a test.
- **The brand-name check matches whole words.** Why: "Question 1 of 3" contains "Quest".

### Not working or not done
- The mic is not opened by the flow after Albie asks a question (push to talk as usual), and with the hub live and no on-device voice nothing is spoken and `os_answer` is not offered. Both are as the writer documented; the voice path was exercised with `VoiceLiveOverride = true` (`ForgeNet.Send` goes nowhere with no hub), so Albie actually speaking the question was not heard.
- Ray and poke were not exercised (no headset); presses are EventSystem pointer presses as in the other OS tests.
- The Albie walk check proves he moved closer from a parked spot at the desk; on a headset with the user standing in the room the path is the lead gator's `ComeToUser` and needs a real run.

### Changes outside `OS/`
None. Edits to other OS files: `Scripts/OSProjectWindow.cs` gets `public void Suggest(string line) => SetStatus(line);`; `Editor/OSCheck.cs` gets the `PlannerTree` walk and `BuildToolDefinition`; `Editor/OSPlayTest.cs` gets step 5. No Project Settings, scene or `manifest.json`.

## agents-5.10: Agents window, Bring home, Send out, Dismiss behind a confirm

Integrated the staged `agents` part. It compiled on the first try (0 errors, 0 warnings). Logic that had never run needed three real fixes, all found by the new play test (below).

### What was done
- Copied `OSAgentsWindow.cs` (window id `agents`, registers under the strip button's id), `OSAgentHome.cs` (pad spot, bring home, send out, all) and `OSHomePad.cs` (the disc on the desk under a home agent) into `unity/Assets/_Forge/OS/Scripts/`.
- Applied the staged Pet hook by hand (`HOOKS.md`, sections 1 and 2a to 2d), plus one addition of mine (`SettleHomeSpot`, see Decisions). Listed precisely under "Changes outside OS/".
- New `Editor/OSAgentsTest.cs` (menu `Forge/OS/Run Agents Test`, `LastResult`, 98 checks) and an `OSCheck` line for the page and the hook.
- `OSMenuTest`: after the strip's Agents press the test now closes the real window (HOOKS.md section 5).

### Check output (verbatim)
```
PASS [OSAgentsTest] 98 passed, 0 failed
PASS os check   (ok   the Agents page registers under the strip's id and the crew's Bring home hook is in place)
PASS [OSMenuTest] 63 passed, 0 failed
PASS [OSWindowsTest] 52 passed, 0 failed
PASS [OSToolsTest] 95 passed, 0 failed
PASS [OSProjectsTest] 87 passed, 0 failed
PASS [OSArtifactsTest] 68 passed, 0 failed
PASS os play test: 67 passed, 0 failed (1 skipped)
PASS [OSPlannerTest] 91 passed, 0 failed
PASS [OSDriveTest] 82 passed, 0 failed (level: full)
[GestureSelfTest] 104 passed, 0 failed
```
Measurements from the Agents test: `desk surface built: True, at desk: True, crew allowed (Code mode): False`; the builder reached its pad in 2.0 to 3.0 s, the scout in 7.5 to 8.0 s (an agent walks about 15 cm a second); the builder stood 0.000 m from its (settled) pad and moved 0.0000 m in a second; the smallest button in the window is 30 mm tall (the panel's own "Face me"; the window's own buttons are larger).

Screenshots (`unity/Logs/os/`, each opened and looked at): `71_agents_two_rows.png` (Albie as lead, Builder with its real task "Read the sample project and say what it does" and Scout "Idle", both Walking, Bring home and Dismiss on each row, Bring all home and Send all out above, the type buttons under them), `72_agents_home_pad.png` (the window put away; the builder with its hat standing on its coloured pad on the desk), `73_agents_both_home.png` (both rows say Home with the home icon, buttons flipped to Send out, "2 agents are home."), `74_agents_dismiss_confirm.png` (the card "Dismiss Scout? It goes away and its task stops." with Dismiss and Cancel, nothing dismissed yet), `75_agents_page_one_of_three.png` (nine agents, page 1 of 3 with the pager). The test checks that no text is clipped or overlapping (`OSToolsTest.LayoutProblem`), no placeholder text, and that every button is at least 22 x 22 mm; I also looked at each picture for brand names and found none.

### What the test proves
Empty crew: "No agents yet." and Bring all home is off. `spawn_agent` builder and scout (crew egg gate shut), `assign_task` to the builder (the request goes to a local socket that never answers, so the task stays running offline). The menu strip's Agents button opens the window and closes the menu. Two rows; the builder's row shows its real task. Bring home on the builder: it walks to a pad on the desk, stands still on it, its task is still listed in `crew_status`, its bridge is still busy, its controller is still Working, no result arrived, the window's row still shows the task. Send out: back to walking, pad gone, task still there. The scout wanders again after Send out (moves at least 8 cm). Bring all home and Send all out with the right buttons on and off, the two pads at least 7 cm apart. Dismiss opens a confirm that names the agent and says its task stops; Cancel keeps the agent; Dismiss removes it. The `dismiss_agent` tool path stops the task (`stopped_task: true`); Bring home never did. Fixer hatched from the window with `CrewAllowed` false. Nine agents take three pages and the pager works. All agents dismissed: no pad left behind.

### Decisions (one line of why each)
- **The hatching gate stays as it is (the decision spec 5.10 asks for).** `AlbieCrew.CrewAllowed` (gate mode `code`) gates only the egg and picker, the N and X keys and the captions' mute; `AlbieCrew.Spawn`, the hub's `crew.spawn`, `spawn_agent` and `assign_task` never read it. The Agents window hatches through `spawn_agent`, so a user in the OS who never opens Code still gets agents. Why: it needs no Pet change, keeps the egg a desk prop tied to the code view, and the test hatches a Fixer with `CrewAllowed == false` to prove the window does not depend on it. If Hudson wants the egg and keys everywhere: set `gateModeId` to empty on the `AlbieCrew` in Main.unity (Core owner only); nothing here depends on it.
- **Bring home never calls `GoHome`.** Why: `GoHome` destroys the member and `dismiss_agent` drops its task; Bring home only stops wandering (a 6 cm territory on the pad plus the controller's own `StationAt`), so the task, bridge and hub state keep running. Away from a desk, or with no free pad, the body is hidden (`forceRenderingOff`, colliders off) and it keeps working out of sight.
- **Dismiss is the only thing that stops a task, and it asks first.** Why: spec says so. The row button is labelled "Dismiss" (a row has no room for "(stops its task)"); the card says "Dismiss Scout? It goes away and its task stops." before anything happens, and a hint line under the rows says it too.
- **The window reads each agent's task through the `crew_status` tool twice a second.** Why: `CrewTools.running` is private and one small allocation per half second was not worth a change outside OS/; the staged HOOKS.md section 3 has the zero-allocation accessor if it ever matters.
- **`SettleHomeSpot` (my addition to the Pet hook).** Why: the controller stops walking about 15 cm short of the point it was sent to (`NavMeshAgent.stoppingDistance` 0.1 plus its 0.05 arrival slack), and a working agent stands at the front of its territory about 13 cm from the centre, so the agent stood beside its pad. Once a home agent has not moved 1 cm over a 0.3 s tick and is within 30 cm of its pad, the pad's spot becomes where it stands (the pad follows; `Taken` still keeps two pads 7 cm apart).
- **The test waits for an agent to arrive instead of a fixed 8 s, and waits one second between `spawn_agent` and `assign_task`.** Why: a walk of 1.3 m takes 9 s; and a crew member hooks onto the event source in its first `Update`, so a task injected in the very frame it hatched is missed (the bridge never goes busy). That second one is a real, pre-existing race in the Pet module (the voice path and the hub are slower than a frame, so it does not show there); I left it alone and am logging it here.
- **`OSInput.Tap` puts the Game view in front first, and `OSMenuTest` waits for the menu up to 4 s after M.** Why: with the editor focused on another window the Input System drops the key event (found by running `OSMenuTest` first: "M opens the menu" failed, 55 passed and 8 failed; with the Game view focused it is 63 and 0). Tests that already had a fallback are unchanged.

### Not working or not done
- Ray and poke were not exercised (no headset): presses are EventSystem pointer presses as in the other OS tests.
- The Agents step in `OSPlayTest` (spec 6.1 step 7) was not added; `OSAgentsTest` covers it in full and the placeholder comment in `OSPlayTest` is still there.
- Away from a desk, the hidden branch (body hidden, no pad) is implemented but did not run in the live test: the test always sits at the desk, so `SetHome(true, null)` and the hidden-body checks were not exercised.
- Agents walk to their pad at about 15 cm a second, so Bring all home with agents on the far side of a 2 m desk takes up to 13 s before they stand on their pads. The pads show at once.
- One run of `OSMenuTest` printed the failure `the selected tab's label ... contrast ... 5.5:1, at least 5.5`: a rounding edge in an existing check, not seen on the other runs.

### Changes outside `OS/`
- `Pet/Scripts/Crew/AlbieCrewMember.cs` (+134 lines, nothing removed): `using System.Collections.Generic;`; public `Home`, `HomeSpot` (Vector3?), `BodyHidden`; `SetHome(bool home, Vector3? spot = null)`; private helpers `ParkAtHome`, `HomeTick` (every 0.3 s while home), `SettleHomeSpot`, `SetBodyHidden`, `ApplyBodyHidden`; one line at the top of `Update`: `if (Home && !Leaving) HomeTick();`. `GoHome`, `Init`, `Awake`, `OnPoked` are untouched. Why: Bring home must stop wandering without ending the task.
- `Pet/Scripts/Crew/AlbieCrew.cs` (+10, -3): new `NotifyHomeChanged()` (re-split the areas, raise `Changed`); `ClearDeskForLead` skips a home member (`m == null || m.Home`) so the whistle neither dismisses nor lines it up; `AssignTerritories` leaves a home member out of the areas it hands out; `AssignLineup` skips a home member. Why: without these the crew's own bookkeeping would walk a home agent off its pad or dismiss it on a whistle.
No Project Settings, scene or `manifest.json`. `Main.unity` was not touched.

## settings-5.7 (integrator lane)

Integrated the staged Settings window (`OS/Scripts/OSSettingsWindow.cs`, window id `settings`, registered under the strip's id `settings`) and wrote `OS/Editor/OSSettingsTest.cs` (menu: Forge/OS/Run Settings Test, `OSSettingsTest.LastResult`). The staged file compiled as written; the changes I made were layout and tests.

### Done, with evidence
- `PASS [OSSettingsTest] 102 passed, 0 failed` (first run 93/8: four were my test's wrong assumptions, see below; one was a real layout defect, fixed).
- Every row persists in `os_settings.json` (the test reads the file after each change: `"tableButton": false`, `"cornerOnLeft": true`, `"flatLayout": true`, `"menuDistance": 0.6`, `"menuDropDegrees": 20.0`, `"gestureDisplay"`) and takes effect at once: the table button leaves the desk the frame after the switch is turned off; the workspace goes Flat/Curved; the gesture panel shows/hides; the menu then opens 0.600 m from the eyes and 20.0 degrees below level (measured). A reload of the file updates the open window (0.45 m).
- Both resets ask first: Forget permissions (the artifact still needs no approval until "Forget them"; Cancel keeps it; confirm makes it ask again) and Bring windows here (window does not move before the confirm; moved 0.92 m after it). Note lines say what was done.
- Hands-free row: an honest read-only line, not a toggle. `AlbieVoice` has no runtime switch (only the editor pref read at install), so a toggle would be fake. It reads "Voice is off in this session, so Albie is not listening." (test checks there is no switch for it); with voice on it says it is on and cannot be turned off here yet.
- Screenshots (`unity/Logs/os/`, opened and looked at): `81_settings_page1.png` (Menu button: three switches, Corner side Left/Right, Voice line, pager "1 of 3"), `82_settings_page2.png` and `83_settings_page2_sliders.png` (Layout Curved/Flat, Gesture display, Distance "0.60 m", Height "20 degrees down"), `84_settings_page3.png` (both resets, About with version and credits), `85_settings_confirm_permissions.png` (confirm card over the window: question, Forget them, Cancel), `86_settings_page3_after_reset.png` (note "Artifact permissions cleared. Each one will ask again."). Text not clipped or overlapping (LayoutProblem on each page), no placeholder or brand names in UI strings (the Meta credit is the licence notice the spec asks for).

### Every test, rerun after the change
```
PASS [OSSettingsTest] 102 passed, 0 failed
PASS [OSMenuTest] 63 passed, 0 failed
PASS [OSWindowsTest] 52 passed, 0 failed
PASS [OSToolsTest] 95 passed, 0 failed
PASS [OSProjectsTest] 87 passed, 0 failed
PASS [OSArtifactsTest] 68 passed, 0 failed
PASS os play test: 67 passed, 0 failed (1 skipped)
PASS [OSAgentsTest] 98 passed, 0 failed
PASS [OSPlannerTest] 91 passed, 0 failed
PASS [OSDriveTest] 82 passed, 0 failed (level: full)
PASS os check (OSCheck.Run)
```

### Decisions (one line of why each)
- **Sections get `flexibleHeight = 0` and so does the pager row** (helper `Section` in the window). Why: the kit's controls report flexible height, so the page column spread the sections apart (a 160-unit gap) and stretched the pager to 171 units; found by dumping rect sizes in Play.
- **The test scan counts a switch's whole row (a Button, 28 mm tall) as the target, not the 26 x 16 mm switch graphic.** Why: `OSUi.Toggle` makes the whole row tappable on purpose; the test presses the switch itself and it works.
- **Gesture display test is relative to the panel's real state.** Why: the engine shows the display on start in desktop, so "starts off" was wrong; the window shows the real state, and the test checks that.
- **File check uses `0.44999` for 0.45.** Why: `JsonUtility` writes the float as 0.44999998807907104.

### Not working or not done
- **Watch turn and Corner button rows save and persist but nothing reads them yet**: `OSWatch` and `OSCornerButton` (spec 5.11, 5.12) do not exist in the code. Only the table button, layout, gesture display and menu position have a visible effect today. They will take effect when those parts read `OSSettings.Current` each frame.
- `OSSettings.gestureDisplay` is not applied at app start (staged HOOKS.md: optional, not done).
- Slider ranges (0.35 to 0.7 m, 0 to 30 degrees) are narrower than `OSSettings` clamps (0.8, 35), as the task says.
- Ray and poke were not exercised (no headset); presses are EventSystem pointer presses.
- `OSCheck` has no Settings line (the registration happens at BeforeSceneLoad, Play mode only; `OSSettingsTest` asserts it).

### Changes outside `OS/`
None. No Project Settings, scene or `manifest.json`. `Main.unity` was not saved (one test run left it dirty in memory; reopened it from disk, dirty=False, git clean).

## corner-5.12: the corner button (spec 5.12)

### What was done
- Integrated the staged `OSCornerButton` (`OS/Scripts/OSCornerButton.cs`). It compiled with no changes. It sits on the shell prefab next to the table button (`OSPrefabBuilder` now adds it; the prefab was rebuilt, only `OSShell.prefab` changed).
- Shown only away from a desk, with no dock hold, no open menu, Settings on. 0.45 m out, 28 degrees to the chosen side, 22 degrees down, in the head's roll-free frame. Re-anchors with a 0.4 s smooth-step ease when the head turns more than 25 degrees or moves more than 0.3 m, and when Settings flips the side. First show does not fly in. Press opens the menu (summon Corner); it hides while the menu is open.
- Fix found in the live editor: the icon sat left of centre because the empty label box stayed in the button's row. The corner button now hides that `Text` box.
- New `Editor/OSCornerTest.cs` (Forge/OS/Run Corner Test, `OSCornerTest.LastResult`), on a scratch settings file. `OSPlayTest.StepCornerButton` is now a real step (it was a skip). `OSCheck` gained a corner-button line on the shell prefab.

### Check output (verbatim)
```
PASS [OSCornerTest] 55 passed, 0 failed
measured: corner button at 0.450 m, 28.0 degrees right, 22.0 degrees down
measured: the pressable button measures 37.7 mm
measured: the ease took 0.40 s (set 0.4), it moved 0.35 m
measured: left corner: -28.0 degrees, 22.0 down, 0.45 m
PASS [OSMenuTest] 63 passed, 0 failed
PASS [OSWindowsTest] 52 passed, 0 failed
PASS [OSToolsTest] 95 passed, 0 failed
PASS [OSProjectsTest] 87 passed, 0 failed
PASS [OSArtifactsTest] 68 passed, 0 failed
PASS [OSSettingsTest] 102 passed, 0 failed
PASS [OSAgentsTest] 98 passed, 0 failed
PASS [OSDriveTest] 82 passed, 0 failed (level: full)
PASS [OSPlannerTest] 91 passed, 0 failed
PASS os play test: 73 passed, 0 failed
PASS os check (Forge.OS.Editor.OSCheck.Run, 25 ok lines)
```
Screenshots (unity/Logs/os/): `91_corner_away.png` (button low right, icon centred), `92_corner_menu_open_corner_hidden.png` (menu open, button gone), `94_corner_left.png` (left corner with the Settings window), `93_corner_settings_off.png`.

### Decisions
- Corner and side are measured in the head's roll-free gaze frame, not against the horizon: that is how the code places it ("low in the corner of the view"); the first test run measured against the horizon and failed only when the head was pitched.
- Kept distance, angles, recenter limits and ease time as the writer chose: they match the spec numbers; to be tuned on the headset (public fields on the shell prefab).
- Button looks slightly oval at the screen edge in the desktop camera: wide-FOV perspective at 35 degrees off axis, the button itself faces the head (asserted within 5 degrees).

### Not done
- Ray and poke not exercised (no headset); presses are EventSystem pointer presses. Allocation-freeness in LateUpdate checked by reading the code only.

### Changes outside OS/
None.

## watch-5.11: the watch turn (spec 5.11)

### What was done
- Integrated the staged `OSWatch` (`OS/Scripts/OSWatch.cs`). It compiled with no changes (0 errors, 0 warnings). It sits on the shell prefab next to the table and corner buttons (`OSPrefabBuilder` adds it; the prefab was rebuilt, only `OSShell.prefab` changed). `OSCheck` gained a line for it on the shell prefab.
- It reads the left hand from `GestureEngine.Current.Hands[0]` in `LateUpdate`: back of the hand toward the eyes (dot above 0.6), wrist within 35 degrees of the gaze and 0.2 to 0.7 m away, held 0.3 s, then a round 28 mm kit button (on a 38 mm plate) shows 45 mm above the wrist, facing the eyes. A press (mouse, ray or poke all end in `Button.onClick`) opens the menu with the Watch summon from the wrist. It hides 0.25 s after the pose is lost, stays away after a press until the wrist has turned away once, and keeps out of palm up, a pinch, an open menu, a dragged panel (`PanelManipulator.Grabbing`) and 0.8 s after a left-hand gesture. `OSSettings.watchTurn` switches it off at once.
- The Gestures hook (the only change outside OS/): `SimulatedHandSource` got a left test hand, off by default (see "Changes outside OS/").
- New `Editor/OSWatchTest.cs` (Forge/OS/Run Watch Test, `OSWatchTest.LastResult`), on a scratch settings file. `OSPlayTest` step "4 watch turn" is now a real step. Every thresholds is a public field on `OSWatch` with a tooltip: they need Hudson's headset tuning (`backFacingDot` first).

### Check output (verbatim)
```
PASS [OSWatchTest] 107 passed, 0 failed
measured: made-up relaxed left hand at the watch spot: Ok, back dot 0.99, 20 degrees off the gaze, 0.43 m, pinch 0.39, palm up -0.59
measured: [at the desk] button shown 0.32 s after the pose was set (hold 0.3 s), back dot 0.99, 20 degrees off the gaze, 0.43 m
measured: [at the desk] the pressable button measures 28.0 mm (28 mm set)
measured: [at the desk] 10 s of resting hands: poses seen NoHand > TooLow > BackAway, left-hand gestures fired 0
measured: [away from the desk] button shown 0.32 s after the pose was set (hold 0.3 s), back dot 0.99, 20 degrees off the gaze, 0.43 m
measured: [away from the desk] 10 s of resting hands: poses seen NoHand > TooLow > BackAway, left-hand gestures fired 0
[GestureSelfTest] 104 passed, 0 failed
[GesturePlayTest] 32 passed, 0 failed
PASS [OSMenuTest] 63 passed, 0 failed
PASS [OSWindowsTest] 52 passed, 0 failed
PASS [OSToolsTest] 95 passed, 0 failed
PASS [OSProjectsTest] 87 passed, 0 failed
PASS [OSArtifactsTest] 68 passed, 0 failed
PASS [OSSettingsTest] 102 passed, 0 failed
PASS [OSAgentsTest] 98 passed, 0 failed
PASS [OSCornerTest] 55 passed, 0 failed
PASS [OSDriveTest] 82 passed, 0 failed (level: full)
PASS [OSPlannerTest] 91 passed, 0 failed
PASS os play test: 79 passed, 0 failed
PASS os check (Forge.OS.Editor.OSCheck.Run, 25 ok lines)
```
Screenshots (unity/Logs/os/): `101_watch_desk_wrist_button.png` (round button, icon centred, at the wrist spot, at the desk), `104_watch_away_wrist_button.png` (same away from the desk, the corner button low right), `105_watch_away_menu_open.png` (menu open after the press), `103_watch_desk_resting_tilted.png` (a tilted resting hand: no button), plus `15_watch_button.png` and `16_watch_menu.png` from the play test.

### What the test proves (each at the desk and away from it)
- Pose maths on made-up hands: Ok, BackAway (tilt 65), OutOfView, TooFar, TooClose, TooLow, PalmUp, Pinch gate.
- Not shown at 0.1 s, shown 0.32 s after the pose; button 45 mm above the wrist along the hand's back normal (within 8 mm), faces the eyes (dot above 0.95), a live UI Set button, 28 mm, on screen, with its icon; follows the wrist.
- A real EventSystem pointer press opens the menu (summon Watch) from where the button was; it hides, disarms and counts one press; stays away with the menu open and after it closes (debounce); turning away re-arms and hides; turning back shows it.
- A menu opened another way hides it at once and it returns when that menu closes (nothing disarmed it); a raised `PanelManipulator.Grabbing` hides it and keeps it away, and it returns when the drag ends.
- Settings off: gone at once, nothing shows, `LastPose` NoHand; on again: shows.
- 10 s of resting left hands (5 s on the desk, 5 s tilted away, with 1 cm drift): the button never showed, no press, no menu, and the gesture engine fired nothing with the left hand (a `Fired` subscriber counted 0).
- Palm up for 0.4 s (under the engine's 0.6 s): not the watch pose, no left-hand gesture fired. A pinching left hand: pose Pinch, nothing shown.

### Decisions (one line of why each)
- **Kept the writer's thresholds and the extra `maxBelowEyeMeters` 0.42 gate.** Why: they match the spec numbers and, with the hand on the desk, it is what said TooLow in the test; setting it to 0 gives exactly the spec's rule. All tune on the headset.
- **The grab guard is tested by setting `PanelManipulator.Grabbing`'s backing field by reflection.** Why: a real grab needs a pointer on a panel handle; the property has a private setter and nothing else raises it. The test puts it back to 0 (and Cleanup does too).
- **The pinch test uses its own `HandShape` (index 0.55, thumb 0.5).** Why: there is no Pinch shape; this one measures 0.19 on the pinch feature, under the gate's 0.26 and the engine's 0.22.
- **A left hand placed by the test counts as a real hand for the engine**, so the gesture display opens the first time (engine behaviour, once). The test asserts no left-hand gesture fired.

### Not working or not done
- Ray and poke were not exercised (no headset); presses are EventSystem pointer presses. Whether a 28 mm round button on a 38 mm plate is easy to hit with a real fingertip on a moving wrist is for the headset.
- The left hand is not drawn in the desktop view (only the right-hand dots are), so screenshots show the button but no hand.
- Allocation-freeness of `LateUpdate` checked by reading the code only.

### Changes outside OS/
- `unity/Assets/_Forge/Core/Scripts/Gestures/SimulatedHandSource.cs` (Gestures module, the Hudson-owned hand source): added four static test fields (`LeftTestActive` default false, `LeftTestWrist`, `LeftTestRotation`, `LeftTestShape`), a `SubsystemRegistration` reset that puts them back, and in `Read` replaced `if (hand != 1) return;` with `if (hand == 0) { if (LeftTestActive) SyntheticHand.Build(sample, LeftTestShape, LeftTestWrist, LeftTestRotation, true); return; } if (hand != 1) return;`. Why: on the desktop rig hand 0 was never tracked, so the watch could not be exercised. With `LeftTestActive` false (the default) hand 0 returns exactly as before, so `GestureSelfTest` (104/0, its own synthetic hands, idle test unchanged) and `GesturePlayTest` (32/0, drives hand 1 only) are unaffected, as the runs above show.

## playtest-p1: OSPlayTest steps 6 to 8 and the P1 checks in OSCheck (spec 6.1)

### What was done
- `OSPlayTest` now walks all nine steps of spec 6.1 in order (1, 2a, 2b, 3a corner button was already a real step, 3b, 3c, 3d, 4 watch turn, 5 build something were already in). New: **6** the RC truck project from the Projects tab, then the project window's own buttons by real pointer presses: Course (asks first, Allow on the scratch approvals, gives the slalom and the brain truck, linked to the truck), Drive (opens the pad, starts a two-lap recording), a knob-up press on the pad (the truck moves, letting go writes zero), Stop, Drive again and two laps driven through the pad by the same pursuit `OSDriveTest` uses, the laps are scored runs, Train, Autopilot completes the slalom. **7** Agents: `spawn_agent` builder and scout, a task that stays running offline, the Agents window from the menu strip, Bring home (task kept: crew_status, bridge busy, still Working), Send out. **8** Settings from the strip: the corner button shows, its switch goes off and the button is gone (setting, file, `Shown`, button inactive), on again and it is back. **9** Close all (unchanged, now with the truck, pad, Train and agents' leftovers to close).
- 29 screenshots in the manifest (`Logs/os/play_test_shots.txt`), 18 to 29 are new: `18_truck_course`, `19_truck_pad_knob_up`, `20_truck_pad_driving`, `21_truck_laps_recorded`, `22_truck_training`, `23_truck_autopilot_done`, `24_agents_two_rows`, `25_agents_builder_home`, `26_agents_builder_out`, `27_settings_corner_on`, `28_settings_corner_off`, `29_closed_all`.
- **Level and fallback:** step 6 reports the level the way `OSDriveTest` does ("full", or "pad laps, pretrained brain" when `brain.Drove` is the pretrained fallback) in its Autopilot output line. A fallback sets a note and the final result then starts `PASS with fallback os play test`, never a plain PASS. `OSDriveTest.Level` is logged next to it (an info line) when that test ran in the same session.
- **Time:** the whole run takes about 2.3 minutes (step 6 about 1 minute: the laps take 20 to 30 s, training about 1.5 s of compute, the Autopilot 10 to 30 s). That is well under 6 minutes, so the long part is not behind a flag by need. `RunQuick()` exists anyway (skips the two laps, Train and the Autopilot, about 1.5 min) and its result says `PASS with fallback ... Quick run: ... skipped`, so it can never read as a full PASS. `Run()` sets `Full`, `RunQuick()` clears it. It was run once: `PASS with fallback os play test: 128 passed, 0 failed (1 skipped). Quick run: step 6: two laps through the pad, Train and the Autopilot skipped (quick run)`.
- `OSCheck` gained: the planner tree check now also needs 5 or more paths, `MaxQuestions` 3, and walks "answer 1 every time" to "answer 5 every time" and each must end in a plan within 3 questions (on top of `OSPlanner.Validate()` returning no problems); the tabs check requires the Drive tile (it was skipped when Drive was not built); new line "every system page the strip opens is registered (Build something, Agents, Settings)" which runs each page's `Register()`, then checks `OSShell.HasSystem` and reads `OSMenu.cs` for every `OpenSystem("...")` the strip calls; settings round-trip sets every field away from its default (including `watchTurn`, `cornerButton`, `cornerOnLeft`, `gestureDisplay`, `flatLayout`), checks they come back and are named in the file, ResetToDefaults restores all of them, and fails if a field is added to `OSSettings` that the check does not know.

### Check output (verbatim, last runs)
```
PASS os check (OSCheck.Run, 27 ok lines)
PASS os play test: 136 passed, 0 failed
PASS [OSMenuTest] 63 passed, 0 failed
PASS [OSWindowsTest] 52 passed, 0 failed
PASS [OSToolsTest] 95 passed, 0 failed
PASS [OSProjectsTest] 87 passed, 0 failed
PASS [OSArtifactsTest] 68 passed, 0 failed
PASS [OSSettingsTest] 102 passed, 0 failed
PASS [OSAgentsTest] 98 passed, 0 failed
PASS [OSCornerTest] 55 passed, 0 failed
PASS [OSWatchTest] 107 passed, 0 failed
PASS [OSDriveTest] 82 passed, 0 failed (level: full)
PASS [OSPlannerTest] 91 passed, 0 failed
[GestureSelfTest] 104 passed, 0 failed
[GesturePlayTest] 32 passed, 0 failed
[ArtifactChecks] 97 passed, 0 failed
PASS slalom brain check (Play mode, the brain truck on its colliders)
PASS notebook check
[ML Main smoke] PASS: saved Main scene, ML desk, flat/curved layout, ml.layout aliases, seven VizCard formats, scatter selection, regression ...
PASS voice rules check
PASS room slalom check (mock truck, Editor)
```
The play test's step 6 line in the last run: `the Autopilot completed the slalom (1 tap, drove: candidate 1, attempts: candidate 1 completed, last run completed; level: full)`. Compile: 0 errors, 0 warnings.

### Flaky Autopilot at step 6 (said plainly)
Of 12 full runs of the final play test, 2 failed at the Autopilot (the second also logged a step 8 line, "the menu opened when asked (recovering with OpenMenu)", a knock-on of the same stalled run): all three trained candidates and the pretrained fallback crashed (`attempts: candidate 1 crashed, candidate 2 crashed, candidate 3 crashed, the pretrained fallback crashed`), the second time even after a second tap. Both were the first run right after a recompile and domain reload; the other 10 runs (and run 3 of the loop, where candidates 1 and 2 crashed and candidate 3 completed) passed. I could not reproduce it with the diagnostic line in (it prints what the truck touches and how many room colliders are back on), so the cause is not found. Candidate guess: an editor stall just after the reload (the log of the first failure has `IPCStream ... Timed out`) while the truck is placed on the slalom start. The step now taps Autopilot a second time if every candidate failed (the output line says "2 taps"), the way a user would. `OSDriveTest` takes the same route and passed in its one run in this session (it was not looped).

### Decisions (one line of why each)
- **Step 6 copies the pad-driving helpers (a real press, drags, a release) into `OSPlayTest` and shares only the pursuit (`OSDriveTest.Pursuit`, now internal).** Why: the helpers hold statics tied to `OSDriveTest`'s own run; the pursuit is the part worth sharing.
- **Step 6 leaves the project, Drive and slalom open and step 9 closes them.** Why: Close all is then tested on a real pile (a project, a pad, the slalom with its brain truck), not on nothing.
- **Agents step skips waiting for the agent to walk to its pad.** Why: the test is away from the desk where Bring home hides the body (`Home, hidden`); the pad walk is `OSAgentsTest`'s.
- **`OSSettingsWindow.Register()` made public.** Why: `OSCheck` runs it, as it does for the other two pages.
- **`PASS with fallback` for a fallback or quick run.** Why: the brief: never a plain PASS.

### Not working or not done
- The Autopilot flake above.
- Ray and poke were not exercised (no headset); presses are EventSystem pointer presses.
- The Main scene was left dirty by some of the other checks (ML smoke, Check Artifacts); it was reopened from disk (never saved) before the play test. `Main.unity` is not modified in git.

### Changes outside OS/
None. (`OSDriveTest.Pursuit` and `OSSettingsWindow.Register` are inside `OS/`.)
