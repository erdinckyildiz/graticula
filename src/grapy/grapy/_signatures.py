"""Signature files (.gsg): Create Signatures, Edit Signatures and Class Probability (ADR-243).

The signature file's layout is the one How Create Signatures works and How Iso Cluster works print by example — a
commented header naming the layers, a "Type / Number of Classes / Number of Layers" line, then per class its id,
cell count and optional name, its means and, unless MEAN_ONLY, its covariance matrix. grapy writes and reads that
layout (``_classify._read_gsg``); that the examples are the layout is INFERRED.
"""
from __future__ import annotations

import os

import numpy as np

from ._classify import _gaussian_scores, _given, _path, _read_gsg, _refuse, class_priors
from ._raster import Raster, _open_bands, _refuse_environment
from ._runtime import equivalent


def _bands(in_raster_bands, tool):
    inputs = in_raster_bands if isinstance(in_raster_bands, (list, tuple)) else [in_raster_bands]
    rasters = [_open_bands(r, tool) for r in inputs]
    grid = rasters[0]._grid
    for r in rasters[1:]:
        if not r._grid.same(grid):
            _refuse(tool, "in_raster_bands", r, "bands on different grids need resampling")
    data = np.stack([np.asarray(a, np.float64) for r in rasters for a in r._arrays], axis=-1)
    mask = np.logical_or.reduce([m for r in rasters for m in r._masks])
    names = []
    for value, r in zip(inputs, rasters):
        base = os.path.splitext(os.path.basename(str(value if isinstance(value, (str, os.PathLike)) else
                                                     (r.catalogPath or "raster"))))[0]
        names += [base] if len(r._arrays) == 1 else [f"{base}_b{k + 1}" for k in range(len(r._arrays))]
    return data, mask, grid, names


def write_gsg(path, classes, layers, header=None):
    """``classes``: dicts of value, count, name, means and (optionally) covariance, written in the printed layout."""
    out = []
    if header:
        out.append(f"# {header}")
    out += ["# Number of selected grids", f"/*        {len(layers)}", "# Layer-Number        Grid-name"]
    out += [f"/*        {i + 1:<12d} {name}" for i, name in enumerate(layers)]
    out += ["# Type  Number of Classes  Number of Layers  Number of Parametric Layers",
            f"  1     {len(classes):<18d} {len(layers):<17d} {len(layers)}"]
    for c in classes:
        out += ["# " + "-" * 70, "# Class ID    Number of Cells    Class Name",
                f"  {c['value']:<11d} {c['count']:<18d} {c.get('name') or ''}".rstrip(),
                "# Layers " + "".join(f"  {i + 1:<10d}" for i in range(len(layers))).rstrip(), "# Means",
                "        " + "".join(f"{v:14.6f}" for v in c["means"])]
        if c.get("covariance"):
            out.append("# Covariance")
            out += [f"{i + 1:<4d}" + "".join(f"{v:14.6f}" for v in row) for i, row in enumerate(c["covariance"])]
    path = _path(path)
    with open(path, "w", encoding="utf-8") as handle:
        handle.write("\n".join(out) + "\n")
    return path


def CreateSignatures(in_raster_bands, in_sample_data, out_signature_file, compute_covariance="COVARIANCE",
                     sample_field=None):
    """The class signatures of the samples over the bands, written as an ASCII signature file in the layout How
    Create Signatures works prints: for each class its number of cells, its mean in each band and, with COVARIANCE
    (the default), its covariance matrix. The samples are an integer raster, each value a class, or features — a
    polygon takes the cells whose centres it holds, a point or line every cell it touches — classed by
    ``sample_field``; an integer field gives the class ids, a text field the class names, as documented. At least two
    classes, as documented.

    INFERRED: covariances divide by n − 1; a text sample field numbers its classes 1, 2 … in the names' sorted order;
    features without a sample field are classed by their object id; a cell under two features goes to the later."""
    from ._result import Result
    tool = "CreateSignatures"
    _refuse_environment(f"arcpy.sa.{tool}")
    data, mask, grid, layers = _bands(in_raster_bands, tool)
    names = {}
    if isinstance(in_sample_data, Raster) or str(in_sample_data).lower().endswith((".tif", ".tiff")):
        sample = _open_bands(in_sample_data, tool)
        if not sample._grid.same(grid):
            _refuse(tool, "in_sample_data", in_sample_data, "a sample raster on another grid needs resampling")
        if not np.issubdtype(np.asarray(sample._arrays[0]).dtype, np.integer):
            raise ValueError(f"{tool}: the sample raster must be integer, as documented.")
        labels = np.where(sample._masks[0], 0, sample._arrays[0]).astype(np.int64)
        labels_mask = sample._masks[0]
    else:
        labels, names = _burn(in_sample_data, sample_field, grid, tool)
        labels_mask = labels == 0
    keep = ~mask & ~labels_mask
    values = sorted(int(v) for v in np.unique(labels[keep]))
    if len(values) < 2:
        raise ValueError(f"{tool}: the samples hold {len(values)} class; at least two are needed, as documented.")
    covariance = str(compute_covariance or "COVARIANCE").upper() not in ("MEAN_ONLY", "FALSE")
    classes = []
    for v in values:
        x = data[keep & (labels == v)]
        entry = {"value": v, "count": int(x.shape[0]), "name": names.get(v), "means": x.mean(axis=0).tolist()}
        if covariance:
            entry["covariance"] = (np.cov(x, rowvar=False).reshape(x.shape[1], x.shape[1]) if x.shape[0] > 1
                                   else np.zeros((x.shape[1], x.shape[1]))).tolist()
        classes.append(entry)
    header = f"Signatures Produced by ClassSig from Zone-Grid {os.path.basename(str(in_sample_data))} and Stack " \
             f"{' '.join(layers)}"
    return Result(write_gsg(out_signature_file, classes, layers, header))


