from __future__ import annotations

import asyncio
import time
from typing import Any

from agent.events import Event
from agent.watcher import Watcher, line_for

SCRIPT = "/repo/hub/cad/parts/rocket.py"


def test_regen_failed_from_script_points_at_the_line() -> None:
    line = line_for("cad.regen_failed", {"source": "script", "kind": "build", "error": "fin_count must be > 0",
                                         "error_line": 42}, SCRIPT)
    assert line is not None
    assert line.text == "Line 42 broke the rocket. I kept the last good one. Want me to fix it?"
    assert (line.path, line.line, line.note) == (SCRIPT, 42, "fin_count must be > 0")
    # Grok gets what broke (never spoken), so a "yes" becomes delegate(mode=code) on that line
    assert line.context is not None
    assert f"line 42 of {SCRIPT}: fin_count must be > 0" in line.context and "mode code" in line.context


def test_regen_failed_from_pet_or_ui_stays_quiet() -> None:
    # The pet sees its own tool error; only edits to the script surprise the user.
    assert line_for("cad.regen_failed", {"source": "pet", "kind": "params", "error": "x"}, SCRIPT) is None


class FakeVoice:
    def __init__(self) -> None:
        self.said: list[str] = []
        self.contexts: list[str | None] = []

    async def say(self, text: str, context: str | None = None) -> None:
        self.said.append(text)
        self.contexts.append(context)


def _watcher(gap_s: float) -> tuple[Watcher, list[Event]]:
    sent: list[Event] = []

    async def broadcast(event: Event) -> None:
        sent.append(event)

    w = Watcher(broadcast=broadcast, gap_s=gap_s)
    w.cad_script = SCRIPT
    return w, sent


def _says(sent: list[Event]) -> list[str]:
    return [e.payload["text"] for e in sent if e.type == "pet.say"]


REGEN = {"source": "script", "kind": "build", "error": "boom", "error_line": 2, "kept_version": 1}
BROKE = "Line 2 broke the rocket. I kept the last good one. Want me to fix it?"


def _mention(ts: str, text: str, channel: str = "C2") -> dict[str, Any]:
    return {"channel": channel, "channel_name": "forge-team", "user_name": "Hudson", "ts": ts, "is_dm": False,
            "mentions_me": True, "is_me": False, "text": text, "source": None}


def test_albie_moves_and_speaks() -> None:
    async def run() -> None:
        w, sent = _watcher(gap_s=0)
        voice = FakeVoice()
        w.add(voice)
        w.notify("cad.regen_failed", REGEN)
        await w.idle()
        assert [e.type for e in sent] == ["pet.state", "pet.say"]
        assert sent[0].payload == {"pet_id": "bit", "mode": "working", "target_panel": "screen-main"}
        assert voice.said == [BROKE]
        assert voice.contexts[0] is not None and f"line 2 of {SCRIPT}: boom" in voice.contexts[0]

    asyncio.run(run())


def test_same_error_twice_is_said_once() -> None:
    async def run() -> None:
        w, sent = _watcher(gap_s=0)
        w.notify("cad.regen_failed", REGEN)
        await w.idle()
        w.notify("cad.regen_failed", REGEN)
        await w.idle()
        assert len(_says(sent)) == 1

    asyncio.run(run())


def test_budget_keeps_only_the_newest_queued_line() -> None:
    async def run() -> None:
        w, sent = _watcher(gap_s=0.2)
        w.notify("cad.regen_failed", REGEN)
        await asyncio.sleep(0.01)  # first line goes out now and starts the gap
        w.notify("notify.message", _mention("1", "first"))
        w.notify("notify.message", _mention("2", "second"))  # same chat before the gap ended: replaces it
        await asyncio.sleep(0.05)
        assert _says(sent) == [BROKE]
        await w.idle()
        assert _says(sent) == [BROKE, "Hudson mentioned you in forge-team: second Want me to reply?"]

    asyncio.run(run())


