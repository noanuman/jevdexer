"""Traces the mascot (brand/mascot.png) into a clean vector twin (brand/mascot.svg).

The 3D render is for stills; animation (blinking, squash, turning into icons) needs shapes. This
takes the silhouette from the alpha channel and the eyes and smile from the white areas, fits
smooth cubic curves, and writes an SVG on a 0..100 viewBox with ids the animations use:
#body, #eye-left, #eye-right (each: #eye-*-white, #eye-*-pupil, #eye-*-shine), #smile.

Run: python tools/brand/trace_mascot.py
"""
import json
import os

import cv2
import numpy as np

HERE = os.path.dirname(__file__)
BRAND = os.path.join(HERE, "..", "..", "brand")
SRC = os.path.join(BRAND, "mascot.png")


def smooth_closed_path(points, scale, ox, oy):
    """Catmull-Rom through points -> cubic Bezier path (closed), in viewBox units."""
    p = [((x - ox) * scale, (y - oy) * scale) for x, y in points]
    n = len(p)
    d = f"M{p[0][0]:.2f} {p[0][1]:.2f}"
    for i in range(n):
        p0, p1, p2, p3 = p[(i - 1) % n], p[i], p[(i + 1) % n], p[(i + 2) % n]
        c1 = (p1[0] + (p2[0] - p0[0]) / 6, p1[1] + (p2[1] - p0[1]) / 6)
        c2 = (p2[0] - (p3[0] - p1[0]) / 6, p2[1] - (p3[1] - p1[1]) / 6)
        d += f" C{c1[0]:.2f} {c1[1]:.2f} {c2[0]:.2f} {c2[1]:.2f} {p2[0]:.2f} {p2[1]:.2f}"
    return d + " Z"


