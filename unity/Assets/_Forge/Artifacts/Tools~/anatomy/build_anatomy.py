"""Builds the heart, eye, brain, lungs, kidneys and stomach GLBs for the artifact templates from open anatomy data.

    Blender -b --disable-autoexec <Z-Anatomy/Startup.blend> --python build_anatomy.py -- \
        <heart|eye|brain|lungs|kidneys|stomach> <bp3d_stl_dir> <out.glb> <out_landmarks.json>

Sources (both CC BY-SA, so the GLBs are CC BY-SA 4.0 and the app must show the credit):
- Z-Anatomy, "The libre 3D atlas of anatomy", CC BY-SA 4.0: https://github.com/Z-Anatomy/Models-of-human-anatomy
  (Z-Anatomy.zip -> Startup.blend). Chambers, valve leaflets and papillary muscles of the heart; the eye.
- BodyParts3D, (c) 2008 Life Science Integrated Database Center, CC BY-SA 2.1 Japan:
  https://github.com/Kevin-Mattheus-Moerman/BodyParts3D (assets/BodyParts3D_data/stl). The great vessels, the
  coronary arteries and veins, the mitral and tricuspid valves, and the optic nerve, which Z-Anatomy's GitHub model
  leaves out.

Z-Anatomy is BodyParts3D re-modelled, scaled and moved. We fit a similarity transform from BodyParts3D to Z-Anatomy
on parts both have (the papillary muscles and the pulmonary valve), then move the BodyParts3D meshes with it.

Both are Z-up with the body's front at -Y and its left at +X. The glTF exporter maps Blender (X, Y, Z) to glTF
(X, Z, -Y), so for the heart the front faces glTF +Z. The eye is turned so the cornea faces glTF -X: the
runtime's section (which drops the half facing the viewer) then cuts through the line of sight.

Every part gets an empty child named "explode": where the part's origin goes when the model is separated.
"""
import bpy, bmesh, json, math, os, random, sys
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

args = sys.argv[sys.argv.index("--") + 1:]
WHAT, BP3D, OUT, OUT_JSON = args

# --- materials: base colour (sRGB), roughness, alpha, double sided ---------------------------------------------------
MATS = {
    "myocardium": ((0.47, 0.12, 0.10), 0.38, 1, True),
    "atrium": ((0.55, 0.16, 0.14), 0.4, 1, True),
    "papillary": ((0.56, 0.17, 0.14), 0.42, 1, True),
    "valve": ((0.93, 0.85, 0.72), 0.55, 1, True),
    "aorta": ((0.80, 0.42, 0.36), 0.35, 1, True),
    "pulmonary_artery": ((0.36, 0.42, 0.72), 0.35, 1, True),
    "pulmonary_vein": ((0.74, 0.24, 0.22), 0.35, 1, True),
    "vena_cava": ((0.24, 0.27, 0.58), 0.35, 1, True),
    "coronary": ((0.84, 0.14, 0.11), 0.3, 1, False),
    "cardiac_vein": ((0.30, 0.30, 0.66), 0.3, 1, False),
    "sclera": ((0.93, 0.90, 0.85), 0.45, 1, True),
    "cornea": ((0.85, 0.92, 0.98), 0.05, 0.22, True),
    "aqueous": ((0.80, 0.90, 1.0), 0.05, 0.10, True),
    "iris": ((1, 1, 1), 0.5, 1, True),  # colour from vertex colours
    "lens": ((0.98, 0.92, 0.70), 0.08, 0.45, True),
    "vitreous": ((0.82, 0.90, 0.95), 0.05, 0.12, True),
    "retina": ((0.86, 0.36, 0.24), 0.5, 1, True),
    "zonule": ((0.95, 0.93, 0.85), 0.6, 1, True),
    "eye_muscle": ((0.62, 0.20, 0.17), 0.45, 1, False),
    "nerve": ((0.93, 0.88, 0.70), 0.5, 1, False),
}

zobj = bpy.data.objects
deps = bpy.context.evaluated_depsgraph_get()
built = {}  # part name -> (object, material key)


def mesh_from_z(names, part):
    """Copies Z-Anatomy objects with their modifiers applied, in world space."""
    bms = bmesh.new()
    for n in names:
        o = zobj[n]
        ev = o.evaluated_get(deps)
        me = ev.to_mesh()
        me.transform(o.matrix_world)
        bms.from_mesh(me)
        ev.to_mesh_clear()
    me = bpy.data.meshes.new(part)
    bms.to_mesh(me); bms.free()
    return me


def mesh_from_stl(fmas, part, transform):
    bms = bmesh.new()
    for f in fmas:
        before = set(bpy.data.objects)
        bpy.ops.wm.stl_import(filepath=os.path.join(BP3D, f + ".stl"))
        o = (set(bpy.data.objects) - before).pop()
        o.data.transform(o.matrix_world)
        o.data.transform(transform)
        bms.from_mesh(o.data)
        bpy.data.objects.remove(o)
    bmesh.ops.remove_doubles(bms, verts=bms.verts, dist=0.00005)
    me = bpy.data.meshes.new(part)
    bms.to_mesh(me); bms.free()
    return me


def stl_points(fmas):
    pts = []
    for f in fmas:
        before = set(bpy.data.objects)
        bpy.ops.wm.stl_import(filepath=os.path.join(BP3D, f + ".stl"))
        o = (set(bpy.data.objects) - before).pop()
        pts += [o.matrix_world @ v.co for v in o.data.vertices]
        bpy.data.objects.remove(o)
    return pts


def z_points(names):
    pts = []
    for n in names:
        o = zobj[n]
        pts += [o.matrix_world @ v.co for v in o.data.vertices]
    return pts


def mean(pts):
    return sum(pts, Vector()) / len(pts)


def fit_similarity(src, dst):
    """Umeyama: dst ~ s R src + t, from matched centroids."""
    A = np.array([list(p) for p in src]); B = np.array([list(p) for p in dst])
    ma, mb = A.mean(0), B.mean(0)
    A0, B0 = A - ma, B - mb
    U, S, Vt = np.linalg.svd(B0.T @ A0 / len(A))
    d = np.sign(np.linalg.det(U @ Vt))
    D = np.diag([1, 1, d])
    R = U @ D @ Vt
    s = np.trace(np.diag(S) @ D) / (A0 ** 2).sum(1).mean()
    t = mb - s * R @ ma
    M = Matrix.Identity(4)
    for i in range(3):
        for j in range(3):
            M[i][j] = s * R[i, j]
        M[i][3] = t[i]
    err = np.linalg.norm((s * (R @ A.T)).T + t - B, axis=1)
    return M, s, err


