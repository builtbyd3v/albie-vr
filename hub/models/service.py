"""Open-licensed 3D models for artifacts: search by text, fetch one as a single .glb.

Where they come from (checked with real requests, November 2026):
  * Search and per-model facts: Sketchfab's public Data API (`/v3/search`, `/v3/models/{uid}`). No token needed.
    Each model has a licence, author, face count and page URL.
  * The bytes: Objaverse on the Hugging Face Hub (`allenai/objaverse`), a public copy of ~800k CC-licensed Sketchfab
    models as `glbs/000-NNN/<sketchfab uid>.glb`. No token needed. Which folder a uid is in is asked from the Hub's
    `paths-info` API (160 candidate paths per uid), so no index is downloaded.
  * Optional: with SKETCHFAB_API_TOKEN set, models that aren't in Objaverse come from Sketchfab's own download API
    (it answers 401 without a token). That path is written from Sketchfab's documentation and has not been run live.

What this module refuses: any licence but CC0, CC BY and CC BY-SA (so no NC, no ND, no "Standard"); a file over
FORGE_MODELS_MAX_BYTES; bytes that aren't a glTF 2 binary; a model too heavy for a Quest (triangles, texture size,
compression the headset can't decode); any URL or redirect off the host allow-list (it is not a proxy: callers give an
id, never a URL). Fetched files are cached by id under FORGE_MODELS_DIR.

Env (all optional, read on each call): FORGE_MODELS_DIR (default $FORGE_DATA_DIR/models, else hub/.data/models),
FORGE_MODELS_MAX_BYTES (8 MiB, the headset's own limit for an artifact asset), FORGE_MODELS_MAX_TRIANGLES (200000),
FORGE_MODELS_MAX_TEXTURE (2048), FORGE_MODELS_TIMEOUT_S (20, per upstream request), SKETCHFAB_API_TOKEN.
"""
from __future__ import annotations

import asyncio
import hashlib
import json
import logging
import os
import re
import ssl
from pathlib import Path
from typing import Any
from urllib.parse import urlencode, urljoin, urlsplit

import aiohttp
import certifi
from pydantic import BaseModel

from . import glb

log = logging.getLogger("models")

SKETCHFAB_API = "https://api.sketchfab.com/v3"
HF_API = "https://huggingface.co/api/datasets/allenai/objaverse"
HF_FILES = "https://huggingface.co/datasets/allenai/objaverse/resolve/main/"
OBJAVERSE_FOLDERS = 160  # glbs/000-000 .. glbs/000-159
PATHS_PER_CALL = 6 * OBJAVERSE_FOLDERS  # paths-info refuses a few thousand paths at once; 960 is accepted
CANDIDATES = 24  # search hits asked from Sketchfab per licence, before the ones with no downloadable copy are dropped
MAX_CHECKED = 36  # how many of them are looked up in Objaverse (6 paths-info calls)
MAX_REDIRECTS = 5
USER_AGENT = "albie-models/1"

# Hosts this module talks to. A download may be redirected, and every hop is checked against these.
API_HOSTS = frozenset({"api.sketchfab.com", "huggingface.co"})
DOWNLOAD_HOSTS = frozenset({"huggingface.co", "sketchfab-prod-media.s3.amazonaws.com"})
DOWNLOAD_SUFFIXES = (".hf.co", ".sketchfab.com")

# Sketchfab licence slug -> (SPDX id, URL). Anything not here is refused.
LICENCES: dict[str, tuple[str, str]] = {
    "cc0": ("CC0-1.0", "https://creativecommons.org/publicdomain/zero/1.0/"),
    "by": ("CC-BY-4.0", "https://creativecommons.org/licenses/by/4.0/"),
    "by-sa": ("CC-BY-SA-4.0", "https://creativecommons.org/licenses/by-sa/4.0/"),
}
# Search results name the licence by label only.
_LABELS = {"CC0 Public Domain": "cc0", "CC Attribution": "by", "CC Attribution-ShareAlike": "by-sa"}
_SPDX = {spdx for spdx, _ in LICENCES.values()}

_ID = re.compile(r"^(objaverse|sketchfab):([0-9a-f]{32})$")
_SSL = ssl.create_default_context(cafile=certifi.where())
_locks: dict[str, asyncio.Lock] = {}


class ModelError(Exception):
    """A refusal with an HTTP status and a short code the headset and Albie can act on."""

    def __init__(self, status: int, code: str, message: str) -> None:
        super().__init__(f"{code}: {message}")
        self.status, self.code, self.message = status, code, message


