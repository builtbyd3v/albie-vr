"""Small local app for testing agent orchestration without the CAD and messaging services.

Run from hub/: uvicorn agent.standalone:app --host 127.0.0.1 --port 8000
"""
from fastapi import FastAPI, WebSocket

from .app import health_fields, load_env
from .router import router

load_env()
app = FastAPI(title="Albie agent workflows")
app.include_router(router)


@app.websocket("/ws")
async def socket(websocket: WebSocket):
    from .ws import handle_socket
    await handle_socket(websocket)


@app.get("/health")
async def health() -> dict:
    return {"ok": True, **health_fields()}
