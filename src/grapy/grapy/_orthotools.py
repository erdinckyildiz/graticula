"""The ortho mapping tools of arcpy.management, over grapy's mosaic datasets of Frame Camera items (``_ortho``) —
ADR-234.

Each is built to its reference page on doc.esri.com (which pro.arcgis.com's pages redirect to), read on 2026-10-30;
what a page leaves open is labelled INFERRED in the tool's docstring, and what grapy cannot do is refused by name.
``arcpy.management`` answers these functions under their own names; the matchers and the block adjustment are
equivalents (ADR-224 §5.6) and are marked so there.
"""
from __future__ import annotations

import json
import math
import os

import numpy as np
import shapely

from . import _gpkg, _mosaic, _ortho
from ._ortho import CHECK, GCP, TIE
from ._result import Result
from ._runtime import AddMessage, AddWarning, ExecuteError, NotBuiltError, env
from ._runtime import _is_set  # noqa: E402 — a Raster is never compared with ==

_refuse = _ortho._refuse
_given = _ortho._given
_word = _ortho._word


def _mask(value, tool):
    """(include, exclude) areas of a mask dataset: its polygons whose ``mask`` field is 2 and 1, as documented."""
    if not _given(value):
        return None, None
    from . import da
    include, exclude = [], []
    with da.SearchCursor(value, ["mask", "SHAPE@"]) as rows:
        for kind, shape in rows:
            if shape is None:
                continue
            (include if int(kind or 0) == 2 else exclude if int(kind or 0) == 1 else []).append(
                shapely.force_2d(shape._shape))
    return (shapely.union_all(include) if include else None), (shapely.union_all(exclude) if exclude else None)


def _masked(region, include, exclude):
    if include is not None:
        region = region.intersection(include)
    if exclude is not None:
        region = region.difference(exclude)
    return region


def _choices(similarity, density, distribution, location_accuracy, tool):
    sim = _word(similarity, "MEDIUM")
    den = _word(density, "MEDIUM")
    dist = _word(distribution, "RANDOM")
    loc = _word(location_accuracy, "MEDIUM")
    if sim not in _ortho._SIMILARITY:
        _refuse(tool, "similarity", similarity)
    if den not in _ortho._DENSITY:
        _refuse(tool, "density", density)
    if dist not in ("RANDOM", "REGULAR"):
        _refuse(tool, "distribution", distribution)
    if loc == "LOW":
        _refuse(tool, "location_accuracy", location_accuracy, "the page names SIFT for shifts and rotations over five "
                "degrees; grapy's matcher is Harris corners with correlation, which the page assigns to MEDIUM and "
                "HIGH, and would not stand in for SIFT on rotated images (ADR-234 §5.5)")
    if loc not in _ortho._SEARCH:
        _refuse(tool, "location_accuracy", location_accuracy)
    return _ortho._SIMILARITY[sim], _ortho._DENSITY[den], dist, _ortho._SEARCH[loc]


def ComputeTiePoints(in_mosaic_dataset, out_control_points, similarity="MEDIUM", in_mask_dataset=None,
                     out_image_features=None, density="MEDIUM", distribution="RANDOM", location_accuracy="MEDIUM",
                     options=None):
    """EQUIVALENT: tie points between every two overlapping Frame Camera items — Harris corners (Harris and Stephens
    1988) of the first image in the overlap, matched in the second by normalised cross-correlation (Lewis 1995) of the
    two images resampled on a common ground grid at the items' AverageZ (object-space correlation, Helava 1988). The
    page names Harris for MEDIUM and HIGH location accuracy but not how matches are made or kept. Each tie point is two
    rows of the control point feature class (``_ortho.CONTROL_FIELDS``), its shape the intersection of the two rays.

    Refused: ``location_accuracy`` LOW (SIFT, ADR-234 §5.5); ``out_image_features`` — the polygons of image feature
    points have no documented shape; ``options``, which the page reserves to third-party adjustment engines.

    INFERRED (ADR-234 §5.9 (g)): the defaults MEDIUM, MEDIUM, RANDOM, MEDIUM; correlation thresholds LOW 0.6, MEDIUM 0.7,
    HIGH 0.8; 4, 8 or 16 cells across an overlap for LOW, MEDIUM, HIGH density, one point per cell for REGULAR and the
    same number of strongest corners for RANDOM; a 15 × 15 template at the coarser item's ground sample distance; a
    search of ±12 cells (MEDIUM) or ±4 (HIGH); images read as the mean of their bands."""
    tool = "ComputeTiePoints"
    if _given(out_image_features):
        _refuse(tool, "out_image_features", out_image_features, "the polygons of image feature points have no "
                "documented shape or fields")
    if _given(options):
        _refuse(tool, "options", options, "the page reserves them to third-party adjustment engines")
    threshold, n, dist, search = _choices(similarity, density, distribution, location_accuracy, tool)
    include, exclude = _mask(in_mask_dataset, tool)
    connection, mosaic, rec, models, items = _ortho._frame_items(in_mosaic_dataset, tool)
    connection.close()
    ids = sorted(models)
    images = {i: _ortho.image(items[i]["Raster"]) for i in ids}
    responses = {i: _ortho.harris(images[i]) for i in ids}
    rows, point_id = [], 0
    for a_index, i in enumerate(ids):
        for j in ids[a_index + 1:]:
            region = items[i]["shape"].intersection(items[j]["shape"])
            region = _masked(region, include, exclude)
            if region.is_empty or region.area <= 0:
                continue
            mi, mj = models[i], models[j]
            z = mi.average_z
            cell = max(mi.gsd(z), mj.gsd(z))
            a, b = _ortho.Sampler(images[i], mi), _ortho.Sampler(images[j], mj)
            for col, row, X, Y in _ortho.corners_in(mi, images[i], region, z, n, dist, responses[i]):
                found = _ortho.match(a, b, X, Y, z, cell, search, threshold)
                if found is None:
                    continue
                dx, dy, _ = found
                cj, rj = mj.project(np.array([X + dx]), np.array([Y + dy]), np.array([z]))
                cj, rj = float(cj[0]), float(rj[0])
                if not (0 <= cj <= mj.columns and 0 <= rj <= mj.rows):
                    continue
                ground = _ortho.intersect([mi, mj], [(col, row), (cj, rj)])
                point_id += 1
                rows.append((point_id, TIE, 1, i, float(col), float(row), None, None, None, tuple(ground)))
                rows.append((point_id, TIE, 1, j, cj, rj, None, None, None, tuple(ground)))
    out = _ortho.create_points(out_control_points, rec.srid)
    _ortho.write_points(out, rows)
    AddMessage(f"{point_id} tie points between the items of {mosaic}.")
    return Result(out)


def _reference(value, tool):
    """(array, grid) of a reference image read as one grey band."""
    from ._raster import Raster
    raster = Raster(_mosaic.text(value)) if not isinstance(value, Raster) else value
    arrays = [np.where(m, np.nan, a.astype(np.float64)) for a, m in zip(raster._arrays, raster._masks)]
    return np.nanmean(np.stack(arrays), axis=0), raster._grid


def _grid_corners(array, grid, region, n, dist):
    """Candidate ground points of a georeferenced reference image: Harris corners, as ``_ortho.corners_in`` picks them."""
    response = _ortho.harris(np.nan_to_num(array))
    margin = _ortho._HALF + 2
    rows, cols = np.mgrid[margin:array.shape[0] - margin:2, margin:array.shape[1] - margin:2]
    rows, cols = rows.ravel(), cols.ravel()
    xs, ys = grid.left + (cols + 0.5) * grid.width, grid.top - (rows + 0.5) * grid.height
    strength = response[rows, cols]
    keep = shapely.contains_xy(region, xs, ys) & (strength > 0)
    xs, ys, strength = xs[keep], ys[keep], strength[keep]
    if not len(xs):
        return []
    if dist == "REGULAR":
        x0, y0, x1, y1 = region.bounds
        ix = np.minimum(((xs - x0) / max(x1 - x0, 1e-9) * n).astype(int), n - 1)
        iy = np.minimum(((ys - y0) / max(y1 - y0, 1e-9) * n).astype(int), n - 1)
        best = {}
        for k in range(len(xs)):
            if (ix[k], iy[k]) not in best or strength[k] > strength[best[(ix[k], iy[k])]]:
                best[(ix[k], iy[k])] = k
        chosen = sorted(best.values())
    else:
        chosen = list(np.argsort(-strength, kind="stable")[: n * n])
    return [(xs[k], ys[k]) for k in chosen]


