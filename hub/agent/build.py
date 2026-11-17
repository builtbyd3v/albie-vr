"""Albie builds a whole project with its crew: "Albie, build me a ...".

One request runs end to end, in the XR workspace:
  1. Plan.   One Claude call (ml/setup/llm.py: structured output, so the plan always parses) turns the goal into up to
             MAX_STEPS steps, each owned by one crew type (builder, tester, scribe, reviewer, ...). Albie shows it on a card.
  2. Crew.   The crew types the plan needs hatch in Unity (crew.spawn), so the user sees who is working.
  3. Build.  The steps run in order, each as that crew member's own Claude runner turn (runner.run_turn, the same as
             assign_task) in one workspace for this build, so each step sees the last one's files. Every step that
             changed files is committed, with the crew member's name on it.
  4. Ship.   When the build is ready, Albie's card has an "Open pull request" button. Nothing leaves the hub until the
             user taps it or says yes (build_ship with confirm): then the branch is pushed and a pull request opened on
             the demo GitHub account (code_browser.open_pr).

Where the project lives:
  BUILD_REPO=owner/name  the demo account's repo. The build clones it, works on branch albie/build-<slug>-<id>, and puts
                         the project under builds/<slug>/. Needs GITHUB_TOKEN with write access (hub env only, never the APK).
  (unset)                a new local git repo; the project is kept on the hub and can't be shipped.
  BUILD_DIR              where workspaces go (default hub/.data/builds). BUILD_REMOTE overrides the clone URL (tests).

One build runs at a time. State is in memory (like orchestration.py): a hub restart forgets builds, not their files.
"""
from __future__ import annotations

import asyncio
import base64
import logging
import os
import re
import subprocess
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from . import events as ev
from . import tools
from .bus import Emitter, bus
from .config import HUB_DIR, ProjectConfig
from .crew import TYPES, crew
from .turn import TurnControl

log = logging.getLogger(__name__)

LEAD = "bit"  # Albie's pet id
MAX_STEPS = 5
PLAN_AGENTS = ("builder", "tester", "scribe", "reviewer", "fixer", "sentinel")
GIT_NAME, GIT_EMAIL = "Albie", "albie-bot@users.noreply.github.com"
DONE_STATES = ("ready", "shipped", "failed", "cancelled")

PLAN_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "title": {"type": "string", "description": "Short project name, 2-5 words"},
        "summary": {"type": "string", "description": "One sentence: what will be built"},
        "steps": {"type": "array", "items": {
            "type": "object",
            "properties": {
                "agent": {"type": "string", "enum": list(PLAN_AGENTS)},
                "task": {"type": "string", "description": "Self-contained instruction for this crew member"},
            },
            "required": ["agent", "task"], "additionalProperties": False}},
    },
    "required": ["title", "summary", "steps"], "additionalProperties": False,
}

PLAN_SYSTEM = (
    "You plan small software projects for Albie's crew of coding agents, who work in a shared folder in an XR "
    "workspace while the user watches. Each step is done by one crew member, in order, and each sees the files the "
    "earlier steps wrote. Crew: builder (writes the code), fixer (fixes a bug a test found), tester (writes and runs "
    "tests, never production code), scribe (writes the README and comments), reviewer (reads and reports, never "
    "changes files), sentinel (security review, never changes files). "
    f"Plan 3 to {MAX_STEPS} steps: one or two builder steps, then a tester step, a scribe step, and end with a "
    "reviewer step. The whole project must be finishable in about ten minutes: one language, standard library first, "
    "no external services, accounts or API keys, and runnable from a terminal. Each task is self-contained: name the "
    "files to create and what they do, and how to run the result. Keep the title short."
)


def now() -> str:
    return datetime.now(timezone.utc).isoformat()


def build_dir() -> Path:
    return Path(os.getenv("BUILD_DIR") or HUB_DIR / ".data" / "builds")


def build_repo() -> str | None:
    repo = (os.getenv("BUILD_REPO") or "").strip()
    return repo if re.fullmatch(r"[\w.-]+/[\w.-]+", repo) else None


