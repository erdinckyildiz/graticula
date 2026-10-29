"""arcpy.stats — the Spatial Statistics tools grapy builds, by their documented names and parameters (ADR-223 §5.2, ADR-224).

Every formula here is the one Esri's public "How … works" page gives for the tool, and each docstring names it. A
feature's location is its centroid, as the tools document: a point is itself; a line, polygon or multipart feature is
"the weighted mean center of all feature parts", weighted by length or area, which is the shape's centroid.

What the documentation does not settle is refused or chosen and said: a refusal names the tool, the parameter and the
value (NotBuiltError), and a choice is written ``INFERRED:`` in the tool's docstring. Refused everywhere:

- a geographic reference — the tools measure it in chordal metres on an oblate spheroid, which grapy does not;
- ``GET_SPATIAL_WEIGHTS_FROM_FILE`` (an .swm file's binary layout is not published) and ``ZONE_OF_INDIFFERENCE``
  (the documentation says only that weights "diminish with distance" past the band, not how);
- ``APPLY_FDR`` (the correction "for multiple testing and spatial dependence" is not given as a formula);
- ``GENERATE_REPORT`` (the report's content is not documented);
- permutation p-values (``Number_of_Permutations`` above 0), which are random by definition.

p-values are two-sided, from the standard normal distribution — ``erfc(|z| / sqrt 2)`` — as Esri's "What is a
z-score? What is a p-value?" describes them. INFERRED: a derived scalar output is returned as text, as arcpy's
Result hands back derived values (``result[0]`` is concatenated with text in Esri's own samples).
"""
from __future__ import annotations

import math

from . import _gpkg, da
from ._result import Result
from ._runtime import NotBuiltError


# ---- shared: reading, centroids, refusals ------------------------------------------------------------------------

def _out(path):
    from .analysis import _out as out
    return out(path)


def _schema(path):
    from .analysis import _schema as schema
    return schema(path)


def _list(value):
    from .management import _list as listed
    return listed(value)


def _refuse(tool, parameter, value, why):
    raise NotBuiltError(f"arcpy.stats.{tool}: {parameter} '{value}' is not built in grapy — {why}.")


def _given(value):
    return value not in (None, "", "#")


def _keyword(value):
    """A keyword as arcpy takes it: the Python form (``FIXED_DISTANCE_BAND``) or the label (``Fixed distance band``)."""
    return str(value).strip().upper().replace(" ", "_")


def _projected(tool, srid):
    """The tools measure a geographic reference in chordal metres; grapy measures planar distance only."""
    from ._reference import SpatialReference
    from ._runtime import env
    if srid > 0 and SpatialReference(srid).type == "Geographic":
        _refuse(tool, "Input_Feature_Class", f"EPSG:{srid}", "a geographic reference is measured in chordal metres on "
                "the spheroid; project the data first (management.Project)")
    wanted = env.outputCoordinateSystem
    if wanted not in (None, ""):
        from ._reference import reference_of
        target = reference_of(wanted)
        if target is not None and target.factoryCode != srid:
            _refuse(tool, "env.outputCoordinateSystem", target.factoryCode, "distances are measured in the input's own "
                    "reference; project the data first")


def _features(tool, path, fields=(), where=None):
    """Every feature with a shape as (oid, {field: value}, x, y, shape), its x and y the centroid."""
    columns, kind, srid, _ = _schema(path)
    _projected(tool, srid)
    names = {c.name.lower(): c.name for c in columns}
    wanted = []
    for field in fields:
        if field.lower() not in names:
            raise ValueError(f"arcpy.stats.{tool}: the input has no field '{field}'.")
        wanted.append(names[field.lower()])
    out = []
    with da.SearchCursor(path, ["OID@", *wanted, "SHAPE@"], where) as rows:
        for row in rows:
            shape = row[-1]._shape if row[-1] is not None else None
            if shape is None or shape.is_empty:
                continue
            c = shape if shape.geom_type == "Point" else shape.centroid
            out.append((row[0], dict(zip([f.lower() for f in wanted], row[1:-1])), c.x, c.y, shape))
    return out, columns, kind, srid


def _distances(xy, method="EUCLIDEAN_DISTANCE"):
    import numpy as np
    xy = np.asarray(xy, dtype=float)
    dx = xy[:, 0][:, None] - xy[:, 0][None, :]
    dy = xy[:, 1][:, None] - xy[:, 1][None, :]
    if _keyword(method) in ("MANHATTAN_DISTANCE", "MANHATTAN"):
        return np.abs(dx) + np.abs(dy)
    if _keyword(method) not in ("EUCLIDEAN_DISTANCE", "EUCLIDEAN"):
        raise ValueError(f"Distance_Method '{method}' is neither EUCLIDEAN_DISTANCE nor MANHATTAN_DISTANCE.")
    return np.hypot(dx, dy)


def _nearest(d):
    """Each feature's distance to its nearest other feature."""
    import numpy as np
    masked = d + np.diag(np.full(len(d), np.inf))
    return masked.min(axis=1)


def _p(z):
    return math.erfc(abs(z) / math.sqrt(2))


def _text(value):
    return repr(float(value))


def _threshold(value, srid):
    from .analysis import _distance
    return _distance(value, srid)


_CONCEPTS = ("INVERSE_DISTANCE", "INVERSE_DISTANCE_SQUARED", "FIXED_DISTANCE_BAND", "K_NEAREST_NEIGHBORS",
             "CONTIGUITY_EDGES_ONLY", "CONTIGUITY_EDGES_CORNERS")


