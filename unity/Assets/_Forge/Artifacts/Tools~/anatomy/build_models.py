"""Builds the DNA, animal cell and plant cell GLBs procedurally (no source data, so they're our own work, CC0).

    Blender -b --python build_models.py -- <dna|cell|plant_cell> <out.glb> <out_landmarks.json>

Like build_anatomy.py, every part is a named mesh with an empty "explode" child (where it goes when separated) and,
for the outer parts, an "xray" child (see-through in X-ray). Shading is ambient occlusion in vertex colours.
Sizes are in metres as shown in the headset: the DNA is 0.2 m tall, the cells about 0.2 m across.

Real proportions where they matter:
- DNA is B-form: 10 base pairs per turn, a rise of 0.34 nm per pair, a 2 nm diameter, and the two backbones 150
  degrees apart round the axis, which makes the major and minor grooves. A pairs with T by two hydrogen bonds, G with C
  by three. Purines (A, G) are longer than pyrimidines (T, C).
- The cells keep textbook relative sizes: nucleus about a third of the cell across, mitochondria about 1:3 capsules
  with folded cristae inside, chloroplasts lens-shaped with stacked grana.
"""
import bpy, bmesh, json, math, os, random, sys
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

args = sys.argv[sys.argv.index("--") + 1:]
WHAT, OUT, OUT_JSON = args
rng = random.Random(11)

MATS = {
    # base colour (sRGB), roughness, alpha, double sided
    "backbone": ((0.93, 0.66, 0.30), 0.35, 1, False), "phosphate": ((0.98, 0.84, 0.45), 0.3, 1, False),
    "bases": ((1, 1, 1), 0.4, 1, False), "hbond": ((0.92, 0.95, 1.0), 0.3, 1, False),
    "membrane": ((0.92, 0.70, 0.66), 0.2, 0.2, True), "plant_membrane": ((0.82, 0.92, 0.72), 0.2, 0.07, True), "cytoplasm": ((0.95, 0.85, 0.80), 0.2, 0.08, True),
    "envelope": ((0.58, 0.44, 0.76), 0.4, 1, True), "nucleolus": ((0.38, 0.22, 0.48), 0.5, 1, False),
    "chromatin": ((0.70, 0.45, 0.80), 0.5, 1, False), "rough_er": ((0.40, 0.60, 0.86), 0.4, 1, True),
    "ribosome": ((0.20, 0.25, 0.45), 0.5, 1, False), "smooth_er": ((0.52, 0.78, 0.86), 0.4, 1, True),
    "golgi": ((0.95, 0.72, 0.30), 0.4, 1, True), "mito": ((0.90, 0.42, 0.30), 0.4, 1, True),
    "lysosome": ((0.62, 0.40, 0.72), 0.35, 1, False), "vesicle": ((0.96, 0.80, 0.50), 0.3, 1, False),
    "centriole": ((0.45, 0.80, 0.55), 0.4, 1, False), "microtubule": ((0.85, 0.88, 0.70), 0.4, 0.8, False),
    "wall": ((0.50, 0.68, 0.28), 0.45, 0.16, True), "vacuole": ((0.55, 0.78, 0.96), 0.1, 0.13, True),
    "chloroplast": ((0.20, 0.56, 0.16), 0.4, 1, True), "grana": ((0.12, 0.40, 0.12), 0.5, 1, False),
    "plasmodesmata": ((0.75, 0.62, 0.35), 0.5, 1, False),
}
BASE = {"A": (0.32, 0.72, 0.36), "T": (0.88, 0.32, 0.30), "G": (0.98, 0.80, 0.22), "C": (0.30, 0.52, 0.90)}

parts = {}  # name -> [bmesh, material, vertex colour fn or None]
landmarks = {}


def bm_for(part, mat):
    if part not in parts:
        parts[part] = [bmesh.new(), mat, None]
    return parts[part][0]


