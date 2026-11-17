# Proposal: Albie's agent crew in XR

**Status:** Proposal for Hudson's review; not an implementation or a claim that the demo works on a headset.

**Goal:** Let a person speak to Albie, bring several recognizable 3D helper agents into their XR workspace, give them different tasks, and understand their work as it happens. The Agents tool should make the collaboration feel alive while showing real task state and results.

## The experience

A judge opens **Agents** and sees Albie alongside a small crew area in the room. They say, “Albie, ask Scout to find where training starts and Tester to check the existing tests.” Scout and Tester appear as distinct 3D characters, acknowledge their own assignments, and work concurrently. A compact task board next to the crew shows each agent, its task, and an honest state: waiting, working, done, failed, or cancelled. The judge can select Scout with a hand ray or poke and ask a follow-up by name. When both finish, Albie gives a short spoken summary; the judge can open either result for details and evidence.

The first demo should use **read-only** repository questions with existing Scout and Reviewer roles, so it shows real parallel work without unexpectedly modifying a project. A later demo may add Builder and Tester with an explicit review step for edits. The 3D characters are the interface to actual agents, not a replay pretending that work is happening.

## What exists today

This is based on the repository at the proposal's base revision, not a fresh Quest verification:

- `unity/Assets/_Forge/OS/Scripts/OSAgentsWindow.cs` already registers the Agents page, displays the lead and crew, hatches roles, and provides bring home, send out, and confirmed dismiss controls. `OSAgentHome.cs` moves a crew member home without stopping its task. The page is presently a roster; verify the user's empty-state report in the actual build before deciding whether registration, scene setup, connectivity, or presentation is responsible.
- `unity/Assets/_Forge/Pet/Scripts/Crew/AlbieCrew.cs`, `AlbieCrewMember.cs`, and `AlbieAgentTypes.cs` already create 3D crew members with different colors, sizes, hats, names, and desk territories. The current catalog has nine roles but **only one member of each type**, identified by `pet_id == type`. `AlbieAgentBridge` maps task events to character state and captions. The existing egg and keyboard shortcuts have a Code-mode gate; the OS Agents page is documented as hatching independently of that gate.
- `hub/agent/crew.py` has a lead-driven `spawn_agent`, `assign_task`, `dismiss_agent`, and `crew_status`; it runs members concurrently and rejects another assignment to a busy member. Unity sends `crew.roster`; the hub owns task execution. `agent.task_started`, `agent.task_progress`, `agent.task_complete`, and `agent.error` already exist in `hub/agent/events.py` and are routed to characters via `AgentClient` / `AlbieAgentBridge`.
- `hub/agent/wake.py` and `hub/README.md` describe addressing an agent by name. The live voice path and crew runner have separate responsibilities; verify the exact deployed cloud/hub configuration and per-agent speech before promising a judge that all helpers can independently speak. The current hub README describes isolated worktrees for code tasks **and automatic application of successful changes to the main checkout as uncommitted edits**. That is not a review-before-apply workflow.
- `OSAgentsTest`, `hub/tests/agent/`, and fake agent/voice modes provide useful starting points. Fake mode is for deterministic rehearsal and must be visibly labelled; it is not a live multi-agent result. No headset or end-to-end live verification was performed for this proposal.

## Proposed interaction

### 1. Open the crew

The existing Agents button opens an **Agent crew** window. It shows Albie, available roles, and a simple **Add agent** action. Selecting Scout or Reviewer spawns its existing 3D character in a safe, visible spot and adds its row to the window. Show a clear connection status and, if the crew cannot initialize, explain what is missing instead of displaying an empty panel.

Use the desk when available. In open space, place a small anchored crew area within comfortable reach and the user's view. Respect the room and Quest boundary; never put controls or characters behind furniture or outside the available play area. If placement cannot be established, leave the roster usable in a floating panel and explain that the spatial area is unavailable. Reuse the existing crew and placement code where possible; validate the spatial behavior on a headset rather than assuming desk logic covers open space.

### 2. Talk and assign

Support two explicit paths with the same underlying task API:

- **Through Albie:** “Albie, ask Scout to find the training entry point, and Reviewer to review the current changes.” Albie confirms who received what, then both work in parallel.
- **Directly to a helper:** Select its character or row and speak, or address it by name using the existing wake path. Show the selected recipient before listening and the recognized request before or as it is submitted. Resolve ambiguous names by asking the person; never silently send an instruction to the wrong agent.

The panel offers **Assign task**, **Talk**, **Stop task**, and **View result** as appropriate for that agent's current state. For the first slice, reuse speech input and named commands rather than inventing a new keyboard. Hand ray and poke must reach every action. A busy helper can answer a status request without cancelling its active task; a new work request should be refused or queued explicitly, never silently replace it. This matters because the current `AlbieAgentBridge.Ask` cancels an in-flight ask/task before sending a new one: the direct-talk path needs a deliberate design before it is enabled during work.

### 3. See work and results

Use a modest spatial task board beside the characters, not a separate floating window per event. Each row shows role/name, task title, state in words, latest meaningful progress, and elapsed time. A character's animation and a small label mirror its real state; color is supplemental. Selecting a row focuses or highlights its corresponding character. Progress must come from a real event, not a fabricated percentage or animation timer.

On completion, retain a concise result with the agent, task, status, and evidence (for example file paths or test outcome where provided). Albie may summarize one result at a time; queue spoken announcements so agents do not talk over each other. The original result remains available in the panel even after speech. Failed, cancelled, and disconnected tasks stay distinguishable from successful tasks. If a result lacks evidence, say so.

### 4. Finish or leave

