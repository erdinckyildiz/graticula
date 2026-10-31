"""arcpy.stats.HotSpotAnalysisComparison: the spatial fuzzy kappa between two hot spot results.

Built from the tool's page (tool-reference/spatial-statistics/hot-spot-comparison.html) and "How Hot Spot Analysis
Comparison works" (how-hot-spot-analysis-comparison-works.html), both read 2026-10-31 at doc.esri.com. The pages give
the method closely: seven Gi_Bin categories compared through a 7 × 7 matrix of category weights, a distance weight
1 − rank/(k + 1) over each feature and its k nearest neighbours, a directional similarity that is the largest product
of the two, the local similarity the smaller of the two directions, an expected similarity from random pairings of
the first result's features with the second's, and κ = (S − E)/(1 − E). The method rests on Hagen-Zanker's fuzzy
kappa (Hagen 2003; Hagen-Zanker, Straatman and Uljee 2005; Hagen-Zanker 2009), which the page cites.

It is an equivalent because the expected similarity is a mean over random draws: grapy draws them with NumPy's PCG64
from ``env.randomGenerator``'s seed (``management._seed``: 0 when unset), so a run repeats itself exactly and its
expected similarity and kappa differ slightly from Pro's, whose stream is not grapy's. The similarity itself is
deterministic and does not depend on the draws.
"""
from __future__ import annotations

import math

from . import _gpkg, da
from ._result import Result
from ._runtime import AddMessage, ExecuteError, NotBuiltError, equivalent

CATEGORIES = (-3, -2, -1, 0, 1, 2, 3)
LABELS = {-3: "Cold 99%", -2: "Cold 95%", -1: "Cold 90%", 0: "Not Significant", 1: "Hot 90%", 2: "Hot 95%",
          3: "Hot 99%"}
# The critical values of the upper one-sided rejection regions at 90, 95 and 99 percent, as the page writes the
# 95/99 example (1.645/2.33 = 0.71). INFERRED: 90 percent is 1.28, which gives the page's 0.55 and 0.78.
_CRITICAL = {1: 1.28, 2: 1.645, 3: 2.33}
_TOOL = "HotSpotAnalysisComparison"


def _keyword(value):
    return str(value).strip().upper().replace(" ", "_")


def _given(value):
    if value is None:
        return False
    if isinstance(value, str):
        return value.strip() not in ("", "#")
    return True


def _fuzzy():
    """The FUZZY matrix: 1 on the diagonal, the ratio of the smaller to the larger critical value between two hot (or
    two cold) levels, and 0 between hot, cold and not significant."""
    w = {}
    for a in CATEGORIES:
        for b in CATEGORIES:
            if a == b:
                w[a, b] = 1.0
            elif a * b > 0:
                ca, cb = _CRITICAL[abs(a)], _CRITICAL[abs(b)]
                w[a, b] = min(ca, cb) / max(ca, cb)
            else:
                w[a, b] = 0.0
    return w


def _grouped(groups):
    """A matrix in which categories of one group are perfectly similar and of different groups dissimilar."""
    of = {c: g for g, members in enumerate(groups) for c in members}
    return {(a, b): 1.0 if of[a] == of[b] else 0.0 for a in CATEGORIES for b in CATEGORIES}


def _rows_to_weights(rows):
    """Weights from (category 1, category 2, weight) rows; a combination not given is 0, as the page says of the table.
    INFERRED: a pair given in one order only is mirrored, the page requiring the weights to be symmetric; a pair given
    in both orders keeps each as given."""
    given = {}
    for row in rows:
        a, b, weight = int(float(row[0])), int(float(row[1])), float(row[2])
        if a not in LABELS or b not in LABELS:
            raise ValueError(f"arcpy.stats.{_TOOL}: a category is a Gi_Bin value from -3 to 3, not {row[0]} or {row[1]}.")
        if not 0.0 <= weight <= 1.0:
            raise ValueError(f"arcpy.stats.{_TOOL}: a similarity weight is between 0 and 1, not {weight}.")
        given[a, b] = weight
    w = {}
    for a in CATEGORIES:
        for b in CATEGORIES:
            w[a, b] = given.get((a, b), given.get((b, a), 0.0))
    return w


