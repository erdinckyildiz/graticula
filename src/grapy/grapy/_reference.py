"""arcpy's SpatialReference and Extent, by their documented names — ADR-223 §5.2."""
from __future__ import annotations


class SpatialReference:
    """A coordinate system by its EPSG code; ``factoryCode`` is the code, as arcpy names it."""

    def __init__(self, item=None):
        self.factoryCode = 0
        self.name = "Unknown"
        self.type = "Unknown"
        self.linearUnitName = ""
        self.angularUnitName = ""
        if item is not None:
            self.loadFromString(str(item)) if not isinstance(item, int) else self._load(item)

    def _load(self, code):
        from pyproj import CRS
        crs = CRS.from_epsg(int(code))
        self.factoryCode = int(code)
        self.name = crs.name
        self.type = "Geographic" if crs.is_geographic else "Projected"
        axis = crs.axis_info[0] if crs.axis_info else None
        self.linearUnitName = "" if crs.is_geographic else (axis.unit_name.title() if axis else "")
        self.angularUnitName = "Degree" if crs.is_geographic else ""

    def loadFromString(self, text):
        text = text.strip()
        if text.isdigit():
            self._load(int(text))
            return
        from pyproj import CRS
        crs = CRS.from_user_input(text)
        code = crs.to_epsg()
        if code is None:
            raise ValueError(f"'{text[:60]}' names no EPSG coordinate system grapy can carry.")
        self._load(code)

    def exportToString(self):
        from pyproj import CRS
        return CRS.from_epsg(self.factoryCode).to_wkt() if self.factoryCode else ""

    @property
    def PCSCode(self):
        return self.factoryCode if self.type == "Projected" else 0

    @property
    def GCSCode(self):
        return self.factoryCode if self.type == "Geographic" else 0

    def __eq__(self, other):
        return isinstance(other, SpatialReference) and other.factoryCode == self.factoryCode

    def __hash__(self):
        return hash(self.factoryCode)

    def __repr__(self):
        return f"SpatialReference({self.factoryCode})"


class Extent:
    """arcpy's Extent: XMin, YMin, XMax, YMax and a reference."""

    def __init__(self, XMin=None, YMin=None, XMax=None, YMax=None, spatial_reference=None, *_):
        self.XMin, self.YMin, self.XMax, self.YMax = XMin, YMin, XMax, YMax
        self.spatialReference = reference_of(spatial_reference)

    @property
    def width(self):
        return self.XMax - self.XMin

    @property
    def height(self):
        return self.YMax - self.YMin

    @property
    def polygon(self):
        from shapely.geometry import box
        from ._geometry import Polygon
        return Polygon(box(self.XMin, self.YMin, self.XMax, self.YMax), self.spatialReference)

    def contains(self, other):
        return self.polygon.contains(other)

    def __repr__(self):
        return f"Extent({self.XMin}, {self.YMin}, {self.XMax}, {self.YMax})"


def reference_of(value):
    """A SpatialReference from one, a code, or text; None stays None."""
    if value is None or isinstance(value, SpatialReference):
        return value
    if isinstance(value, int) and value <= 0:
        return None
    return SpatialReference(value)
