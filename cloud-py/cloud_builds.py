"""Builds on Albie's cloud ("Albie, build me a ..."): hub/agent/build.py, one manager per headset.

Each headset gets its own `CloudBuilds`: workspaces, saved state and check logs under its own folder
(<CLOUD_DATA_DIR>/devices/<id>/builds), its events on its own bus (build.updated, pet.say, crew.spawn, agent.*: the
headset polls them from GET /events), and the same plan -> approve -> build -> check -> "open the pull request?" steps
as the laptop hub. `mount` adds the routes the headset's Project workflow and Agent office windows call:

    GET  /agent/builds                 this headset's builds, newest first
    POST /agent/builds                 {goal, repo?} -> the new build (it plans first, then waits for approve)
    GET  /agent/builds/{id}            one build
    POST /agent/builds/{id}/approve    {confirm: true}: the user agreed to the plan
    GET  /agent/builds/{id}/review     the committed diff and the independent checks
    POST /agent/builds/{id}/ship       {confirm: true}: push the branch and open the pull request
    POST /agent/builds/{id}/cancel

and `tool` runs Albie's build_* voice tools (POST /tools/{name} in app.py). When builds can't run here the routes
answer 503 {"detail": <one plain sentence>} and the tools {"error": <the same sentence>}: the windows and Albie show
or say it.

Builds are on whenever the Space can run them, with no limit on how many or how often: anyone with the app can
build as much as they like. What the owner sets:

  ANTHROPIC_API_KEY         the Space's Claude key (the planner and the crew); without it the routes say so.
                            BUILD_DEMO=1 runs the fixed scripted sample instead (no key, no cost, nothing published);
                            FAKE_AGENT=1 is the scripted runner the tests use.
  BUILD_REPO                owner/name the pull requests go to (needs the Space's GITHUB_TOKEN); unset: nothing ships
  BUILD_ANY_REPO=1          let a request name another repo the token can reach (default: only BUILD_REPO)
  CLOUD_BUILDS=0            the off switch: every route then says builds are switched off

Optional limits, all off unless set (unset or 0 = no limit; nobody is ever told "limit reached" by default):
  MAX_BUILDS                approved builds the whole machine works on at once
  BUILDS_PER_DAY            builds one headset may start per UTC day
  BUILDS_PER_DAY_TOTAL      builds every headset together may start per UTC day
  BUILD_MINUTES             stop a build still going this long after it started (a plan nobody approved too)

One unfinished build per headset at a time, as on the hub (Builds.start). The crew's Claude turns get the hub runner's
scrubbed environment (agent/runner.py agent_env) with every secret-looking variable of this machine blanked as well,
and the Claude CLI is told to keep its own key out of the commands it runs. Shipping still needs the explicit confirm.
Custom check commands and the OpenHands executor are not offered here.
"""
from __future__ import annotations

import asyncio
import datetime
import json
import logging
import os
import shutil
from pathlib import Path
from typing import Any

from fastapi import FastAPI, HTTPException, Request
from pydantic import BaseModel, ConfigDict, Field

try:  # the Claude Agent SDK and the hub's agent files; without them CAD and ML still run and builds say why not
    from agent import build as hub_build
    from agent import events as agent_events

    IMPORT_ERROR: str | None = None
except Exception as exc:  # noqa: BLE001 - anything missing in the image means: no builds, with the reason
    hub_build = None  # type: ignore[assignment]
    IMPORT_ERROR = f"{type(exc).__name__}: {exc}"

log = logging.getLogger("albie.cloud.builds")

TOOL_NAMES = frozenset({"build_start", "build_approve", "build_review", "build_status", "build_ship", "build_cancel"})
WHERE = "Albie's cloud"

OFF = "Building projects is switched off on Albie's cloud right now."
NO_KEY = "Albie's cloud has no Claude key yet, so the crew can't build there."
NO_RUNNER = "Albie's cloud is missing the crew's coding agent, so it can't build projects right now."
BUSY = "The workshop is busy with another headset's build. Try again in a few minutes."
DEVICE_LIMIT = "This headset has used its {n} builds for today. It can build again tomorrow."
TOTAL_LIMIT = "Albie's cloud has done all its builds for today. It can build again tomorrow."


