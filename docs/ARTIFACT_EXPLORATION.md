# Artifact exploration

The artifact panel can turn, cut open, separate, see through and reset any imported GLB model. It works with the mouse
(DesktopRig) and with ray and poke in the headset. The heart and eye templates are now real anatomy made of named
parts, so those controls have something to show.

## Controls (`ArtifactExplorer`)

| Button | What it does |
|---|---|
| Turn 30° | Rotates the model about its centre. |
| Cut open / Whole | One cut through the model's centre, facing the viewer. The front half of every part is dropped, so the inside shows: chambers, valves and chordae in the heart; cornea, lens, vitreous body and retina in the eye. The imported meshes aren't changed. |
| X-ray | The outer parts go see-through (18% opacity, keeping their colour), and the inner parts stay solid. In the heart, the valves and papillary muscles show through the chambers and vessels, and the blood cells can be followed. |
| Separate / Assemble | Parts glide apart over 0.7 s to a layout that ships with the model. Each part shows its name, and labels move above or below a part so they don't collide. The scene's own labels hide while the parts are apart. |
| Reset view | Puts the model back whole, solid, assembled and in its starting pose. |

## Hands first (`ArtifactHandGrab`)

No controller needed. Every artifact volume gets it (`ArtifactHost.ShowVolume`).

