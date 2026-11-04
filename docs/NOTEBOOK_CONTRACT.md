# C-17 Notebook: contract proposal

> **Status: PROPOSAL** (2026-11-03). Drafted by Person 5 (Satyabrata) for the Nov 4 goal in `PROJECT_CONTEXT.md` 16.3. It needs Person 2's answer on Thing incidents (section 11) and the Core owner's acceptance before anyone builds on it as law. Until then, build against it as written (16.5).
>
> Index entry: `PROJECT_CONTEXT.md` section 13, C-17. Background: sections 15.6 (demo spine), 15.8 (the notebook), D-28, D-34. Sister contract: `docs/ARTIFACT_CONTRACT.md` (C-12).

Every project has a **notebook**: the engineering log Albie keeps while you work. It works fully on the device, with no network.

This contract also pins down **project versions** (D-28), because every notebook entry points at one and C-16, C-19 and C-12 all depend on what a version number means.

Fixtures are in `unity/Assets/_Forge/Mocks/Resources/Mocks/notebook/` (section 10).

---

## 1. What this changes from section 15.8

| # | 15.8 or D-28 says | This contract says | Why |
|---|---|---|---|
| 1 | `notebook.entry {project_id, version, ...}` | The field is `project_version`, and entries gain `id`, `kind`, `created_at`, `updated_at` | `version` is ambiguous beside Thing, controller and artifact versions. An entry needs an id to be marked later. |
| 2 | `observed` is unstructured | `observed` is an object. Its numbers are filled in by the host, never by the model. | "It never invents data" (9.6) has to hold for the log too. The failure wall is only honest if the numbers are real. |
| 3 | One attempt per version change | One version can hold several entries | The demo trains the Autopilot on v2 without making a v3 (section 4.2). ML A logs each training run as an entry (16.8). |
| 4 | D-28 pins repos, CAD, datasets, checkpoints and artifacts | It also pins **Things and controllers** | D-28 predates the Thing runtime. They're what the competition actually versions. |
| 5 | `v14` as a string (9.2) | An integer on the wire, shown as `v14` | Sorts correctly and matches `thing_version` in C-19. |
| 6 | Tools: `notebook_log`, `notebook_mark`, `notebook_lessons`, `plan_revise` | Adds `notebook_read` | Albie has to read the notebook at session start, and `notebook_lessons` only returns lessons. |
| 7 | The Touches column says "Project module" | The module is new `Notebook/` | Matches 16.1. |

## 2. Entry: `albie.notebook.entry/1`

```json
{
  "schema": "albie.notebook.entry/1",
  "id": "ent_0001",
  "project_id": "proj_truck",
  "project_version": 1,
  "kind": "attempt",
  "change": "First truck: 4 wheels, rear drive, servo steering.",
  "rationale": "Simplest truck that can do a slalom around the couch.",
  "rationale_by": "albie",
  "expected": "Drives around the couch without tipping.",
  "observed": {
    "runs": [],
    "incidents": [ { "kind": "rolled_over", "thing": "truck", "thing_version": 1, "speed_ms": 2.1, "at": "2026-11-10T13:58:40Z" } ],
    "training": null,
    "note": "Rolled over on a sharp turn at 2.1 m/s."
  },
  "outcome": "failed",
  "outcome_by": "person",
  "lesson": "Track width under 1.6x the CoG height rolls at this speed.",
  "lesson_id": "les_0001",
  "tags": ["vehicle", "stability"],
  "created_at": "2026-11-10T13:55:02Z",
  "updated_at": "2026-11-10T13:59:10Z"
}
```

| Field | Rule |
|---|---|
| `id` | `ent_` plus a per-project counter. Never reused. |
| `project_version` | The version current when the entry was opened. |
| `kind` | `attempt`, `training` or `postmortem` (section 3). |
| `change`, `rationale`, `expected` | Plain sentences. `expected` may be null: not everything had a prediction. |
| `rationale_by` | `person` or `albie`: your words or Albie's (15.8). |
| `observed.runs` | `run_id`s (C-12). The numbers stay in the run record and are shown from there. They're never copied into the text. |
| `observed.incidents` | Copied from `thing.incident` events (section 11, question 1). |
| `observed.training` | The controller's `training` block, copied as it is (C-19, 3.4), plus `controller` and `controller_version`. |
| `observed.note` | The only free-text part. Albie's or your summary. Any number in it must come from the records above. |
| `outcome` | `open`, `worked`, `partly` or `failed`. |
| `outcome_by` | `person`, `albie` or `run`. |
| `lesson` | The text, or null. The full record is in the lesson store (section 5). |
| `tags` | From the list in section 5. At least one on any entry with a lesson. |
| `created_at`, `updated_at` | ISO 8601, UTC. |

