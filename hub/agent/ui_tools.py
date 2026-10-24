"""Bit's workspace tools: open a mode, point at a panel. Registered from main.py; they only broadcast on /ws
(Unity's BitUiDirector acts on them). Events: ui.open_mode {mode} and ui.highlight {panel, item_ids?, class_idx?}.
"""
from __future__ import annotations

from typing import Any

from events import UiHighlight, UiOpenMode

MODES = ("code", "cad", "ml", "messages")
PANEL_IDS = ("screen-main", "ml-datastack", "ml-model3d", "ml-results", "ml-training", "cad-viewer", "messages",
             "messages-mentions")  # PROJECT_CONTEXT section 7

TOOLS: list[dict[str, Any]] = [
    {"name": "ui_open_mode",
     "description": "Open a workspace mode around the desk: code (just the editor), cad (the rocket), ml (dataset, "
                    "model, training, results), messages (Slack).",
     "parameters": {"type": "object", "properties": {"mode": {"type": "string", "enum": list(MODES)}},
                    "required": ["mode"]}},
    {"name": "ui_highlight",
     "description": "Point the user at a panel: its frame pulses. Its mode must be open (ui_open_mode first). On "
                    "ml-results, item_ids selects that sample and class_idx shows that class's mistakes.",
     "parameters": {"type": "object", "properties": {
         "panel": {"type": "string", "enum": list(PANEL_IDS)},
         "item_ids": {"type": "array", "items": {"type": "string"}, "description": "optional sample ids"},
         "class_idx": {"type": "integer", "minimum": 0, "description": "optional class index"}},
         "required": ["panel"]}},
]


async def call_tool(name: str, args: dict[str, Any], bus: Any) -> dict[str, Any]:
    if name == "ui_open_mode":
        mode = args.get("mode")
        if mode not in MODES:
            return {"error": f"mode must be one of {', '.join(MODES)}"}
        await bus.publish("ui.open_mode", UiOpenMode(mode=mode))
        return {"ok": True, "mode": mode}
    if name == "ui_highlight":
        panel, item_ids, class_idx = args.get("panel"), args.get("item_ids"), args.get("class_idx")
        if panel not in PANEL_IDS:
            return {"error": f"panel must be one of {', '.join(PANEL_IDS)}"}
        if item_ids is not None and not (isinstance(item_ids, list) and all(isinstance(i, str) for i in item_ids)):
            return {"error": "item_ids must be a list of sample ids"}
        if class_idx is not None and (not isinstance(class_idx, int) or class_idx < 0):
            return {"error": "class_idx must be a class index (0 or more)"}
        await bus.publish("ui.highlight", UiHighlight(panel=panel, item_ids=item_ids, class_idx=class_idx))
        return {"ok": True, "panel": panel}
    return {"error": f"Unknown UI tool {name!r}"}
