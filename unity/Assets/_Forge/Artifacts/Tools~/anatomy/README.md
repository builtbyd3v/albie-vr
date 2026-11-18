# Biology models (heart, eye, brain, lungs, kidneys, stomach, cell, plant cell, DNA)

- `build_anatomy.py` makes the heart, eye, brain, lungs, kidneys and stomach GLBs from open anatomy data.
- `build_models.py` models the DNA, animal cell and plant cell from scratch. No source data is involved, so these
  GLBs are our own work and CC0.
- `make_scenes.py` then writes each template's `scene.json` and `manifest.json` from the GLB and the landmarks the
  build measured, and copies the GLB into `Resources/ArtifactTemplates/<name>/assets/`.

Unity ignores this folder (its name ends in `~`).

The brain is built from Z-Anatomy only. Its gyri and sulci are grouped into the frontal, parietal, temporal,
occipital, insular and limbic cortex of each hemisphere (the rules are `BRAIN_RULES` in `build_anatomy.py`). The white
matter, cerebellum, brainstem and the deep structures are separate parts. The brain is turned so the patient's left
faces glTF +Z, which makes the runtime's cut go down the midline.

```sh
$B -b --disable-autoexec Z-Anatomy/Startup.blend --python build_anatomy.py -- brain <stl_dir> out/brain.glb out/brain.json
for w in dna cell plant_cell; do $B -b --python build_models.py -- $w out/$w.glb out/$w.json; done
for w in brain dna cell plant_cell; do python make_scenes.py $w out/$w.glb out/$w.json ../../Resources/ArtifactTemplates/$w; done
```

## Sources and licence

