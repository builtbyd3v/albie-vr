"""Forge adapter for a text project Albie built from a Hugging Face dataset (hub/ml/templates/text).

It imports the project's model and data, never copies them. Items are the raw texts; the vocab is rebuilt
from the train split, deterministically, so it always matches the checkpoint.
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
import torch

from data import CLASSES, MAX_LEN, PROJECT, get_datasets
from forge.adapter import Adapter, Sample
from model import build

REPO = Path(__file__).resolve().parents[1]
CHECKPOINT = REPO / "checkpoints" / "best.pt"


class TextProjectAdapter(Adapter):
    archetype = "text_classification"

    def __init__(self) -> None:
        self._data = None

    def _datasets(self):
        if self._data is None:
            self._data = get_datasets()
        return self._data

    def build_model(self):
        return build(PROJECT["model"], len(self._datasets()[2]), len(CLASSES), MAX_LEN)

    def load_model(self):
        if not CHECKPOINT.exists():
            return None
        model = self.build_model()
        model.load_state_dict(torch.load(CHECKPOINT, map_location="cpu"))
        return model.eval()

    def example_input(self) -> torch.Tensor:
        return torch.ones(1, MAX_LEN, dtype=torch.long)

    def classes(self) -> list[str]:
        return list(CLASSES)

    def samples(self, split: str = "val", limit: int = 1000) -> list[Sample]:
        train, val, _ = self._datasets()
        ds = val if split == "val" else train
        return [Sample(id=ds.ids[i], item=ds.texts[i], label=ds.labels[i], name=ds.names[i],
                       meta={"split": split, "words": len(ds.texts[i].split())})
                for i in range(min(limit, len(ds)))]

    def _batch(self, items: list[str]) -> torch.Tensor:
        vocab = self._datasets()[2]
        return torch.stack([vocab.encode(text) for text in items])

    def predict(self, model, items: list[str]) -> np.ndarray:
        was_training = model.training
        model.eval()
        with torch.no_grad():
            probs = torch.softmax(model(self._batch(items)), dim=1)
        model.train(was_training)
        return probs.numpy()

    def features(self, model, items: list[str]) -> np.ndarray:
        was_training = model.training
        model.eval()
        with torch.no_grad():
            feats = model.head[:-2](model.encode(self._batch(items)))
        model.train(was_training)
        return feats.numpy()