def test_regen_failed_without_a_line_number() -> None:
    line = line_for("cad.regen_failed", {"source": "script", "kind": "build", "error": "x", "kept_version": 1}, SCRIPT)
    assert line is not None and line.text == "The rocket script broke. I kept the last good one. Want me to fix it?"
    assert line.line is None


def test_queued_lines_keep_their_order_and_dropped_lines_are_not_marked_said() -> None:
    async def run() -> None:
        w, sent = _watcher(gap_s=0.1)
        w.notify("cad.regen_failed", REGEN)
        await asyncio.sleep(0.01)
        w.notify("ml.overfit_alert", {"run_id": "r1", "epoch": 6})
        w.notify("notify.message", _mention("1", "first"))
        w.notify("notify.message", _mention("2", "second"))  # replaces the queued "first"
        await w.idle()
        assert _says(sent)[1:] == ["Heads up, the model is overfitting since epoch 6. Want me to explain what's going on?",
                                   "Hudson mentioned you in forge-team: second Want me to reply?"]
        w.notify("notify.message", _mention("1", "first"))  # never said before, so it is said now
        await w.idle()
        assert _says(sent)[-1] == "Hudson mentioned you in forge-team: first Want me to reply?"

    asyncio.run(run())


class SlowVoice(FakeVoice):
    async def say(self, text: str, context: str | None = None) -> None:
        await asyncio.sleep(10)  # Grok still talking
        self.said.append(text)


def test_closed_socket_cancels_queued_speech() -> None:
    async def run() -> None:
        w, _ = _watcher(gap_s=0)
        voice = SlowVoice()
        w.add(voice)
        w.notify("cad.regen_failed", REGEN)
        await w.idle()
        w.drop(voice)
        await asyncio.sleep(0.01)
        assert voice.said == [] and asyncio.all_tasks() == {asyncio.current_task()}

    asyncio.run(run())


def test_fixed_it_only_after_a_spoken_break() -> None:
    async def run() -> None:
        w, sent = _watcher(gap_s=0)
        built = {"source": "script", "version": 3}
        w.notify("cad.built", built)  # a normal save: nothing broke, nothing to say
        await w.idle()
        assert _says(sent) == []
        w.notify("cad.regen_failed", {"source": "script", "kind": "build", "error": "bad", "error_line": 9,
                                      "kept_version": 3})
        await w.idle()
        w.notify("cad.built", {"source": "pet", "version": 4})  # a slider change is not the fix
        w.notify("cad.built", {**built, "version": 5})
        await w.idle()
        w.notify("cad.built", {**built, "version": 6})  # said once
        await w.idle()
        assert _says(sent)[1:] == ["Fixed it. The rocket builds again."]

    asyncio.run(run())


def test_fix_offers_carry_a_card_and_a_tap_runs_the_fix_once() -> None:
    async def run() -> None:
        w, sent = _watcher(gap_s=0)
        w.notify("cad.regen_failed", {"source": "script", "kind": "build", "error": "bad fin", "error_line": 9,
                                      "kept_version": 1})
        await w.idle()
        card = next(e for e in sent if e.type == "pet.say").payload["card"]
        assert card["title"] == "bad fin"
        fix, later = (a["action_id"] for a in card["actions"])
        assert [a["label"] for a in card["actions"]] == ["Fix it", "Not now"]
        task = w.take_offer(fix)
        assert task is not None and f"line 9 of {SCRIPT}: bad fin" in task
        assert w.take_offer(fix) is None and w.take_offer(later) is None  # once
        w.notify("notify.message", _mention("3", "hi"))
        await w.idle()
        assert next(e for e in reversed(sent) if e.type == "pet.say").payload.get("card") is None  # nothing to fix

    asyncio.run(run())


def test_not_now_forgets_the_offer() -> None:
    async def run() -> None:
        w, sent = _watcher(gap_s=0)
        w.notify("cad.regen_failed", REGEN)
        await w.idle()
        fix, later = (a["action_id"] for a in next(e for e in sent if e.type == "pet.say").payload["card"]["actions"])
        assert w.take_offer(later) is None and w.take_offer(fix) is None

    asyncio.run(run())


