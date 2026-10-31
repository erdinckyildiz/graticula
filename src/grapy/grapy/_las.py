"""LAS datasets: grapy's .lasd, the LAS files it references, and the tools on them — ADR-229.

A LAS file is the ASPRS LAS format (versions 1.0–1.4), a public specification, read and written with laspy. LAZ is read
only when laspy has a LAZ backend installed; Esri's .zlas is not published and is refused.

ArcGIS Pro's .lasd format is not documented ("This .lasd file is a pointer to .las files and surface constraints").
grapy's .lasd is its own (INFERRED, ADR-229 §5.2): a JSON document holding

- ``files``: each LAS file, absolute or relative to the .lasd, with the statistics computed for it — point count,
  extent, z, intensity, counts per class code, return and flag — and the size and time of the file they were computed
  from, which is how Describe's ``needsUpdateStatistics`` knows they are stale;
- ``constraints``: each surface constraint: feature class, height field and surface feature type;
- ``spatialReference``: an EPSG code, from the first file's header unless one is given;
- ``relativePaths``.

A .lasd written by ArcGIS Pro is not JSON, and is refused by name.

Which points a tool reads: a LAS dataset layer's filters (MakeLasDatasetLayer) when given a layer; a .lasd itself reads
every point but the withheld ones, as a new layer's defaults do (INFERRED).
"""
from __future__ import annotations

import json
import math
import os

import numpy as np
import shapely

from . import _gpkg
from ._result import Result
from ._runtime import ExecuteError, NotBuiltError, env
from ._runtime import _is_given  # noqa: E402 — a Raster is never compared with ==

LAYERS: dict = {}
_FORMAT = "grapy LAS dataset (Graticula ADR-229)"
_CONSTRAINT_TYPES = ("anchorpoints", "hardline", "softline", "hardclip", "softclip", "harderase", "softerase",
                     "hardreplace", "softreplace")


def _given(value):
    return _is_given(value)


