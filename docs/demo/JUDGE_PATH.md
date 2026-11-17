# Judge path: what has to be real so a judge can do what the video shows

> Written 2026-11-16. The video (`VIDEO_SCRIPT.md`) is scripted. This is the list for the rest of the team: what has to work **live** so a judge, in their own room, hands only, can do something close to it within 10 minutes. Deadline 2026-11-18, 12:00 PM PT. The app has to keep working for judges until about Dec 11.
> Rule of thumb: real where it's cheap and safe, **replay of a real run** where it's slow or costs money (training), and say which is which, in the app ("replay of a recorded run") and in the Devpost text.

## The judge's version of the story

1. Put on the headset, hold out a hand: Albie comes. *(Real: summon exists; palm-up trigger is new.)*
2. "Open the drone project." The drone loads from its CAD files and hovers. *(Real: CAD mock files exist.)*
3. "Find a camera so it can follow me." Albie searches the web and the model hub and shows cards with fit checks. *(New: live search tools.)*
4. "Train it." Albie shows the fine-tune, labelled honestly. *(Replay of a real recorded run, or a short real job.)*
5. "Push it." Confirm card, then a real push to the judges' demo repo. *(Real, via the demo machine account, D-30.)*
6. "Test it in my room." The follow-me flight test runs as an artifact in their room, with the drone-cam window. *(Real, on device.)*

## Work list

Owners are the 16.1 leads in `PROJECT_CONTEXT.md`; reassign as you like. P0 = needed for the judge path. P1 = makes it much better. Each item says what to reuse from tonight's demo build.

| # | Item | P | Owner | Reuse | Done when |
|---|---|---|---|---|---|
| J1 | **Palm-up summon.** Holding out a palm (either hand, ~1 s) calls Albie to hop onto it and start listening. Off while a window is being dragged. | P0 | Person 1 (hands) | Director's `albie.summon_hand` motion | Works 9 of 10 tries in the headset, never fires while poking UI. |
| J2 | **"Open project" by voice** opens the drone project and plays `drone.open` / `drone.takeoff` from the real project files, not the script. | P0 | Person 2 | Flight's drone build and hover | The agent's tool call opens the project; the drone hovers in the judge's room. |
| J3 | **`research.search` tool (hub).** Web search through a search API, then the LLM pulls a parts table (name, specs, price, shipping) and checks each part against the project's constraints (voltage rail, weight budget, mount). Returns the same JSON as `Demo/Resources/research_data.json`, streamed card by card. Brand names stripped from what the UI shows (the vendor goes in a small "source" line only if the rules allow; check with the submission owner). | P0 | Person 3 | Research panel takes the same JSON live | A spoken request shows real cards in under 15 s; no crash with zero results. |
| J4 | **`modelhub.search` tool (hub).** Public model-hub search API (no key needed for search), filtered by task and licence, sorted by downloads; returns `research_data.json`'s model shape. | P0 | Person 4 (ML A) | Model-hub lane | Real model cards with real licence and size. |
| J5 | **Training, honest version.** Default: a **replay of one real fine-tune** we run tonight or tomorrow (a small face/person tracker on a tiny dataset), shown with the label "replay of a recorded run". Option: a real cloud job with a timeout and spend cap (PROJECT_CONTEXT 8.5), behind a setting. Metrics come from the real log (`hub/ml/logtail.py`). | P0 | Person 4 (ML A and B) | Pipeline panel's Train step reads the log | The judge sees real numbers from a real run, labelled. |
| J6 | **Push to the demo repo.** A real commit of the weights and config to a sample repo in the judges' demo org, through the machine account (token only in the cloud, fine-grained, expiry after Dec 11), after the Confirm poke. Nightly reset of the demo repos. | P0 | Person 3 | Pipeline's Push step and confirm card | The commit appears in the demo org; nothing pushes without Confirm. |
| J7 | **Follow-me flight test as an artifact.** Make the demo's follow flight a real artifact template (`drone_follow`, like `hoop_course`), generated from the room scan (MRUK), with the drone-cam window. Runs without the hub. | P0 | Person 5 | Flight's follow controller and drone cam | Opens from the Artifacts tab and from "test it in my room"; 72 fps on Quest 3S. |
| J8 | **Agent routing.** The voice agent maps the judge's words to J2 to J7 (tool descriptions and a short system prompt section). Unknown requests get a friendly "I can't do that yet" and a suggestion. | P0 | Person 3 | | 10 phrasings tested; each lands on the right tool. |
| J9 | **First-run guide.** A three-line card at first launch: "Hold out your hand", "Say: open the drone project", "Say: find a camera that can follow me". Dismissed by a poke. | P0 | Person 1 | Demo panel styles | A new user reaches step 6 without help. |
| J10 | **No-voice fallback.** Every step also has a poke button (on the cards and the project window), for judges whose mic or voice service fails. | P1 | Person 1 | | The whole path works muted. |
| J11 | **Cost guard.** Voice is gated (decided); search and LLM calls per judge capped per hour in the cloud proxy. | P1 | Person 3 | | A loop can't spend more than the cap. |
| J12 | **Stage 1 pass.** Full pass hands-only, seated, cold start under the guideline, pause/resume clean, before every Competition-channel upload. | P0 | Person 1 | | Checklist signed off on the APK that's uploaded. |
| J13 | **Devpost text.** Which parts are live, which are replays, which were scripted for the video. Name the services there (model hub, source control, voice), not in the UI. Update `docs/DISCLOSURE.md`. | P0 | Person 1 | `VIDEO_SCRIPT.md` | Text reviewed by one other person. |

## Order (fits in roughly a day for five people)

1. J7 and J2 first: the drone flying in the judge's room is the core and needs no network.
2. J3, J4, J6 in parallel on the hub side.
3. J5: start the real fine-tune early, because the replay depends on it existing.
4. J8 once the tools exist, then J9 and J12 last, on the build that ships.

## What not to do

- Don't ship the scripted Director in the judge build. `demo_config.json` stays `"enabled": false` in the Competition APK.
- Don't let the app order or buy anything. "Add to parts list" saves to the project only.
- Don't show vendor or model-hub brand names in the UI.
