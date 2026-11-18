"""Reading a binary glTF (.glb) without loading it: is it really one, and how heavy is it for a Quest.

Only the 12-byte header, the JSON chunk and the first bytes of each embedded image are read. Nothing is decoded.
The headset runs the same checks again before it saves a file (ModelImportGlb.cs): the server's word isn't trusted.
"""
from __future__ import annotations

import json
import struct
from typing import Any

from pydantic import BaseModel

MAGIC = b"glTF"
_JSON = 0x4E4F534A
_BIN = 0x004E4942
# glTFast on the headset has no decoder package for these (unity/Packages/manifest.json), so such a file can't load.
UNSUPPORTED = frozenset({"KHR_draco_mesh_compression", "EXT_meshopt_compression", "KHR_texture_basisu",
                         "EXT_texture_webp"})


class GlbError(ValueError):
    """The bytes aren't a usable binary glTF."""


class GlbStats(BaseModel):
    triangles: int = 0
    vertices: int = 0
    meshes: int = 0
    max_texture_px: int = 0  # the largest side of any embedded PNG or JPEG; 0 when there are none (or none readable)
    unsupported: list[str] = []  # required extensions the headset can't decode
    extras: dict[str, str] = {}  # asset.extras strings: Sketchfab exports carry author, license, source, title


def is_glb(data: bytes) -> bool:
    return len(data) >= 12 and data[:4] == MAGIC


def inspect(data: bytes) -> GlbStats:
    """What a .glb holds. Raises GlbError for anything that isn't a well-formed glTF 2 binary."""
    if not is_glb(data):
        raise GlbError("not a binary glTF: the file doesn't start with 'glTF'")
    version, length = struct.unpack_from("<II", data, 4)
    if version != 2:
        raise GlbError(f"glTF version {version}, only 2 loads")
    if length != len(data):
        raise GlbError("the file's length doesn't match its header (cut short?)")
    if len(data) < 20:
        raise GlbError("no JSON chunk")
    chunk_len, chunk_type = struct.unpack_from("<II", data, 12)
    if chunk_type != _JSON or 20 + chunk_len > len(data):
        raise GlbError("the first chunk isn't JSON")
    try:
        doc = json.loads(data[20:20 + chunk_len])
    except (ValueError, UnicodeDecodeError) as e:
        raise GlbError("its JSON chunk doesn't parse") from e
    if not isinstance(doc, dict):
        raise GlbError("its JSON chunk isn't an object")

    binary = b""
    at = 20 + chunk_len + (-chunk_len % 4)
    if at + 8 <= len(data):
        bin_len, bin_type = struct.unpack_from("<II", data, at)
        if bin_type == _BIN and at + 8 + bin_len <= len(data):
            binary = data[at + 8:at + 8 + bin_len]

    try:
        return _stats(doc, binary)
    except (KeyError, IndexError, TypeError, AttributeError) as e:
        raise GlbError("its JSON doesn't follow glTF 2") from e


def _stats(doc: dict[str, Any], binary: bytes) -> GlbStats:
    accessors = doc.get("accessors") or []
    meshes = doc.get("meshes") or []
    per_mesh: list[tuple[int, int]] = []
    for mesh in meshes:
        triangles = vertices = 0
        for primitive in mesh.get("primitives") or []:
            position = (primitive.get("attributes") or {}).get("POSITION")
            count = int(accessors[position]["count"]) if position is not None else 0
            vertices += count
            indices = primitive.get("indices")
            n = int(accessors[indices]["count"]) if indices is not None else count
            mode = primitive.get("mode", 4)
            if mode == 4:
                triangles += n // 3
            elif mode in (5, 6):
                triangles += max(n - 2, 0)
        per_mesh.append((triangles, vertices))
    # A mesh drawn by several nodes costs that many times over.
    used = [node["mesh"] for node in doc.get("nodes") or [] if isinstance(node, dict) and node.get("mesh") is not None]
    if not used:
        used = list(range(len(per_mesh)))
    triangles = sum(per_mesh[i][0] for i in used)
    vertices = sum(per_mesh[i][1] for i in used)

    largest = 0
    views = doc.get("bufferViews") or []
    for image in doc.get("images") or []:
        view = image.get("bufferView")
        if view is None:
            continue
        start = int(views[view].get("byteOffset", 0))
        largest = max(largest, max(image_size(binary[start:start + int(views[view]["byteLength"])]) or (0, 0)))

    required = [str(x) for x in doc.get("extensionsRequired") or []]
    extras = (doc.get("asset") or {}).get("extras")
    return GlbStats(
        triangles=triangles, vertices=vertices, meshes=len(meshes), max_texture_px=largest,
        unsupported=sorted(x for x in required if x in UNSUPPORTED),
        extras={str(k): v for k, v in extras.items() if isinstance(v, str)} if isinstance(extras, dict) else {})


def image_size(data: bytes) -> tuple[int, int] | None:
    """(width, height) of a PNG or JPEG from its header, or None."""
    if len(data) >= 24 and data[:8] == b"\x89PNG\r\n\x1a\n":
        return struct.unpack_from(">II", data, 16)
    if len(data) >= 4 and data[:2] == b"\xff\xd8":
        at = 2
        while at + 9 <= len(data):
            if data[at] != 0xFF:
                at += 1
                continue
            marker = data[at + 1]
            if marker == 0xFF:  # fill byte
                at += 1
                continue
            if 0xD0 <= marker <= 0xD9 or marker == 0x01:  # no length
                at += 2
                continue
            size = struct.unpack_from(">H", data, at + 2)[0]
            if 0xC0 <= marker <= 0xCF and marker not in (0xC4, 0xC8, 0xCC):  # a start-of-frame
                height, width = struct.unpack_from(">HH", data, at + 5)
                return width, height
            at += 2 + size
    return None
