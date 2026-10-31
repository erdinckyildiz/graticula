"""Image classification, pan sharpening, region growing and mean shift segmentation for arcpy.sa and arcpy.ia — the
names once refused as "not specified closely enough to reproduce", built as equivalents (ADR-224 §5.6, ADR-243).

Each pages names a method and leaves its details open; each tool here is the published method that does what the
page describes, cited in its docstring and its ``_runtime.equivalent`` mark, with the open details recorded as
INFERRED.

The classifier definition (.ecd)
================================

Esri's pages say the ``.ecd`` "is a JSON format file that contains attribute information, statistics, hyperplane
vectors, and other information for the classifier", but do not publish its keys. grapy writes its own, documented
here, under the same extension so that a Train tool's output feeds Classify Raster and Classify (INFERRED):

``{"format": "grapy-ecd", "version": 1, "classifier": <name>, "bands": <n>, "additional_bands": <m>,
"classes": [{"value": v, "name": s, "count": c}, …], …}`` with, by classifier,

- ``MaximumLikelihood``: ``"means"`` (one list of n + m per class) and ``"covariances"`` (one matrix per class);
- ``IsoCluster``: ``"means"``;
- ``KNearestNeighbor``: ``"k"``, ``"samples"`` (feature vectors) and ``"labels"``;
- ``SupportVectorMachine``: ``"centre"`` and ``"scale"`` (the standardisation), and ``"machines"``, one per pair of
  classes: ``{"first": v1, "second": v2, "weights": w, "bias": b}`` — the separating hyperplane w·x + b, positive for
  ``first``.

A file that is not grapy's — an ``.ecd`` written by ArcGIS Pro — is refused by name, since its layout is not
published.
"""
from __future__ import annotations

import json
import math
import os

import numpy as np

from ._raster import Raster, _Grid, _open_bands, _refuse_environment, as_raster
from ._runtime import NotBuiltError, env


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _given(value):
    return value is not None and not (isinstance(value, str) and value.strip() in ("", "#"))


def _path(value):
    path = os.fspath(value)
    if not os.path.isabs(path) and env.workspace:
        path = os.path.join(env.workspace, path)
    return path


# ---- the inputs: a stack of bands, and training samples burnt onto it


def _stack(in_raster, in_additional_raster, tool):
    """The bands of the raster and of the additional raster as a (rows, columns, bands) array, a NoData mask (NoData
    in any band) and the grid; how many bands came from the additional raster."""
    raster = _open_bands(in_raster, tool)
    arrays, masks = list(raster._arrays), list(raster._masks)
    extra = 0
    if _given(in_additional_raster):
        more = _open_bands(in_additional_raster, tool)
        if not more._grid.same(raster._grid):
            _refuse(tool, "in_additional_raster", in_additional_raster,
                    "an additional raster on another grid would need resampling, which the page does not specify")
        arrays += list(more._arrays)
        masks += list(more._masks)
        extra = len(more._arrays)
    data = np.stack([np.asarray(a, dtype=np.float64) for a in arrays], axis=-1)
    mask = np.logical_or.reduce(masks)
    return data, mask, raster._grid, extra


def _segment_parameters(tool, used_attributes, dimension_value_field):
    if _given(used_attributes):
        _refuse(tool, "used_attributes", used_attributes,
                "segment attributes need the Segmented key property, which grapy's rasters do not carry, and the page "
                "does not define COMPACTNESS or RECTANGULARITY")
    if _given(dimension_value_field):
        _refuse(tool, "dimension_value_field", dimension_value_field,
                "a change-analysis raster's training by dimension is not defined on the page")


def _training(in_training_features, grid, tool):
    """Each cell's class value from the training features (0 where none) and the classes' names. A polygon takes the
    cells whose centres it contains, a point or line every cell it touches (INFERRED); where samples overlap, the
    later feature wins (INFERRED)."""
    from rasterio.features import rasterize
    from . import da
    from .analysis import _schema
    columns = {c.name.lower(): c.name for c in _schema(in_training_features)[0]}
    for needed in ("classvalue", "classname"):
        if needed not in columns:
            raise ValueError(f"{tool}: the training samples need the fields classname and classvalue, as documented; "
                             f"'{needed}' is missing from {in_training_features}.")
    polygons, others, names = [], [], {}
    with da.SearchCursor(in_training_features, [columns["classvalue"], columns["classname"], "SHAPE@"]) as rows:
        for value, name, shape in rows:
            if shape is None or value is None:
                continue
            value = int(value)
            names.setdefault(value, str(name) if name is not None else str(value))
            (polygons if shape._shape.geom_type.endswith("Polygon") else others).append((shape._shape, value))
    labels = np.zeros((grid.rows, grid.columns), np.int64)
    for group, touched in ((polygons, False), (others, True)):
        if group:
            burnt = rasterize(group, out_shape=(grid.rows, grid.columns), transform=grid.transform(), fill=0,
                              dtype="int64", all_touched=touched)
            labels = np.where(burnt != 0, burnt, labels)
    if not (labels != 0).any():
        raise ValueError(f"{tool}: no training sample falls on the raster.")
    return labels, names


def _samples(data, mask, labels, max_samples_per_class, seed=0):
    """The training vectors and their labels, at most ``max_samples_per_class`` of each class, drawn without
    replacement by a generator seeded with 0 (INFERRED: the page does not say which samples are kept)."""
    rng = np.random.default_rng(seed)
    xs, ys = [], []
    for value in sorted(int(v) for v in np.unique(labels[(labels != 0) & ~mask])):
        rows, cols = np.nonzero((labels == value) & ~mask)
        if max_samples_per_class is not None and int(max_samples_per_class) > 0 and rows.size > int(max_samples_per_class):
            keep = np.sort(rng.choice(rows.size, int(max_samples_per_class), replace=False))
            rows, cols = rows[keep], cols[keep]
        xs.append(data[rows, cols])
        ys.append(np.full(rows.size, value))
    return np.concatenate(xs), np.concatenate(ys)


# ---- the classifier definition file


def _write_ecd(path, classifier, bands, extra, classes, **content):
    document = {"format": "grapy-ecd", "version": 1, "classifier": classifier, "bands": int(bands),
                "additional_bands": int(extra), "classes": classes}
    document.update(content)
    path = _path(path)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(document, handle, indent=1)
    from ._result import Result
    return Result(path)


def _read_ecd(path, tool):
    path = _path(path)
    try:
        with open(path, encoding="utf-8") as handle:
            document = json.load(handle)
    except (OSError, ValueError) as error:
        raise NotBuiltError(f"{tool}: '{path}' is not a classifier definition grapy wrote: an .ecd from ArcGIS Pro is "
                            "read by Pro only, since Esri does not publish its layout.") from error
    if not isinstance(document, dict) or document.get("format") != "grapy-ecd":
        raise NotBuiltError(f"{tool}: '{path}' is not a classifier definition grapy wrote: an .ecd from ArcGIS Pro is "
                            "read by Pro only, since Esri does not publish its layout.")
    return document


# ---- the classifiers' decision rules


def _gaussian_scores(x, means, covariances, priors=None):
    """Richards and Jia's discriminant for normal classes: g = ln p(ω) − ½ ln|Σ| − ½ (x − m)ᵀ Σ⁻¹ (x − m), with the
    squared Mahalanobis distances returned beside it."""
    scores, distances = [], []
    for k, (m, c) in enumerate(zip(means, covariances)):
        c = np.asarray(c, dtype=np.float64)
        sign, logdet = np.linalg.slogdet(c)
        if sign <= 0:
            raise ValueError("A class's covariance matrix is singular; it needs more samples than bands, spread out.")
        d = x - np.asarray(m)
        mahalanobis = np.einsum("...i,ij,...j->...", d, np.linalg.inv(c), d)
        prior = 0.0 if priors is None else math.log(priors[k]) if priors[k] > 0 else -np.inf
        scores.append(prior - 0.5 * logdet - 0.5 * mahalanobis)
        distances.append(mahalanobis)
    return np.stack(scores, axis=-1), np.stack(distances, axis=-1)


def _nearest_mean(x, means):
    d = ((x[..., None, :] - np.asarray(means)) ** 2).sum(axis=-1)
    return np.argmin(d, axis=-1)


