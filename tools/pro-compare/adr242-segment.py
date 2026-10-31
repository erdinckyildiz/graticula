"""ADR-242 condition 3: Segment Mean Shift on the same image in ArcGIS Pro and grapy. A tool, not a test: it needs
ArcGIS Pro and never runs in CI.

    <Pro python>    adr242-segment.py inputs  <folder>
    <Pro python>    adr242-segment.py pro     <folder> [spectral spatial minimum]
    <grapy python>  adr242-segment.py grapy   <folder> <worktree> [spectral spatial minimum]
    <grapy python>  adr242-segment.py compare <folder> <worktree> [spectral spatial minimum]

The image is synthetic, 3-band 8-bit, 160 x 160: a field of patches with soft edges, a gradient, a disc and noise.
"""
import os
import sys
import time

import numpy as np

step, folder = sys.argv[1], sys.argv[2]
rest = sys.argv[4:] if step in ("grapy", "compare") else sys.argv[3:]
params = tuple(float(v) for v in rest[:3]) if len(rest) >= 3 else (15.5, 15, 20)
tag = "_".join(f"{v:g}" for v in params)


def image():
    rng = np.random.default_rng(242)
    n = 160
    r, k = np.indices((n, n), dtype=float)
    out = np.zeros((n, n, 3))
    palette = rng.integers(20, 235, size=(16, 3))
    patch = ((r // 40) * 4 + (k // 40)).astype(int)
    out[:] = palette[patch]
    out[..., 0] += 0.25 * k  # a gradient in red
    disc = (r - 70) ** 2 + (k - 95) ** 2 < 28 ** 2
    out[disc] = (230, 210, 40)
    out += rng.normal(0, 6, out.shape)
    return np.clip(out, 0, 255).astype(np.uint8)


if step == "inputs":
    import arcpy
    arcpy.env.overwriteOutput = True
    os.makedirs(folder, exist_ok=True)
    a = image()
    bands = []
    for b in range(3):
        path = os.path.join(folder, f"band{b + 1}.tif")
        arcpy.NumPyArrayToRaster(np.ascontiguousarray(a[..., b]), arcpy.Point(0, 0), 1, 1).save(path)
        arcpy.management.DefineProjection(path, arcpy.SpatialReference(3857))
        bands.append(path)
    arcpy.management.CompositeBands(";".join(bands), os.path.join(folder, "image.tif"))
    r = arcpy.Raster(os.path.join(folder, "image.tif"))
    print(r.bandCount, r.pixelType, r.width, r.height)
    sys.exit(0)

out = os.path.join(folder, f"{{}}_seg_{tag}.tif")
if step in ("pro", "grapy"):
    if step == "grapy":
        sys.path.insert(0, os.path.join(sys.argv[3], "src", "grapy"))
        import grapy as arcpy
    else:
        import arcpy
        arcpy.CheckOutExtension("Spatial")
    arcpy.env.overwriteOutput = True
    t = time.perf_counter()
    arcpy.sa.SegmentMeanShift(os.path.join(folder, "image.tif"), *params).save(out.format(step))
    print(f"{step} {tag}: {time.perf_counter() - t:.1f} s")
    sys.exit(0)

sys.path.insert(0, os.path.join(sys.argv[3], "src", "grapy"))
import grapy as arcpy  # noqa: E402


def bands(path):
    raster = arcpy.Raster(path)
    return np.stack([arcpy.RasterToNumPyArray(raster.getRasterBands(b + 1)).astype(int) for b in range(3)], -1)


def labels(colours):
    """Segments as 4-connected regions of one colour."""
    from scipy import ndimage
    key = colours[..., 0] * 65536 + colours[..., 1] * 256 + colours[..., 2]
    out = np.zeros(key.shape, int)
    count = 0
    for value in np.unique(key):
        lab, n = ndimage.label(key == value)
        out[lab > 0] = lab[lab > 0] + count
        count += n
    return out, count


def rand_index(a, b):
    """The adjusted Rand index of two labellings (Hubert and Arabie 1985)."""
    _, ai = np.unique(a.ravel(), return_inverse=True)
    _, bi = np.unique(b.ravel(), return_inverse=True)
    table = np.zeros((ai.max() + 1, bi.max() + 1))
    np.add.at(table, (ai, bi), 1)
    comb = lambda x: x * (x - 1) / 2  # noqa: E731
    index = comb(table).sum()
    rows, cols, total = comb(table.sum(1)).sum(), comb(table.sum(0)).sum(), comb(a.size)
    expected = rows * cols / total
    return (index - expected) / ((rows + cols) / 2 - expected)


pro, ours = bands(out.format("pro")), bands(out.format("grapy"))
truth = image().astype(int)
lp, np_ = labels(pro)
lg, ng = labels(ours)
d = np.abs(pro - ours)
print(f"params {tag}: segments Pro {np_}, grapy {ng}; adjusted Rand {rand_index(lp, lg):.4f}; "
      f"colour |d| max {d.max()} mean {d.mean():.2f}; |Pro - image| mean {np.abs(pro - truth).mean():.2f}, "
      f"|grapy - image| mean {np.abs(ours - truth).mean():.2f}")
