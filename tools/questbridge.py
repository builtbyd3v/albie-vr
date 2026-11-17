#!/usr/bin/env python3
"""Laptop client for the Albie debug bridge (development builds and the Unity editor only).

With a headset on the table, plugged in over USB (Developer Mode on, USB debugging allowed), run in order:
  1. sh quest-run.sh                                 # install and launch a Development Build (Ctrl+C stops the log, app keeps running)
  2. python3 tools/questbridge.py wake               # keep it awake unworn, pause the boundary
  3. python3 tools/questbridge.py ping               # does the bridge answer? (runs adb forward itself)
  4. python3 tools/questbridge.py shot /tmp/q.png    # see what the headset shows
  5. python3 tools/questbridge.py sweep              # run tools/questbridge_cases.json, report in unity/Logs/questbridge-*/
  6. python3 tools/questbridge.py rest               # undo wake

Against the Unity editor in play mode: add --no-adb (for example `questbridge.py --no-adb sweep`).
Env: ADB overrides the adb path. QUEST_PIN (the account's Store PIN) adds Meta's documented boundary/autosleep switch to wake/rest.
"""
import argparse
import itertools
import json
import os
import socket
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ADB = os.environ.get("ADB", "/Applications/Unity/Hub/Editor/6000.6.7f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb")
PNG_SIG = b"\x89PNG\r\n\x1a\n"
BAD_LOGS = {"Error", "Exception", "Assert"}
_ids = itertools.count(1)

SCRIPTABLE = "https://developers.meta.com/horizon/documentation/native/android/ts-scriptable-testing/"
OPERATOR = "https://developers.meta.com/horizon/documentation/unity/meta-xr-operator/quest/"


def rc_props(value):
    # content://com.oculus.rc SET_PROPERTY with disable_guardian / disable_autosleep, Store PIN required.
    # Confirmed on SCRIPTABLE (2026-11-16). Survives reboots, so `rest` must undo it.
    pin = os.environ.get("QUEST_PIN")
    if not pin:
        return []
    return [["shell", "content", "call", "--uri", "content://com.oculus.rc", "--method", "SET_PROPERTY",
             "--extra", f"disable_guardian:b:{value}", "--extra", f"disable_autosleep:b:{value}",
             "--extra", f"PIN:s:{pin}"]]


def wake_cmds():
    return [
        # Fake "headset on" so it stays awake and the runtime keeps running off-face.
        # Confirmed on SCRIPTABLE and OPERATOR (2026-11-16). Lost on reboot.
        ["shell", "am", "broadcast", "-a", "com.oculus.vrpowermanager.prox_close"],
        # UNVERIFIED: on no Meta doc page; community posts only, and they disagree on whether 1 or 0 pauses.
        ["shell", "setprop", "debug.oculus.guardian_pause", "1"],
    ] + rc_props("true")


def rest_cmds():
    return [
        # Hand control back to the physical proximity sensor. Confirmed on OPERATOR (2026-11-16).
        ["shell", "am", "broadcast", "-a", "com.oculus.vrpowermanager.automation_disable"],
        # UNVERIFIED: see wake_cmds.
        ["shell", "setprop", "debug.oculus.guardian_pause", "0"],
    ] + rc_props("false")


def adb(*args, **kw):
    return subprocess.run([ADB, *args], **kw)


def run_adb_list(cmds):
    code = 0
    for c in cmds:
        shown = " ".join("PIN:s:****" if a.startswith("PIN:s:") else a for a in c)
        p = adb(*c, capture_output=True, text=True)
        print(f"adb {shown}\n  -> exit {p.returncode} {(p.stdout + p.stderr).strip()}")
        code = code or p.returncode
    if not os.environ.get("QUEST_PIN"):
        print("note: QUEST_PIN not set, so Meta's documented disable_guardian/disable_autosleep switch was skipped.")
    return 1 if code else 0


def forward(port):
    p = adb("forward", f"tcp:{port}", f"tcp:{port}", capture_output=True, text=True)
    if p.returncode:
        raise OSError(f"adb forward failed: {(p.stdout + p.stderr).strip()} (headset plugged in and allowed?)")


def screenshot(path):
    """Return None on success, else the reason it failed."""
    try:
        with open(path, "wb") as f:
            p = adb("exec-out", "screencap", "-p", stdout=f, stderr=subprocess.PIPE)
        with open(path, "rb") as f:
            head = f.read(8)
    except OSError as e:
        return f"screenshot failed: {e}"
    size = os.path.getsize(path)
    if p.returncode or head != PNG_SIG or size < 1024:
        return f"bad screenshot (exit {p.returncode}, {size} bytes, png={head == PNG_SIG}) {p.stderr.decode(errors='replace').strip()}"
    return None


def connect(a):
    sock = socket.create_connection((a.host, a.port), timeout=5)
    return sock, sock.makefile("rw", encoding="utf-8", newline="\n")


