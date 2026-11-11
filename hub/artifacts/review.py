"""Advisory Jev review of a model-written artifact (plan step C1, audit 3 part B).

Unity runs every geometric check itself (validator, ArtifactNodes.Missing, plausibility) and writes a short facts
text ("board: box 100x3x20 cm, rests on fulcrum; slider tilt turns board about Z, ends move 26 cm"). Jev can't see
3D: it is a typed classifier over text, so it only judges meaning from that text plus a coordinate-free summary of
the manifest and scene: does it match the request, is every part held up, does each control visibly change
something, do the labels fit the effects, is the arrangement sensible, does the space suit it.

Never blocking and never a 5xx: no TYPESAFE_API_KEY -> status "skipped"; a Jev error, a bad answer or the time
budget running out -> status "error". Verdicts are advisory (phase 1): Unity puts them in result.review only.
The key goes only to TypeSafe, through ml.setup.jev.ask.
"""
from __future__ import annotations

import asyncio
import logging
import os
import time
from typing import Any, Literal

from pydantic import BaseModel, Field

from ml.setup import jev
from ml.setup.llm import LLMError

log = logging.getLogger("artifacts.review")

QuestionName = Literal["matches_request", "parts_supported", "controls_effective", "controls_match_labels",
                       "arrangement_sensible", "space_fits"]

# Unity waits 2.5 s on its own timer (plan C2), so the hub answers before that.
TIMEOUT_S = float(os.environ.get("FORGE_ARTIFACT_REVIEW_TIMEOUT", "2.2"))
MAX_LINES = 120  # per section of the summary: a huge scene can't blow up Jev's state
MAX_TEXT = 160  # per string copied from the scene

# name -> (instructions, criteria, the options that mean "no problem found").
# "unsure" counts as ok: ok is false only when Jev names a specific problem, so a later gate (phase 2) can't
# refuse on doubt alone.
QUESTIONS: dict[str, tuple[str, dict[str, str], set[str]]] = {
    "matches_request": (
        "The person asked for the thing in REQUEST. Does the artifact described in `state` (its parts, controls "
        "and the measured facts) build that thing?",
        {"yes": "Yes: it has the main parts of what was asked for.",
         "missing_main_part": "It is the right kind of thing but a main part is missing (e.g. a windmill with "
                              "no blades, a traffic light with two lamps).",
         "different_thing": "It builds something else than what was asked for.",
         "unsure": "The text doesn't say enough to tell."},
        {"yes", "unsure"}),
    "parts_supported": (
        "According to FACTS, does every solid part rest on, hang from or attach to another part or the base, "
        "except parts that are meant to float (orbits, diagrams, labels)?",
        {"supported": "Every solid part is held up by something, or is meant to float.",
         "floating_or_unattached": "Some part that should be held up floats free or is detached.",
         "unsure": "FACTS doesn't say enough to tell."},
        {"supported", "unsure"}),
    "controls_effective": (
        "According to FACTS and RULES, does each slider, button or toggle in CONTROLS visibly change something in "
        "the artifact (moves, turns, shows, hides or recolours a part, or starts or stops a motion)?",
        {"all_visible": "Every control visibly changes something.",
         "some_do_nothing": "At least one control changes nothing that can be seen.",
         "unsure": "The text doesn't say enough to tell."},
        {"all_visible", "unsure"}),
    "controls_match_labels": (
        "Does each control's label match what FACTS and RULES say it does (a 'Speed' slider changes how fast "
        "something moves, a 'Start' button starts a motion)?",
        {"matches": "Every label describes the control's effect.",
         "wrong_effect": "Some control does something other than its label says.",
         "unsure": "The text doesn't say enough to tell."},
        {"matches", "unsure"}),
    "arrangement_sensible": (
        "For the thing in REQUEST (or TITLE), are the parts in a sensible order, orientation and proportion "
        "according to FACTS?",
        {"sensible": "Order, orientation and proportions look like the real thing.",
         "misordered_or_upside_down": "Parts are in the wrong order or upside down (e.g. a traffic light with "
                                      "green on top).",
         "wrong_proportions": "Parts are far out of proportion (e.g. a blade longer than its tower).",
         "unsure": "The text doesn't say enough to tell."},
        {"sensible", "unsure"}),
    "space_fits": (
        "Where does this artifact belong?",
        {"volume": "A small object or model that floats in its own box in front of the person.",
         "panel": "Something flat to read or tap, like a board, chart or display.",
         "room": "Something placed in the real room at full size, like a course on the floor."},
        set()),  # ok when the choice equals scene.space
}


