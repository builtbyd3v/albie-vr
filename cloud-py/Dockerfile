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
COPY hub/events.py hub/events.py
COPY hub/agent/events.py hub/agent/events.py
COPY forge-sdk/forge forge-sdk/forge
COPY ml-examples/text-classification ml-examples/text-classification
COPY cloud-py cloud-py

# ml_new ("start an RNN project"): the datasets it reaches for, sampled once here (~25 MB; it reads them from this
# cache first), so the demo's new projects don't wait on, or get rate-limited by, Hugging Face. A failure only
# means those projects download live.
ENV FORGE_HF_CACHE=/app/hf-cache
RUN PYTHONPATH=/app/hub python -m ml.new_project warm || echo "ml_new cache warm failed; new projects download live"

# The shared AG News subset (~30 MB) and a first 4-epoch run, so "open my ML project" has a score at once.
RUN cd ml-examples/text-classification \
    && PYTHONPATH=/app/forge-sdk python data.py \
    && FORGE_OFFLINE=1 PYTHONPATH=/app/forge-sdk python train.py --epochs 4 > /dev/null \
    && rm -rf checkpoints .forge/__pycache__ && mkdir -p /data \
    && chmod -R a+w /app /data  # HF Spaces run the container as uid 1000, not root

# ponytail: HOME=/tmp gives library caches (torch, matplotlib) a writable home under uid 1000.
ENV PYTHONPATH=/app/cloud-py/stubs:/app/cloud-py:/app/hub CLOUD_DATA_DIR=/data PORT=8080 HOME=/tmp \
    CLAUDE_PROXY_URL=https://albie-cloud.vercel.app/api/claude \
    FORGE_ML_PIP=1 FORGE_MAX_TRAIN=4000 FORGE_MAX_VAL=500 FORGE_LLM_MODEL=claude-sonnet-5-5
# ml_github: a cloned repo's missing packages go into its own venv (FORGE_ML_PIP, hub/ml/repo_env.py), and its
# patched training script caps the data at FORGE_MAX_TRAIN / FORGE_MAX_VAL samples so an epoch takes seconds on
# this CPU. FORGE_LLM_MODEL: the model setup uses when the Space has its own ANTHROPIC_API_KEY secret.
EXPOSE 8080
CMD ["python", "-m", "uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8080"]
