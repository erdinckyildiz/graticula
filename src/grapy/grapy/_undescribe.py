"""Describe of a utility network — the documented properties of the Utility Network, Domain Network, Tier, Network
Attribute and Terminal Configuration describe objects, read off grapy's definition tables (``grapy._utility``).

Measured in ArcGIS Pro 3.7, 2026-11-12 (the Pro sweep of arcpy.un): Pro lists the structure network first, with the
tier definition 'None' and no controller type; a source's asset groups and types include the default subtype and code 0
'Unknown'; the system network attributes are Source ID, Terminal ID, Asset group, Asset type, Is subnetwork
controller, Tier name, Shape length, Position from, Position to and Flow direction, in that order and with the bit
sizes below, before the user's; and the terminal configurations begin with 'Single terminal' (id 0). Ids are not
compared and grapy numbers its own: what a source, a domain network or a tier is called and holds is what a script
reads.
"""
from __future__ import annotations

import os

from . import _gpkg, _subtypes, _utility

_USAGE = {"Device": "Device", "Line": "Line", "Junction": "Junction", "Assembly": "Assembly",
          "JunctionObject": "JunctionObject", "EdgeObject": "EdgeObject", "StructureJunction": "StructureJunction",
          "StructureLine": "StructureLine", "StructureBoundary": "StructureBoundary"}
_EDGES = ("Line", "StructureLine", "EdgeObject")
# (name, field type, bit size, in line, usage type, roles it is assigned to, field) — measured in Pro 3.7.
_SYSTEM = (("Source ID", "SmallInteger", 7, True, "SourceID", (), None),
           ("Terminal ID", "SmallInteger", 0, False, "TerminalID", (), None),
           ("Asset group", "SmallInteger", 10, True, "AssetGroup", "all", "ASSETGROUP"),
           ("Asset type", "SmallInteger", 10, True, "AssetType", "all", "ASSETTYPE"),
           ("Is subnetwork controller", "SmallInteger", 1, True, "IsSubnetworkController", ("Device", "JunctionObject"),
            "ISSUBNETWORKCONTROLLER"),
           ("Tier name", "SmallInteger", 6, True, "TierName", ("Device", "JunctionObject"), "TIERNAME"),
           ("Shape length", "Double", 0, False, "ShapeLength", ("Line", "StructureLine"), "SHAPE_Length"),
           ("Position from", "Double", 0, False, "PositionFrom", (), None),
           ("Position to", "Double", 0, False, "PositionTo", (), None),
           ("Flow direction", "SmallInteger", 2, True, "FlowDirection", _EDGES, "FLOWDIRECTION"))
_DATA_TYPES = {"SHORT": ("Integer", "SmallInteger"), "LONG": ("Integer", "Integer"), "DOUBLE": ("Double", "Double"),
               "DATE": ("Date", "Date"), "BIGINTEGER": ("BigInteger", "BigInteger")}


class _Object:
    def __init__(self, **properties):
        self.__dict__.update(properties)

    def __repr__(self):
        return f"<geoprocessing describe data object {self.__dict__.get('name', '')}>"


def describe(path):
    """The description of the utility network at ``path``, or None when it is not one."""
    try:
        file, dataset, name = _utility.locate(path)
    except Exception:  # noqa: BLE001 — not a utility network path
        return None
    if not os.path.isfile(file):
        return None
    connection = _gpkg.connect(file)
    try:
        if not connection.execute("select 1 from sqlite_master where name = 'grapy_un'").fetchone() or \
                not connection.execute("select 1 from grapy_un where name = ? and dataset = ?",
                                       (name, dataset)).fetchone():
            return None
    finally:
        connection.close()
    with _utility.Network(path) as network:
        return _network(network)


def _network(network):
    sources = network.sources()
    domains = [r for r in network.q("select name, alias, kind, tier_definition, controller_type from grapy_un_domain "
                                    "where network = ? order by rowid", network.name)]
    return _Object(
        dataType="UtilityNetwork", name=network.name, baseName=network.name, catalogPath=network.path,
        path=f"{network.file}/{network.dataset}", serviceTerritoryFeatureClassName=network.territory.rsplit("/", 1)[-1],
        schemaGeneration=int(network.version), minimalDirtyAreaSize=1.0,
        domainNetworks=[_domain(network, sources, i + 1, row) for i, row in enumerate(domains)],
        networkAttributes=_attributes(network, sources), categories=[_Object(name=n) for (n,) in network.q(
            "select name from grapy_un_category where network = ? order by system desc, rowid", network.name)],
        terminalConfigurations=_terminal_configurations(network))


