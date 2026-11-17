"""Albie's crew builds a project end to end (agent/build.py), with a scripted runner: no Claude, no GitHub."""
from __future__ import annotations

import asyncio
import re
import subprocess
import time
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from agent import build as build_mod
from agent import events as ev
from agent.build import MAX_STEPS, parse_plan

FILES = {"builder": "app.py", "tester": "test_app.py", "scribe": "README.md", "fixer": "app.py"}


def git(cwd: Path, *args: str) -> str:
    return subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True, check=True).stdout.strip()


def scripted_runner(delay: float = 0.0, commits_itself: tuple[str, ...] = ()):
    """A crew member that writes one file per step (the reviewer only reads), like a real turn would. Members named
    in commits_itself also commit their own work, as real crew members sometimes do."""
    seen: list[tuple[str, str]] = []

    async def run(project, text, mode, emit, *, pet_id, control=None, agent=None):
        seen.append((pet_id, mode))
        await emit(ev.task_progress(pet_id, "t1", f"Editing {project.path}/out/{FILES.get(pet_id, 'x')}"))
        for _ in range(int(delay / 0.01)):
            if control is not None and control.cancelled.is_set():
                return ""
            await asyncio.sleep(0.01)
        if mode == "code":
            folder = re.search(r"Work only in the folder ([^ ]+)/", text)
            out = project.path / (folder.group(1) if folder else ".") / "out"
            out.mkdir(parents=True, exist_ok=True)
            (out / "__init__.py").touch()
            content = "import unittest\nclass TestApp(unittest.TestCase):\n def test_runs(self): self.assertEqual(2 + 2, 4)\n" if pet_id == "tester" else f"# by {pet_id}\n"
            (out / FILES[pet_id]).write_text(content)
            if pet_id in commits_itself:
                git(project.path, "add", "-A")
                git(project.path, "-c", "user.name=crew", "-c", "user.email=c@c", "commit", "-q", "-m", f"{pet_id} work")
            return f"{pet_id} wrote out/{FILES[pet_id]}."
        return "No bugs found. REVIEW: PASS"

    run.seen = seen  # type: ignore[attr-defined]
    return run


