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

On the ellipsoid since 2026-11-01 (D-355, ``_Geodesy``): Pro measured in five more coordinate systems adjusts a line's
Distance as a WGS 84 geodesic and its Direction as the geodesic's azimuth, and grapy now does; the unknowns stay the
grid coordinates. The normal equations are solved sparsely and only the cofactors the report needs are formed, so a
fabric of thousands of parcels fits ADR-223's limits (ADR-250 condition 3).
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


class _Geodesy:
    """The fabric's coordinate system tied to an ellipsoid, for an adjustment on it rather than in the grid.

    Measured beside ArcGIS Pro 3.7 on 2026-11-01 (ADR-250 condition 2, D-355; ``tools/pro-compare/adr250-parcel.py``
    in UTM 32N, Web Mercator at 0° and 60°, WGS 84 geographic and DHDN Gauss-Krüger zone 3): Pro takes a line's
    Distance as the geodesic between its points and its Direction as the geodesic's azimuth, and adjusts the points
    until the geodesics fit — between Pro's adjusted points the WGS 84 geodesic is the measured distance to 0.3 mm in
    every system. **The geodesic is on WGS 84's ellipsoid whatever the datum**: in Gauss-Krüger the points' latitudes
    and longitudes are DHDN's (no datum shift) and the distance between them is measured on WGS 84 — on Bessel's
    ellipsoid the same adjusted square's sides are 99.988 m. grapy does the same, through pyproj (Karney's geodesics).
    """

    STEP_METRES = 1.0      # the step of a frame's central differences (PROJ's inverses are noisy at a centimetre)

    def __init__(self, crs):
        from pyproj import Geod, Transformer
        self.geographic = crs.is_geographic
        self._to = Transformer.from_crs(crs, crs.geodetic_crs, always_xy=True)
        self.geod = Geod(ellps="WGS84")
        if self.geographic:
            self.unit = 1.0         # a geographic fabric's Distance is in metres (Pro 3.7, measured)
            self.step = self.STEP_METRES / 111320.0
        else:
            self.unit = float(crs.axis_info[0].unit_conversion_factor or 1.0)
            self.step = self.STEP_METRES / self.unit

    @classmethod
    def of(cls, srid):
        """The geodesy of a coordinate system with an ellipsoid, or None (no system, or one pyproj cannot place)."""
        if not srid or int(srid) <= 0 or __import__("os").environ.get("XGRID"):
            return None
        try:
            from pyproj import CRS
            crs = CRS.from_epsg(int(srid))
            if not (crs.is_projected or crs.is_geographic) or crs.geodetic_crs is None:
                return None
            return cls(crs)
        except Exception:       # noqa: BLE001 — an unknown system is adjusted in the grid, as before
            return None

    def lonlat(self, xs, ys):
        lon, lat = self._to.transform(numpy.asarray(xs, float), numpy.asarray(ys, float))
        return numpy.asarray(lon, float), numpy.asarray(lat, float)

    def inverse(self, lon1, lat1, lon2, lat2):
        """Azimuths (degrees) and lengths (metres) of the geodesics."""
        az, _, s = self.geod.inv(lon1, lat1, lon2, lat2)
        return numpy.asarray(az, float), numpy.asarray(s, float)

    def frames(self, xs, ys):
        """At each point, the matrix taking a grid step (dx, dy) to metres east and north on the ellipsoid: the
        projection's scales and its meridian convergence (Pro 3.7 reports a point's standard deviations and ellipse
        east and north of true north, measured in UTM 32N, where the grid is 3 degrees off it)."""
        h = self.step
        xs, ys = numpy.asarray(xs, float), numpy.asarray(ys, float)
        out = numpy.zeros((len(xs), 2, 2))
        for column, (dx, dy) in enumerate(((h, 0.0), (0.0, h))):
            lo1, la1 = self.lonlat(xs - dx, ys - dy)
            lo2, la2 = self.lonlat(xs + dx, ys + dy)
            az, s = self.inverse(lo1, la1, lo2, la2)
            az = numpy.radians(az)
            out[:, 0, column] = s * numpy.sin(az) / (2.0 * h)
            out[:, 1, column] = s * numpy.cos(az) / (2.0 * h)
        return out


