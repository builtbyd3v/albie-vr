# Testing the slalom on a Quest (hands only)

> Written 2026-11-16 by Satyabrata, for whoever tests tonight. Build: `Forge-slalom-board.apk`, from branch `satyabrata/slalom-scoreboard-to-you` (into `feat/albie-os`). It has Hudson's OS menu, the merged Artifacts and ML work, the new slalom buttons, and the scoreboard that comes to you and says where the course is.

## What the slalom is for

It is the machine-learning beat of the demo, in three steps:

1. **A course in your real room.** Albie lays cones and gates on your free floor, around your furniture.
2. **You drive a toy truck through the gates twice**, with your hands.
3. **Albie learns from your two laps and drives it himself.** A small neural network trains on the headset in a few seconds, then the truck runs the course with nobody touching it.

The biology models are things to look at. The slalom is something Albie learns to do from you.

## Before you start

- **Scan the room.** Quest Settings, Physical space, Space setup. Mark the walls and the big furniture (couch, table). With no scan the slalom can't be laid.
- **Clear floor.** The smallest course needs a free strip about 4.6 m long and 0.8 m wide. In a small room the tile says there isn't enough floor, with the reason.
- **Controllers down.** Stage 1 is judged hands only.
- **Install:**

```
adb install -r Forge-slalom-board.apk
```

If it fails with `INSTALL_FAILED_UPDATE_INCOMPATIBLE`, an older build signed on another machine is installed. Remove it first:

```
adb uninstall com.UnityTechnologies.com.unity.template.urpblank
```

The app is listed as **"unity"** under Unknown Sources (the real name and package id are still to be set by Core). Allow the **spatial data** permission when the app asks on first launch.

## The test, step by step

1. **Open the menu.** Turn your left wrist as if checking a watch and poke the small button with your right index finger. Or use the corner button low in your view, or the round button on a table.
2. **Tap the Slalom tile** (it says "on your floor").
3. **Tap Allow** on the sheet that appears in front of you. It asks once, for the room and the truck.
4. **The scoreboard comes up in front of you.** Its first line says where the course starts, for example "The course starts behind you, about 3 m away. The truck waits in the green box." Albie says the same. The course is laid where your floor is free: look for orange cones, a green box (the start, with the orange truck in it) and a white box (the finish).
5. **On the scoreboard, tap "Drive a lap"**, then turn to the course.
6. **Drive the truck through every gate to the white box.**
   - Pinch your right thumb and index finger together: the truck goes. A light pinch crawls.
   - Tilt the pinching hand: pinky side down turns right, thumb side down turns left.
   - Pinch with your left hand to reverse.
   - Look at the truck while you drive, not at a panel: a pinch while pointing at a panel also clicks it.
   - Hitting a cone or a wall ends the lap. Only clean laps count.
7. **Do a second clean lap** (tap "Drive a lap" again).
8. **Tap "Let Albie drive."** Training takes a few seconds, then the truck drives the course by itself. The scoreboard lists your laps and Albie's run, with times and cones hit.
   - With fewer than two clean laps, "Let Albie drive" first asks for the missing laps, then carries on by itself.
9. **"Reset"** puts the course back for another run.

By voice, the same steps are "make me a slalom", tap Allow, then "drive it twice and I'll learn it".

## What to write down

| Check | Pass when |
|---|---|
| Slalom opens from the menu | The Allow sheet appears in front, and after Allow cones appear on the floor |
| You can find it | You see the course and the scoreboard within a few seconds of looking around |
| Pinch drives | A full pinch moves the truck; letting go stops it |
| Tilt steers | Pinky down turns right, thumb down turns left |
| Two clean laps | Both count on the scoreboard |
| Albie drives | After "Let Albie drive" the truck finishes the course alone |
| Frame rate | Smooth throughout; note any stutter and when |

Write down anything that felt wrong, especially: the pinch too sensitive or too dull, steering too fast or too slow, the course somewhere you couldn't find it.

## Known problems (not fixed yet)

- **The scoreboard is not beside the course any more.** It comes to you, so while you drive it may be behind you. You can grab it by its handles and move it.
- **The pinch thresholds and the tilt for full lock are a first guess.** They have never run on a headset.
- **Hudson's Drive pad also drives the truck.** If both are on screen, use one.
- **The hoop course is hidden** from the menu: it is for a drone, and the app has none.