def _knn(x, samples, labels, k):
    """Plurality vote of the k nearest samples by Euclidean distance (Cover and Hart 1967); a tie goes to the class
    of the nearest sample among the tied (INFERRED)."""
    samples, labels = np.asarray(samples, dtype=np.float64), np.asarray(labels)
    out = np.empty(x.shape[0], np.int64)
    k = max(1, min(int(k), samples.shape[0]))
    for start in range(0, x.shape[0], 2048):
        chunk = x[start:start + 2048]
        d = ((chunk[:, None, :] - samples[None]) ** 2).sum(axis=-1)
        order = np.argsort(d, axis=1, kind="stable")[:, :k]
        for i, nearest in enumerate(order):
            votes = labels[nearest]
            values, counts = np.unique(votes, return_counts=True)
            tied = set(values[counts == counts.max()].tolist())
            out[start + i] = next(v for v in votes if v in tied)
    return out


def _smo(x, y, c=1.0, tolerance=1e-4, passes=10000):
    """A linear soft-margin support vector machine trained by sequential minimal optimisation (Platt 1998): the dual's
    multipliers are optimised two at a time, the second chosen to maximise |E1 − E2|. Answers the hyperplane (w, b)
    with w·x + b > 0 for y = +1."""
    n = x.shape[0]
    kernel = x @ x.T
    alpha = np.zeros(n)
    b = 0.0
    errors = -y.astype(np.float64)  # f(x) − y with f = 0

    def take(i, j):
        nonlocal b
        if i == j:
            return False
        ai, aj = alpha[i], alpha[j]
        if y[i] != y[j]:
            low, high = max(0.0, aj - ai), min(c, c + aj - ai)
        else:
            low, high = max(0.0, ai + aj - c), min(c, ai + aj)
        if low >= high:
            return False
        eta = kernel[i, i] + kernel[j, j] - 2 * kernel[i, j]
        if eta <= 1e-12:
            return False
        new_j = min(high, max(low, aj + y[j] * (errors[i] - errors[j]) / eta))
        if abs(new_j - aj) < 1e-8 * (new_j + aj + 1e-8):
            return False
        new_i = ai + y[i] * y[j] * (aj - new_j)
        b1 = b - errors[i] - y[i] * (new_i - ai) * kernel[i, i] - y[j] * (new_j - aj) * kernel[i, j]
        b2 = b - errors[j] - y[i] * (new_i - ai) * kernel[i, j] - y[j] * (new_j - aj) * kernel[j, j]
        new_b = b1 if 0 < new_i < c else b2 if 0 < new_j < c else (b1 + b2) / 2
        errors[:] += y[i] * (new_i - ai) * kernel[i] + y[j] * (new_j - aj) * kernel[j] + (new_b - b)
        alpha[i], alpha[j], b = new_i, new_j, new_b
        return True

    def examine(i):
        r = errors[i] * y[i]
        if not ((r < -tolerance and alpha[i] < c) or (r > tolerance and alpha[i] > 0)):
            return False
        if take(i, int(np.argmax(np.abs(errors[i] - errors)))):
            return True
        bound = (alpha > 0) & (alpha < c)
        order = np.roll(np.arange(n), -i - 1)
        for j in np.concatenate([order[bound[order]], order[~bound[order]]]):
            if take(i, int(j)):
                return True
        return False

    # Platt's outer loop: a pass over every example, then passes over the non-bound ones until none changes, then
    # every example again, until a pass over every example changes nothing. Measured against ArcGIS Pro 3.7
    # (2026-10-31, ADR-243 condition 2): fifty plain passes over every example, at a KKT tolerance of 1e-3, stopped
    # short of the optimum, at twice the optimal objective for one pair of classes. The tolerance is now 1e-4.
    examine_all, sweeps = True, 0
    while sweeps < passes:
        sweeps += 1
        if examine_all:
            changed = sum(examine(i) for i in range(n))
        else:
            changed = sum(examine(int(i)) for i in np.nonzero((alpha > 0) & (alpha < c))[0])
        if examine_all:
            if changed == 0:
                break
            examine_all = False
        elif changed == 0:
            examine_all = True
    w = (alpha * y) @ x
    return w, b


def _svm_vote(x, machines, values):
    votes = np.zeros(x.shape[:-1] + (len(values),), np.int64)
    margin = np.zeros_like(votes, dtype=np.float64)
    index = {v: i for i, v in enumerate(values)}
    for m in machines:
        f = x @ np.asarray(m["weights"]) + m["bias"]
        first, second = index[m["first"]], index[m["second"]]
        votes[..., first] += f > 0
        votes[..., second] += f <= 0
        margin[..., first] += f
        margin[..., second] -= f
    # A tie in votes goes to the higher summed margin, and then to the lower class value (INFERRED).
    best = votes.max(axis=-1, keepdims=True)
    return np.argmax(np.where(votes == best, margin, -np.inf), axis=-1)


# ---- the training tools


ML_METHOD = ("Gaussian maximum likelihood classification with equal priors",
             "Richards and Jia 2006, Remote Sensing Digital Image Analysis, 4th ed., §8.3")
ISO_METHOD = ("isodata clustering: migrating means with cluster elimination and merging",
              "Ball and Hall 1965, ISODATA, Stanford Research Institute; Tou and Gonzalez 1974, Pattern Recognition "
              "Principles, §3.3.6; How Iso Cluster works")
KNN_METHOD = ("k-nearest-neighbour plurality vote by Euclidean distance",
              "Cover and Hart 1967, IEEE Trans. Information Theory 13(1)")
SVM_METHOD = ("one-against-one linear soft-margin support vector machines (C = 1) on standardised attributes, trained "
              "by sequential minimal optimisation",
              "Cortes and Vapnik 1995, Machine Learning 20(3); Platt 1998, MSR-TR-98-14; Hsu and Lin 2002, IEEE Trans. "
              "Neural Networks 13(2)")


def TrainMaximumLikelihoodClassifier(in_raster, in_training_features, out_classifier_definition,
                                     in_additional_raster=None, used_attributes=None, dimension_value_field=None):
    """EQUIVALENT: the training of Gaussian maximum likelihood (Richards and Jia, §8.3): each class's mean vector and
    covariance matrix (dividing by n − 1) over the cells of its training samples, written to grapy's .ecd (see the
    module's docstring). Classify Raster then assigns each cell to the class of highest likelihood, equal priors.

    Refused: ``used_attributes`` (segment attributes) and ``dimension_value_field`` (change-analysis rasters)."""
    tool = "TrainMaximumLikelihoodClassifier"
    _refuse_environment(f"arcpy.sa.{tool}")
    _segment_parameters(tool, used_attributes, dimension_value_field)
    data, mask, grid, extra = _stack(in_raster, in_additional_raster, tool)
    labels, names = _training(in_training_features, grid, tool)
    x, y = _samples(data, mask, labels, None)
    classes, means, covariances = [], [], []
    for value in sorted(set(y.tolist())):
        rows = x[y == value]
        if rows.shape[0] <= x.shape[1]:
            raise ValueError(f"{tool}: class {value} has {rows.shape[0]} cells, not more than its {x.shape[1]} bands, so "
                             "its covariance matrix cannot be estimated.")
        classes.append({"value": int(value), "name": names[value], "count": int(rows.shape[0])})
        means.append(rows.mean(axis=0).tolist())
        covariances.append(np.cov(rows, rowvar=False).reshape(x.shape[1], x.shape[1]).tolist())
    return _write_ecd(out_classifier_definition, "MaximumLikelihood", data.shape[-1] - extra, extra, classes,
                      means=means, covariances=covariances)


