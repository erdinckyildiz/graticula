"""D-413: what ArcGIS Pro 3.7 answers downstream of a flow-direction loop, and grapy beside it.

    "<Pro python>"   d413-loop.py pro   <out dir>
    "<grapy python>" d413-loop.py grapy <out dir>      (the worktree's src/grapy is put on the path)
    "<grapy python>" d413-loop.py compare <out dir>

The sweep's shared DEM (fixtures.dem_value) has a pit that Flow Direction writes as two cells pointing at each other.
The ``pro`` side builds the DEM, saves Pro's flow direction as ``fd.tif`` and runs FlowAccumulation, FlowLength
(both directions), StreamLink, StreamOrder (STRAHLER and SHREVE), SnapPourPoint and Watershed on it; the ``grapy`` side
runs the same tools on *Pro's* ``fd.tif``, so a difference is the tool's, not Flow Direction's (D-413's one other
cell). ``compare`` prints, per tool, the cells that differ and the values around the loop.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "sweep"))

TOOLS = ("acc", "len_down", "len_up", "link", "strahler", "shreve", "snap", "shed")


def side(name, out):
    import numpy as np
    if name == "pro":
        import arcpy as ap
    else:
        sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(HERE)), "src", "grapy"))
        import grapy as ap
    import fixtures
    os.makedirs(out, exist_ok=True)
    ap.env.overwriteOutput = True
    work = os.path.join(out, f"_{name}")
    os.makedirs(work, exist_ok=True)
    sr = ap.SpatialReference(fixtures.SRID)
    fd_path = os.path.join(out, "fd.tif")
    if name == "pro":
        inputs = fixtures.build(ap, work)
        ap.sa.FlowDirection(inputs["dem"]).save(fd_path)
    fd = ap.Raster(fd_path)
    ap.env.outputCoordinateSystem = sr
    acc = ap.sa.FlowAccumulation(fd)
    results = {"acc": acc, "len_down": ap.sa.FlowLength(fd, "DOWNSTREAM"), "len_up": ap.sa.FlowLength(fd, "UPSTREAM")}
    streams = ap.sa.Con(acc > 20, 1)
    results["link"] = ap.sa.StreamLink(streams, fd)
    results["strahler"] = ap.sa.StreamOrder(streams, fd, "STRAHLER")
    results["shreve"] = ap.sa.StreamOrder(streams, fd, "SHREVE")
    # A pour point on the loop's cells, so Watershed answers what drains into the pit.
    pour = ap.management.CreateFeatureclass(work, "pour.shp" if name == "pro" else "pour.gpkg", "POINT",
                                            spatial_reference=sr)[0]
    ap.management.AddField(pour, "pid", "LONG")
    with ap.da.InsertCursor(pour, ["pid", "SHAPE@XY"]) as cursor:
        cursor.insertRow((1, (fixtures.X0 + 455.0, fixtures.Y0 + 155.0)))
    results["snap"] = ap.sa.SnapPourPoint(pour, acc, 15, "pid")
    results["shed"] = ap.sa.Watershed(fd, results["snap"])
    for key, raster in results.items():
        try:
            array = ap.RasterToNumPyArray(raster).astype("float64")
            nd = raster.noDataValue
            if nd is not None:
                array = np.where(array == float(nd), np.nan, array)
            np.save(os.path.join(out, f"{name}_{key}.npy"), array)
            print(name, key, "ok")
        except Exception as error:  # noqa: BLE001 — a refusal is part of the answer
            print(name, key, type(error).__name__, str(error)[:200])


def compare(out):
    import numpy as np
    import rasterio
    with rasterio.open(os.path.join(out, "fd.tif")) as src:
        fd = src.read(1)
    codes = {1: (0, 1), 2: (1, 1), 4: (1, 0), 8: (1, -1), 16: (0, -1), 32: (-1, -1), 64: (-1, 0), 128: (-1, 1)}
    loop = []
    for r in range(fd.shape[0]):
        for c in range(fd.shape[1]):
            d = codes.get(int(fd[r, c]))
            if d is None:
                continue
            rr, cc = r + d[0], c + d[1]
            if 0 <= rr < fd.shape[0] and 0 <= cc < fd.shape[1]:
                back = codes.get(int(fd[rr, cc]))
                if back and (rr + back[0], cc + back[1]) == (r, c):
                    loop.append((r, c))
    print("loop cells (row, col):", loop)
    for key in TOOLS:
        a_path, b_path = os.path.join(out, f"pro_{key}.npy"), os.path.join(out, f"grapy_{key}.npy")
        if not os.path.exists(a_path) or not os.path.exists(b_path):
            print(f"{key:9} missing on {'Pro' if not os.path.exists(a_path) else 'grapy'}'s side")
            continue
        a, b = np.load(a_path), np.load(b_path)
        same = (np.isnan(a) & np.isnan(b)) | np.isclose(a, b, rtol=1e-6, atol=1e-6)
        print(f"{key:9} differ in {int((~same).sum())} of {a.size}; at the loop Pro "
              f"{[a[p] for p in loop]} grapy {[b[p] for p in loop]}")
        for r, c in list(zip(*np.nonzero(~same)))[:5]:
            print(f"{'':11}({r}, {c}) Pro {a[r, c]} grapy {b[r, c]}")


if __name__ == "__main__":
    if sys.argv[1] == "compare":
        compare(sys.argv[2])
    else:
        side(sys.argv[1], sys.argv[2])
