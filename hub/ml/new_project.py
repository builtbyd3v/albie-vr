"""A new ML project from a sentence ("start an RNN project on movie reviews"): ml_new's plan and build.

`plan` picks the model and the Hugging Face dataset and reads the dataset's layout (no rows downloaded). `build`
fetches a sample of the dataset and writes a small, complete project from a reviewed template
(hub/ml/templates/<text|image>): train.py, model.py, data.py, project.json, a README, and a hand-written
.forge/ (profile + adapter), so the normal setup opens it with no LLM call and the ML desk can train it at once.

Albie never runs code it wrote: every model is one of the template's, picked by project.json's "model".
"""

from __future__ import annotations

import json
import os
import re
import shutil
import time
from collections.abc import Callable
from pathlib import Path
from typing import Any

from . import hf_data

TEMPLATES = Path(__file__).resolve().parent / "templates"
TASKS = ("text_classification", "image_classification")
MODELS = ("rnn", "lstm", "gru", "transformer", "cnn", "mlp", "hf")
MODEL_WORDS = {  # what people call them -> the template's kind
    "rnn": "rnn", "recurrent": "rnn", "vanilla rnn": "rnn", "lstm": "lstm", "long short-term memory": "lstm",
    "gru": "gru", "transformer": "transformer", "attention": "transformer", "bert": "transformer", "vit": "transformer",
    "vision transformer": "transformer", "cnn": "cnn", "convolutional": "cnn", "convnet": "cnn", "conv": "cnn",
    "resnet": "cnn", "mlp": "mlp", "dense": "mlp", "feedforward": "mlp", "feed-forward": "mlp",
    "fully connected": "mlp", "perceptron": "mlp", "bag of words": "mlp",
    # fine-tuning a pretrained encoder from the Hub (the `pretrained` template)
    "pretrained": "hf", "pre-trained": "hf", "finetune": "hf", "fine-tune": "hf", "fine tune": "hf",
    "finetuned": "hf", "fine-tuned": "hf", "fine tuned": "hf", "distilbert": "hf", "distil bert": "hf",
    "roberta": "hf", "albert": "hf", "electra": "hf", "deberta": "hf", "minilm": "hf", "mini lm": "hf",
    "xtremedistil": "hf", "extreme distil": "hf", "small bert": "hf", "bert tiny": "hf", "tiny bert": "hf",
}
MODEL_NAMES = {"rnn": "RNN", "lstm": "LSTM", "gru": "GRU", "transformer": "Transformer", "cnn": "CNN", "mlp": "MLP",
               "hf": "pretrained"}
PRETTY_MODELS = {  # the base model's name as Albie says it
    "microsoft/xtremedistil-l6-h256-uncased": "XTREMEDistil", "distilbert-base-uncased": "DistilBERT",
    "roberta-base": "RoBERTa", "albert-base-v2": "ALBERT", "google/electra-small-discriminator": "ELECTRA",
    "microsoft/deberta-v3-xsmall": "DeBERTa", "microsoft/MiniLM-L12-H384-uncased": "MiniLM",
    "google/bert_uncased_L-2_H-128_A-2": "BERT-Tiny", "bert-base-uncased": "BERT",
}
ROWS = {"text_classification": (4000, 500), "image_classification": (2000, 400)}
PRETRAINED_ROWS = (2000, 400)  # fine-tuning forwards and backwards through the encoder: fewer rows keep it a minute
PRETRAINED_EPOCHS = 3  # a pretrained encoder needs a few epochs, not the eight a model from scratch does
DEFAULT_MODEL = {"text_classification": "lstm", "image_classification": "cnn"}
DEFAULT_EPOCHS = 8
KEEP_PROJECTS = 4  # older projects Albie built in the same folder are deleted



class NewProjectError(Exception):
    """Why the project can't be built, in words Albie can say."""


def model_kind(said: str | None) -> str | None:
    """The template kind for what the user called the model; None if they didn't name one."""
    if not said:
        return None
    text = said.lower().strip()
    if text in MODELS:
        return text
    for words in sorted(MODEL_WORDS, key=len, reverse=True):
        if re.search(rf"\b{re.escape(words)}s?\b", text):
            return MODEL_WORDS[words]
    return None