def sphere(part, mat, c, r, scale=(1, 1, 1), subdiv=2, rot=None):
    bm = bm_for(part, mat)
    m = Matrix.Translation(c) @ (rot or Matrix.Identity(4)) @ Matrix.Diagonal((*scale, 1))
    bmesh.ops.create_icosphere(bm, subdivisions=subdiv, radius=r, matrix=m)


def uv_sphere(part, mat, c, r, scale=(1, 1, 1), segs=32, rings=16, rot=None):
    bm = bm_for(part, mat)
    m = Matrix.Translation(c) @ (rot or Matrix.Identity(4)) @ Matrix.Diagonal((*scale, 1))
    bmesh.ops.create_uvsphere(bm, u_segments=segs, v_segments=rings, radius=r, matrix=m)


def look(direction):
    """Rotation taking +Z to direction."""
    return Vector((0, 0, 1)).rotation_difference(direction.normalized()).to_matrix().to_4x4()


def cylinder(part, mat, a, b, r, segs=12, caps=True, r2=None):
    bm = bm_for(part, mat)
    d = b - a
    m = Matrix.Translation((a + b) / 2) @ look(d)
    bmesh.ops.create_cone(bm, cap_ends=caps, segments=segs, radius1=r, radius2=r if r2 is None else r2, depth=d.length, matrix=m)


def box(part, mat, c, size, rot=None):
    bm = bm_for(part, mat)
    m = Matrix.Translation(c) @ (rot or Matrix.Identity(4)) @ Matrix.Diagonal((*size, 1))
    bmesh.ops.create_cube(bm, size=1.0, matrix=m)


def tube(part, mat, points, r, resolution=3, closed=False):
    """A smooth tube through points (a bevelled curve turned into a mesh)."""
    cu = bpy.data.curves.new("t", "CURVE"); cu.dimensions = "3D"
    cu.bevel_depth = r; cu.bevel_resolution = resolution; cu.use_fill_caps = True
    sp = cu.splines.new("NURBS"); sp.points.add(len(points) - 1)
    for i, p in enumerate(points):
        sp.points[i].co = (p.x, p.y, p.z, 1)
    sp.order_u = 3; sp.use_endpoint_u = not closed; sp.use_cyclic_u = closed; sp.resolution_u = 4
    ob = bpy.data.objects.new("t", cu); bpy.context.collection.objects.link(ob)
    ev = ob.evaluated_get(bpy.context.evaluated_depsgraph_get())
    me = ev.to_mesh()
    bm_for(part, mat).from_mesh(me)
    ev.to_mesh_clear(); bpy.data.objects.remove(ob); bpy.data.curves.remove(cu)


def shell(part, mat, c, r, thickness, scale=(1, 1, 1), segs=48, rings=24, keep=None, rot=None):
    """A hollow ellipsoid; keep(direction) -> bool trims it (for sheets of ER, the cut wedge, ...)."""
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=segs, v_segments=rings, radius=r)
    if keep is not None:
        drop = [f for f in bm.faces if not keep(f.calc_center_median().normalized())]
        bmesh.ops.delete(bm, geom=drop, context="FACES")
    me = bpy.data.meshes.new("s"); bm.to_mesh(me); bm.free()
    ob = bpy.data.objects.new("s", me); bpy.context.collection.objects.link(ob)
    mod = ob.modifiers.new("solid", "SOLIDIFY"); mod.thickness = thickness; mod.offset = 0
    ob.matrix_world = Matrix.Translation(c) @ (rot or Matrix.Identity(4)) @ Matrix.Diagonal((*scale, 1))
    ev = ob.evaluated_get(bpy.context.evaluated_depsgraph_get())
    m2 = ev.to_mesh(); m2.transform(ob.matrix_world)
    bm_for(part, mat).from_mesh(m2)
    ev.to_mesh_clear(); bpy.data.objects.remove(ob); bpy.data.meshes.remove(me)


def random_dir():
    while True:
        v = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1)))
        if 0.1 < v.length <= 1:
            return v.normalized()


