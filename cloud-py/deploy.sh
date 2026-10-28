#!/usr/bin/env bash
# One-command deploy of Albie's cloud to Fly.io (costs money: $13.39/month always on; see fly.toml).
# Run from anywhere:  cloud-py/deploy.sh          Needs: fly auth login, a payment method on the Fly org.
set -euo pipefail
cd "$(dirname "$0")/.."
APP=albie-cad
KEY_FILE=unity/Assets/_Forge/Core/Resources/AlbieAppKey.txt
[ -s "$KEY_FILE" ] || { echo "missing $KEY_FILE (the app key the headset sends)"; exit 1; }

fly apps list --json | grep -q "\"$APP\"" || fly apps create "$APP"
# The key goes in on stdin, never on the command line or in the log.
printf 'ALBIE_APP_KEY=%s\n' "$(tr -d '\r\n' < "$KEY_FILE")" | fly secrets import --app "$APP" --stage
fly deploy . --config cloud-py/fly.toml --dockerfile cloud-py/Dockerfile --remote-only --ha=false
fly scale count 1 --app "$APP" --yes

URL="https://$APP.fly.dev"
echo "no key (want 401): $(curl -s -o /dev/null -w '%{http_code}' "$URL/health")"
K="$(tr -d '\r\n' < "$KEY_FILE")"
for i in 1 2; do
  curl -s -o /dev/null -w "regen $i: %{http_code} %{time_total}s\n" -X POST -H "x-albie-key: $K" \
    -H "x-albie-device: deploycheck01" -H 'content-type: application/json' -d "{\"fin_span\": $((30 + i))}" "$URL/cad/params"
done
echo "Headset: set PlayerPrefs Albie.CloudUrl to $URL (or keep the built-in default)."