@pytest.fixture()
def builds_env(client: TestClient, tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setenv("BUILD_DIR", str(tmp_path / "builds"))
    monkeypatch.delenv("BUILD_REPO", raising=False)
    monkeypatch.delenv("BUILD_REMOTE", raising=False)
    monkeypatch.delenv("GITHUB_TOKEN", raising=False)
    build_mod.builds.builds.clear()
    build_mod.builds.latest = None
    yield client
    for b in build_mod.builds.builds.values():  # never leave a running build behind for the next test
        if not b.done and b.future is not None:
            client.portal.call(build_mod.builds.cancel, b)
    build_mod.builds.builds.clear()


def wait_for(client: TestClient, build_id: str, states=("ready", "shipped", "failed", "cancelled"), timeout=10) -> dict:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        b = client.get(f"/agent/builds/{build_id}").json()
        if b["state"] == "awaiting_plan":
            client.post(f"/agent/builds/{build_id}/approve", json={"confirm": True})
        if b["state"] in states:
            return b
        time.sleep(0.02)
    raise AssertionError(f"build stuck: {b}")


def tool(client: TestClient, name: str, **args) -> dict:
    return client.portal.call(build_mod.call_tool, name, args)


def test_a_build_plans_runs_each_crew_member_in_turn_and_commits(builds_env, monkeypatch) -> None:
    runner = scripted_runner()
    monkeypatch.setattr(build_mod, "_runner", lambda: runner)
    started = builds_env.post("/agent/builds", json={"goal": "a tiny to-do list app"})
    assert started.status_code == 202
    b = wait_for(builds_env, started.json()["id"])

    assert b["state"] == "ready", b
    assert b["planned_by"] == "default"  # FAKE_AGENT: no Claude call for the plan
    assert [s["type"] for s in b["steps"]] == ["builder", "tester", "scribe", "reviewer"]
    assert all(s["state"] == "done" for s in b["steps"])
    assert [bool(s["commit"]) for s in b["steps"]] == [True, True, True, False]  # the reviewer changes nothing
    assert runner.seen == [("builder", "code"), ("tester", "code"), ("scribe", "code"), ("reviewer", "qa")]
    assert "workspace" not in b and str(build_mod.build_dir()) not in str(b)  # no hub paths to clients
    # The build page's live line and timings: the member's last progress line, with the workspace path scrubbed.
    builder = b["steps"][0]
    assert builder["progress"].startswith("Editing ") and builder["progress"].endswith("out/app.py")
    assert builder["started_at"] <= builder["finished_at"] <= b["steps"][1]["started_at"]

    ws = build_mod.builds.builds[b["id"]].workspace
    log = git(ws, "log", "--format=%an|%s").splitlines()
    assert len(log) == 3 and all(line.startswith("Albie|") for line in log)
    assert log[-1].startswith("Albie|Builder:")
    assert sorted(p.name for p in (ws / "out").iterdir() if p.is_file()) == ["README.md", "__init__.py", "app.py", "test_app.py"]

    # No BUILD_REPO: it stays on the hub, and shipping says so instead of pretending.
    asked = tool(builds_env, "build_ship")
    assert asked["status"] == "awaiting_confirmation"
    assert "error" in tool(builds_env, "build_ship", confirm=True)
    assert builds_env.get(f"/agent/builds/{b['id']}").json()["state"] == "ready"


def test_shipping_needs_a_yes_then_pushes_the_branch_and_opens_a_pull_request(
        builds_env, tmp_path: Path, monkeypatch) -> None:
    remote = tmp_path / "remote.git"
    subprocess.run(["git", "init", "-q", "--bare", "-b", "main", str(remote)], check=True)
    seed = tmp_path / "seed"
    subprocess.run(["git", "clone", "-q", str(remote), str(seed)], check=True, capture_output=True)
    (seed / "README.md").write_text("sandbox\n")
    git(seed, "add", "-A")
    git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "seed")
    git(seed, "push", "-q", "origin", "main")
    monkeypatch.setenv("BUILD_REPO", "demo/sandbox")
    monkeypatch.setenv("BUILD_REMOTE", str(remote))

    opened: list[tuple] = []

    async def fake_open_pr(owner, repo, head, title, body):
        opened.append((owner, repo, head, title, body))
        return {"number": 7, "url": "https://github.com/demo/sandbox/pull/7", "base": "main"}

    import code_browser
    monkeypatch.setattr(code_browser, "open_pr", fake_open_pr)
    monkeypatch.setattr(build_mod, "_runner", lambda: scripted_runner())

    b = wait_for(builds_env, tool(builds_env, "build_start", goal="Snake game in the terminal")["build_id"])
    assert b["state"] == "ready" and b["branch"].startswith("albie/build-snake-game")
    assert b["folder"] == "builds/snake-game-in-the"

    # A confirm on the very first call is not a yes: it must be asked first. Nothing is pushed either way.
    assert tool(builds_env, "build_ship", confirm=True)["status"] == "awaiting_confirmation"
    assert git(remote, "branch", "--list", b["branch"]) == ""
    assert opened == []

    shipped = tool(builds_env, "build_ship", confirm=True)
    assert shipped == {"status": "shipped", "pr_number": 7, "pr_url": "https://github.com/demo/sandbox/pull/7"}
    assert git(remote, "branch", "--list", b["branch"]).strip() == b["branch"]
    files = git(remote, "ls-tree", "-r", "--name-only", b["branch"]).splitlines()
    assert {"README.md", *(b["folder"] + "/out/" + p for p in ("app.py", "test_app.py", "README.md"))} <= set(files)
    owner, repo, head, title, body = opened[0]
    assert (owner, repo, head) == ("demo", "sandbox", b["branch"])
    assert title.startswith("Albie's crew:") and "**Builder**" in body and "**Reviewer**" in body
    assert tool(builds_env, "build_ship", confirm=True)["status"] == "already_shipped"
    assert len(opened) == 1