**Bring home** changes position only and does not stop work. **Stop task** asks the runner to cancel that specific task and displays the resulting status. **Dismiss** remains separate and explains whether work will stop. Closing the Agents window must not imply that tasks completed. A reconnect should reconcile the real roster and task state, with an explicit “status unknown” when the backend cannot confirm it.

## Implementation outline

### A. Audit and make the existing path visible

1. In the current Main scene and a Quest build, trace Agents button → `OSAgentsWindow.Register/Open` → `AlbieCrew.Instance` → `spawn_agent` → roster acknowledgement. Record where the reported empty experience occurs with and without the hub/cloud connection. Check both empty crew and an unavailable crew service.
2. Reuse the existing `AlbieAgentTypes` catalog and character prefab. Add a clear empty state and connection explanation to the Agents page. Verify naming, hatching, and the active backend; do not silently fall back to a scripted worker.
3. Confirm what the deployed voice path supports for named helpers. Keep the current one-agent-per-role limit for the first slice; show it in the UI instead of spawning duplicate identities.

### B. Real task state and XR presentation

1. Add an orchestration view model owned by the Agents feature. Key each assignment by stable **agent ID and task ID**, not display name. It consumes the existing roster and agent task events; retain the latest result rather than deriving history from the transient busy flag alone.
2. Extend the Agents page with a task board and recipient selection. Connect the board to the existing 3D `AlbieCrewMember` instances; do not create a second fake character roster. Add per-agent status, selection, task/result, and accessible stop controls. Keep labels readable at the actual headset distance and provide text alongside audio.
3. Use the existing `assign_task` for parallel requests, and route per-agent events to the correct row and character. If task identity, completion, cancellation acknowledgement, or reconnect snapshots are insufficient, add versioned protocol fields/events on both ends; preserve existing clients. Define ownership, ordering, duplicate-event handling, and what happens to running work across disconnects before implementation.
4. Provide a direct-address path that does not call the cancelling `Ask` method on a busy member. Start with status and follow-ups after completion; support concurrent conversation during work only after its isolation and cancellation behavior are implemented and tested.

### C. Boundaries for real work

Start with read-only Scout and Reviewer tasks. Existing code tasks use separate worktrees, but successful changes are currently applied to the main checkout automatically. Before exposing write-capable orchestration to judges, agree with Hudson whether to change that behavior: present a diff and require a deliberate user action before applying changes, preserve conflicting worktrees, and never push, merge, send messages, or delete user data as a side effect of a demo. Keep tool permissions and project scope visible. Dismissing an agent or closing the window must not discard work silently.

The existing fake runner and voice modes are useful when credentials or connectivity are unavailable. Show **Demo / simulated** prominently, use a scripted example only with the person's knowledge, and never label a fake branch, tool call, or task result as real. Live mode needs the configured service, project, and network; a loss of connection shows a recoverable error rather than a completed task.

## Judge walkthrough and fallback

1. Open Agents; show Albie and two distinct helpers in a visible crew area.
2. Ask Scout to locate the ML training entry point and Reviewer to inspect the current changes. Show two real tasks running at the same time with their own identities and progress.
3. Ask for Scout's status, then open its completed result and a referenced file. Open Reviewer's result and show any findings or the honest “no findings” outcome.
4. Bring one helper home while it works and show that its task continues. Stop a separate in-progress task and show a cancelled state, if the live path supports it reliably.
5. If the live service is unavailable, explicitly switch to a labelled simulated rehearsal or show the connection error. Do not represent a rehearsal as live execution.

Use a known repository and two pre-rehearsed, bounded read-only prompts. Measure the actual start and completion times on the target network; no latency or headset frame-rate target is claimed until measured.

## Verification and acceptance

- **Backend:** Extend `hub/tests/agent/test_agent_crew.py`, `test_agent_events.py`, and relevant voice/wake tests for two concurrent role-specific tasks; correlation by agent/task ID; busy-agent behavior; cancellation; errors; and disconnect/reconnect. Verify no write or push occurs in the read-only demo. Test fake mode separately from live mode.
- **Editor / XR UI:** Extend `Forge > OS > Run Agents Test` for an unavailable crew, hatching two roles, real event-to-row/character mapping, result retention, bring-home vs stop vs dismiss, and no false completion. Exercise every control with poke and ray through the existing OS test harness; inspect text at headset distance and test gaze/occlusion and a small or obstructed room.
- **End-to-end:** Run the walkthrough with the configured service and a real repository, first in Editor and then on Quest. Capture proof that both tasks overlap, results correspond to the correct helpers, voice targets the intended recipient, and reported evidence is real. Repeat with network loss and recovery and with the hub off.
- **Performance:** Measure frame time, memory, and readable character/board placement with two helpers on the target Quest. Choose limits from those measurements; degrade gracefully if the space or device cannot support more characters.
- **Pass bar:** Two agents can be spawned, assigned independent real read-only tasks, visibly progress concurrently, and return attributable results; the user can address the intended helper and use the controls hands-only. Failure and simulated states remain honest. A headset run is required before claiming the XR demo is complete.

## Decisions for Hudson

1. Is the initial two-agent, read-only judge demo the right scope, or should one agent make an editable artifact in the first slice?
2. Which deployed service is the source of truth for task execution and voice in the intended demo build, and is a laptop/hub expected?
3. Should task results and the board survive app restarts, or is session-only history sufficient for the demo?
4. Should write-capable agents stop at a reviewable diff rather than automatically applying worktree changes to the checkout? Who owns that change?
5. Who owns the XR layout and interaction review, backend event contract, and Quest validation? Confirm the owner and milestone for each before implementation.
