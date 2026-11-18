# Albie agent office

Open **Home → Tools → Agents → Agent office** in Unity Main.

This is a Unity office visualization inspired by [Agent Office](https://github.com/harishkotra/agent-office).
That project uses Phaser/React and Colyseus. This implementation keeps Albie's Unity frontend, Python hub,
Claude/OpenHands executors and existing Scout, Builder, Fixer, Tester, Reviewer, Scribe, Shipper, Designer and
Sentinel identities. No Agent Office code or art is bundled; the furniture is generated in Unity and the
characters use mesh-only portraits of Albie's existing models.

This working copy currently has a missing nested body prefab in `AlbieAssistant.prefab` (GUID
`6060f4d2a87a1d44f955bbb43045cfdc`). The office explicitly labels and uses temporary procedural crocodile
avatars when a body cannot be rendered. These represent the same real crew identities and tasks. Restore
the original model asset to recover the full appearance and animations; the office does not repair or replace
the source prefab. Other views of the original characters can still be missing their bodies until restored.

This first version is a 3D office **inside a spatial window**, not a separate walkable room or an import of
Agent Office's browser application.

## Use it

1. Start the hub and Unity Main, using the same setup as `AGENT_WORKFLOW_TESTING.md`.
2. Open **Tools → Agents**. Choose Project workflow to open the office alongside the plan, or open Agent office on its own.
3. Click **Agent office**. Albie, each assigned project role, and manually hatched crew members get workstations. Project workers appear directly in the office. New crew members appear
   within three seconds. There is no automatic hiring or new inference loop.
4. Select a role button below the viewport to inspect its work status and workflow task.
5. Use **Project workflow** to create and approve a task. The office reads `build.updated` and the existing
   agent bridges; work indicators show queued, running, done or failed steps. A running agent's monitor pulses.
6. Use **Manage crew** for the existing hatch/dismiss controls. Closing the office releases its camera,
   render texture, meshes and materials; existing agents and running tasks continue unchanged.

The viewport is a visualization. Its small idle walks and desk positions are presentation, not commands to
the coding agents. It contains no extra controller, socket, microphone, tool runner, physical collider or
navigation surface. Mesh poses come from the existing characters; hats retain the role colors. A separate
camera draws the office to the panel and never moves the main player or the actual crew.

The office does not start a workflow when opened. It displays the latest workflow, including completed runs.
If the backend is disconnected, the banner and station labels say so rather than claiming current activity.
It works with the no-key scripted demo and the normal backend. Demo builds remain visibly marked as scripted.

## Scope

Implemented here: an office viewport, procedural furniture, existing crew portraits, roster synchronization,
live work indicators, role inspection and links to the existing workflow and crew controls.

Not implemented here: Agent Office's autonomous hiring, agent social conversations, persistent conversational
memory, a furniture editor, new agent task delegation, multi-user isolation, cloud hosting or a replacement
for the current sequential project workflow. Scout appears when hatched; the fixed tip-calculator demo still
assigns work only to Builder, Tester, Scribe and Reviewer.

The referenced repository's license is MIT, copyright 2026 Harish Kotra. If its code or assets are imported in
a future change, preserve the applicable copyright and license notices and check the assets' own licenses.

## Verification — 2026-11-17

- Unity compiled with zero errors and warnings.
- Opened through the real Agents → Agent office button. Albie plus Scout, Builder, Tester and Reviewer made
  five stations. The demo automatically hatched Scribe; reopening showed all six stations.
- Started a demo through the office's Project workflow link and approved it in Unity. The office displayed
  Builder running with the real scripted progress text.
- Build `1bcc25debc1b4e309e362a4ddcc7f292` finished ready; all four workflow roles completed and all six tests passed.
- Closing the office preserved the crew roster and removed the preview stage and camera. Reopened successfully.
- Preview screenshot: `unity/Logs/agent-office-preview.png`. The office was left open in Unity.
- Tested through Unity button handlers and visual screenshots, not physical Quest controller input. Live model
  execution and PR publication remain outside this scripted demonstration.
