"""GitHub proxy for the headset code browser (PROJECT_CONTEXT section 7.14 / C-21).

Limited to repos the demo account owns. Reading is GET /code/...; the two writes (approve and merge a pull
request) are POSTs that need {"confirm": true}, sent only after the user confirmed on the device or to Albie.
Albie's tools on the same functions are in code_browser/tools.py.

CODE_GITHUB_TOKEN: the demo account's token, set in hub/.env. It never ships in the APK (D-04, D-30) and never
appears in a response. It is taken out of the process environment on first use, so child processes started
afterwards (ML training scripts from imported repos) don't inherit it.
"""
from __future__ import annotations

import asyncio
import base64
import os
from urllib.parse import quote

import aiohttp
from fastapi import APIRouter, HTTPException, Request
from pydantic import BaseModel

router = APIRouter(prefix="/code", tags=["code"])

_BASE = "https://api.github.com"
_MAX_FILE = 256_000
_MAX_PATCH = 8_000

_token_value: str | None = None
_owned: dict[str, dict] = {}  # "owner/name" in lower case -> the repo as GitHub returned it
current_repo = ""  # the repo the device last read: what "this repo" means to Albie's tools


class Source(BaseModel):
    id: str
    owner: str
    name: str
    full_name: str
    default_branch: str
    description: str = ""
    kind: str = "repo"
    writable: bool = False


class Sources(BaseModel):
    user: str = ""
    sources: list[Source] = []
    note: str = ""


class TreeItem(BaseModel):
    name: str
    path: str
    type: str
    size: int = 0


class Tree(BaseModel):
    path: str
    items: list[TreeItem]


class FileContent(BaseModel):
    path: str
    sha: str = ""
    size: int = 0
    content: str = ""
    truncated: bool = False
    binary: bool = False
    note: str = ""


class PullSummary(BaseModel):
    number: int
    title: str
    author: str
    state: str
    created_at: str
    body: str = ""
    head: str
    base: str


class PullFile(BaseModel):
    filename: str
    status: str
    additions: int = 0
    deletions: int = 0
    patch: str = ""
    truncated: bool = False


class Pulls(BaseModel):
    prs: list[PullSummary]


class PullDetail(PullSummary):
    additions: int = 0
    deletions: int = 0
    changed_files: int = 0
    mergeable: bool | None = None
    files: list[PullFile] = []


class Confirm(BaseModel):
    confirm: bool = False


class Done(BaseModel):
    ok: bool = True
    note: str = ""


def _token() -> str:
    global _token_value
    if _token_value is None:
        _token_value = os.environ.pop("CODE_GITHUB_TOKEN", "")
    return _token_value


async def _get(session: aiohttp.ClientSession, path: str) -> dict | list:
    try:
        async with session.get(_BASE + path) as r:
            if r.status == 404:
                raise HTTPException(404, "not found")
            if r.status >= 400:
                raise HTTPException(502, f"GitHub API {r.status}")
            return await r.json()
    except (aiohttp.ClientError, asyncio.TimeoutError):
        # No exception text: an aiohttp error can carry the request headers.
        raise HTTPException(502, "GitHub unreachable") from None


async def _write(session: aiohttp.ClientSession, method: str, path: str, body: dict) -> dict:
    """A write to GitHub. A refusal comes back as 409 with GitHub's own reason, so the user hears why."""
    try:
        async with session.request(method, _BASE + path, json=body) as r:
            data = await r.json(content_type=None)
            if r.status >= 400:
                reason = data.get("message") if isinstance(data, dict) else None
                errors = data.get("errors") if isinstance(data, dict) else None
                if errors and isinstance(errors[0], str):
                    reason = errors[0]
                raise HTTPException(409, str(reason or f"GitHub API {r.status}")[:200])
            return data if isinstance(data, dict) else {}
    except (aiohttp.ClientError, asyncio.TimeoutError, ValueError):
        raise HTTPException(502, "GitHub unreachable") from None