def slug(text: str, n: int = 32) -> str:
    """'Snake game in the terminal' -> 'snake-game-in-the-terminal', cut at a word, at most n characters."""
    s = re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")
    if len(s) > n:
        cut = s[: n + 1].rsplit("-", 1)[0] if "-" in s[: n + 1] else ""
        s = cut if len(cut) >= n // 2 else s[:n]
    return s.strip("-") or "project"


@dataclass
class Step:
    agent: str
    task: str
    state: str = "queued"  # queued, running, done, failed, cancelled
    summary: str = ""
    commit: str | None = None
    progress: str = ""  # the member's latest progress line while it works ("Editing tipcalc/cli.py")
    started_at: str | None = None
    finished_at: str | None = None

    def public(self) -> dict[str, Any]:
        return {"agent": TYPES[self.agent].name, "type": self.agent, "task": self.task, "state": self.state,
                "summary": self.summary, "commit": self.commit, "progress": self.progress,
                "started_at": self.started_at, "finished_at": self.finished_at}


@dataclass
class Build:
    id: str
    goal: str
    title: str = ""
    summary: str = ""
    state: str = "planning"  # planning, building, ready, shipping, shipped, failed, cancelled
    repo: str | None = None
    branch: str | None = None
    folder: str = "."  # the project inside the workspace
    steps: list[Step] = field(default_factory=list)
    planned_by: str = ""  # "claude" or "default"
    pr_number: int | None = None
    pr_url: str | None = None
    error: str | None = None
    created_at: str = field(default_factory=now)
    finished_at: str | None = None
    workspace: Path | None = field(default=None, repr=False)
    control: TurnControl | None = field(default=None, repr=False)
    future: asyncio.Task | None = field(default=None, repr=False)
    ship_asked: bool = field(default=False, repr=False)

    @property
    def done(self) -> bool:
        return self.state in DONE_STATES

    def public(self) -> dict[str, Any]:
        return {"id": self.id, "goal": self.goal, "title": self.title, "summary": self.summary, "state": self.state,
                "repo": self.repo, "branch": self.branch, "folder": self.folder, "planned_by": self.planned_by,
                "steps": [s.public() for s in self.steps], "pr_number": self.pr_number, "pr_url": self.pr_url,
                "error": self.error, "created_at": self.created_at, "finished_at": self.finished_at}


# ------------------------------------------------------------------ git (no shell; the token never lands on disk)
def _auth_args(remote: str) -> list[str]:
    """An http header for github.com from GITHUB_TOKEN, passed per command: never in the remote URL or .git/config."""
    token = os.getenv("GITHUB_TOKEN", "")
    if not token or not remote.startswith("https://github.com/"):
        return []
    basic = base64.b64encode(f"x-access-token:{token}".encode()).decode()
    return ["-c", f"http.extraHeader=Authorization: Basic {basic}"]


def _git(cwd: Path, *args: str, auth: str | None = None, timeout: float = 120) -> subprocess.CompletedProcess[str]:
    cmd = ["git", *(_auth_args(auth) if auth else []), *args]
    env = {**os.environ, "GIT_TERMINAL_PROMPT": "0"}
    return subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, timeout=timeout, env=env)


def _check(res: subprocess.CompletedProcess[str], what: str) -> None:
    if res.returncode != 0:
        # git's own message names the problem; it never echoes the header.
        raise RuntimeError(f"{what} failed: {(res.stderr or res.stdout).strip()[:300]}")


def remote_url(repo: str) -> str:
    return os.getenv("BUILD_REMOTE") or f"https://github.com/{repo}.git"