def close(conn):
    for x in reversed(conn):
        try:
            x.close()
        except OSError:
            pass


def request(conn, cmd, timeout=10, **kw):
    sock, f = conn
    rid = next(_ids)
    sock.settimeout(timeout)
    f.write(json.dumps({"id": rid, "cmd": cmd, **kw}) + "\n")
    f.flush()
    line = f.readline()
    if not line:
        raise ConnectionError("bridge closed the connection")
    resp = json.loads(line)
    if resp.get("id") != rid:
        raise ValueError(f"response id {resp.get('id')!r} != request id {rid}")
    return resp


def call(conn, tool, args, timeout_s=30):
    return request(conn, "call", timeout=timeout_s + 10, tool=tool, args=args, timeout_s=timeout_s)


def one(a, cmd, **kw):
    conn = connect(a)
    try:
        resp = call(conn, kw["tool"], kw["args"], kw["timeout_s"]) if cmd == "call" else request(conn, cmd, **kw)
    finally:
        close(conn)
    print(json.dumps(resp, indent=2))
    if cmd == "say" and not resp.get("ok") and "not supported" in str(resp.get("error", "")):
        print("SKIP: this build does not support say yet.")
        return 0
    return 0 if resp.get("ok") else 1


def pick_window(windows, want):
    if want:
        hits = [w for w in windows if want in str(w.get("name", "")) or want in str(w.get("path", ""))]
        return hits[0] if hits else None
    return min(windows, key=lambda w: w.get("distance_m") or 1e9) if windows else None


def run_case(conn, case, i, out, a, names):
    tool, args = case.get("tool"), case.get("args") or {}
    r = {"name": case["name"], "tool": tool or "cmd:" + case["cmd"], "args": args, "status": "PASS", "reasons": []}
    if tool and tool not in names:
        r.update(status="SKIP", reasons=["tool not registered in this build"])
        return r
    if conn is None:
        r.update(status="FAIL", reasons=["no connection to the bridge"])
        return r
    try:
        seq = request(conn, "logs").get("seq", 0)
        # {"cmd": "hand", "args": {...}} sends that bridge command with args as its fields; {"tool": ...} calls an Albie tool.
        r["response"] = resp = call(conn, tool, args) if tool else request(conn, case["cmd"], timeout=40, **args)
        time.sleep(case.get("wait_s", 1.5))
        r["state"] = state = request(conn, "state")
        if not a.no_adb:
            name = f"{i:02d}-{''.join(c if c.isalnum() else '_' for c in case['name'])}.png"
            err = screenshot(os.path.join(out, name))
            r["screenshot"] = None if err else name
            if err:
                r["screenshot_error"] = err
        logs = request(conn, "logs", since=seq)
    except (OSError, ValueError) as e:
        r.update(status="FAIL", reasons=[f"connection lost: {e}"], dropped=True)
        return r
    result = resp.get("result") or {}
    if not resp.get("ok"):
        r["reasons"].append(f"bridge error: {resp.get('error')}")
    elif isinstance(result, dict) and result.get("error") and not case.get("allow_tool_error"):
        r["reasons"].append(f"tool error: {result['error']}")
    r["error_logs"] = [l for l in logs.get("lines", []) if l.get("seq", 0) > seq and l.get("type") in BAD_LOGS]
    if r["error_logs"]:
        r["reasons"].append(f"{len(r['error_logs'])} new error log line(s)")
    win = pick_window(state.get("windows") or [], case.get("expect_window"))
    if case.get("expect_window") and not win:
        r["reasons"].append(f"no window matching {case['expect_window']!r}")
    r["window"] = win
    if r["reasons"]:
        r["status"] = "FAIL"
    return r


def write_report(out, info, results):
    count = {s: sum(r["status"] == s for r in results) for s in ("PASS", "FAIL", "SKIP")}
    failed = [r for r in results if r["status"] == "FAIL"]
    shots = sum(bool(r.get("screenshot")) for r in results)
    lines = [
        "# questbridge sweep",
        "",
        f"- {len(results)} cases: {count['PASS']} pass, {count['FAIL']} fail, {count['SKIP']} skip.",
        f"- Build: {info.get('app')} {info.get('version')} on {info.get('platform')}, Unity {info.get('unity')}, scene {info.get('scene')}.",
        f"- Failed: {', '.join(r['name'] for r in failed) or 'none'}.",
        f"- Skipped cases use tools this build does not register.",
        f"- Screenshots: {shots}. Full data: results.json.",
        "",
        "| case | result | reason | window distance (m) | facing (deg) | screenshot |",
        "|---|---|---|---|---|---|",
    ]
    for r in results:
        w = r.get("window") or {}
        cells = [r["name"], r["status"], "; ".join(r["reasons"]), w.get("distance_m", ""), w.get("facing_deg", ""), r.get("screenshot") or ""]
        lines.append("| " + " | ".join(str(c).replace("|", "\\|").replace("\n", " ") for c in cells) + " |")
    for r in failed:
        if r.get("error_logs"):
            lines += ["", f"## {r['name']}: error logs", ""]
            for l in r["error_logs"]:
                lines.append(f"- [{l.get('seq')}] {l.get('type')}: {l.get('msg')}")
                if l.get("stack"):
                    lines += ["", "```", l["stack"].strip(), "```"]
    with open(os.path.join(out, "report.md"), "w") as f:
        f.write("\n".join(lines) + "\n")
    with open(os.path.join(out, "results.json"), "w") as f:
        json.dump({"ping": info, "results": results}, f, indent=2)
    return count


