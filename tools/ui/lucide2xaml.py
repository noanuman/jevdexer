"""Converts Lucide SVG icons into a WPF ResourceDictionary of Geometry resources (key: Icon.<Name>).
Lucide icons are 24x24 strokes; they are drawn with a Path stroked at 2 (see the IconPath style)."""
import os, re, sys
import xml.etree.ElementTree as ET

src, out = sys.argv[1], sys.argv[2]
names = sys.argv[3:]

def num(v): return float(v)
def fmt(v): return ('%g' % v)

def element_to_path(el):
    tag = el.tag.split('}')[-1]
    a = el.attrib
    if tag == 'path':
        # A path's first moveto is absolute even when written 'm'. Joined with the previous element's
        # data it would become relative to that element's end point, so make it explicit.
        # Pairs after 'm' are relative line-tos, so they must stay relative: "m19 12-7 7" -> "M19 12 l-7 7".
        d = a['d'].strip()
        m = re.match(r'^m\s*(-?[\d.]+)[\s,]*(-?[\d.]+)(.*)$', d, re.S)
        if not m:
            return d
        x, y, rest = m.group(1), m.group(2), m.group(3).strip()
        if rest and (rest[0].isdigit() or rest[0] in '-.'):
            return f'M{x} {y} l{rest}'
        return f'M{x} {y} {rest}'.strip()
    if tag == 'circle':
        cx, cy, r = num(a['cx']), num(a['cy']), num(a['r'])
        return f"M{fmt(cx - r)} {fmt(cy)}a{fmt(r)} {fmt(r)} 0 1 0 {fmt(2 * r)} 0a{fmt(r)} {fmt(r)} 0 1 0 {fmt(-2 * r)} 0"
    if tag == 'ellipse':
        cx, cy, rx, ry = num(a['cx']), num(a['cy']), num(a['rx']), num(a['ry'])
        return f"M{fmt(cx - rx)} {fmt(cy)}a{fmt(rx)} {fmt(ry)} 0 1 0 {fmt(2 * rx)} 0a{fmt(rx)} {fmt(ry)} 0 1 0 {fmt(-2 * rx)} 0"
    if tag == 'line':
        return f"M{a['x1']} {a['y1']}L{a['x2']} {a['y2']}"
    if tag in ('polyline', 'polygon'):
        pts = re.split(r'[\s,]+', a['points'].strip())
        pairs = [f"{pts[i]} {pts[i + 1]}" for i in range(0, len(pts), 2)]
        return 'M' + 'L'.join(pairs) + ('Z' if tag == 'polygon' else '')
    if tag == 'rect':
        x, y, w, h = num(a.get('x', 0)), num(a.get('y', 0)), num(a['width']), num(a['height'])
        rx = num(a.get('rx', a.get('ry', 0)))
        if rx == 0:
            return f"M{fmt(x)} {fmt(y)}h{fmt(w)}v{fmt(h)}h{fmt(-w)}Z"
        return (f"M{fmt(x + rx)} {fmt(y)}h{fmt(w - 2 * rx)}a{fmt(rx)} {fmt(rx)} 0 0 1 {fmt(rx)} {fmt(rx)}"
                f"v{fmt(h - 2 * rx)}a{fmt(rx)} {fmt(rx)} 0 0 1 {fmt(-rx)} {fmt(rx)}h{fmt(-(w - 2 * rx))}"
                f"a{fmt(rx)} {fmt(rx)} 0 0 1 {fmt(-rx)} {fmt(-rx)}v{fmt(-(h - 2 * rx))}a{fmt(rx)} {fmt(rx)} 0 0 1 {fmt(rx)} {fmt(-rx)}Z")
    raise ValueError('unsupported element ' + tag)

lines = [
    '<!-- Generated from lucide-static by lucide2xaml.py. Lucide is ISC licensed (Assets/Lucide-ISC.txt). Do not edit by hand. -->',
    '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
    '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
]
for name in names:
    root = ET.parse(os.path.join(src, name + '.svg')).getroot()
    parts = [element_to_path(el) for el in root if el.tag.split('}')[-1] != 'title']
    key = ''.join(p.capitalize() for p in name.split('-'))
    data = ' '.join(parts)
    lines.append(f'    <Geometry x:Key="Icon.{key}">F0 {data}</Geometry>')
lines.append('</ResourceDictionary>')
open(out, 'w', encoding='utf8').write('\n'.join(lines) + '\n')
print(f'{len(names)} icons -> {out}')
