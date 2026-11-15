# Devpost text: artifacts, the notebook and ML A

> Draft 2026-11-13 by Satyabrata (Person 5 and ML A), for whoever fills in the Devpost form. Paste what fits; cut the rest. The app itself names no brands (competition rule), so the model credits below go **here**, not in the app. Stills for the gallery are in `docs/devpost/stills/` (1600×1000 PNG, rendered from the Editor with `ArtifactShot`; see the end for how to retake them).

## One paragraph

Albie doesn't only talk about things: it makes them. Ask for a slalom and it lays a cone course on your real floor, in the space your room actually has, and refuses with a reason when the room is too small. Drive the truck round twice and say "learn it", and Albie trains a small on-device policy from your two laps, writes the attempt into its lab notebook, and the truck drives the course by itself. Ask what shipped this week and your repo's real commits stand up as a small city on your desk. Ask about the heart and a cutaway heart beats on the table at 72 a minute with every chamber and valve labelled, and Albie traces the blood through it. Every one of these is a data-driven artifact: a manifest, a scene file and at most one model, validated before anything is built, kept in a versioned library, and recorded in the notebook so Albie remembers what worked.

## What to list as features

- **Room-aware courses.** "Make me a slalom" or "make me a hoop course" reads the room scan (Meta's Mixed Reality Utility Kit) and fits the course to the free floor. Six room layouts open a course; four that are too small are refused with a sentence Albie can say.
- **Learn it in two laps.** One voice request records two laps, trains a policy on the headset, and hands the truck to the Autopilot, which completes the course. The notebook gets the run and the training entry, with loss and result.
- **What shipped this week.** The repo-activity artifact shows real commits as a city: one block per commit, height for lines changed, colour per author. On a computer it reads `git log`; on the headset it reads a copy saved into the build, labelled with the day it was taken; it falls back to a clearly labelled sample only when both are missing.
- **Biology you can walk round.** Cell, heart, brain, plant cell, eye and DNA, each with measured labels and a guided tour (follow a protein, trace the blood, tour the lobes, follow the sunlight, follow the light, spin the helix).
- **Physics you can break.** Two double pendulums started 0.01° apart that split after five seconds; the Sun and four rocky planets under real gravity, with a slider for the Sun's mass that throws the planets out.
- **A lab notebook.** Attempts, failures and lessons, read back at the start of a session; v1 versus v2 comparisons; anchors saved in the room.
- **Hands first.** Every panel is a Forge panel and works with Interaction SDK ray and poke; the truck can be driven by hand gestures. Artifacts step aside for each other so no panel covers another.
- **Permission before data.** An artifact that needs a connector or the room asks once, in a sheet, and nothing is built until you allow it.

## Model credits (required: the app can't show these)

The six anatomy models are public domain (CC0) from Grab3D, simplified with gltf-transform to 52k to 65k triangles and 1024 px textures:

- Animal cell cutaway: https://grab3d.com/models/anatomy/human-cell/
- Human heart cutaway: https://grab3d.com/models/anatomy/human-heart-cutaway/
- Human brain: https://grab3d.com/models/anatomy/human-brain/
- Plant cell: https://grab3d.com/models/anatomy/plant-cell/
- Human eye: https://grab3d.com/models/anatomy/human-eye/
- DNA double helix: https://grab3d.com/models/anatomy/dna/

Suggested line for the form: "Anatomy models: public domain (CC0) from Grab3D, simplified for the headset."

## Built with (for the form's field)

Unity 6 (URP), Meta XR SDK 207 with the Mixed Reality Utility Kit and Interaction SDK, glTFast, Newtonsoft.Json, and a small on-device policy trainer in C#. Data files: `albie.artifact/1` manifests and `albie.scene/1` scenes.

## Gallery stills

| File | What it shows |
|---|---|
| `stills/heart.png` | Heart cutaway, 13 labels, panel with Trace the blood |
| `stills/cell.png` | Animal cell with the wedge removed |
| `stills/brain.png` | Brain, left side, lobes labelled |
| `stills/plant_cell.png` | Plant cell tipped open |
| `stills/eye.png` | Eye at 8× with the optic nerve |
| `stills/dna.png` | DNA helix |
| `stills/double_pendulum.png` | Two pendulums on one pivot, dial behind |
| `stills/solar_system.png` | Sun and four planets with their orbits |
| `stills/repo_activity.png` | This repo's commits as a city |
| `stills/slalom.png` | A 5-gate slalom laid around the furniture of a mock living room, the truck at the start, the finish box, the scoreboard by the wall (Albie said: "This room is tight, so I made a shorter course") |
| `stills/hoop_course.png` | Four hoops in a line in the same room, the ideal line through them |

The two course stills are from the slalom sandbox with a mock room (grey boxes for the couch and shelves), not passthrough. Real headset footage (passthrough, hands) must replace all of these for the video; the rules want real footage. The stills are for the gallery only.

**To retake them:** open `Assets/_Forge/Artifacts/ArtifactsSandbox.unity`, enter Play, make the artifact (the sandbox keys: `h` heart, `c` cell, `b` brain, `p` plant cell, `v` eye, `n` DNA, `k` double pendulum, `o` solar system, `7` repo activity, `2` slalom), then from the `unity` CLI:

```
unity command eval 'return ArtifactShot.Take("/absolute/path/heart.png", 0f, 10f, 1600, 1000, 1.15f);'
```

Yaw 0 is the user's view: model on the left, panel on the right. Point the sandbox camera at the host first, or the shot looks at the panel's back.

For the courses, open `SlalomSandbox.unity` instead, enter Play (it starts in the first mock room, which is big enough; `n` cycles the mock rooms), press `2` (slalom) or `7` (hoops), then pass `room: true` so the shot frames the course on the floor and the truck as well as the host:

```
unity command eval 'return ArtifactShot.Take("/absolute/path/slalom.png", 35f, 30f, 1600, 1000, 1.35f, true);'
```
