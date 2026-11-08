"""Compare two harness folders, Pro's and grapy's, case by case, and write a verdict for each.

    <grapy python> compare.py <pro module folder> <grapy module folder> [--json report.json] [--show case]

Verdicts:

- MATCH: the same outputs — fields, row count, values and shapes within tolerance (below);
- SCHEMA: the values match, but one side writes fields the other does not (listed);
- DIFF: values, shapes or cells differ beyond tolerance (the largest differences are listed);
- REFUSED: grapy refuses the case by name (NotBuiltError) where Pro answers;
- GRAPY-ERROR: grapy raises anything else where Pro answers;
- PRO-ERROR: Pro rejects the case (the case is wrong for Pro, or Pro needs what this machine lacks);
- BOTH-ERROR: both reject it.

Tolerances: a polygon's symmetric difference within 0.5 % of its area (Pro writes true curves that WKT densifies); a
line or point within 0.01 m (Hausdorff); numbers within 1e-6 relative or 1e-6 absolute; raster cells the same NoData
pattern and within 1e-4 relative or 1e-4 absolute. Rows are paired by shape where there is one, else by order.
"""
import json
import math
import os
import sys

import numpy as np
import shapely
from shapely import wkt as _wkt

IGNORED_FIELDS = {"objectid", "fid", "oid", "shape_length", "shape_area", "shape_leng", "shape_le_1"}
POLY_TOL, LINE_TOL, REL, ABS = 0.005, 0.01, 1e-6, 1e-6


def _read_json(path):
    return json.load(open(path, encoding="utf-8")) if os.path.exists(path) else None


