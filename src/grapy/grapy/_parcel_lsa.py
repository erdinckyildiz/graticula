"""The least-squares adjustment of a parcel fabric's dimensions — ADR-250 §5.4.

The tool pages describe what goes into the adjustment (distances and direction sets from the lines' COGO dimensions,
fixed and weighted points), what comes out (the AdjustmentPoints, AdjustmentLines and AdjustmentVectors classes and
their fields) and which engine Esri runs (DynAdjust). The arithmetic here is the published one of a parametric
least-squares adjustment of a 2D network: Ghilani, C. D. and Wolf, P. R., *Adjustment Computations: Spatial Data
Analysis*, 5th ed., Wiley 2010 — observation equations of distances and directions with an orientation unknown per
direction set (ch. 14–16), weights as inverse variances (ch. 10), iteration to convergence (ch. 11), free networks by
weak constraints (ch. 20), error ellipses (ch. 19) and data snooping with standardized residuals (ch. 21, after
Baarda); Pelzer's reliability and Pope's tau as the DynAdjust user guide (Geoscience Australia, Apache-2.0) names them.

Measured beside ArcGIS Pro 3.7 on 2026-10-31 (ADR-250 condition 2, ``tools/pro-compare/adr250-parcel.py``), and
followed where its outputs pin a rule: only an Adjustment Constraint of XYZ constrained makes a control point (Fixed
Shape does not), and a weighted point counts as one; at most 15 iterations; a direction set is adjusted with its
orientation and reported as Pro reports it — sorted by azimuth from its backsight (the direction of smallest azimuth
at the point), the backsight's row carrying nothing adjusted and each other row the angle from the direction before
it (its correction the difference of the two directions' corrections, its standard deviation sqrt(2) times a
direction's); the degrees of freedom are measurements (distances, a set's directions less one, weighted points'
coordinates) less unknown coordinates; a free network is held by every point weighted at 10 m (so its ellipses are of
that size); the points' standard deviations and ellipses are a-priori (not scaled by the variance factor), the
ellipses at 95 % (2.4477 sigma), and XY Uncertainty is the radius of the 95 % circle; Z is held at 5 mm (StdDevUp
0.005, ZUncertainty 1.96 times it). On the cases measured the coordinates and every correction agree with Pro to
0.1 mm and 0.1 second; the precisions to about 1 %.
"""
from __future__ import annotations

import math

import numpy
from shapely.geometry import LineString, Point

from . import _parcel as pf
from ._runtime import AddMessage, AddWarning, ExecuteError, NotBuiltError

SECONDS = math.pi / (180.0 * 3600.0)
CHI95_2D = math.sqrt(5.991464547107979)   # the 95% quantile of chi-square with 2 degrees of freedom, square-rooted
MAX_ITERATIONS = 15          # Pro 3.7's "Maximum iterations: 15" (measured)
FREE_SIGMA = 10.0            # a free network's points held at 10 m (Pro 3.7's ellipses, measured)
Z_SIGMA = 0.005              # Z held at 5 mm: StdDevUp 0.005, ZUncertainty 0.0098 (Pro 3.7, measured)
ELLIPSE_95 = CHI95_2D        # the 95 % ellipse's semi-axes are 2.4477 sigma


class _Station:
    def __init__(self, point, xy):
        self.point = point
        self.xy = xy
        self.index = None


def _start(point):
    x, y = point.get_ci("X"), point.get_ci("Y")
    if x is not None and y is not None:
        return float(x), float(y)
    return float(point.shape.x), float(point.shape.y)


def _constrained(point):
    """XYZ constrained only: Pro 3.7 reports "XYZ point constraints not found" for points whose Fixed Shape is Yes
    (measured 2026-10-31)."""
    return point.get_ci("AdjustmentConstraint") == pf.XYZ_CONSTRAINED


def _weighted(point):
    return not _constrained(point) and point.get_ci("XYAccuracy") not in (None, 0)


def _ground_to_grid(line, distance, direction):
    if line.get_ci("IsCOGOGround") == pf.YES:
        scale, rotation = line.get_ci("Scale"), line.get_ci("Rotation")
        if distance is not None and scale is not None:
            distance *= scale
        if direction is not None and rotation is not None:
            direction += rotation
    return distance, direction


