"""Models module: search, fetch, the licence allow-list, size and host limits, the cache (no network: sources stubbed)."""
from __future__ import annotations

import asyncio
import hashlib
import json
import struct
from typing import Any

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

import models
from models import glb, service, tools

UID = "0123456789abcdef0123456789abcdef"
OTHER = "fedcba9876543210fedcba9876543210"
NC = "11111111111111112222222222222222"
BY = {"label": "CC Attribution", "slug": "by", "url": "http://creativecommons.org/licenses/by/4.0/"}
BY_NC = {"label": "CC Attribution-NonCommercial", "slug": "by-nc"}


def make_glb(triangles: int = 2, *, extras: dict[str, str] | None = None, required: list[str] | None = None,
             png: tuple[int, int] | None = None, magic: bytes = b"glTF") -> bytes:
    """A small, well-formed .glb whose JSON says it has `triangles` triangles (the buffer itself is filler)."""
    binary = b"\0" * 8
    doc: dict[str, Any] = {
        "asset": {"version": "2.0", **({"extras": extras} if extras else {})},
        "accessors": [{"count": triangles * 3}, {"count": triangles * 2}],
        "meshes": [{"primitives": [{"attributes": {"POSITION": 1}, "indices": 0}]}],
        "nodes": [{"mesh": 0}],
        "buffers": [{"byteLength": 8}],
    }
    if required:
        doc["extensionsRequired"] = required
    if png:
        image = b"\x89PNG\r\n\x1a\n" + struct.pack(">I4sII", 13, b"IHDR", *png) + b"\0" * 8
        doc["bufferViews"] = [{"buffer": 0, "byteOffset": len(binary), "byteLength": len(image)}]
        doc["images"] = [{"bufferView": 0, "mimeType": "image/png"}]
        binary += image + b"\0" * (-len(image) % 4)
        doc["buffers"][0]["byteLength"] = len(binary)
    text = json.dumps(doc).encode()
    text += b" " * (-len(text) % 4)
    body = struct.pack("<II", len(text), 0x4E4F534A) + text + struct.pack("<II", len(binary), 0x004E4942) + binary
    return magic + struct.pack("<II", 2, 12 + len(body)) + body


def sketchfab(uid: str, name: str = "Jet engine", licence: dict[str, Any] | None = BY, faces: int = 1200) -> dict[str, Any]:
    return {
        "uid": uid, "name": name, "license": licence, "isDownloadable": True, "faceCount": faces, "vertexCount": 700,
        "viewerUrl": f"https://sketchfab.com/3d-models/jet-{uid}",
        "user": {"displayName": "Ada", "username": "ada", "profileUrl": "https://sketchfab.com/ada"},
        "thumbnails": {"images": [{"url": "https://media.sketchfab.com/a.jpeg", "width": 1920},
                                  {"url": "https://media.sketchfab.com/b.jpeg", "width": 256}]},
        "archives": {"glb": {"size": 5000, "textureMaxResolution": 1024}},
    }


class FakeSession:
    def __init__(self, *args: object, **kwargs: object) -> None:
        pass

    async def __aenter__(self) -> "FakeSession":
        return self

    async def __aexit__(self, *exc: object) -> None:
        return None


class World:
    """What the stubbed sources hold, and what was asked of them."""

    def __init__(self) -> None:
        self.catalogue: dict[str, dict[str, Any]] = {UID: sketchfab(UID), NC: sketchfab(NC, "Paid chair", BY_NC)}
        self.search: dict[str, list[str]] = {"by": [UID, OTHER], "cc0": [], "by-sa": []}
        self.objaverse: dict[str, int] = {}  # uid -> size
        self.files: dict[str, bytes] = {}  # url -> bytes
        self.asked: list[str] = []
        self.downloads: list[str] = []
        self.auth: list[dict[str, str] | None] = []

    def put(self, uid: str, data: bytes) -> None:
        self.objaverse[uid] = len(data)
        self.files[f"{service.HF_FILES}glbs/000-007/{uid}.glb"] = data

    async def json(self, session: object, method: str, url: str, *, body: Any = None, headers: dict[str, str] | None = None) -> Any:
        service.check_url(url, service.API_HOSTS)
        self.asked.append(f"{method} {url}")
        self.auth.append(headers)
        if url.startswith(service.SKETCHFAB_API + "/search?"):
            slug = url.rsplit("license=", 1)[1]
            return {"results": [self.catalogue.get(uid) or sketchfab(uid, "Other") for uid in self.search.get(slug, [])]}
        if url.endswith("/download"):
            return {"glb": {"url": "https://sketchfab-prod-media.s3.amazonaws.com/archives/x.glb", "size": 100}}
        if url.startswith(service.SKETCHFAB_API + "/models/"):
            uid = url.rsplit("/", 1)[1]
            if uid not in self.catalogue:
                raise service.ModelError(404, "not_found", "there's no such model")
            return self.catalogue[uid]
        if url == service.HF_API + "/paths-info/main":
            assert len(body["paths"]) <= service.PATHS_PER_CALL
            return [{"type": "file", "path": p, "size": self.objaverse[p[-36:-4]]} for p in body["paths"]
                    if "/000-007/" in p and p[-36:-4] in self.objaverse]
        raise AssertionError(f"unexpected request {url}")

    async def download(self, session: object, url: str, max_bytes: int, headers: dict[str, str] | None = None) -> bytes:
        service.check_url(url, service.DOWNLOAD_HOSTS, service.DOWNLOAD_SUFFIXES)
        self.downloads.append(url)
        data = self.files[url]
        if len(data) > max_bytes:
            raise service._too_big(len(data), max_bytes)
        return data


