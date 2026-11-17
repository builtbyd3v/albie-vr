"""Headset-independent, read-only parallel Claude tasks for the Albie hub.

State lives in memory for this process. The existing crew/Unity roster is not changed.
"""
from __future__ import annotations

import asyncio
import logging
import os
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator

from .bus import Emitter
from .config import ProjectConfig, ProjectNotFound, resolve_project
from .crew import TYPES
from .events import Event
from .turn import TurnControl
from . import tools

log = logging.getLogger(__name__)
Role = Literal["scout", "reviewer", "sentinel"]
MAX_ACTIVE_WORKFLOWS = 4
MAX_HISTORY = 50


class TaskRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    role: Role
    instruction: str = Field(min_length=1, max_length=4000)

    @field_validator("instruction")
    @classmethod
    def not_blank(cls, text: str) -> str:
        text = text.strip()
        if not text:
            raise ValueError("instruction must not be blank")
        return text


class WorkflowRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    project_id: str | None = Field(default=None, max_length=100)
    tasks: list[TaskRequest] = Field(min_length=1, max_length=6)


def now() -> str:
    return datetime.now(timezone.utc).isoformat()


@dataclass
class Task:
    id: str
    role: str
    instruction: str
    state: str = "queued"
    progress: str = ""
    result: str | None = None
    error: str | None = None
    started_at: str | None = None
    finished_at: str | None = None
    control: TurnControl = field(default_factory=TurnControl, repr=False)
    future: asyncio.Task | None = field(default=None, repr=False)

    def public(self) -> dict:
        return {k: getattr(self, k) for k in ("id", "role", "instruction", "state", "progress", "result", "error", "started_at", "finished_at")}


@dataclass
class Workflow:
    id: str
    project_id: str
    backend: str
    tasks: list[Task]
    created_at: str = field(default_factory=now)

    def public(self) -> dict:
        return {"id": self.id, "project_id": self.project_id, "backend": self.backend,
                "created_at": self.created_at, "tasks": [task.public() for task in self.tasks]}


class Orchestrator:
    def __init__(self) -> None:
        self.workflows: dict[str, Workflow] = {}

    def start(self, project: ProjectConfig, request: WorkflowRequest) -> Workflow:
        active = sum(any(t.state in ("queued", "running", "cancelling") for t in w.tasks) for w in self.workflows.values())
        if active >= MAX_ACTIVE_WORKFLOWS:
            raise RuntimeError("Too many active workflows; wait for one to finish.")
        while len(self.workflows) >= MAX_HISTORY:
            finished = next((key for key, w in self.workflows.items()
                             if all(t.state in ("completed", "failed", "cancelled") for t in w.tasks)), None)
            if finished is None:
                raise RuntimeError("Workflow history is full.")
            del self.workflows[finished]
        workflow = Workflow(uuid.uuid4().hex, project.id, "simulated" if os.getenv("FAKE_AGENT", "0") == "1" else "claude",
                            [Task(uuid.uuid4().hex, t.role, t.instruction) for t in request.tasks])
        self.workflows[workflow.id] = workflow
        for task in workflow.tasks:
            task.future = asyncio.create_task(self._run(project, task))
        return workflow

    async def _run(self, project: ProjectConfig, task: Task) -> None:
        if task.control.cancelled.is_set():
            task.state, task.finished_at = "cancelled", now()
            return
        task.state, task.started_at = "running", now()
        scrubber = Emitter(task.id, redact=[str(project.path)], companion=False)

        async def receive(event: Event) -> None:
            event = scrubber._scrub(event)
            payload = event.payload
            if event.type == "agent.error":
                task.error = str(payload.get("message") or "Agent failed.")[:4000]
            elif event.type in ("agent.task_progress", "agent.partial_answer"):
                task.progress = str(payload.get("text") or "")[:4000]

        try:
            from .runner import get_runner
            result = await get_runner()(project, task.instruction, "qa", receive, pet_id=task.id,
                                        control=task.control, agent=TYPES[task.role])
            if task.control.cancelled.is_set():
                task.state = "cancelled"
            elif task.error:
                task.state = "failed"
            elif not result:
                task.state, task.error = "failed", "Agent returned no result."
            else:
                clean = scrubber._scrub(Event(type="agent.answer_done", payload={"text": result}))
                task.result, task.state = str(clean.payload["text"])[:12000], "completed"
        except asyncio.CancelledError:
            task.state = "cancelled"
        except Exception:
            log.exception("orchestration task %s failed", task.id)
            if task.control.cancelled.is_set():
                task.state = "cancelled"
            else:
                task.state, task.error = "failed", "Agent failed; check the hub logs."
        finally:
            task.finished_at = now()

    async def cancel(self, task: Task) -> bool:
        if task.state not in ("queued", "running"):
            return False
        task.state = "cancelling"
        try:
            await asyncio.wait_for(task.control.cancel(), timeout=3)
        except (Exception, asyncio.TimeoutError):
            log.exception("could not interrupt orchestration task %s", task.id)
        if task.future is not None:
            try:
                await asyncio.wait_for(asyncio.shield(task.future), timeout=3)
            except asyncio.TimeoutError:
                task.future.cancel()
                await asyncio.gather(task.future, return_exceptions=True)
        if task.state == "cancelling":
            task.state, task.finished_at = "cancelled", now()
        return True


orchestrator = Orchestrator()

TOOLS = [
    {"name": "workflow_start", "description": "Start independent read-only Claude research or review tasks in parallel without spawning XR characters. Each task has a role and a self-contained instruction. Returns a workflow id; use workflow_status to retrieve results.",
     "parameters": {"type": "object", "properties": {
         "project_id": {"type": "string", "description": "Configured project ID; omit for the hub default."},
         "tasks": {"type": "array", "minItems": 1, "maxItems": 6, "items": {"type": "object", "properties": {
             "role": {"type": "string", "enum": ["scout", "reviewer", "sentinel"]},
             "instruction": {"type": "string", "description": "A self-contained read-only task."}},
             "required": ["role", "instruction"]}}}, "required": ["tasks"]}},
    {"name": "workflow_status", "description": "Get the individual states, progress, and results of a previously started workflow.",
     "parameters": {"type": "object", "properties": {"workflow_id": {"type": "string"}}, "required": ["workflow_id"]}},
    {"name": "workflow_cancel", "description": "Stop one running task in a workflow without stopping the others.",
     "parameters": {"type": "object", "properties": {"workflow_id": {"type": "string"}, "task_id": {"type": "string"}}, "required": ["workflow_id", "task_id"]}},
]


async def call_tool(name: str, args: dict) -> dict:
    if name == "workflow_start":
        try:
            request = WorkflowRequest.model_validate(args)
            return orchestrator.start(resolve_project(request.project_id), request).public()
        except (ValueError, ProjectNotFound, RuntimeError) as exc:
            return {"error": str(exc)}
    workflow = orchestrator.workflows.get(str(args.get("workflow_id", "")))
    if workflow is None:
        return {"error": "Workflow not found (history is in memory)."}
    if name == "workflow_status":
        return workflow.public()
    if name == "workflow_cancel":
        task = next((t for t in workflow.tasks if t.id == args.get("task_id")), None)
        if task is None:
            return {"error": "Task not found."}
        if not await orchestrator.cancel(task):
            return {"error": "Task is already finished or stopping."}
        return task.public()
    return {"error": "Unknown workflow tool."}


tools.register(TOOLS, call_tool, lead_only=True)