def bp3d_to_z():
    pairs = [(["FMA7262"], ["Septal papillary muscle of right ventricle"]),
             (["FMA7260"], ["Anterior papillary muscle of right ventricle"]),
             (["FMA7261"], ["Inferior papillary muscle of right ventricle"]),
             (["FMA7266"], ["Inferior papillary muscle of left ventricle"]),
             (["FMA7246"], ["Anterior semilunar leaflet of pulmonary valve", "Left semilunar leaflet of pulmonary valve",
                            "Right semilunar leaflet of pulmonary valve"])]
    src = [mean(stl_points(a)) * 0.001 for a, _ in pairs]  # BodyParts3D is millimetres
    dst = [mean(z_points(b)) for _, b in pairs]
    M, s, err = fit_similarity(src, dst)
    print("FIT scale", round(s, 4), "residual mm", [round(e * 1000, 2) for e in err])
    return M @ Matrix.Scale(0.001, 4)


def crop(me, centre, half):
    bm = bmesh.new(); bm.from_mesh(me)
    for axis in range(3):
        for sign in (1, -1):
            n = Vector((0, 0, 0)); n[axis] = sign
            bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=centre + n * half,
                                   plane_no=n, clear_outer=True)
    bm.to_mesh(me); bm.free()


def drop_islands(me, min_faces):
    """Removes loose bits (cropping leaves stubs of vessels that left and re-entered the box)."""
    bm = bmesh.new(); bm.from_mesh(me)
    seen = set(); drop = []
    for f in bm.faces:
        if f.index in seen:
            continue
        island = []; stack = [f]; seen.add(f.index)
        while stack:
            g = stack.pop(); island.append(g)
            for e in g.edges:
                for h in e.link_faces:
                    if h.index not in seen:
                        seen.add(h.index); stack.append(h)
        if len(island) < min_faces:
            drop += island
    bmesh.ops.delete(bm, geom=drop, context="FACES")
    bm.to_mesh(me); bm.free()


def tris(me):
    return sum(len(p.vertices) - 2 for p in me.polygons)


def add(part, me, mat, target):
    clash = bpy.data.objects.get(part)
    if clash is not None:  # a Z-Anatomy object has this name; move it aside so ours exports without ".001"
        clash.name = part + " (Z-Anatomy)"
    o = bpy.data.objects.new(part, me)
    assert o.name == part, o.name
    bpy.context.collection.objects.link(o)
    if tris(me) > target:
        m = o.modifiers.new("dec", "DECIMATE"); m.ratio = target / tris(me); m.use_collapse_triangulate = True
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.select_all(action="DESELECT"); o.select_set(True)
        bpy.ops.object.modifier_apply(modifier="dec")
    built[part] = (o, mat)
    return o


def material(key):
    if key in bpy.data.materials:
        return bpy.data.materials[key]
    (r, g, b), rough, alpha, double = MATS[key]
    m = bpy.data.materials.new(key)
    m.use_nodes = True
    bsdf = m.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (*(c ** 2.2 for c in (r, g, b)), 1)
    bsdf.inputs["Roughness"].default_value = rough
    if alpha < 1:
        bsdf.inputs["Alpha"].default_value = alpha
        for prop, val in (("surface_render_method", "BLENDED"), ("blend_method", "BLEND")):
            if hasattr(m, prop):
                try: setattr(m, prop, val)
                except Exception: pass
    m.use_backface_culling = not double
    return m


def bake_colours(scale, colour_fn=None, samples=20, reach=0.012, self_only=()):
    """Ambient occlusion (times an optional per-vertex colour) into COLOR_0, which glTF multiplies into the base colour."""
    tmp = []
    for o, _ in built.values():
        c = o.copy(); c.data = o.data.copy(); bpy.context.collection.objects.link(c); tmp.append(c)
    bpy.ops.object.select_all(action="DESELECT")
    for c in tmp: c.select_set(True)
    bpy.context.view_layer.objects.active = tmp[0]
    bpy.ops.object.join()
    allo = bpy.context.view_layer.objects.active
    bvh = BVHTree.FromObject(allo, bpy.context.evaluated_depsgraph_get())
    rng = random.Random(7)
    dirs = []
    while len(dirs) < samples:
        v = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1)))
        if 0.05 < v.length <= 1: dirs.append(v.normalized())
    for part, (o, mat) in built.items():
        me = o.data
        # A part that something else wraps (white matter under the cortex) is shaded by its own folds only.
        tree = BVHTree.FromObject(o, bpy.context.evaluated_depsgraph_get()) if part in self_only else bvh
        layer = me.color_attributes.new("Col", "BYTE_COLOR", "POINT")
        skip_ao = MATS[mat][2] < 1  # see-through parts don't occlude themselves
        for i, v in enumerate(me.vertices):
            a = 1.0
            if not skip_ao:
                hits = 0
                for d in dirs:
                    if d.dot(v.normal) < 0: d = -d
                    if tree.ray_cast(v.co + v.normal * reach * 0.03, d, reach)[0] is not None: hits += 1
                a = max(0.4, 1.0 - 0.8 * hits / samples)
            col = colour_fn(part, v.co) if colour_fn else (1, 1, 1)
            layer.data[i].color = (col[0] * a, col[1] * a, col[2] * a, 1.0)
        me.color_attributes.active_color = layer
    bpy.data.objects.remove(allo)


def noise3(p, f):
    # cheap value noise for colour mottling, deterministic
    return 0.5 + 0.5 * math.sin(p.x * f * 1.7 + math.sin(p.y * f * 2.3)) * math.sin(p.y * f * 1.3 + math.sin(p.z * f * 1.9)) * math.sin(p.z * f * 1.1 + math.sin(p.x * f * 2.9))