def weights(method="FUZZY", similarity_weights=None, in_weights_table=None):
    """The 7 × 7 category weights of a weighting method, as {(category 1, category 2): weight}."""
    kind = _keyword(method) if _given(method) else "FUZZY"
    if kind == "FUZZY":
        return _fuzzy()
    if kind == "EXACT_MATCH":
        return {(a, b): 1.0 if a == b else 0.0 for a in CATEGORIES for b in CATEGORIES}
    if kind == "ABOVE_90":
        return _grouped([(-3, -2, -1), (0,), (1, 2, 3)])
    if kind == "ABOVE_95":
        return _grouped([(-3, -2), (-1, 0, 1), (2, 3)])
    if kind == "ABOVE_99":
        return _grouped([(-3,), (-2, -1, 0, 1, 2), (3,)])
    if kind == "REVERSE":
        fuzzy = _fuzzy()
        return {(a, b): fuzzy[a, -b] for a in CATEGORIES for b in CATEGORIES}
    if kind == "CUSTOM":
        if not _given(similarity_weights):
            raise ValueError(f"arcpy.stats.{_TOOL}: CUSTOM needs similarity_weights.")
        from .management import _pairs
        return _rows_to_weights(_pairs(similarity_weights))
    if kind == "TABLE":
        if not _given(in_weights_table):
            raise ValueError(f"arcpy.stats.{_TOOL}: TABLE needs in_weights_table.")
        from .management import _schema, _source
        path, where = _source(in_weights_table)
        names = {c.name.lower(): c.name for c in _schema(path)[0]}
        missing = [f for f in ("category1", "category2", "weight") if f not in names]
        if missing:
            raise ValueError(f"arcpy.stats.{_TOOL}: the weights table needs CATEGORY1, CATEGORY2 and WEIGHT fields.")
        with da.SearchCursor(path, [names["category1"], names["category2"], names["weight"]], where) as rows:
            return _rows_to_weights([r for r in rows if None not in r])
    raise ValueError(f"arcpy.stats.{_TOOL}: weighting_method is FUZZY, EXACT_MATCH, ABOVE_90, ABOVE_95, ABOVE_99, "
                     "CUSTOM, TABLE or REVERSE.")


def _matrix(w):
    import numpy as np
    m = np.zeros((7, 7))
    for (a, b), value in w.items():
        m[a + 3, b + 3] = value
    return m


def neighbourhoods(xy, k, geographic=False):
    """Each feature followed by its k nearest others, by centroid distance — chordal on the sphere for a geographic
    reference, as the page says — as an (n, k + 1) index array, and the distance weights 1 − rank/(k + 1).
    INFERRED: neighbours at the same distance are taken in feature order."""
    import numpy as np
    xy = np.asarray(xy, dtype=float)
    if geographic:
        lon, lat = np.radians(xy[:, 0]), np.radians(xy[:, 1])
        points = np.column_stack([np.cos(lat) * np.cos(lon), np.cos(lat) * np.sin(lon), np.sin(lat)])
    else:
        points = xy
    d = np.sqrt(((points[:, None, :] - points[None, :, :]) ** 2).sum(axis=2))
    n = len(points)
    np.fill_diagonal(d, -1.0)                   # the feature itself first
    order = np.argsort(d, axis=1, kind="stable")[:, :k + 1]
    distance_weight = 1.0 - np.arange(k + 1) / (k + 1)
    return order.reshape(n, k + 1), distance_weight


def directional(source, target, neighbours_of_target, distance_weight, m):
    """For each i, the largest category weight × distance weight between ``source[i]`` and the categories of
    ``target`` over the neighbourhood ``neighbours_of_target[i]`` (the corresponding feature first)."""
    import numpy as np
    s = np.asarray(source) + 3
    t = np.asarray(target)[neighbours_of_target] + 3
    return (m[s[:, None], t] * distance_weight[None, :]).max(axis=1)


def local_similarity(a, b, neighbours, distance_weight, m):
    """The page's local similarity of each corresponding pair: the smaller of A → B and B → A."""
    import numpy as np
    return np.minimum(directional(a, b, neighbours, distance_weight, m),
                      directional(b, a, neighbours, distance_weight, m))


def expected_similarity(a, b, neighbours, distance_weight, m, included, eligible, permutations, seed):
    """Each included feature's expected similarity: the first result's feature i paired with a random feature r of
    the second, its similarity min(A_i → B around r, B_r → A around i), averaged over ``permutations`` pairings.
    INFERRED: each pairing draws r uniformly, with replacement and independently for every feature, from the eligible
    features (the excluded ones never, as the page says), r = i not being ruled out."""
    import numpy as np
    random = np.random.default_rng(seed)
    a, b = np.asarray(a), np.asarray(b)
    included, eligible = np.asarray(included), np.asarray(eligible)
    total = np.zeros(len(included))
    a_i = a[included] + 3
    around_i = a[neighbours[included]] + 3
    for _ in range(permutations):
        r = random.choice(eligible, size=len(included))
        forward = (m[a_i[:, None], b[neighbours[r]] + 3] * distance_weight[None, :]).max(axis=1)
        backward = (m[(b[r] + 3)[:, None], around_i] * distance_weight[None, :]).max(axis=1)
        total += np.minimum(forward, backward)
    return total / permutations


def _kappa(s, e):
    return None if e is None or s is None or e >= 1.0 else (s - e) / (1.0 - e)


