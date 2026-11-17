"""Record Albie's demo lines in his real voice (Grok Voice) as 24 kHz mono 16-bit WAVs.

    cd hub && python scripts/demo_record_lines.py            # all lines
    python scripts/demo_record_lines.py b1_hello b4_verdict  # just these
    python scripts/demo_record_lines.py --list

Uses the same path the hub uses when Albie speaks a given text: GrokVoiceSession.say(text) (agent/voice.py), which sends
a force_message so Grok says the text verbatim, in the voice from hub/.env (XAI_API_KEY, GROK_VOICE). The PCM frames the
session would send to Unity are collected instead of sent. Output: unity/Assets/_Forge/Demo/Resources/DemoAudio/<clip>.wav,
silence trimmed at both ends, loudness about -16 LUFS (pyloudnorm if installed, else an RMS estimate), peak under -1 dB.

Needs the key in hub/.env. Without it (or when the service is unreachable) the script says so and exits 1.
"""
from __future__ import annotations

import argparse
import asyncio
import os
import re
import sys
import wave
from pathlib import Path
from typing import Any

import numpy as np

HUB = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HUB))

OUT_DIR = HUB.parent / "unity" / "Assets" / "_Forge" / "Demo" / "Resources" / "DemoAudio"
RATE = 24000
TARGET_LUFS = -16.0
PEAK_DB = -1.0
SPEECH_TIMEOUT_S = 40.0

LINES: dict[str, str] = {
    "b1_hello": "Hey! What do you feel like building today?",
    "b2_open": "Here's yesterday's quad.",
    "b2_flying": "She's flying. What should the camera do?",
    "b3_search": "Got it. I'll find a camera that fits your battery, and a model that can track you.",
    "b4_verdict": "This one. It runs off the five-volt rail your battery already powers, it's sharp enough to lock onto "
                  "your face at ten meters, it's twenty-four dollars, and it can be here in three days.",
    "b5_brain": "For the brain, this tracking model is a good start. I'll fine-tune it on you, then push it to your "
                "private repo.",
    "b5_pushed": "Done. Version one is in your repo.",
    "b6_building": "Building a flight test in your room.",
    "b6_tracking": "Tracking you. I'll stay two meters back.",
    "b7_done": "Nice flight. Want me to log this run?",
}


class NoVoice(RuntimeError):
    """The voice service can't be used (no key, unreachable, refused)."""


def _transcript_ok(said: str, wanted: str) -> bool:
    """Grok must have said the text verbatim: compare words, ignoring case and punctuation."""
    norm = lambda t: re.findall(r"[a-z0-9']+", t.lower().replace("’", "'"))  # noqa: E731
    return norm(said) == norm(wanted)


async def speak_once(text: str) -> tuple[bytes, str]:
    """Have a Grok voice session say `text`; returns (PCM16 mono at RATE, the transcript Grok reports)."""
    os.environ["VOICE_MAX_HEARD_S"] = "0"  # no sentence cut: the whole line (read when agent.voice is imported)
    from agent import voice as vmod

    vmod.SAY_MAX = 400  # say() keeps whole sentences within 150 characters; b4_verdict is longer
    chunks: list[bytes] = []
    done = asyncio.Event()
    heard: list[str] = []

    async def send_event(event: Any) -> None:
        if event.type == "tts.end":
            done.set()

    async def send_bytes(data: bytes) -> None:
        chunks.append(data)

    async def emit(event: Any) -> None:
        pass

    class Recording(vmod.GrokVoiceSession):
        async def handle(self, msg: dict[str, Any]) -> None:
            if msg.get("type") in ("response.output_audio_transcript.done", "response.audio_transcript.done"):
                heard.append(str(msg.get("transcript") or ""))  # what Grok actually said (say() hides the caption)
            elif msg.get("type") == "error":
                raise NoVoice(f"Grok error: {msg.get('error')}")
            await super().handle(msg)

    session = Recording(pet_id="demo", sample_rate=RATE, send_event=send_event, send_bytes=send_bytes, emit=emit)
    try:
        await session.say(text)  # a force_message: the same call speech.speak makes for the live Albie
        await asyncio.wait_for(done.wait(), SPEECH_TIMEOUT_S)
    except (OSError, asyncio.TimeoutError, RuntimeError) as exc:
        raise NoVoice(f"{type(exc).__name__}: {exc}") from exc
    finally:
        await session.close()
    pcm = b"".join(chunks)
    if not pcm:
        raise NoVoice("the voice session returned no audio")
    return pcm, (heard[-1] if heard else "")


