"""Deep learning on point clouds for arcpy.ddd: training data, training, classification, object detection and evaluation
— ADR-249. Built on ADR-248's foundation (``_deeplearning``: the framework, the model definition, checkpoints and the
training loop) and on ADR-229's LAS datasets (``_las``); the networks are ``_dlpcnets``'s.

Training is an equivalent (ADR-224 §5.6): Pro trains with Esri's arcgis.learn, whose code is proprietary and is not
reproduced; grapy trains the published network the architecture value names, from random weights, with Adam, and says
so when it runs.

The training data folders (INFERRED, ADR-249 §5.2)
==================================================

Pro's ``.pctd`` and ``.pcotd`` are folders of uncompressed HDF5 blocks, as the pages say, whose layout Esri does not
publish. grapy writes and reads its own: a folder named ``<name>.pctd`` (classification) or ``<name>.pcotd`` (object
detection) holding

- ``grapy_pointcloud_training.json``: ``format`` (``"grapy point cloud training data (Graticula ADR-249)"``),
  ``version`` 1, ``kind`` (``classification`` or ``object_detection``), ``block_size`` in the cloud's units,
  ``block_point_limit``, ``attributes`` (the attribute names the data carries), ``reference_height`` (whether relative
  heights were computed), ``excluded_class_codes``, ``srid``, ``class_counts`` or ``object_counts`` per split, and the
  points-per-block histogram per split;
- ``training/blocks.npz`` and ``validation/blocks.npz``: numpy arrays ``xyz`` (n × 3, float64), ``attributes``
  (n × 8, float32, in the order of ``ATTRIBUTES``, 0 where the cloud does not carry one), ``classification`` (n, uint8),
  ``offsets`` (blocks + 1: block k's points are ``offsets[k]:offsets[k+1]``), ``origin`` (blocks × 3: the block's lower
  x, y and its lowest point's z); for object detection also ``boxes`` (m × 8: centre x, y, z, length, width, height,
  yaw, object code) and ``box_offsets``;
- ``histogram_<split>.csv``: points per block, in ten bins (the page's histogram, as a table: grapy carries no plotting
  library).

A folder without that manifest — one Pro wrote — is refused by name.

Blocks (INFERRED, ADR-249 §5.3)
================================

A block is a square whose side is ``block_size`` (the page's "diameter"), on a grid whose step is half a block, so that
each point is in four blocks — the page's *"replicate each point in four blocks"*. A block holding more points than
``block_point_limit`` is cut into as many blocks of the same square as it takes, its points dealt at random (seeded by
``env.randomGenerator``). Withheld points are never exported, and points outside the reference height raster are
omitted, as documented.
"""
from __future__ import annotations

import csv
import json
import math
import os

import numpy as np
import shapely

from . import _deeplearning as dl
from . import _las
from ._result import Result
from ._runtime import AddMessage, ExecuteError, NotBuiltError, _is_given, env

FORMAT = "grapy point cloud training data (Graticula ADR-249)"
MANIFEST = "grapy_pointcloud_training.json"
ATTRIBUTES = ("INTENSITY", "RETURN_NUMBER", "NUMBER_OF_RETURNS", "RED", "GREEN", "BLUE", "NEAR_INFRARED",
              "RELATIVE_HEIGHT")
CLASSIFICATION = "PointCloudClassification"
DETECTION = "PointCloudObjectDetection"
WIDTH = 1.0     # the papers' widths; the tests scale the networks down through this, and nothing else does

REFUSED_ARCHITECTURES = {
    ("detection", "POINT_TRANSFORMER_V3"): "the Point Transformer V3 paper defines a backbone and evaluates detection "
                                          "through other frameworks' detection heads, and the page names no head to "
                                          "put on it; grapy detects with SECOND (SECD)",
}


def _tool(name):
    return f"arcpy.ddd.{name}"


def _refuse(tool, parameter, value, why):
    raise NotBuiltError(f"{tool}: {parameter} '{value}' is not built in grapy: {why}.")


def _word(value, default, allowed, tool, parameter):
    if not _is_given(value):
        return default
    if isinstance(value, bool):
        return allowed[0] if value else allowed[1]
    text = str(value).strip().upper()
    for each in allowed:
        if each == text:
            return each
    raise ValueError(f"{tool}: {parameter} must be one of {', '.join(allowed)}; got '{value}'.")


def _codes(value):
    """A list of class codes from a list, a value table or ``"2;6 7"``."""
    if not _is_given(value):
        return []
    if isinstance(value, (int, np.integer)):
        return [int(value)]
    if isinstance(value, str):
        value = value.replace(";", " ").split()
    out = []
    for v in value:
        if isinstance(v, (list, tuple)):
            out.extend(int(float(x)) for x in v if str(x).strip())
        elif str(v).strip():
            out.append(int(float(v)))
    for code in out:
        if not 0 <= code <= 255:
            raise ValueError(f"class code {code} is out of the range 0–255.")
    return out


def _pairs(value, columns=2):
    from ._tin import rows_of
    return [row[:columns] for row in rows_of(value, columns) if str(row[0]).strip() != ""]


def _path(value):
    path = os.fspath(value)
    if not os.path.isabs(path) and env.workspace and ".gpkg" not in str(env.workspace).lower():
        path = os.path.join(env.workspace, path)
    return path


def _linear(value, srid, tool, parameter):
    from .analysis import _distance
    if not _is_given(value):
        raise ValueError(f"{tool}: {parameter} is required.")
    size = float(_distance(value, srid))
    if size <= 0:
        raise ValueError(f"{tool}: {parameter} must be greater than 0.")
    return size


# ---- the points


def _attributes_of(las, a):
    """(n × 8) attribute columns, and the names this file carries."""
    n = len(a["x"])
    out = np.zeros((n, len(ATTRIBUTES)), np.float32)
    have = {"INTENSITY", "RETURN_NUMBER", "NUMBER_OF_RETURNS"}
    out[:, 0], out[:, 1], out[:, 2] = a["intensity"], a["return_number"], a["number_of_returns"]
    names = set(las.point_format.dimension_names)
    for k, (name, field) in enumerate((("RED", "red"), ("GREEN", "green"), ("BLUE", "blue"), ("NEAR_INFRARED", "nir")),
                                      start=3):
        if field in names:
            out[:, k] = np.asarray(las[field], np.float32)
            have.add(name)
    return out, have


def cloud(value, tool, area=None, excluded=(), reference=None, filtered=True):
    """Every point of a LAS dataset, a LAS dataset layer or a .las file that a tool reads: its filters honoured (a
    layer's), withheld points and ``excluded`` class codes left out, within ``area`` (a shapely polygon) when given;
    with a ``reference`` raster, RELATIVE_HEIGHT is z less the raster's bilinear value and points off it are omitted, as
    the pages say. Answers a dict of arrays (``x``, ``y``, ``z``, ``classification``, ``attributes``, ``file``,
    ``index``), the file paths, the srid and the attribute names every file carries."""
    if _las.is_las(value):
        dataset = _las.LasDataset.open(value)
        paths, filters, srid = dataset.file_paths(), _las.filters_of(value), dataset.srid
    else:
        path = _path(value)
        if not os.path.isfile(path):
            raise ExecuteError(f"ERROR 000732: {tool}: Input point cloud '{value}' does not exist or is not supported.")
        paths, filters, srid = [path], dict(_las._DEFAULTS), None
    parts, have = [], None
    for number, path in enumerate(paths):
        las = _las.read_file(path)
        a = _las.arrays(las)
        if srid is None:
            srid = _las.srid_of(las)
        attributes, names = _attributes_of(las, a)
        have = names if have is None else have & names
        keep = (_las._selected(a, filters) if filtered else np.ones(len(a["x"]), bool)) & ~a["withheld"]
        if excluded:
            keep &= ~np.isin(a["classification"], list(excluded))
        if area is not None and keep.any():
            keep[keep] = shapely.covers(area, shapely.points(a["x"][keep], a["y"][keep]))
        parts.append({"x": a["x"][keep], "y": a["y"][keep], "z": a["z"][keep],
                      "classification": a["classification"][keep], "attributes": attributes[keep],
                      "file": np.full(int(keep.sum()), number), "index": np.nonzero(keep)[0]})
    if parts:
        found = {k: np.concatenate([p[k] for p in parts]) for k in parts[0]}
    else:
        found = {"x": np.zeros(0), "y": np.zeros(0), "z": np.zeros(0), "classification": np.zeros(0, np.int64),
                 "attributes": np.zeros((0, len(ATTRIBUTES)), np.float32), "file": np.zeros(0, int),
                 "index": np.zeros(0, int)}
    have = set(have or ())
    if _is_given(reference):
        from ._mptools import _bilinear
        from ._raster import as_raster
        ground = _bilinear(as_raster(reference, tool), found["x"], found["y"])
        on = np.isfinite(ground)
        found = {k: v[on] for k, v in found.items()}
        found["attributes"][:, 7] = found["z"] - ground[on]
        have.add("RELATIVE_HEIGHT")
    return found, paths, int(srid or 0), sorted(have, key=ATTRIBUTES.index)


def area_of(boundary):
    if not _is_given(boundary):
        return None
    return _las._area_of(None, boundary)


