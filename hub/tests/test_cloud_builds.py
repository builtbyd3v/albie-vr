"""The crew's builds on Albie's cloud (cloud-py/cloud_builds.py): per-headset managers, the caps, and why not.

Scripted runners only: no Claude, no GitHub. The last test drives the real cloud app (cloud-py/app.py) in its own
process, with the import path the container uses.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import textwrap
import time
from pathlib import Path

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

REPO = Path(__file__).resolve().parents[2]
CLOUD = REPO / "cloud-py"
if str(CLOUD) not in sys.path:
    sys.path.append(str(CLOUD))  # cloud_builds only; app.py patches ml.improve, so it runs in a subprocess below

import cloud_builds  # noqa: E402
from agent import build as hub_build  # noqa: E402
from tests.agent.test_build import git, scripted_runner  # noqa: E402

DONE = ("ready", "shipped", "failed", "cancelled")


class Bus:
    """The cloud's device bus, as far as builds use it: publish(type, payload), kept in order."""

    def __init__(self) -> None:
        self.events: list[tuple[str, dict]] = []

    async def publish(self, type_: str, payload: dict) -> None:
        self.events.append((type_, payload))

    def types(self) -> set[str]:
        return {t for t, _ in self.events}


class Headset:
    def __init__(self, data: Path, name: str, usage: cloud_builds.Usage) -> None:
        self.root = data / "devices" / name / "builds"
        self.bus = Bus()
        self.manager = cloud_builds.CloudBuilds(self.root, self.bus, usage)
        app = FastAPI()
        cloud_builds.mount(app, self.manager)

        @app.post("/tools/{name}")
        async def tool(name: str, args: dict) -> dict:
            return await cloud_builds.tool(name, args, self.manager)

        self.client = TestClient(app)
        self.client.__enter__()

    def tool(self, name: str, **args) -> dict:
        return self.client.post(f"/tools/{name}", json=args).json()

    def wait(self, build_id: str, states=DONE, approve: bool = True, timeout: float = 15) -> dict:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            b = self.client.get(f"/agent/builds/{build_id}").json()
            if b["state"] in states:
                return b
            if b["state"] == "awaiting_plan" and approve:
                self.client.post(f"/agent/builds/{build_id}/approve", json={"confirm": True})
            time.sleep(0.02)
        raise AssertionError(f"build stuck: {b}")

    def close(self) -> None:
        self.client.portal.call(self.manager.close)
        self.client.__exit__(None, None, None)


