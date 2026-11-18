"""A project Albie built (ml_new) as a GitHub pull request: one branch, one commit, one PR (ml_publish).

The PR goes to FORGE_ML_PUBLISH_REPO (owner/name), else <the token's account>/albie-ml-projects, which is created
(public, with a README) the first time. Each project is a folder, projects/<project folder name>/, on a branch
albie/<project folder name>, so PRs never touch each other. GITHUB_TOKEN is the demo account's token, the one the
code panel uses; it stays on the hub or the cloud.

What goes in: the code (train.py, model.py, data.py, download_data.py, hf_data.py), project.json, README.md with
the last run's results, and .forge/ (profile, adapter). The data goes in too when it's small (text projects);
otherwise download_data.py fetches the same sample from Hugging Face again.

`plan` describes the PR and changes nothing; `publish` does it. ml_publish calls `publish` only after the user said
yes to what `plan` described (the pet's confirmation rule for anything outward-facing).
"""
from __future__ import annotations

import base64
import json
import os
import re
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

API = "https://api.github.com"
TIMEOUT_S = 20
DEFAULT_REPO_NAME = "albie-ml-projects"
CODE = ("train.py", "model.py", "data.py", "download_data.py", "hf_data.py", "project.json", "README.md",
        ".forge/profile.json", ".forge/adapter.py")
MAX_DATA_FILES = 20
MAX_DATA_BYTES = 12_000_000


class PublishError(Exception):
    """Why the PR can't be opened, in words Albie can say."""


def _token() -> str:
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        raise PublishError("GitHub isn't connected here: GITHUB_TOKEN isn't set on Albie's hub or cloud")
    return token


def call(method: str, path: str, body: dict | None = None, ok_missing: bool = False) -> Any:
    """One GitHub REST call. A 404 is None when ok_missing; any other refusal is a PublishError with GitHub's reason."""
    request = urllib.request.Request(API + path, method=method, data=None if body is None else json.dumps(body).encode(),
                                     headers={"Authorization": f"Bearer {_token()}", "Accept": "application/vnd.github+json",
                                              "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "albie-hub/1",
                                              "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=TIMEOUT_S) as answer:
            text = answer.read()
            return json.loads(text) if text else {}
    except urllib.error.HTTPError as e:
        if e.code == 404 and ok_missing:
            return None
        try:
            reason = json.loads(e.read()).get("message")
        except Exception:  # noqa: BLE001 - the code alone is enough
            reason = None
        raise PublishError(f"GitHub said {e.code}: {reason or e.reason}") from None
    except (urllib.error.URLError, TimeoutError):
        raise PublishError("GitHub didn't answer") from None  # no exception text: it can carry request headers


def target_repo() -> str:
    configured = os.environ.get("FORGE_ML_PUBLISH_REPO", "").strip()
    if configured:
        if not re.match(r"^[\w.-]+/[\w.-]+$", configured):
            raise PublishError(f"FORGE_ML_PUBLISH_REPO {configured!r} isn't owner/name")
        return configured
    return f"{call('GET', '/user')['login']}/{DEFAULT_REPO_NAME}"


def files(project: Path) -> tuple[list[tuple[str, bytes]], bool]:
    """(path in the project, bytes) for every file the PR adds, and whether the data went in."""
    out = [(rel, (project / rel).read_bytes()) for rel in CODE if (project / rel).is_file()]
    data = sorted(p for p in (project / "data").rglob("*") if p.is_file()) if (project / "data").is_dir() else []
    small = len(data) <= MAX_DATA_FILES and sum(p.stat().st_size for p in data) <= MAX_DATA_BYTES
    if small:
        out += [(p.relative_to(project).as_posix(), p.read_bytes()) for p in data]
    return out, small and bool(data)


def results_section(project: Path) -> str:
    """The last finished run, as a README section: the score and the curve, epoch by epoch."""
    runs = sorted((p for p in (project / ".forge" / "runs").glob("*") if (p / "summary.json").exists()),
                  key=lambda p: p.stat().st_mtime) if (project / ".forge" / "runs").is_dir() else []
    if not runs:
        return "\n## Results\n\nNot trained yet: `python train.py`.\n"
    run = runs[-1]
    summary = json.loads((run / "summary.json").read_text())
    lines = ["", "## Results", "", f"Run `{run.name}`, trained by Albie on a CPU."]
    best = summary.get("best_val_acc")
    if best is not None:
        lines.append(f"Best validation accuracy: **{best:.1%}**.")
    ticks = []
    events = run / "events.jsonl"
    if events.exists():
        for line in events.read_text().splitlines():
            try:
                e = json.loads(line)
            except ValueError:
                continue
            if e.get("type") == "ml.metrics":
                ticks.append(e.get("payload") or {})
    if ticks:
        lines += ["", "| Epoch | Train loss | Train acc | Val loss | Val acc |", "|---|---|---|---|---|"]
        for t in ticks:
            cell = lambda v, pct=False: "" if v is None else (f"{v:.1%}" if pct else f"{v:.3f}")  # noqa: E731
            lines.append(f"| {t.get('epoch')} | {cell(t.get('train_loss'))} | {cell(t.get('train_acc'), True)} | "
                         f"{cell(t.get('val_loss'))} | {cell(t.get('val_acc'), True)} |")
    return "\n".join(lines) + "\n"


def plan(project: Path) -> dict[str, Any]:
    """What publish would do, with nothing changed on GitHub (one or two reads)."""
    project = Path(project)
    if not (project / "project.json").is_file():
        raise PublishError("the open ML project isn't one Albie built; start one with ml_new first")
    meta = json.loads((project / "project.json").read_text())
    repo = target_repo()
    exists = call("GET", f"/repos/{repo}", ok_missing=True) is not None
    found, with_data = files(project)
    return {"repo": repo, "create_repo": not exists, "branch": f"albie/{project.name}",
            "folder": f"projects/{project.name}", "title": f"Add {meta['title']}", "files": len(found),
            "with_data": with_data, "project": project.name,
            "paths": [path for path, _ in found if not path.startswith("data/")][:16]}  # the confirm card lists them


def publish(project: Path, expected: dict[str, Any]) -> dict[str, Any]:
    """Create the repo if needed, then the branch, the commit and the PR described by `expected` (from plan)."""
    project = Path(project)
    meta = json.loads((project / "project.json").read_text())
    repo, branch, folder = expected["repo"], expected["branch"], expected["folder"]
    owner, name = repo.split("/", 1)
    info = call("GET", f"/repos/{repo}", ok_missing=True)
    if info is None:
        me = call("GET", "/user")["login"]
        description = "ML projects Albie built and trained in VR, one folder and one pull request each."
        info = (call("POST", "/user/repos", {"name": name, "description": description, "auto_init": True})
                if owner.lower() == me.lower()
                else call("POST", f"/orgs/{owner}/repos", {"name": name, "description": description, "auto_init": True}))
    base = info["default_branch"]
    head = call("GET", f"/repos/{repo}/git/ref/heads/{base}")["object"]["sha"]
    base_tree = call("GET", f"/repos/{repo}/git/commits/{head}")["tree"]["sha"]

    found, with_data = files(project)
    readme = dict(found).get("README.md", b"").decode() + results_section(project)
    if not with_data:
        readme += ("\n## Data\n\nThe sample isn't in the repo (too many files). `python download_data.py` "
                   "fetches the same one from Hugging Face.\n")
    found = [(rel, readme.encode() if rel == "README.md" else body) for rel, body in found]
    tree = []
    for rel, body in found:
        blob = call("POST", f"/repos/{repo}/git/blobs", {"content": base64.b64encode(body).decode(), "encoding": "base64"})
        tree.append({"path": f"{folder}/{rel}", "mode": "100644", "type": "blob", "sha": blob["sha"]})
    new_tree = call("POST", f"/repos/{repo}/git/trees", {"base_tree": base_tree, "tree": tree})["sha"]
    commit = call("POST", f"/repos/{repo}/git/commits", {"message": f"{expected['title']}\n\nBuilt and trained by Albie (ml_new).",
                                                          "tree": new_tree, "parents": [head]})["sha"]
    if call("GET", f"/repos/{repo}/git/ref/heads/{branch}", ok_missing=True) is None:
        call("POST", f"/repos/{repo}/git/refs", {"ref": f"refs/heads/{branch}", "sha": commit})
    else:
        call("PATCH", f"/repos/{repo}/git/refs/heads/{branch}", {"sha": commit, "force": True})  # Albie's own branch
    classes = ", ".join(meta["classes"][:10]) + (" ..." if len(meta["classes"]) > 10 else "")
    body = (f"{meta['title']}, built by Albie in VR from the Hugging Face dataset "
            f"[{meta['dataset']}](https://huggingface.co/datasets/{meta['dataset']}) (license: {meta.get('license') or 'not stated'}).\n\n"
            f"- Model: `{meta['model']}` in `model.py`\n- Classes: {classes}\n"
            f"- Data: {meta['rows']['train']} training and {meta['rows']['val']} validation rows"
            f"{' (in the PR)' if with_data else ' (download_data.py fetches them)'}\n"
            + results_section(project))
    existing = call("GET", f"/repos/{repo}/pulls?state=open&head={owner}:{branch}")
    pr = existing[0] if existing else call("POST", f"/repos/{repo}/pulls",
                                           {"title": expected["title"], "head": branch, "base": base, "body": body})
    return {"repo": repo, "number": pr["number"], "url": pr["html_url"], "branch": branch, "files": len(found),
            "created_repo": expected.get("create_repo", False)}