def _chord(line):
    """A line's measured length: its Distance, or for a curve with an arc length the chord its radius gives."""
    radius, arc = line.get_ci("Radius"), line.get_ci("ArcLength")
    if radius not in (None, 0) and arc is not None:
        r = abs(float(radius))
        return 2.0 * r * math.sin(float(arc) / (2.0 * r))
    distance = line.get_ci("Distance")
    return None if distance is None else float(distance)


def analyze(fab, lines, analysis_type, tolerance):
    """The adjustment of the given lines ((class, Feature) pairs); writes the three adjustment classes."""
    from ._parcel_build import Snap
    for table, line in lines:
        kind = line.get_ci("AzimuthType")
        if kind is not None and int(kind) in (2, 3, 4, 5):
            raise NotBuiltError("arcpy.parcel.AnalyzeParcelsByLeastSquaresAdjustment: lines with an AzimuthType of 2 to "
                                "5 (geodetic azimuths and ellipsoid arc distances) are not built in grapy; its "
                                "adjustment is in the grid of the fabric's coordinate system.")
    snap = Snap(fab.tolerance)
    stations = {}
    points = [p for p in pf.read(fab.file, fab.points) if p.shape is not None]
    points.sort(key=lambda p: (not pf.current(p), p.oid))
    for point in points:
        place = snap.add((point.shape.x, point.shape.y))
        stations.setdefault(place, _Station(point, _start(point)))
    used, missing = {}, 0
    measured = []
    for table, line in lines:
        a, b = pf.ends(line.shape)
        sa, sb = stations.get(snap.find(a)), stations.get(snap.find(b))
        if sa is None or sb is None or sa is sb:
            missing += 1
            continue
        used[id(sa)], used[id(sb)] = sa, sb
        measured.append((table, line, sa, sb))
    if missing:
        AddWarning(f"WARNING: {missing} lines have no parcel point at an end and are left out of the adjustment; "
                   "build the fabric (BuildParcelFabric) to make their points.")
    order = sorted(used.values(), key=lambda s: s.point.oid)
    for i, station in enumerate(order):
        station.index = i
    weighted = analysis_type == "WEIGHTED_LEAST_SQUARES"
    controls = [s for s in order if weighted and (_constrained(s.point) or _weighted(s.point))]
    if weighted and not any(_constrained(s.point) for s in order):
        AddMessage("XYZ point constraints not found.")
    if not order:
        raise ExecuteError("ERROR 160207: AnalyzeParcelsByLeastSquaresAdjustment: no line with points at both ends.")
    default_distance = pf.linear("0.15 Meters", fab.srid)
    default_direction = 30.0
    five_mm = pf.linear("0.005 Meters", fab.srid)
    # observations
    observations = []   # dicts: kind, stations, value, sigma (radians for directions), set, line, table
    for table, line, sa, sb in measured:
        distance, _ = _ground_to_grid(line, _chord(line), None)
        if distance is not None and distance > 0:
            sigma = line.get_ci("DistanceAccuracy") or default_distance
            observations.append({"kind": pf.DISTANCE, "from": sa, "to": sb, "value": float(distance),
                                 "sigma": float(sigma), "line": line, "table": table})
    sets = {}
    for table, line, sa, sb in measured:
        direction = line.get_ci("Direction")
        if direction is None:
            continue
        _, direction = _ground_to_grid(line, None, float(direction))
        sigma = (line.get_ci("DirectionAccuracy") or default_direction) * SECONDS
        record = str(line.get_ci("CreatedByRecord") or "").upper()
        sets.setdefault((id(sa), record), []).append((sa, sb, direction % 360.0, sigma, line, table))
        sets.setdefault((id(sb), record), []).append((sb, sa, (direction + 180.0) % 360.0, sigma, line, table))
    orientation = 0
    for members in sets.values():
        if len(members) < 2:
            continue
        # sorted by azimuth: Pro 3.7's backsight is the direction of smallest azimuth at the point, and its angles run
        # from each direction to the next (measured on four stations)
        members.sort(key=lambda m: (m[2], m[4].oid))
        backsight = members[0]
        for rank, (origin, target, value, sigma, line, table) in enumerate(members):
            observations.append({"kind": pf.DIRECTION_SET, "from": origin, "to": target, "value": value,
                                 "sigma": sigma, "line": line, "table": table, "set": orientation, "rank": rank,
                                 "backsight": backsight[1]})
        orientation += 1
    if weighted:
        for station in controls:
            sigma = five_mm if _constrained(station.point) else float(station.point.get_ci("XYAccuracy"))
            x, y = _start(station.point)
            observations.append({"kind": "x", "from": station, "value": x, "sigma": sigma})
            observations.append({"kind": "y", "from": station, "value": y, "sigma": sigma})
    if not observations:
        raise ExecuteError("ERROR 160207: AnalyzeParcelsByLeastSquaresAdjustment: the lines carry no distances or "
                           "directions to adjust.")
    n_measurements = sum(1 for o in observations if o["kind"] == pf.DISTANCE) + \
        sum(1 for o in observations if o["kind"] == pf.DIRECTION_SET) - orientation + \
        sum(1 for o in observations if o["kind"] in ("x", "y") and not _constrained(o["from"].point))
    free = not weighted
    # Pro 3.7 holds a free network by every point at 10 m (its ellipses on the measured square are the inner-constraint
    # ones plus that datum's), and runs a weighted adjustment with fewer than two control points the same way, its
    # free points so held (measured: no error, ellipses of 17 m).
    if free or len(controls) < 2:
        for station in order:
            if station in controls:
                continue
            x0, y0 = station.xy
            observations.append({"kind": "x", "from": station, "value": x0, "sigma": FREE_SIGMA, "datum": True})
            observations.append({"kind": "y", "from": station, "value": y0, "sigma": FREE_SIGMA, "datum": True})
    n_coordinates = 2 * len(order)
    n_unknowns = n_coordinates + orientation
    x = numpy.zeros(n_unknowns)
    for station in order:
        x[2 * station.index], x[2 * station.index + 1] = station.xy
    # initial orientations: the mean difference of computed azimuth and measured direction
    for k in range(orientation):
        members = [o for o in observations if o.get("set") == k]
        diffs = [((math.degrees(_azimuth(x, o["from"], o["to"])) - o["value"] + 180.0) % 360.0 - 180.0) for o in members]
        x[n_coordinates + k] = math.radians(sum(diffs) / len(diffs))
    weights = numpy.array([1.0 / (o["sigma"] ** 2) for o in observations])
    converged = False
    for _ in range(MAX_ITERATIONS):
        a, misclosure = _linearize(x, observations, n_unknowns, n_coordinates)
        dx, rank, nullity = _solve(a, weights, misclosure, n_coordinates, False)
        x += dx
        if numpy.max(numpy.abs(dx[:n_coordinates])) < tolerance:
            converged = True
            break
    if not converged:
        AddWarning(f"WARNING: the adjustment did not converge to {tolerance} in {MAX_ITERATIONS} iterations.")
    a, misclosure = _linearize(x, observations, n_unknowns, n_coordinates)
    computed = _computed(x, observations, n_coordinates)
    # Pro 3.7's degrees of freedom: measurements less unknown coordinates (a constrained point's are not unknown, a
    # weighted point's coordinates are measurements; a free network's datum is not counted).
    unknown = 2 * sum(1 for s in order if not (weighted and _constrained(s.point)))
    redundancy = n_measurements - unknown
    corrections = numpy.array([_difference(o, computed[i]) for i, o in enumerate(observations)])
    measured_rows = numpy.array([not o.get("datum") and not (o["kind"] in ("x", "y") and _constrained(o["from"].point))
                                 for o in observations])
    vpv = float(numpy.sum((weights * corrections ** 2)[measured_rows]))
    variance = vpv / redundancy if redundancy > 0 else 1.0
    sigma0 = math.sqrt(variance)
    qxx = _cofactor(a, weights, n_coordinates, False)
    AddMessage(f"SOLUTION {'Converged' if converged else 'did not converge'}")
    AddMessage(f"Number of unknown parameters: {unknown}")
    AddMessage(f"Number of measurements: {n_measurements}")
    AddMessage(f"Degrees of freedom: {redundancy}")
    AddMessage(f"Standard Error (Sigma): {sigma0:.3f}")
    _write(fab, order, observations, computed, corrections, a, qxx, sigma0, weighted, x)
    return sigma0, redundancy