def _read(value, which):
    from .management import _schema, _source
    path, where = _source(value)
    columns, kind, srid, _ = _schema(path)
    names = {c.name.lower(): c.name for c in columns}
    field = names.get("gi_bin")
    if field is None:
        raise ValueError(f"arcpy.stats.{_TOOL}: in_hot_spot_{which} has no Gi_Bin field; give the output of Hot Spot "
                         "Analysis (Getis-Ord Gi*) or Optimized Hot Spot Analysis.")
    out = []
    with da.SearchCursor(path, [field, "SHAPE@"], where) as rows:
        for category, shape in rows:
            if category is None or shape is None or shape._shape.is_empty:
                continue
            if int(category) not in LABELS:
                raise ValueError(f"arcpy.stats.{_TOOL}: Gi_Bin {category} is not a category from -3 to 3.")
            out.append((int(category), shape._shape))
    return out, kind, srid


_DIMENSION = {"Point": 0, "Multipoint": 0, "Polyline": 1, "Polygon": 2}


def _keep(piece, dimension):
    """The parts of an intersection of the given dimension, or None."""
    import shapely
    parts = [g for g in getattr(piece, "geoms", [piece]) if not g.is_empty]
    if dimension == 2:
        parts = [g for g in parts if g.geom_type in ("Polygon", "MultiPolygon") and g.area > 0]
    elif dimension == 1:
        parts = [g for g in parts if g.geom_type in ("LineString", "MultiLineString") and g.length > 0]
    else:
        parts = [g for g in parts if g.geom_type in ("Point", "MultiPoint")]
    if not parts:
        return None
    return parts[0] if len(parts) == 1 else shapely.union_all(parts)


def pairs(first, second, dimension):
    """The corresponding features: each feature of the first result intersected with each of the second it meets, as
    the page says, keeping the pieces of the comparison's dimension. Answers [(category 1, category 2, shape)]."""
    import shapely
    tree = shapely.STRtree([s for _, s in second])
    out = []
    for category, shape in first:
        for j in sorted(int(j) for j in tree.query(shape, predicate="intersects")):
            piece = _keep(shape.intersection(second[j][1]), dimension)
            if piece is not None:
                out.append((category, second[j][0], piece))
    return out


@equivalent("the spatial fuzzy kappa as its pages define it, the expected similarity's random pairings drawn with "
            "NumPy's PCG64 from env.randomGenerator",
            "How Hot Spot Analysis Comparison works; Hagen-Zanker 2009, IJGIS 23:1, 61-73")
