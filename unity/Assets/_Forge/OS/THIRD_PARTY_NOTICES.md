# Third-party notices

## Meta Horizon OS UI Set (Interaction SDK samples)

The OS menu and controls are built from prefabs, icons, sounds and a theme that ship in the Meta XR Interaction SDK
(`com.meta.xr.sdk.interaction`, `Runtime/Sample/`): the UI Set prefabs (backplate, buttons, text tile, shelf toggle, switch,
slider), the 24 px Filled icon atlas, and the sample audio clips. They are referenced from the package by `Resources/OSKit.asset`,
not copied. The one exception is `Themes/AlbieDark.asset`, which is a copy of the package's `UIThemeQuest_Dark.asset` with a few
colour changes; it keeps pointing at the package's animators, materials and font.

Copyright (c) Meta Platforms, Inc. and affiliates. Used under the Oculus SDK License Agreement:
https://developers.meta.com/horizon/licenses/oculussdk/

## Files this branch added

Nothing in the OS was copied from outside the project except where this list says so.

| File | What it is | Where it came from |
|---|---|---|
| `OS/Resources/OSKit.asset` | Maps friendly names to the UI Set prefabs, icon sprites, sounds and fonts | Written for the OS (built by `Forge > OS > Build OS Prefabs`). It only references the Meta assets above by their package path |
| `OS/Themes/AlbieDark.asset` | The OS colour theme | Copy of the package's `UIThemeQuest_Dark.asset` with a few colour changes (Meta UI Set, licence above) |
| `OS/Prefabs/OSMenu.prefab`, `OS/Resources/OSShell.prefab` | The menu and the shell | Built in code by `OSPrefabBuilder` from UI Set prefabs (Meta UI Set, licence above) and OS scripts |
| `OS/Resources/AlbieTools/os.json`, `os_build.json` | Voice tool definitions | Written for the OS |
| `Messages/Resources/SlackIcons/caret.png`, `lock.png`, `send.png`, `sparkle.png` | Four white 128 px icons for the Messages panel | They replace the four SVG files of the same names that the Messages module already had (an editor without the SVG importer showed them as grey squares). They were made during the OS look pass. The run log does not record the tool that rasterised them, so how they were drawn is not documented here; their shapes follow the replaced SVGs. They are not from a third-party icon pack |

The OS adds no audio, texture, model or font files of its own. Sounds, icons, fonts and materials the OS shows come from the Meta XR Interaction SDK package and are not copied into the project, apart from the theme asset above.