def _weights(tool, features, srid, concept, method, band, k, self_weight=False):
    """The spatial weights matrix the documentation describes, as a dense array, the diagonal 1 when ``self_weight``
    (Gi*'s feature counts itself) and 0 otherwise.

    - INVERSE_DISTANCE: ``1/d``; INVERSE_DISTANCE_SQUARED: ``1/d²`` — a distance under 1 and two coincident points
      both weigh 1, as the tools' usage notes say. Past the band the weight is 0; a band of 0 applies none.
    - FIXED_DISTANCE_BAND: 1 within the band, 0 outside it.
    - K_NEAREST_NEIGHBORS: 1 for the k nearest.
    - CONTIGUITY_EDGES_ONLY / _CORNERS: 1 for polygons that share a boundary (or overlap) / that touch at all.

    The default band, when none is given, is "the minimum distance to ensure that every feature has at least one
    neighbor": the largest nearest-neighbour distance.

    INFERRED: a feature at exactly the band's distance is inside it (the default band would otherwise leave the
    feature that sets it with no neighbour). INFERRED: among neighbours tied at the k-th distance, the lower object
    id is taken first.
    """
    import numpy as np
    concept = _keyword(concept)
    n = len(features)
    if concept == "GET_SPATIAL_WEIGHTS_FROM_FILE":
        _refuse(tool, "Conceptualization_of_Spatial_Relationships", concept, "a spatial weights matrix file's .swm "
                "layout is not published")
    if concept not in _CONCEPTS:
        _refuse(tool, "Conceptualization_of_Spatial_Relationships", concept, "its weights past the band are documented "
                "only as diminishing with distance" if concept == "ZONE_OF_INDIFFERENCE" else "not a conceptualization "
                "grapy builds")
    if concept.startswith("CONTIGUITY"):
        from shapely.strtree import STRtree
        shapes = [f[4] for f in features]
        if any(not s.geom_type.endswith("Polygon") for s in shapes):
            raise ValueError(f"arcpy.stats.{tool}: {concept} needs polygon features.")
        w = np.zeros((n, n))
        tree = STRtree(shapes)
        for i, shape in enumerate(shapes):
            for j in tree.query(shape, predicate="intersects"):
                j = int(j)
                if j == i:
                    continue
                if concept == "CONTIGUITY_EDGES_CORNERS":
                    w[i, j] = 1.0
                else:
                    shared = shape.intersection(shapes[j])
                    if shared.length > 0 or shared.area > 0:
                        w[i, j] = 1.0
    else:
        d = _distances([(f[2], f[3]) for f in features], method)
        off = ~np.eye(n, dtype=bool)
        if concept == "K_NEAREST_NEIGHBORS":
            if not _given(k):
                raise ValueError(f"arcpy.stats.{tool}: K_NEAREST_NEIGHBORS needs number_of_neighbors.")
            k = int(k)
            if not 1 <= k < n:
                raise ValueError(f"arcpy.stats.{tool}: number_of_neighbors must be between 1 and {n - 1}.")
            w = np.zeros((n, n))
            oids = np.array([f[0] for f in features])
            for i in range(n):
                order = sorted((j for j in range(n) if j != i), key=lambda j: (d[i, j], oids[j]))
                w[i, order[:k]] = 1.0
        else:
            if _given(band):
                limit = _threshold(band, srid)
            else:
                limit = float(_nearest(d).max())
            within = (d <= limit) if limit > 0 or concept == "FIXED_DISTANCE_BAND" else np.ones((n, n), dtype=bool)
            if concept == "FIXED_DISTANCE_BAND":
                w = within.astype(float)
            else:
                power = 2 if concept == "INVERSE_DISTANCE_SQUARED" else 1
                w = np.where(within, 1.0 / np.maximum(d, 1.0) ** power, 0.0)
        w = np.where(off, w, 0.0)
    if self_weight:
        np.fill_diagonal(w, 1.0)
    return w


def _standardized(w, standardization):
    import numpy as np
    option = _keyword(standardization)
    if option == "ROW":
        sums = w.sum(axis=1, keepdims=True)
        return np.divide(w, sums, out=np.zeros_like(w), where=sums != 0)
    if option != "NONE":
        raise ValueError(f"Standardization '{standardization}' is neither ROW nor NONE.")
    return w


def _no_report(tool, report):
    if _keyword(report) in ("GENERATE_REPORT", "TRUE"):
        _refuse(tool, "Generate_Report", report, "the report's content is not documented")


def _no_fdr(tool, fdr):
    if _keyword(fdr) in ("APPLY_FDR", "TRUE"):
        _refuse(tool, "Apply_False_Discovery_Rate__FDR__Correction", fdr, "the correction for multiple testing and "
                "spatial dependence is not documented as a formula")


def _values(tool, features, field):
    import numpy as np
    values = [f[1][field.lower()] for f in features]
    if any(v is None for v in values):
        raise ValueError(f"arcpy.stats.{tool}: '{field}' is null in some features.")
    return np.asarray(values, dtype=float)


# ---- Analyzing Patterns -------------------------------------------------------------------------------------------

def AverageNearestNeighbor(Input_Feature_Class, Distance_Method="EUCLIDEAN_DISTANCE", Generate_Report="NO_REPORT",
                           Area=None):
    """The nearest neighbour index of a distribution, from "How Average Nearest Neighbor works":
    ``D_O = Σd_i / n``, ``D_E = 0.5 / sqrt(n/A)``, ``ANN = D_O / D_E``, ``SE = 0.26136 / sqrt(n²/A)``,
    ``z = (D_O − D_E) / SE``. ``A`` is the Area given, or "the area of the minimum enclosing rectangle that would
    encompass all features", which "will not necessarily align with the x- and y-axes".

    Outputs, in order: NNRatio, NNZScore, PValue, NNExpected, NNObserved, Report_File (empty).
    INFERRED: the minimum enclosing rectangle is the rotated rectangle of least area.
    """
    import shapely
    _no_report("AverageNearestNeighbor", Generate_Report)
    features, _, _, srid = _features("AverageNearestNeighbor", Input_Feature_Class)
    n = len(features)
    if n < 2:
        raise ValueError("arcpy.stats.AverageNearestNeighbor needs two features or more.")
    d = _distances([(f[2], f[3]) for f in features], Distance_Method)
    observed = float(_nearest(d).mean())
    if _given(Area):
        area = float(Area)
    else:
        area = shapely.minimum_rotated_rectangle(shapely.MultiPoint([(f[2], f[3]) for f in features])).area
    if area <= 0:
        raise ValueError("arcpy.stats.AverageNearestNeighbor: the features enclose no area; give Area.")
    expected = 0.5 / math.sqrt(n / area)
    error = 0.26136 / math.sqrt(n * n / area)
    z = (observed - expected) / error
    return Result(_text(observed / expected), _text(z), _text(_p(z)), _text(expected), _text(observed), "")


def _moran(x, w):
    """Global Moran's I and its z-score under randomization: "How Spatial Autocorrelation (Global Moran's I) works"
    and its additional mathematics, equations (6)–(12)."""
    n = len(x)
    z = x - x.mean()
    s0 = w.sum()
    i = (n / s0) * float(z @ w @ z) / float((z * z).sum())
    expected = -1.0 / (n - 1)
    s1 = 0.5 * float(((w + w.T) ** 2).sum())
    s2 = float(((w.sum(axis=1) + w.sum(axis=0)) ** 2).sum())
    d = n * float((z ** 4).sum()) / float((z * z).sum()) ** 2
    a = n * ((n * n - 3 * n + 3) * s1 - n * s2 + 3 * s0 * s0)
    b = d * ((n * n - n) * s1 - 2 * n * s2 + 6 * s0 * s0)
    c = (n - 1) * (n - 2) * (n - 3) * s0 * s0
    variance = (a - b) / c - expected * expected
    return i, expected, variance, (i - expected) / math.sqrt(variance)


