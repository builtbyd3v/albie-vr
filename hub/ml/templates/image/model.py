"""Image classifiers Albie can build: a CNN, an MLP, an RNN/LSTM/GRU that reads the image row by row,
or a small vision Transformer that looks at 4x4 patches.

Every model reads normalized RGB images [batch, 3, SIZE, SIZE] and has the same two parts:
`encode` (image -> one feature vector) and `head` (features -> class scores).
"""

from __future__ import annotations

import torch
import torch.nn as nn

KINDS = ("cnn", "mlp", "rnn", "lstm", "gru", "transformer")


class Classifier(nn.Module):
    def __init__(self, features: int, num_classes: int) -> None:
        super().__init__()
        self.head = nn.Sequential(nn.Linear(features, 64), nn.ReLU(), nn.Dropout(0.2), nn.Linear(64, num_classes))

    def encode(self, x: torch.Tensor) -> torch.Tensor:
        raise NotImplementedError

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return self.head(self.encode(x))


class Convolution(Classifier):
    """Two blocks of 3x3 filters, each halving the image (edges, then shapes), then a dense layer that knows
    where in the picture each shape was."""

    def __init__(self, size: int, num_classes: int) -> None:
        super().__init__(128, num_classes)

        def block(c_in: int, c_out: int) -> nn.Sequential:
            return nn.Sequential(nn.Conv2d(c_in, c_out, 3, padding=1), nn.BatchNorm2d(c_out), nn.ReLU(), nn.MaxPool2d(2))

        self.features = nn.Sequential(block(3, 32), block(32, 64), nn.Flatten(), nn.Dropout(0.3),
                                      nn.Linear(64 * (size // 4) ** 2, 128), nn.ReLU())

    def encode(self, x: torch.Tensor) -> torch.Tensor:
        return self.features(x)


class Dense(Classifier):
    """Every pixel into one big layer: no idea which pixels are neighbours."""

    def __init__(self, size: int, num_classes: int) -> None:
        super().__init__(128, num_classes)
        self.features = nn.Sequential(nn.Flatten(), nn.Linear(3 * size * size, 256), nn.ReLU(), nn.Linear(256, 128), nn.ReLU())

    def encode(self, x: torch.Tensor) -> torch.Tensor:
        return self.features(x)


class Recurrent(Classifier):
    """Reads the image one row of pixels at a time, top to bottom, and keeps its last state."""

    def __init__(self, kind: str, size: int, num_classes: int, hidden: int = 128) -> None:
        super().__init__(hidden, num_classes)
        cell = {"rnn": nn.RNN, "lstm": nn.LSTM, "gru": nn.GRU}[kind]
        self.rnn = cell(3 * size, hidden, batch_first=True)

    def encode(self, x: torch.Tensor) -> torch.Tensor:
        rows = x.permute(0, 2, 1, 3).flatten(2)  # [batch, rows, 3 * width]
        states, _ = self.rnn(rows)
        return states[:, -1]


class VisionTransformer(Classifier):
    """Cuts the image into 4x4 patches and lets every patch attend to every other one."""

    def __init__(self, size: int, num_classes: int, patch: int = 4, dim: int = 64) -> None:
        super().__init__(dim, num_classes)
        self.patches = nn.Conv2d(3, dim, patch, stride=patch)
        self.position = nn.Parameter(torch.zeros(1, (size // patch) ** 2, dim))
        layer = nn.TransformerEncoderLayer(dim, nhead=4, dim_feedforward=128, dropout=0.1, batch_first=True)
        self.encoder = nn.TransformerEncoder(layer, num_layers=2, enable_nested_tensor=False)

    def encode(self, x: torch.Tensor) -> torch.Tensor:
        tokens = self.patches(x).flatten(2).transpose(1, 2) + self.position
        return self.encoder(tokens).mean(1)


def build(kind: str, size: int, num_classes: int) -> Classifier:
    if kind == "cnn":
        return Convolution(size, num_classes)
    if kind == "mlp":
        return Dense(size, num_classes)
    if kind in ("rnn", "lstm", "gru"):
        return Recurrent(kind, size, num_classes)
    if kind == "transformer":
        return VisionTransformer(size, num_classes)
    raise ValueError(f"unknown model {kind!r}; one of {', '.join(KINDS)}")
