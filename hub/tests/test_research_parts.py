"""research_parts: real distributor listings or an honest "not set up". No network: the Mouser call is mocked."""
from __future__ import annotations

import asyncio
from types import SimpleNamespace

from fastapi import FastAPI
from fastapi.testclient import TestClient

from research import parts, router, tools

LISTING = {"Errors": [], "SearchResults": {"NumberOfResult": 1, "Parts": [{
    "MouserPartNumber": "000-TESTCAM", "ManufacturerPartNumber": "TESTCAM-1", "Manufacturer": "Test Maker",
    "Description": "Camera Module 5MP 3.3V", "Category": "Camera Modules", "ProductDetailUrl": "https://example.test/p",
    "DataSheetUrl": "", "Availability": "12 In Stock",
    "PriceBreaks": [{"Quantity": 1, "Price": "$24.95", "Currency": "USD"}, {"Quantity": 10, "Price": "$22.10",
                                                                         "Currency": "USD"}],
    "ProductAttributes": [{"AttributeName": "Packaging", "AttributeValue": "Bulk"}]}]}}


def test_without_a_key_it_says_not_set_up_and_lists_nothing(monkeypatch):
    monkeypatch.delenv("MOUSER_API_KEY", raising=False)
    monkeypatch.setattr(parts, "_post", lambda url, body: (_ for _ in ()).throw(AssertionError("no call")))
    out = asyncio.run(tools.call_tool("research_parts", {"query": "camera module"}))
    assert out["status"] == "not_set_up" and out["cards"] == [] and "MOUSER_API_KEY" in out["error"]
    assert any(o["supported"] for o in out["options"])


def test_listings_are_checked_against_the_limits_and_the_design(monkeypatch):
    monkeypatch.setenv("MOUSER_API_KEY", "k")
    sent = []
    monkeypatch.setattr(parts, "_post", lambda url, body: sent.append(body) or LISTING)
    record = SimpleNamespace(part="rover", params={"logic_voltage": 3.3, "payload_budget": 50},
                             param_schema=[{"name": "logic_voltage", "unit": "V"}, {"name": "payload_budget", "unit": "g"}])
    out = asyncio.run(tools.call_tool("research_parts", {"query": "camera module", "max_price_usd": 20},
                                      cad=SimpleNamespace(current=record)))
    card = out["cards"][0]
    assert sent[0]["SearchByKeywordRequest"]["keyword"] == "camera module"
    assert card["unit_price"] == 24.95 and card["mpn"] == "TESTCAM-1" and card["voltages_stated"] == [3.3]
    levels = {c["name"]: c["level"] for c in card["checks"]}
    assert levels == {"price": "bad", "voltage": "good", "weight": "unknown"}  # weight isn't listed: not guessed
    assert out["design_facts"]["design"] == "rover" and out["limits"]["max_weight_g"] == 50


def test_a_refusal_is_the_reason(monkeypatch):
    monkeypatch.setenv("MOUSER_API_KEY", "bad")
    monkeypatch.setattr(parts, "_post", lambda url, body: {"Errors": [{"Message": "Invalid unique identifier."}],
                                                           "SearchResults": None})
    out = asyncio.run(tools.call_tool("research_parts", {"query": "motor"}))
    assert "Invalid unique identifier" in out["error"]


def test_the_route(monkeypatch):
    monkeypatch.delenv("MOUSER_API_KEY", raising=False)
    app = FastAPI()
    app.include_router(router)
    client = TestClient(app)
    assert client.post("/research/parts", json={"query": "camera"}).json()["status"] == "not_set_up"
    assert client.get("/research/parts/status").json()["configured"] is False
