"""REST for the desk extras (proposed contract: README "Desk"): /timers, /notes, /ui/widgets.

Everything that changes something is a POST, like the rest of the hub (ForgeNet sends GET and POST).

Unity's buttons call these; Albie's tools go through the same DeskService, so both raise the same events.
"""
from __future__ import annotations

from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel, Field

from events import Note, NotesUpdated, TimersUpdated, TimerState, UiWidgets, WidgetState

from .service import DeskService

router = APIRouter(tags=["desk"])


def desk(request: Request) -> DeskService:
    return request.app.state.desk


class TimerStart(BaseModel):
    minutes: float | None = Field(None, gt=0)
    seconds: float | None = Field(None, gt=0)  # instead of minutes (mocks, quick tests)
    label: str = ""


class TimerAdd(BaseModel):
    minutes: float


class NoteIn(BaseModel):
    text: str


class NotePatch(BaseModel):
    text: str | None = None
    done: bool | None = None


class WidgetsIn(BaseModel):
    timer: WidgetState | None = None
    notes: WidgetState | None = None


async def _run(coro):
    try:
        return await coro
    except LookupError as e:
        raise HTTPException(404, str(e)) from None
    except ValueError as e:  # can't do that now: pause a timer that isn't running, an empty note, ...
        raise HTTPException(422, str(e)) from None


# ---------------------------------------------------------------------- timers
@router.get("/timers", response_model=TimersUpdated)
def timers(d: DeskService = Depends(desk)) -> TimersUpdated:
    return d.timers.updated()


@router.post("/timers", response_model=TimerState)
async def timer_start(req: TimerStart, d: DeskService = Depends(desk)) -> TimerState:
    """A new timer (up to 5); `label` names it ("tea"), default Focus."""
    if req.seconds is None and req.minutes is None:
        raise HTTPException(422, "give minutes or seconds")
    seconds = req.seconds if req.seconds is not None else req.minutes * 60
    return await _run(d.timers.start(seconds, req.label))


@router.post("/timers/stop_all", response_model=TimersUpdated)
async def timer_stop_all(d: DeskService = Depends(desk)) -> TimersUpdated:
    await _run(d.timers.stop_all())
    return d.timers.updated()


@router.post("/timers/{timer_id}/pause", response_model=TimerState)
async def timer_pause(timer_id: str, d: DeskService = Depends(desk)) -> TimerState:
    return await _run(d.timers.pause(timer_id))


@router.post("/timers/{timer_id}/resume", response_model=TimerState)
async def timer_resume(timer_id: str, d: DeskService = Depends(desk)) -> TimerState:
    return await _run(d.timers.resume(timer_id))


@router.post("/timers/{timer_id}/add", response_model=TimerState)
async def timer_add(timer_id: str, req: TimerAdd, d: DeskService = Depends(desk)) -> TimerState:
    return await _run(d.timers.add(timer_id, req.minutes * 60))


@router.post("/timers/{timer_id}/stop", response_model=TimerState)
async def timer_stop(timer_id: str, d: DeskService = Depends(desk)) -> TimerState:
    """Cancel it, or turn off its ring (the pill's Stop). Returns it as it was."""
    return await _run(d.timers.stop(timer_id))


# ---------------------------------------------------------------------- notes
@router.get("/notes", response_model=NotesUpdated)
def notes(d: DeskService = Depends(desk)) -> NotesUpdated:
    return d.notes.updated()


@router.post("/notes", response_model=Note)
async def notes_add(req: NoteIn, d: DeskService = Depends(desk)) -> Note:
    return await _run(d.add_note(req.text))


@router.post("/notes/undo", response_model=NotesUpdated)
async def notes_undo(d: DeskService = Depends(desk)) -> NotesUpdated:
    await _run(d.undo_note())
    return d.notes.updated()


@router.post("/notes/{note_id}/edit", response_model=Note)
async def notes_edit(note_id: str, req: NotePatch, d: DeskService = Depends(desk)) -> Note:
    return await _run(d.edit_note(note_id, req.text, req.done))


@router.post("/notes/{note_id}/remove", response_model=NotesUpdated)
async def notes_remove(note_id: str, d: DeskService = Depends(desk)) -> NotesUpdated:
    """Removes it, kept for Undo."""
    await _run(d.remove_note(note_id))
    return d.notes.updated()


# ---------------------------------------------------------------------- widgets
@router.get("/ui/widgets", response_model=UiWidgets)
def widgets(d: DeskService = Depends(desk)) -> UiWidgets:
    return d.widgets


@router.post("/ui/widgets", response_model=UiWidgets)
async def widgets_set(req: WidgetsIn, d: DeskService = Depends(desk)) -> UiWidgets:
    """Minimize, expand, close. Closing the timers stops them."""
    return await _run(d.set_widgets(req.timer, req.notes))
