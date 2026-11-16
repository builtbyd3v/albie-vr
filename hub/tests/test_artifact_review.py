"""POST /artifacts/review (plan C1): the advisory Jev review. Jev is always mocked; no network, no key needed.

Fixtures: fan.json is audit 2's archetype A (a valid artifact); seesaw/ and traffic_light/ are copies of the two
broken model-written artifacts saved on the device (unknown fields such as "position", "scripts").
"""
from __future__ import annotations

import json
import time
from pathlib import Path
from typing import Any

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from artifacts import review as rv
from artifacts import router
from ml.setup import jev
from ml.setup.llm import LLMError

FIX = Path(__file__).parent / "fixtures" / "artifact_review"
FAN = json.loads((FIX / "fan.json").read_text())
FAN_FACTS = ("hub: sphere 6 cm, on mast; mast: cylinder 26 cm tall, rests on base; 3 blades 20 cm, turn about the hub; "
             "slider speed (0..4) sets how fast the blades turn; button start plays the rotors, stop pauses them")


def saved(name: str) -> dict[str, Any]:
    return {f: json.loads((FIX / name / f"{f}.json").read_text()) for f in ("manifest", "scene")}


def body(**over: Any) -> dict[str, Any]:
    out = {"request": "a fan with a speed slider", "manifest": FAN["manifest"], "scene": FAN["scene"],
           "facts": FAN_FACTS}
    out.update(over)
    return out


@pytest.fixture
def client():
    app = FastAPI()
    app.include_router(router)
    with TestClient(app) as c:  # one event loop for the test, like uvicorn: a timed-out Jev thread isn't joined
        yield c


