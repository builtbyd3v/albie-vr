"""Albie's parts-research tool (registered in hub/main.py like the other modules' tools)."""
from __future__ import annotations

import asyncio
from typing import Any

from . import parts

TOOLS: list[dict[str, Any]] = [
    {"name": "research_parts",
     "description": "Look up real electronic and hardware parts from a distributor ('find a camera module for the "
                    "drone', 'a 2205 brushless motor under 20 dollars'): part number, maker, price breaks, stock, "
                    "datasheet, and checks against the budget, voltage and weight (the user's, or the open CAD "
                    "design's when it states them). If the result says not_set_up, tell the user parts search needs "
                    "a distributor key on the server and that you won't make parts up. Never name a part that isn't "
                    "in the result; say 'not listed' for specs a card doesn't state.",
     "parameters": {"type": "object", "properties": {
         "query": {"type": "string", "description": "the part, in a few words ('OV5640 camera module')"},
         "max_price_usd": {"type": "number", "description": "budget per part in US dollars, if the user said"},
         "voltage_v": {"type": "number", "description": "the supply voltage it must run on, if the user said"},
         "max_weight_g": {"type": "number", "description": "the most it may weigh in grams, if the user said"},
         "limit": {"type": "integer", "description": "how many parts, 1 to 10 (default 6)"}},
         "required": ["query"]}},
]


async def call_tool(name: str, args: dict[str, Any], cad: Any = None) -> dict[str, Any]:
    """Run one tool. Errors come back as {"error": ...} so the model can explain them."""
    if name != "research_parts":
        return {"error": f"Unknown research tool {name!r}"}
    limits = {k: args.get(k) for k in ("max_price_usd", "voltage_v", "max_weight_g") if args.get(k) is not None}
    try:
        return await asyncio.to_thread(parts.search, str(args.get("query") or ""), int(args.get("limit") or 6),
                                       limits, cad)
    except (parts.PartsError, ValueError) as e:
        return {"error": str(e)}
