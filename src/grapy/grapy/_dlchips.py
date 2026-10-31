"""Deep learning training data, non-maximum suppression and accuracy — ADR-248, the parts that need no framework.

Export Training Data For Deep Learning cuts a raster into image chips and writes a label for each in the metadata format
asked. Two of its formats follow public specifications, and grapy writes them as those specifications define them:

- **PASCAL_VOC_rectangles** — one XML file per chip, the PASCAL Visual Object Classes annotation (Everingham et al. 2010,
  *IJCV* 88(2); the VOC2012 development kit's ``annotation`` element): ``filename``, ``size`` (``width``, ``height``,
  ``depth``) and one ``object`` per sample, its ``name`` the class value, as the tool's page says, and its ``bndbox``
  (``xmin``, ``ymin``, ``xmax``, ``ymax``) in the devkit's 1-based, inclusive pixel coordinates.
- **KITTI_rectangles** — one text file per chip, one object per line in the KITTI object benchmark's fifteen columns
  (Geiger, Lenz and Urtasun 2012; the devkit's ``readme.txt``): type, truncated, occluded, alpha, the box's left, top,
  right and bottom, three dimensions, three location values and rotation_y. As the page says, five are used — the class
  value in the first column and the box in columns 5 to 8, in 0-based pixel coordinates; the other ten are written 0.

The other formats are described by the page in a sentence each, and grapy's layout for them is its own (INFERRED,
ADR-248 §5.3, Q-218):

- **Classified_Tiles** — ``labels/<chip>.tif``, one band, each pixel its class value, 0 where no sample lies. With
  ``in_raster2`` it is the change detection layout the page names: ``images/`` before, ``images2/`` after, ``labels/``
  the change classes.
- **RCNN_Masks** — ``labels/<chip>.tif`` of two bands: the class value, and each sample's instance number in the chip
  (1, 2, …; 0 elsewhere), so that a mask per instance can be read back.
- **Labeled_Tiles** and **Imagenet** — one chip per sample, centred on it (``FIXED_SIZE``) or spanning its bounding box
  (``BOUNDING_BOX``); ``labels/<chip>.xml`` a PASCAL VOC annotation holding the one object. From a classified raster,
  each chip is labelled with the class of most of its cells.
- **MultiLabeled_Tiles** — PASCAL VOC annotations naming every class present in the chip, one object per class, its box
  that of the class's samples together.
- **Export_Tiles** and **CycleGAN** — ``images/`` and, with ``in_raster2``, ``images2/``: for Export_Tiles the same
  footprint in both, for CycleGAN each raster cut on its own grid, as the page says CycleGAN's images need not overlap.
- **Panoptic_Segmentation** — ``labels/`` as Classified_Tiles from ``in_class_data`` and ``instance_labels/`` of two
  bands, each pixel's instance number and its class, from ``in_instance_data`` — the page's *two output labels folders*.

Every export also writes ``map.txt`` (one line per chip, the image's path then its label's, relative to the folder) and
``grapy_training.json``: ``{"format": "grapy-training", "version": 1, "metadata_format", "image_chip_format",
"tile_size": [x, y], "bands", "cell_size": [w, h], "srid", "reference_system", "classes": [{"value", "name", "count"}],
"statistics": {"min", "max", "mean", "std"} per band, "chips"}``, which grapy's training tools read.
"""
from __future__ import annotations

import json
import math
import os
import xml.etree.ElementTree as ElementTree

import numpy as np
import shapely
import shapely.affinity

from ._runtime import ExecuteError, NotBuiltError, _is_given, env

FORMATS = ("KITTI_rectangles", "PASCAL_VOC_rectangles", "Classified_Tiles", "RCNN_Masks", "Labeled_Tiles",
           "MultiLabeled_Tiles", "Export_Tiles", "CycleGAN", "Imagenet", "Panoptic_Segmentation")
RECTANGLES = ("KITTI_rectangles", "PASCAL_VOC_rectangles")
CHIP_FORMATS = {"TIFF": ("GTiff", ".tif"), "PNG": ("PNG", ".png"), "JPEG": ("JPEG", ".jpg"), "MRF": ("MRF", ".mrf")}
MANIFEST = "grapy_training.json"


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _path(value):
    path = os.fspath(value)
    if not os.path.isabs(path) and env.workspace:
        path = os.path.join(env.workspace, path)
    return path


def _word(value, default, allowed, tool, parameter):
    if not _is_given(value):
        return default
    if isinstance(value, bool):
        return allowed[0] if value else allowed[1]
    text = str(value).strip()
    for each in allowed:
        if each.upper() == text.upper():
            return each
    raise ValueError(f"{tool}: {parameter} must be one of {', '.join(allowed)}; got '{value}'.")


# ---- the inputs: rasters, samples


def _is_features(value):
    if not isinstance(value, (str, os.PathLike)):
        return False
    text = os.fspath(value).lower()
    if ".gpkg" not in text:
        from . import _layers
        return _layers.find(os.fspath(value)) is not None
    from . import _mosaic
    return not _mosaic.exists(value)


def rasters(in_raster, processing_mode, tool):
    """[(name, Raster)]: one raster, every image of a folder, or a mosaic dataset as one image or item by item."""
    from . import _mosaic
    from ._raster import Raster, _open_bands
    mode = _word(processing_mode, "PROCESS_AS_MOSAICKED_IMAGE",
                 ("PROCESS_AS_MOSAICKED_IMAGE", "PROCESS_ITEMS_SEPARATELY"), tool, "processing_mode")
    if isinstance(in_raster, (str, os.PathLike)):
        path = _path(in_raster)
        if os.path.isdir(path) and ".gpkg" not in path.lower():
            found = sorted(f for f in os.listdir(path)
                           if f.lower().endswith((".tif", ".tiff", ".png", ".jpg", ".jpeg", ".mrf")))
            if not found:
                raise ExecuteError(f"ERROR: {tool}: the folder {in_raster} holds no image.")
            return [(os.path.splitext(f)[0], Raster(os.path.join(path, f))) for f in found]
        if _mosaic.exists(in_raster) and mode == "PROCESS_ITEMS_SEPARATELY":
            connection, name, _, view = _mosaic.open_mosaic(in_raster, tool)
            try:
                listed = _mosaic.items(connection, name, view.where if view is not None else None)
            finally:
                connection.close()
            return [(str(i.get("Name") or i["OBJECTID"]), Raster(i["Raster"])) for i in listed if i.get("Raster")]
    raster = _open_bands(in_raster, tool)
    return [(os.path.splitext(os.path.basename(raster.catalogPath))[0] if raster.catalogPath else "raster", raster)]


