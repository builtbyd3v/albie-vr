"""Fine-tune the project's pretrained Hugging Face encoder on its dataset, on the CPU.

    python train.py --epochs 3

project.json picks the base model (base_model). Lines marked `# forge` stream the run to the ML desk: the
live curves, the dot wall and the results.
"""

from __future__ import annotations

import argparse
import json

import torch
import torch.nn.functional as F

import forge  # forge
from data import BASE_MODEL, CLASSES, HERE, MAX_LEN, PROJECT, get_loaders, pad_id
from model import build

CHECKPOINT = HERE / "checkpoints" / "best.pt"


def run_epoch(model, loader, opt, scheduler=None) -> tuple[float, float]:
    training = opt is not None
    model.train(training)
    total_loss, correct, n = 0.0, 0, 0
    with torch.set_grad_enabled(training):
        for ids, y in loader:
            out = model(ids)
            loss = F.cross_entropy(out, y)
            if training:
                opt.zero_grad()
                loss.backward()
                torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
                opt.step()
                if scheduler is not None:
                    scheduler.step()
            total_loss += loss.item() * len(y)
            correct += (out.argmax(1) == y).sum().item()
            n += len(y)
    return total_loss / n, correct / n


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--epochs", type=int, default=int(PROJECT.get("epochs_hint", 3)))
    parser.add_argument("--batch-size", type=int, default=int(PROJECT.get("batch_size", 32)))
    parser.add_argument("--lr", type=float, default=float(PROJECT.get("lr", 3e-5)))
    parser.add_argument("--seed", type=int, default=0)
    args = parser.parse_args()

    torch.manual_seed(args.seed)
    torch.set_num_threads(max(1, min(8, torch.get_num_threads())))
    train_loader, val_loader, _ = get_loaders(args.batch_size)
    model = build(BASE_MODEL, len(CLASSES), pad_id(), MAX_LEN)
    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=0.01)
    total = max(1, len(train_loader) * args.epochs)
    warm = max(1, int(total * 0.06))

    def lr_at(step: int) -> float:  # a short warmup, then a straight line to zero (the usual fine-tuning shape)
        return step / warm if step < warm else max(0.0, (total - step) / max(1, total - warm))

    scheduler = torch.optim.lr_scheduler.LambdaLR(opt, lr_at)
    trainable = sum(p.numel() for p in model.parameters() if p.requires_grad)
    print(f"fine-tuning {BASE_MODEL} on {PROJECT['dataset']}: {len(train_loader.dataset)} train, "
          f"{len(val_loader.dataset)} val, {trainable:,} trainable parameters")
    forge.watch(model, val_loader.dataset, classes=CLASSES, epochs=args.epochs)  # forge

    best_acc = 0.0
    for epoch in range(1, args.epochs + 1):
        train_loss, train_acc = run_epoch(model, train_loader, opt, scheduler)
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
