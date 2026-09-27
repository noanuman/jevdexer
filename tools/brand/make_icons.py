"""App and tray icons from the mascot (brand/mascot.png).

- src/Jevdexer.App/Assets/Jevdexer.ico: the 3D render at 16..256 px (exe, installer, Start menu).
- src/Jevdexer.App/Assets/tray-dark.png: the mascot as is, for a light taskbar.
- src/Jevdexer.App/Assets/tray-light.png: a light blob with dark eyes and smile, for a dark
  taskbar, where the black mascot would disappear.

Run: python tools/brand/make_icons.py
"""
import os

import cv2
import numpy as np
from PIL import Image

HERE = os.path.dirname(__file__)
ROOT = os.path.join(HERE, "..", "..")
BRAND = os.path.join(ROOT, "brand")
ASSETS = os.path.join(ROOT, "src", "Jevdexer.App", "Assets")


def square(img):
    a = img[:, :, 3]
    ys, xs = np.where(a > 8)
    x0, x1, y0, y1 = xs.min(), xs.max(), ys.min(), ys.max()
    size = int(max(x1 - x0, y1 - y0) * 1.04)
    cx, cy = (x0 + x1) // 2, (y0 + y1) // 2
    out = np.zeros((size, size, 4), np.uint8)
    sx, sy = cx - size // 2, cy - size // 2
    src = img[max(0, sy):sy + size, max(0, sx):sx + size]
    out[max(0, -sy):max(0, -sy) + src.shape[0], max(0, -sx):max(0, -sx) + src.shape[1]] = src
    return out


def sticker(img, size):
    """The mascot on a white cut-out border, square, `size` px."""
    border = max(1.6, size * 0.045) * img.shape[0] / size  # in source pixels
    inner = int(img.shape[0] - 2 * border)
    mascot = cv2.resize(img, (inner, inner), interpolation=cv2.INTER_AREA)
    canvas = np.zeros_like(img)
    o = int(border)
    canvas[o:o + inner, o:o + inner] = mascot
    alpha = canvas[:, :, 3]
    k = int(border * 2) | 1
    halo = cv2.dilate((alpha > 40).astype(np.uint8) * 255, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k)))
    halo = cv2.GaussianBlur(halo, (0, 0), border * 0.25)
    out = np.zeros_like(img)
    out[..., :3] = 250
    out[..., 3] = halo
    a = alpha[..., None] / 255.0
    out[..., :3] = (canvas[..., :3] * a + out[..., :3] * (1 - a)).astype(np.uint8)
    out[..., 3] = np.maximum(halo, alpha)
    small = cv2.resize(out, (size, size), interpolation=cv2.INTER_AREA)
    return Image.fromarray(cv2.cvtColor(small, cv2.COLOR_BGRA2RGBA))


def main():
    img = square(cv2.imread(os.path.join(BRAND, "mascot.png"), cv2.IMREAD_UNCHANGED))
    rgba = cv2.cvtColor(img, cv2.COLOR_BGRA2RGBA)

    # App icon: the render as a sticker, with a white border, so it reads on a dark taskbar and
    # Start menu too (a plain black blob disappears there). Each size is made separately, with a
    # border that stays about 1.5 px wide when small.
    frames = []
    for size in (256, 128, 64, 48, 32, 24, 16):
        frames.append(sticker(img, size))
    frames[0].save(os.path.join(ASSETS, "Jevdexer.ico"), sizes=[f.size for f in frames], append_images=frames[1:])
    frames[0].save(os.path.join(BRAND, "icon-sticker-256.png"))

    # Tray icons are 16 to 24 px on screen: the render's eyes turn to noise there. Both are the
    # silhouette with two bold eye dots (no pupils, no smile), dark on a light taskbar, light on a dark one.
    alpha = img[:, :, 3]
    body = alpha > 128
    bright = img[:, :, :3].astype(int).sum(axis=2) > 600
    white = (bright & body).astype(np.uint8)
    contours, _ = cv2.findContours(white * 255, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    eyes = sorted(contours, key=cv2.contourArea, reverse=True)[:2]
    dots = np.zeros_like(white)
    for c in eyes:
        (cx, cy), (w, h), ang = cv2.fitEllipse(c)
        cv2.ellipse(dots, ((cx, cy), (w * 1.05, h * 1.12), ang), 1, -1)
    for name, fill, eye in (("tray-dark.png", 18, 250), ("tray-light.png", 250, 24)):
        icon = np.zeros_like(img)
        icon[..., 3] = alpha
        icon[..., :3] = fill
        icon[dots.astype(bool) & body, :3] = eye
        Image.fromarray(cv2.cvtColor(icon, cv2.COLOR_BGRA2RGBA)).resize((32, 32), Image.LANCZOS).save(os.path.join(ASSETS, name))
    print("icons written")


if __name__ == "__main__":
    main()