def place_free(c, r, taken, bound, tries=400):
    """A random point inside radius bound of c whose ball of radius r misses the taken balls."""
    for _ in range(tries):
        p = c + random_dir() * rng.uniform(0, bound)
        if all((p - q).length > r + s for q, s in taken):
            return p
    return None


# --- DNA ------------------------------------------------------------------------------------------------------------
def dna():
    seq = "ATGCGTACCTGAGCTTAACG"
    pair = {"A": "T", "T": "A", "G": "C", "C": "G"}
    n = len(seq)
    rise, radius, twist, offset = 0.0102, 0.03, math.radians(36), math.radians(150)
    h0 = -rise * (n - 1) / 2
    def at(angle, rad, z):
        return Vector((rad * math.cos(angle), rad * math.sin(angle), z))
    s1, s2 = [], []
    for i in range(n):
        a = i * twist; z = h0 + i * rise
        s1.append(at(a, radius, z)); s2.append(at(a + offset, radius, z))
    # Backbones: a smooth tube through the phosphates, with a phosphate bead at each.
    for name, pts in (("Backbone, strand 1", s1), ("Backbone, strand 2", s2)):
        ext = [pts[0] + (pts[0] - pts[1]) * 0.5] + pts + [pts[-1] + (pts[-1] - pts[-2]) * 0.5]
        tube(name, "backbone", ext, 0.0034)
        for p in pts:
            sphere(name, "backbone", p, 0.0052, subdiv=2)
    colours = {}
    for i, b in enumerate(seq):
        a = i * twist; z = h0 + i * rise
        p1, p2 = s1[i], s2[i]
        # The bases meet a little off the axis, toward the minor groove, like real B-DNA.
        mid = at(a + offset / 2, radius * 0.18, z)
        purine1 = b in "AG"
        split = 0.58 if purine1 else 0.42
        meet = p1 + (p2 - p1) * split
        meet = meet + (mid - (p1 + p2) / 2) * 0.6
        gap = 0.0016
        for strand, start, base, frac_end in (("Bases, strand 1", p1, b, meet), ("Bases, strand 2", p2, pair[b], meet)):
            d = (frac_end - start); L = d.length - gap
            end = start + d.normalized() * L
            # A flat slab, in the plane of the pair, perpendicular to the helix axis.
            c = (start + end) / 2
            rot = Matrix.Identity(3)
            x = d.normalized(); zax = Vector((0, 0, 1)); y = zax.cross(x).normalized(); zz = x.cross(y)
            rot = Matrix((x, y, zz)).transposed().to_4x4()
            bm = bm_for(strand, "bases")
            first = len(bm.verts)
            bmesh.ops.create_cube(bm, size=1.0, matrix=Matrix.Translation(c) @ rot @ Matrix.Diagonal((L, 0.0085, 0.0028, 1)))
            colours.setdefault(strand, []).append((first, len(bm.verts), BASE[base]))
        # Hydrogen bonds across the gap: 2 for A-T, 3 for G-C.
        bonds = 2 if b in "AT" else 3
        dirv = (p2 - p1).normalized(); side = Vector((0, 0, 1)).cross(dirv).normalized()
        for k in range(bonds):
            o = side * (0.0028 * (k - (bonds - 1) / 2))
            cylinder("Hydrogen bonds", "hbond", meet - dirv * 0.0016 + o, meet + dirv * 0.0016 + o, 0.0007, segs=6)
        if i == 4: landmarks["base"] = (p1 + meet) / 2
        if i == 3: landmarks["pair"] = meet; landmarks["bond"] = meet
        if i == 7: landmarks["backbone"] = s1[i]
        if i == 12: landmarks["strand2"] = s2[i]
    landmarks["top"] = Vector((0, 0, h0 + rise * n)); landmarks["bottom"] = Vector((0, 0, h0 - rise))
    for strand, spans in colours.items():
        parts[strand][2] = spans
    explode = {"Backbone, strand 1": Vector((-0.07, 0, 0)), "Bases, strand 1": Vector((-0.045, 0, 0)),
               "Backbone, strand 2": Vector((0.07, 0, 0)), "Bases, strand 2": Vector((0.045, 0, 0)),
               "Hydrogen bonds": Vector((0, 0, 0))}
    # The helix axis is Blender Z; turn it to stand up in glTF (Blender Z is glTF Y already). Front: glTF +Z.
    return explode, set(), Matrix.Identity(4)


