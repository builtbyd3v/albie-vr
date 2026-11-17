# Albie coding workflows

For the office-style crew visualization, open **Home → Tools → Agents → Agent office**.
See [Agent office](AGENT_OFFICE.md) for its controls and scope; it uses this same workflow backend.

Unity remains the XR frontend. Albie's conversation layer calls the hub's workflow tools; the hub owns the plan,
approval, build branch, sequential crew steps, validation and publication. Each crew member receives the previous
member's files and summary. Existing `crew.spawn` and `agent.*` events animate the 3D characters. The additive
`build.updated` event carries the workflow snapshot to the spatial panel.
The crew stays unmuted while a coding workflow is active or its panel is open, even when Code is closed. Reconnecting
clients receive the latest snapshot. Text-field focus suppresses movement, hatch/dismiss and whistle keyboard shortcuts.

## Use it

### Try it locally without keys

Run `uvicorn agent.demo_server:app --app-dir hub --host 127.0.0.1 --port 8003` with the full hub's Python environment.
Open `http://127.0.0.1:8003/agent/build`, or point Unity's hub address to `http://127.0.0.1:8003` and open
**Home menu → Tools → Agents → Project workflow**. Create a plan, then approve it. Builder, Tester, Scribe and Reviewer run a
scripted tip-calculator sample. Files, local Git commits, six unit tests and diff review are real; model reasoning
and custom project generation are simulated. GitHub publication is disabled. The sample is fixed even if the input
goal changes. The demo uses its own `hub/.data/orchestration-demo` folder and process-local configuration; the normal
hub's secrets and `hub/.env` are unchanged. Voice is disabled for this no-key preview.

### Use real agents

1. Start the normal hub and open Unity Main. Open **Home menu → Tools → Agents → Project workflow**.
2. Choose **New task** and describe a bounded project or change. Optionally name an existing GitHub repository as
   `owner/repo`; leaving it blank uses `BUILD_REPO` for a new project, or creates a local project.
3. Inspect the proposed plan and approve it. No workspace is cloned and no coding agent starts before approval.
4. Watch the crew work. A failed agent, incomplete review or failed independent check stops the workflow.
5. Inspect **Changes** and **Checks**. The spatial diff pages show 12 lines at a time and shorten long lines; the
   hub's `/agent/build` page offers the full returned diff, with an explicit marker when the 80,000-character limit is hit.
6. Tap **Publish branch and open PR** only when the result is ready. This pushes its own build branch and creates a PR;
   it does not merge. Publication stops if the checkout, commit, branch or destination changed after review.

Milestones also go through the hub's registered speech bridge when voice is enabled. The speech equivalent uses `build_start`, then `build_approve` after the user's plan approval, `build_status` /
`build_review`, and `build_ship` after a separate publication approval. The existing Grok transport remains optional;
the spatial panel works without speech. `VOICE_PROVIDER=none` disables spoken requests.

## Hub configuration

Keep credentials in the ignored `hub/.env` or the hub process environment. Never put tokens in Unity assets, agent
prompts, remote URLs, or checked-in files. Rotate any token that has been shared in a conversation.

```dotenv
FAKE_AGENT=0
ANTHROPIC_API_KEY=<local-secret>
GITHUB_TOKEN=<local-secret-with-contents-and-pull-request-write-access>
BUILD_REPO=your-account/your-repository
BUILD_EXECUTOR=claude
```

GitHub publication uses the hub's existing code-browser proxy, restricted to repositories owned by the authenticated
account. `GITHUB_USER` is informational: setting a name does not grant repository access. The destination must exist.
Plan approval and publication are separate. Tests never receive GitHub, speech or model provider keys.

`claude` uses the installed Claude Agent SDK in a separate build checkout. This is file separation, **not an OS sandbox**;
its shell commands run on the hub's machine. Use the optional Docker executor for stronger execution isolation.

## Optional OpenHands executor

OpenHands packages have their own Python requirements; install them separately:

```sh
uv venv .venv/openhands --python 3.12
uv pip install --python .venv/openhands/bin/python -r hub/requirements-openhands.txt
```

Then set these on the hub and restart it:

```dotenv
OPENHANDS_PYTHON=/absolute/path/to/project/.venv/openhands/bin/python
OPENHANDS_MODEL=anthropic/claude-sonnet-5-5
OPENHANDS_SERVER_IMAGE=ghcr.io/openhands/agent-server:latest-python
BUILD_EXECUTOR=openhands
```

