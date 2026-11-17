"""Forge adapter for a project Albie built on a pretrained Hugging Face encoder (hub/ml/templates/pretrained).

It imports the project's model and data, never copies them. Items are the raw texts; the base model's own
tokenizer encodes them, deterministically, so predictions always match the checkpoint.
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
import torch

from data import BASE_MODEL, CLASSES, MAX_LEN, PROJECT, get_tokenizer, labelled_rows, pad_id
from forge.adapter import Adapter, Sample
from model import build

REPO = Path(__file__).resolve().parents[1]
CHECKPOINT = REPO / "checkpoints" / "best.pt"


class PretrainedTextAdapter(Adapter):
    archetype = "text_classification"

    def build_model(self):
        return build(BASE_MODEL, len(CLASSES), pad_id(), MAX_LEN)

    def load_model(self):
        if not CHECKPOINT.exists():
            return None
        model = self.build_model()
        model.load_state_dict(torch.load(CHECKPOINT, map_location="cpu"))
        return model.eval()

    def example_input(self):
        token = 2 if pad_id() in (0, 1) else 0  # a real token, so the graph sees a non-pad attention mask
        return torch.full((1, MAX_LEN), token, dtype=torch.long)

    def classes(self) -> list[str]:
        return list(CLASSES)

    def samples(self, split: str = "val", limit: int = 1000) -> list[Sample]:
        rows = labelled_rows(split)
        return [Sample(id=r["id"], item=r["text"], label=int(r["label"]), name=f"{r['id']}.txt",
                       meta={"split": split, "words": len(r["text"].split())})
                for r in rows[:limit]]

    def _batch(self, items: list[str]) -> torch.Tensor:
        encoded = get_tokenizer()(list(items), truncation=True, max_length=MAX_LEN, padding="max_length",
                                  return_tensors="pt")
        return encoded["input_ids"]

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
            feats = model.encode(self._batch(items))
        model.train(was_training)
        return feats.numpy()
