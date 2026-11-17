#!/usr/bin/env python3
"""Laptop client for the Albie debug bridge (development builds and the Unity editor only).

With a headset on the table, plugged in over USB (Developer Mode on, USB debugging allowed), run in order:
  1. sh quest-run.sh                                 # install and launch a Development Build (Ctrl+C stops the log, app keeps running)
  2. python3 tools/questbridge.py wake               # keep it awake unworn, pause the boundary
  3. python3 tools/questbridge.py ping               # does the bridge answer? (runs adb forward itself)
  4. python3 tools/questbridge.py shot /tmp/q.png    # see what the headset shows
  5. python3 tools/questbridge.py sweep              # run tools/questbridge_cases.json, report in unity/Logs/questbridge-*/
     python3 tools/questbridge.py sweep tools/questbridge_feature_cases.json   # the feature sweep (docs/HEADSET_TESTS.md)
  6. python3 tools/questbridge.py rest               # undo wake

Against the Unity editor in play mode: add --no-adb (for example `questbridge.py --no-adb sweep`).

Case file: a JSON list. Each case runs one tool ("tool", "args") or one bridge command ("cmd", "cmd_args"), waits
"wait_s" (1.5), then checks. Optional checks: "expect" / "expect_state" ({dotted.path: value or {op: value}}, ops eq ne
in contains not_contains gte lte exists len any none) against the response / the state; "expect_logs" (text, or
{"text", "count"}) and "forbid_logs" in the log lines since the case started; "expect_window" (name, path or panel id);
"expect_elapsed" (seconds from the action until everything passed). "within_s" polls until it passes, "steady_s" then
keeps it passing that long. "allow_tool_error", "allow_error_logs", "requires_state" (else SKIP), "warn_if" (a failure
whose response matches is WARN, not FAIL). "paid": SKIP unless --paid. "manual": never run; "steps" and "look_for" go
in the report. "setup" / "teardown": always run, whatever --only / --group pick.
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
        hits = [w for w in windows if any(want in str(w.get(k, "")) for k in ("name", "path", "id"))]
        return hits[0] if hits else None
    return min(windows, key=lambda w: w.get("distance_m") or 1e9) if windows else None


MISSING = object()
OPS = ("eq", "ne", "in", "contains", "not_contains", "gte", "lte", "exists", "len", "any", "none")


def dig(obj, path):
    """obj at a dotted path ("result.opened.status", "tries.0.tool"); MISSING when it isn't there."""
    for key in path.split("."):
        if isinstance(obj, list) and key.lstrip("-").isdigit() and -len(obj) <= int(key) < len(obj):
            obj = obj[int(key)]
        elif isinstance(obj, dict) and key in obj:
            obj = obj[key]
        else:
            return MISSING
    return obj


def subset(item, want):
    return isinstance(item, dict) and all(item.get(k) == v for k, v in want.items())


def matches(actual, want):
    """A literal means equality; a dict of OPS keys means every operator holds."""
    if not (isinstance(want, dict) and want and all(k in OPS for k in want)):
        return actual is not MISSING and actual == want
    for op, v in want.items():
        if op == "exists":
            ok = (actual is not MISSING and actual is not None) == v
        elif actual is MISSING:
            ok = False
        elif op == "eq":
            ok = actual == v
        elif op == "ne":
            ok = actual != v
        elif op == "in":
            ok = actual in v
        elif op in ("contains", "not_contains"):
            ok = isinstance(actual, (str, list)) and (v in actual)
            ok = ok if op == "contains" else isinstance(actual, (str, list)) and not ok
        elif op in ("gte", "lte"):
            ok = isinstance(actual, (int, float)) and not isinstance(actual, bool) and (actual >= v if op == "gte" else actual <= v)
        elif op == "len":
            ok = isinstance(actual, (str, list, dict)) and len(actual) == v
        else:  # any / none: a list holding (or not holding) a dict with these fields
            hit = isinstance(actual, list) and any(subset(x, v) for x in actual)
            ok = isinstance(actual, list) and (hit if op == "any" else not hit)
        if not ok:
            return False
    return True


def shown(v):
    return "missing" if v is MISSING else json.dumps(v)


def check_paths(label, obj, wants):
    return [f"{label} {path}: want {json.dumps(want)}, got {shown(dig(obj, path))}"
            for path, want in (wants or {}).items() if not matches(dig(obj, path), want)]