def convergence(value, srid, default="0.05 Meters"):
    """A tolerance parameter: in the fabric's linear unit, or in metres for a geographic fabric (whose adjustment is
    on the ellipsoid, Pro 3.7 measured)."""
    geo = _Geodesy.of(srid)
    if geo is None or not geo.geographic:
        return pf.linear(value, srid, default)
    from . import _units
    if isinstance(value, (int, float)):
        return float(value)
    parts = (str(value).strip() if pf.given(value) else default).split()
    try:
        per = _units.linear(parts[1]) if len(parts) > 1 else 1.0
        if per is None:
            raise ValueError(parts[1])
        return float(parts[0]) * per
    except (ValueError, IndexError):
        raise ExecuteError(f"ERROR 000800: '{value}' is not a linear unit.") from None


def analyze(fab, lines, analysis_type, tolerance):
    """The adjustment of the given lines ((class, Feature) pairs); writes the three adjustment classes."""
    from ._parcel_build import Snap
    for table, line in lines:
        kind = line.get_ci("AzimuthType")
        if kind is not None and int(kind) in (2, 3, 4, 5):
            raise NotBuiltError("arcpy.parcel.AnalyzeParcelsByLeastSquaresAdjustment: lines with an AzimuthType of 2 to "
                                "5 (geodetic azimuths and ellipsoid arc distances) are not built in grapy; its "
                                "adjustment takes a line's Direction as a north azimuth of the fabric's system.")
    geo = _Geodesy.of(fab.srid)
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
    # On the ellipsoid (geo), distances and their accuracies are metres and the points' accuracies become grid units
    # through each point's scales; in the grid (no geodesy), everything is in the fabric's unit, as before.
    if geo is not None:
        unit = geo.unit
        default_distance, five_mm, free_sigma = 0.15, 0.005, FREE_SIGMA
        xs = numpy.array([s.xy[0] for s in order])
        ys = numpy.array([s.xy[1] for s in order])
        frames = geo.frames(xs, ys)
        scale_x, scale_y = numpy.hypot(frames[:, 0, 0], frames[:, 1, 0]), numpy.hypot(frames[:, 0, 1], frames[:, 1, 1])
    else:
        unit = 1.0
        default_distance = pf.linear("0.15 Meters", fab.srid)
        five_mm = pf.linear("0.005 Meters", fab.srid)
        free_sigma = FREE_SIGMA
        scale_x = scale_y = numpy.ones(len(order))
        frames = numpy.repeat(numpy.eye(2)[None, :, :], len(order), axis=0)
    default_direction = 30.0
    # observations
    observations = []   # dicts: kind, stations, value, sigma (radians for directions), set, line, table
    for table, line, sa, sb in measured:
        distance, _ = _ground_to_grid(line, _chord(line), None)
        if distance is not None and distance > 0:
            sigma = line.get_ci("DistanceAccuracy") or (default_distance / unit)
            observations.append({"kind": pf.DISTANCE, "from": sa, "to": sb, "value": float(distance) * unit,
                                 "sigma": float(sigma) * unit, "line": line, "table": table})
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

    def held(station, kind, metres):
        """A coordinate observation's standard deviation in grid units, from one in metres (or the fabric's unit)."""
        return metres / (scale_x if kind == "x" else scale_y)[station.index] if geo is not None else metres

    if weighted:
        for station in controls:
            sigma = five_mm if _constrained(station.point) else float(station.point.get_ci("XYAccuracy")) * unit
            x, y = _start(station.point)
            observations.append({"kind": "x", "from": station, "value": x, "sigma": held(station, "x", sigma)})
            observations.append({"kind": "y", "from": station, "value": y, "sigma": held(station, "y", sigma)})
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
            observations.append({"kind": "x", "from": station, "value": x0, "sigma": held(station, "x", free_sigma),
                                 "datum": True})
            observations.append({"kind": "y", "from": station, "value": y0, "sigma": held(station, "y", free_sigma),
                                 "datum": True})
    n_coordinates = 2 * len(order)
    n_unknowns = n_coordinates + orientation
    model = _Model(observations, n_coordinates, n_unknowns, geo)
    x = numpy.zeros(n_unknowns)
    for station in order:
        x[2 * station.index], x[2 * station.index + 1] = station.xy
    # initial orientations: the mean difference of computed azimuth and measured direction
    azimuths = model.azimuths(x)
    for k in range(orientation):
        rows_k = numpy.nonzero(model.sets == k)[0]
        diffs = (azimuths[rows_k] - model.values[rows_k] + 180.0) % 360.0 - 180.0
        x[n_coordinates + k] = math.radians(float(numpy.mean(diffs)))
    weights = numpy.array([1.0 / (o["sigma"] ** 2) for o in observations])
    # the step test in metres on the ellipsoid (a geographic fabric's grid is degrees), in grid units otherwise
    step_scale = numpy.column_stack([scale_x, scale_y]).ravel()
    limit = tolerance * unit
    converged = False
    factor = None
    for _ in range(MAX_ITERATIONS):
        a, misclosure = model.linearize(x)
        dx, factor = _solve(a, weights, misclosure)
        x += dx
        if numpy.max(numpy.abs(dx[:n_coordinates]) * step_scale) < limit:
            converged = True
            break
    if not converged:
        AddWarning(f"WARNING: the adjustment did not converge to {tolerance} in {MAX_ITERATIONS} iterations.")
    a, misclosure = model.linearize(x)
    computed = model.computed(x)
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
    _, factor = _solve(a, weights, misclosure)
    keys, gradients = _reported(observations, a)
    qxx = _Cofactor(factor, n_unknowns, n_coordinates, gradients)
    variances = dict(zip(keys, qxx.forms(gradients).tolist())) if gradients is not None else {}
    AddMessage(f"SOLUTION {'Converged' if converged else 'did not converge'}")
    AddMessage(f"Number of unknown parameters: {unknown}")
    AddMessage(f"Number of measurements: {n_measurements}")
    AddMessage(f"Degrees of freedom: {redundancy}")
    AddMessage(f"Standard Error (Sigma): {sigma0:.3f}")
    # the points' precisions east and north, in metres on the ellipsoid (the fabric's unit for a projected one)
    report = frames / (1.0 if geo is None or geo.geographic else unit)
    _write(fab, order, observations, computed, corrections, variances, qxx, sigma0, weighted, x, unit, report)
    return sigma0, redundancy


