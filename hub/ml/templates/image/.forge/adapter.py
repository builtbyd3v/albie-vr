"""Forge adapter for an image project Albie built from a Hugging Face dataset (hub/ml/templates/image).

It imports the project's model and data, never copies them. Items are PIL images at the project's size.
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
import torch
from PIL import Image

from data import CLASSES, PROJECT, SIZE, get_datasets, normalize
from forge.adapter import Adapter, Sample
from model import build

REPO = Path(__file__).resolve().parents[1]
CHECKPOINT = REPO / "checkpoints" / "best.pt"


class ImageProjectAdapter(Adapter):
    archetype = "image_classification"

    def __init__(self) -> None:
        self._data = None

    def _datasets(self):
        if self._data is None:
            self._data = get_datasets()
        return self._data

    def build_model(self):
        return build(PROJECT["model"], SIZE, len(CLASSES))

    def load_model(self):
        if not CHECKPOINT.exists():
            return None
        model = self.build_model()
        model.load_state_dict(torch.load(CHECKPOINT, map_location="cpu"))
        return model.eval()

    def example_input(self) -> torch.Tensor:
        return torch.zeros(1, 3, SIZE, SIZE)

    def classes(self) -> list[str]:
        return list(CLASSES)

    def samples(self, split: str = "val", limit: int = 1000) -> list[Sample]:
        train, val = self._datasets()
        ds = val if split == "val" else train
        return [Sample(id=ds.ids[i], item=Image.fromarray(ds.images[i]), label=ds.labels[i], name=ds.names[i],
                       meta={"split": split})
                for i in range(min(limit, len(ds)))]

    def _batch(self, items: list[Image.Image]) -> torch.Tensor:
        return torch.stack([normalize(np.asarray(img.convert("RGB").resize((SIZE, SIZE)), dtype=np.uint8))
                            for img in items])

    def predict(self, model, items: list[Image.Image]) -> np.ndarray:
        was_training = model.training
        model.eval()
        with torch.no_grad():
            probs = torch.softmax(model(self._batch(items)), dim=1)
        model.train(was_training)
        return probs.numpy()

    def features(self, model, items: list[Image.Image]) -> np.ndarray:
        was_training = model.training
        model.eval()
        with torch.no_grad():
            feats = model.head[:-2](model.encode(self._batch(items)))
        model.train(was_training)
        return feats.numpy()