def prepare_workspace(build: Build) -> None:
    """A fresh git workspace: a clone of BUILD_REPO on a new branch, or a new local repo."""
    root = build_dir()
    root.mkdir(parents=True, exist_ok=True)
    ws = root / f"{slug(build.goal, 24)}-{build.id[:6]}"
    build.workspace = ws
    if build.repo:
        url = remote_url(build.repo)
        _check(_git(root, "clone", "--depth", "1", url, str(ws), auth=url, timeout=300), "cloning " + build.repo)
        build.branch = f"albie/build-{slug(build.goal, 24)}-{build.id[:6]}"
        _check(_git(ws, "checkout", "-b", build.branch), "making the branch")
        build.folder = f"builds/{slug(build.goal, 24)}"
        (ws / build.folder).mkdir(parents=True, exist_ok=True)
    else:
        ws.mkdir(parents=True, exist_ok=False)
        _check(_git(ws, "init", "-q", "-b", "main"), "git init")
        build.folder = "."
    _git(ws, "config", "user.name", GIT_NAME)
    _git(ws, "config", "user.email", GIT_EMAIL)


def head(build: Build) -> str | None:
    assert build.workspace is not None
    res = _git(build.workspace, "rev-parse", "HEAD")
    return res.stdout.strip() or None if res.returncode == 0 else None


def commit_step(build: Build, step: Step, before: str | None) -> str | None:
    """Commit what the step left uncommitted. The step's last commit (its own, if the crew member committed, or this
    one), or None if the step changed nothing since `before`, the head when the step started."""
    ws = build.workspace
    assert ws is not None
    _git(ws, "add", "-A")
    if _git(ws, "diff", "--cached", "--quiet").returncode != 0:
        _check(_git(ws, "commit", "-q", "-m", f"{TYPES[step.agent].name}: {step.task[:60]}",
                    "-m", "Built by Albie's crew."), "committing")
    after = head(build)  # None in a repo with no commits yet (git prints "HEAD" there, which is no commit)
    return after[:7] if after and after != before else None


# ------------------------------------------------------------------ planning
def default_plan(goal: str) -> tuple[str, str, list[Step]]:
    title = goal if len(goal) <= 40 else (goal[:41].rsplit(" ", 1)[0] or goal[:40]) + "…"  # cut at a word
    return (title, goal, [
        Step("builder", f"Build this, complete and runnable: {goal}. Create the source files and a way to run it."),
        Step("tester", "Write tests for what the builder made and run them. Report what passed and what failed."),
        Step("scribe", "Write a README: what it is, how to run it, how to run the tests."),
        Step("reviewer", "Review the project for bugs and missing edge cases, ranked by severity."),
    ])


def parse_plan(data: dict[str, Any], goal: str) -> tuple[str, str, list[Step]]:
    """The model's plan, kept to known crew types and MAX_STEPS, with a reviewer last; the default if it's empty."""
    steps = [Step(str(s.get("agent")), str(s.get("task", "")).strip()) for s in data.get("steps") or []]
    steps = [s for s in steps if s.agent in PLAN_AGENTS and s.task]
    if not any(s.agent == "builder" for s in steps):
        return default_plan(goal)
    review = next((s for s in reversed(steps) if s.agent == "reviewer"), None)
    work = [s for s in steps if s is not review][: MAX_STEPS - (1 if review else 0)]
    steps = work + ([review] if review else [])
    title = str(data.get("title") or goal).strip()[:60]
    return title, str(data.get("summary") or goal).strip()[:300], steps


async def make_plan(goal: str) -> tuple[str, str, list[Step], str]:
    if os.getenv("FAKE_AGENT", "0") == "1":
        return (*default_plan(goal), "default")
    try:
        from ml.setup.llm import LLMError, complete_json
    except ImportError:
        return (*default_plan(goal), "default")
    try:
        result = await asyncio.to_thread(
            complete_json, PLAN_SYSTEM, [{"role": "user", "content": f"Build this: {goal}"}], PLAN_SCHEMA,
            timeout=120.0, effort="medium")
    except LLMError as exc:
        log.warning("build plan fell back to the default: %s", exc)
        return (*default_plan(goal), "default")
    return (*parse_plan(result.data, goal), "claude")