`OPENHANDS_API_KEY` optionally overrides `ANTHROPIC_API_KEY` for this executor. `OPENHANDS_MODEL` uses OpenHands'
provider/model naming. Pin `OPENHANDS_SERVER_IMAGE` to a version or digest for reproducible deployments.

Docker Desktop must be running. The NDJSON worker uses OpenHands `DockerWorkspace`, mounts only the disposable build
checkout at `/workspace`, uses a read-only mount for the reviewer, and cleans up through its context manager. Agent
server ports bind to loopback and use a new random session key. GitHub credentials and the Docker socket are not
mounted into the container. The worker never falls back to a host workspace. Validation commands use the same Docker
path; dependencies installed into a previous step's container do not persist, so install project dependencies into a
workspace virtual environment or provide checks that install their requirements in the fresh validation container.

The parent hub runs Git operations and opens PRs only after approval. The OpenHands adapter has a 15-minute process
limit; local checks have a 3-minute limit. Stopping a workflow interrupts the active executor. In the rare case of a
forced worker kill during Docker startup, inspect `docker ps` for an orphaned `agent-server-*` container.

## Test and review contract

`POST /agent/builds` accepts `goal`, optional `repo`, `backend` (`claude` or `openhands`), and up to four `checks`, each
an argument list such as `["python", "-m", "unittest", "discover", "-s", "tests", "-v"]`. Checks are not shell strings.
Without explicit checks, Python unittest / configured pytest and npm test are detected. A missing command, nonzero
exit, timeout or zero discovered unittest tests blocks readiness. Reviewer replies must end with `REVIEW: PASS`;
otherwise the build fails with the review result. Tests and code review remain fallible and do not certify arbitrary
projects or headset behavior.

Routes under `/agent/builds/{id}`: `GET` for status, `POST /approve {confirm:true}`, `GET /review`, `POST /cancel`, and
device-only `POST /ship {confirm:true}`. Browser publication is rejected; same-origin plan approval is permitted.
Snapshots survive restart under `BUILD_DIR/.state`. An interrupted run becomes failed and keeps its files, rather than
automatically continuing with stale permissions. Workflows currently stop at failures; starting a repair task is explicit.

## Open-source integration choices

- [OpenHands Software Agent SDK](https://github.com/OpenHands/software-agent-sdk) supplies the optional execution
  runtime, event callbacks and Docker workspace; its [Docker guide](https://docs.openhands.dev/sdk/guides/agent-server/docker-sandbox)
  is the adapter's reference. SDK/tool/workspace dependencies are pinned to 1.62.0.
- [XARP](https://github.com/HAL-UCSB/xarp) was evaluated as an alternative Python XR frontend. Albie already has a
  working Unity/Meta client, so no second Quest frontend is bundled. A future XARP client can consume the hub's REST
  snapshots and `build.updated` events.
- [NVIDIA XR AI](https://github.com/NVIDIA/xr-ai) was evaluated for multimodal transport. Its media hub and CloudXR
  stack are not required by this Unity workflow; they remain optional frontend/transport integrations.
- [OpenHands/OpenHands](https://github.com/OpenHands/OpenHands) is the larger application; the smaller SDK packages
  are used here instead of embedding another complete UI/server product.

No upstream source files are vendored. These are integration choices, not a claim that the four projects form one
pre-integrated product.

## Desktop verification record (2026-11-17)

- 329 agent tests passed, including the milestone speech bridge; the targeted build/executor suite passed 22 checks before that addition.
- Unity 6000.6.7f1 compiled the workflow UI; the task/repository input form and panel layout were inspected in Play mode.
- A 3D Builder hatched with Code closed; opening the workflow enabled its captions. The task form's text focus and
  desktop shortcut guard were checked in the running scene.
- OpenHands 1.62.0 ran a real Python command in Docker and returned exit code 0 and a file in the disposable test mount.
- A real Claude build attempt was rejected by the configured Anthropic credentials. The workflow stopped and did not
  offer publication. A successful live coding run and live GitHub PR remain unverified until credentials and a target
  repository are supplied. The token pasted into the conversation was not installed or used.
- The saved Grok key passed a credential check (HTTP 200); `VOICE_PROVIDER=grok` is enabled locally for spoken requests
  and workflow milestones. Physical speech interaction still needs a user test.
- The current full desktop hub runs on port 8002, with Unity's hub preference set to `http://127.0.0.1:8002`; an existing
  Docker service occupies port 8000. The separate agent-only verification server was stopped.
- Physical Quest interaction, spatial readability and sustained performance were not certified by these desktop checks.
