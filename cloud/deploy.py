#!/usr/bin/env python3
"""Deploy cloud/ to Dev's Vercel project albie-cloud through the REST API.

A token scoped to this one project is enough (the Vercel CLI needs team access, which reaches every project).
Env: VERCEL_TOKEN (required); TARGET=production|preview (default production);
SYNC_KEYS=1 first upserts XAI_API_KEY / ANTHROPIC_API_KEY / ALBIE_APP_KEY from env (unset ones are skipped).
Run: VERCEL_TOKEN=... python3 cloud/deploy.py
"""
import base64
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

TEAM = "team_Tz2cQu7N1T6l7YBIp6By7ixT"
PROJECT = "prj_Kr8jVwYNgjDcf6KrxvHHUjwo1CMs"
HERE = Path(__file__).resolve().parent
KEYS = ("XAI_API_KEY", "ANTHROPIC_API_KEY", "ALBIE_APP_KEY")


def call(method: str, path: str, body: object = None) -> dict:
    sep = "&" if "?" in path else "?"
    req = urllib.request.Request(f"https://api.vercel.com{path}{sep}teamId={TEAM}", method=method,
                                 data=None if body is None else json.dumps(body).encode(),
                                 headers={"Authorization": f"Bearer {os.environ['VERCEL_TOKEN']}",
                                          "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        sys.exit(f"{method} {path.split('?')[0]} failed: {e.code} {e.read().decode()[:300]}")


if os.environ.get("SYNC_KEYS") == "1":
    for key in KEYS:
        value = os.environ.get(key, "")
        if not value:
            print(f"{key}: not set here, Vercel keeps its value")
            continue
        call("POST", f"/v10/projects/{PROJECT}/env?upsert=true",
             {"key": key, "value": value, "type": "sensitive", "target": ["production"]})
        print(f"{key}: updated")

# Tracked files only, so .env / .vercel never ship; this script stays out too (Vercel would serve it).
names = [n for n in subprocess.run(["git", "ls-files"], cwd=HERE, check=True, capture_output=True, text=True)
         .stdout.split() if n not in ("deploy.py", ".gitignore")]
files = [{"file": n, "data": base64.b64encode((HERE / n).read_bytes()).decode(), "encoding": "base64"} for n in names]
body: dict = {"name": "albie-cloud", "project": PROJECT, "files": files}
if os.environ.get("TARGET", "production") == "production":
    body["target"] = "production"
dep = call("POST", "/v13/deployments", body)
print(f"Deploying {len(files)} files: https://{dep['url']}")

for _ in range(60):  # 5 min
    state = call("GET", f"/v13/deployments/{dep['id']}").get("readyState")
    if state == "READY":
        print("READY")
        sys.exit(0)
    if state in ("ERROR", "CANCELED"):
        sys.exit(f"Deployment {state}: https://vercel.com/builtbyd3vs-projects/albie-cloud")
    time.sleep(5)
sys.exit("Still building after 5 min: check https://vercel.com/builtbyd3vs-projects/albie-cloud")
