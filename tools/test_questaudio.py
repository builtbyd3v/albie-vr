"""Tests for questaudio.py: python3 -m unittest tools/test_questaudio.py -v (standard library only, no headset)."""
import array
import base64
import math
import os
import shutil
import sys
import tempfile
import unittest
import wave

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import questaudio as qa  # noqa: E402

RATE = 16000


def tone(seconds, amp=0.25, hz=440, rate=RATE):
    return [int(amp * 32767 * math.sin(2 * math.pi * hz * i / rate)) for i in range(int(seconds * rate))]


def silence(seconds, rate=RATE):
    return [0] * int(seconds * rate)


def write_wav(path, samples, rate=RATE, channels=1):
    with wave.open(path, "wb") as w:
        w.setnchannels(channels)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(array.array("h", samples).tobytes())
    return path


class Stats(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir)

    def stats(self, samples, channels=1):
        return qa.stats(write_wav(os.path.join(self.dir, "x.wav"), samples, channels=channels))

    def test_tone(self):
        st = self.stats(tone(1.0))
        self.assertEqual(st["duration_s"], 1.0)
        self.assertAlmostEqual(st["peak_dbfs"], -12.0, delta=0.2)
        self.assertAlmostEqual(st["rms_dbfs"], -15.1, delta=0.2)  # a sine's RMS is peak - 3 dB
        self.assertFalse(st["silent"])
        self.assertEqual(st["clipped_pct"], 0)
        self.assertEqual(st["gaps"], [])

    def test_silence(self):
        st = self.stats(silence(1.0))
        self.assertTrue(st["silent"])
        self.assertEqual(st["rms_dbfs"], -120.0)
        self.assertEqual(st["gaps"], [])
        self.assertIsNone(st["first_sound_s"])

    def test_hole_is_exactly_one_gap(self):
        # Leading and trailing silence are not gaps; only the hole between two sounds is.
        st = self.stats(silence(0.2) + tone(0.5) + silence(0.3) + tone(0.5) + silence(0.4))
        self.assertEqual(len(st["gaps"]), 1)
        self.assertAlmostEqual(st["gaps"][0]["start_s"], 0.7, delta=0.002)
        self.assertAlmostEqual(st["gaps"][0]["length_s"], 0.3, delta=0.002)
        self.assertAlmostEqual(st["sound_s"], 1.3, delta=0.002)

    def test_short_hole_is_not_a_gap(self):
        self.assertEqual(self.stats(tone(0.5) + silence(0.1) + tone(0.5))["gaps"], [])

    def test_clipped(self):
        loud = [max(-32767, min(32767, int(1.5 * v / 0.25))) for v in tone(0.5)]
        st = self.stats(loud)
        self.assertGreater(st["clipped_pct"], 10)
        self.assertEqual(st["peak_dbfs"], 0.0)

    def test_stereo_is_averaged(self):
        mono = tone(0.5)
        st = self.stats([v for s in mono for v in (s, s)], channels=2)
        self.assertEqual(st["duration_s"], 0.5)
        self.assertAlmostEqual(st["peak_dbfs"], -12.0, delta=0.2)


