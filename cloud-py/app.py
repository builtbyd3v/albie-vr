"""Albie's cloud: the hub's CAD and ML, for headsets with no laptop. One process, every judge's headset at once.

    PYTHONPATH=cloud-py/stubs:cloud-py:hub uvicorn app:app --port 8080      (from the repo root)

Only hub/cad and hub/ml are mounted (no Slack, agent or voice). Every request carries
  x-albie-key     == env ALBIE_APP_KEY (401 otherwise)
  x-albie-device  the headset's own id (the app makes one and keeps it); each device gets its own CAD library and
                  version history, its own ML project copy and its own event log, so nobody overwrites anyone.
Each device is a small FastAPI app holding the hub's own routers with that device's state (request.app.state is
per device, so hub/cad/router.py and hub/ml/router.py run unchanged). One pool of CAD worker processes is shared.

Device routes beyond the hub's:
  GET  /events?after=N   what the hub would have broadcast on /ws (cad.model_updated, ml.metrics, pet.say ...)
  POST /tools/{name}     one of Albie's CAD or ML tools (hub/cad/tools.py, hub/ml/tools.py) -> {result, show}
                         `show`: the view the tool wanted open ("cad"/"ml"); the headset opens it.
Env: ALBIE_APP_KEY (required), CLOUD_DATA_DIR (/data), CAD_WORKERS (1), MAX_TRAINING (1), MAX_DEVICES (200),
CLAUDE_PROXY_URL (the Vercel proxy for 'improve it'; empty = the free scripted change), PORT (8080).
"""
from __future__ import annotations

import asyncio
import collections
import hmac
import json
import logging
import os
import queue
import re
import secrets
import shutil
import urllib.request
from pathlib import Path
from typing import Any

from fastapi import Body, FastAPI, Request
from starlette.responses import JSONResponse

import code_browser
from cad import tools as cad_tools
from cad.router import router as cad_router
from cad.service import CadService
from cad.worker import WorkerHandle
from events import EventBus, _body
from ml import improve, router as ml_router_module
from ml.service import Conflict, MLService
from ml.setup.llm import LLMError, LLMResult
from ml.tools import TOOLS as ML_TOOLS, MLTools
from code_browser import tools as code_tools

import text_improve

log = logging.getLogger("albie.cloud")
ROOT = Path(__file__).resolve().parents[1]
HUB = ROOT / "hub"
DATA = Path(os.environ.get("CLOUD_DATA_DIR") or ROOT / "cloud-py" / ".data")
ML_TEMPLATE = Path(os.environ.get("CLOUD_ML_TEMPLATE") or ROOT / "ml-examples" / "text-classification")
PORT = int(os.environ.get("PORT", "8080"))
MAX_DEVICES = int(os.environ.get("MAX_DEVICES", "200"))
MAX_TRAINING = int(os.environ.get("MAX_TRAINING", "1"))
CLAUDE_PROXY_URL = os.environ.get("CLAUDE_PROXY_URL", "")
DEVICE_ID = re.compile(r"^[A-Za-z0-9_-]{8,64}$")
INTERNAL = secrets.token_hex(16)  # training runs post their events to /_in/<this>/<device>/ml/ingest (no app key)
# A device trains its own copy of the template, or a repo Albie cloned for it with ml_github (after the user said
# yes to it). The ML panel's own import path and the laptop-only routes stay off.
BLOCKED = ("/ml/project/rescan", "/ml/project/type", "/ml/github/", "/ml/project/diff")
CAD_TOOLS = {t["name"] for t in cad_tools.TOOLS}
ML_TOOL_NAMES = {t["name"] for t in ML_TOOLS}
CODE_TOOLS = {t["name"] for t in code_tools.TOOLS}
EVENT_LOG = 400

os.environ.setdefault("FORGE_ML_IMPROVE", "auto")  # Claude through the proxy when set, else the scripted change


# ---- shared CAD workers ----------------------------------------------------------------------------------