def _class_field(columns, given, tool, parameter="class_value_field"):
    names = {c.name.lower(): c.name for c in columns}
    if _is_given(given):
        if str(given).lower() not in names:
            raise ValueError(f"{tool}: {parameter} '{given}' is not a field of the input.")
        return names[str(given).lower()]
    for each in ("classvalue", "value"):
        if each in names:
            return names[each]
    return None


def samples(in_class_data, class_value_field, buffer_radius, srid, tool, parameter="class_value_field"):
    """The training samples of a feature class as [(shapely shape in ``srid``, class value)], and {value: name}. With no
    class field every record is one class, 1 (INFERRED: the page says they are one class but not its value); a point
    is a circle of ``buffer_radius``; names come from a ``classname`` field when there is one, else the value."""
    from .analysis import _distance, _read, _schema
    columns = _schema(in_class_data)[0]
    field = _class_field(columns, class_value_field, tool, parameter)
    names_field = {c.name.lower(): c.name for c in columns}.get("classname")
    order = [c.name for c in columns]
    radius = _distance(buffer_radius, srid) if _is_given(buffer_radius) else None
    found, names = [], {}
    for _, values, shape in _read(in_class_data, srid):
        if shape is None or shape.is_empty:
            continue
        value = 1 if field is None else values[order.index(field)]
        if value is None:
            continue
        value = int(value)
        if shape.geom_type in ("Point", "MultiPoint") and radius:
            shape = shape.buffer(radius)
        found.append((shape, value))
        if names_field is not None and values[order.index(names_field)] is not None:
            names.setdefault(value, str(values[order.index(names_field)]))
        names.setdefault(value, str(value))
    return found, names


# ---- a chip: a window of the raster, possibly rotated about its centre


class Chip:
    """A chip of ``columns`` × ``rows`` pixels of ``cw`` × ``ch`` map units, centred on (cx, cy) and turned ``angle``
    degrees anticlockwise. ``affine`` takes a chip pixel (column, row), from its corner, to map coordinates."""

    def __init__(self, cx, cy, columns, rows, cw, ch, angle=0.0):
        from affine import Affine
        self.cx, self.cy, self.columns, self.rows, self.cw, self.ch = cx, cy, int(columns), int(rows), cw, ch
        self.angle = float(angle)
        t = math.radians(self.angle)
        c, s = math.cos(t), math.sin(t)
        self.affine = Affine(c * cw, s * ch, cx - c * cw * columns / 2 - s * ch * rows / 2,
                             s * cw, -c * ch, cy - s * cw * columns / 2 + c * ch * rows / 2)
        self.inverse = ~self.affine

    def footprint(self):
        corners = [self.affine * (0, 0), self.affine * (self.columns, 0), self.affine * (self.columns, self.rows),
                   self.affine * (0, self.rows)]
        return shapely.Polygon(corners)

    def to_pixels(self, shape):
        i = self.inverse
        return shapely.affinity.affine_transform(shape, [i.a, i.b, i.d, i.e, i.c, i.f])

    def sample(self, raster):
        """Every band of the raster at the chip's pixel centres, nearest cell, and a mask of the pixels outside it or
        on NoData (INFERRED: nearest neighbour, so an unturned chip on the grid copies its cells exactly)."""
        g = raster._grid
        jj, ii = np.mgrid[0:self.rows, 0:self.columns]
        a = self.affine
        x = a.a * (ii + 0.5) + a.b * (jj + 0.5) + a.c
        y = a.d * (ii + 0.5) + a.e * (jj + 0.5) + a.f
        col = np.floor((x - g.left) / g.width + 1e-9).astype(np.int64)
        row = np.floor((g.top - y) / g.height + 1e-9).astype(np.int64)
        inside = (col >= 0) & (col < g.columns) & (row >= 0) & (row < g.rows)
        cc, rr = np.clip(col, 0, g.columns - 1), np.clip(row, 0, g.rows - 1)
        arrays, mask = [], ~inside
        for band, band_mask in zip(raster._arrays, raster._masks):
            arrays.append(np.where(inside, np.asarray(band)[rr, cc], 0))
            mask = mask | (np.asarray(band_mask)[rr, cc] & inside)
        return arrays, mask


def windows(grid, size_x, size_y, stride_x, stride_y, angles):
    """The chips of a sliding window over a grid, from its top-left corner, whole windows only (INFERRED: a window
    running past the raster's edge is not cut), each at every angle."""
    out = []
    for row0 in range(0, grid.rows - size_y + 1, stride_y):
        for col0 in range(0, grid.columns - size_x + 1, stride_x):
            cx = grid.left + (col0 + size_x / 2) * grid.width
            cy = grid.top - (row0 + size_y / 2) * grid.height
            out.extend(Chip(cx, cy, size_x, size_y, grid.width, grid.height, a) for a in angles)
    return out


def angles_of(rotation_angle):
    """0 and each further multiple of ``rotation_angle`` short of a full turn, as the page counts them: 45 gives eight."""
    step = float(rotation_angle) if _is_given(rotation_angle) else 0.0
    if step <= 0:
        return [0.0]
    count = int(math.ceil(360.0 / step - 1e-9))
    return [k * step for k in range(count)]


# ---- writing chips and labels


def write_image(path, arrays, chip, srid, driver, dtype=None):
    import rasterio
    dtype = np.dtype(dtype or np.result_type(*[a.dtype for a in arrays]))
    if driver in ("PNG", "JPEG"):
        if len(arrays) > 3:
            raise ValueError(f"PNG and JPEG chips hold at most three bands, as the page says; the raster has {len(arrays)}.")
        allowed = (np.uint8,) if driver == "JPEG" else (np.uint8, np.uint16)
        if dtype.type not in allowed:
            low = min(float(np.min(a)) for a in arrays) if arrays[0].size else 0
            high = max(float(np.max(a)) for a in arrays) if arrays[0].size else 0
            if low < 0 or high > 255:
                raise ValueError(f"{driver} chips are 8-bit and the raster holds values from {low} to {high}; write TIFF "
                                 "or MRF chips.")
            dtype = np.dtype(np.uint8)
    profile = {"driver": driver, "height": chip.rows, "width": chip.columns, "count": len(arrays), "dtype": dtype.name,
               "transform": chip.affine}
    if srid:
        profile["crs"] = f"EPSG:{srid}"
    with rasterio.open(path, "w", **profile) as out:
        for k, a in enumerate(arrays, 1):
            out.write(np.asarray(a).astype(dtype), k)


