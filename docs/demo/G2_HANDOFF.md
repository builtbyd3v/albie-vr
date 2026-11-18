# G2 handoff: run the scripted demo on the headset

Built 2026-11-16 on `feat/albie-os`. Demo mode is **on** in this build (`Demo/Resources/demo_config.json` `"enabled": true`). Before any judge build run **Forge > Demo > Toggle Demo Mode** (off).

## Start it
1. **APK:** `unity/Builds/AlbieDemo.apk` (129.5 MB, built 10:47 PM, Succeeded, 0 errors). No Quest was connected, so it is **not installed yet**.
   `ADB="C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"`
   `"$ADB" install -r unity/Builds/AlbieDemo.apk` then `"$ADB" reverse tcp:8000 tcp:8000` (the Android package is `com.UnityTechnologies.com.unity.template.urpblank`).
2. **Hub** (Windows; `run-demo.sh` is Mac only): `cd hub` then `python -m uvicorn main:app --host 0.0.0.0 --port 8000`. It needs `build123d==0.13.0` (installed here tonight; `pip install -r hub/requirements.txt` elsewhere) or it dies at startup in the CAD bring-up. Check `curl localhost:8000/health`. The first run asks Windows Firewall to allow Python on private networks: allow it.
3. **Laptop remote:** `http://10.138.118.1:8000/demo` (this laptop's Wi-Fi address; `ipconfig` if the router changes it). The phone or laptop must be on the same Wi-Fi. Buttons: Back, Next, Reset and a B0 to B7 list. Verified: `/demo` loads on the LAN address and `POST /demo/next` returns ok.
4. In the headset: sit, put on the headset, launch. Albie arrives as normal. Close any OS windows left open (Gestures, etc.) first: they sit where the demo panels go. The first `Next` (or `]` on the desktop rig) starts B0.

## What each beat should look like
| Beat | You | On screen |
|---|---|---|
| B0 | nothing (auto 5 s) | quiet, Albie idle |
| B1 | hold left palm up, then say your line | Albie hops onto the palm, "Hey! What do you feel like building today?" Moves on when you say a sentence. |
| B2 | say a sentence | quad builds on a glowing pad, props spin up, hovers. "She's flying. What should the camera do?" |
| B3 | nothing | Left panel: four queries type, six camera cards, check chips go green or red (about 12 s). Right: model-hub lane, "Best fit" on face-track-nano. |
| B4 | poke **Add to parts list** | verdict card (Tern C2 Mini) lifts out, button turns "Added", the camera flies from the card onto the drone's nose. If you don't poke, it mounts after 45 s. |
| B5 | nothing | pipeline panel: pull (4 s), train (12 s, curves, "20x faster"), then Albie asks to push |
| B5b | poke **Confirm** on the push card, say a sentence | upload bar, commit card (private repo). Cancel sends nothing. |
| B6 | hold a palm up to end it (or auto 150 s) | research and pipeline close, room scan shimmer, drone follows 2 m back, drone-cam window appears (REC, LOCKED 0.9x) |
| B7 | `Next` on the remote | drone returns over your left palm. "Nice flight. Want me to log this run?" |

Keys on the desktop rig: `]` next, `[` back, `\` reset, `` ` `` holds the palm up.

## Fake, placeholder or broken
- **No audio clips yet.** `Resources/DemoAudio/` does not exist: every line shows as a caption with a timed wait (0.06 s per character) and Albie's jaw does not move. Record them once `XAI_API_KEY` is in `hub/.env`: `python hub/scripts/demo_record_lines.py`, listen to all 10, then rebuild the APK.
- All research, pipeline and flight data is scripted from JSON. Nothing is searched, trained or pushed. "Plover V3 Lite" and the other camera names are made up.
- B1 palm landing, the poke targets (Add, Confirm, Cancel) and the drone-cam placement were only tested with the mouse and a synthetic hand. Real hand tracking is untested. If the palm is not found, `Next` skips B1 (it waits 60 s).
- Muting the live agent is best effort (`tts.stop` and `agent.cancel` after each of your sentences): watch for Albie answering over the script, and use the remote if he does.
- Speech advances B1, B2 and B5b through the hub's speech-to-text, so the hub must be running and hearing you. Remote `Next` always works instead.
- Going back or jumping resets everything and replays the earlier cues quickly: it is not a true undo.
- The drone avoids furniture by footprint only. The room scan path (real MRUK room) was tested only on the mock room in the editor.
- Editor checks run tonight: `Play Whole Script` 17/17, `Check Main` 12/12 (Director, four pieces, OS menu before and after). Screenshots in `unity/Logs/demo/` (git-ignored).