def _azimuth(x, a, b):
    dx = x[2 * b.index] - x[2 * a.index]
    dy = x[2 * b.index + 1] - x[2 * a.index + 1]
    return math.atan2(dx, dy)


def _computed(x, observations, n_coordinates):
    out = []
    for o in observations:
        if o["kind"] == pf.DISTANCE:
            a, b = o["from"], o["to"]
            out.append(math.hypot(x[2 * b.index] - x[2 * a.index], x[2 * b.index + 1] - x[2 * a.index + 1]))
        elif o["kind"] == pf.DIRECTION_SET:
            out.append(math.degrees(_azimuth(x, o["from"], o["to"]) - x[n_coordinates + o["set"]]) % 360.0)
        elif o["kind"] == "x":
            out.append(x[2 * o["from"].index])
        else:
            out.append(x[2 * o["from"].index + 1])
    return out


def _difference(o, computed):
    """Adjusted minus measured, in the observation's own unit of weight: radians for a direction."""
    if o["kind"] == pf.DIRECTION_SET:
        return math.radians((computed - o["value"] + 180.0) % 360.0 - 180.0)
    return computed - o["value"]


def _linearize(x, observations, n_unknowns, n_coordinates):
    a = numpy.zeros((len(observations), n_unknowns))
    misclosure = numpy.zeros(len(observations))
    computed = _computed(x, observations, n_coordinates)
    for row, o in enumerate(observations):
        if o["kind"] in ("x", "y"):
            a[row, 2 * o["from"].index + (0 if o["kind"] == "x" else 1)] = 1.0
        else:
            i, j = o["from"].index, o["to"].index
            dx = x[2 * j] - x[2 * i]
            dy = x[2 * j + 1] - x[2 * i + 1]
            s2 = dx * dx + dy * dy
            if o["kind"] == pf.DISTANCE:
                s = math.sqrt(s2)
                a[row, 2 * i], a[row, 2 * i + 1], a[row, 2 * j], a[row, 2 * j + 1] = -dx / s, -dy / s, dx / s, dy / s
            else:
                a[row, 2 * i], a[row, 2 * i + 1] = -dy / s2, dx / s2
                a[row, 2 * j], a[row, 2 * j + 1] = dy / s2, -dx / s2
                a[row, n_coordinates + o["set"]] = -1.0
        misclosure[row] = -_difference(o, computed[row])
    return a, misclosure


