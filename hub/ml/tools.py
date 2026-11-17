"""Albie's ML tools (voice model + Claude runner), registered through agent/tools.py like the desk tools.

The demo: "Albie, open the ML project I've been working on" (ml_open: the ML desk unfolds with the last run),
"what can I do better?" (ml_improve: Claude reads the run and writes a change; the card shows it and
Albie says what he'd change), "yes" (ml_apply: the change goes in, training starts, the curves stream live and
Albie says the new score when it's done). ml_undo puts the code back.

The slow parts (Claude's proposal, the training run) run in the background and Albie speaks when they finish,
so a voice turn never waits on them. Nothing in the user's repo changes before ml_apply.
"""
from __future__ import annotations

import asyncio
import json
import logging
import os
import re
import shutil
from collections.abc import Awaitable, Callable
from pathlib import Path
from typing import Any

from agent import events as ev
from agent import speech as agent_speech

from . import github, github_publish, hf_data, improve, new_project, repo_env
from .router import _stop_tail, service, start_run, start_setup
from .service import Conflict, NotFound
from .setup.adapt import ROOT

log = logging.getLogger(__name__)

Speak = Callable[[str, str], Awaitable[bool]]
PET_ID = "bit"
OPEN_WAIT_S = 25.0  # a project with a working .forge/ is ready in a few seconds; a cold one keeps going after
SEARCH_DIRS = [ROOT / "ml-examples", Path.home() / "Desktop" / "ml-examples"]

TOOLS: list[dict[str, Any]] = [
    {"name": "ml_open",
     "description": "Open a machine-learning project on the ML desk: the training, data and results panels "
                    "unfold, with its last run. Without `project` it opens the one the user worked on last "
                    "('the ML stuff I've been working on'). Then say the last run's score from the result.",
     "parameters": {"type": "object", "properties": {
         "project": {"type": "string", "description": "folder name or path, if the user named one (e.g. 'cifar10')"}}}},
    {"name": "ml_github",
     "description": "Pull an ML project from GitHub and open it on the ML desk ('pull up the pytorch cifar repo'). "
                    "First call it with what the user said: it finds the repo but downloads nothing. Say its name "
                    "and owner and ask if that's the one. Only after a yes, call it again with confirm true and "
                    "query set to the full_name it returned: it downloads the repo, works out what kind of model "
                    "it is, and that model's panels unfold. A new repo takes about a minute; say you're setting it up. "
                    "Any PyTorch or scikit-learn repo works, notebooks too; a folder URL (github.com/owner/repo/tree/"
                    "main/mnist) opens just that example. Once it's ready, offer to train it (ml_train).",
     "parameters": {"type": "object", "properties": {
         "query": {"type": "string", "description": "the repo as the user said it, owner/repo, or a GitHub URL "
                                                    "(a /tree/<branch>/<folder> URL for one folder of it)"},
         "confirm": {"type": "boolean", "description": "true only after the user said yes to the repo you found"}},
         "required": ["query"]}},
    {"name": "ml_new",
     "description": "Start a NEW machine-learning project from scratch, for any request to make, build, start or "
                    "train a new model or ML project ('start an RNN project', 'build a CNN on handwritten digits', "
                    "'train a transformer on movie reviews', 'fine-tune distilbert on movie reviews'). "
                    "It picks a Hugging Face dataset, downloads a sample, writes the project's code (an RNN, LSTM, "
                    "GRU, Transformer, CNN or MLP from scratch, or a pretrained Hugging Face encoder fine-tuned with a "
                    "new head) and starts training: the ML desk opens by itself in about 10 to 30 seconds and the "
                    "curves go live (a pretrained model's first run also downloads its weights, so it can take a "
                    "couple of minutes). Pass what the user said; pass base_model when they named a Hugging Face "
                    "model to fine-tune (e.g. 'distilbert', 'roberta-base', 'google/electra-small-discriminator'). "
                    "Then say in one short sentence what you're building from the result (model, dataset, classes); "
                    "the score is spoken when it finishes. Don't call ml_open or ml_train after it.",
     "parameters": {"type": "object", "properties": {
         "request": {"type": "string", "description": "the user's own words for the project"},
         "model": {"type": "string", "enum": ["rnn", "lstm", "gru", "transformer", "cnn", "mlp", "hf"],
                   "description": "the model the user named, if any ('hf' = fine-tune a pretrained encoder)"},
         "base_model": {"type": "string", "description": "the Hugging Face model to fine-tune: an id "
                                                        "(owner/name), a family ('distilbert', 'roberta'), or a "
                                                        "model name the user said. Leave it out to train from scratch"},
         "dataset": {"type": "string", "description": "a Hugging Face dataset id (owner/name) or what the data is "
                                                      "about ('movie reviews', 'handwritten digits'), if the user said"},
         "task": {"type": "string", "enum": ["text_classification", "image_classification"],
                  "description": "only if the user was clear about text or images"},
         "epochs": {"type": "integer", "description": "how many epochs, if the user said (default 8 from scratch, "
                                                      "3 when fine-tuning)"},
         "publish": {"type": "boolean", "description": "true if the user also asked to put it on GitHub or open a "
                                                       "pull request; after training Albie asks before opening it"}},
         "required": ["request"]}},
    {"name": "ml_publish",
     "description": "Put the ML project Albie built (ml_new) on GitHub as a pull request: its code, a README with the "
                    "training results, and the data when it's small ('push it to GitHub', 'create a PR'). Call it "
                    "first without confirm: nothing is posted, and you get the repo and branch; say them and ask "
                    "'open the pull request?'. Only after a clear yes, call it again with confirm true. If you "
                    "already offered the pull request after training and the user said yes, call it with confirm true.",
     "parameters": {"type": "object", "properties": {
         "confirm": {"type": "boolean", "description": "true only after the user said yes to this pull request"}}}},
    {"name": "ml_status",
     "description": "The open ML project: whether it's training, and the last run's numbers (validation "
                    "accuracy, train/val gap, weakest classes). Use it for 'how's my model doing?'.",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "ml_improve",
     "description": "The user asks how to make the model better ('what can I do better?', 'improve it'). Looks at "
                    "the last run and writes a code change; it takes about half a minute and you will speak the "
                    "suggestions yourself when they're ready, ending with whether to apply and retrain. Just say "
                    "you're looking at the run. Nothing changes in their code until ml_apply.",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "ml_apply",
     "description": "Only after the user says yes to the suggested change: apply it to their code and start "
                    "training again. The curves stream live; the new score is spoken when the run finishes.",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "ml_undo",
     "description": "Put the training code back the way it was before the applied improvement.",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "ml_train",
     "description": "Train the open ML project again (the Train button). The new score is spoken when it's done.",
     "parameters": {"type": "object", "properties": {
         "epochs": {"type": "integer", "description": "how many epochs, if the user said (default 10)"}}}},
]