def check_logs(case, lines):
    reasons = []
    for want in case.get("expect_logs") or []:
        text, count = (want, None) if isinstance(want, str) else (want["text"], want.get("count"))
        n = sum(text in str(l.get("msg", "")) for l in lines)
        if (count is None and n == 0) or (count is not None and n != count):
            reasons.append(f"log {text!r}: want {'at least 1' if count is None else count}, saw {n}")
    for text in case.get("forbid_logs") or []:
        n = sum(text in str(l.get("msg", "")) for l in lines)
        if n:
            reasons.append(f"log {text!r} must not appear, saw {n}")
    return reasons


def evaluate(case, resp, state, lines, elapsed, seq):
    reasons = []
    result = resp.get("result") or {}
    if not resp.get("ok"):
        reasons.append(f"bridge error: {resp.get('error')}")
    elif isinstance(result, dict) and result.get("error") and not case.get("allow_tool_error"):
        reasons.append(f"tool error: {result['error']}")
    errs = [l for l in lines if l.get("seq", 0) > seq and l.get("type") in BAD_LOGS]
    if errs and not case.get("allow_error_logs"):
        reasons.append(f"{len(errs)} new error log line(s)")
    win = pick_window(state.get("windows") or [], case.get("expect_window"))
    if case.get("expect_window") and not win:
        reasons.append(f"no window matching {case['expect_window']!r}")
    reasons += check_paths("response", resp, case.get("expect"))
    reasons += check_paths("state", state, case.get("expect_state"))
    reasons += check_logs(case, lines)
    if "expect_elapsed" in case and not matches(round(elapsed, 1), case["expect_elapsed"]):
        reasons.append(f"elapsed: want {json.dumps(case['expect_elapsed'])} s, took {elapsed:.1f} s")
    return reasons, errs, win


def run_case(conn, case, i, out, a, names):
    tool, cmd = case.get("tool"), case.get("cmd")
    r = {"name": case["name"], "group": case.get("group", ""), "status": "PASS", "reasons": []}
    r.update({"tool": tool, "args": case.get("args") or {}} if tool else {"cmd": cmd, "cmd_args": case.get("cmd_args") or {}})
    if case.get("manual"):
        r.update(status="SKIP", reasons=["manual: do it on the headset (see the Manual section)"], manual=True)
        return r
    if case.get("paid") and not a.paid:
        r.update(status="SKIP", reasons=["paid: opens a billed voice session; run with --paid"])
        return r
    if tool and tool not in names:
        r.update(status="SKIP", reasons=["tool not registered in this build"])
        return r
    if conn is None:
        r.update(status="FAIL", reasons=["no connection to the bridge"])
        return r
    timeout_s = case.get("timeout_s", 30)
    within = case.get("within_s", 0)
    try:
        if case.get("requires_state"):
            before = request(conn, "state")
            missing = check_paths("needs", before, case["requires_state"])
            if missing:
                r.update(status="SKIP", reasons=missing)
                return r
        seq = request(conn, "logs").get("seq", 0)
        t0 = time.time()
        if tool:
            r["response"] = resp = call(conn, tool, r["args"], timeout_s)
        else:
            r["response"] = resp = request(conn, cmd, timeout=timeout_s + 10, timeout_s=timeout_s, **r["cmd_args"])
            if not resp.get("ok") and str(resp.get("error", "")).startswith("unknown cmd"):
                r.update(status="SKIP", reasons=[f"bridge command '{cmd}' not in this build"])
                return r
        time.sleep(case.get("wait_s", 1.5))
        while True:  # within_s: poll state and logs once a second until the case passes or time runs out
            elapsed = time.time() - t0
            state = request(conn, "state")
            lines = request(conn, "logs", since=seq).get("lines", [])
            reasons, errs, win = evaluate(case, resp, state, lines, elapsed, seq)
            if not reasons or time.time() - t0 >= within:
                break
            time.sleep(1)
        r["elapsed_s"] = round(elapsed, 1)
        steady = case.get("steady_s", 0)
        held_from = time.time()
        while not reasons and time.time() - held_from < steady:  # steady_s: it must keep passing this long
            time.sleep(1)
            state = request(conn, "state")
            lines = request(conn, "logs", since=seq).get("lines", [])
            reasons = check_paths("state", state, case.get("expect_state")) + check_logs(case, lines)
            if reasons:
                reasons = [f"{x} (after {time.time() - held_from:.0f} of {steady} s steady)" for x in reasons]
        r["state"] = state
        if not a.no_adb:
            name = f"{i:02d}-{''.join(c if c.isalnum() else '_' for c in case['name'])}.png"
            err = screenshot(os.path.join(out, name))
            r["screenshot"] = None if err else name
            if err:
                r["screenshot_error"] = err
    except (OSError, ValueError) as e:
        r.update(status="FAIL", reasons=[f"connection lost: {e}"], dropped=True)
        return r
    r["error_logs"] = errs
    r["window"] = win
    r["reasons"] = reasons
    if reasons:
        warn = case.get("warn_if")
        r["status"] = "WARN" if warn and not check_paths("warn_if", resp, warn) else "FAIL"
    return r


