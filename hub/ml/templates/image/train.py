"""Train the project's image classifier on its Hugging Face dataset, on the CPU.

    python train.py --epochs 8

project.json picks the model (cnn, mlp, rnn, lstm, gru or transformer). Lines marked `# forge` stream
the run to the ML desk: the live curves, the dot wall and the results.
"""

from __future__ import annotations

import argparse
import json

import torch
import torch.nn.functional as F

import forge  # forge
from data import CLASSES, HERE, PROJECT, SIZE, get_loaders
from model import build

CHECKPOINT = HERE / "checkpoints" / "best.pt"


def run_epoch(model, loader, opt) -> tuple[float, float]:
    training = opt is not None
    model.train(training)
    total_loss, correct, n = 0.0, 0, 0
    with torch.set_grad_enabled(training):
        for x, y in loader:
            out = model(x)
            loss = F.cross_entropy(out, y)
            if training:
                opt.zero_grad()
                loss.backward()
                torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)  # keeps a plain RNN from blowing up
                opt.step()
            total_loss += loss.item() * len(y)
            correct += (out.argmax(1) == y).sum().item()
            n += len(y)
    return total_loss / n, correct / n


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--epochs", type=int, default=8)
    parser.add_argument("--batch-size", type=int, default=64)
    parser.add_argument("--lr", type=float, default=float(PROJECT.get("lr", 2e-3)))
    parser.add_argument("--seed", type=int, default=0)
    args = parser.parse_args()

    torch.manual_seed(args.seed)
    torch.set_num_threads(max(1, min(8, torch.get_num_threads())))
    train_loader, val_loader = get_loaders(args.batch_size)
    model = build(PROJECT["model"], SIZE, len(CLASSES))
    opt = torch.optim.Adam(model.parameters(), lr=args.lr)
    print(f"{PROJECT['model']} on {PROJECT['dataset']}: {len(train_loader.dataset)} train, "
          f"{len(val_loader.dataset)} val, {sum(p.numel() for p in model.parameters()):,} parameters")
    forge.watch(model, val_loader.dataset, classes=CLASSES, epochs=args.epochs)  # forge

    best_acc = 0.0
    for epoch in range(1, args.epochs + 1):
        train_loss, train_acc = run_epoch(model, train_loader, opt)
        val_loss, val_acc = run_epoch(model, val_loader, None)
        print(f"epoch {epoch}/{args.epochs}  train {train_loss:.3f} / {train_acc:.3f}  val {val_loss:.3f} / {val_acc:.3f}")
        forge.log(epoch=epoch, train_loss=train_loss, train_acc=train_acc, val_loss=val_loss,  # forge
                  val_acc=val_acc, lr=opt.param_groups[0]["lr"], model=model)  # forge
        if val_acc > best_acc:
            best_acc = val_acc
            CHECKPOINT.parent.mkdir(exist_ok=True)
            torch.save(model.state_dict(), CHECKPOINT)
            CHECKPOINT.with_suffix(".json").write_text(json.dumps({"val_acc": val_acc, "epoch": epoch}))

    model.load_state_dict(torch.load(CHECKPOINT, map_location="cpu"))  # the results show the best epoch, the one Albie reports
    forge.evaluate(model, val_loader)  # forge
    print(f"best val acc {best_acc:.3f}, saved to {CHECKPOINT}")


if __name__ == "__main__":
    main()
