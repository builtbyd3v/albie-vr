# Albie design system

One look for every panel: the OS windows, the menu, the older Forge panels (ML, CAD, Code, Artifacts) and Messages. The values live in one place, `unity/Assets/_Forge/Core/Scripts/ForgeTheme.cs` (the "Design tokens" block). Change them there and every screen follows. The helpers that apply them are `ForgeLook` (any canvas), `ForgeButtonLook` (control states), `ForgeMotion` (easing), `ForgeProgress` (rails) and `OSUi` (OS windows).

To see everything at once, enter Play mode in Main, then pick **Forge > OS > Design Gallery**. `Capture Look Set` saves it as the `design_gallery` picture, and `OSLookShots.RunOnly("design_gallery")` captures only that one.

## Principles

1. **Deep glass, quiet chrome.** The surface is dark graphite, slightly cool and 95% opaque, so the room shows faintly but never through the text. The structure comes from light: a 1 px rim, hairlines and soft shadows, not from boxes.
2. **One accent per view.** The OS accent is Albie lime. A window with a mode badge uses its mode hue, lifted toward white so it reads as text. The accent marks the current step, progress, focus and "open". It is not used for decoration.
3. **Fewer sizes, real weight contrast.** Four type sizes. Headings are bold and body text is regular. Sentence case everywhere. Caps appear only in tags.
4. **Air.** Spacing is on a 4/8 grid. Sections get space above them. Paragraphs stop at about 65 characters per line. A view has at most one primary button.
5. **Hands first.** A target is at least 30 mm. Hover (a ray, or a fingertip coming close) lifts and glows, a press sinks, and every state is visible.

## Colour (worst case: the 95% plate over a white room, on a raised card)

| Token | Value | Use | Contrast |
|---|---|---|---|
| `PanelTop` / `PanelBottom` | #34373F to #1B1C21, alpha 0.95 | the window plate (gradient) | |
| `TextPrimary` | #FFFFFF | body, titles | 8.7:1 |
| `TextSecondary` | white 72% | hints, labels, subtitles | 5.4:1 |
| `TextTertiary` | white 50% | disabled or decorative only | 3.5:1 |
| `Accent` | #C8DC5A lime | current, progress, open | 5.7:1 as text |
| `TextOnAccent` | #12172B ink | on lime | 11.7:1 |
| `TextOnLight` | #272727 | on the white primary button | 14.9:1 |
| `Success` / `SuccessText` | #3DDC97 | done, good | 4.9:1 |
| `Warning` / `WarningText` | #FFB547 | check, overfit | 4.9:1 |
| `Danger` / `DangerText` | #FF5D6C fill / #FFA8AF text | failed, destructive | 4.7:1 (text) |
| `Info` / `InfoText` | #5B9BFF fill / #9CC3FF text | information | 4.8:1 (text) |
| Mode badge text | mode hue lifted 55% toward white | ML, CAD, Code, Messages | 5.3 to 6.2:1 |

Messages keeps its own brand palette (aubergine, lime, orange). It shares the radii and type rules.

## Elevation

| Level | Look | Helper |
|---|---|---|
| Surface | the window plate: gradient, top rim light, inner hairline, soft SDF shadow (in the panel shader) | `ForgePanel` |
| Raised | white 5.5% with a 10% hairline | `OSUi.Card`, `OSUi.Surface(Raised)`, `MLUI.Card` |
| Inset | black 24% (wells, charts, fields) | `OSUi.Surface(Inset)` |
| Floating | opaque #2E3138, a dark halo or a 9-sliced shadow sprite, a 14% light ring | `OSUi.Surface(Floating)`, `OSUi.Confirm` |
| Selected | accent 16% with a 60% accent ring | `OSUi.Surface(Selected)` |

No more than two full-panel transparent layers sit on top of each other. Shadows are one sliced sprite (`ForgeLook.Shadow`) or the shader's halo, never a blur.

## Radius, spacing and type

- **Radius:** panel 24 mm. Card 14 mm (22 OS units). Control 10 mm (16 units). Chips and icon buttons are full pills. Corners are squircles (SDF).
- **Spacing (OS units):** 4, 8, 12, 16, 24, 32, 48 (`Space1` to `Space7`). Columns default to 16 between items, cards to 24 padding and 12 between items, and sections get 8 above them.
- **Type (OS units, Inter):** display 34 (one per view, `OSUi.Heading`), title 26 bold (`Section`), body 22, caption 20. The minimum is 21.5 for secondary text and 22 for body text (`OSUi.Text` enforces it). Line spacing is +6%. Headings use −0.6 tracking. Tags use +5 tracking.
- **Font:** Inter everywhere. The OS theme used to fall back to TextMeshPro's default sans. Glyphs that are missing from Inter's static atlas fall back to that default font.

## Icons

Glyphs are drawn as SDF bars (`ForgeLook.Glyph`: close, minimise, plus, check, chevrons, more). They are crisp at any size and need no assets. Their stroke is 11% of the glyph size, with round caps. Kit icons (the 24 px filled set) are used for everything else, in white or white 72%.

## Motion (s)

- Panel open and close: 0.2 and 0.18, scale and fade (`OSWindows`).
- Press: 0.08, down to scale 0.965.
- Hover: 0.16, fill lift plus glow.
- State: 0.22.
- Progress: 0.35.

Everything eases through `ForgeMotion`, a single runner that ticks only the controls that are moving and allocates nothing per frame.

## States

| State | Look |
|---|---|
| Default | quiet plate with a hairline |
| Hover / near hand | lighter fill, brighter hairline, soft glow |
| Pressed | dimmer fill, sinks 3.5% |
| Disabled | 4% fill and tertiary text, not tappable |
| Selected | accent tint and ring |
| Loading | a `ProgressRail` (eased), or a chip that says what is happening |
| Empty | `OSUi.EmptyState`: icon disc, title, one sentence, and at most one action |

The stepper uses the same states. Done is a solid accent node with a check. Current is an accent-ringed node, the word "Next" and a ringed chip. Upcoming is a hairline node. Locked is a faint node with its chip switched off. The rail between two steps is lit once the step before it is done.

## Quest budget

- `ForgeSurface` draws every new shape (one shared material, no textures), so shapes batch.
- One 192 x 128 procedural atlas holds the shadow and disc sprites. It is made once.
- Nothing allocates per frame. Idle controls cost one bool compare each in `LateUpdate`, which catches `interactable` changes.