def finish(centre, rotation, explode, landmarks, xray, narrow=False):
    """Moves everything so centre is the origin, turns it, sets part origins, adds explode empties, exports."""
    report = {"parts": {}, "landmarks": {}}
    to_out = rotation @ Matrix.Translation(-centre)
    for part, (o, mat) in built.items():
        me = o.data
        me.transform(to_out)
        me.materials.clear(); me.materials.append(material(mat))
        for p in me.polygons: p.use_smooth = True
        c = mean([v.co for v in me.vertices])
        # Anchor for a label's leader line: the front-most point near the part's middle (front = Blender -Y).
        spread = max((v.co - c).length for v in me.vertices)
        # Copies: v.co follows the mesh, and the mesh moves to its own origin below (an anchor must stay in model space).
        near = [v.co.copy() for v in me.vertices if Vector((v.co.x - c.x, 0, v.co.z - c.z)).length < spread * 0.3] or [c]
        anchor = min(near, key=lambda q: q.y)
        if narrow:  # a long or wrapped part (a lung lobe): the front surface straight in front of its centre
            xs = [v.co.x for v in me.vertices]; zs = [v.co.z for v in me.vertices]
            wx, wz = (max(xs) - min(xs)) * 0.12, (max(zs) - min(zs)) * 0.12
            column = [v.co.copy() for v in me.vertices if abs(v.co.x - c.x) < wx and abs(v.co.z - c.z) < wz]
            if column:
                anchor = min(column, key=lambda q: q.y)
        me.transform(Matrix.Translation(-c)); o.location = c
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.select_all(action="DESELECT"); o.select_set(True)
        bpy.ops.object.shade_auto_smooth(angle=math.radians(55))
        e = bpy.data.objects.new("explode", None)
        bpy.context.collection.objects.link(e)
        e.parent = o
        e.location = Vector(explode(part, c))
        if part in xray:  # see-through in the runtime's X-ray view
            x = bpy.data.objects.new("xray", None)
            bpy.context.collection.objects.link(x)
            x.parent = o
        report["parts"][part] = {"tris": tris(me), "material": mat, "centre": gl(c), "anchor": gl(anchor), "explode": gl(Vector(explode(part, c)))}
    for k, p in landmarks.items():
        report["landmarks"][k] = gl(to_out @ p)
    # Only our parts and their explode markers, not the rest of the Z-Anatomy body.
    bpy.ops.object.select_all(action="DESELECT")
    for o, _ in built.values():
        o.select_set(True)
        for ch in o.children: ch.select_set(True)
    bpy.ops.export_scene.gltf(filepath=OUT, export_format="GLB", export_apply=True, export_normals=True,
                              export_vertex_color="ACTIVE", export_texcoords=False, export_materials="EXPORT",
                              export_yup=True, export_extras=False, use_selection=True)
    report["total_tris"] = sum(p["tris"] for p in report["parts"].values())
    report["bytes"] = os.path.getsize(OUT)
    with open(OUT_JSON, "w") as f: json.dump(report, f, indent=1)
    print("BUILD_OK", WHAT, report["total_tris"], report["bytes"])


def gl(p):
    return [round(p.x, 5), round(p.z, 5), round(-p.y, 5)]


# --- the heart ------------------------------------------------------------------------------------------------------
def heart():
    M = bp3d_to_z()
    Z = {
        "Right atrium": (["Right atrium"], "atrium", 16000),
        "Left atrium": (["Left atrium"], "atrium", 16000),
        "Right ventricle": (["Right ventricle"], "myocardium", 18000),
        "Left ventricle": (["Left ventricle"], "myocardium", 18000),
        "Pulmonary valve": (["Anterior semilunar leaflet of pulmonary valve", "Left semilunar leaflet of pulmonary valve",
                             "Right semilunar leaflet of pulmonary valve"], "valve", 2500),
        "Aortic valve": (["Left coronary leaflet", "Right coronary leaflet", "Non-coronary leaflet"], "valve", 2000),
        "Papillary muscles": (["Anterior papillary muscle of right ventricle", "Inferior papillary muscle of right ventricle",
                               "Septal papillary muscle of right ventricle", "Inferior papillary muscle of left ventricle"],
                              "papillary", 4000),
    }
    B = {
        "Tricuspid valve": (["FMA7234"], "valve", 4000, None),
        "Mitral valve": (["FMA7235"], "valve", 3000, None),
        "Aorta": (["FMA3736", "FMA3768", "FMA3784"], "aorta", 6000, 0.07),
        "Pulmonary arteries": (["FMA66326"], "pulmonary_artery", 6000, 0.07),
        "Pulmonary veins": (["FMA66643"], "pulmonary_vein", 5000, 0.064),
        "Superior vena cava": (["FMA4720"], "vena_cava", 1500, 0.08),
        "Inferior vena cava": (["FMA10951"], "vena_cava", 1500, 0.062),
        "Right coronary artery": (["FMA3802", "FMA3818", "FMA3840nsn", "FMA76994", "FMA71669"], "coronary", 4000, None),
        "Left coronary artery": (["FMA4685", "FMA3862nsn", "FMA3895", "FMA71670"], "coronary", 4000, None),
        "Cardiac veins": (["FMA4706", "FMA4707", "FMA4713", "FMA71567", "FMA76751"], "cardiac_vein", 5000, None),
    }
    chambers = ["Right atrium", "Left atrium", "Right ventricle", "Left ventricle"]
    centre = mean(z_points(chambers))
    for part, (names, mat, target) in Z.items():
        add(part, mesh_from_z(names, part), mat, target)
    for part, (fmas, mat, target, box) in B.items():
        me = mesh_from_stl(fmas, part, M)
        if box:
            crop(me, centre, box)
            drop_islands(me, 60)
        add(part, me, mat, target)

    def colour(part, p):
        if MATS[built[part][1]][2] < 1: return (1, 1, 1)
        n = noise3(p, 900.0)
        k = 0.86 + 0.14 * n
        return (k, k * (0.96 + 0.04 * n), k)
    bake_colours(1.0, colour)

    lm = {}
    for k, part in (("mitral", "Mitral valve"), ("tricuspid", "Tricuspid valve"), ("pulmonary_valve", "Pulmonary valve"),
                    ("aortic_valve", "Aortic valve"), ("ra", "Right atrium"), ("la", "Left atrium"),
                    ("rv", "Right ventricle"), ("lv", "Left ventricle")):
        lm[k] = mean([v.co for v in built[part][0].data.vertices])
    # Vessel ends inside the crop box: the point of each vessel farthest from the heart centre.
    for k, part in (("svc_top", "Superior vena cava"), ("ivc_bottom", "Inferior vena cava"), ("aorta_far", "Aorta"),
                    ("pa_far", "Pulmonary arteries"), ("pv_far", "Pulmonary veins")):
        lm[k] = max((v.co for v in built[part][0].data.vertices), key=lambda q: (q - centre).length)
    # Points along the vessels, sorted by height, for the blood path.
    for k, part in (("svc", "Superior vena cava"), ("ivc", "Inferior vena cava"), ("aorta", "Aorta"),
                    ("pa", "Pulmonary arteries"), ("pv", "Pulmonary veins")):
        vs = [v.co for v in built[part][0].data.vertices]
        lm[k] = mean(vs)
        lm[k + "_top"] = max(vs, key=lambda q: q.z)
        lm[k + "_bottom"] = min(vs, key=lambda q: q.z)
    apex = max((v.co for v in built["Left ventricle"][0].data.vertices), key=lambda q: (q - (lm["mitral"] + lm["tricuspid"]) / 2).length)
    lm["apex"] = apex

    # Separated layout, in output space (glTF +Y up, +Z toward the viewer; Blender here is +Z up, -Y toward the viewer).
    up, front, left = Vector((0, 0, 1)), Vector((0, -1, 0)), Vector((1, 0, 0))
    def explode(part, c):
        radial = Vector((c.x, c.y, c.z)); d = radial.length
        r = radial.normalized() if d > 1e-6 else up
        # Atria up and out, ventricles down and out, great vessels on top, valves in a row in front of the heart,
        # coronary arteries peeled forward to their own side, cardiac veins behind.
        table = {
            "Right atrium": -left * 0.055 + up * 0.04, "Left atrium": left * 0.05 + up * 0.04 - front * 0.03,
            "Right ventricle": -left * 0.035 - up * 0.045 + front * 0.015, "Left ventricle": left * 0.05 - up * 0.055,
            "Aorta": up * 0.1 - front * 0.025, "Pulmonary arteries": up * 0.085 + front * 0.02 + left * 0.015,
            "Pulmonary veins": up * 0.035 - front * 0.08 + left * 0.03,
            "Superior vena cava": up * 0.085 - left * 0.065, "Inferior vena cava": -up * 0.075 - left * 0.065,
            "Tricuspid valve": front * 0.11 - left * 0.04, "Mitral valve": front * 0.11 + left * 0.035,
            "Pulmonary valve": front * 0.11 + left * 0.01 + up * 0.035,
            "Aortic valve": front * 0.11 - left * 0.02 + up * 0.03,
            "Papillary muscles": front * 0.09 - up * 0.075,
            "Right coronary artery": front * 0.045 - left * 0.085, "Left coronary artery": front * 0.045 + left * 0.085,
            "Cardiac veins": -front * 0.08 - up * 0.03,
        }
        return table.get(part, r * 0.05)
    xray = {"Right atrium", "Left atrium", "Right ventricle", "Left ventricle", "Aorta", "Pulmonary arteries",
            "Pulmonary veins", "Superior vena cava", "Inferior vena cava", "Right coronary artery", "Left coronary artery",
            "Cardiac veins"}
    finish(centre, Matrix.Identity(4), explode, lm, xray)


