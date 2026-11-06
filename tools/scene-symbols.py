#!/usr/bin/env python3
"""Writes the scene viewer's own 3D symbol gallery (ADR-281 §5.3): low-poly glTF models made from boxes, cylinders,
cones and spheres here, so nothing in the gallery is anyone else's.

    python3 tools/scene-symbols.py

Writes src/Graticula.Host/wwwroot/symbols/*.glb and gallery.json. Metres, Y up, the model standing on its origin.
Deterministic: the same script writes the same bytes, so a diff of the models is a diff of this file.
"""
import json
import math
import os
import struct

OUT = os.path.join(os.path.dirname(__file__), "..", "src", "Graticula.Host", "wwwroot", "symbols")


# ---- primitives: each returns (positions, normals, indices), positions as (x, y, z) -----------------------------------

def box(sx, sy, sz, at=(0, 0, 0)):
    """A box sx by sy by sz, its base centre at `at`."""
    cx, cy, cz = at
    hx, hz = sx / 2, sz / 2
    faces = [  # normal, four corners
        ((1, 0, 0), [(hx, 0, -hz), (hx, sy, -hz), (hx, sy, hz), (hx, 0, hz)]),
        ((-1, 0, 0), [(-hx, 0, hz), (-hx, sy, hz), (-hx, sy, -hz), (-hx, 0, -hz)]),
        ((0, 1, 0), [(-hx, sy, -hz), (-hx, sy, hz), (hx, sy, hz), (hx, sy, -hz)]),
        ((0, -1, 0), [(-hx, 0, hz), (-hx, 0, -hz), (hx, 0, -hz), (hx, 0, hz)]),
        ((0, 0, 1), [(hx, 0, hz), (hx, sy, hz), (-hx, sy, hz), (-hx, 0, hz)]),
        ((0, 0, -1), [(-hx, 0, -hz), (-hx, sy, -hz), (hx, sy, -hz), (hx, 0, -hz)]),
    ]
    p, n, i = [], [], []
    for normal, corners in faces:
        base = len(p)
        for (x, y, z) in corners:
            p.append((x + cx, y + cy, z + cz))
            n.append(normal)
        i += [base, base + 1, base + 2, base, base + 2, base + 3]
    return p, n, i


def frustum(r0, r1, h, at=(0, 0, 0), sides=12, axis="y"):
    """A cylinder (r0 == r1) or cone (r1 == 0) of height h along an axis, its base centre at `at`, closed at both ends."""
    p, n, i = [], [], []
    slope = (r0 - r1) / h if h else 0
    ring = [(math.cos(2 * math.pi * k / sides), math.sin(2 * math.pi * k / sides)) for k in range(sides)]

    def put(local, normal):
        x, y, z = local
        nx, ny, nz = normal
        if axis == "x":
            x, y, z, nx, ny, nz = y, x, z, ny, nx, nz
        elif axis == "z":
            x, y, z, nx, ny, nz = x, z, y, nx, nz, ny
        p.append((x + at[0], y + at[1], z + at[2]))
        n.append((nx, ny, nz))

    for k in range(sides):
        (c0, s0), (c1, s1) = ring[k], ring[(k + 1) % sides]
        base = len(p)
        for (c, s, r, y) in ((c0, s0, r0, 0), (c1, s1, r0, 0), (c1, s1, r1, h), (c0, s0, r1, h)):
            length = math.sqrt(1 + slope * slope)
            put((c * r, y, s * r), (c / length, slope / length, s / length))
        i += [base, base + 2, base + 1, base, base + 3, base + 2]
    for (y, r, ny) in ((0, r0, -1), (h, r1, 1)):
        if r <= 0:
            continue
        centre = len(p)
        put((0, y, 0), (0, ny, 0))
        for (c, s) in ring:
            put((c * r, y, s * r), (0, ny, 0))
        for k in range(sides):
            a, b = centre + 1 + k, centre + 1 + (k + 1) % sides
            i += [centre, a, b] if ny > 0 else [centre, b, a]
    return p, n, i


