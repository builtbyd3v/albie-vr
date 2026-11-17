# Albie OS

The Albie OS is the layer that gives Albie a menu, windows and a set of tools. The user opens one menu, finds their tools, projects and artifacts there, and opens them as windows around them. It works at a desk and away from one. The OS code lives in `Scripts/`, its editor checks in `Editor/`, and its prefabs and voice tools in `Prefabs/` and `Resources/`.

## How it starts

- `OSBootstrap` runs after each scene loads. It looks for an `OSShell` in the scene. If there is none and the scene has a `Workspace`, it builds one from `Resources/OSShell` and adds it as a root object. A scene with no `Workspace` (the sandboxes and checks) is left alone.
- If `Resources/OSShell` is missing, it logs one warning and the scene has no menu. Fix it with **Forge > OS > Build OS Prefabs**.
- The shell has these components: `OSShell` (state and events), `OSWindows`, `OSKeys`, `OSTableButton`, `OSCornerButton`, `OSWatch`, an `AudioSource` for `OSSounds`, and a child `OSMenu` view.
- Before the first scene loads, the Tools, Projects and Artifacts tabs register their entries (`OSToolsCatalog`, `OSProjects`, `OSArtifacts`). The voice tools register in `OSTools` and `OSBuildTools`. The system pages (Agents, Settings, Build something) register in their own `Register()` methods.
- The desk dock never shows while the OS runs. The table button takes its place.
- At start, the shell applies the saved Gesture display setting once. It tries every half second for ten seconds, then gives up quietly if the gesture engine is not there.

## The menu

`OSMenu` is a UI Set backplate with the OS's own tiles.

- **Tab bar:** Tools, Projects, Artifacts, and Close. The last tab opened is remembered for the session.
- **Grid:** 4 columns by 2 rows per page, with paging when there are more tiles. Each tile has a title, an icon and an optional status line. The "New" section on the Artifacts tab is a heading inside the grid.
- **Strip:** Build something (the primary button), Agents, Settings, Bring windows here, and Close.
  - Agents, Settings and Build something open their system page and then close the menu.
  - Bring windows here lays out every open window again and closes the menu.
- **Tools tab:** CAD, Code, Train, Notebook, Messages, Gestures, Drive, Agents. Train also opens the controller brain window when a brain truck is running. **Tools → Agents → Project workflow** opens the coding orchestration panel.
- **Drive:** when no truck is loaded, tap **Set up truck**, then **Allow** on the course card. The Drive window includes **Record laps**, **Train**, **Autopilot**, the steering pad and **Stop**. Record two clean laps with the pad before training. Setup uses the RC truck project and its existing course/permission flow.
- **Projects tab:** RC truck, Quadcopter, Rocket, Image classifier, then each other folder under the notebook's `projects/` folder. A project's status line is read again every few seconds or when the notebook changes.
- **Artifacts tab:** a "New" section with one tile per template in `ArtifactLibrary.Templates` (slalom, hoop course, What shipped?, cell, heart, brain, plant cell, eye, DNA, double pendulum, solar system). Three-body is listed only when it opens here; it opens, it is not created.
- An empty tab shows a sentence that says what to do (`OSRegistry.EmptyText`).
- Tapping a tile runs its `open` action. A tile whose thing is open shows as open.
- **Text sizes** are constants in `OSUi` (one place): body 22 units (9.8 mm cap height at the OS unit of 0.65 mm), secondary text 21.5 (9.6 mm), a tile's caption 20 (8.9 mm), section headings 26. `OSUi.Text` lifts anything smaller, so a window cannot ask for less. The menu is 0.663 m wide, 67 degrees of view at its default 0.5 m. `Capture Look Set` also writes `look/text_audit.txt`: every visible text with its size, its cap height and a flag when it does not fit its box.

## Ways to open the menu

Each summon records where it came from (`OSSummon`). The menu grows from that place.

