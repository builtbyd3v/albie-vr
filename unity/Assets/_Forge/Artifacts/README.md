# Artifacts (Person 5: Satyabrata)

Generated tools, visuals and simulations (C-12, `docs/ARTIFACT_CONTRACT.md`). An artifact is a folder of plain data that `ArtifactHost` reads, validates (`ArtifactValidator`) and builds, with no scripts (D-18).

## "Make me a slalom" (Nov 10, first step)

Albie calls `artifact_create {template: "slalom"}`. That call:
1. copies `Resources/ArtifactTemplates/slalom/` into the library, at `persistentDataPath/artifacts/slalom/`, linked to the open notebook project;
2. opens the artifact. On the first open, a permissions sheet asks to use the room;
3. runs the `floor_slalom` generator (`Scripts/Room/FloorSlalom.cs`), which finds free floor in the real room and places the course, around the couch if there is one;
4. saves the layout (`Scripts/Room/ArtifactLayouts.cs`), so reruns in that room reuse it;
5. builds the cones (`array`), the gates, start and finish (`zone`) and the guide line, on the real floor at true scale.

**When the course doesn't fit in full.** If the requested course doesn't fit, the generator follows C-12's fit rules:
- First it reduces the spacing, then the number of gates. The tool result's `say` tells Albie what to tell the person, for example "This room is tight, so I made a shorter course: 4 gates, 0.6 m apart."
- If even the smallest course doesn't fit, the result is `no_free_floor`, with the size it needs.
- If the room has no scan, the result is `no_room_data`.
- Nothing is ever placed from a guess.

**Where the room comes from.** `ArtifactHost.Room` sets the room. When it's null, the host uses MRUK's current room (`MrukFloorPlan`). Checks and sandboxes use `MockFloorPlan` instead.

**Runs aren't scored yet.** The `run` block is the next step. That step adds zones that notice the Thing, the metrics, run records, and `NotebookStore.AttachRun`.

## Files

| Path | What it is |
|---|---|
| `Scripts/ArtifactHost.cs` | Opens, validates, asks permission, places generated scenes in the room, and relayouts. `ArtifactHost.Current` is the host the tools use. |
| `Scripts/Room/` | `IFloorPlan`, plus the MRUK and mock rooms, the `floor_slalom` generator, layout records, and `ArtifactGenerators`. |
| `Scripts/ArtifactZone.cs` | The `zone` node (with `Contains` for runs) and the `array` node over a slot. |
| `Scripts/ArtifactLibrary.cs`, `Scripts/ArtifactTools.cs` | The templates and the device library. The tools are `artifact_list`, `artifact_create`, `artifact_open`, `artifact_close` and `artifact_relayout`. |
| `SlalomSandbox.unity` | Hub off, no headset. It uses MRUK's own prefab rooms and the mock rooms. Keys: 2 make a slalom, M / N next room, R relayout, [ ] spacing, G gates, O view. Rebuild it with **Forge > Artifacts > Build Slalom Sandbox**. |
| `Editor/SlalomCheck.cs` | 13 headless checks: **Forge > Artifacts > Check Slalom**. `Forge > Check Artifacts` runs the rest. |

## For other modules

```csharp
var host = ArtifactHost.Current;      // the ArtifactHost prefab in the scene
host.Open("slalom");                  // or the tool: AlbieTools.Call("artifact_create", {template: "slalom"})
host.LastLayout;                      // the albie.layout/1 record, or null
host.LayoutMessage;                   // what Albie should say about it
host.Build.Arrays["gates"];           // the gate zones, in order (the run engine counts these)
```

**Events:** `artifact.layout` fires when a course is laid out. Its payload has `{artifact_id, layout_id, placed, shrunk, reused, message}`, or a `code` when nothing fits. `artifact.updated` fires when the library changes.
