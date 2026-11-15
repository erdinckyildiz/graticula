"""arcpy's SpatialReference and Extent, by their documented names — ADR-223 §5.2."""
from __future__ import annotations


# Esri's unit names where pyproj's Esri WKT writes another (as ArcGIS Pro 3.7 answered linearUnitName, 2026-11-08).
_UNIT_NAMES = {"US survey foot": "Foot_US", "foot": "Foot", "metre": "Meter"}


def _esri_parts(crs):
    """The names and parameters of a reference's Esri WKT, as pyproj writes it from the EPSG definition."""
    import re
    try:
        text = crs.to_wkt("WKT1_ESRI")
    except Exception:  # noqa: BLE001 — a reference Esri WKT cannot express
        return {"name": "", "unit": ""}
    head = re.match(r'(PROJCS|GEOGCS)\["([^"]*)"', text)
    datum = re.search(r'DATUM\["([^"]*)",SPHEROID\["([^"]*)",([-0-9.eE]+),([-0-9.eE]+)\]', text)
    projection = re.search(r'PROJECTION\["([^"]*)"\]', text)
    primem = re.search(r'PRIMEM\["([^"]*)"', text)
    units = re.findall(r'UNIT\["([^"]*)"', text)
    return {"name": head.group(2) if head else "", "datum": datum.group(1) if datum else "",
            "spheroid": datum.group(2) if datum else "", "a": float(datum.group(3)) if datum else 0.0,
            "invf": float(datum.group(4)) if datum else 0.0, "projection": projection.group(1) if projection else "",
            "primem": primem.group(1) if primem else "", "unit": units[-1] if units else "",
            "parameters": {k: float(v) for k, v in re.findall(r'PARAMETER\["([^"]*)",([-0-9.eE]+)\]', text)}}


class SpatialReference:
    """A coordinate system by its EPSG code; ``factoryCode`` is the code, as arcpy names it."""

    def __init__(self, item=None):
        self.factoryCode = 0
        self.name = "Unknown"
        self.type = "Unknown"
        self.linearUnitName = ""
        self.angularUnitName = ""
        self.metersPerUnit = 1.0
        if item is not None:
            self.loadFromString(str(item)) if not isinstance(item, int) else self._load(item)

    def _load(self, code):
        from pyproj import CRS
        crs = CRS.from_epsg(int(code))
        self.factoryCode = int(code)
        self.type = "Geographic" if crs.is_geographic else "Projected"
        axis = crs.axis_info[0] if crs.axis_info else None
        self.metersPerUnit = 1.0 if crs.is_geographic or not axis else float(axis.unit_conversion_factor)
        esri = _esri_parts(crs)
        # Names are the Esri WKT's — WGS_1984_UTM_Zone_35N, GCS_WGS_1984, D_WGS_1984 — which pyproj writes from the
        # EPSG definition, as ArcGIS Pro 3.7 answers them (measured 2026-11-08, the Pro sweep).
        self.name = esri["name"] or crs.name
        self.linearUnitName = "" if crs.is_geographic else _UNIT_NAMES.get(esri["unit"], esri["unit"])
        self.angularUnitName = "Degree" if crs.is_geographic else ""
        self._esri = esri
        self._crs = crs

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
        # Esri WKT, as ArcGIS Pro 3.7's begins (PROJCS["WGS_1984_UTM_Zone_35N",…), measured 2026-11-08.
        return CRS.from_epsg(self.factoryCode).to_wkt("WKT1_ESRI") if self.factoryCode else ""

    @property
    def PCSCode(self):
        return self.factoryCode if self.type == "Projected" else 0

    @property
    def GCSCode(self):
        return self.factoryCode if self.type == "Geographic" else 0

    # The properties below answer as ArcGIS Pro 3.7 does (measured 2026-11-08, the Pro sweep): a projected reference's
    # own projection parameters, with its datum and spheroid properties empty and its geographic system as ``GCS``; a
    # geographic one's datum, spheroid and prime meridian. Default tolerances are Esri's: 1 mm and 0.1 mm in the
    # reference's units, or on a geographic one 1 mm of arc at the equator and 1e-9 degrees.

    def _part(self, key, default=None):
        return getattr(self, "_esri", {}).get(key, default)

    def _parameter(self, name):
        if self.type != "Projected":
            return 0.0
        return float(self._part("parameters", {}).get(name, 0.0))

    @property
    def PCSName(self):
        return self.name if self.type == "Projected" else ""

    @property
    def GCSName(self):
        return self.name if self.type == "Geographic" else ""

    @property
    def GCS(self):
        if self.type == "Geographic":
            return self
        code = self._crs.geodetic_crs.to_epsg() if getattr(self, "_crs", None) is not None else None
        return SpatialReference(int(code)) if code else SpatialReference()

    @property
    def linearUnitCode(self):
        return self._unit_code() if self.type == "Projected" else 0

    @property
    def angularUnitCode(self):
        return 9102 if self.type == "Geographic" else 0

    @property
    def radiansPerUnit(self):
        import math
        return math.pi / 180 if self.type == "Geographic" else 0.0

    def _unit_code(self):
        crs = getattr(self, "_crs", None)
        try:
            return int(crs.axis_info[0].unit_code) if crs is not None and crs.axis_info else 0
        except (TypeError, ValueError):
            return 0

    @property
    def projectionName(self):
        return self._part("projection", "") if self.type == "Projected" else ""

    @property
    def falseEasting(self):
        return self._parameter("False_Easting")

    @property
    def falseNorthing(self):
        return self._parameter("False_Northing")

    @property
    def centralMeridian(self):
        return self._parameter("Central_Meridian")

    @property
    def scaleFactor(self):
        return self._parameter("Scale_Factor")

    @property
    def latitudeOfOrigin(self):
        return self._parameter("Latitude_Of_Origin")

    @property
    def datumName(self):
        return self._part("datum", "") if self.type == "Geographic" else ""

    @property
    def datumCode(self):
        if self.type != "Geographic":
            return 0
        datum = self._crs.datum
        try:
            return int((datum.to_json_dict().get("id") or {}).get("code", 0)) if datum is not None else 0
        except Exception:  # noqa: BLE001 — a datum pyproj cannot code
            return 0

    @property
    def spheroidName(self):
        return self._part("spheroid", "") if self.type == "Geographic" else ""

    @property
    def semiMajorAxis(self):
        return float(self._part("a", 0.0)) if self.type == "Geographic" else 0.0

    @property
    def flattening(self):
        inverse = float(self._part("invf", 0.0))
        return (1.0 / inverse if inverse else 0.0) if self.type == "Geographic" else 0.0

    @property
    def primeMeridianName(self):
        return self._part("primem", "") if self.type == "Geographic" else ""

    @property
    def XYTolerance(self):
        import math
        if self.type == "Geographic":
            return 0.001 / (float(self._part("a", 6378137.0)) * math.pi / 180)
        return 0.001 / (self.metersPerUnit or 1.0)

    @property
    def XYResolution(self):
        return 1e-9 if self.type == "Geographic" else 0.0001 / (self.metersPerUnit or 1.0)

    ZTolerance = 0.001
    MTolerance = 0.001
    isHighPrecision = True
    hasXYPrecision = True

    def __eq__(self, other):
        return isinstance(other, SpatialReference) and other.factoryCode == self.factoryCode

    def __hash__(self):
        return hash(self.factoryCode)

    def __repr__(self):
        return f"SpatialReference({self.factoryCode})"


