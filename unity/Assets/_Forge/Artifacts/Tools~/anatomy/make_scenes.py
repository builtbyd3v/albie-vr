"""Writes the heart and eye templates' scene.json and manifest.json from the GLBs that build_anatomy.py made.

    python make_scenes.py <heart|eye> <model.glb> <landmarks.json> <template_dir>

Labels point at each part's front surface, so they follow the model rather than being placed by eye. The heart gets
blood cells that run the real path through the chambers and valves (from the model's own landmarks); the eye gets a
ray of light that crosses the cornea, pupil and lens to the retina, and a signal down the optic nerve.

A glTF point p maps into the scene like the runtime does: glTFast flips X, then the node's rotation (about Y),
scale and position apply.
"""
import hashlib, json, math, os, shutil, sys

WHAT, GLB, LANDMARKS, OUT = sys.argv[1:5]
data = json.load(open(LANDMARKS))
parts, lm = data["parts"], data["landmarks"]

CREDIT = ("3D model: Z-Anatomy (CC BY-SA 4.0) and BodyParts3D, (c) 2008 Life Science Integrated Database Center "
          "(CC BY-SA 2.1 JP). This model is shared under CC BY-SA 4.0.")


def to_scene(p, node):
    x, y, z = -p[0], p[1], p[2]  # glTF -> Unity
    s = node["scale"][0]
    a = math.radians(node["rot"][1])
    rx = math.cos(a) * x + math.sin(a) * z
    rz = -math.sin(a) * x + math.cos(a) * z
    return [round(node["pos"][0] + s * rx, 4), round(node["pos"][1] + s * y, 4), round(node["pos"][2] + s * rz, 4)]


def lerp(a, b, t):
    return [a[i] + (b[i] - a[i]) * t for i in range(3)]


def path_at(points, u):
    """Point u in [0, 1] of the way along a polyline, by length."""
    seg = [math.dist(points[i], points[i + 1]) for i in range(len(points) - 1)]
    total = sum(seg) or 1.0
    d = u * total
    for i, L in enumerate(seg):
        if d <= L or i == len(seg) - 1:
            return [round(v, 4) for v in lerp(points[i], points[i + 1], min(1.0, d / L if L else 0.0))]
        d -= L


def labels(spec, node, column_x, z, y_range, height=0.016):
    """spec: [(id, text, anchor in scene)]. Left column for anchors left of centre, right for the rest, ordered by height."""
    nodes, ids = [], []
    for side in (-1, 1):
        col = sorted([s for s in spec if (s[2][0] < node["pos"][0]) == (side < 0)], key=lambda s: -s[2][1])
        if not col:
            continue
        lo, hi = y_range
        step = (hi - lo) / max(len(col) - 1, 1)
        for i, (lid, text, anchor) in enumerate(col):
            y = round(hi - i * step if len(col) > 1 else (hi + lo) / 2, 4)
            x = column_x * side
            nodes.append({"id": "label_" + lid, "kind": "text", "text": text, "pos": [x, y, z], "height": height,
                          "color": "#FFFFFF", "billboard": True, "align": "right" if side < 0 else "left"})
            nodes.append({"id": "leader_" + lid, "kind": "line",
                          "points": [[round(x + 0.008 * side, 4), round(y - 0.004, 4), z], anchor],
                          "width": 0.0008, "color": "#D0D4DA"})
            ids.append(lid)
    return nodes, ids


def label_rules(ids):
    rules = []
    for on in (True, False):
        rules.append({"type": "on_input", "input": "labels_on" if on else "labels_off",
                      "do": [{"set": f"{k}_{i}.visible", "value": on} for i in ids for k in ("label", "leader")]})
    return rules


def steps(captions, y, z, duration, timeline):
    """Captions shown one after another on a timeline that waits for its button."""
    nodes, rules = [], []
    span = duration / len(captions)
    for i, text in enumerate(captions):
        sid = f"step_{i + 1}"
        nodes.append({"id": sid, "kind": "text", "text": f"{i + 1}  {text}", "pos": [0, y, z], "height": 0.018,
                      "color": "#FFE66D", "billboard": True, "visible": False})
        a, b = round(i * span, 3), round((i + 1) * span - (0.05 if i == len(captions) - 1 else 0), 3)
        rules.append({"type": "animate", "target": sid, "property": "visible", "timeline": timeline,
                      "keys": [[0, False], [max(a, 0.01), True], [b, False]]})
    return nodes, rules


def highlight(label_ids, a, b, timeline):
    return [{"type": "animate", "target": "label_" + lid, "property": "color", "timeline": timeline,
             "keys": [[0, "#FFFFFF"], [a, "#FFFFFF"], [round(a + 0.01, 3), "#FFE66D"], [round(b - 0.01, 3), "#FFE66D"], [b, "#FFFFFF"]]}
            for lid in label_ids]