def _null_space(a, weights):
    scaled = a * numpy.sqrt(weights)[:, None]
    _, s, vt = numpy.linalg.svd(scaled)
    limit = (s[0] if len(s) else 0.0) * max(scaled.shape) * numpy.finfo(float).eps * 1e3
    rank = int(numpy.sum(s > limit))
    return vt[rank:].T


def _inner(g, n_coordinates):
    """The projector that removes from a solution its part along the datum defect ``g`` measured on the coordinates
    only — the inner-constraint solution (Ghilani and Wolf, ch. 20)."""
    if g.shape[1] == 0:
        return None
    gc = g[:n_coordinates]
    e = numpy.zeros((g.shape[0], g.shape[0]))
    e[:n_coordinates, :n_coordinates] = numpy.eye(n_coordinates)
    return numpy.eye(g.shape[0]) - g @ numpy.linalg.pinv(gc.T @ gc) @ gc.T @ e[:n_coordinates]


def _solve(a, weights, misclosure, n_coordinates, free):
    scaled = a * numpy.sqrt(weights)[:, None]
    rhs = misclosure * numpy.sqrt(weights)
    dx, _, rank, _ = numpy.linalg.lstsq(scaled, rhs, rcond=None)
    g = _null_space(a, weights)
    if g.shape[1]:
        if not free:
            raise ExecuteError("ERROR 160207: AnalyzeParcelsByLeastSquaresAdjustment: the network is not fixed by its "
                               "control points (a part of it connects to fewer than two of them).")
        dx = _inner(g, n_coordinates) @ dx
    return dx, rank, g.shape[1]