def HotSpotAnalysisComparison(in_hot_spot_1, in_hot_spot_2, out_features, num_neighbors=8, num_perms=499,
                              weighting_method="FUZZY", similarity_weights=None, in_weights_table=None,
                              exclude_nonsig_features="NO_EXCLUDE"):
    """EQUIVALENT: compares the Gi_Bin categories of two hot spot results, as documented. The features are paired by
    intersecting the two results; each pair's similarity is the smaller of the two directional similarities, each the
    largest category weight × distance weight 1 − rank/(k + 1) over the corresponding feature and its ``num_neighbors``
    nearest (centroid distance, chordal for a geographic reference); the expected similarity is the mean over
    ``num_perms`` random pairings; κ = (S − E)/(1 − E), locally and globally, the global values being the means of the
    local ones. Weighting methods FUZZY (critical value ratios), EXACT_MATCH, ABOVE_90, ABOVE_95, ABOVE_99, CUSTOM
    ([[category 1, category 2, weight], ...]), TABLE (CATEGORY1, CATEGORY2, WEIGHT) and REVERSE, as documented.
    EXCLUDE leaves out every pair both of whose categories weigh 1 against Not Significant: they get no values and
    are never drawn, but stay neighbours, as documented. At least 20 pairs are required, as documented.

    Writes GI_BIN_1, GI_BIN_2 (long), GI_SIG_1, GI_SIG_2 (text), SIM_VALUE, EXP_SIM, KAPPA (double) and CAT_PAIR
    (text), as documented. Outputs, in order: the output features, SIM_VALUE, EXP_SIM_VALUE and KAPPA.

    INFERRED (the pages are silent): num_neighbors defaults to 8, the value of the page's own code sample; the 90
    percent critical value is 1.28; a pair of weights given in one order only is mirrored; neighbours at equal
    distance are taken in feature order; each random pairing draws its partner uniformly with replacement; a local
    kappa whose expected similarity is 1 is null; CAT_PAIR is "<GI_SIG_1> - <GI_SIG_2>"; when the two results have
    different shape types the pairs have the lower dimension; overlapping features inside one result are not split
    further; a feature with a null Gi_Bin is left out; the messages and charts' tables are written as messages and no
    group layer or charts are made, grapy having neither.
    """
    import numpy as np
    from .management import _seed, _shape_kind
    from .stats import _out
    from ._reference import SpatialReference
    k = int(num_neighbors) if _given(num_neighbors) else 8
    if k < 0:
        raise ValueError(f"arcpy.stats.{_TOOL}: num_neighbors is 0 or more.")
    permutations = int(num_perms) if _given(num_perms) else 499
    if permutations < 1:
        raise ValueError(f"arcpy.stats.{_TOOL}: num_perms is 1 or more.")
    exclude = exclude_nonsig_features is True or (
        isinstance(exclude_nonsig_features, str) and _keyword(exclude_nonsig_features) in ("EXCLUDE", "TRUE"))
    w = weights(weighting_method, similarity_weights, in_weights_table)
    m = _matrix(w)
    first, kind1, srid = _read(in_hot_spot_1, 1)
    second, kind2, srid2 = _read(in_hot_spot_2, 2)
    if srid and srid2 and srid != srid2:
        raise NotBuiltError(f"arcpy.stats.{_TOOL}: two hot spot results in different coordinate systems are not "
                            "intersected by grapy; project one first (management.Project).")
    dimension = min(_DIMENSION.get(kind1, 2), _DIMENSION.get(kind2, 2))
    paired = pairs(first, second, dimension)
    n = len(paired)
    if n < 20:
        raise ExecuteError(f"arcpy.stats.{_TOOL}: the two hot spot results have {n} feature intersections; at least 20 are "
                           "required.")
    a = np.array([p[0] for p in paired])
    b = np.array([p[1] for p in paired])
    xy = [(s.x, s.y) if s.geom_type == "Point" else (s.centroid.x, s.centroid.y) for _, _, s in paired]
    geographic = bool(srid) and srid > 0 and SpatialReference(srid).type == "Geographic"
    neighbours, distance_weight = neighbourhoods(xy, min(k, n - 1), geographic)
    similarity = local_similarity(a, b, neighbours, distance_weight, m)
    quiet1 = {c for c in CATEGORIES if w[c, 0] == 1.0}
    quiet2 = {c for c in CATEGORIES if w[0, c] == 1.0}
    quiet = np.array([x in quiet1 and y in quiet2 for x, y in zip(a, b)])
    excluded = quiet if exclude else np.zeros(n, dtype=bool)
    included = np.flatnonzero(~excluded)
    if not len(included):
        raise ExecuteError(f"arcpy.stats.{_TOOL}: every pair of features is not significant in both results; nothing is left "
                           "to compare.")
    expected = expected_similarity(a, b, neighbours, distance_weight, m, included, included, permutations, _seed())
    local_s = [None] * n
    local_e = [None] * n
    for position, i in enumerate(included):
        local_s[i] = float(similarity[i])
        local_e[i] = float(expected[position])
    global_s = float(np.mean(similarity[included]))
    global_e = float(np.mean(expected))
    global_k = _kappa(global_s, global_e)
    out = _out(out_features)
    columns = [_gpkg.Column("GI_BIN_1", "Integer"), _gpkg.Column("GI_BIN_2", "Integer"),
               _gpkg.Column("GI_SIG_1", "String"), _gpkg.Column("GI_SIG_2", "String"),
               _gpkg.Column("SIM_VALUE", "Double"), _gpkg.Column("EXP_SIM", "Double"),
               _gpkg.Column("KAPPA", "Double"), _gpkg.Column("CAT_PAIR", "String")]
    shape_kind = _shape_kind(paired[0][2]) if dimension else "Point"
    if dimension == 0 and any(p[2].geom_type == "MultiPoint" for p in paired):
        shape_kind = "Multipoint"
    _gpkg.create(out, shape_kind, srid, columns)
    with da.InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as rows:
        for i, (x, y, shape) in enumerate(paired):
            rows.insertRow((int(x), int(y), LABELS[int(x)], LABELS[int(y)], local_s[i], local_e[i],
                            _kappa(local_s[i], local_e[i]), f"{LABELS[int(x)]} - {LABELS[int(y)]}", shape))
    prefix = "Conditional " if exclude else ""
    AddMessage(f"{prefix}Similarity Value: {global_s:.4f}")
    AddMessage(f"{prefix}Expected Similarity Value: {global_e:.4f}")
    AddMessage(f"{prefix}Spatial Fuzzy Kappa: " + ("null" if global_k is None else f"{global_k:.4f}"))
    both_quiet = int(quiet.sum())
    AddMessage(("Number of Excluded Nonsignificant Features: " if exclude else "Number of Nonsignificant Features: ")
               + str(both_quiet))
    AddMessage("Hot Spot Significance Level Pair (Counts), rows the first result, columns the second: " + "; ".join(
        f"{LABELS[x]}: " + ", ".join(str(int(((a == x) & (b == y)).sum())) for y in CATEGORIES) for x in CATEGORIES))
    return Result(out, global_s, global_e, global_k)