class ReviewIn(BaseModel):
    request: str = Field("", max_length=500)  # the person's words, if Unity has them
    manifest: dict[str, Any]
    scene: dict[str, Any]
    facts: str = Field("", max_length=6000)  # Unity's deterministic facts text (plan B4)
    questions: list[QuestionName] | None = None  # None: every question that applies


class Verdict(BaseModel):
    question: str
    choice: str
    confidence: float
    probabilities: dict[str, float] = {}
    ok: bool


class ReviewOut(BaseModel):
    status: Literal["ok", "skipped", "error"]
    reason: str | None = None
    model: str | None = None
    verdicts: list[Verdict] = []
    elapsed_ms: int = 0


def _text(value: Any) -> str:
    s = " ".join(str(value).split())
    return s if len(s) <= MAX_TEXT else s[: MAX_TEXT - 3] + "..."


def _capped(lines: list[str]) -> list[str]:
    if len(lines) <= MAX_LINES:
        return lines
    return lines[:MAX_LINES] + [f"- ... and {len(lines) - MAX_LINES} more"]


def _bindings(node: dict[str, Any]) -> str:
    bound = [f"{k} bound to {v}" for k, v in node.items() if isinstance(v, str) and v.startswith("$")]
    return f", {', '.join(bound)}" if bound else ""


def _part_line(node: dict[str, Any]) -> str:
    """One node, with no coordinates: id, kind, shape, parent, bindings. Geometry is in FACTS."""
    kind = str(node.get("kind", "?"))
    bits = [kind]
    if node.get("shape"):
        bits.append(str(node["shape"]))
    if kind == "sim":
        bodies = [str(b.get("node")) for b in node.get("bodies", []) if isinstance(b, dict)]
        bits.append(f"solver {node.get('solver', '?')}" + (f", moves {', '.join(bodies)}" if bodies else ""))
    if kind == "line" and isinstance(node.get("through"), list):
        bits.append("through " + ", ".join(map(str, node["through"])))
    if node.get("text"):
        bits.append(f'text "{_text(node["text"])}"')
    if node.get("parent"):
        bits.append(f"child of {node['parent']}")
    if node.get("visible") is False:
        bits.append("hidden at start")
    return f"- {_text(node.get('id', '?'))}: {_text(', '.join(bits))}{_bindings(node)}"


def _input_labels(manifest: dict[str, Any]) -> dict[str, str]:
    return {str(i.get("name")): str(i.get("label") or i.get("name"))
            for i in manifest.get("inputs") or [] if isinstance(i, dict)}


def _controls(manifest: dict[str, Any], nodes: list[dict[str, Any]]) -> list[str]:
    labels, lines = _input_labels(manifest), []
    for node in nodes:
        for w in node.get("widgets") or []:
            if not isinstance(w, dict):
                continue
            kind = str(w.get("type", "?"))
            if kind in ("slider", "toggle", "choice"):
                name = str(w.get("input", "?"))
                lines.append(f'- {kind} "{_text(w.get("label") or labels.get(name, name))}" sets input {name}')
            elif kind == "button":
                lines.append(f'- button "{_text(w.get("label") or w.get("id", "?"))}" sends input {w.get("id", "?")}')
            elif kind == "stat":
                lines.append(f'- readout "{_text(w.get("label") or w.get("metric", "?"))}" shows {w.get("metric", "?")}')
            elif kind == "note":
                lines.append(f'- note "{_text(w.get("text", ""))}"')
    return lines


def _action(a: Any) -> str:
    if not isinstance(a, dict):
        return _text(a)
    if "set" in a:
        return f"set {a['set']} = {_text(a.get('value'))}"
    for key in ("sim", "timeline"):
        if key in a:
            return f"{key} {a[key]} {a.get('action', '?')}"
    return _text(a)


def _rules(scene: dict[str, Any]) -> list[str]:
    lines = []
    for r in scene.get("rules") or []:
        if not isinstance(r, dict):
            continue
        kind = str(r.get("type", "?"))
        if kind == "on_input":
            acts = "; ".join(_action(a) for a in r.get("do") or [])
            lines.append(f"- when input {r.get('input', '?')}: {_text(acts)}")
        elif kind == "animate":
            lines.append(f"- timeline {r.get('timeline', '?')} animates {r.get('target', '?')}.{r.get('property', '?')}")
        elif kind == "metric":
            lines.append(f"- metric {r.get('name', '?')} ({r.get('op', '?')})")
        else:
            lines.append(f"- {kind}")
    return lines


def _nodes(scene: dict[str, Any]) -> list[dict[str, Any]]:
    return [n for n in scene.get("nodes") or [] if isinstance(n, dict)]


