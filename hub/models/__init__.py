"""Models hub module: open-licensed 3D models for artifacts (service.py says where they come from and what is refused).

GET /models/search?q=&limit=   text search: id, name, author, licence, source, size, face count, thumbnail
GET /models/fetch?id=          download, check and cache one model server-side; answers its facts, sha256 and credit line
GET /models/file?id=           the .glb bytes (model/gltf-binary), with the same facts in X-Model-* headers

Self-contained: nothing on app.state, so the cloud mounts the same router (`app.include_router(models.router)`).
Albie's tools on the same functions are in models/tools.py.
"""
from .router import router

__all__ = ["router"]
