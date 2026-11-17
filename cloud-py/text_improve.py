"""The free 'what can I do better?' for the cloud's text classifier (ml-examples/text-classification), used when
Claude isn't reachable: hub/ml/improve.py's `scripted` path, for this project instead of the golden CIFAR-10 one.

The run overfits from about epoch 3 (val loss rises while train accuracy heads to 100%), so the change fights that:
weight decay, label smoothing, a cosine learning rate and fewer epochs. The patch is made from the project's own
train.py, so the forge lines stay exactly as they are (improve.check_proposal).

A project Albie built with ml_new (hub/ml/templates, a project.json beside train.py) gets the same three fixes when
it overfits, worded for its model and data. When it doesn't (a train/val gap under 5 points), regularizing only costs
accuracy, so it trains twice as long with a cosine learning rate instead (measured: LSTM on MNIST 88% -> 93%, CNN on
Fashion-MNIST 83% -> 86%).
"""
from __future__ import annotations

import json
from pathlib import Path

EDITS = [
    ('    parser.add_argument("--seed", type=int, default=0)\n',
     '    parser.add_argument("--seed", type=int, default=0)\n'
     '    parser.add_argument("--weight-decay", type=float, default=0.05)\n'
     '    parser.add_argument("--label-smoothing", type=float, default=0.1)\n'),
    ("            loss = F.cross_entropy(out, y)\n",
     "            loss = F.cross_entropy(out, y, label_smoothing=LABEL_SMOOTHING if training else 0.0)\n"),
    ("    opt = torch.optim.Adam(model.parameters(), lr=args.lr)\n",
     "    global LABEL_SMOOTHING\n"
     "    LABEL_SMOOTHING = args.label_smoothing\n"
     "    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=args.weight_decay)\n"
     "    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=args.epochs)\n"),
    ("        run_best = max(run_best, val_acc)\n",
     "        sched.step()\n        run_best = max(run_best, val_acc)\n"),
    ("def run_epoch(", "LABEL_SMOOTHING = 0.0\n\n\ndef run_epoch("),
]


# ml_new's templates have the same lines, except the per-epoch bookkeeping the scheduler steps before.
STEP = ("        if val_acc > best_acc:\n", "        sched.step()\n        if val_acc > best_acc:\n")
BUILT_EDITS = EDITS[:3] + [STEP, EDITS[4]]
LONGER_EDITS = [
    ("    opt = torch.optim.Adam(model.parameters(), lr=args.lr)\n",
     "    opt = torch.optim.Adam(model.parameters(), lr=args.lr)\n"
     "    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=args.epochs)\n"),
    STEP,
]
# The pretrained template (hub/ml/templates/pretrained) already uses AdamW and a warmup/decay schedule, so the
# change is more weight decay and label smoothing: a fine-tuned encoder memorizes 2,000 texts fast.
HF_EDITS = [
    EDITS[0],
    EDITS[1],
    ("    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=0.01)\n",
     "    global LABEL_SMOOTHING\n"
     "    LABEL_SMOOTHING = args.label_smoothing\n"
     "    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=args.weight_decay)\n"),
    EDITS[4],
]


def patched(train_py: Path, edits: list[tuple[str, str]] = EDITS) -> str:
    text = train_py.read_text()
    for old, new in edits:
        if old not in text:
            raise ValueError(f"train.py changed; can't find {old.strip()!r}")
        text = text.replace(old, new, 1)
    return text


def plan(d: dict, repo: Path) -> dict:
    """improve.scripted's shape: diagnosis, changes, patches, train_args, epochs, expected."""
    gap = d.get("train_val_gap")
    seen = (f"It's overfitting: {d['train_acc']:.0%} on training but {d['val_acc']:.0%} on validation."
            if gap is not None and gap > 0.05 else f"It tops out at {d.get('best_val_acc', 0):.0%} on validation.")
    if (repo / "project.json").is_file():
        return built_plan(d, repo, seen)
    return {
        "diagnosis": seen + " The word embeddings memorize the 12,000 headlines after two or three epochs.",
        "changes": [
            {"title": "Weight decay with AdamW", "why": "keeps the embeddings small, so they can't memorize headlines"},
            {"title": "Label smoothing", "why": "3% of the labels are wrong; it stops chasing them"},
            {"title": "Cosine learning rate", "why": "big steps first, then small ones to settle"},
            {"title": "Five epochs instead of eight", "why": "validation peaks early; the rest is overfitting"},
        ],
        "patches": [{"path": "train.py", "content": patched(repo / "train.py")}],
        "train_args": [],
        "epochs": 5,
        "expected": "a smaller train/val gap and about the same or better validation accuracy in under a minute",
    }


def built_plan(d: dict, repo: Path, seen: str) -> dict:
    """A project ml_new built: regularize it if it overfits, else train it longer, worded for its model and data."""
    project = json.loads((repo / "project.json").read_text())
    hf = project.get("model") == "hf"
    model = project.get("model_name") if hf else \
        {"rnn": "RNN", "lstm": "LSTM", "gru": "GRU", "cnn": "CNN", "mlp": "MLP"}.get(project.get("model"), "Transformer")
    what = "texts" if project.get("task") == "text_classification" else "images"
    n = (project.get("rows") or {}).get("train") or 0
    epochs = int(d.get("epochs") or (3 if hf else 8))
    gap = d.get("train_val_gap")
    if gap is not None and gap > 0.05:
        edits = HF_EDITS if hf else BUILT_EDITS
        changes = ([{"title": "More weight decay with AdamW", "why": "keeps the fine-tuned weights close to the pretrained ones, so they can't memorize examples"},
                    {"title": "Label smoothing", "why": "stops it chasing total certainty on every training example"}]
                   if hf else
                   [{"title": "Weight decay with AdamW", "why": "keeps the weights small, so the model can't memorize examples"},
                    {"title": "Label smoothing", "why": "stops it chasing total certainty on every training example"},
                    {"title": "Cosine learning rate", "why": "big steps first, then small ones to settle"}])
        return {
            "diagnosis": f"{seen} The {model} starts memorizing the {n:,} training {what} after a few epochs.",
            "changes": changes,
            "patches": [{"path": "train.py", "content": patched(repo / "train.py", edits)}],
            "train_args": [],
            "epochs": epochs,
            "expected": "a smaller train/val gap and about the same or better validation accuracy in under a minute",
        }
    longer = min(2 * epochs, 20)
    edits = HF_EDITS if hf else LONGER_EDITS
    changes = ([{"title": f"{longer} epochs instead of {epochs}", "why": "it was still improving when training stopped"},
                {"title": "Label smoothing", "why": "keeps the extra epochs from turning into memorization"}]
               if hf else
               [{"title": f"{longer} epochs instead of {epochs}", "why": "it was still improving when training stopped"},
                {"title": "Cosine learning rate", "why": "big steps first, then small ones to settle into a better minimum"}])
    return {
        "diagnosis": f"{seen} Training and validation are close, so the {model} is still learning: it needs more time, not less capacity.",
        "changes": changes,
        "patches": [{"path": "train.py", "content": patched(repo / "train.py", edits)}],
        "train_args": [],
        "epochs": longer,
        "expected": "a few points better validation accuracy, in about twice the time",
    }