@unittest.skipUnless(shutil.which("say"), "macOS say not available")
class Tts(unittest.TestCase):
    def test_tts_writes_16bit_mono_wav(self):
        with tempfile.TemporaryDirectory() as d:
            path = qa.tts("Hello Albie, what time is it?", os.path.join(d, "s.wav"))
            with wave.open(path, "rb") as w:
                self.assertEqual((w.getnchannels(), w.getsampwidth(), w.getframerate()), (1, 2, 16000))
                self.assertGreater(w.getnframes(), 16000 // 2)
            st = qa.stats(path)
            self.assertFalse(st["silent"])


def fake_wav(text, path):
    return write_wav(path, tone(0.1))


class FakeBridge:
    """Answers bridge requests like the app. t_start 100 / t_end 101: the injected speech ends 1 s into the recording.
    The recording holds Albie's reply from 1.4 s, with one 200 ms dropout."""

    def __init__(self, session="open", inject_ok=True, replies=True):
        self.session, self.inject_ok, self.replies = session, inject_ok, replies
        self.calls = []
        self.polls = 0
        self.recording = silence(1.4) + tone(1.0) + silence(0.2) + tone(0.5)
        self.capturing = False

    def __call__(self, cmd, timeout=15, **kw):
        self.calls.append((cmd, kw.get("action")))
        if cmd == "voice":
            v = {"ok": True, "session": self.session, "heard": "earlier", "heard_at": 50.0, "said": "earlier reply",
                 "said_at": 51.0, "error": None, "error_at": -1, "speaking": False, "audio_started": False, "response_active": False}
            if any(c == "mic_inject" for c, _ in self.calls):
                self.polls += 1
                v.update(heard="what time is it", heard_at=101.2)
                if self.replies and self.polls == 1:
                    v.update(response_active=True, audio_started=True)
                elif self.replies and self.polls == 2:
                    v.update(said="It's noon.", said_at=102.0, speaking=True)
                elif self.replies:
                    v.update(said="It's noon.", said_at=102.0)
            return v
        if cmd == "mic_inject":
            self.wav = base64.b64decode(kw["wav_b64"])
            if not self.inject_ok:
                return {"ok": False, "error": "no open voice session (session closed). mic_inject never opens one"}
            return {"ok": True, "samples": 1600, "duration_s": 0.1, "t_end": 101.0}
        if cmd == "audio_capture":
            if kw["action"] == "start":
                self.capturing = True
                return {"ok": True, "t_start": 100.0, "rate": RATE}
            if kw["action"] == "stop":
                self.capturing = False
                self.name = kw["name"]
                return {"ok": True, "rms_dbfs": -20.0, "gaps": []}
            if kw["action"] == "get":
                with tempfile.TemporaryDirectory() as d:
                    with open(write_wav(os.path.join(d, "r.wav"), self.recording), "rb") as f:
                        return {"ok": True, "wav_b64": base64.b64encode(f.read()).decode()}
        return {"ok": False, "error": f"unknown cmd {cmd}"}


class Roundtrip(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir)
        self.now = 0.0

    def clock(self):
        return self.now

    def sleep(self, s):
        self.now += s

    def run_it(self, bridge):
        return qa.roundtrip(bridge, "what time is it", out_dir=self.dir, sleep=self.sleep, clock=self.clock, make_wav=fake_wav)

    def test_happy_path(self):
        b = FakeBridge()
        code, r = self.run_it(b)
        self.assertEqual(code, 0, r)
        self.assertEqual(r["heard"], "what time is it")
        self.assertEqual(r["said"], "It's noon.")
        self.assertAlmostEqual(r["reply_latency_s"], 0.4, delta=0.001)
        self.assertEqual(len(r["gaps"]), 1)
        self.assertAlmostEqual(r["gaps"][0]["length_s"], 0.2, delta=0.002)
        self.assertEqual(r["duration_s"], 3.1)
        self.assertTrue(r["finished"])
        self.assertTrue(os.path.exists(r["wav"]))
        self.assertTrue(b.wav.startswith(b"RIFF"))
        self.assertFalse(b.capturing)
        # Order: check the session, capture on, inject, poll until finished (3 polls), capture off, fetch.
        self.assertEqual(b.calls, [("voice", None), ("audio_capture", "start"), ("mic_inject", None),
                                   ("voice", None), ("voice", None), ("voice", None),
                                   ("audio_capture", "stop"), ("audio_capture", "get")])

    def test_refuses_closed_session_and_sends_nothing_else(self):
        for session in ("closed", "connecting"):
            b = FakeBridge(session=session)
            code, r = self.run_it(b)
            self.assertEqual(code, 1)
            self.assertIn("never opens", r["error"])
            self.assertEqual(b.calls, [("voice", None)])

    def test_inject_refused_stops_the_capture(self):
        b = FakeBridge(inject_ok=False)
        code, r = self.run_it(b)
        self.assertEqual(code, 1)
        self.assertIn("mic_inject", r["error"])
        self.assertFalse(b.capturing)

    def test_no_reply_gives_up_after_the_wait(self):
        b = FakeBridge(replies=False)
        b.recording = silence(2.0)
        code, r = self.run_it(b)
        self.assertEqual(code, 1)
        self.assertIsNone(r["said"])
        self.assertIsNone(r["reply_latency_s"])
        self.assertFalse(r["finished"])
        self.assertTrue(r["silent"])
        self.assertGreaterEqual(self.now, qa.WAIT_S)
        self.assertFalse(b.capturing)


if __name__ == "__main__":
    unittest.main()