# --- the eye --------------------------------------------------------------------------------------------------------
def eye():
    side = "r"
    Z = {
        "Sclera": (["Sclera"], "sclera", 6000),
        "Cornea": (["Cornea"], "cornea", 3000),
        "Aqueous humour": (["Anterior chamber of eyeball"], "aqueous", 2000),
        "Iris": (["Iris"], "iris", 7000),
        "Lens": (["Lens"], "lens", 3000),
        "Zonular fibres": (["Zonular fibres"], "zonule", 5000),
        "Vitreous body": (["Vitreous body"], "vitreous", 5000),
        "Retina": (["Retina"], "retina", 6000),
        "Superior rectus": (["Superior rectus muscle"], "eye_muscle", 2500),
        "Inferior rectus": (["Inferior rectus muscle"], "eye_muscle", 2500),
        "Medial rectus": (["Medial rectus muscle"], "eye_muscle", 2500),
        "Lateral rectus": (["Lateral rectus muscle"], "eye_muscle", 2500),
        "Superior oblique": (["Superior oblique muscle", "Trochlea of superior oblique muscle"], "eye_muscle", 2500),
        "Inferior oblique": (["Inferior oblique muscle"], "eye_muscle", 2000),
    }
    for part, (names, mat, target) in Z.items():
        add(part, mesh_from_z([f"{n}.{side}" for n in names], part), mat, target)
    sclera = built["Sclera"][0].data
    centre = mean([v.co for v in sclera.vertices])
    cornea_c = mean([v.co for v in built["Cornea"][0].data.vertices])
    axis = (cornea_c - centre).normalized()          # the line of sight, pointing out of the cornea

    # Optic nerve from BodyParts3D: move it with the heart fit, then nudge it so its eyeball lines up with the sclera.
    M = bp3d_to_z()
    eyes = [M @ p for p in stl_points(["FMA12513"])]
    right = [p for p in eyes if p.x < sum(q.x for q in eyes) / len(eyes)] if side == "r" else [p for p in eyes if p.x >= sum(q.x for q in eyes) / len(eyes)]
    nudge = centre - mean(right)
    me = mesh_from_stl(["FMA50875" if side == "r" else "FMA50878"], "Optic nerve", Matrix.Translation(nudge) @ M)
    crop(me, centre - axis * 0.02, 0.022)
    # BodyParts3D's nerve starts inside its own eyeball; keep only what's behind Z-Anatomy's sclera.
    radius = sum((v.co - centre).length for v in sclera.vertices) / len(sclera.vertices)
    bm = bmesh.new(); bm.from_mesh(me)
    inside = [f for f in bm.faces if (f.calc_center_median() - centre).length < radius * 0.97]
    bmesh.ops.delete(bm, geom=inside, context="FACES")
    bm.to_mesh(me); bm.free()
    drop_islands(me, 40)
    add("Optic nerve", me, "nerve", 3000)

    def colour(part, p):
        if part != "Iris": return (1, 1, 1)
        q = p - cornea_c
        rad = (q - axis * q.dot(axis)).length * 1000  # mm from the line of sight
        ref = axis.orthogonal().normalized(); ref2 = axis.cross(ref)
        ang = math.atan2(q.dot(ref2), q.dot(ref))
        streak = 0.5 + 0.5 * math.sin(ang * 61) * math.sin(ang * 23 + rad * 1.7)
        base = (0.36, 0.22, 0.12)       # brown
        light = (0.62, 0.45, 0.25)
        t = 0.35 * streak + (0.35 if 2.4 < rad < 3.4 else 0.0)  # collarette ring
        col = [base[i] * (1 - t) + light[i] * t for i in range(3)]
        if rad > 5.6: col = [c * 0.55 for c in col]              # dark limbal ring
        if rad < 2.0: col = [c * 0.6 for c in col]               # pupillary ruff
        return col
    bake_colours(1.0, colour, reach=0.006)

    # Turn the eye so the line of sight is Blender -X (glTF -X), keeping up as up.
    fwd = Vector((-1, 0, 0))
    rot = axis.rotation_difference(fwd).to_matrix().to_4x4()
    lm = {"cornea": cornea_c, "centre": centre, "lens": mean([v.co for v in built["Lens"][0].data.vertices]),
          "optic_disc": centre - axis * 0.0115, "nerve_far": centre - axis * 0.04}

    def explode(part, c):
        along = {"Cornea": -0.03, "Aqueous humour": -0.022, "Iris": -0.015, "Lens": -0.008, "Zonular fibres": -0.003,
                 "Vitreous body": 0.005, "Retina": 0.013, "Sclera": 0.022, "Optic nerve": 0.034}
        if part in along:
            return Vector((along[part], 0, 0))  # negative = out through the cornea (output -X)
        radial = Vector((0, c.y, c.z))
        return radial.normalized() * 0.014 + Vector((0.006, 0, 0)) if radial.length > 1e-6 else Vector((0, 0, 0.014))
    xray = {"Sclera", "Retina", "Optic nerve", "Superior rectus", "Inferior rectus", "Medial rectus", "Lateral rectus",
            "Superior oblique", "Inferior oblique"}
    finish(centre, rot, explode, lm, xray)