def train(prefix, points, count, colour, size, duration, timeline):
    """count cells evenly spaced along points, looping; each hides for a moment where it wraps back to the start."""
    nodes, rules = [], []
    samples = 24
    for i in range(count):
        phase = i / count
        cid = f"{prefix}{i + 1}"
        start = path_at(points, phase)
        nodes.append({"id": cid, "kind": "primitive", "shape": "sphere", "pos": start, "size": [size] * 3,
                      "color": colour, "glow": True, "visible": False})
        wrap = round((1 - phase) * duration, 3)
        keys = []
        for k in range(samples + 1):
            t = round(k * duration / samples, 3)
            if abs(t - wrap) < 1e-3:
                continue
            keys.append([t, path_at(points, ((t / duration) + phase) % 1.0)])
        if 0 < wrap < duration:
            keys += [[round(wrap - 0.002, 3), path_at(points, 1.0)], [round(wrap + 0.002, 3), path_at(points, 0.0)]]
        keys.sort(key=lambda kv: kv[0])
        rules.append({"type": "animate", "target": cid, "property": "pos", "timeline": timeline, "keys": keys})
        vis = [[0, False], [0.01, True]]
        if 0.05 < wrap < duration - 0.05:
            vis += [[round(wrap - 0.03, 3), False], [round(wrap + 0.03, 3), True]]
        vis.append([duration, False])
        rules.append({"type": "animate", "target": cid, "property": "visible", "timeline": timeline, "keys": vis})
    return nodes, rules


def anchor(part, node):
    return to_scene(parts[part]["anchor"], node)


def L(name, node):
    return to_scene(lm[name], node)


def manifest(tid, title, asset):
    sha = hashlib.sha256(open(GLB, "rb").read()).hexdigest()
    return {"schema": "albie.artifact/1", "id": tid, "title": title, "version": 1, "kind": "standalone", "category": "viz",
            "template": tid, "links": {"project": None, "project_version": None, "repo": None, "thing": None},
            "runtime": {"type": "scene", "min_runtime": 4}, "scene": "scene.json", "script": None, "inputs": [],
            "scenarios": [{"id": "default", "seed": 1, "params": {}}], "data": [],
            "assets": [{"path": asset, "sha256": sha, "license": "CC-BY-SA-4.0"}],
            "permissions": {"network": [], "connectors": [], "storage": "artifact-local", "write_project": "none",
                            "room": "none", "things": "none"}, "outputs": []}


