"""ml_new: "start an RNN project on movie reviews" -> a Hugging Face dataset, a project from the reviewed templates,
the normal setup (no LLM), and a real training run whose score Albie says.

Offline: a fake Hugging Face (hf_data.get_json / get_bytes) serves a made-up review set (two classes with their own
words) and a made-up image set (three colours). Training runs the template's own train.py on this python.
"""

from __future__ import annotations

import asyncio
import io
import json
import re
import sys
import time
import urllib.parse
from types import SimpleNamespace

import pytest

from events import EventBus
from ml import hf_data, logtail, new_project
from ml import prescan as prescan_mod
from ml.service import MLService
from ml.tools import MLTools

GOOD = ["great", "wonderful", "loved", "brilliant", "moving", "fun"]
BAD = ["awful", "boring", "hated", "dull", "messy", "slow"]
COLOURS = [(220, 40, 40), (40, 200, 60), (50, 70, 230)]


def text_info(name: str) -> dict:
    return {"dataset_info": {"plain_text": {
        "features": {"text": {"dtype": "string", "_type": "Value"}, "label": {"names": ["neg", "pos"], "_type": "ClassLabel"}},
        "splits": {"train": {"num_examples": 2000}, "test": {"num_examples": 1000}}}}}


def image_info(name: str) -> dict:
    return {"dataset_info": {name: {
        "features": {"image": {"_type": "Image"}, "label": {"names": ["red", "green", "blue"], "_type": "ClassLabel"}},
        "splits": {"train": {"num_examples": 1500}, "test": {"num_examples": 600}}}}}


def text_row(i: int) -> dict:
    words = GOOD if i % 2 else BAD
    return {"text": f"The film was {words[i % 6]} and {words[(i + 2) % 6]}, really {words[(i + 3) % 6]}. Number {i}.",
            "label": i % 2}


def fake_get_json(url: str, tries: int = 3):
    parsed = urllib.parse.urlparse(url)
    q = dict(urllib.parse.parse_qsl(parsed.query))
    name = q.get("dataset", "")
    images = any(w in name for w in ("mnist", "shapes"))
    if parsed.netloc == "huggingface.co" and parsed.path == "/api/datasets":
        return [{"id": "fake/reviews"}] if "review" in q.get("search", "") else []
    if parsed.netloc == "huggingface.co":
        if "nolabels" in parsed.path:
            return {"cardData": {"license": "cc0-1.0"}}
        return {"cardData": {"license": "mit"}, "tags": []}
    if parsed.path == "/info":
        if "nolabels" in name:
            return {"dataset_info": {"default": {"features": {"text": {"dtype": "string", "_type": "Value"}},
                                                 "splits": {"train": {"num_examples": 10}}}}}
        return image_info(q.get("config") or name.split("/")[-1]) if images else text_info(name)
    if parsed.path == "/rows":
        CALLS.append("rows")
        offset, length = int(q["offset"]), int(q["length"])
        rows = []
        for i in range(offset, offset + length):
            if images:
                rows.append({"row": {"image": {"src": f"https://fake.assets/{q['split']}/{i}/image.png"}, "label": i % 3}})
            else:
                rows.append({"row": text_row(i)})
        return {"rows": rows}
    if parsed.path == "/parquet":
        CALLS.append("parquet")
        if PARQUET_DOWN:
            raise hf_data.DatasetError("no parquet here")
        config = name.split("/")[-1] if images else "plain_text"
        splits = {"train": 1500, "test": 600} if images else {"train": 2000, "test": 1000}
        return {"parquet_files": [{"dataset": name, "config": config, "split": s, "filename": "0000.parquet", "size": 1000,
                                   "url": f"https://fake.parquet/{'images' if images else 'text'}/{s}/{n}.parquet"}
                                  for s, n in splits.items()]}
    raise AssertionError(f"unexpected url {url}")


CALLS: list[str] = []
PARQUET_DOWN = False