def _cofactor(a, weights, n_coordinates, free):
    normal = a.T @ (a * weights[:, None])
    q = numpy.linalg.pinv(normal)
    if free:
        g = _null_space(a, weights)
        t = _inner(g, n_coordinates)
        if t is not None:
            q = t @ q @ t.T
    return q


def circle_95(major, minor):
    """The radius of the circle holding 95 % of a bivariate normal with these standard deviations along its axes: the
    probability (1 / 2 pi a b) times the integral over the angle of (1 - exp(-R^2 q / 2)) / q, q = cos^2/a^2 + sin^2/b^2,
    solved for R by bisection. Pro 3.7's XY Uncertainty is this radius to within 0.5 % on the cases measured; its own
    approximation is not published."""
    a, b = max(major, 0.0), max(minor, 0.0)
    if a <= 0:
        return 0.0
    if b <= a * 1e-9:
        return 1.959963984540054 * a
    theta = numpy.linspace(0.0, 2.0 * math.pi, 721)[:-1]
    q = numpy.cos(theta) ** 2 / a ** 2 + numpy.sin(theta) ** 2 / b ** 2

    def probability(r):
        return float(numpy.mean((1.0 - numpy.exp(-r * r * q / 2.0)) / q)) / (a * b)
    low, high = 1.959963984540054 * a, CHI95_2D * a
    for _ in range(60):
        mid = (low + high) / 2.0
        if probability(mid) < 0.95:
            low = mid
        else:
            high = mid
    return (low + high) / 2.0


def _ellipse(qxx, i, variance):
    sxx, syy, sxy = qxx[2 * i, 2 * i] * variance, qxx[2 * i + 1, 2 * i + 1] * variance, qxx[2 * i, 2 * i + 1] * variance
    mean, half = (sxx + syy) / 2.0, math.sqrt(max(((sxx - syy) / 2.0) ** 2 + sxy ** 2, 0.0))
    major, minor = math.sqrt(max(mean + half, 0.0)), math.sqrt(max(mean - half, 0.0))
    # the major axis's direction as a north azimuth (from the y axis, clockwise)
    angle = 0.5 * math.atan2(2.0 * sxy, syy - sxx)
    return major, minor, math.degrees(angle) % 180.0, math.sqrt(max(sxx, 0.0)), math.sqrt(max(syy, 0.0))


def _short(table):
    return table.rsplit("/", 1)[-1]


