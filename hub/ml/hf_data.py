"""Hugging Face datasets for Albie's new ML projects (ml_new): find one, read its layout, fetch a sample of it.

Only the Hub's public HTTP APIs through the standard library, plus pyarrow to read Parquet (optional: without it,
the rows API alone). HF_TOKEN, if set, signs the calls for higher rate limits.
  huggingface.co/api/datasets             search, and one dataset's card (license, gated, private)
  datasets-server.huggingface.co/info     configs, splits, row counts and column types (ClassLabel names)
  datasets-server.huggingface.co/parquet  the split's Parquet files on the Hub's CDN (images as bytes)
  datasets-server.huggingface.co/rows     fallback: up to 100 rows per call; an Image column comes as a signed URL

`inspect` reads the layout and downloads no rows. `fetch` reads a seeded spread of the split (row groups of
its Parquet export, else pages of the rows API), so a split sorted by label (IMDB's is) still gives every class,
and writes data/{train,val}.jsonl, plus data/images/ for an image dataset. Samples are cached on disk (FORGE_HF_CACHE).
"""

from __future__ import annotations

import hashlib
import io
import json
import logging
import os
import random
import re
import shutil
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from collections.abc import Callable
from concurrent.futures import ThreadPoolExecutor
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any

log = logging.getLogger(__name__)

HUB_API = "https://huggingface.co/api/datasets"
SERVER = "https://datasets-server.huggingface.co"
USER_AGENT = "albie-forge/1.0"
TIMEOUT_S = 20
PAGE = 100            # the rows API's maximum
MAX_CLASSES = 100
MAX_TEXT_CHARS = 2000
IMAGE_WORKERS = 16     # only for the rows API fallback; Parquet carries the image bytes
WHOLE_PARQUET_BYTES = 80_000_000  # read a split whole up to this; past it, only the row groups we need
MAX_PARQUET_FILES = 8
CACHE_VERSION = 2
MAX_IMAGE_PX = 64
DATASET_ID = re.compile(r"^[A-Za-z0-9][\w.-]*/[\w.-]+$|^[A-Za-z0-9][\w.-]*$")

TEXT_NAMES = ("text", "sentence", "review", "content", "document", "sms", "tweet", "comment", "question", "title")
LABEL_NAMES = ("label", "labels", "fine_label", "coarse_label", "target", "class", "category", "sentiment")
VAL_SPLITS = ("validation", "valid", "val", "test", "dev")

# What people say -> a dataset that's known to work. Checked in order; the first phrase found in the request wins.
ALIASES: list[tuple[tuple[str, ...], str]] = [
    (("imdb", "movie review", "movie", "film"), "stanfordnlp/imdb"),
    (("rotten tomatoes",), "cornell-movie-review-data/rotten_tomatoes"),
    (("sst",), "stanfordnlp/sst2"),
    (("emotion", "feeling", "mood"), "dair-ai/emotion"),
    (("news", "headline", "topic"), "fancyzhx/ag_news"),
    (("spam", "sms"), "ucirvine/sms_spam"),
    (("yelp", "restaurant", "star rating"), "Yelp/yelp_review_full"),
    (("tweet", "twitter"), "cardiffnlp/tweet_eval"),
    (("sentiment", "positive", "negative"), "stanfordnlp/imdb"),
    (("fashion", "cloth", "shoe"), "zalando-datasets/fashion_mnist"),
    (("mnist", "digit", "handwrit", "number"), "ylecun/mnist"),
    (("cifar100", "cifar-100", "cifar 100"), "uoft-cs/cifar100"),
    (("cifar", "object", "animal", "vehicle", "airplane"), "uoft-cs/cifar10"),
    (("bean", "leaf", "plant disease"), "AI-Lab-Makerere/beans"),
    (("cats vs dogs", "cat", "dog"), "microsoft/cats_vs_dogs"),
]
DEFAULTS = {"text_classification": "stanfordnlp/imdb", "image_classification": "zalando-datasets/fashion_mnist"}
TASK_FILTER = {"text_classification": "task_categories:text-classification",
               "image_classification": "task_categories:image-classification"}


class DatasetError(Exception):
    """Why a dataset can't be used, in words Albie can say."""


@dataclass
class Layout:
    dataset: str
    config: str
    task: str                       # text_classification | image_classification
    input: str                      # the text or image column
    label: str
    classes: list[str]
    train_split: str
    val_split: str | None           # None: hold out part of train
    train_rows: int
    val_rows: int
    license: str | None = None
    label_names_from_data: bool = False  # an int/str label with no ClassLabel names: named from the rows
    notes: list[str] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


