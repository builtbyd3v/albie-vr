"""Laptop remote for the scripted demo (docs/demo/TONIGHT_PLAN.md).

GET  /demo               one small dark page: Cue, Back / Skip / Reset, the cue sheet and a beat list (B0..B7), phone friendly
POST /demo/step          publish demo.step: Hudson's next move (start, palm, the end of each of his lines, a poke, the end)
POST /demo/next|back|reset   publish demo.next / demo.back / demo.reset  (payload {}); next skips the running beat
POST /demo/goto {beat}   publish demo.goto (payload {beat: "B3"})

The events go out over the hub's EventBus to every connected headset (ForgeNet.On("demo.next") etc.).
Demo mode is off by default on the Unity side, so nothing here does anything for a normal user.
"""
from __future__ import annotations

import re
from typing import Literal

from fastapi import APIRouter, HTTPException, Request
from fastapi.responses import HTMLResponse
from pydantic import BaseModel, Field

from events import EventBus

router = APIRouter(tags=["demo"])

BEATS: list[tuple[str, str]] = [
    ("B0", "Idle"),
    ("B1", "Summon Albie"),
    ("B2", "Open the quad"),
    ("B3", "Camera search"),
    ("B4", "Verdict"),
    ("B5", "Brain: train and push"),
    ("B6", "Flight test"),
    ("B7", "Done"),
]
# The operator's cue sheet: when to press Cue, and what the headset does. One press each, in order.
CUES: list[tuple[str, str]] = [
    ("Hudson is ready, headset on", "The take starts (5 quiet seconds)."),
    ("His palm is out and still", "Only if Albie hasn't moved after a second: he runs over and lands on the palm."),
    ("He finishes line 1 (\"...camera to it.\")", "The quad builds and takes off."),
    ("He finishes line 2 (\"...while I bike.\")", "Research runs by itself, then the verdict card."),
    ("His finger reaches Add to parts list", "Only if his poke didn't take: the button presses, the camera mounts. Training then runs by itself."),
    ("His finger reaches Confirm", "Only if his poke didn't take: the push runs."),
    ("He finishes line 5 (\"...around the room.\")", "The flight test: the drone follows him."),
    ("His palm is out again", "Only if the drone hasn't turned for home: it comes back over his hand."),
    ("Albie's last line is done", "End of the take. Cue once more resets for the next one."),
]
_BEAT_RE = re.compile(r"^B\d{1,2}[a-z]?$")


class GotoIn(BaseModel):
    beat: str = Field(..., description="B0..B7, optionally with a letter suffix (B5b)")


class Sent(BaseModel):
    ok: Literal[True] = True
    type: str
    payload: dict[str, str]


def bus(request: Request) -> EventBus:
    return request.app.state.bus


async def _send(request: Request, type_: str, payload: dict[str, str]) -> Sent:
    await bus(request).publish(type_, payload)
    return Sent(type=type_, payload=payload)


@router.post("/demo/step", response_model=Sent)
async def demo_step(request: Request) -> Sent:
    return await _send(request, "demo.step", {})


@router.post("/demo/next", response_model=Sent)
async def demo_next(request: Request) -> Sent:
    return await _send(request, "demo.next", {})


@router.post("/demo/back", response_model=Sent)
async def demo_back(request: Request) -> Sent:
    return await _send(request, "demo.back", {})


@router.post("/demo/reset", response_model=Sent)
async def demo_reset(request: Request) -> Sent:
    return await _send(request, "demo.reset", {})


@router.post("/demo/goto", response_model=Sent)
async def demo_goto(req: GotoIn, request: Request) -> Sent:
    beat = req.beat.strip()
    beat = beat[:1].upper() + beat[1:].lower()  # "b5B" -> "B5b"
    if not _BEAT_RE.match(beat):
        raise HTTPException(422, f"unknown beat {req.beat!r}; use B0..B7")
    return await _send(request, "demo.goto", {"beat": beat})


@router.get("/demo", response_class=HTMLResponse)
def page() -> str:
    cues = "".join(f"<li><b>{when}</b><span>{what}</span></li>" for when, what in CUES)
    beats = "".join(f'<button class="beat" data-beat="{b}"><b>{b}</b><span>{name}</span></button>' for b, name in BEATS)
    return PAGE.replace("%BEATS%", beats).replace("%CUES%", cues)