def SpatialAutocorrelation(Input_Feature_Class, Input_Field, Generate_Report="NO_REPORT",
                           Conceptualization_of_Spatial_Relationships="INVERSE_DISTANCE",
                           Distance_Method="EUCLIDEAN_DISTANCE", Standardization="ROW",
                           Distance_Band_or_Threshold_Distance=None, Weights_Matrix_File=None, number_of_neighbors=None):
    """Global Moran's I: ``I = (n/S0) ΣΣ w_ij z_i z_j / Σz_i²``, ``E[I] = −1/(n−1)``, ``V[I] = E[I²] − E[I]²`` with
    ``E[I²] = (A − B)/C`` and A, B, C, D, S1, S2 as the additional mathematics page gives them.

    Outputs, in order: Index, ZScore, PValue, Report_File (empty), Derived_Input_Dataset.
    INFERRED: Conceptualization_of_Spatial_Relationships defaults to INVERSE_DISTANCE, the tool's first option, when a
    script leaves it out; the documented signature has no default for it.
    """
    _no_report("SpatialAutocorrelation", Generate_Report)
    features, _, _, srid = _features("SpatialAutocorrelation", Input_Feature_Class, [Input_Field])
    if len(features) < 4:
        raise ValueError("arcpy.stats.SpatialAutocorrelation needs four features or more: its variance divides by "
                         "(n−1)(n−2)(n−3).")
    x = _values("SpatialAutocorrelation", features, Input_Field)
    w = _standardized(_weights("SpatialAutocorrelation", features, srid, Conceptualization_of_Spatial_Relationships,
                               Distance_Method, Distance_Band_or_Threshold_Distance, number_of_neighbors), Standardization)
    i, _, _, z = _moran(x, w)
    return Result(_text(i), _text(z), _text(_p(z)), "", Input_Feature_Class)


def HighLowClustering(Input_Feature_Class, Input_Field, Generate_Report="NO_REPORT",
                      Conceptualization_of_Spatial_Relationships="INVERSE_DISTANCE",
                      Distance_Method="EUCLIDEAN_DISTANCE", Standardization="ROW",
                      Distance_Band_or_Threshold_Distance=None, Weights_Matrix_File=None, number_of_neighbors=None):
    """Getis-Ord General G: ``G = ΣΣ_{j≠i} w_ij x_i x_j / ΣΣ_{j≠i} x_i x_j``, ``E[G] = W / (n(n−1))``,
    ``V[G] = E[G²] − E[G]²`` with ``E[G²] = (A + B)/C`` and A, B, C, D0–D4, W, S1, S2 as the additional mathematics
    page gives them. The values must not be negative, as the tool requires.

    Outputs, in order: Observed_General_G, ZScore, PValue, Report_File (empty).
    INFERRED: the conceptualization's default, as SpatialAutocorrelation's.
    """
    _no_report("HighLowClustering", Generate_Report)
    features, _, _, srid = _features("HighLowClustering", Input_Feature_Class, [Input_Field])
    n = len(features)
    if n < 4:
        raise ValueError("arcpy.stats.HighLowClustering needs four features or more.")
    x = _values("HighLowClustering", features, Input_Field)
    if (x < 0).any():
        raise ValueError("arcpy.stats.HighLowClustering: the Input Field holds negative values; the tool takes only "
                         "values of 0 or more.")
    w = _standardized(_weights("HighLowClustering", features, srid, Conceptualization_of_Spatial_Relationships,
                               Distance_Method, Distance_Band_or_Threshold_Distance, number_of_neighbors), Standardization)
    total, squares = float(x.sum()), float((x * x).sum())
    g = float(x @ w @ x) / (total * total - squares)
    big_w = float(w.sum())
    expected = big_w / (n * (n - 1))
    s1 = 0.5 * float(((w + w.T) ** 2).sum())
    s2 = float(((w.sum(axis=1) + w.sum(axis=0)) ** 2).sum())
    d0 = (n * n - 3 * n + 3) * s1 - n * s2 + 3 * big_w ** 2
    d1 = -((n * n - n) * s1 - 2 * n * s2 + 6 * big_w ** 2)
    d2 = -(2 * n * s1 - (n + 3) * s2 + 6 * big_w ** 2)
    d3 = 4 * (n - 1) * s1 - 2 * (n + 1) * s2 + 8 * big_w ** 2
    d4 = s1 - s2 + big_w ** 2
    cubes, fourths = float((x ** 3).sum()), float((x ** 4).sum())
    a = d0 * squares ** 2 + d1 * fourths + d2 * total ** 2 * squares
    b = d3 * total * cubes + d4 * total ** 4
    c = (total * total - squares) ** 2 * n * (n - 1) * (n - 2) * (n - 3)
    variance = (a + b) / c - expected ** 2
    z = (g - expected) / math.sqrt(variance)
    return Result(_text(g), _text(z), _text(_p(z)), "")


# ---- Mapping Clusters ---------------------------------------------------------------------------------------------

def _bin(z, p):
    """Gi_Bin: ±3 at 99 percent confidence, ±2 at 95, ±1 at 90, 0 otherwise. INFERRED: the levels are read from the
    p-value (p < 0.01, < 0.05, < 0.10) and the sign from the z-score."""
    level = 3 if p < 0.01 else 2 if p < 0.05 else 1 if p < 0.10 else 0
    return int(math.copysign(level, z)) if level else 0


def _local_output(in_features, out_feature_class, features, columns, kind, srid, field, extra):
    """The output feature class of a local statistic: SOURCE_ID, the input field, then ``extra`` [(name, type, values)]."""
    source = {c.name.lower(): c for c in columns}[field.lower()]
    out = _out(out_feature_class)
    written_columns = [_gpkg.Column("SOURCE_ID", "Integer"), _gpkg.Column(source.name, source.type),
                       *[_gpkg.Column(name, kind_) for name, kind_, _ in extra]]
    _gpkg.create(out, kind, srid, written_columns)
    with da.InsertCursor(out, [*[c.name for c in written_columns], "SHAPE@"]) as rows:
        for i, f in enumerate(features):
            rows.insertRow((f[0], f[1][field.lower()], *[values[i] for _, _, values in extra], f[4]))
    return out


