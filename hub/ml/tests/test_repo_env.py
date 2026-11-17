"""ml_github on any repo: notebooks become scripts, missing packages go into the repo's venv, TensorFlow-only repos are
named before any LLM call, folder URLs open one example, and the repo's code never sees the host's secrets."""
import json
import subprocess
import sys

import pytest

from ml import github, repo_env
from ml.setup.adapt import run_check


def notebook(cells):
    return json.dumps({"cells": [{"cell_type": t, "source": s} for t, s in cells], "metadata": {}, "nbformat": 4})


def test_a_notebook_becomes_a_script_without_shell_or_magic_lines(tmp_path):
    (tmp_path / "1 - Train RNN.ipynb").write_text(notebook([
        ("markdown", "# Title"), ("code", ["!pip install torchtext\n", "import torch\n", "%matplotlib inline\n"]),
        ("code", "x = torch.zeros(2)\nprint(x)")]))
    assert repo_env.notebooks_to_scripts(tmp_path) == ["1_Train_RNN_nb.py"]
    script = (tmp_path / "1_Train_RNN_nb.py").read_text()
    assert "import torch" in script and "print(x)" in script
    assert "!pip" not in script and "%matplotlib" not in script and "# Title" not in script
    compile(script, "nb", "exec")
    assert repo_env.notebooks_to_scripts(tmp_path) == []  # kept as it is on a second call


def test_missing_lists_third_party_imports_only(tmp_path):
    (tmp_path / "train.py").write_text("import os, json\nimport torch\nfrom models import Net\nimport cv2\n"
                                       "from sklearn.metrics import f1_score\nimport somethingnotinstalled\n")
    (tmp_path / "models").mkdir()
    (tmp_path / "models" / "__init__.py").write_text("Net = None\n")
    (tmp_path / "requirements.txt").write_text("torch==1.4\nnumpy>=1.0\nalbumentations==1.3.0\n# comment\n-e .\n")
    installed = {"torch", "numpy", "os", "json"}
    packages, blocked = repo_env.missing(tmp_path, have=lambda m: object() if m in installed else None)
    assert packages == ["opencv-python-headless", "scikit-learn", "somethingnotinstalled", "albumentations"]
    assert blocked == []  # torch and its pins are never reinstalled; local `models` is not a package


def test_a_tensorflow_only_repo_is_named_before_setup(tmp_path):
    (tmp_path / "train.py").write_text("import tensorflow as tf\nfrom keras import layers\n")
    with pytest.raises(repo_env.UnsupportedRepo, match="TensorFlow"):
        repo_env.prepare(tmp_path, pip=False)
    (tmp_path / "torch_version.py").write_text("import torch\n")
    assert repo_env.prepare(tmp_path, pip=False)["unsupported"] == ["Keras", "TensorFlow"]  # torch code too: try it


def test_prepare_installs_into_the_repos_own_venv(tmp_path, monkeypatch):
    calls = []

    def run(argv, **kw):
        calls.append(argv)
        if argv[1:3] == ["-m", "venv"]:
            (tmp_path / ".venv" / "bin").mkdir(parents=True)
            (tmp_path / ".venv" / "bin" / "python").write_text("")
        return subprocess.CompletedProcess(argv, 0, "", "")

    monkeypatch.setattr(repo_env.subprocess, "run", run)
    monkeypatch.setattr(repo_env, "missing", lambda repo: (["tqdm", "einops"], []))
    out = repo_env.prepare(tmp_path, pip=True)
    assert calls[0][1:4] == ["-m", "venv", "--system-site-packages"]  # the host's torch stays visible
    assert calls[1][-2:] == ["tqdm", "einops"] and "--only-binary=:all:" in calls[1]
    assert out["installed"] == ["tqdm", "einops"] and out["python"].endswith(".venv/bin/python")
    assert json.loads((tmp_path / ".forge" / "env.json").read_text())["installed"] == ["tqdm", "einops"]


def test_a_package_pip_cant_find_is_skipped_and_the_rest_installed(tmp_path, monkeypatch):
    (tmp_path / ".venv" / "bin").mkdir(parents=True)
    (tmp_path / ".venv" / "bin" / "python").write_text("")
    tries = []

    def run(argv, **kw):
        tries.append(argv[-2:])
        if "nosuchpkg" in argv:
            return subprocess.CompletedProcess(argv, 1, "", "ERROR: No matching distribution found for nosuchpkg")
        return subprocess.CompletedProcess(argv, 0, "", "")

    monkeypatch.setattr(repo_env.subprocess, "run", run)
    out = repo_env.install(tmp_path, ["tqdm", "nosuchpkg"])
    assert out == {"installed": ["tqdm"], "failed": ["nosuchpkg"], "python": str(tmp_path / ".venv/bin/python")}


def test_folder_urls_open_one_example_of_a_repo():
    assert github.parse_folder("https://github.com/pytorch/examples/tree/main/mnist") == ("pytorch/examples", "mnist")
    assert github.parse_folder("github.com/pytorch/examples/blob/main/mnist/main.py") == ("pytorch/examples", "mnist")
    assert github.parse_folder("github.com/o/r/tree/main/a/b/") == ("o/r", "a/b")
    assert github.parse_folder("github.com/o/r/tree/main/../../etc") is None
    assert github.parse_folder("kuangliu/pytorch-cifar") is None
    gh = lambda path, params=None: {"full_name": path.removeprefix("repos/"), "stargazers_count": 1}  # noqa: E731
    assert github.find("https://github.com/pytorch/examples/tree/main/mnist", gh)["folder"] == "mnist"
    assert "folder" not in github.find("pytorch/examples", gh)


def test_the_repos_code_never_sees_keys_or_tokens(tmp_path, monkeypatch):
    for name in ("ALBIE_APP_KEY", "GITHUB_TOKEN", "HF_TOKEN", "ANTHROPIC_API_KEY", "SOME_SECRET"):
        monkeypatch.setenv(name, "secret")
    monkeypatch.setenv("FORGE_MAX_TRAIN", "4000")
    env = repo_env.foreign_env(drop=("FORGE_MAX_TRAIN",), EXTRA="1")
    assert not {"ALBIE_APP_KEY", "GITHUB_TOKEN", "HF_TOKEN", "ANTHROPIC_API_KEY", "SOME_SECRET", "FORGE_MAX_TRAIN"} & set(env)
    assert env["EXTRA"] == "1" and env["PATH"]
    # forge.check runs the repo's adapter in this environment too
    (tmp_path / ".forge").mkdir()
    probe = tmp_path / "probe.py"
    probe.write_text("import os, pathlib; pathlib.Path('seen.txt').write_text(os.environ.get('ALBIE_APP_KEY', 'none'))")
    real = subprocess.run
    monkeypatch.setattr("ml.setup.adapt.subprocess.run", lambda argv, **kw: real([sys.executable, str(probe)], **kw))
    run_check(tmp_path, sys.executable)
    assert (tmp_path / "seen.txt").read_text() == "none"
