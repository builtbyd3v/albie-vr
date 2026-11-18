"""The RC truck part: the headset's driving model (TruckDesign.cs), same parameter names and the same numbers."""
import math
import re

import pytest

from cad.analysis import analyze, mass_properties
from cad.worker import build_part, load_part

from conftest import HUB, PARTS

TRUCK = PARTS / "truck.py"
DEVICE = HUB.parent / "unity" / "Assets" / "_Forge" / "Controllers" / "Scripts" / "Thing" / "TruckDesign.cs"


def metrics(params):
    module, _, _, result = build_part(TRUCK, params)
    return {m["id"]: m for m in analyze(result, mass_properties(result), module.analyze)["metrics"]}


def test_parameters_match_the_headset():
    module = load_part(TRUCK)
    if not DEVICE.exists():
        pytest.skip("no Unity project next to the hub")
    source = DEVICE.read_text()
    device = re.findall(r'P\("(\w+)", "[^"]*", "[^"]*", ([\d.]+)f, ([\d.]+)f, ([\d.]+)f', source)
    assert [n for n, *_ in device] == [p.name for p in module.PARAMS]
    for (name, lo, hi, default), p in zip(device, module.PARAMS):
        assert (float(lo), float(hi), float(default)) == (p.min, p.max, p.default), name


def test_stock_truck_drives_as_the_original_tuning():
    module = load_part(TRUCK)
    ph = module.physics(module.DEFAULTS)
    assert ph["top_speed"] == pytest.approx(3.0, abs=1e-6)
    assert ph["accel"] == pytest.approx(3.0, abs=1e-6)
    assert ph["turn_radius"] == pytest.approx(0.17 / math.tan(math.radians(35)), abs=1e-6)   # 0.243 m
    assert ph["tip_accel"] == pytest.approx(9.81 * 0.072 / 0.055)                            # 1.31 g
    assert not ph["tips_first"]                                                               # slides at 1.1 g
    assert ph["corner_speed"] == pytest.approx(math.sqrt(1.1 * 9.81 * ph["turn_radius"]))    # 1.62 m/s


def test_a_high_narrow_truck_tips_over():
    module = load_part(TRUCK)
    ph = module.physics({**module.DEFAULTS, "track_mm": 120, "ride_height_mm": 50})
    assert ph["tips_first"] and ph["tip_accel"] == pytest.approx(9.81 * 0.060 / 0.084)
    found = metrics({"track_mm": 120, "ride_height_mm": 50})
    assert found["stability"]["status"] == "bad" and found["corner_speed"]["note"] == "then it tips"


def test_motor_and_gearing_trade_speed_for_push():
    module = load_part(TRUCK)
    stock, fast = module.physics(module.DEFAULTS), module.physics({**module.DEFAULTS, "motor_kv": 4000})
    assert fast["accel"] == pytest.approx(stock["accel"] / 2) and fast["top_speed"] > 1.6 * stock["top_speed"]
    slipping = module.physics({**module.DEFAULTS, "motor_kv": 1000, "tyre_grip": 0.5})
    assert slipping["traction_limited"] and slipping["accel"] == pytest.approx(0.5 * 9.81)


def test_mass_is_the_design_mass():
    module, values, _, result = build_part(TRUCK, {"battery_cells": 3})
    total = sum(m for m, _ in mass_properties(result).values())
    assert total == pytest.approx(values["mass_g"] + 3 * 45, rel=1e-6)
