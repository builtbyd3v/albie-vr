"""Stand-in for hub/agent in the cloud container: only agent.events (the real file) and a silent agent.speech.

hub/agent/__init__ pulls in the voice, Slack and Claude runner; the CAD and ML tools need none of that. This
package's path lists this folder first (speech.py below), then hub/agent (events.py).
"""
from pathlib import Path

__path__ = [str(Path(__file__).resolve().parent), str(Path(__file__).resolve().parents[3] / "hub" / "agent")]