_DISTANCE, _DIRECTION, _X, _Y = 0, 1, 2, 3


class _Model:
    """The observations as arrays, their computed values and their Jacobian (sparse) at a solution."""

    def __init__(self, observations, n_coordinates, n_unknowns, geo):
        code = {pf.DISTANCE: _DISTANCE, pf.DIRECTION_SET: _DIRECTION, "x": _X, "y": _Y}
        self.kinds = numpy.array([code[o["kind"]] for o in observations], dtype=numpy.int8)
        self.origin = numpy.array([o["from"].index for o in observations], dtype=numpy.int64)
        self.target = numpy.array([o["to"].index if "to" in o else -1 for o in observations], dtype=numpy.int64)
        self.sets = numpy.array([o.get("set", -1) for o in observations], dtype=numpy.int64)
        self.values = numpy.array([o["value"] for o in observations], dtype=float)
        self.datum = numpy.array([bool(o.get("datum")) for o in observations])
        self.n_coordinates, self.n_unknowns, self.geo = n_coordinates, n_unknowns, geo
        self.pair = numpy.nonzero((self.kinds == _DISTANCE) | (self.kinds == _DIRECTION))[0]

    def _pairs(self, x, i=None, j=None):
        """Azimuths (degrees) and lengths of the pair observations, from stations i to j (default: every pair)."""
        i = self.origin[self.pair] if i is None else i
        j = self.target[self.pair] if j is None else j
        if self.geo is None:
            dx, dy = x[2 * j] - x[2 * i], x[2 * j + 1] - x[2 * i + 1]
            return numpy.degrees(numpy.arctan2(dx, dy)), numpy.hypot(dx, dy)
        lon, lat = self.geo.lonlat(x[0:self.n_coordinates:2], x[1:self.n_coordinates:2])
        return self.geo.inverse(lon[i], lat[i], lon[j], lat[j])

    def azimuths(self, x):
        out = numpy.zeros(len(self.kinds))
        out[self.pair] = self._pairs(x)[0]
        return out

    def computed(self, x):
        out = numpy.zeros(len(self.kinds))
        az, s = self._pairs(x)
        kinds = self.kinds[self.pair]
        sets = self.sets[self.pair]
        orient = numpy.zeros(len(sets))
        orient[sets >= 0] = x[self.n_coordinates + sets[sets >= 0]]
        out[self.pair] = numpy.where(kinds == _DISTANCE, s, (az - numpy.degrees(orient)) % 360.0)
        cx = self.kinds == _X
        cy = self.kinds == _Y
        out[cx] = x[2 * self.origin[cx]]
        out[cy] = x[2 * self.origin[cy] + 1]
        return out

    def linearize(self, x):
        """The design matrix (sparse, directions in radians) and the misclosure (measured minus computed)."""
        from scipy import sparse
        computed = self.computed(x)
        kinds = self.kinds
        diff = computed - self.values
        direction = kinds == _DIRECTION
        diff[direction] = numpy.radians((diff[direction] + 180.0) % 360.0 - 180.0)
        misclosure = -diff
        # A free network's datum holds each point at 10 m about where the last iteration put it, not where it was
        # drawn: it fixes the position, and the measurements alone the shape. Pro 3.7, measured: in Web Mercator at
        # 60 degrees, where the grid is twice the ground and the drawn square half the measured one, its adjusted
        # sides have no corrections and the centroid stays where it was drawn.
        misclosure[self.datum] = 0.0
        i, j = self.origin[self.pair], self.target[self.pair]
        pk = kinds[self.pair]
        if self.geo is None:
            dx, dy = x[2 * j] - x[2 * i], x[2 * j + 1] - x[2 * i + 1]
            s2 = dx * dx + dy * dy
            s = numpy.sqrt(s2)
            # distance: d s / d(xj, yj) = (dx, dy) / s; direction (radians): (dy, -dx) / s^2
            gxj = numpy.where(pk == _DISTANCE, dx / s, dy / s2)
            gyj = numpy.where(pk == _DISTANCE, dy / s, -dx / s2)
            gxi, gyi = -gxj, -gyj
        else:
            gxi, gyi, gxj, gyj = self._numeric(x, i, j, pk)
        rows = numpy.concatenate([self.pair] * 4)
        cols = numpy.concatenate([2 * i, 2 * i + 1, 2 * j, 2 * j + 1])
        vals = numpy.concatenate([gxi, gyi, gxj, gyj])
        ds = numpy.nonzero(direction)[0]
        cx = numpy.nonzero(kinds == _X)[0]
        cy = numpy.nonzero(kinds == _Y)[0]
        rows = numpy.concatenate([rows, ds, cx, cy])
        cols = numpy.concatenate([cols, self.n_coordinates + self.sets[ds], 2 * self.origin[cx], 2 * self.origin[cy] + 1])
        vals = numpy.concatenate([vals, -numpy.ones(len(ds)), numpy.ones(len(cx)), numpy.ones(len(cy))])
        a = sparse.csr_matrix((vals, (rows, cols)), shape=(len(kinds), self.n_unknowns))
        return a, misclosure

    def _numeric(self, x, i, j, pk):
        """The pair observations' partial derivatives on the ellipsoid: a geodesic's length and azimuth differentiated
        in metres east and north at its ends (its azimuths there; the reduced length taken as the length, exact to
        (s/R)^2 for a parcel's lines), taken to the grid through each end's frame. Differencing the geodesics in the
        grid instead is noisy at a centimetre's step (PROJ's inverse projections)."""
        n = self.n_coordinates
        xs, ys = x[0:n:2], x[1:n:2]
        lon, lat = self.geo.lonlat(xs, ys)
        az12, az21, s = self.geo.geod.inv(lon[i], lat[i], lon[j], lat[j])
        a1 = numpy.radians(numpy.asarray(az12, float))
        a2 = numpy.radians(numpy.asarray(az21, float) + 180.0)      # the forward azimuth at the far end
        s = numpy.asarray(s, float)
        frames = self.geo.frames(xs, ys)
        distance = pk == _DISTANCE
        # d s / d(E, N): -(sin a1, cos a1) at the start, (sin a2, cos a2) at the end; d a1 / d(E, N): (-cos a1, sin a1)
        # / s at the start, (cos a2, -sin a2) / s at the end
        ei = numpy.where(distance, -numpy.sin(a1), -numpy.cos(a1) / s)
        ni = numpy.where(distance, -numpy.cos(a1), numpy.sin(a1) / s)
        ej = numpy.where(distance, numpy.sin(a2), numpy.cos(a2) / s)
        nj = numpy.where(distance, numpy.cos(a2), -numpy.sin(a2) / s)
        fi, fj = frames[i], frames[j]
        gxi = ei * fi[:, 0, 0] + ni * fi[:, 1, 0]
        gyi = ei * fi[:, 0, 1] + ni * fi[:, 1, 1]
        gxj = ej * fj[:, 0, 0] + nj * fj[:, 1, 0]
        gyj = ej * fj[:, 0, 1] + nj * fj[:, 1, 1]
        return gxi, gyi, gxj, gyj