def _domain(network, sources, number, row):
    name, alias, kind, definition, controller = row
    structure = kind == "Structure"
    mine = [(s, c, r) for s, (c, d, r) in sources.items() if d == name]
    junctions = [_source(network, s, c, r) for s, c, r in mine if r not in _EDGES + ("SubnetLine",)]
    edges = [_source(network, s, c, r) for s, c, r in mine if r in _EDGES + ("SubnetLine",)]
    subnet = next((c for c, d, r in sources.values() if d == name and r == "SubnetLine"), None)
    tiers = network.q("select name, rank, topology_type, tier_group, subnetwork_field from grapy_un_tier where "
                      "network = ? and domain = ? order by rowid", network.name, name).fetchall()
    return _Object(
        domainNetworkName=name, domainNetworkAliasName="Structure" if structure else (alias or name),
        domainNetworkID=number, isStructureNetwork=structure, releaseNumber=1,
        subnetworkControllerType="" if structure else str(controller or "").title(),
        subnetworkLabelFieldName="" if structure else "SUBNETWORKNAME",
        subnetworkTableName=subnet.rsplit("/", 1)[-1] if subnet else "",
        tierDefinition="None" if structure else str(definition or "").title(),
        tierGroups=[_Object(name=n) for (n,) in network.q("select name from grapy_un_tier_group where network = ? and "
                                                          "domain = ? order by rowid", network.name, name)],
        tiers=[_tier(network, i + 1, definition, t) for i, t in enumerate(tiers)], junctionSources=junctions,
        edgeSources=edges)


def _source(network, source_id, class_name, role):
    usage = _USAGE.get(role, role) if not class_name.rsplit("/", 1)[-1].startswith("Structure") or \
        role not in ("JunctionObject", "EdgeObject") else "Structure" + role
    groups = dict(_subtypes.codes(network.connection, class_name))
    groups.setdefault(0, "Unknown")
    if role == "SubnetLine":  # an edge source of its own in Pro, with the Unknown asset group and no asset types
        return _Object(sourceName=class_name.rsplit("/", 1)[-1], sourceID=source_id, sourceType="Edge",
                       utilityNetworkFeatureClassUsageType="SubnetLine", assetTypeFieldName="ASSETTYPE", shapeType="",
                       assetGroups=[_Object(assetGroupCode=0, assetGroupName="Unknown", assetTypes=[])])
    return _Object(
        sourceName=class_name.rsplit("/", 1)[-1], sourceID=source_id, sourceType="Edge" if role in _EDGES else "Junction",
        utilityNetworkFeatureClassUsageType=usage, assetTypeFieldName="ASSETTYPE", shapeType="",
        assetGroups=[_Object(assetGroupCode=code, assetGroupName=text,
                             assetTypes=_asset_types(network, class_name, role, code))
                     for code, text in sorted(groups.items())])


def _asset_types(network, class_name, role, group):
    kinds = dict(network.asset_types(class_name, group))
    kinds.setdefault(0, "Unknown")
    out = []
    for code, text in sorted(kinds.items()):
        config = network.terminal_config(class_name, group, code)
        setting = network.setting(class_name, group, code, "association_role") or {}
        policy = network.setting(class_name, group, code, "edge_connectivity")
        categories = [c for (c,) in network.q("select category from grapy_un_category_asset where network = ? and "
                                              "class_name = ? and asset_group = ? and asset_type = ? order by rowid",
                                              network.name, class_name, int(group), int(code))]
        out.append(_Object(
            assetTypeCode=code, assetTypeName=text, categories=categories,
            associationRoleType=str(setting.get("role") or "NONE").title(),
            associationDeleteType={"SET_TO_NONE": "None", None: "Restricted"}.get(setting.get("deletion"),
                                                                                 str(setting.get("deletion")).title()),
            terminalConfigurationID=config[0] if config else 0, isTerminalConfigurationSupported=role not in _EDGES,
            connectivityPolicy="AnyVertex" if policy == "ANY_VERTEX" else "EndVertex",
            splitContent=bool(setting.get("split_content", False))))
    return out


