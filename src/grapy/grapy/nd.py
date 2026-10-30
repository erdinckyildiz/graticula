"""arcpy.nd — network diagrams of a utility network, by their documented names and parameters (ADR-232).

The model is ``grapy._diagram`` (templates, diagrams, diagram features and their links to network elements, in
grapy's tables and three feature classes beside the network), the rules ``grapy._ndrules`` and the layouts
``grapy._ndlayout``. A *diagram layer* — what the layout and diagram tools take — is a grapy layer made by
MakeDiagramLayer or CreateDiagram and named after the diagram, or a diagram path
``<file>.gpkg/<feature dataset>/<network>/<diagram>`` (INFERRED, ADR-232 §5.9 (g)); the *map* AppendToDiagram and
OverwriteDiagram read is grapy's layers, whose selections stand for the map's (INFERRED: grapy has no map document).

Refused by name, each for its own reason: the diagram layer definitions (.ndld) and the tools that make them — a
layer definition is the symbology of a diagram map in ArcGIS Pro's application; the model a template becomes —
ModelBuilder; Rotate Tree — it turns trees round pivot junctions, which only Pro's interactive diagram map sets; and
Relative Mainline — its page describes the result but not how lines are ordered and spaced, and no published method
draws it. The telecom circuit diagrams and the trace network's path direction are refused with them.
"""
from __future__ import annotations

import datetime
import json
import os

from . import _diagram, _gpkg, _layers, _ndlayout, _utility
from ._diagram import JUNCTION_SIZE
from ._result import Result
from ._runtime import ExecuteError, NotBuiltError, equivalent

_TELECOM = ("needs a telecom domain network, which grapy does not hold: the Add Telecom Domain Network page names the "
            "tool's parameters but not the classes and tables a telecom domain network has (ADR-231 §5.8)")


# ---- parameters ----------------------------------------------------------------------------------------------------

def _given(value):
    return value is not None and str(value).strip() not in ("", "#")


def _word(value, default):
    return str(value).strip().upper() if _given(value) else default


def _flag(value, yes, no, default, name="parameter"):
    if value is True:
        return True
    if value is False:
        return False
    word = _word(value, default)
    if word in (yes, "TRUE"):
        return True
    if word in (no, "FALSE"):
        return False
    raise ExecuteError(f"ERROR 000800: {name} is {yes} or {no}, not '{value}'.")


def _choice(value, words, default, name):
    word = _word(value, default)
    if word not in words:
        raise ExecuteError(f"ERROR 000800: {name} is one of {', '.join(words)}, not '{value}'.")
    return word


def _path(value):
    return os.fspath(value) if isinstance(value, os.PathLike) else str(value)


def _rows(value):
    """A multivalue parameter as a list: a list, or a string split on ``;`` outside quotes."""
    if value is None:
        return []
    if isinstance(value, (list, tuple)):
        return [v for v in value if _given(v)]
    rows, current, quote = [], "", None
    for c in str(value):
        if quote:
            current += c
            if c == quote:
                quote = None
        elif c in "'\"":
            quote = c
            current += c
        elif c == ";":
            rows.append(current)
            current = ""
        else:
            current += c
    rows.append(current)
    return [r.strip().strip("'\"") for r in rows if r.strip()]


def _class_name(value):
    """A source as a template keeps it: the class's short name (``WaterDevice``), or ``SystemJunctions``."""
    layer = _layers.find(value)
    text = _path(layer.source if layer is not None else value).replace("\\", "/")
    return text.rsplit("/", 1)[-1]


def _active(value):
    return _flag(value, "ACTIVE", "INACTIVE", "ACTIVE", "is_active")


def _network_path(value):
    """The utility network a parameter names: a path, a utility network layer, or a diagram layer's network."""
    layer = _layers.find(value)
    if layer is not None:
        return getattr(layer, "network", None) or layer.source
    return _path(value)


def _diagram_path(value):
    """(network path, diagram name) of a diagram layer or a diagram path."""
    layer = _layers.find(value)
    if layer is not None and getattr(layer, "diagram", None):
        return layer.network, layer.diagram
    text = _path(value).replace("\\", "/")
    if ".gpkg" in text.lower() and text.count("/") >= 2:
        network, name = text.rsplit("/", 1)
        try:
            _utility.locate(network)
            with _utility.Network(network):
                return network, name
        except ExecuteError:
            pass
    raise ExecuteError(f"ERROR 000732: '{value}' is not a diagram layer: make one with MakeDiagramLayer or CreateDiagram, "
                       "or give <file>.gpkg/<feature dataset>/<network>/<diagram>.")


def _register(name, network, diagram):
    layer = _layers.Layer(str(name), network, None, "DiagramLayer")
    layer.network, layer.diagram = network, diagram
    _layers.LAYERS[str(name)] = layer
    return layer


def _rename_layers(network, old, new):
    for layer in _layers.LAYERS.values():
        if getattr(layer, "diagram", None) == old and getattr(layer, "network", None) == network:
            layer.diagram = new


class _Units:
    """A layout's distances: linear units in the diagram's units, proportional ones in junction sizes."""

    def __init__(self, network_path, tool):
        self.tool = tool
        with _utility.Network(network_path) as network:
            self.srid = _diagram.srid_of(network)

    def linear(self, value):
        from .management import _linear
        return _linear(value, self.srid, self.tool)


def _pair(out, key, absolute_value, proportional_value, absolute, units):
    if absolute and _given(absolute_value):
        out[key] = units.linear(absolute_value)
    elif not absolute and _given(proportional_value):
        out[key] = float(proportional_value) * JUNCTION_SIZE


def _containers(out, value):
    if _given(value):
        out["preserve"] = _flag(value, "PRESERVE_CONTAINERS", "IGNORE_CONTAINERS", "IGNORE_CONTAINERS",
                                "are_containers_preserved")


def _run_async(value):
    """Synchronous or asynchronous: a grapy layout runs in the tool's own process either way."""
    _choice(value, ("RUN_SYNCHRONOUSLY", "RUN_ASYNCHRONOUSLY"), "RUN_SYNCHRONOUSLY", "run_async")


def _edge_display(out, are_edges_orthogonal, breakpoint_position, edge_display_type):
    if _given(edge_display_type):
        out["edge_display"] = _choice(edge_display_type, ("REGULAR_EDGES", "ORTHOGONAL_EDGES", "CURVED_EDGES"),
                                      "REGULAR_EDGES", "edge_display_type")
    elif _given(are_edges_orthogonal):
        out["edge_display"] = "ORTHOGONAL_EDGES" if _flag(are_edges_orthogonal, "ORTHOGONAL_EDGES", "SLANTED_EDGES",
                                                          "SLANTED_EDGES") else "REGULAR_EDGES"
    if _given(breakpoint_position):
        value = float(breakpoint_position)
        if not 0 <= value <= 100:
            raise ExecuteError("ERROR 000800: breakpoint_position is a percentage from 0 to 100.")
        out["breakpoint"] = value


# Each layout's parameters, read from a tool's arguments into the keys ``_ndlayout`` takes. Only what was given is
# returned: an Apply tool lays its own over the template's configured values, which lie over the defaults (the pages:
# "if the input diagram's template configures this layout with different values, those values are used instead").

def _grid(a, units):
    out = {}
    _containers(out, a.get("are_containers_preserved"))
    for name, key in (("cell_width_absolute", "cell_width"), ("cell_height_absolute", "cell_height")):
        if _given(a.get(name)):
            out[key] = units.linear(a[name])
    return out


def _linear_dispatch(a, units):
    out = {}
    if _given(a.get("junction_placement_type")):
        out["placement"] = _choice(a["junction_placement_type"], ("EQUAL_DISTANCE", "USER_DEFINE_DISTANCE",
                                                                  "ITERATIVE_DISTANCE"), "EQUAL_DISTANCE",
                                   "junction_placement_type")
    absolute = _flag(a.get("is_unit_absolute"), "ABSOLUTE_UNIT", "PROPORTIONAL_UNIT", "PROPORTIONAL_UNIT")
    _pair(out, "maximum_shift", a.get("maximum_shift_absolute"), a.get("maximum_shift_proportional"), absolute, units)
    _pair(out, "minimum_shift", a.get("minimum_shift_absolute"), a.get("minimum_shift_proportional"), absolute, units)
    _pair(out, "expand_shift", a.get("expand_shift_absolute"), a.get("expand_shift_proportional"), absolute, units)
    if _given(a.get("iterations_number")):
        out["iterations"] = int(a["iterations_number"])
    for name, key, yes, no, default in (("is_path_preserved", "preserve_path", "PRESERVE_PATH", "IGNORE_PATH",
                                         "PRESERVE_PATH"),
                                        ("are_leaves_moved", "move_leaves", "MOVE_LEAVES", "DO_NOT_MOVE_LEAVES",
                                         "DO_NOT_MOVE_LEAVES"),
                                        ("are_leaves_expanded", "expand_leaves", "EXPAND_LEAVES",
                                         "DO_NOT_EXPAND_LEAVES", "DO_NOT_EXPAND_LEAVES")):
        if _given(a.get(name)):
            out[key] = _flag(a[name], yes, no, default, name)
    return out


def _spatial_dispatch(a, units):
    out = {}
    _containers(out, a.get("are_containers_preserved"))
    if _given(a.get("iterations_number")):
        out["iterations"] = int(a["iterations_number"])
    if _given(a.get("maximum_shift_factor")):
        out["maximum_shift_factor"] = float(a["maximum_shift_factor"])
    return out


def _compression(a, units):
    out = {}
    _containers(out, a.get("are_containers_preserved"))
    if _given(a.get("grouping_distance_absolute")):
        out["grouping_distance"] = units.linear(a["grouping_distance_absolute"])
    if _given(a.get("vertices_removal_rule")):
        out["vertices_removal"] = _choice(a["vertices_removal_rule"], ("ALL", "OUTER", "OUTER_EXCEPT_FIRST"), "OUTER",
                                          "vertices_removal_rule")
    return out


def _angle_directed(a, units):
    out = {}
    _containers(out, a.get("are_containers_preserved"))
    if _given(a.get("iterations_number")):
        out["iterations"] = int(a["iterations_number"])
    if _given(a.get("number_of_directions")):
        out["directions"] = _choice(a["number_of_directions"], ("TWELVE_DIRECTIONS", "EIGHT_DIRECTIONS",
                                                                "FOUR_DIRECTIONS"), "EIGHT_DIRECTIONS",
                                    "number_of_directions")
    return out


def _force_directed(a, units):
    out = {}
    _containers(out, a.get("are_containers_preserved"))
    if _given(a.get("iterations_number")):
        out["iterations"] = int(a["iterations_number"])
    if _given(a.get("repel_factor")):
        out["repel_factor"] = float(a["repel_factor"])
    if _given(a.get("degree_freedom")):
        out["degree_freedom"] = _choice(a["degree_freedom"], ("LOW", "MEDIUM", "HIGH"), "LOW", "degree_freedom")
    if _given(a.get("edge_display_type")):
        out["edge_display"] = _choice(a["edge_display_type"], ("REGULAR_EDGES", "CURVED_EDGES"), "REGULAR_EDGES",
                                      "edge_display_type")
    if _given(a.get("breakpoint_position")):
        value = float(a["breakpoint_position"])
        if not 15 <= value <= 40:
            raise ExecuteError("ERROR 000800: a curved edge's breakpoint_position is from 15 to 40 per cent.")
        out["breakpoint"] = value
    return out


_DIRECTIONS = ("FROM_LEFT_TO_RIGHT", "FROM_RIGHT_TO_LEFT", "FROM_BOTTOM_TO_TOP", "FROM_TOP_TO_BOTTOM")


def _tree_spacing(a, units, names):
    out = {}
    absolute = _flag(a.get("is_unit_absolute"), "ABSOLUTE_UNIT", "PROPORTIONAL_UNIT", "PROPORTIONAL_UNIT")
    for key in names:
        _pair(out, key, a.get(f"{key}_absolute"), a.get(f"{key}_proportional"), absolute, units)
    if "offset" in names and "offset" in out and not out["offset"]:
        out.pop("offset")
    return out