def _sentences(text: str, n: int) -> str:
    parts = re.split(r"(?<=[.!?])\s+", text.strip())
    return " ".join(parts[:n])


COUNT = {1: "one", 2: "two", 3: "three", 4: "four", 5: "five"}


def titles(plan: dict) -> list[str]:
    return [t for t in (str(c.get("title") or "").strip().rstrip(".") for c in plan.get("changes") or []) if t]


def spoken(plan: dict) -> str:
    """What Albie says about a proposal, about 20 words (the brevity rule): the diagnosis, the first change and
    the question. The full list and the diff are on the card. Never raises: Albie always answers."""
    try:
        changes = titles(plan)
        changes = [c[0].lower() + c[1:] if not any(ch.isupper() for ch in c.split()[0][1:]) else c for c in changes]  # keep "AdamW", "LR"
        if len(changes) > 1:
            what = f"I'd make {COUNT.get(len(changes), len(changes))} changes, starting with {changes[0]}."
        elif changes:
            what = f"I'd change one thing: {changes[0]}."
        else:
            what = "I have a change ready."
        return f"{_sentences(str(plan.get('diagnosis') or ''), 1)} {what} Apply it and retrain?".strip()
    except Exception:  # noqa: BLE001 - an odd proposal still gets a spoken line
        log.exception("spoken ML line")
        return "I have a change ready; it's on the card. Apply it and retrain?"


def card(plan: dict, n: int) -> ev.Card:
    """The proposal card over Albie: Apply runs the same path as a spoken yes (ml_apply), Not now drops it."""
    changes = titles(plan)
    return ev.Card(title=f"Apply {len(changes) or 'the'} change{'s' if len(changes) != 1 else ''} and retrain?",
                   bullets=changes[:5], actions=[ev.CardAction(label="Apply", action_id=f"ml:apply:{n}"),
                                                 ev.CardAction(label="Not now", action_id=f"ml:dismiss:{n}")])


