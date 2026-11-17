"""ml_publish: a project Albie built goes to GitHub as one branch, one commit and one pull request, only after a yes.

A fake GitHub (github_publish.call) keeps repos, refs, blobs, trees, commits and pulls in memory and records every
call, so the tests see exactly what would be posted. The project is built and trained by the real ml_new flow on
the fake Hugging Face from test_new_project.
"""
from __future__ import annotations

import asyncio
import base64
import json
from types import SimpleNamespace

import pytest

from events import EventBus
from ml import github_publish, new_project
from ml.service import MLService
from ml.tests.test_new_project import _run_tool, fake_hf  # noqa: F401 - the fixture
from ml.tools import MLTools


class FakeGitHub:
    def __init__(self, user: str = "albie-demo", repos: tuple[str, ...] = ()) -> None:
        self.user, self.calls = user, []
        self.repos = {r: {"default_branch": "main", "refs": {"main": "c0"}, "pulls": []} for r in repos}
        self.blobs, self.trees, self.commits = {}, {"t0": {}}, {"c0": "t0"}

    def writes(self) -> list[tuple[str, str]]:
        return [(m, p) for m, p, _ in self.calls if m != "GET"]

    def __call__(self, method, path, body=None, ok_missing=False):
        self.calls.append((method, path, body))
        if path == "/user":
            return {"login": self.user}
        if method == "POST" and path == "/user/repos":
            self.repos[f"{self.user}/{body['name']}"] = {"default_branch": "main", "refs": {"main": "c0"}, "pulls": []}
            return {"default_branch": "main"}
        parts = path.split("?")[0].split("/")
        repo_name = "/".join(parts[2:4])
        repo = self.repos.get(repo_name)
        rest = "/".join(parts[4:])
        if repo is None:
            if ok_missing:
                return None
            raise github_publish.PublishError("GitHub said 404: Not Found")
        if not rest:
            return {"default_branch": repo["default_branch"]}
        if method == "GET" and rest.startswith("git/ref/heads/"):
            ref = repo["refs"].get(rest.removeprefix("git/ref/heads/"))
            return {"object": {"sha": ref}} if ref else (None if ok_missing else {})
        if method == "GET" and rest.startswith("git/commits/"):
            return {"tree": {"sha": self.commits[rest.split("/")[-1]]}}
        if rest == "git/blobs":
            sha = f"b{len(self.blobs)}"
            self.blobs[sha] = base64.b64decode(body["content"])
            return {"sha": sha}
        if rest == "git/trees":
            sha = f"t{len(self.trees)}"
            self.trees[sha] = {**self.trees[body["base_tree"]], **{e["path"]: e["sha"] for e in body["tree"]}}
            return {"sha": sha}
        if rest == "git/commits":
            sha = f"c{len(self.commits)}"
            self.commits[sha] = body["tree"]
            return {"sha": sha}
        if rest == "git/refs":
            repo["refs"][body["ref"].removeprefix("refs/heads/")] = body["sha"]
            return {}
        if method == "PATCH" and rest.startswith("git/refs/heads/"):
            repo["refs"][rest.removeprefix("git/refs/heads/")] = body["sha"]
            return {}
        if rest == "pulls" and method == "GET":
            head = path.split("head=")[1].split(":")[1] if "head=" in path else None
            return [p for p in repo["pulls"] if p["head"] == head]
        if rest == "pulls" and method == "POST":
            pr = {"number": len(repo["pulls"]) + 1, "html_url": f"https://github.com/{repo_name}/pull/{len(repo['pulls']) + 1}", **body}
            repo["pulls"].append(pr)
            return pr
        raise AssertionError(f"unexpected {method} {path}")

    def files(self, repo: str, branch: str) -> dict[str, bytes]:
        tree = self.trees[self.commits[self.repos[repo]["refs"][branch]]]
        return {path: self.blobs[sha] for path, sha in tree.items()}


@pytest.fixture()
def github(monkeypatch):
    fake = FakeGitHub()
    monkeypatch.setattr(github_publish, "call", fake)
    monkeypatch.setenv("GITHUB_TOKEN", "test-token")
    monkeypatch.delenv("FORGE_ML_PUBLISH_REPO", raising=False)
    return fake