@pytest.fixture
def world(monkeypatch: pytest.MonkeyPatch, tmp_path) -> World:
    w = World()
    monkeypatch.setattr(service, "_json", w.json)
    monkeypatch.setattr(service, "_download", w.download)
    monkeypatch.setattr(service, "_session", FakeSession)
    monkeypatch.setenv("FORGE_MODELS_DIR", str(tmp_path / "models"))
    for name in ("SKETCHFAB_API_TOKEN", "FORGE_MODELS_MAX_BYTES", "FORGE_MODELS_MAX_TRIANGLES", "FORGE_MODELS_MAX_TEXTURE"):
        monkeypatch.delenv(name, raising=False)
    service._locks.clear()
    return w


@pytest.fixture
def client(world: World) -> TestClient:
    app = FastAPI()
    app.include_router(models.router)
    return TestClient(app)


# ---- glb ---------------------------------------------------------------------------------------------------

def test_inspect_counts_triangles_textures_and_extras() -> None:
    stats = glb.inspect(make_glb(40, extras={"license": "CC-BY-4.0 (http://x)", "author": "Ada"}, png=(4096, 512)))
    assert (stats.triangles, stats.vertices, stats.meshes, stats.max_texture_px) == (40, 80, 1, 4096)
    assert stats.extras["author"] == "Ada" and stats.unsupported == []


def test_inspect_refuses_what_is_not_a_glb() -> None:
    good = make_glb()
    for bad in (b"<html>not a model</html>", make_glb(magic=b"GLTF"), good[:-4], good[:12], b""):
        with pytest.raises(glb.GlbError):
            glb.inspect(bad)


def test_inspect_names_compression_the_headset_cannot_decode() -> None:
    assert glb.inspect(make_glb(required=["KHR_draco_mesh_compression", "KHR_materials_unlit"])).unsupported == ["KHR_draco_mesh_compression"]


def test_jpeg_size() -> None:
    jpeg = b"\xff\xd8\xff\xe0" + struct.pack(">H", 4) + b"\0\0" + b"\xff\xc0" + struct.pack(">HBHH", 11, 8, 300, 200) + b"\0" * 8
    assert glb.image_size(jpeg) == (200, 300)
    assert glb.image_size(b"not an image") is None


# ---- hosts -------------------------------------------------------------------------------------------------

@pytest.mark.parametrize("url", [
    "http://huggingface.co/x", "https://example.com/x.glb", "https://169.254.169.254/latest/meta-data",
    "https://localhost/x", "https://huggingface.co.evil.test/x", "https://user:pw@huggingface.co/x",
    "https://huggingface.co:8443/x", "file:///etc/passwd", "https://evilhf.co/x", ""])
def test_urls_off_the_allow_list_are_refused(url: str) -> None:
    with pytest.raises(service.ModelError) as e:
        service.check_url(url, service.DOWNLOAD_HOSTS, service.DOWNLOAD_SUFFIXES)
    assert e.value.code == "blocked_host"


def test_download_hosts_allowed() -> None:
    for url in ("https://huggingface.co/datasets/allenai/objaverse/resolve/main/glbs/000-001/a.glb",
                "https://us.aws.cdn.hf.co/xet-bridge-us/abc?sig=1"):
        assert service.check_url(url, service.DOWNLOAD_HOSTS, service.DOWNLOAD_SUFFIXES) == url


class Reply:
    def __init__(self, status: int, headers: dict[str, str] | None = None, body: bytes = b"") -> None:
        self.status, self.headers, self.body = status, headers or {}, body
        self.content = self

    async def iter_chunked(self, size: int):
        for i in range(0, len(self.body), size):
            yield self.body[i:i + size]

    async def __aenter__(self) -> "Reply":
        return self

    async def __aexit__(self, *exc: object) -> None:
        return None


