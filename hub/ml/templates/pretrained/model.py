"""A pretrained Hugging Face encoder fine-tuned as this project's text classifier.

`build` loads the encoder named by project.json's `base_model` (BERT, DistilBERT, RoBERTa, ALBERT, ELECTRA and
the other encoder families) and puts a new classification head on it. The encoder's weights are the Hub's:
the model learns from the dataset, not from scratch.

Every model reads padded word ids [batch, MAX_LEN] (the tokenizer's pad id = padding) and has the same two
parts the ML desk uses: `encode` (ids -> one feature vector per text) and `head` (features -> class scores).
"""

from __future__ import annotations

import torch
import torch.nn as nn
from transformers import AutoModel


class PretrainedClassifier(nn.Module):
    def __init__(self, base_model: str, num_classes: int, pad_id: int, dropout: float = 0.1) -> None:
        super().__init__()
        self.base_model = base_model
        self.pad_id = int(pad_id)
        self.encoder = AutoModel.from_pretrained(base_model)
        hidden = int(self.encoder.config.hidden_size)
        self.dropout = nn.Dropout(dropout)
        self.head = nn.Linear(hidden, num_classes)

    def encode(self, ids: torch.Tensor) -> torch.Tensor:
        """Mean of the encoder's last hidden states over the real tokens (padding is skipped)."""
        mask = (ids != self.pad_id).long()
        hidden = self.encoder(input_ids=ids, attention_mask=mask).last_hidden_state
        weights = mask.unsqueeze(-1).float()
        return (hidden * weights).sum(1) / weights.sum(1).clamp(min=1.0)

    def forward(self, ids: torch.Tensor) -> torch.Tensor:
        return self.head(self.dropout(self.encode(ids)))


def build(base_model: str, num_classes: int, pad_id: int, max_len: int | None = None) -> PretrainedClassifier:
    return PretrainedClassifier(base_model, num_classes, pad_id)