def png(i: int) -> bytes:
    from PIL import Image

    image = Image.new("RGB", (28, 28), COLOURS[i % 3])
    image.putpixel((i % 28, (i // 28) % 28), (255, 255, 255))
    out = io.BytesIO()
    image.save(out, format="PNG")
    return out.getvalue()


def parquet_bytes(kind: str, n: int) -> bytes:
    """A split sorted by label, like IMDB's: the sample has to spread over it to see every class."""
    import pyarrow as pa
    import pyarrow.parquet as pq

    order = sorted(range(n), key=lambda i: (i % (3 if kind == "images" else 2), i))
    if kind == "images":
        table = pa.table({"image": [{"bytes": png(i), "path": None} for i in order], "label": [i % 3 for i in order]})
    else:
        table = pa.table({"text": [text_row(i)["text"] for i in order], "label": [i % 2 for i in order]})
    out = io.BytesIO()
    pq.write_table(table, out, row_group_size=100)
    return out.getvalue()


def fake_get_bytes(url: str) -> bytes:
    CALLS.append("bytes")
    m = re.match(r"https://fake.parquet/(\w+)/(\w+)/(\d+).parquet", url)
    if m:
        return parquet_bytes(m.group(1), int(m.group(3)))
    return png(int(re.search(r"/(\d+)/image", url).group(1)))


@pytest.fixture()
def fake_hf(monkeypatch, tmp_path):
    global PARQUET_DOWN
    PARQUET_DOWN = False
    CALLS.clear()
    monkeypatch.setattr(hf_data, "get_json", fake_get_json)
    monkeypatch.setattr(hf_data, "get_bytes", fake_get_bytes)
    monkeypatch.setattr(new_project, "ROWS", {"text_classification": (300, 80), "image_classification": (150, 60)})
    monkeypatch.setenv("FORGE_HF_CACHE", str(tmp_path / "hf-cache"))


# --- words ---------------------------------------------------------------------------------------------------

@pytest.mark.parametrize("said, kind", [("an RNN", "rnn"), ("LSTMs", "lstm"), ("a convolutional network", "cnn"),
                                         ("vision transformer", "transformer"), ("an lstm neural network", "lstm"),
                                         ("logistic regression", None), ("", None)])
def test_model_words(said, kind):
    assert new_project.model_kind(said) == kind


@pytest.mark.parametrize("said, topic", [("start an RNN project on movie reviews", "movie reviews"),
                                          ("make a cnn", ""), ("Albie, build me a CNN for handwritten digits", "handwritten digits"),
                                          ("train a transformer on stanfordnlp/imdb", "stanfordnlp/imdb")])
def test_topic_keeps_only_what_the_data_is_about(said, topic):
    assert new_project.topic(said) == topic


# --- plan ------------------------------------------------------------------------------------------------------

def test_plan_reads_the_dataset_layout_without_downloading_rows(fake_hf):
    p = new_project.plan(model="rnn", request="start an RNN project on movie reviews")
    assert (p["task"], p["model"], p["dataset"], p["classes"]) == ("text_classification", "rnn", "stanfordnlp/imdb", ["neg", "pos"])
    assert p["layout"]["input"] == "text" and p["layout"]["val_split"] == "test" and p["license"] == "mit"
    said = new_project.describe(p)
    assert said["model"] == "RNN" and said["dataset"] == "stanfordnlp/imdb"


def test_a_cnn_with_no_data_named_is_an_image_classifier_on_the_default(fake_hf):
    p = new_project.plan(model="cnn", request="make a cnn")
    assert (p["task"], p["dataset"], p["classes"]) == ("image_classification", "zalando-datasets/fashion_mnist", ["red", "green", "blue"])


def test_an_unknown_phrase_is_searched_on_the_hub(fake_hf):
    assert new_project.plan(request="a gru on some product review data")["dataset"] == "fake/reviews"


def test_refusals_say_why(fake_hf):
    with pytest.raises(new_project.NewProjectError, match="template"):
        new_project.plan(model="svm", request="an svm")
    with pytest.raises(hf_data.DatasetError, match="no label column"):
        new_project.plan(dataset="someone/nolabels", request="")
    with pytest.raises(new_project.NewProjectError, match="text or image"):
        new_project.plan(task="object_detection", request="")


# --- build, open, train ------------------------------------------------------------------------------------------

async def _until(cond, said, timeout=120.0):
    end = time.monotonic() + timeout
    while not cond():
        assert time.monotonic() < end, said
        await asyncio.sleep(0.1)


def _run_tool(tmp_path, monkeypatch, args: dict) -> tuple[dict, list[str], list[tuple[str, dict]], MLService]:
    pytest.importorskip("torch")  # the template's train.py runs on this python
    monkeypatch.setattr(logtail, "POLL_S", 0.05)
    monkeypatch.setattr(prescan_mod, "find_python", lambda repo: sys.executable)
    home = tmp_path / "home"
    home.mkdir()
    said: list[str] = []
    events: list[tuple[str, dict]] = []

    async def speak(pet_id, text):
        said.append(text)
        return True

    async def flow():
        bus = EventBus()
        publish = bus.publish

        async def record(type_, payload):
            events.append((type_, payload if isinstance(payload, dict) else payload.model_dump()))
            await publish(type_, payload)

        bus.publish = record
        svc = MLService(home)
        tools = MLTools(SimpleNamespace(bus=bus, ml=svc), tmp_path / "data", speak)
        result = await tools.call("ml_new", args)
        assert result.get("status") == "building", result
        again = await tools.call("ml_new", args)
        assert "still building" in again["error"]  # one at a time
        await _until(lambda: any(s.startswith(("Done!", "I couldn", "The project", "That run")) for s in said), said)
        return result, svc

    result, svc = asyncio.run(flow())
    return result, said, events, svc


def test_ml_new_builds_opens_and_trains_an_rnn_on_movie_reviews(tmp_path, monkeypatch, fake_hf):
    result, said, events, svc = _run_tool(tmp_path, monkeypatch, {
        "request": "start an RNN project on movie reviews", "model": "rnn", "epochs": 2})
    assert result["model"] == "RNN" and result["dataset"] == "stanfordnlp/imdb" and result["classes"] == ["neg", "pos"]
    assert said[0] == "Got 300 texts. Training the RNN now; watch the curves.", said
    assert said[-1].startswith("Done!"), said

    repo = svc.repo
    assert repo.parent == tmp_path / "data" / "ml" / "new" and repo.name.startswith("rnn-imdb-")
    project = json.loads((repo / "project.json").read_text())
    assert project["model"] == "rnn" and project["rows"] == {"train": 300, "val": 80}
    assert min(project["train_counts"]) > 100  # both classes, though pages are spread across the split
    assert {p.name for p in repo.iterdir()} >= {"train.py", "model.py", "data.py", "README.md", "project.json", ".forge"}

    layouts = [p for t, p in events if t == "ml.layout"]
    assert layouts and layouts[-1]["archetype"] == "text_classification" and layouts[-1]["open"] is True
    assert svc.project["status"] == "ready"
    run = svc.latest_run("results.json")
    assert run is not None and "best_val_acc" in json.loads((run / "summary.json").read_text())
    assert json.loads((tmp_path / "data" / "ml" / "recent.json").read_text())[0] == str(repo)


def test_ml_new_builds_and_trains_a_cnn_on_images(tmp_path, monkeypatch, fake_hf):
    result, said, events, svc = _run_tool(tmp_path, monkeypatch, {"request": "build a CNN", "model": "cnn", "epochs": 2})
    assert result["model"] == "CNN" and result["dataset"] == "zalando-datasets/fashion_mnist"
    assert said[0] == "Got 150 images. Training the CNN now; watch the curves.", said
    assert said[-1].startswith("Done!"), said
    repo = svc.repo
    assert len(list((repo / "data" / "images").iterdir())) == 210
    assert [p for t, p in events if t == "ml.layout"][-1]["archetype"] == "image_classification"
    assert svc.latest_run("results.json") is not None


@pytest.mark.parametrize("kind", ["lstm", "gru", "transformer", "cnn", "mlp"])
def test_every_text_model_trains_a_step(tmp_path, kind, fake_hf):
    _every_model_trains(tmp_path, kind, "movie reviews")


@pytest.mark.parametrize("kind", ["mlp", "rnn", "lstm", "gru", "transformer"])
def test_every_image_model_trains_a_step(tmp_path, kind, fake_hf):
    _every_model_trains(tmp_path, kind, "mnist digits")


def _every_model_trains(tmp_path, kind: str, words: str) -> None:
    """Each model the tool offers: build the project, run the real forge.check, train one epoch offline."""
    pytest.importorskip("torch")
    import os
    import subprocess

    from ml.service import FORGE_SDK

    p = new_project.plan(model=kind, request=f"a {kind} on {words}")
    repo = new_project.build(p, tmp_path / "new", epochs=1)
    env = {**os.environ, "FORGE_OFFLINE": "1", "PYTHONPATH": str(FORGE_SDK)}
    check = subprocess.run([sys.executable, "-m", "forge.check", str(repo)], cwd=repo, env=env,
                           capture_output=True, text=True, timeout=300)
    assert check.returncode == 0, check.stdout[-2000:] + check.stderr[-2000:]
    done = subprocess.run([sys.executable, "train.py", "--epochs", "1"], cwd=repo, env=env,
                          capture_output=True, text=True, timeout=300)
    assert done.returncode == 0, done.stderr[-2000:]
    assert "best val acc" in done.stdout


def test_old_projects_are_pruned_but_the_new_one_kept(tmp_path, fake_hf):
    p = new_project.plan(model="mlp", request="an mlp on movie reviews")
    built = [new_project.build(p, tmp_path / "new", epochs=1) for _ in range(new_project.KEEP_PROJECTS + 2)]
    left = sorted(x for x in (tmp_path / "new").iterdir())
    assert len(left) == new_project.KEEP_PROJECTS and built[-1] in left


def test_the_headset_lists_the_cloud_ml_tools_exactly_as_the_hub_defines_them():
    """Core/Resources/AlbieTools/cloud.json is what the headset's voice model sees; a stale copy would send
    'start an RNN project' with old arguments, or not at all."""
    from ml.service import ROOT
    from ml.tools import TOOLS

    cloud = json.loads((ROOT / "unity" / "Assets" / "_Forge" / "Core" / "Resources" / "AlbieTools" / "cloud.json").read_text())
    listed = {t["name"]: t for t in cloud if t["name"].startswith("ml_")}
    hub = {t["name"]: t for t in TOOLS if t["name"] != "ml_github"}  # the cloud never clones repos
    assert listed == hub


# --- fetching: Parquet first, the rows API as fallback, ranges for big files, a shared cache ------------------

def _fetch(tmp_path, request: str, model: str, name: str = "p") -> dict:
    p = new_project.plan(model=model, request=request)
    return json.loads((new_project.build(p, tmp_path / name, epochs=1) / "project.json").read_text())


def test_parquet_is_read_whole_and_the_rows_api_isnt_called(tmp_path, fake_hf):
    pytest.importorskip("pyarrow")
    project = _fetch(tmp_path, "an lstm on movie reviews", "lstm")
    assert "rows" not in CALLS and CALLS.count("parquet") == 2  # train and test
    assert project["train_counts"] == [150, 150]  # the split is sorted by label; the sample still has both


def test_images_come_with_their_bytes_from_parquet(tmp_path, fake_hf):
    pytest.importorskip("pyarrow")
    project = _fetch(tmp_path, "a cnn", "cnn")
    assert "rows" not in CALLS and project["train_counts"] == [50, 50, 50]
    files = sorted((tmp_path / "p").glob("*/data/images/*"))
    assert len(files) == 210 and files[0].suffix == ".png"


def test_without_parquet_the_rows_api_still_builds_it(tmp_path, fake_hf):
    global PARQUET_DOWN
    PARQUET_DOWN = True
    project = _fetch(tmp_path, "an lstm on movie reviews", "lstm")
    assert "rows" in CALLS and project["rows"] == {"train": 300, "val": 80}
    assert min(project["train_counts"]) >= 140


def test_a_big_parquet_file_is_read_by_range_only_where_the_sample_is(tmp_path, fake_hf, monkeypatch):
    pytest.importorskip("pyarrow")
    monkeypatch.setattr(hf_data, "WHOLE_PARQUET_BYTES", 0)
    reads: list[int] = []

    class Answer:
        def __init__(self, url, total, start, end):
            self.url, self.headers = url, {"Content-Range": f"bytes {start}-{end}/{total}"}

        def geturl(self):
            return self.url

    def ranged(url, headers=None, tries=4):
        body = fake_get_bytes(url)
        start, end = map(int, headers["Range"].removeprefix("bytes=").split("-"))
        reads.append(end - start + 1)
        return body[start:end + 1], Answer(url, len(body), start, end)

    monkeypatch.setattr(hf_data, "_open", ranged)
    project = _fetch(tmp_path, "a cnn", "cnn")
    assert reads and project["train_counts"] == [50, 50, 50]  # evenly spaced row groups reach every class


def test_the_same_sample_comes_from_the_cache_the_second_time(tmp_path, fake_hf):
    pytest.importorskip("pyarrow")
    first = _fetch(tmp_path, "an lstm on movie reviews", "lstm", "a")
    CALLS.clear()
    second = _fetch(tmp_path, "a gru on movie reviews", "gru", "b")  # another model, the same data
    assert "bytes" not in CALLS and "rows" not in CALLS and "parquet" not in CALLS
    assert first["train_counts"] == second["train_counts"] and second["model"] == "gru"
    a, b = (next((tmp_path / d).iterdir()) / "data" / "train.jsonl" for d in ("a", "b"))
    assert a.read_text() == b.read_text()


def test_balanced_takes_classes_in_turn_and_lets_others_fill_in():
    rows = [{"label": 0}] * 90 + [{"label": 1}] * 10
    out = hf_data.balanced(rows, "label", 40, seed=1)
    assert len(out) == 40 and sum(r["label"] == 1 for r in out) == 10  # all of the rare class, the rest from 0
    assert len(hf_data.balanced(rows, "label", 500, seed=1)) == 100


@pytest.mark.parametrize("template", ["text", "image"])
@pytest.mark.parametrize("gap, changes, epochs", [(0.2, 3, 8), (0.01, 2, 16)])
def test_the_clouds_free_improvement_patches_a_built_project(tmp_path, template, gap, changes, epochs):
    """cloud-py/text_improve.py, the 'what can I do better?' with no Claude: regularize an overfitting project,
    train a close one longer. Either patch passes the same guardrails as Claude's (it parses, keeps the forge lines)."""
    import importlib.util
    import shutil

    from ml import improve
    from ml.service import ROOT

    spec = importlib.util.spec_from_file_location("text_improve", ROOT / "cloud-py" / "text_improve.py")
    text_improve = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(text_improve)
    repo = tmp_path / "repo"
    shutil.copytree(new_project.TEMPLATES / template, repo)
    (repo / "project.json").write_text(json.dumps({"model": "lstm", "task": f"{template}_classification",
                                                   "rows": {"train": 4000}}))
    d = {"train_val_gap": gap, "train_acc": 0.9, "val_acc": 0.9 - gap, "best_val_acc": 0.9 - gap, "epochs": 8}
    plan = text_improve.plan(d, repo)
    assert len(plan["changes"]) == changes and plan["epochs"] == epochs
    assert "LSTM" in plan["diagnosis"] and "headlines" not in plan["diagnosis"]
    (rel, _, content), = improve.check_proposal(repo, plan)
    assert rel == "train.py" and "CosineAnnealingLR" in content and "sched.step()" in content
