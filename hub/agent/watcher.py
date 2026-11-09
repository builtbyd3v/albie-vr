"""Proactive Albie: when something breaks under the user, Albie speaks first. Once, never over anyone, never acting.

Callers hand events over directly (no bus listener, so no contract change): cad/service.py on cad.regen_failed
and every good build, the ML overfit alert and Slack mentions (main.py, ml/router.py).

Per event Albie moves to the panel (pet.state working + target_panel) and says one line (pet.say bubble, and
voice when a Grok session is live). It never edits code: Grok gets the
file, line and error as hidden context with the line, so a "yes" turns into its normal delegate(mode=code) fix.

Rules: one unprompted line per GAP_S; while waiting only the newest line per file (or terminal, CAD) is kept;
the same error is said once.
"""

from __future__ import annotations

import asyncio
import logging
import re
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from typing import Any

from pydantic import BaseModel


from . import events as ev
from .bus import bus
from .events import Event

log = logging.getLogger(__name__)

PET_ID = "albie"
GAP_S = 20.0
ERROR_MAX = 300  # chars of the error Grok gets as context
OFFERS_KEPT = 5  # Fix it buttons still on older cards work for the last few lines


@dataclass(frozen=True)
class Line:
    slot: str  # what it is about (a file, the CAD design, a chat); a new line for the same slot replaces the old one
    key: str | None  # same slot + key as last time = already said; None = always say
    text: str
    path: str | None = None
    line: int | None = None
    note: str | None = None
    panel: str = "screen-main"
    context: str | None = None  # what Grok reads (never says) with the line: what broke and how to fix it on "yes"
    task: str | None = None  # the fix for the card's Fix it button (a code task for the runner)


def fix_task(where: str, error: str) -> str:
    return f"Fix the error at {where}: {error}. Edit the file in place, then say in one short sentence what you changed."


def fix_context(said: str, where: str, error: str) -> str:
    return (f"You just told the user: \"{said}\" It is about {where}: {error}. If they say yes, call delegate with "
            f"mode code and a task to fix exactly that, naming the file, the line and the error.")


SAID_MAX = 100  # a message read out is cut to this many characters


def _said(text: str) -> str:
    """A message as Albie reads it: Slack mention markup out, one line, short."""
    text = " ".join(re.sub(r"<[@#!][^>]*>", "", text or "").split())
    return text if len(text) <= SAID_MAX else text[:SAID_MAX].rsplit(" ", 1)[0] + "..."


def reply_context(said: str, ts: str) -> str:
    return (f"You just told the user: \"{said}\" If they want to answer it, call messages_send with reply_to_ts "
            f"\"{ts}\" and their words: it only writes a draft for them to send.")


def line_for(type_: str, p: dict[str, Any], cad_script: str | None) -> Line | None:
    """What Albie says about one event, or None to stay quiet."""
    if type_ == "ml.overfit_alert":  # one per run (the SDK alerts once; each demo replay alerts once)
        epoch = p.get("epoch")
        since = f" since epoch {epoch}" if epoch is not None else ""
        return Line(f"ml:{p.get('run_id')}", None,
                    f"Heads up, the model is overfitting{since}. Want me to explain what's going on?", panel="ml-results")

    if type_ == "notify.message":  # a Slack mention or DM, or an iMessage from the allowlisted contact
        if p.get("is_me"):
            return None
        ts, text = str(p.get("ts")), _said(p.get("text", ""))
        if p.get("source") == "imessage":
            said = f"New text from {p.get('channel_name') or p.get('user_name') or 'someone'}: {text} Want me to reply?"
            return Line(f"im:{p.get('channel')}", ts, said, panel="messages", context=reply_context(said, ts))
        if not p.get("mentions_me"):
            return None
        who = p.get("user_name") or "Someone"
        where = "messaged you" if p.get("is_dm") else f"mentioned you in {p.get('channel_name') or 'Slack'}"
        said = f"{who} {where}: {text} Want me to reply?"
        return Line(f"slack:{p.get('channel')}", ts, said, panel="messages", context=reply_context(said, ts))
    if type_ == "cad.regen_failed":
        if p.get("source") != "script":
            return None  # pet and slider changes: the caller already sees the error
        n = p.get("error_line")
        text = f"Line {n} broke the rocket." if n else "The rocket script broke."
        text = f"{text} I kept the last good one. Want me to fix it?"
        key = f"{p.get('kept_version')}:{p['error']}"  # a good rebuild moves kept_version, so a rehearsal re-fires
        where = f"line {n} of {cad_script}" if n else str(cad_script)
        error = p["error"][:ERROR_MAX]
        return Line("cad", key, text, cad_script, n, p["error"][:80],
                    context=fix_context(text, where, error), task=fix_task(where, error))

    if type_ == "cad.built":
        # Albie only says it once a broken rocket he spoke about builds again (Watcher.notify checks that)
        return Line("cad", "fixed", "Fixed it. The rocket builds again.") if p.get("source") == "script" else None

    return None