# --- animal cell ----------------------------------------------------------------------------------------------------
def cell():
    R = 0.1
    centre = Vector((0, 0, 0))
    # Membrane: a slightly irregular sphere, see-through so the organelles show.
    bm = bm_for("Cell membrane", "membrane")
    bmesh.ops.create_icosphere(bm, subdivisions=5, radius=R)
    for v in bm.verts:
        d = v.co.normalized()
        v.co = d * R * (1 + 0.035 * math.sin(d.x * 5.1 + 1) * math.sin(d.y * 4.3 + 2) * math.sin(d.z * 3.7)) * Vector((1.12, 1.0, 0.9))
    nuc_c = Vector((-0.012, 0.008, 0.004)); nuc_r = 0.034
    taken = [(nuc_c, nuc_r + 0.02)]
    # Nucleus: envelope with pores, nucleolus, chromatin.
    shell("Nuclear envelope", "envelope", nuc_c, nuc_r, 0.0025, segs=48, rings=24)
    for _ in range(46):
        d = random_dir()
        bmp = bm_for("Nuclear envelope", "envelope")
        m = Matrix.Translation(nuc_c + d * (nuc_r + 0.0012)) @ look(d)
        bmesh.ops.create_cone(bmp, cap_ends=False, segments=10, radius1=0.0028, radius2=0.0028, depth=0.0034, matrix=m)
    uv_sphere("Nucleolus", "nucleolus", nuc_c + Vector((0.008, -0.006, 0.006)), 0.011, scale=(1, 0.9, 0.85))
    for _ in range(9):
        p = nuc_c + random_dir() * nuc_r * 0.5
        pts = [p]
        for _ in range(12):
            p = p + random_dir() * 0.006
            if (p - nuc_c).length > nuc_r * 0.85: p = nuc_c + (p - nuc_c).normalized() * nuc_r * 0.8
            pts.append(p)
        tube("Chromatin", "chromatin", pts, 0.0011, resolution=1)
    landmarks.update(nucleus=nuc_c + Vector((0, 0, nuc_r)), nucleolus=nuc_c + Vector((0.008, -0.006, 0.017)))
    # Rough ER: curved sheets wrapped round the nucleus on one side, studded with ribosomes.
    er_dir = Vector((0.7, -0.3, 0.2)).normalized()
    for k, rr in enumerate((0.044, 0.051, 0.058, 0.065)):
        keep = lambda d, k=k: d.dot(er_dir) > 0.0 + 0.06 * k and abs(d.z) < 0.8
        shell("Rough ER", "rough_er", nuc_c, rr, 0.0026, scale=(1, 1, 0.95), keep=keep, segs=112, rings=56)
        for _ in range(220):
            d = random_dir()
            if d.dot(er_dir) > 0.05 + 0.06 * k and abs(d.z) < 0.75:
                sphere("Rough ER", "ribosome", nuc_c + d * (rr + 0.0022), 0.0011, subdiv=0)
    landmarks["rough_er"] = nuc_c + er_dir * 0.058
    taken.append((nuc_c + er_dir * 0.055, 0.025))
    # Smooth ER: a tangle of tubes beyond the rough ER.
    se_c = nuc_c + er_dir * 0.075 + Vector((0, 0.02, -0.02))
    for _ in range(7):
        p = se_c + random_dir() * 0.01; pts = [p]
        for _ in range(8):
            p = p + random_dir() * 0.008; pts.append(p)
        tube("Smooth ER", "smooth_er", pts, 0.0022, resolution=2)
    taken.append((se_c, 0.02))
    # Golgi: six stacked, curved cisternae with vesicles budding off the ends.
    g_c = nuc_c + Vector((-0.045, -0.035, 0.012)); g_dir = (g_c - nuc_c).normalized()
    for k in range(6):
        rr = 0.03 + k * 0.0042
        keep = lambda d: d.dot(g_dir) > 0.93
        shell("Golgi apparatus", "golgi", g_c - g_dir * 0.03, rr, 0.0018, scale=(1, 1, 0.55), keep=keep, segs=64, rings=32)
    for _ in range(14):
        sphere("Vesicles", "vesicle", g_c + g_dir * 0.012 + random_dir() * 0.016, rng.uniform(0.0018, 0.003), subdiv=2)
    landmarks["golgi"] = g_c; taken.append((g_c, 0.024))
    # Mitochondria: capsules with folded cristae inside.
    for m in range(10):
        p = place_free(centre, 0.016, taken, R * 0.74)
        if p is None: continue
        taken.append((p, 0.018))
        rot = look(random_dir())
        L, r = 0.03, 0.0085
        shell("Mitochondria", "mito", p, r, 0.0012, scale=(1, 1, L / r / 2), rot=rot, segs=24, rings=16)
        for k in range(6):
            z = -L / 2 * 0.75 + k * (L * 0.75 / 5)
            box("Mitochondria", "mito", p + (rot.to_3x3() @ Vector((0, 0, z))), (r * 1.4, 0.0007, 0.0012),
                rot=rot @ Matrix.Rotation(rng.uniform(-0.4, 0.4), 4, "X"))
        if m == 0: landmarks["mitochondrion"] = p
    # Lysosomes, free ribosomes, centrioles and microtubules.
    for k in range(8):
        p = place_free(centre, 0.008, taken, R * 0.78)
        if p is None: continue
        taken.append((p, 0.009)); sphere("Lysosomes", "lysosome", p, rng.uniform(0.0055, 0.008), subdiv=3)
        if k == 0: landmarks["lysosome"] = p
    for _ in range(320):
        p = place_free(centre, 0.0012, taken, R * 0.85, tries=20)
        if p is not None: sphere("Free ribosomes", "ribosome", p, 0.0011, subdiv=0)
    cs = nuc_c + Vector((0.01, 0.045, -0.02)); taken.append((cs, 0.01))
    for axis in (Vector((0, 0, 1)), Vector((1, 0, 0))):
        rot = look(axis); off = cs + (Vector((0.004, 0, 0)) if axis.z else Vector((-0.004, 0, 0)))
        for k in range(9):
            a = k * 2 * math.pi / 9
            for t in range(3):
                q = Vector((math.cos(a) * (0.0028 - t * 0.0006), math.sin(a) * (0.0028 - t * 0.0006), 0))
                q = off + rot.to_3x3() @ q
                cylinder("Centrosome", "centriole", q - rot.to_3x3() @ Vector((0, 0, 0.0045)), q + rot.to_3x3() @ Vector((0, 0, 0.0045)), 0.00045, segs=6)
    landmarks["centrosome"] = cs
    for _ in range(26):
        d = random_dir(); pts = [cs]
        for k in range(1, 8):
            pts.append(cs + d * (R * 0.85 * k / 7) + random_dir() * 0.004 * (k > 1))
        tube("Microtubules", "microtubule", pts, 0.0006, resolution=1)
    landmarks["membrane"] = Vector((0, -R * 0.98, 0.04)); landmarks["cytoplasm"] = Vector((0.05, -0.04, -0.03))
    landmarks["centre"] = centre
    explode = {"Cell membrane": Vector((0, 0, 0.23)), "Nuclear envelope": Vector((0, 0, 0)),
               "Nucleolus": Vector((0, -0.07, 0.0)), "Chromatin": Vector((0, -0.05, -0.035))}
    xray = {"Cell membrane", "Nuclear envelope"}
    return explode, xray, Matrix.Identity(4)