def ComputeControlPoints(in_mosaic_dataset, in_reference_images, out_control_points, similarity="MEDIUM",
                         out_image_feature_points=None, density="MEDIUM", distribution="RANDOM", area_of_interest=None,
                         location_accuracy="MEDIUM"):
    """EQUIVALENT: ground control points from a georeferenced reference image — its Harris corners within each item's
    footprint, matched in the item as ComputeTiePoints matches (see there). A control point's ground x and y are the
    reference image's, its z the item's AverageZ (INFERRED; AppendControlPoints takes z from a field or DEM), and each
    is one row per item it was found on (Type 2).

    Refused as in ComputeTiePoints: LOW location accuracy (SIFT), ``out_image_feature_points``; a reference that is not
    a raster grapy reads (image and map services: a fenced tool has no network, ADR-223 §5.3)."""
    tool = "ComputeControlPoints"
    if _given(out_image_feature_points):
        _refuse(tool, "out_image_feature_points", out_image_feature_points, "the polygons of image feature points have "
                "no documented shape or fields")
    threshold, n, dist, search = _choices(similarity, density, distribution, location_accuracy, tool)
    from .management import _list
    references = [_reference(r, tool) for r in _list(in_reference_images)]
    if not references:
        raise ExecuteError("ERROR 000735: Reference Images: Value is required.")
    aoi = None
    if _given(area_of_interest):
        from . import da
        with da.SearchCursor(area_of_interest, ["SHAPE@"]) as rows:
            shapes = [shapely.force_2d(r[0]._shape) for r in rows if r[0] is not None]
        aoi = shapely.union_all(shapes) if shapes else None
    connection, mosaic, rec, models, items = _ortho._frame_items(in_mosaic_dataset, tool)
    connection.close()
    rows, point_id = [], 0
    for i in sorted(models):
        model, img = models[i], _ortho.image(items[i]["Raster"])
        b = _ortho.Sampler(img, model)
        z = model.average_z
        for array, grid in references:
            region = items[i]["shape"].intersection(shapely.box(grid.left, grid.bottom, grid.right, grid.top))
            if aoi is not None:
                region = region.intersection(aoi)
            if region.is_empty:
                continue
            a = _ortho.Sampler(array, grid=grid)
            cell = max(model.gsd(z), (grid.width + grid.height) / 2)
            for X, Y in _grid_corners(array, grid, region, n, dist):
                found = _ortho.match(a, b, X, Y, z, cell, search, threshold)
                if found is None:
                    continue
                dx, dy, _ = found
                c, r = model.project(np.array([X + dx]), np.array([Y + dy]), np.array([z]))
                point_id += 1
                rows.append((point_id, GCP, 1, i, float(c[0]), float(r[0]), None, None, None, (X, Y, z)))
    out = _ortho.create_points(out_control_points, rec.srid)
    _ortho.write_points(out, rows)
    AddMessage(f"{point_id} control points on the items of {mosaic}.")
    return Result(out)


def _json_points(value, srid, tool):
    """Control point records from MatchControlPoints' JSON — a string or a file — with its documented keys (pointId, x,
    y, z, xyAccuracy, zAccuracy, spatialReference, imagePointSpatialReference, imagePoints: imageId, x, y); the code
    samples' ``imageID``, ``u`` and ``v`` are read too."""
    text = _mosaic.text(value)
    data = json.load(open(text, encoding="utf-8")) if os.path.isfile(text) else json.loads(text)
    if isinstance(data, dict):
        data = data.get("points") or data.get("controlPoints") or [data]
    out = []
    for point in data:
        ref = point.get("spatialReference") or {"wkid": 4326}
        wkid = ref.get("wkid") if isinstance(ref, dict) else ref
        x, y, z = float(point["x"]), float(point["y"]), float(point.get("z") or 0)
        if wkid and srid and int(wkid) != int(srid):
            from pyproj import Transformer
            x, y = Transformer.from_crs(int(wkid), int(srid), always_xy=True).transform(x, y)
        ics = point.get("imagePointSpatialReference")
        if ics not in (None, "ICS", {"ics": True}) and str(ics).upper() != "ICS":
            _refuse(tool, "imagePointSpatialReference", ics, "image points are read in the image coordinate system "
                    "(ICS), the page's default")
        base = {"PointID": int(point.get("pointId") or point.get("pointID") or len(out) + 1), "Type": GCP, "Status": 1,
                "XYAccuracy": point.get("xyAccuracy"), "ZAccuracy": point.get("zAccuracy"), "Tag": None,
                "xyz": (x, y, z)}
        observations = point.get("imagePoints") or []
        if not observations:
            out.append({**base, "ImageID": None, "ImageX": None, "ImageY": None})
        for o in observations:
            out.append({**base, "ImageID": int(o.get("imageId", o.get("imageID"))),
                        "ImageX": float(o.get("x", o.get("u"))), "ImageY": float(o.get("y", o.get("v")))})
    return out


def _records_of(value, srid, tool):
    """Control point records from a feature class (a GeoPackage path) or the page's JSON (a string or file)."""
    text = _mosaic.text(value)
    if ".gpkg" in text.lower():
        return _ortho.read_points(text)
    return _json_points(value, srid, tool)


def _row(r):
    return (int(r["PointID"]), int(r["Type"] or TIE), int(1 if r.get("Status") is None else r["Status"]),
            r.get("ImageID"), r.get("ImageX"), r.get("ImageY"), r.get("XYAccuracy"), r.get("ZAccuracy"), r.get("Tag"),
            tuple(r["xyz"]))


def MatchControlPoints(in_mosaic_dataset, in_control_points, out_control_points, similarity="MEDIUM"):
    """EQUIVALENT: each ground control point found on every other item that covers it — the GCP's image measured on one
    item (its "initial tie point") matched into the others as ComputeTiePoints matches, at the GCP's height. The input is
    a control point feature class or the page's JSON. It fails without a GCP, or with GCPs but no initial tie point, and
    warns when no new tie point is found, as documented.

    INFERRED (ADR-234 §5.9 (h)): the JSON's ``spatialReference`` defaults to WGS 84, as documented, and is projected to
    the mosaic dataset's; the similarity default MEDIUM; the search ±12 cells."""
    tool = "MatchControlPoints"
    threshold = _ortho._SIMILARITY.get(_word(similarity, "MEDIUM"))
    if threshold is None:
        _refuse(tool, "similarity", similarity)
    connection, mosaic, rec, models, items = _ortho._frame_items(in_mosaic_dataset, tool)
    connection.close()
    records = _records_of(in_control_points, rec.srid, tool)
    gcps = {pid: rs for pid, rs in _ortho.points_by_id(records).items() if any(int(r["Type"] or 0) in (GCP, CHECK)
                                                                            for r in rs)}
    if not gcps:
        raise ExecuteError("ERROR: the control points hold no ground control point.")
    measured = {pid: [r for r in rs if r.get("ImageID") is not None] for pid, rs in gcps.items()}
    if not any(measured.values()):
        raise ExecuteError("ERROR: the ground control points have no initial tie point.")
    images, added = {}, []
    for pid, seen in measured.items():
        if not seen:
            continue
        first = seen[0]
        k = int(first["ImageID"])
        if k not in models:
            continue
        X0, Y0, Z = first["xyz"]
        images.setdefault(k, _ortho.image(items[k]["Raster"]))
        gx, gy = models[k].to_ground(np.array([first["ImageX"]]), np.array([first["ImageY"]]), Z)
        a = _ortho.Sampler(images[k], models[k])
        done = {int(r["ImageID"]) for r in seen}
        for j in sorted(models):
            if j in done or not items[j]["shape"].contains(shapely.Point(gx[0], gy[0])):
                continue
            images.setdefault(j, _ortho.image(items[j]["Raster"]))
            cell = max(models[k].gsd(Z), models[j].gsd(Z))
            found = _ortho.match(a, _ortho.Sampler(images[j], models[j]), gx[0], gy[0], Z, cell, 12, threshold)
            if found is None:
                continue
            c, r = models[j].project(np.array([gx[0] + found[0]]), np.array([gy[0] + found[1]]), np.array([Z]))
            added.append({**first, "ImageID": j, "ImageX": float(c[0]), "ImageY": float(r[0])})
    if not added:
        AddWarning("No new tie points were found; the control points are unchanged.")
    out = _ortho.create_points(out_control_points, rec.srid)
    _ortho.write_points(out, [_row(r) for r in records + added])
    return Result(out)


