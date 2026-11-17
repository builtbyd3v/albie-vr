# Station: demo-integrate (G1 and G2)

Read `docs/demo/briefs/_COMMON.md` and `docs/demo/briefs/director.md` first. You start after the five build stations (director, flight, research, pipeline, content) have finished their turns. Use the get-linked-context skill to read each one's final report first (they are linked to you), and `git log --oneline -20`.

## Do, in order
1. **Compile clean.** Recompile; if errors exist, fix them with the smallest change, in whichever Demo folder they're in (you may touch any `unity/Assets/_Forge/Demo/` file now; outside Demo only if a station's report says it was blocked by something there, and note it).
2. **G1:** run `Forge/Demo/Build Demo Sandbox` then `Forge/Demo/Play Whole Script`. Every beat B0 to B7 must fire with its pieces present (research, pipeline, drone, drone-cam, audio). Check the per-beat screenshots in `unity/Logs/demo/`. Fix wiring gaps between pieces (a missing prefab in the sandbox, an action name mismatch, the verdict position not passed to `drone.mount_camera`, audio not found). Do not redesign any piece.
3. **Main integration:** the Director installs at runtime when `demo_config.json` is enabled (no `Main.unity` edit if at all possible). Turn demo mode **on** for this build. Check in Play mode on Main with the desktop rig that B1 to B7 run with the real Albie and that the demo does not break the OS menu.
4. **APK:** build with `Core/Editor/QuestBuild.cs` (find its MenuItem or method). Note the APK path and size. If a Quest is connected (`adb devices`), install it; don't launch anything else on it.
5. Write `docs/demo/G2_HANDOFF.md`: how to start the demo on the headset, the laptop remote URL (find the hub's port and the laptop's LAN IP; the hub is started as documented in `docs/SETUP.md`), what each beat should look like, everything known to be fake, placeholder or broken. Keep it to one screen.
6. Commit your changes (your paths plus fixes, never `git add -A`), then `report-outcome`.

## Done when
APK built (or the exact build error reported), G2_HANDOFF.md written, committed. Then stop: Hudson tests on the headset next and gives the list.
