"""Defaults for every test suite under hub/ (pytest.ini: cad/tests, tests, ml/tests)."""
import os

# A developer's hub/.env may say VOICE_PROVIDER=grok; tests must not open a Grok session (load_env never overrides).
os.environ.setdefault("VOICE_PROVIDER", "none")
