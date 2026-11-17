#!/usr/bin/env bash
# One-command deploy of Albie's cloud to a Hugging Face Docker Space (free cpu-basic). See the create line for sleep.
#
# Run from anywhere:  cloud-py/deploy.sh      Deploys the committed HEAD, not the working tree.
# Needs (once): brew install hf, then hf auth login (a write token from huggingface.co/settings/tokens).
set -euo pipefail
cd "$(dirname "$0")/.."
KEY_FILE=unity/Assets/_Forge/Core/Resources/AlbieAppKey.txt
# CI (.github/workflows/deploy-space.yml) passes ALBIE_APP_KEY and HF_OWNER as env; a laptop uses the key file and the login.
APP_KEY=${ALBIE_APP_KEY:-$([ -s "$KEY_FILE" ] && tr -d '\r\n' < "$KEY_FILE")}
[ -n "$APP_KEY" ] || { echo "missing ALBIE_APP_KEY env or $KEY_FILE (the app key the headset sends)"; exit 1; }
HF_USER=${HF_OWNER:-$(hf auth whoami | head -1 | sed 's/.*[= ]//')}  # first line is user=<name>
SPACE="$HF_USER/albie-cloud"
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT

# Public, so the headset reaches it without an HF token; every request still needs the app key.
# Free cpu-basic; a Docker Space on it needs PRO on the owner account. It sleeps after 48 h without requests, and
# .github/workflows/space-keepalive.yml pings it so it stays awake.
hf repos create "$SPACE" --type space --sdk docker --flavor cpu-basic --no-private --exist-ok \
  || echo "create skipped (a token scoped to this Space can't create repos; fine when it exists)"
# The key goes in on stdin, never on the command line or in the log.
printf 'ALBIE_APP_KEY=%s\n' "$APP_KEY" | hf spaces secrets add "$SPACE" --secrets-file -
# Optional: the demo GitHub account's token, for the code panel and ml_publish (a project's pull request).
# CI passes ALBIE_GITHUB_TOKEN; unset leaves whatever the Space already has.
if [ -n "${ALBIE_GITHUB_TOKEN:-}" ]; then
  printf 'GITHUB_TOKEN=%s\n' "$ALBIE_GITHUB_TOKEN" | hf spaces secrets add "$SPACE" --secrets-file -
fi
# Optional: a Claude API key for setting up the GitHub repos Albie pulls (ml_github) without the Vercel proxy.
if [ -n "${ALBIE_ANTHROPIC_API_KEY:-}" ]; then
  printf 'ANTHROPIC_API_KEY=%s\n' "$ALBIE_ANTHROPIC_API_KEY" | hf spaces secrets add "$SPACE" --secrets-file -
fi

git archive HEAD hub/cad hub/ml hub/code_browser hub/events.py hub/agent/events.py forge-sdk/forge ml-examples/text-classification cloud-py \
  | tar -x -C "$STAGE"
cp cloud-py/Dockerfile "$STAGE/Dockerfile"
cat > "$STAGE/README.md" <<'EOF'
---
title: Albie Cloud
sdk: docker
app_port: 8080
pinned: false
---
CAD, small ML training and the GitHub code browser for the Albie headset app. Every request needs the app key.
EOF
hf upload "$SPACE" "$STAGE" . --repo-type space --delete "*" --commit-message "Deploy $(git rev-parse --short HEAD)"

URL="https://$(echo "$HF_USER" | tr "A-Z_." "a-z--")-albie-cloud.hf.space"  # HF lower-cases the host
echo "Uploaded. The Space builds for about 10 min: https://huggingface.co/spaces/$SPACE"
echo "Then check (want 200): curl -s -o /dev/null -w '%{http_code}' -H \"x-albie-key: \$(cat $KEY_FILE)\" $URL/health"
echo "Headset: set PlayerPrefs Albie.CloudUrl to $URL. Vercel: set env CLOUD_URL to $URL."
