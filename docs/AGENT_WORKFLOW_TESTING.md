# Test Albie's 3D project workflow

This guide covers the no-key scripted demo and the separate live-agent acceptance test.
The current demo has four roles: Builder, Tester, Scribe and Reviewer, coordinated by Albie.
It runs them sequentially. A selectable three-agent crew is future work.

## What the demo proves

Unity submits a task, displays an approval gate, receives crew events, and shows the resulting changes and checks.
The generated files, local Git commits and six Python tests are real. Model reasoning is scripted; every prompt
produces the same tip calculator. Voice and GitHub publication are disabled. Passing this demo does not validate
Claude credentials, arbitrary project generation, a real PR or physical Quest interaction.

## Get the same source

Obtain the working source snapshot containing this guide, `hub/agent/demo_server.py` and
`unity/Assets/_Forge/OS/Scripts/OSWorkflowWindow.cs`. These changes may not yet be on the remote default branch.
Do not share `.env` files, API keys, `.data`, Python environments or Unity's Library folder.

## Start a backend on the tester's computer

Prerequisites: Git, a Python environment compatible with `hub/requirements.txt`, and Unity **6000.6.7f1**.
The repository's hub setup documents Python 3.11. Use an existing working hub environment if available.
First-time dependency installation also includes the full hub's CAD dependencies and can take time.

From the repository root on macOS/Linux:

```sh
python3 -m venv .venv
source .venv/bin/activate
python -m pip install -r hub/requirements.txt
python -m uvicorn agent.demo_server:app --app-dir hub --host 127.0.0.1 --port 8003
```

On Windows, create the environment with `py -3.11 -m venv .venv`, activate it with
`.venv\Scripts\Activate.ps1`, then run the same `python -m pip` and `python -m uvicorn` commands.
Keep the terminal running. No provider key is required.

Open `http://127.0.0.1:8003/health`: expect `ok: true`, `fake_agent: true`, and `stt: "none"`.
The optional web dashboard is `http://127.0.0.1:8003/agent/build`.

## Connect Unity

1. Add the repository's `unity` folder to Unity Hub and open it with 6000.6.7f1. Wait for imports and compilation.
2. Open `Assets/_Forge/Core/Main.unity`.
3. Enable **Forge → Desktop Mode (skip XR)** for a desktop test.
4. Before Play, find the scene's **ForgeNet** component and set its **Hub Url** to
   `http://127.0.0.1:8003`. A saved PlayerPrefs value named `Forge.HubUrl` overrides this Inspector value.
   If this computer previously used another address, set that preference to the demo URL using the Unity CLI
   below, or remove that specific preference before Play.
5. Press Play. Click the Game view. Press **M** to open the menu.
6. Open **Tools → Agents → Project workflow**. Press **Esc** to free the mouse for selecting buttons and typing.

Optional Unity CLI command, run from the repository root with the Unity Editor open and Pipeline connected:

```sh
unity command script eval 'UnityEngine.PlayerPrefs.SetString("Forge.HubUrl", "http://127.0.0.1:8003"); UnityEngine.PlayerPrefs.Save(); return "Demo address saved";' --project-path ./unity --json
```

On the original development Mac, this address is already saved and the demo backend is already running.

## Try a spoken project

Use an updated Unity client and updated voice-enabled hub, or a configured Quest build connected to the updated cloud.
The demo on port 8003 disables speech. For a laptop voice test, run this from the repository root with Grok voice
and the coding provider configured in the hub environment.

```sh
VOICE_PROVIDER=grok .venv/bin/python -m uvicorn agent.standalone:app --app-dir hub --host 127.0.0.1 --port 8014
```

Point the Unity Editor's Forge.HubUrl preference at `http://127.0.0.1:8014` before entering Play.
Open **Home → Tools → Agents → Project workflow → Describe your project**. When Albie asks what the crew should build,
allow microphone access and say “Build a Python tip calculator with bill splitting, tests and a README.”
Check the plan, approve implementation, then select each assigned worker in the right-hand office to inspect progress.
The room crew count should stay the same. **Type instead** opens the optional text form.

## Run the whole example

1. Click **Type instead**.
2. Enter: **Build a Python tip calculator with bill amount, tip percentage, bill splitting, tests and README.**
3. Leave the repository field empty. Click **Create implementation plan**.
4. Expect `awaiting_plan`, four queued roles and the scripted-demo banner. The crew must not begin this build
   until approval. Existing crew characters from previous runs may remain in the room.
5. Click **Approve implementation plan**. Watch Builder → Tester → Scribe → Reviewer change from queued to
   running to done. Watch the assigned workers in the right-hand Agent office. Normal generation takes about
   12 seconds, plus workspace creation and checks; allow up to a minute on a slower machine.