def AppendControlPoints(in_master_control_points, in_input_control_points, in_z_field=None, in_tag_field=None,
                        in_dem=None, in_xy_accuracy=None, in_z_accuracy=None, Geoid="NONE", area_of_interest=None,
                        append_option="ALL"):
    """Control points appended to a control point feature class: ``ALL`` every point, ``GCP`` only the ground control
    points, ``GCPSET`` the GCPs and the tie points measured with them, as documented. A plain point feature class reads
    as GCPs not yet measured on an image. A GCP's or check point's z comes from ``in_z_field``, else ``in_dem``, else 0,
    as the page says; ``in_tag_field`` is copied into Tag; the accuracies are written as given; points outside
    ``area_of_interest`` are left out. Point IDs are renumbered after the master's.

    Refused: ``Geoid`` GEOID — grapy carries no EGM96 grid.

    INFERRED (ADR-234 §5.9 (h)): the tie points "associated" with a GCP under GCPSET are those sharing an image with it;
    an input's points keep its z when neither a field nor a DEM is given only when they are tie points."""
    tool = "AppendControlPoints"
    if _word(Geoid, "NONE") not in ("NONE", "FALSE"):
        _refuse(tool, "Geoid", Geoid, "grapy carries no EGM96 geoid grid")
    option = _word(append_option, "ALL")
    if option not in ("ALL", "GCP", "GCPSET"):
        _refuse(tool, "append_option", append_option)
    master = _ortho.read_points(_mosaic.text(in_master_control_points))
    from .analysis import _schema
    srid = _schema(_mosaic.text(in_master_control_points))[2]
    incoming = _records_of(in_input_control_points, srid, tool)
    extras = {}
    if _given(in_z_field) or _given(in_tag_field):
        from . import da
        fields = [f for f in (in_z_field, in_tag_field) if _given(f)]
        with da.SearchCursor(_mosaic.text(in_input_control_points), ["OID@", *fields]) as rows:
            for row in rows:
                extras[row[0]] = dict(zip(fields, row[1:]))
    dem = None
    if _given(in_dem) and not _given(in_z_field):
        from ._raster import Raster
        dem = Raster(_mosaic.text(in_dem))
    area = None
    if _given(area_of_interest):
        from ._terrain import _area
        area = _area(area_of_interest, tool)
    if option != "ALL":
        gcp_images = {r.get("ImageID") for r in incoming if int(r["Type"] or 0) in (GCP, CHECK)}
        incoming = [r for r in incoming if int(r["Type"] or 0) in (GCP, CHECK) or
                    (option == "GCPSET" and r.get("ImageID") in gcp_images)]
    start = max([int(r["PointID"]) for r in master], default=0)
    renumber, out = {}, []
    for r in incoming:
        x, y, z = r["xyz"]
        if area is not None and not area.covers(shapely.Point(x, y)):
            continue
        extra = extras.get(r.get("OID"), {})
        if int(r["Type"] or 0) in (GCP, CHECK):
            if _given(in_z_field):
                z = float(extra.get(in_z_field) or 0)
            elif dem is not None:
                from .sa import _sample
                value = _sample(dem, x, y, True)
                z = 0.0 if value is None else float(value)
            elif "pointid" not in {k.lower() for k in r}:
                z = 0.0
        key = int(r["PointID"])
        renumber.setdefault(key, start + len(renumber) + 1)
        tag = extra.get(in_tag_field) if _given(in_tag_field) else r.get("Tag")
        out.append({**r, "PointID": renumber[key], "xyz": (x, y, z), "Tag": None if tag is None else str(tag),
                    "XYAccuracy": float(in_xy_accuracy) if _given(in_xy_accuracy) else r.get("XYAccuracy"),
                    "ZAccuracy": float(in_z_accuracy) if _given(in_z_accuracy) else r.get("ZAccuracy")})
    _ortho.write_points(_mosaic.text(in_master_control_points), [_row(r) for r in out])
    return Result(_mosaic.text(in_master_control_points))


def AnalyzeControlPoints(in_mosaic_dataset, in_control_points, out_coverage_table, out_overlap_table,
                         in_mask_dataset=None, minimum_area=None, maximum_level=None):
    """For each item, how many control points it has and how much of it they cover, and every overlap of items with
    the control points in it — the two feature classes the page describes.

    INFERRED (ADR-234 §5.9 (i)): the fields — coverage: ``ImageID``, ``PointCount``, ``Coverage`` (the area of the
    convex hull of the item's points' ground positions as a percent of its footprint); overlap: ``ImageIDs`` (the ids
    joined by ``;``), ``ImageCount``, ``PointCount`` (points measured on every image of the overlap), ``Area``;
    ``minimum_area`` a percent of the smallest image of the overlap, 0 by default; ``maximum_level`` 2 by default."""
    from itertools import combinations
    tool = "AnalyzeControlPoints"
    include, exclude = _mask(in_mask_dataset, tool)
    connection, mosaic, rec, models, items = _ortho._frame_items(in_mosaic_dataset, tool)
    connection.close()
    records = [r for r in _ortho.read_points(_mosaic.text(in_control_points)) if r.get("ImageID") is not None
               and int(r["Status"] if r["Status"] is not None else 1) == 1]
    by_image: dict = {}
    for r in records:
        by_image.setdefault(int(r["ImageID"]), []).append(r)
    coverage = []
    for i in sorted(models):
        region = _masked(items[i]["shape"], include, exclude)
        found = by_image.get(i, [])
        hull = shapely.convex_hull(shapely.MultiPoint([r["xyz"][:2] for r in found])) if found else None
        share = 0.0 if hull is None or region.area <= 0 else hull.intersection(region).area / region.area * 100
        coverage.append((i, len(found), share, region))
    from ._tintools import _write
    _write(out_coverage_table, "Polygon", rec.srid, [_gpkg.Column("ImageID", "Integer"),
                                                     _gpkg.Column("PointCount", "Integer"),
                                                     _gpkg.Column("Coverage", "Double")], False,
           [(i, n, share, shapely.MultiPolygon([p for p in getattr(region, "geoms", [region]) if p.area > 0]))
            for i, n, share, region in coverage if region.area > 0])
    level = int(maximum_level) if _given(maximum_level) else 2
    least = float(minimum_area) if _given(minimum_area) else 0.0
    points = _ortho.points_by_id(records)
    overlaps = []
    for size in range(2, max(level, 2) + 1):
        for combo in combinations(sorted(models), size):
            region = shapely.intersection_all([items[i]["shape"] for i in combo])
            region = _masked(region, include, exclude)
            smallest = min(items[i]["shape"].area for i in combo)
            if region.is_empty or region.area <= 0 or region.area < least / 100 * smallest:
                continue
            count = sum(1 for rs in points.values() if set(combo) <= {int(r["ImageID"]) for r in rs})
            overlaps.append((";".join(str(i) for i in combo), size, count, region.area,
                             shapely.MultiPolygon([p for p in getattr(region, "geoms", [region]) if p.area > 0])))
    _write(out_overlap_table, "Polygon", rec.srid, [_gpkg.Column("ImageIDs", "String"),
                                                    _gpkg.Column("ImageCount", "Integer"),
                                                    _gpkg.Column("PointCount", "Integer"),
                                                    _gpkg.Column("Area", "Double")], False, overlaps)
    return Result(_mosaic.text(out_coverage_table))


# ---- block adjustment

ADJUSTMENT = ("a bundle adjustment of the collinearity equations by sparse Levenberg–Marquardt least squares",
              "Triggs, McLauchlan, Hartley and Fitzgibbon 2000; Wolf, Dewitt and Wilkinson 2014, ch. 17")
_PRIOR = {"x": 10.0, "y": 10.0, "z": 10.0, "omega": 1.0, "phi": 1.0, "kappa": 1.0}


