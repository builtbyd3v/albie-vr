"""Publication must follow approval, successful execution, independent tests and an unchanged reviewed commit."""
import asyncio
import sys
import time
from pathlib import Path

import pytest

from agent import build as module
from agent import events
from agent.build_review import command_env, review
from tests.agent.test_build import builds_env, scripted_runner, wait_for, git  # noqa: F401


def awaiting(client, build_id):
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        current = client.get(f"/agent/builds/{build_id}").json()
        if current["state"] == "awaiting_plan":
            return current
        time.sleep(.01)
    raise AssertionError(current)


def test_no_workspace_or_code_runs_before_plan_approval(builds_env, monkeypatch):
    runner = scripted_runner()
    monkeypatch.setattr(module, "_runner", lambda: runner)
    build_id = builds_env.post("/agent/builds", json={"goal": "calculator"}).json()["id"]
    awaiting(builds_env, build_id)
    assert runner.seen == []
    assert module.builds.get(build_id).workspace is None
    assert builds_env.post(f"/agent/builds/{build_id}/approve", json={"confirm": False}).status_code == 409
    final = wait_for(builds_env, build_id)
    assert final["state"] == "ready"
    assert final["approved_at"] and final["verification"][0]["state"] == "passed"
    detail = builds_env.get(f"/agent/builds/{build_id}/review").json()
    assert "out/app.py" in detail["files"] and "test_runs" in detail["diff"]
    assert detail["head"] == final["review_head"]


@pytest.mark.parametrize("failure", ["exception", "event", "empty", "review"])
def test_agent_failures_cannot_be_ready_or_publishable(builds_env, monkeypatch, failure):
    base = scripted_runner()

    async def run(project, text, mode, emit, **kwargs):
        if failure == "review" and mode == "qa":
            return "Blocking bug in app.py. REVIEW: FAIL"
        if mode == "code" and failure != "review":
            if failure == "exception":
                raise RuntimeError("broken")
            if failure == "event":
                await emit(events.error(kwargs["pet_id"], "Agent unavailable"))
                return "Something went wrong"
            return ""
        return await base(project, text, mode, emit, **kwargs)

    monkeypatch.setattr(module, "_runner", lambda: run)
    first = builds_env.post("/agent/builds", json={"goal": "test failure handling"}).json()
    final = wait_for(builds_env, first["id"])
    assert final["state"] == "failed"
    assert any(step["state"] == "failed" for step in final["steps"])
    assert builds_env.post(f"/agent/builds/{first['id']}/ship", json={"confirm": True}).status_code == 409


def test_independent_failed_check_blocks_ready_even_if_agents_report_success(builds_env, monkeypatch):
    monkeypatch.setattr(module, "_runner", lambda: scripted_runner())
    first = builds_env.post("/agent/builds", json={"goal": "check gating", "checks": [[sys.executable, "-c", "raise SystemExit(7)"]]}).json()
    final = wait_for(builds_env, first["id"])
    assert final["state"] == "failed"
    assert final["verification"][0]["exit_code"] == 7
    assert final["verification"][0]["state"] == "failed"


def test_cancel_while_waiting_for_plan_never_executes(builds_env, monkeypatch):
    runner = scripted_runner()
    monkeypatch.setattr(module, "_runner", lambda: runner)
    first = builds_env.post("/agent/builds", json={"goal": "not approved"}).json()
    awaiting(builds_env, first["id"])
    assert builds_env.post(f"/agent/builds/{first['id']}/cancel").json()["state"] == "cancelled"
    assert runner.seen == []


def test_history_and_review_survive_a_new_orchestrator(builds_env, monkeypatch):
    monkeypatch.setattr(module, "_runner", lambda: scripted_runner())
    final = wait_for(builds_env, builds_env.post("/agent/builds", json={"goal": "persistent"}).json()["id"])
    restored = module.Builds().get(final["id"])
    assert restored.state == "ready"
    assert restored.review_head == final["review_head"]
    assert "out/test_app.py" in review(restored)["files"]


def test_tests_cannot_read_hub_credentials(monkeypatch):
    for name in ("ANTHROPIC_API_KEY", "GITHUB_TOKEN", "XAI_API_KEY", "SLACK_BOT_TOKEN"):
        monkeypatch.setenv(name, "test-secret")
    assert not any(name in command_env() for name in ("ANTHROPIC_API_KEY", "GITHUB_TOKEN", "XAI_API_KEY", "SLACK_BOT_TOKEN"))


def test_mutating_a_reviewed_workspace_prevents_publication(builds_env, monkeypatch):
    monkeypatch.setattr(module, "_runner", lambda: scripted_runner())
    final = wait_for(builds_env, builds_env.post("/agent/builds", json={"goal": "stale review"}).json()["id"])
    build = module.builds.get(final["id"])
    build.repo, build.branch = "demo/example", "test-branch"
    (build.workspace / "out" / "app.py").write_text("unreviewed\n")
    result = builds_env.post(f"/agent/builds/{build.id}/ship", json={"confirm": True})
    assert result.status_code == 409 and "changed after review" in result.json()["detail"]


def test_cross_origin_publication_is_rejected(builds_env):
    result = builds_env.post("/agent/builds/missing/ship", json={"confirm": True}, headers={"Origin": "https://outside.example"})
    assert result.status_code == 403


def test_workflow_milestones_reach_the_registered_voice_bridge(monkeypatch):
    from agent import speech
    spoken = []

    async def speak(pet_id, text):
        spoken.append((pet_id, text))
        return True

    monkeypatch.setattr(speech, "speak", speak)
    asyncio.run(module.say("Tester is checking the project."))
    assert spoken == [("bit", "Tester is checking the project.")]
