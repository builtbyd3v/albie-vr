"""C-19 controller mocks (docs/CONTROLLER_CONTRACT.md): the generated files follow the contract and stay current."""

import json

import numpy as np
import pytest

from ml import make_controller_mocks as m


@pytest.fixture(scope="module")
def built(tmp_path_factory):
    out = tmp_path_factory.mktemp("controllers")
    report = m.build(out)
    load = lambda name: json.loads((out / name).read_text())  # noqa: E731
    return out, report, {name: load(name) for name in
                         ("laps/truck_v2.json", "truck_manual.json", "truck_cruise.json", "truck_autopilot.json")}


def test_laps_file_matches_contract(built):
    _, report, files = built
    laps = files["laps/truck_v2.json"]
    assert laps["schema"] == "albie.laps/1" and laps["hz"] == 50
    assert laps["sensors"] == m.SENSORS and laps["actuators"] == m.ACTUATORS
    assert sum(lap["outcome"] == "completed" for lap in laps["laps"]) == 2
    for lap in laps["laps"]:
        assert len(lap["x"]) == len(lap["y"]) > 100
        x, y = np.array(lap["x"]), np.array(lap["y"])
        assert x.shape[1] == len(m.SENSORS) and y.shape[1] == len(m.ACTUATORS)
        assert (x[:, :m.RAYS] >= 0).all() and (x[:, :m.RAYS] <= 1).all()   # rays: 0..1, 1 = nothing hit
        assert (np.abs(x) <= 1).all() and (np.abs(y) <= 1).all()
        assert (x[:, :m.RAYS] < 1).any()                                     # the rays actually see the cones


def test_mirror_map_names_real_channels_and_is_its_own_inverse(built):
    _, _, files = built
    mirror = files["laps/truck_v2.json"]["mirror"]
    channels = set(m.SENSORS) | set(m.ACTUATORS)
    assert all(a in channels and b in channels for a, b in mirror["swap"])
    assert all(c in channels for c in mirror["negate"])
    x = np.array(files["laps/truck_v2.json"]["laps"][0]["x"])
    y = np.array(files["laps/truck_v2.json"]["laps"][0]["y"])
    mx, my = m.mirror(*m.mirror(x, y))
    assert np.array_equal(mx, x) and np.array_equal(my, y)


def test_controllers_only_use_known_channels(built):
    _, _, files = built
    sensors, actuators = set(m.SENSORS), set(m.ACTUATORS)
    for name in ("truck_manual.json", "truck_cruise.json", "truck_autopilot.json"):
        c = files[name]
        assert c["schema"] == "albie.controller/1" and f"{c['name']}.json" == name
        assert c["limits"]["speed"] == "$max_speed" and "max_speed" in c["tunables"]
        t = c["tunables"]["max_speed"]
        assert t["min"] <= t["value"] <= t["max"]
    assert {b["actuator"] for b in files["truck_manual.json"]["bindings"]} <= actuators
    for node in files["truck_cruise.json"]["nodes"]:
        reads = [node.get("input"), node.get("a"), node.get("b"), (node.get("if") or [None])[0]]
        assert {r for r in reads if isinstance(r, str) and not r.startswith("$")} <= sensors
        writes = [node["output"]] if "output" in node else list(node["set"])
        assert set(writes) <= actuators


def test_autopilot_net_shapes_follow_nnnetwork_layout(built):
    _, report, files = built
    c = files["truck_autopilot.json"]
    sizes = c["net"]["sizes"]
    assert sizes[0] == len(c["inputs"]) and sizes[-1] == len(c["outputs"])
    assert [i["channel"] for i in c["inputs"]] == m.SENSORS
    for l, (w, b) in enumerate(zip(c["net"]["weights"], c["net"]["biases"])):
        assert len(w) == len(b) == sizes[l + 1]              # weights[l][to][from]
        assert all(len(row) == sizes[l] for row in w)
        assert all(np.isfinite(v) for row in w for v in row)
    assert report["closed_loop"] == "completed"              # the mock autopilot can drive the mock slalom


def test_committed_laps_are_current(built):
    """The laps in Unity's Mocks come from this generator. Rerun `python -m ml.make_controller_mocks` if this fails."""
    out, _, _ = built
    committed = m.OUT / "laps" / "truck_v2.json"
    assert committed.exists(), "run `python -m ml.make_controller_mocks` from hub/"
    assert json.loads(committed.read_text()) == json.loads((out / "laps" / "truck_v2.json").read_text())
