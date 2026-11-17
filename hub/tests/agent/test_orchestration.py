"""The headset-independent orchestration API uses the existing fake Claude runner."""
from __future__ import annotations

import time
import asyncio

from fastapi.testclient import TestClient


def wait_until(client: TestClient, workflow_id: str, predicate, timeout: float = 5) -> dict:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        response = client.get(f"/agent/workflows/{workflow_id}")
        assert response.status_code == 200
        workflow = response.json()
        if predicate(workflow):
            return workflow
        time.sleep(0.02)
    raise AssertionError(f"workflow did not reach expected state: {workflow}")


def test_parallel_tasks_finish_without_unity_roster(client: TestClient, monkeypatch) -> None:
    monkeypatch.setenv("FAKE_AGENT_DELAY", "0.15")
    response = client.post("/agent/workflows", json={"project_id": "demo", "tasks": [
        {"role": "scout", "instruction": "Find the entry point"},
        {"role": "reviewer", "instruction": "Review the changes"},
    ]})
    assert response.status_code == 202
    initial = response.json()
    assert initial["backend"] == "simulated"
    assert len({t["id"] for t in initial["tasks"]}) == 2
    workflow = wait_until(client, initial["id"], lambda w: all(t["state"] == "running" for t in w["tasks"]))
    assert all(t["started_at"] for t in workflow["tasks"])
    workflow = wait_until(client, initial["id"], lambda w: all(t["state"] == "completed" for t in w["tasks"]))
    assert all("WebSocket handler" in t["result"] for t in workflow["tasks"])
    assert {t["role"] for t in workflow["tasks"]} == {"scout", "reviewer"}
    assert "<html" in client.get("/agent/orchestration").text.lower()


def test_failure_and_cancellation_are_distinct(client: TestClient, monkeypatch) -> None:
    monkeypatch.setenv("FAKE_AGENT_DELAY", "0.3")
    response = client.post("/agent/workflows", json={"tasks": [
        {"role": "scout", "instruction": "error"},
        {"role": "sentinel", "instruction": "Inspect the project"},
    ]})
    workflow = response.json()
    workflow_id = workflow["id"]
    running = wait_until(client, workflow_id, lambda w: all(t["state"] == "running" for t in w["tasks"]))
    stop_id = running["tasks"][1]["id"]
    stopped = client.post(f"/agent/workflows/{workflow_id}/tasks/{stop_id}/cancel")
    assert stopped.status_code == 200 and stopped.json()["state"] == "cancelled"
    finished = wait_until(client, workflow_id, lambda w: w["tasks"][0]["state"] == "failed")
    assert finished["tasks"][0]["error"] and finished["tasks"][0]["result"] is None
    assert finished["tasks"][1]["state"] == "cancelled" and finished["tasks"][1]["result"] is None
    assert client.post(f"/agent/workflows/{workflow_id}/tasks/{stop_id}/cancel").status_code == 409


def test_validation_and_unknown_workflows(client: TestClient) -> None:
    assert client.post("/agent/workflows", json={"tasks": []}).status_code == 422
    assert client.post("/agent/workflows", json={"tasks": [{"role": "builder", "instruction": "edit code"}]}).status_code == 422
    assert client.post("/agent/workflows", json={"tasks": [{"role": "scout", "instruction": "edit code", "mode": "code"}]}).status_code == 422
    assert client.post("/agent/workflows", json={"tasks": [{"role": "scout", "instruction": "   "}]}).status_code == 422
    assert client.post("/agent/workflows", json={"project_id": "missing", "tasks": [{"role": "scout", "instruction": "hello"}]}).status_code == 404
    assert client.get("/agent/workflows/not-found").status_code == 404


def test_workflow_tools_are_lead_only_and_return_results(client: TestClient) -> None:
    from agent import tools

    assert {"workflow_start", "workflow_status", "workflow_cancel"} <= set(tools._names())
    assert "workflow_start" not in tools._names(lead=False)

    async def start_and_wait():
        result = await tools.call("workflow_start", {"project_id": "demo", "tasks": [
            {"role": "scout", "instruction": "Where is the entry point?"},
            {"role": "reviewer", "instruction": "Review the changes."},
        ]}, "bit")
        assert result["backend"] == "simulated"
        for _ in range(300):
            status = await tools.call("workflow_status", {"workflow_id": result["id"]}, "bit")
            if all(t["state"] == "completed" for t in status["tasks"]):
                return status
            await asyncio.sleep(0.01)
        raise AssertionError("workflow tools did not complete")

    status = asyncio.run(start_and_wait())
    assert all(t["result"] for t in status["tasks"])
