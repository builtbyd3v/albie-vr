# Dev's work: where it stands (Nov 3)

- Dev is away Nov 4 to Nov 16. Ops (services, keys, deploys, APK build) are in `docs/TEAM_HANDOFF.md`. This file covers Dev's code: what's done, what's half-done, what's next.
- Done and on `main`: the on-device port. Albie runs on the Quest with no laptop (voice, 59 tools, CAD and ML in the cloud).
- Half-done: `feat/voice-cost` cuts Grok cost. Not fully tested, **do not merge to `main`**.
- Not tested on the headset yet: hands-only fixes, proactive tips, `feat/voice-cost`.
- Next big item: turn the root `PROJECT_CONTEXT.md` plan into the competition build (deadline Wed Nov 18, 12:00 PM PT).

## 1. On-device port (done, on `main`, PR #1)

How it fits together:

```
Quest app (Unity)
 ├─ AlbieVoice ── voice token ──► Vercel cloud/ ──► xAI (Grok realtime)
 │      └─ Grok WS straight from the headset
 ├─ AlbieTools ── 59 tools, defined in */Resources/AlbieTools/*.json
 │      └─ Claude calls ──► Vercel cloud/api/claude (proxy, key never in the APK)
 └─ ForgeCloud ── CAD, flight sim, ML ──► HF Space (cloud-py/app.py)
Laptop hub/ (Python) = optional. Hub off = everything above.
```

| Piece | File | Notes |
|---|---|---|
| Voice | `unity/Assets/_Forge/Core/Scripts/AlbieVoice.cs` | Grok realtime from the device. Talks to modules through `ForgeNet.LocalPeer`, so modules don't know whether the hub is on. |
| Voice token | `unity/Assets/_Forge/Core/Scripts/AlbieVoiceToken.cs` | Gets a short-lived Grok token from Vercel with the app key. |
| Tools | `unity/Assets/_Forge/Core/Scripts/AlbieTools.cs` | C# tool registry. Each module adds tools in its own `Resources/AlbieTools/<module>.json` (9 files). |
| Cloud client | `unity/Assets/_Forge/Core/Net/ForgeCloud.cs` | CAD and ML calls to the HF Space. URL default in code, PlayerPrefs `Albie.CloudUrl` overrides it. |
| Cloud server | `cloud-py/app.py` | Wraps the existing `hub/cad` and `hub/ml` Python. Every route needs the `x-albie-key` header. |
| Token + Claude proxy | `cloud/api/voice-token.ts`, `cloud/api/claude.ts` | The proxy caps models, tokens and rate. |
| Wake + follow-up | `unity/Assets/_Forge/Pet/Scripts/Voice/WakeListener.cs`, `GatorFollowUp.cs` | Room speech, follow-up window, barge-in. |
| Proactive tips | `unity/Assets/_Forge/Core/Scripts/AlbieTips.cs` | Max 1 tip per 120 s, held while Albie is busy. |

What runs where with no laptop:
- **Real:** voice, CAD (versions, analysis, flight sim), ML training on cloud CPU, the Neural Net Lab (C# on the headset), the neutral messenger with local drafts, crew tasks (one Claude call per task).
- **Partial:** the code panel works on a bundled sample project only.
- **Laptop only:** live screens (screen bridge), VS Code, iMessage. Off by default.

## 2. `feat/voice-cost` (WIP, pushed, not merged)

Why: xAI bills about $0.08 per open minute. Grok sends a message about every 10 s while idle, and the old code counted it as activity, so the session never closed (about $4.80/h of app time).

What the 2 commits do:
1. `4f4daec`: open the Grok session only for a turn. Close it 35 s after Albie's last word (15.5 s follow-up window plus 20 s idle). The last 8 lines of history carry into the next session.
2. `47197ee`: room speech, follow-up and barge-in turns reach Grok only while you face Albie. The look cone is narrowed to 15 degrees.

Status: not fully built or tested. Open questions:
- Is the cold start (new session per turn) too slow?
- Is the 15 degree cone too strict for real use?

The branch is 4 commits behind `main` (the plan docs). Rebase or merge `main` into it before testing. Whatever you change, never count Grok's idle messages as activity.

## 3. Merged but not tested on the headset

From the overnight UI lane (Oct 28), all on `main`:
- Hands-only: fingertip poke on the gator (`GatorHandPoke`), system keyboard for text fields (`Assets/Oculus/OculusProjectConfig`, `requiresSystemKeyboard`).
- Proactive tips: spoken with voice on, caption with voice off.
- Test on a Quest with hands only (no controllers): poke the gator, type in a text field, trigger a tip.

## 4. Next (proposed by Dev, not decided)

The root `PROJECT_CONTEXT.md` (2,325 lines) is the plan for the next Albie. Dev's proposed edits (about 1 h):
1. Section 15 is the plan. Mark sections 4, 8.4 and 14.5 as "roadmap after Nov 18".
2. Cut 15.10 to the RC truck spine, steps 1 to 6. Code mode = one controller file plus a commit. PR #4 (Satya, C-19 controllers) relates to this.
3. Grok stays the voice for the competition. Try Muse only if its tool calling passes a 1-hour test.
4. Remove the Slack branding in the Messages UI (`SlackBlue`, `SlackText`). The rules ban brand names.
5. Shrink the root plan to 1 page. Move the details to `docs/next/`.

Open decisions for the team:
- Division: New vs Adapted (ask the organizers).
- The demo video must be under 3 min. The section 15.6 script is already about 3 min.
- Code mode needs network (the repo lives in the cloud). That conflicts with a "take it away" test.
- Contract id `muse.artifact/1` breaks the no-brand rule.
- Cloud cost: Claude $50/month cap, HF on $16 of credits.

Known bugs to fix early (from `PROJECT_CONTEXT.md` 0.2):
- `cloud-py/app.py`: `DEVICES.popitem()` doesn't close the evicted device's CAD service.
- The app key ships inside the APK, so treat it as public. The rate limit lives in memory on each server instance.

## 5. Parked branches

- `feat/albie-send-files`: Albie attaches ML desk files to a message draft. Slack lacks `files:write`. Fork it after the competition.
- `screens/panel-context`: screen panel shows file context. Laptop-only feature.
