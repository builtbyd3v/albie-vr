"""Local no-key workflow demo. Run: uvicorn agent.demo_server:app --app-dir hub --port 8003."""
import os
from pathlib import Path

# These overrides are process-local; hub/.env and the normal hub are left unchanged.
os.environ.update(
    FAKE_AGENT="1", BUILD_DEMO="1", BUILD_EXECUTOR="claude", BUILD_REPO="", BUILD_REMOTE="",
    BUILD_DIR=str(Path(__file__).resolve().parents[1] / ".data" / "orchestration-demo"),
    VOICE_PROVIDER="none", STT_PROVIDER="none", IMESSAGE_ENABLED="0",
    ANTHROPIC_API_KEY="", GITHUB_TOKEN="", GH_TOKEN="", XAI_API_KEY="",
    SLACK_BOT_TOKEN="", SLACK_APP_TOKEN="", SLACK_USER_TOKEN="", TYPESAFE_API_KEY="",
)

from main import app  # noqa: E402,F401
