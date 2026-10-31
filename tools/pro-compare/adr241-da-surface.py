# A comparison with ArcGIS Pro 3.7 (ADR-241 condition 1, D-336, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
"""Distance accumulation over a surface, and with a cost or a factor, in ArcGIS Pro 3.7 and grapy.

    D:/temp/grapy-env/Scripts/python.exe                                   adr241-da-surface.py make  <folder>
    "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr241-da-surface.py run   <folder> pro
    D:/temp/grapy-env/Scripts/python.exe                                   adr241-da-surface.py run   <folder> grapy <grapy src>
    D:/temp/grapy-env/Scripts/python.exe                                   adr241-da-surface.py compare <folder>

``make`` writes one source at the centre of a 41 x 41 grid of 1 m cells and five surfaces (two tilted planes, a steep
one, a bowl and a ridge), a random cost and a constant 45° horizontal raster; ``run`` saves each case's accumulation
under <folder>/<side>; ``compare`` prints the largest and mean difference of grapy from Pro, and, for the planes, of
each from the exact surface distance. On 2026-10-31 the five surfaces alone agreed to 1e-4 after grapy took Pro's
update — the eight triangles of an edge and a diagonal neighbour, each step's length measured over the surface — and
the cases with a cost or a factor did not (D-336).
"""
import os
import sys

mode, folder = sys.argv[1], sys.argv[2]
N = 41
SURFACES = ("plane", "plane2", "steep", "bowl", "ridge")

if mode == "make":
    import numpy as np
    import rasterio
    from rasterio.transform import from_origin
    os.makedirs(folder, exist_ok=True)
    prof = dict(driver="GTiff", width=N, height=N, count=1, crs="EPSG:3857", transform=from_origin(0, N, 1, 1))
    src = np.full((N, N), -9999, np.int32)
    src[20, 20] = 1
    with rasterio.open(os.path.join(folder, "src.tif"), "w", dtype="int32", nodata=-9999, **prof) as w:
        w.write(src, 1)
    x = np.tile(np.arange(N, dtype=np.float64), (N, 1))
    y = np.arange(N, dtype=np.float64)[:, None] * np.ones((1, N))
    rasters = {"plane": 0.5 * x, "plane2": 0.5 * x + 0.3 * y, "steep": 2.0 * x,
               "bowl": 0.02 * ((x - 20) ** 2 + (y - 20) ** 2), "ridge": 5.0 * np.abs(np.sin(x / 6.0)),
               "cost": np.random.default_rng(7).uniform(0.5, 3.0, (N, N)), "wind": np.full((N, N), 45.0)}
    for name, z in rasters.items():
        with rasterio.open(os.path.join(folder, f"{name}.tif"), "w", dtype="float32", **prof) as w:
            w.write(z.astype(np.float32), 1)

elif mode == "run":
    side = sys.argv[3]
    if side == "grapy":
        sys.path.insert(0, sys.argv[4])
        import grapy as arcpy
    else:
        import arcpy
        arcpy.CheckOutExtension("Spatial")
    sa = arcpy.sa
    out = os.path.join(folder, side)
    os.makedirs(out, exist_ok=True)
    arcpy.env.overwriteOutput = True

    def R(name):
        return arcpy.Raster(os.path.join(folder, name + ".tif"))
    cases = {k: (lambda k=k: sa.DistanceAccumulation(R("src"), in_surface_raster=R(k))) for k in SURFACES}
    cases.update({
        "cost": lambda: sa.DistanceAccumulation(R("src"), in_cost_raster=R("cost")),
        "cost_surf": lambda: sa.DistanceAccumulation(R("src"), in_surface_raster=R("bowl"), in_cost_raster=R("cost")),
        "vf_lin": lambda: sa.DistanceAccumulation(R("src"), in_vertical_raster=R("plane"),
                                                  vertical_factor=sa.VfLinear(1, -90, 90, 1.0 / 90)),
        "hf_lin": lambda: sa.DistanceAccumulation(R("src"), in_horizontal_raster=R("wind"),
                                                  horizontal_factor=sa.HfLinear(0.5, 181, 1.0 / 90)),
    })
    for k, f in cases.items():
        try:
            f().save(os.path.join(out, k + ".tif"))
            print(k, "ok", flush=True)
        except Exception as e:  # noqa: BLE001 — the message is the observation
            print(k, "ERR", str(e)[:200], flush=True)

else:
    import numpy as np
    import rasterio
    r, c = np.indices((N, N))
    dx, dy = (c - 20).astype(float), (20 - r).astype(float)
    exact = {"plane": np.sqrt(dx ** 2 + dy ** 2 + (0.5 * dx) ** 2),
             "plane2": np.sqrt(dx ** 2 + dy ** 2 + (0.5 * dx - 0.3 * dy) ** 2),
             "steep": np.sqrt(dx ** 2 + dy ** 2 + (2.0 * dx) ** 2)}
    for k in (*SURFACES, "cost", "cost_surf", "vf_lin", "hf_lin"):
        p = rasterio.open(os.path.join(folder, "pro", k + ".tif")).read(1).astype(float)
        g = rasterio.open(os.path.join(folder, "grapy", k + ".tif")).read(1).astype(float)
        line = f"{k:10s} grapy-pro max {abs(g - p).max():.4f} ({abs(g - p).max() / p.max() * 100:.3f} %) mean {abs(g - p).mean():.5f}"
        if k in exact:
            line += f" | pro-exact max {abs(p - exact[k]).max():.4f}, grapy-exact max {abs(g - exact[k]).max():.4f}"
        print(line)