def _write(fab, order, observations, computed, corrections, a, qxx, sigma0, weighted, x):
    lines_table, points_table = fab.roles["adjustment_lines"], fab.roles["adjustment_points"]
    vectors_table = fab.roles["adjustment_vectors"]
    for table in (lines_table, points_table, vectors_table):
        pf.delete(fab.file, table, [f.oid for f in pf.read(fab.file, table)])

    def place(station):
        return x[2 * station.index], x[2 * station.index + 1]

    point_rows, vector_rows = [], []
    for station in order:
        point = station.point
        # a-priori, as Pro 3.7 writes them (measured: nonzero ellipses where the standard error is 0)
        major, minor, direction, sde, sdn = _ellipse(qxx, station.index, 1.0)
        constrained = weighted and _constrained(point)
        adjusted = place(station)
        z = point.get_ci("Z")
        point_rows.append({
            "Name": point.get_ci("Name"), "PointID": point.get_ci("GlobalID"),
            "Constrained": "CCC" if constrained else "FFC", "X": point.get_ci("X"), "Y": point.get_ci("Y"),
            "Z": z, "AdjustedX": adjusted[0], "AdjustedY": adjusted[1], "AdjustedZ": z if z is not None else 0.0,
            "XYAccuracy": point.get_ci("XYAccuracy"), "XYUncertainty": circle_95(major, minor),
            "ZAccuracy": point.get_ci("ZAccuracy"), "EllipseMajor": ELLIPSE_95 * major,
            "EllipseMinor": ELLIPSE_95 * minor, "EllipseDirection": direction,
            "ZUncertainty": 1.959963984540054 * Z_SIGMA, "StdDevE": sde, "StdDevN": sdn, "StdDevUp": Z_SIGMA,
            "UpdateSource": pf.YES if weighted else pf.NO, "shape": Point(adjusted)})
        before = (point.shape.x, point.shape.y)
        if not constrained and math.hypot(adjusted[0] - before[0], adjusted[1] - before[1]) > 0:
            vector_rows.append({"PointID": point.get_ci("GlobalID"), "shape": LineString([before, adjusted])})
    line_rows = []

    def statistics(row, v, sigma, gradient, to_report):
        """The statistics of a measurement whose adjusted value has the gradient ``gradient`` (a row of A, or the
        difference of two) and whose a-priori standard deviation is ``sigma``."""
        adjusted_sigma = math.sqrt(max(float(gradient @ qxx @ gradient), 0.0))
        correction_sigma = math.sqrt(max(sigma ** 2 - adjusted_sigma ** 2, 0.0))
        normal = v / correction_sigma if correction_sigma > 0 else None
        row.update({"MeasurementStdDev": to_report(sigma), "AdjustedStdDev": to_report(adjusted_sigma),
                    "Residual": to_report(correction_sigma), "NormalStatistic": normal,
                    "StudentStatistic": normal / sigma0 if normal is not None and sigma0 > 0 else None,
                    "PelzerReliablity": sigma / correction_sigma if correction_sigma > 0 else None,
                    "Outlier": pf.YES if normal is not None and abs(normal) > 1.96 else pf.NO})

    def base(o, origin, target):
        return {"PointID": origin.point.get_ci("GlobalID"), "LineID": o["line"].get_ci("GlobalID"),
                "Source": _short(o["table"]), "MeasurementType": o["kind"],
                "Point1Name": origin.point.get_ci("Name"), "Point2Name": target.point.get_ci("Name"),
                "Point3Name": None, "shape": LineString([place(origin), place(target)])}
    sets = {}
    for i, o in enumerate(observations):
        if o["kind"] == pf.DISTANCE:
            row = base(o, o["from"], o["to"])
            row.update({"Measurement": o["value"], "AdjustedMeasurement": computed[i],
                        "MeasurementCorrection": corrections[i]})
            statistics(row, corrections[i], o["sigma"], a[i], lambda value: value)
            line_rows.append(row)
        elif o["kind"] == pf.DIRECTION_SET:
            sets.setdefault(o["set"], []).append(i)
    seconds = (lambda value: value / SECONDS)
    for members in sets.values():
        members.sort(key=lambda i: observations[i]["rank"])
        first = observations[members[0]]
        # the backsight: station and backsight, the line's recorded Direction, nothing adjusted (Pro 3.7)
        row = base(first, first["from"], first["to"])
        row.update({"Measurement": first["line"].get_ci("Direction"), "Outlier": pf.NO})
        line_rows.append(row)
        for previous, i in zip(members, members[1:]):
            o = observations[i]
            # the angle from the direction before: Pro's row names station, backsight and target, measures the
            # station-to-target direction and adjusts it by the angle's correction
            v = corrections[i] - corrections[previous]
            row = base(o, o["from"], o["backsight"])
            row.update({"Point3Name": o["to"].point.get_ci("Name"), "Measurement": o["value"],
                        "AdjustedMeasurement": (o["value"] + math.degrees(v)) % 360.0,
                        "MeasurementCorrection": math.degrees(v),
                        "shape": LineString([place(o["from"]), place(o["to"])])})
            statistics(row, v, math.hypot(o["sigma"], observations[previous]["sigma"]), a[i] - a[previous], seconds)
            line_rows.append(row)
    pf.insert(fab.file, points_table, point_rows)
    pf.insert(fab.file, lines_table, line_rows)
    pf.insert(fab.file, vectors_table, vector_rows)


# ---- applying --------------------------------------------------------------------------------------------------------


def _similarity(old_a, old_b, new_a, new_b):
    """The 2D similarity transformation (scale, rotation, translation) taking old_a→new_a and old_b→new_b."""
    oa, ob, na, nb = (complex(*p) for p in (old_a, old_b, new_a, new_b))
    if ob == oa:
        shift = na - oa
        return lambda p: (p[0] + shift.real, p[1] + shift.imag)
    m = (nb - na) / (ob - oa)

    def transform(p):
        z = na + m * (complex(*p) - oa)
        return z.real, z.imag
    return transform