def sphere(r, at=(0, 0, 0), squash=1.0, rings=8, sides=12):
    """A sphere of radius r centred at `at`, squashed vertically by `squash`."""
    p, n, i = [], [], []
    for a in range(rings + 1):
        phi = math.pi * a / rings
        for b in range(sides + 1):
            theta = 2 * math.pi * b / sides
            x, y, z = math.sin(phi) * math.cos(theta), math.cos(phi), math.sin(phi) * math.sin(theta)
            p.append((x * r + at[0], y * r * squash + at[1], z * r + at[2]))
            n.append((x, y, z))
    for a in range(rings):
        for b in range(sides):
            k0, k1 = a * (sides + 1) + b, (a + 1) * (sides + 1) + b
            i += [k0, k0 + 1, k1, k1, k0 + 1, k1 + 1]
    return p, n, i


# ---- the gallery ------------------------------------------------------------------------------------------------------

BARK, LEAF, PINE, WOOD, METAL, GLASS, LIGHT = "#6b4a2b", "#4f8a3a", "#2f6b3a", "#a0703c", "#5d646b", "#9fc3d6", "#fff2b0"
MODELS = {
    "tree": ("Tree", "Vegetation", [(frustum(0.25, 0.2, 2.6), BARK), (sphere(2.0, (0, 4.4, 0), 0.9), LEAF)]),
    "conifer": ("Conifer", "Vegetation", [(frustum(0.2, 0.15, 1.4), BARK), (frustum(1.8, 0, 6.2, (0, 1.2, 0)), PINE)]),
    "bush": ("Bush", "Vegetation", [(sphere(0.8, (0, 0.55, 0), 0.7), LEAF)]),
    "bench": ("Bench", "Street furniture", [
        (box(1.8, 0.08, 0.45, (0, 0.42, 0)), WOOD), (box(1.8, 0.4, 0.06, (0, 0.55, -0.22)), WOOD),
        (box(0.06, 0.42, 0.4, (-0.8, 0, 0)), METAL), (box(0.06, 0.42, 0.4, (0.8, 0, 0)), METAL)]),
    "street-lamp": ("Street lamp", "Street furniture", [
        (frustum(0.1, 0.07, 6.0), METAL), (box(1.2, 0.08, 0.1, (0.55, 5.9, 0)), METAL),
        (box(0.45, 0.15, 0.25, (1.05, 5.75, 0)), LIGHT)]),
    "bollard": ("Bollard", "Street furniture", [(frustum(0.1, 0.1, 0.8), METAL), (sphere(0.1, (0, 0.8, 0)), METAL)]),
    "bin": ("Litter bin", "Street furniture", [(frustum(0.28, 0.3, 0.9), "#3d5a40"), (frustum(0.31, 0.31, 0.05, (0, 0.9, 0)), METAL)]),
    "bus-stop": ("Bus stop", "Street furniture", [
        (box(3.0, 0.08, 1.4, (0, 2.5, 0)), METAL), (box(3.0, 2.2, 0.05, (0, 0.3, -0.6)), GLASS),
        (box(0.08, 2.5, 0.08, (-1.4, 0, -0.6)), METAL), (box(0.08, 2.5, 0.08, (1.4, 0, -0.6)), METAL),
        (box(2.0, 0.08, 0.35, (0, 0.45, -0.4)), WOOD)]),
    "hydrant": ("Fire hydrant", "Street furniture", [
        (frustum(0.15, 0.13, 0.65), "#c0392b"), (sphere(0.13, (0, 0.65, 0), 0.8), "#c0392b"),
        (frustum(0.05, 0.05, 0.36, (-0.18, 0.4, 0), axis="x"), "#c0392b")]),
    "traffic-cone": ("Traffic cone", "Traffic", [(box(0.4, 0.04, 0.4), "#222222"), (frustum(0.16, 0.03, 0.7, (0, 0.04, 0)), "#f39c12")]),
    "car": ("Car", "Traffic", [
        (box(4.2, 0.75, 1.8, (0, 0.3, 0)), "#2e6fb5"), (box(2.2, 0.6, 1.6, (-0.2, 1.05, 0)), GLASS),
        (frustum(0.33, 0.33, 1.9, (-1.35, 0.33, -0.95), axis="z"), "#1b1b1b"),
        (frustum(0.33, 0.33, 1.9, (1.35, 0.33, -0.95), axis="z"), "#1b1b1b")]),
    "person": ("Person", "People", [
        (frustum(0.17, 0.2, 0.85), "#34495e"), (frustum(0.22, 0.18, 0.6, (0, 0.85, 0)), "#7f8c8d"),
        (sphere(0.12, (0, 1.6, 0)), "#e0b48a")]),
}


