"""Research module: real parts from a distributor API (parts.py), checked against the design.

POST /research/parts        {query, max_price_usd?, voltage_v?, max_weight_g?, limit?} -> cards, or not_set_up
GET  /research/parts/status  whether the server has a distributor key, and the options

Self-contained: it reads app.state.cad only when it is there, so the cloud can mount the same router
(`app.include_router(research.router)`) and route `research.tools.TOOLS` by name. Needs MOUSER_API_KEY on the
server; without it every call says "not set up" and lists no parts. Model and dataset research is in ml/research.py.
"""
from .router import router

__all__ = ["router"]