def test_the_open_pull_request_button_on_albies_card_ships(builds_env, tmp_path: Path, monkeypatch) -> None:
    remote = tmp_path / "remote.git"
    subprocess.run(["git", "init", "-q", "--bare", "-b", "main", str(remote)], check=True)
    seed = tmp_path / "seed"
    subprocess.run(["git", "clone", "-q", str(remote), str(seed)], check=True, capture_output=True)
    (seed / "README.md").write_text("x\n")
    git(seed, "add", "-A")
    git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "seed")
    git(seed, "push", "-q", "origin", "main")
    monkeypatch.setenv("BUILD_REPO", "demo/sandbox")
    monkeypatch.setenv("BUILD_REMOTE", str(remote))

    async def fake_open_pr(owner, repo, head, title, body):
        return {"number": 3, "url": "u", "base": "main"}

    import code_browser
    monkeypatch.setattr(code_browser, "open_pr", fake_open_pr)
    monkeypatch.setattr(build_mod, "_runner", lambda: scripted_runner())
    b = wait_for(builds_env, tool(builds_env, "build_start", goal="calculator")["build_id"])
    card = build_mod.Builds._ready_card(build_mod.builds.builds[b["id"]])
    assert [a.label for a in card.actions] == ["Open pull request", "Not now"]

    class Session:
        voice = None

    builds_env.portal.call(build_mod.on_card, Session(), card.actions[1].action_id)  # Not now: nothing happens
    assert builds_env.get(f"/agent/builds/{b['id']}").json()["state"] == "ready"
    builds_env.portal.call(build_mod.on_card, Session(), card.actions[0].action_id)
    assert builds_env.get(f"/agent/builds/{b['id']}").json()["pr_number"] == 3


def test_a_member_that_commits_its_own_work_still_gets_the_credit(builds_env, monkeypatch) -> None:
    # Seen live: the Builder and Tester committed their own work, and the build said they had no commit.
    monkeypatch.setattr(build_mod, "_runner", lambda: scripted_runner(commits_itself=("builder", "tester")))
    b = wait_for(builds_env, builds_env.post("/agent/builds", json={"goal": "notes app"}).json()["id"])
    ws = build_mod.builds.builds[b["id"]].workspace
    log = git(ws, "log", "--format=%h|%s").splitlines()
    assert [s["commit"] for s in b["steps"][:3]] == [log[2].split("|")[0], log[1].split("|")[0], log[0].split("|")[0]]
    assert log[0].split("|")[1].startswith("Scribe:")  # left uncommitted by the scribe, committed by the build
    assert b["steps"][3]["commit"] is None


def test_a_step_that_changes_nothing_in_a_new_repo_has_no_commit(builds_env, monkeypatch) -> None:
    # Seen on the build page: in a repo with no commits yet, git answers "HEAD", which was shown as a commit.
    async def idle(project, text, mode, emit, *, pet_id, control=None, agent=None):
        return "Nothing to change."

    monkeypatch.setattr(build_mod, "_runner", lambda: idle)
    b = wait_for(builds_env, builds_env.post("/agent/builds", json={"goal": "empty"}).json()["id"])
    assert b["state"] == "failed" and not any(s["commit"] for s in b["steps"])


def test_one_build_at_a_time_and_cancel_stops_it(builds_env, monkeypatch) -> None:
    monkeypatch.setattr(build_mod, "_runner", lambda: scripted_runner(delay=5.0))
    first = builds_env.post("/agent/builds", json={"goal": "slow thing"}).json()
    second = builds_env.post("/agent/builds", json={"goal": "another"})
    assert second.status_code == 409 and "still building" in second.json()["detail"]
    wait_for(builds_env, first["id"], states=("building",))
    stopped = builds_env.post(f"/agent/builds/{first['id']}/cancel").json()
    assert stopped["state"] == "cancelled"
    assert builds_env.post("/agent/builds", json={"goal": "now it can start"}).status_code == 202


