"""Pull an ML repo from GitHub for ml_github ("Albie, pull up the pytorch cifar repo"), then ml_open sets it up.

Lookups go through the `gh` CLI (`gh api`) when it's installed, so on the laptop they use its GitHub login;
elsewhere (the cloud) they call the REST API with GITHUB_TOKEN. A clone is shallow and lands in
<data>/ml/github/<repo name>; an existing one is reused as it is. GITHUB_TOKEN also lets it clone private repos. FORGE_ML_VENV (a venv with torch, e.g. the
cifar10 example's .venv) is linked in as the clone's .venv, so its setup check and training run with torch.
"""
from __future__ import annotations

import base64
import json
import os
import re
import shutil
import subprocess
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any, Callable

from .service import NotFound

BASE = "https://github.com/"
NAME = re.compile(r"^(?:https?://)?(?:www\.)?(?:github\.com/)?([\w.-]+/[\w.-]+?)(?:\.git)?/?$")


def gh(path: str, params: dict[str, str] | None = None) -> dict[str, Any]:
    if shutil.which("gh") is None:
        return rest(path, params)
    args = ["gh", "api", "-X", "GET", path] + [a for k, v in (params or {}).items() for a in ("-f", f"{k}={v}")]
    out = subprocess.run(args, capture_output=True, text=True, timeout=20)
    if out.returncode != 0:
        raise NotFound(f"GitHub said: {out.stderr.strip()[:200] or 'no answer'}")
    return json.loads(out.stdout)


def rest(path: str, params: dict[str, str] | None = None) -> dict[str, Any]:
    """`gh api` without the CLI: GitHub's REST API, with GITHUB_TOKEN when it's set."""
    url = f"https://api.github.com/{path}" + (f"?{urllib.parse.urlencode(params)}" if params else "")
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "albie-hub/1"}
    token = os.environ.get("GITHUB_TOKEN")
    if token:
        headers["Authorization"] = f"Bearer {token}"
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=20, context=_ssl()) as r:
            return json.loads(r.read())
    except urllib.error.HTTPError as e:
        raise NotFound(f"GitHub said: {e.code} {e.reason}") from None
    except (urllib.error.URLError, TimeoutError) as e:
        raise NotFound(f"GitHub didn't answer: {getattr(e, 'reason', e)}") from None


def _ssl():
    try:
        import ssl

        import certifi
        return ssl.create_default_context(cafile=certifi.where())
    except ImportError:
        return None


def parse(text: str) -> str | None:
    """"owner/repo" from an exact name or a GitHub URL; None for plain words."""
    m = NAME.match(text.strip())
    return m.group(1) if m else None


def find(query: str, api: Callable[..., dict[str, Any]] | None = None) -> dict[str, Any]:
    """The repo the user means: an exact owner/repo, else GitHub's best match for the words."""
    api = api or gh
    name = parse(query)
    if name:
        repo = api(f"repos/{name}")
    else:
        # stars, not best match: spoken words like "pytorch cifar" mean the well-known repo, not an exact-name one
        items = api("search/repositories", {"q": query, "sort": "stars", "per_page": "1"}).get("items") or []
        if not items:
            raise NotFound(f"no GitHub repo matches {query!r}")
        repo = items[0]
    return {"full_name": repo["full_name"], "description": repo.get("description"),
            "stars": repo.get("stargazers_count", 0)}


def clone(full_name: str, root: Path, base: str | None = None) -> Path:
    dest = Path(root) / full_name.split("/")[-1]
    if not dest.is_dir():
        dest.parent.mkdir(parents=True, exist_ok=True)
        auth = []
        token = os.environ.get("GITHUB_TOKEN")
        if token and (base or BASE) == BASE:  # private repos; a header, so the token isn't saved in the clone's config
            basic = base64.b64encode(f"x-access-token:{token}".encode()).decode()
            auth = ["-c", f"http.{BASE}.extraheader=AUTHORIZATION: basic {basic}"]
        out = subprocess.run(["git", *auth, "clone", "-q", "--depth", "1", f"{base or BASE}{full_name}.git", str(dest)],
                             capture_output=True, text=True, timeout=180)
        if out.returncode != 0:
            raise NotFound(f"couldn't download {full_name}: {out.stderr.strip()[:200]}")
    venv = os.environ.get("FORGE_ML_VENV")
    if venv and not any((dest / v).exists() for v in (".venv", "venv", "env")):
        (dest / ".venv").symlink_to(Path(venv).expanduser().resolve())
    return dest
