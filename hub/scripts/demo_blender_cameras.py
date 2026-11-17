"""Build the made-up camera boards for the demo, render product pictures, export the winner (cam_a) as glb and fbx.

Run headless (Blender 4.1+):

    blender -b -P hub/scripts/demo_blender_cameras.py -- <repo root>

Writes unity/Assets/_Forge/Demo/Resources/DemoImages/cam_a..cam_d.png (512x512, transparent) and
DemoModels/camera_a.glb + camera_a.fbx. Real scale (metres): cam_a is 25 x 25 x 20 mm, origin at the back of the
mount plate, lens facing Blender -Y (which lands on +Z, Unity forward, in both glb and fbx).
No logos, no text, no brands.
"""
import math
import os
import sys

import bpy
from mathutils import Vector

ROOT = sys.argv[sys.argv.index("--") + 1] if "--" in sys.argv else os.getcwd()
IMG_DIR = os.path.join(ROOT, "unity", "Assets", "_Forge", "Demo", "Resources", "DemoImages")
MODEL_DIR = os.path.join(ROOT, "unity", "Assets", "_Forge", "Demo", "Resources", "DemoModels")
os.makedirs(IMG_DIR, exist_ok=True)
os.makedirs(MODEL_DIR, exist_ok=True)

MM = 0.001