def test_plans_are_kept_to_the_crew_with_the_reviewer_last() -> None:
    title, _, steps = parse_plan({"title": "T", "summary": "S", "steps": [
        {"agent": "reviewer", "task": "review"}, {"agent": "builder", "task": "b1"}, {"agent": "designer", "task": "cad"},
        {"agent": "builder", "task": "b2"}, {"agent": "tester", "task": "t"}, {"agent": "scribe", "task": "s"},
        {"agent": "fixer", "task": "f"}, {"agent": "sentinel", "task": "x"}]}, "goal")
    assert title == "T"
    assert len(steps) == MAX_STEPS and steps[-1].agent == "reviewer"
    assert "designer" not in [s.agent for s in steps]  # not a build crew type
    assert [s.task for s in steps[:2]] == ["b1", "b2"]
    # No builder in the plan: the default plan instead of a build that writes nothing.
    assert [s.agent for s in parse_plan({"title": "T", "summary": "S", "steps": [
        {"agent": "tester", "task": "t"}]}, "goal")[2]] == ["builder", "tester", "scribe", "reviewer"]


def test_the_build_page_is_served_and_has_no_ship_control(builds_env) -> None:
    page = builds_env.get("/agent/build")
    assert page.status_code == 200 and "text/html" in page.headers["content-type"]
    html = page.text
    assert "Albie's crew builds" in html and "/agent/builds" in html
    assert "build_ship" not in html and "/ship" not in html  # shipping only from the headset, after a yes
    assert "innerHTML" not in html  # build text is shown with textContent only


def test_bad_goals_are_refused(builds_env) -> None:
    assert builds_env.post("/agent/builds", json={"goal": ""}).status_code == 422
    assert "error" in tool(builds_env, "build_start", goal="   ")
    assert "error" in tool(builds_env, "build_status")  # nothing built yet


def test_a_build_on_its_own_sinks_like_a_cloud_headset(tmp_path: Path, monkeypatch) -> None:
    """The cloud's shape (cloud-py/app.py): no socket bus, no Unity roster. Every event goes to the headset's own log,
    and the crew is hatched by a crew.spawn event in that log."""
    monkeypatch.setenv("FAKE_AGENT", "1")
    monkeypatch.setenv("BUILD_DIR", str(tmp_path / "builds"))
    monkeypatch.delenv("BUILD_REPO", raising=False)
    monkeypatch.setattr(build_mod, "_runner", lambda: scripted_runner())
    log: list = []

    async def publish(event) -> None:
        log.append(event)

    async def hatch(agent_type: str) -> dict:
        await publish(ev.crew_spawn(agent_type))
        return {"status": "hatched"}

    async def run() -> dict:
        mine = build_mod.Builds(publish=publish, hatch=hatch)
        started = await build_mod.call_tool("build_start", {"goal": "a dice roller"}, of=mine)
        b = mine.get(started["build_id"])
        async def finish():
            while b.state == "planning":
                await asyncio.sleep(0.01)
            assert b.state == "awaiting_plan"
            assert b.workspace is None
            refused = await build_mod.call_tool("build_approve", {"confirm": False}, of=mine)
            assert "error" in refused
            approved = await build_mod.call_tool("build_approve", {"confirm": True}, of=mine)
            assert approved["build_id"] == b.id
            await b.future

        await asyncio.wait_for(finish(), timeout=10)
        assert b.verification[0]["state"] == "passed"
        return b.public()

    b = asyncio.run(run())
    assert b["state"] == "ready" and [s["state"] for s in b["steps"]] == ["done"] * 4
    types = [e.type for e in log]
    assert [e.payload["agent_type"] for e in log if e.type == "crew.spawn"] == ["builder", "tester", "scribe", "reviewer"]
    says = [e.payload for e in log if e.type == "pet.say"]
    assert says[0]["text"].startswith("Planning") and says[1]["card"]["title"].startswith("Plan:")
    assert says[-1]["card"]["title"].startswith("Built:")
    assert "build.updated" in types
    assert "agent.task_progress" in types  # the crew members' live lines reach the headset's log
    progress = [e.payload for e in log if e.type == "agent.task_progress"]
    assert {p["pet_id"] for p in progress} == {"builder", "tester", "scribe", "reviewer"}
    assert str(tmp_path) not in str([e.payload for e in log])  # no cloud paths to the headset
