"""Get a cloned GitHub repo ready to run where Albie trains it (ml_github, before setup), with no LLM.

- Notebooks: a repo whose code lives in .ipynb files gets a plain-script copy of each (`<name>_nb.py`, the code
  cells in order, shell `!` and `%` magic lines dropped), so the prescan, the classifier and forge.check can read it.
- Packages: the third-party modules the repo imports (and its requirements.txt names) that this Python lacks are
  pip-installed into the repo's own .venv, made with --system-site-packages so torch and the rest come from the
  host. Wheels only (no sdist builds), one bounded pip run. On when FORGE_ML_PIP=1 (the cloud); the laptop keeps
  its own venvs.
- Frameworks the cloud can't train (TensorFlow, JAX) are reported before any LLM call, so Albie can say so at once.

`foreign_env()` is the environment for every process that runs a repo's own code (check, train, runner, pip):
no keys, tokens or secrets of the host.
"""
from __future__ import annotations

import importlib.util
import json
import os
import re
import subprocess
import sys
from pathlib import Path
from typing import Any

from .prescan import SKIP_DIRS

PIP_TIMEOUT_S = 240
MAX_PACKAGES = 15
MAX_NOTEBOOKS = 12
# import name -> pip name, where they differ
PIP_NAMES = {"PIL": "pillow", "sklearn": "scikit-learn", "cv2": "opencv-python-headless", "yaml": "PyYAML",
             "skimage": "scikit-image", "bs4": "beautifulsoup4", "Bio": "biopython", "dateutil": "python-dateutil",
             "pytorch_lightning": "pytorch-lightning", "lightning": "lightning", "attr": "attrs",
             "google.protobuf": "protobuf", "dotenv": "python-dotenv", "jwt": "PyJWT", "fitz": "PyMuPDF",
             "Levenshtein": "python-Levenshtein", "sentencepiece": "sentencepiece", "torch_geometric": "torch-geometric"}
# frameworks the cloud can't run: a repo built on one of them is reported, not set up
UNSUPPORTED = {"tensorflow": "TensorFlow", "keras": "Keras", "jax": "JAX", "flax": "JAX", "mxnet": "MXNet",
               "paddle": "PaddlePaddle", "theano": "Theano", "caffe": "Caffe"}
# never pip-installed: GPU-only, the host's own torch stack, or not real packages
NEVER = {"torch", "torchvision", "torchaudio", "torchtext", "triton", "apex", "cupy", "pycuda", "tensorrt",
         "google", "setuptools", "pip", "wheel", "distutils", "pkg_resources", "IPython", "ipywidgets", "google.colab",
         "wandb", "comet_ml", "neptune", "mlflow", "tensorboard", "tensorboardX", "deepspeed", "horovod", "xformers",
         "bitsandbytes", "flash_attn"}
SECRET = re.compile(r"KEY|TOKEN|SECRET|PASSWORD|PASS$|CREDENTIAL|AUTH", re.IGNORECASE)
IMPORT_RE = re.compile(r"^\s*(?:from\s+([A-Za-z_][\w]*)[\w.]*\s+import|import\s+([A-Za-z_][\w]*(?:\s*,\s*[A-Za-z_][\w]*)*))",
                       re.MULTILINE)
REQ_RE = re.compile(r"^\s*([A-Za-z0-9][A-Za-z0-9._-]*)")


def foreign_env(drop: tuple[str, ...] = (), **extra: str) -> dict[str, str]:
    """os.environ without anything secret-looking (and without `drop`), plus `extra`: what a repo's code may see."""
    env = {k: v for k, v in os.environ.items() if not SECRET.search(k) and k not in drop}
    env.update(extra)
    return env


def _files(repo: Path, suffix: str):
    for root, dirs, files in os.walk(repo):
        dirs[:] = sorted(d for d in dirs if d not in SKIP_DIRS and not d.startswith("."))
        for name in sorted(files):
            if name.endswith(suffix):
                yield Path(root) / name


def notebooks_to_scripts(repo: Path) -> list[str]:
    """A `<name>_nb.py` next to each notebook (up to MAX_NOTEBOOKS) that has no such script yet. Returns them."""
    written = []
    for nb in list(_files(repo, ".ipynb"))[:MAX_NOTEBOOKS]:
        out = nb.with_name(re.sub(r"[^\w]+", "_", nb.stem).strip("_") + "_nb.py")
        if out.exists():
            continue
        try:
            cells = json.loads(nb.read_text(encoding="utf-8")).get("cells", [])
        except (OSError, ValueError):
            continue
        lines = [f"# Converted by Albie from {nb.name} (code cells in order; shell and magic lines removed)."]
        for cell in cells:
            if cell.get("cell_type") != "code":
                continue
            src = cell.get("source", "")
            src = "".join(src) if isinstance(src, list) else str(src)
            kept = [ln for ln in src.splitlines() if not ln.lstrip().startswith(("!", "%"))]
            if any(ln.strip() for ln in kept):
                lines += ["", *kept]
        if len(lines) > 1:
            out.write_text("\n".join(lines) + "\n", encoding="utf-8")
            written.append(out.relative_to(repo).as_posix())
    return written


def _local_modules(repo: Path) -> set[str]:
    names = set()
    for path in _files(repo, ".py"):
        rel = path.relative_to(repo)
        names.add(path.stem)
        names.update(rel.parts[:-1])
    return names