def heart():
    node = {"id": "heart", "kind": "gltf", "asset": "assets/heart.glb", "pos": [0, -0.04, 0], "rot": [0, 180, 0], "scale": [1.8, 1.8, 1.8]}
    rest = node["scale"][0]
    beat_keys = [[0, 1.0], [0.1, 0.975], [0.17, 1.0], [0.27, 0.93], [0.47, 1.0], [0.833, 1.0]]
    rules = [
        {"type": "animate", "target": "heart", "property": "scale", "timeline": "beat", "keys": [[t, round(rest * s, 4)] for t, s in beat_keys]},
        {"type": "animate", "target": "heart", "property": "pos", "timeline": "beat", "keys": [[t, node["pos"]] for t, _ in beat_keys]},
    ]
    spec = [
        ("svc", "Superior vena cava", anchor("Superior vena cava", node)),
        ("ivc", "Inferior vena cava", anchor("Inferior vena cava", node)),
        ("right_atrium", "Right atrium", anchor("Right atrium", node)),
        ("tricuspid", "Tricuspid valve", anchor("Tricuspid valve", node)),
        ("right_ventricle", "Right ventricle", anchor("Right ventricle", node)),
        ("aortic_valve", "Aortic valve", anchor("Aortic valve", node)),
        ("aorta", "Aorta", anchor("Aorta", node)),
        ("pulmonary_artery", "Pulmonary artery", anchor("Pulmonary arteries", node)),
        ("pulmonary_valve", "Pulmonary valve", anchor("Pulmonary valve", node)),
        ("pulmonary_veins", "Pulmonary veins", anchor("Pulmonary veins", node)),
        ("left_atrium", "Left atrium", anchor("Left atrium", node)),
        ("mitral", "Mitral valve", anchor("Mitral valve", node)),
        ("left_ventricle", "Left ventricle", anchor("Left ventricle", node)),
        ("coronary", "Coronary arteries", anchor("Left coronary artery", node)),
    ]
    label_nodes, ids = labels(spec, node, 0.17, -0.09, (-0.18, 0.13))
    rules += label_rules(ids)

    # The blood's path, from the model's own landmarks.
    rv_out = lerp(L("rv", node), L("pulmonary_valve", node), 0.5)
    lv_low = lerp(L("lv", node), L("apex", node), 0.35)
    venous = [L("svc_top", node), L("svc", node), L("ra", node), L("tricuspid", node), L("rv", node), rv_out,
              L("pulmonary_valve", node), L("pa_top", node), L("pa_far", node)]
    lower = [L("ivc_bottom", node), L("ivc", node), L("ra", node), L("tricuspid", node), L("rv", node), rv_out,
             L("pulmonary_valve", node), L("pa_top", node)]
    arterial = [L("pv_far", node), L("pv", node), L("la", node), L("mitral", node), L("lv", node), lv_low,
                L("aortic_valve", node), L("aorta_top", node), L("aorta_bottom", node)]
    T = 4.0
    flow_nodes, flow_rules = [], []
    for prefix, pts, n, colour in (("cell_v", venous, 7, "#3A6FE0"), ("cell_i", lower, 4, "#3A6FE0"), ("cell_a", arterial, 8, "#E0303A")):
        nn, rr = train(prefix, pts, n, colour, 0.008, T, "circulation")
        flow_nodes += nn; flow_rules += rr
    cells = [n["id"] for n in flow_nodes]

    captions = ["Oxygen-poor blood from the body fills the right atrium",
                "It passes the tricuspid valve, and the right ventricle pumps it to the lungs",
                "Oxygen-rich blood returns from the lungs to the left atrium",
                "It passes the mitral valve, and the left ventricle pumps it into the aorta"]
    step_nodes, step_rules = steps(captions, 0.2, -0.09, 10.0, "flow")
    hl = (highlight(["svc", "ivc", "right_atrium"], 0.01, 2.5, "flow") + highlight(["tricuspid", "right_ventricle", "pulmonary_valve", "pulmonary_artery"], 2.5, 5, "flow")
          + highlight(["pulmonary_veins", "left_atrium"], 5, 7.5, "flow") + highlight(["mitral", "left_ventricle", "aortic_valve", "aorta"], 7.5, 9.95, "flow"))
    hide_cells = [{"set": c + ".visible", "value": False} for c in cells]
    rules += step_rules + hl + flow_rules + [
        {"type": "on_input", "input": "trace", "do": [{"timeline": "flow", "action": "restart"}, {"timeline": "circulation", "action": "restart"}]},
        {"type": "on_input", "input": "flow_on", "do": [{"timeline": "circulation", "action": "restart"}]},
        {"type": "on_input", "input": "flow_off", "do": [{"timeline": "circulation", "action": "pause"}] + hide_cells},
        {"type": "on_input", "input": "beat_pause", "do": [{"timeline": "beat", "action": "restart"}, {"timeline": "beat", "action": "pause"}]},
        {"type": "on_input", "input": "beat_play", "do": [{"timeline": "beat", "action": "play"}]},
    ]
    info = {"id": "info", "kind": "panel", "title": "Human heart", "widgets": [
        {"type": "note", "text": "A real adult heart, about the size of a fist, built from anatomical scans. It beats 72 times a minute. "
                                 "Blue is oxygen-poor blood, red is oxygen-rich. Turn on X-ray or cut it open to watch the blood move inside."},
        {"type": "button", "id": "trace", "label": "Trace the blood", "primary": True},
        {"type": "button", "id": "flow_on", "label": "Show blood flow", "alt_id": "flow_off", "alt_label": "Hide blood flow"},
        {"type": "button", "id": "beat_pause", "label": "Pause the beat", "alt_id": "beat_play", "alt_label": "Start the beat"},
        {"type": "button", "id": "labels_off", "label": "Hide labels", "alt_id": "labels_on", "alt_label": "Show labels"},
        {"type": "note", "text": CREDIT},
    ]}
    nodes = ([node, {"id": "beat", "kind": "timeline", "duration_s": 0.833, "loop": True, "autoplay": True}] + label_nodes + flow_nodes
             + [{"id": "circulation", "kind": "timeline", "duration_s": T, "loop": True, "autoplay": False},
                {"id": "flow", "kind": "timeline", "duration_s": 10, "loop": False, "autoplay": False}] + step_nodes + [info])
    return {"schema": "albie.scene/1", "space": "volume", "view": {"radius": 0.3}, "generators": [], "nodes": nodes, "rules": rules}, \
        manifest("heart", "Human heart", "assets/heart.glb")


