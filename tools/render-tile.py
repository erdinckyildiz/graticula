#!/usr/bin/env python3
"""Decodes a vector tile and draws it, beside the same extent drawn from source.

Why this exists
---------------
ADR-021 condition 3: four rounds of tile benchmarking across three days, and
nobody had ever looked at a rendered tile. Every check until now was structural
— rings closed, coordinates in range, features decodable. All of those pass on a
tile that is geometrically nonsense: wound the wrong way, y-flipped, off by a
tile, or missing every hole.

So this renders two panels from the same extent:

  left   the MVT that ST_AsMVT produced, decoded here and drawn from the
         decoded tile coordinates only — nothing from the database
  right  the same extent read straight out of PostGIS as WKT and projected
         with plain arithmetic

If the tile is right the two are the same picture. If it is y-flipped, rotated,
scaled wrong, or dropping interior rings, they are visibly not, and that is a
thing an eye catches in one second and a structural assertion never catches at
all.

The decoder is written here rather than taken from a library on purpose: a
library that shares assumptions with the encoder would agree with it about a
mistake they both make.

Usage:  python tools/render-tile.py [z x y] [-o out.png]
"""

import io
import os
import subprocess
import sys

from PIL import Image, ImageDraw

WORLD = 20037508.342789244

# ---------------------------------------------------------------- protobuf


def varint(buf, i):
    value = shift = 0
    while i < len(buf):
        byte = buf[i]
        i += 1
        value |= (byte & 0x7F) << shift
        if not byte & 0x80:
            return value, i
        shift += 7
    raise ValueError("truncated varint")


def fields(buf, start=0, end=None):
    """Yields (field_number, wire_type, payload) over one message."""
    i = start
    end = len(buf) if end is None else end

    while i < end:
        key, i = varint(buf, i)
        num, wire = key >> 3, key & 7

        if wire == 0:
            value, i = varint(buf, i)
            yield num, wire, value
        elif wire == 1:
            yield num, wire, buf[i:i + 8]
            i += 8
        elif wire == 2:
            length, i = varint(buf, i)
            yield num, wire, buf[i:i + length]
            i += length
        elif wire == 5:
            yield num, wire, buf[i:i + 4]
            i += 4
        else:
            raise ValueError(f"wire type {wire}")


def packed(buf):
    out, i = [], 0
    while i < len(buf):
        value, i = varint(buf, i)
        out.append(value)
    return out


# ---------------------------------------------------------------- MVT


def decode(tile):
    """Every layer in the tile, as name -> (extent, [ (type, [rings]) ])."""
    layers = {}

    for num, _, payload in fields(tile):
        if num != 3:
            continue

        name, extent, features = "?", 4096, []

        for f, _, p in fields(payload):
            if f == 1:
                name = p.decode("utf-8")
            elif f == 5:
                extent = p
            elif f == 2:
                features.append(feature(p))

        layers[name] = (extent, features)

    return layers


def feature(buf):
    kind, geometry = 0, []

    for num, _, payload in fields(buf):
        if num == 3:
            kind = payload
        elif num == 4:
            geometry = packed(payload)

    return kind, rings(geometry)


def rings(commands):
    """Walks the command stream into rings of (x, y) in tile coordinates."""
    out, current = [], []
    x = y = i = 0

    while i < len(commands):
        header = commands[i]
        i += 1
        command, count = header & 0x7, header >> 3

        if command == 1:                      # MoveTo
            for _ in range(count):
                if current:
                    out.append(current)
                    current = []
                x += zigzag(commands[i])
                y += zigzag(commands[i + 1])
                i += 2
                current = [(x, y)]
        elif command == 2:                    # LineTo
            for _ in range(count):
                x += zigzag(commands[i])
                y += zigzag(commands[i + 1])
                i += 2
                current.append((x, y))
        elif command == 7:                    # ClosePath
            if current:
                current.append(current[0])
                out.append(current)
                current = []
        else:
            break

    if current:
        out.append(current)

    return out


def zigzag(n):
    return (n >> 1) ^ (-(n & 1))


# ---------------------------------------------------------------- source


def psql(sql):
    result = subprocess.run(
        ["docker", "exec", "gis-experiment-postgis", "psql", "-U", "gis", "-d", "gis", "-tAc", sql],
        capture_output=True, text=True)
    if result.returncode:
        raise SystemExit(result.stderr.strip())
    return result.stdout


def tile_bounds(z, x, y):
    size = (WORLD * 2) / (1 << z)
    minx = -WORLD + x * size
    maxy = WORLD - y * size
    return minx, maxy - size, minx + size, maxy


# ---------------------------------------------------------------- draw

SIZE = 720
INK = (24, 28, 32)
FILL = (86, 140, 170)
EDGE = (28, 58, 82)