# --- HTTP ---------------------------------------------------------------------------------------------------

def _headers(extra: dict[str, str] | None = None) -> dict[str, str]:
    headers = {"User-Agent": USER_AGENT, **(extra or {})}
    token = os.environ.get("HF_TOKEN")  # optional: a signed-in caller gets higher rate limits
    if token:
        headers["Authorization"] = f"Bearer {token}"
    return headers


def _backoff(e: Exception, attempt: int) -> None:
    """Wait before the next try: what a 429 asks for (Retry-After, at most 8 s), else a growing pause."""
    wait = 0.5 * 2 ** attempt
    if isinstance(e, urllib.error.HTTPError) and e.code == 429:
        try:
            wait = float(e.headers.get("Retry-After") or wait * 2)
        except ValueError:
            wait *= 2
    time.sleep(min(wait, 8.0))


def _open(url: str, headers: dict[str, str] | None = None, tries: int = 4) -> tuple[bytes, Any]:
    """GET with retries on 429, 5xx and network errors. Returns (body, response headers)."""
    last: Exception | None = None
    for attempt in range(tries):
        try:
            request = urllib.request.Request(url, headers=_headers(headers))
            with urllib.request.urlopen(request, timeout=TIMEOUT_S) as answer:
                return answer.read(), answer
        except urllib.error.HTTPError as e:
            if e.code in (401, 403, 404):
                raise DatasetError(_http_reason(e)) from e
            last = e
        except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
            last = e
        if attempt + 1 < tries:
            _backoff(last, attempt)
    if isinstance(last, urllib.error.HTTPError) and last.code == 429:
        raise DatasetError("Hugging Face is rate-limiting this server right now; try again in a minute, "
                           "or pick a dataset I've already downloaded")
    raise DatasetError(f"Hugging Face didn't answer ({last})")


def get_json(url: str, tries: int = 4) -> Any:
    body, _ = _open(url, tries=tries)
    try:
        return json.loads(body)
    except ValueError as e:
        raise DatasetError("Hugging Face sent something that wasn't JSON") from e


def _http_reason(e: urllib.error.HTTPError) -> str:
    try:
        detail = json.loads(e.read()).get("error", "")
    except Exception:  # noqa: BLE001 - the code alone is enough
        detail = ""
    if e.code == 404:
        return "Hugging Face has no such dataset, or it can't be read row by row"
    return f"that dataset isn't public ({detail or e.code})"


def get_bytes(url: str) -> bytes:
    return _open(url)[0]


class RangeFile(io.RawIOBase):
    """A remote file read with HTTP Range requests, so pyarrow reads a big Parquet file's footer and only the
    row groups it needs. The Hub redirects to its CDN once; the signed CDN URL is kept for the reads."""

    def __init__(self, url: str) -> None:
        super().__init__()
        body, answer = _open(url, {"Range": "bytes=0-0"})
        self.url = answer.geturl()
        total = (answer.headers.get("Content-Range") or "").rpartition("/")[2]
        if not total.isdigit():
            raise DatasetError("Hugging Face's file server doesn't do partial downloads here")
        self.size, self.pos, self.fetched = int(total), 0, 0

    def readable(self) -> bool:
        return True

    def seekable(self) -> bool:
        return True

    def tell(self) -> int:
        return self.pos

    def seek(self, offset: int, whence: int = io.SEEK_SET) -> int:
        self.pos = {io.SEEK_SET: 0, io.SEEK_CUR: self.pos, io.SEEK_END: self.size}[whence] + offset
        return self.pos

    def readinto(self, buffer) -> int:
        n = min(len(buffer), self.size - self.pos)
        if n <= 0:
            return 0
        body, _ = _open(self.url, {"Range": f"bytes={self.pos}-{self.pos + n - 1}"})
        buffer[:len(body)] = body
        self.pos += len(body)
        self.fetched += len(body)
        return len(body)


def _q(**params: Any) -> str:
    return urllib.parse.urlencode({k: v for k, v in params.items() if v is not None})


# --- find ---------------------------------------------------------------------------------------------------

def alias(words: str) -> str | None:
    said = " " + words.lower() + " "
    for phrases, dataset in ALIASES:
        if any(p in said for p in phrases):
            return dataset
    return None


