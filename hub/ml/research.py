"""Research on the Hugging Face Hub: model and dataset cards with an honest "fits this project" check.

`find_models` asks the Hub's public model API (no token needed; HF_TOKEN raises the rate limit) and returns one
card per model with the Hub's own facts (task, parameter count, licence, downloads, likes, last update) and checks
computed from those facts against what `ml_new` can do today:

- **task**: does the model's task match what the user wants to train?
- **trainer**: can `ml_new` fine-tune it? Only encoder families in `hf_data.ENCODER_TYPES` (BERT, DistilBERT,
  RoBERTa...) as a text classifier. Everything else (image, generative, speech models) is shown as "browse only",
  with the reason, never hidden and never offered to train.
- **size**: a training-time estimate scaled linearly by parameter count from the one measured run in
  hub/ml/README.md (XTREMEDistil, 12.7M parameters, 19.4 s per epoch at 2,000 rows on a laptop CPU), for the
  default 3 epochs. It is labelled as an estimate; the cloud's cpu-basic CPU may be slower. An unpublished
  parameter count is "unknown", not guessed.
- **licence**: open (Apache, MIT, BSD, CC-BY...), conditions (share-alike, RAIL, custom), non-commercial, or none
  stated.
- **access**: gated, private or disabled models can't be downloaded without a sign-in.

The Hub publishes no on-device latency, so the cards carry `on_device_latency: null` and say "not measured".

Results stream: `emit(phase, payload)` is called as the list arrives ("card" per model) and again as each model's
file list arrives ("update": weight size from the real files, tokenizer and weights present). `run` turns those
calls into `ml.research` events on the app's bus, so the ML desk's Research panel fills in as they come.
"""
from __future__ import annotations

import asyncio
import itertools
import math
import re
import time
import urllib.parse
from collections.abc import Callable
from concurrent.futures import ThreadPoolExecutor
from typing import Any

from . import hf_data

MODEL_API = hf_data.MODEL_API
DATASET_API = hf_data.HUB_API
# Fields the list API returns with expand[] (checked against the live API on 2026-11-17).
MODEL_EXPAND = ("safetensors", "lastModified", "downloads", "likes", "pipeline_tag", "cardData", "gated", "config",
                "library_name", "tags", "private", "disabled")
DATASET_EXPAND = ("cardData", "downloads", "likes", "lastModified", "gated", "tags", "private", "disabled")
MAX_LIMIT = 10
DETAIL_WORKERS = 4

# The one measured fine-tuning run (hub/ml/README.md): the default encoder, 2,000 train rows, laptop CPU.
MEASURED_PARAMS = 12.7e6
MEASURED_EPOCH_S = 19.4
MEASURED_ROWS = 2000
DEFAULT_EPOCHS = 3  # new_project.PRETRAINED_EPOCHS
GOOD_MINUTES, SLOW_MINUTES = 10.0, 30.0  # 3 epochs within these: fine / slow / too slow for a live demo

# Tasks a fine-tuned encoder can serve: the template adds a classification head to the encoder.
ENCODER_PIPELINES = {"text-classification", "fill-mask", "feature-extraction", "sentence-similarity",
                     "token-classification", "zero-shot-classification", None, ""}
TASKS = {  # what the tool accepts -> the Hub's pipeline tag
    "text_classification": "text-classification", "image_classification": "image-classification",
    "object_detection": "object-detection", "speech_recognition": "automatic-speech-recognition",
    "text_generation": "text-generation",
}
TASK_WORDS = [  # words in the query -> a Hub task, when no task was given (first match wins)
    (("detect", "detection", "bounding box", "yolo"), "object-detection"),
    (("image", "photo", "picture", "camera", "vision", "pixel", "x-ray", "xray", "scan"), "image-classification"),
    (("speech", "audio", "voice", "transcri"), "automatic-speech-recognition"),
    (("chat", "generate text", "llm", "generation"), "text-generation"),
]
TRAINABLE_DATASET_TASKS = {"text-classification": "text_classification", "image-classification": "image_classification"}
STOP_WORDS = {"model", "models", "dataset", "datasets", "data", "fine", "tune", "tuned", "train", "with", "that",
              "find", "some", "best", "good", "small", "from", "hugging", "face", "huggingface", "classifier",
              "classification", "about", "using", "what", "which", "pretrained", "this", "project"}
