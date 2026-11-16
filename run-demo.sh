#!/bin/sh
# Daily loop: stop the old hub, start the hub, open the sandbox ML project, wire up the Quest.
#   sh run-demo.sh                 # hub on :8000, log in /tmp/forge-hub.log
#   PORT=8100 NO_ADB=1 sh run-demo.sh   # a second hub for testing, no headset
# Python: hub/.venv (from dev-setup.sh) when it exists, else $HUB_PYTHON, else the active env's python3
# (e.g. `conda activate albie-vr` first).
set -eu
repo=$(cd "$(dirname "$0")" && pwd)
port=${PORT:-8000}
log=${HUB_LOG:-/tmp/forge-hub.log}
sandbox=${ALBIE_SANDBOX:-$HOME/Documents/GitHub/albie-sandbox}
adb=${ADB:-/Applications/Unity/Hub/Editor/6000.6.7f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb}
[ -x "$adb" ] || adb=$(command -v adb || true)

old=$(lsof -ti "tcp:$port" -sTCP:LISTEN || true)
[ -n "$old" ] && { echo "stopping the hub on :$port ($old)"; kill $old; sleep 2; }

cd "$repo/hub"
if [ -x .venv/bin/python ]; then py=.venv/bin/python; else py=${HUB_PYTHON:-$(command -v python3 || true)}; fi
"$py" -c 'import uvicorn, fastapi' 2>/dev/null || {
  echo "MISSING: the hub's packages in ${py:-python3}. Run dev-setup.sh, activate the env that has them, or set HUB_PYTHON."; exit 1; }
# Inherited CLAUDECODE / ANTHROPIC_BASE_URL (a Claude Code shell) make the agent SDK's CLI hang (hub/README.md).
nohup env -u CLAUDECODE -u ANTHROPIC_BASE_URL "$py" -m uvicorn main:app --host 0.0.0.0 --port "$port" >> "$log" 2>&1 &
echo "hub starting on :$port (log: $log)"
i=0
until curl -sf "localhost:$port/health" >/dev/null; do
  i=$((i + 1)); [ $i -gt 60 ] && { echo "no /health after 60 s; see $log"; exit 1; }; sleep 1
done
echo "hub ok"

curl -sf -X POST "localhost:$port/ml/project" -H 'content-type: application/json' \
  -d "{\"path\": \"$sandbox/ml\"}" >/dev/null && echo "ML desk: $sandbox/ml" || echo "WARN: /ml/project failed (see $log)"

[ "${NO_ADB:-0}" = "1" ] && exit 0
if [ -z "$adb" ] || ! "$adb" devices | grep -q "	device$"; then echo "no headset on USB: plug it in, then rerun"; exit 0; fi
"$adb" reverse tcp:8000 tcp:8000
"$adb" shell monkey -p com.albie.vr -c android.intent.category.LAUNCHER 1 >/dev/null
echo "Quest: port reversed, app launched"
