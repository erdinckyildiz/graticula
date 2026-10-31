"""ADR-250 condition 2: the least-squares cases of adr250-parcel.py, Pro 3.7 against grapy, measured.

    python adr250-compare.py <pro.json> <grapy.json> [case ...]

For each case: the largest coordinate difference of the adjusted points, the largest difference of a correction
(distances in metres, angles in seconds) over rows matched by measurement type and point names, the largest
relative difference of AdjustedStdDev and Residual, of NormalStatistic, of EllipseMajor/EllipseMinor and XY
Uncertainty, and the rows one side has and the other has not. Pro rounds its outputs (4 decimals for metres, 2 for
statistics), which bounds what agreement can show.
"""
import json
import sys

pro, grapy = (json.load(open(p)) for p in sys.argv[1:3])
cases = sys.argv[3:] or [c for c in pro if c.startswith(("lsa", "dir")) and c in grapy]


def key(row):
    return (row["MeasurementType"], row["Point1Name"], row["Point2Name"], row.get("Point3Name"))


def rel(a, b):
    if a is None or b is None:
        return None
    return abs(a - b) / max(abs(a), 1e-12)


for case in cases:
    p, g = pro[case], grapy[case]
    if "error" in p or "error" in g or "exception" in p or "exception" in g:
        print(case, "pro:", p.get("error") or p.get("exception", "")[:120], "| grapy:", g.get("error") or
              g.get("exception", "")[:200])
        continue
    pp = {r["Name"]: r for r in p["Fab_AdjustmentPoints"]}
    gp = {r["Name"]: r for r in g["Fab_AdjustmentPoints"]}
    dxy = max(max(abs(pp[n]["AdjustedX"] - gp[n]["AdjustedX"]), abs(pp[n]["AdjustedY"] - gp[n]["AdjustedY"]))
              for n in pp)
    ell = max(max(rel(pp[n]["EllipseMajor"], gp[n]["EllipseMajor"]) or 0, rel(pp[n]["EllipseMinor"], gp[n]["EllipseMinor"]) or 0)
              for n in pp)
    xyu = max(rel(pp[n]["XYUncertainty"], gp[n]["XYUncertainty"]) or 0 for n in pp)
    sd = max(max(abs(pp[n]["StdDevE"] - gp[n]["StdDevE"]), abs(pp[n]["StdDevN"] - gp[n]["StdDevN"])) for n in pp)
    flags = all((pp[n]["Constrained"], pp[n]["UpdateSource"]) == (gp[n]["Constrained"], gp[n]["UpdateSource"]) for n in pp)
    pl = {key(r): r for r in p["Fab_AdjustmentLines"]}
    gl = {key(r): r for r in g["Fab_AdjustmentLines"]}
    only_p, only_g = sorted(set(pl) - set(gl)), sorted(set(gl) - set(pl))
    dist, ang, stat, norm = 0.0, 0.0, 0.0, 0.0
    for k in set(pl) & set(gl):
        a, b = pl[k], gl[k]
        if a.get("MeasurementCorrection") is None:
            continue
        if k[0] == 2:
            dist = max(dist, abs(a["MeasurementCorrection"] - b["MeasurementCorrection"]))
        else:
            ang = max(ang, abs(a["MeasurementCorrection"] - b["MeasurementCorrection"]) * 3600.0)
        for f in ("AdjustedStdDev", "Residual"):
            r = rel(a.get(f), b.get(f))
            if r is not None and abs(a[f]) >= 0.001:
                stat = max(stat, r)
        if a.get("NormalStatistic") is not None and b.get("NormalStatistic") is not None:
            norm = max(norm, abs(a["NormalStatistic"] - b["NormalStatistic"]))
    print(f"{case}: points {len(pp)}/{len(gp)}, coordinates <= {dxy * 1000:.3f} mm, flags {'same' if flags else 'DIFFER'}; "
          f"rows {len(pl)}/{len(gl)} (only Pro {only_p}, only grapy {only_g}); corrections <= {dist * 1000:.3f} mm and "
          f"{ang:.3f}\"; AdjustedStdDev/Residual <= {stat * 100:.1f} %; NormalStatistic <= {norm:.3f}; "
          f"StdDevE/N <= {sd * 1000:.3f} mm; ellipse axes <= {ell * 100:.2f} %; XY Uncertainty <= {xyu * 100:.2f} %")
