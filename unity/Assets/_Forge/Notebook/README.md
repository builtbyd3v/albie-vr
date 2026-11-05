# Notebook (Person 5: Satyabrata)

The lab notebook (C-17, `docs/NOTEBOOK_CONTRACT.md`). It's the engineering log Albie keeps for a project, and it works on the Quest with no network.

**v1 (Nov 8 goal, PROJECT_CONTEXT 16.3):**
- Attempts are saved.
- "That didn't work" logs a failure.
- The notebook survives a restart.

**Still to come (Nov 12 goals):**
- Lessons shown at session start: `notebook_read` already returns them; the brain's prompt has to call it.
- Plan revision (`plan_revise`).
- Comparing v1 and v2.

## Files

| Path | What it is |
|---|---|
| `Scripts/NotebookStore.cs` | The API other modules call. Entries, project versions and lessons, plus the outcome rules. |
| `Scripts/NotebookFiles.cs` | Writes on a background queue, through a temp file and then a replace. Parses JSON with dates kept as strings. |
| `Scripts/NotebookTools.cs` | Albie's tools, the bridge from ML A's training runs, and the default project. Set up at Play start. |
| `Scripts/Lesson.cs` | A read-only view of an `albie.lesson/1` record. |
| `Resources/AlbieTools/notebook.json` | Tool definitions: `notebook_read`, `notebook_log`, `notebook_mark`, `notebook_lessons`. |
| `Scripts/NotebookSandbox.cs`, `NotebookSandbox.unity` | Desktop sandbox with the hub off. Rebuild it with **Forge > Notebook > Build Sandbox**. |
| `Editor/NotebookCheck.cs` | 13 headless checks: **Forge > Notebook > Check Notebook**. |

## On the device

Everything lives under `persistentDataPath` (C-17 section 7):

```
projects/<project_id>/notebook/ent_0001.json   one file per entry
projects/<project_id>/versions/v0001.json      written once, never edited
user/lessons.json                              lessons belong to the person, not the project
```

- A file that won't load is moved to `damaged/` beside it and listed in `NotebookStore.LoadProblems`. The rest of the notebook still opens.
- Ids are never reused, not even ones from damaged files.
- Writes don't happen in `Update`. On the main thread a write only serialises the record (about 0.05 ms). A worker thread writes the file. `Application.quitting` flushes the queue.
- Until the Project module (C-09) opens projects itself, `proj_truck` is opened at Play start. The sandbox uses its own folder, `notebook_sandbox/`.

## For other modules

```csharp
using Forge.Notebook;

// Artifact host (C-12), when a run ends: "completed" suggests worked, "failed" suggests failed, "aborted" changes nothing.
NotebookStore.AttachRun(runId, "completed");

// Thing runtime (C-16), on a roll-over. thing.incident is still a question for Person 2 (C-17 section 11).
NotebookStore.AddIncident(new JObject { ["kind"] = "rolled_over", ["thing"] = "truck", ["thing_version"] = 2, ["speed_ms"] = 2.1 });

// Thing runtime or code mode: a Thing spec change or a commit cuts a version and opens an attempt.
NotebookStore.CutVersion("thing", "Wider track and a lower centre of mass.", new JObject { ["things"] = new JObject { ["truck"] = 2 } });

// Panels: C# events, or the ForgeNet events notebook.entry, notebook.updated and project.version.
NotebookStore.EntryCreated += entry => ...;
```

ML A doesn't call the notebook directly. `Forge.Controllers.TrainingLog.Report` is connected to `NotebookStore.LogTraining` at Play start, so every finished training run becomes a `training` entry. The controller's `training` block is copied into it unchanged.

## Outcome rules (C-17 section 2)

- **Who wins:** a person beats Albie, and Albie beats a run. A lower one can't change a higher one's outcome. The refusal codes are `person_marked` and `albie_marked`.
- **"That didn't work"** is `notebook_mark {outcome: failed}`, recorded as `outcome_by: person`. The tool answers with a `next` hint: ask one question. The second call has the same outcome plus a `lesson`. It adds the lesson (written by Albie) and keeps `outcome_by: person`.
- **Runs:** a run joins the newest entry on the current version that is still open, or was only judged by a run. So a failed run followed by a completed one ends as `worked`.
