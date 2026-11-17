"""Tests for questbridge.py against a fake bridge server. Run: python3 -m unittest tools/test_questbridge.py -v"""
import importlib.util
import json
import os
import re
import socketserver
import subprocess
import sys
import tempfile
import threading
import time
import unittest

TOOLS = os.path.dirname(os.path.abspath(__file__))
CLI = os.path.join(TOOLS, "questbridge.py")
ASSETS = os.path.join(os.path.dirname(TOOLS), "unity", "Assets", "_Forge")
_spec = importlib.util.spec_from_file_location("questbridge", CLI)
qb = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(qb)


class Bridge(socketserver.StreamRequestHandler):
    """Speaks the bridge protocol. Tools: ok_tool works, err_tool returns result.error, log_tool logs an Error,
    drop_tool drops the connection the first time it is called. ghost_tool is not registered."""

    def log(self, kind, msg):
        s = self.server
        s.seq += 1
        s.lines.append({"seq": s.seq, "type": kind, "msg": msg, **({"stack": "at Fake.Tool()"} if kind != "Log" else {})})

    def handle(self):
        s = self.server
        for raw in self.rfile:
            req = json.loads(raw)
            cmd, out = req.get("cmd"), {"id": req.get("id"), "ok": True}
            if cmd == "ping":
                out.update(app="com.example.albie", version="0.1", unity="6000.6.7f1", scene="Main", frame=42, platform="Editor")
            elif cmd == "tools":
                out["names"] = ["ok_tool", "err_tool", "log_tool", "drop_tool"]
            elif cmd == "state":
                away = s.away_at is not None and time.time() >= s.away_at
                out.update(head={"pos": [0, 1.6, 0], "fwd": [0, 0, 1]}, fps=72.0, windows=s.windows,
                           lifecycle={"away": away, "time_scale": 0 if away else 1},
                           voice={"present": True, "connected": False, "held": s.held})
            elif cmd == "lifecycle":  # fake only: "delay" puts the away flip in the future
                s.away_at = time.time() + req.get("delay", 0) if req.get("away") else None
                self.log("Log", "[AppLifecycle] away (simulated headset off)" if req.get("away") else "[AppLifecycle] back (simulated headset on) after 1 s.")
            elif cmd == "voice_guard":
                s.held = bool(req.get("on", True))
                out.update(on=s.held, voice=True)
            elif cmd == "logs":
                since = req.get("since", -1)
                out.update(seq=s.seq, lines=[l for l in s.lines if l["seq"] > since])
            elif cmd == "say":
                out = {"id": req.get("id"), "ok": False, "error": "not supported yet"}
            elif cmd == "call":
                tool = req.get("tool")
                if tool == "ok_tool":
                    s.windows = [{"name": "CAD View", "type": "view", "path": "Desk/CAD", "pos": [0, 1, 1], "distance_m": 0.8, "facing_deg": 12.5}]
                    self.log("Log", "opened")
                    out["result"] = {"opened": True, "args": req.get("args", {})}
                elif tool == "err_tool":
                    out["result"] = {"error": "sit down at the desk first"}
                elif tool == "log_tool":
                    self.log("Error", "NullReferenceException in Fake.Tool")
                    out["result"] = {}
                elif tool == "drop_tool" and not s.dropped:
                    s.dropped = True
                    return  # close without answering
                elif tool == "drop_tool":
                    out["result"] = {}
                else:
                    out = {"id": req.get("id"), "ok": False, "error": f"unknown tool {tool}"}
            else:
                out = {"id": req.get("id"), "ok": False, "error": f"unknown cmd {cmd}"}
            self.wfile.write((json.dumps(out) + "\n").encode())