| With your hands | What happens |
|---|---|
| Pinch on (or within 6 cm of) the model | You pick it up. It follows your hand, and turning your wrist turns it (1.5× for spinning, 1.2× for tilting, so you don't need to twist far). |
| Pinch with the other hand too | Pull your hands apart to make it bigger, or together to make it smaller (0.4× to 3×). Turn both hands like a steering wheel to spin it. |
| Let go with one hand | The other hand keeps holding it. |
| Separate, then pinch one part | You take just that part (the smallest under your fingers, so a valve beats the chamber around it) and can turn it freely. Assemble or Reset view glides it home. |
| Reset view | Also puts back wherever your hands moved it. |

How it stays smooth and dependable:
- The pinch point and hand direction are smoothed more when your hand is still and less when it moves fast, so the
  model doesn't shake but doesn't trail behind. In the checks, 4 mm of tracking jitter moves a held model well
  under 2.5 mm.
- The pinch closes at 24% of your hand's size and opens at 40%, so it doesn't flicker between the two.
- It survives a quarter second of lost tracking.
- While a hand is near or holding the model, the volume's ray grips (`VolumeManipulator`, for pointing from afar)
  pause, so the two never fight. The base lights up and a hint line says what a pinch will do.
- The logic is `HandGrabCore`, plain C#. `ArtifactChecks` drives it with synthetic hands.

## By voice (`ArtifactVoiceTools`, `Resources/AlbieTools/artifact_voice.json`)

These are headset tools, part of Albie's standalone voice (`AlbieVoice`). The laptop hub's voice doesn't have
artifact tools.

| Say | Tool and what it does |
|---|---|
| "Expand the heart", "take the eye apart", "explode it" | `artifact_explore` separate. With `template`, it opens the model first if it isn't open and waits for it to load. |
| "Put it back together", "cut it open", "make it whole", "X-ray it", "turn it", "reset it" | `artifact_explore` assemble, cut open, whole, X-ray, turn (by degrees), reset |
| "Show me the frontal lobe", "just the left ventricle", "only the valves", "the frontal and temporal lobes" | `artifact_explore` isolate: those parts glow a little, come toward you and grow, and show their name and what they do; the rest fade to a faint outline. `mode: highlight` keeps the rest see-through instead, `expand: false` leaves them in place. One call, any list or group. |
| "Show me this lobe", "expand this", "just this part" | `artifact_explore` isolate with `part: "this"`: the part you're pointing at (hand, finger, ray or mouse), else the one you're looking at, in the last 2.5 s. |
| "Show me the mitral valve" (focus) | `artifact_explore` focus: that part solid and the rest see-through, nothing moves. |
| "Show all", "bring the rest back" | `artifact_explore` unfocus. Reset view also clears it. |
| "What's this?" | `artifact_describe`: `pointed_at` and `looked_at` say which part and what it does. |
| "Explain the blood flow", "how does the eye see", "tour the brain", "follow a protein" | `artifact_press`: plays the model's walkthrough. Albie gets its captions in order, with timings, and explains one step per caption as it plays. A request to explain that names no button plays the model's main walkthrough. |
| "Show / hide the blood flow", "spin it", "stop spinning", "hide labels" | `artifact_press` on that button. A toggle's label flips as if it were tapped, and asking for what's already on says so. |
| A question about the open model | `artifact_describe`: the title, parts, buttons, walkthrough captions, notes and state, so Albie answers from what's there. |

The scene's own labels now hide whenever the parts are apart or shown on their own, however that was asked for (the Explorer does it).

### Part names people use (`parts.json`, `PartMatch`)

Each anatomy and biology template has `Resources/ArtifactTemplates/<template>/parts.json`: per part a one-line `about`
and other names (`aka`: "upper left chamber", "little brain", "white of the eye"), `groups` ("chambers", "atria",
"valves", "arteries", "lobes", "hemispheres", "back of the brain", "the left side"), words that narrow nothing
(`ignore`: "heart", "brain"), and `bilateral` for the brain, where "the frontal lobe" means both sides. `PartMatch`
(plain C#) matches case, spacing, underscores, plurals, British or American spelling, lists ("the aorta and the mitral
valve", "frontal and temporal lobes") and partial words. A singular that fits several parts ("the valve", "the lobe",
"vena cava") isn't guessed: the result has `options` and Albie asks. Nothing matching returns the real part names and
groups. The heart's own left side is the anatomical one (pulmonary veins to aorta), not every part named "Left".

### "This" (`ArtifactPartPointer`)

Each loaded model gets picking colliders (its meshes, on the Ignore Raycast layer, added two a frame) and a pointer
that, 15 times a second, asks what is under: a hand holding or about to take a part, an index finger pointed at it
(middle finger curled), the Interaction SDK rays, then the mouse; and separately the centre of the view. What was
pointed at is kept 2.5 s so the voice round trip doesn't lose it, and the part under a pointer is lit a little.

### Models nobody split (imported with `model_import`)

A model without `explode`/`xray` markers gets parts from its node tree: down through exporter wrappers
(Sketchfab_model, root, GLTF_SceneRootNode) to the first node with several branches; a branch that is several is opened
while there are fewer than four; fragments under 6% of the model's size join their nearest neighbour; at most 20 parts.
Names come from the nodes ("Fan_Blades_2" is "Fan blades"); "Object_12", "polySurface34" and hashes are dropped for
"Part 3 (top)". Parts move straight out from the centre. A model that is one mesh is one solid piece and says so.
Imported models get Separate and X-ray on their panel once they turn out to have parts.

Two markers in the GLB, both empty child nodes of a part, drive this:

- `explode` (or `explode.001` and so on, as Blender names them). Its local position is where the part goes when
  separated. A part without one moves straight out from the centre.
- `xray`. Marks a part that goes see-through. A model with no `xray` markers uses size instead: parts whose bounds are
  at least 12% of the model's.

The markers are data inside the asset, so the C-12 scene schema doesn't change.

## The models

Built by `unity/Assets/_Forge/Artifacts/Tools~/anatomy/` (README there) from Z-Anatomy (CC BY-SA 4.0) and BodyParts3D
(CC BY-SA 2.1 JP). The GLBs are CC BY-SA 4.0, and the panels show the credit.

- **Heart, 17 parts, 104,861 triangles, 2.4 MB.**
  - From Z-Anatomy: the four chambers as separate walls, the aortic and pulmonary valve leaflets, and the papillary
    muscles.
  - From BodyParts3D: the aorta, both venae cavae, the pulmonary arteries and veins, the right and left coronary
    arteries, the cardiac veins, and the mitral and tricuspid valves with their chordae.
  - Separated, the atria go up, the ventricles go down, the valves line up in front, and the coronary vessels peel off
    to the sides.
- **Eye, 15 parts, 42,040 triangles, 1 MB.**
  - From Z-Anatomy: sclera, cornea, aqueous humour, iris, lens, zonular fibres, vitreous body, retina and the six eye
    muscles.
  - From BodyParts3D: the optic nerve.
  - Separated, the layers spread along the line of sight and the muscles move outward.

- **Brain, 22 parts, 96,270 triangles, 3 MB.** From Z-Anatomy, with its gyri and sulci grouped into lobes.
  - Each hemisphere: frontal, parietal, temporal, occipital, insular and limbic cortex, and white matter.
  - Also: cerebellum, brainstem, corpus callosum, thalamus, basal ganglia, hippocampus and fornix, amygdala and
    ventricles.
  - Cut open gives the midline view. Separated, the lobes move out and the hemispheres part.
- **Animal cell, 13 parts; plant cell, 10 parts; DNA, 5 parts.** Modelled in Blender (`build_models.py`, CC0).
  - Cells: organelles at textbook relative sizes, with mitochondrial cristae, chloroplast grana and ribosomes on the
    rough ER.
  - DNA: B-form, 10 pairs per turn with the grooves. Bases are coloured by letter, with 2 or 3 hydrogen bonds per pair.
    Separate unzips the strands.

Shading is ambient occlusion baked into vertex colours. The cornea, aqueous humour, lens and vitreous body are
see-through glTF materials. glTFast's PBR shader is in Always Included Shaders, so those variants are in the Quest build.

## Templates

`make_scenes.py` writes each template's scene from the model's own landmarks, so labels point at the real parts.

- **Heart.** 14 labels with leaders.
  - *Trace the blood* plays four captioned steps and starts the blood cells.
  - *Show blood flow* runs 19 cells on a loop along the real path: venae cavae, right atrium, tricuspid valve, right
    ventricle, pulmonary valve, pulmonary arteries; then pulmonary veins, left atrium, mitral valve, left ventricle,
    aortic valve, aorta. Blue cells carry oxygen-poor blood, red cells oxygen-rich.
  - The heart beats at 72 per minute about its own centre.
- **Eye.** 8 labels.
  - *Follow the light* sends a photon through the cornea, the pupil and the lens to the retina, then a signal down the
    optic nerve, with four captions.

## Checks

- `ArtifactChecks` (Exploration):
  - a single mesh doesn't claim parts, and the cut keeps only the back half;
  - with two parts: the move is gradual, a marker sets where a part goes, an unmarked part moves outward, names show
    only when apart, X-ray changes only the marked or big part, and reset restores everything.
- `ArtifactChecks` (Exploration, isolate): stand-ins named from the bundled heart and brain GLBs' nodes run isolate,
  groups, other names, "the valve" asking which, the no-match list, highlight mode and reset; a made-up ray finds the
  lobe it's on and "this lobe" isolates it; a Sketchfab-shaped tree becomes named parts, a one-mesh model refuses, and
  sixty meshes become at most 20 parts. Casting and laying out allocate nothing.
- `BioTemplatesCheck`:
  - every template is valid;
  - the models are openly licensed, credited in the panel, and match their hashes;
  - the heart and eye have at least 10 named parts, each with an `explode` marker, plus `xray` markers;
  - no part name has a `.001`;
  - labels have leaders;
  - captions and cells stay hidden until their button;
  - the heart beats about its own centre.

Still to check on a Quest: label readability, frame time with X-ray on (up to 12 transparent parts), and ray and poke
on the panel buttons.

## Next

- Bind the scene labels to parts so they travel with them when the model is separated, instead of hiding.
- A beat that squeezes the atria, then the ventricles, rather than scaling the whole heart.
- For patient-specific hearts, segment CT or MRI in 3D Slicer (SlicerHeart) and export OBJ or STL into the same
  pipeline.
