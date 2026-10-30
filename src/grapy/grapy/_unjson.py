"""A utility network trace's JSON results — Trace's ELEMENTS, CONNECTIVITY, FEATURES,
CONTAINMENT_AND_ATTACHMENT_ASSOCIATIONS and RELATED_RECORDS result types, and ExportSubnetwork's file (ADR-231).

The Trace page names the result types and the options each takes but not the file's layout. The ArcGIS REST API's
public "Trace (Utility Network Server)" page documents the response: ``traceResults`` holding ``elements``,
``connectivity``, ``featureElements``, ``associations``, ``relatedRecords``, ``globalFunctionResults``,
``kFeaturesForKNNFound``, ``startingPointsIgnored`` and ``warnings``, beside ``sourceMapping``, ``resultTypes`` and
``success``, with each entry's fields. grapy writes that layout (INFERRED, ADR-231 §5.9 (l): that the file a tool writes
is the service's response). Geometry is Esri JSON, the REST API's geometry objects. A system junction, which is no
feature, is written with network source 0 and no global ID.
"""
from __future__ import annotations

import json

from . import _gpkg, _untrace
from ._runtime import ExecuteError
from ._utility import terminal_of

RESULT_TYPES = ("ELEMENTS", "CONNECTIVITY", "FEATURES", "CONTAINMENT_AND_ATTACHMENT_ASSOCIATIONS", "RELATED_RECORDS")


def esri(shape):
    """A shapely geometry as an Esri JSON geometry object."""
    if shape is None:
        return None
    kind = shape.geom_type
    if kind == "Point":
        return {"x": shape.x, "y": shape.y}
    if kind == "MultiPoint":
        return {"points": [[p.x, p.y] for p in shape.geoms]}
    if kind in ("LineString", "MultiLineString"):
        parts = [shape] if kind == "LineString" else list(shape.geoms)
        return {"paths": [[list(c[:2]) for c in p.coords] for p in parts]}
    if kind in ("Polygon", "MultiPolygon"):
        parts = [shape] if kind == "Polygon" else list(shape.geoms)
        rings = []
        for p in parts:
            rings.append([list(c[:2]) for c in p.exterior.coords])
            rings += [[list(c[:2]) for c in r.coords] for r in p.interiors]
        return {"rings": rings}
    return None