def _burn(features, field, grid, tool):
    from rasterio.features import rasterize
    from . import da
    from .analysis import _schema
    columns = {c.name.lower(): c for c in _schema(features)[0]}
    if _given(field) and str(field).lower() not in columns:
        raise ValueError(f"{tool}: '{field}' is not a field of {features}.")
    column = columns.get(str(field).lower()) if _given(field) else None
    text = column is not None and column.type == "String"
    with da.SearchCursor(features, [column.name if column is not None else "OID@", "SHAPE@"]) as rows:
        pairs = [(shape._shape, value) for value, shape in rows if shape is not None and value is not None]
    names = {}
    if text:
        codes = {name: i + 1 for i, name in enumerate(sorted({str(v) for _, v in pairs}))}
        names = {code: name for name, code in codes.items()}
        pairs = [(s, codes[str(v)]) for s, v in pairs]
    else:
        pairs = [(s, int(v)) for s, v in pairs]
    labels = np.zeros((grid.rows, grid.columns), np.int64)
    for touched in (False, True):
        group = [(s, v) for s, v in pairs if s.geom_type.endswith("Polygon") != touched]
        if group:
            burnt = rasterize(group, out_shape=(grid.rows, grid.columns), transform=grid.transform(), fill=0,
                              dtype="int64", all_touched=touched)
            labels = np.where(burnt != 0, burnt, labels)
    return labels, names


EDIT_METHOD = ("merged signatures by the pooled mean and covariance of their classes' cells",
               "Chan, Golub and LeVeque 1979, Updating formulae and a pairwise algorithm for computing sample "
               "variances, Stanford CS-79-773; How Dendrogram works (pooled statistics of a merged class)")


def EditSignatures(in_raster_bands, in_signature_file, in_signature_remap_file, out_signature_file,
                   sample_interval=10):
    """EQUIVALENT: the signature file edited by the remap file the page documents — lines "old : new", a class mapped
    to −9999 deleted, classes mapped to one id merged, a class renumbered to a new id, classes left out unchanged. A
    merged class takes the pooled statistics of its members: n = Σnᵢ, mean = Σnᵢmᵢ/n and covariance
    [Σ(nᵢ − 1)Cᵢ + Σnᵢ(mᵢ − m)(mᵢ − m)ᵀ]/(n − 1), which are the statistics of the union of their cells; it carries the
    name of the class whose id it takes, as documented.

    INFERRED: the statistics are pooled from the signatures rather than resampled from the bands, so
    ``sample_interval`` changes nothing (the page does not say what is sampled); the bands are checked to have as many
    layers as the file; classes are written in ascending id order."""
    from ._result import Result
    tool = "EditSignatures"
    _refuse_environment(f"arcpy.sa.{tool}")
    data, _, _, layers = _bands(in_raster_bands, tool)
    classes = _read_gsg(in_signature_file, tool, covariance=False)
    if len(classes) < 2:
        raise ValueError(f"{tool}: the signature file must hold at least two classes, as documented.")
    if any(len(c["means"]) != data.shape[-1] for c in classes):
        raise ValueError(f"{tool}: the signature file's layers do not match the {data.shape[-1]} bands given.")
    remap = {}
    with open(_path(in_signature_remap_file), encoding="utf-8", errors="replace") as handle:
        for line in handle:
            if ":" not in line:
                continue
            old, new = line.split(":", 1)
            try:
                remap[int(float(old))] = int(float(new))
            except ValueError:
                continue
    names = {c["value"]: c["name"] for c in classes}
    groups = {}
    for c in classes:
        target = remap.get(c["value"], c["value"])
        if target == -9999:
            continue
        groups.setdefault(target, []).append(c)
    has_covariance = all(c["covariance"] for c in classes)
    out = []
    for target in sorted(groups):
        members = groups[target]
        counts = np.array([c["count"] for c in members], float)
        means = np.array([c["means"] for c in members])
        n = counts.sum()
        mean = (counts[:, None] * means).sum(axis=0) / n
        entry = {"value": target, "count": int(n), "means": mean.tolist(),
                 "name": names.get(target) or (members[0]["name"] if len(members) == 1 else None)}
        if has_covariance:
            pooled = sum((k - 1) * np.array(c["covariance"]) + k * np.outer(m - mean, m - mean)
                         for k, c, m in zip(counts, members, means))
            entry["covariance"] = (pooled / (n - 1) if n > 1 else pooled).tolist()
        out.append(entry)
    return Result(write_gsg(out_signature_file, out, layers))