def search(words: str, task: str | None = None, limit: int = 6) -> list[str]:
    """Dataset ids for what the user said, most downloaded first. The Hub matches ids, so each word is tried too."""
    tries = [words] + sorted({w for w in re.findall(r"[a-z0-9]{4,}", words.lower())} - {"data", "dataset"}, key=len, reverse=True)
    found: list[str] = []
    for query in tries:
        params = {"search": query, "sort": "downloads", "direction": -1, "limit": limit}
        if task in TASK_FILTER:
            params["filter"] = TASK_FILTER[task]
        try:
            rows = get_json(f"{HUB_API}?{_q(**params)}")
        except DatasetError:
            continue
        for row in rows:
            if not row.get("gated") and not row.get("private") and not row.get("disabled") and row["id"] not in found:
                found.append(row["id"])
        if len(found) >= limit:
            break
    return found[:limit]


def card(dataset: str) -> dict[str, Any]:
    """License and access from the dataset's Hub page; raises for a gated, private or disabled one."""
    data = get_json(f"{HUB_API}/{urllib.parse.quote(dataset, safe='/')}")
    if data.get("private") or data.get("gated") or data.get("disabled"):
        raise DatasetError(f"{dataset} needs a sign-in or an access request on Hugging Face")
    license_ = (data.get("cardData") or {}).get("license")
    if isinstance(license_, list):
        license_ = ", ".join(map(str, license_))
    if not license_:
        license_ = next((t.split(":", 1)[1] for t in data.get("tags", []) if t.startswith("license:")), None)
    return {"license": license_, "downloads": data.get("downloads")}


# --- layout -------------------------------------------------------------------------------------------------

def _pick_config(infos: dict[str, Any], dataset: str, words: str) -> str:
    names = list(infos)
    said = words.lower()
    for name in names:
        if name.lower() in said.split() or name.lower() in said:
            return name
    for preferred in ("default", "plain_text", dataset.split("/")[-1]):
        if preferred in infos:
            return preferred
    return names[0]


def _pick(features: dict[str, Any], names: tuple[str, ...], ok: Callable[[dict], bool]) -> str | None:
    usable = [n for n, f in features.items() if ok(f)]
    for preferred in names:
        if preferred in usable:
            return preferred
    return usable[0] if usable else None


def _is_text(f: dict) -> bool:
    return f.get("_type") == "Value" and f.get("dtype") in ("string", "large_string")


def _is_label(f: dict) -> bool:
    return f.get("_type") == "ClassLabel" or (f.get("_type") == "Value" and str(f.get("dtype", "")).startswith(("int", "string")))


def inspect(dataset: str, words: str = "", task: str | None = None) -> Layout:
    """The dataset's columns, classes and splits. Downloads no rows; raises DatasetError with the reason."""
    dataset = dataset.strip().removeprefix("https://huggingface.co/datasets/").strip("/")
    if not DATASET_ID.match(dataset):
        raise DatasetError(f"{dataset!r} doesn't look like a Hugging Face dataset id (owner/name)")
    meta = card(dataset)
    info = get_json(f"{SERVER}/info?{_q(dataset=dataset)}")
    infos = info.get("dataset_info") or {}
    if not infos:
        raise DatasetError(f"Hugging Face can't read {dataset} row by row yet")
    config = _pick_config(infos, dataset, words)
    spec = infos[config]
    features: dict[str, Any] = spec.get("features") or {}
    splits = {name: s.get("num_examples") or 0 for name, s in (spec.get("splits") or {}).items()}

    image = _pick(features, ("image", "img", "picture"), lambda f: f.get("_type") == "Image")
    text = _pick(features, TEXT_NAMES, _is_text)
    if task == "image_classification" and image is None:
        raise DatasetError(f"{dataset} has no image column")
    if task == "text_classification" and text is None:
        raise DatasetError(f"{dataset} has no text column")
    found_task = task or ("image_classification" if image else "text_classification" if text else None)
    if found_task is None:
        kinds = ", ".join(f"{n} ({f.get('_type')})" for n, f in features.items())
        raise DatasetError(f"{dataset} isn't text or images with labels (its columns: {kinds}); "
                           "I can build text and image classifiers")
    column = image if found_task == "image_classification" else text
    label = _pick({k: v for k, v in features.items() if k != column}, LABEL_NAMES, _is_label)
    if label is None:
        raise DatasetError(f"{dataset} has no label column to learn from")
    names = features[label].get("names")
    if names is not None and len(names) > MAX_CLASSES:
        raise DatasetError(f"{dataset} has {len(names)} classes; I can train up to {MAX_CLASSES}")

    train_split = "train" if "train" in splits else max(splits, key=splits.get) if splits else None
    if train_split is None:
        raise DatasetError(f"{dataset} has no splits Hugging Face can read")
    val_split = next((s for s in VAL_SPLITS if s in splits and s != train_split), None)
    return Layout(dataset=dataset, config=config, task=found_task, input=column, label=label,
                  classes=[str(n) for n in names] if names else [], train_split=train_split, val_split=val_split,
                  train_rows=splits.get(train_split, 0), val_rows=splits.get(val_split, 0) if val_split else 0,
                  license=meta.get("license"), label_names_from_data=names is None)