def HotSpots(Input_Feature_Class, Input_Field, Output_Feature_Class,
             Conceptualization_of_Spatial_Relationships="FIXED_DISTANCE_BAND", Distance_Method="EUCLIDEAN_DISTANCE",
             Standardization="NONE", Distance_Band_or_Threshold_Distance=None, Self_Potential_Field=None,
             Weights_Matrix_File=None, Apply_False_Discovery_Rate__FDR__Correction="NO_FDR", number_of_neighbors=None):
    """Getis-Ord Gi*, from "How Hot Spot Analysis (Getis-Ord Gi*) works":
    ``Gi* = (Σ_j w_ij x_j − X̄ Σ_j w_ij) / (S sqrt((n Σ_j w_ij² − (Σ_j w_ij)²)/(n−1)))`` with
    ``S = sqrt(Σx_j²/n − X̄²)``, the sums over every feature j, the feature itself included. Standardization has no
    effect, as documented.

    Writes SOURCE_ID, the input field, GiZScore, GiPValue and Gi_Bin. Outputs, in order: the output feature class,
    "GiZScore", "GiPValue", "SOURCE_ID".
    INFERRED: a feature's weight on itself is 1 — the coincident-point weight of the distance conceptualizations and
    the self potential an .swm file defaults to — or its Self_Potential_Field value when one is given.
    INFERRED: the output carries no NNeighbors field, which the documentation does not name.
    """
    import numpy as np
    _no_fdr("HotSpots", Apply_False_Discovery_Rate__FDR__Correction)
    fields = [Input_Field] + ([Self_Potential_Field] if _given(Self_Potential_Field) else [])
    features, columns, kind, srid = _features("HotSpots", Input_Feature_Class, fields)
    n = len(features)
    if n < 3:
        raise ValueError("arcpy.stats.HotSpots needs three features or more.")
    x = _values("HotSpots", features, Input_Field)
    w = _weights("HotSpots", features, srid, Conceptualization_of_Spatial_Relationships, Distance_Method,
                 Distance_Band_or_Threshold_Distance, number_of_neighbors, self_weight=True)
    if _given(Self_Potential_Field):
        np.fill_diagonal(w, _values("HotSpots", features, Self_Potential_Field))
    mean = x.mean()
    s = math.sqrt(float((x * x).sum()) / n - mean * mean)
    if s == 0:
        raise ValueError("arcpy.stats.HotSpots: the Input Field has one value everywhere; the statistic needs variation.")
    sums = w.sum(axis=1)
    spread = np.sqrt((n * (w * w).sum(axis=1) - sums ** 2) / (n - 1))
    z = (w @ x - mean * sums) / (s * spread)
    p = [_p(v) for v in z]
    out = _local_output(Input_Feature_Class, Output_Feature_Class, features, columns, kind, srid, Input_Field,
                        [("GiZScore", "Double", [float(v) for v in z]), ("GiPValue", "Double", p),
                         ("Gi_Bin", "Integer", [_bin(float(v), q) for v, q in zip(z, p)])])
    return Result(out, "GiZScore", "GiPValue", "SOURCE_ID")


def ClustersOutliers(Input_Feature_Class, Input_Field, Output_Feature_Class,
                     Conceptualization_of_Spatial_Relationships="INVERSE_DISTANCE", Distance_Method="EUCLIDEAN_DISTANCE",
                     Standardization="ROW", Distance_Band_or_Threshold_Distance=None, Weights_Matrix_File=None,
                     Apply_False_Discovery_Rate__FDR__Correction="NO_FDR", Number_of_Permutations=499,
                     number_of_neighbors=None):
    """Anselin Local Moran's I, from "How Cluster and Outlier Analysis (Anselin Local Moran's I) works":
    ``I_i = (x_i − X̄)/S² Σ_{j≠i} w_ij (x_j − X̄)``, ``S² = Σ(x_i − X̄)²/(n−1)``, ``E[I_i] = −Σ_{j≠i} w_ij/(n−1)``,
    ``V[I_i] = E[I_i²] − E[I_i]²`` and, from its additional mathematics, ``E[I_i²] = A − B`` with
    ``A = (n − b2i) Σ_{j≠i} w_ij² / (n−1)``, ``B = (2 b2i − n) Σ_{k≠i} Σ_{h≠i} w_ik w_ih / ((n−1)(n−2))``.

    Only ``Number_of_Permutations=0`` is built — "the result is a traditional p-value instead of a pseudo p-value";
    the default 499 permutes values at random and is refused, so a script must pass 0.

    Writes SOURCE_ID, the input field, LMiIndex, LMiZScore, LMiPValue and COType. COType is HH, LL, HL or LH for a
    feature significant at p < 0.05, NN for a feature with no neighbours, and empty otherwise. Outputs, in order: the
    output feature class, "LMiIndex", "LMiZScore", "LMiPValue", "COType", "SOURCE_ID".
    INFERRED: ``b2i = Σ_{k≠i} (x_k − X̄)⁴ / (Σ_{k≠i} (x_k − X̄)²)²`` — equation (9) exactly as printed, its index
    read as "every feature but i"; Anselin (1995) scales the same ratio by the count, and the page does not.
    INFERRED: the index, z-score and p-value field names, which the page lists only as "Index field name" and so on.
    INFERRED: the first letter of COType is the sign of x_i − X̄ and the second the opposite of it when I_i < 0.
    """
    import numpy as np
    _no_fdr("ClustersOutliers", Apply_False_Discovery_Rate__FDR__Correction)
    if _given(Number_of_Permutations) and int(Number_of_Permutations) != 0:
        _refuse("ClustersOutliers", "Number_of_Permutations", Number_of_Permutations, "permutation pseudo p-values are "
                "random; pass 0 for the traditional p-value")
    features, columns, kind, srid = _features("ClustersOutliers", Input_Feature_Class, [Input_Field])
    n = len(features)
    if n < 3:
        raise ValueError("arcpy.stats.ClustersOutliers needs three features or more.")
    x = _values("ClustersOutliers", features, Input_Field)
    w = _standardized(_weights("ClustersOutliers", features, srid, Conceptualization_of_Spatial_Relationships,
                               Distance_Method, Distance_Band_or_Threshold_Distance, number_of_neighbors), Standardization)
    dev = x - x.mean()
    s2 = float((dev * dev).sum()) / (n - 1)
    if s2 == 0:
        raise ValueError("arcpy.stats.ClustersOutliers: the Input Field has one value everywhere.")
    index, zs, ps, types = [], [], [], []
    for i in range(n):
        row = w[i]
        others = np.arange(n) != i
        ii = dev[i] / s2 * float(row @ dev)
        expected = -float(row.sum()) / (n - 1)
        b2i = float((dev[others] ** 4).sum()) / float((dev[others] ** 2).sum()) ** 2
        a = (n - b2i) * float((row * row).sum()) / (n - 1)
        b = (2 * b2i - n) * float(row.sum()) ** 2 / ((n - 1) * (n - 2))
        variance = a - b - expected ** 2
        if not row.any():
            index.append(ii)
            zs.append(None)
            ps.append(None)
            types.append("NN")
            continue
        z = (ii - expected) / math.sqrt(variance)
        p = _p(z)
        high = dev[i] > 0
        kind_ = ""
        if p < 0.05:
            kind_ = ("HH" if high else "LL") if ii > 0 else ("HL" if high else "LH")
        index.append(ii)
        zs.append(z)
        ps.append(p)
        types.append(kind_)
    out = _local_output(Input_Feature_Class, Output_Feature_Class, features, columns, kind, srid, Input_Field,
                        [("LMiIndex", "Double", index), ("LMiZScore", "Double", zs), ("LMiPValue", "Double", ps),
                         ("COType", "String", types)])
    return Result(out, "LMiIndex", "LMiZScore", "LMiPValue", "COType", "SOURCE_ID")