def _num(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def _same(a, b):
    if _num(a) and _num(b):
        return math.isclose(float(a), float(b), rel_tol=REL, abs_tol=ABS)
    if isinstance(a, str) and isinstance(b, str):
        return a.strip() == b.strip()
    if a in (None, "") and b in (None, ""):
        return True
    return a == b


def _shape(text):
    if text in (None, "<no WKT>"):
        return None
    try:
        g = _wkt.loads(text)
        return None if g.is_empty else g
    except Exception:  # noqa: BLE001
        return None


def _shape_distance(a, b):
    """0 for the same shape; for polygons the symmetric difference over the larger area, else the Hausdorff distance
    divided by LINE_TOL — so a value at or below 1 is within tolerance either way."""
    if a is None or b is None:
        return 0.0 if a is None and b is None else math.inf
    if a.geom_type.endswith("Polygon") and b.geom_type.endswith("Polygon"):
        area = max(a.area, b.area, 1e-12)
        return shapely.symmetric_difference(shapely.make_valid(a), shapely.make_valid(b)).area / area / POLY_TOL
    if a.geom_type.endswith("Polygon") != b.geom_type.endswith("Polygon"):
        return math.inf
    return shapely.hausdorff_distance(a, b) / LINE_TOL


def compare_features(p, g):
    notes = []
    pf = {n.lower(): t for n, t in p["fields"] if n.lower() not in IGNORED_FIELDS}
    gf = {n.lower(): t for n, t in g["fields"] if n.lower() not in IGNORED_FIELDS}
    only_pro, only_grapy = sorted(set(pf) - set(gf)), sorted(set(gf) - set(pf))
    shared = [n for n in pf if n in gf]
    pidx = {n.lower(): i for i, (n, _) in enumerate(p["fields"])}
    gidx = {n.lower(): i for i, (n, _) in enumerate(g["fields"])}
    if len(p["rows"]) != len(g["rows"]):
        return "DIFF", [f"rows: Pro {len(p['rows'])}, grapy {len(g['rows'])}"], (only_pro, only_grapy)
    shaped = p.get("shape") and g.get("shape")
    pshapes = [_shape(r[-1]) for r in p["rows"]] if shaped else []
    gshapes = [_shape(r[-1]) for r in g["rows"]] if shaped else []
    pairs = []
    if shaped:
        free = list(range(len(g["rows"])))
        for i, a in enumerate(pshapes):
            best, cost = None, math.inf
            for j in free:
                d = _shape_distance(a, gshapes[j])
                if d < cost or (d == cost and best is not None and all(
                        _same(p["rows"][i][pidx[n]], g["rows"][j][gidx[n]]) for n in shared)):
                    best, cost = j, d
            free.remove(best)
            pairs.append((i, best, cost))
    else:
        def key(row, idx):
            return [str(row[idx[n]]) for n in shared]
        po = sorted(range(len(p["rows"])), key=lambda i: key(p["rows"][i], pidx))
        go = sorted(range(len(g["rows"])), key=lambda j: key(g["rows"][j], gidx))
        pairs = [(i, j, 0.0) for i, j in zip(po, go)]
    worst_shape = max([c for _, _, c in pairs], default=0.0)
    if worst_shape > 1:
        i, j, c = max(pairs, key=lambda t: t[2])
        notes.append(f"shape off by {c:.3g}× tolerance (Pro row {i}: {str(p['rows'][i][-1])[:120]} | grapy: "
                     f"{str(g['rows'][j][-1])[:120]})")
    bad = []
    for n in shared:
        for i, j, _ in pairs:
            a, b = p["rows"][i][pidx[n]], g["rows"][j][gidx[n]]
            if not _same(a, b):
                bad.append(f"{n}: Pro {a!r} grapy {b!r}")
                break
    notes += bad[:8]
    if notes:
        return "DIFF", notes, (only_pro, only_grapy)
    if only_pro or only_grapy:
        return "SCHEMA", [f"fields only Pro writes: {only_pro}; only grapy: {only_grapy}"], (only_pro, only_grapy)
    return "MATCH", [], (only_pro, only_grapy)


def compare_raster(pdir, gdir, name):
    p, g = _read_json(os.path.join(pdir, name + ".json")), _read_json(os.path.join(gdir, name + ".json"))
    a, b = np.load(os.path.join(pdir, name + ".npy")), np.load(os.path.join(gdir, name + ".npy"))
    notes = []
    if a.shape != b.shape:
        return "DIFF", [f"cells: Pro {a.shape}, grapy {b.shape}; extent Pro {p['extent']} grapy {g['extent']}"]
    cell = max(p["cell"][0], 1e-9)
    if any(abs(x - y) > cell / 1000 for x, y in zip(p["extent"], g["extent"])):
        notes.append(f"extent Pro {p['extent']} grapy {g['extent']}")
    ma, mb = np.isnan(a), np.isnan(b)
    if (ma != mb).any():
        notes.append(f"NoData differs in {int((ma != mb).sum())} of {a.size} cells")
    both = ~ma & ~mb
    if both.any():
        d = np.abs(a - b)[both]
        tol = np.maximum(1e-4 * np.abs(a[both]), 1e-4)
        if (d > tol).any():
            notes.append(f"cells differ: {int((d > tol).sum())} of {int(both.sum())}, max |d| {d.max():.6g}, "
                         f"mean |d| {d.mean():.6g}")
    if p.get("isInteger") != g.get("isInteger"):
        notes.append(f"integer: Pro {p.get('isInteger')} grapy {g.get('isInteger')} ({p.get('pixelType')} vs "
                     f"{g.get('pixelType')})")
    return ("DIFF", notes) if notes else ("MATCH", [])


def compare_case(pdir, gdir):
    pe, ge = _read_json(os.path.join(pdir, "error.json")), _read_json(os.path.join(gdir, "error.json"))
    if pe and ge:
        return "BOTH-ERROR", [f"Pro {pe['type']}: {pe['message'][:200]}", f"grapy {ge['type']}: {ge['message'][:200]}"]
    if pe:
        return "PRO-ERROR", [f"{pe['type']}: {pe['message'][:300]}"]
    if ge:
        kind = "REFUSED" if ge["type"] == "NotBuiltError" else "GRAPY-ERROR"
        return kind, [f"{ge['type']}: {ge['message'][:300]}"]
    verdicts, notes = [], []
    pfiles = {f for f in os.listdir(pdir) if f.endswith(".json") and f != "result.json"}
    gfiles = {f for f in os.listdir(gdir) if f.endswith(".json") and f != "result.json"}
    for missing in sorted(pfiles ^ gfiles):
        verdicts.append("DIFF")
        notes.append(f"output {missing[:-5]} only on {'Pro' if missing in pfiles else 'grapy'}'s side")
    for f in sorted(pfiles & gfiles):
        name = f[:-5]
        p, g = _read_json(os.path.join(pdir, f)), _read_json(os.path.join(gdir, f))
        if p["kind"] == "raster" and g["kind"] == "raster":
            v, n = compare_raster(pdir, gdir, name)
        elif p["kind"] == "features" and g["kind"] == "features":
            v, n, _ = compare_features(p, g)
        elif p["kind"] == "file" and g["kind"] == "file":
            v, n = ("MATCH", []) if p["text"].split() == g["text"].split() else ("DIFF", ["file text differs"])
        else:
            v, n = "DIFF", [f"{name}: Pro wrote {p['kind']}, grapy {g['kind']}"]
        verdicts.append(v)
        notes += [f"{name}: {x}" for x in n]
    pr, gr = _read_json(os.path.join(pdir, "result.json")), _read_json(os.path.join(gdir, "result.json"))
    if pr is not None and gr is not None and not pfiles and not gfiles:
        pv = pr if not isinstance(pr, list) else [x for x in pr if not os.path.sep in str(x) and "/" not in str(x)]
        gv = gr if not isinstance(gr, list) else [x for x in gr if not os.path.sep in str(x) and "/" not in str(x)]
        same = (len(pv) == len(gv) and all(_same(_coerce(a), _coerce(b)) for a, b in zip(pv, gv))) \
            if isinstance(pv, list) and isinstance(gv, list) else _same(_coerce(pv), _coerce(gv))
        verdicts.append("MATCH" if same else "DIFF")
        if not same:
            notes.append(f"answer: Pro {str(pr)[:200]} grapy {str(gr)[:200]}")
    order = ["DIFF", "SCHEMA", "MATCH"]
    return (min(verdicts, key=order.index) if verdicts else "MATCH"), notes


def _coerce(v):
    if isinstance(v, str):
        try:
            return float(v)
        except ValueError:
            return v.lower() if v.lower() in ("true", "false") else v
    return v


def main():
    pro, ours = sys.argv[1], sys.argv[2]
    out = sys.argv[sys.argv.index("--json") + 1] if "--json" in sys.argv else None
    report = {}
    for name in sorted(os.listdir(pro)):
        pdir, gdir = os.path.join(pro, name), os.path.join(ours, name)
        if name.startswith("_") or not os.path.isdir(pdir):
            continue
        if not os.path.isdir(gdir):
            report[name] = {"verdict": "NOT-RUN", "notes": ["no grapy folder"]}
            continue
        verdict, notes = compare_case(pdir, gdir)
        report[name] = {"verdict": verdict, "notes": notes}
    counts = {}
    for name, r in report.items():
        counts[r["verdict"]] = counts.get(r["verdict"], 0) + 1
        print(f"{r['verdict']:12} {name}")
        for n in r["notes"][:6]:
            print(f"{'':14}{n}")
    print("  ".join(f"{k} {v}" for k, v in sorted(counts.items())))
    if out:
        json.dump(report, open(out, "w", encoding="utf-8"), indent=1, ensure_ascii=False)


if __name__ == "__main__":
    main()