**Outcome rules:**
- A new entry starts `open`.
- A finished run suggests an outcome: `completed` gives `worked`, `failed` gives `failed`, and `aborted` changes nothing. `outcome_by` becomes `run`.
- `partly` only comes from a person or Albie. Runs can't judge it.
- **A person's mark always wins.** Albie can overwrite a run, and a person can overwrite anything. A run or Albie never overwrites a person.
- An outcome can be changed later. A notebook that can't be corrected isn't honest either.

## 3. What writes an entry

| Moment | Who calls | Result |
|---|---|---|
| A project version is cut (section 4) | Project module | New `attempt`, `open`, with `change` from the cut |
| A training run ends | ML A's `BehaviourCloner` | New `training` entry on the current version, with `observed.training` |
| A run ends | Artifact host | The run id is added to the newest `open` entry on that version. If there's none, a new `attempt` is opened. |
| The Thing rolls over | Notebook, on `thing.incident` | The incident is added to the newest `open` entry, or a new one, the same way. It doesn't change the outcome on its own. |
| "That didn't work", or tapping ✗ | Agent, through `notebook_mark` | `failed`, `outcome_by: person`. Albie asks one question, then writes the lesson with a second `notebook_mark`. |
| A whole plan fails | Agent, through `plan_revise` | New `postmortem` entry and a new plan version (section 6) |

## 4. Project versions: `albie.project_version/1`

### 4.1 What cuts a version

A project version is a saved point you can go back to. It's cut by exactly two things:

1. **A Thing spec change** (`thing.updated`).
2. **A commit** (the "diff to commit" step in code mode, or a repo commit).

Everything else is working state until the next cut: training, tunable changes, a new artifact, a new layout, new runs. That's safe because run records name the exact controller version (C-12, 6.2).

A slider drag saves the controller file each time (C-19 bumps its `version`), and that doesn't cut a project version. Without this rule, one drag would create dozens of attempts.

```json
{
  "schema": "albie.project_version/1",
  "project_id": "proj_truck",
  "version": 3,
  "parent": 2,
  "cut_by": "commit",
  "summary": "Autopilot max_speed raised to 2.2 m/s.",
  "things": { "truck": 2 },
  "controllers": { "truck_manual": 1, "truck_autopilot": 2 },
  "artifacts": { "slalom": 1 },
  "cad": {}, "repos": {}, "datasets": {}, "checkpoints": {},
  "created_at": "2026-11-10T14:20:15Z"
}
```

`cut_by` is `thing` or `commit`. Manifests are written once and never edited. A rollback cuts a new version with `parent` set to the one it came back to, so history only moves forward.

### 4.2 The demo, walked through

This is the check that the rule gives the numbers 15.6 uses:

| Step | What happens | Version | Notebook |
|---|---|---|---|
| 3 | Truck built | **v1** (`cut_by: thing`) | `ent_0001` opens |
| 5 | Flips, "that didn't work" | v1 | `ent_0001` failed, incident and lesson |
| 6 | Wider truck | **v2** (`cut_by: thing`) | `ent_0002` opens |
| 7 | Slalom made, v2 completes it | v2 (no cut) | Run added, `ent_0002` worked |
| 8 | Autopilot trained, drives the slalom | v2 (no cut) | `ent_0003` (`training`), run added |
| 9 | `max_speed` up, the diff becomes a commit | **v3** (`cut_by: commit`) | `ent_0004` opens |
| 10 | "Send me the updated model": truck v3 | v3 | |

This also matches the pretrained "v2 fallback controller" in 16.8, which is trained for Thing v2 and doesn't need a version of its own.

## 5. Lessons: `albie.lesson/1`

Lessons belong to the **user**, not the project, so they carry over (15.8).

