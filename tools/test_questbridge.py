"""Tests for questbridge.py against a fake bridge server. Run: python3 -m unittest tools/test_questbridge.py -v"""
import json
import os
import socketserver
import subprocess
import sys
import tempfile
import threading
import unittest

CLI = os.path.join(os.path.dirname(os.path.abspath(__file__)), "questbridge.py")


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
                out.update(head={"pos": [0, 1.6, 0], "fwd": [0, 0, 1]}, fps=72.0, windows=s.windows)
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


if __name__ == "__main__":
    unittest.main()
