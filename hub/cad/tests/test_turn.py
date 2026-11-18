"""cad_turn: "rotate it", "turn it around", "spin the robot" turn the open design about its centre (cad.turn to Unity),
without ever pressing Expand or Explode, so an expanded, exploded model stays so. Also: a repeated explode says so, and
the headset lists every CAD tool exactly as the hub defines it (cloud.json, plus cad_device.json for the tools the
headset runs itself)."""
import json

import pytest
from fastapi.testclient import TestClient

from cad import tools as cad_tools
from cad.tools import call_tool
from conftest import HUB, ROCKET
from main import create_app

FORGE = HUB.parent / "unity" / "Assets" / "_Forge"


@pytest.fixture(scope="module")
def client(tmp_path_factory):
    app = create_app(data_dir=tmp_path_factory.mktemp("data"), cad_script=ROCKET, watch=False)
    with TestClient(app) as c:
        yield c


class Bus:
    def __init__(self, last_view=None):
        self.events, self.last_view = [], last_view

    async def publish(self, type_, payload):
        self.events.append((type_, payload.model_dump()))

    def latest(self, prefix=""):
        return {"cad.view": self.last_view} if self.last_view is not None and "cad.view".startswith(prefix) else {}

    def of(self, type_):
        return [p for t, p in self.events if t == type_]


async def _shown(view):
    return {"ok": True, "view": view}


async def _away(view):
    return {"error": "The user isn't at their desk"}


def tool(client, name, bus, show=_shown, **args):
    return client.portal.call(call_tool, name, args, client.app.state.cad, bus, show)


def test_rotate_with_nothing_else_turns_a_quarter_and_never_touches_expand_or_explode(client):
    bus = Bus()
    out = tool(client, "cad_turn", bus)
    assert out["ok"] and out["did"] == "turned 90 degrees"
    [turn] = bus.of("cad.turn")
    assert turn["degrees"] == 90 and turn["axis"] == "up" and turn["face"] is None and not turn["reset"]
    assert turn["part"] == client.app.state.cad.current.part and turn["version"] == client.app.state.cad.current.version
    assert [t for t, _ in bus.events] == ["cad.turn"]  # no cad.view (explode), no cad.action (expand, demo)


@pytest.mark.parametrize("args, said", [
    ({"degrees": 180}, "turned 180 degrees"),
    ({"degrees": -45}, "turned -45 degrees"),
    ({"degrees": 20, "axis": "side"}, "turned 20 degrees (tipped)"),
    ({"face": "back"}, "turned so its back faces the user"),
    ({"spin": True}, "spinning slowly"),
    ({"spin": False}, "stopped turning"),
    ({"reset": True}, "back as it first stood"),
])
def test_each_way_of_turning_goes_to_unity_as_asked(client, args, said):
    bus = Bus()
    out = tool(client, "cad_turn", bus, **args)
    assert out["did"] == said
    [turn] = bus.of("cad.turn")
    for k, v in args.items():
        assert turn[k] == v
    if "degrees" not in args:
        assert turn["degrees"] is None  # a side, a spin or a reset is not also a quarter turn


@pytest.mark.parametrize("args", [{"axis": "diagonal"}, {"face": "top"}, {"degrees": "lots"}, {"degrees": float("nan")}])
def test_bad_arguments_answer_with_an_error_and_send_nothing(client, args):
    bus = Bus()
    assert "error" in tool(client, "cad_turn", bus, **args)
    assert bus.events == []


def test_with_the_view_shut_it_says_why_and_sends_nothing(client):
    bus = Bus()
    out = tool(client, "cad_turn", bus, show=_away, degrees=90)
    assert out == {"error": "The user isn't at their desk"} and bus.events == []


def test_explode_again_while_exploded_says_so(client):
    out = tool(client, "cad_view", Bus(last_view={"explode": True}), explode=True)
    assert out["ok"] and out["already"] == ["explode"] and "already" in out["note"]
    out = tool(client, "cad_view", Bus(last_view={"explode": False}), explode=True)
    assert "already" not in out


def test_the_descriptions_route_rotate_to_cad_turn_and_name_the_humanoid():
    by = {t["name"]: t for t in cad_tools.TOOLS}
    assert "cad_turn" in by["cad_view"]["description"] and "cad_turn" in by["cad_demo"]["description"]
    assert "turn them" not in by["cad_view"]["description"]  # it read as "expand to turn it"
    for name in ("cad_list_parts", "cad_open_part", "cad_turn"):
        assert "humanoid" in by[name]["description"]
    assert "walks" in by["cad_demo"]["description"]


def test_the_headset_lists_the_cad_tools_exactly_as_the_hub_defines_them():
    """What the headset's voice model sees: cloud.json (run on the hub or the cloud) plus cad_device.json (cad_turn,
    which the headset runs itself so it works offline too)."""
    listed = json.loads((FORGE / "Core" / "Resources" / "AlbieTools" / "cloud.json").read_text())
    device = json.loads((FORGE / "CAD" / "Resources" / "AlbieTools" / "cad_device.json").read_text())
    listed += device
    headset = {t["name"]: t for t in listed if t["name"].startswith("cad_")}
    hub = {t["name"]: t for t in cad_tools.TOOLS if t["name"] != "cad_get_context"}  # delegate only
    assert set(headset) == set(hub)
    assert [t["name"] for t in device] == ["cad_turn"]
    for name, t in hub.items():
        assert headset[name]["parameters"] == t["parameters"], name
        if name != "cad_simulate_flight":  # the headset's copy leaves out "(RocketPy)": the cloud runs its own sim
            assert headset[name]["description"] == t["description"], name
