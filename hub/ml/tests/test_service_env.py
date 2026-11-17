"""Imported repo code runs in a child process; it must not inherit the hub's GitHub token."""

import json
import sys

from ml.service import MLService


def test_training_child_does_not_see_github_token(tmp_path, monkeypatch):
    monkeypatch.setenv("GITHUB_TOKEN", "secret-token")
    (tmp_path / ".forge").mkdir()
    probe = "import os, pathlib; pathlib.Path('seen.txt').write_text(os.environ.get('GITHUB_TOKEN', 'none'))"
    (tmp_path / ".forge" / "profile.json").write_text(
        json.dumps({"python": sys.executable, "train_cmd": f'python -c "{probe}"'}))
    svc = MLService(tmp_path)
    svc.start_training(1, "http://hub")
    svc.train_proc.wait(timeout=30)
    assert (tmp_path / "seen.txt").read_text() == "none"
