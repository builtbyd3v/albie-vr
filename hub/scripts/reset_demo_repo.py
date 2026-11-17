"""Put the demo repo back to "one open pull request", after a rehearsal merged it.

    GITHUB_TOKEN=<the demo account's token> python scripts/reset_demo_repo.py

It moves rc-truck's main back to its first commit, points the corner-slowdown branch at the change again, and
opens the pull request if none is open (a merged one can't be reopened, so the new one gets the next number).
Nothing happens if the pull request is still open. It only ever touches the one repo named below.
"""
from __future__ import annotations

import json
import os
import sys
from typing import Any
from urllib.error import HTTPError
from urllib.request import Request, urlopen

REPO = "albieuser/rc-truck"
BASE, BRANCH = "main", "corner-slowdown"
BASE_SHA = "ae12ee47f92b7fad6c8d5177ad593c7c2caa4d8b"    # RC truck controller, slalom simulator and speed sweep
BRANCH_SHA = "5bc8e8c0bba0cc69e3c7ceadec276c40fcc4563f"  # Slow down in hard turns
TITLE = "Slow down in hard turns"
BODY = """The truck slid wide of the slalom gates at higher speeds, because it held full throttle through every turn.

- The controller now eases off in proportion to how hard the wheels are turned.
- New tunable `corner_slowdown` (0 to 1) in `controller/config.json`, set to 0.35. At 0 the controller behaves as before.
- One new test for the hard-turn case.

The speed sweep still clears every gate; laps are slower at the same `max_speed`, which is the trade for staying on the course.
"""


def api(method: str, path: str, body: dict[str, Any] | None = None) -> Any:
    request = Request(
        f"https://api.github.com/repos/{REPO}{path}", method=method,
        data=None if body is None else json.dumps(body).encode(),
        headers={"Authorization": f"Bearer {os.environ['GITHUB_TOKEN']}", "Accept": "application/vnd.github+json",
                 "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "albie-hub/1"})
    with urlopen(request, timeout=20) as response:
        return json.loads(response.read() or b"null")


def point(branch: str, sha: str) -> None:
    try:
        api("PATCH", f"/git/refs/heads/{branch}", {"sha": sha, "force": True})
    except HTTPError as e:
        if e.code not in (404, 422):  # the branch is gone (deleted after the merge): make it again
            raise
        api("POST", "/git/refs", {"ref": f"refs/heads/{branch}", "sha": sha})


def main() -> int:
    if not os.environ.get("GITHUB_TOKEN"):
        print("Set GITHUB_TOKEN to the demo account's token.", file=sys.stderr)
        return 2
    owner = REPO.split("/")[0]
    open_prs = api("GET", f"/pulls?state=open&head={owner}:{BRANCH}&base={BASE}")
    if open_prs:
        print(f"Nothing to do: pull request #{open_prs[0]['number']} is still open.")
        return 0
    point(BASE, BASE_SHA)
    point(BRANCH, BRANCH_SHA)
    pr = api("POST", "/pulls", {"title": TITLE, "body": BODY, "head": BRANCH, "base": BASE})
    print(f"Reset {REPO}: {BASE} is back at its first commit and pull request #{pr['number']} is open.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
