#!/usr/bin/env python3
"""Converts real 3D models, made by real tools, with the converters a 3D object layer uses — ADR-282 condition 2.

Usage:  python tools/real-models.py [--only fbx,ifc,usd]

Each model is downloaded from a pinned commit of a public repository, refused unless its SHA-256 is the one below,
converted by the same command line `ModelConverters` runs (assimp, IfcConvert, usd2glb, found on the path), and its
glb read back: the number of triangles and the size of the box it fills, which must be what the model is. A model that
is refused must be refused, with the converter's own reason. Nothing downloaded is kept or committed: these are other
people's models, under the licences named beside them, used here only to be converted and measured.

Why the models are fetched rather than committed: the corpus under tests/ holds only what this project wrote. The
files are named by commit and hash, so a run measures the same bytes every time or fails.
"""

import hashlib
import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import time
import urllib.request

# (format, name, url at a pinned commit, sha256, made by, licence, expected: None to be refused, else
#  (minimum triangles, (low, high) of the largest side of its box in the glb's units), or ("either", ...) when converting
#  and being refused with a reason are both an answer)
MODELS = [
    ("fbx", "maxPbrMaterial_metalRough.fbx",
     "https://raw.githubusercontent.com/assimp/assimp/f7be3048de830c5b3567c23a04c3746079bfcb67/test/models/FBX/maxPbrMaterial_metalRough.fbx",
     "4b259489329f8dae292faae826f319e26bbd70a9fe91ac95cc6f9691c3b34b2e",
     "Autodesk 3ds Max 2021, FBX SDK 2020.0.1 (ASCII FBX 7.7)", "BSD-3-Clause (Assimp's repository)",
     ("either", 12, (15.0, 30.0))),
    ("fbx", "spider.fbx",
     "https://raw.githubusercontent.com/assimp/assimp/dcfc696e86736a3b869a3efe0e64e09f13609040/test/models/FBX/spider.fbx",
     "37ca4979dec31f5fbf41eaac2594918d56459cad3461e4b35425396142d4fe0a",
     "Blender 2.76 (binary FBX)", "BSD-3-Clause (Assimp's repository)",
     # Either: ADR-282 §4 saw Debian's Assimp 5.2.5 fail on a binary FBX, and Assimp 6.0.6 converts this one (measured
     # 2026-11-20); which the image's packaged Assimp does is what this run is for, and is printed.
     ("either", 1000, (1.0, 1e6))),
    ("ifc", "hhs-construction.ifc",
     "https://raw.githubusercontent.com/opensourceBIM/IFC-files/cb96c50f511248cf0df7a16fd4bef0c89a3159f0/HHS%20Office/construction.ifc",
     "37917ba4dc9ee168fccbd0752a85c7cc927a19541edabb35d436c8511b936c05",
     "Autodesk Revit Architecture 2010 (IFC2x3, an office building)", "CC BY-ND 4.0 (opensourceBIM/IFC-files)",
     (10000, (50.0, 90.0))),
    ("usd", "CarbonFrameBike.usdz",
     "https://raw.githubusercontent.com/usd-wg/assets/091bb5c6f8eb665ad4cf9d6c10025cecc07eda88/full_assets/CarbonFrameBike/CarbonFrameBike.usdz",
     "c6ab6be330401744f6d867e0f32c9f4e020aefcea66b06e2c3bf731a46e3f762",
     "A production bicycle for Apple's AR Quick Look, by Robert Schweier and prefrontal cortex", "CC BY-SA 4.0 (usd-wg/assets)",
     (100000, (1.5, 2.5))),
    ("usd", "McUsd.usdz",
     "https://raw.githubusercontent.com/usd-wg/assets/091bb5c6f8eb665ad4cf9d6c10025cecc07eda88/full_assets/McUsd/McUsd.usdz",
     "6f3ca1c4f27b135124fda81ccb0662f3616ce390119e42b63173d491fdd8c308",
     "Mineways (a Minecraft scene exported as USD)", "CC BY-NC-SA 4.0 (usd-wg/assets)",
     None),
]

# The command lines of src/Graticula.Host/ModelConverters.cs.
COMMANDS = {
    "fbx": lambda i, o: [os.environ.get("ASSIMP", "assimp"), "export", i, o, "-fglb2"],
    "ifc": lambda i, o: [os.environ.get("IFCCONVERT", "IfcConvert"), "-y", "-q", i, o],
    "usd": lambda i, o: [os.environ.get("USD2GLB", "usd2glb"), i, o],
}


