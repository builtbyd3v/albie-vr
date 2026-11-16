# Fix: the slalom is placed outside the Quest boundary

**For:** Nandani. **From:** Satyabrata (Artifacts owner, so you have my OK to work in `Artifacts/Scripts/Room/`). **Date:** 2026-11-16.
**Deadline:** please open the PR by **the evening of Nov 17**, so Satyabrata can test it on the headset before submission (Nov 18, 12:00 PT). This is a bug fix after the freeze, so keep it small and keep every check green. Satyabrata reviews the PR.

**Updated 2026-11-16:** the boundary check now goes in `FloorGrid`, not in each `IsFree` (section 1).

## What I saw on the Quest (main `7b2c6db`)

1. **"Make me a slalom" put part of the course outside my Quest boundary.** To walk beside the cones, I had to step through the boundary wall.
2. **Once I was outside the boundary, Albie stopped and got stuck.**

## Why it happens (checked in the code)

- The course generator only knows the **MRUK room scan**, never the **Quest boundary** (Guardian). Free floor is `MrukFloorPlan.IsFree`: inside the room's walls and not under furniture. The boundary is often smaller than the scanned room, so a course can fit the room and still cross the boundary.
- Nothing in `_Forge/` reads the boundary today (no `OVRBoundary` or `OVRManager.boundary` anywhere).
- Every floor course goes through one check, so **one fix covers the slalom and the hoops**:
  - `FloorSlalom.FloorGrid` → `IFloorPlan.IsFree`;
  - `RoomHoops.StillFits` and saved layouts both go through `FloorSlalom.StillFits`, which uses the same grid.
- **Albie getting stuck: not confirmed yet.** Albie's floor NavMesh (`Pet/.../AlbieMrukAdapter.BuildFloor`) is the room's floor plane, not the boundary, so the NavMesh isn't what stops it. The likeliest cause is the Quest itself: when you leave the boundary, the system takes the app's focus and pauses it. If that's right, keeping the course inside the boundary fixes it, because nobody has to step out. Please check part 3 on the headset.

## The fix

### 1. Keep floor courses inside the boundary (the main fix)

**Where the check goes: `FloorSlalom.FloorGrid`.**
- `FloorGrid` is the only caller of `IsFree`.
- The slalom (`FloorSlalom.Search`), the hoops (`RoomHoops.Fit` calls the same `Search`) and saved courses (`StillFits`) all build a `FloorGrid`.
- So one check there covers all three, and both floor plans' `IsFree` stay as they are.

**New file `Artifacts/Scripts/Room/PlayArea.cs`:** the boundary as a world xz polygon.
- `const float Margin = 0.3f` keeps the course this far inside the edge, so you can walk beside the lanes without touching the boundary wall.
- `bool Contains(Vector3 p)` passes when the point is inside the polygon and at least `Margin` from every edge.
- `static PlayArea FromHeadset()` reads the boundary from the headset:
  - Return `null`, and log once, unless all of these hold:
    - `OVRManager.loadedXRDevice == OVRManager.XRDevice.Oculus`. Check this **before** calling `GetGeometry`, which otherwise logs an error in the Editor.
    - `OVRManager.boundary` (a static property) isn't null.
    - `OVRManager.boundary.GetConfigured()` is true.
    - `GetGeometry` returns at least 3 points.
  - It reads `OVRManager.boundary.GetGeometry(OVRBoundary.BoundaryType.PlayArea)`. Use **`PlayArea`**, the rectangle inside the drawn boundary; `OuterBoundary` is deprecated under OpenXR.
  - The points are in **tracking space**. Convert them with `FindFirstObjectByType<OVRCameraRig>().trackingSpace.TransformPoint(...)`, then keep x and z.
  - Log the point count and size once, for the headset check in part 3.

**`IFloorPlan` gets `PlayArea Boundary { get; }`.**
- `null` means no boundary, which is today's behaviour: the Editor, Link, prefab rooms and every current mock room.
- `MrukFloorPlan` sets it **once**, in its constructor (`PlayArea.FromHeadset()`), because `GetGeometry` allocates. `MrukFloorPlan.Current()` already builds a new plan for each placement, so a redrawn boundary is picked up the next time a course is placed.
- The Meta XR Simulator may report a boundary of its own. If it does, courses there are limited by it, the same as on the headset.

**In `FloorGrid`:**
- Add a `bool useBoundary` to the constructor.
- A cell is free only if `room.IsFree(p, clearance)` **and** (`!useBoundary || room.Boundary == null || room.Boundary.Contains(p)`).
- `Clear` already checks the whole footprint (both lanes, end to end) against the grid, so every cone, gate, start and finish ends up inside the boundary.
- `Search` and `StillFits` pass `useBoundary: true` by default.