def TrainIsoClusterClassifier(in_raster, max_classes, out_classifier_definition, in_additional_raster=None,
                              max_iterations=20, min_samples_per_cluster=20, skip_factor=10, used_attributes=None,
                              max_merge_per_iter=5, max_merge_distance=0.5):
    """EQUIVALENT: isodata clustering (Ball and Hall 1965; Tou and Gonzalez 1974, §3.3.6), as How Iso Cluster works
    describes it: every sampled cell is assigned to the nearest of the cluster means (Euclidean), the means are
    recomputed, and the two steps repeat up to ``max_iterations`` times or until no cell changes cluster. After each
    pass, at most ``max_merge_per_iter`` pairs of clusters whose means are closer than ``max_merge_distance`` are
    merged, closest first. At the end, clusters of fewer than ``min_samples_per_cluster`` cells are eliminated and
    their cells reassigned. The surviving means, numbered 1, 2 … in order of their first band's mean, are written to
    grapy's .ecd; Classify Raster assigns each cell to the nearest.

    Refused: ``used_attributes`` (segment attributes).

    INFERRED: the ``max_classes`` initial means lie evenly along the line from m − s to m + s, m and s the samples'
    mean and standard deviation per band (Tou and Gonzalez's seeding); merge distances are measured on attributes
    divided by their standard deviation, so the page's "0 to 5" is a number of standard deviations; ``skip_factor``
    keeps every n-th valid cell in row-major order; the defaults of max_iterations (20), skip_factor (10),
    max_merge_per_iter (5) and max_merge_distance (0.5), which the Python page does not state, are those of the tool's
    dialog; a merged mean is the count-weighted mean of the two."""
    tool = "TrainIsoClusterClassifier"
    _refuse_environment(f"arcpy.sa.{tool}")
    _segment_parameters(tool, used_attributes, None)
    data, mask, grid, extra = _stack(in_raster, in_additional_raster, tool)
    k = int(max_classes)
    if k < 2:
        raise ValueError(f"{tool}: max_classes must be at least 2.")
    skip = max(1, int(skip_factor if _given(skip_factor) else 10))
    x = data[~mask][::skip]
    if x.shape[0] < k:
        raise ValueError(f"{tool}: the raster has fewer sampled cells than classes.")
    means = _isodata(x, k, int(max_iterations if _given(max_iterations) else 20),
                     int(min_samples_per_cluster if _given(min_samples_per_cluster) else 20),
                     int(max_merge_per_iter if _given(max_merge_per_iter) else 5),
                     float(max_merge_distance if _given(max_merge_distance) else 0.5))
    counts = np.bincount(_nearest_mean(x, means), minlength=len(means))
    classes = [{"value": i + 1, "name": f"Class_{i + 1}", "count": int(c)} for i, c in enumerate(counts)]
    return _write_ecd(out_classifier_definition, "IsoCluster", data.shape[-1] - extra, extra, classes,
                      means=[m.tolist() for m in means])


def _isodata(x, k, iterations, minimum, merges, distance):
    centre, spread = x.mean(axis=0), x.std(axis=0)
    scale = np.where(spread > 0, spread, 1.0)
    steps = np.linspace(-1.0, 1.0, k) if k > 1 else np.zeros(1)
    means = [centre + t * spread for t in steps]
    assigned = None
    for _ in range(max(1, iterations)):
        nearest = _nearest_mean(x, means)
        kept = [i for i in range(len(means)) if (nearest == i).any()]
        means = [x[nearest == i].mean(axis=0) for i in kept]
        counts = [int((nearest == i).sum()) for i in kept]
        merged = 0
        while merged < merges and len(means) > 1:
            scaled = np.array(means) / scale
            d = np.sqrt(((scaled[:, None] - scaled[None]) ** 2).sum(axis=-1))
            d[np.tril_indices(len(means))] = np.inf
            i, j = np.unravel_index(int(np.argmin(d)), d.shape)
            if d[i, j] >= distance:
                break
            total = counts[i] + counts[j]
            means[i] = (means[i] * counts[i] + means[j] * counts[j]) / total
            counts[i] = total
            del means[j], counts[j]
            merged += 1
        now = _nearest_mean(x, means)
        if assigned is not None and merged == 0 and len(now) == len(assigned) and (now == assigned).all():
            break
        assigned = now
    while True:
        nearest = _nearest_mean(x, means)
        counts = np.bincount(nearest, minlength=len(means))
        small = [i for i, c in enumerate(counts) if c < minimum]
        if not small or len(means) - len(small) < 1:
            break
        means = [m for i, m in enumerate(means) if i not in small]
        nearest = _nearest_mean(x, means)
        means = [x[nearest == i].mean(axis=0) for i in range(len(means)) if (nearest == i).any()]
    return sorted(means, key=lambda m: tuple(m.tolist()))


def TrainKNearestNeighborClassifier(in_raster, in_training_features, out_classifier_definition,
                                    in_additional_raster=None, kNN=1, max_samples_per_class=1000, used_attributes=None,
                                    dimension_value_field=None):
    """EQUIVALENT: the k-nearest-neighbour rule (Cover and Hart 1967), as the page describes it: the training cells
    (at most ``max_samples_per_class`` of each class; all when it is 0 or less) are written to grapy's .ecd, and
    Classify Raster gives each cell the class of a plurality vote of its ``kNN`` nearest samples by Euclidean distance
    on the band values.

    Refused: ``used_attributes`` (segment attributes) and ``dimension_value_field`` (change-analysis rasters).

    INFERRED: which samples are kept when a class has more (a seeded draw); a tied vote goes to the class of the
    nearest of the tied samples; distances are on the raw band values."""
    tool = "TrainKNearestNeighborClassifier"
    _refuse_environment(f"arcpy.sa.{tool}")
    _segment_parameters(tool, used_attributes, dimension_value_field)
    data, mask, grid, extra = _stack(in_raster, in_additional_raster, tool)
    labels, names = _training(in_training_features, grid, tool)
    x, y = _samples(data, mask, labels, max_samples_per_class if _given(max_samples_per_class) else 1000)
    classes = [{"value": int(v), "name": names[v], "count": int((y == v).sum())} for v in sorted(set(y.tolist()))]
    return _write_ecd(out_classifier_definition, "KNearestNeighbor", data.shape[-1] - extra, extra, classes,
                      k=int(kNN if _given(kNN) else 1), samples=x.tolist(), labels=[int(v) for v in y])


def TrainSupportVectorMachineClassifier(in_raster, in_training_features, out_classifier_definition,
                                        in_additional_raster=None, max_samples_per_class=500, used_attributes=None,
                                        dimension_value_field=None):
    """EQUIVALENT: support vector machines (Cortes and Vapnik 1995) — the page's .ecd holds "hyperplane vectors", so
    linear ones: for every pair of classes a soft-margin hyperplane (C = 1) is trained by sequential minimal
    optimisation (Platt 1998) on attributes standardised to mean 0 and standard deviation 1, and Classify Raster
    gives each cell the class most pairs vote for (one against one, Hsu and Lin 2002). At most
    ``max_samples_per_class`` training cells of each class are used, all when it is 0 or less.

    Refused: ``used_attributes`` (segment attributes) and ``dimension_value_field`` (change-analysis rasters).

    INFERRED: the linear kernel and C = 1; the standardisation; which samples are kept (a seeded draw); a tied vote
    goes to the higher summed margin, then the lower class value."""
    tool = "TrainSupportVectorMachineClassifier"
    _refuse_environment(f"arcpy.sa.{tool}")
    _segment_parameters(tool, used_attributes, dimension_value_field)
    data, mask, grid, extra = _stack(in_raster, in_additional_raster, tool)
    labels, names = _training(in_training_features, grid, tool)
    x, y = _samples(data, mask, labels, max_samples_per_class if _given(max_samples_per_class) else 500)
    values = sorted(set(y.tolist()))
    if len(values) < 2:
        raise ValueError(f"{tool}: the training samples hold one class; a support vector machine separates two or more.")
    centre, spread = x.mean(axis=0), x.std(axis=0)
    scale = np.where(spread > 0, spread, 1.0)
    z = (x - centre) / scale
    machines = []
    for a in range(len(values)):
        for b in range(a + 1, len(values)):
            pick = (y == values[a]) | (y == values[b])
            w, bias = _smo(z[pick], np.where(y[pick] == values[a], 1.0, -1.0))
            machines.append({"first": int(values[a]), "second": int(values[b]), "weights": w.tolist(),
                             "bias": float(bias)})
    classes = [{"value": int(v), "name": names[v], "count": int((y == v).sum())} for v in values]
    return _write_ecd(out_classifier_definition, "SupportVectorMachine", data.shape[-1] - extra, extra, classes,
                      centre=centre.tolist(), scale=scale.tolist(), machines=machines)


# ---- classifying with a definition


