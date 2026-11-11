"""
Backdrop key: turns screenshots taken with Limber's Backdrop (Extras page) into clean dataset images.

The backdrop colour (found by itself from the picture's edges, or given with --key) is replaced with pure white (or
--bg), the colour's spill / glow on the character's edges is taken out ("despill"), and each picture is cropped to the
character and made square, ready for training.

Use a strongly coloured backdrop (Green screen) for the cleanest cut: white or skin-like colours can't be told apart
from the character.

    python backdrop_key.py "C:/path/to/screenshots" [--out folder] [--bg 255,255,255] [--key 0,255,0]
                           [--size 1024] [--margin 0.06] [--no-crop] [--soft 0.10] [--hard 0.28]

Needs Python 3 with numpy and Pillow:  pip install numpy pillow
"""
import argparse
import os
import sys

import numpy as np
from PIL import Image


def find_key(img):
    """The backdrop colour: the median of the picture's border pixels."""
    h, w, _ = img.shape
    b = max(2, min(h, w) // 50)
    border = np.concatenate([img[:b].reshape(-1, 3), img[-b:].reshape(-1, 3), img[:, :b].reshape(-1, 3), img[:, -b:].reshape(-1, 3)])
    return np.median(border, axis=0)


def key_out(img, key, soft, hard):
    """Alpha (1 = character) from how far each pixel's colour is from the key colour, and the pixels despilled."""
    f = img.astype(np.float32) / 255.0
    k = np.asarray(key, np.float32) / 255.0
    c = int(np.argmax(k))
    others = [i for i in range(3) if i != c]
    k_sat = float(k[c] - max(k[others[0]], k[others[1]]))
    if k_sat > 0.3:
        # A strongly coloured backdrop (green screen and the like): how much a pixel's key channel stands above its
        # other two (the classic keyer). The backdrop's glow on the edges counts as backdrop too; dark hair, shadows
        # and skin don't.
        excess = f[..., c] - np.maximum(f[..., others[0]], f[..., others[1]])
        lo, hi = soft * k_sat, hard * k_sat
        alpha = 1.0 - np.clip((excess - lo) / max(hi - lo, 1e-4), 0.0, 1.0)
    else:
        # Anything else (white, grey...): how far each pixel's colour is from the backdrop's.
        dist = np.sqrt(((f - k) ** 2).sum(-1))
        alpha = np.clip((dist - soft) / max(hard - soft, 1e-4), 0.0, 1.0)

    # Despill: the key's strongest channel limited to the other two's level where it sticks out
    # (green fringe on skin and hair turns back to their own colour).
    out = f.copy()
    if k_sat > 0.15:
        limit = np.maximum(f[..., others[0]], f[..., others[1]])
        out[..., c] = np.minimum(f[..., c], limit)
    return alpha, out


def crop_box(alpha, margin, size_ratio=1.0):
    ys, xs = np.nonzero(alpha > 0.5)
    if len(xs) == 0:
        return None
    x0, x1, y0, y1 = xs.min(), xs.max(), ys.min(), ys.max()
    h, w = alpha.shape
    side = max(x1 - x0, y1 - y0) * (1 + 2 * margin)
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    return int(round(cx - side / 2)), int(round(cy - side / 2)), int(round(cx + side / 2)), int(round(cy + side / 2))


def process(path, out_dir, bg, key, size, margin, crop, soft, hard):
    img = np.asarray(Image.open(path).convert("RGB"))
    k = np.asarray(key, np.float32) if key is not None else find_key(img)
    alpha, fg = key_out(img, k, soft, hard)
    bgc = np.asarray(bg, np.float32) / 255.0
    comp = fg * alpha[..., None] + bgc * (1 - alpha[..., None])
    result = Image.fromarray(np.clip(comp * 255 + 0.5, 0, 255).astype(np.uint8))
    if crop:
        box = crop_box(alpha, margin)
        if box is not None:
            # (outside the picture: filled with the background colour)
            x0, y0, x1, y1 = box
            canvas = Image.new("RGB", (x1 - x0, y1 - y0), tuple(int(v) for v in bg))
            canvas.paste(result, (-x0, -y0))
            result = canvas
        if size > 0:
            result = result.resize((size, size), Image.LANCZOS)
    name = os.path.splitext(os.path.basename(path))[0] + ".png"
    result.save(os.path.join(out_dir, name))
    return name, tuple(int(v) for v in k)


def colour(text):
    parts = [int(p) for p in text.split(",")]
    if len(parts) != 3 or any(p < 0 or p > 255 for p in parts):
        raise argparse.ArgumentTypeError("a colour is three numbers 0-255, like 255,255,255")
    return parts


def main():
    ap = argparse.ArgumentParser(description="Limber backdrop screenshots -> clean dataset images")
    ap.add_argument("input", help="a picture or a folder of pictures")
    ap.add_argument("--out", help="where to save (default: a 'keyed' folder next to the pictures)")
    ap.add_argument("--bg", type=colour, default=[255, 255, 255], help="the new background colour (default white)")
    ap.add_argument("--key", type=colour, help="the backdrop colour (default: found from the picture's edges)")
    ap.add_argument("--size", type=int, default=1024, help="square size in pixels after cropping (0: keep)")
    ap.add_argument("--margin", type=float, default=0.06, help="space round the character when cropping (share of its size)")
    ap.add_argument("--no-crop", action="store_true", help="keep the whole picture")
    ap.add_argument("--soft", type=float, default=0.15, help="colour distance where the character starts to show")
    ap.add_argument("--hard", type=float, default=0.45, help="colour distance where it's fully the character")
    a = ap.parse_args()

    if os.path.isdir(a.input):
        files = [os.path.join(a.input, f) for f in sorted(os.listdir(a.input))
                 if f.lower().endswith((".png", ".jpg", ".jpeg", ".bmp", ".webp"))]
        out = a.out or os.path.join(a.input, "keyed")
    else:
        files = [a.input]
        out = a.out or os.path.join(os.path.dirname(os.path.abspath(a.input)), "keyed")
    if not files:
        sys.exit("No pictures found.")
    os.makedirs(out, exist_ok=True)
    for f in files:
        try:
            name, k = process(f, out, a.bg, a.key, a.size, a.margin, not a.no_crop, a.soft, a.hard)
            print(f"{name}  (backdrop {k[0]},{k[1]},{k[2]})")
        except Exception as e:
            print(f"{os.path.basename(f)}: skipped ({e})")
    print(f"Done: {len(files)} picture(s) in {out}")


if __name__ == "__main__":
    main()
