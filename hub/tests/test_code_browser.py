"""Code browser proxy: only the account's own repos, safe paths, file and pull request shapes (GitHub stubbed)."""
from __future__ import annotations

import base64
from typing import Any

import pytest
from fastapi import FastAPI, HTTPException
from fastapi.testclient import TestClient

import code_browser


def repo(name: str) -> dict[str, Any]:
    return {
        "full_name": f"albie/{name}", "name": name, "owner": {"login": "albie"}, "default_branch": "main",
        "description": None, "permissions": {"push": True},
    }


def pull(number: int) -> dict[str, Any]:
    return {
        "number": number, "title": "Clamp speed", "user": {"login": "albie"}, "state": "open", "created_at": "2026-11-16T00:00:00Z",
        "body": None, "head": {"ref": "clamp"}, "base": {"ref": "main"}, "additions": 3, "deletions": 1, "changed_files": 1,
        "mergeable": None,
    }


class FakeSession:
    async def __aenter__(self) -> "FakeSession":
        return self

    async def __aexit__(self, *exc: object) -> None:
        return None


@pytest.fixture
def github(monkeypatch: pytest.MonkeyPatch) -> tuple[TestClient, dict[str, Any], list[str]]:
    replies: dict[str, Any] = {"/user/repos?type=owner&sort=updated&per_page=100": [repo("rc-truck")]}
    asked: list[str] = []

    async def fake_get(session: object, path: str) -> Any:
        asked.append(path)
        if path not in replies:
            raise HTTPException(404, "not found")
        return replies[path]

    monkeypatch.setattr(code_browser, "_get", fake_get)
    monkeypatch.setattr(code_browser, "_session", FakeSession)
    monkeypatch.setenv("GITHUB_TOKEN", "secret-token")
    code_browser._owned.clear()
    app = FastAPI()
    app.include_router(code_browser.router)
    return TestClient(app), replies, asked


def test_sources_lists_owned_repos(github) -> None:
    client, _, _ = github
    body = client.get("/code/sources").json()
    assert body["user"] == "albie"
    assert body["sources"][0]["id"] == "albie/rc-truck"
    assert body["sources"][0]["writable"] is True
    assert "secret-token" not in str(body)


def test_no_token_is_a_note_not_an_error(github, monkeypatch: pytest.MonkeyPatch) -> None:
    client, _, asked = github
    monkeypatch.delenv("GITHUB_TOKEN")
    body = client.get("/code/sources").json()
    assert body["sources"] == [] and "GITHUB_TOKEN" in body["note"]
    assert asked == []


def test_other_owners_repos_are_not_found(github) -> None:
    client, _, asked = github
    assert client.get("/code/someone/else/tree").status_code == 404
    assert client.get("/code/someone/else/file", params={"path": "a.py"}).status_code == 404
    assert client.get("/code/someone/else/prs").status_code == 404
    assert client.get("/code/someone/else/pr/1").status_code == 404
    assert all(path.startswith("/user/repos") for path in asked)


@pytest.mark.parametrize("path", ["../../../user/emails", "src/../../x", "./a"])
def test_dot_segments_are_refused(github, path: str) -> None:
    client, _, asked = github
    assert client.get("/code/albie/rc-truck/tree", params={"path": path}).status_code == 400
    assert not any("contents" in p for p in asked)


def test_path_is_encoded_per_segment(github) -> None:
    client, replies, asked = github
    replies["/repos/albie/rc-truck/contents/docs/C%23%20notes%3Fref%3Dx.md"] = {
        "type": "file", "path": "docs/C# notes?ref=x.md", "sha": "abc", "size": 2, "encoding": "base64",
        "content": base64.b64encode(b"hi").decode(),
    }
    body = client.get("/code/albie/rc-truck/file", params={"path": "docs/C# notes?ref=x.md"}).json()
    assert body["content"] == "hi"
    assert asked[-1] == "/repos/albie/rc-truck/contents/docs/C%23%20notes%3Fref%3Dx.md"


def test_tree_lists_a_folder(github) -> None:
    client, replies, _ = github
    replies["/repos/albie/rc-truck/contents/"] = [
        {"name": "src", "path": "src", "type": "dir", "size": 0},
        {"name": "README.md", "path": "README.md", "type": "file", "size": 12},
    ]
    body = client.get("/code/albie/rc-truck/tree").json()
    assert [i["name"] for i in body["items"]] == ["src", "README.md"]


def test_large_and_binary_files_say_why_they_are_empty(github) -> None:
    client, replies, _ = github
    replies["/repos/albie/rc-truck/contents/big.csv"] = {"type": "file", "path": "big.csv", "sha": "a", "size": 2_000_000, "encoding": "none", "content": ""}
    replies["/repos/albie/rc-truck/contents/logo.png"] = {
        "type": "file", "path": "logo.png", "sha": "b", "size": 4, "encoding": "base64", "content": base64.b64encode(b"\x89P\x00G").decode(),
    }
    big = client.get("/code/albie/rc-truck/file", params={"path": "big.csv"}).json()
    assert big["content"] == "" and big["truncated"] and big["note"]
    png = client.get("/code/albie/rc-truck/file", params={"path": "logo.png"}).json()
    assert png["content"] == "" and png["binary"] and png["note"]