def _classify(data, mask, grid, document, tool):
    if data.shape[-1] != document["bands"] + document["additional_bands"]:
        raise ValueError(f"{tool}: the classifier definition was trained on {document['bands']} bands and "
                         f"{document['additional_bands']} additional ones; the inputs given have {data.shape[-1]}.")
    x = data[~mask]
    values = [c["value"] for c in document["classes"]]
    kind = document["classifier"]
    if kind == "DeepLearning":  # DeepLearningModelToEcd's definition (ADR-248): the model classifies the whole grid
        from ._dltools import classify_with_ecd
        index = np.searchsorted(values, classify_with_ecd(data, mask, document, tool)[~mask])
    elif kind == "MaximumLikelihood":
        index = np.argmax(_gaussian_scores(x, document["means"], document["covariances"])[0], axis=-1)
    elif kind == "IsoCluster":
        index = _nearest_mean(x, [np.asarray(m) for m in document["means"]])
    elif kind == "KNearestNeighbor":
        found = _knn(x, document["samples"], document["labels"], document["k"])
        index = np.searchsorted(values, found)
    elif kind == "SupportVectorMachine":
        z = (x - np.asarray(document["centre"])) / np.asarray(document["scale"])
        index = _svm_vote(z, document["machines"], values)
    else:
        raise NotBuiltError(f"{tool}: the classifier '{kind}' in the definition is not one grapy trains.")
    # ArcGIS Pro 3.7 writes each class's 0-based index in the definition as the cell value and the class value beside
    # it in the table, Value, Classvalue, Class_name, Red, Green, Blue (measured 2026-10-31, ADR-243 condition 2).
    out = np.zeros(mask.shape, np.int32)
    out[~mask] = np.asarray(index, dtype=np.int32)
    result = Raster._of(out, mask.copy(), grid)
    from ._raster import _Rat
    rows = []
    for i, c in enumerate(document["classes"]):
        colour = c.get("color") or c.get("colour")
        if not colour or len(colour) < 3:
            # INFERRED: Pro gives a class without a colour a random one; grapy's is drawn from the class value.
            colour = np.random.default_rng(int(c["value"]) & 0xFFFFFFFF).integers(0, 256, 3).tolist()
        rows.append([i, int(c["value"]), str(c["name"]), *(int(v) for v in colour[:3])])
    result._rat = _Rat([("Value", "Integer", 5), ("Classvalue", "Integer", 0), ("Class_name", "String", 2),
                        ("Red", "Integer", 6), ("Green", "Integer", 7), ("Blue", "Integer", 8)], rows)
    return result


def class_values(classified, document):
    """The class value of each cell of ``_classify``'s answer (0 where masked)."""
    values = np.asarray([c["value"] for c in document["classes"]], np.int64)
    return np.where(classified._mask, 0, values[np.clip(classified._array, 0, len(values) - 1)])


def ClassifyRaster(in_raster, in_classifier_definition, in_additional_raster=None):
    """The raster classified by a classifier definition from grapy's Train tools: each cell (with no NoData in any
    band) is given the class its classifier's rule picks — see each Train tool. As ArcGIS Pro 3.7 writes it (measured
    2026-10-31): the cell holds the class's 0-based index among the definition's classes, and the attribute table
    Value, Classvalue, Class_name, Red, Green and Blue (INFERRED: a class without a colour gets one drawn from its
    value, where Pro draws a random one)."""
    tool = "ClassifyRaster"
    _refuse_environment(f"arcpy.sa.{tool}")
    document = _read_ecd(in_classifier_definition, tool)
    data, mask, grid, _ = _stack(in_raster, in_additional_raster, tool)
    return _classify(data, mask, grid, document, tool)


def Classify(raster1, raster2=None, classifier_definition=None):
    """arcpy.sa.Classify / arcpy.ia.Classify, the Classify raster function: Classify Raster's answer, the second raster
    being the additional one, as the function's page says."""
    if not _given(classifier_definition):
        raise ValueError("Classify needs a classifier_definition (.ecd).")
    document = _read_ecd(classifier_definition, "Classify")
    data, mask, grid, _ = _stack(raster1, raster2, "Classify")
    return _classify(data, mask, grid, document, "Classify")


INSPECT_METHOD = ("each training sample's share of cells the classifier definition classifies as its class",
                  "the accuracy score of Inspect Training Samples' page (correct cells over the sample's cells), with "
                  "the classifier definition's own rule")


def InspectTrainingSamples(in_raster, in_training_features, in_classifier_definition, out_training_feature_class,
                           out_misclassified_raster, in_additional_raster=None):
    """EQUIVALENT: as the page describes, the raster is classified with the classifier definition (grapy's .ecd, see
    Classify Raster) and compared with every training sample: the sample's accuracy score is its cells classified as
    its own class over all its cells — a fraction for a polygon, 0 or 1 for a point, as documented. The training
    samples are copied to ``out_training_feature_class`` with the score added; ``out_misclassified_raster`` is NoData
    outside the samples and on their correctly classified cells, and elsewhere holds the class the cell was given,
    as documented.

    INFERRED: the score's field is named ``accuracy``; a polygon's cells are those whose centres it holds, a point's
    the cell under it; a sample with no valid cell scores NoData (null); where samples overlap, the misclassified
    raster compares a cell with the later sample's class."""
    from rasterio.features import rasterize
    from . import da, management
    from ._result import Result
    from .analysis import _out, _schema
    tool = "InspectTrainingSamples"
    _refuse_environment(f"arcpy.sa.{tool}")
    document = _read_ecd(in_classifier_definition, tool)
    data, mask, grid, _ = _stack(in_raster, in_additional_raster, tool)
    classified = _classify(data, mask, grid, document, tool)
    given = class_values(classified, document)
    columns = {c.name.lower(): c.name for c in _schema(in_training_features)[0]}
    if "classvalue" not in columns:
        raise ValueError(f"{tool}: the training samples need a classvalue field, as the training tools document.")
    out = _out(out_training_feature_class)
    management.CopyFeatures(in_training_features, out)
    management.AddField(out, "accuracy", "DOUBLE")
    wrong = np.zeros(mask.shape, bool)
    expected = np.zeros(mask.shape, np.int64)
    with da.UpdateCursor(out, [columns["classvalue"], "accuracy", "SHAPE@"]) as rows:
        for row in rows:
            value, shape = row[0], row[2]
            if shape is None or value is None:
                continue
            geometry = shape._shape
            cells = rasterize([(geometry, 1)], out_shape=mask.shape, transform=grid.transform(), fill=0,
                              dtype="uint8", all_touched=not geometry.geom_type.endswith("Polygon")).astype(bool)
            cells &= ~mask
            expected = np.where(cells, int(value), expected)
            if cells.any():
                row[1] = float((given[cells] == int(value)).sum()) / float(cells.sum())
            else:
                row[1] = None
            rows.updateRow(row)
    wrong = (expected != 0) & (given != expected) & ~mask
    Raster._of(np.where(wrong, given, 0).astype(np.int32), ~wrong, grid).save(out_misclassified_raster)
    return Result(out, out_misclassified_raster)


# ---- Maximum Likelihood Classification, from a signature file


_REJECT = (0.0, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 0.75, 0.9, 0.95, 0.975, 0.99, 0.995)


def _read_gsg(path, tool, covariance=True):
    """A signature file in the layout How Create Signatures works and How Iso Cluster works show by example: after the
    header comments, per class a line "id cells [name]", then "# Means" and the means, then "# Covariance" and one row
    per layer led by its layer number."""
    path = _path(path)
    with open(path, encoding="utf-8", errors="replace") as handle:
        lines = [line.rstrip("\n") for line in handle]
    classes, current, section = [], None, None
    expect_class = False
    for line in lines:
        text = line.strip()
        if not text:
            continue
        if text.startswith("#"):
            lowered = text.lower()
            if lowered.startswith("# class id"):
                expect_class, section = True, None
            elif lowered.startswith("# means"):
                section = "means"
                rest = text[len("# means"):].split()
                if rest and current is not None:
                    current["means"] += [float(v) for v in rest]
            elif lowered.startswith("# covariance"):
                section = "covariance"
            elif lowered.startswith("# layers"):
                section = None
            continue
        if text.startswith("/*"):
            continue
        parts = text.split()
        if expect_class:
            current = {"value": int(float(parts[0])), "count": int(float(parts[1])),
                       "name": " ".join(parts[2:]) or None, "means": [], "covariance": []}
            classes.append(current)
            expect_class = False
        elif section == "means" and current is not None:
            current["means"] += [float(v) for v in parts]
        elif section == "covariance" and current is not None:
            current["covariance"].append([float(v) for v in parts[1:]])
    if not classes:
        raise ValueError(f"{tool}: '{path}' holds no class signatures.")
    for c in classes:
        if covariance and not c["covariance"]:
            raise ValueError(f"{tool}: class {c['value']} of '{path}' has means only; maximum likelihood needs the "
                             "covariance matrices (Create Signatures' COVARIANCE).")
    return classes