# ---- Measuring Geographic Distributions ---------------------------------------------------------------------------

def _cases(tool, path, weight_field=None, case_field=None, more=()):
    """Features grouped by the case field (nulls left out, as documented), in the order of the case values."""
    fields = [f for f in [weight_field, case_field, *more] if _given(f)]
    features, columns, kind, srid = _features(tool, path, fields)
    groups: dict = {}
    for f in features:
        key = f[1][case_field.lower()] if _given(case_field) else None
        if _given(case_field) and key is None:
            continue
        groups.setdefault(key, []).append(f)
    ordered = sorted(groups.items(), key=lambda item: (item[0] is None, item[0])) if _given(case_field) else list(groups.items())
    return ordered, columns, kind, srid


def _weights_of(tool, group, weight_field):
    import numpy as np
    if not _given(weight_field):
        return np.ones(len(group))
    values = [f[1][weight_field.lower()] for f in group]
    if any(v is None for v in values):
        raise ValueError(f"arcpy.stats.{tool}: '{weight_field}' is null in some features.")
    return np.asarray(values, dtype=float)


def _case_column(columns, case_field):
    if not _given(case_field):
        return []
    return [{c.name.lower(): c for c in columns}[case_field.lower()]]


def MeanCenter(Input_Feature_Class, Output_Feature_Class, Weight_Field=None, Case_Field=None, Dimension_Field=None):
    """A point at the (weighted) mean of the centroids, ``X̄ = Σ w_i x_i / Σ w_i``, one per case; its XCOORD and
    YCOORD, the case value, and the mean of the Dimension Field.

    INFERRED: the dimension mean is written in a field named as the Dimension Field, and is unweighted ("the average
    for all values in that field"). Z is not written: grapy's feature classes are two-dimensional.
    """
    import numpy as np
    groups, columns, _, srid = _cases("MeanCenter", Input_Feature_Class, Weight_Field, Case_Field,
                                      [Dimension_Field] if _given(Dimension_Field) else [])
    out = _out(Output_Feature_Class)
    dimension = [_gpkg.Column({c.name.lower(): c for c in columns}[Dimension_Field.lower()].name, "Double")] \
        if _given(Dimension_Field) else []
    written = [_gpkg.Column("XCOORD", "Double"), _gpkg.Column("YCOORD", "Double"), *_case_column(columns, Case_Field),
               *dimension]
    _gpkg.create(out, "Point", srid, written)
    with da.InsertCursor(out, [*[c.name for c in written], "SHAPE@"]) as rows:
        for key, group in groups:
            w = _weights_of("MeanCenter", group, Weight_Field)
            x = float((w * np.array([f[2] for f in group])).sum() / w.sum())
            y = float((w * np.array([f[3] for f in group])).sum() / w.sum())
            extra = []
            if _given(Dimension_Field):
                values = [f[1][Dimension_Field.lower()] for f in group if f[1][Dimension_Field.lower()] is not None]
                extra = [sum(values) / len(values) if values else None]
            rows.insertRow((x, y, *([key] if _given(Case_Field) else []), *extra, (x, y)))
    return Result(out)


