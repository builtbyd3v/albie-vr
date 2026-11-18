# Disclosure

- The project started at Gator Hack V (Fri Oct 23, 5:00 PM to Sun Oct 25, 5:00 PM ET). The first commit is Fri Oct 23, 6:20 PM ET. Work continued through Nov 18, 2026 for the Meta VR Start Developer Competition; the git history shows what was built when.
- The idea and planning doc (PROJECT_CONTEXT.md) were written by the team. No pre-written project code was used.
- AI tools assisted with coding. They are listed below, as the MLH and competition rules require.
- Third-party libraries, APIs, and assets are listed below. Each is used under its own license.

## AI tools
- Claude Code (Anthropic; Claude Opus 5.5 and Claude Fable 5.1): used by Dev, Hudson, Adrian, Satyabrata and Nandani for code generation, debugging, tests and docs. Many commits carry a Claude Co-Authored-By trailer.
- Claude API via the Anthropic Python SDK (anthropic, MIT), at runtime: the ML desk's setup (hub/ml/setup/) and Albie's "what can I do better?" proposals (hub/ml/improve.py)
- Claude Agent SDK (claude-agent-sdk, MIT), at runtime: Albie's coding and Q&A runner and the crew members (hub/agent/runner.py, crew.py)
- Grok Voice Agent API (xAI, grok-voice-3.0), at runtime: Albie's speech-to-speech voice and transcription (hub/agent/voice.py)
- Codex (OpenAI): coding assistant for code generation, debugging and review, used during Gator Hack V
- Cursor: coding assistant used by Satyabrata; 14 commits carry a Cursor Co-authored-by trailer
- OpenHands SDK (openhands-sdk, openhands-tools, openhands-workspace, MIT), optional and not installed by default: an alternative coding-agent executor in a separate Python 3.12+ environment (hub/requirements-openhands.txt)

