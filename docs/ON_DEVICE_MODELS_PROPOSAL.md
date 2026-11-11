# Proposal: Hugging Face models on the Quest, with no desktop

**From:** Satyabrata (ML A, Person 5). **For:** Hudson (Core owner). **Date:** 2026-11-09.
**Asking for:** a yes or no on a 1-day spike, and on adding one package to `Packages/manifest.json`.

## The idea

Today, ML runs on the headset only for our own small nets (the brain truck and the "obstacle ahead" template). Everything else needs the laptop hub or the cloud. The idea is to let Albie run an open model from Hugging Face on the Quest itself, with no desktop and no network, such as a small chat model for an offline Albie or a vision model that sees the room.

## What I checked

**Meta already ships this in our SDK.** Meta XR SDK 207.0.1, which we already use, includes Meta's AI Building Blocks (`com.meta.xr.sdk.core`, `Scripts/BuildingBlocks/AIBlocks/`):
- `LlmAgent` with `UnityInferenceEngineProvider` runs a small chat model on the Quest. Meta's docs list SmolLM, Qwen and Phi.
- `ObjectDetectionAgent` runs YOLO-style detection on the passthrough camera.
- Meta's docs say on-device support needs v85 or later, on Quest 3 and 3S. We're on 207.

It's a released Meta tool, which the competition rules allow (PROJECT_CONTEXT 0.4).

**What the source code says** (read from the installed package, not just the docs):

| Fact | Where |
|---|---|
| The on-device path only compiles if `com.unity.ai.inference` 2.2.1 or later is installed (`UNITY_INFERENCE_INSTALLED`). **We don't have it**, so this needs a manifest change. The latest version I found is 2.6.1. | `Meta.XR.BuildingBlocks.AIBlocks.asmdef` |
| Models are ONNX, converted to `.sentis` in the Editor and loaded from the build or `StreamingAssets`. | `UnityInferenceEngineProvider.cs`, `TextOnlyLLMRunner.cs` |
| The worked example config is **Qwen2.5-0.5B** (24 layers, a GPT-2-style tokenizer). CPU is the recommended backend for LLMs. | `OnDeviceLLMConfig.cs` |
| Meta's own timing note: about 2–3 s per reply in blocking mode (which freezes the frame), and **5–10 s per reply in non-blocking mode** (smooth frame rate, recommended for VR). | `OnDeviceLLMConfig.cs` tooltips |
| Limits: text only on the device, greedy decoding, at most 100 new tokens, and the prompt cut to 512 tokens. **No tool calling.** | `TextOnlyLLMRunner.cs` |
| `HuggingFaceProvider` is a *cloud* call to Hugging Face's inference API, with the API key stored on the headset. That breaks our rule that the headset never holds a long-lived key (PROJECT_CONTEXT 6). It's not part of this proposal. | `HuggingFaceProvider.cs` |

**Speed from outside sources.** The LoXR paper measured llama.cpp, a different runtime, on Quest 3 with the CPU only and nothing else running:
- Qwen2-0.5B: about 17 tokens/s
- Gemma 2B (4-bit): about 14 tokens/s
- Phi-3 mini: about 9 tokens/s
- 7B models: 3–6 tokens/s
- About 2.35 GB of RAM, and about 10% of the battery in 10 minutes.

The paper recommends models under 2B parameters. Nobody I found has measured Unity Inference Engine LLM speed on a Quest. That's what the spike is for.

**Licences.** Qwen2.5-0.5B-Instruct is Apache 2.0 (checked on its Hugging Face repo). SmolLM2 is listed as Apache 2.0 (seen on a third-party page only, still to check on its model card). Every model needs its licence checked before we ship it.

## Risks

1. **The manifest change is yours to make**, 5 days before feature freeze. Unity Inference Engine 2.6.1 is unproven on our Unity version (6000.6) and with our Meta SDK version.
2. **The CPU is shared.** The brain truck trains on the CPU at 4 ms a frame, and physics runs there too. An LLM generating at the same time could cost us 72 fps.
3. **Size.** About 494M parameters is about 2 GB as float32, about 1 GB as float16, and about 0.5 GB as 8-bit (estimated from the parameter count). An APK must be under 1 GB, so the model would ship as a Meta asset file, which is downloaded at install.
4. **Garbage.** Meta's runner allocates on every token (LINQ, new tensors). It doesn't run in `Update`, but it may cause GC hitches.
5. **Quality.** A 0.5B model gives short, plain answers and can't drive Albie's tools. Albie's brain stays Muse (D-20).
6. **"Pull any model" can't happen on the device yet.** I believe ONNX-to-`.sentis` conversion is Editor-only (Meta's converter is an Editor menu), but I haven't verified that. If so, a model picked at runtime would need a cloud converter.

## What I propose

**Before submission: one time-boxed spike, kept off `main` until it passes.**
- **Who:** Satyabrata, in a test APK like the ML A timing kit. Nothing touches `Main.unity`.
- **What it adds:** `com.unity.ai.inference` (only on the spike branch, with your OK), Qwen2.5-0.5B-Instruct as 8-bit `.sentis`, and one `LlmAgent` in non-blocking mode.
- **What it measures on a Quest 3:**
  - load time and time to the first token
  - tokens per second
  - fps while generating, alone and while the brain truck trains
  - memory
  - build size
- **It passes if:**
  - the frame rate holds 72 fps while generating (frame time at or under 13.9 ms);
  - a short reply arrives in 10 s or less;
  - the app doesn't crash on low memory;
  - the build stays under 1 GB, or the model ships as an asset file.
- **If it passes**, the only competition use is **offline Albie**. With no network, Albie still answers in short text and says it's offline. Every tool call stays on Muse.
- **Decision by Nov 12**, so it's in or out before the Nov 13 end-to-end run and the Nov 14 freeze. If it fails or slips, it goes to the roadmap and nothing in the build changes.

**After Nov 18 (roadmap), in this order:**
1. **"Albie sees your room"**: `ObjectDetectionAgent` on the passthrough camera. Meta ships a working Quest sample. It needs the headset camera permission.
2. **Pick a model from Hugging Face**: Albie checks the model's size, format and licence. If it fits (under about 2B parameters, ONNX), the cloud converts it and the headset downloads the `.sentis` file. Anything bigger runs in the cloud, as PROJECT_CONTEXT already plans.
3. Try llama.cpp (GGUF) as a second runtime if the Inference Engine is too slow. It needs a native Android plugin.

## What I need from you

1. Yes or no to the spike.
2. Yes or no to adding `com.unity.ai.inference` on the spike branch, and to `main` only if it passes.
3. Is offline Albie worth showing in the video, or should the spike only feed the roadmap?

## Sources

- Meta, AI Building Blocks overview: https://developers.meta.com/horizon/documentation/unity/unity-ai-building-blocks-overview/
- Meta, Unity Inference Engine on Quest: https://developers.meta.com/horizon/documentation/unity/unity-ai-unity-inference-engine/
- Meta, camera object detection sample: https://developers.meta.com/vr/documentation/unity/unity-sample-camera-object-detection
- Unity Inference Engine changelog (2.6): https://docs.unity3d.com/Packages/com.unity.ai.inference@2.6/changelog/CHANGELOG.html
- LoXR, LLMs on XR devices including Quest 3: https://arxiv.org/html/2502.15761v1
- Meta, packaging limits (APK under 1 GB): https://developers.meta.com/horizon/resources/vrc-quest-packaging-5/
- Meta, asset files: https://developers.meta.com/horizon/documentation/unity/ps-assets/
- Qwen2.5-0.5B-Instruct licence: https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct
