"""Optional OpenHands executor in a separate Python environment, with Docker-only workspaces."""
from __future__ import annotations

import asyncio
import json
import os
import sys
from pathlib import Path

from . import events as ev
from .build_review import command_env


async def execute(request: dict, control=None, progress=None) -> dict:
    python = os.getenv("OPENHANDS_PYTHON")
    if not python:
        raise RuntimeError("OpenHands needs OPENHANDS_PYTHON; install hub/requirements-openhands.txt in a separate Python 3.12 environment.")
    env = command_env()
    env.update(OPENHANDS_SUPPRESS_BANNER="1", LITELLM_LOCAL_MODEL_COST_MAP="True")
    env.update({k: os.environ[k] for k in ("ANTHROPIC_API_KEY", "OPENHANDS_API_KEY", "OPENHANDS_MODEL", "OPENHANDS_SERVER_IMAGE") if os.getenv(k)})
    worker = Path(__file__).with_name("openhands_worker.py")
    process = await asyncio.create_subprocess_exec(python, str(worker), stdin=asyncio.subprocess.PIPE,
                                                  stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL,
                                                  env=env, limit=128000)

    async def stop():
        if process.returncode is None:
            try:
                process.terminate()
                await asyncio.wait_for(process.wait(), timeout=10)
            except ProcessLookupError:
                pass
            except asyncio.TimeoutError:
                process.kill()
                await process.wait()

    result = None
    try:
        if control is not None:
            control.set_interrupter(stop)
        process.stdin.write((json.dumps(request) + "\n").encode())
        await process.stdin.drain()
        process.stdin.close()
        async with asyncio.timeout(900):
            async for raw in process.stdout:
                try:
                    message = json.loads(raw)
                except ValueError:
                    continue  # SDK logger output is not part of our contract.
                if not isinstance(message, dict):
                    continue
                if message.get("type") == "progress" and progress is not None:
                    await progress(str(message.get("text") or "Working in Docker"))
                elif message.get("type") == "result":
                    result = message
            await process.wait()
        if process.returncode != 0 or result is None:
            raise RuntimeError("OpenHands stopped without completing. Check Docker, the optional environment, and provider configuration.")
        return result
    finally:
        await stop()
        if control is not None:
            control.set_interrupter(None)


async def run_turn(project, text, mode, emit, *, pet_id, control=None, agent=None):
    await emit(ev.task_started(pet_id, project.id, "Working in the Docker sandbox"))

    async def progress(line):
        await emit(ev.task_progress(pet_id, project.id, line[:300]))

    result = await execute({"kind": "agent", "workspace": str(project.path.resolve()), "prompt": text,
                            "read_only": mode == "qa", "role": agent.persona if agent else "Implement the task."},
                           control, progress)
    if control is not None and control.cancelled.is_set():
        return ""
    if result.get("error"):
        raise RuntimeError(result["error"])
    summary = str(result.get("summary") or "")
    await emit(ev.task_complete(pet_id, project.id, summary))
    return summary


async def check(build, argv):
    result = await execute({"kind": "check", "workspace": str(build.workspace.resolve()),
                            "folder": build.folder, "argv": argv}, build.control)
    code = result.get("exit_code")
    text = str(result.get("output") or result.get("error") or "")[-6000:]
    return {"command": argv, "state": "passed" if code == 0 and "Ran 0 tests" not in text else "failed",
            "exit_code": code, "output": text}
