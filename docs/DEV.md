Paste this into Claude Code: **"Set me up for Albie: follow docs/SETUP.md in this repo. My env file is at <path>."**

# Working on Albie

- Get the env file from a teammate. Save it as `hub/.env`. Never commit it.
- First time (macOS): `sh dev-setup.sh <env file>`. It sets up the hub, the ML example, Albie's sandbox and the screen bridge.
- Every day: `sh run-demo.sh` starts the hub and wires up the Quest over USB.
- Voice work is hub-only: edit `hub/agent/`, run `sh run-demo.sh` again. No Unity build.
- Full guide, HUMAN steps (permissions, Slack token, Unity, Quest) and checks: `SETUP.md`.

1. Run the hub tests before you push: `cd hub && ALBIE_VSCODE_PORT=9 .venv/bin/python -m pytest -q`.
2. Check the Forge Albie extension: VS Code's status bar shows the Forge Albie item.
3. Never commit a rebuilt `unity/Assets/_Forge/Core/Main.unity`.
4. Work on a branch, open a PR, never push to `main`.