def _refuse(module, tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.{module}.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _list(value):
    if value is None:
        return []
    if isinstance(value, (list, tuple)):
        return [os.fspath(v) if isinstance(v, os.PathLike) else v for v in value]
    return [v.strip().strip("'\"") for v in str(value).split(";") if v.strip()]


def _absolute(path):
    path = os.fspath(path)
    return path if os.path.isabs(path) else os.path.join(env.workspace, path)


# ---- LAS files


def _check_format(path, tool):
    extension = os.path.splitext(path)[1].lower()
    if extension == ".zlas":
        raise NotBuiltError(f"{tool}: '{path}' is a .zlas file, Esri's compressed LAS, whose format is not published "
                            "(ADR-229 §5.5).")
    if extension == ".laz":
        import laspy
        if not laspy.LazBackend.detect_available():
            raise NotBuiltError(f"{tool}: '{path}' is LAZ, and no LAZ backend (lazrs or laszip) is installed with grapy "
                                "(ADR-229 §5.5).")


def _files_in(inputs, recursion, suffix=(".las", ".laz", ".zlas")):
    """The LAS files named, folders walked (into subfolders with recursion)."""
    out = []
    for item in _list(inputs):
        path = _absolute(item)
        if os.path.isdir(path):
            walker = os.walk(path) if recursion else [(path, [], os.listdir(path))]
            for folder, _, names in walker:
                out.extend(os.path.join(folder, n) for n in sorted(names) if n.lower().endswith(suffix))
        elif os.path.exists(path):
            out.append(path)
        else:
            raise ExecuteError(f"ERROR 000732: {item} does not exist.")
    return out


def read_file(path):
    import laspy
    _check_format(path, "LAS")
    return laspy.read(path)


def _flag(las, name):
    try:
        return np.asarray(las[name]).astype(bool)
    except (ValueError, KeyError):
        return np.zeros(len(las.points), bool)


def arrays(las):
    """The point attributes grapy's tools read, as numpy arrays."""
    return {"x": np.asarray(las.x, float), "y": np.asarray(las.y, float), "z": np.asarray(las.z, float),
            "classification": np.asarray(las.classification, np.int64),
            "return_number": np.asarray(las.return_number, np.int64),
            "number_of_returns": np.asarray(las.number_of_returns, np.int64),
            "intensity": np.asarray(las.intensity, np.int64),
            "synthetic": _flag(las, "synthetic"), "key_point": _flag(las, "key_point"),
            "withheld": _flag(las, "withheld"), "overlap": _flag(las, "overlap")}


def srid_of(las):
    try:
        crs = las.header.parse_crs()
    except Exception:  # noqa: BLE001 — a header whose reference pyproj cannot read has none grapy can use
        return 0
    return (crs.to_epsg() or 0) if crs is not None else 0


def statistics(path):
    """A LAS file's statistics, as LasDatasetStatistics computes them."""
    las = read_file(path)
    a = arrays(las)
    n = len(a["x"])
    header = las.header
    stat = os.stat(path)
    classes = {}
    for code in np.unique(a["classification"]):
        keep = a["classification"] == code
        classes[str(int(code))] = {"count": int(keep.sum()), "zMin": float(a["z"][keep].min()),
                                   "zMax": float(a["z"][keep].max()),
                                   "intensityMin": int(a["intensity"][keep].min()),
                                   "intensityMax": int(a["intensity"][keep].max()),
                                   "synthetic": int(a["synthetic"][keep].sum())}
    returns = {str(int(r)): int((a["return_number"] == r).sum()) for r in np.unique(a["return_number"])}
    box = [float(v) for v in (*header.mins[:2], *header.maxs[:2])] if n else [0.0, 0.0, 0.0, 0.0]
    area = (box[2] - box[0]) * (box[3] - box[1])
    return {"pointCount": n, "version": f"{header.version.major}.{header.version.minor}",
            "pointFormat": int(header.point_format.id), "extent": box,
            "zMin": float(a["z"].min()) if n else None, "zMax": float(a["z"].max()) if n else None,
            "pointSpacing": math.sqrt(area / n) if n and area > 0 else None, "classes": classes, "returns": returns,
            "flags": {name: int(a[name].sum()) for name in ("synthetic", "key_point", "withheld", "overlap")},
            "srid": srid_of(las), "size": stat.st_size, "mtime": stat.st_mtime}


# ---- the .lasd


def is_las(value):
    if str(value) in LAYERS:
        return True
    try:
        text = os.fspath(value)
    except TypeError:
        return False
    return str(text).lower().endswith(".lasd")


def _lasd_path(value):
    layer = LAYERS.get(str(value))
    return layer["source"] if layer else _absolute(value)


class LasDataset:
    def __init__(self, path, files=None, constraints=None, srid=0, relative=False):
        self.path = path
        self.files = files or []          # [{"path": …, "stats": {…} or None}]
        self.constraints = constraints or []
        self.srid = int(srid or 0)
        self.relative = bool(relative)

    @classmethod
    def open(cls, value):
        path = _lasd_path(value)
        if not os.path.exists(path):
            raise ExecuteError(f"ERROR 000732: Input LAS Dataset: Dataset {value} does not exist or is not supported.")
        try:
            data = json.load(open(path, encoding="utf-8"))
        except (ValueError, UnicodeDecodeError):
            raise NotBuiltError(f"'{path}' is not a LAS dataset grapy wrote: ArcGIS Pro's .lasd format is not published, "
                                "so grapy reads only its own (ADR-229 §5.2).") from None
        if data.get("format") != _FORMAT:
            raise NotBuiltError(f"'{path}' is not a LAS dataset grapy wrote (ADR-229 §5.2).")
        return cls(path, data.get("files", []), data.get("constraints", []), data.get("spatialReference", 0),
                   data.get("relativePaths", False))

    def file_paths(self):
        base = os.path.dirname(os.path.abspath(self.path))
        return [os.path.normpath(os.path.join(base, f["path"])) if not os.path.isabs(f["path"]) else f["path"]
                for f in self.files]

    def save(self):
        base = os.path.dirname(os.path.abspath(self.path))
        for entry, full in zip(self.files, self.file_paths()):
            entry["path"] = os.path.relpath(full, base) if self.relative else full
        os.makedirs(base, exist_ok=True)
        with open(self.path, "w", encoding="utf-8", newline="\n") as out:
            json.dump({"format": _FORMAT, "files": self.files, "constraints": self.constraints,
                       "spatialReference": self.srid, "relativePaths": self.relative}, out, indent=1)

    def add(self, paths, compute=False):
        have = {os.path.normcase(p) for p in self.file_paths()}
        for path in paths:
            _check_format(path, "LAS dataset")
            if os.path.normcase(path) in have:
                continue
            stats = statistics(path) if compute else None
            self.files.append({"path": path, "stats": stats})
            have.add(os.path.normcase(path))
            if not self.srid:
                self.srid = stats["srid"] if stats else srid_of(read_file(path))

    def compute(self, overwrite=False):
        for entry, full in zip(self.files, self.file_paths()):
            if overwrite or not entry.get("stats") or _stale(entry["stats"], full):
                entry["stats"] = statistics(full)

    def stats(self):
        return [(full, entry.get("stats")) for entry, full in zip(self.files, self.file_paths())]


def _stale(stats, path):
    if not stats or not os.path.exists(path):
        return True
    stat = os.stat(path)
    return stats.get("size") != stat.st_size or abs(stats.get("mtime", 0) - stat.st_mtime) > 1e-6


def _constraints(rows, tool):
    from ._tin import rows_of
    out = []
    for path, height, sf in (r[:3] for r in rows_of(rows, 3)):
        if not path:
            continue
        kind = str(sf or "").lower().replace("_", "").replace(" ", "")
        if kind not in _CONSTRAINT_TYPES:
            _refuse("management", tool, "SF_type", sf, "a LAS dataset's surface constraints are anchor points, lines, "
                    "and clip, erase and replace polygons, as documented")
        out.append({"path": _absolute(path) if ".gpkg" in str(path).lower() else str(path), "heightField": height or "",
                    "type": kind, "name": os.path.basename(str(path).rstrip("/\\"))})
    return out


# ---- filters: which points a tool reads


_DEFAULTS = {"class_codes": None, "returns": None, "unflagged": True, "synthetic": True, "key_point": True,
             "withheld": False, "overlap": True, "constraints": None}


def filters_of(value):
    layer = LAYERS.get(str(value))
    return dict(layer["filters"]) if layer else dict(_DEFAULTS)


def _selected(a, f):
    keep = np.ones(len(a["x"]), bool)
    if f["class_codes"] is not None:
        keep &= np.isin(a["classification"], f["class_codes"])
    if f["returns"] is not None:
        wanted = np.zeros(len(keep), bool)
        last = a["return_number"] == a["number_of_returns"]
        many = a["number_of_returns"] > 1
        for r in f["returns"]:
            if r == "LAST":
                wanted |= last
            elif r == "FIRST_OF_MANY":
                wanted |= many & (a["return_number"] == 1)
            elif r == "LAST_OF_MANY":
                wanted |= many & last
            elif r == "SINGLE":
                wanted |= a["number_of_returns"] == 1
            else:
                wanted |= a["return_number"] == int(r)
        keep &= wanted
    flagged = a["synthetic"] | a["key_point"] | a["withheld"] | a["overlap"]
    if not f["unflagged"]:
        keep &= flagged
    for name in ("synthetic", "key_point", "withheld", "overlap"):
        if not f[name]:
            keep &= ~a[name]
    return keep


def points(value, tool, filtered=True):
    """Every point a LAS dataset or layer shows, concatenated over its files, with ``file`` and ``index`` arrays saying
    where each came from."""
    dataset = LasDataset.open(value)
    f = filters_of(value)
    parts = []
    for number, path in enumerate(dataset.file_paths()):
        a = arrays(read_file(path))
        keep = _selected(a, f) if filtered else np.ones(len(a["x"]), bool)
        part = {k: v[keep] for k, v in a.items()}
        part["file"] = np.full(int(keep.sum()), number)
        part["index"] = np.nonzero(keep)[0]
        parts.append(part)
    if not parts:
        return {k: np.zeros(0) for k in ("x", "y", "z", "classification", "return_number", "number_of_returns",
                                          "intensity", "file", "index")}, dataset
    return {k: np.concatenate([p[k] for p in parts]) for k in parts[0]}, dataset


def _active_constraints(value, dataset):
    wanted = filters_of(value)["constraints"]
    return [c for c in dataset.constraints if wanted is None or c["name"] in wanted]


def surface(value, tool, z_factor=1.0, thinning=None):
    """A TIN Surface of a LAS dataset's points and surface constraints."""
    from . import _tin
    found, dataset = points(value, tool)
    xyz = np.column_stack([found["x"], found["y"], found["z"] * z_factor])
    if thinning is not None:
        xyz = _thinned(xyz, *thinning, origin=_extent(dataset, found))
    inputs = []
    for c in _active_constraints(value, dataset):
        kind = "masspoints" if c["type"] == "anchorpoints" else c["type"]
        features, _, _ = _tin.read_features(c["path"], c["heightField"], kind, None, tool)
        inputs.extend((kind, shape, tag, height) for shape, pending, height, tag in features if not pending)
    pts, segments, polygons = _tin.surface_inputs(inputs)
    return _tin.triangulate([*map(tuple, xyz), *pts], segments, polygons, srid=dataset.srid)


def _extent(dataset, found):
    if len(found["x"]):
        return float(found["x"].min()), float(found["y"].min()), float(found["x"].max()), float(found["y"].max())
    return 0.0, 0.0, 0.0, 0.0


def _thinned(xyz, size, method, origin):
    """One point per square window of ``size``, the grid anchored at the dataset's lower-left corner (INFERRED): the
    lowest (MIN), highest (MAX), or the one closest to the window's mean z (CLOSEST_TO_MEAN), as documented."""
    if not len(xyz):
        return xyz
    cx = np.floor((xyz[:, 0] - origin[0]) / size).astype(np.int64)
    cy = np.floor((xyz[:, 1] - origin[1]) / size).astype(np.int64)
    key = cx * (cy.max() + 2) + cy
    order = np.lexsort((xyz[:, 2], key))
    key_sorted = key[order]
    starts = np.r_[0, np.nonzero(np.diff(key_sorted))[0] + 1]
    ends = np.r_[starts[1:], len(order)]
    chosen = []
    for s, e in zip(starts, ends):
        members = order[s:e]
        if method == "MIN":
            chosen.append(members[0])
        elif method == "MAX":
            chosen.append(members[-1])
        else:
            mean = xyz[members, 2].mean()
            chosen.append(members[np.argmin(np.abs(xyz[members, 2] - mean))])
    return xyz[np.sort(np.array(chosen))]


# ---- management: the dataset


def CreateLasDataset(input, out_las_dataset, folder_recursion="NO_RECURSION", in_surface_constraints=None,
                     spatial_reference=None, compute_stats="NO_COMPUTE_STATS", relative_paths="ABSOLUTE_PATHS",
                     create_las_prj="NO_FILES", extent=None, boundary=None, add_only_contained_files="INTERSECTED_FILES"):
    """A LAS dataset referencing LAS files (folders walked, into subfolders with RECURSION) and surface constraints, as
    documented, its spatial reference the first file's unless one is given; COMPUTE_STATS computes each file's
    statistics. With an ``extent`` or ``boundary``, only the files whose header extent intersects it — or lies within
    it, with CONTAINED_FILES — are added. See ``_las`` for grapy's .lasd.

    Refused: ``create_las_prj`` other than NO_FILES — grapy does not write .prj files beside LAS files; .zlas files."""
    from ._reference import reference_of
    if str(create_las_prj or "NO_FILES").upper() != "NO_FILES":
        _refuse("management", "CreateLasDataset", "create_las_prj", create_las_prj,
                "grapy does not write .prj files beside LAS files")
    path = _absolute(out_las_dataset)
    if not path.lower().endswith(".lasd"):
        path += ".lasd"
    dataset = LasDataset(path, relative=str(relative_paths or "").upper() == "RELATIVE_PATHS")
    files = _files_in(input, str(folder_recursion or "").upper() == "RECURSION")
    files = _within(files, extent, boundary, str(add_only_contained_files or "").upper() == "CONTAINED_FILES")
    if _given(spatial_reference):
        reference = reference_of(spatial_reference)
        dataset.srid = reference.factoryCode if reference is not None else 0
    dataset.add(files, str(compute_stats or "").upper() == "COMPUTE_STATS")
    dataset.constraints = _constraints(in_surface_constraints, "CreateLasDataset")
    dataset.save()
    return Result(path)


def _within(files, extent, boundary, contained):
    if not _given(extent) and not _given(boundary):
        return files
    import laspy
    area = _area_of(extent, boundary)
    out = []
    for path in files:
        with laspy.open(path) as reader:
            box = shapely.box(*reader.header.mins[:2], *reader.header.maxs[:2])
        if (area.covers(box) if contained else area.intersects(box)):
            out.append(path)
    return out


def _area_of(extent, boundary):
    """The area an ``extent`` (an Extent or "xmin ymin xmax ymax") and a ``boundary`` (a polygon feature class) leave —
    their intersection when both are given, as ExtractLas documents."""
    from . import da
    area = None
    if _given(extent):
        if hasattr(extent, "XMin"):
            box = (extent.XMin, extent.YMin, extent.XMax, extent.YMax)
        else:
            box = tuple(float(v) for v in str(extent).split()[:4])
        area = shapely.box(*box)
    if _given(boundary):
        with da.SearchCursor(boundary, ["SHAPE@"]) as rows:
            shapes = [shapely.force_2d(r[0]._shape) for r in rows if r[0] is not None]
        polygons = shapely.union_all(shapes) if shapes else shapely.Polygon()
        area = polygons if area is None else shapely.intersection(area, polygons)
    return area


def AddFilesToLasDataset(in_las_dataset, in_files=None, folder_recursion="NO_RECURSION", in_surface_constraints=None):
    """LAS files and surface constraints added to a LAS dataset, as documented; a file it holds already is not added
    twice. INFERRED: statistics are computed for the added files when the dataset's other files have them."""
    dataset = LasDataset.open(in_las_dataset)
    files = _files_in(in_files, str(folder_recursion or "").upper() == "RECURSION") if _given(in_files) else []
    dataset.add(files, compute=bool(dataset.files) and all(f.get("stats") for f in dataset.files))
    dataset.constraints += _constraints(in_surface_constraints, "AddFilesToLasDataset")
    dataset.save()
    return Result(dataset.path)


def RemoveFilesFromLasDataset(in_las_dataset, in_files=None, in_surface_constraints=None,
                              delete_pyramid="NO_DELETE_PYRAMID"):
    """LAS files, or the files of a folder (its subfolders kept), and surface constraints named by their name, removed
    from a LAS dataset, as documented. The files themselves are not touched. ``delete_pyramid`` has nothing to delete:
    grapy's LAS datasets have no pyramid."""
    dataset = LasDataset.open(in_las_dataset)
    removed = set()
    for item in _list(in_files):
        full = os.path.normcase(os.path.normpath(_absolute(item)))
        removed.add(full)
        removed.update(os.path.normcase(os.path.normpath(p)) for p in dataset.file_paths()
                       if os.path.normcase(os.path.dirname(os.path.normpath(p))) == full)
    keep = [(e, p) for e, p in zip(dataset.files, dataset.file_paths())
            if os.path.normcase(os.path.normpath(p)) not in removed and
            os.path.normcase(os.path.basename(p)) not in {os.path.normcase(str(i)) for i in _list(in_files)}]
    dataset.files = [e for e, p in keep]
    for e, p in keep:
        e["path"] = p
    names = {str(n).lower() for n in _list(in_surface_constraints)}
    dataset.constraints = [c for c in dataset.constraints if c["name"].lower() not in names]
    dataset.save()
    return Result(dataset.path)


def LasDatasetStatistics(in_las_dataset, calculation_type="SKIP_EXISTING_STATS", out_file=None, summary_level="DATASET",
                         delimiter="SPACE", decimal_separator="DECIMAL_POINT"):
    """Each file's statistics computed and kept in the LAS dataset — skipped where they are current, unless
    OVERWRITE_EXISTING_STATS — and, with ``out_file``, a report.

    INFERRED, the report's columns being documented nowhere: with LAS_FILES one row per file, with the columns the LAS
    dataset properties' LAS Files tab lists — ``File_Name LAS_Version Point_Count Point_Spacing Z_Min Z_Max``; with
    DATASET one row per class code, with those of its Statistics tab — ``Class_Code Point_Count Percentage Z_Min Z_Max
    Intensity_Min Intensity_Max Synthetic``; values separated by the delimiter, decimals by the separator."""
    dataset = LasDataset.open(in_las_dataset)
    dataset.compute(str(calculation_type or "").upper() == "OVERWRITE_EXISTING_STATS")
    dataset.save()
    if _given(out_file):
        separator = "," if str(delimiter or "SPACE").upper() == "COMMA" else " "
        comma = str(decimal_separator or "").upper() == "DECIMAL_COMMA"
        if comma and separator == ",":
            raise ExecuteError("ERROR: a comma cannot be both the delimiter and the decimal separator.")

        def number(v, places=3):
            if v is None:
                return ""
            text = f"{v:.{places}f}" if isinstance(v, float) else str(v)
            return text.replace(".", ",") if comma else text
        lines = []
        if str(summary_level or "DATASET").upper() == "LAS_FILES":
            lines.append(separator.join(("File_Name", "LAS_Version", "Point_Count", "Point_Spacing", "Z_Min", "Z_Max")))
            for path, s in dataset.stats():
                lines.append(separator.join((os.path.basename(path), s["version"], str(s["pointCount"]),
                                             number(s["pointSpacing"]), number(s["zMin"]), number(s["zMax"]))))
        else:
            lines.append(separator.join(("Class_Code", "Point_Count", "Percentage", "Z_Min", "Z_Max", "Intensity_Min",
                                         "Intensity_Max", "Synthetic")))
            merged: dict = {}
            for _, s in dataset.stats():
                for code, c in s["classes"].items():
                    m = merged.setdefault(int(code), {"count": 0, "zMin": math.inf, "zMax": -math.inf,
                                                      "intensityMin": math.inf, "intensityMax": -math.inf, "synthetic": 0})
                    m["count"] += c["count"]
                    m["synthetic"] += c["synthetic"]
                    for k, f in (("zMin", min), ("zMax", max), ("intensityMin", min), ("intensityMax", max)):
                        m[k] = f(m[k], c[k])
            total = sum(m["count"] for m in merged.values()) or 1
            for code in sorted(merged):
                m = merged[code]
                lines.append(separator.join((str(code), str(m["count"]), number(100.0 * m["count"] / total, 2),
                                             number(float(m["zMin"])), number(float(m["zMax"])),
                                             str(int(m["intensityMin"])), str(int(m["intensityMax"])),
                                             str(m["synthetic"]))))
        with open(_absolute(out_file), "w", encoding="utf-8", newline="\n") as written:
            written.write("\n".join(lines) + "\n")
    return Result(dataset.path)


def MakeLasDatasetLayer(in_las_dataset, out_layer, class_code=None, return_values=None, no_flag="INCLUDE_UNFLAGGED",
                        synthetic="INCLUDE_SYNTHETIC", keypoint="INCLUDE_KEYPOINT", withheld="EXCLUDE_WITHHELD",
                        surface_constraints=None, overlap="INCLUDE_OVERLAP", min_height=None, max_height=None):
    """A layer over a LAS dataset whose filters every LAS tool given the layer honours: class codes, returns (LAST,
    FIRST_OF_MANY, LAST_OF_MANY, SINGLE, or a return number), unflagged, synthetic, key-point, withheld and overlap
    points, and which surface constraints take part — with the documented defaults (all but withheld points).

    Refused: ``min_height`` and ``max_height``, heights above a ground surface the page does not define."""
    if _given(min_height) or _given(max_height):
        _refuse("management", "MakeLasDatasetLayer", "min_height/max_height", f"{min_height}/{max_height}",
                "the page does not say what surface the heights are measured from")
    source = _lasd_path(in_las_dataset)
    LasDataset.open(source)
    f = dict(filters_of(in_las_dataset))

    def include(value, default):
        return str(value or default).upper().startswith("INCLUDE")
    if _given(class_code):
        f["class_codes"] = [int(c) for c in _list(class_code)]
    if _given(return_values):
        f["returns"] = [str(r).upper() if not str(r).isdigit() else int(r) for r in _list(return_values)]
    f["unflagged"] = include(no_flag, "INCLUDE")
    f["synthetic"] = include(synthetic, "INCLUDE")
    f["key_point"] = include(keypoint, "INCLUDE")
    f["withheld"] = include(withheld, "EXCLUDE")
    f["overlap"] = include(overlap, "INCLUDE")
    if _given(surface_constraints):
        f["constraints"] = [str(c) for c in _list(surface_constraints)]
    LAYERS[str(out_layer)] = {"source": source, "filters": f}
    return Result(str(out_layer))


# ---- rasters


def _grid(found, dataset, sampling_type, sampling_value):
    from ._tintools import _grid_for
    box = _extent(dataset, found)
    kind = str(sampling_type or "CELLSIZE").upper()
    value = float(sampling_value) if _given(sampling_value) else (10.0 if kind == "CELLSIZE" else 250.0)
    return _grid_for(box, kind, value, dataset.srid, centred=False)


def _cells(found, grid):
    """The row and column of each point; a point on the grid's right or top edge falls in the last cell (INFERRED)."""
    columns = np.clip(np.floor((found["x"] - grid.left) / grid.width).astype(np.int64), 0, grid.columns - 1)
    rows = np.clip(np.floor((grid.top - found["y"]) / grid.height).astype(np.int64), 0, grid.rows - 1)
    return rows, columns


def _mode(values, cell, size):
    """The most frequent value per cell, the smallest of a tie (INFERRED)."""
    out = np.zeros(size, np.int64)
    present = np.zeros(size, bool)
    if not len(values):
        return out, present
    order = np.lexsort((values, cell))
    c, v = cell[order], values[order]
    pairs, counts = np.unique(np.column_stack([c, v]), axis=0, return_counts=True)
    best = {}
    for (k, value), n in zip(pairs, counts):
        if k not in best or n > best[k][0]:
            best[k] = (n, value)
    for k, (_, value) in best.items():
        out[k], present[k] = value, True
    return out, present


def LasPointStatsAsRaster(in_las_dataset, out_raster, method="POINT_COUNT", sampling_type="CELLSIZE", sampling_value=None):
    """A raster of per-cell point statistics, as documented: POINT_COUNT, PULSE_COUNT (the number of last-return
    points, which ignores the layer's filters, as documented), PREDOMINANT_LAST_RETURN, PREDOMINANT_CLASS,
    INTENSITY_RANGE or Z_RANGE.

    INFERRED, the page leaving them open: POINT_COUNT is the default method and 10 the default cell size; the grid starts
    at the dataset's lower-left point and holds whole cells; a cell without points is NoData; a tie takes the smallest
    value; PREDOMINANT_LAST_RETURN is the most frequent return number among the cell's last returns."""
    from ._raster import Raster
    kind = str(method or "POINT_COUNT").upper()
    if kind not in ("POINT_COUNT", "PULSE_COUNT", "PREDOMINANT_LAST_RETURN", "PREDOMINANT_CLASS", "INTENSITY_RANGE",
                    "Z_RANGE"):
        _refuse("management", "LasPointStatsAsRaster", "method", method)
    found, dataset = points(in_las_dataset, "LasPointStatsAsRaster", filtered=kind != "PULSE_COUNT")
    grid = _grid(found, dataset, sampling_type, sampling_value)
    last = found["return_number"] == found["number_of_returns"]
    if kind in ("PULSE_COUNT", "PREDOMINANT_LAST_RETURN"):
        found = {k: v[last] for k, v in found.items()}
    rows, columns = _cells(found, grid)
    cell = rows * grid.columns + columns
    size = grid.rows * grid.columns
    counts = np.bincount(cell, minlength=size)
    present = counts > 0
    if kind in ("POINT_COUNT", "PULSE_COUNT"):
        array = counts.astype(np.int32)
    elif kind == "PREDOMINANT_CLASS":
        array, present = _mode(found["classification"], cell, size)
    elif kind == "PREDOMINANT_LAST_RETURN":
        array, present = _mode(found["return_number"], cell, size)
    else:
        values = found["z"] if kind == "Z_RANGE" else found["intensity"].astype(float)
        high = np.full(size, -np.inf)
        low = np.full(size, np.inf)
        np.maximum.at(high, cell, values)
        np.minimum.at(low, cell, values)
        array = np.where(present, high - low, 0)
        array = array.astype(np.float32) if kind == "Z_RANGE" else array.astype(np.int32)
    array = np.asarray(array).reshape(grid.rows, grid.columns)
    if array.dtype == np.int64:
        array = array.astype(np.int32)
    return Result(Raster._of(array, ~present.reshape(grid.rows, grid.columns), grid).save(out_raster))


def _interpolation(text):
    parts = [p.upper() for p in str(text or "BINNING AVERAGE LINEAR").replace(",", " ").split()]
    return parts[0], parts[1:]


def LasDatasetToRaster(in_las_dataset, out_raster, value_field="ELEVATION", interpolation_type="BINNING AVERAGE LINEAR",
                       data_type="FLOAT", sampling_type="CELLSIZE", sampling_value=10, z_factor=1):
    """A raster of a LAS dataset's elevation or intensity, as documented: ``BINNING {AVERAGE|MINIMUM|MAXIMUM|NEAREST}
    {NONE|SIMPLE|LINEAR}`` assigns each cell from the points in it and fills the empty cells — SIMPLE from the cells
    around them, LINEAR across a triangulation of the filled cells — honouring only clip, erase and replace
    constraints, as documented; ``TRIANGULATION LINEAR {NO_THINNING|WINDOW_SIZE} {MAXIMUM|MINIMUM|CLOSEST_TO_MEAN}
    {size}`` interpolates the triangle planes of a TIN of the points and constraints at each cell centre.

    Refused: RGB (a three-band raster); IDW binning, whose power and neighbourhood the page does not give; NATURAL_NEIGHBOR
    fill and interpolation (ADR-229 §5.4).

    INFERRED: the grid starts at the dataset's lower-left point and holds whole cells; NEAREST takes the point closest to
    the cell centre; SIMPLE fills, in one pass, an empty cell with a filled neighbour among its eight with their mean;
    a cell is inside a constraint polygon when its centre is; INT rounds."""
    from ._raster import Raster
    field = str(value_field or "ELEVATION").upper()
    if field not in ("ELEVATION", "INTENSITY"):
        _refuse("conversion", "LasDatasetToRaster", "value_field", value_field,
                "an RGB raster's three bands are not written by this tool in grapy")
    kind = str(data_type or "FLOAT").upper()
    zf = float(z_factor if _given(z_factor) else 1)
    method, options = _interpolation(interpolation_type)
    found, dataset = points(in_las_dataset, "LasDatasetToRaster")
    values = found["z"] * zf if field == "ELEVATION" else found["intensity"].astype(float)
    grid = _grid(found, dataset, sampling_type, sampling_value)
    xs, ys = grid.centres()
    gx, gy = np.meshgrid(xs, ys)
    if method == "BINNING":
        assign = options[0] if options else "AVERAGE"
        fill = options[1] if len(options) > 1 else "LINEAR"
        if assign not in ("AVERAGE", "MINIMUM", "MAXIMUM", "NEAREST"):
            _refuse("conversion", "LasDatasetToRaster", "cell assignment", assign,
                    "IDW's power and neighbourhood are not given on the page" if assign == "IDW" else "")
        if fill not in ("NONE", "SIMPLE", "LINEAR"):
            _refuse("conversion", "LasDatasetToRaster", "void fill", fill, "natural neighbour is not built (ADR-229 §5.4)")
        rows, columns = _cells(found, grid)
        cell = rows * grid.columns + columns
        size = grid.rows * grid.columns
        present = np.bincount(cell, minlength=size) > 0
        out = np.zeros(size)
        if assign == "AVERAGE":
            out = np.bincount(cell, weights=values, minlength=size) / np.maximum(np.bincount(cell, minlength=size), 1)
        elif assign == "MINIMUM":
            out = np.full(size, np.inf)
            np.minimum.at(out, cell, values)
        elif assign == "MAXIMUM":
            out = np.full(size, -np.inf)
            np.maximum.at(out, cell, values)
        else:
            distance = np.hypot(found["x"] - gx.ravel()[cell], found["y"] - gy.ravel()[cell])
            order = np.lexsort((distance, cell))
            first = np.unique(cell[order], return_index=True)[1]
            out[cell[order][first]] = values[order][first]
        out = np.where(present, out, np.nan).reshape(grid.rows, grid.columns)
        out = _apply_constraints(out, gx, gy, in_las_dataset, dataset)
        if fill == "SIMPLE":
            out = _fill_simple(out)
        elif fill == "LINEAR":
            out = _fill_linear(out, gx, gy, grid.srid)
    elif method == "TRIANGULATION":
        how = options[0] if options else "LINEAR"
        if how != "LINEAR":
            _refuse("conversion", "LasDatasetToRaster", "interpolation", how, "natural neighbour is not built (ADR-229 §5.4)")
        thinning = None
        if len(options) > 1 and options[1] == "WINDOW_SIZE":
            pick = options[2] if len(options) > 2 else "MAXIMUM"
            pick = {"MAXIMUM": "MAX", "MINIMUM": "MIN"}.get(pick, pick)
            if pick not in ("MAX", "MIN", "CLOSEST_TO_MEAN"):
                _refuse("conversion", "LasDatasetToRaster", "point selection", pick)
            thinning = (float(options[3]) if len(options) > 3 else grid.width, pick)
        elif len(options) > 1 and options[1] not in ("NO_THINNING",):
            _refuse("conversion", "LasDatasetToRaster", "point thinning", options[1])
        if field == "INTENSITY":
            _refuse("conversion", "LasDatasetToRaster", "value_field", value_field,
                    "with TRIANGULATION only ELEVATION is built")
        tin = surface(in_las_dataset, "LasDatasetToRaster", zf, thinning)
        out = tin.z_at(gx.ravel(), gy.ravel()).reshape(gx.shape)
    else:
        _refuse("conversion", "LasDatasetToRaster", "interpolation_type", interpolation_type)
    mask = np.isnan(out)
    array = (np.where(mask, 0, np.rint(out)).astype(np.int32) if kind == "INT"
             else np.where(mask, 0, out).astype(np.float32))
    return Result(Raster._of(array, mask, grid).save(out_raster))


def _apply_constraints(out, gx, gy, value, dataset):
    from . import _tin
    centres = shapely.points(gx.ravel(), gy.ravel())
    flat = out.ravel().copy()
    clips = []
    for c in _active_constraints(value, dataset):
        role = c["type"].replace("hard", "").replace("soft", "")
        if role not in ("clip", "erase", "replace"):
            continue
        features, _, _ = _tin.read_features(c["path"], c["heightField"], c["type"], None, "LasDatasetToRaster")
        for shape, _, height, _ in features:
            area = shapely.force_2d(shape)
            if role == "clip":
                clips.append(area)
            elif role == "erase":
                flat[shapely.within(centres, area)] = np.nan
            else:
                z = height if height is not None else float(shapely.get_coordinates(shape, include_z=True)[0, 2])
                flat[shapely.within(centres, area)] = z
    if clips:
        flat[~shapely.within(centres, shapely.union_all(clips))] = np.nan
    return flat.reshape(out.shape)


def _fill_simple(out):
    padded = np.pad(out, 1, constant_values=np.nan)
    stack = np.stack([padded[1 + dy:1 + dy + out.shape[0], 1 + dx:1 + dx + out.shape[1]]
                      for dy in (-1, 0, 1) for dx in (-1, 0, 1) if dx or dy])
    with np.errstate(invalid="ignore"):
        counts = np.sum(~np.isnan(stack), axis=0)
        means = np.nansum(stack, axis=0) / np.maximum(counts, 1)
    return np.where(np.isnan(out) & (counts > 0), means, out)


def _fill_linear(out, gx, gy, srid):
    from . import _tin
    known = ~np.isnan(out)
    if known.sum() < 3 or known.all():
        return out
    tin = _tin.triangulate(list(zip(gx[known], gy[known], out[known])), [], srid=srid)
    filled = tin.z_at(gx.ravel(), gy.ravel()).reshape(out.shape)
    return np.where(known, out, filled)


# ---- 3D Analyst: points to features


def LASToMultipoint(input, out_feature_class, average_point_spacing, class_code=None, _return="ANY_RETURNS",
                    attribute=None, input_coordinate_system=None, file_suffix="las", z_factor=1,
                    folder_recursion="NO_RECURSION", **keywords):
    """3D multipoints of LAS files' points, filtered by class code and return (ANY_RETURNS, a return number or
    LAST_RETURNS), z multiplied by ``z_factor``. The keyword ``return`` is a Python reserved word; it is accepted as
    ``**{"return": …}`` or by position.

    Refused: ``attribute`` — the page stores them in BLOB fields without saying how the values are encoded.

    INFERRED: one multipoint row per LAS file — the page does not say how points are grouped, and says that with folder
    recursion the output has "a row for each file encountered"; ``average_point_spacing`` is not used, its role in the
    grouping not being given; the reference is the given one, else the first file's."""
    from ._reference import reference_of
    returns = keywords.pop("return", _return)
    if keywords:
        raise TypeError(f"LASToMultipoint() got unexpected keyword arguments {sorted(keywords)}")
    if _given(attribute):
        _refuse("ddd", "LASToMultipoint", "attribute", attribute, "the page does not say how a BLOB field encodes the "
                "values")
    codes = [int(c) for c in _list(class_code)] if _given(class_code) else None
    wanted = [str(r).upper() for r in _list(returns)] if _given(returns) else ["ANY_RETURNS"]
    suffix = "." + str(file_suffix or "las").lstrip(".").lower()
    files = _files_in(input, str(folder_recursion or "").upper() == "RECURSION", (suffix,))
    zf = float(z_factor if _given(z_factor) else 1)
    srid = 0
    if _given(input_coordinate_system):
        reference = reference_of(input_coordinate_system)
        srid = reference.factoryCode if reference is not None else 0
    rows = []
    for path in files:
        las = read_file(path)
        srid = srid or srid_of(las)
        a = arrays(las)
        keep = np.ones(len(a["x"]), bool)
        if codes is not None:
            keep &= np.isin(a["classification"], codes)
        if "ANY_RETURNS" not in wanted:
            chosen = np.zeros(len(keep), bool)
            for r in wanted:
                chosen |= (a["return_number"] == a["number_of_returns"]) if r == "LAST_RETURNS" else \
                    (a["return_number"] == int(r))
            keep &= chosen
        if keep.any():
            rows.append((shapely.MultiPoint(np.column_stack([a["x"][keep], a["y"][keep], a["z"][keep] * zf])),))
    from ._tintools import _write
    return Result(_write(out_feature_class, "Multipoint", srid, [], True, rows))


def PointFileInformation(input, out_feature_class, in_file_type="LAS", file_suffix="las", input_coordinate_system=None,
                         folder_recursion="NO_RECURSION", extrude_geometry="NO_EXTRUSION",
                         decimal_separator="DECIMAL_POINT", summarize_by_class_code="NO_SUMMARIZE",
                         improve_las_point_spacing="LAS_SPACING"):
    """A polygon of each LAS file's x,y extent with ``FileName``, ``Pt_Count``, ``Pt_Spacing`` (assuming the points
    "are evenly spaced over the x,y extent"), ``Z_Min`` and ``Z_Max``, and with SUMMARIZE one row per class code with
    ``Class``, as documented.

    EXTRUSION writes multipatch features (ADR-238) "depicting the z-range of each input file", as documented: a box
    over the file's x,y extent from its Z_Min to its Z_Max (a class's, with SUMMARIZE).

    Refused: XYZ, XYZI and GENERATE files; NO_LAS_SPACING — the page's two descriptions of this parameter contradict
    each other, and grapy builds the even-spacing estimate the LAS_SPACING text describes.

    INFERRED: Pt_Spacing is √(extent area ÷ count), from the file's header extent; field types are text, integers and
    doubles; an extruded file whose points are all at one height is its extent's rectangle at that height (Q-209 (j))."""
    from ._reference import reference_of
    from ._tintools import _write
    if str(in_file_type or "LAS").upper() != "LAS":
        _refuse("ddd", "PointFileInformation", "in_file_type", in_file_type, "only LAS files are read")
    extrude = str(extrude_geometry or "NO_EXTRUSION").upper()
    if extrude not in ("EXTRUSION", "NO_EXTRUSION"):
        raise ExecuteError(f"ERROR 000800: extrude_geometry is EXTRUSION or NO_EXTRUSION, not '{extrude_geometry}'.")
    extrude = extrude == "EXTRUSION"
    if str(improve_las_point_spacing or "LAS_SPACING").upper() != "LAS_SPACING":
        _refuse("ddd", "PointFileInformation", "improve_las_point_spacing", improve_las_point_spacing,
                "the page's descriptions of LAS_SPACING and NO_LAS_SPACING contradict each other")
    by_class = str(summarize_by_class_code or "").upper() == "SUMMARIZE"
    suffix = "." + str(file_suffix or "las").lstrip(".").lower()
    files = _files_in(input, str(folder_recursion or "").upper() == "RECURSION", (suffix,))
    srid = 0
    if _given(input_coordinate_system):
        reference = reference_of(input_coordinate_system)
        srid = reference.factoryCode if reference is not None else 0
    columns = [_gpkg.Column("FileName", "String"), _gpkg.Column("Pt_Spacing", "Double"),
               _gpkg.Column("Z_Min", "Double"), _gpkg.Column("Z_Max", "Double"), _gpkg.Column("Pt_Count", "Integer")]
    if by_class:
        columns.append(_gpkg.Column("Class", "Integer"))
    rows = []

    def shape(extent, low, high):
        if not extrude:
            return shapely.MultiPolygon([shapely.box(*extent)])
        from . import _multipatch as M
        x0, y0, x1, y1 = extent
        if high > low:
            return M.from_triangles(M.box((x0, y0, low), (x1, y1, high)))
        return shapely.MultiPolygon([shapely.Polygon([(x0, y0, low), (x1, y0, low), (x1, y1, low), (x0, y1, low)])])

    for path in files:
        s = statistics(path)
        srid = srid or s["srid"]
        area = (s["extent"][2] - s["extent"][0]) * (s["extent"][3] - s["extent"][1])
        if by_class:
            for code, c in sorted(s["classes"].items(), key=lambda kv: int(kv[0])):
                spacing = math.sqrt(area / c["count"]) if c["count"] and area > 0 else None
                rows.append((os.path.basename(path), spacing, c["zMin"], c["zMax"], c["count"], int(code),
                             shape(s["extent"], c["zMin"], c["zMax"])))
        else:
            rows.append((os.path.basename(path), s["pointSpacing"], s["zMin"], s["zMax"], s["pointCount"],
                         shape(s["extent"], s["zMin"], s["zMax"])))
    if extrude:
        from . import _multipatch as M
        return Result(M.write(out_feature_class, srid, columns, [(list(r[:-1]), r[-1]) for r in rows]))
    return Result(_write(out_feature_class, "Polygon", srid, columns, False, rows))


def LasPointStatsByArea(in_las_dataset, in_features, out_property):
    """Fields of the points' statistics added to each polygon: ``Z_MIN``, ``Z_MAX``, ``Z_MEAN``, ``POINT_COUNT`` and
    ``STD_DEV`` (asked for as STANDARD_DEVIATION), overwritten when present, from the LAS points the dataset or layer
    shows, as documented — constraints' heights ignored.

    INFERRED: a point on a polygon's boundary counts; the standard deviation is the population's; a polygon with no point
    gets a count of 0 and nulls."""
    from . import da
    from ._tintools import _ensure_fields
    names = {"Z_MIN": "Z_MIN", "Z_MAX": "Z_MAX", "Z_MEAN": "Z_MEAN", "POINT_COUNT": "POINT_COUNT",
             "STANDARD_DEVIATION": "STD_DEV"}
    wanted = []
    for p in _list(out_property):
        if str(p).upper() not in names:
            _refuse("ddd", "LasPointStatsByArea", "out_property", p)
        wanted.append(names[str(p).upper()])
    found, _ = points(in_las_dataset, "LasPointStatsByArea")
    _ensure_fields(in_features, [w for w in wanted if w != "POINT_COUNT"])
    if "POINT_COUNT" in wanted:
        _ensure_fields(in_features, ["POINT_COUNT"], "Integer")
    tree_points = shapely.points(found["x"], found["y"])
    with da.UpdateCursor(in_features, [*wanted, "SHAPE@"]) as rows:
        for row in rows:
            if row[-1] is None:
                continue
            area = shapely.force_2d(row[-1]._shape)
            inside = shapely.covers(area, tree_points)
            z = found["z"][inside]
            stat = {"Z_MIN": float(z.min()) if len(z) else None, "Z_MAX": float(z.max()) if len(z) else None,
                    "Z_MEAN": float(z.mean()) if len(z) else None, "POINT_COUNT": int(len(z)),
                    "STD_DEV": float(z.std()) if len(z) else None}
            rows.updateRow([*(stat[w] for w in wanted), row[-1]])
    return Result(str(in_features))


# ---- 3D Analyst: editing LAS files


def _write_las(path, las):
    las.write(path)


def _class_range(las, code, tool):
    limit = 255 if las.header.point_format.id >= 6 or (las.header.version.major, las.header.version.minor) == (1, 0) \
        else 31
    if not 0 <= code <= limit:
        raise ExecuteError(f"ERROR: class code {code} is out of range 0–{limit} for LAS "
                           f"{las.header.version.major}.{las.header.version.minor}, as {tool}'s page documents.")


def _set_flag(las, name, action, where):
    action = str(action or "NO_CHANGE").upper()
    if action == "NO_CHANGE" or not where.any():
        return
    if name not in las.point_format.dimension_names:
        raise ExecuteError(f"ERROR: this LAS point format has no {name} flag.")
    values = np.asarray(las[name]).astype(np.uint8)
    values[where] = 1 if action == "SET" else 0
    las[name] = values


def _update_stats(value, files_changed, compute):
    if not compute:
        return
    dataset = LasDataset.open(value)
    for entry, full in zip(dataset.files, dataset.file_paths()):
        if full in files_changed:
            entry["stats"] = statistics(full)
    dataset.save()


def _region(extent, boundary, process_entire_files):
    if str(process_entire_files or "PROCESS_EXTENT").upper() == "PROCESS_ENTIRE_FILES":
        return None
    return _area_of(extent, boundary) if (_given(extent) or _given(boundary)) else None


def ChangeLasClassCodes(in_las_dataset, class_codes, compute_stats="COMPUTE_STATS", extent=None, boundary=None,
                        process_entire_files="PROCESS_EXTENT", update_pyramid="UPDATE_PYRAMID"):
    """Class codes and flags of a LAS dataset's points changed in their files: each row ``current new synthetic
    model_key withheld overlap``, the flags NO_CHANGE, SET or CLEAR, −1 as the current class for every class, as
    documented. Layer filters are not honoured, as documented. An extent or boundary limits the points changed, unless
    PROCESS_ENTIRE_FILES. ``update_pyramid`` has no pyramid to update in grapy."""
    from ._tin import rows_of
    rules = []
    for row in rows_of(class_codes, 6):
        current, new = int(row[0]), int(row[1]) if row[1] not in ("", None) else None
        rules.append((current, new, row[2:6]))
    dataset = LasDataset.open(in_las_dataset)
    area = _region(extent, boundary, process_entire_files)
    changed = []
    for path in dataset.file_paths():
        las = read_file(path)
        a = arrays(las)
        within = shapely.covers(area, shapely.points(a["x"], a["y"])) if area is not None else np.ones(len(a["x"]), bool)
        classes = a["classification"].copy()
        touched = False
        for current, new, flags in rules:
            where = within & ((a["classification"] == current) if current != -1 else True)
            if not np.any(where):
                continue
            touched = True
            if new is not None:
                _class_range(las, new, "ChangeLasClassCodes")
                classes[where] = new
            for name, action in zip(("synthetic", "key_point", "withheld", "overlap"), flags):
                _set_flag(las, name, action, where)
        if touched:
            las.classification = classes.astype(np.uint8)
            _write_las(path, las)
            changed.append(path)
    _update_stats(in_las_dataset, changed, str(compute_stats or "").upper() == "COMPUTE_STATS")
    return Result(_lasd_path(in_las_dataset))


def SetLasClassCodesUsingFeatures(in_las_dataset, feature_class, compute_stats="COMPUTE_STATS",
                                  update_pyramid="UPDATE_PYRAMID"):
    """The points that fall on the 2D position of each feature class's features, buffered by its distance, given its
    class code and flags (NO_CHANGE, SET, CLEAR), as documented: each row ``features buffer_distance new_class synthetic
    key_point withheld overlap``. The layer's filters are honoured. INFERRED: rows apply in order, later ones winning;
    a point on a buffer's edge is inside."""
    from . import da
    from ._tin import rows_of
    rules = []
    for row in rows_of(feature_class, 7):
        path, distance = row[0], row[1]
        with da.SearchCursor(path, ["SHAPE@"]) as rows:
            shapes = [shapely.force_2d(r[0]._shape) for r in rows if r[0] is not None]
        area = shapely.union_all(shapes) if shapes else shapely.Polygon()
        d = float(str(distance).split()[0]) if _given(distance) else 0.0
        if d > 0:
            area = shapely.buffer(area, d)
        new = None if str(row[2] or "NO_CHANGE").upper() == "NO_CHANGE" else int(row[2])
        rules.append((area, d, new, row[3:7]))
    found, dataset = points(in_las_dataset, "SetLasClassCodesUsingFeatures")
    changed = []
    for number, path in enumerate(dataset.file_paths()):
        mine = found["file"] == number
        if not mine.any():
            continue
        las = read_file(path)
        classes = np.asarray(las.classification, np.int64).copy()
        xy = shapely.points(found["x"][mine], found["y"][mine])
        index = found["index"][mine]
        touched = False
        for area, d, new, flags in rules:
            hit = shapely.covers(area, xy) if area.geom_type.endswith("Polygon") else shapely.dwithin(area, xy, d)
            if not hit.any():
                continue
            touched = True
            where = np.zeros(len(classes), bool)
            where[index[hit]] = True
            if new is not None:
                _class_range(las, new, "SetLasClassCodesUsingFeatures")
                classes[where] = new
            for name, action in zip(("synthetic", "key_point", "withheld", "overlap"), flags):
                _set_flag(las, name, action, where)
        if touched:
            las.classification = classes.astype(np.uint8)
            _write_las(path, las)
            changed.append(path)
    _update_stats(in_las_dataset, changed, str(compute_stats or "").upper() == "COMPUTE_STATS")
    return Result(_lasd_path(in_las_dataset))


def ClassifyLasByHeight(in_las_dataset, ground_source="GROUND", height_classification=None, noise="NONE",
                        compute_stats="COMPUTE_STATS", extent=None, process_entire_files="PROCESS_EXTENT", boundary=None,
                        update_pyramid="UPDATE_PYRAMID", in_surface=None):
    """Points of class 0 and 1 classed by their height above the ground, as documented: each row ``class_code
    height_from_ground``, the first class from the ground to its height and each next from the one before's height to
    its own. The ground is a TIN of the class 2 and 8 points (GROUND), of the class 8 points (MODEL_KEY), or a raster
    (SURFACE). Withheld and overlap points are left. ``noise`` classes points below the ground as 7 (LOW_NOISE), above
    the last height as 18 (HIGH_NOISE), or both (ALL_NOISE).

    Refused: a ground height the page gives no default for — ``height_classification`` must be given.

    INFERRED: the ground TIN is linear on its triangles' planes; a range includes its upper height; NONE is the default
    noise, the page giving none; a point off the ground surface is left."""
    from ._tin import rows_of, triangulate
    from . import sa
    from ._raster import as_raster
    rules = sorted(((float(r[1]), int(r[0])) for r in rows_of(height_classification, 2) if r[0] != ""))
    if not rules:
        raise ExecuteError("ERROR 000735: height_classification needs at least one 'class_code height' row; the page "
                           "gives no default heights.")
    source = str(ground_source or "GROUND").upper()
    kind = str(noise or "NONE").upper()
    if kind not in ("NONE", "ALL_NOISE", "HIGH_NOISE", "LOW_NOISE"):
        _refuse("ddd", "ClassifyLasByHeight", "noise", noise)
    found, dataset = points(in_las_dataset, "ClassifyLasByHeight", filtered=False)
    if source in ("GROUND", "MODEL_KEY"):
        codes = (2, 8) if source == "GROUND" else (8,)
        g = np.isin(found["classification"], codes) & ~found.get("withheld", np.zeros(len(found["x"]), bool))
        ground = triangulate(list(zip(found["x"][g], found["y"][g], found["z"][g])), [], srid=dataset.srid)

        def height_of(xs, ys):
            return ground.z_at(xs, ys)
    elif source == "SURFACE":
        raster = as_raster(in_surface, "ClassifyLasByHeight")

        def height_of(xs, ys):
            return np.array([np.nan if (v := sa._sample(raster, x, y, True)) is None else v for x, y in zip(xs, ys)])
    else:
        _refuse("ddd", "ClassifyLasByHeight", "ground_source", ground_source)
    area = _region(extent, boundary, process_entire_files)
    changed = []
    for number, path in enumerate(dataset.file_paths()):
        las = read_file(path)
        a = arrays(las)
        evaluated = np.isin(a["classification"], (0, 1)) & ~a["withheld"] & ~a["overlap"]
        if area is not None:
            evaluated &= shapely.covers(area, shapely.points(a["x"], a["y"]))
        if not evaluated.any():
            continue
        heights = np.full(len(a["x"]), np.nan)
        heights[evaluated] = a["z"][evaluated] - height_of(a["x"][evaluated], a["y"][evaluated])
        classes = a["classification"].copy()
        previous = 0.0
        for top, code in rules:
            _class_range(las, code, "ClassifyLasByHeight")
            classes[evaluated & (heights >= previous) & (heights <= top)] = code
            previous = top
        if kind in ("ALL_NOISE", "LOW_NOISE"):
            classes[evaluated & (heights < 0)] = 7
        if kind in ("ALL_NOISE", "HIGH_NOISE"):
            classes[evaluated & (heights > rules[-1][0])] = 18
        if (classes != a["classification"]).any():
            las.classification = classes.astype(np.uint8)
            _write_las(path, las)
            changed.append(path)
    _update_stats(in_las_dataset, changed, str(compute_stats or "").upper() == "COMPUTE_STATS")
    return Result(_lasd_path(in_las_dataset))


def ExtractLas(in_las_dataset, target_folder, extent=None, boundary=None, process_entire_files="PROCESS_EXTENT",
               name_suffix=None, remove_vlr="MAINTAIN_VLR", rearrange_points="REARRANGE_POINTS",
               compute_stats="COMPUTE_STATS", out_las_dataset=None, compression="SAME_AS_INPUT"):
    """For each LAS file of the dataset, a LAS file in ``target_folder`` holding the points the dataset or layer shows
    within the extent and boundary (their intersection when both are given), keeping the source's version and point
    format, as documented; with ``out_las_dataset`` a LAS dataset of them. REMOVE_VLR keeps only the coordinate system's
    records.

    Refused: ZLAS compression (Esri's format is not published).

    INFERRED: REARRANGE_POINTS keeps the points in their order — the page does not say what order it rearranges them
    into; a file with no point left is not written; an output is named after its source plus the suffix."""
    import laspy
    if str(compression or "SAME_AS_INPUT").upper() not in ("SAME_AS_INPUT", "NO_COMPRESSION"):
        _refuse("ddd", "ExtractLas", "compression", compression, "zLAS is Esri's format, which is not published")
    folder = _absolute(target_folder)
    os.makedirs(folder, exist_ok=True)
    whole = str(process_entire_files or "PROCESS_EXTENT").upper() == "PROCESS_ENTIRE_FILES"
    area = _area_of(extent, boundary) if (_given(extent) or _given(boundary)) else None
    found, dataset = points(in_las_dataset, "ExtractLas")
    written = []
    for number, path in enumerate(dataset.file_paths()):
        mine = found["file"] == number
        index = found["index"][mine]
        if area is not None:
            xy = shapely.points(found["x"][mine], found["y"][mine])
            if whole:
                if not shapely.intersects(area, shapely.multipoints(np.column_stack([found["x"][mine],
                                                                                     found["y"][mine]]))):
                    continue
            else:
                index = index[shapely.covers(area, xy)]
        if not len(index):
            continue
        las = read_file(path)
        out = laspy.LasData(las.header)
        out.points = las.points[np.sort(index)]
        if str(remove_vlr or "").upper() == "REMOVE_VLR":
            keep = [v for v in las.header.vlrs if getattr(v, "user_id", "") == "LASF_Projection"]
            out.header.vlrs = laspy.VLRList(keep)
        stem, extension = os.path.splitext(os.path.basename(path))
        target = os.path.join(folder, stem + (str(name_suffix) if _given(name_suffix) else "") + ".las")
        out.write(target)
        written.append(target)
    if _given(out_las_dataset):
        CreateLasDataset(written, out_las_dataset, compute_stats="COMPUTE_STATS"
                         if str(compute_stats or "").upper() == "COMPUTE_STATS" else "NO_COMPUTE_STATS",
                         spatial_reference=dataset.srid or None)
    return Result(folder)


# ---- LAS dataset to TIN


def LasDatasetToTin(in_las_dataset, out_tin, thinning_type="NONE", thinning_method=None, thinning_value=None,
                    max_nodes=5000000, z_factor=1, clip_to_extent="CLIP"):
    """A TIN of a LAS dataset's points (its layer's filters honoured) and surface constraints, as documented; with
    WINDOW_SIZE thinning one point per square window of ``thinning_value``, the lowest (MIN), highest (MAX) or closest
    to the window's mean (CLOSEST_TO_MEAN). CLIP keeps the data area within the dataset's extent.

    Refused: RANDOM thinning — the page does not define how points are drawn.

    INFERRED: the windows start at the dataset's lower-left point; more nodes than ``max_nodes`` is an error; the TIN
    stores the points as one mass-point input; CLIP masks the triangles whose centroid is outside the extent without
    enforcing its edges, which would add its corners as nodes of no height."""
    from . import _tin
    kind = str(thinning_type or "NONE").upper()
    thinning = None
    if kind == "WINDOW_SIZE":
        method = str(thinning_method or "MIN").upper()
        if method not in ("MIN", "MAX", "CLOSEST_TO_MEAN"):
            _refuse("ddd", "LasDatasetToTin", "thinning_method", thinning_method)
        if not _given(thinning_value):
            raise ExecuteError("ERROR 000735: WINDOW_SIZE thinning needs thinning_value.")
        thinning = (float(thinning_value), method)
    elif kind == "RANDOM":
        _refuse("ddd", "LasDatasetToTin", "thinning_type", thinning_type, "the page does not define how random points "
                "are drawn")
    elif kind != "NONE":
        _refuse("ddd", "LasDatasetToTin", "thinning_type", thinning_type)
    zf = float(z_factor if _given(z_factor) else 1)
    found, dataset = points(in_las_dataset, "LasDatasetToTin")
    xyz = np.column_stack([found["x"], found["y"], found["z"] * zf])
    if thinning is not None:
        xyz = _thinned(xyz, *thinning, origin=_extent(dataset, found))
    if _given(max_nodes) and len(xyz) > int(max_nodes):
        raise ExecuteError(f"ERROR: the TIN would hold {len(xyz)} nodes, more than max_nodes ({int(max_nodes)}).")
    inputs = [("masspoints", shapely.MultiPoint(xyz), 0, None, False)] if len(xyz) else []
    for c in _active_constraints(in_las_dataset, dataset):
        sf = "masspoints" if c["type"] == "anchorpoints" else c["type"]
        features, _, _ = _tin.read_features(c["path"], c["heightField"], sf, None, "LasDatasetToTin")
        inputs.extend((sf, shape, tag, height, pending) for shape, pending, height, tag in features)
    if str(clip_to_extent or "CLIP").upper() == "CLIP" and len(xyz):
        box = shapely.box(*_extent(dataset, found))
        inputs.append(("extentclip", shapely.Polygon([(x, y, 0.0) for x, y in box.exterior.coords]), 0, None, False))
    _tin.store(out_tin, dataset.srid, inputs)
    return Result(str(out_tin))


# ---- ground classification: an equivalent


def ClassifyLasGround(in_las_dataset, method="STANDARD", reuse_ground="RECLASSIFY_GROUND", dem_resolution=None,
                      compute_stats="COMPUTE_STATS", extent=None, boundary=None, process_entire_files="PROCESS_EXTENT",
                      update_pyramid="UPDATE_PYRAMID", algorithm="LATEST", classify_low_noise="NO_CLASSIFY_LOW_NOISE",
                      minimum_depth_below_ground=None, preserve_low_noise="PRESERVE",
                      classify_high_noise="NO_CLASSIFY_HIGH_NOISE", minimum_height_above_ground=None,
                      preserve_high_noise="PRESERVE", classify_key_points="NO_CLASSIFY_KEY_POINTS",
                      classification_type="Z_TOLERANCE", z_tolerance=None, window_size=None,
                      window_size_method="AVERAGE"):
    """EQUIVALENT: the progressive morphological filter (Zhang et al. 2003, *IEEE Transactions on Geoscience and Remote
    Sensing* 41(4)), not ArcGIS Pro's ground classifier, whose algorithm the page does not describe. As documented, only
    the last returns of class 0, 1 and 2 are evaluated, and ground points become class 2; with RECLASSIFY_GROUND the
    evaluated class 2 points are first made 1.

    The filter: a grid of each cell's lowest last return at ``dem_resolution``, empty cells taking their nearest cell's
    value; grey openings with windows growing 3, 5, 9, 17 … cells until a window spans 20 m (Zhang's exponential
    series), each opening the one before; a point is non-ground once it stands above an opened surface by more than
    that window's threshold dh = s·(w − w_prev)·c + 0.3 m, capped at 3 m. ``method`` sets the slope s: CONSERVATIVE 0.15,
    STANDARD 0.3, AGGRESSIVE 0.6 (INFERRED, the page saying only that conservative is tighter and aggressive suits
    rugged terrain).

    Refused: RECOVER_RIDGES and MODEL_KEY; the FIRST algorithm; noise and key-point classification; WINDOW_SIZE
    classification.

    INFERRED: ``dem_resolution`` defaults to 1.5 × the point spacing (√(area ÷ count)), never under 0.3, the page's
    minimum; non-ground points keep their class."""
    from scipy import ndimage
    kind = str(method or "STANDARD").upper()
    slopes = {"CONSERVATIVE": 0.15, "STANDARD": 0.3, "AGGRESSIVE": 0.6}
    if kind not in slopes:
        _refuse("ddd", "ClassifyLasGround", "method", method, "its method is not described on the page")
    if str(algorithm or "LATEST").upper() != "LATEST":
        _refuse("ddd", "ClassifyLasGround", "algorithm", algorithm, "it selects an earlier Esri algorithm")
    for name, value, default in (("classify_low_noise", classify_low_noise, "NO_CLASSIFY_LOW_NOISE"),
                                 ("classify_high_noise", classify_high_noise, "NO_CLASSIFY_HIGH_NOISE"),
                                 ("classify_key_points", classify_key_points, "NO_CLASSIFY_KEY_POINTS"),
                                 ("classification_type", classification_type, "Z_TOLERANCE")):
        if str(value or default).upper() != default:
            _refuse("ddd", "ClassifyLasGround", name, value)
    reuse = str(reuse_ground or "RECLASSIFY_GROUND").upper() == "REUSE_GROUND"
    found, dataset = points(in_las_dataset, "ClassifyLasGround", filtered=False)
    last = found["return_number"] == found["number_of_returns"]
    evaluated = last & np.isin(found["classification"], (0, 1, 2)) & ~found["withheld"]
    area = _region(extent, boundary, process_entire_files)
    if area is not None:
        evaluated &= shapely.covers(area, shapely.points(found["x"], found["y"]))
    if not evaluated.any():
        return Result(_lasd_path(in_las_dataset))
    x, y, z = found["x"][evaluated], found["y"][evaluated], found["z"][evaluated]
    box = (x.min(), y.min(), x.max(), y.max())
    spacing = math.sqrt(max((box[2] - box[0]) * (box[3] - box[1]), 1e-12) / len(x))
    cell = float(dem_resolution) if _given(dem_resolution) else max(1.5 * spacing, 0.3)
    columns = int((box[2] - box[0]) // cell) + 1
    rows = int((box[3] - box[1]) // cell) + 1
    c = ((x - box[0]) // cell).astype(np.int64)
    r = ((y - box[1]) // cell).astype(np.int64)
    grid = np.full((rows, columns), np.inf)
    np.minimum.at(grid, (r, c), z)
    empty = ~np.isfinite(grid)
    if empty.any():
        _, (ir, ic) = ndimage.distance_transform_edt(empty, return_indices=True)
        grid = grid[ir, ic]
    s, dh0, dhmax = slopes[kind], 0.3, 3.0
    surface = grid.copy()
    ground = np.ones(len(z), bool)
    previous, k = 1, 1
    while True:
        window = 2 ** k + 1                       # 3, 5, 9, 17 … cells
        surface = ndimage.grey_opening(surface, size=(window, window))
        threshold = min(s * (window - previous) * cell + dh0, dhmax)
        ground &= z - surface[r, c] <= threshold
        previous, k = window, k + 1
        if (window - 1) * cell >= 20.0 or window > 2 * max(rows, columns):
            break
    changed = []
    index_all = np.nonzero(evaluated)[0]
    for number, path in enumerate(dataset.file_paths()):
        mine = found["file"][index_all] == number
        if not mine.any():
            continue
        las = read_file(path)
        classes = np.asarray(las.classification, np.int64).copy()
        idx = found["index"][index_all][mine]
        if not reuse:
            classes[idx[classes[idx] == 2]] = 1
        classes[idx[ground[mine]]] = 2
        las.classification = classes.astype(np.uint8)
        _write_las(path, las)
        changed.append(path)
    _update_stats(in_las_dataset, changed, str(compute_stats or "").upper() == "COMPUTE_STATS")
    return Result(_lasd_path(in_las_dataset))


# ---- describe


class Described:
    """What Describe answers for a LAS dataset: dataType ``LasDataset`` and the documented properties — constraintCount,
    fileCount, hasStatistics, needsUpdateStatistics, pointCount, usesRelativePath — with the Dataset group's extent and
    spatial reference. INFERRED: pointCount is 0 until statistics are computed; the extent is the files' headers'."""

    def __init__(self, path, dataset):
        import laspy
        from ._reference import Extent, SpatialReference
        self.catalogPath = str(path)
        self.path = os.path.dirname(dataset.path)
        self.file = os.path.basename(dataset.path)
        self.name = self.baseName = os.path.splitext(self.file)[0]
        self.dataType = self.datasetType = "LasDataset"
        self.constraintCount = len(dataset.constraints)
        self.fileCount = len(dataset.files)
        stats = dataset.stats()
        self.hasStatistics = bool(stats) and all(s for _, s in stats)
        self.needsUpdateStatistics = any(_stale(s, p) for p, s in stats)
        self.pointCount = sum(s["pointCount"] for _, s in stats if s)
        self.usesRelativePath = dataset.relative
        self.spatialReference = SpatialReference(dataset.srid) if dataset.srid > 0 else None
        boxes = []
        for p in dataset.file_paths():
            if os.path.exists(p) and p.lower().endswith(".las"):
                with laspy.open(p) as reader:
                    boxes.append((*reader.header.mins[:2], *reader.header.maxs[:2]))
        self.extent = (Extent(min(b[0] for b in boxes), min(b[1] for b in boxes), max(b[2] for b in boxes),
                              max(b[3] for b in boxes), self.spatialReference) if boxes else None)


def describe(path):
    if not is_las(path):
        return None
    try:
        return Described(path, LasDataset.open(path))
    except ExecuteError:
        return None
