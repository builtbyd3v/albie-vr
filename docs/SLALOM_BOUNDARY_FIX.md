# Fix: the slalom is placed outside the Quest boundary

**For:** Nandani. **From:** Satyabrata (Artifacts owner, so you have my OK to work in `Artifacts/Scripts/Room/`). **Date:** 2026-11-16.
**Deadline:** submission is Nov 18, 12:00 PT. This is a bug fix after the freeze, so keep it small and keep every check green.

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

**New file `Artifacts/Scripts/Room/PlayArea.cs`:**
- `static Vector2[] WorldPolygon()` returns the boundary as a world xz polygon, or `null` when there's no boundary.
- It returns `null`, and logs once, if `OVRManager.loadedXRDevice != OVRManager.XRDevice.Oculus`, or `OVRManager.boundary?.GetConfigured()` is false, or there are fewer than 3 points.
  - Check `loadedXRDevice` **before** calling `GetGeometry`. Otherwise it logs an error in the Editor and the simulator.
- It reads `OVRManager.boundary.GetGeometry(OVRBoundary.BoundaryType.PlayArea)`.
  - Use **`PlayArea`**, the rectangle inside the drawn boundary. `OuterBoundary` is deprecated under OpenXR.
- The points are in **tracking space**. Convert them with the rig's `trackingSpace.TransformPoint(...)` (`FindFirstObjectByType<OVRCameraRig>()`), then keep x and z.
- `const float Margin = 0.3f` keeps the course this far inside the edge, so you can walk beside the lanes without touching the boundary wall.
- `static bool Inside(Vector2[] poly, Vector3 p, float clearance)` passes when the point is inside the polygon and at least `clearance` from every edge.

**In `MrukFloorPlan`:**
- Read the polygon **once**, in the constructor. `GetGeometry` allocates, and `IsFree` runs thousands of times per placement. `MrukFloorPlan.Current()` already builds a new plan for each placement, so a redrawn boundary is picked up the next time a course is placed.
- At the top of `IsFree`, return false when the polygon isn't null and `!PlayArea.Inside(polygon, point, clearance + PlayArea.Margin)`.
- No polygon means today's behaviour (Editor, simulator, Link, prefab rooms), so the existing checks don't change.

**Messages, with no contract change:**
- If the boundary leaves too little room, the generator still refuses with the existing code `no_free_floor` (C-12; don't add a new code).
- Make the **message** say why when the room alone would have fitted, for example: *"Your Quest boundary is too small for a slalom here. Redraw it bigger, or ask for hoops."* Compare a fit against the room without the polygon to decide which message to use.

**Saved courses** need no extra work. A saved slalom is rechecked by `StillFits` → `IsFree`, so one that's now outside the boundary is placed again.

### 2. Tests (no headset needed)

- **`MockFloorPlan`** gets an optional boundary polygon (null by default, so every current mock room behaves exactly as now). `IsFree` uses `PlayArea.Inside` the same way.
- **New steps in `SlalomCheck`, which `HoopCheck` can share:**
  - The 7 × 5 m living room with a 3 × 5 m boundary: the course is placed and every cone, gate, start and finish slot is inside the boundary by at least the margin.
  - The same room with a 2 × 2 m boundary: `no_free_floor`, and the message mentions the boundary.
  - A slalom saved before the boundary shrank isn't reused (`StillFits` is false) and is placed again inside.
- **Run all 15 batch suites plus the Play-mode brain check** before the PR (commands at the end).

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
| `Artifacts/Scripts/Room/PlayArea.cs` | new: the boundary polygon and `Inside` |
| `Artifacts/Scripts/Room/MrukFloorPlan.cs` | read the polygon once; `IsFree` checks it |
| `Artifacts/Scripts/Room/MockFloorPlan.cs` | optional boundary, for the checks |
| `Artifacts/Scripts/Room/FloorSlalom.cs`, `RoomHoops.cs` | the "boundary too small" message only |
| `Artifacts/Editor/SlalomCheck.cs`, `HoopCheck.cs` | the new steps |

Not touched: `Main.unity`, `Packages/manifest.json`, Project Settings, contracts.

## How to run the checks

- **Batch** (from the repo root, with the Editor closed). Exit 133 or 134 after the PASS line is Unity's harmless shutdown crash:
  `/Applications/Unity/Hub/Editor/6000.6.7f1/Unity.app/Contents/MacOS/Unity -batchmode -projectPath unity -executeMethod SlalomCheck.RunBatch -logFile slalom.log -buildTarget Android`
  - Run the same for `HoopCheck`, `ArtifactChecks`, `ArtifactRunCheck`, `RoomSlalomCheck`, `ThingRuntimeCheck` and the rest of the 15.
- **Play mode:** menu *Forge > Artifacts > Check Brain Loop (Play)*.