def main():
    img = cv2.imread(SRC, cv2.IMREAD_UNCHANGED)
    alpha = img[:, :, 3]
    ys, xs = np.where(alpha > 128)
    x0, x1, y0, y1 = xs.min(), xs.max(), ys.min(), ys.max()
    size = max(x1 - x0, y1 - y0)
    # Centre the body in a square 0..100 box with a little padding.
    pad = size * 0.04
    ox = (x0 + x1) / 2 - size / 2 - pad
    oy = (y0 + y1) / 2 - size / 2 - pad
    scale = 100 / (size + 2 * pad)

    # Body: the outer contour, simplified to ~28 points.
    mask = (alpha > 128).astype(np.uint8) * 255
    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    body = max(contours, key=cv2.contourArea)[:, 0, :]
    step = max(1, len(body) // 28)
    body_pts = [tuple(map(float, body[i])) for i in range(0, len(body), step)]

    # Whites (eyes, smile): bright opaque pixels.
    rgb = img[:, :, :3].astype(int)
    white = ((rgb.sum(axis=2) > 600) & (alpha > 200)).astype(np.uint8) * 255
    wc, _ = cv2.findContours(white, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    blobs = sorted([c for c in wc if cv2.contourArea(c) > 300], key=cv2.contourArea, reverse=True)
    eyes = sorted(blobs[:2], key=lambda c: cv2.boundingRect(c)[0])
    smile = blobs[2] if len(blobs) > 2 else None

    def v(x, y):
        return ((x - ox) * scale, (y - oy) * scale)

    out = {"viewBox": 100, "body": smooth_closed_path(body_pts, scale, ox, oy), "eyes": []}
    for c in eyes:
        (cx, cy), (w, h), ang = cv2.fitEllipse(c)
        # Pupil: dark opaque pixels inside the fitted eye ellipse (slightly shrunk, to skip the rim).
        eye_mask = np.zeros(alpha.shape, np.uint8)
        cv2.ellipse(eye_mask, ((cx, cy), (w * 0.92, h * 0.92), ang), 1, -1)
        dark = (eye_mask > 0) & (alpha > 200) & (rgb.sum(axis=2) < 240)
        py, px = np.where(dark)
        pr = np.sqrt(dark.sum() / np.pi) * 1.18  # the threshold misses the pupil's soft edge
        pcx, pcy = px.mean(), py.mean()
        # Shine: the brightest small blob inside the pupil area.
        ex, ey = v(cx, cy)
        out["eyes"].append({
            "cx": round(ex, 2), "cy": round(ey, 2),
            "rx": round(w / 2 * scale, 2), "ry": round(h / 2 * scale, 2),
            "pupil": {"cx": round(v(pcx, pcy)[0], 2), "cy": round(v(pcx, pcy)[1], 2), "r": round(pr * scale, 2)},
        })
    if smile is not None:
        s = smile[:, 0, :]
        left, right = s[s[:, 0].argmin()], s[s[:, 0].argmax()]
        bottom = s[s[:, 1].argmax()]
        (lx, ly), (rx, ry), (bx, by) = v(*left), v(*right), v(*bottom)
        thickness = cv2.contourArea(smile) / max(1, (right[0] - left[0])) * scale
        out["smile"] = {"x1": round(lx, 2), "y1": round(ly, 2), "x2": round(rx, 2), "y2": round(ry, 2),
                        "cx": round((lx + rx) / 2, 2), "cy": round(by + (by - (ly + ry) / 2) * 0.6, 2), "w": round(thickness, 2)}

    json.dump(out, open(os.path.join(BRAND, "mascot-geometry.json"), "w"), indent=1)

    L, R = out["eyes"]
    sm = out["smile"]
    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" role="img" aria-label="Jevdexer mascot">
  <!-- Vector twin of brand/mascot.png, traced by tools/brand/trace_mascot.py. Ids are animation hooks. -->
  <defs>
    <!-- Soft volume like the render: lit from the upper left. -->
    <radialGradient id="mascot-shade" cx="0.38" cy="0.3" r="0.8">
      <stop offset="0" stop-color="#2b2b2f"/>
      <stop offset="0.55" stop-color="#141416"/>
      <stop offset="1" stop-color="#070708"/>
    </radialGradient>
  </defs>
  <path id="body" d="{out['body']}" fill="url(#mascot-shade)"/>
  <g id="eye-left">
    <ellipse id="eye-left-white" cx="{L['cx']}" cy="{L['cy']}" rx="{L['rx']}" ry="{L['ry']}" fill="#f4f2ee"/>
    <circle id="eye-left-pupil" cx="{L['pupil']['cx']}" cy="{L['pupil']['cy']}" r="{L['pupil']['r']}" fill="#0b0b0c"/>
    <circle id="eye-left-shine" cx="{L['pupil']['cx'] + L['pupil']['r'] * 0.35:.2f}" cy="{L['pupil']['cy'] - L['pupil']['r'] * 0.4:.2f}" r="{L['pupil']['r'] * 0.28:.2f}" fill="#f4f2ee"/>
  </g>
  <g id="eye-right">
    <ellipse id="eye-right-white" cx="{R['cx']}" cy="{R['cy']}" rx="{R['rx']}" ry="{R['ry']}" fill="#f4f2ee"/>
    <circle id="eye-right-pupil" cx="{R['pupil']['cx']}" cy="{R['pupil']['cy']}" r="{R['pupil']['r']}" fill="#0b0b0c"/>
    <circle id="eye-right-shine" cx="{R['pupil']['cx'] + R['pupil']['r'] * 0.35:.2f}" cy="{R['pupil']['cy'] - R['pupil']['r'] * 0.4:.2f}" r="{R['pupil']['r'] * 0.28:.2f}" fill="#f4f2ee"/>
  </g>
  <path id="smile" d="M{sm['x1']} {sm['y1']} Q{sm['cx']} {sm['cy']} {sm['x2']} {sm['y2']}" fill="none" stroke="#f4f2ee" stroke-width="{max(1.2, sm['w']):.2f}" stroke-linecap="round"/>
</svg>
'''
    open(os.path.join(BRAND, "mascot.svg"), "w", encoding="utf8").write(svg)

    # Cropped render (square, transparent) for stills: the hero, the app, the video.
    crop = img[int(max(0, oy)):int(oy + (size + 2 * pad)), int(max(0, ox)):int(ox + (size + 2 * pad))]
    cv2.imwrite(os.path.join(BRAND, "mascot-render.png"), crop)
    for s in (512, 256, 128, 64):
        cv2.imwrite(os.path.join(BRAND, f"mascot-render-{s}.png"), cv2.resize(crop, (s, s), interpolation=cv2.INTER_AREA))
    print(json.dumps(out, indent=1)[:900])


if __name__ == "__main__":
    main()