def _options(value, tool):
    """{name: number} of an adjustment options value table (Name and Value)."""
    from ._tin import rows_of
    out = {}
    for row in rows_of(value, 2):
        if not row[0]:
            continue
        out[str(row[0]).strip().lower()] = float(row[1]) if str(row[1]).strip() else 0.0
    known = {"minresidual", "maxresidualfactor", "calibratef", "calibratepp", "calibratep", "calibratek",
             "aprioriaccuracyx", "aprioriaccuracyy", "aprioriaccuracyz", "aprioriaccuracyxy", "aprioriaccuracyxyz",
             "aprioriaccuracyomega", "aprioriaccuracyphi", "aprioriaccuracyomegaphi", "aprioriaccuracykappa",
             "computeantennaoffset", "computeshift", "computeimageposteriorstd", "computesolutionpointposteriorstd",
             "rigcamera"}
    for name, number in out.items():
        if name not in known:
            _refuse(tool, "adjustment_options", name, "it is not one of the Frame options the page lists")
        if name in ("calibratep", "calibratek") and number:
            _refuse(tool, "adjustment_options", name, "distortion is calibrated in the cameras table's Radial and "
                    "Tangential terms, whose sign convention the page does not give, so coefficients estimated under "
                    "grapy's reading would be read the other way by Pro (ADR-234 §5.9 (a))")
        if name in ("computeantennaoffset", "computeshift", "rigcamera") and number:
            _refuse(tool, "adjustment_options", name, "the page names it without saying what it estimates or from which "
                    "fields")
    return out


def _priors(model, options):
    """σ of each exterior orientation parameter: the options' a priori accuracies, else the frame's PriorAccuracy
    fields, else grapy's defaults (INFERRED, ADR-234 §5.9 (j): 10 map units, 1 degree)."""
    d = model.data.get("prior") or {}

    def pick(*names, key):
        for n in names:
            if n in options and options[n] > 0:
                return options[n]
        return d.get(key) or _PRIOR[key]
    return [pick("aprioriaccuracyx", "aprioriaccuracyxy", "aprioriaccuracyxyz", key="x"),
            pick("aprioriaccuracyy", "aprioriaccuracyxy", "aprioriaccuracyxyz", key="y"),
            pick("aprioriaccuracyz", "aprioriaccuracyxyz", key="z"),
            pick("aprioriaccuracyomega", "aprioriaccuracyomegaphi", key="omega"),
            pick("aprioriaccuracyphi", "aprioriaccuracyomegaphi", key="phi"),
            pick("aprioriaccuracykappa", key="kappa")]


def _with(model, eo, cam=None):
    data = dict(model.data)
    data.update(dict(zip(("X", "Y", "Z", "omega", "phi", "kappa"), (float(v) for v in eo))))
    if cam is not None:
        data.update(dict(zip(("focal_length", "principal_x", "principal_y"), (float(v) for v in cam))))
    return _ortho.FrameModel(data)


def adjust(models, records, options, max_residual, dem=None, elevation_accuracy=None):
    """The bundle adjustment: adjusted models, adjusted ground points, per-observation residuals and the statuses of
    the observations kept. Unknowns are each image's six exterior orientation parameters, each tie point's ground
    position and, when asked, each camera's focal length and principal point; observations are the image measurements
    (σ 1 pixel), the frames' exterior orientation (σ the priors), the GCPs' ground positions (σ their accuracies, 0.1
    by default, INFERRED) and, with a DEM, each tie point's height (σ ``elevation_accuracy``, 1 by default, INFERRED)."""
    from scipy.optimize import least_squares
    from scipy.sparse import lil_matrix
    ids = sorted(models)
    index = {i: k for k, i in enumerate(ids)}
    calibrate_f, calibrate_pp = bool(options.get("calibratef")), bool(options.get("calibratepp"))
    cameras = sorted({models[i].data.get("camera_id") or "" for i in ids}) if (calibrate_f or calibrate_pp) else []
    cam_index = {c: k for k, c in enumerate(cameras)}
    groups = _ortho.points_by_id(records)
    points, obs = [], []   # obs: (point k, image i, col, row, record)
    known = {}
    for pid, rs in sorted(groups.items()):
        seen = [r for r in rs if r.get("ImageID") is not None and int(r["ImageID"]) in index and
                int(r["Status"] if r["Status"] is not None else 1) == 1]
        kind = int(rs[0]["Type"] or TIE)
        if kind == CHECK:
            continue
        if kind == TIE and len(seen) < 2:
            continue
        if not seen:
            continue
        k = len(points)
        start = rs[0]["xyz"]
        if kind == TIE or not np.all(np.isfinite(start)):
            start = _ortho.intersect([models[int(r["ImageID"])] for r in seen],
                                     [(r["ImageX"], r["ImageY"]) for r in seen]) if len(seen) > 1 else start
        points.append((pid, kind, np.array(start, float)))
        if kind == GCP:
            xy = float(rs[0].get("XYAccuracy") or 0.1)
            zs = float(rs[0].get("ZAccuracy") or 0.1)
            known[k] = (np.array(rs[0]["xyz"], float), np.array([xy, xy, zs]))
        for r in seen:
            obs.append((k, index[int(r["ImageID"])], float(r["ImageX"]), float(r["ImageY"]), r))
    if not obs:
        raise ExecuteError("ERROR: no control point is measured on two or more Frame Camera items.")
    n_img, n_pt, n_cam = len(ids), len(points), len(cameras)
    eo0 = np.array([[models[i].X, models[i].Y, models[i].Z, models[i].omega, models[i].phi, models[i].kappa]
                    for i in ids], float)
    first_of = {}
    for i in ids:
        first_of.setdefault(models[i].data.get("camera_id") or "", models[i])
    cam0 = np.array([[first_of[c].f, first_of[c].x0, first_of[c].y0] for c in cameras], float).reshape(-1, 3)
    sigma = np.array([_priors(models[i], options) for i in ids], float)
    heights = {}
    if dem is not None:
        from .sa import _sample
        accuracy = float(elevation_accuracy) if _given(elevation_accuracy) else 1.0
        for k, (_, kind, start) in enumerate(points):
            if kind == TIE:
                value = _sample(dem, start[0], start[1], True)
                if value is not None:
                    heights[k] = (float(value), accuracy)
    active = np.ones(len(obs), bool)

    def unpack(x):
        eo = x[:6 * n_img].reshape(n_img, 6)
        pts = x[6 * n_img:6 * n_img + 3 * n_pt].reshape(n_pt, 3)
        cam = x[6 * n_img + 3 * n_pt:].reshape(n_cam, 3) if n_cam else None
        return eo, pts, cam

    def current(eo, cam, k):
        i = ids[k]
        c = cam[cam_index[models[i].data.get("camera_id") or ""]] if cam is not None else None
        return _with(models[i], eo[k], c)

    obs_point = np.array([o[0] for o in obs], np.int64)
    measured = np.array([[o[2], o[3]] for o in obs], float)
    by_image = [np.nonzero(np.array([o[1] for o in obs]) == k)[0] for k in range(n_img)]
    known_ids = np.array(list(known), np.int64)
    known_xyz = np.array([known[k][0] for k in known], float).reshape(-1, 3)
    known_s = np.array([known[k][1] for k in known], float).reshape(-1, 3)
    height_ids = np.array(list(heights), np.int64)
    height_z = np.array([heights[k][0] for k in heights], float)
    height_s = np.array([heights[k][1] for k in heights], float)

    def residuals(x, keep):
        eo, pts, cam = unpack(x)
        image = np.zeros((len(obs), 2))
        for k in range(n_img):
            sel = by_image[k]
            if not len(sel):
                continue
            p = pts[obs_point[sel]]
            c, r = current(eo, cam, k).project(p[:, 0], p[:, 1], p[:, 2])
            image[sel, 0], image[sel, 1] = c - measured[sel, 0], r - measured[sel, 1]
        image[~keep] = 0.0
        return np.concatenate([image.ravel(), ((eo - eo0) / sigma).ravel(),
                               ((pts[known_ids] - known_xyz) / known_s).ravel(),
                               (pts[height_ids, 2] - height_z) / height_s])

    sparsity = lil_matrix((2 * len(obs) + 6 * n_img + 3 * len(known) + len(heights), 6 * n_img + 3 * n_pt + 3 * n_cam),
                          dtype=int)
    for o, (k, i, _, _, _) in enumerate(obs):
        for row in (2 * o, 2 * o + 1):
            sparsity[row, 6 * i:6 * i + 6] = 1
            sparsity[row, 6 * n_img + 3 * k:6 * n_img + 3 * k + 3] = 1
            if n_cam:
                c = cam_index[models[ids[i]].data.get("camera_id") or ""]
                sparsity[row, 6 * n_img + 3 * n_pt + 3 * c:6 * n_img + 3 * n_pt + 3 * c + 3] = 1
    base = 2 * len(obs)
    for p in range(6 * n_img):
        sparsity[base + p, p] = 1
    base += 6 * n_img
    for q, k in enumerate(known):
        for a in range(3):
            sparsity[base + 3 * q + a, 6 * n_img + 3 * k + a] = 1
    base += 3 * len(known)
    for q, k in enumerate(heights):
        sparsity[base + q, 6 * n_img + 3 * k + 2] = 1
    x = np.concatenate([eo0.ravel(), np.array([p[2] for p in points]).ravel(), cam0.ravel()])
    factor = options.get("maxresidualfactor")
    floor = options.get("minresidual", 0.0)
    for _ in range(6):
        solved = least_squares(residuals, x, args=(active,), jac_sparsity=sparsity, method="trf", x_scale="jac",
                               xtol=1e-12, ftol=1e-12, gtol=1e-12, max_nfev=200)
        x = solved.x
        r = solved.fun[:2 * len(obs)].reshape(-1, 2)
        norms = np.hypot(r[:, 0], r[:, 1])
        rms = float(np.sqrt(np.mean(norms[active] ** 2))) if active.any() else 0.0
        limit = max_residual if max_residual is not None else (factor * rms if factor else None)
        if limit is None:
            break
        limit = max(limit, floor)
        drop = active & (norms > limit)
        if not drop.any():
            break
        active &= ~drop
    eo, pts, cam = unpack(x)
    adjusted = {i: current(eo, cam, k) for k, i in enumerate(ids)}
    std = None
    if options.get("computeimageposteriorstd", 1) and solved.jac is not None:
        J = solved.jac.toarray() if hasattr(solved.jac, "toarray") else solved.jac
        dof = max(len(solved.fun) - len(x), 1)
        s0 = float(solved.fun @ solved.fun) / dof
        try:
            covariance = np.linalg.pinv(J.T @ J) * s0
            std = np.sqrt(np.abs(np.diag(covariance)))
        except np.linalg.LinAlgError:
            std = None
    return {"models": adjusted, "points": points, "ground": pts, "obs": obs, "active": active, "residuals": norms,
            "std": std, "ids": ids, "n": (n_img, n_pt)}


