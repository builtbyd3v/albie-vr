"""Bit's ML tools (ml/tools.py) and workspace tools (agent/ui_tools.py), against the make_mocks image repo."""
import asyncio
import json
import sys
import textwrap

import pytest

from agent import tools as agent_tools
from agent import ui_tools
from ml import make_mocks
from ml import tools as ml_tools
from ml.service import MLService


class FakeBus:
    def __init__(self):
        self.sent = []

    async def publish(self, type_, payload):
        self.sent.append((type_, payload if isinstance(payload, dict) else payload.model_dump()))


def run(coro):
    return asyncio.run(coro)


@pytest.fixture()
def svc(tmp_path):
    if not make_mocks.UNITY_MOCKS.exists():
        pytest.skip("Unity ML mocks missing")
    make_mocks.image_classification(tmp_path / "img")
    ml_tools.pending = None
    return MLService(tmp_path / "img")


def ml(name, args, svc, bus):
    return run(ml_tools.call_tool(name, args, svc, bus))


def test_summary_is_small_enough_to_speak(svc):
    out = ml("ml_get_run_summary", {}, svc, FakeBus())
    assert out["run_id"] == "mock0001" and 0 <= out["accuracy"] <= 1
    assert len(out["worst_classes"]) == 3 and len(out["top_confusions"]) == 3
    accs = [c["accuracy"] for c in out["worst_classes"]]
    assert accs == sorted(accs) and all(isinstance(c["name"], str) for c in out["worst_classes"])
    pair = out["top_confusions"][0]
    assert set(pair) == {"true", "predicted", "count"} and isinstance(pair["true"], str)
    assert len(json.dumps(out)) < 600


def test_summary_errors(svc):
    assert "error" in ml("ml_get_run_summary", {"run_id": "nope"}, svc, FakeBus())
    assert "error" in ml("ml_get_run_summary", {"run_id": "../x"}, svc, FakeBus())


def test_relabel_by_name_or_index_runs_now_and_broadcasts(svc):
    bus = FakeBus()
    out = ml("ml_relabel", {"sample_id": "val_0003", "label": "Cat"}, svc, bus)
    assert out == {"id": "val_0003", "label_idx": 3, "removed": False}
    assert bus.sent == [("ml.sample_updated", out)]
    assert ml("ml_relabel", {"sample_id": "val_0003", "label": 5}, svc, bus)["label_idx"] == 5
    assert ml("ml_relabel", {"sample_id": "val_0003", "label": "2"}, svc, bus)["label_idx"] == 2
    assert svc.overrides()[0] == {"val_0003": 2}


def test_relabel_rejects_bad_input(svc):
    bus = FakeBus()
    assert "error" in ml("ml_relabel", {"sample_id": "val_0003", "label": "unicorn"}, svc, bus)
    assert "error" in ml("ml_relabel", {"sample_id": "val_0003", "label": 99}, svc, bus)
    assert "error" in ml("ml_relabel", {"sample_id": "nope", "label": 1}, svc, bus)
    assert "error" in ml("ml_relabel", {"label": 1}, svc, bus)
    assert bus.sent == []


def test_remove_waits_for_confirmation(svc):
    bus = FakeBus()
    out = ml("ml_remove", {"sample_id": "val_0001"}, svc, bus)
    assert out["status"] == "awaiting_confirmation" and bus.sent == [] and svc.overrides()[1] == set()
    done = ml("ml_confirm", {"confirm": True}, svc, bus)
    assert done["status"] == "done" and done["removed"] is True
    assert bus.sent == [("ml.sample_updated", {"id": "val_0001", "label_idx": done["label_idx"], "removed": True})]
    assert "error" in ml("ml_confirm", {"confirm": True}, svc, bus)  # nothing pending any more


def test_cancel_drops_the_pending_action(svc):
    bus = FakeBus()
    ml("ml_remove", {"sample_id": "val_0001"}, svc, bus)
    assert ml("ml_confirm", {"confirm": False}, svc, bus)["status"] == "cancelled"
    assert bus.sent == [] and svc.overrides()[1] == set()
    assert "error" in ml("ml_remove", {"sample_id": "nope"}, svc, bus)


def test_training_waits_for_confirmation_then_starts(tmp_path, monkeypatch):
    repo = tmp_path / "repo"
    (repo / ".forge").mkdir(parents=True)
    (repo / ".forge" / "profile.json").write_text(json.dumps(
        {"archetype": "generic", "python": sys.executable, "train_cmd": "python train.py --epochs 10"}))
    (repo / "train.py").write_text(textwrap.dedent("""
        import json, os, sys
        json.dump({"argv": sys.argv[1:], "hub": os.environ["FORGE_HUB"]}, open("ran.json", "w"))
    """))
    ml_tools.pending = None
    svc, bus = MLService(repo), FakeBus()
    assert "error" in ml("ml_start_training", {"epochs": 0}, svc, bus)
    assert ml("ml_start_training", {"epochs": 4}, svc, bus)["status"] == "awaiting_confirmation"
    assert svc.train_proc is None
    out = ml("ml_confirm", {"confirm": True}, svc, bus)
    assert out["status"] == "started" and out["run_id"] == svc.train_run_id
    svc.train_proc.wait(timeout=30)
    assert json.loads((repo / "ran.json").read_text()) == {"argv": ["--epochs", "4"], "hub": "http://localhost:8000"}


def test_unknown_ml_tool(svc):
    assert "error" in ml("ml_nope", {}, svc, FakeBus())


def ui(name, args, bus):
    return run(ui_tools.call_tool(name, args, bus))


def test_highlight_broadcasts_the_existing_event():
    bus = FakeBus()
    assert ui("ui_highlight", {"panel": "ml-results", "item_ids": ["val_0003"]}, bus)["ok"]
    assert ui("ui_highlight", {"panel": "ml-results", "class_idx": 3}, bus)["ok"]
    assert bus.sent == [("ui.highlight", {"panel": "ml-results", "item_ids": ["val_0003"], "class_idx": None}),
                        ("ui.highlight", {"panel": "ml-results", "item_ids": None, "class_idx": 3})]


def test_highlight_rejects_bad_input():
    bus = FakeBus()
    assert "error" in ui("ui_highlight", {"panel": "nowhere"}, bus)
    assert "error" in ui("ui_highlight", {"panel": "ml-results", "item_ids": "val_1"}, bus)
    assert "error" in ui("ui_highlight", {"panel": "ml-results", "class_idx": "cat"}, bus)
    assert "error" in ui("ui_nope", {}, bus)
    assert bus.sent == []


def test_registry_walks_bit_to_the_ml_panel():
    assert agent_tools.PANELS["ml"] == "ml-results"
    names = {d["name"] for d in ml_tools.TOOLS + ui_tools.TOOLS}
    assert names == {"ml_get_run_summary", "ml_relabel", "ml_remove", "ml_start_training", "ml_confirm",
                     "ui_highlight"}  # ui_open_view: agent/workspace.py


def test_hub_registers_the_tools(tmp_path, monkeypatch):
    from fastapi.testclient import TestClient

    for k, v in {"FAKE_AGENT": "1", "STT_PROVIDER": "none", "VOICE_PROVIDER": "fake", "SCREEN_BRIDGE": "0"}.items():
        monkeypatch.setenv(k, v)

    from main import create_app

    with TestClient(create_app(data_dir=tmp_path, watch=False)) as client:
        assert agent_tools.has("ml_get_run_summary") and agent_tools.has("ui_open_view")
        assert client.app.state.ml is not None