OPEN_LICENSES = ("apache", "mit", "bsd", "cc-by-4.0", "cc-by-3.0", "cc-by-2.0", "cc0", "unlicense", "isc", "afl",
                 "artistic", "zlib", "pddl", "odc-by", "cdla-permissive", "ecl", "wtfpl", "bsl-1.0", "postgresql")
NONCOMMERCIAL = re.compile(r"(^|[-_ ])nc([-_ ]|$)|non-?commercial|-nd|research-only", re.I)
SIZE_ROWS = {"n<1K": 1_000, "1K<n<10K": 10_000, "10K<n<100K": 100_000, "100K<n<1M": 1_000_000,
             "1M<n<10M": 10_000_000, "10M<n<100M": 100_000_000, "100M<n<1B": 1_000_000_000, "n>1T": 10**12}

Emit = Callable[[str, dict[str, Any]], None]


# --- small helpers -------------------------------------------------------------------------------------------

def _check(name: str, level: str, text: str) -> dict[str, str]:
    """One fit check: level is good | warn | bad | unknown (green / amber / red / grey on the panel)."""
    return {"name": name, "level": level, "text": text}


def _tries(query: str) -> list[str]:
    """The whole query, then its longer words: the Hub's search matches ids, so "tweet sentiment" finds little."""
    words = sorted({w for w in re.findall(r"[a-z0-9][a-z0-9-]{3,}", (query or "").lower())} - STOP_WORDS,
                   key=len, reverse=True)
    out = [q for q in [(query or "").strip()] + words if q]
    return list(dict.fromkeys(out))


def hub_task(query: str, task: str | None) -> tuple[str, bool]:
    """(the Hub pipeline tag to search, whether it was inferred from the words). Text classification by default:
    that is what ml_new fine-tunes."""
    if task:
        t = task.strip().lower()
        return TASKS.get(t, t.replace("_", "-")), False
    said = " " + (query or "").lower() + " "
    for words, tag in TASK_WORDS:
        if any(w in said for w in words):
            return tag, True
    return "text-classification", True


def _expand(fields: tuple[str, ...]) -> str:
    return "&".join("expand[]=" + f for f in fields)


def _license(row: dict[str, Any]) -> str | None:
    return hf_data._license(row)  # cardData.license, else a license: tag


def license_check(license_: str | None) -> dict[str, str]:
    if not license_ or license_.lower() in ("unknown", "none"):
        return _check("licence", "warn", "No licence stated: check before you ship anything built on it")
    low = license_.lower()
    if NONCOMMERCIAL.search(low):
        return _check("licence", "bad", f"{license_}: non-commercial or no-derivatives terms")
    if low.startswith("cc-by-sa") or "gpl" in low:
        return _check("licence", "warn", f"{license_}: open, but derived work must share alike")
    if any(low.startswith(p) for p in OPEN_LICENSES):
        return _check("licence", "good", f"{license_}: open licence")
    return _check("licence", "warn", f"{license_}: custom or conditional terms; read them before you ship")


def size_check(params: int | None) -> tuple[dict[str, str], float | None]:
    """The size check and the estimated seconds per epoch (None when the parameter count isn't published)."""
    if not params:
        return _check("size", "unknown", "Parameter count not published, so training time can't be estimated"), None
    epoch_s = MEASURED_EPOCH_S * params / MEASURED_PARAMS
    minutes = epoch_s * DEFAULT_EPOCHS / 60
    est = (f"about {minutes:.0f} min for {DEFAULT_EPOCHS} epochs on {MEASURED_ROWS:,} rows (estimate scaled from "
           "one measured CPU run; the cloud CPU may be slower)")
    if minutes <= GOOD_MINUTES:
        return _check("size", "good", f"{_pretty_params(params)} parameters: {est}"), epoch_s
    if minutes <= SLOW_MINUTES:
        return _check("size", "warn", f"{_pretty_params(params)} parameters: slow, {est}"), epoch_s
    return _check("size", "bad", f"{_pretty_params(params)} parameters: too big for the CPU trainer, {est}"), epoch_s