def test_card_tap_over_ws_runs_the_fix_as_a_code_ask(client: Any, monkeypatch: Any) -> None:
    from agent import ws as agent_ws
    from agent.watcher import Line, watcher

    asked: list[tuple[str, str]] = []

    def runner():
        async def run(project: Any, text: str, mode: str, emit: Any, **_: Any) -> str:
            asked.append((text, mode))
            return "Fixed it."
        return run

    monkeypatch.setattr(agent_ws, "get_runner", runner)
    card = watcher._card(Line("cad", "k", "Line 3 broke the rocket.", "rocket.py", 3, "boom", task="Fix line 3: boom"))
    assert card is not None and card.actions is not None
    with client.websocket_connect("/ws") as ws:
        ws.send_json({"type": "hello", "payload": {"client": "test", "pet_id": "bit", "project_id": "demo"}})
        ws.send_json({"type": "pet.card_action", "payload": {"action_id": card.actions[0].action_id}})
        ws.send_json({"type": "pet.card_action", "payload": {"action_id": card.actions[0].action_id}})  # twice: once
        for _ in range(50):
            if asked:
                break
            time.sleep(0.02)
    assert asked == [("Fix line 3: boom", "code")]


def test_overfit_alert_points_albie_at_the_results_panel() -> None:
    line = line_for("ml.overfit_alert", {"run_id": "r1", "epoch": 6, "reason": "val loss up"}, None)
    assert line is not None and line.panel == "ml-results" and line.key is None  # every run / replay alerts once
    assert line.text == "Heads up, the model is overfitting since epoch 6. Want me to explain what's going on?"


MENTION = {"channel": "C1", "channel_name": "forge-team", "user_name": "Hudson", "ts": "1.2", "is_dm": False,
           "mentions_me": True, "is_me": False, "text": "hey <@UME> is the demo build ready?", "source": None}


def test_a_slack_mention_or_dm_is_read_out_and_others_stay_quiet() -> None:
    line = line_for("notify.message", MENTION, None)
    assert line is not None and line.panel == "messages"
    assert line.text == "Hudson mentioned you in forge-team: hey is the demo build ready? Want me to reply?"
    assert line.context is not None and 'reply_to_ts "1.2"' in line.context  # "Albie, yes, ..." drafts the reply
    dm = line_for("notify.message", {**MENTION, "is_dm": True, "text": "call me"}, None)
    assert dm is not None and dm.text == "Hudson messaged you: call me Want me to reply?"
    assert line_for("notify.message", {**MENTION, "mentions_me": False}, None) is None
    assert line_for("notify.message", {**MENTION, "is_me": True}, None) is None


def test_an_allowlisted_imessage_is_read_out_short() -> None:
    text = {**MENTION, "source": "imessage", "mentions_me": False, "channel": "im:x", "channel_name": "Mom",
            "text": "word " * 40}
    line = line_for("notify.message", text, None)
    assert line is not None and line.text.startswith("New text from Mom: word word") and "..." in line.text
    assert line_for("notify.message", {**text, "is_me": True}, None) is None


def test_a_cad_line_names_the_design_that_is_open() -> None:
    broke = line_for("cad.regen_failed", {"source": "script", "kind": "build", "error": "bad", "error_line": 7,
                                          "kept_version": 2}, "/repo/hub/cad/parts/quadcopter.py")
    assert broke is not None and broke.text.startswith("Line 7 broke the quadcopter.")
    fixed = line_for("cad.built", {"source": "script", "version": 3, "part": "gearbox"}, None)
    assert fixed is not None and fixed.text == "Fixed it. The gearbox builds again."
    unknown = line_for("cad.regen_failed", {"source": "script", "kind": "build", "error": "bad"}, None)
    assert unknown is not None and unknown.text.startswith("The design script broke.")
