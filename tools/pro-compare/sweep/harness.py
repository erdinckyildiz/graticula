"""Run one module's comparison cases under ArcGIS Pro's arcpy or under grapy, and dump every output to a neutral form.

    "<Pro python>"   harness.py pro   <module> <out dir> [case ...]
    "<grapy python>" harness.py grapy <module> <out dir> [case ...]   (the worktree's src/grapy is put on the path)

A tool, not a test: it needs ArcGIS Pro for one side and is not run in CI. The same case file runs on both sides:
each case is ``run(ap, I, O)`` with ``ap`` the arcpy module (Pro's or grapy), ``I`` the inputs (fixtures.py) and
``O(name, kind)`` an output path. What a case answers or writes is dumped by ``dump`` to ``<out>/<case>/``:

- a feature class or table: ``<name>.json`` — its fields (name, type) and its rows, the shape as WKT;
- a raster: ``<name>.npy`` (cells, NoData as NaN) and ``<name>.json`` (extent, cell size, pixel type, bands);
- anything else a tool answers: ``result.json``, ``str()`` of each output.

Errors are recorded, not raised: ``<case>/error.json`` holds the exception's type and message. compare.py reads two
such folders.
"""
import importlib
import json
import math
import os
import sys
import time
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)


def load(side):
    if side == "pro":
        import arcpy
        return arcpy
    root = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
    sys.path.insert(0, os.path.join(root, "src", "grapy"))
    import grapy
    return grapy


def _wkt(shape):
    if shape is None:
        return None
    try:
        return shape.WKT
    except Exception:  # noqa: BLE001 — a shape Pro cannot write as WKT is recorded as such
        return "<no WKT>"


def _value(v):
    if v is None or isinstance(v, (bool, int, str)):
        return v
    if isinstance(v, float):
        return v if math.isfinite(v) else None
    return str(v)


def _patch_vertices(shape):
    """A multipatch as the MULTIPOINT Z of its distinct vertices, rounded to the millimetre: Pro cannot write a
    multipatch as WKT (SHAPE@WKT raises "Internal error") and stores faces as triangle lists where grapy keeps rings,
    so the vertices are what both sides can be compared on (added by the ddd sweep, 2026-11-08)."""
    if shape is None:
        return None
    seen = set()
    for part in shape:
        for p in part:
            if p is not None:
                seen.add((round(p.X, 3), round(p.Y, 3), round(p.Z if p.Z is not None else 0.0, 3)))
    if not seen:
        return None
    return "MULTIPOINT Z (" + ", ".join(f"({x} {y} {z})" for x, y, z in sorted(seen)) + ")"


def dump_features(ap, path, out):
    desc = ap.Describe(path)
    fields = [(f.name, f.type) for f in ap.ListFields(path)
              if f.type not in ("Geometry", "OID", "Raster", "Blob", "GlobalID") and f.name.lower() not in ("shape_length",
                                                                                               "shape_area")]
    has_shape = getattr(desc, "shapeType", None) not in (None, "", "Table") and getattr(desc, "dataType", "") != "Table"
    patch = getattr(desc, "shapeType", None) == "MultiPatch"
    names = [n for n, _ in fields] + ((["SHAPE@"] if patch else ["SHAPE@WKT"]) if has_shape else [])
    rows = []
    with ap.da.SearchCursor(path, names) as cursor:
        for row in cursor:
            row = list(row)
            if patch:
                row[-1] = _patch_vertices(row[-1])
            rows.append([_value(v) for v in row])
    srid = None
    try:
        srid = desc.spatialReference.factoryCode
    except Exception:  # noqa: BLE001
        pass
    json.dump({"kind": "features", "shapeType": getattr(desc, "shapeType", None), "srid": srid, "fields": fields,
               "rows": rows, "shape": has_shape}, open(out + ".json", "w", encoding="utf-8"))


def dump_raster(ap, path, out):
    import numpy as np
    raster = ap.Raster(path) if not hasattr(path, "extent") else path
    bands = int(getattr(raster, "bandCount", 1) or 1)
    array = ap.RasterToNumPyArray(raster).astype("float64")
    nd = raster.noDataValue
    if nd is not None:
        array = np.where(array == float(nd), np.nan, array)
    np.save(out + ".npy", array)
    e = raster.extent
    json.dump({"kind": "raster", "extent": [e.XMin, e.YMin, e.XMax, e.YMax], "cell": [raster.meanCellWidth,
               raster.meanCellHeight], "pixelType": raster.pixelType, "bands": bands, "isInteger": raster.isInteger,
               "srid": raster.spatialReference.factoryCode if raster.spatialReference else None},
              open(out + ".json", "w", encoding="utf-8"))