def _smart_tree(a, units):
    out = {}
    _containers(out, a.get("are_containers_preserved"))
    if _given(a.get("tree_direction")):
        out["direction"] = _choice(a["tree_direction"], _DIRECTIONS, "FROM_LEFT_TO_RIGHT", "tree_direction")
    spacing = _tree_spacing(a, units, ("subtree", "perpendicular", "along", "offset"))
    absolute = _flag(a.get("is_unit_absolute"), "ABSOLUTE_UNIT", "PROPORTIONAL_UNIT", "PROPORTIONAL_UNIT")
    _pair(spacing, "disjoined", a.get("disjoined_graph_absolute"), a.get("disjoined_graph_proportional"), absolute,
          units)
    out.update(spacing)
    _edge_display(out, a.get("are_edges_orthogonal"), a.get("breakpoint_position"), a.get("edge_display_type"))
    if _given(a.get("order_tree_branches")):
        out["order_by_attribute"] = _flag(a["order_tree_branches"], "USE_ORDER_ATTRIBUTE", "USE_CURRENT_GEOMETRY",
                                          "USE_CURRENT_GEOMETRY", "order_tree_branches")
    if _given(a.get("attribute_availability")):
        out["order_on_edge"] = _choice(a["attribute_availability"], ("JUNCTION", "EDGE"), "JUNCTION",
                                       "attribute_availability") == "EDGE"
    if _given(a.get("attribute_name")):
        out["order_attribute"] = str(a["attribute_name"])
    if _given(a.get("attribute_sort_order")):
        out["order_descending"] = _choice(a["attribute_sort_order"], ("ASCENDING", "DESCENDING"), "ASCENDING",
                                          "attribute_sort_order") == "DESCENDING"
    return out


def _mainline_tree(a, units):
    out = {}
    _containers(out, a.get("are_containers_preserved"))
    if _given(a.get("tree_direction")):
        out["direction"] = _choice(a["tree_direction"], _DIRECTIONS, "FROM_LEFT_TO_RIGHT", "tree_direction")
    if _given(a.get("branches_placement")):
        out["placement"] = _choice(a["branches_placement"], ("BOTH_SIDES", "LEFT_SIDE", "RIGHT_SIDE"), "BOTH_SIDES",
                                   "branches_placement")
    out.update(_tree_spacing(a, units, ("perpendicular", "along", "offset")))
    absolute = _flag(a.get("is_unit_absolute"), "ABSOLUTE_UNIT", "PROPORTIONAL_UNIT", "PROPORTIONAL_UNIT")
    _pair(out, "disjoined", a.get("disjoined_graph_absolute"), a.get("disjoined_graph_proportional"), absolute, units)
    _edge_display(out, a.get("are_edges_orthogonal"), a.get("breakpoint_position"), a.get("edge_display_type"))
    return out


def _radial_tree(a, units):
    out = {}
    _containers(out, a.get("are_containers_preserved"))
    absolute = _flag(a.get("is_unit_absolute"), "ABSOLUTE_UNIT", "PROPORTIONAL_UNIT", "PROPORTIONAL_UNIT")
    _pair(out, "initial_radius", a.get("initial_radius_absolute"), a.get("initial_radius_proportional"), absolute,
          units)
    _pair(out, "disjoined", a.get("disjoined_graph_absolute"), a.get("disjoined_graph_proportional"), absolute, units)
    if _given(a.get("radius_factor")):
        out["radius_factor"] = float(a["radius_factor"])
    return out


def _main_ring(a, units):
    out = {}
    _containers(out, a.get("are_containers_preserved"))
    if _given(a.get("ring_type")):
        out["ring_type"] = _choice(a["ring_type"], ("ELLIPSE", "RECTANGLE"), "ELLIPSE", "ring_type")
    absolute = _flag(a.get("is_unit_absolute"), "ABSOLUTE_UNIT", "PROPORTIONAL_UNIT", "PROPORTIONAL_UNIT")
    for key in ("ring_width", "ring_height"):
        _pair(out, key, a.get(f"{key}_absolute"), a.get(f"{key}_proportional"), absolute, units)
    if _given(a.get("tree_type")):
        out["tree_type"] = _choice(a["tree_type"], ("BOTH_SIDES", "LEFT_SIDE", "RIGHT_SIDE", "SMART_TREE"),
                                   "SMART_TREE", "tree_type")
    out.update(_tree_spacing(a, units, ("perpendicular", "along", "offset")))
    _edge_display(out, None, a.get("breakpoint_position"), a.get("edge_display_type"))
    return out


def _partial_overlapping(a, units):
    out = {}
    for name, key in (("buffer_width_absolute", "buffer_width"), ("offset_absolute", "offset")):
        if _given(a.get(name)):
            out[key] = units.linear(a[name])
    if _given(a.get("optimize_edges")):
        out["optimize"] = _flag(a["optimize_edges"], "OPTIMIZE_EDGES", "DO_NOT_OPTIMIZE_EDGES",
                                "DO_NOT_OPTIMIZE_EDGES", "optimize_edges")
    return out


def _reshape(a, units):
    out = {}
    _containers(out, a.get("are_containers_preserved"))
    out["reshape_type"] = _choice(a.get("reshape_type"), ("REMOVE_VERTICES", "SQUARE_EDGES",
                                                          "SEPARATE_OVERLAPPING_EDGES", "REDUCE_VERTICES_BY_ANGLE",
                                                          "MARK_CROSSING_EDGES"), "SQUARE_EDGES", "reshape_type")
    if _given(a.get("is_path_preserved")):
        out["preserve_path"] = _flag(a["is_path_preserved"], "PRESERVE_PATH", "IGNORE_PATH", "PRESERVE_PATH",
                                     "is_path_preserved")
    for name, key in (("offset_between_segment_absolute", "offset_between_segment"),
                      ("breakpoint_absolute", "breakpoint"), ("shift_between_edge_absolute", "shift_between_edge"),
                      ("circular_arc_radius", "arc_radius")):
        if _given(a.get(name)):
            out[key] = units.linear(a[name])
    if _given(a.get("angle_threshold")):
        out["angle_threshold"] = float(a["angle_threshold"])
    if _given(a.get("circular_arc_position")):
        out["arc_position"] = _choice(a["circular_arc_position"], ("LEFT_OF_VERTICAL_SEGMENT",
                                                                   "RIGHT_OF_VERTICAL_SEGMENT",
                                                                   "ABOVE_HORIZONTAL_SEGMENT",
                                                                   "BELOW_HORIZONTAL_SEGMENT"),
                                      "ABOVE_HORIZONTAL_SEGMENT", "circular_arc_position")
    return out


def _geo(a, units):
    out = {}
    if _given(a.get("restore_edges_geo_positions")):
        out["restore_edges"] = _flag(a["restore_edges_geo_positions"], "RESTORE_EDGES_GEO_POSITIONS",
                                     "DO_NOT_RESTORE_EDGES_GEO_POSITIONS", "RESTORE_EDGES_GEO_POSITIONS",
                                     "restore_edges_geo_positions")
    return out


_LAYOUTS = {"Grid": _grid, "LinearDispatch": _linear_dispatch, "SpatialDispatch": _spatial_dispatch,
            "Compression": _compression, "AngleDirected": _angle_directed, "ForceDirected": _force_directed,
            "SmartTree": _smart_tree, "MainlineTree": _mainline_tree, "RadialTree": _radial_tree,
            "MainRing": _main_ring, "PartialOverlappingEdges": _partial_overlapping, "ReshapeEdges": _reshape,
            "GeoPositions": _geo}


def _apply(layout, a, tool):
    """An Apply … Layout tool: the diagram laid out with its arguments over the template's values for the layout."""
    _run_async(a.get("run_async"))
    network_path, name = _diagram_path(a["in_network_diagram_layer"])
    given = _LAYOUTS[layout](a, _Units(network_path, tool))
    with _utility.Network(network_path) as network:
        d = _diagram.load(network, name)
        found = _diagram.template(network, d.template)
        params = {}
        for step in found["steps"]:
            if step["kind"] == "layout" and step["layout"] == layout:
                params = dict(step["params"])
        params.update(given)
        _require(layout, params, tool)
        builder = _diagram.Builder(network)
        builder.bind(d)
        _ndlayout.run(builder, d, layout, params, found)
        d.updated, d.updated_by = _diagram.now(), _diagram.user()
        _diagram.save(network, d)
    return Result(_path(a["in_network_diagram_layer"]))


def _require(layout, params, tool):
    if layout == "PartialOverlappingEdges":
        for key in ("buffer_width", "offset"):
            if key not in params:
                raise ExecuteError(f"ERROR 000735: {tool}: {key}_absolute is required.")


def _add_layout(layout, a, tool):
    """An Add … Layout tool: the layout appended to the template's automatic layouts, its values preset."""
    network_path = _network_path(a["in_utility_network"])
    given = _LAYOUTS[layout](a, _Units(network_path, tool))
    _require(layout, given, tool)
    return _add_step(a, {"kind": "layout", "layout": layout, "active": _active(a.get("is_active")), "params": given})


def _add_step(a, step):
    network_path = _network_path(a.get("in_utility_network", a.get("in_network")))
    with _utility.Network(network_path) as network:
        _diagram.ensure(network.connection)
        name = _diagram.add_step(network, a["template_name"], step)
    return Result(network_path, name)


# ---- templates -----------------------------------------------------------------------------------------------------

def AddDiagramTemplate(in_utility_network, template_name, ndbd_file=None, ndld_file=None):
    """A diagram template with no particular settings, as the page says, or with the rules and layouts of a .ndbd file
    that ExportDiagramTemplateDefinitions wrote. A layer definition (.ndld) is refused: it is the symbology of a
    diagram map in ArcGIS Pro's application."""
    if _given(ndld_file):
        raise NotBuiltError(f"arcpy.nd.AddDiagramTemplate: ndld_file {_LAYER_DEFINITIONS}.")
    path = _network_path(in_utility_network)
    _diagram.ensure_classes(path)
    with _utility.Network(path) as network:
        if _diagram.template(network, template_name, must=False):
            raise ExecuteError(f"ERROR 000258: the diagram template '{template_name}' already exists.")
        found = dict(_diagram.TEMPLATE_DEFAULTS)
        found["name"] = str(template_name)
        if _given(ndbd_file):
            found["steps"] = _read_ndbd(ndbd_file)
        _diagram.save_template(network, found, revise=False)
    return Result(path, str(template_name))


def AlterDiagramTemplate(in_utility_network, template_name, out_name=None, is_default_template=None,
                         are_rules_and_layouts_removed=None, are_vertices_kept=None, container_margin=None,
                         is_diagram_storage_enabled=None, is_diagram_extension_enabled=None, description=None,
                         are_layer_definitions_removed=None, are_reflexive_connectivity_associations_kept=None,
                         are_linear_containers_removed=None, reflexive_edge_radius=None,
                         is_template_associated_to_telecom_domain=None, telecom_domain_network=None,
                         are_aggregations_persisted=None):
    """A template's properties changed, as the page lists them. Changing the container margin or removing the rules
    and layouts makes its diagrams inconsistent. Removing layer definitions leaves nothing to remove: grapy keeps none.
    A telecom domain is refused."""
    if _given(telecom_domain_network):
        raise NotBuiltError(f"arcpy.nd.AlterDiagramTemplate: telecom_domain_network {_TELECOM}.")
    path = _network_path(in_utility_network)
    units = _Units(path, "AlterDiagramTemplate")
    with _utility.Network(path) as network:
        found = _diagram.template(network, template_name)
        old_name = found["name"]
        revise = False
        if _given(is_default_template):
            found["is_default"] = _flag(is_default_template, "DEFAULT_TEMPLATE", "NOT_DEFAULT_TEMPLATE",
                                        "NOT_DEFAULT_TEMPLATE", "is_default_template")
        if _given(are_rules_and_layouts_removed) and _flag(are_rules_and_layouts_removed, "REMOVE_RULES_AND_LAYOUTS",
                                                           "DO_NOT_REMOVE_RULES_AND_LAYOUTS",
                                                           "DO_NOT_REMOVE_RULES_AND_LAYOUTS"):
            found["steps"], revise = [], True
        for value, key, yes, no, default in (
                (are_vertices_kept, "keep_vertices", "KEEP_VERTICES", "DO_NOT_KEEP_VERTICES", "DO_NOT_KEEP_VERTICES"),
                (is_diagram_storage_enabled, "storage", "ENABLE_DIAGRAM_STORAGE", "DISABLE_DIAGRAM_STORAGE",
                 "ENABLE_DIAGRAM_STORAGE"),
                (is_diagram_extension_enabled, "extension", "ENABLE_DIAGRAM_EXTENSION", "DISABLE_DIAGRAM_EXTENSION",
                 "DISABLE_DIAGRAM_EXTENSION"),
                (are_reflexive_connectivity_associations_kept, "keep_reflexive",
                 "KEEP_REFLEXIVE_CONNECTIVITY_ASSOCIATIONS", "DO_NOT_KEEP_REFLEXIVE_CONNECTIVITY_ASSOCIATIONS",
                 "KEEP_REFLEXIVE_CONNECTIVITY_ASSOCIATIONS"),
                (are_linear_containers_removed, "remove_linear_containers", "REMOVE_LINEAR_CONTAINERS",
                 "DO_NOT_REMOVE_LINEAR_CONTAINERS", "DO_NOT_REMOVE_LINEAR_CONTAINERS"),
                (are_aggregations_persisted, "persist_aggregations", "PERSIST_AGGREGATIONS",
                 "DO_NOT_PERSIST_AGGREGATIONS", "PERSIST_AGGREGATIONS")):
            if _given(value):
                found[key] = _flag(value, yes, no, default, key)
        _flag(are_layer_definitions_removed, "REMOVE_LAYER_DEFINITIONS", "DO_NOT_REMOVE_LAYER_DEFINITIONS",
              "DO_NOT_REMOVE_LAYER_DEFINITIONS", "are_layer_definitions_removed")
        _flag(is_template_associated_to_telecom_domain, "ASSOCIATE_TEMPLATE_TO_TELECOM_DOMAIN",
              "DO_NOT_ASSOCIATE_TEMPLATE_TO_TELECOM_DOMAIN", "ASSOCIATE_TEMPLATE_TO_TELECOM_DOMAIN",
              "is_template_associated_to_telecom_domain")
        if _given(container_margin):
            margin = units.linear(container_margin)
            if margin != found["container_margin"]:
                found["container_margin"], revise = margin, True
        if _given(reflexive_edge_radius):
            found["reflexive_radius"] = units.linear(reflexive_edge_radius)
        if description is not None:
            found["description"] = str(description)
        if _given(out_name) and str(out_name) != old_name:
            if _diagram.template(network, out_name, must=False):
                raise ExecuteError(f"ERROR 000258: the diagram template '{out_name}' already exists.")
            network.q("delete from grapy_nd_template where network = ? and name = ?", network.name, old_name)
            network.q("update grapy_nd_diagram set template = ? where network = ? and template = ?", str(out_name),
                      network.name, old_name)
            found["name"] = str(out_name)
        if found["is_default"]:
            for other in _diagram.templates(network):
                if other != found["name"]:
                    o = _diagram.template(network, other)
                    if o["is_default"]:
                        o["is_default"] = False
                        _diagram.save_template(network, o, revise=False)
        _diagram.save_template(network, found, revise=revise)
    return Result(path, found["name"])


