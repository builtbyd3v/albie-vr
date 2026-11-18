# Station: demo-research

Read `docs/demo/briefs/_COMMON.md` first and follow it.

You own: `unity/Assets/_Forge/Demo/Scripts/Research/`, `Demo/Resources/research_data.json`, `Demo/Prefabs/Research*.prefab`, `Demo/Editor/Research/`, `Demo/Sandboxes/ResearchSandbox.unity`.

This is the shot that has to feel like a modern AI assistant "browsing": the way a chat assistant shows its searches and source previews while it thinks, but as spatial panels in the room. It is beats B3 and B4 in `VIDEO_SCRIPT.md`.

## Build

1. **`research_data.json`** (also the shape the real `research.search` tool will return later, see `docs/demo/JUDGE_PATH.md` J3/J4; keep it clean and documented in a comment block at the top of your main script):
   - `constraints`: the drone's, shown as chips: `5 V rail (from 4S battery BEC)`, `under 30 g`, `20 x 20 mm mount`, `under $40`.
   - `queries`: 4 search lines that type out, e.g. "small camera module 5V 60fps wide FOV", "low latency camera for drones under 30 g", "camera board 1080p face detection range", "fast shipping camera module".
   - `cameras`: 6 made-up products (no real brands): `name`, `image` (`cam_a` to `cam_d`; reuse images for the extra two; the `demo-content` station renders them into `Demo/Resources/DemoImages/`; until they exist, draw a neutral placeholder), `source` (made-up shop domain, e.g. "partsbay.example"), `specs` {resolution, fps, fov, voltage, weight_g, price_usd, ships_days}, `checks` {power, weight, mount, price} each pass/fail with a short reason. Exactly one winner (`cam_a`, with a made-up name that is not a real camera or electronics brand you know of, e.g. "Tern C2 Mini", 1080p60, 120° FOV, 5 V, 9 g, $24, ships in 3 days) passes all four; others fail one or two plausibly (12 V only; 48 g; $89; 2-week shipping).
   - `models`: 5 model-hub cards: `name` (descriptive, e.g. "face-track-nano", "person-detect-s", "pose-lite"), `image` (`model_a`/`model_b`), `task`, `params` ("3.2 M"), `license` ("Apache-2.0"), `latency_ms` on a small board, `downloads` ("48k"). The chosen one is marked `pick: true`.
   - `verdict`: the winner id and 4 lines: power, resolution ("locks onto a face at 10 m"), price, shipping ("here in 3 days").
2. **Research panel** (`Slot.Left`): header "Research" with a thinking shimmer line; queries type out one by one (~25 chars/s) each with a small spinner that turns to a check; under them, camera cards stream in (fade and slide, 0.35 s apart) in a 2-column grid: picture, name, source line, 3 key specs, and check chips that flip from grey to green or red one by one. Total B3 run about 12 to 15 s. Raise `research.cameras.done` at the end.
3. **Model-hub lane** (`Slot.Right`): header "Model hub", one query line, then 5 compact model cards streaming in; the picked one gets a highlight ring at the end. Raise `research.models.done`.
4. **Verdict** (`research.verdict`): the winner card lifts out of the grid and grows into a larger card in front (between Left and Center), the other cards dim, the four verdict lines appear with green checks one by one, and a poke button **"Add to parts list"**. Raise `research.verdict.shown` when it's settled. When poked (poke or mouse): a pressed state, a check, the card shrinks back, raise `research.added`. Expose the card's world position (public static `Vector3 VerdictPosition`) so the Flight station can fly the camera model from there (`drone.mount_camera` args).
5. `research.open` / `research.close` (fade in/out the panels), `demo.reset`.

## Done when
Your Play Test fires open, cameras, models, verdict, a simulated poke, close, and the screenshots at each step look polished at seated distance (check text size and spacing at 0.62 m). Committed, report, stop.