def select(cases, a):
    """--only / --group keep matching cases; setup and teardown cases always run (they hold the voice, seat the user)."""
    def keep(c):
        if c.get("setup") or c.get("teardown"):
            return True
        return (not a.only or a.only.lower() in c["name"].lower()) and (not a.group or a.group.lower() in str(c.get("group", "")).lower())
    return [c for c in cases if keep(c)]


def write_report(out, info, results, cases=()):
    count = {s: sum(r["status"] == s for r in results) for s in ("PASS", "FAIL", "SKIP", "WARN")}
    failed = [r for r in results if r["status"] == "FAIL"]
    shots = sum(bool(r.get("screenshot")) for r in results)
    lines = [
        "# questbridge sweep",
        "",
        f"- {len(results)} cases: {count['PASS']} pass, {count['FAIL']} fail, {count['SKIP']} skip"
        + (f", {count['WARN']} warn (reported, not failed)." if count["WARN"] else "."),
        f"- Build: {info.get('app')} {info.get('version')} on {info.get('platform')}, Unity {info.get('unity')}, scene {info.get('scene')}.",
        f"- Failed: {', '.join(r['name'] for r in failed) or 'none'}.",
        f"- Skipped: tools or bridge commands this build lacks, paid cases (--paid), manual cases.",
        f"- Screenshots: {shots}. Full data: results.json.",
        "",
        "| case | result | reason | window distance (m) | facing (deg) | screenshot |",
        "|---|---|---|---|---|---|",
    ]
    for r in results:
        w = r.get("window") or {}
        cells = [r["name"], r["status"], "; ".join(r["reasons"]), w.get("distance_m", ""), w.get("facing_deg", ""), r.get("screenshot") or ""]
        lines.append("| " + " | ".join(str(c).replace("|", "\\|").replace("\n", " ") for c in cells) + " |")
    manual = [c for c in cases if c.get("manual")]
    if manual:
        lines += ["", "## Manual cases (do these on the headset)", ""]
        for c in manual:
            lines.append(f"### {c['name']}")
            lines += [f"{n}. {step}" for n, step in enumerate(c.get("steps") or [], 1)]
            if c.get("look_for"):
                lines.append("Look for these log lines (`adb logcat -s Unity`, or the recording's log):")
                lines += [f"- `{t}`" for t in c["look_for"]]
            lines.append("")
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
        cases = select(json.load(f), a)
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
    count = write_report(out, info, results, cases)
    print(f"{count['PASS']} pass, {count['FAIL']} fail, {count['SKIP']} skip, {count['WARN']} warn. Report: {os.path.join(out, 'report.md')}")
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
    s = sub.add_parser("shot", help="save a headset screenshot")
    s.add_argument("out")
    s = sub.add_parser("sweep", help="unattended test run over a case file")
    s.add_argument("cases", nargs="?", default=os.path.join(ROOT, "tools", "questbridge_cases.json"))
    s.add_argument("--out", help="report folder (default unity/Logs/questbridge-MMDD-HHMM/)")
    s.add_argument("--only", help="run only cases whose name contains this (setup/teardown cases always run)")
    s.add_argument("--group", help="run only cases whose group contains this (setup/teardown cases always run)")
    s.add_argument("--paid", action="store_true", help="also run paid cases (they open a billed voice session)")
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