def DeleteDiagramTemplate(in_utility_network, template_name):
    """A template deleted, and every diagram based on it with it, as the page says."""
    path = _network_path(in_utility_network)
    with _utility.Network(path) as network:
        found = _diagram.template(network, template_name)
        for name, in network.q("select name from grapy_nd_diagram where network = ? and template = ?", network.name,
                               found["name"]).fetchall():
            _diagram.delete(network, name)
        network.q("delete from grapy_nd_template where network = ? and name = ?", network.name, found["name"])
    return Result(path)


def GetDiagramTemplateNames(in_utility_network):
    """The network's template names, ``;``-separated, as the page's sample splits them."""
    with _utility.Network(_network_path(in_utility_network)) as network:
        _diagram.ensure(network.connection)
        return Result(";".join(_diagram.templates(network)))


_NDBD = "grapy network diagram rule and layout definitions (ADR-232)"


def _read_ndbd(path):
    try:
        data = json.load(open(_path(path), encoding="utf-8"))
    except (OSError, ValueError) as said:
        raise ExecuteError(f"ERROR 000732: '{path}' is not a rule and layout definitions file grapy wrote: {said}.") \
            from None
    if not isinstance(data, dict) or data.get("format") != _NDBD:
        raise ExecuteError(f"ERROR 000732: '{path}' is not a rule and layout definitions file grapy wrote; Esri's own "
                           ".ndbd layout is not published.")
    return data["steps"]


def ExportDiagramTemplateDefinitions(in_utility_network, template_name, out_ndbd_file=None, out_ndld_file=None):
    """A template's rules and layouts written to a .ndbd file — grapy's own JSON, ``{"format", "template", "steps"}``,
    each step its tool's parameters (INFERRED, §5.9 (h): Esri's .ndbd layout is not published, so a file grapy writes
    is read by grapy, not by Pro). A layer definition file (.ndld) is refused."""
    if _given(out_ndld_file):
        raise NotBuiltError(f"arcpy.nd.ExportDiagramTemplateDefinitions: out_ndld_file {_LAYER_DEFINITIONS}.")
    path = _network_path(in_utility_network)
    with _utility.Network(path) as network:
        found = _diagram.template(network, template_name)
    if _given(out_ndbd_file):
        with open(_path(out_ndbd_file), "w", encoding="utf-8") as out:
            json.dump({"format": _NDBD, "template": found["name"], "steps": found["steps"]}, out, indent=1)
    return Result(path, found["name"])


def ImportDiagramTemplateDefinitions(in_utility_network, template_name, ndbd_file=None, ndld_file=None):
    """A .ndbd file grapy wrote replaces an existing template's rules and layouts; its diagrams become inconsistent.
    A layer definition file (.ndld) is refused."""
    if _given(ndld_file):
        raise NotBuiltError(f"arcpy.nd.ImportDiagramTemplateDefinitions: ndld_file {_LAYER_DEFINITIONS}.")
    if not _given(ndbd_file):
        raise ExecuteError("ERROR 000735: ndbd_file or ndld_file is required.")
    steps = _read_ndbd(ndbd_file)
    path = _network_path(in_utility_network)
    with _utility.Network(path) as network:
        found = _diagram.template(network, template_name)
        found["steps"] = steps
        _diagram.save_template(network, found)
    return Result(path, found["name"])


# ---- rules ---------------------------------------------------------------------------------------------------------

def _rule(a, rule, params):
    return _add_step(a, {"kind": "rule", "rule": rule, "active": _active(a.get("is_active")), "params": params})


def _sources(value, include, required_when_included=True):
    names = [_class_name(v) for v in _rows(value)]
    if include and required_when_included and not names:
        raise ExecuteError("ERROR 000735: at least one source is required when sources are included.")
    return names


def _include(value, default="INCLUDE_SOURCE_CLASSES"):
    return _choice(value, ("INCLUDE_SOURCE_CLASSES", "EXCLUDE_SOURCE_CLASSES"), default,
                   "inverse_source_selection") == "INCLUDE_SOURCE_CLASSES"


def _include_categories(value, categories):
    include = _choice(value, ("INCLUDE_CATEGORIES", "EXCLUDE_CATEGORIES"), "EXCLUDE_CATEGORIES",
                      "inverse_category_selection") == "INCLUDE_CATEGORIES"
    names = [str(c) for c in _rows(categories)]
    if include and not names:
        raise ExecuteError("ERROR 000735: at least one category is required with INCLUDE_CATEGORIES.")
    return {"by": "category", "include": include, "categories": names}


def _where(value):
    return str(value) if _given(value) else None


def _reduce_options(connectivity_options, unconnected_junctions, one_connected_junction, two_connected_junctions,
                    edges_attributes, use_digitized_direction):
    return {"connectivity": _choice(connectivity_options, ("MAX_2_CONNECTED_JUNCTIONS", "MIN_3_CONNECTED_JUNCTIONS"),
                                    "MAX_2_CONNECTED_JUNCTIONS", "connectivity_options"),
            "unconnected": _flag(unconnected_junctions, "REDUCE_UNCONNECTED_JCT", "KEEP_UNCONNECTED_JCT",
                                 "KEEP_UNCONNECTED_JCT", "unconnected_junctions"),
            "one": _flag(one_connected_junction, "REDUCE_JCT_TO_1JCT", "KEEP_JCT_TO_1JCT", "KEEP_JCT_TO_1JCT",
                         "one_connected_junction"),
            "two": _flag(two_connected_junctions, "REDUCE_JCT_TO_2JCTS", "KEEP_JCT_TO_2JCTS", "REDUCE_JCT_TO_2JCTS",
                         "two_connected_junctions"),
            "edge_attributes": [str(x) for x in _rows(edges_attributes)],
            "digitized": _flag(use_digitized_direction, "USE_DIGITIZED_DIRECTION", "IGNORE_DIGITIZED_DIRECTION",
                               "IGNORE_DIGITIZED_DIRECTION", "use_digitized_direction")}


def AddReduceJunctionRule(in_utility_network, template_name, is_active="ACTIVE",
                          inverse_source_selection="INCLUDE_SOURCE_CLASSES", junction_source=None,
                          connectivity_options="MAX_2_CONNECTED_JUNCTIONS", unconnected_junctions="KEEP_UNCONNECTED_JCT",
                          one_connected_junction="KEEP_JCT_TO_1JCT", two_connected_junctions="REDUCE_JCT_TO_2JCTS",
                          edges_attributes=None, description=None, use_digitized_direction="IGNORE_DIGITIZED_DIRECTION"):
    """A Reduce Junction rule over junction sources, included in priority order or excluded (none excluded: every
    junction). A junction is reduced by its count of connected junctions: none — removed; one — reduced onto it with
    its edge; two — it and its edges replaced by a reduction edge; three or more — reduced onto its one upstream
    junction. ``SystemJunctions`` names the system junctions."""
    include = _include(inverse_source_selection)
    selector = {"by": "source", "include": include, "sources": _sources(junction_source, include)}
    return _rule(locals(), "ReduceJunction", {"selector": selector, **_reduce_options(
        connectivity_options, unconnected_junctions, one_connected_junction, two_connected_junctions, edges_attributes,
        use_digitized_direction)})


def AddReduceJunctionByAttributeRule(in_utility_network, template_name, is_active="ACTIVE", junction_source=None,
                                     where_clause=None, connectivity_options="MAX_2_CONNECTED_JUNCTIONS",
                                     unconnected_junctions="KEEP_UNCONNECTED_JCT",
                                     one_connected_junction="KEEP_JCT_TO_1JCT",
                                     two_connected_junctions="REDUCE_JCT_TO_2JCTS", edges_attributes=None,
                                     description=None, use_digitized_direction="IGNORE_DIGITIZED_DIRECTION"):
    """A Reduce Junction rule over a junction source's features a where clause selects."""
    if not _given(junction_source):
        raise ExecuteError("ERROR 000735: junction_source is required.")
    selector = {"by": "attribute", "source": _class_name(junction_source), "where": _where(where_clause)}
    return _rule(locals(), "ReduceJunction", {"selector": selector, **_reduce_options(
        connectivity_options, unconnected_junctions, one_connected_junction, two_connected_junctions, edges_attributes,
        use_digitized_direction)})


def AddReduceJunctionByCategoryRule(in_utility_network, template_name, is_active="ACTIVE",
                                    inverse_category_selection="EXCLUDE_CATEGORIES", category=None,
                                    connectivity_options="MAX_2_CONNECTED_JUNCTIONS",
                                    unconnected_junctions="KEEP_UNCONNECTED_JCT",
                                    one_connected_junction="KEEP_JCT_TO_1JCT",
                                    two_connected_junctions="REDUCE_JCT_TO_2JCTS", edges_attributes=None,
                                    description=None, use_digitized_direction="IGNORE_DIGITIZED_DIRECTION"):
    """A Reduce Junction rule over the junctions of network categories, or all but them."""
    selector = _include_categories(inverse_category_selection, category)
    return _rule(locals(), "ReduceJunction", {"selector": selector, **_reduce_options(
        connectivity_options, unconnected_junctions, one_connected_junction, two_connected_junctions, edges_attributes,
        use_digitized_direction)})


def AddReduceEdgeByAttributeRule(in_utility_network, template_name, is_active="ACTIVE", network_source=None,
                                 where_clause=None, description=None,
                                 reconnected_edges_option="AGGREGATE_RECONNECTED_EDGES"):
    """A Reduce Edge rule: each selected line or edge object, with its connection points, becomes one reduction
    junction; edges that met them are reconnected to it, or (AGGREGATE_RECONNECTED_EDGES) replaced by one reduction
    edge per junction they lead to."""
    if not _given(network_source):
        raise ExecuteError("ERROR 000735: network_source is required.")
    return _rule(locals(), "ReduceEdge", {"source": _class_name(network_source), "where": _where(where_clause),
                                          "aggregate": _flag(reconnected_edges_option, "AGGREGATE_RECONNECTED_EDGES",
                                                             "DONT_AGGREGATE_RECONNECTED_EDGES",
                                                             "AGGREGATE_RECONNECTED_EDGES")})


def _connectivity_constraints(unconnected_junctions, one_connected_junction):
    return {"unconnected": _flag(unconnected_junctions, "MUST_BE_UNCONNECTED", "NO_CONSTRAINT", "NO_CONSTRAINT",
                                 "unconnected_junctions"),
            "single": _flag(one_connected_junction, "MUST_BE_CONNECTED_TO_SINGLE_JUNCTION", "NO_CONSTRAINT",
                            "NO_CONSTRAINT", "one_connected_junction")}


_SOURCE_TYPES = ("JUNCTIONS", "EDGES", "BOTH")


