"""Independent test evidence and the exact committed diff offered for publication."""
from __future__ import annotations

import asyncio
import json
import os
import re
import signal
import sys
from pathlib import Path


def empty_test_run(text: str) -> bool:
    return "Ran 0 tests" in text or re.search(r"^(?:#|ℹ) tests 0\s*$", text, re.MULTILINE) is not None


def command_env() -> dict[str, str]:
    # Tests execute project code. They do not receive the hub's provider or GitHub credentials.
    return {k: v for k, v in os.environ.items()
            if k in ("PATH", "HOME", "SYSTEMROOT", "WINDIR", "LANG", "LC_ALL", "TMPDIR", "TEMP", "TMP")}


def discover_checks(root: Path, docker: bool = False) -> list[list[str]]:
    python = "python" if docker else sys.executable
    commands = []
    if (root / "package.json").is_file():
        package = json.loads((root / "package.json").read_text())
        scripts = package.get("scripts") or {}
        # Tests must be a finite run (CI disables watch mode in common test runners).
        if scripts.get("test"):
            commands.append(["npm", "test"])
        if scripts.get("build"):
            commands.append(["npm", "run", "build"])
    tests = False
    for directory, folders, files in os.walk(root):
        folders[:] = [f for f in folders if f not in (".git", ".venv", "node_modules", "__pycache__")]
        if any(f.startswith("test") and f.endswith(".py") for f in files):
            tests = True
            break
    if tests:
        pytest_config = (root / "pytest.ini").exists() or ((root / "pyproject.toml").exists() and "[tool.pytest" in (root / "pyproject.toml").read_text())
        if pytest_config:
            commands.append([python, "-m", "pytest", "-q"])
        else:
            commands.append([python, "-m", "unittest", "discover", "-s", "tests" if (root / "tests").is_dir() else ".", "-v"])
    return commands


async def run_check(build, argv: list[str], index: int) -> dict:
    root = build.workspace / build.folder
    logs = build.workspace.parent / ".logs"  # beside the workspace: BUILD_DIR on the hub, the headset's folder in the cloud
    logs.mkdir(parents=True, exist_ok=True)
    path = logs / f"{build.id}-{index}.log"
    process = None
    state, code = "failed", None

    async def stop():
        if process is not None and process.returncode is None:
            try:
                if os.name == "posix":
                    os.killpg(process.pid, signal.SIGKILL)
                else:
                    process.kill()
            except ProcessLookupError:
                pass
            await process.wait()

    try:
        with path.open("wb") as output:
            process = await asyncio.create_subprocess_exec(
                *argv, cwd=root, env={**command_env(), "CI": "true"}, stdout=output, stderr=asyncio.subprocess.STDOUT,
                start_new_session=os.name == "posix")
            if build.control is not None:
                build.control.set_interrupter(stop)
            await asyncio.wait_for(process.wait(), timeout=180)
            code = process.returncode
            state = "passed" if code == 0 else "failed"
    except asyncio.TimeoutError:
        state = "timed_out"
        with path.open("ab") as output:
            output.write(b"\nCommand exceeded the 180-second check timeout.\n")
    except OSError as exc:
        state = "unavailable"
        with path.open("ab") as output:
            message = (f"Required executable '{argv[0]}' was not found. Install it in the workstation runtime "
                       "(Node.js and npm for web projects)." if isinstance(exc, FileNotFoundError)
                       else f"Could not start '{argv[0]}': {exc.strerror}.")
            output.write(message.encode())
    finally:
        await stop()
        if build.control is not None:
            build.control.set_interrupter(None)
    with path.open("rb") as output:
        output.seek(max(0, path.stat().st_size - 6000))
        text = output.read().decode("utf-8", errors="replace")
    text = text.replace(str(build.workspace), ".")
    if empty_test_run(text):
        state = "failed"
    shown = [Path(argv[0]).name, *argv[1:]]
    return {"command": shown, "state": state, "exit_code": code, "output": text}


async def verify(build) -> list[dict]:
    root = build.workspace / build.folder
    if build.control is not None and build.control.cancelled.is_set():
        return []
    commands = build.checks or discover_checks(root, build.backend == "openhands")
    if not commands:
        return [{"command": [], "state": "unavailable", "exit_code": None,
                 "output": "No check command found. Add package.json test/build scripts, executable Python tests, or explicit checks."}]
    results = []

    async def execute(argv):
        if build.backend == "openhands":
            from .openhands_executor import check
            return await check(build, argv)
        return await run_check(build, argv, len(results))

    # Do not rely on a previous agent turn having installed packages. Clean installs also
    # catch stale or inconsistent lockfiles before a project can be offered for publication.
    if (root / "package.json").is_file():
        locked = any((root / name).is_file() for name in ("package-lock.json", "npm-shrinkwrap.json"))
        setup = await execute(["npm", "ci" if locked else "install", "--no-audit", "--no-fund", "--include=dev",
                               *([] if locked else ["--package-lock=false"])])
        results.append(setup)
        if setup["state"] != "passed":
            return results
    for argv in commands:
        if build.control is not None and build.control.cancelled.is_set():
            break
        result = await execute(argv)
        results.append(result)
        if result["state"] != "passed":
            break
    return results


def review(build) -> dict:
    from .build import _check, _git, head

    if build.workspace is None:
        return {"files": [], "diff": "", "head": None, "truncated": False}
    base = build.base_commit or _git(build.workspace, "hash-object", "-t", "tree", os.devnull).stdout.strip()
    current = head(build)
    if not current:
        return {"files": [], "diff": "", "head": None, "truncated": False}
    result = _git(build.workspace, "diff", "--no-ext-diff", "--no-color", base, current, "--")
    _check(result, "reading the diff")
    names = _git(build.workspace, "diff", "--name-only", "-z", base, current, "--").stdout
    return {"head": current, "base": build.base_commit, "files": [n for n in names.split("\0") if n],
            "diff": result.stdout[:80000], "truncated": len(result.stdout) > 80000,
            "verification": build.verification, "steps": [s.public() for s in build.steps]}
