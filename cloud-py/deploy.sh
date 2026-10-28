#!/usr/bin/env bash
# One-command deploy of Albie's cloud to a Hugging Face Docker Space (CPU upgrade, $0.03/h while awake). See the create line for sleep.
#
# Run from anywhere:  cloud-py/deploy.sh      Deploys the committed HEAD, not the working tree.
# Needs (once): brew install hf, then hf auth login (a write token from huggingface.co/settings/tokens).
set -euo pipefail
cd "$(dirname "$0")/.."
KEY_FILE=unity/Assets/_Forge/Core/Resources/AlbieAppKey.txt
[ -s "$KEY_FILE" ] || { echo "missing $KEY_FILE (the app key the headset sends)"; exit 1; }
HF_USER=$(hf auth whoami | head -1 | sed 's/.*[= ]//')  # first line is user=<name>
SPACE="$HF_USER/albie-cloud"
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT

# Public, so the headset reaches it without an HF token; every request still needs the app key.
# cpu-upgrade ($0.03/h, from account credits) because free cpu-basic Docker Spaces need PRO; billed only while awake,
# and it sleeps after 1 h with no requests (the first request after that waits for a cold start).
hf repos create "$SPACE" --type space --sdk docker --flavor cpu-upgrade --sleep-time 3600 --no-private --exist-ok
# The key goes in on stdin, never on the command line or in the log.
printf 'ALBIE_APP_KEY=%s\n' "$(tr -d '\r\n' < "$KEY_FILE")" | hf spaces secrets add "$SPACE" --secrets-file -

git archive HEAD hub/cad hub/ml hub/events.py hub/agent/events.py forge-sdk/forge ml-examples/text-classification cloud-py \
  | tar -x -C "$STAGE"
cp cloud-py/Dockerfile "$STAGE/Dockerfile"
cat > "$STAGE/README.md" <<'EOF'
---
title: Albie Cloud
sdk: docker
app_port: 8080
pinned: false
---
CAD and small ML training for the Albie headset app. Every request needs the app key.
EOF
hf upload "$SPACE" "$STAGE" . --repo-type space --delete "*" --commit-message "Deploy $(git rev-parse --short HEAD)"

URL="https://$(echo "$HF_USER" | tr "A-Z_." "a-z--")-albie-cloud.hf.space"  # HF lower-cases the host
echo "Uploaded. The Space builds for about 10 min: https://huggingface.co/spaces/$SPACE"
echo "Then check (want 200): curl -s -o /dev/null -w '%{http_code}' -H \"x-albie-key: \$(cat $KEY_FILE)\" $URL/health"
echo "Headset: set PlayerPrefs Albie.CloudUrl to $URL. Vercel: set env CLOUD_URL to $URL."