_SOLUTION_FIELDS = (("ImageID", "Integer"), ("CameraID", "String"), ("PerspectiveX", "Double"),
                    ("PerspectiveY", "Double"), ("PerspectiveZ", "Double"), ("Omega", "Double"), ("Phi", "Double"),
                    ("Kappa", "Double"), ("FocalLength", "Double"), ("PrincipalX", "Double"), ("PrincipalY", "Double"),
                    ("StdX", "Double"), ("StdY", "Double"), ("StdZ", "Double"), ("StdOmega", "Double"),
                    ("StdPhi", "Double"), ("StdKappa", "Double"), ("RMSE", "Double"), ("Observations", "Integer"))
_SOLUTION_POINT_FIELDS = (("PointID", "Integer"), ("Type", "Integer"), ("Status", "Integer"), ("ImageCount", "Integer"),
                          ("Residual", "Double"))


def ComputeBlockAdjustment(in_mosaic_dataset, in_control_points, transformation_type, out_solution_table,
                           out_solution_point_table=None, maximum_residual_value=None, adjustment_options=None,
                           location_accuracy=None, out_quality_table=None, DEM=None, elevation_accuracy=None):
    """EQUIVALENT: the block adjustment of a mosaic dataset of Frame Camera items — a bundle adjustment of the
    collinearity equations (Triggs et al. 2000; Wolf et al. ch. 17) solved by sparse Levenberg–Marquardt least squares.
    The page calls it a block adjustment and names its options but not its estimator. Observations whose residual
    exceeds ``maximum_residual_value`` (5 pixels by default for Frame, as documented) are dropped and the block solved
    again; ``MinResidual`` and ``MaxResidualFactor`` act as documented. ``CalibrateF`` and ``CalibratePP`` add each
    camera's focal length and principal point as unknowns; the ``APrioriAccuracy`` options weigh the frames' exterior
    orientation; a ``DEM`` gives each tie point a height observation weighted by ``elevation_accuracy``, as documented.
    The solution table holds each image's adjusted orientation, which ApplyBlockAdjustment applies.

    Refused: POLYORDER0 and POLYORDER1 — a polynomial moves a georeferenced item off its grid, and grapy's mosaic
    datasets read items only on whole cells (ADR-230 §5.3); RPC — an RPC image enters a mosaic dataset through a
    satellite raster type, Esri's own definition (ADR-230 §5.3); CalibrateP and CalibrateK, ComputeAntennaOffset,
    ComputeShift, rigCamera; ``out_quality_table`` (RPC only, as documented).

    INFERRED (ADR-234 §5.9 (j)): the solution and solution point tables' fields (``_SOLUTION_FIELDS``,
    ``_SOLUTION_POINT_FIELDS``) and that the solution points are points, the page saying polygons; a priori accuracies
    of 10 map units and 1 degree where neither the options nor the frames table give one; a GCP's accuracy 0.1 when the
    control points give none."""
    tool = "ComputeBlockAdjustment"
    kind = _word(transformation_type, "POLYORDER1")
    if kind in ("POLYORDER0", "POLYORDER1"):
        _refuse(tool, "transformation_type", transformation_type, "a polynomial moves a georeferenced item off its "
                "grid, and grapy's mosaic datasets read items only on whole cells, refusing resampling (ADR-230 §5.3)")
    if kind == "RPC":
        _refuse(tool, "transformation_type", transformation_type, "an RPC image enters a mosaic dataset through a "
                "satellite sensor's raster type, Esri's own definition (ADR-230 §5.3); CreateOrthoCorrectedRasterDataset "
                "orthorectifies an RPC raster")
    if kind != "FRAME":
        _refuse(tool, "transformation_type", transformation_type)
    if _given(out_quality_table):
        _refuse(tool, "out_quality_table", out_quality_table, "it is written for RPC only, as documented")
    options = _options(adjustment_options, tool)
    connection, mosaic, rec, models, items = _ortho._frame_items(in_mosaic_dataset, tool)
    connection.close()
    records = _ortho.read_points(_mosaic.text(in_control_points))
    dem = None
    if _given(DEM):
        from ._raster import Raster
        dem = Raster(_mosaic.text(DEM))
    limit = float(maximum_residual_value) if _given(maximum_residual_value) else (
        None if options.get("maxresidualfactor") else 5.0)
    result = adjust(models, records, options, limit, dem, elevation_accuracy)
    _write_solution(out_solution_table, result)
    if _given(out_solution_point_table):
        _write_solution_points(out_solution_point_table, result, rec.srid)
    kept = result["active"]
    rms = float(np.sqrt(np.mean(result["residuals"][kept] ** 2))) if kept.any() else 0.0
    AddMessage(f"Block adjustment of {len(models)} images and {result['n'][1]} points: RMS {rms:.4f} pixels, "
               f"{int((~kept).sum())} observations dropped.")
    return Result(_mosaic.text(out_solution_table))


def _write_solution(path, result):
    from . import da
    from .management import _table_out
    out = _table_out("ComputeBlockAdjustment", _mosaic.text(path))
    _gpkg.create(out, "Geometry", 0, [_gpkg.Column(n, t) for n, t in _SOLUTION_FIELDS])
    std = result["std"]
    with da.InsertCursor(out, [n for n, _ in _SOLUTION_FIELDS]) as written:
        for k, i in enumerate(result["ids"]):
            m = result["models"][i]
            mine = [o for o, ob in enumerate(result["obs"]) if ob[1] == k and result["active"][o]]
            rmse = float(np.sqrt(np.mean(result["residuals"][mine] ** 2))) if mine else None
            s = std[6 * k:6 * k + 6].tolist() if std is not None else [None] * 6
            written.insertRow([i, m.data.get("camera_id") or "", m.X, m.Y, m.Z, m.omega, m.phi, m.kappa, m.f, m.x0,
                               m.y0, *s, rmse, len(mine)])
    return out