def _pretty_params(n: int) -> str:
    return f"{n / 1e9:.1f}B" if n >= 1e9 else f"{n / 1e6:.0f}M" if n >= 1e7 else f"{n / 1e6:.1f}M"


def _params(row: dict[str, Any]) -> int | None:
    total = (row.get("safetensors") or {}).get("total")
    return int(total) if isinstance(total, (int, float)) and total > 0 else None


# --- model cards ---------------------------------------------------------------------------------------------

def model_card(row: dict[str, Any], want_task: str) -> dict[str, Any]:
    """A card from one row of the Hub's model list, with the checks the list alone can decide."""
    config = row.get("config") or {}
    kind = str(config.get("model_type") or "").lower() or None
    pipeline = row.get("pipeline_tag") or None
    library = row.get("library_name") or None
    params = _params(row)
    license_ = _license(row)
    checks: list[dict[str, str]] = []

    if pipeline == want_task:
        checks.append(_check("task", "good", f"Built for {want_task}"))
    elif want_task == "text-classification" and pipeline in ENCODER_PIPELINES and kind in hf_data.ENCODER_TYPES:
        checks.append(_check("task", "good", f"A {kind} encoder ({pipeline or 'no task tag'}): gets a new "
                                             "classification head"))
    else:
        checks.append(_check("task", "bad", f"Its task is {pipeline or 'not tagged'}, not {want_task}"))

    blocked = row.get("gated") not in (False, None) or row.get("private") or row.get("disabled")
    reason = None
    if blocked:
        reason = "needs a sign-in or an access request on Hugging Face"
    elif pipeline and pipeline.startswith("image"):
        reason = "image models aren't fine-tuned yet; ml_new trains images from scratch with a CNN"
    elif kind is None:
        reason = "its config names no model type, so the trainer can't load it"
    elif kind in hf_data.GENERATIVE_TYPES or pipeline in hf_data.GENERATIVE_PIPELINES:
        reason = f"it's a {pipeline or kind} model; ml_new fine-tunes text encoders as classifiers"
    elif kind not in hf_data.ENCODER_TYPES:
        reason = (f"it's a {kind} model; ml_new fine-tunes text encoders (BERT, DistilBERT, RoBERTa...) only"
                  + ("; images train from scratch with a CNN" if pipeline and "image" in pipeline else ""))
    elif pipeline not in ENCODER_PIPELINES:
        reason = f"its task ({pipeline}) isn't one a classification head fits"
    trainable = reason is None
    checks.append(_check("trainer", "good", "ml_new can fine-tune it today") if trainable
                  else _check("trainer", "bad", "Browse only: " + reason))
    size, epoch_s = size_check(params)
    if not trainable:  # no training estimate for a model the trainer can't take
        size, epoch_s = _check("size", "unknown", f"{_pretty_params(params)} parameters" if params
                               else "Parameter count not published"), None
    checks.append(size)
    checks.append(license_check(license_))
    model_id = str(row.get("id") or row.get("modelId"))
    return {"kind": "model", "id": model_id, "url": f"https://huggingface.co/{model_id}", "task": pipeline,
            "model_type": kind, "library": library, "params": params,
            "params_source": "safetensors metadata" if params else None, "weights_mb": None,
            "license": license_, "downloads": row.get("downloads"), "likes": row.get("likes"),
            "last_modified": row.get("lastModified"), "gated": bool(blocked),
            "status": "trainable" if trainable else "browse_only", "trainable": trainable,
            "est_epoch_s": round(epoch_s, 1) if epoch_s else None, "on_device_latency": None,
            "checks": checks, "details": "pending" if trainable else "skipped"}


