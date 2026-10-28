"""The free 'what can I do better?' for the cloud's text classifier (ml-examples/text-classification), used when
Claude isn't reachable: hub/ml/improve.py's `scripted` path, for this project instead of the golden CIFAR-10 one.

The run overfits from about epoch 3 (val loss rises while train accuracy heads to 100%), so the change fights that:
weight decay, label smoothing, a cosine learning rate and fewer epochs. The patch is made from the project's own
train.py, so the forge lines stay exactly as they are (improve.check_proposal).
"""
from __future__ import annotations

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


def patched(train_py: Path) -> str:
    text = train_py.read_text()
    for old, new in EDITS:
        if old not in text:
            raise ValueError(f"train.py changed; can't find {old.strip()!r}")
        text = text.replace(old, new, 1)
    return text


def plan(d: dict, repo: Path) -> dict:
    """improve.scripted's shape: diagnosis, changes, patches, train_args, epochs, expected."""
    gap = d.get("train_val_gap")
    seen = (f"It's overfitting: {d['train_acc']:.0%} on training but {d['val_acc']:.0%} on validation."
            if gap is not None and gap > 0.05 else f"It tops out at {d.get('best_val_acc', 0):.0%} on validation.")
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