class QuestBridgeTest(unittest.TestCase):
    def setUp(self):
        self.start()
        self.tmp = tempfile.TemporaryDirectory()

    def start(self):
        self.srv = socketserver.ThreadingTCPServer(("127.0.0.1", 0), Bridge)
        self.srv.daemon_threads = True
        self.srv.seq, self.srv.lines, self.srv.windows, self.srv.dropped = 0, [], [], False
        self.srv.away_at, self.srv.held = None, False
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()

    def tearDown(self):
        self.srv.shutdown()
        self.srv.server_close()
        self.tmp.cleanup()

    def cli(self, *args, port=None):
        p = subprocess.run([sys.executable, CLI, "--no-adb", "--port", str(port or self.srv.server_address[1]), *args],
                           capture_output=True, text=True, timeout=60)
        return p.returncode, p.stdout + p.stderr

    def cases(self, cases):
        path = os.path.join(self.tmp.name, "cases.json")
        with open(path, "w") as f:
            json.dump(cases, f)
        return path

    def test_ping(self):
        code, out = self.cli("ping")
        self.assertEqual(code, 0, out)
        self.assertEqual(json.loads(out)["app"], "com.example.albie")

    def test_tools(self):
        code, out = self.cli("tools")
        self.assertEqual(code, 0)
        self.assertIn("ok_tool", json.loads(out)["names"])

    def test_state(self):
        code, out = self.cli("state")
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(out)["fps"], 72.0)

    def test_logs_since(self):
        self.cli("call", "ok_tool")
        self.cli("call", "log_tool")
        code, out = self.cli("logs", "--since", "1")
        self.assertEqual(code, 0)
        self.assertEqual([l["seq"] for l in json.loads(out)["lines"]], [2])

    def test_call_passes_args_and_exit_codes(self):
        code, out = self.cli("call", "ok_tool", '{"view": "cad"}', "--timeout", "5")
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(out)["result"]["args"], {"view": "cad"})
        code, out = self.cli("call", "err_tool")  # tool-level error is still ok:true
        self.assertEqual(code, 0)
        self.assertIn("error", json.loads(out)["result"])
        code, _ = self.cli("call", "ghost_tool")
        self.assertEqual(code, 1)
        code, out = self.cli("call", "ok_tool", "[1]")
        self.assertEqual(code, 1)
        self.assertIn("JSON object", out)

    def test_say_not_supported_is_skip(self):
        code, out = self.cli("say", "hello")
        self.assertEqual(code, 0)
        self.assertIn("SKIP", out)

    def test_no_server_exits_1(self):
        port = self.srv.server_address[1]
        self.srv.shutdown()
        self.srv.server_close()
        code, out = self.cli("ping", port=port)
        self.assertEqual(code, 1)
        self.assertIn("error talking to the bridge", out)
        self.start()  # tearDown shuts down a running server

    def test_device_only_commands_refuse_no_adb(self):
        for cmd in (["shot", "x.png"], ["wake"], ["rest"]):
            code, out = self.cli(*cmd)
            self.assertEqual(code, 1)
            self.assertIn("needs the headset", out)

    def test_sweep_mix_and_reports(self):
        out_dir = os.path.join(self.tmp.name, "out")
        path = self.cases([
            {"name": "works", "tool": "ok_tool", "args": {"a": 1}, "wait_s": 0, "expect_window": "CAD"},
            {"name": "tool error", "tool": "err_tool", "wait_s": 0},
            {"name": "tool error allowed", "tool": "err_tool", "wait_s": 0, "allow_tool_error": True},
            {"name": "logs an error", "tool": "log_tool", "wait_s": 0},
            {"name": "missing window", "tool": "ok_tool", "wait_s": 0, "expect_window": "Notebook"},
            {"name": "not in build", "tool": "ghost_tool", "wait_s": 0},
        ])
        code, out = self.cli("sweep", path, "--out", out_dir)
        self.assertEqual(code, 1, out)
        with open(os.path.join(out_dir, "results.json")) as f:
            results = {r["name"]: r for r in json.load(f)["results"]}
        self.assertEqual({k: r["status"] for k, r in results.items()}, {
            "works": "PASS", "tool error": "FAIL", "tool error allowed": "PASS",
            "logs an error": "FAIL", "missing window": "FAIL", "not in build": "SKIP"})
        self.assertEqual(results["logs an error"]["error_logs"][0]["type"], "Error")
        self.assertEqual(results["works"]["window"]["distance_m"], 0.8)
        with open(os.path.join(out_dir, "report.md")) as f:
            report = f.read()
        self.assertIn("6 cases: 2 pass, 3 fail, 1 skip.", report)
        self.assertIn("NullReferenceException", report)
        self.assertIn("PASS  works", out)
        self.assertIn("SKIP  not in build", out)

    def test_sweep_all_pass_exits_0(self):
        out_dir = os.path.join(self.tmp.name, "out")
        code, out = self.cli("sweep", self.cases([{"name": "works", "tool": "ok_tool", "wait_s": 0}]), "--out", out_dir)
        self.assertEqual(code, 0, out)

    def test_sweep_survives_dropped_connection(self):
        out_dir = os.path.join(self.tmp.name, "out")
        path = self.cases([
            {"name": "before", "tool": "ok_tool", "wait_s": 0},
            {"name": "drops", "tool": "drop_tool", "wait_s": 0},
            {"name": "after", "tool": "ok_tool", "wait_s": 0},
        ])
        code, out = self.cli("sweep", path, "--out", out_dir)
        self.assertEqual(code, 1, out)
        self.assertIn("FAIL  drops: connection lost", out)
        self.assertIn("PASS  after", out)

    def sweep(self, cases, *extra):
        out_dir = os.path.join(self.tmp.name, "out")
        code, out = self.cli("sweep", self.cases(cases), "--out", out_dir, *extra)
        with open(os.path.join(out_dir, "results.json")) as f:
            results = {r["name"]: r for r in json.load(f)["results"]}
        with open(os.path.join(out_dir, "report.md")) as f:
            return code, out, results, f.read()

    def test_cmd_cases_expect_state_logs_and_skips(self):
        code, out, results, report = self.sweep([
            {"name": "off", "cmd": "lifecycle", "cmd_args": {"away": True}, "wait_s": 0,
             "expect_state": {"lifecycle.away": True, "lifecycle.time_scale": 0}, "expect_logs": ["[AppLifecycle] away"]},
            {"name": "wrong state", "cmd": "lifecycle", "cmd_args": {"away": False}, "wait_s": 0, "expect_state": {"lifecycle.away": True}},
            {"name": "log counted", "cmd": "lifecycle", "cmd_args": {"away": False}, "wait_s": 0,
             "expect_logs": [{"text": "[AppLifecycle] back", "count": 2}], "forbid_logs": ["Welcome back"]},
            {"name": "response checked", "cmd": "voice_guard", "cmd_args": {"on": True}, "wait_s": 0,
             "expect": {"on": True, "voice": {"eq": True}}, "expect_state": {"voice.held": True}},
            {"name": "old build", "cmd": "warp", "wait_s": 0},
            {"name": "needs voice off", "cmd": "state", "wait_s": 0, "requires_state": {"voice.held": False}},
            {"name": "costs money", "cmd": "say", "cmd_args": {"text": "hi"}, "paid": True},
            {"name": "by hand", "manual": True, "steps": ["Take the headset off."], "look_for": ["[AppLifecycle] away ("]},
        ])
        self.assertEqual(code, 1, out)
        self.assertEqual({k: r["status"] for k, r in results.items()}, {
            "off": "PASS", "wrong state": "FAIL", "log counted": "FAIL", "response checked": "PASS",
            "old build": "SKIP", "needs voice off": "SKIP", "costs money": "SKIP", "by hand": "SKIP"})
        self.assertIn("state lifecycle.away: want true, got false", results["wrong state"]["reasons"][0])
        self.assertIn("want 2, saw 1", results["log counted"]["reasons"][0])
        self.assertIn("bridge command 'warp' not in this build", results["old build"]["reasons"][0])
        self.assertIn("--paid", results["costs money"]["reasons"][0])
        self.assertIn("## Manual cases", report)
        self.assertIn("- `[AppLifecycle] away (`", report)

    def test_paid_runs_only_with_flag(self):
        case = [{"name": "costs money", "cmd": "ping", "paid": True, "wait_s": 0}]
        self.assertEqual(self.sweep(case)[2]["costs money"]["status"], "SKIP")
        self.assertEqual(self.sweep(case, "--paid")[2]["costs money"]["status"], "PASS")

    def test_warn_if_reports_without_failing(self):
        code, out, results, report = self.sweep([
            {"name": "identity", "cmd": "ping", "wait_s": 0, "expect": {"app": "com.albie.vr"}, "warn_if": {"app": {"contains": "example"}}},
            {"name": "identity strict", "cmd": "ping", "wait_s": 0, "expect": {"version": "1.0.0"}, "warn_if": {"app": "com.albie.vr"}},
        ])
        self.assertEqual(results["identity"]["status"], "WARN")
        self.assertEqual(results["identity strict"]["status"], "FAIL")
        self.assertIn("1 warn (reported, not failed)", report)
        code, _, _, _ = self.sweep([{"name": "identity", "cmd": "ping", "wait_s": 0, "expect": {"app": "com.albie.vr"},
                                     "warn_if": {"app": {"contains": "example"}}}])
        self.assertEqual(code, 0)  # a WARN alone exits 0

    def test_within_polls_and_elapsed(self):
        code, out, results, _ = self.sweep([
            {"name": "flips later", "cmd": "lifecycle", "cmd_args": {"away": True, "delay": 1.5}, "wait_s": 0, "within_s": 6,
             "expect_state": {"lifecycle.away": True}, "expect_elapsed": {"gte": 1.0, "lte": 5}},
            {"name": "too slow", "cmd": "lifecycle", "cmd_args": {"away": True, "delay": 30}, "wait_s": 0, "within_s": 1.5,
             "expect_state": {"lifecycle.away": True}},
        ])
        self.assertEqual(results["flips later"]["status"], "PASS", results["flips later"]["reasons"])
        self.assertGreaterEqual(results["flips later"]["elapsed_s"], 1.0)
        self.assertEqual(results["too slow"]["status"], "FAIL")

    def test_steady_catches_a_late_change(self):
        _, _, results, _ = self.sweep([
            {"name": "arm", "cmd": "lifecycle", "cmd_args": {"away": True, "delay": 1.5}, "wait_s": 0},
            {"name": "must stay home", "cmd": "state", "wait_s": 0, "steady_s": 4, "expect_state": {"lifecycle.away": False}},
        ])
        self.assertEqual(results["must stay home"]["status"], "FAIL")
        self.assertIn("steady", results["must stay home"]["reasons"][0])

    def test_filters_keep_setup_and_teardown(self):
        cases = [
            {"name": "setup: hold", "setup": True, "cmd": "voice_guard", "cmd_args": {"on": True}, "wait_s": 0},
            {"name": "a: one", "group": "alpha", "cmd": "ping", "wait_s": 0},
            {"name": "b: two", "group": "beta", "cmd": "ping", "wait_s": 0},
            {"name": "teardown: free", "teardown": True, "cmd": "voice_guard", "cmd_args": {"on": False}, "wait_s": 0},
        ]
        self.assertEqual(list(self.sweep(cases, "--group", "beta")[2]), ["setup: hold", "b: two", "teardown: free"])
        self.assertEqual(list(self.sweep(cases, "--only", "ONE")[2]), ["setup: hold", "a: one", "teardown: free"])

    def test_matches_operators(self):
        m, miss = qb.matches, qb.MISSING
        self.assertTrue(m(0.0, 0) and m("x", "x") and not m(miss, None) and m(None, None))
        self.assertTrue(m(5, {"gte": 5, "lte": 6}) and not m(True, {"gte": 0}) and not m("5", {"gte": 1}))
        self.assertTrue(m("abc", {"contains": "b"}) and m(["a"], {"not_contains": "b"}) and not m(miss, {"not_contains": "b"}))
        self.assertTrue(m(miss, {"exists": False}) and m(None, {"exists": False}) and m(0, {"exists": True}))
        self.assertTrue(m([1, 2], {"len": 2}) and m("b", {"in": ["a", "b"]}) and m(1, {"ne": 2}))
        rows = [{"id": "messages", "x": 1}, {"id": "messages-mentions"}]
        self.assertTrue(m(rows, {"any": {"id": "messages"}}) and m(rows, {"none": {"id": "notes"}}) and not m(rows, {"none": {"id": "messages"}}))
        self.assertTrue(m({"template": "slalom"}, {"template": "slalom"}))  # a dict without operator keys is a literal
        self.assertEqual(qb.dig({"tries": [{"tool": "t"}]}, "tries.0.tool"), "t")
        self.assertIs(qb.dig({"tries": []}, "tries.0.tool"), miss)

    def test_window_by_panel_id(self):
        self.assertEqual(qb.pick_window([{"name": "MentionsPanel(Clone)", "id": "messages-mentions"}], "messages-mentions")["id"],
                         "messages-mentions")


