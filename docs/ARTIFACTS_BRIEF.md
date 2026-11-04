# Artifacts brief

**From:** Hudson (product direction, 2026-11-03)
**For:** Satyabrata (Person 5) and any agent designing how artifacts look and work
**Goes with:** `PROJECT_CONTEXT.md` section 9 (the technical spec), section 15 (the Thing runtime and competition cut)

This doc explains what an artifact is meant to be. Section 9 explains how we currently plan to build one. Where the two disagree, see [Where this differs from the spec](#where-this-differs-from-the-spec). Those points are open, not settled.

## The short version

Albie doesn't have general apps. It has **tools**, **projects** and **artifacts**. The idea is that you can do anything you want to do.

An artifact is whatever gets built when someone wants to simulate something, try something out, test something, research something or just make something. It can be part of a project or stand alone. It runs, it just works, and it looks beautiful.

"Artifact" is a working name and may change.

## The three building blocks

| | What it is | Examples |
|---|---|---|
| **Tools** | How you interact and what you can do. Some we make ourselves, and more can be added down the line. | Code editor, the ML tools, 3D editing, possibly schematics |
| **Projects** | A real goal you're working toward. Albie takes it from the idea through design, parts and testing. It should be so intuitive that it just works with everything. | "I want to build my own drone with a 3D-printed case and a mount for my camera" |
| **Artifacts** | The random things people end up needing. Some connect to a project and some don't. | An air simulation for the drone, a three-body problem visualization, a one-off 3D thing |

## What an artifact has to do

- **Do whatever we want.** This is the main requirement. It shouldn't be limited to a short list of types. If someone is building something and wants to simulate it, try it out or do anything else with it, Albie builds what that takes.
- **Work as part of a project or on its own.** Both are normal. Neither is a lesser version.
- **Connect to its project.** A project artifact uses that project's real data, such as the actual drone design.
- **Possibly auto-update when its project updates.** If the drone changes, its simulation might change too. See open question 2.
- **Run with no setup.** You ask for it, it appears and it works.
- **Look beautiful.** A really good result, not a rough placeholder.
- **Let you change things and watch what happens.** You edit values and see how things play out over time.
- **Respond to you when it needs to.** For example, a simulation where a drone follows you as you actually walk around.

There's more to it than this list. This is the general gist, so design for the open-ended version, not just these examples.

## Example: the drone project

This shows how the three blocks work together.

1. **The ask.** "I want to build my own drone with my own 3D-printed case, structured this way, with a mount for my camera."
2. **Design (tool).** Albie takes the data, designs it in 3D and shows a mock-up.
3. **Electronics.** Albie asks: "Do you have the electronics sorted out, or do you want me to figure that out?" If the user has no idea, Albie goes to the internet and finds all the electronics the drone needs.
4. **Schematic (possibly a separate tool).** Albie shows how everything connects together.
5. **Simulation (artifact).** "That's cool, but can we actually simulate it?" Albie builds a simulation, such as an air simulation, connected to this project.
6. **Behavior (artifact).** "Program it so that if it detects a person, it follows them, and let me simulate that." The user walks around and the drone follows them. It's kind of crazy, but it should just work.

## Examples: standalone artifacts

- **Three-body problem.** "I saw a video about the three-body problem. Can you make a really good visualization where I can edit things and see how they work over time?" Albie makes a standalone artifact that shows all of that. It just runs, it just works and it's beautiful.
- **Research.** Someone wants to research a topic and gets something built around it.
- **Trying something out.** Someone wants to test a specific thing without starting a project.
- **Random 3D.** Someone just wants to make a random 3D thing.

## The test

When deciding whether something is an artifact, ask:

- Am I building something and want to simulate it or try it out? **Artifact, connected to the project.**
- Do I want to test a specific thing, research something or build something random? **Artifact, standalone.**
- Is it how I work on things, like an editor? **Tool.**
- Is it the overall goal that ties the work together? **Project.**

## Open questions

These are the questions the direction above leaves open. Hudson decides them.

1. **The name.** "Artifact" is up for debate.
2. **Auto-update.** Should a project artifact always update when its project does, or should it be a choice per artifact? Section 9.7 is relevant here.
3. **Schematics.** Is this its own tool or part of something else?
4. **Tool vs artifact boundary.** A simulation is an artifact, but could a good one become a reusable tool? Tools can be added down the line, so the path between them is worth designing.

## Where this differs from the spec

These were noticed while writing this brief. **None of them have been resolved**, and this doc doesn't change any contract or decision. Raise them with Hudson rather than picking a side.

| Topic | This brief | `PROJECT_CONTEXT.md` | Why it matters |
|---|---|---|---|
| **How much an artifact can do** | Whatever we want | D-18, 9.3: Phase 1 is a data-driven scene with fixed node kinds and declared rules, and no scripts. Scripts wait for a Quest IL2CPP spike. | Section 9.8 already says a full three-body integrator needs Phase 2. The Phase 1 fallback is playing back cloud-computed data, which doesn't obviously allow live edits. |
| **Project updates** | Maybe auto-update | 9.7: a design change doesn't change the course. The user taps Rerun, and runs are compared across versions. | A test that changes by itself can't give a fair v14 vs v15 comparison. One possible answer is to update the thing being tested but keep the test fixed. |
| **Top-level structure** | Tools, projects, artifacts, with the code editor as a tool | D-07: three modes, CODE, PROJECT and ARTIFACTS. ML, CAD and simulation are core tools (section 10). Code mode is the cockpit (15.7). | This affects `ui.mode` and navigation, so it isn't only a naming change. |
| **Internet and research** | Albie pulls what it needs off the internet | 9.10: artifacts have no network by default. 9.6: outside data comes through connectors as snapshots. | These may fit together if the agent does the research at build time and the artifact keeps a snapshot. That needs confirming. |
| **Following the user** | The drone follows you as you walk | 9.6 lists built-in sources (project, notebook, Thing telemetry, arena runs, the room). The user's live position isn't listed. | The follow-me simulation needs it as an input. |
| **Scope by Nov 18** | The full vision | 15.10 and 16: the competition cut is room-aware courses (slalom, hoops), with run records. Electrical and PCB work is roadmap (15.9). | **This brief describes where artifacts are headed and doesn't change the competition cut** unless Hudson says so. Design so the open-ended version isn't ruled out, and build the cut first. |