class WorkerPool:
    """Stands in for one CadService's WorkerHandle, shared by every device: a call takes a free warm worker."""

    def __init__(self, n: int) -> None:
        self.workers = [WorkerHandle() for _ in range(max(1, n))]
        self.free: queue.Queue[WorkerHandle] = queue.Queue()
        for w in self.workers:
            self.free.put(w)

    def start(self) -> None:
        for w in self.workers:
            w.spawn()

    def spawn(self) -> None:  # CadService.start: the pool is already warm
        pass

    def close(self) -> None:  # CadService.stop: the pool outlives devices
        pass

    def shutdown(self) -> None:
        for w in self.workers:
            w.close()

    def call(self, job: dict[str, Any], timeout_s: float) -> dict[str, Any]:
        worker = self.free.get()
        try:
            return worker.call(job, timeout_s)
        finally:
            self.free.put(worker)


POOL = WorkerPool(int(os.environ.get("CAD_WORKERS", "1")))


# ---- per-device state ------------------------------------------------------------------------------------

class CloudBus(EventBus):
    """The hub's bus with no sockets: each event is also kept, numbered, for the headset's GET /events."""

    def __init__(self) -> None:
        super().__init__()
        self.seq = 0
        self.log: collections.deque[dict[str, Any]] = collections.deque(maxlen=EVENT_LOG)

    async def publish(self, type_: str, payload: Any) -> None:
        await super().publish(type_, payload)
        self.seq += 1
        self.log.append({"seq": self.seq, "type": type_, "payload": _body(payload)})


def training_now() -> int:
    return sum(1 for d in DEVICES.values() if d.ml.training_active())


class DeviceML(MLService):
    """The device's copy of the cloud's ML project. Runs post their live events back to this device."""

    def __init__(self, repo: Path, device: str, built: Path) -> None:
        super().__init__(repo)
        self.template = self.repo
        self.clones = repo.parent / "github"  # where ml_github clones for this device (MLTools.github)
        self.built = built.resolve()  # ml_new's projects: reviewed templates plus downloaded data, no foreign code
        self.ingest_url = f"http://127.0.0.1:{PORT}/_in/{INTERNAL}/{device}"

    def open_project(self, path: str) -> Path:
        target = Path(path).resolve()
        if not self.allowed(target):
            raise Conflict("the cloud trains only this headset's project, the repos Albie cloned for it "
                           "and the projects he built for it")
        return super().open_project(path)

    def allowed(self, path: Path) -> bool:
        return path == self.template or path.is_relative_to(self.clones.resolve()) or path.parent == self.built

    def start_training(self, epochs: int, hub_url: str) -> str:
        if not self.training_active() and training_now() >= MAX_TRAINING:
            raise Conflict("the training machine is busy with someone else's run; try again in a minute")
        return super().start_training(min(epochs, 20), self.ingest_url)


class DeviceMLTools(MLTools):
    def resolve(self, name: str) -> Path:
        """A path, or a repo cloned or a project built for this device, by name. With no name, the one it used
        last (kept across a restart or the Space's sleep), else its own copy of the cloud's project."""
        svc = self.svc
        given = Path(name) if name else None
        if given is not None and given.is_absolute() and given.is_dir():
            return given
        recent = [p for p in (Path(r).resolve() for r in self.recent()) if svc.allowed(p)]
        key = (name or "").lower().replace(" ", "").replace("-", "").replace("_", "")
        if not key:
            return recent[0] if recent else svc.repo
        candidates = list(recent)
        for root in (svc.clones, svc.built):
            if root.is_dir():
                candidates += sorted(p for p in root.iterdir() if p.is_dir())
        for path in candidates:
            if key in path.name.lower().replace("-", "").replace("_", ""):
                return path
        return svc.template


def copy_template(dest: Path) -> None:
    """The ML project for one device: the template's code and its baked first run; the data folder is shared."""
    if dest.exists():
        return
    skip = {"data", "checkpoints", "__pycache__"}
    shutil.copytree(ML_TEMPLATE, dest, ignore=lambda d, names: [n for n in names if n in skip])
    if (ML_TEMPLATE / "data").is_dir():
        (dest / "data").symlink_to(ML_TEMPLATE / "data", target_is_directory=True)


def no_llm(*_: Any, **__: Any) -> LLMResult:
    raise LLMError("no Claude in the cloud for project setup")


