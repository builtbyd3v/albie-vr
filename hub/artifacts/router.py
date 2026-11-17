"""Artifacts router: POST /artifacts/review (advisory Jev review, never a 5xx). See review.py."""
from __future__ import annotations

from fastapi import APIRouter

from . import review
from .review import ReviewIn, ReviewOut

router = APIRouter(prefix="/artifacts", tags=["artifacts"])


@router.post("/review", response_model=ReviewOut)
async def post_review(body: ReviewIn) -> ReviewOut:
    return await review.review(body)