def AddRemoveFeatureRule(in_utility_network, template_name, is_active="ACTIVE", source_type="BOTH",
                         inverse_source_selection="INCLUDE_SOURCE_CLASSES", network_source=None, description=None,
                         unconnected_junctions="NO_CONSTRAINT", one_connected_junction="NO_CONSTRAINT"):
    """A Remove Feature rule: the listed sources' features removed (or every other's), junctions — when the source type
    is JUNCTIONS — only if unconnected or connected to a single junction as asked. A removed junction takes its edges
    with it (INFERRED, §5.9 (c))."""
    include = _include(inverse_source_selection)
    return _rule(locals(), "RemoveFeature", {
        "selector": {"by": "source", "include": include, "sources": _sources(network_source, include)},
        "source_type": _choice(source_type, _SOURCE_TYPES, "BOTH", "source_type"),
        **_connectivity_constraints(unconnected_junctions, one_connected_junction)})


def AddRemoveFeatureByAttributeRule(in_utility_network, template_name, is_active="ACTIVE", network_source=None,
                                    where_clause=None, description=None, unconnected_junctions="NO_CONSTRAINT",
                                    one_connected_junction="NO_CONSTRAINT"):
    """A Remove Feature rule over a source's features a where clause selects."""
    if not _given(network_source):
        raise ExecuteError("ERROR 000735: network_source is required.")
    name = _class_name(network_source)
    with _utility.Network(_network_path(in_utility_network)) as network:
        role = "SystemJunction" if name.lower() == "systemjunctions" else network.role(network.table(name))
    kind = "JUNCTIONS" if role not in _utility.LINE_ROLES + ("EdgeObject",) else "EDGES"
    return _rule(locals(), "RemoveFeature", {
        "selector": {"by": "attribute", "source": name, "where": _where(where_clause)}, "source_type": kind,
        **_connectivity_constraints(unconnected_junctions, one_connected_junction)})


def AddRemoveFeatureByCategoryRule(in_utility_network, template_name, is_active="ACTIVE", source_type="BOTH",
                                   inverse_category_selection="EXCLUDE_CATEGORIES", category=None,
                                   unconnected_junctions="NO_CONSTRAINT", one_connected_junction="NO_CONSTRAINT",
                                   description=None):
    """A Remove Feature rule over the features of network categories, or all but them."""
    return _rule(locals(), "RemoveFeature", {
        "selector": _include_categories(inverse_category_selection, category),
        "source_type": _choice(source_type, _SOURCE_TYPES, "BOTH", "source_type"),
        **_connectivity_constraints(unconnected_junctions, one_connected_junction)})


def _aggregate(value):
    return _flag(value, "AGGREGATE_RECONNECTED_EDGES", "DONT_AGGREGATE_RECONNECTED_EDGES",
                 "AGGREGATE_RECONNECTED_EDGES", "reconnected_edges_option")


def AddCollapseContainerRule(in_utility_network, template_name, is_active="ACTIVE", container_type="BOTH",
                             inverse_source_selection="EXCLUDE_SOURCE_CLASSES", container_sources=None,
                             description=None, reconnected_edges_option="AGGREGATE_RECONNECTED_EDGES"):
    """A Collapse Container rule: each container's content aggregated into it, edges from the content reconnected to
    the collapsed container or (by default) replaced by one reduction edge per junction they lead to. With the
    defaults every container is collapsed."""
    include = _include(inverse_source_selection, "EXCLUDE_SOURCE_CLASSES")
    return _rule(locals(), "CollapseContainer", {
        "selector": {"by": "source", "include": include, "sources": _sources(container_sources, include)},
        "container_type": _choice(container_type, _SOURCE_TYPES, "BOTH", "container_type"),
        "aggregate": _aggregate(reconnected_edges_option)})


def AddCollapseContainerByAttributeRule(in_utility_network, template_name, is_active="ACTIVE", container_source=None,
                                        where_clause=None, description=None,
                                        reconnected_edges_option="AGGREGATE_RECONNECTED_EDGES"):
    """A Collapse Container rule over a container source's features a where clause selects."""
    if not _given(container_source):
        raise ExecuteError("ERROR 000735: container_source is required.")
    return _rule(locals(), "CollapseContainer", {
        "selector": {"by": "attribute", "source": _class_name(container_source), "where": _where(where_clause)},
        "container_type": "BOTH", "aggregate": _aggregate(reconnected_edges_option)})


def AddCollapseContainerByCategoryRule(in_utility_network, template_name, is_active="ACTIVE", container_type="BOTH",
                                       inverse_category_selection="EXCLUDE_CATEGORIES", category=None,
                                       reconnected_edges_option="AGGREGATE_RECONNECTED_EDGES", description=None):
    """A Collapse Container rule over the containers of network categories, or all but them."""
    return _rule(locals(), "CollapseContainer", {
        "selector": _include_categories(inverse_category_selection, category),
        "container_type": _choice(container_type, _SOURCE_TYPES, "BOTH", "container_type"),
        "aggregate": _aggregate(reconnected_edges_option)})


def _hide(value):
    return _flag(value, "HIDE", "KEEP_VISIBLE", "KEEP_VISIBLE", "containers_visibility")


def AddExpandContainerRule(in_utility_network, template_name, is_active="ACTIVE", containers_visibility="KEEP_VISIBLE",
                           container_type="BOTH", inverse_source_selection="EXCLUDE_SOURCE_CLASSES",
                           container_sources=None, description=None):
    """An Expand Container rule: each container's content — the features whose containment association names it —
    added, the container kept visible or hidden (removed, its content no longer contained, INFERRED)."""
    include = _include(inverse_source_selection, "EXCLUDE_SOURCE_CLASSES")
    return _rule(locals(), "ExpandContainer", {
        "selector": {"by": "source", "include": include, "sources": _sources(container_sources, include)},
        "container_type": _choice(container_type, _SOURCE_TYPES, "BOTH", "container_type"),
        "hide": _hide(containers_visibility)})


def AddExpandContainerByAttributeRule(in_utility_network, template_name, is_active="ACTIVE",
                                      containers_visibility="KEEP_VISIBLE", container_source=None, where_clause=None,
                                      description=None):
    """An Expand Container rule over a container source's features a where clause selects."""
    if not _given(container_source):
        raise ExecuteError("ERROR 000735: container_source is required.")
    return _rule(locals(), "ExpandContainer", {
        "selector": {"by": "attribute", "source": _class_name(container_source), "where": _where(where_clause)},
        "container_type": "BOTH", "hide": _hide(containers_visibility)})


def AddExpandContainerByCategoryRule(in_utility_network, template_name, is_active="ACTIVE",
                                     containers_visibility="KEEP_VISIBLE", container_type="BOTH",
                                     inverse_category_selection="EXCLUDE_CATEGORIES", category=None, description=None):
    """An Expand Container rule over the containers of network categories, or all but them."""
    return _rule(locals(), "ExpandContainer", {
        "selector": _include_categories(inverse_category_selection, category),
        "container_type": _choice(container_type, _SOURCE_TYPES, "BOTH", "container_type"),
        "hide": _hide(containers_visibility)})


def AddConnectivityAssociationsRule(in_utility_network, template_name, is_active="ACTIVE", description=None):
    """Junction-junction connectivity associations drawn as straight edges between two junctions in the diagram."""
    return _rule(locals(), "ConnectivityAssociations", {})


def AddStructuralAttachmentsRule(in_utility_network, template_name, is_active="ACTIVE", description=None):
    """Structural attachments drawn as straight edges from the structure to the attached feature, both in the
    diagram."""
    return _rule(locals(), "StructuralAttachments", {})


def AddStartIterationRule(in_utility_network, template_name, is_active="ACTIVE", description=None):
    """The start of a rule sequence run again while it changes the diagram, up to 100 times (INFERRED)."""
    return _rule(locals(), "StartIteration", {})


def AddStopIterationRule(in_utility_network, template_name, is_active="ACTIVE", description=None):
    """The end of the sequence a Start Iteration rule began."""
    return _rule(locals(), "StopIteration", {})


def AddSetRootJunctionByAttributeRule(in_utility_network, template_name, is_active="ACTIVE", junction_source=None,
                                      where_clause=None, description=None):
    """The selected junctions flagged as root junctions, which the tree layouts start from."""
    if not _given(junction_source):
        raise ExecuteError("ERROR 000735: junction_source is required.")
    return _rule(locals(), "SetRootJunction", {"source": _class_name(junction_source), "where": _where(where_clause)})


def AddSetStartingPointByAttributeRule(in_utility_network, template_name, is_active="ACTIVE", network_source=None,
                                       where_clause=None, junction_terminals=None, description=None):
    """The selected features — at the terminals given — made the starting points of the next Trace rule."""
    if not _given(network_source):
        raise ExecuteError("ERROR 000735: network_source is required.")
    return _rule(locals(), "SetStartingPoint", {"source": _class_name(network_source), "where": _where(where_clause),
                                                "terminals": [int(t) for t in _rows(junction_terminals)]})


_CAPABILITIES = ("PREVENT_TO_COLLAPSE_CONTAINER", "ALLOW_TO_COLLAPSE_CONTAINER", "PREVENT_TO_REDUCE_JUNCTION",
                 "ALLOW_TO_REDUCE_JUNCTION", "SAVE_EMPTY_CONTAINER_AS_POLYGON_IF_UNCONNECTED",
                 "IGNORED_STARTING_POINT_FOR_UPSTREAM_TRACE", "IGNORED_STARTING_POINT_FOR_DOWNSTREAM_TRACE")


def _valences(*values):
    return [_flag(v, "APPLY", "DO_NOT_APPLY", "APPLY", "valence") for v in values]


def AddDiagramFeatureCapabilityByAttributeRule(in_utility_network, template_name, is_active="ACTIVE",
                                               network_source=None, where_clause=None,
                                               capability="PREVENT_TO_COLLAPSE_CONTAINER", description=None,
                                               valence_0="APPLY", valence_1="APPLY", valence_2="APPLY",
                                               valence_3_plus="APPLY"):
    """A capability set on the selected features — junctions only at the valences applied — for the later rules to
    read: collapse or reduce prevented or allowed, an empty unconnected container saved as a polygon, a feature not a
    starting point of upstream or downstream traces."""
    if not _given(network_source):
        raise ExecuteError("ERROR 000735: network_source is required.")
    return _rule(locals(), "Capability", {
        "selector": {"by": "attribute", "source": _class_name(network_source), "where": _where(where_clause)},
        "capability": _choice(capability, _CAPABILITIES, "PREVENT_TO_COLLAPSE_CONTAINER", "capability"),
        "valences": _valences(valence_0, valence_1, valence_2, valence_3_plus)})


def AddDiagramFeatureCapabilityByCategoryRule(in_utility_network, template_name, is_active="ACTIVE",
                                              inverse_category_selection="EXCLUDE_CATEGORIES", category=None,
                                              capability="PREVENT_TO_COLLAPSE_CONTAINER", description=None,
                                              valence_0="APPLY", valence_1="APPLY", valence_2="APPLY",
                                              valence_3_plus="APPLY"):
    """A capability set on the features of network categories, or all but them."""
    return _rule(locals(), "Capability", {
        "selector": _include_categories(inverse_category_selection, category),
        "capability": _choice(capability, _CAPABILITIES, "PREVENT_TO_COLLAPSE_CONTAINER", "capability"),
        "valences": _valences(valence_0, valence_1, valence_2, valence_3_plus)})


_OVERLAPS = ("INTERSECT", "WITHIN_A_DISTANCE", "CONTAINS", "WITHIN", "BOUNDARY_TOUCHES", "SHARE_A_LINE_SEGMENT_WITH",
             "CROSSED_BY_THE_OUTLINE_OF")


def AddSpatialQueryRule(in_utility_network, template_name, is_active="ACTIVE", added_features=None,
                        overlap_type="INTERSECT", existing_features=None, search_distance=None,
                        added_where_clause=None, existing_where_clause=None, description=None):
    """A Spatial Query rule: the features of ``added_features`` that meet, by the overlap type, a feature of
    ``existing_features`` already in the diagram are added to it."""
    if not _given(added_features) or not _given(existing_features):
        raise ExecuteError("ERROR 000735: added_features and existing_features are required.")
    overlap = _choice(overlap_type, _OVERLAPS, "INTERSECT", "overlap_type")
    distance = None
    if _given(search_distance):
        if overlap not in ("INTERSECT", "WITHIN_A_DISTANCE", "CONTAINS", "WITHIN"):
            raise ExecuteError(f"ERROR 000800: a search distance is not valid with {overlap}.")
        distance = _Units(_network_path(in_utility_network), "AddSpatialQueryRule").linear(search_distance)
    return _rule(locals(), "SpatialQuery", {"added": _class_name(added_features), "overlap": overlap,
                                            "existing": _class_name(existing_features), "distance": distance,
                                            "added_where": _where(added_where_clause),
                                            "existing_where": _where(existing_where_clause)})


