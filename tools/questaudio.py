#!/usr/bin/env python3
"""Albie voice tests from the laptop, with nobody wearing or talking to the headset (debug bridge, development builds).

  python3 tools/questaudio.py tts "Hey Albie, set a timer" /tmp/q.wav   # 16 kHz mono 16-bit WAV from macOS `say`
  python3 tools/questaudio.py stats /tmp/q.wav                          # duration, peak, RMS, clipping, silence, gaps
  python3 tools/questaudio.py capture reply-1 5                         # record 5 s of what the app plays, fetch, judge
  python3 tools/questaudio.py inject "what time is it"                  # speak a sentence into the app's mic path
  python3 tools/questaudio.py roundtrip "what time is it"               # THE unattended voice test, one JSON line

Nothing here plays sound in the room: speech goes in as data (mic_inject) and comes back as data (audio_capture).
inject and roundtrip need a voice session that is already open, and refuse when it is closed: they never open one,
because a live session costs money (xAI). Open it on purpose first (for example `questbridge.py say ...`), then run
them within the app's idle window. The physical microphone is not tested by this tool.

Flags: --no-adb talks to the Unity editor in play mode; --host/--port as questbridge.py.
"""
import argparse
import array
import base64
import json
import math
import os
import subprocess
import sys
import tempfile
import time
import wave

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import questbridge as qb  # noqa: E402

SILENT_DB = -60.0
FLOOR_DB = -120.0
GAP_S = 0.15
WAIT_S = 30.0
OUT = os.path.join(qb.ROOT, "unity", "Logs", "questaudio")


def tts(text, out):
    # LEI16@16000 = little-endian 16-bit integer PCM at 16 kHz (checked with afinfo: 1 ch, 16000 Hz, Int16).
    subprocess.run(["say", "-o", out, "--file-format=WAVE", "--data-format=LEI16@16000", text], check=True)
    return out


def read_wav(path):
    """Mono 16-bit samples (stereo is averaged) and the rate."""
    with wave.open(path, "rb") as w:
        if w.getsampwidth() != 2:
            raise ValueError(f"{path}: need 16-bit PCM, got {8 * w.getsampwidth()}-bit")
        ch, rate = w.getnchannels(), w.getframerate()
        data = array.array("h", w.readframes(w.getnframes()))
    if sys.byteorder == "big":
        data.byteswap()
    if ch > 1:
        data = array.array("h", (int(sum(data[i:i + ch]) / ch) for i in range(0, len(data) - ch + 1, ch)))
    return data, rate


def db(linear):
    return max(FLOOR_DB, round(20 * math.log10(linear), 1)) if linear > 0 else FLOOR_DB


def stats_of(s, rate):
    """Same rules as DebugBridgeAudio.Stats in the app. Silence = |sample| <= 1; a gap = a silent run longer than
    150 ms with sound on both sides (how a dropout shows up)."""
    n = len(s)
    peak = clipped = 0
    first = last = run = -1
    total = 0.0
    gaps = []
    min_gap = int(GAP_S * rate)
    for i, v in enumerate(s):
        a = abs(v)
        total += a * a
        if a > peak:
            peak = a
        if a >= 32767:
            clipped += 1
        if a <= 1:
            if run < 0:
                run = i
            continue
        if run >= 0 and first >= 0 and i - run > min_gap:
            gaps.append({"start_s": round(run / rate, 3), "length_s": round((i - run) / rate, 3)})
        run = -1
        if first < 0:
            first = i
        last = i
    rms = db(math.sqrt(total / n) / 32767) if n else FLOOR_DB
    return {
        "duration_s": round(n / rate, 3), "rate": rate, "peak_dbfs": db(peak / 32767), "rms_dbfs": rms,
        "clipped_pct": round(100 * clipped / n, 3) if n else 0.0, "silent": rms < SILENT_DB,
        "sound_s": round((last - first + 1) / rate, 3) if first >= 0 else 0.0,
        "first_sound_s": round(first / rate, 3) if first >= 0 else None, "gaps": gaps,
    }


def stats(path):
    return stats_of(*read_wav(path))


def first_sound_after(s, rate, t):
    """Seconds into the recording of the first non-silent sample at or after t, or None."""
    for i in range(max(0, int(t * rate)), len(s)):
        if abs(s[i]) > 1:
            return i / rate
    return None


# ---------------------------------------------------------------- bridge helpers (req(cmd, **fields) -> response)

def checked(resp, what):
    if not resp.get("ok"):
        raise RuntimeError(f"{what}: {resp.get('error')}")
    return resp


def inject(req, text, realtime=True, make_wav=tts):
    with tempfile.TemporaryDirectory() as d:
        path = make_wav(text, os.path.join(d, "say.wav"))
        with open(path, "rb") as f:
            raw = f.read()
        with wave.open(path, "rb") as w:
            seconds = w.getnframes() / w.getframerate()
    wait = seconds + 15
    return req("mic_inject", timeout=wait + 10, timeout_s=wait, realtime=realtime,
               wav_b64=base64.b64encode(raw).decode("ascii"))


def fetch(req, name, out_dir):
    got = checked(req("audio_capture", timeout=60, timeout_s=50, action="get", name=name), "audio_capture get")
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, name + ".wav")
    with open(path, "wb") as f:
        f.write(base64.b64decode(got["wav_b64"]))
    return path