def _valid(items):
    groups = {}
    for _, group, kind in items or ():
        groups.setdefault(int(group), []).append(int(kind))
    return [_Object(assetGroupCode=g, assetGroupName="", assetTypes=[_Object(assetTypeCode=k, assetTypeName="")
                                                                       for k in kinds])
            for g, kinds in groups.items()]


# A condition barrier as Describe answers it: measured in ArcGIS Pro 3.7, 2026-11-12, for "Category IS_EQUAL_TO
# SPECIFIC_VALUE Isolating" (type 'Category', operator 'Equal', isSpecificValue True, combineUsingOr False); the other
# operators' words follow the same pattern (INFERRED).
_OPERATORS = {"IS_EQUAL_TO": "Equal", "DOES_NOT_EQUAL": "NotEqual", "IS_GREATER_THAN": "GreaterThan",
              "IS_GREATER_THAN_OR_EQUAL_TO": "GreaterThanEqual", "IS_LESS_THAN": "LessThan",
              "IS_LESS_THAN_OR_EQUAL_TO": "LessThanEqual", "INCLUDES_THE_VALUES": "IncludesTheValues",
              "DOES_NOT_INCLUDE_THE_VALUES": "DoesNotIncludeTheValues", "INCLUDES_ANY": "IncludesAny",
              "DOES_NOT_INCLUDE_ANY": "DoesNotIncludeAny"}


def _barrier(row):
    row = list(row) + [None] * (5 - len(row))
    name, operator, kind, value, combine = row[:5]
    return _Object(name=name, type="Category" if str(name).lower() == "category" else "NetworkAttribute",
                   operator=_OPERATORS.get(str(operator).upper(), operator), value=value,
                   isSpecificValue=str(kind or "SPECIFIC_VALUE").upper() == "SPECIFIC_VALUE",
                   combineUsingOr=str(combine or "").upper() == "OR")


def _tier(network, number, definition, row):
    from . import _unsubnet
    name, rank, topology, group, field = row
    found = _unsubnet.definition(network, name)
    trace = _Object(
        includeBarriers=bool(found["include_barriers"]), includeContainers=bool(found["include_containers"]),
        includeContent=bool(found["include_content"]), includeStructures=bool(found["include_structures"]),
        includeIsolated=False, ignoreBarriersAtStartingPoints=False, validateConsistency=True,
        validateLocatability=bool(found["validate_locatability"]),
        traversabilityScope={"BOTH_JUNCTIONS_AND_EDGES": "JunctionsAndEdges", "JUNCTIONS_ONLY": "JunctionsOnly",
                             "EDGES_ONLY": "EdgesOnly"}.get(found["traversability_scope"], found["traversability_scope"]),
        conditionBarriers=[_barrier(r) for r in found["condition_barriers"]],
        functionBarriers=list(found["function_barriers"]), propagators=list(found["propagators"]),
        domainNetworkName=network.tier(name)[0], tierName=name, targetTierName="", subnetworkName="",
        allowIndeterminateFlow=True, useDigitizedDirection=False, synthesizeGeometries=False, inferConnectivity=False)
    return _Object(
        name=name, rank=int(rank), tierID=number, tierGroupName=group or "",
        subnetworkFieldName=field if definition == "HIERARCHICAL" else "", tierTopology=str(topology).title(),
        supportDisjointSubnetwork=bool(found["support_disjoint_subnetwork"]),
        validDevices=_valid(found["valid_devices"]), validLines=_valid(found["valid_lines"]),
        validSubnetworkControllers=_valid(found["valid_subnetwork_controller"]),
        validJunctions=_valid(found["valid_junctions"]), validJunctionObjects=_valid(found["valid_junction_objects"]),
        validJunctionObjectSubnetworkControllers=_valid(found["valid_junction_object_subnetwork_controller"]),
        validEdgeObjects=_valid(found["valid_edge_objects"]),
        aggregatedLinesForSubnetLine=_valid(found["aggregated_line"]),
        manageSubnetwork=_Object(isDirty=bool(found["manage_subnetwork_isdirty"])),
        updateSubnetworkTraceConfiguration=trace, diagramTemplates=list(found.get("diagram_template", [])))