_TRACE_TYPES = ("CONNECTED", "SUBNETWORK", "UPSTREAM", "DOWNSTREAM", "SHORTEST_PATH", "PATH")


def AddTraceRule(in_utility_network, template_name, is_active="ACTIVE", trace_type="CONNECTED", domain_network=None,
                 tier=None, target_tier=None, include_structures="EXCLUDE_STRUCTURES",
                 include_barriers="INCLUDE_BARRIERS", condition_barriers=None, function_barriers=None,
                 traversability_scope="BOTH_JUNCTIONS_AND_EDGES", filter_barriers=None, filter_function_barriers=None,
                 filter_scope="BOTH_JUNCTIONS_AND_EDGES", filter_bitset_network_attribute_name=None,
                 filter_nearest="DO_NOT_FILTER", nearest_count=None, nearest_cost_network_attribute=None,
                 nearest_categories=None, nearest_assets=None, propagators=None, description=None,
                 allow_indeterminate_flow="IGNORE_INDETERMINATE_FLOW", path_direction=None,
                 path_network_weight_name=None, use_trace_config="DO_NOT_USE_TRACE_CONFIGURATION",
                 trace_config_name=None, use_digitized_direction="IGNORE_DIGITIZED_DIRECTION", output_assettypes=None,
                 output_conditions=None, num_paths=None, max_hops=None, infer_connectivity="DO_NOT_INFER_CONNECTIVITY"):
    """A Trace rule: the trace — run with grapy's ``un.Trace`` rules (ADR-231) — from the diagram's features, the
    starting points a Set Starting Point rule chose, or the trace locations the diagram was created from; what it
    returns becomes the diagram's content. A named trace configuration's settings replace the rule's. CIRCUIT and
    infer_connectivity are refused for the telecom domain network, path_direction for the trace networks grapy does
    not hold."""
    a = dict(locals())
    if _flag(use_trace_config, "USE_TRACE_CONFIGURATION", "DO_NOT_USE_TRACE_CONFIGURATION",
             "DO_NOT_USE_TRACE_CONFIGURATION", "use_trace_config"):
        if not _given(trace_config_name):
            raise ExecuteError("ERROR 000735: trace_config_name is required to use a trace configuration.")
        with _utility.Network(_network_path(in_utility_network)) as network:
            row = network.q("select trace_type, configuration from grapy_un_trace_config where network = ? and "
                            "lower(name) = lower(?)", network.name, str(trace_config_name)).fetchone()
        if row is None:
            raise ExecuteError(f"ERROR 000732: '{trace_config_name}' is not a trace configuration of the network.")
        a["trace_type"] = row[0]
        a.update({k: v for k, v in json.loads(row[1]).items() if k in a})
    kind = _word(a["trace_type"], "CONNECTED")
    if kind == "CIRCUIT" or _flag(a["infer_connectivity"], "INFER_CONNECTIVITY", "DO_NOT_INFER_CONNECTIVITY",
                                  "DO_NOT_INFER_CONNECTIVITY"):
        raise NotBuiltError(f"arcpy.nd.AddTraceRule: the circuit trace and infer_connectivity {_TELECOM}.")
    if _given(a["path_direction"]) and _word(a["path_direction"], "") != "NO_DIRECTION":
        raise NotBuiltError("arcpy.nd.AddTraceRule: path_direction directs a trace network's shortest path, and grapy "
                            "holds no trace networks (only utility networks, ADR-231).")
    if kind not in _TRACE_TYPES:
        raise ExecuteError(f"ERROR 000800: '{a['trace_type']}' is not a trace type of the Trace rule.")
    digitized = _flag(a["use_digitized_direction"], "USE_DIGITIZED_DIRECTION", "IGNORE_DIGITIZED_DIRECTION",
                      "IGNORE_DIGITIZED_DIRECTION") and kind in ("UPSTREAM", "DOWNSTREAM")
    if kind in ("SUBNETWORK", "UPSTREAM", "DOWNSTREAM") and not digitized and not (
            _given(a["domain_network"]) and _given(a["tier"])):
        raise ExecuteError(f"ERROR 000735: a {kind.lower()} Trace rule needs domain_network and tier.")
    if kind == "SHORTEST_PATH" and not _given(a["path_network_weight_name"]):
        raise ExecuteError("ERROR 000735: a shortest path Trace rule needs path_network_weight_name.")
    nearest = None
    if _flag(a["filter_nearest"], "FILTER_BY_NEAREST", "DO_NOT_FILTER", "DO_NOT_FILTER"):
        categories, assets = [str(c) for c in _rows(a["nearest_categories"])], [str(x) for x in _rows(a["nearest_assets"])]
        if not _given(a["nearest_count"]) or not _given(a["nearest_cost_network_attribute"]) or not (categories or assets):
            raise ExecuteError("ERROR 000735: filtering by nearest needs a count, a cost network attribute and "
                               "categories or asset types.")
        nearest = [int(a["nearest_count"]), str(a["nearest_cost_network_attribute"]), categories, assets]
    table = _utility.value_table
    request = {
        "domain_network": None if digitized or not _given(a["domain_network"]) else str(a["domain_network"]),
        "tier": None if digitized or not _given(a["tier"]) else str(a["tier"]),
        "target_tier": None if digitized or not _given(a["target_tier"]) else str(a["target_tier"]),
        "condition_barriers": table(a["condition_barriers"], 5), "function_barriers": table(a["function_barriers"], 5),
        "filter_barriers": table(a["filter_barriers"], 5),
        "filter_function_barriers": table(a["filter_function_barriers"], 5),
        "propagators": [] if digitized else table(a["propagators"], 6),
        "traversability_scope": _word(a["traversability_scope"], "BOTH_JUNCTIONS_AND_EDGES"),
        "filter_scope": _word(a["filter_scope"], "BOTH_JUNCTIONS_AND_EDGES"),
        "include_barriers": _flag(a["include_barriers"], "INCLUDE_BARRIERS", "EXCLUDE_BARRIERS", "INCLUDE_BARRIERS"),
        "allow_indeterminate_flow": _flag(a["allow_indeterminate_flow"], "TRACE_INDETERMINATE_FLOW",
                                          "IGNORE_INDETERMINATE_FLOW", "IGNORE_INDETERMINATE_FLOW"),
        "shortest_path_attribute": str(a["path_network_weight_name"]) if _given(a["path_network_weight_name"]) else None,
        "num_paths": int(a["num_paths"]) if _given(a["num_paths"]) else None,
        "max_hops": int(a["max_hops"]) if _given(a["max_hops"]) else None,
        "filter_bitset": str(a["filter_bitset_network_attribute_name"])
        if _given(a["filter_bitset_network_attribute_name"]) and kind in ("UPSTREAM", "DOWNSTREAM") else None,
        "nearest": nearest, "digitized": digitized}
    return _rule(a, "Trace", {
        "trace_type": kind, "request": request,
        "include_structures": _flag(a["include_structures"], "INCLUDE_STRUCTURES", "EXCLUDE_STRUCTURES",
                                    "EXCLUDE_STRUCTURES"),
        "output_assettypes": [str(r).strip("'\"") for r in _rows(a["output_assettypes"])],
        "output_conditions": table(a["output_conditions"], 5)})


def AddDiagramFeatureSelectionToLayout(in_network, template_name, is_active="ACTIVE",
                                       selection_type="SELECT_BY_ATTRIBUTES", network_source=None, category=None,
                                       source_by_attribute=None, where_clause=None,
                                       invert_selection="DO_NOT_INVERT_SELECTION",
                                       only_features_contained="APPLY_TO_ALL_FEATURES", description=None):
    """The diagram features the next active layout of the template applies to — by attributes, category or source,
    inverted, or only contained ones; several in a row add up."""
    kind = _choice(selection_type, ("SELECT_BY_ATTRIBUTES", "SELECT_BY_CATEGORY", "SELECT_BY_SOURCE"),
                   "SELECT_BY_ATTRIBUTES", "selection_type")
    params = {"selection_type": kind,
              "invert": _flag(invert_selection, "INVERT_SELECTION", "DO_NOT_INVERT_SELECTION",
                              "DO_NOT_INVERT_SELECTION", "invert_selection"),
              "contained": _flag(only_features_contained, "APPLY_ONLY_TO_FEATURES_CONTAINED", "APPLY_TO_ALL_FEATURES",
                                 "APPLY_TO_ALL_FEATURES", "only_features_contained")}
    if kind == "SELECT_BY_ATTRIBUTES":
        if not _given(source_by_attribute):
            raise ExecuteError("ERROR 000735: source_by_attribute is required to select by attributes.")
        params.update(source=_class_name(source_by_attribute), where=_where(where_clause))
    elif kind == "SELECT_BY_CATEGORY":
        params["categories"] = [str(c) for c in _rows(category)]
    else:
        params["sources"] = [_class_name(s) for s in _rows(network_source)]
    a = dict(locals())
    a["in_utility_network"] = in_network
    return _add_step(a, {"kind": "selection", "active": _active(is_active), "params": params})


# ---- layouts added to a template -----------------------------------------------------------------------------------

def AddGridLayout(in_utility_network, template_name, is_active="ACTIVE", are_containers_preserved=None,
                  cell_width_absolute=None, cell_height_absolute=None):
    """The Grid layout added to the template's automatic layouts (ApplyGridLayout says what it does)."""
    return _add_layout("Grid", locals(), "AddGridLayout")


def AddLinearDispatchLayout(in_utility_network, template_name, is_active="ACTIVE", junction_placement_type=None,
                            is_unit_absolute=None, maximum_shift_absolute=None, maximum_shift_proportional=None,
                            minimum_shift_absolute=None, minimum_shift_proportional=None, iterations_number=None,
                            is_path_preserved=None, are_leaves_moved=None, are_leaves_expanded=None,
                            expand_shift_absolute=None, expand_shift_proportional=None):
    """The Linear Dispatch layout added to the template's automatic layouts."""
    return _add_layout("LinearDispatch", locals(), "AddLinearDispatchLayout")


@equivalent(*_ndlayout.DISPATCH)
def AddSpatialDispatchLayout(in_utility_network, template_name, is_active="ACTIVE", are_containers_preserved=None,
                             iterations_number=None, maximum_shift_factor=None):
    """EQUIVALENT (ApplySpatialDispatchLayout): the Spatial Dispatch layout added to the template's layouts."""
    return _add_layout("SpatialDispatch", locals(), "AddSpatialDispatchLayout")


@equivalent(*_ndlayout.COMPRESS)
def AddCompressionLayout(in_utility_network, template_name, is_active="ACTIVE", are_containers_preserved=None,
                         grouping_distance_absolute=None, vertices_removal_rule=None):
    """EQUIVALENT (ApplyCompressionLayout): the Compression layout added to the template's layouts."""
    return _add_layout("Compression", locals(), "AddCompressionLayout")


def AddAngleDirectedLayout(in_utility_network, template_name, is_active="ACTIVE", are_containers_preserved=None,
                           iterations_number=None, number_of_directions=None):
    """The Angle Directed layout added to the template's automatic layouts."""
    return _add_layout("AngleDirected", locals(), "AddAngleDirectedLayout")


@equivalent(*_ndlayout.FORCE)
def AddForceDirectedLayout(in_utility_network, template_name, is_active="ACTIVE", are_containers_preserved=None,
                           iterations_number=None, repel_factor=None, degree_freedom=None, breakpoint_position=None,
                           edge_display_type=None):
    """EQUIVALENT (ApplyForceDirectedLayout): the Force Directed layout added to the template's layouts."""
    return _add_layout("ForceDirected", locals(), "AddForceDirectedLayout")


@equivalent(*_ndlayout.TIDY)
def AddSmartTreeLayout(in_utility_network, template_name, is_active="ACTIVE", are_containers_preserved=None,
                       tree_direction=None, is_unit_absolute=None, subtree_absolute=None, subtree_proportional=None,
                       perpendicular_absolute=None, perpendicular_proportional=None, along_absolute=None,
                       along_proportional=None, disjoined_graph_absolute=None, disjoined_graph_proportional=None,
                       are_edges_orthogonal=None, breakpoint_position=None, edge_display_type=None,
                       offset_absolute=None, offset_proportional=None, order_tree_branches=None,
                       attribute_availability=None, attribute_name=None, attribute_sort_order=None):
    """EQUIVALENT (ApplySmartTreeLayout): the Smart Tree layout added to the template's layouts."""
    return _add_layout("SmartTree", locals(), "AddSmartTreeLayout")