def _difference(o, computed):
    """Adjusted minus measured, in the observation's own unit of weight: radians for a direction."""
    if o["kind"] == pf.DIRECTION_SET:
        return math.radians((computed - o["value"] + 180.0) % 360.0 - 180.0)
    return computed - o["value"]


SINGULAR = 1e-11        # a pivot this small against the largest is a datum defect, not a weak network


def _solve(a, weights, misclosure):
    """The step of one iteration from the normal equations, factored sparsely (the network of a large fabric is
    thousands of points, and its design matrix is almost all zeros); a vanishing pivot is a network its controls do
    not fix."""
    from scipy import sparse
    from scipy.sparse.linalg import splu
    w = sparse.diags(weights)
    normal = (a.T @ w @ a).tocsc()
    try:
        factor = splu(normal, permc_spec="MMD_AT_PLUS_A", diag_pivot_thresh=0.0,
                      options={"SymmetricMode": True})
        pivots = numpy.abs(factor.U.diagonal())
        singular = pivots.size == 0 or pivots.min() <= pivots.max() * SINGULAR
    except RuntimeError:            # "Factor is exactly singular"
        singular, factor = True, None
    if singular:
        raise ExecuteError("ERROR 160207: AnalyzeParcelsByLeastSquaresAdjustment: the network is not fixed by its "
                           "control points (a part of it connects to fewer than two of them).")
    dx = factor.solve(a.T @ (weights * misclosure))
    return dx, factor