class Hops:
    def __init__(self, replies: dict[str, Reply]) -> None:
        self.replies, self.seen = replies, []

    def get(self, url: str, **kwargs: Any) -> Reply:
        self.seen.append((url, kwargs.get("headers")))
        assert kwargs["allow_redirects"] is False
        return self.replies[url]


def test_download_follows_allowed_redirects_and_drops_headers() -> None:
    start, cdn = "https://huggingface.co/a.glb", "https://us.aws.cdn.hf.co/blob?x=1"
    hops = Hops({start: Reply(302, {"Location": cdn}), cdn: Reply(200, {"Content-Length": "5"}, b"hello")})
    assert asyncio.run(service._download(hops, start, 100, {"Authorization": "Token t"})) == b"hello"
    assert hops.seen == [(start, {"Authorization": "Token t"}), (cdn, None)]


def test_download_refuses_a_redirect_off_the_allow_list() -> None:
    start = "https://huggingface.co/a.glb"
    hops = Hops({start: Reply(302, {"Location": "http://169.254.169.254/latest/meta-data"})})
    with pytest.raises(service.ModelError) as e:
        asyncio.run(service._download(hops, start, 100))
    assert e.value.code == "blocked_host" and len(hops.seen) == 1


def test_download_stops_at_the_cap_whatever_the_length_header_says() -> None:
    start = "https://huggingface.co/a.glb"
    for reply in (Reply(200, {"Content-Length": "999"}, b"x"), Reply(200, {}, b"x" * 200_000)):
        with pytest.raises(service.ModelError) as e:
            asyncio.run(service._download(Hops({start: reply}), start, 100_000 if not reply.headers else 100))
        assert e.value.code == "too_big" and e.value.status == 413


def test_download_gives_up_on_a_redirect_loop() -> None:
    start = "https://huggingface.co/a.glb"
    with pytest.raises(service.ModelError) as e:
        asyncio.run(service._download(Hops({start: Reply(302, {"Location": start})}), start, 100))
    assert "redirects" in e.value.message


# ---- search ------------------------------------------------------------------------------------------------

def test_search_lists_only_models_with_an_open_copy(client: TestClient, world: World) -> None:
    world.put(UID, make_glb())
    body = client.get("/models/search", params={"q": "jet  engine"}).json()
    assert body["query"] == "jet engine"
    assert [h["id"] for h in body["hits"]] == [f"objaverse:{UID}"]  # OTHER isn't in Objaverse, and there's no token
    hit = body["hits"][0]
    assert hit["name"] == "Jet engine" and hit["author"] == "Ada" and hit["source"] == "objaverse"
    assert hit["licence"] == "CC-BY-4.0" and hit["licence_url"] == "https://creativecommons.org/licenses/by/4.0/"
    assert hit["source_url"].endswith(UID) and hit["size_bytes"] == world.objaverse[UID] and hit["triangles"] == 1200
    assert hit["thumbnail_url"] == "https://media.sketchfab.com/b.jpeg"
    searches = [a for a in world.asked if "/search?" in a]
    assert len(searches) == 3 and all("downloadable=true" in a and "max_face_count=200000" in a for a in searches)


def test_search_drops_other_licences_even_if_the_source_returns_them(client: TestClient, world: World) -> None:
    world.search["by"] = [NC, UID]
    world.put(NC, make_glb())
    world.put(UID, make_glb())
    assert [h["id"] for h in client.get("/models/search", params={"q": "chair"}).json()["hits"]] == [f"objaverse:{UID}"]


def test_search_drops_files_over_the_size_limit(client: TestClient, world: World, monkeypatch: pytest.MonkeyPatch) -> None:
    world.put(UID, make_glb())
    monkeypatch.setenv("FORGE_MODELS_MAX_BYTES", "100")
    body = client.get("/models/search", params={"q": "jet"}).json()
    assert body["hits"] == [] and "light enough" in body["note"]


def test_search_with_a_token_also_offers_sketchfab_downloads(client: TestClient, world: World, monkeypatch: pytest.MonkeyPatch) -> None:
    world.put(UID, make_glb())
    monkeypatch.setenv("SKETCHFAB_API_TOKEN", "secret-token")
    body = client.get("/models/search", params={"q": "jet"}).json()
    assert [h["id"] for h in body["hits"]] == [f"objaverse:{UID}", f"sketchfab:{OTHER}"]
    assert "secret-token" not in json.dumps(body) and all(h is None for h in world.auth)  # the token isn't sent to search