def step_prompt(build: Build, i: int) -> str:
    step = build.steps[i]
    where = "the repository root" if build.folder == "." else f"the folder {build.folder}/ (create it if missing)"
    earlier = [f"- {TYPES[s.agent].name}: {s.summary}" for s in build.steps[:i] if s.summary]
    return "\n".join([
        f"Albie's crew is building a project for the user, step {i + 1} of {len(build.steps)}.",
        f"Project: {build.goal}",
        f"Plan: {build.summary}",
        f"Your task: {step.task}",
        f"Work only in {where}. Do the whole of your task now, not a stub, and don't ask questions: make sensible "
        "choices and say what you chose. Don't commit (each step is committed for you), don't push, don't switch "
        "branches, and don't touch anything outside that folder.",
        *(["What the crew did before you:", *earlier] if earlier else []),
        "When you're done, reply in one or two plain sentences: what you did and the files you touched.",
    ])


# ------------------------------------------------------------------ the run
async def say(text: str, card: ev.Card | None = None) -> None:
    await bus.broadcast(ev.pet_say(LEAD, text, card))


def _runner():
    from .runner import get_runner  # late: runner imports the crew, which imports tools

    return get_runner()


class Builds:
    def __init__(self) -> None:
        self.builds: dict[str, Build] = {}
        self.latest: str | None = None

    def get(self, build_id: str | None) -> Build | None:
        return self.builds.get(build_id or self.latest or "")

    def start(self, goal: str) -> Build:
        goal = " ".join(goal.split())
        if not goal:
            raise ValueError("say what to build")
        if len(goal) > 2000:
            raise ValueError("keep the goal under 2000 characters")
        running = next((b for b in self.builds.values() if not b.done), None)
        if running is not None:
            raise RuntimeError(f"the crew is still building {running.title or running.goal!r}; cancel it or wait")
        from . import ws  # late: ws imports the runner; the card buttons must work before the first tap

        ws.card_handlers.setdefault("build", on_card)
        build = Build(uuid.uuid4().hex, goal, repo=build_repo(), control=TurnControl())
        self.builds[build.id] = build
        self.latest = build.id
        build.future = asyncio.create_task(self._run(build), name=f"build-{build.id[:6]}")
        return build

    async def _run(self, build: Build) -> None:
        try:
            await say(f"Planning: {build.goal}")
            await asyncio.to_thread(prepare_workspace, build)
            build.title, build.summary, build.steps, build.planned_by = await make_plan(build.goal)
            await say(f"Here's the plan for {build.title}.", ev.Card(
                title=f"Plan: {build.title}",
                bullets=[f"{TYPES[s.agent].name}: {s.task[:90]}" for s in build.steps]))
            build.state = "building"
            for agent in dict.fromkeys(s.agent for s in build.steps):  # hatch who the plan needs, in plan order
                hatched = await crew.spawn(agent)
                if "error" in hatched:
                    log.info("build %s: %s didn't hatch (%s); working without the character", build.id[:6], agent,
                             hatched["error"])
            project = ProjectConfig(id=f"build-{build.id[:6]}", path=build.workspace, default_mode="code",
                                    edit_in_place=True)
            for i, step in enumerate(build.steps):
                if self._cancelled(build):
                    break
                await self._step(build, project, i)
            if self._cancelled(build):
                build.state = "cancelled"
                await say(f"Stopped building {build.title}.")
                return
            build.state = "ready"
            await say(f"{build.title} is built.", self._ready_card(build))
        except asyncio.CancelledError:
            build.state = "cancelled"
        except Exception as exc:
            log.exception("build %s failed", build.id[:6])
            build.state, build.error = "failed", str(exc)[:300]
            await say(f"The build stopped: {build.error}")
        finally:
            if build.done and build.finished_at is None:
                build.finished_at = now()

    async def _step(self, build: Build, project: ProjectConfig, i: int) -> None:
        step = build.steps[i]
        agent = TYPES[step.agent]
        step.state, step.started_at = "running", now()
        emitter = Emitter(agent.id, redact=[str(build.workspace)])

        async def emit(event: ev.Event) -> None:  # to the headset as always, and the latest line for the build page
            await emitter(event)
            if event.type in ("agent.task_progress", "agent.partial_answer"):
                step.progress = str(emitter._scrub(event).payload.get("text") or "")[:300]

        mode = "qa" if agent.read_only else "code"
        before = await asyncio.to_thread(head, build)
        try:
            result = await _runner()(project, step_prompt(build, i), mode, emit, pet_id=agent.id,
                                     control=build.control, agent=agent)
        except asyncio.CancelledError:
            step.state, step.finished_at = "cancelled", now()
            raise
        except Exception:
            log.exception("build %s step %d failed", build.id[:6], i + 1)
            step.state, step.summary, step.finished_at = "failed", "Something went wrong in this step.", now()
            return
        if self._cancelled(build):
            step.state, step.finished_at = "cancelled", now()
            return
        step.summary = (result or "").strip()[:600] or "Done."
        if not agent.read_only:
            step.commit = await asyncio.to_thread(commit_step, build, step, before)
        step.state, step.finished_at = "done", now()

    @staticmethod
    def _cancelled(build: Build) -> bool:
        return build.control is not None and build.control.cancelled.is_set()

    @staticmethod
    def _ready_card(build: Build) -> ev.Card:
        bullets = [f"{TYPES[s.agent].name}: {s.summary[:90]}" for s in build.steps if s.state == "done"]
        failed = [TYPES[s.agent].name for s in build.steps if s.state == "failed"]
        if failed:
            bullets.append("Didn't finish: " + ", ".join(failed))
        if not build.repo:
            bullets.append("Saved on the hub (no BUILD_REPO set, so there's nowhere to open a pull request).")
            return ev.Card(title=f"Built: {build.title}", bullets=bullets)
        return ev.Card(title=f"Built: {build.title}", bullets=bullets, actions=[
            ev.CardAction(label="Open pull request", action_id=f"build:ship:{build.id}"),
            ev.CardAction(label="Not now", action_id=f"build:later:{build.id}")])

    async def cancel(self, build: Build) -> bool:
        if build.done:
            return False
        if build.control is not None:
            try:
                await asyncio.wait_for(build.control.cancel(), timeout=3)
            except (Exception, asyncio.TimeoutError):
                log.exception("could not interrupt build %s", build.id[:6])
        if build.future is not None:
            try:
                await asyncio.wait_for(asyncio.shield(build.future), timeout=5)
            except asyncio.TimeoutError:
                build.future.cancel()
                await asyncio.gather(build.future, return_exceptions=True)
        if not build.done:
            build.state, build.finished_at = "cancelled", now()
        return True

    async def ship(self, build: Build) -> dict[str, Any]:
        """Push the branch and open the pull request. Only call after the user said yes."""
        if build.state == "shipped":
            return {"status": "already_shipped", "pr_number": build.pr_number, "pr_url": build.pr_url}
        if build.state != "ready":
            return {"error": f"the build is {build.state}, not ready to ship"}
        if not build.repo or not build.branch or build.workspace is None:
            return {"error": "no BUILD_REPO is set for the hub, so the project stays on the hub"}
        build.state = "shipping"
        try:
            url = remote_url(build.repo)
            res = await asyncio.to_thread(_git, build.workspace, "push", "-u", "origin", build.branch,
                                          auth=url, timeout=300)
            _check(res, "pushing the branch")
            from code_browser import open_pr

            owner, name = build.repo.split("/", 1)
            pr = await open_pr(owner, name, build.branch, f"Albie's crew: {build.title}", pr_body(build))
        except Exception as exc:  # HTTPException from code_browser carries GitHub's reason in .detail
            build.state = "ready"
            reason = str(getattr(exc, "detail", "") or exc)[:300]
            log.warning("build %s didn't ship: %s", build.id[:6], reason)
            return {"error": f"couldn't open the pull request: {reason}"}
        build.state, build.pr_number, build.pr_url = "shipped", pr.get("number"), pr.get("url")
        await say(f"Pull request #{build.pr_number} is open.", ev.Card(
            title=f"PR #{build.pr_number}: {build.title}",
            bullets=[f"{build.branch} into {pr.get('base', 'main')}", str(build.pr_url or "")]))
        return {"status": "shipped", "pr_number": build.pr_number, "pr_url": build.pr_url}