def mat(name, color, metal=0.0, rough=0.5, coat=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    b.inputs["Base Color"].default_value = (*color, 1)
    b.inputs["Metallic"].default_value = metal
    b.inputs["Roughness"].default_value = rough
    if coat:
        for key in ("Coat Weight", "Clearcoat"):
            if key in b.inputs:
                b.inputs[key].default_value = coat
                break
    return m


def box(name, size, loc, m, bevel=0.0):
    bpy.ops.mesh.primitive_cube_add(size=1, location=(loc[0] * MM, loc[1] * MM, loc[2] * MM))
    o = bpy.context.object
    o.name = name
    o.scale = (size[0] * MM, size[1] * MM, size[2] * MM)
    bpy.ops.object.transform_apply(scale=True)
    if bevel:
        bv = o.modifiers.new("bevel", "BEVEL")
        bv.width = bevel * MM
        bv.segments = 1
        bpy.ops.object.modifier_apply(modifier="bevel")
    o.data.materials.append(m)
    return o


def cyl(name, r, depth, loc, m, axis_y=True, verts=32, r2=None):
    """Cylinder along Y (lens axis, -Y = front). loc is the centre in mm."""
    if r2 is None:
        bpy.ops.mesh.primitive_cylinder_add(vertices=verts, radius=r * MM, depth=depth * MM,
                                            location=(loc[0] * MM, loc[1] * MM, loc[2] * MM),
                                            rotation=(math.pi / 2, 0, 0) if axis_y else (0, 0, 0))
    else:
        bpy.ops.mesh.primitive_cone_add(vertices=verts, radius1=r * MM, radius2=r2 * MM, depth=depth * MM,
                                        location=(loc[0] * MM, loc[1] * MM, loc[2] * MM),
                                        rotation=(math.pi / 2, 0, 0) if axis_y else (0, 0, 0))
    o = bpy.context.object
    o.name = name
    bpy.ops.object.shade_smooth()
    o.data.materials.append(m)
    return o


def board(prefix, pcb_rgb, lens_r, ribbon_len, ribbon_w, gimbal=False, holder=14.0, accent=(0.9, 0.7, 0.25), ribbon=True):
    """One camera module. +Y is back, -Y is front. Back of the mount plate at y=0, PCB 25 x 25 mm in XZ."""
    pcb = mat(prefix + "pcb", pcb_rgb, 0.0, 0.32, 0.4)
    gold = mat(prefix + "gold", (0.83, 0.62, 0.22), 1.0, 0.3)
    black = mat(prefix + "black", (0.025, 0.025, 0.03), 0.6, 0.4)
    alu = mat(prefix + "alu", (0.55, 0.56, 0.6), 1.0, 0.35)
    glass = mat(prefix + "glass", (0.015, 0.025, 0.07), 0.2, 0.04, 1.0)
    amber = mat(prefix + "amber", tuple(c ** 2.2 * 0.8 for c in accent), 0.0, 0.5)  # amber film, darker than the swatch
    white = mat(prefix + "white", (0.85, 0.85, 0.82), 0.0, 0.45)
    parts = []
    # mount plate and four stand-offs (the "mount": origin is the back face of the plate)
    parts.append(box(prefix + "plate", (25, 1.5, 25), (0, -0.75, 0), black, 0.4))
    for sx in (-10.5, 10.5):
        for sz in (-10.5, 10.5):
            parts.append(cyl(prefix + "post", 1.3, 4.5, (sx, -(1.5 + 2.25), sz), alu, verts=12))
    # PCB: stack grows toward the front (-Y), so the "depth" runs from y=0 (back) to y=-20 (front)
    y_pcb = -(1.5 + 4.5 + 0.8)
    parts.append(box(prefix + "pcb", (25, 1.6, 25), (0, y_pcb, 0), pcb, 0.5))
    y_face = y_pcb - 0.8
    # gold pad row on the bottom edge, small SMD parts and a driver chip on the front face
    for i in range(8):
        parts.append(box(prefix + "pad", (1.2, 0.15, 1.6), (-8.4 + i * 2.4, y_pcb + 0.8, -10.6), gold))
    for i, (px, pz) in enumerate([(-10, 9), (-7.5, 9), (9.5, 9.5), (10, -9.5), (-10, -9.5), (-10, 0), (10, 2)]):
        parts.append(box(prefix + "smd%d" % i, (1.6, 0.5, 0.9), (px, y_face - 0.25, pz), white if i % 2 else alu))
    parts.append(box(prefix + "chip", (4.0, 0.7, 4.0), (8.0, y_face - 0.35, -8.0), black, 0.2))
    # lens holder block, barrel, glass and a front ring
    y = y_face
    holder_h = 5.8
    parts.append(box(prefix + "holder", (holder, holder_h, holder), (0, y - holder_h / 2, 0), black, 0.8))
    y -= holder_h
    barrel_h = 20.0 - (-y)  # depth up to 20 mm overall (front of the barrel at y = -20)
    barrel_h = max(barrel_h, 3.0)
    parts.append(cyl(prefix + "barrel", lens_r, barrel_h, (0, y - barrel_h / 2, 0), black, verts=40))
    ring_t = 0.9
    parts.append(cyl(prefix + "ring", lens_r + 0.35, ring_t, (0, y - barrel_h + ring_t / 2, 0), alu, verts=40))
    parts.append(cyl(prefix + "glass", lens_r * 0.8, 0.5, (0, y - barrel_h - 0.05, 0), glass, verts=40))
    # flat ribbon cable leaving the bottom edge, with a connector block at its end
    rib_y = y_pcb + 0.55
    if ribbon:
        parts.append(box(prefix + "ribbon", (ribbon_w, 0.25, ribbon_len), (0, rib_y, -(12.5 + ribbon_len / 2)), amber))
        parts.append(box(prefix + "conn", (ribbon_w + 2.5, 2.0, 4.0), (0, rib_y, -(12.5 + ribbon_len + 1.5)), white, 0.3))
        parts.append(box(prefix + "tab", (ribbon_w - 3, 0.4, 1.4), (0, rib_y - 1.1, -(12.5 + ribbon_len + 1.5)), gold))
    if gimbal:
        # two-axis gimbal: a ring around the lens, a U bracket and two motor cans
        ring = mat(prefix + "gring", (0.12, 0.13, 0.15), 0.8, 0.35)
        bpy.ops.mesh.primitive_torus_add(major_radius=(lens_r + 4.0) * MM, minor_radius=0.9 * MM,
                                         major_segments=40, minor_segments=8,
                                         location=(0, (y - barrel_h / 2 + 1.5) * MM, 0), rotation=(math.pi / 2, 0, 0))
        t = bpy.context.object
        t.name = prefix + "gimbal_ring"
        t.data.materials.append(ring)
        parts.append(t)
        for sx in (-1, 1):
            parts.append(box(prefix + "arm", (1.6, 6.0, 15.5), (sx * (lens_r + 4.2), y - barrel_h / 2 + 1.5, 0), ring, 0.4))
            parts.append(cyl(prefix + "motor", 2.6, 3.0, (sx * (lens_r + 5.8), y - barrel_h / 2 + 1.5, 0), black,
                             axis_y=False, verts=20))
            parts[-1].rotation_euler = (0, math.pi / 2, 0)
    return parts


def join(parts, name):
    bpy.ops.object.select_all(action="DESELECT")
    for o in parts:
        o.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    o = bpy.context.object
    o.name = name
    return o


def tri_count(o):
    me = o.evaluated_get(bpy.context.evaluated_depsgraph_get()).to_mesh()
    n = sum(max(1, len(p.vertices) - 2) for p in me.polygons)
    o.evaluated_get(bpy.context.evaluated_depsgraph_get()).to_mesh_clear()
    return n


def reset_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete()
    for block in (bpy.data.meshes, bpy.data.materials):
        for b in list(block):
            if b.users == 0:
                block.remove(b)


def setup_render(target_center, direction, dist=0.165):
    sc = bpy.context.scene
    for engine in ("CYCLES", "BLENDER_EEVEE_NEXT", "BLENDER_EEVEE"):
        try:
            sc.render.engine = engine
            break
        except TypeError:
            continue
    if sc.render.engine == "CYCLES":
        sc.cycles.samples = 48
        sc.cycles.use_denoising = True
        sc.cycles.device = "CPU"
    sc.render.resolution_x = sc.render.resolution_y = 512
    sc.render.film_transparent = True
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGBA"
    sc.view_settings.view_transform = "Standard"
    # soft studio world: dim grey so metal and glass have something to reflect
    w = bpy.data.worlds.new("studio") if not sc.world else sc.world
    sc.world = w
    w.use_nodes = True
    bg = next(n for n in w.node_tree.nodes if n.type == "BACKGROUND")
    bg.inputs["Color"].default_value = (0.09, 0.1, 0.12, 1)
    bg.inputs["Strength"].default_value = 0.6
    cam_data = bpy.data.cameras.new("cam")
    cam_data.lens = 85
    cam = bpy.data.objects.new("cam", cam_data)
    bpy.context.collection.objects.link(cam)
    d = Vector(direction).normalized()
    cam.location = Vector(target_center) + d * dist
    empty = bpy.data.objects.new("target", None)
    empty.location = target_center
    bpy.context.collection.objects.link(empty)
    tc = cam.constraints.new("TRACK_TO")
    tc.target = empty
    tc.track_axis = "TRACK_NEGATIVE_Z"
    tc.up_axis = "UP_Y"
    sc.camera = cam

    def area(name, loc, size, energy, color=(1, 1, 1)):
        ld = bpy.data.lights.new(name, "AREA")
        ld.energy = energy
        ld.size = size
        ld.color = color
        lo = bpy.data.objects.new(name, ld)
        lo.location = Vector(target_center) + Vector(loc)
        bpy.context.collection.objects.link(lo)
        c = lo.constraints.new("TRACK_TO")
        c.target = empty
        c.track_axis = "TRACK_NEGATIVE_Z"
        c.up_axis = "UP_Y"

    area("key", (-0.12, -0.14, 0.14), 0.12, 1.1)
    area("rim", (0.16, 0.06, 0.1), 0.1, 0.9, (0.75, 0.85, 1.0))
    area("fill", (0.1, -0.15, -0.02), 0.2, 0.35)
    return sc


def render(path):
    sc = bpy.context.scene
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)