def test_search_needs_a_query_and_a_sane_limit(client: TestClient, world: World) -> None:
    assert client.get("/models/search").status_code == 422
    assert client.get("/models/search", params={"q": "x", "limit": 500}).status_code == 422
    assert client.get("/models/search", params={"q": "   "}).status_code == 400
    assert world.asked == []


def test_search_says_when_the_source_is_down(client: TestClient, world: World, monkeypatch: pytest.MonkeyPatch) -> None:
    async def down(*args: Any, **kwargs: Any) -> Any:
        raise service.ModelError(502, "source_unreachable", "couldn't reach api.sketchfab.com")

    monkeypatch.setattr(service, "_json", down)
    r = client.get("/models/search", params={"q": "jet"})
    assert r.status_code == 502 and r.json()["detail"].startswith("source_unreachable:")


# ---- fetch -------------------------------------------------------------------------------------------------

def test_fetch_checks_caches_and_serves_the_bytes(client: TestClient, world: World, tmp_path) -> None:
    data = make_glb(500, extras={"license": "CC-BY-4.0 (http://creativecommons.org/licenses/by/4.0/)"}, png=(1024, 1024))
    world.put(UID, data)
    meta = client.get("/models/fetch", params={"id": f"objaverse:{UID}"}).json()
    assert meta["sha256"] == hashlib.sha256(data).hexdigest() and meta["size_bytes"] == len(data)
    assert (meta["triangles"], meta["vertices"], meta["max_texture_px"]) == (500, 1000, 1024)
    assert meta["licence"] == "CC-BY-4.0" and meta["author"] == "Ada"
    assert meta["attribution"] == f"\"Jet engine\" by Ada, CC-BY-4.0. https://sketchfab.com/3d-models/jet-{UID}"
    assert (tmp_path / "models" / f"objaverse-{UID}.glb").read_bytes() == data

    asked = len(world.asked)
    file = client.get(meta["file"])
    assert file.status_code == 200 and file.content == data and file.headers["content-type"] == "model/gltf-binary"
    assert file.headers["x-model-sha256"] == meta["sha256"] and file.headers["x-model-licence"] == "CC-BY-4.0"
    assert file.headers["x-model-author"] == "Ada" and file.headers["x-model-source-url"] == meta["source_url"]
    assert len(world.asked) == asked and len(world.downloads) == 1  # served from the cache


def test_a_tampered_cache_file_is_downloaded_again(client: TestClient, world: World, tmp_path) -> None:
    data = make_glb()
    world.put(UID, data)
    client.get("/models/fetch", params={"id": f"objaverse:{UID}"})
    (tmp_path / "models" / f"objaverse-{UID}.glb").write_bytes(b"glTF something else")
    assert client.get("/models/file", params={"id": f"objaverse:{UID}"}).content == data
    assert len(world.downloads) == 2


@pytest.mark.parametrize("bad", ["", "chair", "https://example.com/a.glb", "objaverse:../../etc/passwd",
                                 f"objaverse:{UID.upper()}", f"polyhaven:{UID}", f"objaverse:{UID}/../x"])
def test_fetch_takes_ids_not_urls(client: TestClient, world: World, bad: str) -> None:
    r = client.get("/models/fetch", params={"id": bad})
    assert r.status_code == 400 and r.json()["detail"].startswith("bad_id:")
    assert world.asked == [] and world.downloads == []


def test_fetch_refuses_a_closed_licence_before_downloading(client: TestClient, world: World) -> None:
    world.put(NC, make_glb())
    r = client.get("/models/fetch", params={"id": f"objaverse:{NC}"})
    assert r.status_code == 403 and "licence_refused" in r.json()["detail"] and "NonCommercial" in r.json()["detail"]
    world.catalogue[NC]["license"] = None  # no licence at all is refused too
    assert client.get("/models/file", params={"id": f"objaverse:{NC}"}).status_code == 403
    assert world.downloads == []


def test_fetch_refuses_a_copy_published_under_a_closed_licence(client: TestClient, world: World, tmp_path) -> None:
    world.put(UID, make_glb(extras={"license": "CC-BY-NC-4.0 (http://creativecommons.org/licenses/by-nc/4.0/)"}))
    r = client.get("/models/fetch", params={"id": f"objaverse:{UID}"})
    assert r.status_code == 403 and "CC-BY-NC-4.0" in r.json()["detail"]
    assert not (tmp_path / "models").exists()