class Writer:
    """The JSON of one trace or exported subnetwork."""

    def __init__(self, network, graph, outcome, ids, keys, geometry=False, descriptions=False, attributes=(),
                 fields=(), related_fields=(), flow_directions=False, propagated=False):
        self.network, self.graph, self.outcome, self.ids, self.keys = network, graph, outcome, ids, keys
        self.geometry, self.descriptions, self.flow_directions, self.propagated = (geometry, descriptions,
                                                                                   flow_directions, propagated)
        self.attributes, self.fields, self.related_fields = list(attributes), list(fields), list(related_fields)
        self.sources = graph.sources
        from ._utility import Values
        self.values = Values(graph)

    # -- one element --

    def ids_of(self, i, prefix=""):
        e = self.graph.elements[i]
        out = {f"{prefix}NetworkSourceId" if prefix else "networkSourceId": e.source,
               f"{prefix}GlobalId" if prefix else "globalId": e.globalid,
               f"{prefix}ObjectId" if prefix else "objectId": e.oid if e.feature else None}
        return out

    def element(self, i, names=False):
        e = self.graph.elements[i]
        data = self.graph.features[e.feature][1]
        out = {"networkSourceId": e.source, "globalId": e.globalid, "objectId": e.oid, "terminalId": terminal_of(e),
               "assetGroupCode": data["group"], "assetTypeCode": data["type"]}
        if e.kind == "E":
            out["positionFrom"], out["positionTo"] = e.start, e.end
            if self.flow_directions:
                out["flowDirection"] = _untrace.flow_direction(self.graph, self.outcome.flow, i)
        if names:
            class_name = self.sources[e.source][0]
            group, kind = self.network.asset_names(class_name, data["group"], data["type"])
            out.update({"networkSourceName": class_name.rsplit("/", 1)[-1], "assetGroupName": group,
                        "assetTypeName": kind,
                        "terminalName": self.network.terminal_name(class_name, data["group"], data["type"],
                                                                   e.terminal) if e.kind == "J" else None})
        return out

    def end(self, i, prefix):
        e = self.graph.elements[i]
        out = {f"{prefix}NetworkSourceId": e.source, f"{prefix}GlobalId": e.globalid,
               f"{prefix}ObjectId": e.oid if e.feature else None}
        if prefix != "via":
            out[f"{prefix}TerminalId"] = terminal_of(e)
        else:
            out["viaPositionFrom"], out["viaPositionTo"] = e.start, e.end
        if self.geometry:
            out[f"{prefix}Geometry"] = esri(e.shape)
        if self.descriptions:
            out[f"{prefix}NetworkSourceName"] = self.sources[e.source][0].rsplit("/", 1)[-1] if e.source else None
            if prefix != "via" and e.feature:
                data = self.graph.features[e.feature][1]
                out[f"{prefix}TerminalName"] = self.network.terminal_name(self.sources[e.source][0], data["group"],
                                                                         data["type"], e.terminal)
        return out

    # -- the result types --

    def elements(self):
        return [self.element(i) for i in sorted(self.ids) if self.graph.elements[i].feature is not None]

    def connectivity(self):
        """An entry per traced edge element from each junction at its start to each at its end (via the edge), and per
        junction-junction connection between two traced features."""
        out = []
        for i in sorted(self.ids):
            if i in self.graph.ends:
                starts, ends = self.graph.ends[i]
                for a in starts:
                    for b in ends:
                        if a in self.ids and b in self.ids:
                            entry = {**self.end(i, "via"), **self.end(a, "from"), **self.end(b, "to")}
                            if self.flow_directions:
                                entry["flowDirection"] = _untrace.flow_direction(self.graph, self.outcome.flow, i)
                            out.append(entry)
        for a, b in sorted(self.graph.links):
            ea, eb = self.graph.elements[a], self.graph.elements[b]
            if a in self.ids and b in self.ids and ea.kind == eb.kind == "J" and ea.feature != eb.feature:
                out.append({**self.end(a, "from"), **self.end(b, "to")})
        return out

    def feature_elements(self):
        out = []
        for i in sorted(self.ids):
            e = self.graph.elements[i]
            if e.feature is None:
                continue
            entry = self.element(i, names=self.descriptions)
            class_name = self.sources[e.source][0]
            if self.geometry:
                entry["geometry"] = esri(e.shape)
            if self.attributes:
                entry["networkAttributeValues"] = [{a: self.values.attribute(e, a)} for a in self.attributes]
            wanted = [f for c, f in self.fields if self.network.table(c) == class_name]
            if wanted:
                entry["fieldValues"] = [{"fieldName": f, "value": self._field(class_name, e.oid, f)} for f in wanted]
            if self.propagated:
                entry["propagatedValues"] = [{"attributeName": a, "propagatedValue": v}
                                             for a, v in sorted(self.outcome.propagated.get(i, {}).items())]
            out.append(entry)
        return out

    def _field(self, class_name, oid, field):
        columns = {c.name.lower(): c.name for c in _gpkg.describe(self.network.connection, class_name)[0]}
        if field.lower() not in columns:
            raise ExecuteError(f"ERROR 000728: the field '{field}' does not exist in {class_name.rsplit('/', 1)[-1]}.")
        return self.network.q(f'select "{columns[field.lower()]}" from "{class_name}" where '
                              f'"{self.network.oid(class_name)}" = ?', oid).fetchone()[0]

    def associations(self):
        """Containment and structural attachment associations with an end among the result's features."""
        by_globalid = {g: k for k, (g, _) in self.graph.features.items()}
        out = []
        for row in self.graph.associations:
            kind = int(row["type"])
            if kind not in (2, 3):
                continue
            a = by_globalid.get(str(row["from_globalid"]).upper())
            b = by_globalid.get(str(row["to_globalid"]).upper())
            if a not in self.keys and b not in self.keys:
                continue
            entry = {"associationType": "containment" if kind == 2 else "attachment",
                     "fromNetworkSourceId": row["from_source"], "fromGlobalId": row["from_globalid"],
                     "fromTerminalId": row["from_terminal"], "toNetworkSourceId": row["to_source"],
                     "toGlobalId": row["to_globalid"], "toTerminalId": row["to_terminal"]}
            if self.descriptions:
                entry["fromNetworkSourceName"] = self.sources[row["from_source"]][0].rsplit("/", 1)[-1]
                entry["toNetworkSourceName"] = self.sources[row["to_source"]][0].rsplit("/", 1)[-1]
            out.append(entry)
        return out

    def related_records(self):
        """For each result feature, the records its relationship classes (ADR-225) relate to it, with the fields
        ``related_record_fields`` names for each class (none named: every class, no fields; INFERRED §5.9 (l))."""
        from . import _relationships
        connection = self.network.connection
        classes = _relationships.classes(connection)
        named = {}
        for rc, field in self.related_fields:
            named.setdefault(str(rc).rsplit("/", 1)[-1].lower(), []).append(str(field))
        out = []
        for key in sorted(self.keys):
            class_name = self.sources[key[0]][0]
            entry_classes = []
            for rc in classes:
                short = rc["name"].rsplit("/", 1)[-1].lower()
                if named and short not in named:
                    continue
                if rc["origin"] == class_name:
                    related = [d for o, d in _relationships.pairs(connection, rc) if o == key[1]]
                    other = rc["destination"]
                elif rc["destination"] == class_name:
                    related = [o for o, d in _relationships.pairs(connection, rc) if d == key[1]]
                    other = rc["origin"]
                else:
                    continue
                rows = []
                for oid in related:
                    fields = named.get(short, [])
                    rows.append({"outFields": {f: self._field(other, oid, f) for f in fields}})
                item = {"name": rc["name"], "originLayerId": None, "destinationLayerId": None, "relationships": rows}
                if self.descriptions:
                    item["originName"] = rc["origin"].rsplit("/", 1)[-1]
                    item["destinationName"] = rc["destination"].rsplit("/", 1)[-1]
                entry_classes.append(item)
            if entry_classes:
                out.append({"networkSourceId": key[0], "networkSourceName": class_name.rsplit("/", 1)[-1],
                            "globalId": self.graph.features[key][0], "relationshipClasses": entry_classes})
        return out

    def document(self, types, extra=None):
        results = {}
        if "ELEMENTS" in types:
            results["elements"] = self.elements()
        if "CONNECTIVITY" in types:
            results["connectivity"] = self.connectivity()
        if "FEATURES" in types:
            results["featureElements"] = self.feature_elements()
        if "CONTAINMENT_AND_ATTACHMENT_ASSOCIATIONS" in types:
            results["associations"] = self.associations()
        if "RELATED_RECORDS" in types:
            results["relatedRecords"] = self.related_records()
        results["globalFunctionResults"] = self.outcome.functions
        results["kFeaturesForKNNFound"] = bool(self.outcome.knn_found)
        results["startingPointsIgnored"] = False
        results["warnings"] = []
        doc = dict(extra or {})
        doc.update({"traceResults": results,
                    "sourceMapping": {str(s): c.rsplit("/", 1)[-1] for s, (c, _, _) in self.sources.items()},
                    "resultTypes": [t.lower() for t in types], "success": True})
        return doc


def write(path, document):
    path = str(path)
    if not path.lower().endswith(".json"):
        raise ExecuteError(f"ERROR 000800: '{path}' is not a .json file.")
    with open(path, "w", encoding="utf-8") as written:
        json.dump(document, written, indent=2, default=str)
