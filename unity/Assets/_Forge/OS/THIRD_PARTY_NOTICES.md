# Third-party notices

## Meta Horizon OS UI Set (Interaction SDK samples)

The OS menu and controls are built from prefabs, icons, sounds and a theme that ship in the Meta XR Interaction SDK
(`com.meta.xr.sdk.interaction`, `Runtime/Sample/`): the UI Set prefabs (backplate, buttons, text tile, shelf toggle, switch,
slider), the 24 px Filled icon atlas, and the sample audio clips. They are referenced from the package by `Resources/OSKit.asset`,
not copied. The one exception is `Themes/AlbieDark.asset`, which is a copy of the package's `UIThemeQuest_Dark.asset` with a few
colour changes; it keeps pointing at the package's animators, materials and font.

Copyright (c) Meta Platforms, Inc. and affiliates. Used under the Oculus SDK License Agreement:
https://developers.meta.com/horizon/licenses/oculussdk/