def find(words: str = "", dataset: str | None = None, task: str | None = None) -> Layout:
    """The dataset the user meant: a named id, else a known phrase, else a Hub search (first one that fits),
    else the default for the task."""
    if dataset:
        named = dataset.strip()
        if "/" in named or DATASET_ID.match(named) and not alias(named):
            try:
                return inspect(named, words, task)
            except DatasetError:
                if "/" in named:
                    raise
        words = f"{named} {words}".strip()
    known = alias(words) if words else None
    if known:
        return inspect(known, words, task)
    reasons: list[str] = []
    if words:
        for candidate in search(words, task):
            try:
                return inspect(candidate, words, task)
            except DatasetError as e:
                reasons.append(f"{candidate}: {e}")
            if len(reasons) >= 4:
                break
        if reasons or task is None:
            raise DatasetError(f"no Hugging Face dataset for {words!r} that I can train on"
                               + (f" ({reasons[0]})" if reasons else ""))
    return inspect(DEFAULTS[task or "text_classification"], words, task)


# --- pretrained models ---------------------------------------------------------------------------------------
#
# A new project can fine-tune an encoder from the Hub instead of training from scratch (ml_new with a
# base_model). Only encoders are accepted: the template adds a classifier head to AutoModel, so a generative
# model (GPT, Llama, T5) would be used for the wrong job. The config decides; the tokenizer's files must be
# there too, and the weights must be PyTorch or safetensors.

MODEL_API = "https://huggingface.co/api/models"
MODEL_ID = re.compile(r"^(?:[A-Za-z0-9][\w.-]*/)?[A-Za-z0-9][\w.-]*$")
# Encoder families AutoModel can load and that have a tokenizer; the template fine-tunes any of these.
ENCODER_TYPES = {"bert", "distilbert", "roberta", "albert", "electra", "deberta", "deberta-v2", "xlm-roberta",
                 "camembert", "xlnet", "longformer", "mpnet", "mobilebert", "squeezebert", "funnel", "modernbert"}
GENERATIVE_TYPES = {"gpt2", "gpt_neo", "gptj", "gpt_neox", "llama", "mistral", "mixtral", "falcon", "opt", "bloom",
                    "t5", "mt5", "bart", "mbart", "pegasus", "marian", "whisper", "wav2vec2", "vit", "clip", "blip",
                    "sam", "stable-diffusion"}
GENERATIVE_PIPELINES = ("text-generation", "text2text-generation", "summarization", "translation",
                        "automatic-speech-recognition", "text-to-image")
TOKENIZER_FILES = ("tokenizer.json", "tokenizer_config.json", "vocab.txt", "spiece.model",
                   "sentencepiece.bpe.model", "vocab.json")
WEIGHT_FILES = ("pytorch_model.bin", "model.safetensors")
# What people call a model -> one that is known to load and is small enough to train on a CPU.
MODEL_ALIASES: list[tuple[tuple[str, ...], str]] = [
    (("xtremedistil", "extreme distil", "small bert"), "microsoft/xtremedistil-l6-h256-uncased"),
    (("distilbert", "distil bert"), "distilbert-base-uncased"),
    (("minilm", "mini lm"), "microsoft/MiniLM-L12-H384-uncased"),
    (("roberta",), "roberta-base"),
    (("albert",), "albert-base-v2"),
    (("electra",), "google/electra-small-discriminator"),
    (("deberta",), "microsoft/deberta-v3-xsmall"),
    (("bert tiny", "tiny bert"), "google/bert_uncased_L-2_H-128_A-2"),
    (("bert",), "bert-base-uncased"),
]
DEFAULT_MODEL = "microsoft/xtremedistil-l6-h256-uncased"  # 12.7M parameters: fast on a CPU, still a real encoder


@dataclass
class ModelInfo:
    model: str
    license: str | None
    downloads: int | None
    model_type: str
    local: bool = False
    notes: list[str] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


def model_alias(words: str) -> str | None:
    """The model a phrase names ("a distilbert" -> distilbert-base-uncased); None if it names none."""
    said = " " + (words or "").lower() + " "
    for phrases, model in MODEL_ALIASES:
        if any(re.search(rf"\b{re.escape(p)}", said) for p in phrases):
            return model
    return None