```json
{
  "schema": "albie.lesson/1",
  "id": "les_0001",
  "text": "Track width under 1.6x the CoG height rolls at this speed.",
  "tags": ["vehicle", "stability"],
  "by": "albie",
  "from": { "project_id": "proj_truck", "project_version": 1, "entry": "ent_0001" },
  "seen": 1,
  "hidden": false,
  "created_at": "2026-11-10T13:59:10Z"
}
```

- `tags` come from a closed list, so that `notebook_lessons` matches reliably: `vehicle`, `aircraft`, `rocket`, `stability`, `control`, `power`, `ml`, `build`. Changing the list is a small contract PR.
- `seen` counts the attempts behind a lesson. Albie talks about a lesson with `seen: 1` as something that happened ("last time your truck rolled"), not as a rule.
- A lesson always keeps `from`, so you can get from the claim to the evidence.
- `hidden: true` removes a wrong lesson from what Albie reads. The record stays, so the entry doesn't break.

**Session start.** The agent calls `notebook_read`, which returns the current plan, the last 5 entries, and up to 10 lessons that match the project's tags, in about 2,000 characters at most. New projects get matching lessons the same way: "I've started wider."

## 6. Plan revision

C-11 owns the plan document and only names `plan.updated {project_id, version}` so far. This is the minimum `plan_revise` needs, and it holds until C-11 says more:

```json
{
  "schema": "albie.plan/1",
  "project_id": "proj_truck",
  "version": 2,
  "goal": "An RC truck that does a slalom around the couch.",
  "parts": ["body", "wheel", "motor", "servo", "battery"],
  "test": "Finish the slalom with no cones hit.",
  "milestones": [ { "id": "m1", "text": "Drives on the real floor", "done": true } ],
  "supersedes": 1,
  "postmortem": "ent_0005",
  "created_at": "2026-11-10T14:30:00Z"
}
```

Plan versions have their own counter. The old plan is kept (15.8), and `plan_revise` writes the `postmortem` entry and the new plan together. The project's progress ring is `done` milestones over all milestones in the newest plan.

## 7. Storage on the device

**Provisional until the Nov 6 decision in 2.2, but C-19 is waiting on this path, so build against it.** It's the 2.2 default: JSON under `persistentDataPath`, with the device as the source of truth until a cloud copy exists.

```
<persistentDataPath>/projects/<project_id>/
    versions/v0003.json
    notebook/ent_0001.json
    plans/plan_v0002.json
    things/<name>.json
    controllers/<name>.json
    controllers/laps/<name>.json
    artifacts/<id>/manifest.json, scene.json, data/, assets/, layouts/, runs/
<persistentDataPath>/user/lessons.json
```

- **One file per entry**, so a crash can lose at most the entry being written, and sync can merge by id.
- **Survives a restart:** every write goes to a temp file, then replaces the target with `File.Replace`. Never written in `Update` or `FixedUpdate`.
- **Load:** a file that won't parse is moved to `notebook/damaged/` and reported. The rest of the notebook still opens.
- **Sync rule (when a cloud copy exists):** merge by `id`, and the later `updated_at` wins. Project versions and runs never change, so they can't conflict.

## 8. C# surface

Namespace `Forge.Notebook`, folder `unity/Assets/_Forge/Notebook/` (new). No asmdef. This is what other modules call, so ML A and the artifact host don't touch the files.

```csharp
namespace Forge.Notebook
{
    public static class NotebookStore
    {
        /// Opens a new entry on the current project version. Returns its id.
        public static string Log(string kind, string change, string rationale, string rationaleBy,
                                 string expected, string[] tags);
        /// Sets the outcome, and a lesson if given. Returns null, or why it was refused.
        public static string Mark(string entryId, string outcome, string by,
                                  string note = null, string lesson = null, string[] tags = null);
        /// Called by the artifact host when a run ends. Adds it to the newest open entry.
        public static void AttachRun(string runId, string runOutcome);
        /// Called by ML A when training ends. Writes a kind = "training" entry.
        public static string LogTraining(string controller, int controllerVersion, JObject training);
        public static IReadOnlyList<Lesson> Lessons(string[] tags, int limit = 10);
        public static int CurrentVersion(string projectId);
    }
}
```