def _write_solution_points(path, result, srid):
    from ._tintools import _write
    rows = []
    for k, (pid, kind, _) in enumerate(result["points"]):
        mine = [o for o, ob in enumerate(result["obs"]) if ob[0] == k]
        kept = [o for o in mine if result["active"][o]]
        residual = float(np.sqrt(np.mean(result["residuals"][kept] ** 2))) if kept else None
        rows.append((pid, kind, 1 if kept else 0, len(kept), residual, shapely.Point(*result["ground"][k])))
    return _write(path, "Point", srid, [_gpkg.Column(n, t) for n, t in _SOLUTION_POINT_FIELDS], True, rows)


def _solution(path):
    from . import da
    with da.SearchCursor(_mosaic.text(path), [n for n, _ in _SOLUTION_FIELDS]) as rows:
        return {int(r[0]): dict(zip([n for n, _ in _SOLUTION_FIELDS], r)) for r in rows}


def ApplyBlockAdjustment(in_mosaic_dataset, adjustment_operation, input_solution_table=None,
                         pan_to_ms_scaling_factor=None, DEM=None, zoffset=None, control_point_table=None,
                         adjust_footprints="NO_ADJUST_FOOTPRINTS", solution_point_table=None,
                         adjust_tiepoints="NO_ADJUST_TIEPOINTS"):
    """A solution table applied to a mosaic dataset in place (no new mosaic dataset is made, as documented): ``ADJUST``
    sets each item's orientation to the solution's; ``RESET`` puts back the orientation the item was added with;
    ``REACTIVATE`` restores items an adjustment dropped — grapy's drops none, so it changes nothing and says so.
    ``ADJUST_FOOTPRINTS`` moves the footprints to the new orientation. With a solution point table, a control point
    table's Status is updated from it, and ``ADJUST_TIEPOINTS`` moves the tie points to their adjusted ground positions,
    as documented. ``DEM`` and ``zoffset`` are kept as the mosaic dataset's elevation for orthorectification.

    Refused: ``pan_to_ms_scaling_factor``, which applies to pan-sharpened rasters grapy's mosaics do not hold."""
    tool = "ApplyBlockAdjustment"
    operation = _word(adjustment_operation, "")
    if operation not in ("ADJUST", "RESET", "REACTIVATE"):
        _refuse(tool, "adjustment_operation", adjustment_operation)
    if _given(pan_to_ms_scaling_factor):
        _refuse(tool, "pan_to_ms_scaling_factor", pan_to_ms_scaling_factor, "it applies to pan-sharpened rasters, which "
                "grapy's mosaic datasets do not hold")
    footprints = _word(adjust_footprints, "NO_ADJUST_FOOTPRINTS") in ("ADJUST_FOOTPRINTS", "TRUE")
    connection, mosaic, rec, models, items = _ortho._frame_items(in_mosaic_dataset, tool)
    try:
        if operation == "REACTIVATE":
            AddMessage("No item was dropped by grapy's block adjustment; nothing to reactivate.")
        elif operation == "ADJUST":
            if not _given(input_solution_table):
                raise ExecuteError("ERROR 000735: Input Solution Table: Value is required for ADJUST.")
            solution = _solution(input_solution_table)
            for i, model in models.items():
                s = solution.get(i)
                if s is None:
                    continue
                adjusted = _with(model, [s["PerspectiveX"], s["PerspectiveY"], s["PerspectiveZ"], s["Omega"], s["Phi"],
                                         s["Kappa"]], [s["FocalLength"], s["PrincipalX"], s["PrincipalY"]])
                _ortho.save_model(connection, mosaic, i, adjusted)
                models[i] = adjusted
        else:
            for i in models:
                original = connection.execute("select original from grapy_ortho_item where mosaic = ? and item = ?",
                                              (mosaic, i)).fetchone()[0]
                models[i] = _ortho.FrameModel(json.loads(original))
                _ortho.save_model(connection, mosaic, i, models[i])
        if footprints:
            from ._mosaictools import _update
            for i, model in models.items():
                shape = _ortho.footprint(model)
                _update(connection, mosaic, rec.srid, i, shape, {"CenterX": model.X, "CenterY": model.Y})
        if _given(DEM) or _given(zoffset):
            properties = dict(rec.properties)
            if _given(DEM):
                properties["orthoDEM"] = _mosaic.text(DEM)
            if _given(zoffset):
                properties["orthoZOffset"] = float(zoffset)
            _mosaic.save_properties(connection, mosaic, properties)
        connection.commit()
    finally:
        connection.close()
    if _given(control_point_table) and _given(solution_point_table) and operation == "ADJUST":
        from . import da
        solved = {}
        with da.SearchCursor(_mosaic.text(solution_point_table), ["PointID", "Status", "SHAPE@"]) as rows:
            for pid, status, shape in rows:
                solved[int(pid)] = (status, shapely.get_coordinates(shape._shape, include_z=True)[0])
        move = _word(adjust_tiepoints, "NO_ADJUST_TIEPOINTS") in ("ADJUST_TIEPOINTS", "TRUE")
        with da.UpdateCursor(_mosaic.text(control_point_table), ["PointID", "Type", "Status", "SHAPE@"]) as rows:
            for pid, kind, status, shape in rows:
                if int(pid) not in solved:
                    continue
                new_status, xyz = solved[int(pid)]
                new_shape = shapely.Point(*xyz) if move and int(kind or TIE) == TIE else shape
                rows.updateRow([pid, kind, new_status, new_shape])
    return Result(_mosaic.text(in_mosaic_dataset))


def GenerateBlockAdjustmentReport(input_mosaic_dataset, input_solution_table, input_solution_point, output_report,
                                  input_control_point_for_adjustment=None, report_format="HTML"):
    """A report of a block adjustment: the images, their adjusted orientation and residuals, and the points; with the
    control points, the accuracy of each ground control and check point — the ground distance between its known
    position and the intersection of its image rays under the solution table's orientation — as documented. ``HTML`` (the
    default), ``JSON`` and ``BRIEF JSON``.

    Refused: ``PDF`` — grapy carries no PDF writer.

    INFERRED (ADR-234 §5.9 (k)): the report's sections and keys (``summary``, ``images``, ``controlPoints``), which the
    page does not list; BRIEF JSON is the summary alone."""
    tool = "GenerateBlockAdjustmentReport"
    kind = _word(report_format, "HTML").replace("_", " ")
    if kind == "PDF":
        _refuse(tool, "report_format", report_format, "grapy carries no PDF writer")
    if kind not in ("HTML", "JSON", "BRIEF JSON"):
        _refuse(tool, "report_format", report_format)
    connection, mosaic, rec, models, items = _ortho._frame_items(input_mosaic_dataset, tool)
    connection.close()
    solution = _solution(input_solution_table)
    for i, s in solution.items():   # the accuracy is the solution's, applied or not
        if i in models:
            models[i] = _with(models[i], [s["PerspectiveX"], s["PerspectiveY"], s["PerspectiveZ"], s["Omega"], s["Phi"],
                                          s["Kappa"]], [s["FocalLength"], s["PrincipalX"], s["PrincipalY"]])
    from . import da
    with da.SearchCursor(_mosaic.text(input_solution_point), [n for n, _ in _SOLUTION_POINT_FIELDS]) as rows:
        solved = [dict(zip([n for n, _ in _SOLUTION_POINT_FIELDS], r)) for r in rows]
    residuals = [p["Residual"] for p in solved if p["Residual"] is not None and p["Status"]]
    summary = {"mosaicDataset": mosaic, "images": len(solution), "points": len(solved),
               "pointsUsed": sum(1 for p in solved if p["Status"]),
               "rmsePixels": float(np.sqrt(np.mean(np.square(residuals)))) if residuals else None}
    report = {"summary": summary}
    if kind != "BRIEF JSON":
        report["images"] = [{"imageId": i, **{k: v for k, v in s.items() if k != "ImageID"}}
                            for i, s in sorted(solution.items())]
        if _given(input_control_point_for_adjustment):
            accuracy = []
            records = _ortho.read_points(_mosaic.text(input_control_point_for_adjustment))
            for pid, rs in _ortho.points_by_id(records).items():
                kind_of = int(rs[0]["Type"] or TIE)
                seen = [r for r in rs if r.get("ImageID") is not None and int(r["ImageID"]) in models]
                if kind_of == TIE or len(seen) < 2:
                    continue
                at = _ortho.intersect([models[int(r["ImageID"])] for r in seen], [(r["ImageX"], r["ImageY"]) for r in seen])
                dx, dy, dz = (np.array(at) - np.array(rs[0]["xyz"])).tolist()
                accuracy.append({"pointId": pid, "type": "GCP" if kind_of == GCP else "CHECK", "dx": dx, "dy": dy,
                                 "dz": dz})
            report["controlPoints"] = accuracy
            for t in ("GCP", "CHECK"):
                chosen = [a for a in accuracy if a["type"] == t]
                if chosen:
                    summary[f"{t.lower()}RmseXY"] = float(np.sqrt(np.mean([a["dx"] ** 2 + a["dy"] ** 2 for a in chosen])))
                    summary[f"{t.lower()}RmseZ"] = float(np.sqrt(np.mean([a["dz"] ** 2 for a in chosen])))
    path = _mosaic.text(output_report)
    with open(path, "w", encoding="utf-8") as written:
        if kind == "HTML":
            written.write(_html(report))
        else:
            json.dump(report, written, indent=2)
    return Result(path)


