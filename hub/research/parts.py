"""Parts research (cameras, motors, sensors...): real distributor listings, checked against the design's numbers.

There is no free, keyless parts API (checked 2026-11-17 with plain requests): Mouser's Search API answers
"API Key Required", DigiKey's v4 search wants an X-DIGIKEY-Client-Id (OAuth client), Nexar/Octopart's GraphQL wants
a token, Octopart's site and LCSC's search are web pages behind bot protection. So this module uses **Mouser's
Search API with a server key** (`MOUSER_API_KEY`, free to request on mouser.com for a registered account; one key
for the whole server, not per user). With no key it answers a clear "not set up" with the options, and never
invents a part.

Every number on a card comes from the distributor's answer: price breaks, stock, the description and the listed
attributes. Specs a listing doesn't state (weight, often voltage) are "unknown", not guessed. Checks compare what
is stated with the caller's limits (max_price_usd, voltage_v, max_weight_g) and with the open CAD design's
parameters that have those units, when the design has any.
"""
from __future__ import annotations

import json
import os
import re
import urllib.error
import urllib.parse
import urllib.request
from typing import Any

MOUSER_URL = "https://api.mouser.com/api/v1/search/keyword"
USER_AGENT = "albie-forge/1.0"
TIMEOUT_S = 20
MAX_LIMIT = 10
KEY_ENV = "MOUSER_API_KEY"
OPTIONS = [
    {"provider": "Mouser Search API", "env": "MOUSER_API_KEY", "cost": "free key for a registered Mouser account "
     "(request on mouser.com, API Hub); daily call limits apply", "supported": True},
    {"provider": "DigiKey Product Information API v4", "env": None, "cost": "free developer account; OAuth client "
     "id and secret (not wired here)", "supported": False},
    {"provider": "Nexar (Octopart) API", "env": None, "cost": "token from a Nexar account; small free evaluation "
     "quota, paid plans beyond it (not wired here)", "supported": False},
]


class PartsError(Exception):
    """Why the search failed, in words Albie can say."""


def configured() -> bool:
    return bool(os.environ.get(KEY_ENV, "").strip())


def not_set_up() -> dict[str, Any]:
    return {"status": "not_set_up", "cards": [], "sample": False,
            "error": "Parts search isn't set up on this server: it needs a distributor API key (MOUSER_API_KEY). "
                     "I won't make up parts, so I can't list any yet.",
            "options": OPTIONS}


# --- the distributor call ------------------------------------------------------------------------------------

