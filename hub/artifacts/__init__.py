"""Artifacts hub module: an optional, advisory Jev review of a model-written artifact (plan step C1).

Unity's artifact_create posts the manifest, the scene and its deterministic facts text to POST /artifacts/review
and gets typed verdicts back. It never blocks a save: no TYPESAFE_API_KEY means status "skipped", and a Jev error
or timeout means status "error" (both HTTP 200). Mount in hub/main.py with `app.include_router(artifacts_router)`.
"""
from .router import router

__all__ = ["router"]
