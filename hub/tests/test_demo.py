"""Laptop demo remote: /demo page and the demo.next/back/reset/goto events (no hub, no Unity)."""
from __future__ import annotations

from typing import Any

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from demo.router import BEATS, router
from events import EventBus


class Recorder(EventBus):
    def __init__(self) -> None:
        super().__init__()
        self.events: list[tuple[str, dict[str, Any]]] = []

    async def publish(self, type_: str, payload: Any) -> None:
        self.events.append((type_, payload.model_dump() if hasattr(payload, "model_dump") else payload))
        await super().publish(type_, payload)


def make() -> tuple[TestClient, Recorder]:
    app = FastAPI()
    app.include_router(router)
    app.state.bus = Recorder()
    return TestClient(app), app.state.bus


def test_page_lists_buttons_and_every_beat() -> None:
    client, _ = make()
    r = client.get("/demo")
    assert r.status_code == 200 and "text/html" in r.headers["content-type"]
    for label in ("Cue", "Back", "Skip beat", "Reset"):
        assert f">{label}<" in r.text
    for beat, _name in BEATS:
        assert f'data-beat="{beat}"' in r.text
    assert "%BEATS%" not in r.text and "%CUES%" not in r.text


def test_next_back_reset_publish_their_event() -> None:
    client, bus = make()
    assert client.post("/demo/step").json()["type"] == "demo.step"
    assert bus.events.pop() == ("demo.step", {})
    for name in ("next", "back", "reset"):
        r = client.post(f"/demo/{name}")
        assert r.status_code == 200 and r.json()["type"] == f"demo.{name}"
    assert bus.events == [("demo.next", {}), ("demo.back", {}), ("demo.reset", {})]


def test_goto_publishes_the_beat() -> None:
    client, bus = make()
    assert client.post("/demo/goto", json={"beat": "B3"}).status_code == 200
    assert bus.events == [("demo.goto", {"beat": "B3"})]
    client.post("/demo/goto", json={"beat": "b5b"})
    assert bus.events[-1] == ("demo.goto", {"beat": "B5b"})


def test_goto_rejects_junk() -> None:
    client, bus = make()
    assert client.post("/demo/goto", json={"beat": "later"}).status_code == 422
    assert client.post("/demo/goto", json={}).status_code == 422
    assert bus.events == []


def test_main_app_includes_the_remote() -> None:
    pytest.importorskip("claude_agent_sdk")  # main.py imports the whole hub
    from main import create_app

    paths = set(create_app(watch=False).openapi()["paths"])
    assert {"/demo", "/demo/step", "/demo/next", "/demo/back", "/demo/reset", "/demo/goto"} <= paths