| Source | Licence | Used for |
|---|---|---|
| [Z-Anatomy](https://github.com/Z-Anatomy/Models-of-human-anatomy) (`Z-Anatomy.zip` → `Startup.blend`) | CC BY-SA 4.0 | Heart: the four chambers, the aortic and pulmonary valve leaflets, the papillary muscles. Eye: sclera, cornea, aqueous humour, iris, lens, zonular fibres, vitreous body, retina, the six eye muscles. |
| [BodyParts3D](https://dbarchive.biosciencedbc.jp/en/bodyparts3d/download.html), © 2008 Life Science Integrated Database Center (STL copy: [Kevin-Mattheus-Moerman/BodyParts3D](https://github.com/Kevin-Mattheus-Moerman/BodyParts3D)) | CC BY-SA 2.1 Japan | Heart: aorta, venae cavae, pulmonary arteries and veins, coronary arteries, cardiac veins, mitral and tricuspid valves. Eye: optic nerve. Lungs: everything. Kidneys: everything but the renal pelvis. Stomach: everything. |

Both are share-alike, so the GLBs are CC BY-SA 4.0. The manifests say `CC-BY-SA-4.0`, and each template's panel shows
the credit (`BioTemplatesCheck` fails without it).

Z-Anatomy is BodyParts3D re-modelled and moved. The build fits a similarity transform from BodyParts3D to Z-Anatomy on
parts both have (four papillary muscles and the pulmonary valve: scale 1.033, residuals under 1.5 mm) and moves the
BodyParts3D meshes with it.

## Lungs, kidneys and stomach

These are built from BodyParts3D, which is in millimetres on Z-Anatomy's axes, so no fit is needed. The exception is
the kidneys' renal pelvis, which only Z-Anatomy has: it is fitted onto BodyParts3D by the kidneys and adrenal glands
(scale 1.07, residuals 6 to 10 mm; the pelvis still sits inside each kidney at the hilum).

- Lungs: the five lobes, trachea, bronchial tree, pulmonary arteries and veins. Separate spreads the lobes and leaves
  the airway and vessel tree; X-ray sees through the lobes.
- Kidneys: kidneys, renal pelvis, adrenal glands, ureters, renal arteries and veins, the aorta and vena cava cropped to
  the kidneys' level, and the bladder.
- Stomach: BodyParts3D has the stomach as one surface, so the build cuts it with planes into the fundus (the dome on the
  patient's left of the cardia, above the cardiac notch), the body, the pyloric antrum and the pylorus (6.5 and 2.2 cm
  before the duodenum). The lining is the wall moved 3 mm inward with longitudinal folds (rugae); it is modelled, not
  scanned.

```sh
# BodyParts3D STLs: FMA7333 FMA7337 FMA7383 FMA7370 FMA7371 FMA7394 FMA7409 FMA66326 FMA66643 (lungs),
#  FMA7204 FMA7205 FMA15571 FMA15572 FMA15629 FMA15630 FMA14752 FMA14753 FMA14335 FMA14336 FMA3784 FMA10951 FMA15900
#  (kidneys), FMA7131 FMA7148 FMA7206 (stomach).
for w in lungs kidneys stomach; do
  $B -b --disable-autoexec Z-Anatomy/Startup.blend --python build_anatomy.py -- $w <stl_dir> out/$w.glb out/$w.json
  python make_scenes.py $w out/$w.glb out/$w.json ../../Resources/ArtifactTemplates/$w
done
```

Labels on these three point at the front surface straight in front of each part's centre (`finish(..., narrow=True)`),
because a lobe or a stomach region wraps round and its front-most point can be across the midline.

## Rebuild

Blender 4.2 or newer (tested on 5.2.2) and Python 3.

```sh
# Z-Anatomy: unzip Z-Anatomy.zip (87 MB) somewhere. BodyParts3D: download the STL files the build names into one folder
# (FMA7234 FMA7235 FMA3736 FMA3768 FMA3784 FMA66326 FMA66643 FMA4720 FMA10951 FMA3802 FMA3818 FMA3840nsn FMA76994
#  FMA71669 FMA4685 FMA3862nsn FMA3895 FMA71670 FMA4706 FMA4707 FMA4713 FMA71567 FMA76751 FMA7260 FMA7261 FMA7262
#  FMA7266 FMA7246 FMA12513 FMA50875).
B=/Applications/Blender.app/Contents/MacOS/Blender
for w in heart eye; do
  $B -b --disable-autoexec Z-Anatomy/Startup.blend --python build_anatomy.py -- $w <stl_dir> out/$w.glb out/$w.json
  python make_scenes.py $w out/$w.glb out/$w.json ../../Resources/ArtifactTemplates/$w
done
# Optional: whole, cut-open and separated renders to check a build.
$B -b --python render_preview.py -- out/heart.glb out/heart 0.6 front
```

`--disable-autoexec` keeps scripts inside the downloaded `.blend` from running.

## What the GLBs carry

- One mesh node per part, named for people ("Left ventricle", "Optic nerve"). `ArtifactExplorer` shows these names
  when the model is separated.
- An empty child under each part named `explode` (Blender exports `explode.001` and so on). Its local position is where
  the part moves when separated. The heart opens as atria up, ventricles down, valves in a row in front, and coronary
  vessels peeled to the sides. The eye opens along the line of sight.
- Ambient occlusion and a little colour variation baked into vertex colours (`COLOR_0`), which glTF multiplies into the
  base colour. Creases read on the headset without textures.
- See-through glTF materials (alpha blend) for the cornea, aqueous humour, lens and vitreous body.
- The heart faces glTF +Z. The eye's cornea faces glTF -X, so the runtime's cut (which drops the half facing the
  viewer) goes through the line of sight.

Sizes: heart 104,861 triangles and 2.4 MB; eye 42,040 triangles and 1 MB; lungs 100,997 and 2.3 MB; kidneys 52,321 and
1.1 MB; stomach 26,645 and 0.6 MB.

## Limits

- Z-Anatomy's GitHub model has no great vessels or coronary arteries, so those come from BodyParts3D. They line up to
  within about 1.5 mm.
- The mitral and tricuspid valves are BodyParts3D's whole valves with their chordae; Z-Anatomy has only some of their
  leaflets.
- Vessels that run on into the body are cropped close to the heart.
- These are teaching models from one adult male dataset, not patient data. For a patient's own heart, segment CT or MRI
  in 3D Slicer (SlicerHeart) and export OBJ/STL into the same pipeline.
