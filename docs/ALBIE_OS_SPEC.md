# Albie OS: build spec for an autonomous run

Written 2026-11-12 for Hudson (Core owner) by a planning session. A fresh Claude Code session, with Fable as the orchestrator, builds from this. Read all of it before you plan. Where this spec and the code disagree, the code wins: note the difference in the run log and carry on.

Deadline context: the Meta VR Start entry is due **2026-11-18, 12:00 PT**. The team plan (root `PROJECT_CONTEXT.md` §16.4) has the video recorded on Nov 16. Hudson tests on a Quest 3S once this run hands back. Finish working software first. Polish comes after that.

---

## 1. Goal

Turn Albie from "four apps on a desk dock" into a platform that feels like an operating system made by Meta, and keep the RC-truck demo working inside it.

When the run ends, Hudson can:
1. Put on the headset, tap a button on his real table (or turn his left wrist, or tap a corner button when he's away from a table), and get a menu that looks and moves like Quest's own system UI.
2. Find everything in three tabs: **Tools**, **Projects** and **Artifacts**. Next to them are **Build something**, **Agents** and **Settings**.
3. Open things as windows that work at a desk and away from it, with several open at once.
4. Run the whole truck demo (root `PROJECT_CONTEXT.md` §15.6) from the menu, **with hands only**: no controllers and no voice required.
5. Install a build that you made and confirmed compiles. You do not install it on the headset; Hudson does.

## 2. Product decisions (settled; don't reopen them)

- **It's an OS, not a desk of apps.** It works with or without a desk. The desk dock's mode rings are retired as the main way in. Every mode becomes a Tool in the menu.
- **Three tabs: Tools, Projects, Artifacts.** The definitions are in `docs/ARTIFACTS_BRIEF.md`.
  - **Tools** are what you work with: **CAD**, **Code**, **Train** (ML and the neural-net trainer), **Notebook**, **Drive** (the hand transmitter, §5.8), **Messages**, **Gestures** (the gesture display and teaching).
  - **Projects** are things you build, with versions and a notebook: **RC truck** (`proj_truck`, the demo project), **Quadcopter**, **Rocket**, **Image classifier** (the first demo's ML project), plus any project made with Build something.
  - **Artifacts** are generated things that run and can link to a project: the **slalom**, **hoop course** and **scoreboard**; the **biology models** (cell, heart, brain, plant cell, eye, DNA); the **double pendulum, solar system and three-body sims**; **"What shipped this week?"** (`repo_activity`). The slalom links to the truck project.
- **Opening the menu:**
  - **At a desk**, a single button sits on the real table (MRUK `TABLE`) and you press it. It replaces the dock.
  - **Away from a desk**, there are two ways, both on by default and each switchable in Settings:
    - **Watch turn:** turn the left wrist as if checking a watch, and a small button appears on the wrist. Tap it with the right index finger.
    - **Corner button:** a small button low in the corner of your view. It lags behind the head; it is never head-locked.
  - It also opens with the desktop key **M**, the voice tool `os_menu`, and the left controller's menu button. Controllers are optional, never required.
- **Hands first:**
  - Anything within reach is **tapped** (poke). Pinch with a ray is only for things out of reach.
  - Every step can be done with hands alone. Voice is a shortcut, never the only path.
  - Index-finger poke only. Targets are at least **22 × 22 mm** with at least **12 mm** between them. The finger stops at the surface (touch limiting), and every press gives visual and audio feedback. This is Meta's direct-touch guidance.
- **The look:**
  - "As if Meta made it."
  - Build on Meta's Horizon OS UI Set, which ships in the Interaction SDK and is already installed. Do not hand-style a new kit.
  - The clickable mock at https://claude.ai/artifact/CvaZfnQhzr4PpCScTW2UBn shows layout and flow only, not the look.
- **Build something** is a button. Albie comes to you and plans with you: one question at a time, every answer tappable, then a plan card you confirm. Nothing is created until you confirm.
- **Agents:**
  - The menu lists your agents.
  - **Bring home** stops an agent walking around **but it keeps working on its task**. This is new: today `dismiss_agent`/`GoHome()` destroys the agent and drops its task.
  - **Send out** reverses Bring home. There is also **Bring all home**.
  - Making your own agent is a stretch goal.
- **Settings** starts small (§5.7).
- **No brand names in the UI.** That includes Slack, VS Code, Meta, Quest and Sketchfab.

## 3. Rules for this run

### 3.1 Git
1. Work in `C:\Users\marrh\GitHub Repos\albie-vr` (the main checkout). Don't make a worktree: the disk has about 21 GB free and a second Unity `Library` costs about 13 GB.
2. `git fetch`. Create **`feat/albie-os`** from `origin/main`. Hudson's local branch `feat/hand-gestures-mach1` is already merged; leave it alone.
3. Hudson asked for this: first merge **`origin/chore/remove-movie-rename-albie`** (D-41, by apscual) into `feat/albie-os`. It removes the movie, VS Code, the screen bridge and the Screens module, and renames Bit to Albie. A trial merge on 2026-11-12 was clean. After merging, open Unity and confirm everything compiles and the existing checks (§6.2) pass before writing OS code. If it doesn't compile, fix it on `feat/albie-os` and log what you changed.
4. Commit small and often, with clear messages. Push `feat/albie-os` to `origin` as you go.
5. At the end, open **one draft PR** from `feat/albie-os` to `main`. **Never merge it**, never push to `main`, and never push to anyone else's branch.
6. Commit this spec and the run log (`docs/ALBIE_OS_RUN_LOG.md`) on the branch.
7. Merge `origin/main` into the branch at least once a day. Satyabrata merges several PRs a day.

### 3.2 Project rules
These are the `CLAUDE.md` rules that matter most here:
- **New code goes in `unity/Assets/_Forge/OS/`** (Scripts, Editor, Resources, Prefabs, README). Touch other modules only where a hook is needed, keep that small, and list every such change in the PR.
- **Don't edit or commit `Main.unity`.** Put the OS into the app the way #59 and #62 did:
  - a static bootstrap with `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]`
  - skip scenes with no `Workspace` (that's how sandboxes are left alone)
  - load the prefab from `Resources`
  
  The template is `Artifacts/Scripts/ArtifactBootstrap.cs`, and `Desk/Scripts/DeskBootstrap.cs` covers setting fields on a prefab before its `OnEnable` runs.
- **Tool registration:** register tools at `BeforeSceneLoad` and reset statics at `SubsystemRegistration`.
- **Project Settings:** Hudson is the Core owner, so a needed change is allowed. For example, `UIThemeManager` registers its `QDSUI*` tags in the TagManager. Keep changes to what's needed and list them in the PR. Don't touch `Packages/manifest.json`.
- **Input:** UI must work with the DesktopRig mouse, Interaction SDK poke and ray. UI Set canvases need a `GraphicRaycaster` for the mouse, as `ArtifactHost.NewPanel` adds one.
- **Performance:** Quest performance rules apply. No allocations or LINQ in `Update`, textures 2048 max, 72 fps on a Quest 3S.
- **Sending:** never send anything, or take an irreversible action, without a confirm tap.
- **Licence:**
  - The UI Set is Meta SDK sample content. The Meta SDK License lets you modify and redistribute sample source if the copyright notice stays.
  - Keep Meta's headers on anything you copy.
  - Add `unity/Assets/_Forge/OS/THIRD_PARTY_NOTICES.md` naming the UI Set and the licence URL (https://developers.meta.com/horizon/licenses/oculussdk/).
  - Prefer referencing the package's prefabs and nesting them over copying their files.

### 3.3 Machine
- **Disk:** check free space on `C:` before any build. Stop and report if it drops under **5 GB**. It filled up once already, on Nov 5.
- **Unity:** use one editor, on `albie-vr/unity`.
  - Drive it with the `unity` CLI (skill `unity-cli`): `unity command eval`, `unity recompile`, menu items, and Play.
  - Batch mode can't run while that editor is open, so run checks in the live editor by calling their `Run()` methods through `eval`.
  - If the editor freezes, look for a modal dialog (low disk space, safe mode, an API updater) before anything else.
- **Building:** build the APK with `QuestBuild.Build` into `Builds/`. Don't install it.

### 3.4 When to stop and ask (otherwise decide, log it and carry on)
- Something would delete data that isn't yours, rewrite history, or push to `main`.
- It needs credentials, keys or accounts.
- Disk is under 5 GB.
- The D-41 merge breaks things you can't fix within the branch.
- A product decision in §2 turns out impossible as written.

Record every other judgement call in the run log with one line of why.

## 4. What exists today: the integration map

This was verified at `origin/main` e69b8db. Paths are under `unity/Assets/_Forge/`. Re-check anything you depend on; the code wins.

### 4.1 Bootstrap pattern
- `Artifacts/Scripts/ArtifactBootstrap.cs:14-71` gives `Ensure()` and `EnsureBrain()`.
- `Desk/Scripts/DeskBootstrap.cs:10-51` gives a prefab under the Workspace with fields set before `OnEnable`.
- Other installers that follow it:
  - `GestureEngine.Install` (`Core/Scripts/Gestures/GestureEngine.cs:49-54`)
  - `CodeMode.Install` (`Code/Scripts/CodeMode.cs:17-21`)
  - `AlbieVoice.Install` (`Core/Scripts/AlbieVoice.cs:100-116`)
  - `WorkspaceRemote.Install` and `MLWorkspaceBridge.Install`

### 4.2 Workspace and the desk: the main constraint
**Workspace** (`Core/Scripts/Workspace.cs`):
- API: `Open(modeId)`, `Close()`, `Toggle`, `ActiveMode`, `Current`, `HoldDock(bool)`, `SetLayout`, `ResetLayout`, `TryGetItemTransform`, `BesidePose`.
- `DefaultModes()` (lines 862-905) defines `ml`, `cad`, `code` and `messages`. D-41 removes `screen-main`; re-read this after the merge.
- **`Open` refuses away from a desk** (lines 191-195). The voice path returns `not_at_desk` (`WorkspaceRemote.cs:50-65`).
- **Only one mode at a time:** `Open` calls `FoldAll()`.
- **Leaving the desk folds everything and hides the dock** (lines 144-156). `WorkspaceExtras` hides its extras, and `GestureWindow` drops its minimise dots.
- Items are parented to `presence.Frame`, so they follow the table.

**DeskPresence:** `State` (Away, Near, Seated), `AtDesk`, `Frame` (the front-edge frame), `FrameWidth`, `FrameDepth`, `EyeInFrame()`, `forceSeated`, and the `stateChanged` and `frameChanged` events.

**DeskLocator:** `Tables`, `NearestTable`, `DeskTop.From(anchor)`.

**DeskDock:**
- `Build`, `Show`, `Hide`, `IsShown`, `TryGetRightSide`.
- `DockRing.Create(parent, label, hue, diameterMm, onClick)` (`DockRing.cs:73,80`) is a button on the desk that already works with mouse, ray and poke.
- Desk space already used: the dock sits on the front-edge centre, the whistle past its right end, and the crew egg on the near-left corner.
- On the first arrival, `ArrivalDirector` holds the dock until START and then opens `code`.

**Panels:**
- **`ForgePanel`:**
  - 1 canvas unit = 1 mm.
  - Members: `Content`, `Title`, `Subtitle`, `Accent`, `SetBadge`, `ShowEmptyState`, `SizeMeters` (set this, not the scale), `ForgePanel.All`, `Handles`.
  - The prefab is `Core/Prefabs/ForgePanel.prefab` and is **not** in Resources. Borrow it from `Workspace.Current.panelPrefab`, or put a reference on the OS prefab.
- **`PanelManipulator.Init(eyes, deskFrame)`, `SetPlacement`, `AddMinimize`:** gestures only target panels whose `Handles` is set and not `Busy` (`GestureEngine.cs:164-168`).
- **A desk-free window example:** `GesturePanel.Create` (`Core/Scripts/Gestures/GesturePanel.cs:53-140`). It's a free root panel with `PanelManipulator.Init(head, frame or head)`, placed 0.95 m from the head.
- **`ForgeVolume`** and **`VolumeManipulator`** are for 3D content.

**Consequence:** OS windows must **not** be Workspace modes, and must not be parented under `presence.Frame`. Build an OS window manager (§5.3).

### 4.3 Notebook (projects)
- `Notebook/Scripts/NotebookStore.cs` is a **single-project static store**.
  - Default project: `"proj_truck"`. `NotebookTools.Boot` opens it at `BeforeSceneLoad`.
  - API: `Open(projectId, root)`, `Close`, `Read(last, maxChars)`, `Log`, `Mark`, `CutVersion`, `RevisePlan`, `CurrentPlan`, `Entries`, `Versions`, `Lessons`, `CompareVersions`.
  - Files live under `persistentDataPath/projects/<id>/…`.
- **No API lists projects, and no notebook panel exists in Main.** You build both.
- **Gotcha:** `ArtifactLibrary.FromTemplate` nulls every link, **including `links.thing = truck`**, when no notebook is open. The brain truck needs that link. Always keep a project open. Opening the truck project must re-open `proj_truck`. Switching projects must never leave nothing open.

### 4.4 Artifacts
**`ArtifactLibrary`:**
- `Templates` covers the slalom, hoop course, repo activity, the biology models, double pendulum and solar system. Three-body may be a separate scene; check.
- Also: `List()` (id, title, version, kind, template), `FromTemplate`, `FromScene`.
- The slalom and hoop course are `space: room` (true scale on the real floor). The others are `space: volume`.

**`ArtifactHost`:**
- Members: `Current`, `Open(id)` (**one artifact at a time; it closes the previous one**), `Close`, `ComeToUser`, `FollowsUser`, `Shown`.
- Its panels have **no `PanelManipulator`**: they can't be moved, and gestures skip them.
- The first open shows an Allow / Not now sheet.

**Tools:** `artifact_list`, `artifact_create`, `artifact_open`, `artifact_close`, `artifact_relayout`, `artifact_run`, `artifact_compare`.
- Open through the same path the tools use (`ArtifactTools`), so `ComeToUser` runs.
- Keeping one artifact at a time is fine for this run.

### 4.5 Truck, brain and controllers
- **Today the truck beat in Main is voice only:**
  - `artifact_create{template:"slalom"}` → Allow → `ArtifactBootstrap.EnsureBrain` spawns the truck (`SlalomBrain`).
  - Then `brain_record{start, laps:2, then_autopilot:true}`.
  - Laps are driven with the **Touch thumbstick** (`Controllers/Scripts/QuestStick.cs`, which reads zero with hands) or WASD.
- **There is no hand transmitter.** Its input contract is `ManualInputs` channels `stick.x/y`, `stick2.x/y`, `hand.roll`, `voice.throttle/steer` (`Controllers/Scripts/ManualController.cs:7-14`).
- Tools: `brain_record`, `brain_train`, `autopilot_on`, `autopilot_off`, `brain_status`, `template_train`, `template_status`, `learned_brake`. Also `ThingHosts.Find("truck")` and the `SlalomBrain` API.

### 4.6 Crew (agents)
**`Forge.Pet.GatorCrew`:**
- Members: `Instance`, `Members`, `Lead` (Albie), `Changed`, `Spawn(type, at)`, `Dismiss`, `ClearDeskForLead`.
- `GatorCrewMember.GoHome()` **says "Bye!", shrinks and Destroys the gator.** Dismiss also cancels its task.

**Other pieces:**
- Types: `GatorAgentTypes.All` (scout, builder, fixer, tester, reviewer, scribe, shipper, designer, sentinel).
- Tools: `spawn_agent`, `assign_task`, `dismiss_agent`, `crew_status`.
- The crew only hatches in the `code` mode (`gateModeId`).

### 4.7 Tools, cards and voice
**`AlbieTools`:**
- `Register(file, handlers)` loads `Resources/AlbieTools/<file>.json`.
- Also `Call`, `CallAsync`, `Has`, `Error`. A clean example is `GestureTools.cs:11-28`.

**`AlbieCards`:**
- `Register(prefix, handler)` and `Tap(actionId)`.
- To show a card, inject `pet.say` with `card{title, actions[{action_id, label}]}`. See `GestureActions.Confirm` (`GestureActions.cs:91-114`).
- Device handlers run only when the hub isn't Live.

**Voice rules:**
- They live in `AlbieVoice.ToolRules` (lines 43-56).
- `Artifacts/Editor/VoiceRulesCheck.cs` fails if any snake_case word there isn't a real tool, argument or template.
- If you add OS rules, add matching `ViewNotes` and `Keyterms`.

### 4.8 Gestures (Mach 1, merged in #19)
- **Built-in gestures** (`GestureLibrary.cs:143-172`):

  | Gesture | Action |
  |---|---|
  | point flick in | `window.summon` |
  | point flick out | `window.minimize` |
  | palm up | `albie.perch` |
  | fist close | `albie.release` |
  | fist open | `display.toggle` |

- **`HandSample`:**
  - Holds joint positions only; there are no rotations.
  - Features include `PalmUp`, `PalmToHead`, `InView` and `FingersUp`. The sample also has `isLeft`.
  - **There is no handedness filter and no wrist-roll feature.**
- **Watch turn:** build it as its own small OS component on the gesture hand source (`IsdkHandSource`), with left-hand-only logic. **Don't** build it as a sixth built-in `GestureDef`, because `GesturePlayTest` asserts exactly 6 `gesture_list` entries and built-ins aren't per-hand.
- **Avoid these poses:**
  - Meta's system pose: palm toward the face, then pinch.
  - `palm_up` (Call Albie) and the fists.
  - The watch pose is the **back of the hand toward the eyes** (`PalmToHead` clearly negative), wrist in view, held about 0.3 s.
- **`SimulatedHandSource` (desktop) simulates the right hand only.** Extend it with a left hand, or add a test hook, so the watch turn can be tested in desktop mode.

### 4.9 Tests and tools you can run
- **Check pattern:** `[MenuItem("Forge/…/Check X")]`, `public static string Run()` that returns "PASS…" or "FAIL…", and `RunBatch()`.
- **Play-mode template:** `Core/Editor/GesturePlayTest.cs`:
  - sets EditorPrefs for forced desktop mode and voice off
  - opens Main and plays it
  - steps an `IEnumerator<float>`
  - forces `presence.forceSeated` and `DesktopRig.Current.ToggleSeat()`
  - screenshots `PetUser.HeadCamera` to `Logs/gestures/*.png`
- **Screenshot helpers:** `ArtifactShot.Take(...)` and `Messages/Editor/PanelShot.Save(...)`.
- **Desktop mode:** "Forge/Desktop Mode (skip XR)". Uses `DesktopRig` and `Core/Rooms/SimLivingRoom.json`.
- **Keys:**

  | Key | Does |
  |---|---|
  | WASD | walk |
  | E | sit |
  | F | whistle |
  | N / X | crew |
  | 1-6 | hand shapes |
  | Q / Y | flicks |
  | **M** | free; use it for the menu |

- **APK:** `QuestBuild.Build`.

### 4.10 Meta UI Set (installed)
- **Location:** `Packages/com.meta.xr.sdk.interaction/Runtime/Sample/Objects/UISet/`.
- **Prefabs:**
  - `Backplate/EmptyUIBackplateWithCanvas` (canvas, backplate, poke, ray)
  - buttons (Primary, Secondary, Borderless, Destructive, TextTile, ButtonShelf, toggles, checkbox, radio, switch)
  - ContextMenu, Dialog, DropDown, Slider, TextInputField and SearchBar, Tooltip
  - `Patterns/ContentUIExample-HorizonOS1-3` and `GridMenuExample2x4/3x3`
- **Theming:**
  - `UIThemeManager` (namespace `Oculus.Interaction`) applies `UITheme` assets by tag. There are `UIThemeQuest_Dark` and `_Light` themes.
  - `ApplyTheme(index)` switches themes. Custom themes are made by duplicating one.
- **Scenes to study:**
  - `UISet.unity` and `UISetPatterns.unity`.
  - Package scenes can't be opened in place; copy one to a scratch folder under `Assets/` that you don't commit.
- **Docs:** https://developers.meta.com/horizon/documentation/unity/unity-isdk-uiset/
- Nothing in `_Forge` uses it yet.

## 5. What to build

Build in priority order. If time runs out, cut from the bottom. Each part lists what "done" means. Every part must run with the hub off and voice off.

### 5.1 Shell and bootstrap (P0)
- **The bootstrap:** `OS/Scripts/OSBootstrap.cs` puts an `OSShell` prefab (from `OS/Resources/`) into any scene with a `Workspace`, the #59 way.
- **`OSShell`:**
  - one per scene
  - state: menu open or closed, current tab, open windows
  - events, and the open, close and toggle API
  - remembers the last tab for the session
- **Voice tools** in `OS/Resources/AlbieTools/os.json`, registered at `BeforeSceneLoad`:
  - `os_menu{open}`, `os_tab{tab}`, `os_open{kind, id}`, `os_windows{action: recenter|close_all}`
  - The tool names must stay real so `VoiceRulesCheck` passes.
- **Done:** Main in desktop mode starts with the shell present and no errors or warnings from OS code; sandboxes are unaffected; **M** toggles the menu.

### 5.2 The menu (P0)
**Shape:**
- A Navigator-style panel built from UI Set parts.
- Tab bar: **Tools · Projects · Artifacts**.
- A grid of tiles: an icon, a title, and one line of status, such as "v2 · slalom passed" or "open".
- A bottom strip: **Build something** (primary button), **Agents**, **Settings**, **Bring windows here**, and **Close**.

**Placement:**
- It opens world-locked where it was summoned, within arm's reach.
- Distance: start at about 0.45–0.55 m from the eyes, 10–20° below eye level, tilted to face the user.
- Measure everything as a parameter so Hudson can tune it on the headset.
- At a desk, it rises from the table button.
- It is never head-locked.

**Motion:**
- About 200–250 ms scale and fade in, from the summon point.
- Hover lift and press depress, from the UI Set animators.
- A soft sound on open, close and press.
- Use audio already in the project or the SDK packages. Add no unlicensed assets.

**Input:** poke is primary (targets of at least 22 mm, with 12 mm gaps), plus ray and the DesktopRig mouse. Out of reach, it must still be ray-usable.

**Theme:** an Albie dark `UITheme` derived from `UIThemeQuest_Dark`. One accent per tab is fine, but keep it restrained.

**Data:**
- The tiles are generated from registries (§5.4–5.6), not hard-coded layouts.
- Every tile shows real data.
- Every tab has an empty state, for example "No artifacts yet. Tap Build something or ask Albie."

**Done:** at a desk and away from one, the menu opens, switches tabs, opens an item and closes, by mouse in desktop mode and by simulated poke. Screenshots pass the visual rubric (§6.3).

### 5.3 Window manager (P0)
`OSWindows` opens Tools, Projects and Artifacts as windows:
- Each is a `ForgePanel` (or `ForgeVolume`) **root object** with `PanelManipulator`, so gestures (flick in or out, minimise to a dot) work on it.
- **Several can be open at once.**
- **Placement:**
  - At a desk, around the desk, reusing Workspace's arc layout idea.
  - Away from a desk, in front of the user, about 0.8–1.0 m away, fanned so they don't overlap.
- **Windows don't fold when you leave the desk.** They stay where they are.
- **Bring windows here** springs every window back around you.
- Every window gets a close button. Close all is in the menu and the tool.
- **Existing Workspace modes:** a Tool that is a Workspace mode (CAD, Train/ML, Code, Messages) must open **with and without a desk**.
  - Either change `Workspace` so its items can live outside the desk frame and more than one can be open, or open the mode's primary prefab(s) as OS windows.
  - Pick the option with less risk, log why, and don't break the existing ML, CAD or Code layouts at the desk.
  - The `Workspace.Open` refusal away from a desk must go or be bypassed for OS opens.
  - `WorkspaceRemote`'s `not_at_desk` error must stop happening for things the OS can open.
- **Retire the desk dock:**
  - When the OS is present, the dock stays hidden and the table button takes its place.
  - Don't delete the dock code.
  - Keep the whistle and crew egg working.
  - Make sure `ArrivalDirector` still has a sensible first arrival: the dock hold becomes an OS hold until START, then show the table button.

**Done:** a play test opens three windows at once at a desk, walks away (forced Away) and shows they're all still there, presses Bring windows here, and opens a Tool while away.

### 5.4 Tools tab (P0)
| Tool | Opens |
|---|---|
| **CAD** | the CAD mode's panels and volume |
| **Code** | code mode (after D-41, the headset code browser; check what the merge leaves) |
| **Train** | the ML panels, plus `ControllerBrainView` (the brain trainer) when a truck exists. The brain view is probably missing from Main today; make it reachable. |
| **Notebook** | new: a notebook window for the open project (plan, versions, recent attempts with outcomes, lessons) from `NotebookStore.Read` and its events. Read-only plus "Mark worked / failed" pills on the newest open attempt. |
| **Drive** | §5.8 |
| **Messages** | the messages panel. Sending keeps its confirm. |
| **Gestures** | shows the gesture display (`GesturePanel`), with Teach a gesture |

Tools are a registry: `OSRegistry` holds Tool entries `{id, title, icon, open()}`, so adding one is one entry.

**Done:** each tile opens a working window at a desk and away from one.

### 5.5 Projects tab (P0)
**List:**
- Every folder in `persistentDataPath/projects/`, plus the built-in starters.
- A project is open if `NotebookStore.ProjectId` matches.
- Starters:

  | Starter | Opening it does |
  |---|---|
  | RC truck | `proj_truck` |
  | Quadcopter | opens CAD on `quadcopter` |
  | Rocket | opens CAD on `rocket` |
  | Image classifier | the ML project, via `ml_open` |

  Starters that need data or tools the app doesn't have show an honest "not available offline" state; they never break.

**Opening a project:**
1. `NotebookStore.Open(id)`. Never leave no project open.
2. Show a **Project window**:
   - its plan card (goal, test, milestones)
   - its version list
   - linked artifacts, with run results
   - "Continue" actions that open the right Tools and Artifacts
3. For the truck, the Continue actions are: **Course** (create or open the slalom), **Drive**, **Train**, **Autopilot**, **Code** and **Send**. They're the §15.6 beats as buttons.

**Done:** from a fresh install, Projects → RC truck → Course gives a slalom with the brain truck (`links.thing == "truck"` kept).

### 5.6 Artifacts tab (P0)
- **List:** `ArtifactLibrary.List()`, plus a **New** row of templates with friendly names and icons.
- **Opening one** goes through the `ArtifactTools` path, so placement, approval and `FollowsUser` behave as they do by voice.
- Room artifacts (slalom, hoops) show where they'll go. Volume artifacts open as windows.
- **Make artifact panels movable:** give the host's panels and volumes a `PanelManipulator`/`VolumeManipulator`, or wrap them. Gestures and Bring windows here must reach them. The change goes in `Artifacts/`, so keep it minimal and log it for Satyabrata.
- **Linking:** an artifact made while a project is open links to that project; the tile shows "for RC truck".

**Done:** you can open and close the slalom, the heart, the solar system and repo activity from the tab. You can make a new slalom while the truck project is open, and the brain truck appears.

### 5.7 Settings (P1)
Settings is a window from the strip, with:
- **Menu button:** Table, Watch turn and Corner, each on or off.
- **Hands-free voice:** whatever toggle exists in `AlbieVoice`. Read it first.
- **Window layout:** curved or flat (`Workspace.SetLayout`).
- **Gesture display:** on or off.
- **Reset window positions** and **Reset artifact permissions**. Each confirms with a tap.
- **Menu distance and height:** sliders, for Hudson's headset tuning. Persist them.
- **About:** version, plus credits and licences (CC0 models, the UI Set notice).

Settings persist in `persistentDataPath/os_settings.json`.

### 5.8 Drive: the hand transmitter (P1)
**This is needed for hands-only.** Without it, the truck laps need a Touch controller.
- **Drive** is a window: a pokeable or grabbable two-axis pad. Up/down is throttle, left/right is steer. It springs back to centre on release, and there's a big Stop.
- It writes to the `ManualInputs` channels `stick.x` and `stick.y` that `QuestStick` feeds, so the existing recording and Autopilot code needs no change. Read `ManualController.cs` and `QuestStick.cs` first.
- **Optional:** tilting the pinched hand also steers, using the `hand.roll` channel.
- Opens from Tools and from the truck project's **Drive** action. When a truck exists, the window says which Thing it drives.

**Done:** in desktop mode, mouse-dragging the pad drives the brain truck around the slalom, and `brain_record` laps recorded through it train and run the Autopilot (the `SlalomBrainCheck`-style flow).

### 5.9 Build something (P1)
1. **Albie comes to you.** Use `GestureActions`/`GesturePerch`'s `albie.come` path, or the lead gator's walk.
2. **Albie asks one question at a time.** Each question is a card with 2–4 **tappable** answers, plus "Something else" when voice is on.
   - Use a deterministic planner (`OSPlanner`) with a small question tree, so it works offline and with voice off:
     1. What are we building? (RC truck, Drone, Rocket, A model that recognises things, Something else)
     2. 1–2 follow-ups per kind: size or speed for a truck, the test ("slalom around my couch"), the goal.
   - At most **3 questions**.
   - When voice is live, Albie speaks the same questions, and a spoken answer that matches an option counts as tapping it.
3. **A plan card shows** the goal, parts, test and milestones, with **Start**, **Change** and **Cancel**.
4. **On Start:**
   - create or open the project (the truck maps to `proj_truck`; others get a new id)
   - `RevisePlan` with the answers
   - open the Project window
   - offer the first Continue action
5. **Nothing is created before Start.** Cancel leaves no files behind.

**Done:** the play test taps through Build something → RC truck → two answers → Start, and the truck project opens with the new plan saved.

### 5.10 Agents (P1)
- **Agents window:** a row per crew member (and Albie as the lead), with type, name, what it's doing, and walking/home.
- **Bring home:** new behaviour on `GatorCrewMember`, for example `SetHome(bool)`.
  - The agent stops wandering and goes to a home spot: a small "home" pad on the desk near the crew egg, or, away from a desk, it folds into an icon on the Agents window.
  - **Its task, bridge and hub state keep running.**
  - **Send out** returns it to walking. Also add **Bring all home**.
- **Dismiss** stays as is, behind a confirm, labelled "Dismiss (stops its task)".
- **Make an agent (stretch):** choose a type from `GatorAgentTypes`, and spawn it.
- **Hatching:** the crew still only hatches in code mode; check whether the OS needs that gate relaxed, and log the decision.

**Done:** the play test spawns two agents (key N or `spawn_agent`), brings one home, shows its task is still assigned, and sends it out again.

### 5.11 Watch turn (P1)
- An `OSWatch` component reads the left hand from the gesture hand source:
  - back of the hand toward the eyes
  - wrist in view
  - held 0.3 s
- When it fires, a small button (at least 22 mm) appears just above the wrist, attached to the wrist, and the right index finger taps it.
- It hides when the wrist turns away.
- Debounced. It doesn't fire during `palm_up` or a pinch, and doesn't fire while a panel is being dragged (`PanelManipulator.Grabbing`).
- **Desktop test:** add a left hand to `SimulatedHandSource`, or a test hook, so the play test can turn the wrist and tap the button.
- **Done:** the play test fires it, taps it, and the menu opens. `GestureSelfTest`'s "an idle hand fires nothing" still passes. A resting left hand fires nothing over 10 s of synthetic idle.
- Real-hand thresholds still need Hudson's headset tuning. Expose them as fields and say so in the hand-back.

### 5.12 Corner button (P1)
- Away from a desk only.
- A small round button low in one corner of the view, which Settings can choose.
- It **follows the head with lag:** it eases into place over about 0.4 s and only re-centres once the head has turned more than about 25°. It is never locked to the head.
- It opens the menu with poke or ray. It hides while the menu is open.

### 5.13 Look-and-feel pass (P2, but don't skip it)
- **Do this after everything above works.** Do it as a loop: screenshot every screen, then have reviewer agents grade them against the rubric (§6.3), fix what they flag, and repeat until two rounds in a row find nothing above minor.
- **Icons:** use a single consistent icon set. Use what the UI Set ships, or simple vector icons you draw as SVG sprites in `OS/Resources/`. No emoji, and no mixed styles.
- **Typography:** the UI Set theme fonts if they're included and licensed; otherwise Inter (already in `ForgeLook`).
- **Restyle the old windows lightly:** make the existing `ForgePanel` windows sit comfortably next to the new UI (corner radius, header, colours), so the OS doesn't look like two products. Don't rewrite module UIs.

## 6. Verification: how you know it works

### 6.1 New checks (in `OS/Editor/`)
- **`OSCheck`** ("Forge/OS/Check OS", `Run()` plus `RunBatch()`):
  - registries load
  - every tile has an id, title and icon
  - `os.json` tools are registered with handlers
  - settings round-trip
  - the planner tree is valid (it ends in a plan within 3 questions)
  - the voice rules still pass
- **`OSPlayTest`** ("Forge/OS/Run Play Test", modelled on `GesturePlayTest`): Main in desktop mode, voice off. It walks the whole flow and saves screenshots to `Logs/os/NN_name.png`:
  1. Arrive and sit at the desk. The table button appears. Press it, and the menu opens.
  2. Open each tab. Open one Tool, one Project and one Artifact; three windows are open at once.
  3. Stand and walk away (Away). The windows stay. The corner button appears and opens the menu. Bring windows here works.
  4. The watch turn opens the menu.
  5. Build something → truck → Start. The project opens with the plan.
  6. Truck project → Course gives a slalom and the brain truck. Drive pad laps are recorded. Train runs, and Autopilot completes the slalom. Use the pretrained fallback if live training is slow, and say so.
  7. Agents: spawn, Bring home (task kept), Send out.
  8. Settings: toggle the corner button off, and it's gone.
  9. Close all.
  
  It ends PASS only if every step passes. Otherwise it reports the first failing step with its screenshot.

### 6.2 Existing checks that must still pass
Run these **after the D-41 merge (baseline) and again before the PR**:
- **Gestures:** `GestureSelfTest`, `GesturePlayTest` (update its expectations only if the OS legitimately changes them, and log why).
- **Artifacts:**
  - the "Forge/Check Artifacts" suite
  - "Forge/Artifacts/Check …": Runs, Biology Templates, glTF and Timelines, Hoops, Repo Activity, Repo Connector, Physics Templates, Slalom, Voice Rules, and Brain Loop (Play)
- **Controllers:** Autopilot, Behaviour Cloning, Runner, Five In A Row, Hot Reload, ML Template, Room Slalom, Thing Runtime.
- **Notebook:** Check Notebook.
- **ML:** Validate Main.
- **Compile:** a clean compile with no new warnings from OS code.
- **APK:** `QuestBuild.Build` succeeds. Record its size.

### 6.3 Visual rubric (for screenshot review)
A reviewer grades each screenshot pass or fail per line:
1. **Polish:** it looks like it came from the same team as Quest's system UI. A consistent radius, spacing, type scale and colour; nothing looks like a debug UI.
2. **Hierarchy:** the tab bar, grid and strip read at a glance. One primary action per screen (Build something on the menu, Start on the plan card).
3. **Legibility on a Quest 3S** (lower resolution than Quest 3):
   - body text at least about 16 px at the panel's canvas scale, which works out to about 9–10 mm cap height at 0.5 m
   - contrast at least 4.5:1
   - no thin grey text on grey
4. **Touch targets:** at least 22 mm with 12 mm gaps. Hover and pressed states are visible.
5. **Honest content:** no placeholder lorem, no "TODO", and no brand names. Empty states say what to do.
6. **Integration:** older windows next to the new UI don't clash.
7. **Comfort:** nothing is clipped. The menu fits in about 70° of horizontal field of view and sits within reach.

### 6.4 What the run can't verify (say so in the hand-back)
- **Real-hand feel:** poke depth, the watch-turn thresholds, and whether the watch turn clashes with Meta's system gesture.
- **Frame rate on the 3S**, and passthrough legibility.
- **Voice** with the paid session.
- **Room fit** in Hudson's actual room.

For each of these, the hand-back gives a short checklist Hudson can run on the headset in under 15 minutes, with the parameter names to tune.

## 7. How to run it

**Orchestrator:** Fable, at its own effort.
- It plans, splits the work, reviews, integrates, and keeps the run log. It reads every diff its workers produce before it accepts it.

**Workers, through the Workflow tool** (Hudson has opted in, and the size limit is set to large, about 50 agents):

| Work | Model | Effort |
|---|---|---|
| C# core and integration: shell, window manager, Workspace changes, crew Bring home, watch turn, Drive pad, planner, play test | Sonnet | `high` |
| UI assembly from UI Set prefabs, the theme, settings, tiles, icons | Sonnet | `medium` |
| JSON tool defs, README and docs, notices, check scaffolding, finding call sites, reading logs | Haiku | `low`–`medium` |
| Screenshot grading (§6.3) and reviewing diffs against §3 rules (allocations in `Update`, Main.unity untouched, statics reset, no brand names) | Haiku | `medium`; any "fail" it reports is confirmed by a Sonnet `high` agent before you act on it |
| Any finding that would change a §2 decision | — | the orchestrator reads it itself |

**Parallelism:**
- One Unity editor is the bottleneck.
- Workers may write code in parallel only in **separate files and folders**.
- Exactly **one integrator lane** compiles, runs checks, takes screenshots and commits.
- Don't give workers git worktrees: each would need its own 13 GB `Library`.
- Pipeline the work so writers keep going while the integrator verifies.

**Loops:**
- Each part (§5.x) loops write → compile → check → fix until its "Done" passes, or until 4 tries fail. Then log it, move on, and come back later.
- The look pass (§5.13) loops until two clean rounds.

**Order:**
- §3.1 setup and the D-41 merge, then the baseline checks.
- Then §5.1–5.6 (P0).
- Then §5.7–5.12 (P1). The Drive pad (§5.8) goes first among these.
- Then §5.13 and the final checks.

**Run log:** `docs/ALBIE_OS_RUN_LOG.md`, appended as you go:
- what was done
- what passed, with the check output
- decisions with a one-line why
- anything skipped

**Hand-back** (the final message, and the PR description):
1. What works, with the check results and the paths to the key screenshots.
2. What's partial or skipped, and why.
3. Every change outside `OS/`, listed for review: Workspace, Artifacts, Pet, Gestures, Project Settings.
4. The APK path and size.
5. The headset checklist from §6.4.
6. Open questions for Hudson.

Don't say something works unless a check or screenshot shows it.