def trim_silence(x: np.ndarray, floor_db: float = -45.0, pad_s: float = 0.06) -> np.ndarray:
    """Cut leading and trailing samples below floor_db relative to the peak, keep a short pad."""
    peak = float(np.max(np.abs(x))) if x.size else 0.0
    if peak == 0.0:
        return x
    live = np.nonzero(np.abs(x) > peak * 10 ** (floor_db / 20))[0]
    pad = int(pad_s * RATE)
    return x[max(0, live[0] - pad): min(x.size, live[-1] + pad + 1)]


def loudness_gain_db(x: np.ndarray) -> float:
    """dB to add to hit TARGET_LUFS: BS.1770 integrated loudness via pyloudnorm if present, else an RMS estimate
    (speech: LUFS is roughly RMS dBFS minus 3 over the active samples)."""
    try:
        import pyloudnorm as pyln

        return TARGET_LUFS - float(pyln.Meter(RATE).integrated_loudness(x))
    except ImportError:
        active = x[np.abs(x) > 0.01 * max(float(np.max(np.abs(x))), 1e-9)]
        rms_db = 20 * np.log10(max(float(np.sqrt(np.mean(active ** 2))), 1e-9))
        return TARGET_LUFS + 3.0 - rms_db  # LUFS ~ RMS dBFS - 3


def finish(pcm: bytes) -> np.ndarray:
    x = np.frombuffer(pcm, dtype="<i2").astype(np.float64) / 32768.0
    x = trim_silence(x)
    gain_db = loudness_gain_db(x)
    peak_db = 20 * np.log10(max(float(np.max(np.abs(x))), 1e-9))
    gain_db = min(gain_db, PEAK_DB - peak_db)  # never past the -1 dB peak ceiling
    fade = int(0.01 * RATE)  # 10 ms fades so a trim never clicks
    x = x * 10 ** (gain_db / 20)
    x[:fade] *= np.linspace(0, 1, fade)
    x[-fade:] *= np.linspace(1, 0, fade)
    return x


def write_wav(path: Path, x: np.ndarray) -> float:
    path.parent.mkdir(parents=True, exist_ok=True)
    data = np.clip(np.round(x * 32767), -32768, 32767).astype("<i2")
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(data.tobytes())
    return data.size / RATE


async def main(names: list[str], retries: int) -> int:
    from dotenv import load_dotenv

    load_dotenv(HUB / ".env")  # same file agent.app.load_env reads
    if not os.getenv("XAI_API_KEY"):
        print("XAI_API_KEY is not set (hub/.env): can't reach Albie's voice. No clips recorded.", file=sys.stderr)
        return 1
    failed = 0
    for name in names:
        text = LINES[name]
        for attempt in range(1, retries + 1):
            try:
                pcm, said = await speak_once(text)
            except NoVoice as exc:
                print(f"{name}: voice failed ({exc})", file=sys.stderr)
                failed += 1
                break
            if not _transcript_ok(said, text):
                print(f"{name}: attempt {attempt}: Grok said {said!r}, wanted {text!r}", file=sys.stderr)
                if attempt == retries:
                    failed += 1
                continue
            secs = write_wav(OUT_DIR / f"{name}.wav", finish(pcm))
            print(f"{name}: {secs:.1f} s -> {OUT_DIR / (name + '.wav')}")
            break
    return 1 if failed else 0


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("clips", nargs="*", help="clip names (default: all)")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--retries", type=int, default=3, help="attempts per clip when Grok changes the words")
    args = ap.parse_args()
    if args.list:
        for k, v in LINES.items():
            print(f"{k}: {v}")
        sys.exit(0)
    unknown = [c for c in args.clips if c not in LINES]
    if unknown:
        sys.exit(f"unknown clip(s): {', '.join(unknown)} (see --list)")
    sys.exit(asyncio.run(main(args.clips or list(LINES), args.retries)))
