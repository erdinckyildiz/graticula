"""arcpy.stats — the Spatial Statistics tools grapy builds, by their documented names and parameters (ADR-223 §5.2, ADR-224).

Every formula here is the one Esri's public "How … works" page gives for the tool, and each docstring names it. A
feature's location is its centroid, as the tools document: a point is itself; a line, polygon or multipart feature is
"the weighted mean center of all feature parts", weighted by length or area, which is the shape's centroid.

What the documentation does not settle is refused or chosen and said: a refusal names the tool, the parameter and the
value (NotBuiltError), and a choice is written ``INFERRED:`` in the tool's docstring.
Refused everywhere, unless said to be built as an equivalent (ADR-224 §5.6):

- a geographic reference — the tools measure it in chordal metres on an oblate spheroid, which grapy does not;
- ``GET_SPATIAL_WEIGHTS_FROM_FILE`` (an .swm file's binary layout is not published) and ``ZONE_OF_INDIFFERENCE``
  (the documentation says only that weights "diminish with distance" past the band, not how);
- ``APPLY_FDR`` is built as an equivalent, Benjamini–Hochberg (the correction "for multiple testing and spatial
  dependence" is not given as a formula);
- ``GENERATE_REPORT`` (the report's content is not documented);
- permutation p-values (``Number_of_Permutations`` above 0) are built as an equivalent with a stated seed.

p-values are two-sided, from the standard normal distribution — ``erfc(|z| / sqrt 2)`` — as Esri's "What is a
z-score? What is a p-value?" describes them. INFERRED: a derived scalar output is returned as text, as arcpy's
Result hands back derived values (``result[0]`` is concatenated with text in Esri's own samples).
"""
from __future__ import annotations

import math

from . import _gpkg, da
from ._result import Result
from ._runtime import NotBuiltError
from ._runtime import _is_given  # noqa: E402 — a Raster is never compared with ==


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
    return _is_given(value)


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


def _wants_fdr(fdr):
    return _keyword(fdr) in ("APPLY_FDR", "TRUE")


def _seed():
    from .management import _seed as seed
    return seed()


def _bh_significant(p_values, alpha):
    """Which p-values the Benjamini–Hochberg procedure rejects at false discovery rate ``alpha`` (Benjamini and
    Hochberg 1995): with the m p-values sorted, the largest k with p_(k) ≤ kα/m, and every p up to p_(k)."""
    present = sorted(p for p in p_values if p is not None)
    m = len(present)
    cut = None
    for k, value in enumerate(present, start=1):
        if value <= k * alpha / m:
            cut = value
    return [p is not None and cut is not None and p <= cut for p in p_values]


def _fdr_equivalent():
    from ._runtime import equivalent

    @equivalent("the Benjamini–Hochberg false discovery rate", "Benjamini and Hochberg 1995")
    def FalseDiscoveryRate():
        """EQUIVALENT: Esri's correction "for multiple testing and spatial dependence" is not given as a formula;
        grapy applies Benjamini and Hochberg's (1995) at each confidence level, which does not adjust for spatial
        dependence."""
    return FalseDiscoveryRate


_FDR = None


def _say_fdr():
    global _FDR
    if _FDR is None:
        _FDR = _fdr_equivalent()
    _FDR()


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
    if variance <= 1e-15:
        raise ValueError("Moran's I has no variance here: every feature is the neighbour of every other.")
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
    fdr = _wants_fdr(Apply_False_Discovery_Rate__FDR__Correction)
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
    if fdr:
        _say_fdr()
        levels = {alpha: _bh_significant(p, alpha) for alpha in (0.10, 0.05, 0.01)}
        bins = [int(math.copysign(3 if levels[0.01][i] else 2 if levels[0.05][i] else 1, float(z[i])))
                if levels[0.10][i] else 0 for i in range(n)]
    else:
        bins = [_bin(float(v), q) for v, q in zip(z, p)]
    out = _local_output(Input_Feature_Class, Output_Feature_Class, features, columns, kind, srid, Input_Field,
                        [("GiZScore", "Double", [float(v) for v in z]), ("GiPValue", "Double", p),
                         ("Gi_Bin", "Integer", bins)])
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

    With ``Number_of_Permutations=0`` the p-value is the traditional one, as documented. With more, LMiPValue is a
    conditional-permutation pseudo p-value — an EQUIVALENT (see ``_pseudo_p``), seeded from env.randomGenerator, so its
    numbers differ from Pro's draws. APPLY_FDR is the Benjamini–Hochberg rate at 0.05 — an EQUIVALENT (see
    ``_bh_significant``), which does not adjust for spatial dependence as Esri's correction is said to.

    Writes SOURCE_ID, the input field, LMiIndex, LMiZScore, LMiPValue and COType. COType is HH, LL, HL or LH for a
    feature significant at p < 0.05, NN for a feature with no neighbours, and empty otherwise. Outputs, in order: the
    output feature class, "LMiIndex", "LMiZScore", "LMiPValue", "COType", "SOURCE_ID".
    INFERRED: ``b2i = Σ_{k≠i} (x_k − X̄)⁴ / (Σ_{k≠i} (x_k − X̄)²)²`` — equation (9) exactly as printed, its index
    read as "every feature but i"; Anselin (1995) scales the same ratio by the count, and the page does not.
    INFERRED: the index, z-score and p-value field names, which the page lists only as "Index field name" and so on.
    INFERRED: the first letter of COType is the sign of x_i − X̄ and the second the opposite of it when I_i < 0.
    """
    import numpy as np
    fdr = _wants_fdr(Apply_False_Discovery_Rate__FDR__Correction)
    permutations = int(Number_of_Permutations) if _given(Number_of_Permutations) else 0
    if permutations < 0:
        raise ValueError("arcpy.stats.ClustersOutliers: Number_of_Permutations is 0 or more.")
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
        index.append(ii)
        zs.append(z)
        ps.append(_p(z))
        types.append("")
    if permutations:
        ps = _pseudo_p(dev, w, s2, index, ps, permutations)
    significant = _bh_significant(ps, 0.05) if fdr else [p is not None and p < 0.05 for p in ps]
    if fdr:
        _say_fdr()
    for i in range(n):
        if types[i] != "NN" and significant[i]:
            high = dev[i] > 0
            types[i] = ("HH" if high else "LL") if index[i] > 0 else ("HL" if high else "LH")
    out = _local_output(Input_Feature_Class, Output_Feature_Class, features, columns, kind, srid, Input_Field,
                        [("LMiIndex", "Double", index), ("LMiZScore", "Double", zs), ("LMiPValue", "Double", ps),
                         ("COType", "String", types)])
    return Result(out, "LMiIndex", "LMiZScore", "LMiPValue", "COType", "SOURCE_ID")


def _pseudo_p_equivalent():
    from ._runtime import equivalent

    @equivalent("conditional permutation pseudo p-values with a stated seed", "Anselin 1995, §4")
    def ConditionalPermutation(dev, w, s2, index, analytical, permutations):
        """EQUIVALENT: Anselin's (1995) conditional permutation. For each feature its own value is held and the
        others' are permuted ``permutations`` times; the pseudo p-value is (M + 1)/(R + 1), M the permutations whose
        I_i is at least as extreme, on the side of the permutations' mean the observed value lies. The draws are
        NumPy's PCG64 from env.randomGenerator's seed (0 when unset): a run repeats itself, and Pro's draws differ."""
        import numpy as np
        random = np.random.default_rng(_seed())
        n = len(dev)
        out = list(analytical)
        for i in range(n):
            if analytical[i] is None:
                continue
            others = np.delete(dev, i)
            row = np.delete(w[i], i)
            draws = np.array([dev[i] / s2 * float(row @ random.permutation(others)) for _ in range(permutations)])
            larger = int((draws >= index[i]).sum())
            if permutations - larger < larger:
                larger = permutations - larger
            out[i] = (larger + 1) / (permutations + 1)
        return out
    return ConditionalPermutation


_PSEUDO = None


def _pseudo_p(dev, w, s2, index, analytical, permutations):
    global _PSEUDO
    if _PSEUDO is None:
        _PSEUDO = _pseudo_p_equivalent()
    return _PSEUDO(dev, w, s2, index, analytical, permutations)


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


def _delaunay_neighbours(tool, xy):
    """Each point's Delaunay neighbours, "sharing edges … in their Delaunay triangulation" — refused where that
    triangulation is not unique (four points or more on one circle), since which diagonal is drawn is then a choice."""
    import numpy as np
    from scipy.spatial import Delaunay
    points = np.asarray(xy, dtype=float)
    if len({tuple(p) for p in points}) != len(points):
        _refuse(tool, "neighborhood_type", "DELAUNAY_TRIANGULATION", "coincident points have no Delaunay triangulation")
    try:
        triangulation = Delaunay(points - points.min(axis=0))   # Qhull drops points on projected coordinates
    except Exception:  # Collinear points: scipy's Qhull raises its own error type.
        _refuse(tool, "neighborhood_type", "DELAUNAY_TRIANGULATION", "the points are collinear and have no triangulation")
    scale = float(np.ptp(points, axis=0).max()) or 1.0
    for t, simplex in enumerate(triangulation.simplices):
        for k, other in enumerate(triangulation.neighbors[t]):
            if other < 0:
                continue
            opposite = [v for v in triangulation.simplices[other] if v not in simplex][0]
            a, b, c = points[simplex]
            d = points[opposite]
            rows = [[p[0] - d[0], p[1] - d[1], (p[0] - d[0]) ** 2 + (p[1] - d[1]) ** 2] for p in (a, b, c)]
            if abs(np.linalg.det(np.array(rows))) <= 1e-9 * scale ** 4:
                _refuse(tool, "neighborhood_type", "DELAUNAY_TRIANGULATION", "four or more points lie on one circle, so "
                        "the triangulation is not unique")
    neighbours = [set() for _ in points]
    for simplex in triangulation.simplices:
        for i in simplex:
            neighbours[i].update(int(j) for j in simplex if j != i)
    return [sorted(n) for n in neighbours]


def _quantile(values, weights, p):
    """The documented weighted p-quantile of values sorted ascending, weights summing to one: the mean of x_i and
    x_{i+1} when the weights through i sum to p exactly, otherwise the x_{i+1} whose cumulative weight passes p."""
    total = 0.0
    for i in range(len(values)):
        before = total
        total += weights[i]
        if i < len(values) - 1 and math.isclose(total, p, rel_tol=1e-12, abs_tol=1e-15):
            return (values[i] + values[i + 1]) / 2
        if before < p < total or (i == len(values) - 1):
            return values[i]
    return values[-1]


def _local_statistics(values, weights):
    """Mean, median, standard deviation, interquartile range, skewness and quantile imbalance, by the formulas of
    "How Neighborhood Summary Statistics works"."""
    order = sorted(range(len(values)), key=lambda i: values[i])
    x = [float(values[i]) for i in order]
    total = sum(weights)
    w = [weights[i] / total for i in order]
    mean = sum(wi * xi for wi, xi in zip(w, x))
    std = math.sqrt(sum(wi * (xi - mean) ** 2 for wi, xi in zip(w, x)))
    third = sum(wi * (xi - mean) ** 3 for wi, xi in zip(w, x))
    skew = math.copysign(abs(third) ** (1 / 3), third) / std if std > 0 else None
    median, q1, q3 = (_quantile(x, w, p) for p in (0.5, 0.25, 0.75))
    iqr = q3 - q1
    imbalance = (2 * median - (q1 + q3)) / iqr if iqr != 0 else None
    return {"MEAN": mean, "MEDIAN": median, "STD_DEV": std, "IQR": iqr, "SKEWNESS": skew, "QUANTILE_IMBALANCE": imbalance}


_NSS = {"MEAN": "MEAN", "MEDIAN": "MED", "STD_DEV": "STD", "IQR": "IQR", "SKEWNESS": "SKEW", "QUANTILE_IMBALANCE": "QIMB"}


def NeighborhoodSummaryStatistics(in_features, output_features, analysis_fields=None, local_summary_statistic="ALL",
                                  include_focal_feature="INCLUDE_FOCAL", ignore_nulls="IGNORE_NULLS",
                                  neighborhood_type=None, distance_band=None, number_of_neighbors=8,
                                  weights_matrix_file=None, local_weighting_scheme="UNWEIGHTED", kernel_bandwidth=None):
    """Local summary statistics of each feature's neighbourhood, by the formulas of "How Neighborhood Summary Statistics
    works": the weighted mean, standard deviation ``sqrt(Σw(x − x̄)²)``, skewness ``cbrt(Σw(x − x̄)³)/s``, and the
    weighted p-quantile's median, interquartile range and quantile imbalance ``(2 Med − (Q1 + Q3))/IQR``, weights
    normalised to sum to one (1/n unweighted). Kernels: bisquare ``(1 − (d/b)²)²`` and Gaussian ``exp(−½(d/b)²)``.

    Neighbourhoods: DISTANCE_BAND (default band: the shortest that gives every feature one other neighbour; at most
    the 1,000 nearest), NUMBER_OF_NEIGHBORS (8 by default, the focal feature not counted), CONTIGUITY_EDGES_ONLY,
    CONTIGUITY_EDGES_CORNERS (the default for polygons), DELAUNAY_TRIANGULATION (the default for points). The focal
    feature is included unless EXCLUDE_FOCAL; nulls are left out unless INCLUDE_NULLS, when any null makes every
    statistic of that field null. Statistics of the distances to the neighbours, the focal feature left out and
    unweighted, are written too.

    Refused: GET_SPATIAL_WEIGHTS_FROM_FILE; a kernel without kernel_bandwidth outside a distance band (it is then
    estimated by Kernel Density's rule, not built here); a bisquare kernel reaching a neighbour beyond its bandwidth
    (the formula as written turns positive again there); a Delaunay triangulation that is not unique.

    INFERRED: the field names — MEAN_, MED_, STD_, IQR_, SKEW_ and QIMB_ before the analysis field's name, NUM_NBRS_
    before it for the count used, and the same with DIST for the distances — which the documentation does not give.
    INFERRED: the output carries the analysis fields and these, not the input's other fields. INFERRED: a feature
    exactly at the band's distance is inside it; neighbours tied at the k-th distance are taken by object id; a skewness
    with s = 0 and an imbalance with IQR = 0 are null.
    """
    import numpy as np
    tool = "NeighborhoodSummaryStatistics"
    fields = [f for f in _list(analysis_fields) if _given(f)]
    features, columns, kind, srid = _features(tool, in_features, fields)
    n = len(features)
    if n < 2:
        raise ValueError(f"arcpy.stats.{tool} needs two features or more.")
    by_name = {c.name.lower(): c for c in columns}
    kind_of = _keyword(neighborhood_type) if _given(neighborhood_type) else (
        "CONTIGUITY_EDGES_CORNERS" if kind == "Polygon" else "DELAUNAY_TRIANGULATION")
    statistic = _keyword(local_summary_statistic) if _given(local_summary_statistic) else "ALL"
    wanted = list(_NSS) if statistic == "ALL" else [statistic]
    if any(s not in _NSS for s in wanted):
        raise ValueError(f"arcpy.stats.{tool}: local_summary_statistic '{local_summary_statistic}' is not one of ALL, "
                         f"{', '.join(_NSS)}.")
    focal = _keyword(include_focal_feature) not in ("EXCLUDE_FOCAL", "FALSE")
    skip_nulls = _keyword(ignore_nulls) not in ("INCLUDE_NULLS", "FALSE")
    scheme = _keyword(local_weighting_scheme) if _given(local_weighting_scheme) else "UNWEIGHTED"
    k = int(number_of_neighbors) if _given(number_of_neighbors) else 8
    neighbours, d, band = _neighbourhoods(tool, features, kind, srid, kind_of, distance_band, k, cap=1000)
    bandwidth = None
    if scheme != "UNWEIGHTED":
        if scheme not in ("BISQUARE", "GAUSSIAN"):
            raise ValueError(f"arcpy.stats.{tool}: local_weighting_scheme '{local_weighting_scheme}' is not one of "
                             "UNWEIGHTED, BISQUARE, GAUSSIAN.")
        if kind_of not in ("DISTANCE_BAND", "NUMBER_OF_NEIGHBORS"):
            raise ValueError(f"arcpy.stats.{tool}: a kernel weights only DISTANCE_BAND and NUMBER_OF_NEIGHBORS.")
        if _given(kernel_bandwidth):
            bandwidth = _threshold(kernel_bandwidth, srid)
        elif kind_of == "DISTANCE_BAND":
            bandwidth = band
        else:
            _refuse(tool, "kernel_bandwidth", "(empty)", "it is then estimated by Kernel Density's rule, which is not built")
        if scheme == "BISQUARE" and any(d[i, j] > bandwidth for i in range(n) for j in neighbours[i]):
            _refuse(tool, "kernel_bandwidth", kernel_bandwidth, "a neighbour lies beyond it, where the bisquare formula as "
                    "written turns positive again")

    def kernel(distance):
        if scheme == "UNWEIGHTED":
            return 1.0
        ratio = (distance / bandwidth) ** 2
        return (1 - ratio) ** 2 if scheme == "BISQUARE" else math.exp(-0.5 * ratio)

    out_columns = []
    for field in fields:
        name = by_name[field.lower()].name
        out_columns += [by_name[field.lower()], *[_gpkg.Column(f"{_NSS[s]}_{name}", "Double") for s in wanted],
                        _gpkg.Column(f"NUM_NBRS_{name}", "Integer")]
    out_columns += [*[_gpkg.Column(f"{_NSS[s]}_DIST", "Double") for s in wanted], _gpkg.Column("NUM_NBRS_DIST", "Integer")]
    out = _out(output_features)
    _gpkg.create(out, kind, srid, out_columns)
    with da.InsertCursor(out, [*[c.name for c in out_columns], "SHAPE@"]) as rows:
        for i, f in enumerate(features):
            members = ([i] if focal else []) + list(neighbours[i])
            row = []
            for field in fields:
                key = field.lower()
                pairs = [(features[j][1][key], kernel(d[i, j])) for j in members]
                present = [(v, w) for v, w in pairs if v is not None]
                if (not skip_nulls and len(present) != len(pairs)) or not present:
                    stats = {s: None for s in wanted}
                else:
                    stats = _local_statistics([v for v, _ in present], [w for _, w in present])
                row += [f[1][key], *[stats[s] for s in wanted], len(present)]
            distances = [float(d[i, j]) for j in neighbours[i]]
            stats = _local_statistics(distances, [1.0] * len(distances)) if distances else {s: None for s in wanted}
            row += [*[stats[s] for s in wanted], len(distances)]
            rows.insertRow((*row, f[4]))
    return Result(out)


def _rows_of(tool, path, fields):
    """(oid, {field: value}, shape) of every feature with a shape, the fields read case-insensitively."""
    columns, kind, srid, _ = _schema(path)
    by_name = {c.name.lower(): c.name for c in columns}
    names = []
    for field in fields:
        if field.lower() not in by_name:
            raise ValueError(f"arcpy.stats.{tool}: '{path}' has no field '{field}'.")
        names.append(by_name[field.lower()])
    out = []
    with da.SearchCursor(path, ["OID@", *names, "SHAPE@"]) as rows:
        for row in rows:
            out.append((row[0], dict(zip([n.lower() for n in names], row[1:-1])), row[-1]._shape if row[-1] else None))
    return out, columns, kind, srid


def SimilaritySearch(Input_Features_To_Match, Candidate_Features, Output_Features, Collapse_Output_To_Points="NO_COLLAPSE",
                     Most_Or_Least_Similar="MOST_SIMILAR", Match_Method="ATTRIBUTE_VALUES", Number_Of_Results=10,
                     Attributes_Of_Interest=None, Fields_To_Append_To_Output=None, scale="SCALE_DATA"):
    """The candidates most (or least) like the features to match, from "How Similarity Search works". Several features
    to match are averaged into one target. Each attribute is standardized — "the mean for all values is subtracted
    from each value and divided by the standard deviation for all values (both the Input Features to Match and the
    Candidate Features are included)". ATTRIBUTE_VALUES: SIMINDEX is the sum of squared differences from the target,
    smallest most similar. ATTRIBUTE_PROFILES: SIMINDEX is the cosine similarity ΣAB/(√ΣA² √ΣB²), largest most similar
    (two attributes or more).

    The output lists the features to match first (MATCH_ID their object id), then the matches (CAND_ID theirs), with
    the attributes of interest, the appended fields, SIMRANK (most similar first, 1 for the best) and/or DSIMRANK
    (least similar first), and SIMINDEX. Number_Of_Results 0, or more than there are candidates, ranks them all; with
    BOTH at most half the candidates each way. Geometry stays polygon or line when both inputs are, unless COLLAPSE;
    otherwise each feature is its centroid.

    Refused: RANKED_ATTRIBUTE_VALUES (the page does not say how ties are ranked, or whether the target is ranked among
    the candidates or with them), and ATTRIBUTE_PROFILES with NO_SCALE_DATA (whether the mean is still subtracted is
    not said, and the cosine depends on it).
    INFERRED: the standard deviation is the population one (divided by n); with ATTRIBUTE_VALUES and NO_SCALE_DATA the
    values are compared as they are. INFERRED: a feature with a null attribute of interest is left out; candidates
    with equal SIMINDEX are ranked by object id; the attribute values written are the original ones; the features to
    match carry no SIMINDEX; LABELRANK, a display field whose values are not documented, is not written.
    """
    import numpy as np
    tool = "SimilaritySearch"
    method = _keyword(Match_Method)
    if method == "RANKED_ATTRIBUTE_VALUES":
        _refuse(tool, "Match_Method", method, "how ties are ranked, and whether the target is ranked among the "
                "candidates, is not documented")
    if method not in ("ATTRIBUTE_VALUES", "ATTRIBUTE_PROFILES"):
        raise ValueError(f"arcpy.stats.{tool}: Match_Method '{Match_Method}' is not one it takes.")
    scaled = _keyword(scale) not in ("NO_SCALE_DATA", "FALSE")
    if method == "ATTRIBUTE_PROFILES" and not scaled:
        _refuse(tool, "scale", "NO_SCALE_DATA", "whether the mean is still subtracted for a cosine is not documented")
    direction = _keyword(Most_Or_Least_Similar)
    if direction not in ("MOST_SIMILAR", "LEAST_SIMILAR", "BOTH"):
        raise ValueError(f"arcpy.stats.{tool}: Most_Or_Least_Similar '{Most_Or_Least_Similar}' is not one it takes.")
    attributes = [a for a in _list(Attributes_Of_Interest) if _given(a)]
    if not attributes:
        raise ValueError(f"arcpy.stats.{tool} needs Attributes_Of_Interest.")
    candidate_columns = {c.name.lower() for c in _schema(Candidate_Features)[0]}
    kept = [a for a in attributes if a.lower() in candidate_columns]
    if len(kept) < len(attributes):
        from ._runtime import AddWarning
        AddWarning(f"Attributes missing from the candidates were dropped: "
                   f"{', '.join(a for a in attributes if a not in kept)}.")
    if not kept:
        raise ValueError(f"arcpy.stats.{tool}: none of the attributes of interest is in the candidates.")
    if method == "ATTRIBUTE_PROFILES" and len(kept) < 2:
        raise ValueError(f"arcpy.stats.{tool}: ATTRIBUTE_PROFILES needs two attributes of interest or more.")
    appended = [f for f in _list(Fields_To_Append_To_Output) if _given(f)]
    targets, target_columns, target_kind, srid = _rows_of(tool, Input_Features_To_Match, kept + appended)
    candidates, _, candidate_kind, candidate_srid = _rows_of(tool, Candidate_Features, kept + appended)
    _projected(tool, srid)
    if candidate_srid and srid and candidate_srid != srid:
        _refuse(tool, "Candidate_Features", Candidate_Features, "its reference differs from the features to match; "
                "project one first")
    keys = [a.lower() for a in kept]
    complete = lambda rows: [r for r in rows if r[2] is not None and all(r[1][k] is not None for k in keys)]
    targets, candidates = complete(targets), complete(candidates)
    if not targets or not candidates:
        raise ValueError(f"arcpy.stats.{tool} needs a feature to match and a candidate, each with every attribute.")
    t = np.array([[float(r[1][k]) for k in keys] for r in targets])
    c = np.array([[float(r[1][k]) for k in keys] for r in candidates])
    every = np.vstack([t, c])
    if scaled:
        mean, sd = every.mean(axis=0), every.std(axis=0)
        if (sd == 0).any():
            raise ValueError(f"arcpy.stats.{tool}: an attribute of interest has one value everywhere.")
        t, c = (t - mean) / sd, (c - mean) / sd
    target = t.mean(axis=0)
    if method == "ATTRIBUTE_VALUES":
        index = ((c - target) ** 2).sum(axis=1)
        order = sorted(range(len(candidates)), key=lambda i: (index[i], candidates[i][0]))
    else:
        norms = np.sqrt((c * c).sum(axis=1)) * math.sqrt(float((target * target).sum()))
        if (norms == 0).any():
            raise ValueError(f"arcpy.stats.{tool}: a cosine similarity is undefined for a vector of zeros.")
        index = (c @ target) / norms
        order = sorted(range(len(candidates)), key=lambda i: (-index[i], candidates[i][0]))
    count = int(Number_Of_Results) if _given(Number_Of_Results) else 10
    limit = len(candidates) // 2 if direction == "BOTH" else len(candidates)
    count = limit if count <= 0 or count > limit else count
    most = order[:count] if direction in ("MOST_SIMILAR", "BOTH") else []
    least = list(reversed(order))[:count] if direction in ("LEAST_SIMILAR", "BOTH") else []
    keep_shape = _keyword(Collapse_Output_To_Points) not in ("COLLAPSE", "TRUE") and target_kind == candidate_kind \
        and target_kind in ("Polygon", "Polyline")
    kind = target_kind if keep_shape else "Point"
    by_name = {col.name.lower(): col for col in target_columns}
    columns = [by_name[k] for k in keys] + [by_name[f.lower()] for f in appended if f.lower() not in keys]
    extra = [_gpkg.Column("MATCH_ID", "Integer"), _gpkg.Column("CAND_ID", "Integer")]
    if most:
        extra.append(_gpkg.Column("SIMRANK", "Integer"))
    if least:
        extra.append(_gpkg.Column("DSIMRANK", "Integer"))
    extra.append(_gpkg.Column("SIMINDEX", "Double"))
    out = _out(Output_Features)
    _gpkg.create(out, kind, srid, [*columns, *extra])
    names = [col.name.lower() for col in columns]

    def shape_of(shape):
        return shape if keep_shape or shape.geom_type == "Point" else shape.centroid

    with da.InsertCursor(out, [*[col.name for col in columns], *[e.name for e in extra], "SHAPE@"]) as rows:
        for oid, values, shape in targets:
            ranks = [None] * (bool(most) + bool(least))
            rows.insertRow((*[values[n] for n in names], oid, None, *ranks, None, shape_of(shape)))
        for position, i in enumerate(most, start=1):
            oid, values, shape = candidates[i]
            ranks = [position] + ([None] if least else [])
            rows.insertRow((*[values[n] for n in names], None, oid, *ranks, float(index[i]), shape_of(shape)))
        for position, i in enumerate(least, start=1):
            oid, values, shape = candidates[i]
            ranks = ([None] if most else []) + [position]
            rows.insertRow((*[values[n] for n in names], None, oid, *ranks, float(index[i]), shape_of(shape)))
    return Result(out)


