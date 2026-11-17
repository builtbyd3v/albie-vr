"""NDJSON bridge; run only with OPENHANDS_PYTHON. Never falls back to executing on the host."""
from __future__ import annotations

import json
import os
import platform
import shlex
import signal
import secrets
import sys
from pathlib import Path


def send(**message):
    print(json.dumps(message), flush=True)


def interrupted(*_):
    # Unwind DockerWorkspace's context manager, including on cancellation by the hub.
    raise KeyboardInterrupt


def main():
    from pydantic import SecretStr
    from openhands.sdk import LLM, Conversation
    from openhands.tools.preset.default import get_default_agent
    from openhands.workspace import DockerWorkspace

    signal.signal(signal.SIGTERM, interrupted)
    request = json.loads(sys.stdin.readline())
    root = Path(request["workspace"]).resolve(strict=True)
    if not root.is_dir() or ":" in str(root):
        raise ValueError("Invalid workspace")
    readonly = bool(request.get("read_only"))
    mount = f"{root}:/workspace" + (":ro" if readonly else "")
    architecture = "linux/arm64" if platform.machine().lower() in ("arm64", "aarch64") else "linux/amd64"
    os.environ["OH_SESSION_API_KEYS_0"] = secrets.token_urlsafe(32)
    with DockerWorkspace(server_image=os.getenv("OPENHANDS_SERVER_IMAGE", "ghcr.io/openhands/agent-server:latest-python"),
                         platform=architecture, volumes=[mount], working_dir="/workspace",
                         forward_env=["OH_SESSION_API_KEYS_0"]) as workspace:
        if request["kind"] == "check":
            folder = Path(request.get("folder", "."))
            if folder.is_absolute() or ".." in folder.parts:
                raise ValueError("Invalid check folder")
            result = workspace.execute_command("cd " + shlex.quote(str(Path("/workspace") / folder)) + " && " + shlex.join(request["argv"]))
            send(type="result", exit_code=result.exit_code, output=(result.stdout or "")[-6000:])
            return
        key = os.getenv("OPENHANDS_API_KEY") or os.getenv("ANTHROPIC_API_KEY")
        model = os.getenv("OPENHANDS_MODEL")
        if not key or not model:
            raise ValueError("Configure OPENHANDS_MODEL and OPENHANDS_API_KEY (or ANTHROPIC_API_KEY)")
        llm = LLM(model=model, api_key=SecretStr(key))
        agent = get_default_agent(llm=llm, cli_mode=True)
        summaries = []

        def callback(event):
            kind = type(event).__name__
            # Keep raw actions, tool arguments and reasoning on the executor; emit only stage labels.
            send(type="progress", text=f"Docker: {kind}")
            data = event.model_dump(mode="json")
            if data.get("source") == "agent" and kind == "MessageEvent":
                message = data.get("llm_message") or {}
                for item in message.get("content") or []:
                    if isinstance(item, dict) and item.get("type") == "text":
                        summaries.append(str(item.get("text") or ""))

        conversation = Conversation(agent=agent, workspace=workspace, callbacks=[callback], max_iteration_per_run=40, visualizer=None)
        try:
            conversation.send_message("Work only under /workspace. Never publish, push, or access credentials. "
                                      + request.get("role", "") + "\n" + request["prompt"])
            conversation.run()
            status = str(conversation.state.execution_status).lower()
            if "finished" not in status:
                raise RuntimeError("The Docker agent did not finish its task")
            send(type="result", summary="\n".join(summaries)[-12000:])
        finally:
            conversation.close()


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        sys.exit(130)
    except Exception:
        send(type="result", error="OpenHands could not complete. Check its dependencies, Docker daemon, image and model configuration.")
        sys.exit(1)