class CaseFileTest(unittest.TestCase):
    """The shipped case files parse, use only known fields and commands, and their copy matches the C# it asserts."""
    KEYS = {"name", "group", "tool", "args", "cmd", "cmd_args", "wait_s", "timeout_s", "within_s", "steady_s", "expect",
            "expect_state", "expect_logs", "forbid_logs", "expect_window", "expect_elapsed", "allow_tool_error", "allow_error_logs",
            "requires_state", "warn_if", "paid", "manual", "steps", "look_for", "setup", "teardown"}
    BRIDGE = {"ping", "tools", "state", "logs", "say"}

    def load(self, name):
        with open(os.path.join(TOOLS, name)) as f:
            return json.load(f)

    def feature_cmds(self):
        with open(os.path.join(ASSETS, "Core", "DebugBridge", "DebugBridgeFeatures.cs")) as f:
            return set(re.findall(r'case "([a-z_]+)":', f.read()))

    def test_case_files_are_well_formed(self):
        cmds = self.BRIDGE | self.feature_cmds()
        self.assertTrue({"lifecycle", "desk", "voice_guard", "inject", "first_run"} <= cmds)
        for file in ("questbridge_cases.json", "questbridge_feature_cases.json"):
            cases = self.load(file)
            names = [c["name"] for c in cases]
            self.assertEqual(len(names), len(set(names)), f"{file}: duplicate names")
            for c in cases:
                self.assertLessEqual(set(c) - self.KEYS, set(), f"{file}: {c['name']}: unknown fields")
                if c.get("manual"):
                    self.assertTrue(c.get("steps") and c.get("look_for"), c["name"])
                    continue
                self.assertEqual(("tool" in c) + ("cmd" in c), 1, f"{file}: {c['name']}: needs a tool or a cmd")
                if "cmd" in c:
                    self.assertIn(c["cmd"], cmds, c["name"])
            setup = [c["name"] for c in cases if c.get("setup")]
            teardown = [c for c in cases if c.get("teardown")]
            self.assertTrue(setup and teardown, file)
            self.assertIn("voice_guard", [c.get("cmd") for c in teardown], f"{file}: the voice must be given back at the end")

    def test_feature_sweep_spends_nothing_by_default(self):
        cases = self.load("questbridge_feature_cases.json")
        guard = next(i for i, c in enumerate(cases) if c.get("cmd") == "voice_guard" and c["cmd_args"]["on"])
        for i, c in enumerate(cases):
            if c.get("cmd") == "say":
                self.assertTrue(c.get("paid"), f"{c['name']}: say opens a billed voice session, so it must be paid")
                self.assertLess(i, guard, f"{c['name']}: paid voice cases run before the voice is held")
        paid = [c["name"] for c in cases if c.get("paid")]
        self.assertEqual(len(paid), 3, paid)
        free_before = [c for c in cases[:guard] if not c.get("paid")]
        self.assertTrue(all(c.get("cmd") in ("state", "ping") for c in free_before), "only free reads before the voice is held")

    def test_feature_groups_present(self):
        groups = {c.get("group") for c in self.load("questbridge_feature_cases.json")}
        self.assertTrue({"identity", "voice", "first run", "lifecycle", "cloud offline", "messages", "desk", "manual"} <= groups, groups)

    def test_copy_matches_the_csharp(self):
        def const(path, name):
            with open(os.path.join(ASSETS, path)) as f:
                return re.search(name + r' = "([^"]+)";', f.read()).group(1)
        text = json.dumps(self.load("questbridge_feature_cases.json"))
        self.assertIn(json.dumps(const("Core/Net/CloudHealth.cs", "OfflineLine")), text)
        self.assertIn(json.dumps(const("Core/Scripts/FirstRun/FirstRunGuide.cs", "NeedSlalom")), text)
        with open(os.path.join(ASSETS, "Core", "Scripts", "AlbieVoice.cs")) as f:
            voice = f.read()
        self.assertIn("[AlbieVoice] idle: closing the voice session", voice)
        self.assertIn("[AlbieVoice] connected to the voice service", voice)
        self.assertIn("[AlbieVoice] suspended", voice)
        with open(os.path.join(ASSETS, "Core", "Scripts", "AppLifecycle.cs")) as f:
            life = f.read()
        self.assertIn('Debug.Log($"[AppLifecycle] away ({why})', life)
        self.assertIn('Debug.Log($"[AppLifecycle] back ({why}) after', life)


