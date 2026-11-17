# Anatomy models (heart, eye)

`build_anatomy.py` makes the heart and eye GLBs from open anatomy data. `make_scenes.py` then writes the two templates'
`scene.json` and `manifest.json` from the GLB and the landmarks the build measured, and copies the GLB into
`Resources/ArtifactTemplates/<heart|eye>/assets/`. Unity ignores this folder (its name ends in `~`).

## Sources and licence

| Source | Licence | Used for |
|---|---|---|
| [Z-Anatomy](https://github.com/Z-Anatomy/Models-of-human-anatomy) (`Z-Anatomy.zip` → `Startup.blend`) | CC BY-SA 4.0 | Heart: the four chambers, the aortic and pulmonary valve leaflets, the papillary muscles. Eye: sclera, cornea, aqueous humour, iris, lens, zonular fibres, vitreous body, retina, the six eye muscles. |
| [BodyParts3D](https://dbarchive.biosciencedbc.jp/en/bodyparts3d/download.html), © 2008 Life Science Integrated Database Center (STL copy: [Kevin-Mattheus-Moerman/BodyParts3D](https://github.com/Kevin-Mattheus-Moerman/BodyParts3D)) | CC BY-SA 2.1 Japan | Heart: aorta, venae cavae, pulmonary arteries and veins, coronary arteries, cardiac veins, mitral and tricuspid valves. Eye: optic nerve. |

Both are share-alike, so the GLBs are CC BY-SA 4.0. The manifests say `CC-BY-SA-4.0`, and each template's panel shows
the credit (`BioTemplatesCheck` fails without it).

Z-Anatomy is BodyParts3D re-modelled and moved. The build fits a similarity transform from BodyParts3D to Z-Anatomy on
parts both have (four papillary muscles and the pulmonary valve: scale 1.033, residuals under 1.5 mm) and moves the
BodyParts3D meshes with it.

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

Sizes: heart 104,861 triangles and 2.4 MB; eye 42,040 triangles and 1 MB.

## Limits

- Z-Anatomy's GitHub model has no great vessels or coronary arteries, so those come from BodyParts3D. They line up to
  within about 1.5 mm.
- The mitral and tricuspid valves are BodyParts3D's whole valves with their chordae; Z-Anatomy has only some of their
  leaflets.
- Vessels that run on into the body are cropped close to the heart.
- These are teaching models from one adult male dataset, not patient data. For a patient's own heart, segment CT or MRI
  in 3D Slicer (SlicerHeart) and export OBJ/STL into the same pipeline.