def search_models(words: str, limit: int = 6) -> list[str]:
    """Model ids for what the user said, most downloaded first; gated, private and disabled ones are skipped."""
    tries = [words] + sorted({w for w in re.findall(r"[a-z0-9]{4,}", (words or "").lower())} - {"model", "fine", "tune"},
                             key=len, reverse=True)
    found: list[str] = []
    for query in tries:
        if not query:
            continue
        try:
            rows = get_json(f"{MODEL_API}?{_q(search=query, sort='downloads', direction=-1, limit=limit)}")
        except DatasetError:
            continue
        for row in rows:
            if not row.get("gated") and not row.get("private") and not row.get("disabled") and row["id"] not in found:
                found.append(row["id"])
        if len(found) >= limit:
            break
    return found[:limit]


def _license(data: dict[str, Any]) -> str | None:
    license_ = (data.get("cardData") or {}).get("license")
    if isinstance(license_, list):
        license_ = ", ".join(map(str, license_))
    if not license_:
        license_ = next((t.split(":", 1)[1] for t in data.get("tags", []) if t.startswith("license:")), None)
    return license_


def _remote_card(model: str) -> dict[str, Any]:
    """The model's Hub record, with its config.json fetched when the API doesn't carry one."""
    data = get_json(f"{MODEL_API}/{urllib.parse.quote(model, safe='/')}")
    if data.get("private") or data.get("gated") or data.get("disabled"):
        raise DatasetError(f"{model} needs a sign-in or an access request on Hugging Face")
    config = data.get("config") or {}
    if not config.get("model_type"):
        try:
            config = get_json(f"https://huggingface.co/{urllib.parse.quote(model, safe='/')}/resolve/main/config.json")
        except DatasetError:
            config = {}
    files = {str(s.get("rfilename") or "") for s in data.get("siblings") or []}
    return {"config": config, "files": files, "license": _license(data), "downloads": data.get("downloads"),
            "pipeline_tag": data.get("pipeline_tag") or ""}


def _local_card(path: Path) -> dict[str, Any]:
    config_path = path / "config.json"
    if not config_path.is_file():
        raise DatasetError(f"{path} isn't a Hugging Face model folder (no config.json)")
    try:
        config = json.loads(config_path.read_text())
    except ValueError as e:
        raise DatasetError(f"{path}/config.json isn't valid JSON: {e}") from None
    files = {p.name for p in path.iterdir()}
    return {"config": config, "files": files, "license": None, "downloads": None, "pipeline_tag": ""}


def _inspect(model: str, card: dict[str, Any], local: bool) -> ModelInfo:
    config = card["config"]
    kind = str(config.get("model_type") or "").lower()
    arch = " ".join(str(a) for a in config.get("architectures") or [])
    pipeline = card["pipeline_tag"]
    if kind in GENERATIVE_TYPES or pipeline in GENERATIVE_PIPELINES:
        raise DatasetError(f"{model} is a generative ({pipeline or kind}) model; I fine-tune encoder models "
                           "like BERT, DistilBERT or RoBERTa as a text classifier")
    if not kind:
        raise DatasetError(f"{model}'s config doesn't name a model type, so Hugging Face's loader can't read it")
    if kind not in ENCODER_TYPES and "SequenceClassification" not in arch:
        raise DatasetError(f"{model} is a {kind} model; I fine-tune encoder models like BERT, DistilBERT or RoBERTa")
    files = card["files"]
    if not any(f in files for f in TOKENIZER_FILES) and not any(f.endswith("tokenizer.json") for f in files):
        raise DatasetError(f"{model} has no tokenizer I can use")
    if not any(f in files for f in WEIGHT_FILES) and not any(f.endswith((".safetensors", ".bin")) for f in files):
        raise DatasetError(f"{model} has no PyTorch or safetensors weights")
    return ModelInfo(model=str(model), license=card["license"], downloads=card["downloads"], model_type=kind,
                     local=local)


def inspect_model(model: str) -> ModelInfo:
    """A pretrained model that can be fine-tuned as a text classifier; raises DatasetError with the reason.
    A local folder (the tests' tiny model) is read from disk; a Hub id is read from its API and config.json."""
    model = (model or "").strip()
    local = Path(model).expanduser()
    if local.is_dir():
        return _inspect(str(local.resolve()), _local_card(local), local=True)
    model = model.removeprefix("https://huggingface.co/").strip("/")
    if not MODEL_ID.match(model):
        raise DatasetError(f"{model!r} doesn't look like a Hugging Face model id (owner/name)")
    return _inspect(model, _remote_card(model), local=False)


