"""Albie's source control tools with the hub on: the demo account's repos and pull requests in the code panel.

Each tool reads GitHub through code_browser (so Albie can talk about what it found) and publishes ``code.view``,
which the headset's code panel follows. The two writes, approve and merge, take two calls: the first only describes
the pull request and tells Albie to ask; the second, with confirm true, acts, and only for the pull request that
was just described. Registered from main.py (the hub) and cloud-py/app.py (one per headset), each passing its
app's state, which holds the repo the code panel shows and the approve or merge waiting for a yes.
"""
from __future__ import annotations

from typing import Any, Awaitable, Callable

from fastapi import HTTPException
from pydantic import BaseModel

import code_browser as cb

Show = Callable[[str], Awaitable[dict[str, Any]]]


class CodeView(BaseModel):
    """hub -> clients: show this in the code panel. Only `repo` is always set."""
    repo: str                       # owner/name
    view: str | None = None         # "files" or "prs": switch to the repo's files or its pull request list
    path: str | None = None         # a folder to list ("" = the top)
    file: str | None = None         # a file to open
    pr: int | None = None           # a pull request to open
    link_project: bool = False      # also link this repo to the project open on the device


_REPO = {"type": "string", "description": "repo name or owner/name; leave out for the repo that is open in the code panel"}
_PR = {"type": "integer", "minimum": 1, "description": "pull request number, e.g. 5"}
_CONFIRM = {"type": "boolean", "description": "true only after the user said yes to exactly this pull request"}

TOOLS: list[dict[str, Any]] = [
    {"name": "github_list_repos",
     "description": "The repos in the user's connected source control account, and which one is open in the code panel. "
                    "These are the user's own code repos. To pull a public ML repo for training, use ml_github instead.",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "github_open_repo",
     "description": "Open one of the account's repos in the code panel ('open the truck repo', 'show me the code'). "
                    "Returns its top folder so you can say what is in it.",
     "parameters": {"type": "object", "properties": {"repo": _REPO}}},
    {"name": "github_open_file",
     "description": "Open a file from a repo in the code panel and get its text, so you can explain it. Read-only.",
     "parameters": {"type": "object", "properties": {
         "path": {"type": "string", "description": "file path in the repo, e.g. src/controller.py"}, "repo": _REPO},
         "required": ["path"]}},
    {"name": "github_list_prs",
     "description": "Show a repo's open pull requests in the code panel and get the list.",
     "parameters": {"type": "object", "properties": {"repo": _REPO}}},
    {"name": "github_show_pr",
     "description": "Show one pull request in the code panel (summary, changed files, diffs) and get its description "
                    "and changes, so you can walk the user through it. Pass file to open one changed file's diff.",
     "parameters": {"type": "object", "properties": {
         "number": _PR, "repo": _REPO,
         "file": {"type": "string", "description": "optional: a changed file to show the diff of"}},
         "required": ["number"]}},
    {"name": "github_close_pr",
     "description": "Leave the pull requests and go back to the repo's files in the code panel.",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "github_approve_pr",
     "description": "Approve a pull request as the user's account. This is posted in source control for everyone to see. "
                    "Call it first without confirm: nothing is posted, and you get the pull request to read back. "
                    "Say its number and title and ask 'approve it?'. Only after a clear yes, call again with confirm true.",
     "parameters": {"type": "object", "properties": {"number": _PR, "repo": _REPO, "confirm": _CONFIRM},
                    "required": ["number"]}},
    {"name": "github_merge_pr",
     "description": "Merge a pull request into its base branch. This changes the repo and can't be undone from here. "
                    "Call it first without confirm: nothing is merged, and you get the pull request to read back. "
                    "Say its number, title and the branch it goes into and ask 'merge it?'. Only after a clear yes, "
                    "call again with confirm true.",
     "parameters": {"type": "object", "properties": {"number": _PR, "repo": _REPO, "confirm": _CONFIRM},
                    "required": ["number"]}},
    {"name": "github_link_repo",
     "description": "Link a repo to the project that is open, so the code panel opens that repo whenever this "
                    "project is open ('this repo is for the truck project').",
     "parameters": {"type": "object", "properties": {"repo": _REPO}}},
]