| Way | What it is | Turned on by |
|---|---|---|
| Table button | A round button on the real table, shown when the user is at a desk and the greeting is not holding the desk | `tableButton` (on) |
| Corner button | A small button low in one corner of the view, shown away from a desk. It keeps an anchor and is never locked to the head. It hides while the menu is open | `cornerButton` (on), `cornerOnLeft` picks the side |
| Watch turn | Turn the left wrist as if checking a watch; a small button appears above it. Poke it with the right index finger | `watchTurn` (on) |
| M key or left controller menu button | Toggles the menu. A plain M only: Shift+M is CAD's measure key. An M typed into a text field is a letter | Always on |
| Voice | The `os_menu` and `os_tab` voice tools | Always on when voice is on |

## Windows and modes

- **OS windows** (`OSWindows`): a free `ForgePanel` at the scene root with move, turn and resize handles and a close button. `Open(id, title, size, build)` makes one, or brings the existing one with that id forward. `Adopt` tracks something that already exists (the gesture display, an artifact's panels, the open mode). `Close(id)`, `IsOpen(id)` and `Find(id)` work by id.
  - Opening takes 0.2 s and closing 0.18 s. A window can be put away into a minimised dot (a flick away from its gesture). Opening it again brings it back out of the dot.
  - Placement is in angles from the eyes. A new window takes the nearest free place, clear of every other panel and volume and of the room's walls. Nothing moves by itself when the user walks away from a desk.
- **Bring windows here** (`BringHere()`): lays out the open mode and every window again around the user, facing them.
- **Close all** (`CloseAll()`): closes every window, runs each adopted window's close action, and closes the open mode.
- **Workspace modes** (`OSModes`): CAD, Train (the `ml` mode), Code and Messages are Workspace modes, not OS windows. `OSModes.Open(id)` opens one at a desk or away from one. Only one mode is open at a time. The open mode is tracked in `OSWindows` as one group entry (`OSWindows.GroupId`), so Close all and Bring windows here reach it.
- The OS gives the workspace its hooks when the shell starts (`OSModes.Attach`): a virtual desk in front of the user when there is no desk, keep-open-away, a retired dock, and the flat or curved layout from Settings.

## Adding a tool tile

1. Write a static method with `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]` that calls `OSRegistry.RegisterTool`. Call it from `OSToolsCatalog.Register()` (that is where the existing tools are listed), or give it its own attribute as `OSTools.Register` does.
2. Give it an `OSEntry` with a stable snake_case `id`, a `title`, an `icon` name from the kit and an `open` action. Add `status` and `isOpen` if it has them.
3. The same id replaces an older entry. `OSRegistry.UnregisterTool(id)` removes it.

```csharp
OSRegistry.RegisterTool(new OSEntry
{
    id = "notebook", title = "Notebook", icon = "notebook",
    status = NotebookStatus,
    isOpen = () => WindowOpen(OSNotebookWindow.WindowId),
    open = () => OSNotebookWindow.Open(),
});
```
(From `OSToolsCatalog.cs`, the Notebook entry.)

## Adding a project starter

1. Add a row to the `starters` array in `OSProjects.cs`: `new Starter { id, title, icon, kind, part }`. `kind` is `Truck`, `Cad`, `Ml` or `Generic`; `part` names the CAD design for `Cad` kinds.
2. The row must have an icon name the kit resolves (see the icon table).
3. Any folder under the notebook's `projects/` folder appears on its own, with the icon `projects`. No code is needed for that.

## Adding an artifact template tile

1. Add the template name to `ArtifactLibrary.Templates` and its files under `Resources/ArtifactTemplates/<name>/` (the Artifacts module).
2. A new template appears under "New" on its own, titled from its manifest. For a better title, add it to `FriendlyTitles` in `OSArtifacts.cs`.
3. For its icon, add a case to `IconFor` in `OSArtifacts.cs`. The default icon is `artifact`.
4. Room templates (`RoomTemplates`: slalom, hoop course) stand on the real floor; the rest open as windows.

## Adding a system page

1. Write `Register()` with `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]` that calls `OSShell.RegisterSystem(id, open)`.
2. The page then opens through `OSShell.OpenItem("system", id)` and through `os_open` with kind `system`. A second call with the same id replaces the first.
3. The strip's buttons are fixed in `OSMenu.cs`. Adding a strip button means editing that file.

```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
public static void Register() => OSShell.RegisterSystem(WindowId, () => Open());
```
(From `OSSettingsWindow.cs`.)

## Settings

The Agents page also opens **Agent office**, a 3D viewport of Albie and the existing crew with workstations,
live workflow states and role inspection. Its mesh-only portraits do not create agents or change the real crew.
See `docs/AGENT_OFFICE.md` at the repository root.

Settings are in `OSSettings` and saved to `os_settings.json` in the app's data folder (`Application.persistentDataPath`). A missing or damaged file gives the defaults, and values are clamped when read and saved. Settings are edited in the Settings window, and each change takes effect at once.

| Field | Default | What reads it |
|---|---|---|
| `tableButton` | true | `OSTableButton` (shows the table button at a desk) |
| `watchTurn` | true | `OSWatch` (shows the watch button when the wrist is turned; off hides it) |
| `cornerButton` | true | `OSCornerButton` (shows the corner button away from a desk) |
| `cornerOnLeft` | false | `OSCornerButton` (puts the corner button on the left) |
| `menuDistance` | 0.5 m | `OSMenu` placement. Range 0.35 to 0.8. The Settings slider offers 0.35 to 0.7 |
| `menuDropDegrees` | 15 | `OSMenu` placement. Range 0 to 35. The Settings slider offers 0 to 30 |
| `gestureDisplay` | false | `OSShell` at start, through `OSToolsCatalog.ApplySavedGestureDisplay`; the Settings window sets it |
| `flatLayout` | false | `OSModes` (the workspace's flat or curved layout) |

Menu distance and height are read the next time the menu opens. `OSSettings.Changed` tells listeners when anything is saved.

The Settings window has three pages. Page 1 has the menu button switches (table button, watch turn, corner button, corner side) and a plain sentence about voice. Page 2 has the window and menu position settings. Page 3 has the two resets (each asks first) and About.

## Voice tools

The tools are in `Resources/AlbieTools/`.

`os.json` (registered in `OSTools.cs`):
- `os_menu`: opens or closes the menu. Without `open`, it toggles.
- `os_tab`: shows the tools, projects or artifacts tab, and opens the menu.
- `os_open`: opens a tool, project, artifact or system page by kind and id. Tool ids are cad, code, train, notebook, drive, messages and gestures. It works at the desk and away from it.
- `os_windows`: `recenter` brings every window back in front of the user, or `close_all` closes them all.

`os_build.json` (registered in `OSBuildTools.cs`):
- `os_answer`: answers the open Build something card with what the user said. It is for a real answer only. Words that match no choice return an error with the choices.

## Tests and checks

All are under **Forge > OS** and run in Play mode on the desktop rig with voice off, except Check OS and Check Test Environment, which need no Play mode.

- **Check OS** (`OSCheck`): no Play mode. Checks the kit, the entries (ids, titles, icons, open actions), the tabs, the settings round trip, the planner tree and the shell prefab.
- **Check Test Environment** (`OSTestEnvCheck`): checks the scratch test environment itself.
- **Run Menu Test** (`OSMenuTest`): the table button, the menu's placement, summons, tiles and paging.
- **Run Windows Test** (`OSWindowsTest`): placement, overlap, close, minimise, Bring here and Close all.
- **Run Tools Test** (`OSToolsTest`): the voice tools and the Tools tab.
- **Run Projects Test** (`OSProjectsTest`) and **Run Artifacts Test** (`OSArtifactsTest`): the two tabs and their actions.
- **Run Planner Test** (`OSPlannerTest`): Build something's question tree and plan card.
- **Run Agents Test** (`OSAgentsTest`): the Agents window, Bring home, Send out and Dismiss.
- **Run Settings Test** (`OSSettingsTest`): each setting, its file and its reset.
- **Run Corner Test** (`OSCornerTest`) and **Run Watch Test** (`OSWatchTest`): the corner button and the watch pose.
- **Run Drive Test** (`OSDriveTest`): the pad, Stop, a recording and the Autopilot.
- **Run Play Test** (`OSPlayTest`): walks the nine spec steps with real pointer presses and writes screenshots to `unity/Logs/os/`.
- **Capture Look Set** (`OSLookShots`): saves one picture per screen to `unity/Logs/os/look/` with an `index.json` of what each shows. It is the set to review for look and layout.
- **Build OS Prefabs** (`OSPrefabBuilder`): builds the menu prefab and the shell prefab, and applies the icon map. Run it after an icon or prefab change.

Each test runs on a scratch test environment (`OSTestEnv`). `Enter` saves the forced-desktop and voice-off editor prefs and the Claude address, and `UseScratch` moves the notebook, artifact library, approvals and `os_settings.json` to a scratch folder. `Leave` puts every value back exactly and reports anything that still differs. A test never touches the user's own data. If an interrupted run leaves its saved copy behind, the next run puts the real original back first.

The tests that run in Play mode report through a static `LastResult` string (poll it after the run), and the console gets a summary line.

## Headset tuning

Change these only after a headset test shows a problem. Each one is a public field, so the Inspector or the prefab can change it.

**Watch turn** (`OSWatch`, on the shell):

| Field | Default | What to change if it feels wrong |
|---|---|---|
| `backFacingDot` | 0.6 | Lower it if the watch is hard to trigger. Raise it if a hand resting on the desk or chest triggers it |
| `viewDegrees` | 35 | Wider if the wrist must be pointed too exactly at the eyes; narrower if it fires while the user looks elsewhere |
| `minDistance` | 0.2 m | Closest the wrist may be to the head |
| `maxDistance` | 0.7 m | Farthest the wrist may be from the head |
| `maxBelowEyeMeters` | 0.42 m | Lower it if a hand in the lap is still wrongly counted; 0 turns the check off |
| `holdSeconds` | 0.3 s | Longer if the button shows by accident; shorter if it is slow |
| `hideSeconds` | 0.25 s | How long the pose may be lost before the button hides; also the re-arm time |
| `pinchBelow` | 0.26 | Pinch threshold in hand lengths; a relaxed hand is about 0.4 |
| `palmUpAbove` | 0.45 | Palm-up threshold (0 to 1); lower it if the watch still fires with the palm up |
| `quietSeconds` | 0.8 s | How long the watch stays off after a left-hand gesture fires |
| `keepBackFacingSlack` | 0.15 | Slack on `backFacingDot` once the button is shown |
| `keepViewSlackDegrees` | 10 | Slack on `viewDegrees` once the button is shown |
| `buttonMillimetres` | 28 mm | Button diameter. Never below 22 mm (the finger-sized minimum) |
| `liftMillimetres` | 45 mm | How far the button floats above the wrist |
| `followSharpness` | 25 | How quickly the button follows the wrist. Higher is tighter, 0 is rigid |

**Corner button** (`OSCornerButton`, on the shell):

| Field | Default | What to change if it feels wrong |
|---|---|---|
| `distance` | 0.45 m | Closer if it is hard to reach; further if it is in the way |
| `sideDegrees` | 28 | Angle to the side of the gaze line at rest |
| `dropDegrees` | 22 | Angle below the gaze line at rest; raise it to sit lower |
| `recenterDegrees` | 25 | Head turn that picks a new anchor. Raise it if it moves too often |
| `recenterMeters` | 0.3 m | Head move that picks a new anchor |
| `easeSeconds` | 0.4 s | How long the move to a new anchor takes |

**Menu placement** (settings, read when the menu opens): `menuDistance` (0.5 m, range 0.35 to 0.8) and `menuDropDegrees` (15, range 0 to 35). If the menu sits too close, raise `menuDistance`. If it sits too high or too low, change `menuDropDegrees`. Placement moves the menu nearer, or to the side, when a window is in front of it. The fixed values are in `OSMenu` (`DistanceStep` 0.02 m, `ProbeYawDegrees` 10, `MaxDropDegrees` 70). They are code constants, not settings.

**Drive pad** (`OSDrivePad`):
- `SpringSeconds` (0.12 s, public constant): how fast the knob springs back.
- `ThrottleScale` is set by the Drive window. For the brain truck it is `OSDriveWindow.HandThrottle`, so laps driven by hand train the same way as laps from a Touch stick. Do not change it by hand.

**Table button** (`OSTableButton`): no public tuning fields. The sizes are constants (plate 84 units, button 62 units, lift 12 mm).

## Known limits

- **Ray and poke were not tested on a headset.** The menu, windows and tiles were tested with EventSystem pointer presses on the desktop rig. Hover and press looks come from the UI Set animators and were not checked in pictures.
- **Pictures come from the desktop rig.** The look set shows layout, size and states. It does not show passthrough or headset optics. A headset check is still needed.
- **Wrist-roll steering (`hand.roll`) is not built.** The Drive pad is the only transmitter.
- **The brain truck stays after the slalom closes.** This is the Artifacts module's behaviour. The Artifacts window then reads "Course: not open" with the brain truck still ready.
- **Some older windows keep their own colours and small captions** (for example in Code and Train). Those are module UIs; the OS only changed the shared plate and type.
- **Agents hatch without the code mode.** The Agents window hatches through `spawn_agent`, which the code mode's gate does not stop. The crew egg and the N and X keys are still gated by the code mode.
- **Some Projects actions need the cloud.** The RC truck's dataset load and the send of the truck model show a plain line saying they need Albie's cloud when it is not reachable.

## Icons

Every OS screen uses one icon set only: the Meta UI Set's 24 px Filled atlas (`Packages/com.meta.xr.sdk.interaction/Runtime/Sample/Textures/OCUIIconSet/OCUI_24_Filled_2x.png`). No emoji, no mixed styles. Code asks for a friendly name (`OSUi.Icon("cube")`, or `OSEntry.icon`); `Resources/OSKit.asset` maps it to a sprite. The mapping lives in `Editor/OSPrefabBuilder.cs` (`IconMap`) and is applied by **Forge > OS > Build OS Prefabs**; **Forge > OS > Check OS** fails if a name doesn't resolve.

| Friendly name | Atlas sprite (`icon_NAME_24_Filled`) | Used for |
|---|---|---|
| `cube` | `vr-object` | CAD tool |
| `code` | `desktop` | Code tool |
| `train` | `experiments` | Train tool |
| `notebook` | `compose` | Notebook tool |
| `drive` | `controller-full` | Drive tool (the hand transmitter) |
| `messages` | `chat-text` | Messages tool |
| `gestures` | `hand-tracking` | Gestures tool |
| `truck` | `destination` | RC truck project |
| `quadcopter` | `airplane-mode` | Quadcopter project |
| `rocket` | `to-top` | Rocket project |
| `classifier` | `image` | Image classifier project |
| `artifact` | `category-all` | a generic artifact |
| `slalom` | `move-reposition-alt` | slalom artifact |
| `hoops` | `open-circle` | hoop course artifact |
| `scoreboard` | `leaderboard` | scoreboard artifact |
| `biology` | `heart-on` | biology models |
| `physics` | `speed` | physics simulations |
| `repo` | `folder` | repository activity |
| `plus` | `add` | new / create |
| `close` | `close` | close buttons |
| `settings` | `settings` | Settings |
| `agents` | `friends` | Agents |
| `build` | `add-circle` | Build something |
| `bring` | `reorient` | Bring windows here |
| `home` | `home` | bring an agent home |
| `send` | `send` | send out |
| `check` | `check` | confirm, done |
| `warning` | `warning` | problems |
| `chevron_left` | `chevron-left` | previous page |
| `chevron_right` | `chevron-right` | next page |
| `table` | `table` | the desk |
| `apps` | `apps` | the table button |
| `tools` | `apps` | Tools tab |
| `projects` | `gallery-folder` | Projects tab |
| `artifacts` | `category-all` | Artifacts tab |

To change a glyph, edit its row in `IconMap`, run Build OS Prefabs, and update this table.

## Third-party notice

The UI kit and the icon set are used under their licences. The full notice is in `THIRD_PARTY_NOTICES.md` in this folder.