def _session() -> aiohttp.ClientSession:
    token = _token()
    if not token:
        raise HTTPException(503, "CODE_GITHUB_TOKEN not set in hub/.env")
    return aiohttp.ClientSession(
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "albie-hub/1",
        },
        timeout=aiohttp.ClientTimeout(total=10),
    )


async def _load_owned(session: aiohttp.ClientSession) -> None:
    data = await _get(session, "/user/repos?type=owner&sort=updated&per_page=100")
    _owned.clear()
    for repo in data:
        _owned[repo["full_name"].lower()] = repo


async def _repo(session: aiohttp.ClientSession, owner: str, name: str) -> str:
    """The API path of a repo the demo account owns. Anything else is a 404, whatever the token could read."""
    key = f"{owner}/{name}".lower()
    if key not in _owned:
        await _load_owned(session)
    if key not in _owned:
        raise HTTPException(404, "not found")
    return "/repos/" + _owned[key]["full_name"]


def _contents_path(path: str) -> str:
    parts = [p for p in path.strip("/").split("/") if p]
    if any(p in (".", "..") for p in parts):
        raise HTTPException(400, "bad path")
    return "/contents/" + "/".join(quote(p, safe="") for p in parts)


def _summary(pr: dict, body_limit: int) -> dict:
    return {
        "number": pr["number"],
        "title": pr["title"],
        "author": pr["user"]["login"],
        "state": pr["state"],
        "created_at": pr["created_at"],
        "body": (pr.get("body") or "")[:body_limit],
        "head": pr["head"]["ref"],
        "base": pr["base"]["ref"],
    }


def _patch(text: str) -> tuple[str, bool]:
    if len(text) <= _MAX_PATCH:
        return text, False
    return text[: text.rfind("\n", 0, _MAX_PATCH)], True


async def list_sources() -> Sources:
    if not _token():
        return Sources(note="CODE_GITHUB_TOKEN not set in hub/.env")
    async with _session() as s:
        await _load_owned(s)
    repos = list(_owned.values())
    return Sources(
        user=repos[0]["owner"]["login"] if repos else "",
        sources=[
            Source(
                id=r["full_name"],
                owner=r["owner"]["login"],
                name=r["name"],
                full_name=r["full_name"],
                default_branch=r["default_branch"],
                description=r.get("description") or "",
                writable=bool((r.get("permissions") or {}).get("push")),
            )
            for r in repos
        ],
    )


async def read_tree(owner: str, repo: str, path: str = "") -> Tree:
    async with _session() as s:
        data = await _get(s, await _repo(s, owner, repo) + _contents_path(path))
    items = data if isinstance(data, list) else [data]
    return Tree(
        path=path.strip("/"),
        items=[TreeItem(name=i["name"], path=i["path"], type=i["type"], size=i.get("size") or 0) for i in items],
    )


async def read_file(owner: str, repo: str, path: str) -> FileContent:
    async with _session() as s:
        data = await _get(s, await _repo(s, owner, repo) + _contents_path(path))
    if isinstance(data, list) or data.get("type") == "dir":
        raise HTTPException(400, "path is a directory; use /tree")
    out = FileContent(path=data.get("path", path), sha=data.get("sha") or "", size=data.get("size") or 0)
    if data.get("type") != "file":
        out.note = f"A {data.get('type')} can't be shown."
        return out
    if data.get("encoding") != "base64":  # GitHub leaves the content out above 1 MB
        out.truncated = out.size > 0
        out.note = "File is too large to show." if out.truncated else ""
        return out
    raw = base64.b64decode(data.get("content") or "")
    if b"\0" in raw[:8000]:
        out.binary = True
        out.note = "Binary file."
        return out
    out.truncated = len(raw) > _MAX_FILE
    out.content = raw[:_MAX_FILE].decode("utf-8", errors="replace")
    if out.truncated:
        out.note = "File truncated at 256 KB."
    return out