## Engines, SDKs, and libraries
- Unity 6 (6000.6.7f1), URP
- Meta XR All-in-One SDK 207, OpenXR, Meta XR Simulator
- glTFast (runtime GLB loading). com.unity.webrtc is still in the package manifest, but the screen streaming that used it was removed on 2026-11-06
- Python: FastAPI, build123d (CAD), Pillow, matplotlib (CIFAR-10 example extra chart)
- Python hub (hub/requirements.txt): uvicorn (BSD-3), pydantic (MIT), watchfiles (MIT), RocketPy (MIT, flight sim), PyYAML (MIT), python-dotenv (BSD-3), websockets (BSD-3), aiohttp (Apache-2.0), certifi (MPL-2.0); tests: pytest (MIT), httpx (BSD-3)
- Slack: Bolt for Python (slack-bolt, MIT) and the Python Slack SDK (slack-sdk, MIT); the Slack CLI for app setup
- Unity CLI and com.unity.pipeline (Unity, experimental), used to drive the Editor and build the APK during development; its agent skill is tracked in .claude/skills/unity-cli/
- forge-sdk (forge-sdk/pyproject.toml): hatchling (MIT) build backend, httpx (BSD-3)
- Unity packages: Meta Interaction SDK (part of Meta XR All-in-One, Meta license), Input System, Newtonsoft Json (com.unity.nuget.newtonsoft-json), TextMesh Pro; plus Unity's default editor packages (collab-proxy, IDE support, uGUI, XR Management)
- cad-vision (github.com/BAAA-Hacks/cad-vision, MIT), copied unchanged into unity/Assets/_Forge/CAD/ThirdParty/CadVision/
- Neural Net Lab: our own C# re-implementation of the TensorFlow Playground idea (datasets, features, init); no Playground code is copied
- Blender: Albie's opening mouth and our own animation clips (art/gator/, scripts build_albie_*.py and export_albie_fbx.py)
- Cloud service on the Hugging Face Space (cloud-py/requirements.txt, cloud-py/Dockerfile): the hub's libraries above plus NumPy (BSD-3), pyarrow (Apache-2.0, reads Hugging Face Parquet exports), transformers (Apache-2.0, fine-tunes a pretrained encoder), pandas (BSD-3), scikit-learn (BSD-3), tqdm (MPL-2.0 and MIT), PyTorch and torchvision (BSD-3, CPU builds)
- Cloud functions on Vercel (cloud/, TypeScript): no third-party npm dependencies (cloud/package.json)
- Icons: the ML and Messages dock icons are adapted from Lucide ("brain-circuit" and "message-square-text", lucide-static v0.468.0, ISC), as noted in unity/Assets/_Forge/Core/Resources/ForgeLook/DockIcons/ml.svg and messages.svg. The CAD and Code icons are our own
- faster-whisper (MIT), optional and not installed by default: local speech-to-text when STT_PROVIDER=whisper
- SciPy (BSD-3): convex hulls for the humanoid design (hub/cad/parts/humanoid.py)
- Offline data prep only, not run by the hub (hub/cad/tools_offline/): trimesh (MIT) and fast-simplification (MIT) for the G1 meshes; MuJoCo (Apache-2.0), PyTorch (BSD-3), PyYAML (MIT) and matplotlib to record the G1 walk clip
- ML example (ml-examples/cifar10/, run in the user's own env, not the hub): PyTorch (BSD-3), torchvision (BSD-3), NumPy (BSD-3), Pillow (MIT-CMU), matplotlib (PSF-based). TensorBoard logs are read by our own decoder (hub/ml/logtail.py); TensorBoard itself is not a dependency
- ML example (ml-examples/text-classification/, run in the user's own env, not the hub): PyTorch (BSD-3), NumPy (BSD-3), Pillow (MIT-CMU); text cards use Pillow's bundled default font

## APIs and services
- TypeSafe Jev (TypeSafe, hosted API at api.typesafe.ai, used under its terms of service): a model that answers typed questions with a confidence score. The ML desk asks it what kind of model a repo trains, which file trains it, and which panels to show (hub/ml/)
- GitHub REST API through the GitHub CLI (`gh api`, MIT): Albie's ml_github finds a repo by name or search (hub/ml/github.py). Repos it clones are not committed and keep their own licenses
- macOS Messages, optional and off by default (`IMESSAGE_ENABLED=1`): the hub reads the local `~/Library/Messages/chat.db` read-only
- imsg CLI (github.com/steipete/imsg, Homebrew tap `steipete/tap/imsg`): sends confirmed iMessage drafts. Optional, never installed by the hub; the user installs it
- GitHub REST API (api.github.com, optional GITHUB_TOKEN): the ML desk's GitHub import reads a repo's metadata and files, then shallow-clones a public repo over https into the system temp folder (hub/ml/github_import.py). Clones are not committed and keep their own licenses

- Vercel: hosts the cloud functions in cloud/ (voice token, Claude proxy; cloud/deploy.py)
- Hugging Face: a Docker Space hosts cloud-py/ (cloud-py/deploy.sh, unity/Assets/_Forge/Core/Net/ForgeCloud.cs). The ML desk's new projects use the Hub API (huggingface.co/api/datasets and /api/models) and the datasets-server API (/info, /parquet, /rows) to find datasets and download samples, and download pretrained model files (hub/ml/hf_data.py). Datasets and models are not committed; each keeps its own license, and Albie shows the dataset's license from its card

## Assets and data
- Unitree G1 robot description (URDF and meshes) from unitreerobotics/unitree_ros, BSD 3-Clause, in hub/cad/data/unitree_g1/ (license: see hub/cad/data/unitree_g1/LICENSE, details in NOTICE.md)
- Unitree G1 walking policy from unitreerobotics/unitree_rl_gym, BSD 3-Clause, used offline to record walk_clip.json (license: see hub/cad/data/unitree_g1/LICENSE-unitree_rl_gym)
- CIFAR-10 dataset (Krizhevsky, Nair and Hinton, 2009, University of Toronto), downloaded at run time by torchvision for the ML demo (ml-examples/cifar10/); not committed to the repo
- Estes C6 motor thrust curve (RASP .eng file made from NAR published certification data, 2000), hub/cad/data/estes_c6.eng: the rocket's flight sim
- Fonts: Fredoka (SIL OFL 1.1, unity/Assets/_Forge/Pet/ThirdParty/Fredoka/), Inter (SIL OFL 1.1, unity/Assets/_Forge/Core/Fonts/), Liberation Sans (SIL OFL, TextMesh Pro default)
- Albie model, texture and base animations: Alligator from Quirky Series Ultimate Pack Vol.1 (Omabuarts Studio, Unity Asset Store, paid, Standard Unity Asset Store EULA). Not in this repo: the EULA forbids redistribution, so the pack and the files made from its mesh (Gator_Model.fbx, gator_anims.blend) are git-ignored (.gitignore), as the Gator Hack V organizers advised. Each builder imports the pack from their own Asset Store license; the release APK includes it. Our own work on top of it is in the repo: the opening mouth and talk clips (art/gator/ scripts, Gator_Animations.fbx), the controller and the pet code
- AG News topic dataset (Zhang, Zhao and LeCun, 2015, from AG's corpus), CSVs downloaded at run time from github.com/mhjabreel/CharCnn_Keras for ml-examples/text-classification/; not committed
- Hugging Face datasets the ML desk reaches for by name (hub/ml/hf_data.py, ALIASES and DEFAULTS), downloaded at run time and sampled into the Space image at build time (cloud-py/Dockerfile): stanfordnlp/imdb, cornell-movie-review-data/rotten_tomatoes, stanfordnlp/sst2, dair-ai/emotion, fancyzhx/ag_news, ucirvine/sms_spam, Yelp/yelp_review_full, cardiffnlp/tweet_eval, zalando-datasets/fashion_mnist, ylecun/mnist, uoft-cs/cifar100, uoft-cs/cifar10, AI-Lab-Makerere/beans, microsoft/cats_vs_dogs. Any other public dataset can be named by the user
- Hugging Face pretrained encoders the ML desk fine-tunes by name (hub/ml/hf_data.py, MODEL_ALIASES and DEFAULT_MODEL), downloaded at run time: microsoft/xtremedistil-l6-h256-uncased (default), distilbert-base-uncased, microsoft/MiniLM-L12-H384-uncased, roberta-base, albert-base-v2, google/electra-small-discriminator, microsoft/deberta-v3-xsmall, google/bert_uncased_L-2_H-128_A-2, bert-base-uncased
- Anatomy models (heart, brain, eye, lungs, kidneys, stomach): built by unity/Assets/_Forge/Artifacts/Tools~/anatomy/ from Z-Anatomy (CC BY-SA 4.0, github.com/Z-Anatomy/Models-of-human-anatomy) and BodyParts3D, (c) 2008 Life Science Integrated Database Center (CC BY-SA 2.1 JP); the GLBs are shared under CC BY-SA 4.0 and each panel shows the credit. Cell, plant cell and DNA: modelled for Albie (Tools~/anatomy/build_models.py, CC0). All in unity/Assets/_Forge/Artifacts/Resources/ArtifactTemplates/*/assets/
- "PyTorch MNIST" import preset: github.com/pytorch/examples (BSD-3) and the MNIST dataset (LeCun, Cortes and Burges), downloaded at run time by that repo; not committed