def pr_body(build: Build) -> str:
    lines = [f"**Goal:** {build.goal}", "", build.summary, "", "## What the crew did", ""]
    for s in build.steps:
        commit = f" (`{s.commit}`)" if s.commit else ""
        lines.append(f"- **{TYPES[s.agent].name}** ({s.state}){commit}: {s.summary}")
    lines += ["", f"Project folder: `{build.folder}/`", "",
              "Built in Albie's XR workspace: Albie planned it, the crew built, tested, documented and reviewed it, "
              "and the user said yes to this pull request."]
    return "\n".join(lines)


builds = Builds()


# ------------------------------------------------------------------ Albie's tools (lead only)
_ID = {"type": "string", "description": "build id; omit for the latest build"}
TOOLS: list[dict[str, Any]] = [
    {"name": "build_start",
     "description": "Build a whole small project from scratch with the crew, end to end, when the user asks Albie to "
                    "build or make something ('build me a to-do app', 'make a snake game'). Albie plans it, the crew "
                    "hatches, and builder, tester, scribe and reviewer each do their part in turn, committing as they "
                    "go. Takes a few minutes; returns at once with a build id. Say you're on it.",
     "parameters": {"type": "object", "properties": {
         "goal": {"type": "string", "description": "what to build, in the user's words plus anything they said about it"}},
         "required": ["goal"]}},
    {"name": "build_status",
     "description": "Where a build is: its plan, each crew member's step and result, and the pull request if opened.",
     "parameters": {"type": "object", "properties": {"build_id": _ID}}},
    {"name": "build_ship",
     "description": "Push a finished build's branch and open a pull request on the demo GitHub account. Call it first "
                    "without confirm: nothing is pushed, and you get what would be sent. Say the project name and "
                    "the repo, and ask 'open the pull request?'. Only after a clear yes, call again with confirm true.",
     "parameters": {"type": "object", "properties": {"build_id": _ID, "confirm": {"type": "boolean"}}}},
    {"name": "build_cancel",
     "description": "Stop the build that is running.",
     "parameters": {"type": "object", "properties": {"build_id": _ID}}},
]