def read_priors(path, values, tool):
    """The a priori probability file Class Probability's page documents: on each line a class id and its probability
    (lines that do not start with a number are skipped); each class left out takes an equal share of what remains of
    1. Answers the priors in the order of ``values``."""
    given = {}
    with open(_path(path), encoding="utf-8", errors="replace") as handle:
        for line in handle:
            parts = line.replace(",", " ").split()
            if len(parts) < 2:
                continue
            try:
                given[int(float(parts[0]))] = float(parts[1])
            except ValueError:
                continue
    if any(p < 0 for p in given.values()):
        raise ValueError(f"{tool}: a priori probabilities cannot be negative.")
    total = sum(p for v, p in given.items() if v in values)
    if total > 1 + 1e-9:
        raise ValueError(f"{tool}: the a priori probabilities sum to {total}, more than 1.")
    missing = [v for v in values if v not in given]
    share = (1 - total) / len(missing) if missing else 0.0
    return [given.get(v, share) for v in values]


def class_priors(classes, kind, in_a_priori_file, tool):
    """None for EQUAL; for SAMPLE each class's share of the cells; for FILE the a priori file's."""
    if kind == "EQUAL":
        return None
    if kind == "SAMPLE":
        total = sum(c["count"] for c in classes)
        return [c["count"] / total for c in classes]
    if not _given(in_a_priori_file):
        raise ValueError(f"{tool}: FILE priors need in_a_priori_file.")
    return read_priors(in_a_priori_file, [c["value"] for c in classes], tool)


@np.errstate(all="ignore")
def MLClassify(in_raster_bands, in_signature_file, reject_fraction="0.0", a_priori_probabilities="EQUAL",
               in_a_priori_file=None, out_confidence_raster=None):
    """EQUIVALENT: Gaussian maximum likelihood classification with Bayes' priors (Richards and Jia, §8.3), as How
    Maximum Likelihood Classification works describes: each cell goes to the class of highest
    ln p(ω) − ½ ln|Σ| − ½ (x − m)ᵀ Σ⁻¹ (x − m), from the signature file's means and covariances; priors are EQUAL, or
    SAMPLE (proportional to each class's number of cells). The likelihood that a cell belongs to its class is the
    chi-squared tail probability of its squared Mahalanobis distance with as many degrees of freedom as bands (Richards
    and Jia, §8.3.5, thresholds); a cell below ``reject_fraction`` is NoData. The confidence raster's 14 levels are the
    14 reject fractions: level 1 is a likelihood of at least 0.995, level 2 at least 0.99, … level 14 below 0.005.

    FILE priors are read from ``in_a_priori_file`` in the layout Class Probability's page gives: a class id and its
    probability on each line, the classes left out sharing what remains of 1 equally.

    INFERRED: the signature file's layout is read from the examples Esri's pages print (the layout is not otherwise
    specified); the a priori file is the one Class Probability documents (this tool's page does not repeat it); the
    likelihood measure; class values come from the file's class ids; a class of prior 0 is never chosen."""
    tool = "MLClassify"
    _refuse_environment(f"arcpy.sa.{tool}")
    from scipy.stats import chi2
    kind = str(a_priori_probabilities or "EQUAL").upper()
    if kind not in ("EQUAL", "SAMPLE", "FILE"):
        _refuse(tool, "a_priori_probabilities", a_priori_probabilities)
    reject = float(reject_fraction if _given(reject_fraction) else 0.0)
    if not any(abs(reject - r) < 1e-12 for r in _REJECT):
        raise ValueError(f"{tool}: reject_fraction must be one of {', '.join(str(r) for r in _REJECT)}.")
    inputs = in_raster_bands if isinstance(in_raster_bands, (list, tuple)) else [in_raster_bands]
    rasters = [_open_bands(r, tool) for r in inputs]
    grid = rasters[0]._grid
    for r in rasters[1:]:
        if not r._grid.same(grid):
            _refuse(tool, "in_raster_bands", r, "bands on different grids need resampling")
    data = np.stack([np.asarray(a, np.float64) for r in rasters for a in r._arrays], axis=-1)
    mask = np.logical_or.reduce([m for r in rasters for m in r._masks])
    classes = _read_gsg(in_signature_file, tool)
    if any(len(c["means"]) != data.shape[-1] for c in classes):
        raise ValueError(f"{tool}: the signature file's classes have {len(classes[0]['means'])} layers; "
                         f"the input has {data.shape[-1]} bands.")
    priors = class_priors(classes, kind, in_a_priori_file, tool)
    x = data[~mask]
    scores, distances = _gaussian_scores(x, [c["means"] for c in classes], [c["covariance"] for c in classes], priors)
    best = np.argmax(scores, axis=-1)
    likelihood = chi2.sf(distances[np.arange(best.size), best], data.shape[-1])
    values = np.array([c["value"] for c in classes], np.int32)
    out = np.zeros(mask.shape, np.int32)
    out[~mask] = values[best]
    rejected = np.zeros(mask.shape, bool)
    rejected[~mask] = likelihood < reject
    if _given(out_confidence_raster):
        thresholds = np.array(sorted(_REJECT[1:], reverse=True))  # 0.995 … 0.005
        level = np.zeros(mask.shape, np.int32)
        level[~mask] = 1 + (thresholds[None, :] > likelihood[:, None]).sum(axis=1)
        Raster._of(np.where(mask, 0, level), mask.copy(), grid).save(out_confidence_raster)
    final = mask | rejected
    return Raster._of(np.where(final, 0, out), final, grid)


# ---- pan sharpening


PANSHARPEN_METHOD = ("Brovey, Esri, simple mean and IHS fusion as Fundamentals of pan sharpening writes them, and "
                     "Gram-Schmidt spectral sharpening",
                     "Esri, Fundamentals of pan sharpening; Laben and Brower 2000, US Patent 6,011,875; Tu et al. 2001, "
                     "Information Fusion 2(3) (the additive IHS)")

# The weights Fundamentals of pan sharpening suggests for Gram-Schmidt, red, green, blue and infrared.
_SENSOR_WEIGHTS = {"GEOEYE": (0.6, 0.85, 0.75, 0.3), "GEOEYE-1": (0.6, 0.85, 0.75, 0.3),
                   "IKONOS": (0.85, 0.65, 0.35, 0.9), "QUICKBIRD": (0.85, 0.7, 0.35, 1.0),
                   "WORLDVIEW-2": (0.95, 0.7, 0.5, 1.0)}


def _resample_bilinear(array, mask, source, target):
    """``array`` on ``source`` sampled at the centres of ``target`` by bilinear interpolation, edges clamped; a sample
    touching NoData is NoData."""
    xs, ys = target.centres()
    fx = (xs - source.left) / source.width - 0.5
    fy = (source.top - ys) / source.height - 0.5
    fx = np.clip(fx, 0, source.columns - 1)
    fy = np.clip(fy, 0, source.rows - 1)
    c0 = np.minimum(np.floor(fx).astype(int), max(source.columns - 2, 0))
    r0 = np.minimum(np.floor(fy).astype(int), max(source.rows - 2, 0))
    c1 = np.minimum(c0 + 1, source.columns - 1)
    r1 = np.minimum(r0 + 1, source.rows - 1)
    tx, ty = (fx - c0)[None, :], (fy - r0)[:, None]
    a = np.asarray(array, np.float64)
    top = a[np.ix_(r0, c0)] * (1 - tx) + a[np.ix_(r0, c1)] * tx
    bottom = a[np.ix_(r1, c0)] * (1 - tx) + a[np.ix_(r1, c1)] * tx
    out = top * (1 - ty) + bottom * ty
    nodata = mask[np.ix_(r0, c0)] | mask[np.ix_(r0, c1)] | mask[np.ix_(r1, c0)] | mask[np.ix_(r1, c1)]
    return out, nodata