@equivalent(*_ndlayout.TIDY)
def AddMainlineTreeLayout(in_utility_network, template_name, is_active="ACTIVE", are_containers_preserved=None,
                          tree_direction=None, branches_placement=None, is_unit_absolute=None,
                          perpendicular_absolute=None, perpendicular_proportional=None, along_absolute=None,
                          along_proportional=None, disjoined_graph_absolute=None, disjoined_graph_proportional=None,
                          are_edges_orthogonal=None, breakpoint_position=None, edge_display_type=None,
                          offset_absolute=None, offset_proportional=None):
    """EQUIVALENT (ApplyMainlineTreeLayout): the Mainline Tree layout added to the template's layouts."""
    return _add_layout("MainlineTree", locals(), "AddMainlineTreeLayout")


@equivalent(*_ndlayout.RADIAL)
def AddRadialTreeLayout(in_utility_network, template_name, is_active="ACTIVE", are_containers_preserved=None,
                        is_unit_absolute=None, initial_radius_absolute=None, initial_radius_proportional=None,
                        disjoined_graph_absolute=None, disjoined_graph_proportional=None, radius_factor=None):
    """EQUIVALENT (ApplyRadialTreeLayout): the Radial Tree layout added to the template's layouts."""
    return _add_layout("RadialTree", locals(), "AddRadialTreeLayout")


@equivalent(*_ndlayout.RING)
def AddMainRingLayout(in_utility_network, template_name, is_active="ACTIVE", are_containers_preserved=None,
                      ring_type=None, is_unit_absolute=None, ring_width_absolute=None, ring_width_proportional=None,
                      ring_height_absolute=None, ring_height_proportional=None, tree_type=None,
                      perpendicular_absolute=None, perpendicular_proportional=None, along_absolute=None,
                      along_proportional=None, breakpoint_position=None, edge_display_type=None, offset_absolute=None,
                      offset_proportional=None):
    """EQUIVALENT (ApplyMainRingLayout): the Main Ring layout added to the template's layouts."""
    return _add_layout("MainRing", locals(), "AddMainRingLayout")


def AddPartialOverlappingEdgesLayout(in_utility_network, template_name, is_active="ACTIVE", buffer_width_absolute=None,
                                     offset_absolute=None, optimize_edges=None):
    """The Partial Overlapping Edges layout added to the template's automatic layouts."""
    return _add_layout("PartialOverlappingEdges", locals(), "AddPartialOverlappingEdgesLayout")


def AddReshapeDiagramEdgesLayout(in_utility_network, template_name, is_active="ACTIVE", are_containers_preserved=None,
                                 reshape_type="SQUARE_EDGES", is_path_preserved=None,
                                 offset_between_segment_absolute=None, breakpoint_absolute=None,
                                 shift_between_edge_absolute=None, angle_threshold=None, circular_arc_radius=None,
                                 circular_arc_position=None):
    """The Reshape Diagram Edges layout added to the template's automatic layouts."""
    return _add_layout("ReshapeEdges", locals(), "AddReshapeDiagramEdgesLayout")


# ---- layouts applied to a diagram ----------------------------------------------------------------------------------

def ApplyGeoPositionsLayout(in_network_diagram_layer, restore_edges_geo_positions=None, run_async=None):
    """Each junction and edge moved back to its network feature's geographic position; edge vertices restored where
    an edge is one piece of a line, else straight; reduction edges always straight."""
    return _apply("GeoPositions", locals(), "ApplyGeoPositionsLayout")


def ApplyGridLayout(in_network_diagram_layer, are_containers_preserved=None, cell_width_absolute=None,
                    cell_height_absolute=None, run_async=None):
    """Junctions snapped to the grid of the cell width and height (default 2 units), a taken node sending the next
    junction to the nearest free node (INFERRED)."""
    return _apply("Grid", locals(), "ApplyGridLayout")


def ApplyLinearDispatchLayout(in_network_diagram_layer, junction_placement_type=None, is_unit_absolute=None,
                              maximum_shift_absolute=None, maximum_shift_proportional=None,
                              minimum_shift_absolute=None, minimum_shift_proportional=None, iterations_number=None,
                              is_path_preserved=None, are_leaves_moved=None, are_leaves_expanded=None,
                              expand_shift_absolute=None, expand_shift_proportional=None, run_async=None):
    """Two-edge junctions spaced along their edges' path (``_ndlayout.linear_dispatch``)."""
    return _apply("LinearDispatch", locals(), "ApplyLinearDispatchLayout")


@equivalent(*_ndlayout.DISPATCH)
def ApplySpatialDispatchLayout(in_network_diagram_layer, are_containers_preserved=None, iterations_number=None,
                               maximum_shift_factor=None, run_async=None):
    """EQUIVALENT: near junctions separated by the force-scan (``_ndlayout.spatial_dispatch``)."""
    return _apply("SpatialDispatch", locals(), "ApplySpatialDispatchLayout")


@equivalent(*_ndlayout.COMPRESS)
def ApplyCompressionLayout(in_network_diagram_layer, are_containers_preserved=None, grouping_distance_absolute=None,
                           vertices_removal_rule=None, run_async=None):
    """EQUIVALENT: junction groups compressed toward the middle, their order kept (``_ndlayout.compression``)."""
    return _apply("Compression", locals(), "ApplyCompressionLayout")


def ApplyAngleDirectedLayout(in_network_diagram_layer, are_containers_preserved=None, iterations_number=None,
                             number_of_directions=None, run_async=None):
    """Edges turned to the nearest allowed direction (``_ndlayout.angle_directed``)."""
    return _apply("AngleDirected", locals(), "ApplyAngleDirectedLayout")


@equivalent(*_ndlayout.FORCE)
def ApplyForceDirectedLayout(in_network_diagram_layer, are_containers_preserved=None, iterations_number=None,
                             repel_factor=None, degree_freedom=None, breakpoint_position=None, edge_display_type=None,
                             run_async=None):
    """EQUIVALENT: Fruchterman–Reingold placement (``_ndlayout.force_directed``)."""
    return _apply("ForceDirected", locals(), "ApplyForceDirectedLayout")


@equivalent(*_ndlayout.TIDY)
def ApplySmartTreeLayout(in_network_diagram_layer, are_containers_preserved=None, tree_direction=None,
                         is_unit_absolute=None, subtree_absolute=None, subtree_proportional=None,
                         perpendicular_absolute=None, perpendicular_proportional=None, along_absolute=None,
                         along_proportional=None, disjoined_graph_absolute=None, disjoined_graph_proportional=None,
                         are_edges_orthogonal=None, breakpoint_position=None, edge_display_type=None, run_async=None,
                         offset_absolute=None, offset_proportional=None, order_tree_branches=None,
                         attribute_availability=None, attribute_name=None, attribute_sort_order=None):
    """EQUIVALENT: a tidy tree from the root junctions (``_ndlayout.smart_tree``)."""
    return _apply("SmartTree", locals(), "ApplySmartTreeLayout")


@equivalent(*_ndlayout.TIDY)
def ApplyMainlineTreeLayout(in_network_diagram_layer, are_containers_preserved=None, tree_direction=None,
                            branches_placement=None, is_unit_absolute=None, perpendicular_absolute=None,
                            perpendicular_proportional=None, along_absolute=None, along_proportional=None,
                            disjoined_graph_absolute=None, disjoined_graph_proportional=None,
                            are_edges_orthogonal=None, breakpoint_position=None, edge_display_type=None,
                            run_async=None, offset_absolute=None, offset_proportional=None):
    """EQUIVALENT: the longest branch as a main line, the rest as tidy trees beside it (``_ndlayout.mainline_tree``)."""
    return _apply("MainlineTree", locals(), "ApplyMainlineTreeLayout")


@equivalent(*_ndlayout.RADIAL)
def ApplyRadialTreeLayout(in_network_diagram_layer, are_containers_preserved=None, is_unit_absolute=None,
                          initial_radius_absolute=None, initial_radius_proportional=None,
                          disjoined_graph_absolute=None, disjoined_graph_proportional=None, radius_factor=None,
                          run_async=None):
    """EQUIVALENT: a radial tree on concentric circles (``_ndlayout.radial_tree``)."""
    return _apply("RadialTree", locals(), "ApplyRadialTreeLayout")


@equivalent(*_ndlayout.RING)
def ApplyMainRingLayout(in_network_diagram_layer, are_containers_preserved=None, ring_type=None, is_unit_absolute=None,
                        ring_width_absolute=None, ring_width_proportional=None, ring_height_absolute=None,
                        ring_height_proportional=None, tree_type=None, perpendicular_absolute=None,
                        perpendicular_proportional=None, along_absolute=None, along_proportional=None,
                        breakpoint_position=None, edge_display_type=None, run_async=None, offset_absolute=None,
                        offset_proportional=None):
    """EQUIVALENT: the longest loop as a ring, trees outward from it (``_ndlayout.main_ring``)."""
    return _apply("MainRing", locals(), "ApplyMainRingLayout")


def ApplyPartialOverlappingEdgesLayout(in_network_diagram_layer, buffer_width_absolute=None, offset_absolute=None,
                                       optimize_edges=None, run_async=None):
    """Collinear segments of different edges spread ``offset`` apart (``_ndlayout.partial_overlapping_edges``)."""
    return _apply("PartialOverlappingEdges", locals(), "ApplyPartialOverlappingEdgesLayout")


def ReshapeDiagramEdgesLayout(in_network_diagram_layer, are_containers_preserved=None, reshape_type="SQUARE_EDGES",
                              is_path_preserved=None, offset_between_segment_absolute=None, breakpoint_absolute=None,
                              shift_between_edge_absolute=None, angle_threshold=None, circular_arc_radius=None,
                              circular_arc_position=None, run_async=None):
    """The edge vertex operations of ``_ndlayout.reshape_edges``."""
    return _apply("ReshapeEdges", locals(), "ReshapeDiagramEdgesLayout")


def ApplyTemplateLayouts(in_network_diagram_layer):
    """The template's active layouts run again, in order, each on the diagram feature selection before it."""
    network_path, name = _diagram_path(in_network_diagram_layer)
    with _utility.Network(network_path) as network:
        d = _diagram.load(network, name)
        found = _diagram.template(network, d.template)
        builder = _diagram.Builder(network)
        builder.bind(d)
        _ndlayout.template_layouts(builder, d, found)
        d.updated, d.updated_by = _diagram.now(), _diagram.user()
        _diagram.save(network, d)
    return Result(_path(in_network_diagram_layer))


# ---- diagrams ------------------------------------------------------------------------------------------------------

def _keys_of_layers(network, values):
    """Network features (graph keys) of layers or classes: a layer's selection, or every feature it shows when nothing
    is selected; a class's every feature."""
    from . import da
    from .management import _source
    out = set()
    sources = {c: s for s, (c, _, _) in network.sources().items()}
    for value in values:
        path, where = _source(value)
        file, table = _gpkg.split(path)
        if os.path.normcase(os.path.abspath(file)) != os.path.normcase(os.path.abspath(network.file)):
            continue
        class_name = next((c for c in sources if c == table or c.rsplit("/", 1)[-1] == (table or "").rsplit("/", 1)[-1]),
                          None)
        if class_name is None:
            continue
        with da.SearchCursor(value, ["OID@"]) as rows:
            out |= {(sources[class_name], int(r[0])) for r in rows}
    return out


def _map_selection(network):
    """The map's selected network features: the selection of every grapy layer over a network class (INFERRED, §5.9
    (g): grapy has no map document, and its layers are what a tool sees of one)."""
    chosen = [layer.name for layer in _layers.LAYERS.values() if layer.kind != "DiagramLayer" and layer.selection]
    return _keys_of_layers(network, chosen) if chosen else set()


def _network_keys(builder, keys):
    """Only network features in the topology; a selection of nothing else is the pages' error."""
    kept = {k for k in keys if k in builder.graph.features}
    if not kept:
        raise ExecuteError("ERROR 002095: no network feature or object is selected: nonnetwork elements make no "
                           "diagram.")
    return kept


def _input_keys(builder, d):
    return {builder.gkey(tuple(k)) for k in d.inputs["features"]} - {None}


def _save_inputs(builder, d, keys):
    d.inputs["features"] = sorted([list(builder.nkey(k)) for k in keys])