def capture(req, name, seconds, out_dir=OUT, sleep=time.sleep):
    checked(req("audio_capture", action="start"), "audio_capture start")
    sleep(seconds)
    stop = checked(req("audio_capture", action="stop", name=name), "audio_capture stop")
    path = fetch(req, name, out_dir)
    return {"device": stop, "local": stats(path), "wav": path}


def finished(v, since):
    if v.get("error_at", -1) > since:
        return True
    return (v.get("said_at", -1) > since and not v.get("speaking") and not v.get("audio_started")
            and not v.get("response_active"))


def roundtrip(req, text, out_dir=OUT, wait_s=WAIT_S, sleep=time.sleep, clock=time.monotonic, make_wav=tts):
    """Capture on, inject TEXT, wait for Albie to finish (or wait_s), capture off, judge. Returns (exit code, result)."""
    v = checked(req("voice"), "voice")
    if v.get("session") != "open":
        return 1, {"error": f"voice session is {v.get('session')}: roundtrip never opens one (a live session costs money). "
                            "Open it on purpose first (for example questbridge.py say ...), then run roundtrip within the idle window."}
    start = checked(req("audio_capture", action="start"), "audio_capture start")
    name = time.strftime("roundtrip-%m%d-%H%M%S")
    try:
        fed = inject(req, text, make_wav=make_wav)
    except Exception:
        req("audio_capture", action="stop", name=name)
        raise
    if not fed.get("ok"):
        req("audio_capture", action="stop", name=name)
        return 1, {"error": f"mic_inject: {fed.get('error')}"}
    t_end = fed["t_end"]
    until = clock() + wait_s
    v = {}
    while clock() < until:
        v = checked(req("voice"), "voice")
        if finished(v, t_end):
            break
        sleep(0.5)
    stop = checked(req("audio_capture", action="stop", name=name), "audio_capture stop")
    path = fetch(req, name, out_dir)
    samples, rate = read_wav(path)
    st = stats_of(samples, rate)
    offset = t_end - start["t_start"]  # end of the injected speech, in recording seconds (both clocks are the app's)
    reply = first_sound_after(samples, rate, offset)
    replied = v.get("said_at", -1) > t_end
    result = {
        "heard": v.get("heard") if v.get("heard_at", -1) > t_end - fed["duration_s"] else None,
        "said": v.get("said") if replied else None,
        "reply_latency_s": round(reply - offset, 3) if reply is not None else None,
        "duration_s": st["duration_s"], "rms_dbfs": st["rms_dbfs"], "clipped_pct": st["clipped_pct"], "gaps": st["gaps"],
        "silent": st["silent"], "finished": finished(v, t_end), "device_stats": {k: stop.get(k) for k in ("rms_dbfs", "gaps")},
        "error": v.get("error") if v.get("error_at", -1) > t_end else None, "wav": path,
    }
    ok = replied and reply is not None and not result["error"]
    return (0 if ok else 1), result


def main(argv=None):
    p = argparse.ArgumentParser(description="Albie voice tests over the debug bridge, no human present.")
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=8765)
    p.add_argument("--no-adb", action="store_true", help="talk straight to the Unity editor, skip adb forward")
    p.add_argument("--out", default=OUT, help="where fetched WAVs go")
    sub = p.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("tts", help="spoken sentence -> 16 kHz mono 16-bit WAV (macOS say)")
    s.add_argument("text")
    s.add_argument("out")
    s = sub.add_parser("stats", help="judge a WAV: duration, peak, RMS, clipped, silent, gaps")
    s.add_argument("file")
    s = sub.add_parser("inject", help="speak TEXT into the app's mic path (needs an open session)")
    s.add_argument("text")
    s.add_argument("--fast", action="store_true", help="feed at once instead of real-time pace")
    s = sub.add_parser("capture", help="record what the app plays for SECONDS, fetch it, judge it")
    s.add_argument("name")
    s.add_argument("seconds", type=float)
    s = sub.add_parser("roundtrip", help="the unattended voice test (needs an open session)")
    s.add_argument("text")
    a = p.parse_args(argv)

    if a.cmd == "tts":
        tts(a.text, a.out)
        print(json.dumps(stats(a.out)))
        return 0
    if a.cmd == "stats":
        print(json.dumps(stats(a.file)))
        return 0
    conn = None
    try:
        if not a.no_adb:
            qb.forward(a.port)
        conn = qb.connect(a)

        def req(cmd, timeout=15, **kw):
            return qb.request(conn, cmd, timeout=timeout, **kw)

        if a.cmd == "inject":
            resp = inject(req, a.text, realtime=not a.fast)
            print(json.dumps(resp))
            return 0 if resp.get("ok") else 1
        if a.cmd == "capture":
            print(json.dumps(capture(req, a.name, a.seconds, a.out)))
            return 0
        code, result = roundtrip(req, a.text, a.out)
        print(json.dumps(result))
        return code
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as e:
        print(json.dumps({"error": f"{type(e).__name__}: {e}"}))
        return 1
    finally:
        if conn:
            qb.close(conn)


if __name__ == "__main__":
    sys.exit(main())