def panel(title, draw_rings, count):
    """Outlines, not fills.

    The first render filled every polygon and produced two identical solid
    rectangles: the Marmara sea feature covers the whole tile and was painted
    over every building in it. That is finding 11 showing up as a picture — one
    enormous geometry overlapping a tile that wants to show a city block — and
    it made the comparison useless in exactly the way it was meant to prevent.
    """
    image = Image.new("RGB", (SIZE, SIZE + 34), (250, 249, 246))
    draw = ImageDraw.Draw(image)
    draw.rectangle([0, 0, SIZE, 33], fill=INK)
    draw.text((10, 11), f"{title}  —  {count} features", fill=(240, 240, 235))

    # Largest first, so a huge background polygon cannot hide what is on top of

    ordered = sorted(draw_rings, key=shoelace, reverse=True)

    for i, ring in enumerate(ordered):
        if len(ring) < 3:
            continue
        # The biggest few get a pale wash so coverage is visible; everything
        # else is outline only, which is what makes 200 buildings legible.
        if i < 3 and shoelace(ring) > 0.25 * SIZE * SIZE:
            draw.polygon([(px, py + 34) for px, py in ring], fill=(228, 236, 241))
        draw.line([(px, py + 34) for px, py in ring], fill=EDGE, width=1)

    draw.rectangle([0, 34, SIZE - 1, SIZE + 33], outline=(170, 166, 158))
    return image


def shoelace(ring):
    total = 0.0
    for i in range(len(ring) - 1):
        total += ring[i][0] * ring[i + 1][1] - ring[i + 1][0] * ring[i][1]
    return abs(total) / 2.0


def main():
    argv = sys.argv[1:]
    out = "tile.png"
    if "-o" in argv:
        at = argv.index("-o")
        out = argv[at + 1]
        # Remove BOTH the flag and its value. Leaving the value behind made the
        # positional list four long, so the tile arguments were silently ignored
        # and a different tile was rendered than the one asked for.
        del argv[at:at + 2]

    numbers = [a for a in argv if a.lstrip("-").isdigit()]
    z, x, y = (int(v) for v in (numbers if len(numbers) == 3 else ["16", "38030", "24562"]))

    minx, miny, maxx, maxy = tile_bounds(z, x, y)
    print(f"z{z}/{x}/{y}   bounds {minx:.0f} {miny:.0f} {maxx:.0f} {maxy:.0f}")

    # ---- left: what ST_AsMVT produced, decoded here
    hexed = psql(f"""
        WITH bounds AS (SELECT ST_TileEnvelope({z},{x},{y}) AS geom),
        mvtgeom AS (
            SELECT ST_AsMVTGeom(t.way, bounds.geom, 4096, 64, true) AS geom, t.osm_id
            FROM planet_osm_polygon t, bounds WHERE t.way && bounds.geom
        )
        SELECT encode(ST_AsMVT(mvtgeom.*, 'polygons', 4096, 'geom'), 'hex') FROM mvtgeom
        """).strip()

    tile = bytes.fromhex(hexed)
    layers = decode(tile)
    print(f"decoded {len(tile):,} bytes, layers: "
          + ", ".join(f"{n} ({len(f)} features, extent {e})" for n, (e, f) in layers.items()))

    scale = SIZE / 4096.0
    left_rings, kinds = [], {}

    for extent, features in layers.values():
        for kind, geom in features:
            kinds[kind] = kinds.get(kind, 0) + 1
            for ring in geom:
                # Tile space only. The y axis already points down in MVT, which
                # is the same direction as image space — so no flip here, and if
                # one were needed the panels would disagree and say so.
                left_rings.append([(px * SIZE / extent, py * SIZE / extent) for px, py in ring])

    left_count = sum(len(f) for _, f in layers.values())

    # ---- right: the same extent from source, projected by hand
    rows = psql(f"""
        SELECT ST_AsText(ST_Intersection(
                   ST_MakeValid(way),
                   ST_MakeEnvelope({minx},{miny},{maxx},{maxy},3857)))
        FROM planet_osm_polygon
        WHERE way && ST_MakeEnvelope({minx},{miny},{maxx},{maxy},3857)
        """).strip().splitlines()

    right_rings = []
    width = maxx - minx
    height = maxy - miny

    for wkt in rows:
        for chunk in wkt.replace("MULTIPOLYGON", "").replace("POLYGON", "").split("(("):
            pts = []
            for pair in chunk.split(")")[0].split(","):
                bits = pair.strip().split()
                if len(bits) >= 2:
                    try:
                        mx, my = float(bits[0]), float(bits[1])
                    except ValueError:
                        continue
                    pts.append(((mx - minx) / width * SIZE, (maxy - my) / height * SIZE))
            if len(pts) >= 3:
                right_rings.append(pts)

    print(f"source: {len(rows)} geometries, {len(right_rings)} rings")
    print(f"geometry types in tile: {kinds}  (3 = polygon)")

    canvas = Image.new("RGB", (SIZE * 2 + 12, SIZE + 34), (210, 206, 198))
    canvas.paste(panel(f"ST_AsMVT, decoded  z{z}/{x}/{y}", left_rings, left_count), (0, 0))
    canvas.paste(panel("same extent, straight from PostGIS", right_rings, len(rows)), (SIZE + 12, 0))
    canvas.save(out)
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
