# Artifacts and ML A: the plan to the freeze (Nov 12 to Nov 17)

> Written 2026-11-12 by Satyabrata (Person 5, ML A). Feature freeze is Nov 14; the Competition build is Nov 16; we submit Nov 17. Dates and rules are in `PROJECT_CONTEXT.md` sections 0.4 and 16.3.

## Where things stand (verified, not assumed)

Everything below is on `main` as of `e69b8db` (PR #62) and passes its checks in the Editor:

| Beat | State | Verified by |
|---|---|---|
| "Make me a slalom" in free floor space, run records, hoop course | Done. 6 mock rooms open a course, 4 that are too small are refused with a reason Albie can say. | `Check Slalom`, `Check Hoops`, `Check Brain Loop (Play)` |
| "Drive it twice and I'll learn it" in the app's `Main` | Done. One voice call records 2 laps, trains, and the Autopilot completes the course. The notebook gets the run and the training entry. | PR #60 and #62 notes; a hand-driven run of `Main.unity` in Play mode on MRUK LivingRoom02 |
| "What shipped this week?" with real commits | Done. `git log` on a computer; a saved copy on the headset, labelled with its day; the bundled sample only when both are missing. | `Check Repo Connector` |
| Biology (6), physics (2), fixtures (4) | Done, with no brand name in the app's UI (PR #58). | `Check Biology Templates`, `Check Physics Templates`, `Check Artifacts` 97/97 |
| Notebook: attempts, failures, lessons at session start, v1/v2 compare, saved anchors | Done. | `Check Notebook`, `Check Runs` |

Two things have **never run on a Quest**: the APK built 2026-11-10 21:11 (`albie-vr-headset/unity/Builds/Forge.apk`) predates #59 to #62, and the build the other session started on Nov 11 hasn't been installed yet.

**Nov 13 update.** `albie-vr-headset/unity/Builds/Forge-main.apk` was built at 02:33 from `main` at `b9bbfd3` (PRs #64 and #65 in), 128.7 MB, 0 errors. Checked inside the APK: the panel-avoidance and truck-hide code, the "3D model: public domain (CC0)" line in all six scenes, the glTFast shaders (`Shader Graphs/glTF-pbrMetallicRoughness`, so no magenta models), and the repo snapshot the build step saved that morning (29 commits, `saved_copy`). No brand name reaches the UI: the only "Grab3D" in the build is inside our own commit messages, and the repo city shows author and lines changed, not messages. **Not installed:** the headset never enumerated on USB or Wi-Fi during the session.

What the Editor can say about Quest performance, measured Nov 13 in `ArtifactsSandbox`: every artifact appears on the frame it's asked for, and the model streams in afterwards (glTFast's per-frame budget), so there's no wait. Each anatomy model is one mesh, one material and two 1024 px JPEG textures: brain 52k triangles, cell 52k, DNA 53k, eye 52k, heart 65k, plant cell 53k. The long frames seen in the Editor at open (60 to 100 ms) are within the Editor's own idle noise (190 ms), so they say nothing about a Quest; the one cost that is much slower there is decoding the two JPEGs when a model opens. Item 5 above stays a headset measurement.

## What's wrong, in order of what the judges would notice

1. **Panels cover each other in `Main`.** The artifact host comes to the user 0.8 m ahead and opens its panel 0.5 m to the right; the Hands panel is placed once, 0.95 m away, and never moves. Opening any artifact can put the artifact's panel on top of the Hands panel (seen twice while verifying #60). "FoV-aware layout" is a quarter of the Experience Design score. *Fix in this plan.*
2. **The brain truck is left standing on the floor** after the slalom closes, next to whatever opens next (seen with the heart). *Fix in this plan.*
3. **A permission sheet doesn't move the truck either**, because the host builds nothing until Allow is tapped. *Covered by the same fix: the truck hides on any close, not only on a new scene.*
4. **Hands-only laps in `Main` are untested.** Every lap in the checks is scripted or by keys and thumbstick. The Touch thumbstick is a controller; Stage 1 is pass/fail on hands. The hands transmitter (Person 1) writes `truck_manual` inputs, which the brain accepts (the "another writer" check), but nobody has driven a lap with it in `Main` on a headset. *Needs a Quest. Test script below.*
5. **No measured frame rate on a Quest** with the heart (65k triangles, beating) or the solar system (n-body, 9 bodies) open. The rubric says at least 60 fps. *Needs a Quest. Capture with OVR Metrics during the hands-only pass.*
6. **The sample commit rows read "Sample Author A".** Compliant (the rules want fictional people), and judges only see it when both git and the saved copy are missing. *Leave it.*

## The plan

**Nov 12 (today), no headset needed. PR straight to `main`, never stacked.**
- `ArtifactHost.ComeToUser` looks for other panels (the Hands panel, desk windows) before it picks a spot: if the host or its panel would land within 0.5 m of one, it turns 40° then 80° to either side until the spot is clear, at the same distance from the user. Checked in `Check Artifacts`.
- `SlalomBrain` hides itself (unseen, parked, no longer the host's Thing) whenever the host closes its scene, and comes back to the start when a truck course is built. It stays the active brain loop while hidden, so `brain_record` with the heart open still says "open the slalom first" instead of losing the truck. Checked in `Check Brain Loop (Play)`.
- README as-built notes.

**Nov 13, with the headset (Satyabrata on the Quest, the APK from `main` after today's PR):**
1. Hands only, controllers in a drawer. Cold start to "make me a slalom" to Allow to a hand-driven lap to "drive it twice and I'll learn it" to the Autopilot completing the course. Time it: it has to fit the 10-minute guideline with the rest of the demo.
2. "What shipped this week?": Albie must say it's a saved copy from a day, not sample data.
3. Open the heart, the solar system and the repo activity in turn. Read the fps from OVR Metrics with each one open. Anything under 72 fps gets written down with the artifact's name.
4. Each artifact's panel must sit inside the view without covering the Hands panel, from wherever the head was when it opened.
5. A room nobody has tested: a different room scan than the dev room. The slalom either fits or is refused with a reason.

**Nov 14, feature freeze.** Fixes only from here: what the Nov 13 pass found, in the order above.
- *Done Nov 14, no headset:* the host lands on a real table when one is in front (the biggest `TABLE` the person sees over, from MRUK), pulled inside its edge, and keeps itself and its panel 0.3 m off the room's walls, swapping the panel to the left when the right is blocked. Verified in `Main` on MRUK's LivingRoom02: standing at the dining table the heart stands on its top; seated it still does; 0.5 m from a wall it turns aside instead of standing in it; in the open nothing changes. `Check Artifacts` 107/107. The headset pass should include one open at a desk and one near a wall.

**Nov 15 to 16.** Help Person 1 with the hands-only passes and the video. `ArtifactShot` (Editor) gives clean stills of any artifact for the Devpost gallery.

**Nov 17.** Submitted.

## Cross-team asks, so nobody is surprised

- **Hudson (Core):** PR #57 (on-device models) waits for a yes or no; it blocks nothing. The host now avoids other panels by looking for `ForgePanel`s in the scene; if Core adds a panel registry with reserved spots, the host should use that instead.
- **Person 1:** a hand-driven lap in `Main` is the one thing on the ML A list that only the hands transmitter can prove. Nov 13, with the headset.
- **Person 3:** when the connector host lands, set `ArtifactData.Provider` before the first scene loads and the local fixture connector steps aside by itself. Until then the headset shows the saved copy.
- **Devpost text:** credit the six CC0 models (Grab3D, links in `Artifacts/README.md`) there, since the app may not name a brand. A draft of the whole artifacts section, the credit lines and a gallery of stills is in `docs/DEVPOST_ARTIFACTS.md` and `docs/devpost/stills/`.
- **Hudson (Core), before the Nov 16 build:** the APK still carries the template's identity. The Quest library will show it as **"unity"**, package `com.UnityTechnologies.com.unity.template.urpblank`, version `0.1.0` (`ProjectSettings.asset`: `productName`, `applicationIdentifier`, `bundleVersion`). Those are Project Settings, so Core's to change; the Competition channel upload needs a stable package name from the first upload on.
