"""Bit's ML tools (voice model + Claude runner), registered through agent/tools.py like the messaging tools.

ml_get_run_summary and ml_relabel run at once (a relabel only writes labels_override.json, and Bit can relabel back).
ml_remove and ml_start_training only store a pending action and return awaiting_confirmation; ml_confirm is the
spoken "yes" / "never mind". One pending action at a time: a new one replaces the old.
"""
from __future__ import annotations

from typing import Any

from .models import SampleUpdated
from .service import Conflict, MLService, NotFound, safe_id

HUB_URL = "http://localhost:8000"  # where the training script sends its ml.* events (ponytail: the hub's fixed port)

TOOLS: list[dict[str, Any]] = [
    {"name": "ml_get_run_summary",
     "description": "How the last training run did: accuracy, the 3 worst classes and the 3 most confused class pairs "
                    "(true -> predicted). Pass run_id for an older run. Use it for 'how did training go?'.",
     "parameters": {"type": "object", "properties": {
         "run_id": {"type": "string", "description": "optional; default the latest run with results"}}}},
    {"name": "ml_relabel",
     "description": "Fix one sample's label in the dataset. Runs at once (the dot on the results wall updates). label "
                    "is a class name ('cat') or its index.",
     "parameters": {"type": "object", "properties": {
         "sample_id": {"type": "string", "description": "sample id, e.g. val_0239"},
         "label": {"type": ["string", "integer"], "description": "class name or index"}},
         "required": ["sample_id", "label"]}},
    {"name": "ml_remove",
     "description": "Drop one sample from the dataset. Does NOT remove yet: returns awaiting_confirmation. Ask the user, "
                    "then call ml_confirm.",
     "parameters": {"type": "object", "properties": {
         "sample_id": {"type": "string", "description": "sample id, e.g. val_0239"}}, "required": ["sample_id"]}},
    {"name": "ml_start_training",
     "description": "Train the model again. Does NOT start yet: returns awaiting_confirmation. Ask the user, then call "
                    "ml_confirm.",
     "parameters": {"type": "object", "properties": {
         "epochs": {"type": "integer", "minimum": 1, "maximum": 100, "description": "how many epochs"}},
         "required": ["epochs"]}},
    {"name": "ml_confirm",
     "description": "The user's decision on the pending ml_remove / ml_start_training. confirm=true for 'yes' / 'go "
                    "ahead', false for 'cancel' / 'never mind'. Only call after the user clearly decided.",
     "parameters": {"type": "object", "properties": {
         "confirm": {"type": "boolean", "description": "true = do it, false = drop it"}}, "required": ["confirm"]}},
]

pending: dict[str, Any] | None = None  # {"action": "remove", "sample_id"} | {"action": "train", "epochs"}


def _summary(svc: MLService, run_id: str | None) -> dict[str, Any]:
    if not run_id:
        latest = svc.latest_run("summary.json")
        if latest is None:
            return {"error": "no run has results yet"}
        run_id = latest.name
    s = svc.run_file(run_id, "summary.json")
    per_class = s.get("per_class") or []
    names = {c["idx"]: c["name"] for c in per_class}
    out: dict[str, Any] = {"run_id": run_id, "archetype": s.get("archetype")}
    for key in ("accuracy", "r2", "mae", "rmse"):
        if s.get(key) is not None:
            out[key] = round(s[key], 3)
    out["worst_classes"] = [{"name": c["name"], "accuracy": round(c["accuracy"], 3)}
                            for c in sorted(per_class, key=lambda c: c["accuracy"])[:3]]
    out["top_confusions"] = [{"true": names.get(p["true_idx"], str(p["true_idx"])),
                              "predicted": names.get(p["pred_idx"], str(p["pred_idx"])), "count": p["count"]}
                             for p in sorted(s.get("top_confusions") or [], key=lambda p: -p["count"])[:3]]
    return out


def _label_idx(svc: MLService, label: Any) -> int:
    if isinstance(label, int) or (isinstance(label, str) and label.strip().isdigit()):
        return int(label)
    want = str(label or "").strip().lower()
    for i, name in enumerate(svc.classes()):
        if name.lower() == want:
            return i
    raise Conflict(f"no class named {label!r}")


def _sample_id(args: dict[str, Any]) -> str:
    sample_id = str(args.get("sample_id") or "").strip()
    if not sample_id:
        raise Conflict("sample_id is empty")
    return safe_id(sample_id, "sample id")


async def _publish(bus: Any, update: dict) -> dict:
    event = SampleUpdated(**update)
    await bus.publish("ml.sample_updated", event)
    return event.model_dump()


async def call_tool(name: str, args: dict[str, Any], svc: MLService, bus: Any) -> dict[str, Any]:
    """Run one tool. Errors come back as {"error": ...} so the model can explain them."""
    global pending
    try:
        if name == "ml_get_run_summary":
            return _summary(svc, args.get("run_id"))
        if name == "ml_relabel":
            sample_id = _sample_id(args)
            return await _publish(bus, svc.relabel(sample_id, _label_idx(svc, args.get("label"))))
        if name == "ml_remove":
            sample_id = _sample_id(args)
            svc._current_label(sample_id)  # unknown ids fail now, not after the user said yes
            pending = {"action": "remove", "sample_id": sample_id}
            return {"status": "awaiting_confirmation", **pending,
                    "next": "Ask the user to confirm removing this sample. Not removed yet."}
        if name == "ml_start_training":
            epochs = args.get("epochs")
            if not isinstance(epochs, int) or not 1 <= epochs <= 100:
                return {"error": "epochs must be a whole number from 1 to 100"}
            if svc.training_active():
                return {"error": f"run {svc.train_run_id} is still training"}
            pending = {"action": "train", "epochs": epochs}
            return {"status": "awaiting_confirmation", **pending,
                    "next": "Ask the user to confirm starting training. Not started yet."}
        if name == "ml_confirm":
            if pending is None:
                return {"error": "nothing is waiting for confirmation"}
            action, pending = pending, None
            if not args.get("confirm"):
                return {"status": "cancelled", "action": action["action"]}
            if action["action"] == "remove":
                return {"status": "done", "action": "remove", **await _publish(bus, svc.remove(action["sample_id"]))}
            return {"status": "started", "action": "train", "run_id": svc.start_training(action["epochs"], HUB_URL)}
    except (NotFound, Conflict, RuntimeError) as e:
        return {"error": str(e)}
    return {"error": f"Unknown ML tool {name!r}"}