class Watcher:
    def __init__(self, *, broadcast: Callable[[Event], Awaitable[None]] = bus.broadcast,
                 gap_s: float = GAP_S) -> None:
        self.cad_script: str | None = None
        # ponytail: duck-typed, only Grok has say(); agent/ws.py calls add() and drop()
        self._voices: dict[Any, asyncio.Task[None] | None] = {}  # voice -> its queued say()
        self._broadcast, self._gap_s = broadcast, gap_s
        self._said: dict[str, str | None] = {}  # slot -> key last spoken
        self._pending: dict[str, Line] = {}  # slot -> newest unspoken line, oldest slot first
        self._next_ok = 0.0
        self._task: asyncio.Task[None] | None = None
        self._offers: dict[str, str] = {}  # card action id -> fix task, newest last (OFFERS_KEPT)
        self._offer_n = 0

    def add(self, voice: Any) -> None:
        self._voices[voice] = None

    def drop(self, voice: Any) -> None:
        """Socket closed: forget the voice and cancel its queued line, so say() can't reopen a paid session."""
        task = self._voices.pop(voice, None)
        if task is not None:
            task.cancel()

    def notify(self, type_: str, payload: BaseModel | dict[str, Any]) -> None:
        """Hand over an event (from a running event loop). Returns at once; Albie speaks in the background."""
        body = payload.model_dump() if isinstance(payload, BaseModel) else payload
        line = line_for(type_, body, self.cad_script)
        if line is None:
            return
        if type_ == "cad.built" and self._said.get("cad") in (None, "fixed"):
            self._pending.pop("cad", None)  # nothing said about a break (or it was queued only): stay quiet
            return
        if line.key is not None and self._said.get(line.slot) == line.key:
            self._pending.pop(line.slot, None)  # back to what Albie last said: the queued change is stale
            return
        self._pending.pop(line.slot, None)  # re-insert at the end: newest line for this slot wins
        self._pending[line.slot] = line
        if self._task is None or self._task.done():
            self._task = asyncio.get_running_loop().create_task(self._drain())

    def take_offer(self, action_id: str) -> str | None:
        """A tap on a card button: "fix:N" -> its fix task (once), "dismiss:N" or an old card -> None."""
        kind, _, n = action_id.partition(":")
        task = self._offers.pop(n, None)
        return task if kind == "fix" else None

    def _card(self, line: Line) -> ev.Card | None:
        if line.task is None:
            return None
        self._offer_n += 1
        n = str(self._offer_n)
        self._offers[n] = line.task
        while len(self._offers) > OFFERS_KEPT:
            self._offers.pop(next(iter(self._offers)))
        return ev.Card(title=line.note or line.text, actions=[
            ev.CardAction(label="Fix it", action_id=f"fix:{n}"), ev.CardAction(label="Not now", action_id=f"dismiss:{n}")])

    async def idle(self) -> None:
        """Wait until every queued line is out (tests)."""
        while self._task is not None and not self._task.done():
            await self._task

    async def _drain(self) -> None:
        while self._pending:
            wait = self._next_ok - time.monotonic()
            if wait > 0:
                await asyncio.sleep(wait)
                if not self._pending:
                    return
            line = self._pending.pop(next(iter(self._pending)))
            self._said[line.slot] = line.key
            self._next_ok = time.monotonic() + self._gap_s
            await self._speak(line)

    async def _speak(self, line: Line) -> None:
        await self._broadcast(ev.pet_state(PET_ID, "working", target_panel=line.panel))
        await self._broadcast(ev.pet_say(PET_ID, line.text, self._card(line)))
        for voice, queued in list(self._voices.items()):
            say = getattr(voice, "say", None)  # the fake voice has no say()
            if say is None:
                continue
            if queued is not None:
                queued.cancel()  # still waiting for silence: the newer line replaces it
            # say() waits until nobody is talking; the context goes in right before the line
            self._voices[voice] = asyncio.create_task(say(line.text, line.context))


watcher = Watcher()