def project_kind(path: Path, new_root: Path, clones: Path) -> str:
    """"new" (ml_new built it), "github" (ml_github cloned it) or "example" (the template, ml-examples/)."""
    for root, kind in ((new_root, "new"), (clones, "github")):
        try:
            if path.resolve().is_relative_to(root.resolve()):
                return kind
        except OSError:
            pass
    return "example"


def _json_file(path: Path) -> dict[str, Any]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def project_card(path: Path, kind: str, open_svc: Any = None) -> dict[str, Any]:
    """What the Projects tab shows for one ML project. Reads only small files (project.json, .forge/profile.json,
    the newest run's summary.json): no setup, no LLM, so listing every project stays quick."""
    built = _json_file(path / "project.json")       # ml_new's plan: title, task, model, dataset
    profile = _json_file(path / ".forge" / "profile.json")  # setup's classification: summary, archetype
    title = str(built.get("title") or "").strip() or path.name.replace("-", " ").replace("_", " ")
    summary = str(profile.get("summary") or "").strip() or None
    runs_dir = path / ".forge" / "runs"
    runs = sorted((p for p in runs_dir.iterdir() if p.is_dir()), key=lambda p: p.stat().st_mtime) if runs_dir.is_dir() else []
    best = None
    for run in reversed(runs):
        summary_json = _json_file(run / "summary.json")
        value = summary_json.get("best_val_acc") or summary_json.get("accuracy") or summary_json.get("r2")
        if isinstance(value, (int, float)):
            best = round(float(value), 4)
            break
    if open_svc is not None:
        status = str(open_svc.project.get("status") or "idle")
        training = bool(open_svc.training_active())
        summary = summary or open_svc.project.get("summary")
    else:
        status = "ready" if (path / ".forge" / "profile.json").exists() else "not set up"
        training = False
    card: dict[str, Any] = {"name": path.name, "title": title, "kind": kind, "status": status, "open": open_svc is not None,
                            "training": training, "runs": len(runs), "best_metric": best}
    if summary:
        card["summary"] = summary[:240]
    for key in ("task", "model", "dataset", "base_model"):
        if built.get(key):
            card[key] = built[key]
    return card


