"""ADR-229's second batch of LAS tools: converting, projecting, colouring, thinning and tiling LAS files, classifying
overlap and buildings, locating points and outliers, interpolating a raster from a point cloud and extracting objects.

Each is built to its reference page on doc.esri.com (pro.arcgis.com redirects there), read on 2026-10-30; what a page
leaves open is labelled INFERRED in the tool's docstring and listed in ADR-229 §5.9 (j)–(r), and what grapy cannot do
is refused by name. ClassifyLasBuilding is an *equivalent* (ADR-224 §5.6): its page names plane detection without
describing it. ``arcpy.ddd``, ``arcpy.management`` and ``arcpy.conversion`` answer these under their own names.

Common to the tools that write LAS files, INFERRED where the pages are silent:

- an output file is named after its source (plus the tool's suffix or prefix) with the extension ``.las``;
- REARRANGE_POINTS keeps the points in their order, as ExtractLas does (ADR-229 §5.9 (e)): the pages do not say what
  order "spatial clustering" is;
- REMOVE_VLR keeps only the coordinate system's records;
- a point format must hold every class code and flag of the input, as ConvertLas's page says: a class above 31 or an
  overlap flag cannot go into formats 0–5.
"""
from __future__ import annotations

import math
import os

import numpy as np
import shapely

from . import _gpkg, _las
from ._result import Result
from ._runtime import ExecuteError, NotBuiltError

_given = _las._given
_list = _las._list
_absolute = _las._absolute

# The point record formats each LAS version holds (ASPRS LAS 1.1–1.4).
_FORMATS = {"1.1": (0, 1), "1.2": (0, 1, 2, 3), "1.3": (0, 1, 2, 3, 4, 5), "1.4": tuple(range(11))}
# A format's nearest counterpart across the 1.4 divide (INFERRED: the one with the same optional fields).
_DOWN = {6: 1, 7: 3, 8: 3, 9: 4, 10: 5}
_UP = {0: 6, 1: 6, 2: 7, 3: 7, 4: 9, 5: 10}
_ZLAS = "zLAS is Esri's compressed LAS, whose format is not published (ADR-229 §5.5)"
_FLAG_NAMES = {"MODEL_KEY": "key_point", "KEY_POINT": "key_point", "OVERLAP": "overlap", "SYNTHETIC": "synthetic",
               "WITHHELD": "withheld"}