def _html(report):
    from html import escape

    def table(rows):
        if not rows:
            return "<p>None.</p>"
        keys = list(rows[0])
        head = "".join(f"<th>{escape(str(k))}</th>" for k in keys)
        body = "".join("<tr>" + "".join(f"<td>{escape(str(r.get(k)))}</td>" for k in keys) + "</tr>" for r in rows)
        return f"<table><tr>{head}</tr>{body}</table>"
    parts = ["<!doctype html><html><head><meta charset='utf-8'><title>Block adjustment report</title></head><body>",
             "<h1>Block adjustment report</h1>", "<h2>Summary</h2>", table([report["summary"]])]
    if "images" in report:
        parts += ["<h2>Images</h2>", table(report["images"])]
    if "controlPoints" in report:
        parts += ["<h2>Control points</h2>", table(report["controlPoints"])]
    return "".join(parts) + "</body></html>"


def ExportFrameAndCameraParameters(input_mosaic_dataset, output_file, output_format="ESRI_FRAME_AND_CAMERA_TABLE"):
    """The orientation of every Frame Camera item written to a ``.csv`` (or ``.txt``) file in the frames and cameras
    table schemas, as the page says: one row per frame with the frames table's fields and its camera's beside them —
    the frames table page letting a frame's own camera fields stand in for the cameras table's.

    Refused: ``PIX4D_CALIBRATED_CAMERA_PARAMETERS`` — the page does not describe the file, Pix4D's own format."""
    import csv
    tool = "ExportFrameAndCameraParameters"
    kind = _word(output_format, "ESRI_FRAME_AND_CAMERA_TABLE")
    if kind != "ESRI_FRAME_AND_CAMERA_TABLE":
        _refuse(tool, "output_format", output_format, "the page does not describe the file, which is Pix4D's format")
    path = _mosaic.text(output_file)
    if not path.lower().endswith((".csv", ".txt")):
        raise ExecuteError("ERROR: the output file is a .csv or .txt file, as documented.")
    connection, mosaic, rec, models, items = _ortho._frame_items(input_mosaic_dataset, tool)
    connection.close()
    columns = ["ObjectID", "Raster", "Name", "CameraID", "PerspectiveX", "PerspectiveY", "PerspectiveZ", "Omega", "Phi",
               "Kappa", "SRS", "FocalLength", "PrincipalX", "PrincipalY", "PixelSize", "NRows", "NColumns", "AverageZ",
               "AngleDirection", "Polarity", "Radial", "Tangential", "A0", "A1", "A2", "B0", "B1", "B2"]
    with open(path, "w", newline="", encoding="utf-8") as handle:
        written = csv.writer(handle)
        written.writerow(columns)
        for i, m in sorted(models.items()):
            d = m.data
            affine = d.get("affine") or [""] * 6
            written.writerow([i, d.get("raster"), d.get("name"), d.get("camera_id"), m.X, m.Y, m.Z, m.omega, m.phi,
                              m.kappa, rec.srid or "", m.f, m.x0, m.y0, m.ps, m.rows, m.columns, m.average_z,
                              d.get("angle_direction", -1), d.get("polarity", -1),
                              ";".join(str(v) for v in d.get("radial") or []),
                              ";".join(str(v) for v in d.get("tangential") or []), *affine])
    return Result(path)


def BuildStereoModel(in_mosaic_dataset, minimum_angle=10, maximum_angle=90, minimum_overlap=0.5, maximum_diff_OP=None,
                     maximum_diff_GSD=2, group_by=None, same_flight="SAMEFLIGHT"):
    """Stereo pairs of a mosaic dataset's Frame Camera items, kept in a table of the mosaic dataset as the page says —
    grapy's ``<mosaic>_Stereo`` polygon feature table: each pair whose intersection angle lies between the minimum and
    maximum, whose overlap is at least ``minimum_overlap`` of the image, whose Omega and Phi differ by no more than
    ``maximum_diff_OP``, whose GSD ratio is at most ``maximum_diff_GSD``, in one group when ``group_by`` names a field,
    and in one flight line under ``SAMEFLIGHT`` — the page's checks and defaults.

    INFERRED (ADR-234 §5.9 (l)): the intersection angle is the angle between the two rays to the centroid of the overlap
    at the items' AverageZ; the overlap is the overlap's area over each image's footprint, both meeting the minimum; a
    flight line is the frames table's StripName (all one line where none is given); the table's fields ``ImageID1``,
    ``ImageID2``, ``IntersectionAngle``, ``Overlap`` (the smaller share), ``GSDRatio``, ``Use`` (0), ordered by overlap
    then angle, larger first, as GeneratePointCloud ranks pairs."""
    tool = "BuildStereoModel"
    connection, mosaic, rec, models, items = _ortho._frame_items(in_mosaic_dataset, tool)
    connection.close()
    low = float(minimum_angle) if _given(minimum_angle) else 10.0
    high = float(maximum_angle) if _given(maximum_angle) else 90.0
    share = float(minimum_overlap) if _given(minimum_overlap) else 0.5
    op = float(maximum_diff_OP) if _given(maximum_diff_OP) else None
    ratio_limit = float(maximum_diff_GSD) if _given(maximum_diff_GSD) else 2.0
    same = _word(same_flight, "SAMEFLIGHT") in ("SAMEFLIGHT", "TRUE")
    group_values = {}
    if _given(group_by):
        connection2, _, _, _ = _mosaic.open_mosaic(in_mosaic_dataset, tool)
        try:
            group_values = {i["OBJECTID"]: i.get(str(group_by)) for i in _mosaic.items(connection2, mosaic, None,
                                                                                        [str(group_by)])}
        finally:
            connection2.close()
    pairs = []
    ids = sorted(models)
    for a, i in enumerate(ids):
        for j in ids[a + 1:]:
            mi, mj = models[i], models[j]
            if same and (mi.data.get("strip") or "") != (mj.data.get("strip") or ""):
                continue
            if group_values and group_values.get(i) != group_values.get(j):
                continue
            region = items[i]["shape"].intersection(items[j]["shape"])
            if region.is_empty or region.area <= 0:
                continue
            fraction = min(region.area / items[i]["shape"].area, region.area / items[j]["shape"].area)
            if fraction < share:
                continue
            c = region.centroid
            g = np.array([c.x, c.y, mi.average_z])
            u, v = np.array([mi.X, mi.Y, mi.Z]) - g, np.array([mj.X, mj.Y, mj.Z]) - g
            angle = math.degrees(math.acos(max(-1.0, min(1.0, float(u @ v / (np.linalg.norm(u) * np.linalg.norm(v)))))))
            if not low <= angle <= high:
                continue
            if op is not None and (abs(mi.omega - mj.omega) > op or abs(mi.phi - mj.phi) > op):
                continue
            gi, gj = mi.gsd(), mj.gsd()
            gsd_ratio = max(gi, gj) / min(gi, gj)
            if gsd_ratio > ratio_limit:
                continue
            pairs.append((i, j, angle, fraction, gsd_ratio, 0, shapely.MultiPolygon(
                [p for p in getattr(region, "geoms", [region]) if p.area > 0])))
    pairs.sort(key=lambda p: (-p[3], -p[2], p[0], p[1]))
    from ._mosaic import catalog_path
    file = _gpkg.split(catalog_path(in_mosaic_dataset))[0]
    from ._tintools import _write
    _write(f"{file}/{mosaic}_Stereo", "Polygon", rec.srid,
           [_gpkg.Column("ImageID1", "Integer"), _gpkg.Column("ImageID2", "Integer"),
            _gpkg.Column("IntersectionAngle", "Double"), _gpkg.Column("Overlap", "Double"),
            _gpkg.Column("GSDRatio", "Double"), _gpkg.Column("Use", "Integer")], False, pairs)
    AddMessage(f"{len(pairs)} stereo pairs in {mosaic}_Stereo.")
    return Result(_mosaic.text(in_mosaic_dataset))