# Named models that aren't text encoders: refused by name, before any Hub search, so "fine-tune gpt2" doesn't
# silently fall back to the default encoder. Word boundaries keep "gpt" out of "gpt2" (the entries are longest
# first) and "opt" out of "optimize".
GENERATIVE_NAMES = ("stable diffusion", "command-r", "mixtral", "mistral", "llama", "falcon", "gemma", "bloom",
                    "qwen", "whisper", "gpt2", "gpt-2", "gpt", "opt", "t5", "mt5", "bart", "mbart", "pegasus", "phi")
VISION_NAMES = ("vision transformer", "vit", "clip", "resnet", "yolo")


def refuse_named_model(words: str) -> None:
    """Raise DatasetError when the words name a model this template can't use, with the reason to say."""
    said = " " + (words or "").lower() + " "
    for name in GENERATIVE_NAMES:
        if re.search(rf"\b{re.escape(name)}\b", said):
            raise DatasetError(f"{name} is a generative model; I fine-tune encoder models like BERT, DistilBERT "
                               "or RoBERTa as a text classifier")
    for name in VISION_NAMES:
        if re.search(rf"\b{re.escape(name)}\b", said):
            raise DatasetError(f"{name} is a vision model; image fine-tuning isn't supported yet (images train "
                               "from scratch with a CNN)")


def find_model(words: str = "", model: str | None = None) -> ModelInfo:
    """The pretrained model the user meant: one they named, else a known family, else a Hub search, else the
    small default. Only encoders that pass `inspect_model` are returned."""
    if model:
        named = model.strip()
        if Path(named).expanduser().is_dir() or "/" in named:
            return inspect_model(named)
        refuse_named_model(named)
        known = model_alias(named)
        if known:
            return inspect_model(known)
        reasons: list[str] = []
        for candidate in search_models(named):
            try:
                return inspect_model(candidate)
            except DatasetError as e:
                reasons.append(f"{candidate}: {e}")
        raise DatasetError(f"no Hugging Face model for {named!r} that I can fine-tune"
                           + (f" ({reasons[0]})" if reasons else ""))
    refuse_named_model(words)
    known = model_alias(words)
    if known:
        return inspect_model(known)
    return inspect_model(DEFAULT_MODEL)


def model_name(model: str) -> str:
    """The part of a model id people say: microsoft/xtremedistil-l6-h256-uncased -> xtremedistil-l6-h256-uncased."""
    return model.rstrip("/").split("/")[-1]


# --- fetch --------------------------------------------------------------------------------------------------
#
# Parquet first: Hugging Face converts every dataset it can read to Parquet, and a split is one or a few files on
# the Hub's file CDN, images included, so a project is a handful of downloads instead of one per row or image (the
# rows API rate-limits per IP, and the cloud's IP is shared). Small files are read whole; big ones by HTTP range,
# only the row groups the sample needs. The rows API is the fallback when pyarrow is missing or Parquet fails.

def _parquet_files(layout: Layout, split: str) -> list[dict]:
    data = get_json(f"{SERVER}/parquet?{_q(dataset=layout.dataset, config=layout.config)}")
    return [f for f in data.get("parquet_files", []) if f.get("config") == layout.config and f.get("split") == split]


