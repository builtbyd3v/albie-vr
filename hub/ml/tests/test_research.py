"""ml_search_models / ml_search_datasets: Hub cards with fit checks, streamed as ml.research events. No network:
hf_data.get_json is replaced with rows shaped like the live API's answers (checked 2026-11-17)."""
from __future__ import annotations

import asyncio
from types import SimpleNamespace

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from events import EventBus
from ml import hf_data, research
from ml.router import router
from ml.service import MLService
from ml.tools import TOOLS, MLTools

ROWS = [
    {"id": "distilbert/distilbert-base-uncased", "pipeline_tag": "fill-mask", "library_name": "transformers",
     "safetensors": {"total": 66985530}, "cardData": {"license": "apache-2.0"}, "config": {"model_type": "distilbert"},
     "downloads": 10_000_000, "likes": 700, "lastModified": "2024-05-06T00:00:00.000Z", "gated": False},
    {"id": "big/xlm-roberta-large-sentiment", "pipeline_tag": "text-classification", "library_name": "transformers",
     "safetensors": {"total": 560_000_000}, "cardData": {"license": "mit"}, "config": {"model_type": "xlm-roberta"},
     "downloads": 50_000, "likes": 3, "lastModified": "2025-01-01T00:00:00.000Z", "gated": False},
    {"id": "nc/roberta-sentiment", "pipeline_tag": "text-classification", "library_name": "transformers",
     "cardData": {"license": "cc-by-nc-4.0"}, "config": {"model_type": "roberta"}, "downloads": 900_000,
     "gated": False},
    {"id": "openai-community/gpt2", "pipeline_tag": "text-generation", "library_name": "transformers",
     "safetensors": {"total": 137_000_000}, "cardData": {"license": "mit"}, "config": {"model_type": "gpt2"},
     "downloads": 20_000_000, "gated": False},
    {"id": "locked/bert-gated", "pipeline_tag": "text-classification", "config": {"model_type": "bert"},
     "safetensors": {"total": 110_000_000}, "cardData": {"license": "apache-2.0"}, "gated": "manual"},
]
FILES = {
    "distilbert/distilbert-base-uncased": [("config.json", 483), ("model.safetensors", 267_954_768),
                                           ("tokenizer.json", 466_062), ("vocab.txt", 231_508)],
    "big/xlm-roberta-large-sentiment": [("config.json", 600), ("model.safetensors", 2_240_000_000),
                                        ("sentencepiece.bpe.model", 5_000_000)],
    "nc/roberta-sentiment": [("config.json", 500), ("pytorch_model.bin", 500_000_000)],  # no tokenizer
}


@pytest.fixture()
def hub(monkeypatch):
    calls: list[str] = []

    def fake(url: str, tries: int = 4):
        calls.append(url)
        if url.startswith(research.MODEL_API + "/"):
            model = url[len(research.MODEL_API) + 1:].split("?")[0]
            return {"id": model, "siblings": [{"rfilename": n, "size": s} for n, s in FILES[model]]}
        if url.startswith(research.MODEL_API + "?"):
            return ROWS if "search=sentiment" in url else []
        if url.startswith(research.DATASET_API + "?"):
            return [{"id": "stanfordnlp/imdb", "downloads": 100_000, "likes": 300, "gated": False,
                     "cardData": {"license": "other"},
                     "tags": ["task_categories:text-classification", "size_categories:10K<n<100K"]},
                    {"id": "someone/movie-qa", "gated": False, "cardData": {"license": "mit"},
                     "tags": ["task_categories:question-answering", "size_categories:n<1K"]},
                    {"id": "locked/reviews", "gated": "auto", "tags": ["task_categories:text-classification"]}]
        raise AssertionError(url)

    monkeypatch.setattr(hf_data, "get_json", fake)
    return calls


def checks(card):
    return {c["name"]: c["level"] for c in card["checks"]}


def test_cards_carry_the_hubs_facts_and_honest_checks(hub):
    phases: list[str] = []
    r = research.find_models("sentiment", emit=lambda phase, body: phases.append(phase))
    cards = {c["id"]: c for c in r["cards"]}
    assert phases[0] == "start" and phases[-1] == "done" and phases.count("card") == 5 and "update" in phases
    distil = cards["distilbert/distilbert-base-uncased"]
    assert distil["params"] == 66985530 and distil["license"] == "apache-2.0" and distil["downloads"] == 10_000_000
    assert distil["weights_mb"] == 268.0 and distil["trainable"] and distil["on_device_latency"] is None
    assert checks(distil) == {"task": "good", "trainer": "good", "size": "good", "licence": "good", "download": "good"}
    assert "estimate" in next(c["text"] for c in distil["checks"] if c["name"] == "size")
    assert checks(cards["big/xlm-roberta-large-sentiment"])["size"] == "bad"  # 560M: too slow on the CPU trainer
    nc = cards["nc/roberta-sentiment"]
    assert checks(nc)["licence"] == "bad" and checks(nc)["size"] == "unknown"  # no parameter count: not guessed
    assert nc["status"] == "browse_only" and "tokenizer" in next(c["text"] for c in nc["checks"] if c["name"] == "trainer")
    gpt = cards["openai-community/gpt2"]
    assert gpt["status"] == "browse_only" and checks(gpt)["task"] == "bad" and gpt["details"] == "skipped"
    assert cards["locked/bert-gated"]["status"] == "browse_only"
    assert r["recommended"]["id"] == "distilbert/distilbert-base-uncased" and r["recommended"]["reasons"]
    assert r["sample"] is False and "not measured" in r["on_device_latency"]
    assert not any("gpt2?" in u or "openai-community/gpt2?" in u for u in hub)  # browse-only cards: no detail fetch