def _weights(weights, count):
    if not _given(weights):
        values = [0.166, 0.167, 0.167, 0.5]
    elif isinstance(weights, str):
        values = [float(v) for v in weights.replace(",", " ").replace(";", " ").split()]
    else:
        values = [float(v) for v in weights]
    if len(values) < count:
        raise ValueError(f"Pansharpen: weights for {count} bands are needed (red, green, blue, infrared).")
    values = np.array(values[:count])
    if values.sum() <= 0:
        raise ValueError("Pansharpen: the weights must sum to more than 0.")
    return values / values.sum()


def Pansharpen(pan_raster, ms_raster, ir_raster=None, fourth_band_of_ms_is_ir=True, weights=None, type="ESRI",
               sensor=None):
    """EQUIVALENT: pan sharpening by the methods Fundamentals of pan sharpening names. The multispectral bands are
    resampled to the panchromatic grid; with P the pan, R, G, B its first three bands and I the infrared (its fourth
    band, or ``ir_raster``), and the weights normalised to sum to 1, as the page says they are:

    - ``ESRI``: ADJ = P − (RW·R + GW·G + BW·B + IW·I); each band + ADJ, as the page writes it.
    - ``Brovey``: DNF = (P − IW·I) / (RW·R + GW·G + BW·B); each band · DNF, as the page writes it.
    - ``SimpleMean`` (``Mean``): each band's mean with P, 0.5·(band + P), as the page writes it.
    - ``IHS``: the intensity (R + G + B)/3 replaced by P − IW·I, in the additive form of the IHS transform (Tu et al.
      2001): each band + (P − IW·I) − (R + G + B)/3.
    - ``Gram-Schmidt``: Laben and Brower's (2000) method — a simulated pan, the weighted mean of the bands, leads the
      Gram-Schmidt orthogonalisation of the bands; it is replaced by P matched to its mean and standard deviation,
      and the bands are transformed back. ``sensor`` sets the weights for the four sensors the page suggests weights
      for (GeoEye, IKONOS, QuickBird, WorldView-2).

    Refused: another ``sensor``, whose weights the documentation does not give (pass ``weights`` instead).

    INFERRED: bilinear resampling; the first three multispectral bands are red, green and blue in that order; the
    output holds a band for each band used (three, or four with the infrared), as 32-bit floating point; the infrared
    band of IHS and SimpleMean is sharpened with the same formula as the colour bands; a cell where the Brovey
    denominator is 0 is NoData."""
    tool = "Pansharpen"
    _refuse_environment(f"arcpy.sa.{tool}")
    pan = as_raster(pan_raster, tool)
    ms = _open_bands(ms_raster, tool)
    if pan._grid.srid != ms._grid.srid:
        _refuse(tool, "ms_raster", ms_raster, "rasters in different coordinate systems need projecting first")
    if len(ms._arrays) < 3:
        raise ValueError(f"{tool}: the multispectral raster needs red, green and blue bands.")
    grid = pan._grid
    bands, masks = [], []
    for k in range(3):
        b, m = _resample_bilinear(ms._arrays[k], ms._masks[k], ms._grid, grid)
        bands.append(b)
        masks.append(m)
    if _given(ir_raster):
        ir = as_raster(ir_raster, tool)
        b, m = _resample_bilinear(ir._array, ir._mask, ir._grid, grid)
        bands.append(b)
        masks.append(m)
    elif str(fourth_band_of_ms_is_ir).upper() not in ("FALSE", "0", "NONE") and fourth_band_of_ms_is_ir is not False \
            and len(ms._arrays) >= 4:
        b, m = _resample_bilinear(ms._arrays[3], ms._masks[3], ms._grid, grid)
        bands.append(b)
        masks.append(m)
    method = str(type or "ESRI").upper().replace("_", "-").replace(" ", "")
    if method in ("GRAM-SCHMIDT", "GRAMSCHMIDT") and _given(sensor) and str(sensor).upper() not in ("UNKNOWN",):
        key = str(sensor).upper().replace(" ", "")
        if key not in _SENSOR_WEIGHTS:
            _refuse(tool, "sensor", sensor, "the documentation gives Gram-Schmidt weights for GeoEye, IKONOS, "
                                            "QuickBird and WorldView-2 only; pass weights")
        weights = _SENSOR_WEIGHTS[key]
    w = _weights(weights, len(bands))
    p = np.asarray(pan._array, np.float64)
    mask = np.logical_or.reduce([pan._mask, *masks])
    has_ir = len(bands) == 4
    infrared = bands[3] if has_ir else 0.0
    iw = w[3] if has_ir else 0.0
    with np.errstate(all="ignore"):
        if method == "ESRI":
            adjust = p - sum(wk * b for wk, b in zip(w, bands))
            out = [b + adjust for b in bands]
        elif method == "BROVEY":
            dnf = (p - iw * infrared) / (w[0] * bands[0] + w[1] * bands[1] + w[2] * bands[2])
            mask = mask | ~np.isfinite(dnf)
            out = [b * dnf for b in bands]
        elif method in ("SIMPLEMEAN", "MEAN"):
            out = [0.5 * (b + p) for b in bands]
        elif method == "IHS":
            shift = (p - iw * infrared) - (bands[0] + bands[1] + bands[2]) / 3.0
            out = [b + shift for b in bands]
        elif method in ("GRAM-SCHMIDT", "GRAMSCHMIDT"):
            out = _gram_schmidt(bands, w, p, mask)
        else:
            _refuse(tool, "type", type)
    out = [np.where(mask, 0, b).astype(np.float32) for b in out]
    return Raster._of_bands(out, [mask.copy() for _ in out], grid)


def _gram_schmidt(bands, w, p, mask):
    valid = ~mask
    simulated = sum(wk * b for wk, b in zip(w, bands))
    components, coefficients, means = [], [], []
    first = simulated - simulated[valid].mean()
    components.append(first)
    for b in bands:
        mean = b[valid].mean()
        residual = b - mean
        row = []
        for g in components:
            variance = (g[valid] ** 2).mean()
            phi = ((b[valid] - mean) * g[valid]).mean() / variance if variance > 0 else 0.0
            residual = residual - phi * g
            row.append(phi)
        components.append(residual)
        coefficients.append(row)
        means.append(mean)
    sd_first, sd_pan = first[valid].std(), p[valid].std()
    matched = (p - p[valid].mean()) * (sd_first / sd_pan if sd_pan > 0 else 0.0)
    components[0] = matched
    out = []
    for k in range(len(bands)):
        value = components[k + 1] + means[k]
        for l, phi in enumerate(coefficients[k]):
            value = value + phi * components[l]
        out.append(value)
    return out


# ---- region growing


REGION_GROW_METHOD = ("seeded region growing from each seed point, within its radius and spectral threshold",
                      "Adams and Bischof 1994, IEEE Trans. Pattern Analysis and Machine Intelligence 16(6)")