def SpatialAssociationBetweenZones(input_feature_or_raster, categorical_zone_field, overlay_feature_or_raster,
                                   categorical_overlay_zone_field, output_features=None, output_raster=None,
                                   correspondence_overlay_to_input=None, correspondence_input_to_overlay=None):
    """The V-measure between two sets of polygon zones, from "How Spatial Association Between Zones works" and its
    reference (Nowosad and Stepinski 2018, after Rosenberg and Hirschberg 2007). With p_ij the share of the overlapping
    area where input zone i meets overlay zone j: H(O|I) = −Σ p_ij ln(p_ij/p_i), H(I|O) = −Σ p_ij ln(p_ij/p_j);
    the correspondence of overlay zones within input zones is 1 − H(O|I)/H(O) — 1 when every input zone holds one
    overlay zone, as documented — and that of input zones within overlay zones 1 − H(I|O)/H(I); the global measure is
    their harmonic mean. Zones that meet no zone of the other set are left out, as documented.

    Outputs, in order: output_features, output_raster, the two correspondence feature classes (all empty), the global
    measure, the correspondence of overlay within input, the correspondence of input within overlay.

    Refused: rasters as zones or as output, and the local outputs (output_features and the two correspondence
    feature classes) — how the measures are partitioned per intersection is left to the paper.
    INFERRED: a set of zones with one zone only (zero entropy) corresponds perfectly, 1, as Rosenberg and Hirschberg
    define it. INFERRED: the derived values are numbers, as the page's own sample compares result[4] with 0.9 — though
    the derived-output list says, contrary to the usage notes, that lower correspondence values are better; the usage
    notes and How it works are followed.
    """
    tool = "SpatialAssociationBetweenZones"
    for name, value in (("output_features", output_features), ("output_raster", output_raster),
                        ("correspondence_overlay_to_input", correspondence_overlay_to_input),
                        ("correspondence_input_to_overlay", correspondence_input_to_overlay)):
        if _given(value):
            _refuse(tool, name, value, "how the measures are partitioned per zone or intersection is not given on the "
                    "page, only in its reference")
    for value in (input_feature_or_raster, overlay_feature_or_raster):
        if str(value).lower().endswith((".tif", ".tiff", ".img")) or type(value).__name__ == "Raster":
            _refuse(tool, "input_feature_or_raster", value, "rasters as zones are not built in grapy")
    zones, _, kind, srid = _rows_of(tool, input_feature_or_raster, [categorical_zone_field])
    overlay, _, overlay_kind, overlay_srid = _rows_of(tool, overlay_feature_or_raster, [categorical_overlay_zone_field])
    if kind != "Polygon" or overlay_kind != "Polygon":
        raise ValueError(f"arcpy.stats.{tool}: the zones must be polygons.")
    _projected(tool, srid)
    if overlay_srid and srid and overlay_srid != srid:
        _refuse(tool, "overlay_feature_or_raster", overlay_feature_or_raster, "its reference differs from the input "
                "zones'; project one first")
    from shapely.strtree import STRtree
    key, overlay_key = categorical_zone_field.lower(), categorical_overlay_zone_field.lower()
    shapes = [o[2] for o in overlay]
    tree = STRtree(shapes)
    areas: dict = {}
    for _, values, shape in zones:
        if shape is None or values[key] is None:
            continue
        for j in tree.query(shape, predicate="intersects"):
            other = overlay[int(j)]
            if other[1][overlay_key] is None:
                continue
            area = shape.intersection(other[2]).area
            if area > 0:
                pair = (values[key], other[1][overlay_key])
                areas[pair] = areas.get(pair, 0.0) + area
    total = sum(areas.values())
    if total <= 0:
        raise ValueError(f"arcpy.stats.{tool}: the input and overlay zones do not overlap.")
    p = {pair: a / total for pair, a in areas.items()}
    p_i, p_j = {}, {}
    for (i, j), share in p.items():
        p_i[i] = p_i.get(i, 0.0) + share
        p_j[j] = p_j.get(j, 0.0) + share
    entropy = lambda shares: -sum(s * math.log(s) for s in shares if s > 0)
    h_i, h_j = entropy(p_i.values()), entropy(p_j.values())
    h_j_given_i = -sum(s * math.log(s / p_i[i]) for (i, j), s in p.items())
    h_i_given_j = -sum(s * math.log(s / p_j[j]) for (i, j), s in p.items())
    overlay_in_input = 1.0 if h_j == 0 else 1 - h_j_given_i / h_j
    input_in_overlay = 1.0 if h_i == 0 else 1 - h_i_given_j / h_i
    both = overlay_in_input + input_in_overlay
    v = 0.0 if both == 0 else 2 * overlay_in_input * input_in_overlay / both
    return Result("", "", "", "", v, overlay_in_input, input_in_overlay)


def _tukey_equivalent():
    from ._runtime import equivalent

    @equivalent("Tukey's box-plot fences on the k-th neighbour distance, quartiles by Hyndman and Fan's definition 7",
                "Tukey, Exploratory Data Analysis, 1977; Hyndman and Fan 1996")
    def GlobalSpatialOutliers(in_features, output_features, n_neighbors, output_raster, sensitivity, keep_type):
        """EQUIVALENT: each point's distance to its k-th nearest neighbour (k = n_neighbors, 1 by default, as
        documented); a point is an outlier when that distance is above Q3 + c·IQR — c is 1 for HIGH, 1.5 for MEDIUM,
        2 for LOW, as the page gives — with Q1 and Q3 by linear interpolation between order statistics (Hyndman and
        Fan 1996, definition 7), which the page does not give. Writes the input's fields, NEIGHBOR_DIST and
        IS_OUTLIER (INFERRED names)."""
        import numpy as np
        tool = "SpatialOutlierDetection"
        if _given(output_raster):
            _refuse(tool, "output_raster", output_raster, "the grid of the prediction surface is not specified")
        factor = {"HIGH": 1.0, "MEDIUM": 1.5, "LOW": 2.0}.get(_keyword(sensitivity or "MEDIUM"))
        if factor is None:
            raise ValueError(f"arcpy.stats.{tool}: sensitivity is LOW, MEDIUM or HIGH.")
        features, columns, kind, srid = _features(tool, in_features)
        n = len(features)
        k = int(n_neighbors) if _given(n_neighbors) else 1
        if not 1 <= k < n:
            raise ValueError(f"arcpy.stats.{tool}: n_neighbors is between 1 and {n - 1}.")
        d = _distances([(f[2], f[3]) for f in features])
        np.fill_diagonal(d, np.inf)
        distance = np.sort(d, axis=1)[:, k - 1]
        q1, q3 = np.quantile(distance, [0.25, 0.75], method="linear")
        outlier = distance > q3 + factor * (q3 - q1)
        keep_all = _keyword(keep_type) not in ("KEEP_OUTLIER", "FALSE")
        out = _out(output_features)
        names = [c.name for c in columns]
        _gpkg.create(out, kind, srid, [*columns, _gpkg.Column("NEIGHBOR_DIST", "Double"),
                                       _gpkg.Column("IS_OUTLIER", "Integer")])
        by_oid = {f[0]: i for i, f in enumerate(features)}
        with da.SearchCursor(in_features, ["OID@", *names, "SHAPE@"]) as rows, \
                da.InsertCursor(out, [*names, "NEIGHBOR_DIST", "IS_OUTLIER", "SHAPE@"]) as written:
            for row in rows:
                i = by_oid.get(row[0])
                if i is None or (not keep_all and not outlier[i]):
                    continue
                written.insertRow((*row[1:-1], float(distance[i]), int(outlier[i]), row[-1]))
        return Result(out)
    return GlobalSpatialOutliers


_TUKEY = None


def _tukey_global(*args):
    global _TUKEY
    if _TUKEY is None:
        _TUKEY = _tukey_equivalent()
    return _TUKEY(*args)


def SpatialOutlierDetection(in_features, output_features, n_neighbors=None, percent_outlier=None, output_raster=None,
                            outlier_type="GLOBAL", sensitivity="MEDIUM", keep_type="KEEP_ALL"):
    """Local spatial outliers by the local outlier factor, from "How Spatial Outlier Detection works" (Breunig et al.
    2000): the neighbourhood N(A) of the k nearest, ReachabilityDistance(A, B) = max(KDistance(B), d(A, B)), the local
    reachability density the inverse of the mean reachability distance over N(A), and LOF(A) the mean over N(A) of
    lrd(B)/lrd(A). The percent_outlier percent of points with the highest LOF are outliers: with m = percent × n / 100,
    every point whose LOF is at least the m-th highest, so ties at the threshold are all outliers, as the page's own
    example shows ([0, 1, 2, 3, 4, 5, 9, 9, 9, 9] at 10 percent gives the threshold 9).

    GLOBAL, the default, is an EQUIVALENT (see ``_tukey_global``): the page's box-plot rule, Q3 plus 1, 1.5 or 2
    interquartile ranges, with the quartiles of Hyndman and Fan's definition 7, since the page does not give them.
    Refused: an omitted n_neighbors or percent_outlier (they are then estimated by a
    t-statistic search over a domain the page does not give); a percent_outlier that does not make m a whole number of
    points (the page does not say how m is rounded); output_raster (the cells' grid is not specified).
    INFERRED: the output keeps the input's fields and adds LOF (Double) and IS_OUTLIER (1 or 0) — the page names the
    contents, not the fields. INFERRED: N(A) holds every point within the k-distance, ties included, as Breunig et al.
    define it; coincident points, whose reachability density is infinite, are refused.
    """
    import numpy as np
    tool = "SpatialOutlierDetection"
    if _keyword(outlier_type) == "GLOBAL":
        return _tukey_global(in_features, output_features, n_neighbors, output_raster, sensitivity, keep_type)
    if _keyword(outlier_type) != "LOCAL":
        raise ValueError(f"arcpy.stats.{tool}: outlier_type '{outlier_type}' is GLOBAL or LOCAL.")
    if _given(output_raster):
        _refuse(tool, "output_raster", output_raster, "the grid of the prediction surface is not specified")
    if not _given(n_neighbors):
        _refuse(tool, "n_neighbors", "(empty)", "it is then estimated by a t-statistic search whose domain the page "
                "does not give")
    if not _given(percent_outlier):
        _refuse(tool, "percent_outlier", "(empty)", "it is then estimated by a t-statistic search whose domain the page "
                "does not give")
    k = int(n_neighbors)
    percent = float(percent_outlier)
    if k < 2:
        raise ValueError(f"arcpy.stats.{tool}: n_neighbors must be at least 2 for local outliers.")
    if not 0 < percent <= 50:
        raise ValueError(f"arcpy.stats.{tool}: percent_outlier is above 0 and at most 50.")
    features, columns, kind, srid = _features(tool, in_features)
    if kind not in ("Point", "Multipoint"):
        raise ValueError(f"arcpy.stats.{tool} takes point features.")
    n = len(features)
    if k >= n:
        raise ValueError(f"arcpy.stats.{tool}: n_neighbors must be fewer than the {n} points.")
    m = percent * n / 100
    if abs(m - round(m)) > 1e-9:
        _refuse(tool, "percent_outlier", percent_outlier, f"{percent:g} percent of {n} points is {m:g}, not a whole "
                "number, and the page does not say how it is rounded")
    m = int(round(m))
    if len({(f[2], f[3]) for f in features}) != n:
        _refuse(tool, "in_features", in_features, "coincident points have an infinite reachability density")
    d = _distances([(f[2], f[3]) for f in features])
    np.fill_diagonal(d, np.inf)
    sorted_d = np.sort(d, axis=1)
    k_distance = sorted_d[:, k - 1]
    neighbours = [np.nonzero(d[i] <= k_distance[i])[0] for i in range(n)]
    lrd = np.array([1.0 / np.mean([max(k_distance[j], d[i, j]) for j in neighbours[i]]) for i in range(n)])
    lof = np.array([np.mean([lrd[j] / lrd[i] for j in neighbours[i]]) for i in range(n)])
    threshold = np.sort(lof)[::-1][m - 1]
    outlier = lof >= threshold - 1e-12 * abs(threshold)
    keep_all = _keyword(keep_type) not in ("KEEP_OUTLIER", "FALSE")
    out = _out(output_features)
    names = [c.name for c in columns]
    _gpkg.create(out, kind, srid, [*columns, _gpkg.Column("LOF", "Double"), _gpkg.Column("IS_OUTLIER", "Integer")])
    by_oid = {f[0]: i for i, f in enumerate(features)}
    with da.SearchCursor(in_features, ["OID@", *names, "SHAPE@"]) as rows, \
            da.InsertCursor(out, [*names, "LOF", "IS_OUTLIER", "SHAPE@"]) as written:
        for row in rows:
            i = by_oid.get(row[0])
            if i is None or (not keep_all and not outlier[i]):
                continue
            written.insertRow((*row[1:-1], float(lof[i]), int(outlier[i]), row[-1]))
    return Result(out)


# ---- Regression: equivalents (ADR-224 §5.6) -----------------------------------------------------------------------

from ._runtime import equivalent as _equivalent  # noqa: E402


def _regression_data(tool, in_features, dependent, explanatory, extra=()):
    """The features with every variable present, as (oids, y, X with a leading column of ones, xy, rows), as the tools
    document: "features that contain missing values … will be excluded"."""
    import numpy as np
    names = [dependent, *explanatory, *[e for e in extra if _given(e)]]
    features, columns, kind, srid = _features(tool, in_features, names)
    kept = [f for f in features if all(f[1][n.lower()] is not None for n in names)]
    if len(kept) <= len(explanatory) + 2:
        raise ValueError(f"arcpy.stats.{tool}: too few features with every variable present.")
    y = np.array([float(f[1][dependent.lower()]) for f in kept])
    x = np.column_stack([np.ones(len(kept)), *[[float(f[1][e.lower()]) for f in kept] for e in explanatory]])
    xy = np.array([(f[2], f[3]) for f in kept])
    return kept, y, x, xy, columns, kind, srid


def _ols_fit(x, y):
    """β, fitted values, residuals and (X'X)⁻¹ of least squares."""
    import numpy as np
    inverse = np.linalg.inv(x.T @ x)
    beta = inverse @ x.T @ y
    fitted = x @ beta
    return beta, fitted, y - fitted, inverse


def _write_table(out_table, columns, rows):
    out = _out(out_table)
    _gpkg.create(out, "Geometry", 0, columns)
    with da.InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
        for row in rows:
            written.insertRow((*row, None))
    return out


@_equivalent("ordinary least squares with classical, White (HC0), Koenker–Bassett and Jarque–Bera diagnostics",
             "Greene, Econometric Analysis, 7th ed., ch. 4–5 and 9; White 1980; Koenker 1981; Jarque and Bera 1980")
def OrdinaryLeastSquares(Input_Feature_Class, Unique_ID_Field, Output_Feature_Class, Dependent_Variable,
                         Explanatory_Variables, Coefficient_Output_Table=None, Diagnostic_Output_Table=None,
                         Output_Report_File=None):
    """EQUIVALENT: ordinary least squares (Greene, Econometric Analysis, ch. 4–5) — Esri's "How OLS regression works"
    page could not be read, so its exact diagnostics are not reproduced. β = (X'X)⁻¹X'y with an intercept; σ² = RSS/(n−k);
    standard errors √(σ² diag (X'X)⁻¹) and two-sided t probabilities with n−k degrees of freedom; White's
    heteroskedasticity-consistent (HC0) standard errors (White 1980); R², adjusted R²; the joint F statistic and the
    robust joint Wald statistic of the slopes; Koenker's studentized Breusch–Pagan statistic n·R² of e² on X (Koenker
    1981); Jarque–Bera n/6·(S² + (K−3)²/4) of the residuals (1980); AIC = n ln(2π RSS/n) + n + 2(k+1) and
    AICc = n ln(2π RSS/n) + n(n+k)/(n−2−k), the Gaussian likelihood's (Hurvich and Tsai 1989).

    The output feature class carries the unique ID, the dependent and explanatory variables, Estimated, Residual and
    StdResid (the residual over σ). The coefficient table has Variable, Coef, StdError, t_Stat, Prob, Robust_SE,
    Robust_t and Robust_Pr; the diagnostic table Diag_Name, Diag_Value. Output_Report_File (a PDF) is refused.
    """
    import numpy as np
    from scipy import stats as distributions
    tool = "OrdinaryLeastSquares"
    if _given(Output_Report_File):
        _refuse(tool, "Output_Report_File", Output_Report_File, "the PDF report's content is not documented")
    explanatory = [e for e in _list(Explanatory_Variables) if _given(e)]
    kept, y, x, _, columns, kind, srid = _regression_data(tool, Input_Feature_Class, Dependent_Variable, explanatory,
                                                          [Unique_ID_Field])
    n, k = x.shape
    beta, fitted, residual, inverse = _ols_fit(x, y)
    rss = float(residual @ residual)
    tss = float(((y - y.mean()) ** 2).sum())
    sigma2 = rss / (n - k)
    se = np.sqrt(sigma2 * np.diag(inverse))
    t = beta / se
    p = 2 * distributions.t.sf(np.abs(t), n - k)
    robust_cov = inverse @ (x.T * residual ** 2) @ x @ inverse
    robust_se = np.sqrt(np.diag(robust_cov))
    robust_t = beta / robust_se
    robust_p = 2 * distributions.t.sf(np.abs(robust_t), n - k)
    r2 = 1 - rss / tss
    adjusted = 1 - (1 - r2) * (n - 1) / (n - k)
    f_stat = ((tss - rss) / (k - 1)) / (rss / (n - k))
    slopes = beta[1:]
    wald = float(slopes @ np.linalg.inv(robust_cov[1:, 1:]) @ slopes)
    squared = residual ** 2
    _, aux_fit, aux_residual, _ = _ols_fit(x, squared)
    aux_r2 = 1 - float(aux_residual @ aux_residual) / float(((squared - squared.mean()) ** 2).sum())
    koenker = n * aux_r2
    m2, m3, m4 = (float((residual ** power).mean()) for power in (2, 3, 4))
    skew, kurtosis = m3 / m2 ** 1.5, m4 / m2 ** 2
    jarque_bera = n / 6 * (skew ** 2 + (kurtosis - 3) ** 2 / 4)
    log_term = n * math.log(2 * math.pi * rss / n)
    aic = log_term + n + 2 * (k + 1)
    aicc = log_term + n * (n + k) / (n - 2 - k)
    from ._runtime import AddMessage
    by_name = {c.name.lower(): c for c in columns}
    keep = [by_name[Unique_ID_Field.lower()], by_name[Dependent_Variable.lower()], *[by_name[e.lower()] for e in explanatory]]
    out = _out(Output_Feature_Class)
    added = [_gpkg.Column("Estimated", "Double"), _gpkg.Column("Residual", "Double"), _gpkg.Column("StdResid", "Double")]
    _gpkg.create(out, kind, srid, [*keep, *added])
    with da.InsertCursor(out, [*[c.name for c in keep], *[c.name for c in added], "SHAPE@"]) as written:
        for i, f in enumerate(kept):
            written.insertRow((*[f[1][c.name.lower()] for c in keep], float(fitted[i]), float(residual[i]),
                               float(residual[i] / math.sqrt(sigma2)), f[4]))
    names = ["Intercept", *explanatory]
    coefficient = ""
    if _given(Coefficient_Output_Table):
        coefficient = _write_table(Coefficient_Output_Table, [
            _gpkg.Column("Variable", "String"), *[_gpkg.Column(c, "Double") for c in
                                                  ("Coef", "StdError", "t_Stat", "Prob", "Robust_SE", "Robust_t", "Robust_Pr")]],
            [(names[j], *[float(v[j]) for v in (beta, se, t, p, robust_se, robust_t, robust_p)]) for j in range(k)])
    diagnostics = [("AIC", aic), ("AICc", aicc), ("R2", r2), ("AdjR2", adjusted), ("F-Stat", f_stat),
                   ("F-Prob", float(distributions.f.sf(f_stat, k - 1, n - k))), ("Wald", wald),
                   ("Wald-Prob", float(distributions.chi2.sf(wald, k - 1))), ("K(BP)", koenker),
                   ("K(BP)-Prob", float(distributions.chi2.sf(koenker, k - 1))), ("JB", jarque_bera),
                   ("JB-Prob", float(distributions.chi2.sf(jarque_bera, 2))), ("Sigma2", sigma2)]
    for name, value in diagnostics:
        AddMessage(f"{name}: {value}")
    diagnostic = ""
    if _given(Diagnostic_Output_Table):
        diagnostic = _write_table(Diagnostic_Output_Table, [_gpkg.Column("Diag_Name", "String"),
                                                            _gpkg.Column("Diag_Value", "Double")], diagnostics)
    return Result(out, coefficient, diagnostic, "")


def _glm(x, y, family, weights=None, iterations=100):
    """A generalized linear model by iteratively reweighted least squares (McCullagh and Nelder 1989, §2.5):
    Gaussian identity, binomial logit, Poisson log. Answers β, the fitted means and (X'WX)⁻¹ at convergence."""
    import numpy as np
    prior = np.ones(len(y)) if weights is None else weights
    if family == "gaussian":
        w = prior
        inverse = np.linalg.inv((x.T * w) @ x)
        beta = inverse @ (x.T * w) @ y
        return beta, x @ beta, inverse
    mu = (y + 0.5) / 2 if family == "binomial" else y + 0.5
    eta = np.log(mu / (1 - mu)) if family == "binomial" else np.log(mu)
    beta = np.zeros(x.shape[1])
    for _ in range(iterations):
        if family == "binomial":
            mu = 1 / (1 + np.exp(-eta))
            variance = mu * (1 - mu)
            z = eta + (y - mu) / variance
        else:
            mu = np.exp(eta)
            variance = mu
            z = eta + (y - mu) / mu
        w = prior * variance
        inverse = np.linalg.inv((x.T * w) @ x)
        new = inverse @ (x.T * w) @ z
        eta = x @ new
        if np.max(np.abs(new - beta)) < 1e-12 * (1 + np.max(np.abs(new))):
            beta = new
            break
        beta = new
    mu = 1 / (1 + np.exp(-eta)) if family == "binomial" else np.exp(eta)
    return beta, mu, inverse