# ---- orthorectification


def _elevation(kind, constant, dem, zfactor, zoffset, tool):
    """A function giving heights at ground x and y (in the DEM's reference)."""
    zf = float(zfactor) if _given(zfactor) else 1.0
    zo = float(zoffset) if _given(zoffset) else 0.0
    if kind == "CONSTANT_ELEVATION":
        if not _given(constant):
            raise ExecuteError("ERROR 000735: Constant Elevation: Value is required for CONSTANT_ELEVATION.")
        value = float(constant)
        return lambda x, y: np.full(np.shape(x), value)
    if not _given(dem):
        raise ExecuteError("ERROR 000735: DEM: Value is required for the DEM orthorectification type.")
    from ._raster import Raster
    raster = Raster(_mosaic.text(dem))
    array = np.where(raster._mask, np.nan, raster._array.astype(np.float64))
    g = raster._grid

    def heights(x, y):
        col, row = (np.asarray(x) - g.left) / g.width, (g.top - np.asarray(y)) / g.height
        return _ortho.sample(array, col, row) * zf + zo
    return heights


def _out_grid(boxes, cell, srid):
    from ._raster import _Grid
    left, bottom = min(b[0] for b in boxes), min(b[1] for b in boxes)
    right, top = max(b[2] for b in boxes), max(b[3] for b in boxes)
    left, bottom = math.floor(left / cell) * cell, math.floor(bottom / cell) * cell
    columns, rows = max(1, int(math.ceil((right - left) / cell - 1e-9))), max(1, int(math.ceil((top - bottom) / cell - 1e-9)))
    return _Grid(left, bottom + rows * cell, cell, cell, rows, columns, srid)


def _env_cell():
    value = env.cellSize
    if not _is_set(value, "MAXOF", "MINOF"):
        return None
    return float(value)


def _resampling(tool):
    method = str(getattr(env, "resamplingMethod", None) or "NEAREST").upper()
    if method not in ("NEAREST", "BILINEAR"):
        raise NotBuiltError(f"arcpy.management.{tool}: the resampling method '{method}' is not built in grapy; NEAREST "
                            "and BILINEAR are.")
    return _ortho.nearest if method == "NEAREST" else _ortho.sample


def CreateOrthoCorrectedRasterDataset(in_raster, out_raster_dataset, Ortho_type, constant_elevation=None,
                                      in_DEM_raster=None, ZFactor=None, ZOffset=None, Geoid="NONE"):
    """An orthorectified raster: each output cell's ground point, at the constant elevation or the DEM's height (times
    ``ZFactor`` plus ``ZOffset``, as documented), projected into the image by its sensor model and the image's value
    taken there. The input is a raster file with RPCs, or a mosaic dataset of Frame Camera items — each cell from the
    item whose perspective centre is nearest it (Closest to Nadir) among those that see it.

    The output's reference is the mosaic dataset's for frames, and WGS 84 for RPCs (INFERRED: the RPC's own system; the
    output coordinate system environment is not read); its cell size is the cell size environment, else the finest
    image's ground sample distance (INFERRED); its extent the images' footprints; NEAREST resampling, or BILINEAR when
    the resampling method environment says so; cells no image sees are NoData.

    Refused: ``Geoid`` GEOID — grapy carries no EGM96 grid; a raster without RPCs and a mosaic dataset without Frame
    Camera items, which have no sensor model."""
    from ._raster import Raster
    tool = "CreateOrthoCorrectedRasterDataset"
    if _word(Geoid, "NONE") not in ("NONE", "FALSE"):
        _refuse(tool, "Geoid", Geoid, "grapy carries no EGM96 geoid grid")
    kind = _word(Ortho_type, "")
    if kind not in ("CONSTANT_ELEVATION", "DEM"):
        _refuse(tool, "Ortho_type", Ortho_type)
    height = _elevation(kind, constant_elevation, in_DEM_raster, ZFactor, ZOffset, tool)
    pick = _resampling(tool)
    text = _mosaic.text(in_raster)
    cell = _env_cell()
    if _mosaic.exists(in_raster):
        connection, mosaic, rec, models, items = _ortho._frame_items(in_raster, tool)
        connection.close()
        ids = sorted(models)
        cell = cell or min(models[i].gsd() for i in ids)
        grid = _out_grid([items[i]["shape"].bounds for i in ids], cell, rec.srid)
        xs, ys = grid.centres()
        gx, gy = np.meshgrid(xs, ys)
        z = height(gx, gy)
        bands = None
        best = np.full(gx.shape, np.inf)
        for i in ids:
            m = models[i]
            data = _bands(items[i]["Raster"])
            if bands is None:
                bands = [np.full(gx.shape, np.nan) for _ in data]
            col, row = m.project(gx, gy, z)
            seen = (col >= 0) & (row >= 0) & (col <= m.columns) & (row <= m.rows)
            distance = np.hypot(gx - m.X, gy - m.Y)
            take = seen & (distance < best)
            for b, band in enumerate(data):
                values = pick(band, col, row)
                bands[b] = np.where(take & np.isfinite(values), values, bands[b])
            best = np.where(take, distance, best)
        dtype = np.dtype(_mosaic.PIXEL_TYPES.get(rec.pixel_type, "float32"))
    else:
        model = _ortho.rpc_of(text)
        if model is None:
            _refuse(tool, "in_raster", text, "it carries no RPCs and is no mosaic dataset of Frame Camera items, so it "
                    "has no sensor model")
        import rasterio
        with rasterio.open(text) as source:
            data = [b.astype(np.float64) for b in source.read()]
            dtype = np.dtype(source.dtypes[0])
        cols = np.array([0, model.columns, model.columns, 0], float)
        rows = np.array([0, 0, model.rows, model.rows], float)
        z0 = float(np.nanmean(height(np.array([model.rpc.long_off]), np.array([model.rpc.lat_off]))))
        lon, lat = model.to_ground(cols, rows, z0)
        if cell is None:
            lo2, la2 = model.to_ground(np.array([model.columns / 2 + 1]), np.array([model.rows / 2 + 1]), z0)
            lo1, la1 = model.to_ground(np.array([model.columns / 2]), np.array([model.rows / 2]), z0)
            cell = float(max(abs(lo2[0] - lo1[0]), abs(la2[0] - la1[0])))
        grid = _out_grid([(lon.min(), lat.min(), lon.max(), lat.max())], cell, 4326)
        xs, ys = grid.centres()
        gx, gy = np.meshgrid(xs, ys)
        h = height(gx, gy)
        col, row = model.project(gx, gy, h)
        seen = (col >= 0) & (row >= 0) & (col <= model.columns) & (row <= model.rows)
        bands = [np.where(seen, pick(band, col, row), np.nan) for band in data]
    masks = [~np.isfinite(b) for b in bands]
    if np.issubdtype(dtype, np.integer):
        arrays = [np.where(m, 0, np.rint(b)).astype(dtype) for b, m in zip(bands, masks)]
    else:
        arrays = [np.where(m, 0, b).astype(dtype) for b, m in zip(bands, masks)]
    raster = Raster._of_bands(arrays, masks, grid) if len(arrays) > 1 else Raster._of(arrays[0], masks[0], grid)
    return Result(raster.save(_mosaic.text(out_raster_dataset)))


def _bands(path):
    import rasterio
    import warnings
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        with rasterio.open(path) as source:
            return [b.astype(np.float64) for b in source.read()]

