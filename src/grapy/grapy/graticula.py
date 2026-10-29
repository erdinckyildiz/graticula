"""Graticula's own tools, beside arcpy's names and never under them — ADR-223 §5.2, the owner's *"ekstra toollar"*."""
from __future__ import annotations

from . import _gpkg, da
from ._result import Result


def HexagonBins(in_features, out_feature_class, size, count_field="point_count"):
    """Hexagons of ``size`` (the distance between flat sides, in the data's units) over the features, each with how many
    of them it holds — what the Spatial Analysis Service's AggregatePoints does with bins, as a toolbox step."""
    import math

    from shapely.geometry import Polygon
    from shapely.strtree import STRtree

    _, _, srid, _ = _describe(in_features)
    with da.SearchCursor(in_features, ["SHAPE@"]) as rows:
        shapes = [r[0]._shape for r in rows if r[0] is not None]
    out = out_feature_class if ".gpkg" in str(out_feature_class).lower() else out_feature_class + ".gpkg"
    _gpkg.create(out, "Polygon", srid, [_gpkg.Column(count_field, "Integer")])
    if not shapes:
        return Result(out)

    edge = size / math.sqrt(3)
    x0, y0, x1, y1 = (min(s.bounds[0] for s in shapes), min(s.bounds[1] for s in shapes),
                      max(s.bounds[2] for s in shapes), max(s.bounds[3] for s in shapes))
    tree = STRtree(shapes)
    width, height = 1.5 * edge, size
    with da.InsertCursor(out, [count_field, "SHAPE@"]) as written:
        column = 0
        x = x0 - edge
        while x <= x1 + edge:
            y = y0 - size + (height / 2 if column % 2 else 0)
            while y <= y1 + size:
                hexagon = Polygon([(x + edge * math.cos(math.radians(a)), y + edge * math.sin(math.radians(a)))
                                   for a in range(0, 360, 60)])
                inside = sum(1 for i in tree.query(hexagon, predicate="intersects"))
                if inside:
                    written.insertRow((inside, hexagon))
                y += height
            x += width
            column += 1
    return Result(out)


def _describe(path):
    connection, table = _gpkg.table_of(path)
    try:
        return _gpkg.describe(connection, table)
    finally:
        connection.close()


# ---- network dataset settings (ADR-226) -----------------------------------------------------------------------------
#
# ArcGIS Pro sets a network dataset's connectivity, attributes and travel modes in its properties dialog, with no
# documented tool; these are grapy's. Each leaves the network to be built again with arcpy.na.BuildNetwork.

def SetNetworkConnectivity(in_network_dataset, source, policy):
    """A source's connectivity policy: ``END_POINT`` or ``ANY_VERTEX``."""
    from . import _network
    _network.set_connectivity(str(in_network_dataset), source, policy)
    return Result(str(in_network_dataset))


def AddNetworkAttribute(in_network_dataset, name, usage, units=None, evaluators=(), use_by_default=False):
    """A ``Cost`` or ``Restriction`` attribute and its evaluators, ``[(source, direction, kind, value)]``: direction
    ``FROM_TO``, ``TO_FROM`` or ``BOTH``; kind ``LENGTH`` (a cost in the linear unit ``units``), ``FIELD`` (a field's
    value; a restriction holds where it is true) or ``CONSTANT``. Script evaluators are refused."""
    from . import _network
    _network.add_attribute(str(in_network_dataset), name, usage, units, evaluators, use_by_default)
    return Result(str(in_network_dataset))


def AddNetworkTravelMode(in_network_dataset, name, impedance, restrictions=(), time_attribute=None, distance_attribute=None):
    """A travel mode: the cost attribute it minimises, the restrictions it honours, and its time and distance
    attributes."""
    from . import _network
    _network.add_travel_mode(str(in_network_dataset), name, impedance, restrictions, time_attribute, distance_attribute)
    return Result(str(in_network_dataset))
