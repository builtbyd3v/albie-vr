"""The project's Hugging Face text dataset as the base model's own token ids.

Albie downloaded the rows into data/train.jsonl and data/val.jsonl ({"id", "text", "label"} per line);
project.json says where they came from, the class names and the model. The tokenizer is the base model's
(the same tokens it was pretrained on). Relabel/remove decisions from the ML desk live in
.forge/labels_override.json and apply on the next load:
    {"relabel": {"val_0007": 1}, "remove": ["train_01234"]}
"""

from __future__ import annotations

import json
from pathlib import Path

import torch
from torch.utils.data import DataLoader, Dataset

HERE = Path(__file__).resolve().parent
PROJECT = json.loads((HERE / "project.json").read_text())
CLASSES: list[str] = PROJECT["classes"]
MAX_LEN = int(PROJECT.get("max_len", 128))
BASE_MODEL: str = PROJECT["base_model"]
OVERRIDES = HERE / ".forge" / "labels_override.json"

_tokenizer = None


def get_tokenizer():
    """The base model's tokenizer, loaded once. A pad token is required for fixed-size batches; encoder
    tokenizers that have none borrow their EOS or UNK token."""
    global _tokenizer
    if _tokenizer is None:
        from transformers import AutoTokenizer

        tokenizer = AutoTokenizer.from_pretrained(BASE_MODEL, use_fast=True)
        if tokenizer.pad_token is None:
            tokenizer.pad_token = tokenizer.eos_token or tokenizer.unk_token
        _tokenizer = tokenizer
    return _tokenizer


def pad_id() -> int:
    token = get_tokenizer().pad_token_id
    return int(token if token is not None else 0)


def read_rows(split: str) -> list[dict]:
    with open(HERE / "data" / f"{split}.jsonl", encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def _overrides(path: Path | None) -> tuple[dict[str, int], set[str]]:
    if path is None or not path.exists():
        return {}, set()
    data = json.loads(path.read_text())
    return {k: int(v) for k, v in data.get("relabel", {}).items()}, set(data.get("remove", []))


def labelled_rows(split: str, overrides: Path | None = OVERRIDES) -> list[dict]:
    """The split's rows with the ML desk's relabel/remove decisions applied (no tokenizing: for samples)."""
    relabel, remove = _overrides(overrides)
    return [{**r, "label": relabel.get(r["id"], int(r["label"]))}
            for r in read_rows(split) if r["id"] not in remove]


class TextDataset(Dataset):
    """(token ids [MAX_LEN], label) per item. `texts`, `labels`, `ids` and `names` are what the ML desk shows."""

    def __init__(self, texts: list[str], labels: list[int], ids: list[str]) -> None:
        self.texts, self.labels, self.ids = texts, labels, ids
        self.names = [f"{i}.txt" for i in ids]
        encoded = get_tokenizer()(texts, truncation=True, max_length=MAX_LEN, padding="max_length",
                                  return_tensors="pt")["input_ids"]
        self.encoded = [encoded[i] for i in range(encoded.shape[0])]

    def __len__(self) -> int:
        return len(self.texts)

    def __getitem__(self, i: int) -> tuple[torch.Tensor, int]:
        return self.encoded[i], self.labels[i]


def get_datasets(overrides: Path | None = OVERRIDES) -> tuple[TextDataset, TextDataset, object]:
    train_rows, val_rows = labelled_rows("train", overrides), labelled_rows("val", overrides)
    out = [TextDataset([r["text"] for r in rows], [int(r["label"]) for r in rows], [r["id"] for r in rows])
           for rows in (train_rows, val_rows)]
    return out[0], out[1], get_tokenizer()


def get_loaders(batch_size: int = 32) -> tuple[DataLoader, DataLoader, object]:
    train, val, tokenizer = get_datasets()
    return (DataLoader(train, batch_size=batch_size, shuffle=True),
            DataLoader(val, batch_size=128, shuffle=False), tokenizer)


if __name__ == "__main__":
    train, val, tokenizer = get_datasets()
    print(f"{PROJECT['dataset']}: train {len(train)}, val {len(val)}, {len(CLASSES)} classes, "
          f"{type(tokenizer).__name__} for {BASE_MODEL}")