def plan(model: str | None = None, dataset: str | None = None, task: str | None = None,
         request: str = "", base_model: str | None = None) -> dict[str, Any]:
    """What ml_new will build: {task, model, dataset, config, classes, license, rows, title, ...}.
    `base_model` fine-tunes that pretrained Hugging Face encoder (the `pretrained` template) instead of
    training a model from scratch. Raises NewProjectError (or hf_data.DatasetError) with a reason Albie can say."""
    if task is not None and task not in TASKS:
        raise NewProjectError(f"I can build text or image classifiers, not {task!r} yet")
    named = model_kind(model)
    kind = named or model_kind(request)
    if kind is None:
        try:
            hf_data.refuse_named_model(request)
        except hf_data.DatasetError:
            kind = "hf"  # a model named in the request that the pretrained template refuses, with the reason
    kind = kind or ("hf" if base_model else None)
    if base_model and kind not in (None, "hf"):
        raise NewProjectError(f"I can fine-tune a Hugging Face model or train a {MODEL_NAMES.get(kind, kind)} from "
                              "scratch, not both")
    if model and named is None and not re.search(r"auto|any|neural|network|model|classifier", model.lower()):
        raise NewProjectError(f"I don't have a {model} template; I can build an "
                              + ", ".join(MODEL_NAMES[m] for m in MODELS[:-1]) + " or fine-tune a pretrained model")
    words = topic(request)
    if kind == "hf":
        if task == "image_classification":
            raise NewProjectError("I can fine-tune a pretrained model on text only right now; images train from "
                                  "scratch with a CNN")
        asked = base_model or (model if model and ("/" in model or hf_data.model_alias(model)) else None)
        info = hf_data.find_model(request, asked)
        layout = hf_data.find(words, dataset, "text_classification")
        kind = "hf"
        n_train, n_val = PRETRAINED_ROWS
        n_train = min(n_train, layout.train_rows or n_train)
        n_val = min(n_val, layout.val_rows or n_val) if layout.val_split else n_val
        short = layout.dataset.split("/")[-1]
        pretty = PRETTY_MODELS.get(info.model) or hf_data.model_name(info.model)
        return {"task": "text_classification", "model": kind, "model_name": pretty, "base_model": info.model,
                "base_license": info.license, "dataset": layout.dataset, "config": layout.config,
                "classes": layout.classes, "license": layout.license, "rows": {"train": n_train, "val": n_val},
                "layout": layout.to_dict(), "title": f"{pretty} fine-tuned on {short}",
                "slug": slugify(f"hf-{short}"), "epochs": PRETRAINED_EPOCHS}
    if task is None and dataset is None and kind in ("cnn", "mlp") and not hf_data.alias(words):
        task = "image_classification"  # "a CNN" with no data named: images, the classic
    layout = hf_data.find(words, dataset, task)
    kind = kind or DEFAULT_MODEL[layout.task]
    n_train, n_val = ROWS[layout.task]
    n_train = min(n_train, layout.train_rows or n_train)
    n_val = min(n_val, layout.val_rows or n_val) if layout.val_split else n_val
    short = layout.dataset.split("/")[-1]
    what = "text" if layout.task == "text_classification" else "image"
    return {"task": layout.task, "model": kind, "model_name": MODEL_NAMES[kind], "dataset": layout.dataset,
            "config": layout.config, "classes": layout.classes, "license": layout.license,
            "rows": {"train": n_train, "val": n_val}, "layout": layout.to_dict(),
            "title": f"{MODEL_NAMES[kind]} {what} classifier on {short}",
            "slug": slugify(f"{kind}-{short}")}


def default_epochs(p: dict[str, Any]) -> int:
    """How many epochs the project should train for when the user didn't say."""
    return int(p.get("epochs") or DEFAULT_EPOCHS)


FILLER = set("""a an the me my us our for on of in with using use about to from that this some new please albie hey
can could would you i we let lets let's start make build create train run set up setup project projects model models
classifier classifiers classify classification network networks neural deep learning machine ml ai data dataset
datasets hugging face huggingface one quick small simple demo show and or it is be do get""".split())


def topic(request: str) -> str:
    """What the data is about, from the user's sentence: the words left after the model names and the filler
    ("start an RNN project on movie reviews" -> "movie reviews"). Empty when they named no data."""
    text = (request or "").lower()
    for words in sorted(MODEL_WORDS, key=len, reverse=True):
        text = re.sub(rf"\b{re.escape(words)}s?\b", " ", text)
    return " ".join(w for w in re.findall(r"[a-z0-9][a-z0-9'/_.-]*", text) if w not in FILLER)