def _deviance(y, mu, family):
    import numpy as np
    if family == "gaussian":
        return float(((y - mu) ** 2).sum())
    if family == "binomial":
        with np.errstate(divide="ignore", invalid="ignore"):
            terms = np.where(y > 0, y * np.log(y / mu), 0) + np.where(y < 1, (1 - y) * np.log((1 - y) / (1 - mu)), 0)
        return float(2 * terms.sum())
    with np.errstate(divide="ignore", invalid="ignore"):
        terms = np.where(y > 0, y * np.log(y / mu), 0) - (y - mu)
    return float(2 * terms.sum())


_FAMILIES = {"CONTINUOUS": "gaussian", "BINARY": "binomial", "COUNT": "poisson"}


@_equivalent("a generalized linear model fitted by iteratively reweighted least squares — Gaussian identity, binomial "
             "logit, Poisson log", "McCullagh and Nelder, Generalized Linear Models, 2nd ed., 1989")
def GeneralizedLinearRegression(in_features, dependent_variable, model_type, output_features, explanatory_variables,
                                distance_features=None, prediction_locations=None, explanatory_variables_to_match=None,
                                explanatory_distance_matching=None, output_predicted_features=None,
                                output_trained_model=None):
    """EQUIVALENT: a generalized linear model by iteratively reweighted least squares (McCullagh and Nelder 1989) —
    CONTINUOUS Gaussian with the identity link (ordinary least squares), BINARY binomial with the logit link, COUNT
    Poisson with the log link. Fit to convergence of β (relative change below 10⁻¹²).

    The output carries the dependent and explanatory variables, PREDICTED (the fitted mean) and RESIDUAL (observed
    minus fitted); the coefficients, their standard errors √diag((X'WX)⁻¹ φ) — φ = RSS/(n−k) for Gaussian, 1 otherwise —
    and the deviance are written as messages. INFERRED: those field names, which the page does not give.

    Refused: distance_features, prediction locations and their matching, and output_trained_model (an .ssm file).
    """
    import numpy as np
    tool = "GeneralizedLinearRegression"
    for name, value in (("distance_features", distance_features), ("prediction_locations", prediction_locations),
                        ("output_predicted_features", output_predicted_features),
                        ("output_trained_model", output_trained_model)):
        if _given(value):
            _refuse(tool, name, value, "it is not part of this equivalent" if name != "output_trained_model" else
                    "a spatial statistics model (.ssm) file's layout is not published")
    family = _FAMILIES.get(_keyword(model_type))
    if family is None:
        raise ValueError(f"arcpy.stats.{tool}: model_type is CONTINUOUS, BINARY or COUNT.")
    explanatory = [e for e in _list(explanatory_variables) if _given(e)]
    kept, y, x, _, columns, kind, srid = _regression_data(tool, in_features, dependent_variable, explanatory)
    if family == "binomial" and not set(np.unique(y)) <= {0.0, 1.0}:
        raise ValueError(f"arcpy.stats.{tool}: a BINARY dependent variable holds only 0 and 1.")
    if family == "poisson" and ((y < 0).any() or (y != np.round(y)).any()):
        raise ValueError(f"arcpy.stats.{tool}: a COUNT dependent variable holds whole numbers of 0 or more.")
    beta, mu, inverse = _glm(x, y, family)
    n, k = x.shape
    dispersion = float(((y - mu) ** 2).sum()) / (n - k) if family == "gaussian" else 1.0
    se = np.sqrt(np.diag(inverse) * dispersion)
    from ._runtime import AddMessage
    for name, b, s in zip(["Intercept", *explanatory], beta, se):
        AddMessage(f"{name}: coefficient {b}, standard error {s}")
    AddMessage(f"Deviance: {_deviance(y, mu, family)}")
    by_name = {c.name.lower(): c for c in columns}
    keep = [by_name[dependent_variable.lower()], *[by_name[e.lower()] for e in explanatory]]
    out = _out(output_features)
    added = [_gpkg.Column("PREDICTED", "Double"), _gpkg.Column("RESIDUAL", "Double")]
    _gpkg.create(out, kind, srid, [*keep, *added])
    with da.InsertCursor(out, [*[c.name for c in keep], *[c.name for c in added], "SHAPE@"]) as written:
        for i, f in enumerate(kept):
            written.insertRow((*[f[1][c.name.lower()] for c in keep], float(mu[i]), float(y[i] - mu[i]), f[4]))
    return Result(out, *[float(b) for b in beta])


def _gwr_weights(d, bandwidth, kernel, adaptive):
    """Kernel weights of each row's distances: Gaussian exp(−½(d/b)²), bisquare (1−(d/b)²)² inside b and 0 outside
    (Fotheringham, Brunsdon and Charlton 2002, eq. 2.8–2.9); adaptive, b is each row's distance to its k-th nearest
    feature, the focal feature counted."""
    import numpy as np
    if adaptive:
        b = np.sort(d, axis=1)[:, int(bandwidth) - 1][:, None]
    else:
        b = float(bandwidth)
    ratio = d / b
    if kernel == "GAUSSIAN":
        return np.exp(-0.5 * ratio ** 2)
    return np.where(ratio < 1, (1 - ratio ** 2) ** 2, 0.0)


def _gwr_fit(x, y, d, bandwidth, kernel, adaptive):
    """Every local fit: β_i = (X'W_iX)⁻¹X'W_iy, the hat matrix's row i, and C_i = (X'W_iX)⁻¹X'W_i."""
    import numpy as np
    n, k = x.shape
    w = _gwr_weights(d, bandwidth, kernel, adaptive)
    betas, hat, spread, conditions, local_r2 = np.zeros((n, k)), np.zeros(n), np.zeros((n, k)), np.zeros(n), np.zeros(n)
    rows = []
    for i in range(n):
        xw = x.T * w[i]
        matrix = xw @ x
        try:
            inverse = np.linalg.inv(matrix)
        except np.linalg.LinAlgError:
            raise ValueError("arcpy.stats.GWR: a local regression is singular at this bandwidth — too few neighbours.")
        c = inverse @ xw
        betas[i] = c @ y
        rows.append(x[i] @ c)
        hat[i] = rows[-1][i]
        spread[i] = np.sqrt(np.diag(c @ c.T))
        scaled = x * np.sqrt(w[i])[:, None]
        conditions[i] = np.linalg.cond(scaled[:, 1:] if k > 1 else scaled)
        fitted_local = x @ betas[i]
        mean = float((w[i] * y).sum() / w[i].sum())
        total = float((w[i] * (y - mean) ** 2).sum())
        local_r2[i] = 1 - float((w[i] * (y - fitted_local) ** 2).sum()) / total if total > 0 else 0.0
    s = np.array(rows)
    return betas, s, spread, conditions, local_r2


def _gwr_aicc(x, y, d, bandwidth, kernel, adaptive):
    """AICc = n ln(σ̂²) + n ln 2π + n(n + tr S)/(n − 2 − tr S), σ̂² = RSS/n (Fotheringham et al. 2002, eq. 2.33)."""
    import numpy as np
    betas, s, *_ = _gwr_fit(x, y, d, bandwidth, kernel, adaptive)
    n = len(y)
    residual = y - (x * betas).sum(axis=1)
    trace = float(np.trace(s))
    if n - 2 - trace <= 0:
        return math.inf
    rss = float(residual @ residual)
    return n * math.log(rss / n) + n * math.log(2 * math.pi) + n * (n + trace) / (n - 2 - trace)


def _golden(f, low, high, integer):
    """The minimum of f on [low, high] by golden-section search (Kiefer 1953), to a whole number of neighbours or to
    10⁻⁵ of the interval."""
    ratio = (math.sqrt(5) - 1) / 2
    a, b = float(low), float(high)
    cache = {}

    def value(t):
        t = round(t) if integer else t
        if t not in cache:
            cache[t] = f(t)
        return cache[t]

    c, e = b - ratio * (b - a), a + ratio * (b - a)
    while (b - a) > (1 if integer else 1e-5 * (high - low)):
        if value(c) <= value(e):
            b, e = e, c
            c = b - ratio * (b - a)
        else:
            a, c = c, e
            e = a + ratio * (b - a)
    return min(cache, key=lambda t: (cache[t], t))


@_equivalent("geographically weighted regression with a Gaussian or bisquare kernel and an AICc bandwidth chosen by "
             "golden-section search", "Fotheringham, Brunsdon and Charlton, Geographically Weighted Regression, 2002")