def _voc(path, image_name, chip, depth, objects):
    """A PASCAL VOC annotation; ``objects`` are (class value, (x0, y0, x1, y1)) in continuous pixel coordinates."""
    root = ElementTree.Element("annotation")
    ElementTree.SubElement(root, "filename").text = image_name
    size = ElementTree.SubElement(root, "size")
    for tag, value in (("width", chip.columns), ("height", chip.rows), ("depth", depth)):
        ElementTree.SubElement(size, tag).text = str(value)
    ElementTree.SubElement(root, "segmented").text = "0"
    for value, (x0, y0, x1, y1) in objects:
        obj = ElementTree.SubElement(root, "object")
        ElementTree.SubElement(obj, "name").text = str(value)
        ElementTree.SubElement(obj, "difficult").text = "0"
        box = ElementTree.SubElement(obj, "bndbox")
        for tag, v in (("xmin", int(math.floor(x0)) + 1), ("ymin", int(math.floor(y0)) + 1),
                       ("xmax", int(math.ceil(x1))), ("ymax", int(math.ceil(y1)))):
            ElementTree.SubElement(box, tag).text = str(v)
    ElementTree.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


def read_voc(path):
    """[(class value or name, (x0, y0, x1, y1))] of a PASCAL VOC annotation, in continuous 0-based coordinates."""
    root = ElementTree.parse(path).getroot()
    out = []
    for obj in root.iter("object"):
        name = obj.findtext("name")
        box = obj.find("bndbox")
        x0, y0 = float(box.findtext("xmin")) - 1, float(box.findtext("ymin")) - 1
        x1, y1 = float(box.findtext("xmax")), float(box.findtext("ymax"))
        out.append((_number(name), (x0, y0, x1, y1)))
    return out


def _kitti(path, objects):
    with open(path, "w", encoding="utf-8") as handle:
        for value, (x0, y0, x1, y1) in objects:
            handle.write(f"{value} 0.00 0 0.00 {x0:.2f} {y0:.2f} {x1:.2f} {y1:.2f} 0.00 0.00 0.00 0.00 0.00 0.00 0.00\n")


def read_kitti(path):
    out = []
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            parts = line.split()
            if len(parts) >= 8:
                out.append((_number(parts[0]), tuple(float(p) for p in parts[4:8])))
    return out


def _number(text):
    try:
        number = float(text)
        return int(number) if number.is_integer() else number
    except (TypeError, ValueError):
        return text


def _burn(shapes, chip, dtype="int32"):
    """Class values of shapes, already in chip pixel coordinates, burnt onto the chip: a polygon the pixels whose centres
    it holds, a line or point every pixel it touches, a later shape over an earlier (INFERRED)."""
    from affine import Affine
    from rasterio.features import rasterize
    out = np.zeros((chip.rows, chip.columns), dtype)
    polygons = [(s, v) for s, v in shapes if s.geom_type.endswith("Polygon") and not s.is_empty]
    others = [(s, v) for s, v in shapes if not s.geom_type.endswith("Polygon") and not s.is_empty]
    for group, touched in ((polygons, False), (others, True)):
        if group:
            burnt = rasterize(group, out_shape=out.shape, transform=Affine.identity(), fill=0, dtype=dtype,
                              all_touched=touched)
            out = np.where(burnt != 0, burnt, out)
    return out


def _objects(found, chip, ratio):
    """The samples on a chip as (class value, clipped box in chip pixels, pixel shape), keeping those with at least
    ``ratio`` of their area (of their length, for a line) on it; a point with no radius is kept as a point."""
    footprint = chip.footprint()
    whole = shapely.box(0, 0, chip.columns, chip.rows)
    out = []
    for shape, value in found:
        if not shape.intersects(footprint):
            continue
        part = shape.intersection(footprint)
        if not shape.geom_type.endswith("Point") and (part.area if shape.area > 0 else part.length) <= 0:
            continue   # a polygon or line that only touches the chip's edge is not on it
        measure = shape.area if shape.area > 0 else shape.length
        kept = (part.area if shape.area > 0 else part.length) / measure if measure > 0 else 1.0
        if ratio and kept + 1e-12 < ratio:
            continue
        pixels = chip.to_pixels(part if not part.is_empty else shape).intersection(whole)
        if pixels.is_empty:
            continue
        out.append((value, pixels.bounds, pixels))
    return out


# ---- Export Training Data For Deep Learning


