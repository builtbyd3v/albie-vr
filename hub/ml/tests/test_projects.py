"""GET /ml/projects: every ML project the headset's Projects tab lists (MLTools.projects)."""
from __future__ import annotations

import json
from types import SimpleNamespace

from fastapi import FastAPI
from fastapi.testclient import TestClient

from events import EventBus
from ml import tools as ml_tools
from ml.router import router
from ml.service import MLService
from ml.tools import MLTools


def _project(root, name, *, built=None, summary=None, best=None):
    path = root / name
    (path / ".forge").mkdir(parents=True)
    if built:
        (path / "project.json").write_text(json.dumps(built))
    if summary:
        (path / ".forge" / "profile.json").write_text(json.dumps({"summary": summary}))
    if best is not None:
        run = path / ".forge" / "runs" / "r1"
        run.mkdir(parents=True)
        (run / "summary.json").write_text(json.dumps({"best_val_acc": best}))
    return path


def _setup(tmp_path, monkeypatch):
    monkeypatch.setattr(ml_tools, "SEARCH_DIRS", [])  # only the folders this test makes
    monkeypatch.delenv("FORGE_ML_PROJECTS", raising=False)
    data = tmp_path / "data"
    template = _project(tmp_path, "template", summary="CIFAR-10 image classifier", best=0.71)
    built = _project(data / "ml" / "new", "hf-imdb-1117", summary="DistilBERT text classifier, 2 classes",
                     built={"title": "DistilBERT fine-tuned on imdb", "task": "text_classification",
                            "model": "hf", "dataset": "stanfordnlp/imdb", "base_model": "distilbert-base-uncased"},
                     best=0.874)
    cloned = _project(data / "ml" / "github", "Sentiment-Analysis-IMDb-Reviews")
    svc = MLService(template)
    tools = MLTools(SimpleNamespace(bus=EventBus(), ml=svc), data, None)
    return tools, svc, template, built, cloned


def test_lists_built_cloned_and_open_projects(tmp_path, monkeypatch):
    tools, svc, template, built, cloned = _setup(tmp_path, monkeypatch)
    cards = {c["name"]: c for c in tools.projects()}
    assert set(cards) == {"template", "hf-imdb-1117", "Sentiment-Analysis-IMDb-Reviews"}

    assert cards["template"]["open"] and cards["template"]["kind"] == "example"
    assert cards["template"]["best_metric"] == 0.71 and cards["template"]["runs"] == 1

    new = cards["hf-imdb-1117"]
    assert new["kind"] == "new" and new["title"] == "DistilBERT fine-tuned on imdb"
    assert new["dataset"] == "stanfordnlp/imdb" and new["base_model"] == "distilbert-base-uncased"
    assert new["status"] == "ready" and new["best_metric"] == 0.874 and not new["open"]

    clone = cards["Sentiment-Analysis-IMDb-Reviews"]
    assert clone["kind"] == "github" and clone["status"] == "not set up" and clone["runs"] == 0
    assert clone["title"] == "Sentiment Analysis IMDb Reviews" and clone["best_metric"] is None


def test_each_folder_once_and_the_open_one_first(tmp_path, monkeypatch):
    tools, svc, template, built, cloned = _setup(tmp_path, monkeypatch)
    tools.remember(built)  # recent: the built project, which is also under ml/new
    names = [c["name"] for c in tools.projects()]
    assert names[0] == "template" and names.count("hf-imdb-1117") == 1


def test_route(tmp_path, monkeypatch):
    tools, *_ = _setup(tmp_path, monkeypatch)
    app = FastAPI()
    app.include_router(router)
    client = TestClient(app)
    assert client.get("/ml/projects").status_code == 503  # no ML tools on this app yet
    app.state.ml_tools = tools
    body = client.get("/ml/projects").json()
    assert len(body["projects"]) == 3 and {c["kind"] for c in body["projects"]} == {"example", "new", "github"}
