"""The project's Hugging Face image dataset as normalized RGB tensors.

Albie downloaded the images into data/images/<id>.png (RGB, SIZE x SIZE) and the labels into
data/train.jsonl and data/val.jsonl ({"id", "file", "label"} per line); project.json says where they came
from, the class names and the model. Relabel/remove decisions from the ML desk live in
.forge/labels_override.json and apply on the next load:
    {"relabel": {"val_0007": 1}, "remove": ["train_01234"]}
"""

from __future__ import annotations

import json
from pathlib import Path

import numpy as np
import torch
from PIL import Image
from torch.utils.data import DataLoader, Dataset

HERE = Path(__file__).resolve().parent
PROJECT = json.loads((HERE / "project.json").read_text())
CLASSES: list[str] = PROJECT["classes"]
SIZE = int(PROJECT.get("image_size", 32))
OVERRIDES = HERE / ".forge" / "labels_override.json"


def normalize(image: np.ndarray) -> torch.Tensor:
    """uint8 [H, W, 3] -> float [3, H, W], each channel centred on 0."""
    return (torch.from_numpy(image).permute(2, 0, 1).float() / 255.0 - 0.5) / 0.25


class ImageDataset(Dataset):
    """(image tensor, label) per item. With `transform` off the item is the raw uint8 image (the desk's thumbnails)."""

    def __init__(self, images: np.ndarray, labels: list[int], ids: list[str]) -> None:
        self.images, self.labels, self.ids = images, labels, ids
        self.names = [f"{i}.png" for i in ids]
        self.transform = normalize

    def __len__(self) -> int:
        return len(self.labels)

    def __getitem__(self, i: int):
        image = self.images[i]
        return (self.transform(image) if self.transform is not None else image), self.labels[i]


def read_rows(split: str) -> list[dict]:
    with open(HERE / "data" / f"{split}.jsonl", encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def load_image(name: str) -> np.ndarray:
    with Image.open(HERE / "data" / "images" / name) as img:
        return np.asarray(img.convert("RGB").resize((SIZE, SIZE)), dtype=np.uint8)


def _overrides(path: Path | None) -> tuple[dict[str, int], set[str]]:
    if path is None or not path.exists():
        return {}, set()
    data = json.loads(path.read_text())
    return {k: int(v) for k, v in data.get("relabel", {}).items()}, set(data.get("remove", []))


def get_datasets(overrides: Path | None = OVERRIDES) -> tuple[ImageDataset, ImageDataset]:
    relabel, remove = _overrides(overrides)
    out = []
    for split in ("train", "val"):
        rows = [r for r in read_rows(split) if r["id"] not in remove]
        images = np.stack([load_image(r["file"]) for r in rows]) if rows else np.zeros((0, SIZE, SIZE, 3), np.uint8)
        out.append(ImageDataset(images, [relabel.get(r["id"], int(r["label"])) for r in rows], [r["id"] for r in rows]))
    return out[0], out[1]


def get_loaders(batch_size: int = 64) -> tuple[DataLoader, DataLoader]:
    train, val = get_datasets()
    return (DataLoader(train, batch_size=batch_size, shuffle=True),
            DataLoader(val, batch_size=256, shuffle=False))


if __name__ == "__main__":
    train, val = get_datasets()
    print(f"{PROJECT['dataset']}: train {len(train)}, val {len(val)}, {len(CLASSES)} classes, {SIZE}x{SIZE}")