def glb_box(path):
    """Triangles and the size of the box a glb fills, node transforms applied (glTF 2.0)."""
    data = open(path, "rb").read()
    magic, _, length = struct.unpack_from("<4sII", data, 0)
    if magic != b"glTF":
        raise ValueError("not a glb")
    off, gltf, binary = 12, None, b""
    while off < length:
        size, kind = struct.unpack_from("<II", data, off)
        chunk = data[off + 8: off + 8 + size]
        if kind == 0x4E4F534A:
            gltf = json.loads(chunk)
        elif kind == 0x004E4942:
            binary = chunk
        off += 8 + size

    def mul(a, b):
        return [sum(a[r + 4 * k] * b[k + 4 * c] for k in range(4)) for c in range(4) for r in range(4)]

    def local(n):
        if "matrix" in n:
            return n["matrix"]
        t, (x, y, z, w), s = n.get("translation", [0, 0, 0]), n.get("rotation", [0, 0, 0, 1]), n.get("scale", [1, 1, 1])
        r = [1 - 2 * (y * y + z * z), 2 * (x * y + z * w), 2 * (x * z - y * w), 0,
             2 * (x * y - z * w), 1 - 2 * (x * x + z * z), 2 * (y * z + x * w), 0,
             2 * (x * z + y * w), 2 * (y * z - x * w), 1 - 2 * (x * x + y * y), 0, 0, 0, 0, 1]
        return mul([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, t[0], t[1], t[2], 1], mul(r, [s[0], 0, 0, 0, 0, s[1], 0, 0, 0, 0, s[2], 0, 0, 0, 0, 1]))

    acc, views = gltf["accessors"], gltf["bufferViews"]
    lo, hi, triangles = [float("inf")] * 3, [float("-inf")] * 3, 0

    def visit(i, parent):
        nonlocal triangles
        n = gltf["nodes"][i]
        m = mul(parent, local(n))
        for p in gltf["meshes"][n["mesh"]]["primitives"] if "mesh" in n else []:
            a = acc[p["attributes"]["POSITION"]]
            v = views[a["bufferView"]]
            stride, base = v.get("byteStride", 12), v.get("byteOffset", 0) + a.get("byteOffset", 0)
            triangles += (acc[p["indices"]]["count"] if "indices" in p else a["count"]) // 3 if p.get("mode", 4) == 4 else 0
            for k in range(a["count"]):
                x, y, z = struct.unpack_from("<fff", binary, base + k * stride)
                w = (m[0] * x + m[4] * y + m[8] * z + m[12], m[1] * x + m[5] * y + m[9] * z + m[13], m[2] * x + m[6] * y + m[10] * z + m[14])
                for j in range(3):
                    lo[j], hi[j] = min(lo[j], w[j]), max(hi[j], w[j])
        for c in n.get("children", []):
            visit(c, m)

    for i in gltf["scenes"][gltf.get("scene", 0)]["nodes"]:
        visit(i, [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1])
    return triangles, [hi[j] - lo[j] for j in range(3)]


def main():
    only = set(sys.argv[sys.argv.index("--only") + 1].split(",")) if "--only" in sys.argv else {"fbx", "ifc", "usd"}
    failed = 0
    work = tempfile.mkdtemp(prefix="real-models-")
    try:
        for kind, name, url, sha, made, licence, expected in MODELS:
            if kind not in only:
                continue
            path = os.path.join(work, name)
            with urllib.request.urlopen(url, timeout=120) as answer, open(path, "wb") as out:
                shutil.copyfileobj(answer, out)
            if hashlib.sha256(open(path, "rb").read()).hexdigest() != sha:
                print(f"FAIL {name}: not the pinned file")
                failed += 1
                continue
            glb = path + ".glb"
            start = time.perf_counter()
            run = subprocess.run(COMMANDS[kind](path, glb), capture_output=True, text=True, timeout=90, check=False)
            took = (time.perf_counter() - start) * 1000
            said = (run.stdout + "\n" + run.stderr).strip().splitlines()
            reason = next((l for l in said if l.startswith("The ")), said[-1] if said else "")
            if expected is None:
                ok = run.returncode != 0 and not os.path.exists(glb)
                print(f"{'ok  ' if ok else 'FAIL'} {name} ({made}; {licence}): refused in {took:.0f} ms: {reason[:200]}")
            elif run.returncode != 0 or not os.path.exists(glb):
                ok = expected[0] == "either" and bool(reason)
                print(f"{'ok  ' if ok else 'FAIL'} {name} ({made}): refused, exit {run.returncode}: {reason[:300]}")
            else:
                expected = expected[1:] if expected[0] == "either" else expected
                triangles, size = glb_box(glb)
                low, high = expected[1]
                ok = triangles >= expected[0] and low <= max(size) <= high
                print(f"{'ok  ' if ok else 'FAIL'} {name} ({made}; {licence}): {os.path.getsize(path):,} bytes -> "
                      f"{os.path.getsize(glb):,} bytes glb in {took:.0f} ms, {triangles:,} triangles, "
                      f"box {size[0]:.2f} x {size[1]:.2f} x {size[2]:.2f}")
            failed += 0 if ok else 1
    finally:
        shutil.rmtree(work, ignore_errors=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