def eye():
    node = {"id": "eye", "kind": "gltf", "asset": "assets/eye.glb", "pos": [0, -0.02, 0], "rot": [0, 180, 0], "scale": [6, 6, 6]}
    spec = [
        ("cornea", "Cornea", anchor("Cornea", node)),
        ("iris", "Iris", anchor("Iris", node)),
        ("lens", "Lens", anchor("Lens", node)),
        ("vitreous", "Vitreous body", anchor("Vitreous body", node)),
        ("retina", "Retina", anchor("Retina", node)),
        ("sclera", "Sclera", anchor("Sclera", node)),
        ("optic_nerve", "Optic nerve", anchor("Optic nerve", node)),
        ("muscles", "Eye muscles", anchor("Superior rectus", node)),
    ]
    label_nodes, ids = labels(spec, node, 0.2, -0.12, (-0.13, 0.11))
    rules = label_rules(ids)
    cornea, centre = L("cornea", node), L("centre", node)
    axis = [cornea[i] - centre[i] for i in range(3)]
    norm = math.sqrt(sum(a * a for a in axis)) or 1
    axis = [a / norm for a in axis]
    far = [round(cornea[i] + axis[i] * 0.12, 4) for i in range(3)]
    fovea = [round(centre[i] - axis[i] * norm * 0.98, 4) for i in range(3)]
    lens = L("lens", node)
    disc, nerve = L("optic_disc", node), L("nerve_far", node)
    D = 10.0
    rules += [
        {"type": "animate", "target": "photon", "property": "visible", "timeline": "light", "keys": [[0, False], [0.01, True], [7.4, False]]},
        {"type": "animate", "target": "photon", "property": "pos", "timeline": "light",
         "keys": [[0, far], [2.4, cornea], [3.6, lerp(cornea, lens, 0.6)], [5.0, lens], [7.3, fovea]]},
        {"type": "animate", "target": "signal", "property": "visible", "timeline": "light", "keys": [[0, False], [7.5, True], [9.9, False]]},
        {"type": "animate", "target": "signal", "property": "pos", "timeline": "light", "keys": [[7.5, fovea], [8.2, disc], [9.8, nerve]]},
        {"type": "on_input", "input": "light", "do": [{"set": f"{k}_{i}.visible", "value": True} for i in ids for k in ("label", "leader")]
         + [{"timeline": "light", "action": "restart"}]},
    ]
    captions = ["Light enters through the cornea, which bends it",
                "It passes the pupil; the iris narrows the pupil in bright light",
                "Inside, the lens focuses it onto the retina at the back of the eye",
                "The retina turns light into signals; the optic nerve carries them to the brain"]
    step_nodes, step_rules = steps(captions, 0.18, -0.12, D, "light")
    rules += step_rules + highlight(["cornea"], 0.01, 2.5, "light") + highlight(["iris"], 2.5, 5, "light") \
        + highlight(["lens", "vitreous"], 5, 7.5, "light") + highlight(["retina", "optic_nerve"], 7.5, 9.95, "light")
    info = {"id": "info", "kind": "panel", "title": "Human eye", "widgets": [
        {"type": "note", "text": "A human eye at 6 times life size; a real one is about 2.4 cm across. Cut it open to see the lens, "
                                 "the vitreous body and the retina, or separate it into its layers."},
        {"type": "button", "id": "light", "label": "Follow the light", "primary": True},
        {"type": "button", "id": "labels_off", "label": "Hide labels", "alt_id": "labels_on", "alt_label": "Show labels"},
        {"type": "note", "text": CREDIT},
    ]}
    nodes = ([node] + label_nodes + [{"id": "light", "kind": "timeline", "duration_s": D, "loop": False, "autoplay": False}] + step_nodes
             + [{"id": "photon", "kind": "primitive", "shape": "sphere", "pos": far, "size": [0.01] * 3, "color": "#FFE66D", "glow": True, "visible": False},
                {"id": "signal", "kind": "primitive", "shape": "sphere", "pos": fovea, "size": [0.01] * 3, "color": "#7FDBFF", "glow": True, "visible": False},
                info])
    return {"schema": "albie.scene/1", "space": "volume", "view": {"radius": 0.3}, "generators": [], "nodes": nodes, "rules": rules}, \
        manifest("eye", "Human eye", "assets/eye.glb")


scene, man = heart() if WHAT == "heart" else eye()
with open(os.path.join(OUT, "scene.json"), "w") as f:
    json.dump(scene, f, indent=2)
with open(os.path.join(OUT, "manifest.json"), "w") as f:
    json.dump(man, f, indent=2)
shutil.copyfile(GLB, os.path.join(OUT, "assets", f"{WHAT}.glb.bytes"))
print("SCENE_OK", WHAT, len(scene["nodes"]), "nodes", len(scene["rules"]), "rules")