# --- plant cell -----------------------------------------------------------------------------------------------------
def rounded_box(part, mat, c, size, radius, thickness=None):
    """A rounded box (a bevelled cube), hollow when thickness is given."""
    me = bpy.data.meshes.new("b"); bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0, matrix=Matrix.Diagonal((*size, 1)))
    bm.to_mesh(me); bm.free()
    ob = bpy.data.objects.new("b", me); bpy.context.collection.objects.link(ob)
    bev = ob.modifiers.new("bev", "BEVEL"); bev.width = radius; bev.segments = 6; bev.limit_method = "NONE"
    if thickness:
        so = ob.modifiers.new("solid", "SOLIDIFY"); so.thickness = thickness; so.offset = -1
    ob.location = c
    ev = ob.evaluated_get(bpy.context.evaluated_depsgraph_get())
    m2 = ev.to_mesh(); m2.transform(ob.matrix_world)
    bm_for(part, mat).from_mesh(m2)
    ev.to_mesh_clear(); bpy.data.objects.remove(ob); bpy.data.meshes.remove(me)


def plant_cell():
    W, D, H = 0.22, 0.13, 0.13   # length, depth, height
    rounded_box("Cell wall", "wall", Vector(), (W, D, H), 0.02, thickness=0.006)
    rounded_box("Cell membrane", "plant_membrane", Vector(), (W - 0.016, D - 0.016, H - 0.016), 0.016)
    # Central vacuole: most of the cell, a soft rounded block.
    rounded_box("Central vacuole", "vacuole", Vector((0.018, 0.004, 0)), (W * 0.62, D * 0.62, H * 0.62), 0.03)
    landmarks.update(wall=Vector((0, -D / 2, H / 2 - 0.01)), vacuole=Vector((0.018, -D * 0.31, 0.0)),
                     cytoplasm=Vector((-0.085, -0.03, -0.04)), centre=Vector())
    # Nucleus pressed to one end by the vacuole.
    nuc_c = Vector((-0.072, 0.0, 0.012)); nuc_r = 0.022
    shell("Nuclear envelope", "envelope", nuc_c, nuc_r, 0.002, segs=40, rings=20)
    for _ in range(30):
        d = random_dir(); bmp = bm_for("Nuclear envelope", "envelope")
        bmesh.ops.create_cone(bmp, cap_ends=False, segments=8, radius1=0.002, radius2=0.002, depth=0.0026,
                              matrix=Matrix.Translation(nuc_c + d * (nuc_r + 0.001)) @ look(d))
    uv_sphere("Nucleolus", "nucleolus", nuc_c + Vector((0.004, -0.004, 0.004)), 0.0075)
    landmarks.update(nucleus=nuc_c + Vector((0, 0, nuc_r)), nucleolus=nuc_c + Vector((0.004, -0.004, 0.0115)))
    # Chloroplasts round the edge, between vacuole and membrane, each with grana stacks inside.
    spots = []
    for k in range(10):
        a = k / 10 * 2 * math.pi
        p = Vector((0.06 * math.cos(a) + 0.012, 0.045 * math.sin(a) * (1 if k % 2 else -1) * 0.0 + (0.044 if k % 2 else -0.044), 0.044 * math.sin(a)))
        if (p - nuc_c).length < 0.035: continue
        spots.append(p)
    for i, p in enumerate(spots):
        rot = look(Vector((0, 1 if p.y > 0 else -1, 0.2))) @ Matrix.Rotation(rng.uniform(0, 3.1), 4, "Z")
        L = 0.026
        shell("Chloroplasts", "chloroplast", p, 0.0075, 0.0011, scale=(L / 0.011, 1, 0.55), rot=rot, segs=28, rings=14)
        for g in range(5):
            gx = -L * 0.32 + g * (L * 0.64 / 4)
            for s in range(4):
                c = p + rot.to_3x3() @ Vector((gx, 0, -0.0012 + s * 0.0008))
                bmg = bm_for("Chloroplasts", "grana")
                bmesh.ops.create_cone(bmg, cap_ends=True, segments=12, radius1=0.0016, radius2=0.0016, depth=0.0005,
                                      matrix=Matrix.Translation(c) @ rot)
        if i == 0: landmarks["chloroplast"] = p
    # Mitochondria, Golgi and rough ER near the nucleus.
    for k, p in enumerate((Vector((-0.09, 0.03, -0.03)), Vector((-0.05, -0.04, -0.045)), Vector((0.08, -0.04, -0.04)))):
        rot = look(random_dir())
        shell("Mitochondria", "mito", p, 0.0045, 0.001, scale=(1, 1, 2.2), rot=rot, segs=20, rings=12)
        for j in range(4):
            box("Mitochondria", "mito", p + rot.to_3x3() @ Vector((0, 0, -0.006 + j * 0.004)), (0.006, 0.0006, 0.001), rot=rot)
    g_c = Vector((-0.05, 0.035, -0.035)); g_dir = (g_c - nuc_c).normalized()
    for k in range(5):
        shell("Golgi apparatus", "golgi", g_c - g_dir * 0.022, 0.022 + k * 0.003, 0.0014, scale=(1, 1, 0.55),
              keep=lambda d: d.dot(g_dir) > 0.93, segs=56, rings=28)
    landmarks["golgi"] = g_c
    er_dir = Vector((0.2, -1, -0.3)).normalized()
    for k, rr in enumerate((0.03, 0.036, 0.042)):
        shell("Rough ER", "rough_er", nuc_c, rr, 0.002, keep=lambda d, k=k: d.dot(er_dir) > 0.62 + 0.04 * k, segs=96, rings=48)
        for _ in range(90):
            d = random_dir()
            if d.dot(er_dir) > 0.64 + 0.04 * k: sphere("Rough ER", "ribosome", nuc_c + d * (rr + 0.0018), 0.0009, subdiv=0)
    landmarks["rough_er"] = nuc_c + er_dir * 0.036
    # Plasmodesmata: channels through the wall to the next cell.
    for k in range(12):
        x = rng.uniform(-W * 0.35, W * 0.35); z = rng.uniform(-H * 0.3, H * 0.3); sgn = 1 if k % 2 else -1
        cylinder("Plasmodesmata", "plasmodesmata", Vector((x, sgn * (D / 2 - 0.009), z)), Vector((x, sgn * (D / 2 + 0.002), z)), 0.0012, segs=8)
    explode = {"Cell wall": Vector((0, 0.0, 0.2)), "Cell membrane": Vector((0, 0, 0.13)), "Plasmodesmata": Vector((0, 0, 0.2)),
               "Central vacuole": Vector((0.06, 0, 0)), "Nuclear envelope": Vector((-0.03, 0, 0)), "Nucleolus": Vector((-0.03, -0.06, 0)),
               "Chloroplasts": Vector((0, -0.07, 0)), "Mitochondria": Vector((0, 0.0, -0.07)), "Golgi apparatus": Vector((-0.02, 0.06, -0.03)),
               "Rough ER": Vector((-0.02, -0.03, -0.05))}
    xray = {"Cell wall", "Cell membrane", "Central vacuole", "Nuclear envelope"}
    return explode, xray, Matrix.Identity(4)