def has_controls(body: ReviewIn) -> bool:
    nodes = _nodes(body.scene)
    return any(isinstance(w, dict) and w.get("type") in ("slider", "toggle", "choice", "button")
               for n in nodes for w in n.get("widgets") or [])


def build_state(body: ReviewIn) -> str:
    """Request + title + a coordinate-free summary of the scene + Unity's facts. Never raw positions."""
    m, s = body.manifest, body.scene
    nodes = _nodes(s)
    parts = [_part_line(n) for n in nodes if n.get("kind") != "panel"]
    inputs = [f"- {_text(i.get('name'))}: {i.get('type', '?')}"
              + (f" {i.get('min')}..{i.get('max')}" if "min" in i or "max" in i else "")
              + (f', label "{_text(i["label"])}"' if i.get("label") else "")
              for i in m.get("inputs") or [] if isinstance(i, dict)]
    out = [f"REQUEST: {body.request.strip() or '(not given)'}",
           f"TITLE: {_text(m.get('title') or m.get('id') or '?')}",
           f"SPACE: {s.get('space', '(not given)')}",
           "PARTS:", *(_capped(parts) or ["- (none)"]),
           "INPUTS:", *(_capped(inputs) or ["- (none)"]),
           "CONTROLS:", *(_capped(_controls(m, nodes)) or ["- (none)"]),
           "RULES:", *(_capped(_rules(s)) or ["- (none)"]),
           "FACTS (measured by the headset, exact):", body.facts.strip() or "(not given)"]
    return "\n".join(out)


def pick_questions(body: ReviewIn) -> list[str]:
    """The asked-for questions (default all) minus those the input can't support."""
    names = list(dict.fromkeys(body.questions)) if body.questions is not None else list(QUESTIONS)
    skip = set()
    if not body.request.strip():
        skip.add("matches_request")
    if not body.facts.strip():
        skip.add("parts_supported")  # only the measured facts say what rests on what
    if not has_controls(body):
        skip |= {"controls_effective", "controls_match_labels"}
    if body.scene.get("space") not in QUESTIONS["space_fits"][1]:
        skip.add("space_fits")
    return [n for n in names if n not in skip]


def jev_questions(names: list[str]) -> dict[str, Any]:
    return {n: {"type": "choice", "instructions": QUESTIONS[n][0], "criteria": dict(QUESTIONS[n][1])}
            for n in names}


def _verdicts(answers: dict[str, Any], names: list[str], space: Any) -> list[Verdict]:
    out = []
    for name in names:
        a = answers.get(name)
        if not isinstance(a, dict) or a.get("choice") not in QUESTIONS[name][1]:
            continue
        choice = a["choice"]
        ok = choice == space if name == "space_fits" else choice in QUESTIONS[name][2]
        probs = a.get("probabilities") or {}
        out.append(Verdict(question=name, choice=choice, confidence=float(a.get("confidence") or 0.0),
                           probabilities={str(k): float(v) for k, v in probs.items()
                                          if isinstance(v, (int, float))} if isinstance(probs, dict) else {},
                           ok=ok))
    return out


async def review(body: ReviewIn, timeout: float | None = None) -> ReviewOut:
    started = time.monotonic()

    def done(**kw: Any) -> ReviewOut:
        out = ReviewOut(elapsed_ms=int((time.monotonic() - started) * 1000), **kw)
        log.info("artifact review %s: %s %s", body.manifest.get("id", "?"), out.status,
                 out.reason or ", ".join(f"{v.question}={v.choice}({v.confidence:.2f})" for v in out.verdicts))
        return out

    if not jev.available():
        return done(status="skipped", reason="no_key")
    names = pick_questions(body)
    if not names:
        return done(status="skipped", reason="no_questions")
    budget = TIMEOUT_S if timeout is None else timeout
    try:
        res = await asyncio.wait_for(asyncio.to_thread(jev.ask, build_state(body), jev_questions(names), budget),
                                     budget)
    except (asyncio.TimeoutError, TimeoutError):
        return done(status="error", reason="timeout")
    except LLMError as exc:
        return done(status="error", reason=_text(exc))
    except Exception as exc:  # never a 5xx: the review is advisory
        log.exception("artifact review failed")
        return done(status="error", reason=f"review failed ({type(exc).__name__})")
    answers = res.get("answers") if isinstance(res, dict) else None
    verdicts = _verdicts(answers, names, body.scene.get("space")) if isinstance(answers, dict) else []
    if not verdicts:
        return done(status="error", reason="bad_response", model=res.get("model") if isinstance(res, dict) else None)
    return done(status="ok", model=str(res.get("model") or jev.JEV_MODEL), verdicts=verdicts)
