# Station: demo-flight

Read `docs/demo/briefs/_COMMON.md` first and follow it.

You own: `unity/Assets/_Forge/Demo/Scripts/Flight/`, `Demo/Prefabs/DemoDrone.prefab`, `Demo/Prefabs/DroneCam*.prefab`, `Demo/Editor/Flight/`, `Demo/Sandboxes/FlightSandbox.unity`, and any materials/render textures under `Demo/Art/Flight/`.

Beats B2, B4 (mount), B6 and B7 in `VIDEO_SCRIPT.md`. This is the most important visual in the video: a small drone flying in a real room, then following Hudson around it.

## The drone

- "The drone we built yesterday" is the quadcopter CAD mock: `unity/Assets/_Forge/Mocks/cad/quadcopter/v7/` (and v5/v6); it has a `model.glb.bytes`, `params.json` and `metadata.json`. Load the glb at runtime the way the CAD module does (`CAD/Scripts/CadModel.cs`, `CadViewer.cs`, glTFast) or import once in the editor into a prefab, whichever is faster and reliable on device. Find the props (part names in `params.json`, e.g. `PROP_*`) so they can spin. Real size: arm length 110 mm, so about 25 to 30 cm across. Check the scale looks right in passthrough at 0.85 m.
- `drone.open` (B2, ~3 s): the parts fly in from slightly outside their places and click together (an exploded view collapsing; `CAD/Scripts/CadExploder.cs` may help), with a soft light sweep. Raise `drone.built`. It sits on a small glowing pad at `DemoLayout.Slot.Drone`, a little lower.
- `drone.takeoff`: props spin up (blur disc or fast rotation plus a faint disc), a quiet motor hum (procedural or a short loop, low volume), the drone rises to hover at `Slot.Drone` with a gentle bob and small tilt noise. Raise `drone.hovering`.
- `drone.mount_camera` (B4): the camera model (`Demo/Resources/DemoModels/camera_a.glb` or `.fbx`, from the `demo-content` station; until it exists use a small dark box with a lens cylinder) appears at `args.from` (or at `Forge.Demo.Research...VerdictPosition` if you can find it without a compile dependency; use args), arcs to the drone's nose in ~1 s, a tiny click and flash. Raise `drone.mounted`.

## The flight test (B6)

- `drone.follow_start` `{distance: 2.0}`: a scan shimmer runs once over the room (MRUK room mesh / scene anchors through `MRUK`, see `Artifacts/Scripts/Room/MrukFloorPlan.cs` and how `RoomHoops` reads the room; in the editor/desktop rig use `Core/Rooms/SimLivingRoom.json` or the mock floor plan). Then the drone flies a smooth path to keep `distance` metres from the head, at head height minus 0.1 m, on the side the head faces, always yawed to face the head. Use a critically damped follow (SmoothDamp on position, max speed 1.5 m/s, banking into turns). Stay above 0.5 m and under the ceiling, keep 0.4 m off walls and furniture (sample the room's boxes; if no room data, a simple floor/ceiling clamp). Raise `drone.following`.
- `drone.follow_stop`: fly back to hover above the left palm if tracked (else `Slot.Drone`), raise `drone.home`.
- `demo.reset`: park on the pad, props stopped.

## The drone-cam window

- A camera on the drone's nose renders to a 512x384 RenderTexture at 30 fps (render every other frame; Quest performance), shown in a small rounded window (about 16 x 12 cm) floating near the left wrist (Interaction SDK hand pose; fall back to `Slot.Left` lowered) and turning to face the head.
- Passthrough isn't visible to a Unity camera, so give the drone camera its own look: the room mesh drawn as clean wireframe or flat shaded surfaces on a dark background, plus a simple body marker at the user's head and shoulders (a capsule and sphere only that camera sees, via a layer). A tracking box is drawn around the marker in screen space (project the head bounds into the drone camera), with a label "LOCKED 0.97" (vary 0.95 to 0.98), plus a small HUD: distance, altitude, a REC dot. When the target leaves the view the box turns amber "SEARCHING".
- `dronecam.show` / `dronecam.hide`.

## Done when
Your Play Test (desktop rig: walk the rig with keys or move the camera along a scripted path) shows: built, hover, mount, follow while the head moves around a mock room, drone-cam window with the box tracking, return home. Screenshots at each step plus 3 from the drone camera. No allocations per frame in the follow loop. Committed, report, stop.