def test_pull_request_detail_and_long_patch(github) -> None:
    client, replies, _ = github
    long_patch = "\n".join(f"+line {i}" for i in range(2000))
    replies["/repos/albie/rc-truck/pulls?state=open&per_page=20"] = [pull(5)]
    replies["/repos/albie/rc-truck/pulls/5"] = pull(5)
    replies["/repos/albie/rc-truck/pulls/5/files?per_page=100"] = [
        {"filename": "truck.py", "status": "modified", "additions": 3, "deletions": 1, "patch": "@@ -1 +1 @@\n-a\n+b"},
        {"filename": "data.py", "status": "added", "additions": 2000, "deletions": 0, "patch": long_patch},
        {"filename": "logo.png", "status": "added", "additions": 0, "deletions": 0},
    ]
    assert client.get("/code/albie/rc-truck/prs").json()["prs"][0]["number"] == 5
    body = client.get("/code/albie/rc-truck/pr/5").json()
    assert body["additions"] == 3 and body["mergeable"] is None and body["body"] == ""
    short, long, binary = body["files"]
    assert short["patch"].endswith("+b") and not short["truncated"]
    assert long["truncated"] and len(long["patch"]) <= 8000 and long["patch"].splitlines()[-1].startswith("+line ")
    assert binary["patch"] == ""


# ---- writes: approve and merge ---------------------------------------------------------------------------------

@pytest.fixture
def writes(github, monkeypatch: pytest.MonkeyPatch) -> list[tuple[str, str, dict]]:
    sent: list[tuple[str, str, dict]] = []

    async def fake_write(session: object, method: str, path: str, body: dict) -> dict:
        sent.append((method, path, body))
        return {"merged": True, "message": "Pull Request successfully merged"}

    monkeypatch.setattr(code_browser, "_write", fake_write)
    return sent


def test_writes_need_confirm_and_refuse_browsers(github, writes) -> None:
    client, _, _ = github
    assert client.post("/code/albie/rc-truck/pr/5/approve", json={}).status_code == 400
    assert client.post("/code/albie/rc-truck/pr/5/merge", json={"confirm": False}).status_code == 400
    assert client.post("/code/albie/rc-truck/pr/5/merge", json={"confirm": True}, headers={"Origin": "http://evil.example"}).status_code == 403
    assert client.post("/code/someone/else/pr/5/merge", json={"confirm": True}).status_code == 404
    assert writes == []


def test_confirmed_approve_and_merge(github, writes) -> None:
    client, _, _ = github
    assert client.post("/code/albie/rc-truck/pr/5/approve", json={"confirm": True}).json()["ok"]
    assert client.post("/code/albie/rc-truck/pr/5/merge", json={"confirm": True}).json()["ok"]
    assert writes == [
        ("POST", "/repos/albie/rc-truck/pulls/5/reviews", {"event": "APPROVE"}),
        ("PUT", "/repos/albie/rc-truck/pulls/5/merge", {}),
    ]


# ---- Albie's tools ---------------------------------------------------------------------------------------------

class Bus:
    def __init__(self) -> None:
        self.events: list[tuple[str, dict]] = []

    async def publish(self, type_: str, payload: Any) -> None:
        self.events.append((type_, payload.model_dump(exclude_none=True)))


class State:
    pass


def run(coro: Any) -> Any:
    import asyncio
    return asyncio.run(coro)


def with_pr(replies: dict[str, Any]) -> None:
    replies["/repos/albie/rc-truck/pulls/5"] = pull(5)
    replies["/repos/albie/rc-truck/pulls/5/files?per_page=100"] = [
        {"filename": "truck.py", "status": "modified", "additions": 3, "deletions": 1, "patch": "@@ -1 +1 @@\n-a\n+b"}]


def test_tools_show_things_in_the_panel(github) -> None:
    from code_browser import tools
    _, replies, _ = github
    with_pr(replies)
    replies["/repos/albie/rc-truck/contents/"] = [{"name": "src", "path": "src", "type": "dir"}]
    bus, shown, state = Bus(), [], State()

    async def show(view: str) -> dict:
        shown.append(view)
        return {"ok": True}

    out = run(tools.call_tool("github_open_repo", {"repo": "RC-Truck"}, bus, state, show))
    assert out == {"opened": "albie/rc-truck", "top_folder": ["src/"]}
    out = run(tools.call_tool("github_show_pr", {"number": 5}, bus, state, show))
    assert out["title"] == "Clamp speed" and out["files"][0]["diff"].endswith("+b")
    assert bus.events == [
        ("code.view", {"repo": "albie/rc-truck", "view": "files", "path": "", "link_project": False}),
        ("code.view", {"repo": "albie/rc-truck", "pr": 5, "link_project": False}),
    ]
    assert shown == ["code", "code"]
    assert "error" in run(tools.call_tool("github_open_repo", {"repo": "nope"}, bus, state, show))


@pytest.mark.parametrize("tool, method", [("github_approve_pr", "POST"), ("github_merge_pr", "PUT")])
def test_tools_write_only_after_asking(github, writes, tool: str, method: str) -> None:
    from code_browser import tools
    _, replies, _ = github
    with_pr(replies)
    bus, state = Bus(), State()
    # confirm true on the first call is not enough: the pull request has to have been read back first
    first = run(tools.call_tool(tool, {"number": 5, "confirm": True}, bus, state))
    assert first["status"] == "awaiting_confirmation" and writes == []
    # another tool call in between cancels it
    run(tools.call_tool("github_list_repos", {}, bus, state))
    assert run(tools.call_tool(tool, {"number": 5, "confirm": True}, bus, state))["status"] == "awaiting_confirmation"
    assert writes == []
    done = run(tools.call_tool(tool, {"number": 5, "confirm": True}, bus, state))
    assert done["ok"] and len(writes) == 1 and writes[0][0] == method
