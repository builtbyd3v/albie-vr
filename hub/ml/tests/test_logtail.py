"""A6 log-tail fallback: a training script that never calls forge still gets live curves from its own logs."""

import json
import sys
import textwrap
import time
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from ml import logtail
from ml.logtail import LogTail
from ml.service import MLService
from ml.tests.test_api import make_app


def write_csv(path: Path, rows: list[dict]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    keys = sorted({k for r in rows for k in r})
    lines = [",".join(keys)] + [",".join(str(r.get(k, "")) for k in keys) for r in rows]
    path.write_text("\n".join(lines) + "\n")


def lightning_rows(epochs: int) -> list[dict]:
    """Lightning CSVLogger shape: sparse rows, both _step and _epoch variants, lr from LearningRateMonitor."""
    rows = []
    for e in range(epochs):
        rows.append({"epoch": e, "step": e * 10 + 5, "train_loss_step": 9.0, "lr-Adam": 0.001})
        rows.append({"epoch": e, "step": e * 10 + 9, "val_loss": 1.0 + 0.1 * e ** 2 - 0.3 * e, "val_acc": 0.5 + 0.05 * e})
        rows.append({"epoch": e, "step": e * 10 + 9, "train_loss_epoch": 2.0 - 0.2 * e})
    return rows


def test_lightning_csv_gives_one_tick_per_epoch_with_epoch_values(tmp_path):
    write_csv(tmp_path / "metrics.csv", lightning_rows(3))
    ticks = logtail.read(tmp_path / "metrics.csv")
    assert [t["epoch"] for t in ticks] == [1, 2, 3]
    assert ticks[0]["train_loss"] == 2.0  # train_loss_epoch wins over train_loss_step
    assert ticks[2]["val_acc"] == pytest.approx(0.6) and ticks[1]["lr"] == 0.001


def test_hf_trainer_state_groups_fractional_epochs(tmp_path):
    state = {"log_history": [
        {"epoch": 0.5, "step": 50, "loss": 2.1, "learning_rate": 5e-5},
        {"epoch": 1.0, "step": 100, "loss": 1.8, "learning_rate": 4e-5},
        {"epoch": 1.0, "step": 100, "eval_loss": 1.6, "eval_accuracy": 0.61, "eval_runtime": 3.2},
        {"epoch": 2.0, "step": 200, "loss": 1.2, "learning_rate": 3e-5},
        {"epoch": 2.0, "step": 200, "eval_loss": 1.4, "eval_accuracy": 0.7},
        {"epoch": 2.0, "step": 200, "train_runtime": 99.0, "train_loss": 1.5},
    ]}
    (tmp_path / "trainer_state.json").write_text(json.dumps(state))
    ticks = logtail.read(tmp_path / "trainer_state.json")
    assert [(t["epoch"], t["train_loss"], t["val_loss"], t["val_acc"]) for t in ticks] == [(1, 1.8, 1.6, 0.61), (2, 1.2, 1.4, 0.7)]
    assert ticks[1]["lr"] == 3e-5 and "extra" not in ticks[0]


def test_tensorboard_files_from_summary_writer(tmp_path):
    tb = pytest.importorskip("torch.utils.tensorboard")
    writer = tb.SummaryWriter(str(tmp_path / "runs" / "exp1"))
    for step in range(1, 31):  # train loss every step, validation every 10 steps (once per "epoch")
        writer.add_scalar("Loss/train", 3.0 - step * 0.05, step)
        if step % 10 == 0:
            writer.add_scalar("Loss/val", 2.0 - step * 0.01, step)
            writer.add_scalar("Accuracy/val", 40 + step, step)  # percent
            writer.add_scalar("lr", 0.01, step)
    writer.close()
    [source] = logtail.find_sources(tmp_path)
    ticks = logtail.read(source)
    assert [t["epoch"] for t in ticks] == [1, 2, 3]  # one tick per validation step
    assert ticks[0]["train_loss"] == pytest.approx(2.5) and ticks[2]["val_acc"] == pytest.approx(0.7)
    assert ticks[1]["val_loss"] == pytest.approx(1.8) and ticks[1]["lr"] == pytest.approx(0.01)


def test_keras_style_train_and_validation_folders(tmp_path):
    tb = pytest.importorskip("torch.utils.tensorboard")
    for split, base in (("train", 1.0), ("validation", 1.5)):
        writer = tb.SummaryWriter(str(tmp_path / "logs" / split))
        for epoch in range(3):
            writer.add_scalar("epoch_loss", base - 0.1 * epoch, epoch)
            writer.add_scalar("epoch_accuracy", 0.5 + 0.1 * epoch, epoch)
        writer.close()
    ticks = logtail.read(logtail.find_sources(tmp_path)[0])
    assert len(ticks) == 3
    assert ticks[2]["train_loss"] == pytest.approx(0.8) and ticks[2]["val_loss"] == pytest.approx(1.3)
    assert ticks[0]["val_acc"] == pytest.approx(0.5)


def test_a_torn_tensorboard_record_ends_the_read_quietly(tmp_path):
    tb = pytest.importorskip("torch.utils.tensorboard")
    writer = tb.SummaryWriter(str(tmp_path))
    for step in range(5):
        writer.add_scalar("loss", 1.0 / (step + 1), step)
    writer.close()
    [source] = logtail.find_sources(tmp_path)
    data = source.read_bytes()
    source.write_bytes(data[:-7])  # the writer is mid-flush
    ticks = logtail.read(source)
    assert 1 <= len(ticks) < 5


def test_tail_sends_only_new_epochs_flags_overfit_and_records_the_run(tmp_path):
    repo, runs = tmp_path / "repo", tmp_path / "repo" / ".forge" / "runs"
    csv_path = repo / "lightning_logs" / "version_0" / "metrics.csv"
    tail = LogTail(repo, runs, "abc12345", since=time.time() - 5, total_epochs=6)
    assert tail.poll() == []

    write_csv(csv_path, lightning_rows(2))
    first = tail.poll()
    assert [t for t, _ in first] == ["ml.run_state", "ml.metrics"] * 2
    assert first[1][1]["run_id"] == "abc12345" and first[1][1]["epoch"] == 1

    write_csv(csv_path, lightning_rows(5))  # val loss now rises while train loss falls
    second = tail.poll()
    metrics = [p for t, p in second if t == "ml.metrics"]
    assert [m["epoch"] for m in metrics] == [3, 4, 5]
    assert [t for t, _ in second].count("ml.overfit_alert") == 1
    assert tail.poll() == []
    assert tail.finish()[0][1]["status"] == "done" and tail.finish() == []

    history = MLService(repo).metrics_history("abc12345")  # recorded like an SDK run: curves after the fact
    assert [m["epoch"] for m in history["metrics"]] == [1, 2, 3, 4, 5] and history["overfit"]
    assert json.loads((runs / "abc12345" / "run.json").read_text())["source"] == "logtail"


def test_tail_stands_down_when_the_sdk_owns_the_run(tmp_path):
    repo, runs = tmp_path / "repo", tmp_path / "repo" / ".forge" / "runs"
    (runs / "sdk00001").mkdir(parents=True)
    (runs / "sdk00001" / "run.json").write_text(json.dumps({"run_id": "sdk00001"}))  # written by forge.watch
    write_csv(repo / "metrics.csv", lightning_rows(2))
    tail = LogTail(repo, runs, "sdk00001", since=0)
    assert tail.poll() == [] and tail.stopped


def sdk_run(runs: Path, run_id: str, events: list[dict]) -> None:
    (runs / run_id).mkdir(parents=True)
    (runs / run_id / "run.json").write_text(json.dumps({"run_id": run_id}))
    (runs / run_id / "events.jsonl").write_text("".join(json.dumps(e) + "\n" for e in events))


def test_an_sdk_run_without_evaluate_is_closed_when_its_process_exits(tmp_path):
    """forge.log without forge.evaluate (e.g. a backtest) never sends done; the tail closes it once."""
    runs = tmp_path / ".forge" / "runs"
    sdk_run(runs, "bt000001", [
        {"t": 0, "type": "ml.run_state", "payload": {"run_id": "bt000001", "status": "training", "epoch": 0, "total_epochs": 0}},
        {"t": 1, "type": "ml.metrics", "payload": {"run_id": "bt000001", "epoch": 1, "extra": {"val_sharpe": 1.2}}},
    ])
    tail = LogTail(tmp_path, runs, "bt000001", since=0)
    assert tail.poll() == [] and tail.stopped
    assert tail.finish() == [("ml.run_state", {"run_id": "bt000001", "status": "done", "epoch": 1, "total_epochs": 1})]
    assert tail.finish() == []
    assert MLService(tmp_path).metrics_history("bt000001")["metrics"][0]["extra"]["val_sharpe"] == 1.2


def test_an_sdk_run_that_finished_itself_is_left_alone(tmp_path):
    runs = tmp_path / ".forge" / "runs"
    sdk_run(runs, "cnn00001", [
        {"t": 0, "type": "ml.metrics", "payload": {"run_id": "cnn00001", "epoch": 3}},
        {"t": 1, "type": "ml.run_state", "payload": {"run_id": "cnn00001", "status": "done", "epoch": 3, "total_epochs": 3}},
    ])
    tail = LogTail(tmp_path, runs, "cnn00001", since=0)
    tail.poll()
    assert tail.finish() == []


def test_follow_mode_starts_a_new_run_for_each_new_log(tmp_path):
    repo, runs = tmp_path / "repo", tmp_path / "repo" / ".forge" / "runs"
    tail = LogTail(repo, runs)
    write_csv(repo / "lightning_logs" / "version_0" / "metrics.csv", lightning_rows(2))
    first_run = [p for t, p in tail.poll() if t == "ml.metrics"][0]["run_id"]
    time.sleep(0.01)
    write_csv(repo / "lightning_logs" / "version_1" / "metrics.csv", lightning_rows(1))
    events = tail.poll()
    assert events[0] == ("ml.run_state", {"run_id": first_run, "status": "done", "epoch": 2, "total_epochs": 2})
    second_run = [p for t, p in events if t == "ml.metrics"][0]["run_id"]
    assert second_run != first_run


TRAIN_WITHOUT_FORGE = textwrap.dedent('''
    import argparse, time
    from pathlib import Path
    parser = argparse.ArgumentParser()
    parser.add_argument("--epochs", type=int)
    epochs = parser.parse_args().epochs
    out = Path("lightning_logs/version_0/metrics.csv")
    out.parent.mkdir(parents=True, exist_ok=True)
    lines = ["epoch,train_loss,val_loss,val_acc"]
    for e in range(epochs):
        time.sleep(0.2)
        lines.append(f"{e},{2 - 0.3 * e},{1.8 - 0.2 * e},{0.4 + 0.1 * e}")
        out.write_text("\\n".join(lines) + "\\n")
''')


def test_train_without_forge_streams_curves_from_its_own_logs(tmp_path, monkeypatch):
    monkeypatch.setattr(logtail, "POLL_S", 0.05)
    repo = tmp_path / "repo"
    (repo / ".forge").mkdir(parents=True)
    (repo / ".forge" / "profile.json").write_text(json.dumps(
        {"archetype": "generic", "python": sys.executable, "train_cmd": "python train.py --epochs 10"}))
    (repo / "train.py").write_text(TRAIN_WITHOUT_FORGE)
    with TestClient(make_app(repo)) as client, client.websocket_connect("/ws") as ws:
        run_id = client.post("/ml/train", json={"epochs": 3}).json()["run_id"]
        seen = []
        while True:
            event = ws.receive_json()
            seen.append(event)
            if event["type"] == "ml.run_state" and event["payload"]["status"] == "done":
                break
    metrics = [e["payload"] for e in seen if e["type"] == "ml.metrics"]
    assert [m["epoch"] for m in metrics] == [1, 2, 3] and all(m["run_id"] == run_id for m in metrics)
    assert metrics[-1]["val_acc"] == pytest.approx(0.6)
    assert seen[-1]["payload"] == {"run_id": run_id, "status": "done", "epoch": 3, "total_epochs": 3}


# --- printed output: GitHub scripts that only print() their progress ------------------------------------------
def test_printed_progress_in_the_usual_formats_becomes_epochs(tmp_path):
    log = tmp_path / "train.log"
    log.write_text(textwrap.dedent("""\
        Loading MNIST...
        Train Epoch: 1 [0/60000 (0%)]\tLoss: 2.301
        Train Epoch: 1 [59520/60000 (99%)]\tLoss: 0.210
        Test set: Average loss: 0.0510, Accuracy: 9837/10000 (98%)
        Train Epoch: 2 [0/60000 (0%)]\tLoss: 0.150
        Test set: Average loss: 0.0400, Accuracy: 9871/10000 (99%)
        """))
    live = logtail.read_stdout(log)
    assert [t["epoch"] for t in live] == [1]  # epoch 2 is still running
    assert live[0]["train_loss"] == pytest.approx(0.21) and live[0]["val_loss"] == pytest.approx(0.051)
    assert live[0]["val_acc"] == pytest.approx(0.9837)
    final = logtail.read_stdout(log, final=True)
    assert [t["epoch"] for t in final] == [1, 2] and final[1]["val_acc"] == pytest.approx(0.9871)

    keras = tmp_path / "keras.log"
    keras.write_text("Epoch 1/2\n1875/1875 - 3s - loss: 0.25 - accuracy: 0.92 - val_loss: 0.12 - val_accuracy: 0.96\n"
                     "Epoch 2/2\n1875/1875 - 3s - loss: 0.10 - accuracy: 0.97 - val_loss: 0.09 - val_accuracy: 0.97\n")
    ticks = logtail.read_stdout(keras, final=True)
    assert [t["epoch"] for t in ticks] == [1, 2]
    assert ticks[0]["train_acc"] == pytest.approx(0.92) and ticks[0]["val_acc"] == pytest.approx(0.96)

    nielsen = tmp_path / "nielsen.log"
    nielsen.write_text("Epoch 0: 9012 / 10000\nEpoch 1: 9234 / 10000\nEpoch 2, accuracy 95.5 %\n")
    ticks = logtail.read_stdout(nielsen, final=True)
    assert [t["epoch"] for t in ticks] == [1, 2, 3]  # 0-based epochs counted from 1
    assert ticks[0]["val_acc"] == pytest.approx(0.9012) and ticks[2]["train_acc"] == pytest.approx(0.955)


PRINTS_ONLY = """\
import time
for epoch in range(3):
    time.sleep(0.1)
    print(f"Epoch {epoch + 1}/3 - loss: {1.0 / (epoch + 1):.3f} - val_accuracy: {0.5 + 0.1 * epoch:.2f}", flush=True)
"""

CRASHES = """\
print("Loading data/mnist.pkl.gz", flush=True)
raise FileNotFoundError("data/mnist.pkl.gz")
"""


def _train_until_done(tmp_path, script: str) -> tuple[str, list[dict]]:
    repo = tmp_path / "repo"
    (repo / ".forge").mkdir(parents=True)
    (repo / ".forge" / "profile.json").write_text(json.dumps(
        {"archetype": "generic", "python": sys.executable, "train_cmd": "python train.py"}))
    (repo / "train.py").write_text(script)
    with TestClient(make_app(repo)) as client, client.websocket_connect("/ws") as ws:
        run_id = client.post("/ml/train", json={"epochs": 3}).json()["run_id"]
        seen = []
        while True:
            event = ws.receive_json()
            seen.append(event)
            if event["type"] == "ml.run_state" and event["payload"]["status"] == "done":
                return run_id, seen


def test_a_script_that_only_prints_streams_curves_and_ends_done(tmp_path, monkeypatch):
    monkeypatch.setattr(logtail, "POLL_S", 0.05)
    run_id, seen = _train_until_done(tmp_path, PRINTS_ONLY)
    metrics = [e["payload"] for e in seen if e["type"] == "ml.metrics"]
    assert [m["epoch"] for m in metrics] == [1, 2, 3] and metrics[-1]["val_acc"] == pytest.approx(0.7)
    assert seen[-1]["payload"] == {"run_id": run_id, "status": "done", "epoch": 3, "total_epochs": 3}


def test_a_crashing_script_ends_done_with_its_error_instead_of_training_forever(tmp_path, monkeypatch):
    monkeypatch.setattr(logtail, "POLL_S", 0.05)
    run_id, seen = _train_until_done(tmp_path, CRASHES)
    done = seen[-1]["payload"]
    assert done["run_id"] == run_id and done["epoch"] == 0
    assert "exit code 1" in done["error"] and "mnist.pkl.gz" in done["error"]


def test_a_cloned_repo_trained_from_the_panel_streams_and_ends_done(tmp_path, monkeypatch):
    """POST /ml/github/train used to start the clone's script with no tail: no curves, TRAINING forever."""
    monkeypatch.setattr(logtail, "POLL_S", 0.05)
    repo = tmp_path / "clone"
    (repo / ".forge").mkdir(parents=True)
    (repo / ".forge" / "profile.json").write_text(json.dumps(
        {"archetype": "generic", "python": sys.executable, "train_cmd": "python train.py"}))
    (repo / "train.py").write_text(PRINTS_ONLY)
    app = make_app(repo)
    svc = app.state.ml
    svc.github_imports["imp123456789"] = {"mode": "clone", "path": str(repo), "repository": "owner/digits"}
    svc.project["status"] = "ready"
    with TestClient(app) as client, client.websocket_connect("/ws") as ws:
        started = client.post("/ml/github/train", json={"import_id": "imp123456789", "epochs": 3})
        assert started.status_code == 200, started.text
        run_id = started.json()["run_id"]
        seen = []
        while not (seen and seen[-1]["type"] == "ml.run_state" and seen[-1]["payload"]["status"] == "done"):
            seen.append(ws.receive_json())
    assert [e["payload"]["epoch"] for e in seen if e["type"] == "ml.metrics"] == [1, 2, 3]
    assert seen[-1]["payload"] == {"run_id": run_id, "status": "done", "epoch": 3, "total_epochs": 3}