def test_fetch_refuses_too_big_before_and_after_downloading(client: TestClient, world: World, monkeypatch: pytest.MonkeyPatch) -> None:
    data = make_glb()
    world.put(UID, data)
    monkeypatch.setenv("FORGE_MODELS_MAX_BYTES", str(len(data) - 1))
    r = client.get("/models/fetch", params={"id": f"objaverse:{UID}"})
    assert r.status_code == 413 and r.json()["detail"].startswith("too_big:") and world.downloads == []
    world.objaverse[UID] = 10  # the listing understates it: the download itself is capped
    assert client.get("/models/fetch", params={"id": f"objaverse:{UID}"}).status_code == 413


@pytest.mark.parametrize("data, status, code", [
    (b"<html>Sign in</html>", 422, "not_glb"),
    (make_glb(300_000), 422, "too_heavy"),
    (make_glb(png=(4096, 4096)), 422, "too_heavy"),
    (make_glb(required=["KHR_draco_mesh_compression"]), 422, "cant_decode"),
    (make_glb(0), 422, "not_glb"),
])
def test_fetch_refuses_what_the_headset_cannot_show(client: TestClient, world: World, tmp_path, data: bytes, status: int, code: str) -> None:
    world.put(UID, data)
    r = client.get("/models/fetch", params={"id": f"objaverse:{UID}"})
    assert r.status_code == status and r.json()["detail"].startswith(code + ":")
    assert not (tmp_path / "models").exists()  # nothing refused is cached


def test_fetch_of_a_model_with_no_open_copy_is_not_found(client: TestClient, world: World) -> None:
    assert client.get("/models/fetch", params={"id": f"objaverse:{UID}"}).status_code == 404
    assert client.get("/models/fetch", params={"id": f"objaverse:{OTHER}"}).status_code == 404


def test_sketchfab_download_needs_the_server_token(client: TestClient, world: World, monkeypatch: pytest.MonkeyPatch) -> None:
    r = client.get("/models/fetch", params={"id": f"sketchfab:{UID}"})
    assert r.status_code == 503 and "SKETCHFAB_API_TOKEN" in r.json()["detail"]
    data = make_glb()
    world.files["https://sketchfab-prod-media.s3.amazonaws.com/archives/x.glb"] = data
    monkeypatch.setenv("SKETCHFAB_API_TOKEN", "secret-token")
    r = client.get("/models/fetch", params={"id": f"sketchfab:{UID}"})
    assert r.status_code == 200 and r.json()["source"] == "sketchfab" and "secret-token" not in r.text
    sent = [h for a, h in zip(world.asked, world.auth) if a.endswith("/download")]
    assert sent == [{"Authorization": "Token secret-token"}]
    assert [h for a, h in zip(world.asked, world.auth) if not a.endswith("/download") and h] == []


# ---- tools -------------------------------------------------------------------------------------------------

def test_tools_search_then_import_one_chosen_model(world: World) -> None:
    data = make_glb()
    world.put(UID, data)
    sent: list[tuple[str, Any]] = []

    async def publish(type_: str, payload: Any) -> None:
        sent.append((type_, payload))

    found = asyncio.run(tools.call_tool("model_search", {"query": "jet engine"}, publish))
    assert found["hits"] == [{"id": f"objaverse:{UID}", "name": "Jet engine", "author": "Ada", "licence": "CC-BY-4.0",
                              "size_bytes": len(data), "triangles": 1200}]
    assert sent == [] and world.downloads == []  # searching never downloads or imports

    done = asyncio.run(tools.call_tool("model_import", {"id": f"objaverse:{UID}"}, publish))
    assert done["licence"] == "CC-BY-4.0" and "Ada" in done["attribution"] and "error" not in done
    assert sent[0][0] == "models.import"
    assert sent[0][1].model_dump() == {"id": f"objaverse:{UID}", "name": "Jet engine", "author": "Ada", "licence": "CC-BY-4.0",
                                       "source_url": f"https://sketchfab.com/3d-models/jet-{UID}",
                                       "sha256": hashlib.sha256(data).hexdigest(), "size_bytes": len(data)}


def test_tools_return_errors_as_data(world: World) -> None:
    assert asyncio.run(tools.call_tool("model_import", {"id": "nope"}))["error"].startswith("bad_id:")
    assert asyncio.run(tools.call_tool("model_search", {"query": ""}))["error"].startswith("bad_query:")
    assert "licence_refused" in asyncio.run(tools.call_tool("model_import", {"id": f"objaverse:{NC}"}))["error"]
    world.put(UID, make_glb())
    assert "no headset" in asyncio.run(tools.call_tool("model_import", {"id": f"objaverse:{UID}"}))["error"]
    assert {t["name"] for t in tools.TOOLS} == {"model_search", "model_import"}