def _reported(observations, a):
    """The gradients of the measurements the report states, as keys and the rows of one sparse matrix: a distance's
    gradient is its row of A, an angle's the difference of its two directions' rows (in which the set's orientation
    cancels)."""
    from scipy import sparse
    groups = {}
    for i, o in enumerate(observations):
        if o["kind"] == pf.DIRECTION_SET:
            groups.setdefault(o["set"], []).append(i)
    angles = []
    for members in groups.values():
        members.sort(key=lambda i: observations[i]["rank"])
        angles.extend(zip(members[1:], members))
    distances = [i for i, o in enumerate(observations) if o["kind"] == pf.DISTANCE]
    parts = []
    if distances:
        parts.append(a[distances])
    if angles:
        parts.append(a[[i for i, _ in angles]] - a[[p for _, p in angles]])
    keys = [("d", i) for i in distances] + [("a", i) for i, _ in angles]
    return keys, (sparse.vstack(parts).tocsr() if parts else None)


class _Cofactor:
    """The entries of the unknowns' cofactor matrix (the normal matrix's inverse) that the report needs: each point's
    2 x 2 block and the quadratic form of each reported measurement's gradient. Solved a block of unit columns at a
    time from the factor, only for the unknowns those need, keeping only those entries, so a fabric of thousands of
    points never holds the dense inverse."""

    BLOCK = 512

    def __init__(self, factor, n, n_coordinates, gradients):
        from scipy import sparse
        coordinates = sparse.csr_matrix((numpy.ones(n_coordinates), (numpy.arange(n_coordinates) // 2,
                                                                      numpy.arange(n_coordinates))),
                                        shape=(max(1, n_coordinates // 2), n))
        pattern = coordinates.T @ coordinates
        if gradients is not None:
            g = abs(gradients)
            pattern = pattern + g.T @ g
        pattern = pattern.tocsc()
        pattern.sum_duplicates()
        values = numpy.zeros(pattern.nnz)
        wanted = numpy.nonzero(numpy.diff(pattern.indptr))[0]
        for start in range(0, len(wanted), self.BLOCK):
            chunk = wanted[start:start + self.BLOCK]
            unit = numpy.zeros((n, len(chunk)))
            unit[chunk, numpy.arange(len(chunk))] = 1.0
            columns = factor.solve(unit)
            for k, c in enumerate(chunk.tolist()):
                lo, hi = pattern.indptr[c], pattern.indptr[c + 1]
                values[lo:hi] = columns[pattern.indices[lo:hi], k]
        self.q = sparse.csc_matrix((values, pattern.indices, pattern.indptr), shape=(n, n)).tocsr()

    def block(self, i):
        q = self.q
        return q[2 * i, 2 * i], q[2 * i + 1, 2 * i + 1], q[2 * i, 2 * i + 1]

    def forms(self, gradients):
        """g Q g' for each row g of a sparse matrix (Q holds every entry the rows' supports need)."""
        return numpy.asarray((gradients @ self.q).multiply(gradients).sum(axis=1)).ravel()


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
    for _ in range(40):          # the bracket is 0.45 a wide: 40 halvings leave 4e-13 a
        mid = (low + high) / 2.0
        if probability(mid) < 0.95:
            low = mid
        else:
            high = mid
    return (low + high) / 2.0


def _ellipse(qxx, i, variance, frame=None):
    """A point's standard deviations east and north and its error ellipse: its grid cofactors taken through the
    point's frame (grid step to metres east and north, the identity in the grid)."""
    qx, qy, qxy = qxx.block(i)
    grid = numpy.array([[qx, qxy], [qxy, qy]]) * variance
    j = numpy.eye(2) if frame is None else frame
    (sxx, sxy), (_, syy) = j @ grid @ j.T
    mean, half = (sxx + syy) / 2.0, math.sqrt(max(((sxx - syy) / 2.0) ** 2 + sxy ** 2, 0.0))
    major, minor = math.sqrt(max(mean + half, 0.0)), math.sqrt(max(mean - half, 0.0))
    # the major axis's direction as a north azimuth (from the north axis, clockwise)
    angle = 0.5 * math.atan2(2.0 * sxy, syy - sxx)
    return major, minor, math.degrees(angle) % 180.0, math.sqrt(max(sxx, 0.0)), math.sqrt(max(syy, 0.0))


def _short(table):
    return table.rsplit("/", 1)[-1]


def _write(fab, order, observations, computed, corrections, variances, qxx, sigma0, weighted, x, unit=1.0, report=None):
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
        frame = report[station.index] if report is not None else numpy.eye(2)
        major, minor, direction, sde, sdn = _ellipse(qxx, station.index, 1.0, frame)
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

    def statistics(row, v, sigma, variance, to_report):
        """The statistics of a measurement whose adjusted value has the variance ``variance`` and whose a-priori
        standard deviation is ``sigma``."""
        adjusted_sigma = math.sqrt(max(variance, 0.0))
        correction_sigma = math.sqrt(max(sigma ** 2 - adjusted_sigma ** 2, 0.0))
        if correction_sigma <= sigma * 1e-6:
            # a measurement nothing checks (no redundancy): its correction and that correction's deviation are both
            # rounding, and their ratio is not a statistic
            correction_sigma = 0.0
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
            # in the line's own unit; on the ellipsoid the adjustment ran in metres
            row.update({"Measurement": o["value"] / unit, "AdjustedMeasurement": computed[i] / unit,
                        "MeasurementCorrection": corrections[i] / unit})
            statistics(row, corrections[i], o["sigma"], variances[("d", i)], lambda value: value / unit)
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
            statistics(row, v, math.hypot(o["sigma"], observations[previous]["sigma"]), variances[("a", i)], seconds)
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
    geo = _Geodesy.of(fab.srid)

    def moved(old, new):
        """How far a point moves: metres on the ellipsoid for a geographic fabric (its tolerance is metres), grid
        units otherwise."""
        if geo is not None and geo.geographic:
            lon, lat = geo.lonlat([old[0], new[0]], [old[1], new[1]])
            return float(geo.inverse(lon[:1], lat[:1], lon[1:], lat[1:])[1][0])
        return math.hypot(new[0] - old[0], new[1] - old[1])
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
        if moving and new[0] is not None and moved(old, new) > movement_tolerance:
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
