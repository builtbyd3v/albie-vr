# CLAUDE.md

Read `PROJECT_CONTEXT.md` (repo root) first. It's the source of truth for the next Albie:
- status and the Meta VR Start competition rules (section 0)
- verified facts about Meta's Muse models (section 1)
- decisions (section 2)
- the build-anything plan and the competition cut (section 15)
- contract proposals (section 13)
- the migration and team rules (section 14)

`docs/PROJECT_CONTEXT.md` is the record of the Gator Hack build: the module specs and the contracts that are live today (its section 7).

## Rules (from PROJECT_CONTEXT section 14.6)
- Work only inside your module folder (`unity/Assets/_Forge/<Module>/`, `hub/<module>/`) unless told otherwise.
- Contracts are law: the live ones in `docs/PROJECT_CONTEXT.md` section 7, plus the proposals in `PROJECT_CONTEXT.md` section 13 once the Core owner accepts them. Propose changes, never silently change them.
- Mock first. Every Unity module runs in its own sandbox scene with mocks from `Assets/_Forge/Mocks/` with the hub off.
- Never edit `Main.unity`, `Packages/manifest.json`, or Project Settings unless you are the Core owner (Hudson).
- Ship features as prefabs. No scene-only setup that someone else has to recreate.
- UI goes inside `ForgePanel` and must work with mouse (DesktopRig) and Interaction SDK ray/poke.
- Quest performance: no allocations or LINQ in `Update`, instancing for repeated meshes, textures 2048 max.
- Unity networking: go through `ForgeNet` (`Core/Net/`): `ForgeEvents.On...` / `ForgeNet.On` for hub events, `ForgeNet.Get/Post` for REST. Don't open your own sockets. Hub off = mock mode (`Resources/Mocks/`, `ForgeMockPlayer`).
- Python: FastAPI routers per module, type hints, pydantic models mirroring `events.py`.
- Never send a real message or take an irreversible action from the pet without a user confirmation step.
- Small commits, pull often, never break `main`.

## Unity project
- Project is `unity/`: Unity 6000.6.7f1, URP, Meta XR SDK 207.0.1 over OpenXR 1.18.0. Setup in PROJECT_CONTEXT 6.1.
- Scripts in `Assets/` with no asmdef compile into Assembly-CSharp, which already references `Oculus.VR` and `meta.xr.mrutilitykit`.
- Drive an open editor with the `unity` CLI (skill: `.claude/skills/unity-cli`). Run from inside `unity/`. `unity status` can wrongly report "no instances" in a sandbox; `unity command` still works.
- Prefer editor scripts with a `[MenuItem]` that build scenes/prefabs in code over hand-editing `.unity`/`.prefab` YAML.
- Play-mode logs: `unity/Logs/Editor.log`. In the simulator, passthrough renders only in the simulator window; the Game view is black.