def RegionGrow(raster, seed_points, max_growth_radius_field, similarity_threshold_field, fill_value_field=None):
    """EQUIVALENT: seeded region growing (Adams and Bischof 1994): from each seed point's cell, the region takes in
    neighbouring cells whose centres lie within the seed's maximum growth radius of the seed point and whose band
    values are within its similarity threshold — Euclidean distance in spectral space, as documented — of the
    region's mean, the region's mean updated as it grows. The region's cells take the seed's fill value in every band,
    as documented; every other cell keeps its value.

    INFERRED: eight-connected growth, nearest-first in spectral distance (Adams and Bischof's ordering); seeds are
    grown in the order the cursor reads them and a cell already taken stays with the first region; without
    ``fill_value_field`` a region takes its mean, per band; a seed on NoData or off the raster grows nothing."""
    import heapq
    from . import da
    from .analysis import _schema
    tool = "RegionGrow"
    _refuse_environment(f"arcpy.ia.{tool}")
    source = _open_bands(raster, tool)
    grid = source._grid
    data = np.stack([np.asarray(a, np.float64) for a in source._arrays], axis=-1)
    mask = np.logical_or.reduce(source._masks)
    columns = {c.name.lower(): c.name for c in _schema(seed_points)[0]}

    def field(name, required=True):
        if not _given(name):
            if required:
                raise ValueError(f"{tool} needs the field {name}.")
            return None
        if str(name).lower() not in columns:
            raise ValueError(f"{tool}: '{name}' is not a field of {seed_points}.")
        return columns[str(name).lower()]

    names = [field(max_growth_radius_field), field(similarity_threshold_field)]
    fill_name = field(fill_value_field, required=False)
    if fill_name:
        names.append(fill_name)
    out = [np.asarray(a).copy() for a in source._arrays]
    taken = np.zeros(mask.shape, bool)
    xs, ys = grid.centres()
    with da.SearchCursor(seed_points, [*names, "SHAPE@"]) as rows:
        seeds = [row for row in rows if row[-1] is not None]
    for row in seeds:
        radius, threshold = float(row[0]), float(row[1])
        fill = row[2] if fill_name else None
        point = row[-1]._shape
        cell = grid.cell_of(point.x, point.y)
        if cell is None or mask[cell] or taken[cell]:
            continue
        members = [cell]
        total = data[cell].copy()
        inside = np.zeros(mask.shape, bool)
        inside[cell] = True
        queue = []

        def push(r, c):
            for dr in (-1, 0, 1):
                for dc in (-1, 0, 1):
                    rr, cc = r + dr, c + dc
                    if (dr or dc) and 0 <= rr < grid.rows and 0 <= cc < grid.columns and not inside[rr, cc] \
                            and not mask[rr, cc] and not taken[rr, cc] \
                            and math.hypot(xs[cc] - point.x, ys[rr] - point.y) <= radius:
                        inside[rr, cc] = True
                        mean = total / len(members)
                        heapq.heappush(queue, (float(np.linalg.norm(data[rr, cc] - mean)), rr, cc))

        push(*cell)
        while queue:
            _, r, c = heapq.heappop(queue)
            mean = total / len(members)
            if np.linalg.norm(data[r, c] - mean) > threshold:
                inside[r, c] = False
                continue
            members.append((r, c))
            total += data[r, c]
            push(r, c)
        region = np.zeros(mask.shape, bool)
        for r, c in members:
            region[r, c] = True
        taken |= region
        mean = total / len(members)
        for k in range(len(out)):
            value = float(fill) if fill is not None else mean[k]
            if np.issubdtype(out[k].dtype, np.integer):
                value = int(round(value))
            out[k][region] = value
    if len(out) == 1:
        return Raster._of(out[0], source._masks[0].copy(), grid)
    return Raster._of_bands(out, [m.copy() for m in source._masks], grid)


# ---- mean shift segmentation


MEAN_SHIFT_METHOD = ("mean shift filtering and segmentation with flat kernels in the joint spatial-range domain",
                     "Comaniciu and Meer 2002, IEEE Trans. Pattern Analysis and Machine Intelligence 24(5), §4")


def _mean_shift(values, mask, hs, hr, iterations=20):
    """Comaniciu and Meer's mean shift filtering (2002, §4.1) with flat kernels: each cell's point in the joint
    domain moves to the mean of the cells within ``hs`` cells and ``hr`` in value of it, until it moves less than
    half a cell and 0.5 in value."""
    rows, columns, bands = values.shape
    py, px = np.indices((rows, columns)).astype(np.float64)
    colour = values.copy()
    offsets = [(dr, dc) for dr in range(-hs, hs + 1) for dc in range(-hs, hs + 1) if dr * dr + dc * dc <= hs * hs]
    active = ~mask
    for _ in range(iterations):
        if not active.any():
            break
        ry, rx = np.rint(py).astype(int), np.rint(px).astype(int)
        sum_y = np.zeros((rows, columns))
        sum_x = np.zeros((rows, columns))
        sum_c = np.zeros_like(values)
        count = np.zeros((rows, columns))
        for dr, dc in offsets:
            ny, nx = ry + dr, rx + dc
            inside = (ny >= 0) & (ny < rows) & (nx >= 0) & (nx < columns)
            ny, nx = np.clip(ny, 0, rows - 1), np.clip(nx, 0, columns - 1)
            near = inside & ~mask[ny, nx] & active
            near &= ((ny - py) ** 2 + (nx - px) ** 2) <= hs * hs
            near &= ((values[ny, nx] - colour) ** 2).sum(axis=-1) <= hr * hr
            sum_y += np.where(near, ny, 0)
            sum_x += np.where(near, nx, 0)
            sum_c += np.where(near[..., None], values[ny, nx], 0)
            count += near
        moved = count > 0
        new_y = np.where(moved, sum_y / np.maximum(count, 1), py)
        new_x = np.where(moved, sum_x / np.maximum(count, 1), px)
        new_c = np.where(moved[..., None], sum_c / np.maximum(count, 1)[..., None], colour)
        shift = np.hypot(new_y - py, new_x - px)
        change = np.sqrt(((new_c - colour) ** 2).sum(axis=-1))
        py, px, colour = np.where(active, new_y, py), np.where(active, new_x, px), np.where(active[..., None], new_c,
                                                                                               colour)
        active &= (shift >= 0.5) | (change >= 0.5)
    return py, px, colour


def _segments(values, mask, py, px, colour, hs, hr, min_size):
    """Comaniciu and Meer's segmentation (§4.2): neighbouring cells whose converged points are within ``hs`` cells and
    ``hr`` in value join one region; then regions smaller than ``min_size`` cells are merged, smallest first, into the
    neighbouring region of nearest mean value."""
    rows, columns = mask.shape
    parent = np.arange(rows * columns)

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for dr, dc in ((0, 1), (1, 0), (1, 1), (1, -1)):
        r0, r1 = max(0, -dr), rows - max(0, dr)
        c0, c1 = max(0, -dc), columns - max(0, dc)
        a = (slice(r0, r1), slice(c0, c1))
        b = (slice(r0 + dr, r1 + dr), slice(c0 + dc, c1 + dc))
        join = ~mask[a] & ~mask[b]
        join &= ((py[a] - py[b]) ** 2 + (px[a] - px[b]) ** 2) <= hs * hs
        join &= ((colour[a] - colour[b]) ** 2).sum(axis=-1) <= hr * hr
        for r, c in zip(*np.nonzero(join)):
            i = (r + r0) * columns + (c + c0)
            j = (r + r0 + dr) * columns + (c + c0 + dc)
            fi, fj = find(i), find(j)
            if fi != fj:
                parent[max(fi, fj)] = min(fi, fj)
    labels = np.array([find(i) for i in range(rows * columns)]).reshape(rows, columns)
    labels = np.where(mask, -1, labels)
    while True:
        ids, counts = np.unique(labels[~mask], return_counts=True)
        small = [(c, i) for i, c in zip(ids, counts) if c < min_size]
        if not small or len(ids) == 1:
            break
        _, region = min(small)
        inside = labels == region
        grown = np.zeros_like(inside)
        grown[:-1] |= inside[1:]
        grown[1:] |= inside[:-1]
        grown[:, :-1] |= inside[:, 1:]
        grown[:, 1:] |= inside[:, :-1]
        neighbours = set(np.unique(labels[grown & ~inside & ~mask]).tolist())
        if not neighbours:
            break
        mean = values[inside].mean(axis=0)
        best = min(neighbours, key=lambda n: (float(((values[labels == n].mean(axis=0) - mean) ** 2).sum()), n))
        labels[inside] = best
    return labels


def _segment(values, mask, grid, hs, hr, min_size, astype=None):
    py, px, colour = _mean_shift(values, mask, hs, hr)
    labels = _segments(values, mask, py, px, colour, hs, hr, max(1, int(min_size)))
    out = np.zeros(values.shape)
    for region in np.unique(labels[~mask]):
        inside = labels == region
        out[inside] = values[inside].mean(axis=0)
    kind = np.uint8 if astype in (None, "", "U8") else np.dtype(str(astype).lower().replace("f32", "float32")
                                                                .replace("f64", "float64"))
    out = np.clip(np.rint(out), 0, 255).astype(kind) if kind == np.uint8 else out.astype(kind)
    arrays = [np.where(mask, 0, out[..., k]) for k in range(out.shape[-1])]
    if len(arrays) == 1:
        return Raster._of(arrays[0], mask.copy(), grid)
    return Raster._of_bands(arrays, [mask.copy() for _ in arrays], grid)