# --- the brain ------------------------------------------------------------------------------------------------------
# Z-Anatomy splits the cortex into gyri and sulci; these rules group them into lobes, first match wins.
BRAIN_SKIP = ("nerve", "salivatory", "ambiguus", "cochlear nucleus", "vestibular nuclei", "nucleus of", "eyeball", "cornea",
              "iris", "lens", "retina", "sclera", "vitreous", "zonular", "lacrimal", "cochlea", "vestibule", "auditory",
              "tympanic", "falx", "tentorium", "suspensory", "optic", "septal nuclei", "stria", "habenula", "commissure",
              "interpeduncular", "geniculate", "mamillary", "choroid plexus", "septum pellucidum", "root", "lentiform")
BRAIN_RULES = [
    ("White matter", ("white matter",)),
    ("Limbic lobe", ("cingulate", "parahippocampal")),
    ("Insula", ("insula",)),
    ("Temporal lobe", ("occipitotemporal", "collateral sulcus", "temporal", "lat_fis-post")),
    ("Occipital lobe", ("parieto-occipital", "occipital", "cuneus", "lingual", "calcarine", "lunate")),
    ("Parietal lobe", ("parietal", "postcentral", "supramarginal", "angular", "precuneus", "jensen")),
    ("Frontal lobe", ("frontal", "precentral", "central sulcus", "orbital", "straight gyrus", "olfactory", "paracentral",
                      "opercular", "triangular", "lat_fis-ant", "frontopolar")),
    ("Cerebellum", ("lobule", "vermis", "culmen", "declive", "flocculus", "lingula", "tonsil", "cerebellar peduncle")),
    ("Brainstem", ("medulla", "pons", "midbrain", "olive", "colliculus", "base of peduncle", "red nucleus")),
    ("Ventricles", ("ventricle", "aqueduct")),
    ("Corpus callosum", ("corpus callosum",)),
    ("Thalamus", ("thalamus",)),
    ("Basal ganglia", ("caudate", "putamen", "globus pallidus")),
    ("Hippocampus and fornix", ("hippocampus", "fornix")),
    ("Amygdala", ("amygdaloid",)),
]
SIDED = {"White matter", "Limbic lobe", "Insula", "Temporal lobe", "Occipital lobe", "Parietal lobe", "Frontal lobe"}
MATS.update({
    "frontal": ((0.80, 0.60, 0.53), 0.55, 1, True), "parietal": ((0.83, 0.68, 0.54), 0.55, 1, True),
    "temporal": ((0.74, 0.60, 0.64), 0.55, 1, True), "occipital": ((0.82, 0.57, 0.57), 0.55, 1, True),
    "insula": ((0.78, 0.65, 0.58), 0.55, 1, True), "limbic": ((0.86, 0.68, 0.60), 0.55, 1, True),
    "white_matter": ((0.93, 0.90, 0.83), 0.6, 1, True), "cerebellum": ((0.76, 0.54, 0.49), 0.55, 1, True),
    "brainstem": ((0.86, 0.76, 0.66), 0.55, 1, True), "ventricles": ((0.45, 0.65, 0.90), 0.1, 0.55, True),
    "callosum": ((0.95, 0.93, 0.87), 0.6, 1, True), "thalamus": ((0.72, 0.56, 0.62), 0.55, 1, True),
    "basal_ganglia": ((0.62, 0.50, 0.60), 0.55, 1, True), "hippocampus": ((0.86, 0.72, 0.54), 0.55, 1, True),
    "amygdala": ((0.82, 0.58, 0.44), 0.55, 1, True),
})
BRAIN_MAT = {"Frontal lobe": "frontal", "Parietal lobe": "parietal", "Temporal lobe": "temporal",
             "Occipital lobe": "occipital", "Insula": "insula", "Limbic lobe": "limbic", "White matter": "white_matter",
             "Cerebellum": "cerebellum", "Brainstem": "brainstem", "Ventricles": "ventricles",
             "Corpus callosum": "callosum", "Thalamus": "thalamus", "Basal ganglia": "basal_ganglia",
             "Hippocampus and fornix": "hippocampus", "Amygdala": "amygdala"}
BRAIN_TRIS = {"White matter": 6000, "Cerebellum": 10000, "Brainstem": 4000, "Ventricles": 3000, "Frontal lobe": 8000,
              "Parietal lobe": 6000, "Temporal lobe": 6000, "Occipital lobe": 4000, "Insula": 1500, "Limbic lobe": 2500}


