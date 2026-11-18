# Albie cloud: CAD + small ML training for headsets with no laptop. Build context: the repo root
# layout (cloud-py/deploy.sh copies these paths into a Hugging Face Docker Space with this file at its root).
FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1
# ponytail: GL/glib/Xrender are what OCP wheels usually want on slim images; drop them if the build proves otherwise.
# git: ml_github clones the ML repos Albie pulls up.
RUN apt-get update && apt-get install -y --no-install-recommends libgl1 libglib2.0-0 libxrender1 git \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY cloud-py/requirements.txt cloud-py/requirements.txt
RUN pip install torch==2.14.1 torchvision==0.29.1 --index-url https://download.pytorch.org/whl/cpu \
    && pip install -r cloud-py/requirements.txt

COPY hub/cad hub/cad
COPY hub/ml hub/ml
COPY hub/code_browser hub/code_browser
COPY hub/models hub/models
COPY hub/research hub/research
COPY hub/events.py hub/events.py
# The crew's builds (cloud-py/cloud_builds.py): only the hub agent files a build imports, not its voice or Slack.
COPY hub/agent/build.py hub/agent/build_review.py hub/agent/bus.py hub/agent/config.py hub/agent/crew.py hub/agent/demo_runner.py hub/agent/events.py hub/agent/fake.py hub/agent/runner.py hub/agent/tools.py hub/agent/turn.py hub/agent/workspace.py hub/agent/
COPY forge-sdk/forge forge-sdk/forge
COPY ml-examples/text-classification ml-examples/text-classification
COPY cloud-py cloud-py
# The crew runs on the Claude Agent SDK, whose wheel carries its own Claude Code CLI (no Node needed). Say so in the
# build log if this platform's wheel came without one: builds then answer that the coding agent is missing.
RUN python -c "import claude_agent_sdk, pathlib; cli = pathlib.Path(claude_agent_sdk.__file__).parent / '_bundled' / 'claude'; \
print('claude cli bundled:', cli.is_file())" || echo "claude_agent_sdk missing; builds will say so"

# ml_new ("start an RNN project"): the datasets it reaches for, sampled once here (~25 MB; it reads them from this
# cache first), so the demo's new projects don't wait on, or get rate-limited by, Hugging Face. A failure only
# means those projects download live.
ENV FORGE_HF_CACHE=/app/hf-cache HF_HOME=/app/hf-models
RUN PYTHONPATH=/app/hub python -m ml.new_project warm || echo "ml_new cache warm failed; new projects download live"
# ml_new fine-tuning ("fine-tune distilbert on movie reviews"): the default encoder's tokenizer and weights, once,
# so the first demo run doesn't wait on the Hub. A failure only means that run downloads them.
RUN python -c "from transformers import AutoTokenizer, AutoModel; m='microsoft/xtremedistil-l6-h256-uncased'; \
AutoTokenizer.from_pretrained(m); AutoModel.from_pretrained(m)" || echo "pretrained model warm failed; it downloads live"

# The shared AG News subset (~30 MB) and a first 4-epoch run, so "open my ML project" has a score at once.
RUN cd ml-examples/text-classification \
    && PYTHONPATH=/app/forge-sdk python data.py \
    && FORGE_OFFLINE=1 PYTHONPATH=/app/forge-sdk python train.py --epochs 4 > /dev/null \
    && rm -rf checkpoints .forge/__pycache__ && mkdir -p /data \
    && chmod -R a+w /app /data  # HF Spaces run the container as uid 1000, not root

# ponytail: HOME=/tmp gives library caches (torch, matplotlib) a writable home under uid 1000.
ENV PYTHONPATH=/app/cloud-py/stubs:/app/cloud-py:/app/hub CLOUD_DATA_DIR=/data PORT=8080 HOME=/tmp \
    HF_HOME=/app/hf-models \
    CLAUDE_PROXY_URL=https://albie-cloud.vercel.app/api/claude \
    FORGE_ML_PIP=1 FORGE_MAX_TRAIN=4000 FORGE_MAX_VAL=500 FORGE_LLM_MODEL=claude-sonnet-5-5
# ml_github: a cloned repo's missing packages go into its own venv (FORGE_ML_PIP, hub/ml/repo_env.py), and its
# patched training script caps the data at FORGE_MAX_TRAIN / FORGE_MAX_VAL samples so an epoch takes seconds on
# this CPU. FORGE_LLM_MODEL: the model setup uses when the Space has its own ANTHROPIC_API_KEY secret.
EXPOSE 8080
CMD ["python", "-m", "uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8080"]
