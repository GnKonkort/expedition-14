"""Generate jumpsuit displacement maps from breast RSI contours.

Displacement encoding (SS14): R=horizontal sample offset, G=vertical, 128=neutral.
Sampling toward body center makes fabric appear stretched outward over the breast.
"""
from __future__ import annotations

from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
BREAST_RSI = ROOT / "Resources/Textures/_Arcane/ERP/Mobs/Breasts/human.rsi"
BASE_DISP = ROOT / "Resources/Textures/Mobs/Species/Human/displacement.rsi/jumpsuit-female.png"
OUT_DIR = ROOT / "Resources/Textures/_Arcane/ERP/Displacements/human_breasts.rsi"

# breast state name -> size index used by ErpOrganConfig
SIZE_STATES = [("aa", 1), ("b", 2), ("c", 3), ("d", 4)]
# how strongly breast contour pulls samples toward torso center
STRENGTH_BY_SIZE = {1: 1.2, 2: 1.8, 3: 2.4, 4: 3.0}
# soft blur radius for smoother clothing warp
BLUR = 1.2


def dir_tiles(arr: np.ndarray):
    """Yield (dir_index, x0, y0, tile) for 2x2 32px directions."""
    for di in range(4):
        x0 = (di % 2) * 32
        y0 = (di // 2) * 32
        yield di, x0, y0, arr[y0 : y0 + 32, x0 : x0 + 32]


def soft_mask(alpha: np.ndarray) -> np.ndarray:
    """Alpha -> soft float mask 0..1 with light blur."""
    m = (alpha.astype(np.float32) / 255.0)
    # box blur
    k = 3
    pad = np.pad(m, k // 2, mode="edge")
    out = np.zeros_like(m)
    for y in range(32):
        for x in range(32):
            out[y, x] = pad[y : y + k, x : x + k].mean()
    return np.clip(out, 0.0, 1.0)


def breast_offsets(mask: np.ndarray, strength: float) -> tuple[np.ndarray, np.ndarray]:
    """Return sample offsets (dx, dy) toward torso center for breast pixels."""
    ys, xs = np.where(mask > 0.05)
    dx = np.zeros((32, 32), dtype=np.float32)
    dy = np.zeros((32, 32), dtype=np.float32)
    if len(xs) == 0:
        return dx, dy

    # Approximate chest center for humanoid doll
    cx = 15.5
    cy = float(ys.min() + (ys.max() - ys.min()) * 0.35)

    for y, x in zip(ys, xs, strict=False):
        w = float(mask[y, x])
        # vector from pixel to center => sample from center direction
        ox = cx - x
        oy = cy - y
        # normalize lightly so edges don't explode
        dist = max((ox * ox + oy * oy) ** 0.5, 1.0)
        scale = strength * w * min(dist / 6.0, 1.0)
        dx[y, x] = ox / dist * scale
        dy[y, x] = oy / dist * scale

    # blur offsets for smoother fabric
    k = 3
    for channel in (dx, dy):
        pad = np.pad(channel, k // 2, mode="edge")
        blurred = np.zeros_like(channel)
        for y in range(32):
            for x in range(32):
                blurred[y, x] = pad[y : y + k, x : x + k].mean()
        channel[:] = blurred

    return dx, dy


def encode(base_tile: np.ndarray, dx: np.ndarray, dy: np.ndarray, mask: np.ndarray) -> np.ndarray:
    """Blend breast offsets onto existing female displacement tile."""
    out = base_tile.copy()
    # base may be RGB or RGBA
    if out.shape[2] == 3:
        rgba = np.zeros((32, 32, 4), dtype=np.uint8)
        rgba[:, :, :3] = out
        rgba[:, :, 3] = 255
        out = rgba

    # Neutral where base is transparent
    base_a = out[:, :, 3].astype(np.float32) / 255.0
    r = out[:, :, 0].astype(np.float32)
    g = out[:, :, 1].astype(np.float32)

    # Add offsets: +dx sample => higher R
    r2 = np.clip(r + dx * (127.0 / 4.0), 0, 255)  # strength mapped roughly for displacementSize~4..127
    g2 = np.clip(g + dy * (127.0 / 4.0), 0, 255)

    # Where only breast mask exists and base is empty, start from neutral
    only_breast = (mask > 0.05) & (base_a < 0.05)
    r2 = np.where(only_breast, 128.0 + dx * (127.0 / 4.0), r2)
    g2 = np.where(only_breast, 128.0 + dy * (127.0 / 4.0), g2)

    a2 = np.clip(np.maximum(base_a, mask) * 255.0, 0, 255)

    out[:, :, 0] = np.clip(r2, 0, 255).astype(np.uint8)
    out[:, :, 1] = np.clip(g2, 0, 255).astype(np.uint8)
    out[:, :, 2] = 128  # unused / keep neutral blue
    out[:, :, 3] = a2.astype(np.uint8)
    return out


def main() -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    base = np.array(Image.open(BASE_DISP).convert("RGBA"))

    for state, size in SIZE_STATES:
        breast_path = BREAST_RSI / f"{state}.png"
        breast = np.array(Image.open(breast_path).convert("RGBA"))
        strength = STRENGTH_BY_SIZE[size]
        out = np.zeros_like(base)

        for di, x0, y0, base_tile in dir_tiles(base):
            breast_tile = breast[y0 : y0 + 32, x0 : x0 + 32]
            mask = soft_mask(breast_tile[:, :, 3])
            dx, dy = breast_offsets(mask, strength)
            out[y0 : y0 + 32, x0 : x0 + 32] = encode(base_tile, dx, dy, mask)

        Image.fromarray(out, "RGBA").save(OUT_DIR / f"jumpsuit-{size}.png")
        print(f"wrote jumpsuit-{size}.png strength={strength}")

    meta = """{
    "version": 1,
    "license": "CC-BY-SA-3.0",
    "copyright": "Generated from Human jumpsuit-female displacement (TheShuEd) + Arcane breast contours (bulbo44kkey)",
    "size": {
        "x": 32,
        "y": 32
    },
    "load": {
        "srgb": false
    },
    "states": [
        { "name": "jumpsuit-1", "directions": 4 },
        { "name": "jumpsuit-2", "directions": 4 },
        { "name": "jumpsuit-3", "directions": 4 },
        { "name": "jumpsuit-4", "directions": 4 }
    ]
}
"""
    (OUT_DIR / "meta.json").write_text(meta, encoding="utf-8")
    print(f"done -> {OUT_DIR}")


if __name__ == "__main__":
    main()
