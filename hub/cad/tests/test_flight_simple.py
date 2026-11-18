"""The simplified rocket flight (cad/flight_simple.py), the model the headset falls back to with the cloud down, and
the wind option of the RocketPy flight (cad/flight.py, GET /cad/flight?wind=, cad_simulate_flight wind_mps).

The hand calculations need nothing but the standard library; the cross-checks against RocketPy and against the
built part's own mass analysis are skipped when rocketpy / build123d are not installed."""
import importlib.util
import math

import pytest

from cad import flight_simple as fs

HAVE_ROCKETPY = importlib.util.find_spec("rocketpy") is not None
HAVE_BUILD123D = importlib.util.find_spec("build123d") is not None
SMALL_FINS = {"fin_root": 42, "fin_tip": 21, "fin_span": 36, "fin_sweep": 21}   # rocket.py's "Smaller fins"


def test_thrust_curve_is_a_c6():
    # A C class motor is 5 to 10 N s; the C6's certification data says about 8.8 N s over 1.86 s.
    assert 8.5 < fs.TOTAL_IMPULSE < 9.2 and fs.BURNOUT_S == pytest.approx(1.86)
    assert fs.thrust(0.192)[0] == pytest.approx(14.09) and fs.thrust(2.0) == (0.0, fs.TOTAL_IMPULSE)


def test_no_drag_vertical_climb_matches_the_hand_calculation(monkeypatch):
    """With no drag, no rod tilt and no parachute, the apogee has a closed form. Burnout speed from the rocket
    equation in its impulse form, dv = integral F/m dt - g t_b, done here by hand with the same curve; then the
    coast v^2 / 2g. The integrator must land within 1 %."""
    monkeypatch.setattr(fs, "DRAG_CD", 0.0)
    monkeypatch.setattr(fs, "PARACHUTE_CD", 0.0)
    monkeypatch.setattr(fs, "INCLINATION_DEG", 90.0)
    liftoff_g = 80.0
    dry = (liftoff_g - fs.MOTOR_PROPELLANT_G) / 1000
    # Hand integration of the boost with a fine fixed step (independent of the model's RK2 loop).
    v = h = t = 0.0
    dt = 1e-4
    while t < fs.BURNOUT_S:
        f, impulse = fs.thrust(t)
        m = dry + fs.MOTOR_PROPELLANT_G / 1000 * (1 - impulse / fs.TOTAL_IMPULSE)
        a = max(f / m - fs.G, 0.0) if v == 0.0 else f / m - fs.G
        v += a * dt
        h += v * dt
        t += dt
    apogee = h + v * v / (2 * fs.G)
    out = fs.fly(liftoff_g, 40.0)
    assert out["apogee_m"] == pytest.approx(apogee, rel=0.01)
    assert out["time_to_apogee_s"] == pytest.approx(fs.BURNOUT_S + v / fs.G, rel=0.01)


def test_descent_rate_is_the_parachute_terminal_speed():
    """Under the full parachute it falls at sqrt(2 m g / (rho Cd A)), body drag included."""
    out = fs.simulate_params()
    m = (out["liftoff_mass_g"] - fs.MOTOR_PROPELLANT_G) / 1000
    cda = fs.PARACHUTE_CD * math.pi * (fs.PARACHUTE_MM / 2000) ** 2 + fs.DRAG_CD * math.pi * 0.02 ** 2
    assert out["descent_rate_mps"] == pytest.approx(math.sqrt(2 * m * fs.G / (1.225 * cda)), abs=0.15)


def test_result_shape_and_physics_directions():
    calm, windy = fs.simulate_params(), fs.simulate_params(wind_mps=5)
    for f in (calm, windy):
        assert f["model"] == "simplified" and f["flight_time_s"] == f["landing_time_s"]
        assert 150 < f["apogee_m"] < 250 and 4 < f["time_to_apogee_s"] < 8 and 50 < f["max_speed_mps"] < 100
        assert f["samples"][0]["t"] == 0 and f["samples"][-1]["alt_m"] == 0 and len(f["samples"]) <= fs.MAX_SAMPLES
        assert [e["id"] for e in f["events"]] == ["liftoff", "rail_exit", "burnout", "apogee", "deploy", "landing"]
        assert max(s["alt_m"] for s in f["samples"]) == pytest.approx(f["apogee_m"], abs=0.5)
    assert windy["drift_m"] > calm["drift_m"] + 50      # the parachute drifts with the wind
    assert windy["apogee_m"] < calm["apogee_m"]         # weathercocking into the wind costs height
    assert calm["stability_margin_cal"] > 1.0 and calm["warning"] is None
    small = fs.simulate_params(SMALL_FINS)
    assert small["stability_margin_cal"] < 1.0 and "tumble" in small["warning"]
    assert fs.simulate_params({"fin_count": 4})["stability_margin_cal"] > calm["stability_margin_cal"]


@pytest.mark.skipif(not HAVE_BUILD123D, reason="needs build123d to build the real part")
@pytest.mark.parametrize("params", [{}, SMALL_FINS, {"body_length": 500, "fin_count": 4, "nose_shape": "cone"}])
def test_mass_estimate_matches_the_built_part(params):
    """The headset's mass estimate (no CAD kernel) against the part's own analysis from its geometry."""
    from cad.analysis import analyze, mass_properties
    from cad.worker import build_part
    from conftest import ROCKET

    module, values, _, result = build_part(ROCKET, params)
    a = analyze(result, mass_properties(result), module.analyze, features=["flight"])
    total, cg_z, _ = fs.estimate_mass(values)
    length = values["body_length"] + values["nose_length"]
    assert total == pytest.approx(a["mass_g"], rel=0.03)
    assert length - cg_z == pytest.approx(a["cg_mm"], abs=4.0)
    assert fs.simulate_params(values)["stability_margin_cal"] == pytest.approx(a["margin_cal"], abs=0.1)


@pytest.mark.skipif(not (HAVE_ROCKETPY and HAVE_BUILD123D), reason="needs rocketpy and build123d")
@pytest.mark.parametrize("params,wind", [({}, 0.0), (SMALL_FINS, 0.0), ({}, 5.0)])
def test_simplified_model_tracks_rocketpy(params, wind, tmp_path):
    """The fallback is a different, cruder model: within 5 % of RocketPy's apogee and times with no wind; with wind
    it also drifts further than with none, as RocketPy does."""
    from cad.worker import run_job
    from conftest import ROCKET

    r = run_job({"op": "flight", "script": str(ROCKET), "params": params, "out_dir": str(tmp_path), "wind_mps": wind})
    assert r["ok"], r.get("traceback") or r["error"]
    real, simple = r["flight"], fs.simulate_params(params, wind)
    assert real["model"] == "rocketpy" and real["wind_mps"] == wind
    tol = 0.05 if not wind else 0.08
    assert simple["apogee_m"] == pytest.approx(real["apogee_m"], rel=tol)
    assert simple["time_to_apogee_s"] == pytest.approx(real["time_to_apogee_s"], rel=tol)
    assert simple["max_speed_mps"] == pytest.approx(real["max_speed_mps"], rel=tol)
    assert simple["landing_time_s"] == pytest.approx(real["landing_time_s"], rel=tol)
    assert simple["stability_margin_cal"] == pytest.approx(real["stability_margin_cal"], abs=0.15)
    assert real["flight_time_s"] == real["landing_time_s"]
    if wind:
        calm = run_job({"op": "flight", "script": str(ROCKET), "params": params, "out_dir": str(tmp_path / "calm")})
        assert real["drift_m"] > calm["flight"]["drift_m"] + 50
        assert (tmp_path / "flight.json").exists()