def CreateDiagram(in_utility_network, template_name, features=None,
                  is_created_from_trace_locations="DO_NOT_CREATE_DIAGRAM_FROM_TRACE_LOCATIONS",
                  is_created_from_circuit_name="DO_NOT_CREATE_DIAGRAM_FROM_CIRCUIT_NAME", circuit_name=None):
    """A temporary diagram, named ``Temporary Diagram`` (then 2, 3 …, INFERRED), from the network features of
    ``features`` — layers' selections or the features they show, or classes — or, when none is given, the selection of
    grapy's layers. From trace locations, ``features`` are the starting point, barrier and stopping point classes in
    that order (INFERRED, §5.9 (g)), which the template's first Trace rule uses. The template's rules run, the diagram
    is placed at the geographic positions of its features, its automatic layouts run, and a diagram layer named after
    it is made. The topology must be enabled and no dirty area may meet its features. A diagram from a circuit name is
    refused."""
    if _flag(is_created_from_circuit_name, "CREATE_DIAGRAM_FROM_CIRCUIT_NAME", "DO_NOT_CREATE_DIAGRAM_FROM_CIRCUIT_NAME",
             "DO_NOT_CREATE_DIAGRAM_FROM_CIRCUIT_NAME") or _given(circuit_name):
        raise NotBuiltError(f"arcpy.nd.CreateDiagram: a diagram from a circuit name {_TELECOM}.")
    from_trace = _flag(is_created_from_trace_locations, "CREATE_DIAGRAM_FROM_TRACE_LOCATIONS",
                       "DO_NOT_CREATE_DIAGRAM_FROM_TRACE_LOCATIONS", "DO_NOT_CREATE_DIAGRAM_FROM_TRACE_LOCATIONS")
    path = _network_path(in_utility_network)
    _diagram.ensure_classes(path)
    trace = None
    if from_trace:
        from .un import _locations
        layers = _rows(features)
        if not layers:
            raise ExecuteError("ERROR 000735: a diagram from trace locations needs the starting points' class.")
        starts = _locations(layers[0])
        barriers = _locations(layers[1]) if len(layers) > 1 else []
        stops = _locations(layers[2]) if len(layers) > 2 else []
        trace = {"starts": [list(r[:3]) for r in starts], "barriers": [list(r) for r in barriers],
                 "stops": [list(r[:3]) for r in stops]}
    with _utility.Network(path) as network:
        network.require_enabled("CreateDiagram")
        found = _diagram.template(network, template_name)
        builder = _diagram.Builder(network)
        if trace:
            keys = {builder.by_globalid.get(str(r[0]).upper()) for r in trace["starts"] + trace["barriers"]} - {None}
        else:
            keys = _keys_of_layers(network, _rows(features)) if _rows(features) else _map_selection(network)
        keys = _network_keys(builder, keys)
        _diagram.check_dirty(builder, keys, "CreateDiagram")
        d = _diagram.Diagram(_diagram.temporary_name(network), found["name"])
        _save_inputs(builder, d, keys)
        d.inputs["trace"] = trace
        builder.run(d, found, keys, trace)
        _diagram.save(network, d)
    _register(d.name, path, d.name)
    return Result(path, d.name)


def MakeDiagramLayer(in_utility_network, network_diagram_name, out_layer, sublayers_option="DO_NOT_ADD_SUBLAYERS"):
    """A diagram layer over a diagram, for the tools that take one. ADD_SUBLAYERS also makes three feature layers,
    ``<out_layer>/Junctions``, ``/Edges`` and ``/Containers`` (INFERRED, §5.9 (g)), over the diagram's features in the
    network's diagram classes, which the conversion tools export as feature classes."""
    path = _network_path(in_utility_network)
    sublayers = _flag(sublayers_option, "ADD_SUBLAYERS", "DO_NOT_ADD_SUBLAYERS", "DO_NOT_ADD_SUBLAYERS",
                      "sublayers_option")
    name = str(out_layer)
    if ".gpkg" in name.lower():
        raise ExecuteError(f"ERROR 000725: a layer's name may not contain '.gpkg' in grapy: {name}.")
    _diagram.ensure_classes(path)
    with _utility.Network(path) as network:
        d = _diagram.load(network, network_diagram_name)
        classes = {s: f"{network.file}/{_diagram.class_path(network, s)}" for s, _ in _diagram.CLASSES}
    layer = _register(name, path, d.name)
    if sublayers:
        from .management import MakeFeatureLayer
        quoted = d.name.replace("'", "''")
        layer.sublayers = []
        for suffix, label in (("DiagramJunctions", "Junctions"), ("DiagramEdges", "Edges"),
                              ("DiagramContainers", "Containers")):
            MakeFeatureLayer(classes[suffix], f"{name}/{label}", f"DIAGRAMNAME = '{quoted}'")
            layer.sublayers.append(f"{name}/{label}")
    return Result(name)


def _check_writable(d, tool):
    if d.system:
        raise ExecuteError(f"ERROR 002097: {tool}: '{d.name}' is a subnetwork system diagram, which only "
                           "UpdateSubnetwork maintains.")


def _rebuild(network, builder, d, keys, keep_positions, tool):
    """A diagram made again from ``keys`` with its template: its name and properties kept, the features it already had
    at their positions when ``keep_positions`` (INFERRED, §5.9 (g)), the rest placed geographically and — for a full
    rebuild — the template's layouts run."""
    found = _diagram.template(network, d.template)
    _diagram.check_dirty(builder, keys, tool)
    fresh = _diagram.Diagram(d.name, d.template)
    for attr in ("owner", "access", "tags", "stored", "system", "created"):
        setattr(fresh, attr, getattr(d, attr))
    _save_inputs(builder, fresh, keys)
    fresh.inputs["trace"] = d.inputs.get("trace")
    keep = {f.identity(): f.shape for f in d.features.values() if f.shape is not None} if keep_positions else None
    builder.run(fresh, found, keys, d.inputs.get("trace"), keep=keep)
    fresh.updated, fresh.updated_by = _diagram.now(), _diagram.user()
    return fresh


def AppendToDiagram(in_network_diagram_layer, map=None):  # noqa: A002 — the page's own parameter name
    """The network features selected in the map — grapy's layers' selections (INFERRED) — added to the diagram's input
    and the diagram rebuilt from it, the features it had keeping their positions. A subnetwork system diagram is
    refused, as the page says."""
    network_path, name = _diagram_path(in_network_diagram_layer)
    with _utility.Network(network_path) as network:
        network.require_enabled("AppendToDiagram")
        d = _diagram.load(network, name)
        _check_writable(d, "AppendToDiagram")
        builder = _diagram.Builder(network)
        added = _network_keys(builder, _map_selection(network))
        fresh = _rebuild(network, builder, d, _input_keys(builder, d) | added, True, "AppendToDiagram")
        _diagram.save(network, fresh)
    return Result(_path(in_network_diagram_layer))


def OverwriteDiagram(in_network_diagram_layer, map=None):  # noqa: A002 — the page's own parameter name
    """The diagram's content replaced by the network features selected in the map; a temporary diagram is fully
    rebuilt, a stored one keeps the positions of the features it already held, as the page says."""
    network_path, name = _diagram_path(in_network_diagram_layer)
    with _utility.Network(network_path) as network:
        network.require_enabled("OverwriteDiagram")
        d = _diagram.load(network, name)
        _check_writable(d, "OverwriteDiagram")
        builder = _diagram.Builder(network)
        keys = _network_keys(builder, _map_selection(network))
        fresh = _rebuild(network, builder, d, keys, d.stored, "OverwriteDiagram")
        fresh.inputs["trace"] = None
        _diagram.save(network, fresh)
    return Result(_path(in_network_diagram_layer))


def UpdateDiagram(in_diagrams, template_names=None, diagram_names=None, update_option="INCONSISTENT_DIAGRAMS_ONLY",
                  autolayout_option="DO_NOT_REAPPLY_AUTOLAYOUT"):
    """Diagrams rebuilt from their input with their template's current rules — only the inconsistent ones unless
    ALL_SELECTED_DIAGRAMS — keeping the positions of the features they held, and with REAPPLY_AUTOLAYOUT the
    template's layouts run at the end. From a network, diagram names or template names are required, and names of
    another template than one named are an error, as the page says."""
    every = _choice(update_option, ("INCONSISTENT_DIAGRAMS_ONLY", "ALL_SELECTED_DIAGRAMS"), "INCONSISTENT_DIAGRAMS_ONLY",
                    "update_option") == "ALL_SELECTED_DIAGRAMS"
    reapply = _flag(autolayout_option, "REAPPLY_AUTOLAYOUT", "DO_NOT_REAPPLY_AUTOLAYOUT", "DO_NOT_REAPPLY_AUTOLAYOUT",
                    "autolayout_option")
    network_path, chosen = _which(in_diagrams, template_names, diagram_names, "UpdateDiagram")
    with _utility.Network(network_path) as network:
        network.require_enabled("UpdateDiagram")
        builder = _diagram.Builder(network)
        for name in chosen:
            d = _diagram.load(network, name)
            _check_writable(d, "UpdateDiagram")
            if not every and _diagram.consistent(network, d):
                continue
            fresh = _rebuild(network, builder, d, _input_keys(builder, d), True, "UpdateDiagram")
            if reapply:
                _ndlayout.template_layouts(builder, fresh, _diagram.template(network, fresh.template))
            _diagram.save(network, fresh)
    return Result(_path(in_diagrams))


def _which(in_diagrams, template_names, diagram_names, tool):
    """(network path, diagram names) a network-or-diagram-layer tool works on."""
    layer = _layers.find(in_diagrams)
    own = None
    if layer is not None and getattr(layer, "diagram", None):
        network_path, own = layer.network, layer.diagram
    else:
        network_path = _network_path(in_diagrams)
    templates = [str(t) for t in _rows(template_names)]
    wanted = [str(n) for n in _rows(diagram_names)]
    with _utility.Network(network_path) as network:
        _diagram.ensure(network.connection)
        rows = {r[0].lower(): r for r in network.q("select name, template from grapy_nd_diagram where network = ?",
                                                    network.name)}
        if not wanted and not templates:
            if own is None:
                raise ExecuteError(f"ERROR 000735: {tool}: from a network, diagram names or template names are "
                                   "required.")
            wanted = [own]
        if wanted:
            out = []
            for n in wanted:
                row = rows.get(n.lower())
                if row is None:
                    raise ExecuteError(f"ERROR 000732: '{n}' is not a network diagram of '{network.name}'.")
                if templates and row[1].lower() not in {t.lower() for t in templates}:
                    raise ExecuteError(f"ERROR 000800: {tool}: '{n}' is based on '{row[1]}', not on the templates "
                                       "named.")
                out.append(row[0])
            return network_path, out
        for t in templates:
            _diagram.template(network, t)
        return network_path, [r[0] for r in rows.values() if r[1].lower() in {t.lower() for t in templates}]


def ExtendDiagram(in_network_diagram_layer, ignore_traversability=None, extension_type=None):
    """The diagram extended by one network element level: by connectivity, traversability, structural attachment or
    containment, as the page says. The new features are placed geographically; the template's rules are not run on
    them (INFERRED). The template must allow extension, and a subnetwork system diagram is refused."""
    if _given(extension_type):
        kind = _choice(extension_type, ("BY_CONNECTIVITY", "BY_TRAVERSABILITY", "BY_ATTACHMENT", "BY_CONTAINMENT"),
                       "BY_CONNECTIVITY", "extension_type")
    elif _given(ignore_traversability):
        kind = "BY_CONNECTIVITY" if _flag(ignore_traversability, "IGNORE_TRAVERSABILITY", "HONOR_TRAVERSABILITY",
                                          "IGNORE_TRAVERSABILITY") else "BY_TRAVERSABILITY"
    else:
        kind = "BY_CONNECTIVITY"
    network_path, name = _diagram_path(in_network_diagram_layer)
    with _utility.Network(network_path) as network:
        network.require_enabled("ExtendDiagram")
        d = _diagram.load(network, name)
        _check_writable(d, "ExtendDiagram")
        found = _diagram.template(network, d.template)
        if not found["extension"]:
            raise ExecuteError(f"ERROR 002098: the diagram template '{found['name']}' does not allow its diagrams to "
                               "be extended (AlterDiagramTemplate's is_diagram_extension_enabled).")
        builder = _diagram.Builder(network)
        builder.bind(d)
        held = _diagram.keys_of(builder, d)
        _diagram.check_dirty(builder, held, "ExtendDiagram")
        before = set(d.features)
        if kind in ("BY_CONNECTIVITY", "BY_TRAVERSABILITY"):
            arcs = builder.graph.undirected() if kind == "BY_CONNECTIVITY" else builder.graph.arcs()
            mine = {i for f in d.features.values() for i in f.elements}
            reach = {b for a in mine for b in arcs.get(a, ())} - mine
            builder.add_elements(d, reach)
        elif kind == "BY_ATTACHMENT":
            more = set()
            for g in held:
                more |= set(builder.attachments_of.get(g, [])) | set(builder.attached_to.get(g, []))
            builder.add_features(d, more - held)
        else:
            more = set()
            for g in held:
                more |= set(builder.contents_of.get(g, []))
            builder.add_features(d, more - held)
        builder.link_containers(d)
        added = [d.features[i] for i in d.features if i not in before]
        if added:
            new_keys = {builder.gkey(f.key) for f in added if f.key} - {None}
            _diagram.check_dirty(builder, new_keys, "ExtendDiagram")
            placed = {}
            for f in added:
                if f.kind != "E":
                    f.shape = builder.junction_position(d, f, placed)
            for f in added:
                if f.kind == "E":
                    a, b = d.features[f.from_id].shape, d.features[f.to_id].shape
                    from shapely.geometry import LineString
                    f.shape = LineString([_diagram.point_of(a).coords[0], _diagram.point_of(b).coords[0]])
            _diagram.reconnect(d)
            _diagram.containers(d, found["container_margin"])
            _save_inputs(builder, d, _input_keys(builder, d) | new_keys)
        d.signature = _diagram.signature(network, d)
        d.updated, d.updated_by = _diagram.now(), _diagram.user()
        _diagram.save(network, d)
    return Result(_path(in_network_diagram_layer))


