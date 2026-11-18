"""Independent test evidence and the exact committed diff offered for publication."""
from __future__ import annotations

import asyncio
import os
import signal
import sys
from pathlib import Path


def command_env() -> dict[str, str]:
    # Tests execute project code. They do not receive the hub's provider or GitHub credentials.
    return {k: v for k, v in os.environ.items()
            if k in ("PATH", "SYSTEMROOT", "WINDIR", "LANG", "LC_ALL", "TMPDIR", "TEMP", "TMP")}


def discover_checks(root: Path, docker: bool = False) -> list[list[str]]:
    python = "python" if docker else sys.executable
    tests = False
    for directory, folders, files in os.walk(root):
        folders[:] = [f for f in folders if f not in (".git", ".venv", "node_modules", "__pycache__")]
        if any(f.startswith("test") and f.endswith(".py") for f in files):
            tests = True
            break
    if tests:
        pytest_config = (root / "pytest.ini").exists() or ((root / "pyproject.toml").exists() and "[tool.pytest" in (root / "pyproject.toml").read_text())
        if pytest_config:
            return [[python, "-m", "pytest", "-q"]]
        return [[python, "-m", "unittest", "discover", "-s", "tests" if (root / "tests").is_dir() else ".", "-v"]]
    if (root / "package.json").is_file():
        return [["npm", "test"]]
    return []


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
                *argv, cwd=root, env=command_env(), stdout=output, stderr=asyncio.subprocess.STDOUT,
                start_new_session=os.name == "posix")
            if build.control is not None:
                build.control.set_interrupter(stop)
            await asyncio.wait_for(process.wait(), timeout=180)
            code = process.returncode
            state = "passed" if code == 0 else "failed"
    except asyncio.TimeoutError:
        state = "timed_out"
    except OSError:
        state = "unavailable"
    finally:
        await stop()
        if build.control is not None:
            build.control.set_interrupter(None)
    with path.open("rb") as output:
        output.seek(max(0, path.stat().st_size - 6000))
        text = output.read().decode("utf-8", errors="replace")
    text = text.replace(str(build.workspace), ".")
    if "Ran 0 tests" in text:
        state = "failed"
    shown = [Path(argv[0]).name, *argv[1:]]
    return {"command": shown, "state": state, "exit_code": code, "output": text}


async def verify(build) -> list[dict]:
    commands = build.checks or discover_checks(build.workspace / build.folder, build.backend == "openhands")
    if not commands:
        return [{"command": [], "state": "unavailable", "exit_code": None,
                 "output": "No test command found. Supply checks as argument lists or add executable Python tests."}]
    results = []
    for i, argv in enumerate(commands):
        if build.control is not None and build.control.cancelled.is_set():
            break
        if build.backend == "openhands":
            from .openhands_executor import check
            result = await check(build, argv)
        else:
            result = await run_check(build, argv, i)
        results.append(result)
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
