"""Web builds install dependencies and prove both tests and production compilation work."""
import asyncio
import json
import shutil
import subprocess

import pytest

from agent.build import Build
from agent.build_review import discover_checks, run_check, verify
from tests.agent.test_build import builds_env, git, wait_for, tool  # noqa: F401


def web_project(tmp_path, monkeypatch):
    monkeypatch.setenv("HOME", str(tmp_path))
    root = tmp_path / "workspace"
    root.mkdir()
    (root / "validator").mkdir()
    (root / "validator/package.json").write_text(json.dumps({"name": "login-validator", "version": "1.0.0"}))
    (root / "validator/index.js").write_text("module.exports = (email, password) => email.includes('@') && password.length >= 8;")
    (root / "package.json").write_text(json.dumps({
        "name": "login-page", "version": "1.0.0",
        "dependencies": {"login-validator": "file:./validator"},
        "scripts": {"test": "node --test login.test.cjs", "build": "node build.cjs"},
    }))
    (root / "package-lock.json").write_text(json.dumps({
        "name": "login-page", "version": "1.0.0", "lockfileVersion": 3,
        "packages": {"": {"name": "login-page", "version": "1.0.0", "dependencies": {"login-validator": "file:./validator"}},
                     "node_modules/login-validator": {"resolved": "validator", "link": True},
                     "validator": {"name": "login-validator", "version": "1.0.0"}},
    }))
    (root / "login.test.cjs").write_text("""const test = require('node:test');
const assert = require('node:assert/strict');
const valid = require('login-validator');
test('rejects invalid login fields', () => {
  assert.equal(valid('bad', 'password123'), false);
  assert.equal(valid('user@example.com', 'short'), false);
});
test('accepts valid login fields', () => assert.equal(valid('user@example.com', 'password123'), true));
""")
    (root / "build.cjs").write_text("require('node:fs').writeFileSync('built.html', '<form>Login</form>');")
    return Build("web-test", "Build a login page", workspace=root)


@pytest.mark.skipif(not shutil.which("npm"), reason="Node/npm integration test")
def test_clean_web_project_installs_local_dependency_then_tests_and_builds(tmp_path, monkeypatch):
    build = web_project(tmp_path, monkeypatch)
    assert not (build.workspace / "node_modules").exists()
    results = asyncio.run(verify(build))
    assert [r["command"][1] for r in results] == ["ci", "test", "run"]
    assert all(r["state"] == "passed" for r in results), results
    assert (build.workspace / "built.html").read_text() == "<form>Login</form>"


@pytest.mark.skipif(not shutil.which("npm"), reason="Node/npm integration test")
def test_passing_tests_do_not_hide_a_failed_production_build(tmp_path, monkeypatch):
    build = web_project(tmp_path, monkeypatch)
    (build.workspace / "build.cjs").write_text("console.error('Cannot compile login page'); process.exit(2);")
    results = asyncio.run(verify(build))
    assert [r["state"] for r in results] == ["passed", "passed", "failed"]
    assert "Cannot compile login page" in results[-1]["output"]


def test_missing_npm_has_an_actionable_error_and_stops_before_tests(tmp_path, monkeypatch):
    build = web_project(tmp_path, monkeypatch)
    monkeypatch.setenv("PATH", str(tmp_path / "no-executables"))
    results = asyncio.run(verify(build))
    assert len(results) == 1 and results[0]["state"] == "unavailable"
    assert "'npm' was not found" in results[0]["output"]


def test_mixed_project_discovers_web_and_python_checks(tmp_path):
    (tmp_path / "package.json").write_text(json.dumps({"scripts": {"build": "vite build", "test": "node --test"}}))
    (tmp_path / "test_api.py").touch()
    commands = discover_checks(tmp_path)
    assert commands[:2] == [["npm", "test"], ["npm", "run", "build"]]
    assert commands[2][1:4] == ["-m", "unittest", "discover"]


@pytest.mark.skipif(not shutil.which("npm"), reason="Node/npm integration test")
@pytest.mark.parametrize("reporter", ["tap", "spec"])
def test_empty_node_test_suite_cannot_be_published(tmp_path, monkeypatch, reporter):
    build = web_project(tmp_path, monkeypatch)
    (build.workspace / "login.test.cjs").write_text("require('node:test').describe('login', () => {});\n")
    package = json.loads((build.workspace / "package.json").read_text())
    package["scripts"]["test"] = f"node --test --test-reporter={reporter} login.test.cjs"
    (build.workspace / "package.json").write_text(json.dumps(package))
    results = asyncio.run(verify(build))
    assert [r["state"] for r in results] == ["passed", "failed"]
    assert "tests 0" in results[-1]["output"]


def test_check_timeout_is_visible_in_output(tmp_path, monkeypatch):
    build = web_project(tmp_path, monkeypatch)

    async def timed_out(*args, **kwargs):
        raise asyncio.TimeoutError

    monkeypatch.setattr(asyncio, "create_subprocess_exec", timed_out)
    result = asyncio.run(run_check(build, ["npm", "test"], 0))
    assert result["state"] == "timed_out"
    assert "180-second" in result["output"]


@pytest.mark.skipif(not shutil.which("npm"), reason="Node/npm integration test")
def test_office_web_workflow_can_publish_only_after_real_npm_checks(builds_env, tmp_path, monkeypatch):
    """Script the model only; package install, tests, compilation, git and push are real."""
    from agent import build as module
    import code_browser

    fixture = web_project(tmp_path, monkeypatch).workspace
    (fixture / ".gitignore").write_text("node_modules/\nbuilt.html\n")
    remote = tmp_path / "remote.git"
    subprocess.run(["git", "init", "--bare", "-q", "-b", "main", str(remote)], check=True)
    seed = tmp_path / "seed"
    subprocess.run(["git", "clone", "-q", str(remote), str(seed)], check=True, capture_output=True)
    (seed / "README.md").write_text("Web projects\n")
    git(seed, "add", "-A")
    git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "seed")
    git(seed, "push", "-q", "origin", "main")
    monkeypatch.setenv("BUILD_REPO", "demo/web-projects")
    monkeypatch.setenv("BUILD_REMOTE", str(remote))

    async def run(project, text, mode, emit, *, agent, **kwargs):
        if agent.id == "builder":
            build = next(iter(module.builds.builds.values()))
            shutil.copytree(fixture, project.path / build.folder, dirs_exist_ok=True)
        return "REVIEW: PASS" if mode == "qa" else "Added and checked the login page."

    opened = []

    async def open_pr(*args):
        opened.append(args)
        return {"number": 1, "url": "https://github.com/demo/web-projects/pull/1", "base": "main"}

    monkeypatch.setattr(module, "_runner", lambda: run)
    monkeypatch.setattr(code_browser, "open_pr", open_pr)
    created = builds_env.post("/agent/builds", json={"goal": "Build a simple login web page"}).json()
    final = wait_for(builds_env, created["id"])
    assert final["state"] == "ready", final
    assert [r["state"] for r in final["verification"]] == ["passed"] * 3
    assert [r["command"][1] for r in final["verification"]] == ["ci", "test", "run"]
    assert opened == []
    assert tool(builds_env, "build_ship", confirm=True)["status"] == "awaiting_confirmation"
    assert tool(builds_env, "build_ship", confirm=True)["status"] == "shipped"
    assert len(opened) == 1
    assert git(remote, "branch", "--list", final["branch"]).strip() == final["branch"]
