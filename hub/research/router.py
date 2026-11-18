"""POST /research/parts: the research_parts tool over REST (the headset's panels, the cloud)."""
from __future__ import annotations

from fastapi import APIRouter, Request
from pydantic import BaseModel

from . import tools

router = APIRouter(prefix="/research", tags=["research"])


class PartsRequest(BaseModel):
    query: str
    max_price_usd: float | None = None
    voltage_v: float | None = None
    max_weight_g: float | None = None
    limit: int = 6


@router.post("/parts")
async def research_parts(req: PartsRequest, request: Request) -> dict:
    """Real listings with checks, or {"status": "not_set_up", "options"} when the server has no distributor key."""
    return await tools.call_tool("research_parts", req.model_dump(), getattr(request.app.state, "cad", None))


@router.get("/parts/status")
def parts_status() -> dict:
    from . import parts
    return {"configured": parts.configured(), "options": parts.OPTIONS}