def sweep(a):
    with open(a.cases) as f:
        cases = json.load(f)
    out = a.out or os.path.join(ROOT, "unity", "Logs", time.strftime("questbridge-%m%d-%H%M"))
    os.makedirs(out, exist_ok=True)
    conn = connect(a)
    info = request(conn, "ping")
    names = set(request(conn, "tools").get("names") or [])
    results = []
    for i, case in enumerate(cases, 1):
        r = run_case(conn, case, i, out, a, names)
        if r.pop("dropped", False):
            close(conn)
            try:
                conn = connect(a)
            except OSError:
                conn = None
        results.append(r)
        print(f"{r['status']}  {r['name']}" + (f": {'; '.join(r['reasons'])}" if r["reasons"] else ""))
    if conn:
        close(conn)
    count = write_report(out, info, results)
    print(f"{count['PASS']} pass, {count['FAIL']} fail, {count['SKIP']} skip. Report: {os.path.join(out, 'report.md')}")
    return 1 if count["FAIL"] else 0


def main(argv=None):
    p = argparse.ArgumentParser(description="Client for the Albie debug bridge (TCP newline-JSON on port 8765).")
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=8765)
    p.add_argument("--no-adb", action="store_true", help="talk straight to the Unity editor, skip every adb step")
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("wake", help="keep an unworn headset awake and pause the boundary")
    sub.add_parser("rest", help="undo wake")
    sub.add_parser("forward", help="adb forward tcp:PORT tcp:PORT")
    for c in ("ping", "tools", "state"):
        sub.add_parser(c, help=f"send {c}")
    s = sub.add_parser("logs", help="Unity log lines")
    s.add_argument("--since", type=int)
    s = sub.add_parser("call", help="call one Albie tool")
    s.add_argument("tool")
    s.add_argument("json_args", nargs="?", default="{}", help='JSON object, e.g. \'{"view": "cad"}\'')
    s.add_argument("--timeout", type=float, default=30)
    s = sub.add_parser("say", help="send Albie a typed sentence")
    s.add_argument("text")
    s = sub.add_parser("cmd", help="send any bridge command, e.g. cmd hand '{\"gesture\": \"fist\"}'")
    s.add_argument("name")
    s.add_argument("json_params", nargs="?", default="{}", help="JSON object of extra request fields")
    s = sub.add_parser("shot", help="save a headset screenshot")
    s.add_argument("out")
    s = sub.add_parser("sweep", help="unattended test run over a case file")
    s.add_argument("cases", nargs="?", default=os.path.join(ROOT, "tools", "questbridge_cases.json"))
    s.add_argument("--out", help="report folder (default unity/Logs/questbridge-MMDD-HHMM/)")
    a = p.parse_args(argv)

    if a.no_adb and a.cmd in ("wake", "rest", "forward", "shot"):
        print(f"{a.cmd} needs the headset over adb; it does nothing with --no-adb.")
        return 1
    try:
        if a.cmd == "wake":
            return run_adb_list(wake_cmds())
        if a.cmd == "rest":
            return run_adb_list(rest_cmds())
        if a.cmd == "shot":
            err = screenshot(a.out)
            print(err or f"saved {a.out}")
            return 1 if err else 0
        if not a.no_adb:
            forward(a.port)
        if a.cmd == "forward":
            print(f"forwarded tcp:{a.port}")
            return 0
        if a.cmd == "logs":
            return one(a, "logs", **({} if a.since is None else {"since": a.since}))
        if a.cmd == "call":
            args = json.loads(a.json_args)
            if not isinstance(args, dict):
                raise ValueError("JSON_ARGS must be a JSON object")
            return one(a, "call", tool=a.tool, args=args, timeout_s=a.timeout)
        if a.cmd == "cmd":
            params = json.loads(a.json_params)
            if not isinstance(params, dict):
                raise ValueError("JSON_PARAMS must be a JSON object")
            return one(a, a.name, timeout=float(params.get("timeout_s", 30)) + 10, **params)
        if a.cmd == "say":
            return one(a, "say", text=a.text)
        if a.cmd == "sweep":
            return sweep(a)
        return one(a, a.cmd)
    except FileNotFoundError as e:
        print(f"not found: {e.filename} (set ADB to your adb path if this is adb)")
    except (OSError, ValueError) as e:
        print(f"error talking to the bridge at {a.host}:{a.port}: {e}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