async def list_prs(owner: str, repo: str) -> Pulls:
    async with _session() as s:
        data = await _get(s, await _repo(s, owner, repo) + "/pulls?state=open&per_page=20")
    return Pulls(prs=[PullSummary(**_summary(pr, 400)) for pr in data])


async def read_pr(owner: str, repo: str, number: int) -> PullDetail:
    async with _session() as s:
        base = f"{await _repo(s, owner, repo)}/pulls/{number}"
        pr, files = await asyncio.gather(_get(s, base), _get(s, base + "/files?per_page=100"))
    out = PullDetail(
        **_summary(pr, 800),
        additions=pr.get("additions") or 0,
        deletions=pr.get("deletions") or 0,
        changed_files=pr.get("changed_files") or 0,
        mergeable=pr.get("mergeable"),
    )
    for f in files:
        patch, cut = _patch(f.get("patch") or "")
        out.files.append(
            PullFile(
                filename=f["filename"],
                status=f["status"],
                additions=f.get("additions") or 0,
                deletions=f.get("deletions") or 0,
                patch=patch,
                truncated=cut,
            )
        )
    return out


async def approve_pr(owner: str, repo: str, number: int) -> Done:
    """Approve a pull request as the demo account. GitHub refuses an account approving its own pull request."""
    async with _session() as s:
        await _write(s, "POST", f"{await _repo(s, owner, repo)}/pulls/{number}/reviews", {"event": "APPROVE"})
    return Done(note=f"Pull request #{number} approved.")


async def merge_pr(owner: str, repo: str, number: int) -> Done:
    async with _session() as s:
        data = await _write(s, "PUT", f"{await _repo(s, owner, repo)}/pulls/{number}/merge", {})
    return Done(ok=bool(data.get("merged")), note=str(data.get("message") or f"Pull request #{number} merged."))


def _seen(owner: str, repo: str) -> None:
    global current_repo
    current_repo = f"{owner}/{repo}"


def _confirmed(request: Request, body: Confirm) -> None:
    # The headset never sends Origin; a browser always does on a cross-site POST. The hub allows any CORS origin,
    # so without this a web page open on the same network could approve or merge.
    if request.headers.get("origin"):
        raise HTTPException(403, "writes are not accepted from a browser")
    if not body.confirm:
        raise HTTPException(400, "confirm must be true: ask the user first")


@router.get("/sources", response_model=Sources)
async def sources() -> Sources:
    """Repos the demo account owns, most recently updated first."""
    return await list_sources()


@router.get("/{owner}/{repo}/tree", response_model=Tree)
async def tree(owner: str, repo: str, path: str = "") -> Tree:
    out = await read_tree(owner, repo, path)
    _seen(owner, repo)
    return out


@router.get("/{owner}/{repo}/file", response_model=FileContent)
async def file(owner: str, repo: str, path: str) -> FileContent:
    return await read_file(owner, repo, path)


@router.get("/{owner}/{repo}/prs", response_model=Pulls)
async def prs(owner: str, repo: str) -> Pulls:
    return await list_prs(owner, repo)


@router.get("/{owner}/{repo}/pr/{number}", response_model=PullDetail)
async def pr_detail(owner: str, repo: str, number: int) -> PullDetail:
    return await read_pr(owner, repo, number)


@router.post("/{owner}/{repo}/pr/{number}/approve", response_model=Done)
async def approve(owner: str, repo: str, number: int, body: Confirm, request: Request) -> Done:
    _confirmed(request, body)
    return await approve_pr(owner, repo, number)


@router.post("/{owner}/{repo}/pr/{number}/merge", response_model=Done)
async def merge(owner: str, repo: str, number: int, body: Confirm, request: Request) -> Done:
    _confirmed(request, body)
    return await merge_pr(owner, repo, number)