def _median(values):
    ordered = sorted(values)
    m = len(ordered)
    if not m:
        return None
    return ordered[m // 2] if m % 2 else (ordered[m // 2 - 1] + ordered[m // 2]) / 2


def _weiszfeld(points, weights):
    """The point minimising the weighted sum of Euclidean distances, by Kuhn and Kuenne's iteration from the mean.

    INFERRED: it starts at the weighted mean centre and stops when a step moves less than 1e-12 of the points'
    extent (or after 100,000 steps); a candidate on an input point, where the iteration is undefined, stops there if
    that point is optimal by Kuhn's test, and otherwise steps off it as Vardi and Zhang (2000) do."""
    import numpy as np
    xy = np.asarray(points, dtype=float)
    w = np.asarray(weights, dtype=float)
    current = (w[:, None] * xy).sum(axis=0) / w.sum()
    scale = max(float(np.ptp(xy[:, 0])), float(np.ptp(xy[:, 1])), 1.0)
    for _ in range(100000):
        d = np.hypot(*(xy - current).T)
        at = d < 1e-15 * scale
        if at.any():
            # Vardi and Zhang's step off a data point: optimal there when the others' pull is no stronger than its weight.
            others = ~at
            pull = ((w[others, None] * (xy[others] - current)) / d[others, None]).sum(axis=0)
            strength, own = float(np.hypot(*pull)), float(w[at].sum())
            if strength <= own:
                return current
            step = (w[others] / d[others])[:, None]
            towards = (step * xy[others]).sum(axis=0) / step.sum()
            nxt = (1 - own / strength) * towards + (own / strength) * current
        else:
            step = (w / d)[:, None]
            nxt = (step * xy).sum(axis=0) / step.sum()
        if float(np.hypot(*(nxt - current))) < 1e-12 * scale:
            return nxt
        current = nxt
    return current


def MedianCenter(Input_Feature_Class, Output_Feature_Class, Weight_Field=None, Case_Field=None, Attribute_Field=None):
    """The point minimising the (weighted) Euclidean distance to every centroid — "an iterative procedure introduced
    by Kuhn and Kuenne (1962)" — one per case, with its XCOORD and YCOORD, the case value, and the median of each
    Attribute Field.

    INFERRED: an attribute median of an even count is the mean of the middle two, and is written in a field named as
    the attribute field. See ``_weiszfeld`` for the iteration's start and stop.
    """
    attributes = [a for a in _list(Attribute_Field) if _given(a)]
    groups, columns, _, srid = _cases("MedianCenter", Input_Feature_Class, Weight_Field, Case_Field, attributes)
    by_name = {c.name.lower(): c for c in columns}
    out = _out(Output_Feature_Class)
    written = [_gpkg.Column("XCOORD", "Double"), _gpkg.Column("YCOORD", "Double"), *_case_column(columns, Case_Field),
               *[_gpkg.Column(by_name[a.lower()].name, "Double") for a in attributes]]
    _gpkg.create(out, "Point", srid, written)
    with da.InsertCursor(out, [*[c.name for c in written], "SHAPE@"]) as rows:
        for key, group in groups:
            x, y = (float(v) for v in _weiszfeld([(f[2], f[3]) for f in group],
                                                  _weights_of("MedianCenter", group, Weight_Field)))
            medians = [_median([f[1][a.lower()] for f in group if f[1][a.lower()] is not None]) for a in attributes]
            rows.insertRow((x, y, *([key] if _given(Case_Field) else []), *medians, (x, y)))
    return Result(out)


def CentralFeature(Input_Feature_Class, Output_Feature_Class, Distance_Method="EUCLIDEAN_DISTANCE", Weight_Field=None,
                   Self_Potential_Weight_Field=None, Case_Field=None):
    """The feature whose centroid has the least accumulated distance to every other feature's, copied with its
    attributes — every one of them when several tie, as documented — one or more per case.

    INFERRED: a weight weighs the destination, ``Σ_j w_j d_ij`` ("used to weight distances in the origin-destination
    distance matrix"). Self_Potential_Weight_Field is refused: how a self-potential enters the sum is not documented.
    """
    if _given(Self_Potential_Weight_Field):
        _refuse("CentralFeature", "Self_Potential_Weight_Field", Self_Potential_Weight_Field, "how a self-potential "
                "enters the accumulated distance is not documented")
    groups, columns, kind, srid = _cases("CentralFeature", Input_Feature_Class, Weight_Field, Case_Field)
    names = [c.name for c in columns]
    chosen = set()
    for _, group in groups:
        w = _weights_of("CentralFeature", group, Weight_Field)
        totals = _distances([(f[2], f[3]) for f in group], Distance_Method) @ w
        least = float(totals.min())
        chosen.update(f[0] for f, t in zip(group, totals) if math.isclose(float(t), least, rel_tol=1e-12, abs_tol=0.0)
                      or float(t) == least)
    out = _out(Output_Feature_Class)
    _gpkg.create(out, kind, srid, columns)
    with da.SearchCursor(Input_Feature_Class, ["OID@", *names, "SHAPE@"]) as rows, \
            da.InsertCursor(out, [*names, "SHAPE@"]) as written:
        for row in rows:
            if row[0] in chosen:
                written.insertRow(row[1:])
    return Result(out)


_SIZES = {"1_STANDARD_DEVIATION": 1, "2_STANDARD_DEVIATIONS": 2, "3_STANDARD_DEVIATIONS": 3}


def _size(tool, parameter, value):
    key = _keyword(value)
    if key not in _SIZES:
        raise ValueError(f"arcpy.stats.{tool}: {parameter} '{value}' is not one of {', '.join(_SIZES)}.")
    return _SIZES[key]


def _ellipse(cx, cy, semi_x, semi_y, rotation, vertices=360):
    """A polygon of ``vertices`` points on an ellipse of the given semi-axes, its y semi-axis turned ``rotation``
    degrees clockwise from north."""
    theta = math.radians(rotation)
    points = []
    for k in range(vertices):
        t = 2 * math.pi * k / vertices
        x, y = semi_x * math.cos(t), semi_y * math.sin(t)
        points.append((cx + x * math.cos(theta) + y * math.sin(theta), cy - x * math.sin(theta) + y * math.cos(theta)))
    from shapely.geometry import Polygon
    return Polygon(points)


def StandardDistance(Input_Feature_Class, Output_Standard_Distance_Feature_Class, Circle_Size="1_STANDARD_DEVIATION",
                     Weight_Field=None, Case_Field=None):
    """A circle around the (weighted) mean centre of radius ``k × SD``, from "How Standard Distance works":
    ``SD = sqrt(Σ(x_i − X̄)²/n + Σ(y_i − Ȳ)²/n)``, weighted ``SD_w = sqrt(Σw_i(x_i − X̄_w)²/Σw_i + Σw_i(y_i − Ȳ_w)²/Σw_i)``.

    INFERRED: the fields are CenterX, CenterY and StdDist, the names Directional Distribution documents, and StdDist
    holds the radius drawn (k × SD). INFERRED: the circle is a polygon of 360 vertices on it.
    """
    import numpy as np
    k = _size("StandardDistance", "Circle_Size", Circle_Size)
    groups, columns, _, srid = _cases("StandardDistance", Input_Feature_Class, Weight_Field, Case_Field)
    out = _out(Output_Standard_Distance_Feature_Class)
    written = [_gpkg.Column("CenterX", "Double"), _gpkg.Column("CenterY", "Double"), _gpkg.Column("StdDist", "Double"),
               *_case_column(columns, Case_Field)]
    _gpkg.create(out, "Polygon", srid, written)
    with da.InsertCursor(out, [*[c.name for c in written], "SHAPE@"]) as rows:
        for key, group in groups:
            w = _weights_of("StandardDistance", group, Weight_Field)
            xs, ys = np.array([f[2] for f in group]), np.array([f[3] for f in group])
            cx, cy = float((w * xs).sum() / w.sum()), float((w * ys).sum() / w.sum())
            sd = math.sqrt(float((w * (xs - cx) ** 2).sum() / w.sum()) + float((w * (ys - cy) ** 2).sum() / w.sum()))
            radius = k * sd
            rows.insertRow((cx, cy, radius, *([key] if _given(Case_Field) else []), _ellipse(cx, cy, radius, radius, 0)))
    return Result(out)


def DirectionalDistribution(Input_Feature_Class, Output_Ellipse_Feature_Class, Ellipse_Size="1_STANDARD_DEVIATION",
                            Weight_Field=None, Case_Field=None):
    """The standard deviational ellipse, from "How Directional Distribution works": the axes' standard deviations
    ``σ1,2 = sqrt(((Σx̃² + Σỹ²) ± sqrt((Σx̃² − Σỹ²)² + 4(Σx̃ỹ)²)) / 2n)``, scaled by the page's two-dimensional
    adjustment factor (1.41, 2.83, 4.24 for 1, 2, 3 standard deviations). Writes CenterX, CenterY, XStdDist, YStdDist,
    Rotation — "the rotation of the long axis measured clockwise from noon" — and the case field.

    INFERRED: the factor is k·sqrt 2, the exact value the table rounds, applied to the standard deviations (the table
    is captioned "for the variances", but its values are those of k·sqrt d, which scale standard deviations).
    INFERRED: YStdDist is the long axis and XStdDist the short one, so that the ellipse with x semi-axis XStdDist and
    y semi-axis YStdDist turned Rotation degrees clockwise is the one drawn; Rotation lies in [0, 180).
    INFERRED: weights enter as in the weighted standard distance, ``Σw x̃² / Σw``. The ellipse is 360 vertices.
    """
    import numpy as np
    k = _size("DirectionalDistribution", "Ellipse_Size", Ellipse_Size)
    groups, columns, _, srid = _cases("DirectionalDistribution", Input_Feature_Class, Weight_Field, Case_Field)
    out = _out(Output_Ellipse_Feature_Class)
    written = [_gpkg.Column("CenterX", "Double"), _gpkg.Column("CenterY", "Double"), _gpkg.Column("XStdDist", "Double"),
               _gpkg.Column("YStdDist", "Double"), _gpkg.Column("Rotation", "Double"), *_case_column(columns, Case_Field)]
    _gpkg.create(out, "Polygon", srid, written)
    with da.InsertCursor(out, [*[c.name for c in written], "SHAPE@"]) as rows:
        for key, group in groups:
            w = _weights_of("DirectionalDistribution", group, Weight_Field)
            xs, ys = np.array([f[2] for f in group]), np.array([f[3] for f in group])
            total = float(w.sum())
            cx, cy = float((w * xs).sum()) / total, float((w * ys).sum()) / total
            dx, dy = xs - cx, ys - cy
            sxx, syy, sxy = float((w * dx * dx).sum()), float((w * dy * dy).sum()), float((w * dx * dy).sum())
            root = math.sqrt((sxx - syy) ** 2 + 4 * sxy * sxy)
            long_ = math.sqrt(max((sxx + syy + root) / (2 * total), 0.0))
            short = math.sqrt(max((sxx + syy - root) / (2 * total), 0.0))
            # The long axis' direction: the eigenvector of [[sxx, sxy], [sxy, syy]] for the larger eigenvalue.
            angle = 0.5 * math.atan2(2 * sxy, sxx - syy)      # counterclockwise from east
            rotation = (90.0 - math.degrees(angle)) % 180.0    # clockwise from north
            factor = k * math.sqrt(2)
            sx, sy = short * factor, long_ * factor
            rows.insertRow((cx, cy, sx, sy, rotation, *([key] if _given(Case_Field) else []),
                            _ellipse(cx, cy, sx, sy, rotation)))
    return Result(out)


def DirectionalMean(Input_Feature_Class, Output_Feature_Class, Orientation_Only="DIRECTION", Case_Field=None):
    """Linear Directional Mean, from "How Linear Directional Mean works": ``LDM = atan(Σ sin θ_i / Σ cos θ_i)`` with the
    page's quadrant adjustment, ``CirVar = 1 − sqrt((Σ sin θ)² + (Σ cos θ)²)/n``. θ is each line's direction from its
    first point to its last, the only points the tool considers. Writes a line through the mean centre of the lines'
    centroids, as long as their mean length, along the mean direction, with CompassA (clockwise from north), DirMean
    (counterclockwise from east), CirVar, AveX, AveY, AveLen and the case field.

    ORIENTATION_ONLY is refused: how the from and to nodes are set aside is not given as a formula.
    INFERRED: the Rayleigh test fields (ZScore, PValue, RefValue, UnifTest) are not written — the page names them but
    gives neither the statistic's formula nor its critical value. INFERRED: a multipart line's first point is its first
    part's and its last its last part's.
    """
    import numpy as np
    if _keyword(Orientation_Only) in ("ORIENTATION_ONLY", "TRUE"):
        _refuse("DirectionalMean", "Orientation_Only", Orientation_Only, "how orientation without direction is averaged "
                "is not documented")
    groups, columns, kind, srid = _cases("DirectionalMean", Input_Feature_Class, None, Case_Field)
    if kind != "Polyline":
        raise ValueError("arcpy.stats.DirectionalMean: the input must be a line feature class.")
    out = _out(Output_Feature_Class)
    written = [_gpkg.Column("CompassA", "Double"), _gpkg.Column("DirMean", "Double"), _gpkg.Column("CirVar", "Double"),
               _gpkg.Column("AveX", "Double"), _gpkg.Column("AveY", "Double"), _gpkg.Column("AveLen", "Double"),
               *_case_column(columns, Case_Field)]
    _gpkg.create(out, "Polyline", srid, written)
    with da.InsertCursor(out, [*[c.name for c in written], "SHAPE@"]) as rows:
        for key, group in groups:
            sines, cosines, lengths = [], [], []
            for f in group:
                parts = list(getattr(f[4], "geoms", [f[4]]))
                (x0, y0), (x1, y1) = parts[0].coords[0][:2], parts[-1].coords[-1][:2]
                theta = math.atan2(y1 - y0, x1 - x0)
                sines.append(math.sin(theta))
                cosines.append(math.cos(theta))
                lengths.append(f[4].length)
            s, c, n = sum(sines), sum(cosines), len(group)
            mean = math.degrees(math.atan2(s, c)) % 360.0
            compass = (90.0 - mean) % 360.0
            variance = 1 - math.hypot(s, c) / n
            ax = float(np.mean([f[2] for f in group]))
            ay = float(np.mean([f[3] for f in group]))
            length = float(np.mean(lengths))
            ux, uy = math.cos(math.radians(mean)) * length / 2, math.sin(math.radians(mean)) * length / 2
            from shapely.geometry import LineString
            rows.insertRow((compass, mean, variance, ax, ay, length, *([key] if _given(Case_Field) else []),
                            LineString([(ax - ux, ay - uy), (ax + ux, ay + uy)])))
    return Result(out)


def MultiDistanceSpatialClustering(Input_Feature_Class, Output_Table, Number_of_Distance_Bands=10,
                                   Compute_Confidence_Envelope="0_PERMUTATIONS_-_NO_CONFIDENCE_ENVELOPE",
                                   Display_Results_Graphically="NO_DISPLAY", Weight_Field=None, Beginning_Distance=None,
                                   Distance_Increment=None, Boundary_Correction_Method="NONE",
                                   Study_Area_Method="MINIMUM_ENCLOSING_RECTANGLE", Study_Area_Feature_Class=None):
    """Ripley's K as L(d), from "How Multi-Distance Spatial Cluster Analysis (Ripley's K-function) works":
    ``L(d) = sqrt(A ΣΣ_{j≠i} k_ij / (π n (n−1)))``, k_ij 1 for a pair within d and 0 otherwise, A the study area — the
    minimum enclosing rectangle, or the one polygon of the study area feature class. Writes a table of ExpectedK (the
    distance), ObservedK and DiffK (ObservedK − ExpectedK), one row per distance band.

    Distances: with no Distance_Increment, the maximum distance is 25 percent of the enclosing rectangle's longer side;
    the increment is (maximum − Beginning_Distance)/bands, or maximum/bands with the beginning equal to it.

    Refused: confidence envelopes (they are random permutations), a Weight_Field (the weighted k is not given as a
    formula), and every boundary correction but NONE (each is described, not specified). A study area that leaves
    input points outside is refused too: whether those points count in n is not documented.
    INFERRED: a pair exactly d apart counts — the usage notes say "less than or equal to d", the "How … works" page
    "less than d". INFERRED: the minimum enclosing rectangle is the rotated rectangle of least area, and its longer
    side is the "maximum extent length". INFERRED: Number_of_Distance_Bands defaults to 10, as the page says.
    """
    import numpy as np
    import shapely
    tool = "MultiDistanceSpatialClustering"
    if not _keyword(Compute_Confidence_Envelope).startswith("0_PERMUTATIONS"):
        _refuse(tool, "Compute_Confidence_Envelope", Compute_Confidence_Envelope, "a confidence envelope is built from "
                "random permutations")
    if _given(Weight_Field):
        _refuse(tool, "Weight_Field", Weight_Field, "the weighted K function is not given as a formula")
    if _keyword(Boundary_Correction_Method) != "NONE":
        _refuse(tool, "Boundary_Correction_Method", Boundary_Correction_Method, "the edge corrections are described, not "
                "specified")
    features, _, _, srid = _features(tool, Input_Feature_Class)
    n = len(features)
    if n < 2:
        raise ValueError(f"arcpy.stats.{tool} needs two features or more.")
    points = shapely.MultiPoint([(f[2], f[3]) for f in features])
    rectangle = shapely.minimum_rotated_rectangle(points)
    if _keyword(Study_Area_Method) == "USER_PROVIDED_STUDY_AREA_FEATURE_CLASS":
        if not _given(Study_Area_Feature_Class):
            raise ValueError(f"arcpy.stats.{tool}: a user-provided study area needs Study_Area_Feature_Class.")
        with da.SearchCursor(Study_Area_Feature_Class, ["SHAPE@"]) as rows:
            areas = [r[0]._shape for r in rows if r[0] is not None]
        if len(areas) != 1:
            raise ValueError(f"arcpy.stats.{tool}: the study area feature class must hold exactly one polygon.")
        if not areas[0].covers(points):
            _refuse(tool, "Study_Area_Feature_Class", Study_Area_Feature_Class, "input points lie outside the study "
                    "area, and whether they count in n is not documented")
        area = areas[0].area
    else:
        area = rectangle.area
    if area <= 0:
        raise ValueError(f"arcpy.stats.{tool}: the study area has no area.")
    bands = int(Number_of_Distance_Bands)
    if _given(Distance_Increment):
        increment = float(Distance_Increment)
        beginning = float(Beginning_Distance) if _given(Beginning_Distance) else increment
    else:
        corners = list(rectangle.exterior.coords)
        sides = [math.dist(corners[0], corners[1]), math.dist(corners[1], corners[2])]
        longest = 0.25 * max(sides)
        if _given(Beginning_Distance):
            beginning = float(Beginning_Distance)
            increment = (longest - beginning) / bands
        else:
            increment = longest / bands
            beginning = increment
    d = _distances([(f[2], f[3]) for f in features])
    off = ~np.eye(n, dtype=bool)
    out = _out(Output_Table)
    columns = [_gpkg.Column("ExpectedK", "Double"), _gpkg.Column("ObservedK", "Double"), _gpkg.Column("DiffK", "Double")]
    _gpkg.create(out, "Geometry", 0, columns)
    with da.InsertCursor(out, ["ExpectedK", "ObservedK", "DiffK", "SHAPE@"]) as rows:
        for k in range(bands):
            distance = beginning + k * increment
            pairs = int(((d <= distance) & off).sum())
            observed = math.sqrt(area * pairs / (math.pi * n * (n - 1)))
            rows.insertRow((distance, observed, observed - distance, None))
    return Result(out)


# ---- Utilities ----------------------------------------------------------------------------------------------------

def CalculateDistanceBand(Input_Features, Neighbors, Distance_Method="EUCLIDEAN_DISTANCE"):
    """The minimum, average and maximum of each feature's distance to its N-th nearest neighbour.

    Outputs, in order: Minimum_Distance, Average_Distance, Maximum_Distance.
    INFERRED: coincident features are neighbours at distance 0.
    """
    import numpy as np
    features, _, _, _ = _features("CalculateDistanceBand", Input_Features)
    n = len(features)
    k = int(Neighbors)
    if not 1 <= k < n:
        raise ValueError(f"arcpy.stats.CalculateDistanceBand: Neighbors must be between 1 and {n - 1}.")
    d = _distances([(f[2], f[3]) for f in features], Distance_Method)
    d = d + np.diag(np.full(n, np.inf))
    kth = np.sort(d, axis=1)[:, k - 1]
    return Result(_text(kth.min()), _text(kth.mean()), _text(kth.max()))


def CollectEvents(Input_Incident_Features, Output_Weighted_Point_Feature_Class):
    """One point per distinct centroid location, with ICOUNT the number of features there. Only "the exact same x and
    y centroid coordinates" combine.

    Outputs, in order: the output feature class, "ICOUNT", the largest count.
    INFERRED: the points are written in the order each location is first met, and carry ICOUNT alone.
    """
    features, _, _, srid = _features("CollectEvents", Input_Incident_Features)
    counts: dict = {}
    for f in features:
        counts[(f[2], f[3])] = counts.get((f[2], f[3]), 0) + 1
    out = _out(Output_Weighted_Point_Feature_Class)
    _gpkg.create(out, "Point", srid, [_gpkg.Column("ICOUNT", "Integer")])
    with da.InsertCursor(out, ["ICOUNT", "SHAPE@"]) as rows:
        for xy, count in counts.items():
            rows.insertRow((count, xy))
    return Result(out, "ICOUNT", _text(max(counts.values()) if counts else 0))


_REFUSED = {
    "OptimizedHotSpotAnalysis": "it aggregates incidents and chooses its scale by procedures the documentation "
                                "describes only in outline",
    "OptimizedOutlierAnalysis": "it chooses its scale and permutes values at random",
    "GenerateSpatialWeightsMatrix": "its output, an .swm file, has no published layout",
    "GenerateNetworkSpatialWeights": "it needs a network dataset and writes an .swm file",
    "GenerateNetworkSWM": "it needs a network dataset and writes an .swm file",
    "Forest": "random forests are random by definition",
    "DensityBasedClustering": "HDBSCAN and OPTICS are not in the libraries grapy runs on",
}


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)

    def missing(*_, **__):
        why = _REFUSED.get(name)
        raise NotBuiltError(f"arcpy.stats.{name} is not built in grapy" + (f" — {why}." if why else " yet."))

    return missing