def _detail(card: dict[str, Any]) -> dict[str, Any]:
    """The model's real file list (sizes included): weight size, and whether a tokenizer and weights are there.
    Changes `card` in place and returns it."""
    data = hf_data.get_json(f"{MODEL_API}/{urllib.parse.quote(card['id'], safe='/')}?blobs=true", tries=2)
    files = {str(s.get("rfilename") or ""): s.get("size") for s in data.get("siblings") or []}
    weights = [n for n in files if n.endswith(".safetensors")] or \
              [n for n in files if re.match(r"(.*/)?pytorch_model.*\.bin$", n)]
    sizes = [files[n] for n in weights if isinstance(files[n], int)]
    card["weights_mb"] = round(sum(sizes) / 1e6, 1) if sizes else None
    tokenizer = any(n.rsplit("/", 1)[-1] in hf_data.TOKENIZER_FILES for n in files)
    if not weights or not tokenizer:
        missing = " and ".join(w for w, ok in (("PyTorch or safetensors weights", weights), ("a tokenizer", tokenizer))
                               if not ok)
        card["trainable"], card["status"] = False, "browse_only"
        card["checks"] = [c for c in card["checks"] if c["name"] != "trainer"] + \
                         [_check("trainer", "bad", f"Browse only: it has no {missing}")]
    elif card["weights_mb"] is not None:
        card["checks"].append(_check("download", "good" if card["weights_mb"] < 600 else "warn",
                                     f"{card['weights_mb']:,.0f} MB of weights to download (from its files)"))
    card["details"] = "done"
    return card


def _score(card: dict[str, Any]) -> tuple:
    levels = [c["level"] for c in card["checks"]]
    # An unknown size is worse than a known slow one: the pick should be one whose cost we can say.
    return (levels.count("bad"), levels.count("unknown"), levels.count("warn"),
            card.get("params") or 0, -math.log10((card.get("downloads") or 0) + 1))


def recommend(cards: list[dict[str, Any]]) -> tuple[dict[str, Any] | None, str]:
    """The pick: a trainable model with no red check, fewest unknown then amber checks, the smallest, the most
    downloaded."""
    ok = [c for c in cards if c["trainable"] and not any(k["level"] == "bad" for k in c["checks"])]
    if not ok:
        browse = sum(1 for c in cards if not c["trainable"])
        why = (f"none of these {len(cards)} can be fine-tuned here ({browse} are browse only)" if cards
               else "the Hub found nothing for that")
        return None, why + f"; ml_new's default encoder ({hf_data.DEFAULT_MODEL}) can always be fine-tuned"
    best = min(ok, key=_score)
    reasons = [k["text"] for k in best["checks"] if k["level"] == "good"]
    caveats = [k["text"] for k in best["checks"] if k["level"] in ("warn", "unknown")]
    return {"id": best["id"], "reasons": reasons, "caveats": caveats}, ("no red checks, fewest unknowns and amber checks, then the "
                                                    "smallest, then the most downloaded")