async def _resolve(repo: str | None, shown: str) -> tuple[str, str]:
    """(owner, name) of an owned repo, by full name or name; default: the one the code panel has open."""
    found = await cb.list_sources()
    if not found.sources:
        raise HTTPException(503, found.note or "The GitHub account has no repos.")
    wanted = (repo or shown or found.sources[0].id).strip().lower()
    for s in found.sources:
        if wanted in (s.id.lower(), s.name.lower()):
            return s.owner, s.name
    names = ", ".join(s.name for s in found.sources)
    raise HTTPException(404, f"There is no repo '{repo}'. The account has: {names}.")


async def call_tool(name: str, args: dict[str, Any], bus: Any, state: Any, show: Show | None = None) -> dict[str, Any]:
    """Run one tool. Errors come back as {"error": ...} so the model can say them."""
    try:
        return await _call(name, args, bus, state, show)
    except HTTPException as e:
        return {"error": str(e.detail)}


async def _call(name: str, args: dict[str, Any], bus: Any, state: Any, show: Show | None) -> dict[str, Any]:
    # (tool, repo, number) Albie was told to ask the user about; any other tool call in between cancels it
    pending = getattr(state, "code_pending", None)
    state.code_pending = None
    shown_repo = getattr(state, "code_repo", "")

    async def view(**fields: Any) -> None:
        if show is not None:
            await show("code")
        await bus.publish("code.view", CodeView(repo=full, **fields))

    if name == "github_list_repos":
        found = await cb.list_sources()
        if found.note:
            return {"error": found.note}
        return {"repos": [{"name": s.name, "id": s.id, "description": s.description} for s in found.sources],
                "open": shown_repo or None}

    owner, repo = await _resolve(args.get("repo"), shown_repo)
    full = f"{owner}/{repo}"
    state.code_repo = full

    if name == "github_open_repo":
        tree = await cb.read_tree(owner, repo)
        await view(view="files", path="")
        return {"opened": full, "top_folder": [i.name + ("/" if i.type == "dir" else "") for i in tree.items]}

    if name == "github_open_file":
        path = str(args.get("path") or "").strip()
        f = await cb.read_file(owner, repo, path)
        await view(file=f.path)
        return {"opened": f.path, "repo": full, "note": f.note, "lines": f.content.count("\n") + 1 if f.content else 0,
                "text": f.content[:6000]}

    if name == "github_list_prs":
        pulls = await cb.list_prs(owner, repo)
        await view(view="prs")
        return {"repo": full, "open_pull_requests": [
            {"number": p.number, "title": p.title, "author": p.author, "from": p.head, "into": p.base} for p in pulls.prs]}

    if name == "github_close_pr":
        await view(view="files")
        return {"ok": True, "repo": full}

    if name == "github_link_repo":
        await view(view="files", link_project=True)
        return {"linked_repo": full, "note": "the code panel opens this repo for the open project from now on"}

    number = args.get("number")
    if not isinstance(number, int) or number < 1:
        return {"error": "number must be a pull request number"}

    if name == "github_show_pr":
        pr = await cb.read_pr(owner, repo, number)
        await view(pr=number, file=args.get("file") or None)
        budget = 6000
        files = []
        for f in pr.files:
            patch = f.patch[:max(0, min(1500, budget))]
            budget -= len(patch)
            files.append({"file": f.filename, "status": f.status, "added": f.additions, "removed": f.deletions, "diff": patch})
        return {"repo": full, "number": pr.number, "title": pr.title, "author": pr.author, "from": pr.head, "into": pr.base,
                "description": pr.body, "added": pr.additions, "removed": pr.deletions, "files": files}

    if name in ("github_approve_pr", "github_merge_pr"):
        verb = "approve" if name == "github_approve_pr" else "merge"
        if args.get("confirm") is not True or pending != (name, full, number):
            pr = await cb.read_pr(owner, repo, number)
            await view(pr=number)
            state.code_pending = (name, full, number)
            return {"status": "awaiting_confirmation", "repo": full, "number": pr.number, "title": pr.title,
                    "author": pr.author, "from": pr.head, "into": pr.base,
                    "next": f"Nothing was done. Say the number and title and ask the user whether to {verb} it. "
                            f"Only after a clear yes, call {name} again with confirm true."}
        done = await (cb.approve_pr if verb == "approve" else cb.merge_pr)(owner, repo, number)
        if verb == "approve":
            await view(pr=number)
        else:
            await view(view="prs")
        return {"status": "approved" if verb == "approve" else "merged", "ok": done.ok, "note": done.note}

    return {"error": f"Unknown tool {name!r}"}
