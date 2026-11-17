# Albie: competition video script

> Written 2026-11-16. Video rule (PROJECT_CONTEXT 0.4): **under 3:00**, public on YouTube or Vimeo, real Quest or simulator footage, no AI-generated video. Target **2:50**.
> The hero section (0:00 to about 2:05) is one continuous story recorded on the Quest. It is **scripted**: the Director (`unity/Assets/_Forge/Demo/`) plays each beat when Hudson finishes his line. The last 45 seconds are clips of features that already exist, under voiceover.
> Beat ids (B1, B2 ...) and action names match `unity/Assets/_Forge/Demo/Resources/demo_script.json` and the plan in `TONIGHT_PLAN.md`.

## Rules the footage has to follow

- **No brand names in the app's UI.** The UI says "model hub", not Hugging Face, "private repo" or "source control", not GitHub, and the camera cards use made-up product names. The voiceover and the Devpost text can name the real services.
- **No real people other than the team.** The face the drone tracks is Hudson's.
- **Hands only.** No controllers on screen, ever.
- **Seated start, within reach.** Hudson starts reclined. Standing up for the flight test is fine, because that beat is the room test, but every panel sits within 2 ft of the chair.
- Scripted UI recorded in the headset is real footage. Say in the Devpost description which parts are live and which were scripted for the video (see `JUDGE_PATH.md`).

## Hero story: "add a tracking camera to yesterday's drone"

| Time | Beat | Hudson (says / does) | Albie (says) | On screen |
|---|---|---|---|---|
| 0:00 | B0 | Puts the headset on, leans back in the chair. | (none) | Passthrough living room. Quiet. Title card goes on in the edit. |
| 0:05 | B1 `albie.summon_hand` | Holds out his left hand, palm up. | "Hey! What do you feel like building today?" | Albie runs over, hops up onto the palm, looks up at him. |
| 0:12 | (Hudson) | "Let's open up the drone we built yesterday. I want to add a machine-learning camera to it." | | Albie nods, Thinking pose. |
| 0:18 | B2 `drone.open`, `drone.takeoff` | Watches. | "Here's yesterday's quad." ... "She's flying. What should the camera do?" | The project card "Drone v7, built yesterday" opens. The quad builds itself from its CAD parts in front of him, the props spin up, and it lifts off to hover at eye level about 0.85 m away. |
| 0:30 | (Hudson) | "Facial recognition and object tracking, so it can follow me while I bike." | | |
| 0:36 | B3 `research.open`, `research.cameras`, `research.models` | Watches. Looks left, then right. | "Got it. I'll find a camera that fits your battery, and a model that can track you." | **Left panel, Research:** a "Searching" line types out queries (for example "small camera 5V 60fps wide FOV" and "low latency camera for drones under 30 g"), then source cards stream in, each with a picture, specs (resolution, fps, FOV, voltage, weight, price, ships in) and green/red checks against the drone (5 V rail, weight budget, mount). **Right panel, Model hub:** model cards stream in (task, parameters, licence, on-device latency, downloads). |
| 0:58 | B4 `research.verdict`, `drone.mount_camera` | Pokes "Add to parts list" on the verdict card. | "This one. It runs off the five-volt rail your battery already powers, it's sharp enough to lock onto your face at ten meters, it's twenty-four dollars, and it can be here in three days." | The winning card lifts out and grows. Four checks light up: power, resolution, price, shipping. A 3D model of the camera flies off the card and clicks onto the drone's nose. |
| 1:15 | B5 `pipeline.open`, `pipeline.pull`, `pipeline.train` | Watches. | "For the brain, this tracking model is a good start. I'll fine-tune it on you, then push it to your private repo." | **Pipeline panel** (low, centre): three steps, Pull, Train, Push. Pull: the model card from the right panel slides in and fills a progress bar. Train: a loss curve falls and an accuracy curve rises, with an epoch counter, a "20x" fast-forward badge and sample frames with tracking boxes. |
| 1:35 | B5b `pipeline.push` | Pokes **Confirm** on "Push to private repo?" | "Done. Version one is in your repo." | The commit card shows the repo name, the file list (weights, config, eval report), the size and "private". This shows the confirm rule: nothing leaves without a yes. |
| 1:42 | (Hudson) | "While that trains, I want to test it. Build me a flight test so it can follow me around the room." | | |
| 1:48 | B6 `drone.follow_start`, `dronecam.show` | Stands up and walks around the room. | "Building a flight test in your room." ... "Tracking you. I'll stay two meters back." | A scan shimmer runs over the room's walls and furniture. The drone swings round to face him, keeps 2 m away at head height and follows as he walks, going round furniture. A **drone-cam window** floats at his left wrist showing the drone's view, with a tracking box on him ("LOCKED 0.97"), the distance and the altitude. |
| 2:05 | B7 `drone.follow_stop` | Holds his palm out again. | "Nice flight. Want me to log this run?" | The drone comes back and hovers over his hand. Hard cut. |

### Hudson's lines (memorise these; the Director advances on the end of each one)

1. "Let's open up the drone we built yesterday. I want to add a machine-learning camera to it."
2. "Facial recognition and object tracking, so it can follow me while I bike."
3. (poke "Add to parts list")
4. (poke "Confirm")
5. "While that trains, I want to test it. Build me a flight test so it can follow me around the room."
6. (hold out your palm)

If a beat fires early or late, keep going: we cut in the edit. Back and Reset are on the laptop remote (`TONIGHT_PLAN.md`, section 6).

## Clips (2:05 to 2:50), under voiceover

Record each clip as its own take after the hero takes. Each clip is 5 to 8 seconds. Show hands touching things.

| # | Clip | What to show | Where it lives today |
|---|---|---|---|
| C1 | Artifacts | Open the Artifacts tab, launch the hoop course in the room, then the slalom with the truck going round real furniture. | OS menu, Artifacts tab; `Artifacts/` (hoop_course, slalom) |
| C2 | 3D models | The drone CAD with exploded view, a params slider (arm length) changing the model live, and the versions panel. | `CAD/` (CadViewer, params panel) |
| C3 | Messaging | Albie drafts a reply, the draft card shows, Hudson pokes Send, the confirm step shows. | `Messages/` (DraftCard, Slack panel) |
| C4 | Machine learning | Neural Lab: the network lights up while it trains, and the results wall. | `ML/` (NeuralLab, ResultsWall, TrainingDash) |
| C5 | The OS | Wrist-turn to open the menu, the three tabs, Agents bringing the crew home. | `OS/` |
| C6 | RC truck (optional) | Driving the truck with the hand transmitter. | `Controllers/`, OS Drive pad |

## Voiceover (draft, about 110 words for the 45 seconds of clips, plus 2 or 3 lines over the hero)

- Over B3: "Albie is an agent that lives in your room. It researches parts against your actual design..."
- Over B5: "...pulls an open model, fine-tunes it in the cloud and commits it to your repo, and only after you say yes."
- Over B6: "Then it builds a test in your real room, so you can try the idea before you buy a single part."
- Over the clips: "Every project is built from the same pieces: artifacts you can run in your space, 3D models you can take apart with your hands, agents that handle your messages, and machine learning you can see. Albie: build anything, with your hands." (Name Meta Quest, the model hub and the other services here, not in the UI.)

## Takes

- At least 5 clean hero takes. The edit can join takes at any Hudson line.
- Record on the Quest itself (on-device capture includes passthrough; check this in the first hour). Fallback: record over Link from the editor.
- Mic: Hudson's voice from the headset mic is fine for timing. Re-record his lines as voiceover only if the headset audio is bad.
