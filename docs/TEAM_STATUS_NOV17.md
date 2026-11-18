# Team status: Nov 17 evening (Dev)

- Main has the release fixes (#109 load hang + far-desk guard, #111 voice openers, #112 brand-free strings, README, disclosure).
- Four fix PRs are open from the live Quest test: #114 gestures, #115 Agent office, #116 voice, #117 keyboard. Each compiles; a combined build launched on the Quest.
- Each fix still needs one hands-on check on the headset (listed below) before or after merge.
- The repo is private and old commits hold the paid gator pack: decide before making anything public (see "Decisions").
- Build the Competition APK with `QuestBuild.Release` on a machine that has the two git-ignored files (see "Build").

## Open PRs and the headset check each needs

| PR | What | Check on the headset |
|---|---|---|
| #110 | Desk follows the room you walk into | Start outside the scanned room, walk in, sit: the table button appears |
| #114 | Gestures only from a raised, steady hand | Palm-up still calls Albie when raised; typing hands fire nothing |
| #115 | Agent office: gator scaled once | Office shows floor, walls, desks and small crew gators |
| #116 | Voice: mic retry, re-ask, focus loss, token backoff | Talk over a reply: no "microphone failed"; open the system menu mid-turn: no reply |
| #117 | Keyboard: app launches as Activity | Pinch a text field: the system keyboard opens and typing lands. Do NOT click "Fix All" in Meta's Project Setup Tool (it reverts this) |

## Build

1. Close the Unity editor. Have `unity/Assets/_Forge/Core/Resources/AlbieAppKey.txt` (real 32-character key; a placeholder makes voice and cloud return 401) and the gator model (`Pet/Models/Gator_Model.fbx` + `Pet/ThirdParty/Quirky Series`).
2. Release key: get `albie-release.keystore` and its password from Dev (password manager), put the file at `~/.albie/`.
3. Run the batch command in `docs/RELEASE.md`. The log line must say `release key (albie)`.
4. Raise the version code by 1 for every upload. Upload to the "Competition" release channel.

## Test tooling gotchas

- Quit Meta VR Developer Hub during tests: it wipes adb port forwards (kills the debug bridge and the demo hub link).
- Any Unity launch (even a batch compile) restarts adb and cuts a running headset test.
- `bash tools/quest-record.sh` runs the 48-case sweep on a dev build. Last run (Nov 17, 20:00): desk, first run, lifecycle, messages pass. Expected fails: identity on a dev build (`com.albie.devbuild`), cloud-offline cases while the cloud is online.
- Slalom fails with "boundary too small" in a seated/stationary boundary, and "Make me a slalom" is one of the Welcome card's three tries. Judges sit, so consider swapping that try or adding the hoops fallback.

## Still open (minor, from the live test)

- Windows can land in the desk or near the ceiling; Agents window text is clipped.
- The "…" glyph is missing from the Inter font (89 warnings per run).
- Welcome card covers the heart's labels; heart labels hard to read over a laptop.
- Desk presence flips Seated/Near/Away while sitting still; "Welcome back" can flash mid-session.
- Project workflow is a dead end without the laptop hub.
- Frame dips to 43-51 fps during keyboard and focus changes.

## Decisions (team)

- Public repo: the Devpost form decides. If a public repo is required, publish a copy without the gator-pack commits (filtered history or a snapshot) rather than flipping this repo public.
- Albie Demo (`com.albie.demo`, PR #107) is the scripted video build; it must never ship as the Competition APK (demo mode on).