PROBABILITY_METHOD = ("Bayesian posterior probabilities of normal classes",
                      "Richards and Jia 2006, Remote Sensing Digital Image Analysis, 4th ed., §8.3 (Bayes' rule for "
                      "Gaussian classes)")


@np.errstate(all="ignore")
def ClassProbability(in_raster_bands, in_signature_file, maximum_output_value=100, a_priori_probabilities="EQUAL",
                     in_a_priori_file=None):
    """EQUIVALENT: as the page describes, Bayes' rule over the signature file's normal classes: a cell's probability
    of class k is p(k)·N(x; mₖ, Σₖ) / Σⱼ p(j)·N(x; mⱼ, Σⱼ), with the priors EQUAL, SAMPLE (each class's share of the
    cells) or from the a priori file (an id and a probability per line, the classes left out sharing the remainder of
    1 equally, as documented). The output has a band per class, in the file's order — none for a class of prior 0, as
    documented — scaled to 0 … ``maximum_output_value``: integer, floating point when it is 1, as documented.

    INFERRED: the scaled probabilities are rounded to the nearest integer."""
    tool = "ClassProbability"
    _refuse_environment(f"arcpy.sa.{tool}")
    kind = str(a_priori_probabilities or "EQUAL").upper()
    if kind not in ("EQUAL", "SAMPLE", "FILE"):
        _refuse(tool, "a_priori_probabilities", a_priori_probabilities)
    top = int(maximum_output_value if _given(maximum_output_value) else 100)
    if top <= 0:
        raise ValueError(f"{tool}: maximum_output_value must be above 0.")
    data, mask, grid, _ = _bands(in_raster_bands, tool)
    classes = _read_gsg(in_signature_file, tool)
    priors = class_priors(classes, kind, in_a_priori_file, tool)
    scores, _ = _gaussian_scores(data[~mask], [c["means"] for c in classes], [c["covariance"] for c in classes],
                                 priors)
    scores = scores - scores.max(axis=-1, keepdims=True)
    posterior = np.exp(scores)
    posterior /= posterior.sum(axis=-1, keepdims=True)
    arrays, names = [], []
    for k, c in enumerate(classes):
        if priors is not None and priors[k] <= 0:
            continue
        band = np.zeros(mask.shape)
        band[~mask] = posterior[:, k] * top
        arrays.append(band.astype(np.float32) if top == 1 else np.rint(band).astype(np.int32))
        names.append(f"Class_{c['value']}")
    return Raster._of_bands(arrays, [mask.copy() for _ in arrays], grid)


EditSignatures = equivalent(*EDIT_METHOD)(EditSignatures)
ClassProbability = equivalent(*PROBABILITY_METHOD)(ClassProbability)

REFUSED = {
    "Dendrogram": "its VARIANCE distance between classes is given only as an image, and the ASCII graph it writes is "
                  "shown by one example without the rules for its layout, scale bars or rounding",
    "RemoveRasterSegmentTilingArtifacts": "the page does not say how a segment cut by a tile boundary is recognised — "
                                          "which segments meeting along a tile line are rejoined — nor what value the "
                                          "rejoined segment takes",
}