_ACCESS = ("PUBLIC", "PROTECTED", "PRIVATE")


def _access(value):
    word = _choice(value, _ACCESS, "PUBLIC", "access_right_type")
    if word != "PUBLIC":
        raise NotBuiltError(f"a {word} diagram needs a portal connection, as the Store Diagram page says of a file "
                            "geodatabase, and a user tool runs fenced, with no portal (ADR-223 §5.3).")
    return word


def StoreDiagram(in_network_diagram_layer, out_name, access_right_type="PUBLIC", tags=None):
    """A temporary diagram stored under ``out_name``, PUBLIC — the access a file geodatabase's diagrams have without a
    portal — with ``#``-separated tags. Storing fails when the template disables storage or the diagram is
    inconsistent, as the page says; the diagram layer then names the stored diagram."""
    network_path, name = _diagram_path(in_network_diagram_layer)
    try:
        access = _access(access_right_type)
    except NotBuiltError as said:
        raise NotBuiltError(f"arcpy.nd.StoreDiagram: {said}") from None
    with _utility.Network(network_path) as network:
        d = _diagram.load(network, name)
        found = _diagram.template(network, d.template)
        if not found["storage"]:
            raise ExecuteError(f"ERROR 002099: the diagram template '{found['name']}' disables storing its diagrams.")
        if not _diagram.consistent(network, d):
            raise ExecuteError(f"ERROR 002100: '{d.name}' is inconsistent; run UpdateDiagram before storing it.")
        other = _diagram.load(network, out_name, must=False)
        if other is not None and other.name != d.name:
            raise ExecuteError(f"ERROR 000725: a diagram named '{out_name}' already exists.")
        old = d.name
        d.name, d.stored, d.access = str(out_name), True, access
        d.tags = str(tags) if _given(tags) else d.tags
        d.updated, d.updated_by = _diagram.now(), _diagram.user()
        _diagram.save(network, d, rename_from=old)
    _rename_layers(network_path, old, d.name)
    return Result(_path(in_network_diagram_layer))


def AlterDiagramProperties(in_network_diagram_layer, out_name, access_right_type="PUBLIC", tags=None):
    """A stored diagram renamed, its access right and tags changed."""
    network_path, name = _diagram_path(in_network_diagram_layer)
    try:
        access = _access(access_right_type)
    except NotBuiltError as said:
        raise NotBuiltError(f"arcpy.nd.AlterDiagramProperties: {said}") from None
    with _utility.Network(network_path) as network:
        d = _diagram.load(network, name)
        if not d.stored:
            raise ExecuteError(f"ERROR 002100: '{d.name}' is a temporary diagram; AlterDiagramProperties changes stored "
                               "ones (StoreDiagram).")
        other = _diagram.load(network, out_name, must=False)
        if other is not None and other.name != d.name:
            raise ExecuteError(f"ERROR 000725: a diagram named '{out_name}' already exists.")
        old = d.name
        d.name, d.access = str(out_name), access
        if tags is not None:
            d.tags = str(tags) if _given(tags) else ""
        _diagram.save(network, d, rename_from=old)
    _rename_layers(network_path, old, d.name)
    return Result(_path(in_network_diagram_layer))


def DeleteDiagram(in_diagrams, template_names=None, diagram_names=None):
    """Diagrams deleted — by name, or every diagram of the templates named. A subnetwork system diagram is refused:
    the page leaves its deletion to the portal utility network owner, whom a fenced run is not."""
    network_path, chosen = _which(in_diagrams, template_names, diagram_names, "DeleteDiagram")
    with _utility.Network(network_path) as network:
        for name in chosen:
            d = _diagram.load(network, name)
            if d.system:
                raise ExecuteError(f"ERROR 002097: '{d.name}' is a subnetwork system diagram; only the portal utility "
                                   "network owner deletes those.")
            _diagram.delete(network, d.name)
    return Result(_path(in_diagrams))


def ChangeDiagramsOwner(in_diagrams, target_owner, source_owner=None, diagram_names=None):
    """Stored diagrams given to ``target_owner``: those named (whoever owns them), or every one ``source_owner`` owns.
    Both owners must own a stored diagram, and names match without regard to case, as the page says."""
    names = [str(n) for n in _rows(diagram_names)]
    if not names and not _given(source_owner):
        raise ExecuteError("ERROR 000735: ChangeDiagramsOwner needs diagram_names or source_owner.")
    layer = _layers.find(in_diagrams)
    network_path = layer.network if layer is not None and getattr(layer, "diagram", None) else \
        _network_path(in_diagrams)
    with _utility.Network(network_path) as network:
        _diagram.ensure(network.connection)
        rows = network.q("select name, owner from grapy_nd_diagram where network = ? and stored = 1",
                         network.name).fetchall()
        owners = {(o or "").lower() for _, o in rows}
        if str(target_owner).lower() not in owners or (not names and str(source_owner).lower() not in owners):
            raise ExecuteError("ERROR 002102: the source and target owners must each own at least one stored diagram.")
        if names:
            chosen = [n for n, _ in rows if n.lower() in {x.lower() for x in names}]
        else:
            chosen = [n for n, o in rows if (o or "").lower() == str(source_owner).lower()]
        for name in chosen:
            network.q("update grapy_nd_diagram set owner = ? where network = ? and name = ?", str(target_owner),
                      network.name, name)
    return Result(_path(in_diagrams))


def PurgeTemporaryDiagrams(in_utility_network, created_before=None):
    """The temporary diagrams — all of them, or those created before a date — deleted from the network."""
    path = _network_path(in_utility_network)
    cutoff = None
    if _given(created_before):
        cutoff = created_before if isinstance(created_before, datetime.datetime) else (
            datetime.datetime.combine(created_before, datetime.time()) if isinstance(created_before, datetime.date)
            else datetime.datetime.fromisoformat(str(created_before)))
    with _utility.Network(path) as network:
        _diagram.ensure(network.connection)
        for name, created in network.q("select name, created from grapy_nd_diagram where network = ? and stored = 0 "
                                       "and system = 0", network.name).fetchall():
            if cutoff is None or datetime.datetime.fromisoformat(created) < cutoff:
                _diagram.delete(network, name)
    return Result(path)


def ExportDiagramContent(in_utility_network, network_diagram_name, out_file,
                         include_diagram_properties="EXCLUDE_DIAGRAM_PROPERTIES", include_geometries="EXCLUDE_GEOMETRIES",
                         include_attributes="EXCLUDE_ATTRIBUTES", include_aggregations="EXCLUDE_AGGREGATIONS",
                         use_domains="DONT_USE_CODED_VALUE_NAMES"):
    """A diagram written as JSON in the layout of the ArcGIS REST API's Query Diagram Content response, which the page
    points to: ``diagram``, ``spatialRef``, ``sourceMapping``, ``containers``, ``junctions`` and ``edges`` (``id``,
    ``containerID``, ``assocSourceID``, ``assocGlobalID``, ``userType``, an edge's ``fromID`` and ``toID``), and on
    request ``diagramInfo``, each feature's ``geometry``, ``attributes`` (its network feature's fields by alias) and
    ``aggregations`` (``sourceID``, ``globalID``, ``assetGroup``, ``assetType``), coded values as their names with
    USE_CODED_VALUE_NAMES. ``userType`` is the class's name (INFERRED, §5.9 (h))."""
    props = _flag(include_diagram_properties, "INCLUDE_DIAGRAM_PROPERTIES", "EXCLUDE_DIAGRAM_PROPERTIES",
                  "EXCLUDE_DIAGRAM_PROPERTIES")
    geoms = _flag(include_geometries, "INCLUDE_GEOMETRIES", "EXCLUDE_GEOMETRIES", "EXCLUDE_GEOMETRIES")
    attrs = _flag(include_attributes, "INCLUDE_ATTRIBUTES", "EXCLUDE_ATTRIBUTES", "EXCLUDE_ATTRIBUTES")
    aggs = _flag(include_aggregations, "INCLUDE_AGGREGATIONS", "EXCLUDE_AGGREGATIONS", "EXCLUDE_AGGREGATIONS")
    names = _flag(use_domains, "USE_CODED_VALUE_NAMES", "DONT_USE_CODED_VALUE_NAMES", "DONT_USE_CODED_VALUE_NAMES")
    if names and not (attrs or aggs):
        raise ExecuteError("ERROR 000800: use_domains applies only with include_attributes or include_aggregations.")
    layer = _layers.find(in_utility_network)
    path = layer.network if layer is not None and getattr(layer, "diagram", None) else _network_path(in_utility_network)
    from . import _ndjson
    with _utility.Network(path) as network:
        d = _diagram.load(network, network_diagram_name)
        document = _ndjson.document(network, d, props, geoms, attrs, aggs, names)
    with open(_path(out_file), "w", encoding="utf-8") as out:
        json.dump(document, out, indent=1)
    return Result(_path(out_file))


# ---- refused by name -----------------------------------------------------------------------------------------------

_LAYER_DEFINITIONS = ("is a diagram layer definition, the sublayers and symbology of a diagram map in ArcGIS Pro's "
                      "application: its .ndld layout is not published, and a run on this server has no map to "
                      "symbolize (ADR-232 §5.7)")
_REASONS = {
    _LAYER_DEFINITIONS: ("CreateDiagramLayerDefinition", "ExportDiagramLayerDefinition"),
    "writes a ModelBuilder model into a toolbox, and grapy runs Python toolboxes and scripts, not models: it has no "
    "ModelBuilder to write one for (ADR-232 §5.7)": ("CreateGeoprocessingModelFromDiagramTemplate",),
    "rotates the trees hanging from a diagram's pivot junctions, and pivot junctions are set only in ArcGIS Pro's "
    "interactive diagram map: no tool or rule sets one, so a run on this server has none to turn (ADR-232 §5.7)":
        ("ApplyRotateTreeLayout",),
    "lays edges sharing a line attribute on parallel straight lines, and its page describes that result but not how the "
    "lines are ordered and offset, where a branch leaves its line or how compression groups junctions, and no "
    "published method draws such a schematic: an equivalent would be grapy's invention under Esri's name (ADR-232 "
    "§5.7)": ("ApplyRelativeMainlineLayout", "AddRelativeMainlineLayout"),
}
REFUSED = {name: reason for reason, names in _REASONS.items() for name in names}


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    reason = REFUSED.get(name)
    message = f"arcpy.nd.{name} {reason}." if reason else f"arcpy.nd.{name} is not built in grapy yet."

    def missing(*_, **__):
        raise NotBuiltError(message)

    return missing


# ---- subnetwork system diagrams ------------------------------------------------------------------------------------

def _system_diagrams(path, tier, names):
    """UpdateSubnetwork's subnetwork system diagrams: one per subnetwork and diagram template of the tier's definition,
    named after the subnetwork (``<subnetwork>::<template>`` when there are several templates, INFERRED, §5.9 (g)),
    stored, made again from the subnetwork's features whenever it is updated — positions kept — and deleted with it."""
    from . import _unsubnet
    with _utility.Network(path) as network:
        templates = _unsubnet.definition(network, tier).get("diagram_template") or []
    if not templates:
        return
    _diagram.ensure_classes(path)
    with _utility.Network(path) as network:
        builder = _diagram.Builder(network)
        for name in names:
            row = network.q("select features, isdeleted from grapy_un_subnetwork where network = ? and lower(tier) = "
                            "lower(?) and name = ?", network.name, tier, name).fetchone()
            for t in templates:
                found = _diagram.template(network, t)
                diagram_name = name if len(templates) == 1 else f"{name}::{found['name']}"
                old = _diagram.load(network, diagram_name, must=False)
                keys = {tuple(k) for k in json.loads(row[0])} if row and row[0] and not row[1] else set()
                keys &= set(builder.graph.features)
                if not keys:
                    if old is not None:
                        _diagram.delete(network, old.name)
                    continue
                d = _diagram.Diagram(diagram_name, found["name"])
                d.stored = d.system = True
                if old is not None:
                    d.owner, d.created = old.owner, old.created
                _save_inputs(builder, d, keys)
                keep = {f.identity(): f.shape for f in old.features.values() if f.shape is not None} if old else None
                builder.run(d, found, keys, None, keep=keep)
                _diagram.save(network, d)
