"""Generates src/Jevdexer.App/Assets/Jevdexer.ico: a zinc-900 rounded square with Lucide's "search"
glyph in white, at 16 to 256 px. Pure Python (zlib + struct), 4x4 supersampled for smooth edges.

    python tools/ui/make_icon.py
"""
import math, os, struct, zlib

BG = (24, 24, 27)       # zinc-900, shadcn primary
FG = (250, 250, 250)    # zinc-50
SIZES = [16, 24, 32, 48, 64, 128, 256]
SS = 4                   # supersampling per axis

def coverage(x, y):
    """(bg alpha, fg alpha) at a point in 0..1 icon space."""
    # Rounded square with a 22% corner radius, inset slightly so edges stay crisp.
    inset, r = 0.02, 0.22
    cx = min(max(x, inset + r), 1 - inset - r)
    cy = min(max(y, inset + r), 1 - inset - r)
    in_bg = (x - cx) ** 2 + (y - cy) ** 2 <= r * r and inset <= x <= 1 - inset and inset <= y <= 1 - inset
    # Lucide search on a 24 grid, mapped into 0.2..0.8: circle r=8 at (11,11), handle to (21,21).
    s, o = 0.6 / 24, 0.2
    gx, gy = (x - o) / s, (y - o) / s
    stroke = 2.4  # a touch bolder than Lucide's 2, so it survives at 16 px
    ring = abs(math.hypot(gx - 11, gy - 11) - 8) <= stroke / 2
    # Segment from (16.65,16.65) to (21,21), round caps.
    ax, ay, bx, by = 16.65, 16.65, 21, 21
    t = max(0, min(1, ((gx - ax) * (bx - ax) + (gy - ay) * (by - ay)) / ((bx - ax) ** 2 + (by - ay) ** 2)))
    handle = math.hypot(gx - (ax + t * (bx - ax)), gy - (ay + t * (by - ay))) <= stroke / 2
    return in_bg, in_bg and (ring or handle)

def render(size):
    rows = []
    for py in range(size):
        row = bytearray([0])  # PNG filter: none
        for px in range(size):
            bg = fg = 0
            for sy in range(SS):
                for sx in range(SS):
                    b, f = coverage((px + (sx + 0.5) / SS) / size, (py + (sy + 0.5) / SS) / size)
                    bg += b
                    fg += f
            n = SS * SS
            a_bg, a_fg = bg / n, fg / n
            # fg drawn over bg
            color = [round(BG[i] * (1 - a_fg / max(a_bg, 1e-9)) + FG[i] * (a_fg / max(a_bg, 1e-9))) if a_bg else 0 for i in range(3)]
            row += bytes(color) + bytes([round(255 * a_bg)])
        rows.append(bytes(row))
    raw = zlib.compress(b''.join(rows), 9)
    def chunk(tag, data):
        return struct.pack('>I', len(data)) + tag + data + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff)
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)) + chunk(b'IDAT', raw) + chunk(b'IEND', b'')

def main():
    images = [render(s) for s in SIZES]
    header = struct.pack('<HHH', 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, blobs = b'', b''
    for size, png in zip(SIZES, images):
        dim = 0 if size >= 256 else size
        entries += struct.pack('<BBBBHHII', dim, dim, 0, 0, 1, 32, len(png), offset + len(blobs))
        blobs += png
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    out = os.path.join(root, 'src', 'Jevdexer.App', 'Assets', 'Jevdexer.ico')
    with open(out, 'wb') as f:
        f.write(header + entries + blobs)
    with open(out.replace('.ico', '-256.png'), 'wb') as f:
        f.write(images[-1])
    print(f'{out} ({len(SIZES)} sizes)')

if __name__ == '__main__':
    main()