6. Expect `ready · simulated`, with every role done.
7. Click **Changes**. Expect `tipcalc.py`, `test_tipcalc.py` and `README.md`. Use Previous/Next to page through
   the diff. The web dashboard provides a larger diff view.
8. Click **Checks**. Expect the independent unittest command to show `passed`, `Ran 6 tests`, and `OK`.
9. Confirm there is no publication action for this demo. No GitHub PR should be created.
10. Repeat with **Type instead** to watch it again. Changing the prompt still creates the same fixed sample.

Files and workflow history are stored beneath `hub/.data/orchestration-demo/`. Each approved run gets its own
directory and local Git history. Inside a completed run's directory, optionally run:

```sh
python tipcalc.py 20 --percent 15 --people 2
python -m unittest discover -v
git log --oneline
```

Expected calculator output: tip **3.00**, total **23.00**, per person **11.50**.

## Additional checks

| Check | Procedure | Expected result |
|---|---|---|
| Approval | Create a task without approving | It stays at awaiting_plan; no coding steps start |
| Cancel | Create another task; click Stop workflow before approval | State becomes cancelled |
| Reopen | Close and reopen Project workflow after completion | Latest build and results load again |
| Text entry | Type into the project form | Letters do not trigger walking, hatching or whistle actions |
| Disconnection | Stop the demo backend | The panel reports a connection/action problem; no success is invented |

If a test fails, report the OS, Unity version, hub URL, build ID, last state, exact error, reproduction steps and
a screenshot. Include relevant Unity Console errors and redacted server errors. Do not include credentials.

## Sharing one backend with teammates

`127.0.0.1` only works on the same computer as the server. For independent testing, each teammate can run the
backend locally using the steps above.

For a coordinated demo on a trusted local network, the host can instead start the demo with `--host 0.0.0.0`.
Use `http://HOST_LAN_IP:8003` in each client's ForgeNet address and browser; allow the port through the host's
firewall. Do not use `0.0.0.0` as a client URL. The host must stay running. A headset also needs the host's LAN
address rather than localhost. Physical Quest input and networking require a separate device test.

The current hub shares its crew roster, workflow list and event broadcasts across connections. Use one task
and one person controlling approval at a time. It does not yet provide independent private teammate sessions.

Cloud deployment is possible, but has not been verified by this local run. Before sharing a public endpoint:

- Put REST and `/ws` behind HTTPS/WSS with authentication supported by both Unity and the web client.
  The current local hub does not implement the per-user access controls needed for public hosting.
- Use persistent storage for workspaces and `.state` history. Start with one worker/replica because active build
  tasks and approvals live in process memory; interrupted runs are marked failed on restart.
- Add per-user authorization and session/workspace isolation before allowing independent concurrent projects.
- For live coding, run a worker that supports the chosen executor. OpenHands needs Docker-backed execution;
  the direct Claude executor runs commands on its host and is not an OS sandbox.
- Keep provider/GitHub credentials on the server. Verify reconnects, streaming, startup, restart recovery,
  resource limits and publication approval through the actual deployed address.

## Separate live-agent acceptance test

Follow `docs/AGENT_ORCHESTRATION.md` to configure the normal hub with a valid Anthropic key and a rotated GitHub
token in ignored `hub/.env`. Start `main:app`, not `agent.demo_server:app`, on a separate port and point Unity at it.
The demo launcher deliberately disables provider credentials and publishing even when local keys exist.

1. Use an owned test repository with a known passing test command.
2. Request a small change, such as a tip calculator with tests. Enter the repository as `owner/repo`.
3. Review and approve the plan. Verify real agent activity and generated changes.
4. Require all independent checks and Reviewer approval; inspect Changes and Checks yourself.
5. Explicitly approve **Publish branch and open PR**.
6. Open the returned GitHub PR and verify the destination, branch, diff and test evidence. Do not merge as part
   of this acceptance test.

This live-provider and PR test is still pending. The no-key demo cannot substitute for it.

## Recorded local run — 2026-11-17

- Submitted and approved through the Unity workflow button handlers, using the goal above.
- Build ID: `4a1b70a3d5354b248029407a4a7f1169`; final state: `ready`; demo: true.
- All four crew roles completed. Unity reported all four crew members present, with bodies enabled, and showed
  Reviewer busy during its step. Physical pointer/controller interaction was not exercised by this automated run.
- Builder, Tester and Scribe created three local commits. Independent verification passed all six tests.
- Workspace: `hub/.data/orchestration-demo/build-a-python-tip-4a1b70/`.
- Unity screenshots: `unity/Logs/workflow-demo-running.png` and `unity/Logs/workflow-demo-checks.png`.
- No real model request, voice request, cloud deployment or GitHub publication was exercised.