# --- shared finishing -----------------------------------------------------------------------------------------------
def material(key):
    if key in bpy.data.materials:
        return bpy.data.materials[key]
    (r, g, b), rough, alpha, double = MATS[key]
    m = bpy.data.materials.new(key); m.use_nodes = True
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


def gl(p):
    return [round(p.x, 5), round(p.z, 5), round(-p.y, 5)]


def finish(explode, xray, rotation):
    objs = {}
    for name, (bm, mat, spans) in parts.items():
        me = bpy.data.meshes.new(name)
        if not spans: bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)  # spans index vertices
        bm.to_mesh(me)
        o = bpy.data.objects.new(name, me); bpy.context.collection.objects.link(o)
        me.transform(rotation)
        objs[name] = (o, mat, spans)
    # Ambient occlusion against everything, times per-part colours (the DNA's bases).
    tmp = []
    for o, _, _ in objs.values():
        c = o.copy(); c.data = o.data.copy(); bpy.context.collection.objects.link(c); tmp.append(c)
    bpy.ops.object.select_all(action="DESELECT")
    for c in tmp: c.select_set(True)
    bpy.context.view_layer.objects.active = tmp[0]; bpy.ops.object.join()
    allo = bpy.context.view_layer.objects.active
    bvh = BVHTree.FromObject(allo, bpy.context.evaluated_depsgraph_get())
    dirs = [random_dir() for _ in range(16)]
    for name, (o, mat, spans) in objs.items():
        me = o.data
        layer = me.color_attributes.new("Col", "BYTE_COLOR", "POINT")
        see_through = MATS[mat][2] < 1
        tint = [None] * len(me.vertices)
        if spans:
            for a, b, col in spans:
                for i in range(a, min(b, len(tint))): tint[i] = col
        for i, v in enumerate(me.vertices):
            a = 1.0
            if not see_through:
                hits = sum(1 for d in dirs if bvh.ray_cast(v.co + v.normal * 0.0003, d if d.dot(v.normal) > 0 else -d, 0.012)[0] is not None)
                a = max(0.45, 1 - 0.75 * hits / len(dirs))
            col = tint[i] or (1, 1, 1)
            layer.data[i].color = (col[0] * a, col[1] * a, col[2] * a, 1)
        me.color_attributes.active_color = layer
    bpy.data.objects.remove(allo)
    report = {"parts": {}, "landmarks": {k: gl(rotation @ v) for k, v in landmarks.items()}}
    for name, (o, mat, _) in objs.items():
        me = o.data
        me.materials.append(material(mat))
        for p in me.polygons: p.use_smooth = True
        c = sum((v.co for v in me.vertices), Vector()) / len(me.vertices)
        spread = max((v.co - c).length for v in me.vertices)
        near = [v.co for v in me.vertices if Vector((v.co.x - c.x, 0, v.co.z - c.z)).length < spread * 0.3] or [c]
        anchor = min(near, key=lambda q: q.y)
        me.transform(Matrix.Translation(-c)); o.location = c
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.select_all(action="DESELECT"); o.select_set(True)
        bpy.ops.object.shade_auto_smooth(angle=math.radians(50))
        e = bpy.data.objects.new("explode", None); bpy.context.collection.objects.link(e); e.parent = o
        e.location = explode.get(name, (c.normalized() if c.length > 1e-6 else Vector((0, 0, 1))) * 0.06)
        if name in xray:
            x = bpy.data.objects.new("xray", None); bpy.context.collection.objects.link(x); x.parent = o
        report["parts"][name] = {"tris": sum(len(p.vertices) - 2 for p in me.polygons), "material": mat,
                                 "centre": gl(c), "anchor": gl(anchor), "explode": gl(Vector(e.location))}
    bpy.ops.object.select_all(action="DESELECT")
    for o, _, _ in objs.values():
        o.select_set(True)
        for ch in o.children: ch.select_set(True)
    bpy.ops.export_scene.gltf(filepath=OUT, export_format="GLB", export_apply=True, export_normals=True,
                              export_vertex_color="ACTIVE", export_texcoords=False, export_materials="EXPORT",
                              export_yup=True, use_selection=True)
    report["total_tris"] = sum(p["tris"] for p in report["parts"].values()); report["bytes"] = os.path.getsize(OUT)
    json.dump(report, open(OUT_JSON, "w"), indent=1)
    print("BUILD_OK", WHAT, report["total_tris"], report["bytes"], len(objs), "parts")


bpy.ops.wm.read_factory_settings(use_empty=True)
explode, xray, rotation = {"dna": dna, "cell": cell, "plant_cell": plant_cell}[WHAT]()
finish(explode, xray, rotation)