def apply(fab, movement_tolerance, update_attributes):
    from ._parcel_build import Snap
    adjusted = pf.read(fab.file, fab.roles["adjustment_points"])
    points = pf.read(fab.file, fab.points)
    by_guid = {str(p.get_ci("GlobalID")).upper(): p for p in points}
    moves = {}            # old xy -> new xy
    point_changes = {}
    for row in adjusted:
        point = by_guid.get(str(row.get_ci("PointID")).upper())
        if point is None or point.shape is None or row.get_ci("UpdateSource") != pf.YES:
            continue
        old = (point.shape.x, point.shape.y)
        new = (row.get_ci("AdjustedX"), row.get_ci("AdjustedY"))
        change = {}
        # a fixed shape (Fixed Shape Yes) and a constrained point keep their place; their attributes are updated, as
        # Pro 3.7 updates them (measured)
        moving = point.get_ci("IsFixed") != pf.YES and not str(row.get_ci("Constrained") or "").startswith("CC")
        if moving and new[0] is not None and math.hypot(new[0] - old[0], new[1] - old[1]) > movement_tolerance:
            change["shape"] = Point(new)
            moves[old] = new
        if update_attributes:
            for name in ("XYUncertainty", "EllipseMajor", "EllipseMinor", "EllipseDirection"):
                change[name] = row.get_ci(name)
        if change:
            point_changes[point.oid] = change
    # constrained points whose shape is not at their recorded coordinates move to them
    for row in adjusted:
        point = by_guid.get(str(row.get_ci("PointID")).upper())
        if point is None or point.shape is None or not str(row.get_ci("Constrained") or "").startswith("CC"):
            continue
        if point.get_ci("IsFixed") == pf.YES:      # a fixed shape does not move (the SHAPE IS FIXED rule)
            continue
        x, y = point.get_ci("X"), point.get_ci("Y")
        if x is None or y is None:
            continue
        old = (point.shape.x, point.shape.y)
        if math.hypot(x - old[0], y - old[1]) > 0:
            point_changes.setdefault(point.oid, {})["shape"] = Point((x, y))
            moves[old] = (x, y)
    pf.update(fab.file, fab.points, point_changes)
    snap = Snap(fab.tolerance)
    for old in moves:
        snap.add(old)
    targets = {}
    for row in pf.read(fab.file, fab.roles["adjustment_lines"]):
        targets.setdefault(str(row.get_ci("Source")), set()).add(str(row.get_ci("LineID")).upper())
    vertex_moves = {}
    moved_lines = 0
    for table in fab.line_classes():
        wanted = targets.get(_short(table))
        if not wanted:
            continue
        changes = {}
        for line in pf.read(fab.file, table):
            if str(line.get_ci("GlobalID")).upper() not in wanted or line.shape is None:
                continue
            coordinates = pf.line_coordinates(line.shape)
            a, b = coordinates[0], coordinates[-1]
            new_a = moves.get(snap.find(a), a) if snap.find(a) is not None else a
            new_b = moves.get(snap.find(b), b) if snap.find(b) is not None else b
            if new_a == a and new_b == b:
                continue
            transform = _similarity(a, b, new_a, new_b)
            moved = [new_a] + [transform(p) for p in coordinates[1:-1]] + [new_b]
            for before, after in zip(coordinates, moved):
                vertex_moves[before] = after
            changes[line.oid] = {"shape": LineString(moved)}
        pf.update(fab.file, table, changes)
        moved_lines += len(changes)
    for old, new in moves.items():
        vertex_moves.setdefault(old, new)
    vertex_snap = Snap(fab.tolerance)
    for old in vertex_moves:
        vertex_snap.add(old)
    import shapely
    for each in fab.types:
        changes = {}
        for polygon in pf.read(fab.file, each.polygons):
            if polygon.shape is None:
                continue

            def follow(coordinates):
                out = []
                for c in coordinates.tolist():
                    found = vertex_snap.find((c[0], c[1]))
                    out.append(vertex_moves[found] if found is not None else (c[0], c[1]))
                return numpy.array(out)
            moved = shapely.transform(polygon.shape, follow)
            if not moved.equals_exact(polygon.shape, 0.0):
                changes[polygon.oid] = {"shape": moved}
        pf.update(fab.file, each.polygons, changes)
    AddMessage(f"{len(moves)} points and {moved_lines} lines moved.")
    return len(moves)