def _refuse(module, tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.{module}.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _upper(value, default):
    return str(value if _given(value) else default).upper().replace(" ", "_")


def _length(value):
    """A linear unit (``"20 Centimeters"``) in metres, or a number as it is (the data's units)."""
    from .analysis import _distance
    return _distance(value, 0)


def _area(value):
    from . import _units
    if isinstance(value, (int, float)):
        return float(value)
    parts = str(value).split()
    number = float(parts[0])
    if len(parts) == 1 or parts[1].upper() == "UNKNOWN":
        return number
    per = _units.areal(parts[1])
    if per is None:
        raise ValueError(f"'{value}' is not an areal unit: '{parts[1]}' is not a unit ArcGIS names.")
    return number * per


def _files(value):
    """The LAS files an input names: a LAS dataset's or layer's files, or LAS files and folders."""
    if _las.is_las(value):
        return _las.LasDataset.open(value).file_paths()
    return _las._files_in(value, False)


def _target(folder):
    path = _absolute(folder)
    os.makedirs(path, exist_ok=True)
    return path


def _out_name(folder, source, prefix="", suffix=""):
    stem = os.path.splitext(os.path.basename(source))[0]
    return os.path.join(folder, f"{prefix or ''}{stem}{suffix or ''}.las")


def _dataset_of(written, out_las_dataset, compute, srid=0, relative=False):
    if _given(out_las_dataset):
        _las.CreateLasDataset(written, out_las_dataset, compute_stats="COMPUTE_STATS" if compute else "NO_COMPUTE_STATS",
                              spatial_reference=srid or None,
                              relative_paths="RELATIVE_PATHS" if relative else "ABSOLUTE_PATHS")


def _version(value, default, tool, module="ddd"):
    text = str(value if _given(value) else default).strip()
    if text in ("1", "1.0"):
        _refuse(module, tool, "las_version", value, "laspy, which grapy writes LAS with, writes versions 1.1 to 1.4")
    try:
        text = f"{float(text):.1f}"
    except ValueError:
        raise ExecuteError(f"ERROR 000800: '{value}' is not a LAS version.") from None
    if text not in _FORMATS:
        raise ExecuteError(f"ERROR 000800: '{value}' is not a LAS version (1.1–1.4).")
    return text


def _point_format(value, version, source_format, tool):
    """The output point record format: the one asked for, which the version must hold; else the source's, or its
    counterpart across the 1.4 divide where the version does not hold it (INFERRED)."""
    if _given(value):
        wanted = int(value)
        if wanted not in _FORMATS[version]:
            raise ExecuteError(f"ERROR: {tool}: LAS {version} has no point record format {wanted}; it holds "
                               f"{', '.join(map(str, _FORMATS[version]))}.")
        return wanted
    if source_format in _FORMATS[version]:
        return source_format
    return _DOWN.get(source_format, source_format) if version != "1.4" else _UP.get(source_format, source_format)


def _check_holds(las, fmt, tool):
    if fmt >= 6:
        return
    a = _las.arrays(las)
    if len(a["classification"]) and a["classification"].max() > 31:
        raise ExecuteError(f"ERROR: {tool}: point record format {fmt} holds class codes 0–31, and the input has "
                           f"{int(a['classification'].max())}; a format must support the input's class codes.")
    if a["overlap"].any():
        raise ExecuteError(f"ERROR: {tool}: point record format {fmt} has no overlap flag, and the input has overlap "
                           "points; a format must support the input's flags.")


def _rebuild(las, fmt, version, *, extra=True, vlrs=True, crs="keep", scales=None, offsets=None, xyz=None,
             extra_names=None):
    """A copy of ``las`` in point record format ``fmt`` and LAS ``version``: each dimension both formats have copied,
    the scan angle converted between its two encodings, the coordinate system kept (or replaced by ``crs``), extra bytes
    kept unless ``extra`` is false, the other records kept unless ``vlrs`` is false; with ``xyz`` new coordinates."""
    import laspy
    header = laspy.LasHeader(point_format=fmt, version=version)
    header.scales = np.asarray(scales if scales is not None else las.header.scales, float)
    header.offsets = np.asarray(offsets if offsets is not None else las.header.offsets, float)
    source_extra = [(d.name, d.dtype) for d in las.point_format.extra_dimensions]
    if extra:
        for name, dtype in source_extra:
            if extra_names is None or name in extra_names:
                header.add_extra_dims([laspy.ExtraBytesParams(name=name, type=dtype)])
    if vlrs:
        for vlr in las.header.vlrs:
            if type(vlr).__name__ in ("GeoKeyDirectoryVlr", "GeoAsciiParamsVlr", "GeoDoubleParamsVlr",
                                      "WktCoordinateSystemVlr", "ExtraBytesVlr"):
                continue
            header.vlrs.append(vlr)
    target_crs = las.header.parse_crs() if crs == "keep" else crs
    if target_crs is not None:
        header.add_crs(target_crs)
    out = laspy.LasData(header, laspy.ScaleAwarePointRecord.zeros(len(las.points), header=header))
    have = set(las.point_format.dimension_names)
    for name in out.point_format.dimension_names:
        if name in ("X", "Y", "Z"):
            continue
        if name in have:
            out[name] = np.asarray(las[name])
        elif name == "scan_angle" and "scan_angle_rank" in have:
            out[name] = np.round(np.asarray(las["scan_angle_rank"], float) / 0.006).astype(np.int16)
        elif name == "scan_angle_rank" and "scan_angle" in have:
            out[name] = np.clip(np.round(np.asarray(las["scan_angle"], float) * 0.006), -90, 90).astype(np.int8)
    x, y, z = xyz if xyz is not None else (np.asarray(las.x), np.asarray(las.y), np.asarray(las.z))
    out.x, out.y, out.z = x, y, z
    return out


def _scan_angle(las):
    """Each point's scan angle in degrees: the rank of formats 0–5, or the 0.006° steps of formats 6–10."""
    names = set(las.point_format.dimension_names)
    if "scan_angle" in names:
        return np.asarray(las["scan_angle"], float) * 0.006
    return np.asarray(las["scan_angle_rank"], float)


# ---- conversion.ConvertLas


def ConvertLas(in_las, target_folder, file_version="SAME_AS_INPUT", point_format=None, compression="NO_COMPRESSION",
               las_options="REARRANGE_POINTS", out_las_dataset=None, define_coordinate_system="NO_FILES",
               in_coordinate_system=None):
    """Each LAS file — named, in a folder, or of a LAS dataset, whole and with no layer filter, as documented — written
    in ``target_folder`` in another LAS version and point record format. A version may rise but "a version 1.4 file
    cannot be converted to an earlier version", and the output format must hold the input's class codes and flags, as
    documented; a field the output format lacks is dropped. ``define_coordinate_system`` writes
    ``in_coordinate_system`` into ALL_FILES or the FILES_MISSING_PROJECTION. REMOVE_VLR and REMOVE_EXTRA_BYTES as
    documented.

    Refused: ZLAS and EDITED_ZLAS_TO_NON_EDITED — zLAS is Esri's format, which is not published; LAZ while no LAZ
    backend is installed; version 1.0, which laspy does not write.

    INFERRED: an output keeps its source's name with ``.las``; the point format defaults to the source's, or its
    counterpart across the 1.4 divide; REARRANGE_POINTS keeps the order."""
    import laspy
    from ._reference import reference_of
    kind = _upper(compression, "NO_COMPRESSION")
    if kind in ("ZLAS", "EDITED_ZLAS_TO_NON_EDITED"):
        _refuse("conversion", "ConvertLas", "compression", compression, _ZLAS)
    if kind == "LAZ" and not laspy.LazBackend.detect_available():
        _refuse("conversion", "ConvertLas", "compression", compression,
                "no LAZ backend (lazrs or laszip) is installed with grapy (ADR-229 §5.5)")
    if kind not in ("NO_COMPRESSION", "LAZ"):
        _refuse("conversion", "ConvertLas", "compression", compression)
    options = {_upper(o, "") for o in _list(las_options)}
    define = _upper(define_coordinate_system, "NO_FILES")
    if define not in ("NO_FILES", "ALL_FILES", "FILES_MISSING_PROJECTION"):
        _refuse("conversion", "ConvertLas", "define_coordinate_system", define_coordinate_system)
    crs = None
    if define != "NO_FILES":
        if not _given(in_coordinate_system):
            raise ExecuteError("ERROR 000735: define_coordinate_system needs in_coordinate_system.")
        from pyproj import CRS
        crs = CRS.from_epsg(reference_of(in_coordinate_system).factoryCode)
    folder = _target(target_folder)
    written = []
    for path in _files(in_las):
        las = _las.read_file(path)
        source = f"{las.header.version.major}.{las.header.version.minor}"
        same = _upper(file_version, "SAME_AS_INPUT") == "SAME_AS_INPUT"
        version = source if same else _version(file_version, "1.4", "ConvertLas", "conversion")
        if source == "1.4" and version != "1.4":
            raise ExecuteError(f"ERROR: ConvertLas: '{path}' is LAS 1.4, and a version 1.4 file cannot be converted to an "
                               "earlier version, as documented.")
        if version < source:
            raise ExecuteError(f"ERROR: ConvertLas: '{path}' is LAS {source}; its version can be raised, not lowered.")
        fmt = _point_format(point_format, version, int(las.header.point_format.id), "ConvertLas")
        _check_holds(las, fmt, "ConvertLas")
        keep_crs = "keep"
        if crs is not None and (define == "ALL_FILES" or _las.srid_of(las) == 0):
            keep_crs = crs
        out = _rebuild(las, fmt, version, extra="REMOVE_EXTRA_BYTES" not in options, vlrs="REMOVE_VLR" not in options,
                       crs=keep_crs)
        target = _out_name(folder, path)
        if kind == "LAZ":
            target = target[:-4] + ".laz"
        out.write(target)
        written.append(target)
    _dataset_of(written, out_las_dataset, True)
    return Result(folder)


# ---- management.ProjectLAS


def _source_crs(path, las):
    """A LAS file's coordinate system: a .prj of the same name beside it overrides the header's, as documented."""
    from pyproj import CRS
    prj = os.path.splitext(path)[0] + ".prj"
    if os.path.exists(prj):
        return CRS.from_user_input(open(prj, encoding="utf-8").read())
    return las.header.parse_crs()


def ProjectLAS(in_las_dataset, target_folder, coordinate_system, geographic_transform=None,
               compression="SAME_AS_INPUT", las_options="REARRANGE", name_modifier=None, out_las_dataset=None):
    """Each LAS file — one file, or the files of a LAS dataset — projected into ``coordinate_system`` and written in
    ``target_folder``, its name given the prefix and suffix of ``name_modifier``, as documented. A file needs a
    coordinate system, and a .prj file of the same name overrides the one inside it, as documented. The x and y are
    projected by PROJ; z is transformed too where both systems have a vertical datum, and is otherwise kept. With
    ``out_las_dataset`` a LAS dataset of relative paths, as documented, its statistics computed with REARRANGE or
    COMPUTE_STATISTICS.

    Refused: ``geographic_transform`` — grapy uses PROJ's default transformation between two systems, and Esri's
    transformation names are not PROJ's; ZLAS output (Esri's format is not published).

    INFERRED: the output's scale is 0.0000001 for x and y in a geographic system and 0.001 otherwise, z's scale kept, and
    its offsets the whole units below its smallest coordinates; REARRANGE keeps the order."""
    from pyproj import CRS, Transformer
    from ._reference import reference_of
    from ._tin import rows_of
    if _given(geographic_transform):
        _refuse("management", "ProjectLAS", "geographic_transform", geographic_transform,
                "grapy projects with PROJ's default transformation, and Esri's transformation names are not PROJ's")
    kind = _upper(compression, "SAME_AS_INPUT")
    if kind == "ZLAS":
        _refuse("management", "ProjectLAS", "compression", compression, _ZLAS)
    if kind not in ("SAME_AS_INPUT", "NO_COMPRESSION"):
        _refuse("management", "ProjectLAS", "compression", compression)
    reference = reference_of(coordinate_system)
    if reference is None:
        raise ExecuteError("ERROR 000735: ProjectLAS needs coordinate_system.")
    target_crs = CRS.from_epsg(reference.factoryCode)
    options = {_upper(o, "") for o in _list(las_options)}
    modifier = rows_of(name_modifier, 2)
    prefix, suffix = (modifier[0][0], modifier[0][1]) if modifier else ("", "")
    folder = _target(target_folder)
    written = []
    for path in _files(in_las_dataset):
        las = _las.read_file(path)
        source_crs = _source_crs(path, las)
        if source_crs is None:
            raise ExecuteError(f"ERROR: ProjectLAS: '{path}' has no coordinate system; define one first, as documented.")
        move = Transformer.from_crs(source_crs, target_crs, always_xy=True)
        x, y, z = move.transform(np.asarray(las.x), np.asarray(las.y), np.asarray(las.z))
        x, y, z = np.asarray(x, float), np.asarray(y, float), np.asarray(z, float)
        if not (np.isfinite(x).all() and np.isfinite(y).all() and np.isfinite(z).all()):
            raise ExecuteError(f"ERROR: ProjectLAS: some points of '{path}' fall outside {target_crs.name}.")
        xy_scale = 1e-7 if target_crs.is_geographic else 0.001
        scales = [xy_scale, xy_scale, float(las.header.scales[2])]
        offsets = [math.floor(x.min()) if len(x) else 0.0, math.floor(y.min()) if len(y) else 0.0,
                   math.floor(z.min()) if len(z) else 0.0]
        version = f"{las.header.version.major}.{las.header.version.minor}"
        out = _rebuild(las, int(las.header.point_format.id), version, extra="REMOVE_EXTRA_BYTES" not in options,
                       vlrs="REMOVE_VLR" not in options, crs=target_crs, scales=scales, offsets=offsets, xyz=(x, y, z))
        target = _out_name(folder, path, prefix, suffix)
        out.write(target)
        written.append(target)
    _dataset_of(written, out_las_dataset, bool(options & {"REARRANGE", "COMPUTE_STATISTICS"}), reference.factoryCode,
                relative=True)
    return Result(folder)


# ---- ddd.ColorizeLas


def ColorizeLas(in_las_dataset, in_image, bands, target_folder, name_suffix=None, las_version=1.4, point_format=7,
                compression="NO_COMPRESSION", rearrange_points="REARRANGE_POINTS", compute_stats="COMPUTE_STATS",
                out_las_dataset=None):
    """Each LAS file of the dataset written in ``target_folder`` with its name plus ``name_suffix``, in LAS 1.2, 1.3 or
    1.4 and point record format 2, 3, 7 or 8, its RED, GREEN, BLUE and NIR channels taken from the image's bands named
    in ``bands`` (``"RED Band_1; GREEN Band_2; BLUE Band_3"``), as documented. NIR needs format 8, and formats 7 and 8
    LAS 1.4, as documented.

    Refused: ZLAS compression (Esri's format is not published).

    INFERRED, the page not saying: a point takes the value of the image cell it falls in, unresampled, rounded and held
    within 0–65535, and 0 where it falls outside the image or on NoData; a channel not named keeps the value the source
    has, or 0; every point of a file is coloured, the layer's filters not read; the points are moved into the image's
    coordinate system to find their cells when the two differ."""
    from ._raster import as_raster, _band_number
    from ._tin import rows_of
    if _upper(compression, "NO_COMPRESSION") == "ZLAS":
        _refuse("ddd", "ColorizeLas", "compression", compression, _ZLAS)
    version = _version(las_version, "1.4", "ColorizeLas")
    if version == "1.1":
        raise ExecuteError("ERROR 000800: ColorizeLas writes LAS 1.2, 1.3 or 1.4.")
    fmt = int(point_format) if _given(point_format) else 7
    if fmt not in (2, 3, 7, 8):
        raise ExecuteError(f"ERROR 000800: ColorizeLas writes point record format 2, 3, 7 or 8, not {point_format}.")
    if fmt >= 7 and version != "1.4":
        raise ExecuteError(f"ERROR: point record format {fmt} needs LAS 1.4, as documented.")
    image = as_raster(in_image, "ColorizeLas", bands=True)
    channels = {}
    for channel, band in (r[:2] for r in rows_of(bands, 2)):
        name = str(channel).upper()
        if name not in ("RED", "GREEN", "BLUE", "NIR"):
            raise ExecuteError(f"ERROR 000800: '{channel}' is not a LAS channel (RED, GREEN, BLUE or NIR).")
        if name == "NIR" and fmt != 8:
            raise ExecuteError("ERROR: near-infrared is stored only in LAS 1.4 point record format 8, as documented.")
        channels[name.lower()] = image._band(_band_number(band, image.bandNames, repr(image)))
    if not channels:
        raise ExecuteError("ERROR 000735: ColorizeLas needs at least one channel in bands.")
    folder = _target(target_folder)
    written = []
    for path in _files(in_las_dataset):
        las = _las.read_file(path)
        _check_holds(las, fmt, "ColorizeLas")
        out = _rebuild(las, fmt, version)
        x, y = np.asarray(las.x, float), np.asarray(las.y, float)
        grid = next(iter(channels.values()))._grid
        srid = _las.srid_of(las)
        if grid.srid and srid and grid.srid != srid:
            from pyproj import Transformer
            x, y = Transformer.from_crs(srid, grid.srid, always_xy=True).transform(x, y)
            x, y = np.asarray(x, float), np.asarray(y, float)
        for name, band in channels.items():
            g = band._grid
            column = np.floor((x - g.left) / g.width).astype(np.int64)
            row = np.floor((g.top - y) / g.height).astype(np.int64)
            inside = (row >= 0) & (row < g.rows) & (column >= 0) & (column < g.columns)
            values = np.zeros(len(x))
            r, c = row[inside], column[inside]
            cells = np.asarray(band._array, float)[r, c]
            cells[np.asarray(band._mask)[r, c]] = 0
            values[inside] = cells
            out[name] = np.clip(np.rint(values), 0, 65535).astype(np.uint16)
        target = _out_name(folder, path, suffix=name_suffix if _given(name_suffix) else "")
        out.write(target)
        written.append(target)
    _dataset_of(written, out_las_dataset, _upper(compute_stats, "COMPUTE_STATS") == "COMPUTE_STATS" or
                _upper(rearrange_points, "REARRANGE_POINTS") == "REARRANGE_POINTS")
    return Result(folder)


# ---- ddd.ThinLas


def _first_per_group(group, *keys):
    """The index of the first point of each group, ordered by ``keys`` (most significant first), ties by file order."""
    order = np.lexsort((np.arange(len(group)), *reversed(keys), group))
    g = group[order]
    starts = np.r_[0, np.nonzero(np.diff(g))[0] + 1] if len(g) else np.zeros(0, np.int64)
    return order[starts]


def _returns_mask(a, wanted):
    last = a["return_number"] == a["number_of_returns"]
    many = a["number_of_returns"] > 1
    out = np.zeros(len(a["x"]), bool)
    for r in wanted:
        out |= {"SINGLE": a["number_of_returns"] == 1, "LAST": last, "FIRST_OF_MANY": many & (a["return_number"] == 1),
                "LAST_OF_MANY": many & last}.get(r, np.zeros(len(out), bool))
    return out


def _by(a, codes, flags, returns, tool, what):
    """The points named by class codes, flags and return types (any of them)."""
    hit = np.zeros(len(a["x"]), bool)
    if codes:
        hit |= np.isin(a["classification"], [int(c) for c in codes])
    for f in flags:
        name = _FLAG_NAMES.get(str(f).upper())
        if name is None:
            _refuse("ddd", tool, what + "_flags", f)
        hit |= a[name]
    wanted = [str(r).upper() for r in returns]
    for r in wanted:
        if r not in ("SINGLE", "LAST", "FIRST_OF_MANY", "LAST_OF_MANY"):
            _refuse("ddd", tool, what + "_returns", r)
    if wanted:
        hit |= _returns_mask(a, wanted)
    return hit


def ThinLas(in_las_dataset, target_folder, thinning_dimension="3D", xy_resolution=None, z_resolution=None,
            point_selection_method="CLOSEST_TO_CENTER", class_codes_weights=None, name_suffix=None, out_las_dataset=None,
            preserved_class_codes=None, preserved_flags=None, preserved_returns=None, excluded_class_codes=None,
            excluded_flags=None, excluded_returns=None, compression="NO_COMPRESSION", remove_vlr="MAINTAIN_VLR",
            rearrange_points="REARRANGE_POINTS", compute_stats="COMPUTE_STATS"):
    """For each LAS file, thinned on its own as documented, a LAS file in ``target_folder`` (its name plus
    ``name_suffix``) holding one point — two for the MIN_MAX methods — of each thinning region: a square of
    ``xy_resolution`` (2D), or that square cut into heights of ``z_resolution`` (3D). The point kept is the one
    CLOSEST_TO_CENTER of the region; of the CLASS_CODE with the highest weight, ties to the lower point source ID, as
    documented; Z_MIN, Z_MAX, both, or Z_AVERAGE (closest to the region's mean height); INTENSITY_MIN, INTENSITY_MAX,
    both, or INTENSITY_AVERAGE; or one of the PREDOMINANT_CLASS. Excluded class codes, flags and returns are left out;
    preserved ones are kept without thinning, as documented.

    Refused: ZLAS compression (Esri's format is not published).

    INFERRED: the regions start at the file's lower-left corner and lowest z; CLOSEST_TO_CENTER measures in 2D for 2D
    thinning and in 3D for 3D; a class without a weight weighs 0; PREDOMINANT_CLASS keeps, of the region's most frequent
    class (the smaller code on a tie), the point closest to the centre; other ties go to the earlier point in the file;
    the layer's filters are honoured, as ExtractLas's are; a file left with no point is not written."""
    import laspy
    from ._tin import rows_of
    if _upper(compression, "NO_COMPRESSION") == "ZLAS":
        _refuse("ddd", "ThinLas", "compression", compression, _ZLAS)
    dimension = _upper(thinning_dimension, "3D")
    if dimension not in ("2D", "3D"):
        raise ExecuteError(f"ERROR 000800: thinning_dimension is 2D or 3D, not '{thinning_dimension}'.")
    if not _given(xy_resolution):
        raise ExecuteError("ERROR 000735: ThinLas needs xy_resolution.")
    xy = _length(xy_resolution)
    zr = None
    if dimension == "3D":
        if not _given(z_resolution):
            raise ExecuteError("ERROR 000735: 3D thinning needs z_resolution; the page gives no default.")
        zr = _length(z_resolution)
    method = _upper(point_selection_method, "CLOSEST_TO_CENTER")
    methods = ("CLOSEST_TO_CENTER", "CLASS_CODE", "PREDOMINANT_CLASS", "Z_MIN", "Z_MAX", "Z_MIN_MAX", "Z_AVERAGE",
               "INTENSITY_MIN", "INTENSITY_MAX", "INTENSITY_MIN_MAX", "INTENSITY_AVERAGE")
    if method not in methods:
        raise ExecuteError(f"ERROR 000800: '{point_selection_method}' is not a point selection method.")
    weights = {int(r[0]): int(r[1]) for r in rows_of(class_codes_weights, 2) if str(r[0]) != ""}
    if method == "CLASS_CODE" and not weights:
        raise ExecuteError("ERROR 000735: the CLASS_CODE method needs class_codes_weights.")
    found, dataset = _las.points(in_las_dataset, "ThinLas")
    folder = _target(target_folder)
    written = []
    for number, path in enumerate(dataset.file_paths()):
        las = _las.read_file(path)
        a = _las.arrays(las)
        shown = np.zeros(len(a["x"]), bool)
        shown[found["index"][found["file"] == number]] = True
        excluded = _by(a, _list(excluded_class_codes), _list(excluded_flags), _list(excluded_returns), "ThinLas",
                       "excluded")
        preserved = _by(a, _list(preserved_class_codes), _list(preserved_flags), _list(preserved_returns), "ThinLas",
                        "preserved")
        keep = shown & ~excluded & preserved
        thin = np.nonzero(shown & ~excluded & ~preserved)[0]
        if len(thin):
            x, y, z = a["x"][thin], a["y"][thin], a["z"][thin]
            ox, oy, oz = (float(v) for v in las.header.mins)
            cells = [np.floor((x - ox) / xy), np.floor((y - oy) / xy)]
            if zr:
                cells.append(np.floor((z - oz) / zr))
            _, group = np.unique(np.column_stack(cells), axis=0, return_inverse=True)
            group = group.ravel()
            centre = [ox + (cells[0] + 0.5) * xy, oy + (cells[1] + 0.5) * xy]
            distance = (x - centre[0]) ** 2 + (y - centre[1]) ** 2
            if zr:
                distance = distance + (z - (oz + (cells[2] + 0.5) * zr)) ** 2
            values = {"Z": z, "INTENSITY": a["intensity"][thin].astype(float)}
            chosen = []
            if method == "CLOSEST_TO_CENTER":
                chosen.append(_first_per_group(group, distance))
            elif method == "CLASS_CODE":
                weight = np.array([weights.get(int(c), 0) for c in a["classification"][thin]], float)
                source = np.asarray(las.point_source_id, np.int64)[thin]
                chosen.append(_first_per_group(group, -weight, source))
            elif method == "PREDOMINANT_CLASS":
                classes = a["classification"][thin]
                pairs, counts = np.unique(np.column_stack([group, classes]), axis=0, return_counts=True)
                order = np.lexsort((pairs[:, 1], -counts, pairs[:, 0]))
                firsts = np.r_[0, np.nonzero(np.diff(pairs[order, 0]))[0] + 1]
                mode = dict(zip(pairs[order][firsts, 0].tolist(), pairs[order][firsts, 1].tolist()))
                other = (classes != np.array([mode[g] for g in group.tolist()])).astype(float)
                chosen.append(_first_per_group(group, other, distance))
            else:
                field, how = method.split("_", 1)
                v = values[field]
                if how in ("MIN", "MIN_MAX"):
                    chosen.append(_first_per_group(group, v))
                if how in ("MAX", "MIN_MAX"):
                    chosen.append(_first_per_group(group, -v))
                if how == "AVERAGE":
                    mean = np.bincount(group, weights=v) / np.bincount(group)
                    chosen.append(_first_per_group(group, np.abs(v - mean[group])))
            keep[thin[np.unique(np.concatenate(chosen))]] = True
        if not keep.any():
            continue
        out = laspy.LasData(las.header)
        out.points = las.points[np.nonzero(keep)[0]]
        if _upper(remove_vlr, "MAINTAIN_VLR") == "REMOVE_VLR":
            out.header.vlrs = laspy.VLRList([v for v in las.header.vlrs if getattr(v, "user_id", "") == "LASF_Projection"])
        target = _out_name(folder, path, suffix=name_suffix if _given(name_suffix) else "")
        out.write(target)
        written.append(target)
    _dataset_of(written, out_las_dataset, _upper(compute_stats, "COMPUTE_STATS") == "COMPUTE_STATS", dataset.srid)
    return Result(folder)


# ---- ddd.TileLas


def _coordinate(value):
    value = round(float(value), 6)
    return str(int(value)) if value == int(value) else f"{value:f}".rstrip("0").rstrip(".")


def TileLas(in_las_dataset, target_folder, base_name=None, out_las_dataset=None, compute_stats="COMPUTE_STATS",
            las_version="1.3", point_format=None, compression="NO_COMPRESSION", las_options=None, tile_feature=None,
            naming_method="XY_COORDS", file_size=250, tile_width=None, tile_height=None, tile_origin=None):
    """Nonoverlapping LAS files, one per cell of a regular grid that holds points, gathering every point of the
    dataset's files — the layer's filters not applied, as documented — named ``<base>_<x>_<y>`` after a tile's
    lower-left corner (XY_COORDS), ``<base>_<row>_<column>`` with rows from the bottom and columns from the left
    (ROW_COLUMN), or ``<base>_<n>`` in order from the bottom left to the top right with leading zeros to the largest
    number (ORDINAL), as documented. The grid starts at ``tile_origin``, by default the dataset's lower-left corner; its
    cells are ``tile_width`` by ``tile_height``; with ``tile_feature`` the first feature's extent gives the cell size and
    the origin, as documented. Without either, the size is estimated from ``file_size`` (MB of uncompressed points),
    "from average point spacing and the bytes needed per point", assuming the points are evenly spread, as documented.

    Refused: ZLAS compression (Esri's format is not published); version 1.0, which laspy does not write.

    INFERRED: a point on a tile's left or bottom edge belongs to it; one of ``tile_width`` and ``tile_height`` given
    alone sets both; a megabyte is 1,048,576 bytes; the base name defaults to the LAS dataset's name; rows and columns
    count from 1 at the origin; the point format defaults to the first file's, or its counterpart across the 1.4 divide;
    extra bytes every file has are kept; the coordinate system and other records are the first file's; the tile's
    scale is the first file's and its offset its lower-left corner."""
    import laspy
    from . import da
    if _upper(compression, "NO_COMPRESSION") not in ("NO_COMPRESSION",):
        if "ZLAS" in _upper(compression, ""):
            _refuse("ddd", "TileLas", "compression", compression, _ZLAS)
        _refuse("ddd", "TileLas", "compression", compression)
    version = _version(las_version, "1.3", "TileLas")
    options = {_upper(o, "") for o in _list(las_options)}
    naming = _upper(naming_method, "XY_COORDS")
    if naming not in ("XY_COORDS", "ROW_COLUMN", "ORDINAL"):
        raise ExecuteError(f"ERROR 000800: '{naming_method}' is not a naming method.")
    dataset = _las.LasDataset.open(in_las_dataset)
    paths = dataset.file_paths()
    if not paths:
        raise ExecuteError("ERROR: the LAS dataset holds no LAS file.")
    sources = [_las.read_file(p) for p in paths]
    first = sources[0]
    fmt = _point_format(point_format, version, int(first.header.point_format.id), "TileLas")
    extra = None
    if "REMOVE_EXTRA_BYTES" not in options:
        sets = [{(d.name, str(d.dtype)) for d in s.point_format.extra_dimensions} for s in sources]
        extra = {name for name, _ in set.intersection(*sets)} if sets else set()
    converted = []
    for s in sources:
        _check_holds(s, fmt, "TileLas")
        converted.append(_rebuild(s, fmt, version, extra=bool(extra), extra_names=extra, vlrs=False))
    x = np.concatenate([np.asarray(c.x, float) for c in converted])
    y = np.concatenate([np.asarray(c.y, float) for c in converted])
    z = np.concatenate([np.asarray(c.z, float) for c in converted])
    if not len(x):
        raise ExecuteError("ERROR: the LAS dataset holds no point.")
    ox, oy = float(x.min()), float(y.min())
    width = height = None
    if _given(tile_feature):
        with da.SearchCursor(tile_feature, ["SHAPE@"]) as cursor:
            shapes = [r[0] for r in cursor if r[0] is not None]
        if not shapes:
            raise ExecuteError("ERROR: tile_feature holds no feature.")
        xmin, ymin, xmax, ymax = shapely.bounds(shapes[0]._shape)
        ox, oy, width, height = float(xmin), float(ymin), float(xmax - xmin), float(ymax - ymin)
    else:
        if _given(tile_origin):
            if hasattr(tile_origin, "X"):
                ox, oy = float(tile_origin.X), float(tile_origin.Y)
            else:
                ox, oy = (float(v) for v in str(tile_origin).replace(",", " ").split()[:2])
        if _given(tile_width) or _given(tile_height):
            width = _length(tile_width) if _given(tile_width) else _length(tile_height)
            height = _length(tile_height) if _given(tile_height) else width
        else:
            size = float(file_size) if _given(file_size) else 250.0
            record = converted[0].point_format.size
            per_tile = size * 1048576 / record
            area = max((x.max() - x.min()) * (y.max() - y.min()), 1e-12)
            width = height = math.sqrt(per_tile * area / len(x))
    if width <= 0 or height <= 0:
        raise ExecuteError("ERROR: a tile's width and height must be positive.")
    column = np.floor((x - ox) / width).astype(np.int64)
    row = np.floor((y - oy) / height).astype(np.int64)
    tiles = sorted({(int(r), int(c)) for r, c in zip(row, column)})
    folder = _target(target_folder)
    base = str(base_name) if _given(base_name) else os.path.splitext(os.path.basename(dataset.path))[0]
    digits = len(str(len(tiles)))
    source_index = np.concatenate([np.full(len(c.points), k) for k, c in enumerate(converted)])
    point_index = np.concatenate([np.arange(len(c.points)) for c in converted])
    crs = first.header.parse_crs()
    written = []
    for ordinal, (r, c) in enumerate(tiles, 1):
        mine = np.nonzero((row == r) & (column == c))[0]
        left, bottom = ox + c * width, oy + r * height
        header = laspy.LasHeader(point_format=converted[0].point_format, version=version)
        header.scales = first.header.scales
        header.offsets = [math.floor(left), math.floor(bottom), float(first.header.offsets[2])]
        if "REMOVE_VLR" not in options:
            for vlr in first.header.vlrs:
                if type(vlr).__name__ not in ("GeoKeyDirectoryVlr", "GeoAsciiParamsVlr", "GeoDoubleParamsVlr",
                                              "WktCoordinateSystemVlr", "ExtraBytesVlr"):
                    header.vlrs.append(vlr)
        if crs is not None:
            header.add_crs(crs)
        out = laspy.LasData(header, laspy.ScaleAwarePointRecord.zeros(len(mine), header=header))
        for name in out.point_format.dimension_names:
            if name in ("X", "Y", "Z"):
                continue
            out[name] = np.concatenate([np.asarray(converted[k][name])[point_index[mine][source_index[mine] == k]]
                                        for k in range(len(converted))])
        order = np.concatenate([mine[source_index[mine] == k] for k in range(len(converted))])
        out.x, out.y, out.z = x[order], y[order], z[order]
        if naming == "XY_COORDS":
            name = f"{base}_{_coordinate(left)}_{_coordinate(bottom)}"
        elif naming == "ROW_COLUMN":
            name = f"{base}_{r + 1}_{c + 1}"
        else:
            name = f"{base}_{ordinal:0{digits}d}"
        target = os.path.join(folder, name + ".las")
        out.write(target)
        written.append(target)
    _dataset_of(written, out_las_dataset, _upper(compute_stats, "COMPUTE_STATS") == "COMPUTE_STATS", dataset.srid)
    return Result(folder)


# ---- ddd.ClassifyLasOverlap


def ClassifyLasOverlap(in_las_dataset, sample_distance, extent=None, process_entire_files="PROCESS_EXTENT",
                       compute_stats="COMPUTE_STATS", update_pyramid="UPDATE_PYRAMID"):
    """Overlap points classified in their files: each file on its own, as documented, in squares of side
    ``sample_distance``; in a square holding points of more than one point source ID, the source whose point has the
    scan angle closest to nadir keeps its points and every other source's points there are overlap — given the overlap
    flag in point record formats 6 and above, keeping their class, and class 12 in the others, as documented. With an
    extent only the points within it are evaluated, unless PROCESS_ENTIRE_FILES. ``update_pyramid`` has no pyramid to
    update in grapy.

    INFERRED: the squares start at the file's lower-left corner; the angle closest to nadir is the smallest absolute
    angle, a tie going to the lower point source ID; every point of the file is evaluated, the layer's filters not read,
    as ChangeLasClassCodes does; the flag is used for formats 9 and 10 too, which have it."""
    distance = _length(sample_distance)
    if distance <= 0:
        raise ExecuteError("ERROR: sample_distance must be positive.")
    area = _las._region(extent, None, process_entire_files)
    dataset = _las.LasDataset.open(in_las_dataset)
    changed = []
    for path in dataset.file_paths():
        las = _las.read_file(path)
        a = _las.arrays(las)
        evaluated = np.ones(len(a["x"]), bool)
        if area is not None:
            evaluated &= shapely.covers(area, shapely.points(a["x"], a["y"]))
        index = np.nonzero(evaluated)[0]
        if not len(index):
            continue
        ox, oy = (float(v) for v in las.header.mins[:2])
        cells = np.column_stack([np.floor((a["x"][index] - ox) / distance), np.floor((a["y"][index] - oy) / distance)])
        _, group = np.unique(cells, axis=0, return_inverse=True)
        group = group.ravel()
        source = np.asarray(las.point_source_id, np.int64)[index]
        angle = np.abs(_scan_angle(las))[index]
        winner = source[_first_per_group(group, angle, source)]
        overlap = source != winner[group]
        if not overlap.any():
            continue
        where = index[overlap]
        if las.header.point_format.id >= 6:
            flags = np.asarray(las["overlap"]).astype(np.uint8)
            flags[where] = 1
            las["overlap"] = flags
        else:
            classes = np.asarray(las.classification, np.uint8).copy()
            classes[where] = 12
            las.classification = classes
        las.write(path)
        changed.append(path)
    _las._update_stats(in_las_dataset, changed, _upper(compute_stats, "COMPUTE_STATS") == "COMPUTE_STATS")
    return Result(_las._lasd_path(in_las_dataset))


# ---- ddd.ClassifyLasBuilding: an equivalent


def ClassifyLasBuilding(in_las_dataset, min_height=None, min_area=None, compute_stats="COMPUTE_STATS", extent=None,
                        boundary=None, process_entire_files="PROCESS_EXTENT", point_spacing=None,
                        reuse_building="RECLASSIFY_BUILDING", photogrammetric_data="NOT_PHOTOGRAMMETRIC_DATA",
                        method="STANDARD", classify_above_roof="NO_CLASSIFY_ABOVE_ROOF", above_roof_height=None,
                        above_roof_code=None, classify_below_roof="NO_CLASSIFY_BELOW_ROOF", below_roof_code=None,
                        update_pyramid="UPDATE_PYRAMID"):
    """EQUIVALENT: plane detection by region growing with a smoothness constraint (Rabbani, van den Heuvel and
    Vosselman 2006, *Segmentation of point clouds using smoothness constraint*, IAPRS 36(5)), not ArcGIS Pro's rooftop
    classifier — the page names a "plane detection process" without describing it. As documented: points of class 0,
    1 and 6 are evaluated, withheld and overlap points ignored; ground must be classified first (class 2); a rooftop is
    at least ``min_height`` above the ground and covers at least ``min_area``; its points become class 6, and with
    RECLASSIFY_BUILDING evaluated class 6 points that are not rooftop become 1, while REUSE_BUILDING keeps them.
    ``point_spacing`` is "no longer used", as documented.

    The equivalent: the ground is a TIN of the class 2 points; each candidate's neighbours are the candidates within
    r = 2 × the dataset's point spacing (√(area ÷ count)); a candidate is planar when the RMS distance of its neighbours
    (three or more) to their least-squares plane is within the method's tolerance and the plane is no steeper than 60°;
    planar points within r whose normals differ by no more than 15° grow into one plane; touching planes (within r)
    form a rooftop, whose area is its points' 2D convex hull.

    Refused: PHOTOGRAMMETRIC_DATA — the page does not say how the processing differs; classifying points above and
    below the roof — the equivalent finds roof planes, and the page does not say what outline the points are taken in.

    INFERRED: the method's tolerance — CONSERVATIVE 0.05, STANDARD 0.1, AGGRESSIVE 0.2 (data units), the page saying
    only that it sets the tolerance for irregular points; ``min_height`` defaults to 2, the page giving none; r, 60° and
    15° as above."""
    from scipy.sparse import coo_matrix
    from scipy.sparse.csgraph import connected_components
    from scipy.spatial import cKDTree
    from ._tin import triangulate
    tolerances = {"CONSERVATIVE": 0.05, "STANDARD": 0.1, "AGGRESSIVE": 0.2}
    kind = _upper(method, "STANDARD")
    if kind not in tolerances:
        raise ExecuteError(f"ERROR 000800: '{method}' is not a classification method.")
    if _upper(photogrammetric_data, "NOT_PHOTOGRAMMETRIC_DATA") != "NOT_PHOTOGRAMMETRIC_DATA":
        _refuse("ddd", "ClassifyLasBuilding", "photogrammetric_data", photogrammetric_data,
                "the page does not say how photogrammetric points are processed differently")
    for name, value, default in (("classify_above_roof", classify_above_roof, "NO_CLASSIFY_ABOVE_ROOF"),
                                 ("classify_below_roof", classify_below_roof, "NO_CLASSIFY_BELOW_ROOF")):
        if _upper(value, default) != default:
            _refuse("ddd", "ClassifyLasBuilding", name, value, "the equivalent finds roof planes, and the page does not "
                    "say within what outline the points above or below a roof are taken")
    if not _given(min_area):
        raise ExecuteError("ERROR 000735: ClassifyLasBuilding needs min_area.")
    least_area = _area(min_area)
    least_height = _length(min_height) if _given(min_height) else 2.0
    reuse = _upper(reuse_building, "RECLASSIFY_BUILDING") == "REUSE_BUILDING"
    found, dataset = _las.points(in_las_dataset, "ClassifyLasBuilding", filtered=False)
    usable = ~found["withheld"] & ~found.get("overlap", np.zeros(len(found["x"]), bool))
    ground = usable & (found["classification"] == 2)
    if ground.sum() < 3:
        raise ExecuteError("ERROR: ClassifyLasBuilding needs ground points (class 2); classify the ground first, as "
                           "documented.")
    surface = triangulate(list(zip(found["x"][ground], found["y"][ground], found["z"][ground])), [], srid=dataset.srid)
    codes = (0, 1) if reuse else (0, 1, 6)
    evaluated = usable & np.isin(found["classification"], codes)
    region = _las._region(extent, boundary, process_entire_files)
    if region is not None:
        evaluated &= shapely.covers(region, shapely.points(found["x"], found["y"]))
    n = len(found["x"])
    box = (found["x"].max() - found["x"].min()) * (found["y"].max() - found["y"].min()) if n else 0.0
    radius = 2.0 * math.sqrt(box / n) if n and box > 0 else 1.0
    height = found["z"] - surface.z_at(found["x"], found["y"])
    candidate = np.nonzero(evaluated & (height >= least_height))[0]
    roof = np.zeros(n, bool)
    if len(candidate) >= 3:
        xyz = np.column_stack([found["x"][candidate], found["y"][candidate], found["z"][candidate]])
        tree = cKDTree(xyz)
        near = tree.query_ball_point(xyz, radius)
        normals = np.zeros((len(xyz), 3))
        planar = np.zeros(len(xyz), bool)
        for k, members in enumerate(near):
            if len(members) < 3:
                continue
            pts = xyz[members]
            centred = pts - pts.mean(axis=0)
            _, values, vectors = np.linalg.svd(centred, full_matrices=False)
            normal = vectors[-1]
            rms = math.sqrt(values[-1] ** 2 / len(pts))
            if rms <= tolerances[kind] and abs(normal[2]) >= 0.5:
                planar[k], normals[k] = True, normal
        pairs = tree.query_pairs(radius, output_type="ndarray")
        if len(pairs):
            both = planar[pairs[:, 0]] & planar[pairs[:, 1]]
            pairs = pairs[both]
        m = len(xyz)
        if len(pairs):
            touching = coo_matrix((np.ones(len(pairs)), (pairs[:, 0], pairs[:, 1])), shape=(m, m))
            _, rooftop = connected_components(touching, directed=False)
            cosine = np.abs(np.sum(normals[pairs[:, 0]] * normals[pairs[:, 1]], axis=1))
            smooth = pairs[cosine >= math.cos(math.radians(15.0))]
            plane_graph = coo_matrix((np.ones(len(smooth)), (smooth[:, 0], smooth[:, 1])), shape=(m, m))
            _, plane = connected_components(plane_graph, directed=False)
            plane_size = np.bincount(plane, minlength=m)
            in_plane = planar & (plane_size[plane] >= 3)
            for label in np.unique(rooftop[in_plane]):
                members = np.nonzero(in_plane & (rooftop == label))[0]
                hull = shapely.convex_hull(shapely.multipoints(xyz[members, :2]))
                if hull.area >= least_area:
                    roof[candidate[members]] = True
    changed = []
    for number, path in enumerate(dataset.file_paths()):
        mine = found["file"] == number
        if not mine.any():
            continue
        las = _las.read_file(path)
        classes = np.asarray(las.classification, np.int64).copy()
        idx = found["index"][mine]
        was = classes.copy()
        if not reuse:
            demote = evaluated[mine] & (classes[idx] == 6) & ~roof[mine]
            classes[idx[demote]] = 1
        classes[idx[roof[mine]]] = 6
        if (classes != was).any():
            las.classification = classes.astype(np.uint8)
            las.write(path)
            changed.append(path)
    _las._update_stats(in_las_dataset, changed, _upper(compute_stats, "COMPUTE_STATS") == "COMPUTE_STATS")
    return Result(_las._lasd_path(in_las_dataset))


# ---- ddd.LocateLasPointsByProximity


def _distance_3d(xyz, shape):
    """The 3D distance from each point to a point, multipoint or line with z."""
    best = np.full(len(xyz), np.inf)
    geoms = shapely.get_parts(shape)
    for part in geoms:
        coords = shapely.get_coordinates(part, include_z=True)
        if part.geom_type == "Point" or len(coords) == 1:
            best = np.minimum(best, np.linalg.norm(xyz - coords[0], axis=1))
            continue
        for p, q in zip(coords[:-1], coords[1:]):
            d = q - p
            length = float(d @ d)
            t = np.clip(((xyz - p) @ d) / length, 0, 1) if length > 0 else np.zeros(len(xyz))
            best = np.minimum(best, np.linalg.norm(xyz - (p + t[:, None] * d), axis=1))
    return best


def LocateLasPointsByProximity(in_las_dataset, in_features, search_radius, count_field="COUNT", out_features=None,
                               geometry="MULTIPOINT", class_code=None, compute_stats="COMPUTE_STATS",
                               update_pyramid="UPDATE_PYRAMID"):
    """The LAS points within ``search_radius`` — a distance, or a numeric field of the features — of z-enabled point or
    line features, in three dimensions, as documented: their number is written to ``count_field`` of each feature;
    with ``out_features`` they are written as POINT rows with ``DIST3D``, "the 3D distance" to the nearest feature, or
    as MULTIPOINT rows; with ``class_code`` they are reclassified in their files. The layer's filters are honoured, as
    documented.

    Refused: polygon and multipatch features — the page does not say whether a 3D polygon's distance is measured to its
    outline or its surface, and grapy holds no multipatch.

    INFERRED: the radius is measured in 3D and includes its edge; MULTIPOINT is the default, the page giving none, and
    writes one multipoint for each feature with points near it; POINT writes each point found once."""
    from . import da
    from .analysis import _schema
    from ._tintools import _ensure_fields, _write
    columns, kind, srid, _ = _schema(in_features)
    if kind not in ("Point", "Multipoint", "Polyline"):
        _refuse("ddd", "LocateLasPointsByProximity", "in_features", in_features,
                f"its features are {kind.lower()}s; the page does not say whether a 3D polygon's distance is to its "
                "outline or its surface, and grapy holds no multipatch")
    names = {c.name.lower(): c.name for c in columns}
    by_field = isinstance(search_radius, str) and search_radius.lower() in names
    field = str(count_field) if _given(count_field) else "COUNT"
    _ensure_fields(in_features, [field], "Integer")
    found, dataset = _las.points(in_las_dataset, "LocateLasPointsByProximity")
    xyz = np.column_stack([found["x"], found["y"], found["z"]])
    xy = shapely.points(found["x"], found["y"])
    nearest = np.full(len(xyz), np.inf)
    groups = []
    read = [names[search_radius.lower()]] if by_field else []
    with da.UpdateCursor(in_features, [*read, field, "SHAPE@"]) as rows:
        for row in rows:
            if row[-1] is None:
                continue
            shape = row[-1]._shape
            if not shapely.has_z(shape):
                raise ExecuteError("ERROR: LocateLasPointsByProximity needs z-enabled features, as documented.")
            radius = float(row[0]) if by_field else _length(search_radius)
            close = np.nonzero(shapely.dwithin(shapely.force_2d(shape), xy, radius))[0]
            d = _distance_3d(xyz[close], shape)
            hit = close[d <= radius]
            nearest[hit] = np.minimum(nearest[hit], d[d <= radius])
            groups.append(hit)
            rows.updateRow([*row[:-2], int(len(hit)), row[-1]])
    within = np.isfinite(nearest)
    if _given(out_features):
        form = _upper(geometry, "MULTIPOINT")
        if form == "POINT":
            columns_out = [_gpkg.Column("DIST3D", "Double")]
            rows_out = [(float(nearest[k]), shapely.Point(*xyz[k])) for k in np.nonzero(within)[0]]
            _write(out_features, "Point", dataset.srid or srid, columns_out, True, rows_out)
        elif form == "MULTIPOINT":
            rows_out = [(shapely.MultiPoint(xyz[g]),) for g in groups if len(g)]
            _write(out_features, "Multipoint", dataset.srid or srid, [], True, rows_out)
        else:
            raise ExecuteError(f"ERROR 000800: geometry is MULTIPOINT or POINT, not '{geometry}'.")
    if _given(class_code):
        code = int(class_code)
        changed = []
        for number, path in enumerate(dataset.file_paths()):
            mine = (found["file"] == number) & within
            if not mine.any():
                continue
            las = _las.read_file(path)
            _las._class_range(las, code, "LocateLasPointsByProximity")
            classes = np.asarray(las.classification, np.int64).copy()
            classes[found["index"][mine]] = code
            las.classification = classes.astype(np.uint8)
            las.write(path)
            changed.append(path)
        _las._update_stats(in_las_dataset, changed, _upper(compute_stats, "COMPUTE_STATS") == "COMPUTE_STATS")
    return Result(_las._lasd_path(in_las_dataset), str(in_features))


# ---- ddd.LocateOutliers


def LocateOutliers(in_surface, out_feature_class, apply_hard_limit="NO_APPLY_HARD_LIMIT", absolute_z_min=0,
                   absolute_z_max=0, apply_comparison_filter="APPLY_COMPARISON_FILTER", z_tolerance=0,
                   slope_tolerance=150, exceed_tolerance_ratio=0.5, outlier_cap=2500):
    """Points of a TIN or LAS dataset whose height is anomalous, written as 3D points with ``REASON`` — 0 the hard limit,
    1 both, 2 the comparison filter — as documented: the hard limit flags a point below ``absolute_z_min`` or above
    ``absolute_z_max``; the comparison filter tests each point against its natural neighbours, and flags it when at least
    ``exceed_tolerance_ratio`` of them exceed the tolerances; no more than ``outlier_cap`` are written.

    INFERRED, the page leaving them open: the natural neighbours are the points joined to it by an edge of the
    Delaunay triangulation (a TIN's edges, or a triangulation of the LAS points the layer shows); a neighbour exceeds the
    tolerances when the height difference is more than ``z_tolerance`` *and* the slope between them, in percent, more
    than ``slope_tolerance``; points are taken in the surface's node order up to the cap; every node of a TIN is a
    measurement, densification points included."""
    from . import _tin
    from ._tintools import _write
    if _tin.exists(in_surface):
        surface = _tin.load(in_surface)
    elif _las.is_las(in_surface):
        surface = _las.surface(in_surface, "LocateOutliers")
    else:
        raise ExecuteError(f"ERROR 000732: {in_surface} is not a TIN or LAS dataset grapy holds (terrains are refused, "
                           "ADR-229 §5.6).")
    nodes = surface.nodes
    hard = np.zeros(len(nodes), bool)
    if _upper(apply_hard_limit, "NO_APPLY_HARD_LIMIT") == "APPLY_HARD_LIMIT":
        hard = (nodes[:, 2] < float(absolute_z_min or 0)) | (nodes[:, 2] > float(absolute_z_max or 0))
    compared = np.zeros(len(nodes), bool)
    if _upper(apply_comparison_filter, "APPLY_COMPARISON_FILTER") == "APPLY_COMPARISON_FILTER":
        edges = np.array(list(surface.all_edges()), np.int64).reshape(-1, 2)
        if len(edges):
            both = np.vstack([edges, edges[:, ::-1]])
            p, q = nodes[both[:, 0]], nodes[both[:, 1]]
            dz = np.abs(p[:, 2] - q[:, 2])
            run = np.hypot(p[:, 0] - q[:, 0], p[:, 1] - q[:, 1])
            with np.errstate(divide="ignore", invalid="ignore"):
                slope = np.where(run > 0, dz / run * 100.0, np.inf)
            exceeds = (dz > float(z_tolerance or 0)) & (slope > float(slope_tolerance if _given(slope_tolerance)
                                                                         else 150))
            total = np.bincount(both[:, 0], minlength=len(nodes))
            over = np.bincount(both[:, 0], weights=exceeds.astype(float), minlength=len(nodes))
            ratio = float(exceed_tolerance_ratio if _given(exceed_tolerance_ratio) else 0.5)
            compared = (total > 0) & (over >= ratio * np.maximum(total, 1))
    reason = np.where(hard & compared, 1, np.where(hard, 0, 2))
    flagged = np.nonzero(hard | compared)[0]
    cap = int(outlier_cap) if _given(outlier_cap) else 2500
    rows = [(int(reason[k]), shapely.Point(*nodes[k])) for k in flagged[:cap]]
    return Result(_write(out_feature_class, "Point", surface.srid, [_gpkg.Column("REASON", "Integer")], True, rows))


# ---- management.InterpolateFromPointCloud


def _options(value):
    from ._tin import rows_of
    out = {"classify": "standard", "lownoise": 0.25, "highnoise": 100.0, "reuseground": 0, "reuselownoise": 0,
           "reusehighnoise": 0}
    for key, val in (r[:2] for r in rows_of(value, 2)):
        if str(key).lower() not in out:
            raise ExecuteError(f"ERROR 000800: '{key}' is not an option of InterpolateFromPointCloud.")
        out[str(key).lower()] = val
    return out


def InterpolateFromPointCloud(in_container, out_raster, cell_size, interpolation_method="TRIANGULATION",
                              smooth_method="NONE", surface_type="DSM", fill_dem=None, options=None):
    """A DSM of every point, or a DTM of the ground points, of LAS files or a LAS dataset, as a raster of ``cell_size``
    cells: TRIANGULATION is "TIN linear", each cell centre on a triangle's plane, as documented; with ``fill_dem`` the
    cells left NoData take the DEM's value there.

    Refused: NATURAL_NEIGHBOR (ADR-229 §5.4); IDW, whose power the page does not give, nor how its "automatically
    computed" radius follows from the density; the GAUSS smoothing filters, whose kernels' widths are not given; a DTM
    that classifies its ground itself (ReuseGround 0) — that classifier is ClassifyLasGround's undescribed one, so run
    ClassifyLasGround (an equivalent) and pass ``ReuseGround 1``; a solution point table, Esri's block-adjustment output.

    INFERRED, the page giving no defaults: TRIANGULATION, NONE and DSM; the withheld points are left out; the grid starts
    at the points' lower-left corner and holds whole cells; a cell outside the triangulation is NoData; the DEM is read
    at the cell centre, unresampled."""
    from . import _tin
    from ._raster import Raster, as_raster
    from .sa import _sample
    from ._tintools import _grid_for
    method = _upper(interpolation_method, "TRIANGULATION")
    if method == "NATURAL_NEIGHBOR":
        _refuse("management", "InterpolateFromPointCloud", "interpolation_method", interpolation_method,
                "natural neighbour over a TIN is not built (ADR-229 §5.4)")
    if method == "IDW":
        _refuse("management", "InterpolateFromPointCloud", "interpolation_method", interpolation_method,
                "the page gives neither IDW's power nor how its radius follows from the point density")
    if method != "TRIANGULATION":
        raise ExecuteError(f"ERROR 000800: '{interpolation_method}' is not an interpolation method.")
    if _upper(smooth_method, "NONE") != "NONE":
        _refuse("management", "InterpolateFromPointCloud", "smooth_method", smooth_method,
                "the page does not give the Gaussian kernels' widths")
    kind = _upper(surface_type, "DSM")
    if kind not in ("DSM", "DTM"):
        raise ExecuteError(f"ERROR 000800: surface_type is DTM or DSM, not '{surface_type}'.")
    settings = _options(options)
    if kind == "DTM" and str(settings["reuseground"]).strip() != "1":
        _refuse("management", "InterpolateFromPointCloud", "options", "ReuseGround 0",
                "a DTM that classifies its own ground uses ClassifyLasGround's classifier, which the pages do not "
                "describe; classify with ClassifyLasGround (an equivalent) and pass ReuseGround 1")
    if not _las.is_las(in_container):
        path = _absolute(in_container)
        if ".gpkg" in path.lower() or (os.path.isfile(path) and not path.lower().endswith((".las", ".laz", ".zlas"))):
            _refuse("management", "InterpolateFromPointCloud", "in_container", in_container,
                    "a solution point table is Esri's block-adjustment output, which grapy does not make")
        files = _las._files_in(in_container, False)
        parts = [_las.arrays(_las.read_file(p)) for p in files]
        a = {k: np.concatenate([p[k] for p in parts]) for k in ("x", "y", "z", "classification", "withheld")} if parts \
            else {k: np.zeros(0) for k in ("x", "y", "z", "classification", "withheld")}
        keep = ~a["withheld"].astype(bool)
        srid = _las.srid_of(_las.read_file(files[0])) if files else 0
    else:
        a, dataset = _las.points(in_container, "InterpolateFromPointCloud")
        keep = np.ones(len(a["x"]), bool)
        srid = dataset.srid
    if kind == "DTM":
        keep &= a["classification"] == 2
    x, y, z = a["x"][keep], a["y"][keep], a["z"][keep]
    if len(x) < 3:
        raise ExecuteError("ERROR: InterpolateFromPointCloud needs at least three points.")
    grid = _grid_for((float(x.min()), float(y.min()), float(x.max()), float(y.max())), "CELLSIZE", cell_size, srid,
                     centred=False)
    surface = _tin.triangulate(list(zip(x, y, z)), [], srid=srid)
    xs, ys = grid.centres()
    gx, gy = np.meshgrid(xs, ys)
    out = surface.z_at(gx.ravel(), gy.ravel()).reshape(gx.shape)
    if _given(fill_dem):
        dem = as_raster(fill_dem, "InterpolateFromPointCloud")
        for r, c in zip(*np.nonzero(np.isnan(out))):
            value = _sample(dem, gx[r, c], gy[r, c], False)
            if value is not None:
                out[r, c] = value
    mask = np.isnan(out)
    return Result(Raster._of(np.where(mask, 0, out).astype(np.float32), mask, grid).save(out_raster))


# ---- ddd.ExtractObjectsFromPointCloud


def _alpha_shape(xy, alpha):
    """The union of the Delaunay triangles whose circumradius is at most ``alpha`` (Edelsbrunner, Kirkpatrick and Seidel
    1983)."""
    from ._tin import _delaunay
    xy = np.unique(xy, axis=0)
    triangles = _delaunay(xy)
    if not len(triangles):
        return shapely.Polygon()
    p, q, r = xy[triangles[:, 0]], xy[triangles[:, 1]], xy[triangles[:, 2]]
    a, b, c = np.linalg.norm(q - r, axis=1), np.linalg.norm(p - r, axis=1), np.linalg.norm(p - q, axis=1)
    twice_area = np.abs((q[:, 0] - p[:, 0]) * (r[:, 1] - p[:, 1]) - (r[:, 0] - p[:, 0]) * (q[:, 1] - p[:, 1]))
    with np.errstate(divide="ignore", invalid="ignore"):
        circumradius = a * b * c / (2 * twice_area)
    keep = twice_area > 0
    keep &= circumradius <= alpha
    if not keep.any():
        return shapely.Polygon()
    rings = np.stack([p[keep], q[keep], r[keep], p[keep]], axis=1)
    return shapely.union_all(shapely.polygons(rings))


def ExtractObjectsFromPointCloud(in_point_cloud, class_codes_to_extract, clustering_distance, out_features,
                                 geometry_type="CONVEX_HULL_3D", min_points=10, vertical_scale_factor=1,
                                 alpha_calculation="ALL_OBJECTS", alpha_radius=None, min_height=None, max_height=None):
    """Objects of a classified LAS dataset: the points of the class codes named — codes sharing a group ID treated as one
    — clustered so that points within ``clustering_distance`` in 3D, z multiplied by ``vertical_scale_factor`` (0 for 2D
    clustering), belong to one object, as documented; objects of fewer than ``min_points`` dropped; written as a
    CENTERPOINT, BOUNDING_CIRCLE, BOUNDING_BOX_2D, CONVEX_HULL_2D or CONCAVE_HULL_2D, the alpha radius the given one or
    the clustering distance, as documented, with ``ObjectCode``, ``COUNT``, ``ALPHA`` (concave hulls) and ``MIN_Z`` and
    ``MAX_Z`` (polygons). ``min_height`` and ``max_height`` rasters keep only the points above, and below, them.

    Refused: the 3D geometry types, the default among them — they write multipatch features, a data model this server
    does not hold yet (ADR-224 group 2); PER_OBJECT alpha — the page does not say how each object's radius is computed.

    INFERRED: a code without a group ID is its own group; the centre point is the mean of the points; BOUNDING_BOX_2D is
    the axis-aligned box; the concave hull is the alpha shape — the union of the Delaunay triangles whose circumradius is
    at most the radius — and an object whose alpha shape is empty is not written; the layer's filters are honoured; a
    point off a height surface is dropped."""
    from scipy.sparse import coo_matrix
    from scipy.sparse.csgraph import connected_components
    from scipy.spatial import cKDTree
    from ._raster import as_raster
    from ._tin import rows_of
    from ._tintools import _write
    form = _upper(geometry_type, "CONVEX_HULL_3D")
    if form in ("BOUNDING_SPHERE", "BOUNDING_BOX_3D", "CONVEX_HULL_3D", "CONCAVE_HULL_3D"):
        _refuse("ddd", "ExtractObjectsFromPointCloud", "geometry_type", form,
                "it writes multipatch features, a data model this server does not hold yet (ADR-224 group 2)")
    if form not in ("CENTERPOINT", "BOUNDING_CIRCLE", "BOUNDING_BOX_2D", "CONVEX_HULL_2D", "CONCAVE_HULL_2D"):
        raise ExecuteError(f"ERROR 000800: '{geometry_type}' is not a geometry type.")
    concave = form == "CONCAVE_HULL_2D"
    if concave and _upper(alpha_calculation, "ALL_OBJECTS") == "PER_OBJECT":
        _refuse("ddd", "ExtractObjectsFromPointCloud", "alpha_calculation", alpha_calculation,
                "the page does not say how each object's alpha radius is computed")
    distance = _length(clustering_distance)
    scale = float(vertical_scale_factor) if _given(vertical_scale_factor) else 1.0
    if not 0.0 <= scale <= 10.0:
        raise ExecuteError("ERROR: vertical_scale_factor ranges from 0.0 to 10.0, as documented.")
    alpha = _length(alpha_radius) if _given(alpha_radius) else distance
    least = int(min_points) if _given(min_points) else 10
    groups = {}
    for code, group in (r[:2] for r in rows_of(class_codes_to_extract, 2)):
        if str(code) == "":
            continue
        groups[int(code)] = int(group) if str(group).strip() not in ("", "#") else int(code)
    if not groups:
        raise ExecuteError("ERROR 000735: ExtractObjectsFromPointCloud needs class codes to extract.")
    found, dataset = _las.points(in_point_cloud, "ExtractObjectsFromPointCloud")
    keep = np.isin(found["classification"], list(groups))
    for surface, above in ((min_height, True), (max_height, False)):
        if _given(surface):
            from .sa import _sample
            raster = as_raster(surface, "ExtractObjectsFromPointCloud")
            heights = np.array([np.nan if (v := _sample(raster, x, y, False)) is None else v
                                for x, y in zip(found["x"], found["y"])])
            with np.errstate(invalid="ignore"):
                keep &= (found["z"] > heights) if above else (found["z"] < heights)
    x, y, z = found["x"][keep], found["y"][keep], found["z"][keep]
    group_of = np.array([groups[int(c)] for c in found["classification"][keep]], np.int64)
    polygon = form != "CENTERPOINT"
    columns = [_gpkg.Column("ObjectCode", "Integer"), _gpkg.Column("COUNT", "Integer")]
    if concave:
        columns.append(_gpkg.Column("ALPHA", "Double"))
    if polygon:
        columns += [_gpkg.Column("MIN_Z", "Double"), _gpkg.Column("MAX_Z", "Double")]
    rows = []
    for g in sorted(set(group_of.tolist())):
        members = np.nonzero(group_of == g)[0]
        pts = np.column_stack([x[members], y[members], z[members] * scale])
        pairs = cKDTree(pts).query_pairs(distance, output_type="ndarray")
        m = len(members)
        graph = coo_matrix((np.ones(len(pairs)), (pairs[:, 0], pairs[:, 1])) if len(pairs) else
                           (np.zeros(0), (np.zeros(0, int), np.zeros(0, int))), shape=(m, m))
        _, label = connected_components(graph, directed=False)
        for k in range(label.max() + 1 if m else 0):
            one = members[label == k]
            if len(one) < least:
                continue
            xy = np.column_stack([x[one], y[one]])
            if form == "CENTERPOINT":
                shape = shapely.Point(float(x[one].mean()), float(y[one].mean()), float(z[one].mean()))
            elif form == "BOUNDING_CIRCLE":
                shape = shapely.minimum_bounding_circle(shapely.multipoints(xy))
            elif form == "BOUNDING_BOX_2D":
                shape = shapely.box(*shapely.bounds(shapely.multipoints(xy)))
            elif form == "CONVEX_HULL_2D":
                shape = shapely.convex_hull(shapely.multipoints(xy))
            else:
                shape = _alpha_shape(xy, alpha)
            if polygon:
                if shape.is_empty or shape.geom_type not in ("Polygon", "MultiPolygon"):
                    continue
                shape = shape if shape.geom_type == "MultiPolygon" else shapely.MultiPolygon([shape])
            values = [g, int(len(one))]
            if concave:
                values.append(float(alpha))
            if polygon:
                values += [float(z[one].min()), float(z[one].max())]
            rows.append((*values, shape))
    kind = "Polygon" if polygon else "Point"
    return Result(_write(out_features, kind, dataset.srid, columns, not polygon, rows))
