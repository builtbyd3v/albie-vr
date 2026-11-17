"""The project's Hugging Face text dataset as padded word ids.

Albie downloaded the rows into data/train.jsonl and data/val.jsonl ({"id", "text", "label"} per line);
project.json says where they came from, the class names and the model. Relabel/remove decisions from the
ML desk live in .forge/labels_override.json and apply on the next load:
    {"relabel": {"val_0007": 1}, "remove": ["train_01234"]}
"""

from __future__ import annotations

import json
import re
from collections import Counter
from pathlib import Path

import torch
from torch.utils.data import DataLoader, Dataset

HERE = Path(__file__).resolve().parent
PROJECT = json.loads((HERE / "project.json").read_text())
CLASSES: list[str] = PROJECT["classes"]
MAX_LEN = int(PROJECT.get("max_len", 128))
VOCAB_SIZE = 20_000
PAD, UNK = 0, 1
OVERRIDES = HERE / ".forge" / "labels_override.json"
TOKEN = re.compile(r"[a-z0-9']+")


def tokenize(text: str) -> list[str]:
    return TOKEN.findall(text.lower())


class Vocab:
    """The VOCAB_SIZE most common train words (ties by first appearance); 0 is padding, 1 an unknown word."""

    def __init__(self, texts: list[str]) -> None:
        counts = Counter(word for text in texts for word in tokenize(text))
        self.words = [w for w, _ in counts.most_common(VOCAB_SIZE - 2)]
        self.index = {w: i + 2 for i, w in enumerate(self.words)}

    def __len__(self) -> int:
        return len(self.words) + 2

    def encode(self, text: str) -> torch.Tensor:
        ids = [self.index.get(w, UNK) for w in tokenize(text)][:MAX_LEN] or [UNK]
        return torch.tensor(ids + [PAD] * (MAX_LEN - len(ids)), dtype=torch.long)


class TextDataset(Dataset):
    """(word ids [MAX_LEN], label) per item. `texts`, `labels`, `ids` and `names` are what the ML desk shows."""

    def __init__(self, texts: list[str], labels: list[int], ids: list[str], vocab: Vocab) -> None:
        self.texts, self.labels, self.ids, self.vocab = texts, labels, ids, vocab
        self.names = [f"{i}.txt" for i in ids]
        self.encoded = [vocab.encode(t) for t in texts]

    def __len__(self) -> int:
        return len(self.texts)

    def __getitem__(self, i: int) -> tuple[torch.Tensor, int]:
        return self.encoded[i], self.labels[i]


def read_rows(split: str) -> list[dict]:
    with open(HERE / "data" / f"{split}.jsonl", encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def _overrides(path: Path | None) -> tuple[dict[str, int], set[str]]:
    if path is None or not path.exists():
        return {}, set()
    data = json.loads(path.read_text())
    return {k: int(v) for k, v in data.get("relabel", {}).items()}, set(data.get("remove", []))


def get_datasets(overrides: Path | None = OVERRIDES) -> tuple[TextDataset, TextDataset, Vocab]:
    train_rows, val_rows = read_rows("train"), read_rows("val")
    vocab = Vocab([r["text"] for r in train_rows])  # from the train texts only: the same vocab on every load
    relabel, remove = _overrides(overrides)
    out = []
    for rows in (train_rows, val_rows):
        rows = [r for r in rows if r["id"] not in remove]
        out.append(TextDataset([r["text"] for r in rows], [relabel.get(r["id"], int(r["label"])) for r in rows],
                               [r["id"] for r in rows], vocab))
    return out[0], out[1], vocab


def get_loaders(batch_size: int = 64) -> tuple[DataLoader, DataLoader, Vocab]:
    train, val, vocab = get_datasets()
    return (DataLoader(train, batch_size=batch_size, shuffle=True),
            DataLoader(val, batch_size=256, shuffle=False), vocab)


if __name__ == "__main__":
    train, val, vocab = get_datasets()
    print(f"{PROJECT['dataset']}: train {len(train)}, val {len(val)}, {len(CLASSES)} classes, vocab {len(vocab)}")