class ModelHit(BaseModel):
    id: str  # "objaverse:<uid>" or "sketchfab:<uid>": what /models/fetch takes
    name: str
    author: str
    author_url: str = ""
    licence: str  # SPDX id: CC0-1.0, CC-BY-4.0 or CC-BY-SA-4.0
    licence_url: str
    source: str  # where the bytes come from: "objaverse" or "sketchfab"
    source_url: str  # the model's own page, for the credit line
    size_bytes: int | None = None
    triangles: int | None = None  # a search hit reports the catalogue's face count; a fetched file its real triangles
    vertices: int | None = None
    thumbnail_url: str | None = None


class ModelSearch(BaseModel):
    query: str
    hits: list[ModelHit] = []
    note: str = ""  # why there are few or no hits, in words


class ModelFile(ModelHit):
    sha256: str
    max_texture_px: int = 0
    attribution: str  # the line to show with the model
    file: str  # GET this path for the bytes


class Limits(BaseModel):
    max_bytes: int
    max_triangles: int
    max_texture: int
    timeout_s: float


def limits() -> Limits:
    def number(name: str, default: float) -> float:
        try:
            value = float(os.environ.get(name) or default)
        except ValueError:
            return default
        return value if value > 0 else default

    return Limits(max_bytes=int(number("FORGE_MODELS_MAX_BYTES", 8 * 1024 * 1024)),
                  max_triangles=int(number("FORGE_MODELS_MAX_TRIANGLES", 200_000)),
                  max_texture=int(number("FORGE_MODELS_MAX_TEXTURE", 2048)),
                  timeout_s=number("FORGE_MODELS_TIMEOUT_S", 20))


def cache_dir() -> Path:
    explicit = os.environ.get("FORGE_MODELS_DIR")
    if explicit:
        return Path(explicit)
    data = os.environ.get("FORGE_DATA_DIR")
    return (Path(data) if data else Path(__file__).resolve().parent.parent / ".data") / "models"


def _token() -> str:
    return os.environ.get("SKETCHFAB_API_TOKEN", "").strip()


def parse_id(model_id: str) -> tuple[str, str]:
    """(source, uid). The id is the only thing a caller chooses, and it can only name a model on a known source."""
    match = _ID.match(model_id or "")
    if not match:
        raise ModelError(400, "bad_id", "a model id looks like objaverse:<32 hex digits>, as /models/search returns it")
    return match.group(1), match.group(2)


def check_url(url: str, hosts: frozenset[str], suffixes: tuple[str, ...] = ()) -> str:
    """The URL, if it is https to an allowed host on the default port with no credentials. Else a ModelError."""
    parts = urlsplit(url)
    host = (parts.hostname or "").lower()
    allowed = host in hosts or any(host.endswith(s) for s in suffixes)
    if parts.scheme != "https" or not allowed or parts.port not in (None, 443) or parts.username or parts.password:
        raise ModelError(502, "blocked_host", f"refused to contact {host or 'that address'}: it isn't a model source")
    return url


def attribution(name: str, author: str, licence: str, source_url: str) -> str:
    return f"\"{name}\" by {author}, {licence}. {source_url}"


# ---- network (the tests replace these three) ---------------------------------------------------------------

def _session(timeout_s: float) -> aiohttp.ClientSession:
    # trust_env stays off: no proxy or .netrc from the environment decides where requests go.
    return aiohttp.ClientSession(headers={"User-Agent": USER_AGENT}, timeout=aiohttp.ClientTimeout(total=timeout_s),
                                 connector=aiohttp.TCPConnector(ssl=_SSL))


async def _json(session: aiohttp.ClientSession, method: str, url: str, *, body: Any = None,
                headers: dict[str, str] | None = None) -> Any:
    """One JSON API call to an allowed host. Redirects are not followed. 404 is ModelError 404."""
    check_url(url, API_HOSTS)
    host = urlsplit(url).hostname
    try:
        async with session.request(method, url, json=body, headers=headers, allow_redirects=False) as r:
            if r.status == 404:
                raise ModelError(404, "not_found", "there's no such model")
            if r.status in (401, 403):
                raise ModelError(502, "source_refused", f"{host} refused the request ({r.status})")
            if r.status == 429:
                raise ModelError(503, "rate_limited", f"{host} is rate limiting; try again in a minute")
            if r.status >= 300:
                raise ModelError(502, "source_error", f"{host} answered {r.status}")
            return await r.json(content_type=None)
    except (aiohttp.ClientError, asyncio.TimeoutError, ValueError):
        # No exception text: an aiohttp error can carry request headers (a token).
        raise ModelError(502, "source_unreachable", f"couldn't reach {host}") from None