STUB_ADB = r"""#!/bin/bash
# Fake adb for quest-record.sh tests: logs every call, keeps "device" files in $STUB_REMOTE.
echo "$*" >> "$STUB_LOG"
[ "$1" = "devices" ] && { echo "List of devices attached"; printf "%b" "$STUB_DEVICES"; exit 0; }
[ "$1" = "-s" ] && shift 2
case "$1 $2" in
  "shell screenrecord") f="${@: -1}"; touch "$STUB_REMOTE/$(basename "$f")"; sleep 0.3 ;;
  "logcat "*) echo "I Unity : [AppLifecycle] away (headset off)"; exec sleep 30 ;;
  "pull "*) touch "$3" ;;
  "shell ls"*) for f in "$STUB_REMOTE"/*.mp4; do [ -e "$f" ] && echo "/sdcard/$(basename "$f")"; done ;;
  "shell rm"*) rm -f "$STUB_REMOTE"/*.mp4 ;;
esac
exit 0
"""


class RecordScriptTest(unittest.TestCase):
    """tools/quest-record.sh against a fake adb (the real one and the headset are never touched)."""
    SCRIPT = os.path.join(TOOLS, "quest-record.sh")

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        d = self.tmp.name
        self.adb, self.log, self.remote, self.out = (os.path.join(d, n) for n in ("adb", "calls.log", "remote", "out"))
        os.makedirs(self.remote)
        with open(self.adb, "w") as f:
            f.write(STUB_ADB)
        os.chmod(self.adb, 0o755)

    def tearDown(self):
        self.tmp.cleanup()

    def run_script(self, devices, *cmd):
        env = dict(os.environ, ADB=self.adb, STUB_LOG=self.log, STUB_REMOTE=self.remote, STUB_DEVICES=devices)
        p = subprocess.run(["bash", self.SCRIPT, "--out", self.out, "--", *cmd], env=env, capture_output=True, text=True, timeout=60)
        calls = ""
        if os.path.exists(self.log):
            with open(self.log) as f:
                calls = f.read()
        return p.returncode, p.stdout + p.stderr, calls

    def test_no_device_exits_2(self):
        code, out, calls = self.run_script("", "true")
        self.assertEqual(code, 2, out)
        self.assertIn("no headset found", out)
        self.assertNotIn("screenrecord", calls)

    def test_unauthorized_only_counts_as_none(self):
        code, out, _ = self.run_script("1WMHH000000000\tunauthorized\n", "true")
        self.assertEqual(code, 2, out)

    def test_two_devices_exit_3(self):
        code, out, calls = self.run_script("AAA\tdevice\nBBB\tdevice\n", "true")
        self.assertEqual(code, 3, out)
        self.assertNotIn("screenrecord", calls)

    def test_records_runs_pulls_cleans_up(self):
        code, out, calls = self.run_script("1WMHH000000000\tdevice\n", "sh", "-c", "sleep 1; exit 5")
        self.assertEqual(code, 5, out)  # the command's own exit code
        self.assertIn("-s 1WMHH000000000 shell screenrecord --size 1280x720 --bit-rate 8000000 --time-limit 180 /sdcard/albie-rec-", calls)
        self.assertIn("logcat -v threadtime -T 1 Unity:V AndroidRuntime:E *:S", calls)
        self.assertIn("pkill -INT -f /sdcard/albie-rec-", calls)
        self.assertIn("shell rm -f /sdcard/albie-rec-", calls)
        shots = sorted(f for f in os.listdir(self.out) if f.startswith("screen-"))
        self.assertGreaterEqual(len(shots), 2, out)  # 0.3 s fake segments: chained while the command ran
        self.assertEqual(shots[0], "screen-01.mp4")
        self.assertEqual(os.listdir(self.remote), [], "videos left on the headset")
        with open(os.path.join(self.out, "unity.log")) as f:
            self.assertIn("[AppLifecycle] away", f.read())
        self.assertIn(os.path.join(self.out, "screen-01.mp4"), out)
        for word in ("install", "uninstall", "settings put", "setprop", "logcat -c", "reboot"):
            self.assertNotIn(word, calls)


if __name__ == "__main__":
    unittest.main()