def proxy_llm(system: str, messages: list[dict], schema: dict, *, timeout: float = 60.0, effort: str = "medium") -> LLMResult:
    """ml/setup/llm.complete_json through the Vercel Claude proxy (its key never reaches this machine)."""
    if not CLAUDE_PROXY_URL:
        raise LLMError("no Claude proxy set (CLAUDE_PROXY_URL)")
    body = {"model": "claude-sonnet-5-5", "max_tokens": 4096, "system": system, "messages": messages,
            "output_config": {"effort": effort, "format": {"type": "json_schema", "schema": schema}}}
    request = urllib.request.Request(CLAUDE_PROXY_URL, data=json.dumps(body, default=lambda o: o.model_dump()).encode(),
                                     headers={"content-type": "application/json",
                                              "x-albie-key": os.environ.get("ALBIE_APP_KEY", "")})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as answer:
            message = json.loads(answer.read())
    except Exception as e:  # noqa: BLE001 - any failure means: use the scripted change
        raise LLMError(f"Claude proxy: {e}") from e
    if message.get("stop_reason") in ("refusal", "max_tokens"):
        raise LLMError(f"Claude stopped: {message.get('stop_reason')}")
    text = next((b.get("text") for b in message.get("content", []) if b.get("type") == "text"), None)
    try:
        return LLMResult(data=json.loads(text or ""), content=message.get("content", []), usage=message.get("usage", {}))
    except ValueError as e:
        raise LLMError("Claude's reply wasn't JSON") from e


# 'what can I do better?' for the text project with no Claude: improve.py's scripted path, pointed at this project.
_golden_available = improve.scripted_available
_golden_plan = improve.scripted
_last_repo: list[Path] = []  # ponytail: propose() asks scripted_available(svc) right before scripted(d)


def _scripted_available(svc) -> bool:
    _last_repo[:] = [svc.repo]
    return _golden_available(svc) or (svc.repo / "train.py").is_file() and (svc.repo / "data.py").is_file()


def _scripted(d: dict) -> dict:
    repo = _last_repo[0] if _last_repo else None
    return _golden_plan(d) if repo is None or _golden_available_repo(repo) else text_improve.plan(d, repo)


def _golden_available_repo(repo: Path) -> bool:
    golden = improve.GOLDEN / "train.py"
    return golden.is_file() and (repo / "train.py").read_bytes() == golden.read_bytes()


improve.scripted_available, improve.scripted = _scripted_available, _scripted  # ponytail: monkeypatch, not a fork


class Device:
    def __init__(self, device_id: str) -> None:
        self.id = device_id
        root = DATA / "devices" / device_id
        root.mkdir(parents=True, exist_ok=True)
        self.bus = CloudBus()
        self.cad = CadService(root / "cad", HUB / "cad" / "parts" / "rocket.py", self.bus)
        self.cad._worker = POOL
        copy_template(root / "ml" / ML_TEMPLATE.name)
        self.ml = DeviceML(root / "ml" / ML_TEMPLATE.name, device_id, root / "ml" / "new")
        app = FastAPI(title=f"Albie cloud device {device_id}")
        app.state.bus, app.state.cad, app.state.ml = self.bus, self.cad, self.ml
        app.state.ml_llm = proxy_llm if CLAUDE_PROXY_URL else no_llm
        # A repo cloned with ml_github is classified and adapted by Claude through the proxy; with no proxy, setup
        # fails and says so (the template needs no setup).
        app.state.ml_jev, app.state.ml_backend = no_llm, "claude" if CLAUDE_PROXY_URL else "jev"
        self.ml_tools = DeviceMLTools(app.state, root, speak=lambda pet, text: asyncio.sleep(0, False))
        app.include_router(cad_router)
        app.include_router(ml_router_module.router)
        app.include_router(code_browser.router)  # /code/*: the GitHub repos and pull requests in the code panel
        self.add_routes(app)
        self.app = app

    async def start(self) -> None:
        await self.cad.start()

    def add_routes(self, app: FastAPI) -> None:
        @app.get("/events")
        def events(after: int = 0):
            bus = self.bus
            reset = after > bus.seq  # the server restarted: everything is new
            start = 0 if reset else after
            return {"seq": bus.seq, "reset": reset, "events": [e for e in bus.log if e["seq"] > start]}

        @app.post("/tools/{name}")
        async def tool(name: str, args: dict[str, Any] = Body(default_factory=dict)):
            shown: list[str] = []

            async def show(view: str) -> dict[str, Any]:
                shown.append(view)
                return {}

            if name in CAD_TOOLS:
                result = await cad_tools.call_tool(name, args, self.cad, self.bus, show=show)
            elif name in ML_TOOL_NAMES:
                result = await self.ml_tools.call(name, args)
                if name in ("ml_open", "ml_train", "ml_apply") or (name == "ml_github" and args.get("confirm") is True):
                    shown.append("ml")
                elif name == "ml_publish" and result.get("status") == "opened":
                    shown.append("code")  # the new pull request, in the code panel
            elif name in CODE_TOOLS:
                result = await code_tools.call_tool(name, args, self.bus, app.state, show=show)
            else:
                result = {"error": f"no cloud tool {name!r}"}
            return {"result": result, "show": shown[0] if shown else None, "seq": self.bus.seq}


