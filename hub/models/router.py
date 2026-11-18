"""Models router. A refusal is {"detail": "<code>: <message>"} with the status service.ModelError carries."""
from __future__ import annotations

from urllib.parse import quote

from fastapi import APIRouter, HTTPException, Query
from fastapi.responses import Response

from . import service
from .service import ModelFile, ModelSearch

router = APIRouter(prefix="/models", tags=["models"])


def _refuse(e: service.ModelError) -> HTTPException:
    return HTTPException(e.status, f"{e.code}: {e.message}")


@router.get("/search", response_model=ModelSearch)
async def get_search(q: str = Query(..., min_length=1, max_length=100), limit: int = Query(5, ge=1, le=20)) -> ModelSearch:
    try:
        return await service.search(q, limit)
    except service.ModelError as e:
        raise _refuse(e) from None


@router.get("/fetch", response_model=ModelFile)
async def get_fetch(id: str = Query(..., max_length=64)) -> ModelFile:  # noqa: A002 - the query parameter is "id"
    try:
        meta, _ = await service.fetch(id)
    except service.ModelError as e:
        raise _refuse(e) from None
    return meta


@router.get("/file")
async def get_file(id: str = Query(..., max_length=64)) -> Response:  # noqa: A002
    try:
        meta, data = await service.fetch(id)
    except service.ModelError as e:
        raise _refuse(e) from None
    # Header values are percent-encoded: names and authors aren't always Latin-1.
    headers = {
        "X-Model-Id": meta.id, "X-Model-Sha256": meta.sha256, "X-Model-Licence": meta.licence,
        "X-Model-Licence-Url": meta.licence_url, "X-Model-Source-Url": quote(meta.source_url, safe=":/?&=%-._~"),
        "X-Model-Name": quote(meta.name), "X-Model-Author": quote(meta.author),
        "X-Model-Attribution": quote(meta.attribution), "Cache-Control": "private, max-age=86400",
    }
    return Response(content=data, media_type="model/gltf-binary", headers=headers)
