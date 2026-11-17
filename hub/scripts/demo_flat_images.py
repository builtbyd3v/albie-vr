"""Finish the demo pictures: recentre the Blender camera renders and draw the two flat model-hub cards.

    python hub/scripts/demo_flat_images.py          (run after demo_blender_cameras.py)

cam_a..d.png: crop to the visible pixels, pad to a square with a margin, 512 x 512, transparent.
model_a.png / model_b.png: abstract face with a tracking box and keypoints on a dark rounded tile (flat, no text).
"""
from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parents[2] / "unity/Assets/_Forge/Demo/Resources/DemoImages"
S = 4  # supersample
N = 512


def recentre(path: Path, margin: float = 0.10) -> None:
    im = Image.open(path).convert("RGBA")
    box = im.getchannel("A").point(lambda a: 255 if a > 8 else 0).getbbox()
    if not box:
        return
    im = im.crop(box)
    side = int(max(im.size) / (1 - 2 * margin))
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.paste(im, ((side - im.width) // 2, (side - im.height) // 2))
    canvas.resize((N, N), Image.LANCZOS).save(path)


def tile(bg: tuple[int, int, int]) -> tuple[Image.Image, ImageDraw.ImageDraw]:
    im = Image.new("RGBA", (N * S, N * S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle((0, 0, N * S - 1, N * S - 1), radius=56 * S, fill=bg + (255,))
    return im, d


def brackets(d: ImageDraw.ImageDraw, box: tuple[float, float, float, float], col, w: float, arm: float) -> None:
    x0, y0, x1, y1 = [v * S for v in box]
    w, arm = w * S, arm * S
    for (cx, cy, sx, sy) in ((x0, y0, 1, 1), (x1, y0, -1, 1), (x0, y1, 1, -1), (x1, y1, -1, -1)):
        d.line((cx, cy, cx + sx * arm, cy), fill=col, width=int(w))
        d.line((cx, cy, cx, cy + sy * arm), fill=col, width=int(w))


def dot(d: ImageDraw.ImageDraw, x: float, y: float, r: float, col) -> None:
    d.ellipse(((x - r) * S, (y - r) * S, (x + r) * S, (y + r) * S), fill=col)


def face(d: ImageDraw.ImageDraw, cx: float, cy: float, skin, feature) -> None:
    d.ellipse(((cx - 120) * S, (cy - 150) * S, (cx + 120) * S, (cy + 150) * S), fill=skin)
    for ex in (-48, 48):
        d.ellipse(((cx + ex - 17) * S, (cy - 38) * S, (cx + ex + 17) * S, (cy - 4) * S), fill=feature)
    d.line((cx * S, (cy - 8) * S, cx * S, (cy + 34) * S), fill=feature, width=7 * S)
    d.arc(((cx - 48) * S, (cy + 40) * S, (cx + 48) * S, (cy + 96) * S), 20, 160, fill=feature, width=8 * S)


def model_a() -> None:
    im, d = tile((22, 27, 36))
    accent = (74, 163, 255, 255)
    face(d, 256, 262, (62, 74, 92, 255), (22, 27, 36, 255))
    brackets(d, (120, 96, 392, 428), accent, 9, 46)
    for x, y in ((208, 224), (304, 224), (256, 270), (216, 336), (296, 336)):
        dot(d, x, y, 9, accent)
    d.rounded_rectangle((150 * S, 452 * S, 362 * S, 462 * S), radius=5 * S, fill=(42, 49, 60, 255))
    d.rounded_rectangle((150 * S, 452 * S, 330 * S, 462 * S), radius=5 * S, fill=accent)
    im.resize((N, N), Image.LANCZOS).save(OUT / "model_a.png")


def model_b() -> None:
    im, d = tile((24, 24, 30))
    accent = (56, 214, 168, 255)
    face(d, 256, 262, (70, 66, 82, 255), (24, 24, 30, 255))
    pts = [(176, 190), (212, 168), (256, 160), (300, 168), (336, 190), (352, 250), (340, 320), (300, 380),
           (256, 404), (212, 380), (172, 320), (160, 250), (216, 226), (296, 226), (256, 280), (226, 338),
           (256, 352), (286, 338)]
    links = [(0, 1), (1, 2), (2, 3), (3, 4), (4, 5), (5, 6), (6, 7), (7, 8), (8, 9), (9, 10), (10, 11), (11, 0),
             (12, 14), (13, 14), (15, 16), (16, 17), (14, 16)]
    for a, b in links:
        d.line((pts[a][0] * S, pts[a][1] * S, pts[b][0] * S, pts[b][1] * S), fill=accent[:3] + (150,), width=3 * S)
    for x, y in pts:
        dot(d, x, y, 7, accent)
    brackets(d, (132, 120, 380, 432), (240, 240, 245, 255), 7, 34)
    im.resize((N, N), Image.LANCZOS).save(OUT / "model_b.png")


if __name__ == "__main__":
    for n in ("cam_a", "cam_b", "cam_c", "cam_d"):
        recentre(OUT / f"{n}.png")
    model_a()
    model_b()
    print("ok")