async def _download(session: aiohttp.ClientSession, url: str, max_bytes: int,
                    headers: dict[str, str] | None = None) -> bytes:
    """The bytes at `url`, following redirects by hand so every hop is on the allow-list, and stopping at the cap."""
    for _ in range(MAX_REDIRECTS + 1):
        check_url(url, DOWNLOAD_HOSTS, DOWNLOAD_SUFFIXES)
        host = urlsplit(url).hostname
        try:
            async with session.get(url, allow_redirects=False, headers=headers) as r:
                if r.status in (301, 302, 303, 307, 308):
                    url = urljoin(url, r.headers.get("Location", ""))
                    headers = None  # nothing we sent the first host goes to the next
                    continue
                if r.status == 404:
                    raise ModelError(404, "not_found", "the model's file is gone")
                if r.status != 200:
                    raise ModelError(502, "source_error", f"{host} answered {r.status}")
                declared = r.headers.get("Content-Length")
                if declared and declared.isdigit() and int(declared) > max_bytes:
                    raise _too_big(int(declared), max_bytes)
                chunks, total = [], 0
                async for chunk in r.content.iter_chunked(64 * 1024):
                    total += len(chunk)
                    if total > max_bytes:
                        raise _too_big(total, max_bytes)
                    chunks.append(chunk)
                return b"".join(chunks)
        except (aiohttp.ClientError, asyncio.TimeoutError):
            raise ModelError(502, "source_unreachable", f"couldn't download from {host}") from None
    raise ModelError(502, "source_error", "too many redirects")


def _too_big(size: int, max_bytes: int) -> ModelError:
    return ModelError(413, "too_big", f"that model is over {size / 1048576:.1f} MB; the limit is {max_bytes / 1048576:.0f} MB")


# ---- sources -----------------------------------------------------------------------------------------------

def _licence(model: dict[str, Any]) -> tuple[str, str] | None:
    """(SPDX, URL) for a Sketchfab model's licence, or None if it isn't one we take (or there is none)."""
    licence = model.get("license")
    if not isinstance(licence, dict):
        return None
    slug = licence.get("slug") or _LABELS.get(str(licence.get("label") or ""))
    return LICENCES.get(str(slug))


def _thumbnail(model: dict[str, Any]) -> str | None:
    images = ((model.get("thumbnails") or {}).get("images")) or []
    usable = [i for i in images if isinstance(i, dict) and str(i.get("url") or "").startswith("https://")]
    if not usable:
        return None
    # The smallest one that is still at least 200 px wide; else the widest there is.
    wide = sorted((i for i in usable if int(i.get("width") or 0) >= 200), key=lambda i: int(i.get("width") or 0))
    return (wide[0] if wide else max(usable, key=lambda i: int(i.get("width") or 0)))["url"]


def _hit(model: dict[str, Any], source: str, size: int | None) -> ModelHit | None:
    licence = _licence(model)
    uid = str(model.get("uid") or "")
    if licence is None or not re.fullmatch(r"[0-9a-f]{32}", uid):
        return None
    user = model.get("user") or {}
    author = str(user.get("displayName") or user.get("username") or "").strip()
    name = str(model.get("name") or "").strip()
    if not author or not name:
        return None  # a CC BY model with nobody to credit can't be shown properly
    username = str(user.get("username") or "")
    return ModelHit(
        id=f"{source}:{uid}", name=name[:120], author=author[:80],
        author_url=str(user.get("profileUrl") or (f"https://sketchfab.com/{username}" if username else "")),
        licence=licence[0], licence_url=licence[1], source=source,
        source_url=str(model.get("viewerUrl") or f"https://sketchfab.com/3d-models/{uid}"),
        size_bytes=size, triangles=_int(model.get("faceCount")), vertices=_int(model.get("vertexCount")),
        thumbnail_url=_thumbnail(model))


def _int(value: Any) -> int | None:
    return int(value) if isinstance(value, (int, float)) and not isinstance(value, bool) else None