async def call_tool(name: str, args: dict[str, Any]) -> dict[str, Any]:
    if name == "build_start":
        try:
            build = builds.start(str(args.get("goal", "")))
        except (ValueError, RuntimeError) as exc:
            return {"error": str(exc)}
        return {"status": "started", "build_id": build.id, "repo": build.repo,
                "say": "Say you'll plan it and the crew will build it; you'll hear when it's ready."}
    build = builds.get(args.get("build_id"))
    if build is None:
        return {"error": "no build yet (builds are kept in memory)"}
    if name == "build_status":
        return build.public()
    if name == "build_cancel":
        if not await builds.cancel(build):
            return {"error": f"the build is already {build.state}"}
        return build.public()
    if name == "build_ship":
        if args.get("confirm") is not True or not build.ship_asked:
            if build.state != "ready":
                return {"error": f"the build is {build.state}, not ready to ship"}
            build.ship_asked = True
            return {"status": "awaiting_confirmation", "title": build.title, "repo": build.repo,
                    "branch": build.branch, "folder": build.folder,
                    "say": f"Ask: open a pull request for {build.title} on {build.repo}? Only after a clear yes, "
                           "call build_ship again with confirm true."}
        return await builds.ship(build)
    return {"error": f"unknown tool {name!r}"}


async def on_card(session: Any, action_id: str) -> None:
    """Albie's card buttons: build:ship:<id> (Open pull request), build:later:<id> (Not now)."""
    _, verb, build_id = (action_id.split(":", 2) + ["", ""])[:3]
    build = builds.get(build_id)
    if build is None or verb != "ship":
        return
    result = await builds.ship(build)
    note = getattr(getattr(session, "voice", None), "note", None)
    if note is not None:
        if "error" in result:
            await note(f"The user tapped Open pull request, but it failed: {result['error']}. Tell them why.")
        else:
            await note(f"The user tapped Open pull request; pull request #{result.get('pr_number')} is open. "
                       "Don't call build_ship.")
    if "error" in result:
        await say(result["error"])


tools.register(TOOLS, call_tool, lead_only=True)