All of it runs on the main thread. File writes happen on a background queue, in order.

## 9. Events and tools

**Events.** Raised with `ForgeNet.Emit` and read with `ForgeNet.On`, so they work with no hub and need no change to `ForgeEvents.cs`.

| Event | Payload | When |
|---|---|---|
| `notebook.entry` | The full entry | An entry was created. |
| `notebook.updated` | `{project_id, entry_id, outcome, lesson_id}` | An entry changed. |
| `project.version` | `{project_id, version, cut_by, summary}` | A version was cut. |
| `plan.updated` | `{project_id, version}` | Same as C-11. |

**Tools** go in `Notebook/Resources/AlbieTools/notebook.json`, registered with `AlbieTools.Register`, so they run on the device with no hub.

| Tool | Does | Confirm? |
|---|---|---|
| `notebook_read {last?}` | Plan, recent entries and matching lessons (section 5). | No |
| `notebook_log {change, rationale, expected?, tags}` | Opens an attempt. | No |
| `notebook_mark {outcome, entry?, note?, lesson?, tags?}` | Marks the newest open entry, or the one named. | No |
| `notebook_lessons {tags}` | Lessons that match, skipping `hidden`. | No |
| `plan_revise {reason, goal?, milestones?, test?}` | Writes a postmortem and a new plan. The old one is kept. | No |

None of these send anything off the device, and all of them can be corrected afterwards, so none needs a confirm under 14.6.

**Errors** come back as `AlbieTools.Error` data, so the agent can fix them:

| Code | Example message |
|---|---|
| `unknown_entry` | `no entry ent_0042 in this project` |
| `bad_outcome` | `outcome "done": use worked, partly or failed` |
| `unknown_tag` | `tag "physics": use one of vehicle, aircraft, ...` |
| `person_marked` | `a person marked ent_0003 failed, so Albie can't change it` |
| `lesson_needs_tag` | `a lesson needs at least one tag` |
| `no_project` | `no project is open` |

## 10. Fixtures

Under `Mocks/Resources/Mocks/notebook/`. In mock mode `NotebookStore` seeds itself from these, and a panel can read them with `ForgeNet.Get("/notebook/entries")`:

| Path | Covers |
|---|---|
| `entries.json` | The demo's story: failed with an incident and lesson, worked, training, partly, and a postmortem |
| `versions.json` | v1, v2 and v3, as in 4.2 |
| `lessons.json` | Three lessons, one `hidden` |
| `plans.json` | A plan and its revision |

`Mocks/notebook/sequence.json` plays the same story through `ForgeMockPlayer`. The run ids point at the C-12 fixtures.

## 11. Questions to settle

**For Person 2 (Thing runtime):**
1. **Demo step 5 needs an event that isn't in any contract.** The truck flips during free driving, with no artifact running, and the notebook has to know. Proposed addition to C-16: `thing.incident {project_id, thing, thing_version, kind, speed_ms, at}`, with `kind` as `rolled_over` to start, raised when `upright` stays below 0.3 for 1 s (the same test as the slalom's `fail` rule).
2. Are you happy for `thing.updated` to cut a project version (4.1)? A burst of `thing_set` calls from one agent turn should cut one version, so the runtime would need to raise it once per turn.
3. Who calls the version cut for a commit: code mode (Person 1) or the Project module? Proposed: code mode calls `NotebookStore`, since the Project module doesn't exist yet.

**For Person 3 (brain):**

4. Is about 2,000 characters right for the session-start read, and does it go in the prompt or come from a tool call?
5. Lessons are written by the model. The prompt has to say that any number in a lesson or a note comes from the records. Can that go in the agent prompt?

**For ML A:**

6. `LogTraining` copies the `training` block as it is, so the mock's `sim_check` field will appear in `observed.training`. That's fine for the notebook. Should it be added to C-19 section 3.4?

## 12. What's out of this contract (for now)

- The cloud copy and sync service. Section 7 only states the rule.
- Per-user accounts. `user/lessons.json` assumes one person per headset.
- The ghost version trail, the failure wall and the progress ring. They read this data, and their look isn't part of a contract.
- The rest of the project record (C-09). The folder in section 7 is keyed by its `id`.