def find_models(query: str, task: str | None = None, limit: int = 6, emit: Emit | None = None) -> dict[str, Any]:
    """Model cards for what the user said, most downloaded first, each with its fit checks, and one pick."""
    emit = emit or (lambda phase, payload: None)
    limit = max(1, min(MAX_LIMIT, int(limit or 6)))
    want, inferred = hub_task(query, task)
    emit("start", {"kind": "models", "query": query, "task": want, "task_inferred": inferred})
    cards: list[dict[str, Any]] = []
    seen: set[str] = set()
    errors: list[str] = []
    for words in _tries(query):
        # Text: no task filter, so encoders tagged fill-mask (bert-base) come too; the task check sorts them out.
        params = hf_data._q(search=words, pipeline_tag=None if want == "text-classification" else want,
                            sort="downloads", direction=-1, limit=limit)
        url = f"{MODEL_API}?{params}&{_expand(MODEL_EXPAND)}"
        try:
            rows = hf_data.get_json(url, tries=2)
        except hf_data.DatasetError as e:
            errors.append(str(e))
            continue
        for row in rows:
            card = model_card(row, want)
            if card["id"] in seen or len(cards) >= limit:
                continue
            seen.add(card["id"])
            cards.append(card)
            emit("card", {"card": card})
        if len(cards) >= limit:
            break
    with ThreadPoolExecutor(DETAIL_WORKERS) as pool:
        def one(card: dict[str, Any]) -> None:
            try:
                _detail(card)
            except hf_data.DatasetError as e:
                card["details"] = f"unavailable: {e}"
            emit("update", {"card": card})
        list(pool.map(one, [c for c in cards if c["trainable"]]))
    pick, why = recommend(cards)
    result = {"kind": "models", "query": query, "task": want, "task_inferred": inferred, "cards": cards,
              "recommended": pick, "recommended_why": why, "sample": False, "source": "huggingface.co/api/models",
              "fetched_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
              "on_device_latency": "not measured: the Hub doesn't publish it"}
    if not cards:
        result["error"] = ("Hugging Face didn't answer: " + errors[0]) if errors else f"no models found for {query!r}"
    emit("done", {k: v for k, v in result.items() if k != "cards"})
    return result


# --- dataset cards -------------------------------------------------------------------------------------------

def dataset_card(row: dict[str, Any]) -> dict[str, Any]:
    tags = [str(t) for t in row.get("tags") or []]
    tasks = [t.split(":", 1)[1] for t in tags if t.startswith("task_categories:")]
    size = next((t.split(":", 1)[1] for t in tags if t.startswith("size_categories:")), None)
    license_ = _license(row)
    checks = []
    usable = [t for t in tasks if t in TRAINABLE_DATASET_TASKS]
    if usable:
        checks.append(_check("task", "good", f"Tagged {usable[0]}: ml_new trains this kind"))
    elif tasks:
        checks.append(_check("task", "bad", f"Tagged {', '.join(tasks[:2])}; ml_new trains text or image classifiers"))
    else:
        checks.append(_check("task", "unknown", "No task tag; ml_new reads its columns to decide"))
    if size is None:
        checks.append(_check("size", "unknown", "Size not tagged"))
    elif SIZE_ROWS.get(size, 10**6) <= 1_000:
        checks.append(_check("size", "warn", f"{size} rows: small for training"))
    else:
        checks.append(_check("size", "good", f"{size} rows; ml_new samples a few thousand"))
    checks.append(license_check(license_))
    blocked = row.get("gated") not in (False, None) or row.get("private") or row.get("disabled")
    checks.append(_check("access", "bad", "Needs a sign-in or an access request") if blocked
                  else _check("access", "good", "Public download"))
    ok = not any(c["level"] == "bad" for c in checks)
    return {"kind": "dataset", "id": row.get("id"), "url": f"https://huggingface.co/datasets/{row.get('id')}",
            "tasks": tasks, "size_category": size, "license": license_, "downloads": row.get("downloads"),
            "likes": row.get("likes"), "last_modified": row.get("lastModified"), "gated": bool(blocked),
            "status": "usable" if ok else "browse_only", "trainable": ok, "checks": checks,
            "note": "ml_new still reads its columns before training and says if it can't find text and labels"}