async def _objaverse_files(session: aiohttp.ClientSession, uids: list[str]) -> dict[str, tuple[str, int]]:
    """uid -> (path in the dataset, size) for the uids Objaverse has. One paths-info call per 6 uids, in parallel."""
    async def ask(group: list[str]) -> list[Any]:
        paths = [f"glbs/000-{n:03d}/{uid}.glb" for uid in group for n in range(OBJAVERSE_FOLDERS)]
        found = await _json(session, "POST", f"{HF_API}/paths-info/main", body={"paths": paths, "expand": False})
        return found if isinstance(found, list) else []

    per = PATHS_PER_CALL // OBJAVERSE_FOLDERS
    groups = [uids[i:i + per] for i in range(0, len(uids), per)]
    files: dict[str, tuple[str, int]] = {}
    for found in await asyncio.gather(*(ask(g) for g in groups)):
        for entry in found:
            path = str(entry.get("path") or "") if isinstance(entry, dict) else ""
            match = re.fullmatch(r"glbs/000-\d{3}/([0-9a-f]{32})\.glb", path)
            if match and entry.get("type") == "file":
                files[match.group(1)] = (path, int(entry.get("size") or 0))
    return files


async def search(query: str, limit: int = 5) -> ModelSearch:
    """Models matching `query` that can be fetched right now, within the size and triangle limits."""
    query = " ".join((query or "").split())[:100]
    if not query:
        raise ModelError(400, "bad_query", "say what to look for")
    limit = max(1, min(int(limit), 20))
    caps = limits()
    base = [("type", "models"), ("q", query), ("downloadable", "true"), ("count", str(CANDIDATES)),
            ("max_face_count", str(caps.max_triangles))]
    async with _session(caps.timeout_s) as session:
        # Sketchfab's search takes one licence at a time (a repeated `license` keeps only the last), so one search
        # per licence, then the lists are interleaved: each is in the catalogue's own relevance order.
        found = await asyncio.gather(*(
            _json(session, "GET", f"{SKETCHFAB_API}/search?{urlencode(base + [('license', slug)])}") for slug in LICENCES))
        lists = [[m for m in (f.get("results") if isinstance(f, dict) else None) or [] if isinstance(m, dict)] for f in found]
        models, seen = [], set()
        for rank in range(CANDIDATES):
            for results in lists:
                uid = str(results[rank].get("uid") or "") if rank < len(results) else ""
                if re.fullmatch(r"[0-9a-f]{32}", uid) and uid not in seen and len(models) < MAX_CHECKED:
                    seen.add(uid)
                    models.append(results[rank])
        files = await _objaverse_files(session, [str(m["uid"]) for m in models]) if models else {}
    direct = bool(_token())
    hits: list[ModelHit] = []
    heavy = 0
    for model in models:
        uid = str(model.get("uid") or "")
        if uid in files:
            hit = _hit(model, "objaverse", files[uid][1])
        elif direct:
            archive = (model.get("archives") or {}).get("glb") or {}
            hit = _hit(model, "sketchfab", _int(archive.get("size")))
        else:
            continue
        if hit is None:
            continue
        texture = _int(((model.get("archives") or {}).get("glb") or {}).get("textureMaxResolution")) or 0
        if (hit.size_bytes or 0) > caps.max_bytes or (hit.source == "sketchfab" and texture > caps.max_texture):
            heavy += 1
            continue
        hits.append(hit)
        if len(hits) == limit:
            break
    note = ""
    if not hits:
        note = ("Nothing open-licensed and light enough for the headset matched." if models or heavy
                else "Nothing open-licensed matched that.")
    return ModelSearch(query=query, hits=hits, note=note)


def _paths(model_id: str) -> tuple[Path, Path]:
    stem = model_id.replace(":", "-")
    return cache_dir() / f"{stem}.glb", cache_dir() / f"{stem}.json"


def _cached(model_id: str) -> tuple[ModelFile, bytes] | None:
    glb_path, meta_path = _paths(model_id)
    try:
        meta = ModelFile.model_validate_json(meta_path.read_text(encoding="utf-8"))
        data = glb_path.read_bytes()
    except (OSError, ValueError):
        return None
    # A cached file is checked like a new one: right id, an allowed licence, the bytes it was saved with.
    if meta.id != model_id or meta.licence not in _SPDX or hashlib.sha256(data).hexdigest() != meta.sha256:
        return None
    return meta, data


def _store(meta: ModelFile, data: bytes) -> None:
    glb_path, meta_path = _paths(meta.id)
    glb_path.parent.mkdir(parents=True, exist_ok=True)
    for path, payload in ((glb_path, data), (meta_path, meta.model_dump_json(indent=2).encode())):
        temp = path.with_suffix(path.suffix + f".{os.getpid()}.tmp")
        temp.write_bytes(payload)
        os.replace(temp, path)


