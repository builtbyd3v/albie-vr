# Station: demo-director

Read `docs/demo/briefs/_COMMON.md` first and follow it.

You own: `unity/Assets/_Forge/Demo/Scripts/Director/`, `Demo/Scripts/Core/` (the contract; change only if a station asks and you agree, and note it), `Demo/Editor/` (your MenuItems), `Demo/Resources/demo_script.json`, `Demo/Resources/demo_config.json`, `Demo/Sandboxes/DemoSandbox.unity`, `Demo/Prefabs/DemoDirector.prefab`. After G1 (all five pieces play in your sandbox), you are the only station that adds the demo to the Main scene (through `Core/Editor/MainSceneBuilder.cs` or a runtime installer, whichever the project already uses; prefer a runtime installer like `AlbieVoice.Install` so Main needs no edit) and builds the APK (`Core/Editor/QuestBuild.cs`).

## Build

1. **`demo_script.json`**: the beats B0 to B7 from `VIDEO_SCRIPT.md`. Each beat: `id`, `lines` (Albie's lines, each `{text, audio}` where `audio` is a clip name under `Resources/DemoAudio/`; use exactly the line texts and clip names in the table in `docs/demo/briefs/content.md`, which the `demo-content` station records), `cues` (`{at: seconds after beat start, action, args}`), and `advance` (`"speech"` = Hudson's next final speech-to-text result, `"signal:<name>"`, `"auto:<seconds>"`, or `"remote"`). Remote Next always works too.
2. **`DemoDirector`** (MonoBehaviour, on a prefab): loads the JSON, `DemoLayout.Capture()` at start and on reset, plays beats, plays each line (AudioSource on Albie's head or mouth if found, caption via Albie's existing caption: see `Pet/Scripts/**/AlbieCaption.cs`; make Albie's mouth move if `AlbieTalkAnimator` can be fed a level), fires cues on time, waits for the advance condition. Inputs:
   - speech: subscribe to final transcripts the way `Pet/Scripts/**/PushToTalk.cs` / `HubSpeechToText.cs` get them (`ForgeEvents` / `ForgeNet.On("stt.final")`); in demo mode the agent must **not** answer Hudson's speech itself. Find how to mute the agent's reply (pet state or a flag) and do it while the Director runs. Keep the mic listening hands-free the whole run.
   - remote: `ForgeNet.On("demo.next" | "demo.back" | "demo.reset" | "demo.goto")` (the `demo-content` station's `hub/demo/` router broadcasts these; payload `{beat: "B3"}` for goto).
   - editor keys on the desktop rig: `]` next, `[` back, `\` reset (check `PetSandboxHotkeys` / `OSKeys` so you don't steal a key).
   - If an audio clip is missing, fall back to live speech through the hub (`speech.speak`, find the event the Unity side sends) or just the caption plus a wait of 0.06 s per character.
3. **B1 palm summon** (`albie.summon_hand`): when the beat starts, wait for Hudson's left palm to face up (Interaction SDK hand pose, see `Core/Scripts/Gestures/IsdkHandSource.cs` / `GestureHand.cs`; on the desktop rig a key or the synthetic hand), then Albie walks or hops to and lands on the palm (look at `Pet/Scripts/**/AlbieController*.cs`, `AlbieSummon.cs`, the perch code `GesturePerch.cs` may already do "sit on hand"). Raise `albie.on_hand`. If landing on the palm is unreliable, Albie hops onto a point just above the palm and follows it.
4. **Demo mode flag**: `demo_config.json` `{ "enabled": false }` by default. A `[MenuItem("Forge/Demo/Toggle Demo Mode")]` flips it. When enabled the Director installs at startup.
5. **Combined sandbox** `[MenuItem("Forge/Demo/Build Demo Sandbox")]` and `[MenuItem("Forge/Demo/Play Whole Script")]`: the Director plus Albie plus the three pieces' prefabs/components (find them in `Demo/Scripts/Research|Pipeline|Flight`; they may not exist yet: missing actions just log), auto-advancing every beat after 4 s, screenshots per beat to `unity/Logs/demo/director_B*.png`.
6. **Reset** (`demo.reset`): stop audio, Albie back to idle near the chair, every piece resets.

## Done when
- `Play Whole Script` runs B0 to B7 in the editor with no exceptions, firing every cue, with whatever pieces exist at that time.
- Remote Next/Back/Reset events work (test by injecting with `ForgeNet.Inject`).
- Committed. Report to the orchestrator, then **wait**: the orchestrator will send you the integration step (Main scene and APK) once the other stations report.
