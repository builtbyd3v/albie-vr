"""GitHub proxy for the headset code browser (PROJECT_CONTEXT section 7.14 / C-21).

GITHUB_TOKEN: a fine-grained PAT with Contents:read, Pull requests:read, Metadata:read,
              scoped to the demo org. Set in hub/.env. Never ships in the APK (D-04, D-30).
GITHUB_USER:  the GitHub login that owns the demo repos. Set in hub/.env.
"""
from __future__ import annotations

import base64
import os

import aiohttp
from fastapi import APIRouter, HTTPException

router = APIRouter(prefix="/code", tags=["code"])

_BASE = "https://api.github.com"


def _headers() -> dict[str, str]:
    token = os.getenv("GITHUB_TOKEN", "")
    return {
        "Authorization": f"Bearer {token}",
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "albie-hub/1",
    }


async def _get(path: str, *, timeout: int = 10) -> dict | list:
    url = path if path.startswith("http") else f"{_BASE}{path}"
    async with aiohttp.ClientSession() as s:
        async with s.get(url, headers=_headers(), timeout=aiohttp.ClientTimeout(total=timeout)) as r:
            if r.status == 404:
                raise HTTPException(404, "not found")
            if r.status >= 400:
                raise HTTPException(r.status, f"GitHub API {r.status}")
            return await r.json()


@router.get("/sources")
async def sources():
    """Repos the hard-coded demo account owns, most-recently-updated first."""
    token = os.getenv("GITHUB_TOKEN", "")
    if not token:
        return {"user": "", "sources": [], "note": "GITHUB_TOKEN not set in hub/.env"}
    data = await _get("/user/repos?type=owner&sort=updated&per_page=30")
    user = os.getenv("GITHUB_USER", data[0]["owner"]["login"] if data else "")
    return {
        "user": user,
        "sources": [
            {
                "id": repo["full_name"],
                "owner": repo["owner"]["login"],
                "name": repo["name"],
                "full_name": repo["full_name"],
                "default_branch": repo["default_branch"],
                "description": repo.get("description") or "",
                "kind": "repo",
                "writable": not repo["fork"],
            }
            for repo in data
        ],
    }


@router.get("/{owner}/{repo}/tree")
async def tree(owner: str, repo: str, path: str = ""):
    data = await _get(f"/repos/{owner}/{repo}/contents/{path}")
    items = data if isinstance(data, list) else [data]
    return {
        "path": path,
        "items": [
            {"name": i["name"], "path": i["path"], "type": i["type"], "size": i.get("size", 0)}
            for i in items
        ],
    }


@router.get("/{owner}/{repo}/file")
async def file(owner: str, repo: str, path: str):
    data = await _get(f"/repos/{owner}/{repo}/contents/{path}")
    if isinstance(data, list) or data.get("type") == "dir":
        raise HTTPException(400, "path is a directory; use /tree")
    size = data.get("size", 0)
    content_b64 = data.get("content", "").replace("\n", "")
    if not content_b64:
        return {"path": path, "sha": data.get("sha", ""), "size": size, "content": "", "truncated": False}
    raw_bytes = base64.b64decode(content_b64)
    truncated = len(raw_bytes) > 256_000
    text = raw_bytes[:256_000].decode("utf-8", errors="replace")
    return {"path": path, "sha": data["sha"], "size": size, "content": text, "truncated": truncated}


@router.get("/{owner}/{repo}/prs")
async def prs(owner: str, repo: str):
    data = await _get(f"/repos/{owner}/{repo}/pulls?state=open&per_page=20")
    return {
        "prs": [
            {
                "number": pr["number"],
                "title": pr["title"],
                "author": pr["user"]["login"],
                "state": pr["state"],
                "created_at": pr["created_at"],
                "body": (pr.get("body") or "")[:400],
                "head": pr["head"]["ref"],
                "base": pr["base"]["ref"],
                "additions": pr.get("additions", 0),
                "deletions": pr.get("deletions", 0),
                "changed_files": pr.get("changed_files", 0),
            }
            for pr in data
        ]
    }


@router.get("/{owner}/{repo}/pr/{number}")
async def pr_detail(owner: str, repo: str, number: int):
    pr = await _get(f"/repos/{owner}/{repo}/pulls/{number}")
    files = await _get(f"/repos/{owner}/{repo}/pulls/{number}/files?per_page=50")
    return {
        "number": pr["number"],
        "title": pr["title"],
        "author": pr["user"]["login"],
        "state": pr["state"],
        "created_at": pr["created_at"],
        "body": (pr.get("body") or "")[:800],
        "head": pr["head"]["ref"],
        "base": pr["base"]["ref"],
        "additions": pr.get("additions", 0),
        "deletions": pr.get("deletions", 0),
        "changed_files": pr.get("changed_files", 0),
        "mergeable": pr.get("mergeable"),
        "files": [
            {
                "filename": f["filename"],
                "status": f["status"],
                "additions": f["additions"],
                "deletions": f["deletions"],
                "patch": f.get("patch", "")[:8000],
            }
            for f in files
        ],
    }