PAGE = """<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<title>Albie demo remote</title>
<style>
:root{color-scheme:dark;--bg:#0e1116;--card:#1a1f27;--line:#2a313c;--ink:#eef1f5;--dim:#8d97a5;--accent:#4aa3ff}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
body{margin:0 auto;background:var(--bg);color:var(--ink);font:16px/1.4 system-ui,-apple-system,"Segoe UI",sans-serif;
  padding:16px;padding-bottom:calc(16px + env(safe-area-inset-bottom));max-width:560px}
h1{font-size:15px;font-weight:600;color:var(--dim);margin:4px 0 14px}
.row{display:grid;grid-template-columns:1fr 1fr;gap:12px;margin-bottom:12px}
#step{width:100%;height:132px;font-size:30px;font-weight:700;margin-bottom:12px;background:var(--accent);border-color:var(--accent);color:#06121f}
ol{margin:0 0 22px;padding:0;list-style:none;counter-reset:c}
li{counter-increment:c;display:grid;grid-template-columns:26px 1fr;column-gap:8px;padding:9px 12px;border:1px solid var(--line);border-radius:12px;margin-bottom:6px}
li:before{content:counter(c);color:var(--dim);grid-row:span 2}
li span{color:var(--dim);font-size:13px;grid-column:2}
li.on{border-color:var(--accent)}li.done{opacity:.45}
button{font:inherit;color:var(--ink);background:var(--card);border:1px solid var(--line);border-radius:16px;cursor:pointer}
button:active{transform:scale(.97);background:#232a35}
.big{height:72px;font-size:20px;font-weight:600}
#reset{width:100%;height:64px;font-size:18px;margin-bottom:22px;color:#ffb4a8}
.beats{display:grid;grid-template-columns:1fr 1fr;gap:10px}
.beat{display:flex;flex-direction:column;align-items:flex-start;gap:2px;padding:12px 14px;min-height:64px;text-align:left}
.beat b{font-size:18px}.beat span{font-size:13px;color:var(--dim)}
.beat.on{border-color:var(--accent)}
#status{margin-top:18px;min-height:20px;color:var(--dim);font-size:14px}
</style></head><body>
<h1>Albie demo remote</h1>
<button id="step">Cue</button>
<ol id="cues">%CUES%</ol>
<div class="row"><button class="big" id="back">Back</button><button class="big" id="next">Skip beat</button></div>
<button id="reset">Reset</button>
<div class="beats">%BEATS%</div>
<div id="status">Ready</div>
<script>
const st=document.getElementById("status");
async function send(path,body){
  try{
    const r=await fetch(path,{method:"POST",headers:{"Content-Type":"application/json"},body:body?JSON.stringify(body):null});
    st.textContent=r.ok?("Sent "+(body&&body.beat?body.beat:path.split("/").pop())):("Failed "+r.status);
    if(navigator.vibrate)navigator.vibrate(15);
  }catch(e){st.textContent="Hub not reachable"}
}
// The highlighted cue is only a place-keeper for the operator (tap a row to move it): the headset decides what a Cue does.
const rows=[...document.querySelectorAll("#cues li")];let at=0;
function mark(i){at=Math.max(0,Math.min(rows.length,i));rows.forEach((r,n)=>{r.classList.toggle("on",n===at);r.classList.toggle("done",n<at)})}
rows.forEach((r,n)=>r.onclick=()=>mark(n));mark(0);
document.getElementById("step").onclick=()=>{send("/demo/step");mark(at+1)};
document.addEventListener("keydown",e=>{if(e.code==="Space"||e.code==="Enter"){e.preventDefault();document.getElementById("step").click()}});
for(const k of ["next","back","reset"])document.getElementById(k).onclick=()=>{
  if(k==="reset"){document.querySelectorAll(".beat").forEach(b=>b.classList.remove("on"));mark(0)}
  send("/demo/"+k)};
document.querySelectorAll(".beat").forEach(b=>b.onclick=()=>{
  document.querySelectorAll(".beat").forEach(x=>x.classList.toggle("on",x===b));
  send("/demo/goto",{beat:b.dataset.beat})});
</script></body></html>
"""