def _eight_bit(source, indexes, tool):
    arrays = [source._arrays[i - 1] for i in indexes]
    masks = [source._masks[i - 1] for i in indexes]
    mask = np.logical_or.reduce(masks)
    values = np.stack([np.asarray(a, np.float64) for a in arrays], axis=-1)
    if values[~mask].size and (values[~mask].min() < 0 or values[~mask].max() > 255):
        low, high = values[~mask].min(), values[~mask].max()
        values = (values - low) / (high - low) * 255 if high > low else np.zeros_like(values)
    return values, mask


def SegmentMeanShift(in_raster, spectral_detail=15.5, spatial_detail=15, min_segment_size=20, band_indexes=None,
                     max_segment_size=-1):
    """EQUIVALENT: mean shift segmentation (Comaniciu and Meer 2002, §4): mean shift filtering with flat kernels of
    spatial radius hs and range radius hr in the joint spatial-range domain, regions of neighbouring cells whose
    converged points lie within hs and hr of each other, and regions under ``min_segment_size`` cells merged into
    their best fitting neighbour — the one of nearest mean colour, as the page describes. The output is 8-bit: each
    cell holds its segment's mean colour, three bands for a multiband input and one for a grayscale one, as
    documented. ``band_indexes`` (for example ``"4 3 2"``) chooses the three bands; without it, bands 1, 2 and 3.

    Refused: ``max_segment_size`` other than −1, since the page does not say how a larger segment is divided.

    INFERRED: the radii, hs = 21 − spatial_detail cells and hr = 21 − spectral_detail grey levels (at least 1 each) —
    SegMeanShift's page gives its spectral and spatial radius on the same 0 to 21 scale as the details, running the
    other way; the defaults 15.5, 15 and 20, which the Python page does not state, are the tool dialog's; an input
    beyond 0 to 255 is stretched linearly onto it; segments' colours are rounded."""
    tool = "SegmentMeanShift"
    _refuse_environment(f"arcpy.sa.{tool}")
    if _given(max_segment_size) and int(max_segment_size) != -1:
        _refuse(tool, "max_segment_size", max_segment_size, "how a larger segment is divided is not documented")
    source = _open_bands(in_raster, tool)
    if _given(band_indexes):
        indexes = [int(v) for v in str(band_indexes).replace(",", " ").replace(";", " ").split()] \
            if not isinstance(band_indexes, (list, tuple)) else [int(v) for v in band_indexes]
    else:
        indexes = [1] if len(source._arrays) == 1 else [1, 2, 3]
    if len(source._arrays) > 1 and len(indexes) != 3:
        raise ValueError(f"{tool}: band_indexes takes three bands.")
    values, mask = _eight_bit(source, indexes, tool)
    hs = max(1, int(round(21 - float(spatial_detail if _given(spatial_detail) else 15))))
    hr = max(1.0, 21 - float(spectral_detail if _given(spectral_detail) else 15.5))
    return _segment(values, mask, source._grid, hs, hr, int(min_segment_size if _given(min_segment_size) else 20))


def SegMeanShift(raster, spectral_detail=None, spatial_detail=None, spectral_radius=None, spatial_radius=None,
                 min_num_pixels_per_segment=None, astype=None):
    """EQUIVALENT: the Segment Mean Shift raster function — SegmentMeanShift's mean shift segmentation, its radii
    given directly by ``spectral_radius`` and ``spatial_radius`` or by the details as 21 − detail, as the two scales
    run on the function's page. A raster of one or three bands, as the function's page requires.

    INFERRED: as SegmentMeanShift; ``astype`` names a numpy type (``U8``, the default, or ``F32``)."""
    tool = "SegMeanShift"
    _refuse_environment(f"arcpy.ia.{tool}")
    source = _open_bands(raster, tool)
    if len(source._arrays) not in (1, 3):
        raise ValueError(f"{tool}: the raster needs one or three bands, as the function's page says.")
    values, mask = _eight_bit(source, list(range(1, len(source._arrays) + 1)), tool)
    hr = float(spectral_radius) if _given(spectral_radius) else 21 - float(
        spectral_detail if _given(spectral_detail) else 15.5)
    hs = int(round(float(spatial_radius))) if _given(spatial_radius) else int(round(21 - float(
        spatial_detail if _given(spatial_detail) else 15)))
    size = int(min_num_pixels_per_segment) if _given(min_num_pixels_per_segment) else 20
    return _segment(values, mask, source._grid, max(1, hs), max(1.0, hr), size, astype)


# ---- the names that stay refused, with their reasons

REFUSED = {
    "TrainRandomTreesClassifier": "its random trees are grown by a randomised procedure — each tree on a random "
                                  "subspace, each node's decision 'optimised by a randomized procedure' — that the "
                                  "page names without defining, and a random forest equivalent (Breiman 2001) needs a "
                                  "decision-tree learner, which is not among grapy's libraries",
    "TrainRandomTreesRegressionModel": "it trains random trees for regression by the same undefined randomised "
                                       "procedure as Train Random Trees Classifier, and a random forest equivalent "
                                       "(Breiman 2001) needs a decision-tree learner, which is not among grapy's "
                                       "libraries",
    "PredictUsingRegressionModel": "it reads only the regression definition (.ecd) that Train Random Trees Regression "
                                   "Model writes, which is refused for its random trees",
    "TasseledCap": "it chooses its coefficients by the sensor named in the raster's metadata (Landsat MSS, TM, ETM+, "
                   "8, 9, IKONOS, QuickBird, WorldView-2, RapidEye) and takes no sensor parameter, and grapy's rasters "
                   "carry no sensor metadata to choose by",
    # arcpy.sa only
    "ContourWithBarriers": "it contours each side of a barrier independently, and the page does not say how the cells "
                           "beyond a barrier are kept out of a contour's interpolation, nor name the field that holds "
                           "each contour's value (only Type is named)",
    "TopoToRaster": "it is Hutchinson's ANUDEM program: How Topo to Raster works describes its drainage enforcement "
                    "(spurious sinks cleared through the lowest saddle within a tolerance), the stream and ridge lines "
                    "it derives from contour curvature, its lake heights and cliff adjustments in words only, and no "
                    "published interpolator stands in for those constraints",
    "TopoToRasterByFile": "it runs Topo to Raster from a parameter file, and Topo to Raster is refused: its ANUDEM "
                          "drainage enforcement is described in words only",
}


# ---- the equivalents' marks (ADR-224 §5.6)

from ._runtime import equivalent as _equivalent  # noqa: E402

_DEFINITION = ("the classifier definition's own rule: Gaussian maximum likelihood, nearest isodata mean, k-nearest "
               "neighbours or one-against-one linear support vector machines, as the Train tool wrote it",
               "Richards and Jia 2006, §8.3; Ball and Hall 1965; Cover and Hart 1967; Cortes and Vapnik 1995")
TrainMaximumLikelihoodClassifier = _equivalent(*ML_METHOD)(TrainMaximumLikelihoodClassifier)
TrainIsoClusterClassifier = _equivalent(*ISO_METHOD)(TrainIsoClusterClassifier)
TrainKNearestNeighborClassifier = _equivalent(*KNN_METHOD)(TrainKNearestNeighborClassifier)
TrainSupportVectorMachineClassifier = _equivalent(*SVM_METHOD)(TrainSupportVectorMachineClassifier)
ClassifyRaster = _equivalent(*_DEFINITION)(ClassifyRaster)
Classify = _equivalent(*_DEFINITION)(Classify)
MLClassify = _equivalent(*ML_METHOD)(MLClassify)
InspectTrainingSamples = _equivalent(*INSPECT_METHOD)(InspectTrainingSamples)
Pansharpen = _equivalent(*PANSHARPEN_METHOD)(Pansharpen)
RegionGrow = _equivalent(*REGION_GROW_METHOD)(RegionGrow)
SegmentMeanShift = _equivalent(*MEAN_SHIFT_METHOD)(SegmentMeanShift)
SegMeanShift = _equivalent(*MEAN_SHIFT_METHOD)(SegMeanShift)


def MLClassifyFunction(raster, signature):
    """EQUIVALENT, as arcpy.sa.MLClassify is: arcpy.ia.MLClassify(raster, signature), the MLClassify raster
    function — arcpy.sa.MLClassify's maximum likelihood classification with its defaults (no reject fraction, equal
    priors)."""
    return MLClassify(raster, signature)


MLClassifyFunction.__grapy_equivalent__ = MLClassify.__grapy_equivalent__
