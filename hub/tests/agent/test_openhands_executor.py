"""The optional Docker bridge has no GitHub credentials, tolerates SDK output, and never falls back to host execution."""
import asyncio
import json

import pytest

from agent import openhands_executor as executor


def test_optional_executor_requires_its_own_environment(monkeypatch):
    monkeypatch.delenv("OPENHANDS_PYTHON", raising=False)
    with pytest.raises(RuntimeError, match="OPENHANDS_PYTHON"):
        asyncio.run(executor.execute({"kind": "check"}))


def test_json_bridge_ignores_non_protocol_output_and_drops_hub_credentials(monkeypatch):
    monkeypatch.setenv("OPENHANDS_PYTHON", "/optional/python")
    monkeypatch.setenv("GITHUB_TOKEN", "must-not-leave-hub")
    monkeypatch.setenv("XAI_API_KEY", "must-not-leave-hub")
    seen = {}

    class Input:
        def write(self, data):
            seen["input"] = json.loads(data)

        async def drain(self):
            pass

        def close(self):
            pass

    class Process:
        returncode = 0
        stdin = Input()

        def __init__(self):
            self.stdout = asyncio.StreamReader()
            self.stdout.feed_data(b'OpenHands SDK\ntrue\n[]\n{"type":"result","exit_code":0,"output":"ok"}\n')
            self.stdout.feed_eof()

        async def wait(self):
            return 0

    async def create(*args, **kwargs):
        seen["argv"], seen["env"] = args, kwargs["env"]
        return Process()

    monkeypatch.setattr(executor.asyncio, "create_subprocess_exec", create)
    result = asyncio.run(executor.execute({"kind": "check", "argv": ["python", "--version"]}))
    assert result["output"] == "ok"
    assert "GITHUB_TOKEN" not in seen["env"] and "XAI_API_KEY" not in seen["env"]
    assert seen["argv"][0] == "/optional/python"
    assert seen["input"]["kind"] == "check"