def brain():
    ref = zobj["Corpus callosum"]
    centre_ref = ref.matrix_world @ (sum((Vector(b) for b in ref.bound_box), Vector()) / 8)
    groups = {}
    for o in list(zobj):
        if o.type != "MESH" or len(o.data.polygons) == 0 or not o.users_collection:
            continue
        if "Nervous" not in o.users_collection[0].name:
            continue
        c = o.matrix_world @ (sum((Vector(b) for b in o.bound_box), Vector()) / 8)
        if (c - centre_ref).length > 0.11:
            continue
        low = o.name.lower()
        if any(s in low for s in BRAIN_SKIP) and not any(k in low for k in ("caudate", "red nucleus")):
            continue
        part = next((p for p, keys in BRAIN_RULES if any(k in low for k in keys)), None)
        if part is None:
            print("BRAIN unassigned:", o.name)
            continue
        if part in SIDED:
            side = "Left" if o.name.endswith(".l") else "Right" if o.name.endswith(".r") else None
            if side is None:
                print("BRAIN no side:", o.name); continue
            part = f"{side} {part[0].lower()}{part[1:]}"
        groups.setdefault(part, []).append(o.name)
    for part, names in sorted(groups.items()):
        print("BRAIN", part, len(names))
        base = part.split(" ", 1)[1].capitalize() if part.startswith(("Left ", "Right ")) else part
        add(part, mesh_from_z(names, part), BRAIN_MAT[base], BRAIN_TRIS.get(base, 3000))

    centre = mean([v.co for o, _ in built.values() for v in o.data.vertices])
    def colour(part, p):
        n = noise3(p, 700.0)
        k = 0.9 + 0.1 * n
        return (k, k, k)
    bake_colours(1.0, colour, reach=0.01, self_only={p for p in built if "white matter" in p})

    # Patient's left toward glTF +Z (Blender -Y): the runtime's cut then goes down the midline.
    rot = Matrix.Rotation(math.radians(-90), 4, "Z")
    lm = {}
    for part, (o, _) in built.items():
        lm[part] = mean([v.co for v in o.data.vertices])

    def explode(part, c):
        # Output space: front -X, top +Z, patient's left -Y (toward the viewer).
        side = -1 if part.startswith("Left") else 1 if part.startswith("Right") else 0
        radial = Vector((c.x, 0, c.z))
        r = radial.normalized() if radial.length > 1e-6 else Vector((0, 0, 1))
        if "white matter" in part:
            return Vector((0, side * 0.03, 0))
        if side:
            return r * 0.035 + Vector((0, side * 0.06, 0))
        table = {"Cerebellum": Vector((0.045, 0, -0.04)), "Brainstem": Vector((0.0, 0, -0.08)),
                 "Corpus callosum": Vector((0, 0, 0.06)), "Ventricles": Vector((0, 0, 0.035)),
                 "Thalamus": Vector((0.0, 0, 0.0)), "Basal ganglia": Vector((-0.03, 0, 0.015)),
                 "Hippocampus and fornix": Vector((0.03, 0, -0.025)), "Amygdala": Vector((-0.035, 0, -0.04))}
        return table.get(part, r * 0.03)
    xray = {p for p in built if "lobe" in p or "Insula" in p.capitalize() or p.endswith("insula") or "white matter" in p}
    finish(centre, rot, explode, lm, xray)


# --- lungs, kidneys and stomach ------------------------------------------------------------------------------------
# BodyParts3D only, except the kidneys' renal pelvis (Z-Anatomy). BodyParts3D is millimetres on Z-Anatomy's axes
# (patient's left +X, front -Y, up +Z), so MM puts its meshes in the same space. All three face glTF +Z, like the heart.
MM = Matrix.Scale(0.001, 4)
MATS.update({
    "lung_ru": ((0.88, 0.60, 0.60), 0.6, 1, True), "lung_rm": ((0.84, 0.54, 0.57), 0.6, 1, True),
    "lung_rl": ((0.81, 0.52, 0.53), 0.6, 1, True), "lung_lu": ((0.87, 0.58, 0.61), 0.6, 1, True),
    "lung_ll": ((0.82, 0.53, 0.55), 0.6, 1, True), "cartilage": ((0.94, 0.89, 0.81), 0.5, 1, True),
    "bronchi": ((0.92, 0.82, 0.73), 0.5, 1, True),
    "kidney": ((0.56, 0.21, 0.18), 0.42, 1, True), "renal_pelvis": ((0.94, 0.82, 0.55), 0.5, 1, True),
    "adrenal": ((0.91, 0.69, 0.31), 0.5, 1, True), "ureter": ((0.92, 0.84, 0.64), 0.45, 1, True),
    "bladder": ((0.89, 0.71, 0.57), 0.5, 1, True),
    "fundus": ((0.87, 0.57, 0.52), 0.45, 1, True), "stomach_body": ((0.85, 0.53, 0.49), 0.45, 1, True),
    "antrum": ((0.82, 0.50, 0.47), 0.45, 1, True), "pylorus": ((0.76, 0.43, 0.41), 0.45, 1, True),
    "rugae": ((0.94, 0.62, 0.62), 0.55, 1, True), "oesophagus": ((0.86, 0.60, 0.53), 0.45, 1, True),
    "duodenum": ((0.89, 0.67, 0.55), 0.45, 1, True),
})


def bp3d(fmas, part, islands=30):
    me = mesh_from_stl(fmas, part, MM)
    if islands:
        drop_islands(me, islands)
    return me


def crop_z(me, lo, hi):
    """Keeps the part of a mesh between heights lo and hi (metres): a vessel cut close to the organ."""
    bm = bmesh.new(); bm.from_mesh(me)
    for z, n in ((hi, Vector((0, 0, 1))), (lo, Vector((0, 0, -1)))):
        bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=Vector((0, 0, z)),
                               plane_no=n, clear_outer=True)
    bm.to_mesh(me); bm.free()


def verts(part):
    return [v.co.copy() for v in built[part][0].data.vertices]


def box_centre(parts):
    pts = [p for part in parts for p in verts(part)]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return (lo + hi) / 2


def along_z(pts, n):
    """n points down a tube, top to bottom: the mean of each height slice (a path for a tour to follow)."""
    lo, hi = min(p.z for p in pts), max(p.z for p in pts)
    out = []
    for i in range(n):
        a, b = hi - (hi - lo) * i / n, hi - (hi - lo) * (i + 1) / n
        band = [p for p in pts if b <= p.z <= a]
        if band:
            out.append(mean(band))
    return out


def nearest_to(pts, target):
    return min(pts, key=lambda q: (q - target).length)


def mottle(scale):
    def colour(part, p):
        k = 0.9 + 0.1 * noise3(p, scale)
        return (k, k, k)
    return colour


