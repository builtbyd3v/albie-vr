# Albie OS

This file is completed by a later part. For now it holds the icon table.

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