@pytest.fixture
def key(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("TYPESAFE_API_KEY", "test-key")


class FakeJev:
    """Stands in for jev.ask: records the call and answers each question with a chosen option."""

    def __init__(self, choices: dict[str, str] | None = None, confidence: float = 0.9) -> None:
        self.choices, self.confidence, self.calls = choices or {}, confidence, []

    def __call__(self, state: str, questions: dict[str, Any], timeout: float = 60) -> dict[str, Any]:
        self.calls.append((state, questions, timeout))
        answers = {}
        for name, q in questions.items():
            options = list(q["criteria"])
            choice = self.choices.get(name, options[0])
            answers[name] = {"choice": choice, "confidence": self.confidence,
                             "probabilities": {o: (self.confidence if o == choice else 0.01) for o in options}}
        return {"model": "jev-test", "answers": answers, "usage": {}}


def test_no_key_is_skipped_quickly_and_never_calls_jev(client, monkeypatch):
    monkeypatch.delenv("TYPESAFE_API_KEY", raising=False)
    monkeypatch.setattr(jev, "ask", lambda *a, **k: pytest.fail("Jev must not be called without a key"))
    started = time.monotonic()
    res = client.post("/artifacts/review", json=body())
    assert time.monotonic() - started < 0.5
    assert res.status_code == 200
    assert res.json()["status"] == "skipped" and res.json()["reason"] == "no_key" and res.json()["verdicts"] == []


def test_good_artifact_gets_every_question_and_ok_verdicts(client, key, monkeypatch):
    fake = FakeJev(choices={"space_fits": "volume"})
    monkeypatch.setattr(jev, "ask", fake)
    res = client.post("/artifacts/review", json=body())
    out = res.json()
    assert res.status_code == 200 and out["status"] == "ok" and out["model"] == "jev-test"
    assert [v["question"] for v in out["verdicts"]] == list(rv.QUESTIONS)
    assert all(v["ok"] for v in out["verdicts"])
    assert all(v["confidence"] == pytest.approx(0.9) for v in out["verdicts"])

    state, questions, timeout = fake.calls[0]
    assert timeout == rv.TIMEOUT_S  # the HTTP call inside Jev ends with the budget too
    for q in questions.values():  # the classify_jev.py choice form
        assert q["type"] == "choice" and q["instructions"] and q["criteria"]
    assert "REQUEST: a fan with a speed slider" in state
    assert FAN_FACTS in state
    assert 'slider "Speed" sets input speed' in state
    assert 'button "Start" sends input start' in state
    assert "rate bound to $speed" in state
    assert "when input start: sim rotor_a play" in state


def test_state_has_no_raw_coordinates(key, monkeypatch):
    state = rv.build_state(rv.ReviewIn(**body()))
    for n in FAN["scene"]["nodes"]:
        if "pos" in n:
            assert json.dumps(n["pos"]) not in state
    assert "-0.27" not in state
    for name in ("seesaw", "traffic_light"):
        s = rv.build_state(rv.ReviewIn(request=name, facts="", **saved(name)))
        assert "position" not in s and "0.8" not in s


@pytest.mark.parametrize("question, choice", [
    ("matches_request", "missing_main_part"),
    ("matches_request", "different_thing"),
    ("parts_supported", "floating_or_unattached"),
    ("controls_effective", "some_do_nothing"),
    ("controls_match_labels", "wrong_effect"),
    ("arrangement_sensible", "wrong_proportions"),
    ("space_fits", "room"),  # the scene says volume
])
def test_named_problem_is_not_ok(client, key, monkeypatch, question, choice):
    monkeypatch.setattr(jev, "ask", FakeJev(choices={question: choice}))
    out = client.post("/artifacts/review", json=body()).json()
    verdict = next(v for v in out["verdicts"] if v["question"] == question)
    assert verdict["choice"] == choice and verdict["ok"] is False


def test_unsure_counts_as_ok(client, key, monkeypatch):
    monkeypatch.setattr(jev, "ask", FakeJev(choices={"matches_request": "unsure"}, confidence=0.4))
    out = client.post("/artifacts/review", json=body()).json()
    assert next(v for v in out["verdicts"] if v["question"] == "matches_request")["ok"] is True


def test_questions_that_do_not_apply_are_not_asked(client, key, monkeypatch):
    fake = FakeJev()
    monkeypatch.setattr(jev, "ask", fake)
    # The saved seesaw: no widgets (its "slider" is an unknown field), space room, no request, no facts.
    out = client.post("/artifacts/review", json={**saved("seesaw"), "request": "", "facts": ""}).json()
    assert out["status"] == "ok"
    assert set(fake.calls[0][1]) == {"arrangement_sensible", "space_fits"}
    assert next(v for v in out["verdicts"] if v["question"] == "space_fits")["ok"] is False  # Jev's first: volume


def test_question_subset(client, key, monkeypatch):
    fake = FakeJev()
    monkeypatch.setattr(jev, "ask", fake)
    out = client.post("/artifacts/review", json=body(questions=["matches_request", "matches_request",
                                                                "controls_match_labels"])).json()
    assert list(fake.calls[0][1]) == ["matches_request", "controls_match_labels"]
    assert [v["question"] for v in out["verdicts"]] == ["matches_request", "controls_match_labels"]


def test_nothing_to_ask_is_skipped(client, key, monkeypatch):
    monkeypatch.setattr(jev, "ask", lambda *a, **k: pytest.fail("nothing to ask"))
    out = client.post("/artifacts/review", json=body(request="", questions=["matches_request"])).json()
    assert out["status"] == "skipped" and out["reason"] == "no_questions"


def test_timeout_is_an_error_not_a_wait(client, key, monkeypatch):
    def slow(state, questions, timeout=60):
        time.sleep(1.0)
        return FakeJev()(state, questions)

    monkeypatch.setattr(jev, "ask", slow)
    monkeypatch.setattr(rv, "TIMEOUT_S", 0.1)
    started = time.monotonic()
    res = client.post("/artifacts/review", json=body())
    assert time.monotonic() - started < 0.8 and res.json()["elapsed_ms"] < 800
    assert res.status_code == 200 and res.json()["status"] == "error" and res.json()["reason"] == "timeout"


def test_jev_error_is_reported_not_raised(client, key, monkeypatch):
    def broken(*a, **k):
        raise LLMError("TypeSafe API error 500: boom")

    monkeypatch.setattr(jev, "ask", broken)
    res = client.post("/artifacts/review", json=body())
    assert res.status_code == 200
    assert res.json() == {**res.json(), "status": "error", "reason": "TypeSafe API error 500: boom", "verdicts": []}


def test_unexpected_exception_is_still_200(client, key, monkeypatch):
    monkeypatch.setattr(jev, "ask", lambda *a, **k: 1 / 0)
    res = client.post("/artifacts/review", json=body())
    assert res.status_code == 200 and res.json()["status"] == "error" and "ZeroDivisionError" in res.json()["reason"]


@pytest.mark.parametrize("reply", [{}, {"answers": None}, {"answers": {"matches_request": {"choice": "maybe"}}}, []])
def test_bad_reply_is_an_error(client, key, monkeypatch, reply):
    monkeypatch.setattr(jev, "ask", lambda *a, **k: reply)
    res = client.post("/artifacts/review", json=body())
    assert res.status_code == 200 and res.json()["status"] == "error" and res.json()["reason"] == "bad_response"


def test_partial_reply_keeps_the_good_verdicts(client, key, monkeypatch):
    monkeypatch.setattr(jev, "ask", lambda *a, **k: {"model": "jev-test", "answers": {
        "matches_request": {"choice": "yes", "confidence": 0.8}, "space_fits": {"choice": "nonsense"}}})
    out = client.post("/artifacts/review", json=body()).json()
    assert out["status"] == "ok" and [v["question"] for v in out["verdicts"]] == ["matches_request"]


@pytest.mark.parametrize("bad", [{"facts": "x" * 6001}, {"request": "x" * 501}, {"questions": ["is_it_fun"]},
                                 {"scene": "not a dict"}])
def test_bad_input_is_422(client, key, bad):
    assert client.post("/artifacts/review", json=body(**bad)).status_code == 422


def test_huge_scene_is_capped(key):
    scene = {"space": "volume", "nodes": [{"id": f"p{i}", "kind": "primitive", "shape": "box"} for i in range(1000)]}
    state = rv.build_state(rv.ReviewIn(manifest={"id": "big"}, scene=scene))
    assert "and 880 more" in state and len(state) < 10_000


def test_hub_main_registers_the_router():
    text = (Path(__file__).parents[1] / "main.py").read_text()
    assert "from artifacts import router as artifacts_router" in text
    assert "app.include_router(artifacts_router)" in text


def test_hub_app_mounts_the_route():
    try:
        import main
    except ModuleNotFoundError as exc:  # main imports every module (claude_agent_sdk, build123d, ...)
        pytest.skip(f"the full hub needs {exc.name}")

    paths = main.create_app(watch=False).openapi()["paths"]  # newer FastAPI nests included routers in .routes
    assert "post" in paths["/artifacts/review"]