@pytest.fixture()
def cloud(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    """headset(name) -> a Headset on a throwaway cloud data folder, builds on, scripted crew."""
    for name in ("BUILD_REPO", "BUILD_REMOTE", "BUILD_DEMO", "BUILD_DIR", "BUILD_ANY_REPO", "GITHUB_TOKEN",
                 "ANTHROPIC_API_KEY", "MAX_BUILDS", "BUILDS_PER_DAY", "BUILDS_PER_DAY_TOTAL", "BUILD_MINUTES"):
        monkeypatch.delenv(name, raising=False)
    monkeypatch.delenv("CLOUD_BUILDS", raising=False)
    monkeypatch.setenv("FAKE_AGENT", "1")
    monkeypatch.setenv("FAKE_AGENT_DELAY", "0.01")
    monkeypatch.setattr(hub_build, "_runner", lambda: scripted_runner())
    cloud_builds.ACTIVE.clear()
    data = tmp_path / "data"
    usage = cloud_builds.Usage(data / "builds-usage.json")
    made: list[Headset] = []

    def headset(name: str = "headset-a") -> Headset:
        made.append(Headset(data, name, usage))
        return made[-1]

    headset.data = data  # type: ignore[attr-defined]
    yield headset
    for h in made:
        h.close()
    cloud_builds.ACTIVE.clear()


def test_the_owner_can_switch_builds_off(cloud, monkeypatch) -> None:
    monkeypatch.setenv("CLOUD_BUILDS", "0")
    h = cloud()
    listed = h.client.get("/agent/builds")
    assert listed.status_code == 503 and listed.json() == {"detail": cloud_builds.OFF}
    started = h.client.post("/agent/builds", json={"goal": "a snake game"})
    assert started.status_code == 503 and started.json() == {"detail": cloud_builds.OFF}
    assert h.tool("build_start", goal="a snake game") == {"error": cloud_builds.OFF}
    assert h.manager.builds == {} and not h.root.exists()


def test_without_a_claude_key_the_routes_say_so_and_nothing_starts(cloud, monkeypatch) -> None:
    monkeypatch.setenv("FAKE_AGENT", "0")
    h = cloud()
    for answer in (h.client.get("/agent/builds"), h.client.post("/agent/builds", json={"goal": "a snake game"})):
        assert answer.status_code == 503 and answer.json() == {"detail": cloud_builds.NO_KEY}
    assert h.tool("build_status") == {"error": cloud_builds.NO_KEY}
    assert h.manager.builds == {}


def test_a_headset_builds_in_its_own_folder_and_hears_about_it_on_its_own_bus(cloud) -> None:
    a, b = cloud("headset-a"), cloud("headset-b")
    started = a.client.post("/agent/builds", json={"goal": "Snake game in the terminal", "repo": None})
    assert started.status_code == 202
    build_id = started.json()["id"]
    plan = a.wait(build_id, states=("awaiting_plan",), approve=False)
    assert [s["type"] for s in plan["steps"]] == ["builder", "tester", "scribe", "reviewer"]
    assert a.client.post(f"/agent/builds/{build_id}/approve", json={"confirm": False}).status_code == 409

    done = a.wait(build_id)
    assert done["state"] == "ready", done
    assert done["simulated"] is True and done["repo"] is None
    assert all(c["state"] == "passed" for c in done["verification"])
    assert str(cloud.data) not in json.dumps(done)  # no server paths to the headset

    workspace = a.manager.builds[build_id].workspace
    assert workspace.parent == a.root and (workspace / "out" / "app.py").is_file()
    assert (a.root / ".state" / f"{build_id}.json").is_file()
    assert not (cloud.data / "devices" / "headset-b" / "builds").exists()

    assert {"build.updated", "pet.say", "crew.spawn", "agent.task_progress"} <= a.bus.types()
    assert [p["agent_type"] for t, p in a.bus.events if t == "crew.spawn"] == ["builder", "tester", "scribe", "reviewer"]
    assert str(cloud.data) not in json.dumps([p for _, p in a.bus.events])
    assert b.bus.events == []

    review = a.client.get(f"/agent/builds/{build_id}/review").json()
    assert {"out/app.py", "out/test_app.py", "out/README.md"} <= set(review["files"])
    assert [x["id"] for x in a.client.get("/agent/builds").json()] == [build_id]

    # Another headset sees none of it.
    assert b.client.get("/agent/builds").json() == []
    assert b.client.get(f"/agent/builds/{build_id}").status_code == 404
    assert b.client.post(f"/agent/builds/{build_id}/ship", json={"confirm": True}).status_code == 404

    # Nothing to ship without a repo, and never without the confirm.
    assert a.client.post(f"/agent/builds/{build_id}/ship", json={"confirm": False}).status_code == 403
    refused = a.client.post(f"/agent/builds/{build_id}/ship", json={"confirm": True})
    assert refused.status_code == 409 and "Albie's cloud" in refused.json()["detail"]


def test_albies_tools_build_and_ship_only_after_the_yes(cloud, tmp_path: Path, monkeypatch) -> None:
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
        opened.append((owner, repo, head))
        return {"number": 7, "url": "https://github.com/demo/sandbox/pull/7", "base": "main"}

    import code_browser
    monkeypatch.setattr(code_browser, "open_pr", fake_open_pr)

    h = cloud()
    started = h.tool("build_start", goal="Snake game in the terminal")
    assert started["status"] == "started" and started["repo"] == "demo/sandbox"
    build_id = started["build_id"]
    h.wait(build_id, states=("awaiting_plan",), approve=False)
    assert "error" in h.tool("build_approve")  # no confirm: the user hasn't agreed yet
    assert h.tool("build_approve", confirm=True)["status"] == "approved"
    b = h.wait(build_id, approve=False)
    assert b["state"] == "ready" and b["branch"].startswith("albie/build-snake-game")

    assert h.tool("build_ship", confirm=True)["status"] == "awaiting_confirmation"  # the first call only asks
    assert git(remote, "branch", "--list", b["branch"]) == "" and opened == []
    shipped = h.tool("build_ship", confirm=True)
    assert shipped == {"status": "shipped", "pr_number": 7, "pr_url": "https://github.com/demo/sandbox/pull/7"}
    assert git(remote, "branch", "--list", b["branch"]).strip() == b["branch"]
    assert opened == [("demo", "sandbox", b["branch"])]


def test_only_the_clouds_own_repo_claude_and_the_projects_own_tests(cloud, monkeypatch) -> None:
    h = cloud()
    other = h.client.post("/agent/builds", json={"goal": "fix it", "repo": "someone/private"})
    assert other.status_code == 422 and "Leave the repository blank" in other.json()["detail"]
    assert h.client.post("/agent/builds", json={"goal": "x", "backend": "openhands"}).status_code == 422
    assert h.client.post("/agent/builds", json={"goal": "x", "checks": [["rm", "-rf", "/"]]}).status_code == 422
    assert "error" in h.tool("build_start", goal="x", repo="someone/private")
    assert h.manager.builds == {}


def test_nobody_is_limited_unless_the_owner_sets_a_limit(cloud, monkeypatch) -> None:
    monkeypatch.setattr(hub_build, "_runner", lambda: scripted_runner(delay=5.0))
    headsets = [cloud(f"headset-{i}") for i in range(3)]
    for h in headsets:  # every headset builds at once
        build_id = h.client.post("/agent/builds", json={"goal": "one"}).json()["id"]
        assert h.wait(build_id, states=("building",))["state"] == "building"
    assert all(not b.done for h in headsets for b in h.manager.builds.values())
    monkeypatch.setattr(hub_build, "_runner", lambda: scripted_runner())
    a = headsets[0]
    for h in headsets:
        for b in list(h.manager.builds.values()):
            assert h.client.post(f"/agent/builds/{b.id}/cancel").status_code == 200
    for n in range(6):  # and one headset builds again and again, the same day
        started = a.client.post("/agent/builds", json={"goal": f"project {n}"})
        assert started.status_code == 202, started.json()
        assert a.wait(started.json()["id"])["state"] == "ready"
    assert a.manager._watchers == set()  # no time limit either


def test_optional_limits_one_build_per_headset_and_daily_caps(cloud, monkeypatch) -> None:
    monkeypatch.setenv("BUILDS_PER_DAY", "1")
    monkeypatch.setenv("BUILDS_PER_DAY_TOTAL", "2")
    a, b, c = cloud("headset-a"), cloud("headset-b"), cloud("headset-c")
    first = a.client.post("/agent/builds", json={"goal": "one"}).json()["id"]
    again = a.client.post("/agent/builds", json={"goal": "two"})
    assert again.status_code == 409 and "still building" in again.json()["detail"]
    assert a.wait(first)["state"] == "ready"

    over = a.client.post("/agent/builds", json={"goal": "two"})
    assert over.status_code == 429 and over.json()["detail"] == cloud_builds.DEVICE_LIMIT.format(n=1)
    assert a.tool("build_start", goal="two") == {"error": cloud_builds.DEVICE_LIMIT.format(n=1)}

    assert b.client.post("/agent/builds", json={"goal": "three"}).status_code == 202
    total = c.client.post("/agent/builds", json={"goal": "four"})
    assert total.status_code == 429 and total.json()["detail"] == cloud_builds.TOTAL_LIMIT
    # The count outlives a restart of the server.
    assert cloud_builds.Usage(cloud.data / "builds-usage.json").used() == 2


def test_optional_limit_the_whole_cloud_builds_one_at_a_time(cloud, monkeypatch) -> None:
    monkeypatch.setenv("MAX_BUILDS", "1")
    monkeypatch.setattr(hub_build, "_runner", lambda: scripted_runner(delay=5.0))
    a, b = cloud("headset-a"), cloud("headset-b")
    one = a.client.post("/agent/builds", json={"goal": "one"}).json()["id"]
    two = b.client.post("/agent/builds", json={"goal": "two"}).json()["id"]  # planning isn't capped
    a.wait(one, states=("building",))
    b.wait(two, states=("awaiting_plan",), approve=False)

    busy = b.client.post(f"/agent/builds/{two}/approve", json={"confirm": True})
    assert busy.status_code == 429 and busy.json() == {"detail": cloud_builds.BUSY}
    assert b.tool("build_approve", confirm=True) == {"error": cloud_builds.BUSY}
    assert b.client.get(f"/agent/builds/{two}").json()["state"] == "awaiting_plan"

    assert a.client.post(f"/agent/builds/{one}/cancel").json()["state"] == "cancelled"
    assert b.client.post(f"/agent/builds/{two}/approve", json={"confirm": True}).status_code == 200
    b.wait(two, states=("building",), approve=False)
    assert cloud_builds.active() == 1


def test_optional_limit_a_plan_nobody_approves_is_dropped(cloud, monkeypatch) -> None:
    monkeypatch.setattr(cloud_builds, "build_minutes", lambda: 0.005)  # 0.3 s
    h = cloud()
    build_id = h.client.post("/agent/builds", json={"goal": "one"}).json()["id"]
    b = h.wait(build_id, states=("cancelled", "ready", "failed"), approve=False)
    assert b["state"] == "cancelled"
    deadline = time.monotonic() + 5
    while "Nobody approved" not in str(b["error"]) and time.monotonic() < deadline:
        time.sleep(0.02)
        b = h.client.get(f"/agent/builds/{build_id}").json()
    assert "Nobody approved the plan in time" in b["error"]


def test_the_crews_claude_turns_see_none_of_the_spaces_secrets(monkeypatch) -> None:
    from agent import runner

    monkeypatch.setattr(runner, "HIDDEN_ENV", runner.HIDDEN_ENV)
    monkeypatch.setattr(runner, "agent_env", runner.agent_env)
    for name in ("ALBIE_APP_KEY", "GITHUB_TOKEN", "HF_TOKEN", "SOME_VENDOR_SECRET", "ANTHROPIC_API_KEY"):
        monkeypatch.setenv(name, "real-secret")
    cloud_builds.harden_runner()
    cloud_builds.harden_runner()  # twice: it wraps the hub's agent_env once
    env = runner.agent_env()
    assert all(env[name] == "" for name in ("ALBIE_APP_KEY", "GITHUB_TOKEN", "HF_TOKEN", "SOME_VENDOR_SECRET"))
    assert "ANTHROPIC_API_KEY" not in env  # the CLI needs it; it strips it from the commands it runs:
    assert env["CLAUDE_CODE_SUBPROCESS_ENV_SCRUB"] == "1"
    assert "real-secret" not in env.values()


E2E = textwrap.dedent("""
    import asyncio, json, sys
    import httpx
    import app as cloud
    from agent import demo_runner

    HEADERS = {"x-albie-key": "test-key", "x-albie-device": "headset-e2e-1"}

    async def no_delay(_):
        pass

    async def main():
        demo_runner.asyncio = type("A", (), {"sleep": staticmethod(no_delay)})  # the sample's pauses
        await asyncio.to_thread(cloud.POOL.start)
        try:
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=cloud.app), base_url="http://cloud",
                                         headers=HEADERS, timeout=120) as http:
                out = {"no_key": (await http.get("/agent/builds", headers={"x-albie-key": "wrong"})).status_code}
                health = (await http.get("/health")).json()
                out["health_builds"] = health["builds"]
                started = await http.post("/tools/build_start", json={"goal": "a tip calculator"})
                build_id = started.json()["result"]["build_id"]
                seen, state, after = set(), "", 0
                for _ in range(1500):
                    b = (await http.get("/agent/builds/" + build_id)).json()
                    state = b["state"]
                    events = (await http.get("/events?after=%d" % after)).json()
                    after = events["seq"]
                    seen |= {e["type"] for e in events["events"]}
                    if state == "awaiting_plan":
                        await http.post("/tools/build_approve", json={"confirm": True})
                    if state in ("ready", "failed", "cancelled"):
                        break
                    await asyncio.sleep(0.02)
                out.update(state=state, error=b["error"], demo=b["demo"], events=sorted(seen),
                           checks=[c["state"] for c in b["verification"]],
                           listed=len((await http.get("/agent/builds")).json()),
                           other=(await http.get("/agent/builds", headers={"x-albie-device": "headset-e2e-2"})).json(),
                           ship=(await http.post("/agent/builds/%s/ship" % build_id, json={"confirm": True})).json(),
                           files=(await http.get("/agent/builds/%s/review" % build_id)).json()["files"])
                print("RESULT " + json.dumps(out))
        finally:
            await asyncio.to_thread(cloud.POOL.shutdown)

    if __name__ == "__main__":  # the CAD worker is a spawned process: it imports this file again
        asyncio.run(main())
""")


def test_the_real_cloud_app_builds_the_scripted_sample_end_to_end(tmp_path: Path) -> None:
    pytest.importorskip("build123d")  # the cloud app starts a CAD worker for every headset
    env = {k: v for k, v in os.environ.items() if k not in ("ANTHROPIC_API_KEY", "GITHUB_TOKEN", "BUILD_REPO", "CLOUD_BUILDS")}
    env.update(PYTHONPATH=os.pathsep.join(str(p) for p in (CLOUD / "stubs", CLOUD, REPO / "hub")),
               ALBIE_APP_KEY="test-key", CLOUD_DATA_DIR=str(tmp_path / "data"), BUILD_DEMO="1",
               FAKE_AGENT="0", CLAUDE_PROXY_URL="")
    script = tmp_path / "e2e.py"
    script.write_text(E2E)
    run = subprocess.run([sys.executable, str(script)], cwd=tmp_path, env=env, capture_output=True, text=True, timeout=300)
    line = next((x for x in run.stdout.splitlines() if x.startswith("RESULT ")), None)
    assert line, run.stdout[-2000:] + run.stderr[-4000:]
    out = json.loads(line[len("RESULT "):])
    assert out["no_key"] == 401 and out["health_builds"] is True
    assert out["state"] == "ready" and out["demo"] is True and out["error"] is None, out
    assert out["checks"] and all(c == "passed" for c in out["checks"])
    assert {"build.updated", "pet.say", "crew.spawn"} <= set(out["events"])
    assert out["listed"] == 1 and out["other"] == []
    assert "disabled for the scripted local demo" in out["ship"]["detail"]
    assert out["files"]
    assert (tmp_path / "data" / "devices" / "headset-e2e-1" / "builds" / ".state").is_dir()