VARIANTS = [
    # name, pcb colour, lens radius mm, ribbon length mm, ribbon width, gimbal, ribbon colour
    ("cam_a", (0.02, 0.025, 0.03), 5.4, 16.0, 14.0, False, (0.9, 0.62, 0.2)),   # winner: compact black board, wide lens
    ("cam_b", (0.03, 0.30, 0.12), 3.6, 22.0, 10.0, False, (0.85, 0.55, 0.15)),  # green board, small lens, long ribbon
    ("cam_c", (0.05, 0.16, 0.45), 4.6, 12.0, 16.0, False, (0.95, 0.75, 0.3)),   # blue board, mid lens
    ("cam_d", (0.5, 0.06, 0.07), 4.2, 14.0, 12.0, True, (0.9, 0.65, 0.2)),      # red board with a gimbal
]

for name, pcb, lens_r, rl, rw, gimbal, ribbon in VARIANTS:
    reset_scene()
    parts = board(name + "_", pcb, lens_r, rl, rw, gimbal, accent=ribbon)
    obj = join(parts, name)
    # centre of the board's visual bounds for the framing
    pts = [obj.matrix_world @ Vector(c) for c in obj.bound_box]
    centre = sum(pts, Vector()) / 8
    setup_render(centre, (0.50, -0.78, 0.42))
    render(os.path.join(IMG_DIR, name + ".png"))
    print(name, "tris", tri_count(obj), "size_mm", [round(v / MM, 1) for v in obj.dimensions])
    if name == "cam_a":
        # the model has no ribbon (a mounted camera is the 25 x 25 x 20 mm module); origin = back face of the mount
        # plate, which is the world origin, and the plate is centred on x and z
        reset_scene()
        obj = join(board(name + "_", pcb, lens_r, rl, rw, gimbal, accent=ribbon, ribbon=False), "camera_a")
        bpy.ops.object.select_all(action="DESELECT")
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj
        bpy.context.scene.cursor.location = (0, 0, 0)
        bpy.ops.object.origin_set(type="ORIGIN_CURSOR")
        print("camera_a tris", tri_count(obj), "size_mm", [round(v / MM, 1) for v in obj.dimensions])
        bpy.ops.export_scene.gltf(filepath=os.path.join(MODEL_DIR, "camera_a.glb"), export_format="GLB",
                                  use_selection=True, export_apply=True)
        bpy.ops.export_scene.fbx(filepath=os.path.join(MODEL_DIR, "camera_a.fbx"), use_selection=True,
                                 apply_unit_scale=True, global_scale=1.0, apply_scale_options="FBX_SCALE_UNITS",
                                 bake_space_transform=True, axis_forward="-Z", axis_up="Y")
print("done")