async def fetch(model_id: str) -> tuple[ModelFile, bytes]:
    """The model's facts and its bytes, from the cache or downloaded, checked every time. Raises ModelError."""
    source, uid = parse_id(model_id)
    lock = _locks.setdefault(model_id, asyncio.Lock())
    async with lock:  # two asks for the same model download it once
        cached = _cached(model_id)
        if cached is not None:
            return cached
        caps = limits()
        async with _session(caps.timeout_s) as session:
            model = await _json(session, "GET", f"{SKETCHFAB_API}/models/{uid}")
            if not isinstance(model, dict) or str(model.get("uid")) != uid:
                raise ModelError(502, "source_error", "the catalogue's answer wasn't that model")
            hit = _hit(model, source, None)
            if hit is None:
                label = (model.get("license") or {}).get("label") if isinstance(model.get("license"), dict) else None
                raise ModelError(403, "licence_refused",
                                 f"that model's licence ({label or 'none given'}) isn't CC0, CC BY or CC BY-SA, so it can't be used")
            if source == "objaverse":
                files = await _objaverse_files(session, [uid])
                if uid not in files:
                    raise ModelError(404, "not_found", "that model has no open copy to download")
                path, size = files[uid]
                if size > caps.max_bytes:
                    raise _too_big(size, caps.max_bytes)
                url, headers = HF_FILES + path, None
            else:
                token = _token()
                if not token:
                    raise ModelError(503, "needs_token", "downloading straight from Sketchfab needs SKETCHFAB_API_TOKEN on the server")
                if model.get("isDownloadable") is not True:
                    raise ModelError(403, "licence_refused", "its author doesn't allow downloads")
                archives = await _json(session, "GET", f"{SKETCHFAB_API}/models/{uid}/download",
                                       headers={"Authorization": f"Token {token}"})
                archive = archives.get("glb") if isinstance(archives, dict) else None
                if not isinstance(archive, dict) or not archive.get("url"):
                    raise ModelError(404, "not_found", "Sketchfab has no .glb for that model")
                if int(archive.get("size") or 0) > caps.max_bytes:
                    raise _too_big(int(archive["size"]), caps.max_bytes)
                url, headers = str(archive["url"]), None
            # A download gets longer than an API call, but still ends.
            data = await asyncio.wait_for(_download(session, url, caps.max_bytes, headers), timeout=caps.timeout_s * 3)
        meta = check(hit, data, caps)
        _store(meta, data)
        log.info("fetched %s (%d bytes, %d triangles, %s)", model_id, len(data), meta.triangles or 0, meta.licence)
        return meta, data


def check(hit: ModelHit, data: bytes, caps: Limits) -> ModelFile:
    """The downloaded bytes against every limit. Returns what to tell the headset; raises ModelError."""
    if len(data) > caps.max_bytes:
        raise _too_big(len(data), caps.max_bytes)
    try:
        stats = glb.inspect(data)
    except glb.GlbError as e:
        raise ModelError(422, "not_glb", str(e)) from None
    # Sketchfab's own exports say inside the file what licence that copy was published under. It has to be an open one too.
    embedded = stats.extras.get("license", "").split(" ")[0]
    if embedded and embedded not in _SPDX:
        raise ModelError(403, "licence_refused", f"this copy of the model was published as {embedded}, which isn't an open licence we take")
    if stats.unsupported:
        raise ModelError(422, "cant_decode", "the headset can't decode " + ", ".join(stats.unsupported))
    if stats.triangles > caps.max_triangles:
        raise ModelError(422, "too_heavy", f"{stats.triangles} triangles; the headset's limit is {caps.max_triangles}")
    if stats.triangles == 0:
        raise ModelError(422, "not_glb", "it has no triangles to show")
    if stats.max_texture_px > caps.max_texture:
        raise ModelError(422, "too_heavy", f"a {stats.max_texture_px} px texture; the headset's limit is {caps.max_texture} px")
    return ModelFile(
        **hit.model_dump(exclude={"size_bytes", "triangles", "vertices"}),
        size_bytes=len(data), triangles=stats.triangles, vertices=stats.vertices,
        sha256=hashlib.sha256(data).hexdigest(), max_texture_px=stats.max_texture_px,
        attribution=attribution(hit.name, hit.author, hit.licence, hit.source_url),
        file=f"/models/file?{urlencode({'id': hit.id})}")
