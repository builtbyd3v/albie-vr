# Station: demo-content

Read `docs/demo/briefs/_COMMON.md` first and follow it. You do mostly non-Unity work, so you rarely need the editor lock.

You own: `unity/Assets/_Forge/Demo/Resources/DemoAudio/`, `Demo/Resources/DemoImages/`, `Demo/Resources/DemoModels/`, `hub/demo/` (new package), `hub/scripts/demo_*.py`, and one line in `hub/main.py` to include your router (add only that line plus its import).

## 1. Albie's lines as audio (do first)

Albie's voice is the Grok speech-to-speech voice in `hub/agent/voice.py` (keys in `hub/.env`, loaded by `agent.app.load_env`). Write `hub/scripts/demo_record_lines.py` that, for each line below, gets the same voice to say the text verbatim and saves 24 kHz mono 16-bit WAV to `unity/Assets/_Forge/Demo/Resources/DemoAudio/<clip>.wav`. Read `voice.py` and `speech.py` to find how the hub makes Albie say a given text (`speech.speak`) and how PCM frames come back; reuse that path, don't invent a new provider. Trim leading and trailing silence, normalise to about -16 LUFS (or peak -1 dB). If the voice service can't be reached or refuses verbatim text, report that at once (`report-outcome --outcome failed`) after doing parts 2 to 4.

| Clip | Beat | Text |
|---|---|---|
| `b1_hello` | B1 | Hey! What do you feel like building today? |
| `b2_open` | B2 | Here's yesterday's quad. |
| `b2_flying` | B2 | She's flying. What should the camera do? |
| `b3_search` | B3 | Got it. I'll find a camera that fits your battery, and a model that can track you. |
| `b4_verdict` | B4 | This one. It runs off the five-volt rail your battery already powers, it's sharp enough to lock onto your face at ten meters, it's twenty-four dollars, and it can be here in three days. |
| `b5_brain` | B5 | For the brain, this tracking model is a good start. I'll fine-tune it on you, then push it to your private repo. |
| `b5_pushed` | B5b | Done. Version one is in your repo. |
| `b6_building` | B6 | Building a flight test in your room. |
| `b6_tracking` | B6 | Tracking you. I'll stay two meters back. |
| `b7_done` | B7 | Nice flight. Want me to log this run? |

## 2. Product pictures (`DemoImages/`)

Four made-up small camera modules for the research cards, plus two for the model hub lane. Use Blender through the Blender MCP tools (`mcp__blender__*`: load them with ToolSearch): model simple, believable camera boards (a square PCB, a lens barrel, a ribbon connector; vary colour, lens size, a gimbal on one), render each on a transparent or soft dark background, 512x512 PNG, studio light. Names: `cam_a.png` (the winner: compact board, wide lens), `cam_b.png`, `cam_c.png`, `cam_d.png`. For the model cards: `model_a.png`, `model_b.png` (abstract: a face with a tracking box and keypoints, flat style). No logos, no text, no real brands. If Blender is not open or the MCP fails, build the same simple boards from Unity primitives in a small editor script under `Demo/Editor/Content/` and render them with a camera to PNG instead. Set the Unity import to Sprite (2D and UI) if you touch the editor; otherwise leave it to the stations that use them.

## 3. The camera 3D model (`DemoModels/`)

Export the winner (`cam_a`) as `camera_a.glb` (under 20k triangles, real scale: about 25 x 25 x 20 mm), origin at the back of the mount. The Flight station loads it with glTFast (see `Artifacts/Scripts/ArtifactGltf.cs`) or as an imported asset; also export `camera_a.fbx` so it imports as a normal Unity model.

## 4. Laptop remote (`hub/demo/`)

A FastAPI router (`hub/demo/router.py`, pydantic models, type hints) with:
- `GET /demo`: one small HTML page, big buttons Back / Next / Reset and a beat list (B0 to B7) to jump to; dark, works on a phone.
- `POST /demo/next|back|reset`, `POST /demo/goto {beat}`: each broadcasts a hub event `demo.next` / `demo.back` / `demo.reset` / `demo.goto` (payload `{beat}`) to the headset over the hub's existing event bus (read `hub/events.py` and how another router publishes events to Unity, e.g. `hub/desk/router.py`).
- A pytest in `hub/tests/test_demo.py` that hits each endpoint and checks the event was published.

## Done when
All ten WAVs exist and sound like Albie (or the failure is reported), six PNGs and the camera model exist, the remote page works with tests passing, all committed. Report and stop.