class Extent:
    """arcpy's Extent: ``Extent(XMin, YMin, XMax, YMax, ZMin, ZMax, MMin, MMax, spatial_reference)``, as documented.
    grapy's own code also passes the reference fifth, ``Extent(x0, y0, x1, y1, reference)``; a fifth argument that is
    not a number is read as that reference."""

    def __init__(self, XMin=None, YMin=None, XMax=None, YMax=None, ZMin=None, ZMax=None, MMin=None, MMax=None,
                 spatial_reference=None):
        if ZMin is not None and not isinstance(ZMin, (int, float)):
            ZMin, spatial_reference = None, ZMin
        self.XMin, self.YMin, self.XMax, self.YMax = XMin, YMin, XMax, YMax
        self.ZMin, self.ZMax, self.MMin, self.MMax = ZMin, ZMax, MMin, MMax
        self.spatialReference = reference_of(spatial_reference)

    def _point(self, x, y):
        from ._geometry import Point
        return Point(x, y)

    @property
    def lowerLeft(self):
        return self._point(self.XMin, self.YMin)

    @property
    def lowerRight(self):
        return self._point(self.XMax, self.YMin)

    @property
    def upperLeft(self):
        return self._point(self.XMin, self.YMax)

    @property
    def upperRight(self):
        return self._point(self.XMax, self.YMax)

    def overlaps(self, other):
        return self.polygon.overlaps(_extent_shape(other))

    def disjoint(self, other):
        return self.polygon.disjoint(_extent_shape(other))

    def within(self, other):
        return self.polygon.within(_extent_shape(other))

    def touches(self, other):
        return self.polygon.touches(_extent_shape(other))

    def crosses(self, other):
        return self.polygon.crosses(_extent_shape(other))

    def equals(self, other):
        return self.polygon.equals(_extent_shape(other))

    def projectAs(self, spatial_reference, transformation_name=None):
        """The extent of this extent's polygon projected — the bounds of the projected rectangle."""
        moved = self.polygon.projectAs(spatial_reference, transformation_name)
        e = moved.extent
        return Extent(e.XMin, e.YMin, e.XMax, e.YMax, self.ZMin, self.ZMax, self.MMin, self.MMax,
                      moved.spatialReference)

    def __str__(self):
        """``"XMin YMin XMax YMax ZMin ZMax MMin MMax"``, NaN for each value not set, as ArcGIS Pro 3.7 writes it
        (measured 2026-11-08)."""
        def text(v):
            if v is None:
                return "NaN"
            return str(int(v)) if float(v).is_integer() else repr(float(v))
        return " ".join(text(v) for v in (self.XMin, self.YMin, self.XMax, self.YMax, self.ZMin, self.ZMax,
                                          self.MMin, self.MMax))

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
        return self.polygon.contains(_extent_shape(other))

    def __repr__(self):
        return f"Extent({self.XMin}, {self.YMin}, {self.XMax}, {self.YMax})"


def _extent_shape(other):
    """A geometry to relate an extent to: an Extent's polygon, or the geometry or Point itself."""
    from ._geometry import Point, PointGeometry
    if isinstance(other, Extent):
        return other.polygon
    if isinstance(other, Point):
        return PointGeometry(other)
    return other


def reference_of(value):
    """A SpatialReference from one, a code, or text; None stays None."""
    if value is None or isinstance(value, SpatialReference):
        return value
    if isinstance(value, int) and value <= 0:
        return None
    return SpatialReference(value)
