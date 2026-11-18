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


def manifest(tid, title, asset, license="CC-BY-SA-4.0"):
    sha = hashlib.sha256(open(GLB, "rb").read()).hexdigest()
    return {"schema": "albie.artifact/1", "id": tid, "title": title, "version": 1, "kind": "standalone", "category": "viz",
            "template": tid, "links": {"project": None, "project_version": None, "repo": None, "thing": None},
            "runtime": {"type": "scene", "min_runtime": 4}, "scene": "scene.json", "script": None, "inputs": [],
            "scenarios": [{"id": "default", "seed": 1, "params": {}}], "data": [],
            "assets": [{"path": asset, "sha256": sha, "license": license}],
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


OWN = "3D model: made for Albie (CC0)."


def tour(spot_id, stops, duration, timeline):
    """A glowing marker that visits each stop in turn: [(position, label ids)] -> nodes, rules."""
    span = duration / len(stops)
    keys = []
    for i, (pos, _) in enumerate(stops):
        keys += [[round(i * span + 0.01, 3), pos], [round((i + 1) * span - 0.3, 3), pos]]
    keys[0][0] = 0
    rules = [{"type": "animate", "target": spot_id, "property": "pos", "timeline": timeline, "keys": keys},
             {"type": "animate", "target": spot_id, "property": "visible", "timeline": timeline,
              "keys": [[0, False], [0.01, True], [round(duration - 0.05, 3), False]]}]
    for i, (_, ids) in enumerate(stops):
        rules += highlight(ids, round(i * span + (0.01 if i == 0 else 0), 3), round((i + 1) * span - (0.05 if i == len(stops) - 1 else 0), 3), timeline)
    node = {"id": spot_id, "kind": "primitive", "shape": "sphere", "pos": stops[0][0], "size": [0.014] * 3,
            "color": "#FFE66D", "glow": True, "visible": False}
    return [node], rules


def model_scene(node, spec, column_x, z, y_range, extra_nodes, rules, info, label_height=0.016):
    label_nodes, ids = labels(spec, node, column_x, z, y_range, label_height)
    rules = label_rules(ids) + rules
    return {"schema": "albie.scene/1", "space": "volume", "view": {"radius": 0.3}, "generators": [],
            "nodes": [node] + label_nodes + extra_nodes + [info], "rules": rules}


def brain():
    node = {"id": "brain", "kind": "gltf", "asset": "assets/brain.glb", "pos": [0, -0.01, 0], "rot": [0, 180, 0], "scale": [1.3, 1.3, 1.3]}
    A = lambda part: anchor(part, node)
    spec = [("frontal", "Frontal lobe", A("Left frontal lobe")), ("parietal", "Parietal lobe", A("Left parietal lobe")),
            ("temporal", "Temporal lobe", A("Left temporal lobe")), ("occipital", "Occipital lobe", A("Left occipital lobe")),
            ("cerebellum", "Cerebellum", A("Cerebellum")), ("brainstem", "Brainstem", A("Brainstem")),
            ("corpus_callosum", "Corpus callosum", A("Corpus callosum")), ("thalamus", "Thalamus", A("Thalamus"))]
    where = {k: a for k, _, a in spec}
    stops = [(where["frontal"], ["frontal"]), (where["parietal"], ["parietal"]), (where["occipital"], ["occipital"]),
             (where["temporal"], ["temporal"]), (where["cerebellum"], ["cerebellum"]), (where["brainstem"], ["brainstem"])]
    spot_nodes, spot_rules = tour("spot", stops, 12.0, "tour")
    captions = ["The frontal lobe plans, decides, and sends the signals that move your body",
                "The parietal lobe makes sense of touch and of where your body is",
                "The occipital lobe, at the back, turns signals from the eyes into vision",
                "The temporal lobe handles hearing, memory and understanding speech",
                "The cerebellum keeps you balanced and makes movements smooth",
                "The brainstem keeps you breathing and your heart beating"]
    step_nodes, step_rules = steps(captions, 0.17, -0.12, 12.0, "tour")
    rules = spot_rules + step_rules + [{"type": "on_input", "input": "tour", "do": [{"timeline": "tour", "action": "restart"}]}]
    info = {"id": "info", "kind": "panel", "title": "Human brain", "widgets": [
        {"type": "note", "text": "A real adult brain from anatomical scans, its left side toward you and the front to your left. "
                                 "It weighs about 1.4 kg. Cut it open down the middle, or separate it into its lobes and deep parts."},
        {"type": "button", "id": "tour", "label": "Tour the lobes", "primary": True},
        {"type": "button", "id": "labels_off", "label": "Hide labels", "alt_id": "labels_on", "alt_label": "Show labels"},
        {"type": "note", "text": CREDIT}]}
    extra = [{"id": "tour", "kind": "timeline", "duration_s": 12, "loop": False, "autoplay": False}] + step_nodes + spot_nodes
    return model_scene(node, spec, 0.2, -0.12, (-0.12, 0.12), extra, rules, info), manifest("brain", "Human brain", "assets/brain.glb")


def dna():
    node = {"id": "dna", "kind": "gltf", "asset": "assets/dna.glb", "pos": [0, 0, 0], "rot": [0, 180, 0], "scale": [1, 1, 1]}
    spec = [("backbone", "Backbone", L("backbone", node)), ("base", "Base", L("base", node)),
            ("pair", "Base pair", L("pair", node)), ("bonds", "Hydrogen bonds", L("bond", node)),
            ("strand2", "Second strand", L("strand2", node))]
    where = {k: a for k, _, a in spec}
    stops = [(where["backbone"], ["backbone", "strand2"]), (where["pair"], ["pair"]), (where["bonds"], ["bonds"]),
             (where["base"], ["base"])]
    spot_nodes, spot_rules = tour("spot", stops, 10.0, "explain")
    captions = ["Two strands twist around each other: a double helix",
                "Each rung is a base pair: A with T, and G with C",
                "Hydrogen bonds are weak, so the strands can unzip to be copied",
                "The order of the bases is the code: about 3 billion pairs in one human genome"]
    step_nodes, step_rules = steps(captions, 0.15, -0.1, 10.0, "explain")
    ids = [k for k, _, _ in spec]
    hide = [{"set": f"{k}_{i}.visible", "value": False} for i in ids for k in ("label", "leader")]
    rules = spot_rules + step_rules + [
        {"type": "animate", "target": "dna", "property": "rot", "timeline": "spin", "keys": [[0, [0, 180, 0]], [12, [0, 540, 0]]]},
        {"type": "on_input", "input": "explain", "do": [{"timeline": "explain", "action": "restart"}]},
        {"type": "on_input", "input": "spin_on", "do": hide + [{"timeline": "spin", "action": "play"}]},
        {"type": "on_input", "input": "spin_off", "do": [{"timeline": "spin", "action": "restart"}, {"timeline": "spin", "action": "pause"}]},
    ]
    info = {"id": "info", "kind": "panel", "title": "DNA double helix", "widgets": [
        {"type": "note", "text": "Twenty base pairs of B-DNA, about 100 million times life size: 10 pairs per turn, 2 nm wide, with "
                                 "the major and minor grooves. Bases: A green, T red, G yellow, C blue. A-T pairs share two hydrogen "
                                 "bonds, G-C pairs three. Separate unzips the two strands."},
        {"type": "button", "id": "explain", "label": "Explain it", "primary": True},
        {"type": "button", "id": "spin_on", "label": "Spin it", "alt_id": "spin_off", "alt_label": "Stop spinning"},
        {"type": "button", "id": "labels_off", "label": "Hide labels", "alt_id": "labels_on", "alt_label": "Show labels"},
        {"type": "note", "text": OWN}]}
    extra = [{"id": "explain", "kind": "timeline", "duration_s": 10, "loop": False, "autoplay": False},
             {"id": "spin", "kind": "timeline", "duration_s": 12, "loop": True, "autoplay": False}] + step_nodes + spot_nodes
    return model_scene(node, spec, 0.13, -0.08, (-0.08, 0.08), extra, rules, info), manifest("dna", "DNA double helix", "assets/dna.glb", "CC0")


def cell():
    node = {"id": "cell", "kind": "gltf", "asset": "assets/cell.glb", "pos": [0, 0, 0], "rot": [0, 180, 0], "scale": [1.1, 1.1, 1.1]}
    spec = [(k, t, L(k, node)) for k, t in (("nucleus", "Nucleus"), ("nucleolus", "Nucleolus"), ("rough_er", "Rough ER"),
            ("golgi", "Golgi apparatus"), ("mitochondrion", "Mitochondrion"), ("lysosome", "Lysosome"),
            ("centrosome", "Centrosome"), ("membrane", "Cell membrane"), ("cytoplasm", "Cytoplasm"))]
    nucleus, er, golgi, membrane = L("nucleus", node), L("rough_er", node), L("golgi", node), L("membrane", node)
    outside = [round(v, 4) for v in lerp(L("centre", node), membrane, 1.35)]
    rules = [
        {"type": "animate", "target": "tracer", "property": "visible", "timeline": "pathway", "keys": [[0, False], [0.01, True], [9.9, False]]},
        {"type": "animate", "target": "tracer", "property": "pos", "timeline": "pathway",
         "keys": [[0, nucleus], [2.3, er], [4.8, er], [7.0, golgi], [9.0, membrane], [9.8, outside]]},
        {"type": "animate", "target": "tracer", "property": "color", "timeline": "pathway",
         "keys": [[0, "#7FDBFF"], [2.4, "#7FDBFF"], [2.6, "#FFE66D"], [7.0, "#FFE66D"], [7.2, "#FF9F43"]]},
        {"type": "on_input", "input": "follow", "do": [{"timeline": "pathway", "action": "restart"}]},
    ]
    captions = ["The DNA's instructions are copied to mRNA, which leaves the nucleus",
                "Ribosomes on the rough ER read the mRNA and build the protein",
                "The Golgi apparatus modifies the protein and packs it into a vesicle",
                "The vesicle fuses with the membrane and releases the protein"]
    step_nodes, step_rules = steps(captions, 0.17, -0.13, 10.0, "pathway")
    rules += step_rules + highlight(["nucleus"], 0.01, 2.5, "pathway") + highlight(["rough_er"], 2.5, 5, "pathway") \
        + highlight(["golgi"], 5, 7.5, "pathway") + highlight(["membrane"], 7.5, 9.95, "pathway")
    info = {"id": "info", "kind": "panel", "title": "Animal cell", "widgets": [
        {"type": "note", "text": "A typical animal cell, about 10 000 times life size (a real one is 10 to 30 micrometres across). "
                                 "Its membrane is see-through; cut it open or separate it to see each organelle."},
        {"type": "button", "id": "follow", "label": "Follow a protein", "primary": True},
        {"type": "button", "id": "labels_off", "label": "Hide labels", "alt_id": "labels_on", "alt_label": "Show labels"},
        {"type": "note", "text": OWN}]}
    extra = [{"id": "pathway", "kind": "timeline", "duration_s": 10, "loop": False, "autoplay": False}] + step_nodes + [
        {"id": "tracer", "kind": "primitive", "shape": "sphere", "pos": nucleus, "size": [0.009] * 3, "color": "#7FDBFF", "glow": True, "visible": False}]
    return model_scene(node, spec, 0.19, -0.13, (-0.12, 0.12), extra, rules, info), manifest("cell", "Animal cell", "assets/cell.glb", "CC0")


def plant_cell():
    node = {"id": "plant_cell", "kind": "gltf", "asset": "assets/plant_cell.glb", "pos": [0, 0, 0], "rot": [0, 180, 0], "scale": [1.1, 1.1, 1.1]}
    spec = [(k, t, L(k, node)) for k, t in (("wall", "Cell wall"), ("vacuole", "Central vacuole"), ("chloroplast", "Chloroplast"),
            ("nucleus", "Nucleus"), ("nucleolus", "Nucleolus"), ("rough_er", "Rough ER"), ("golgi", "Golgi apparatus"),
            ("cytoplasm", "Cytoplasm"))]
    vac, chl, cyt, wall = L("vacuole", node), L("chloroplast", node), L("cytoplasm", node), L("wall", node)
    sky = [chl[0], round(chl[1] + 0.25, 4), chl[2]]
    out = [round(v, 4) for v in lerp(L("centre", node), wall, 1.6)]
    rules = [
        {"type": "animate", "target": "molecule", "property": "visible", "timeline": "photosynthesis", "keys": [[0, False], [0.01, True], [9.9, False]]},
        {"type": "animate", "target": "molecule", "property": "pos", "timeline": "photosynthesis", "keys": [[0, vac], [2.4, chl], [7.4, chl], [9.8, cyt]]},
        {"type": "animate", "target": "molecule", "property": "color", "timeline": "photosynthesis",
         "keys": [[0, "#7FDBFF"], [5.0, "#7FDBFF"], [5.2, "#FFFFFF"]]},
        {"type": "animate", "target": "sunbeam", "property": "visible", "timeline": "photosynthesis", "keys": [[0, False], [2.5, True], [5.0, False]]},
        {"type": "animate", "target": "oxygen", "property": "visible", "timeline": "photosynthesis", "keys": [[0, False], [5.2, True], [9.9, False]]},
        {"type": "animate", "target": "oxygen", "property": "pos", "timeline": "photosynthesis", "keys": [[5.2, chl], [7.5, wall], [9.8, out]]},
        {"type": "on_input", "input": "sun", "do": [{"timeline": "photosynthesis", "action": "restart"}]},
    ]
    captions = ["Water stored in the central vacuole moves to a chloroplast",
                "Sunlight reaches the chloroplast, and its green stacks capture the energy",
                "It joins water and carbon dioxide into sugar, giving off oxygen",
                "Sugar feeds the cell through the cytoplasm; oxygen leaves through the wall"]
    step_nodes, step_rules = steps(captions, 0.17, -0.13, 10.0, "photosynthesis")
    rules += step_rules + highlight(["vacuole"], 0.01, 2.5, "photosynthesis") + highlight(["chloroplast"], 2.5, 7.5, "photosynthesis") \
        + highlight(["cytoplasm", "wall"], 7.5, 9.95, "photosynthesis")
    info = {"id": "info", "kind": "panel", "title": "Plant cell", "widgets": [
        {"type": "note", "text": "A leaf cell, about 2 000 times life size. Unlike an animal cell it has a cell wall, chloroplasts and "
                                 "one big central vacuole. Cut it open or separate it to see inside."},
        {"type": "button", "id": "sun", "label": "Follow the sunlight", "primary": True},
        {"type": "button", "id": "labels_off", "label": "Hide labels", "alt_id": "labels_on", "alt_label": "Show labels"},
        {"type": "note", "text": OWN}]}
    extra = [{"id": "photosynthesis", "kind": "timeline", "duration_s": 10, "loop": False, "autoplay": False}] + step_nodes + [
        {"id": "molecule", "kind": "primitive", "shape": "sphere", "pos": vac, "size": [0.008] * 3, "color": "#7FDBFF", "glow": True, "visible": False},
        {"id": "oxygen", "kind": "primitive", "shape": "sphere", "pos": chl, "size": [0.007] * 3, "color": "#FF6B6B", "glow": True, "visible": False},
        {"id": "sunbeam", "kind": "line", "points": [sky, chl], "width": 0.004, "color": "#FFE66D", "visible": False}]
    return model_scene(node, spec, 0.21, -0.13, (-0.12, 0.12), extra, rules, info), manifest("plant_cell", "Plant cell", "assets/plant_cell.glb", "CC0")


BP3D_CREDIT = ("3D model: BodyParts3D, (c) 2008 Life Science Integrated Database Center (CC BY-SA 2.1 JP). "
               "This model is shared under CC BY-SA 4.0.")


def breathing(node, timeline, duration=4.0, depth=0.035):
    """The model swells and settles once a breath (about 15 a minute), about its own centre, like the heart's beat."""
    rest = node["scale"][0]
    keys = [[0, 1.0], [round(duration * 0.4, 3), 1 + depth], [duration, 1.0]]
    return [{"type": "animate", "target": node["id"], "property": "scale", "timeline": timeline,
             "keys": [[t, round(rest * k, 4)] for t, k in keys]},
            {"type": "animate", "target": node["id"], "property": "pos", "timeline": timeline,
             "keys": [[t, node["pos"]] for t, _ in keys]}]


def lungs():
    node = {"id": "lungs", "kind": "gltf", "asset": "assets/lungs.glb", "pos": [0, 0, 0], "rot": [0, 180, 0], "scale": [0.9, 0.9, 0.9]}
    A = lambda part: anchor(part, node)
    spec = [("trachea", "Trachea", A("Trachea")), ("bronchi", "Bronchi", A("Bronchi")),
            ("right_upper", "Right upper lobe", A("Right upper lobe")), ("right_middle", "Right middle lobe", A("Right middle lobe")),
            ("right_lower", "Right lower lobe", A("Right lower lobe")), ("left_upper", "Left upper lobe", A("Left upper lobe")),
            ("left_lower", "Left lower lobe", A("Left lower lobe")), ("arteries", "Pulmonary arteries", A("Pulmonary arteries")),
            ("veins", "Pulmonary veins", A("Pulmonary veins"))]
    top, car = L("trachea_top", node), L("carina", node)
    D = 12.0
    air_nodes, air_rules = [], []
    for i, (lobe, side) in enumerate((("Right upper lobe", "right"), ("Right middle lobe", "right"), ("Right lower lobe", "right"),
                                      ("Left upper lobe", "left"), ("Left lower lobe", "left"))):
        path = [top, car, L(side + "_bronchus", node), L(lobe, node)]
        nn, rr = train(f"air{i + 1}_", path, 2, "#BFEFFF", 0.008, D, "breath")
        air_nodes += nn; air_rules += rr
    captions = ["Air comes in through the trachea, held open by rings of cartilage",
                "At the carina it splits into the right and left main bronchi",
                "The bronchi branch about 23 times, into every lobe: three on the right, two on the left",
                "At the tips, 300 million tiny air sacs pass oxygen into the blood",
                "The pulmonary arteries bring oxygen-poor blood in; the veins carry it back to the heart"]
    step_nodes, step_rules = steps(captions, 0.2, -0.12, D, "breath")
    span = D / len(captions)
    hl = (highlight(["trachea"], 0.01, span, "breath") + highlight(["bronchi"], span, 2 * span, "breath")
          + highlight(["right_upper", "right_middle", "right_lower", "left_upper", "left_lower"], 2 * span, 3 * span, "breath")
          + highlight(["arteries", "veins"], 4 * span, D - 0.05, "breath"))  # step 4 (the air sacs) is inside every lobe: no label of its own
    rules = breathing(node, "breathing") + step_rules + hl + air_rules + [
        {"type": "on_input", "input": "breath", "do": [{"timeline": "breath", "action": "restart"}]},
        {"type": "on_input", "input": "breathe_pause", "do": [{"timeline": "breathing", "action": "restart"}, {"timeline": "breathing", "action": "pause"}]},
        {"type": "on_input", "input": "breathe_play", "do": [{"timeline": "breathing", "action": "play"}]}]
    info = {"id": "info", "kind": "panel", "title": "Human lungs", "widgets": [
        {"type": "note", "text": "A real adult pair of lungs from anatomical scans, shown with the airways and blood vessels inside. "
                                 "The right lung has three lobes and the left two, leaving room for the heart. Together they hold about 6 litres of air. "
                                 "Cut them open, X-ray them, or separate the lobes."},
        {"type": "button", "id": "breath", "label": "Follow a breath", "primary": True},
        {"type": "button", "id": "breathe_pause", "label": "Hold the breath", "alt_id": "breathe_play", "alt_label": "Breathe"},
        {"type": "button", "id": "labels_off", "label": "Hide labels", "alt_id": "labels_on", "alt_label": "Show labels"},
        {"type": "note", "text": BP3D_CREDIT}]}
    extra = [{"id": "breathing", "kind": "timeline", "duration_s": 4.0, "loop": True, "autoplay": True},
             {"id": "breath", "kind": "timeline", "duration_s": D, "loop": False, "autoplay": False}] + step_nodes + air_nodes
    return model_scene(node, spec, 0.21, -0.12, (-0.12, 0.12), extra, rules, info, 0.015), manifest("lungs", "Human lungs", "assets/lungs.glb")


def kidneys():
    node = {"id": "kidneys", "kind": "gltf", "asset": "assets/kidneys.glb", "pos": [0, 0, 0], "rot": [0, 180, 0], "scale": [0.75, 0.75, 0.75]}
    A = lambda part: anchor(part, node)
    spec = [("kidney", "Kidney", A("Left kidney")), ("pelvis", "Renal pelvis", A("Left renal pelvis")),
            ("adrenal", "Adrenal gland", A("Left adrenal gland")), ("artery", "Renal artery", A("Right renal artery")),
            ("vein", "Renal vein", A("Right renal vein")), ("ureter", "Ureter", A("Right ureter")),
            ("aorta", "Aorta", A("Aorta")), ("ivc", "Inferior vena cava", A("Inferior vena cava")),
            ("bladder", "Bladder", A("Urinary bladder"))]
    D = 12.0
    flow_nodes, flow_rules = [], []
    for k in ("left", "right"):
        paths = [("blood_" + k, [L("aorta_top", node), L("aorta_" + k, node), L("artery_" + k, node), L("hilum_" + k, node), L("kidney_" + k, node)], "#FF5A5A"),
                 ("clean_" + k, [L("kidney_" + k, node), L("vein_" + k, node), L("ivc_" + k, node)], "#5A8CFF"),
                 ("urine_" + k, [L("pelvis_" + k, node)] + [L(f"ureter_{k}_{i}", node) for i in range(6)] + [L("bladder", node)], "#FFE066")]
        for prefix, pts, colour in paths:
            nn, rr = train(prefix + "_", pts, 2, colour, 0.009, D, "blood")
            flow_nodes += nn; flow_rules += rr
    captions = ["Blood arrives from the aorta through the renal arteries: a fifth of every heartbeat",
                "In each kidney about a million nephrons filter it, some 180 litres a day",
                "Clean blood leaves through the renal veins into the inferior vena cava",
                "What's filtered out becomes urine and collects in the renal pelvis",
                "The ureters carry it down to the bladder, which holds about half a litre"]
    step_nodes, step_rules = steps(captions, 0.2, -0.12, D, "blood")
    span = D / len(captions)
    hl = (highlight(["aorta", "artery"], 0.01, span, "blood") + highlight(["kidney"], span, 2 * span, "blood")
          + highlight(["vein", "ivc"], 2 * span, 3 * span, "blood") + highlight(["pelvis"], 3 * span, 4 * span, "blood")
          + highlight(["ureter", "bladder"], 4 * span, D - 0.05, "blood"))
    rules = step_rules + hl + flow_rules + [{"type": "on_input", "input": "blood", "do": [{"timeline": "blood", "action": "restart"}]}]
    info = {"id": "info", "kind": "panel", "title": "Kidneys", "widgets": [
        {"type": "note", "text": "A real adult pair of kidneys from anatomical scans, with their blood supply, the ureters and the bladder. "
                                 "Each kidney is about 11 cm long. Cut them open or separate them to see the renal pelvis that collects urine."},
        {"type": "button", "id": "blood", "label": "Follow the blood", "primary": True},
        {"type": "button", "id": "labels_off", "label": "Hide labels", "alt_id": "labels_on", "alt_label": "Show labels"},
        {"type": "note", "text": CREDIT}]}
    extra = [{"id": "blood", "kind": "timeline", "duration_s": D, "loop": False, "autoplay": False}] + step_nodes + flow_nodes
    return model_scene(node, spec, 0.2, -0.12, (-0.13, 0.12), extra, rules, info, 0.015), manifest("kidneys", "Human kidneys", "assets/kidneys.glb")


def stomach():
    node = {"id": "stomach", "kind": "gltf", "asset": "assets/stomach.glb", "pos": [0, 0, 0], "rot": [0, 180, 0], "scale": [1.1, 1.1, 1.1]}
    A = lambda part: anchor(part, node)
    spec = [("oesophagus", "Oesophagus", A("Oesophagus")), ("fundus", "Fundus", A("Fundus")),
            ("body", "Body", A("Body of stomach")), ("antrum", "Pyloric antrum", A("Pyloric antrum")),
            ("pylorus", "Pylorus", A("Pylorus")), ("duodenum", "Duodenum", A("Duodenum")),
            ("rugae", "Rugae (folds)", A("Lining (rugae)"))]
    D = 12.0
    route = [L("oesophagus_top", node), L("cardia", node), L("Fundus", node), L("Body of stomach", node),
             L("Pyloric antrum", node), L("pylorus", node), L("duodenum_mid", node), L("duodenum_end", node)]
    times = [0.01, 2.2, 3.6, 5.6, 7.6, 9.4, 10.6, 11.8]
    rules = [{"type": "animate", "target": "bolus", "property": "visible", "timeline": "meal", "keys": [[0, False], [0.01, True], [11.9, False]]},
             {"type": "animate", "target": "bolus", "property": "pos", "timeline": "meal", "keys": [[t, p] for t, p in zip(times, route)]},
             {"type": "animate", "target": "bolus", "property": "color", "timeline": "meal",
              "keys": [[0, "#C9A27E"], [5.6, "#C9A27E"], [7.6, "#E6D3A3"], [11.9, "#E6D3A3"]]}]
    captions = ["Food is pushed down the oesophagus and enters the stomach at the cardia",
                "The fundus and body store it; the wall stretches from 50 ml to over a litre",
                "Acid and the enzyme pepsin start digesting protein; the rugae unfold as it fills",
                "In the antrum, waves of muscle churn it into a paste called chyme",
                "The pylorus lets a little through at a time into the duodenum"]
    step_nodes, step_rules = steps(captions, 0.2, -0.12, D, "meal")
    span = D / len(captions)
    hl = (highlight(["oesophagus"], 0.01, span, "meal") + highlight(["fundus", "body"], span, 2 * span, "meal")
          + highlight(["rugae"], 2 * span, 3 * span, "meal") + highlight(["antrum"], 3 * span, 4 * span, "meal")
          + highlight(["pylorus", "duodenum"], 4 * span, D - 0.05, "meal"))
    rules += step_rules + hl + [{"type": "on_input", "input": "meal", "do": [{"timeline": "meal", "action": "restart"}]}]
    info = {"id": "info", "kind": "panel", "title": "Stomach", "widgets": [
        {"type": "note", "text": "A real adult stomach from anatomical scans, with the end of the oesophagus and the duodenum. "
                                 "Empty it holds about 50 ml and its lining lies in folds; after a meal it stretches to 1 to 1.5 litres. "
                                 "Cut it open to see the folds, or separate its regions."},
        {"type": "button", "id": "meal", "label": "Follow a meal", "primary": True},
        {"type": "button", "id": "labels_off", "label": "Hide labels", "alt_id": "labels_on", "alt_label": "Show labels"},
        {"type": "note", "text": BP3D_CREDIT}]}
    extra = [{"id": "meal", "kind": "timeline", "duration_s": D, "loop": False, "autoplay": False}] + step_nodes + [
        {"id": "bolus", "kind": "primitive", "shape": "sphere", "pos": route[0], "size": [0.013] * 3, "color": "#C9A27E", "glow": True, "visible": False}]
    return model_scene(node, spec, 0.2, -0.12, (-0.12, 0.12), extra, rules, info, 0.015), manifest("stomach", "Human stomach", "assets/stomach.glb")


scene, man = {"heart": heart, "eye": eye, "brain": brain, "dna": dna, "cell": cell, "plant_cell": plant_cell,
              "lungs": lungs, "kidneys": kidneys, "stomach": stomach}[WHAT]()
with open(os.path.join(OUT, "scene.json"), "w") as f:
    json.dump(scene, f, indent=2)
with open(os.path.join(OUT, "manifest.json"), "w") as f:
    json.dump(man, f, indent=2)
shutil.copyfile(GLB, os.path.join(OUT, "assets", f"{WHAT}.glb.bytes"))
print("SCENE_OK", WHAT, len(scene["nodes"]), "nodes", len(scene["rules"]), "rules")
