"""Download this project's data sample from Hugging Face again (the same rows Albie trained on):

    python download_data.py

project.json says which dataset, config, columns and sample; hf_data.py is the reader Albie used (it needs pyarrow
for the fast path, else it falls back to Hugging Face's rows API).
"""

from __future__ import annotations

import json
from pathlib import Path

import hf_data

HERE = Path(__file__).resolve().parent

if __name__ == "__main__":
    project = json.loads((HERE / "project.json").read_text())
    layout = hf_data.Layout(**project["layout"])
    sample = project["sample"]
    got = hf_data.fetch(layout, HERE, sample["train"], sample["val"], print, seed=sample["seed"], cache=False)
    print(f"{project['dataset']}: {got['train']} training and {got['val']} validation rows in data/")
