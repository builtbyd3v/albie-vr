#!/usr/bin/env bash
# One-command deploy of Albie's cloud to a free Hugging Face Docker Space (CPU basic, $0). A free Space sleeps after
# 48 h with no traffic; the daily Vercel cron (cloud/vercel.json -> /api/ping-space) keeps it awake.
# Run from anywhere:  cloud-py/deploy.sh <hf-user>      Deploys the committed HEAD, not the working tree.
# Needs (once): the Space <hf-user>/albie-cloud created on huggingface.co (SDK Docker, blank), the Space secret
# ALBIE_APP_KEY (same value as unity/Assets/_Forge/Core/Resources/AlbieAppKey.txt), and an HF write token as the git password.
set -euo pipefail
cd "$(dirname "$0")/.."
HF_USER=${1:?usage: cloud-py/deploy.sh <hf-user>}
SPACE=albie-cloud
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT

git clone --quiet "https://huggingface.co/spaces/$HF_USER/$SPACE" "$STAGE"
git -C "$STAGE" rm -rq --ignore-unmatch .
git archive HEAD hub/cad hub/ml hub/events.py hub/agent/events.py forge-sdk/forge ml-examples/text-classification cloud-py \
  | tar -x -C "$STAGE"
cp cloud-py/Dockerfile "$STAGE/Dockerfile"
# HF rejects binary files outside LFS.
printf '*.glb filter=lfs diff=lfs merge=lfs -text\n*.npz filter=lfs diff=lfs merge=lfs -text\n' > "$STAGE/.gitattributes"
cat > "$STAGE/README.md" <<'EOF'
---
title: Albie Cloud
sdk: docker
app_port: 8080
pinned: false
---
CAD and small ML training for the Albie headset app. Every request needs the app key.
EOF

cd "$STAGE"
git lfs install --local > /dev/null
git add -A
git commit -qm "Deploy $(git -C "$OLDPWD" rev-parse --short HEAD)" || { echo "nothing changed"; exit 0; }
git push -q origin HEAD:main

URL="https://$HF_USER-$SPACE.hf.space"
echo "Pushed. The Space builds for about 10 min: https://huggingface.co/spaces/$HF_USER/$SPACE"
echo "Then check (want 200): curl -s -o /dev/null -w '%{http_code}' -H \"x-albie-key: \$(cat unity/Assets/_Forge/Core/Resources/AlbieAppKey.txt)\" $URL/health"
echo "Headset: set PlayerPrefs Albie.CloudUrl to $URL. Vercel: set env CLOUD_URL to $URL."