def dump(ap, path, out):
    """Dump whatever ``path`` is: a raster, a feature class or table, or a file (copied as text)."""
    text = str(path)
    if hasattr(path, "extent") and hasattr(path, "meanCellWidth"):
        return dump_raster(ap, path, out)
    if text.lower().endswith((".tif", ".tiff", ".img", ".crf")):
        return dump_raster(ap, text, out)
    if not ap.Exists(text):
        if os.path.isfile(text):
            json.dump({"kind": "file", "text": open(text, encoding="utf-8", errors="replace").read()[:200000]},
                      open(out + ".json", "w", encoding="utf-8"))
            return
        raise FileNotFoundError(f"output '{text}' does not exist")
    kind = ap.Describe(text).dataType
    if kind in ("RasterDataset", "RasterBand", "MosaicDataset"):
        return dump_raster(ap, text, out)
    return dump_features(ap, text, out)


def main():
    side, module, out_root = sys.argv[1], sys.argv[2], sys.argv[3]
    wanted = set(sys.argv[4:])
    ap = load(side)
    import fixtures
    cases = importlib.import_module(f"cases.{module}")
    os.makedirs(out_root, exist_ok=True)
    work = os.path.join(out_root, "_work")
    os.makedirs(work, exist_ok=True)
    ap.env.overwriteOutput = True
    inputs = fixtures.build(ap, work)
    ap.env.workspace = work
    summary = {}
    for name, case in cases.CASES.items():
        if wanted and name not in wanted:
            continue
        folder = os.path.join(out_root, name.replace("/", "_"))
        os.makedirs(folder, exist_ok=True)
        for old in os.listdir(folder):
            os.remove(os.path.join(folder, old))
        made = {}

        def O(output, kind="features", _made=made, _case=name):
            safe = (_case.split(".")[-1] + "_" + output).replace(" ", "_")
            if kind == "raster":
                path = os.path.join(work, safe + ".tif")
            elif kind == "file":
                path = os.path.join(work, safe + ".txt")
            elif kind == "folder":
                path = os.path.join(work, safe)
                os.makedirs(path, exist_ok=True)
            else:
                path = os.path.join(inputs["gdb"], safe)
            _made[output] = path
            return path

        started = time.time()
        try:
            for env_name in ("extent", "mask", "cellSize", "snapRaster", "outputCoordinateSystem"):
                try:
                    setattr(ap.env, env_name, None)
                except Exception:  # noqa: BLE001 — an environment one side does not hold
                    pass
            answer = case(ap, inputs, O)
            if answer is not None and hasattr(answer, "extent") and hasattr(answer, "meanCellWidth"):
                dump_raster(ap, answer, os.path.join(folder, "answer"))
            elif answer is not None and hasattr(answer, "getOutput"):
                json.dump([str(answer.getOutput(i)) for i in range(answer.outputCount)],
                          open(os.path.join(folder, "result.json"), "w", encoding="utf-8"))
            elif answer is not None:
                json.dump(_value(answer) if not isinstance(answer, (list, tuple, dict)) else
                          json.loads(json.dumps(answer, default=str)),
                          open(os.path.join(folder, "result.json"), "w", encoding="utf-8"))
            for output, path in made.items():
                if os.path.isdir(path):
                    continue
                dump(ap, path, os.path.join(folder, output))
            summary[name] = {"ok": True, "seconds": round(time.time() - started, 2)}
        except Exception as error:  # noqa: BLE001 — a side's refusal is part of the comparison
            json.dump({"type": type(error).__name__, "message": str(error)[:4000],
                       "trace": traceback.format_exc()[-4000:]},
                      open(os.path.join(folder, "error.json"), "w", encoding="utf-8"))
            summary[name] = {"ok": False, "type": type(error).__name__, "seconds": round(time.time() - started, 2)}
        print(f"{side:5} {name:50} {'ok' if summary[name]['ok'] else summary[name]['type']}", flush=True)
    json.dump(summary, open(os.path.join(out_root, "_summary.json"), "w", encoding="utf-8"), indent=1)


if __name__ == "__main__":
    main()