LUNG_PARTS = [("Right upper lobe", ["FMA7333"], "lung_ru", 11000), ("Right middle lobe", ["FMA7383"], "lung_rm", 7000),
              ("Right lower lobe", ["FMA7337"], "lung_rl", 12000), ("Left upper lobe", ["FMA7370"], "lung_lu", 12000),
              ("Left lower lobe", ["FMA7371"], "lung_ll", 12000), ("Trachea", ["FMA7394"], "cartilage", 5000),
              ("Bronchi", ["FMA7409"], "bronchi", 20000), ("Pulmonary arteries", ["FMA66326"], "pulmonary_artery", 12000),
              ("Pulmonary veins", ["FMA66643"], "pulmonary_vein", 10000)]
LOBES = [p for p, *_ in LUNG_PARTS if "lobe" in p]


def lungs():
    for part, fmas, mat, target in LUNG_PARTS:
        add(part, bp3d(fmas, part), mat, target)
    centre = box_centre(LOBES)
    bake_colours(1.0, mottle(300.0), reach=0.015)
    trachea = verts("Trachea")
    lm = {"trachea_top": mean([p for p in trachea if p.z > max(q.z for q in trachea) - 0.006]),
          "carina": mean([p for p in trachea if p.z < min(q.z for q in trachea) + 0.006])}
    bronchi = verts("Bronchi")
    car = lm["carina"]
    for side, sign in (("right", -1), ("left", 1)):  # main bronchi: just below and to each side of the carina
        near = [p for p in bronchi if sign * (p.x - car.x) > 0.012 and (p - car).length < 0.045 and p.z < car.z]
        lm[side + "_bronchus"] = mean(near) if near else car + Vector((sign * 0.025, 0, -0.015))
    for part in LOBES:
        lm[part] = mean(verts(part))

    def explode(part, c):
        # Output space: patient's left +X, front -Y, up +Z. Lobes spread sideways and apart; airways and vessels stay.
        side = 1 if part.startswith("Left") else -1 if part.startswith("Right") else 0
        if part in LOBES:
            up = (0.045 if side < 0 else 0.065) if "upper" in part else (-0.045 if side < 0 else -0.06) if "lower" in part else 0.0
            return Vector((side * 0.07, -0.04 if "middle" in part else 0.0, up))
        return {"Trachea": Vector((0, 0, 0.05)), "Bronchi": Vector((0, 0, 0)),
                "Pulmonary arteries": Vector((0, -0.07, 0.03)), "Pulmonary veins": Vector((0, -0.07, -0.04))}[part]
    finish(centre, Matrix.Identity(4), explode, lm, set(LOBES), narrow=True)


KIDNEY_PARTS = [("Left kidney", ["FMA7205"], "kidney", 10000), ("Right kidney", ["FMA7204"], "kidney", 10000),
                ("Left adrenal gland", ["FMA15630"], "adrenal", 3500), ("Right adrenal gland", ["FMA15629"], "adrenal", 3500),
                ("Left ureter", ["FMA15572"], "ureter", 2500), ("Right ureter", ["FMA15571"], "ureter", 2500),
                ("Left renal artery", ["FMA14753"], "aorta", 1500), ("Right renal artery", ["FMA14752"], "aorta", 1800),
                ("Left renal vein", ["FMA14336"], "vena_cava", 1200), ("Right renal vein", ["FMA14335"], "vena_cava", 1600),
                ("Urinary bladder", ["FMA15900"], "bladder", 4000)]


def kidneys():
    for part, fmas, mat, target in KIDNEY_PARTS:
        add(part, bp3d(fmas, part), mat, target)
    lo_z, hi_z = 0.955, 1.165  # the aorta and vena cava from just below the kidneys to above the adrenal glands
    for part, fma, mat, target in (("Aorta", "FMA3784", "aorta", 5000), ("Inferior vena cava", "FMA10951", "vena_cava", 4000)):
        me = bp3d([fma], part, islands=0)
        crop_z(me, lo_z, hi_z)
        drop_islands(me, 40)
        add(part, me, mat, target)
    # The renal pelvis (the funnel inside each kidney that collects urine) is Z-Anatomy's: fit Z-Anatomy to
    # BodyParts3D on the kidneys and adrenal glands, then move the pelvis with that fit's inverse.
    pairs = [(["FMA7205"], ["Kidney.l"]), (["FMA7204"], ["Kidney.r"]), (["FMA15630"], ["Suprarenal gland.l"]),
             (["FMA15629"], ["Suprarenal gland.r"])]
    src = [mean(stl_points(a)) * 0.001 for a, _ in pairs]
    dst = [mean(z_points(b)) for _, b in pairs]
    M, s, err = fit_similarity(src, dst)
    print("FIT kidneys scale", round(s, 4), "residual mm", [round(e * 1000, 2) for e in err])
    for side, name in (("Left", "Renal pelvis.l"), ("Right", "Renal pelvis.r")):
        me = mesh_from_z([name], side + " renal pelvis")
        me.transform(M.inverted())
        add(side + " renal pelvis", me, "renal_pelvis", 1500)
    centre = box_centre(["Left kidney", "Right kidney", "Urinary bladder", "Left adrenal gland", "Right adrenal gland"])
    bake_colours(1.0, mottle(400.0), reach=0.012)
    lm = {}
    aorta, ivc = verts("Aorta"), verts("Inferior vena cava")
    for side in ("Left", "Right"):
        k = side.lower()
        artery, vein = verts(side + " renal artery"), verts(side + " renal vein")
        kidney = mean(verts(side + " kidney"))
        lm["aorta_" + k] = nearest_to(aorta, mean(artery))
        lm["artery_" + k] = mean(artery)
        lm["hilum_" + k] = nearest_to(artery, kidney)
        lm["kidney_" + k] = kidney
        lm["vein_" + k] = mean(vein)
        lm["ivc_" + k] = nearest_to(ivc, mean(vein))
        lm["pelvis_" + k] = mean(verts(side + " renal pelvis"))
        for i, p in enumerate(along_z(verts(side + " ureter"), 6)):
            lm[f"ureter_{k}_{i}"] = p
    lm["aorta_top"] = mean([p for p in aorta if p.z > max(q.z for q in aorta) - 0.005])
    lm["bladder"] = mean(verts("Urinary bladder"))

    def explode(part, c):
        side = 1 if part.startswith("Left") else -1 if part.startswith("Right") else 0
        if part.endswith("kidney"):
            return Vector((side * 0.07, 0, 0))  # out to the sides: the renal pelvis stays, uncovered
        if "adrenal" in part:
            return Vector((side * 0.03, 0, 0.05))
        if "artery" in part:
            return Vector((0, -0.04, 0.015))
        if "vein" in part:
            return Vector((0, -0.04, -0.015))
        return {"Urinary bladder": Vector((0, 0, -0.04)), "Aorta": Vector((0.0, 0.03, 0)),
                "Inferior vena cava": Vector((0.0, 0.03, 0))}.get(part, Vector((side * 0.02, 0, 0)))
    finish(centre, Matrix.Identity(4), explode, lm, {"Left kidney", "Right kidney", "Urinary bladder"}, narrow=True)


