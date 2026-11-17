"""Text classifiers Albie can build: an RNN, LSTM or GRU, a small Transformer, a 1D CNN, or an MLP over averaged words.

Every model reads padded word ids [batch, MAX_LEN] (0 = padding) and has the same two parts:
`encode` (ids -> one feature vector per text) and `head` (features -> class scores).
"""

from __future__ import annotations

import torch
import torch.nn as nn

KINDS = ("rnn", "lstm", "gru", "transformer", "cnn", "mlp")


def mean_over_words(vectors: torch.Tensor, ids: torch.Tensor) -> torch.Tensor:
    """Average of the per-word vectors, skipping padding."""
    mask = (ids != 0).unsqueeze(-1).float()
    return (vectors * mask).sum(1) / mask.sum(1).clamp(min=1)


class Classifier(nn.Module):
    def __init__(self, features: int, num_classes: int) -> None:
        super().__init__()
        self.head = nn.Sequential(nn.Linear(features, 64), nn.ReLU(), nn.Dropout(0.2), nn.Linear(64, num_classes))

    def encode(self, ids: torch.Tensor) -> torch.Tensor:
        raise NotImplementedError

    def forward(self, ids: torch.Tensor) -> torch.Tensor:
        return self.head(self.encode(ids))


class Recurrent(Classifier):
    """Reads the text one word at a time (an RNN, LSTM or GRU), then averages its states over the words."""

    def __init__(self, kind: str, vocab_size: int, num_classes: int, dim: int = 64, hidden: int = 64) -> None:
        super().__init__(hidden, num_classes)
        self.embedding = nn.Embedding(vocab_size, dim, padding_idx=0)
        cell = {"rnn": nn.RNN, "lstm": nn.LSTM, "gru": nn.GRU}[kind]
        self.rnn = cell(dim, hidden, batch_first=True)

    def encode(self, ids: torch.Tensor) -> torch.Tensor:
        states, _ = self.rnn(self.embedding(ids))
        return mean_over_words(states, ids)


class Transformer(Classifier):
    """A layer of self-attention: every word looks at every other word at once."""

    def __init__(self, vocab_size: int, num_classes: int, max_len: int, dim: int = 64) -> None:
        super().__init__(dim, num_classes)
        self.embedding = nn.Embedding(vocab_size, dim, padding_idx=0)
        self.position = nn.Embedding(max_len, dim)
        layer = nn.TransformerEncoderLayer(dim, nhead=4, dim_feedforward=128, dropout=0.1, batch_first=True)
        self.encoder = nn.TransformerEncoder(layer, num_layers=1, enable_nested_tensor=False)

    def encode(self, ids: torch.Tensor) -> torch.Tensor:
        positions = torch.arange(ids.shape[1], device=ids.device).unsqueeze(0)
        x = self.encoder(self.embedding(ids) + self.position(positions), src_key_padding_mask=ids == 0)
        return mean_over_words(x, ids)


class Convolution(Classifier):
    """Filters that spot 3, 4 and 5-word phrases anywhere in the text (Kim, 2014)."""

    def __init__(self, vocab_size: int, num_classes: int, dim: int = 64, filters: int = 32) -> None:
        super().__init__(filters * 3, num_classes)
        self.embedding = nn.Embedding(vocab_size, dim, padding_idx=0)
        self.convs = nn.ModuleList(nn.Conv1d(dim, filters, k, padding=k // 2) for k in (3, 4, 5))
        self.act = nn.ReLU()

    def encode(self, ids: torch.Tensor) -> torch.Tensor:
        x = self.embedding(ids).transpose(1, 2)  # [batch, dim, words]
        return torch.cat([self.act(conv(x)).amax(dim=2) for conv in self.convs], dim=1)


class BagOfWords(Classifier):
    """The words' vectors averaged, then an MLP: fast, and blind to word order."""

    def __init__(self, vocab_size: int, num_classes: int, dim: int = 64) -> None:
        super().__init__(dim, num_classes)
        self.embedding = nn.Embedding(vocab_size, dim, padding_idx=0)

    def encode(self, ids: torch.Tensor) -> torch.Tensor:
        return mean_over_words(self.embedding(ids), ids)


def build(kind: str, vocab_size: int, num_classes: int, max_len: int) -> Classifier:
    if kind in ("rnn", "lstm", "gru"):
        return Recurrent(kind, vocab_size, num_classes)
    if kind == "transformer":
        return Transformer(vocab_size, num_classes, max_len)
    if kind == "cnn":
        return Convolution(vocab_size, num_classes)
    if kind == "mlp":
        return BagOfWords(vocab_size, num_classes)
    raise ValueError(f"unknown model {kind!r}; one of {', '.join(KINDS)}")
