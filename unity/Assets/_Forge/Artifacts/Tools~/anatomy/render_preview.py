"""Renders a GLB from the front: whole, cut open (front half removed) and exploded. For checking builds.
    Blender -b --python render_preview.py -- <in.glb> <out_prefix> [explode_factor]
"""
import bpy, bmesh, math, sys
from mathutils import Vector
args = sys.argv[sys.argv.index("--") + 1:]
GLB, PREFIX = args[0], args[1]
K = float(args[2]) if len(args) > 2 else 0.6
VIEW = args[3] if len(args) > 3 else "front"

def setup():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=GLB)
    sc = bpy.context.scene
    sc.render.engine = "BLENDER_EEVEE"
    sc.render.resolution_x = 900; sc.render.resolution_y = 900
    sc.render.film_transparent = False
    w = bpy.data.worlds.new("w"); sc.world = w; w.use_nodes = True
    w.node_tree.nodes["Background"].inputs[0].default_value = (0.03, 0.035, 0.045, 1)
    w.node_tree.nodes["Background"].inputs[1].default_value = 1.0
    meshes = [o for o in sc.objects if o.type == "MESH"]
    pts = [o.matrix_world @ Vector(c) for o in meshes for c in o.bound_box]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return sc, meshes, (lo + hi) / 2, (hi - lo).length

def camera(sc, centre, size, scale=1.0):
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam")); sc.collection.objects.link(cam)
    d = {"front": Vector((0, -1, 0)), "side": Vector((1, 0, 0)), "top": Vector((0, 0, 1)), "three": Vector((0.6, -1, 0.35)).normalized()}[VIEW]
    cam.location = centre + d * size * 1.6 * scale
    cam.rotation_euler = (centre - cam.location).to_track_quat("-Z", "Y").to_euler()
    cam.data.lens = 50; cam.data.clip_start = 0.001
    sc.camera = cam
    for i, (dx, dz, e) in enumerate([(-1, 1, 3.0), (1, 0.4, 1.4), (0.2, -1, 0.8)]):
        L = bpy.data.objects.new(f"l{i}", bpy.data.lights.new(f"l{i}", "AREA")); sc.collection.objects.link(L)
        L.data.energy = e * 40 * size * size; L.data.size = size
        L.location = centre + Vector((dx, -1.2, dz)) * size * 1.5
        L.rotation_euler = (centre - L.location).to_track_quat("-Z", "Y").to_euler()

def render(sc, path):
    sc.render.filepath = path; bpy.ops.render.render(write_still=True)

sc, meshes, c, size = setup(); camera(sc, c, size); render(sc, PREFIX + "_front.png")
# Cut: drop geometry in front of the centre (glTF +Z = Blender -Y after import).
sc, meshes, c, size = setup()
for o in meshes:
    bm = bmesh.new(); bm.from_mesh(o.data)
    mw = o.matrix_world; inv = mw.inverted()
    co = inv @ c; no = (inv.to_3x3() @ Vector((0, -1, 0))).normalized()
    bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=co, plane_no=no, clear_outer=True)
    bm.to_mesh(o.data); bm.free()
camera(sc, c, size); render(sc, PREFIX + "_cut.png")
sc, meshes, c, size = setup()
moves = []
for o in meshes:
    marker = next((ch for ch in o.children if ch.name.startswith("explode")), None)
    if marker is not None:  # the layout the build authored
        moves.append((o, o.matrix_world.to_3x3() @ marker.location))
    else:
        pc = o.matrix_world @ (sum((Vector(b) for b in o.bound_box), Vector()) / 8)
        moves.append((o, (pc - c) * K))
for o, d in moves:
    o.location += d
camera(sc, c, size, 1.5); render(sc, PREFIX + "_exploded.png")
print("RENDER_OK")