STOMACH_REGIONS = [("Fundus", "fundus"), ("Body of stomach", "stomach_body"), ("Pyloric antrum", "antrum"),
                   ("Pylorus", "pylorus")]


def stomach():
    from mathutils.kdtree import KDTree
    whole = bp3d(["FMA7148"], "stomach_whole")
    oes = bp3d(["FMA7131"], "Oesophagus", islands=0)
    top = max(v.co.z for v in whole.vertices)
    crop_z(oes, min(v.co.z for v in whole.vertices), top + 0.06)  # its last 6 cm, into the cardia
    drop_islands(oes, 40)
    duo = bp3d(["FMA7206"], "Duodenum")
    # Landmarks for the regions: the cardia (where the oesophagus ends) and the pylorus (where the duodenum starts).
    o_pts = [v.co.copy() for v in oes.vertices]
    cardia = mean([p for p in o_pts if p.z < min(q.z for q in o_pts) + 0.006])
    tree = KDTree(len(whole.vertices))
    for i, v in enumerate(whole.vertices):
        tree.insert(v.co, i)
    tree.balance()
    d_pts = [v.co.copy() for v in duo.vertices]
    near = sorted(d_pts, key=lambda p: tree.find(p)[2])[:60]
    pylorus = mean(near)
    # The regions, cut with planes like a dissection (clean edges, not a face-by-face split):
    # - antrum and pylorus: planes square to the stomach's run toward the duodenum, 6.5 and 2.2 cm before it;
    # - fundus: the dome on the patient's left of the cardia (+X), above a level just under it (the cardiac notch).
    bm = bmesh.new(); bm.from_mesh(whole)
    body_c = mean([v.co for v in bm.verts])
    run = (pylorus - body_c).normalized()

    def half(src, co, no, keep_positive):
        b = src.copy()
        bmesh.ops.bisect_plane(b, geom=b.verts[:] + b.edges[:] + b.faces[:], plane_co=co, plane_no=no,
                               clear_outer=not keep_positive, clear_inner=keep_positive)
        return b
    distal = half(bm, pylorus - run * 0.065, run, True)
    proximal = half(bm, pylorus - run * 0.065, run, False)
    pieces = {"Pylorus": [half(distal, pylorus - run * 0.022, run, True)],
              "Pyloric antrum": [half(distal, pylorus - run * 0.022, run, False)]}
    level = Vector((0, 0, cardia.z - 0.02))
    upper = half(proximal, level, Vector((0, 0, 1)), True)
    side = Vector((cardia.x, 0, 0))
    pieces["Fundus"] = [half(upper, side, Vector((1, 0, 0)), True)]
    pieces["Body of stomach"] = [half(proximal, level, Vector((0, 0, 1)), False), half(upper, side, Vector((1, 0, 0)), False)]
    for b in (distal, proximal, upper):
        b.free()
    for part, mat in STOMACH_REGIONS:
        me = bpy.data.meshes.new(part)
        joined = bmesh.new()
        for b in pieces[part]:
            tmp = bpy.data.meshes.new("tmp"); b.to_mesh(tmp); b.free()
            joined.from_mesh(tmp); bpy.data.meshes.remove(tmp)
        joined.to_mesh(me); joined.free()
        drop_islands(me, 20)
        add(part, me, mat, 8000)
    # The lining: the wall moved 3 mm inward, with long folds (rugae) running along the stomach, as when it's empty.
    lin = bm.copy(); bm.free()  # the whole wall, before any cut
    c0 = mean([v.co for v in lin.verts])
    outward = sum((v.normal.dot(v.co - c0) for v in lin.verts)) > 0
    pts = np.array([list(v.co) for v in lin.verts])
    axis = Vector(np.linalg.svd(pts - pts.mean(0), full_matrices=False)[2][0])
    ref = axis.orthogonal().normalized(); ref2 = axis.cross(ref)
    for v in lin.verts:
        n = v.normal if outward else -v.normal
        r = v.co - c0
        ang = math.atan2(r.dot(ref2), r.dot(ref))
        fold = max(0.0, math.sin(ang * 12 + 2.0 * math.sin(r.dot(axis) * 45))) ** 2
        v.co -= n * (0.0035 - 0.003 * fold)
    me = bpy.data.meshes.new("Lining (rugae)"); lin.to_mesh(me); lin.free()
    add("Lining (rugae)", me, "rugae", 9000)
    add("Oesophagus", oes, "oesophagus", 2500)
    add("Duodenum", duo, "duodenum", 6000)
    centre = box_centre([p for p, _ in STOMACH_REGIONS] + ["Duodenum"])
    bake_colours(1.0, mottle(350.0), reach=0.01)
    o = verts("Oesophagus")
    lm = {"oesophagus_top": mean([p for p in o if p.z > max(q.z for q in o) - 0.005]), "cardia": cardia,
          "pylorus": pylorus}
    for part, _ in STOMACH_REGIONS:
        lm[part] = mean(verts(part))
    d = verts("Duodenum")
    lm["duodenum_mid"] = mean(d)
    lm["duodenum_end"] = max(d, key=lambda p: (p - pylorus).length)

    def explode(part, c):
        return {"Fundus": Vector((0.03, 0, 0.05)), "Body of stomach": Vector((0.055, -0.02, -0.005)),
                "Pyloric antrum": Vector((0.01, -0.02, -0.055)), "Pylorus": Vector((-0.04, -0.02, -0.04)),
                "Duodenum": Vector((-0.07, 0, -0.02)), "Oesophagus": Vector((0, 0, 0.06)),
                "Lining (rugae)": Vector((0, -0.01, 0))}[part]
    finish(centre, Matrix.Identity(4), explode, lm, {p for p, _ in STOMACH_REGIONS}, narrow=True)


BUILD = {"heart": lambda: heart(), "eye": lambda: eye(), "brain": lambda: brain(), "lungs": lungs, "kidneys": kidneys,
         "stomach": stomach}
if WHAT not in BUILD:
    raise SystemExit("heart, eye, brain, lungs, kidneys or stomach")
BUILD[WHAT]()
