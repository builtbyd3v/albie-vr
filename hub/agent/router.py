"""REST endpoints for the agent module (mounted at /agent). Asking Albie anything goes over /ws (agent.ask)."""

from __future__ import annotations

import asyncio

from fastapi import APIRouter, HTTPException, Request
from fastapi.responses import HTMLResponse
from pathlib import Path

from pydantic import BaseModel, ConfigDict, Field

from .build import builds
from .config import ProjectNotFound, PublicProject, public_projects, resolve_project
from .orchestration import WorkflowRequest, orchestrator

router = APIRouter(prefix="/agent", tags=["agent"])


@router.get("/projects", response_model=list[PublicProject])
async def agent_projects() -> list[PublicProject]:
    return public_projects()


@router.get("/orchestration", response_class=HTMLResponse)
async def orchestration_page() -> str:
    return (Path(__file__).parent / "orchestration.html").read_text(encoding="utf-8")


@router.post("/workflows", status_code=202)
async def start_workflow(request: WorkflowRequest) -> dict:
    try:
        project = resolve_project(request.project_id)
    except ProjectNotFound as exc:
        raise HTTPException(status_code=404, detail=str(exc)) from exc
    try:
        return orchestrator.start(project, request).public()
    except RuntimeError as exc:
        raise HTTPException(status_code=429, detail=str(exc)) from exc


@router.get("/workflows/{workflow_id}")
async def workflow_status(workflow_id: str) -> dict:
    workflow = orchestrator.workflows.get(workflow_id)
    if workflow is None:
        raise HTTPException(status_code=404, detail="Workflow not found (history is in memory).")
    return workflow.public()


class BuildRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    goal: str = Field(min_length=1, max_length=2000)
    repo: str | None = Field(default=None, max_length=200)
    backend: str | None = None
    checks: list[list[str]] = Field(default_factory=list, max_length=4)


def device_or_same_origin(request: Request) -> None:
    origin = request.headers.get("origin")
    if origin and origin.rstrip("/") != str(request.base_url).rstrip("/"):
        raise HTTPException(403, "Workflow actions must come from the device or this hub's page.")


# Builds (agent/build.py): start, watch and stop. Shipping (push + pull request) is not here on purpose: it only happens
# through build_ship with confirm, or the Open pull request button on Albie's card, after the user says yes.
@router.get("/build", response_class=HTMLResponse)
async def builds_page() -> str:
    """A page for watching builds live (the plan, each crew member's step, commits); no shipping from here."""
    return (Path(__file__).parent / "builds.html").read_text(encoding="utf-8")


@router.post("/builds", status_code=202)
async def start_build(request: BuildRequest, http_request: Request) -> dict:
    device_or_same_origin(http_request)
    try:
        return builds.start(request.goal, repo=request.repo, backend=request.backend, checks=request.checks).public()
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    except RuntimeError as exc:
        raise HTTPException(status_code=409, detail=str(exc)) from exc


@router.get("/builds")
async def list_builds() -> list[dict]:
    builds.load()
    return [b.public() for b in reversed(list(builds.builds.values()))]


@router.get("/builds/{build_id}")
async def build_status(build_id: str) -> dict:
    build = builds.get(build_id)
    if build is None:
        raise HTTPException(status_code=404, detail="Build not found.")
    return build.public()


class BuildConfirm(BaseModel):
    model_config = ConfigDict(extra="forbid")
    confirm: bool = False


@router.post("/builds/{build_id}/approve")
async def approve_build(build_id: str, body: BuildConfirm, request: Request) -> dict:
    device_or_same_origin(request)
    build = builds.get(build_id)
    if build is None:
        raise HTTPException(404, "Build not found.")
    if not body.confirm:
        raise HTTPException(409, "Approve the implementation plan first.")
    result = await builds.approve(build)
    if "error" in result:
        raise HTTPException(409, result["error"])
    return result


@router.get("/builds/{build_id}/review")
async def review_build(build_id: str) -> dict:
    build = builds.get(build_id)
    if build is None:
        raise HTTPException(404, "Build not found.")
    from .build_review import review
    return await asyncio.to_thread(review, build)


@router.post("/builds/{build_id}/ship")
async def ship_build(build_id: str, body: BuildConfirm, request: Request) -> dict:
    # Publication is a device action. Preserve the hub's ban on cross-origin browser writes.
    if request.headers.get("origin") or not body.confirm:
        raise HTTPException(403, "Confirm publication on the device or through Albie.")
    build = builds.get(build_id)
    if build is None:
        raise HTTPException(404, "Build not found.")
    result = await builds.ship(build)
    if "error" in result:
        raise HTTPException(409, result["error"])
    return result


@router.post("/builds/{build_id}/cancel")
async def cancel_build(build_id: str) -> dict:
    build = builds.get(build_id)
    if build is None:
        raise HTTPException(status_code=404, detail="Build not found.")
    if not await builds.cancel(build):
        raise HTTPException(status_code=409, detail=f"The build is already {build.state}.")
    return build.public()


@router.post("/workflows/{workflow_id}/tasks/{task_id}/cancel")
async def cancel_workflow_task(workflow_id: str, task_id: str) -> dict:
    workflow = orchestrator.workflows.get(workflow_id)
    task = next((t for t in workflow.tasks if t.id == task_id), None) if workflow else None
    if task is None:
        raise HTTPException(status_code=404, detail="Task not found.")
    if not await orchestrator.cancel(task):
        raise HTTPException(status_code=409, detail="Task is already finished or stopping.")
    return task.public()