def _sample_parquet(layout: Layout, split: str, want: int, seed: int) -> list[dict]:
    import pyarrow.parquet as pq  # optional: without it, the rows API

    files = _parquet_files(layout, split)
    if not files:
        raise DatasetError(f"no Parquet files for {layout.dataset} {split}")
    rng = random.Random(seed)
    if len(files) > MAX_PARQUET_FILES:
        files = sorted(rng.sample(files, MAX_PARQUET_FILES), key=lambda f: f.get("filename", ""))
    whole = sum(int(f.get("size") or 0) for f in files) <= WHOLE_PARQUET_BYTES
    opened = []
    for f in files:
        source = io.BytesIO(get_bytes(f["url"])) if whole else RangeFile(f["url"])
        opened.append(pq.ParquetFile(source))
    groups = [(pf, g, pf.metadata.row_group(g).num_rows) for pf in opened for g in range(pf.num_row_groups)]
    picked = groups
    total = sum(n for _, _, n in groups)
    if not whole and total > want * 2:
        # Evenly spaced row groups from a random start: a split sorted by label still gives every class.
        need = min(len(groups), max(1, -(-len(groups) * want * 2 // total)))  # about twice the rows we keep
        start = rng.randrange(len(groups))
        picked = [groups[(start + round(i * len(groups) / need)) % len(groups)] for i in range(need)]
    columns = [layout.input, layout.label]
    rows = [row for pf, g, _ in picked for row in pf.read_row_group(g, columns=columns).to_pylist()]
    return rows


def _pages(total: int, want: int, seed: int) -> list[int]:
    """Offsets of `ceil(want / PAGE)` pages spread across a split of `total` rows, in a seeded order."""
    n_pages = max(1, -(-total // PAGE))
    take = min(n_pages, -(-want // PAGE) + 1)  # one spare page for rows we drop (no text, no label)
    return sorted(random.Random(seed).sample(range(n_pages), take)) if total else [0]


def _rows(layout: Layout, split: str, offset: int) -> list[dict]:
    url = f"{SERVER}/rows?{_q(dataset=layout.dataset, config=layout.config, split=split, offset=offset * PAGE, length=PAGE)}"
    return [r["row"] for r in get_json(url).get("rows", [])]


def _sample_rows(layout: Layout, split: str, total: int, want: int, seed: int) -> list[dict]:
    with ThreadPoolExecutor(4) as pool:
        pages = list(pool.map(lambda p: _rows(layout, split, p), _pages(total, want, seed)))
    return [row for page in pages for row in page]


def _sample(layout: Layout, split: str, total: int, want: int, seed: int) -> list[dict]:
    try:
        rows = _sample_parquet(layout, split, want, seed)
    except Exception as e:  # noqa: BLE001 - no pyarrow, no Parquet export, a CDN hiccup: the rows API
        log.info("parquet sample of %s %s failed (%s); using the rows API", layout.dataset, split, e)
        rows = _sample_rows(layout, split, total, want, seed)
    return balanced([row for row in rows if _usable(layout, row)], layout.label, want, seed + 1)


def balanced(rows: list[dict], label: str, want: int, seed: int) -> list[dict]:
    """Up to `want` rows, taking each class in turn, so a rare class isn't drowned out and a split sorted by
    label doesn't give one class only. A class that runs out leaves its turns to the others. Shuffled."""
    rng = random.Random(seed)
    by_class: dict[str, list[dict]] = {}
    for row in rows:
        by_class.setdefault(str(row[label]), []).append(row)
    queues = [by_class[k] for k in sorted(by_class)]
    for q in queues:
        rng.shuffle(q)
    out: list[dict] = []
    while len(out) < want and queues:
        queues = [q for q in queues if q]
        for q in queues:
            if len(out) >= want:
                break
            out.append(q.pop())
    rng.shuffle(out)
    return out


def _usable(layout: Layout, row: dict) -> bool:
    value, label = row.get(layout.input), row.get(layout.label)
    if label is None or (isinstance(label, int) and label < 0):  # -1: an unlabelled test row (GLUE, tweet_eval)
        return False
    if layout.task == "image_classification":
        return isinstance(value, dict) and bool(value.get("bytes") or value.get("src"))
    return isinstance(value, str) and bool(value.strip())


def _image_ext(data: bytes) -> str:
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        return ".png"
    if data[:3] == b"\xff\xd8\xff":
        return ".jpg"
    if data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return ".webp"
    if data[:2] == b"BM":
        return ".bmp"
    return ".img"  # PIL reads it by its content anyway


def shrink(body: bytes, largest: int = MAX_IMAGE_PX) -> bytes:
    """A photo bigger than `largest` px on a side, scaled down and saved as PNG (the models see 32 px, the desk's
    thumbnails 64). Small images, and anything PIL can't read, stay as they came."""
    try:
        from PIL import Image

        with Image.open(io.BytesIO(body)) as img:
            if max(img.size) <= largest:
                return body
            img = img.convert("RGB")
            img.thumbnail((largest, largest))
            out = io.BytesIO()
            img.save(out, format="PNG")
            return out.getvalue()
    except Exception:  # noqa: BLE001 - keep the original bytes
        return body


def cache_root() -> Path:
    return Path(os.environ.get("FORGE_HF_CACHE") or Path(tempfile.gettempdir()) / "forge-hf-cache")


def _cache_key(layout: Layout, n_train: int, n_val: int, seed: int) -> str:
    what = [CACHE_VERSION, layout.dataset, layout.config, layout.input, layout.label, layout.train_split,
            layout.val_split, n_train, n_val, seed]
    return hashlib.sha1(json.dumps(what).encode()).hexdigest()[:16]


def fetch(layout: Layout, dest: Path, n_train: int, n_val: int,
          progress: Callable[[str], None] = lambda _: None, seed: int = 7, cache: bool = True) -> dict[str, Any]:
    """Download the sample into dest/data/. Returns {classes, train, val, counts, cached} (classes filled in for a
    label with no ClassLabel names). The same sample (same dataset, sizes and seed) is kept in cache_root() and
    copied from there next time, for any project on this machine. Raises DatasetError when too little is usable."""
    data = dest / "data"
    hit = cache_root() / _cache_key(layout, n_train, n_val, seed)
    if cache and (hit / "meta.json").exists():
        try:
            shutil.copytree(hit / "data", data, dirs_exist_ok=True)
            meta = json.loads((hit / "meta.json").read_text())
            layout.notes.extend(meta.pop("notes", []))
            return {**meta, "cached": True}
        except (OSError, ValueError):
            log.warning("unreadable cache entry %s; downloading again", hit)
    meta = _download(layout, data, n_train, n_val, progress, seed)
    if cache:
        try:
            staging = hit.with_name(hit.name + f".{os.getpid()}.partial")
            shutil.rmtree(staging, ignore_errors=True)
            shutil.copytree(data, staging / "data")
            (staging / "meta.json").write_text(json.dumps({**meta, "notes": layout.notes}))
            if not hit.exists():
                staging.rename(hit)
            shutil.rmtree(staging, ignore_errors=True)
        except OSError:
            log.warning("couldn't keep %s in the cache", layout.dataset, exc_info=True)
    return {**meta, "cached": False}


def _download(layout: Layout, data: Path, n_train: int, n_val: int, progress: Callable[[str], None],
              seed: int) -> dict[str, Any]:
    data.mkdir(parents=True, exist_ok=True)
    if layout.val_split:
        train = _sample(layout, layout.train_split, layout.train_rows, n_train, seed)
        val = _sample(layout, layout.val_split, layout.val_rows, n_val, seed + 2)
    else:
        rows = _sample(layout, layout.train_split, layout.train_rows, n_train + n_val, seed)
        val, train = rows[:n_val], rows[n_val:]
    if len(val) < 20 and len(train) > 200:  # a test split with no labels: hold out train rows instead
        layout.notes.append(f"{layout.val_split} has no labels; holding out part of {layout.train_split}")
        val, train = train[:n_val], train[n_val:]
    if len(train) < 50 or len(val) < 20:
        raise DatasetError(f"only {len(train)} usable training rows and {len(val)} validation rows in {layout.dataset}")

    classes = list(layout.classes)
    if layout.label_names_from_data:
        values = sorted({str(r[layout.label]) for r in train + val}, key=lambda v: (len(v), v))
        if len(values) > MAX_CLASSES:
            raise DatasetError(f"{layout.dataset}'s {layout.label} column has {len(values)} different values; "
                               f"I can train up to {MAX_CLASSES} classes")
        classes = values
    index = {name: i for i, name in enumerate(classes)}

    def label_of(row: dict) -> int:
        value = row[layout.label]
        return index[str(value)] if layout.label_names_from_data else int(value)

    out: dict[str, list[dict]] = {}
    for split, rows in (("train", train), ("val", val)):
        out[split] = [{"id": f"{split}_{i:05d}" if split == "train" else f"val_{i:04d}", "label": label_of(r), "row": r}
                      for i, r in enumerate(rows)]

    if layout.task == "image_classification":
        images = data / "images"
        images.mkdir(exist_ok=True)
        todo = [item for split in ("train", "val") for item in out[split]]
        progress(f"saving {len(todo)} images")

        def save(item: dict) -> None:
            value = item["row"][layout.input]
            body = shrink(value.get("bytes") or get_bytes(value["src"]))  # Parquet carries the bytes; the rows API a URL
            item["file"] = f"{item['id']}{_image_ext(body)}"
            (images / item["file"]).write_bytes(body)

        with ThreadPoolExecutor(IMAGE_WORKERS) as pool:
            list(pool.map(save, todo))

    counts = [0] * len(classes)
    for split in ("train", "val"):
        with open(data / f"{split}.jsonl", "w", encoding="utf-8") as f:
            for item in out[split]:
                line = {"id": item["id"], "label": item["label"]}
                if layout.task == "image_classification":
                    line["file"] = item["file"]
                else:
                    line["text"] = " ".join(item["row"][layout.input].split())[:MAX_TEXT_CHARS]
                f.write(json.dumps(line, ensure_ascii=False) + "\n")
                if split == "train" and 0 <= item["label"] < len(counts):
                    counts[item["label"]] += 1
    return {"classes": classes, "train": len(out["train"]), "val": len(out["val"]), "counts": counts}