class Unavailable(RuntimeError):
    """Builds can't run on this machine at all; the message is for the user."""


class OverLimit(RuntimeError):
    """A cap was reached; the message is for the user."""


def _int(name: str, default: int) -> int:
    try:
        return max(0, int(os.environ.get(name, "") or default))
    except ValueError:
        return default


def max_builds() -> int:
    return _int("MAX_BUILDS", 0)  # each of these: 0 = no limit, and that is the default


def per_day() -> int:
    return _int("BUILDS_PER_DAY", 0)


def per_day_total() -> int:
    return _int("BUILDS_PER_DAY_TOTAL", 0)


def build_minutes() -> float:
    return _int("BUILD_MINUTES", 0)


def scripted() -> bool:
    """No Claude call is made: the bundled sample (BUILD_DEMO) or the tests' runner (FAKE_AGENT)."""
    return os.environ.get("BUILD_DEMO") == "1" or os.environ.get("FAKE_AGENT", "0") == "1"


_runner_problem: list[str | None] = []  # checked once: the SDK and its CLI don't change while the server runs


def runner_problem() -> str | None:
    """None when the Claude Agent SDK imports and its Claude Code CLI is on this machine (the SDK's wheel bundles
    one for its platform; a `claude` on PATH works too)."""
    if not _runner_problem:
        problem: str | None = None
        try:
            import claude_agent_sdk
            from agent import runner  # noqa: F401 - the turn the crew runs

            bundled = Path(claude_agent_sdk.__file__).parent / "_bundled" / "claude"
            if not bundled.is_file() and not shutil.which("claude"):
                problem = "no Claude Code CLI"
        except Exception as exc:  # noqa: BLE001
            problem = f"{type(exc).__name__}: {exc}"
        if problem:
            log.warning("builds unavailable: %s", problem)
        _runner_problem.append(problem)
    return _runner_problem[0]


def unavailable() -> str | None:
    """Why builds can't run here, as one sentence for the user; None when they can."""
    if os.environ.get("CLOUD_BUILDS", "1").strip().lower() in ("0", "false", "off", "no"):
        return OFF
    if hub_build is None:
        log.warning("builds unavailable: %s", IMPORT_ERROR)
        return NO_RUNNER
    if scripted():
        return None
    if not os.environ.get("ANTHROPIC_API_KEY"):
        return NO_KEY
    return NO_RUNNER if runner_problem() else None


def harden_runner() -> None:
    """Before a real Claude turn: blank every secret-looking variable of this machine in the agent's environment
    (the hub's list plus the app key and whatever else the Space holds), and have the Claude CLI drop its own
    credentials from the commands it runs. ANTHROPIC_API_KEY itself stays: the CLI needs it to run at all."""
    from agent import runner
    from ml.repo_env import SECRET

    hidden = set(runner.HIDDEN_ENV) | {"ALBIE_APP_KEY"} | {k for k in os.environ if SECRET.search(k)}
    hidden.discard("ANTHROPIC_API_KEY")
    runner.HIDDEN_ENV = tuple(sorted(hidden))
    if not getattr(runner.agent_env, "cloud", False):
        hub_env = runner.agent_env

        def agent_env() -> dict[str, str]:
            return {**hub_env(), "CLAUDE_CODE_SUBPROCESS_ENV_SCRUB": "1"}

        agent_env.cloud = True  # type: ignore[attr-defined]
        runner.agent_env = agent_env


# ---- the machine-wide caps ---------------------------------------------------------------------------------

ACTIVE: dict[str, Any] = {}  # approved builds that are still going, by id, across every headset


def active() -> int:
    for build_id in [i for i, b in ACTIVE.items() if b.done]:
        del ACTIVE[build_id]
    return len(ACTIVE)


def today() -> str:
    return datetime.datetime.now(datetime.timezone.utc).date().isoformat()