def test_image_models_are_browse_only_and_nothing_is_recommended(monkeypatch):
    rows = [{"id": "google/vit-base-patch16-224", "pipeline_tag": "image-classification", "library_name": "transformers",
             "safetensors": {"total": 86567656}, "cardData": {"license": "apache-2.0"}, "config": {"model_type": "vit"}}]
    seen = []
    monkeypatch.setattr(hf_data, "get_json", lambda url, tries=4: seen.append(url) or rows)
    r = research.find_models("vit", task="image_classification")
    assert "pipeline_tag=image-classification" in seen[0]
    card = r["cards"][0]
    assert card["status"] == "browse_only" and "CNN" in next(c["text"] for c in card["checks"] if c["name"] == "trainer")
    assert card["est_epoch_s"] is None and checks(card)["size"] == "unknown"  # no training estimate for it
    assert r["recommended"] is None and "browse only" in r["recommended_why"]


def test_the_task_is_inferred_from_the_words():
    assert research.hub_task("photos of plant leaves", None) == ("image-classification", True)
    assert research.hub_task("tweet sentiment", None) == ("text-classification", True)
    assert research.hub_task("x", "object_detection") == ("object-detection", False)


def test_a_hub_failure_comes_back_as_the_reason(monkeypatch):
    def down(url, tries=4):
        raise hf_data.DatasetError("Hugging Face didn't answer (timed out)")

    monkeypatch.setattr(hf_data, "get_json", down)
    r = research.find_models("sentiment")
    assert r["cards"] == [] and "didn't answer" in r["error"] and r["recommended"] is None


def test_dataset_cards(hub):
    r = research.find_datasets("movie reviews", task="text_classification")
    cards = {c["id"]: c for c in r["cards"]}
    assert cards["stanfordnlp/imdb"]["status"] == "usable" and checks(cards["stanfordnlp/imdb"])["licence"] == "warn"
    assert checks(cards["someone/movie-qa"]) == {"task": "bad", "size": "warn", "licence": "good", "access": "good"}
    assert cards["locked/reviews"]["status"] == "browse_only"
    assert r["recommended"]["id"] == "stanfordnlp/imdb"
    assert any("filter=task_categories%3Atext-classification" in u for u in hub)


def test_the_tool_streams_ml_research_events_and_answers_short(hub, tmp_path):
    assert {"ml_search_models", "ml_search_datasets"} <= {t["name"] for t in TOOLS}
    events: list = []
    bus = EventBus()

    async def record(type_, payload):
        events.append((type_, payload))

    bus.publish = record
    tools = MLTools(SimpleNamespace(bus=bus, ml=MLService(tmp_path)), tmp_path / "data", None)
    out = asyncio.run(tools.call("ml_search_models", {"query": "sentiment"}))
    assert out["recommended"]["id"] == "distilbert/distilbert-base-uncased" and "ml_new" in out["next"]
    assert all(isinstance(c, str) for c in out["cards"][0]["checks"])
    phases = [p["phase"] for t, p in events if t == "ml.research"]
    assert phases[0] == "start" and phases[-1] == "done" and phases.count("card") == 5
    assert len({p["search_id"] for _, p in events}) == 1
    assert "error" in asyncio.run(tools.call("ml_search_models", {"query": "  "}))


def test_routes_search_and_use_this_model(hub, tmp_path):
    app = FastAPI()
    app.include_router(router)
    called = []

    class Tools:
        async def call(self, name, args):
            called.append((name, args))
            return {"status": "building"}

    app.state.bus, app.state.ml_tools = EventBus(), Tools()
    client = TestClient(app)
    r = client.post("/ml/research/models", json={"query": "sentiment", "limit": 3}).json()
    assert len(r["cards"]) == 3 and r["search_id"]
    assert client.post("/ml/research/models", json={"query": ""}).status_code == 400
    assert client.post("/ml/research/use", json={"base_model": "distilbert/distilbert-base-uncased",
                                                 "request": "sentiment"}).json() == {"status": "building"}
    assert called == [("ml_new", {"request": "fine-tune distilbert/distilbert-base-uncased on sentiment",
                                  "model": "hf", "base_model": "distilbert/distilbert-base-uncased"})]
    client.post("/ml/publish", json={})
    assert called[-1] == ("ml_publish", {"confirm": False})  # the card's first call never posts