**The "boundary too small" message, with no contract change:**
- Keep the existing code `no_free_floor` (C-12; don't add a new code).
- When `FloorSlalom.Fit` or `RoomHoops.Fit` fails and `room.Boundary != null`, run the same `Search` again with `useBoundary: false`. It's cheap, and it stops at the first fit.
- If that one fits, the room is big enough but the boundary isn't, so use a boundary message instead of the free-floor one. For example: *"Your Quest boundary is too small for a slalom here: the smallest course needs a clear strip about 4.6 by 0.8 m inside it. Redraw your boundary bigger and ask me again."* Build the numbers the way the current message does.

**Saved courses (`ArtifactGenerators.Place`):**
- A saved course outside the boundary already fails `StillFits` and is placed again.
- Only the message needs changing. If it fails `StillFits` but passes with `useBoundary: false`, say *"Your boundary has changed since I made the course, so I made a new one."* instead of *"Something in the room has moved since I made the course, so I made a new one."*

### 2. Tests (no headset needed)

- **`MockFloorPlan`** gets a settable `Boundary` and a `WithBoundary(PlayArea)` helper, like `WithCeiling`. It's null by default, so every current mock room behaves exactly as now.
  - `PlayArea` needs a constructor from world xz points, for the mocks.
- **New steps in `SlalomCheck`, plus the same three in `HoopCheck`:**
  - The 8 × 6 m loft with a 3 × 6 m boundary at one end: the course is placed, shrunk, and every slot (cones, gates, start, finish) and the lanes either side are at least 0.3 m inside the boundary.
  - The living room with a 2 × 2 m boundary: `no_free_floor`, and the message is the boundary one, not the free-floor one.
  - A course saved with no boundary, then the boundary added: it isn't reused (`StillFits` is false), it's placed again inside, and the message says the boundary changed.
  - The current steps, all with no boundary, should pass unchanged. That's the proof nothing else moved.
- **Run all 15 batch suites plus the Play-mode brain check** before the PR (commands at the end). The brain check uses mock rooms with no boundary, so it shouldn't change.

### 3. On the headset (Satyabrata can do this)

- [ ] Log the boundary's point count and size once at startup, to confirm `GetGeometry` works under our OpenXR setup.
- [ ] Use a roomscale boundary smaller than the scanned room. The slalom is placed inside it, or refuses with the boundary message.
- [ ] Albie being stuck: step out of the boundary, then back in.
  - If Albie carries on once you're back inside, that's the Quest pausing the app, and fix 1 is enough.
  - If Albie stays stuck after you come back, it's a second bug. Look at the focus-regain path: our code has **no** `OnApplicationPause`, `OnApplicationFocus` or `OVRManager.InputFocusAcquired` handlers, so check that Albie's NavMeshAgent, voice session and hub socket recover. Raise that one with the Pet owner.

### 4. Optional, Hudson's call (Core: `Main.unity` / `OVRManager`)

`OVRManager.shouldBoundaryVisibilityBeSuppressed = true` hides the boundary while passthrough is on. The SDK refuses it, with `Warning_BoundaryVisibilitySuppressionNotAllowed`, when passthrough is off. I haven't checked whether it also stops the Quest pausing the app when you step out. Test it on the headset before relying on it. Either way, fix 1 is still needed, because the course shouldn't send people past their boundary.

## Files

| File | Change |
|---|---|
| `Artifacts/Scripts/Room/PlayArea.cs` | new: the boundary polygon, `Contains`, `FromHeadset` |
| `Artifacts/Scripts/Room/IFloorPlan.cs` | `PlayArea Boundary { get; }` |
| `Artifacts/Scripts/Room/MrukFloorPlan.cs` | sets `Boundary` once, in the constructor |
| `Artifacts/Scripts/Room/MockFloorPlan.cs` | settable `Boundary` and `WithBoundary`, for the checks |
| `Artifacts/Scripts/Room/FloorSlalom.cs` | `FloorGrid` checks the boundary; the "boundary too small" message |
| `Artifacts/Scripts/Room/RoomHoops.cs` | the "boundary too small" message |
| `Artifacts/Scripts/Room/ArtifactGenerators.cs` | the "your boundary has changed" message |
| `Artifacts/Editor/SlalomCheck.cs`, `HoopCheck.cs` | the new steps |

Not touched: `Main.unity`, `Packages/manifest.json`, Project Settings, contracts.

## How to run the checks

- **Batch** (from the repo root, with the Editor closed). Exit 133 or 134 after the PASS line is Unity's harmless shutdown crash:
  `/Applications/Unity/Hub/Editor/6000.6.7f1/Unity.app/Contents/MacOS/Unity -batchmode -projectPath unity -executeMethod SlalomCheck.RunBatch -logFile slalom.log -buildTarget Android`
  - Run the same for all 15: `ArtifactChecks`, `ArtifactRunCheck`, `RepoActivityCheck`, `BioTemplatesCheck`, `HoopCheck`, `GltfTimelineCheck`, `RepoConnectorCheck`, `SimTemplatesCheck`, `SlalomCheck`, `VoiceRulesCheck`, `NotebookCheck`, `FiveInARowCheck`, `MLTemplateCheck`, `RoomSlalomCheck`, `ThingRuntimeCheck`.
- **Play mode:** menu *Forge > Artifacts > Check Brain Loop (Play)*.
