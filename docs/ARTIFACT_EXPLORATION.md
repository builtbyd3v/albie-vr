# Artifact exploration

Branch: `feat/artifact-exploration`

The imported GLB viewer now supports 30-degree turns around the geometry centre,
a reversible surface section, separate/assemble for models authored with multiple
mesh transforms, and resetting the inspection pose. Controls use the existing
artifact panel, which supports desktop mouse and headset ray/poke interactions.

The bundled heart and eye each contain one textured mesh, one primitive and one
material. They have no authored anatomical part hierarchy. Their existing labels
and flow animations remain scene annotations: they are not bound to moving mesh
parts. Turning the surface does not reposition these annotations.

The section tool removes triangles on the positive local-Z half of each mesh.
It preserves the source mesh and textures. It leaves an open surface, without a
filled cut face or invented internal structures. Separate is inactive in effect
for a single-mesh asset. This first pass does not improve the source geometry's
realism or certify anatomical accuracy.

Next model work:

- Replace heart and eye with licensed, reviewed meshes with named structures.
- Define per-part identifiers, labels, explanatory text and authored separation
  vectors; bind leader lines and learning animations to those identifiers.
- Add smooth explode transitions, individual focus/hide and hand manipulation.
- Apply the same part contract to other imported artifacts. The solar system is
  currently built from scene nodes, so it does not use these GLB controls.
- Verify readability, clipping, frame time and interaction on the headset before
  shipping. Automated checks cover section reversibility and reset behavior.
