"""The no-key demo exercises the real workflow, commits and tests, with scripted sample generation."""
from agent import build as module
from tests.agent.test_build import builds_env, wait_for  # noqa: F401


def test_demo_finishes_without_provider_keys_and_cannot_publish(builds_env, monkeypatch):
    monkeypatch.setenv("BUILD_DEMO", "1")
    monkeypatch.setenv("ANTHROPIC_API_KEY", "")
    monkeypatch.setenv("GITHUB_TOKEN", "")

    async def no_delay(_):
        pass

    from agent import demo_runner
    monkeypatch.setattr(demo_runner.asyncio, "sleep", no_delay)
    first = builds_env.post("/agent/builds", json={"goal": "Try the local demo"}).json()
    final = wait_for(builds_env, first["id"])
    assert final["state"] == "ready", final
    assert final["demo"] and final["simulated"]
    assert final["planned_by"] == "demo"
    assert "fixed local sample" in final["summary"]
    assert all(s["state"] == "done" for s in final["steps"])
    assert "Ran 6 tests" in final["verification"][0]["output"]
    detail = builds_env.get(f"/agent/builds/{first['id']}/review").json()
    assert set(detail["files"]) == {"tipcalc.py", "test_tipcalc.py", "README.md"}
    result = builds_env.post(f"/agent/builds/{first['id']}/ship", json={"confirm": True})
    assert result.status_code == 409 and "disabled" in result.json()["detail"]


def test_demo_refuses_repository_or_executor_overrides(builds_env, monkeypatch):
    monkeypatch.setenv("BUILD_DEMO", "1")
    for extra in ({"repo": "someone/repo"}, {"backend": "openhands"}, {"checks": [["arbitrary-command"]]}):
        result = builds_env.post("/agent/builds", json={"goal": "demo", **extra})
        assert result.status_code == 422