def glb(parts):
    """One glTF 2.0 binary: a mesh with a primitive for each part, each part its own colour."""
    buffer = bytearray()
    views, accessors, primitives, materials = [], [], [], []
    colours = {}
    for (p, n, i), colour in parts:
        if colour not in colours:
            r, g, b = (int(colour[k:k + 2], 16) / 255 for k in (1, 3, 5))
            # sRGB to linear, as glTF's baseColorFactor is linear.
            lin = [round(((c + 0.055) / 1.055) ** 2.4 if c > 0.04045 else c / 12.92, 4) for c in (r, g, b)]
            colours[colour] = len(materials)
            materials.append({"pbrMetallicRoughness": {"baseColorFactor": lin + [1.0], "metallicFactor": 0.0, "roughnessFactor": 0.9}})
        attrs = {}
        for name, data in (("POSITION", p), ("NORMAL", n)):
            offset = len(buffer)
            for v in data:
                buffer += struct.pack("<3f", *v)
            views.append({"buffer": 0, "byteOffset": offset, "byteLength": len(data) * 12, "target": 34962})
            accessor = {"bufferView": len(views) - 1, "componentType": 5126, "count": len(data), "type": "VEC3"}
            if name == "POSITION":
                accessor["min"] = [min(v[k] for v in data) for k in range(3)]
                accessor["max"] = [max(v[k] for v in data) for k in range(3)]
            accessors.append(accessor)
            attrs[name] = len(accessors) - 1
        offset = len(buffer)
        for k in i:
            buffer += struct.pack("<H", k)
        while len(buffer) % 4:
            buffer += b"\0"
        views.append({"buffer": 0, "byteOffset": offset, "byteLength": len(i) * 2, "target": 34963})
        accessors.append({"bufferView": len(views) - 1, "componentType": 5123, "count": len(i), "type": "SCALAR"})
        primitives.append({"attributes": attrs, "indices": len(accessors) - 1, "material": colours[colour]})
    doc = {"asset": {"version": "2.0", "generator": "Graticula tools/scene-symbols.py"}, "scene": 0,
           "scenes": [{"nodes": [0]}], "nodes": [{"mesh": 0}], "meshes": [{"primitives": primitives}],
           "materials": materials, "accessors": accessors, "bufferViews": views, "buffers": [{"byteLength": len(buffer)}]}
    text = json.dumps(doc, separators=(",", ":")).encode()
    text += b" " * (-len(text) % 4)
    body = struct.pack("<II", len(text), 0x4E4F534A) + text + struct.pack("<II", len(buffer), 0x004E4942) + bytes(buffer)
    return struct.pack("<III", 0x46546C67, 2, 12 + len(body)) + body


def main():
    os.makedirs(OUT, exist_ok=True)
    items = []
    for name, (title, category, parts) in MODELS.items():
        data = glb(parts)
        with open(os.path.join(OUT, name + ".glb"), "wb") as f:
            f.write(data)
        height = max(v[1] for (p, _, _), _ in parts for v in p)
        items.append({"name": name, "title": title, "category": category, "href": name + ".glb", "height": round(height, 2)})
    with open(os.path.join(OUT, "gallery.json"), "w") as f:
        json.dump({"name": "Graticula", "licence": "the product's own, made by tools/scene-symbols.py", "items": items}, f, indent=1)
        f.write("\n")
    print(f"{len(items)} models in {os.path.normpath(OUT)}")


if __name__ == "__main__":
    main()
