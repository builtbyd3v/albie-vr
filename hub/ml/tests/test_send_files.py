"""'Send the updated files to Adrian': the ML desk's files on a Slack draft (code, run results, checkpoint), and one
`attach` that picks the open desk's files for "all". Slack is the stub client from test_messaging_files."""
from __future__ import annotations

import asyncio
import json
import shutil
from pathlib import Path
from types import SimpleNamespace
from typing import Any

import pytest

from events import Attachment, EventBus
from messaging import tools as mtools
from ml import improve
from ml.service import MLService
from ml.tests.test_improve import cifar  # noqa: F401 - fixture
from ml.tools import MLTools
from tests.test_messaging_files import service


def tools_for(repo: Path, tmp_path: Path) -> MLTools:
    return MLTools(SimpleNamespace(bus=EventBus(), ml=MLService(repo)), tmp_path / "data", None)


def apply_change(repo: Path) -> None:
    """What ml_apply leaves: the new train.py in the repo, the old one as .forge/improve/train.py.orig."""
    out = repo / ".forge" / "improve"
    out.mkdir(parents=True)
    shutil.copyfile(repo / "train.py", out / "train.py.orig")
    (repo / "train.py").write_text((repo / "train.py").read_text() + "\n# weight decay 5e-4\n")
    (out / "plan.json").write_text(json.dumps({"files": {"train.py": {"sha256": "x"}}, "applied": True}))


def test_all_on_the_ml_desk_is_the_changed_code_its_diff_and_the_results(cifar, tmp_path):  # noqa: F811
    apply_change(cifar)
    files = asyncio.run(tools_for(cifar, tmp_path).attachments(["all"]))
    assert [f.name for f in files] == ["train.py", "train.py.diff", "cifar10_base0001_results.json"]
    assert all(Path(f.path).is_file() and f.bytes == Path(f.path).stat().st_size for f in files)
    assert "+# weight decay 5e-4" in Path(files[1].path).read_text()
    assert json.loads(Path(files[2].path).read_text())["best_val_acc"] == 0.605
    assert files[0].source == "ml cifar10 run base0001"


def test_without_an_applied_change_code_is_the_training_script(cifar, tmp_path):  # noqa: F811
    files = asyncio.run(tools_for(cifar, tmp_path).attachments(["code"]))
    assert [f.name for f in files] == ["train.py"]


def test_the_checkpoint_goes_only_when_it_exists_and_is_small_enough(cifar, tmp_path, monkeypatch):  # noqa: F811
    tools = tools_for(cifar, tmp_path)
    with pytest.raises(LookupError, match="no checkpoint"):
        asyncio.run(tools.attachments(["model"]))
    ckpt = cifar / json.loads((cifar / ".forge" / "profile.json").read_text())["checkpoint"]
    ckpt.parent.mkdir(parents=True)
    ckpt.write_bytes(b"x" * 100)
    assert [f.name for f in asyncio.run(tools.attachments(["all"]))][-1] == ckpt.name
    monkeypatch.setattr("ml.tools.MODEL_MAX_BYTES", 10)
    assert ckpt.name not in [f.name for f in asyncio.run(tools.attachments(["all"]))]  # "all" skips it quietly
    with pytest.raises(LookupError, match="too big"):
        asyncio.run(tools.attachments(["model"]))


def test_no_finished_run_means_no_results(tmp_path):
    repo = tmp_path / "empty"
    (repo / ".forge").mkdir(parents=True)
    with pytest.raises(LookupError, match="no finished run"):
        asyncio.run(tools_for(repo, tmp_path).attachments(["results"]))


def test_one_attach_routes_all_to_the_open_desk_and_kinds_to_their_desk():
    asked: list[tuple[str, list[str]]] = []

    def fake(desk: str):
        async def attach(kinds: list[str]) -> list[Attachment]:
            asked.append((desk, kinds))
            return [Attachment(id=f"{desk}-{k}", name=k) for k in kinds]
        return attach

    desk: list[str | None] = ["ml"]
    attach = mtools.desk_attach(fake("cad"), fake("ml"), lambda: desk[0])
    asyncio.run(attach(["all"]))
    desk[0] = "cad"
    asyncio.run(attach(["all"]))
    desk[0] = None  # Unity never said: the CAD files, as before
    asyncio.run(attach(["all"]))
    assert [n.name for n in asyncio.run(attach(["step", "code"]))] == ["step", "code"]
    assert asked == [("ml", ["all"]), ("cad", ["all"]), ("cad", ["all"]), ("cad", ["step"]), ("ml", ["code"])]


def test_send_the_updated_files_to_adrian_drafts_first_and_uploads_only_on_yes(cifar, tmp_path):  # noqa: F811
    apply_change(cifar)
    svc, bus, client = service(tmp_path)
    ml = tools_for(cifar, tmp_path)

    async def no_cad(kinds: list[str]) -> list[Attachment]:
        raise AssertionError("the CAD desk isn't open")

    async def show(view: str) -> dict[str, Any]:
        return {"ok": True, "view": view}

    attach = mtools.desk_attach(no_cad, ml.attachments, lambda: "ml")

    async def run() -> tuple[dict[str, Any], int, dict[str, Any]]:
        drafted = await mtools.call_tool("messages_send", {"text": "Here are the updated files from the last run",
                                                           "channel": "Jane", "attach": ["all"]},
                                         svc, attach=attach, show=show)
        uploads = len(client.uploads)
        sent = await mtools.call_tool("messages_confirm", {"send": True}, svc, attach=attach, show=show)
        return drafted, uploads, sent

    drafted, uploads_before_yes, sent = asyncio.run(run())
    assert drafted["status"] == "awaiting_confirmation"
    assert drafted["files"] == ["train.py", "train.py.diff", "cifar10_base0001_results.json"]
    assert uploads_before_yes == 0  # nothing leaves before "yes, send it"
    assert sent["status"] == "sent" and sent["files"] == drafted["files"]
    assert [f["filename"] for f in client.uploads[0]["file_uploads"]] == drafted["files"]
    assert improve.plan(MLService(cifar))["applied"]  # sending touched nothing in the project