def _assignment(number, class_name, field):
    return _Object(networkAttributeID=number, networkSourceName=class_name.rsplit("/", 1)[-1],
                   evaluator=_Object(evaluatorType="FieldEvaluator", fieldName=field))


def _attributes(network, sources):
    real = [(c, d, r) for c, d, r in sources.values() if r != "SubnetLine"]
    out = []
    for name, field_type, bits, inline, usage, roles, field in _SYSTEM:
        number = len(out) + 1
        # the structure network's junction objects are no subnetwork controllers and carry no tier
        assigned = [c for c, d, r in real if roles == "all" or (r in roles and not (
            d == _utility.STRUCTURE and usage in ("IsSubnetworkController", "TierName")))]
        out.append(_Object(
            name=name, id=number, dataType="Double" if field_type == "Double" else "Integer", fieldType=field_type,
            domainName="", bitSize=bits, bitPosition=0, isEmbedded=inline, isApportionable=name == "Shape length",
            isNullable=False, isOverridable=False, isSubstitution=False, networkAttributeToSubstitute="",
            usageType=usage, assignments=[_assignment(number, c, field) for c in assigned]))
    fields = {}
    for attribute, class_name, field in network.q("select attribute, class_name, field from grapy_un_attribute_field "
                                                  "where network = ? order by rowid", network.name):
        fields.setdefault(attribute, []).append((class_name, field))
    for row in network.q("select name, type, inline, apportionable, domain_name, nullable, substitution, substitute "
                         "from grapy_un_attribute where network = ? and system = 0 order by rowid", network.name):
        name, kind, inline, apportionable, domain, nullable, substitution, substitute = row
        number = len(out) + 1
        data_type, field_type = _DATA_TYPES.get(kind, ("Integer", "Integer"))
        out.append(_Object(
            name=name, id=number, dataType=data_type, fieldType=field_type, domainName=domain or "",
            bitSize=_utility_bits(network, domain) if inline else 0, bitPosition=0, isEmbedded=bool(inline),
            isApportionable=bool(apportionable), isNullable=bool(nullable), isOverridable=False,
            isSubstitution=bool(substitution), networkAttributeToSubstitute=substitute or "", usageType="Unknown",
            assignments=[_assignment(number, c, f) for c, f in fields.get(name, [])]))
    return out


def _utility_bits(network, domain):
    from .un import _bits
    return _bits(network, domain)


def _terminal_configurations(network):
    import json
    out = [_Object(terminalConfigurationID=0, terminalConfigurationName="Single terminal",
                   traversabilityModel="Bidirectional", defaultConfiguration="All", validConfigurationPaths=[],
                   terminals=[_Object(terminalID=1, terminalName="Single Terminal", isUpstreamTerminal=False)])]
    for number, name, model, terminals, paths, default in network.q(
            "select id, name, model, terminals, paths, default_path from grapy_un_terminal_config where network = ? "
            "order by id", network.name):
        named = {int(t[0]): t[1] for t in json.loads(terminals)}
        valid = []
        for path_name, value in json.loads(paths).items():
            pairs = value if isinstance(value, list) else []
            valid.append(_Object(name=path_name, description=",".join(f"{named.get(int(a))}-{named.get(int(b))}"
                                                                      for a, b in pairs) if pairs else value,
                                 terminalPaths=[[named.get(int(a)), named.get(int(b))] for a, b in pairs]))
        out.append(_Object(
            terminalConfigurationID=int(number), terminalConfigurationName=name,
            traversabilityModel=str(model).title(), defaultConfiguration=default.title() if default in ("ALL", "NONE")
            else default, validConfigurationPaths=valid,
            terminals=[_Object(terminalID=int(t[0]), terminalName=t[1], isUpstreamTerminal=bool(t[2]))
                       for t in json.loads(terminals)]))
    return out
