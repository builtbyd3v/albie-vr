# Team handoff: Dev away Nov 4 to Nov 16

- Dev is offline Nov 4 to Nov 9, then out of state until Nov 14, and back in person Mon Nov 16. The Meta VR Start deadline is Wed Nov 18, 12:00 PM PT.
- Hudson (hudmarr) is the point person. Everyone with write access can merge and run the deploy workflows. Only Dev (repo owner) can change repo secrets.
- Both clouds stay on Dev's accounts. You change them through git: merge to `main` and GitHub Actions redeploys. No account access needed.
- `main` is the on-device Albie (no laptop). `feat/voice-cost` is untested WIP. Do not merge it.
- Building an APK needs 2 things that are not in git: the app key and Albie files (section 4).

## 1. What runs where

| Service | What it does | Where | Owner / cost | You change it with |
|---|---|---|---|---|
| Vercel `albie-cloud` (`cloud/`) | Mints Grok voice tokens (`/api/voice-token`), proxies Claude (`/api/claude`) | https://albie-cloud.vercel.app | Dev, Hobby plan, $0 | Workflow "Deploy Vercel cloud" |
| HF Space `builtbyd3v/albie-cloud` (`cloud-py/`) | CAD (build123d), flight sim, ML training | https://builtbyd3v-albie-cloud.hf.space | Dev, $16 HF credits, $0.03/h only while awake, sleeps after 1 h | Workflow "Deploy HF Space" |
| xAI (Grok) key | Voice | in Vercel env `XAI_API_KEY` | Hudson. He extends its expiry in the xAI console. | Hudson only |
| Anthropic key | Code panel, crew, ML "improve" | in Vercel env `ANTHROPIC_API_KEY` | capped at $50/month | Workflow with "sync keys" |
| App key | Headset proves it is Albie to both clouds | Vercel env + HF secret `ALBIE_APP_KEY`; local file `unity/Assets/_Forge/Core/Resources/AlbieAppKey.txt` (git-ignored) | shared once by Dev | Don't change it: every installed APK carries it |
| GitHub `builtbyd3v/albie-vr` | Code, PRs, Actions | private | Dev (owner), team (write) | PRs |

The headset reads both URLs from code defaults: `unity/Assets/_Forge/Core/Net/ForgeCloud.cs` and `unity/Assets/_Forge/Core/Scripts/AlbieVoiceToken.cs`. Keep them as they are, or every APK must be rebuilt.

## 2. Change and redeploy a cloud

1. Make the change on a branch and open a PR.
2. Merge it to `main`.
3. A push to `main` under `cloud/` runs **Deploy Vercel cloud**. A push under `cloud-py/`, `hub/cad/`, `hub/ml/`, `forge-sdk/forge/` or `ml-examples/text-classification/` runs **Deploy HF Space**.
4. To redeploy without a code change, open GitHub > Actions, pick the workflow and click **Run workflow**.
5. Check the result. Vercel is live in about 30 s. The HF Space rebuilds for about 10 min; watch it at https://huggingface.co/spaces/builtbyd3v/albie-cloud.

Both workflows run a script you can also run from a laptop: `cloud/deploy.py` (Vercel REST API, `VERCEL_TOKEN` scoped to the agent-cloud project only) and `cloud-py/deploy.sh` (HF CLI).

Health check (needs the app key; want `200`):

```sh
read -s K && curl -s -o /dev/null -w '%{http_code}\n' -H "x-albie-key: $K" https://builtbyd3v-albie-cloud.hf.space/health
```

A `401` in a browser is normal: every route, `/health` included, needs the `x-albie-key` header.

## 3. Change a key

- **Grok key expired** (voice says it can't reach the voice service, Vercel logs show a 502 from xAI "Incorrect API key"): Hudson extends it in the xAI console. The value stays the same, so nothing else changes. A new value: Dev sets the repo secret `XAI_API_KEY` (message Dev), then anyone runs **Deploy Vercel cloud** with **sync keys** ticked.
- **Claude key:** Dev sets the repo secret `ANTHROPIC_API_KEY`, then anyone runs **Deploy Vercel cloud** with **sync keys** ticked.
- Repo secrets: GitHub > Settings > Secrets and variables > Actions (repo owner only). Nobody can read a secret back.
- A secret you never set is skipped, and Vercel keeps its current value.
- The HF Space's `CLAUDE_PROXY_URL` lives in the Space settings. The deploy doesn't touch it.

## 4. Build and install the APK

Before the first build:
1. Get the app key from Dev's one-time link. Save it as `unity/Assets/_Forge/Core/Resources/AlbieAppKey.txt` (one line, no spaces).
2. Get Albie files (paid Asset Store pack, never in git): follow `docs/SETUP.md` section 9 step 0 **before** Unity opens the project. Without them, Albie looks gray or is missing.
3. Gray Albie after an import means Unity reset the `.meta`. Fix: `git show 9fd0c93^:unity/Assets/_Forge/Pet/Models/Gator_Model.fbx.meta > unity/Assets/_Forge/Pet/Models/Gator_Model.fbx.meta`.

Build (close the Unity Editor first; a batch build fails while the project is open):

```sh
cd unity && unity build --target Android --execute-method QuestBuild.Build -o Builds/AlbieStandalone.apk .
```

Or in the Editor: File > Build Profiles > Android, scene `Assets/_Forge/Core/Main.unity` only.

Install on the Quest (USB, Developer Mode on, tap **Allow USB debugging**):

```sh
sh quest-run.sh unity/Builds/AlbieStandalone.apk
```

It installs the APK, grants mic and room permissions, launches Albie, and records headset logs to `unity/Logs/quest-*.log` until Ctrl+C. Once it launches you can unplug the cable.

Shipping to judges: upload the APK to the Meta VR Developer Dashboard, "Competition" release channel (open item below).

## 5. Branches

- `main`: on-device Albie (PR #1 port) plus the next-Albie plan in the root `PROJECT_CONTEXT.md`. Start new work here.
- `feat/voice-cost`: Grok session opens only for a turn and closes 35 s after Albie's last word; room speech needs you facing Albie. **Not fully built or tested. Do not merge into `main`.** Test it on the headset if you want; tell Dev what you find.
- `feat/albie-send-files`, `screens/panel-context`: parked.

## 6. Traps that cost us a debug round

- Unity compiles only while its window is in front.
- `Forge > Build Main Scene` rewrites `Main.unity`. Don't commit that file. Run `git checkout -- unity/Assets/_Forge/Core/Main.unity` before a pull.
- Grok bills about $0.08 per open minute. A session left open costs about $4.80/h. Close the app when you finish testing.
- The HF Space sleeps after 1 h. The first CAD or ML call after that waits for a cold start (about 1 to 2 min).
- Don't commit the TMP `LiberationSans SDF - Fallback.asset` or `EditorPipelineConfig.json` changes.
- Never commit `AlbieAppKey.txt`, `hub/.env`, or any Albie file.

## 7. Open items (decide as a team)

1. Headset: who keeps the Quest 3S. It is signed in to Dev's Meta account with Developer Mode on. Any Mac can deploy after you tap Allow.
2. Albie licence: check whether each person who builds needs their own Asset Store licence.
3. Meta VR Developer Dashboard: who owns the app and the Competition release channel, and who is the submitting representative.
4. Demo video (under 3 min, public YouTube or Vimeo) and the Devpost form.