def _post(url: str, body: dict[str, Any]) -> Any:
    request = urllib.request.Request(url, data=json.dumps(body).encode(), method="POST",
                                     headers={"User-Agent": USER_AGENT, "Content-Type": "application/json",
                                              "Accept": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=TIMEOUT_S) as answer:
            return json.loads(answer.read())
    except urllib.error.HTTPError as e:
        raise PartsError(f"Mouser said {e.code}") from None
    except (urllib.error.URLError, TimeoutError, ConnectionError, ValueError):
        raise PartsError("Mouser didn't answer") from None  # no exception text: the URL carries the key


def mouser_search(query: str, limit: int) -> list[dict[str, Any]]:
    key = os.environ.get(KEY_ENV, "").strip()
    url = f"{MOUSER_URL}?{urllib.parse.urlencode({'apiKey': key})}"
    data = _post(url, {"SearchByKeywordRequest": {"keyword": query, "records": limit, "startingRecord": 0}})
    errors = [e.get("Message") for e in data.get("Errors") or [] if isinstance(e, dict)]
    if errors:
        raise PartsError("Mouser refused the search: " + "; ".join(str(e) for e in errors[:2]))
    return list((data.get("SearchResults") or {}).get("Parts") or [])


# --- facts and checks ----------------------------------------------------------------------------------------

def _money(text: Any) -> float | None:
    m = re.search(r"(\d[\d,]*\.?\d*)", str(text or ""))
    return float(m.group(1).replace(",", "")) if m else None


def _volts(text: str) -> list[float]:
    return [float(v) for v in re.findall(r"(\d+(?:\.\d+)?)\s*V(?:DC|dc)?\b", text or "")]


def _grams(text: str) -> float | None:
    m = re.search(r"(\d+(?:\.\d+)?)\s*(g|grams|kg)\b", text or "", re.I)
    if not m:
        return None
    return float(m.group(1)) * (1000 if m.group(2).lower() == "kg" else 1)


def _check(name: str, level: str, text: str) -> dict[str, str]:
    return {"name": name, "level": level, "text": text}


def card(part: dict[str, Any], limits: dict[str, float]) -> dict[str, Any]:
    """One listing as a card: only what Mouser states, and checks against the limits."""
    attrs = {str(a.get("AttributeName")): str(a.get("AttributeValue"))
             for a in part.get("ProductAttributes") or [] if isinstance(a, dict)}
    description = str(part.get("Description") or "")
    stated = description + " " + " ".join(f"{k} {v}" for k, v in attrs.items())
    breaks = [b for b in part.get("PriceBreaks") or [] if isinstance(b, dict)]
    unit = _money(breaks[0].get("Price")) if breaks else None
    currency = breaks[0].get("Currency") if breaks else None
    volts = _volts(stated)
    grams = _grams(" ".join(v for k, v in attrs.items() if "weight" in k.lower() or "mass" in k.lower()))
    checks = []
    if "max_price_usd" in limits:
        if unit is None:
            checks.append(_check("price", "unknown", "No price listed"))
        elif currency not in (None, "USD"):
            checks.append(_check("price", "unknown", f"Priced in {currency}, not compared with a USD budget"))
        else:
            ok = unit <= limits["max_price_usd"]
            checks.append(_check("price", "good" if ok else "bad",
                                 f"${unit:,.2f} each {'within' if ok else 'over'} the ${limits['max_price_usd']:,.2f} budget"))
    if "voltage_v" in limits:
        want = limits["voltage_v"]
        if not volts:
            checks.append(_check("voltage", "unknown", "Supply voltage not stated in the listing"))
        elif any(abs(v - want) <= 0.1 * want for v in volts):
            checks.append(_check("voltage", "good", f"Lists {', '.join(f'{v:g} V' for v in volts)}; the design runs {want:g} V"))
        else:
            checks.append(_check("voltage", "warn", f"Lists {', '.join(f'{v:g} V' for v in volts)}, not {want:g} V: "
                                                    "check the datasheet"))
    if "max_weight_g" in limits:
        if grams is None:
            checks.append(_check("weight", "unknown", "Weight not stated in the listing"))
        else:
            ok = grams <= limits["max_weight_g"]
            checks.append(_check("weight", "good" if ok else "bad",
                                 f"{grams:g} g {'within' if ok else 'over'} the {limits['max_weight_g']:g} g budget"))
    stock = str(part.get("Availability") or "") or None
    return {"kind": "part", "id": part.get("MouserPartNumber"), "mpn": part.get("ManufacturerPartNumber"),
            "manufacturer": part.get("Manufacturer"), "description": description or None,
            "category": part.get("Category"), "url": part.get("ProductDetailUrl"),
            "datasheet": part.get("DataSheetUrl") or None, "availability": stock,
            "unit_price": unit, "currency": currency,
            "price_breaks": [{"qty": b.get("Quantity"), "price": _money(b.get("Price"))} for b in breaks[:4]],
            "voltages_stated": volts, "weight_g": grams, "attributes": dict(list(attrs.items())[:8]),
            "checks": checks, "source": "Mouser Search API"}


def design_limits(cad: Any) -> tuple[dict[str, float], dict[str, Any]]:
    """Limits read from the open CAD design: a parameter in volts is its supply voltage, one in grams named like a
    budget or payload is the weight budget. (limits, the facts used) - both empty when the design states none."""
    try:
        record = cad.current
    except Exception:  # noqa: BLE001 - no design open, or no CAD here (the cloud's parts route)
        return {}, {}
    units = {str(p.get("name")): str(p.get("unit") or "") for p in record.param_schema or [] if isinstance(p, dict)}
    limits: dict[str, float] = {}
    used: dict[str, Any] = {"design": record.part}
    for name, value in (record.params or {}).items():
        unit = units.get(name, "")
        if not isinstance(value, (int, float)):
            continue
        if unit == "V" and "voltage_v" not in limits:
            limits["voltage_v"] = float(value)
            used[name] = f"{value:g} V"
        elif unit == "g" and re.search(r"payload|budget|max_mass|max_weight", name) and "max_weight_g" not in limits:
            limits["max_weight_g"] = float(value)
            used[name] = f"{value:g} g"
    return limits, (used if len(used) > 1 else {})


def search(query: str, limit: int = 6, limits: dict[str, float] | None = None, cad: Any = None) -> dict[str, Any]:
    """Real listings for what the user said, each checked; {"status": "not_set_up"} without a server key."""
    query = (query or "").strip()
    if not query:
        raise PartsError("say what part to look for, e.g. 'camera module 5MP' or '2205 brushless motor'")
    if not configured():
        return {"query": query, **not_set_up()}
    from_design, facts = design_limits(cad) if cad is not None else ({}, {})
    merged = {**from_design, **{k: float(v) for k, v in (limits or {}).items() if v is not None}}
    parts = mouser_search(query, max(1, min(MAX_LIMIT, int(limit or 6))))
    cards = [card(p, merged) for p in parts]
    return {"query": query, "status": "ok", "cards": cards, "limits": merged, "design_facts": facts,
            "sample": False, "source": "Mouser Search API",
            **({} if cards else {"error": f"Mouser lists nothing for {query!r}"})}