def slugify(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")[:48] or "project"


def _folder(root: Path, slug: str) -> Path:
    root.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime("%m%d-%H%M%S")
    path = root / f"{slug}-{stamp}"
    n = 2
    while path.exists():
        path, n = root / f"{slug}-{stamp}-{n}", n + 1
    return path


def _prune(root: Path, keep: Path) -> None:
    built = sorted((p for p in root.iterdir() if p.is_dir() and (p / "project.json").exists() and p != keep),
                   key=lambda p: p.stat().st_mtime, reverse=True)
    for old in built[KEEP_PROJECTS - 1:]:
        shutil.rmtree(old, ignore_errors=True)


def build(p: dict[str, Any], root: Path, progress: Callable[[str], None] = lambda _: None,
          epochs: int = DEFAULT_EPOCHS) -> Path:
    """Fetch the data and write the project under root/<slug>-<time>/. Returns its folder.
    Nothing is written outside it except deleting this folder's oldest siblings that Albie built."""
    layout = hf_data.Layout(**p["layout"])
    dest = _folder(Path(root), p["slug"])
    template = TEMPLATES / ("pretrained" if p["model"] == "hf" else
                            "text" if p["task"] == "text_classification" else "image")
    shutil.copytree(template, dest, ignore=shutil.ignore_patterns("__pycache__"))
    shutil.copy2(TEMPLATES / "common" / "download_data.py", dest / "download_data.py")
    shutil.copy2(Path(hf_data.__file__), dest / "hf_data.py")  # so download_data.py works in a clone of the PR
    # Like ml_github: FORGE_ML_VENV (a venv with torch and transformers) becomes the project's .venv, so the project
    # trains with those packages instead of the hub's bare python. Not published: the PR's file list is explicit.
    venv = os.environ.get("FORGE_ML_VENV")
    if venv and not any((dest / v).exists() for v in (".venv", "venv", "env")):
        try:
            (dest / ".venv").symlink_to(Path(venv).expanduser().resolve())
        except OSError:
            pass  # a venv that can't be linked is not a reason to lose the project; the profile falls back to python
    try:
        got = hf_data.fetch(layout, dest, p["rows"]["train"], p["rows"]["val"], progress)
        classes = got["classes"]
        project = {"title": p["title"], "task": p["task"], "model": p["model"], "dataset": p["dataset"],
                   "config": p["config"], "license": p["license"], "classes": classes,
                   "columns": {"input": layout.input, "label": layout.label},
                   "splits": {"train": layout.train_split, "val": layout.val_split or f"{layout.train_split} (held out)"},
                   "rows": {"train": got["train"], "val": got["val"]}, "train_counts": got["counts"],
                   "created_at": time.time(), "notes": layout.notes,
                   "layout": layout.to_dict(), "sample": {**p["rows"], "seed": 7}}
        if p["model"] == "hf":
            project.update(base_model=p["base_model"], model_name=p["model_name"], base_license=p.get("base_license"),
                           max_len=128, lr=3e-5, batch_size=32)
        elif p["task"] == "text_classification":
            project.update(max_len=128, lr=2e-3)
        else:
            project.update(image_size=32, lr=1e-3)  # measured on Fashion-MNIST: 80% at 1e-3 vs 77.5% at 2e-3

        (dest / "project.json").write_text(json.dumps(project, indent=1, ensure_ascii=False))
        (dest / ".forge").mkdir(exist_ok=True)
        (dest / ".forge" / "profile.json").write_text(json.dumps(profile(project, epochs), indent=1, ensure_ascii=False))
        (dest / "README.md").write_text(readme(project, epochs))
    except BaseException:
        shutil.rmtree(dest, ignore_errors=True)
        raise
    _prune(Path(root), dest)
    return dest


def profile(project: dict[str, Any], epochs: int) -> dict[str, Any]:
    """The hand-written .forge/profile.json: setup finds it (with the template's adapter), forge.check passes,
    and the project is ready with no LLM call."""
    text = project["task"] == "text_classification"
    base = project.get("base_model")
    name = project.get("model_name") or MODEL_NAMES[project["model"]]
    summary = (f"{name} {'text' if text else 'image'} classifier, {len(project['classes'])} classes, on "
               f"{project['dataset']} from Hugging Face.")
    if base:
        summary += f" Fine-tuned from {base}."
    return {"schema": 1, "archetype": project["task"], "confidence": 1.0, "framework": "pytorch", "python": "python",
            "entry_points": {"train": "train.py", "model": "model.py:build", "data": "data.py:get_loaders"},
            "train_cmd": f"python train.py --epochs {epochs}", "classes": project["classes"], "target": None,
            "input_shape": [project["max_len"]] if text else [3, project["image_size"], project["image_size"]],
            "checkpoint": "checkpoints/best.pt", "existing_logs": [], "extra_viz": [], "base_model": base,
            "summary": summary,
            "notes": ("Built by Albie (ml_new) from hub/ml/templates/pretrained; model.py fine-tunes the Hugging Face "
                      f"model {base} with a new classification head." if base else
                      "Built by Albie (ml_new) from hub/ml/templates; model.py has every model, project.json picks one.")}


def readme(project: dict[str, Any], epochs: int) -> str:
    classes = ", ".join(project["classes"][:12]) + (" ..." if len(project["classes"]) > 12 else "")
    base = project.get("base_model")
    model_line = (f"- Model: {project.get('model_name') or MODEL_NAMES[project['model']]}, fine-tuned from "
                  f"[{base}](https://huggingface.co/{base}) (license: {project.get('base_license') or 'not stated'}) "
                  "with a new classification head (`model.py`)\n" if base else
                  f"- Model: {MODEL_NAMES[project['model']]} (`model.py`, picked by `project.json`)\n")
    return (f"# {project['title']}\n\n"
            f"Built by Albie from the Hugging Face dataset [{project['dataset']}](https://huggingface.co/datasets/"
            f"{project['dataset']}) (config `{project['config']}`, license: {project['license'] or 'not stated'}).\n\n"
            f"{model_line}"
            f"- Data: {project['rows']['train']} training and {project['rows']['val']} validation rows, sampled from "
            f"`{project['splits']['train']}` and `{project['splits']['val']}` (`data/`)\n"
            f"- Classes ({len(project['classes'])}): {classes}\n\n"
            f"Train it: `python train.py --epochs {epochs}` (CPU, about a minute to a few minutes).\n")


def describe(p: dict[str, Any]) -> dict[str, Any]:
    """What Albie says before it's built: the plan without the internals."""
    classes = p["classes"]
    out = {"building": p["title"], "model": p["model_name"], "dataset": p["dataset"],
           "classes": classes[:10] + (["..."] if len(classes) > 10 else []) if classes else "named from the data",
           "rows": p["rows"], "license": p["license"] or "not stated"}
    if p.get("base_model"):
        out["base_model"] = p["base_model"]
        out["base_license"] = p.get("base_license") or "not stated"
        out["finetune"] = True
    return out



# The datasets "start an RNN project", "a CNN on digits" and the like reach for (hf_data.ALIASES and DEFAULTS).
# `python -m ml.new_project warm` samples each one into the cache once (the cloud image does it at build time), so
# the demo's projects don't wait on, or get rate-limited by, Hugging Face.
WARM = ("stanfordnlp/imdb", "fancyzhx/ag_news", "dair-ai/emotion", "ucirvine/sms_spam", "cornell-movie-review-data/rotten_tomatoes",
        "ylecun/mnist", "zalando-datasets/fashion_mnist", "uoft-cs/cifar10", "AI-Lab-Makerere/beans")


def warm(datasets: tuple[str, ...] | list[str] = WARM) -> int:
    """Fill hf_data.cache_root() with each dataset's sample. Returns how many failed (each one is printed)."""
    import tempfile

    failed = 0
    for name in datasets:
        start = time.monotonic()
        try:
            p = plan(dataset=name)
            with tempfile.TemporaryDirectory() as tmp:
                path = build(p, Path(tmp), epochs=1)
                rows = json.loads((path / "project.json").read_text())["rows"]
            print(f"warm {name}: {rows['train']} + {rows['val']} rows in {time.monotonic() - start:.1f} s", flush=True)
        except Exception as e:  # noqa: BLE001 - one dataset failing must not stop the rest
            failed += 1
            print(f"warm {name}: FAILED {type(e).__name__}: {e}", flush=True)
    return failed


if __name__ == "__main__":
    import sys

    if sys.argv[1:2] != ["warm"]:
        sys.exit("usage: python -m ml.new_project warm [dataset ...]")
    print(f"cache: {hf_data.cache_root()}")
    sys.exit(1 if warm(sys.argv[2:] or WARM) else 0)
