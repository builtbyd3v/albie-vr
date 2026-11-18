"""Albie's model tools on the hub (voice model + Claude runner), registered through agent/tools.py like the desk tools.

model_search only reads. model_import is for one model the user picked: the server downloads and checks it, then
`publish("models.import", ...)` asks the headset to save it as an artifact and open it (ModelImportTools.cs listens).
Nothing is ever imported in bulk or without the user choosing it.
"""
from __future__ import annotations

from typing import Any, Awaitable, Callable

from pydantic import BaseModel

from . import service

Publish = Callable[[str, Any], Awaitable[None]]


class ModelsImport(BaseModel):
    """Payload of `models.import`: the headset fetches /models/file?id= itself and checks sha256."""
    id: str
    name: str
    author: str
    licence: str
    source_url: str
    sha256: str
    size_bytes: int


TOOLS: list[dict[str, Any]] = [
    {"name": "model_search",
     "description": "Find real, open-licensed 3D models by name (a jet engine, a T-rex skull, a chair) that can be shown "
                    "in the room. Returns a few hits: id, name, author, licence. Say the top two or three by name and "
                    "author and ask which one; never import one the user didn't pick.",
     "parameters": {"type": "object", "properties": {
         "query": {"type": "string", "description": "what to look for, in a few plain words"},
         "limit": {"type": "integer", "description": "how many hits, 1 to 10 (default 5)"}},
         "required": ["query"]}},
    {"name": "model_import",
     "description": "Bring ONE model the user chose into the room as an artifact. `id` is a hit's id from model_search. "
                    "Then say its name, who made it and its licence (the credit is also shown on its panel).",
     "parameters": {"type": "object", "properties": {
         "id": {"type": "string", "description": "the chosen hit's id, exactly as model_search gave it"}},
         "required": ["id"]}},
]


async def call_tool(name: str, args: dict[str, Any], publish: Publish | None = None) -> dict[str, Any]:
    """Run one tool. Errors come back as {"error": ...} so the model can explain them."""
    try:
        if name == "model_search":
            limit = args.get("limit")
            found = await service.search(str(args.get("query") or ""), min(int(limit), 10) if limit else 5)
            hits = [h.model_dump(include={"id", "name", "author", "licence", "size_bytes", "triangles"}) for h in found.hits]
            return {"hits": hits, **({"note": found.note} if found.note else {})}
        if name == "model_import":
            meta, _ = await service.fetch(str(args.get("id") or ""))
            result = {"id": meta.id, "name": meta.name, "author": meta.author, "licence": meta.licence,
                      "attribution": meta.attribution}
            if publish is None:
                return {**result, "error": "the model is ready on the server, but there's no headset connected to show it"}
            await publish("models.import", ModelsImport(**meta.model_dump(include=set(ModelsImport.model_fields))))
            return {**result, "status": "sent to the headset: it saves the model as an artifact and opens it"}
        return {"error": f"unknown tool {name!r}"}
    except service.ModelError as e:
        return {"error": f"{e.code}: {e.message}"}
    except (TypeError, ValueError) as e:
        return {"error": f"bad_value: {e}"}