def _tools(svc: MLService, tmp_path) -> tuple[MLTools, list]:
    events: list = []
    bus = EventBus()
    publish = bus.publish

    async def record(type_, payload):
        events.append((type_, payload))
        await publish(type_, payload)

    bus.publish = record
    return MLTools(SimpleNamespace(bus=bus, ml=svc), tmp_path / "data", None), events


def test_a_trained_text_project_becomes_one_pull_request_only_after_a_yes(tmp_path, monkeypatch, fake_hf, github):
    _, _, _, svc = _run_tool(tmp_path, monkeypatch, {"request": "an RNN on movie reviews", "model": "rnn", "epochs": 2})
    tools, events = _tools(svc, tmp_path)

    async def flow():
        asked = await tools.call("ml_publish", {})
        assert asked["status"] == "awaiting_confirmation" and asked["create_repo"] is True
        assert asked["repo"] == "albie-demo/albie-ml-projects" and asked["branch"] == f"albie/{svc.repo.name}"
        assert github.writes() == []  # nothing posted before the yes
        return await tools.call("ml_publish", {"confirm": True})

    done = asyncio.run(flow())
    assert done["status"] == "opened" and done["number"] == 1, done
    assert done["url"] == "https://github.com/albie-demo/albie-ml-projects/pull/1"
    files = github.files("albie-demo/albie-ml-projects", f"albie/{svc.repo.name}")
    folder = f"projects/{svc.repo.name}/"
    assert {folder + f for f in ("train.py", "model.py", "data.py", "download_data.py", "hf_data.py", "project.json",
                                 "README.md", ".forge/adapter.py", ".forge/profile.json", "data/train.jsonl",
                                 "data/val.jsonl")} <= set(files)
    readme = files[folder + "README.md"].decode()
    assert "## Results" in readme and "Best validation accuracy" in readme and "| 2 |" in readme
    commits = [p for m, p, _ in github.calls if m == "POST" and p.endswith("/git/commits")]
    assert len(commits) == 1  # one commit with every file
    pr = github.repos["albie-demo/albie-ml-projects"]["pulls"][0]
    assert pr["title"].startswith("Add RNN text classifier") and "stanfordnlp/imdb" in pr["body"]
    assert ("code.view", {"repo": "albie-demo/albie-ml-projects", "pr": 1}) in events  # the code panel shows it

    again = asyncio.run(tools.call("ml_publish", {"confirm": True}))  # a stale yes: asks again, posts nothing new
    assert again["status"] == "awaiting_confirmation"


def test_an_image_project_leaves_its_many_images_to_download_data(tmp_path, fake_hf, github):
    pytest.importorskip("torch")
    p = new_project.plan(model="cnn", request="a cnn")
    repo = new_project.build(p, tmp_path / "new", epochs=1)
    plan = github_publish.plan(repo)
    assert plan["with_data"] is False
    github_publish.publish(repo, plan)
    files = github.files(plan["repo"], plan["branch"])
    assert not any("/data/" in f for f in files)
    readme = files[f"{plan['folder']}/README.md"].decode()
    assert "download_data.py" in readme and "Not trained yet" in readme
    layout = json.loads(files[f"{plan['folder']}/project.json"])["layout"]
    assert layout["dataset"] == "zalando-datasets/fashion_mnist"


def test_ml_new_with_publish_offers_the_pull_request_after_training(tmp_path, monkeypatch, fake_hf, github):
    result, said, _, svc = _run_tool(tmp_path, monkeypatch, {"request": "an RNN on movie reviews and make a PR",
                                                             "model": "rnn", "epochs": 1, "publish": True},
                                     last=("Shall I open a pull request",))
    assert said[-2].startswith("Done!") and "a new repo, albie-demo/albie-ml-projects" in said[-1]
    assert github.writes() == []


def test_without_a_token_albie_says_what_is_missing(tmp_path, monkeypatch, fake_hf):
    monkeypatch.delenv("GITHUB_TOKEN", raising=False)
    p = new_project.plan(model="mlp", request="an mlp on movie reviews")
    repo = new_project.build(p, tmp_path / "new", epochs=1)
    tools, _ = _tools(MLService(repo), tmp_path)
    out = asyncio.run(tools.call("ml_publish", {}))
    assert "GITHUB_TOKEN" in out["error"]


def test_a_project_albie_didnt_build_isnt_published(tmp_path, github):
    tools, _ = _tools(MLService(tmp_path), tmp_path)
    assert "isn't one Albie built" in asyncio.run(tools.call("ml_publish", {}))["error"]
