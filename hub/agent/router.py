"""REST endpoints for the agent module (mounted at /agent). Asking Albie anything goes over /ws (agent.ask)."""

from __future__ import annotations

from fastapi import APIRouter, HTTPException
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


# Builds (agent/build.py): start, watch and stop. Shipping (push + pull request) is not here on purpose: it only happens
# through build_ship with confirm, or the Open pull request button on Albie's card, after the user says yes.
@router.post("/builds", status_code=202)
async def start_build(request: BuildRequest) -> dict:
    try:
        return builds.start(request.goal).public()
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    except RuntimeError as exc:
        raise HTTPException(status_code=409, detail=str(exc)) from exc


@router.get("/builds")
async def list_builds() -> list[dict]:
    return [b.public() for b in reversed(list(builds.builds.values()))]


@router.get("/builds/{build_id}")
async def build_status(build_id: str) -> dict:
    build = builds.builds.get(build_id)
    if build is None:
        raise HTTPException(status_code=404, detail="Build not found (builds are kept in memory).")
    return build.public()


@router.post("/builds/{build_id}/cancel")
async def cancel_build(build_id: str) -> dict:
    build = builds.builds.get(build_id)
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