def tiles(x, y, size):
    """The blocks a cloud is cut into: a dict of (column, row) → indexes of its points, and the grid's origin. Each
    block is a square of side ``size`` at ``origin + (column, row) · size/2``; each point is in four."""
    if not len(x):
        return {}, (0.0, 0.0)
    step = size / 2.0
    ox, oy = math.floor(x.min() / step) * step - step, math.floor(y.min() / step) * step - step
    i, j = np.floor((x - ox) / step).astype(np.int64), np.floor((y - oy) / step).astype(np.int64)
    out = {}
    for di in (0, 1):
        for dj in (0, 1):
            key = (i - di) * 1_000_003 + (j - dj)
            order = np.argsort(key, kind="stable")
            values, starts = np.unique(key[order], return_index=True)
            ends = np.append(starts[1:], len(order))
            for v, s, e in zip(values, starts, ends):
                column, row = int(v // 1_000_003), int(v % 1_000_003)
                if row > 1_000_003 // 2:
                    column, row = column + 1, row - 1_000_003
                out.setdefault((column, row), []).append(order[s:e])
    return {k: np.sort(np.concatenate(v)) for k, v in out.items()}, (ox, oy)


def split_block(indexes, limit, rng):
    """A block of more than ``limit`` points as several, its points dealt at random."""
    if len(indexes) <= limit:
        return [indexes]
    shuffled = indexes[rng.permutation(len(indexes))]
    return [np.sort(part) for part in np.array_split(shuffled, math.ceil(len(indexes) / limit))]


def _histogram(counts, folder, split):
    counts = np.asarray(counts, int)
    if not len(counts):
        return []
    edges = np.linspace(0, max(1, counts.max()), 11)
    numbers, _ = np.histogram(counts, edges)
    rows = [(int(math.floor(edges[k])), int(math.ceil(edges[k + 1])), int(numbers[k])) for k in range(10)]
    with open(os.path.join(folder, f"histogram_{split}.csv"), "w", newline="", encoding="utf-8") as out:
        writer = csv.writer(out)
        writer.writerow(["From", "To", "Blocks"])
        writer.writerows(rows)
    AddMessage(f"Points per block ({split}): " + ", ".join(f"{a}–{b}: {c}" for a, b, c in rows if c))
    return rows


def _write_split(folder, split, found, blocks, origins, boxes=None):
    os.makedirs(os.path.join(folder, split), exist_ok=True)
    index = np.concatenate(blocks) if blocks else np.zeros(0, int)
    offsets = np.concatenate([[0], np.cumsum([len(b) for b in blocks])]).astype(np.int64)
    origin = np.array([[o[0], o[1], float(found["z"][b].min()) if len(b) else 0.0] for b, o in zip(blocks, origins)],
                      float).reshape(-1, 3)
    arrays = {"xyz": np.column_stack([found["x"][index], found["y"][index], found["z"][index]]),
              "attributes": found["attributes"][index].astype(np.float32),
              "classification": found["classification"][index].astype(np.uint8), "offsets": offsets,
              "origin": origin}
    if boxes is not None:
        arrays["boxes"] = np.concatenate(boxes).reshape(-1, 8) if boxes else np.zeros((0, 8))
        arrays["box_offsets"] = np.concatenate([[0], np.cumsum([len(b) for b in boxes])]).astype(np.int64)
    np.savez(os.path.join(folder, split, "blocks.npz"), **arrays)


def _blocks_of(found, size, limit, rng, keep=None):
    """The blocks of a cloud as (indexes, (x0, y0)) — each square's lower corner; ``keep(indexes)`` filters them."""
    grid, (ox, oy) = tiles(found["x"], found["y"], size)
    out = []
    for (column, row), indexes in sorted(grid.items()):
        if keep is not None and not keep(indexes, (ox + column * size / 2.0, oy + row * size / 2.0)):
            continue
        for part in split_block(indexes, limit, rng):
            out.append((part, (ox + column * size / 2.0, oy + row * size / 2.0)))
    return out


def _rng():
    return np.random.default_rng(dl_seed())


def dl_seed():
    from .management import _seed
    return int(_seed()) % (2 ** 31)


def _sources(tool, in_point_cloud, training_boundary, validation_point_cloud, validation_boundary, excluded,
             reference_height):
    """The training and validation clouds, as the pages divide them: a validation cloud (within its boundary), or the
    input within the validation boundary; with neither, the input outside the training boundary (INFERRED)."""
    if not _is_given(validation_point_cloud) and not _is_given(training_boundary):
        raise ValueError(f"{tool}: a training boundary is required when no validation point cloud is given.")
    train_area = area_of(training_boundary)
    training, _, srid, have = cloud(in_point_cloud, tool, train_area, excluded, reference_height)
    if _is_given(validation_point_cloud):
        validation, _, _, have_v = cloud(validation_point_cloud, tool, area_of(validation_boundary), excluded,
                                         reference_height)
    else:
        valid_area = area_of(validation_boundary)
        if valid_area is None:
            whole, _, _, have_v = cloud(in_point_cloud, tool, None, excluded, reference_height)
            outside = ~shapely.covers(train_area, shapely.points(whole["x"], whole["y"]))
            validation = {k: v[outside] for k, v in whole.items()}
        else:
            if train_area is not None and train_area.intersection(valid_area).area > 0:
                AddMessage(f"{tool}: the training and validation boundaries overlap, and the page advises against it.")
            validation, _, _, have_v = cloud(in_point_cloud, tool, valid_area, excluded, reference_height)
    return training, validation, srid, [a for a in have if a in have_v]


def _manifest(folder, document):
    with open(os.path.join(folder, MANIFEST), "w", encoding="utf-8") as out:
        json.dump(document, out, indent=1)


def _prepare(tool, folder, kind, training, validation, size, limit, srid, have, excluded, reference, keep=None,
             boxes_for=None):
    """Writes the training data folder; ``keep`` (a dict of split → filter of a block's point indexes and corner)
    chooses the blocks exported."""
    rng = _rng()
    os.makedirs(folder, exist_ok=True)
    document = {"format": FORMAT, "version": 1, "kind": kind, "block_size": size, "block_point_limit": int(limit),
                "attributes": have, "reference_height": bool(_is_given(reference)),
                "excluded_class_codes": list(excluded), "srid": srid, "splits": {}}
    for split, found in (("training", training), ("validation", validation)):
        blocks = _blocks_of(found, size, limit, rng, (keep or {}).get(split))
        if not blocks:
            raise ExecuteError(f"ERROR: {tool}: no {split} block holds a point (check the boundaries and class codes).")
        boxes = [boxes_for[split](o, size) for _, o in blocks] if boxes_for else None
        _write_split(folder, split, found, [b for b, _ in blocks], [o for _, o in blocks], boxes)
        counts = [len(b) for b, _ in blocks]
        entry = {"blocks": len(blocks), "points": int(sum(counts)), "histogram": _histogram(counts, folder, split)}
        codes, numbers = np.unique(found["classification"], return_counts=True)
        entry["class_counts"] = {str(int(c)): int(n) for c, n in zip(codes, numbers)}
        if boxes:
            every = np.concatenate(boxes).reshape(-1, 8)
            o_codes, o_numbers = np.unique(every[:, 7].astype(int), return_counts=True) if len(every) else ([], [])
            entry["object_counts"] = {str(int(c)): int(n) for c, n in zip(o_codes, o_numbers)}
        document["splits"][split] = entry
    _manifest(folder, document)
    return folder


def _training_folder(value, extension, tool):
    path = _path(value)
    if not path.lower().endswith(extension):
        path += extension
    return path


def PreparePointCloudTrainingData(in_point_cloud, block_size, out_training_data, training_boundary=None,
                                  validation_point_cloud=None, validation_boundary=None, class_codes_of_interest=None,
                                  block_point_limit=8192, reference_height=None, excluded_class_codes=None):
    """Blocks of a classified point cloud for TrainPointCloudClassificationModel, written in grapy's ``.pctd`` folder
    (see the module's docstring). With ``class_codes_of_interest``, only blocks holding one of those codes are
    exported, as documented."""
    tool = _tool("PreparePointCloudTrainingData")
    excluded = _codes(excluded_class_codes)
    interest = _codes(class_codes_of_interest)
    training, validation, srid, have = _sources(tool, in_point_cloud, training_boundary, validation_point_cloud,
                                                validation_boundary, excluded, reference_height)
    size = _linear(block_size, srid, tool, "block_size")
    limit = int(block_point_limit or 8192)
    keep = None
    if interest:        # the codes of interest select the blocks exported, of both splits (INFERRED)
        def of(found):
            return lambda indexes, _origin: bool(np.isin(found["classification"][indexes], interest).any())
        keep = {"training": of(training), "validation": of(validation)}
    folder = _training_folder(out_training_data, ".pctd", tool)
    _prepare(tool, folder, "classification", training, validation, size, limit, srid, have, excluded,
             reference_height, keep)
    return Result(folder)


# ---- object boxes


def box_of(shape):
    """A multipatch feature's box (INFERRED, ADR-249 §5.3): the minimum rotated rectangle of its vertices in x and y,
    the lowest and highest vertex in z; length along its longer side, yaw that side's angle from the x axis in
    [−π/2, π/2)."""
    from ._mptools import _xyz
    xyz = _xyz(shape)
    if not len(xyz):
        return None
    rect = shapely.minimum_rotated_rectangle(shapely.MultiPoint(xyz[:, :2]))
    if rect.geom_type != "Polygon":
        rect = rect.buffer(1e-6, cap_style="square")
    c = np.asarray(rect.exterior.coords)[:4]
    e1, e2 = c[1] - c[0], c[2] - c[1]
    a, b = np.hypot(*e1), np.hypot(*e2)
    major = e1 if a >= b else e2
    yaw = math.atan2(major[1], major[0])
    yaw = (yaw + math.pi / 2) % math.pi - math.pi / 2
    centre = c.mean(axis=0)
    z0, z1 = float(xyz[:, 2].min()), float(xyz[:, 2].max())
    return np.array([centre[0], centre[1], (z0 + z1) / 2, max(a, b), min(a, b), max(z1 - z0, 1e-6), yaw])


def footprint(box):
    cx, cy, _, length, width, _, yaw = box[:7]
    c, s = math.cos(yaw), math.sin(yaw)
    corners = [(-length / 2, -width / 2), (length / 2, -width / 2), (length / 2, width / 2), (-length / 2, width / 2)]
    return shapely.Polygon([(cx + c * u - s * v, cy + s * u + c * v) for u, v in corners])


def iou3d(a, b):
    """Intersection volume over union volume of two yawed boxes, as Detect Objects From Point Cloud's page defines the
    overlap."""
    inter = footprint(a).intersection(footprint(b)).area
    dz = min(a[2] + a[5] / 2, b[2] + b[5] / 2) - max(a[2] - a[5] / 2, b[2] - b[5] / 2)
    volume = inter * max(0.0, dz)
    union = a[3] * a[4] * a[5] + b[3] * b[4] * b[5] - volume
    return volume / union if union > 0 else 0.0


def _objects(features, code_field, tool):
    from . import _multipatch as M
    M.require(_path(features), "ddd", tool.rsplit(".", 1)[-1], "in_training_features")
    _, _, rows = M.read(_path(features))
    out = []
    for _, values, shape in rows:
        box = box_of(shape)
        if box is None:
            continue
        code = 0
        if _is_given(code_field):
            found = {k.lower(): v for k, v in values.items()}
            if str(code_field).lower() not in found:
                raise ValueError(f"{tool}: the features have no field '{code_field}'.")
            code = int(found[str(code_field).lower()] or 0)
        out.append(np.append(box, code))
    return np.array(out, float).reshape(-1, 8)


def PreparePointCloudObjectDetectionTrainingData(in_point_cloud, in_training_features, in_validation_features,
                                                 block_size, out_training_data, training_boundary=None,
                                                 training_code_field=None, validation_point_cloud=None,
                                                 validation_boundary=None, validation_code_field=None,
                                                 block_point_limit=500000, reference_height=None,
                                                 excluded_class_codes=None, blocks_contain_objects="ALL_BLOCKS"):
    """Blocks of a point cloud and the boxes of its objects for TrainPointCloudObjectDetectionModel, in grapy's
    ``.pcotd`` folder (the module's docstring). An object is in a block when its box's centre is (INFERRED). The quartile
    ratio of each object type's volumes is reported, as documented."""
    tool = _tool("PreparePointCloudObjectDetectionTrainingData")
    excluded = _codes(excluded_class_codes)
    which = _word(blocks_contain_objects, "ALL_BLOCKS", ("BLOCKS_WITH_OBJECTS", "ALL_BLOCKS"), tool,
                  "blocks_contain_objects")
    training, validation, srid, have = _sources(tool, in_point_cloud, training_boundary, validation_point_cloud,
                                                validation_boundary, excluded, reference_height)
    size = _linear(block_size, srid, tool, "block_size")
    objects = {"training": _objects(in_training_features, training_code_field, tool),
               "validation": _objects(in_validation_features, validation_code_field, tool)}

    def boxes_in(split):
        found = objects[split]

        def inside(origin, s):
            if not len(found):
                return np.zeros((0, 8))
            x0, y0 = origin
            hit = (found[:, 0] >= x0) & (found[:, 0] < x0 + s) & (found[:, 1] >= y0) & (found[:, 1] < y0 + s)
            return found[hit]
        return inside

    keep = None
    if which == "BLOCKS_WITH_OBJECTS":     # the page: it does not affect the validation data
        keep = {"training": lambda _indexes, origin: len(boxes_in("training")(origin, size)) > 0}
    for code in np.unique(objects["training"][:, 7]).astype(int) if len(objects["training"]) else []:
        volumes = np.prod(objects["training"][objects["training"][:, 7] == code][:, 3:6], axis=1)
        q1, q3 = np.percentile(volumes, [25, 75])
        AddMessage(f"Object code {code}: {len(volumes)} objects, volume quartile ratio "
                   f"{(q3 / q1 if q1 > 0 else float('inf')):.2f}.")
    folder = _training_folder(out_training_data, ".pcotd", tool)
    _prepare(tool, folder, "object_detection", training, validation, size, int(block_point_limit or 500000), srid,
             have, excluded, reference_height, keep, {"training": boxes_in("training"),
                                                      "validation": boxes_in("validation")})
    return Result(folder)


# ---- reading the training data back


class Split:
    def __init__(self, folder, split):
        with np.load(os.path.join(folder, split, "blocks.npz")) as data:
            self.arrays = {k: data[k] for k in data.files}
        self.offsets = self.arrays["offsets"]

    def __len__(self):
        return len(self.offsets) - 1

    def block(self, k):
        s, e = self.offsets[k], self.offsets[k + 1]
        out = {"xyz": self.arrays["xyz"][s:e], "attributes": self.arrays["attributes"][s:e],
               "classification": self.arrays["classification"][s:e].astype(np.int64),
               "origin": self.arrays["origin"][k]}
        if "boxes" in self.arrays:
            bo = self.arrays["box_offsets"]
            out["boxes"] = self.arrays["boxes"][bo[k]:bo[k + 1]]
        return out


def read_training(value, kind, tool):
    folder = _path(value)
    manifest = os.path.join(folder, MANIFEST)
    if not os.path.isdir(folder):
        raise ValueError(f"{tool}: the training data '{value}' does not exist.")
    if not os.path.isfile(manifest):
        raise NotBuiltError(f"{tool}: '{value}' is not training data grapy wrote: ArcGIS Pro's .pctd and .pcotd are "
                            "folders of HDF5 blocks whose layout Esri does not publish, so grapy reads only its own "
                            "(ADR-249 §5.2).")
    with open(manifest, encoding="utf-8") as handle:
        document = json.load(handle)
    if document.get("format") != FORMAT:
        raise NotBuiltError(f"{tool}: '{value}' is not training data grapy wrote (ADR-249 §5.2).")
    if document.get("kind") != kind:
        raise ValueError(f"{tool}: '{value}' holds {document.get('kind')} training data, not {kind}.")
    return document, Split(folder, "training"), Split(folder, "validation")


def _attributes_wanted(value, available, tool):
    wanted = []
    for name in ([] if not _is_given(value) else (value.split(";") if isinstance(value, str) else list(value))):
        name = str(name).strip().upper()
        if not name:
            continue
        if name not in ATTRIBUTES:
            raise ValueError(f"{tool}: attributes must be among {', '.join(ATTRIBUTES)}; got '{name}'.")
        if name not in available:
            raise ValueError(f"{tool}: the training data does not carry {name}.")
        wanted.append(name)
    return [a for a in ATTRIBUTES if a in wanted]


def normalised(block, attributes, mean, std, size, points, rng):
    """A block as the network's input: ``points`` points (dealt at random, repeated when it holds fewer); x and y about
    the block's centre and z above its lowest point, in half block sizes; the attributes standardised. Answers (xyz,
    features, the positions of the block's points taken)."""
    n = len(block["xyz"])
    take = rng.permutation(n)
    if n < points:
        take = np.concatenate([take, rng.integers(0, n, points - n)])
    take = take[:points]
    half = size / 2.0
    o = block["origin"]
    xyz = (block["xyz"][take] - np.array([o[0] + half, o[1] + half, o[2]])) / half
    columns = [ATTRIBUTES.index(a) for a in attributes]
    features = (block["attributes"][take][:, columns] - mean) / std if columns else np.zeros((points, 0), np.float32)
    return xyz.astype(np.float32), np.asarray(features, np.float32), take


# ---- classification


MONITOR = {"VALIDATION_LOSS": ("valid_loss", False), "RECALL": ("recall", True), "F1_SCORE": ("f1", True),
           "PRECISION": ("precision", True), "ACCURACY": ("accuracy", True)}


def _per_class(matrix):
    """Per-class precision, recall and F1 of a confusion matrix (rows true, columns predicted)."""
    tp = np.diag(matrix).astype(float)
    predicted, actual = matrix.sum(0).astype(float), matrix.sum(1).astype(float)
    with np.errstate(divide="ignore", invalid="ignore"):
        precision = np.where(predicted > 0, tp / predicted, 0.0)
        recall = np.where(actual > 0, tp / actual, 0.0)
        f1 = np.where(precision + recall > 0, 2 * precision * recall / (precision + recall), 0.0)
    return precision, recall, f1


def _remap(codes, remap, targets, background):
    out = codes.copy()
    for current, new in remap:
        out[codes == current] = new
    if targets:
        out[~np.isin(out, targets)] = background
    return out


def _focal(logits, labels, torch, gamma=2.0, ignore=-100):
    """Focal loss (Lin et al. 2017), γ 2, over the labelled points."""
    keep = labels != ignore
    if not keep.any():
        return logits.sum() * 0.0
    logp = torch.log_softmax(logits[keep], -1)
    picked = logp.gather(1, labels[keep][:, None])[:, 0]
    return (-(1 - picked.exp()) ** gamma * picked).mean()


def TrainPointCloudClassificationModel(in_training_data, out_model_location, out_model_name, pretrained_model=None,
                                       attributes=None, min_points=0, class_remap=None, target_classes=None,
                                       background_class=None, class_descriptions=None,
                                       model_selection_criteria="RECALL", max_epochs=25, epoch_iterations=100,
                                       learning_rate=None, batch_size=2, early_stop="EARLY_STOP",
                                       learning_rate_strategy="ONE_CYCLE", architecture="POINT_TRANSFORMER_V3",
                                       loss_function="CROSS_ENTROPY_LOSS"):
    """EQUIVALENT: trains the network ``architecture`` names (``_dlpcnets``: RandLA-Net, SQN, PointCNN, Point Transformer
    V3) on grapy's ``.pctd`` and writes ``<out_model_location>/<out_model_name>/<out_model_name>.emd``, ``.pth`` and
    ``.dlpk``; each epoch's weights in ``<out_model_name>.checkpoints``, with ``<out_model_name>_stats.csv`` (precision,
    recall and F1 per class and epoch) beside them; and ``<out_model_name>_epoch_stats.csv`` in the model's folder.

    INFERRED (ADR-249 §5.3): a block is resampled to the training data's largest block, at most its block point limit;
    the selection metrics are the classes' means (macro); with ``target_classes`` only blocks holding one are trained on
    and ``background_class`` is required; SQN learns from the points of class 0 (never classified) nothing — they are
    its unlabelled points — and the other networks learn class 0 as a class; FOCAL_LOSS's γ is 2."""
    from ._dlpcnets import CLASSIFIERS, build_classifier
    tool = _tool("TrainPointCloudClassificationModel")
    torch = dl.framework(tool)
    dl.threads(tool)
    document, train_split, valid_split = read_training(in_training_data, "classification", tool)
    pretrained = dl.read_definition(pretrained_model, tool, kinds=(CLASSIFICATION,)) if _is_given(pretrained_model) \
        else None
    arch = _word(architecture, "POINT_TRANSFORMER_V3", CLASSIFIERS, tool, "architecture")
    if pretrained is not None:
        arch = pretrained.grapy["architecture"]
    wanted = _attributes_wanted(attributes, document["attributes"], tool)
    if pretrained is not None and wanted != pretrained.grapy["attributes"]:
        raise ValueError(f"{tool}: the training data's attributes ({', '.join(wanted) or 'none'}) are not the "
                         f"pretrained model's ({', '.join(pretrained.grapy['attributes']) or 'none'}).")
    remap = [(int(float(a)), int(float(b))) for a, b in _pairs(class_remap)]
    targets = _codes(target_classes)
    if targets and not _is_given(background_class):
        raise ValueError(f"{tool}: background_class is required with target_classes.")
    background = int(background_class) if _is_given(background_class) else None
    monitor_key, larger = MONITOR[_word(model_selection_criteria, "RECALL", tuple(MONITOR), tool,
                                        "model_selection_criteria")]
    early = _word(early_stop, "EARLY_STOP", ("EARLY_STOP", "NO_EARLY_STOP"), tool, "early_stop") == "EARLY_STOP"
    strategy = _word(learning_rate_strategy, "ONE_CYCLE", ("ONE_CYCLE", "FIXED"), tool, "learning_rate_strategy")
    loss_kind = _word(loss_function, "CROSS_ENTROPY_LOSS", ("CROSS_ENTROPY_LOSS", "FOCAL_LOSS"), tool,
                      "loss_function")
    size = float(document["block_size"])
    rng = np.random.default_rng(dl.seed(tool=tool))

    def blocks(split, filtered):
        out = []
        for k in range(len(split)):
            b = split.block(k)
            if len(b["xyz"]) < max(1, int(min_points or 0)):
                continue
            original = b["classification"]
            b["classification"] = _remap(original, remap, targets, background)
            if filtered and targets and not np.isin(_remap(original, remap, [], None), targets).any():
                continue
            out.append(b)
        return out

    training, validation = blocks(train_split, True), blocks(valid_split, False)
    if not training or not validation:
        raise ExecuteError(f"ERROR: {tool}: no training or no validation block is left after min_points and "
                           "target_classes.")
    unlabelled = arch == "SQN"
    codes = sorted({int(c) for b in training for c in np.unique(b["classification"])} - ({0} if unlabelled else set()))
    if pretrained is not None:
        if codes and not set(codes) <= set(pretrained.grapy["classes"]):
            raise ValueError(f"{tool}: the training data's class codes {codes} are not the pretrained model's "
                             f"{pretrained.grapy['classes']}; remap them (class_remap).")
        codes = list(pretrained.grapy["classes"])
    if len(codes) < 2:
        raise ExecuteError(f"ERROR: {tool}: the training data holds fewer than two classes ({codes}).")
    lookup = {c: k for k, c in enumerate(codes)}
    every = np.concatenate([b["attributes"] for b in training])
    columns = [ATTRIBUTES.index(a) for a in wanted]
    mean = every[:, columns].mean(0) if columns else np.zeros(0)
    std = np.maximum(every[:, columns].std(0), 1e-6) if columns else np.ones(0)
    if pretrained is not None and columns:
        mean, std = np.array(pretrained.grapy["mean"]), np.array(pretrained.grapy["std"])
    points = int(min(int(document["block_point_limit"]), max(len(b["xyz"]) for b in training)))
    if pretrained is not None:
        points = int(pretrained.grapy["points_per_block"])
    model = build_classifier(arch, len(wanted), len(codes), WIDTH)
    if pretrained is not None:
        dl.load_weights(model, pretrained.path_of(), tool)

    def tensor(b, local):
        xyz, features, take = normalised(b, wanted, mean, std, size, points, local)
        labels = np.array([lookup.get(int(c), -100) for c in b["classification"][take]], np.int64)
        return xyz, features, labels

    def batches(items, shuffle, local):
        order = local.permutation(len(items)) if shuffle else np.arange(len(items))
        out = []
        bs = max(1, int(batch_size or 2))
        for s in range(0, len(order), bs):
            group = [tensor(items[k], local) for k in order[s:s + bs]]
            if shuffle and len(group) == 1 and len(order) > 1:
                group.append(tensor(items[order[0]], local))     # batch norm needs two samples in training
            out.append(tuple(torch.from_numpy(np.stack(part)) for part in zip(*group)))
        return out

    fraction = max(1e-6, float(epoch_iterations if _is_given(epoch_iterations) else 100)) / 100.0

    def epoch_batches(epoch):
        local = np.random.default_rng(int(rng.integers(2 ** 31)) + epoch)
        count = max(1, int(math.ceil(len(training) * min(1.0, fraction))))
        chosen = [training[k] for k in local.permutation(len(training))[:count]]
        return batches(chosen, True, local)

    valid_batches = batches(validation, False, np.random.default_rng(0))

    def step(net, batch):
        xyz, features, labels = batch
        logits = net(xyz, features).reshape(-1, len(codes))
        target = labels.reshape(-1)
        if loss_kind == "FOCAL_LOSS":
            return _focal(logits, target, torch)
        if not (target != -100).any():
            return logits.sum() * 0.0
        return torch.nn.functional.cross_entropy(logits, target, ignore_index=-100)

    per_class_history = []

    def evaluate(net, items):
        matrix = np.zeros((len(codes), len(codes)), np.int64)
        losses = []
        for batch in items:
            losses.append(float(step(net, batch)))
            predicted = net(batch[0], batch[1]).argmax(-1).reshape(-1).numpy()
            truth = batch[2].reshape(-1).numpy()
            keep = truth >= 0
            np.add.at(matrix, (truth[keep], predicted[keep]), 1)
        precision, recall, f1 = _per_class(matrix)
        present = matrix.sum(1) > 0
        per_class_history.append((precision, recall, f1))
        total = matrix.sum()
        return {"valid_loss": float(np.mean(losses)), "accuracy": float(np.trace(matrix) / total) if total else 0.0,
                "precision": float(precision[present].mean()) if present.any() else 0.0,
                "recall": float(recall[present].mean()) if present.any() else 0.0,
                "f1": float(f1[present].mean()) if present.any() else 0.0}

    location = _path(out_model_location)
    if not os.path.isdir(location):
        raise ValueError(f"{tool}: out_model_location '{out_model_location}' is not an existing folder.")
    name = str(out_model_name)
    folder = os.path.join(location, name)
    checkpoints = os.path.join(location, f"{name}.checkpoints")
    os.makedirs(checkpoints, exist_ok=True)

    def checkpoint(epoch, net, _scores):
        dl.save_weights(net, os.path.join(checkpoints, f"{name}_epoch_{epoch}.pth"), tool)

    rate = learning_rate
    if not _is_given(rate):
        rate = dl.find_learning_rate(model, epoch_batches(0), step, tool)
        AddMessage(f"{tool}: the learning rate found by the range test is {rate:.3g}.")
    history = dl.fit(model, epoch_batches, valid_batches, step, evaluate, int(max_epochs or 25), float(rate),
                     monitor=monitor_key, larger_is_better=larger, early_stopping=early, tool=tool,
                     schedule=strategy, on_epoch=checkpoint)
    model_stats = os.path.join(checkpoints, f"{name}_stats.csv")
    with open(model_stats, "w", newline="", encoding="utf-8") as out:
        writer = csv.writer(out)
        writer.writerow(["Epoch", "Class_Code", "Precision", "Recall", "F1_Score"])
        for epoch, (precision, recall, f1) in enumerate(per_class_history[-len(history):], start=1):
            for k, code in enumerate(codes):
                writer.writerow([epoch, code, round(float(precision[k]), 6), round(float(recall[k]), 6),
                                 round(float(f1[k]), 6)])
    os.makedirs(folder, exist_ok=True)
    epoch_stats = os.path.join(folder, f"{name}_epoch_stats.csv")
    with open(epoch_stats, "w", newline="", encoding="utf-8") as out:
        writer = csv.writer(out)
        writer.writerow(["Epoch", "Training_Loss", "Validation_Loss", "Accuracy", "Precision", "Recall", "F1_Score"])
        for h in history:
            writer.writerow([h["epoch"], round(h["train_loss"], 6), round(h["valid_loss"], 6), round(h["accuracy"], 6),
                             round(h["precision"], 6), round(h["recall"], 6), round(h["f1"], 6)])
    descriptions = {int(float(a)): str(b) for a, b in _pairs(class_descriptions)}
    dl.save_weights(model, os.path.join(folder, f"{name}.pth"), tool)
    emd = _write_definition(folder, name, CLASSIFICATION, arch, codes, descriptions, {
        "attributes": wanted, "mean": [float(v) for v in mean], "std": [float(v) for v in std], "block_size": size,
        "points_per_block": points, "block_point_limit": int(document["block_point_limit"]),
        "excluded_class_codes": document.get("excluded_class_codes", []),
        "relative_height": "RELATIVE_HEIGHT" in wanted, "width": WIDTH, "batch_size": int(batch_size or 2),
        "history": history})
    package = dl.write_package(emd)
    return Result(package, model_stats, epoch_stats)


def _write_definition(folder, name, model_type, architecture, codes, descriptions, extra):
    """grapy's point cloud .emd (INFERRED, ADR-249 §5.2): the documented keys ADR-248 writes, ModelType
    ``PointCloudClassification`` or ``PointCloudObjectDetection``, and the ``grapy`` object."""
    document = {"Framework": "PyTorch", "ModelConfiguration": dl.GRAPY_CONFIGURATION, "ModelType": model_type,
                "ModelFile": f"{name}.pth", "ModelName": name, "Version": "1",
                "Classes": [{"Value": int(c), "Name": descriptions.get(int(c), str(c)), "Color": dl._colour(k)}
                            for k, c in enumerate(codes)],
                "grapy": {"version": dl.DEFINITION_VERSION, "architecture": architecture,
                          "classes": [int(c) for c in codes], **extra}}
    path = os.path.join(folder, f"{name}.emd")
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(document, handle, indent=1)
    return path


def _model(value, tool, kind):
    from ._dlpcnets import build_classifier, build_detector
    definition = dl.read_definition(value, tool, kinds=(kind,))
    g = definition.grapy
    if kind == CLASSIFICATION:
        model = build_classifier(g["architecture"], len(g["attributes"]), len(g["classes"]), g.get("width", 1.0))
    else:
        model = build_detector(g["point_features"], len(g["anchors"]), tuple(g["grid"]), g.get("width", 1.0))
    dl.load_weights(model, definition.path_of(), tool)
    model.eval()
    return definition, model


def _needs_reference(definition, reference, tool):
    if definition.grapy.get("relative_height") and not _is_given(reference):
        raise ValueError(f"{tool}: the model was trained with RELATIVE_HEIGHT, and reference_height is required.")


def predict(definition, model, found, batch, tool):
    """Each point's class code by the model: the cloud cut into the model's blocks (each point in four), each block's
    class probabilities summed at its points, the most probable class taken."""
    torch = dl.framework(tool)
    g = definition.grapy
    size, points = float(g["block_size"]), int(g["points_per_block"])
    codes = np.array(g["classes"])
    mean, std = np.array(g["mean"], np.float32), np.array(g["std"], np.float32)
    rng = np.random.default_rng(0)
    totals = np.zeros((len(found["x"]), len(codes)), np.float64)
    blocks = _blocks_of(found, size, points, rng)
    pending = []

    def run(group):
        xyz = torch.from_numpy(np.stack([p[0] for p in group]))
        features = torch.from_numpy(np.stack([p[1] for p in group]))
        with torch.no_grad():
            probability = torch.softmax(model(xyz, features), -1).numpy()
        for (_, _, take, indexes), p in zip(group, probability):
            np.add.at(totals, indexes[take], p)

    for indexes, origin in blocks:
        block = {"xyz": np.column_stack([found["x"][indexes], found["y"][indexes], found["z"][indexes]]),
                 "attributes": found["attributes"][indexes],
                 "origin": (origin[0], origin[1], float(found["z"][indexes].min()))}
        xyz, features, take = normalised(block, g["attributes"], mean, std, size, points, rng)
        pending.append((xyz, features, take, indexes))
        if len(pending) >= max(1, int(batch)):
            run(pending)
            pending = []
    if pending:
        run(pending)
    return codes[totals.argmax(1)] if len(totals) else np.zeros(0, int)


def ClassifyPointCloudUsingTrainedModel(in_point_cloud, in_trained_model, output_classes=None,
                                        in_class_mode="EDIT_ALL", target_classes=None, compute_stats="COMPUTE_STATS",
                                        boundary=None, update_pyramid="UPDATE_PYRAMID", reference_height=None,
                                        excluded_class_codes=None, batch_size=None):
    """The LAS dataset's points classified by a model grapy trained, their class codes rewritten in its LAS files.
    ``output_classes`` limits the model's classes written; ``in_class_mode`` EDIT_SELECTED edits only the points of
    ``target_classes`` and PRESERVE_SELECTED every other; ``boundary`` limits the points evaluated; excluded classes and
    withheld points are not evaluated, as documented. A model on ArcGIS Online or Living Atlas (a URL) is refused: a
    user tool has no network, and those models are Esri's.

    INFERRED (ADR-249 §5.3): a point whose predicted class is not among ``output_classes`` keeps its code; without a
    boundary every point is evaluated; ``batch_size`` defaults to the training batch size; ``update_pyramid`` has no
    pyramid to update in grapy."""
    tool = _tool("ClassifyPointCloudUsingTrainedModel")
    if str(in_trained_model).lower().startswith(("http://", "https://")):
        _refuse(tool, "in_trained_model", in_trained_model, "a model on a portal is fetched over the network, which a "
                                                            "user tool does not have, and Esri's published models run "
                                                            "on arcgis.learn (ADR-248 §5.2)")
    if not _las.is_las(in_point_cloud):
        raise ValueError(f"{tool}: in_point_cloud must be a LAS dataset or LAS dataset layer.")
    definition, model = _model(in_trained_model, tool, CLASSIFICATION)
    _needs_reference(definition, reference_height, tool)
    mode = _word(in_class_mode, "EDIT_ALL", ("EDIT_ALL", "EDIT_SELECTED", "PRESERVE_SELECTED"), tool, "in_class_mode")
    selected = _codes(target_classes)
    if mode != "EDIT_ALL" and not selected:
        raise ValueError(f"{tool}: target_classes is required with {mode}.")
    found, paths, _, _ = cloud(in_point_cloud, tool, area_of(boundary), _codes(excluded_class_codes),
                               reference_height)
    if mode == "EDIT_SELECTED":
        editable = np.isin(found["classification"], selected)
    elif mode == "PRESERVE_SELECTED":
        editable = ~np.isin(found["classification"], selected)
    else:
        editable = np.ones(len(found["x"]), bool)
    predicted = predict(definition, model, found, batch_size if _is_given(batch_size)
                        else definition.grapy.get("batch_size", 2), tool)
    wanted = _codes(output_classes) or list(definition.grapy["classes"])
    unknown = sorted(set(wanted) - set(definition.grapy["classes"]))
    if unknown:
        raise ValueError(f"{tool}: output_classes {unknown} are not classes of the model.")
    write = editable & np.isin(predicted, wanted)
    changed = []
    for number, path in enumerate(paths):
        here = write & (found["file"] == number)
        if not here.any():
            continue
        las = _las.read_file(path)
        classes = np.asarray(las.classification, np.int64).copy()
        for code in np.unique(predicted[here]):
            _las._class_range(las, int(code), "ClassifyPointCloudUsingTrainedModel")
        classes[found["index"][here]] = predicted[here]
        las.classification = classes.astype(np.uint8)
        _las._write_las(path, las)
        changed.append(path)
    AddMessage(f"{tool}: {int(write.sum())} of {len(found['x'])} points classified.")
    _las._update_stats(in_point_cloud, changed, str(compute_stats or "COMPUTE_STATS").upper() == "COMPUTE_STATS")
    return Result(_las._lasd_path(in_point_cloud))


def EvaluatePointCloudClassificationModel(in_trained_model, in_point_cloud, target_folder, base_name, boundary=None,
                                          class_remap=None, reference_height=None, excluded_class_codes=None):
    """Each model's classification of a well-classified reference point cloud, compared with its class codes:
    ``<base_name>_ConfusionMatrices.csv`` (``Model``, ``Reference_Class``, a ``C_<code>`` count per predicted class,
    ``Total``), ``<base_name>_ModelStatistics.csv`` (``Model``, ``Accuracy``, ``Precision``, ``Recall``, ``F1_Score``)
    and ``<base_name>_ClassCodeStatistics.csv`` (``Model``, ``Class_Code`` and the same four). ``class_remap`` maps the
    reference's codes to the models'.

    INFERRED (ADR-249 §5.3): a model's precision, recall and F1 are its classes' means; a class's accuracy is (true
    positives + true negatives) over all points; the confusion matrix charts (PNG) are not drawn — grapy carries no
    plotting library — and their counts are the confusion matrix table's."""
    tool = _tool("EvaluatePointCloudClassificationModel")
    rows = []
    for row in _pairs(in_trained_model, 2):
        rows.append((row[0], int(float(row[1])) if str(row[1]).strip() else None))
    if not rows:
        raise ValueError(f"{tool}: in_trained_model names no model.")
    remap = [(int(float(a)), int(float(b))) for a, b in _pairs(class_remap)]
    found, _, _, _ = cloud(in_point_cloud, tool, area_of(boundary), _codes(excluded_class_codes), reference_height)
    reference = _remap(found["classification"], remap, [], None)
    folder = _path(target_folder)
    os.makedirs(folder, exist_ok=True)
    models = []
    for path, batch in rows:
        definition, model = _model(path, tool, CLASSIFICATION)
        _needs_reference(definition, reference_height, tool)
        models.append((path, definition, model, batch))
    first = models[0][1].grapy["classes"]
    if any(m[1].grapy["classes"] != first for m in models):
        raise ValueError(f"{tool}: the models do not share their class codes, and the page requires it.")
    codes = sorted(set(first) | {int(c) for c in np.unique(reference)})
    lookup = {c: k for k, c in enumerate(codes)}
    out_matrix = os.path.join(folder, f"{base_name}_ConfusionMatrices.csv")
    out_models = os.path.join(folder, f"{base_name}_ModelStatistics.csv")
    out_classes = os.path.join(folder, f"{base_name}_ClassCodeStatistics.csv")
    with open(out_matrix, "w", newline="", encoding="utf-8") as m_out, \
            open(out_models, "w", newline="", encoding="utf-8") as s_out, \
            open(out_classes, "w", newline="", encoding="utf-8") as c_out:
        matrices, statistics, per_class = csv.writer(m_out), csv.writer(s_out), csv.writer(c_out)
        matrices.writerow(["Model", "Reference_Class", *(f"C_{c}" for c in codes), "Total"])
        statistics.writerow(["Model", "Accuracy", "Precision", "Recall", "F1_Score"])
        per_class.writerow(["Model", "Class_Code", "Accuracy", "Precision", "Recall", "F1_Score"])
        for number, (path, definition, model, batch) in enumerate(models, start=1):
            predicted = predict(definition, model, found, batch or definition.grapy.get("batch_size", 2), tool)
            matrix = np.zeros((len(codes), len(codes)), np.int64)
            np.add.at(matrix, ([lookup[int(c)] for c in reference], [lookup[int(c)] for c in predicted]), 1)
            label = os.path.basename(str(path))
            for k, code in enumerate(codes):
                matrices.writerow([label, code, *matrix[k].tolist(), int(matrix[k].sum())])
            precision, recall, f1 = _per_class(matrix)
            total = matrix.sum()
            present = (matrix.sum(1) + matrix.sum(0)) > 0
            statistics.writerow([label, round(float(np.trace(matrix) / total) if total else 0.0, 6),
                                 round(float(precision[present].mean()), 6), round(float(recall[present].mean()), 6),
                                 round(float(f1[present].mean()), 6)])
            for k, code in enumerate(codes):
                tn = total - matrix[k].sum() - matrix[:, k].sum() + matrix[k, k]
                per_class.writerow([label, code, round(float((matrix[k, k] + tn) / total) if total else 0.0, 6),
                                    round(float(precision[k]), 6), round(float(recall[k]), 6), round(float(f1[k]), 6)])
            AddMessage(f"{tool}: model {number} ({label}): overall accuracy "
                       f"{(np.trace(matrix) / total if total else 0):.4f}.")
    return Result(out_matrix, out_models, out_classes)


# ---- object detection (SECOND)


SECOND_DEFAULTS = {"VOXEL_POINT_LIMIT": 5, "MAX_TRAINING_VOXELS": 16000, "MAX_VALIDATION_VOXELS": 40000}


class Voxels:
    """The voxel grid of a detector over one block: x and y from the block's corner, z above its lowest point."""

    def __init__(self, size, z_range, width, height, limit, features):
        self.size, self.z_range = float(size), float(z_range)
        self.nx = max(2, int(math.ceil(self.size / width / 2.0)) * 2)
        self.nz = max(2, int(math.ceil(self.z_range / height)))
        self.width, self.height = self.size / self.nx, self.z_range / self.nz
        self.limit, self.features = int(limit), features

    @property
    def grid(self):
        return (self.nz, self.nx, self.nx)

    def voxelise(self, local, attributes, most, rng):
        """(points (V, T, c), mask (V, T, 1), coords (V, 3) of z, y, x): each point's x, y, z, attributes and offsets
        from its voxel's mean (VoxelNet's input); at most ``limit`` points a voxel and ``most`` voxels, both taken at
        random as SECOND does."""
        ix = np.floor(local[:, 0] / self.width).astype(int)
        iy = np.floor(local[:, 1] / self.width).astype(int)
        iz = np.floor(local[:, 2] / self.height).astype(int)
        ok = (ix >= 0) & (ix < self.nx) & (iy >= 0) & (iy < self.nx) & (iz >= 0) & (iz < self.nz)
        order = rng.permutation(int(ok.sum()))
        local, attributes = local[ok][order], attributes[ok][order]
        key = (iz[ok][order] * self.nx + iy[ok][order]) * self.nx + ix[ok][order]
        unique, inverse = np.unique(key, return_inverse=True)
        chosen = rng.permutation(len(unique))[:int(most)]
        keep_voxel = np.full(len(unique), -1)
        keep_voxel[chosen] = np.arange(len(chosen))
        slot = keep_voxel[inverse]
        order_slot = np.argsort(slot, kind="stable")
        first = np.searchsorted(slot[order_slot], slot[order_slot], side="left")
        rank = np.empty(len(slot), int)
        rank[order_slot] = np.arange(len(slot)) - first
        use = (slot >= 0) & (rank < self.limit)
        v_count = len(chosen)
        c = 3 + attributes.shape[1] + 3
        points = np.zeros((v_count, self.limit, c), np.float32)
        mask = np.zeros((v_count, self.limit, 1), np.float32)
        scaled = local / np.array([self.size, self.size, self.z_range])
        values = np.column_stack([scaled, attributes])
        points[slot[use], rank[use], :values.shape[1]] = values[use]
        mask[slot[use], rank[use], 0] = 1.0
        sums = np.zeros((v_count, 3))
        np.add.at(sums, slot[use], scaled[use])
        counts = np.maximum(mask.sum(1), 1)
        centre = sums / counts
        points[slot[use], rank[use], values.shape[1]:] = scaled[use] - centre[slot[use]]
        coords = np.column_stack([unique[chosen] // (self.nx * self.nx), (unique[chosen] // self.nx) % self.nx,
                                  unique[chosen] % self.nx])
        return points, mask, coords


class Anchors:
    """SECOND's anchors: at every cell of the bird's-eye view the network answers (half the voxel grid), two per object
    code — its training objects' mean size, at yaw 0 and π/2 — at their mean height (INFERRED, ADR-249 §5.3)."""

    def __init__(self, sizes, voxels):
        self.codes = [int(c) for c in sizes]
        cells = voxels.nx // 2
        step = voxels.size / cells
        centres = (np.arange(cells) + 0.5) * step
        gy, gx = np.meshgrid(centres, centres, indexing="ij")
        out, owner = [], []
        for k, code in enumerate(self.codes):
            length, width, height, z = sizes[code]
            for yaw in (0.0, math.pi / 2):
                a = np.zeros((cells, cells, 7))
                a[..., 0], a[..., 1], a[..., 2] = gx, gy, z
                a[..., 3], a[..., 4], a[..., 5], a[..., 6] = length, width, height, yaw
                out.append(a)
                owner.append(k)
        self.boxes = np.stack(out)                  # (A, H, W, 7)
        self.owner = np.array(owner)
        self.flat = self.boxes.reshape(-1, 7)
        self.polygons = np.array([footprint(b) for b in self.flat], dtype=object)

    def targets(self, gt, positive=0.6, negative=0.45):
        """Labels (1, 0, −1 to ignore) and encoded residuals of every anchor for the ground truth boxes (x, y, z, l,
        w, h, yaw, code index), by bird's-eye IoU: SECOND's thresholds for cars, each object's best anchor positive."""
        n = len(self.flat)
        labels, residuals = np.zeros(n, np.float32), np.zeros((n, 7), np.float32)
        if not len(gt):
            return labels, residuals
        owners = np.repeat(self.owner, len(self.flat) // len(self.owner))
        polys = np.array([footprint(b) for b in gt], dtype=object)
        inter = shapely.area(shapely.intersection(self.polygons[:, None], polys[None, :]))
        area_a = (self.flat[:, 3] * self.flat[:, 4])[:, None]
        area_b = (gt[:, 3] * gt[:, 4])[None, :]
        iou = inter / np.maximum(area_a + area_b - inter, 1e-12)
        iou[owners[:, None] != gt[None, :, 7].astype(int)] = 0.0
        best = iou.argmax(1)
        top = iou.max(1)
        labels[top < negative] = 0
        labels[(top >= negative) & (top < positive)] = -1
        labels[top >= positive] = 1
        for g in range(len(gt)):
            if iou[:, g].max() > 0:
                k = int(iou[:, g].argmax())
                labels[k], best[k] = 1, g
        pos = labels == 1
        residuals[pos] = encode(self.flat[pos], gt[best[pos], :7])
        return labels, residuals


def encode(anchor, box):
    """VoxelNet's residuals, as SECOND uses them: centre offsets over the anchor's diagonal (z over its height), log
    size ratios, and the yaw difference."""
    d = np.hypot(anchor[:, 3], anchor[:, 4])
    return np.column_stack([(box[:, 0] - anchor[:, 0]) / d, (box[:, 1] - anchor[:, 1]) / d,
                            (box[:, 2] - anchor[:, 2]) / anchor[:, 5], np.log(box[:, 3] / anchor[:, 3]),
                            np.log(box[:, 4] / anchor[:, 4]), np.log(box[:, 5] / anchor[:, 5]),
                            box[:, 6] - anchor[:, 6]])


def decode(anchor, r):
    d = np.hypot(anchor[:, 3], anchor[:, 4])
    yaw = anchor[:, 6] + r[:, 6]
    yaw = (yaw + math.pi / 2) % math.pi - math.pi / 2
    return np.column_stack([anchor[:, 0] + r[:, 0] * d, anchor[:, 1] + r[:, 1] * d, anchor[:, 2] + r[:, 2] * anchor[:, 5],
                            anchor[:, 3] * np.exp(np.clip(r[:, 3], -4, 4)), anchor[:, 4] * np.exp(np.clip(r[:, 4], -4, 4)),
                            anchor[:, 5] * np.exp(np.clip(r[:, 5], -4, 4)), yaw])


def nms3d(boxes, scores, overlap):
    """Greedy non-maximum suppression by 3D IoU: the most confident box kept, every box overlapping it by more than
    ``overlap`` dropped (Felzenszwalb et al. 2010; the page's NMS)."""
    boxes = np.asarray(boxes, float).reshape(-1, 7)
    if not len(boxes):
        return []
    shapes = np.array([footprint(b) for b in boxes], dtype=object)
    volume = boxes[:, 3] * boxes[:, 4] * boxes[:, 5]
    order = np.argsort(-np.asarray(scores), kind="stable")
    keep = []
    while len(order):
        k, rest = order[0], order[1:]
        keep.append(int(k))
        area = shapely.area(shapely.intersection(shapes[k], shapes[rest]))
        dz = np.minimum(boxes[k, 2] + boxes[k, 5] / 2, boxes[rest, 2] + boxes[rest, 5] / 2) -             np.maximum(boxes[k, 2] - boxes[k, 5] / 2, boxes[rest, 2] - boxes[rest, 5] / 2)
        inter = area * np.maximum(dz, 0.0)
        union = volume[k] + volume[rest] - inter
        iou = np.where(union > 0, inter / np.where(union > 0, union, 1.0), 0.0)
        order = rest[iou <= overlap]
    return keep


def average_precision(detections, truths, threshold=0.5):
    """PASCAL VOC's all-point average precision (Everingham et al. 2010) of one object code: ``detections`` (block,
    box, score), ``truths`` per block."""
    total = sum(len(t) for t in truths.values())
    if total == 0:
        return None
    detections = sorted(detections, key=lambda d: -d[2])
    used = {b: np.zeros(len(t), bool) for b, t in truths.items()}
    tp = np.zeros(len(detections))
    for k, (block, box, _) in enumerate(detections):
        found = truths.get(block, [])
        if not len(found):
            continue
        overlaps = [iou3d(box, t) for t in found]
        j = int(np.argmax(overlaps))
        if overlaps[j] >= threshold and not used[block][j]:
            used[block][j] = True
            tp[k] = 1
    if not len(detections):
        return 0.0
    ctp = np.cumsum(tp)
    recall = ctp / total
    precision = ctp / np.arange(1, len(detections) + 1)
    r = np.concatenate([[0], recall, [1]])
    p = np.concatenate([[0], precision, [0]])
    for k in range(len(p) - 2, -1, -1):
        p[k] = max(p[k], p[k + 1])
    changed = np.nonzero(r[1:] != r[:-1])[0]
    return float(np.sum((r[changed + 1] - r[changed]) * p[changed + 1]))


def _collate(items, torch):
    points, masks, coords = [], [], []
    for s, (p, m, c) in enumerate(items):
        points.append(p)
        masks.append(m)
        coords.append(np.column_stack([np.full(len(c), s), c]))
    return (torch.from_numpy(np.concatenate(points)), torch.from_numpy(np.concatenate(masks)),
            torch.from_numpy(np.concatenate(coords).astype(np.int64)))


def _local(block, size):
    o = block["origin"]
    return block["xyz"] - np.array([o[0], o[1], o[2]])


def TrainPointCloudObjectDetectionModel(in_training_data, out_model_location, out_model_name, pretrained_model=None,
                                        architecture="SECD", attributes=None, min_points=0, remap_objects=None,
                                        target_objects=None, train_blocks="ALL_BLOCKS", object_descriptions=None,
                                        model_selection_criteria="AVERAGE_PRECISION", max_epochs=25,
                                        learning_rate_strategy="ONE_CYCLE", learning_rate=None, batch_size=2,
                                        early_stop="NO_EARLY_STOP", architecture_settings=None):
    """EQUIVALENT: trains SECOND (``_dlpcnets``) on grapy's ``.pcotd`` and writes ``<out_model_location>/
    <out_model_name>/<out_model_name>.emd``, ``.pth``, ``.dlpk`` and ``<out_model_name>_epoch_stats.csv``, each epoch's
    weights in ``<out_model_name>.checkpoints``. ``architecture_settings`` takes VOXEL_WIDTH, VOXEL_HEIGHT,
    VOXEL_POINT_LIMIT, MAX_TRAINING_VOXELS and MAX_VALIDATION_VOXELS, as documented.

    INFERRED (ADR-249 §5.3): VOXEL_WIDTH defaults to a 64th of the block, VOXEL_HEIGHT to a 16th of the height range,
    which is 1.25 times the highest training object's top above its block's lowest point; VOXEL_POINT_LIMIT 5 and
    16,000 and 40,000 voxels (SECOND's); anchors as ``Anchors``; average precision is VOC's all-point AP at a 3D IoU of
    0.5, the codes' mean, over detections scoring 0.05 or more after NMS at 0.5; a multipatch box has no heading, so
    SECOND's direction classifier is not trained and yaw lies in [−π/2, π/2)."""
    from ._dlpcnets import DETECTORS, build_detector
    tool = _tool("TrainPointCloudObjectDetectionModel")
    arch = str(architecture or "SECD").upper()
    if ("detection", arch) in REFUSED_ARCHITECTURES:
        _refuse(tool, "architecture", architecture, REFUSED_ARCHITECTURES[("detection", arch)])
    _word(arch, "SECD", DETECTORS, tool, "architecture")
    torch = dl.framework(tool)
    dl.threads(tool)
    F = torch.nn.functional
    document, train_split, valid_split = read_training(in_training_data, "object_detection", tool)
    pretrained = dl.read_definition(pretrained_model, tool, kinds=(DETECTION,)) if _is_given(pretrained_model) \
        else None
    wanted = _attributes_wanted(attributes, document["attributes"], tool)
    remap = {int(float(a)): int(float(b)) for a, b in _pairs(remap_objects)}
    targets = set(_codes(target_objects))
    only_objects = _word(train_blocks, "ALL_BLOCKS", ("OBJECT_BLOCKS", "ALL_BLOCKS"), tool, "train_blocks") \
        == "OBJECT_BLOCKS"
    monitor = _word(model_selection_criteria, "AVERAGE_PRECISION", ("VALIDATION_LOSS", "AVERAGE_PRECISION"), tool,
                    "model_selection_criteria")
    early = _word(early_stop, "NO_EARLY_STOP", ("EARLY_STOP", "NO_EARLY_STOP"), tool, "early_stop") == "EARLY_STOP"
    strategy = _word(learning_rate_strategy, "ONE_CYCLE", ("ONE_CYCLE", "FIXED"), tool, "learning_rate_strategy")
    settings = {}
    for option, value in _pairs(architecture_settings):
        option = str(option).strip().upper()
        if option not in ("VOXEL_WIDTH", "VOXEL_HEIGHT", "VOXEL_POINT_LIMIT", "MAX_TRAINING_VOXELS",
                          "MAX_VALIDATION_VOXELS"):
            raise ValueError(f"{tool}: architecture_settings has no option '{option}'.")
        settings[option] = float(value)
    size = float(document["block_size"])

    def blocks(split, training):
        out = []
        for k in range(len(split)):
            b = split.block(k)
            if len(b["xyz"]) < max(1, int(min_points or 0)):
                continue
            boxes = b["boxes"].copy()
            for current, new in remap.items():
                boxes[b["boxes"][:, 7] == current, 7] = new
            if targets:
                boxes = boxes[np.isin(boxes[:, 7].astype(int), list(targets))]
            b["boxes"] = boxes
            if training and only_objects and not len(boxes):
                continue
            out.append(b)
        return out

    training, validation = blocks(train_split, True), blocks(valid_split, False)
    if not training or not validation:
        raise ExecuteError(f"ERROR: {tool}: no training or no validation block is left.")
    every_box = np.concatenate([b["boxes"] for b in training]).reshape(-1, 8)
    if not len(every_box) and pretrained is None:
        raise ExecuteError(f"ERROR: {tool}: the training blocks hold no object.")
    if pretrained is not None:
        g = pretrained.grapy
        codes, sizes, z_range = list(g["classes"]), {int(k): v for k, v in g["anchor_sizes"].items()}, g["z_range"]
        wanted_p = g["attributes"]
        if wanted != wanted_p:
            raise ValueError(f"{tool}: the training data's attributes are not the pretrained model's.")
        settings = {**g["settings"], **settings}
    else:
        codes = sorted({int(c) for c in every_box[:, 7]})
        sizes, tops = {}, []
        for b in training:
            for box in b["boxes"]:
                tops.append(box[2] + box[5] / 2 - b["origin"][2])
        for code in codes:
            own = []
            for b in training:
                for box in b["boxes"][b["boxes"][:, 7] == code]:
                    own.append([box[3], box[4], box[5], box[2] - b["origin"][2]])
            sizes[code] = [float(v) for v in np.mean(own, axis=0)]
        z_range = float(max(1.25 * max(tops), 1e-3))
    settings = {**SECOND_DEFAULTS, "VOXEL_WIDTH": size / 64.0, "VOXEL_HEIGHT": z_range / 16.0, **settings}
    columns = [ATTRIBUTES.index(a) for a in wanted]
    every = np.concatenate([b["attributes"] for b in training])
    mean = every[:, columns].mean(0) if columns else np.zeros(0)
    std = np.maximum(every[:, columns].std(0), 1e-6) if columns else np.ones(0)
    if pretrained is not None:
        mean, std = np.array(pretrained.grapy["mean"]), np.array(pretrained.grapy["std"])
    voxels = Voxels(size, z_range, settings["VOXEL_WIDTH"], settings["VOXEL_HEIGHT"], settings["VOXEL_POINT_LIMIT"],
                    len(wanted))
    anchors = Anchors(sizes, voxels)
    point_features = 3 + len(wanted) + 3
    model = build_detector(point_features, len(anchors.boxes), voxels.grid, WIDTH)
    if pretrained is not None:
        dl.load_weights(model, pretrained.path_of(), tool)
    lookup = {c: k for k, c in enumerate(codes)}
    rng = np.random.default_rng(dl.seed(tool=tool))

    def prepared(b, most, local_rng):
        local = _local(b, size)
        attrs = (b["attributes"][:, columns] - mean) / std if columns else np.zeros((len(local), 0), np.float32)
        p, m, c = voxels.voxelise(local, attrs.astype(np.float32), most, local_rng)
        gt = b["boxes"][np.isin(b["boxes"][:, 7].astype(int), codes)].copy()
        gt[:, :3] -= np.array([b["origin"][0], b["origin"][1], b["origin"][2]])
        gt[:, 7] = [lookup[int(v)] for v in gt[:, 7]]
        labels, residuals = anchors.targets(gt)
        return p, m, c, labels, residuals, gt

    def batches(items, local_rng, most, shuffle=False):
        out, bs = [], max(1, int(batch_size or 2))
        order = local_rng.permutation(len(items)) if shuffle else np.arange(len(items))
        for s in range(0, len(order), bs):
            group = []
            for k in order[s:s + bs]:
                group.append(prepared(items[k], most, local_rng))
            points, masks, coords = _collate([g[:3] for g in group], torch)
            out.append((points, masks, coords, torch.from_numpy(np.stack([g[3] for g in group])),
                        torch.from_numpy(np.stack([g[4] for g in group])), [g[5] for g in group]))
        return out

    def epoch_batches(epoch):
        local = np.random.default_rng(int(rng.integers(2 ** 31)) + epoch)
        return batches(training, local, settings["MAX_TRAINING_VOXELS"], True)

    valid_batches = batches(validation, np.random.default_rng(0), settings["MAX_VALIDATION_VOXELS"])

    def forward(net, batch):
        points, masks, coords, labels, residuals, _ = batch
        scores, boxes = net(points, masks, coords, labels.shape[0])
        b = scores.shape[0]
        return (scores.reshape(b, -1), boxes.permute(0, 1, 3, 4, 2).reshape(b, -1, 7))

    def step(net, batch):
        scores, boxes = forward(net, batch)
        labels, residuals = batch[3], batch[4]
        care = labels >= 0
        positive = labels == 1
        count = positive.sum().clamp(min=1).float()
        p = torch.sigmoid(scores)
        target = positive.float()
        ce = F.binary_cross_entropy_with_logits(scores, target, reduction="none")
        pt = p * target + (1 - p) * (1 - target)
        alpha = 0.25 * target + 0.75 * (1 - target)
        focal = (alpha * (1 - pt) ** 2 * ce * care).sum() / count
        if positive.any():
            pred, true = boxes[positive], residuals[positive]
            p6, t6 = pred[:, 6], true[:, 6]         # SECOND's sine-error loss: sin(p − t) as two products
            pred = torch.cat([pred[:, :6], (torch.sin(p6) * torch.cos(t6))[:, None]], 1)
            true = torch.cat([true[:, :6], (torch.cos(p6) * torch.sin(t6))[:, None]], 1)
            location = F.smooth_l1_loss(pred, true, beta=1 / 9, reduction="sum") / count
        else:
            location = boxes.sum() * 0.0
        return focal + 2.0 * location

    def detections(net, batch, threshold, overlap):
        scores, boxes = forward(net, batch)
        out = []
        for s in range(scores.shape[0]):
            p = torch.sigmoid(scores[s]).numpy()
            keep = np.nonzero(p >= threshold)[0]
            found = []
            for k in range(len(codes)):
                mine = keep[anchors.owner.repeat(len(anchors.flat) // len(anchors.owner))[keep] == k]
                if not len(mine):
                    continue
                decoded = decode(anchors.flat[mine], boxes[s][mine].numpy())
                for j in nms3d(decoded, p[mine], overlap):
                    found.append((k, decoded[j], float(p[mine][j])))
            out.append(found)
        return out

    def evaluate(net, items):
        losses, per_code, truths = [], {k: [] for k in range(len(codes))}, {k: {} for k in range(len(codes))}
        block = 0
        for batch in items:
            losses.append(float(step(net, batch)))
            for found, gt in zip(detections(net, batch, 0.05, 0.5), batch[5]):
                for k in range(len(codes)):
                    truths[k][block] = [g[:7] for g in gt if int(g[7]) == k]
                for k, box, score in found:
                    per_code[k].append((block, box, score))
                block += 1
        aps = [average_precision(per_code[k], truths[k]) for k in range(len(codes))]
        aps = [a for a in aps if a is not None]
        return {"valid_loss": float(np.mean(losses)), "average_precision": float(np.mean(aps)) if aps else 0.0}

    location = _path(out_model_location)
    if not os.path.isdir(location):
        raise ValueError(f"{tool}: out_model_location '{out_model_location}' is not an existing folder.")
    name = str(out_model_name)
    folder = os.path.join(location, name)
    checkpoints = os.path.join(location, f"{name}.checkpoints")
    os.makedirs(checkpoints, exist_ok=True)

    def checkpoint(epoch, net, _scores):
        dl.save_weights(net, os.path.join(checkpoints, f"{name}_epoch_{epoch}.pth"), tool)

    rate = learning_rate
    if not _is_given(rate):
        rate = dl.find_learning_rate(model, epoch_batches(0), step, tool)
        AddMessage(f"{tool}: the learning rate found by the range test is {rate:.3g}.")
    key, larger = ("valid_loss", False) if monitor == "VALIDATION_LOSS" else ("average_precision", True)
    history = dl.fit(model, epoch_batches, valid_batches, step, evaluate, int(max_epochs or 25), float(rate),
                     monitor=key, larger_is_better=larger, early_stopping=early, tool=tool, schedule=strategy,
                     on_epoch=checkpoint)
    os.makedirs(folder, exist_ok=True)
    epoch_stats = os.path.join(folder, f"{name}_epoch_stats.csv")
    with open(epoch_stats, "w", newline="", encoding="utf-8") as out:
        writer = csv.writer(out)
        writer.writerow(["Epoch", "Training_Loss", "Validation_Loss", "Average_Precision"])
        for h in history:
            writer.writerow([h["epoch"], round(h["train_loss"], 6), round(h["valid_loss"], 6),
                             round(h["average_precision"], 6)])
    descriptions = {int(float(a)): str(b) for a, b in _pairs(object_descriptions)}
    dl.save_weights(model, os.path.join(folder, f"{name}.pth"), tool)
    emd = _write_definition(folder, name, DETECTION, "SECD", codes, descriptions, {
        "attributes": wanted, "mean": [float(v) for v in mean], "std": [float(v) for v in std], "block_size": size,
        "block_point_limit": int(document["block_point_limit"]), "z_range": z_range,
        "settings": settings, "anchor_sizes": {str(k): v for k, v in sizes.items()},
        "anchors": [0] * len(anchors.boxes), "grid": list(voxels.grid), "point_features": point_features,
        "excluded_class_codes": document.get("excluded_class_codes", []),
        "relative_height": "RELATIVE_HEIGHT" in wanted, "width": WIDTH, "batch_size": int(batch_size or 2),
        "descriptions": {str(k): v for k, v in descriptions.items()}, "history": history})
    package = dl.write_package(emd)
    return Result(package, epoch_stats)


def DetectObjectsFromPointCloudUsingTrainedModel(in_point_cloud, in_trained_model, target_objects, out_features,
                                                 batch_size=1, boundary=None, reference_height=None,
                                                 excluded_class_codes=None):
    """Objects found by a detector grapy trained, as multipatch boxes: the cloud cut into the model's blocks (each
    point in four), each block's detections at or above its object's confidence kept, and the detections of each
    object merged by non-maximum suppression at its overlap (3D IoU), as documented. ``target_objects`` rows are
    ``Object Code, Confidence, Overlap``.

    INFERRED (ADR-249 §5.3): the output's fields are ``Object_Code`` (Long), ``Description`` (Text) and
    ``Confidence`` (Double); the box is a prism over its yawed footprint; without a boundary every point is read."""
    from . import _multipatch as M
    from ._gpkg import Column
    tool = _tool("DetectObjectsFromPointCloudUsingTrainedModel")
    if str(in_trained_model).lower().startswith(("http://", "https://")):
        _refuse(tool, "in_trained_model", in_trained_model, "a model on a portal is fetched over the network, which a "
                                                            "user tool does not have, and Esri's published models run "
                                                            "on arcgis.learn (ADR-248 §5.2)")
    torch = dl.framework(tool)
    definition, model = _model(in_trained_model, tool, DETECTION)
    _needs_reference(definition, reference_height, tool)
    g = definition.grapy
    codes = list(g["classes"])
    wanted = {}
    for row in _pairs(target_objects, 3):
        code = int(float(row[0]))
        if code not in codes:
            raise ValueError(f"{tool}: object code {code} is not one the model was trained on ({codes}).")
        confidence = float(row[1]) if str(row[1]).strip() else 0.5
        overlap = float(row[2]) if str(row[2]).strip() else 0.1
        if not 0 <= confidence <= 1 or not 0 <= overlap <= 1:
            raise ValueError(f"{tool}: confidence and overlap lie between 0 and 1.")
        wanted[code] = (confidence, overlap)
    if not wanted:
        raise ValueError(f"{tool}: target_objects names no object code.")
    found, _, srid, _ = cloud(in_point_cloud, tool, area_of(boundary), _codes(excluded_class_codes), reference_height)
    size = float(g["block_size"])
    settings = g["settings"]
    voxels = Voxels(size, g["z_range"], settings["VOXEL_WIDTH"], settings["VOXEL_HEIGHT"],
                    settings["VOXEL_POINT_LIMIT"], len(g["attributes"]))
    anchors = Anchors({int(k): v for k, v in g["anchor_sizes"].items()}, voxels)
    owner = anchors.owner.repeat(len(anchors.flat) // len(anchors.owner))
    columns = [ATTRIBUTES.index(a) for a in g["attributes"]]
    mean, std = np.array(g["mean"], np.float32), np.array(g["std"], np.float32)
    rng = np.random.default_rng(0)
    candidates = {code: ([], []) for code in wanted}
    blocks = _blocks_of(found, size, int(g["block_point_limit"]), rng)
    for s in range(0, len(blocks), max(1, int(batch_size or 1))):
        group, origins = [], []
        for indexes, origin in blocks[s:s + max(1, int(batch_size or 1))]:
            z0 = float(found["z"][indexes].min())
            local = np.column_stack([found["x"][indexes] - origin[0], found["y"][indexes] - origin[1],
                                     found["z"][indexes] - z0])
            attrs = ((found["attributes"][indexes][:, columns] - mean) / std).astype(np.float32) if columns \
                else np.zeros((len(local), 0), np.float32)
            group.append(voxels.voxelise(local, attrs, settings["MAX_VALIDATION_VOXELS"], rng))
            origins.append((origin[0], origin[1], z0))
        points, masks, coords = _collate(group, torch)
        with torch.no_grad():
            scores, boxes = model(points, masks, coords, len(group))
        b = scores.shape[0]
        scores = torch.sigmoid(scores.reshape(b, -1)).numpy()
        boxes = boxes.permute(0, 1, 3, 4, 2).reshape(b, -1, 7).numpy()
        for k in range(b):
            for code, (confidence, _) in wanted.items():
                mine = np.nonzero((owner == codes.index(code)) & (scores[k] >= confidence))[0]
                if not len(mine):
                    continue
                decoded = decode(anchors.flat[mine], boxes[k][mine])
                decoded[:, :3] += np.array(origins[k])
                candidates[code][0].extend(decoded)
                candidates[code][1].extend(scores[k][mine])
    descriptions = g.get("descriptions", {})
    rows = []
    for code, (found_boxes, found_scores) in candidates.items():
        for j in nms3d(found_boxes, found_scores, wanted[code][1]):
            box = found_boxes[j]
            solid = M.prism(footprint(box), box[2] - box[5] / 2, box[2] + box[5] / 2)
            rows.append(([code, descriptions.get(str(code), str(code)), float(found_scores[j])],
                         M.from_triangles(solid)))
    AddMessage(f"{tool}: {len(rows)} objects detected.")
    columns_out = [Column("Object_Code", "Integer"), Column("Description", "String"), Column("Confidence", "Double")]
    path = M.write(_path(out_features), srid, columns_out, rows)
    return Result(path)