def imports(repo: Path) -> set[str]:
    """Top-level module names the repo's .py files import (its own modules and the standard library left out)."""
    found: set[str] = set()
    for path in _files(repo, ".py"):
        try:
            text = path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        for m in IMPORT_RE.finditer(text):
            if m.group(1):
                found.add(m.group(1))
            else:
                found.update(n.strip() for n in m.group(2).split(","))
    return found - _local_modules(repo) - set(sys.stdlib_module_names) - {"__future__", "forge"}


def requirements(repo: Path) -> set[str]:
    names = set()
    for req in sorted(repo.glob("requirements*.txt"))[:3]:
        try:
            for line in req.read_text(errors="replace").splitlines():
                if line.strip().startswith(("#", "-", "git+", "http")):
                    continue
                m = REQ_RE.match(line)
                if m:
                    names.add(m.group(1))
        except OSError:
            continue
    return names


def _norm(name: str) -> str:
    return re.sub(r"[-_.]+", "-", name).lower()


def missing(repo: Path, have=importlib.util.find_spec) -> tuple[list[str], list[str]]:
    """(pip names to install, unsupported frameworks the repo is built on)."""
    mods = imports(repo)
    blocked = sorted({UNSUPPORTED[m] for m in mods if m in UNSUPPORTED})
    never = {_norm(n) for n in NEVER} | {_norm(m) for m in UNSUPPORTED}
    want: dict[str, None] = {}
    for mod in sorted(mods):
        if mod in NEVER or mod in UNSUPPORTED:
            continue
        try:
            present = have(mod) is not None
        except (ImportError, ValueError):
            present = False
        if not present:
            want[PIP_NAMES.get(mod, mod)] = None
    for name in sorted(requirements(repo)):
        if _norm(name) in never or _norm(name) in {_norm(w) for w in want}:
            continue
        module = name.replace("-", "_")
        try:
            present = have(module) is not None or have(name) is not None
        except (ImportError, ValueError):
            present = False
        if not present:
            want[name] = None
    return [w for w in want if _norm(w) not in never][:MAX_PACKAGES], blocked


def _venv_python(repo: Path) -> Path:
    return repo / ".venv" / ("Scripts/python.exe" if os.name == "nt" else "bin/python")


def install(repo: Path, packages: list[str], python: str = sys.executable) -> dict[str, Any]:
    """pip-install `packages` into repo/.venv (made with the host's site-packages visible). Wheels only, one run;
    a package pip can't find is skipped (pip is retried without it once). Returns {installed, failed, python}."""
    venv = _venv_python(repo)
    env = foreign_env()
    if not venv.exists():
        if (repo / ".venv").is_symlink() or (repo / ".venv").exists():
            return {"installed": [], "failed": packages, "python": str(venv), "error": "the repo already has a .venv"}
        subprocess.run([python, "-m", "venv", "--system-site-packages", str(repo / ".venv")], check=True,
                       capture_output=True, timeout=60, env=env)
    todo, failed = list(packages), []
    for _ in range(3):
        if not todo:
            break
        proc = subprocess.run([str(venv), "-m", "pip", "install", "-q", "--disable-pip-version-check", "--no-input",
                               "--only-binary=:all:", *todo], capture_output=True, text=True, timeout=PIP_TIMEOUT_S,
                              cwd=repo, env=env)
        if proc.returncode == 0:
            return {"installed": todo, "failed": failed, "python": str(venv)}
        bad = [p for p in todo if re.search(rf"(?i)\b{re.escape(p)}\b", proc.stderr or "")]
        if not bad:
            failed += todo
            return {"installed": [], "failed": failed, "python": str(venv), "error": (proc.stderr or "")[-400:]}
        failed += bad
        todo = [p for p in todo if p not in bad]
    return {"installed": todo, "failed": failed, "python": str(venv)}


class UnsupportedRepo(Exception):
    """The repo is built on a framework the cloud can't train; the message is safe to say."""


def prepare(repo: Path, pip: bool | None = None) -> dict[str, Any]:
    """Notebooks to scripts, then (when `pip`, default FORGE_ML_PIP=1) the missing packages. Raises UnsupportedRepo
    for a TensorFlow/JAX-only repo. Written to .forge/env.json; safe to call again (it only adds)."""
    repo = Path(repo)
    pip = os.environ.get("FORGE_ML_PIP") == "1" if pip is None else pip
    out: dict[str, Any] = {"notebooks": notebooks_to_scripts(repo)}
    packages, blocked = missing(repo)
    uses_torch = any(m in imports(repo) for m in ("torch", "sklearn", "xgboost", "lightgbm"))
    if blocked and not uses_torch:
        raise UnsupportedRepo(f"it's built on {' and '.join(blocked)}, and I can only train PyTorch and scikit-learn "
                              "projects in the cloud")
    out.update(packages=packages, unsupported=blocked)
    if pip and packages:
        out.update(install(repo, packages))
    forge = repo / ".forge"
    forge.mkdir(exist_ok=True)
    if not (forge / ".gitignore").exists():
        (forge / ".gitignore").write_text("*\n")
    (forge / "env.json").write_text(json.dumps(out, indent=1))
    return out