def GWR(in_features, dependent_variable, model_type, explanatory_variables, output_features, neighborhood_type,
        neighborhood_selection_method, minimum_number_of_neighbors=None, maximum_number_of_neighbors=None,
        minimum_search_distance=None, maximum_search_distance=None, number_of_neighbors_increment=None,
        search_distance_increment=None, number_of_increments=None, number_of_neighbors=None, distance_band=None,
        prediction_locations=None, explanatory_variables_to_match=None, output_predicted_features=None,
        robust_prediction="ROBUST", local_weighting_scheme="BISQUARE", coefficient_raster_workspace=None,
        scale="SCALE_DATA"):
    """EQUIVALENT: geographically weighted regression (Fotheringham, Brunsdon and Charlton 2002): at each feature,
    β_i = (X'W_iX)⁻¹X'W_iy with Gaussian or bisquare kernel weights; the bandwidth a distance (DISTANCE_BAND) or a
    number of neighbours (NUMBER_OF_NEIGHBORS, the focal feature counted); chosen by GOLDEN_SEARCH or MANUAL_INTERVALS
    as the one of least AICc (eq. 2.33), or given (USER_DEFINED). The golden search's range is the page's: from the
    distance (or count) at which every feature has 5 percent of the features as neighbours to that at which every
    feature has half, narrowed by the minimum and maximum given.

    The output carries the dependent and explanatory variables, INTERCEPT, SE_INTERCEPT, C_<variable> and SE_<variable>
    (√diag(C_iC_i')·σ, C_i = (X'W_iX)⁻¹X'W_i), PREDICTED, RESIDUAL, STDRESID (e_i/(σ√(1−S_ii))), INFLUENCE (S_ii),
    COOKSD (STDRESID²·S_ii/(tr S·(1−S_ii))) — null where S_ii is 1 — LOCALR2 (weighted) and COND (the condition number of the weighted
    explanatory columns). σ² = RSS/(n − tr S). The bandwidth, tr S, AICc, R² and σ² are written as messages.
    INFERRED: those field names and these formulas for the ones the page names without formulas.

    Refused: BINARY and COUNT (local GLMs are not built in this equivalent), prediction locations, coefficient rasters.
    INFERRED: SCALE_DATA does not change the fit, the predictions or the bandwidth chosen; its scaled coefficients are
    not written.
    """
    import numpy as np
    tool = "GWR"
    if _keyword(model_type) != "CONTINUOUS":
        _refuse(tool, "model_type", model_type, "local generalized linear models are not built in this equivalent")
    for name, value in (("prediction_locations", prediction_locations), ("output_predicted_features",
                                                                         output_predicted_features),
                        ("coefficient_raster_workspace", coefficient_raster_workspace)):
        if _given(value):
            _refuse(tool, name, value, "it is not part of this equivalent")
    kernel = _keyword(local_weighting_scheme or "BISQUARE")
    if kernel not in ("BISQUARE", "GAUSSIAN"):
        raise ValueError(f"arcpy.stats.{tool}: local_weighting_scheme is BISQUARE or GAUSSIAN.")
    adaptive = _keyword(neighborhood_type) == "NUMBER_OF_NEIGHBORS"
    if not adaptive and _keyword(neighborhood_type) != "DISTANCE_BAND":
        raise ValueError(f"arcpy.stats.{tool}: neighborhood_type is NUMBER_OF_NEIGHBORS or DISTANCE_BAND.")
    explanatory = [e for e in _list(explanatory_variables) if _given(e)]
    kept, y, x, xy, columns, kind, srid = _regression_data(tool, in_features, dependent_variable, explanatory)
    n = len(y)
    d = _distances(xy)
    method = _keyword(neighborhood_selection_method)
    aicc = (lambda b: _gwr_aicc(x, y, d, b, kernel, adaptive))
    if method == "USER_DEFINED":
        given = number_of_neighbors if adaptive else distance_band
        if not _given(given):
            raise ValueError(f"arcpy.stats.{tool}: USER_DEFINED needs "
                             f"{'number_of_neighbors' if adaptive else 'distance_band'}.")
        bandwidth = int(given) if adaptive else _threshold(given, srid)
    elif method == "MANUAL_INTERVALS":
        start = minimum_number_of_neighbors if adaptive else minimum_search_distance
        step = number_of_neighbors_increment if adaptive else search_distance_increment
        if not (_given(start) and _given(step) and _given(number_of_increments)):
            raise ValueError(f"arcpy.stats.{tool}: MANUAL_INTERVALS needs a minimum, an increment and a number of "
                             "increments.")
        first = int(start) if adaptive else _threshold(start, srid)
        delta = int(step) if adaptive else _threshold(step, srid)
        candidates = [first + i * delta for i in range(int(number_of_increments))]
        bandwidth = min(candidates, key=lambda b: (aicc(b), b))
    elif method == "GOLDEN_SEARCH":
        sorted_d = np.sort(d, axis=1)
        if adaptive:
            low = max(int(math.ceil(0.05 * n)), x.shape[1] + 1)
            high = max(low, n // 2)
            if _given(minimum_number_of_neighbors):
                low = int(minimum_number_of_neighbors)
            if _given(maximum_number_of_neighbors):
                high = int(maximum_number_of_neighbors)
        else:
            low = float(sorted_d[:, max(int(math.ceil(0.05 * n)) - 1, 1)].max())
            high = float(sorted_d[:, max(n // 2 - 1, 1)].max())
            if _given(minimum_search_distance):
                low = _threshold(minimum_search_distance, srid)
            if _given(maximum_search_distance):
                high = _threshold(maximum_search_distance, srid)
        bandwidth = _golden(aicc, low, high, adaptive)
    else:
        raise ValueError(f"arcpy.stats.{tool}: neighborhood_selection_method is GOLDEN_SEARCH, MANUAL_INTERVALS or "
                         "USER_DEFINED.")
    betas, s, spread, conditions, local_r2 = _gwr_fit(x, y, d, bandwidth, kernel, adaptive)
    fitted = (x * betas).sum(axis=1)
    residual = y - fitted
    trace = float(np.trace(s))
    rss = float(residual @ residual)
    sigma2 = rss / (n - trace)
    influence = np.diag(s)
    with np.errstate(divide="ignore", invalid="ignore"):
        std_residual = residual / np.sqrt(sigma2 * (1 - influence))
        cooks = std_residual ** 2 * influence / (trace * (1 - influence))
    finite = (lambda v: float(v) if np.isfinite(v) else None)
    se = spread * math.sqrt(sigma2)
    from ._runtime import AddMessage
    AddMessage(f"Bandwidth: {bandwidth}")
    AddMessage(f"Effective number of parameters (tr S): {trace}")
    AddMessage(f"AICc: {aicc(bandwidth)}")
    AddMessage(f"R2: {1 - rss / float(((y - y.mean()) ** 2).sum())}")
    AddMessage(f"Sigma-squared: {sigma2}")
    by_name = {c.name.lower(): c for c in columns}
    keep = [by_name[dependent_variable.lower()], *[by_name[e.lower()] for e in explanatory]]
    labels = ["INTERCEPT", "SE_INTERCEPT"] + [name for e in explanatory for name in (f"C_{e}", f"SE_{e}")]
    labels += ["PREDICTED", "RESIDUAL", "STDRESID", "INFLUENCE", "COOKSD", "LOCALR2", "COND"]
    out = _out(output_features)
    _gpkg.create(out, kind, srid, [*keep, *[_gpkg.Column(name, "Double") for name in labels]])
    with da.InsertCursor(out, [*[c.name for c in keep], *labels, "SHAPE@"]) as written:
        for i, f in enumerate(kept):
            pairs = [float(v) for j in range(x.shape[1]) for v in (betas[i, j], se[i, j])]
            written.insertRow((*[f[1][c.name.lower()] for c in keep], *pairs, float(fitted[i]), float(residual[i]),
                               finite(std_residual[i]), float(influence[i]), finite(cooks[i]), float(local_r2[i]),
                               finite(conditions[i]), f[4]))
    return Result(out)


@_equivalent("Global Moran's I over a series of fixed distance bands, its peaks read as distances",
             "the Spatial Autocorrelation page's formulas; the outputs as the tool page's own sample reads them")
def IncrementalSpatialAutocorrelation(Input_Features, Input_Field, Number_of_Distance_Bands, Beginning_Distance=None,
                                      Distance_Increment=None, Distance_Method="EUCLIDEAN",
                                      Row_Standardization="ROW_STANDARDIZATION", Output_Table=None,
                                      Output_Report_File=None):
    """EQUIVALENT: Global Moran's I (this module's SpatialAutocorrelation, fixed distance band) at Beginning_Distance and
    each increment after it. The page lists the derived outputs First_Peak and Max_Peak as z-scores, but its own
    sample reads ``getOutput(2)`` as the distance to use next: grapy answers as the sample reads — the outputs, in
    order, are the output table, the report (empty), the first peak's distance and the highest peak's distance (empty
    when there is no peak). A peak is a band whose z-score is higher than the bands either side of it. The table has
    Distance, MoransI, ExpectedI, Variance, ZScore and PValue (INFERRED names).

    The beginning distance defaults, as documented, to the largest nearest-neighbour distance. Refused: an omitted
    Distance_Increment (its default rests on a "maximum threshold distance" the page does not define) and the report.
    """
    import numpy as np
    tool = "IncrementalSpatialAutocorrelation"
    if _given(Output_Report_File):
        _refuse(tool, "Output_Report_File", Output_Report_File, "the report's content is not documented")
    if not _given(Distance_Increment):
        _refuse(tool, "Distance_Increment", "(empty)", "its default rests on a maximum threshold distance the page "
                "does not define")
    features, _, _, srid = _features(tool, Input_Features, [Input_Field])
    if len(features) < 4:
        raise ValueError(f"arcpy.stats.{tool} needs four features or more.")
    x = _values(tool, features, Input_Field)
    method = "MANHATTAN_DISTANCE" if _keyword(Distance_Method).startswith("MANHATTAN") else "EUCLIDEAN_DISTANCE"
    d = _distances([(f[2], f[3]) for f in features], method)
    begin = _threshold(Beginning_Distance, srid) if _given(Beginning_Distance) else float(_nearest(d).max())
    step = _threshold(Distance_Increment, srid)
    standardize = "ROW" if _keyword(Row_Standardization) in ("ROW_STANDARDIZATION", "ROW", "TRUE") else "NONE"
    rows = []
    for k in range(int(Number_of_Distance_Bands)):
        distance = begin + k * step
        w = _standardized(_weights(tool, features, srid, "FIXED_DISTANCE_BAND", method, distance, None), standardize)
        try:
            i, expected, variance, z = _moran(x, w)
            rows.append((distance, i, expected, variance, z, _p(z)))
        except ValueError:  # Every feature the neighbour of every other: the variance is zero and z undefined.
            rows.append((distance, None, None, None, None, None))
    zs = [r[4] for r in rows]
    peaks = [k for k in range(1, len(rows) - 1) if None not in zs[k - 1:k + 2] and zs[k] > zs[k - 1] and zs[k] > zs[k + 1]]
    first = rows[peaks[0]][0] if peaks else ""
    highest = rows[max(peaks, key=lambda k: rows[k][4])][0] if peaks else ""
    table = ""
    if _given(Output_Table):
        table = _write_table(Output_Table, [_gpkg.Column(name, "Double") for name in
                                            ("Distance", "MoransI", "ExpectedI", "Variance", "ZScore", "PValue")], rows)
    return Result(table, "", first, highest)


@_equivalent("k-means by Lloyd's algorithm with k-means++ seeding, the number of clusters by the Calinski–Harabasz "
             "pseudo F", "Lloyd 1982; Arthur and Vassilvitskii 2007; Calinski and Harabasz 1974")
def MultivariateClustering(in_features, output_features, analysis_fields, clustering_method="K_MEANS",
                           initialization_method="OPTIMIZED_SEED_LOCATIONS", initialization_field=None,
                           number_of_clusters=None, output_table=None, scale="SCALE_DATA"):
    """EQUIVALENT: k-means (Lloyd 1982): each feature joins its nearest centre in the analysis fields' space (the
    lower-numbered centre on a tie), each centre moves to its members' mean, until no feature changes cluster.
    Seeds: OPTIMIZED_SEED_LOCATIONS is k-means++ (Arthur and Vassilvitskii 2007); RANDOM_SEED_LOCATIONS draws k
    distinct features; USER_DEFINED_SEED_LOCATIONS takes the features whose initialization_field is not zero, as many
    clusters as seeds. Draws are NumPy's PCG64 from env.randomGenerator's seed, 0 when unset: a run repeats itself,
    and Pro's draws differ. With SCALE_DATA each field is divided by its (population) standard deviation.
    With no number_of_clusters, 2 to 30 (at most n − 1) are tried and the largest pseudo F, (SSB/(k−1))/(SSW/(n−k))
    (Calinski and Harabasz 1974), wins; output_table then lists NUM_CLUSTERS and PSEUDO_F (INFERRED names).

    Writes the analysis fields and CLUSTER_ID, the clusters numbered in the order of their lowest object id
    (INFERRED). Refused: K_MEDOIDS (not in this equivalent).
    """
    import numpy as np
    tool = "MultivariateClustering"
    if _keyword(clustering_method or "K_MEANS") != "K_MEANS":
        _refuse(tool, "clustering_method", clustering_method, "k-medoids is not in this equivalent")
    fields = [f for f in _list(analysis_fields) if _given(f)]
    extra = [initialization_field] if _given(initialization_field) else []
    features, columns, kind, srid = _features(tool, in_features, fields + extra)
    features = [f for f in features if all(f[1][k.lower()] is not None for k in fields)]
    data = np.array([[float(f[1][k.lower()]) for k in fields] for f in features])
    if _keyword(scale or "SCALE_DATA") not in ("NO_SCALE_DATA", "FALSE"):
        sd = data.std(axis=0)
        sd[sd == 0] = 1.0
        data = (data - data.mean(axis=0)) / sd
    n = len(data)
    random = np.random.default_rng(_seed())
    start = _keyword(initialization_method or "OPTIMIZED_SEED_LOCATIONS")

    def seeds(k):
        if start == "USER_DEFINED_SEED_LOCATIONS":
            chosen = [i for i, f in enumerate(features) if f[1][initialization_field.lower()] not in (None, 0)]
            return data[chosen]
        if start == "RANDOM_SEED_LOCATIONS":
            return data[random.choice(n, k, replace=False)]
        centres = [data[random.integers(n)]]
        while len(centres) < k:
            gap = np.min([((data - c) ** 2).sum(axis=1) for c in centres], axis=0)
            centres.append(data[random.choice(n, p=gap / gap.sum())] if gap.sum() > 0 else data[random.integers(n)])
        return np.array(centres)

    def lloyd(centres):
        labels = None
        for _ in range(1000):
            new = np.argmin(((data[:, None, :] - centres[None, :, :]) ** 2).sum(axis=2), axis=1)
            if labels is not None and (new == labels).all():
                break
            labels = new
            centres = np.array([data[labels == c].mean(axis=0) if (labels == c).any() else centres[c]
                                for c in range(len(centres))])
        return labels

    def pseudo_f(labels, k):
        mean = data.mean(axis=0)
        within = sum(((data[labels == c] - data[labels == c].mean(axis=0)) ** 2).sum() for c in set(labels.tolist()))
        between = ((data - mean) ** 2).sum() - within
        return (between / (k - 1)) / (within / (n - k)) if within > 0 else math.inf

    if start == "USER_DEFINED_SEED_LOCATIONS":
        if not extra:
            raise ValueError(f"arcpy.stats.{tool}: USER_DEFINED_SEED_LOCATIONS needs initialization_field.")
        labels = lloyd(seeds(None))
        table_rows = []
    elif _given(number_of_clusters):
        labels = lloyd(seeds(int(number_of_clusters)))
        table_rows = []
    else:
        table_rows, best = [], None
        for k in range(2, min(30, n - 1) + 1):
            candidate = lloyd(seeds(k))
            value = pseudo_f(candidate, k)
            table_rows.append((k, value))
            if best is None or value > best[0]:
                best = (value, candidate)
        labels = best[1]
    order = {}
    for i in sorted(range(n), key=lambda i: features[i][0]):
        order.setdefault(int(labels[i]), len(order) + 1)
    by_name = {c.name.lower(): c for c in columns}
    keep = [by_name[f.lower()] for f in fields]
    out = _out(output_features)
    _gpkg.create(out, kind, srid, [*keep, _gpkg.Column("CLUSTER_ID", "Integer")])
    with da.InsertCursor(out, [*[c.name for c in keep], "CLUSTER_ID", "SHAPE@"]) as written:
        for i, f in enumerate(features):
            written.insertRow((*[f[1][c.name.lower()] for c in keep], order[int(labels[i])], f[4]))
    table = ""
    if _given(output_table) and table_rows:
        table = _write_table(output_table, [_gpkg.Column("NUM_CLUSTERS", "Integer"), _gpkg.Column("PSEUDO_F", "Double")],
                             table_rows)
    return Result(out, table)


@_equivalent("DBSCAN", "Ester, Kriegel, Sander and Xu 1996")
def DensityBasedClustering(in_features, output_features, cluster_method, min_features_cluster, search_distance=None,
                           cluster_sensitivity=None, time_field=None, search_time_interval=None):
    """EQUIVALENT: DBSCAN (Ester et al. 1996) as the page describes it: a point with min_features_cluster points
    within search_distance, itself included, is a core point; a cluster is the core points reachable from one another
    and the points within reach of them; the rest are noise. Points are visited in object id order, and a border point
    joins the first cluster to reach it, as in the paper. With no search_distance, the page's default: the highest core
    distance after the top 1 percent are set aside — INFERRED: the floor(n/100) largest. Writes the input's fields and
    CLUSTER_ID, −1 for noise (INFERRED), clusters numbered in the order they are found.

    Refused: HDBSCAN and OPTICS (not in this equivalent) and a time field (space-time clusters are not built).
    """
    import numpy as np
    tool = "DensityBasedClustering"
    method = _keyword(cluster_method)
    if method != "DBSCAN":
        _refuse(tool, "cluster_method", cluster_method, "only DBSCAN is in this equivalent")
    if _given(time_field):
        _refuse(tool, "time_field", time_field, "space-time clustering is not built")
    features, columns, kind, srid = _features(tool, in_features)
    n = len(features)
    k = int(min_features_cluster)
    if not 2 <= k <= n:
        raise ValueError(f"arcpy.stats.{tool}: min_features_cluster is between 2 and {n}.")
    d = _distances([(f[2], f[3]) for f in features])
    if _given(search_distance):
        eps = _threshold(search_distance, srid)
    else:
        core = np.sort(d, axis=1)[:, k - 1]
        drop = n // 100
        eps = float(np.sort(core)[: n - drop].max())
    order = sorted(range(n), key=lambda i: features[i][0])
    neighbours = [np.nonzero(d[i] <= eps)[0] for i in range(n)]
    labels = [None] * n
    cluster = 0
    for i in order:
        if labels[i] is not None:
            continue
        if len(neighbours[i]) < k:
            labels[i] = -1
            continue
        cluster += 1
        labels[i] = cluster
        queue = [int(j) for j in sorted(neighbours[i], key=lambda j: features[j][0])]
        while queue:
            j = queue.pop(0)
            if labels[j] == -1:
                labels[j] = cluster
            if labels[j] is not None:
                continue
            labels[j] = cluster
            if len(neighbours[j]) >= k:
                queue.extend(int(m) for m in sorted(neighbours[j], key=lambda m: features[m][0]))
    names = [c.name for c in columns]
    out = _out(output_features)
    _gpkg.create(out, kind, srid, [*columns, _gpkg.Column("CLUSTER_ID", "Integer")])
    by_oid = {f[0]: i for i, f in enumerate(features)}
    with da.SearchCursor(in_features, ["OID@", *names, "SHAPE@"]) as rows, \
            da.InsertCursor(out, [*names, "CLUSTER_ID", "SHAPE@"]) as written:
        for row in rows:
            i = by_oid.get(row[0])
            if i is not None:
                written.insertRow((*row[1:-1], labels[i], row[-1]))
    return Result(out)


def _locational_outliers(d):
    """Which features are locational outliers. EQUIVALENT: the page names them without a rule; grapy takes Tukey's fence
    on the nearest-neighbour distance, Q3 + 1.5·IQR, quartiles by Hyndman and Fan's definition 7."""
    import numpy as np
    nearest = _nearest(d)
    q1, q3 = np.quantile(nearest, [0.25, 0.75], method="linear")
    return nearest > q3 + 1.5 * (q3 - q1)


def _optimized_prepare(tool, Input_Features, Analysis_Field, method, bounding, aggregation_polygons, Cell_Size,
                       Distance_Band, said):
    """The weighted features (the input with its field, or incidents counted into cells or polygons) and the scale of
    analysis, by the steps "How Optimized Hot Spot Analysis works" documents."""
    import os
    import numpy as np
    scratch = os.path.join(env_scratch(), f"{tool.lower()}_weighted.gpkg")
    if _given(Analysis_Field):
        path, field = Input_Features, Analysis_Field
    else:
        method = _keyword(method or "COUNT_INCIDENTS_WITHIN_HEXAGON_POLYGONS")
        features, _, kind, srid = _features(tool, Input_Features)
        if kind not in ("Point", "Multipoint"):
            raise ValueError(f"arcpy.stats.{tool}: incidents to aggregate are points; give an Analysis_Field for polygons.")
        xy = np.array([(f[2], f[3]) for f in features])
        field = "Join_Count"
        if method == "COUNT_INCIDENTS_WITHIN_AGGREGATION_POLYGONS":
            if not _given(aggregation_polygons):
                raise ValueError(f"arcpy.stats.{tool}: this method needs Polygons_For_Aggregating_Incidents_Into_Counts.")
            from shapely import points as to_points
            polygons, _, pkind, psrid = _features(tool, aggregation_polygons)
            located = to_points(xy)
            _gpkg.create(scratch, "Polygon", psrid, [_gpkg.Column(field, "Integer")])
            with da.InsertCursor(scratch, [field, "SHAPE@"]) as rows:
                for p in polygons:
                    rows.insertRow((int(sum(p[4].covers(q) for q in located)), p[4]))
        elif method in ("COUNT_INCIDENTS_WITHIN_FISHNET_POLYGONS", "COUNT_INCIDENTS_WITHIN_HEXAGON_POLYGONS"):
            unique = np.unique(xy, axis=0)
            d = _distances(unique)
            keep = ~_locational_outliers(d)
            nearest = _nearest(d)[keep]
            x0, y0 = xy.min(axis=0)
            x1, y1 = xy.max(axis=0)
            extent = max(x1 - x0, y1 - y0)
            if _given(Cell_Size):
                cell = _threshold(Cell_Size, srid)
            else:
                import shapely
                hull = shapely.MultiPoint([tuple(p) for p in xy]).convex_hull.area
                random_nn = 0.5 / math.sqrt(len(xy) / hull) if hull > 0 else math.inf
                cell = extent / 100 if 2 * random_nn < extent / 100 else 2 * max(float(nearest.mean()),
                                                                                    float(np.median(nearest)))
            said(f"Cell size: {cell}")
            if method == "COUNT_INCIDENTS_WITHIN_HEXAGON_POLYGONS":
                if _given(bounding):
                    _refuse(tool, "Bounding_Polygons_Defining_Where_Incidents_Are_Possible", bounding, "keeping empty "
                            "hexagons inside bounding polygons is not built in this equivalent")
                from .graticula import HexagonBins
                HexagonBins(Input_Features, scratch, cell, field)
            else:
                from shapely.geometry import box
                columns = int(math.floor((x1 - x0) / cell)) + 1
                rows_ = int(math.floor((y1 - y0) / cell)) + 1
                counts = np.zeros((rows_, columns), dtype=int)
                for x, y in xy:
                    counts[int((y - y0) // cell), int((x - x0) // cell)] += 1
                area = None
                if _given(bounding):
                    import shapely
                    area = shapely.union_all([f[4] for f in _features(tool, bounding)[0]])
                _gpkg.create(scratch, "Polygon", srid, [_gpkg.Column(field, "Integer")])
                with da.InsertCursor(scratch, [field, "SHAPE@"]) as written:
                    for r in range(rows_):
                        for c in range(columns):
                            square = box(x0 + c * cell, y0 + r * cell, x0 + (c + 1) * cell, y0 + (r + 1) * cell)
                            inside = area.contains(square.centroid) if area is not None else counts[r, c] > 0
                            if inside:
                                written.insertRow((int(counts[r, c]), square))
        else:
            _refuse(tool, "Incident_Data_Aggregation_Method", method, "the Integrate tool's snapping that it runs is "
                    "not documented")
        path = scratch
    features, _, _, srid = _features(tool, path, [field])
    if len(features) < 30:
        raise ValueError(f"arcpy.stats.{tool}: {len(features)} weighted features; the tool needs at least 30.")
    values = _values(tool, features, field)
    if values.min() == values.max():
        raise ValueError(f"arcpy.stats.{tool}: every weighted feature has the same value.")
    if _given(Distance_Band):
        return path, field, _threshold(Distance_Band, srid)
    xy = np.array([(f[2], f[3]) for f in features])
    d = _distances(xy)
    keep = ~_locational_outliers(d)
    nearest = _nearest(d)
    begin = float(nearest[keep].max())
    step = float(nearest[keep].mean())
    distance = None
    if not ((d <= begin + 9 * step).sum(axis=1) - 1 >= 500).any():
        zs = []
        for k in range(10):
            band = begin + k * step
            try:
                zs.append(_moran(values, _standardized(_weights(tool, features, srid, "FIXED_DISTANCE_BAND",
                                                                "EUCLIDEAN_DISTANCE", band, None), "ROW"))[3])
            except ValueError:
                zs.append(None)
        peaks = [k for k in range(1, 9) if None not in zs[k - 1:k + 2] and zs[k] > zs[k - 1] and zs[k] > zs[k + 1]]
        if peaks:
            distance = begin + peaks[0] * step
            said(f"Scale of analysis: the first peak of Incremental Spatial Autocorrelation, {distance}.")
        neighbours = None
    else:
        neighbours = 30
    if distance is None:
        n = len(features)
        k = neighbours or min(30, max(3, int(round(0.05 * n))))
        average = float(np.sort(d, axis=1)[:, min(k, n - 1)].mean())
        centre = xy.mean(axis=0)
        standard = math.sqrt(float(((xy - centre) ** 2).sum(axis=1).mean()))
        distance = standard if neighbours is None and average > standard else average
        said(f"Scale of analysis: the average distance to {k} neighbours, or one standard distance: {distance}.")
    return path, field, distance


def env_scratch():
    from ._runtime import env
    return env.scratchWorkspace


@_equivalent("Optimized Hot Spot Analysis's documented steps over grapy's Gi*, Benjamini–Hochberg FDR and Tukey's fence "
             "for locational outliers", "the tool's How it works page; Getis and Ord 1992; Benjamini and Hochberg 1995; "
             "Tukey 1977")
def OptimizedHotSpotAnalysis(Input_Features, Output_Features, Analysis_Field=None, Incident_Data_Aggregation_Method=None,
                             Bounding_Polygons_Defining_Where_Incidents_Are_Possible=None,
                             Polygons_For_Aggregating_Incidents_Into_Counts=None, Density_Surface=None, Cell_Size=None,
                             Distance_Band=None):
    """EQUIVALENT: the page's steps — aggregate incidents (fishnet, hexagon or given polygons; the cell size twice the
    larger of the average and median nearest-neighbour distances, or a hundredth of the extent when the data are dense
    against N random points in their convex hull), find the scale of analysis (the first peak of Incremental Spatial
    Autocorrelation; with none, the average distance to K = 5 percent of N neighbours, 3 to 30, capped at one standard
    distance; 30 neighbours when a feature would have 500), then Gi* (HotSpots, fixed band) with the FDR correction —
    here Benjamini–Hochberg. What the page leaves open, and grapy chose: locational outliers by Tukey's fence on the
    nearest-neighbour distance; Incremental Spatial Autocorrelation's ten bands from the largest nearest-neighbour
    distance in steps of the average one; the convex hull as the minimum bounding polygon; hexagons as
    graticula.HexagonBins draws them, the cell size their height; incident counts in ``Join_Count``.

    Refused: SNAP_NEARBY_INCIDENTS_TO_CREATE_WEIGHTED_POINTS (it runs the Integrate tool, whose snapping is not
    documented) and bounding polygons with hexagons. Density_Surface is disabled in arcpy too.
    """
    from ._runtime import AddMessage
    path, field, distance = _optimized_prepare("OptimizedHotSpotAnalysis", Input_Features, Analysis_Field,
                                               Incident_Data_Aggregation_Method,
                                               Bounding_Polygons_Defining_Where_Incidents_Are_Possible,
                                               Polygons_For_Aggregating_Incidents_Into_Counts, Cell_Size, Distance_Band,
                                               AddMessage)
    return HotSpots(path, field, Output_Features, "FIXED_DISTANCE_BAND", "EUCLIDEAN_DISTANCE", "NONE", distance, None,
                    None, "APPLY_FDR")


@_equivalent("Optimized Outlier Analysis's documented steps over grapy's Local Moran's I with seeded permutations, "
             "Benjamini–Hochberg FDR and Tukey's fence for locational outliers",
             "the tool's How it works page; Anselin 1995; Benjamini and Hochberg 1995; Tukey 1977")
def OptimizedOutlierAnalysis(Input_Features, Output_Features, Analysis_Field=None, Incident_Data_Aggregation_Method=None,
                             Bounding_Polygons_Defining_Where_Incidents_Are_Possible=None,
                             Polygons_For_Aggregating_Incidents_Into_Counts=None, Performance_Adjustment="BALANCED_499",
                             Cell_Size=None, Distance_Band=None):
    """EQUIVALENT: OptimizedHotSpotAnalysis's preparation, then Local Moran's I (ClustersOutliers, fixed band, row
    standardized) with 199, 499 or 999 conditional permutations as Performance_Adjustment says — seeded from
    env.randomGenerator, so Pro's draws differ — and the FDR correction (Benjamini–Hochberg). "For features with no
    neighbors at this distance, the Distance Band is extended to include their nearest neighbor": INFERRED, as one band
    for all, the larger of the scale and the largest nearest-neighbour distance. Refusals as OptimizedHotSpotAnalysis's.
    """
    from ._runtime import AddMessage
    permutations = {"QUICK_199": 199, "BALANCED_499": 499, "ROBUST_999": 999}.get(
        _keyword(Performance_Adjustment or "BALANCED_499"))
    if permutations is None:
        raise ValueError("arcpy.stats.OptimizedOutlierAnalysis: Performance_Adjustment is QUICK_199, BALANCED_499 or "
                         "ROBUST_999.")
    path, field, distance = _optimized_prepare("OptimizedOutlierAnalysis", Input_Features, Analysis_Field,
                                               Incident_Data_Aggregation_Method,
                                               Bounding_Polygons_Defining_Where_Incidents_Are_Possible,
                                               Polygons_For_Aggregating_Incidents_Into_Counts, Cell_Size, Distance_Band,
                                               AddMessage)
    features, _, _, _ = _features("OptimizedOutlierAnalysis", path, [field])
    distance = max(distance, float(_nearest(_distances([(f[2], f[3]) for f in features])).max()))
    return ClustersOutliers(path, field, Output_Features, "FIXED_DISTANCE_BAND", "EUCLIDEAN_DISTANCE", "ROW", distance,
                            None, "APPLY_FDR", permutations)


def _univariate_fit(column, target, weights):
    """Each feature's local coefficient of one variable alone on ``target`` with kernel ``weights``, and the trace of
    that smoother: β_i = Σ_k w_ik x_k r_k / Σ_k w_ik x_k²."""
    import numpy as np
    denominator = weights @ (column * column)
    if (denominator == 0).any():
        raise ValueError("arcpy.stats.MGWR: a local regression is empty at this bandwidth — too few neighbours.")
    beta = (weights @ (column * target)) / denominator
    trace = float((np.diag(weights) * column * column / denominator).sum())
    return beta, trace


def _univariate_aicc(column, target, d, bandwidth, kernel, adaptive):
    n = len(target)
    beta, trace = _univariate_fit(column, target, _gwr_weights(d, bandwidth, kernel, adaptive))
    residual = target - beta * column
    rss = float(residual @ residual)
    if n - 2 - trace <= 0 or rss <= 0:
        return math.inf if n - 2 - trace <= 0 else -math.inf
    return n * math.log(rss / n) + n * math.log(2 * math.pi) + n * (n + trace) / (n - 2 - trace)


@_equivalent("multiscale geographically weighted regression by back-fitting, a bandwidth per variable chosen on AICc",
             "Fotheringham, Yang and Kang 2017; Oshan, Li, Kang, Wolf and Fotheringham 2019 (mgwr)")
def MGWR(in_features, dependent_variable, model_type, explanatory_variables, output_features, neighborhood_type,
         neighborhood_selection_method, minimum_number_of_neighbors=None, maximum_number_of_neighbors=None,
         distance_unit=None, minimum_search_distance=None, maximum_search_distance=None,
         number_of_neighbors_increment=None, search_distance_increment=None, number_of_increments=None,
         number_of_neighbors=None, distance_band=None, number_of_neighbors_golden=None, number_of_neighbors_manual=None,
         number_of_neighbors_defined=None, distance_golden=None, distance_manual=None, distance_defined=None,
         prediction_locations=None, explanatory_variables_to_match=None, output_predicted_features=None,
         robust_prediction=None, local_weighting_scheme="BISQUARE", output_table=None, coefficient_raster_workspace=None,
         scale="SCALE_DATA", number_of_neighbors_gradient=None, distance_gradient=None):
    """EQUIVALENT: MGWR by back-fitting (Fotheringham, Yang and Kang 2017): y = Σ_j f_j, f_j = β_j(u)·x_j; starting
    from ordinary least squares, each term is refitted in turn as a GWR of its partial residual y − Σ_{l≠j} f_l on x_j
    alone, with its own bandwidth chosen on that fit's AICc (golden-section search, manual intervals, or given), until
    the score of change √(Σ(Δf)²/n / Σ(Σ_j f_j)²) falls below 10⁻⁵ (Oshan et al. 2019) or 200 rounds pass. The
    intercept is a variable with its own bandwidth. With SCALE_DATA (the default) every variable is standardized
    (population standard deviation) before fitting, as MGWR needs to compare bandwidths, and the coefficients are
    written back in the data's units.

    Writes the dependent and explanatory variables, INTERCEPT, C_<variable>, PREDICTED and RESIDUAL; output_table lists
    VARIABLE and BANDWIDTH (INFERRED names). Standard errors and the model's AICc need MGWR's per-term hat matrices,
    which this equivalent does not compute, and are not written. Kernels and neighbourhoods are GWR's.

    Refused: GRADIENT_SEARCH (its optimiser is not specified), CONTINUOUS only, per-variable golden or manual ranges,
    distance_unit, prediction locations and coefficient rasters.
    """
    import numpy as np
    tool = "MGWR"
    if _keyword(model_type or "CONTINUOUS") != "CONTINUOUS":
        raise ValueError(f"arcpy.stats.{tool}: only CONTINUOUS is supported, as documented.")
    for name, value in (("number_of_neighbors_golden", number_of_neighbors_golden),
                        ("number_of_neighbors_manual", number_of_neighbors_manual),
                        ("distance_golden", distance_golden), ("distance_manual", distance_manual),
                        ("number_of_neighbors_gradient", number_of_neighbors_gradient),
                        ("distance_gradient", distance_gradient), ("distance_unit", distance_unit),
                        ("prediction_locations", prediction_locations),
                        ("output_predicted_features", output_predicted_features),
                        ("coefficient_raster_workspace", coefficient_raster_workspace)):
        if _given(value):
            _refuse(tool, name, value, "it is not part of this equivalent")
    method = _keyword(neighborhood_selection_method)
    if method == "GRADIENT_SEARCH":
        _refuse(tool, "neighborhood_selection_method", method, "the gradient-based optimiser is not specified")
    kernel = _keyword(local_weighting_scheme or "BISQUARE")
    adaptive = _keyword(neighborhood_type) == "NUMBER_OF_NEIGHBORS"
    explanatory = [e for e in _list(explanatory_variables) if _given(e)]
    kept, y, x, xy, columns, kind, srid = _regression_data(tool, in_features, dependent_variable, explanatory)
    n, k = x.shape
    d = _distances(xy)
    scaled = _keyword(scale or "SCALE_DATA") not in ("NO_SCALE_DATA", "FALSE")
    y_mean, y_sd = (float(y.mean()), float(y.std())) if scaled else (0.0, 1.0)
    x_mean = np.concatenate([[0.0], x[:, 1:].mean(axis=0)]) if scaled else np.zeros(k)
    x_sd = np.concatenate([[1.0], x[:, 1:].std(axis=0)]) if scaled else np.ones(k)
    ys = (y - y_mean) / y_sd
    xs = (x - x_mean) / x_sd
    names = ["Intercept", *explanatory]
    defined = {}
    for row in _list(number_of_neighbors_defined if adaptive else distance_defined):
        pair = row if isinstance(row, (list, tuple)) else str(row).split()
        defined[str(pair[0]).lower()] = int(pair[1]) if adaptive else _threshold(pair[1], srid)

    def choose(j, target):
        if method == "USER_DEFINED":
            given = defined.get(names[j].lower(), number_of_neighbors if adaptive else distance_band)
            if not _given(given):
                raise ValueError(f"arcpy.stats.{tool}: USER_DEFINED needs a bandwidth for {names[j]}.")
            return int(given) if adaptive else (given if isinstance(given, float) else _threshold(given, srid))
        score = (lambda b: _univariate_aicc(xs[:, j], target, d, b, kernel, adaptive))
        if method == "MANUAL_INTERVALS":
            start = minimum_number_of_neighbors if adaptive else minimum_search_distance
            step = number_of_neighbors_increment if adaptive else search_distance_increment
            first = int(start) if adaptive else _threshold(start, srid)
            delta = int(step) if adaptive else _threshold(step, srid)
            return min((first + i * delta for i in range(int(number_of_increments))), key=lambda b: (score(b), b))
        if method != "GOLDEN_SEARCH":
            raise ValueError(f"arcpy.stats.{tool}: neighborhood_selection_method is GOLDEN_SEARCH, MANUAL_INTERVALS or "
                             "USER_DEFINED.")
        sorted_d = np.sort(d, axis=1)
        if adaptive:
            low = int(minimum_number_of_neighbors) if _given(minimum_number_of_neighbors) else max(int(math.ceil(0.05 * n)), 3)
            high = int(maximum_number_of_neighbors) if _given(maximum_number_of_neighbors) else max(low, n // 2)
        else:
            low = _threshold(minimum_search_distance, srid) if _given(minimum_search_distance) else \
                float(sorted_d[:, max(int(math.ceil(0.05 * n)) - 1, 1)].max())
            high = _threshold(maximum_search_distance, srid) if _given(maximum_search_distance) else \
                float(sorted_d[:, max(n // 2 - 1, 1)].max())
        return _golden(score, low, high, adaptive)

    beta_global = np.linalg.lstsq(xs, ys, rcond=None)[0]
    betas = np.tile(beta_global, (n, 1))
    terms = betas * xs
    bandwidths = [None] * k
    for _ in range(200):
        before = terms.copy()
        for j in range(k):
            partial = ys - terms.sum(axis=1) + terms[:, j]
            bandwidths[j] = choose(j, partial)
            beta_j, _ = _univariate_fit(xs[:, j], partial, _gwr_weights(d, bandwidths[j], kernel, adaptive))
            betas[:, j] = beta_j
            terms[:, j] = beta_j * xs[:, j]
        change = math.sqrt(float(((terms - before) ** 2).sum()) / n / float((terms.sum(axis=1) ** 2).sum()))
        if change < 1e-5:
            break
    unscaled = betas * y_sd / x_sd
    unscaled[:, 0] = y_mean + y_sd * betas[:, 0] - (unscaled[:, 1:] * x_mean[1:]).sum(axis=1)
    fitted = (unscaled * x).sum(axis=1)
    from ._runtime import AddMessage
    for name, bandwidth in zip(names, bandwidths):
        AddMessage(f"Bandwidth of {name}: {bandwidth}")
    by_name = {c.name.lower(): c for c in columns}
    keep = [by_name[dependent_variable.lower()], *[by_name[e.lower()] for e in explanatory]]
    labels = ["INTERCEPT", *[f"C_{e}" for e in explanatory], "PREDICTED", "RESIDUAL"]
    out = _out(output_features)
    _gpkg.create(out, kind, srid, [*keep, *[_gpkg.Column(name, "Double") for name in labels]])
    with da.InsertCursor(out, [*[c.name for c in keep], *labels, "SHAPE@"]) as written:
        for i, f in enumerate(kept):
            written.insertRow((*[f[1][c.name.lower()] for c in keep], *[float(v) for v in unscaled[i]],
                               float(fitted[i]), float(y[i] - fitted[i]), f[4]))
    table = ""
    if _given(output_table):
        table = _write_table(output_table, [_gpkg.Column("VARIABLE", "String"), _gpkg.Column("BANDWIDTH", "Double")],
                             [(name, float(b)) for name, b in zip(names, bandwidths)])
    return Result(out, table)


def _gwr_cv(x, y, d, bandwidth, kernel, adaptive):
    """The cross-validation score Σ (y_i − ŷ_{≠i})², from the leave-one-out residual e_i/(1 − S_ii) (Fotheringham
    et al. 2002, eq. 2.30)."""
    import numpy as np
    betas, s, *_ = _gwr_fit(x, y, d, bandwidth, kernel, adaptive)
    residual = y - (x * betas).sum(axis=1)
    influence = np.diag(s)
    if (influence >= 1).any():
        return math.inf
    return float(((residual / (1 - influence)) ** 2).sum())


@_equivalent("geographically weighted regression with a Gaussian kernel, the bandwidth by golden-section search on AICc "
             "or cross-validation", "Fotheringham, Brunsdon and Charlton, Geographically Weighted Regression, 2002")
def GeographicallyWeightedRegression(in_features, dependent_field, explanatory_field, out_featureclass, kernel_type,
                                     bandwidth_method, distance=None, number_of_neighbors=None, weight_field=None,
                                     coefficient_raster_workspace=None, cell_size=None, in_prediction_locations=None,
                                     prediction_explanatory_field=None, out_prediction_featureclass=None):
    """EQUIVALENT: GWR as stats.GWR computes it, with the Gaussian kernel this tool documents — FIXED a distance,
    ADAPTIVE a number of neighbours — and the bandwidth given (BANDWIDTH_PARAMETER) or chosen by golden-section search
    on AICc (eq. 2.33) or on the cross-validation score (eq. 2.30) between 5 percent and half of the features. The
    summary table is written beside the output with ``_supp`` appended, as documented, as Diag_Name and Diag_Value
    (INFERRED fields): Bandwidth, ResidualSquares, EffectiveNumber, Sigma, AICc, R2, R2Adjusted. Output fields as
    stats.GWR's. Refused: weight_field, coefficient rasters, prediction locations.
    """
    import numpy as np
    tool = "GeographicallyWeightedRegression"
    for name, value in (("weight_field", weight_field), ("coefficient_raster_workspace", coefficient_raster_workspace),
                        ("in_prediction_locations", in_prediction_locations),
                        ("out_prediction_featureclass", out_prediction_featureclass)):
        if _given(value):
            _refuse(tool, name, value, "it is not part of this equivalent")
    adaptive = _keyword(kernel_type) == "ADAPTIVE"
    if not adaptive and _keyword(kernel_type) != "FIXED":
        raise ValueError(f"arcpy.stats.{tool}: kernel_type is FIXED or ADAPTIVE.")
    method = _keyword(bandwidth_method)
    explanatory = [e for e in _list(explanatory_field) if _given(e)]
    kept, y, x, xy, _, _, srid = _regression_data(tool, in_features, dependent_field, explanatory)
    n = len(y)
    d = _distances(xy)
    if method == "BANDWIDTH_PARAMETER":
        given = number_of_neighbors if adaptive else distance
        if not _given(given):
            raise ValueError(f"arcpy.stats.{tool}: BANDWIDTH_PARAMETER needs {'number_of_neighbors' if adaptive else 'distance'}.")
        bandwidth = int(given) if adaptive else _threshold(given, srid)
    elif method in ("AICC", "CV"):
        score = (lambda b: _gwr_aicc(x, y, d, b, "GAUSSIAN", adaptive)) if method == "AICC" else \
            (lambda b: _gwr_cv(x, y, d, b, "GAUSSIAN", adaptive))
        sorted_d = np.sort(d, axis=1)
        if adaptive:
            low, high = max(int(math.ceil(0.05 * n)), x.shape[1] + 1), max(x.shape[1] + 2, n // 2)
        else:
            low = float(sorted_d[:, max(int(math.ceil(0.05 * n)) - 1, 1)].max())
            high = float(sorted_d[:, max(n // 2 - 1, 1)].max())
        bandwidth = _golden(score, low, high, adaptive)
    else:
        raise ValueError(f"arcpy.stats.{tool}: bandwidth_method is AICc, CV or BANDWIDTH_PARAMETER.")
    out = GWR(in_features, dependent_field, "CONTINUOUS", explanatory, out_featureclass,
              "NUMBER_OF_NEIGHBORS" if adaptive else "DISTANCE_BAND", "USER_DEFINED",
              number_of_neighbors=bandwidth if adaptive else None, distance_band=None if adaptive else bandwidth,
              local_weighting_scheme="GAUSSIAN").getOutput(0)
    betas, s, *_ = _gwr_fit(x, y, d, bandwidth, "GAUSSIAN", adaptive)
    residual = y - (x * betas).sum(axis=1)
    rss, trace = float(residual @ residual), float(np.trace(s))
    tss = float(((y - y.mean()) ** 2).sum())
    r2 = 1 - rss / tss
    rows = [("Bandwidth", float(bandwidth)), ("ResidualSquares", rss), ("EffectiveNumber", trace),
            ("Sigma", math.sqrt(rss / (n - trace))), ("AICc", _gwr_aicc(x, y, d, bandwidth, "GAUSSIAN", adaptive)),
            ("R2", r2), ("R2Adjusted", 1 - (1 - r2) * (n - 1) / (n - trace - 1))]
    supp = _out(out)[:-5] + "_supp.gpkg" if _out(out).lower().endswith(".gpkg") else _out(out) + "_supp"
    table = _write_table(supp, [_gpkg.Column("Diag_Name", "String"), _gpkg.Column("Diag_Value", "Double")], rows)
    return Result(out, table)


# ---- Neighbourhoods, kernels and appended fields, shared by the tools below ----------------------------------------

def _neighbourhoods(tool, features, kind, srid, neighborhood_type, distance_band, number_of_neighbors, cap=None):
    """Each feature's neighbours, itself left out, as (neighbours, d, band): DISTANCE_BAND (the default band the shortest
    that gives every feature one other neighbour), K_NEAREST_NEIGHBORS or NUMBER_OF_NEIGHBORS, CONTIGUITY_EDGES_ONLY,
    CONTIGUITY_EDGES_CORNERS and DELAUNAY_TRIANGULATION, as the Modeling Spatial Relationships tools describe them.
    GET_SPATIAL_WEIGHTS_FROM_FILE is refused. INFERRED: a feature exactly at the band is inside it; neighbours tied at the
    k-th distance are taken by object id."""
    import numpy as np
    n = len(features)
    xy = [(f[2], f[3]) for f in features]
    d = _distances(xy)
    oids = [f[0] for f in features]
    kind_of = _keyword(neighborhood_type)
    band = None
    if kind_of in ("DISTANCE_BAND", "FIXED_DISTANCE_BAND"):
        band = _threshold(distance_band, srid) if _given(distance_band) else float(_nearest(d).max())
        neighbours = []
        for i in range(n):
            near = sorted((j for j in range(n) if j != i and d[i, j] <= band), key=lambda j: (d[i, j], oids[j]))
            neighbours.append(near[:cap] if cap else near)
    elif kind_of in ("NUMBER_OF_NEIGHBORS", "K_NEAREST_NEIGHBORS"):
        k = int(number_of_neighbors)
        if not 1 <= k < n:
            raise ValueError(f"arcpy.stats.{tool}: number_of_neighbors must be between 1 and {n - 1}.")
        neighbours = [sorted((j for j in range(n) if j != i), key=lambda j: (d[i, j], oids[j]))[:k] for i in range(n)]
    elif kind_of in ("CONTIGUITY_EDGES_ONLY", "CONTIGUITY_EDGES_CORNERS"):
        w = _weights(tool, features, srid, kind_of, "EUCLIDEAN_DISTANCE", None, None)
        neighbours = [[int(j) for j in np.nonzero(w[i])[0]] for i in range(n)]
    elif kind_of == "DELAUNAY_TRIANGULATION":
        if kind != "Point":
            raise ValueError(f"arcpy.stats.{tool}: DELAUNAY_TRIANGULATION is for point features.")
        neighbours = _delaunay_neighbours(tool, xy)
    elif kind_of == "GET_SPATIAL_WEIGHTS_FROM_FILE":
        _refuse(tool, "neighborhood_type", kind_of, "a spatial weights matrix file's .swm layout is not published")
    else:
        raise ValueError(f"arcpy.stats.{tool}: neighborhood_type '{neighborhood_type}' is not one the tool takes.")
    return neighbours, d, band


def _kernel(scheme, distance, bandwidth):
    """The documented kernels: bisquare ``(1 − (d/b)²)²`` and Gaussian ``exp(−½(d/b)²)``; 1 unweighted."""
    if scheme in ("UNWEIGHTED", "NONE"):
        return 1.0
    ratio = (distance / bandwidth) ** 2 if bandwidth > 0 else 0.0
    return (1 - ratio) ** 2 if scheme == "BISQUARE" else math.exp(-0.5 * ratio)


def _two_sided_permutation(observed, draws):
    """The documented permutation p-value: the permuted values at least as extreme as the observed one on the side with
    fewer of them, plus one, over the permutations plus one, doubled. INFERRED: a tie counts as at least as extreme, and
    the doubled value is capped at 1."""
    import numpy as np
    draws = np.asarray(draws)
    larger = int((draws >= observed).sum())
    smaller = int((draws <= observed).sum())
    return min(1.0, 2 * (min(larger, smaller) + 1) / (len(draws) + 1))


def _rows_with_oids(tool, path, fields):
    """Every row, shape or none, as (oid, {field: value}); a missing field is a ValueError."""
    columns, kind, srid, _ = _schema(path)
    names = {c.name.lower(): c.name for c in columns}
    wanted = []
    for field in fields:
        if field.lower() not in names:
            raise ValueError(f"arcpy.stats.{tool}: the input has no field '{field}'.")
        wanted.append(names[field.lower()])
    with da.SearchCursor(path, ["OID@", *wanted]) as rows:
        out = [(row[0], dict(zip([f.lower() for f in wanted], row[1:]))) for row in rows]
    return out, columns, kind, srid


def _appended(tool, in_table, append, out_table, new_columns, values_by_oid):
    """The new fields written beside the input's own: on the input itself when ``append``, otherwise on a copy at
    ``out_table`` (CopyFeatures for features, CopyRows for a table). ``values_by_oid`` maps an input object id to the
    row of new values. INFERRED: a field already there of the same name is overwritten, and the copy's rows follow the
    input's in object id order."""
    from . import management
    _, kind, srid, _ = _schema(in_table)
    if append:
        target = in_table
        mapping = {oid: oid for oid in values_by_oid}
    else:
        if not _given(out_table):
            raise ValueError(f"arcpy.stats.{tool}: an output is needed when the fields are not appended to the input.")
        target = _out(out_table)
        if kind == "Geometry" and srid == 0:
            management.CopyRows(in_table, target)
        else:
            management.CopyFeatures(in_table, target)
        with da.SearchCursor(in_table, ["OID@"]) as rows:
            source = [r[0] for r in rows]
        with da.SearchCursor(target, ["OID@"]) as rows:
            copied = [r[0] for r in rows]
        mapping = dict(zip(source, copied))
    present = {c.name.lower() for c in _schema(target)[0]}
    for column in new_columns:
        if column.name.lower() not in present:
            _gpkg.add_column(target, column)
    by_target = {mapping[oid]: row for oid, row in values_by_oid.items() if oid in mapping}
    with da.UpdateCursor(target, ["OID@", *[c.name for c in new_columns]]) as rows:
        for row in rows:
            values = by_target.get(row[0])
            rows.updateRow((row[0], *(values if values is not None else [None] * len(new_columns))))
    return target


def _wants_append(value, yes=("APPEND", "APPEND_TO_INPUT", "TRUE")):
    return value is True or _keyword(value) in yes


def _pairs(value, width):
    """A value table as rows of ``width``: a list of lists, or the text form ``"a b;c d"``."""
    if not _given(value):
        return []
    if isinstance(value, str):
        rows = [part.split() for part in value.split(";") if part.strip()]
    else:
        rows = [[item] if not isinstance(item, (list, tuple)) else list(item) for item in value]
    for row in rows:
        if len(row) < width:
            row.extend([None] * (width - len(row)))
    return rows


# ---- Modeling Spatial Relationships: Bivariate Spatial Association (Lee's L) --------------------------------------

_PERMUTATION_COUNTS = (99, 199, 499, 999, 4999, 9999)


@_equivalent("Lee's L by its documented formulas, its permutations drawn with NumPy's PCG64 from env.randomGenerator",
             "Lee 2001; Lee 2009; Esri's \"How Bivariate Spatial Association (Lee's L) works\"")
def BivariateSpatialAssociation(in_features, analysis_field1, analysis_field2, out_features, neighborhood_type=None,
                                distance_band=None, num_neighbors=None, weights_matrix_file=None,
                                local_weighting_scheme="UNWEIGHTED", kernel_bandwidth=None, num_permutations=999):
    """EQUIVALENT only in its draws: every statistic is the formula Esri's "How Bivariate Spatial Association (Lee's L)
    works" gives, and the permutations are the ones it describes — all values of the two variables reassigned to new
    locations together — but drawn with NumPy's PCG64 from env.randomGenerator's seed (0 when unset), so a run repeats
    itself and its p-values differ from Pro's.

    With x̃ and ỹ the neighbourhood weighted averages (each neighbourhood holding the feature itself at weight 1, the
    weights row standardized): ``L = Σ(x̃ − x̄)(ỹ − ȳ) / (√Σ(x − x̄)² √Σ(y − ȳ)²)``, ``L_i = n(x̃_i − x̄)(ỹ_i − ȳ) / (same)``,
    the spatial smoothing scalars ``SSS = Σ(x̃ − x̄)²/Σ(x − x̄)²`` and the correlation of the neighbourhood averages
    (both written as messages). A p-value is the permuted values at least as extreme, on the side with fewer, plus one,
    over the permutations plus one, doubled.

    Neighbourhoods: DISTANCE_BAND (the default for points; at most 1,000 neighbours), K_NEAREST_NEIGHBORS (8 by default,
    at least 2), CONTIGUITY_EDGES_ONLY, CONTIGUITY_EDGES_CORNERS (the default for polygons), DELAUNAY_TRIANGULATION.
    BISQUARE weights ``(1 − (d/b)²)²``, b the kernel bandwidth, by default the band or, for k nearest neighbours, the
    distance to the (k+1)-th neighbour. Refused: GET_SPATIAL_WEIGHTS_FROM_FILE; a given bisquare bandwidth short of a
    neighbour (the formula as written turns positive again there).

    Output fields, as documented: SOURCE_ID, the two analysis fields, LOCAL_L, NWA_VAR1, NWA_VAR2, P_VALUE, SIG_LEVEL,
    ASSOC_CAT and NUM_NBRS. Derived outputs, in order: the output, Lee's L, its p-value, the Pearson correlation.
    INFERRED: NWA_VAR1 and NWA_VAR2 are the field names as written, not the variables' names; significance is p < 0.10,
    < 0.05, < 0.01; a neighbourhood average equal to the mean counts as Low; features with a null in either field are
    left out.
    """
    import numpy as np
    tool = "BivariateSpatialAssociation"
    features, columns, kind, srid = _features(tool, in_features, [analysis_field1, analysis_field2])
    k1, k2 = analysis_field1.lower(), analysis_field2.lower()
    features = [f for f in features if f[1][k1] is not None and f[1][k2] is not None]
    n = len(features)
    if n < 3:
        raise ValueError(f"arcpy.stats.{tool} needs three features or more.")
    kind_of = _keyword(neighborhood_type) if _given(neighborhood_type) else (
        "CONTIGUITY_EDGES_CORNERS" if kind == "Polygon" else "DISTANCE_BAND")
    k = int(num_neighbors) if _given(num_neighbors) else 8
    if kind_of == "K_NEAREST_NEIGHBORS" and k < 2:
        raise ValueError(f"arcpy.stats.{tool}: num_neighbors must be at least 2.")
    neighbours, d, band = _neighbourhoods(tool, features, kind, srid, kind_of, distance_band, k, cap=1000)
    scheme = _keyword(local_weighting_scheme) if _given(local_weighting_scheme) else "UNWEIGHTED"
    if scheme not in ("UNWEIGHTED", "BISQUARE"):
        raise ValueError(f"arcpy.stats.{tool}: local_weighting_scheme '{local_weighting_scheme}' is neither UNWEIGHTED "
                         "nor BISQUARE.")
    w = np.zeros((n, n))
    for i in range(n):
        w[i, i] = 1.0
        if scheme == "BISQUARE":
            if kind_of not in ("DISTANCE_BAND", "K_NEAREST_NEIGHBORS"):
                raise ValueError(f"arcpy.stats.{tool}: BISQUARE weights only DISTANCE_BAND and K_NEAREST_NEIGHBORS.")
            if _given(kernel_bandwidth):
                b = _threshold(kernel_bandwidth, srid)
                if any(d[i, j] > b for j in neighbours[i]):
                    _refuse(tool, "kernel_bandwidth", kernel_bandwidth, "a neighbour lies beyond it, where the bisquare "
                            "formula as written turns positive again")
            elif kind_of == "DISTANCE_BAND":
                b = band
            else:
                others = np.sort(np.delete(d[i], i))
                if len(others) <= k:
                    raise ValueError(f"arcpy.stats.{tool}: the adaptive bandwidth needs more than k + 1 features.")
                b = float(others[k])
        for j in neighbours[i]:
            w[i, j] = _kernel(scheme, d[i, j], b) if scheme == "BISQUARE" else 1.0
    w = _standardized(w, "ROW")
    x = np.array([float(f[1][k1]) for f in features])
    y = np.array([float(f[1][k2]) for f in features])
    dx, dy = x - x.mean(), y - y.mean()
    denominator = math.sqrt(float(dx @ dx)) * math.sqrt(float(dy @ dy))
    if denominator == 0:
        raise ValueError(f"arcpy.stats.{tool}: an analysis field is constant.")

    def local(xs, ys):
        return n * (w @ xs - xs.mean()) * (w @ ys - ys.mean()) / denominator

    observed = local(x, y)
    lee = float(observed.mean())
    count = int(num_permutations) if _given(num_permutations) else 999
    if count not in _PERMUTATION_COUNTS:
        raise ValueError(f"arcpy.stats.{tool}: num_permutations must be one of {_PERMUTATION_COUNTS}.")
    random = np.random.default_rng(_seed())
    drawn = np.empty((count, n))
    for r in range(count):
        order = random.permutation(n)
        drawn[r] = local(x[order], y[order])
    p_global = _two_sided_permutation(lee, drawn.mean(axis=1))
    p_local = [_two_sided_permutation(observed[i], drawn[:, i]) for i in range(n)]
    xt, yt = w @ x, w @ y
    sss_x = float(((xt - x.mean()) ** 2).sum() / (dx @ dx))
    sss_y = float(((yt - y.mean()) ** 2).sum() / (dy @ dy))
    pearson = float(dx @ dy) / denominator
    averages = float(np.corrcoef(xt, yt)[0, 1]) if np.ptp(xt) > 0 and np.ptp(yt) > 0 else None
    from ._runtime import AddMessage
    AddMessage(f"Global Lee's L: {lee}")
    AddMessage(f"Global P-value: {p_global}")
    AddMessage(f"Spatial Smoothing Scalar ({analysis_field1}): {sss_x}")
    AddMessage(f"Spatial Smoothing Scalar ({analysis_field2}): {sss_y}")
    AddMessage(f"Pearson Correlation (raw): {pearson}")
    AddMessage(f"Pearson Correlation (neighborhood averages): {averages}")
    levels, categories = [], []
    for i in range(n):
        p = p_local[i]
        level = "99% Significant" if p < 0.01 else "95% Significant" if p < 0.05 else "90% Significant" if p < 0.10 \
            else "Not Significant"
        levels.append(level)
        if level == "Not Significant":
            categories.append("Not Significant")
        else:
            categories.append(("High" if xt[i] > x.mean() else "Low") + "-" + ("High" if yt[i] > y.mean() else "Low"))
    by_name = {c.name.lower(): c for c in columns}
    out = _out(out_features)
    out_columns = [_gpkg.Column("SOURCE_ID", "Integer"), by_name[k1], by_name[k2], _gpkg.Column("LOCAL_L", "Double"),
                   _gpkg.Column("NWA_VAR1", "Double"), _gpkg.Column("NWA_VAR2", "Double"),
                   _gpkg.Column("P_VALUE", "Double"), _gpkg.Column("SIG_LEVEL", "String"),
                   _gpkg.Column("ASSOC_CAT", "String"), _gpkg.Column("NUM_NBRS", "Integer")]
    _gpkg.create(out, kind, srid, out_columns)
    with da.InsertCursor(out, [*[c.name for c in out_columns], "SHAPE@"]) as rows:
        for i, f in enumerate(features):
            rows.insertRow((f[0], f[1][k1], f[1][k2], float(observed[i]), float(xt[i]), float(yt[i]), p_local[i],
                            levels[i], categories[i], int((w[i] > 0).sum()), f[4]))
    return Result(out, _text(lee), _text(p_global), _text(pearson))


# ---- Analyzing Patterns: Colocation Analysis ----------------------------------------------------------------------

@_equivalent("the local colocation quotient with a Gaussian, bisquare or box kernel and conditional permutation "
             "pseudo p-values with a stated seed; the global colocation quotient as their mean",
             "Leslie and Kronenfeld 2011; Wang, Hu, Wang and Li 2017")
def ColocationAnalysis(input_type, in_features_of_interest, output_features, field_of_interest=None,
                       time_field_of_interest=None, category_of_interest=None, input_feature_for_comparison=None,
                       field_for_comparison=None, time_field_for_comparison=None, category_for_comparison=None,
                       neighborhood_type="K_NEAREST_NEIGHBORS", number_of_neighbors=None, distance_band=None,
                       weights_matrix_file=None, temporal_relationship_type=None, time_step_interval=None,
                       number_of_permutations=99, local_weighting_scheme="GAUSSIAN", output_table=None):
    """EQUIVALENT: Esri's "How Colocation Analysis works" page answers 404, so the statistic is the literature's. For a
    point A_i of the category of interest A and the neighbouring category B, among N points of which N_B are B, the
    local colocation quotient (Wang et al. 2017, after Leslie and Kronenfeld 2011) is
    ``LCLQ_i = (Σ_j w_ij f_j / Σ_j w_ij) / (N'_B / (N − 1))`` over A_i's neighbours j (A_i itself left out), f_j = 1
    when j is B, N'_B = N_B − 1 when A and B are one category and N_B otherwise. Kernels, b the distance to the farthest
    neighbour (k nearest) or the band: GAUSSIAN ``exp(−½(d/b)²)`` (the default), BISQUARE ``(1 − (d/b)²)²``, NONE 1.

    Pseudo p-values by conditional permutation (Anselin 1995's scheme): A_i held, the other N − 1 points' categories
    shuffled ``number_of_permutations`` times by NumPy's PCG64 from env.randomGenerator's seed (0 when unset); the
    permuted LCLQs at least as extreme on the side with fewer, plus one, over the permutations plus one, doubled — which
    gives the documented smallest p-value, 0.02 for 99 permutations. The global table holds, for every category pair,
    the mean LCLQ of the first's points — with one unweighted nearest neighbour, Leslie and Kronenfeld's CLQ.

    Input types: SINGLE_DATASET (both categories from field_of_interest), TWO_DATASETS (the comparison dataset's
    categories from field_for_comparison, both datasets' points counted together), DATASETS_WITHOUT_CATEGORIES (each
    dataset one category, named by its table). Refused: time fields and space-time windows; GET_SPATIAL_WEIGHTS_FROM_FILE.
    Neighbourhoods: K_NEAREST_NEIGHBORS (8 by default) or DISTANCE_BAND (by default the mean distance to each point's
    eighth neighbour, "the average distance at which each feature has at least eight neighbors").

    INFERRED: the output carries every feature of interest with LCLQ, P_VALUE, LCLQ_BIN and LCLQ_TYPE, null for the
    points not of category A; LCLQ_TYPE is "Colocated - Significant" (p < 0.05, LCLQ > 1), "Isolated - Significant"
    (p < 0.05, LCLQ < 1) or "Not Significant", and LCLQ_BIN 1, −1 or 0 with it; the global table has CATEGORY_OF_INTEREST,
    NEIGHBORING_CATEGORY and GLOBAL_CLQ; a point with no weight among its neighbours has a null LCLQ.
    """
    import os
    import numpy as np
    tool = "ColocationAnalysis"
    if _given(time_field_of_interest) or _given(time_field_for_comparison):
        _refuse(tool, "time_field_of_interest", time_field_of_interest or time_field_for_comparison,
                "space-time windows are not built")
    mode = _keyword(input_type)
    if mode not in ("SINGLE_DATASET", "TWO_DATASETS", "DATASETS_WITHOUT_CATEGORIES"):
        raise ValueError(f"arcpy.stats.{tool}: input_type '{input_type}' is not one the tool takes.")
    first_fields = [field_of_interest] if mode != "DATASETS_WITHOUT_CATEGORIES" else []
    interest, columns, kind, srid = _features(tool, in_features_of_interest, first_fields)
    if kind != "Point":
        raise ValueError(f"arcpy.stats.{tool} takes point features only.")

    def label(path):
        from ._gpkg import split
        file, table = split(_out(path))
        return table or os.path.splitext(os.path.basename(file))[0]

    if mode == "SINGLE_DATASET":
        if not _given(field_of_interest):
            raise ValueError(f"arcpy.stats.{tool}: SINGLE_DATASET needs field_of_interest.")
        points = [(f, f[1][field_of_interest.lower()]) for f in interest]
        own = [True] * len(points)
    else:
        if not _given(input_feature_for_comparison):
            raise ValueError(f"arcpy.stats.{tool}: {mode} needs input_feature_for_comparison.")
        second_fields = [field_for_comparison] if mode == "TWO_DATASETS" else []
        if mode == "TWO_DATASETS" and not (_given(field_of_interest) and _given(field_for_comparison)):
            raise ValueError(f"arcpy.stats.{tool}: TWO_DATASETS needs field_of_interest and field_for_comparison.")
        other, _, other_kind, other_srid = _features(tool, input_feature_for_comparison, second_fields)
        if other_kind != "Point" or other_srid != srid:
            raise ValueError(f"arcpy.stats.{tool}: the comparison features must be points in the same reference.")
        if mode == "TWO_DATASETS":
            points = [(f, ("interest", f[1][field_of_interest.lower()])) for f in interest] + \
                     [(f, ("comparison", f[1][field_for_comparison.lower()])) for f in other]
            category_a, category_b = ("interest", category_of_interest), ("comparison", category_for_comparison)
        else:
            category_a, category_b = label(in_features_of_interest), label(input_feature_for_comparison)
            points = [(f, category_a) for f in interest] + [(f, category_b) for f in other]
        own = [True] * len(interest) + [False] * (len(points) - len(interest))
    if mode == "SINGLE_DATASET":
        category_a, category_b = category_of_interest, category_for_comparison
    if mode != "DATASETS_WITHOUT_CATEGORIES" and not (_given(category_of_interest) and _given(category_for_comparison)):
        raise ValueError(f"arcpy.stats.{tool}: category_of_interest and category_for_comparison are needed.")

    def same(value, wanted):
        if isinstance(value, tuple):
            return value[0] == wanted[0] and value[1] is not None and str(value[1]) == str(wanted[1])
        return value is not None and str(value) == str(wanted)

    kept = [i for i, (_, c) in enumerate(points) if c is not None and not (isinstance(c, tuple) and c[1] is None)]
    points = [points[i] for i in kept]
    own = [own[i] for i in kept]
    n = len(points)
    if n < 3:
        raise ValueError(f"arcpy.stats.{tool} needs three points or more.")
    xy = np.array([(f[2], f[3]) for f, _ in points])
    d = _distances(xy)
    oids = list(range(n))
    kind_of = _keyword(neighborhood_type) if _given(neighborhood_type) else "K_NEAREST_NEIGHBORS"
    if kind_of == "GET_SPATIAL_WEIGHTS_FROM_FILE":
        _refuse(tool, "neighborhood_type", kind_of, "a spatial weights matrix file's .swm layout is not published")
    scheme = _keyword(local_weighting_scheme) if _given(local_weighting_scheme) else "GAUSSIAN"
    if scheme not in ("GAUSSIAN", "BISQUARE", "NONE"):
        raise ValueError(f"arcpy.stats.{tool}: local_weighting_scheme '{local_weighting_scheme}' is not one of "
                         "GAUSSIAN, BISQUARE, NONE.")
    neighbours, bandwidths = [], []
    if kind_of == "K_NEAREST_NEIGHBORS":
        k = int(number_of_neighbors) if _given(number_of_neighbors) else 8
        if not 1 <= k < n:
            raise ValueError(f"arcpy.stats.{tool}: number_of_neighbors must be between 1 and {n - 1}.")
        for i in range(n):
            near = sorted((j for j in range(n) if j != i), key=lambda j: (d[i, j], oids[j]))[:k]
            neighbours.append(near)
            bandwidths.append(float(d[i, near[-1]]))
    elif kind_of == "DISTANCE_BAND":
        if _given(distance_band):
            band = _threshold(distance_band, srid)
        else:
            if n <= 8:
                raise ValueError(f"arcpy.stats.{tool}: the default distance band needs more than eight points.")
            band = float(np.sort(d + np.diag(np.full(n, np.inf)), axis=1)[:, 7].mean())
        for i in range(n):
            neighbours.append([j for j in range(n) if j != i and d[i, j] <= band])
            bandwidths.append(band)
    else:
        raise ValueError(f"arcpy.stats.{tool}: neighborhood_type '{neighborhood_type}' is not one the tool takes.")
    weights = [np.array([_kernel(scheme, d[i, j], bandwidths[i]) for j in neighbours[i]]) for i in range(n)]
    categories = [c for _, c in points]

    def quotient(i, is_b, share):
        total = float(weights[i].sum())
        if total <= 0 or share <= 0:
            return None
        return float(weights[i] @ is_b[neighbours[i]]) / total / share

    def lclq(a_value, b_value, i, is_b):
        n_b = int(is_b.sum()) - (1 if same(categories[i], b_value) else 0)
        return quotient(i, is_b, n_b / (n - 1))

    is_b = np.array([1.0 if same(c, category_b) else 0.0 for c in categories])
    count = int(number_of_permutations) if _given(number_of_permutations) else 99
    if count not in (99, 199, 499, 999, 9999):
        raise ValueError(f"arcpy.stats.{tool}: number_of_permutations must be one of 99, 199, 499, 999, 9999.")
    random = np.random.default_rng(_seed())
    values, p_values = [None] * n, [None] * n
    for i in range(n):
        if not own[i] or not same(categories[i], category_a):
            continue
        observed = lclq(category_a, category_b, i, is_b)
        values[i] = observed
        if observed is None:
            continue
        others = np.delete(is_b, i)
        n_b = int(is_b.sum()) - int(is_b[i])
        share = n_b / (n - 1)
        draws = []
        for _ in range(count):
            shuffled = np.insert(random.permutation(others), i, is_b[i])
            draws.append(quotient(i, shuffled, share))
        p_values[i] = _two_sided_permutation(observed, draws)
    out = _out(output_features)
    out_columns = [*columns, _gpkg.Column("LCLQ", "Double"), _gpkg.Column("P_VALUE", "Double"),
                   _gpkg.Column("LCLQ_BIN", "Integer"), _gpkg.Column("LCLQ_TYPE", "String")]
    _gpkg.create(out, "Point", srid, out_columns)
    names = [c.name for c in columns]
    position = {points[i][0][0]: i for i in range(n) if own[i]}
    with da.SearchCursor(in_features_of_interest, ["OID@", *names, "SHAPE@"]) as source, \
            da.InsertCursor(out, [*names, "LCLQ", "P_VALUE", "LCLQ_BIN", "LCLQ_TYPE", "SHAPE@"]) as written:
        for row in source:
            i = position.get(row[0])
            value = values[i] if i is not None else None
            p = p_values[i] if i is not None else None
            if value is None or p is None:
                kind_of_result, bin_ = (None, None) if value is None else ("Not Significant", 0)
            elif p < 0.05 and value > 1:
                kind_of_result, bin_ = "Colocated - Significant", 1
            elif p < 0.05 and value < 1:
                kind_of_result, bin_ = "Isolated - Significant", -1
            else:
                kind_of_result, bin_ = "Not Significant", 0
            written.insertRow((*row[1:-1], value, p, bin_, kind_of_result, row[-1]))
    table = None
    if _given(output_table):
        firsts = sorted({c for c, o in zip(categories, own) if o}, key=str)
        seconds = sorted({c for c, o in zip(categories, own) if not o} if mode != "SINGLE_DATASET" else set(categories),
                         key=str)
        if mode == "DATASETS_WITHOUT_CATEGORIES":
            firsts = seconds = [category_a, category_b]
        rows = []
        for a in firsts:
            for b in seconds:
                b_flags = np.array([1.0 if c == b else 0.0 for c in categories])
                local = [lclq(a, b, i, b_flags) for i in range(n) if categories[i] == a]
                local = [v for v in local if v is not None]
                shown = (lambda c: c[1] if isinstance(c, tuple) else c)
                rows.append((str(shown(a)), str(shown(b)), sum(local) / len(local) if local else None))
        table = _write_table(output_table, [_gpkg.Column("CATEGORY_OF_INTEREST", "String"),
                                            _gpkg.Column("NEIGHBORING_CATEGORY", "String"),
                                            _gpkg.Column("GLOBAL_CLQ", "Double")], rows)
    return Result(out, table) if table else Result(out)


# ---- Mapping Clusters: Calculate Rates ----------------------------------------------------------------------------

_RATE_METHODS = ("CRUDE_RATE", "GLOBAL_EMPIRICAL_BAYES", "LOCAL_EMPIRICAL_BAYES", "LOCALLY_WEIGHTED_AVERAGE",
                 "LOCALLY_WEIGHTED_MEDIAN")


def _empirical_bayes(counts, populations):
    """Marshall's (1991) moment estimator for Poisson counts: b = Σy/Σn, s² = Σn(r − b)²/Σn, a = s² − b/n̄ (0 when
    negative); each rate shrunk to b by C = a/(a + b/n)."""
    total = sum(populations)
    b = sum(counts) / total
    mean_n = total / len(populations)
    s2 = sum(n * (y / n - b) ** 2 for y, n in zip(counts, populations)) / total
    a = max(0.0, s2 - b / mean_n)
    return b, a


@_equivalent("crude rates; empirical Bayes smoothing by Marshall's moment estimator, global and local; the spatial "
             "rate smoother Σy/Σn and the population-weighted median of the neighbourhood's rates",
             "Marshall 1991; Clayton and Kaldor 1987; Kafadar 1996")
def CalculateRates(in_table, rate_fields, append_to_input="NO_APPEND", out_table=None, rate_method="CRUDE_RATE",
                   probability_distribution="POISSON", neighborhood_type=None, distance_band=None,
                   number_of_neighbors=None, weights_matrix_file=None, local_weighting_scheme="UNWEIGHTED",
                   kernel_bandwidth=None, rate_multiplier=None):
    """EQUIVALENT: the page giving Calculate Rates' formulas answers 404, so the smoothing is the literature's.
    CRUDE_RATE is ``y/n``, null where n is 0. GLOBAL_EMPIRICAL_BAYES shrinks each crude rate r toward the global rate
    b = Σy/Σn by Marshall's (1991) moment estimator for Poisson counts (after Clayton and Kaldor 1987):
    ``s² = Σn(r − b)²/Σn``, ``a = max(0, s² − b/n̄)``, ``EB = b + a/(a + b/n)·(r − b)``. LOCAL_EMPIRICAL_BAYES does the
    same within each feature's neighbourhood, the feature included. LOCALLY_WEIGHTED_AVERAGE is the spatial rate
    smoother Σy/Σn over the neighbourhood; LOCALLY_WEIGHTED_MEDIAN the population-weighted median of its crude rates
    (Kafadar 1996), by Neighborhood Summary Statistics' weighted quantile. Every rate is multiplied by rate_multiplier.

    A count or population that is null or negative leaves the feature out of every estimate; its own rate is null,
    except that the two locally weighted methods fill it from its neighbours (FILLED_ = 1). Neighbourhoods as the
    Modeling Spatial Relationships tools build them, DISTANCE_BAND the default for points and lines and
    CONTIGUITY_EDGES_CORNERS for polygons; a table takes only CRUDE_RATE and GLOBAL_EMPIRICAL_BAYES, as documented.

    Refused: BINOMIAL for the empirical Bayes methods (Marshall's estimator is for Poisson counts); BISQUARE and
    GAUSSIAN (how a kernel weights a rate is not documented); GET_SPATIAL_WEIGHTS_FROM_FILE.

    INFERRED: the new fields are RATE_<count field>, and for the local methods NBRS_<count field> (the neighbourhood's
    features with valid values, the feature included) and, for the locally weighted ones, FILLED_<count field>;
    number_of_neighbors is 8 when not given; the default band is the shortest that gives every feature a neighbour.
    """
    tool = "CalculateRates"
    pairs = [(row[0], row[1]) for row in _pairs(rate_fields, 2)]
    if not pairs or any(not _given(c) or not _given(p) for c, p in pairs):
        raise ValueError(f"arcpy.stats.{tool}: rate_fields needs a count field and a population field in each row.")
    if not _given(rate_multiplier):
        raise ValueError(f"arcpy.stats.{tool}: rate_multiplier is required.")
    multiplier = float(rate_multiplier)
    method = _keyword(rate_method) if _given(rate_method) else "CRUDE_RATE"
    if method not in _RATE_METHODS:
        raise ValueError(f"arcpy.stats.{tool}: rate_method '{rate_method}' is not one of {', '.join(_RATE_METHODS)}.")
    distribution = _keyword(probability_distribution) if _given(probability_distribution) else "POISSON"
    if distribution not in ("POISSON", "BINOMIAL"):
        raise ValueError(f"arcpy.stats.{tool}: probability_distribution '{probability_distribution}' is neither POISSON "
                         "nor BINOMIAL.")
    if distribution == "BINOMIAL" and "EMPIRICAL_BAYES" in method:
        _refuse(tool, "probability_distribution", "BINOMIAL", "Marshall's empirical Bayes estimator is for Poisson "
                "counts, and the binomial one the tool uses is not documented")
    local = method not in ("CRUDE_RATE", "GLOBAL_EMPIRICAL_BAYES")
    scheme = _keyword(local_weighting_scheme) if _given(local_weighting_scheme) else "UNWEIGHTED"
    if local and scheme != "UNWEIGHTED":
        _refuse(tool, "local_weighting_scheme", scheme, "how a kernel weights a neighbourhood's rate is not documented")
    fields = [f for pair in pairs for f in pair]
    columns, kind, srid, _ = _schema(in_table)
    if local:
        if kind == "Geometry" and srid == 0:
            raise ValueError(f"arcpy.stats.{tool}: {method} needs features; a table takes CRUDE_RATE or "
                             "GLOBAL_EMPIRICAL_BAYES.")
        records, _, kind, srid = _features(tool, in_table, fields)
        kind_of = _keyword(neighborhood_type) if _given(neighborhood_type) else (
            "CONTIGUITY_EDGES_CORNERS" if kind == "Polygon" else "DISTANCE_BAND")
        k = int(number_of_neighbors) if _given(number_of_neighbors) else 8
        neighbours, _, _ = _neighbourhoods(tool, records, kind, srid, kind_of, distance_band, k)
        records = [(f[0], f[1]) for f in records]
    else:
        records, _, _, _ = _rows_with_oids(tool, in_table, fields)

    def valid(record, count, population):
        y, n = record[1][count.lower()], record[1][population.lower()]
        return y is not None and n is not None and y >= 0 and n >= 0

    new_columns, results = [], {oid: [] for oid, _ in records}
    for count, population in pairs:
        new_columns.append(_gpkg.Column(f"RATE_{count}", "Double"))
        if local:
            new_columns.append(_gpkg.Column(f"NBRS_{count}", "Integer"))
        if method in ("LOCALLY_WEIGHTED_AVERAGE", "LOCALLY_WEIGHTED_MEDIAN"):
            new_columns.append(_gpkg.Column(f"FILLED_{count}", "Integer"))
        ok = [valid(r, count, population) for r in records]
        ys = [float(r[1][count.lower()]) if ok[i] else None for i, r in enumerate(records)]
        ns = [float(r[1][population.lower()]) if ok[i] else None for i, r in enumerate(records)]
        if method == "GLOBAL_EMPIRICAL_BAYES":
            usable = [i for i in range(len(records)) if ok[i] and ns[i] > 0]
            b, a = _empirical_bayes([ys[i] for i in usable], [ns[i] for i in usable]) if usable else (None, None)
        for i, (oid, _) in enumerate(records):
            members = [i, *neighbours[i]] if local else []
            usable = [j for j in members if ok[j] and ns[j] > 0]
            rate = ys[i] / ns[i] if ok[i] and ns[i] > 0 else None
            row = []
            if method == "CRUDE_RATE":
                row.append(rate * multiplier if rate is not None else None)
            elif method == "GLOBAL_EMPIRICAL_BAYES":
                if rate is None or b is None:
                    row.append(None)
                else:
                    shrink = a / (a + b / ns[i]) if a + b / ns[i] > 0 else 0.0
                    row.append((b + shrink * (rate - b)) * multiplier)
            elif method == "LOCAL_EMPIRICAL_BAYES":
                if rate is None or not usable:
                    row.append(None)
                else:
                    bi, ai = _empirical_bayes([ys[j] for j in usable], [ns[j] for j in usable])
                    shrink = ai / (ai + bi / ns[i]) if ai + bi / ns[i] > 0 else 0.0
                    row.append((bi + shrink * (rate - bi)) * multiplier)
                row.append(len([j for j in members if ok[j]]))
            else:
                if not usable or sum(ns[j] for j in usable) == 0:
                    row.append(None)
                elif method == "LOCALLY_WEIGHTED_AVERAGE":
                    row.append(sum(ys[j] for j in usable) / sum(ns[j] for j in usable) * multiplier)
                else:
                    order = sorted(usable, key=lambda j: ys[j] / ns[j])
                    total = sum(ns[j] for j in order)
                    row.append(_quantile([ys[j] / ns[j] for j in order], [ns[j] / total for j in order], 0.5)
                               * multiplier)
                row.append(len([j for j in members if ok[j]]))
                row.append(0 if ok[i] else 1)
            results[oid].extend(row)
    target = _appended(tool, in_table, _wants_append(append_to_input), out_table, new_columns, results)
    return Result(target)


# ---- Mapping Clusters: Calculate Composite Index ------------------------------------------------------------------

_PRESETS = {"MEAN_SCALED": ("MINMAX", "MEAN"), "MEAN_PCTL": ("PERCENTILE", "MEAN"),
            "GEOMEAN_SCALED": ("MINMAX", "GEOMETRIC_MEAN"), "SUM_FLAGSPCTL": ("BINARY", "SUM")}


def _percentiles(values):
    """The documented percentile ``(R − 1)/(N − 1)``, R the minimum rank among ties."""
    ordered = sorted(values)
    first = {}
    for position, value in enumerate(ordered, start=1):
        first.setdefault(value, position)
    n = len(values)
    return [(first[v] - 1) / (n - 1) if n > 1 else 0.0 for v in values]


def _ranks(values):
    """Ranks from 1, ties given the mean of their ranks."""
    ordered = sorted(values)
    positions = {}
    for position, value in enumerate(ordered, start=1):
        positions.setdefault(value, []).append(position)
    return [sum(positions[v]) / len(positions[v]) for v in values]


def _scaled(tool, method, values, field, custom_min_max, custom_z):
    import statistics
    if method in ("MINMAX", "THRESHOLD_MINMAX"):
        low, high = min(values), max(values)
        if high == low:
            raise ValueError(f"arcpy.stats.{tool}: '{field}' is constant and cannot be scaled between 0 and 1.")
        return [(v - low) / (high - low) for v in values]
    if method in ("CUST_MINMAX", "THRESHOLD_CUST_MINMAX"):
        if field.lower() not in custom_min_max:
            raise ValueError(f"arcpy.stats.{tool}: pre_min_max has no row for '{field}'.")
        low, high = custom_min_max[field.lower()]
        return [(v - low) / (high - low) for v in values]
    if method in ("PERCENTILE", "THRESHOLD_PERCENTILE"):
        return _percentiles(values)
    if method == "RANK":
        return _ranks(values)
    if method in ("ZSCORE", "THRESHOLD_ZSCORE"):
        mean, deviation = statistics.fmean(values), statistics.pstdev(values)
        if deviation == 0:
            raise ValueError(f"arcpy.stats.{tool}: '{field}' is constant and has no z-score.")
        return [(v - mean) / deviation for v in values]
    if method in ("CUST_ZSCORE", "THRESHOLD_CUST_ZSCORE"):
        if field.lower() not in custom_z:
            raise ValueError(f"arcpy.stats.{tool}: pre_custom_zscore has no row for '{field}'.")
        mean, deviation = custom_z[field.lower()]
        return [(v - mean) / deviation for v in values]
    if method in ("RAW", "THRESHOLD_RAW"):
        return list(values)
    raise ValueError(f"arcpy.stats.{tool}: '{method}' is not a scaling method the tool takes.")


@_equivalent("the documented scaling, combination and post-processing steps, with the OECD handbook's weighted "
             "arithmetic and geometric aggregation", "Esri's \"How Calculate Composite Index works\"; OECD/JRC, "
             "Handbook on Constructing Composite Indicators, 2008, §6")
def CalculateCompositeIndex(in_table, in_variables, append_to_input="NEW_FEATURES", out_table=None,
                            index_preset="MEAN_SCALED", preprocessing=None, pre_threshold_scaling="THRESHOLD_RAW",
                            pre_custom_zscore=None, pre_min_max=None, pre_thresholds=None, index_method=None,
                            index_weights=None, out_index_name=None, out_index_reverse="NO_REVERSE", post_min_max=None,
                            post_reclass=None, post_num_classes=None, post_custom_classes=None):
    """EQUIVALENT where the page stops: the steps are the ones "How Calculate Composite Index works" states — each
    variable scaled (MINMAX ``(x − min)/(max − min)``, CUST_MINMAX, PERCENTILE ``(R − 1)/(N − 1)`` with the minimum rank
    among ties, RANK with mean ranks for ties, ZSCORE, CUST_ZSCORE, BINARY flags ``≥`` (GREATERTHAN) or ``<`` (LESSTHAN)
    a threshold after the threshold scaling, RAW); a reversed variable becomes ``max + min − x`` of its scaled values,
    reversed after scaling; then combined. The combinations are the OECD handbook's weighted ones, since the page gives
    only that "weights are applied by multiplying" (additive) or "by raising each variable to the power" (multiplicative):
    SUM ``Σwx``, MEAN ``Σwx/Σw``, PRODUCT ``Πx^w``, GEOMETRIC_MEAN ``(Πx^w)^(1/Σw)``. The index is then reversed
    (``max + min − x``) if asked, rescaled to post_min_max by the documented formula, and classified.

    Presets: MEAN_SCALED (MINMAX, MEAN), MEAN_PCTL (PERCENTILE, MEAN), GEOMEAN_SCALED (MINMAX, GEOMETRIC_MEAN),
    SUM_FLAGSPCTL (BINARY at the 90th percentile, GREATERTHAN, SUM); CUSTOM takes preprocessing and index_method.
    Classes: EQINTERVAL and CUST. Refused: QUANTILE and STDDEV (how a value on a class edge is placed is not documented).
    A record with a null in any variable gets a null index and is left out of the scaling.

    INFERRED: the new fields are <field>_SCALED for each variable, COMP_INDEX, COMP_PCTL (the index's percentile, which
    the page's percentile layer shows), COMP_EQINT and COMP_CUST; ZSCORE divides by the population standard deviation;
    an equal interval class is ``min(k, ⌊(x − min)/((max − min)/k)⌋ + 1)`` with k = 5 when not given; a custom class is
    the value of the first upper bound at or above x, null above the last.
    """
    import numpy as np
    tool = "CalculateCompositeIndex"
    variables = []
    for row in _pairs(in_variables, 2):
        variables.append((row[0], _keyword(row[1]) in ("REVERSE", "TRUE") if row[1] is not None else False))
    if not variables:
        raise ValueError(f"arcpy.stats.{tool}: in_variables names no field.")
    preset = _keyword(index_preset) if _given(index_preset) else "MEAN_SCALED"
    if preset == "CUSTOM":
        scaling = _keyword(preprocessing) if _given(preprocessing) else "MINMAX"
        combine = _keyword(index_method) if _given(index_method) else "MEAN"
        thresholds = {r[0].lower(): (_keyword(r[1] or "GREATERTHAN"), float(r[2])) for r in _pairs(pre_thresholds, 3)}
        threshold_scaling = _keyword(pre_threshold_scaling) if _given(pre_threshold_scaling) else "THRESHOLD_RAW"
    elif preset in _PRESETS:
        scaling, combine = _PRESETS[preset]
        threshold_scaling = "THRESHOLD_PERCENTILE"
        thresholds = {f.lower(): ("GREATERTHAN", 0.9) for f, _ in variables}
    else:
        raise ValueError(f"arcpy.stats.{tool}: index_preset '{index_preset}' is not one the tool takes.")
    if combine not in ("SUM", "MEAN", "PRODUCT", "GEOMETRIC_MEAN"):
        raise ValueError(f"arcpy.stats.{tool}: index_method '{index_method}' is not one of SUM, MEAN, PRODUCT, "
                         "GEOMETRIC_MEAN.")
    multiplicative = combine in ("PRODUCT", "GEOMETRIC_MEAN")
    if multiplicative and (scaling in ("ZSCORE", "CUST_ZSCORE")):
        raise ValueError(f"arcpy.stats.{tool}: z-scores cannot be multiplied or combined by a geometric mean.")
    custom_min_max = {r[0].lower(): (float(r[1]), float(r[2])) for r in _pairs(pre_min_max, 3)}
    custom_z = {r[0].lower(): (float(r[1]), float(r[2])) for r in _pairs(pre_custom_zscore, 3)}
    weights = {f.lower(): 1.0 for f, _ in variables}
    for row in _pairs(index_weights, 2):
        weights[row[0].lower()] = float(row[1])
    classes = [_keyword(c) for c in _list(post_reclass) if _given(c)]
    for c in classes:
        if c in ("QUANTILE", "STDDEV"):
            _refuse(tool, "post_reclass", c, "how a value on a class edge is placed is not documented")
        if c not in ("EQINTERVAL", "CUST"):
            raise ValueError(f"arcpy.stats.{tool}: post_reclass '{c}' is not one of EQINTERVAL, QUANTILE, STDDEV, CUST.")
    records, columns, _, _ = _rows_with_oids(tool, in_table, [f for f, _ in variables])
    by_name = {c.name.lower(): c.name for c in columns}
    complete = [r for r in records if all(r[1][f.lower()] is not None for f, _ in variables)]
    if len(complete) < 2:
        raise ValueError(f"arcpy.stats.{tool} needs two records with every variable present.")
    scaled_by_field = {}
    for field, reverse in variables:
        values = [float(r[1][field.lower()]) for r in complete]
        if scaling == "BINARY":
            if field.lower() not in thresholds:
                raise ValueError(f"arcpy.stats.{tool}: pre_thresholds has no row for '{field}'.")
            direction, threshold = thresholds[field.lower()]
            base = _scaled(tool, threshold_scaling, values, field, custom_min_max, custom_z)
            scaled = [1.0 if (v >= threshold if direction == "GREATERTHAN" else v < threshold) else 0.0 for v in base]
        else:
            scaled = _scaled(tool, scaling, values, field, custom_min_max, custom_z)
        if reverse:
            low, high = min(scaled), max(scaled)
            scaled = [high + low - v for v in scaled]
        if multiplicative and min(scaled) < 0:
            raise ValueError(f"arcpy.stats.{tool}: {combine} needs every scaled value at or above zero.")
        scaled_by_field[field.lower()] = scaled
    index = []
    for i in range(len(complete)):
        parts = [(scaled_by_field[f.lower()][i], weights[f.lower()]) for f, _ in variables]
        total_weight = sum(w for _, w in parts)
        if combine == "SUM":
            index.append(sum(w * v for v, w in parts))
        elif combine == "MEAN":
            index.append(sum(w * v for v, w in parts) / total_weight)
        else:
            product = float(np.prod([v ** w for v, w in parts]))
            index.append(product if combine == "PRODUCT" else product ** (1 / total_weight))
    if _keyword(out_index_reverse) in ("REVERSE", "TRUE") or out_index_reverse is True:
        low, high = min(index), max(index)
        index = [high + low - v for v in index]
    bounds = _pairs(post_min_max, 2)
    if bounds:
        a, b = float(bounds[0][0]), float(bounds[0][1])
        low, high = min(index), max(index)
        index = [a + (v - low) * (b - a) / (high - low) if high > low else a for v in index]
    percentile = _percentiles(index)
    new_columns = [_gpkg.Column(f"{by_name[f.lower()]}_SCALED", "Double") for f, _ in variables]
    new_columns += [_gpkg.Column("COMP_INDEX", "Double"), _gpkg.Column("COMP_PCTL", "Double")]
    k = int(post_num_classes) if _given(post_num_classes) else 5
    custom = sorted(((float(r[0]), r[1]) for r in _pairs(post_custom_classes, 2)), key=lambda r: r[0])
    if "EQINTERVAL" in classes:
        new_columns.append(_gpkg.Column("COMP_EQINT", "Integer"))
    if "CUST" in classes:
        if not custom:
            raise ValueError(f"arcpy.stats.{tool}: CUST needs post_custom_classes.")
        numeric = all(isinstance(v, (int, float)) or str(v).lstrip("-").replace(".", "", 1).isdigit() for _, v in custom)
        new_columns.append(_gpkg.Column("COMP_CUST", "Double" if numeric else "String"))
    low, high = min(index), max(index)
    results = {r[0]: [None] * len(new_columns) for r in records}
    for i, record in enumerate(complete):
        row = [scaled_by_field[f.lower()][i] for f, _ in variables] + [index[i], percentile[i]]
        if "EQINTERVAL" in classes:
            row.append(min(k, int((index[i] - low) // ((high - low) / k)) + 1) if high > low else 1)
        if "CUST" in classes:
            value = next((v for upper, v in custom if index[i] <= upper), None)
            row.append(float(value) if value is not None and numeric else value)
        results[record[0]] = row
    target = _appended(tool, in_table, _wants_append(append_to_input), out_table, new_columns, results)
    return Result(target)


# ---- Time Series: Time Series Smoothing ---------------------------------------------------------------------------

_TIME_UNITS = {"SECOND": 1, "MINUTE": 60, "HOUR": 3600, "DAY": 86400, "WEEK": 604800}


def _when(value):
    import datetime
    if value is None or isinstance(value, datetime.datetime):
        return value
    if isinstance(value, datetime.date):
        return datetime.datetime(value.year, value.month, value.day)
    return datetime.datetime.fromisoformat(str(value).replace("Z", ""))


def _shift(moment, amount, unit):
    """``moment`` moved by ``amount`` of ``unit``. Months and years are calendar steps. INFERRED: a day past the end of
    the target month is clamped to its last day (31 January plus one month is 28 or 29 February)."""
    import calendar
    import datetime
    if unit in _TIME_UNITS:
        return moment + datetime.timedelta(seconds=amount * _TIME_UNITS[unit])
    months = int(amount) * (12 if unit == "YEAR" else 1)
    month = moment.month - 1 + months
    year, month = moment.year + month // 12, month % 12 + 1
    return moment.replace(year=year, month=month, day=min(moment.day, calendar.monthrange(year, month)[1]))


@_equivalent("backward, forward and centered moving averages over a closed time window",
             "Esri's \"How Time Series Smoothing works\"; Hyndman and Athanasopoulos, Forecasting: Principles and "
             "Practice, 3rd ed., §3.3")
def TimeSeriesSmoothing(in_features, time_field, analysis_field, group_method="LOCATION", method="BACKWARD",
                        time_window=None, append_to_input="NEW_OUTPUT", output_features=None, id_field=None,
                        apply_shorter_window="NOT_APPLY_SHORTER_WINDOW", enable_time_series_popups=None):
    """EQUIVALENT in its field names: the moving averages are the documented ones — the mean of the non-null values whose
    times lie in ``[t − W, t]`` (BACKWARD), ``[t, t + W]`` (FORWARD) or ``[t − W/2, t + W/2]`` (CENTERED), the borders
    included ("with a time window of four days, five values will be included"). Without a shorter window a record whose
    window reaches before its series' first time or past its last is null; with APPLY_SHORTER_WINDOW the window is cut
    at the ends. Months and years are calendar steps and must be whole numbers, and a centered window in them an even
    one, as documented. Series: LOCATION (features at one centroid), ID_FIELD, or NONE; two values at one time in a
    series are an error, as documented.

    Refused: ADAPTIVE — Friedman's (1984) variable span smoother is named, but grapy has not built it.
    enable_time_series_popups is accepted and does nothing: a server has no pop-ups.

    INFERRED: the new fields are SMOOTH_<analysis field> (Double), NUM_NBRS (the non-null values averaged, the record's
    own included) and TIME_WINDOW (the window as given); a record with no non-null value in its window is null.
    """
    tool = "TimeSeriesSmoothing"
    smoothing = _keyword(method) if _given(method) else "BACKWARD"
    if smoothing == "ADAPTIVE":
        _refuse(tool, "method", "ADAPTIVE", "Friedman's (1984) variable span smoother is named but not built")
    if smoothing not in ("BACKWARD", "FORWARD", "CENTERED"):
        raise ValueError(f"arcpy.stats.{tool}: method '{method}' is not one of BACKWARD, CENTERED, FORWARD, ADAPTIVE.")
    if not _given(time_window):
        raise ValueError(f"arcpy.stats.{tool}: a moving average needs time_window.")
    parts = str(time_window).split()
    amount = float(parts[0])
    unit = (parts[1] if len(parts) > 1 else "DAYS").upper().rstrip("S")
    if unit not in _TIME_UNITS and unit not in ("MONTH", "YEAR"):
        raise ValueError(f"arcpy.stats.{tool}: time_window unit '{parts[1]}' is not seconds, minutes, hours, days, "
                         "weeks, months or years.")
    if amount <= 0:
        raise ValueError(f"arcpy.stats.{tool}: time_window must be positive.")
    if unit in ("MONTH", "YEAR"):
        if amount != int(amount):
            raise ValueError(f"arcpy.stats.{tool}: a window in months or years must be a whole number.")
        if smoothing == "CENTERED" and int(amount) % 2:
            raise ValueError(f"arcpy.stats.{tool}: a centered window in months or years must be an even number.")
    grouping = _keyword(group_method) if _given(group_method) else "LOCATION"
    fields = [time_field, analysis_field] + ([id_field] if grouping == "ID_FIELD" else [])
    if grouping == "ID_FIELD" and not _given(id_field):
        raise ValueError(f"arcpy.stats.{tool}: ID_FIELD needs id_field.")
    if grouping == "LOCATION":
        columns, kind, srid, _ = _schema(in_features)
        if kind == "Geometry" and srid == 0:
            raise ValueError(f"arcpy.stats.{tool}: a table cannot be grouped by LOCATION.")
        features, _, _, _ = _features(tool, in_features, fields)
        records = [(f[0], f[1], (f[2], f[3])) for f in features]
    elif grouping in ("ID_FIELD", "NONE"):
        rows, _, _, _ = _rows_with_oids(tool, in_features, fields)
        records = [(r[0], r[1], r[1][id_field.lower()] if grouping == "ID_FIELD" else None) for r in rows]
    else:
        raise ValueError(f"arcpy.stats.{tool}: group_method '{group_method}' is not LOCATION, ID_FIELD or NONE.")
    shorter = _keyword(apply_shorter_window) in ("APPLY_SHORTER_WINDOW", "TRUE") or apply_shorter_window is True
    series: dict = {}
    for oid, values, key in records:
        moment = _when(values[time_field.lower()])
        if moment is None:
            continue
        series.setdefault(key, []).append((moment, oid, values[analysis_field.lower()]))
    name = f"SMOOTH_{analysis_field}"
    new_columns = [_gpkg.Column(name, "Double"), _gpkg.Column("NUM_NBRS", "Integer"),
                   _gpkg.Column("TIME_WINDOW", "String")]
    results = {oid: [None, None, str(time_window)] for oid, _, _ in records}
    half = amount / 2
    for key, members in series.items():
        members.sort()
        times = [m[0] for m in members]
        if len(set(times)) != len(times):
            raise ValueError(f"arcpy.stats.{tool}: a time series holds two values at one time.")
        first, last = times[0], times[-1]
        for moment, oid, _ in members:
            if smoothing == "BACKWARD":
                start, end = _shift(moment, -amount, unit), moment
            elif smoothing == "FORWARD":
                start, end = moment, _shift(moment, amount, unit)
            else:
                start, end = _shift(moment, -half, unit), _shift(moment, half, unit)
            if not shorter and (start < first or end > last):
                results[oid][:2] = [None, 0]
                continue
            inside = [v for t, _, v in members if start <= t <= end and v is not None]
            results[oid][:2] = [sum(float(v) for v in inside) / len(inside) if inside else None, len(inside)]
    target = _appended(tool, in_features, _wants_append(append_to_input, ("APPEND_TO_INPUT", "TRUE")),
                       output_features, new_columns, results)
    return Result(target)


# ---- Modeling Spatial Relationships: Spatial Autoregression -------------------------------------------------------

def _lm_tests(w, x, y):
    """Anselin's (1988) Lagrange multiplier tests on the OLS residuals and their robust forms (Anselin, Bera, Florax and
    Yoon 1996): with e the residuals, σ² = e'e/n, T = tr(W'W + W²), M = I − X(X'X)⁻¹X' and
    D = (WXβ)'M(WXβ)/σ² + T — LM_err = (e'We/σ²)²/T, LM_lag = (e'Wy/σ²)²/D,
    RLM_lag = (e'Wy/σ² − e'We/σ²)²/(D − T), RLM_err = (e'We/σ² − T/D · e'Wy/σ²)²/(T − T²/D) and
    LM_SARMA = RLM_err + LM_lag, each χ² with one degree of freedom (two for SARMA)."""
    import numpy as np
    from scipy import stats as distributions
    n = len(y)
    beta, fitted, e, inverse = _ols_fit(x, y)
    s2 = float(e @ e) / n
    t = float(np.trace(w.T @ w + w @ w))
    m = np.eye(n) - x @ inverse @ x.T
    wxb = w @ fitted
    d = float(wxb @ m @ wxb) / s2 + t
    err = float(e @ w @ e) / s2
    lag = float(e @ w @ y) / s2
    out = {"LM_ERROR": err ** 2 / t, "LM_LAG": lag ** 2 / d}
    out["ROBUST_LM_LAG"] = (lag - err) ** 2 / (d - t) if d - t > 1e-12 * d else None
    denominator = t - t * t / d
    out["ROBUST_LM_ERROR"] = (err - t / d * lag) ** 2 / denominator if denominator > 1e-12 * t else None
    out["LM_SARMA"] = out["ROBUST_LM_ERROR"] + out["LM_LAG"] if out["ROBUST_LM_ERROR"] is not None else None
    p = {name: (float(distributions.chi2.sf(value, 2 if name == "LM_SARMA" else 1)) if value is not None else None)
         for name, value in out.items()}
    return out, p


def _choose_model(statistic, p, alpha=0.05):
    """The documented choice: OLS when neither LM test is significant; the one that is when only one is; between the
    robust tests likewise when both are; when both robust tests are, SAC if LM SARMA is significant and has the largest
    statistic, otherwise the model whose robust statistic is larger; SAC when neither robust test is significant.
    INFERRED: "the model with the largest test statistic" compares the robust lag, robust error and SARMA statistics."""
    def significant(name):
        return p.get(name) is not None and p[name] < alpha
    lag, error = significant("LM_LAG"), significant("LM_ERROR")
    if not lag and not error:
        return "OLS"
    if lag != error:
        return "LAG" if lag else "ERROR"
    robust_lag, robust_error = significant("ROBUST_LM_LAG"), significant("ROBUST_LM_ERROR")
    if robust_lag != robust_error:
        return "LAG" if robust_lag else "ERROR"
    if not robust_lag:
        return "COMBINED"
    candidates = {"LAG": statistic["ROBUST_LM_LAG"], "ERROR": statistic["ROBUST_LM_ERROR"]}
    if significant("LM_SARMA"):
        candidates["COMBINED"] = statistic["LM_SARMA"]
    return max(candidates, key=candidates.get)


def _gm_lambda(w, u):
    """Kelejian and Prucha's (1999) generalized moments estimate of λ from residuals u: with ū = Wu, ū̄ = W²u, the three
    sample moments of ε = u − λū — ε'ε/n = σ², ε̄'ε̄/n = σ² tr(W'W)/n, ε̄'ε/n = 0 — solved for (λ, σ²) by nonlinear
    least squares, λ kept within (−0.99, 0.99)."""
    import numpy as np
    from scipy.optimize import least_squares
    n = len(u)
    ub = w @ u
    ubb = w @ ub
    trace = float(np.trace(w.T @ w)) / n

    def moments(theta):
        lam, s2 = theta
        e = u - lam * ub
        eb = ub - lam * ubb
        return [e @ e / n - s2, eb @ eb / n - s2 * trace, eb @ e / n]

    start = [0.0, float(u @ u) / n]
    fit = least_squares(moments, start, bounds=([-0.99, 0.0], [0.99, np.inf]), xtol=1e-15, ftol=1e-15, gtol=1e-15)
    return float(fit.x[0]), float(fit.x[1])


def _two_stage(z, h, y):
    """Two-stage least squares of y on z with instruments h: δ = (Ẑ'Z)⁻¹Ẑ'y, Ẑ = H(H'H)⁺H'Z; with White's
    heteroskedasticity-robust covariance (Ẑ'Ẑ)⁻¹Ẑ' diag(e²) Ẑ(Ẑ'Ẑ)⁻¹."""
    import numpy as np
    projected = h @ np.linalg.pinv(h.T @ h) @ h.T @ z
    inverse = np.linalg.inv(projected.T @ z)
    delta = inverse @ projected.T @ y
    e = y - z @ delta
    covariance = inverse @ (projected.T * e ** 2) @ projected @ inverse.T
    return delta, e, covariance


@_equivalent("Anselin's LM tests and the documented model choice; the spatial lag model by spatial two-stage least "
             "squares, the error model by Kelejian and Prucha's generalized moments and feasible GLS, the combined "
             "model by generalized spatial two-stage least squares",
             "Anselin 1988; Anselin, Bera, Florax and Yoon 1996; Kelejian and Prucha 1998, 1999")
def SAR(in_features, dependent_variable, explanatory_variables, out_features, model_type="AUTO",
        neighborhood_type=None, distance_band=None, number_of_neighbors=None, weights_matrix_file=None,
        local_weighting_scheme="UNWEIGHTED", kernel_bandwidth=None):
    """EQUIVALENT: the documentation names the estimators — S2SLS for the lag model, GMM for the error model and
    GS2SLS for the combined one — and the LM tests, but not every step, so they are the literature's homoskedastic
    forms. LAG: y on [X, Wy] with instruments [X, WX, W²X] (Kelejian and Prucha 1998), White-robust standard errors.
    ERROR: λ by Kelejian and Prucha's (1999) generalized moments from the OLS residuals, then β by least squares of
    y − λWy on X − λWX. COMBINED: the lag model's S2SLS, λ by generalized moments from its residuals, then S2SLS of the
    spatially filtered y and [X, Wy] (Kelejian and Prucha 1998's GS2SLS). AUTO chooses by the documented flow chart at
    p < 0.05; the LM statistics are Anselin's (1988) and their robust forms (Anselin et al. 1996).

    Weights: the neighbourhoods of the Modeling Spatial Relationships tools (DISTANCE_BAND, K_NEAREST_NEIGHBORS — 8 by
    default — the contiguities and DELAUNAY_TRIANGULATION, the default for points; CONTIGUITY_EDGES_CORNERS for
    polygons), BISQUARE or GAUSSIAN kernels with the given bandwidth or, adaptively, each feature's farthest neighbour,
    always row standardized; a matrix over 30 percent connected is refused, as documented.

    Not done, and so refused or left out: the Anselin–Kelejian test and HAC standard errors, the spatial pseudo
    R-squared, and GET_SPATIAL_WEIGHTS_FROM_FILE. Coefficients, standard errors, λ, ρ and the LM tests are written as
    messages. INFERRED: the output fields are the dependent and explanatory variables, PREDICTED, RESIDUAL, STD_RESID
    (the residual over its standard deviation), LAG_RESID (W times STD_RESID) and NUM_NBRS; PREDICTED is Xβ + ρWy for
    the lag models and Xβ for the error model; features with a null variable are left out.
    """
    import numpy as np
    from ._runtime import AddMessage
    tool = "SAR"
    explanatory = [e for e in _list(explanatory_variables) if _given(e)]
    kept, y, x, xy, columns, kind, srid = _regression_data(tool, in_features, dependent_variable, explanatory)
    n = len(kept)
    if kind not in ("Point", "Polygon"):
        raise ValueError(f"arcpy.stats.{tool} takes point and polygon features only.")
    kind_of = _keyword(neighborhood_type) if _given(neighborhood_type) else (
        "CONTIGUITY_EDGES_CORNERS" if kind == "Polygon" else "DELAUNAY_TRIANGULATION")
    k = int(number_of_neighbors) if _given(number_of_neighbors) else 8
    neighbours, d, band = _neighbourhoods(tool, kept, kind, srid, kind_of, distance_band, k)
    scheme = _keyword(local_weighting_scheme) if _given(local_weighting_scheme) else "UNWEIGHTED"
    if scheme not in ("UNWEIGHTED", "BISQUARE", "GAUSSIAN"):
        raise ValueError(f"arcpy.stats.{tool}: local_weighting_scheme '{local_weighting_scheme}' is not one the tool "
                         "takes.")
    w = np.zeros((n, n))
    for i in range(n):
        if not neighbours[i]:
            raise ValueError(f"arcpy.stats.{tool}: a feature has no neighbours.")
        b = _threshold(kernel_bandwidth, srid) if _given(kernel_bandwidth) else max(d[i, j] for j in neighbours[i])
        if scheme == "BISQUARE" and any(d[i, j] > b for j in neighbours[i]):
            _refuse(tool, "kernel_bandwidth", kernel_bandwidth, "a neighbour lies beyond it, where the bisquare formula "
                    "as written turns positive again")
        for j in neighbours[i]:
            w[i, j] = _kernel(scheme, d[i, j], b)
    if (w > 0).sum() / (n * n) >= 0.30:
        raise ValueError(f"arcpy.stats.{tool}: the spatial weights are 30 percent connected or more, which biases the "
                         "estimates (Smith 2009); choose fewer neighbours.")
    w = _standardized(w, "ROW")
    statistic, p = _lm_tests(w, x, y)
    for name in ("LM_LAG", "LM_ERROR", "ROBUST_LM_LAG", "ROBUST_LM_ERROR", "LM_SARMA"):
        AddMessage(f"{name}: statistic {statistic[name]}, p-value {p[name]}")
    wanted = _keyword(model_type) if _given(model_type) else "AUTO"
    if wanted not in ("AUTO", "ERROR", "LAG", "COMBINED"):
        raise ValueError(f"arcpy.stats.{tool}: model_type '{model_type}' is not AUTO, ERROR, LAG or COMBINED.")
    chosen = _choose_model(statistic, p) if wanted == "AUTO" else wanted
    AddMessage(f"Model: {chosen}")
    names = ["Intercept", *explanatory]
    wy = w @ y
    instruments = np.column_stack([x, w @ x[:, 1:], w @ w @ x[:, 1:]])
    rho = lam = None
    if chosen == "OLS":
        beta, fitted, residual, inverse = _ols_fit(x, y)
        se = np.sqrt(np.diag(inverse) * float(residual @ residual) / (n - x.shape[1]))
        predicted = fitted
    elif chosen == "LAG":
        z = np.column_stack([x, wy])
        delta, residual, covariance = _two_stage(z, instruments, y)
        beta, rho, se = delta[:-1], float(delta[-1]), np.sqrt(np.diag(covariance))
        predicted = z @ delta
    elif chosen == "ERROR":
        _, _, u, _ = _ols_fit(x, y)
        lam, _ = _gm_lambda(w, u)
        xs, ys = x - lam * (w @ x), y - lam * wy
        beta, _, filtered, inverse = _ols_fit(xs, ys)
        se = np.sqrt(np.diag(inverse) * float(filtered @ filtered) / n)
        predicted = x @ beta
        residual = y - predicted
    else:
        z = np.column_stack([x, wy])
        first, u, _ = _two_stage(z, instruments, y)
        lam, _ = _gm_lambda(w, u)
        delta, _, covariance = _two_stage(z - lam * (w @ z), instruments, y - lam * wy)
        beta, rho, se = delta[:-1], float(delta[-1]), np.sqrt(np.diag(covariance))
        predicted = z @ delta
        residual = y - predicted
    for i, name in enumerate(names):
        AddMessage(f"Coefficient {name}: {float(beta[i])}, standard error {float(se[i])}")
    if rho is not None:
        AddMessage(f"Lag Y (rho): {rho}, standard error {float(se[-1])}")
        for i, name in enumerate(names[1:], start=1):
            total = float(beta[i]) / (1 - rho)
            AddMessage(f"Impacts {name}: direct {float(beta[i])}, indirect {total - float(beta[i])}, total {total}")
    if lam is not None:
        AddMessage(f"Lag Residual (lambda): {lam}")
    spread = float(np.std(residual))
    standardized = residual / spread if spread > 0 else np.zeros(n)
    lagged = w @ standardized
    by_name = {c.name.lower(): c for c in columns}
    out_columns = [by_name[dependent_variable.lower()], *[by_name[e.lower()] for e in explanatory],
                   _gpkg.Column("PREDICTED", "Double"), _gpkg.Column("RESIDUAL", "Double"),
                   _gpkg.Column("STD_RESID", "Double"), _gpkg.Column("LAG_RESID", "Double"),
                   _gpkg.Column("NUM_NBRS", "Integer")]
    out = _out(out_features)
    _gpkg.create(out, kind, srid, out_columns)
    with da.InsertCursor(out, [*[c.name for c in out_columns], "SHAPE@"]) as rows:
        for i, f in enumerate(kept):
            rows.insertRow((f[1][dependent_variable.lower()], *[f[1][e.lower()] for e in explanatory],
                            float(predicted[i]), float(residual[i]), float(standardized[i]), float(lagged[i]),
                            len(neighbours[i]), f[4]))
    return Result(out)


# ---- Utilities: Dimension Reduction -------------------------------------------------------------------------------

@_equivalent("principal component analysis by the eigendecomposition of the sample covariance (or correlation) matrix",
             "James, Witten, Hastie and Tibshirani, An Introduction to Statistical Learning, 2014, §10.2; Jolliffe, "
             "Principal Component Analysis, 2nd ed., 2002")
def DimensionReduction(in_table, output_data=None, fields=None, method="PCA", scale="SCALE_DATA", categorical_field=None,
                       min_variance=None, min_components=None, append_fields="NO_APPEND", output_eigenvalues_table=None,
                       output_eigenvectors_table=None, number_of_permutations=0, append_to_input="NEW_OUTPUT"):
    """EQUIVALENT: "How Dimension Reduction works" names PCA and refers to James et al. (2014) for it; its exact output
    is not given, so this is the textbook's. Each field is centred and, with SCALE_DATA (the default), divided by its
    standard deviation; the components are the eigenvectors of the resulting covariance matrix, in descending order of
    eigenvalue, each of unit norm, and a record's component value is its centred (scaled) values times the eigenvector.
    The percent of variance of component i is ``100·d_i/Σd``. The number of components is, as documented, the smallest
    that reaches min_components, or min_variance percent, or the larger of the two when both are given.

    Refused: LDA (the scaling of its eigenvalues, from which its percent variances are read, is not given); and neither
    min_variance nor min_components (the tool then takes the largest of several stopping rules whose combination is
    named but not specified), with which number_of_permutations does nothing.

    INFERRED: the standard deviation and covariance divide by n − 1; an eigenvector's sign makes its largest-magnitude
    loading positive; the components are PC_1, PC_2, …; the output holds the analysis fields (all fields with APPEND)
    and the components; the eigenvalues table has COMPONENT, EIGENVALUE, PCT_VAR and CUM_VAR, and the eigenvectors
    table FIELD and one column per component; records with a null analysis value get null components and are left out
    of the fit.
    """
    import numpy as np
    tool = "DimensionReduction"
    if _keyword(method or "PCA") == "LDA":
        _refuse(tool, "method", "LDA", "the scaling of its eigenvalues, from which its percent variances are read, is not "
                "given")
    if _keyword(method or "PCA") != "PCA":
        raise ValueError(f"arcpy.stats.{tool}: method '{method}' is neither PCA nor LDA.")
    if not _given(min_variance) and not _given(min_components):
        _refuse(tool, "min_components", "(empty)", "with neither minimum the tool combines stopping rules whose "
                "combination is named but not specified; give min_components or min_variance")
    names = [f for f in _list(fields) if _given(f)]
    if len(names) < 2:
        raise ValueError(f"arcpy.stats.{tool}: fields needs two analysis fields or more.")
    records, columns, kind, srid = _rows_with_oids(tool, in_table, names)
    by_name = {c.name.lower(): c for c in columns}
    complete = [r for r in records if all(r[1][f.lower()] is not None for f in names)]
    if len(complete) <= len(names):
        raise ValueError(f"arcpy.stats.{tool} needs more complete records than fields.")
    data = np.array([[float(r[1][f.lower()]) for f in names] for r in complete])
    centred = data - data.mean(axis=0)
    if _keyword(scale or "SCALE_DATA") in ("SCALE_DATA", "TRUE") or scale is True:
        deviation = data.std(axis=0, ddof=1)
        if (deviation == 0).any():
            raise ValueError(f"arcpy.stats.{tool}: a constant field cannot be scaled.")
        centred = centred / deviation
    covariance = centred.T @ centred / (len(complete) - 1)
    values, vectors = np.linalg.eigh(covariance)
    order = np.argsort(values)[::-1]
    values, vectors = np.clip(values[order], 0, None), vectors[:, order]
    for c in range(vectors.shape[1]):
        if vectors[np.argmax(np.abs(vectors[:, c])), c] < 0:
            vectors[:, c] = -vectors[:, c]
    percent = 100 * values / values.sum()
    cumulative = np.cumsum(percent)
    wanted = int(min_components) if _given(min_components) else 1
    if _given(min_variance):
        reach = int(np.searchsorted(cumulative, float(min_variance) - 1e-9) + 1)
        wanted = max(wanted, min(reach, len(names)))
    if not 1 <= wanted <= len(names):
        raise ValueError(f"arcpy.stats.{tool}: min_components must be between 1 and {len(names)}.")
    scores = centred @ vectors[:, :wanted]
    component_columns = [_gpkg.Column(f"PC_{c + 1}", "Double") for c in range(wanted)]
    position = {r[0]: i for i, r in enumerate(complete)}
    results = {r[0]: ([float(v) for v in scores[position[r[0]]]] if r[0] in position else [None] * wanted)
               for r in records}
    append = _wants_append(append_to_input, ("APPEND_TO_INPUT", "TRUE"))
    if append:
        target = _appended(tool, in_table, True, None, component_columns, results)
    else:
        if not _given(output_data):
            raise ValueError(f"arcpy.stats.{tool}: output_data is needed when the components are not appended.")
        everything = _keyword(append_fields) in ("APPEND", "TRUE") or append_fields is True
        kept = list(columns) if everything else [by_name[f.lower()] for f in names]
        target = _out(output_data)
        shape_kind, shape_srid = (kind, srid) if not (kind == "Geometry" and srid == 0) else ("Geometry", 0)
        _gpkg.create(target, shape_kind, shape_srid, [*kept, *component_columns])
        kept_names = [c.name for c in kept]
        with da.SearchCursor(in_table, ["OID@", *kept_names, "SHAPE@"]) as rows, \
                da.InsertCursor(target, [*kept_names, *[c.name for c in component_columns], "SHAPE@"]) as written:
            for row in rows:
                written.insertRow((*row[1:-1], *results[row[0]], row[-1]))
    outputs = [target]
    if _given(output_eigenvalues_table):
        outputs.append(_write_table(output_eigenvalues_table,
                                    [_gpkg.Column("COMPONENT", "Integer"), _gpkg.Column("EIGENVALUE", "Double"),
                                     _gpkg.Column("PCT_VAR", "Double"), _gpkg.Column("CUM_VAR", "Double")],
                                    [(c + 1, float(values[c]), float(percent[c]), float(cumulative[c]))
                                     for c in range(len(names))]))
    if _given(output_eigenvectors_table):
        outputs.append(_write_table(output_eigenvectors_table,
                                    [_gpkg.Column("FIELD", "String"),
                                     *[_gpkg.Column(f"PC_{c + 1}", "Double") for c in range(len(names))]],
                                    [(by_name[f.lower()].name, *[float(v) for v in vectors[i]])
                                     for i, f in enumerate(names)]))
    return Result(*outputs)


# ---- Spatial Component Utilities: Decompose Spatial Structure ------------------------------------------------------

@_equivalent("Moran eigenvector maps: the eigenvectors of the doubly centred, symmetric spatial weights matrix",
             "Griffith 1996; Dray, Legendre and Peres-Neto 2006")
def DecomposeSpatialStructure(in_features, out_features, append_all_fields="APPEND_ALL", min_autocorrelation=0.25,
                              max_components=15, neighborhood_type=None, distance_band=None, number_of_neighbors=None,
                              weights_matrix_file=None, local_weighting_scheme="UNWEIGHTED", kernel_bandwidth=None,
                              out_swm=None, id_field=None):
    """EQUIVALENT: Esri's page on Moran eigenvectors answers 404, so the components are the literature's Moran eigenvector
    maps (Griffith 1996; Dray, Legendre and Peres-Neto 2006). With W the weights made symmetric, ``(W + W')/2``, and
    ``H = I − 11'/n``, the components are the eigenvectors of ``HWH`` of unit length, and component k's Moran's I is
    ``(n/S₀)·λ_k``, S₀ the sum of the weights. As documented, they are ordered from the largest Moran's I down, and
    included while there are fewer than max_components and the next one's Moran's I is at least min_autocorrelation
    times the first's; a component with a Moran's I of 0 or less is never included. Each one's Moran's I and its
    normal-approximation p-value (as Spatial Autocorrelation computes them) are written as messages.

    Neighbourhoods as the Modeling Spatial Relationships tools build them (DISTANCE_BAND the default for points, at most
    1,000 neighbours; NUMBER_OF_NEIGHBORS, 8 by default; the contiguities, CONTIGUITY_EDGES_CORNERS the default for
    polygons; DELAUNAY_TRIANGULATION), with BISQUARE or GAUSSIAN kernels at kernel_bandwidth or, when not given, each
    feature's farthest neighbour. Refused: GET_SPATIAL_WEIGHTS_FROM_FILE and out_swm (the .swm layout is not published);
    an included component whose eigenvalue is repeated (it is then not unique, and any rotation of the pair would do).

    INFERRED: the weights are not row standardized (Moran eigenvector maps are defined on the symmetric matrix); the
    fields are SPCOMP_1, SPCOMP_2, …; a component's sign makes its first entry that is not zero positive.
    """
    import numpy as np
    from ._runtime import AddMessage
    tool = "DecomposeSpatialStructure"
    if _given(out_swm):
        _refuse(tool, "out_swm", out_swm, "the .swm layout is not published")
    features, columns, kind, srid = _features(tool, in_features)
    n = len(features)
    if n < 3:
        raise ValueError(f"arcpy.stats.{tool} needs three features or more.")
    kind_of = _keyword(neighborhood_type) if _given(neighborhood_type) else (
        "CONTIGUITY_EDGES_CORNERS" if kind == "Polygon" else "DISTANCE_BAND")
    k = int(number_of_neighbors) if _given(number_of_neighbors) else 8
    neighbours, d, _ = _neighbourhoods(tool, features, kind, srid, kind_of, distance_band, k, cap=1000)
    scheme = _keyword(local_weighting_scheme) if _given(local_weighting_scheme) else "UNWEIGHTED"
    if scheme not in ("UNWEIGHTED", "BISQUARE", "GAUSSIAN"):
        raise ValueError(f"arcpy.stats.{tool}: local_weighting_scheme '{local_weighting_scheme}' is not one the tool "
                         "takes.")
    w = np.zeros((n, n))
    for i in range(n):
        if not neighbours[i]:
            continue
        b = _threshold(kernel_bandwidth, srid) if _given(kernel_bandwidth) else max(d[i, j] for j in neighbours[i])
        if scheme == "BISQUARE" and any(d[i, j] > b for j in neighbours[i]):
            _refuse(tool, "kernel_bandwidth", kernel_bandwidth, "a neighbour lies beyond it, where the bisquare formula "
                    "as written turns positive again")
        for j in neighbours[i]:
            w[i, j] = _kernel(scheme, d[i, j], b)
    w = (w + w.T) / 2
    s0 = float(w.sum())
    if s0 <= 0:
        raise ValueError(f"arcpy.stats.{tool}: no feature has a neighbour.")
    centring = np.eye(n) - np.full((n, n), 1 / n)
    values, vectors = np.linalg.eigh(centring @ w @ centring)
    order = np.argsort(values)[::-1]
    values, vectors = values[order], vectors[:, order]
    morans = n / s0 * values
    limit = int(max_components) if _given(max_components) else 15
    share = float(min_autocorrelation) if _given(min_autocorrelation) else 0.25
    if not 0 <= share <= 1:
        raise ValueError(f"arcpy.stats.{tool}: min_autocorrelation must be between 0 and 1.")
    scale = max(1.0, float(np.abs(values).max()))
    kept = []
    for c in range(n):
        if len(kept) >= limit or morans[c] <= 1e-12 * scale or morans[c] < share * morans[0] - 1e-12 * scale:
            break
        kept.append(c)
    for c in kept:
        if any(abs(values[c] - values[o]) <= 1e-9 * scale for o in range(n) if o != c):
            _refuse(tool, "max_components", limit, f"component {c + 1}'s eigenvalue is repeated, so the component is not "
                    "unique")
        first = next(i for i in range(n) if abs(vectors[i, c]) > 1e-9)
        if vectors[first, c] < 0:
            vectors[:, c] = -vectors[:, c]
    for position, c in enumerate(kept, start=1):
        try:
            _, _, _, z = _moran(vectors[:, c], w)
            AddMessage(f"SPCOMP_{position}: Moran's I {float(morans[c])}, p-value {_p(z)}")
        except ValueError:
            AddMessage(f"SPCOMP_{position}: Moran's I {float(morans[c])}")
    keep_all = append_all_fields is True or _keyword(append_all_fields) in ("APPEND_ALL", "TRUE")
    kept_columns = list(columns) if keep_all else []
    new_columns = [_gpkg.Column(f"SPCOMP_{p}", "Double") for p in range(1, len(kept) + 1)]
    out = _out(out_features)
    _gpkg.create(out, kind, srid, [*kept_columns, *new_columns])
    names = [c.name for c in kept_columns]
    position = {f[0]: i for i, f in enumerate(features)}
    with da.SearchCursor(in_features, ["OID@", *names, "SHAPE@"]) as rows, \
            da.InsertCursor(out, [*names, *[c.name for c in new_columns], "SHAPE@"]) as written:
        for row in rows:
            i = position.get(row[0])
            if i is None:
                continue
            written.insertRow((*row[1:-1], *[float(vectors[i, c]) for c in kept], row[-1]))
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


_DELIMITERS = {"SPACE": " ", "COMMA": ",", "SEMI-COLON": ";", "SEMICOLON": ";", "TAB": "\t"}


def ExportXYv(Input_Feature_Class, Value_Field, Delimiter="SPACE", Output_ASCII_File=None,
              Add_Field_Names_to_Output="NO_FIELD_NAMES"):
    """Each feature's x, y and values written as one delimited line of an ASCII text file. As documented: x and y with
    eight significant digits, floating-point values with six, a null as ``NULL``, the delimiter SPACE (the default),
    COMMA, SEMI-COLON or TAB, and the field names as the first line with ADD_FIELD_NAMES.

    The format chosen where the documentation is silent — INFERRED, each of it:

    - x and y are the feature's centroid (a point is itself), as the other Spatial Statistics tools locate a feature;
    - "significant digits" is Python's ``g`` format (``format(v, ".8g")`` and ``".6g"``), which drops trailing zeros
      and writes an exponent below 1e-4 or from 1e8 (x, y) and 1e6 (values) up;
    - the header line is ``XCoord``, ``YCoord`` and the value fields' names as stored;
    - integers are written whole, text as it is (no quotes), a date as the cursor reads it (``YYYY-MM-DD HH:MM:SS``);
    - lines end with a line feed and the file is UTF-8; features without a shape are skipped.
    """
    tool = "ExportXYv"
    fields = [f for f in _list(Value_Field) if _given(f)]
    if not fields:
        raise ValueError(f"arcpy.stats.{tool}: Value_Field names no field.")
    if not _given(Output_ASCII_File):
        raise ValueError(f"arcpy.stats.{tool}: Output_ASCII_File is required.")
    separator = _DELIMITERS.get(_keyword(Delimiter) if _given(Delimiter) else "SPACE")
    if separator is None:
        raise ValueError(f"arcpy.stats.{tool}: Delimiter '{Delimiter}' is not SPACE, COMMA, SEMI-COLON or TAB.")
    header = Add_Field_Names_to_Output is True or _keyword(Add_Field_Names_to_Output) in ("ADD_FIELD_NAMES", "TRUE")
    columns, _, _, _ = _schema(Input_Feature_Class)
    names = {c.name.lower(): c.name for c in columns}
    for field in fields:
        if field.lower() not in names:
            raise ValueError(f"arcpy.stats.{tool}: the input has no field '{field}'.")
    stored = [names[f.lower()] for f in fields]

    def written(value):
        if value is None:
            return "NULL"
        if isinstance(value, bool):
            return str(int(value))
        if isinstance(value, int):
            return str(value)
        if isinstance(value, float):
            return format(value, ".6g")
        return str(value)

    lines = []
    if header:
        lines.append(separator.join(["XCoord", "YCoord", *stored]))
    with da.SearchCursor(Input_Feature_Class, [*stored, "SHAPE@"]) as rows:
        for row in rows:
            shape = row[-1]._shape if row[-1] is not None else None
            if shape is None or shape.is_empty:
                continue
            c = shape if shape.geom_type == "Point" else shape.centroid
            lines.append(separator.join([format(c.x, ".8g"), format(c.y, ".8g"), *[written(v) for v in row[:-1]]]))
    with open(Output_ASCII_File, "w", encoding="utf-8", newline="\n") as out:
        out.write("".join(line + "\n" for line in lines))
    return Result(Output_ASCII_File)


_REFUSED = {
    "GenerateSpatialWeightsMatrix": "its output, an .swm file, has no published layout",
    "GenerateNetworkSpatialWeights": "it needs a network dataset and writes an .swm file",
    "GenerateNetworkSWM": "it needs a network dataset and writes an .swm file",
    "Forest": "random forests are random by definition",
}
_SWM = "it reads or writes spatial weights matrix (.swm) files, whose layout is not published"
_SSM = "it reads or writes a spatial statistics model (.ssm) file, whose layout is not published"
_FIT = "its estimation — the optimisation, its stopping rules and the diagnostics it writes — is named but not " \
       "specified closely enough to reproduce"
_REFUSED.update({  # re-read 2026-10-31 (ADR-244); the other three of this group are built in _statsmore.py
    "AttributeUncertainty": "it reruns the analysis that made its input, which it identifies — tool and parameters — "
                            "from the geoprocessing history ArcGIS Pro writes into the result layer's metadata, and "
                            "grapy's outputs carry none; its pages also leave unstated the spread a margin of error at "
                            "a confidence level gives, the bounds of its uniform and triangular draws and the fields it "
                            "writes",
    "CrossValidate": "it refits the model behind a Forest-based and Boosted, Generalized Linear Regression or "
                     "Presence-only Prediction result with that run's own parameters, which grapy's result features do "
                     "not record, and its page names neither the fields nor the metrics it writes",
    "PresenceOnlyPrediction": "its MaxEnt fit is named but not specified: its pages give neither the regularization "
                              "penalty nor its values, nor the hinge function's linear segment, nor how background "
                              "points are sampled or the entropy term H of its link functions is found",
    "BuildBalancedZones": "its genetic growth algorithm leaves open which neighbour a growing zone adds next and how "
                          "crossover combines two parents (shown only as a diagram), and does not say how equal area "
                          "or compactness is measured",
})
_REFUSED.update({name: _SWM for name in (
    "ConvertSpatialWeightsMatrixtoTable", "CompareNeighborhoodConceptualizations",
    "CreateSpatialComponentExplanatoryVariables", "FilterSpatialAutocorrelationFromField")})
_REFUSED.update({name: _SSM for name in (
    "DescribeSSMFile", "PredictUsingSSMFile", "SetSSMFileProperties", "EvaluateVariableInfluence")})
_REFUSED.update({name: _FIT for name in (
    "CausalInferenceAnalysis", "ExploratoryRegression",
    "EstimateTimeToEvent")})
_REFUSED.update({
    "DirectionalTrend": "its output is a chart on a layer in the active map, and the tool cannot run without one",
    "ConvertSSPopup": "it saves ArcGIS Pro pop-up charts as images, and a run on this server has no Pro charts",
    "GroupingAnalysis": "it was retired for Multivariate Clustering, whose seeds are random",
})


from ._statsmore import (  # noqa: E402,F401 — ADR-244: built with a seeded generator
    LocalBivariateRelationships, PrepareData, SpatiallyConstrainedMultivariateClustering)
from ._binsizes import EvaluateBinSizes  # noqa: E402,F401 — ADR-224: its page found again 2026-10-31
from ._hscompare import HotSpotAnalysisComparison  # noqa: E402,F401 — ADR-224: its page found again 2026-10-31


def CalculateAreas(Input_Feature_Class, Output_Feature_Class):
    """A copy of the polygons with ``F_AREA`` added or overwritten: each polygon's area in square units of the output
    coordinate system, the features projected to ``env.outputCoordinateSystem`` first when it is set, as the tool's page
    documents. Pro's documentation dropped the page; ArcMap's still documents ``arcpy.stats.CalculateAreas`` under this
    name and these parameters (desktop.arcgis.com/en/arcmap/latest/tools/spatial-statistics-toolbox/calculate-areas.htm,
    read 2026-10-31), and calls it deprecated. INFERRED (Q-215): ``F_AREA`` is a double, and in a geographic system the
    area is in square degrees, the page saying only "square units"."""
    from . import management
    from ._reference import reference_of
    from ._runtime import ExecuteError, env
    source, _ = management._source(Input_Feature_Class)
    _, kind, srid, _ = management._schema(source)
    if kind != "Polygon":
        raise ExecuteError("ERROR 000035: Input_Feature_Class must be polygons.")
    target = env.outputCoordinateSystem
    if target is not None and reference_of(target).factoryCode not in (None, 0, srid):
        out = management.Project(Input_Feature_Class, Output_Feature_Class, target).getOutput(0)
    else:
        out = management.CopyFeatures(Input_Feature_Class, Output_Feature_Class).getOutput(0)
    names = {c.name.lower() for c in management._schema(out)[0]}
    if "f_area" not in names:
        management.AddField(out, "F_AREA", "DOUBLE")
    with da.SearchCursor(out, ["OID@", "SHAPE@AREA"]) as rows:
        areas = dict(rows)
    with da.UpdateCursor(out, ["OID@", "F_AREA"]) as rows:
        for oid, _ in rows:
            rows.updateRow([oid, areas.get(oid)])
    return Result(out)


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)

    def missing(*_, **__):
        why = _REFUSED.get(name)
        raise NotBuiltError(f"arcpy.stats.{name} is not built in grapy" + (f" — {why}." if why else " yet."))

    return missing