def find_datasets(query: str, task: str | None = None, limit: int = 6, emit: Emit | None = None) -> dict[str, Any]:
    emit = emit or (lambda phase, payload: None)
    limit = max(1, min(MAX_LIMIT, int(limit or 6)))
    t = (task or "").strip().lower()
    filter_ = hf_data.TASK_FILTER.get(t)
    emit("start", {"kind": "datasets", "query": query, "task": t or None, "task_inferred": False})
    cards, seen, errors = [], set(), []
    for words in _tries(query):
        params = hf_data._q(search=words, filter=filter_, sort="downloads", direction=-1, limit=limit)
        url = f"{DATASET_API}?{params}&{_expand(DATASET_EXPAND)}"
        try:
            rows = hf_data.get_json(url, tries=2)
        except hf_data.DatasetError as e:
            errors.append(str(e))
            continue
        for row in rows:
            if row.get("id") in seen or len(cards) >= limit:
                continue
            seen.add(row.get("id"))
            card = dataset_card(row)
            cards.append(card)
            emit("card", {"card": card})
        if len(cards) >= limit:
            break
    ok = [c for c in cards if c["trainable"]]
    pick = min(ok, key=_score) if ok else None
    result = {"kind": "datasets", "query": query, "task": t or None, "cards": cards,
              "recommended": {"id": pick["id"], "reasons": [k["text"] for k in pick["checks"] if k["level"] == "good"]}
              if pick else None,
              "recommended_why": "no red checks, fewest unknowns and amber checks, then the most downloaded" if pick
              else "none of these is tagged for a task ml_new trains", "sample": False,
              "source": "huggingface.co/api/datasets", "fetched_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    if not cards:
        result["error"] = ("Hugging Face didn't answer: " + errors[0]) if errors else f"no datasets found for {query!r}"
    emit("done", {k: v for k, v in result.items() if k != "cards"})
    return result


# --- on the bus ----------------------------------------------------------------------------------------------

_ids = itertools.count(1)


async def run(state: Any, kind: str, query: str, task: str | None = None, limit: int = 6) -> dict[str, Any]:
    """Search in a worker thread; each card goes out as an `ml.research` event as it arrives (the Research panel
    streams them), and the whole result comes back at the end. Without a bus (a test) it just searches."""
    query = (query or "").strip()
    if not query:
        raise ValueError("say what to look for, e.g. 'sentiment' or 'small text classifier'")
    search_id = f"r{next(_ids)}-{int(time.time())}"
    bus = getattr(state, "bus", None)
    loop = asyncio.get_running_loop()

    def emit(phase: str, payload: dict[str, Any]) -> None:
        if bus is None:
            return
        body = {"search_id": search_id, "phase": phase, **payload}
        loop.call_soon_threadsafe(lambda: asyncio.ensure_future(bus.publish("ml.research", body)))

    find = find_models if kind == "models" else find_datasets
    try:
        result = await asyncio.to_thread(find, query, task, limit, emit)
    except Exception as e:  # noqa: BLE001 - the panel shows why
        emit("error", {"error": str(e)})
        raise
    await asyncio.sleep(0)  # let the queued events go out before the answer
    return {"search_id": search_id, **result}


def spoken(result: dict[str, Any]) -> dict[str, Any]:
    """What Albie's voice model gets back: short cards, the pick and how to act on it."""
    short = []
    for c in result.get("cards", []):
        keep = {k: c.get(k) for k in ("id", "task", "params", "license", "downloads", "status", "weights_mb",
                                      "size_category", "tasks") if c.get(k) is not None}
        keep["checks"] = [f"{k['level']}: {k['text']}" for k in c["checks"]]
        short.append(keep)
    out = {k: v for k, v in result.items() if k != "cards"}
    out["cards"] = short
    pick = result.get("recommended")
    if result.get("kind") == "models":
        out["next"] = ("The cards are on the ML desk's Research panel. Say the recommended model, its main reason "
                       "and its biggest caveat (if any) in one or two sentences, and offer to fine-tune it: after a "
                       "yes, call ml_new with base_model set to its id. Don't quote numbers not in these cards; latency isn't measured."
                       if pick else "Say why none of these fits, in one sentence.")
    else:
        out["next"] = ("Say the recommended dataset and why in one sentence; ml_new takes it as dataset."
                       if pick else "Say why none of these fits, in one sentence.")
    return out
