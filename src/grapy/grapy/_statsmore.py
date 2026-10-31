"""arcpy.stats tools whose answers rest on random draws, built with a seeded generator and the published method their
pages name (ADR-224 §5.6, ADR-244): Spatially Constrained Multivariate Clustering, Local Bivariate Relationships and
Prepare Data for Prediction.

Every draw is NumPy's PCG64 from ``env.randomGenerator``'s seed (``management._seed``: 0 when unset), so a run repeats
itself exactly and differs from Pro's, whose Mersenne Twister stream is not grapy's. Each tool is marked an equivalent,
says so once when it runs, and names its method and source; what a page leaves open is written ``INFERRED:`` in the
tool's docstring.
"""
from __future__ import annotations

import math

from . import _gpkg, da
from ._result import Result
from ._runtime import AddWarning, equivalent


def _s():
    from . import stats
    return stats


# ---- Spatially Constrained Multivariate Clustering (SKATER) --------------------------------------------------------

def _kruskal(n, edges):
    """The minimum spanning forest of ``edges`` — (cost, i, j) — by Kruskal's algorithm; ties broken by (i, j), so the
    forest is the same on every run. Answers each node's tree neighbours."""
    parent = list(range(n))

    def root(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    tree = [[] for _ in range(n)]
    for _, i, j in sorted(edges):
        a, b = root(i), root(j)
        if a != b:
            parent[a] = b
            tree[i].append(j)
            tree[j].append(i)
    return tree


def _components(nodes, tree):
    seen, out = set(), []
    for start in sorted(nodes):
        if start in seen:
            continue
        part, stack = [], [start]
        seen.add(start)
        while stack:
            a = stack.pop()
            part.append(a)
            for b in tree[a]:
                if b not in seen and b in nodes:
                    seen.add(b)
                    stack.append(b)
        out.append(sorted(part))
    return out


def _best_cut(data, tree, cluster, size_of, minimum):
    """SKATER's cut of one tree (Assunção et al. 2006, §3): the tree edge whose removal most reduces the sum of squared
    deviations, SSD(T) − SSD(Ta) − SSD(Tb), among the cuts that leave both parts at ``minimum`` size or more.
    Answers (keeps no singleton, reduction, child) — the child the root side of the removed edge loses — or None."""
    members = set(cluster)
    root = cluster[0]
    order, parent = [root], {root: None}
    for a in order:
        for b in tree[a]:
            if b in members and b not in parent:
                parent[b] = a
                order.append(b)
    count = {a: 1 for a in order}
    total = {a: data[a].copy() for a in order}
    squares = {a: float(data[a] @ data[a]) for a in order}
    size = {a: size_of(a) for a in order}
    for a in reversed(order[1:]):
        p = parent[a]
        count[p] += count[a]
        total[p] = total[p] + total[a]
        squares[p] += squares[a]
        size[p] += size[a]

    def ssd(c, s, q):
        return q - float(s @ s) / c if c else 0.0

    whole = ssd(count[root], total[root], squares[root])
    best = None
    for a in order[1:]:
        rest_count = count[root] - count[a]
        if minimum is not None and (size[a] < minimum or size[root] - size[a] < minimum):
            continue
        reduction = whole - ssd(count[a], total[a], squares[a]) - ssd(rest_count, total[root] - total[a],
                                                                     squares[root] - squares[a])
        key = (count[a] > 1 and rest_count > 1, reduction)
        if best is None or key > best[:2]:
            best = (*key, a)
    return best


def _split(tree, cluster, child):
    """The two parts of ``cluster`` when the tree edge above ``child`` is removed."""
    members = set(cluster)
    below, stack = {child}, [child]
    # the root side is the part holding cluster[0]; walk from the child without crossing back to its parent
    parent_side = _path_parent(tree, cluster, child)
    while stack:
        a = stack.pop()
        for b in tree[a]:
            if b in members and b not in below and not (a == child and b == parent_side):
                below.add(b)
                stack.append(b)
    return sorted(members - below), sorted(below)


def _path_parent(tree, cluster, child):
    members, root = set(cluster), cluster[0]
    parent, order = {root: None}, [root]
    for a in order:
        for b in tree[a]:
            if b in members and b not in parent:
                parent[b] = a
                order.append(b)
    return parent[child]


def _skater(data, tree, clusters, size_of, minimum, maximum, wanted, keep_steps=False):
    """Clusters split one edge at a time. With ``maximum``, only clusters above it are split, until none is; otherwise
    until there are ``wanted`` clusters. A split that reduces nothing is arbitrary and is not made. Answers the
    clusters and, with ``keep_steps``, every intermediate partition."""
    clusters = [list(c) for c in clusters]
    steps = [[list(c) for c in clusters]] if keep_steps else None
    while True:
        if maximum is not None:
            eligible = [i for i, c in enumerate(clusters) if sum(size_of(a) for a in c) > maximum]
            if not eligible:
                break
        else:
            if len(clusters) >= wanted:
                break
            eligible = range(len(clusters))
        best = None
        for i in eligible:
            if len(clusters[i]) < 2:
                continue
            cut = _best_cut(data, tree, clusters[i], size_of, minimum)
            if cut is not None and (best is None or cut[:2] > best[0][:2]):
                best = (cut, i)
        if best is None or best[0][1] <= 1e-12:
            break
        (_, _, child), i = best
        first, second = _split(tree, clusters[i], child)
        clusters[i:i + 1] = [first, second]
        if keep_steps:
            steps.append([list(c) for c in clusters])
    return clusters, steps


def _pseudo_f(data, clusters):
    """Calinski and Harabasz's pseudo F as the "How Spatially Constrained Multivariate Clustering works" page gives it:
    [R²/(n_c − 1)] / [(1 − R²)/(n − n_c)], R² = (SST − SSE)/SST."""
    n, k = len(data), len(clusters)
    if k < 2 or k >= n:
        return None
    sst = float(((data - data.mean(axis=0)) ** 2).sum())
    sse = sum(float(((data[c] - data[c].mean(axis=0)) ** 2).sum()) for c in clusters)
    if sst == 0:
        return None
    r2 = (sst - sse) / sst
    return math.inf if r2 >= 1 else (r2 / (k - 1)) / ((1 - r2) / (n - k))


def _trimmed_delaunay(tool, features):
    """Trimmed Delaunay triangulation as the page describes it: Thiessen polygons of the centroids clipped to the
    features' convex hull, two features neighbours when their clipped polygons share an edge.
    INFERRED: an edge is shared when the common boundary has positive length; on collinear centroids (a hull with no
    area) each feature's neighbours are the ones beside it along the line."""
    import shapely
    from shapely.geometry import MultiPoint
    from shapely.strtree import STRtree
    xy = [(f[2], f[3]) for f in features]
    if len(set(xy)) != len(xy):
        raise ValueError(f"arcpy.stats.{tool}: TRIMMED_DELAUNAY_TRIANGULATION cannot be used with coincident features.")
    n = len(xy)
    hull = shapely.convex_hull(shapely.union_all([f[4] for f in features]))
    if hull.area == 0:
        order = sorted(range(n), key=lambda i: xy[i])
        out = [[] for _ in range(n)]
        for a, b in zip(order, order[1:]):
            out[a].append(b)
            out[b].append(a)
        return out
    cells = list(shapely.voronoi_polygons(MultiPoint(xy), ordered=True).geoms)
    clipped = [c.intersection(hull) for c in cells]
    scale = max(hull.length, 1.0)
    tree = STRtree(clipped)
    out = [set() for _ in range(n)]
    for i, cell in enumerate(clipped):
        for j in tree.query(cell, predicate="intersects"):
            j = int(j)
            if j != i and cell.intersection(clipped[j]).length > 1e-9 * scale:
                out[i].add(j)
                out[j].add(i)
    return [sorted(s) for s in out]


@equivalent("SKATER — a minimum spanning tree of the neighbour graph cut where it most reduces the within-cluster sum "
            "of squares — with membership probabilities from SKATER rerun on random spanning trees, drawn with NumPy's "
            "PCG64 from env.randomGenerator", "Assunção, Neves, Câmara and da Costa Freitas 2006; Calinski and Harabasz "
            "1974; Esri's \"How Spatially Constrained Multivariate Clustering works\"")
def SpatiallyConstrainedMultivariateClustering(in_features, output_features, analysis_fields, size_constraints="NONE",
                                               constraint_field=None, min_constraint=None, max_constraint=None,
                                               number_of_clusters=None, spatial_constraints=None,
                                               weights_matrix_file=None, number_of_permutations=0, output_table=None,
                                               scale="SCALE_DATA"):
    """EQUIVALENT: SKATER (Assunção et al. 2006), which the page names. The analysis fields are z-transformed (with
    SCALE_DATA; a constant field is left out of the analysis and kept in the output). The neighbour graph's edges cost
    the squared distance between the two features in that space; the minimum spanning tree (forest, if the graph is
    disconnected) is cut one edge at a time, at the edge of any tree whose removal most reduces the sum of squared
    deviations from the cluster means, "avoiding (if possible) singletons", until the number of clusters is reached.

    Size constraints (NUM_FEATURES counts features, ATTRIBUTE_VALUE sums ``constraint_field``): a cut is allowed only if
    both parts meet ``min_constraint``; with ``max_constraint`` the tool "starts with a single cluster" and splits only
    clusters above the maximum until none is, and ``number_of_clusters`` is not used. Clusters that miss a constraint
    are named in a warning. With no ``number_of_clusters`` 2 to 30 are evaluated by the page's pseudo F and the largest
    wins. A disconnected graph starts from its parts, and fails when they outnumber the clusters asked.

    Spatial constraints: CONTIGUITY_EDGES_ONLY, CONTIGUITY_EDGES_CORNERS (the default for polygons) and
    TRIMMED_DELAUNAY_TRIANGULATION (the default for points). GET_SPATIAL_WEIGHTS_FROM_FILE is refused (the .swm layout
    is not published).

    Membership probabilities (``number_of_permutations`` above 0): the page says the tool "creates random spanning
    trees" and compares their solutions with the original. INFERRED: a random spanning tree is the minimum spanning
    tree of the neighbour graph under independent uniform random edge costs; SKATER is rerun on it to the same number
    of clusters with the same constraints; a feature's PROB is the mean, over the permutations, of the share of its
    original cluster (itself included) that lies in its cluster of the permuted solution.

    Output: the analysis fields, CLUSTER_ID (numbered by each cluster's lowest object id, INFERRED) and PROB when
    probabilities are asked. INFERRED: ``output_table`` holds NUM_CLUSTERS and PSEUDO_F (the page does not name its
    fields), one row per number of clusters evaluated, or the one row of the number asked; features with a null in an
    analysis field are left out; ``number_of_permutations`` defaults to 0, which the page says skips the probabilities;
    the minimum spanning tree's ties are broken by object id order.
    """
    import numpy as np
    s = _s()
    tool = "SpatiallyConstrainedMultivariateClustering"
    if not s._given(output_table):
        raise ValueError(f"arcpy.stats.{tool}: output_table is required.")
    fields = [f for f in s._list(analysis_fields) if s._given(f)]
    size_kind = s._keyword(size_constraints) if s._given(size_constraints) else "NONE"
    if size_kind not in ("NONE", "NUM_FEATURES", "ATTRIBUTE_VALUE"):
        raise ValueError(f"arcpy.stats.{tool}: size_constraints '{size_constraints}' is not NONE, NUM_FEATURES or "
                         "ATTRIBUTE_VALUE.")
    extra = [constraint_field] if size_kind == "ATTRIBUTE_VALUE" and s._given(constraint_field) else []
    if size_kind == "ATTRIBUTE_VALUE" and not extra:
        raise ValueError(f"arcpy.stats.{tool}: ATTRIBUTE_VALUE needs constraint_field.")
    features, columns, kind, srid = s._features(tool, in_features, fields + extra)
    features = [f for f in features if all(f[1][k.lower()] is not None for k in fields + extra)]
    features.sort(key=lambda f: f[0])
    n = len(features)
    if n < 2:
        raise ValueError(f"arcpy.stats.{tool} needs two features or more.")
    raw = np.array([[float(f[1][k.lower()]) for k in fields] for f in features])
    varied = raw.std(axis=0) > 0
    if not varied.any():
        raise ValueError(f"arcpy.stats.{tool}: every analysis field is constant.")
    data = raw[:, varied]
    if s._keyword(scale or "SCALE_DATA") not in ("NO_SCALE_DATA", "FALSE"):
        data = (data - data.mean(axis=0)) / data.std(axis=0)
    concept = s._keyword(spatial_constraints) if s._given(spatial_constraints) else (
        "CONTIGUITY_EDGES_CORNERS" if kind == "Polygon" else "TRIMMED_DELAUNAY_TRIANGULATION")
    if concept == "GET_SPATIAL_WEIGHTS_FROM_FILE":
        s._refuse(tool, "spatial_constraints", concept, "a spatial weights matrix file's .swm layout is not published")
    if concept in ("CONTIGUITY_EDGES_ONLY", "CONTIGUITY_EDGES_CORNERS"):
        w = s._weights(tool, features, srid, concept, "EUCLIDEAN_DISTANCE", None, None)
        graph = [[int(j) for j in np.nonzero(w[i])[0]] for i in range(n)]
    elif concept == "TRIMMED_DELAUNAY_TRIANGULATION":
        graph = _trimmed_delaunay(tool, features)
    else:
        raise ValueError(f"arcpy.stats.{tool}: spatial_constraints '{spatial_constraints}' is not one the tool takes.")
    edges = sorted({(min(i, j), max(i, j)) for i in range(n) for j in graph[i]})
    if size_kind == "ATTRIBUTE_VALUE":
        sizes = [float(f[1][constraint_field.lower()]) for f in features]
    else:
        sizes = [1.0] * n
    size_of = sizes.__getitem__
    minimum = float(min_constraint) if size_kind != "NONE" and s._given(min_constraint) else None
    maximum = float(max_constraint) if size_kind != "NONE" and s._given(max_constraint) else None
    for value, name in ((minimum, "min_constraint"), (maximum, "max_constraint")):
        if value is not None and value <= 0:
            raise ValueError(f"arcpy.stats.{tool}: {name} must be positive.")

    def solve(tree, wanted, keep_steps):
        parts = _components(set(range(n)), tree)
        if maximum is None and wanted is not None and len(parts) > wanted:
            raise ValueError(f"arcpy.stats.{tool}: the features fall into {len(parts)} unconnected groups, more than "
                             f"the {wanted} clusters asked.")
        return _skater(data, tree, parts, size_of, minimum, maximum, wanted, keep_steps)

    costs = [(float(((data[i] - data[j]) ** 2).sum()), i, j) for i, j in edges]
    mst = _kruskal(n, costs)
    table_rows = []
    if maximum is not None:
        clusters, _ = solve(mst, None, False)
    elif s._given(number_of_clusters):
        wanted = int(number_of_clusters)
        if wanted < 2:
            raise ValueError(f"arcpy.stats.{tool}: number_of_clusters must be at least 2.")
        clusters, _ = solve(mst, wanted, False)
        table_rows.append((len(clusters), _pseudo_f(data, clusters)))
    else:
        top = min(30, n - 1)
        _, steps = solve(mst, top, True)
        candidates = [c for c in steps if 2 <= len(c) <= top]
        if not candidates:
            raise ValueError(f"arcpy.stats.{tool}: no split of the features reduces their within-cluster variation.")
        for c in candidates:
            table_rows.append((len(c), _pseudo_f(data, c)))
        best = max(range(len(candidates)), key=lambda i: (table_rows[i][1] if table_rows[i][1] is not None else -1,
                                                          -i))
        clusters = candidates[best]
        from ._runtime import AddMessage
        AddMessage(f"Optimal number of clusters: {len(clusters)}")
    if s._given(number_of_clusters) and maximum is None and len(clusters) < int(number_of_clusters):
        AddWarning(f"{tool}: only {len(clusters)} clusters could be made; further splits would be arbitrary or break "
                   "the size constraints.")
    missed = []
    for c in clusters:
        total = sum(sizes[a] for a in c)
        if (minimum is not None and total < minimum) or (maximum is not None and total > maximum):
            missed.append(c)
    label = np.zeros(n, dtype=int)
    for index, c in enumerate(sorted(clusters, key=lambda c: features[c[0]][0]), start=1):
        label[c] = index
    if missed:
        AddWarning(f"{tool}: cluster(s) {sorted(int(label[c[0]]) for c in missed)} do not meet the size constraints.")
    permutations = int(number_of_permutations) if s._given(number_of_permutations) else 0
    if permutations < 0:
        raise ValueError(f"arcpy.stats.{tool}: number_of_permutations cannot be negative.")
    probability = None
    if permutations:
        random = np.random.default_rng(s._seed())
        stay = np.zeros(n)
        original = [set(c) for c in clusters]
        home = {a: original[i] for i, c in enumerate(clusters) for a in c}
        for _ in range(permutations):
            tree = _kruskal(n, [(float(random.random()), i, j) for i, j in edges])
            parts = _components(set(range(n)), tree)
            if maximum is not None:
                again, _ = _skater(data, tree, parts, size_of, minimum, maximum, None)
            else:
                again, _ = _skater(data, tree, parts, size_of, minimum, None, len(clusters))
            where = {a: set(c) for c in again for a in c}
            for a in range(n):
                stay[a] += len(home[a] & where[a]) / len(home[a])
        probability = stay / permutations
    by_name = {c.name.lower(): c for c in columns}
    keep = [by_name[f.lower()] for f in fields]
    out = s._out(output_features)
    new = [_gpkg.Column("CLUSTER_ID", "Integer")] + ([_gpkg.Column("PROB", "Double")] if probability is not None else [])
    _gpkg.create(out, kind, srid, [*keep, *new])
    with da.InsertCursor(out, [*[c.name for c in keep], *[c.name for c in new], "SHAPE@"]) as written:
        for i, f in enumerate(features):
            extra_values = [float(probability[i])] if probability is not None else []
            written.insertRow((*[f[1][c.name.lower()] for c in keep], int(label[i]), *extra_values, f[4]))
    table = s._write_table(output_table, [_gpkg.Column("NUM_CLUSTERS", "Integer"), _gpkg.Column("PSEUDO_F", "Double")],
                           table_rows) if table_rows else ""
    return Result(out, table)


# ---- Local Bivariate Relationships (entropy, Guo 2010) -------------------------------------------------------------

def _mst_length(points, gamma):
    """The power-weighted minimum spanning tree length Σ|e|^γ over the Euclidean minimum spanning tree (Hero, Ma,
    Michel and Gorman 2002). A zero distance is kept as an edge of length zero."""
    import numpy as np
    from scipy.sparse.csgraph import minimum_spanning_tree
    d = np.sqrt(((points[:, None, :] - points[None, :, :]) ** 2).sum(axis=2))
    d[(d == 0) & ~np.eye(len(points), dtype=bool)] = 1e-300
    tree = minimum_spanning_tree(d).toarray()
    lengths = tree[tree > 0]
    return float((np.where(lengths <= 1e-300, 0.0, lengths) ** gamma).sum())


def _renyi(length, count, alpha):
    """Hero et al.'s (2002) Rényi α-entropy estimate from the power-weighted spanning tree length, its bias constant
    β left out (it is the same for every estimate compared): (1/(1 − α)) ln(L_γ / n^α)."""
    return math.log(max(length, 1e-300) / count ** alpha) / (1 - alpha)


def _local_fit(x, y, quadratic):
    """Least squares of y on x (and x²): coefficients, AICc as grapy's OLS writes it — n ln(2π RSS/n) + n(n+k)/(n−2−k),
    k the coefficients (Hurvich and Tsai 1989) — and adjusted R²."""
    import numpy as np
    design = np.column_stack([np.ones_like(x), x] + ([x ** 2] if quadratic else []))
    beta, *_ = np.linalg.lstsq(design, y, rcond=None)
    rss = float(((y - design @ beta) ** 2).sum())
    n, k = len(y), design.shape[1]
    tss = float(((y - y.mean()) ** 2).sum())
    r2 = 1 - rss / tss if tss > 0 else 0.0
    adjusted = 1 - (1 - r2) * (n - 1) / (n - k)
    aicc = n * math.log(2 * math.pi * max(rss, 1e-300) / n) + n * (n + k) / (n - 2 - k)
    return beta, aicc, adjusted


@equivalent("joint entropy by the power-weighted minimum spanning tree with a permutation test, the relationship "
            "typed by linear and quadratic least squares compared on AICc, its permutations drawn with NumPy's PCG64 "
            "from env.randomGenerator", "Guo 2010; Hero, Ma, Michel and Gorman 2002; Hurvich and Tsai 1989; "
            "Benjamini and Hochberg 1995; Esri's \"How Local Bivariate Relationships works\"")
def LocalBivariateRelationships(in_features, dependent_variable, explanatory_variable, output_features,
                                number_of_neighbors=30, number_of_permutations=199,
                                enable_local_scatterplot_popups="CREATE_POPUP", level_of_confidence="90%",
                                apply_false_discovery_rate_fdr_correction="APPLY_FDR", scaling_factor=0.5):
    """EQUIVALENT: the steps of Esri's "How Local Bivariate Relationships works", each over a published estimator.

    1. Both variables are rescaled to 0–1 over the whole dataset (minimum subtracted, divided by the range).
    2. Each feature's neighbourhood is its ``number_of_neighbors`` nearest features, itself included (ties by object
       id).
    3. The joint entropy of the neighbourhood's (x, y) points is estimated from the power-weighted minimum spanning
       tree (Guo 2010, after Hero et al. 2002): Ĥ_α = (1/(1 − α)) ln(Σ|e|^γ / k^α), γ = 2(1 − α).
    4. Permutations reassign each value of the explanatory variable to a new value of the dependent one; the pseudo
       p-value is (the permutations whose joint entropy is smaller than the data's, plus one) over (the permutations
       plus one), the page's proportion with its stated 1/(N + 1) precision.
    5. Significance at ``level_of_confidence``, with the Benjamini–Hochberg correction under APPLY_FDR.
    6. A significant feature's local relationship: linear and quadratic least squares of the dependent on the
       explanatory variable (rescaled, the neighbourhood's values); the quadratic is chosen only if its AICc is at
       least 3 less; an adjusted R² under 0.05 is Undefined Complex; otherwise Positive Linear, Negative Linear, Convex
       (x² coefficient positive) or Concave, as the page gives them.

    INFERRED: the scaling factor s is the spanning tree's power as a share of the dimension, γ = 2s (α = 1 − s): the
    page says only that values near 0 detect strong relationships and resist outliers, which a small power does by
    flattening long edges; ENTROPY is the mutual information the page's identity gives — the permutations' mean joint
    entropy (both variables' own entropies, no shared information) less the data's; SIG_LEVEL is the highest of 90%,
    95% and 99% at or above the chosen level at which the feature is significant, as "90%", "95%", "99%" or "Not
    Significant"; the field names ENTROPY, PVALUES, SIG_LEVEL, LBR_TYPE, INTERCEPT, COEF_1, COEF_2 (null for a linear
    model), AICC and R2 (adjusted) are grapy's, the page does not give them; the fit is written for every feature;
    features with a null in either field are left out. ``enable_local_scatterplot_popups`` is accepted and does
    nothing: a server has no pop-ups.
    """
    import numpy as np
    s = _s()
    tool = "LocalBivariateRelationships"
    features, columns, kind, srid = s._features(tool, in_features, [dependent_variable, explanatory_variable])
    ky, kx = dependent_variable.lower(), explanatory_variable.lower()
    features = sorted((f for f in features if f[1][ky] is not None and f[1][kx] is not None), key=lambda f: f[0])
    n = len(features)
    k = int(number_of_neighbors) if s._given(number_of_neighbors) else 30
    if not 30 <= k <= 1000:
        raise ValueError(f"arcpy.stats.{tool}: number_of_neighbors must be between 30 and 1,000.")
    if k > n:
        raise ValueError(f"arcpy.stats.{tool}: number_of_neighbors ({k}) is more than the {n} features.")
    count = int(number_of_permutations) if s._given(number_of_permutations) else 199
    if count not in (99, 199, 499, 999):
        raise ValueError(f"arcpy.stats.{tool}: number_of_permutations must be 99, 199, 499 or 999.")
    level = str(level_of_confidence if s._given(level_of_confidence) else "90%").replace("%", "").strip()
    levels = {"90": 0.10, "95": 0.05, "99": 0.01}
    if level not in levels:
        raise ValueError(f"arcpy.stats.{tool}: level_of_confidence must be 90%, 95% or 99%.")
    factor = float(scaling_factor) if s._given(scaling_factor) else 0.5
    if not 0.01 <= factor <= 1:
        raise ValueError(f"arcpy.stats.{tool}: scaling_factor must be between 0.01 and 1.")
    gamma, alpha = 2 * factor, 1 - factor
    if alpha == 0:
        alpha = 1e-12  # s = 1: γ = 2, the Rényi order's limit; ln(L/k^α)/(1 − α) stays finite
    y = np.array([float(f[1][ky]) for f in features])
    x = np.array([float(f[1][kx]) for f in features])
    for name, v in ((dependent_variable, y), (explanatory_variable, x)):
        if np.ptp(v) == 0:
            raise ValueError(f"arcpy.stats.{tool}: {name} is constant.")
    xs, ys = (x - x.min()) / np.ptp(x), (y - y.min()) / np.ptp(y)
    d = s._distances([(f[2], f[3]) for f in features])
    oids = [f[0] for f in features]
    random = np.random.default_rng(s._seed())
    p_values, entropy, fits = [], [], []
    for i in range(n):
        near = sorted(range(n), key=lambda j: (d[i, j], j != i, oids[j]))[:k]
        lx, ly = xs[near], ys[near]
        observed = _renyi(_mst_length(np.column_stack([lx, ly]), gamma), k, alpha)
        drawn = np.array([_renyi(_mst_length(np.column_stack([lx[random.permutation(k)], ly]), gamma), k, alpha)
                          for _ in range(count)])
        p_values.append(float(((drawn < observed).sum() + 1) / (count + 1)))
        entropy.append(float(drawn.mean() - observed))
        linear, aicc_linear, adjusted_linear = _local_fit(lx, ly, False)
        curved, aicc_curved, adjusted_curved = _local_fit(lx, ly, True)
        if aicc_curved <= aicc_linear - 3:
            fits.append(("QUADRATIC", curved, aicc_curved, adjusted_curved))
        else:
            fits.append(("LINEAR", linear, aicc_linear, adjusted_linear))
    fdr = s._wants_fdr(apply_false_discovery_rate_fdr_correction if s._given(
        apply_false_discovery_rate_fdr_correction) else "APPLY_FDR")
    significant_at = {}
    for name, cut in levels.items():
        if fdr:
            flags = s._bh_significant(p_values, cut)
            significant_at[name] = {i for i, flag in enumerate(flags) if flag}
        else:
            significant_at[name] = {i for i, p in enumerate(p_values) if p <= cut}
    reached = []
    for i in range(n):
        best = "Not Significant"
        for name in ("90", "95", "99"):
            if float(name) >= float(level) and i in significant_at[name]:
                best = f"{name}%"
        reached.append(best)
    types = []
    for i in range(n):
        model, beta, _, adjusted = fits[i]
        if reached[i] == "Not Significant":
            types.append("Not Significant")
        elif adjusted < 0.05:
            types.append("Undefined Complex")
        elif model == "LINEAR":
            types.append("Positive Linear" if beta[1] > 0 else "Negative Linear")
        else:
            types.append("Convex" if beta[2] > 0 else "Concave")
    by_name = {c.name.lower(): c for c in columns}
    out = s._out(output_features)
    new = [_gpkg.Column(name, kind_of) for name, kind_of in (
        ("ENTROPY", "Double"), ("PVALUES", "Double"), ("SIG_LEVEL", "String"), ("LBR_TYPE", "String"),
        ("INTERCEPT", "Double"), ("COEF_1", "Double"), ("COEF_2", "Double"), ("AICC", "Double"), ("R2", "Double"))]
    keep = [by_name[ky], by_name[kx]]
    _gpkg.create(out, kind, srid, [*keep, *new])
    with da.InsertCursor(out, [*[c.name for c in keep], *[c.name for c in new], "SHAPE@"]) as written:
        for i, f in enumerate(features):
            model, beta, aicc, adjusted = fits[i]
            written.insertRow((f[1][ky], f[1][kx], entropy[i], p_values[i], reached[i], types[i], float(beta[0]),
                               float(beta[1]), float(beta[2]) if model == "QUADRATIC" else None, float(aicc),
                               float(adjusted), f[4]))
    return Result(out)


# ---- Prepare Data for Prediction ----------------------------------------------------------------------------------

def _attribute_space(rows, numeric):
    """The numeric explanatory variables, z-transformed, as the attribute space the balancing methods measure in."""
    import numpy as np
    if not numeric:
        return np.zeros((len(rows), 0))
    data = np.array([[float(r[name]) for name in numeric] for r in rows])
    sd = data.std(axis=0)
    sd[sd == 0] = 1.0
    return (data - data.mean(axis=0)) / sd


def _k_medoids(points, k):
    """Park and Jun's (2009) simple k-medoids: the k objects of least normalised distance sum start; each object joins
    its nearest medoid, each medoid moves to the member of least total distance, until nothing changes."""
    import numpy as np
    d = np.sqrt(((points[:, None, :] - points[None, :, :]) ** 2).sum(axis=2))
    sums = d.sum(axis=1, keepdims=True)
    v = (d / np.where(sums == 0, 1, sums)).sum(axis=0)
    medoids = sorted(np.argsort(v, kind="stable")[:k].tolist())
    for _ in range(100):
        nearest = np.argmin(d[:, medoids], axis=1)
        moved = []
        for c in range(len(medoids)):
            members = np.nonzero(nearest == c)[0]
            moved.append(int(members[np.argmin(d[np.ix_(members, members)].sum(axis=1))]) if len(members) else medoids[c])
        moved = sorted(moved)
        if moved == medoids:
            break
        medoids = moved
    return medoids


@equivalent("random and nearest-neighbour test splits and the published balancing methods — random under- and "
            "oversampling, Tomek links, k-medoids undersampling, SMOTE and greedy spatial thinning — drawn with "
            "NumPy's PCG64 from env.randomGenerator", "Tomek 1976; Chawla, Bowyer, Hall and Kegelmeyer 2002; Park "
            "and Jun 2009; Esri's \"How Prepare Data for Prediction works\"")
def PrepareData(in_features, out_features, splitting_type="RANDOM_SPLIT", out_test_features=None, variable_predict=None,
                treat_variable_as_categorical="NUMERIC", explanatory_variables=None, distance_features=None,
                explanatory_rasters=None, use_raster_values="SAMPLE_POLYGON", percent=10, balancing_type="NONE",
                thinning_distance_band=None, number_of_iterations=10, encode_variables="NO_ENCODE",
                append_all_fields="APPEND"):
    """EQUIVALENT: the steps of Esri's "How Prepare Data for Prediction works", each by a published method.

    Split (``percent`` of the features to ``out_test_features``): RANDOM_SPLIT draws them at random; SPATIAL_SPLIT
    draws one feature at random and takes it with its nearest neighbours, as the page describes. Every category of a
    categorical target or explanatory variable must be in the training output: a split that leaves one out is drawn
    again, and the tool fails after 30 tries, as documented. NONE writes no test output.

    Balancing, on the training output only: RANDOM_UNDER removes random features of every class but the smallest down
    to its count; RANDOM_OVER duplicates random features of every class but the largest up to its count; TOMEK_UNDER
    removes the non-minority member of each Tomek link (Tomek 1976) — a pair of mutual nearest neighbours in attribute
    space, one of them of the minority class; KMED_UNDER keeps, of every other class, the medoids of a k-medoids
    clustering (Park and Jun 2009) with k the minority count; SMOTE_OVER (Chawla et al. 2002) makes each synthetic
    feature from a random minority feature and a random one of its 5 nearest minority neighbours, its numeric values
    at a uniform random point between theirs, with the first feature's geometry; SPATIAL_THINNING keeps, in a random
    order, each feature at least ``thinning_distance_band`` from every one kept (within each category when the target
    is categorical), over ``number_of_iterations`` orders.

    INFERRED: the test count is ``percent`` of the features, rounded; the attribute space is the numeric explanatory
    variables z-transformed; the categorical explanatory variables of a synthetic SMOTE feature are its first
    feature's; with several classes, "the minority" is the smallest; SPATIAL_THINNING keeps the order that keeps most
    (the first on a tie) and measures between centroids; an encoded field is named ``<field>_<value>``; with
    NO_APPEND the output holds the target and the explanatory fields; the rows keep the input's object id order.
    Refused: ``distance_features`` and ``explanatory_rasters`` (the page names no field for the values they add).
    """
    import numpy as np
    s = _s()
    tool = "PrepareData"
    if s._given(distance_features):
        s._refuse(tool, "distance_features", distance_features, "the page does not name the field each distance is "
                  "written to")
    if s._given(explanatory_rasters):
        s._refuse(tool, "explanatory_rasters", explanatory_rasters, "the page does not name the field each raster's "
                  "values are written to")
    split = s._keyword(splitting_type) if s._given(splitting_type) else "RANDOM_SPLIT"
    if split not in ("RANDOM_SPLIT", "SPATIAL_SPLIT", "NONE"):
        raise ValueError(f"arcpy.stats.{tool}: splitting_type '{splitting_type}' is not RANDOM_SPLIT, SPATIAL_SPLIT "
                         "or NONE.")
    balance = s._keyword(balancing_type) if s._given(balancing_type) else "NONE"
    if balance not in ("NONE", "SPATIAL_THINNING", "RANDOM_UNDER", "TOMEK_UNDER", "KMED_UNDER", "RANDOM_OVER",
                       "SMOTE_OVER"):
        raise ValueError(f"arcpy.stats.{tool}: balancing_type '{balancing_type}' is not one the tool takes.")
    target = variable_predict if s._given(variable_predict) else None
    categorical_target = target is not None and (treat_variable_as_categorical is True or s._keyword(
        treat_variable_as_categorical) in ("CATEGORICAL", "TRUE"))
    if balance not in ("NONE", "SPATIAL_THINNING") and not categorical_target:
        raise ValueError(f"arcpy.stats.{tool}: {balance} balances the classes of a categorical variable_predict.")
    explanatory = [(row[0], s._keyword(row[1] or "") in ("CATEGORICAL", "TRUE")) for row in s._pairs(
        explanatory_variables, 2) if s._given(row[0])]
    names = ([target] if target else []) + [name for name, _ in explanatory]
    features, columns, kind, srid = s._features(tool, in_features, names)
    features.sort(key=lambda f: f[0])
    n = len(features)
    by_name = {c.name.lower(): c for c in columns}
    categorical = ([target.lower()] if categorical_target else []) + [name.lower() for name, c in explanatory if c]
    numeric = [name.lower() for name, c in explanatory if not c]
    random = np.random.default_rng(s._seed())
    share = float(percent) if s._given(percent) else 10.0
    if not 0 < share < 100:
        raise ValueError(f"arcpy.stats.{tool}: percent must be between 0 and 100.")
    test = set()
    if split != "NONE":
        wanted = int(round(n * share / 100))
        if not 0 < wanted < n:
            raise ValueError(f"arcpy.stats.{tool}: {share} percent of {n} features leaves no test or no training "
                             "features.")
        d = s._distances([(f[2], f[3]) for f in features]) if split == "SPATIAL_SPLIT" else None
        for attempt in range(30):
            if split == "RANDOM_SPLIT":
                test = set(random.choice(n, wanted, replace=False).tolist())
            else:
                start = int(random.integers(n))
                test = set(sorted(range(n), key=lambda j: (d[start, j], features[j][0]))[:wanted])
            training = [features[i] for i in range(n) if i not in test]
            if all({f[1][c] for f in training} >= {f[1][c] for f in features} for c in categorical):
                break
        else:
            raise ValueError(f"arcpy.stats.{tool}: no split in 30 tries keeps every category in the training data.")
    training = [features[i] for i in range(n) if i not in test]
    tested = [features[i] for i in sorted(test)]
    # each row: (feature, values) — values may be synthetic
    rows = [(f, dict(f[1])) for f in training]
    if balance == "SPATIAL_THINNING":
        if not s._given(thinning_distance_band):
            raise ValueError(f"arcpy.stats.{tool}: SPATIAL_THINNING needs thinning_distance_band.")
        band = s._threshold(thinning_distance_band, srid)
        tries = int(number_of_iterations) if s._given(number_of_iterations) else 10
        if not 1 <= tries <= 50:
            raise ValueError(f"arcpy.stats.{tool}: number_of_iterations must be between 1 and 50.")
        groups = {}
        for index, (f, v) in enumerate(rows):
            groups.setdefault(v[target.lower()] if categorical_target else None, []).append(index)
        kept_all = []
        for members in groups.values():
            best = None
            for _ in range(tries):
                kept = []
                for index in random.permutation(members).tolist():
                    f = rows[index][0]
                    if all(math.hypot(f[2] - rows[j][0][2], f[3] - rows[j][0][3]) >= band for j in kept):
                        kept.append(index)
                if best is None or len(kept) > len(best):
                    best = kept
            kept_all.extend(best)
        rows = [rows[i] for i in sorted(kept_all)]
    elif balance != "NONE":
        key = target.lower()
        classes = {}
        for index, (f, v) in enumerate(rows):
            classes.setdefault(v[key], []).append(index)
        smallest = min(classes, key=lambda c: (len(classes[c]), str(c)))
        largest = max(classes, key=lambda c: (len(classes[c]), str(c)))
        space = _attribute_space([v for _, v in rows], numeric)
        if balance == "RANDOM_UNDER":
            keep = set(classes[smallest])
            for c, members in classes.items():
                if c != smallest:
                    keep.update(random.choice(members, len(classes[smallest]), replace=False).tolist())
            rows = [rows[i] for i in sorted(keep)]
        elif balance == "KMED_UNDER":
            keep = set(classes[smallest])
            for c, members in classes.items():
                if c != smallest:
                    medoids = _k_medoids(space[members], len(classes[smallest]))
                    keep.update(members[m] for m in medoids)
            rows = [rows[i] for i in sorted(keep)]
        elif balance == "TOMEK_UNDER":
            if space.shape[1] == 0:
                raise ValueError(f"arcpy.stats.{tool}: TOMEK_UNDER needs numeric explanatory variables.")
            dd = np.sqrt(((space[:, None, :] - space[None, :, :]) ** 2).sum(axis=2))
            np.fill_diagonal(dd, np.inf)
            nearest = np.argmin(dd, axis=1)
            drop = set()
            for i in classes[smallest]:
                j = int(nearest[i])
                if int(nearest[j]) == i and rows[j][1][key] != smallest:
                    drop.add(j)
            rows = [rows[i] for i in range(len(rows)) if i not in drop]
        elif balance == "RANDOM_OVER":
            extra = []
            for c, members in classes.items():
                if c != largest:
                    extra.extend(random.choice(members, len(classes[largest]) - len(members), replace=True).tolist())
            rows = rows + [(rows[i][0], dict(rows[i][1])) for i in extra]
        elif balance == "SMOTE_OVER":
            if space.shape[1] == 0:
                raise ValueError(f"arcpy.stats.{tool}: SMOTE_OVER needs numeric explanatory variables.")
            extra = []
            for c, members in classes.items():
                if c == largest:
                    continue
                if len(members) < 2:
                    raise ValueError(f"arcpy.stats.{tool}: SMOTE_OVER needs two features or more of class {c}.")
                local = space[members]
                dd = np.sqrt(((local[:, None, :] - local[None, :, :]) ** 2).sum(axis=2))
                for _ in range(len(classes[largest]) - len(members)):
                    a = int(random.integers(len(members)))
                    order = [j for j in np.argsort(dd[a], kind="stable").tolist() if j != a][:5]
                    b = order[int(random.integers(len(order)))]
                    gap = float(random.random())
                    first, second = rows[members[a]], rows[members[b]]
                    values = dict(first[1])
                    for name in numeric:
                        values[name] = float(first[1][name]) + gap * (float(second[1][name]) - float(first[1][name]))
                    extra.append((first[0], values))
            rows = rows + extra
    append = append_all_fields is True or s._keyword(append_all_fields if s._given(append_all_fields) else "APPEND") \
        in ("APPEND", "TRUE")
    if append:
        keep = [c for c in columns]
    else:
        keep = [by_name[name.lower()] for name in names]
    encode = encode_variables is True or s._keyword(encode_variables or "NO_ENCODE") in ("ENCODE", "TRUE")
    encoded = []
    if encode:
        for name, is_categorical in explanatory:
            if is_categorical:
                for value in sorted({f[1][name.lower()] for f in features if f[1][name.lower()] is not None}, key=str):
                    encoded.append((name.lower(), value, _gpkg.Column(f"{by_name[name.lower()].name}_{value}",
                                                                      "Integer")))
    all_columns = [*keep, *[c for _, _, c in encoded]]

    def write(path, written_rows):
        out = s._out(path)
        _gpkg.create(out, kind, srid, all_columns)
        fields = [c.name for c in keep]
        with da.SearchCursor(in_features, ["OID@", *fields]) as source:
            originals = {r[0]: dict(zip([f.lower() for f in fields], r[1:])) for r in source}
        with da.InsertCursor(out, [*[c.name for c in all_columns], "SHAPE@"]) as cursor:
            for f, values in written_rows:
                record = dict(originals[f[0]])
                record.update({k2: v for k2, v in values.items() if k2 in record})
                cursor.insertRow((*[record[c.name.lower()] for c in keep],
                                  *[int(values.get(field) == value) for field, value, _ in encoded], f[4]))
        return out

    out = write(out_features, rows)
    test_out = ""
    if split != "NONE" and s._given(out_test_features):
        test_out = write(out_test_features, [(f, dict(f[1])) for f in tested])
    return Result(out, test_out)