def ExportTrainingDataForDeepLearning(in_raster, out_folder, in_class_data, image_chip_format, tile_size_x=256,
                                      tile_size_y=256, stride_x=128, stride_y=128,
                                      output_nofeature_tiles="ONLY_TILES_WITH_FEATURES",
                                      metadata_format="PASCAL_VOC_rectangles", start_index=0, class_value_field=None,
                                      buffer_radius=0, in_mask_polygons=None, rotation_angle=0,
                                      reference_system="MAP_SPACE", processing_mode="PROCESS_AS_MOSAICKED_IMAGE",
                                      blacken_around_feature="NO_BLACKEN", crop_mode="FIXED_SIZE", in_raster2=None,
                                      in_instance_data=None, instance_class_value_field=None,
                                      min_polygon_overlap_ratio=0):
    """Image chips and their labels, in the metadata format asked — see the module's docstring for each format's files.

    ``in_class_data`` is a feature class (a ``classvalue`` or ``value`` field, or ``class_value_field``; with none,
    one class) or a classified raster (integer, 0 and NoData no class). Chips are cut by a sliding window of
    ``tile_size`` stepping ``stride`` from the raster's top-left corner; with ``rotation_angle`` each window is also cut
    turned by every multiple of the angle about its centre. ``ONLY_TILES_WITH_FEATURES`` keeps the chips a sample
    falls on, ``in_mask_polygons`` those wholly inside its polygons, and ``min_polygon_overlap_ratio`` counts a
    sample on a chip only when that share of it lies there. Labeled_Tiles and Imagenet cut a chip per sample, centred
    on it or spanning its box (``crop_mode``), black outside it with ``BLACKEN_AROUND_FEATURE``.

    INFERRED (ADR-248 §5.3): tile size 256 and stride 128 by default, the values of the page's examples; NoData cells
    are written 0; ``PIXEL_SPACE`` cuts the same chips as ``MAP_SPACE`` on a north-up raster, whose pixel and map
    spaces differ by its grid alone, and is recorded in the manifest. ``start_index`` is deprecated, as the page says,
    and only 0 is taken."""
    from ._raster import Raster, _open_bands
    from ._result import Result
    tool = "ExportTrainingDataForDeepLearning"
    fmt = _word(metadata_format, "PASCAL_VOC_rectangles", FORMATS, tool, "metadata_format")
    chip_format = _word(image_chip_format, "TIFF", tuple(CHIP_FORMATS), tool, "image_chip_format")
    driver, extension = CHIP_FORMATS[chip_format]
    keep_all = _word(output_nofeature_tiles, "ONLY_TILES_WITH_FEATURES", ("ALL_TILES", "ONLY_TILES_WITH_FEATURES"),
                     tool, "output_nofeature_tiles") == "ALL_TILES"
    reference = _word(reference_system, "MAP_SPACE", ("MAP_SPACE", "PIXEL_SPACE"), tool, "reference_system")
    blacken = _word(blacken_around_feature, "NO_BLACKEN", ("BLACKEN_AROUND_FEATURE", "NO_BLACKEN"), tool,
                    "blacken_around_feature") == "BLACKEN_AROUND_FEATURE"
    crop = _word(crop_mode, "FIXED_SIZE", ("FIXED_SIZE", "BOUNDING_BOX"), tool, "crop_mode")
    if _is_given(start_index) and int(start_index) != 0:
        _refuse(tool, "start_index", start_index, "the parameter is deprecated, as its page says, and only 0 is taken")
    if blacken and fmt != "Labeled_Tiles":
        raise ValueError(f"{tool}: BLACKEN_AROUND_FEATURE applies to Labeled_Tiles only, as the page says.")
    if _is_given(in_raster2) and fmt not in ("Classified_Tiles", "Export_Tiles", "CycleGAN"):
        raise ValueError(f"{tool}: in_raster2 applies to Classified_Tiles, Export_Tiles and CycleGAN only, as the page says.")
    if fmt == "CycleGAN" and not _is_given(in_raster2):
        raise ValueError(f"{tool}: CycleGAN needs in_raster2, the second domain's imagery.")
    if _is_given(in_instance_data) != (fmt == "Panoptic_Segmentation"):
        raise ValueError(f"{tool}: in_instance_data is given with Panoptic_Segmentation, and only with it, as the page says.")
    size_x, size_y = int(tile_size_x or 256), int(tile_size_y or 256)
    step_x, step_y = int(stride_x or size_x), int(stride_y or size_y)
    if min(size_x, size_y, step_x, step_y) <= 0:
        raise ValueError(f"{tool}: tile sizes and strides are positive.")
    ratio = float(min_polygon_overlap_ratio or 0)
    angles = angles_of(rotation_angle)

    sources = rasters(in_raster, processing_mode, tool)
    second = _open_bands(in_raster2, tool) if _is_given(in_raster2) else None
    folder = _path(out_folder)
    unlabelled = fmt in ("Export_Tiles", "CycleGAN")
    for sub in ("images",) + (() if unlabelled else ("labels",)) + (("images2",) if second is not None else ()) + (
            ("instance_labels",) if fmt == "Panoptic_Segmentation" else ()):
        os.makedirs(os.path.join(folder, sub), exist_ok=True)

    first = sources[0][1]
    srid = first._grid.srid
    class_raster = None
    if isinstance(in_class_data, Raster) or (isinstance(in_class_data, (str, os.PathLike)) and not _is_features(in_class_data)):
        class_raster = _open_bands(in_class_data, tool)
        if len(class_raster._arrays) != 1 or not np.issubdtype(np.asarray(class_raster._arrays[0]).dtype, np.integer):
            raise ValueError(f"{tool}: a classified raster for in_class_data is one band of integers.")
        found, names = [], {}
        values = np.unique(np.asarray(class_raster._arrays[0])[~class_raster._masks[0]])
        names = {int(v): str(int(v)) for v in values if int(v) != 0}
        if fmt in RECTANGLES or fmt in ("RCNN_Masks", "MultiLabeled_Tiles"):
            found = _regions(class_raster)
    else:
        found, names = samples(in_class_data, class_value_field, buffer_radius, srid, tool)
    instances = []
    if fmt == "Panoptic_Segmentation":
        instances, more = samples(in_instance_data, instance_class_value_field, buffer_radius, srid, tool,
                                  "instance_class_value_field")
        for k, v in more.items():
            names.setdefault(k, v)
    mask_area = None
    if _is_given(in_mask_polygons):
        from .analysis import _read
        mask_area = shapely.union_all([s for _, _, s in _read(in_mask_polygons, srid)])

    manifest_rows, counts, stats = [], {}, _Stats()
    index = 0
    for source_name, raster in sources:
        per_feature = fmt in ("Labeled_Tiles", "Imagenet") and class_raster is None
        if per_feature:
            chips = []
            for shape, value in found:
                chips.extend((c, (shape, value)) for c in _feature_chips(shape, raster._grid, size_x, size_y, crop, angles))
        else:
            chips = [(c, None) for c in windows(raster._grid, size_x, size_y, step_x, step_y, angles)]
        second_chips = []
        if fmt == "CycleGAN":
            second_chips = windows(second._grid, size_x, size_y, step_x, step_y, angles)
        for chip, owner in chips:
            footprint = chip.footprint()
            if mask_area is not None and not mask_area.buffer(1e-9 * max(chip.cw, chip.ch)).contains(footprint):
                continue
            arrays, nodata = chip.sample(raster)
            name = f"{index:09d}"
            image_name = f"{name}{extension}"
            label_path = None
            objects, label_array, chosen = [], None, []
            if owner is not None:
                shape, value = owner
                objects = [(value, chip.to_pixels(shape.intersection(footprint) if shape.area > 0 else shape).intersection(
                    shapely.box(0, 0, chip.columns, chip.rows)).bounds)]
                chosen = [value]
                if blacken:
                    inside = _burn([(chip.to_pixels(shape), 1)], chip, "uint8") != 0
                    arrays = [np.where(inside, a, 0) for a in arrays]
            elif class_raster is not None and fmt in ("Classified_Tiles", "Labeled_Tiles", "Imagenet", "Export_Tiles",
                                                      "CycleGAN", "Panoptic_Segmentation"):
                label_array = _class_chip(chip, class_raster)
                chosen = sorted(int(v) for v in np.unique(label_array) if v != 0)
            elif class_raster is not None:
                objects = [(v, b) for v, b, _ in _objects(found, chip, 0)]
                chosen = sorted({v for v, _ in objects})
            else:
                kept = _objects(found, chip, ratio)
                objects = [(v, b) for v, b, _ in kept]
                chosen = sorted({v for v, _ in objects})
                if fmt in ("Classified_Tiles", "RCNN_Masks", "Panoptic_Segmentation", "Export_Tiles", "CycleGAN"):
                    label_array = _burn([(s, v) for v, _, s in kept], chip)
                    chosen = sorted(int(v) for v in np.unique(label_array) if v != 0) or chosen
            if not keep_all and not chosen:
                continue
            arrays = [np.where(nodata, 0, a) for a in arrays]
            write_image(os.path.join(folder, "images", image_name), arrays, chip, srid, driver,
                        np.result_type(*[np.asarray(b).dtype for b in raster._arrays]))
            stats.add(arrays)
            if second is not None and fmt != "CycleGAN":
                more, more_nodata = chip.sample(second)
                write_image(os.path.join(folder, "images2", image_name), [np.where(more_nodata, 0, a) for a in more],
                            chip, srid, driver, np.result_type(*[np.asarray(b).dtype for b in second._arrays]))
            if fmt == "PASCAL_VOC_rectangles" or fmt in ("Labeled_Tiles", "Imagenet") or fmt == "MultiLabeled_Tiles":
                label_path = f"labels/{name}.xml"
                if fmt in ("Labeled_Tiles", "Imagenet") and label_array is not None:
                    values, cells = np.unique(label_array[label_array != 0], return_counts=True)
                    top = int(values[np.argmax(cells)]) if values.size else 0
                    objects, chosen = ([(top, (0, 0, chip.columns, chip.rows))], [top]) if top else ([], [])
                if fmt == "MultiLabeled_Tiles":
                    objects = _per_class(objects)
                _voc(os.path.join(folder, label_path), image_name, chip, len(arrays), objects)
            elif fmt == "KITTI_rectangles":
                label_path = f"labels/{name}.txt"
                _kitti(os.path.join(folder, label_path), objects)
            elif fmt in ("Classified_Tiles", "Panoptic_Segmentation"):
                label_path = f"labels/{name}.tif"
                if label_array is None:
                    label_array = np.zeros((chip.rows, chip.columns), np.int32)
                write_image(os.path.join(folder, label_path), [label_array.astype(np.int32)], chip, srid, "GTiff")
                if fmt == "Panoptic_Segmentation":
                    kept = _objects(instances, chip, ratio)
                    ids = _burn([(s, k) for k, (_, _, s) in enumerate(kept, 1)], chip)
                    classes = _burn([(s, v) for v, _, s in kept], chip)
                    write_image(os.path.join(folder, "instance_labels", f"{name}.tif"), [ids, classes], chip, srid, "GTiff")
                    chosen = sorted(set(chosen) | {v for v, _, _ in kept})
            elif fmt == "RCNN_Masks":
                label_path = f"labels/{name}.tif"
                kept = _objects(found, chip, ratio) if class_raster is None else _objects(found, chip, 0)
                ids = _burn([(s, k) for k, (_, _, s) in enumerate(kept, 1)], chip)
                classes = _burn([(s, v) for v, _, s in kept], chip)
                write_image(os.path.join(folder, label_path), [classes, ids], chip, srid, "GTiff")
            for v in chosen:
                counts[v] = counts.get(v, 0) + 1
            manifest_rows.append(f"images/{image_name}" + (f" {label_path}" if label_path else ""))
            index += 1
        for chip in second_chips:
            more, more_nodata = chip.sample(second)
            write_image(os.path.join(folder, "images2", f"{index:09d}{extension}"),
                        [np.where(more_nodata, 0, a) for a in more], chip, second._grid.srid, driver,
                        np.result_type(*[np.asarray(b).dtype for b in second._arrays]))
            index += 1
    if not manifest_rows:
        from ._runtime import AddWarning
        AddWarning(f"{tool}: no chip was written — no window of the raster holds a training sample.")
    with open(os.path.join(folder, "map.txt"), "w", encoding="utf-8", newline="\n") as handle:
        handle.write("".join(r + "\n" for r in manifest_rows))
    manifest = {"format": "grapy-training", "version": 1, "metadata_format": fmt, "image_chip_format": chip_format,
                "tile_size": [size_x, size_y], "bands": len(first._arrays),
                "cell_size": [first._grid.width, first._grid.height], "srid": srid, "reference_system": reference,
                "classes": [{"value": int(v), "name": names.get(v, str(v)), "count": int(counts.get(v, 0))}
                            for v in sorted(names)],
                "statistics": stats.result(), "chips": len(manifest_rows), "change_detection":
                    bool(second is not None and fmt == "Classified_Tiles")}
    with open(os.path.join(folder, MANIFEST), "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, indent=1)
    return Result(folder)


def _feature_chips(shape, grid, size_x, size_y, crop, angles):
    if crop == "BOUNDING_BOX":
        x0, y0, x1, y1 = shape.bounds
        cw = max(x1 - x0, grid.width) / size_x
        ch = max(y1 - y0, grid.height) / size_y
        return [Chip((x0 + x1) / 2, (y0 + y1) / 2, size_x, size_y, cw, ch, a) for a in angles]
    centre = shape.centroid
    return [Chip(centre.x, centre.y, size_x, size_y, grid.width, grid.height, a) for a in angles]


def _class_chip(chip, class_raster):
    arrays, mask = chip.sample(class_raster)
    return np.where(mask, 0, arrays[0]).astype(np.int32)


def _regions(class_raster):
    """A classified raster's samples as polygons: each 8-connected region of one class value (INFERRED), as the cells'
    union."""
    from rasterio.features import shapes
    array = np.asarray(class_raster._arrays[0]).astype(np.int32)
    valid = (~class_raster._masks[0]) & (array != 0)
    from scipy import ndimage
    out = []
    for value in np.unique(array[valid]):
        labelled, count = ndimage.label(valid & (array == value), structure=np.ones((3, 3)))
        for k in range(1, count + 1):
            pieces = [shapely.geometry.shape(g) for g, v in shapes(labelled.astype(np.int32), mask=labelled == k,
                                                                   transform=class_raster._grid.transform()) if v == k]
            out.append((shapely.union_all(pieces), int(value)))
    return out


def _per_class(objects):
    merged = {}
    for value, (x0, y0, x1, y1) in objects:
        if value in merged:
            a = merged[value]
            merged[value] = (min(a[0], x0), min(a[1], y0), max(a[2], x1), max(a[3], y1))
        else:
            merged[value] = (x0, y0, x1, y1)
    return sorted(merged.items())


class _Stats:
    def __init__(self):
        self.n, self.low, self.high, self.total, self.squares = 0, None, None, None, None

    def add(self, arrays):
        data = np.stack([np.asarray(a, np.float64).ravel() for a in arrays])
        low, high = data.min(axis=1), data.max(axis=1)
        self.low = low if self.low is None else np.minimum(self.low, low)
        self.high = high if self.high is None else np.maximum(self.high, high)
        self.total = data.sum(axis=1) if self.total is None else self.total + data.sum(axis=1)
        self.squares = (data ** 2).sum(axis=1) if self.squares is None else self.squares + (data ** 2).sum(axis=1)
        self.n += data.shape[1]

    def result(self):
        if not self.n:
            return {}
        mean = self.total / self.n
        std = np.sqrt(np.maximum(self.squares / self.n - mean ** 2, 0))
        return {"min": self.low.tolist(), "max": self.high.tolist(), "mean": mean.tolist(), "std": std.tolist()}


def read_training(folder, tool):
    """(manifest, [(image path, label path or None, second image path or None)]) of a folder grapy exported."""
    folder = _path(folder)
    manifest_path = os.path.join(folder, MANIFEST)
    if not os.path.isfile(manifest_path):
        raise NotBuiltError(f"{tool}: '{folder}' is not training data grapy's Export Training Data For Deep Learning "
                            "wrote (no grapy_training.json); a folder exported by ArcGIS Pro carries statistics and "
                            "model files in layouts Esri does not publish.")
    with open(manifest_path, encoding="utf-8") as handle:
        manifest = json.load(handle)
    rows = []
    with open(os.path.join(folder, "map.txt"), encoding="utf-8") as handle:
        for line in handle:
            parts = line.split()
            if not parts:
                continue
            image = os.path.join(folder, parts[0])
            label = os.path.join(folder, parts[1]) if len(parts) > 1 else None
            second = os.path.join(folder, parts[0].replace("images/", "images2/", 1))
            rows.append((image, label, second if os.path.isfile(second) else None))
    if manifest.get("metadata_format") == "CycleGAN":
        others = sorted(os.listdir(os.path.join(folder, "images2")))
        manifest["domain_b"] = [os.path.join(folder, "images2", f) for f in others]
    return manifest, rows


# ---- Non Maximum Suppression


def iou(a, b):
    """Intersection over union of two shapes' areas."""
    if a.is_empty or b.is_empty:
        return 0.0
    union = a.union(b).area
    return a.intersection(b).area / union if union > 0 else 0.0


def greedy_nms(shapes, scores, max_overlap_ratio, classes=None):
    """The indexes kept by greedy non-maximum suppression (Felzenszwalb et al. 2010, *IEEE TPAMI* 32(9); Girshick et
    al. 2014, *CVPR*): in descending score, a shape is dropped when its intersection over union with one already kept
    exceeds ``max_overlap_ratio``. Equal scores keep the earlier shape (INFERRED). With ``classes``, a shape is only
    compared with shapes of its own class."""
    from shapely.strtree import STRtree
    order = sorted(range(len(shapes)), key=lambda i: (-float(scores[i]), i))
    tree = STRtree(shapes)
    kept, gone = [], set()
    threshold = float(max_overlap_ratio)
    for i in order:
        if i in gone:
            continue
        kept.append(i)
        for j in tree.query(shapes[i]):
            j = int(j)
            if j == i or j in gone or (classes is not None and classes[j] != classes[i]):
                continue
            if (-float(scores[j]), j) < (-float(scores[i]), i):
                continue
            if iou(shapes[i], shapes[j]) > threshold:
                gone.add(j)
    return sorted(kept)


def NonMaximumSuppression(in_featureclass, confidence_score_field, out_featureclass, class_value_field=None,
                          max_overlap_ratio=0):
    """Duplicates removed: where two features overlap with an intersection over union above ``max_overlap_ratio``, the
    one of lower confidence goes, as the page says — first among the features of each class, then across classes
    (its two passes). The class field is ``class_value_field``, else ``Classvalue`` or ``Value``; with none, one class.
    Greedy in descending confidence (Felzenszwalb et al. 2010; Girshick et al. 2014)."""
    from . import management
    from ._result import Result
    from .analysis import _out, _read, _schema
    tool = "NonMaximumSuppression"
    columns = _schema(in_featureclass)[0]
    names = {c.name.lower(): c.name for c in columns}
    if str(confidence_score_field).lower() not in names:
        raise ValueError(f"{tool}: '{confidence_score_field}' is not a field of {in_featureclass}.")
    score_field = names[str(confidence_score_field).lower()]
    class_field = _class_field(columns, class_value_field, tool)
    order = [c.name for c in columns]
    rows = _read(in_featureclass)
    oids = [r[0] for r in rows]
    shapes = [r[2] for r in rows]
    scores = [r[1][order.index(score_field)] or 0.0 for r in rows]
    classes = [r[1][order.index(class_field)] if class_field else 0 for r in rows]
    first = greedy_nms(shapes, scores, max_overlap_ratio or 0, classes)
    second = greedy_nms([shapes[i] for i in first], [scores[i] for i in first], max_overlap_ratio or 0)
    keep = {oids[first[k]] for k in second}
    out = _out(out_featureclass)
    management.CopyFeatures(in_featureclass, out)
    from . import da
    with da.UpdateCursor(out, ["OID@"]) as cursor:
        for (oid,) in cursor:
            if oid not in keep:
                cursor.deleteRow()
    return Result(out)


# ---- Compute Accuracy For Object Detection


def average_precision(scores, matched, positives):
    """PASCAL VOC's average precision from VOC2010 on (Everingham et al. 2010; 2015, *IJCV* 111(1), §4.2): the area
    under the precision–recall curve with precision made monotone (each point the maximum precision at any higher
    recall), summed over every recall step."""
    if positives == 0:
        return 0.0
    order = np.argsort(-np.asarray(scores, float), kind="stable")
    tp = np.asarray(matched, float)[order]
    fp = 1.0 - tp
    tp, fp = np.cumsum(tp), np.cumsum(fp)
    recall = tp / positives
    precision = tp / np.maximum(tp + fp, np.finfo(float).eps)
    mrec = np.concatenate([[0.0], recall, [1.0]])
    mpre = np.concatenate([[0.0], precision, [0.0]])
    for i in range(len(mpre) - 2, -1, -1):
        mpre[i] = max(mpre[i], mpre[i + 1])
    steps = np.where(mrec[1:] != mrec[:-1])[0]
    return float(np.sum((mrec[steps + 1] - mrec[steps]) * mpre[steps + 1]))


def match(detections, truths, min_iou):
    """PASCAL VOC's matching (Everingham et al. 2010, §4.4): detections in descending confidence each take the unmatched
    ground truth of highest IoU, a true positive when that IoU is at least ``min_iou``; a second detection of a truth
    already taken is a false positive. ``detections`` are (shape, score); answers a 0/1 per detection."""
    from shapely.strtree import STRtree
    tree = STRtree([t for t in truths]) if truths else None
    taken = set()
    matched = [0] * len(detections)
    for i in sorted(range(len(detections)), key=lambda k: (-float(detections[k][1]), k)):
        shape = detections[i][0]
        best, best_iou = None, -1.0
        if tree is not None:
            for j in tree.query(shape):
                j = int(j)
                value = iou(shape, truths[j])
                if value > best_iou:
                    best, best_iou = j, value
        if best is not None and best_iou >= min_iou and best not in taken:
            taken.add(best)
            matched[i] = 1
    return matched


def _accuracy_report(tool, value):
    _refuse(tool, "out_accuracy_report", value,
            "its PDF's charts are not specified on the page, and grapy writes no PDF; the table holds every metric")


def ComputeAccuracyForObjectDetection(detected_features, ground_truth_features, out_accuracy_table,
                                      out_accuracy_report=None, detected_class_value_field=None,
                                      ground_truth_class_value_field=None, min_iou=0.5, mask_features=None):
    """EQUIVALENT: precision, recall, F1 and average precision of detections against ground truth, per class and for
    all classes, with PASCAL VOC's matching and all-point average precision (Everingham et al. 2010; 2015).

    Precision is TP / (TP + FP), recall TP / (TP + FN) and F1 their harmonic mean, as the page defines them; a
    detection is a true positive when its IoU with an unmatched ground truth of its class reaches ``min_iou``.
    INFERRED (ADR-248 §5.3): ``min_iou`` defaults to 0.5, PASCAL VOC's threshold; a detection's confidence is its
    ``Confidence`` field when it has one, else every detection ranks equal in object ID order; IoU is that of the
    features' own shapes (boxes, as the tool's detections are); the table has a ``Class`` field naming each class value
    and a last row ``All`` whose counts are the sums and whose AP is the mean of the classes' (mAP)."""
    from . import _gpkg, da
    from ._result import Result
    from .analysis import _out, _read, _schema
    tool = "ComputeAccuracyForObjectDetection"
    if _is_given(out_accuracy_report):
        _accuracy_report(tool, out_accuracy_report)
    threshold = 0.5 if not _is_given(min_iou) else float(min_iou)
    if not 0 <= threshold <= 1:
        raise ValueError(f"{tool}: min_iou ranges from 0 to 1, as the page says.")
    truth_columns, _, srid, _ = _schema(ground_truth_features)
    det_columns = _schema(detected_features)[0]
    det_class = _class_field(det_columns, detected_class_value_field, tool, "detected_class_value_field")
    truth_class = _class_field(truth_columns, ground_truth_class_value_field, tool, "ground_truth_class_value_field")
    confidence = {c.name.lower(): c.name for c in det_columns}.get("confidence")
    area = None
    if _is_given(mask_features):
        area = shapely.union_all([s for _, _, s in _read(mask_features, srid or None)])

    def rows_of(path, columns, field, score):
        order = [c.name for c in columns]
        out = []
        for _, values, shape in _read(path, srid or None):
            if area is not None and not shape.intersects(area):
                continue
            value = values[order.index(field)] if field else 1
            out.append((_number(str(value)) if value is not None else None, shape,
                        float(values[order.index(score)] or 0.0) if score else 0.0))
        return out

    detections = rows_of(detected_features, det_columns, det_class, confidence)
    truths = rows_of(ground_truth_features, truth_columns, truth_class, None)
    classes = sorted({v for v, _, _ in truths} | {v for v, _, _ in detections}, key=lambda v: (str(type(v)), v))
    table = []
    totals = [0, 0, 0]
    aps = []
    for value in classes:
        mine = [(s, c) for v, s, c in detections if v == value]
        theirs = [s for v, s, _ in truths if v == value]
        matched = match(mine, theirs, threshold)
        tp = sum(matched)
        fp, fn = len(mine) - tp, len(theirs) - tp
        ap = average_precision([c for _, c in mine], matched, len(theirs))
        table.append(_metrics(str(value), tp, fp, fn, ap))
        totals = [totals[0] + tp, totals[1] + fp, totals[2] + fn]
        aps.append(ap)
    table.append(_metrics("All", *totals, float(np.mean(aps)) if aps else 0.0))
    out = _out(out_accuracy_table)
    fields = [("Class", "String"), ("Precision", "Double"), ("Recall", "Double"), ("F1_Score", "Double"),
              ("AP", "Double"), ("True_Positive", "Integer"), ("False_Positive", "Integer"),
              ("False_Negative", "Integer")]
    _gpkg.create(out, "Geometry", 0, [_gpkg.Column(n, k) for n, k in fields])
    with da.InsertCursor(out, [n for n, _ in fields] + ["SHAPE@"]) as cursor:
        for row in table:
            cursor.insertRow(tuple(row) + (None,))
    return Result(out)


def _metrics(name, tp, fp, fn, ap):
    precision = tp / (tp + fp) if tp + fp else 0.0
    recall = tp / (tp + fn) if tp + fn else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    return (name, precision, recall, f1, ap, int(tp), int(fp), int(fn))


# ---- Compute Accuracy For Pixel Classification


def confusion(classified, reference, classes):
    """The confusion matrix: rows the classified class, columns the reference class."""
    index = {c: k for k, c in enumerate(classes)}
    matrix = np.zeros((len(classes), len(classes)), np.int64)
    for c, r in zip(classified, reference):
        if c in index and r in index:
            matrix[index[c], index[r]] += 1
    return matrix


def kappa(matrix):
    """Cohen's kappa (Cohen 1960, *Educational and Psychological Measurement* 20(1)): (po − pe) / (1 − pe)."""
    n = matrix.sum()
    if n == 0:
        return 0.0
    po = np.trace(matrix) / n
    pe = float((matrix.sum(axis=0) * matrix.sum(axis=1)).sum()) / (n * n)
    return float((po - pe) / (1 - pe)) if pe != 1 else 1.0


def ComputeAccuracyForPixelClassification(in_raster, in_ground_truth_data, out_confusion_matrix, num_random_points=500,
                                          sampling="STRATIFIED_RANDOM", min_point_distance=0):
    """EQUIVALENT: a confusion matrix of the classified raster against reference data at random points, with user's and
    producer's accuracy, Cohen's kappa (Cohen 1960) and each class's intersection over union, by stratified random
    sampling (Congalton and Green 2019, *Assessing the Accuracy of Remotely Sensed Data*, 3rd ed., ch. 5).

    Points are cell centres of the classified raster drawn without replacement: ``STRATIFIED_RANDOM`` allots each class
    a share of ``num_random_points`` by its area, rounded up, so that the total is never lower, as the page says;
    ``EQUALIZED_STRATIFIED_RANDOM`` the same number to each; ``RANDOM`` across the image. A point's reference class is
    the ground truth raster's cell, or the class of the polygon it lies in; with polygons, points nearer each other than
    ``min_point_distance`` are not drawn.

    The table follows the page's example: a ``ClassValue`` field naming each row, ``C_<value>`` per class, ``Total``,
    ``U_Accuracy`` and ``Kappa`` columns, and rows ``C_<value>``, ``Total``, ``P_Accuracy`` and ``Kappa``. INFERRED
    (ADR-248 §5.3): the draw is seeded by ``env.randomGenerator`` (0 when unset); a last row ``IoU`` holds each class's
    intersection over union, which the page says the tool computes without naming where; the classes are those of
    the classified raster and the reference together."""
    from . import _gpkg, da
    from ._raster import _open_bands
    from ._result import Result
    from .analysis import _out
    from .management import _seed
    tool = "ComputeAccuracyForPixelClassification"
    mode = _word(sampling, "STRATIFIED_RANDOM", ("STRATIFIED_RANDOM", "EQUALIZED_STRATIFIED_RANDOM", "RANDOM"), tool,
                 "sampling")
    total = int(num_random_points or 500)
    classified = _open_bands(in_raster, tool)
    if len(classified._arrays) != 1:
        raise ValueError(f"{tool}: the classified raster is one band.")
    grid = classified._grid
    values = np.asarray(classified._arrays[0])
    valid = ~classified._masks[0]
    rng = np.random.default_rng(_seed())
    reference_raster, polygons = None, None
    if _is_features(in_ground_truth_data):
        polygons, _ = samples(in_ground_truth_data, None, None, grid.srid, tool)
    else:
        reference_raster = _open_bands(in_ground_truth_data, tool)
    xs, ys = grid.centres()
    distance = float(min_point_distance or 0) if polygons is not None else 0.0
    rows_all, cols_all = np.nonzero(valid)
    chosen = []
    if mode == "RANDOM":
        strata = [(None, np.arange(rows_all.size), total)]
    else:
        classes = np.unique(values[valid])
        share = {c: int(((values[valid] == c)).sum()) for c in classes}
        strata = []
        for c in classes:
            members = np.nonzero(values[rows_all, cols_all] == c)[0]
            want = math.ceil(total / len(classes)) if mode == "EQUALIZED_STRATIFIED_RANDOM" else \
                math.ceil(total * share[c] / valid.sum())
            strata.append((c, members, want))
    for _, members, want in strata:
        picked = []
        for k in rng.permutation(members):
            x, y = xs[cols_all[k]], ys[rows_all[k]]
            if distance > 0 and any(math.hypot(x - px, y - py) < distance for px, py in picked + chosen):
                continue
            picked.append((x, y))
            if len(picked) >= want:
                break
        chosen.extend(picked)
    predicted, truth = [], []
    for x, y in chosen:
        cell = grid.cell_of(x, y)
        p = int(values[cell])
        if reference_raster is not None:
            where = reference_raster._grid.cell_of(x, y)
            if where is None or reference_raster._masks[0][where]:
                continue
            t = int(np.asarray(reference_raster._arrays[0])[where])
        else:
            point = shapely.Point(x, y)
            hits = [v for s, v in polygons if s.covers(point)]
            if not hits:
                continue
            t = hits[-1]
        predicted.append(p)
        truth.append(t)
    classes = sorted(set(predicted) | set(truth))
    matrix = confusion(predicted, truth, classes)
    n = len(classes)
    rows_total, cols_total = matrix.sum(axis=1), matrix.sum(axis=0)
    names = [f"C_{c}" for c in classes]
    fields = [("ClassValue", "String")] + [(m, "Double") for m in names] + [("Total", "Double"), ("U_Accuracy", "Double"),
                                                                          ("Kappa", "Double")]
    table = []
    for k in range(n):
        user = matrix[k, k] / rows_total[k] if rows_total[k] else 0.0
        table.append([names[k]] + [float(v) for v in matrix[k]] + [float(rows_total[k]), float(user), None])
    table.append(["Total"] + [float(v) for v in cols_total] + [float(matrix.sum()), None, None])
    producer = [matrix[k, k] / cols_total[k] if cols_total[k] else 0.0 for k in range(n)]
    overall = float(np.trace(matrix) / matrix.sum()) if matrix.sum() else 0.0
    table.append(["P_Accuracy"] + [float(p) for p in producer] + [None, overall, None])
    table.append(["Kappa"] + [None] * n + [None, None, kappa(matrix)])
    ious = [matrix[k, k] / (rows_total[k] + cols_total[k] - matrix[k, k]) if (rows_total[k] + cols_total[k]) else 0.0
            for k in range(n)]
    table.append(["IoU"] + [float(v) for v in ious] + [None, float(np.mean(ious)) if ious else None, None])
    out = _out(out_confusion_matrix)
    _gpkg.create(out, "Geometry", 0, [_gpkg.Column(name, kind) for name, kind in fields])
    with da.InsertCursor(out, [name for name, _ in fields] + ["SHAPE@"]) as cursor:
        for row in table:
            cursor.insertRow(tuple(row) + (None,))
    return Result(out)


ACCURACY_OBJECTS = ("PASCAL VOC matching and all-point average precision",
                    "Everingham et al. 2010, IJCV 88(2); Everingham et al. 2015, IJCV 111(1)")
ACCURACY_PIXELS = ("stratified random sampling, a confusion matrix and Cohen's kappa",
                   "Congalton and Green 2019, Assessing the Accuracy of Remotely Sensed Data, ch. 5; Cohen 1960")