class MLTools:
    """One per hub: the app state (bus, MLService), where recent projects are kept, and Albie's voice."""

    def __init__(self, state: Any, data_dir: Path, speak: Speak, pet_id: str = PET_ID) -> None:
        self.state, self.speak, self.pet_id = state, speak, pet_id
        self.recent_path = Path(data_dir) / "ml" / "recent.json"
        self.jobs: set[asyncio.Task[None]] = set()
        self.offer = 0  # the live proposal card's number; 0 = none (a tap on an older card does nothing)
        self.publish_offer: dict[str, Any] | None = None  # the pull request Albie described and is waiting for a yes on

    async def call(self, name: str, args: dict[str, Any]) -> dict[str, Any]:
        """Run one tool. Errors come back as {"error": ...} so the model can explain them."""
        try:
            if name == "ml_open":
                return await self.open(str(args.get("project") or "").strip())
            if name == "ml_github":
                return await self.github(str(args.get("query") or "").strip(), args.get("confirm") is True)
            if name == "ml_new":
                return await self.new(args)
            if name == "ml_publish":
                return await self.publish(args.get("confirm") is True)
            if name == "ml_status":
                return self.status()
            if name == "ml_improve":
                return self.start_improve()
            if name == "ml_apply":
                self.offer = 0  # the spoken yes: the card's Apply button is spent too
                return await self.apply()
            if name == "ml_undo":
                restored = await asyncio.to_thread(improve.undo, self.svc)
                return {"restored": restored} if restored else {"note": "no improvement is applied; nothing to undo"}
            if name == "ml_train":
                epochs = args.get("epochs")
                return self.train(10 if epochs is None else int(epochs))
        except (improve.ImproveError, Conflict, NotFound, ValueError, new_project.NewProjectError,
                hf_data.DatasetError, github_publish.PublishError) as e:
            return {"error": str(e)}
        return {"error": f"Unknown ML tool {name!r}"}

    @property
    def svc(self):
        return service(self.state)

    # --- open -----------------------------------------------------------------------------------
    def recent(self) -> list[str]:
        try:
            return [p for p in json.loads(self.recent_path.read_text()) if Path(p).is_dir()]
        except (OSError, ValueError):
            return []

    def remember(self, path: Path) -> None:
        paths = [str(path)] + [p for p in self.recent() if p != str(path)]
        self.recent_path.parent.mkdir(parents=True, exist_ok=True)
        self.recent_path.write_text(json.dumps(paths[:10], indent=1))

    def resolve(self, name: str) -> Path:
        """A path, a project folder name (recent ones first, then ml-examples/), or the last one opened."""
        if not name:
            recent = self.recent()
            return Path(recent[0]) if recent else self.svc.repo
        given = Path(name).expanduser()
        if given.is_absolute() and given.is_dir():
            return given
        key = name.lower().replace(" ", "").replace("-", "").replace("_", "")
        candidates = [Path(p) for p in self.recent()]
        configured = [Path(p).expanduser() for p in os.environ.get("FORGE_ML_PROJECTS", "").split(os.pathsep) if p]
        for root in configured + SEARCH_DIRS:
            if root.is_dir():
                candidates += sorted(p for p in root.iterdir() if p.is_dir() and not p.name.startswith("."))
        for path in candidates:
            if key in path.name.lower().replace("-", "").replace("_", ""):
                return path
        raise NotFound(f"no ML project called {name!r}; I know {', '.join(sorted({p.name for p in candidates})) or 'none'}")

    async def open(self, name: str) -> dict[str, Any]:
        path, svc = self.resolve(name), self.svc
        running = getattr(self.state, "ml_setup", None)
        if svc.repo == path.resolve() and svc.project.get("status") == "ready":
            await self.state.bus.publish("ml.layout", {**svc.project["layout"], "open": True})  # already set up
        else:
            if running is not None and not running.done():
                raise Conflict("I'm still setting up a project; give me a moment")
            svc.open_project(str(path))
            _stop_tail(self.state)
            start_setup(self.state, open_desk=True)
            deadline = asyncio.get_running_loop().time() + OPEN_WAIT_S
            while not self.state.ml_setup.done() and asyncio.get_running_loop().time() < deadline:
                await asyncio.sleep(0.25)
        self.remember(svc.repo)
        out: dict[str, Any] = {"project": svc.repo.name, "status": svc.project.get("status"),
                               "summary": svc.project.get("summary")}
        if out["status"] == "needs_approval":
            out["note"] = "Setup wrote a small patch so training streams live; the user accepts it on the Training panel."
        elif out["status"] not in ("ready", "failed"):
            out["note"] = "Still setting up; the desk opens by itself when it's ready."
        elif out["status"] == "failed":
            out["error"] = svc.project.get("error")
        out.update(self._last_run())
        return out

    # --- every project, for the headset's Projects tab (GET /ml/projects) -----------------------------
    def project_dirs(self) -> list[tuple[Path, str]]:
        """(folder, kind) for every ML project here, the open one and the recent ones first."""
        svc = self.svc
        new_root, clones = self.new_root(), self.recent_path.parent / "github"
        configured = [Path(p).expanduser() for p in os.environ.get("FORGE_ML_PROJECTS", "").split(os.pathsep) if p]
        found: list[Path] = [svc.repo] + [Path(p) for p in self.recent()]
        for root in (new_root, clones, *configured, *SEARCH_DIRS):
            if root.is_dir():
                found += sorted((p for p in root.iterdir() if p.is_dir() and not p.name.startswith(".")),
                                key=lambda p: p.stat().st_mtime, reverse=True)
        return [(p, project_kind(p, new_root, clones)) for p in found]

    def projects(self) -> list[dict[str, Any]]:
        """One card per project (project_card), each folder once."""
        svc, seen, out = self.svc, set(), []
        current = svc.repo.resolve()
        for path, kind in self.project_dirs():
            try:
                real = path.resolve()
            except OSError:
                continue
            if real in seen or not real.is_dir():
                continue
            seen.add(real)
            out.append(project_card(real, kind, svc if real == current else None))
        return out

    async def github(self, query: str, confirm: bool) -> dict[str, Any]:
        """Find the repo; only on the confirmed second call download it and open it like ml_open."""
        if not query:
            raise ValueError("say which GitHub repo to pull up")
        found = await asyncio.to_thread(github.find, query)
        if not confirm:
            return {**found, "note": "Nothing downloaded yet. Ask the user if this is the repo; "
                                     "if yes, call ml_github again with confirm true."}
        path = await asyncio.to_thread(github.clone, found["full_name"], self.recent_path.parent / "github")
        if found.get("folder"):
            inner = (path / found["folder"]).resolve()
            if not inner.is_dir() or not inner.is_relative_to(path.resolve()):
                raise NotFound(f"{found['full_name']} has no folder {found['folder']!r}")
            path = inner
        try:  # notebooks to scripts, the packages it imports, before any LLM call (repo_env)
            env = await asyncio.to_thread(repo_env.prepare, path)
        except repo_env.UnsupportedRepo as e:
            return {"repo": found["full_name"], "error": f"I downloaded {found['full_name']}, but {e}."}
        out = {"repo": found["full_name"], **await self.open(str(path))}
        if env.get("installed"):
            out["installed"] = env["installed"]
        if env.get("notebooks"):
            out["notebooks"] = env["notebooks"][:5]
        return out

    # --- a new project from a sentence (ml_new) ---------------------------------------------------
    def new_root(self) -> Path:
        return self.recent_path.parent / "new"

    async def new(self, args: dict[str, Any]) -> dict[str, Any]:
        """Plan now (a second or two: the dataset's layout, no rows), then build, open and train in the background."""
        building = getattr(self.state, "ml_building", None)
        if building is not None and not building.done():
            raise Conflict("I'm still building the last project; give me a moment")
        running = getattr(self.state, "ml_setup", None)
        if running is not None and not running.done():
            raise Conflict("I'm still setting up a project; give me a moment")
        if self.svc.training_active():
            raise Conflict("a run is still training; wait for it to finish, or tell me to stop it")
        raw_epochs = args.get("epochs")
        if raw_epochs is not None and not 1 <= int(raw_epochs) <= 30:
            raise ValueError("epochs must be between 1 and 30")
        model = args.get("model")
        base_model = str(args.get("base_model") or "").strip() or None
        p = await asyncio.to_thread(new_project.plan, model=str(model) if model else None,
                                    dataset=str(args.get("dataset") or "").strip() or None, task=args.get("task") or None,
                                    request=str(args.get("request") or ""), base_model=base_model)
        epochs = int(raw_epochs) if raw_epochs is not None else new_project.default_epochs(p)
        task = asyncio.create_task(self._build_new(p, epochs, args.get("publish") is True))
        self.state.ml_building = task
        self.jobs.add(task)
        task.add_done_callback(self.jobs.discard)
        note = ("Downloading the data and writing the code now. " if not p.get("base_model") else
                "Downloading the data and the pretrained model (the weights are cached after the first time), and "
                "writing the code now. ")
        return {**new_project.describe(p), "epochs": epochs, "status": "building",
                "note": note + "Say in one short sentence what you're building (model, dataset, what it tells apart). "
                        "The ML desk opens by itself and the training curves go live; the score is spoken when it's done."}

    async def _build_new(self, p: dict[str, Any], epochs: int, publish: bool = False) -> None:
        svc = self.svc
        try:
            path = await asyncio.to_thread(new_project.build, p, self.new_root(), lambda _: None, epochs)
        except Exception as e:  # noqa: BLE001 - say it instead of failing silently
            log.exception("ml_new build failed")
            await self.say(f"I couldn't get {p['dataset']} from Hugging Face: {e}")
            return
        try:
            svc.open_project(str(path))
            _stop_tail(self.state)
            start_setup(self.state, open_desk=True)
        except Conflict as e:
            await self.say(f"I built the project but couldn't open it: {e}")
            return
        while not self.state.ml_setup.done():
            await asyncio.sleep(0.25)
        if svc.project.get("status") != "ready":
            await self.say(f"I built the project but it didn't pass its checks: "
                           f"{svc.project.get('error') or svc.project.get('status')}")
            return
        self.remember(svc.repo)
        try:
            run_id = start_run(self.state, epochs)
        except Conflict as e:
            await self.say(f"The project is ready, but I couldn't start training: {e}")
            return
        what = "texts" if p["task"] == "text_classification" else "images"
        rows = json.loads((path / "project.json").read_text())["rows"]["train"]
        await self.say(f"Got {rows:,} {what}. Training the {p['model_name']} now; watch the curves.")
        if not publish:
            self._announce(run_id, {})
            return
        await self._when_done(run_id, {})
        await self.offer_publish()

    # --- the built project as a GitHub pull request (ml_publish) ---------------------------------------
    async def offer_publish(self) -> None:
        """After training, when the user asked for a PR up front: describe it and ask (nothing is posted yet)."""
        try:
            plan = await asyncio.to_thread(github_publish.plan, self.svc.repo)
        except github_publish.PublishError as e:
            await self.say(f"I can't open the pull request: {e}.")
            return
        self.publish_offer = {**plan, "path": str(self.svc.repo)}
        where = f"a new repo, {plan['repo']}" if plan["create_repo"] else plan["repo"]
        await self.say(f"Shall I open a pull request with the project in {where}?")
        await agent_speech.note(self.pet_id, "You just offered to open a GitHub pull request with the ML project. A yes "
                                             "means ml_publish with confirm true; a no means say okay.")

    async def publish(self, confirm: bool) -> dict[str, Any]:
        svc = self.svc
        if svc.training_active():
            raise Conflict("it's still training; I'll put it on GitHub once the results are in")
        offer = self.publish_offer
        if not confirm or offer is None or offer.get("path") != str(svc.repo):
            plan = await asyncio.to_thread(github_publish.plan, svc.repo)
            self.publish_offer = {**plan, "path": str(svc.repo)}
            return {"status": "awaiting_confirmation", **plan,
                    "next": "Nothing was posted. Say the repo (and that it's new, if create_repo) and ask whether to "
                            "open the pull request. Only after a clear yes, call ml_publish again with confirm true."}
        self.publish_offer = None
        done = await asyncio.to_thread(github_publish.publish, svc.repo, offer)
        await self.state.bus.publish("code.view", {"repo": done["repo"], "pr": done["number"]})  # the code panel shows it
        return {"status": "opened", **done,
                "note": "Say the pull request number and that it's open in the code panel."}

    def _last_run(self) -> dict[str, Any]:
        try:
            d = improve.diagnose(self.svc)
        except (improve.ImproveError, NotFound):
            return {"last_run": "none yet"}
        keep = ("best_val_acc", "val_acc", "train_acc", "train_val_gap", "overfit", "weakest_classes", "r2", "extra")
        return {"last_run": improve.headline(d), **{k: d[k] for k in keep if k in d}}

    def status(self) -> dict[str, Any]:
        svc = self.svc
        out: dict[str, Any] = {"project": svc.repo.name, "setup": svc.project.get("status"),
                               "training": svc.train_run_id if svc.training_active() else None}
        pending = improve.plan(svc)
        if pending:
            out["improvement"] = {"applied": pending["applied"], "changes": [c["title"] for c in pending["changes"]]}
        out.update(self._last_run())
        return out

    # --- improve, apply, train -------------------------------------------------------------------
    def start_improve(self) -> dict[str, Any]:
        if getattr(self.state, "ml_improving", None) is not None and not self.state.ml_improving.done():
            return {"status": "already working on it"}
        improve.diagnose(self.svc)  # no run yet: say so now, not half a minute later
        llm = getattr(self.state, "ml_llm", None)  # tests inject a fake
        task = asyncio.create_task(self._improve(llm))
        self.state.ml_improving = task
        self.jobs.add(task)
        task.add_done_callback(self.jobs.discard)
        return {"status": "started", "note": "Looking at the run now; the suggestions are spoken when ready."}

    async def _improve(self, llm) -> None:
        svc = self.svc
        try:
            kwargs = {"llm": llm} if llm is not None else {}
            plan = await asyncio.to_thread(improve.propose, svc, **kwargs)
        except Exception as e:  # noqa: BLE001 - say it instead of failing silently
            log.exception("ml improve failed")
            await self.say(f"I couldn't work out a change: {e}")
            return
        self.offer += 1
        await self.say(spoken(plan), card(plan, self.offer))
        # Lab 2026-10-25 (2 of 2 runs): "go for it" after this offer ran ml_train, not the change. Same note as the NN lab.
        await agent_speech.note(self.pet_id, "You just offered this code change for the ML project. A yes ('do it', "
                                             "'go for it', 'sounds good') means ml_apply; a no means say okay.")

    async def card_action(self, action_id: str) -> bool:
        """A tap on the proposal card ("ml:apply:N" / "ml:dismiss:N"). Apply is ml_apply; Not now discards the
        proposal. A tap on an older card is ignored. True when the tap applied the change."""
        _, kind, n = action_id.split(":", 2) if action_id.count(":") >= 2 else ("", "", "")
        if not n.isdigit() or int(n) != self.offer:
            return False
        self.offer = 0
        if kind != "apply":
            if not improve.plan(self.svc).get("applied"):
                shutil.rmtree(improve._dir(self.svc), ignore_errors=True)  # not now: the proposal is gone
            await self.say("Okay, I'll leave it as it is.")
            return False
        try:
            out = await self.apply()
        except (improve.ImproveError, Conflict, NotFound) as e:
            await self.say(f"I couldn't apply it: {e}")
            return False
        await self.say(f"Applied. Training {out['epochs']} epochs now; the curves are live.")
        return True

    async def apply(self) -> dict[str, Any]:
        svc = self.svc
        if svc.training_active():
            raise Conflict("a run is still training; wait for it to finish")
        # A spoken yes: fold the Apply / Not now card now and caption the work (a card-less pet.say folds it).
        self.offer = 0
        await self.state.bus.publish("pet.say", {"pet_id": self.pet_id, "text": "Applying the change and retraining..."})
        plan = await asyncio.to_thread(improve.apply, svc)
        run_id = start_run(self.state, plan["epochs"])
        self._announce(run_id, plan["baseline"])
        return {"applied": list(plan["files"]), "run_id": run_id, "epochs": plan["epochs"],
                "note": "Training now with the change. The curves are live on the Training panel."}

    async def stop_training(self) -> bool:
        """"Albie, stop" during a run he started: end the training process and skip its "done" line. True if one ran."""
        svc = self.svc
        if not svc.training_active():
            return False
        for job in list(self.jobs):
            job.cancel()  # no "That run stopped without results" for a run the user stopped
        svc.train_proc.terminate()
        try:
            await asyncio.to_thread(svc.train_proc.wait, 5)
        except Exception:  # noqa: BLE001 - still running after 5 s: make sure
            svc.train_proc.kill()
        return True

    def train(self, epochs: int) -> dict[str, Any]:
        if not 1 <= epochs <= 100:
            raise ValueError("epochs must be between 1 and 100")
        try:
            before = improve.diagnose(self.svc)
            baseline = {"run_id": before["run_id"], "headline": improve.headline(before),
                        "best_val_acc": before.get("best_val_acc")}
        except (improve.ImproveError, NotFound):
            baseline = {}
        run_id = start_run(self.state, epochs)
        self._announce(run_id, baseline)
        return {"run_id": run_id, "epochs": epochs, "note": "Training; the curves are live on the Training panel."}

    def _announce(self, run_id: str, baseline: dict) -> None:
        task = asyncio.create_task(self._when_done(run_id, baseline))
        self.jobs.add(task)
        task.add_done_callback(self.jobs.discard)

    async def _when_done(self, run_id: str, baseline: dict) -> None:
        """Wait for the run's process, then say the new score next to the old one."""
        svc = self.svc
        while svc.train_run_id == run_id and svc.training_active():
            await asyncio.sleep(1.0)
        await asyncio.sleep(1.5)  # the log tail's last read and the run's results land
        try:
            d = improve.diagnose(svc, run_id)
        except (improve.ImproveError, NotFound):
            code = svc.train_proc.returncode if svc.train_proc is not None else None
            await self.say(f"That run stopped without results (exit code {code}). The log is in .forge/train-{run_id}.log.")
            return
        new, old = d.get("best_val_acc"), baseline.get("best_val_acc")
        if new is not None and old is not None:
            if f"{new:.1%}" == f"{old:.1%}":
                await self.say(f"Done! Validation accuracy stayed at {new:.1%}.")
            else:
                verb = "went up" if new > old else "went down"
                await self.say(f"Done! Validation accuracy {verb} from {old:.1%} to {new:.1%}.")
        else:
            await self.say(f"Done! {improve.headline(d)}.")

    async def say(self, text: str, card: ev.Card | None = None) -> None:
        payload: dict[str, Any] = {"pet_id": self.pet_id, "text": text}
        if card is not None:
            payload["card"] = card.model_dump(exclude_none=True)
        await self.state.bus.publish("pet.say", payload)
        try:
            await self.speak(self.pet_id, text)
        except Exception:  # noqa: BLE001 - the caption already went out
            log.exception("spoken ML line failed")