class Usage:
    """How many builds were started today, over every headset; kept in a file so a restart doesn't reset it."""

    def __init__(self, path: Path) -> None:
        self.path = path
        self.day, self.count = "", 0
        try:
            saved = json.loads(path.read_text())
            self.day, self.count = str(saved["day"]), int(saved["count"])
        except (OSError, ValueError, KeyError, TypeError):
            pass

    def used(self) -> int:
        return self.count if self.day == today() else 0

    def add(self) -> None:
        self.day, self.count = today(), self.used() + 1
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            self.path.write_text(json.dumps({"day": self.day, "count": self.count}))
        except OSError:
            log.exception("couldn't save the build count")


# ---- one headset's builds ----------------------------------------------------------------------------------

class _Target:
    """What agent/build.py broadcasts on, for a headset: its events go on the device's bus (so GET /events has them)."""

    def __init__(self, bus: Any) -> None:
        self.bus = bus

    async def broadcast(self, event: Any) -> None:
        await self.bus.publish(event.type, event.payload)


_Base: Any = hub_build.Builds if hub_build is not None else object


class CloudBuilds(_Base):
    def __init__(self, root: Path, bus: Any, usage: Usage) -> None:
        self.usage = usage
        self._watchers: set[asyncio.Task] = set()
        if hub_build is not None:
            super().__init__(root, target=_Target(bus), hatch=self._hatch, where=WHERE)
        else:  # the image has no agent runner: every route answers with the reason (unavailable)
            self.builds = {}

    async def _hatch(self, agent: str) -> dict[str, Any]:
        """The headset hatches the crew member itself when it reads crew.spawn (AlbieCrew); nothing to wait for."""
        await self.target.broadcast(agent_events.crew_spawn(agent))
        return {"status": "hatched", "agent": agent}

    def started_today(self) -> int:
        day = today()
        return sum(1 for b in self.builds.values() if str(b.created_at)[:10] == day)

    def start(self, goal: str, *, repo: str | None = None, checks: list[list[str]] | None = None,
              backend: str | None = None):
        reason = unavailable()
        if reason:
            raise Unavailable(reason)
        if checks:
            raise ValueError("Albie's cloud runs the project's own tests; it doesn't take custom check commands.")
        if backend not in (None, "", "claude"):
            raise ValueError("Albie's cloud builds with Claude only.")
        repo = (repo or "").strip() or None
        home = hub_build.build_repo()
        if repo and os.environ.get("BUILD_ANY_REPO") != "1" and repo.lower() != (home or "").lower():
            raise ValueError("Albie's cloud builds new projects in its own repository. Leave the repository blank."
                             if home else "Albie's cloud has no repository connected. Leave the repository blank.")
        self.load()
        if not any(not b.done for b in self.builds.values()):  # with one running, Builds.start says which
            if per_day() and self.started_today() >= per_day():  # the optional limits: off unless set
                raise OverLimit(DEVICE_LIMIT.format(n=per_day()))
            if per_day_total() and self.usage.used() >= per_day_total():
                raise OverLimit(TOTAL_LIMIT)
        if not scripted():
            harden_runner()
        build = super().start(goal, repo=repo, backend="claude")
        self.usage.add()
        if build_minutes():
            watcher = asyncio.create_task(self._watch(build), name=f"build-watch-{build.id[:6]}")
            self._watchers.add(watcher)
            watcher.add_done_callback(self._watchers.discard)
        return build

    async def approve(self, build) -> dict:
        if build.state == "awaiting_plan" and build.id not in ACTIVE:
            if max_builds() and active() >= max_builds():  # optional: off unless MAX_BUILDS is set
                return {"error": BUSY}
            ACTIVE[build.id] = build
        return await super().approve(build)

    async def _watch(self, build) -> None:
        """BUILD_MINUTES set: stop a build that outlives it (a plan nobody approved, or a crew that never finishes)."""
        minutes = build_minutes()
        try:
            await asyncio.wait_for(asyncio.shield(build.future), minutes * 60)
            return
        except asyncio.TimeoutError:
            pass
        except (asyncio.CancelledError, Exception):  # noqa: BLE001 - the build ended some other way
            return
        waiting = build.state == "awaiting_plan"
        log.warning("build %s stopped after %s min (%s)", build.id[:6], minutes, build.state)
        if await self.cancel(build):
            build.error = ("Nobody approved the plan in time, so it was dropped. Start it again when you're ready."
                           if waiting else f"The build took more than {minutes} minutes, so it was stopped.")
            await self.changed(build)

    async def close(self) -> None:
        """The headset's state is being dropped from memory: stop what is still running (its files stay)."""
        for build in list(self.builds.values()):
            if not build.done:
                await self.cancel(build)


class BuildRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    goal: str = Field(min_length=1, max_length=2000)
    repo: str | None = Field(default=None, max_length=200)
    backend: str | None = None


class BuildConfirm(BaseModel):
    model_config = ConfigDict(extra="forbid")
    confirm: bool = False


def _ready() -> None:
    reason = unavailable()
    if reason:
        raise HTTPException(503, reason)


def mount(app: FastAPI, manager: CloudBuilds) -> None:
    """The headset's /agent/builds routes (the hub's, in agent/router.py, for this headset's manager)."""

    def find(build_id: str):
        _ready()
        build = manager.get(build_id)
        if build is None:
            raise HTTPException(404, "Build not found.")
        return build

    @app.get("/agent/builds")
    async def list_builds() -> list[dict]:
        _ready()
        manager.load()
        return [b.public() for b in reversed(list(manager.builds.values()))]

    @app.post("/agent/builds", status_code=202)
    async def start_build(request: BuildRequest) -> dict:
        try:
            return manager.start(request.goal, repo=request.repo, backend=request.backend).public()
        except Unavailable as exc:
            raise HTTPException(503, str(exc)) from exc
        except OverLimit as exc:
            raise HTTPException(429, str(exc)) from exc
        except ValueError as exc:
            raise HTTPException(422, str(exc)) from exc
        except RuntimeError as exc:
            raise HTTPException(409, str(exc)) from exc

    @app.get("/agent/builds/{build_id}")
    async def build_status(build_id: str) -> dict:
        return find(build_id).public()

    @app.post("/agent/builds/{build_id}/approve")
    async def approve_build(build_id: str, body: BuildConfirm) -> dict:
        build = find(build_id)
        if not body.confirm:
            raise HTTPException(409, "Approve the implementation plan first.")
        result = await manager.approve(build)
        if "error" in result:
            raise HTTPException(429 if result["error"] == BUSY else 409, result["error"])
        return result

    @app.get("/agent/builds/{build_id}/review")
    async def review_build(build_id: str) -> dict:
        from agent.build_review import review

        return await asyncio.to_thread(review, find(build_id))

    @app.post("/agent/builds/{build_id}/ship")
    async def ship_build(build_id: str, body: BuildConfirm, request: Request) -> dict:
        # Publication is the headset's own action, after the user said yes: never a browser page's.
        if request.headers.get("origin") or not body.confirm:
            raise HTTPException(403, "Confirm publication on the device or through Albie.")
        result = await manager.ship(find(build_id))
        if "error" in result:
            raise HTTPException(409, result["error"])
        return result

    @app.post("/agent/builds/{build_id}/cancel")
    async def cancel_build(build_id: str) -> dict:
        build = find(build_id)
        if not await manager.cancel(build):
            raise HTTPException(409, f"The build is already {build.state}.")
        return build.public()


async def tool(name: str, args: dict[str, Any], manager: CloudBuilds) -> dict[str, Any]:
    """One of Albie's build_* tools for this headset (agent/build.py call_tool, on its manager)."""
    reason = unavailable()
    if reason:
        return {"error": reason}
    result = await hub_build.call_tool(name, args, manager)
    if name == "build_approve" and result.get("status") == "approved":
        result["say"] = "Say the crew is starting; the user hears each step and when it's built."
    return result
