#!/bin/sh
# One-time setup of everything that runs on the laptop (macOS): hub, ML example env and Albie's sandbox.
# Safe to rerun: each step skips what is already there. docs/SETUP.md has the full guide.
#   sh dev-setup.sh [path/to/forge-hub.env]
set -eu
repo=$(cd "$(dirname "$0")" && pwd)
sandbox=${ALBIE_SANDBOX:-$HOME/Documents/GitHub/albie-sandbox}
py=${PYTHON:-}
if [ -z "$py" ]; then  # the newest python3.1x on PATH; macOS's own python3 is often 3.9
  for v in python3.13 python3.12 python3.11 python3; do command -v $v >/dev/null 2>&1 && { py=$v; break; }; done
fi
step() { printf '\n== %s\n' "$1"; }
need() { command -v "$1" >/dev/null 2>&1 || { echo "MISSING: $1. $2"; exit 1; }; }

step "Tools"
need "$py" "brew install python@3.11"
"$py" -c 'import sys; sys.exit(sys.version_info < (3, 11))' || { echo "MISSING: Python 3.11+ ($($py --version)). brew install python@3.11, then PYTHON=python3.11 sh dev-setup.sh"; exit 1; }
echo "ok: $($py --version)"

step "hub/.env"
if [ -f "$repo/hub/.env" ]; then echo "ok: hub/.env exists (not overwritten)"
elif [ -n "${1:-}" ]; then cp "$1" "$repo/hub/.env" && echo "ok: copied $1 to hub/.env"
else echo "MISSING: hub/.env. Ask a teammate for the env file, then: sh dev-setup.sh <path>"; exit 1; fi

step "Hub Python env (hub/.venv)"
[ -x "$repo/hub/.venv/bin/python" ] || "$py" -m venv "$repo/hub/.venv"
"$repo/hub/.venv/bin/pip" install -q -r "$repo/hub/requirements-dev.txt" -r "$repo/hub/ml/requirements.txt"
echo "ok: hub deps"

step "ML example env (ml-examples/cifar10/.venv) + CIFAR-10 data"
ml="$repo/ml-examples/cifar10"
[ -x "$ml/.venv/bin/python" ] || "$py" -m venv "$ml/.venv"
"$ml/.venv/bin/pip" install -q -r "$ml/requirements.txt" -e "$repo/forge-sdk"
if [ -d "$ml/data" ]; then echo "ok: data/ exists"; else (cd "$ml" && .venv/bin/python data.py); fi

step "Albie's sandbox ($sandbox)"
if [ -f "$sandbox/.albie-sandbox" ]; then echo "ok: exists (rebuild: sh sandbox/setup.sh --reset)"
else ALBIE_SANDBOX="$sandbox" sh "$repo/sandbox/setup.sh"; fi
[ -e "$sandbox/ml/.venv" ] || { ln -s "$ml/.venv" "$sandbox/ml/.venv" && echo "ok: linked $sandbox/ml/.venv"; }

step "Paths in hub/.env"
# ponytail: a plain line reader, not a dotenv parser; only expands ${HOME} and ~, like the shared file uses.
bad=0
for key in FORGE_ML_VENV FORGE_ML_REPO FORGE_CAD_SCRIPT; do
  val=$(sed -n "s/^$key=//p" "$repo/hub/.env" | tail -1 | sed "s#\${HOME}#$HOME#g; s#^~#$HOME#; s/^\"//; s/\"\$//")
  if [ -z "$val" ]; then echo "unset: $key"; continue; fi
  [ -e "$val" ] && continue
  bad=1; echo "MISSING $key -> $val"
done
[ $bad -eq 0 ] && echo "ok: every path exists" || cat <<FIX
Fix: set these in hub/.env:
  FORGE_ML_VENV=$ml/.venv
  FORGE_ML_REPO=$sandbox/ml
  FORGE_CAD_SCRIPT=$sandbox/cad/rocket.py
FIX

cat <<'NEXT'

== Done. Your manual steps (docs/SETUP.md, HUMAN steps):
1. Optional: your own SLACK_USER_TOKEN, iMessage (docs/SETUP.md steps 7 and 8).
2. Unity 6000.6.7f1 + Android Build Support, build the APK (docs/SETUP.md step 9).
3. Daily: sh run-demo.sh
NEXT