DEVICES: collections.OrderedDict[str, Device] = collections.OrderedDict()
_device_lock = asyncio.Lock()


async def device(device_id: str) -> Device:
    found = DEVICES.get(device_id)
    if found is not None:
        DEVICES.move_to_end(device_id)
        return found
    async with _device_lock:
        if device_id not in DEVICES:
            d = Device(device_id)
            await d.start()
            DEVICES[device_id] = d
            while len(DEVICES) > MAX_DEVICES:  # its files stay on disk; it reloads on its next request
                _, old = DEVICES.popitem(last=False)
                try:
                    await old.cad.stop()  # the shared POOL ignores it; a device's own worker would close here
                except Exception:  # noqa: BLE001 - a failed close must not fail the new device's request
                    log.exception("closing evicted device %s failed", old.id)
        return DEVICES[device_id]


# ---- the ASGI entry point --------------------------------------------------------------------------------

async def lifespan(receive, send) -> None:
    while True:
        message = await receive()
        if message["type"] == "lifespan.startup":
            await asyncio.to_thread(POOL.start)
            await send({"type": "lifespan.startup.complete"})
        elif message["type"] == "lifespan.shutdown":
            await asyncio.to_thread(POOL.shutdown)
            await send({"type": "lifespan.shutdown.complete"})
            return


async def app(scope, receive, send) -> None:
    if scope["type"] == "lifespan":
        return await lifespan(receive, send)
    if scope["type"] != "http":
        return
    path: str = scope["path"]
    headers = {k.decode("latin-1"): v.decode("latin-1") for k, v in scope["headers"]}
    internal = path.startswith(f"/_in/{INTERNAL}/")
    if internal:  # a training run on this machine posting its events (forge SDK -> /ml/ingest)
        _, _, _, device_id, rest = path.split("/", 4)
        scope = {**scope, "path": "/" + rest, "raw_path": ("/" + rest).encode()}
        path = scope["path"]
    else:
        key = os.environ.get("ALBIE_APP_KEY", "")
        if not key or not hmac.compare_digest(headers.get("x-albie-key", ""), key):
            return await JSONResponse({"error": "missing or wrong x-albie-key"}, 401)(scope, receive, send)
        if path == "/health":
            return await JSONResponse({"ok": True, "devices": len(DEVICES), "cad_workers": len(POOL.workers),
                                       "training": training_now()})(scope, receive, send)
        device_id = headers.get("x-albie-device", "")
    if not DEVICE_ID.match(device_id):
        return await JSONResponse({"error": "x-albie-device must be 8-64 letters, digits, - or _"}, 400)(scope, receive, send)
    if any(path.startswith(p) for p in BLOCKED) or (path == "/ml/project" and scope["method"] == "POST"):
        return await JSONResponse({"error": "not available in the cloud"}, 403)(scope, receive, send)
    try:
        d = await device(device_id)
    except Exception as e:  # noqa: BLE001 - say why instead of a bare 500
        log.exception("device %s failed to start", device_id)
        return await JSONResponse({"error": f"couldn't start this device's workspace: {e}"}, 500)(scope, receive, send)
    await d.app(scope, receive, send)


logging.basicConfig(level=logging.INFO, format="%(asctime)s %(name)s %(levelname)s %(message)s")
